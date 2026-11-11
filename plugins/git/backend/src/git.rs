//! Which git runs, and with what arguments.
//!
//! `git.exe` is resolved once, from absolute `PATH` entries only, so a
//! relative entry can never pick up a `git.exe` from whatever folder the
//! process happens to run in. Without one every call fails with
//! [`GIT_NOT_FOUND`] and spawns nothing.
//!
//! Read-only queries (rev-parse, status, the diff preview) run against
//! whatever repository the user happens to browse, and that repository's
//! config can name programs. A query runs none of them:
//!
//! - every query passes `-c core.fsmonitor=false` and points
//!   `core.hooksPath` at a randomly named folder inside the data folder that
//!   is never created, so no fsmonitor program or hook runs;
//! - status and the diff preview, which read file contents, first list the
//!   configured filter drivers (`git config --list -z`, which runs nothing)
//!   and override each one with an empty `clean`, `smudge` and `process` and
//!   `required=false`, so no clean filter or filter process runs, whichever
//!   attributes file (`.gitattributes`, `.git/info/attributes`,
//!   `core.attributesFile`) names it. The work tree's attributes still apply,
//!   so `text` and `eol` keep their meaning;
//! - status and the diff preview pass `--ignore-submodules=dirty`, so git
//!   starts no `git status` inside a submodule (whose own filters would run);
//! - the diff preview adds `--no-ext-diff --no-textconv --submodule=short`,
//!   so neither `diff.external`, a diff driver's `command` or `textconv`, nor
//!   a diff inside a submodule runs (see `crate::commands::diff`).
//!
//! Queries also pass `--no-optional-locks`, so browsing never takes the
//! index lock a concurrent `git commit` needs. Hooks and filters stay on for
//! the user's own explicit actions (stage, unstage, discard, commit, fetch,
//! pull, push): staging must run clean filters by design.

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use crate::models::GitCommandResult;
use crate::run::{run_command, CancelCheck, RunOptions, GIT_LOCAL_TIMEOUT, GIT_REMOTE_TIMEOUT};
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
        query_args(&self.hooks_off, path, &[], args)
    }

    /// Runs a read-only query in `path` and returns its stdout.
    pub fn query(&self, path: &Path, args: &[&str]) -> Result<String, String> {
        self.query_with(path, &[], args, None)
    }

    /// Runs a read-only query in `path` with the extra `-c` pairs of
    /// `config` (see [`Git::filter_overrides`]); `cancel` stops it with
    /// [`crate::GIT_CANCELLED`].
    pub fn query_with(
        &self,
        path: &Path,
        config: &[OsString],
        args: &[&str],
        cancel: Option<&CancelCheck>,
    ) -> Result<String, String> {
        let args: Vec<OsString> = args.iter().map(OsString::from).collect();
        self.run(
            query_args(&self.hooks_off, path, config, &args),
            &RunOptions {
                name: "command".to_string(),
                timeout: GIT_LOCAL_TIMEOUT,
                cancel: cancel.cloned(),
            },
            "Git command completed.",
        )
        .map(|result| result.stdout)
    }

    /// The `-c` pairs that switch off every filter driver configured for the
    /// repository at `root` (its own config, the user's and the system's,
    /// includes followed). Fails, so the caller reads no file contents, when
    /// the config cannot be listed or names a driver `-c` cannot spell.
    pub fn filter_overrides(
        &self,
        root: &Path,
        cancel: Option<&CancelCheck>,
    ) -> Result<Vec<OsString>, String> {
        let listing = self.query_with(root, &[], &["config", "--list", "-z"], cancel)?;
        Ok(filter_overrides(&filter_drivers(&listing)?))
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

/// The filter driver names of a `git config --list -z` listing, in the
/// order they first appear: `<name>` of every `filter.<name>.<key>`. A name
/// that holds `=` cannot be overridden with `-c` (git splits the pair at the
/// first `=`), so it fails the listing.
pub fn filter_drivers(listing: &str) -> Result<Vec<String>, String> {
    let mut drivers: Vec<String> = Vec::new();
    for entry in listing.split('\0') {
        // `<key>\n<value>`, or a bare `<key>` for a value-less boolean.
        let key = entry.split_once('\n').map_or(entry, |(key, _)| key);
        let Some(rest) = key
            .get(..7)
            .filter(|section| section.eq_ignore_ascii_case("filter."))
            .map(|_| &key[7..])
        else {
            continue;
        };
        let Some((name, _)) = rest.rsplit_once('.') else {
            continue;
        };
        if name.contains('=') {
            return Err(format!(
                "Git filter driver {name:?} cannot be switched off; its file contents are not read."
            ));
        }
        if !drivers.iter().any(|known| known == name) {
            drivers.push(name.to_string());
        }
    }
    Ok(drivers)
}

/// `-c filter.<name>.clean= -c filter.<name>.smudge= -c filter.<name>.process=
/// -c filter.<name>.required=false` for each driver: an empty command runs
/// nothing, and a driver that is not required may run nothing.
pub fn filter_overrides(drivers: &[String]) -> Vec<OsString> {
    let mut config = Vec::with_capacity(drivers.len() * 8);
    for name in drivers {
        for setting in ["clean=", "smudge=", "process=", "required=false"] {
            config.push(OsString::from("-c"));
            config.push(OsString::from(format!("filter.{name}.{setting}")));
        }
    }
    config
}

/// `--no-optional-locks --literal-pathspecs -c core.fsmonitor=false
/// -c core.hooksPath=<hooks_off> <config> -C <path> <args>`.
pub fn query_args(
    hooks_off: &Path,
    path: &Path,
    config: &[OsString],
    args: &[OsString],
) -> Vec<OsString> {
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
    ];
    all.extend(config.iter().cloned());
    all.push(OsString::from("-C"));
    all.push(path.as_os_str().to_os_string());
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
    use super::{
        filter_drivers, filter_overrides, find_on_path, git_remote_args, query_args, root_args,
        Git, GIT_EXE,
    };
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
            &[],
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

    #[test]
    fn filter_drivers_come_from_every_filter_key_of_the_listing() {
        let listing = "core.bare\nfalse\0filter.lfs.clean\ngit-lfs clean -- %f\0\
             filter.lfs.process\ngit-lfs filter-process\0FILTER.Mixed.Case.smudge\ncat\0\
             filter.flag.required\0filters.x.clean\ncat\0filter.noname\ncat\0\
             diff.y.textconv\ncat\0";

        assert_eq!(
            filter_drivers(listing).unwrap(),
            vec!["lfs", "Mixed.Case", "flag"]
        );
        assert!(filter_drivers("").unwrap().is_empty());
        assert!(filter_drivers("filter.a=b.clean\ncat\0").is_err());
    }

    #[test]
    fn filter_overrides_empty_every_command_and_drop_required() {
        let args = to_strings(filter_overrides(&[
            "x".to_string(),
            "Mixed.Case".to_string(),
        ]));

        assert_eq!(
            args.join(" "),
            "-c filter.x.clean= -c filter.x.smudge= -c filter.x.process= \
             -c filter.x.required=false -c filter.Mixed.Case.clean= \
             -c filter.Mixed.Case.smudge= -c filter.Mixed.Case.process= \
             -c filter.Mixed.Case.required=false"
        );
    }

    #[test]
    fn query_config_sits_before_the_repository_path() {
        let args = to_strings(query_args(
            Path::new("no-hooks"),
            Path::new("repo"),
            &filter_overrides(&["x".to_string()]),
            &[OsString::from("status")],
        ));
        let joined = args.join(" ");

        assert!(
            joined.contains("-c filter.x.required=false -C repo status"),
            "{joined}"
        );
        assert!(joined.starts_with("--no-optional-locks"), "{joined}");
    }
}
