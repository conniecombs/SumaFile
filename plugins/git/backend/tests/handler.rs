//! `GitHandler` through `PluginHandler::handle` with `HostClient::fake`:
//! routing, params, Transfers rows and cancel by row id.

mod common;

use std::collections::HashMap;
use std::io::Read;
use std::net::TcpListener;
use std::sync::Arc;
use std::time::{Duration, Instant};

use common::{text, write_under, TestRepo};
use serde_json::{json, Value};
use sumafile_git_backend::git::Git;
use sumafile_git_backend::handler::GitHandler;
use sumafile_git_backend::GIT_CANCELLED;
use sumafile_plugin::{HostClient, PluginError, PluginHandler};

fn handler(repo: &TestRepo) -> Arc<GitHandler> {
    Arc::new(GitHandler::with_git(repo.git_under_test()))
}

fn folder_name(repo: &TestRepo) -> String {
    repo.root.file_name().unwrap().to_string_lossy().to_string()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn methods_route_and_reject_bad_params() {
    let repo = TestRepo::new();
    repo.write("a.txt", "one\n");
    let handler = handler(&repo);
    let host = HostClient::fake(HashMap::new());

    let status = handler
        .handle("repo.status", json!({ "path": text(&repo.root) }), &host)
        .await
        .unwrap();
    assert_eq!(status["is_repo"], true);
    assert_eq!(status["root"], text(&repo.root));
    assert_eq!(status["untracked"], 1);
    assert_eq!(
        status["changes"][0]["absolute_path"],
        text(&repo.path("a.txt"))
    );

    let decorations = handler
        .handle(
            "repo.decorations",
            json!({ "folder": text(&repo.root) }),
            &host,
        )
        .await
        .unwrap();
    assert_eq!(
        decorations,
        json!({
            "in_repository": true,
            "root": text(&repo.root),
            "rows": [{ "path": text(&repo.path("a.txt")), "status": "untracked" }]
        })
    );

    let staged = handler
        .handle(
            "paths.stage",
            json!({ "path": text(&repo.root), "paths": [text(&repo.path("a.txt"))] }),
            &host,
        )
        .await
        .unwrap();
    assert_eq!(staged["command"], "git stage");
    let diff = handler
        .handle(
            "paths.diff",
            json!({ "path": text(&repo.root), "filePath": text(&repo.path("a.txt")) }),
            &host,
        )
        .await
        .unwrap();
    assert!(
        diff.as_str().unwrap().starts_with("Staged changes"),
        "{diff}"
    );
    let committed = handler
        .handle(
            "repo.commit",
            json!({ "path": text(&repo.root), "message": "first", "operationId": "op-commit" }),
            &host,
        )
        .await
        .unwrap();
    assert_eq!(committed["summary"], "Commit completed.");
    assert!(host.fake_progress_calls().is_empty(), "commit opens no row");

    match handler.handle("repo.status", json!({}), &host).await {
        Err(PluginError::InvalidParams(message)) => {
            assert!(
                message.starts_with("Invalid repo.status params"),
                "{message}"
            )
        }
        other => panic!("expected invalid params, got {other:?}"),
    }
    match handler
        .handle("paths.stage", json!({ "path": text(&repo.root) }), &host)
        .await
    {
        Err(PluginError::InvalidParams(message)) => {
            assert!(
                message.starts_with("Invalid paths.stage params"),
                "{message}"
            )
        }
        other => panic!("expected invalid params, got {other:?}"),
    }
    assert_eq!(
        handler.handle("repo.nothing", Value::Null, &host).await,
        Err(PluginError::MethodNotFound)
    );
    let outside = repo.temp.path().join("plain");
    std::fs::create_dir_all(&outside).unwrap();
    assert_eq!(
        handler
            .handle(
                "paths.diff",
                json!({ "path": text(&outside), "filePath": text(&outside) }),
                &host
            )
            .await,
        Err(PluginError::Application(
            "The current path is not inside a Git repository.".to_string()
        ))
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn fetch_opens_a_transfers_row_and_completes_it() {
    let repo = TestRepo::with_remote();
    let handler = handler(&repo);
    let host = HostClient::fake(HashMap::new());

    let result = handler
        .handle(
            "remote.fetch",
            json!({ "path": text(&repo.root), "operationId": "op-fetch" }),
            &host,
        )
        .await
        .unwrap();

    assert_eq!(result["command"], "git fetch");
    assert_eq!(
        host.fake_progress_calls(),
        vec![
            format!("begin Fetching {}", folder_name(&repo)),
            "complete fake-operation-1".to_string(),
        ]
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn cancel_by_row_id_fails_the_fetch_with_git_operation_cancelled() {
    let repo = TestRepo::new();
    repo.write("a.txt", "one\n");
    repo.commit_all("initial");
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    std::thread::spawn(move || {
        if let Ok((mut socket, _)) = listener.accept() {
            let mut buffer = [0u8; 4096];
            while matches!(socket.read(&mut buffer), Ok(read) if read > 0) {}
        }
    });
    repo.git(&[
        "remote",
        "add",
        "origin",
        &format!("http://127.0.0.1:{port}/repo.git"),
    ]);
    let mut env = repo.env();
    for name in ["NO_PROXY", "no_proxy"] {
        env.push((name.into(), "127.0.0.1,localhost".into()));
    }
    let handler = Arc::new(GitHandler::with_git(
        Git::discover(&repo.data).with_env(env),
    ));
    let host = HostClient::fake(HashMap::new());

    let call = {
        let handler = handler.clone();
        let host = host.clone();
        let root = text(&repo.root);
        tokio::spawn(async move {
            handler
                .handle(
                    "remote.fetch",
                    json!({ "path": root, "operationId": "op-fetch" }),
                    &host,
                )
                .await
        })
    };
    let begun = format!("begin Fetching {}", folder_name(&repo));
    let deadline = Instant::now() + Duration::from_secs(20);
    while !host.fake_progress_calls().contains(&begun) {
        assert!(Instant::now() < deadline, "the Transfers row never opened");
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    tokio::time::sleep(Duration::from_millis(500)).await;
    host.fake_cancel("fake-operation-1");
    let cancelled_at = Instant::now();

    let reply = tokio::time::timeout(Duration::from_secs(5), call)
        .await
        .expect("the fetch did not stop after its row was cancelled")
        .unwrap();

    assert!(cancelled_at.elapsed() < Duration::from_secs(5));
    assert_eq!(
        reply,
        Err(PluginError::Application(GIT_CANCELLED.to_string()))
    );
    assert_eq!(
        host.fake_progress_calls(),
        vec![begun, format!("fail fake-operation-1 {GIT_CANCELLED}")]
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn failed_pull_fails_its_row_with_gits_message() {
    let repo = TestRepo::with_remote();
    let peer = repo.peer();
    write_under(&peer, "from-peer.txt", "peer\n");
    repo.git_in(&peer, &["add", "--", "from-peer.txt"]);
    repo.git_in(&peer, &["commit", "-m", "from peer"]);
    repo.git_in(&peer, &["push", "origin", "main"]);
    repo.write("local.txt", "local\n");
    repo.commit_all("local");
    let handler = handler(&repo);
    let host = HostClient::fake(HashMap::new());

    let reply = handler
        .handle(
            "remote.pull",
            json!({ "path": text(&repo.root), "operationId": "op-pull" }),
            &host,
        )
        .await;

    let Err(PluginError::Application(message)) = reply else {
        panic!("expected the pull to fail, got {reply:?}");
    };
    assert!(message.contains("divergent branches"), "{message}");
    assert_eq!(
        host.fake_progress_calls(),
        vec![
            format!("begin Pulling {}", folder_name(&repo)),
            format!("fail fake-operation-1 {message}"),
        ]
    );
}
