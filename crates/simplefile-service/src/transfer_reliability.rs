//! Shared, bounded recovery for filesystem and native remote transfers.
use std::fmt;
use std::io;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

/// How a failure should be treated by retry loops, the coordinator and the UI.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FailureKind {
    /// Not worth retrying; the file is reported and the rest of the job continues.
    Permanent,
    /// Transient I/O; bounded low-level retries, and one high-level retry pass later.
    Retryable,
    /// Another process holds the file (sharing/lock violation): a few quick retries,
    /// never counted as evidence of transport contention.
    Busy,
    /// The destination already exists (or has a different type); needs a user decision.
    Conflict,
    /// The user cancelled the operation.
    Cancelled,
}

impl FailureKind {
    pub fn is_retryable(self) -> bool {
        matches!(self, Self::Retryable | Self::Busy)
    }
}

/// Attempts allowed for a `Busy` failure before it is reported.
pub const BUSY_ATTEMPTS: u32 = 3;
pub const CANCELLED_MESSAGE: &str = "Operation cancelled";
pub const CONFLICT_PREFIX: &str = "CONFLICT:";
/// Legacy marker used by resource errors that should be retried at a higher level.
pub const RETRYABLE_PREFIX: &str = "RETRYABLE:";

#[derive(Clone, Debug)]
pub struct Failure {
    pub message: String,
    pub kind: FailureKind,
}

impl Failure {
    pub fn new(message: impl Into<String>, retryable: bool) -> Self {
        Self::with_kind(
            message,
            if retryable {
                FailureKind::Retryable
            } else {
                FailureKind::Permanent
            },
        )
    }

    pub fn with_kind(message: impl Into<String>, kind: FailureKind) -> Self {
        let message = message.into();
        if let Some(rest) = message.strip_prefix(RETRYABLE_PREFIX) {
            return Self {
                message: rest.trim().to_string(),
                kind: FailureKind::Retryable,
            };
        }
        let kind = if kind == FailureKind::Permanent && message.starts_with(CONFLICT_PREFIX) {
            FailureKind::Conflict
        } else if kind == FailureKind::Permanent && message == CANCELLED_MESSAGE {
            FailureKind::Cancelled
        } else {
            kind
        };
        Self { message, kind }
    }

    pub fn cancelled() -> Self {
        Self::with_kind(CANCELLED_MESSAGE, FailureKind::Cancelled)
    }

    pub fn conflict(message: impl Into<String>) -> Self {
        let message = message.into();
        let message = if message.starts_with(CONFLICT_PREFIX) {
            message
        } else {
            format!("{CONFLICT_PREFIX} {message}")
        };
        Self::with_kind(message, FailureKind::Conflict)
    }

    pub fn io(phase: &str, path: impl AsRef<Path>, error: io::Error) -> Self {
        let kind = error
            .get_ref()
            .and_then(|e| e.downcast_ref::<Self>())
            .map_or_else(|| classify_io(&error), |e| e.kind);
        Self::with_kind(
            format!("{phase} [{}]: {error}", path.as_ref().display()),
            kind,
        )
    }

    pub fn context(self, phase: &str, path: &str) -> Self {
        Self::with_kind(format!("{phase} [{path}]: {}", self.message), self.kind)
    }

    /// Kept for call sites that only distinguish "try again" from "give up".
    pub fn retryable(&self) -> bool {
        self.kind.is_retryable()
    }

    pub fn is_cancelled(&self) -> bool {
        self.kind == FailureKind::Cancelled
    }

    pub fn is_conflict(&self) -> bool {
        self.kind == FailureKind::Conflict
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
        Self::with_kind(message, FailureKind::Permanent)
    }
}
impl From<&str> for Failure {
    fn from(message: &str) -> Self {
        Self::with_kind(message, FailureKind::Permanent)
    }
}
impl From<Failure> for String {
    fn from(error: Failure) -> Self {
        error.message
    }
}

fn classify_io(error: &io::Error) -> FailureKind {
    #[cfg(windows)]
    if matches!(error.raw_os_error(), Some(32 | 33)) {
        return FailureKind::Busy;
    }
    if error.kind() == io::ErrorKind::AlreadyExists {
        return FailureKind::Conflict;
    }
    if transient_io(error) {
        FailureKind::Retryable
    } else {
        FailureKind::Permanent
    }
}

fn transient_io(error: &io::Error) -> bool {
    // Do not retry conflicts, invalid paths, permissions, missing files, data
    // corruption, or a full volume: none of those clear up by waiting.
    if matches!(
        error.kind(),
        io::ErrorKind::TimedOut
            | io::ErrorKind::WouldBlock
            | io::ErrorKind::Interrupted
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::ConnectionAborted
            | io::ErrorKind::NotConnected
            | io::ErrorKind::BrokenPipe
    ) {
        return true;
    }
    #[cfg(windows)]
    if matches!(
        error.raw_os_error(),
        Some(21 | 54 | 64 | 121 | 170 | 995 | 1117 | 1231 | 1450 | 1453)
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

    /// Returns the delay before the next attempt, or the failure to report. The
    /// returned failure keeps the kind of the last attempt, so a conflict or a
    /// cancellation that follows a transient error is still recognised as one.
    pub fn failed_with(&mut self, error: Failure) -> Result<Duration, Failure> {
        self.failures += 1;
        let first = self.first.get_or_insert_with(|| error.clone()).clone();
        log::warn!("Transfer attempt {}: {}", self.failures, error);
        let limit = if error.kind == FailureKind::Busy {
            BUSY_ATTEMPTS.min(self.max_attempts)
        } else {
            self.max_attempts
        };
        if !error.kind.is_retryable() || self.failures >= limit {
            let kind = error.kind;
            let message = if self.failures == 1
                || matches!(kind, FailureKind::Conflict | FailureKind::Cancelled)
            {
                error.message
            } else {
                format!(
                    "{}; after {} attempts, last failure: {}",
                    first, self.failures, error
                )
            };
            return Err(Failure::with_kind(message, kind));
        }
        let shift = (self.failures - 1).min(4);
        let factor = 1u32 << shift;
        Ok(self
            .base_sleep
            .saturating_mul(factor)
            .min(Duration::from_secs(60)))
    }

    pub fn failed(&mut self, error: Failure) -> Result<Duration, String> {
        self.failed_with(error).map_err(|failure| failure.message)
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
            .failed_with(Failure::io(
                "Create temporary destination",
                "partial",
                io::Error::from(io::ErrorKind::PermissionDenied),
            ))
            .unwrap_err();
        assert_eq!(error.kind, FailureKind::Permanent);
        assert!(error.message.starts_with("Read source [cloud.jpg]"));
        assert!(error
            .message
            .contains("last failure: Create temporary destination [partial]"));
    }

    #[test]
    fn conflicts_and_cancellation_keep_their_kind_and_message_after_retries() {
        let mut retry = Retry::default();
        assert!(retry.failed_with(Failure::new("timeout", true)).is_ok());
        let conflict = retry
            .failed_with(Failure::conflict("destination already exists: x"))
            .unwrap_err();
        assert_eq!(conflict.kind, FailureKind::Conflict);
        assert_eq!(conflict.message, "CONFLICT: destination already exists: x");
        assert!(conflict.message.starts_with(CONFLICT_PREFIX));
        let mut retry = Retry::default();
        assert!(retry.failed_with(Failure::new("timeout", true)).is_ok());
        let cancelled = retry.failed_with(Failure::cancelled()).unwrap_err();
        assert_eq!(cancelled.kind, FailureKind::Cancelled);
        assert_eq!(cancelled.message, CANCELLED_MESSAGE);
    }

    #[test]
    fn busy_failures_get_three_attempts_and_stay_busy() {
        let mut retry = Retry::with_limits(10, Duration::from_millis(10));
        let busy = || Failure::with_kind("sharing violation", FailureKind::Busy);
        assert!(retry.failed_with(busy()).is_ok());
        assert!(retry.failed_with(busy()).is_ok());
        let reported = retry.failed_with(busy()).unwrap_err();
        assert_eq!(reported.kind, FailureKind::Busy);
        assert!(reported.message.contains("after 3 attempts"));
    }

    #[test]
    fn message_prefixes_select_kinds() {
        assert_eq!(
            Failure::from("CONFLICT: destination already exists: y").kind,
            FailureKind::Conflict
        );
        assert_eq!(
            Failure::from(CANCELLED_MESSAGE.to_string()).kind,
            FailureKind::Cancelled
        );
        assert_eq!(Failure::from("boom").kind, FailureKind::Permanent);
        assert!(Failure::new("x", true).retryable());
        assert!(!Failure::new("x", false).retryable());
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

#[cfg(test)]
mod classification_tests {
    use super::{classify_io, FailureKind};

    /// A full destination volume does not recover by waiting: retrying it
    /// ten times with backoff only delays the report and halves concurrency.
    #[test]
    fn disk_full_is_permanent_not_transient() {
        let error = std::io::Error::from(std::io::ErrorKind::StorageFull);
        assert_eq!(classify_io(&error), FailureKind::Permanent);
    }
}
