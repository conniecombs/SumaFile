#![cfg(windows)]

//! Starts the built backend as a real process and talks to it over its pipe
//! the way the host does: a host-role client that answers the backend's
//! `host.*` requests.

mod common;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use common::{text, TestRepo};
use serde_json::{json, Value};
use tokio::io::{AsyncReadExt, AsyncWriteExt, ReadHalf, WriteHalf};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::process::Child;
use tokio::sync::{mpsc, oneshot};

type Waiters = Arc<Mutex<HashMap<u64, oneshot::Sender<Value>>>>;

/// The host side of one backend connection.
struct HostRole {
    outbound: mpsc::UnboundedSender<Value>,
    waiters: Waiters,
    next_id: u64,
    /// The reader and writer tasks; aborting both closes the pipe.
    tasks: Vec<tokio::task::JoinHandle<()>>,
}

impl Drop for HostRole {
    fn drop(&mut self) {
        for task in &self.tasks {
            task.abort();
        }
    }
}

fn frame(message: &Value) -> Vec<u8> {
    simplefile_ipc::frame::encode_frame(message.to_string().as_bytes()).unwrap()
}

async fn read_message(reader: &mut ReadHalf<NamedPipeClient>) -> Option<Value> {
    let mut header = [0u8; 4];
    reader.read_exact(&mut header).await.ok()?;
    let length = simplefile_ipc::frame::decode_length(header).ok()? as usize;
    let mut payload = vec![0u8; length];
    reader.read_exact(&mut payload).await.ok()?;
    serde_json::from_slice(&payload).ok()
}

async fn write_loop(
    mut writer: WriteHalf<NamedPipeClient>,
    mut messages: mpsc::UnboundedReceiver<Value>,
) {
    while let Some(message) = messages.recv().await {
        if writer.write_all(&frame(&message)).await.is_err() {
            break;
        }
        let _ = writer.flush().await;
    }
}

impl HostRole {
    fn start(client: NamedPipeClient) -> HostRole {
        let (mut reader, writer) = tokio::io::split(client);
        let (outbound, messages) = mpsc::unbounded_channel();
        let writer_task = tokio::spawn(write_loop(writer, messages));
        let waiters: Waiters = Arc::default();
        let reader_task = {
            let outbound = outbound.clone();
            let waiters = waiters.clone();
            tokio::spawn(async move {
                let mut operations = 0;
                while let Some(message) = read_message(&mut reader).await {
                    let method = message.get("method").and_then(Value::as_str);
                    let id = message.get("id").filter(|id| !id.is_null()).cloned();
                    match (method, id) {
                        (Some(method), Some(id)) => {
                            let result = match method {
                                "host.get_settings" => json!({ "values": {} }),
                                "host.progress_begin" => {
                                    operations += 1;
                                    json!({ "operationId": format!("host-op-{operations}") })
                                }
                                _ => Value::Null,
                            };
                            let _ = outbound
                                .send(json!({ "jsonrpc": "2.0", "id": id, "result": result }));
                        }
                        (None, Some(id)) => {
                            if let Some(waiter) = id
                                .as_u64()
                                .and_then(|id| waiters.lock().unwrap().remove(&id))
                            {
                                let _ = waiter.send(message);
                            }
                        }
                        _ => {}
                    }
                }
            })
        };
        HostRole {
            outbound,
            waiters,
            next_id: 0,
            tasks: vec![reader_task, writer_task],
        }
    }

    async fn ok(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let id = self.next_id;
        let (sender, receiver) = oneshot::channel();
        self.waiters.lock().unwrap().insert(id, sender);
        self.outbound
            .send(json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }))
            .unwrap();
        let reply = tokio::time::timeout(Duration::from_secs(30), receiver)
            .await
            .unwrap_or_else(|_| panic!("timed out waiting for {method}"))
            .unwrap();
        assert!(reply.get("error").is_none(), "{method}: {reply}");
        reply["result"].clone()
    }
}

/// Launches the backend with `--data-dir` and the test's git isolation,
/// then connects and handshakes as the host.
async fn launch(repo: &TestRepo) -> (Child, HostRole) {
    let pipe_name = format!(
        "sumafile-git-test-{}-{}",
        std::process::id(),
        repo.temp
            .path()
            .file_name()
            .unwrap()
            .to_string_lossy()
            .replace(|c: char| !c.is_ascii_alphanumeric(), "")
    );
    let mut command = tokio::process::Command::new(env!("CARGO_BIN_EXE_sumafile-git-backend"));
    command
        .args(["--pipe-name", &pipe_name, "--data-dir"])
        .arg(&repo.data)
        .kill_on_drop(true);
    for (key, value) in repo.env() {
        command.env(key, value);
    }
    let child = command.spawn().unwrap();

    let path = format!(r"\\.\pipe\{pipe_name}");
    let mut client = None;
    for _ in 0..500 {
        if let Ok(opened) = ClientOptions::new().open(&path) {
            client = Some(opened);
            break;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    let mut host = HostRole::start(client.expect("backend pipe never appeared"));
    let reply = host
        .ok(
            "plugin.handshake",
            json!({
                "client": "sumafile-host",
                "pluginId": "com.sumafile.git",
                "hostVersion": "1.0.4",
                "sdkVersion": "1.0.0"
            }),
        )
        .await;
    assert_eq!(reply["pluginId"], "com.sumafile.git");
    assert_eq!(reply["pluginVersion"], "1.0.0");
    (child, host)
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn backend_process_serves_status_and_decorations() {
    let repo = TestRepo::new();
    // One commit first: an unborn branch reports no branch name ("## No commits yet on main"), as 1.0.4 did.
    repo.write("seed.txt", "seed\n");
    repo.commit_all("seed");
    repo.write("a.txt", "one\n");
    let plain = repo.temp.path().join("plain");
    std::fs::create_dir_all(&plain).unwrap();
    let (mut child, mut host) = launch(&repo).await;

    let status = host
        .ok("repo.status", json!({ "path": text(&repo.root) }))
        .await;
    assert_eq!(status["is_repo"], true, "{status}");
    assert_eq!(status["root"], text(&repo.root));
    assert_eq!(status["branch"], "main");
    assert_eq!(status["untracked"], 1);
    assert_eq!(
        status["changes"][0]["absolute_path"],
        text(&repo.path("a.txt"))
    );

    let decorations = host
        .ok("repo.decorations", json!({ "folder": text(&plain) }))
        .await;
    assert_eq!(
        decorations,
        json!({ "in_repository": false, "root": null, "rows": [] })
    );

    drop(host);
    let exit = tokio::time::timeout(Duration::from_secs(10), child.wait())
        .await
        .expect("backend did not exit within 10 s of the disconnect")
        .unwrap();
    assert!(exit.success(), "backend exit status {exit}");
}
