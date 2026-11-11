use super::*;

const SECOND: Duration = Duration::from_secs(1);

/// Feed steady one-second windows at `rate` until the controller acts on a
/// reading (or shows, by not acting, that it never will).
fn observe(c: &mut Controller, rate: u64) {
    let before = c.decisions();
    for _ in 0..16 {
        c.sample(rate, SECOND, true, 0);
        if c.decisions() != before {
            return;
        }
    }
}

/// Deterministic noise in `[-spread, +spread]` percent around `rate`.
struct Noise(u64);

impl Noise {
    fn next(&mut self, rate: f64, spread_percent: f64) -> u64 {
        // splitmix64: neighbouring seeds give unrelated streams.
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^= z >> 31;
        let unit = ((z >> 11) as f64 / (1u64 << 53) as f64) * 2.0 - 1.0;
        (rate * (1.0 + unit * spread_percent / 100.0)).max(1.0) as u64
    }
}

// ----- starting point ------------------------------------------------------

fn start(mode: Mode, route: DriveKind, small: bool) -> usize {
    let (bytes, files) = if small {
        (64 * 4096, 64)
    } else {
        (64 << 20, 4)
    };
    Controller::new_for_route(
        Policy {
            mode,
            max_concurrency: 32,
            memory_budget_mb: 512,
            ..Policy::default()
        },
        4 << 20,
        None,
        bytes,
        files,
        route,
    )
    .concurrency
}

#[test]
fn unknown_storage_keeps_the_previous_starting_points() {
    assert_eq!(start(Mode::Conservative, DriveKind::Unknown, false), 2);
    assert_eq!(start(Mode::Automatic, DriveKind::Unknown, false), 4);
    assert_eq!(start(Mode::Maximum, DriveKind::Unknown, false), 8);
    assert_eq!(start(Mode::Conservative, DriveKind::Unknown, true), 2);
    assert_eq!(start(Mode::Automatic, DriveKind::Unknown, true), 12);
    assert_eq!(start(Mode::Maximum, DriveKind::Unknown, true), 16);
}

#[test]
fn a_hard_disk_starts_with_one_or_two_files_in_every_mode() {
    for mode in [Mode::Conservative, Mode::Automatic, Mode::Maximum] {
        for small in [false, true] {
            let count = start(mode, DriveKind::Rotational, small);
            assert!((1..=2).contains(&count), "{mode:?} small={small}: {count}");
            assert_eq!(start(mode, DriveKind::Optical, small), 1);
        }
    }
    assert_eq!(start(Mode::Automatic, DriveKind::Rotational, false), 1);
    assert_eq!(start(Mode::Automatic, DriveKind::Rotational, true), 2);
}

#[test]
fn solid_state_starts_above_network_which_starts_above_a_hard_disk() {
    for mode in [Mode::Automatic, Mode::Maximum] {
        for small in [false, true] {
            let ssd = start(mode, DriveKind::SolidState, small);
            let network = start(mode, DriveKind::Network, small);
            let disk = start(mode, DriveKind::Rotational, small);
            assert!(
                ssd > network && network > disk,
                "{mode:?} {small}: {ssd} {network} {disk}"
            );
            assert!(ssd >= start(mode, DriveKind::Unknown, small));
        }
    }
    assert_eq!(start(Mode::Automatic, DriveKind::SolidState, false), 6);
    assert_eq!(start(Mode::Automatic, DriveKind::SolidState, true), 16);
    assert_eq!(start(Mode::Automatic, DriveKind::Network, false), 3);
    assert_eq!(start(Mode::Automatic, DriveKind::Network, true), 8);
    assert_eq!(start(Mode::Automatic, DriveKind::Removable, true), 2);
}

#[test]
fn the_slower_end_of_a_route_sets_the_pace() {
    use DriveKind::*;
    assert_eq!(SolidState.slower(Rotational), Rotational);
    assert_eq!(Rotational.slower(Network), Rotational);
    assert_eq!(Unknown.slower(Rotational), Rotational);
    assert_eq!(SolidState.slower(Network), Network);
    assert_eq!(SolidState.slower(Removable), Removable);
    assert_eq!(SolidState.slower(SolidState), SolidState);
    // Unknown storage does not borrow a faster guess from the other end.
    assert_eq!(SolidState.slower(Unknown), Unknown);
    assert_eq!(Network.slower(Unknown), Unknown);
    assert_eq!(Optical.slower(Rotational), Optical);
}

#[test]
fn drive_kinds_follow_the_drive_list() {
    use DriveKind::*;
    assert_eq!(DriveKind::classify(true, "", false, None), Network);
    assert_eq!(DriveKind::classify(false, "Network", false, None), Network);
    assert_eq!(
        DriveKind::classify(false, "Removable", false, None),
        Removable
    );
    assert_eq!(DriveKind::classify(false, "CD-ROM", false, None), Optical);
    assert_eq!(
        DriveKind::classify(false, "Fixed", false, Some(true)),
        Rotational
    );
    assert_eq!(
        DriveKind::classify(false, "Fixed", false, Some(false)),
        SolidState
    );
    // No answer from the driver, a cloud mount, a RAM disk: today's defaults.
    assert_eq!(DriveKind::classify(false, "Fixed", false, None), Unknown);
    assert_eq!(
        DriveKind::classify(false, "Fixed", true, Some(false)),
        Unknown
    );
    assert_eq!(DriveKind::classify(false, "RAM Disk", false, None), Unknown);
}

#[test]
fn remembered_manual_and_limits_outrank_the_storage_kind() {
    let policy = Policy::default();
    let remembered = Controller::new_for_route(
        policy.clone(),
        4 << 20,
        Some(9),
        64 << 20,
        4,
        DriveKind::Rotational,
    );
    assert_eq!(remembered.concurrency, 9);
    assert_eq!(remembered.explanation, "Rechecking remembered setting");

    let manual = Controller::new_for_route(
        Policy {
            mode: Mode::Manual,
            manual_concurrency: 7,
            ..Policy::default()
        },
        4 << 20,
        None,
        64 << 20,
        4,
        DriveKind::Rotational,
    );
    assert_eq!(manual.concurrency, 7);
    assert_eq!(manual.explanation, "Manual limit");

    let capped = Controller::new_for_route(
        Policy {
            max_concurrency: 3,
            ..Policy::default()
        },
        4 << 20,
        None,
        64 * 4096,
        64,
        DriveKind::SolidState,
    );
    assert_eq!(capped.concurrency, 3);

    let disk = Controller::new_for_route(policy, 4 << 20, None, 64 << 20, 4, DriveKind::Rotational);
    assert_eq!(disk.concurrency, 1);
    assert_eq!(disk.explanation, "Starting low for a hard disk");
}

// ----- estimator -----------------------------------------------------------

#[test]
fn steady_input_is_trusted_after_three_windows() {
    let mut estimator = RateEstimator::default();
    estimator.push(1000.0);
    estimator.push(1010.0);
    assert_eq!(estimator.reading(), None);
    estimator.push(990.0);
    let reading = estimator.reading().expect("three agreeing windows");
    assert!((reading.rate - 1000.0).abs() < 1.0);
    assert!(reading.error < 0.01);
}

#[test]
fn noisier_input_needs_more_windows_and_wild_input_is_never_trusted() {
    let mut estimator = RateEstimator::default();
    let mut noise = Noise(7);
    let mut trusted_at = None;
    for window in 1..=RATE_WINDOWS {
        estimator.push(noise.next(1000.0, 20.0) as f64);
        if trusted_at.is_none() && estimator.reading().is_some() {
            trusted_at = Some(window);
        }
    }
    let trusted_at = trusted_at.expect("moderate noise averages out");
    assert!(trusted_at > MIN_RATE_WINDOWS, "trusted at {trusted_at}");
    let reading = estimator.reading().unwrap();
    assert!((reading.rate - 1000.0).abs() < 120.0, "{reading:?}");

    let mut wild = RateEstimator::default();
    for window in 0..200 {
        wild.push(if window % 2 == 0 { 100.0 } else { 1000.0 });
        assert_eq!(wild.reading(), None);
    }
}

// ----- sampling windows ----------------------------------------------------

#[test]
fn a_window_needs_a_second_and_enough_work_or_the_cap() {
    let start = Instant::now();
    let at = |ms: u64| start + Duration::from_millis(ms);
    let mut sampler = Sampler::new(start, 0, 100);
    sampler.observe(start, 0, true, false);
    // Plenty of work but not a second yet.
    assert_eq!(sampler.observe(at(500), 1000, true, false), None);
    let window = sampler.observe(at(1000), 2000, true, false).unwrap();
    assert_eq!(window.bytes, 2000);
    assert_eq!(window.elapsed, SECOND);
    assert!(window.saturated);
    // A second passed but too little moved: wait for the two-second cap.
    assert_eq!(sampler.observe(at(2100), 2050, true, false), None);
    let window = sampler.observe(at(3000), 2060, true, false).unwrap();
    assert_eq!(window.bytes, 60);
    assert_eq!(window.elapsed, Duration::from_secs(2));
}

#[test]
fn a_slow_link_stretches_the_window_to_five_seconds_and_recovers() {
    let start = Instant::now();
    let at = |ms: u64| start + Duration::from_millis(ms);
    let mut sampler = Sampler::new(start, 0, 1000);
    assert!(sampler.observe(at(2000), 10, true, false).is_some());
    // Cap is now four seconds, then five.
    assert_eq!(sampler.observe(at(5900), 20, true, false), None);
    assert!(sampler.observe(at(6000), 20, true, false).is_some());
    assert_eq!(sampler.observe(at(10_900), 30, true, false), None);
    assert!(sampler.observe(at(11_000), 30, true, false).is_some());
    assert_eq!(sampler.observe(at(15_900), 40, true, false), None);
    // Enough work closes the window early and restores the short cap.
    let window = sampler.observe(at(12_000), 5000, true, false).unwrap();
    assert_eq!(window.elapsed, SECOND);
    assert_eq!(sampler.observe(at(13_900), 5001, true, false), None);
    assert!(sampler.observe(at(14_000), 5001, true, false).is_some());
}

#[test]
fn a_window_counts_as_full_only_when_the_lane_was_busy_for_most_of_it() {
    let start = Instant::now();
    let at = |ms: u64| start + Duration::from_millis(ms);
    let mut sampler = Sampler::new(start, 0, 1);
    sampler.observe(at(0), 0, true, false);
    sampler.observe(at(500), 10, false, false);
    let window = sampler.observe(at(1000), 20, true, false).unwrap();
    assert!(!window.saturated, "half the window was idle");
    sampler.observe(at(1000), 20, true, false);
    let window = sampler.observe(at(2000), 40, true, false).unwrap();
    assert!(window.saturated);
    // A retry closes the window at once, whatever its length.
    let window = sampler.observe(at(2001), 40, true, true).unwrap();
    assert_eq!(window.elapsed, Duration::from_millis(1));
}

// ----- controller: existing behaviour ---------------------------------------

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
    // However long the disagreement lasts, and at the new window length too.
    for window in 0..200 {
        c.sample(if window % 2 == 0 { 100 } else { 1000 }, SECOND, true, 0);
        assert_eq!(c.concurrency, 4);
    }
    assert_eq!(c.learned_concurrency(), None);
    // Windows during which the lane drained are never evidence.
    for _ in 0..50 {
        c.sample(1000, SECOND, false, 0);
    }
    assert_eq!(c.learned_concurrency(), None);
    // Nor are windows shorter than a second.
    for _ in 0..50 {
        c.sample(1000, Duration::from_millis(900), true, 0);
    }
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
fn slower_storage_can_choose_fewer_workers_after_a_plateau() {
    let mut c = Controller::new(Policy::default(), 4 << 20, None);
    observe(&mut c, 1000); // Probe 6 from the baseline of 4.
    observe(&mut c, 1000); // Roll back and schedule a lower probe.
    assert_eq!(c.concurrency, 4);
    // The cooldown passes, then the lower setting is tried.
    for _ in 0..=COOLDOWN_SAMPLES {
        assert_eq!(c.concurrency, 4);
        observe(&mut c, 1000);
    }
    assert_eq!(c.concurrency, 2);
    observe(&mut c, 990);
    assert_eq!(c.learned_concurrency(), Some(2));
}

#[test]
fn cloud_hydration_and_nas_flush_gaps_do_not_reduce_concurrency() {
    for window in [SECOND, Duration::from_secs(2), Duration::from_secs(5)] {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        for _ in 0..24 {
            c.sample(0, window, true, 0);
        }
        assert_eq!(c.concurrency, 4);
        assert_eq!(c.learned_concurrency(), None);
    }
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
        // A raised write latency is no reason to change a custom limit either.
        c.sample_with_latency(0, Duration::from_secs(30), true, retries, Some(1 << 40));
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
        for window in [SECOND, Duration::from_secs(2), Duration::from_secs(5)] {
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
            for _ in 0..4 {
                for rate in [100_000, 0, 4_000, 0, 1_000, 0, 300, 0, 100, 0] {
                    c.sample(rate, window, true, 0);
                    assert_eq!(c.concurrency, initial);
                }
            }
            // The same collapse without the quiet windows: still not evidence.
            for rate in [100_000, 30_000, 4_000, 1_000, 300, 100, 30] {
                c.sample(rate, window, true, 0);
                assert!(c.concurrency >= initial, "{mode:?}: {}", c.concurrency);
            }
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

/// Storage whose optimum is below the limit must not be re-probed at the
/// same cadence forever: each rejected upward probe lengthens the pause.
#[test]
fn rejected_probes_lengthen_the_cooldown() {
    let mut c = Controller::new(Policy::default(), 4 << 20, None);
    observe(&mut c, 1000); // baseline, starts an upward probe
    assert!(c.is_probing() && c.concurrency > c.best_concurrency());
    observe(&mut c, 900); // rejected
    assert_eq!(c.cooldown(), COOLDOWN_SAMPLES);

    // Keep the rate flat until the controller tries upward again.
    let mut reached_upward_probe = false;
    for _ in 0..64 {
        observe(&mut c, 1000);
        if c.is_probing() && c.concurrency > c.best_concurrency() {
            reached_upward_probe = true;
            break;
        }
    }
    assert!(reached_upward_probe, "controller never probed upward again");
    observe(&mut c, 900); // rejected again
    assert!(
        c.cooldown() >= 2 * COOLDOWN_SAMPLES,
        "second rejection kept cooldown at {}",
        c.cooldown()
    );
}

// ----- controller: faster sampling ------------------------------------------

/// Windows a steady transfer needs for each decision, as seconds of transfer.
#[test]
fn a_steady_transfer_decides_within_five_seconds() {
    let mut c = Controller::new(Policy::default(), 4 << 20, None);
    let mut windows = 0;
    while c.decisions() == 0 {
        c.sample(1000, SECOND, true, 0);
        windows += 1;
        assert!(windows <= 5);
    }
    assert_eq!(windows, 3, "first decision");
    assert_eq!(c.concurrency, 6);
    // A probe step: one settling window, then three that agree.
    let mut windows = 0;
    while c.decisions() == 1 {
        c.sample(1200, SECOND, true, 0);
        windows += 1;
        assert!(windows <= 5);
    }
    assert_eq!(windows, 4, "probe step");
    assert_eq!(c.learned_concurrency(), Some(6));
}

#[test]
fn the_window_straddling_a_change_is_not_counted() {
    let mut c = Controller::new(Policy::default(), 4 << 20, None);
    observe(&mut c, 1000);
    assert_eq!(c.concurrency, 6);
    // In-flight files from the old setting make this window look terrible.
    c.sample(10, SECOND, true, 0);
    observe(&mut c, 1200);
    assert_eq!(
        c.learned_concurrency(),
        Some(6),
        "the probe was judged fairly"
    );
}

/// Steps the controller took and the range it moved in under `spread` noise
/// around a rate that does not depend on the setting at all.
fn wander(spread: f64, seed: u64, windows: usize) -> (usize, usize, usize) {
    let mut c = Controller::new(Policy::default(), 4 << 20, None);
    let mut noise = Noise(seed);
    let (mut changes, mut low, mut high) = (0, c.concurrency, c.concurrency);
    let mut previous = c.concurrency;
    for _ in 0..windows {
        c.sample(noise.next(1_000_000.0, spread), SECOND, true, 0);
        if c.concurrency != previous {
            changes += 1;
            previous = c.concurrency;
        }
        low = low.min(c.concurrency);
        high = high.max(c.concurrency);
    }
    (changes, low, high)
}

#[test]
fn noisy_input_does_not_oscillate() {
    // Ten minutes of one-second windows per run. With no noise at all the
    // controller's own experiments (a step up, a step down, then ever rarer
    // steps up) change the setting this often:
    let (steady, _, _) = wander(0.0, 1, 600);
    assert_eq!(steady, 13);
    for seed in 1..=40 {
        // Heavy noise is almost never trusted: at most one experiment starts
        // on windows that happened to agree, and it is abandoned unread.
        let (changes, low, high) = wander(60.0, seed, 600);
        assert!(changes <= 2, "seed {seed}: {changes} changes");
        assert_eq!(low, 4, "seed {seed}");
        assert!(high <= 6, "seed {seed}: climbed to {high}");

        // Moderate noise adds next to nothing to the steady count, and a rate
        // that never improves is not taken as a reason to keep climbing.
        for spread in [5.0, 10.0, 25.0, 40.0] {
            let (changes, low, high) = wander(spread, seed, 600);
            assert!(
                changes <= steady + 8,
                "seed {seed} spread {spread}: {changes} changes"
            );
            assert!(high <= 9, "seed {seed} spread {spread}: climbed to {high}");
            assert!(low >= 2, "seed {seed} spread {spread}: fell to {low}");
        }
    }
}

#[test]
fn noise_raises_the_gain_an_upward_probe_must_show() {
    // Readings that are steady accept a 9% gain...
    let mut steady = Controller::new(Policy::default(), 4 << 20, None);
    observe(&mut steady, 1000);
    observe(&mut steady, 1090);
    assert_eq!(steady.learned_concurrency(), Some(6));

    // ...readings that wobble by several percent do not.
    let mut noisy = Controller::new(Policy::default(), 4 << 20, None);
    for rate in [950, 1000, 1050] {
        noisy.sample(rate, SECOND, true, 0);
    }
    assert_eq!(noisy.concurrency, 6);
    noisy.sample(1090, SECOND, true, 0); // settling window
    for rate in [1035, 1090, 1145] {
        noisy.sample(rate, SECOND, true, 0);
    }
    assert_eq!(noisy.decisions(), 2);
    assert_eq!(noisy.learned_concurrency(), Some(4));
}

// ----- latency --------------------------------------------------------------

const MIB: usize = 1 << 20;

fn record(histogram: &LatencyHistogram, millis: u64, times: usize) {
    for _ in 0..times {
        histogram.record(Duration::from_millis(millis), MIB);
    }
}

#[test]
fn latency_buckets_are_ordered_and_within_a_fifth_of_the_value() {
    let mut previous = 0;
    for value in [1u64, 3, 1000, 999_999, 1 << 20, 123_456_789, u64::MAX / 3] {
        let bucket = latency_bucket(value);
        assert!(bucket >= previous);
        previous = bucket;
        let floor = latency_bucket_floor(bucket);
        assert!(floor <= value, "{value}: floor {floor}");
        if value >= 4 {
            assert!(
                value as f64 / floor as f64 <= 1.25,
                "{value}: floor {floor}"
            );
        }
    }
    assert!(latency_bucket(u64::MAX) < LATENCY_BUCKETS);
}

#[test]
fn window_median_ignores_one_stalled_write_and_small_writes() {
    let histogram = LatencyHistogram::default();
    let mut reader = LatencyReader::default();
    assert_eq!(reader.window_median(&histogram), None);
    record(&histogram, 10, 3);
    assert_eq!(reader.window_median(&histogram), None, "too few writes");

    record(&histogram, 10, 7);
    histogram.record(Duration::from_secs(30), MIB); // antivirus, hydration
    let median = reader.window_median(&histogram).unwrap();
    assert!((8_000_000..=10_000_000).contains(&median), "{median}");

    // Only what arrived since the last look counts.
    record(&histogram, 40, 8);
    let median = reader.window_median(&histogram).unwrap();
    assert!((32_000_000..=40_000_000).contains(&median), "{median}");

    // Latency is per MiB, so a half-size write at half the time is the same.
    for _ in 0..8 {
        histogram.record(Duration::from_millis(20), MIB / 2);
    }
    let median = reader.window_median(&histogram).unwrap();
    assert!((32_000_000..=40_000_000).contains(&median), "{median}");

    // Tiny writes are not timed at all.
    assert!(!LatencyHistogram::times(4096));
    for _ in 0..100 {
        histogram.record(Duration::from_secs(1), 4096);
    }
    assert_eq!(reader.window_median(&histogram), None);
}

#[test]
fn a_rise_counts_only_when_sustained_against_its_own_baseline() {
    let mut tracker = LatencyTracker::default();
    for _ in 0..6 {
        assert!(!tracker.observe(Some(10_000)));
    }
    // One raised window, then calm: nothing.
    assert!(!tracker.observe(Some(50_000)));
    assert!(!tracker.observe(Some(10_000)));
    // Two raised windows and a window with too few writes: nothing.
    assert!(!tracker.observe(Some(50_000)));
    assert!(!tracker.observe(Some(50_000)));
    assert!(!tracker.observe(None));
    assert!(!tracker.observe(Some(50_000)));
    assert!(!tracker.observe(Some(50_000)));
    // The third raised window in a row.
    assert!(tracker.observe(Some(50_000)));
    // Once accepted as the new normal it is not reported again...
    tracker.rebase();
    for _ in 0..10 {
        assert!(!tracker.observe(Some(50_000)));
    }
    // ...and a rise smaller than double never is.
    for _ in 0..20 {
        assert!(!tracker.observe(Some(90_000)));
    }
}

#[test]
fn a_slow_drift_moves_the_baseline_instead_of_raising_an_alarm() {
    let mut tracker = LatencyTracker::default();
    let mut level = 10_000.0;
    for _ in 0..200 {
        assert!(!tracker.observe(Some(level as u64)));
        level *= 1.02;
    }
}

/// Windows at a steady rate with the given median latency.
fn feed(c: &mut Controller, rate: u64, latency: u64, windows: usize) {
    for _ in 0..windows {
        c.sample_with_latency(rate, SECOND, true, 0, Some(latency));
    }
}

#[test]
fn sustained_latency_rise_ends_an_upward_probe_before_throughput_is_judged() {
    let mut c = Controller::new(Policy::default(), 4 << 20, None);
    // Baseline forms while the first reading is taken (and one more).
    feed(&mut c, 1000, 10_000, 3);
    assert_eq!(c.concurrency, 6);
    assert!(c.is_probing());
    feed(&mut c, 1000, 10_000, 1); // settling window
    feed(&mut c, 1000, 10_000, 1); // fourth baseline window
    let decisions = c.decisions();
    // Throughput is still fine, but writes take three times as long.
    feed(&mut c, 1300, 30_000, 2);
    assert_eq!(c.concurrency, 6, "two raised windows are not sustained");
    feed(&mut c, 1300, 30_000, 1);
    assert_eq!(c.concurrency, 4);
    assert!(!c.is_probing());
    assert_eq!(
        c.explanation,
        "Write latency rose; keeping the previous setting"
    );
    assert_eq!(c.decisions(), decisions, "no throughput reading was needed");
    assert_eq!(c.cooldown(), COOLDOWN_SAMPLES);
    assert_eq!(c.learned_concurrency(), Some(4));
}

#[test]
fn one_slow_window_does_not_end_a_probe() {
    let mut c = Controller::new(Policy::default(), 4 << 20, None);
    feed(&mut c, 1000, 10_000, 5);
    assert_eq!(c.concurrency, 6);
    feed(&mut c, 1200, 80_000, 1);
    feed(&mut c, 1200, 10_000, 3);
    assert_eq!(c.learned_concurrency(), Some(6));
    assert_eq!(c.explanation, "Higher concurrency improved throughput");
}

#[test]
fn sustained_latency_rise_at_a_settled_setting_holds_and_never_reduces_by_itself() {
    let mut c = Controller::new(
        Policy {
            max_concurrency: 4,
            ..Policy::default()
        },
        4 << 20,
        None,
    );
    feed(&mut c, 1000, 10_000, 6);
    assert_eq!(c.concurrency, 4);
    assert_eq!(c.explanation, "At the configured concurrency limit");
    feed(&mut c, 1000, 40_000, 3);
    assert_eq!(c.explanation, "Write latency rose; holding");
    assert_eq!(c.concurrency, 4);
    assert_eq!(c.cooldown(), COOLDOWN_SAMPLES);
    // The hold runs out; only then is the usual, throughput-judged test of
    // fewer files made, and the unchanged rate keeps the lower setting.
    let mut held = 0;
    while c.concurrency == 4 {
        observe(&mut c, 1000);
        held += 1;
        assert!(held <= COOLDOWN_SAMPLES + 1);
    }
    assert_eq!(held, COOLDOWN_SAMPLES + 1);
    assert_eq!(c.concurrency, 2);
    assert_eq!(
        c.explanation,
        "Testing whether fewer files reduce contention"
    );
    observe(&mut c, 400);
    assert_eq!(c.concurrency, 4, "slower with fewer files: back to four");
    // The raised level was accepted as normal: it does not trigger again.
    feed(&mut c, 1000, 40_000, 12);
    assert_ne!(c.explanation, "Write latency rose; holding");
}

// ----- lanes ----------------------------------------------------------------

#[test]
fn files_under_one_mebibyte_use_the_small_lane() {
    assert_eq!(Lane::for_size(0), Lane::Small);
    assert_eq!(Lane::for_size(SMALL_LANE_MAX_BYTES - 1), Lane::Small);
    assert_eq!(Lane::for_size(SMALL_LANE_MAX_BYTES), Lane::Large);
    assert_eq!(Lane::for_size(u64::MAX), Lane::Large);
    assert_eq!(Lane::ALL.map(Lane::index), [0, 1]);
}

#[test]
fn lanes_are_off_for_custom_limits_and_chosen_queue_orders() {
    assert!(lanes_enabled(&Policy::default()));
    for mode in [Mode::Conservative, Mode::Maximum] {
        assert!(lanes_enabled(&Policy {
            mode,
            ..Policy::default()
        }));
    }
    assert!(!lanes_enabled(&Policy {
        mode: Mode::Manual,
        ..Policy::default()
    }));
    for order_by in [OrderBy::SizeAsc, OrderBy::SizeDesc, OrderBy::Name] {
        assert!(!lanes_enabled(&Policy {
            order_by,
            ..Policy::default()
        }));
    }
}

#[test]
fn the_total_limit_is_the_configured_ceiling_or_the_custom_limit() {
    assert_eq!(total_limit(&Policy::default()), 16);
    assert_eq!(
        total_limit(&Policy {
            mode: Mode::Conservative,
            ..Policy::default()
        }),
        4
    );
    assert_eq!(
        total_limit(&Policy {
            mode: Mode::Manual,
            manual_concurrency: 7,
            ..Policy::default()
        }),
        7
    );
    assert_eq!(
        total_limit(&Policy {
            mode: Mode::Manual,
            manual_concurrency: 30,
            max_concurrency: 8,
            ..Policy::default()
        }),
        8
    );
}

#[test]
fn lanes_share_the_limit_without_starving_either() {
    // Room for both: each gets what it wants.
    assert_eq!(lane_caps(16, [4, 12], [true, true]), [4, 12]);
    // Oversubscribed: split in proportion, never more than the limit.
    assert_eq!(lane_caps(16, [6, 16], [true, true]), [4, 12]);
    for limit in 2..=32 {
        for large in 1..=32 {
            for small in 1..=32 {
                let caps = lane_caps(limit, [large, small], [true, true]);
                assert!(caps[0] >= 1 && caps[1] >= 1, "{limit} {large} {small}");
                assert!(caps[0] + caps[1] <= limit, "{limit} {large} {small}");
                assert!(caps[0] <= large && caps[1] <= small.max(limit));
            }
        }
    }
    // A lane with no work holds nothing back from the other.
    assert_eq!(lane_caps(16, [4, 12], [true, false]), [4, 0]);
    assert_eq!(lane_caps(16, [4, 24], [false, true]), [0, 16]);
    assert_eq!(lane_caps(16, [4, 12], [false, false]), [0, 0]);
}

#[test]
fn a_shared_ceiling_clamps_at_once_and_releases_when_the_other_lane_drains() {
    let mut c = Controller::new_for_workload(Policy::default(), 4 << 20, None, 64 * 4096, 64);
    assert_eq!(c.concurrency, 12);
    c.set_ceiling(Some(5));
    assert_eq!(c.concurrency, 5);
    // With no room to step up, the lane checks once whether fewer do as well.
    observe(&mut c, 1000);
    assert_eq!(c.concurrency, 2);
    assert_eq!(
        c.explanation,
        "Testing whether fewer files reduce contention"
    );
    observe(&mut c, 500);
    assert_eq!(c.concurrency, 5, "slower with fewer: back to the ceiling");
    for _ in 0..12 {
        observe(&mut c, 1000);
        assert_eq!(c.concurrency, 5);
    }
    assert_eq!(c.explanation, "At the configured concurrency limit");
    // A custom limit is shared the same way.
    c.update_policy(
        Policy {
            mode: Mode::Manual,
            manual_concurrency: 9,
            ..Policy::default()
        },
        4 << 20,
    );
    assert_eq!(c.concurrency, 5);
    c.update_policy(Policy::default(), 4 << 20);
    c.set_ceiling(None);
    observe(&mut c, 1000);
    assert_eq!(c.concurrency, 7, "room returned");
    assert_eq!(c.explanation, "Testing more simultaneous files");
}

#[test]
fn each_lane_adapts_on_its_own_within_the_shared_limit() {
    let policy = Policy::default();
    let limit = total_limit(&policy);
    let mut lanes = [
        Controller::new_for_route(
            policy.clone(),
            4 << 20,
            None,
            8 << 30,
            4,
            DriveKind::SolidState,
        ),
        Controller::new_for_route(policy, 4 << 20, None, 64 * 4096, 64, DriveKind::SolidState),
    ];
    assert_eq!([lanes[0].concurrency, lanes[1].concurrency], [6, 16]);
    let share = |lanes: &mut [Controller; 2]| {
        let wanted = [lanes[0].concurrency, lanes[1].concurrency];
        if wanted[0] + wanted[1] > limit {
            let caps = lane_caps(limit, wanted, [true, true]);
            lanes[0].set_ceiling(Some(caps[0]));
            lanes[1].set_ceiling(Some(caps[1]));
        } else {
            lanes[0].set_ceiling(Some(limit - wanted[1]));
            lanes[1].set_ceiling(Some(limit - wanted[0]));
        }
    };
    share(&mut lanes);
    assert_eq!([lanes[0].concurrency, lanes[1].concurrency], [4, 12]);
    // The large lane finds fewer files just as fast; the small lane then has
    // room to grow, and the sum never passes the limit.
    for round in 0..40 {
        let large_rate = 1_000_000;
        let small_rate = 2000 * lanes[1].concurrency as u64;
        for lane in 0..2 {
            let other_is_testing = lanes[1 - lane].is_probing();
            lanes[lane].hold_experiments(other_is_testing);
            observe(&mut lanes[lane], [large_rate, small_rate][lane]);
            share(&mut lanes);
            assert!(
                !(lanes[0].is_probing() && lanes[1].is_probing()),
                "round {round}: one experiment at a time"
            );
        }
        assert!(
            lanes[0].concurrency + lanes[1].concurrency <= limit,
            "round {round}"
        );
        assert!(lanes[0].concurrency >= 1 && lanes[1].concurrency >= 1);
    }
    assert!(lanes[0].concurrency < 4, "large: {}", lanes[0].concurrency);
    assert!(lanes[1].concurrency > 12, "small: {}", lanes[1].concurrency);
}

// ----- the lanes of one transfer ---------------------------------------------

/// Two lanes on solid-state storage, driven one second at a time.
struct Job {
    lanes: Lanes,
    now: Instant,
    bytes: [u64; 2],
    histograms: [LatencyHistogram; 2],
    /// The lane still has files but fewer than it may run.
    draining: [bool; 2],
}

impl Job {
    fn new(policy: Policy, two: bool) -> Self {
        let now = Instant::now();
        let mut lanes = Lanes::new(policy, 4 << 20, now);
        lanes.begin(two, now);
        Self {
            lanes,
            now,
            bytes: [0; 2],
            histograms: [LatencyHistogram::default(), LatencyHistogram::default()],
            draining: [false; 2],
        }
    }

    fn start(&mut self, lane: usize, bytes: u64, files: usize) {
        let controller = Controller::new_for_route(
            self.lanes.policy().clone(),
            4 << 20,
            None,
            bytes,
            files,
            DriveKind::SolidState,
        );
        self.lanes
            .start(lane, controller, self.now, self.bytes[lane]);
        self.report([0, 0], [true, true], [0, 0]);
    }

    fn report(&mut self, moved: [u64; 2], demand: [bool; 2], retries: [u64; 2]) {
        let report = |lane: usize| LaneReport {
            bytes: self.bytes[lane] + moved[lane],
            saturated: demand[lane] && !self.draining[lane],
            demand: demand[lane],
            retries: retries[lane],
        };
        let reports = [report(0), report(1)];
        self.bytes = [reports[0].bytes, reports[1].bytes];
        self.lanes.observe(
            self.now,
            reports,
            [&self.histograms[0], &self.histograms[1]],
        );
    }

    /// One second in which each lane with demand moved `rates` bytes.
    fn second(&mut self, rates: [u64; 2], demand: [bool; 2]) {
        self.now += SECOND;
        self.report(rates, demand, [0, 0]);
    }

    fn plan(&self) -> [usize; 2] {
        [self.lanes.concurrency(0), self.lanes.concurrency(1)]
    }
}

const LARGE_RATE: u64 = 200 << 20;

#[test]
fn with_lanes_off_every_file_uses_one_pool() {
    let mut job = Job::new(
        Policy {
            order_by: OrderBy::SizeAsc,
            ..Policy::default()
        },
        false,
    );
    assert!(!job.lanes.two());
    assert_eq!(lane_index(false, 1), 0);
    assert_eq!(lane_index(false, u64::MAX), 0);
    job.start(0, 64 << 20, 4);
    assert_eq!(job.lanes.planned(), 6);
    assert_eq!(
        job.lanes.explanation(),
        "Starting higher for solid-state drives"
    );
    for _ in 0..3 {
        job.second([LARGE_RATE, 0], [true, false]);
    }
    assert_eq!(job.lanes.planned(), 9);
    assert_eq!(job.lanes.explanation(), "Testing more simultaneous files");
    assert!(!job.lanes.started(1));
}

#[test]
fn two_lanes_split_the_limit_adapt_separately_and_test_one_at_a_time() {
    let mut job = Job::new(Policy::default(), true);
    assert!(job.lanes.two());
    assert_eq!(lane_index(true, 4096), Lane::Small.index());
    assert_eq!(lane_index(true, 1 << 30), Lane::Large.index());
    job.start(0, 8 << 30, 4);
    assert_eq!(job.plan()[0], 6, "alone, the large lane starts as usual");
    job.start(1, 64 * 4096, 64);
    assert_eq!(job.plan(), [4, 12], "16 shared in proportion to 6 and 16");
    assert_eq!(job.lanes.planned(), 16);
    assert!(
        job.lanes
            .explanation()
            .starts_with("4 large + 12 small \u{b7} "),
        "{}",
        job.lanes.explanation()
    );

    // Large files copy no faster with more of them; small files do.
    let mut most_small = 0;
    for second in 0..240 {
        let plan = job.plan();
        job.second([LARGE_RATE, (2 << 20) * plan[1] as u64], [true, true]);
        let plan = job.plan();
        assert!(plan[0] >= 1 && plan[1] >= 1, "second {second}: {plan:?}");
        assert!(plan[0] + plan[1] <= 16, "second {second}: {plan:?}");
        assert!(job.lanes.planned() <= 16);
        assert!(
            !(job.lanes.controller(0).is_probing() && job.lanes.controller(1).is_probing()),
            "second {second}: both lanes experimenting"
        );
        most_small = most_small.max(plan[1]);
    }
    let plan = job.plan();
    assert!(
        plan[0] < 4,
        "large lane gave up room it did not need: {plan:?}"
    );
    assert!(most_small > 12, "small lane grew into it: {most_small}");
    assert_eq!(job.lanes.controller(0).learned_concurrency(), Some(plan[0]));
}

#[test]
fn a_retry_or_a_latency_rise_in_one_lane_leaves_the_other_alone() {
    let mut job = Job::new(Policy::default(), true);
    job.start(0, 8 << 30, 4);
    job.start(1, 64 * 4096, 64);
    assert_eq!(job.plan(), [4, 12]);
    job.report([0, 0], [true, true], [0, 3]);
    assert_eq!(job.plan(), [4, 6]);
    assert!(job
        .lanes
        .explanation()
        .ends_with("Reduced concurrency after an I/O retry"));

    // Writes in the large lane slow threefold for good; small files are fine.
    let mut held = false;
    for second in 0..40 {
        let slow = second >= 12;
        for _ in 0..20 {
            job.histograms[0].record(Duration::from_millis(if slow { 30 } else { 10 }), 1 << 20);
        }
        let before = job.plan();
        job.second([LARGE_RATE, 12 << 20], [true, true]);
        if job
            .lanes
            .controller(0)
            .explanation
            .starts_with("Write latency rose")
        {
            held = true;
            assert_eq!(job.plan()[0], before[0].min(job.plan()[0]));
        }
        assert!(!job
            .lanes
            .controller(1)
            .explanation
            .starts_with("Write latency"));
    }
    assert!(held, "the large lane noticed its own latency");
}

#[test]
fn a_lane_alone_has_the_whole_limit_and_gives_it_back() {
    let mut job = Job::new(Policy::default(), true);
    job.start(1, 64 * 4096, 64);
    assert_eq!(job.plan()[1], 16);
    assert_eq!(job.lanes.planned(), 16);
    assert_eq!(
        job.lanes.explanation(),
        "Scaling concurrency for small-file batch"
    );
    // A large file turns up: the small lane makes room at once.
    job.start(0, 8 << 30, 1);
    assert_eq!(job.plan(), [4, 12]);
    // The large lane drains; nothing holds the small lane back any more.
    for _ in 0..12 {
        let plan = job.plan();
        job.second([0, (2 << 20) * plan[1] as u64], [false, true]);
    }
    assert!(job.plan()[1] > 12, "{:?}", job.plan());
    assert_eq!(job.lanes.planned(), job.plan()[1]);
    assert!(!job.lanes.explanation().contains("large"));
}

#[test]
fn a_custom_limit_set_mid_transfer_is_one_limit_for_both_lanes() {
    let mut job = Job::new(Policy::default(), true);
    job.start(0, 8 << 30, 4);
    job.start(1, 64 * 4096, 64);
    let custom = Policy {
        mode: Mode::Manual,
        manual_concurrency: 7,
        ..Policy::default()
    };
    job.lanes.update_policy(custom, [4 << 20, 4 << 20]);
    assert_eq!(job.lanes.total_limit(), 7);
    for _ in 0..6 {
        job.second([LARGE_RATE, 12 << 20], [true, true]);
        let plan = job.plan();
        assert_eq!(plan[0] + plan[1], 7, "{plan:?}");
        assert!(plan[0] >= 1 && plan[1] >= 1);
    }
    assert_eq!(job.lanes.planned(), 7);
    // Alone again, the remaining lane uses the whole custom limit.
    for _ in 0..3 {
        job.second([0, 12 << 20], [false, true]);
    }
    assert_eq!(job.plan()[1], 7);
    assert_eq!(job.lanes.planned(), 7);
}

#[test]
fn a_limit_of_one_is_planned_as_one() {
    let mut job = Job::new(
        Policy {
            max_concurrency: 1,
            ..Policy::default()
        },
        true,
    );
    job.start(0, 8 << 30, 4);
    job.start(1, 64 * 4096, 64);
    assert_eq!(
        job.plan(),
        [1, 1],
        "the pool gives the one slot to the older file"
    );
    assert_eq!(job.lanes.total_limit(), 1);
    assert_eq!(job.lanes.planned(), 1);
}

#[test]
fn an_unfinished_test_in_a_draining_lane_does_not_hold_the_other_lane() {
    let mut job = Job::new(
        Policy {
            max_concurrency: 32,
            ..Policy::default()
        },
        true,
    );
    job.start(0, 8 << 30, 4);
    job.start(1, 64 * 4096, 64);
    assert_eq!(job.plan(), [6, 16]);
    // The large lane starts a test of nine files...
    let mut seconds = 0;
    while !job.lanes.controller(0).is_probing() {
        job.second([LARGE_RATE, 32 << 20], [true, true]);
        seconds += 1;
        assert!(seconds < 10);
    }
    assert!(!job.lanes.controller(1).is_probing());
    // ...and then has only its last few files left, so the test never ends.
    job.draining[0] = true;
    for _ in 0..12 {
        let plan = job.plan();
        job.second([LARGE_RATE / 4, (2 << 20) * plan[1] as u64], [true, true]);
    }
    assert!(job.lanes.controller(0).is_probing());
    assert_eq!(job.plan(), [9, 23], "the small lane went on adapting");
    assert_eq!(
        job.lanes.controller(1).explanation,
        "At the configured concurrency limit"
    );
}
