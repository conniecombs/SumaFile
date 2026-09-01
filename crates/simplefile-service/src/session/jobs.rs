use super::io::{
    binary_response_id, encode_json_payload, queue_payload, write_binary_response, write_json,
    OutboundSink,
};
use crate::dispatch::ProgressCopyMoveParams;
use crate::progress::OperationRegistry;
use crate::scheduler::BlockingScheduler;
use serde::Serialize;
use serde_json::{json, Value};
use simplefile_core::cleanup::{scan_disk_cleanup, scan_duplicate_check, DuplicateScanOptions};
use simplefile_core::dir_list::ListDirectoryOptions;
use simplefile_core::models::{FileChangeEvent, ProgressUpdate, SearchOptions, SearchResult};
use simplefile_ipc::rpc::{JsonRpcNotification, JsonRpcResponse};
use simplefile_ipc::{
    FILE_CHANGE, LIST_DIRECTORY_CHUNK, OPERATION_PROGRESS, SEARCH_COMPLETE, SEARCH_RESULTS_BATCH,
    UPDATE_CHUNK,
};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

#[derive(Clone)]
pub(super) struct EventSink {
    outbound: OutboundSink,
    binary_hot_frames: Arc<AtomicBool>,
}

pub(super) struct DuplicateCheckJob {
    pub(super) cancel: Arc<AtomicBool>,
    pub(super) id: Option<Value>,
    pub(super) directory: String,
    pub(super) min_size: Option<u64>,
    pub(super) partial_hash_bytes: Option<u64>,
    pub(super) operation_id: Option<String>,
}

pub(super) struct DiskCleanupJob {
    pub(super) cancel: Arc<AtomicBool>,
    pub(super) id: Option<Value>,
    pub(super) directory: String,
    pub(super) size_threshold: Option<u64>,
    pub(super) operation_id: Option<String>,
}

impl EventSink {
    pub(super) fn new(outbound: OutboundSink, binary_hot_frames: Arc<AtomicBool>) -> Self {
        Self {
            outbound,
            binary_hot_frames,
        }
    }

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

    pub(super) fn emit_file_change(&self, change: &FileChangeEvent) {
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

fn json_result_response<T: Serialize>(
    id: Option<Value>,
    result: T,
    serialize_error: &str,
) -> JsonRpcResponse {
    match serde_json::to_value(result) {
        Ok(value) => JsonRpcResponse::result(id, value),
        Err(error) => JsonRpcResponse::application_error(id, format!("{serialize_error}: {error}")),
    }
}

fn scheduled_result_response<T: Serialize>(
    id: Option<Value>,
    result: Result<Result<T, String>, String>,
    task_label: &str,
    serialize_error: &str,
) -> JsonRpcResponse {
    match result {
        Ok(Ok(value)) => json_result_response(id, value, serialize_error),
        Ok(Err(message)) => JsonRpcResponse::application_error(id, message),
        Err(error) => {
            JsonRpcResponse::application_error(id, format!("{task_label} failed: {error}"))
        }
    }
}

pub(super) fn spawn_install_update(
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

        let response = scheduled_result_response(
            id,
            result,
            "update task",
            "failed to serialize update result",
        );
        let _ = write_json(&writer, &response).await;
    });
}

pub(super) fn spawn_folder_size(
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

pub(super) fn spawn_folder_item_count(
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

pub(super) fn spawn_folder_metrics(
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

pub(super) async fn list_directory_and_reply(
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

pub(super) fn spawn_list_directory(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    id: Option<Value>,
    path: String,
    options: Option<ListDirectoryOptions>,
) {
    tokio::spawn(async move {
        let response_id = id.clone();
        let result =
            list_directory_and_reply(&writer, scheduler, binary_hot_frames, id, path, options)
                .await;

        if let Err(message) = result {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::application_error(
                    response_id,
                    format!("listing task failed: {message}"),
                ),
            )
            .await;
        }
    });
}

pub(super) async fn generate_thumbnail_and_reply(
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

pub(super) async fn generate_thumbnails_and_reply(
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

pub(super) fn spawn_copy_move_with_progress(
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

        let response = scheduled_result_response(
            id,
            result,
            "transfer task",
            "failed to serialize transfer result",
        );

        let _ = write_json(&writer, &response).await;
        registry.remove(&op_id).await;
    });
}

pub(super) fn spawn_search_files(
    writer: OutboundSink,
    registry: std::sync::Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    id: Option<Value>,
    options: SearchOptions,
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
                    json_result_response(id, results, "failed to serialize search result")
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

pub(super) fn spawn_duplicate_check(
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

        let response = scheduled_result_response(
            id,
            result,
            "duplicate check task",
            "failed to serialize duplicate check result",
        );
        let _ = write_json(&writer, &response).await;
    });
}

pub(super) fn spawn_disk_cleanup(
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

        let response = scheduled_result_response(
            id,
            result,
            "disk cleanup task",
            "failed to serialize cleanup result",
        );
        let _ = write_json(&writer, &response).await;
    });
}
