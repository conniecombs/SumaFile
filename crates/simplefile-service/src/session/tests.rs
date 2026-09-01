use super::io::{read_frame, writer_loop, OutboundFrame, OUTBOUND_QUEUE_CAPACITY};
use super::*;
use serde_json::{json, Value};
use simplefile_ipc::frame::{decode_length, encode_frame};
use simplefile_ipc::rpc::JsonRpcRequest;
use simplefile_ipc::{
    BINARY_FRAME_MAGIC, BINARY_LIST_DIRECTORY_CHUNK, BINARY_LIST_DIRECTORY_RESULT,
    HANDSHAKE_METHOD, HEALTH_METHOD, LIST_DIRECTORY_CHUNK, PROTOCOL_VERSION,
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
