#![cfg(windows)]

//! Starts the built backend from a staged plugin folder, beside the bundled
//! 7-Zip, and talks to it over its pipe the way the SumaFile service does.

use std::path::{Path, PathBuf};
use std::time::Duration;

use serde_json::{json, Value};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use tokio::process::Child;

const BACKEND_EXE: &str = "sumafile-sevenzip-backend.exe";

async fn call(client: &mut NamedPipeClient, id: u64, method: &str, params: Value) -> Value {
    let message = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params });
    let frame = simplefile_ipc::frame::encode_frame(message.to_string().as_bytes()).unwrap();
    client.write_all(&frame).await.unwrap();
    client.flush().await.unwrap();
    // Skip archive.progress notifications until the reply arrives.
    loop {
        let mut header = [0u8; 4];
        tokio::time::timeout(Duration::from_secs(60), client.read_exact(&mut header))
            .await
            .expect("timed out waiting for a reply")
            .unwrap();
        let length = simplefile_ipc::frame::decode_length(header).unwrap() as usize;
        let mut payload = vec![0u8; length];
        client.read_exact(&mut payload).await.unwrap();
        let frame: Value = serde_json::from_slice(&payload).unwrap();
        if frame.get("method") == Some(&json!("archive.progress")) {
            continue;
        }
        assert_eq!(frame["id"], json!(id), "unexpected frame {frame}");
        return frame;
    }
}

/// A plugin folder as the host installs it: the backend exe beside the
/// bundled 7-Zip files.
fn stage(root: &Path) -> PathBuf {
    let folder = root.join("com.sumafile.sevenzip");
    std::fs::create_dir_all(&folder).unwrap();
    std::fs::copy(
        env!("CARGO_BIN_EXE_sumafile-sevenzip-backend"),
        folder.join(BACKEND_EXE),
    )
    .unwrap();
    let bundled = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("third_party")
        .join("7zip")
        .join("win-x64");
    for name in ["7za.exe", "7za.dll", "7zxa.dll", "License.txt"] {
        let source = bundled.join(name);
        assert!(source.is_file(), "missing {}", source.display());
        std::fs::copy(&source, folder.join(name)).unwrap();
    }
    folder
}

/// Launches the staged backend and handshakes as the service.
async fn launch(folder: &Path, data_dir: &Path) -> (Child, NamedPipeClient) {
    let pipe_name = format!(
        "sevenzip-plugin-test-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    );
    let child = tokio::process::Command::new(folder.join(BACKEND_EXE))
        .args(["--pipe-name", &pipe_name, "--data-dir"])
        .arg(data_dir)
        .env_remove("SIMPLEFILE_7Z")
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
            "pluginId": "com.sumafile.sevenzip",
            "hostVersion": "1.0.4",
            "sdkVersion": "1.0.0"
        }),
    )
    .await;
    assert_eq!(reply["result"]["pluginId"], json!("com.sumafile.sevenzip"));
    assert_eq!(reply["result"]["pluginVersion"], json!("1.0.0"));
    (child, client)
}

#[tokio::test]
async fn backend_process_creates_and_lists_a_7z_through_its_pipe() {
    let dir = tempfile::tempdir().unwrap();
    let folder = stage(dir.path());
    let source = dir.path().join("source");
    std::fs::create_dir_all(source.join("café 資料")).unwrap();
    std::fs::write(source.join("café 資料").join("hello.txt"), "hello").unwrap();
    std::fs::write(source.join("literal [brackets] & spaces.txt"), "abc").unwrap();
    let archive = dir.path().join("pack.7z");
    let data_dir = dir.path().join("data");
    let (mut child, mut client) = launch(&folder, &data_dir).await;

    let reply = call(
        &mut client,
        2,
        "archive.create",
        json!({
            "archivePath": archive,
            "paths": [source.join("café 資料"), source.join("literal [brackets] & spaces.txt")],
            "operationId": "op-create",
        }),
    )
    .await;
    assert!(reply.get("error").is_none(), "{reply}");
    assert_eq!(reply["result"], Value::Null, "{reply}");

    let reply = call(
        &mut client,
        3,
        "archive.list",
        json!({ "archivePath": archive }),
    )
    .await;
    let mut entries = reply["result"]["entries"]
        .as_array()
        .unwrap_or_else(|| panic!("no entries in {reply}"))
        .clone();
    entries.sort_by_key(|e| e["path"].as_str().unwrap().to_string());
    assert_eq!(
        Value::Array(entries),
        json!([
            { "path": "café 資料", "isDir": true, "size": 0, "compressedSize": 0 },
            { "path": "café 資料/hello.txt", "isDir": false, "size": 5, "compressedSize": entries_packed(&reply, "café 資料/hello.txt") },
            { "path": "literal [brackets] & spaces.txt", "isDir": false, "size": 3, "compressedSize": entries_packed(&reply, "literal [brackets] & spaces.txt") },
        ])
    );

    drop(client);
    let status = tokio::time::timeout(Duration::from_secs(5), child.wait())
        .await
        .expect("backend did not exit within 5 s of the last disconnect")
        .unwrap();
    assert!(status.success(), "backend exit status {status}");
}

/// The packed size 7-Zip reported for `path`; solid blocks make it vary.
fn entries_packed(reply: &Value, path: &str) -> Value {
    reply["result"]["entries"]
        .as_array()
        .unwrap()
        .iter()
        .find(|e| e["path"] == json!(path))
        .map(|e| e["compressedSize"].clone())
        .unwrap_or(Value::Null)
}
