//! The folder size and item count requests of a session that are still
//! running, so that each can be cancelled.
//!
//! The session used to keep one cancel flag per kind of request, replaced by
//! each new request. Of two requests running at once only the newer could be
//! cancelled: a cancel meant for the older one stopped the newer, and the
//! older ran on, also after the client had gone.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Weak};

/// What a request works out. `get_folder_metrics` works out both.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct Metrics(u8);

impl Metrics {
    pub(crate) const SIZE: Metrics = Metrics(1);
    pub(crate) const COUNT: Metrics = Metrics(2);
    pub(crate) const BOTH: Metrics = Metrics(3);

    fn overlaps(self, other: Metrics) -> bool {
        self.0 & other.0 != 0
    }
}

struct Job {
    path: String,
    metrics: Metrics,
    /// The job holds the flag; when it ends the flag is gone and so is the job.
    cancel: Weak<AtomicBool>,
}

#[derive(Default)]
pub struct FolderJobs {
    jobs: Vec<Job>,
}

impl FolderJobs {
    /// Registers a request and returns the flag its job watches.
    pub(crate) fn start(&mut self, path: &str, metrics: Metrics) -> Arc<AtomicBool> {
        self.jobs.retain(|job| job.cancel.strong_count() > 0);
        let cancel = Arc::new(AtomicBool::new(false));
        self.jobs.push(Job {
            path: path.to_string(),
            metrics,
            cancel: Arc::downgrade(&cancel),
        });
        cancel
    }

    /// Cancels the running requests that work out any of `metrics`: those for
    /// `path` when one is given, all of them otherwise. Returns how many.
    pub(crate) fn cancel(&mut self, metrics: Metrics, path: Option<&str>) -> usize {
        let mut cancelled = 0;
        for job in &self.jobs {
            if !job.metrics.overlaps(metrics)
                || path.is_some_and(|path| !same_folder(&job.path, path))
            {
                continue;
            }
            if let Some(flag) = job.cancel.upgrade() {
                flag.store(true, Ordering::Relaxed);
                cancelled += 1;
            }
        }
        self.jobs.retain(|job| job.cancel.strong_count() > 0);
        cancelled
    }

    /// Cancels everything, for a session that has ended.
    pub(crate) fn cancel_all(&mut self) {
        self.cancel(Metrics::BOTH, None);
    }

    #[cfg(test)]
    fn running(&self) -> usize {
        self.jobs
            .iter()
            .filter(|job| job.cancel.strong_count() > 0)
            .count()
    }
}

/// Two spellings of one folder: case and a trailing separator do not matter.
fn same_folder(left: &str, right: &str) -> bool {
    let trim = |path: &str| path.trim_end_matches(['\\', '/']).to_lowercase();
    trim(left) == trim(right)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn is_cancelled(flag: &Arc<AtomicBool>) -> bool {
        flag.load(Ordering::Relaxed)
    }

    #[test]
    fn a_cancel_for_one_folder_leaves_the_request_for_another_running() {
        let mut jobs = FolderJobs::default();
        let first = jobs.start(r"C:\Photos", Metrics::BOTH);
        let second = jobs.start(r"C:\Music", Metrics::BOTH);

        assert_eq!(jobs.cancel(Metrics::BOTH, Some(r"c:\photos\")), 1);

        assert!(
            is_cancelled(&first),
            "the older request is the one cancelled"
        );
        assert!(!is_cancelled(&second), "the newer request runs on");
    }

    #[test]
    fn a_cancel_without_a_folder_stops_every_running_request_of_that_kind() {
        let mut jobs = FolderJobs::default();
        let size = jobs.start(r"C:\A", Metrics::SIZE);
        let count = jobs.start(r"C:\B", Metrics::COUNT);
        let both = jobs.start(r"C:\C", Metrics::BOTH);

        assert_eq!(jobs.cancel(Metrics::SIZE, None), 2);

        assert!(is_cancelled(&size));
        assert!(!is_cancelled(&count), "a count is not a size request");
        assert!(
            is_cancelled(&both),
            "a request for both is a size request too"
        );
    }

    #[test]
    fn two_requests_for_the_same_folder_are_both_cancelled() {
        let mut jobs = FolderJobs::default();
        let first = jobs.start(r"C:\Photos", Metrics::BOTH);
        let second = jobs.start(r"C:\Photos", Metrics::BOTH);

        assert_eq!(jobs.cancel(Metrics::COUNT, Some(r"C:\Photos")), 2);

        assert!(is_cancelled(&first) && is_cancelled(&second));
    }

    #[test]
    fn a_finished_request_is_forgotten() {
        let mut jobs = FolderJobs::default();
        let finished = jobs.start(r"C:\Done", Metrics::BOTH);
        drop(finished);
        let running = jobs.start(r"C:\Busy", Metrics::BOTH);

        assert_eq!(jobs.running(), 1);
        assert_eq!(jobs.cancel(Metrics::BOTH, Some(r"C:\Done")), 0);
        assert!(!is_cancelled(&running));
        drop(running);
        assert_eq!(jobs.cancel(Metrics::BOTH, None), 0);
        assert_eq!(jobs.jobs.len(), 0);
    }

    #[test]
    fn the_end_of_a_session_cancels_every_request_not_only_the_last() {
        let mut jobs = FolderJobs::default();
        let flags: Vec<_> = (0..5)
            .map(|index| jobs.start(&format!(r"C:\Folder{index}"), Metrics::SIZE))
            .collect();

        jobs.cancel_all();

        assert!(flags.iter().all(is_cancelled));
    }
}
