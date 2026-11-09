use super::handlers::{auth_token_matches, constant_time_eq};
use super::*;
use serde_json::{json, Value};
use simplefile_ipc::rpc::JsonRpcRequest;
use simplefile_ipc::{
    ERR_HOST_OWNED, ERR_INVALID_PARAMS, ERR_INVALID_REQUEST, ERR_METHOD_NOT_FOUND,
    HANDSHAKE_METHOD, PREFIX_HOST_OWNED,
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
    let response = reply(outcome);
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
    let ready = reply(handshake);
    assert!(ready.error.is_none());
    assert!(state.handshake_done);

    let home = dispatch(&mut state, &request("get_home_dir", 2, json!({})));
    let response = reply(home);
    let path = response.result.unwrap().as_str().unwrap().to_string();
    assert!(!path.is_empty());
}

#[test]
fn move_to_trash_empty_selection_returns_tracked_paths_array() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    let outcome = dispatch(
        &mut state,
        &request("move_to_trash", 30, json!({ "paths": [] })),
    );

    let response = reply(outcome);
    assert_eq!(response.result.unwrap(), json!([]));
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
    let ready = reply(handshake);
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
    let ready = reply(handshake);
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
                "partialHashBytes": 262144,
                "maxDepth": 4,
                "excludePatterns": ["@eaDir", "#recycle"],
                "networkMode": true,
                "operationId": "dup-1"
            }),
        ),
    );
    match duplicate {
        Dispatch::DuplicateCheck {
            directory,
            min_size,
            partial_hash_bytes,
            max_depth,
            exclude_patterns,
            network_mode,
            operation_id,
            ..
        } => {
            assert_eq!(directory, "C:\\");
            assert_eq!(min_size, Some(1));
            assert_eq!(partial_hash_bytes, Some(262144));
            assert_eq!(max_depth, Some(4));
            assert_eq!(
                exclude_patterns,
                vec!["@eaDir".to_string(), "#recycle".to_string()]
            );
            assert_eq!(network_mode, Some(true));
            assert_eq!(operation_id.as_deref(), Some("dup-1"));
        }
        other => panic!("expected DuplicateCheck, got {other:?}"),
    }

    let cancel = dispatch(
        &mut state,
        &request(
            "cancel_duplicate_check",
            5,
            json!({ "operationId": "dup-1" }),
        ),
    );
    assert!(matches!(
        cancel,
        Dispatch::CancelDuplicateCheck {
            operation_id: Some(id),
            ..
        } if id == "dup-1"
    ));

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
    let response = reply(outcome);
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
    let response = reply(outcome);
    assert_eq!(response.error.unwrap().code, ERR_METHOD_NOT_FOUND);
}

fn register_archive_engine_request(id: u64, pipe_name: &str, extension: &str) -> JsonRpcRequest {
    request(
        "register_archive_engine",
        id,
        json!({
            "pluginId": "com.test.validation",
            "pipeName": pipe_name,
            "formats": [{
                "extension": extension,
                "canList": true,
                "canExtract": true,
                "canCreate": false,
                "canModify": false,
                "engineName": "Validation"
            }]
        }),
    )
}

#[test]
fn register_archive_engine_rejects_unsafe_pipe_names_before_connecting() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };
    let too_long = "a".repeat(300);
    for (id, pipe_name) in [
        (6u64, r"..\evil"),
        (7, too_long.as_str()),
        (8, r"\\.\pipe\other"),
        (9, ""),
    ] {
        // A direct reply (not a deferred job) proves no pipe was opened.
        let outcome = dispatch(
            &mut state,
            &register_archive_engine_request(id, pipe_name, ".validation"),
        );
        let Dispatch::Reply(response) = outcome else {
            panic!("{pipe_name:?} must be rejected without touching the pipe");
        };
        let error = response.error.expect("invalid pipe name is an error");
        assert_eq!(
            error.code, ERR_INVALID_PARAMS,
            "{pipe_name:?}: {}",
            error.message
        );
    }
}

#[test]
fn register_archive_engine_with_only_built_in_formats_is_an_error() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };
    let response = reply(dispatch(
        &mut state,
        &register_archive_engine_request(10, "simplefile-test-unused-pipe", ".zip"),
    ));
    let error = response.error.expect("only built-in formats is an error");
    assert!(
        error.message.contains("no registrable formats"),
        "{}",
        error.message
    );
}

#[test]
fn unregister_archive_engine_for_unknown_plugin_is_ok_null() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };
    let outcome = dispatch(
        &mut state,
        &request(
            "unregister_archive_engine",
            5,
            json!({ "pluginId": "com.test.never-registered" }),
        ),
    );
    let response = reply(outcome);
    assert!(response.error.is_none(), "{:?}", response.error);
    assert_eq!(response.result, Some(Value::Null));
}

#[test]
fn settings_methods_round_trip_through_metadata_db() {
    let _lock = metadata_db_env_lock()
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
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
    let set_response = reply(set);
    assert!(set_response.error.is_none());

    let get = dispatch(
        &mut state,
        &request("get_db_setting", 6, json!({ "key": "winui.layout" })),
    );
    let get_response = reply(get);
    assert_eq!(
        get_response.result.unwrap().as_str(),
        Some("{\"dualPane\":true}")
    );

    let missing = dispatch(
        &mut state,
        &request("get_db_setting", 7, json!({ "key": "missing" })),
    );
    let missing_response = reply(missing);
    assert!(missing_response.result.unwrap().is_null());

    let batch = dispatch(
        &mut state,
        &request(
            "get_db_settings",
            8,
            json!({ "keys": ["winui.layout", "missing"] }),
        ),
    );
    let batch_response = reply(batch);
    let batch_result = batch_response.result.unwrap();
    assert_eq!(
        batch_result["winui.layout"].as_str(),
        Some("{\"dualPane\":true}")
    );
    assert!(batch_result["missing"].is_null());

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
    let preview_response = reply(preview);
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
    let checksum_response = reply(checksum);
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
    let compare_response = reply(compare);
    let compare_value = compare_response.result.unwrap();
    assert_eq!(compare_value["identical"], false);
    assert_eq!(compare_value["comparison_type"], "text");
    assert!(compare_value["changed"].as_u64().unwrap() >= 1);

    let binary_left = temp_file("binary-left", &[0, 1, 2, 3]);
    let binary_right = temp_file("binary-right", &[0, 1, 9, 3]);
    let binary_compare = dispatch(
        &mut state,
        &request(
            "compare_files",
            13,
            json!({
                "pathA": binary_left.to_string_lossy(),
                "pathB": binary_right.to_string_lossy(),
            }),
        ),
    );
    let binary_compare_response = reply(binary_compare);
    let binary_compare_value = binary_compare_response.result.unwrap();
    assert_eq!(binary_compare_value["comparison_type"], "binary");
    assert_eq!(binary_compare_value["first_difference"], 2);
    assert_eq!(binary_compare_value["different_bytes"], 1);
    assert_eq!(binary_compare_value["binary_rows"][0]["offset"], 0);

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
            14,
            json!({
                "paths": [left.to_string_lossy()],
                "archivePath": archive_path.to_string_lossy(),
                "format": "zip",
            }),
        ),
    );
    let create_archive_response = reply(create_archive);
    assert!(create_archive_response.error.is_none());
    assert!(archive_path.exists());

    let list_archive = dispatch(
        &mut state,
        &request(
            "list_archive",
            15,
            json!({ "path": archive_path.to_string_lossy() }),
        ),
    );
    let list_archive_response = reply(list_archive);
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
            16,
            json!({
                "archivePath": archive_path.to_string_lossy(),
                "destination": extract_dir.to_string_lossy(),
            }),
        ),
    );
    let extract_archive_response = reply(extract_archive);
    assert!(extract_archive_response.error.is_none());
    assert!(extract_dir.join(left.file_name().unwrap()).exists());

    let _ = fs::remove_file(left);
    let _ = fs::remove_file(right);
    let _ = fs::remove_file(binary_left);
    let _ = fs::remove_file(binary_right);
    let _ = fs::remove_file(archive_path);
    let _ = fs::remove_dir_all(extract_dir);
}

/// Resolve a dispatch outcome to its JSON-RPC reply, running a deferred
/// blocking job inline the way the session loop's scheduler would.
fn reply(outcome: Dispatch) -> simplefile_ipc::rpc::JsonRpcResponse {
    match outcome {
        Dispatch::Reply(response) => response,
        Dispatch::Blocking { id, job } => match job.run() {
            Ok(value) => simplefile_ipc::rpc::JsonRpcResponse::result(id, value),
            Err(message) => simplefile_ipc::rpc::JsonRpcResponse::application_error(id, message),
        },
        other => panic!("expected a reply, got {other:?}"),
    }
}

fn assert_domain_method_is_wired(state: &mut SessionState, method: &str, id: u64, params: Value) {
    match dispatch(state, &request(method, id, params)) {
        // A deferred job is wired by construction; running it here would hit
        // the real disk, network or recycle bin.
        Dispatch::Blocking { .. } => {}
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
    let _lock = metadata_db_env_lock()
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    let db_path = temp_file("leftover-domain-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    // Methods with no required params. Skip check_for_update (hits the network
    // unless a manifest is injected).
    for (id, method) in [
        (20u64, "get_all_tags"),
        (21, "get_all_file_tags"),
        (22, "load_smart_folders"),
        (23, "get_archive_capabilities"),
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
        (49, "get_git_repository_status"),
        (50, "get_git_file_statuses"),
        (51, "git_stage_paths"),
        (52, "git_unstage_paths"),
        (53, "git_discard_paths"),
        (54, "git_diff_path"),
        (55, "git_commit"),
        (56, "git_fetch"),
        (57, "git_pull"),
        (58, "git_push"),
        (59, "disk_cleanup"),
        (60, "duplicate_check"),
        (63, "open_terminal"),
        (64, "open_powershell_admin"),
    ] {
        assert_domain_method_is_wired(&mut state, method, id, json!({}));
    }

    assert_domain_method_is_wired(
        &mut state,
        "restore_recycle_bin",
        65,
        json!({ "paths": [] }),
    );

    let _ = fs::remove_file(db_path);
}

#[test]
fn tags_and_smart_folders_round_trip_through_core() {
    let _lock = metadata_db_env_lock()
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
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
    let seeded_response = reply(seeded);
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
    let created_response = reply(created);
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
    let set_tags_response = reply(set_tags);
    assert!(set_tags_response.error.is_none());

    let path_tags = dispatch(
        &mut state,
        &request("get_tags_for_path", 63, json!({ "path": "C:\\file.txt" })),
    );
    let path_tags_response = reply(path_tags);
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
    let save_response = reply(save);
    assert!(save_response.error.is_none());

    let loaded = dispatch(&mut state, &request("load_smart_folders", 65, json!({})));
    let loaded_response = reply(loaded);
    assert_eq!(loaded_response.result.unwrap()[0]["id"], "sf-review");

    let git_status = dispatch(
        &mut state,
        &request(
            "get_git_status",
            66,
            json!({ "path": std::env::temp_dir().to_string_lossy() }),
        ),
    );
    let git_status_response = reply(git_status);
    assert!(git_status_response.error.is_none());
    assert_eq!(git_status_response.result.unwrap()["is_repo"], false);

    let capabilities = dispatch(
        &mut state,
        &request("get_archive_capabilities", 67, json!({})),
    );
    let capabilities_response = reply(capabilities);
    let formats = capabilities_response.result.unwrap()["formats"]
        .as_array()
        .expect("formats array")
        .clone();
    assert!(formats.iter().any(|format| {
        format["format"] == "rar" && format["can_create"] == false && format["can_extract"] == true
    }));

    let _ = fs::remove_file(db_path);
    let _ = fs::remove_dir_all(app_data);
}

#[test]
fn handshake_cannot_be_repeated_once_it_succeeded() {
    let mut state = SessionState {
        expected_token: Some("dev".to_string()),
        ..SessionState::default()
    };
    let params = json!({ "protocolVersion": simplefile_ipc::PROTOCOL_VERSION, "clientName": "t", "authToken": "dev" });
    let first = reply(dispatch(
        &mut state,
        &request("ipc.handshake", 1, params.clone()),
    ));
    assert!(first.error.is_none());
    let second = reply(dispatch(&mut state, &request("ipc.handshake", 2, params)));
    assert_eq!(
        second.error.expect("repeat refused").code,
        ERR_INVALID_REQUEST
    );
}

#[test]
fn repeated_bad_tokens_close_the_session() {
    let mut state = SessionState {
        expected_token: Some("dev".to_string()),
        ..SessionState::default()
    };
    let bad = json!({ "protocolVersion": simplefile_ipc::PROTOCOL_VERSION, "clientName": "t", "authToken": "nope" });
    for attempt in 1..5u64 {
        let outcome = dispatch(&mut state, &request("ipc.handshake", attempt, bad.clone()));
        assert!(matches!(outcome, Dispatch::Reply(_)), "attempt {attempt}");
    }
    let last = dispatch(&mut state, &request("ipc.handshake", 5, bad));
    assert!(
        matches!(last, Dispatch::Shutdown(_)),
        "fifth failure closes: {last:?}"
    );
}

#[test]
fn register_archive_engine_rejects_invalid_plugin_ids() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };
    for (id, plugin_id) in [(30u64, "Bad.Id"), (31, "a"), (32, "x/y")] {
        let outcome = dispatch(
            &mut state,
            &request(
                "register_archive_engine",
                id,
                json!({
                    "pluginId": plugin_id,
                    "pipeName": "simplefile-test-unused-pipe",
                    "formats": [{
                        "extension": ".validation",
                        "canList": true,
                        "canExtract": true,
                        "canCreate": false,
                        "canModify": false,
                        "engineName": "Validation"
                    }]
                }),
            ),
        );
        let Dispatch::Reply(response) = outcome else {
            panic!("{plugin_id:?} must be rejected without touching the pipe");
        };
        let error = response.error.expect("invalid plugin id is an error");
        assert_eq!(error.code, ERR_INVALID_PARAMS, "{plugin_id:?}");
        assert_eq!(
            error.message,
            "invalid params: pluginId must match ^[a-z0-9][a-z0-9.-]{1,63}$"
        );
    }
}

#[test]
fn archive_entry_shell_calls_and_mutations_run_on_the_general_lane() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };
    let root = std::env::temp_dir().join(format!(
        "simplefile-service-dispatch-lanes-{}",
        simplefile_core::utils::generate_operation_id()
    ));
    fs::create_dir_all(&root).unwrap();
    let archive = root.join("x.zip");
    fs::write(&archive, b"not opened").unwrap();
    let entry = format!(r"{}\docs\a.txt", archive.display());
    let folder = format!(r"{}\docs", archive.display());

    let cases = [
        ("open_file", json!({ "path": entry })),
        ("reveal_in_folder", json!({ "path": entry })),
        (
            "open_file_with",
            json!({ "path": entry, "application": "notepad.exe" }),
        ),
        ("create_directory", json!({ "path": folder, "name": "new" })),
        ("create_file", json!({ "path": folder, "name": "n.txt" })),
        ("rename_entry", json!({ "path": entry, "newName": "b.txt" })),
    ];
    for (index, (method, params)) in cases.into_iter().enumerate() {
        // The job is dropped unrun, so nothing is launched or changed.
        match dispatch(&mut state, &request(method, 40 + index as u64, params)) {
            Dispatch::Blocking { job, .. } => {
                assert_eq!(job.lane(), BlockingLane::General, "{method}");
            }
            other => panic!("{method} on an archive entry must leave the session loop: {other:?}"),
        }
    }

    let missing = root.join("missing.txt");
    let outcome = dispatch(
        &mut state,
        &request(
            "open_file",
            50,
            json!({ "path": missing.to_string_lossy() }),
        ),
    );
    assert!(
        matches!(outcome, Dispatch::Reply(_)),
        "a local path stays inline: {outcome:?}"
    );
    let _ = fs::remove_dir_all(root);
}
