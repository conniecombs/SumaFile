//! Drives the real Hello fixture backend exe through the service: browsing,
//! reading and mutation of `.hello` archives, the read deadline, progress,
//! cancel and the rule that no engine call runs on the session loop.
#![cfg(windows)]

mod support;

use serde_json::{json, Value};
use simplefile_core::archive::{ArchiveEngine, EngineLimits};
use simplefile_service::plugin_archive::{PipeArchiveEngine, PluginConnection, PluginTimeouts};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};
use support::{
    built_exe, launch_plugin, temp_dir, FakeHost, PluginProcess, Service, Unregister, REGISTRY_LOCK,
};

const HELLO_ID: &str = "com.sumafile.hello";

fn hello_backend() -> PluginProcess {
    launch_plugin(&built_exe("hello-plugin-backend"), &[])
}

async fn register_hello(service: &Service, backend: &PluginProcess) -> Unregister {
    let unregister = Unregister(HELLO_ID.to_string());
    let registered = service
        .call(
            "register_archive_engine",
            json!({
                "pluginId": HELLO_ID,
                "pipeName": backend.pipe_name,
                "formats": [{
                    "extension": ".hello",
                    "canList": true,
                    "canExtract": true,
                    "canCreate": true,
                    "canModify": true,
                    "engineName": "Hello Engine"
                }]
            }),
        )
        .await;
    assert_ok(&registered);
    unregister
}

fn write_archive(dir: &Path, name: &str, listing: &str) -> PathBuf {
    let archive = dir.join(name);
    std::fs::write(&archive, listing).unwrap();
    archive
}

fn inside(archive: &Path, inner: &str) -> String {
    format!("{}\\{inner}", archive.display())
}

fn assert_ok(reply: &Value) {
    assert!(reply.get("error").is_none(), "{reply}");
}

fn error_message(reply: &Value) -> String {
    reply["error"]["message"]
        .as_str()
        .unwrap_or_else(|| panic!("expected an error, got {reply}"))
        .to_string()
}

/// The `path` of every entry `list_archive` reports, sorted.
async fn archive_paths(service: &Service, archive: &Path) -> Vec<String> {
    let listing = service
        .call("list_archive", json!({ "path": archive.to_string_lossy() }))
        .await;
    assert_ok(&listing);
    let mut paths: Vec<String> = listing["result"]["entries"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| entry["path"].as_str().unwrap().to_string())
        .collect();
    paths.sort();
    paths
}

async fn listed_names(service: &Service, path: &str) -> Vec<String> {
    let listing = service
        .call(
            "list_directory",
            json!({ "path": path, "finalEntries": true }),
        )
        .await;
    assert_ok(&listing);
    let mut names: Vec<String> = listing["result"]["entries"]
        .as_array()
        .unwrap_or_else(|| panic!("entries in {listing}"))
        .iter()
        .map(|entry| entry["name"].as_str().unwrap().to_string())
        .collect();
    names.sort();
    names
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn hello_engine_serves_browse_read_and_mutation_through_the_service() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let service = Service::start().await;
    let _unregister = register_hello(&service, &backend).await;
    let root = temp_dir("hello-e2e");
    let archive = write_archive(&root, "x.hello", "docs/a.txt\t4\ntop.txt\t3\n");

    // Browse the root and a nested folder.
    assert_eq!(
        listed_names(&service, &archive.to_string_lossy()).await,
        ["docs", "top.txt"]
    );
    assert_eq!(
        listed_names(&service, &inside(&archive, "docs")).await,
        ["a.txt"]
    );

    // Read: Hello writes `size` spaces.
    let preview = service
        .call(
            "read_file_preview",
            json!({ "path": inside(&archive, r"docs\a.txt") }),
        )
        .await;
    assert_ok(&preview);
    assert_eq!(preview["result"]["content"], "    ", "{preview}");

    // New folder and new file.
    let created = service
        .call(
            "create_directory",
            json!({ "path": inside(&archive, "docs"), "name": "new" }),
        )
        .await;
    assert_ok(&created);
    assert!(archive_paths(&service, &archive)
        .await
        .contains(&"docs/new".to_string()));
    let created = service
        .call(
            "create_file",
            json!({ "path": inside(&archive, "docs"), "name": "n.txt" }),
        )
        .await;
    assert_ok(&created);
    assert!(archive_paths(&service, &archive)
        .await
        .contains(&"docs/n.txt".to_string()));

    // Rename.
    let renamed = service
        .call(
            "rename_entry",
            json!({ "path": inside(&archive, r"docs\n.txt"), "newName": "m.txt" }),
        )
        .await;
    assert_ok(&renamed);
    let paths = archive_paths(&service, &archive).await;
    assert!(paths.contains(&"docs/m.txt".to_string()), "{paths:?}");
    assert!(!paths.contains(&"docs/n.txt".to_string()), "{paths:?}");

    // Copy in from a local file, and out to a local folder.
    let local = root.join("local");
    std::fs::create_dir_all(&local).unwrap();
    std::fs::write(local.join("in.txt"), b"input").unwrap();
    let copied_in = service
        .call(
            "copy_entry_resolved",
            json!({
                "source": local.join("in.txt").to_string_lossy(),
                "destination": inside(&archive, "docs"),
                "conflictAction": "error"
            }),
        )
        .await;
    assert_ok(&copied_in);
    assert!(archive_paths(&service, &archive)
        .await
        .contains(&"docs/in.txt".to_string()));
    let out = root.join("out");
    std::fs::create_dir_all(&out).unwrap();
    let copied_out = service
        .call(
            "copy_entry_resolved",
            json!({
                "source": inside(&archive, r"docs\a.txt"),
                "destination": out.to_string_lossy(),
                "conflictAction": "error"
            }),
        )
        .await;
    assert_ok(&copied_out);
    assert_eq!(std::fs::read(out.join("a.txt")).unwrap(), b"    ");

    // Move within the archive.
    let moved = service
        .call(
            "move_entry_resolved",
            json!({
                "source": inside(&archive, "top.txt"),
                "destination": inside(&archive, "docs"),
                "conflictAction": "error"
            }),
        )
        .await;
    assert_ok(&moved);
    let paths = archive_paths(&service, &archive).await;
    assert!(paths.contains(&"docs/top.txt".to_string()), "{paths:?}");
    assert!(!paths.contains(&"top.txt".to_string()), "{paths:?}");

    // Delete.
    let deleted = service
        .call(
            "delete_entry",
            json!({ "path": inside(&archive, r"docs\m.txt") }),
        )
        .await;
    assert_ok(&deleted);
    assert_eq!(
        archive_paths(&service, &archive).await,
        ["docs/a.txt", "docs/in.txt", "docs/new", "docs/top.txt"]
    );

    // Folder metrics come from the listing: a.txt (4), in.txt (5), new,
    // top.txt (3).
    let metrics = service
        .call(
            "get_folder_metrics",
            json!({ "path": inside(&archive, "docs") }),
        )
        .await;
    assert_ok(&metrics);
    assert_eq!(metrics["result"]["item_count"], 4, "{metrics}");
    assert_eq!(metrics["result"]["size"], 12, "{metrics}");
    let count = service
        .call(
            "count_folder_items",
            json!({ "path": inside(&archive, "docs") }),
        )
        .await;
    assert_ok(&count);
    assert_eq!(count["result"], 4, "{count}");

    let metadata = service
        .call(
            "get_file_metadata",
            json!({ "path": inside(&archive, r"docs\a.txt") }),
        )
        .await;
    assert_ok(&metadata);

    let compared = service
        .call(
            "compare_files",
            json!({
                "pathA": inside(&archive, r"docs\a.txt"),
                "pathB": out.join("a.txt").to_string_lossy()
            }),
        )
        .await;
    assert_ok(&compared);
    assert_eq!(compared["result"]["identical"], true, "{compared}");

    // Batch rename refuses archive entries before any engine call.
    let before = std::fs::read(&archive).unwrap();
    let batch = service
        .call(
            "batch_rename",
            json!({ "entries": [
                { "path": inside(&archive, r"docs\a.txt"), "new_name": "b.txt" },
                { "path": inside(&archive, r"docs\in.txt"), "new_name": "c.txt" }
            ]}),
        )
        .await;
    assert!(
        error_message(&batch).contains("Path does not exist"),
        "{batch}"
    );
    assert_eq!(std::fs::read(&archive).unwrap(), before);
    assert_eq!(
        archive_paths(&service, &archive).await,
        ["docs/a.txt", "docs/in.txt", "docs/new", "docs/top.txt"]
    );

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn open_file_on_an_archive_entry_does_not_block_the_session_loop() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let service = Service::start().await;
    let _unregister = register_hello(&service, &backend).await;
    let root = temp_dir("hello-open");
    let archive = write_archive(&root, "x.hello", "docs/a.txt\t1\ndocs/__slow__\t2000\n");

    // A folder, so nothing is launched; materializing it reads the slow
    // entry for 2 s.
    let open = service
        .send("open_file", json!({ "path": inside(&archive, "docs") }))
        .await;
    let capabilities = service.call("get_archive_capabilities", json!({})).await;
    assert_ok(&capabilities);
    assert!(
        service.try_reply(open).is_none(),
        "open_file answered before get_archive_capabilities"
    );
    let opened = service.reply(open).await;
    assert_eq!(
        error_message(&opened),
        "Cannot open a directory as a file",
        "{opened}"
    );

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn slow_read_entry_progress_extends_the_read_deadline() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let root = temp_dir("hello-read-deadline");
    let archive = write_archive(&root, "x.hello", "docs/a.txt\t1\ndocs/__slow__\t1500\n");
    let destination = root.join("read");
    std::fs::create_dir_all(&destination).unwrap();
    let pipe_name = backend.pipe_name.clone();

    let (elapsed, result) = tokio::task::spawn_blocking(move || {
        let connection = PluginConnection::connect_with_timeouts(
            HELLO_ID,
            &pipe_name,
            PluginTimeouts {
                read: Duration::from_millis(500),
                ..PluginTimeouts::default()
            },
        )
        .expect("connect to Hello");
        let engine = PipeArchiveEngine::new("Hello Engine", connection);
        let started = Instant::now();
        let result = engine.read_entry(
            &archive,
            "docs",
            &destination,
            EngineLimits {
                max_bytes: 1 << 20,
                max_entries: 16,
            },
        );
        (started.elapsed(), result)
    })
    .await
    .unwrap();

    result.expect("progress keeps the read alive past its 500 ms idle deadline");
    assert!(elapsed >= Duration::from_millis(1400), "{elapsed:?}");
    assert!(root.join("read").join("docs").join("a.txt").is_file());
    drop(backend);
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn tracked_extract_relays_plugin_progress_as_operation_progress() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let service = Service::start().await;
    let _unregister = register_hello(&service, &backend).await;
    let root = temp_dir("hello-progress");
    let archive = write_archive(&root, "x.hello", "__slow__\t300\n");
    let destination = root.join("out");

    let extracted = service
        .call(
            "extract_archive",
            json!({
                "archivePath": archive.to_string_lossy(),
                "destination": destination.to_string_lossy(),
                "operationId": "op-progress"
            }),
        )
        .await;
    assert_eq!(extracted["result"], Value::Null, "{extracted}");
    assert_ok(&extracted);

    let events = service.progress_events("op-progress");
    assert!(events.len() >= 3, "{events:?}");
    assert_eq!(events[0]["status"], "running", "{events:?}");
    assert_eq!(events[0]["operation_type"], "extract-archive", "{events:?}");
    assert!(
        events
            .iter()
            .any(|event| event["current"].as_u64().unwrap_or(0) > 0 && event["total"] == 300),
        "{events:?}"
    );
    assert_eq!(events.last().unwrap()["status"], "completed", "{events:?}");
    assert!(destination.join("__slow__").is_file());

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

async fn wait_for_engine_progress(service: &Service, operation_id: &str) {
    let deadline = Instant::now() + Duration::from_secs(10);
    while !service
        .progress_events(operation_id)
        .iter()
        .any(|event| event["total"].as_u64().unwrap_or(0) > 0)
    {
        assert!(
            Instant::now() < deadline,
            "no engine progress for {operation_id}"
        );
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn cancel_operation_stops_a_slow_plugin_extract() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let service = Service::start().await;
    let _unregister = register_hello(&service, &backend).await;
    let root = temp_dir("hello-cancel");
    let archive = write_archive(&root, "x.hello", "__slow__\t20000\n");
    let destination = root.join("out");

    let extract = service
        .send(
            "extract_archive",
            json!({
                "archivePath": archive.to_string_lossy(),
                "destination": destination.to_string_lossy(),
                "operationId": "op-cancel"
            }),
        )
        .await;
    wait_for_engine_progress(&service, "op-cancel").await;

    let cancelled = service
        .call("cancel_operation", json!({ "operationId": "op-cancel" }))
        .await;
    assert_eq!(cancelled["result"], true, "{cancelled}");
    let started = Instant::now();
    let reply = service.reply(extract).await;
    assert!(started.elapsed() < Duration::from_secs(5), "{reply}");
    assert_eq!(error_message(&reply), "Archive operation cancelled");

    let events = service.progress_events("op-cancel");
    assert_eq!(events.last().unwrap()["status"], "cancelled", "{events:?}");
    let leftovers: Vec<String> = std::fs::read_dir(&destination)
        .map(|entries| {
            entries
                .filter_map(Result::ok)
                .map(|entry| entry.file_name().to_string_lossy().into_owned())
                .collect()
        })
        .unwrap_or_default();
    assert!(
        !leftovers
            .iter()
            .any(|name| name.starts_with(".sumafile-extract-")),
        "{leftovers:?}"
    );

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn concurrent_list_is_answered_while_a_slow_extract_runs() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let service = Service::start().await;
    let _unregister = register_hello(&service, &backend).await;
    let root = temp_dir("hello-concurrent");
    let slow = write_archive(&root, "a.hello", "__slow__\t20000\n");
    let other = write_archive(&root, "b.hello", "readme.txt\t6\n");

    let extract = service
        .send(
            "extract_archive",
            json!({
                "archivePath": slow.to_string_lossy(),
                "destination": root.join("out").to_string_lossy(),
                "operationId": "op-long"
            }),
        )
        .await;
    wait_for_engine_progress(&service, "op-long").await;

    let started = Instant::now();
    let listing = service
        .call("list_archive", json!({ "path": other.to_string_lossy() }))
        .await;
    assert!(started.elapsed() < Duration::from_secs(5), "{listing}");
    assert_ok(&listing);
    assert_eq!(listing["result"]["entries"][0]["path"], "readme.txt");

    service
        .call("cancel_operation", json!({ "operationId": "op-long" }))
        .await;
    let reply = service.reply(extract).await;
    assert_eq!(error_message(&reply), "Archive operation cancelled");

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn archive_mutation_does_not_block_the_session_loop() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let service = Service::start().await;
    let _unregister = register_hello(&service, &backend).await;
    let root = temp_dir("hello-mutation");
    let archive = write_archive(&root, "x.hello", "docs/a.txt\t1\n__slow__\t2000\n");

    let create = service
        .send(
            "create_directory",
            json!({ "path": archive.to_string_lossy(), "name": "fresh" }),
        )
        .await;
    let capabilities = service.call("get_archive_capabilities", json!({})).await;
    assert_ok(&capabilities);
    assert!(
        service.try_reply(create).is_none(),
        "create_directory answered before get_archive_capabilities"
    );
    let created = service.reply(create).await;
    assert_ok(&created);
    assert!(archive_paths(&service, &archive)
        .await
        .contains(&"fresh".to_string()));

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn fake_host_answers_backend_settings_through_the_service() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = hello_backend();
    let service = Service::start().await;
    let stored = service
        .call(
            "set_db_setting",
            json!({ "key": "plugin.com.sumafile.hello.greeting", "value": "Howdy" }),
        )
        .await;
    assert_ok(&stored);
    let host = FakeHost::connect(&backend.pipe_name, service.clone()).await;
    assert_eq!(host.plugin_id(), HELLO_ID);

    let pong = host.request("ping", json!({})).await;
    assert_eq!(pong["result"]["greeting"], "Howdy", "{pong}");
    assert_eq!(host.served(), ["host.get_settings"]);

    drop(host);
    service.stop().await;
}
