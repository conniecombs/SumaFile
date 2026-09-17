mod io;
mod jobs;

use std::time::Instant;

use crate::dispatch::{dispatch, Dispatch, SessionState};
use crate::progress::OperationRegistry;
use crate::scheduler::BlockingScheduler;
use crate::watcher::WatcherState;
use io::{read_frame, spawn_writer, write_json};
use jobs::{
    spawn_copy_move_with_progress, spawn_disk_cleanup, spawn_duplicate_check,
    spawn_folder_item_count, spawn_folder_metrics, spawn_folder_size, spawn_generate_thumbnail,
    spawn_generate_thumbnails, spawn_install_update, spawn_list_directory, spawn_search_files,
    CopyMoveJob, DiskCleanupJob, DuplicateCheckJob, EventSink,
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
    let events = EventSink::new(writer.clone(), binary_hot_frames.clone());

    loop {
        let payload = match read_frame(&mut reader).await {
            Ok(payload) => payload,
            Err(FrameError::UnexpectedEof) => return Ok(()),
            Err(FrameError::Oversize { length }) => {
                return Err(format!("inbound frame too large: {length}"));
            }
            Err(error) => return Err(error.to_string()),
        };

        let request: JsonRpcRequest = serde_json::from_slice(&payload)
            .map_err(|error| format!("invalid JSON-RPC request: {error}"))?;

        let dispatch_start = Instant::now();
        let action = dispatch(&mut state, &request);
        let dispatch_ms = dispatch_start.elapsed().as_secs_f64() * 1000.0;

        match action {
            Dispatch::Reply(response) => write_json(&writer, &response).await?,
            Dispatch::ListDirectory { id, path, options } => {
                spawn_list_directory(
                    writer.clone(),
                    scheduler.clone(),
                    binary_hot_frames.clone(),
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
                    CopyMoveJob {
                        id,
                        params,
                        op_id,
                        cancel,
                        is_copy: false,
                    },
                );
            }
            Dispatch::CancelOperation { id, operation_id } => {
                let cancelled = operations.cancel(&operation_id).await;
                write_json(&writer, &JsonRpcResponse::result(id, json!(cancelled))).await?;
            }
            Dispatch::SearchFiles { id, options } => {
                spawn_search_files(
                    writer.clone(),
                    searches.clone(),
                    scheduler.clone(),
                    events.clone(),
                    id,
                    options,
                );
            }
            Dispatch::CancelSearch { id, search_id } => {
                searches.cancel(&search_id).await;
                write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await?;
            }
            Dispatch::WatchDirectory { id, path } => {
                let events = events.clone();
                let result =
                    crate::watcher::watch_directory(path, &mut watcher_state, move |change| {
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
                    id,
                    paths,
                    size,
                );
            }
            Dispatch::CalculateFolderSize { id, path, cancel } => {
                spawn_folder_size(writer.clone(), scheduler.clone(), id, path, cancel);
            }
            Dispatch::CountFolderItems { id, path, cancel } => {
                spawn_folder_item_count(writer.clone(), scheduler.clone(), id, path, cancel);
            }
            Dispatch::GetFolderMetrics { id, path, cancel } => {
                spawn_folder_metrics(writer.clone(), scheduler.clone(), id, path, cancel);
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

#[cfg(test)]
mod tests;
