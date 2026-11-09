//! A plugin backend serves archive formats over its own named pipe; the
//! service connects as a client once the host registers it.
#![cfg(windows)]

mod support;

use serde_json::{json, Value};
use simplefile_core::archive::{ArchiveEngine, EngineContext, EngineLimits};
use simplefile_service::plugin_archive::{PipeArchiveEngine, PluginConnection, PluginTimeouts};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use support::{nonce, read_message, temp_dir, write_message, Service, Unregister, REGISTRY_LOCK};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};

/// What the fake plugin saw, for assertions.
#[derive(Default)]
struct FakeLog {
    handshakes: AtomicUsize,
    disconnects: AtomicUsize,
    calls: Mutex<Vec<(String, Value)>>,
}

/// Act as a plugin backend on `pipe_name`: answer the handshake and the
/// archive methods. The server keeps accepting new connections.
///
/// - The handshake reply echoes the requested `pluginId`, or answers as
///   `com.test.someone-else` on a pipe whose name contains `wrong-id`. On a
///   pipe whose name contains `slow-handshake` it answers after 1 s.
/// - An `archive.list` for a path containing `drop-me` closes the connection
///   without replying, to simulate a crash; one for a path containing
///   `hang-me` is never answered while the connection stays open.
/// - An `archive.read_entry` for a path containing `slow-read` answers after
///   1.5 s and sends no progress.
/// - An `archive.extract` for a path containing `progress-script` sends
///   `archive.progress` for the call's `operationId` every 100 ms for 1.5 s,
///   then extracts.
fn spawn_fake_plugin(pipe_name: &str) -> Arc<FakeLog> {
    let path = format!(r"\\.\pipe\{pipe_name}");
    let log = Arc::new(FakeLog::default());
    let mut server = ServerOptions::new()
        .first_pipe_instance(true)
        .create(&path)
        .expect("create fake plugin pipe");
    let task_log = log.clone();
    let pipe_name = pipe_name.to_string();
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
            tokio::spawn(serve_fake_client(
                connected,
                task_log.clone(),
                pipe_name.clone(),
            ));
        }
    });
    log
}

async fn serve_fake_client(mut pipe: NamedPipeServer, log: Arc<FakeLog>, pipe_name: String) {
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
                if pipe_name.contains("slow-handshake") {
                    tokio::time::sleep(Duration::from_secs(1)).await;
                }
                let plugin_id = if pipe_name.contains("wrong-id") {
                    json!("com.test.someone-else")
                } else {
                    params["pluginId"].clone()
                };
                json!({ "pluginId": plugin_id, "pluginVersion": "0.0.1", "sdkVersion": "1.0.0" })
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
            "archive.read_entry" => {
                if params["archivePath"]
                    .as_str()
                    .unwrap_or_default()
                    .contains("slow-read")
                {
                    tokio::time::sleep(Duration::from_millis(1500)).await;
                }
                Value::Null
            }
            "archive.extract" => {
                if params["archivePath"]
                    .as_str()
                    .unwrap_or_default()
                    .contains("progress-script")
                {
                    for step in 1..=15u64 {
                        tokio::time::sleep(Duration::from_millis(100)).await;
                        write_message(
                            &mut pipe,
                            &json!({
                                "jsonrpc": "2.0",
                                "method": "archive.progress",
                                "params": {
                                    "operationId": params["operationId"],
                                    "current": step,
                                    "total": 15
                                }
                            }),
                        )
                        .await;
                    }
                }
                // The listed `hello/world.txt` (11 bytes), plus a file the
                // listing never mentioned, which core must not move out.
                let destination =
                    std::path::Path::new(params["destination"].as_str().unwrap_or_default());
                std::fs::create_dir_all(destination.join("hello")).unwrap();
                std::fs::write(destination.join("hello").join("world.txt"), b"hello world")
                    .unwrap();
                std::fs::write(destination.join("stray.txt"), b"not listed").unwrap();
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

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn registered_plugin_engine_lists_and_extracts_hello_archives() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let plugin_id = "com.test.hello";
    let _unregister = Unregister(plugin_id.to_string());
    let service = Service::start().await;

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
        std::fs::read(destination.join("hello").join("world.txt")).unwrap(),
        b"hello world"
    );
    assert!(!destination.join("stray.txt").exists());
    let extract_params = log
        .calls
        .lock()
        .unwrap()
        .iter()
        .find(|(method, _)| method == "archive.extract")
        .map(|(_, params)| params.clone())
        .expect("archive.extract reached the plugin");
    assert!(
        extract_params.get("innerPath").is_none(),
        "{extract_params}"
    );
    // The plugin wrote into core's private staging folder, not the
    // destination itself, and that folder is gone again.
    let staging = std::path::PathBuf::from(extract_params["destination"].as_str().unwrap());
    assert!(staging
        .file_name()
        .unwrap()
        .to_string_lossy()
        .starts_with(".sumafile-extract-"));
    assert!(!staging.exists());

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
    let service = Service::start().await;

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
    let service = Service::start().await;
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
        read: Duration::from_secs(2),
        transfer: Duration::from_secs(2),
        cancel: Duration::from_secs(2),
        lock,
        max_connections: 1,
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
    let _lock = REGISTRY_LOCK.lock().await;
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
    let _lock = REGISTRY_LOCK.lock().await;
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
    let service = Service::start().await;
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

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn handshake_reply_with_another_plugin_id_is_refused() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-wrong-id-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let task_pipe = pipe_name.clone();
    let error = tokio::task::spawn_blocking(move || {
        PluginConnection::connect("com.test.right", &task_pipe)
            .err()
            .expect("a mismatched plugin id is refused")
    })
    .await
    .unwrap();
    assert!(
        error.contains(&format!(
            "Plugin pipe {pipe_name} answered as com.test.someone-else, expected com.test.right"
        )),
        "{error}"
    );
    assert_eq!(log.handshakes.load(Ordering::SeqCst), 1);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn read_entry_without_progress_times_out_at_the_read_deadline() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-read-{}", nonce());
    spawn_fake_plugin(&pipe_name);
    let root = temp_dir("read-deadline");
    let archive = root.join("slow-read.hello");
    let destination = root.join("dest");
    std::fs::create_dir_all(&destination).unwrap();

    let error = tokio::task::spawn_blocking(move || {
        let connection = PluginConnection::connect_with_timeouts(
            "com.test.read",
            &pipe_name,
            PluginTimeouts {
                read: Duration::from_millis(500),
                ..PluginTimeouts::default()
            },
        )
        .expect("connect to fake plugin");
        PipeArchiveEngine::new("Read Engine", connection)
            .read_entry(
                &archive,
                "hello",
                &destination,
                EngineLimits {
                    max_bytes: 1024,
                    max_entries: 4,
                },
            )
            .expect_err("a silent read fails at its idle deadline")
    })
    .await
    .unwrap();
    assert!(
        error.contains("did not answer archive.read_entry within 500ms"),
        "{error}"
    );
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn register_that_completes_after_a_later_unregister_registers_nothing() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-slow-handshake-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let plugin_id = "com.test.superseded";
    let _unregister = Unregister(plugin_id.to_string());
    let service = Service::start().await;

    let register = service
        .send(
            "register_archive_engine",
            json!({ "pluginId": plugin_id, "pipeName": pipe_name, "formats": hello_format() }),
        )
        .await;
    let unregistered = service
        .call(
            "unregister_archive_engine",
            json!({ "pluginId": plugin_id }),
        )
        .await;
    assert_eq!(unregistered["result"], Value::Null, "{unregistered}");

    let registered = service.reply(register).await;
    assert_eq!(
        registered["error"]["message"],
        format!("Registration of plugin {plugin_id} was superseded by a later registration change"),
        "{registered}"
    );
    assert_eq!(log.handshakes.load(Ordering::SeqCst), 1);
    let capabilities = service.call("get_archive_capabilities", json!({})).await;
    assert!(!capabilities["result"]["formats"]
        .as_array()
        .unwrap()
        .iter()
        .any(|format| format["extension"] == ".hello"));

    service.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn second_caller_gets_its_own_connection_when_the_pool_allows() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-pool-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let root = temp_dir("pool");
    let hanging = root.join("hang-me.hello");
    let fine = root.join("fine.hello");

    let task_log = log.clone();
    tokio::task::spawn_blocking(move || {
        let connection = PluginConnection::connect_with_timeouts(
            "com.test.pool",
            &pipe_name,
            PluginTimeouts {
                max_connections: 2,
                ..short_timeouts(Duration::from_millis(1500), Duration::from_millis(300))
            },
        )
        .expect("connect to fake plugin");
        let first = PipeArchiveEngine::new("Pool Engine", connection.clone());
        let second = PipeArchiveEngine::new("Pool Engine", connection.clone());
        let holder = std::thread::spawn(move || first.list_entries(&hanging));
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
        let entries = second
            .list_entries(&fine)
            .expect("a second connection answers");
        assert!(started.elapsed() < Duration::from_millis(1000));
        assert_eq!(entries.len(), 2);
        assert_eq!(connection.open_connections(), 2);
        assert_eq!(task_log.handshakes.load(Ordering::SeqCst), 2);
        holder
            .join()
            .unwrap()
            .expect_err("the hanging call times out");
    })
    .await
    .unwrap();
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn progress_notifications_extend_the_transfer_deadline() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-progress-{}", nonce());
    spawn_fake_plugin(&pipe_name);
    let root = temp_dir("progress");
    let archive = root.join("progress-script.hello");
    let destination = root.join("dest");
    std::fs::create_dir_all(&destination).unwrap();

    let task_destination = destination.clone();
    let (elapsed, result) = tokio::task::spawn_blocking(move || {
        let connection = PluginConnection::connect_with_timeouts(
            "com.test.progress",
            &pipe_name,
            PluginTimeouts {
                transfer: Duration::from_millis(500),
                ..PluginTimeouts::default()
            },
        )
        .expect("connect to fake plugin");
        let engine = PipeArchiveEngine::new("Progress Engine", connection);
        let started = Instant::now();
        let result = engine.extract(&archive, &task_destination, &EngineContext::untracked());
        (started.elapsed(), result)
    })
    .await
    .unwrap();
    result.expect("progress keeps the transfer alive past its idle deadline");
    assert!(elapsed >= Duration::from_millis(1400), "{elapsed:?}");
    assert!(destination.join("hello").join("world.txt").is_file());
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn untracked_transfer_calls_carry_a_service_operation_id() {
    let _lock = REGISTRY_LOCK.lock().await;
    let pipe_name = format!("simplefile-test-plugin-opid-{}", nonce());
    let log = spawn_fake_plugin(&pipe_name);
    let root = temp_dir("opid");
    let archive = root.join("fine.hello");
    let destination = root.join("dest");
    std::fs::create_dir_all(&destination).unwrap();

    tokio::task::spawn_blocking(move || {
        let connection =
            PluginConnection::connect("com.test.opid", &pipe_name).expect("connect to fake plugin");
        let engine = PipeArchiveEngine::new("Opid Engine", connection);
        engine
            .extract(&archive, &destination, &EngineContext::untracked())
            .expect("extract");
        engine
            .read_entry(
                &archive,
                "hello",
                &destination.join("read"),
                EngineLimits {
                    max_bytes: 1024,
                    max_entries: 4,
                },
            )
            .expect("read_entry");
        engine.list_entries(&archive).expect("list");
    })
    .await
    .unwrap();

    let calls = log.calls.lock().unwrap().clone();
    let operation_id = |method: &str| {
        calls
            .iter()
            .find(|(called, _)| called == method)
            .map(|(_, params)| params["operationId"].clone())
            .unwrap_or_else(|| panic!("{method} reached the plugin"))
    };
    let extract_id = operation_id("archive.extract");
    let read_id = operation_id("archive.read_entry");
    assert!(
        extract_id.as_str().is_some_and(|id| id.starts_with("svc-")),
        "{extract_id}"
    );
    assert!(
        read_id.as_str().is_some_and(|id| id.starts_with("svc-")),
        "{read_id}"
    );
    assert_ne!(extract_id, read_id);
    assert_eq!(operation_id("archive.list"), Value::Null);
    let _ = std::fs::remove_dir_all(root);
}
