use super::params::{
    parse_params, BatchRenameParams, CompareParams, ConfirmationTokenParams, CopyMoveParams,
    CreateArchiveParams, ExternalUrlParams, ExtractArchiveParams, GetFilesWithTagParams,
    HandshakeParams, NameParams, OpenWithParams, PathParams, PathsParams, PreviewParams,
    RenameParams, ResolvedCopyMoveParams, SetTagsForPathParams, SettingKeyParams,
    SettingValueParams, SmartFolderIdParams, SmartFolderParams, TagCreateParams, TagForPathParams,
    TagIdParams, TagUpdateParams,
};
use super::{async_ops, Dispatch, SessionState, APP_VERSION};
use serde::Serialize;
use serde_json::{json, Value};
use simplefile_core::utils::dirs_home;
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use simplefile_ipc::*;
use std::sync::atomic::Ordering;

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

    if !is_control_method(&request.method) && !is_domain_method(&request.method) {
        return Dispatch::Reply(method_not_found(request));
    }

    match request.method.as_str() {
        HANDSHAKE_METHOD => Dispatch::Reply(handshake(state, request)),
        HEALTH_METHOD => reply_ok(
            request,
            json!({
                "ok": true,
                "protocolVersion": PROTOCOL_VERSION,
                "appVersion": APP_VERSION,
            }),
        ),
        METHOD_GET_APP_VERSION => reply_ok(request, APP_VERSION),
        METHOD_GET_APP_ABOUT_INFO => {
            reply_ok(request, simplefile_core::updater::get_app_about_info())
        }
        METHOD_CHECK_FOR_UPDATE => {
            reply_result(request, simplefile_core::updater::check_for_update())
        }
        METHOD_INSTALL_UPDATE => async_ops::install_update(request),
        METHOD_GET_HOME_DIR => reply_result(request, dirs_home()),
        METHOD_LIST_DRIVES => reply_result(request, simplefile_core::drives::list_drives()),
        METHOD_GET_DB_SETTING => match parse_params::<SettingKeyParams>(request) {
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
        METHOD_SET_DB_SETTING => match parse_params::<SettingValueParams>(request) {
            Ok(p) => match simplefile_core::settings_store::set_db_setting(p.key, p.value) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(message) => Dispatch::Reply(JsonRpcResponse::application_error(
                    request.id.clone(),
                    message,
                )),
            },
            Err(response) => Dispatch::Reply(response),
        },
        METHOD_LIST_DIRECTORY => async_ops::list_directory(request),
        METHOD_SELECT_DIRECTORY => Dispatch::Reply(JsonRpcResponse::error(
            request.id.clone(),
            ERR_HOST_OWNED,
            format!("{PREFIX_HOST_OWNED} select_directory"),
        )),
        METHOD_SHOW_MAIN_WINDOW => {
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        SHUTDOWN_METHOD => {
            Dispatch::Shutdown(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        // File operations
        METHOD_CREATE_DIRECTORY => match parse_params::<NameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::create_directory(&p.path, &p.name) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_CREATE_FILE => match parse_params::<NameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::create_file(&p.path, &p.name) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_DELETE_ENTRY => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::file_ops::delete_entry(&p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_MOVE_TO_TRASH => match parse_params::<PathsParams>(request) {
            Ok(p) => match simplefile_core::file_ops::move_to_trash(&p.paths) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_RESTORE_RECYCLE_BIN => match parse_params::<PathsParams>(request) {
            Ok(p) => match simplefile_core::recycle_bin::restore_recycle_bin(&p.paths) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_EMPTY_RECYCLE_BIN => match simplefile_core::recycle_bin::empty_recycle_bin() {
            Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
            Err(m) => Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m)),
        },
        METHOD_RENAME_ENTRY => match parse_params::<RenameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::rename_entry(&p.path, &p.new_name) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_BATCH_RENAME => match parse_params::<BatchRenameParams>(request) {
            Ok(p) => match simplefile_core::file_ops::batch_rename(p.entries) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_COPY_ENTRY => match parse_params::<CopyMoveParams>(request) {
            Ok(p) => match simplefile_core::file_ops::copy_entry(&p.source, &p.destination) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_MOVE_ENTRY => match parse_params::<CopyMoveParams>(request) {
            Ok(p) => match simplefile_core::file_ops::move_entry(&p.source, &p.destination) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_COPY_ENTRY_RESOLVED => match parse_params::<ResolvedCopyMoveParams>(request) {
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
        METHOD_MOVE_ENTRY_RESOLVED => match parse_params::<ResolvedCopyMoveParams>(request) {
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
        METHOD_GET_ENTRY_INFO => match parse_params::<PathParams>(request) {
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
        METHOD_LIST_SUBDIRECTORIES => match parse_params::<PathParams>(request) {
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
        METHOD_OPEN_FILE => match parse_params::<PathParams>(request) {
            Ok(p) => match crate::shell::open_file(&p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_REVEAL_IN_FOLDER => match parse_params::<PathParams>(request) {
            Ok(p) => match crate::shell::reveal_in_folder(&p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_OPEN_EXTERNAL_URL => match parse_params::<ExternalUrlParams>(request) {
            Ok(p) => match crate::shell::open_external_url(&p.url) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_OPEN_TERMINAL => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::terminal::open_terminal(p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_OPEN_POWERSHELL_ADMIN => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::terminal::open_powershell_admin(p.path) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_GET_GIT_STATUS => match parse_params::<PathParams>(request) {
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
        METHOD_GET_GIT_FILE_STATUSES => match parse_params::<PathParams>(request) {
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
        METHOD_GIT_PULL => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::git::git_pull(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_GIT_PUSH => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::git::git_push(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_LIST_ARCHIVE => match parse_params::<PathParams>(request) {
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
        METHOD_EXTRACT_ARCHIVE => match parse_params::<ExtractArchiveParams>(request) {
            Ok(p) => match simplefile_core::archive::extract_archive(p.archive_path, p.destination)
            {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_CREATE_ARCHIVE => match parse_params::<CreateArchiveParams>(request) {
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
        METHOD_CHECK_RAR_INSTALLED => {
            reply_ok(request, simplefile_core::rar::check_rar_installed())
        }
        METHOD_PREPARE_RAR_INSTALL => {
            reply_result(request, simplefile_core::rar::prepare_rar_install())
        }
        METHOD_DISCARD_RAR_INSTALL => match parse_params::<ConfirmationTokenParams>(request) {
            Ok(p) => match simplefile_core::rar::discard_rar_install(p.confirmation_token) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_INSTALL_RAR => match parse_params::<ConfirmationTokenParams>(request) {
            Ok(p) => match simplefile_core::rar::install_rar(p.confirmation_token) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), json!(r))),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_READ_FILE_PREVIEW => match parse_params::<PreviewParams>(request) {
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
        METHOD_GENERATE_THUMBNAIL => async_ops::generate_thumbnail(request),
        METHOD_GENERATE_THUMBNAILS => async_ops::generate_thumbnails(request),
        METHOD_COMPUTE_CHECKSUM => match parse_params::<PathParams>(request) {
            Ok(p) => match simplefile_core::checksum::compute_checksum(p.path) {
                Ok(r) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), r)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_GET_IMAGE_METADATA => match parse_params::<PathParams>(request) {
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
        METHOD_GET_FILE_METADATA => match parse_params::<PathParams>(request) {
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
        METHOD_OPEN_FILE_WITH => match parse_params::<OpenWithParams>(request) {
            Ok(p) => match simplefile_core::open_with::open_file_with(p.path, p.application) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_COMPARE_FILES => match parse_params::<CompareParams>(request) {
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
        METHOD_CALCULATE_FOLDER_SIZE => async_ops::calculate_folder_size(state, request),
        // Deprecated: use get_folder_metrics for a combined single-traversal query.
        METHOD_COUNT_FOLDER_ITEMS => async_ops::count_folder_items(state, request),
        METHOD_GET_FOLDER_METRICS => async_ops::get_folder_metrics(state, request),
        METHOD_CANCEL_FOLDER_SIZE => async_ops::cancel_folder_size(state, request),
        METHOD_CANCEL_FOLDER_ITEM_COUNT => async_ops::cancel_folder_item_count(state, request),
        METHOD_CANCEL_COUNT_ITEMS => async_ops::cancel_count_items(state, request),
        METHOD_CANCEL_FOLDER_METRICS => async_ops::cancel_folder_metrics(state, request),
        METHOD_COPY_WITH_PROGRESS => async_ops::copy_with_progress(request),
        METHOD_MOVE_WITH_PROGRESS => async_ops::move_with_progress(request),
        METHOD_CANCEL_OPERATION => async_ops::cancel_operation(request),
        METHOD_SEARCH_FILES => async_ops::search_files(request),
        METHOD_CANCEL_SEARCH => async_ops::cancel_search(request),
        METHOD_WATCH_DIRECTORY => async_ops::watch_directory(request),
        METHOD_UNWATCH_DIRECTORY => async_ops::unwatch_directory(request),
        METHOD_DUPLICATE_CHECK => async_ops::duplicate_check(request),
        METHOD_CANCEL_DUPLICATE_CHECK => async_ops::cancel_duplicate_check(request),
        METHOD_DISK_CLEANUP => async_ops::disk_cleanup(request),
        METHOD_CANCEL_DISK_CLEANUP => async_ops::cancel_disk_cleanup(request),
        METHOD_GET_ALL_TAGS => reply_result(request, simplefile_core::tags::get_all_tags()),
        METHOD_CREATE_TAG => match parse_params::<TagCreateParams>(request) {
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
        METHOD_UPDATE_TAG => match parse_params::<TagUpdateParams>(request) {
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
        METHOD_DELETE_TAG => match parse_params::<TagIdParams>(request) {
            Ok(p) => match simplefile_core::tags::delete_tag(p.id) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_GET_TAGS_FOR_PATH => match parse_params::<TagForPathParams>(request) {
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
        METHOD_SET_TAGS_FOR_PATH => match parse_params::<SetTagsForPathParams>(request) {
            Ok(p) => match simplefile_core::tags::set_tags_for_path(p.path, p.tag_ids) {
                Ok(()) => Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null)),
                Err(m) => {
                    Dispatch::Reply(JsonRpcResponse::application_error(request.id.clone(), m))
                }
            },
            Err(r) => Dispatch::Reply(r),
        },
        METHOD_GET_ALL_FILE_TAGS => {
            reply_result(request, simplefile_core::tags::get_all_file_tags())
        }
        METHOD_GET_FILES_WITH_TAG => match parse_params::<GetFilesWithTagParams>(request) {
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
        METHOD_LOAD_SMART_FOLDERS => reply_result(
            request,
            simplefile_core::smart_folders::load_smart_folders(),
        ),
        METHOD_SAVE_SMART_FOLDER => match parse_params::<SmartFolderParams>(request) {
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
        METHOD_DELETE_SMART_FOLDER => match parse_params::<SmartFolderIdParams>(request) {
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
        _ => Dispatch::Reply(method_not_found(request)),
    }
}

fn method_not_found(request: &JsonRpcRequest) -> JsonRpcResponse {
    JsonRpcResponse::error(
        request.id.clone(),
        ERR_METHOD_NOT_FOUND,
        format!("method not found: {}", request.method),
    )
}

fn reply_ok<T: Serialize>(request: &JsonRpcRequest, result: T) -> Dispatch {
    let value = serde_json::to_value(result).unwrap_or(Value::Null);
    Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), value))
}

fn reply_result<T, E>(request: &JsonRpcRequest, result: Result<T, E>) -> Dispatch
where
    T: Serialize,
    E: ToString,
{
    match result {
        Ok(value) => reply_ok(request, value),
        Err(message) => Dispatch::Reply(JsonRpcResponse::application_error(
            request.id.clone(),
            message.to_string(),
        )),
    }
}

pub(super) fn auth_token_matches(expected: &str, provided: Option<&str>) -> bool {
    let Some(provided) = provided else {
        return false;
    };
    constant_time_eq(expected.as_bytes(), provided.as_bytes())
}

pub(super) fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
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
