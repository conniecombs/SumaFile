use super::io::{
    encode_json_payload_with_limit, read_frame, writer_loop, OutboundFrame, OUTBOUND_QUEUE_CAPACITY,
};
use super::*;
use serde_json::{json, Value};
use simplefile_ipc::frame::{decode_length, encode_frame};
use simplefile_ipc::rpc::JsonRpcRequest;
use simplefile_ipc::{
    BINARY_FRAME_MAGIC, BINARY_LIST_DIRECTORY_CHUNK, BINARY_LIST_DIRECTORY_RESULT,
    HANDSHAKE_METHOD, HEALTH_METHOD, LIST_DIRECTORY_CHUNK, PREFIX_RESULT_TOO_LARGE,
    PROTOCOL_VERSION,
};
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll};
use tokio::io::{duplex, AsyncWrite, AsyncWriteExt};
use tokio::sync::mpsc;

#[derive(Clone, Default)]
struct RecordingWriter {
    writes: Arc<Mutex<Vec<Vec<u8>>>>,
}

impl AsyncWrite for RecordingWriter {
    fn poll_write(
        self: Pin<&mut Self>,
        _cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<std::io::Result<usize>> {
        self.writes.lock().unwrap().push(buf.to_vec());
        Poll::Ready(Ok(buf.len()))
    }

    fn poll_flush(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Poll::Ready(Ok(()))
    }

    fn poll_shutdown(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
        Poll::Ready(Ok(()))
    }
}

async fn send_request(client: &mut tokio::io::DuplexStream, method: &str, id: u64, params: Value) {
    let request = JsonRpcRequest {
        jsonrpc: "2.0".into(),
        id: Some(json!(id)),
        method: method.into(),
        params: Some(params),
    };
    let payload = serde_json::to_vec(&request).unwrap();
    let frame = encode_frame(&payload).unwrap();
    client.write_all(&frame).await.unwrap();
}

async fn call(client: &mut tokio::io::DuplexStream, method: &str, id: u64, params: Value) -> Value {
    send_request(client, method, id, params).await;
    let response = read_frame(client).await.unwrap();
    serde_json::from_slice(&response).unwrap()
}

#[tokio::test]
async fn writer_loop_batches_ready_frames() {
    let writer = RecordingWriter::default();
    let writes = writer.writes.clone();
    let (sender, receiver) = mpsc::channel(OUTBOUND_QUEUE_CAPACITY);

    sender
        .send(OutboundFrame {
            payload: b"alpha".to_vec(),
            ack: None,
        })
        .await
        .unwrap();
    sender
        .send(OutboundFrame {
            payload: b"bravo".to_vec(),
            ack: None,
        })
        .await
        .unwrap();
    drop(sender);

    writer_loop(writer, receiver).await.unwrap();

    let writes = writes.lock().unwrap();
    assert_eq!(writes.len(), 1);
    let batch = &writes[0];
    let first_len = decode_length(batch[0..4].try_into().unwrap()).unwrap() as usize;
    assert_eq!(&batch[4..4 + first_len], b"alpha");
    let second_start = 4 + first_len;
    let second_len =
        decode_length(batch[second_start..second_start + 4].try_into().unwrap()).unwrap() as usize;
    assert_eq!(
        &batch[second_start + 4..second_start + 4 + second_len],
        b"bravo"
    );
}

#[test]
fn oversized_json_response_preserves_request_id() {
    let response = JsonRpcResponse::result(Some(json!(42)), json!({ "data": "large" }));

    let payload = encode_json_payload_with_limit(&response, 24).expect("encode oversize error");
    let value: Value = serde_json::from_slice(&payload).expect("parse oversize error");

    assert_eq!(value["id"], json!(42));
    assert!(value["error"]["message"]
        .as_str()
        .expect("error message")
        .contains(PREFIX_RESULT_TOO_LARGE));
}

#[tokio::test]
async fn duplex_health_and_home_dir() {
    let (mut client, server) = duplex(64 * 1024);
    let (server_read, server_write) = tokio::io::split(server);
    let server = tokio::spawn(serve_connection(
        server_read,
        server_write,
        SessionState {
            expected_token: Some("dev".to_string()),
            ..SessionState::default()
        },
    ));

    let handshake = call(
        &mut client,
        HANDSHAKE_METHOD,
        1,
        json!({
            "protocolVersion": PROTOCOL_VERSION,
            "clientName": "test",
            "authToken": "dev"
        }),
    )
    .await;
    assert_eq!(handshake["result"]["protocolVersion"], PROTOCOL_VERSION);

    let health = call(&mut client, HEALTH_METHOD, 2, json!({})).await;
    assert_eq!(health["result"]["ok"], true);

    let home = call(&mut client, "get_home_dir", 3, json!({})).await;
    assert!(home["result"].as_str().unwrap().len() > 1);

    let dir = std::env::temp_dir();
    let listing = call(
        &mut client,
        "list_directory",
        4,
        json!({ "path": dir.to_string_lossy() }),
    )
    .await;
    // One or more chunk notifications may arrive before the result.
    let mut message = listing;
    while message.get("method").and_then(Value::as_str) == Some(LIST_DIRECTORY_CHUNK) {
        message = {
            let response = read_frame(&mut client).await.unwrap();
            serde_json::from_slice(&response).unwrap()
        };
    }
    assert!(message["result"]["path"].as_str().is_some());
    assert!(message["result"]["entries"].is_array());

    let _ = call(&mut client, "ipc.shutdown", 5, json!({})).await;
    let _ = server.await;
}

#[tokio::test]
async fn binary_hot_frames_emit_listing_chunks_and_result() {
    let (mut client, server) = duplex(64 * 1024);
    let (server_read, server_write) = tokio::io::split(server);
    let server = tokio::spawn(serve_connection(
        server_read,
        server_write,
        SessionState {
            expected_token: Some("dev".to_string()),
            ..SessionState::default()
        },
    ));

    let handshake = call(
        &mut client,
        HANDSHAKE_METHOD,
        1,
        json!({
            "protocolVersion": PROTOCOL_VERSION,
            "clientName": "test",
            "authToken": "dev",
            "binaryHotFrames": true
        }),
    )
    .await;
    assert_eq!(handshake["result"]["binaryHotFrames"], true);

    send_request(
        &mut client,
        "list_directory",
        2,
        json!({ "path": std::env::temp_dir().to_string_lossy() }),
    )
    .await;

    let mut saw_chunk = false;
    let mut saw_result = false;
    for _ in 0..32 {
        let frame = read_frame(&mut client).await.unwrap();
        assert!(frame.starts_with(&BINARY_FRAME_MAGIC));
        match frame.get(5).copied() {
            Some(BINARY_LIST_DIRECTORY_CHUNK) => saw_chunk = true,
            Some(BINARY_LIST_DIRECTORY_RESULT) => {
                saw_result = true;
                break;
            }
            tag => panic!("unexpected binary listing frame tag {tag:?}"),
        }
    }

    assert!(saw_chunk);
    assert!(saw_result);

    let _ = call(&mut client, "ipc.shutdown", 3, json!({})).await;
    let _ = server.await;
}

#[tokio::test]
async fn session_cache_serves_repeated_listings_and_invalidates_on_file_mutation() {
    let temp_root = std::env::temp_dir().join(format!(
        "simplefile-session-cache-e2e-{}",
        crate::progress::generate_transfer_operation_id()
    ));
    std::fs::create_dir_all(&temp_root).unwrap();
    let initial_file = temp_root.join("alpha.txt");
    std::fs::write(&initial_file, b"hello").unwrap();

    let state = SessionState {
        expected_token: Some("dev".to_string()),
        ..SessionState::default()
    };
    let cache_handle = state.cache.clone();

    let (mut client, server) = duplex(64 * 1024);
    let (server_read, server_write) = tokio::io::split(server);
    let server = tokio::spawn(serve_connection(server_read, server_write, state));

    let _ = call(
        &mut client,
        HANDSHAKE_METHOD,
        1,
        json!({
            "protocolVersion": PROTOCOL_VERSION,
            "clientName": "test",
            "authToken": "dev"
        }),
    )
    .await;

    async fn read_listing_response(client: &mut tokio::io::DuplexStream) -> Value {
        loop {
            let raw = read_frame(client).await.unwrap();
            let msg: Value = serde_json::from_slice(&raw).unwrap();
            if msg.get("method").and_then(Value::as_str) != Some(LIST_DIRECTORY_CHUNK) {
                return msg;
            }
        }
    }

    // First listing populates the session cache
    send_request(
        &mut client,
        "list_directory",
        2,
        json!({ "path": temp_root.to_string_lossy() }),
    )
    .await;
    let first = read_listing_response(&mut client).await;
    assert_eq!(first["result"]["entries"].as_array().unwrap().len(), 1);
    assert_eq!(cache_handle.directory_listing_stats().count, 1);

    // Second listing hits the session cache
    send_request(
        &mut client,
        "list_directory",
        3,
        json!({ "path": temp_root.to_string_lossy() }),
    )
    .await;
    let second = read_listing_response(&mut client).await;
    assert_eq!(second["result"]["entries"].as_array().unwrap().len(), 1);
    assert!(cache_handle.directory_listing_stats().hits >= 1);

    // Folder metrics caches both metrics and subdirectories
    let metrics = call(
        &mut client,
        "get_folder_metrics",
        4,
        json!({ "path": temp_root.to_string_lossy() }),
    )
    .await;
    assert_eq!(metrics["result"]["size"], 5);
    assert_eq!(cache_handle.folder_metrics_stats().count, 1);

    // Creating a new file via IPC selectively invalidates parent directory listing and folder metrics
    let created = call(
        &mut client,
        "create_file",
        5,
        json!({ "path": temp_root.to_string_lossy(), "name": "bravo.txt" }),
    )
    .await;
    assert!(created.get("error").is_none());
    assert_eq!(cache_handle.directory_listing_stats().count, 0);
    assert_eq!(cache_handle.folder_metrics_stats().count, 0);

    // Subsequent listing reflects the newly created file
    send_request(
        &mut client,
        "list_directory",
        6,
        json!({ "path": temp_root.to_string_lossy() }),
    )
    .await;
    let third = read_listing_response(&mut client).await;
    assert_eq!(third["result"]["entries"].as_array().unwrap().len(), 2);

    let _ = call(&mut client, "ipc.shutdown", 7, json!({})).await;
    let _ = server.await;
    let _ = std::fs::remove_dir_all(&temp_root);
}

async fn serve_test_session() -> (
    tokio::io::DuplexStream,
    tokio::task::JoinHandle<Result<(), String>>,
) {
    let (mut client, server) = duplex(64 * 1024);
    let (server_read, server_write) = tokio::io::split(server);
    let server = tokio::spawn(serve_connection(
        server_read,
        server_write,
        SessionState {
            expected_token: Some("dev".to_string()),
            ..SessionState::default()
        },
    ));
    let handshake = call(
        &mut client,
        HANDSHAKE_METHOD,
        1,
        json!({
            "protocolVersion": PROTOCOL_VERSION,
            "clientName": "test",
            "authToken": "dev"
        }),
    )
    .await;
    assert_eq!(handshake["result"]["protocolVersion"], PROTOCOL_VERSION);
    (client, server)
}

/// Reads frames until the response carrying `id` arrives, or the deadline passes.
async fn read_response_with_id(
    client: &mut tokio::io::DuplexStream,
    id: u64,
    timeout: std::time::Duration,
) -> Option<Value> {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        match tokio::time::timeout_at(deadline, read_frame(client)).await {
            Ok(Ok(payload)) => {
                let message: Value = serde_json::from_slice(&payload).unwrap();
                if message.get("id") == Some(&json!(id)) {
                    return Some(message);
                }
            }
            _ => return None,
        }
    }
}

fn scratch_dir(label: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "sumafile_session_{label}_{}_{}",
        std::process::id(),
        simplefile_core::utils::generate_operation_id()
    ));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn duplicate_check_responds_through_session_loop() {
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("dup");
    std::fs::write(dir.join("a.txt"), b"same").unwrap();
    std::fs::write(dir.join("b.txt"), b"same").unwrap();

    send_request(
        &mut client,
        "duplicate_check",
        2,
        json!({ "directory": dir.to_string_lossy(), "operationId": "dup-1", "minSize": 0 }),
    )
    .await;
    let response = read_response_with_id(&mut client, 2, std::time::Duration::from_secs(10)).await;
    let _ = std::fs::remove_dir_all(&dir);
    let response = response.expect("duplicate_check never produced a response");
    assert!(response.get("result").is_some(), "{response}");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn disk_cleanup_responds_through_session_loop() {
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("cleanup");
    std::fs::write(dir.join("a.txt"), b"x").unwrap();

    send_request(
        &mut client,
        "disk_cleanup",
        2,
        json!({ "directory": dir.to_string_lossy(), "operationId": "clean-1" }),
    )
    .await;
    let response = read_response_with_id(&mut client, 2, std::time::Duration::from_secs(10)).await;
    let _ = std::fs::remove_dir_all(&dir);
    let response = response.expect("disk_cleanup never produced a response");
    assert!(response.get("result").is_some(), "{response}");
}

#[tokio::test]
async fn malformed_json_frame_gets_parse_error_and_session_continues() {
    let (mut client, server) = serve_test_session().await;

    let frame = encode_frame(b"{ this is not json").unwrap();
    client.write_all(&frame).await.unwrap();
    let response: Value = serde_json::from_slice(&read_frame(&mut client).await.unwrap()).unwrap();
    assert_eq!(response["error"]["code"], simplefile_ipc::ERR_PARSE);
    assert!(response.get("id").is_none() || response["id"].is_null());

    let health = call(&mut client, HEALTH_METHOD, 2, json!({})).await;
    assert_eq!(health["result"]["ok"], true);

    let _ = call(&mut client, "ipc.shutdown", 3, json!({})).await;
    assert_eq!(server.await.unwrap(), Ok(()));
}

/// An unauthenticated client must not be able to make the service allocate
/// an 80 MiB frame; before the handshake only small frames are accepted.
#[tokio::test]
async fn oversized_frame_before_handshake_closes_the_session() {
    let (mut client, server) = duplex(64 * 1024);
    let (server_read, server_write) = tokio::io::split(server);
    let server = tokio::spawn(serve_connection(
        server_read,
        server_write,
        SessionState {
            expected_token: Some("dev".to_string()),
            ..SessionState::default()
        },
    ));
    let claimed: u32 = 8 * 1024 * 1024;
    client.write_all(&claimed.to_le_bytes()).await.unwrap();
    let outcome = tokio::time::timeout(std::time::Duration::from_secs(5), server)
        .await
        .expect("server exits instead of waiting for 8 MiB")
        .unwrap();
    let error = outcome.expect_err("oversized pre-handshake frame is an error");
    assert!(error.contains("too large"), "{error}");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn extract_archive_with_operation_id_emits_running_then_completed_for_zip() {
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("zip_progress");
    std::fs::write(dir.join("a.txt"), b"alpha").unwrap();
    let archive = dir.join("pack.zip");
    simplefile_core::archive::create_archive(
        vec![dir.join("a.txt").to_string_lossy().into_owned()],
        archive.to_string_lossy().into_owned(),
        "zip".to_string(),
    )
    .unwrap();
    let destination = dir.join("out");

    send_request(
        &mut client,
        "extract_archive",
        2,
        json!({
            "archivePath": archive.to_string_lossy(),
            "destination": destination.to_string_lossy(),
            "operationId": "zip-op"
        }),
    )
    .await;
    let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(10);
    let mut events = Vec::new();
    let response = loop {
        let payload = tokio::time::timeout_at(deadline, read_frame(&mut client))
            .await
            .expect("extract_archive never replied")
            .unwrap();
        let message: Value = serde_json::from_slice(&payload).unwrap();
        if message["method"] == "operation-progress" {
            events.push(message["params"].clone());
        } else if message.get("id") == Some(&json!(2)) {
            break message;
        }
    };
    let extracted = std::fs::read(destination.join("a.txt"));
    let _ = std::fs::remove_dir_all(&dir);

    assert_eq!(response["result"], Value::Null, "{response}");
    assert!(response.get("error").is_none(), "{response}");
    assert_eq!(extracted.unwrap(), b"alpha");
    assert!(events.len() >= 2, "{events:?}");
    assert!(events
        .iter()
        .all(|event| event["operation_id"] == "zip-op"
            && event["operation_type"] == "extract-archive"));
    assert_eq!(events[0]["status"], "running");
    assert_eq!(events[0]["current"], 0);
    assert_eq!(events[0]["total"], 0);
    assert_eq!(events.last().unwrap()["status"], "completed");
}

/// No service lib test registers `.7z`, so nothing serves it here: a pane
/// listing of the file or of a folder inside it reports the plugin note,
/// with the options `PaneNavigator` sends and without any.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn list_directory_on_an_unserved_7z_reports_the_plugin_note() {
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("unserved_7z");
    let archive = dir.join("x.7z");
    std::fs::write(&archive, b"not read without an engine").unwrap();
    let archive_path = archive.to_string_lossy().into_owned();
    let pane_options = json!({
        "mode": "light",
        "finalEntries": false,
        "sortBy": "name",
        "sortAscending": true,
        "includeHidden": true
    });

    let mut id = 1;
    let mut errors = Vec::new();
    for path in [archive_path.clone(), format!("{archive_path}\\docs")] {
        for with_options in [true, false] {
            id += 1;
            let mut params = if with_options {
                pane_options.clone()
            } else {
                json!({})
            };
            params["path"] = json!(path);
            send_request(&mut client, "list_directory", id, params).await;
            let response =
                read_response_with_id(&mut client, id, std::time::Duration::from_secs(10))
                    .await
                    .unwrap_or_else(|| panic!("list_directory {path} never replied"));
            errors.push((
                format!("{path} with options {with_options}"),
                response["error"]["message"].clone(),
            ));
        }
    }
    let _ = std::fs::remove_dir_all(&dir);

    for (described, message) in errors {
        assert_eq!(
            message,
            json!("7-Zip support requires the SumaFile 7-Zip plugin."),
            "{described}"
        );
    }
}
