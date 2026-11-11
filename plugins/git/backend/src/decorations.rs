//! Row decorations for one folder: the status labels of its direct children.
//!
//! A folder below no `.git` marker spawns no git at all, so browsing outside
//! repositories costs nothing. The marker walk follows
//! `GIT_CEILING_DIRECTORIES` as git does, and is skipped when `GIT_DIR` or
//! `GIT_WORK_TREE` names the repository instead.

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};

use crate::git::Git;
use crate::models::{GitDecorationRow, GitFolderDecorations};
use crate::paths::{comparable_path, native_path};
use crate::run::CancelCheck;
use crate::status::{repo_status_with_cancel, StatusCache};
use crate::{GIT_CANCELLED, GIT_NOT_FOUND};

/// The nearest folder, starting with `folder` itself, that holds a `.git`
/// file or folder. The walk never goes up into a folder of `ceilings`.
pub fn find_repository_marker(folder: &Path, ceilings: &[PathBuf]) -> Option<PathBuf> {
    let ceilings: Vec<String> = ceilings
        .iter()
        .map(|ceiling| comparable_path(ceiling))
        .collect();
    let mut current = folder;
    loop {
        if std::fs::symlink_metadata(current.join(".git")).is_ok() {
            return Some(current.to_path_buf());
        }
        let parent = current.parent()?;
        if parent.as_os_str().is_empty() || ceilings.contains(&comparable_path(parent)) {
            return None;
        }
        current = parent;
    }
}

/// The absolute folders of a `GIT_CEILING_DIRECTORIES` value, separated as
/// `PATH` is (`;` on Windows). Relative and empty entries are ignored.
pub fn ceilings_from(value: Option<&OsStr>) -> Vec<PathBuf> {
    value
        .map(|value| {
            std::env::split_paths(value)
                .filter(|entry| entry.is_absolute())
                .collect()
        })
        .unwrap_or_default()
}

/// The marker walk finds the repository only while neither `GIT_DIR` nor
/// `GIT_WORK_TREE` is set.
pub fn marker_walk_applies(git_dir: Option<&OsStr>, git_work_tree: Option<&OsStr>) -> bool {
    let is_set = |value: Option<&OsStr>| value.is_some_and(|value| !value.is_empty());
    !is_set(git_dir) && !is_set(git_work_tree)
}

/// Set once "Git was not found" has been logged: decorations log it once
/// per process and keep answering "not in a repository" without a spawn.
static GIT_NOT_FOUND_LOGGED: AtomicBool = AtomicBool::new(false);

/// The decorations of `folder`'s direct children, from one cached status
/// run of its repository. A missing folder, a folder outside any repository
/// and any git failure give `{ in_repository: false, root: null, rows: [] }`.
pub fn folder_decorations(git: &Git, cache: &StatusCache, folder: &Path) -> GitFolderDecorations {
    folder_decorations_with_cancel(git, cache, folder, None)
        .unwrap_or_else(|_| GitFolderDecorations::outside())
}

/// [`folder_decorations`] that `cancel` stops: it then fails with
/// [`GIT_CANCELLED`], its only error. A status run other callers share goes
/// on for them.
pub fn folder_decorations_with_cancel(
    git: &Git,
    cache: &StatusCache,
    folder: &Path,
    cancel: Option<&CancelCheck>,
) -> Result<GitFolderDecorations, String> {
    if !folder.is_dir() {
        return Ok(GitFolderDecorations::outside());
    }
    let git_dir: Option<OsString> = git.env_var("GIT_DIR");
    let git_work_tree: Option<OsString> = git.env_var("GIT_WORK_TREE");
    if marker_walk_applies(git_dir.as_deref(), git_work_tree.as_deref()) {
        let ceilings = ceilings_from(git.env_var("GIT_CEILING_DIRECTORIES").as_deref());
        if find_repository_marker(folder, &ceilings).is_none() {
            return Ok(GitFolderDecorations::outside());
        }
    }
    let status = match repo_status_with_cancel(git, cache, folder, cancel) {
        Ok(status) if status.is_repo => status,
        Ok(_) => return Ok(GitFolderDecorations::outside()),
        Err(error) if error == GIT_CANCELLED => return Err(error),
        Err(error) if error == GIT_NOT_FOUND => {
            if !GIT_NOT_FOUND_LOGGED.swap(true, Ordering::Relaxed) {
                log::warn!("Git decorations are off: {error}");
            }
            return Ok(GitFolderDecorations::outside());
        }
        Err(error) => {
            log::debug!(
                "Git decorations for {} are empty: {error}",
                folder.display()
            );
            return Ok(GitFolderDecorations::outside());
        }
    };

    // Rows use the folder's own spelling, so their keys match the listing.
    let base = native_path(&folder.to_string_lossy());
    let folder_key = comparable_path(folder);
    let rows = status
        .changes
        .iter()
        .filter_map(|change| {
            let absolute = Path::new(&change.absolute_path);
            let name = absolute.file_name()?;
            (comparable_path(absolute.parent()?) == folder_key).then(|| GitDecorationRow {
                path: base.join(name).to_string_lossy().to_string(),
                status: change.status.clone(),
            })
        })
        .collect();
    Ok(GitFolderDecorations {
        in_repository: true,
        root: status.root,
        rows,
    })
}

#[cfg(test)]
mod tests {
    use super::{ceilings_from, find_repository_marker, folder_decorations, marker_walk_applies};
    use crate::git::Git;
    use crate::status::{StatusCache, STATUS_CACHE_CAPACITY, STATUS_CACHE_TTL};
    use crate::GIT_NOT_FOUND;
    use std::ffi::{OsStr, OsString};
    use std::sync::Mutex;

    /// Keeps every warning logged in this test binary.
    struct Warnings(Mutex<Vec<String>>);

    impl log::Log for Warnings {
        fn enabled(&self, metadata: &log::Metadata<'_>) -> bool {
            metadata.level() <= log::Level::Warn
        }

        fn log(&self, record: &log::Record<'_>) {
            if self.enabled(record.metadata()) {
                self.0
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner())
                    .push(record.args().to_string());
            }
        }

        fn flush(&self) {}
    }

    static WARNINGS: Warnings = Warnings(Mutex::new(Vec::new()));

    /// Git missing is a warning in `plugins.log`, once per process; every
    /// request still answers "not in a repository" and spawns nothing.
    #[test]
    fn git_not_found_is_warned_once_per_process() {
        let _ = log::set_logger(&WARNINGS);
        log::set_max_level(log::LevelFilter::Warn);
        let temp = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(temp.path().join(".git")).unwrap();
        let git = Git::with_executable(None, temp.path().join("no-hooks-test")).with_env(
            ["GIT_DIR", "GIT_WORK_TREE", "GIT_CEILING_DIRECTORIES"]
                .into_iter()
                .map(|name| (OsString::from(name), OsString::new()))
                .collect(),
        );
        let cache = StatusCache::new(STATUS_CACHE_TTL, STATUS_CACHE_CAPACITY);

        for _ in 0..3 {
            let decorations = folder_decorations(&git, &cache, temp.path());
            assert!(!decorations.in_repository);
            assert!(decorations.rows.is_empty());
        }

        let warned: Vec<String> = WARNINGS
            .0
            .lock()
            .unwrap()
            .iter()
            .filter(|message| message.contains(GIT_NOT_FOUND))
            .cloned()
            .collect();
        assert_eq!(warned.len(), 1, "{warned:?}");
        assert_eq!(git.spawn_count(), 0);
    }

    #[test]
    fn marker_walk_honours_git_ceiling_directories() {
        let temp = tempfile::tempdir().unwrap();
        let t = temp.path();
        let deep = t.join("a").join("b");
        std::fs::create_dir_all(&deep).unwrap();
        std::fs::create_dir_all(t.join(".git")).unwrap();

        assert_eq!(find_repository_marker(&deep, &[]), Some(t.to_path_buf()));
        assert_eq!(find_repository_marker(&deep, &[t.to_path_buf()]), None);
        // The folder itself is always checked, even when it is a ceiling.
        assert_eq!(
            find_repository_marker(t, &[t.to_path_buf()]),
            Some(t.to_path_buf())
        );

        std::fs::create_dir_all(t.join("a").join(".git")).unwrap();
        assert_eq!(
            find_repository_marker(&deep, &[t.to_path_buf()]),
            Some(t.join("a"))
        );

        // A `.git` file (a worktree or submodule) counts as a marker too.
        let worktree = t.join("c").join("d");
        std::fs::create_dir_all(&worktree).unwrap();
        std::fs::write(t.join("c").join(".git"), "gitdir: elsewhere\n").unwrap();
        assert_eq!(
            find_repository_marker(&worktree, &[t.to_path_buf()]),
            Some(t.join("c"))
        );
    }

    #[test]
    fn ceilings_from_keeps_absolute_entries_only() {
        let temp = tempfile::tempdir().unwrap();
        let value = std::env::join_paths([temp.path(), std::path::Path::new("relative")]).unwrap();

        assert_eq!(ceilings_from(Some(&value)), vec![temp.path().to_path_buf()]);
        assert!(ceilings_from(None).is_empty());
    }

    #[test]
    fn marker_walk_applies_only_without_git_dir_or_work_tree() {
        assert!(marker_walk_applies(None, None));
        assert!(marker_walk_applies(Some(OsStr::new("")), None));
        assert!(!marker_walk_applies(Some(OsStr::new("C:/repo/.git")), None));
        assert!(!marker_walk_applies(None, Some(OsStr::new("C:/repo"))));
        assert!(!marker_walk_applies(
            Some(OsStr::new("C:/repo/.git")),
            Some(OsStr::new("C:/repo"))
        ));
    }
}
