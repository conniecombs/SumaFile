//! Process-wide worker and buffer limits; per-job cancellable bandwidth pacing.
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

static ALLOCATION: Mutex<(usize, usize)> = Mutex::new((0, 0));
static BUFFER_POOL: Mutex<Vec<Vec<u8>>> = Mutex::new(Vec::new());
const MAX_POOLED_BUFFERS: usize = 32;

/// Recycled transfer buffer that returns its backing allocation to a bounded
/// process-wide pool on drop (`rclone` `lib/pool` style), avoiding repeated
/// multi-megabyte heap allocations and page faults across files.
pub(crate) struct PooledBuffer(Vec<u8>);

impl PooledBuffer {
    pub fn acquire(size: usize) -> Self {
        let mut buf = BUFFER_POOL
            .lock()
            .ok()
            .and_then(|mut pool| {
                pool.iter()
                    .position(|b| b.capacity() >= size)
                    .map(|idx| pool.swap_remove(idx))
                    .or_else(|| pool.pop())
            })
            .unwrap_or_default();
        if buf.len() != size {
            buf.resize(size, 0);
        }
        Self(buf)
    }

    pub fn as_mut_slice(&mut self) -> &mut [u8] {
        &mut self.0
    }
}

impl std::ops::Deref for PooledBuffer {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        &self.0
    }
}

impl std::ops::DerefMut for PooledBuffer {
    fn deref_mut(&mut self) -> &mut [u8] {
        &mut self.0
    }
}

impl Drop for PooledBuffer {
    fn drop(&mut self) {
        if (64 * 1024..=64 * 1024 * 1024).contains(&self.0.capacity()) {
            if let Ok(mut pool) = BUFFER_POOL.lock() {
                if pool.len() < MAX_POOLED_BUFFERS {
                    pool.push(std::mem::take(&mut self.0));
                }
            }
        }
    }
}

pub(crate) struct Permit(usize);

impl Permit {
    pub fn acquire(bytes: usize, cancel: &AtomicBool) -> Result<Self, String> {
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Err("Operation cancelled".into());
            }
            {
                let mut allocation = ALLOCATION.lock().map_err(|e| e.to_string())?;
                if allocation.0 < 32 && allocation.1.saturating_add(bytes) <= 512 * 1024 * 1024 {
                    allocation.0 += 1;
                    allocation.1 += bytes;
                    return Ok(Self(bytes));
                }
            }
            std::thread::sleep(Duration::from_millis(25));
        }
    }
}

impl Drop for Permit {
    fn drop(&mut self) {
        if let Ok(mut allocation) = ALLOCATION.lock() {
            allocation.0 -= 1;
            allocation.1 -= self.0;
        }
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
    fn pooled_buffer_reuses_backing_allocation_across_acquires() {
        let ptr_first = {
            let mut buf = PooledBuffer::acquire(256 * 1024);
            buf.as_mut_slice()[0] = 42;
            buf.as_ptr() as usize
        };
        let buf_second = PooledBuffer::acquire(256 * 1024);
        assert_eq!(buf_second.len(), 256 * 1024);
        assert_eq!(buf_second.as_ptr() as usize, ptr_first);
    }
}
