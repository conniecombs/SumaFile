use super::io::{
    binary_response_id, encode_json_payload, queue_payload, write_binary_response, write_json,
    OutboundSink,
};
use crate::dispatch::ProgressCopyMoveParams;
use crate::progress::OperationRegistry;
use crate::scheduler::{BlockingScheduler, MediaLane};
use crate::session_cache::SessionCache;
use serde::Serialize;
use serde_json::{json, Value};
use simplefile_core::cleanup::{scan_disk_cleanup, scan_duplicate_check, DuplicateScanOptions};
use simplefile_core::dir_list::ListDirectoryOptions;
use simplefile_core::models::{
    FileChangeEvent, ListingViewDelta, ProgressUpdate, SearchOptions, SearchResult, ThumbnailResult,
};
use simplefile_ipc::rpc::{JsonRpcNotification, JsonRpcResponse};
use simplefile_ipc::{
    FILE_CHANGE, LISTING_VIEW_CHANGED, LIST_DIRECTORY_CHUNK, OPERATION_PROGRESS, SEARCH_COMPLETE,
    SEARCH_RESULTS_BATCH, UPDATE_CHUNK,
};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

#[derive(Clone)]
pub(super) struct EventSink {
    outbound: OutboundSink,
    binary_hot_frames: Arc<AtomicBool>,
}

pub(super) struct DuplicateCheckJob {
    pub(super) id: Option<Value>,
    pub(super) directory: String,
    pub(super) min_size: Option<u64>,
    pub(super) partial_hash_bytes: Option<u64>,
    pub(super) max_depth: Option<usize>,
    pub(super) exclude_patterns: Vec<String>,
    pub(super) network_mode: Option<bool>,
    /// Registered by the session loop before the job is spawned, so a cancel
    /// that arrives right after the start request finds the token.
    pub(super) operation_id: String,
    pub(super) cancel: Arc<AtomicBool>,
}

pub(super) struct DiskCleanupJob {
    pub(super) id: Option<Value>,
    pub(super) directory: String,
    pub(super) size_threshold: Option<u64>,
    pub(super) operation_id: String,
    pub(super) cancel: Arc<AtomicBool>,
}

pub(super) struct SearchJob {
    pub(super) id: Option<Value>,
    pub(super) options: SearchOptions,
    /// Registry key: the client's search id, or a generated one so the search
    /// is still cancellable on disconnect.
    pub(super) registry_id: String,
    pub(super) cancel: Arc<AtomicBool>,
}

pub(super) struct CopyMoveJob {
    pub(super) id: Option<Value>,
    pub(super) params: ProgressCopyMoveParams,
    pub(super) op_id: String,
    pub(super) cancel: Arc<AtomicBool>,
    pub(super) is_copy: bool,
}

pub(super) struct ArchiveOperationJob {
    pub(super) id: Option<Value>,
    pub(super) operation_id: String,
    pub(super) operation: crate::dispatch::ArchiveOperation,
    pub(super) cancel: Arc<AtomicBool>,
}

/// Run a tracked `extract_archive` or `create_archive` on the Transfer lane.
/// It emits `operation-progress`: `running` (0/0) first, then one event per
/// engine progress (engine bytes, no file counts), then `completed`,
/// `cancelled` or `error`. The reply is `null`, or the cancelled message
/// after a cancel.
pub(super) fn spawn_archive_operation(
    writer: OutboundSink,
    registry: Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    cache: SessionCache,
    job: ArchiveOperationJob,
) {
    use crate::dispatch::ArchiveOperation;
    use simplefile_core::archive::{EngineContext, EngineProgress, CANCELLED_MESSAGE};

    let ArchiveOperationJob {
        id,
        operation_id,
        operation,
        cancel,
    } = job;
    tokio::spawn(async move {
        let (operation_type, item, invalidate) = match &operation {
            ArchiveOperation::Extract {
                archive_path,
                destination,
            } => ("extract-archive", archive_path.clone(), destination.clone()),
            ArchiveOperation::Create { archive_path, .. } => {
                ("create-archive", archive_path.clone(), archive_path.clone())
            }
        };
        let update = |status: &str, current, total, current_item: String, error| ProgressUpdate {
            operation_id: operation_id.clone(),
            operation_type: operation_type.to_string(),
            current,
            total,
            current_files: 0,
            total_files: 0,
            current_item,
            status: status.to_string(),
            error,
            transferred: 0,
        };
        events
            .emit_progress_async(&update("running", 0, 0, item.clone(), None))
            .await;

        let last = Arc::new(std::sync::Mutex::new((0u64, 0u64)));
        let task_last = Arc::clone(&last);
        let task_events = events.clone();
        let task_cancel = Arc::clone(&cancel);
        let task_update = {
            let operation_id = operation_id.clone();
            let item = item.clone();
            move |progress: EngineProgress| ProgressUpdate {
                operation_id: operation_id.clone(),
                operation_type: operation_type.to_string(),
                current: progress.current,
                total: progress.total,
                current_files: 0,
                total_files: 0,
                current_item: progress.current_item.unwrap_or_else(|| item.clone()),
                status: "running".to_string(),
                error: None,
                transferred: 0,
            }
        };
        let task_operation_id = operation_id.clone();
        let result = scheduler
            .run_transfer(move || {
                let progress = |progress: EngineProgress| {
                    if let Ok(mut last) = task_last.lock() {
                        *last = (progress.current, progress.total);
                    }
                    task_events.emit_progress(&task_update(progress));
                };
                let ctx = EngineContext {
                    operation_id: Some(&task_operation_id),
                    cancel: &task_cancel,
                    progress: &progress,
                };
                match operation {
                    ArchiveOperation::Extract {
                        archive_path,
                        destination,
                    } => simplefile_core::archive::extract_archive_with(
                        archive_path,
                        destination,
                        &ctx,
                    ),
                    ArchiveOperation::Create {
                        paths,
                        archive_path,
                        format,
                    } => simplefile_core::archive::create_archive_with(
                        paths,
                        archive_path,
                        format,
                        &ctx,
                    ),
                }
            })
            .await;
        cache.invalidate_path(&invalidate);

        let (current, total) = last.lock().map(|last| *last).unwrap_or((0, 0));
        let cancelled = cancel.load(Ordering::SeqCst);
        let (final_update, response) = match result {
            Ok(Ok(())) => (
                update("completed", total.max(current), total, item, None),
                JsonRpcResponse::result(id, Value::Null),
            ),
            Ok(Err(message)) if cancelled || message == CANCELLED_MESSAGE => (
                update("cancelled", current, total, item, None),
                JsonRpcResponse::application_error(id, CANCELLED_MESSAGE.to_string()),
            ),
            Ok(Err(message)) => (
                update("error", current, total, item, Some(message.clone())),
                JsonRpcResponse::application_error(id, message),
            ),
            Err(error) => {
                let message = format!("archive task failed: {error}");
                (
                    update("error", current, total, item, Some(message.clone())),
                    JsonRpcResponse::application_error(id, message),
                )
            }
        };
        events.emit_progress_async(&final_update).await;
        let _ = write_json(&writer, &response).await;
        registry.remove_token(&operation_id, &cancel).await;
    });
}

/// Run a handler body on the blocking scheduler and write its reply. Used for
/// every request that touches the disk, the network or a child process, so
/// the session loop keeps reading frames (including cancels) meanwhile.
pub(super) fn spawn_blocking_reply(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    id: Option<Value>,
    job: crate::dispatch::BlockingJob,
) {
    tokio::spawn(async move {
        let lane = job.lane();
        let task = move || job.run();
        let result = match lane {
            crate::dispatch::BlockingLane::General => scheduler.run_general(task).await,
            crate::dispatch::BlockingLane::Transfer => scheduler.run_transfer(task).await,
        };
        let response = match result {
            Ok(Ok(value)) => JsonRpcResponse::result(id, value),
            Ok(Err(message)) => JsonRpcResponse::application_error(id, message),
            Err(error) => {
                JsonRpcResponse::application_error(id, format!("blocking task failed: {error}"))
            }
        };
        let _ = write_json(&writer, &response).await;
    });
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
        if let Some(payload) = self.progress_payload(update) {
            let _ = self.outbound.send_payload_blocking(payload);
        }
    }

    /// Progress emit for async (tokio task) context. `emit_progress` uses a
    /// blocking channel send that panics when called on a runtime worker.
    async fn emit_progress_async(&self, update: &ProgressUpdate) {
        if let Some(payload) = self.progress_payload(update) {
            let _ = self.outbound.enqueue_payload(payload).await;
        }
    }

    fn progress_payload(&self, update: &ProgressUpdate) -> Option<Vec<u8>> {
        if self.binary_hot_frames.load(Ordering::Relaxed) {
            if let Ok(payload) = crate::binary::encode_progress_update(update) {
                return Some(payload);
            }
        }
        let params = serde_json::to_value(update).ok()?;
        encode_json_payload(&JsonRpcNotification::new(OPERATION_PROGRESS, params)).ok()
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

    /// A listing view delta, from a plain thread (the watcher's).
    pub(super) fn emit_listing_view_delta(&self, delta: &ListingViewDelta) {
        self.emit(LISTING_VIEW_CHANGED, delta);
    }

    /// A listing view delta, from a runtime task: [`Self::emit`] sends with
    /// a blocking call that panics on a runtime worker.
    pub(super) async fn emit_listing_view_delta_async(&self, delta: &ListingViewDelta) {
        if let Ok(params) = serde_json::to_value(delta) {
            let notification = JsonRpcNotification::new(LISTING_VIEW_CHANGED, params);
            if let Ok(payload) = encode_json_payload(&notification) {
                let _ = self.outbound.enqueue_payload(payload).await;
            }
        }
    }

    fn emit_search_results_batch(&self, search_id: Option<&str>, batch: &[SearchResult]) {
        if self.binary_hot_frames.load(Ordering::Relaxed) {
            if let Ok(payload) = crate::binary::encode_search_results_batch(search_id, batch) {
                let _ = self.outbound.send_payload_blocking(payload);
                return;
            }
        }
        self.emit(
            SEARCH_RESULTS_BATCH,
            &json!({
                "search_id": search_id,
                "results": batch,
            }),
        );
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

/// Keep large merge receipts below the pipe frame limit. The terminal response
/// retains an explicit changes object even when every path travelled in chunks.
async fn stream_transfer_changes(
    writer: &OutboundSink,
    operation_id: &str,
    results: &mut [crate::progress::TransferResult],
) -> Result<(), String> {
    let path_bytes: usize = results
        .iter()
        .filter_map(|item| item.changes.as_ref())
        .flat_map(|changes| {
            changes
                .files
                .iter()
                .chain(&changes.created_dirs)
                .chain(&changes.removed_source_dirs)
        })
        .map(|path| path.len() + 4)
        .sum();
    if path_bytes < 4 * 1024 * 1024 {
        return Ok(());
    }
    for (index, item) in results.iter_mut().enumerate() {
        let Some(changes) = &mut item.changes else {
            continue;
        };
        for (field, paths) in [
            ("files", &mut changes.files),
            ("created_dirs", &mut changes.created_dirs),
            ("removed_source_dirs", &mut changes.removed_source_dirs),
        ] {
            for chunk in paths.chunks(128) {
                let params = json!({"operation_id": operation_id, "result_index": index, "field": field, "paths": chunk});
                write_json(
                    writer,
                    &JsonRpcNotification::new(simplefile_ipc::TRANSFER_CHANGES, params),
                )
                .await?;
            }
            changes.streamed_paths += paths.len();
            paths.clear();
        }
    }
    Ok(())
}

fn transfer_result_response(
    id: Option<Value>,
    result: Result<
        Result<Vec<crate::progress::TransferResult>, crate::progress::TransferBatchError>,
        String,
    >,
) -> JsonRpcResponse {
    match result {
        Ok(Ok(results)) => json_result_response(id, results, "failed to serialize transfer result"),
        Ok(Err(error)) => {
            JsonRpcResponse::application_error_with_data(id, error.message, json!(error.outcome))
        }
        Err(error) => {
            JsonRpcResponse::application_error(id, format!("transfer task failed: {error}"))
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
    cache: SessionCache,
    id: Option<Value>,
    path: String,
    cancel: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        if cancel.load(Ordering::Relaxed) {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::application_error(id, "cancelled".to_string()),
            )
            .await;
            return;
        }
        if let Some(metrics) = cache.get_folder_metrics(&path) {
            let _ = write_json(&writer, &JsonRpcResponse::result(id, json!(metrics.size))).await;
            return;
        }

        let path_for_task = path.clone();
        let result = scheduler
            .run_background(move || {
                simplefile_core::file_ops::get_folder_metrics(&path_for_task, &cancel)
            })
            .await;

        let response = match result {
            Ok(Some(metrics)) => {
                let size = metrics.size;
                cache.put_subdirectories(&path, metrics.subdirectories.clone());
                cache.put_folder_metrics(&path, metrics);
                JsonRpcResponse::result(id, json!(size))
            }
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
    cache: SessionCache,
    id: Option<Value>,
    path: String,
    cancel: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        if cancel.load(Ordering::Relaxed) {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::application_error(id, "cancelled".to_string()),
            )
            .await;
            return;
        }
        if let Some(metrics) = cache.get_folder_metrics(&path) {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::result(id, json!(metrics.item_count)),
            )
            .await;
            return;
        }
        if let Some(listing) = cache.get_directory_listing(&path) {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::result(id, json!(listing.entries.len() as u64)),
            )
            .await;
            return;
        }

        let result = scheduler
            .run_background(move || simplefile_core::file_ops::count_folder_items(&path, &cancel))
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
    cache: SessionCache,
    id: Option<Value>,
    path: String,
    cancel: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        if cancel.load(Ordering::Relaxed) {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::application_error(id, "cancelled".to_string()),
            )
            .await;
            return;
        }
        if let Some(metrics) = cache.get_folder_metrics(&path) {
            let response =
                json_result_response(id, metrics, "failed to serialize folder metrics result");
            let _ = write_json(&writer, &response).await;
            return;
        }

        let path_for_task = path.clone();
        let result = scheduler
            .run_background(move || {
                simplefile_core::file_ops::get_folder_metrics(&path_for_task, &cancel)
            })
            .await;

        let response = match result {
            Ok(Some(metrics)) => {
                cache.put_subdirectories(&path, metrics.subdirectories.clone());
                cache.put_folder_metrics(&path, metrics.clone());
                json_result_response(id, metrics, "failed to serialize folder metrics result")
            }
            Ok(None) => JsonRpcResponse::application_error(id, "cancelled".to_string()),
            Err(error) => JsonRpcResponse::application_error(
                id,
                format!("folder metrics task failed: {error}"),
            ),
        };
        let _ = write_json(&writer, &response).await;
    });
}

pub(super) async fn list_directory_and_reply(
    writer: &OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    cache: SessionCache,
    id: Option<Value>,
    path: String,
    options: Option<ListDirectoryOptions>,
) -> Result<(), String> {
    let start = std::time::Instant::now();
    let binary_request_id = binary_response_id(&binary_hot_frames, &id);
    // Backpressure keeps a fast enumeration from queuing the entire directory
    // while a slow client is still rendering the first pages.
    let (tx, mut rx) = tokio::sync::mpsc::channel(4);
    let cached_raw = cache.get_directory_listing(&path);
    let path_for_cache = path.clone();
    let cache_for_task = cache.clone();

    let join = tokio::spawn(async move {
        scheduler
            .run_foreground(move || {
                if let Some(raw_listing) = cached_raw {
                    simplefile_core::dir_list::emit_cached_directory_listing(
                        &raw_listing,
                        options,
                        |chunk| tx.blocking_send(chunk).map_err(|error| error.to_string()),
                    )
                } else {
                    let result = simplefile_core::dir_list::list_directory_capturing_raw(
                        path,
                        options,
                        |chunk| tx.blocking_send(chunk).map_err(|error| error.to_string()),
                    )?;
                    let (listing, maybe_raw) = result;
                    if let Some(raw_listing) = maybe_raw {
                        cache_for_task
                            .put_directory_listing(&raw_listing.path, raw_listing.clone());
                        if !raw_listing.path.eq_ignore_ascii_case(&path_for_cache) {
                            cache_for_task.put_directory_listing(&path_for_cache, raw_listing);
                        }
                    }
                    Ok(listing)
                }
            })
            .await
    });

    while let Some(chunk) = rx.recv().await {
        if let Some(request_id) = binary_request_id {
            let payload = crate::binary::encode_directory_listing_chunk(request_id, &chunk)?;
            // Flush each small batch before allowing enumeration to run ahead.
            if chunk.chunk_index % 4 == 0 || chunk.done {
                writer.write_payload(payload).await?;
            } else {
                queue_payload(writer, &payload).await?;
            }
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
            let run_ms = start.elapsed().as_secs_f64() * 1000.0;
            if let Some(request_id) = binary_request_id {
                let encode_start = std::time::Instant::now();
                let payload = crate::binary::encode_directory_listing_result(request_id, &listing)?;
                let encode_ms = encode_start.elapsed().as_secs_f64() * 1000.0;
                log::debug!(
                    "job.timing method=ListDirectory run_ms={:.2} encode_ms={:.2}",
                    run_ms,
                    encode_ms
                );
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
    cache: SessionCache,
    id: Option<Value>,
    path: String,
    options: Option<ListDirectoryOptions>,
) {
    tokio::spawn(async move {
        let response_id = id.clone();
        let result = list_directory_and_reply(
            &writer,
            scheduler,
            binary_hot_frames,
            cache,
            id,
            path,
            options,
        )
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

/// Where a thumbnail job waits.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ThumbnailLane {
    /// Made here, on a core: the background lane.
    Background,
    /// Made by the media process: its own lane, so a slow file there never
    /// holds a permit the built-in formats need.
    Media,
    /// A video's, made by the video process: a lane of its own again.
    Video,
}

/// The media lane when every path is of a format the media process decodes,
/// the video lane when every path is a video. A batch that mixes kinds stays
/// on the background lane.
fn thumbnail_lane(paths: &[String]) -> ThumbnailLane {
    let all =
        |is_kind: fn(&str) -> bool| !paths.is_empty() && paths.iter().all(|path| is_kind(path));
    if all(simplefile_core::media::thumbnail_is_video) {
        ThumbnailLane::Video
    } else if all(simplefile_core::media::thumbnail_uses_media_process) {
        ThumbnailLane::Media
    } else {
        ThumbnailLane::Background
    }
}

/// What one `generate_thumbnail` request asks for.
pub(super) struct ThumbnailRequest {
    pub(super) path: String,
    pub(super) size: Option<u32>,
    /// For a video: how far into it the frame is, in percent.
    pub(super) position_percent: Option<u8>,
}

pub(super) async fn generate_thumbnail_and_reply(
    writer: &OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    cache: SessionCache,
    id: Option<Value>,
    request: ThumbnailRequest,
) -> Result<(), String> {
    let ThumbnailRequest {
        path,
        size,
        position_percent,
    } = request;
    let start = std::time::Instant::now();
    let lane = thumbnail_lane(std::slice::from_ref(&path));
    // Only a video has a frame to choose: a picture is one thumbnail
    // whatever position the request names.
    let position = position_percent.filter(|_| lane == ThumbnailLane::Video);
    let result: Result<Result<Vec<u8>, String>, String> =
        if let Some(cached) = cache.get_thumbnail_at(&path, size, position) {
            Ok(Ok(cached))
        } else {
            let path_for_task = path.clone();
            let cache_for_task = cache.clone();
            let render = move || {
                let bytes = simplefile_core::preview::generate_thumbnail_at(
                    path_for_task.clone(),
                    size,
                    position,
                )?;
                cache_for_task.put_thumbnail_at(&path_for_task, size, position, bytes.clone());
                Ok(bytes)
            };
            match lane {
                ThumbnailLane::Video => scheduler.run_media(MediaLane::Video, render).await,
                ThumbnailLane::Media => scheduler.run_media(MediaLane::Thumbnail, render).await,
                ThumbnailLane::Background => scheduler.run_background(render).await,
            }
        };
    let run_ms = start.elapsed().as_secs_f64() * 1000.0;

    match result {
        Ok(Ok(result)) => {
            if let Some(request_id) = binary_response_id(&binary_hot_frames, &id) {
                let encode_start = std::time::Instant::now();
                let payload = crate::binary::encode_thumbnail_result(request_id, &result)?;
                let encode_ms = encode_start.elapsed().as_secs_f64() * 1000.0;
                log::debug!(
                    "job.timing method=GenerateThumbnail run_ms={:.2} encode_ms={:.2}",
                    run_ms,
                    encode_ms
                );
                write_binary_response(writer, id, &payload).await
            } else {
                use base64::{engine::general_purpose, Engine as _};
                let b64 = general_purpose::STANDARD.encode(&result);
                write_json(writer, &JsonRpcResponse::result(id, json!(b64))).await
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

pub(super) fn spawn_generate_thumbnail(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    cache: SessionCache,
    id: Option<Value>,
    request: ThumbnailRequest,
) {
    tokio::spawn(async move {
        let response_id = id.clone();
        let result =
            generate_thumbnail_and_reply(&writer, scheduler, binary_hot_frames, cache, id, request)
                .await;

        if let Err(message) = result {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::application_error(
                    response_id,
                    format!("thumbnail task failed: {message}"),
                ),
            )
            .await;
        }
    });
}

/// A picture for the preview pane, from the media process. It waits in the
/// media lane's preview permits: not behind a folder's thumbnails, and not
/// as foreground work, which would make every background job pause while
/// the media process decodes.
pub(super) fn spawn_render_image_preview(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    id: Option<Value>,
    path: String,
    max_dimension: Option<u32>,
) {
    tokio::spawn(async move {
        let rendered = scheduler
            .run_media(MediaLane::Preview, move || {
                simplefile_core::media::render_image_preview(path, max_dimension)
            })
            .await;
        let written = match rendered {
            Ok(Ok(preview)) => {
                if let Some(request_id) = binary_response_id(&binary_hot_frames, &id) {
                    match crate::binary::encode_image_preview_result(request_id, &preview) {
                        Ok(payload) => write_binary_response(&writer, id, &payload).await,
                        Err(message) => {
                            write_json(&writer, &JsonRpcResponse::application_error(id, message))
                                .await
                        }
                    }
                } else {
                    use base64::{engine::general_purpose, Engine as _};
                    let result = json!({
                        "format": preview.format,
                        "width": preview.width,
                        "height": preview.height,
                        "data": general_purpose::STANDARD.encode(&preview.data),
                    });
                    write_json(&writer, &JsonRpcResponse::result(id, result)).await
                }
            }
            Ok(Err(message)) => {
                write_json(&writer, &JsonRpcResponse::application_error(id, message)).await
            }
            Err(message) => {
                write_json(
                    &writer,
                    &JsonRpcResponse::application_error(
                        id,
                        format!("image preview task failed: {message}"),
                    ),
                )
                .await
            }
        };
        // The connection is gone when a reply cannot be written.
        let _ = written;
    });
}

pub(super) async fn generate_thumbnails_and_reply(
    writer: &OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    cache: SessionCache,
    id: Option<Value>,
    paths: Vec<String>,
    size: Option<u32>,
) -> Result<(), String> {
    let start = std::time::Instant::now();
    let scheduler_for_task = scheduler.clone();
    let lane = thumbnail_lane(&paths);
    let render = move || {
        let mut results = Vec::with_capacity(paths.len());
        for path in paths {
            if let Some(cached) = cache.get_thumbnail(&path, size) {
                results.push(ThumbnailResult {
                    path,
                    data: Some(cached),
                    error: None,
                });
                continue;
            }
            scheduler_for_task.yield_if_foreground_active_blocking();
            match simplefile_core::preview::generate_thumbnail(path.clone(), size) {
                Ok(data) => {
                    cache.put_thumbnail(&path, size, data.clone());
                    results.push(ThumbnailResult {
                        path,
                        data: Some(data),
                        error: None,
                    });
                }
                Err(error) => {
                    results.push(ThumbnailResult {
                        path,
                        data: None,
                        error: Some(error),
                    });
                }
            }
        }
        results
    };
    let result = match lane {
        ThumbnailLane::Video => scheduler.run_media(MediaLane::Video, render).await,
        ThumbnailLane::Media => scheduler.run_media(MediaLane::Thumbnail, render).await,
        ThumbnailLane::Background => scheduler.run_background(render).await,
    };
    let run_ms = start.elapsed().as_secs_f64() * 1000.0;

    match result {
        Ok(results) => {
            if let Some(request_id) = binary_response_id(&binary_hot_frames, &id) {
                let encode_start = std::time::Instant::now();
                let payload = crate::binary::encode_thumbnail_results_result(request_id, &results)?;
                let encode_ms = encode_start.elapsed().as_secs_f64() * 1000.0;
                log::debug!(
                    "job.timing method=GenerateThumbnails run_ms={:.2} encode_ms={:.2}",
                    run_ms,
                    encode_ms
                );
                write_binary_response(writer, id, &payload).await
            } else {
                use base64::{engine::general_purpose, Engine as _};
                let mut json_results = Vec::new();
                for r in results {
                    json_results.push(json!({
                        "path": r.path,
                        "data": r.data.as_ref().map(|d| general_purpose::STANDARD.encode(d)),
                        "error": r.error
                    }));
                }
                let result = serde_json::to_value(json_results).unwrap_or(Value::Null);
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

pub(super) fn spawn_generate_thumbnails(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    binary_hot_frames: Arc<AtomicBool>,
    cache: SessionCache,
    id: Option<Value>,
    paths: Vec<String>,
    size: Option<u32>,
) {
    tokio::spawn(async move {
        let response_id = id.clone();
        let result = generate_thumbnails_and_reply(
            &writer,
            scheduler,
            binary_hot_frames,
            cache,
            id,
            paths,
            size,
        )
        .await;

        if let Err(message) = result {
            let _ = write_json(
                &writer,
                &JsonRpcResponse::application_error(
                    response_id,
                    format!("thumbnail batch task failed: {message}"),
                ),
            )
            .await;
        }
    });
}

pub(super) fn spawn_copy_move_with_progress(
    writer: OutboundSink,
    registry: std::sync::Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    cache: SessionCache,
    job: CopyMoveJob,
) {
    tokio::spawn(async move {
        let CopyMoveJob {
            id,
            params,
            op_id,
            cancel,
            is_copy,
        } = job;
        let sources = params.sources;
        let destination = params.destination;
        let conflict_action = params.conflict_action;
        let operation_type = if is_copy { "copy" } else { "move" };
        let events_for_task = events.clone();
        let op_id_for_task = op_id.clone();
        let sources_for_cache = sources.clone();
        let destination_for_cache = destination.clone();
        let cancel_for_task = cancel.clone();

        let items = params.items;
        let mut result = scheduler
            .run_transfer(move || {
                let emit = |update| events_for_task.emit_progress(&update);
                let tuning = |update| events_for_task.emit("transfer-tuning", &update);
                match items {
                    Some(items) => crate::progress::transfer_items_with_tuning_blocking(
                        operation_type,
                        items
                            .into_iter()
                            .map(|item| crate::progress::TransferItem {
                                source: item.source,
                                destination: item.destination,
                                action: item.action,
                            })
                            .collect(),
                        destination,
                        op_id_for_task,
                        cancel_for_task.clone(),
                        &emit,
                        &tuning,
                    ),
                    None => crate::progress::transfer_with_tuning_blocking(
                        operation_type,
                        sources,
                        destination,
                        op_id_for_task,
                        conflict_action,
                        cancel_for_task.clone(),
                        &emit,
                        &tuning,
                    ),
                }
            })
            .await;

        // Invalidating the destination and (for moves) the source roots
        // already drops every cached subtree and ancestor metric. Walking
        // each transferred item again was O(items x cache) on the runtime.
        cache.invalidate_path(&destination_for_cache);
        if !is_copy {
            cache.invalidate_paths(&sources_for_cache);
        }

        let committed = match &mut result {
            Ok(Ok(results)) => Some(results),
            Ok(Err(error)) => Some(&mut error.outcome.committed),
            Err(_) => None,
        };
        if let Some(committed) = committed {
            if !is_copy {
                // Labels follow what was moved, also when the move stopped part-way.
                let moved: Vec<(String, String)> = committed
                    .iter()
                    .flat_map(crate::progress::TransferResult::moved_paths)
                    .collect();
                if !moved.is_empty() {
                    let _ = tokio::task::spawn_blocking(move || {
                        crate::dispatch::carry_labels(&moved);
                    })
                    .await;
                }
            }
            if let Err(error) = stream_transfer_changes(&writer, &op_id, committed).await {
                result = Err(error);
            }
        }
        let response = transfer_result_response(id, result);

        let _ = write_json(&writer, &response).await;
        registry.remove_token(&op_id, &cancel).await;
    });
}

pub(super) fn spawn_search_files(
    writer: OutboundSink,
    registry: std::sync::Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    job: SearchJob,
) {
    let SearchJob {
        id,
        options,
        registry_id,
        cancel,
    } = job;
    tokio::spawn(async move {
        let start = std::time::Instant::now();
        let binary_request_id = binary_response_id(&events.binary_hot_frames, &id);
        let search_id = options.search_id.clone();
        let cancel_for_task = cancel.clone();

        let events_for_task = events.clone();
        let search_id_for_task = search_id.clone();
        let result = scheduler
            .run_foreground(move || {
                let emit_batch = |batch: Vec<SearchResult>| {
                    events_for_task.emit_search_results_batch(search_id_for_task.as_deref(), &batch)
                };
                let result =
                    crate::search::search_files_blocking(options, cancel_for_task, &emit_batch);
                if let Ok(results) = &result {
                    events_for_task.emit(
                        SEARCH_COMPLETE,
                        &json!({
                            "search_id": search_id_for_task,
                            "count": results.len(),
                        }),
                    );
                }
                result
            })
            .await;

        let run_ms = start.elapsed().as_secs_f64() * 1000.0;
        log::debug!("job.timing method=SearchFiles run_ms={:.2}", run_ms);

        let response = match result {
            Ok(Ok(results)) => {
                if let Some(request_id) = binary_request_id {
                    match crate::binary::encode_search_results_result(request_id, &results) {
                        Ok(payload) => {
                            let _ = write_binary_response(&writer, id.clone(), &payload).await;
                            registry.remove_token(&registry_id, &cancel).await;
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
        registry.remove_token(&registry_id, &cancel).await;
    });
}

pub(super) fn spawn_duplicate_check(
    writer: OutboundSink,
    registry: std::sync::Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    job: DuplicateCheckJob,
) {
    let DuplicateCheckJob {
        id,
        directory,
        min_size,
        partial_hash_bytes,
        max_depth,
        exclude_patterns,
        network_mode,
        operation_id,
        cancel,
    } = job;
    tokio::spawn(async move {
        let cancel_for_task = cancel.clone();
        let events_for_task = events.clone();
        let operation_id_for_task = operation_id.clone();
        let queued_directory = directory.clone();
        events
            .emit_progress_async(&ProgressUpdate {
                operation_id: operation_id.clone(),
                operation_type: "duplicate-check".to_string(),
                current: 0,
                total: 0,
                current_files: 0,
                total_files: 0,
                current_item: queued_directory,
                status: "queued".to_string(),
                error: None,
                transferred: 0,
            })
            .await;
        let start = std::time::Instant::now();
        let scheduler_for_task = scheduler.clone();
        let result = scheduler
            .run_background(move || {
                let emit = |current, total, item: &str| {
                    scheduler_for_task.yield_if_foreground_active_blocking();
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
                        transferred: 0,
                    });
                };
                scan_duplicate_check(
                    &directory,
                    DuplicateScanOptions::from_params(
                        min_size,
                        partial_hash_bytes,
                        max_depth,
                        exclude_patterns,
                        network_mode,
                    ),
                    &cancel_for_task,
                    emit,
                )
            })
            .await;
        let run_ms = start.elapsed().as_secs_f64() * 1000.0;
        log::debug!("job.timing method=DuplicateCheck total_ms={run_ms:.2}");

        let response = scheduled_result_response(
            id,
            result,
            "duplicate check task",
            "failed to serialize duplicate check result",
        );
        let _ = write_json(&writer, &response).await;
        registry.remove_token(&operation_id, &cancel).await;
    });
}

pub(super) fn spawn_disk_cleanup(
    writer: OutboundSink,
    registry: std::sync::Arc<OperationRegistry>,
    scheduler: BlockingScheduler,
    events: EventSink,
    job: DiskCleanupJob,
) {
    let DiskCleanupJob {
        id,
        directory,
        size_threshold,
        operation_id,
        cancel,
    } = job;
    tokio::spawn(async move {
        let cancel_for_task = cancel.clone();
        let events_for_task = events.clone();
        let operation_id_for_task = operation_id.clone();
        let queued_directory = directory.clone();
        events
            .emit_progress_async(&ProgressUpdate {
                operation_id: operation_id.clone(),
                operation_type: "cleanup".to_string(),
                current: 0,
                total: 0,
                current_files: 0,
                total_files: 0,
                current_item: queued_directory,
                status: "queued".to_string(),
                error: None,
                transferred: 0,
            })
            .await;
        let scheduler_for_task = scheduler.clone();
        let result = scheduler
            .run_background(move || {
                let emit = |current, total, item: &str| {
                    scheduler_for_task.yield_if_foreground_active_blocking();
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
                        transferred: 0,
                    });
                };
                scan_disk_cleanup(&directory, size_threshold, &cancel_for_task, emit)
            })
            .await;

        let response = scheduled_result_response(
            id,
            result,
            "disk cleanup task",
            "failed to serialize cleanup result",
        );
        let _ = write_json(&writer, &response).await;
        registry.remove_token(&operation_id, &cancel).await;
    });
}

#[cfg(test)]
mod tests {
    use super::{thumbnail_lane, ThumbnailLane};

    fn lane(paths: &[&str]) -> ThumbnailLane {
        thumbnail_lane(
            &paths
                .iter()
                .map(|path| (*path).to_string())
                .collect::<Vec<_>>(),
        )
    }

    #[test]
    fn a_thumbnail_the_media_process_makes_waits_in_the_media_lane() {
        assert_eq!(lane(&[r"C:\Art\cover.psd"]), ThumbnailLane::Media);
        assert_eq!(
            lane(&[r"C:\Raw\a.CR2", r"C:\Raw\b.nef"]),
            ThumbnailLane::Media
        );
    }

    #[test]
    fn a_video_waits_in_the_video_lane() {
        assert_eq!(lane(&[r"C:\Films\a.mp4"]), ThumbnailLane::Video);
        assert_eq!(
            lane(&[r"C:\Films\a.MKV", r"C:\Films\b.webm"]),
            ThumbnailLane::Video
        );
    }

    #[test]
    fn a_batch_of_videos_and_pictures_stays_in_the_background_lane() {
        assert_eq!(
            lane(&[r"C:\Films\a.mp4", r"C:\Photos\a.jpg"]),
            ThumbnailLane::Background
        );
        assert_eq!(
            lane(&[r"C:\Films\a.mp4", r"C:\Art\cover.psd"]),
            ThumbnailLane::Background
        );
    }

    #[test]
    fn built_in_formats_and_mixed_batches_stay_in_the_background_lane() {
        assert_eq!(lane(&[r"C:\Photos\a.jpg"]), ThumbnailLane::Background);
        assert_eq!(
            lane(&[r"C:\Art\cover.psd", r"C:\Photos\a.jpg"]),
            ThumbnailLane::Background
        );
        assert_eq!(lane(&[]), ThumbnailLane::Background);
    }
}
