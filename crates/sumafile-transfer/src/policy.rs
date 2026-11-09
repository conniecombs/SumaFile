//! The transfer policy model: user tuning, its normalization and the buffer
//! sizing derived from it. Loading the stored policy stays with each engine.

use serde::{Deserialize, Serialize};
use std::time::Duration;

#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    Conservative,
    Maximum,
    Manual,
    #[default]
    #[serde(other)]
    Automatic,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum OrderBy {
    SizeAsc,
    SizeDesc,
    Name,
    #[default]
    #[serde(other)]
    Default,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum CheckMode {
    SizeOnly,
    Checksum,
    IgnoreTimes,
    #[default]
    #[serde(other)]
    SizeAndModTime,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(default, rename_all = "camelCase")]
pub struct Policy {
    pub mode: Mode,
    pub max_concurrency: usize,
    pub manual_concurrency: usize,
    pub checkers: usize,
    pub max_backlog: usize,
    pub order_by: OrderBy,
    pub buffer_size_kib: usize,
    pub memory_budget_mb: usize,
    pub multi_thread_cutoff_mib: usize,
    pub multi_thread_streams: usize,
    pub check_mode: CheckMode,
    pub inplace: bool,
    pub preallocate: bool,
    pub flush_on_close: bool,
    pub bandwidth_mib: usize,
    pub low_level_retries: u32,
    pub high_level_retries: u32,
    pub retry_sleep_ms: u64,
}

impl Default for Policy {
    fn default() -> Self {
        Self {
            mode: Mode::Automatic,
            max_concurrency: 16,
            manual_concurrency: 4,
            checkers: 8,
            max_backlog: 10_000,
            order_by: OrderBy::Default,
            buffer_size_kib: 4096,
            // Sixteen files x (three 4 MiB pipeline slots + scratch) fits.
            memory_budget_mb: 256,
            multi_thread_cutoff_mib: 64,
            // Opt-in: independent byte ranges help only on local fixed disks
            // that a single stream cannot saturate.
            multi_thread_streams: 0,
            check_mode: CheckMode::SizeAndModTime,
            inplace: false,
            preallocate: true,
            flush_on_close: true,
            bandwidth_mib: 0,
            low_level_retries: 10,
            high_level_retries: 3,
            retry_sleep_ms: 1000,
        }
    }
}

impl Policy {
    pub fn normalized(mut self) -> Self {
        self.max_concurrency = self.max_concurrency.clamp(1, 32);
        self.manual_concurrency = self.manual_concurrency.clamp(1, 32);
        self.checkers = self.checkers.clamp(1, 64);
        self.max_backlog = self.max_backlog.clamp(128, 100_000);
        self.buffer_size_kib = self.buffer_size_kib.clamp(64, 65_536);
        self.memory_budget_mb = self.memory_budget_mb.clamp(16, 512);
        self.multi_thread_cutoff_mib = self.multi_thread_cutoff_mib.clamp(1, 16_384);
        self.multi_thread_streams = self.multi_thread_streams.clamp(0, 16);
        self.bandwidth_mib = self.bandwidth_mib.clamp(0, 1_048_576);
        self.low_level_retries = self.low_level_retries.clamp(1, 50);
        self.high_level_retries = self.high_level_retries.clamp(1, 20);
        self.retry_sleep_ms = self.retry_sleep_ms.clamp(10, 60_000);
        self
    }

    pub fn buffer_bytes(&self, network: bool) -> usize {
        let kib = self.buffer_size_kib.clamp(64, 65_536);
        let requested = if network && kib == 4096 {
            512 * 1024
        } else {
            kib * 1024
        };
        requested
            .min((self.memory_bytes() - 128 * 1024) / self.max_buffer_count())
            .max(4096)
    }

    pub fn memory_bytes(&self) -> usize {
        self.memory_budget_mb.clamp(16, 512) * 1024 * 1024
    }

    /// Buffers charged per file when sizing the concurrency limit: the
    /// three-slot pipeline. Multistream files charge their actual stripe count
    /// at reservation time instead of taxing every file for the largest case.
    pub fn max_buffer_count(&self) -> usize {
        3
    }

    /// Multistream stripes that fit the job's memory budget alongside the
    /// 128 KiB scratch reservation. The configured stream count is an upper
    /// bound: with a small budget and a large buffer, reserving one buffer per
    /// configured stripe would exceed the budget and fail every large file.
    pub fn budgeted_streams(&self, network: bool) -> usize {
        let buffer = self.buffer_bytes(network).max(1);
        let affordable = self.memory_bytes().saturating_sub(128 * 1024) / buffer;
        self.multi_thread_streams_count().min(affordable.max(1))
    }

    pub fn buffer_count(&self, network: bool, file_len: u64, _operation: &str) -> usize {
        let buffer = self.buffer_bytes(network) as u64;
        let streams = self.budgeted_streams(network);
        if cfg!(windows)
            && !network
            && self.check_mode != CheckMode::Checksum
            && streams >= 2
            && file_len >= self.multi_thread_cutoff_bytes()
            && file_len >= buffer * 2
        {
            streams
        } else if file_len > buffer {
            3
        } else {
            1
        }
    }

    pub fn checkers_limit(&self) -> usize {
        self.checkers.clamp(1, 64)
    }

    pub fn backlog_limit(&self) -> usize {
        self.max_backlog.clamp(128, 100_000)
    }

    pub fn multi_thread_cutoff_bytes(&self) -> u64 {
        (self.multi_thread_cutoff_mib.clamp(1, 16_384) as u64) * 1024 * 1024
    }

    pub fn multi_thread_streams_count(&self) -> usize {
        self.multi_thread_streams.clamp(0, 16)
    }

    pub fn low_level_retry_limit(&self) -> u32 {
        self.low_level_retries.clamp(1, 50)
    }

    pub fn high_level_retry_limit(&self) -> u32 {
        self.high_level_retries.clamp(1, 20)
    }

    pub fn retry_sleep(&self) -> Duration {
        Duration::from_millis(self.retry_sleep_ms.clamp(10, 60_000))
    }

    pub fn limit(&self, buffer_bytes: usize) -> usize {
        let memory_limit = self.memory_bytes()
            / (buffer_bytes.max(1).saturating_mul(self.max_buffer_count()) + 128 * 1024);
        let mode_limit = if self.mode == Mode::Conservative {
            4
        } else {
            32
        };
        self.max_concurrency
            .clamp(1, 32)
            .min(mode_limit)
            .min(memory_limit.max(1))
    }
}

#[cfg(test)]
mod tests {
    use super::Policy;

    #[test]
    fn default_policy_is_normalized() {
        assert_eq!(Policy::default().normalized(), Policy::default());
    }
}

#[cfg(test)]
mod budget_tests {
    use super::Policy;

    fn reservation(policy: &Policy, network: bool, file_len: u64) -> usize {
        policy.buffer_bytes(network) * policy.buffer_count(network, file_len, "copy") + 128 * 1024
    }

    /// The per-file reservation must always fit the job's memory budget, or
    /// every file above the multistream cutoff fails with a misleading
    /// "budget changed" error.
    #[test]
    fn multistream_reservation_fits_the_memory_budget() {
        let small_budget = Policy {
            buffer_size_kib: 4096,
            multi_thread_streams: 4,
            memory_budget_mb: 16,
            ..Policy::default()
        };
        assert!(reservation(&small_budget, false, 1 << 30) <= small_budget.memory_bytes());

        let many_streams = Policy {
            buffer_size_kib: 65_536,
            multi_thread_streams: 16,
            memory_budget_mb: 64,
            ..Policy::default()
        };
        assert!(reservation(&many_streams, false, 1 << 31) <= many_streams.memory_bytes());
    }

    #[test]
    fn stripes_used_for_copying_match_the_reserved_buffers() {
        let policy = Policy {
            buffer_size_kib: 4096,
            multi_thread_streams: 4,
            memory_budget_mb: 16,
            ..Policy::default()
        };
        assert_eq!(
            policy.budgeted_streams(false),
            policy.buffer_count(false, 1 << 30, "copy")
        );
    }
}
