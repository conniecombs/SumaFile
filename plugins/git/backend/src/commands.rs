//! The commands: stage, unstage, discard, diff, commit, fetch, pull and push.
//!
//! Each resolves the repository of `path` first, so outside a repository it
//! fails with `The current path is not inside a Git repository.` and with no
//! git it fails with [`crate::GIT_NOT_FOUND`]. Every command that changes the
//! repository drops the root's cached status before it runs and again after
//! it finishes, however it finishes.

use std::ffi::OsString;
use std::path::{Path, PathBuf};

use crate::git::Git;
use crate::models::GitCommandResult;
use crate::paths::{existing_path, paths_equal, pathspecs_under_root, repo_relative_path};
use crate::run::{CancelCheck, RunOptions, GIT_LOCAL_TIMEOUT};
use crate::status::{find_root, repo_status, StatusCache};

/// The work tree root that holds `path`.
pub fn repository_root(git: &Git, path: &str) -> Result<PathBuf, String> {
    let path = existing_path(path)?;
    find_root(git, &path, None)?
        .ok_or_else(|| "The current path is not inside a Git repository.".to_string())
}

/// Drops the root's cached status now and again when dropped.
struct Mutation<'a> {
    cache: &'a StatusCache,
    root: &'a Path,
}

impl<'a> Mutation<'a> {
    fn begin(cache: &'a StatusCache, root: &'a Path) -> Mutation<'a> {
        cache.forget(root);
        Mutation { cache, root }
    }
}

impl Drop for Mutation<'_> {
    fn drop(&mut self) {
        self.cache.forget(self.root);
    }
}

fn os_args(args: &[&str]) -> Vec<OsString> {
    args.iter().map(OsString::from).collect()
}

fn run_path_command(
    git: &Git,
    root: &Path,
    command_name: &str,
    prefix_args: &[&str],
    pathspecs: &[String],
    summary: &str,
) -> Result<GitCommandResult, String> {
    let mut args = os_args(prefix_args);
    args.extend(pathspecs.iter().map(OsString::from));
    git.run_in_root(root, &args, true, &RunOptions::local(command_name), summary)
}

pub fn stage(
    git: &Git,
    cache: &StatusCache,
    path: &str,
    paths: &[String],
) -> Result<GitCommandResult, String> {
    let root = repository_root(git, path)?;
    let pathspecs = pathspecs_under_root(&root, paths)?;
    let _mutation = Mutation::begin(cache, &root);
    run_path_command(
        git,
        &root,
        "stage",
        &["add", "--"],
        &pathspecs,
        "Staged selected Git path(s).",
    )
}

pub fn unstage(
    git: &Git,
    cache: &StatusCache,
    path: &str,
    paths: &[String],
) -> Result<GitCommandResult, String> {
    let root = repository_root(git, path)?;
    let pathspecs = pathspecs_under_root(&root, paths)?;
    let _mutation = Mutation::begin(cache, &root);
    run_path_command(
        git,
        &root,
        "unstage",
        &["restore", "--staged", "--"],
        &pathspecs,
        "Unstaged selected Git path(s).",
    )
}

/// Restores tracked paths from the index and HEAD, and deletes the selected
/// untracked paths (and newly added ones) with `git clean -fd`. Nothing that
/// was not selected changes.
pub fn discard(
    git: &Git,
    cache: &StatusCache,
    path: &str,
    paths: &[String],
) -> Result<GitCommandResult, String> {
    let root = repository_root(git, path)?;
    let pathspecs = pathspecs_under_root(&root, paths)?;
    let _mutation = Mutation::begin(cache, &root);
    let status = repo_status(git, cache, &root)?;
    let change_of = |pathspec: &str| {
        status
            .changes
            .iter()
            .find(|change| paths_equal(&change.path, pathspec))
    };
    let mut tracked = Vec::new();
    let mut cleanable = Vec::new();

    for pathspec in &pathspecs {
        let change = change_of(pathspec);
        if change.map(|change| change.untracked).unwrap_or(false) {
            cleanable.push(pathspec.clone());
        } else {
            tracked.push(pathspec.clone());
            if change
                .map(|change| change.index_status == "A")
                .unwrap_or(false)
            {
                cleanable.push(pathspec.clone());
            }
        }
    }

    let mut results = Vec::new();
    if !tracked.is_empty() {
        results.push(run_path_command(
            git,
            &root,
            "discard",
            &["restore", "--staged", "--"],
            &tracked,
            "Unstaged selected Git path(s).",
        )?);

        let restorable: Vec<String> = tracked
            .iter()
            .filter(|pathspec| {
                change_of(pathspec)
                    .map(|change| change.index_status != "A")
                    .unwrap_or(true)
            })
            .cloned()
            .collect();
        if !restorable.is_empty() {
            results.push(run_path_command(
                git,
                &root,
                "discard",
                &["restore", "--worktree", "--"],
                &restorable,
                "Discarded selected Git working tree change(s).",
            )?);
        }
    }

    if !cleanable.is_empty() {
        results.push(run_path_command(
            git,
            &root,
            "discard",
            &["clean", "-fd", "--"],
            &cleanable,
            "Removed selected untracked Git path(s).",
        )?);
    }

    Ok(combine_results(
        "git discard",
        "Discarded selected Git path(s).",
        results,
    ))
}

/// The staged and the working tree diff of one path, each under its own
/// heading. Like status it runs no program the repository configures: the
/// filter drivers are switched off (see [`Git::filter_overrides`]), and
/// `--no-ext-diff --no-textconv` skip `diff.external` and every diff
/// driver's `command` and `textconv`. `--ignore-submodules=dirty
/// --submodule=short` keep git from starting a `git status` or a `git diff`
/// inside a submodule.
pub fn diff(git: &Git, cache: &StatusCache, path: &str, file_path: &str) -> Result<String, String> {
    let root = repository_root(git, path)?;
    let pathspec = repo_relative_path(&root, file_path)?;
    let status = repo_status(git, cache, &root)?;
    if status
        .changes
        .iter()
        .any(|change| paths_equal(&change.path, &pathspec) && change.untracked)
    {
        return Ok(
            "No Git diff is available for untracked files until they are staged.".to_string(),
        );
    }

    let config = git.filter_overrides(&root, None)?;
    let diff_of = |cached: &[&str]| {
        let mut args = vec![
            "diff",
            "--no-ext-diff",
            "--no-textconv",
            "--ignore-submodules=dirty",
            "--submodule=short",
        ];
        args.extend_from_slice(cached);
        args.extend(["--", pathspec.as_str()]);
        git.query_with(&root, &config, &args, None)
    };
    let staged = diff_of(&["--cached"])?;
    let working = diff_of(&[])?;

    let mut sections = Vec::new();
    if !staged.trim().is_empty() {
        sections.push(format!("Staged changes\n\n{staged}"));
    }
    if !working.trim().is_empty() {
        sections.push(format!("Working tree changes\n\n{working}"));
    }

    Ok(if sections.is_empty() {
        "No Git diff for the selected path.".to_string()
    } else {
        sections.join("\n\n")
    })
}

/// Commits the staged changes with the trimmed `message`; hooks run.
pub fn commit(
    git: &Git,
    cache: &StatusCache,
    path: &str,
    message: &str,
    cancel: Option<CancelCheck>,
) -> Result<GitCommandResult, String> {
    let root = repository_root(git, path)?;
    let message = message.trim();
    if message.is_empty() {
        return Err("Commit message is required.".to_string());
    }

    let _mutation = Mutation::begin(cache, &root);
    git.run_in_root(
        &root,
        &[
            OsString::from("commit"),
            OsString::from("-m"),
            OsString::from(message),
        ],
        false,
        &RunOptions {
            name: "commit".to_string(),
            timeout: GIT_LOCAL_TIMEOUT,
            cancel,
        },
        "Commit completed.",
    )
}

/// fetch, pull or push.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RemoteCommand {
    Fetch,
    Pull,
    Push,
}

impl RemoteCommand {
    pub fn subcommand(self) -> &'static str {
        match self {
            RemoteCommand::Fetch => "fetch",
            RemoteCommand::Pull => "pull",
            RemoteCommand::Push => "push",
        }
    }

    fn args(self) -> &'static [&'static str] {
        match self {
            RemoteCommand::Fetch => &["--prune"],
            RemoteCommand::Pull | RemoteCommand::Push => &[],
        }
    }

    /// The Transfers row's verb: `Fetching`, `Pulling` or `Pushing`.
    pub fn progress_verb(self) -> &'static str {
        match self {
            RemoteCommand::Fetch => "Fetching",
            RemoteCommand::Pull => "Pulling",
            RemoteCommand::Push => "Pushing",
        }
    }
}

/// Runs `command` in the repository whose root is `root`.
pub fn remote_in_root(
    git: &Git,
    cache: &StatusCache,
    root: &Path,
    command: RemoteCommand,
    cancel: Option<CancelCheck>,
) -> Result<GitCommandResult, String> {
    let _mutation = Mutation::begin(cache, root);
    git.run_remote(root, command.subcommand(), command.args(), cancel)
}

pub fn fetch(
    git: &Git,
    cache: &StatusCache,
    path: &str,
    cancel: Option<CancelCheck>,
) -> Result<GitCommandResult, String> {
    let root = repository_root(git, path)?;
    remote_in_root(git, cache, &root, RemoteCommand::Fetch, cancel)
}

pub fn pull(
    git: &Git,
    cache: &StatusCache,
    path: &str,
    cancel: Option<CancelCheck>,
) -> Result<GitCommandResult, String> {
    let root = repository_root(git, path)?;
    remote_in_root(git, cache, &root, RemoteCommand::Pull, cancel)
}

pub fn push(
    git: &Git,
    cache: &StatusCache,
    path: &str,
    cancel: Option<CancelCheck>,
) -> Result<GitCommandResult, String> {
    let root = repository_root(git, path)?;
    remote_in_root(git, cache, &root, RemoteCommand::Push, cancel)
}

fn combine_results(
    command: &str,
    summary: &str,
    results: Vec<GitCommandResult>,
) -> GitCommandResult {
    GitCommandResult {
        command: command.to_string(),
        exit_code: results.last().map(|result| result.exit_code).unwrap_or(0),
        stdout: results
            .iter()
            .map(|result| result.stdout.trim())
            .filter(|value| !value.is_empty())
            .collect::<Vec<_>>()
            .join("\n"),
        stderr: results
            .iter()
            .map(|result| result.stderr.trim())
            .filter(|value| !value.is_empty())
            .collect::<Vec<_>>()
            .join("\n"),
        summary: summary.to_string(),
    }
}
