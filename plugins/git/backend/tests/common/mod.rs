//! Real-git test repositories, isolated from the developer's Git config.
//!
//! Every git call here, and every `Git` under test, runs with
//! `GIT_CONFIG_GLOBAL=<temp>/gitconfig` (an empty file) and
//! `GIT_CONFIG_NOSYSTEM=1`, so a global `core.hooksPath`, `commit.gpgsign` or
//! `init.defaultBranch` can never run, prompt or change a result. Repositories
//! are created with `git init -b main` and get their identity per repository.
//! `GIT_CEILING_DIRECTORIES` is the test's temp folder, so neither git nor the
//! marker walk ever finds a repository above it.

#![allow(dead_code)]

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::Command;

use sumafile_git_backend::git::{find_on_path, Git};
use tempfile::TempDir;

pub struct TestRepo {
    /// Owns every folder of the test: `repo`, `origin.git`, `peer`, the
    /// data folder and the gitconfig.
    pub temp: TempDir,
    /// The work tree under test.
    pub root: PathBuf,
    /// The backend's data folder.
    pub data: PathBuf,
    gitconfig: PathBuf,
}

impl TestRepo {
    /// An empty repository on `main` with its own identity.
    pub fn new() -> TestRepo {
        let temp = tempfile::tempdir().unwrap();
        let gitconfig = temp.path().join("gitconfig");
        std::fs::write(&gitconfig, "").unwrap();
        let data = temp.path().join("data");
        std::fs::create_dir_all(&data).unwrap();
        let root = temp.path().join("repo");
        std::fs::create_dir_all(&root).unwrap();
        let repo = TestRepo {
            temp,
            root,
            data,
            gitconfig,
        };
        repo.git_in(&repo.root, &["init", "-b", "main"]);
        repo.identity(&repo.root);
        repo
    }

    /// A repository whose `main` tracks `origin/main` of a bare `origin.git`,
    /// with one commit pushed.
    pub fn with_remote() -> TestRepo {
        let repo = TestRepo::new();
        let origin = repo.origin();
        std::fs::create_dir_all(&origin).unwrap();
        repo.git_in(&origin, &["init", "--bare", "-b", "main"]);
        repo.write("README.md", "hello\n");
        repo.git(&["add", "--", "README.md"]);
        repo.git(&["commit", "-m", "initial"]);
        let origin_text = origin.to_string_lossy().to_string();
        repo.git(&["remote", "add", "origin", &origin_text]);
        repo.git(&["push", "-u", "origin", "main"]);
        repo
    }

    pub fn origin(&self) -> PathBuf {
        self.temp.path().join("origin.git")
    }

    /// A second clone of `origin.git`, with its own identity.
    pub fn peer(&self) -> PathBuf {
        let peer = self.temp.path().join("peer");
        let origin = self.origin().to_string_lossy().to_string();
        let peer_text = peer.to_string_lossy().to_string();
        self.git_in(self.temp.path(), &["clone", &origin, &peer_text]);
        self.identity(&peer);
        peer
    }

    fn identity(&self, dir: &Path) {
        self.git_in(dir, &["config", "user.name", "SumaFile Test"]);
        self.git_in(dir, &["config", "user.email", "test@sumafile.invalid"]);
    }

    /// The isolation every git process of the test gets.
    pub fn env(&self) -> Vec<(OsString, OsString)> {
        vec![
            (
                OsString::from("GIT_CONFIG_GLOBAL"),
                self.gitconfig.clone().into_os_string(),
            ),
            (OsString::from("GIT_CONFIG_NOSYSTEM"), OsString::from("1")),
            (
                OsString::from("GIT_CEILING_DIRECTORIES"),
                self.temp.path().as_os_str().to_os_string(),
            ),
        ]
    }

    /// The `Git` under test: git from PATH, hooks off inside the data folder
    /// and this test's isolation.
    pub fn git_under_test(&self) -> Git {
        Git::discover(&self.data).with_env(self.env())
    }

    /// Runs git in the work tree and returns its stdout; panics on failure.
    pub fn git(&self, args: &[&str]) -> String {
        self.git_in(&self.root, args)
    }

    /// Runs git in `dir` and returns its stdout; panics on failure.
    pub fn git_in(&self, dir: &Path, args: &[&str]) -> String {
        let output = self.command_in(dir, args).output().unwrap();
        assert!(
            output.status.success(),
            "git {args:?} in {} failed: {}",
            dir.display(),
            String::from_utf8_lossy(&output.stderr)
        );
        String::from_utf8_lossy(&output.stdout).to_string()
    }

    pub fn command_in(&self, dir: &Path, args: &[&str]) -> Command {
        let exe = find_on_path(std::env::var_os("PATH").as_deref()).expect("git on PATH");
        let mut command = Command::new(exe);
        command
            .args([
                "-c",
                "init.defaultBranch=main",
                "-c",
                "commit.gpgsign=false",
                "-c",
                "core.autocrlf=false",
            ])
            .arg("-C")
            .arg(dir)
            .args(args)
            .env("GIT_TERMINAL_PROMPT", "0");
        for (key, value) in self.env() {
            command.env(key, value);
        }
        command
    }

    /// Writes `relative` (with `/` separators) under the work tree.
    pub fn write(&self, relative: &str, contents: &str) -> PathBuf {
        write_under(&self.root, relative, contents)
    }

    /// `<root>` joined with `relative`, in native separators.
    pub fn path(&self, relative: &str) -> PathBuf {
        native_join(&self.root, relative)
    }

    pub fn commit_all(&self, message: &str) {
        self.git(&["add", "-A"]);
        self.git(&["commit", "-m", message]);
    }
}

pub fn native_join(base: &Path, relative: &str) -> PathBuf {
    let mut path = base.to_path_buf();
    for part in relative.split('/') {
        path.push(part);
    }
    path
}

pub fn write_under(base: &Path, relative: &str, contents: &str) -> PathBuf {
    let path = native_join(base, relative);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).unwrap();
    }
    std::fs::write(&path, contents).unwrap();
    path
}

pub fn text(path: &Path) -> String {
    path.to_string_lossy().to_string()
}
