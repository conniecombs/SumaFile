//! Progress emission and cancellation helpers for transfers.

use simplefile_core::models::ProgressUpdate;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex};
use std::time::{Duration, Instant};

pub(crate) const NETWORK_BUFFER_SIZE: usize = 512 * 1024;
pub(crate) const LOCAL_BUFFER_SIZE: usize = 4 * 1024 * 1024;
pub(crate) const NETWORK_MAX_RETRIES: u32 = 5;
pub(crate) const PROGRESS_BYTE_STEP: u64 = 4 * 1024 * 1024;
pub(crate) const PROGRESS_MIN_INTERVAL: Duration = Duration::from_millis(80);

pub(crate) fn check_cancelled(cancel: &Arc<AtomicBool>) -> Result<(), String> {
    if cancel.load(Ordering::Relaxed) {
        Err("Operation cancelled".to_string())
    } else {
        Ok(())
    }
}

pub(crate) struct ProgressContext<'a> {
    pub(crate) emit: &'a dyn Fn(ProgressUpdate),
    pub(crate) operation_id: &'a str,
    pub(crate) operation_type: &'a str,
    pub(crate) cancel: &'a Arc<AtomicBool>,
    pub(crate) total_bytes: &'a Arc<AtomicU64>,
    pub(crate) total_files: &'a Arc<AtomicU64>,
    pub(crate) completed_files: &'a Arc<AtomicU64>,
    pub(crate) last_running_emit: StdMutex<Instant>,
}

impl ProgressContext<'_> {
    pub(crate) fn check_cancelled(&self) -> Result<(), String> {
        check_cancelled(self.cancel)
    }

    pub(crate) fn emit(&self, current: u64, current_item: String, status: &str, error: Option<String>) {
        self.emit_with_total(
            current,
            self.total_bytes.load(Ordering::Relaxed),
            current_item,
            status,
            error,
        );
    }

    pub(crate) fn emit_with_total(
        &self,
        current: u64,
        total: u64,
        current_item: String,
        status: &str,
        error: Option<String>,
    ) {
        if status == "running" && !self.should_emit_running() {
            return;
        }
        self.emit_now(current, total, current_item, status, error);
    }

    pub(crate) fn emit_now(
        &self,
        current: u64,
        total: u64,
        current_item: String,
        status: &str,
        error: Option<String>,
    ) {
        (self.emit)(ProgressUpdate {
            operation_id: self.operation_id.to_string(),
            operation_type: self.operation_type.to_string(),
            current,
            total,
            current_files: self.completed_files.load(Ordering::Relaxed),
            total_files: self.total_files.load(Ordering::Relaxed),
            current_item,
            status: status.to_string(),
            error,
        });
    }

    pub(crate) fn should_emit_running(&self) -> bool {
        let now = Instant::now();
        let Ok(mut last) = self.last_running_emit.lock() else {
            return true;
        };
        if now.duration_since(*last) < PROGRESS_MIN_INTERVAL {
            return false;
        }
        *last = now;
        true
    }

    pub(crate) fn complete_file(&self, current: u64, current_item: String) {
        self.complete_files(1, current, current_item);
    }

    pub(crate) fn complete_files(&self, count: u64, current: u64, current_item: String) {
        if count > 0 {
            let completed = self
                .completed_files
                .fetch_add(count, Ordering::Relaxed)
                .saturating_add(count);
            let known = self.total_files.load(Ordering::Relaxed);
            if completed > known {
                self.total_files.store(completed, Ordering::Relaxed);
            }
        }

        let total = self.total_bytes.load(Ordering::Relaxed).max(current);
        self.emit_with_total(current, total, current_item, "running", None);
    }
}

pub(crate) struct CopyContext<'a> {
    pub(crate) progress: &'a ProgressContext<'a>,
    pub(crate) operation_id: &'a str,
    pub(crate) network: bool,
    pub(crate) resume_existing: bool,
}

impl CopyContext<'_> {
    pub(crate) fn attempts(&self) -> u32 {
        if self.network {
            NETWORK_MAX_RETRIES
        } else {
            1
        }
    }

    pub(crate) fn buffer_size(&self) -> usize {
        if self.network {
            NETWORK_BUFFER_SIZE
        } else {
            LOCAL_BUFFER_SIZE
        }
    }
}
