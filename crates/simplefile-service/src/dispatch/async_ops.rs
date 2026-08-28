use super::params::{
    parse_params, parse_path_params, DiskCleanupParams, DuplicateCheckParams, ListDirectoryParams,
    OperationIdParams, PathParams, ProgressCopyMoveParams, SearchFilesParams, SearchIdParams,
    ThumbnailBatchParams, ThumbnailParams,
};
use super::{Dispatch, SessionState};
use serde_json::Value;
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

pub(super) fn install_update(request: &JsonRpcRequest) -> Dispatch {
    Dispatch::InstallUpdate {
        id: request.id.clone(),
    }
}

pub(super) fn list_directory(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ListDirectoryParams>(request) {
        Ok(params) => {
            let (path, options) = params.into_options();
            Dispatch::ListDirectory {
                id: request.id.clone(),
                path,
                options,
            }
        }
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn generate_thumbnail(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ThumbnailParams>(request) {
        Ok(p) => Dispatch::GenerateThumbnail {
            id: request.id.clone(),
            path: p.path,
            size: p.size,
        },
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn generate_thumbnails(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ThumbnailBatchParams>(request) {
        Ok(p) => Dispatch::GenerateThumbnails {
            id: request.id.clone(),
            paths: p.paths,
            size: p.size,
        },
        Err(r) => Dispatch::Reply(r),
    }
}

// Deprecated: use get_folder_metrics for a combined single-traversal query.
pub(super) fn calculate_folder_size(
    state: &mut SessionState,
    request: &JsonRpcRequest,
) -> Dispatch {
    match parse_params::<PathParams>(request) {
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
    }
}

// Deprecated: use get_folder_metrics for a combined single-traversal query.
pub(super) fn count_folder_items(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<PathParams>(request) {
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
    }
}

pub(super) fn get_folder_metrics(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<PathParams>(request) {
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
    }
}

pub(super) fn cancel_folder_size(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    state.folder_size_cancel.store(true, Ordering::Relaxed);
    Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
}

pub(super) fn cancel_folder_item_count(
    state: &mut SessionState,
    request: &JsonRpcRequest,
) -> Dispatch {
    state
        .folder_item_count_cancel
        .store(true, Ordering::Relaxed);
    Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
}

pub(super) fn cancel_count_items(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    state.count_items_cancel.store(true, Ordering::Relaxed);
    state
        .folder_item_count_cancel
        .store(true, Ordering::Relaxed);
    Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
}

pub(super) fn cancel_folder_metrics(
    state: &mut SessionState,
    request: &JsonRpcRequest,
) -> Dispatch {
    state.folder_size_cancel.store(true, Ordering::Relaxed);
    state
        .folder_item_count_cancel
        .store(true, Ordering::Relaxed);
    state.count_items_cancel.store(true, Ordering::Relaxed);
    Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
}

pub(super) fn copy_with_progress(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ProgressCopyMoveParams>(request) {
        Ok(params) => Dispatch::CopyWithProgress {
            id: request.id.clone(),
            params,
        },
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn move_with_progress(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ProgressCopyMoveParams>(request) {
        Ok(params) => Dispatch::MoveWithProgress {
            id: request.id.clone(),
            params,
        },
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn cancel_operation(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<OperationIdParams>(request) {
        Ok(p) => Dispatch::CancelOperation {
            id: request.id.clone(),
            operation_id: p.operation_id,
        },
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn search_files(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<SearchFilesParams>(request) {
        Ok(p) => Dispatch::SearchFiles {
            id: request.id.clone(),
            options: p.options,
        },
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn cancel_search(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<SearchIdParams>(request) {
        Ok(p) => Dispatch::CancelSearch {
            id: request.id.clone(),
            search_id: p.search_id,
        },
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn watch_directory(request: &JsonRpcRequest) -> Dispatch {
    match parse_path_params(request) {
        Ok(path) => Dispatch::WatchDirectory {
            id: request.id.clone(),
            path,
        },
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn unwatch_directory(request: &JsonRpcRequest) -> Dispatch {
    Dispatch::UnwatchDirectory {
        id: request.id.clone(),
    }
}

pub(super) fn duplicate_check(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<DuplicateCheckParams>(request) {
        Ok(params) => Dispatch::DuplicateCheck {
            id: request.id.clone(),
            directory: params.directory,
            min_size: params.min_size,
            partial_hash_bytes: params.partial_hash_bytes,
            operation_id: params.operation_id,
        },
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn cancel_duplicate_check(request: &JsonRpcRequest) -> Dispatch {
    Dispatch::CancelDuplicateCheck {
        id: request.id.clone(),
    }
}

pub(super) fn disk_cleanup(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<DiskCleanupParams>(request) {
        Ok(params) => Dispatch::DiskCleanup {
            id: request.id.clone(),
            directory: params.directory,
            size_threshold: params.size_threshold,
            operation_id: params.operation_id,
        },
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn cancel_disk_cleanup(request: &JsonRpcRequest) -> Dispatch {
    Dispatch::CancelDiskCleanup {
        id: request.id.clone(),
    }
}
