mod async_ops;
mod handlers;
mod params;

use crate::session_cache::SessionCache;
use serde_json::Value;
use simplefile_core::dir_list::ListDirectoryOptions;
use simplefile_core::models::SearchOptions;
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

const APP_VERSION: &str = simplefile_core::APP_DISPLAY_VERSION;

pub(crate) use params::ProgressCopyMoveParams;

pub struct SessionState {
    pub handshake_done: bool,
    /// Failed authentication attempts; the session closes after a few.
    pub handshake_failures: u8,
    pub binary_hot_frames: Arc<AtomicBool>,
    pub expected_token: Option<String>,
    pub shutdown: bool,
    pub duplicate_check_cancel: Arc<AtomicBool>,
    pub disk_cleanup_cancel: Arc<AtomicBool>,
    pub folder_size_cancel: Arc<AtomicBool>,
    pub folder_item_count_cancel: Arc<AtomicBool>,
    pub count_items_cancel: Arc<AtomicBool>,
    pub cache: SessionCache,
}

impl Default for SessionState {
    fn default() -> Self {
        Self {
            handshake_done: false,
            handshake_failures: 0,
            binary_hot_frames: Arc::new(AtomicBool::new(false)),
            expected_token: None,
            shutdown: false,
            duplicate_check_cancel: Arc::new(AtomicBool::new(false)),
            disk_cleanup_cancel: Arc::new(AtomicBool::new(false)),
            folder_size_cancel: Arc::new(AtomicBool::new(false)),
            folder_item_count_cancel: Arc::new(AtomicBool::new(false)),
            count_items_cancel: Arc::new(AtomicBool::new(false)),
            cache: SessionCache::new(),
        }
    }
}

impl std::fmt::Debug for SessionState {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("SessionState")
            .field("handshake_done", &self.handshake_done)
            .field(
                "expected_token",
                &self.expected_token.as_ref().map(|_| "***"),
            )
            .field("shutdown", &self.shutdown)
            .finish_non_exhaustive()
    }
}

/// Which scheduler lane a blocking handler runs on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum BlockingLane {
    /// Short blocking calls: metadata and previews.
    General,
    /// Whole-file work that can run for minutes: copies and archives. Uses
    /// the transfer lane so it never starves listings.
    Transfer,
}

/// A handler body that must not run on the session loop. The session spawns
/// it on the `BlockingScheduler` and replies when it finishes, so frame
/// reading (and cancellation) continues while it works.
pub(crate) struct BlockingJob {
    lane: BlockingLane,
    run: Box<dyn FnOnce() -> Result<Value, String> + Send + 'static>,
}

impl BlockingJob {
    pub(crate) fn new(
        lane: BlockingLane,
        run: impl FnOnce() -> Result<Value, String> + Send + 'static,
    ) -> Self {
        Self {
            lane,
            run: Box::new(run),
        }
    }

    pub(crate) fn lane(&self) -> BlockingLane {
        self.lane
    }

    pub(crate) fn run(self) -> Result<Value, String> {
        (self.run)()
    }
}

impl std::fmt::Debug for BlockingJob {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("BlockingJob")
            .field("lane", &self.lane)
            .finish_non_exhaustive()
    }
}

/// A tracked archive extract or create: it reports `operation-progress` and
/// `cancel_operation` stops it.
#[derive(Debug)]
pub(crate) enum ArchiveOperation {
    Extract {
        archive_path: String,
        destination: String,
    },
    Create {
        paths: Vec<String>,
        archive_path: String,
        format: String,
    },
}

#[derive(Debug)]
pub(crate) enum Dispatch {
    Reply(JsonRpcResponse),
    /// Reply with the job's result once it has run off the session loop.
    Blocking {
        id: Option<Value>,
        job: BlockingJob,
    },
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
    /// `extract_archive` or `create_archive` with an `operationId`.
    ArchiveOperation {
        id: Option<Value>,
        operation_id: String,
        operation: ArchiveOperation,
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
        max_depth: Option<usize>,
        exclude_patterns: Vec<String>,
        network_mode: Option<bool>,
        operation_id: Option<String>,
    },
    CancelDuplicateCheck {
        id: Option<Value>,
        operation_id: Option<String>,
    },
    DiskCleanup {
        id: Option<Value>,
        directory: String,
        size_threshold: Option<u64>,
        operation_id: Option<String>,
    },
    CancelDiskCleanup {
        id: Option<Value>,
        operation_id: Option<String>,
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
