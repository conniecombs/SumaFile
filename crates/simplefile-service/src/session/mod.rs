mod io;
mod jobs;

use std::time::Instant;

use crate::dispatch::{dispatch, Dispatch, SessionState};
use crate::progress::OperationRegistry;
use crate::scheduler::BlockingScheduler;
use crate::watcher::WatcherState;
use io::{read_frame_with_limit, spawn_writer, write_json};

/// Before the handshake only a handshake request is acceptable; nothing that
/// legitimately arrives then needs more than this, so an unauthenticated
/// client cannot make the service allocate the full 80 MiB frame limit.
const PRE_HANDSHAKE_FRAME_LIMIT: u32 = 64 * 1024;
use jobs::{
    spawn_copy_move_with_progress, spawn_disk_cleanup, spawn_duplicate_check,
    spawn_folder_item_count, spawn_folder_metrics, spawn_folder_size, spawn_generate_thumbnail,
    spawn_generate_thumbnails, spawn_install_update, spawn_list_directory, spawn_search_files,
    CopyMoveJob, DiskCleanupJob, DuplicateCheckJob, EventSink, SearchJob,
};
use serde_json::{json, Value};
use simplefile_ipc::frame::FrameError;
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use tokio::io::{AsyncRead, AsyncWrite};

pub async fn serve_connection<R, W>(
    mut reader: R,
    writer: W,
    mut state: SessionState,
) -> Result<(), String>
where
    R: AsyncRead + Unpin,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let writer = spawn_writer(writer);
    let operations = std::sync::Arc::new(OperationRegistry::default());
    let searches = std::sync::Arc::new(OperationRegistry::default());
    let duplicate_scans = std::sync::Arc::new(OperationRegistry::default());
    let cleanup_scans = std::sync::Arc::new(OperationRegistry::default());
    let scheduler = BlockingScheduler::default();
    let mut watcher_state = WatcherState::default();
    let binary_hot_frames = state.binary_hot_frames.clone();
    let cache = state.cache.clone();
    let events = EventSink::new(writer.clone(), binary_hot_frames.clone());

    let result = async {
        loop {
            let frame_limit = if state.handshake_done {
                simplefile_ipc::MAX_FRAME_BYTES
            } else {
                PRE_HANDSHAKE_FRAME_LIMIT
            };
            let payload = match read_frame_with_limit(&mut reader, frame_limit).await {
                Ok(payload) => payload,
                Err(FrameError::UnexpectedEof) => return Ok(()),
                Err(FrameError::Oversize { length }) => {
                    return Err(format!("inbound frame too large: {length}"));
                }
                Err(error) => return Err(error.to_string()),
            };

            // A malformed frame is the client's bug, not grounds to drop the
            // session: answer with a JSON-RPC parse error and keep serving.
            let request: JsonRpcRequest = match serde_json::from_slice(&payload) {
                Ok(request) => request,
                Err(error) => {
                    let response = JsonRpcResponse::error(
                        None,
                        simplefile_ipc::ERR_PARSE,
                        format!("invalid JSON-RPC request: {error}"),
                    );
                    write_json(&writer, &response).await?;
                    continue;
                }
            };

            let dispatch_start = Instant::now();
            let action = dispatch(&mut state, &request);
            let dispatch_ms = dispatch_start.elapsed().as_secs_f64() * 1000.0;

            match action {
                Dispatch::Reply(response) => write_json(&writer, &response).await?,
                Dispatch::Blocking { id, job } => {
                    jobs::spawn_blocking_reply(writer.clone(), scheduler.clone(), id, job);
                }
                Dispatch::RemoteTransfer {
                    id,
                    provider,
                    request,
                } => {
                    let cancel = operations.register(&request.operation_id).await;
                    jobs::spawn_remote_transfer(
                        writer.clone(),
                        operations.clone(),
                        scheduler.clone(),
                        events.clone(),
                        cache.clone(),
                        jobs::RemoteTransferJob {
                            id,
                            provider,
                            request,
                            cancel,
                        },
                    );
                }
                Dispatch::ListDirectory { id, path, options } => {
                    spawn_list_directory(
                        writer.clone(),
                        scheduler.clone(),
                        binary_hot_frames.clone(),
                        cache.clone(),
                        id,
                        path,
                        options,
                    );
                }
                Dispatch::CopyWithProgress { id, params } => {
                    let op_id = params
                        .operation_id
                        .clone()
                        .unwrap_or_else(crate::progress::generate_transfer_operation_id);
                    let cancel = operations.register(&op_id).await;
                    spawn_copy_move_with_progress(
                        writer.clone(),
                        operations.clone(),
                        scheduler.clone(),
                        events.clone(),
                        cache.clone(),
                        CopyMoveJob {
                            id,
                            params,
                            op_id,
                            cancel,
                            is_copy: true,
                        },
                    );
                }
                Dispatch::MoveWithProgress { id, params } => {
                    let op_id = params
                        .operation_id
                        .clone()
                        .unwrap_or_else(crate::progress::generate_transfer_operation_id);
                    let cancel = operations.register(&op_id).await;
                    spawn_copy_move_with_progress(
                        writer.clone(),
                        operations.clone(),
                        scheduler.clone(),
                        events.clone(),
                        cache.clone(),
                        CopyMoveJob {
                            id,
                            params,
                            op_id,
                            cancel,
                            is_copy: false,
                        },
                    );
                }
                Dispatch::ArchiveOperation {
                    id,
                    operation_id,
                    operation,
                } => {
                    // Registered before spawning, so a cancel_operation that
                    // follows the start request finds the token.
                    let cancel = operations.register(&operation_id).await;
                    jobs::spawn_archive_operation(
                        writer.clone(),
                        operations.clone(),
                        scheduler.clone(),
                        events.clone(),
                        cache.clone(),
                        jobs::ArchiveOperationJob {
                            id,
                            operation_id,
                            operation,
                            cancel,
                        },
                    );
                }
                Dispatch::CancelOperation { id, operation_id } => {
                    let cancelled = operations.cancel(&operation_id).await;
                    write_json(&writer, &JsonRpcResponse::result(id, json!(cancelled))).await?;
                }
                Dispatch::SearchFiles { id, options } => {
                    // Register before spawning: a cancel that follows the start
                    // request immediately (type-ahead) must find the token.
                    let registry_id = options
                        .search_id
                        .clone()
                        .unwrap_or_else(simplefile_core::utils::generate_operation_id);
                    let cancel = searches.register(&registry_id).await;
                    spawn_search_files(
                        writer.clone(),
                        searches.clone(),
                        scheduler.clone(),
                        events.clone(),
                        SearchJob {
                            id,
                            options,
                            registry_id,
                            cancel,
                        },
                    );
                }
                Dispatch::CancelSearch { id, search_id } => {
                    searches.cancel(&search_id).await;
                    write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await?;
                }
                Dispatch::WatchDirectory { id, path } => {
                    let events = events.clone();
                    let cache_for_watch = cache.clone();
                    let result =
                        crate::watcher::watch_directory(path, &mut watcher_state, move |change| {
                            cache_for_watch.invalidate_path(&change.path);
                            events.emit_file_change(&change);
                        });
                    let response = match result {
                        Ok(()) => JsonRpcResponse::result(id, Value::Null),
                        Err(message) => JsonRpcResponse::application_error(id, message),
                    };
                    write_json(&writer, &response).await?;
                }
                Dispatch::UnwatchDirectory { id } => {
                    crate::watcher::unwatch_directory(&mut watcher_state);
                    write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await?;
                }
                Dispatch::DuplicateCheck {
                    id,
                    directory,
                    min_size,
                    partial_hash_bytes,
                    max_depth,
                    exclude_patterns,
                    network_mode,
                    operation_id,
                } => {
                    let operation_id =
                        operation_id.unwrap_or_else(simplefile_core::utils::generate_operation_id);
                    let cancel = duplicate_scans.register(&operation_id).await;
                    spawn_duplicate_check(
                        writer.clone(),
                        duplicate_scans.clone(),
                        scheduler.clone(),
                        events.clone(),
                        DuplicateCheckJob {
                            id,
                            directory,
                            min_size,
                            partial_hash_bytes,
                            max_depth,
                            exclude_patterns,
                            network_mode,
                            operation_id,
                            cancel,
                        },
                    );
                }
                Dispatch::CancelDuplicateCheck { id, operation_id } => {
                    if let Some(operation_id) = operation_id {
                        duplicate_scans.cancel(&operation_id).await;
                    } else {
                        duplicate_scans.cancel_all().await;
                    }
                    write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await?;
                }
                Dispatch::DiskCleanup {
                    id,
                    directory,
                    size_threshold,
                    operation_id,
                } => {
                    let operation_id =
                        operation_id.unwrap_or_else(simplefile_core::utils::generate_operation_id);
                    let cancel = cleanup_scans.register(&operation_id).await;
                    spawn_disk_cleanup(
                        writer.clone(),
                        cleanup_scans.clone(),
                        scheduler.clone(),
                        events.clone(),
                        DiskCleanupJob {
                            id,
                            directory,
                            size_threshold,
                            operation_id,
                            cancel,
                        },
                    );
                }
                Dispatch::CancelDiskCleanup { id, operation_id } => {
                    if let Some(operation_id) = operation_id {
                        cleanup_scans.cancel(&operation_id).await;
                    } else {
                        cleanup_scans.cancel_all().await;
                    }
                    write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await?;
                }
                Dispatch::InstallUpdate { id } => {
                    spawn_install_update(writer.clone(), scheduler.clone(), events.clone(), id);
                }
                Dispatch::GenerateThumbnail { id, path, size } => {
                    spawn_generate_thumbnail(
                        writer.clone(),
                        scheduler.clone(),
                        binary_hot_frames.clone(),
                        cache.clone(),
                        id,
                        path,
                        size,
                    );
                }
                Dispatch::GenerateThumbnails { id, paths, size } => {
                    spawn_generate_thumbnails(
                        writer.clone(),
                        scheduler.clone(),
                        binary_hot_frames.clone(),
                        cache.clone(),
                        id,
                        paths,
                        size,
                    );
                }
                Dispatch::CalculateFolderSize { id, path, cancel } => {
                    spawn_folder_size(
                        writer.clone(),
                        scheduler.clone(),
                        cache.clone(),
                        id,
                        path,
                        cancel,
                    );
                }
                Dispatch::CountFolderItems { id, path, cancel } => {
                    spawn_folder_item_count(
                        writer.clone(),
                        scheduler.clone(),
                        cache.clone(),
                        id,
                        path,
                        cancel,
                    );
                }
                Dispatch::GetFolderMetrics { id, path, cancel } => {
                    spawn_folder_metrics(
                        writer.clone(),
                        scheduler.clone(),
                        cache.clone(),
                        id,
                        path,
                        cancel,
                    );
                }
                Dispatch::Shutdown(response) => {
                    crate::watcher::unwatch_directory(&mut watcher_state);
                    write_json(&writer, &response).await?;
                    state.shutdown = true;
                    return Ok(());
                }
            }

            let total_ms = dispatch_start.elapsed().as_secs_f64() * 1000.0;
            if total_ms > 1.0 {
                log::debug!(
                    "ipc.timing method={} dispatch_ms={:.2} total_ms={:.2}",
                    request.method,
                    dispatch_ms,
                    total_ms
                );
            }
        }
    }
    .await;
    // Detached blocking jobs must not outlive the client that owns them.
    writer.close();
    operations.cancel_all().await;
    searches.cancel_all().await;
    duplicate_scans.cancel_all().await;
    cleanup_scans.cancel_all().await;
    crate::watcher::unwatch_directory(&mut watcher_state);
    result
}

#[cfg(test)]
mod tests;
