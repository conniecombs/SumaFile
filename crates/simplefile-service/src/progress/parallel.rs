//! File workers never publish UI events directly: one coordinator owns ordering,
//! aggregate progress, adaptive decisions, and the active-file snapshot.

use super::adaptive::Session;
use super::execute::copy_file_with_progress;
use super::reporting::{CopyContext, ProgressContext, LOCAL_BUFFER_SIZE, NETWORK_BUFFER_SIZE};
use super::resources::Permit;
use serde::Serialize;
use simplefile_core::models::ProgressUpdate;
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::time::{Duration, Instant};

#[derive(Clone)]
pub(crate) struct FileTask {
    pub source: PathBuf,
    pub destination: PathBuf,
    pub size: u64,
    pub network: bool,
    pub replace_existing: bool,
    pub resume_existing: bool,
    pub symlink: bool,
}

/// Enumerate incrementally: at most 128 pending files and one read-directory
/// handle per depth. All workers are joined before cleanup or directory metadata.
pub(crate) fn copy_tree(
    source: &std::path::Path,
    destination: &std::path::Path,
    ctx: &CopyContext<'_>,
    completed_bytes: &mut u64,
    replace_existing: bool,
    session: &mut Session<'_>,
) -> Result<(), String> {
    use super::execute::{is_real_directory, prepare_destination_for_copy};
    use simplefile_core::path_conflict::create_dir_exclusive;
    enum Step {
        Entry(PathBuf, PathBuf),
        Directory(Box<fs::ReadDir>, PathBuf),
    }
    let mut stack = vec![Step::Entry(source.into(), destination.into())];
    let mut directories = Vec::new();
    let mut batch = Vec::with_capacity(128);
    let flush =
        |batch: &mut Vec<FileTask>, completed_bytes: &mut u64, session: &mut Session<'_>| {
            if batch.is_empty() {
                return Ok(());
            }
            let result = copy_files(batch, ctx.progress, session, *completed_bytes);
            *completed_bytes = completed_bytes.saturating_add(result.bytes);
            batch.clear();
            result.failed.map_or(Ok(()), |(_, error)| Err(error))
        };
    let result = (|| {
        while let Some(step) = stack.pop() {
            ctx.progress.check_cancelled()?;
            match step {
                Step::Directory(mut entries, target) => {
                    if let Some(entry) = entries.next() {
                        let entry = entry.map_err(|e| format!("Failed to read entry: {e}"))?;
                        let child = target.join(entry.file_name());
                        stack.push(Step::Directory(entries, target));
                        stack.push(Step::Entry(entry.path(), child));
                    }
                }
                Step::Entry(src, dst) => {
                    let metadata = fs::symlink_metadata(&src)
                        .map_err(|e| format!("Failed to stat source: {e}"))?;
                    if metadata.file_type().is_dir() {
                        let existing = (ctx.resume_existing && is_real_directory(&dst))
                            || prepare_destination_for_copy(true, &dst, replace_existing)?;
                        if !existing {
                            create_dir_exclusive(&dst)?;
                            directories.push((src.clone(), dst.clone()));
                        }
                        if stack.len() >= 1024 {
                            return Err("Directory nesting exceeds the safe transfer limit".into());
                        }
                        let entries = fs::read_dir(&src)
                            .map_err(|e| format!("Failed to read directory: {e}"))?;
                        stack.push(Step::Directory(Box::new(entries), dst));
                    } else {
                        let symlink = metadata.file_type().is_symlink();
                        let size = if symlink { 0 } else { metadata.len() };
                        ctx.progress.total_bytes.fetch_add(size, Ordering::Relaxed);
                        ctx.progress.total_files.fetch_add(1, Ordering::Relaxed);
                        batch.push(FileTask {
                            source: src,
                            destination: dst,
                            size,
                            network: ctx.network,
                            replace_existing,
                            resume_existing: ctx.resume_existing,
                            symlink,
                        });
                        if batch.len() == 128 {
                            flush(&mut batch, completed_bytes, session)?;
                        }
                    }
                }
            }
        }
        flush(&mut batch, completed_bytes, session)?;
        for (src, dst) in directories.iter().rev() {
            simplefile_core::file_ops::preserve_basic_metadata(src, dst)?;
        }
        Ok(())
    })();
    if let Err(original) = result {
        // Match the established cleanup contract; never remove merged directories.
        let mut failure = original;
        for (_, dst) in directories.iter().rev() {
            if let Err(error) = crate::transfer_staging::cleanup_owned_path(dst) {
                log::warn!("{error}");
                failure = format!("{failure}; {error}");
            }
        }
        return Err(failure);
    }
    result
}

#[derive(Clone, Serialize)]
pub(crate) struct ActiveFile {
    pub path: String,
    pub current: u64,
    pub total: u64,
}

#[derive(Serialize)]
pub(crate) struct TuningUpdate {
    pub operation_id: String,
    pub mode: super::tuning::Mode,
    pub concurrency: usize,
    pub explanation: String,
    pub active_files: Vec<ActiveFile>,
}

enum Message {
    Progress(usize, ProgressUpdate),
    Finished(usize, u64, Result<(), String>),
}

pub(crate) struct CopyResult {
    pub bytes: u64,
    pub completed: Vec<usize>,
    pub failed: Option<(usize, String)>,
}

/// Workers share cancellation but not counters. On error no more work is
/// scheduled; all active workers are joined before the caller may remove any
/// staging directory. Policy refreshes are injected for isolated tests.
pub(crate) fn copy_files(
    tasks: &[FileTask],
    ctx: &ProgressContext<'_>,
    session: &mut Session<'_>,
    offset: u64,
) -> CopyResult {
    copy_files_using(tasks, ctx, session, offset, &copy_task)
}

fn copy_task(task: &FileTask, context: &CopyContext<'_>, copied: &mut u64) -> Result<(), String> {
    if task.symlink {
        super::execute::copy_item_with_progress(
            &task.source,
            &task.destination,
            context,
            copied,
            task.replace_existing,
        )
    } else {
        copy_file_with_progress(
            &task.source,
            &task.destination,
            context,
            copied,
            task.replace_existing,
        )
    }
}

type CopyFunction = dyn Fn(&FileTask, &CopyContext<'_>, &mut u64) -> Result<(), String> + Sync;

fn copy_files_using(
    tasks: &[FileTask],
    ctx: &ProgressContext<'_>,
    session: &mut Session<'_>,
    offset: u64,
    copy: &CopyFunction,
) -> CopyResult {
    let buffer_size = if tasks.iter().all(|t| t.network) {
        NETWORK_BUFFER_SIZE
    } else {
        LOCAL_BUFFER_SIZE
    };
    session.workload(
        tasks.iter().map(|t| t.size).sum(),
        tasks.len(),
        buffer_size,
        offset,
    );
    let worker_count = tasks.len().min(32);
    let worker_cancel = Arc::new(AtomicBool::new(false));
    let retry_count = AtomicU64::new(0);
    let (work_tx, work_rx) = mpsc::sync_channel::<usize>(32);
    let work_rx = Mutex::new(work_rx);
    let (event_tx, event_rx) = mpsc::sync_channel::<Message>(128);
    let mut result = CopyResult {
        bytes: 0,
        completed: Vec::new(),
        failed: None,
    };
    let mut active = BTreeMap::<usize, ActiveFile>::new();
    let mut next = 0;
    let mut first_snapshot = true;
    let mut last_display = Instant::now();
    let mut last_retries = 0;
    let operation_id = ctx.operation_id;
    let operation_type = ctx.operation_type;
    if let Some(first) = tasks.first() {
        ctx.emit_now(
            offset,
            ctx.total_bytes.load(Ordering::Relaxed),
            first.source.to_string_lossy().into(),
            "running",
            None,
        );
    }

    std::thread::scope(|scope| {
        for _ in 0..worker_count {
            let work_rx = &work_rx;
            let tx = event_tx.clone();
            let cancel = &worker_cancel;
            let retries = &retry_count;
            let limiter = session.limiter.clone();
            scope.spawn(move || {
                loop {
                    // Drop the receiver guard before doing I/O; keeping it in a
                    // while-let scrutinee would serialize every worker.
                    let next = { work_rx.lock().expect("work queue lock poisoned").recv() };
                    let Ok(index) = next else {
                        break;
                    };
                    let task = &tasks[index];
                    let total_bytes = Arc::new(AtomicU64::new(task.size));
                    let total_files = Arc::new(AtomicU64::new(1));
                    let completed_files = Arc::new(AtomicU64::new(0));
                    let emit = |update| {
                        let _ = tx.send(Message::Progress(index, update));
                    };
                    let progress = ProgressContext {
                        emit: &emit,
                        operation_id,
                        operation_type,
                        cancel,
                        total_bytes: &total_bytes,
                        total_files: &total_files,
                        completed_files: &completed_files,
                        last_running_emit: Mutex::new(Instant::now()),
                        retry_count: Some(retries),
                        rate_limit: Some(&limiter),
                    };
                    let context = CopyContext {
                        progress: &progress,
                        operation_id,
                        network: task.network,
                        resume_existing: task.resume_existing,
                    };
                    let mut copied = 0;
                    let status = Permit::acquire(context.buffer_size() * 3, cancel)
                        .and_then(|_permit| copy(task, &context, &mut copied));
                    if tx.send(Message::Finished(index, copied, status)).is_err() {
                        break;
                    }
                }
            });
        }
        drop(event_tx);
        while next < tasks.len() || !active.is_empty() {
            if ctx.check_cancelled().is_err() {
                worker_cancel.store(true, Ordering::Relaxed);
            }
            while !worker_cancel.load(Ordering::Relaxed)
                && next < tasks.len()
                && active.len() < session.controller.concurrency
            {
                let task = &tasks[next];
                active.insert(
                    next,
                    ActiveFile {
                        path: task.source.to_string_lossy().into(),
                        current: 0,
                        total: task.size,
                    },
                );
                if work_tx.send(next).is_err() {
                    break;
                }
                next += 1;
            }
            if active.is_empty() && (next == tasks.len() || worker_cancel.load(Ordering::Relaxed)) {
                break;
            }
            if first_snapshot {
                session.display(active.values().cloned().collect(), true);
                first_snapshot = false;
            }
            // A drained queue is not evidence that a lower setting is faster.
            let saturated = active.len() >= session.controller.concurrency;
            match event_rx.recv_timeout(Duration::from_millis(100)) {
                Ok(Message::Progress(index, update)) => {
                    if let Some(file) = active.get_mut(&index) {
                        file.current = file.current.max(update.current);
                    }
                    if update.status == "finalizing"
                        && (last_display.elapsed() >= Duration::from_millis(200)
                            || (active.len() == 1 && next == tasks.len()))
                    {
                        let current = offset
                            .saturating_add(result.bytes)
                            .saturating_add(active.values().map(|f| f.current).sum::<u64>());
                        ctx.finalizing(current, update.current_item);
                    }
                }
                Ok(Message::Finished(index, bytes, status)) => {
                    active.remove(&index);
                    match status {
                        Ok(()) => {
                            result.bytes = result.bytes.saturating_add(bytes);
                            result.completed.push(index);
                            ctx.completed_files.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(error) => {
                            if result.failed.is_none() {
                                result.failed = Some((index, error));
                            }
                            worker_cancel.store(true, Ordering::Relaxed);
                        }
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    result.failed = Some((
                        next.saturating_sub(1),
                        "Copy workers stopped unexpectedly".into(),
                    ));
                    break;
                }
            }
            let current = offset
                .saturating_add(result.bytes)
                .saturating_add(active.values().map(|f| f.current).sum::<u64>());
            let retries = retry_count.load(Ordering::Relaxed);
            session.observe(current, saturated, retries - last_retries);
            last_retries = retries;
            if last_display.elapsed() >= Duration::from_millis(200) || active.is_empty() {
                ctx.emit(
                    current,
                    active
                        .values()
                        .next()
                        .map(|f| f.path.clone())
                        .unwrap_or_default(),
                    "running",
                    None,
                );
                session.display(active.values().cloned().collect(), active.is_empty());
                last_display = Instant::now();
            }
        }
        drop(work_tx);
    });
    if ctx.check_cancelled().is_err() && result.failed.is_none() {
        result.failed = Some((next.saturating_sub(1), "Operation cancelled".into()));
    }
    session.observe(offset.saturating_add(result.bytes), false, 0);
    session.display(Vec::new(), true);
    result
}

#[cfg(test)]
mod tests {
    use super::super::tuning::{Mode, Policy};
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn exercise(cancel_during_copy: bool) {
        let root = std::env::temp_dir().join(format!(
            "sumafile_parallel_{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(root.join("src")).unwrap();
        fs::create_dir_all(root.join("dst")).unwrap();
        let tasks: Vec<_> = (0..6)
            .map(|index| {
                let source = root.join("src").join(format!("{index}.bin"));
                fs::write(&source, vec![index as u8; 65536]).unwrap();
                FileTask {
                    source,
                    destination: root.join("dst").join(format!("{index}.bin")),
                    size: 65536,
                    network: false,
                    replace_existing: false,
                    resume_existing: false,
                    symlink: false,
                }
            })
            .collect();
        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(6 * 65536));
        let files = Arc::new(AtomicU64::new(6));
        let completed = Arc::new(AtomicU64::new(0));
        let ctx = ProgressContext {
            emit: &|_| {},
            operation_id: "parallel-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
        };
        let snapshots = Mutex::new(Vec::new());
        let emit = |update| snapshots.lock().unwrap().push(update);
        let policy = || Policy {
            mode: Mode::Manual,
            manual_concurrency: 3,
            ..Policy::default()
        };
        let mut session = Session::new(
            "parallel-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &emit,
        );
        let started = Arc::new(AtomicU64::new(0));
        let observed_started = started.clone();
        let requested_cancel = cancel.clone();
        let copy = move |task: &FileTask, context: &CopyContext<'_>, bytes: &mut u64| {
            observed_started.fetch_add(1, Ordering::SeqCst);
            let deadline = Instant::now() + Duration::from_secs(3);
            // A bounded rendezvous catches accidental serialization without
            // making a regressed test hang forever on a Barrier.
            while observed_started.load(Ordering::SeqCst) < 3 {
                if Instant::now() >= deadline {
                    return Err("Workers did not overlap".into());
                }
                std::thread::sleep(Duration::from_millis(1));
            }
            if cancel_during_copy {
                requested_cancel.store(true, Ordering::Relaxed);
                while context.progress.check_cancelled().is_ok() {
                    if Instant::now() >= deadline {
                        return Err("Worker did not receive cancellation".into());
                    }
                    std::thread::sleep(Duration::from_millis(1));
                }
                return context.progress.check_cancelled();
            }
            copy_task(task, context, bytes)
        };
        let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);
        if cancel_during_copy {
            assert_eq!(result.failed.unwrap().1, "Operation cancelled");
            assert_eq!(started.load(Ordering::SeqCst), 3);
            assert!(result.completed.is_empty());
            assert_eq!(fs::read_dir(root.join("dst")).unwrap().count(), 0);
        } else {
            assert!(result.failed.is_none(), "{:?}", result.failed);
            assert_eq!(result.bytes, 6 * 65536);
            assert_eq!(completed.load(Ordering::Relaxed), 6);
            for task in &tasks {
                assert_eq!(
                    fs::read(&task.source).unwrap(),
                    fs::read(&task.destination).unwrap()
                );
            }
        }
        let snapshots = snapshots.lock().unwrap();
        assert!(snapshots.iter().any(|s| s.active_files.len() == 3));
        assert!(snapshots.last().unwrap().active_files.is_empty());
        fs::remove_dir_all(&root).unwrap();
    }

    fn delayed_filesystem_transfer(operation: &'static str, network: bool) {
        use crate::transfer_test_io::DelayedIo;
        for mode in [
            Mode::Automatic,
            Mode::Conservative,
            Mode::Maximum,
            Mode::Manual,
        ] {
            let root = std::env::temp_dir().join(format!(
                "sumafile_latency_{}",
                crate::progress::generate_transfer_operation_id()
            ));
            fs::create_dir_all(root.join("src")).unwrap();
            fs::create_dir_all(root.join("dst")).unwrap();
            let bytes: Vec<u8> = (0..256 * 1024).map(|n| (n % 251) as u8).collect();
            let tasks: Vec<_> = (0..8)
                .map(|index| {
                    let source = root.join("src").join(format!("{index}.bin"));
                    fs::write(&source, &bytes).unwrap();
                    FileTask {
                        source,
                        destination: root.join("dst").join(format!("{index}.bin")),
                        size: bytes.len() as u64,
                        network,
                        replace_existing: false,
                        resume_existing: false,
                        symlink: false,
                    }
                })
                .collect();
            let cancel = Arc::new(AtomicBool::new(false));
            let total = Arc::new(AtomicU64::new((bytes.len() * tasks.len()) as u64));
            let files = Arc::new(AtomicU64::new(tasks.len() as u64));
            let completed = Arc::new(AtomicU64::new(0));
            let ctx = ProgressContext {
                emit: &|_| {},
                operation_id: "latency-job",
                operation_type: operation,
                cancel: &cancel,
                total_bytes: &total,
                total_files: &files,
                completed_files: &completed,
                last_running_emit: Mutex::new(Instant::now()),
                retry_count: None,
                rate_limit: None,
            };
            let snapshots = Mutex::new(Vec::new());
            let emit = |update| snapshots.lock().unwrap().push(update);
            let policy = || Policy {
                mode,
                max_concurrency: 4,
                manual_concurrency: 4,
                ..Policy::default()
            };
            let mut session = Session::new(
                "latency-job",
                operation,
                &[],
                &root.to_string_lossy(),
                &policy,
                &emit,
            );
            let started = Instant::now();
            let copy = |task: &FileTask, context: &CopyContext<'_>, copied: &mut u64| {
                let mut input = DelayedIo::new(fs::File::open(&task.source).unwrap(), true);
                let mut output = DelayedIo::new(fs::File::create(&task.destination).unwrap(), true);
                *copied = super::super::execute::copy_file_stream(
                    &mut input,
                    &mut output,
                    &task.source,
                    &task.destination,
                    context,
                    0,
                    0,
                    task.size,
                    context.buffer_size(),
                )
                .map_err(String::from)?;
                Ok(())
            };
            let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);
            assert!(result.failed.is_none(), "{:?}", result.failed);
            assert_eq!(result.completed.len(), tasks.len());
            assert_eq!(result.bytes, (bytes.len() * tasks.len()) as u64);
            for task in &tasks {
                assert_eq!(fs::read(&task.destination).unwrap(), bytes);
            }
            let snapshots = snapshots.lock().unwrap();
            assert!(snapshots
                .iter()
                .all(|s| !s.explanation.contains("safety limit")
                    && !s.explanation.contains("Retrying")));
            if mode == Mode::Manual {
                assert!(snapshots.iter().all(|s| s.concurrency == 4));
            }
            // After repeated >750 ms successful I/O, unlimited still means unlimited.
            let wait = Instant::now();
            session.limiter.wait(64 * 1024 * 1024, &cancel).unwrap();
            assert!(wait.elapsed() < Duration::from_millis(100));
            eprintln!("latency filesystem operation={operation} network={network} mode={mode:?}: 8 files, exact contents, no hidden cap, {:.2}s", started.elapsed().as_secs_f64());
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn local_copy_with_cloud_read_and_nas_write_latency() {
        delayed_filesystem_transfer("copy", false);
    }
    #[test]
    fn mounted_copy_with_cloud_read_and_nas_write_latency() {
        delayed_filesystem_transfer("copy", true);
    }
    #[test]
    fn local_move_stream_with_cloud_read_and_nas_write_latency() {
        delayed_filesystem_transfer("move", false);
    }
    #[test]
    fn mounted_move_stream_with_cloud_read_and_nas_write_latency() {
        delayed_filesystem_transfer("move", true);
    }

    #[test]
    fn workers_overlap_commit_exact_bytes_and_clear_active_snapshot() {
        exercise(false);
    }

    #[test]
    fn cancellation_joins_active_workers_and_stops_scheduling() {
        exercise(true);
    }
}
