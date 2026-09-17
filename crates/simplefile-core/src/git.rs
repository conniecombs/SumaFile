use crate::models::{FileEntry, GitCommandResult, GitFileStatus, GitRepositoryStatus, GitStatus};
use crate::utils::{get_file_entry, hidden_command, validate_existing_path_no_resolve};
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

struct RepoContext {
    root: PathBuf,
}

pub fn get_git_status(path: String) -> Result<GitStatus, String> {
    let status = get_git_repository_status(path)?;
    Ok(GitStatus {
        is_repo: status.is_repo,
        branch: status.branch,
        modified: status.unstaged,
        staged: status.staged,
        untracked: status.untracked,
        ahead: status.ahead,
        behind: status.behind,
    })
}

pub fn get_git_repository_status(path: String) -> Result<GitRepositoryStatus, String> {
    let path = validate_existing_path_no_resolve(&path)?;
    let Some(repo) = try_repo_context(&path)? else {
        return Ok(not_a_repo_status());
    };

    let branch = git_output(&repo.root, &["branch", "--show-current"])
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty());
    let head = git_output(&repo.root, &["rev-parse", "--short", "HEAD"])
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty());
    let upstream = git_output(
        &repo.root,
        &[
            "rev-parse",
            "--abbrev-ref",
            "--symbolic-full-name",
            "@{upstream}",
        ],
    )
    .ok()
    .map(|value| value.trim().to_string())
    .filter(|value| !value.is_empty());

    let (ahead, behind) = ahead_behind(&repo.root);
    let changes = git_output(
        &repo.root,
        &["status", "--porcelain=v1", "-z", "--untracked-files=normal"],
    )
    .map(|output| parse_porcelain_status(&repo.root, &output))
    .unwrap_or_default();

    Ok(GitRepositoryStatus {
        is_repo: true,
        root: Some(repo.root.to_string_lossy().to_string()),
        branch,
        upstream,
        head,
        ahead,
        behind,
        staged: changes.iter().filter(|change| change.staged).count() as u32,
        unstaged: changes.iter().filter(|change| change.unstaged).count() as u32,
        untracked: changes.iter().filter(|change| change.untracked).count() as u32,
        conflicted: changes.iter().filter(|change| change.conflicted).count() as u32,
        changes,
    })
}

pub fn get_git_file_statuses(path: String) -> Result<Vec<FileEntry>, String> {
    let status = get_git_repository_status(path)?;
    if !status.is_repo {
        return Ok(Vec::new());
    }

    let mut entries = Vec::with_capacity(status.changes.len());
    for change in status.changes {
        let full_path = PathBuf::from(&change.absolute_path);
        let mut entry = get_file_entry(&full_path)
            .unwrap_or_else(|| deleted_or_missing_entry(&full_path, &change.path));
        entry.git_status = Some(change.status);
        entries.push(entry);
    }

    Ok(entries)
}

pub fn git_stage_paths(path: String, paths: Vec<String>) -> Result<GitCommandResult, String> {
    let repo = repo_context_from_string(&path)?;
    let pathspecs = pathspecs_under_root(&repo.root, &paths)?;
    run_git_path_command(
        &repo.root,
        "stage",
        &[OsString::from("add"), OsString::from("--")],
        &pathspecs,
        "Staged selected Git path(s).",
    )
}

pub fn git_unstage_paths(path: String, paths: Vec<String>) -> Result<GitCommandResult, String> {
    let repo = repo_context_from_string(&path)?;
    let pathspecs = pathspecs_under_root(&repo.root, &paths)?;
    run_git_path_command(
        &repo.root,
        "unstage",
        &[
            OsString::from("restore"),
            OsString::from("--staged"),
            OsString::from("--"),
        ],
        &pathspecs,
        "Unstaged selected Git path(s).",
    )
}

pub fn git_discard_paths(path: String, paths: Vec<String>) -> Result<GitCommandResult, String> {
    let repo = repo_context_from_string(&path)?;
    let pathspecs = pathspecs_under_root(&repo.root, &paths)?;
    let status = get_git_repository_status(repo.root.to_string_lossy().to_string())?;
    let mut tracked = Vec::new();
    let mut cleanable = Vec::new();

    for pathspec in &pathspecs {
        let change = status
            .changes
            .iter()
            .find(|change| paths_equal(&change.path, pathspec));
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
        results.push(run_git_path_command(
            &repo.root,
            "discard",
            &[
                OsString::from("restore"),
                OsString::from("--staged"),
                OsString::from("--"),
            ],
            &tracked,
            "Unstaged selected Git path(s).",
        )?);

        let restorable: Vec<String> = tracked
            .iter()
            .filter(|pathspec| {
                status
                    .changes
                    .iter()
                    .find(|change| paths_equal(&change.path, pathspec))
                    .map(|change| change.index_status != "A")
                    .unwrap_or(true)
            })
            .cloned()
            .collect();
        if !restorable.is_empty() {
            results.push(run_git_path_command(
                &repo.root,
                "discard",
                &[
                    OsString::from("restore"),
                    OsString::from("--worktree"),
                    OsString::from("--"),
                ],
                &restorable,
                "Discarded selected Git working tree change(s).",
            )?);
        }
    }

    if !cleanable.is_empty() {
        results.push(run_git_path_command(
            &repo.root,
            "discard",
            &[
                OsString::from("clean"),
                OsString::from("-fd"),
                OsString::from("--"),
            ],
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

pub fn git_diff_path(path: String, file_path: String) -> Result<String, String> {
    let repo = repo_context_from_string(&path)?;
    let pathspec = repo_relative_path(&repo.root, &file_path)?;
    let status = get_git_repository_status(repo.root.to_string_lossy().to_string())?;
    if status
        .changes
        .iter()
        .any(|change| paths_equal(&change.path, &pathspec) && change.untracked)
    {
        return Ok(
            "No Git diff is available for untracked files until they are staged.".to_string(),
        );
    }

    let staged = git_output_os(
        &repo.root,
        &[
            OsString::from("diff"),
            OsString::from("--cached"),
            OsString::from("--"),
            OsString::from(&pathspec),
        ],
    )?;
    let working = git_output_os(
        &repo.root,
        &[
            OsString::from("diff"),
            OsString::from("--"),
            OsString::from(&pathspec),
        ],
    )?;

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

pub fn git_commit(path: String, message: String) -> Result<GitCommandResult, String> {
    let repo = repo_context_from_string(&path)?;
    let message = message.trim();
    if message.is_empty() {
        return Err("Commit message is required.".to_string());
    }

    run_git_repo_command(
        &repo.root,
        "commit",
        &[
            OsString::from("commit"),
            OsString::from("-m"),
            OsString::from(message),
        ],
        "Commit completed.",
    )
}

pub fn git_fetch(path: String) -> Result<GitCommandResult, String> {
    run_git_remote_command(&path, "fetch", &["--prune"])
}

pub fn git_pull(path: String) -> Result<GitCommandResult, String> {
    run_git_remote_command(&path, "pull", &[])
}

pub fn git_push(path: String) -> Result<GitCommandResult, String> {
    run_git_remote_command(&path, "push", &[])
}

fn not_a_repo_status() -> GitRepositoryStatus {
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

fn repo_context_from_string(path: &str) -> Result<RepoContext, String> {
    let path = validate_existing_path_no_resolve(path)?;
    try_repo_context(&path)?
        .ok_or_else(|| "The current path is not inside a Git repository.".to_string())
}

fn try_repo_context(path: &Path) -> Result<Option<RepoContext>, String> {
    if git_output(path, &["rev-parse", "--is-inside-work-tree"]).is_err() {
        return Ok(None);
    }

    let root = git_output(path, &["rev-parse", "--show-toplevel"])?
        .trim()
        .to_string();
    if root.is_empty() {
        return Ok(None);
    }

    Ok(Some(RepoContext {
        root: PathBuf::from(root),
    }))
}

fn ahead_behind(root: &Path) -> (u32, u32) {
    git_output(
        root,
        &["rev-list", "--left-right", "--count", "HEAD...@{upstream}"],
    )
    .ok()
    .and_then(|counts| {
        let mut parts = counts.split_whitespace();
        let ahead = parts.next()?.parse().ok()?;
        let behind = parts.next()?.parse().ok()?;
        Some((ahead, behind))
    })
    .unwrap_or((0, 0))
}

fn parse_porcelain_status(root: &Path, output: &str) -> Vec<GitFileStatus> {
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
        let path = record[3..].to_string();
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
    let absolute_path = root.join(Path::new(&path)).to_string_lossy().to_string();

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

fn status_label(index_status: char, worktree_status: char) -> &'static str {
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

fn git_output(path: &Path, args: &[&str]) -> Result<String, String> {
    git_output_os(path, &args.iter().map(OsString::from).collect::<Vec<_>>())
}

fn git_output_os(path: &Path, args: &[OsString]) -> Result<String, String> {
    let mut command = hidden_command("git");
    command.arg("-C").arg(path).args(args);
    command_output(command).map(|result| result.stdout)
}

fn run_git_path_command(
    root: &Path,
    command_name: &str,
    prefix_args: &[OsString],
    pathspecs: &[String],
    summary: &str,
) -> Result<GitCommandResult, String> {
    let mut args = Vec::with_capacity(prefix_args.len() + pathspecs.len());
    args.extend(prefix_args.iter().cloned());
    args.extend(pathspecs.iter().map(OsString::from));
    let command = git_root_command(root, &args, true);
    command_output_with_name(command, command_name, summary)
}

fn run_git_repo_command(
    root: &Path,
    command_name: &str,
    args: &[OsString],
    summary: &str,
) -> Result<GitCommandResult, String> {
    let command = git_root_command(root, args, false);
    command_output_with_name(command, command_name, summary)
}

fn git_root_command(root: &Path, args: &[OsString], literal_pathspecs: bool) -> Command {
    let mut command = hidden_command("git");
    if literal_pathspecs {
        command.arg("--literal-pathspecs");
    }
    command.arg("-C").arg(root).args(args);
    command
}

fn command_output(command: Command) -> Result<GitCommandResult, String> {
    command_output_with_name(command, "command", "Git command completed.")
}

fn command_output_with_name(
    mut command: Command,
    command_name: &str,
    summary: &str,
) -> Result<GitCommandResult, String> {
    command
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GCM_INTERACTIVE", "Never")
        .stdin(Stdio::null());
    let output = command.output().map_err(|error| error.to_string())?;
    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    let stderr = String::from_utf8_lossy(&output.stderr).to_string();
    let exit_code = output.status.code().unwrap_or(-1);
    if output.status.success() {
        Ok(GitCommandResult {
            command: format!("git {command_name}"),
            exit_code,
            stdout,
            stderr,
            summary: summary.to_string(),
        })
    } else {
        let message = first_non_empty([
            stderr.trim(),
            stdout.trim(),
            &format!("git {command_name} exited with code {exit_code}"),
        ]);
        Err(message)
    }
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

fn first_non_empty(values: [&str; 3]) -> String {
    values
        .iter()
        .find(|value| !value.trim().is_empty())
        .map(|value| value.to_string())
        .unwrap_or_else(|| "Git command failed.".to_string())
}

fn pathspecs_under_root(root: &Path, paths: &[String]) -> Result<Vec<String>, String> {
    if paths.is_empty() {
        return Err("Select at least one Git path.".to_string());
    }

    let mut pathspecs = Vec::with_capacity(paths.len());
    for path in paths {
        let pathspec = repo_relative_path(root, path)?;
        if pathspec.is_empty() || pathspec == "." {
            return Err(
                "Refusing to run a file-level Git action against the repository root.".to_string(),
            );
        }

        pathspecs.push(pathspec);
    }

    pathspecs.sort_unstable();
    pathspecs.dedup();
    Ok(pathspecs)
}

fn repo_relative_path(root: &Path, value: &str) -> Result<String, String> {
    let value = value.trim();
    if value.is_empty() {
        return Err("Git path cannot be empty.".to_string());
    }

    let input = Path::new(value);
    let absolute = if input.is_absolute() {
        input.to_path_buf()
    } else {
        root.join(input)
    };

    if let Ok(relative) = absolute.strip_prefix(root) {
        return Ok(path_to_git_relative(relative));
    }

    let root_text = comparable_path(root);
    let absolute_text = comparable_path(&absolute);
    let prefix = if root_text.ends_with('/') {
        root_text
    } else {
        format!("{root_text}/")
    };
    if !absolute_text.starts_with(&prefix) {
        return Err(format!(
            "Git path is outside the repository: {}",
            absolute.to_string_lossy()
        ));
    }

    Ok(absolute_text[prefix.len()..].to_string())
}

fn path_to_git_relative(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

fn comparable_path(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "/")
        .trim_end_matches('/')
        .to_lowercase()
}

fn paths_equal(left: &str, right: &str) -> bool {
    left.replace('\\', "/")
        .eq_ignore_ascii_case(&right.replace('\\', "/"))
}

fn deleted_or_missing_entry(path: &Path, relative: &str) -> FileEntry {
    let path_string = path.to_string_lossy().to_string();
    let name = Path::new(relative)
        .file_name()
        .map(|value| value.to_string_lossy().to_string())
        .unwrap_or_else(|| relative.to_string());
    let extension = Path::new(relative)
        .extension()
        .map(|value| value.to_string_lossy().to_string())
        .unwrap_or_default();

    FileEntry {
        name,
        path: path_string,
        is_dir: false,
        is_symlink: false,
        is_hidden: false,
        is_system: false,
        size: 0,
        modified: "-".to_string(),
        extension,
        permissions: None,
        symlink_target: None,
        git_status: None,
    }
}

fn git_remote_args(path: &Path, subcommand: &str, subcommand_args: &[&str]) -> Vec<OsString> {
    let mut args = vec![OsString::from("-C"), path.as_os_str().to_os_string()];
    args.push(OsString::from(subcommand));
    args.extend(subcommand_args.iter().map(OsString::from));
    args
}

fn run_git_remote_command(
    path: &str,
    subcommand: &str,
    subcommand_args: &[&str],
) -> Result<GitCommandResult, String> {
    let path = validate_existing_path_no_resolve(path)?;
    let mut command = hidden_command("git");
    command.args(git_remote_args(&path, subcommand, subcommand_args));
    command_output_with_name(command, subcommand, &format!("Git {subcommand} completed."))
}

#[cfg(test)]
mod tests {
    use super::{
        git_remote_args, git_root_command, parse_porcelain_status, repo_relative_path, status_label,
    };
    use std::ffi::OsString;
    use std::path::Path;

    fn to_strings(args: Vec<std::ffi::OsString>) -> Vec<String> {
        args.into_iter()
            .map(|arg| arg.to_string_lossy().to_string())
            .collect()
    }

    #[test]
    fn git_file_command_enables_literal_pathspecs() {
        let command = git_root_command(
            Path::new("repo"),
            &[
                OsString::from("add"),
                OsString::from("--"),
                OsString::from("a[1].txt"),
            ],
            true,
        );
        let args = command
            .get_args()
            .map(|arg| arg.to_string_lossy().to_string())
            .collect::<Vec<_>>();

        assert_eq!(args[0], "--literal-pathspecs");
        assert_eq!(args[1], "-C");
        assert_eq!(args[2], "repo");
        assert_eq!(args[3], "add");
    }

    #[test]
    fn git_remote_args_do_not_add_auth_config() {
        let args = to_strings(git_remote_args(Path::new("repo"), "push", &[]));

        assert_eq!(args, vec!["-C", "repo", "push"]);
    }

    #[test]
    fn git_remote_args_append_subcommand_args_after_subcommand() {
        let args = to_strings(git_remote_args(Path::new("repo"), "fetch", &["--prune"]));

        assert_eq!(args, vec!["-C", "repo", "fetch", "--prune"]);
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
    fn repo_relative_path_rejects_paths_outside_root() {
        let root = Path::new("C:/repo");

        assert_eq!(
            repo_relative_path(root, "src/lib.rs").unwrap(),
            "src/lib.rs"
        );
        assert!(repo_relative_path(root, "C:/other/lib.rs").is_err());
    }
}
