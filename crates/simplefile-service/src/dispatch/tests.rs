use super::handlers::{auth_token_matches, constant_time_eq};
use super::*;
use crate::remote::provider::{RemoteProvider, RemoteProviderFactory};
use crate::remote::secrets::{MemoryRemoteSecretStore, RemoteSecret, RemoteSecretStore};
use serde_json::{json, Value};
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::RemoteProfile;
use simplefile_ipc::rpc::JsonRpcRequest;
use simplefile_ipc::{
    ERR_HOST_OWNED, ERR_INVALID_REQUEST, ERR_METHOD_NOT_FOUND, HANDSHAKE_METHOD, PREFIX_HOST_OWNED,
};
use std::ffi::OsString;
use std::fs;
use std::sync::Arc;
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
fn move_to_trash_empty_selection_returns_tracked_paths_array() {
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    let outcome = dispatch(
        &mut state,
        &request("move_to_trash", 30, json!({ "paths": [] })),
    );

    let Dispatch::Reply(response) = outcome else {
        panic!("expected reply");
    };
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

    let batch = dispatch(
        &mut state,
        &request(
            "get_db_settings",
            8,
            json!({ "keys": ["winui.layout", "missing"] }),
        ),
    );
    let Dispatch::Reply(batch_response) = batch else {
        panic!("expected batch settings reply");
    };
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
    let Dispatch::Reply(binary_compare_response) = binary_compare else {
        panic!("expected binary compare reply");
    };
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
    let Dispatch::Reply(create_archive_response) = create_archive else {
        panic!("expected create archive reply");
    };
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
            16,
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
    let _ = fs::remove_file(binary_left);
    let _ = fs::remove_file(binary_right);
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

    let capabilities = dispatch(
        &mut state,
        &request("get_archive_capabilities", 67, json!({})),
    );
    let Dispatch::Reply(capabilities_response) = capabilities else {
        panic!("expected get_archive_capabilities reply");
    };
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
fn remote_profile_ipc_saves_and_lists_profiles_without_secret_material() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("remote-profiles-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let mut state = SessionState {
        handshake_done: true,
        ..SessionState::default()
    };

    let save = dispatch(
        &mut state,
        &request(
            "remote_save_profile",
            80,
            json!({
                "profile": {
                    "id": null,
                    "name": "Production SFTP",
                    "protocol": "sftp",
                    "host": "files.example.com",
                    "port": 22,
                    "username": "deploy",
                    "root_path": "var/www",
                    "auth_kind": "password",
                    "insecure_plain_ftp": false,
                    "passive_mode": true,
                    "credential_target": null,
                    "trusted_host_fingerprint": null
                },
                "secret": "not-returned"
            }),
        ),
    );
    let Dispatch::Reply(save_response) = save else {
        panic!("expected remote_save_profile reply");
    };
    assert!(
        save_response.error.is_none(),
        "unexpected save error: {:?}",
        save_response.error
    );
    let saved = save_response.result.expect("saved profile");
    assert_eq!(saved["name"], "Production SFTP");
    assert_eq!(saved["root_path"], "/var/www");
    assert!(saved["id"].as_str().is_some_and(|value| !value.is_empty()));
    assert!(saved.get("secret").is_none());

    let list = dispatch(&mut state, &request("remote_list_profiles", 81, json!({})));
    let Dispatch::Reply(list_response) = list else {
        panic!("expected remote_list_profiles reply");
    };
    assert!(
        list_response.error.is_none(),
        "unexpected list error: {:?}",
        list_response.error
    );
    let profiles = list_response
        .result
        .expect("profile list")
        .as_array()
        .expect("profiles array")
        .clone();
    assert_eq!(profiles.len(), 1);
    assert_eq!(profiles[0]["credential_target"], saved["credential_target"]);
    assert!(profiles[0].get("secret").is_none());

    let _ = fs::remove_file(db_path);
}

struct DispatchFakeProvider;

impl RemoteProvider for DispatchFakeProvider {
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
        Ok(DirectoryListing {
            path: path.to_string(),
            parent: None,
            entries: vec![FileEntry {
                name: "release".to_string(),
                path: "/release".to_string(),
                is_dir: true,
                is_symlink: false,
                is_hidden: false,
                is_system: false,
                size: 0,
                modified: "2026-09-21T00:00:00Z".to_string(),
                extension: "".to_string(),
                permissions: None,
                symlink_target: None,
                git_status: None,
            }],
            is_network: true,
        })
    }

    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
        if path != "/" || name != "incoming" {
            return Err(format!("unexpected create {path}/{name}"));
        }

        Ok(FileEntry {
            name: "incoming".to_string(),
            path: "/incoming".to_string(),
            is_dir: true,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: 0,
            modified: "2026-09-21T00:00:00Z".to_string(),
            extension: "".to_string(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        })
    }

    fn rename_entry(&mut self, path: &str, new_name: &str) -> Result<FileEntry, String> {
        if path != "/incoming" || new_name != "archive" {
            return Err(format!("unexpected rename {path} -> {new_name}"));
        }

        Ok(FileEntry {
            name: "archive".to_string(),
            path: "/archive".to_string(),
            is_dir: true,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: 0,
            modified: "2026-09-21T00:00:00Z".to_string(),
            extension: "".to_string(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        })
    }

    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
        if paths != ["/archive"] {
            return Err(format!("unexpected delete {paths:?}"));
        }

        Ok(paths.to_vec())
    }

    fn read_file(&mut self, path: &str) -> Result<Vec<u8>, String> {
        if path != "/release/app.txt" {
            return Err(format!("unexpected read {path}"));
        }

        Ok(b"remote bytes".to_vec())
    }

    fn write_file(&mut self, path: &str, bytes: &[u8]) -> Result<FileEntry, String> {
        if path != "/incoming/app.txt" || bytes != b"local bytes" {
            return Err(format!("unexpected write {path} {bytes:?}"));
        }

        Ok(FileEntry {
            name: "app.txt".to_string(),
            path: "/incoming/app.txt".to_string(),
            is_dir: false,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: bytes.len() as u64,
            modified: "2026-09-21T00:00:00Z".to_string(),
            extension: "txt".to_string(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        })
    }
}

struct DispatchFakeProviderFactory;

impl RemoteProviderFactory for DispatchFakeProviderFactory {
    fn connect(
        &self,
        _profile: &RemoteProfile,
        _secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        Ok(Box::new(DispatchFakeProvider))
    }
}

struct SecretCheckingProviderFactory;

impl RemoteProviderFactory for SecretCheckingProviderFactory {
    fn connect(
        &self,
        _profile: &RemoteProfile,
        secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        match secret {
            Some(RemoteSecret::Password(value)) if value == "saved-secret" => {
                Ok(Box::new(DispatchFakeProvider))
            }
            other => Err(format!("unexpected remote secret: {other:?}")),
        }
    }
}

struct PrivateKeySecretCheckingProviderFactory;

impl RemoteProviderFactory for PrivateKeySecretCheckingProviderFactory {
    fn connect(
        &self,
        _profile: &RemoteProfile,
        secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        match secret {
            Some(RemoteSecret::PrivateKeyPassphrase(value)) if value == "key-passphrase" => {
                Ok(Box::new(DispatchFakeProvider))
            }
            other => Err(format!("unexpected private key secret: {other:?}")),
        }
    }
}

#[test]
fn remote_session_ipc_connects_lists_and_disconnects_with_provider() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("remote-session-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let mut state = SessionState::with_remote_provider_factory(DispatchFakeProviderFactory);
    state.handshake_done = true;

    let save = dispatch(
        &mut state,
        &request(
            "remote_save_profile",
            90,
            json!({
                "profile": {
                    "id": null,
                    "name": "Production SFTP",
                    "protocol": "sftp",
                    "host": "files.example.com",
                    "port": 22,
                    "username": "deploy",
                    "root_path": "/",
                    "auth_kind": "password",
                    "insecure_plain_ftp": false,
                    "passive_mode": true,
                    "credential_target": null,
                    "trusted_host_fingerprint": null
                },
                "secret": "not-returned"
            }),
        ),
    );
    let Dispatch::Reply(save_response) = save else {
        panic!("expected remote_save_profile reply");
    };
    let saved = save_response.result.expect("saved profile");
    let profile_id = saved["id"].as_str().expect("profile id");

    let connect = dispatch(
        &mut state,
        &request(
            "remote_connect",
            91,
            json!({ "profileId": profile_id, "secret": "not-returned" }),
        ),
    );
    let Dispatch::Reply(connect_response) = connect else {
        panic!("expected remote_connect reply");
    };
    assert!(
        connect_response.error.is_none(),
        "unexpected connect error: {:?}",
        connect_response.error
    );
    let session = connect_response.result.expect("remote session");
    assert_eq!(session["profile_id"], profile_id);
    let session_id = session["session_id"]
        .as_str()
        .expect("session id")
        .to_string();

    let list = dispatch(
        &mut state,
        &request(
            "remote_list_directory",
            92,
            json!({ "remoteSessionId": session_id, "path": "/" }),
        ),
    );
    let Dispatch::Reply(list_response) = list else {
        panic!("expected remote_list_directory reply");
    };
    assert!(list_response.error.is_none());
    let listing = list_response.result.expect("remote listing");
    assert_eq!(listing["path"], "/");
    assert_eq!(listing["entries"][0]["name"], "release");
    assert_eq!(listing["is_network"], true);

    let disconnect = dispatch(
        &mut state,
        &request(
            "remote_disconnect",
            93,
            json!({ "remoteSessionId": session_id }),
        ),
    );
    let Dispatch::Reply(disconnect_response) = disconnect else {
        panic!("expected remote_disconnect reply");
    };
    assert!(disconnect_response.error.is_none());

    let after_disconnect = dispatch(
        &mut state,
        &request(
            "remote_list_directory",
            94,
            json!({ "remoteSessionId": session_id, "path": "/" }),
        ),
    );
    let Dispatch::Reply(after_disconnect_response) = after_disconnect else {
        panic!("expected remote_list_directory reply");
    };
    let error = after_disconnect_response
        .error
        .expect("missing session error");
    assert!(error.message.contains("remote session not found"));

    let _ = fs::remove_file(db_path);
}

#[test]
fn remote_profile_ipc_stores_uses_and_deletes_secrets_by_credential_target() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("remote-secret-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let secret_store = Arc::new(MemoryRemoteSecretStore::default());
    let mut state = SessionState::with_remote_provider_factory_and_secret_store(
        SecretCheckingProviderFactory,
        secret_store.clone(),
    );
    state.handshake_done = true;

    let save = dispatch(
        &mut state,
        &request(
            "remote_save_profile",
            110,
            json!({
                "profile": {
                    "id": null,
                    "name": "Production SFTP",
                    "protocol": "sftp",
                    "host": "files.example.com",
                    "port": 22,
                    "username": "deploy",
                    "root_path": "/",
                    "auth_kind": "password",
                    "insecure_plain_ftp": false,
                    "passive_mode": true,
                    "credential_target": null,
                    "trusted_host_fingerprint": null
                },
                "secret": "saved-secret"
            }),
        ),
    );
    let Dispatch::Reply(save_response) = save else {
        panic!("expected remote_save_profile reply");
    };
    assert!(
        save_response.error.is_none(),
        "unexpected save error: {:?}",
        save_response.error
    );
    let saved = save_response.result.expect("saved profile");
    let profile_id = saved["id"].as_str().expect("profile id");
    let credential_target = saved["credential_target"]
        .as_str()
        .expect("credential target")
        .to_string();
    assert_eq!(
        secret_store
            .read_secret(&credential_target)
            .expect("read saved secret"),
        Some(RemoteSecret::Password("saved-secret".to_string()))
    );

    let connect = dispatch(
        &mut state,
        &request("remote_connect", 111, json!({ "profileId": profile_id })),
    );
    let Dispatch::Reply(connect_response) = connect else {
        panic!("expected remote_connect reply");
    };
    assert!(
        connect_response.error.is_none(),
        "unexpected connect error: {:?}",
        connect_response.error
    );

    let delete = dispatch(
        &mut state,
        &request(
            "remote_delete_profile",
            112,
            json!({ "profileId": profile_id }),
        ),
    );
    let Dispatch::Reply(delete_response) = delete else {
        panic!("expected remote_delete_profile reply");
    };
    assert!(delete_response.error.is_none());
    assert_eq!(
        secret_store
            .read_secret(&credential_target)
            .expect("read deleted secret"),
        None
    );

    let _ = fs::remove_file(db_path);
}

#[test]
fn remote_profile_ipc_stores_private_key_passphrases_by_credential_target() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("remote-private-key-secret-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let secret_store = Arc::new(MemoryRemoteSecretStore::default());
    let mut state = SessionState::with_remote_provider_factory_and_secret_store(
        PrivateKeySecretCheckingProviderFactory,
        secret_store.clone(),
    );
    state.handshake_done = true;

    let save = dispatch(
        &mut state,
        &request(
            "remote_save_profile",
            113,
            json!({
                "profile": {
                    "id": null,
                    "name": "Production SFTP Key",
                    "protocol": "sftp",
                    "host": "files.example.com",
                    "port": 22,
                    "username": "deploy",
                    "root_path": "/",
                    "auth_kind": "private-key",
                    "insecure_plain_ftp": false,
                    "passive_mode": true,
                    "credential_target": null,
                    "private_key_path": "C:\\Users\\raz00\\.ssh\\id_ed25519",
                    "trusted_host_fingerprint": null
                },
                "secret": "key-passphrase"
            }),
        ),
    );
    let Dispatch::Reply(save_response) = save else {
        panic!("expected remote_save_profile reply");
    };
    assert!(
        save_response.error.is_none(),
        "unexpected save error: {:?}",
        save_response.error
    );
    let saved = save_response.result.expect("saved profile");
    let profile_id = saved["id"].as_str().expect("profile id");
    let credential_target = saved["credential_target"]
        .as_str()
        .expect("credential target")
        .to_string();
    assert_eq!(
        secret_store
            .read_secret(&credential_target)
            .expect("read saved secret"),
        Some(RemoteSecret::PrivateKeyPassphrase(
            "key-passphrase".to_string()
        ))
    );

    let connect = dispatch(
        &mut state,
        &request("remote_connect", 114, json!({ "profileId": profile_id })),
    );
    let Dispatch::Reply(connect_response) = connect else {
        panic!("expected remote_connect reply");
    };
    assert!(
        connect_response.error.is_none(),
        "unexpected connect error: {:?}",
        connect_response.error
    );

    let _ = fs::remove_file(db_path);
}

#[test]
fn remote_session_ipc_creates_renames_and_deletes_entries_with_provider() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("remote-mutation-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let mut state = SessionState::with_remote_provider_factory(DispatchFakeProviderFactory);
    state.handshake_done = true;

    let save = dispatch(
        &mut state,
        &request(
            "remote_save_profile",
            100,
            json!({
                "profile": {
                    "id": null,
                    "name": "Production SFTP",
                    "protocol": "sftp",
                    "host": "files.example.com",
                    "port": 22,
                    "username": "deploy",
                    "root_path": "/",
                    "auth_kind": "password",
                    "insecure_plain_ftp": false,
                    "passive_mode": true,
                    "credential_target": null,
                    "trusted_host_fingerprint": null
                },
                "secret": "not-returned"
            }),
        ),
    );
    let Dispatch::Reply(save_response) = save else {
        panic!("expected remote_save_profile reply");
    };
    let saved = save_response.result.expect("saved profile");
    let profile_id = saved["id"].as_str().expect("profile id");

    let connect = dispatch(
        &mut state,
        &request(
            "remote_connect",
            101,
            json!({ "profileId": profile_id, "secret": "not-returned" }),
        ),
    );
    let Dispatch::Reply(connect_response) = connect else {
        panic!("expected remote_connect reply");
    };
    let session = connect_response.result.expect("remote session");
    let session_id = session["session_id"]
        .as_str()
        .expect("session id")
        .to_string();

    let create = dispatch(
        &mut state,
        &request(
            "remote_create_directory",
            102,
            json!({ "remoteSessionId": session_id, "path": "/", "name": "incoming" }),
        ),
    );
    let Dispatch::Reply(create_response) = create else {
        panic!("expected remote_create_directory reply");
    };
    assert!(
        create_response.error.is_none(),
        "unexpected create error: {:?}",
        create_response.error
    );
    let created = create_response.result.expect("created entry");
    assert_eq!(created["name"], "incoming");
    assert_eq!(created["path"], "/incoming");
    assert_eq!(created["is_dir"], true);

    let rename = dispatch(
        &mut state,
        &request(
            "remote_rename_entry",
            103,
            json!({ "remoteSessionId": session_id, "path": "/incoming", "newName": "archive" }),
        ),
    );
    let Dispatch::Reply(rename_response) = rename else {
        panic!("expected remote_rename_entry reply");
    };
    assert!(rename_response.error.is_none());
    let renamed = rename_response.result.expect("renamed entry");
    assert_eq!(renamed["name"], "archive");
    assert_eq!(renamed["path"], "/archive");

    let delete = dispatch(
        &mut state,
        &request(
            "remote_delete_entries",
            104,
            json!({ "remoteSessionId": session_id, "paths": ["/archive"] }),
        ),
    );
    let Dispatch::Reply(delete_response) = delete else {
        panic!("expected remote_delete_entries reply");
    };
    assert!(delete_response.error.is_none());
    assert_eq!(
        delete_response.result.expect("deleted paths"),
        json!(["/archive"])
    );

    let _ = fs::remove_file(db_path);
}

#[test]
fn remote_transfer_ipc_downloads_and_uploads_files_with_provider() {
    let _lock = metadata_db_env_lock().lock().expect("env lock");
    let db_path = temp_file("remote-transfer-db", b"");
    fs::remove_file(&db_path).expect("remove seed temp file");
    let _env = EnvVarGuard::set("SIMPLEFILE_METADATA_DB", &db_path);
    let mut state = SessionState::with_remote_provider_factory(DispatchFakeProviderFactory);
    state.handshake_done = true;

    let save = dispatch(
        &mut state,
        &request(
            "remote_save_profile",
            120,
            json!({
                "profile": {
                    "id": null,
                    "name": "Production SFTP",
                    "protocol": "sftp",
                    "host": "files.example.com",
                    "port": 22,
                    "username": "deploy",
                    "root_path": "/",
                    "auth_kind": "password",
                    "insecure_plain_ftp": false,
                    "passive_mode": true,
                    "credential_target": null,
                    "trusted_host_fingerprint": null
                },
                "secret": "not-returned"
            }),
        ),
    );
    let Dispatch::Reply(save_response) = save else {
        panic!("expected remote_save_profile reply");
    };
    let saved = save_response.result.expect("saved profile");
    let profile_id = saved["id"].as_str().expect("profile id");

    let connect = dispatch(
        &mut state,
        &request(
            "remote_connect",
            121,
            json!({ "profileId": profile_id, "secret": "not-returned" }),
        ),
    );
    let Dispatch::Reply(connect_response) = connect else {
        panic!("expected remote_connect reply");
    };
    let session = connect_response.result.expect("remote session");
    let session_id = session["session_id"]
        .as_str()
        .expect("session id")
        .to_string();

    let download_path = temp_file("remote-download", b"");
    fs::remove_file(&download_path).expect("remove seed download file");
    let download = dispatch(
        &mut state,
        &request(
            "remote_download_file",
            122,
            json!({
                "remoteSessionId": session_id,
                "remotePath": "/release/app.txt",
                "localPath": download_path
            }),
        ),
    );
    let Dispatch::Reply(download_response) = download else {
        panic!("expected remote_download_file reply");
    };
    assert!(download_response.error.is_none());
    assert_eq!(
        fs::read(&download_path).expect("read downloaded file"),
        b"remote bytes"
    );

    let upload_path = temp_file("remote-upload", b"local bytes");
    let upload = dispatch(
        &mut state,
        &request(
            "remote_upload_file",
            123,
            json!({
                "remoteSessionId": session_id,
                "localPath": upload_path,
                "remotePath": "/incoming/app.txt"
            }),
        ),
    );
    let Dispatch::Reply(upload_response) = upload else {
        panic!("expected remote_upload_file reply");
    };
    assert!(upload_response.error.is_none());
    let uploaded = upload_response.result.expect("uploaded entry");
    assert_eq!(uploaded["name"], "app.txt");
    assert_eq!(uploaded["path"], "/incoming/app.txt");
    assert_eq!(uploaded["size"], 11);

    let _ = fs::remove_file(upload_path);
    let _ = fs::remove_file(download_path);
    let _ = fs::remove_file(db_path);
}
