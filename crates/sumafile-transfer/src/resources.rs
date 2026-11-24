//! Process-wide active + cached buffer limits and per-job cancellable budgets.
use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

const GLOBAL_BYTES: usize = 512 * 1024 * 1024;
const POOL_BYTES: usize = 64 * 1024 * 1024;
const MAX_POOLED_BUFFERS: usize = 32;
struct Allocation {
    workers: usize,
    active: usize,
    pooled: usize,
    /// Oldest first: a full cache makes room by dropping its oldest buffers,
    /// so sizes nothing asks for any more cannot occupy it for good.
    pool: VecDeque<Vec<u8>>,
}
static ALLOCATION: Mutex<Allocation> = Mutex::new(Allocation {
    workers: 0,
    active: 0,
    pooled: 0,
    pool: VecDeque::new(),
});
static AVAILABLE: Condvar = Condvar::new();

pub struct MemoryBudget {
    limit: AtomicUsize,
    used: AtomicUsize,
}
impl MemoryBudget {
    pub fn new(bytes: usize) -> Arc<Self> {
        Arc::new(Self {
            limit: AtomicUsize::new(bytes),
            used: AtomicUsize::new(0),
        })
    }
    pub fn update(&self, bytes: usize) {
        self.limit.store(bytes, Ordering::Relaxed);
        AVAILABLE.notify_all();
    }
}

/// Only exact-capacity buffers are reused. A small file cannot inherit a huge
/// allocation that was charged as a small reservation.
pub struct PooledBuffer {
    data: Vec<u8>,
    /// Whether the buffer goes back to the cache when it is dropped.
    cached: bool,
}
impl PooledBuffer {
    /// A buffer of one of the sizes a transfer keeps asking for (the
    /// configured copy buffer, the verification chunk). It comes from the
    /// cache when one of exactly this size is there and returns to it.
    pub fn acquire(size: usize) -> Self {
        let cached = ALLOCATION.lock().ok().and_then(|mut state| {
            let index = state.pool.iter().position(|buf| buf.capacity() == size)?;
            let buffer = state.pool.remove(index)?;
            state.pooled -= buffer.capacity();
            Some(buffer)
        });
        Self {
            data: cached.unwrap_or_else(|| vec![0; size]),
            cached: true,
        }
    }

    /// A buffer cut to one file's length. No later request asks for that
    /// exact size again, so it is neither looked up in the cache nor kept
    /// there: a folder of photos would otherwise fill the cache with one
    /// unusable buffer per file.
    pub fn exact(size: usize) -> Self {
        Self {
            data: vec![0; size],
            cached: false,
        }
    }

    pub fn as_mut_slice(&mut self) -> &mut [u8] {
        &mut self.data
    }
}
impl std::ops::Deref for PooledBuffer {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        &self.data
    }
}
impl std::ops::DerefMut for PooledBuffer {
    fn deref_mut(&mut self) -> &mut [u8] {
        &mut self.data
    }
}
impl Drop for PooledBuffer {
    fn drop(&mut self) {
        let bytes = self.data.capacity();
        if !self.cached || !(64 * 1024..=POOL_BYTES).contains(&bytes) {
            return;
        }
        if let Ok(mut state) = ALLOCATION.lock() {
            // Active reservations still cover buffers being returned. Counting
            // both here is conservative and prevents a cache from exceeding the cap.
            if state.active + bytes > GLOBAL_BYTES {
                return;
            }
            // The newest buffer is the one most likely to be asked for again:
            // make room for it by dropping the oldest ones.
            while state.pool.len() >= MAX_POOLED_BUFFERS
                || state.pooled + bytes > POOL_BYTES
                || state.active + state.pooled + bytes > GLOBAL_BYTES
            {
                let Some(oldest) = state.pool.pop_front() else {
                    break;
                };
                state.pooled -= oldest.capacity();
            }
            state.pooled += bytes;
            state.pool.push_back(std::mem::take(&mut self.data));
        }
    }
}

pub struct Permit {
    bytes: usize,
    budget: Option<Arc<MemoryBudget>>,
}
impl Permit {
    pub fn acquire(bytes: usize, cancel: &AtomicBool) -> Result<Self, String> {
        Self::acquire_inner(bytes, None, cancel)
    }
    pub fn acquire_for_job(
        bytes: usize,
        budget: Arc<MemoryBudget>,
        cancel: &AtomicBool,
    ) -> Result<Self, String> {
        Self::acquire_inner(bytes, Some(budget), cancel)
    }
    fn acquire_inner(
        bytes: usize,
        budget: Option<Arc<MemoryBudget>>,
        cancel: &AtomicBool,
    ) -> Result<Self, String> {
        if bytes > GLOBAL_BYTES {
            return Err("Transfer buffer reservation exceeds the service budget".into());
        }
        let mut state = ALLOCATION.lock().map_err(|e| e.to_string())?;
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Err("Operation cancelled".into());
            }
            if budget
                .as_ref()
                .is_some_and(|b| bytes > b.limit.load(Ordering::Relaxed))
            {
                return Err(
                    "RETRYABLE:Transfer buffer budget changed; retry with current settings".into(),
                );
            }
            let fits_job = budget.as_ref().is_none_or(|b| {
                b.used.load(Ordering::Relaxed).saturating_add(bytes)
                    <= b.limit.load(Ordering::Relaxed)
            });
            if fits_job && state.workers < 32 && state.active.saturating_add(bytes) <= GLOBAL_BYTES
            {
                while state.active + state.pooled + bytes > GLOBAL_BYTES {
                    if let Some(buffer) = state.pool.pop_front() {
                        state.pooled -= buffer.capacity();
                    } else {
                        break;
                    }
                }
                state.workers += 1;
                state.active += bytes;
                if let Some(b) = &budget {
                    b.used.fetch_add(bytes, Ordering::Relaxed);
                }
                return Ok(Self { bytes, budget });
            }
            state = AVAILABLE
                .wait_timeout(state, Duration::from_millis(25))
                .map_err(|e| e.to_string())?
                .0;
        }
    }
}
impl Drop for Permit {
    fn drop(&mut self) {
        let mut state = ALLOCATION
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.workers = state.workers.saturating_sub(1);
        state.active = state.active.saturating_sub(self.bytes);
        if let Some(b) = &self.budget {
            b.used.fetch_sub(self.bytes, Ordering::Relaxed);
        }
        drop(state);
        AVAILABLE.notify_all();
    }
}

/// Only an explicit user bandwidth setting may limit bytes per second. Latency,
/// retry notifications, and free-space estimates must never create hidden caps.
///
/// Waiting requests take turns, and one turn grants at most a sixteenth of a
/// second of the limit, so every file in flight gets a share. Without turns a
/// request for a whole 4 MiB buffer never collects its bytes while requests
/// for small files keep taking the credit as it appears: a limited transfer of
/// mixed files then leaves its large files standing until every small one is
/// done. With turns but whole requests, the small files would wait out each
/// large buffer instead.
pub struct RateLimit {
    mib: AtomicU64,
    bucket: Mutex<Bucket>,
    /// Signalled when the request at the head of the line leaves it.
    turn: Condvar,
    retry_status: Mutex<Option<(Instant, String)>>,
}

struct Bucket {
    refilled: Instant,
    /// Bytes that may be sent now.
    credit: f64,
    next_ticket: u64,
    /// Waiting requests, oldest first.
    line: VecDeque<u64>,
}

/// Longest a waiting request sleeps before it looks at the cancel flag again.
const RATE_POLL: Duration = Duration::from_millis(25);
/// One turn grants this fraction of a second of the limit...
const RATE_TURNS_PER_SECOND: f64 = 16.0;
/// ...and never less than this many bytes, so the slowest limit (1 MiB/s)
/// still hands over the line at most sixteen times a second.
const RATE_MIN_TURN: usize = 64 * 1024;

impl RateLimit {
    pub fn new(mib: usize) -> Self {
        Self {
            mib: AtomicU64::new(mib as u64),
            bucket: Mutex::new(Bucket {
                refilled: Instant::now(),
                credit: 0.0,
                next_ticket: 0,
                line: VecDeque::new(),
            }),
            turn: Condvar::new(),
            retry_status: Mutex::new(None),
        }
    }

    fn bytes_per_second(&self) -> f64 {
        self.mib.load(Ordering::Relaxed).min(1_048_576) as f64 * 1048576.0
    }

    pub fn update(&self, mib: usize) {
        if self.mib.swap(mib as u64, Ordering::Relaxed) != mib as u64 {
            // Do not retain credit from a different user limit.
            let mut bucket = self.bucket.lock().unwrap_or_else(|e| e.into_inner());
            bucket.refilled = Instant::now();
            bucket.credit = 0.0;
            drop(bucket);
            // Whoever waits should see the new limit now, above all its removal.
            self.turn.notify_all();
        }
    }

    pub fn wait(&self, bytes: usize, cancel: &AtomicBool) -> Result<(), String> {
        let mut left = bytes;
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Err("Operation cancelled".into());
            }
            let rate = self.bytes_per_second();
            if rate == 0.0 {
                return Ok(());
            }
            if left == 0 {
                return Ok(());
            }
            // The share is re-read each turn: the limit can change while waiting.
            let share = ((rate / RATE_TURNS_PER_SECOND) as usize).max(RATE_MIN_TURN);
            let turn = left.min(share);
            self.wait_turn(turn, cancel)?;
            left -= turn;
        }
    }

    /// Wait at the back of the line for `bytes` of credit.
    fn wait_turn(&self, bytes: usize, cancel: &AtomicBool) -> Result<(), String> {
        let wanted = bytes as f64;
        let mut bucket = self.bucket.lock().unwrap_or_else(|e| e.into_inner());
        let ticket = bucket.next_ticket;
        bucket.next_ticket = bucket.next_ticket.wrapping_add(1);
        bucket.line.push_back(ticket);
        let outcome = loop {
            if cancel.load(Ordering::Relaxed) {
                break Err("Operation cancelled".to_string());
            }
            let rate = self.bytes_per_second();
            if rate == 0.0 {
                break Ok(());
            }
            let pause = if bucket.line.front() == Some(&ticket) {
                let now = Instant::now();
                let earned = now.saturating_duration_since(bucket.refilled).as_secs_f64() * rate;
                // At most a second of credit is kept, or one whole turn.
                bucket.credit = (bucket.credit + earned).min(rate.max(wanted));
                bucket.refilled = now;
                if bucket.credit >= wanted {
                    bucket.credit -= wanted;
                    break Ok(());
                }
                Duration::from_secs_f64((wanted - bucket.credit) / rate).min(RATE_POLL)
            } else {
                RATE_POLL
            };
            bucket = match self.turn.wait_timeout(bucket, pause) {
                Ok((bucket, _)) => bucket,
                Err(poisoned) => poisoned.into_inner().0,
            };
        };
        bucket.line.retain(|waiting| *waiting != ticket);
        drop(bucket);
        self.turn.notify_all();
        outcome
    }

    /// Display-only. The failing file owns its cancellable retry wait. Healthy
    /// workers and new files keep using the configured bandwidth and concurrency.
    pub fn retrying(&self, delay: Duration, reason: &str) {
        let mut status = self.retry_status.lock().unwrap();
        let until = Instant::now() + delay;
        if status
            .as_ref()
            .is_none_or(|(previous, _)| until >= *previous)
        {
            *status = Some((until, reason.into()));
        }
    }

    pub fn explanation(&self) -> Option<String> {
        self.retry_status
            .lock()
            .unwrap()
            .as_ref()
            .filter(|(until, _)| *until > Instant::now())
            .map(|(_, reason)| format!("Retrying failed I/O · {reason}"))
    }

    pub fn refund(&self, bytes: usize) {
        if bytes > 0 && self.mib.load(Ordering::Relaxed) > 0 {
            self.bucket.lock().unwrap_or_else(|e| e.into_inner()).credit += bytes as f64;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retry_notification_never_caps_or_pauses_other_files() {
        let limit = RateLimit::new(0);
        limit.retrying(Duration::from_secs(16), "transient write failure");
        assert!(limit
            .explanation()
            .unwrap()
            .starts_with("Retrying failed I/O"));
        let started = Instant::now();
        limit
            .wait(64 * 1024 * 1024, &AtomicBool::new(false))
            .unwrap();
        assert!(started.elapsed() < Duration::from_millis(100));
        limit.update(1);
        let started = Instant::now();
        limit.wait(256 * 1024, &AtomicBool::new(false)).unwrap();
        assert!(
            (Duration::from_millis(200)..Duration::from_millis(800)).contains(&started.elapsed())
        );
        limit.update(0);
        let started = Instant::now();
        limit
            .wait(64 * 1024 * 1024, &AtomicBool::new(false))
            .unwrap();
        assert!(started.elapsed() < Duration::from_millis(100));
    }
    #[test]
    fn bandwidth_budget_is_paced_and_cancellation_is_observed() {
        let limit = RateLimit::new(1);
        let cancel = AtomicBool::new(false);
        let started = Instant::now();
        limit.wait(256 * 1024, &cancel).unwrap();
        assert!(started.elapsed() >= Duration::from_millis(200));
        cancel.store(true, Ordering::Relaxed);
        assert_eq!(
            limit.wait(4 * 1024 * 1024, &cancel).unwrap_err(),
            "Operation cancelled"
        );
        cancel.store(false, Ordering::Relaxed);
        limit.update(0);
        limit.wait(usize::MAX, &cancel).unwrap();
    }

    /// The cache is one per process: tests that count what is in it take turns.
    static POOL_TESTS: Mutex<()> = Mutex::new(());

    fn cached_buffers_of(capacity: usize) -> usize {
        let state = ALLOCATION.lock().unwrap();
        state
            .pool
            .iter()
            .filter(|buffer| buffer.capacity() == capacity)
            .count()
    }

    /// A folder of files that each fit in one buffer used to leave thirty-two
    /// buffers of sizes nobody asks for again, and the cache then refused the
    /// copy buffers of every later transfer for as long as the service ran.
    #[test]
    fn a_full_cache_makes_room_for_the_sizes_in_use() {
        let _turn = POOL_TESTS.lock().unwrap_or_else(|e| e.into_inner());
        for file in 0..MAX_POOLED_BUFFERS + 8 {
            drop(PooledBuffer::acquire(64 * 1024 + 4096 * (file + 1)));
        }
        let copy_buffer = 3 * 1024 * 1024 + 8192;
        drop(PooledBuffer::acquire(copy_buffer));
        assert_eq!(
            cached_buffers_of(copy_buffer),
            1,
            "the buffer in use was not kept"
        );
        let reused = PooledBuffer::acquire(copy_buffer);
        assert_eq!(reused.len(), copy_buffer);
        assert_eq!(cached_buffers_of(copy_buffer), 0, "it was not handed out");
        let state = ALLOCATION.lock().unwrap();
        assert!(state.pool.len() <= MAX_POOLED_BUFFERS);
        assert!(state.pooled <= POOL_BYTES);
        assert_eq!(
            state.pooled,
            state.pool.iter().map(Vec::capacity).sum::<usize>()
        );
    }

    #[test]
    fn a_buffer_cut_to_one_file_is_not_cached() {
        let _turn = POOL_TESTS.lock().unwrap_or_else(|e| e.into_inner());
        let file_len = 777_777;
        let buffer = PooledBuffer::exact(file_len);
        assert_eq!(buffer.len(), file_len);
        drop(buffer);
        assert_eq!(cached_buffers_of(file_len), 0);
    }

    /// With a limit of a few MiB per second, a large file asks for whole
    /// buffers while small files ask for their few kilobytes. Each keeps
    /// moving: the large file is not left standing until the small ones are
    /// done, and the small ones do not wait out every large buffer.
    #[test]
    fn large_and_small_requests_share_a_limit() {
        let limit = RateLimit::new(16);
        let stop = AtomicBool::new(false);
        let large_bytes = AtomicU64::new(0);
        let small_bytes = AtomicU64::new(0);
        let started = Instant::now();
        let mut small_before = 0;
        std::thread::scope(|scope| {
            for _ in 0..4 {
                scope.spawn(|| {
                    while limit.wait(64 * 1024, &stop).is_ok() {
                        small_bytes.fetch_add(64 * 1024, Ordering::Relaxed);
                    }
                });
            }
            // The small requests are already taking the credit as it appears.
            std::thread::sleep(Duration::from_millis(100));
            small_before = small_bytes.load(Ordering::Relaxed);
            scope.spawn(|| {
                while limit.wait(4 * 1024 * 1024, &stop).is_ok() {
                    large_bytes.fetch_add(4 * 1024 * 1024, Ordering::Relaxed);
                }
            });
            std::thread::sleep(Duration::from_millis(1400));
            stop.store(true, Ordering::Relaxed);
        });
        let elapsed = started.elapsed().as_secs_f64();
        let large = large_bytes.load(Ordering::Relaxed);
        let small = small_bytes.load(Ordering::Relaxed);
        assert!(
            large >= 4 * 1024 * 1024,
            "the large request got {large} bytes, the small ones {small}"
        );
        assert!(
            small - small_before >= 1024 * 1024,
            "the small requests got {} bytes beside the large one",
            small - small_before
        );
        // One second of credit may be spent at once; the rest is paced.
        let ceiling = (elapsed + 1.0) * 16.0 * 1048576.0 + 4.0 * 1048576.0;
        assert!(
            ((large + small) as f64) <= ceiling,
            "{} bytes in {elapsed:.2}s exceeds the limit",
            large + small
        );
    }

    #[test]
    fn a_waiting_request_is_released_when_the_limit_is_removed() {
        let limit = RateLimit::new(1);
        let cancel = AtomicBool::new(false);
        let started = Instant::now();
        std::thread::scope(|scope| {
            let waiter = scope.spawn(|| limit.wait(64 * 1024 * 1024, &cancel));
            std::thread::sleep(Duration::from_millis(50));
            limit.update(0);
            waiter.join().unwrap().unwrap();
        });
        assert!(started.elapsed() < Duration::from_secs(5));
    }

    #[test]
    fn pool_reuses_only_exact_capacity_and_stays_byte_bounded() {
        let _turn = POOL_TESTS.lock().unwrap_or_else(|e| e.into_inner());
        drop(PooledBuffer::acquire(8 * 1024 * 1024));
        let small = PooledBuffer::acquire(64 * 1024);
        assert_eq!(small.data.capacity(), 64 * 1024);
        let buffers: Vec<_> = (0..40)
            .map(|_| PooledBuffer::acquire(2 * 1024 * 1024))
            .collect();
        drop(buffers);
        let state = ALLOCATION.lock().unwrap();
        assert!(state.pooled <= POOL_BYTES);
        assert!(state.pool.len() <= MAX_POOLED_BUFFERS);
        assert_eq!(
            state.pooled,
            state.pool.iter().map(Vec::capacity).sum::<usize>()
        );
    }

    #[test]
    fn job_budget_waits_cancellably_and_rejects_stale_large_reservations() {
        let budget = MemoryBudget::new(1024);
        let cancel = AtomicBool::new(false);
        let permit = Permit::acquire_for_job(1024, budget.clone(), &cancel).unwrap();
        assert_eq!(budget.used.load(Ordering::Relaxed), 1024);
        std::thread::scope(|scope| {
            let cancel = &cancel;
            scope.spawn(move || {
                std::thread::sleep(Duration::from_millis(40));
                cancel.store(true, Ordering::Relaxed);
            });
            let error = Permit::acquire_for_job(1, budget.clone(), cancel)
                .err()
                .unwrap();
            assert_eq!(error, "Operation cancelled");
        });
        drop(permit);
        assert_eq!(budget.used.load(Ordering::Relaxed), 0);
        budget.update(512);
        cancel.store(false, Ordering::Relaxed);
        assert!(Permit::acquire_for_job(1024, budget, &cancel)
            .err()
            .unwrap()
            .starts_with("RETRYABLE:"));
    }
}
