//! The Git backend against real git and real repositories: decorations,
//! file commands, commit, fetch, pull and push, cancel and a missing git.
//! See `common` for how each test isolates git from the developer's config.

mod common;

use std::io::Read;
use std::net::TcpListener;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::time::{Duration, Instant};

use common::{text, write_under, TestRepo};
use sumafile_git_backend::commands::{commit, diff, discard, fetch, pull, push, stage, unstage};
use sumafile_git_backend::decorations::folder_decorations;
use sumafile_git_backend::git::Git;
use sumafile_git_backend::models::GitDecorationRow;
use sumafile_git_backend::run::CancelCheck;
use sumafile_git_backend::status::{
    repo_status, StatusCache, STATUS_CACHE_CAPACITY, STATUS_CACHE_TTL,
};
use sumafile_git_backend::{GIT_CANCELLED, GIT_NOT_FOUND};

fn cache() -> StatusCache {
    StatusCache::new(STATUS_CACHE_TTL, STATUS_CACHE_CAPACITY)
}

fn row(path: &Path, status: &str) -> GitDecorationRow {
    GitDecorationRow {
        path: text(path),
        status: status.to_string(),
    }
}

fn sorted(mut rows: Vec<GitDecorationRow>) -> Vec<GitDecorationRow> {
    rows.sort_by(|left, right| left.path.cmp(&right.path));
    rows
}

fn cached_names(repo: &TestRepo) -> Vec<String> {
    let mut names: Vec<String> = repo
        .git(&["diff", "--cached", "--name-only", "-z"])
        .split('\0')
        .filter(|name| !name.is_empty())
        .map(str::to_string)
        .collect();
    names.sort();
    names
}

#[test]
fn decorations_mark_changed_children_with_native_paths() {
    let repo = TestRepo::new();
    repo.write("tracked.txt", "one\n");
    repo.write("staged.txt", "one\n");
    repo.write("café 資料/hello.txt", "one\n");
    repo.commit_all("initial");
    repo.write("tracked.txt", "two\n");
    repo.write("new.txt", "new\n");
    repo.write("newdir/inside.txt", "new\n");
    repo.write("staged.txt", "two\n");
    repo.git(&["add", "--", "staged.txt"]);
    repo.write("café 資料/hello.txt", "two\n");
    let git = repo.git_under_test();
    let cache = cache();

    let top = folder_decorations(&git, &cache, &repo.root);
    assert!(top.in_repository);
    assert_eq!(top.root.as_deref(), Some(text(&repo.root).as_str()));
    let top_rows = top.rows.clone();
    assert_eq!(
        sorted(top.rows),
        sorted(vec![
            row(&repo.path("new.txt"), "untracked"),
            row(&repo.path("newdir"), "untracked"),
            row(&repo.path("staged.txt"), "staged"),
            row(&repo.path("tracked.txt"), "modified"),
        ])
    );

    let unicode = folder_decorations(&git, &cache, &repo.path("café 資料"));
    assert!(unicode.in_repository);
    assert_eq!(unicode.root.as_deref(), Some(text(&repo.root).as_str()));
    assert_eq!(
        unicode.rows,
        vec![row(&repo.path("café 資料/hello.txt"), "modified")]
    );
    for rows in [&top_rows, &unicode.rows] {
        for row in rows {
            assert!(!row.path.ends_with(std::path::MAIN_SEPARATOR), "{row:?}");
            assert!(
                std::path::MAIN_SEPARATOR == '/' || !row.path.contains('/'),
                "{row:?}"
            );
        }
    }
}

#[test]
fn decorations_in_a_subfolder_list_only_its_direct_children() {
    let repo = TestRepo::new();
    repo.write("top.txt", "one\n");
    repo.write("sub/a.txt", "one\n");
    repo.write("sub/deeper/b.txt", "one\n");
    repo.commit_all("initial");
    repo.write("top.txt", "two\n");
    repo.write("sub/a.txt", "two\n");
    repo.write("sub/deeper/b.txt", "two\n");
    repo.write("sub/fresh/c.txt", "new\n");
    let git = repo.git_under_test();

    let sub = folder_decorations(&git, &cache(), &repo.path("sub"));

    assert!(sub.in_repository);
    assert_eq!(
        sorted(sub.rows),
        sorted(vec![
            row(&repo.path("sub/a.txt"), "modified"),
            row(&repo.path("sub/fresh"), "untracked"),
        ])
    );
}

#[test]
fn decorations_outside_any_repository_spawn_no_git() {
    let repo = TestRepo::new();
    let plain = repo.temp.path().join("plain");
    write_under(&plain, "file.txt", "x\n");
    let git = repo.git_under_test();
    let cache = cache();

    let outside = folder_decorations(&git, &cache, &plain);
    let missing = folder_decorations(&git, &cache, &repo.temp.path().join("missing"));

    for decorations in [outside, missing] {
        assert!(!decorations.in_repository);
        assert_eq!(decorations.root, None);
        assert!(decorations.rows.is_empty());
    }
    assert_eq!(git.spawn_count(), 0);
}

#[test]
fn file_commands_treat_names_literally() {
    let repo = TestRepo::new();
    repo.write("base.txt", "base\n");
    repo.commit_all("initial");
    repo.write("literal [brackets] & spaces.txt", "x\n");
    // `[brackets]` as a glob would match this one too.
    repo.write("literal b & spaces.txt", "x\n");
    repo.write("-n.txt", "x\n");
    repo.write("other.txt", "x\n");
    let git = repo.git_under_test();
    let cache = cache();
    let root = text(&repo.root);
    let one = |name: &str| vec![text(&repo.path(name))];

    stage(&git, &cache, &root, &one("literal [brackets] & spaces.txt")).unwrap();
    assert_eq!(cached_names(&repo), vec!["literal [brackets] & spaces.txt"]);
    stage(&git, &cache, &root, &one("-n.txt")).unwrap();
    assert_eq!(
        cached_names(&repo),
        vec!["-n.txt", "literal [brackets] & spaces.txt"]
    );

    // `*.txt` names no file here; as a glob it would stage every `.txt`.
    assert!(stage(&git, &cache, &root, &one("*.txt")).is_err());
    assert_eq!(
        cached_names(&repo),
        vec!["-n.txt", "literal [brackets] & spaces.txt"]
    );

    unstage(&git, &cache, &root, &one("-n.txt")).unwrap();
    assert_eq!(cached_names(&repo), vec!["literal [brackets] & spaces.txt"]);
    unstage(&git, &cache, &root, &one("literal [brackets] & spaces.txt")).unwrap();
    assert!(cached_names(&repo).is_empty());

    // Windows forbids `*` in file names; elsewhere a real `*.txt` stages and
    // unstages alone.
    #[cfg(not(windows))]
    {
        repo.write("*.txt", "x\n");
        stage(&git, &cache, &root, &one("*.txt")).unwrap();
        assert_eq!(cached_names(&repo), vec!["*.txt"]);
        unstage(&git, &cache, &root, &one("*.txt")).unwrap();
        assert!(cached_names(&repo).is_empty());
    }
}

#[test]
fn discard_removes_only_the_selected_untracked_paths() {
    let repo = TestRepo::new();
    repo.write("base.txt", "base\n");
    repo.commit_all("initial");
    repo.write("junk/one.txt", "x\n");
    repo.write("junk/nested/two.txt", "x\n");
    repo.write("keep.txt", "x\n");
    repo.write("base.txt", "changed\n");
    let git = repo.git_under_test();
    let cache = cache();
    let root = text(&repo.root);

    let result = discard(
        &git,
        &cache,
        &root,
        &[text(&repo.path("junk")), text(&repo.path("base.txt"))],
    )
    .unwrap();

    assert_eq!(result.command, "git discard");
    assert!(!repo.path("junk").exists());
    assert!(repo.path("keep.txt").exists());
    assert_eq!(
        std::fs::read_to_string(repo.path("base.txt")).unwrap(),
        "base\n"
    );
    let status = repo_status(&git, &cache, &repo.root).unwrap();
    assert_eq!(status.untracked, 1);
    assert_eq!(status.changes[0].path, "keep.txt");
}

#[test]
fn discard_refuses_the_repository_root() {
    let repo = TestRepo::new();
    let git = repo.git_under_test();
    let root = text(&repo.root);

    let error = discard(&git, &cache(), &root, std::slice::from_ref(&root)).unwrap_err();

    assert_eq!(
        error,
        "Refusing to run a file-level Git action against the repository root."
    );
    let error = discard(&git, &cache(), &root, &[]).unwrap_err();
    assert_eq!(error, "Select at least one Git path.");
}

#[test]
fn diff_joins_staged_and_working_sections() {
    let repo = TestRepo::new();
    repo.write("a.txt", "one\n");
    repo.commit_all("initial");
    repo.write("a.txt", "two\n");
    repo.git(&["add", "--", "a.txt"]);
    repo.write("a.txt", "three\n");
    repo.write("u.txt", "new\n");
    let git = repo.git_under_test();
    let cache = cache();
    let root = text(&repo.root);

    let both = diff(&git, &cache, &root, &text(&repo.path("a.txt"))).unwrap();
    let untracked = diff(&git, &cache, &root, &text(&repo.path("u.txt"))).unwrap();

    let staged_at = both.find("Staged changes\n\n").expect("staged section");
    let working_at = both
        .find("\n\nWorking tree changes\n\n")
        .expect("working section");
    assert!(staged_at < working_at, "{both}");
    assert!(both[..working_at].contains("+two"), "{both}");
    assert!(both[working_at..].contains("+three"), "{both}");
    assert_eq!(
        untracked,
        "No Git diff is available for untracked files until they are staged."
    );
}

#[test]
fn commit_requires_a_message_and_commits_staged_changes() {
    let repo = TestRepo::new();
    repo.write("a.txt", "one\n");
    repo.git(&["add", "--", "a.txt"]);
    let git = repo.git_under_test();
    let cache = cache();
    let root = text(&repo.root);

    let error = commit(&git, &cache, &root, "   ", None).unwrap_err();
    assert_eq!(error, "Commit message is required.");

    let result = commit(&git, &cache, &root, "  First commit  ", None).unwrap();
    assert_eq!(result.command, "git commit");
    assert_eq!(result.summary, "Commit completed.");
    assert_eq!(
        repo.git(&["log", "-1", "--format=%s"]).trim(),
        "First commit"
    );
    let status = repo_status(&git, &cache, &repo.root).unwrap();
    assert_eq!(status.staged, 0);
    assert_eq!(status.branch.as_deref(), Some("main"));
}

#[test]
fn fetch_pull_push_against_a_local_bare_remote() {
    let repo = TestRepo::with_remote();
    let peer = repo.peer();
    write_under(&peer, "from-peer.txt", "peer\n");
    repo.git_in(&peer, &["add", "--", "from-peer.txt"]);
    repo.git_in(&peer, &["commit", "-m", "from peer"]);
    repo.git_in(&peer, &["push", "origin", "main"]);
    let git = repo.git_under_test();
    let cache = cache();
    let root = text(&repo.root);
    // A status taken before the fetch is cached; the fetch must drop it.
    assert_eq!(repo_status(&git, &cache, &repo.root).unwrap().behind, 0);

    let fetched = fetch(&git, &cache, &root, None).unwrap();
    assert_eq!(fetched.command, "git fetch");
    let status = repo_status(&git, &cache, &repo.root).unwrap();
    assert_eq!((status.behind, status.ahead), (1, 0));
    assert_eq!(status.upstream.as_deref(), Some("origin/main"));

    pull(&git, &cache, &root, None).unwrap();
    assert!(repo.path("from-peer.txt").exists());

    repo.write("local.txt", "local\n");
    repo.commit_all("local");
    let pushed = push(&git, &cache, &root, None).unwrap();
    assert_eq!(pushed.summary, "Git push completed.");
    let origin = text(&repo.origin());
    assert_eq!(
        repo.git(&["--git-dir", &origin, "rev-parse", "main"])
            .trim(),
        repo.git(&["rev-parse", "HEAD"]).trim()
    );
}

#[test]
fn pull_with_diverged_branches_fails_with_gits_message() {
    let repo = TestRepo::with_remote();
    let peer = repo.peer();
    write_under(&peer, "from-peer.txt", "peer\n");
    repo.git_in(&peer, &["add", "--", "from-peer.txt"]);
    repo.git_in(&peer, &["commit", "-m", "from peer"]);
    repo.git_in(&peer, &["push", "origin", "main"]);
    repo.write("local.txt", "local\n");
    repo.commit_all("local");
    let git = repo.git_under_test();
    let started = Instant::now();

    let error = pull(&git, &cache(), &text(&repo.root), None).unwrap_err();

    assert!(started.elapsed() < Duration::from_secs(60));
    assert!(error.contains("divergent branches"), "{error}");
    assert!(!repo.root.join(".git").join("MERGE_HEAD").exists());
}

/// Accepts one connection, never answers it and reports when the peer
/// closed it: `Some(elapsed since accept)` at EOF or reset.
fn silent_server() -> (u16, mpsc::Receiver<()>, mpsc::Receiver<Instant>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let (accepted_tx, accepted_rx) = mpsc::channel();
    let (closed_tx, closed_rx) = mpsc::channel();
    std::thread::spawn(move || {
        let Ok((mut socket, _)) = listener.accept() else {
            return;
        };
        let _ = accepted_tx.send(());
        let mut buffer = [0u8; 4096];
        loop {
            match socket.read(&mut buffer) {
                Ok(0) | Err(_) => {
                    let _ = closed_tx.send(Instant::now());
                    return;
                }
                Ok(_) => {}
            }
        }
    });
    (port, accepted_rx, closed_rx)
}

#[test]
fn cancelled_fetch_kills_git_and_its_children() {
    let repo = TestRepo::new();
    repo.write("a.txt", "one\n");
    repo.commit_all("initial");
    let (port, accepted, closed) = silent_server();
    let url = format!("http://127.0.0.1:{port}/repo.git");
    repo.git(&["remote", "add", "origin", &url]);
    let mut env = repo.env();
    for name in ["NO_PROXY", "no_proxy"] {
        env.push((name.into(), "127.0.0.1,localhost".into()));
    }
    let git = Git::discover(&repo.data).with_env(env);
    let flag = Arc::new(AtomicBool::new(false));
    let cancel: CancelCheck = {
        let flag = flag.clone();
        Arc::new(move || flag.load(Ordering::SeqCst))
    };
    let canceller = {
        let flag = flag.clone();
        std::thread::spawn(move || {
            let started = Instant::now();
            // Git's remote helper holds the connection: cancel once it is
            // there, and no sooner than 500 ms.
            let connected = accepted.recv_timeout(Duration::from_secs(20)).is_ok();
            if let Some(rest) = Duration::from_millis(500).checked_sub(started.elapsed()) {
                std::thread::sleep(rest);
            }
            flag.store(true, Ordering::SeqCst);
            (connected, Instant::now())
        })
    };

    let error = fetch(&git, &cache(), &text(&repo.root), Some(cancel)).unwrap_err();
    let returned = Instant::now();
    let (connected, cancelled_at) = canceller.join().unwrap();

    assert!(connected, "git never connected to the test server");
    assert_eq!(error, GIT_CANCELLED);
    assert!(returned - cancelled_at < Duration::from_secs(5));
    let closed_at = closed
        .recv_timeout(Duration::from_secs(5))
        .expect("git's remote helper kept the connection open");
    assert!(closed_at - cancelled_at < Duration::from_secs(5));
}

#[test]
fn missing_git_reports_git_not_found_without_spawning() {
    let repo = TestRepo::new();
    repo.write("a.txt", "one\n");
    let git = Git::with_executable(None, repo.data.join("no-hooks-test")).with_env(repo.env());
    let cache = cache();
    let root = text(&repo.root);

    assert_eq!(
        repo_status(&git, &cache, &repo.root).unwrap_err(),
        GIT_NOT_FOUND
    );
    assert_eq!(
        stage(&git, &cache, &root, &[text(&repo.path("a.txt"))]).unwrap_err(),
        GIT_NOT_FOUND
    );
    assert_eq!(
        commit(&git, &cache, &root, "m", None).unwrap_err(),
        GIT_NOT_FOUND
    );
    assert_eq!(fetch(&git, &cache, &root, None).unwrap_err(), GIT_NOT_FOUND);
    let decorations = folder_decorations(&git, &cache, &repo.root);
    assert!(!decorations.in_repository);
    assert!(decorations.rows.is_empty());
    assert_eq!(git.spawn_count(), 0);
}

#[test]
fn mutating_command_refreshes_the_cached_status() {
    let repo = TestRepo::new();
    repo.write("base.txt", "base\n");
    repo.commit_all("initial");
    repo.write("new.txt", "new\n");
    let git = repo.git_under_test();
    let cache = cache();

    let before = repo_status(&git, &cache, &repo.root).unwrap();
    assert_eq!((before.untracked, before.staged), (1, 0));
    let spawns = git.spawn_count();
    // Within the TTL a second status is served from the cache: only the
    // root lookup runs.
    repo_status(&git, &cache, &repo.root).unwrap();
    assert_eq!(git.spawn_count(), spawns + 1);

    stage(
        &git,
        &cache,
        &text(&repo.root),
        &[text(&repo.path("new.txt"))],
    )
    .unwrap();
    let after = repo_status(&git, &cache, &repo.root).unwrap();

    assert_eq!((after.untracked, after.staged), (0, 1));
}
