//! File workers never publish UI events directly: one coordinator owns ordering,
//! aggregate progress, adaptive decisions, and the active-file snapshot.
//!
//! One coordinator serves a whole selection: top-level files are queued up front
//! and every selected directory is walked by the shared checker pool, so workers
//! stay busy across trees. A failed or conflicting file is recorded and the rest
//! of the selection continues; only user cancellation stops scheduling.

use super::adaptive::Session;
use super::execute::{copy_file_with_progress_versioned, copy_symlink_with_progress, FileOutcome};
use super::reporting::{CopyContext, ProgressContext, RetryCounts};
use super::resources::Permit;
use super::tuning::OrderBy;
use crate::transfer_reliability::{Failure, FailureKind};
use crate::transfer_staging::{SourceVersion, TreeCopyManifest};
use serde::Serialize;
use simplefile_core::models::ProgressUpdate;
use simplefile_core::path_conflict::path_exists_no_follow;
use std::collections::{BTreeMap, VecDeque};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Condvar, Mutex};
use std::time::{Duration, Instant};
use sumafile_transfer::tuning::{lane_index, LaneReport};

/// Up to this many tasks are gathered before the first dispatch so the adaptive
/// controller can classify the workload, but never for longer than
/// `PRIME_WINDOW`: copying starts as soon as work is available.
const PRIME_COUNT: usize = 64;
/// The most worker threads one pass starts; the service-wide limit on files
/// copied at once.
const MAX_WORKERS: usize = 32;
const PRIME_WINDOW: Duration = Duration::from_millis(150);

/// How an existing destination is handled for one file or directory.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum ConflictAction {
    Error,
    /// Exact Undo/Redo replay. Even a matching file or folder is a conflict.
    CreateOnly,
    Skip,
    Replace,
    KeepBoth,
}

impl ConflictAction {
    pub(crate) fn parse(action: &str) -> Self {
        match action.trim().to_ascii_lowercase().as_str() {
            "skip" => Self::Skip,
            "create-only" => Self::CreateOnly,
            "replace" => Self::Replace,
            "rename" | "keep-both" | "keep_both" => Self::KeepBoth,
            _ => Self::Error,
        }
    }
}

/// Where two queued files stand in the chosen transfer order.
fn queue_order(
    tasks: &BTreeMap<usize, FileTask>,
    order_by: OrderBy,
    a: usize,
    b: usize,
) -> std::cmp::Ordering {
    match order_by {
        OrderBy::Default => a.cmp(&b),
        OrderBy::SizeAsc => tasks[&a].size.cmp(&tasks[&b].size).then_with(|| a.cmp(&b)),
        OrderBy::SizeDesc => tasks[&b].size.cmp(&tasks[&a].size).then_with(|| a.cmp(&b)),
        OrderBy::Name => tasks[&a]
            .source
            .file_name()
            .cmp(&tasks[&b].source.file_name())
            .then_with(|| tasks[&a].source.cmp(&tasks[&b].source))
            .then_with(|| a.cmp(&b)),
    }
}

/// Queue the file `index` (already in `tasks`) at its place in the chosen
/// order. The queue stays sorted, so the place is found by bisection; sorting
/// the whole queue again for each file the scan delivered cost a full sort of
/// up to "Maximum queued items" entries per file.
fn enqueue_ready(
    queue: &mut VecDeque<usize>,
    tasks: &BTreeMap<usize, FileTask>,
    order_by: OrderBy,
    index: usize,
) {
    if matches!(order_by, OrderBy::Default) {
        queue.push_back(index);
        return;
    }
    let place = queue.partition_point(|&queued| {
        queue_order(tasks, order_by, queued, index) != std::cmp::Ordering::Greater
    });
    queue.insert(place, index);
}

#[derive(Clone, Debug)]
pub(crate) struct FileTask {
    pub source: PathBuf,
    pub destination: PathBuf,
    pub size: u64,
    pub network: bool,
    pub conflict: ConflictAction,
    pub symlink: bool,
    /// Selected directly by the user (reported in `committed`), not found by a walk.
    pub top_level: bool,
    /// Existing destination root whose Undo receipt records this file.
    pub merge_root: Option<usize>,
    /// Move: delete the verified source right after the file is published.
    pub delete_source: bool,
    /// A targeted retry: a destination that already holds the same file is this
    /// operation's own earlier copy, not a conflict.
    pub retry: bool,
    /// The destination's folder was created by this transfer, so no other
    /// item can have the destination's name.
    pub in_new_folder: bool,
}

/// A selected directory: walked by the checkers, published as a whole when its
/// destination did not exist yet.
#[derive(Clone, Debug)]
pub(crate) struct RootTask {
    pub source: PathBuf,
    pub destination: PathBuf,
    pub network: bool,
    pub conflict: ConflictAction,
    pub delete_source: bool,
    /// A targeted retry: a destination that already holds the same file is this
    /// operation's own earlier copy, not a conflict.
    pub retry: bool,
}

/// One failed file or directory, with the destination it was headed for.
#[derive(Clone, Debug)]
pub(crate) struct TaskFailure {
    pub source: PathBuf,
    pub destination: PathBuf,
    pub failure: Failure,
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
    Finished(usize, u64, Result<FileOutcome, Failure>),
}

pub(crate) struct CopyResult {
    pub bytes: u64,
    /// Bytes of files skipped because the destination already existed and the
    /// action was skip. Counted in progress so skipped files appear completed,
    /// without feeding throughput measurements (`lane_done`).
    pub skipped_bytes: u64,
    /// Bytes written by files that failed for good. They stay in the progress
    /// readout: a file that fails after its last byte must not make the bar
    /// run backwards, and the final error report counts what was processed.
    pub failed_bytes: u64,
    /// Indices of completed top-level tasks, in completion order.
    pub completed: Vec<usize>,
    pub reused: std::collections::HashSet<usize>,
    /// Final destinations that differ from the task's planned one (keep-both).
    pub destinations: BTreeMap<usize, PathBuf>,
    /// The completed top-level tasks themselves (source and planned destination).
    pub completed_tasks: BTreeMap<usize, FileTask>,
    /// Tasks skipped because the destination existed and the action was skip.
    pub skipped: Vec<(usize, FileTask)>,
    /// Files that could not be transferred, in failure order.
    pub failed: Vec<(usize, TaskFailure)>,
    /// A directory walk failed; nothing after it was scheduled.
    pub scan_error: Option<String>,
    pub cancelled: bool,
    #[cfg(test)]
    pub retained_tasks_peak: usize,
}

impl CopyResult {
    fn new() -> Self {
        Self {
            bytes: 0,
            skipped_bytes: 0,
            failed_bytes: 0,
            completed: Vec::new(),
            reused: Default::default(),
            destinations: BTreeMap::new(),
            completed_tasks: BTreeMap::new(),
            skipped: Vec::new(),
            failed: Vec::new(),
            scan_error: None,
            cancelled: false,
            #[cfg(test)]
            retained_tasks_peak: 0,
        }
    }

    /// The error a caller that handles one failure at a time should see.
    #[cfg(test)]
    pub(crate) fn first_error(&self) -> Option<String> {
        if self.cancelled {
            return Some(crate::transfer_reliability::CANCELLED_MESSAGE.to_string());
        }
        if let Some(error) = &self.scan_error {
            return Some(error.clone());
        }
        self.failed
            .first()
            .map(|(_, failure)| failure.failure.message.clone())
    }

    #[cfg(test)]
    pub(crate) fn first_failure(&self) -> Option<(usize, String)> {
        if self.cancelled {
            return Some((
                usize::MAX,
                crate::transfer_reliability::CANCELLED_MESSAGE.to_string(),
            ));
        }
        self.failed
            .first()
            .map(|(index, failure)| (*index, failure.failure.message.clone()))
    }
}

struct DirQueue {
    dirs: VecDeque<(usize, usize, PathBuf, PathBuf, ConflictAction)>,
    active_checkers: usize,
    done: bool,
}

#[derive(Default)]
struct MarchState {
    created_dirs: Vec<(usize, usize, PathBuf, PathBuf)>,
    source_dirs: Vec<(usize, usize, PathBuf, SourceVersion)>,
    dir_failures: Vec<TaskFailure>,
}

/// Outcome of one selected directory after the walk, metadata and promotion.
pub(crate) struct RootOutcome {
    pub manifest: TreeCopyManifest,
    pub changes: Option<super::TransferChanges>,
    /// The root itself could not be created, promoted or recovered.
    pub error: Option<Failure>,
}

pub(crate) struct SelectionResult {
    pub copy: CopyResult,
    pub roots: Vec<RootOutcome>,
    /// Directories that could not be read, created or published (not files).
    pub dir_failures: Vec<TaskFailure>,
}

/// Open a real directory for merging or create it exclusively. Only the creator
/// owns it for Undo, including when two transfers reach the same path together.
fn prepare_directory(path: &Path, allow_merge: bool) -> Result<bool, Failure> {
    if !super::execute::is_real_directory(path) {
        sumafile_transfer::ops::record(sumafile_transfer::ops::Op::CreateDir, path);
        match fs::create_dir(path) {
            Ok(()) => return Ok(true),
            Err(error) if simplefile_core::path_conflict::is_already_exists_error(&error) => {
                if !super::execute::is_real_directory(path) {
                    return Err(Failure::conflict(format!(
                        "destination exists with a different type: {}",
                        path.display()
                    )));
                }
            }
            Err(error) => return Err(Failure::io("Create destination directory", path, error)),
        }
    }
    if !allow_merge {
        return Err(Failure::conflict(
            crate::transfer_staging::conflict_for_existing_destination(path),
        ));
    }
    crate::transfer_journal::recover_directory(path)?;
    Ok(false)
}

/// Walk every selected directory with `rclone fs/march`-style multi-checker
/// traversal and feed the shared worker pool together with the top-level files.
/// Directory traversal reuses `DirEntry::file_type()` and `DirEntry::metadata()`
/// (cached from `FindFirstFileW`/`FindNextFileW` on Windows). All checkers and
/// file workers are joined before cleanup or directory metadata preservation.
pub(crate) fn copy_selection(
    roots: &[RootTask],
    files: Vec<FileTask>,
    ctx: &CopyContext<'_>,
    completed_bytes: &mut u64,
    session: &mut Session<'_>,
) -> SelectionResult {
    let cancel = ctx.progress.cancel;
    let total_bytes = ctx.progress.total_bytes;
    let total_files = ctx.progress.total_files;
    let checker_count = ctx.policy.checkers_limit().clamp(1, 32);
    let channel_cap = ctx.policy.backlog_limit().clamp(64, 8192);

    let march_state = Mutex::new(MarchState::default());
    let mut initial_dirs = VecDeque::new();
    let mut merged_roots = vec![false; roots.len()];
    let mut roots_created = vec![false; roots.len()];
    let mut root_errors: Vec<Option<Failure>> = Vec::with_capacity(roots.len());
    for (root_index, root) in roots.iter().enumerate() {
        let mut root_error = None;
        let allow_merge = !matches!(
            root.conflict,
            ConflictAction::KeepBoth | ConflictAction::CreateOnly
        );
        match prepare_directory(&root.destination, allow_merge) {
            Ok(created) => {
                roots_created[root_index] = created;
                merged_roots[root_index] =
                    !created || (root.retry && root.conflict != ConflictAction::CreateOnly);
                if created {
                    march_state
                        .lock()
                        .expect("march state lock poisoned")
                        .created_dirs
                        .push((0, root_index, root.source.clone(), root.destination.clone()));
                }
                initial_dirs.push_back((
                    0usize,
                    root_index,
                    root.source.clone(),
                    root.destination.clone(),
                    if root.conflict == ConflictAction::KeepBoth {
                        ConflictAction::Error
                    } else {
                        root.conflict
                    },
                ));
            }
            Err(error) => root_error = Some(error),
        }
        root_errors.push(root_error);
    }

    let actual_checkers = if initial_dirs.is_empty() {
        0
    } else {
        checker_count
    };
    let dir_queue = (
        Mutex::new(DirQueue {
            done: initial_dirs.is_empty(),
            dirs: initial_dirs,
            active_checkers: 0,
        }),
        Condvar::new(),
    );
    let march_stop = AtomicBool::new(false);
    let (task_tx, task_rx) = mpsc::sync_channel::<Result<FileTask, String>>(channel_cap);
    let task_rx = Mutex::new(Some(task_rx));
    let roots_ref = roots;
    let mut pending_files: VecDeque<FileTask> = files.into_iter().collect();

    let merged_roots = &merged_roots;
    let roots_created = &roots_created;
    let mut changes: Vec<_> = merged_roots
        .iter()
        .map(|merged| merged.then(super::TransferChanges::default))
        .collect();
    let copy_res = std::thread::scope(|march_scope| {
        for _ in 0..actual_checkers {
            let dir_queue = &dir_queue;
            let march_state = &march_state;
            let march_stop = &march_stop;
            let task_tx = task_tx.clone();
            march_scope.spawn(move || loop {
                let (depth, root_index, src_dir, dst_dir, conflict) = {
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
                let root = &roots_ref[root_index];
                let network = root.network;
                let delete_source = root.delete_source;

                let step_res = (|| -> Result<(), Failure> {
                    if cancel.load(Ordering::Relaxed) {
                        return Err(Failure::cancelled());
                    }
                    if depth >= 1024 {
                        return Err(Failure::from(
                            "Directory nesting exceeds the safe transfer limit",
                        ));
                    }
                    let created_here = if depth > 0 {
                        let created =
                            prepare_directory(&dst_dir, conflict != ConflictAction::CreateOnly)?;
                        if created {
                            march_state
                                .lock()
                                .expect("march state lock poisoned")
                                .created_dirs
                                .push((depth, root_index, src_dir.clone(), dst_dir.clone()));
                        }
                        created
                    } else {
                        roots_created[root_index]
                    };
                    // Only a move looks at these again, to delete each source
                    // directory it emptied. A copy would pay several requests
                    // per directory, and keep one record per directory for the
                    // whole transfer, for nothing.
                    if delete_source {
                        let version =
                            crate::transfer_staging::capture_source_version_for_path(&src_dir)?;
                        march_state
                            .lock()
                            .expect("march state lock poisoned")
                            .source_dirs
                            .push((depth, root_index, src_dir.clone(), version));
                    }

                    sumafile_transfer::ops::record(sumafile_transfer::ops::Op::List, &src_dir);
                    let entries = fs::read_dir(&src_dir)
                        .map_err(|e| Failure::io("Read directory", &src_dir, e))?;

                    for entry_res in entries {
                        if march_stop.load(Ordering::Acquire) {
                            return Ok(());
                        }
                        if cancel.load(Ordering::Relaxed) {
                            return Err(Failure::cancelled());
                        }
                        let entry =
                            entry_res.map_err(|e| Failure::io("Read directory", &src_dir, e))?;
                        let child_src = entry.path();
                        let child_dst = dst_dir.join(entry.file_name());
                        let file_type = match entry.file_type() {
                            Ok(ft) => ft,
                            Err(_) => fs::symlink_metadata(&child_src)
                                .map_err(|e| Failure::io("Inspect source", &child_src, e))?
                                .file_type(),
                        };
                        if file_type.is_dir() {
                            let mut q = dir_queue.0.lock().expect("dir queue lock poisoned");
                            q.dirs.push_back((
                                depth + 1,
                                root_index,
                                child_src,
                                child_dst,
                                conflict,
                            ));
                            dir_queue.1.notify_one();
                        } else {
                            let symlink = file_type.is_symlink();
                            let size = if symlink {
                                0
                            } else {
                                match entry.metadata() {
                                    Ok(m) => m.len(),
                                    Err(_) => fs::symlink_metadata(&child_src)
                                        .map_err(|e| Failure::io("Inspect source", &child_src, e))?
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
                                conflict,
                                symlink,
                                top_level: false,
                                merge_root: merged_roots[root_index].then_some(root_index),
                                delete_source,
                                retry: root.retry && root.conflict == ConflictAction::Error,
                                in_new_folder: created_here,
                            };
                            if task_tx.send(Ok(task)).is_err() {
                                return Ok(());
                            }
                        }
                    }
                    Ok(())
                })();

                if let Err(err) = step_res {
                    if err.is_cancelled() {
                        if !march_stop.swap(true, Ordering::AcqRel) {
                            let _ = task_tx.send(Err(err.message));
                        }
                        let mut q = dir_queue.0.lock().expect("dir queue lock poisoned");
                        q.active_checkers = q.active_checkers.saturating_sub(1);
                        q.done = true;
                        dir_queue.1.notify_all();
                        return;
                    }
                    // One unreadable or conflicting directory does not stop the
                    // rest of the selection; it is reported with its subtree.
                    march_state
                        .lock()
                        .expect("march state lock poisoned")
                        .dir_failures
                        .push(TaskFailure {
                            source: src_dir.clone(),
                            destination: dst_dir.clone(),
                            failure: err,
                        });
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
            if let Some(task) = pending_files.pop_front() {
                return Ok(Some(task));
            }
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

        let mut completed =
            |task: &FileTask, _: &SourceVersion, reused: bool| -> Result<(), String> {
                if let Some(index) = task.merge_root {
                    if !reused && task.conflict != ConflictAction::Replace {
                        let relative = task
                            .destination
                            .strip_prefix(&roots_ref[index].destination)
                            .expect("file in merge root");
                        changes[index]
                            .as_mut()
                            .expect("merge receipt")
                            .files
                            .push(relative.to_string_lossy().into_owned());
                    }
                }
                Ok(())
            };
        let res = copy_files_streaming_with_sink(
            &mut next_task,
            ctx.progress,
            session,
            *completed_bytes,
            &copy_task,
            Some(&mut completed),
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

    *completed_bytes = completed_bytes
        .saturating_add(copy_res.bytes)
        .saturating_add(copy_res.skipped_bytes);

    let mut state = march_state.into_inner().expect("march state lock poisoned");
    state.created_dirs.sort_by_key(|item| item.0);
    state.source_dirs.sort_by_key(|item| item.0);
    let dir_failures = std::mem::take(&mut state.dir_failures);

    let mut root_outcomes: Vec<RootOutcome> = root_errors
        .into_iter()
        .zip(changes)
        .map(|(error, changes)| RootOutcome {
            changes,
            manifest: TreeCopyManifest::default(),
            error,
        })
        .collect();
    for (_, root_index, src, version) in state.source_dirs {
        root_outcomes[root_index]
            .manifest
            .source_dirs
            .push((src, version));
    }

    for (_, index, _, dst) in &state.created_dirs {
        if let Some(changes) = &mut root_outcomes[*index].changes {
            let relative = dst
                .strip_prefix(&roots[*index].destination)
                .expect("directory in merge root");
            changes
                .created_dirs
                .push(relative.to_string_lossy().into_owned());
        }
    }

    if copy_res.cancelled {
        // Workers are joined and their interrupted partial files are removed.
        // Everything copied so far stays where it is; sources are untouched.
        return SelectionResult {
            copy: copy_res,
            roots: root_outcomes,
            dir_failures,
        };
    }

    // "Finishing" covers the folders' times too: they are set after the
    // last file, and on a share that takes a while.
    ctx.progress.finalizing(
        *completed_bytes,
        roots
            .first()
            .map(|root| root.destination.to_string_lossy().to_string())
            .unwrap_or_default(),
    );
    restore_directory_metadata(&state.created_dirs, checker_count);

    SelectionResult {
        copy: copy_res,
        roots: root_outcomes,
        dir_failures,
    }
}

/// Fewest created folders that are worth a second thread.
const DIRECTORIES_PER_METADATA_THREAD: usize = 64;

/// Give each folder the transfer created its source folder's times and its
/// read-only, hidden and system attributes, now that nothing is written into
/// it any more.
///
/// The folders do not depend on one another (setting a folder's times does
/// not touch its parent's), so they are shared out between up to `threads`
/// threads. One after another, each folder costs a few requests, and
/// on a share a tree of many folders spent that time after its last file.
fn restore_directory_metadata(created: &[(usize, usize, PathBuf, PathBuf)], threads: usize) {
    let restore = |(_, _, src, dst): &(usize, usize, PathBuf, PathBuf)| {
        // The source folder's facts by path, then its times and attributes
        // in one call through a handle opened on the copy.
        use sumafile_transfer::ops::{record, Op};
        record(Op::Stat, src);
        record(Op::Open, dst);
        record(Op::Query, dst);
        record(Op::Set, dst);
        if let Err(error) = simplefile_core::file_ops::preserve_basic_metadata(src, dst) {
            log::warn!(
                "Could not preserve directory metadata for {}: {error}",
                dst.display()
            );
        }
    };
    let threads = threads
        .min(created.len() / DIRECTORIES_PER_METADATA_THREAD)
        .max(1);
    if threads == 1 {
        // Deepest first, as before.
        created.iter().rev().for_each(restore);
        return;
    }
    let next = std::sync::atomic::AtomicUsize::new(0);
    std::thread::scope(|scope| {
        for _ in 0..threads {
            scope.spawn(|| loop {
                let index = next.fetch_add(1, Ordering::Relaxed);
                let Some(directory) = created.len().checked_sub(index + 1) else {
                    break;
                };
                restore(&created[directory]);
            });
        }
    });
}

/// Walk one directory into `destination` (test and compatibility entry point).
#[cfg(test)]
pub(crate) fn copy_tree(
    source: &Path,
    destination: &Path,
    ctx: &CopyContext<'_>,
    completed_bytes: &mut u64,
    replace_existing: bool,
    session: &mut Session<'_>,
) -> Result<TreeCopyManifest, String> {
    let root = RootTask {
        source: source.to_path_buf(),
        destination: destination.to_path_buf(),
        network: ctx.network,
        conflict: if replace_existing {
            ConflictAction::Replace
        } else {
            ConflictAction::Error
        },
        delete_source: ctx.progress.operation_type == "move",
        retry: false,
    };
    let mut result = copy_selection(&[root], Vec::new(), ctx, completed_bytes, session);
    if let Some(error) = result.copy.first_error() {
        return Err(error);
    }
    if let Some(failure) = result.dir_failures.first() {
        return Err(failure.failure.message.clone());
    }
    let outcome = result.roots.remove(0);
    match outcome.error {
        Some(error) => Err(error.message),
        None => Ok(outcome.manifest),
    }
}

fn copy_task(
    task: &FileTask,
    context: &CopyContext<'_>,
    copied: &mut u64,
) -> Result<FileOutcome, Failure> {
    // A retried file whose destination already holds the same content (by the
    // selected check mode) was transferred by the attempt that reported the
    // failure: it is done, apart from a move's source that is still to delete.
    // Because of that delete, a move compares the bytes whatever the mode.
    let already_copied = task.retry
        && task.conflict == ConflictAction::Error
        && !task.symlink
        && path_exists_no_follow(&task.destination)
        && crate::transfer_staging::retried_file_already_transferred(
            &task.source,
            &task.destination,
            context.policy.check_mode,
            task.delete_source,
        )
        .unwrap_or(false);
    if task.delete_source && !already_copied {
        if let Some(renamed) =
            super::execute::move_by_rename(&task.source, &task.destination, task.conflict, context)
        {
            let outcome = renamed?;
            if let FileOutcome::Copied { .. } = &outcome {
                // Renamed in place: no byte was transferred.
                context.progress.not_transferred(task.size);
                *copied = copied.saturating_add(task.size);
                context
                    .progress
                    .complete_file(*copied, task.source.to_string_lossy().into());
            }
            return Ok(outcome);
        }
    }
    let outcome = if already_copied {
        let version = crate::transfer_staging::capture_source_version_for_path(&task.source)?;
        context.progress.not_transferred(task.size);
        *copied = copied.saturating_add(task.size);
        context
            .progress
            .complete_file(*copied, task.source.to_string_lossy().into());
        FileOutcome::Copied {
            reused: true,
            version,
            destination: task.destination.clone(),
        }
    } else if task.symlink {
        copy_symlink_with_progress(
            &task.source,
            &task.destination,
            context,
            *copied,
            task.conflict,
        )?
    } else {
        copy_file_with_progress_versioned(
            &task.source,
            &task.destination,
            context,
            copied,
            task.conflict,
            task.in_new_folder,
        )?
    };
    if task.delete_source {
        if let FileOutcome::Copied { version, .. } = &outcome {
            crate::transfer_staging::delete_verified_source(&task.source, version).map_err(
                |error| {
                    Failure::with_kind(
                        format!("Copied but failed to delete source: {error}"),
                        FailureKind::Permanent,
                    )
                },
            )?;
        }
    }
    Ok(outcome)
}

/// The failure reported for a file whose copy panicked.
fn worker_panic_failure(task: &FileTask, panic: &(dyn std::any::Any + Send)) -> Failure {
    let reason = panic
        .downcast_ref::<&str>()
        .map(|text| (*text).to_string())
        .or_else(|| panic.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "no reason given".into());
    log::error!(
        "Copying [{}] stopped unexpectedly: {reason}",
        task.source.display()
    );
    Failure::with_kind(
        format!(
            "Copying stopped unexpectedly [{}]: {reason}",
            task.source.display()
        ),
        FailureKind::Permanent,
    )
}

#[cfg(test)]
type CopyFunction<'a> =
    dyn Fn(&FileTask, &CopyContext<'_>, &mut u64) -> Result<(), String> + Sync + 'a;
type CopyVersionedFunction<'a> =
    dyn Fn(&FileTask, &CopyContext<'_>, &mut u64) -> Result<FileOutcome, Failure> + Sync + 'a;

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
        copy(task, context, copied).map_err(Failure::from)?;
        Ok(FileOutcome::Copied {
            reused: false,
            version: SourceVersion {
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
            },
            destination: task.destination.clone(),
        })
    };
    copy_files_streaming_using(&mut || Ok(iter.next()), ctx, session, offset, &wrapped)
}

#[cfg(test)]
pub(crate) fn copy_files_streaming_using(
    next_task: &mut (dyn FnMut() -> Result<Option<FileTask>, String> + Send),
    ctx: &ProgressContext<'_>,
    session: &mut Session<'_>,
    offset: u64,
    copy: &CopyVersionedFunction<'_>,
) -> CopyResult {
    copy_files_streaming_with_sink(next_task, ctx, session, offset, copy, None)
}

type CompletionSink<'a> = dyn FnMut(&FileTask, &SourceVersion, bool) -> Result<(), String> + 'a;

/// Files in flight and their copied bytes, per lane.
fn lane_load(active: &BTreeMap<usize, ActiveFile>, two_lanes: bool) -> ([usize; 2], [u64; 2]) {
    let mut files = [0usize; 2];
    let mut bytes = [0u64; 2];
    for file in active.values() {
        let lane = lane_index(two_lanes, file.total);
        files[lane] += 1;
        bytes[lane] = bytes[lane].saturating_add(file.current);
    }
    (files, bytes)
}

/// Tell the session what is waiting in each lane that has not been classified
/// yet, so the lane gets its starting point before its first file is sent.
fn open_lanes(
    session: &mut Session<'_>,
    open: &mut [bool; 2],
    ready: &[VecDeque<usize>; 2],
    tasks: &BTreeMap<usize, FileTask>,
    buffer_size: usize,
    moved: &[u64; 2],
    finished: &[u64; 2],
) {
    for lane in 0..2 {
        if open[lane] || ready[lane].is_empty() {
            continue;
        }
        let bytes = ready[lane].iter().map(|index| tasks[index].size).sum();
        session.workload(
            lane,
            bytes,
            ready[lane].len(),
            buffer_size,
            moved[lane],
            finished[lane],
        );
        open[lane] = true;
    }
}

fn copy_files_streaming_with_sink(
    next_task: &mut (dyn FnMut() -> Result<Option<FileTask>, String> + Send),
    ctx: &ProgressContext<'_>,
    session: &mut Session<'_>,
    offset: u64,
    copy: &CopyVersionedFunction<'_>,
    mut completed_sink: Option<&mut CompletionSink<'_>>,
) -> CopyResult {
    session.begin();
    // Small and large files are queued and limited separately ("lanes") when
    // the policy leaves the order to the scheduler; otherwise everything is
    // lane 0 and the single queue below behaves as it always has.
    let two_lanes = session.two_lanes();
    let backlog_limit = session.policy().backlog_limit();
    let order_by = session.policy().order_by;
    let channel_cap = backlog_limit.clamp(64, 4096);
    let max_hl_attempts = session.policy().high_level_retry_limit().max(1) as usize;
    let worker_cancel = Arc::new(AtomicBool::new(false));
    let retry_counts = [RetryCounts::default(), RetryCounts::default()];
    let mut result = CopyResult::new();
    let mut tasks = BTreeMap::<usize, FileTask>::new();
    let mut next_index = 0usize;
    let mut ready = [VecDeque::with_capacity(PRIME_COUNT), VecDeque::new()];
    // Bytes of finished files per lane; `result.bytes` stays the job's total.
    let mut lane_done = [0u64; 2];
    // Files copied per lane: the small-file lane is tuned on files per second.
    let mut lane_files = [0u64; 2];
    let mut producer_done = false;
    let (scan_tx, scan_rx) = mpsc::sync_channel::<Result<FileTask, String>>(channel_cap);
    let (event_tx, event_rx) = mpsc::sync_channel::<Message>(channel_cap);
    let need_scan_wake = Arc::new(AtomicBool::new(false));
    let operation_id = ctx.operation_id;
    let operation_type = ctx.operation_type;
    // Created before the scope: worker threads borrow the receiver for the
    // whole scope, so it must outlive it.
    let (work_tx, work_rx) =
        mpsc::sync_channel::<(usize, usize, FileTask, super::tuning::Policy)>(32);
    let work_rx = Mutex::new(work_rx);
    // One ownership record per destination folder, shared by the workers and
    // closed when the pass ends.
    let records = crate::transfer_journal::FolderRecords::new(operation_id);
    let records = &records;

    std::thread::scope(|scope| {
        {
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
        }

        // Gather a short window of work so the controller can classify the
        // workload, then start dispatching: never wait for a whole tree.
        let prime_deadline = Instant::now() + PRIME_WINDOW;
        while tasks.len() < PRIME_COUNT && !producer_done {
            let timeout = if tasks.is_empty() {
                // Nothing to classify yet: wait for the first task, but keep
                // checking for cancellation.
                Duration::from_millis(100)
            } else {
                prime_deadline.saturating_duration_since(Instant::now())
            };
            if !tasks.is_empty() && timeout.is_zero() {
                break;
            }
            match scan_rx.recv_timeout(timeout) {
                Ok(Ok(task)) => {
                    let idx = next_index;
                    next_index += 1;
                    let lane = lane_index(two_lanes, task.size);
                    tasks.insert(idx, task);
                    enqueue_ready(&mut ready[lane], &tasks, order_by, idx);
                }
                Ok(Err(error)) => {
                    result.scan_error = Some(error);
                    producer_done = true;
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {
                    if ctx.check_cancelled().is_err() {
                        break;
                    }
                    if tasks.is_empty() {
                        continue;
                    }
                    break;
                }
                Err(mpsc::RecvTimeoutError::Disconnected) => producer_done = true,
            }
            if ctx.check_cancelled().is_err() {
                break;
            }
        }
        if result.scan_error.is_some() || ctx.check_cancelled().is_err() {
            worker_cancel.store(true, Ordering::Relaxed);
            result.cancelled = ctx.check_cancelled().is_err();
            drop(scan_rx);
            return;
        }
        if tasks.is_empty() {
            drop(scan_rx);
            return;
        }
        #[cfg(test)]
        {
            result.retained_tasks_peak = tasks.len();
        }

        let all_network = tasks.values().all(|t| t.network);
        let buffer_size = session.policy().buffer_bytes(all_network);
        let mut lane_open = [false; 2];
        open_lanes(
            session,
            &mut lane_open,
            &ready,
            &tasks,
            buffer_size,
            &lane_done,
            &lane_files,
        );
        let mut active = BTreeMap::<usize, ActiveFile>::new();
        let mut deferred_retries = VecDeque::<(usize, Failure)>::new();
        // Bytes a deferred file had written when it failed. The failed attempt's
        // partial is removed and the retry copies the file again from the start.
        // The bytes are out of the readout while the file waits and return when
        // its retry starts, so the file's own line does not restart from zero.
        let mut carried = BTreeMap::<usize, u64>::new();
        let mut file_hl_attempts = BTreeMap::<usize, usize>::new();
        let mut first_snapshot = true;
        let mut workers_lost = false;
        // The file named in a running update when no file is in flight.
        let mut last_item = String::new();
        let mut last_display = Instant::now();
        let mut last_retries = [(0u64, 0u64); 2];
        if let Some(&first_idx) = ready.iter().filter_map(VecDeque::front).min() {
            if let Some(first) = tasks.get(&first_idx) {
                ctx.emit_now(
                    offset,
                    ctx.total_bytes.load(Ordering::Relaxed),
                    first.source.to_string_lossy().into(),
                    "running",
                    None,
                );
            }
        }

        // A worker thread is started when a file is sent and every thread
        // started so far has a file, up to MAX_WORKERS. A pass used to start
        // all thirty-two before its first file, whatever it went on to use:
        // one file at a time on a hard disk kept thirty-one threads waiting.
        let limiter = session.limiter.clone();
        let memory = session.memory.clone();
        let latency = [session.latency(0), session.latency(1)];
        let mut workers = Vec::new();
        // The task's count of bytes not transferred, shared with every file.
        let untransferred = ctx.untransferred;
        let spawn_worker = || {
            let work_rx = &work_rx;
            let tx = event_tx.clone();
            let cancel = &worker_cancel;
            let retries = &retry_counts;
            let limiter = limiter.clone();
            let memory = memory.clone();
            let latency = latency.clone();
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
                    let Ok((index, lane, task, policy)) = next_item else {
                        break;
                    };
                    // A panic while copying one file fails that file. Left to
                    // unwind, it ended this thread with the file in its hands:
                    // the coordinator stopped listening, the other threads
                    // blocked on reporting to it, and the pass never ended.
                    let attempt = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                        let total_bytes = Arc::new(AtomicU64::new(task.size));
                        let total_files = Arc::new(AtomicU64::new(1));
                        let completed_files = Arc::new(AtomicU64::new(0));
                        let emit = |update: ProgressUpdate| {
                            if completed_files.load(Ordering::Relaxed) > 0
                                && update.status == "running"
                            {
                                return;
                            }
                            // Byte counts are thinned to one in 50 ms. A change of
                            // state (finalizing, an error) is reported whenever it
                            // happens: a small file finishes within the 50 ms of
                            // its own first report, and this thread may have
                            // reported another file a moment ago.
                            let now = Instant::now();
                            if update.status == "running"
                                && update.error.is_none()
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
                            retry_count: Some(&retries[lane]),
                            rate_limit: Some(&limiter),
                            // A custom limit is never tuned, so nothing is timed.
                            write_latency: (policy.mode != super::tuning::Mode::Manual)
                                .then_some(&*latency[lane]),
                            untransferred,
                        };
                        let context = CopyContext {
                            progress: &progress,
                            operation_id,
                            records,
                            network: task.network,
                            policy: policy.clone(),
                        };
                        let mut copied = 0;
                        let permit_buffer = context
                            .buffer_size()
                            .min(usize::try_from(task.size.max(4096)).unwrap_or(usize::MAX));
                        // The drives are asked only for a file the settings
                        // would copy in several streams.
                        let several_streams = context
                            .policy
                            .multistream_candidate(task.network, task.size)
                            && simplefile_core::utils::is_local_fixed_volume(&task.source)
                            && simplefile_core::utils::is_local_fixed_volume(&task.destination);
                        let chunks =
                            context
                                .policy
                                .buffer_count(task.network, task.size, several_streams);
                        let status = Permit::acquire_for_job(
                            permit_buffer
                                .saturating_mul(chunks)
                                .saturating_add(128 * 1024),
                            memory.clone(),
                            cancel,
                        )
                        .map_err(Failure::from)
                        .and_then(|_permit| copy(&task, &context, &mut copied));
                        (copied, status)
                    }));
                    let (copied, status) = attempt
                        .unwrap_or_else(|panic| (0, Err(worker_panic_failure(&task, &*panic))));
                    if tx.send(Message::Finished(index, copied, status)).is_err() {
                        break;
                    }
                }
            })
        };

        let mut handle_event = |message: Message,
                                active: &mut BTreeMap<usize, ActiveFile>,
                                result: &mut CopyResult,
                                deferred_retries: &mut VecDeque<(usize, Failure)>,
                                file_hl_attempts: &mut BTreeMap<usize, usize>,
                                ready_empty: bool,
                                producer_done: bool,
                                tasks: &mut BTreeMap<usize, FileTask>,
                                lane_done: &mut [u64; 2],
                                lane_files: &mut [u64; 2],
                                carried: &mut BTreeMap<usize, u64>| {
            match message {
                Message::ScanAvailable => {}
                Message::Progress(index, update) => {
                    if let Some(file) = active.get_mut(&index) {
                        file.current = file.current.max(update.current);
                    }
                    // Only the last file's last steps are the task's: one
                    // file finishing while others are copied is not, and
                    // forwarded it showed the task as finishing.
                    if update.status == "finalizing"
                        && active.len() == 1
                        && producer_done
                        && ready_empty
                    {
                        let current = offset
                            .saturating_add(result.bytes)
                            .saturating_add(result.skipped_bytes)
                            .saturating_add(result.failed_bytes)
                            .saturating_add(carried.values().sum::<u64>())
                            .saturating_add(active.values().map(|f| f.current).sum::<u64>());
                        ctx.finalizing(current, update.current_item);
                    }
                }
                Message::Finished(index, bytes, status) => {
                    let finished = active.remove(&index);
                    let lane = finished
                        .as_ref()
                        .map(|file| lane_index(two_lanes, file.total));
                    let written = finished.map(|file| file.current).unwrap_or(0);
                    match status {
                        Ok(FileOutcome::Copied {
                            reused,
                            version,
                            destination,
                        }) => {
                            result.bytes = result.bytes.saturating_add(bytes);
                            if let Some(lane) = lane {
                                lane_done[lane] = lane_done[lane].saturating_add(bytes);
                                lane_files[lane] += 1;
                            }
                            let Some(task) = tasks.remove(&index) else {
                                return;
                            };
                            if let Some(sink) = completed_sink.as_deref_mut() {
                                if let Err(error) = sink(&task, &version, reused) {
                                    result.failed.push((
                                        index,
                                        TaskFailure {
                                            source: task.source.clone(),
                                            destination: destination.clone(),
                                            failure: Failure::from(error),
                                        },
                                    ));
                                }
                            }
                            if task.top_level {
                                if reused {
                                    result.reused.insert(index);
                                }
                                result.completed.push(index);
                                if destination != task.destination {
                                    result.destinations.insert(index, destination);
                                }
                                result.completed_tasks.insert(index, task);
                            }
                            file_hl_attempts.remove(&index);
                            ctx.completed_files.fetch_add(1, Ordering::Relaxed);
                        }
                        Ok(FileOutcome::Skipped) => {
                            if let Some(task) = tasks.remove(&index) {
                                ctx.not_transferred(task.size);
                                result.skipped_bytes =
                                    result.skipped_bytes.saturating_add(task.size);
                                result.skipped.push((index, task));
                            }
                            file_hl_attempts.remove(&index);
                            ctx.completed_files.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(error) => {
                            if error.is_cancelled() {
                                worker_cancel.store(true, Ordering::Relaxed);
                                return;
                            }
                            let attempts = file_hl_attempts.entry(index).or_insert(0);
                            *attempts += 1;
                            if *attempts < max_hl_attempts
                                && !worker_cancel.load(Ordering::Relaxed)
                                && error.kind.is_retryable()
                            {
                                carried.insert(index, written);
                                deferred_retries.push_back((index, error));
                            } else if let Some(task) = tasks.remove(&index) {
                                file_hl_attempts.remove(&index);
                                result.failed_bytes = result.failed_bytes.saturating_add(written);
                                result.failed.push((
                                    index,
                                    TaskFailure {
                                        source: task.source.clone(),
                                        destination: task.destination.clone(),
                                        failure: error,
                                    },
                                ));
                            }
                        }
                    }
                }
            }
        };

        while !producer_done
            || ready.iter().any(|queue| !queue.is_empty())
            || !active.is_empty()
            || (!worker_cancel.load(Ordering::Relaxed) && !deferred_retries.is_empty())
        {
            if ctx.check_cancelled().is_err() {
                worker_cancel.store(true, Ordering::Relaxed);
                producer_done = true;
            }
            while !producer_done
                && !worker_cancel.load(Ordering::Relaxed)
                && (ready[0].len() + ready[1].len())
                    .saturating_add(active.len())
                    .saturating_add(deferred_retries.len())
                    < backlog_limit
            {
                match scan_rx.try_recv() {
                    Ok(Ok(task)) => {
                        let idx = next_index;
                        next_index += 1;
                        let lane = lane_index(two_lanes, task.size);
                        tasks.insert(idx, task);
                        enqueue_ready(&mut ready[lane], &tasks, order_by, idx);
                    }
                    Ok(Err(error)) => {
                        result.scan_error = Some(error);
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
            #[cfg(test)]
            {
                result.retained_tasks_peak = result.retained_tasks_peak.max(tasks.len());
            }

            if (producer_done || deferred_retries.len() >= backlog_limit)
                && ready.iter().all(VecDeque::is_empty)
                && active.is_empty()
                && !worker_cancel.load(Ordering::Relaxed)
                && !deferred_retries.is_empty()
            {
                for (retry_index, _prev_err) in deferred_retries.drain(..) {
                    let lane = lane_index(two_lanes, tasks[&retry_index].size);
                    enqueue_ready(&mut ready[lane], &tasks, order_by, retry_index);
                }
            }
            open_lanes(
                session,
                &mut lane_open,
                &ready,
                &tasks,
                buffer_size,
                &lane_done,
                &lane_files,
            );

            // Each lane fills up to its own limit and the job as a whole to the
            // policy limit. Among lanes with room, the file that has waited
            // longest goes first, so neither lane can starve the other.
            let (mut lane_active, _) = lane_load(&active, two_lanes);
            while !worker_cancel.load(Ordering::Relaxed) && active.len() < session.total_limit() {
                let Some((_, lane)) = (0..2)
                    .filter(|&lane| lane_active[lane] < session.concurrency(lane))
                    .filter_map(|lane| ready[lane].front().map(|&index| (index, lane)))
                    .min()
                else {
                    break;
                };
                let Some(next_idx) = ready[lane].pop_front() else {
                    break;
                };
                lane_active[lane] += 1;
                let task = tasks[&next_idx].clone();
                active.insert(
                    next_idx,
                    ActiveFile {
                        path: task.source.to_string_lossy().into(),
                        current: carried.remove(&next_idx).unwrap_or(0),
                        total: task.size,
                    },
                );
                while workers.len() < active.len().min(MAX_WORKERS) {
                    workers.push(spawn_worker());
                }
                if work_tx
                    .send((next_idx, lane, task, session.policy().clone()))
                    .is_err()
                {
                    break;
                }
            }

            let room_for_more = if two_lanes {
                session.total_limit()
            } else {
                session.concurrency(0)
            };
            if !producer_done
                && !worker_cancel.load(Ordering::Relaxed)
                && ready.iter().all(VecDeque::is_empty)
                && active.len() < room_for_more
            {
                need_scan_wake.store(true, Ordering::Release);
                if let Ok(scanned) = scan_rx.try_recv() {
                    need_scan_wake.store(false, Ordering::Release);
                    match scanned {
                        Ok(task) => {
                            let idx = next_index;
                            next_index += 1;
                            let lane = lane_index(two_lanes, task.size);
                            tasks.insert(idx, task);
                            enqueue_ready(&mut ready[lane], &tasks, order_by, idx);
                            continue;
                        }
                        Err(error) => {
                            result.scan_error = Some(error);
                            worker_cancel.store(true, Ordering::Relaxed);
                            producer_done = true;
                        }
                    }
                }
            }
            if active.is_empty()
                && ((producer_done
                    && ready.iter().all(VecDeque::is_empty)
                    && deferred_retries.is_empty())
                    || worker_cancel.load(Ordering::Relaxed))
            {
                break;
            }
            if first_snapshot && !active.is_empty() {
                session.display(active.values().cloned().collect(), true);
                first_snapshot = false;
            }
            // A drained queue is not evidence that a lower setting is faster.
            let (lane_active, _) = lane_load(&active, two_lanes);
            let saturated = [0, 1].map(|lane| lane_active[lane] >= session.concurrency(lane));
            let demand = [0, 1].map(|lane| lane_active[lane] > 0 || !ready[lane].is_empty());
            let ready_empty = ready.iter().all(VecDeque::is_empty);
            match event_rx.recv_timeout(Duration::from_millis(50)) {
                Ok(first_msg) => {
                    handle_event(
                        first_msg,
                        &mut active,
                        &mut result,
                        &mut deferred_retries,
                        &mut file_hl_attempts,
                        ready_empty,
                        producer_done,
                        &mut tasks,
                        &mut lane_done,
                        &mut lane_files,
                        &mut carried,
                    );
                    while let Ok(msg) = event_rx.try_recv() {
                        handle_event(
                            msg,
                            &mut active,
                            &mut result,
                            &mut deferred_retries,
                            &mut file_hl_attempts,
                            ready_empty,
                            producer_done,
                            &mut tasks,
                            &mut lane_done,
                            &mut lane_files,
                            &mut carried,
                        );
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {
                    // A worker leaves its loop only when the work queue is
                    // closed, which happens after this loop: one that is gone
                    // now has died with a file in its hands.
                    if workers.iter().any(|worker| worker.is_finished()) {
                        result.scan_error = Some("Copy workers stopped unexpectedly".into());
                        workers_lost = true;
                        break;
                    }
                }
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    if !producer_done || !active.is_empty() {
                        result.scan_error = Some("Copy workers stopped unexpectedly".into());
                    }
                    break;
                }
            }
            // A file waiting for a later retry round keeps the bytes it had
            // written in the readout; they used to leave it until the retry
            // started, and the readout fell meanwhile.
            let current = offset
                .saturating_add(result.bytes)
                .saturating_add(result.skipped_bytes)
                .saturating_add(result.failed_bytes)
                .saturating_add(carried.values().sum::<u64>())
                .saturating_add(active.values().map(|f| f.current).sum::<u64>());
            let (_, lane_in_flight) = lane_load(&active, two_lanes);
            let reports = [0, 1].map(|lane| {
                let retries = (
                    retry_counts[lane].all.load(Ordering::Relaxed),
                    retry_counts[lane].files.load(Ordering::Relaxed),
                );
                let report = LaneReport {
                    bytes: lane_done[lane].saturating_add(lane_in_flight[lane]),
                    files: lane_files[lane],
                    saturated: saturated[lane],
                    demand: demand[lane],
                    retries: retries.0 - last_retries[lane].0,
                    retrying_files: retries.1 - last_retries[lane].1,
                };
                last_retries[lane] = retries;
                report
            });
            session.observe(reports);
            if let Some(file) = active.values().next() {
                last_item.clone_from(&file.path);
            }
            if last_display.elapsed() >= Duration::from_millis(200) || active.is_empty() {
                // With one file at a time nothing is in flight between two
                // files; the update names the file just done instead of none,
                // which the Transfers window showed as "Preparing transfer".
                ctx.emit(current, last_item.clone(), "running", None);
                // Not forced. With one file at a time nothing is in flight
                // between any two files, and a forced update for each of
                // them sent the Transfers window one event per copied file.
                // Nor sent with no file: an empty list took every row out of
                // Details between two files. The update after the loop shows
                // the final state.
                if !active.is_empty() {
                    session.display(active.values().cloned().collect(), false);
                }
                last_display = Instant::now();
            }
        }
        drop(scan_rx);
        drop(work_tx);
        if workers_lost {
            // The loop above no longer reads what the remaining threads
            // report, and a full channel would hold them, and with them the
            // end of this scope, for good. They are told to stop, and their
            // reports are read and dropped until the last one has ended.
            worker_cancel.store(true, Ordering::Relaxed);
            while workers.iter().any(|worker| !worker.is_finished()) {
                let _ = event_rx.recv_timeout(Duration::from_millis(50));
            }
        }
        if ctx.check_cancelled().is_err() {
            result.cancelled = true;
        }
    });
    session.observe([0, 1].map(|lane| LaneReport {
        bytes: lane_done[lane],
        files: lane_files[lane],
        ..LaneReport::default()
    }));
    session.display(Vec::new(), true);
    result
}

#[cfg(test)]
mod tests {
    use super::super::tuning::{CheckMode, Mode, OrderBy, Policy};
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn queued_task(name: &str, size: u64) -> FileTask {
        FileTask {
            source: PathBuf::from("src").join(name),
            destination: PathBuf::from("dst").join(name),
            size,
            network: false,
            conflict: ConflictAction::Error,
            symlink: false,
            top_level: false,
            merge_root: None,
            delete_source: false,
            retry: false,
            in_new_folder: false,
        }
    }

    /// A file put in its place one at a time leaves the queue exactly as a
    /// sort of the whole queue would.
    #[test]
    fn a_file_is_queued_at_its_place_in_the_chosen_order() {
        let files = [
            ("m.bin", 5u64),
            ("a.bin", 9),
            ("z.bin", 1),
            ("b.bin", 5),
            ("k.bin", 7),
            ("a2.bin", 5),
            ("c.bin", 0),
        ];
        let tasks: BTreeMap<usize, FileTask> = files
            .iter()
            .enumerate()
            .map(|(index, (name, size))| (index, queued_task(name, *size)))
            .collect();
        for order_by in [
            OrderBy::Default,
            OrderBy::SizeAsc,
            OrderBy::SizeDesc,
            OrderBy::Name,
        ] {
            for arrival in [
                vec![0, 1, 2, 3, 4, 5, 6],
                vec![6, 5, 4, 3, 2, 1, 0],
                vec![3, 0, 6, 1, 5, 2, 4],
            ] {
                let mut queue = VecDeque::new();
                for &index in &arrival {
                    enqueue_ready(&mut queue, &tasks, order_by, index);
                }
                let mut sorted = arrival.clone();
                if !matches!(order_by, OrderBy::Default) {
                    sorted.sort_by(|&a, &b| queue_order(&tasks, order_by, a, b));
                }
                assert_eq!(
                    queue.iter().copied().collect::<Vec<_>>(),
                    sorted,
                    "{order_by:?}, arrival {arrival:?}"
                );
            }
        }
        // Spot checks of what the orders mean.
        let ordered = |order_by| {
            let mut queue = VecDeque::new();
            for index in 0..files.len() {
                enqueue_ready(&mut queue, &tasks, order_by, index);
            }
            queue
                .iter()
                .map(|index| files[*index].0)
                .collect::<Vec<_>>()
        };
        assert_eq!(ordered(OrderBy::SizeAsc)[0], "c.bin");
        assert_eq!(ordered(OrderBy::SizeDesc)[0], "a.bin");
        assert_eq!(
            ordered(OrderBy::Name),
            ["a.bin", "a2.bin", "b.bin", "c.bin", "k.bin", "m.bin", "z.bin"]
        );
    }

    /// Every created folder gets its source's times, on one thread or several.
    #[test]
    fn created_folders_get_their_source_times_on_one_thread_or_several() {
        let root = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
        for (folders, threads) in [(5usize, 8usize), (300, 8)] {
            let mut created = Vec::new();
            for index in 0..folders {
                let src = root.join(format!("src-{folders}-{index}"));
                fs::create_dir_all(&src).unwrap();
                created.push((index, 0usize, src, PathBuf::new()));
            }
            // The destinations are made later, so their own times differ.
            std::thread::sleep(Duration::from_millis(40));
            for (index, _, _, dst) in &mut created {
                *dst = root.join(format!("dst-{folders}-{index}"));
                fs::create_dir_all(&dst).unwrap();
            }
            let modified = |path: &Path| fs::metadata(path).unwrap().modified().unwrap();
            assert_ne!(modified(&created[0].2), modified(&created[0].3));

            restore_directory_metadata(&created, threads);

            for (_, _, src, dst) in &created {
                assert_eq!(modified(dst), modified(src), "{}", dst.display());
            }
        }
        fs::remove_dir_all(root).unwrap();
    }

    /// The threads a pass starts follow the files it runs at once.
    #[test]
    fn a_pass_starts_only_the_worker_threads_it_uses() {
        for (limit, files) in [(1usize, 120usize), (3, 120)] {
            let root = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
            fs::create_dir(&root).unwrap();
            let template = root.join("source");
            fs::write(&template, b"source").unwrap();
            let version =
                crate::transfer_staging::capture_source_version_for_path(&template).unwrap();
            let policy = || Policy {
                mode: Mode::Manual,
                manual_concurrency: limit,
                ..Policy::default()
            };
            let cancel = Arc::new(AtomicBool::new(false));
            let total = Arc::new(AtomicU64::new(files as u64));
            let file_count = Arc::new(AtomicU64::new(files as u64));
            let completed = Arc::new(AtomicU64::new(0));
            let ctx = ProgressContext {
                emit: &|_| {},
                operation_id: "lazy-workers-test",
                operation_type: "copy",
                cancel: &cancel,
                total_bytes: &total,
                total_files: &file_count,
                completed_files: &completed,
                last_running_emit: Mutex::new(Instant::now()),
                retry_count: None,
                rate_limit: None,
                write_latency: None,
                untransferred: None,
            };
            let mut session = Session::new(
                "lazy-workers-test",
                "copy",
                &[],
                &root.to_string_lossy(),
                &policy,
                &|_| {},
            );
            let mut index = 0;
            let mut next = || {
                if index == files {
                    return Ok(None);
                }
                index += 1;
                Ok(Some(FileTask {
                    source: root.join(index.to_string()),
                    destination: root.join(format!("dest-{index}")),
                    size: 1,
                    network: false,
                    conflict: ConflictAction::Error,
                    symlink: false,
                    top_level: false,
                    merge_root: None,
                    delete_source: false,
                    retry: false,
                    in_new_folder: false,
                }))
            };
            let threads = Mutex::new(std::collections::HashSet::new());
            let copy = |task: &FileTask, _: &CopyContext<'_>, _: &mut u64| {
                threads.lock().unwrap().insert(std::thread::current().id());
                // Long enough that an idle thread, had one been started,
                // would have taken the next file.
                std::thread::sleep(Duration::from_millis(1));
                Ok(FileOutcome::Copied {
                    reused: false,
                    version: version.clone(),
                    destination: task.destination.clone(),
                })
            };

            let result =
                copy_files_streaming_with_sink(&mut next, &ctx, &mut session, 0, &copy, None);

            assert!(result.failed.is_empty(), "{:?}", result.failed);
            assert_eq!(completed.load(Ordering::Relaxed), files as u64);
            let used = threads.lock().unwrap().len();
            assert!(
                (1..=limit).contains(&used),
                "{used} worker threads ran files with a limit of {limit}"
            );
            fs::remove_dir_all(root).unwrap();
        }
    }

    /// A worker thins its byte counts to one in 50 ms, but a file's state
    /// changes are reported as they happen. With one worker thread, a small
    /// file finishes within 50 ms of the thread's last report, and its
    /// finalizing used to be dropped with the byte counts; a move whose
    /// source is deleted right after publication then showed no finalizing
    /// for the file at all.
    #[test]
    fn a_files_finalizing_is_reported_whatever_the_thread_reported_just_before() {
        let files = 3usize;
        let root = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
        fs::create_dir(&root).unwrap();
        let template = root.join("source");
        fs::write(&template, b"source").unwrap();
        let version = crate::transfer_staging::capture_source_version_for_path(&template).unwrap();
        let policy = || Policy {
            mode: Mode::Manual,
            manual_concurrency: 1,
            ..Policy::default()
        };
        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(files as u64));
        let file_count = Arc::new(AtomicU64::new(files as u64));
        let completed = Arc::new(AtomicU64::new(0));
        let seen = Mutex::new(Vec::<(String, String)>::new());
        let ctx = ProgressContext {
            emit: &|update| {
                seen.lock()
                    .unwrap()
                    .push((update.status.clone(), update.current_item.clone()));
            },
            operation_id: "finalizing-report-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &file_count,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
            untransferred: None,
        };
        let mut session = Session::new(
            "finalizing-report-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &|_| {},
        );
        let mut index = 0;
        let mut next = || {
            if index == files {
                return Ok(None);
            }
            index += 1;
            Ok(Some(FileTask {
                source: root.join(index.to_string()),
                destination: root.join(format!("dest-{index}")),
                size: 1,
                network: false,
                conflict: ConflictAction::Error,
                symlink: false,
                top_level: false,
                merge_root: None,
                delete_source: false,
                retry: false,
                in_new_folder: false,
            }))
        };
        let copy = |task: &FileTask, context: &CopyContext<'_>, _: &mut u64| {
            let item = task.source.to_string_lossy().to_string();
            context.progress.emit(0, item.clone(), "running", None);
            context.progress.finalizing(1, item);
            Ok(FileOutcome::Copied {
                reused: false,
                version: version.clone(),
                destination: task.destination.clone(),
            })
        };

        let result = copy_files_streaming_with_sink(&mut next, &ctx, &mut session, 0, &copy, None);

        assert!(result.failed.is_empty(), "{:?}", result.failed);
        assert_eq!(completed.load(Ordering::Relaxed), files as u64);
        let last = root.join(files.to_string()).to_string_lossy().to_string();
        let seen = seen.lock().unwrap();
        assert!(
            seen.iter()
                .any(|(status, item)| status == "finalizing" && *item == last),
            "no finalizing for the last file in {seen:?}"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn streaming_completion_is_bounded_and_new_files_receive_current_policy() {
        let root = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
        fs::create_dir(&root).unwrap();
        let template = root.join("source");
        fs::write(&template, b"source").unwrap();
        let version = crate::transfer_staging::capture_source_version_for_path(&template).unwrap();
        let changed = AtomicBool::new(false);
        let policy = || Policy {
            mode: Mode::Manual,
            manual_concurrency: 1,
            max_backlog: 128,
            check_mode: if changed.load(Ordering::Relaxed) {
                CheckMode::Checksum
            } else {
                CheckMode::SizeOnly
            },
            flush_on_close: changed.load(Ordering::Relaxed),
            ..Policy::default()
        };
        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(1000));
        let files = Arc::new(AtomicU64::new(1000));
        let completed = Arc::new(AtomicU64::new(0));
        let ctx = ProgressContext {
            emit: &|_| {},
            operation_id: "bounded-policy-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
            untransferred: None,
        };
        let mut session = Session::new(
            "bounded-policy-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &|_| {},
        );
        let mut index = 0;
        let mut next = || {
            if index == 1000 {
                return Ok(None);
            }
            index += 1;
            Ok(Some(FileTask {
                source: root.join(index.to_string()),
                destination: root.join(format!("dest-{index}")),
                size: 1,
                network: false,
                conflict: ConflictAction::Error,
                symlink: false,
                top_level: false,
                merge_root: None,
                delete_source: false,
                retry: false,
                in_new_folder: false,
            }))
        };
        let mut count = 0;
        let mut sink = |_: &FileTask, _: &SourceVersion, _: bool| {
            count += 1;
            Ok(())
        };
        let copy = |task: &FileTask, context: &CopyContext<'_>, _: &mut u64| {
            if changed.swap(true, Ordering::Relaxed) {
                assert_eq!(context.policy.check_mode, CheckMode::Checksum);
                assert!(context.policy.flush_on_close);
            } else {
                assert_eq!(context.policy.check_mode, CheckMode::SizeOnly);
                // A retry notification exercises the same live-refresh path as
                // a five-second observation without a wall-clock test delay.
                let counts = context.progress.retry_count.unwrap();
                counts.all.fetch_add(1, Ordering::Relaxed);
                counts.files.fetch_add(1, Ordering::Relaxed);
            }
            Ok(FileOutcome::Copied {
                reused: false,
                version: version.clone(),
                destination: task.destination.clone(),
            })
        };
        let result = copy_files_streaming_with_sink(
            &mut next,
            &ctx,
            &mut session,
            0,
            &copy,
            Some(&mut sink),
        );
        assert!(result.failed.is_empty(), "{:?}", result.failed);
        assert_eq!(count, 1000);
        assert!(result.completed.is_empty());
        assert!(
            result.retained_tasks_peak <= 160,
            "{} retained tasks",
            result.retained_tasks_peak
        );
        fs::remove_dir_all(root).unwrap();
    }

    /// Files that fail after their last byte (a refused rename into place)
    /// keep their bytes in the readout: the bar never runs backwards, and the
    /// result says how much was written.
    #[test]
    fn late_failures_never_move_the_readout_backwards() {
        let root = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
        fs::create_dir(&root).unwrap();
        const SIZE: u64 = 65536;
        let tasks: Vec<_> = (0..4)
            .map(|index| FileTask {
                source: root.join(format!("{index}.bin")),
                destination: root.join(format!("dest-{index}.bin")),
                size: SIZE,
                network: false,
                conflict: ConflictAction::Error,
                symlink: false,
                top_level: true,
                merge_root: None,
                delete_source: false,
                retry: false,
                in_new_folder: false,
            })
            .collect();
        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(4 * SIZE));
        let files = Arc::new(AtomicU64::new(4));
        let completed = Arc::new(AtomicU64::new(0));
        let updates = Mutex::new(Vec::<u64>::new());
        let ctx = ProgressContext {
            emit: &|update: ProgressUpdate| updates.lock().unwrap().push(update.current),
            operation_id: "late-failure-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now() - Duration::from_secs(1)),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
            untransferred: None,
        };
        let policy = || Policy {
            mode: Mode::Manual,
            manual_concurrency: 2,
            ..Policy::default()
        };
        let mut session = Session::new(
            "late-failure-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &|_| {},
        );
        let copy = |task: &FileTask, context: &CopyContext<'_>, _: &mut u64| {
            // Past the worker's 50 ms emit throttle, so every file's full
            // progress reaches the scheduler before the file fails.
            std::thread::sleep(Duration::from_millis(60));
            context.progress.emit_now(
                task.size,
                task.size,
                task.source.to_string_lossy().into(),
                "running",
                None,
            );
            std::thread::sleep(Duration::from_millis(60));
            Err(format!(
                "Finalize destination [{}]: refused",
                task.destination.display()
            ))
        };
        let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);

        assert_eq!(result.failed.len(), 4);
        assert_eq!(result.failed_bytes, 4 * SIZE);
        let updates = updates.lock().unwrap();
        assert!(
            updates.windows(2).all(|pair| pair[0] <= pair[1]),
            "readout ran backwards: {updates:?}"
        );
        assert!(updates.iter().any(|&current| current > 0), "{updates:?}");
        fs::remove_dir_all(root).unwrap();
    }

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
                    conflict: ConflictAction::Error,
                    symlink: false,
                    top_level: true,
                    merge_root: None,
                    delete_source: false,
                    retry: false,
                    in_new_folder: false,
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
            write_latency: None,
            untransferred: None,
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
                .map(|_| ())
                .map_err(String::from)
        };
        let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);
        if cancel_during_copy {
            assert_eq!(result.first_failure().unwrap().1, "Operation cancelled");
            assert_eq!(started.load(Ordering::SeqCst), 3);
            assert!(result.completed.is_empty());
            assert_eq!(fs::read_dir(root.join("dst")).unwrap().count(), 0);
        } else {
            assert!(result.failed.is_empty(), "{:?}", result.failed);
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
                        conflict: ConflictAction::Error,
                        symlink: false,
                        top_level: true,
                        merge_root: None,
                        delete_source: false,
                        retry: false,
                        in_new_folder: false,
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
                write_latency: None,
                untransferred: None,
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
            assert!(result.failed.is_empty(), "{:?}", result.failed);
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
                    conflict: ConflictAction::Error,
                    symlink: false,
                    top_level: true,
                    merge_root: None,
                    delete_source: false,
                    retry: false,
                    in_new_folder: false,
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
            write_latency: None,
            untransferred: None,
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

        let snapshot =
            crate::transfer_staging::capture_source_version_for_path(&tasks[1].source).unwrap();
        let copy = move |task: &FileTask, _context: &CopyContext<'_>, bytes: &mut u64| {
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
            // This regression tests scheduling independence from a straggler.
            // Disk flush throughput is covered by the actual-copy tests.
            *bytes += task.size;
            if let Some(stem) = task.source.file_stem().and_then(|s| s.to_str()) {
                if let Ok(idx) = stem.parse::<usize>() {
                    if idx >= 128 {
                        later_files_for_worker.fetch_add(1, Ordering::SeqCst);
                    }
                }
            }
            Ok(FileOutcome::Copied {
                reused: false,
                version: snapshot.clone(),
                destination: task.destination.clone(),
            })
        };

        let mut iter = tasks.iter().cloned();
        let result =
            copy_files_streaming_using(&mut || Ok(iter.next()), &ctx, &mut session, 0, &copy);
        assert!(result.failed.is_empty(), "{:?}", result.failed);
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
            write_latency: None,
            untransferred: None,
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
            records: &crate::transfer_journal::FolderRecords::new("small-tree-test"),
            network: false,
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

        // A copy records nothing to delete afterwards.
        assert!(manifest.files.is_empty());
        assert!(manifest.source_dirs.is_empty());
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
                    conflict: ConflictAction::Error,
                    symlink: false,
                    top_level: true,
                    merge_root: None,
                    delete_source: false,
                    retry: false,
                    in_new_folder: false,
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
            write_latency: None,
            untransferred: None,
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
                        return Err("RETRYABLE: Read source [2.bin]: timed out".into());
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
        assert!(result.failed.is_empty(), "{:?}", result.failed);
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
                        conflict: ConflictAction::Error,
                        symlink: false,
                        top_level: true,
                        merge_root: None,
                        delete_source: false,
                        retry: false,
                        in_new_folder: false,
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
                write_latency: None,
                untransferred: None,
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
                copy_task(task, context, bytes)
                    .map(|_| ())
                    .map_err(String::from)
            };
            let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);
            assert!(result.failed.is_empty(), "{:?}", result.failed);
            assert_eq!(*dispatched_sizes.lock().unwrap(), expected_sizes);
        }

        fs::remove_dir_all(&root).unwrap();
    }

    /// A panic while one file is copied used to end its worker thread with
    /// the file in its hands. The pass then stopped listening to the other
    /// threads, which blocked for good, or it ended by a panic of its own.
    #[test]
    fn a_panic_while_copying_one_file_fails_that_file_and_the_rest_is_copied() {
        let root = std::env::temp_dir().join(format!(
            "sumafile_worker_panic_{}",
            crate::progress::generate_transfer_operation_id()
        ));
        fs::create_dir_all(root.join("src")).unwrap();
        fs::create_dir_all(root.join("dst")).unwrap();
        let tasks: Vec<_> = (0..12)
            .map(|index| {
                let source = root.join("src").join(format!("{index}.bin"));
                fs::write(&source, vec![index as u8; 4096]).unwrap();
                FileTask {
                    source,
                    destination: root.join("dst").join(format!("{index}.bin")),
                    size: 4096,
                    network: false,
                    conflict: ConflictAction::Error,
                    symlink: false,
                    top_level: true,
                    merge_root: None,
                    delete_source: false,
                    retry: false,
                    in_new_folder: false,
                }
            })
            .collect();
        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(12 * 4096));
        let files = Arc::new(AtomicU64::new(12));
        let completed = Arc::new(AtomicU64::new(0));
        let ctx = ProgressContext {
            emit: &|_| {},
            operation_id: "worker-panic-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
            untransferred: None,
        };
        let policy = || Policy {
            mode: Mode::Manual,
            manual_concurrency: 4,
            ..Policy::default()
        };
        let mut session = Session::new(
            "worker-panic-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &|_| {},
        );
        let copy = |task: &FileTask, context: &CopyContext<'_>, bytes: &mut u64| {
            if task.source.ends_with("5.bin") {
                panic!("planted for the test");
            }
            copy_task(task, context, bytes)
                .map(|_| ())
                .map_err(String::from)
        };

        let result = copy_files_using(&tasks, &ctx, &mut session, 0, &copy);

        assert_eq!(result.failed.len(), 1, "{:?}", result.failed);
        let (_, failure) = &result.failed[0];
        assert!(failure.source.ends_with("5.bin"), "{failure:?}");
        assert!(
            failure.failure.message.contains("stopped unexpectedly")
                && failure.failure.message.contains("planted for the test"),
            "{failure:?}"
        );
        assert!(result.scan_error.is_none(), "{:?}", result.scan_error);
        assert_eq!(result.completed.len(), 11);
        for task in tasks.iter().filter(|task| !task.source.ends_with("5.bin")) {
            assert_eq!(
                fs::read(&task.source).unwrap(),
                fs::read(&task.destination).unwrap()
            );
        }
        fs::remove_dir_all(&root).unwrap();
    }

    /// A retried move that finds a file at its destination deletes the
    /// source when that file counts as the copy. With "Size only", or with
    /// size and time, a different file counted, and the move deleted the
    /// source of a file it had never copied.
    #[test]
    fn a_retried_move_keeps_its_source_when_a_different_file_is_at_the_destination() {
        let root = std::env::temp_dir().join(format!(
            "sumafile_retried_move_{}",
            crate::progress::generate_transfer_operation_id()
        ));
        fs::create_dir_all(root.join("src")).unwrap();
        fs::create_dir_all(root.join("dst")).unwrap();
        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(3));
        let files = Arc::new(AtomicU64::new(1));
        let completed = Arc::new(AtomicU64::new(0));
        let progress = ProgressContext {
            emit: &|_| {},
            operation_id: "retried-move-test",
            operation_type: "move",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
            untransferred: None,
        };
        let ctx = CopyContext {
            progress: &progress,
            operation_id: "retried-move-test",
            records: &crate::transfer_journal::FolderRecords::new("retried-move-test"),
            network: false,
            policy: Policy {
                check_mode: CheckMode::SizeOnly,
                ..Policy::default()
            },
        };
        let task = |name: &str| FileTask {
            source: root.join("src").join(name),
            destination: root.join("dst").join(name),
            size: 3,
            network: false,
            conflict: ConflictAction::Error,
            symlink: false,
            top_level: true,
            merge_root: None,
            delete_source: true,
            retry: true,
            in_new_folder: false,
        };

        // Same size, different bytes: not the copy.
        let different = task("different.txt");
        fs::write(&different.source, b"NEW").unwrap();
        fs::write(&different.destination, b"OLD").unwrap();
        let mut bytes = 0u64;
        let error = copy_task(&different, &ctx, &mut bytes)
            .expect_err("a different file at the destination is a conflict");
        assert!(error.is_conflict(), "{error}");
        assert_eq!(fs::read(&different.source).unwrap(), b"NEW");
        assert_eq!(fs::read(&different.destination).unwrap(), b"OLD");

        // The same bytes: the earlier pass copied it, and only the source is left to delete.
        let same = task("same.txt");
        fs::write(&same.source, b"NEW").unwrap();
        fs::write(&same.destination, b"NEW").unwrap();
        let outcome = copy_task(&same, &ctx, &mut bytes).expect("the copy is already there");
        assert!(matches!(outcome, FileOutcome::Copied { reused: true, .. }));
        assert!(!same.source.exists());
        assert_eq!(fs::read(&same.destination).unwrap(), b"NEW");

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
            write_latency: None,
            untransferred: None,
        };
        let ctx = CopyContext {
            progress: &progress,
            operation_id: "overlapped-multistream-test",
            records: &crate::transfer_journal::FolderRecords::new("overlapped-multistream-test"),
            network: true, // 512 KiB buffer -> triggers 3-slot overlapped ring buffer on 2 MiB file
            policy: Policy::default(),
        };
        let mut completed_bytes = 0u64;
        let outcome = super::super::execute::copy_file_with_progress_versioned(
            &src,
            &dst_overlapped,
            &ctx,
            &mut completed_bytes,
            ConflictAction::Error,
            false,
        )
        .expect("overlapped ring-buffer copy should succeed");
        let FileOutcome::Copied { version, .. } = outcome else {
            panic!("overlapped copy must publish the file");
        };
        // Default integrity mode relies on size, timestamps and file identity.
        assert!(version.content_hash.is_none());
        assert_eq!(fs::read(&dst_overlapped).unwrap(), payload);

        let mut dst_file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&dst_multistream)
            .unwrap();
        let copied = super::super::execute::copy_file_multistream(
            &src,
            &dst_multistream,
            &mut dst_file,
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
        for _ in 0..16 {
            let mut broken_destination = fs::OpenOptions::new()
                .write(true)
                .open(&dst_multistream)
                .unwrap();
            let error = super::super::execute::copy_file_multistream(
                &src,
                &dst_multistream,
                &mut broken_destination,
                &ctx,
                0,
                payload.len() as u64 * 2,
                64 * 1024,
                16,
            )
            .unwrap_err();
            assert!(
                error.message.contains("Source became shorter"),
                "{}",
                error.message
            );
            assert!(!cancel.load(Ordering::Relaxed));
        }

        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn skipped_files_count_toward_progress_bytes_without_changing_copied_bytes() {
        let root = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
        fs::create_dir(&root).unwrap();
        const SIZE: u64 = 65536;
        let template = root.join("template");
        fs::write(&template, vec![1u8; SIZE as usize]).unwrap();
        let version = crate::transfer_staging::capture_source_version_for_path(&template).unwrap();

        let tasks = vec![
            FileTask {
                source: root.join("skipped.bin"),
                destination: root.join("dst_skipped.bin"),
                size: SIZE,
                network: false,
                conflict: ConflictAction::Skip,
                symlink: false,
                top_level: true,
                merge_root: None,
                delete_source: false,
                retry: false,
                in_new_folder: false,
            },
            FileTask {
                source: root.join("copied.bin"),
                destination: root.join("dst_copied.bin"),
                size: SIZE,
                network: false,
                conflict: ConflictAction::Skip,
                symlink: false,
                top_level: true,
                merge_root: None,
                delete_source: false,
                retry: false,
                in_new_folder: false,
            },
        ];

        let cancel = Arc::new(AtomicBool::new(false));
        let total = Arc::new(AtomicU64::new(SIZE * 2));
        let files = Arc::new(AtomicU64::new(2));
        let completed = Arc::new(AtomicU64::new(0));
        let updates = Mutex::new(Vec::<u64>::new());
        let ctx = ProgressContext {
            emit: &|update: ProgressUpdate| updates.lock().unwrap().push(update.current),
            operation_id: "skipped-progress-test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total,
            total_files: &files,
            completed_files: &completed,
            last_running_emit: Mutex::new(
                Instant::now()
                    .checked_sub(Duration::from_secs(1))
                    .unwrap_or_else(Instant::now),
            ),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
            untransferred: None,
        };
        let policy = || Policy {
            mode: Mode::Manual,
            manual_concurrency: 1,
            ..Policy::default()
        };
        let mut session = Session::new(
            "skipped-progress-test",
            "copy",
            &[],
            &root.to_string_lossy(),
            &policy,
            &|_| {},
        );
        let mut iter = tasks.into_iter();
        let copy = |task: &FileTask, _context: &CopyContext<'_>, copied: &mut u64| {
            if task.source.ends_with("skipped.bin") {
                Ok(FileOutcome::Skipped)
            } else {
                std::thread::sleep(Duration::from_millis(100));
                *copied = SIZE;
                Ok(FileOutcome::Copied {
                    reused: false,
                    version: version.clone(),
                    destination: task.destination.clone(),
                })
            }
        };
        let result =
            copy_files_streaming_using(&mut || Ok(iter.next()), &ctx, &mut session, 0, &copy);

        assert_eq!(result.bytes, SIZE);
        assert_eq!(result.skipped_bytes, SIZE);
        assert_eq!(result.skipped.len(), 1);
        assert_eq!(completed.load(Ordering::Relaxed), 2);
        let updates = updates.into_inner().unwrap();
        assert!(
            updates.contains(&SIZE) && updates.contains(&(SIZE * 2)),
            "emitted progress must include skipped bytes and final total: {updates:?}"
        );

        fs::remove_dir_all(&root).unwrap();
    }
}
