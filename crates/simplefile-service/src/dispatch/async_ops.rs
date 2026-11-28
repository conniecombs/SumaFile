use super::folder_jobs::Metrics;
use super::params::{
    parse_nullable_params, parse_params, CancelEntryFactsParams, CancelFolderParams,
    DiskCleanupParams, DuplicateCheckParams, EntryFactsParams, ImagePreviewParams,
    ListDirectoryParams, ListingViewIdParams, ListingViewRangeParams, OpenListingViewParams,
    OperationIdParams, OptionalOperationIdParams, PathParams, ProgressCopyMoveParams,
    PutEntryFactsParams, SearchFilesParams, SearchIdParams, ThumbnailBatchParams, ThumbnailParams,
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

pub(super) fn open_listing_view(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<OpenListingViewParams>(request) {
        Ok(params) => Dispatch::OpenListingView {
            id: request.id.clone(),
            params,
        },
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn get_listing_view_range(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ListingViewRangeParams>(request) {
        Ok(params) => Dispatch::GetListingViewRange {
            id: request.id.clone(),
            view_id: params.view_id,
            offset: params.offset,
            count: params.count,
        },
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn refresh_listing_view(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ListingViewIdParams>(request) {
        Ok(params) => Dispatch::RefreshListingView {
            id: request.id.clone(),
            view_id: params.view_id,
        },
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn close_listing_view(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ListingViewIdParams>(request) {
        Ok(params) => Dispatch::CloseListingView {
            id: request.id.clone(),
            view_id: params.view_id,
        },
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

pub(super) fn render_image_preview(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<ImagePreviewParams>(request) {
        Ok(p) => Dispatch::RenderImagePreview {
            id: request.id.clone(),
            path: p.path,
            max_dimension: p.max_dimension,
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
            let cancel = state.folder_jobs.start(&p.path, Metrics::SIZE);
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
            let cancel = state.folder_jobs.start(&p.path, Metrics::COUNT);
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
            // One traversal works out both, so a cancel of either kind stops it.
            let cancel = state.folder_jobs.start(&p.path, Metrics::BOTH);
            Dispatch::GetFolderMetrics {
                id: request.id.clone(),
                path: p.path,
                cancel,
            }
        }
        Err(r) => Dispatch::Reply(r),
    }
}

/// Cancels the running requests for `metrics`: those of the folder named in
/// the parameters, or all of them when no folder is named.
fn cancel_folder_jobs(
    state: &mut SessionState,
    request: &JsonRpcRequest,
    metrics: Metrics,
) -> Dispatch {
    match parse_nullable_params::<CancelFolderParams>(request) {
        Ok(p) => {
            state.folder_jobs.cancel(metrics, p.path.as_deref());
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn cancel_folder_size(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    cancel_folder_jobs(state, request, Metrics::SIZE)
}

pub(super) fn cancel_folder_item_count(
    state: &mut SessionState,
    request: &JsonRpcRequest,
) -> Dispatch {
    cancel_folder_jobs(state, request, Metrics::COUNT)
}

pub(super) fn cancel_count_items(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    cancel_folder_jobs(state, request, Metrics::COUNT)
}

pub(super) fn cancel_folder_metrics(
    state: &mut SessionState,
    request: &JsonRpcRequest,
) -> Dispatch {
    cancel_folder_jobs(state, request, Metrics::BOTH)
}

/// At most this many request ids keep a cancel flag; the app reuses one id per pane, so the map stays small.
const MAX_ENTRY_FACT_REQUESTS: usize = 64;

pub(super) fn get_entry_facts(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<EntryFactsParams>(request) {
        Ok(p) => {
            if state.entry_facts_cancel.len() >= MAX_ENTRY_FACT_REQUESTS {
                state.entry_facts_cancel.clear();
            }
            let cancel = Arc::new(AtomicBool::new(false));
            // The same id again means the app moved on: the earlier request stops.
            if let Some(previous) = state
                .entry_facts_cancel
                .insert(p.request_id.clone(), cancel.clone())
            {
                previous.store(true, Ordering::Relaxed);
            }
            Dispatch::GetEntryFacts {
                id: request.id.clone(),
                request_id: p.request_id,
                paths: p.paths,
                kinds: p.kinds,
                cancel,
            }
        }
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn cancel_entry_facts(state: &mut SessionState, request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<CancelEntryFactsParams>(request) {
        Ok(p) => {
            if let Some(flag) = state.entry_facts_cancel.remove(&p.request_id) {
                flag.store(true, Ordering::Relaxed);
            }
            Dispatch::Reply(JsonRpcResponse::result(request.id.clone(), Value::Null))
        }
        Err(r) => Dispatch::Reply(r),
    }
}

pub(super) fn put_entry_facts(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<PutEntryFactsParams>(request) {
        Ok(p) => Dispatch::PutEntryFacts {
            id: request.id.clone(),
            facts: p.facts,
        },
        Err(r) => Dispatch::Reply(r),
    }
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
    match super::params::parse_watch_directory_params(request) {
        Ok(paths) => Dispatch::WatchDirectory {
            id: request.id.clone(),
            paths,
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
            max_depth: params.max_depth,
            exclude_patterns: params.exclude_patterns.unwrap_or_default(),
            network_mode: params.network_mode,
            operation_id: params.operation_id,
        },
        Err(response) => Dispatch::Reply(response),
    }
}

pub(super) fn cancel_duplicate_check(request: &JsonRpcRequest) -> Dispatch {
    match parse_params::<OptionalOperationIdParams>(request) {
        Ok(params) => Dispatch::CancelDuplicateCheck {
            id: request.id.clone(),
            operation_id: params.operation_id,
        },
        Err(response) => Dispatch::Reply(response),
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
    match parse_params::<OptionalOperationIdParams>(request) {
        Ok(params) => Dispatch::CancelDiskCleanup {
            id: request.id.clone(),
            operation_id: params.operation_id,
        },
        Err(response) => Dispatch::Reply(response),
    }
}
