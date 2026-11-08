#![cfg(windows)]

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use serde_json::{json, Value};
use sumafile_plugin::{serve, Args, HostClient, PluginError, PluginHandler, PluginInfo};
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
            "check_cancel" => Ok(json!(host.is_cancelled("op-1"))),
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
    let mut header = [0u8; 4];
    tokio::time::timeout(Duration::from_secs(5), client.read_exact(&mut header))
        .await
        .expect("timed out waiting for a frame")
        .unwrap();
    let length = simplefile_ipc::frame::decode_length(header).unwrap() as usize;
    let mut payload = vec![0u8; length];
    client.read_exact(&mut payload).await.unwrap();
    serde_json::from_slice(&payload).unwrap()
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

#[tokio::test]
async fn cancel_sets_a_per_connection_flag() {
    let (pipe_name, task) = start(true);
    let mut first = connect(&pipe_name).await;
    handshake(&mut first).await;
    let mut second = connect(&pipe_name).await;
    let reply = call(
        &mut second,
        1,
        "plugin.handshake",
        json!({
            "pluginId": "com.example.echo",
            "hostVersion": "1.0.4",
            "sdkVersion": "1.0.0",
            "client": "simplefile-service"
        }),
    )
    .await;
    assert_eq!(reply["result"]["pluginId"], json!("com.example.echo"));

    let reply = call(&mut first, 2, "check_cancel", json!({})).await;
    assert_eq!(reply["result"], json!(false));
    let reply = call(&mut first, 3, "cancel", json!({ "operationId": "op-1" })).await;
    assert_eq!(reply["result"], Value::Null);
    assert!(reply.get("error").is_none());
    let reply = call(&mut first, 4, "check_cancel", json!({})).await;
    assert_eq!(reply["result"], json!(true));

    // The service connection keeps its own flags and reaches the same handler.
    let reply = call(&mut second, 2, "check_cancel", json!({})).await;
    assert_eq!(reply["result"], json!(false));

    drop(first);
    tokio::time::sleep(Duration::from_millis(100)).await;
    assert!(!task.is_finished(), "serve must wait for the last client");
    drop(second);
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
