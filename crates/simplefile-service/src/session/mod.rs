mod io;
mod jobs;

use crate::dispatch::{dispatch, Dispatch, SessionState};
use crate::progress::OperationRegistry;
use crate::scheduler::BlockingScheduler;
use crate::watcher::WatcherState;
use io::{read_frame, spawn_writer, write_json};
use jobs::{
    generate_thumbnail_and_reply, generate_thumbnails_and_reply, list_directory_and_reply,
    spawn_copy_move_with_progress, spawn_disk_cleanup, spawn_duplicate_check,
    spawn_folder_item_count, spawn_folder_metrics, spawn_folder_size, spawn_install_update,
    spawn_search_files, DiskCleanupJob, DuplicateCheckJob, EventSink,
};
use serde_json::Value;
use simplefile_ipc::frame::FrameError;
use simplefile_ipc::rpc::{JsonRpcRequest, JsonRpcResponse};
use std::sync::atomic::Ordering;
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

        match dispatch(&mut state, &request) {
            Dispatch::Reply(response) => write_json(&writer, &response).await?,
            Dispatch::ListDirectory { id, path, options } => {
                list_directory_and_reply(
                    &writer,
                    scheduler.clone(),
                    binary_hot_frames.clone(),
                    id,
                    path,
                    options,
                )
                .await?;
            }
            Dispatch::CopyWithProgress { id, params } => {
                spawn_copy_move_with_progress(
                    writer.clone(),
                    operations.clone(),
                    scheduler.clone(),
                    events.clone(),
                    id,
                    params,
                    true,
                );
            }
            Dispatch::MoveWithProgress { id, params } => {
                spawn_copy_move_with_progress(
                    writer.clone(),
                    operations.clone(),
                    scheduler.clone(),
                    events.clone(),
                    id,
                    params,
                    false,
                );
            }
            Dispatch::CancelOperation { id, operation_id } => {
                operations.cancel(&operation_id).await;
                write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await?;
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
                operation_id,
            } => {
                spawn_duplicate_check(
                    writer.clone(),
                    scheduler.clone(),
                    events.clone(),
                    DuplicateCheckJob {
                        cancel: state.duplicate_check_cancel.clone(),
                        id,
                        directory,
                        min_size,
                        partial_hash_bytes,
                        operation_id,
                    },
                );
            }
            Dispatch::CancelDuplicateCheck { id } => {
                state.duplicate_check_cancel.store(true, Ordering::Relaxed);
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
                    scheduler.clone(),
                    events.clone(),
                    DiskCleanupJob {
                        cancel: state.disk_cleanup_cancel.clone(),
                        id,
                        directory,
                        size_threshold,
                        operation_id,
                    },
                );
            }
            Dispatch::CancelDiskCleanup { id } => {
                state.disk_cleanup_cancel.store(true, Ordering::Relaxed);
                write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await?;
            }
            Dispatch::InstallUpdate { id } => {
                spawn_install_update(writer.clone(), scheduler.clone(), events.clone(), id);
            }
            Dispatch::GenerateThumbnail { id, path, size } => {
                generate_thumbnail_and_reply(
                    &writer,
                    scheduler.clone(),
                    binary_hot_frames.clone(),
                    id,
                    path,
                    size,
                )
                .await?;
            }
            Dispatch::GenerateThumbnails { id, paths, size } => {
                generate_thumbnails_and_reply(
                    &writer,
                    scheduler.clone(),
                    binary_hot_frames.clone(),
                    id,
                    paths,
                    size,
                )
                .await?;
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
    }
}

#[cfg(test)]
mod tests;
