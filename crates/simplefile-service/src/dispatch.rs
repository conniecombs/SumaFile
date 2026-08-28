use serde::Deserialize;
use serde_json::{json, Value};
use simplefile_core::dir_list::{ListDirectoryOptions, ListingMode};
use simplefile_core::models::SearchOptions;
use simplefile_core::utils::dirs_home;
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use simplefile_ipc::{
    APP_IDENTIFIER, DOMAIN_METHOD_COUNT, ERR_HOST_OWNED, ERR_INVALID_PARAMS, ERR_INVALID_REQUEST,
    ERR_METHOD_NOT_FOUND, HANDSHAKE_METHOD, HEALTH_METHOD, PREFIX_HOST_OWNED, PROTOCOL_VERSION,
    SHUTDOWN_METHOD,
};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

const APP_VERSION: &str = env!("CARGO_PKG_VERSION");

#[derive(Debug, Default)]
pub struct SessionState {
    pub handshake_done: bool,
    pub binary_hot_frames: Arc<AtomicBool>,
    pub expected_token: Option<String>,
    pub shutdown: bool,
    pub duplicate_check_cancel: Arc<AtomicBool>,
    pub disk_cleanup_cancel: Arc<AtomicBool>,
    pub folder_size_cancel: Arc<AtomicBool>,
    pub folder_item_count_cancel: Arc<AtomicBool>,
    pub count_items_cancel: Arc<AtomicBool>,
}

#[derive(Debug)]
pub(crate) enum Dispatch {
    Reply(JsonRpcResponse),
    ListDirectory {
        id: Option<Value>,
        path: String,
        options: Option<ListDirectoryOptions>,
    },
    CopyWithProgress {
        id: Option<Value>,
        params: ProgressCopyMoveParams,
    },
    MoveWithProgress {
        id: Option<Value>,
        params: ProgressCopyMoveParams,
    },
    CancelOperation {
        id: Option<Value>,
        operation_id: String,
    },
    SearchFiles {
        id: Option<Value>,
        options: SearchOptions,
    },
    CancelSearch {
        id: Option<Value>,
        search_id: String,
    },
    WatchDirectory {
        id: Option<Value>,
        path: String,
    },
    UnwatchDirectory {
        id: Option<Value>,
    },
    DuplicateCheck {
        id: Option<Value>,
        directory: String,
        min_size: Option<u64>,
        partial_hash_bytes: Option<u64>,
        operation_id: Option<String>,
    },
    CancelDuplicateCheck {
        id: Option<Value>,
    },
    DiskCleanup {
        id: Option<Value>,
        directory: String,
        size_threshold: Option<u64>,
        operation_id: Option<String>,
    },
    CancelDiskCleanup {
        id: Option<Value>,
    },
    InstallUpdate {
        id: Option<Value>,
    },
    GenerateThumbnail {
        id: Option<Value>,
        path: String,
        size: Option<u32>,
    },
    GenerateThumbnails {
        id: Option<Value>,
        paths: Vec<String>,
        size: Option<u32>,
    },
    CalculateFolderSize {
        id: Option<Value>,
        path: String,
        cancel: Arc<AtomicBool>,
    },
    CountFolderItems {
        id: Option<Value>,
        path: String,
        cancel: Arc<AtomicBool>,
    },
    GetFolderMetrics {
        id: Option<Value>,
        path: String,
        cancel: Arc<AtomicBool>,
    },
    Shutdown(JsonRpcResponse),
}

#[derive(Debug, Deserialize)]
struct HandshakeParams {
    #[serde(rename = "protocolVersion")]
    protocol_version: u32,
    #[serde(rename = "clientName")]
    #[allow(dead_code)]
    client_name: Option<String>,
    #[serde(rename = "authToken")]
    auth_token: Option<String>,
    #[serde(rename = "binaryHotFrames", default)]
    binary_hot_frames: bool,
}

#[derive(Debug, Deserialize)]
struct PathParams {
    path: String,
}

#[derive(Debug, Deserialize)]
struct ListDirectoryParams {
    path: String,
    #[serde(default)]
    mode: Option<String>,
    #[serde(rename = "finalEntries", default)]
    final_entries: Option<bool>,
    #[serde(rename = "sortBy", default)]
    sort_by: Option<String>,
    #[serde(rename = "sortAscending", default)]
    sort_ascending: Option<bool>,
    #[serde(default)]
    filter: Option<String>,
    #[serde(rename = "includeHidden", default)]
    include_hidden: Option<bool>,
}

impl ListDirectoryParams {
    fn into_options(self) -> (String, Option<ListDirectoryOptions>) {
        let has_options = self.mode.is_some()
            || self.final_entries.is_some()
            || self.sort_by.is_some()
            || self.sort_ascending.is_some()
            || self.filter.is_some()
            || self.include_hidden.is_some();
        if !has_options {
            return (self.path, None);
        }

        let mode = match self.mode.as_deref() {
            Some("light") => ListingMode::Light,
            _ => ListingMode::Full,
        };
        (
            self.path,
            Some(ListDirectoryOptions {
                mode,
                final_entries: self.final_entries.unwrap_or(true),
                sort_by: self.sort_by.unwrap_or_else(|| "name".to_string()),
                sort_ascending: self.sort_ascending.unwrap_or(true),
                filter: self.filter,
                include_hidden: self.include_hidden.unwrap_or(true),
            }),
        )
    }
}

#[derive(Debug, Deserialize)]
struct NameParams {
    path: String,
    name: String,
}

#[derive(Debug, Deserialize)]
struct PathsParams {
    paths: Vec<String>,
}

#[derive(Debug, Deserialize)]
struct RenameParams {
    path: String,
    #[serde(rename = "newName")]
    new_name: String,
}

#[derive(Debug, Deserialize)]
struct BatchRenameParams {
    entries: Vec<simplefile_core::file_ops::RenameRequest>,
}

#[derive(Debug, Deserialize)]
struct CopyMoveParams {
    source: String,
    destination: String,
}

#[derive(Debug, Deserialize)]
struct ResolvedCopyMoveParams {
    source: String,
    destination: String,
    #[serde(rename = "conflictAction")]
    conflict_action: String,
}

#[derive(Debug, Deserialize)]
pub(crate) struct ProgressCopyMoveParams {
    pub sources: Vec<String>,
    pub destination: String,
    #[serde(rename = "operationId")]
    pub operation_id: Option<String>,
    #[serde(rename = "conflictAction")]
    pub conflict_action: String,
}

#[derive(Debug, Deserialize)]
struct OperationIdParams {
    #[serde(rename = "operationId")]
    operation_id: String,
}

#[derive(Debug, Deserialize)]
struct SearchFilesParams {
    options: SearchOptions,
}

#[derive(Debug, Deserialize)]
struct SearchIdParams {
    #[serde(rename = "searchId")]
    search_id: String,
}

#[derive(Debug, Deserialize)]
struct PreviewParams {
    path: String,
    #[serde(rename = "maxSize")]
    max_size: Option<u64>,
}

#[derive(Debug, Deserialize)]
struct ThumbnailParams {
    path: String,
    size: Option<u32>,
}

#[derive(Debug, Deserialize)]
struct ThumbnailBatchParams {
    paths: Vec<String>,
    size: Option<u32>,
}

#[derive(Debug, Deserialize)]
struct ExternalUrlParams {
    url: String,
}

#[derive(Debug, Deserialize)]
struct SettingKeyParams {
    key: String,
}

#[derive(Debug, Deserialize)]
struct SettingValueParams {
    key: String,
    value: String,
}

#[derive(Debug, Deserialize)]
struct OpenWithParams {
    path: String,
    application: String,
}

#[derive(Debug, Deserialize)]
struct CompareParams {
    #[serde(rename = "pathA")]
    path_a: String,
    #[serde(rename = "pathB")]
    path_b: String,
}

#[derive(Debug, Deserialize)]
struct ExtractArchiveParams {
    #[serde(rename = "archivePath")]
    archive_path: String,
    destination: String,
}

#[derive(Debug, Deserialize)]
struct CreateArchiveParams {
    paths: Vec<String>,
    #[serde(rename = "archivePath")]
    archive_path: String,
    format: String,
}

#[derive(Debug, Deserialize)]
struct DuplicateCheckParams {
    directory: String,
    #[serde(rename = "minSize")]
    min_size: Option<u64>,
    #[serde(rename = "partialHashBytes")]
    partial_hash_bytes: Option<u64>,
    #[serde(rename = "operationId")]
    operation_id: Option<String>,
}

#[derive(Debug, Deserialize)]
struct DiskCleanupParams {
    directory: String,
    #[serde(rename = "sizeThreshold")]
    size_threshold: Option<u64>,
    #[serde(rename = "operationId")]
    operation_id: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ConfirmationTokenParams {
    #[serde(rename = "confirmationToken")]
    confirmation_token: String,
}

#[derive(Debug, Deserialize)]
struct TagCreateParams {
    name: String,
    color: String,
}

#[derive(Debug, Deserialize)]
struct TagUpdateParams {
    id: i64,
    name: String,
    color: String,
}

#[derive(Debug, Deserialize)]
struct TagIdParams {
    id: i64,
}

#[derive(Debug, Deserialize)]
struct TagForPathParams {
    path: String,
}

#[derive(Debug, Deserialize)]
struct SetTagsForPathParams {
    path: String,
    #[serde(rename = "tagIds")]
    tag_ids: Vec<i64>,
}

#[derive(Debug, Deserialize)]
struct GetFilesWithTagParams {
    #[serde(rename = "tagId")]
    tag_id: i64,
}

#[derive(Debug, Deserialize)]
struct SmartFolderParams {
    folder: simplefile_core::models::SmartFolder,
}

#[derive(Debug, Deserialize)]
struct SmartFolderIdParams {
    id: String,
}

pub(crate) fn dispatch(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    if request.jsonrpc != simplefile_ipc::JSONRPC_VERSION {
        return Dispatch::Reply(JsonRpcResponse::error(
            request.id.clone(),
            ERR_INVALID_REQUEST,
            "jsonrpc must be \"2.0\"",
        ));
    }

    if !state.handshake_done && request.method != HANDSHAKE_METHOD {
        return Dispatch::Reply(JsonRpcResponse::error(
            request.id.clone(),
            ERR_INVALID_REQUEST,
            "ipc.handshake must be the first method",
        ));
    }

    match request.method.as_str() {
        HANDSHAKE_METHOD => Dispatch::Reply(handshake(state, request)),
        HEALTH_METHOD => Dispatch::Reply(JsonRpcResponse::result(
            request.id.clone(),
            json!({
                "ok": true,
                "protocolVersion": PROTOCOL_VERSION,
                "appVersion": APP_VERSION,
            }),
        )),
        "get_app_version" => Dispatch::Reply(JsonRpcResponse::result(
            request.id.clone(),
            json!(APP_VERSION),
        )),
        "get_app_about_info" => Dispatch::Reply(JsonRpcResponse::result(
            request.id.clone(),
            serde_json::to_value(simplefile_core::updater::get_app_about_info())
                .unwrap_or(Value::Null),
        )),
        "check_for_update" => match simplefile_core::updater::check_for_update() {
            Ok(update) => Dispatch::Reply(JsonRpcResponse::result(
                request.id.clone(),
                serde_json::to_value(update).unwrap_or(Value::Null),
            )),
            Err(message) => Dispatch::Reply(JsonRpcResponse::application_error(
                request.id.clone(),
                message,
            )),
        },
        "install_update" => Dispatch::InstallUpdate {
            id: request.id.clone(),
        },
        "get_home_dir" => match dirs_home() {
            Ok(path) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(path))),
            Err(message) => Dispatch::Reply(JsonRpcResponse::application_error(
                request.id.clone(),
                message,
            )),
        },
        "list_drives" => match simplefile_core::drives::list_drives() {
            Ok(drives) => Dispatch::Reply(JsonRpcResponse::result(
                request.id.clone(),
                serde_json::to_value(drives).unwrap_or(Value::Null),
            )),
            Err(message) => Dispatch::Reply(JsonRpcResponse::application_error(
                request.id.clone(),
                message,
            )),
        },
        "get_db_setting" => match parse_params::<SettingKeyParams>(request) {
            Ok(p) => match simplefile_core::settings_store::get_db_setting(p.key) {
                Ok(value) => {
                    Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(value)))
                }
                Err(message) => Dispatch::Reply(JsonRpcResponse::application_error(
                    request.id.clone(),
                    message,
                )),
            },
            Err(response) => Dispatch::Reply(response),
        },
        "set_db_setting" => match parse_params::<SettingValueParams>(request) {
            Ok(p) => match simplefile_core::settings_store::set_db_setting(p.key, p.value) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(message) => Dispatch::Reply(JsonRpcResponse::application_error(
                    request.id.clone(),
                    message,
                )),
            },
            Err(response) => Dispatch::Reply(response),
        },
        "list_directory" => match parse_params::<ListDirectoryParams>(request) {
            Ok(params) => {
                let (path, options) = params.into_options();
                Dispatch::ListDirectory {
                    id: request.id.clone(),
                    path,
                    options,
                }
            }
            Err(response) => Dispatch::Reply(response),
        },
        "select_directory" => Dispatch::Reply(JsonRpcResponse::error(
            request.id.clone(),
            ERR_HOST_OWNED,
            format!("{PREFIX_HOST_OWNED} select_directory"),
        )),
        "show_main_window" => {
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        SHUTDOWN_METHOD => {
            Dispatch::Shutdown(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        // File operations
        "create_directory" => match parse_params::<NameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::create_directory(&p.path, &p.name) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "create_file" => match parse_params::<NameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::create_file(&p.path, &p.name) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "delete_entry" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::file_ops::delete_entry(&p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "move_to_trash" => match parse_params::<PathsParams>(request) {
            Ok(p) => match simplefile_core::file_ops::move_to_trash(&p.paths) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "rename_entry" => match parse_params::<RenameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::rename_entry(&p.path, &p.new_name) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "batch_rename" => match parse_params::<BatchRenameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::batch_rename(p.entries) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "copy_entry" => match parse_params::<CopyMoveParams>(request) {
            Ok(p) => match simplefile_core::file_ops::copy_entry(&p.source, &p.destination) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "move_entry" => match parse_params::<CopyMoveParams>(request) {
            Ok(p) => match simplefile_core::file_ops::move_entry(&p.source, &p.destination) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "copy_entry_resolved" => match parse_params::<ResolvedCopyMoveParams>(request) {
            Ok(p) => match simplefile_core::file_ops::copy_entry_resolved(
                &p.source,
                &p.destination,
                &p.conflict_action,
            ) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "move_entry_resolved" => match parse_params::<ResolvedCopyMoveParams>(request) {
            Ok(p) => match simplefile_core::file_ops::move_entry_resolved(
                &p.source,
                &p.destination,
                &p.conflict_action,
            ) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "get_entry_info" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::file_ops::get_entry_info_simple(&p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "list_subdirectories" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::file_ops::list_subdirectories(&p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "open_file" => match parse_params::<PathParams>(request) {
            Ok(p) => match crate::shell::open_file(&p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "reveal_in_folder" => match parse_params::<PathParams>(request) {
            Ok(p) => match crate::shell::reveal_in_folder(&p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "open_external_url" => match parse_params::<ExternalUrlParams>(request) {
            Ok(p) => match crate::shell::open_external_url(&p.url) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "open_terminal" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::terminal::open_terminal(p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "open_powershell_admin" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::terminal::open_powershell_admin(p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "get_git_status" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::git::get_git_status(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "get_git_file_statuses" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::git::get_git_file_statuses(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "git_pull" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::git::git_pull(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "git_push" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::git::git_push(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "list_archive" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::archive::list_archive(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "extract_archive" => match parse_params::<ExtractArchiveParams>(request) {
            Ok(p) => match simplefile_core::archive::extract_archive(p.archive_path, p.destination)
            {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "create_archive" => match parse_params::<CreateArchiveParams>(request) {
            Ok(p) => {
                match simplefile_core::archive::create_archive(p.paths, p.archive_path, p.format) {
                    Ok(()) => {
                        Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
                    }
                    Err(m) => {
                        Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                    }
                }
            }
            Err(r) => Dispatch::Reply(r),
        },
        "check_rar_installed" => Dispatch::Reply(JsonRpcResponse::result(
            request.id.clone(),
            json!(simplefile_core::rar::check_rar_installed()),
        )),
        "prepare_rar_install" => match simplefile_core::rar::prepare_rar_install() {
            Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                request.id.clone(),
                serde_json::to_value(r).unwrap_or(Value::Null),
            )),
            Err(m) => Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m)),
        },
        "discard_rar_install" => match parse_params::<ConfirmationTokenParams>(request) {
            Ok(p) => match simplefile_core::rar::discard_rar_install(p.confirmation_token) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "install_rar" => match parse_params::<ConfirmationTokenParams>(request) {
            Ok(p) => match simplefile_core::rar::install_rar(p.confirmation_token) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "read_file_preview" => match parse_params::<PreviewParams>(request) {
            Ok(p) => match simplefile_core::preview::read_file_preview(p.path, p.max_size) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "generate_thumbnail" => match parse_params::<ThumbnailParams>(request) {
            Ok(p) => Dispatch::GenerateThumbnail {
                id: request.id.clone(),
                path: p.path,
                size: p.size,
            },
            Err(r) => Dispatch::Reply(r),
        },
        "generate_thumbnails" => match parse_params::<ThumbnailBatchParams>(request) {
            Ok(p) => Dispatch::GenerateThumbnails {
                id: request.id.clone(),
                paths: p.paths,
                size: p.size,
            },
            Err(r) => Dispatch::Reply(r),
        },
        "compute_checksum" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::checksum::compute_checksum(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), r)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "get_image_metadata" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::metadata::get_image_metadata(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "get_file_metadata" => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::metadata::get_file_metadata(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "open_file_with" => match parse_params::<OpenWithParams>(request) {
            Ok(p) => match simplefile_core::open_with::open_file_with(p.path, p.application) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "compare_files" => match parse_params::<CompareParams>(request) {
            Ok(p) => match simplefile_core::compare::compare_files(p.path_a, p.path_b) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        // Deprecated: use get_folder_metrics for a combined single-traversal query.
        "calculate_folder_size" => match parse_params::<PathParams>(request) {
            Ok(p) => {
                let cancel = Arc::new(AtomicBool::new(false));
                state.folder_size_cancel = cancel.clone();
                Dispatch::CalculateFolderSize {
                    id: request.id.clone(),
                    path: p.path,
                    cancel,
                }
            }
            Err(r) => Dispatch::Reply(r),
        },
        // Deprecated: use get_folder_metrics for a combined single-traversal query.
        "count_folder_items" => match parse_params::<PathParams>(request) {
            Ok(p) => {
                let cancel = Arc::new(AtomicBool::new(false));
                state.folder_item_count_cancel = cancel.clone();
                state.count_items_cancel = cancel.clone();
                Dispatch::CountFolderItems {
                    id: request.id.clone(),
                    path: p.path,
                    cancel,
                }
            }
            Err(r) => Dispatch::Reply(r),
        },
        "get_folder_metrics" => match parse_params::<PathParams>(request) {
            Ok(p) => {
                let cancel = Arc::new(AtomicBool::new(false));
                // Share the cancel flag so any of the old cancel methods also work.
                state.folder_size_cancel = cancel.clone();
                state.folder_item_count_cancel = cancel.clone();
                state.count_items_cancel = cancel.clone();
                Dispatch::GetFolderMetrics {
                    id: request.id.clone(),
                    path: p.path,
                    cancel,
                }
            }
            Err(r) => Dispatch::Reply(r),
        },
        "cancel_folder_size" => {
            state.folder_size_cancel.store(true, Ordering::Relaxed);
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        "cancel_folder_item_count" => {
            state
                .folder_item_count_cancel
                .store(true, Ordering::Relaxed);
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        "cancel_count_items" => {
            state.count_items_cancel.store(true, Ordering::Relaxed);
            state
                .folder_item_count_cancel
                .store(true, Ordering::Relaxed);
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        "cancel_folder_metrics" => {
            state.folder_size_cancel.store(true, Ordering::Relaxed);
            state
                .folder_item_count_cancel
                .store(true, Ordering::Relaxed);
            state.count_items_cancel.store(true, Ordering::Relaxed);
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        "copy_with_progress" => match parse_params::<ProgressCopyMoveParams>(request) {
            Ok(params) => Dispatch::CopyWithProgress {
                id: request.id.clone(),
                params,
            },
            Err(r) => Dispatch::Reply(r),
        },
        "move_with_progress" => match parse_params::<ProgressCopyMoveParams>(request) {
            Ok(params) => Dispatch::MoveWithProgress {
                id: request.id.clone(),
                params,
            },
            Err(r) => Dispatch::Reply(r),
        },
        "cancel_operation" => match parse_params::<OperationIdParams>(request) {
            Ok(p) => Dispatch::CancelOperation {
                id: request.id.clone(),
                operation_id: p.operation_id,
            },
            Err(r) => Dispatch::Reply(r),
        },
        "search_files" => match parse_params::<SearchFilesParams>(request) {
            Ok(p) => Dispatch::SearchFiles {
                id: request.id.clone(),
                options: p.options,
            },
            Err(r) => Dispatch::Reply(r),
        },
        "cancel_search" => match parse_params::<SearchIdParams>(request) {
            Ok(p) => Dispatch::CancelSearch {
                id: request.id.clone(),
                search_id: p.search_id,
            },
            Err(r) => Dispatch::Reply(r),
        },
        "watch_directory" => match parse_path_params(request) {
            Ok(path) => Dispatch::WatchDirectory {
                id: request.id.clone(),
                path,
            },
            Err(response) => Dispatch::Reply(response),
        },
        "unwatch_directory" => Dispatch::UnwatchDirectory {
            id: request.id.clone(),
        },
        "duplicate_check" => match parse_params::<DuplicateCheckParams>(request) {
            Ok(params) => Dispatch::DuplicateCheck {
                id: request.id.clone(),
                directory: params.directory,
                min_size: params.min_size,
                partial_hash_bytes: params.partial_hash_bytes,
                operation_id: params.operation_id,
            },
            Err(response) => Dispatch::Reply(response),
        },
        "cancel_duplicate_check" => Dispatch::CancelDuplicateCheck {
            id: request.id.clone(),
        },
        "disk_cleanup" => match parse_params::<DiskCleanupParams>(request) {
            Ok(params) => Dispatch::DiskCleanup {
                id: request.id.clone(),
                directory: params.directory,
                size_threshold: params.size_threshold,
                operation_id: params.operation_id,
            },
            Err(response) => Dispatch::Reply(response),
        },
        "cancel_disk_cleanup" => Dispatch::CancelDiskCleanup {
            id: request.id.clone(),
        },
        "get_all_tags" => match simplefile_core::tags::get_all_tags() {
            Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                request.id.clone(),
                serde_json::to_value(r).unwrap_or(Value::Null),
            )),
            Err(m) => Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m)),
        },
        "create_tag" => match parse_params::<TagCreateParams>(request) {
            Ok(p) => match simplefile_core::tags::create_tag(p.name, p.color) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "update_tag" => match parse_params::<TagUpdateParams>(request) {
            Ok(p) => match simplefile_core::tags::update_tag(p.id, p.name, p.color) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "delete_tag" => match parse_params::<TagIdParams>(request) {
            Ok(p) => match simplefile_core::tags::delete_tag(p.id) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "get_tags_for_path" => match parse_params::<TagForPathParams>(request) {
            Ok(p) => match simplefile_core::tags::get_tags_for_path(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "set_tags_for_path" => match parse_params::<SetTagsForPathParams>(request) {
            Ok(p) => match simplefile_core::tags::set_tags_for_path(p.path, p.tag_ids) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "get_all_file_tags" => match simplefile_core::tags::get_all_file_tags() {
            Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                request.id.clone(),
                serde_json::to_value(r).unwrap_or(Value::Null),
            )),
            Err(m) => Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m)),
        },
        "get_files_with_tag" => match parse_params::<GetFilesWithTagParams>(request) {
            Ok(p) => match simplefile_core::tags::get_files_with_tag(p.tag_id) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "load_smart_folders" => match simplefile_core::smart_folders::load_smart_folders() {
            Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                request.id.clone(),
                serde_json::to_value(r).unwrap_or(Value::Null),
            )),
            Err(m) => Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m)),
        },
        "save_smart_folder" => match parse_params::<SmartFolderParams>(request) {
            Ok(p) => match simplefile_core::smart_folders::save_smart_folder(p.folder) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        "delete_smart_folder" => match parse_params::<SmartFolderIdParams>(request) {
            Ok(p) => match simplefile_core::smart_folders::delete_smart_folder(p.id) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(
                    request.id.clone(),
                    serde_json::to_value(r).unwrap_or(Value::Null),
                )),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        _ => Dispatch::Reply(JsonRpcResponse::error(
            request.id.clone(),
            ERR_METHOD_NOT_FOUND,
            format!("method not found: {}", request.method),
        )),
    }
}

fn auth_token_matches(expected: &str, provided: Option<&str>) -> bool {
    let Some(provided) = provided else {
        return false;
    };
    constant_time_eq(expected.as_bytes(), provided.as_bytes())
}

fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    let mut diff = left.len() ^ right.len();
    let n = left.len().max(right.len());
    for index in 0..n {
        let a = left.get(index).copied().unwrap_or(0);
        let b = right.get(index).copied().unwrap_or(0);
        diff |= usize::from(a ^ b);
    }
    diff == 0
}

fn handshake(state: &mut SessionState, request: &JsonRpcRequest) -> JsonRpcResponse {
    let params = match parse_params::<HandshakeParams>(request) {
        Ok(params) => params,
        Err(response) => return response,
    };

    if params.protocol_version != PROTOCOL_VERSION {
        return JsonRpcResponse::application_error(
            request.id.clone(),
            format!(
                "unsupported protocolVersion {}; this service speaks {PROTOCOL_VERSION}",
                params.protocol_version
            ),
        );
    }

    let Some(expected) = state.expected_token.as_deref() else {
        return JsonRpcResponse::error(
            request.id.clone(),
            ERR_INVALID_REQUEST,
            "authToken is required",
        );
    };
    if !auth_token_matches(expected, params.auth_token.as_deref()) {
        return JsonRpcResponse::error(
            request.id.clone(),
            ERR_INVALID_REQUEST,
            "authToken does not match",
        );
    }

    state.handshake_done = true;
    state
        .binary_hot_frames
        .store(params.binary_hot_frames, Ordering::Relaxed);
    JsonRpcResponse::result(
        request.id.clone(),
        json!({
            "protocolVersion": PROTOCOL_VERSION,
            "appVersion": APP_VERSION,
            "identifier": APP_IDENTIFIER,
            "methodCount": DOMAIN_METHOD_COUNT,
            "binaryHotFrames": params.binary_hot_frames,
            "binaryFrameVersion": simplefile_ipc::BINARY_FRAME_VERSION,
        }),
    )
}

fn parse_path_params(request: &JsonRpcRequest) -> Result<String, JsonRpcResponse> {
    parse_params::<PathParams>(request).map(|params| params.path)
}

fn parse_params<T: for<'de> Deserialize<'de>>(
    request: &JsonRpcRequest,
) -> Result<T, JsonRpcResponse> {
    let params = request
        .params
        .clone()
        .unwrap_or(Value::Object(Default::default()));
    serde_json::from_value(params).map_err(|error| {
        JsonRpcResponse::error(
            request.id.clone(),
            ERR_INVALID_PARAMS,
            format!("invalid params: {error}"),
        )
    })
}

#[cfg(test)]
mod tests {
    use super::*;
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
        let path =
            std::env::temp_dir().join(format!("simplefile-service-dispatch-{name}-{nanos}.txt"));
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

    fn assert_domain_method_is_wired(
        state: &mut SessionState,
        method: &str,
        id: u64,
        params: Value,
    ) {
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
}
