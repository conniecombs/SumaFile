//! File workers never publish UI events directly: one coordinator owns ordering,
//! aggregate progress, adaptive decisions, and the active-file snapshot.

use super::adaptive::Session;
use super::execute::{copy_file_with_progress_versioned, copy_symlink_with_progress};
use super::reporting::{CopyContext, ProgressContext};
use super::resources::Permit;
use super::tuning::OrderBy;
use crate::transfer_staging::{
    backup_existing_destination, cleanup_owned_path, conflict_for_existing_destination,
    restore_backed_up_destination, SourceVersion, TreeCopyManifest,
};
use serde::Serialize;
use simplefile_core::models::ProgressUpdate;
use simplefile_core::path_conflict::path_exists_no_follow;
use std::collections::{BTreeMap, VecDeque};
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

const INITIAL_PRIME_COUNT: usize = 64;

fn is_high_level_retryable(error: &str) -> bool {
    (error.contains("; after ") && error.contains(" attempts, last failure:"))
        || error.starts_with("RETRYABLE:")
}

fn sort_ready_queue(ready_queue: &mut VecDeque<usize>, tasks: &[FileTask], order_by: OrderBy) {
    if matches!(order_by, OrderBy::Default) || ready_queue.len() <= 1 {
        return;
    }
    let slice = ready_queue.make_contiguous();
    match order_by {
        OrderBy::Default => {}
        OrderBy::SizeAsc => {
            slice.sort_by(|&a, &b| tasks[a].size.cmp(&tasks[b].size).then_with(|| a.cmp(&b)));
        }
        OrderBy::SizeDesc => {
            slice.sort_by(|&a, &b| tasks[b].size.cmp(&tasks[a].size).then_with(|| a.cmp(&b)));
        }
        OrderBy::Name => {
            slice.sort_by(|&a, &b| {
                tasks[a]
                    .source
                    .file_name()
                    .cmp(&tasks[b].source.file_name())
                    .then_with(|| tasks[a].source.cmp(&tasks[b].source))
                    .then_with(|| a.cmp(&b))
            });
        }
    }
}

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

/// Enumerate incrementally with rclone `fs/march`-style multi-checker directory traversal
/// and a bounded read-ahead queue (`--checkers` and `--max-backlog`). Directory traversal
/// reuses `DirEntry::file_type()` and `DirEntry::metadata()` (cached from
/// `FindFirstFileW`/`FindNextFileW` on Windows) to avoid redundant per-file stat syscalls.
/// All checkers and file workers are joined before cleanup or directory metadata preservation.
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

    struct DirQueue {
        dirs: VecDeque<(usize, PathBuf, PathBuf)>,
        active_checkers: usize,
        done: bool,
    }

    #[derive(Default)]
    struct MarchState {
        created_dirs: Vec<(usize, PathBuf, PathBuf)>,
        source_dirs: Vec<(usize, PathBuf)>,
        replaced_backups: Vec<(usize, PathBuf, PathBuf)>,
    }

    let cancel = ctx.progress.cancel;
    let total_bytes = ctx.progress.total_bytes;
    let total_files = ctx.progress.total_files;
    let network = ctx.network;
    let resume_existing = ctx.resume_existing;
    let checker_count = ctx.policy.checkers_limit().clamp(1, 32);
    let channel_cap = ctx.policy.backlog_limit().clamp(64, 8192);

    let mut initial_dirs = VecDeque::new();
    initial_dirs.push_back((0usize, source.to_path_buf(), destination.to_path_buf()));
    let dir_queue = (
        Mutex::new(DirQueue {
            dirs: initial_dirs,
            active_checkers: 0,
            done: false,
        }),
        Condvar::new(),
    );
    let march_state = Mutex::new(MarchState::default());
    let march_stop = AtomicBool::new(false);
    let (task_tx, task_rx) = mpsc::sync_channel::<Result<FileTask, String>>(channel_cap);
    let task_rx = Mutex::new(Some(task_rx));

    let copy_res = std::thread::scope(|march_scope| {
        for _ in 0..checker_count {
            let dir_queue = &dir_queue;
            let march_state = &march_state;
            let march_stop = &march_stop;
            let task_tx = task_tx.clone();
            march_scope.spawn(move || loop {
                let (depth, src_dir, dst_dir) = {
                    let mut q = dir_queue.0.lock().expect("dir queue lock poisoned");
                    loop {
                        if q.done || march_stop.load(Ordering::Acquire) {
                            q.done = true;
                            dir_queue.1.notify_all();
                            return;
                        }
                        if let Some(item) = q.dirs.pop_front() {
                            q.active_checkers += 1;
                            break item;
                        }
                        if q.active_checkers == 0 {
                            q.done = true;
                            dir_queue.1.notify_all();
                            return;
                        }
                        q = dir_queue.1.wait(q).expect("dir queue condvar poisoned");
                    }
                };

                let step_res = (|| -> Result<(), String> {
                    if cancel.load(Ordering::Relaxed) {
                        return Err("Operation cancelled".to_string());
                    }
                    if depth >= 1024 {
                        return Err("Directory nesting exceeds the safe transfer limit".into());
                    }
                    let existing = if path_exists_no_follow(&dst_dir) {
                        if is_real_directory(&dst_dir) {
                            if !replace_existing && !resume_existing {
                                return Err(conflict_for_existing_destination(&dst_dir));
                            }
                            true
                        } else if replace_existing || resume_existing {
                            let backup = backup_existing_destination(&dst_dir)?;
                            march_state
                                .lock()
                                .expect("march state lock poisoned")
                                .replaced_backups
                                .push((depth, dst_dir.clone(), backup));
                            false
                        } else {
                            return Err(conflict_for_existing_destination(&dst_dir));
                        }
                    } else {
                        false
                    };
                    if !existing {
                        create_dir_exclusive(&dst_dir)?;
                        march_state
                            .lock()
                            .expect("march state lock poisoned")
                            .created_dirs
                            .push((depth, src_dir.clone(), dst_dir.clone()));
                    }
                    march_state
                        .lock()
                        .expect("march state lock poisoned")
                        .source_dirs
                        .push((depth, src_dir.clone()));

                    let entries = fs::read_dir(&src_dir)
                        .map_err(|e| format!("Failed to read directory: {e}"))?;

                    for entry_res in entries {
                        if march_stop.load(Ordering::Acquire) {
                            return Ok(());
                        }
                        if cancel.load(Ordering::Relaxed) {
                            return Err("Operation cancelled".to_string());
                        }
                        let entry = entry_res.map_err(|e| format!("Failed to read entry: {e}"))?;
                        let child_src = entry.path();
                        let child_dst = dst_dir.join(entry.file_name());
                        let file_type = match entry.file_type() {
                            Ok(ft) => ft,
                            Err(_) => fs::symlink_metadata(&child_src)
                                .map_err(|e| format!("Failed to stat source: {e}"))?
                                .file_type(),
                        };
                        if file_type.is_dir() {
                            let mut q = dir_queue.0.lock().expect("dir queue lock poisoned");
                            q.dirs.push_back((depth + 1, child_src, child_dst));
                            dir_queue.1.notify_one();
                        } else {
                            let symlink = file_type.is_symlink();
                            let size = if symlink {
                                0
                            } else {
                                match entry.metadata() {
                                    Ok(m) => m.len(),
                                    Err(_) => fs::symlink_metadata(&child_src)
                                        .map_err(|e| format!("Failed to stat source: {e}"))?
                                        .len(),
                                }
                            };
                            total_bytes.fetch_add(size, Ordering::Relaxed);
                            total_files.fetch_add(1, Ordering::Relaxed);
                            let task = FileTask {
                                source: child_src,
                                destination: child_dst,
                                size,
                                network,
                                replace_existing,
                                resume_existing,
                                symlink,
                            };
                            if task_tx.send(Ok(task)).is_err() {
                                return Ok(());
                            }
                        }
                    }
                    Ok(())
                })();

                if let Err(err) = step_res {
                    if !march_stop.swap(true, Ordering::AcqRel) {
                        let _ = task_tx.send(Err(err));
                    }
                    let mut q = dir_queue.0.lock().expect("dir queue lock poisoned");
                    q.active_checkers = q.active_checkers.saturating_sub(1);
                    q.done = true;
                    dir_queue.1.notify_all();
                    return;
                }

                let mut q = dir_queue.0.lock().expect("dir queue lock poisoned");
                q.active_checkers = q.active_checkers.saturating_sub(1);
                if q.active_checkers == 0 && q.dirs.is_empty() {
                    q.done = true;
                    dir_queue.1.notify_all();
                    return;
                }
            });
        }
        drop(task_tx);

        let mut next_task = || -> Result<Option<FileTask>, String> {
            let guard = task_rx.lock().expect("task rx lock poisoned");
            let Some(rx) = guard.as_ref() else {
                return Ok(None);
            };
            match rx.recv() {
                Ok(Ok(task)) => Ok(Some(task)),
                Ok(Err(error)) => Err(error),
                Err(_) => Ok(None),
            }
        };

        let res = copy_files_streaming_using(
            &mut next_task,
            ctx.progress,
            session,
            *completed_bytes,
            &copy_task,
        );

        march_stop.store(true, Ordering::Release);
        let _ = task_rx.lock().expect("task rx lock poisoned").take();
        {
            let mut q = dir_queue.0.lock().expect("dir queue lock poisoned");
            q.done = true;
            dir_queue.1.notify_all();
        }
        res
    });

    *completed_bytes = completed_bytes.saturating_add(copy_res.bytes);

    let mut state = march_state.into_inner().expect("march state lock poisoned");
    state.created_dirs.sort_by_key(|item| item.0);
    state.source_dirs.sort_by_key(|item| item.0);
    state.replaced_backups.sort_by_key(|item| item.0);

    let created_dirs: Vec<(PathBuf, PathBuf)> = state
        .created_dirs
        .into_iter()
        .map(|(_, src, dst)| (src, dst))
        .collect();
    let source_dirs: Vec<PathBuf> = state.source_dirs.into_iter().map(|(_, src)| src).collect();
    let replaced_backups: Vec<(PathBuf, PathBuf)> = state
        .replaced_backups
        .into_iter()
        .map(|(_, dst, backup)| (dst, backup))
        .collect();

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
    copy_files_streaming_using(&mut || Ok(iter.next()), ctx, session, offset, &copy_task)
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
    let backlog_limit = session.controller.policy.backlog_limit();
    let order_by = session.controller.policy.order_by;
    let prime_limit = if matches!(order_by, OrderBy::Default) {
        INITIAL_PRIME_COUNT.min(backlog_limit)
    } else {
        backlog_limit
    };
    let mut tasks = Vec::with_capacity(prime_limit.min(1024));
    let mut ready_queue = VecDeque::with_capacity(prime_limit.min(1024));
    let mut producer_done = false;
    let mut initial_error: Option<(usize, String)> = None;

    while tasks.len() < prime_limit {
        match next_task() {
            Ok(Some(task)) => {
                let idx = tasks.len();
                tasks.push(task);
                ready_queue.push_back(idx);
            }
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

    sort_ready_queue(&mut ready_queue, &tasks, order_by);

    let all_network = tasks.iter().all(|t| t.network);
    let buffer_size = session.controller.policy.buffer_bytes(all_network);
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
    let channel_cap = backlog_limit.clamp(64, 4096);
    let max_hl_attempts = session.controller.policy.high_level_retry_limit().max(1) as usize;
    let worker_policy = session.controller.policy.clone();
    let worker_cancel = Arc::new(AtomicBool::new(initial_error.is_some()));
    let retry_count = AtomicU64::new(0);
    let (work_tx, work_rx) = mpsc::sync_channel::<(usize, FileTask)>(32);
    let work_rx = Mutex::new(work_rx);
    let (event_tx, event_rx) = mpsc::sync_channel::<Message>(channel_cap);
    let (scan_tx, scan_rx) = mpsc::sync_channel::<Result<FileTask, String>>(channel_cap);
    let need_scan_wake = Arc::new(AtomicBool::new(false));
    let mut result = CopyResult {
        bytes: 0,
        completed: Vec::new(),
        versions: BTreeMap::new(),
        tasks: Vec::new(),
        failed: initial_error,
    };
    let mut active = BTreeMap::<usize, ActiveFile>::new();
    let mut deferred_retries = VecDeque::<(usize, String)>::new();
    let mut file_hl_attempts = BTreeMap::<usize, usize>::new();
    let mut last_dispatched_idx = 0usize;
    let mut first_snapshot = true;
    let mut last_display = Instant::now();
    let mut last_retries = 0;
    let operation_id = ctx.operation_id;
    let operation_type = ctx.operation_type;
    if let Some(&first_idx) = ready_queue.front() {
        if let Some(first) = tasks.get(first_idx) {
            ctx.emit_now(
                offset,
                ctx.total_bytes.load(Ordering::Relaxed),
                first.source.to_string_lossy().into(),
                "running",
                None,
            );
        }
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
            let policy = worker_policy.clone();
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
                        if completed_files.load(Ordering::Relaxed) > 0 && update.status == "running"
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
                        policy: policy.clone(),
                    };
                    let mut copied = 0;
                    let permit_buffer = context
                        .buffer_size()
                        .min(usize::try_from(task.size.max(4096)).unwrap_or(usize::MAX));
                    let chunks = if task.size > context.buffer_size() as u64 {
                        3
                    } else {
                        1
                    };
                    let status = Permit::acquire(permit_buffer.saturating_mul(chunks), cancel)
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
                            deferred_retries: &mut VecDeque<(usize, String)>,
                            file_hl_attempts: &mut BTreeMap<usize, usize>,
                            ready_empty: bool,
                            last_display: &Instant| {
            match message {
                Message::ScanAvailable => {}
                Message::Progress(index, update) => {
                    if let Some(file) = active.get_mut(&index) {
                        file.current = file.current.max(update.current);
                    }
                    if update.status == "finalizing"
                        && (last_display.elapsed() >= Duration::from_millis(200)
                            || (active.len() == 1 && *producer_done && ready_empty))
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
                            let attempts = file_hl_attempts.entry(index).or_insert(0);
                            *attempts += 1;
                            if *attempts < max_hl_attempts
                                && !worker_cancel.load(Ordering::Relaxed)
                                && is_high_level_retryable(&error)
                            {
                                let clean = error
                                    .strip_prefix("RETRYABLE:")
                                    .unwrap_or(&error)
                                    .trim()
                                    .to_string();
                                deferred_retries.push_back((index, clean));
                            } else {
                                let clean = error
                                    .strip_prefix("RETRYABLE:")
                                    .unwrap_or(&error)
                                    .trim()
                                    .to_string();
                                if result.failed.is_none() {
                                    result.failed = Some((index, clean));
                                }
                                worker_cancel.store(true, Ordering::Relaxed);
                                *producer_done = true;
                            }
                        }
                    }
                }
            }
        };

        while !producer_done
            || !ready_queue.is_empty()
            || !active.is_empty()
            || (!worker_cancel.load(Ordering::Relaxed) && !deferred_retries.is_empty())
        {
            if ctx.check_cancelled().is_err() {
                worker_cancel.store(true, Ordering::Relaxed);
                producer_done = true;
            }
            let mut added_from_scan = false;
            while !producer_done
                && !worker_cancel.load(Ordering::Relaxed)
                && ready_queue.len().saturating_add(active.len()) < backlog_limit
            {
                match scan_rx.try_recv() {
                    Ok(Ok(task)) => {
                        let idx = tasks.len();
                        tasks.push(task);
                        ready_queue.push_back(idx);
                        added_from_scan = true;
                    }
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
            if added_from_scan {
                sort_ready_queue(&mut ready_queue, &tasks, order_by);
            }

            if producer_done
                && ready_queue.is_empty()
                && active.is_empty()
                && !worker_cancel.load(Ordering::Relaxed)
                && !deferred_retries.is_empty()
            {
                for (retry_index, _prev_err) in deferred_retries.drain(..) {
                    ready_queue.push_back(retry_index);
                }
                sort_ready_queue(&mut ready_queue, &tasks, order_by);
            }

            while !worker_cancel.load(Ordering::Relaxed)
                && active.len() < session.controller.concurrency
            {
                let Some(next_idx) = ready_queue.pop_front() else {
                    break;
                };
                last_dispatched_idx = next_idx;
                let task = tasks[next_idx].clone();
                active.insert(
                    next_idx,
                    ActiveFile {
                        path: task.source.to_string_lossy().into(),
                        current: 0,
                        total: task.size,
                    },
                );
                if work_tx.send((next_idx, task)).is_err() {
                    break;
                }
            }

            if !producer_done
                && !worker_cancel.load(Ordering::Relaxed)
                && ready_queue.is_empty()
                && active.len() < session.controller.concurrency
            {
                need_scan_wake.store(true, Ordering::Release);
                if let Ok(scanned) = scan_rx.try_recv() {
                    need_scan_wake.store(false, Ordering::Release);
                    match scanned {
                        Ok(task) => {
                            let idx = tasks.len();
                            tasks.push(task);
                            ready_queue.push_back(idx);
                            sort_ready_queue(&mut ready_queue, &tasks, order_by);
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
                && ((producer_done && ready_queue.is_empty() && deferred_retries.is_empty())
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
                        &mut deferred_retries,
                        &mut file_hl_attempts,
                        ready_queue.is_empty(),
                        &last_display,
                    );
                    while let Ok(msg) = event_rx.try_recv() {
                        handle_event(
                            msg,
                            &mut active,
                            &mut result,
                            &mut producer_done,
                            &mut deferred_retries,
                            &mut file_hl_attempts,
                            ready_queue.is_empty(),
                            &last_display,
                        );
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    if !producer_done || !active.is_empty() {
                        result.failed = Some((
                            last_dispatched_idx,
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
    if result.failed.is_none() {
        if let Some((failed_idx, failed_err)) = deferred_retries.pop_front() {
            result.failed = Some((failed_idx, failed_err));
        }
    }
    result.tasks = tasks;
    if ctx.check_cancelled().is_err() && result.failed.is_none() {
        result.failed = Some((last_dispatched_idx, "Operation cancelled".into()));
    }
    session.observe(offset.saturating_add(result.bytes), false, 0);
    session.display(Vec::new(), true);
    result
}

#[cfg(test)]
mod tests {
    use super::super::tuning::{CheckMode, Mode, OrderBy, Policy};
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
        let result =
            copy_files_streaming_using(&mut || Ok(iter.next()), &ctx, &mut session, 0, &copy);
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
        let active_policy = Policy {
            mode: Mode::Automatic,
            max_concurrency: 16,
            checkers: 8,
            memory_budget_mb: 24,
            ..Policy::default()
        };
        let copy_ctx = CopyContext {
            progress: &progress,
            operation_id: "small-tree-test",
            network: false,
            resume_existing: false,
            policy: active_policy.clone(),
        };
        let snapshots = Mutex::new(Vec::new());
        let emit_tuning = |update| snapshots.lock().unwrap().push(update);
        let policy = || active_policy.clone();
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

    #[test]
    fn high_level_retry_pass_recovers_transient_straggler_after_healthy_batch_completes() {
        let root = std::env::temp_dir().join(format!(
            "sumafile_high_level_retry_{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(root.join("src")).unwrap();
        fs::create_dir_all(root.join("dst")).unwrap();
        let tasks: Vec<_> = (0..10)
            .map(|index| {
                let source = root.join("src").join(format!("{index}.bin"));
                fs::write(&source, vec![index as u8; 1024]).unwrap();
                FileTask {
                    source,
                    destination: root.join("dst").join(format!("{index}.bin")),
                    size: 1024,
                    network: false,
                    replace_existing: false,
                    resume_existing: false,
                    symlink: false,
                }
            })
            .collect();
        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(10 * 1024));
        let files = Arc::new(AtomicU64::new(10));
        let completed = Arc::new(AtomicU64::new(0));
        let ctx = ProgressContext {
            emit: &|_| {},
            operation_id: "hl-retry-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
        };
        let policy = || Policy::default();
        let mut session = Session::new(
            "hl-retry-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &|_| {},
        );
        let flaky_source = tasks[2].source.clone();
        let flaky_attempts = Arc::new(AtomicU64::new(0));
        let healthy_finished_before_retry = Arc::new(AtomicU64::new(0));
        let healthy_counter = Arc::new(AtomicU64::new(0));

        let copy = {
            let flaky_attempts = flaky_attempts.clone();
            let healthy_finished_before_retry = healthy_finished_before_retry.clone();
            let healthy_counter = healthy_counter.clone();
            move |task: &FileTask, context: &CopyContext<'_>, bytes: &mut u64| {
                if task.source == flaky_source {
                    let attempt = flaky_attempts.fetch_add(1, Ordering::SeqCst);
                    if attempt == 0 {
                        return Err(
                            "Read source [2.bin]: timed out; after 6 attempts, last failure: timed out"
                                .into(),
                        );
                    }
                    healthy_finished_before_retry
                        .store(healthy_counter.load(Ordering::SeqCst), Ordering::SeqCst);
                }
                copy_task(task, context, bytes)?;
                if task.source != flaky_source {
                    healthy_counter.fetch_add(1, Ordering::SeqCst);
                }
                Ok(())
            }
        };

        let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);
        assert!(result.failed.is_none(), "{:?}", result.failed);
        assert_eq!(result.completed.len(), 10);
        assert_eq!(flaky_attempts.load(Ordering::SeqCst), 2);
        assert_eq!(
            healthy_finished_before_retry.load(Ordering::SeqCst),
            9,
            "All 9 healthy files should complete before the deferred high-level retry pass runs"
        );
        for task in &tasks {
            assert_eq!(
                fs::read(&task.source).unwrap(),
                fs::read(&task.destination).unwrap()
            );
        }
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn order_by_size_and_name_controls_dispatch_order_and_checksum_inplace_policy_works() {
        let root = std::env::temp_dir().join(format!(
            "sumafile_order_by_{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(root.join("src")).unwrap();
        fs::create_dir_all(root.join("dst_asc")).unwrap();
        fs::create_dir_all(root.join("dst_desc")).unwrap();

        let sizes = [
            ("c.bin", 4096usize),
            ("a.bin", 512usize),
            ("b.bin", 2048usize),
        ];
        let make_tasks = |dst_sub: &str| -> Vec<FileTask> {
            sizes
                .iter()
                .map(|(name, sz)| {
                    let source = root.join("src").join(name);
                    fs::write(&source, vec![0x5Au8; *sz]).unwrap();
                    FileTask {
                        source,
                        destination: root.join(dst_sub).join(name),
                        size: *sz as u64,
                        network: false,
                        replace_existing: false,
                        resume_existing: false,
                        symlink: false,
                    }
                })
                .collect()
        };

        for (order_by, dst_sub, expected_sizes) in [
            (OrderBy::SizeAsc, "dst_asc", vec![512u64, 2048, 4096]),
            (OrderBy::SizeDesc, "dst_desc", vec![4096u64, 2048, 512]),
        ] {
            let tasks = make_tasks(dst_sub);
            let cancel = Arc::new(AtomicBool::new(false));
            let total = Arc::new(AtomicU64::new(tasks.iter().map(|t| t.size).sum()));
            let files = Arc::new(AtomicU64::new(tasks.len() as u64));
            let completed = Arc::new(AtomicU64::new(0));
            let ctx = ProgressContext {
                emit: &|_| {},
                operation_id: "order-by-test",
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
                manual_concurrency: 1,
                order_by,
                inplace: true,
                check_mode: CheckMode::Checksum,
                ..Policy::default()
            };
            let mut session = Session::new(
                "order-by-test",
                "copy",
                &[],
                &root.to_string_lossy(),
                &policy,
                &|_| {},
            );
            let dispatched_sizes = Arc::new(Mutex::new(Vec::new()));
            let dispatched_clone = dispatched_sizes.clone();
            let copy = move |task: &FileTask, context: &CopyContext<'_>, bytes: &mut u64| {
                dispatched_clone.lock().unwrap().push(task.size);
                copy_task(task, context, bytes).map(|_| ())
            };
            let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);
            assert!(result.failed.is_none(), "{:?}", result.failed);
            assert_eq!(*dispatched_sizes.lock().unwrap(), expected_sizes);
        }

        fs::remove_dir_all(&root).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn overlapped_and_multistream_copy_commit_exact_bytes() {
        let root = std::env::temp_dir().join(format!(
            "sumafile_overlapped_multistream_{}",
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        let src = root.join("large_source.bin");
        let dst_overlapped = root.join("large_overlapped.bin");
        let dst_multistream = root.join("large_multistream.bin");

        let payload: Vec<u8> = (0..(2 * 1024 * 1024 + 371))
            .map(|i| ((i * 31 + 17) % 251) as u8)
            .collect();
        fs::write(&src, &payload).unwrap();

        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(payload.len() as u64));
        let files = Arc::new(AtomicU64::new(1));
        let completed = Arc::new(AtomicU64::new(0));
        let progress = ProgressContext {
            emit: &|_| {},
            operation_id: "overlapped-multistream-test",
            operation_type: "move",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
        };
        let ctx = CopyContext {
            progress: &progress,
            operation_id: "overlapped-multistream-test",
            network: true, // 512 KiB buffer -> triggers 3-slot overlapped ring buffer on 2 MiB file
            resume_existing: false,
            policy: Policy::default(),
        };
        let mut completed_bytes = 0u64;
        let version = super::super::execute::copy_file_with_progress_versioned(
            &src,
            &dst_overlapped,
            &ctx,
            &mut completed_bytes,
            false,
        )
        .expect("overlapped ring-buffer copy should succeed");
        assert!(version.content_hash.is_some());
        assert_eq!(fs::read(&dst_overlapped).unwrap(), payload);

        let src_file = fs::File::open(&src).unwrap();
        let mut dst_file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&dst_multistream)
            .unwrap();
        let copied = super::super::execute::copy_file_multistream(
            &src_file,
            &mut dst_file,
            &src,
            &dst_multistream,
            &ctx,
            0,
            payload.len() as u64,
            256 * 1024,
            4,
        )
        .expect("4-stream parallel byte-range copy should succeed");
        assert_eq!(copied, payload.len() as u64);
        drop(dst_file);
        assert_eq!(fs::read(&dst_multistream).unwrap(), payload);

        fs::remove_dir_all(&root).unwrap();
    }
}
