//! `TransferBridge`: carries one transfer's progress from the engine's
//! blocking thread to the host, and the host's cancel and settings back.
//!
//! The engine calls [`TransferObserver`] on its own thread; the bridge only
//! keeps the latest snapshot there and never blocks. One pump task, on the
//! backend runtime:
//! - every 50 ms sets the engine's cancel flag once the host cancelled the
//!   request's `operationId` or the Transfers row's id;
//! - sends `host.progress_report` for the row, with at most one report in
//!   flight and at most one every 100 ms, so a slow host never stalls the
//!   byte stream;
//! - emits `remote.transfer-progress` and `remote.transfer-tuning`, each at
//!   most every 100 ms (`emit` never waits);
//! - every 5 s refreshes the plugin transfer settings into the policy the
//!   engine's `read_policy` reads.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use serde_json::json;
use sumafile_plugin::HostClient;
use sumafile_transfer::policy::Policy;
use tokio::sync::Notify;
use tokio::task::JoinHandle;

use crate::handler::{PROGRESS_EVENT, TUNING_EVENT};
use crate::settings::TransferSettingsCache;
use crate::transfer::{TransferObserver, TransferProgress, TransferTuning};

/// How often the pump wakes: the cancel poll interval.
const CANCEL_POLL: Duration = Duration::from_millis(50);
/// The least time between two reports, and between two events of a kind.
const REPORT_INTERVAL: Duration = Duration::from_millis(100);
/// How often the plugin settings are read again while a transfer runs.
const POLICY_REFRESH: Duration = Duration::from_secs(5);
/// How long the end of a transfer waits for its last report.
const FINAL_REPORT_TIMEOUT: Duration = Duration::from_secs(5);

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// The latest value of a kind and a version number that grows with each.
struct Latest<T> {
    value: Option<T>,
    version: u64,
}

impl<T> Default for Latest<T> {
    fn default() -> Self {
        Latest {
            value: None,
            version: 0,
        }
    }
}

impl<T: Clone> Latest<T> {
    fn set(&mut self, value: &T) {
        self.value = Some(value.clone());
        self.version += 1;
    }

    /// The value when it is newer than `seen`, and its version.
    fn newer_than(&self, seen: u64) -> Option<(T, u64)> {
        (self.version > seen)
            .then(|| self.value.clone().map(|value| (value, self.version)))
            .flatten()
    }
}

#[derive(Default)]
struct Shared {
    progress: Mutex<Latest<TransferProgress>>,
    tuning: Mutex<Latest<TransferTuning>>,
    /// Versions already sent: progress reported, progress emitted, tuning emitted.
    reported: Mutex<u64>,
    emitted_progress: Mutex<u64>,
    emitted_tuning: Mutex<u64>,
}

pub struct TransferBridge {
    shared: Arc<Shared>,
    cancel: Arc<AtomicBool>,
    policy: Arc<Mutex<Policy>>,
    host: HostClient,
    row_id: Option<String>,
    stop: Arc<Notify>,
    pump: Mutex<Option<JoinHandle<Option<JoinHandle<()>>>>>,
}

/// The `detail` of a Transfers-row report.
fn report_detail(progress: &TransferProgress) -> String {
    format!(
        "{}/{} files · {}",
        progress.current_files, progress.total_files, progress.current_item
    )
}

async fn report(host: &HostClient, row_id: &str, progress: &TransferProgress) {
    let _ = host
        .progress_report(
            row_id,
            progress.current,
            progress.total,
            Some(&report_detail(progress)),
        )
        .await;
}

impl TransferBridge {
    /// Starts the pump on the current runtime. `row_id` is the id
    /// `host.progress_begin` returned, if it opened a row; `operation_id` is
    /// the request's. `policy` is the policy the transfer starts with.
    pub fn start(
        host: HostClient,
        row_id: Option<String>,
        operation_id: String,
        settings: Arc<TransferSettingsCache>,
        policy: Policy,
    ) -> TransferBridge {
        let bridge = TransferBridge {
            shared: Arc::new(Shared::default()),
            cancel: Arc::new(AtomicBool::new(false)),
            policy: Arc::new(Mutex::new(policy)),
            host,
            row_id,
            stop: Arc::new(Notify::new()),
            pump: Mutex::new(None),
        };
        let pump = tokio::spawn(pump(
            bridge.shared.clone(),
            bridge.cancel.clone(),
            bridge.policy.clone(),
            bridge.host.clone(),
            bridge.row_id.clone(),
            operation_id,
            settings,
            bridge.stop.clone(),
        ));
        *lock(&bridge.pump) = Some(pump);
        bridge
    }

    /// The flag the engine polls.
    pub fn cancel_flag(&self) -> &AtomicBool {
        &self.cancel
    }

    /// The engine's `read_policy`: the latest plugin settings.
    pub fn read_policy(&self) -> Policy {
        lock(&self.policy).clone()
    }

    /// Stops the pump, waits for the report in flight, then reports the
    /// latest progress to the row if the row has not seen it. Returns the
    /// latest progress and the latest tuning not yet emitted.
    pub async fn finish(&self) -> (Option<TransferProgress>, Option<TransferTuning>) {
        self.stop.notify_one();
        let pump = lock(&self.pump).take();
        if let Some(pump) = pump {
            if let Ok(Some(in_flight)) = pump.await {
                let _ = tokio::time::timeout(FINAL_REPORT_TIMEOUT, in_flight).await;
            }
        }
        let latest = lock(&self.shared.progress).value.clone();
        let pending = {
            let reported = *lock(&self.shared.reported);
            lock(&self.shared.progress).newer_than(reported)
        };
        if let (Some(row_id), Some((progress, version))) = (&self.row_id, pending) {
            *lock(&self.shared.reported) = version;
            let _ =
                tokio::time::timeout(FINAL_REPORT_TIMEOUT, report(&self.host, row_id, &progress))
                    .await;
        }
        let tuning = {
            let emitted = *lock(&self.shared.emitted_tuning);
            lock(&self.shared.tuning).newer_than(emitted)
        }
        .map(|(tuning, _)| tuning);
        (latest, tuning)
    }
}

impl TransferObserver for TransferBridge {
    fn progress(&self, update: &TransferProgress) {
        lock(&self.shared.progress).set(update);
    }

    fn tuning(&self, update: &TransferTuning) {
        lock(&self.shared.tuning).set(update);
    }
}

/// The pump loop. Returns the report still in flight when it stopped.
#[allow(clippy::too_many_arguments)]
async fn pump(
    shared: Arc<Shared>,
    cancel: Arc<AtomicBool>,
    policy: Arc<Mutex<Policy>>,
    host: HostClient,
    row_id: Option<String>,
    operation_id: String,
    settings: Arc<TransferSettingsCache>,
    stop: Arc<Notify>,
) -> Option<JoinHandle<()>> {
    let mut ticker = tokio::time::interval(CANCEL_POLL);
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut in_flight: Option<JoinHandle<()>> = None;
    let mut refreshing: Option<JoinHandle<()>> = None;
    let mut last_report: Option<Instant> = None;
    let mut last_progress_event: Option<Instant> = None;
    let mut last_tuning_event: Option<Instant> = None;
    let mut last_refresh = Instant::now();
    let due = |last: Option<Instant>| last.is_none_or(|at| at.elapsed() >= REPORT_INTERVAL);

    loop {
        tokio::select! {
            _ = ticker.tick() => {}
            _ = stop.notified() => break,
        }

        if host.is_cancelled(&operation_id)
            || row_id.as_deref().is_some_and(|row| host.is_cancelled(row))
        {
            cancel.store(true, Ordering::Relaxed);
        }

        if last_refresh.elapsed() >= POLICY_REFRESH
            && refreshing.as_ref().is_none_or(JoinHandle::is_finished)
        {
            last_refresh = Instant::now();
            let (host, settings, policy) = (host.clone(), settings.clone(), policy.clone());
            refreshing = Some(tokio::spawn(async move {
                let fresh = settings.refresh(&host).await;
                *lock(&policy) = fresh;
            }));
        }

        if let Some(row_id) = &row_id {
            if due(last_report) && in_flight.as_ref().is_none_or(JoinHandle::is_finished) {
                let pending = {
                    let reported = *lock(&shared.reported);
                    lock(&shared.progress).newer_than(reported)
                };
                if let Some((progress, version)) = pending {
                    *lock(&shared.reported) = version;
                    last_report = Some(Instant::now());
                    let (host, row_id) = (host.clone(), row_id.clone());
                    in_flight = Some(tokio::spawn(async move {
                        report(&host, &row_id, &progress).await;
                    }));
                }
            }
        }

        if due(last_progress_event) {
            let pending = {
                let emitted = *lock(&shared.emitted_progress);
                lock(&shared.progress).newer_than(emitted)
            };
            if let Some((progress, version)) = pending {
                *lock(&shared.emitted_progress) = version;
                last_progress_event = Some(Instant::now());
                let _ = host.emit(PROGRESS_EVENT, json!(progress)).await;
            }
        }

        if due(last_tuning_event) {
            let pending = {
                let emitted = *lock(&shared.emitted_tuning);
                lock(&shared.tuning).newer_than(emitted)
            };
            if let Some((tuning, version)) = pending {
                *lock(&shared.emitted_tuning) = version;
                last_tuning_event = Some(Instant::now());
                let _ = host.emit(TUNING_EVENT, json!(tuning)).await;
            }
        }
    }
    if let Some(refreshing) = refreshing {
        refreshing.abort();
    }
    in_flight.filter(|task| !task.is_finished())
}
