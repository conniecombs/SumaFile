#![cfg(windows)]

//! Starts the built backend as a real process and talks to it over its pipe
//! the way the SumaFile service does.

use std::path::Path;
use std::time::Duration;

use serde_json::{json, Value};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::process::Child;

async fn call(client: &mut NamedPipeClient, id: u64, method: &str, params: Value) -> Value {
    let message = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params });
    let frame = simplefile_ipc::frame::encode_frame(message.to_string().as_bytes()).unwrap();
    client.write_all(&frame).await.unwrap();
    client.flush().await.unwrap();
    let mut header = [0u8; 4];
    tokio::time::timeout(Duration::from_secs(5), client.read_exact(&mut header))
        .await
        .expect("timed out waiting for a reply")
        .unwrap();
    let length = simplefile_ipc::frame::decode_length(header).unwrap() as usize;
    let mut payload = vec![0u8; length];
    client.read_exact(&mut payload).await.unwrap();
    let reply: Value = serde_json::from_slice(&payload).unwrap();
    assert_eq!(reply["id"], json!(id), "unexpected frame {reply}");
    reply
}

/// The backend under test: its process, its pipe, and the service's
/// connection to it.
struct Launched {
    child: Child,
    pipe: String,
    client: NamedPipeClient,
}

async fn connect(pipe: &str) -> NamedPipeClient {
    for _ in 0..500 {
        if let Ok(opened) = ClientOptions::new().open(pipe) {
            return opened;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    panic!("backend pipe never appeared");
}

/// Launches the backend and connects to its pipe, then handshakes as the
/// service.
async fn launch(data_dir: &Path) -> Launched {
    let pipe_name = format!(
        "hello-plugin-test-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    );
    let child = tokio::process::Command::new(env!("CARGO_BIN_EXE_hello-plugin-backend"))
        .args(["--pipe-name", &pipe_name, "--data-dir"])
        .arg(data_dir)
        .kill_on_drop(true)
        .spawn()
        .unwrap();

    let pipe = format!(r"\\.\pipe\{pipe_name}");
    let mut client = connect(&pipe).await;

    let reply = call(
        &mut client,
        1,
        "plugin.handshake",
        json!({
            "client": "simplefile-service",
            "pluginId": "com.sumafile.hello",
            "hostVersion": "1.0.4",
            "sdkVersion": "1.0.0"
        }),
    )
    .await;
    assert_eq!(reply["result"]["pluginId"], json!("com.sumafile.hello"));
    assert_eq!(reply["result"]["pluginVersion"], json!("0.1.0"));
    Launched {
        child,
        pipe,
        client,
    }
}

/// The service was the backend's only client, and the host never connected.
/// The backend waits for the host it was started for, and ends when the host
/// has been there and the last client leaves.
async fn assert_waits_for_the_host_then_exits(launched: Launched) {
    let Launched {
        mut child,
        pipe,
        client,
    } = launched;
    drop(client);
    tokio::time::sleep(Duration::from_millis(300)).await;
    assert!(
        child.try_wait().unwrap().is_none(),
        "the backend left before its host came"
    );

    let mut host = connect(&pipe).await;
    let reply = call(
        &mut host,
        1,
        "plugin.handshake",
        json!({
            "pluginId": "com.sumafile.hello",
            "hostVersion": "1.0.4.0",
            "sdkVersion": "1.0.0"
        }),
    )
    .await;
    assert_eq!(reply["result"]["pluginId"], json!("com.sumafile.hello"));
    drop(host);

    let status = tokio::time::timeout(Duration::from_secs(5), child.wait())
        .await
        .expect("backend did not exit within 5 s of the last disconnect")
        .unwrap();
    assert!(status.success(), "backend exit status {status}");
}

fn assert_ok(reply: &Value) {
    assert!(reply.get("error").is_none(), "{reply}");
    assert_eq!(reply["result"], Value::Null, "{reply}");
}

#[tokio::test]
async fn backend_process_lists_a_hello_archive_and_exits_after_disconnect() {
    let dir = tempfile::tempdir().unwrap();
    let archive = dir.path().join("sample.hello");
    std::fs::write(&archive, "a.txt\t5\nb/c.txt\t7\n").unwrap();
    let mut launched = launch(dir.path()).await;
    let client = &mut launched.client;

    let reply = call(client, 2, "archive.list", json!({ "archivePath": archive })).await;
    assert_eq!(
        reply["result"],
        json!({ "entries": [
            { "path": "a.txt", "isDir": false, "size": 5, "compressedSize": 5 },
            { "path": "b/c.txt", "isDir": false, "size": 7, "compressedSize": 7 },
        ]})
    );

    assert_waits_for_the_host_then_exits(launched).await;
}

#[tokio::test]
async fn backend_process_serves_the_v2_archive_methods() {
    let dir = tempfile::tempdir().unwrap();
    let archive = dir.path().join("pack.hello");
    std::fs::write(&archive, "top.txt\t3\n").unwrap();
    let base = dir.path().join("base");
    std::fs::create_dir_all(base.join("docs").join("empty")).unwrap();
    std::fs::write(base.join("docs").join("a.txt"), "hello").unwrap();
    let mut launched = launch(dir.path()).await;
    let client = &mut launched.client;

    let reply = call(
        client,
        2,
        "archive.add",
        json!({ "archivePath": archive, "baseDir": base, "paths": ["docs"], "operationId": "op-add" }),
    )
    .await;
    assert_ok(&reply);
    let reply = call(client, 3, "archive.list", json!({ "archivePath": archive })).await;
    assert_eq!(
        reply["result"],
        json!({ "entries": [
            { "path": "top.txt", "isDir": false, "size": 3, "compressedSize": 3 },
            { "path": "docs/a.txt", "isDir": false, "size": 5, "compressedSize": 5 },
            { "path": "docs/empty", "isDir": true, "size": 0, "compressedSize": 0 },
        ]})
    );

    let reply = call(
        client,
        4,
        "archive.rename",
        json!({ "archivePath": archive, "innerPath": "docs", "newPath": "papers" }),
    )
    .await;
    assert_ok(&reply);
    let reply = call(
        client,
        5,
        "archive.delete",
        json!({ "archivePath": archive, "innerPaths": ["top.txt"] }),
    )
    .await;
    assert_ok(&reply);
    let reply = call(client, 6, "archive.list", json!({ "archivePath": archive })).await;
    assert_eq!(
        reply["result"],
        json!({ "entries": [
            { "path": "papers/a.txt", "isDir": false, "size": 5, "compressedSize": 5 },
            { "path": "papers/empty", "isDir": true, "size": 0, "compressedSize": 0 },
        ]})
    );

    let destination = dir.path().join("read");
    std::fs::create_dir_all(&destination).unwrap();
    let reply = call(
        client,
        7,
        "archive.read_entry",
        json!({
            "archivePath": archive, "innerPath": "papers", "destination": destination,
            "maxBytes": 1024, "maxEntries": 16
        }),
    )
    .await;
    assert_ok(&reply);
    assert_eq!(
        std::fs::metadata(destination.join("papers").join("a.txt"))
            .unwrap()
            .len(),
        5
    );
    assert!(destination.join("papers").join("empty").is_dir());

    assert_waits_for_the_host_then_exits(launched).await;
}
