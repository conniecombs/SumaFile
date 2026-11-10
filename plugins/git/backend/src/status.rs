//! Repository status: one `git status --porcelain=v1 -z --branch` run gives
//! branch, upstream, ahead/behind and the changes; only the abbreviated HEAD
//! needs a second process. Results are cached per repository root.

use std::path::{Component, Path, PathBuf, MAIN_SEPARATOR_STR};
use std::sync::{Mutex, MutexGuard};
use std::time::{Duration, Instant};

use crate::git::Git;
use crate::models::{GitFileStatus, GitRepositoryStatus};
use crate::paths::{comparable_path, native_path};
use crate::GIT_NOT_FOUND;

/// The host asks for decorations and the panel asks for the status on every
/// folder change; both answer from one status run taken within this window.
pub const STATUS_CACHE_TTL: Duration = Duration::from_millis(1500);

/// Roots the cache keeps at most; the oldest entry goes first.
pub const STATUS_CACHE_CAPACITY: usize = 16;

struct CacheEntry {
    key: String,
    taken: Instant,
    status: GitRepositoryStatus,
}

/// Status runs keyed by repository root, case-insensitively on Windows and
/// whatever the separators.
pub struct StatusCache {
    ttl: Duration,
    capacity: usize,
    entries: Mutex<Vec<CacheEntry>>,
}

impl StatusCache {
    pub fn new(ttl: Duration, capacity: usize) -> Self {
        StatusCache {
            ttl,
            capacity: capacity.max(1),
            entries: Mutex::new(Vec::new()),
        }
    }

    fn lock(&self) -> MutexGuard<'_, Vec<CacheEntry>> {
        self.entries
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// The status of `root` taken less than the TTL ago.
    pub fn get(&self, root: &Path) -> Option<GitRepositoryStatus> {
        let key = comparable_path(root);
        self.lock()
            .iter()
            .find(|entry| entry.key == key && entry.taken.elapsed() < self.ttl)
            .map(|entry| entry.status.clone())
    }

    pub fn put(&self, root: &Path, status: GitRepositoryStatus) {
        let key = comparable_path(root);
        let mut entries = self.lock();
        entries.retain(|entry| entry.key != key);
        while entries.len() >= self.capacity {
            // Entries are kept oldest first.
            entries.remove(0);
        }
        entries.push(CacheEntry {
            key,
            taken: Instant::now(),
            status,
        });
    }

    /// Any command that changes the index, the work tree or the refs makes
    /// the cached status of its root stale.
    pub fn forget(&self, root: &Path) {
        let key = comparable_path(root);
        self.lock().retain(|entry| entry.key != key);
    }
}

/// The work tree root that holds `path`, or `None` outside a work tree.
///
/// One `rev-parse --show-toplevel --show-cdup` answers both "is this a
/// repository" and "where is its root". The root is `path` with the
/// `--show-cdup` levels removed, so it keeps the caller's spelling (case,
/// 8.3 short names, substituted drives) and row paths built from it match the
/// listing; it falls back to git's `--show-toplevel`, in native separators.
/// Fails only with [`GIT_NOT_FOUND`].
pub fn find_root(git: &Git, path: &Path) -> Result<Option<PathBuf>, String> {
    let output = match git.query(path, &["rev-parse", "--show-toplevel", "--show-cdup"]) {
        Ok(output) => output,
        Err(error) if error == GIT_NOT_FOUND => return Err(error),
        Err(_) => return Ok(None),
    };
    let mut lines = output.lines();
    let toplevel = lines.next().unwrap_or_default().trim();
    if toplevel.is_empty() {
        return Ok(None);
    }
    let cdup = lines.next().unwrap_or_default().trim();
    Ok(Some(
        root_in_callers_spelling(path, cdup).unwrap_or_else(|| native_path(toplevel)),
    ))
}

/// `path` with one level removed per `..` of `cdup`, or `None` when either
/// is not the plain shape that makes that exact.
fn root_in_callers_spelling(path: &Path, cdup: &str) -> Option<PathBuf> {
    if !path.is_absolute()
        || path
            .components()
            .any(|component| matches!(component, Component::ParentDir | Component::CurDir))
    {
        return None;
    }
    let mut levels = 0;
    for segment in cdup.split('/').filter(|segment| !segment.is_empty()) {
        if segment != ".." {
            return None;
        }
        levels += 1;
    }
    let mut root = path;
    for _ in 0..levels {
        root = root.parent()?;
    }
    Some(native_path(&root.to_string_lossy()))
}

/// The status of the repository that holds `path`; outside one,
/// `is_repo: false`. A status of the same root taken within the TTL is
/// returned from `cache`.
pub fn repo_status(
    git: &Git,
    cache: &StatusCache,
    path: &Path,
) -> Result<GitRepositoryStatus, String> {
    std::fs::metadata(path).map_err(|error| {
        format!(
            "Path does not exist or is not accessible: {} ({error})",
            path.display()
        )
    })?;
    let Some(root) = find_root(git, path)? else {
        return Ok(GitRepositoryStatus::not_a_repo());
    };
    if let Some(status) = cache.get(&root) {
        return Ok(status);
    }

    let head = git
        .query(&root, &["rev-parse", "--short", "HEAD"])
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty());
    let (header, changes) = git
        .query(
            &root,
            &[
                "status",
                "--porcelain=v1",
                "-z",
                "--branch",
                "--untracked-files=normal",
            ],
        )
        .map(|output| parse_porcelain_status_with_branch(&root, &output))
        .unwrap_or_default();

    let status = GitRepositoryStatus {
        is_repo: true,
        root: Some(root.to_string_lossy().to_string()),
        branch: header.branch,
        upstream: header.upstream,
        head,
        ahead: header.ahead,
        behind: header.behind,
        staged: changes.iter().filter(|change| change.staged).count() as u32,
        unstaged: changes.iter().filter(|change| change.unstaged).count() as u32,
        untracked: changes.iter().filter(|change| change.untracked).count() as u32,
        conflicted: changes.iter().filter(|change| change.conflicted).count() as u32,
        changes,
    };
    cache.put(&root, status.clone());
    Ok(status)
}

#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) struct BranchHeader {
    branch: Option<String>,
    upstream: Option<String>,
    ahead: u32,
    behind: u32,
}

/// Parse the `## <branch>...<upstream> [ahead N, behind M]` header that
/// `git status --branch` emits as its first record.
fn parse_branch_header(record: &str) -> BranchHeader {
    let Some(rest) = record.strip_prefix("## ") else {
        return BranchHeader::default();
    };
    if rest.starts_with("HEAD (no branch)") || rest.starts_with("No commits yet") {
        return BranchHeader::default();
    }
    let (names, counts) = match rest.split_once(" [") {
        Some((names, counts)) => (names, Some(counts.trim_end_matches(']'))),
        None => (rest, None),
    };
    let (branch, upstream) = match names.split_once("...") {
        Some((branch, upstream)) => (branch, Some(upstream)),
        None => (names, None),
    };
    let mut header = BranchHeader {
        branch: Some(branch.trim().to_string()).filter(|value| !value.is_empty()),
        upstream: upstream
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty()),
        ..BranchHeader::default()
    };
    for part in counts.unwrap_or_default().split(',') {
        let part = part.trim();
        if let Some(value) = part.strip_prefix("ahead ") {
            header.ahead = value.trim().parse().unwrap_or(0);
        } else if let Some(value) = part.strip_prefix("behind ") {
            header.behind = value.trim().parse().unwrap_or(0);
        }
    }
    header
}

pub(crate) fn parse_porcelain_status_with_branch(
    root: &Path,
    output: &str,
) -> (BranchHeader, Vec<GitFileStatus>) {
    match output.split_once(char::from(0)) {
        Some((first, rest)) if first.starts_with("## ") => (
            parse_branch_header(first),
            parse_porcelain_status(root, rest),
        ),
        _ => (
            BranchHeader::default(),
            parse_porcelain_status(root, output),
        ),
    }
}

pub(crate) fn parse_porcelain_status(root: &Path, output: &str) -> Vec<GitFileStatus> {
    let mut changes = Vec::new();
    let mut parts = output.split('\0').peekable();
    while let Some(record) = parts.next() {
        if record.is_empty() {
            continue;
        }

        let bytes = record.as_bytes();
        if bytes.len() < 4 {
            continue;
        }

        let index_status = bytes[0] as char;
        let worktree_status = bytes[1] as char;
        // An untracked directory is reported as `dir/`.
        let path = record[3..].trim_end_matches('/').to_string();
        if path.trim().is_empty() {
            continue;
        }

        let is_rename_or_copy =
            matches!(index_status, 'R' | 'C') || matches!(worktree_status, 'R' | 'C');
        let original_path = if is_rename_or_copy {
            parts
                .next()
                .filter(|value| !value.is_empty())
                .map(|value| value.to_string())
        } else {
            None
        };

        changes.push(git_file_status(
            root,
            path,
            original_path,
            index_status,
            worktree_status,
        ));
    }

    changes
}

fn git_file_status(
    root: &Path,
    path: String,
    original_path: Option<String>,
    index_status: char,
    worktree_status: char,
) -> GitFileStatus {
    let conflicted = is_conflicted(index_status, worktree_status);
    let untracked = index_status == '?' && worktree_status == '?';
    let staged = !untracked && !conflicted && index_status != ' ';
    let unstaged = !untracked && !conflicted && worktree_status != ' ';
    let status = status_label(index_status, worktree_status).to_string();
    // git reports repo-relative paths with `/`; join with native separators so
    // the UI and the cache see one spelling.
    let absolute_path = root
        .join(path.replace('/', MAIN_SEPARATOR_STR))
        .to_string_lossy()
        .to_string();

    GitFileStatus {
        path,
        absolute_path,
        original_path,
        status,
        index_status: index_status.to_string(),
        worktree_status: worktree_status.to_string(),
        staged,
        unstaged,
        untracked,
        conflicted,
    }
}

fn is_conflicted(index_status: char, worktree_status: char) -> bool {
    matches!(
        (index_status, worktree_status),
        ('D', 'D') | ('A', 'U') | ('U', 'D') | ('U', 'A') | ('D', 'U') | ('A', 'A') | ('U', 'U')
    )
}

pub(crate) fn status_label(index_status: char, worktree_status: char) -> &'static str {
    if is_conflicted(index_status, worktree_status) {
        "conflicted"
    } else if index_status == '?' && worktree_status == '?' {
        "untracked"
    } else if matches!(index_status, 'R' | 'C') || matches!(worktree_status, 'R' | 'C') {
        "renamed"
    } else if index_status == 'D' || worktree_status == 'D' {
        "deleted"
    } else if index_status == 'A' || worktree_status == 'A' {
        "added"
    } else if index_status != ' ' && worktree_status != ' ' {
        "staged + modified"
    } else if index_status != ' ' {
        "staged"
    } else {
        "modified"
    }
}

#[cfg(test)]
mod tests {
    use super::{
        parse_branch_header, parse_porcelain_status, parse_porcelain_status_with_branch,
        root_in_callers_spelling, status_label, BranchHeader, StatusCache,
    };
    use crate::models::GitRepositoryStatus;
    use crate::paths::native_path;
    use std::path::{Path, MAIN_SEPARATOR};
    use std::time::Duration;

    #[test]
    fn branch_header_carries_branch_upstream_and_counts() {
        assert_eq!(
            parse_branch_header("## main...origin/main [ahead 2, behind 1]"),
            BranchHeader {
                branch: Some("main".into()),
                upstream: Some("origin/main".into()),
                ahead: 2,
                behind: 1,
            }
        );
        assert_eq!(
            parse_branch_header("## feature/x"),
            BranchHeader {
                branch: Some("feature/x".into()),
                ..BranchHeader::default()
            }
        );
        assert_eq!(
            parse_branch_header("## HEAD (no branch)"),
            BranchHeader::default()
        );
        assert_eq!(
            parse_branch_header("## No commits yet on main"),
            BranchHeader::default()
        );
    }

    #[test]
    fn status_with_branch_separates_header_from_records() {
        let nul = char::from(0);
        let output =
            format!("## main...origin/main [ahead 1]{nul} M src/lib.rs{nul}?? notes.txt{nul}");
        let (header, changes) = parse_porcelain_status_with_branch(Path::new("repo"), &output);
        assert_eq!(header.branch.as_deref(), Some("main"));
        assert_eq!(header.ahead, 1);
        assert_eq!(changes.len(), 2);
    }

    #[test]
    fn status_label_matches_porcelain_codes() {
        assert_eq!(status_label('?', '?'), "untracked");
        assert_eq!(status_label(' ', 'M'), "modified");
        assert_eq!(status_label('A', ' '), "added");
        assert_eq!(status_label(' ', 'D'), "deleted");
        assert_eq!(status_label('R', ' '), "renamed");
        assert_eq!(status_label('U', 'U'), "conflicted");
        assert_eq!(status_label('M', 'M'), "staged + modified");
    }

    #[test]
    fn parse_porcelain_status_handles_z_records_and_renames() {
        let changes = parse_porcelain_status(
            Path::new("C:/repo"),
            " M src/main.rs\0R  src/new name.rs\0src/old name.rs\0?? notes/todo.txt\0UU conflict.txt\0",
        );

        assert_eq!(changes.len(), 4);
        assert_eq!(changes[0].path, "src/main.rs");
        assert_eq!(
            changes[0].absolute_path.replace('\\', "/"),
            "C:/repo/src/main.rs"
        );
        assert!(changes[0].unstaged);
        assert_eq!(changes[1].status, "renamed");
        assert_eq!(changes[1].original_path.as_deref(), Some("src/old name.rs"));
        assert!(changes[2].untracked);
        assert!(changes[3].conflicted);
    }

    #[test]
    fn status_root_and_paths_use_native_separators() {
        let root = native_path("C:/repo");
        assert_eq!(root.to_string_lossy(), format!("C:{MAIN_SEPARATOR}repo"));

        let changes = parse_porcelain_status(&root, " M src/main.rs\0");

        assert_eq!(
            changes[0].absolute_path,
            format!("C:{MAIN_SEPARATOR}repo{MAIN_SEPARATOR}src{MAIN_SEPARATOR}main.rs")
        );
        assert_eq!(changes[0].path, "src/main.rs");
    }

    #[cfg(windows)]
    #[test]
    fn root_from_cdup_keeps_the_callers_spelling() {
        assert_eq!(
            root_in_callers_spelling(Path::new(r"R:\REPOS\x\Sub\deeper"), "../../"),
            Some(native_path(r"R:\REPOS\x"))
        );
        assert_eq!(
            root_in_callers_spelling(Path::new("R:/Repos/x"), ""),
            Some(native_path(r"R:\Repos\x"))
        );
        assert_eq!(
            root_in_callers_spelling(Path::new(r"R:\Repos\x\..\y"), "../"),
            None
        );
    }

    #[cfg(not(windows))]
    #[test]
    fn root_from_cdup_keeps_the_callers_spelling() {
        assert_eq!(
            root_in_callers_spelling(Path::new("/srv/REPOS/x/Sub/deeper"), "../../"),
            Some(native_path("/srv/REPOS/x"))
        );
        assert_eq!(
            root_in_callers_spelling(Path::new("/srv/x/../y"), "../"),
            None
        );
        assert_eq!(root_in_callers_spelling(Path::new("/srv/x"), "sub/"), None);
    }

    #[test]
    fn untracked_directory_loses_its_trailing_separator() {
        let changes = parse_porcelain_status(&native_path("C:/repo"), "?? newdir/\0");

        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].path, "newdir");
        assert!(changes[0].untracked);
        assert_eq!(
            changes[0].absolute_path,
            format!("C:{MAIN_SEPARATOR}repo{MAIN_SEPARATOR}newdir")
        );
    }

    fn status(branch: &str) -> GitRepositoryStatus {
        GitRepositoryStatus {
            is_repo: true,
            branch: Some(branch.to_string()),
            ..GitRepositoryStatus::not_a_repo()
        }
    }

    #[test]
    fn status_cache_keeps_two_roots_apart() {
        let cache = StatusCache::new(Duration::from_secs(60), 16);
        let first = native_path("C:/one");
        let second = native_path("C:/two");

        cache.put(&first, status("first"));
        cache.put(&second, status("second"));

        assert_eq!(cache.get(&first), Some(status("first")));
        assert_eq!(cache.get(&second), Some(status("second")));
        cache.forget(&first);
        assert_eq!(cache.get(&first), None);
        assert_eq!(cache.get(&second), Some(status("second")));
        #[cfg(windows)]
        assert_eq!(cache.get(Path::new("c:/TWO/")), Some(status("second")));
    }

    #[test]
    fn status_cache_entry_expires_after_the_ttl() {
        let cache = StatusCache::new(Duration::from_millis(100), 16);
        let root = native_path("C:/repo");

        cache.put(&root, status("main"));
        assert_eq!(cache.get(&root), Some(status("main")));
        std::thread::sleep(Duration::from_millis(150));

        assert_eq!(cache.get(&root), None);
    }

    #[test]
    fn status_cache_drops_the_oldest_root_past_its_capacity() {
        let cache = StatusCache::new(Duration::from_secs(60), 2);
        for name in ["C:/a", "C:/b", "C:/c"] {
            cache.put(&native_path(name), status(name));
        }

        assert_eq!(cache.get(&native_path("C:/a")), None);
        assert!(cache.get(&native_path("C:/b")).is_some());
        assert!(cache.get(&native_path("C:/c")).is_some());
    }
}
