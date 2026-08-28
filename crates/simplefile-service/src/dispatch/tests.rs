use super::handlers::{auth_token_matches, constant_time_eq};
use super::*;
use serde_json::{json, Value};
use simplefile_ipc::rpc::JsonRpcRequest;
use simplefile_ipc::{
    ERR_HOST_OWNED, ERR_INVALID_REQUEST, ERR_METHOD_NOT_FOUND, HANDSHAKE_METHOD, PREFIX_HOST_OWNED,
};
use std::ffi::OsString;
use std::fs;
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};

fn request(method: &str, id: u64, params: Value) -> JsonRpcRequest {
    JsonRpcRequest {
        jsonrpc: "2.0".into(),
        id: Some(json!(id)),
        method: method.into(),
        params: Some(params),
    }
}

fn temp_file(name: &str, content: &[u8]) -> std::path::PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("time")
        .as_nanos();
    let path = std::env::temp_dir().join(format!("simplefile-service-dispatch-{name}-{nanos}.txt"));
    fs::write(&path, content).expect("write temp file");
    path
}

fn metadata_db_env_lock() -> &'static Mutex<()> {
    static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
    LOCK.get_or_init(|| Mutex::new(()))
}

struct EnvVarGuard {
    key: &'static str,
    previous: Option<OsString>,
}

impl EnvVarGuard {
    fn set(key: &'static str, value: &std::path::Path) -> Self {
        let previous = std::env::var_os(key);
        std::env::set_var(key, value);
        Self { key, previous }
    }
}

impl Drop for EnvVarGuard {
    fn drop(&mut self) {
        if let Some(previous) = &self.previous {
            std::env::set_var(self.key, previous);
        } else {
            std::env::remove_var(self.key);
        }
    }
}

#[test]
fn rejects_methods_before_handshake() {
    let mut state = SessionState::default();
    let outcome = dispatch(&mut state, &request("get_home_dir", 1, json!({})));
    let Dispatch::Reply(response) = outcome else {
        panic!("expected reply");
    };
    assert_eq!(response.error.unwrap().code, ERR_INVALID_REQUEST);
}

#[test]
fn handshake_then_home_dir() {
    let mut state = SessionState {
        expected_token: Some("dev".to_string()),
        ..SessionState::default()
    };
    let handshake = dispatch(
        &mut state,
        &request(
            HANDSHAKE_METHOD,
            1,
            json!({
                "protocolVersion": 1,
                "clientName": "test",
                "authToken": "dev"
            }),
        ),
    );
    let Dispatch::Reply(ready) = handshake else {
        panic!("expected handshake reply");
    };
    assert!(ready.error.is_none());
    assert!(state.handshake_done);

    let home = dispatch(&mut state, &request("get_home_dir", 2, json!({})));
    let Dispatch::Reply(response) = home else {
        panic!("expected home dir reply");
    };
    let path = response.result.unwrap().as_str().unwrap().to_string();
    assert!(!path.is_empty());
}

#[test]
fn handshake_requires_configured_token() {
    let mut state = SessionState::default();
    let handshake = dispatch(
        &mut state,
        &request(
            HANDSHAKE_METHOD,
            1,
            json!({
                "protocolVersion": 1,
                "clientName": "test",
                "authToken": "dev"
            }),
        ),
    );
    let Dispatch::Reply(ready) = handshake else {
        panic!("expected handshake reply");
    };
    assert!(ready.error.is_some());
    assert!(!state.handshake_done);
}

#[test]
fn handshake_rejects_wrong_token() {
    let mut state = SessionState {
        expected_token: Some("dev".to_string()),
        ..SessionState::default()
    };
    let handshake = dispatch(
        &mut state,
        &request(
            HANDSHAKE_METHOD,
            1,
            json!({
                "protocolVersion": 1,
                "clientName": "test",
                "authToken": "nope"
            }),
        ),
    );
    let Dispatch::Reply(ready) = handshake else {
        panic!("expected handshake reply");
    };
    assert!(ready.error.is_some());
    assert!(!state.handshake_done);
}

#[test]
fn constant_time_eq_distinguishes_tokens() {
    assert!(constant_time_eq(b"abcd", b"abcd"));
    assert!(!constant_time_eq(b"abcd", b"abce"));
    assert!(!constant_time_eq(b"abc", b"abcd"));
    assert!(!auth_token_matches("secret", None));
    assert!(auth_token_matches("secret", Some("secret")));
}

#[test]
fn duplicate_check_and_cleanup_are_dispatched() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    let duplicate = dispatch(
        &mut state,
        &request(
            "duplicate_check",
            4,
            json!({
                "directory": "C:\\",
                "minSize": 1,
                "operationId": "dup-1"
            }),
        ),
    );
    match duplicate {
        Dispatch::DuplicateCheck {
            directory,
            min_size,
            operation_id,
            ..
        } => {
            assert_eq!(directory, "C:\\");
            assert_eq!(min_size, Some(1));
            assert_eq!(operation_id.as_deref(), Some("dup-1"));
        }
        other => panic!("expected DuplicateCheck, got {other:?}"),
    }

    let cancel = dispatch(&mut state, &request("cancel_duplicate_check", 5, json!({})));
    assert!(matches!(cancel, Dispatch::CancelDuplicateCheck { .. }));

    let cleanup = dispatch(
        &mut state,
        &request(
            "disk_cleanup",
            6,
            json!({
                "directory": "C:\\",
                "sizeThreshold": 100,
                "operationId": "clean-1"
            }),
        ),
    );
    assert!(matches!(cleanup, Dispatch::DiskCleanup { .. }));
}

#[test]
fn select_directory_is_host_owned() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };
    let outcome = dispatch(&mut state, &request("select_directory", 3, json!({})));
    let Dispatch::Reply(response) = outcome else {
        panic!("expected reply");
    };
    let error = response.error.unwrap();
    assert_eq!(error.code, ERR_HOST_OWNED);
    assert!(error.message.starts_with(PREFIX_HOST_OWNED));
}

#[test]
fn unknown_method_is_not_found() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };
    let outcome = dispatch(&mut state, &request("not_a_real_method", 4, json!({})));
    let Dispatch::Reply(response) = outcome else {
        panic!("expected reply");
    };
    assert_eq!(response.error.unwrap().code, ERR_METHOD_NOT_FOUND);
}

#[test]
fn settings_methods_round_trip_through_metadata_db() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("settings-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    let set = dispatch(
        &mut state,
        &request(
            "set_db_setting",
            5,
            json!({ "key": "winui.layout", "value": "{\"dualPane\":true}" }),
        ),
    );
    let Dispatch::Reply(set_response) = set else {
        panic!("expected settings set reply");
    };
    assert!(set_response.error.is_none());

    let get = dispatch(
        &mut state,
        &request("get_db_setting", 6, json!({ "key": "winui.layout" })),
    );
    let Dispatch::Reply(get_response) = get else {
        panic!("expected settings get reply");
    };
    assert_eq!(
        get_response.result.unwrap().as_str(),
        Some("{\"dualPane\":true}")
    );

    let missing = dispatch(
        &mut state,
        &request("get_db_setting", 7, json!({ "key": "missing" })),
    );
    let Dispatch::Reply(missing_response) = missing else {
        panic!("expected missing settings reply");
    };
    assert!(missing_response.result.unwrap().is_null());

    let _ = fs::remove_file(db_path);
}

#[test]
fn inspection_methods_use_core_logic() {
    let left = temp_file("left", b"alpha\nbravo\n");
    let right = temp_file("right", b"alpha\ncharlie\n");
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    let preview = dispatch(
        &mut state,
        &request(
            "read_file_preview",
            10,
            json!({ "path": left.to_string_lossy(), "maxSize": 1024 }),
        ),
    );
    let Dispatch::Reply(preview_response) = preview else {
        panic!("expected preview reply");
    };
    let preview_value = preview_response.result.unwrap();
    assert_eq!(preview_value["file_type"], "text");
    assert_eq!(preview_value["content"], "alpha\nbravo\n");

    let checksum = dispatch(
        &mut state,
        &request(
            "compute_checksum",
            11,
            json!({ "path": left.to_string_lossy() }),
        ),
    );
    let Dispatch::Reply(checksum_response) = checksum else {
        panic!("expected checksum reply");
    };
    assert!(
        checksum_response.result.unwrap()["sha256"]
            .as_str()
            .unwrap()
            .len()
            >= 64
    );

    let compare = dispatch(
        &mut state,
        &request(
            "compare_files",
            12,
            json!({
                "pathA": left.to_string_lossy(),
                "pathB": right.to_string_lossy(),
            }),
        ),
    );
    let Dispatch::Reply(compare_response) = compare else {
        panic!("expected compare reply");
    };
    let compare_value = compare_response.result.unwrap();
    assert_eq!(compare_value["identical"], false);
    assert!(compare_value["changed"].as_u64().unwrap() >= 1);

    let archive_path = left.with_file_name(format!(
        "simplefile-service-dispatch-archive-{}.zip",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("time")
            .as_nanos()
    ));
    let create_archive = dispatch(
        &mut state,
        &request(
            "create_archive",
            13,
            json!({
                "paths": [left.to_string_lossy()],
                "archivePath": archive_path.to_string_lossy(),
                "format": "zip",
            }),
        ),
    );
    let Dispatch::Reply(create_archive_response) = create_archive else {
        panic!("expected create archive reply");
    };
    assert!(create_archive_response.error.is_none());
    assert!(archive_path.exists());

    let list_archive = dispatch(
        &mut state,
        &request(
            "list_archive",
            14,
            json!({ "path": archive_path.to_string_lossy() }),
        ),
    );
    let Dispatch::Reply(list_archive_response) = list_archive else {
        panic!("expected list archive reply");
    };
    let archive_value = list_archive_response.result.unwrap();
    assert_eq!(archive_value["format"], "zip");
    assert_eq!(
        archive_value["entries"][0]["name"].as_str().unwrap(),
        left.file_name().unwrap().to_string_lossy()
    );

    let extract_dir = archive_path.with_file_name(format!(
        "simplefile-service-dispatch-extract-{}",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("time")
            .as_nanos()
    ));
    let extract_archive = dispatch(
        &mut state,
        &request(
            "extract_archive",
            15,
            json!({
                "archivePath": archive_path.to_string_lossy(),
                "destination": extract_dir.to_string_lossy(),
            }),
        ),
    );
    let Dispatch::Reply(extract_archive_response) = extract_archive else {
        panic!("expected extract archive reply");
    };
    assert!(extract_archive_response.error.is_none());
    assert!(extract_dir.join(left.file_name().unwrap()).exists());

    let _ = fs::remove_file(left);
    let _ = fs::remove_file(right);
    let _ = fs::remove_file(archive_path);
    let _ = fs::remove_dir_all(extract_dir);
}

fn assert_domain_method_is_wired(state: &mut SessionState, method: &str, id: u64, params: Value) {
    match dispatch(state, &request(method, id, params)) {
        Dispatch::Reply(response) => {
            if let Some(error) = response.error {
                assert_ne!(
                    error.code, ERR_METHOD_NOT_FOUND,
                    "{method} must be implemented, got {}",
                    error.message
                );
                assert!(
                    !error.message.contains("IPC MVP"),
                    "{method} still uses the leftover MVP stub: {}",
                    error.message
                );
            }
        }
        Dispatch::InstallUpdate { .. }
        | Dispatch::DiskCleanup { .. }
        | Dispatch::CancelDiskCleanup { .. }
        | Dispatch::DuplicateCheck { .. }
        | Dispatch::CancelDuplicateCheck { .. } => {}
        other => panic!("{method} produced unexpected dispatch {other:?}"),
    }
}

#[test]
fn leftover_domain_methods_are_wired() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("leftover-domain-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    // Methods with no required params. Skip prepare_rar_install (downloads)
    // and check_for_update (hits the network unless a manifest is injected).
    for (id, method) in [
        (20u64, "get_all_tags"),
        (21, "get_all_file_tags"),
        (22, "load_smart_folders"),
        (23, "check_rar_installed"),
        (24, "get_app_version"),
        (25, "get_app_about_info"),
        (26, "cancel_disk_cleanup"),
        (27, "cancel_duplicate_check"),
        (28, "install_update"),
    ] {
        assert_domain_method_is_wired(&mut state, method, id, json!({}));
    }

    for (id, method) in [
        (40u64, "create_tag"),
        (41, "update_tag"),
        (42, "delete_tag"),
        (43, "get_tags_for_path"),
        (44, "set_tags_for_path"),
        (45, "get_files_with_tag"),
        (46, "save_smart_folder"),
        (47, "delete_smart_folder"),
        (48, "get_git_status"),
        (49, "get_git_file_statuses"),
        (50, "git_pull"),
        (51, "git_push"),
        (52, "disk_cleanup"),
        (53, "duplicate_check"),
        (54, "discard_rar_install"),
        (55, "install_rar"),
        (56, "open_terminal"),
        (57, "open_powershell_admin"),
    ] {
        assert_domain_method_is_wired(&mut state, method, id, json!({}));
    }

    let _ = fs::remove_file(db_path);
}

#[test]
fn tags_and_smart_folders_round_trip_through_core() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("tags-smart-folders-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let app_data = db_path.with_file_name(format!(
        "simplefile-app-data-{}",
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("time")
            .as_nanos()
    ));
    fs::create_dir_all(&app_data).expect("app data dir");
    let _db_env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let _app_env = EnvVarGuard::set("SIMPLEFILE_APP_DATA_DIR", &app_data);
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    let seeded = dispatch(&mut state, &request("get_all_tags", 60, json!({})));
    let Dispatch::Reply(seeded_response) = seeded else {
        panic!("expected seeded tags reply");
    };
    let seeded_tags = seeded_response.result.unwrap();
    assert!(
        seeded_tags.as_array().map(|tags| tags.len()).unwrap_or(0) >= 5,
        "backend should seed default tags"
    );

    let created = dispatch(
        &mut state,
        &request(
            "create_tag",
            61,
            json!({ "name": "Review", "color": "#123456" }),
        ),
    );
    let Dispatch::Reply(created_response) = created else {
        panic!("expected create_tag reply");
    };
    let created_tag = created_response.result.unwrap();
    let tag_id = created_tag["id"].as_i64().expect("tag id");
    assert_eq!(created_tag["name"], "Review");

    let set_tags = dispatch(
        &mut state,
        &request(
            "set_tags_for_path",
            62,
            json!({ "path": "C:\\file.txt", "tagIds": [tag_id] }),
        ),
    );
    let Dispatch::Reply(set_tags_response) = set_tags else {
        panic!("expected set_tags_for_path reply");
    };
    assert!(set_tags_response.error.is_none());

    let path_tags = dispatch(
        &mut state,
        &request("get_tags_for_path", 63, json!({ "path": "C:\\file.txt" })),
    );
    let Dispatch::Reply(path_tags_response) = path_tags else {
        panic!("expected get_tags_for_path reply");
    };
    assert_eq!(
        path_tags_response.result.unwrap()[0]["id"].as_i64(),
        Some(tag_id)
    );

    let save = dispatch(
        &mut state,
        &request(
            "save_smart_folder",
            64,
            json!({
                "folder": {
                    "id": "sf-review",
                    "name": "Review",
                    "icon": null,
                    "search_options": {
                        "query": "review",
                        "search_path": "C:\\",
                        "case_sensitive": false,
                        "include_hidden": false,
                        "file_types": [],
                        "max_results": 200,
                        "max_depth": 6,
                        "search_id": null,
                        "content_search": false,
                        "min_size": null,
                        "max_size": null,
                        "date_after": null,
                        "date_before": null
                    }
                }
            }),
        ),
    );
    let Dispatch::Reply(save_response) = save else {
        panic!("expected save_smart_folder reply");
    };
    assert!(save_response.error.is_none());

    let loaded = dispatch(&mut state, &request("load_smart_folders", 65, json!({})));
    let Dispatch::Reply(loaded_response) = loaded else {
        panic!("expected load_smart_folders reply");
    };
    assert_eq!(loaded_response.result.unwrap()[0]["id"], "sf-review");

    let git_status = dispatch(
        &mut state,
        &request(
            "get_git_status",
            66,
            json!({ "path": std::env::temp_dir().to_string_lossy() }),
        ),
    );
    let Dispatch::Reply(git_status_response) = git_status else {
        panic!("expected get_git_status reply");
    };
    assert!(git_status_response.error.is_none());
    assert_eq!(git_status_response.result.unwrap()["is_repo"], false);

    let rar = dispatch(&mut state, &request("check_rar_installed", 67, json!({})));
    let Dispatch::Reply(rar_response) = rar else {
        panic!("expected check_rar_installed reply");
    };
    assert!(rar_response.result.unwrap().is_boolean());

    let _ = fs::remove_file(db_path);
    let _ = fs::remove_dir_all(app_data);
}
