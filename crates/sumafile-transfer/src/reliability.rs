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
    /// The bytes already in the destination can no longer be trusted: a
    /// write or a flush failed, or the checksum did not match. Another
    /// attempt at this file must write it again from its first byte instead
    /// of continuing where the failed attempt stopped.
    pub restart: bool,
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
                restart: false,
            };
        }
        let kind = if kind == FailureKind::Permanent && message.starts_with(CONFLICT_PREFIX) {
            FailureKind::Conflict
        } else if kind == FailureKind::Permanent && message == CANCELLED_MESSAGE {
            FailureKind::Cancelled
        } else {
            kind
        };
        Self {
            message,
            kind,
            restart: false,
        }
    }

    /// Marks the failure as one that leaves the destination's bytes in
    /// doubt; see [`Self::restart`].
    pub fn restart_file(mut self) -> Self {
        self.restart = true;
        self
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
        let inner = error.get_ref().and_then(|e| e.downcast_ref::<Self>());
        let kind = inner.map_or_else(|| classify_io(&error), |e| e.kind);
        let restart = inner.is_some_and(|e| e.restart);
        let mut failure = Self::with_kind(
            format!("{phase} [{}]: {error}", path.as_ref().display()),
            kind,
        );
        failure.restart = restart;
        failure
    }

    pub fn context(self, phase: &str, path: &str) -> Self {
        let mut failure = Self::with_kind(format!("{phase} [{path}]: {}", self.message), self.kind);
        failure.restart = self.restart;
        failure
    }

    /// The failure to report once the clean-up that followed it has run.
    ///
    /// A clean-up that failed is part of the report: the partial file is
    /// still there. A conflict or a cancellation keeps its message, which
    /// the app reads; there the clean-up's failure is logged.
    pub fn after_cleanup(self, cleanup: Result<(), String>) -> Self {
        let Err(cleanup) = cleanup else {
            return self;
        };
        if matches!(self.kind, FailureKind::Conflict | FailureKind::Cancelled) {
            log::warn!("Clean-up after \"{}\" failed: {cleanup}", self.message);
            return self;
        }
        let mut failure = Self::with_kind(format!("{}; {cleanup}", self.message), self.kind);
        failure.restart = self.restart;
        failure
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
pub struct Retry {
    first: Option<Failure>,
    pub failures: u32,
    max_attempts: u32,
    base_sleep: Duration,
    /// Attempts a `Busy` failure gets; [`BUSY_ATTEMPTS`] unless the caller is
    /// willing to wait a file out (see [`Self::patient_with_busy`]).
    busy_attempts: u32,
    /// A retry of this file has been told to the scheduler.
    reported: bool,
}

impl Default for Retry {
    fn default() -> Self {
        Self {
            first: None,
            failures: 0,
            max_attempts: 6,
            base_sleep: Duration::from_secs(1),
            busy_attempts: BUSY_ATTEMPTS,
            reported: false,
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
            busy_attempts: BUSY_ATTEMPTS,
            reported: false,
        }
    }

    /// True the first time only: whether a retry of this file is the first
    /// the scheduler hears of. Its later retries are about the file; the
    /// first is evidence about the transport.
    pub fn first_report(&mut self) -> bool {
        !std::mem::replace(&mut self.reported, true)
    }

    /// Gives a `Busy` failure the full attempt budget instead of the quick
    /// [`BUSY_ATTEMPTS`]: for the rename that publishes a fully copied file, a
    /// scanner or indexer holding it for a while is worth waiting out rather
    /// than throwing the copy away.
    pub fn patient_with_busy(mut self) -> Self {
        self.busy_attempts = self.max_attempts;
        self
    }

    pub fn from_policy(policy: &crate::policy::Policy) -> Self {
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
            self.busy_attempts.min(self.max_attempts)
        } else {
            self.max_attempts
        };
        if !error.kind.is_retryable() || self.failures >= limit {
            let kind = error.kind;
            let restart = error.restart;
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
            let mut failure = Failure::with_kind(message, kind);
            failure.restart = restart;
            return Err(failure);
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

pub fn wait(duration: Duration, cancel: &AtomicBool) -> Result<(), String> {
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
    fn a_patient_retry_gives_busy_files_the_whole_budget() {
        let mut retry = Retry::with_limits(10, Duration::from_millis(10)).patient_with_busy();
        let busy = || Failure::with_kind("sharing violation", FailureKind::Busy);
        for _ in 0..9 {
            assert!(retry.failed_with(busy()).is_ok());
        }
        let reported = retry.failed_with(busy()).unwrap_err();
        assert_eq!(reported.kind, FailureKind::Busy);
        assert!(
            reported.message.contains("after 10 attempts"),
            "{}",
            reported.message
        );
    }

    #[test]
    fn a_failure_asks_for_a_restart_only_when_marked() {
        assert!(!Failure::new("timeout", true).restart);
        assert!(!Failure::from("boom").restart);
        assert!(
            !Failure::io(
                "Read source",
                "a.bin",
                io::Error::from(io::ErrorKind::TimedOut)
            )
            .restart
        );
        let marked = Failure::new("checksum mismatch", true).restart_file();
        assert!(marked.restart);
        assert_eq!(marked.kind, FailureKind::Retryable);
        assert_eq!(marked.message, "checksum mismatch");
    }

    #[test]
    fn the_restart_mark_survives_added_context_and_an_io_wrapper() {
        let marked = Failure::new("write failed", true).restart_file();
        assert!(marked.clone().context("Copy", "a.bin").restart);
        let wrapped = io::Error::other(marked);
        assert!(Failure::io("Write", "a.bin", wrapped).restart);
    }

    /// The clean-up after a failed final rename was discarded: the report
    /// did not say that the partial file was still there.
    #[test]
    fn a_failed_clean_up_is_part_of_the_report() {
        let failure = Failure::new("Finalize destination [a.bin]: access denied", false);
        assert_eq!(
            failure.clone().after_cleanup(Ok(())).message,
            "Finalize destination [a.bin]: access denied"
        );
        let reported = failure.after_cleanup(Err("partial file is in use".into()));
        assert_eq!(
            reported.message,
            "Finalize destination [a.bin]: access denied; partial file is in use"
        );
        assert_eq!(reported.kind, FailureKind::Permanent);

        let marked = Failure::new("write failed", true)
            .restart_file()
            .after_cleanup(Err("left behind".into()));
        assert!(marked.restart);
        assert_eq!(marked.kind, FailureKind::Retryable);
    }

    /// The app reads the message of a conflict and of a cancellation, so
    /// nothing is added to it.
    #[test]
    fn a_failed_clean_up_leaves_a_conflict_and_a_cancellation_as_they_are() {
        let conflict = Failure::conflict("destination already exists: x")
            .after_cleanup(Err("partial file is in use".into()));
        assert_eq!(conflict.message, "CONFLICT: destination already exists: x");
        assert!(conflict.is_conflict());
        let cancelled = Failure::cancelled().after_cleanup(Err("partial file is in use".into()));
        assert_eq!(cancelled.message, CANCELLED_MESSAGE);
        assert!(cancelled.is_cancelled());
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
