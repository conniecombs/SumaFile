//! Wire shapes. Results keep the 1.0.4 IPC shapes, with snake_case fields.

use serde::{Deserialize, Serialize};

/// One changed path of a repository, as `git status --porcelain=v1` reports
/// it.
#[derive(Debug, Serialize, Deserialize, Clone, PartialEq, Eq)]
pub struct GitFileStatus {
    /// Repository-relative, with `/`. An untracked directory has no
    /// trailing `/`.
    pub path: String,
    /// `<root>` joined with `path`, in native separators.
    pub absolute_path: String,
    pub original_path: Option<String>,
    pub status: String,
    pub index_status: String,
    pub worktree_status: String,
    pub staged: bool,
    pub unstaged: bool,
    pub untracked: bool,
    pub conflicted: bool,
}

#[derive(Debug, Serialize, Deserialize, Clone, PartialEq, Eq)]
pub struct GitRepositoryStatus {
    pub is_repo: bool,
    /// The work tree root, in native separators.
    pub root: Option<String>,
    pub branch: Option<String>,
    pub upstream: Option<String>,
    pub head: Option<String>,
    pub ahead: u32,
    pub behind: u32,
    pub staged: u32,
    pub unstaged: u32,
    pub untracked: u32,
    pub conflicted: u32,
    pub changes: Vec<GitFileStatus>,
}

impl GitRepositoryStatus {
    /// The status of a path outside any repository.
    pub fn not_a_repo() -> GitRepositoryStatus {
        GitRepositoryStatus {
            is_repo: false,
            root: None,
            branch: None,
            upstream: None,
            head: None,
            ahead: 0,
            behind: 0,
            staged: 0,
            unstaged: 0,
            untracked: 0,
            conflicted: 0,
            changes: Vec::new(),
        }
    }
}

#[derive(Debug, Serialize, Deserialize, Clone, PartialEq, Eq)]
pub struct GitCommandResult {
    pub command: String,
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
    pub summary: String,
}

/// One decorated row: a direct child of the folder asked about.
#[derive(Debug, Serialize, Deserialize, Clone, PartialEq, Eq)]
pub struct GitDecorationRow {
    /// `<folder>\<name>`, in native separators.
    pub path: String,
    /// The status label, e.g. `modified` or `untracked`.
    pub status: String,
}

/// The answer to `repo.decorations`.
#[derive(Debug, Serialize, Deserialize, Clone, PartialEq, Eq)]
pub struct GitFolderDecorations {
    pub in_repository: bool,
    pub root: Option<String>,
    pub rows: Vec<GitDecorationRow>,
}

impl GitFolderDecorations {
    /// `{ in_repository: false, root: null, rows: [] }`.
    pub fn outside() -> GitFolderDecorations {
        GitFolderDecorations {
            in_repository: false,
            root: None,
            rows: Vec::new(),
        }
    }
}
