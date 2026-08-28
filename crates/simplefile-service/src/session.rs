use serde::Serialize;
use serde_json::{json, Value};
use simplefile_ipc::frame::{decode_length, FrameError};
use simplefile_ipc::rpc::{JsonRpcNotification, JsonRpcRequest, JsonRpcResponse};
use simplefile_ipc::{
    FILE_CHANGE, LIST_DIRECTORY_CHUNK, MAX_FRAME_BYTES, OPERATION_PROGRESS,
    PREFIX_RESULT_TOO_LARGE, SEARCH_COMPLETE, SEARCH_RESULTS_BATCH, UPDATE_CHUNK,
};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

use crate::dispatch::{dispatch, Dispatch, ProgressCopyMoveParams, SessionState};
use crate::progress::OperationRegistry;
use crate::scheduler::BlockingScheduler;
use crate::watcher::WatcherState;
use simplefile_core::cleanup::{scan_disk_cleanup, scan_duplicate_check, DuplicateScanOptions};
use simplefile_core::dir_list::ListDirectoryOptions;
use simplefile_core::models::{FileChangeEvent, ProgressUpdate, SearchResult};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio::sync::{mpsc, oneshot};

const OUTBOUND_QUEUE_CAPACITY: usize = 1024;
const WRITE_BATCH_LIMIT: usize = 64;
const WRITE_BATCH_BYTES: usize = 1024 * 1024;

#[derive(Clone)]
struct OutboundSink {
    sender: mpsc::Sender<OutboundFrame>,
}

struct OutboundFrame {
    payload: Vec<u8>,
    ack: Option<oneshot::Sender<Result<(), String>>>,
}

#[derive(Clone)]
struct EventSink {
    outbound: OutboundSink,
    binary_hot_frames: Arc<AtomicBool>,
}

struct DuplicateCheckJob {
    cancel: Arc<AtomicBool>,
    id: Option<Value>,
    directory: String,
    min_size: Option<u64>,
    partial_hash_bytes: Option<u64>,
    operation_id: Option<String>,
}

struct DiskCleanupJob {
    cancel: Arc<AtomicBool>,
    id: Option<Value>,
    directory: String,
    size_threshold: Option<u64>,
    operation_id: Option<String>,
}

impl EventSink {
    fn emit<T: Serialize + ?Sized>(&self, method: &str, params: &T) {
        if let Ok(params) = serde_json::to_value(params) {
            let notification = JsonRpcNotification::new(method, params);
            if let Ok(payload) = encode_json_payload(&notification) {
                let _ = self.outbound.send_payload_blocking(payload);
            }
        }
    }

    fn emit_progress(&self, update: &ProgressUpdate) {
        if self.binary_hot_frames.load(Ordering::Relaxed) {
            if let Ok(payload) = crate::binary::encode_progress_update(update) {
                let _ = self.outbound.send_payload_blocking(payload);
                return;
            }
        }
        self.emit(OPERATION_PROGRESS, update);
    }

    fn emit_file_change(&self, change: &FileChangeEvent) {
        if self.binary_hot_frames.load(Ordering::Relaxed) {
            if let Ok(payload) = crate::binary::encode_file_change(change) {
                let _ = self.outbound.send_payload_blocking(payload);
                return;
            }
        }
        self.emit(FILE_CHANGE, change);
    }

    fn emit_search_results_batch(&self, batch: &[SearchResult]) {
        if self.binary_hot_frames.load(Ordering::Relaxed) {
            if let Ok(payload) = crate::binary::encode_search_results_batch(batch) {
                let _ = self.outbound.send_payload_blocking(payload);
                return;
            }
        }
        self.emit(SEARCH_RESULTS_BATCH, batch);
    }
}

impl OutboundSink {
    async fn enqueue_payload(&self, payload: Vec<u8>) -> Result<(), String> {
        self.sender
            .send(OutboundFrame { payload, ack: None })
            .await
            .map_err(|_| "IPC writer is closed".to_string())
    }

    async fn write_payload(&self, payload: Vec<u8>) -> Result<(), String> {
        let (ack, done) = oneshot::channel();
        self.sender
            .send(OutboundFrame {
                payload,
                ack: Some(ack),
            })
            .await
            .map_err(|_| "IPC writer is closed".to_string())?;
        done.await
            .map_err(|_| "IPC writer closed before confirming write".to_string())?
    }

    fn send_payload_blocking(&self, payload: Vec<u8>) -> Result<(), String> {
        self.sender
            .blocking_send(OutboundFrame { payload, ack: None })
            .map_err(|_| "IPC writer is closed".to_string())
    }
}

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
    let events = EventSink {
        outbound: writer.clone(),
        binary_hot_frames: binary_hot_frames.clone(),
    };

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

fn spawn_install_update(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    events: EventSink,
    id: Option<Value>,
) {
    tokio::spawn(async move {
        let result = scheduler
            .run_general(move || {
                simplefile_core::updater::install_update_with_progress(|downloaded, total| {
                    events.emit(UPDATE_CHUNK, &[downloaded, total]);
                })
            })
            .await;

        let response = match result {
            Ok(Ok(())) => JsonRpcResponse::result(id, Value::Null),
            Ok(Err(message)) => JsonRpcResponse::application_error(id, message),
            Err(error) => {
                JsonRpcResponse::application_error(id, format!("update task failed: {error}"))
            }
        };
        let _ = write_json(&writer, &response).await;
    });
}

fn spawn_folder_size(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    id: Option<Value>,
    path: String,
    cancel: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        let result = scheduler
            .run_general(move || simplefile_core::file_ops::calculate_folder_size(&path, &cancel))
            .await;

        let response = match result {
            Ok(Some(size)) => JsonRpcResponse::result(id, json!(size)),
            Ok(None) => JsonRpcResponse::application_error(id, "cancelled".to_string()),
            Err(error) => {
                JsonRpcResponse::application_error(id, format!("folder size task failed: {error}"))
            }
        };
        let _ = write_json(&writer, &response).await;
    });
}

fn spawn_folder_item_count(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    id: Option<Value>,
    path: String,
    cancel: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        let result = scheduler
            .run_general(move || simplefile_core::file_ops::count_folder_items(&path, &cancel))
            .await;

        let response = match result {
            Ok(Some(count)) => JsonRpcResponse::result(id, json!(count)),
            Ok(None) => JsonRpcResponse::application_error(id, "cancelled".to_string()),
            Err(error) => JsonRpcResponse::application_error(
                id,
                format!("folder item count task failed: {error}"),
            ),
        };
        let _ = write_json(&writer, &response).await;
    });
}

fn spawn_folder_metrics(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    id: Option<Value>,
    path: String,
    cancel: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        let cancel2 = cancel.clone();
        let cancel3 = cancel.clone();
        let path2 = path.clone();
        let path3 = path.clone();

        let size_scheduler = scheduler.clone();
        let count_scheduler = scheduler.clone();
        let subdirs_scheduler = scheduler;
        let size_handle = async move {
            size_scheduler
                .run_general(move || {
                    simplefile_core::file_ops::calculate_folder_size(&path, &cancel)
                })
                .await
        };
        let count_handle = async move {
            count_scheduler
                .run_general(move || {
                    simplefile_core::file_ops::count_folder_items(&path2, &cancel2)
                })
                .await
        };
        let subdirs_handle = async move {
            subdirs_scheduler
                .run_general(move || {
                    if cancel3.load(Ordering::Relaxed) {
                        Err("cancelled".to_string())
                    } else {
                        simplefile_core::file_ops::list_subdirectories(&path3)
                    }
                })
                .await
        };

        let (size_result, count_result, subdirs_result) =
            tokio::join!(size_handle, count_handle, subdirs_handle);

        let response = match (size_result, count_result, subdirs_result) {
            (Ok(Some(size)), Ok(Some(count)), Ok(Ok(subdirs))) => JsonRpcResponse::result(
                id,
                json!({
                    "size": size,
                    "itemCount": count,
                    "subdirectories": subdirs,
                }),
            ),
            (Ok(None), _, _) | (_, Ok(None), _) => {
                JsonRpcResponse::application_error(id, "cancelled".to_string())
            }
            (_, _, Ok(Err(message))) => JsonRpcResponse::application_error(id, message),
            (Err(error), _, _) | (_, Err(error), _) | (_, _, Err(error)) => {
                JsonRpcResponse::application_error(
                    id,
                    format!("folder metrics task failed: {error}"),
                )
            }
        };
        let _ = write_json(&writer, &response).await;
    });
}

async fn list_directory_and_reply(
    writer: &OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    id: Option<Value>,
    path: String,
    options: Option<ListDirectoryOptions>,
) -> Result<(), String> {
    let binary_request_id = binary_response_id(&binary_hot_frames, &id);
    let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
    let join = tokio::spawn(async move {
        scheduler
            .run_general(move || {
                if let Some(options) = options {
                    simplefile_core::dir_list::list_directory_with_options(path, options, |chunk| {
                        tx.send(chunk).map_err(|error| error.to_string())
                    })
                } else {
                    simplefile_core::dir_list::list_directory(path, |chunk| {
                        tx.send(chunk).map_err(|error| error.to_string())
                    })
                }
            })
            .await
    });

    while let Some(chunk) = rx.recv().await {
        if let Some(request_id) = binary_request_id {
            let payload = crate::binary::encode_directory_listing_chunk(request_id, &chunk)?;
            queue_payload(writer, &payload).await?;
        } else {
            let mut params = serde_json::to_value(&chunk)
                .map_err(|error| format!("failed to serialize listing chunk: {error}"))?;
            if let Some(object) = params.as_object_mut() {
                if let Some(request_id) = &id {
                    object.insert("requestId".to_string(), request_id.clone());
                }
            }
            write_json(
                writer,
                &JsonRpcNotification::new(LIST_DIRECTORY_CHUNK, params),
            )
            .await?;
        }
    }

    match join
        .await
        .map_err(|error| format!("listing task failed: {error}"))?
    {
        Ok(Ok(listing)) => {
            if let Some(request_id) = binary_request_id {
                let payload = crate::binary::encode_directory_listing_result(request_id, &listing)?;
                write_binary_response(writer, id, &payload).await
            } else {
                let result = serde_json::to_value(&listing)
                    .map_err(|error| format!("failed to serialize listing: {error}"))?;
                write_json(writer, &JsonRpcResponse::result(id, result)).await
            }
        }
        Ok(Err(message)) => {
            write_json(writer, &JsonRpcResponse::application_error(id, message)).await
        }
        Err(message) => {
            write_json(
                writer,
                &JsonRpcResponse::application_error(id, format!("listing task failed: {message}")),
            )
            .await
        }
    }
}

async fn generate_thumbnail_and_reply(
    writer: &OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    id: Option<Value>,
    path: String,
    size: Option<u32>,
) -> Result<(), String> {
    match scheduler
        .run_general(move || simplefile_core::preview::generate_thumbnail(path, size))
        .await
    {
        Ok(Ok(result)) => {
            if let Some(request_id) = binary_response_id(&binary_hot_frames, &id) {
                let payload = crate::binary::encode_thumbnail_result(request_id, &result)?;
                write_binary_response(writer, id, &payload).await
            } else {
                write_json(writer, &JsonRpcResponse::result(id, json!(result))).await
            }
        }
        Ok(Err(message)) => {
            write_json(writer, &JsonRpcResponse::application_error(id, message)).await
        }
        Err(message) => {
            write_json(
                writer,
                &JsonRpcResponse::application_error(
                    id,
                    format!("thumbnail task failed: {message}"),
                ),
            )
            .await
        }
    }
}

async fn generate_thumbnails_and_reply(
    writer: &OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    id: Option<Value>,
    paths: Vec<String>,
    size: Option<u32>,
) -> Result<(), String> {
    match scheduler
        .run_general(move || simplefile_core::preview::generate_thumbnails(paths, size))
        .await
    {
        Ok(results) => {
            if let Some(request_id) = binary_response_id(&binary_hot_frames, &id) {
                let payload = crate::binary::encode_thumbnail_results_result(request_id, &results)?;
                write_binary_response(writer, id, &payload).await
            } else {
                let result = serde_json::to_value(results).unwrap_or(Value::Null);
                write_json(writer, &JsonRpcResponse::result(id, result)).await
            }
        }
        Err(message) => {
            write_json(
                writer,
                &JsonRpcResponse::application_error(
                    id,
                    format!("thumbnail batch task failed: {message}"),
                ),
            )
            .await
        }
    }
}

fn spawn_copy_move_with_progress(
    writer: OutboundSink,
    registry: std::sync::Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    id: Option<Value>,
    params: ProgressCopyMoveParams,
    is_copy: bool,
) {
    tokio::spawn(async move {
        let op_id = params
            .operation_id
            .unwrap_or_else(crate::progress::generate_transfer_operation_id);
        let cancel = registry.register(&op_id).await;
        let sources = params.sources;
        let destination = params.destination;
        let conflict_action = params.conflict_action;
        let operation_type = if is_copy { "copy" } else { "move" };
        let events_for_task = events.clone();
        let op_id_for_task = op_id.clone();

        let result = scheduler
            .run_transfer(move || {
                let emit = |update| events_for_task.emit_progress(&update);
                crate::progress::transfer_with_progress_blocking(
                    operation_type,
                    sources,
                    destination,
                    op_id_for_task,
                    conflict_action,
                    cancel,
                    &emit,
                )
            })
            .await;

        let response = match result {
            Ok(Ok(results)) => match serde_json::to_value(results) {
                Ok(result) => JsonRpcResponse::result(id, result),
                Err(error) => JsonRpcResponse::application_error(
                    id,
                    format!("failed to serialize transfer result: {error}"),
                ),
            },
            Ok(Err(message)) => JsonRpcResponse::application_error(id, message),
            Err(error) => {
                JsonRpcResponse::application_error(id, format!("transfer task failed: {error}"))
            }
        };

        let _ = write_json(&writer, &response).await;
        registry.remove(&op_id).await;
    });
}

fn spawn_search_files(
    writer: OutboundSink,
    registry: std::sync::Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    id: Option<Value>,
    options: simplefile_core::models::SearchOptions,
) {
    tokio::spawn(async move {
        let binary_request_id = binary_response_id(&events.binary_hot_frames, &id);
        let search_id = options.search_id.clone();
        let cancel = if let Some(search_id) = search_id.as_deref() {
            registry.register(search_id).await
        } else {
            std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false))
        };

        let events_for_task = events.clone();
        let result = scheduler
            .run_general(move || {
                let emit_batch =
                    |batch: Vec<SearchResult>| events_for_task.emit_search_results_batch(&batch);
                let result = crate::search::search_files_blocking(options, cancel, &emit_batch);
                if let Ok(results) = &result {
                    events_for_task.emit(SEARCH_COMPLETE, &results.len());
                }
                result
            })
            .await;

        let response = match result {
            Ok(Ok(results)) => {
                if let Some(request_id) = binary_request_id {
                    match crate::binary::encode_search_results_result(request_id, &results) {
                        Ok(payload) => {
                            let _ = write_binary_response(&writer, id.clone(), &payload).await;
                            if let Some(search_id) = search_id.as_deref() {
                                registry.remove(search_id).await;
                            }
                            return;
                        }
                        Err(error) => JsonRpcResponse::application_error(
                            id,
                            format!("failed to encode binary search result: {error}"),
                        ),
                    }
                } else {
                    match serde_json::to_value(results) {
                        Ok(result) => JsonRpcResponse::result(id, result),
                        Err(error) => JsonRpcResponse::application_error(
                            id,
                            format!("failed to serialize search result: {error}"),
                        ),
                    }
                }
            }
            Ok(Err(message)) => JsonRpcResponse::application_error(id, message),
            Err(error) => {
                JsonRpcResponse::application_error(id, format!("search task failed: {error}"))
            }
        };

        let _ = write_json(&writer, &response).await;
        if let Some(search_id) = search_id.as_deref() {
            registry.remove(search_id).await;
        }
    });
}

fn spawn_duplicate_check(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    events: EventSink,
    job: DuplicateCheckJob,
) {
    let DuplicateCheckJob {
        cancel,
        id,
        directory,
        min_size,
        partial_hash_bytes,
        operation_id,
    } = job;
    cancel.store(false, Ordering::Relaxed);
    let operation_id = operation_id.unwrap_or_else(|| "duplicate_check".to_string());
    tokio::spawn(async move {
        let events_for_task = events.clone();
        let operation_id_for_task = operation_id.clone();
        let result = scheduler
            .run_general(move || {
                let emit = |current, total, item: &str| {
                    events_for_task.emit_progress(&ProgressUpdate {
                        operation_id: operation_id_for_task.clone(),
                        operation_type: "duplicate-check".to_string(),
                        current,
                        total,
                        current_files: 0,
                        total_files: 0,
                        current_item: item.to_string(),
                        status: "running".to_string(),
                        error: None,
                    });
                };
                scan_duplicate_check(
                    &directory,
                    DuplicateScanOptions::from_params(min_size, partial_hash_bytes),
                    &cancel,
                    emit,
                )
            })
            .await;

        let response = match result {
            Ok(Ok(result)) => match serde_json::to_value(result) {
                Ok(value) => JsonRpcResponse::result(id, value),
                Err(error) => JsonRpcResponse::application_error(
                    id,
                    format!("failed to serialize duplicate check result: {error}"),
                ),
            },
            Ok(Err(message)) => JsonRpcResponse::application_error(id, message),
            Err(error) => JsonRpcResponse::application_error(
                id,
                format!("duplicate check task failed: {error}"),
            ),
        };
        let _ = write_json(&writer, &response).await;
    });
}

fn spawn_disk_cleanup(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    events: EventSink,
    job: DiskCleanupJob,
) {
    let DiskCleanupJob {
        cancel,
        id,
        directory,
        size_threshold,
        operation_id,
    } = job;
    cancel.store(false, Ordering::Relaxed);
    let operation_id = operation_id.unwrap_or_else(|| "disk_cleanup".to_string());
    tokio::spawn(async move {
        let events_for_task = events.clone();
        let operation_id_for_task = operation_id.clone();
        let result = scheduler
            .run_general(move || {
                let emit = |current, total, item: &str| {
                    events_for_task.emit_progress(&ProgressUpdate {
                        operation_id: operation_id_for_task.clone(),
                        operation_type: "cleanup".to_string(),
                        current,
                        total,
                        current_files: 0,
                        total_files: 0,
                        current_item: item.to_string(),
                        status: "running".to_string(),
                        error: None,
                    });
                };
                scan_disk_cleanup(&directory, size_threshold, &cancel, emit)
            })
            .await;

        let response = match result {
            Ok(Ok(result)) => match serde_json::to_value(result) {
                Ok(value) => JsonRpcResponse::result(id, value),
                Err(error) => JsonRpcResponse::application_error(
                    id,
                    format!("failed to serialize cleanup result: {error}"),
                ),
            },
            Ok(Err(message)) => JsonRpcResponse::application_error(id, message),
            Err(error) => {
                JsonRpcResponse::application_error(id, format!("disk cleanup task failed: {error}"))
            }
        };
        let _ = write_json(&writer, &response).await;
    });
}

async fn read_frame<R: AsyncRead + Unpin>(reader: &mut R) -> Result<Vec<u8>, FrameError> {
    let mut header = [0u8; 4];
    if let Err(error) = reader.read_exact(&mut header).await {
        if error.kind() == std::io::ErrorKind::UnexpectedEof {
            return Err(FrameError::UnexpectedEof);
        }
        return Err(FrameError::Io(error.to_string()));
    }
    let length = decode_length(header)?;
    let mut payload = vec![0u8; length as usize];
    reader
        .read_exact(&mut payload)
        .await
        .map_err(|error| FrameError::Io(error.to_string()))?;
    Ok(payload)
}

fn spawn_writer<W>(writer: W) -> OutboundSink
where
    W: AsyncWrite + Unpin + Send + 'static,
{
    let (sender, receiver) = mpsc::channel(OUTBOUND_QUEUE_CAPACITY);
    tokio::spawn(async move {
        let _ = writer_loop(writer, receiver).await;
    });
    OutboundSink { sender }
}

async fn writer_loop<W>(
    mut writer: W,
    mut receiver: mpsc::Receiver<OutboundFrame>,
) -> Result<(), String>
where
    W: AsyncWrite + Unpin,
{
    while let Some(first) = receiver.recv().await {
        let mut frames = Vec::with_capacity(WRITE_BATCH_LIMIT);
        frames.push(first);

        for _ in 1..WRITE_BATCH_LIMIT {
            match receiver.try_recv() {
                Ok(frame) => frames.push(frame),
                Err(mpsc::error::TryRecvError::Empty) => break,
                Err(mpsc::error::TryRecvError::Disconnected) => break,
            }

            let queued_bytes: usize = frames.iter().map(|frame| frame.payload.len() + 4).sum();
            if queued_bytes >= WRITE_BATCH_BYTES {
                break;
            }
        }

        let result = write_frame_batch(&mut writer, &frames).await;
        for frame in frames {
            if let Some(ack) = frame.ack {
                let _ = ack.send(result.clone());
            }
        }

        result?;
    }

    Ok(())
}

async fn write_json<T>(writer: &OutboundSink, value: &T) -> Result<(), String>
where
    T: serde::Serialize,
{
    let payload = encode_json_payload(value)?;
    writer.write_payload(payload).await
}

fn encode_json_payload<T>(value: &T) -> Result<Vec<u8>, String>
where
    T: serde::Serialize + ?Sized,
{
    let payload =
        serde_json::to_vec(value).map_err(|error| format!("failed to encode JSON: {error}"))?;
    if payload.len() > MAX_FRAME_BYTES as usize {
        let error = JsonRpcResponse::application_error(
            None,
            format!("{PREFIX_RESULT_TOO_LARGE} result exceeds 80 MiB; use streamed chunks"),
        );
        serde_json::to_vec(&error).map_err(|err| format!("failed to encode oversize error: {err}"))
    } else {
        Ok(payload)
    }
}

fn binary_response_id(binary_hot_frames: &Arc<AtomicBool>, id: &Option<Value>) -> Option<i32> {
    if binary_hot_frames.load(Ordering::Relaxed) {
        crate::binary::request_id_i32(id)
    } else {
        None
    }
}

async fn write_binary_response(
    writer: &OutboundSink,
    id: Option<Value>,
    payload: &[u8],
) -> Result<(), String> {
    if payload.len() > MAX_FRAME_BYTES as usize {
        let error = JsonRpcResponse::application_error(
            id,
            format!("{PREFIX_RESULT_TOO_LARGE} binary result exceeds 80 MiB; use streamed chunks"),
        );
        write_json(writer, &error).await
    } else {
        writer.write_payload(payload.to_vec()).await
    }
}

async fn queue_payload(writer: &OutboundSink, payload: &[u8]) -> Result<(), String> {
    if payload.len() > MAX_FRAME_BYTES as usize {
        return Err(format!(
            "{PREFIX_RESULT_TOO_LARGE} binary frame exceeds 80 MiB"
        ));
    }
    writer.enqueue_payload(payload.to_vec()).await
}

async fn write_frame_batch<W>(writer: &mut W, frames: &[OutboundFrame]) -> Result<(), String>
where
    W: AsyncWrite + Unpin,
{
    let total_bytes = frames.iter().try_fold(0usize, |acc, frame| {
        validate_payload_length(&frame.payload)?;
        Ok::<usize, String>(acc.saturating_add(frame.payload.len()).saturating_add(4))
    })?;
    let mut batch = Vec::with_capacity(total_bytes);
    for frame in frames {
        append_frame(&mut batch, &frame.payload)?;
    }

    writer
        .write_all(&batch)
        .await
        .map_err(|error| format!("failed to write frame: {error}"))
}

fn append_frame(batch: &mut Vec<u8>, payload: &[u8]) -> Result<(), String> {
    validate_payload_length(payload)?;
    let length = u32::try_from(payload.len()).map_err(|_| {
        format!("{PREFIX_RESULT_TOO_LARGE} frame length exceeds supported u32 range")
    })?;
    batch.extend_from_slice(&length.to_le_bytes());
    batch.extend_from_slice(payload);
    Ok(())
}

fn validate_payload_length(payload: &[u8]) -> Result<(), String> {
    if payload.len() > MAX_FRAME_BYTES as usize {
        return Err(format!("{PREFIX_RESULT_TOO_LARGE} frame exceeds 80 MiB"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use simplefile_ipc::frame::encode_frame;
    use simplefile_ipc::rpc::JsonRpcRequest;
    use simplefile_ipc::{
        BINARY_FRAME_MAGIC, BINARY_LIST_DIRECTORY_CHUNK, BINARY_LIST_DIRECTORY_RESULT,
        HANDSHAKE_METHOD, HEALTH_METHOD, PROTOCOL_VERSION,
    };
    use std::pin::Pin;
    use std::sync::Mutex;
    use std::task::{Context, Poll};
    use tokio::io::duplex;

    #[derive(Clone, Default)]
    struct RecordingWriter {
        writes: Arc<Mutex<Vec<Vec<u8>>>>,
    }

    impl AsyncWrite for RecordingWriter {
        fn poll_write(
            self: Pin<&mut Self>,
            _cx: &mut Context<'_>,
            buf: &[u8],
        ) -> Poll<std::io::Result<usize>> {
            self.writes.lock().unwrap().push(buf.to_vec());
            Poll::Ready(Ok(buf.len()))
        }

        fn poll_flush(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
            Poll::Ready(Ok(()))
        }

        fn poll_shutdown(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<std::io::Result<()>> {
            Poll::Ready(Ok(()))
        }
    }

    async fn send_request(
        client: &mut tokio::io::DuplexStream,
        method: &str,
        id: u64,
        params: Value,
    ) {
        let request = JsonRpcRequest {
            jsonrpc: "2.0".into(),
            id: Some(json!(id)),
            method: method.into(),
            params: Some(params),
        };
        let payload = serde_json::to_vec(&request).unwrap();
        let frame = encode_frame(&payload).unwrap();
        client.write_all(&frame).await.unwrap();
    }

    async fn call(
        client: &mut tokio::io::DuplexStream,
        method: &str,
        id: u64,
        params: Value,
    ) -> Value {
        send_request(client, method, id, params).await;
        let response = read_frame(client).await.unwrap();
        serde_json::from_slice(&response).unwrap()
    }

    #[tokio::test]
    async fn writer_loop_batches_ready_frames() {
        let writer = RecordingWriter::default();
        let writes = writer.writes.clone();
        let (sender, receiver) = mpsc::channel(OUTBOUND_QUEUE_CAPACITY);

        sender
            .send(OutboundFrame {
                payload: b"alpha".to_vec(),
                ack: None,
            })
            .await
            .unwrap();
        sender
            .send(OutboundFrame {
                payload: b"bravo".to_vec(),
                ack: None,
            })
            .await
            .unwrap();
        drop(sender);

        writer_loop(writer, receiver).await.unwrap();

        let writes = writes.lock().unwrap();
        assert_eq!(writes.len(), 1);
        let batch = &writes[0];
        let first_len = decode_length(batch[0..4].try_into().unwrap()).unwrap() as usize;
        assert_eq!(&batch[4..4 + first_len], b"alpha");
        let second_start = 4 + first_len;
        let second_len = decode_length(batch[second_start..second_start + 4].try_into().unwrap())
            .unwrap() as usize;
        assert_eq!(
            &batch[second_start + 4..second_start + 4 + second_len],
            b"bravo"
        );
    }

    #[tokio::test]
    async fn duplex_health_and_home_dir() {
        let (mut client, server) = duplex(64 * 1024);
        let (server_read, server_write) = tokio::io::split(server);
        let server = tokio::spawn(serve_connection(
            server_read,
            server_write,
            SessionState {
                expected_token: Some("dev".to_string()),
                ..SessionState::default()
            },
        ));

        let handshake = call(
            &mut client,
            HANDSHAKE_METHOD,
            1,
            json!({
                "protocolVersion": PROTOCOL_VERSION,
                "clientName": "test",
                "authToken": "dev"
            }),
        )
        .await;
        assert_eq!(handshake["result"]["protocolVersion"], PROTOCOL_VERSION);

        let health = call(&mut client, HEALTH_METHOD, 2, json!({})).await;
        assert_eq!(health["result"]["ok"], true);

        let home = call(&mut client, "get_home_dir", 3, json!({})).await;
        assert!(home["result"].as_str().unwrap().len() > 1);

        let dir = std::env::temp_dir();
        let listing = call(
            &mut client,
            "list_directory",
            4,
            json!({ "path": dir.to_string_lossy() }),
        )
        .await;
        // One or more chunk notifications may arrive before the result.
        let mut message = listing;
        while message.get("method").and_then(Value::as_str) == Some(LIST_DIRECTORY_CHUNK) {
            message = {
                let response = read_frame(&mut client).await.unwrap();
                serde_json::from_slice(&response).unwrap()
            };
        }
        assert!(message["result"]["path"].as_str().is_some());
        assert!(message["result"]["entries"].is_array());

        let _ = call(&mut client, "ipc.shutdown", 5, json!({})).await;
        let _ = server.await;
    }

    #[tokio::test]
    async fn binary_hot_frames_emit_listing_chunks_and_result() {
        let (mut client, server) = duplex(64 * 1024);
        let (server_read, server_write) = tokio::io::split(server);
        let server = tokio::spawn(serve_connection(
            server_read,
            server_write,
            SessionState {
                expected_token: Some("dev".to_string()),
                ..SessionState::default()
            },
        ));

        let handshake = call(
            &mut client,
            HANDSHAKE_METHOD,
            1,
            json!({
                "protocolVersion": PROTOCOL_VERSION,
                "clientName": "test",
                "authToken": "dev",
                "binaryHotFrames": true
            }),
        )
        .await;
        assert_eq!(handshake["result"]["binaryHotFrames"], true);

        send_request(
            &mut client,
            "list_directory",
            2,
            json!({ "path": std::env::temp_dir().to_string_lossy() }),
        )
        .await;

        let mut saw_chunk = false;
        let mut saw_result = false;
        for _ in 0..32 {
            let frame = read_frame(&mut client).await.unwrap();
            assert!(frame.starts_with(&BINARY_FRAME_MAGIC));
            match frame.get(5).copied() {
                Some(BINARY_LIST_DIRECTORY_CHUNK) => saw_chunk = true,
                Some(BINARY_LIST_DIRECTORY_RESULT) => {
                    saw_result = true;
                    break;
                }
                tag => panic!("unexpected binary listing frame tag {tag:?}"),
            }
        }

        assert!(saw_chunk);
        assert!(saw_result);

        let _ = call(&mut client, "ipc.shutdown", 3, json!({})).await;
        let _ = server.await;
    }
}
