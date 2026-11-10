//! Row decorations for one folder: the status labels of its direct children.
//!
//! A folder below no `.git` marker spawns no git at all, so browsing outside
//! repositories costs nothing. The marker walk follows
//! `GIT_CEILING_DIRECTORIES` as git does, and is skipped when `GIT_DIR` or
//! `GIT_WORK_TREE` names the repository instead.

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};

use crate::git::Git;
use crate::models::{GitDecorationRow, GitFolderDecorations};
use crate::paths::{comparable_path, native_path};
use crate::status::{repo_status, StatusCache};

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

/// The decorations of `folder`'s direct children, from one cached status
/// run of its repository. A missing folder, a folder outside any repository
/// and any git failure give `{ in_repository: false, root: null, rows: [] }`.
pub fn folder_decorations(git: &Git, cache: &StatusCache, folder: &Path) -> GitFolderDecorations {
    if !folder.is_dir() {
        return GitFolderDecorations::outside();
    }
    let git_dir: Option<OsString> = git.env_var("GIT_DIR");
    let git_work_tree: Option<OsString> = git.env_var("GIT_WORK_TREE");
    if marker_walk_applies(git_dir.as_deref(), git_work_tree.as_deref()) {
        let ceilings = ceilings_from(git.env_var("GIT_CEILING_DIRECTORIES").as_deref());
        if find_repository_marker(folder, &ceilings).is_none() {
            return GitFolderDecorations::outside();
        }
    }
    let status = match repo_status(git, cache, folder) {
        Ok(status) if status.is_repo => status,
        Ok(_) => return GitFolderDecorations::outside(),
        Err(error) => {
            log::debug!(
                "Git decorations for {} are empty: {error}",
                folder.display()
            );
            return GitFolderDecorations::outside();
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
    GitFolderDecorations {
        in_repository: true,
        root: status.root,
        rows,
    }
}

#[cfg(test)]
mod tests {
    use super::{ceilings_from, find_repository_marker, marker_walk_applies};
    use std::ffi::OsStr;

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
