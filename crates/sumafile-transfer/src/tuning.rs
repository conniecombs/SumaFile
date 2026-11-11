//! Pure scheduling decisions for the local transfer engine: where to start,
//! how to read throughput and write latency, when to try more or fewer
//! simultaneous files, and how small and large files share the limit.
//!
//! Nothing here touches a file, a clock or a setting store: the caller supplies
//! measurements and applies the answers, so every rule is testable anywhere.

use crate::policy::{Mode, OrderBy, Policy};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

/// Decisions to hold a setting after a halving or a rejected probe. A decision
/// needs at least three one-second windows, so this is twelve seconds or more.
pub const COOLDOWN_SAMPLES: usize = 4;
/// Cap on the exponential probe back-off: 4 << 4 = 64 decisions.
const MAX_PROBE_BACKOFF: u32 = 4;
/// Shortest window that counts as a throughput reading.
pub const MIN_WINDOW: Duration = Duration::from_secs(1);
/// A window closes here even when little work was done...
const WINDOW_CAP: Duration = Duration::from_secs(2);
/// ...and stretches to this on links too slow to fill the shorter one.
const SLOW_WINDOW_CAP: Duration = Duration::from_secs(5);
/// Share of a window the lane must have been full for it to count.
const BUSY_SHARE: f64 = 0.85;
/// Gain an upward probe must show before more files are kept.
const PROBE_GAIN: f64 = 0.08;
/// A lower setting is kept when it is at least this share of the best rate.
const LOWER_KEEP: f64 = 0.95;

// ---------------------------------------------------------------------------
// Starting point
// ---------------------------------------------------------------------------

/// What kind of storage one end of a transfer is.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DriveKind {
    /// Fixed disk that reports no seek penalty (SATA SSD, NVMe).
    SolidState,
    /// Fixed disk that reports a seek penalty.
    Rotational,
    /// USB stick, card reader and other removable media.
    Removable,
    /// UNC path or mapped network drive.
    Network,
    Optical,
    /// Cloud mounts, RAM disks, and anything Windows would not describe.
    Unknown,
}

impl DriveKind {
    /// Classify a volume from what the drive list reports. `drive_type` is the
    /// drive list's label (`Fixed`, `Removable`, `Network`, `CD-ROM`, ...);
    /// `seek_penalty` is the storage driver's answer, when it gave one.
    pub fn classify(
        unc: bool,
        drive_type: &str,
        virtual_file_system: bool,
        seek_penalty: Option<bool>,
    ) -> Self {
        if unc {
            return Self::Network;
        }
        match drive_type {
            "Network" => Self::Network,
            "Removable" => Self::Removable,
            "CD-ROM" => Self::Optical,
            // A cloud or FUSE mount posing as a fixed disk is not a disk.
            "Fixed" if virtual_file_system => Self::Unknown,
            "Fixed" => match seek_penalty {
                Some(true) => Self::Rotational,
                Some(false) => Self::SolidState,
                None => Self::Unknown,
            },
            _ => Self::Unknown,
        }
    }

    fn caution(self) -> u8 {
        match self {
            Self::Optical => 5,
            Self::Rotational => 4,
            Self::Removable => 3,
            Self::Unknown => 2,
            Self::Network => 1,
            Self::SolidState => 0,
        }
    }

    /// The kind that should set the pace when two volumes take part: a hard
    /// disk on either side limits the route whatever the other side is, and an
    /// unknown volume keeps the defaults rather than borrow a faster guess.
    pub fn slower(self, other: Self) -> Self {
        if other.caution() > self.caution() {
            other
        } else {
            self
        }
    }
}

/// The files to start with before anything was measured or remembered, and a
/// plain reason when the storage kind decided it. Unknown storage keeps the
/// long-standing defaults.
pub fn starting_concurrency(
    mode: Mode,
    route: DriveKind,
    small_file_burst: bool,
) -> (usize, Option<&'static str>) {
    // (conservative, automatic, maximum) for large files, then small bursts.
    let (large, small, reason) = match route {
        DriveKind::Unknown => ((2, 4, 8), (2, 12, 16), None),
        DriveKind::SolidState => (
            (2, 6, 8),
            (2, 16, 24),
            Some("Starting higher for solid-state drives"),
        ),
        DriveKind::Network => (
            (2, 3, 6),
            (2, 8, 12),
            Some("Starting moderately for a network share"),
        ),
        DriveKind::Removable => (
            (1, 2, 4),
            (1, 2, 4),
            Some("Starting low for a removable drive"),
        ),
        DriveKind::Rotational => ((1, 1, 2), (1, 2, 2), Some("Starting low for a hard disk")),
        DriveKind::Optical => (
            (1, 1, 1),
            (1, 1, 1),
            Some("Starting low for an optical disc"),
        ),
    };
    let row = if small_file_burst { small } else { large };
    let count = match mode {
        Mode::Conservative => row.0,
        Mode::Maximum => row.2,
        _ => row.1,
    };
    (count, reason)
}

// ---------------------------------------------------------------------------
// Throughput estimator
// ---------------------------------------------------------------------------

/// Windows kept for one reading; older ones slide out.
const RATE_WINDOWS: usize = 12;
/// Fewest windows a reading is built from.
const MIN_RATE_WINDOWS: usize = 3;
/// A reading is trusted once its relative standard error is at most this.
const TRUSTED_ERROR: f64 = 0.05;
/// Weight of the newest window in the running noise average.
const JITTER_WEIGHT: f64 = 0.1;
/// Mean difference of two noisy windows to the spread of one (sqrt(pi) / 2).
const JITTER_TO_SPREAD: f64 = 0.886;
/// Countable windows an experiment may run without a trusted reading.
const UNREAD_PROBE_WINDOWS: u32 = 24;

/// A trusted throughput reading: bytes per second and its relative error.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Reading {
    pub rate: f64,
    pub error: f64,
}

/// Moving average over short windows with a noise measure. Steady input is
/// trusted after three windows; noisier input needs more windows, and input
/// too noisy to average within twelve windows is never trusted.
///
/// Noise is measured twice: as the spread of the windows in the reading, and
/// as a running average of how much consecutive windows differ. The second
/// outlives a reading, so three windows of a wild transfer that happen to
/// agree are not mistaken for a steady one.
#[derive(Clone, Debug, Default)]
pub struct RateEstimator {
    samples: [f64; RATE_WINDOWS],
    len: usize,
    next: usize,
    last: Option<f64>,
    jitter: f64,
    jitter_windows: u32,
}

impl RateEstimator {
    /// Start a new reading. What is known about the transfer's noise is kept.
    pub fn reset(&mut self) {
        self.len = 0;
        self.next = 0;
        self.last = None;
    }

    pub fn push(&mut self, rate: f64) {
        if let Some(last) = self.last.replace(rate) {
            let larger = rate.max(last);
            if larger > 0.0 {
                let step = (rate - last).abs() / larger;
                self.jitter_windows = self.jitter_windows.saturating_add(1);
                let weight = (1.0 / f64::from(self.jitter_windows)).max(JITTER_WEIGHT);
                self.jitter += (step - self.jitter) * weight;
            }
        }
        self.samples[self.next] = rate;
        self.next = (self.next + 1) % RATE_WINDOWS;
        self.len = (self.len + 1).min(RATE_WINDOWS);
    }

    pub fn windows(&self) -> usize {
        self.len
    }

    /// The mean and its relative standard error, once trustworthy.
    pub fn reading(&self) -> Option<Reading> {
        if self.len < MIN_RATE_WINDOWS {
            return None;
        }
        let samples = &self.samples[..self.len];
        let count = self.len as f64;
        let mean = samples.iter().sum::<f64>() / count;
        if mean <= 0.0 {
            return None;
        }
        let variance = samples.iter().map(|s| (s - mean).powi(2)).sum::<f64>() / (count - 1.0);
        let spread = variance.sqrt() / mean;
        let error = spread.max(self.jitter * JITTER_TO_SPREAD) / count.sqrt();
        (error <= TRUSTED_ERROR).then_some(Reading { rate: mean, error })
    }
}

// ---------------------------------------------------------------------------
// Write latency
// ---------------------------------------------------------------------------

/// Writes smaller than this are not timed: their cost is file metadata, not
/// the device, and they are too quick for the clock to say anything useful.
pub const MIN_TIMED_WRITE: usize = 64 * 1024;
/// Quarter-octave buckets over every possible `u64`.
const LATENCY_BUCKETS: usize = 256;
/// Fewest timed writes in a window for its median to count.
const MIN_LATENCY_WRITES: u64 = 4;

/// Lock-free histogram of write latency, normalized to nanoseconds per MiB.
/// Workers add one relaxed counter increment per timed write; the coordinator
/// reads it once per window.
pub struct LatencyHistogram {
    buckets: [AtomicU64; LATENCY_BUCKETS],
}

impl Default for LatencyHistogram {
    fn default() -> Self {
        Self {
            buckets: std::array::from_fn(|_| AtomicU64::new(0)),
        }
    }
}

fn latency_bucket(nanos_per_mib: u64) -> usize {
    let value = nanos_per_mib.max(1);
    let exponent = 63 - value.leading_zeros() as usize;
    let fraction = if exponent >= 2 {
        ((value >> (exponent - 2)) & 3) as usize
    } else {
        0
    };
    exponent * 4 + fraction
}

fn latency_bucket_floor(bucket: usize) -> u64 {
    let exponent = bucket / 4;
    let fraction = (bucket % 4) as u64;
    if exponent >= 2 {
        (4 + fraction) << (exponent - 2)
    } else {
        1 << exponent
    }
}

impl LatencyHistogram {
    /// Whether a write of this size is worth timing at all.
    pub fn times(bytes: usize) -> bool {
        bytes >= MIN_TIMED_WRITE
    }

    pub fn record(&self, elapsed: Duration, bytes: usize) {
        if !Self::times(bytes) {
            return;
        }
        let nanos = u64::try_from(elapsed.as_nanos()).unwrap_or(u64::MAX);
        let per_mib = (u128::from(nanos) << 20) / bytes as u128;
        let bucket = latency_bucket(u64::try_from(per_mib).unwrap_or(u64::MAX));
        self.buckets[bucket].fetch_add(1, Ordering::Relaxed);
    }
}

/// The coordinator's view of a histogram: what was added since the last look.
pub struct LatencyReader {
    seen: [u64; LATENCY_BUCKETS],
}

impl Default for LatencyReader {
    fn default() -> Self {
        Self {
            seen: [0; LATENCY_BUCKETS],
        }
    }
}

impl LatencyReader {
    /// Median latency (ns per MiB) of the writes recorded since the previous
    /// call, or `None` when there were too few to say. The lower median is
    /// used, so one stalled write cannot move a window.
    pub fn window_median(&mut self, histogram: &LatencyHistogram) -> Option<u64> {
        let mut fresh = [0u64; LATENCY_BUCKETS];
        let mut total = 0u64;
        for (index, bucket) in histogram.buckets.iter().enumerate() {
            let now = bucket.load(Ordering::Relaxed);
            fresh[index] = now.saturating_sub(self.seen[index]);
            self.seen[index] = now;
            total += fresh[index];
        }
        if total < MIN_LATENCY_WRITES {
            return None;
        }
        let target = total.div_ceil(2);
        let mut cumulative = 0u64;
        for (index, count) in fresh.iter().enumerate() {
            cumulative += count;
            if cumulative >= target {
                return Some(latency_bucket_floor(index));
            }
        }
        None
    }
}

/// Windows that form the latency baseline before anything is compared to it.
const LATENCY_BASELINE_WINDOWS: u32 = 4;
/// A window is "raised" when its median is this many times the baseline.
const LATENCY_RISE: f64 = 2.0;
/// Raised windows in a row that make the rise "sustained".
const LATENCY_SUSTAIN: u32 = 3;

/// Compares each window's median write latency with a slow-moving baseline of
/// its own and reports a rise only when several windows in a row agree.
#[derive(Clone, Debug, Default)]
pub struct LatencyTracker {
    baseline: f64,
    baseline_windows: u32,
    raised: u32,
    last: f64,
}

impl LatencyTracker {
    /// Feed one window's median. Returns true while the rise is sustained.
    pub fn observe(&mut self, median: Option<u64>) -> bool {
        let Some(median) = median else {
            // Too few writes to judge: neither a rise nor proof of calm.
            self.raised = 0;
            return false;
        };
        let median = median as f64;
        self.last = median;
        if self.baseline_windows < LATENCY_BASELINE_WINDOWS {
            self.baseline_windows += 1;
            self.baseline += (median - self.baseline) / f64::from(self.baseline_windows);
            return false;
        }
        if median >= self.baseline * LATENCY_RISE {
            self.raised += 1;
            return self.raised >= LATENCY_SUSTAIN;
        }
        self.raised = 0;
        self.baseline += (median - self.baseline) * 0.1;
        false
    }

    /// Forget the current streak (the setting that caused it was undone).
    pub fn calm(&mut self) {
        self.raised = 0;
    }

    /// Accept the present level as normal, so one rise is acted on once.
    pub fn rebase(&mut self) {
        if self.last > 0.0 {
            self.baseline = self.last;
        }
        self.raised = 0;
    }
}

// ---------------------------------------------------------------------------
// Sampling windows
// ---------------------------------------------------------------------------

/// One closed measuring window.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Window {
    pub bytes: u64,
    pub elapsed: Duration,
    /// The lane was full for most of the window.
    pub saturated: bool,
}

/// Cuts a transfer into windows sized by work done: a window closes once at
/// least a second has passed and enough bytes moved, or at a two-second cap.
/// A link too slow to fill a window stretches the cap to five seconds.
#[derive(Clone, Debug)]
pub struct Sampler {
    started: Instant,
    observed: Instant,
    bytes_at_start: u64,
    busy: Duration,
    was_busy: bool,
    cap: Duration,
    min_work: u64,
}

impl Sampler {
    pub fn new(now: Instant, current: u64, min_work: u64) -> Self {
        Self {
            started: now,
            observed: now,
            bytes_at_start: current,
            busy: Duration::ZERO,
            was_busy: false,
            cap: WINDOW_CAP,
            min_work: min_work.max(1),
        }
    }

    /// `current` is the lane's cumulative byte count; `saturated` whether the
    /// lane is full right now; `force` closes the window at once (an I/O retry).
    pub fn observe(
        &mut self,
        now: Instant,
        current: u64,
        saturated: bool,
        force: bool,
    ) -> Option<Window> {
        if self.was_busy {
            self.busy += now.saturating_duration_since(self.observed);
        }
        self.observed = now;
        self.was_busy = saturated;
        let elapsed = now.saturating_duration_since(self.started);
        let bytes = current.saturating_sub(self.bytes_at_start);
        let worked = bytes >= self.min_work;
        if !(force || elapsed >= self.cap || (elapsed >= MIN_WINDOW && worked)) {
            return None;
        }
        let window = Window {
            bytes,
            elapsed,
            saturated: self.busy.as_secs_f64() >= elapsed.as_secs_f64() * BUSY_SHARE,
        };
        if !force {
            self.cap = if worked {
                WINDOW_CAP
            } else {
                (self.cap * 2).min(SLOW_WINDOW_CAP)
            };
        }
        self.started = now;
        self.bytes_at_start = current;
        self.busy = Duration::ZERO;
        Some(window)
    }
}

// ---------------------------------------------------------------------------
// Lanes
// ---------------------------------------------------------------------------

/// Files below this size travel in the small-file lane.
pub const SMALL_LANE_MAX_BYTES: u64 = 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Lane {
    Large,
    Small,
}

impl Lane {
    pub const ALL: [Lane; 2] = [Lane::Large, Lane::Small];

    pub fn index(self) -> usize {
        match self {
            Self::Large => 0,
            Self::Small => 1,
        }
    }

    pub fn for_size(bytes: u64) -> Self {
        if bytes < SMALL_LANE_MAX_BYTES {
            Self::Small
        } else {
            Self::Large
        }
    }
}

/// The lane a file is scheduled in: by size with two lanes, lane 0 otherwise.
pub fn lane_index(two_lanes: bool, bytes: u64) -> usize {
    if two_lanes {
        Lane::for_size(bytes).index()
    } else {
        0
    }
}

/// Two lanes are used only where nothing else fixes the dispatch order: a
/// custom file limit is one limit, and a chosen queue order is one queue.
pub fn lanes_enabled(policy: &Policy) -> bool {
    policy.mode != Mode::Manual && policy.order_by == OrderBy::Default
}

/// The most files a job may run at once whatever their size: the configured
/// ceiling for the mode, or the custom limit. Memory is enforced per lane.
pub fn total_limit(policy: &Policy) -> usize {
    let ceiling = policy.limit(1);
    if policy.mode == Mode::Manual {
        policy.manual_concurrency.clamp(1, ceiling)
    } else {
        ceiling
    }
}

/// Share `limit` between lanes that each want `wanted` files. A lane with no
/// work gets nothing; a lane with work always gets at least one file when the
/// limit allows two, and an oversubscribed limit is split in proportion.
pub fn lane_caps(limit: usize, wanted: [usize; 2], demand: [bool; 2]) -> [usize; 2] {
    let limit = limit.max(1);
    let wanted = [wanted[0].max(1), wanted[1].max(1)];
    match demand {
        [false, false] => [0, 0],
        [true, false] => [wanted[0].min(limit), 0],
        [false, true] => [0, wanted[1].min(limit)],
        [true, true] => {
            if wanted[0] + wanted[1] <= limit {
                return wanted;
            }
            if limit == 1 {
                // One slot cannot be split; the pool hands it to the older file.
                return [1, 1];
            }
            let share = (limit * wanted[0] + (wanted[0] + wanted[1]) / 2) / (wanted[0] + wanted[1]);
            let first = share.clamp(1, limit - 1);
            [first, limit - first]
        }
    }
}

// ---------------------------------------------------------------------------
// Controller
// ---------------------------------------------------------------------------

/// Bounded concurrency feedback for one lane of one transfer.
pub struct Controller {
    pub policy: Policy,
    pub concurrency: usize,
    pub explanation: &'static str,
    limit: usize,
    /// Room left by the other lane, when two lanes share the limit.
    ceiling: Option<usize>,
    buffer_bytes: usize,
    effective_buffer: usize,
    best_concurrency: usize,
    best_rate: f64,
    best_error: f64,
    estimator: RateEstimator,
    /// The window after a change straddles two settings and is not counted.
    settle: bool,
    probing: bool,
    cooldown: usize,
    learned: bool,
    try_lower: bool,
    lower_tested: bool,
    /// Upward probes rejected in a row; each one doubles the next cooldown so
    /// storage whose optimum is below the limit is not re-probed constantly.
    rejected_probes: u32,
    latency: LatencyTracker,
    decisions: u64,
    /// Countable windows since the running experiment started.
    probe_windows: u32,
    /// The other lane is running an experiment; do not start one here.
    hold: bool,
    /// A step up has paid off, so there is no reason to suspect fewer would do.
    grew: bool,
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
        Self::new_for_route(
            policy,
            buffer_bytes,
            remembered,
            bytes,
            files,
            DriveKind::Unknown,
        )
    }

    /// `route` is the slower of the source and destination drive kinds.
    pub fn new_for_route(
        policy: Policy,
        buffer_bytes: usize,
        remembered: Option<usize>,
        bytes: u64,
        files: usize,
        route: DriveKind,
    ) -> Self {
        let avg_size = bytes / (files as u64).max(1);
        let small_file_burst = files >= 16 && avg_size <= 256 * 1024;
        let effective_buffer = if small_file_burst {
            buffer_bytes.min(usize::try_from(avg_size.max(64 * 1024)).unwrap_or(buffer_bytes))
        } else {
            buffer_bytes
        };
        let limit = policy.limit(effective_buffer);
        let (start, kind_reason) = starting_concurrency(policy.mode, route, small_file_burst);
        let concurrency = if policy.mode == Mode::Manual {
            policy.manual_concurrency.clamp(1, limit)
        } else if let Some(remembered) = remembered {
            // What worked last time on this route, within the configured ceiling.
            remembered.clamp(1, limit)
        } else {
            start.clamp(1, limit)
        };
        let explanation = if policy.mode == Mode::Manual {
            "Manual limit"
        } else if remembered.is_some() {
            "Rechecking remembered setting"
        } else if let Some(reason) = kind_reason.filter(|_| {
            // A small-file batch on fast storage is better described as that.
            !(small_file_burst && route == DriveKind::SolidState)
        }) {
            reason
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
            ceiling: None,
            buffer_bytes,
            effective_buffer,
            best_concurrency: concurrency,
            best_rate: 0.0,
            best_error: 0.0,
            estimator: RateEstimator::default(),
            settle: false,
            probing: false,
            cooldown: 0,
            learned: false,
            try_lower: false,
            lower_tested: false,
            rejected_probes: 0,
            latency: LatencyTracker::default(),
            decisions: 0,
            probe_windows: 0,
            hold: false,
            grew: false,
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
            updated.decisions = self.decisions;
            updated.hold = self.hold;
            let ceiling = self.ceiling;
            *self = updated;
            self.set_ceiling(ceiling);
        } else {
            self.policy = policy;
        }
    }

    /// The most files this lane may use: its own limit, less what the other
    /// lane holds when two lanes share the job.
    fn cap(&self) -> usize {
        self.ceiling
            .map_or(self.limit, |ceiling| self.limit.min(ceiling.max(1)))
    }

    /// Tell the lane how much of the shared limit is its to use. Shrinking
    /// below the current setting applies at once and ends any experiment.
    pub fn set_ceiling(&mut self, ceiling: Option<usize>) {
        self.ceiling = ceiling;
        let cap = self.cap();
        if self.concurrency > cap {
            self.concurrency = cap;
            self.best_concurrency = self.best_concurrency.min(cap);
            self.probing = false;
            self.estimator.reset();
            self.settle = true;
        }
    }

    /// One measuring window. Windows shorter than a second, or during which
    /// the lane was not kept full, are not evidence for any setting.
    pub fn sample(&mut self, bytes: u64, elapsed: Duration, saturated: bool, retries: u64) {
        self.sample_with_latency(bytes, elapsed, saturated, retries, None);
    }

    /// As [`Self::sample`], with the window's median write latency when enough
    /// writes were timed.
    pub fn sample_with_latency(
        &mut self,
        bytes: u64,
        elapsed: Duration,
        saturated: bool,
        retries: u64,
        latency: Option<u64>,
    ) {
        // Custom settings are authoritative; errors only delay their failing file.
        if self.policy.mode == Mode::Manual {
            self.concurrency = self.policy.manual_concurrency.clamp(1, self.cap());
            self.explanation = "Custom limits (memory and provider limits still apply)";
            return;
        }
        if retries > 0 {
            self.concurrency = (self.concurrency / 2).max(1);
            self.best_concurrency = self.concurrency;
            self.best_rate = 0.0;
            self.estimator.reset();
            self.latency.calm();
            self.settle = true;
            self.probing = false;
            self.learned = false;
            self.cooldown = COOLDOWN_SAMPLES;
            self.explanation = "Reduced concurrency after an I/O retry";
            return;
        }
        if self.settle {
            self.settle = false;
            self.estimator.reset();
            return;
        }
        if elapsed < MIN_WINDOW || !saturated {
            self.estimator.reset();
            self.latency.calm();
            return;
        }
        if self.latency.observe(latency) {
            if self.probing && self.concurrency > self.best_concurrency {
                // The device is queueing: do not wait for throughput to fall.
                self.reject_probe(false, "Write latency rose; keeping the previous setting");
                self.probing = false;
                self.latency.calm();
                return;
            }
            if !self.probing {
                self.cooldown = self.cooldown.max(COOLDOWN_SAMPLES);
                if !self.lower_tested && self.concurrency > 1 {
                    self.try_lower = true;
                }
                self.explanation = "Write latency rose; holding";
                self.latency.rebase();
                self.estimator.reset();
                return;
            }
        }
        let rate = bytes as f64 / elapsed.as_secs_f64();
        if rate <= 0.0 {
            // Hydration, metadata and NAS flush waits do not prove contention.
            // Do not reduce workers just because no bytes arrived in a window.
            self.estimator.reset();
            return;
        }
        // Mixed file sizes and background traffic make single windows noisy;
        // only a reading whose windows agree is acted on.
        self.estimator.push(rate);
        let Some(reading) = self.estimator.reading() else {
            if self.probing {
                self.probe_windows += 1;
                if self.probe_windows >= UNREAD_PROBE_WINDOWS {
                    // An experiment that cannot be read is not worth keeping.
                    let lower = self.concurrency < self.best_concurrency;
                    self.reject_probe(lower, "Keeping the faster stable setting");
                    self.probing = false;
                }
            }
            return;
        };
        self.estimator.reset();
        self.decide(reading);
    }

    fn reject_probe(&mut self, lower: bool, explanation: &'static str) {
        self.concurrency = self.best_concurrency;
        self.cooldown = COOLDOWN_SAMPLES << self.rejected_probes.min(MAX_PROBE_BACKOFF);
        if !lower {
            self.rejected_probes = self.rejected_probes.saturating_add(1);
        }
        self.explanation = explanation;
        self.try_lower = !lower && !self.lower_tested;
        self.estimator.reset();
        self.settle = true;
    }

    fn start_probe(&mut self) {
        self.probing = true;
        self.settle = true;
        self.probe_windows = 0;
    }

    /// At the ceiling the other lane leaves, with the lane's own limit above.
    fn shares_limit(&self) -> bool {
        let cap = self.cap();
        self.concurrency >= cap && cap < self.limit
    }

    /// Whether the other lane is mid-experiment. While it is, this lane keeps
    /// measuring but starts no experiment of its own.
    pub fn hold_experiments(&mut self, hold: bool) {
        self.hold = hold;
    }

    fn decide(&mut self, reading: Reading) {
        self.decisions += 1;
        let rate = reading.rate;
        if self.probing {
            let lower = self.concurrency < self.best_concurrency;
            let needed = if lower {
                LOWER_KEEP
            } else {
                // The gain must clear the noise of both readings.
                1.0 + PROBE_GAIN.max(2.0 * (reading.error + self.best_error))
            };
            if rate >= self.best_rate * needed {
                self.best_rate = rate;
                self.best_error = reading.error;
                self.best_concurrency = self.concurrency;
                if !lower {
                    // Only a successful step upward proves the ceiling moved.
                    self.rejected_probes = 0;
                    self.grew = true;
                }
                self.explanation = if lower {
                    "Similar speed with fewer simultaneous files"
                } else {
                    "Higher concurrency improved throughput"
                };
            } else {
                self.reject_probe(lower, "Keeping the faster stable setting");
            }
            self.probing = false;
            self.learned = true;
        } else {
            self.best_rate = rate;
            self.best_error = reading.error;
            self.best_concurrency = self.concurrency;
            self.learned = true;
            let cap = self.cap();
            if self.cooldown > 0 {
                self.cooldown -= 1;
            } else if self.hold {
                // One experiment at a time across lanes, so each is judged
                // against a steady neighbour.
            } else if self.concurrency > 1
                && (self.try_lower || (self.shares_limit() && !self.lower_tested && !self.grew))
            {
                // After a rejected step up, or when the other lane holds the
                // room to step up and no step up has paid off yet: see whether
                // fewer files do as well.
                self.concurrency = (self.concurrency / 2).max(1);
                self.start_probe();
                self.try_lower = false;
                self.lower_tested = true;
                self.explanation = "Testing whether fewer files reduce contention";
            } else if self.concurrency < cap {
                self.concurrency = (self.concurrency + (self.concurrency / 2).max(1)).min(cap);
                self.start_probe();
                self.explanation = "Testing more simultaneous files";
            } else {
                self.explanation = "At the configured concurrency limit";
            }
        }
    }

    /// Bytes a measuring window should see before it closes early: eight
    /// buffers, and for ordinary files at least four progress steps, because
    /// a file reports its progress in steps of four mebibytes.
    pub fn window_work(&self) -> u64 {
        let buffers = 8 * self.effective_buffer as u64;
        if self.effective_buffer < self.buffer_bytes {
            buffers
        } else {
            buffers.max(16 * 1024 * 1024)
        }
    }

    pub fn learned_concurrency(&self) -> Option<usize> {
        (self.learned && self.policy.mode != Mode::Manual).then_some(self.best_concurrency)
    }

    /// Trusted readings acted on so far.
    pub fn decisions(&self) -> u64 {
        self.decisions
    }

    pub fn is_probing(&self) -> bool {
        self.probing
    }

    pub fn best_concurrency(&self) -> usize {
        self.best_concurrency
    }

    pub fn cooldown(&self) -> usize {
        self.cooldown
    }
}

// ---------------------------------------------------------------------------
// The lanes of one transfer
// ---------------------------------------------------------------------------

struct LaneTuning {
    controller: Controller,
    sampler: Sampler,
    reader: LatencyReader,
    started: bool,
}

/// What the pool measured for one lane since the previous call.
#[derive(Clone, Copy, Debug, Default)]
pub struct LaneReport {
    /// Bytes this lane has moved in total (finished files plus files in flight).
    pub bytes: u64,
    /// The lane is running as many files as it may.
    pub saturated: bool,
    /// The lane has files queued or in flight.
    pub demand: bool,
    /// I/O retries in this lane since the previous report.
    pub retries: u64,
}

/// The large-file and small-file lanes of one transfer: each has its own
/// controller, measuring windows and latency baseline; together they never
/// plan more files than the job's limit, and only one experiments at a time.
/// With lanes off, everything runs in lane 0 exactly as a single pool.
pub struct Lanes {
    lanes: [LaneTuning; 2],
    two: bool,
    demand: [bool; 2],
    /// The lane whose reason is shown: the last one that changed its mind.
    spoke: usize,
}

impl Lanes {
    pub fn new(policy: Policy, buffer_bytes: usize, now: Instant) -> Self {
        let lane = |policy: Policy| {
            let controller = Controller::new(policy, buffer_bytes, None);
            LaneTuning {
                sampler: Sampler::new(now, 0, controller.window_work()),
                controller,
                reader: LatencyReader::default(),
                started: false,
            }
        };
        Self {
            lanes: [lane(policy.clone()), lane(policy)],
            two: false,
            demand: [false; 2],
            spoke: 0,
        }
    }

    /// A scheduler run starts: fix whether files are split by size for its
    /// whole length, and count each lane's bytes from zero again.
    pub fn begin(&mut self, two: bool, now: Instant) {
        self.two = two;
        self.demand = [false; 2];
        for lane in &mut self.lanes {
            lane.sampler = Sampler::new(now, 0, lane.controller.window_work());
        }
    }

    pub fn two(&self) -> bool {
        self.two
    }

    pub fn started(&self, lane: usize) -> bool {
        self.lanes[lane].started
    }

    /// Give a lane the controller for its workload. `bytes` is what the lane
    /// has moved so far in this scheduler run.
    pub fn start(&mut self, lane: usize, controller: Controller, now: Instant, bytes: u64) {
        let hold = self.lanes[lane].controller.hold;
        let slot = &mut self.lanes[lane];
        slot.sampler = Sampler::new(now, bytes, controller.window_work());
        slot.controller = controller;
        slot.controller.hold_experiments(hold);
        slot.started = true;
        // A lane is started because a file is waiting in it.
        self.demand[lane] = true;
        self.spoke = lane;
        self.share();
    }

    pub fn controller(&self, lane: usize) -> &Controller {
        &self.lanes[lane].controller
    }

    /// The policy new files are dispatched with.
    pub fn policy(&self) -> &Policy {
        &self.lanes[0].controller.policy
    }

    pub fn concurrency(&self, lane: usize) -> usize {
        self.lanes[lane].controller.concurrency
    }

    /// The most files the job may run at once across both lanes.
    pub fn total_limit(&self) -> usize {
        total_limit(self.policy())
    }

    pub fn update_policy(&mut self, policy: Policy, buffer_bytes: [usize; 2]) {
        for (lane, buffer) in self.lanes.iter_mut().zip(buffer_bytes) {
            lane.controller.update_policy(policy.clone(), buffer);
        }
        self.share();
    }

    /// Feed both lanes what the pool measured. `latency` holds each lane's
    /// write-latency histogram.
    pub fn observe(
        &mut self,
        now: Instant,
        reports: [LaneReport; 2],
        latency: [&LatencyHistogram; 2],
    ) {
        self.demand = [reports[0].demand, reports[1].demand];
        for index in 0..2 {
            if !self.lanes[index].started || (index == 1 && !self.two) {
                continue;
            }
            let report = reports[index];
            let other = &self.lanes[1 - index];
            // An experiment in a lane that is not full is not being measured,
            // so it does not keep this lane waiting.
            let other_testing = self.two
                && other.started
                && reports[1 - index].saturated
                && other.controller.probing;
            let lane = &mut self.lanes[index];
            let Some(window) =
                lane.sampler
                    .observe(now, report.bytes, report.saturated, report.retries > 0)
            else {
                continue;
            };
            let median = lane.reader.window_median(latency[index]);
            let before = (lane.controller.concurrency, lane.controller.explanation);
            lane.controller.hold_experiments(other_testing);
            lane.controller.sample_with_latency(
                window.bytes,
                window.elapsed,
                window.saturated,
                report.retries,
                median,
            );
            if before != (lane.controller.concurrency, lane.controller.explanation) {
                self.spoke = index;
            }
            self.share();
        }
        self.share();
    }

    /// Keep the lanes' plans within the job's limit: an oversubscribed limit
    /// is split in proportion, and each lane may grow only into room the
    /// other is not using. A lane alone has the whole limit.
    fn share(&mut self) {
        let both = self.two && self.demand == [true, true] && self.lanes.iter().all(|l| l.started);
        if !both {
            for lane in &mut self.lanes {
                lane.controller.set_ceiling(None);
            }
            return;
        }
        let limit = self.total_limit();
        let wanted = [self.concurrency(0), self.concurrency(1)];
        let ceilings = if wanted[0] + wanted[1] > limit {
            lane_caps(limit, wanted, [true, true])
        } else {
            [
                limit.saturating_sub(wanted[1]).max(1),
                limit.saturating_sub(wanted[0]).max(1),
            ]
        };
        for (lane, ceiling) in self.lanes.iter_mut().zip(ceilings) {
            lane.controller.set_ceiling(Some(ceiling));
        }
    }

    fn shown(&self) -> [bool; 2] {
        if !self.two {
            return [true, false];
        }
        let started = [self.lanes[0].started, self.lanes[1].started];
        let busy = [started[0] && self.demand[0], started[1] && self.demand[1]];
        if busy != [false, false] {
            busy
        } else if started != [false, false] {
            [self.spoke == 0, self.spoke == 1]
        } else {
            [true, false]
        }
    }

    /// Files the job plans to run at once, for display.
    pub fn planned(&self) -> usize {
        let shown = self.shown();
        let sum: usize = (0..2)
            .filter(|&lane| shown[lane])
            .map(|lane| self.concurrency(lane))
            .sum();
        sum.min(self.total_limit().max(1))
    }

    /// The reason shown in the Transfers window.
    pub fn explanation(&self) -> String {
        match self.shown() {
            [true, true] => format!(
                "{} large + {} small \u{b7} {}",
                self.concurrency(0),
                self.concurrency(1),
                self.lanes[self.spoke].controller.explanation
            ),
            [false, true] => self.lanes[1].controller.explanation.into(),
            _ => self.lanes[0].controller.explanation.into(),
        }
    }
}

#[cfg(test)]
mod tests;
