//! Shared, bounded recovery for filesystem and native remote transfers.
use std::fmt;
use std::io;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

#[derive(Clone, Debug)]
pub struct Failure {
    pub message: String,
    pub retryable: bool,
}

impl Failure {
    pub fn new(message: impl Into<String>, retryable: bool) -> Self {
        Self {
            message: message.into(),
            retryable,
        }
    }

    pub fn io(phase: &str, path: impl AsRef<Path>, error: io::Error) -> Self {
        let retryable = error
            .get_ref()
            .and_then(|e| e.downcast_ref::<Self>())
            .map_or_else(|| transient_io(&error), |e| e.retryable);
        Self::new(
            format!("{phase} [{}]: {error}", path.as_ref().display()),
            retryable,
        )
    }

    pub fn context(self, phase: &str, path: &str) -> Self {
        Self::new(
            format!("{phase} [{path}]: {}", self.message),
            self.retryable,
        )
    }
}

impl fmt::Display for Failure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}
impl std::error::Error for Failure {}
impl From<String> for Failure {
    fn from(message: String) -> Self {
        Self::new(message, false)
    }
}
impl From<&str> for Failure {
    fn from(message: &str) -> Self {
        Self::new(message, false)
    }
}
impl From<Failure> for String {
    fn from(error: Failure) -> Self {
        error.message
    }
}

fn transient_io(error: &io::Error) -> bool {
    // Do not retry conflicts, invalid paths, permissions, missing files, or data corruption.
    if matches!(
        error.kind(),
        io::ErrorKind::TimedOut
            | io::ErrorKind::WouldBlock
            | io::ErrorKind::Interrupted
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::ConnectionAborted
            | io::ErrorKind::NotConnected
            | io::ErrorKind::BrokenPipe
            | io::ErrorKind::StorageFull
    ) {
        return true;
    }
    #[cfg(windows)]
    if matches!(
        error.raw_os_error(),
        Some(21 | 32 | 33 | 54 | 64 | 121 | 170 | 995 | 1117 | 1231 | 1450 | 1453)
    ) {
        return true;
    }
    false
}

/// Configurable exponential-backoff pacer (`rclone` `lib/pacer` style).
/// Defaults to six attempts with a 1-second base interval (at most 31 seconds of
/// deliberate waiting), or honors user `TransferPolicy` (`--low-level-retries`
/// and `--retries-sleep`).
pub(crate) struct Retry {
    first: Option<Failure>,
    pub failures: u32,
    max_attempts: u32,
    base_sleep: Duration,
}

impl Default for Retry {
    fn default() -> Self {
        Self {
            first: None,
            failures: 0,
            max_attempts: 6,
            base_sleep: Duration::from_secs(1),
        }
    }
}

impl Retry {
    pub fn with_limits(max_attempts: u32, base_sleep: Duration) -> Self {
        Self {
            first: None,
            failures: 0,
            max_attempts: max_attempts.clamp(1, 50),
            base_sleep: base_sleep.clamp(Duration::from_millis(10), Duration::from_secs(60)),
        }
    }

    pub fn from_policy(policy: &crate::progress::Policy) -> Self {
        Self::with_limits(policy.low_level_retry_limit(), policy.retry_sleep())
    }

    pub fn failed(&mut self, error: Failure) -> Result<Duration, String> {
        self.failures += 1;
        let first = self.first.get_or_insert_with(|| error.clone());
        log::warn!("Transfer attempt {}: {}", self.failures, error);
        if !error.retryable || self.failures >= self.max_attempts {
            return Err(if self.failures == 1 {
                first.to_string()
            } else {
                format!(
                    "{}; after {} attempts, last failure: {}",
                    first, self.failures, error
                )
            });
        }
        let shift = (self.failures - 1).min(4);
        let factor = 1u32 << shift;
        Ok(self
            .base_sleep
            .saturating_mul(factor)
            .min(Duration::from_secs(60)))
    }
}

pub(crate) fn wait(duration: Duration, cancel: &AtomicBool) -> Result<(), String> {
    let end = Instant::now() + duration;
    loop {
        if cancel.load(Ordering::Relaxed) {
            return Err("Operation cancelled".into());
        }
        let left = end.saturating_duration_since(Instant::now());
        if left.is_zero() {
            return Ok(());
        }
        std::thread::sleep(left.min(Duration::from_millis(25)));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn first_error_survives_permanent_retry_failure() {
        let mut retry = Retry::default();
        assert!(retry
            .failed(Failure::io(
                "Read source",
                "cloud.jpg",
                io::Error::from(io::ErrorKind::TimedOut)
            ))
            .is_ok());
        let error = retry
            .failed(Failure::io(
                "Create temporary destination",
                "partial",
                io::Error::from(io::ErrorKind::AlreadyExists),
            ))
            .unwrap_err();
        assert!(error.starts_with("Read source [cloud.jpg]"));
        assert!(error.contains("last failure: Create temporary destination [partial]"));
    }
    #[test]
    fn retry_is_bounded_and_permanent_errors_fail_immediately() {
        for kind in [
            io::ErrorKind::PermissionDenied,
            io::ErrorKind::NotFound,
            io::ErrorKind::AlreadyExists,
            io::ErrorKind::InvalidData,
        ] {
            assert!(Retry::default()
                .failed(Failure::io("Read", "file", io::Error::from(kind)))
                .is_err());
        }
        let mut retry = Retry::default();
        let mut delay = Duration::ZERO;
        for _ in 0..5 {
            delay += retry.failed(Failure::new("timeout", true)).unwrap();
        }
        assert_eq!(delay, Duration::from_secs(31));
        assert!(retry.failed(Failure::new("timeout", true)).is_err());
        assert!(wait(Duration::from_secs(16), &AtomicBool::new(true)).is_err());
    }

    #[test]
    fn retry_with_limits_honors_custom_attempts_and_backoff() {
        let mut retry = Retry::with_limits(3, Duration::from_millis(50));
        assert_eq!(
            retry.failed(Failure::new("transient", true)).unwrap(),
            Duration::from_millis(50)
        );
        assert_eq!(
            retry.failed(Failure::new("transient", true)).unwrap(),
            Duration::from_millis(100)
        );
        let err = retry.failed(Failure::new("transient", true)).unwrap_err();
        assert!(err.contains("after 3 attempts"));
    }
}
