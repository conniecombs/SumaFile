//! `get_entry_facts`: lazy column values for a list of paths, streamed back
//! as `entry-facts` notifications in the order the app asked (visible rows
//! first), with a cap on concurrent work, cancelled by `cancel_entry_facts`
//! or by a new request with the same id. Folder facts come from one
//! folder-metrics walk per folder (the session cache first); file facts from
//! the persistent cache, else a read on the background lane.

use super::io::{encode_json_payload, write_json, OutboundSink};
use crate::scheduler::BlockingScheduler;
use crate::session_cache::SessionCache;
use serde_json::Value;
use simplefile_core::entry_facts::{self, FactKind, STATE_FAILED, STATE_NONE, STATE_READY};
use simplefile_core::fact_cache;
use simplefile_core::models::{EntryFact, EntryFactsBatch, EntryFactsResult};
use simplefile_core::utils::format_system_time;
use simplefile_ipc::rpc::{JsonRpcNotification, JsonRpcResponse};
use simplefile_ipc::ENTRY_FACTS;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio::sync::{mpsc, Semaphore};

/// Folder walks at a time.
const FOLDER_WALKS: usize = 2;
/// File reads at a time.
const FILE_READS: usize = 4;
/// A batch goes out at this many facts, or after [`BATCH_WAIT`] with fewer.
const BATCH_FACTS: usize = 16;
const BATCH_WAIT: std::time::Duration = std::time::Duration::from_millis(50);
/// More paths than this in one request are left for the next one.
const MAX_PATHS: usize = 256;

#[allow(clippy::too_many_arguments)]
pub(super) fn spawn_entry_facts(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    cache: SessionCache,
    id: Option<Value>,
    request_id: String,
    paths: Vec<String>,
    kinds: Vec<String>,
    cancel: Arc<AtomicBool>,
) {
    tokio::spawn(async move {
        let kinds: Vec<FactKind> = kinds
            .iter()
            .filter_map(|kind| FactKind::from_wire(kind))
            .collect();
        let folder_kinds: Vec<FactKind> = kinds
            .iter()
            .copied()
            .filter(|kind| kind.is_folder_kind())
            .collect();
        let file_kinds: Vec<FactKind> = kinds
            .iter()
            .copied()
            .filter(|kind| !kind.is_folder_kind())
            .collect();

        let (tx, rx) = mpsc::channel::<EntryFact>(256);
        let flusher = tokio::spawn(flush_batches(writer.clone(), request_id.clone(), rx));
        let folder_walks = Arc::new(Semaphore::new(FOLDER_WALKS));
        let file_reads = Arc::new(Semaphore::new(FILE_READS));
        let mut workers = Vec::new();
        for path in paths.into_iter().take(MAX_PATHS) {
            if !folder_kinds.is_empty() {
                workers.push(tokio::spawn(folder_facts(
                    scheduler.clone(),
                    cache.clone(),
                    folder_walks.clone(),
                    path.clone(),
                    folder_kinds.clone(),
                    cancel.clone(),
                    tx.clone(),
                )));
            }
            for kind in &file_kinds {
                workers.push(tokio::spawn(file_fact(
                    scheduler.clone(),
                    file_reads.clone(),
                    path.clone(),
                    *kind,
                    cancel.clone(),
                    tx.clone(),
                )));
            }
        }
        drop(tx);
        for worker in workers {
            let _ = worker.await;
        }
        let settled = flusher.await.unwrap_or(0);

        let result = EntryFactsResult {
            request_id,
            settled: u32::try_from(settled).unwrap_or(u32::MAX),
        };
        let response = match serde_json::to_value(result) {
            Ok(value) => JsonRpcResponse::result(id, value),
            Err(error) => JsonRpcResponse::application_error(
                id,
                format!("failed to serialize entry facts result: {error}"),
            ),
        };
        let _ = write_json(&writer, &response).await;
    });
}

/// Caches values the app found itself, keyed by each file's current size and modified time.
pub(super) fn spawn_put_entry_facts(
    writer: OutboundSink,
    scheduler: BlockingScheduler,
    id: Option<Value>,
    facts: Vec<EntryFact>,
) {
    tokio::spawn(async move {
        let _ = scheduler
            .run_general(move || {
                for fact in &facts {
                    if FactKind::from_wire(&fact.kind).is_none_or(|kind| kind.is_folder_kind()) {
                        continue;
                    }
                    if let Some((size, modified)) = file_state(Path::new(&fact.path)) {
                        let _ = fact_cache::put_cached_fact(fact, size, &modified);
                    }
                }
            })
            .await;
        let _ = write_json(&writer, &JsonRpcResponse::result(id, Value::Null)).await;
    });
}

/// The size and modified time (in the listing's form) the cache keys a file by.
fn file_state(path: &Path) -> Option<(u64, String)> {
    let metadata = std::fs::metadata(path).ok()?;
    let modified = metadata
        .modified()
        .map(format_system_time)
        .unwrap_or_default();
    Some((metadata.len(), modified))
}

async fn send_all(tx: &mpsc::Sender<EntryFact>, cancel: &AtomicBool, facts: Vec<EntryFact>) {
    if cancel.load(Ordering::Relaxed) {
        return;
    }
    for fact in facts {
        if tx.send(fact).await.is_err() {
            return;
        }
    }
}

async fn folder_facts(
    scheduler: BlockingScheduler,
    cache: SessionCache,
    walks: Arc<Semaphore>,
    path: String,
    kinds: Vec<FactKind>,
    cancel: Arc<AtomicBool>,
    tx: mpsc::Sender<EntryFact>,
) {
    let settle = |state: &str| -> Vec<EntryFact> {
        kinds
            .iter()
            .map(|kind| entry_facts::fact(Path::new(&path), *kind, state, String::new(), None))
            .collect()
    };
    if !Path::new(&path).is_dir() {
        send_all(&tx, &cancel, settle(STATE_NONE)).await;
        return;
    }
    let metrics = match cache.get_folder_metrics(&path) {
        Some(metrics) => Some(metrics),
        None => {
            let Ok(_permit) = walks.acquire_owned().await else {
                return;
            };
            if cancel.load(Ordering::Relaxed) {
                return;
            }
            let (path_for_walk, cancel_for_walk) = (path.clone(), cancel.clone());
            match scheduler
                .run_background(move || {
                    simplefile_core::file_ops::get_folder_metrics(&path_for_walk, &cancel_for_walk)
                })
                .await
            {
                Ok(Some(metrics)) => {
                    cache.put_subdirectories(&path, metrics.subdirectories.clone());
                    cache.put_folder_metrics(&path, metrics.clone());
                    Some(metrics)
                }
                // Cancelled: nothing to say.
                Ok(None) => return,
                Err(_) => None,
            }
        }
    };
    let facts = match metrics {
        Some(metrics) => kinds
            .iter()
            .map(|kind| {
                let value = match kind {
                    FactKind::FolderSize => metrics.size,
                    _ => metrics.item_count,
                };
                entry_facts::fact(
                    Path::new(&path),
                    *kind,
                    STATE_READY,
                    value.to_string(),
                    Some(value as f64),
                )
            })
            .collect(),
        None => settle(STATE_FAILED),
    };
    send_all(&tx, &cancel, facts).await;
}

async fn file_fact(
    scheduler: BlockingScheduler,
    reads: Arc<Semaphore>,
    path: String,
    kind: FactKind,
    cancel: Arc<AtomicBool>,
    tx: mpsc::Sender<EntryFact>,
) {
    let file = Path::new(&path);
    if file.is_dir() {
        send_all(
            &tx,
            &cancel,
            vec![entry_facts::fact(
                file,
                kind,
                STATE_NONE,
                String::new(),
                None,
            )],
        )
        .await;
        return;
    }
    let Some((size, modified)) = file_state(file) else {
        send_all(
            &tx,
            &cancel,
            vec![entry_facts::fact(
                file,
                kind,
                STATE_FAILED,
                String::new(),
                None,
            )],
        )
        .await;
        return;
    };
    if let Some(cached) = fact_cache::get_cached_fact(&path, kind, size, &modified) {
        send_all(&tx, &cancel, vec![cached]).await;
        return;
    }
    let Ok(_permit) = reads.acquire_owned().await else {
        return;
    };
    if cancel.load(Ordering::Relaxed) {
        return;
    }
    let path_for_read = path.clone();
    let read = scheduler
        .run_background(move || {
            let fact = entry_facts::read_file_fact(Path::new(&path_for_read), kind);
            let _ = fact_cache::put_cached_fact(&fact, size, &modified);
            fact
        })
        .await;
    let fact =
        read.unwrap_or_else(|_| entry_facts::fact(file, kind, STATE_FAILED, String::new(), None));
    send_all(&tx, &cancel, vec![fact]).await;
}

/// Sends the facts as `entry-facts` batches and returns how many went out.
async fn flush_batches(
    writer: OutboundSink,
    request_id: String,
    mut rx: mpsc::Receiver<EntryFact>,
) -> usize {
    let mut sent = 0usize;
    let mut pending: Vec<EntryFact> = Vec::new();
    loop {
        let next = tokio::time::timeout(BATCH_WAIT, rx.recv()).await;
        match next {
            Ok(Some(fact)) => {
                pending.push(fact);
                if pending.len() < BATCH_FACTS {
                    continue;
                }
            }
            Ok(None) => {
                sent += emit(&writer, &request_id, std::mem::take(&mut pending)).await;
                return sent;
            }
            Err(_) => {}
        }
        if !pending.is_empty() {
            sent += emit(&writer, &request_id, std::mem::take(&mut pending)).await;
        }
    }
}

async fn emit(writer: &OutboundSink, request_id: &str, facts: Vec<EntryFact>) -> usize {
    if facts.is_empty() {
        return 0;
    }
    let count = facts.len();
    let batch = EntryFactsBatch {
        request_id: request_id.to_string(),
        facts,
    };
    if let Ok(params) = serde_json::to_value(&batch) {
        if let Ok(payload) = encode_json_payload(&JsonRpcNotification::new(ENTRY_FACTS, params)) {
            let _ = writer.enqueue_payload(payload).await;
        }
    }
    count
}
