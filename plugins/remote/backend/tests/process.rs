#![cfg(windows)]

//! Starts the built backend as a real process and talks to it over its pipe
//! the way the host does: a host-role client that answers the backend's
//! `host.*` requests and records them and the events it receives.

mod common;

use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use sumafile_remote_backend::secrets::{RemoteSecret, RemoteSecretStore, WindowsRemoteSecretStore};
use sumafile_remote_test_server::{write_legacy_metadata_db, TestServer};
use tokio::io::{AsyncReadExt, AsyncWriteExt, ReadHalf, WriteHalf};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::process::Child;
use tokio::sync::{mpsc, oneshot};

type Waiters = Arc<Mutex<HashMap<u64, oneshot::Sender<Value>>>>;

/// The host side of one backend connection.
struct HostRole {
    outbound: mpsc::UnboundedSender<Value>,
    waiters: Waiters,
    /// `host.*` methods the backend called, in order.
    host_calls: Arc<Mutex<Vec<String>>>,
    /// Notifications the backend sent: `(method, params)`.
    events: Arc<Mutex<Vec<(String, Value)>>>,
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
        let host_calls: Arc<Mutex<Vec<String>>> = Arc::default();
        let events: Arc<Mutex<Vec<(String, Value)>>> = Arc::default();
        let reader_task = {
            let outbound = outbound.clone();
            let waiters = waiters.clone();
            let host_calls = host_calls.clone();
            let events = events.clone();
            tokio::spawn(async move {
                let mut operations = 0;
                while let Some(message) = read_message(&mut reader).await {
                    let method = message.get("method").and_then(Value::as_str);
                    let id = message.get("id").filter(|id| !id.is_null()).cloned();
                    match (method, id) {
                        (Some(method), Some(id)) => {
                            host_calls.lock().unwrap().push(method.to_string());
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
                        (Some(method), None) => {
                            let params = message.get("params").cloned().unwrap_or(Value::Null);
                            events.lock().unwrap().push((method.to_string(), params));
                        }
                        (None, Some(id)) => {
                            if let Some(waiter) = id
                                .as_u64()
                                .and_then(|id| waiters.lock().unwrap().remove(&id))
                            {
                                let _ = waiter.send(message);
                            }
                        }
                        (None, None) => {}
                    }
                }
            })
        };
        HostRole {
            outbound,
            waiters,
            host_calls,
            events,
            next_id: 0,
            tasks: vec![reader_task, writer_task],
        }
    }

    async fn call(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let id = self.next_id;
        let (sender, receiver) = oneshot::channel();
        self.waiters.lock().unwrap().insert(id, sender);
        self.outbound
            .send(json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }))
            .unwrap();
        tokio::time::timeout(Duration::from_secs(30), receiver)
            .await
            .unwrap_or_else(|_| panic!("timed out waiting for {method}"))
            .unwrap()
    }

    async fn ok(&mut self, method: &str, params: Value) -> Value {
        let reply = self.call(method, params).await;
        assert!(reply.get("error").is_none(), "{method}: {reply}");
        reply["result"].clone()
    }

    fn host_calls(&self) -> Vec<String> {
        self.host_calls.lock().unwrap().clone()
    }

    fn event_names(&self) -> Vec<String> {
        self.events
            .lock()
            .unwrap()
            .iter()
            .map(|(name, _)| name.clone())
            .collect()
    }
}

/// Launches the backend with `--data-dir` and `SIMPLEFILE_METADATA_DB`, then
/// connects and handshakes as the host.
async fn launch(data_dir: &Path, metadata_db: &Path) -> (Child, HostRole) {
    let pipe_name = format!(
        "sumafile-remote-test-{}-{}",
        std::process::id(),
        common::random_hex()
    );
    let child = tokio::process::Command::new(env!("CARGO_BIN_EXE_sumafile-remote-backend"))
        .args(["--pipe-name", &pipe_name, "--data-dir"])
        .arg(data_dir)
        .env("SIMPLEFILE_METADATA_DB", metadata_db)
        .kill_on_drop(true)
        .spawn()
        .unwrap();

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
                "pluginId": "com.sumafile.remote",
                "hostVersion": "1.0.4",
                "sdkVersion": "1.0.0"
            }),
        )
        .await;
    assert_eq!(reply["pluginId"], "com.sumafile.remote");
    assert_eq!(reply["pluginVersion"], "1.0.0");
    (child, host)
}

async fn assert_exits_after_disconnect(mut child: Child, host: HostRole) {
    drop(host);
    let status = tokio::time::timeout(Duration::from_secs(10), child.wait())
        .await
        .expect("backend did not exit within 10 s of the disconnect")
        .unwrap();
    assert!(status.success(), "backend exit status {status}");
}

fn sha256(path: &Path) -> Vec<u8> {
    Sha256::digest(std::fs::read(path).unwrap()).to_vec()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn backend_process_serves_profiles_sessions_listing_and_a_transfer() {
    let remote = tempfile::tempdir().unwrap();
    std::fs::write(remote.path().join("listed.txt"), b"listed").unwrap();
    let server = TestServer::start(remote.path().to_path_buf())
        .await
        .unwrap();
    let info = server.info().clone();
    let temp = tempfile::tempdir().unwrap();
    let data_dir = temp.path().join("data");
    let local = temp.path().join("local");
    std::fs::create_dir_all(&local).unwrap();
    let bytes = common::pattern(512 * 1024);
    std::fs::write(local.join("upload.bin"), &bytes).unwrap();
    let (child, mut host) = launch(&data_dir, &temp.path().join("none.db")).await;

    let saved = host
        .ok(
            "profiles.save",
            json!({ "profile": common::server_profile(&info) }),
        )
        .await;
    let target = saved["credential_target"].as_str().unwrap().to_string();
    let session = host
        .ok(
            "session.connect",
            json!({ "profileId": saved["id"], "secret": info.password }),
        )
        .await;
    let session_id = session["session_id"].as_str().unwrap().to_string();
    let listing = host
        .ok(
            "fs.list",
            json!({ "remoteSessionId": session_id, "path": "/" }),
        )
        .await;
    assert_eq!(listing["entries"][0]["name"], "listed.txt", "{listing}");

    let outcome = host
        .ok(
            "transfer.run",
            json!({
                "remoteSessionId": session_id,
                "direction": "upload",
                "sources": [local.join("upload.bin")],
                "destination": "/",
                "conflictAction": "replace",
                "operationId": "op-process-upload"
            }),
        )
        .await;
    assert_eq!(outcome["retry_sources"], json!([]), "{outcome}");
    assert_eq!(
        std::fs::read(remote.path().join("upload.bin")).unwrap(),
        bytes
    );
    let calls = host.host_calls();
    for method in [
        "host.progress_begin",
        "host.progress_report",
        "host.progress_complete",
    ] {
        assert!(calls.iter().any(|call| call == method), "{calls:?}");
    }
    assert!(host
        .event_names()
        .iter()
        .any(|name| name == "remote.transfer-progress"));

    host.ok("profiles.delete", json!({ "profileId": saved["id"] }))
        .await;
    assert_eq!(host.ok("profiles.list", Value::Null).await, json!([]));
    assert_exits_after_disconnect(child, host).await;
    // No secret was ever given to profiles.save: nothing was written.
    assert_eq!(WindowsRemoteSecretStore.read_secret(&target).unwrap(), None);
    server.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn backend_process_imports_a_1_0_4_profile_and_connects_with_its_saved_password() {
    let remote = tempfile::tempdir().unwrap();
    std::fs::write(remote.path().join("from-1.0.4.txt"), b"kept").unwrap();
    let server = TestServer::start(remote.path().to_path_buf())
        .await
        .unwrap();
    let info = server.info().clone();
    let target = format!("SumaFile.Remote.test-{}", common::random_hex());
    assert_eq!(
        WindowsRemoteSecretStore.read_secret(&target).unwrap(),
        None,
        "a new target"
    );
    let _guard = common::DeleteCredentialOnDrop(target.clone());
    // Written as 1.0.4 wrote it: "password" 0x00 <value>.
    WindowsRemoteSecretStore
        .write_secret(&target, &RemoteSecret::Password(info.password.clone()))
        .unwrap();
    let temp = tempfile::tempdir().unwrap();
    let legacy = temp.path().join("metadata.db");
    let row = info.legacy_profile("legacy-upgrade", &target);
    write_legacy_metadata_db(&legacy, std::slice::from_ref(&row), None).unwrap();
    let legacy_hash = sha256(&legacy);
    let (child, mut host) = launch(&temp.path().join("data"), &legacy).await;

    let profiles = host.ok("profiles.list", Value::Null).await;
    assert_eq!(
        profiles,
        json!([{
            "id": "legacy-upgrade",
            "name": row.name,
            "protocol": "sftp",
            "host": "127.0.0.1",
            "port": info.port,
            "username": info.user,
            "root_path": "/",
            "auth_kind": "password",
            "insecure_plain_ftp": false,
            "passive_mode": true,
            "credential_target": target,
            "private_key_path": null,
            "trusted_host_fingerprint": info.fingerprint
        }])
    );
    assert!(
        host.event_names()
            .iter()
            .any(|name| name == "remote.profiles-changed"),
        "{:?}",
        host.event_names()
    );

    let session = host
        .ok("session.connect", json!({ "profileId": "legacy-upgrade" }))
        .await;
    let listing = host
        .ok(
            "fs.list",
            json!({ "remoteSessionId": session["session_id"], "path": "/" }),
        )
        .await;
    assert_eq!(listing["entries"][0]["name"], "from-1.0.4.txt", "{listing}");

    assert_exits_after_disconnect(child, host).await;
    assert_eq!(
        sha256(&legacy),
        legacy_hash,
        "the legacy database is unchanged"
    );
    server.stop().await;
}
