use std::sync::Arc;
use tokio::sync::{OwnedSemaphorePermit, Semaphore};

const MIN_GENERAL_BLOCKING_PERMITS: usize = 2;
const DEFAULT_TRANSFER_PERMITS: usize = 2;

#[derive(Clone)]
pub(crate) struct BlockingScheduler {
    general: Arc<Semaphore>,
    transfer: Arc<Semaphore>,
}

struct BlockingPermits {
    _general: OwnedSemaphorePermit,
    _transfer: Option<OwnedSemaphorePermit>,
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
        Self {
            general: Arc::new(Semaphore::new(general_permits.max(1))),
            transfer: Arc::new(Semaphore::new(transfer_permits.max(1))),
        }
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
}
