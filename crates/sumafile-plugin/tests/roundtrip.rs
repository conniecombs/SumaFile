#![cfg(windows)]

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use serde_json::{json, Value};
use sumafile_plugin::{
    serve, Args, HostClient, PluginError, PluginHandler, PluginInfo, CANCELLED_MESSAGE,
    EARLY_CANCEL_LIMIT, SERVICE_CONNECTION_ERROR,
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};

struct EchoHandler;

#[async_trait]
impl PluginHandler for EchoHandler {
    async fn handle(
        &self,
        method: &str,
        params: Value,
        host: &HostClient,
    ) -> Result<Value, PluginError> {
        match method {
            "echo" => Ok(params),
            "ask_host" => {
                host.log("info", "x")
                    .await
                    .map_err(PluginError::Application)?;
                Ok(json!({ "logged": true }))
            }
            "read_settings" => {
                let values = host
                    .get_settings(&["k"])
                    .await
                    .map_err(PluginError::Application)?;
                Ok(json!(values))
            }
            "slow_host" => {
                host.set_call_timeout(Duration::from_millis(200));
                host.get_settings(&["k"])
                    .await
                    .map_err(PluginError::Application)?;
                Ok(Value::Null)
            }
            "panic" => panic!("handler exploded on purpose"),
            "slow_progress" => {
                // One progress report, then wait up to `waitMs` for a cancel.
                let operation_id = params["operationId"].as_str().unwrap_or_default();
                let wait = Duration::from_millis(params["waitMs"].as_u64().unwrap_or(0));
                host.archive_progress(operation_id, 1, 2, Some("a"))
                    .map_err(PluginError::Application)?;
                let started = std::time::Instant::now();
                loop {
                    if host.is_cancelled(operation_id) {
                        return Err(PluginError::Application(CANCELLED_MESSAGE.into()));
                    }
                    if started.elapsed() >= wait {
                        return Ok(json!("finished"));
                    }
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            }
            "settings_via_host" => {
                let host = host
                    .host_connection()
                    .ok_or_else(|| PluginError::Application("no host connection".into()))?;
                let values = host
                    .get_settings(&["k"])
                    .await
                    .map_err(PluginError::Application)?;
                Ok(json!(values.get("k").cloned().flatten()))
            }
            "progress_op" => {
                // A plugin's own progress row: begin it, wait at a barrier
                // (a host call the test answers when it is ready), then read
                // the cancel flag of the id the host returned.
                let operation_id = host
                    .progress_begin("Working")
                    .await
                    .map_err(PluginError::Application)?;
                host.get_settings(&["barrier"])
                    .await
                    .map_err(PluginError::Application)?;
                let cancelled = host.is_cancelled(&operation_id);
                if cancelled {
                    host.progress_fail(&operation_id, CANCELLED_MESSAGE)
                        .await
                        .map_err(PluginError::Application)?;
                } else {
                    host.progress_complete(&operation_id)
                        .await
                        .map_err(PluginError::Application)?;
                }
                Ok(json!({ "operationId": operation_id, "cancelled": cancelled }))
            }
            "cancel_flags" => Ok(json!(host.cancel_flag_count())),
            "check_cancel" => Ok(json!(
                host.is_cancelled(params["id"].as_str().unwrap_or_default())
            )),
            "bad_params" => Err(PluginError::InvalidParams("nope".into())),
            "fail" => Err(PluginError::Application("boom".into())),
            _ => Err(PluginError::MethodNotFound),
        }
    }
}

fn unique_pipe_name() -> String {
    static COUNTER: AtomicU32 = AtomicU32::new(0);
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    format!(
        "sumafile-plugin-test-{}-{}-{}",
        std::process::id(),
        nanos,
        COUNTER.fetch_add(1, Ordering::SeqCst)
    )
}

fn info() -> PluginInfo {
    PluginInfo {
        id: "com.example.echo".into(),
        version: "0.3.0".into(),
    }
}

fn start(multi: bool) -> (String, tokio::task::JoinHandle<Result<(), String>>) {
    let pipe_name = unique_pipe_name();
    let args = Args {
        pipe_name: pipe_name.clone(),
        data_dir: std::env::temp_dir(),
        rest: Vec::new(),
    };
    let task = tokio::spawn(serve(args, info(), multi, Arc::new(EchoHandler)));
    (pipe_name, task)
}

async fn connect(pipe_name: &str) -> NamedPipeClient {
    let path = format!(r"\\.\pipe\{pipe_name}");
    for _ in 0..200 {
        if let Ok(client) = ClientOptions::new().open(&path) {
            return client;
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    panic!("could not connect to {path}");
}

async fn send(client: &mut NamedPipeClient, message: Value) {
    let frame = simplefile_ipc::frame::encode_frame(message.to_string().as_bytes()).unwrap();
    client.write_all(&frame).await.unwrap();
    client.flush().await.unwrap();
}

async fn receive(client: &mut NamedPipeClient) -> Value {
    serde_json::from_str(&receive_raw(client).await).unwrap()
}

async fn receive_raw(client: &mut NamedPipeClient) -> String {
    let mut header = [0u8; 4];
    tokio::time::timeout(Duration::from_secs(5), client.read_exact(&mut header))
        .await
        .expect("timed out waiting for a frame")
        .unwrap();
    let length = simplefile_ipc::frame::decode_length(header).unwrap() as usize;
    let mut payload = vec![0u8; length];
    client.read_exact(&mut payload).await.unwrap();
    String::from_utf8(payload).unwrap()
}

async fn call(client: &mut NamedPipeClient, id: u64, method: &str, params: Value) -> Value {
    send(
        client,
        json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }),
    )
    .await;
    let reply = receive(client).await;
    assert_eq!(reply["id"], json!(id), "unexpected frame {reply}");
    reply
}

async fn handshake(client: &mut NamedPipeClient) -> Value {
    call(
        client,
        1,
        "plugin.handshake",
        json!({ "pluginId": "com.example.echo", "hostVersion": "1.0.4", "sdkVersion": "1.0.0" }),
    )
    .await
}

async fn assert_serve_returns(task: tokio::task::JoinHandle<Result<(), String>>) {
    let outcome = tokio::time::timeout(Duration::from_secs(2), task)
        .await
        .expect("serve did not return within 2 s")
        .unwrap();
    assert_eq!(outcome, Ok(()));
}

#[tokio::test]
async fn handshake_echo_and_host_call_round_trip() {
    let (pipe_name, task) = start(true);
    let mut client = connect(&pipe_name).await;

    let reply = handshake(&mut client).await;
    assert_eq!(
        reply["result"],
        json!({ "pluginId": "com.example.echo", "pluginVersion": "0.3.0", "sdkVersion": "1.0.0" })
    );

    let reply = call(&mut client, 2, "echo", json!({ "a": [1, 2, 3] })).await;
    assert_eq!(reply["result"], json!({ "a": [1, 2, 3] }));

    send(
        &mut client,
        json!({ "jsonrpc": "2.0", "id": 3, "method": "ask_host", "params": {} }),
    )
    .await;
    let inbound = receive(&mut client).await;
    assert_eq!(inbound["method"], json!("host.log"));
    assert_eq!(
        inbound["params"],
        json!({ "level": "info", "message": "x" })
    );
    let host_id = inbound["id"].clone();
    assert!(host_id.is_u64(), "host.* ids are numeric: {inbound}");
    send(
        &mut client,
        json!({ "jsonrpc": "2.0", "id": host_id, "result": null }),
    )
    .await;
    let reply = receive(&mut client).await;
    assert_eq!(reply["id"], json!(3));
    assert_eq!(reply["result"], json!({ "logged": true }));

    drop(client);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn methods_before_handshake_are_invalid_requests() {
    let (pipe_name, task) = start(false);
    let mut client = connect(&pipe_name).await;

    let reply = call(&mut client, 7, "echo", json!({})).await;
    assert_eq!(reply["error"]["code"], json!(-32600));

    handshake(&mut client).await;
    let reply = call(&mut client, 8, "echo", json!({ "ok": 1 })).await;
    assert_eq!(reply["result"], json!({ "ok": 1 }));

    drop(client);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn handler_errors_map_to_json_rpc_codes() {
    let (pipe_name, task) = start(false);
    let mut client = connect(&pipe_name).await;
    handshake(&mut client).await;

    let reply = call(&mut client, 2, "bad_params", json!({})).await;
    assert_eq!(reply["error"]["code"], json!(-32602));
    let reply = call(&mut client, 3, "fail", json!({})).await;
    assert_eq!(reply["error"]["code"], json!(-32000));
    assert_eq!(reply["error"]["message"], json!("boom"));
    let reply = call(&mut client, 4, "nothing", json!({})).await;
    assert_eq!(reply["error"]["code"], json!(-32601));

    drop(client);
    assert_serve_returns(task).await;
}

/// Sends a request without waiting for its reply.
async fn send_request(client: &mut NamedPipeClient, id: u64, method: &str, params: Value) {
    send(
        client,
        json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }),
    )
    .await;
}

async fn receive_progress(client: &mut NamedPipeClient, operation_id: &str) {
    let progress = receive(client).await;
    assert_eq!(progress["method"], json!("archive.progress"), "{progress}");
    assert_eq!(progress["params"]["operationId"], json!(operation_id));
}

fn assert_cancelled_reply(reply: &Value, id: u64) {
    assert_eq!(reply["id"], json!(id), "{reply}");
    assert_eq!(reply["error"]["code"], json!(-32000), "{reply}");
    assert_eq!(
        reply["error"]["message"],
        json!(CANCELLED_MESSAGE),
        "{reply}"
    );
}

#[tokio::test]
async fn cancel_on_any_connection_is_seen_by_every_connection() {
    let (pipe_name, task) = start(true);
    let mut host = connect(&pipe_name).await;
    handshake(&mut host).await;
    let mut service = connect(&pipe_name).await;
    service_handshake(&mut service).await;

    // A request on the service connection, cancelled from the host connection.
    send_request(
        &mut service,
        2,
        "slow_progress",
        json!({ "operationId": "op-1", "waitMs": 3000 }),
    )
    .await;
    receive_progress(&mut service, "op-1").await;
    let started = std::time::Instant::now();
    let reply = call(&mut host, 2, "cancel", json!({ "operationId": "op-1" })).await;
    assert_eq!(reply["result"], Value::Null);
    assert!(reply.get("error").is_none(), "{reply}");
    assert_cancelled_reply(&receive(&mut service).await, 2);
    assert!(started.elapsed() < Duration::from_secs(2));

    // And the other way round.
    send_request(
        &mut host,
        3,
        "slow_progress",
        json!({ "operationId": "op-1b", "waitMs": 3000 }),
    )
    .await;
    receive_progress(&mut host, "op-1b").await;
    let reply = call(&mut service, 3, "cancel", json!({ "operationId": "op-1b" })).await;
    assert_eq!(reply["result"], Value::Null);
    assert_cancelled_reply(&receive(&mut host).await, 3);

    drop(host);
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert!(!task.is_finished(), "serve must wait for the last client");
    drop(service);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn cancel_flag_is_dropped_when_its_request_finishes() {
    let (pipe_name, task) = start(true);
    let mut service = connect(&pipe_name).await;
    service_handshake(&mut service).await;
    let mut other = connect(&pipe_name).await;
    service_handshake(&mut other).await;

    send_request(
        &mut service,
        2,
        "slow_progress",
        json!({ "operationId": "op-1", "waitMs": 3000 }),
    )
    .await;
    receive_progress(&mut service, "op-1").await;
    let reply = call(&mut other, 2, "cancel_flags", json!({})).await;
    assert_eq!(reply["result"], json!(1), "op-1 is in flight");

    // A cancel notification on the request's own connection.
    send(
        &mut service,
        json!({ "jsonrpc": "2.0", "method": "cancel", "params": { "operationId": "op-1" } }),
    )
    .await;
    assert_cancelled_reply(&receive(&mut service).await, 2);

    let reply = call(&mut service, 3, "cancel_flags", json!({})).await;
    assert_eq!(reply["result"], json!(0));

    drop(service);
    drop(other);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn cancel_that_overtakes_its_request_still_cancels_it() {
    let (pipe_name, task) = start(true);
    let mut first = connect(&pipe_name).await;
    service_handshake(&mut first).await;
    let mut second = connect(&pipe_name).await;
    service_handshake(&mut second).await;

    let reply = call(&mut second, 2, "cancel", json!({ "operationId": "op-2" })).await;
    assert_eq!(reply["result"], Value::Null);

    let started = std::time::Instant::now();
    send_request(
        &mut first,
        2,
        "slow_progress",
        json!({ "operationId": "op-2", "waitMs": 3000 }),
    )
    .await;
    receive_progress(&mut first, "op-2").await;
    assert_cancelled_reply(&receive(&mut first).await, 2);
    assert!(started.elapsed() < Duration::from_secs(1));

    // The early cancel was used up by its request.
    let reply = call(&mut first, 3, "cancel_flags", json!({})).await;
    assert_eq!(reply["result"], json!(0));

    drop(first);
    drop(second);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn cancels_for_unknown_or_finished_ids_are_bounded() {
    let (pipe_name, task) = start(true);
    let mut client = connect(&pipe_name).await;
    service_handshake(&mut client).await;

    let mut id = 2;
    for i in 0..100 {
        let operation_id = format!("unknown-{i}");
        let reply = call(
            &mut client,
            id,
            "cancel",
            json!({ "operationId": operation_id }),
        )
        .await;
        assert_eq!(reply["result"], Value::Null);
        id += 1;
    }
    let reply = call(&mut client, id, "echo", json!({ "operationId": "fin" })).await;
    assert_eq!(reply["result"], json!({ "operationId": "fin" }));
    id += 1;
    call(&mut client, id, "cancel", json!({ "operationId": "fin" })).await;
    id += 1;

    let reply = call(&mut client, id, "cancel_flags", json!({})).await;
    id += 1;
    assert_eq!(reply["result"], json!(EARLY_CANCEL_LIMIT));
    assert_eq!(EARLY_CANCEL_LIMIT, 64);

    // An early cancel is not a flag: an id that is not in flight reads as
    // not cancelled until a request or progress operation takes it.
    let reply = call(
        &mut client,
        id,
        "check_cancel",
        json!({ "id": "unknown-99" }),
    )
    .await;
    id += 1;
    assert_eq!(reply["result"], json!(false), "{reply}");

    // The oldest early cancels fell off; a recent one still applies.
    send_request(
        &mut client,
        id,
        "slow_progress",
        json!({ "operationId": "unknown-0", "waitMs": 0 }),
    )
    .await;
    receive_progress(&mut client, "unknown-0").await;
    let reply = receive(&mut client).await;
    assert_eq!(reply["result"], json!("finished"), "{reply}");
    id += 1;
    send_request(
        &mut client,
        id,
        "slow_progress",
        json!({ "operationId": "unknown-99", "waitMs": 3000 }),
    )
    .await;
    receive_progress(&mut client, "unknown-99").await;
    assert_cancelled_reply(&receive(&mut client).await, id);

    drop(client);
    assert_serve_returns(task).await;
}

/// Answers the host request the backend sent next on `host`, which must be
/// `method`, with `result`; returns its params.
async fn answer_host_request(client: &mut NamedPipeClient, method: &str, result: Value) -> Value {
    let inbound = receive(client).await;
    assert_eq!(inbound["method"], json!(method), "{inbound}");
    let host_id = inbound["id"].clone();
    send(
        client,
        json!({ "jsonrpc": "2.0", "id": host_id, "result": result }),
    )
    .await;
    inbound["params"].clone()
}

#[tokio::test]
async fn cancel_for_a_progress_begin_id_reaches_the_handler() {
    let (pipe_name, task) = start(true);
    let mut host = connect(&pipe_name).await;
    handshake(&mut host).await;

    // No request carries the id: the host hands it out from progress_begin
    // and cancels it when the user cancels the plugin's progress row.
    send_request(&mut host, 2, "progress_op", json!({})).await;
    answer_host_request(
        &mut host,
        "host.progress_begin",
        json!({ "operationId": "row-1" }),
    )
    .await;
    let barrier = receive(&mut host).await;
    assert_eq!(barrier["method"], json!("host.get_settings"), "{barrier}");
    let reply = call(&mut host, 3, "cancel", json!({ "operationId": "row-1" })).await;
    assert_eq!(reply["result"], Value::Null);
    send(
        &mut host,
        json!({ "jsonrpc": "2.0", "id": barrier["id"], "result": { "values": {} } }),
    )
    .await;
    let params = answer_host_request(&mut host, "host.progress_fail", Value::Null).await;
    assert_eq!(params["operationId"], json!("row-1"));
    let reply = receive(&mut host).await;
    assert_eq!(reply["id"], json!(2), "{reply}");
    assert_eq!(
        reply["result"],
        json!({ "operationId": "row-1", "cancelled": true })
    );

    // progress_fail ended the operation: nothing is left behind.
    let reply = call(&mut host, 4, "cancel_flags", json!({})).await;
    assert_eq!(reply["result"], json!(0));

    drop(host);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn progress_begin_ids_are_in_flight_until_complete_or_fail() {
    let (pipe_name, task) = start(true);
    let mut host = connect(&pipe_name).await;
    handshake(&mut host).await;
    let mut service = connect(&pipe_name).await;
    service_handshake(&mut service).await;

    send_request(&mut host, 2, "progress_op", json!({})).await;
    answer_host_request(
        &mut host,
        "host.progress_begin",
        json!({ "operationId": "row-2" }),
    )
    .await;
    let barrier = receive(&mut host).await;
    assert_eq!(barrier["method"], json!("host.get_settings"), "{barrier}");
    let reply = call(&mut service, 2, "cancel_flags", json!({})).await;
    assert_eq!(reply["result"], json!(1), "row-2 is in flight");

    let reply = call(&mut host, 3, "cancel", json!({ "operationId": "row-2" })).await;
    assert_eq!(reply["result"], Value::Null);
    // More early cancels than the limit, from the service, must not evict
    // the cancel of a running progress operation.
    for i in 0..(EARLY_CANCEL_LIMIT as u64 + 10) {
        let operation_id = format!("unknown-{i}");
        let reply = call(
            &mut service,
            3 + i,
            "cancel",
            json!({ "operationId": operation_id }),
        )
        .await;
        assert_eq!(reply["result"], Value::Null);
    }
    send(
        &mut host,
        json!({ "jsonrpc": "2.0", "id": barrier["id"], "result": { "values": {} } }),
    )
    .await;
    let params = answer_host_request(&mut host, "host.progress_fail", Value::Null).await;
    assert_eq!(params["operationId"], json!("row-2"));
    let reply = receive(&mut host).await;
    assert_eq!(
        reply["result"],
        json!({ "operationId": "row-2", "cancelled": true }),
        "{reply}"
    );

    // Only the capped early cancels are left.
    let reply = call(&mut service, 1000, "cancel_flags", json!({})).await;
    assert_eq!(reply["result"], json!(EARLY_CANCEL_LIMIT));

    // An uncancelled operation completes, and closing the connection ends
    // any operation it left open.
    send_request(&mut host, 4, "progress_op", json!({})).await;
    answer_host_request(
        &mut host,
        "host.progress_begin",
        json!({ "operationId": "row-3" }),
    )
    .await;
    answer_host_request(&mut host, "host.get_settings", json!({ "values": {} })).await;
    let params = answer_host_request(&mut host, "host.progress_complete", Value::Null).await;
    assert_eq!(params["operationId"], json!("row-3"));
    let reply = receive(&mut host).await;
    assert_eq!(
        reply["result"],
        json!({ "operationId": "row-3", "cancelled": false }),
        "{reply}"
    );

    send_request(&mut host, 5, "progress_op", json!({})).await;
    answer_host_request(
        &mut host,
        "host.progress_begin",
        json!({ "operationId": "row-4" }),
    )
    .await;
    let barrier = receive(&mut host).await;
    assert_eq!(barrier["method"], json!("host.get_settings"), "{barrier}");
    let reply = call(&mut service, 1001, "cancel_flags", json!({})).await;
    assert_eq!(
        reply["result"],
        json!(EARLY_CANCEL_LIMIT + 1),
        "row-4 is in flight"
    );
    drop(host);
    let mut count = Value::Null;
    for attempt in 0..100 {
        count =
            call(&mut service, 1002 + attempt, "cancel_flags", json!({})).await["result"].clone();
        if count == json!(EARLY_CANCEL_LIMIT) {
            break;
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    assert_eq!(
        count,
        json!(EARLY_CANCEL_LIMIT),
        "closing the host ends row-4"
    );

    drop(service);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn archive_progress_is_sent_on_the_service_connection() {
    let (pipe_name, task) = start(true);
    let mut service = connect(&pipe_name).await;
    service_handshake(&mut service).await;

    send_request(
        &mut service,
        2,
        "slow_progress",
        json!({ "operationId": "op-1", "waitMs": 0 }),
    )
    .await;
    let progress = receive(&mut service).await;
    assert_eq!(
        progress,
        json!({
            "jsonrpc": "2.0",
            "method": "archive.progress",
            "params": { "operationId": "op-1", "current": 1, "total": 2, "currentItem": "a" }
        })
    );
    let reply = receive(&mut service).await;
    assert_eq!(reply["id"], json!(2));
    assert_eq!(reply["result"], json!("finished"));

    drop(service);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn host_connection_reaches_the_host_from_a_service_connection_handler() {
    let (pipe_name, task) = start(true);
    let mut host = connect(&pipe_name).await;
    handshake(&mut host).await;
    let mut service = connect(&pipe_name).await;
    service_handshake(&mut service).await;

    send_request(&mut service, 2, "settings_via_host", json!({})).await;
    let inbound = receive(&mut host).await;
    assert_eq!(inbound["method"], json!("host.get_settings"), "{inbound}");
    assert_eq!(inbound["params"], json!({ "keys": ["k"] }));
    let host_id = inbound["id"].clone();
    send(
        &mut host,
        json!({ "jsonrpc": "2.0", "id": host_id, "result": { "values": { "k": "v" } } }),
    )
    .await;
    let reply = receive(&mut service).await;
    assert_eq!(reply["id"], json!(2), "{reply}");
    assert_eq!(reply["result"], json!("v"), "{reply}");

    drop(host);
    drop(service);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn host_connection_is_none_without_a_host_client() {
    let (pipe_name, task) = start(true);
    // A client that never handshakes is not a host connection.
    let keep_alive = connect(&pipe_name).await;
    let mut service = connect(&pipe_name).await;
    service_handshake(&mut service).await;

    let started = std::time::Instant::now();
    let reply = call(&mut service, 2, "settings_via_host", json!({})).await;
    assert!(started.elapsed() < Duration::from_secs(1));
    assert_eq!(reply["error"]["code"], json!(-32000), "{reply}");
    assert_eq!(reply["error"]["message"], json!("no host connection"));

    drop(keep_alive);
    drop(service);
    assert_serve_returns(task).await;
}

async fn service_handshake(client: &mut NamedPipeClient) -> Value {
    call(
        client,
        1,
        "plugin.handshake",
        json!({
            "pluginId": "com.example.echo",
            "hostVersion": "1.0.4",
            "sdkVersion": "1.0.0",
            "client": "simplefile-service"
        }),
    )
    .await
}

#[tokio::test]
async fn service_connection_fails_host_calls_fast_and_logs_locally() {
    let (pipe_name, task) = start(true);
    let mut client = connect(&pipe_name).await;
    service_handshake(&mut client).await;

    let started = std::time::Instant::now();
    // `call` asserts the next frame is the reply, so no host.* request was sent.
    let reply = call(&mut client, 2, "read_settings", json!({})).await;
    assert!(started.elapsed() < Duration::from_secs(1));
    assert_eq!(reply["error"]["code"], json!(-32000));
    assert_eq!(reply["error"]["message"], json!(SERVICE_CONNECTION_ERROR));

    let reply = call(&mut client, 3, "ask_host", json!({})).await;
    assert_eq!(reply["result"], json!({ "logged": true }));

    drop(client);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn unanswered_host_call_times_out() {
    let (pipe_name, task) = start(false);
    let mut client = connect(&pipe_name).await;
    handshake(&mut client).await;

    send(
        &mut client,
        json!({ "jsonrpc": "2.0", "id": 2, "method": "slow_host", "params": {} }),
    )
    .await;
    let inbound = receive(&mut client).await;
    assert_eq!(inbound["method"], json!("host.get_settings"));
    // Never answered.
    let reply = receive(&mut client).await;
    assert_eq!(reply["id"], json!(2));
    assert_eq!(reply["error"]["code"], json!(-32000));
    let message = reply["error"]["message"].as_str().unwrap();
    assert!(
        message.contains("host.get_settings") && message.contains("timed out"),
        "{message}"
    );

    drop(client);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn panicking_handler_is_answered_and_connection_survives() {
    let (pipe_name, task) = start(false);
    let mut client = connect(&pipe_name).await;
    handshake(&mut client).await;

    let reply = call(&mut client, 2, "panic", json!({})).await;
    assert_eq!(reply["error"]["code"], json!(-32603));
    assert_eq!(reply["error"]["message"], json!("plugin handler panicked"));
    let reply = call(&mut client, 3, "echo", json!({ "still": "here" })).await;
    assert_eq!(reply["result"], json!({ "still": "here" }));

    drop(client);
    assert_serve_returns(task).await;
}

#[tokio::test]
async fn malformed_frame_gets_parse_error_with_null_id() {
    let (pipe_name, task) = start(false);
    let mut client = connect(&pipe_name).await;

    let frame = simplefile_ipc::frame::encode_frame(b"{not json").unwrap();
    client.write_all(&frame).await.unwrap();
    client.flush().await.unwrap();
    let raw = receive_raw(&mut client).await;
    assert!(raw.contains(r#""id":null"#), "{raw}");
    let reply: Value = serde_json::from_str(&raw).unwrap();
    assert_eq!(reply["error"]["code"], json!(-32700));

    drop(client);
    assert_serve_returns(task).await;
}

#[test]
fn args_parse_round_trip_and_missing_pipe_name() {
    let parsed = Args::parse(
        [
            "--pipe-name",
            "sumafile-plugin-x",
            "--data-dir",
            r"C:\data\x",
            "--verbose",
        ]
        .into_iter()
        .map(String::from),
    )
    .unwrap();
    assert_eq!(parsed.pipe_name, "sumafile-plugin-x");
    assert_eq!(parsed.data_dir, std::path::PathBuf::from(r"C:\data\x"));
    assert_eq!(parsed.rest, vec!["--verbose".to_string()]);

    let error =
        Args::parse(["--data-dir", r"C:\data\x"].into_iter().map(String::from)).unwrap_err();
    assert!(error.contains("--pipe-name"), "{error}");
}

#[cfg(feature = "test-support")]
#[tokio::test]
async fn fake_host_answers_from_memory() {
    let host = HostClient::fake(
        [("token".to_string(), "abc".to_string())]
            .into_iter()
            .collect(),
    );
    let values = host.get_settings(&["token", "missing"]).await.unwrap();
    assert_eq!(values["token"], Some("abc".to_string()));
    assert_eq!(values["missing"], None);

    host.set_setting("missing", "now").await.unwrap();
    let values = host.get_settings(&["missing"]).await.unwrap();
    assert_eq!(values["missing"], Some("now".to_string()));

    host.log("warn", "careful").await.unwrap();
    assert_eq!(
        host.fake_log(),
        vec![("warn".to_string(), "careful".to_string())]
    );

    let id = host.progress_begin("Working").await.unwrap();
    assert!(!id.is_empty());
    host.progress_report(&id, 1, 2, Some("half")).await.unwrap();
    host.progress_complete(&id).await.unwrap();
    host.notify("done", false).await.unwrap();
    host.emit("event", json!({})).await.unwrap();
    assert!(!host.is_cancelled(&id));
}

#[cfg(feature = "test-support")]
#[tokio::test]
async fn fake_host_records_progress_cancels_and_is_its_own_host_connection() {
    let host = HostClient::fake(Default::default());
    host.archive_progress("op-f", 3, 9, Some("x")).unwrap();
    host.archive_progress("op-f", 9, 9, None).unwrap();
    assert_eq!(
        host.fake_progress(),
        vec![
            ("op-f".to_string(), 3, 9, Some("x".to_string())),
            ("op-f".to_string(), 9, 9, None),
        ]
    );
    assert!(!host.is_cancelled("op-f"));
    host.fake_cancel("op-f");
    assert!(host.is_cancelled("op-f"));
    assert!(!host.is_cancelled("op-g"));
    let same = host
        .host_connection()
        .expect("a fake host is its own host connection");
    assert!(same.is_cancelled("op-f"));
}

#[cfg(feature = "test-support")]
#[tokio::test]
async fn fake_host_records_progress_calls_and_events() {
    let host = HostClient::fake(Default::default());
    let first = host.progress_begin("Uploading 1 item").await.unwrap();
    assert_eq!(first, "fake-operation-1");
    host.progress_report(&first, 5, 10, Some("1/2 files · a.txt"))
        .await
        .unwrap();
    host.progress_report(&first, 10, 10, None).await.unwrap();
    host.progress_complete(&first).await.unwrap();
    let second = host.progress_begin("Downloading").await.unwrap();
    assert_eq!(second, "fake-operation-2");
    host.progress_fail(&second, "Transfer cancelled")
        .await
        .unwrap();
    assert_eq!(
        host.fake_progress_calls(),
        vec![
            "begin Uploading 1 item".to_string(),
            "report fake-operation-1 5/10 1/2 files · a.txt".to_string(),
            "report fake-operation-1 10/10".to_string(),
            "complete fake-operation-1".to_string(),
            "begin Downloading".to_string(),
            "fail fake-operation-2 Transfer cancelled".to_string(),
        ]
    );

    host.emit("remote.profiles-changed", json!({}))
        .await
        .unwrap();
    host.emit("remote.transfer-progress", json!({ "current": 1 }))
        .await
        .unwrap();
    assert_eq!(
        host.fake_events(),
        vec![
            ("remote.profiles-changed".to_string(), json!({})),
            (
                "remote.transfer-progress".to_string(),
                json!({ "current": 1 })
            ),
        ]
    );
}
