//! File workers never publish UI events directly: one coordinator owns ordering,
//! aggregate progress, adaptive decisions, and the active-file snapshot.

use super::adaptive::Session;
use super::execute::{copy_file_with_progress_versioned, copy_symlink_with_progress};
use super::reporting::{CopyContext, ProgressContext, LOCAL_BUFFER_SIZE, NETWORK_BUFFER_SIZE};
use super::resources::Permit;
use crate::transfer_staging::{
    backup_existing_destination, cleanup_owned_path, conflict_for_existing_destination,
    restore_backed_up_destination, SourceVersion, TreeCopyManifest,
};
use serde::Serialize;
use simplefile_core::models::ProgressUpdate;
use simplefile_core::path_conflict::path_exists_no_follow;
use std::collections::BTreeMap;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::time::{Duration, Instant};

const INITIAL_PRIME_COUNT: usize = 64;
const READAHEAD_QUEUE_CAPACITY: usize = 2048;

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

/// Enumerate incrementally with a dedicated background scanner and a bounded
/// read-ahead queue. Directory traversal reuses `DirEntry::file_type()` and
/// `DirEntry::metadata()` (cached from `FindFirstFileW`/`FindNextFileW` on Windows)
/// to avoid redundant per-file stat syscalls. All workers are joined before
/// cleanup or directory metadata preservation.
pub(crate) fn copy_tree(
    source: &std::path::Path,
    destination: &std::path::Path,
    ctx: &CopyContext<'_>,
    completed_bytes: &mut u64,
    replace_existing: bool,
    session: &mut Session<'_>,
) -> Result<TreeCopyManifest, String> {
    use super::execute::is_real_directory;
    use simplefile_core::path_conflict::create_dir_exclusive;
    enum Step {
        Entry {
            src: PathBuf,
            dst: PathBuf,
            file_type: Option<fs::FileType>,
            len: Option<u64>,
        },
        Directory(Box<fs::ReadDir>, PathBuf),
    }
    let mut stack = vec![Step::Entry {
        src: source.into(),
        dst: destination.into(),
        file_type: None,
        len: None,
    }];
    let mut created_dirs = Vec::new();
    let mut source_dirs = Vec::new();
    let mut replaced_backups = Vec::new();

    let cancel = ctx.progress.cancel;
    let total_bytes = ctx.progress.total_bytes;
    let total_files = ctx.progress.total_files;
    let network = ctx.network;
    let resume_existing = ctx.resume_existing;

    let mut next_task = || -> Result<Option<FileTask>, String> {
        while let Some(step) = stack.pop() {
            if cancel.load(Ordering::Relaxed) {
                return Err("Operation cancelled".to_string());
            }
            match step {
                Step::Directory(mut entries, target) => {
                    if let Some(entry) = entries.next() {
                        let entry = entry.map_err(|e| format!("Failed to read entry: {e}"))?;
                        let file_type = entry.file_type().ok();
                        let len = match file_type {
                            Some(ft) if ft.is_dir() || ft.is_symlink() => Some(0),
                            _ => entry.metadata().ok().map(|m| m.len()),
                        };
                        let child = target.join(entry.file_name());
                        stack.push(Step::Directory(entries, target));
                        stack.push(Step::Entry {
                            src: entry.path(),
                            dst: child,
                            file_type,
                            len,
                        });
                    }
                }
                Step::Entry {
                    src,
                    dst,
                    file_type,
                    len,
                } => {
                    let (file_type, size) = match (file_type, len) {
                        (Some(ft), Some(size)) => (ft, size),
                        _ => {
                            let metadata = fs::symlink_metadata(&src)
                                .map_err(|e| format!("Failed to stat source: {e}"))?;
                            let ft = metadata.file_type();
                            let size = if ft.is_dir() || ft.is_symlink() {
                                0
                            } else {
                                metadata.len()
                            };
                            (ft, size)
                        }
                    };
                    if file_type.is_dir() {
                        let existing = if path_exists_no_follow(&dst) {
                            if is_real_directory(&dst) {
                                if !replace_existing && !resume_existing {
                                    return Err(conflict_for_existing_destination(&dst));
                                }
                                true
                            } else if replace_existing || resume_existing {
                                let backup = backup_existing_destination(&dst)?;
                                replaced_backups.push((dst.clone(), backup));
                                false
                            } else {
                                return Err(conflict_for_existing_destination(&dst));
                            }
                        } else {
                            false
                        };
                        if !existing {
                            create_dir_exclusive(&dst)?;
                            created_dirs.push((src.clone(), dst.clone()));
                        }
                        source_dirs.push(src.clone());
                        if stack.len() >= 1024 {
                            return Err("Directory nesting exceeds the safe transfer limit".into());
                        }
                        let entries = fs::read_dir(&src)
                            .map_err(|e| format!("Failed to read directory: {e}"))?;
                        stack.push(Step::Directory(Box::new(entries), dst));
                    } else {
                        let symlink = file_type.is_symlink();
                        total_bytes.fetch_add(size, Ordering::Relaxed);
                        total_files.fetch_add(1, Ordering::Relaxed);
                        return Ok(Some(FileTask {
                            source: src,
                            destination: dst,
                            size,
                            network,
                            replace_existing,
                            resume_existing,
                            symlink,
                        }));
                    }
                }
            }
        }
        Ok(None)
    };

    let copy_res = copy_files_streaming_using(
        &mut next_task,
        ctx.progress,
        session,
        *completed_bytes,
        &copy_task,
    );
    *completed_bytes = completed_bytes.saturating_add(copy_res.bytes);

    let result = (|| -> Result<TreeCopyManifest, String> {
        if let Some((_, error)) = copy_res.failed {
            return Err(error);
        }
        for (src, dst) in created_dirs.iter().rev() {
            simplefile_core::file_ops::preserve_basic_metadata(src, dst)?;
        }
        let mut files = Vec::with_capacity(copy_res.completed.len());
        for index in copy_res.completed {
            if let (Some(task), Some(version)) =
                (copy_res.tasks.get(index), copy_res.versions.get(&index))
            {
                files.push((task.source.clone(), version.clone()));
            }
        }
        Ok(TreeCopyManifest { files, source_dirs })
    })();

    if let Err(original) = result {
        // Match the established cleanup contract; never remove merged directories.
        let mut failure = original;
        for (_, dst) in created_dirs.iter().rev() {
            if let Err(error) = cleanup_owned_path(dst) {
                log::warn!("{error}");
                failure = format!("{failure}; {error}");
            }
        }
        for (dst, backup) in replaced_backups.iter().rev() {
            restore_backed_up_destination(dst, backup);
        }
        return Err(failure);
    }

    for (_, backup) in replaced_backups {
        let _ = cleanup_owned_path(&backup);
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
    ScanAvailable,
    Progress(usize, ProgressUpdate),
    Finished(usize, u64, Result<SourceVersion, String>),
}

pub(crate) struct CopyResult {
    pub bytes: u64,
    pub completed: Vec<usize>,
    pub versions: BTreeMap<usize, SourceVersion>,
    pub tasks: Vec<FileTask>,
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
    let mut iter = tasks.iter().cloned();
    copy_files_streaming_using(
        &mut || Ok(iter.next()),
        ctx,
        session,
        offset,
        &copy_task,
    )
}

fn copy_task(
    task: &FileTask,
    context: &CopyContext<'_>,
    copied: &mut u64,
) -> Result<SourceVersion, String> {
    if task.symlink {
        copy_symlink_with_progress(
            &task.source,
            &task.destination,
            context,
            *copied,
            task.replace_existing,
        )
    } else {
        copy_file_with_progress_versioned(
            &task.source,
            &task.destination,
            context,
            copied,
            task.replace_existing,
        )
    }
}

#[cfg(test)]
type CopyFunction<'a> =
    dyn Fn(&FileTask, &CopyContext<'_>, &mut u64) -> Result<(), String> + Sync + 'a;
type CopyVersionedFunction<'a> =
    dyn Fn(&FileTask, &CopyContext<'_>, &mut u64) -> Result<SourceVersion, String> + Sync + 'a;

#[cfg(test)]
fn copy_files_using(
    tasks: &[FileTask],
    ctx: &ProgressContext<'_>,
    session: &mut Session<'_>,
    offset: u64,
    copy: &CopyFunction<'_>,
) -> CopyResult {
    let mut iter = tasks.iter().cloned();
    let wrapped = |task: &FileTask, context: &CopyContext<'_>, copied: &mut u64| {
        copy(task, context, copied)?;
        Ok(SourceVersion {
            is_symlink: task.symlink,
            is_dir: false,
            len: task.size,
            modified: None,
            #[cfg(windows)]
            creation_time: 0,
            #[cfg(windows)]
            last_write_time: 0,
            #[cfg(windows)]
            file_id: None,
            content_hash: None,
        })
    };
    copy_files_streaming_using(&mut || Ok(iter.next()), ctx, session, offset, &wrapped)
}

pub(crate) fn copy_files_streaming_using(
    next_task: &mut (dyn FnMut() -> Result<Option<FileTask>, String> + Send),
    ctx: &ProgressContext<'_>,
    session: &mut Session<'_>,
    offset: u64,
    copy: &CopyVersionedFunction<'_>,
) -> CopyResult {
    let mut tasks = Vec::with_capacity(INITIAL_PRIME_COUNT);
    let mut producer_done = false;
    let mut initial_error: Option<(usize, String)> = None;

    while tasks.len() < INITIAL_PRIME_COUNT {
        match next_task() {
            Ok(Some(task)) => tasks.push(task),
            Ok(None) => {
                producer_done = true;
                break;
            }
            Err(error) => {
                initial_error = Some((tasks.len(), error));
                producer_done = true;
                break;
            }
        }
    }

    if tasks.is_empty() {
        return CopyResult {
            bytes: 0,
            completed: Vec::new(),
            versions: BTreeMap::new(),
            tasks,
            failed: initial_error,
        };
    }

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
    let worker_count = if producer_done {
        tasks.len().min(32)
    } else {
        32
    };
    let worker_cancel = Arc::new(AtomicBool::new(initial_error.is_some()));
    let retry_count = AtomicU64::new(0);
    let (work_tx, work_rx) = mpsc::sync_channel::<(usize, FileTask)>(32);
    let work_rx = Mutex::new(work_rx);
    let (event_tx, event_rx) = mpsc::sync_channel::<Message>(READAHEAD_QUEUE_CAPACITY);
    let (scan_tx, scan_rx) =
        mpsc::sync_channel::<Result<FileTask, String>>(READAHEAD_QUEUE_CAPACITY);
    let need_scan_wake = Arc::new(AtomicBool::new(false));
    let mut result = CopyResult {
        bytes: 0,
        completed: Vec::new(),
        versions: BTreeMap::new(),
        tasks: Vec::new(),
        failed: initial_error,
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
        if !producer_done {
            let cancel = &worker_cancel;
            let wake_tx = event_tx.clone();
            let need_wake = &need_scan_wake;
            scope.spawn(move || {
                while !cancel.load(Ordering::Relaxed) {
                    match next_task() {
                        Ok(Some(task)) => {
                            if scan_tx.send(Ok(task)).is_err() {
                                break;
                            }
                            if need_wake.swap(false, Ordering::AcqRel) {
                                let _ = wake_tx.try_send(Message::ScanAvailable);
                            }
                        }
                        Ok(None) => {
                            let _ = wake_tx.try_send(Message::ScanAvailable);
                            break;
                        }
                        Err(error) => {
                            let _ = scan_tx.send(Err(error));
                            let _ = wake_tx.try_send(Message::ScanAvailable);
                            break;
                        }
                    }
                }
            });
        } else {
            drop(scan_tx);
        }

        for _ in 0..worker_count {
            let work_rx = &work_rx;
            let tx = event_tx.clone();
            let cancel = &worker_cancel;
            let retries = &retry_count;
            let limiter = session.limiter.clone();
            scope.spawn(move || {
                let last_worker_emit = std::cell::Cell::new(
                    Instant::now()
                        .checked_sub(Duration::from_secs(1))
                        .unwrap_or_else(Instant::now),
                );
                loop {
                    // Drop the receiver guard before doing I/O; keeping it in a
                    // while-let scrutinee would serialize every worker.
                    let next_item = { work_rx.lock().expect("work queue lock poisoned").recv() };
                    let Ok((index, task)) = next_item else {
                        break;
                    };
                    let total_bytes = Arc::new(AtomicU64::new(task.size));
                    let total_files = Arc::new(AtomicU64::new(1));
                    let completed_files = Arc::new(AtomicU64::new(0));
                    let emit = |update: ProgressUpdate| {
                        if completed_files.load(Ordering::Relaxed) > 0
                            && update.status == "running"
                        {
                            return;
                        }
                        let now = Instant::now();
                        if update.error.is_none()
                            && now.duration_since(last_worker_emit.get())
                                < Duration::from_millis(50)
                        {
                            return;
                        }
                        last_worker_emit.set(now);
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
                    let permit_buffer = context
                        .buffer_size()
                        .min(usize::try_from(task.size.max(4096)).unwrap_or(usize::MAX));
                    let status = Permit::acquire(permit_buffer.saturating_mul(3), cancel)
                        .and_then(|_permit| copy(&task, &context, &mut copied));
                    if tx.send(Message::Finished(index, copied, status)).is_err() {
                        break;
                    }
                }
            });
        }
        drop(event_tx);

        let handle_event = |message: Message,
                            active: &mut BTreeMap<usize, ActiveFile>,
                            result: &mut CopyResult,
                            producer_done: &mut bool,
                            next: usize,
                            tasks_len: usize,
                            last_display: &Instant| {
            match message {
                Message::ScanAvailable => {}
                Message::Progress(index, update) => {
                    if let Some(file) = active.get_mut(&index) {
                        file.current = file.current.max(update.current);
                    }
                    if update.status == "finalizing"
                        && (last_display.elapsed() >= Duration::from_millis(200)
                            || (active.len() == 1 && *producer_done && next == tasks_len))
                    {
                        let current = offset
                            .saturating_add(result.bytes)
                            .saturating_add(active.values().map(|f| f.current).sum::<u64>());
                        ctx.finalizing(current, update.current_item);
                    }
                }
                Message::Finished(index, bytes, status) => {
                    active.remove(&index);
                    match status {
                        Ok(version) => {
                            result.bytes = result.bytes.saturating_add(bytes);
                            result.completed.push(index);
                            result.versions.insert(index, version);
                            ctx.completed_files.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(error) => {
                            if result.failed.is_none() {
                                result.failed = Some((index, error));
                            }
                            worker_cancel.store(true, Ordering::Relaxed);
                            *producer_done = true;
                        }
                    }
                }
            }
        };

        while !producer_done || next < tasks.len() || !active.is_empty() {
            if ctx.check_cancelled().is_err() {
                worker_cancel.store(true, Ordering::Relaxed);
                producer_done = true;
            }
            while !producer_done
                && !worker_cancel.load(Ordering::Relaxed)
                && (tasks.len().saturating_sub(next)).saturating_add(active.len())
                    < READAHEAD_QUEUE_CAPACITY
            {
                match scan_rx.try_recv() {
                    Ok(Ok(task)) => tasks.push(task),
                    Ok(Err(error)) => {
                        if result.failed.is_none() {
                            result.failed = Some((tasks.len(), error));
                        }
                        worker_cancel.store(true, Ordering::Relaxed);
                        producer_done = true;
                        break;
                    }
                    Err(mpsc::TryRecvError::Empty) => break,
                    Err(mpsc::TryRecvError::Disconnected) => {
                        producer_done = true;
                        break;
                    }
                }
            }
            while !worker_cancel.load(Ordering::Relaxed)
                && next < tasks.len()
                && active.len() < session.controller.concurrency
            {
                let task = tasks[next].clone();
                active.insert(
                    next,
                    ActiveFile {
                        path: task.source.to_string_lossy().into(),
                        current: 0,
                        total: task.size,
                    },
                );
                if work_tx.send((next, task)).is_err() {
                    break;
                }
                next += 1;
            }
            if !producer_done
                && !worker_cancel.load(Ordering::Relaxed)
                && next == tasks.len()
                && active.len() < session.controller.concurrency
            {
                need_scan_wake.store(true, Ordering::Release);
                if let Ok(scanned) = scan_rx.try_recv() {
                    need_scan_wake.store(false, Ordering::Release);
                    match scanned {
                        Ok(task) => {
                            tasks.push(task);
                            continue;
                        }
                        Err(error) => {
                            if result.failed.is_none() {
                                result.failed = Some((tasks.len(), error));
                            }
                            worker_cancel.store(true, Ordering::Relaxed);
                            producer_done = true;
                        }
                    }
                }
            }
            if active.is_empty()
                && ((producer_done && next == tasks.len())
                    || worker_cancel.load(Ordering::Relaxed))
            {
                break;
            }
            if first_snapshot && !active.is_empty() {
                session.display(active.values().cloned().collect(), true);
                first_snapshot = false;
            }
            // A drained queue is not evidence that a lower setting is faster.
            let saturated = active.len() >= session.controller.concurrency;
            match event_rx.recv_timeout(Duration::from_millis(50)) {
                Ok(first_msg) => {
                    handle_event(
                        first_msg,
                        &mut active,
                        &mut result,
                        &mut producer_done,
                        next,
                        tasks.len(),
                        &last_display,
                    );
                    while let Ok(msg) = event_rx.try_recv() {
                        handle_event(
                            msg,
                            &mut active,
                            &mut result,
                            &mut producer_done,
                            next,
                            tasks.len(),
                            &last_display,
                        );
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    if !producer_done || !active.is_empty() {
                        result.failed = Some((
                            next.saturating_sub(1),
                            "Copy workers stopped unexpectedly".into(),
                        ));
                    }
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
        drop(scan_rx);
        drop(work_tx);
    });
    result.tasks = tasks;
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
            copy_task(task, context, bytes).map(|_| ())
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

    #[test]
    fn slow_file_in_first_128_batch_does_not_block_later_files() {
        let root = std::env::temp_dir().join(format!(
            "sumafile_rolling_queue_{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(root.join("src")).unwrap();
        fs::create_dir_all(root.join("dst")).unwrap();
        let total_task_count = 160usize;
        let tasks: Vec<_> = (0..total_task_count)
            .map(|index| {
                let source = root.join("src").join(format!("{index:03}.bin"));
                fs::write(&source, [index as u8; 16]).unwrap();
                FileTask {
                    source,
                    destination: root.join("dst").join(format!("{index:03}.bin")),
                    size: 16,
                    network: false,
                    replace_existing: false,
                    resume_existing: false,
                    symlink: false,
                }
            })
            .collect();

        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new((total_task_count * 16) as u64));
        let files = Arc::new(AtomicU64::new(total_task_count as u64));
        let completed = Arc::new(AtomicU64::new(0));
        let ctx = ProgressContext {
            emit: &|_| {},
            operation_id: "rolling-queue-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
        };
        let policy = || Policy {
            mode: Mode::Manual,
            manual_concurrency: 4,
            ..Policy::default()
        };
        let mut session = Session::new(
            "rolling-queue-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &|_| {},
        );

        let later_files_completed = Arc::new(AtomicU64::new(0));
        let later_files_for_worker = later_files_completed.clone();
        let slow_file_path = tasks[0].source.clone();

        let copy = move |task: &FileTask, context: &CopyContext<'_>, bytes: &mut u64| {
            if task.source == slow_file_path {
                // Wait until at least 20 files beyond the first 128-file window (indices 128..160)
                // have been scheduled and completed while this slow file in batch 0 is still active!
                let deadline = Instant::now() + Duration::from_secs(5);
                while later_files_for_worker.load(Ordering::SeqCst) < 20 {
                    if Instant::now() >= deadline {
                        return Err(
                            "Files beyond the first 128-file batch were blocked by slow file"
                                .into(),
                        );
                    }
                    std::thread::sleep(Duration::from_millis(2));
                }
            }
            let version = copy_task(task, context, bytes)?;
            if let Some(stem) = task.source.file_stem().and_then(|s| s.to_str()) {
                if let Ok(idx) = stem.parse::<usize>() {
                    if idx >= 128 {
                        later_files_for_worker.fetch_add(1, Ordering::SeqCst);
                    }
                }
            }
            Ok(version)
        };

        let mut iter = tasks.iter().cloned();
        let result = copy_files_streaming_using(
            &mut || Ok(iter.next()),
            &ctx,
            &mut session,
            0,
            &copy,
        );
        assert!(result.failed.is_none(), "{:?}", result.failed);
        assert_eq!(result.completed.len(), total_task_count);
        assert_eq!(later_files_completed.load(Ordering::SeqCst), 32);

        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn readahead_scanner_streams_large_small_file_tree_with_scaled_concurrency() {
        let root = std::env::temp_dir().join(format!(
            "sumafile_small_tree_{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let src_root = root.join("src");
        let dst_root = root.join("dst");
        fs::create_dir_all(&src_root).unwrap();

        let total_files_expected = 300usize;
        for i in 0..total_files_expected {
            let folder = src_root.join(format!("bucket_{:02}", i / 25));
            fs::create_dir_all(&folder).unwrap();
            fs::write(
                folder.join(format!("small_{i:04}.txt")),
                format!("payload-{i}").as_bytes(),
            )
            .unwrap();
        }

        let cancel = Arc::new(AtomicBool::new(false));
        let total_bytes = Arc::new(AtomicU64::new(0));
        let total_files = Arc::new(AtomicU64::new(0));
        let completed_files = Arc::new(AtomicU64::new(0));
        let progress = ProgressContext {
            emit: &|_| {},
            operation_id: "small-tree-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total_bytes,
            total_files: &total_files,
            completed_files: &completed_files,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
        };
        let copy_ctx = CopyContext {
            progress: &progress,
            operation_id: "small-tree-test",
            network: false,
            resume_existing: false,
        };
        let snapshots = Mutex::new(Vec::new());
        let emit_tuning = |update| snapshots.lock().unwrap().push(update);
        let policy = || Policy {
            mode: Mode::Automatic,
            max_concurrency: 16,
            memory_budget_mb: 24,
            ..Policy::default()
        };
        let mut session = Session::new(
            "small-tree-test",
            "copy",
            &[src_root.to_string_lossy().into_owned()],
            &dst_root.to_string_lossy(),
            &policy,
            &emit_tuning,
        );
        let mut completed_bytes = 0u64;
        let manifest = copy_tree(
            &src_root,
            &dst_root,
            &copy_ctx,
            &mut completed_bytes,
            false,
            &mut session,
        )
        .expect("small-file tree copy should succeed");

        assert_eq!(manifest.files.len(), total_files_expected);
        assert_eq!(
            completed_files.load(Ordering::Relaxed),
            total_files_expected as u64
        );
        let snapshots = snapshots.lock().unwrap();
        assert!(
            snapshots.iter().any(|s| s.concurrency >= 12),
            "Expected small-file tree burst to scale concurrency to at least 12"
        );
        for i in 0..total_files_expected {
            let copied = dst_root
                .join(format!("bucket_{:02}", i / 25))
                .join(format!("small_{i:04}.txt"));
            assert_eq!(fs::read(copied).unwrap(), format!("payload-{i}").as_bytes());
        }

        fs::remove_dir_all(&root).unwrap();
    }
}
