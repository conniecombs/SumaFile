//! Drives the real 7-Zip plugin backend exe, with the bundled 7za, through
//! the service: create, list, browse, read, extract and in-place mutation of
//! `.7z` archives, progress and cancel of long operations, the settings
//! lookup on reads, and the plugin note once the engine is gone.
#![cfg(windows)]

mod support;

use serde_json::{json, Value};
use std::collections::BTreeMap;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};
use support::{
    built_exe, launch_plugin, temp_dir, FakeHost, PluginProcess, Service, Unregister, REGISTRY_LOCK,
};

const SEVEN_ZIP_ID: &str = "com.sumafile.sevenzip";
const SEVEN_ZIP_NOTE: &str = "7-Zip support requires the SumaFile 7-Zip plugin.";
const CANCELLED: &str = "Archive operation cancelled";
/// `sha256("hello")`.
const HELLO_SHA256: &str = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";

fn bundled_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("third_party")
        .join("7zip")
        .join("win-x64")
}

fn bundled_7za() -> PathBuf {
    bundled_dir().join("7za.exe")
}

/// The backend in a temp plugin folder beside the bundled 7-Zip files, as
/// the release lays out `plugins\com.sumafile.sevenzip`.
fn sevenzip_backend() -> PluginProcess {
    let bundled = bundled_dir();
    let files: Vec<PathBuf> = ["7za.exe", "7za.dll", "7zxa.dll", "License.txt"]
        .iter()
        .map(|name| bundled.join(name))
        .collect();
    for file in &files {
        assert!(file.is_file(), "{} is missing", file.display());
    }
    launch_plugin(&built_exe("sumafile-sevenzip-backend"), &files)
}

async fn register_sevenzip(service: &Service, backend: &PluginProcess) -> Unregister {
    let unregister = Unregister(SEVEN_ZIP_ID.to_string());
    let registered = service
        .call(
            "register_archive_engine",
            json!({
                "pluginId": SEVEN_ZIP_ID,
                "pipeName": backend.pipe_name,
                "formats": [{
                    "extension": ".7z",
                    "canList": true,
                    "canExtract": true,
                    "canCreate": true,
                    "canModify": true,
                    "engineName": "7-Zip"
                }]
            }),
        )
        .await;
    assert_ok(&registered);
    unregister
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

fn write_file(path: &Path, content: &str) {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(path, content).unwrap();
}

/// Every file under `root`, by `/`-separated relative path, with its text.
fn tree(root: &Path) -> BTreeMap<String, String> {
    fn walk(root: &Path, dir: &Path, files: &mut BTreeMap<String, String>) {
        for entry in std::fs::read_dir(dir).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                walk(root, &path, files);
            } else {
                let relative = path
                    .strip_prefix(root)
                    .unwrap()
                    .components()
                    .map(|part| part.as_os_str().to_string_lossy().into_owned())
                    .collect::<Vec<_>>()
                    .join("/");
                files.insert(relative, std::fs::read_to_string(&path).unwrap());
            }
        }
    }
    let mut files = BTreeMap::new();
    walk(root, root, &mut files);
    files
}

/// The children of `dir`, as `create_archive` paths, so they land at the
/// archive root.
fn children(dir: &Path) -> Vec<String> {
    let mut paths: Vec<String> = std::fs::read_dir(dir)
        .unwrap()
        .map(|entry| entry.unwrap().path().to_string_lossy().into_owned())
        .collect();
    paths.sort();
    paths
}

async fn create_7z(service: &Service, paths: Vec<String>, archive: &Path) {
    let created = service
        .call(
            "create_archive",
            json!({
                "paths": paths,
                "archivePath": archive.to_string_lossy(),
                "format": "7z"
            }),
        )
        .await;
    assert_eq!(created["result"], Value::Null, "{created}");
    assert_ok(&created);
}

/// The `path` of every entry `list_archive` reports, sorted.
async fn archive_paths(service: &Service, archive: &Path) -> Vec<String> {
    let listing = service
        .call("list_archive", json!({ "path": archive.to_string_lossy() }))
        .await;
    assert_ok(&listing);
    let mut paths: Vec<String> = listing["result"]["entries"]
        .as_array()
        .unwrap_or_else(|| panic!("entries in {listing}"))
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

fn strings(values: &[&str]) -> Vec<String> {
    values.iter().map(|value| (*value).to_string()).collect()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sevenzip_plugin_serves_create_list_browse_extract_and_mutation() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = sevenzip_backend();
    let service = Service::start().await;
    let _unregister = register_sevenzip(&service, &backend).await;
    let root = temp_dir("sevenzip-e2e");
    let source = root.join("source");
    write_file(&source.join("café 資料").join("hello.txt"), "hello");
    write_file(
        &source.join("literal [brackets] & spaces.txt"),
        "brackets & spaces",
    );
    write_file(&source.join("docs").join("readme.txt"), "readme");
    write_file(&source.join("docs").join("inner").join("deep.txt"), "deep");
    let archive = root.join("x.7z");

    // Create and list.
    create_7z(&service, children(&source), &archive).await;
    assert_eq!(
        archive_paths(&service, &archive).await,
        strings(&[
            "café 資料",
            "café 資料/hello.txt",
            "docs",
            "docs/inner",
            "docs/inner/deep.txt",
            "docs/readme.txt",
            "literal [brackets] & spaces.txt",
        ])
    );

    // Browse the root and nested folders.
    assert_eq!(
        listed_names(&service, &archive.to_string_lossy()).await,
        strings(&["café 資料", "docs", "literal [brackets] & spaces.txt"])
    );
    assert_eq!(
        listed_names(&service, &inside(&archive, "docs")).await,
        strings(&["inner", "readme.txt"])
    );
    assert_eq!(
        listed_names(&service, &inside(&archive, "café 資料")).await,
        strings(&["hello.txt"])
    );

    // Read through a virtual path: preview and checksum.
    let preview = service
        .call(
            "read_file_preview",
            json!({ "path": inside(&archive, r"café 資料\hello.txt") }),
        )
        .await;
    assert_ok(&preview);
    assert_eq!(preview["result"]["content"], "hello", "{preview}");
    let checksum = service
        .call(
            "compute_checksum",
            json!({ "path": inside(&archive, r"café 資料\hello.txt") }),
        )
        .await;
    assert_ok(&checksum);
    assert_eq!(checksum["result"]["sha256"], HELLO_SHA256, "{checksum}");

    // Extract: the tree comes back equal.
    let out = root.join("out");
    let extracted = service
        .call(
            "extract_archive",
            json!({
                "archivePath": archive.to_string_lossy(),
                "destination": out.to_string_lossy()
            }),
        )
        .await;
    assert_ok(&extracted);
    assert_eq!(tree(&out), tree(&source));

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

    // Copy in, copy in again with a conflict renamed, and copy out.
    let local = root.join("local");
    write_file(&local.join("in.txt"), "input");
    for _ in 0..2 {
        let copied_in = service
            .call(
                "copy_entry_resolved",
                json!({
                    "source": local.join("in.txt").to_string_lossy(),
                    "destination": inside(&archive, "docs"),
                    "conflictAction": "rename"
                }),
            )
            .await;
        assert_ok(&copied_in);
    }
    let paths = archive_paths(&service, &archive).await;
    assert!(paths.contains(&"docs/in.txt".to_string()), "{paths:?}");
    assert!(paths.contains(&"docs/in (1).txt".to_string()), "{paths:?}");
    let copied_out_dir = root.join("copied-out");
    std::fs::create_dir_all(&copied_out_dir).unwrap();
    let copied_out = service
        .call(
            "copy_entry_resolved",
            json!({
                "source": inside(&archive, "literal [brackets] & spaces.txt"),
                "destination": copied_out_dir.to_string_lossy(),
                "conflictAction": "error"
            }),
        )
        .await;
    assert_ok(&copied_out);
    assert_eq!(
        std::fs::read_to_string(copied_out_dir.join("literal [brackets] & spaces.txt")).unwrap(),
        "brackets & spaces"
    );

    // Move within the archive.
    let moved = service
        .call(
            "move_entry_resolved",
            json!({
                "source": inside(&archive, "literal [brackets] & spaces.txt"),
                "destination": inside(&archive, "docs"),
                "conflictAction": "error"
            }),
        )
        .await;
    assert_ok(&moved);
    let paths = archive_paths(&service, &archive).await;
    assert!(
        paths.contains(&"docs/literal [brackets] & spaces.txt".to_string()),
        "{paths:?}"
    );
    assert!(
        !paths.contains(&"literal [brackets] & spaces.txt".to_string()),
        "{paths:?}"
    );

    // Delete one entry, then move two to the Recycle Bin.
    let deleted = service
        .call(
            "delete_entry",
            json!({ "path": inside(&archive, r"docs\m.txt") }),
        )
        .await;
    assert_ok(&deleted);
    assert!(!archive_paths(&service, &archive)
        .await
        .contains(&"docs/m.txt".to_string()));
    let trashed = service
        .call(
            "move_to_trash",
            json!({ "paths": [
                inside(&archive, r"docs\in.txt"),
                inside(&archive, r"docs\in (1).txt")
            ]}),
        )
        .await;
    assert_ok(&trashed);
    assert_eq!(
        archive_paths(&service, &archive).await,
        strings(&[
            "café 資料",
            "café 資料/hello.txt",
            "docs",
            "docs/inner",
            "docs/inner/deep.txt",
            "docs/literal [brackets] & spaces.txt",
            "docs/new",
            "docs/readme.txt",
        ])
    );
    assert_eq!(
        listed_names(&service, &inside(&archive, "docs")).await,
        strings(&[
            "inner",
            "literal [brackets] & spaces.txt",
            "new",
            "readme.txt"
        ])
    );

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sevenzip_plugin_reads_a_deflate64_archive_through_the_service() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = sevenzip_backend();
    let service = Service::start().await;
    let _unregister = register_sevenzip(&service, &backend).await;
    let root = temp_dir("sevenzip-deflate64");
    let content = "Deflate64 ".repeat(1000);
    write_file(&root.join("source").join("data.txt"), &content);
    let archive = root.join("deflate64.7z");
    let output = std::process::Command::new(bundled_7za())
        .args([
            "a",
            "-t7z",
            "-m0=Deflate64",
            "-sccUTF-8",
            "-bso0",
            "-bsp0",
            "--",
        ])
        .arg(&archive)
        .arg(root.join("source").join("data.txt"))
        .stdin(std::process::Stdio::null())
        .output()
        .expect("run the bundled 7za");
    assert!(output.status.success(), "{output:?}");

    assert_eq!(
        archive_paths(&service, &archive).await,
        strings(&["data.txt"])
    );
    let preview = service
        .call(
            "read_file_preview",
            json!({ "path": inside(&archive, "data.txt") }),
        )
        .await;
    assert_ok(&preview);
    assert_eq!(preview["result"]["content"], content.as_str(), "{preview}");
    let out = root.join("out");
    let extracted = service
        .call(
            "extract_archive",
            json!({
                "archivePath": archive.to_string_lossy(),
                "destination": out.to_string_lossy()
            }),
        )
        .await;
    assert_ok(&extracted);
    assert_eq!(
        std::fs::read_to_string(out.join("data.txt")).unwrap(),
        content
    );

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sevenzip_plugin_reports_tracked_progress_and_completes() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = sevenzip_backend();
    let service = Service::start().await;
    let _unregister = register_sevenzip(&service, &backend).await;
    let root = temp_dir("sevenzip-progress");
    write_file(&root.join("source").join("a.txt"), &"alpha ".repeat(4096));
    write_file(&root.join("source").join("b.txt"), &"bravo ".repeat(4096));
    let archive = root.join("pack.7z");
    create_7z(&service, children(&root.join("source")), &archive).await;
    let destination = root.join("out");

    let extracted = service
        .call(
            "extract_archive",
            json!({
                "archivePath": archive.to_string_lossy(),
                "destination": destination.to_string_lossy(),
                "operationId": "sevenzip-progress"
            }),
        )
        .await;
    assert_eq!(extracted["result"], Value::Null, "{extracted}");
    assert_ok(&extracted);

    let events = service.progress_events("sevenzip-progress");
    assert!(events.len() >= 2, "{events:?}");
    assert_eq!(events[0]["status"], "running", "{events:?}");
    assert_eq!(events[0]["operation_type"], "extract-archive", "{events:?}");
    assert!(
        events
            .iter()
            .any(|event| event["total"].as_u64().unwrap_or(0) > 0),
        "{events:?}"
    );
    assert_eq!(events.last().unwrap()["status"], "completed", "{events:?}");
    assert_eq!(tree(&destination), tree(&root.join("source")));

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

/// `bytes` that no compressor can shrink, written from a xorshift stream.
fn write_random_file(path: &Path, bytes: usize) {
    let mut file = std::io::BufWriter::new(std::fs::File::create(path).unwrap());
    let mut state = 0x9E37_79B9_7F4A_7C15u64 ^ support::nonce() as u64;
    let mut chunk = vec![0u8; 1 << 20];
    let mut written = 0;
    while written < bytes {
        for word in chunk.as_chunks_mut::<8>().0 {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            *word = state.to_le_bytes();
        }
        let take = chunk.len().min(bytes - written);
        file.write_all(&chunk[..take]).unwrap();
        written += take;
    }
    file.flush().unwrap();
}

fn has_engine_progress(service: &Service, operation_id: &str) -> bool {
    service
        .progress_events(operation_id)
        .iter()
        .any(|event| event["status"] == "running" && event["total"].as_u64().unwrap_or(0) > 0)
}

/// Review Focus 1: a listing is answered while two long creates run on the
/// same backend, and a cancel stops both promptly and leaves nothing behind.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sevenzip_plugin_answers_a_listing_while_two_long_creates_run_and_cancels_them() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = sevenzip_backend();
    let service = Service::start().await;
    let _unregister = register_sevenzip(&service, &backend).await;
    let root = temp_dir("sevenzip-concurrent");
    write_file(&root.join("small-source").join("readme.txt"), "small");
    let small = root.join("small.7z");
    create_7z(&service, children(&root.join("small-source")), &small).await;
    let big_source = root.join("big.bin");
    write_random_file(&big_source, 256 * 1024 * 1024);

    let mut creates = Vec::new();
    for index in 1..=2 {
        let id = service
            .send(
                "create_archive",
                json!({
                    "paths": [big_source.to_string_lossy()],
                    "archivePath": root.join(format!("big-{index}.7z")).to_string_lossy(),
                    "format": "7z",
                    "operationId": format!("sevenzip-big-{index}")
                }),
            )
            .await;
        creates.push(id);
    }
    let deadline = Instant::now() + Duration::from_secs(30);
    while !(has_engine_progress(&service, "sevenzip-big-1")
        && has_engine_progress(&service, "sevenzip-big-2"))
    {
        assert!(
            Instant::now() < deadline,
            "both creates never reported engine progress: {:?} {:?}",
            service.progress_events("sevenzip-big-1"),
            service.progress_events("sevenzip-big-2")
        );
        tokio::time::sleep(Duration::from_millis(10)).await;
    }

    let started = Instant::now();
    let listing = service
        .call("list_archive", json!({ "path": small.to_string_lossy() }))
        .await;
    let listed_in = started.elapsed();
    let still_running = creates
        .iter()
        .map(|id| service.try_reply(*id))
        .collect::<Vec<_>>();
    assert!(
        listed_in < Duration::from_secs(5),
        "{listed_in:?} {listing}"
    );
    assert_ok(&listing);
    assert_eq!(listing["result"]["entries"][0]["path"], "readme.txt");
    assert!(
        still_running.iter().all(Option::is_none),
        "a create replied before the listing: {still_running:?}"
    );

    for index in 1..=2 {
        let cancelled = service
            .call(
                "cancel_operation",
                json!({ "operationId": format!("sevenzip-big-{index}") }),
            )
            .await;
        assert_eq!(cancelled["result"], true, "{cancelled}");
    }
    let cancel_started = Instant::now();
    for id in creates {
        let reply = service.reply(id).await;
        assert_eq!(error_message(&reply), CANCELLED, "{reply}");
    }
    assert!(
        cancel_started.elapsed() < Duration::from_secs(5),
        "{:?}",
        cancel_started.elapsed()
    );

    let leftovers: Vec<String> = std::fs::read_dir(&root)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .filter(|name| name.starts_with("big-") || name.contains(".simplefile-new-"))
        .collect();
    assert!(leftovers.is_empty(), "{leftovers:?}");

    service.stop().await;
    drop(backend);
    let _ = std::fs::remove_dir_all(root);
}

/// Review Focus 6: a read whose backend looks up `binaryOverride` through
/// the host, which asks `get_db_settings` on this same service, completes
/// well inside the backend's 2 s settings timeout.
#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sevenzip_read_with_a_host_settings_lookup_completes_quickly() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = sevenzip_backend();
    let service = Service::start().await;
    let host = FakeHost::connect(&backend.pipe_name, service.clone()).await;
    assert_eq!(host.plugin_id(), SEVEN_ZIP_ID);
    let _unregister = register_sevenzip(&service, &backend).await;
    let root = temp_dir("sevenzip-settings");
    write_file(&root.join("source").join("docs").join("a.txt"), "alpha");
    let archive = root.join("x.7z");
    create_7z(&service, children(&root.join("source")), &archive).await;
    let served_before = host
        .served()
        .iter()
        .filter(|method| *method == "host.get_settings")
        .count();

    // A folder, so nothing is launched.
    let started = Instant::now();
    let opened = service
        .call("open_file", json!({ "path": inside(&archive, "docs") }))
        .await;
    let elapsed = started.elapsed();

    assert_eq!(
        error_message(&opened),
        "Cannot open a directory as a file",
        "{opened}"
    );
    // The read's own time is mostly 7za's, which a loaded runner can stretch past
    // seconds (3.67 s in CI run 42746224565), so it only rules out a hang. The
    // deadlock this test guards against shows in the settings round trip: the
    // service answering get_db_settings while the read waits for it.
    assert!(elapsed < Duration::from_secs(15), "{elapsed:?}");
    let latencies = host.settings_latencies();
    let latest = latencies
        .last()
        .copied()
        .unwrap_or_else(|| panic!("no host.get_settings was answered: {:?}", host.served()));
    assert!(
        latest < Duration::from_millis(1500),
        "the settings lookup took {latest:?}, near the backend's 2 s timeout"
    );
    let served_after = host
        .served()
        .iter()
        .filter(|method| *method == "host.get_settings")
        .count();
    assert!(
        served_after > served_before,
        "the read never asked the host for settings: {:?}",
        host.served()
    );

    drop(host);
    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn sevenzip_plugin_unregistered_7z_reports_the_plugin_note() {
    let _lock = REGISTRY_LOCK.lock().await;
    let backend = sevenzip_backend();
    let service = Service::start().await;
    let unregister = register_sevenzip(&service, &backend).await;
    let root = temp_dir("sevenzip-unregistered");
    write_file(&root.join("source").join("docs").join("a.txt"), "alpha");
    let archive = root.join("x.7z");
    create_7z(&service, children(&root.join("source")), &archive).await;
    assert_eq!(
        listed_names(&service, &inside(&archive, "docs")).await,
        strings(&["a.txt"])
    );

    let unregistered = service
        .call(
            "unregister_archive_engine",
            json!({ "pluginId": SEVEN_ZIP_ID }),
        )
        .await;
    assert_ok(&unregistered);
    drop(unregister);

    let capabilities = service.call("get_archive_capabilities", json!({})).await;
    assert_ok(&capabilities);
    let rows: Vec<&Value> = capabilities["result"]["formats"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|row| row["extension"] == ".7z")
        .collect();
    assert_eq!(rows.len(), 1, "{capabilities}");
    assert_eq!(
        rows[0],
        &json!({
            "format": "7z",
            "extension": ".7z",
            "can_list": false,
            "can_extract": false,
            "can_create": false,
            "can_modify": false,
            "engine": "unavailable",
            "note": SEVEN_ZIP_NOTE
        })
    );

    let listing = service
        .call("list_archive", json!({ "path": archive.to_string_lossy() }))
        .await;
    assert_eq!(error_message(&listing), SEVEN_ZIP_NOTE);
    let pane_options = json!({
        "mode": "light",
        "finalEntries": false,
        "sortBy": "name",
        "sortAscending": true,
        "includeHidden": true
    });
    for path in [
        archive.to_string_lossy().into_owned(),
        inside(&archive, "docs"),
    ] {
        for with_options in [true, false] {
            let mut params = if with_options {
                pane_options.clone()
            } else {
                json!({})
            };
            params["path"] = json!(path);
            let listed = service.call("list_directory", params).await;
            assert_eq!(
                error_message(&listed),
                SEVEN_ZIP_NOTE,
                "{path} with options {with_options}"
            );
        }
    }

    service.stop().await;
    let _ = std::fs::remove_dir_all(root);
}
