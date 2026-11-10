//! Which git runs, and with what arguments.
//!
//! `git.exe` is resolved once, from absolute `PATH` entries only, so a
//! relative entry can never pick up a `git.exe` from whatever folder the
//! process happens to run in. Without one every call fails with
//! [`GIT_NOT_FOUND`] and spawns nothing.
//!
//! Read-only queries (status, rev-parse, diff) run against whatever
//! repository the user happens to browse. Such a repository's own config can
//! name executables (`core.fsmonitor`, `core.hooksPath`), so a listing must
//! never let it run them: queries pass `-c core.fsmonitor=false` and point
//! `core.hooksPath` at a randomly named folder inside the data folder that is
//! never created, so no hook ever runs. They also pass `--no-optional-locks`,
//! so browsing never takes the index lock a concurrent `git commit` needs.
//! Hooks stay on for the user's own explicit actions (commit, push).

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use crate::models::GitCommandResult;
use crate::run::{run_command, CancelCheck, RunOptions, GIT_REMOTE_TIMEOUT};
use crate::GIT_NOT_FOUND;

#[cfg(windows)]
const GIT_EXE: &str = "git.exe";
#[cfg(not(windows))]
const GIT_EXE: &str = "git";

pub struct Git {
    exe: Option<PathBuf>,
    hooks_off: PathBuf,
    env: Vec<(OsString, OsString)>,
    spawns: AtomicU64,
}

impl Git {
    /// Git from `PATH`, with hooks pointed at
    /// `<data_dir>\no-hooks-<16 random hex>`, which is never created.
    pub fn discover(data_dir: &Path) -> Git {
        let exe = find_on_path(std::env::var_os("PATH").as_deref());
        Git::with_executable(exe, data_dir.join(format!("no-hooks-{}", random_hex())))
    }

    pub fn with_executable(exe: Option<PathBuf>, hooks_off: PathBuf) -> Git {
        Git {
            exe,
            hooks_off,
            env: Vec::new(),
            spawns: AtomicU64::new(0),
        }
    }

    /// Environment variables every git process gets on top of the
    /// backend's own. [`Git::env_var`] reads them first, so tests can set
    /// `GIT_CEILING_DIRECTORIES` without changing the process environment.
    pub fn with_env(mut self, env: Vec<(OsString, OsString)>) -> Git {
        self.env = env;
        self
    }

    /// How many git processes this `Git` started.
    pub fn spawn_count(&self) -> u64 {
        self.spawns.load(Ordering::Relaxed)
    }

    pub fn executable(&self) -> Option<&Path> {
        self.exe.as_deref()
    }

    /// `name` from the overrides of [`Git::with_env`], else from the process
    /// environment.
    pub fn env_var(&self, name: &str) -> Option<OsString> {
        self.env
            .iter()
            .rev()
            .find(|(key, _)| key.as_os_str() == OsStr::new(name))
            .map(|(_, value)| value.clone())
            .or_else(|| std::env::var_os(name))
    }

    /// The arguments of a read-only query in `path`.
    pub fn query_args(&self, path: &Path, args: &[OsString]) -> Vec<OsString> {
        query_args(&self.hooks_off, path, args)
    }

    /// Runs a read-only query in `path` and returns its stdout.
    pub fn query(&self, path: &Path, args: &[&str]) -> Result<String, String> {
        let args: Vec<OsString> = args.iter().map(OsString::from).collect();
        self.query_os(path, &args)
    }

    pub fn query_os(&self, path: &Path, args: &[OsString]) -> Result<String, String> {
        self.run(
            self.query_args(path, args),
            &RunOptions::local("command"),
            "Git command completed.",
        )
        .map(|result| result.stdout)
    }

    /// Runs a command that changes the repository at `root`, hooks on.
    pub fn run_in_root(
        &self,
        root: &Path,
        args: &[OsString],
        literal_pathspecs: bool,
        options: &RunOptions,
        summary: &str,
    ) -> Result<GitCommandResult, String> {
        self.run(root_args(root, args, literal_pathspecs), options, summary)
    }

    /// Runs `fetch`, `pull` or `push` in `root` with nothing added to git's
    /// own configuration.
    pub fn run_remote(
        &self,
        root: &Path,
        subcommand: &str,
        subcommand_args: &[&str],
        cancel: Option<CancelCheck>,
    ) -> Result<GitCommandResult, String> {
        self.run(
            git_remote_args(root, subcommand, subcommand_args),
            &RunOptions {
                name: subcommand.to_string(),
                timeout: GIT_REMOTE_TIMEOUT,
                cancel,
            },
            &format!("Git {subcommand} completed."),
        )
    }

    fn run(
        &self,
        args: Vec<OsString>,
        options: &RunOptions,
        summary: &str,
    ) -> Result<GitCommandResult, String> {
        let Some(exe) = &self.exe else {
            return Err(GIT_NOT_FOUND.to_string());
        };
        let mut command = std::process::Command::new(exe);
        command.args(args);
        for (key, value) in &self.env {
            command.env(key, value);
        }
        self.spawns.fetch_add(1, Ordering::Relaxed);
        run_command(command, options, summary)
    }
}

/// The first absolute `PATH` entry that holds git (`git.exe` on Windows).
/// Relative entries and entries without git are skipped.
pub fn find_on_path(path: Option<&OsStr>) -> Option<PathBuf> {
    std::env::split_paths(path?)
        .filter(|entry| entry.is_absolute())
        .map(|entry| entry.join(GIT_EXE))
        .find(|candidate| candidate.is_file())
}

/// `--no-optional-locks --literal-pathspecs -c core.fsmonitor=false
/// -c core.hooksPath=<hooks_off> -C <path> <args>`.
pub fn query_args(hooks_off: &Path, path: &Path, args: &[OsString]) -> Vec<OsString> {
    let mut hooks = OsString::from("core.hooksPath=");
    hooks.push(hooks_off.as_os_str());
    let mut all = vec![
        OsString::from("--no-optional-locks"),
        // Diff and status pathspecs name exact files; `a[1].txt` is not a glob.
        OsString::from("--literal-pathspecs"),
        OsString::from("-c"),
        OsString::from("core.fsmonitor=false"),
        OsString::from("-c"),
        hooks,
        OsString::from("-C"),
        path.as_os_str().to_os_string(),
    ];
    all.extend(args.iter().cloned());
    all
}

/// `[--literal-pathspecs] -c core.fsmonitor=false -C <root> <args>`: hooks
/// stay on.
pub fn root_args(root: &Path, args: &[OsString], literal_pathspecs: bool) -> Vec<OsString> {
    let mut all = Vec::with_capacity(args.len() + 5);
    if literal_pathspecs {
        all.push(OsString::from("--literal-pathspecs"));
    }
    all.push(OsString::from("-c"));
    all.push(OsString::from("core.fsmonitor=false"));
    all.push(OsString::from("-C"));
    all.push(root.as_os_str().to_os_string());
    all.extend(args.iter().cloned());
    all
}

/// `-C <path> <subcommand> <args>`: fetch, pull and push add no
/// configuration, so the user's credential helpers and hooks apply.
pub fn git_remote_args(path: &Path, subcommand: &str, subcommand_args: &[&str]) -> Vec<OsString> {
    let mut args = vec![OsString::from("-C"), path.as_os_str().to_os_string()];
    args.push(OsString::from(subcommand));
    args.extend(subcommand_args.iter().map(OsString::from));
    args
}

fn random_hex() -> String {
    let mut bytes = [0u8; 8];
    if getrandom::fill(&mut bytes).is_err() {
        // Unpredictability only keeps the name from being guessed ahead of
        // time; the folder is never created either way.
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|elapsed| elapsed.as_nanos() as u64)
            .unwrap_or_default();
        bytes = (nanos ^ u64::from(std::process::id()).rotate_left(32)).to_le_bytes();
    }
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::{find_on_path, git_remote_args, query_args, root_args, Git, GIT_EXE};
    use std::ffi::OsString;
    use std::path::{Path, MAIN_SEPARATOR};

    fn to_strings(args: Vec<OsString>) -> Vec<String> {
        args.into_iter()
            .map(|arg| arg.to_string_lossy().to_string())
            .collect()
    }

    #[test]
    fn git_file_command_enables_literal_pathspecs() {
        let args = to_strings(root_args(
            Path::new("repo"),
            &[
                OsString::from("add"),
                OsString::from("--"),
                OsString::from("a[1].txt"),
            ],
            true,
        ));

        assert_eq!(args[0], "--literal-pathspecs");
        // Hardening `-c` pairs may sit between the flag and the repo path.
        let repo_at = args.iter().position(|arg| arg == "-C").expect("-C present");
        assert_eq!(args[repo_at + 1], "repo");
        assert_eq!(args[repo_at + 2], "add");
    }

    #[test]
    fn read_only_queries_disable_repo_controlled_executables() {
        let args = to_strings(query_args(
            Path::new("no-hooks"),
            Path::new("repo"),
            &[OsString::from("status")],
        ));
        let joined = args.join(" ");
        assert!(joined.contains("core.fsmonitor=false"), "{joined}");
        assert!(joined.contains("core.hooksPath="), "{joined}");
        assert!(joined.ends_with("-C repo status"), "{joined}");
    }

    #[test]
    fn mutating_commands_only_disable_fsmonitor() {
        let args = to_strings(root_args(
            Path::new("repo"),
            &[OsString::from("commit")],
            false,
        ));
        assert!(
            args.iter().any(|arg| arg == "core.fsmonitor=false"),
            "{args:?}"
        );
        assert!(
            !args.iter().any(|arg| arg.starts_with("core.hooksPath")),
            "commit must keep the user's hooks: {args:?}"
        );
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
    fn read_only_queries_point_hooks_at_a_missing_folder_inside_the_data_dir() {
        let data = tempfile::tempdir().unwrap();
        let git = Git::discover(data.path());

        let args = to_strings(git.query_args(Path::new("repo"), &[OsString::from("status")]));

        assert_eq!(args[0], "--no-optional-locks", "{args:?}");
        let hooks = args
            .iter()
            .find_map(|arg| arg.strip_prefix("core.hooksPath="))
            .expect("core.hooksPath set");
        let prefix = format!("{}{MAIN_SEPARATOR}no-hooks-", data.path().display());
        let random = hooks
            .strip_prefix(&prefix)
            .unwrap_or_else(|| panic!("{hooks}"));
        assert_eq!(random.len(), 16, "{hooks}");
        assert!(
            random.bytes().all(|byte| byte.is_ascii_hexdigit()),
            "{hooks}"
        );
        assert!(!Path::new(hooks).exists());
        assert_ne!(
            Git::discover(data.path()).query_args(Path::new("repo"), &[]),
            git.query_args(Path::new("repo"), &[]),
            "each backend picks its own name"
        );
    }

    #[test]
    fn find_on_path_skips_relative_and_missing_entries() {
        let empty = tempfile::tempdir().unwrap();
        let first = tempfile::tempdir().unwrap();
        let second = tempfile::tempdir().unwrap();
        std::fs::write(first.path().join(GIT_EXE), b"").unwrap();
        std::fs::write(second.path().join(GIT_EXE), b"").unwrap();
        // A relative entry that does hold git, relative to the current
        // folder: it must never be picked.
        let relative = tempfile::Builder::new()
            .prefix("find-on-path-")
            .tempdir_in(".")
            .unwrap();
        std::fs::write(relative.path().join(GIT_EXE), b"").unwrap();
        let relative_entry = Path::new(relative.path().file_name().unwrap()).to_path_buf();
        assert!(relative_entry.join(GIT_EXE).is_file());
        let missing = empty.path().join("missing");

        let path = std::env::join_paths([
            relative_entry,
            missing,
            empty.path().to_path_buf(),
            first.path().to_path_buf(),
            second.path().to_path_buf(),
        ])
        .unwrap();

        assert_eq!(find_on_path(Some(&path)), Some(first.path().join(GIT_EXE)));
        let only_relative = std::env::join_paths([relative.path().file_name().unwrap()]).unwrap();
        assert_eq!(find_on_path(Some(&only_relative)), None);
        assert_eq!(find_on_path(None), None);
    }
}
