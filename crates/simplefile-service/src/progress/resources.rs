//! Process-wide worker and buffer limits; per-job cancellable bandwidth pacing.
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

static ALLOCATION: Mutex<(usize, usize)> = Mutex::new((0, 0));

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

pub(crate) struct RateLimit {
    mib: AtomicU64,
    bucket: Mutex<(Instant, f64)>,
}

impl RateLimit {
    pub fn new(mib: usize) -> Self {
        Self {
            mib: AtomicU64::new(mib as u64),
            bucket: Mutex::new((Instant::now(), 0.0)),
        }
    }

    pub fn update(&self, mib: usize) {
        self.mib.store(mib as u64, Ordering::Relaxed);
    }

    pub fn wait(&self, bytes: usize, cancel: &AtomicBool) -> Result<(), String> {
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Err("Operation cancelled".into());
            }
            let rate = self.mib.load(Ordering::Relaxed).min(1_048_576) as f64 * 1024.0 * 1024.0;
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
}

#[cfg(test)]
mod tests {
    use super::*;
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
}
