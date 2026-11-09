//! Bounded concurrency feedback and expiring route history. No transfer data or
//! credentials are stored. Measurements are supplied by the copy coordinator.

use serde::{Deserialize, Serialize};
use simplefile_core::settings_store::{get_db_setting, set_db_setting};
use std::collections::BTreeMap;
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub(crate) const POLICY_KEY: &str = "transfer.policy";
const HISTORY_KEY: &str = "transfer.learning.v1";
const MAX_HISTORY: usize = 64;
const MAX_AGE: u64 = 30 * 24 * 60 * 60;
static HISTORY_LOCK: Mutex<()> = Mutex::new(());
/// Qualified samples (each at least five saturated seconds) to hold a setting
/// after a halving or a revert before probing again.
const COOLDOWN_SAMPLES: usize = 2;
/// Cap on the exponential probe back-off: 2 << 4 = 32 cooldown samples.
const MAX_PROBE_BACKOFF: u32 = 4;

pub(crate) use sumafile_transfer::policy::{CheckMode, Mode, OrderBy, Policy};

/// The stored transfer policy, normalized; the default when none is stored.
pub(crate) fn load_policy() -> Policy {
    get_db_setting(POLICY_KEY.into())
        .ok()
        .flatten()
        .and_then(|s| serde_json::from_str::<Policy>(&s).ok())
        .unwrap_or_default()
        .normalized()
}

/// The per-operation override the Transfers window saved, or the stored policy.
pub(crate) fn policy_for_operation(operation_id: &str) -> Policy {
    // The UI keeps this document bounded and expires abandoned overrides.
    #[derive(Deserialize)]
    struct Override {
        policy: Policy,
        updated: u64,
    }
    let overrides: BTreeMap<String, Override> = get_db_setting("transfer.overrides.v1".into())
        .ok()
        .flatten()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default();
    let now = now_seconds();
    overrides
        .into_iter()
        .find_map(|(id, value)| {
            (id == operation_id && value.updated <= now && now - value.updated < 86400)
                .then(|| value.policy.normalized())
        })
        .unwrap_or_else(load_policy)
}

#[derive(Clone, Debug, Deserialize, Serialize)]
struct Learned {
    concurrency: usize,
    updated: u64,
}

#[derive(Default, Deserialize, Serialize)]
struct History {
    routes: BTreeMap<String, Learned>,
}

impl History {
    fn prune(&mut self, now: u64) {
        self.routes.retain(|_, v| {
            v.concurrency > 0
                && v.concurrency <= 32
                && v.updated <= now
                && now - v.updated <= MAX_AGE
        });
        while self.routes.len() > MAX_HISTORY {
            let oldest = self
                .routes
                .iter()
                .min_by_key(|(_, v)| v.updated)
                .map(|(k, _)| k.clone());
            if let Some(key) = oldest {
                self.routes.remove(&key);
            }
        }
    }
}

fn now_seconds() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

fn read_history() -> History {
    get_db_setting(HISTORY_KEY.into())
        .ok()
        .flatten()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

pub(crate) fn recall(key: &str) -> Option<usize> {
    let _guard = HISTORY_LOCK.lock().ok()?;
    let mut history = read_history();
    history.prune(now_seconds());
    history.routes.get(key).map(|v| v.concurrency)
}

pub(crate) fn remember(key: &str, concurrency: usize) -> Result<(), String> {
    let _guard = HISTORY_LOCK.lock().map_err(|e| e.to_string())?;
    let mut history = read_history();
    let now = now_seconds();
    history.routes.insert(
        key.into(),
        Learned {
            concurrency: concurrency.clamp(1, 32),
            updated: now,
        },
    );
    history.prune(now);
    set_db_setting(
        HISTORY_KEY.into(),
        serde_json::to_string(&history).map_err(|e| e.to_string())?,
    )
}

/// Ordered endpoints preserve direction. The endpoint identity should include
/// the actual mapped share / volume identity, not just its mutable drive letter.
pub(crate) fn route_key(source: &str, destination: &str, bytes: u64, files: u64) -> String {
    let average = bytes / files.max(1);
    let workload = if average < 1024 * 1024 {
        "small"
    } else if average < 16 * 1024 * 1024 {
        "medium"
    } else {
        "large"
    };
    let input = format!("copy-v2\0{source}\0{destination}\0{workload}");
    // Stable hash rather than storing account/share names or filesystem paths.
    let hash = input.bytes().fold(0xcbf29ce484222325u64, |h, b| {
        (h ^ u64::from(b)).wrapping_mul(0x100000001b3)
    });
    format!("{hash:016x}")
}

pub(crate) struct Controller {
    pub policy: Policy,
    pub concurrency: usize,
    pub explanation: &'static str,
    limit: usize,
    buffer_bytes: usize,
    effective_buffer: usize,
    best_concurrency: usize,
    best_rate: f64,
    previous_rate: Option<f64>,
    probing: bool,
    cooldown: usize,
    learned: bool,
    try_lower: bool,
    lower_tested: bool,
    /// Upward probes rejected in a row; each one doubles the next cooldown so
    /// storage whose optimum is below the limit is not re-probed every 40 s.
    rejected_probes: u32,
}

impl Controller {
    pub fn new(policy: Policy, buffer_bytes: usize, remembered: Option<usize>) -> Self {
        Self::new_for_workload(policy, buffer_bytes, remembered, 0, 0)
    }

    pub fn new_for_workload(
        policy: Policy,
        buffer_bytes: usize,
        remembered: Option<usize>,
        bytes: u64,
        files: usize,
    ) -> Self {
        let avg_size = bytes / (files as u64).max(1);
        let small_file_burst = files >= 16 && avg_size <= 256 * 1024;
        let effective_buffer = if small_file_burst {
            buffer_bytes.min(usize::try_from(avg_size.max(64 * 1024)).unwrap_or(buffer_bytes))
        } else {
            buffer_bytes
        };
        let limit = policy.limit(effective_buffer);
        let concurrency = if policy.mode == Mode::Manual {
            policy.manual_concurrency.clamp(1, limit)
        } else if let Some(remembered) = remembered {
            // What worked last time on this route, within the configured ceiling.
            remembered.clamp(1, limit)
        } else if small_file_burst {
            match policy.mode {
                Mode::Conservative => 2,
                Mode::Maximum => 16,
                _ => 12,
            }
            .clamp(1, limit)
        } else {
            match policy.mode {
                Mode::Conservative => 2,
                Mode::Maximum => 8,
                _ => 4,
            }
            .min(8)
            .clamp(1, limit)
        };
        let explanation = if policy.mode == Mode::Manual {
            "Manual limit"
        } else if remembered.is_some() {
            "Rechecking remembered setting"
        } else if small_file_burst {
            "Scaling concurrency for small-file batch"
        } else {
            "Measuring transfer speed"
        };
        Self {
            policy,
            concurrency,
            explanation,
            limit,
            buffer_bytes,
            effective_buffer,
            best_concurrency: concurrency,
            best_rate: 0.0,
            previous_rate: None,
            probing: false,
            cooldown: 0,
            learned: false,
            try_lower: false,
            lower_tested: false,
            rejected_probes: 0,
        }
    }

    pub fn update_policy(&mut self, policy: Policy, buffer_bytes: usize) {
        let effective_buffer = if buffer_bytes == self.buffer_bytes {
            self.effective_buffer
        } else {
            buffer_bytes
        };
        let limit = policy.limit(effective_buffer);
        if policy.mode != self.policy.mode
            || limit != self.limit
            || policy.manual_concurrency != self.policy.manual_concurrency
        {
            let mut updated =
                Self::new(policy, effective_buffer, Some(self.concurrency.min(limit)));
            updated.buffer_bytes = buffer_bytes;
            updated.effective_buffer = effective_buffer;
            *self = updated;
        } else {
            self.policy = policy;
        }
    }

    /// Samples must span at least five seconds and have enough queued work to
    /// keep the chosen number busy. Tail-drain and partial warm-up samples do not
    /// become evidence for a higher/lower concurrency setting.
    pub fn sample(&mut self, bytes: u64, elapsed: Duration, saturated: bool, retries: u64) {
        // Custom settings are authoritative; errors only delay their failing file.
        if self.policy.mode == Mode::Manual {
            self.concurrency = self.policy.manual_concurrency.clamp(1, self.limit);
            self.explanation = "Custom limits (memory and provider limits still apply)";
            return;
        }
        if retries > 0 {
            self.concurrency = (self.concurrency / 2).max(1);
            self.best_concurrency = self.concurrency;
            self.best_rate = 0.0;
            self.previous_rate = None;
            self.probing = false;
            self.learned = false;
            self.cooldown = COOLDOWN_SAMPLES;
            self.explanation = "Reduced concurrency after an I/O retry";
            return;
        }
        if elapsed < Duration::from_secs(5) || !saturated {
            self.previous_rate = None;
            return;
        }
        let rate = bytes as f64 / elapsed.as_secs_f64();
        if rate <= 0.0 {
            // Hydration, metadata and NAS flush waits do not prove contention.
            // Do not reduce workers just because no bytes arrived in a window.
            self.previous_rate = None;
            return;
        }
        let Some(previous) = self.previous_rate.replace(rate) else {
            return;
        };
        // Mixed file sizes and background traffic can produce noisy samples.
        if (rate - previous).abs() / rate.max(previous) > 0.30 {
            return;
        }
        self.previous_rate = None;
        let rate = (rate + previous) / 2.0;
        if self.probing {
            let lower = self.concurrency < self.best_concurrency;
            if rate >= self.best_rate * (if lower { 0.95 } else { 1.08 }) {
                self.best_rate = rate;
                self.best_concurrency = self.concurrency;
                if !lower {
                    // Only a successful step upward proves the ceiling moved.
                    self.rejected_probes = 0;
                }
                self.explanation = if lower {
                    "Similar speed with fewer simultaneous files"
                } else {
                    "Higher concurrency improved throughput"
                };
            } else {
                self.concurrency = self.best_concurrency;
                self.cooldown = COOLDOWN_SAMPLES << self.rejected_probes.min(MAX_PROBE_BACKOFF);
                if !lower {
                    self.rejected_probes = self.rejected_probes.saturating_add(1);
                }
                self.explanation = "Keeping the faster stable setting";
                self.try_lower = !lower && !self.lower_tested;
            }
            self.probing = false;
            self.learned = true;
        } else {
            self.best_rate = rate;
            self.best_concurrency = self.concurrency;
            self.learned = true;
            if self.cooldown > 0 {
                self.cooldown -= 1;
            } else if self.try_lower && self.concurrency > 1 {
                self.concurrency = (self.concurrency / 2).max(1);
                self.probing = true;
                self.try_lower = false;
                self.lower_tested = true;
                self.explanation = "Testing whether fewer files reduce contention";
            } else if self.concurrency < self.limit {
                self.concurrency =
                    (self.concurrency + (self.concurrency / 2).max(1)).min(self.limit);
                self.probing = true;
                self.explanation = "Testing more simultaneous files";
            } else {
                self.explanation = "At the configured concurrency limit";
            }
        }
    }

    pub fn learned_concurrency(&self) -> Option<usize> {
        (self.learned && self.policy.mode != Mode::Manual).then_some(self.best_concurrency)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn observe(c: &mut Controller, rate: u64) {
        for _ in 0..2 {
            c.sample(rate * 5, Duration::from_secs(5), true, 0);
        }
    }

    #[test]
    fn explores_and_rolls_back_a_slower_probe() {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        observe(&mut c, 1000);
        assert_eq!(c.concurrency, 6);
        observe(&mut c, 900);
        assert_eq!(c.concurrency, 4);
        assert_eq!(c.learned_concurrency(), Some(4));
    }

    #[test]
    fn remembers_improvement_but_not_an_unfinished_probe() {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        observe(&mut c, 1000);
        assert_eq!(c.learned_concurrency(), Some(4));
        observe(&mut c, 1200);
        assert_eq!(c.learned_concurrency(), Some(6));
    }

    #[test]
    fn errors_reduce_concurrency_immediately_and_invalidate_learning() {
        let mut c = Controller::new(Policy::default(), 4 << 20, Some(8));
        c.sample(0, Duration::from_millis(1), false, 1);
        assert_eq!(c.concurrency, 4);
        assert_eq!(c.learned_concurrency(), None);
    }

    #[test]
    fn tail_and_noisy_samples_do_not_teach_the_controller() {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        c.sample(1000, Duration::from_secs(5), false, 0);
        c.sample(100, Duration::from_secs(5), true, 0);
        c.sample(1000, Duration::from_secs(5), true, 0);
        assert_eq!(c.concurrency, 4);
        assert_eq!(c.learned_concurrency(), None);
    }

    #[test]
    fn policy_caps_memory_and_manual_settings() {
        let policy = Policy {
            mode: Mode::Manual,
            manual_concurrency: 32,
            memory_budget_mb: 24,
            ..Policy::default()
        };
        let mut c = Controller::new(policy, 4 << 20, None);
        assert_eq!(c.concurrency, 1);
        observe(&mut c, 1000);
        assert_eq!(c.concurrency, 1);
        assert_eq!(c.learned_concurrency(), None);
        c.update_policy(
            Policy {
                mode: Mode::Conservative,
                ..Policy::default()
            },
            4 << 20,
        );
        for _ in 0..10 {
            observe(&mut c, 1000);
        }
        assert!(c.concurrency <= 4);
    }

    #[test]
    fn history_is_bounded_expires_and_rejects_future_records() {
        let now = MAX_AGE + 100;
        let mut h = History::default();
        for i in 0..80 {
            h.routes.insert(
                i.to_string(),
                Learned {
                    concurrency: 4,
                    updated: now - i,
                },
            );
        }
        h.routes.insert(
            "stale".into(),
            Learned {
                concurrency: 4,
                updated: 1,
            },
        );
        h.routes.insert(
            "future".into(),
            Learned {
                concurrency: 4,
                updated: now + 1,
            },
        );
        h.prune(now);
        assert_eq!(h.routes.len(), MAX_HISTORY);
        assert!(!h.routes.contains_key("stale"));
        assert!(!h.routes.contains_key("future"));
        let roundtrip: History = serde_json::from_str(&serde_json::to_string(&h).unwrap()).unwrap();
        assert_eq!(roundtrip.routes.len(), MAX_HISTORY);
    }

    #[test]
    fn learning_is_direction_and_workload_specific() {
        assert_ne!(
            route_key("cloud", "nas", 100, 1),
            route_key("nas", "cloud", 100, 1)
        );
        assert_ne!(
            route_key("cloud", "nas", 100, 1),
            route_key("cloud", "nas", 32 << 20, 1)
        );
        assert_ne!(
            route_key("share1", "nas", 100, 1),
            route_key("share2", "nas", 100, 1)
        );
    }

    #[test]
    fn slower_storage_can_choose_fewer_workers_after_a_plateau() {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        observe(&mut c, 1000); // Probe 6 from the baseline of 4.
        observe(&mut c, 1000); // Roll back and schedule a lower probe.
        for _ in 0..7 {
            observe(&mut c, 1000);
        }
        assert_eq!(c.concurrency, 2);
        observe(&mut c, 990);
        assert_eq!(c.learned_concurrency(), Some(2));
    }

    #[test]
    fn cloud_hydration_and_nas_flush_gaps_do_not_reduce_concurrency() {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        for _ in 0..24 {
            c.sample(0, Duration::from_secs(5), true, 0);
        }
        assert_eq!(c.concurrency, 4);
        assert_eq!(c.learned_concurrency(), None);
    }

    #[test]
    fn custom_limits_survive_latency_errors_and_live_policy_changes() {
        let mut c = Controller::new(
            Policy {
                mode: Mode::Manual,
                manual_concurrency: 16,
                memory_budget_mb: 512,
                ..Policy::default()
            },
            4 << 20,
            Some(1),
        );
        for retries in [0, 1, 8, 0, 16, 0] {
            c.sample(0, Duration::from_secs(30), true, retries);
            assert_eq!(c.concurrency, 16);
        }
        c.update_policy(
            Policy {
                mode: Mode::Manual,
                manual_concurrency: 7,
                ..Policy::default()
            },
            4 << 20,
        );
        c.sample(1, Duration::from_secs(5), true, 4);
        assert_eq!(c.concurrency, 7);
        assert_eq!(c.learned_concurrency(), None);
        c.update_policy(Policy::default(), 4 << 20);
        c.sample(0, Duration::from_secs(1), false, 1);
        assert!(c.concurrency < 16);
        c.update_policy(
            Policy {
                mode: Mode::Manual,
                manual_concurrency: 16,
                memory_budget_mb: 512,
                ..Policy::default()
            },
            4 << 20,
        );
        assert_eq!(
            c.concurrency, 16,
            "Switching to Custom must discard automatic reductions"
        );
    }

    #[test]
    fn changing_cloud_throughput_without_errors_is_not_a_reason_to_halve_workers() {
        for mode in [Mode::Automatic, Mode::Conservative, Mode::Maximum] {
            let mut c = Controller::new(
                Policy {
                    mode,
                    ..Policy::default()
                },
                512 << 10,
                None,
            );
            let initial = c.concurrency;
            // Successive bursts / quiet windows as cloud data is hydrated. No
            // stable comparative experiment establishes a benefit to fewer workers.
            for rate in [100_000, 0, 4_000, 0, 1_000, 0, 300, 0, 100, 0] {
                c.sample(rate, Duration::from_secs(5), true, 0);
                assert_eq!(c.concurrency, initial);
            }
        }
    }

    #[test]
    fn default_budget_reaches_the_configured_concurrency_and_busy_retries_are_ignored() {
        let policy = Policy::default();
        assert_eq!(policy.multi_thread_streams, 0, "multistream is opt-in");
        assert!(
            policy.limit(policy.buffer_bytes(false)) >= policy.max_concurrency,
            "defaults must allow max_concurrency, got {}",
            policy.limit(policy.buffer_bytes(false))
        );
        // A remembered value is bounded by the configured ceiling, not by 8.
        let c = Controller::new(policy.clone(), 4 << 20, Some(12));
        assert_eq!(c.concurrency, 12);
        // Halving holds for a short cooldown, then probing resumes.
        let mut c = Controller::new(policy, 4 << 20, Some(8));
        c.sample(0, Duration::from_millis(1), false, 1);
        assert_eq!(c.concurrency, 4);
        for _ in 0..COOLDOWN_SAMPLES {
            observe(&mut c, 1000);
            assert_eq!(c.concurrency, 4);
        }
        observe(&mut c, 1000);
        assert!(c.concurrency > 4, "probing resumes after the cooldown");
    }

    #[test]
    fn small_file_bursts_scale_initial_concurrency_and_right_size_memory_limit() {
        let policy = Policy {
            mode: Mode::Automatic,
            max_concurrency: 16,
            memory_budget_mb: 24,
            ..Policy::default()
        };
        // Four 4 MiB stripes plus verification scratch leave room for one file in 24 MiB.
        let large = Controller::new_for_workload(policy.clone(), 4 << 20, None, 64 << 20, 4);
        assert_eq!(large.concurrency, 1);

        // For 64 small files (4 KiB each), effective buffer is right-sized to 64 KiB
        // and automatic mode starts at 12 workers instead of 2 or 4.
        let small = Controller::new_for_workload(policy, 4 << 20, None, 64 * 4096, 64);
        assert_eq!(small.concurrency, 12);

        let max_small = Controller::new_for_workload(
            Policy {
                mode: Mode::Maximum,
                max_concurrency: 16,
                memory_budget_mb: 24,
                ..Policy::default()
            },
            4 << 20,
            None,
            64 * 4096,
            64,
        );
        assert_eq!(max_small.concurrency, 16);
    }
}

#[cfg(test)]
mod backoff_tests {
    use super::{Controller, Policy, COOLDOWN_SAMPLES};
    use std::time::Duration;

    fn observe(c: &mut Controller, rate: u64) {
        for _ in 0..2 {
            c.sample(rate * 5, Duration::from_secs(5), true, 0);
        }
    }

    /// Storage whose optimum is below the limit must not be re-probed at the
    /// same cadence forever: each rejected upward probe lengthens the pause.
    #[test]
    fn rejected_probes_lengthen_the_cooldown() {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        observe(&mut c, 1000); // baseline, starts an upward probe
        assert!(c.probing && c.concurrency > c.best_concurrency);
        observe(&mut c, 900); // rejected
        let first_cooldown = c.cooldown;
        assert_eq!(first_cooldown, COOLDOWN_SAMPLES);

        // Keep the rate flat until the controller tries upward again.
        let mut reached_upward_probe = false;
        for _ in 0..64 {
            observe(&mut c, 1000);
            if c.probing && c.concurrency > c.best_concurrency {
                reached_upward_probe = true;
                break;
            }
        }
        assert!(reached_upward_probe, "controller never probed upward again");
        observe(&mut c, 900); // rejected again
        assert!(
            c.cooldown >= 2 * COOLDOWN_SAMPLES,
            "second rejection kept cooldown at {}",
            c.cooldown
        );
    }
}
