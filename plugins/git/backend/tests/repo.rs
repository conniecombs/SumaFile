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

/// A shell command, as git runs configured commands, that appends a line to
/// `marker` and then passes stdin through (a clean filter's or a textconv's
/// output). Git for Windows runs it with its own `sh`, which takes
/// `C:/…` paths.
fn marker_command(marker: &Path) -> String {
    let marker = text(marker).replace('\\', "/");
    format!("echo ran >> '{marker}'; cat")
}

fn markers_in(folder: &Path) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(folder)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().to_string())
        .filter(|name| name.starts_with("marker-"))
        .collect();
    names.sort();
    names
}

/// Makes `relative` stat-dirty with the same contents, so git has to read
/// (and clean-filter) it to tell whether it changed.
fn touch(repo: &TestRepo, relative: &str) {
    let path = repo.path(relative);
    let contents = std::fs::read(&path).unwrap();
    std::thread::sleep(Duration::from_millis(20));
    std::fs::write(&path, contents).unwrap();
    let file = std::fs::File::options().write(true).open(&path).unwrap();
    let stamp = std::time::SystemTime::now() + Duration::from_secs(5);
    file.set_modified(stamp).unwrap();
}

/// A browsed repository's config can name programs: a clean filter and a
/// long-running filter process (through `.gitattributes`, `.git/info/attributes`
/// or `core.attributesFile`), `diff.external` and a textconv driver. Status,
/// decorations and the diff preview must run none of them.
#[test]
fn read_only_queries_run_no_program_the_repository_configures() {
    let repo = TestRepo::new();
    repo.write("a.txt", "one\n");
    repo.write("b.txt", "one\n");
    repo.write("c.txt", "one\n");
    repo.commit_all("initial");
    let markers = repo.temp.path().join("markers");
    std::fs::create_dir_all(&markers).unwrap();
    let clean = marker_command(&markers.join("marker-clean"));
    let process = marker_command(&markers.join("marker-process"));
    let external = marker_command(&markers.join("marker-external"));
    let textconv = marker_command(&markers.join("marker-textconv"));
    let attributes_file = repo.temp.path().join("attributes");
    std::fs::write(&attributes_file, "c.txt filter=Mixed.Case\n").unwrap();
    for (key, value) in [
        ("filter.x.clean", clean.as_str()),
        ("filter.x.required", "true"),
        ("filter.p.process", process.as_str()),
        ("filter.Mixed.Case.clean", clean.as_str()),
        ("diff.external", external.as_str()),
        ("diff.y.textconv", textconv.as_str()),
        ("core.attributesFile", &text(&attributes_file)),
    ] {
        repo.git(&["config", key, value]);
    }
    repo.write(".gitattributes", "* filter=x diff=y\n");
    let info = repo.root.join(".git").join("info");
    std::fs::create_dir_all(&info).unwrap();
    std::fs::write(info.join("attributes"), "b.txt filter=p\n").unwrap();
    repo.write("a.txt", "two\n");
    for name in ["b.txt", "c.txt"] {
        touch(&repo, name);
    }
    let git = repo.git_under_test();

    let status = repo_status(&git, &cache(), &repo.root).unwrap();
    let decorations = folder_decorations(&git, &cache(), &repo.root);
    let preview = diff(
        &git,
        &cache(),
        &text(&repo.root),
        &text(&repo.path("a.txt")),
    )
    .unwrap();

    assert!(
        markers_in(&markers).is_empty(),
        "{:?}",
        markers_in(&markers)
    );
    assert!(status.is_repo);
    assert!(
        status.changes.iter().any(|change| change.path == "a.txt"),
        "{:?}",
        status.changes
    );
    assert!(decorations.in_repository);
    assert!(preview.contains("+two"), "{preview}");

    // The planted commands do run when git is not told to skip them, so the
    // checks above prove something.
    touch(&repo, "b.txt");
    touch(&repo, "c.txt");
    let _ = repo
        .command_in(&repo.root, &["status", "--porcelain"])
        .output();
    let no_filters = [
        "-c",
        "filter.x.clean=",
        "-c",
        "filter.x.required=false",
        "-c",
        "filter.p.process=",
        "-c",
        "filter.Mixed.Case.clean=",
    ];
    for extra in [&[][..], &["--no-ext-diff"][..]] {
        let mut args = no_filters.to_vec();
        args.push("diff");
        args.extend_from_slice(extra);
        args.extend(["--", "a.txt"]);
        let _ = repo.command_in(&repo.root, &args).output();
    }
    assert_eq!(
        markers_in(&markers),
        vec![
            "marker-clean",
            "marker-external",
            "marker-process",
            "marker-textconv"
        ]
    );
}

/// `git status` checks each submodule with a `git status` of its own, which
/// would run the submodule's filters. Status and decorations skip that check
/// and still show a submodule whose checked-out commit moved.
#[test]
fn read_only_queries_run_no_program_a_submodule_configures() {
    let repo = TestRepo::new();
    let source = repo.temp.path().join("subsource");
    std::fs::create_dir_all(&source).unwrap();
    repo.git_in(&source, &["init", "-b", "main"]);
    write_under(&source, "s.txt", "one\n");
    repo.git_in(&source, &["add", "--", "s.txt"]);
    repo.git_in(
        &source,
        &[
            "-c",
            "user.name=t",
            "-c",
            "user.email=t@sumafile.invalid",
            "commit",
            "-m",
            "s",
        ],
    );
    repo.git(&[
        "-c",
        "protocol.file.allow=always",
        "submodule",
        "add",
        &text(&source).replace('\\', "/"),
        "sm",
    ]);
    repo.git(&["commit", "-m", "add submodule"]);
    let markers = repo.temp.path().join("markers");
    std::fs::create_dir_all(&markers).unwrap();
    let submodule = repo.path("sm");
    repo.git_in(
        &submodule,
        &[
            "config",
            "filter.z.clean",
            &marker_command(&markers.join("marker-submodule")),
        ],
    );
    let module_dir = repo.root.join(".git").join("modules").join("sm");
    std::fs::create_dir_all(module_dir.join("info")).unwrap();
    std::fs::write(module_dir.join("info").join("attributes"), "* filter=z\n").unwrap();
    touch(&repo, "sm/s.txt");
    let git = repo.git_under_test();

    let status = repo_status(&git, &cache(), &repo.root).unwrap();
    let decorations = folder_decorations(&git, &cache(), &repo.root);

    assert!(
        markers_in(&markers).is_empty(),
        "{:?}",
        markers_in(&markers)
    );
    assert!(status.is_repo && decorations.in_repository);
    assert!(status.changes.is_empty(), "{:?}", status.changes);

    let _ = repo
        .command_in(&repo.root, &["status", "--porcelain"])
        .output();
    assert_eq!(markers_in(&markers), vec!["marker-submodule"]);
    std::fs::remove_file(markers.join("marker-submodule")).unwrap();

    // A moved submodule commit still shows.
    write_under(&submodule, "s.txt", "two\n");
    repo.git_in(&submodule, &["-c", "filter.z.clean=", "add", "--", "s.txt"]);
    repo.git_in(
        &submodule,
        &[
            "-c",
            "user.name=t",
            "-c",
            "user.email=t@sumafile.invalid",
            "commit",
            "-m",
            "moved",
        ],
    );
    // The commit above may run the submodule's filter itself.
    let _ = std::fs::remove_file(markers.join("marker-submodule"));
    touch(&repo, "sm/s.txt");
    let moved = repo_status(&git, &cache(), &repo.root).unwrap();
    assert_eq!(moved.changes.len(), 1, "{:?}", moved.changes);
    assert_eq!(moved.changes[0].path, "sm");
    assert!(
        markers_in(&markers).is_empty(),
        "{:?}",
        markers_in(&markers)
    );
}

/// The work tree's `.gitattributes` still applies: a file checked out with
/// CRLF under `text eol=crlf` is not reported as modified once it is
/// stat-dirty.
#[test]
fn status_keeps_the_repositorys_eol_attributes() {
    let repo = TestRepo::new();
    repo.write(".gitattributes", "*.txt text eol=crlf\n");
    repo.write("f.txt", "a\nb\n");
    repo.commit_all("initial");
    std::fs::remove_file(repo.path("f.txt")).unwrap();
    repo.git(&["checkout", "--", "f.txt"]);
    assert_eq!(std::fs::read(repo.path("f.txt")).unwrap(), b"a\r\nb\r\n");
    touch(&repo, "f.txt");
    let git = repo.git_under_test();

    let status = repo_status(&git, &cache(), &repo.root).unwrap();

    assert!(status.changes.is_empty(), "{:?}", status.changes);
}

/// `link` becomes a directory symbolic link (a junction on Windows) to
/// `target`.
fn link_folder(link: &Path, target: &Path) {
    #[cfg(windows)]
    {
        let status = std::process::Command::new("cmd")
            .args(["/d", "/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .stdout(std::process::Stdio::null())
            .status()
            .unwrap();
        assert!(status.success(), "mklink /J failed");
    }
    #[cfg(unix)]
    std::os::unix::fs::symlink(target, link).unwrap();
}

fn canonical(path: &str) -> std::path::PathBuf {
    std::fs::canonicalize(path).unwrap_or_else(|error| panic!("{path}: {error}"))
}

/// Git counts `--show-cdup` on the resolved folder, so through a link the
/// caller's path minus those levels can be another folder, even another
/// repository. The root is then git's own `--show-toplevel`.
#[test]
fn root_through_a_link_is_the_real_repository_root() {
    let repo = TestRepo::new();
    repo.write("sub/deeper/a.txt", "one\n");
    repo.commit_all("initial");
    repo.write("sub/deeper/a.txt", "two\n");
    let git = repo.git_under_test();

    // `<temp>\link\deeper` is `<repo>\sub\deeper`: one level up from the
    // caller's spelling is `<temp>`, not the repository.
    let link = repo.temp.path().join("link");
    link_folder(&link, &repo.path("sub"));
    let through_link = repo_status(&git, &cache(), &link.join("deeper")).unwrap();
    assert!(through_link.is_repo);
    assert_eq!(
        canonical(through_link.root.as_deref().unwrap()),
        canonical(&text(&repo.root))
    );
    assert_eq!(through_link.changes.len(), 1, "{:?}", through_link.changes);

    // A link inside another repository, at the same depth: one level up is
    // that other repository's root, which has a `.git` of its own.
    let other = repo.temp.path().join("other");
    std::fs::create_dir_all(&other).unwrap();
    repo.git_in(&other, &["init", "-b", "main"]);
    let inner = other.join("inner");
    link_folder(&inner, &repo.path("sub"));
    let decorations = folder_decorations(&git, &cache(), &inner);
    assert!(decorations.in_repository);
    assert_eq!(
        canonical(decorations.root.as_deref().unwrap()),
        canonical(&text(&repo.root))
    );
}

/// `<root>\sub\..` is the root: discarding it would make `git clean -fd`
/// and `git restore` cover the whole repository, so it is refused.
#[test]
fn discard_refuses_a_path_that_resolves_to_the_root() {
    let repo = TestRepo::new();
    repo.write("sub/a.txt", "one\n");
    repo.commit_all("initial");
    repo.write("sub/a.txt", "two\n");
    repo.write("junk.txt", "x\n");
    let git = repo.git_under_test();
    let root = text(&repo.root);
    let dot_dot = text(&repo.path("sub").join(".."));

    let error = discard(&git, &cache(), &root, &[dot_dot]).unwrap_err();

    assert_eq!(
        error,
        "Refusing to run a file-level Git action against the repository root."
    );
    assert!(repo.path("junk.txt").exists());
    assert_eq!(
        std::fs::read_to_string(repo.path("sub/a.txt")).unwrap(),
        "two\n"
    );
}
