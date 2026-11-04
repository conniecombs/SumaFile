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

/// Six attempts, at most 31 seconds of deliberate waiting. Each provider also
/// bounds its network calls; a synchronous OS filesystem call cannot be interrupted.
#[derive(Default)]
pub(crate) struct Retry {
    first: Option<Failure>,
    pub failures: u32,
}
impl Retry {
    pub fn failed(&mut self, error: Failure) -> Result<Duration, String> {
        self.failures += 1;
        let first = self.first.get_or_insert_with(|| error.clone());
        log::warn!("Transfer attempt {}: {}", self.failures, error);
        if !error.retryable || self.failures >= 6 {
            return Err(if self.failures == 1 {
                first.to_string()
            } else {
                format!(
                    "{}; after {} attempts, last failure: {}",
                    first, self.failures, error
                )
            });
        }
        Ok(Duration::from_secs(1 << (self.failures - 1)))
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
}
