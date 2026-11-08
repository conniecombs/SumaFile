#![cfg(windows)]

//! Starts the built backend as a real process and talks to it over its pipe
//! the way the SumaFile service does.

use std::time::Duration;

use serde_json::{json, Value};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};

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

#[tokio::test]
async fn backend_process_lists_a_hello_archive_and_exits_after_disconnect() {
    let dir = tempfile::tempdir().unwrap();
    let archive = dir.path().join("sample.hello");
    std::fs::write(&archive, "a.txt\t5\nb/c.txt\t7\n").unwrap();

    let pipe_name = format!(
        "hello-plugin-test-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    );
    let mut child = tokio::process::Command::new(env!("CARGO_BIN_EXE_hello-plugin-backend"))
        .args(["--pipe-name", &pipe_name, "--data-dir"])
        .arg(dir.path())
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
    let mut client = client.expect("backend pipe never appeared");

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

    let reply = call(
        &mut client,
        2,
        "archive.list",
        json!({ "archivePath": archive }),
    )
    .await;
    assert_eq!(
        reply["result"],
        json!({ "entries": [
            { "path": "a.txt", "isDir": false, "size": 5, "compressedSize": 5 },
            { "path": "b/c.txt", "isDir": false, "size": 7, "compressedSize": 7 },
        ]})
    );

    drop(client);
    let status = tokio::time::timeout(Duration::from_secs(5), child.wait())
        .await
        .expect("backend did not exit within 5 s of the last disconnect")
        .unwrap();
    assert!(status.success(), "backend exit status {status}");
}
