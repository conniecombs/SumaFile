use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore};

const MIN_GENERAL_BLOCKING_PERMITS: usize = 2;
const DEFAULT_TRANSFER_PERMITS: usize = 2;
/// How many thumbnails and previews wait on the media process at a time: the
/// numbers its client lets through.
const MEDIA_THUMBNAIL_PERMITS: usize = 4;
const MEDIA_PREVIEW_PERMITS: usize = 2;
/// How many video thumbnails wait on the video process at a time: two from
/// local drives and one from a network drive for the file list, and one for
/// the preview pane's frame picker. The app keeps to those numbers, so the
/// picker always finds one free.
const MEDIA_VIDEO_PERMITS: usize = 4;
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
    media_thumbnails: Arc<Semaphore>,
    media_previews: Arc<Semaphore>,
    media_videos: Arc<Semaphore>,
    foreground_in_flight: Arc<AtomicUsize>,
    foreground_notify: Arc<Notify>,
}

/// Work that waits on the media process (`sumafile-media`) or the video
/// process (`sumafile-video`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum MediaLane {
    /// A thumbnail of a format the media process decodes.
    Thumbnail,
    /// A picture for the preview pane.
    Preview,
    /// A thumbnail of a video, from the video process.
    Video,
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
            media_thumbnails: Arc::new(Semaphore::new(MEDIA_THUMBNAIL_PERMITS)),
            media_previews: Arc::new(Semaphore::new(MEDIA_PREVIEW_PERMITS)),
            media_videos: Arc::new(Semaphore::new(MEDIA_VIDEO_PERMITS)),
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

    /// Run work that waits on the media process. It takes no general or
    /// background permit: the work is another process's, and a slow file
    /// there must not hold up thumbnails and folder sizes made here. A
    /// thumbnail waits while foreground work runs, as background work does;
    /// a preview is what the user is looking at and does not. Nor does a
    /// video thumbnail: one of its permits serves the preview pane's frame
    /// picker, and the decoding runs in another process at a low priority.
    pub(crate) async fn run_media<F, R>(&self, lane: MediaLane, task: F) -> Result<R, String>
    where
        F: FnOnce() -> R + Send + 'static,
        R: Send + 'static,
    {
        let permits = match lane {
            MediaLane::Thumbnail => &self.media_thumbnails,
            MediaLane::Preview => &self.media_previews,
            MediaLane::Video => &self.media_videos,
        };
        let permit = permits
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| "media scheduler is closed".to_string())?;
        if lane == MediaLane::Thumbnail {
            self.wait_for_foreground_idle().await;
        }
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            task()
        })
        .await
        .map_err(|error| error.to_string())
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
    use super::{
        BlockingScheduler, MediaLane, DEFAULT_TRANSFER_PERMITS, MEDIA_PREVIEW_PERMITS,
        MEDIA_THUMBNAIL_PERMITS, MEDIA_VIDEO_PERMITS,
    };
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

    /// A thumbnail the media process makes waits on that process, not on a
    /// core. Held on a background permit, four slow ones would stop every
    /// JPEG thumbnail and folder size on a four-core PC.
    #[tokio::test]
    async fn media_work_does_not_take_background_permits() {
        let scheduler = BlockingScheduler::new(2, 1);
        assert_eq!(scheduler.background.available_permits(), 1);
        let (entered_tx, entered_rx) = oneshot::channel();
        let (release_tx, release_rx) = mpsc::channel();
        let background = {
            let scheduler = scheduler.clone();
            tokio::spawn(async move {
                scheduler
                    .run_background(move || {
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

        let thumbnail = tokio::time::timeout(
            Duration::from_secs(2),
            scheduler.run_media(MediaLane::Thumbnail, || 7),
        )
        .await;
        let preview = tokio::time::timeout(
            Duration::from_secs(2),
            scheduler.run_media(MediaLane::Preview, || 8),
        )
        .await;

        assert_eq!(thumbnail, Ok(Ok(7)));
        assert_eq!(preview, Ok(Ok(8)));
        release_tx.send(()).unwrap();
        background.await.unwrap().unwrap();
    }

    /// Video thumbnails come from another program than pictures do, so they
    /// have permits of their own: a folder of slow pictures, with previews
    /// on top, does not hold a video back.
    #[tokio::test]
    async fn videos_do_not_wait_for_thumbnails_or_previews() {
        let scheduler = BlockingScheduler::new(2, 1);
        let (release_tx, release_rx) = mpsc::channel::<()>();
        let release_rx = Arc::new(std::sync::Mutex::new(release_rx));
        let entered = Arc::new(AtomicUsize::new(0));
        let mut held = Vec::new();
        let lanes = std::iter::repeat_n(MediaLane::Thumbnail, MEDIA_THUMBNAIL_PERMITS).chain(
            std::iter::repeat_n(MediaLane::Preview, MEDIA_PREVIEW_PERMITS),
        );
        for lane in lanes {
            let (scheduler, entered, release_rx) =
                (scheduler.clone(), entered.clone(), release_rx.clone());
            held.push(tokio::spawn(async move {
                scheduler
                    .run_media(lane, move || {
                        entered.fetch_add(1, Ordering::SeqCst);
                        let _ = release_rx.lock().unwrap().recv();
                    })
                    .await
            }));
        }
        let all = MEDIA_THUMBNAIL_PERMITS + MEDIA_PREVIEW_PERMITS;
        let deadline = tokio::time::Instant::now() + Duration::from_secs(2);
        while entered.load(Ordering::SeqCst) < all {
            assert!(tokio::time::Instant::now() < deadline);
            tokio::time::sleep(Duration::from_millis(5)).await;
        }

        let video = tokio::time::timeout(
            Duration::from_secs(2),
            scheduler.run_media(MediaLane::Video, || 7),
        )
        .await;

        assert_eq!(video, Ok(Ok(7)));
        for _ in 0..all {
            release_tx.send(()).unwrap();
        }
        for task in held {
            task.await.unwrap().unwrap();
        }
    }

    /// Two local list thumbnails, one from a network drive, and one for the
    /// preview pane: four, and a fifth waits.
    #[tokio::test]
    async fn at_most_four_videos_run_at_a_time() {
        assert_eq!(MEDIA_VIDEO_PERMITS, 4);
        let scheduler = BlockingScheduler::new(2, 1);
        let (release_tx, release_rx) = mpsc::channel::<()>();
        let release_rx = Arc::new(std::sync::Mutex::new(release_rx));
        let entered = Arc::new(AtomicUsize::new(0));
        let mut videos = Vec::new();
        for _ in 0..MEDIA_VIDEO_PERMITS + 1 {
            let (scheduler, entered, release_rx) =
                (scheduler.clone(), entered.clone(), release_rx.clone());
            videos.push(tokio::spawn(async move {
                scheduler
                    .run_media(MediaLane::Video, move || {
                        entered.fetch_add(1, Ordering::SeqCst);
                        let _ = release_rx.lock().unwrap().recv();
                    })
                    .await
            }));
        }
        let deadline = tokio::time::Instant::now() + Duration::from_secs(2);
        while entered.load(Ordering::SeqCst) < MEDIA_VIDEO_PERMITS {
            assert!(tokio::time::Instant::now() < deadline);
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;

        assert_eq!(
            entered.load(Ordering::SeqCst),
            MEDIA_VIDEO_PERMITS,
            "the fifth waits"
        );
        for _ in 0..MEDIA_VIDEO_PERMITS + 1 {
            release_tx.send(()).unwrap();
        }
        for video in videos {
            video.await.unwrap().unwrap();
        }
    }

    /// The preview pane must not queue behind a folder's thumbnails.
    #[tokio::test]
    async fn previews_do_not_wait_for_thumbnails() {
        let scheduler = BlockingScheduler::new(2, 1);
        let (release_tx, release_rx) = mpsc::channel::<()>();
        let release_rx = Arc::new(std::sync::Mutex::new(release_rx));
        let entered = Arc::new(AtomicUsize::new(0));
        let mut thumbnails = Vec::new();
        for _ in 0..MEDIA_THUMBNAIL_PERMITS + 1 {
            let (scheduler, entered, release_rx) =
                (scheduler.clone(), entered.clone(), release_rx.clone());
            thumbnails.push(tokio::spawn(async move {
                scheduler
                    .run_media(MediaLane::Thumbnail, move || {
                        entered.fetch_add(1, Ordering::SeqCst);
                        let _ = release_rx.lock().unwrap().recv();
                    })
                    .await
            }));
        }
        let deadline = tokio::time::Instant::now() + Duration::from_secs(2);
        while entered.load(Ordering::SeqCst) < MEDIA_THUMBNAIL_PERMITS {
            assert!(tokio::time::Instant::now() < deadline);
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;

        let preview = tokio::time::timeout(
            Duration::from_secs(2),
            scheduler.run_media(MediaLane::Preview, || 9),
        )
        .await;

        assert_eq!(
            entered.load(Ordering::SeqCst),
            MEDIA_THUMBNAIL_PERMITS,
            "one more waits"
        );
        assert_eq!(preview, Ok(Ok(9)));
        for _ in 0..MEDIA_THUMBNAIL_PERMITS + 1 {
            release_tx.send(()).unwrap();
        }
        for thumbnail in thumbnails {
            thumbnail.await.unwrap().unwrap();
        }
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
