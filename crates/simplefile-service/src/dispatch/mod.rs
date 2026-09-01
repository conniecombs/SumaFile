mod async_ops;
mod handlers;
mod params;

use serde_json::Value;
use simplefile_core::dir_list::ListDirectoryOptions;
use simplefile_core::models::SearchOptions;
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

const APP_VERSION: &str = simplefile_core::APP_DISPLAY_VERSION;

pub(crate) use params::ProgressCopyMoveParams;

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

pub(crate) fn dispatch(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    handlers::dispatch(state, request)
}

#[cfg(test)]
mod tests;
