//! What a transfer costs: the file-system operations of one file, pinned by a
//! test, and a benchmark that times whole transfers.
//!
//! The benchmark is not run with the other tests. On the drive to measure:
//!
//! ```text
//! set SUMAFILE_BENCH_ROOT=D:\bench
//! cargo test -p simplefile-service --release -- --ignored --nocapture transfer_benchmark
//! ```
//!
//! See "Measuring a transfer" in `docs/TRANSFER_TUNING.md` for the cases, the
//! settings and how to compare a run with another version or with robocopy.

use super::tuning::{self, Mode, Policy};
use super::{transfer_with_policy_blocking, Request};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicBool;
use std::sync::Arc;
use std::time::{Duration, Instant};
use sumafile_transfer::ops::{Counter, Counts, Op};

fn unique_root(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!(
        "sumafile_bench_{name}_{}",
        super::generate_transfer_operation_id()
    ))
}

/// Copies `sources` into `destination` and returns what it took.
fn transfer(
    operation: &'static str,
    sources: Vec<PathBuf>,
    destination: &Path,
    conflict: &str,
    policy: Policy,
) -> Result<Vec<super::TransferResult>, super::TransferBatchError> {
    transfer_with_policy_blocking(
        operation,
        Request::Selection {
            sources: sources
                .iter()
                .map(|source| source.to_string_lossy().into_owned())
                .collect(),
            conflict_action: conflict.to_string(),
        },
        destination.to_string_lossy().into_owned(),
        super::generate_transfer_operation_id(),
        Arc::new(AtomicBool::new(false)),
        &|_| {},
        &|_| {},
        &move || policy.clone(),
    )
}

/// The operations one small file costs when it is copied into a folder the
/// transfer creates, with the default settings: none of them asks a path for
/// what the transfer already knows.
///
/// | Operation | Count | What it is |
/// | --- | --- | --- |
/// | Open | 2 | the source, and the partial file created |
/// | Query | 4 | the partial file's ID for its record; the source's size and times when it is opened and again after the last read; the copy's attributes before they are set |
/// | Read | 1 | the source, in one read |
/// | Write | 2 | the line in the folder's record, and the data |
/// | Flush | 1 | the data, before the file gets its name |
/// | Set | 1 | the copy's times and attributes |
/// | Rename | 1 | the partial file to its final name, through its own handle |
///
/// Elsewhere than on Windows the rename goes by path, and the partial file is
/// first checked to be the one recorded: one Stat more.
///
/// The cost of the folder (creating it, its record, listing its source,
/// setting its times) is the same however many files it holds, so it is left
/// out by comparing a folder of one file with a folder of eleven.
#[test]
fn a_small_file_copied_into_a_new_folder_costs_these_operations() {
    let expected = |op: Op| -> u64 {
        match op {
            Op::Stat => u64::from(!cfg!(windows)),
            Op::Open => 2,
            Op::Query => 4,
            Op::Read => 1,
            Op::Write => 2,
            Op::Flush => 1,
            Op::Set => 1,
            Op::Rename => 1,
            Op::Delete | Op::Lock | Op::CreateDir | Op::List => 0,
        }
    };
    let run = |files: usize| -> Counts {
        let root = unique_root(&format!("per_file_{files}"));
        let source = root.join("source").join("tree");
        let destination = root.join("destination");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&destination).unwrap();
        for index in 0..files {
            fs::write(source.join(format!("{index}.txt")), vec![b'x'; 20 * 1024]).unwrap();
        }
        let counter = Counter::under(&root);
        transfer(
            "copy",
            vec![source],
            &destination,
            "error",
            Policy {
                mode: Mode::Manual,
                manual_concurrency: 4,
                ..Policy::default()
            },
        )
        .expect("the copy succeeds");
        let counts = counter.counts();
        drop(counter);
        let _ = fs::remove_dir_all(&root);
        counts
    };

    let one = run(1);
    let eleven = run(11);
    let ten_files = eleven.since(&one);

    let mismatched: Vec<String> = Op::ALL
        .iter()
        .filter(|op| ten_files.get(**op) != 10 * expected(**op))
        .map(|op| {
            format!(
                "{op:?}: {} for ten files, expected {}",
                ten_files.get(*op),
                10 * expected(*op)
            )
        })
        .collect();
    assert!(
        mismatched.is_empty(),
        "{}\none file: {}\neleven files: {}",
        mismatched.join("\n"),
        one.describe(),
        eleven.describe()
    );
}

/// One benchmark case: what is transferred and how.
struct Case {
    name: &'static str,
    /// Builds the source under `data`; returns the selected items.
    build: fn(&Path) -> Vec<PathBuf>,
    operation: &'static str,
    conflict: &'static str,
    /// Prepares a fresh destination from the selected items before a run
    /// (not timed).
    prepare: fn(&[PathBuf], &Path),
    /// Whether every run needs a new source (a move takes it away).
    rebuild: bool,
}

const KIB: usize = 1024;
const MIB: usize = 1024 * KIB;

fn env_usize(name: &str, default: usize) -> usize {
    std::env::var(name)
        .ok()
        .and_then(|value| value.trim().parse().ok())
        .unwrap_or(default)
}

fn small_files() -> usize {
    env_usize("SUMAFILE_BENCH_SMALL_FILES", 3_000)
}

/// Contents that do not compress or deduplicate into nothing.
fn contents(seed: usize, length: usize) -> Vec<u8> {
    let mut state = (seed as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1;
    (0..length)
        .map(|_| {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state as u8
        })
        .collect()
}

fn write_files(folder: &Path, count: usize, length: usize, seed: usize) {
    fs::create_dir_all(folder).unwrap();
    for index in 0..count {
        fs::write(
            folder.join(format!("file-{index:05}.bin")),
            contents(seed + index, length),
        )
        .unwrap();
    }
}

fn tree_in_folders(data: &Path, folders: usize, per_folder: usize, length: usize) -> Vec<PathBuf> {
    let tree = data.join("tree");
    for folder in 0..folders {
        write_files(
            &tree.join(format!("folder-{folder:04}")),
            per_folder,
            length,
            folder * per_folder,
        );
    }
    vec![tree]
}

fn nothing(_: &[PathBuf], destination: &Path) {
    fs::create_dir_all(destination).unwrap();
}

/// The destination already holds a copy of every file: a second run of the
/// same copy, with Skip.
fn holds_a_copy(sources: &[PathBuf], destination: &Path) {
    fs::create_dir_all(destination).unwrap();
    transfer(
        "copy",
        sources.to_vec(),
        destination,
        "error",
        Policy::default(),
    )
    .expect("the first copy succeeds");
}

/// The destination holds the selected folder and all its subfolders, empty:
/// a move into it merges, file by file.
fn holds_the_folders(sources: &[PathBuf], destination: &Path) {
    for source in sources {
        let target = destination.join(source.file_name().unwrap());
        for entry in fs::read_dir(source).unwrap() {
            let entry = entry.unwrap();
            if entry.file_type().unwrap().is_dir() {
                fs::create_dir_all(target.join(entry.file_name())).unwrap();
            }
        }
    }
}

fn cases() -> Vec<Case> {
    vec![
        Case {
            name: "small-one-folder",
            build: |data| {
                let folder = data.join("tree");
                write_files(&folder, small_files(), 4 * KIB, 0);
                vec![folder]
            },
            operation: "copy",
            conflict: "error",
            prepare: nothing,
            rebuild: false,
        },
        Case {
            name: "small-300-folders",
            build: |data| tree_in_folders(data, 300, small_files() / 300, 4 * KIB),
            operation: "copy",
            conflict: "error",
            prepare: nothing,
            rebuild: false,
        },
        Case {
            name: "one-file-folders",
            build: |data| tree_in_folders(data, small_files(), 1, 4 * KIB),
            operation: "copy",
            conflict: "error",
            prepare: nothing,
            rebuild: false,
        },
        Case {
            name: "medium-files",
            build: |data| {
                let folder = data.join("tree");
                write_files(&folder, 300, 3 * MIB, 0);
                vec![folder]
            },
            operation: "copy",
            conflict: "error",
            prepare: nothing,
            rebuild: false,
        },
        Case {
            name: "one-large-file",
            build: |data| {
                let file = data.join("large.bin");
                fs::create_dir_all(data).unwrap();
                let mut written = fs::File::create(&file).unwrap();
                let block = contents(7, 4 * MIB);
                for _ in 0..env_usize("SUMAFILE_BENCH_LARGE_MIB", 1024).div_ceil(4) {
                    std::io::Write::write_all(&mut written, &block).unwrap();
                }
                vec![file]
            },
            operation: "copy",
            conflict: "error",
            prepare: nothing,
            rebuild: false,
        },
        Case {
            name: "skip-all-300-folders",
            build: |data| tree_in_folders(data, 300, small_files() / 300, 4 * KIB),
            operation: "copy",
            conflict: "skip",
            prepare: holds_a_copy,
            rebuild: false,
        },
        Case {
            name: "merge-move-300-folders",
            build: |data| tree_in_folders(data, 300, small_files() / 300, 4 * KIB),
            operation: "move",
            conflict: "error",
            prepare: holds_the_folders,
            rebuild: true,
        },
    ]
}

/// Files and bytes under `paths`.
fn size_of(paths: &[PathBuf]) -> (u64, u64) {
    let mut files = 0;
    let mut bytes = 0;
    let mut pending: Vec<PathBuf> = paths.to_vec();
    while let Some(path) = pending.pop() {
        let meta = fs::symlink_metadata(&path).unwrap();
        if meta.is_dir() {
            pending.extend(fs::read_dir(&path).unwrap().map(|e| e.unwrap().path()));
        } else {
            files += 1;
            bytes += meta.len();
        }
    }
    (files, bytes)
}

fn median(mut times: Vec<Duration>) -> Duration {
    times.sort();
    times[times.len() / 2]
}

/// Times whole transfers. Not run with the other tests; see the top of this
/// file and `docs/TRANSFER_TUNING.md`.
///
/// - `SUMAFILE_BENCH_ROOT`: where the source is built and copied to, on the
///   drive to measure. The system temporary folder when not set.
/// - `SUMAFILE_BENCH_DST_ROOT`: where to copy to instead, to measure between
///   two drives or to a share.
/// - `SUMAFILE_BENCH_CASES`: the cases to run, separated by commas. All when
///   not set.
/// - `SUMAFILE_BENCH_RUNS`: runs per case and setting; the median is shown.
///   3 when not set.
/// - `SUMAFILE_BENCH_SMALL_FILES` (3000) and `SUMAFILE_BENCH_LARGE_MIB` (1024):
///   the sizes of the cases.
/// - `SUMAFILE_BENCH_MODE`: `automatic` (the default setting) or a number, a
///   custom limit of that many files at once.
#[test]
#[ignore = "a benchmark: run it on purpose, with --ignored"]
fn transfer_benchmark() {
    let root = std::env::var_os("SUMAFILE_BENCH_ROOT")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir)
        .join(format!(
            "sumafile-bench-{}",
            super::generate_transfer_operation_id()
        ));
    let destination_root = std::env::var_os("SUMAFILE_BENCH_DST_ROOT")
        .map(|path| {
            PathBuf::from(path).join(format!(
                "sumafile-bench-{}",
                super::generate_transfer_operation_id()
            ))
        })
        .unwrap_or_else(|| root.join("destination"));
    fs::create_dir_all(&root).unwrap();
    fs::create_dir_all(&destination_root).unwrap();
    // Learned settings go to a database of the benchmark's own, never the app's.
    if std::env::var_os("SIMPLEFILE_APP_DATA_DIR").is_none() {
        std::env::set_var("SIMPLEFILE_APP_DATA_DIR", root.join("app-data"));
    }
    let runs = env_usize("SUMAFILE_BENCH_RUNS", 3).max(1);
    let chosen: Option<Vec<String>> = std::env::var("SUMAFILE_BENCH_CASES").ok().map(|list| {
        list.split(',')
            .map(|name| name.trim().to_string())
            .collect()
    });
    let mode = std::env::var("SUMAFILE_BENCH_MODE").unwrap_or_default();
    let policy = |flush: bool| {
        let mut policy = Policy {
            flush_on_close: flush,
            ..Policy::default()
        };
        if let Ok(limit) = mode.trim().parse::<usize>() {
            policy.mode = Mode::Manual;
            policy.manual_concurrency = limit;
        }
        policy
    };

    println!(
        "SumaFile transfer benchmark: {} to {}, {runs} runs per case, mode {}",
        root.display(),
        destination_root.display(),
        if mode.is_empty() { "automatic" } else { &mode }
    );
    for case in cases() {
        if chosen
            .as_ref()
            .is_some_and(|names| !names.iter().any(|name| name == case.name))
        {
            continue;
        }
        let data = root.join("data").join(case.name);
        let mut sources = (case.build)(&data);
        let (files, bytes) = size_of(&sources);
        for flush in [true, false] {
            let mut times = Vec::new();
            let mut counts = Counts::default();
            for run in 0..runs {
                if case.rebuild && run > 0 {
                    let _ = fs::remove_dir_all(&data);
                    sources = (case.build)(&data);
                }
                let destination = destination_root.join(format!("{}-{run}", case.name));
                (case.prepare)(&sources, &destination);
                tuning::forget_learning().unwrap();
                let counters = [Counter::under(&root), Counter::under(&destination_root)];
                let started = Instant::now();
                transfer(
                    case.operation,
                    sources.clone(),
                    &destination,
                    case.conflict,
                    policy(flush),
                )
                .unwrap_or_else(|error| panic!("{}: {}", case.name, error.message));
                times.push(started.elapsed());
                counts = if destination_root.starts_with(&root) {
                    counters[0].counts()
                } else {
                    let [first, second] = counters.map(|counter| counter.counts());
                    first.add(&second)
                };
                let _ = fs::remove_dir_all(&destination);
            }
            let time = median(times.clone());
            let seconds = time.as_secs_f64().max(1e-9);
            let per_file = |op: Op| counts.get(op) as f64 / files.max(1) as f64;
            println!(
                "{:<24} flush {:<3} {:>6} files {:>9.1} MiB  median {:>8.3} s (of {})  {:>8.0} files/s {:>8.1} MiB/s  per file: {} = {:.1} Windows calls",
                case.name,
                if flush { "on" } else { "off" },
                files,
                bytes as f64 / MIB as f64,
                seconds,
                times
                    .iter()
                    .map(|time| format!("{:.3}", time.as_secs_f64()))
                    .collect::<Vec<_>>()
                    .join(", "),
                files as f64 / seconds,
                bytes as f64 / MIB as f64 / seconds,
                Op::ALL
                    .iter()
                    .filter(|op| counts.get(**op) > 0)
                    .map(|op| format!("{op:?} {:.2}", per_file(*op)))
                    .collect::<Vec<_>>()
                    .join(", "),
                counts.windows_calls() as f64 / files.max(1) as f64,
            );
        }
        let _ = fs::remove_dir_all(&data);
    }
    let _ = fs::remove_dir_all(&destination_root);
    let _ = fs::remove_dir_all(&root);
}
