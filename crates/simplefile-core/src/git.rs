use crate::models::{FileEntry, GitStatus};
use crate::settings_store::get_db_setting;
use crate::utils::{get_file_entry, hidden_command, validate_existing_path_no_resolve};
use std::ffi::OsString;
use std::path::Path;
use std::process::Command;

pub fn get_git_status(path: String) -> Result<GitStatus, String> {
    let path = validate_existing_path_no_resolve(&path)?;

    if !is_git_repo(&path) {
        return Ok(GitStatus {
            is_repo: false,
            branch: None,
            modified: 0,
            staged: 0,
            untracked: 0,
            ahead: 0,
            behind: 0,
        });
    }

    let branch = git_output(&path, &["branch", "--show-current"])
        .ok()
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty());

    let mut modified = 0u32;
    let mut staged = 0u32;
    let mut untracked = 0u32;
    if let Ok(status) = git_output(&path, &["status", "--porcelain"]) {
        for line in status.lines() {
            if line.len() < 2 {
                continue;
            }
            let mut chars = line.chars();
            let index_status = chars.next().unwrap_or(' ');
            let worktree_status = chars.next().unwrap_or(' ');
            if index_status != ' ' && index_status != '?' {
                staged += 1;
            }
            if worktree_status != ' ' && worktree_status != '?' {
                modified += 1;
            }
            if index_status == '?' && worktree_status == '?' {
                untracked += 1;
            }
        }
    }

    let (mut ahead, mut behind) = (0u32, 0u32);
    if let Ok(counts) = git_output(
        &path,
        &["rev-list", "--left-right", "--count", "HEAD...@{upstream}"],
    ) {
        let parts: Vec<&str> = counts.split_whitespace().collect();
        if parts.len() == 2 {
            ahead = parts[0].parse().unwrap_or(0);
            behind = parts[1].parse().unwrap_or(0);
        }
    }

    Ok(GitStatus {
        is_repo: true,
        branch,
        modified,
        staged,
        untracked,
        ahead,
        behind,
    })
}

pub fn get_git_file_statuses(path: String) -> Result<Vec<FileEntry>, String> {
    let path = validate_existing_path_no_resolve(&path)?;
    let output = match git_output(&path, &["status", "--porcelain"]) {
        Ok(output) => output,
        Err(_) => return Ok(Vec::new()),
    };

    let mut entries = Vec::new();
    for line in output.lines() {
        if line.len() < 3 {
            continue;
        }
        let xy = &line[0..2];
        let relative = porcelain_path(line);
        if relative.trim().is_empty() {
            continue;
        }

        let full_path = path.join(relative);
        let mut entry = get_file_entry(&full_path)
            .unwrap_or_else(|| deleted_or_missing_entry(&full_path, relative));
        entry.git_status = Some(status_label(xy).to_string());
        entries.push(entry);
    }

    Ok(entries)
}

pub fn git_pull(path: String) -> Result<String, String> {
    let token = get_git_credentials();
    run_git_remote_command(&path, "pull", token.as_deref())
}

pub fn git_push(path: String) -> Result<String, String> {
    let token = get_git_credentials();
    run_git_remote_command(&path, "push", token.as_deref())
}

fn is_git_repo(path: &Path) -> bool {
    git_output(path, &["rev-parse", "--git-dir"]).is_ok()
}

fn git_output(path: &Path, args: &[&str]) -> Result<String, String> {
    let mut command = hidden_command("git");
    command.arg("-C").arg(path).args(args);
    command_output(command)
}

fn command_output(mut command: Command) -> Result<String, String> {
    let output = command.output().map_err(|error| error.to_string())?;
    if output.status.success() {
        Ok(String::from_utf8_lossy(&output.stdout).to_string())
    } else {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        Err(if stderr.is_empty() {
            format!(
                "git exited with code {}",
                output.status.code().unwrap_or(-1)
            )
        } else {
            stderr
        })
    }
}

fn porcelain_path(line: &str) -> &str {
    let path = line[3..].trim();
    if let Some((_, new_path)) = path.split_once(" -> ") {
        new_path.trim_matches('"')
    } else {
        path.trim_matches('"')
    }
}

fn status_label(xy: &str) -> &'static str {
    if xy.contains('?') {
        "untracked"
    } else if xy.starts_with('A') || xy.ends_with('A') {
        "added"
    } else if xy.starts_with('D') || xy.ends_with('D') {
        "deleted"
    } else if xy.starts_with('M') || xy.ends_with('M') {
        "modified"
    } else if xy.starts_with('R') || xy.ends_with('R') {
        "renamed"
    } else {
        "modified"
    }
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
        size: 0,
        modified: "-".to_string(),
        extension,
        permissions: None,
        symlink_target: None,
        git_status: None,
    }
}

fn get_git_credentials() -> Option<String> {
    get_db_setting("github_token".to_string())
        .ok()
        .flatten()
        .filter(|token| !token.trim().is_empty())
}

fn git_remote_args(path: &Path, subcommand: &str, token: Option<&str>) -> Vec<OsString> {
    use base64::{engine::general_purpose, Engine as _};

    let mut args = vec![OsString::from("-C"), path.as_os_str().to_os_string()];

    if let Some(token) = token {
        let auth = general_purpose::STANDARD.encode(format!("token:{token}"));
        args.push(OsString::from("-c"));
        args.push(OsString::from(format!(
            "http.extraHeader=AUTHORIZATION: basic {auth}"
        )));
    }

    args.push(OsString::from(subcommand));
    args
}

fn run_git_remote_command(
    path: &str,
    subcommand: &str,
    token: Option<&str>,
) -> Result<String, String> {
    let path = validate_existing_path_no_resolve(path)?;
    let mut command = hidden_command("git");
    command.args(git_remote_args(&path, subcommand, token));
    command_output(command)
}

#[cfg(test)]
mod tests {
    use super::{git_remote_args, status_label};
    use std::path::Path;

    fn to_strings(args: Vec<std::ffi::OsString>) -> Vec<String> {
        args.into_iter()
            .map(|arg| arg.to_string_lossy().to_string())
            .collect()
    }

    #[test]
    fn git_remote_args_put_auth_header_before_subcommand() {
        let args = to_strings(git_remote_args(
            Path::new("repo"),
            "pull",
            Some("ghp_example"),
        ));

        assert_eq!(args[0], "-C");
        assert_eq!(args[1], "repo");
        assert_eq!(args[2], "-c");
        assert!(args[3].starts_with("http.extraHeader=AUTHORIZATION: basic "));
        assert_eq!(args[4], "pull");
        assert!(
            args.iter().position(|arg| arg == "-c").unwrap()
                < args.iter().position(|arg| arg == "pull").unwrap()
        );
    }

    #[test]
    fn git_remote_args_without_token_do_not_add_auth_config() {
        let args = to_strings(git_remote_args(Path::new("repo"), "push", None));

        assert_eq!(args, vec!["-C", "repo", "push"]);
    }

    #[test]
    fn status_label_matches_porcelain_codes() {
        assert_eq!(status_label("??"), "untracked");
        assert_eq!(status_label(" M"), "modified");
        assert_eq!(status_label("A "), "added");
        assert_eq!(status_label(" D"), "deleted");
        assert_eq!(status_label("R "), "renamed");
    }
}
