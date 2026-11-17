//! The session's listing view requests: open, page, refresh and close, and
//! the watcher's file changes applied to the open views.

use super::io::{
    binary_response_id, queue_payload, write_binary_response, write_json, OutboundSink,
};
use super::jobs::EventSink;
use crate::dispatch::OpenListingViewParams;
use crate::listing_views::{ListingViews, OpenTicket};
use crate::scheduler::BlockingScheduler;
use crate::session_cache::{ListingFreshness, SessionCache};
use serde_json::Value;
use simplefile_core::listing_view::ListingViewOptions;
use simplefile_core::models::{
    DirectoryListing, DirectoryListingChunk, ListingViewDelta, ListingViewPage,
};
use simplefile_ipc::rpc::{JsonRpcNotification, JsonRpcResponse};
use simplefile_ipc::LIST_DIRECTORY_CHUNK;
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

/// While a folder is still being enumerated, this many of its first chunks
/// go to the host as a preview. The rest of the folder is never streamed:
/// the host reads it in pages, in view order.
const PREVIEW_CHUNKS: u32 = 2;

/// Everything a listing view request needs from the session.
#[derive(Clone)]
pub(super) struct ListingContext {
    pub(super) writer: OutboundSink,
    pub(super) scheduler: BlockingScheduler,
    pub(super) binary_hot_frames: Arc<AtomicBool>,
    pub(super) cache: SessionCache,
    pub(super) views: ListingViews,
    pub(super) events: EventSink,
}

/// A folder as enumerated for a view.
struct Enumerated {
    listing: DirectoryListing,
    /// False for the Recycle Bin and archive folders.
    paths_follow_names: bool,
}

impl Enumerated {
    fn is_cacheable(&self) -> bool {
        self.paths_follow_names && !self.listing.is_network
    }
}

/// Enumerates `path` with every entry's full metadata.
fn enumerate(
    path: String,
    on_chunk: impl FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<Enumerated, String> {
    let paths_follow_names = !(simplefile_core::recycle_bin::is_recycle_bin_path(&path)
        || simplefile_core::archive::is_archive_path(&path)
        || simplefile_core::archive::unavailable_archive_note(&path).is_some());
    let listing = simplefile_core::dir_list::list_directory(path, on_chunk)?;
    Ok(Enumerated {
        listing,
        paths_follow_names,
    })
}

pub(super) fn spawn_open_listing_view(
    context: ListingContext,
    id: Option<Value>,
    params: OpenListingViewParams,
) {
    tokio::spawn(async move {
        let response_id = id.clone();
        if let Err(message) = open_listing_view(&context, id, params).await {
            let _ = write_json(
                &context.writer,
                &JsonRpcResponse::application_error(response_id, message),
            )
            .await;
        }
    });
}

async fn open_listing_view(
    context: &ListingContext,
    id: Option<Value>,
    params: OpenListingViewParams,
) -> Result<(), String> {
    let options = params.options().normalized();
    let page_rows = params.page_size.unwrap_or(0);
    let reload = params.reload.unwrap_or(false);

    // The pane only wants another order or filter of the folder it shows.
    // That is not a navigation: it enumerates nothing and leaves an open
    // that is under way alone, and it fails if the view has moved on.
    if params.reuse.unwrap_or(false) {
        let page = context
            .views
            .reconfigure(&params.view_id, &params.path, options, page_rows)
            .ok_or_else(|| {
                format!(
                    "Listing view does not show {}: {}",
                    params.path, params.view_id
                )
            })?;
        return write_page(context, id, &page).await;
    }

    // A navigation. Every earlier open of this view that is still under way
    // is superseded, whether this one enumerates or the view already shows
    // the folder (the pane went back before a slower folder finished).
    let ticket = context.views.begin_open(&params.view_id);
    if !reload {
        if let Some(page) =
            context
                .views
                .reconfigure(&params.view_id, &params.path, options.clone(), page_rows)
        {
            context.views.finish_open(&params.view_id, ticket);
            return write_page(context, id, &page).await;
        }
    }

    // A cached listing opens at once. One too old to trust blindly is shown
    // and then enumerated again behind the view; what differs arrives as a delta.
    if !reload {
        if let Some((listing, freshness)) = context.cache.lookup_directory_listing(&params.path) {
            let folder = listing.path.clone();
            let page = install(
                context,
                &params.view_id,
                ticket,
                listing,
                true,
                options,
                page_rows,
            )?;
            write_page(context, id, &page).await?;
            if freshness == ListingFreshness::Unverified {
                spawn_revalidate(context.clone(), params.view_id, folder);
            }
            return Ok(());
        }
    }

    let binary_request_id = binary_response_id(&context.binary_hot_frames, &id);
    let (tx, mut rx) = tokio::sync::mpsc::channel::<DirectoryListingChunk>(PREVIEW_CHUNKS as usize);
    let scheduler = context.scheduler.clone();
    let path = params.path.clone();
    let preview_options = options.clone();
    let join = tokio::spawn(async move {
        scheduler
            .run_foreground(move || {
                enumerate(path, |mut chunk| {
                    if chunk.chunk_index < PREVIEW_CHUNKS && !chunk.done {
                        chunk.entries.retain(|entry| preview_options.shows(entry));
                        // A full channel or a closed one only costs the preview.
                        let _ = tx.try_send(chunk);
                    }
                    Ok(())
                })
            })
            .await
    });

    while let Some(chunk) = rx.recv().await {
        write_preview_chunk(context, binary_request_id, &id, &chunk).await?;
    }

    let enumerated = join
        .await
        .map_err(|error| format!("listing task failed: {error}"))???;
    if enumerated.is_cacheable() {
        context
            .cache
            .put_directory_listing(&enumerated.listing.path, enumerated.listing.clone());
    }
    let page = install(
        context,
        &params.view_id,
        ticket,
        enumerated.listing,
        enumerated.paths_follow_names,
        options,
        page_rows,
    )?;
    write_page(context, id, &page).await
}

/// Opens the view and, when it replaced a view of another folder, leaves that
/// folder's entries in the cache so that going back to it opens at once. An
/// open that a later one superseded stores nothing and fails: the pane that
/// asked has already moved on and ignores the reply.
fn install(
    context: &ListingContext,
    view_id: &str,
    ticket: OpenTicket,
    listing: DirectoryListing,
    paths_follow_names: bool,
    options: ListingViewOptions,
    page_rows: u32,
) -> Result<ListingViewPage, String> {
    let (page, left_behind) = context
        .views
        .open(
            view_id,
            ticket,
            listing,
            paths_follow_names,
            options,
            page_rows,
        )
        .ok_or_else(|| {
            format!("Listing view was opened again before this open finished: {view_id}")
        })?;
    if let Some(left_behind) = left_behind {
        keep_for_return(&context.cache, left_behind);
    }
    Ok(page)
}

/// Caches a closed view's entries unless the cache still has the folder's
/// own enumeration. They were kept current by change events, not re-read as
/// a whole, so they are only ever served as unverified.
fn keep_for_return(cache: &SessionCache, listing: DirectoryListing) {
    if !cache.has_directory_listing(&listing.path) {
        let path = listing.path.clone();
        cache.put_unverified_directory_listing(&path, listing);
    }
}

async fn write_preview_chunk(
    context: &ListingContext,
    binary_request_id: Option<i32>,
    id: &Option<Value>,
    chunk: &DirectoryListingChunk,
) -> Result<(), String> {
    if let Some(request_id) = binary_request_id {
        let payload = crate::binary::encode_directory_listing_chunk(request_id, chunk)?;
        return queue_payload(&context.writer, &payload).await;
    }

    let mut params = serde_json::to_value(chunk)
        .map_err(|error| format!("failed to serialize listing chunk: {error}"))?;
    if let (Some(object), Some(request_id)) = (params.as_object_mut(), id) {
        object.insert("requestId".to_string(), request_id.clone());
    }
    write_json(
        &context.writer,
        &JsonRpcNotification::new(LIST_DIRECTORY_CHUNK, params),
    )
    .await
}

async fn write_page(
    context: &ListingContext,
    id: Option<Value>,
    page: &ListingViewPage,
) -> Result<(), String> {
    if let Some(request_id) = binary_response_id(&context.binary_hot_frames, &id) {
        let payload = crate::binary::encode_listing_view_page(request_id, page)?;
        return write_binary_response(&context.writer, id, &payload).await;
    }
    let result = serde_json::to_value(page)
        .map_err(|error| format!("failed to serialize listing view page: {error}"))?;
    write_json(&context.writer, &JsonRpcResponse::result(id, result)).await
}

/// `get_listing_view_range`: a window of rows, read under the registry lock.
pub(super) async fn reply_listing_view_range(
    context: &ListingContext,
    id: Option<Value>,
    view_id: &str,
    offset: u32,
    count: u32,
) -> Result<(), String> {
    match context.views.range(view_id, offset, count) {
        Ok(page) => write_page(context, id, &page).await,
        Err(message) => {
            write_json(
                &context.writer,
                &JsonRpcResponse::application_error(id, message),
            )
            .await
        }
    }
}

/// `close_listing_view`: forget the view, keep its entries for a return.
pub(super) fn close_listing_view(context: &ListingContext, view_id: &str) {
    if let Some(listing) = context.views.close(view_id) {
        keep_for_return(&context.cache, listing);
    }
}

/// `refresh_listing_view`: enumerate the view's folder again and reply with
/// what changed.
pub(super) fn spawn_refresh_listing_view(
    context: ListingContext,
    id: Option<Value>,
    view_id: String,
) {
    tokio::spawn(async move {
        let response = match refresh_view(&context, &view_id).await {
            Ok(delta) => match serde_json::to_value(&delta) {
                Ok(value) => JsonRpcResponse::result(id, value),
                Err(error) => JsonRpcResponse::application_error(
                    id,
                    format!("failed to serialize listing view delta: {error}"),
                ),
            },
            Err(message) => JsonRpcResponse::application_error(id, message),
        };
        let _ = write_json(&context.writer, &response).await;
    });
}

async fn refresh_view(context: &ListingContext, view_id: &str) -> Result<ListingViewDelta, String> {
    let folder = context
        .views
        .path_of(view_id)
        .ok_or_else(|| format!("Listing view is not open: {view_id}"))?;
    let path = folder.clone();
    let enumerated = context
        .scheduler
        .run_foreground(move || enumerate(path, |_| Ok(())))
        .await??;
    apply_enumeration(context, view_id, &folder, enumerated)
        .ok_or_else(|| format!("Listing view no longer shows {folder}: {view_id}"))
}

/// Caches a fresh enumeration and applies it to the view, which may have
/// been closed or navigated since (then `None`).
fn apply_enumeration(
    context: &ListingContext,
    view_id: &str,
    folder: &str,
    enumerated: Enumerated,
) -> Option<ListingViewDelta> {
    if enumerated.is_cacheable() {
        context
            .cache
            .put_directory_listing(&enumerated.listing.path, enumerated.listing.clone());
    }
    context
        .views
        .refresh(view_id, folder, enumerated.listing.entries)
}

/// Enumerates a folder shown from an unverified cached listing and sends the
/// view what differs. Background work: it never delays the page already sent.
fn spawn_revalidate(context: ListingContext, view_id: String, folder: String) {
    tokio::spawn(async move {
        let path = folder.clone();
        let Ok(Ok(enumerated)) = context
            .scheduler
            .run_general(move || enumerate(path, |_| Ok(())))
            .await
        else {
            // The folder vanished or cannot be read: the next navigation or
            // refresh reports it; the page already shown stays.
            return;
        };
        if let Some(delta) = apply_enumeration(&context, &view_id, &folder, enumerated) {
            if !delta.changes.is_empty() || delta.reset {
                context.events.emit_listing_view_delta_async(&delta).await;
            }
        }
    });
}

/// A watched path changed: update the views of its folder and tell the host
/// which rows moved. Runs on the watcher's own thread.
pub(super) fn apply_file_change(
    views: &ListingViews,
    cache: &SessionCache,
    events: &EventSink,
    path: &str,
) {
    let changes = views.apply_path_change(path, |changed| {
        simplefile_core::file_ops::get_entry_info_simple(changed).ok()
    });
    for delta in &changes.deltas {
        events.emit_listing_view_delta(delta);
    }

    // A storm of changes is reported as a change of the folder itself.
    for view_id in changes.refresh {
        let Some(folder) = views.path_of(&view_id) else {
            continue;
        };
        let Ok(enumerated) = enumerate(folder.clone(), |_| Ok(())) else {
            continue;
        };
        if enumerated.is_cacheable() {
            cache.put_directory_listing(&enumerated.listing.path, enumerated.listing.clone());
        }
        if let Some(delta) = views.refresh(&view_id, &folder, enumerated.listing.entries) {
            if !delta.changes.is_empty() || delta.reset {
                events.emit_listing_view_delta(&delta);
            }
        }
    }
}
