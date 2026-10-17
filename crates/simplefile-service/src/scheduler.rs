use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore};

const MIN_GENERAL_BLOCKING_PERMITS: usize = 2;
const DEFAULT_TRANSFER_PERMITS: usize = 2;
const FOREGROUND_YIELD_POLL: Duration = Duration::from_millis(25);

#[derive(Clone)]
pub(crate) struct BlockingScheduler {
    general: Arc<Semaphore>,
    background: Arc<Semaphore>,
    transfer: Arc<Semaphore>,
    foreground_in_flight: Arc<AtomicUsize>,
    foreground_notify: Arc<Notify>,
}

struct BlockingPermits {
    _general: OwnedSemaphorePermit,
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

    pub(crate) fn yield_if_foreground_active_blocking(&self) {
        while self.is_foreground_active() {
            std::thread::sleep(Duration::from_millis(8));
        }
    }

    pub(crate) async fn wait_for_foreground_idle(&self) {
        while self.is_foreground_active() {
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
        self.foreground_in_flight.fetch_add(1, Ordering::AcqRel);
        let guard = ForegroundGuard {
            counter: self.foreground_in_flight.clone(),
            notify: self.foreground_notify.clone(),
        };
        let permits = self.acquire_general().await?;
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
        self.wait_for_foreground_idle().await;
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
            _general: general,
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
            _general: general,
            _background: Some(background),
            _transfer: None,
        })
    }

    async fn acquire_transfer(&self) -> Result<BlockingPermits, String> {
        let transfer = self
            .transfer
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| "transfer scheduler is closed".to_string())?;
        let general = self
            .general
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| "blocking scheduler is closed".to_string())?;
        Ok(BlockingPermits {
            _general: general,
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
