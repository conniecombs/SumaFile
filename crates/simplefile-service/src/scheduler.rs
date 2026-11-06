use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore};

const MIN_GENERAL_BLOCKING_PERMITS: usize = 2;
const DEFAULT_TRANSFER_PERMITS: usize = 2;
const FOREGROUND_YIELD_POLL: Duration = Duration::from_millis(25);
/// Longest a queued background job waits for foreground work to drain.
const BACKGROUND_WAIT_CAP: Duration = Duration::from_millis(500);
/// Longest a running background job pauses per yield point.
const BACKGROUND_YIELD_CAP: Duration = Duration::from_millis(250);

#[derive(Clone)]
pub(crate) struct BlockingScheduler {
    general: Arc<Semaphore>,
    background: Arc<Semaphore>,
    transfer: Arc<Semaphore>,
    foreground_in_flight: Arc<AtomicUsize>,
    foreground_notify: Arc<Notify>,
}

struct BlockingPermits {
    _general: Option<OwnedSemaphorePermit>,
    _background: Option<OwnedSemaphorePermit>,
    _transfer: Option<OwnedSemaphorePermit>,
}

struct ForegroundGuard {
    counter: Arc<AtomicUsize>,
    notify: Arc<Notify>,
}

impl Drop for ForegroundGuard {
    fn drop(&mut self) {
        if self.counter.fetch_sub(1, Ordering::AcqRel) == 1 {
            self.notify.notify_waiters();
        }
    }
}

impl Default for BlockingScheduler {
    fn default() -> Self {
        let cores = std::thread::available_parallelism()
            .map(usize::from)
            .unwrap_or(MIN_GENERAL_BLOCKING_PERMITS);
        let general = cores.max(MIN_GENERAL_BLOCKING_PERMITS);
        Self::new(general, DEFAULT_TRANSFER_PERMITS)
    }
}

impl BlockingScheduler {
    pub(crate) fn new(general_permits: usize, transfer_permits: usize) -> Self {
        let general_permits = general_permits.max(1);
        let background_permits = if general_permits > 1 {
            (general_permits - 1).max(1)
        } else {
            1
        };
        Self {
            general: Arc::new(Semaphore::new(general_permits)),
            background: Arc::new(Semaphore::new(background_permits)),
            transfer: Arc::new(Semaphore::new(transfer_permits.max(1))),
            foreground_in_flight: Arc::new(AtomicUsize::new(0)),
            foreground_notify: Arc::new(Notify::new()),
        }
    }

    pub(crate) fn is_foreground_active(&self) -> bool {
        self.foreground_in_flight.load(Ordering::Acquire) > 0
    }

    /// Pause a running background job while foreground work is active, for
    /// at most `BACKGROUND_YIELD_CAP` per call. The cap keeps a long search
    /// from freezing thumbnails and folder sizes for its whole duration.
    pub(crate) fn yield_if_foreground_active_blocking(&self) {
        let started = std::time::Instant::now();
        while self.is_foreground_active() && started.elapsed() < BACKGROUND_YIELD_CAP {
            std::thread::sleep(Duration::from_millis(8));
        }
    }

    /// Wait for foreground work to drain, bounded by `BACKGROUND_WAIT_CAP` so a
    /// steady stream of foreground requests cannot starve background jobs.
    pub(crate) async fn wait_for_foreground_idle(&self) {
        let started = tokio::time::Instant::now();
        while self.is_foreground_active() && started.elapsed() < BACKGROUND_WAIT_CAP {
            let notified = self.foreground_notify.notified();
            if !self.is_foreground_active() {
                break;
            }
            let _ = tokio::time::timeout(FOREGROUND_YIELD_POLL, notified).await;
        }
    }

    pub(crate) async fn run_foreground<F, R>(&self, task: F) -> Result<R, String>
    where
        F: FnOnce() -> R + Send + 'static,
        R: Send + 'static,
    {
        // Count the job as foreground only once it holds a permit. Counting
        // while still queued let background jobs spin on a foreground job that
        // itself waited on permits those background jobs were holding.
        let permits = self.acquire_general().await?;
        self.foreground_in_flight.fetch_add(1, Ordering::AcqRel);
        let guard = ForegroundGuard {
            counter: self.foreground_in_flight.clone(),
            notify: self.foreground_notify.clone(),
        };
        tokio::task::spawn_blocking(move || {
            let _guard = guard;
            let _permits = permits;
            task()
        })
        .await
        .map_err(|error| error.to_string())
    }

    pub(crate) async fn run_background<F, R>(&self, task: F) -> Result<R, String>
    where
        F: FnOnce() -> R + Send + 'static,
        R: Send + 'static,
    {
        let permits = self.acquire_background().await?;
        self.wait_for_foreground_idle().await;
        run_with_permits(permits, task).await
    }

    pub(crate) async fn run_general<F, R>(&self, task: F) -> Result<R, String>
    where
        F: FnOnce() -> R + Send + 'static,
        R: Send + 'static,
    {
        let permits = self.acquire_general().await?;
        run_with_permits(permits, task).await
    }

    pub(crate) async fn run_transfer<F, R>(&self, task: F) -> Result<R, String>
    where
        F: FnOnce() -> R + Send + 'static,
        R: Send + 'static,
    {
        let permits = self.acquire_transfer().await?;
        run_with_permits(permits, task).await
    }

    async fn acquire_general(&self) -> Result<BlockingPermits, String> {
        let general = self
            .general
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| "blocking scheduler is closed".to_string())?;
        Ok(BlockingPermits {
            _general: Some(general),
            _background: None,
            _transfer: None,
        })
    }

    async fn acquire_background(&self) -> Result<BlockingPermits, String> {
        let background = self
            .background
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| "background scheduler is closed".to_string())?;
        let general = self
            .general
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| "blocking scheduler is closed".to_string())?;
        Ok(BlockingPermits {
            _general: Some(general),
            _background: Some(background),
            _transfer: None,
        })
    }

    /// Transfers run for minutes or hours and own their own lane. They do not
    /// take a general permit: the general pool is sized to the core count for
    /// short blocking calls, and two long transfers must not stall directory
    /// listings on a small machine.
    async fn acquire_transfer(&self) -> Result<BlockingPermits, String> {
        let transfer = self
            .transfer
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| "transfer scheduler is closed".to_string())?;
        Ok(BlockingPermits {
            _general: None,
            _background: None,
            _transfer: Some(transfer),
        })
    }
}

async fn run_with_permits<F, R>(permits: BlockingPermits, task: F) -> Result<R, String>
where
    F: FnOnce() -> R + Send + 'static,
    R: Send + 'static,
{
    tokio::task::spawn_blocking(move || {
        let _permits = permits;
        task()
    })
    .await
    .map_err(|error| error.to_string())
}

#[cfg(test)]
mod tests {
    use super::{BlockingScheduler, DEFAULT_TRANSFER_PERMITS};
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::{mpsc, Arc};
    use std::time::Duration;
    use tokio::sync::oneshot;

    #[test]
    fn default_limits_are_nonzero() {
        let scheduler = BlockingScheduler::default();
        assert!(scheduler.general.available_permits() >= 1);
        assert!(scheduler.background.available_permits() >= 1);
        assert!(scheduler.transfer.available_permits() >= 1);
    }

    #[test]
    fn default_allows_two_concurrent_transfer_jobs() {
        let scheduler = BlockingScheduler::default();
        assert_eq!(
            scheduler.transfer.available_permits(),
            DEFAULT_TRANSFER_PERMITS
        );
    }

    #[tokio::test]
    async fn transfer_lane_limits_concurrent_transfer_jobs() {
        let scheduler = BlockingScheduler::new(2, 1);
        let (entered_tx, entered_rx) = oneshot::channel();
        let (release_tx, release_rx) = mpsc::channel();
        let second_started = Arc::new(AtomicUsize::new(0));

        let first = {
            let scheduler = scheduler.clone();
            tokio::spawn(async move {
                scheduler
                    .run_transfer(move || {
                        entered_tx.send(()).unwrap();
                        release_rx.recv().unwrap();
                    })
                    .await
            })
        };
        tokio::time::timeout(Duration::from_secs(2), entered_rx)
            .await
            .unwrap()
            .unwrap();

        let second = {
            let scheduler = scheduler.clone();
            let second_started = second_started.clone();
            tokio::spawn(async move {
                scheduler
                    .run_transfer(move || {
                        second_started.fetch_add(1, Ordering::SeqCst);
                    })
                    .await
            })
        };

        tokio::time::sleep(Duration::from_millis(50)).await;
        assert_eq!(second_started.load(Ordering::SeqCst), 0);

        release_tx.send(()).unwrap();
        first.await.unwrap().unwrap();
        second.await.unwrap().unwrap();
        assert_eq!(second_started.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn transfers_do_not_consume_general_permits() {
        let scheduler = BlockingScheduler::new(1, 2);
        let (entered_tx, entered_rx) = oneshot::channel();
        let (release_tx, release_rx) = mpsc::channel();
        let transfer = {
            let scheduler = scheduler.clone();
            tokio::spawn(async move {
                scheduler
                    .run_transfer(move || {
                        entered_tx.send(()).unwrap();
                        release_rx.recv().unwrap();
                    })
                    .await
            })
        };
        tokio::time::timeout(Duration::from_secs(2), entered_rx)
            .await
            .unwrap()
            .unwrap();
        // A foreground listing still gets the single general permit while the
        // transfer is running.
        let listed = tokio::time::timeout(Duration::from_secs(2), scheduler.run_foreground(|| 42))
            .await
            .expect("foreground work must not wait for a long transfer")
            .unwrap();
        assert_eq!(listed, 42);
        release_tx.send(()).unwrap();
        transfer.await.unwrap().unwrap();
    }

    #[tokio::test]
    async fn background_tasks_yield_while_foreground_is_active() {
        let scheduler = BlockingScheduler::new(4, 2);
        let (fg_entered_tx, fg_entered_rx) = oneshot::channel();
        let (fg_release_tx, fg_release_rx) = mpsc::channel();
        let bg_started = Arc::new(AtomicUsize::new(0));

        let fg_handle = {
            let scheduler = scheduler.clone();
            tokio::spawn(async move {
                scheduler
                    .run_foreground(move || {
                        fg_entered_tx.send(()).unwrap();
                        fg_release_rx.recv().unwrap();
                    })
                    .await
            })
        };

        tokio::time::timeout(Duration::from_secs(2), fg_entered_rx)
            .await
            .unwrap()
            .unwrap();
        assert!(scheduler.is_foreground_active());

        let bg_handle = {
            let scheduler = scheduler.clone();
            let bg_started = bg_started.clone();
            tokio::spawn(async move {
                scheduler
                    .run_background(move || {
                        bg_started.fetch_add(1, Ordering::SeqCst);
                    })
                    .await
            })
        };

        tokio::time::sleep(Duration::from_millis(60)).await;
        assert_eq!(bg_started.load(Ordering::SeqCst), 0);

        fg_release_tx.send(()).unwrap();
        fg_handle.await.unwrap().unwrap();
        bg_handle.await.unwrap().unwrap();
        assert_eq!(bg_started.load(Ordering::SeqCst), 1);
        assert!(!scheduler.is_foreground_active());
    }
}

#[cfg(test)]
mod starvation_tests {
    use super::BlockingScheduler;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::{mpsc, Arc};
    use std::time::{Duration, Instant};
    use tokio::sync::oneshot;

    /// A long foreground job must delay background work, not stall it for
    /// the job's whole duration: otherwise one multi-minute search freezes
    /// every thumbnail and folder-size request in the app.
    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn background_work_starts_while_a_long_foreground_job_is_running() {
        let scheduler = BlockingScheduler::new(4, 2);
        let (fg_entered_tx, fg_entered_rx) = oneshot::channel();
        let (fg_release_tx, fg_release_rx) = mpsc::channel();
        let bg_started = Arc::new(AtomicUsize::new(0));

        let fg_handle = {
            let scheduler = scheduler.clone();
            tokio::spawn(async move {
                scheduler
                    .run_foreground(move || {
                        fg_entered_tx.send(()).unwrap();
                        fg_release_rx.recv().unwrap();
                    })
                    .await
            })
        };
        tokio::time::timeout(Duration::from_secs(2), fg_entered_rx)
            .await
            .unwrap()
            .unwrap();

        let started = Instant::now();
        let bg_handle = {
            let scheduler = scheduler.clone();
            let bg_started = bg_started.clone();
            tokio::spawn(async move {
                scheduler
                    .run_background(move || {
                        bg_started.fetch_add(1, Ordering::SeqCst);
                    })
                    .await
            })
        };

        let outcome = tokio::time::timeout(Duration::from_secs(3), bg_handle).await;
        let waited = started.elapsed();
        fg_release_tx.send(()).unwrap();
        fg_handle.await.unwrap().unwrap();

        assert!(
            outcome.is_ok(),
            "background job never ran while foreground stayed active"
        );
        assert_eq!(bg_started.load(Ordering::SeqCst), 1);
        assert!(
            waited >= Duration::from_millis(50),
            "background should still yield briefly to foreground work"
        );
    }
}
