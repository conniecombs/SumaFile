//! Repository status: one `git status --porcelain=v1 -z --branch` run gives
//! branch, upstream, ahead/behind and the changes; only the abbreviated HEAD
//! needs a second process, and the filter listing that keeps the status from
//! running a repository's programs a third. Results are cached per
//! repository root, and callers that ask for the same root while its status
//! runs share that one run.

use std::collections::HashMap;
use std::path::{Component, Path, PathBuf, MAIN_SEPARATOR_STR};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use crate::git::Git;
use crate::models::{GitFileStatus, GitRepositoryStatus};
use crate::paths::{comparable_path, native_path};
use crate::run::CancelCheck;
use crate::{GIT_CANCELLED, GIT_NOT_FOUND};

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

/// One status run in flight; its callers wait for `result`.
#[derive(Default)]
struct InFlight {
    result: Mutex<Option<Result<GitRepositoryStatus, String>>>,
    done: Condvar,
}

/// How often a caller waiting for another caller's status run looks at its
/// own cancel check.
const WAIT_POLL: Duration = Duration::from_millis(15);

/// Status runs keyed by repository root, case-insensitively on Windows and
/// whatever the separators.
pub struct StatusCache {
    ttl: Duration,
    capacity: usize,
    entries: Mutex<Vec<CacheEntry>>,
    in_flight: Mutex<HashMap<String, Arc<InFlight>>>,
}

impl StatusCache {
    pub fn new(ttl: Duration, capacity: usize) -> Self {
        StatusCache {
            ttl,
            capacity: capacity.max(1),
            entries: Mutex::new(Vec::new()),
            in_flight: Mutex::new(HashMap::new()),
        }
    }

    fn lock_in_flight(&self) -> MutexGuard<'_, HashMap<String, Arc<InFlight>>> {
        self.in_flight
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// The status of `root`: from the cache when it was taken within the
    /// TTL, else from the run another caller has in flight for `root`, else
    /// from `run`, which this caller then runs for everyone who asks
    /// meanwhile. A successful run is cached unless the root was
    /// [`StatusCache::forget`]ten while it ran.
    ///
    /// `cancel` stops only this caller's wait: a shared run goes on for its
    /// other callers. A run that ended with [`GIT_CANCELLED`] (its own
    /// caller cancelled it) is not handed to the callers that waited for it;
    /// they run it again themselves.
    pub fn get_or_run(
        &self,
        root: &Path,
        cancel: Option<&CancelCheck>,
        run: impl Fn() -> Result<GitRepositoryStatus, String>,
    ) -> Result<GitRepositoryStatus, String> {
        let key = comparable_path(root);
        loop {
            if let Some(status) = self.get(root) {
                return Ok(status);
            }
            let (flight, leader) = {
                let mut in_flight = self.lock_in_flight();
                match in_flight.get(&key) {
                    Some(flight) => (flight.clone(), false),
                    None => {
                        let flight = Arc::new(InFlight::default());
                        in_flight.insert(key.clone(), flight.clone());
                        (flight, true)
                    }
                }
            };
            if leader {
                return self.lead(&key, &flight, &run);
            }
            match wait_for(&flight, cancel) {
                Err(error) if error == GIT_CANCELLED && !is_cancelled(cancel) => continue,
                result => return result,
            }
        }
    }

    fn lead(
        &self,
        key: &str,
        flight: &Arc<InFlight>,
        run: &dyn Fn() -> Result<GitRepositoryStatus, String>,
    ) -> Result<GitRepositoryStatus, String> {
        // Hands the result over and leaves the map even when `run` panics.
        struct Finish<'a> {
            cache: &'a StatusCache,
            key: &'a str,
            flight: &'a Arc<InFlight>,
            result: Option<Result<GitRepositoryStatus, String>>,
        }
        impl Drop for Finish<'_> {
            fn drop(&mut self) {
                let current = {
                    let mut in_flight = self.cache.lock_in_flight();
                    let current = in_flight
                        .get(self.key)
                        .is_some_and(|flight| Arc::ptr_eq(flight, self.flight));
                    if current {
                        in_flight.remove(self.key);
                    }
                    current
                };
                let result = self
                    .result
                    .take()
                    .unwrap_or_else(|| Err("Git status failed.".to_string()));
                if current {
                    if let Ok(status) = &result {
                        self.cache.put_key(self.key, status.clone());
                    }
                }
                *self
                    .flight
                    .result
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(result);
                self.flight.done.notify_all();
            }
        }
        let mut finish = Finish {
            cache: self,
            key,
            flight,
            result: None,
        };
        let result = run();
        finish.result = Some(result.clone());
        drop(finish);
        result
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
        self.put_key(&comparable_path(root), status);
    }

    fn put_key(&self, key: &str, status: GitRepositoryStatus) {
        let key = key.to_string();
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
    ///
    /// A status run in flight for the root started before the change: it
    /// still answers the callers already waiting for it, but later callers
    /// start a run of their own and its result is not cached.
    pub fn forget(&self, root: &Path) {
        let key = comparable_path(root);
        self.lock_in_flight().remove(&key);
        self.lock().retain(|entry| entry.key != key);
    }
}

fn is_cancelled(cancel: Option<&CancelCheck>) -> bool {
    cancel.is_some_and(|cancel| cancel())
}

/// The result of `flight`, or [`GIT_CANCELLED`] once `cancel` says so.
fn wait_for(
    flight: &InFlight,
    cancel: Option<&CancelCheck>,
) -> Result<GitRepositoryStatus, String> {
    let mut result = flight
        .result
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    loop {
        if let Some(result) = result.as_ref() {
            return result.clone();
        }
        if is_cancelled(cancel) {
            return Err(GIT_CANCELLED.to_string());
        }
        result = flight
            .done
            .wait_timeout(result, WAIT_POLL)
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .0;
    }
}

/// The work tree root that holds `path`, or `None` outside a work tree.
///
/// One `rev-parse --show-toplevel --show-cdup` answers both "is this a
/// repository" and "where is its root". The root is `path` with the
/// `--show-cdup` levels removed, so it keeps the caller's spelling (case,
/// 8.3 short names, substituted drives) and row paths built from it match the
/// listing. Git counts those levels on the resolved path, so through a
/// junction or a symbolic link the rebuilt folder can be another one, even
/// another repository's root: it is used only when it is the same folder as
/// git's `--show-toplevel` (see `same_folder`), and otherwise the root is
/// `--show-toplevel`, in native separators. Fails only
/// with [`GIT_NOT_FOUND`] and, once `cancel` says so, [`GIT_CANCELLED`].
pub fn find_root(
    git: &Git,
    path: &Path,
    cancel: Option<&CancelCheck>,
) -> Result<Option<PathBuf>, String> {
    let output = match git.query_with(
        path,
        &[],
        &["rev-parse", "--show-toplevel", "--show-cdup"],
        cancel,
    ) {
        Ok(output) => output,
        Err(error) if error == GIT_NOT_FOUND || error == GIT_CANCELLED => return Err(error),
        Err(_) => return Ok(None),
    };
    let mut lines = output.lines();
    let toplevel = lines.next().unwrap_or_default().trim();
    if toplevel.is_empty() {
        return Ok(None);
    }
    let toplevel = native_path(toplevel);
    let cdup = lines.next().unwrap_or_default().trim();
    Ok(Some(
        root_in_callers_spelling(path, cdup)
            .filter(|root| same_folder(root, &toplevel))
            .unwrap_or(toplevel),
    ))
}

/// `rebuilt` is the folder git named `toplevel`: both resolve, every link
/// followed, to one path. Where a path cannot be resolved (a file system
/// that cannot name a handle's final path) `rebuilt` must at least hold a
/// `.git` file or folder.
fn same_folder(rebuilt: &Path, toplevel: &Path) -> bool {
    match (
        std::fs::canonicalize(rebuilt),
        std::fs::canonicalize(toplevel),
    ) {
        (Ok(rebuilt), Ok(toplevel)) => rebuilt == toplevel,
        _ => std::fs::symlink_metadata(rebuilt.join(".git")).is_ok(),
    }
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
/// returned from `cache`, and one in flight for the root is shared.
pub fn repo_status(
    git: &Git,
    cache: &StatusCache,
    path: &Path,
) -> Result<GitRepositoryStatus, String> {
    repo_status_with_cancel(git, cache, path, None)
}

/// [`repo_status`] that `cancel` stops with [`GIT_CANCELLED`]: it kills
/// this caller's git process, or ends its wait for another caller's run.
pub fn repo_status_with_cancel(
    git: &Git,
    cache: &StatusCache,
    path: &Path,
    cancel: Option<&CancelCheck>,
) -> Result<GitRepositoryStatus, String> {
    std::fs::metadata(path).map_err(|error| {
        format!(
            "Path does not exist or is not accessible: {} ({error})",
            path.display()
        )
    })?;
    let Some(root) = find_root(git, path, cancel)? else {
        return Ok(GitRepositoryStatus::not_a_repo());
    };
    cache.get_or_run(&root, cancel, || take_status(git, &root, cancel))
}

/// One status run of the repository at `root`. A git failure gives a status
/// without changes; only [`GIT_NOT_FOUND`] and [`GIT_CANCELLED`] fail it.
fn take_status(
    git: &Git,
    root: &Path,
    cancel: Option<&CancelCheck>,
) -> Result<GitRepositoryStatus, String> {
    let stopped = |error: &String| error == GIT_NOT_FOUND || error == GIT_CANCELLED;
    let head = match git.query_with(root, &[], &["rev-parse", "--short", "HEAD"], cancel) {
        Ok(value) => Some(value.trim().to_string()).filter(|value| !value.is_empty()),
        Err(error) if stopped(&error) => return Err(error),
        Err(_) => None,
    };
    let status = git.filter_overrides(root, cancel).and_then(|config| {
        git.query_with(
            root,
            &config,
            &[
                "status",
                "--porcelain=v1",
                "-z",
                "--branch",
                "--untracked-files=normal",
                "--ignore-submodules=dirty",
            ],
            cancel,
        )
    });
    let (header, changes) = match status {
        Ok(output) => parse_porcelain_status_with_branch(root, &output),
        Err(error) if stopped(&error) => return Err(error),
        Err(error) => {
            log::debug!("Git status of {} failed: {error}", root.display());
            Default::default()
        }
    };

    Ok(GitRepositoryStatus {
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
    })
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
    use crate::run::CancelCheck;
    use crate::GIT_CANCELLED;
    use std::path::{Path, MAIN_SEPARATOR};
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::sync::{mpsc, Arc, Barrier};
    use std::time::Duration;
    use std::time::Instant;

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

    /// Runs `get_or_run` for `root` on a thread of its own; the run counts
    /// itself in `runs`, signals `started` and sleeps `hold`.
    fn spawn_status_call(
        cache: &Arc<StatusCache>,
        root: &str,
        runs: &Arc<AtomicUsize>,
        started: Option<mpsc::Sender<()>>,
        hold: Duration,
        cancel: Option<CancelCheck>,
    ) -> std::thread::JoinHandle<Result<GitRepositoryStatus, String>> {
        let (cache, runs, root) = (cache.clone(), runs.clone(), native_path(root));
        std::thread::spawn(move || {
            cache.get_or_run(&root, cancel.as_ref(), || {
                runs.fetch_add(1, Ordering::SeqCst);
                if let Some(started) = &started {
                    let _ = started.send(());
                }
                std::thread::sleep(hold);
                Ok(status("shared"))
            })
        })
    }

    #[test]
    fn callers_for_one_root_share_the_status_run_in_flight() {
        let cache = Arc::new(StatusCache::new(Duration::from_secs(60), 16));
        let runs = Arc::new(AtomicUsize::new(0));
        let (started_tx, started) = mpsc::channel();
        let hold = Duration::from_millis(300);

        let first = spawn_status_call(&cache, "C:/repo", &runs, Some(started_tx), hold, None);
        started.recv_timeout(Duration::from_secs(5)).unwrap();
        let waiters: Vec<_> = (0..3)
            .map(|_| spawn_status_call(&cache, "C:/repo", &runs, None, hold, None))
            .collect();
        let other_root = spawn_status_call(&cache, "C:/other", &runs, None, hold, None);

        assert_eq!(first.join().unwrap(), Ok(status("shared")));
        for waiter in waiters {
            assert_eq!(waiter.join().unwrap(), Ok(status("shared")));
        }
        assert_eq!(other_root.join().unwrap(), Ok(status("shared")));
        assert_eq!(runs.load(Ordering::SeqCst), 2, "one run per root");
        assert_eq!(cache.get(&native_path("C:/repo")), Some(status("shared")));
    }

    #[test]
    fn a_cancelled_waiter_stops_waiting_and_the_shared_run_goes_on() {
        let cache = Arc::new(StatusCache::new(Duration::from_secs(60), 16));
        let runs = Arc::new(AtomicUsize::new(0));
        let (started_tx, started) = mpsc::channel();
        let leader = spawn_status_call(
            &cache,
            "C:/repo",
            &runs,
            Some(started_tx),
            Duration::from_millis(1500),
            None,
        );
        started.recv_timeout(Duration::from_secs(5)).unwrap();
        let flag = Arc::new(AtomicBool::new(false));
        let cancel: CancelCheck = {
            let flag = flag.clone();
            Arc::new(move || flag.load(Ordering::SeqCst))
        };
        let waiter =
            spawn_status_call(&cache, "C:/repo", &runs, None, Duration::ZERO, Some(cancel));

        std::thread::sleep(Duration::from_millis(100));
        flag.store(true, Ordering::SeqCst);
        let cancelled_at = Instant::now();
        assert_eq!(waiter.join().unwrap(), Err(GIT_CANCELLED.to_string()));
        assert!(cancelled_at.elapsed() < Duration::from_millis(1000));
        assert_eq!(leader.join().unwrap(), Ok(status("shared")));
        assert_eq!(runs.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn waiters_run_again_when_the_run_they_shared_was_cancelled() {
        let cache = Arc::new(StatusCache::new(Duration::from_secs(60), 16));
        let root = native_path("C:/repo");
        let (started_tx, started) = mpsc::channel();
        let release = Arc::new(Barrier::new(2));
        let leader = {
            let (cache, root, release) = (cache.clone(), root.clone(), release.clone());
            std::thread::spawn(move || {
                cache.get_or_run(&root, None, || {
                    let _ = started_tx.send(());
                    release.wait();
                    Err(GIT_CANCELLED.to_string())
                })
            })
        };
        started.recv_timeout(Duration::from_secs(5)).unwrap();
        let runs = Arc::new(AtomicUsize::new(0));
        let waiter = spawn_status_call(&cache, "C:/repo", &runs, None, Duration::ZERO, None);
        std::thread::sleep(Duration::from_millis(100));
        release.wait();

        assert_eq!(leader.join().unwrap(), Err(GIT_CANCELLED.to_string()));
        assert_eq!(waiter.join().unwrap(), Ok(status("shared")));
        assert_eq!(runs.load(Ordering::SeqCst), 1, "the waiter ran it itself");
    }

    #[test]
    fn a_run_in_flight_when_the_root_is_forgotten_is_not_cached() {
        let cache = Arc::new(StatusCache::new(Duration::from_secs(60), 16));
        let runs = Arc::new(AtomicUsize::new(0));
        let (started_tx, started) = mpsc::channel();
        let stale = spawn_status_call(
            &cache,
            "C:/repo",
            &runs,
            Some(started_tx),
            Duration::from_millis(300),
            None,
        );
        started.recv_timeout(Duration::from_secs(5)).unwrap();

        cache.forget(&native_path("C:/repo"));
        // A caller after the change starts a run of its own.
        let fresh = spawn_status_call(&cache, "C:/repo", &runs, None, Duration::ZERO, None);
        assert_eq!(fresh.join().unwrap(), Ok(status("shared")));
        cache.forget(&native_path("C:/repo"));

        assert_eq!(stale.join().unwrap(), Ok(status("shared")));
        assert_eq!(runs.load(Ordering::SeqCst), 2);
        assert_eq!(cache.get(&native_path("C:/repo")), None);
    }
}
