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

/// Reads frames until one satisfies `wanted`, or the deadline passes.
async fn read_message_where(
    client: &mut tokio::io::DuplexStream,
    timeout: std::time::Duration,
    wanted: impl Fn(&Value) -> bool,
) -> Option<Value> {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        match tokio::time::timeout_at(deadline, read_frame(client)).await {
            Ok(Ok(payload)) => {
                let message: Value = serde_json::from_slice(&payload).unwrap();
                if wanted(&message) {
                    return Some(message);
                }
            }
            _ => return None,
        }
    }
}

async fn call_skipping_notifications(
    client: &mut tokio::io::DuplexStream,
    method: &str,
    id: u64,
    params: Value,
) -> Value {
    send_request(client, method, id, params).await;
    read_response_with_id(client, id, std::time::Duration::from_secs(20))
        .await
        .unwrap_or_else(|| panic!("{method} never replied"))
}

fn row_names(page: &Value) -> Vec<String> {
    page["entries"]
        .as_array()
        .expect("a page has entries")
        .iter()
        .map(|entry| entry["name"].as_str().unwrap().to_string())
        .collect()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn listing_view_pages_resorts_and_reports_a_refresh_as_a_delta() {
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("view");
    for name in ["c.txt", "a.txt", "b.txt", ".hidden"] {
        std::fs::write(dir.join(name), b"x").unwrap();
    }
    std::fs::create_dir(dir.join("Zed")).unwrap();
    let path = dir.to_string_lossy().to_string();

    let opened = call_skipping_notifications(
        &mut client,
        "open_listing_view",
        2,
        json!({ "viewId": "primary", "path": path, "includeHidden": false, "pageSize": 2 }),
    )
    .await;
    let first = &opened["result"];
    assert_eq!(
        (4, 0, 1),
        (
            first["total"].as_u64().unwrap(),
            first["offset"].as_u64().unwrap(),
            first["revision"].as_u64().unwrap()
        )
    );
    assert_eq!(vec!["Zed", "a.txt"], row_names(first));
    // An ordinary folder's rows carry names only; the host rebuilds the paths.
    assert_eq!("", first["entries"][1]["path"].as_str().unwrap());

    let rest = call_skipping_notifications(
        &mut client,
        "get_listing_view_range",
        3,
        json!({ "viewId": "primary", "offset": 2, "count": 10 }),
    )
    .await;
    assert_eq!(vec!["b.txt", "c.txt"], row_names(&rest["result"]));

    // The same view and folder with another order: re-sorted, not enumerated again.
    let resorted = call_skipping_notifications(
        &mut client,
        "open_listing_view",
        4,
        json!({ "viewId": "primary", "path": path, "includeHidden": false, "sortAscending": false, "pageSize": 2 }),
    )
    .await;
    assert_eq!(vec!["Zed", "c.txt"], row_names(&resorted["result"]));
    assert_eq!(2, resorted["result"]["revision"].as_u64().unwrap());

    std::fs::write(dir.join("d.txt"), b"new").unwrap();
    let refreshed = call_skipping_notifications(
        &mut client,
        "refresh_listing_view",
        5,
        json!({ "viewId": "primary" }),
    )
    .await;
    let delta = &refreshed["result"];
    assert!(!delta["reset"].as_bool().unwrap());
    assert_eq!(
        (2, 3, 5),
        (
            delta["base_revision"].as_u64().unwrap(),
            delta["revision"].as_u64().unwrap(),
            delta["total"].as_u64().unwrap()
        )
    );
    let changes = delta["changes"].as_array().unwrap();
    assert_eq!(1, changes.len());
    // Descending: Zed, d.txt, c.txt, b.txt, a.txt.
    assert_eq!(
        ("insert", 1, "d.txt"),
        (
            changes[0]["kind"].as_str().unwrap(),
            changes[0]["index"].as_u64().unwrap(),
            changes[0]["entry"]["name"].as_str().unwrap()
        )
    );

    let closed = call_skipping_notifications(
        &mut client,
        "close_listing_view",
        6,
        json!({ "viewId": "primary" }),
    )
    .await;
    assert!(closed.get("error").is_none());
    let gone = call_skipping_notifications(
        &mut client,
        "get_listing_view_range",
        7,
        json!({ "viewId": "primary", "offset": 0, "count": 1 }),
    )
    .await;
    assert!(gone["error"]["message"]
        .as_str()
        .unwrap()
        .contains("not open"));
    let _ = std::fs::remove_dir_all(&dir);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn listing_view_of_a_watched_folder_reports_new_and_removed_files_as_deltas() {
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("view_watch");
    std::fs::write(dir.join("a.txt"), b"x").unwrap();
    let path = dir.to_string_lossy().to_string();

    let opened = call_skipping_notifications(
        &mut client,
        "open_listing_view",
        2,
        json!({ "viewId": "primary", "path": path }),
    )
    .await;
    assert_eq!(1, opened["result"]["total"].as_u64().unwrap());
    let watching =
        call_skipping_notifications(&mut client, "watch_directory", 3, json!({ "path": path }))
            .await;
    assert!(watching.get("error").is_none());

    let is_delta = |message: &Value| {
        message.get("method").and_then(Value::as_str) == Some("listing-view-changed")
    };
    std::fs::write(dir.join("b.txt"), b"new").unwrap();
    let added = read_message_where(&mut client, std::time::Duration::from_secs(20), is_delta)
        .await
        .expect("a delta for the new file");
    let params = &added["params"];
    assert_eq!("primary", params["view_id"].as_str().unwrap());
    let inserted = params["changes"]
        .as_array()
        .unwrap()
        .iter()
        .find(|change| change["kind"] == "insert")
        .expect("an insert change");
    assert_eq!(
        (1, "b.txt"),
        (
            inserted["index"].as_u64().unwrap(),
            inserted["entry"]["name"].as_str().unwrap()
        )
    );

    std::fs::remove_file(dir.join("a.txt")).unwrap();
    let removed = read_message_where(&mut client, std::time::Duration::from_secs(20), |message| {
        is_delta(message)
            && message["params"]["changes"]
                .as_array()
                .is_some_and(|changes| changes.iter().any(|change| change["kind"] == "remove"))
    })
    .await
    .expect("a delta for the removed file");
    assert_eq!(1, removed["params"]["total"].as_u64().unwrap());

    let page = call_skipping_notifications(
        &mut client,
        "get_listing_view_range",
        4,
        json!({ "viewId": "primary", "offset": 0, "count": 10 }),
    )
    .await;
    assert_eq!(vec!["b.txt"], row_names(&page["result"]));

    // A rename is one delta: the old row leaves, and the row that arrives
    // says which name it had.
    std::fs::rename(dir.join("b.txt"), dir.join("c.txt")).unwrap();
    let renamed = read_message_where(&mut client, std::time::Duration::from_secs(20), is_delta)
        .await
        .expect("a delta for the rename");
    let changes = renamed["params"]["changes"].as_array().unwrap();
    assert_eq!(
        vec!["remove", "insert"],
        changes
            .iter()
            .map(|change| change["kind"].as_str().unwrap())
            .collect::<Vec<_>>()
    );
    assert_eq!("c.txt", changes[1]["entry"]["name"].as_str().unwrap());
    assert_eq!("b.txt", changes[1]["renamed_from"].as_str().unwrap());
    assert!(changes[0].get("renamed_from").is_none());

    // The watched folder itself goes away: the host is told so.
    std::fs::remove_file(dir.join("c.txt")).unwrap();
    std::fs::remove_dir(&dir).unwrap();
    let gone = read_message_where(&mut client, std::time::Duration::from_secs(20), |message| {
        message.get("method").and_then(Value::as_str) == Some("file-change")
            && message["params"]["kind"] == "gone"
    })
    .await
    .expect("the watched folder is reported gone");
    assert_eq!(path, gone["params"]["path"].as_str().unwrap());
    // The view still shows the folder's rows, but the folder is not opened
    // from them: only an enumeration says what is there now, and there is nothing.
    let reopened = call_skipping_notifications(
        &mut client,
        "open_listing_view",
        8,
        json!({ "viewId": "primary", "path": path }),
    )
    .await;
    assert!(reopened["error"]["message"].is_string(), "{reopened}");
    let unwatched =
        call_skipping_notifications(&mut client, "unwatch_directory", 5, json!({})).await;
    assert!(unwatched.get("error").is_none());

    // A folder that cannot be watched is an error of the request.
    let refused =
        call_skipping_notifications(&mut client, "watch_directory", 6, json!({ "path": path }))
            .await;
    assert!(refused["error"]["message"].is_string(), "{refused}");
}

/// A cached listing too old to trust blindly opens the view at once; the
/// folder is then enumerated again and what differs arrives as a delta.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn listing_view_opens_from_an_unverified_cached_listing_and_then_corrects_it() {
    let dir = scratch_dir("view_stale");
    std::fs::write(dir.join("a.txt"), b"x").unwrap();
    std::fs::write(dir.join("b.txt"), b"y").unwrap();
    let path = dir.to_string_lossy().to_string();

    let state = SessionState {
        expected_token: Some("dev".to_string()),
        ..SessionState::default()
    };
    // What the cache remembers of the folder: only a.txt.
    let mut remembered = simplefile_core::dir_list::list_directory_collect(path.clone()).unwrap();
    remembered.entries.retain(|entry| entry.name == "a.txt");
    state
        .cache
        .put_unverified_directory_listing(&path, remembered);

    let (mut client, server) = duplex(64 * 1024);
    let (server_read, server_write) = tokio::io::split(server);
    let _server = tokio::spawn(serve_connection(server_read, server_write, state));
    let _ = call(
        &mut client,
        HANDSHAKE_METHOD,
        1,
        json!({ "protocolVersion": PROTOCOL_VERSION, "clientName": "test", "authToken": "dev" }),
    )
    .await;

    send_request(
        &mut client,
        "open_listing_view",
        2,
        json!({ "viewId": "primary", "path": path }),
    )
    .await;
    let mut opened = None;
    let mut corrected = None;
    while opened.is_none() || corrected.is_none() {
        let message = read_message_where(&mut client, std::time::Duration::from_secs(20), |_| true)
            .await
            .expect("the page and then the correcting delta");
        if message.get("id") == Some(&json!(2)) {
            opened = Some(message);
        } else if message.get("method").and_then(Value::as_str) == Some("listing-view-changed") {
            corrected = Some(message);
        }
    }

    assert_eq!(vec!["a.txt"], row_names(&opened.unwrap()["result"]));
    let delta = &corrected.unwrap()["params"];
    assert_eq!(2, delta["total"].as_u64().unwrap());
    assert_eq!(
        ("insert", "b.txt"),
        (
            delta["changes"][0]["kind"].as_str().unwrap(),
            delta["changes"][0]["entry"]["name"].as_str().unwrap()
        )
    );
    let _ = std::fs::remove_dir_all(&dir);
}

fn facts_scratch_db() -> std::path::PathBuf {
    static DB: std::sync::OnceLock<std::path::PathBuf> = std::sync::OnceLock::new();
    DB.get_or_init(|| {
        let path = scratch_dir("facts-db").join("metadata.db");
        // Every fact test in this process caches into one scratch database, never the user's.
        std::env::set_var("SIMPLEFILE_METADATA_DB", &path);
        path
    })
    .clone()
}

fn wav_bytes(seconds: u32) -> Vec<u8> {
    let data_len = 8_000 * seconds;
    let mut bytes = Vec::new();
    bytes.extend_from_slice(b"RIFF");
    bytes.extend_from_slice(&(36 + data_len).to_le_bytes());
    bytes.extend_from_slice(b"WAVEfmt ");
    bytes.extend_from_slice(&16u32.to_le_bytes());
    bytes.extend_from_slice(&1u16.to_le_bytes());
    bytes.extend_from_slice(&1u16.to_le_bytes());
    bytes.extend_from_slice(&8_000u32.to_le_bytes());
    bytes.extend_from_slice(&8_000u32.to_le_bytes());
    bytes.extend_from_slice(&1u16.to_le_bytes());
    bytes.extend_from_slice(&8u16.to_le_bytes());
    bytes.extend_from_slice(b"data");
    bytes.extend_from_slice(&data_len.to_le_bytes());
    bytes.resize(bytes.len() + data_len as usize, 128);
    bytes
}

/// Sends `get_entry_facts` and collects every `entry-facts` fact until the reply with `id` arrives.
async fn entry_facts_round_trip(
    client: &mut tokio::io::DuplexStream,
    id: u64,
    params: Value,
) -> (Vec<Value>, Value) {
    send_request(client, "get_entry_facts", id, params).await;
    let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(20);
    let mut facts = Vec::new();
    loop {
        let message: Value = match tokio::time::timeout_at(deadline, read_frame(client)).await {
            Ok(Ok(payload)) => serde_json::from_slice(&payload).unwrap(),
            _ => panic!("get_entry_facts never replied; facts so far: {facts:?}"),
        };
        if message["id"] == json!(id) {
            return (facts, message);
        }
        if message["method"] == "entry-facts" {
            facts.extend(
                message["params"]["facts"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .cloned(),
            );
        }
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn entry_facts_stream_for_files_and_folders_and_the_reply_counts_them() {
    let _db = facts_scratch_db();
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("facts");
    let tone = dir.join("tone.wav");
    std::fs::write(&tone, wav_bytes(65)).unwrap();
    let sub = dir.join("sub");
    std::fs::create_dir(&sub).unwrap();
    std::fs::write(sub.join("a.txt"), b"aaaa").unwrap();
    std::fs::write(sub.join("b.txt"), b"bb").unwrap();
    let missing = dir.join("missing.mp3");
    let path = |p: &std::path::Path| p.to_string_lossy().to_string();

    let (facts, reply) = entry_facts_round_trip(
        &mut client,
        2,
        json!({
            "requestId": "primary",
            "paths": [path(&tone), path(&sub), path(&missing)],
            "kinds": ["duration", "folder_size", "item_count", "colour"],
        }),
    )
    .await;

    let find = |p: &std::path::Path, kind: &str| {
        facts
            .iter()
            .find(|fact| fact["path"] == path(p) && fact["kind"] == kind)
            .unwrap_or_else(|| panic!("no {kind} for {}", p.display()))
            .clone()
    };
    assert_eq!(
        ("ready", "1:05", 65.0),
        (
            find(&tone, "duration")["state"].as_str().unwrap(),
            find(&tone, "duration")["text"].as_str().unwrap(),
            find(&tone, "duration")["sort"].as_f64().unwrap()
        )
    );
    assert_eq!("none", find(&tone, "folder_size")["state"]);
    assert_eq!("none", find(&sub, "duration")["state"]);
    assert_eq!(
        ("ready", "6"),
        (
            find(&sub, "folder_size")["state"].as_str().unwrap(),
            find(&sub, "folder_size")["text"].as_str().unwrap()
        )
    );
    assert_eq!(
        ("ready", "2"),
        (
            find(&sub, "item_count")["state"].as_str().unwrap(),
            find(&sub, "item_count")["text"].as_str().unwrap()
        )
    );
    assert_eq!("failed", find(&missing, "duration")["state"]);
    // Three paths, three known kinds (the unknown one is ignored).
    assert_eq!(9, facts.len());
    assert_eq!(
        json!({ "request_id": "primary", "settled": 9 }),
        reply["result"]
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn put_entry_facts_is_served_from_the_cache_next_time() {
    let _db = facts_scratch_db();
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("facts-put");
    let clip = dir.join("old.avi");
    std::fs::write(&clip, b"RIFF\0\0\0\0AVI LIST").unwrap();
    let clip_path = clip.to_string_lossy().to_string();

    // Without the cache the service cannot read an AVI.
    let (before, _) = entry_facts_round_trip(
        &mut client,
        2,
        json!({ "requestId": "p", "paths": [clip_path], "kinds": ["duration"] }),
    )
    .await;
    assert_eq!("unsupported", before[0]["state"]);

    let put = call_skipping_notifications(
        &mut client,
        "put_entry_facts",
        3,
        json!({ "facts": [{ "path": clip_path, "kind": "duration", "state": "ready", "text": "0:42", "sort": 42.0 }] }),
    )
    .await;
    assert_eq!(Value::Null, put["result"]);

    let (after, _) = entry_facts_round_trip(
        &mut client,
        4,
        json!({ "requestId": "p", "paths": [clip_path], "kinds": ["duration"] }),
    )
    .await;
    assert_eq!(
        ("ready", "0:42"),
        (
            after[0]["state"].as_str().unwrap(),
            after[0]["text"].as_str().unwrap()
        )
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn cancel_entry_facts_replies_and_a_cancelled_request_still_settles() {
    let _db = facts_scratch_db();
    let (mut client, _server) = serve_test_session().await;
    let dir = scratch_dir("facts-cancel");
    let paths: Vec<String> = (0..40)
        .map(|index| {
            let file = dir.join(format!("f{index}.txt"));
            std::fs::write(&file, b"x").unwrap();
            file.to_string_lossy().to_string()
        })
        .collect();

    send_request(
        &mut client,
        "get_entry_facts",
        2,
        json!({ "requestId": "c", "paths": paths, "kinds": ["dimensions"] }),
    )
    .await;
    send_request(
        &mut client,
        "cancel_entry_facts",
        3,
        json!({ "requestId": "c" }),
    )
    .await;

    // Both reply, in whichever order the work settled.
    let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(20);
    let mut replies = std::collections::HashMap::new();
    while replies.len() < 2 {
        let message: Value = match tokio::time::timeout_at(deadline, read_frame(&mut client)).await
        {
            Ok(Ok(payload)) => serde_json::from_slice(&payload).unwrap(),
            _ => panic!("replies so far: {replies:?}"),
        };
        if let Some(id) = message.get("id").and_then(Value::as_u64) {
            replies.insert(id, message);
        }
    }
    assert_eq!(Value::Null, replies[&3]["result"]);
    assert!(replies[&2]["result"]["settled"].as_u64().unwrap() <= 40);
}
