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

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn remote_transfer_accepts_health_and_cancel_while_streaming_and_cleans_partial_file() {
    use crate::remote::provider::{RemoteProvider, RemoteProviderFactory};
    use simplefile_core::models::{DirectoryListing, FileEntry};
    struct SlowProvider;
    impl RemoteProvider for SlowProvider {
        fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
            Ok(DirectoryListing {
                path: path.into(),
                parent: None,
                is_network: true,
                entries: vec![FileEntry {
                    name: "slow.bin".into(),
                    path: "/slow.bin".into(),
                    is_dir: false,
                    is_symlink: false,
                    is_hidden: false,
                    is_system: false,
                    size: 1000000,
                    modified: "".into(),
                    extension: "bin".into(),
                    permissions: None,
                    symlink_target: None,
                    git_status: None,
                }],
            })
        }
        fn create_directory(&mut self, _: &str, _: &str) -> Result<FileEntry, String> {
            unreachable!()
        }
        fn rename_entry(&mut self, _: &str, _: &str) -> Result<FileEntry, String> {
            unreachable!()
        }
        fn delete_entries(&mut self, _: &[String]) -> Result<Vec<String>, String> {
            unreachable!()
        }
        fn download(
            &mut self,
            _: &str,
            destination: &mut dyn std::io::Write,
        ) -> Result<u64, String> {
            for _ in 0..1000 {
                destination
                    .write_all(&[1; 1024])
                    .map_err(|e| e.to_string())?;
                std::thread::sleep(std::time::Duration::from_millis(5));
            }
            Ok(1024000)
        }
        fn upload(&mut self, _: &str, _: &mut dyn std::io::Read) -> Result<u64, String> {
            unreachable!()
        }
    }
    struct Factory;
    impl RemoteProviderFactory for Factory {
        fn connect(
            &self,
            _: &simplefile_core::remote::RemoteProfile,
            _: Option<&crate::remote::secrets::RemoteSecret>,
        ) -> Result<Box<dyn RemoteProvider>, String> {
            Ok(Box::new(SlowProvider))
        }
    }
    let destination = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
    std::fs::create_dir(&destination).unwrap();
    let mut state = SessionState::with_remote_provider_factory(Factory);
    state.handshake_done = true;
    let profile = serde_json::from_value(json!({ "id":"test", "name":"test", "protocol":"sftp", "host":"localhost", "port":22,
        "username":"test", "root_path":"/", "auth_kind":"password", "insecure_plain_ftp":false, "passive_mode":true })).unwrap();
    let session = state
        .remote_sessions
        .connect_profile(profile, None)
        .unwrap();
    let (mut client, server) = duplex(64 * 1024);
    let (reader, writer) = tokio::io::split(server);
    let serving = tokio::spawn(serve_connection(reader, writer, state));
    send_request(&mut client, "remote_transfer_entries", 1, json!({ "remoteSessionId":session.session_id, "direction":"download",
        "sources":["/slow.bin"], "destination":destination.to_string_lossy(), "conflictAction":"replace", "operationId":"remote-cancel-test" })).await;
    // Wait until the transfer has started, then exercise the same IPC reader.
    let notification: Value =
        serde_json::from_slice(&read_frame(&mut client).await.unwrap()).unwrap();
    assert_eq!(notification["method"], "operation-progress");
    send_request(&mut client, HEALTH_METHOD, 2, json!({})).await;
    send_request(
        &mut client,
        "cancel_operation",
        3,
        json!({ "operationId":"remote-cancel-test" }),
    )
    .await;
    let responses = tokio::time::timeout(std::time::Duration::from_secs(3), async {
        let mut replies = std::collections::HashMap::new();
        while replies.len() < 3 {
            let reply: Value =
                serde_json::from_slice(&read_frame(&mut client).await.unwrap()).unwrap();
            if let Some(id) = reply["id"].as_u64() {
                replies.insert(id, reply);
            }
        }
        replies
    })
    .await
    .unwrap();
    assert!(responses[&2].get("error").is_none());
    assert_eq!(responses[&3]["result"], true);
    assert_eq!(responses[&1]["result"]["cancelled"], true);
    assert_eq!(
        responses[&1]["result"]["retry_sources"],
        json!(["/slow.bin"])
    );
    assert_eq!(std::fs::read_dir(&destination).unwrap().count(), 0);
    drop(client);
    serving.await.unwrap().unwrap();
    std::fs::remove_dir(&destination).unwrap();
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
