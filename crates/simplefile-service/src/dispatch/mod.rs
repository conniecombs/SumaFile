mod async_ops;
mod handlers;
mod params;

use crate::remote::provider::DefaultRemoteProviderFactory;
#[cfg(test)]
use crate::remote::secrets::MemoryRemoteSecretStore;
use crate::remote::secrets::RemoteSecretStore;
#[cfg(not(test))]
use crate::remote::secrets::WindowsRemoteSecretStore;
use crate::remote::session::RemoteSessionRegistry;
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
    pub binary_hot_frames: Arc<AtomicBool>,
    pub expected_token: Option<String>,
    pub shutdown: bool,
    pub duplicate_check_cancel: Arc<AtomicBool>,
    pub disk_cleanup_cancel: Arc<AtomicBool>,
    pub folder_size_cancel: Arc<AtomicBool>,
    pub folder_item_count_cancel: Arc<AtomicBool>,
    pub count_items_cancel: Arc<AtomicBool>,
    pub remote_sessions: RemoteSessionRegistry,
    pub remote_secrets: Arc<dyn RemoteSecretStore>,
}

impl Default for SessionState {
    fn default() -> Self {
        Self {
            handshake_done: false,
            binary_hot_frames: Arc::new(AtomicBool::new(false)),
            expected_token: None,
            shutdown: false,
            duplicate_check_cancel: Arc::new(AtomicBool::new(false)),
            disk_cleanup_cancel: Arc::new(AtomicBool::new(false)),
            folder_size_cancel: Arc::new(AtomicBool::new(false)),
            folder_item_count_cancel: Arc::new(AtomicBool::new(false)),
            count_items_cancel: Arc::new(AtomicBool::new(false)),
            remote_sessions: RemoteSessionRegistry::new(DefaultRemoteProviderFactory),
            remote_secrets: default_remote_secret_store(),
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

impl SessionState {
    #[cfg(test)]
    pub(crate) fn with_remote_provider_factory(
        factory: impl crate::remote::provider::RemoteProviderFactory + 'static,
    ) -> Self {
        Self {
            remote_sessions: RemoteSessionRegistry::new(factory),
            ..Self::default()
        }
    }

    #[cfg(test)]
    pub(crate) fn with_remote_provider_factory_and_secret_store<S>(
        factory: impl crate::remote::provider::RemoteProviderFactory + 'static,
        remote_secrets: Arc<S>,
    ) -> Self
    where
        S: RemoteSecretStore + 'static,
    {
        Self {
            remote_sessions: RemoteSessionRegistry::new(factory),
            remote_secrets,
            ..Self::default()
        }
    }
}

fn default_remote_secret_store() -> Arc<dyn RemoteSecretStore> {
    #[cfg(test)]
    {
        Arc::new(MemoryRemoteSecretStore::default())
    }

    #[cfg(not(test))]
    {
        Arc::new(WindowsRemoteSecretStore)
    }
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
