//! A plugin backend serves archive formats over its own named pipe; the
//! service connects as a client once the host registers it.
#![cfg(windows)]

use serde_json::{json, Value};
use simplefile_core::archive::ArchiveEngine;
use simplefile_ipc::frame::{decode_length, encode_frame};
use simplefile_ipc::rpc::JsonRpcRequest;
use simplefile_ipc::{HANDSHAKE_METHOD, PROTOCOL_VERSION};
use simplefile_service::plugin_archive::{PipeArchiveEngine, PluginConnection, PluginTimeouts};
use simplefile_service::{serve_connection, SessionState};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::io::{duplex, AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, DuplexStream};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};

/// The engine registry is process-wide and these tests share `.hello`, so
/// they take turns.
static REGISTRY_LOCK: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

fn nonce() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("clock after epoch")
        .as_nanos()
}

async fn read_message<R: AsyncRead + Unpin>(reader: &mut R) -> Option<Value> {
    let mut header = [0u8; 4];
    reader.read_exact(&mut header).await.ok()?;
    let length = decode_length(header).ok()? as usize;
    let mut payload = vec![0u8; length];
    reader.read_exact(&mut payload).await.ok()?;
    serde_json::from_slice(&payload).ok()
}

async fn write_message<W: AsyncWrite + Unpin>(writer: &mut W, message: &Value) {
    let payload = serde_json::to_vec(message).unwrap();
    writer
        .write_all(&encode_frame(&payload).unwrap())
        .await
        .unwrap();
}

/// What the fake plugin saw, for assertions.
#[derive(Default)]
struct FakeLog {
    handshakes: AtomicUsize,
    disconnects: AtomicUsize,
    calls: Mutex<Vec<(String, Value)>>,
}

/// Act as a plugin backend on `pipe_name`: answer the handshake and the
/// archive methods. An `archive.list` for a path containing `drop-me`
/// closes the connection without replying, to simulate a crash; one for a
/// path containing `hang-me` is never answered while the connection stays
/// open. The server keeps accepting new connections.
fn spawn_fake_plugin(pipe_name: &str) -> Arc<FakeLog> {
    let path = format!(r"\\.\pipe\{pipe_name}");
    let log = Arc::new(FakeLog::default());
    let mut server = ServerOptions::new()
        .first_pipe_instance(true)
        .create(&path)
        .expect("create fake plugin pipe");
    let task_log = log.clone();
    tokio::spawn(async move {
        loop {
            if server.connect().await.is_err() {
                return;
            }
            let connected = server;
            server = match ServerOptions::new().create(&path) {
                Ok(next) => next,
                Err(_) => return,
            };
            tokio::spawn(serve_fake_client(connected, task_log.clone()));
        }
    });
    log
}

async fn serve_fake_client(mut pipe: NamedPipeServer, log: Arc<FakeLog>) {
    while let Some(request) = read_message(&mut pipe).await {
        let method = request["method"].as_str().unwrap_or_default().to_string();
        let params = request["params"].clone();
        log.calls
            .lock()
            .unwrap()
            .push((method.clone(), params.clone()));
        let result = match method.as_str() {
            "plugin.handshake" => {
                log.handshakes.fetch_add(1, Ordering::SeqCst);
                json!({ "sdkVersion": "1.0.0" })
            }
            "archive.list" => {
                if params["archivePath"]
                    .as_str()
                    .unwrap_or_default()
                    .contains("drop-me")
                {
                    break;
                }
                if params["archivePath"]
                    .as_str()
                    .unwrap_or_default()
                    .contains("hang-me")
                {
                    continue;
                }
                json!({
                    "entries": [
                        { "path": "hello/world.txt", "isDir": false, "size": 11, "compressedSize": 5 },
                        { "path": "hello", "isDir": true, "size": 0, "compressedSize": 0 }
                    ]
                })
            }
            "archive.extract" => {
                let destination = params["destination"].as_str().unwrap_or_default();
                std::fs::write(
                    std::path::Path::new(destination).join("extracted.txt"),
                    b"from the fake plugin",
                )
                .unwrap();
                Value::Null
            }
            other => {
                write_message(
                    &mut pipe,
                    &json!({
                        "jsonrpc": "2.0",
                        "id": request["id"],
                        "error": { "code": -32601, "message": format!("no {other}") }
                    }),
                )
                .await;
                continue;
            }
        };
        write_message(
            &mut pipe,
            &json!({ "jsonrpc": "2.0", "id": request["id"], "result": result }),
        )
        .await;
    }
    log.disconnects.fetch_add(1, Ordering::SeqCst);
}

struct Service {
    client: DuplexStream,
    next_id: u64,
    server: tokio::task::JoinHandle<Result<(), String>>,
}

impl Service {
    async fn start() -> Self {
        let (client, server) = duplex(64 * 1024);
        let (server_read, server_write) = tokio::io::split(server);
        let server = tokio::spawn(serve_connection(
            server_read,
            server_write,
            SessionState {
                expected_token: Some("dev".to_string()),
                ..SessionState::default()
            },
        ));
        let mut service = Self {
            client,
            next_id: 0,
            server,
        };
        let handshake = service
            .call(
                HANDSHAKE_METHOD,
                json!({
                    "protocolVersion": PROTOCOL_VERSION,
                    "clientName": "plugin-archive-test",
                    "authToken": "dev"
                }),
            )
            .await;
        assert_eq!(handshake["result"]["protocolVersion"], PROTOCOL_VERSION);
        service
    }

    async fn call(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let request = JsonRpcRequest {
            jsonrpc: "2.0".into(),
            id: Some(json!(self.next_id)),
            method: method.into(),
            params: Some(params),
        };
        let payload = serde_json::to_vec(&request).unwrap();
        self.client
            .write_all(&encode_frame(&payload).unwrap())
            .await
            .unwrap();
        loop {
            let message = read_message(&mut self.client).await.expect("service reply");
            if message["id"] == json!(self.next_id) {
                return message;
            }
        }
    }

    async fn stop(mut self) {
        let _ = self.call("ipc.shutdown", json!({})).await;
        let _ = self.server.await;
    }
}

/// Unregisters the plugin's formats even when an assertion fails.
struct Unregister(String);

impl Drop for Unregister {
    fn drop(&mut self) {
        simplefile_core::archive::unregister_engine(&self.0);
    }
}

fn hello_format() -> Value {
    json!([{
        "extension": ".hello",
        "canList": true,
        "canExtract": true,
        "canCreate": false,
        "canModify": false,
        "engineName": "Hello Engine"
    }])
}

fn temp_dir(name: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!("simplefile-plugin-archive-{name}-{}", nonce()));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn registered_plugin_engine_lists_and_extracts_hello_archives() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let plugin_id = "com.test.hello";
    let _unregister = Unregister(plugin_id.to_string());
    let mut service = Service::start().await;

    let registered = service
        .call(
            "register_archive_engine",
            json!({ "pluginId": plugin_id, "pipeName": pipe_name, "formats": hello_format() }),
        )
        .await;
    assert_eq!(registered["result"], Value::Null, "{registered}");
    assert!(registered.get("error").is_none(), "{registered}");
    assert_eq!(log.handshakes.load(Ordering::SeqCst), 1);
    let handshake = log.calls.lock().unwrap()[0].clone();
    assert_eq!(handshake.0, "plugin.handshake");
    assert_eq!(handshake.1["pluginId"], plugin_id);
    assert_eq!(handshake.1["sdkVersion"], "1.0.0");
    assert_eq!(handshake.1["client"], "simplefile-service");
    assert_eq!(
        handshake.1["hostVersion"],
        simplefile_core::APP_DISPLAY_VERSION
    );

    let root = temp_dir("list");
    let archive = root.join("x.hello");
    std::fs::write(&archive, b"not really an archive").unwrap();
    let listing = service
        .call("list_archive", json!({ "path": archive.to_string_lossy() }))
        .await;
    let entries = listing["result"]["entries"]
        .as_array()
        .unwrap_or_else(|| panic!("entries in {listing}"));
    assert_eq!(listing["result"]["format"], "hello");
    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0]["path"], "hello/world.txt");
    assert_eq!(entries[0]["name"], "world.txt");
    assert_eq!(entries[0]["size"], 11);
    assert_eq!(entries[0]["compressed_size"], 5);
    assert_eq!(entries[1]["is_dir"], true);

    let destination = root.join("out");
    let extracted = service
        .call(
            "extract_archive",
            json!({
                "archivePath": archive.to_string_lossy(),
                "destination": destination.to_string_lossy()
            }),
        )
        .await;
    assert!(extracted.get("error").is_none(), "{extracted}");
    assert_eq!(
        std::fs::read(destination.join("extracted.txt")).unwrap(),
        b"from the fake plugin"
    );

    let capabilities = service.call("get_archive_capabilities", json!({})).await;
    let hello = capabilities["result"]["formats"]
        .as_array()
        .unwrap()
        .iter()
        .find(|format| format["extension"] == ".hello")
        .cloned()
        .expect("hello format listed");
    assert_eq!(hello["engine"], format!("plugin:{plugin_id}"));
    assert_eq!(hello["can_create"], false);

    let unregistered = service
        .call(
            "unregister_archive_engine",
            json!({ "pluginId": plugin_id }),
        )
        .await;
    assert_eq!(unregistered["result"], Value::Null);
    let after = service
        .call("list_archive", json!({ "path": archive.to_string_lossy() }))
        .await;
    assert!(after.get("error").is_some(), "{after}");

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn register_archive_engine_with_unserved_pipe_registers_nothing() {
    let _lock = REGISTRY_LOCK.lock().await;
    let plugin_id = "com.test.nobody";
    let _unregister = Unregister(plugin_id.to_string());
    let mut service = Service::start().await;

    let registered = service
        .call(
            "register_archive_engine",
            json!({
                "pluginId": plugin_id,
                "pipeName": format!("simplefile-test-nobody-{}", nonce()),
                "formats": hello_format()
            }),
        )
        .await;
    assert!(registered.get("error").is_some(), "{registered}");
    assert!(registered["error"]["message"].as_str().unwrap().len() > 1);

    let capabilities = service.call("get_archive_capabilities", json!({})).await;
    assert!(!capabilities["result"]["formats"]
        .as_array()
        .unwrap()
        .iter()
        .any(|format| format["extension"] == ".hello"));

    service.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn plugin_engine_reconnects_once_after_a_transport_error() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-reconnect-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let plugin_id = "com.test.reconnect";
    let _unregister = Unregister(plugin_id.to_string());
    let mut service = Service::start().await;
    let registered = service
        .call(
            "register_archive_engine",
            json!({ "pluginId": plugin_id, "pipeName": pipe_name, "formats": hello_format() }),
        )
        .await;
    assert!(registered.get("error").is_none(), "{registered}");

    let root = temp_dir("reconnect");
    let crashing = root.join("drop-me.hello");
    std::fs::write(&crashing, b"x").unwrap();
    let failed = service
        .call(
            "list_archive",
            json!({ "path": crashing.to_string_lossy() }),
        )
        .await;
    assert!(failed.get("error").is_some(), "{failed}");

    let archive = root.join("fine.hello");
    std::fs::write(&archive, b"x").unwrap();
    let listing = service
        .call("list_archive", json!({ "path": archive.to_string_lossy() }))
        .await;
    assert!(listing.get("error").is_none(), "{listing}");
    assert_eq!(listing["result"]["entries"].as_array().unwrap().len(), 2);
    assert_eq!(log.handshakes.load(Ordering::SeqCst), 2);

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

fn short_timeouts(list: Duration, lock: Duration) -> PluginTimeouts {
    PluginTimeouts {
        handshake: Duration::from_secs(2),
        list,
        transfer: Duration::from_secs(2),
        lock,
    }
}

async fn wait_until(what: &str, condition: impl Fn() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(5);
    while !condition() {
        assert!(Instant::now() < deadline, "timed out waiting for {what}");
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn plugin_call_that_never_answers_times_out_and_the_next_call_reconnects() {
    let pipe_name = format!("simplefile-test-plugin-hang-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let root = temp_dir("hang");
    let hanging = root.join("hang-me.hello");
    let fine = root.join("fine.hello");

    let task_log = log.clone();
    let task_pipe = pipe_name.clone();
    let (elapsed, error, entries) = tokio::task::spawn_blocking(move || {
        let connection = PluginConnection::connect_with_timeouts(
            "com.test.hang",
            &task_pipe,
            short_timeouts(Duration::from_millis(400), Duration::from_secs(2)),
        )
        .expect("connect to fake plugin");
        let engine = PipeArchiveEngine::new("Hang Engine", connection);
        let started = Instant::now();
        let error = engine
            .list_entries(&hanging)
            .expect_err("an unanswered call fails");
        let elapsed = started.elapsed();
        assert_eq!(task_log.handshakes.load(Ordering::SeqCst), 1);
        let entries = engine.list_entries(&fine).expect("next call reconnects");
        (elapsed, error, entries)
    })
    .await
    .unwrap();

    assert!(error.contains("archive.list"), "{error}");
    assert!(error.contains("400ms"), "{error}");
    assert!(elapsed >= Duration::from_millis(400), "{elapsed:?}");
    assert!(elapsed < Duration::from_millis(1500), "{elapsed:?}");
    assert_eq!(entries.len(), 2);
    assert_eq!(log.handshakes.load(Ordering::SeqCst), 2);
    // The stuck connection was really closed, not leaked.
    wait_until("the timed-out connection to close", || {
        log.disconnects.load(Ordering::SeqCst) >= 1
    })
    .await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn second_caller_on_a_busy_plugin_gets_a_busy_error_within_its_bound() {
    let pipe_name = format!("simplefile-test-plugin-busy-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let root = temp_dir("busy");
    let hanging = root.join("hang-me.hello");
    let fine = root.join("fine.hello");

    let task_pipe = pipe_name.clone();
    let task_log = log.clone();
    tokio::task::spawn_blocking(move || {
        let connection = PluginConnection::connect_with_timeouts(
            "com.test.busy",
            &task_pipe,
            short_timeouts(Duration::from_millis(1500), Duration::from_millis(300)),
        )
        .expect("connect to fake plugin");
        let first = PipeArchiveEngine::new("Busy Engine", connection.clone());
        let second = PipeArchiveEngine::new("Busy Engine", connection);

        let holder = std::thread::spawn(move || first.list_entries(&hanging));
        // Wait until the first call is in flight and holds the connection.
        let in_flight = Instant::now() + Duration::from_secs(5);
        while !task_log
            .calls
            .lock()
            .unwrap()
            .iter()
            .any(|(method, _)| method == "archive.list")
        {
            assert!(
                Instant::now() < in_flight,
                "first call never reached the plugin"
            );
            std::thread::sleep(Duration::from_millis(10));
        }

        let started = Instant::now();
        let busy = second
            .list_entries(&fine)
            .expect_err("a queued caller gives up");
        let waited = started.elapsed();
        assert_eq!(busy, "plugin com.test.busy is busy");
        assert!(waited >= Duration::from_millis(300), "{waited:?}");
        assert!(waited < Duration::from_millis(1200), "{waited:?}");

        let first_error = holder
            .join()
            .unwrap()
            .expect_err("the hanging call times out");
        assert!(first_error.contains("archive.list"), "{first_error}");
        assert_eq!(second.list_entries(&fine).expect("free again").len(), 2);
    })
    .await
    .unwrap();
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn reregistering_with_only_built_in_formats_keeps_the_existing_registration() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-rereg-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let plugin_id = "com.test.rereg";
    let _unregister = Unregister(plugin_id.to_string());
    let mut service = Service::start().await;
    let registered = service
        .call(
            "register_archive_engine",
            json!({ "pluginId": plugin_id, "pipeName": pipe_name, "formats": hello_format() }),
        )
        .await;
    assert!(registered.get("error").is_none(), "{registered}");

    let zip_only = service
        .call(
            "register_archive_engine",
            json!({
                "pluginId": plugin_id,
                "pipeName": pipe_name,
                "formats": [{
                    "extension": ".zip",
                    "canList": true,
                    "canExtract": true,
                    "canCreate": true,
                    "canModify": true,
                    "engineName": "Zip Thief"
                }]
            }),
        )
        .await;
    let message = zip_only["error"]["message"].as_str().unwrap_or_default();
    assert!(message.contains("no registrable formats"), "{zip_only}");
    // Rejected before connecting: only the first registration handshook.
    assert_eq!(log.handshakes.load(Ordering::SeqCst), 1);

    let capabilities = service.call("get_archive_capabilities", json!({})).await;
    let formats = capabilities["result"]["formats"].as_array().unwrap();
    assert!(formats.iter().any(|format| format["extension"] == ".hello"
        && format["engine"] == format!("plugin:{plugin_id}")));
    assert!(!formats
        .iter()
        .any(|format| format["extension"] == ".zip" && format["engine"] != "built-in"));

    service.stop().await;
}
