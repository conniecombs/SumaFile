//! Process-wide active + cached buffer limits and per-job cancellable budgets.
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
    pool: Vec<Vec<u8>>,
}
static ALLOCATION: Mutex<Allocation> = Mutex::new(Allocation {
    workers: 0, active: 0, pooled: 0, pool: Vec::new(),
});
static AVAILABLE: Condvar = Condvar::new();

pub(crate) struct MemoryBudget {
    limit: AtomicUsize,
    used: AtomicUsize,
}
impl MemoryBudget {
    pub fn new(bytes: usize) -> Arc<Self> {
        Arc::new(Self { limit: AtomicUsize::new(bytes), used: AtomicUsize::new(0) })
    }
    pub fn update(&self, bytes: usize) {
        self.limit.store(bytes, Ordering::Relaxed);
        AVAILABLE.notify_all();
    }
}

/// Only exact-capacity buffers are reused. A small file cannot inherit a huge
/// allocation that was charged as a small reservation.
pub(crate) struct PooledBuffer(Vec<u8>);
impl PooledBuffer {
    pub fn acquire(size: usize) -> Self {
        let cached = ALLOCATION.lock().ok().and_then(|mut state| {
            let index = state.pool.iter().position(|buf| buf.capacity() == size)?;
            let buffer = state.pool.swap_remove(index);
            state.pooled -= buffer.capacity();
            Some(buffer)
        });
        Self(cached.unwrap_or_else(|| vec![0; size]))
    }
    pub fn as_mut_slice(&mut self) -> &mut [u8] { &mut self.0 }
}
impl std::ops::Deref for PooledBuffer {
    type Target = [u8];
    fn deref(&self) -> &[u8] { &self.0 }
}
impl std::ops::DerefMut for PooledBuffer {
    fn deref_mut(&mut self) -> &mut [u8] { &mut self.0 }
}
impl Drop for PooledBuffer {
    fn drop(&mut self) {
        let bytes = self.0.capacity();
        if !(64 * 1024..=POOL_BYTES).contains(&bytes) { return; }
        if let Ok(mut state) = ALLOCATION.lock() {
            // Active reservations still cover buffers being returned. Counting
            // both here is conservative and prevents a cache from exceeding the cap.
            if state.pool.len() < MAX_POOLED_BUFFERS
                && state.pooled + bytes <= POOL_BYTES
                && state.active + state.pooled + bytes <= GLOBAL_BYTES {
                state.pooled += bytes;
                state.pool.push(std::mem::take(&mut self.0));
            }
        }
    }
}

pub(crate) struct Permit {
    bytes: usize,
    budget: Option<Arc<MemoryBudget>>,
}
impl Permit {
    pub fn acquire(bytes: usize, cancel: &AtomicBool) -> Result<Self, String> {
        Self::acquire_inner(bytes, None, cancel)
    }
    pub fn acquire_for_job(bytes: usize, budget: Arc<MemoryBudget>, cancel: &AtomicBool) -> Result<Self, String> {
        Self::acquire_inner(bytes, Some(budget), cancel)
    }
    fn acquire_inner(bytes: usize, budget: Option<Arc<MemoryBudget>>, cancel: &AtomicBool) -> Result<Self, String> {
        if bytes > GLOBAL_BYTES { return Err("Transfer buffer reservation exceeds the service budget".into()); }
        let mut state = ALLOCATION.lock().map_err(|e| e.to_string())?;
        loop {
            if cancel.load(Ordering::Relaxed) { return Err("Operation cancelled".into()); }
            if budget.as_ref().is_some_and(|b| bytes > b.limit.load(Ordering::Relaxed)) {
                return Err("RETRYABLE:Transfer buffer budget changed; retry with current settings".into());
            }
            let fits_job = budget.as_ref().is_none_or(|b| {
                b.used.load(Ordering::Relaxed).saturating_add(bytes) <= b.limit.load(Ordering::Relaxed)
            });
            if fits_job && state.workers < 32 && state.active.saturating_add(bytes) <= GLOBAL_BYTES {
                while state.active + state.pooled + bytes > GLOBAL_BYTES {
                    if let Some(buffer) = state.pool.pop() { state.pooled -= buffer.capacity(); }
                    else { break; }
                }
                state.workers += 1;
                state.active += bytes;
                if let Some(b) = &budget { b.used.fetch_add(bytes, Ordering::Relaxed); }
                return Ok(Self { bytes, budget });
            }
            state = AVAILABLE.wait_timeout(state, Duration::from_millis(25)).map_err(|e| e.to_string())?.0;
        }
    }
}
impl Drop for Permit {
    fn drop(&mut self) {
        if let Ok(mut state) = ALLOCATION.lock() {
            state.workers -= 1;
            state.active -= self.bytes;
            if let Some(b) = &self.budget { b.used.fetch_sub(self.bytes, Ordering::Relaxed); }
        }
        AVAILABLE.notify_all();
    }
}

/// Only an explicit user bandwidth setting may limit bytes per second. Latency,
/// retry notifications, and free-space estimates must never create hidden caps.
pub(crate) struct RateLimit {
    mib: AtomicU64,
    bucket: Mutex<(Instant, f64)>,
    retry_status: Mutex<Option<(Instant, String)>>,
}

impl RateLimit {
    pub fn new(mib: usize) -> Self {
        Self {
            mib: AtomicU64::new(mib as u64),
            bucket: Mutex::new((Instant::now(), 0.0)),
            retry_status: Mutex::new(None),
        }
    }

    pub fn update(&self, mib: usize) {
        if self.mib.swap(mib as u64, Ordering::Relaxed) != mib as u64 {
            // Do not retain credit from a different user limit.
            *self.bucket.lock().unwrap() = (Instant::now(), 0.0);
        }
    }

    pub fn wait(&self, bytes: usize, cancel: &AtomicBool) -> Result<(), String> {
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Err("Operation cancelled".into());
            }
            let rate = self.mib.load(Ordering::Relaxed).min(1_048_576) as f64 * 1048576.0;
            if rate == 0.0 {
                return Ok(());
            }
            {
                let mut bucket = self.bucket.lock().map_err(|e| e.to_string())?;
                bucket.1 = (bucket.1 + bucket.0.elapsed().as_secs_f64() * rate)
                    .min(rate.max(bytes as f64));
                bucket.0 = Instant::now();
                if bucket.1 >= bytes as f64 {
                    bucket.1 -= bytes as f64;
                    return Ok(());
                }
            }
            std::thread::sleep(Duration::from_millis(25));
        }
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
            self.bucket.lock().unwrap().1 += bytes as f64;
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

    #[test]
    fn pool_reuses_only_exact_capacity_and_stays_byte_bounded() {
        drop(PooledBuffer::acquire(8 * 1024 * 1024));
        let small = PooledBuffer::acquire(64 * 1024);
        assert_eq!(small.0.capacity(), 64 * 1024);
        let buffers: Vec<_> = (0..40).map(|_| PooledBuffer::acquire(2 * 1024 * 1024)).collect();
        drop(buffers);
        let state = ALLOCATION.lock().unwrap();
        assert!(state.pooled <= POOL_BYTES);
        assert!(state.pool.len() <= MAX_POOLED_BUFFERS);
        assert_eq!(state.pooled, state.pool.iter().map(Vec::capacity).sum::<usize>());
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
            let error = Permit::acquire_for_job(1, budget.clone(), cancel).err().unwrap();
            assert_eq!(error, "Operation cancelled");
        });
        drop(permit);
        assert_eq!(budget.used.load(Ordering::Relaxed), 0);
        budget.update(512);
        cancel.store(false, Ordering::Relaxed);
        assert!(Permit::acquire_for_job(1024, budget, &cancel).err().unwrap().starts_with("RETRYABLE:"));
    }
}
