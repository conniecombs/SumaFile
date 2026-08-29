//! Progress tracking and cancellation for bulk file transfers.

use serde::Serialize;
use simplefile_core::models::ProgressUpdate;
use simplefile_core::path_conflict::{
    create_dir_exclusive, is_keep_both_action, path_collision_key, path_exists_no_follow,
    paths_refer_to_same_entry,
};
use simplefile_core::utils::{
    generate_operation_id, recreate_symlink, validate_existing_path_no_resolve,
    validate_path_no_follow,
};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::io::{BufReader, BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex};
use std::time::{Duration, Instant};
use tokio::sync::Mutex;

const NETWORK_BUFFER_SIZE: usize = 1024 * 1024;
const LOCAL_BUFFER_SIZE: usize = 1024 * 1024;
const NETWORK_MAX_RETRIES: u32 = 3;
const PROGRESS_BYTE_STEP: u64 = 4 * 1024 * 1024;
const PROGRESS_MIN_INTERVAL: Duration = Duration::from_millis(80);

#[derive(Debug, Serialize, Clone)]
pub struct TransferResult {
    pub source: String,
    pub destination: String,
}

#[derive(Default)]
pub struct OperationRegistry {
    operations: Mutex<HashMap<String, Arc<AtomicBool>>>,
}

impl OperationRegistry {
    pub async fn register(&self, operation_id: &str) -> Arc<AtomicBool> {
        let cancel = Arc::new(AtomicBool::new(false));
        self.operations
            .lock()
            .await
            .insert(operation_id.to_string(), cancel.clone());
        cancel
    }

    pub async fn cancel(&self, operation_id: &str) -> bool {
        if let Some(cancel) = self.operations.lock().await.get(operation_id) {
            cancel.store(true, Ordering::Relaxed);
            true
        } else {
            false
        }
    }

    pub async fn remove(&self, operation_id: &str) {
        self.operations.lock().await.remove(operation_id);
    }
}

#[derive(Debug)]
struct TransferPlan {
    source_path: PathBuf,
    final_dest: PathBuf,
    replace_existing: bool,
    allow_rename: bool,
    file_count: u64,
}

#[derive(Default)]
struct TransferEstimate {
    bytes: u64,
    files: u64,
}

fn ensure_not_copying_dir_into_itself(source_path: &Path, final_dest: &Path) -> Result<(), String> {
    let source_meta = fs::symlink_metadata(source_path)
        .map_err(|error| format!("Failed to stat source: {error}"))?;
    if !source_meta.file_type().is_dir() {
        return Ok(());
    }

    if let Ok(canonical_source) = source_path.canonicalize() {
        let canonical_dest = final_dest
            .parent()
            .and_then(|parent| parent.canonicalize().ok())
            .map(|parent| parent.join(final_dest.file_name().unwrap_or_default()));
        if let Some(canonical_dest) = canonical_dest {
            if canonical_dest.starts_with(&canonical_source) {
                return Err(
                    "Cannot copy or move a directory into itself or one of its subdirectories"
                        .to_string(),
                );
            }
        }
    }
    Ok(())
}

fn unique_destination_path(
    dest_dir: &Path,
    file_name: &std::ffi::OsStr,
    planned_destinations: &HashSet<String>,
) -> Result<PathBuf, String> {
    let original = Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |stem| stem.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|extension| extension.to_string_lossy().to_string());

    for index in 1..10_000u32 {
        let candidate_name = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({index}).{ext}"),
            _ => format!("{stem} ({index})"),
        };
        let candidate = dest_dir.join(candidate_name);
        let candidate_key = path_collision_key(&candidate);
        if !path_exists_no_follow(&candidate) && !planned_destinations.contains(&candidate_key) {
            return Ok(candidate);
        }
    }

    Err(format!(
        "Could not choose a unique destination for {}",
        original.to_string_lossy()
    ))
}

fn conflict_for_existing_destination(path: &Path) -> String {
    format!(
        "CONFLICT: destination already exists: {}",
        path.to_string_lossy()
    )
}

fn resolve_destination(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
    planned_destinations: &HashSet<String>,
) -> Result<Option<(PathBuf, bool)>, String> {
    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_dir.join(file_name);
    let final_key = path_collision_key(&final_dest);
    let exists = path_exists_no_follow(&final_dest);
    let planned_conflict = planned_destinations.contains(&final_key);

    if !exists && !planned_conflict {
        return Ok(Some((final_dest, false)));
    }

    match conflict_action.to_ascii_lowercase().as_str() {
        "skip" => Ok(None),
        "replace" => {
            if planned_conflict {
                return Err(format!(
                    "CONFLICT: multiple sources would replace the same destination: {}",
                    final_dest.to_string_lossy()
                ));
            }
            if exists && paths_refer_to_same_entry(source_path, &final_dest) {
                return Ok(None);
            }
            Ok(Some((final_dest, exists)))
        }
        "rename" | "keep-both" | "keep_both" => Ok(Some((
            unique_destination_path(dest_dir, file_name, planned_destinations)?,
            false,
        ))),
        _ => Err(conflict_for_existing_destination(&final_dest)),
    }
}

fn prepare_transfer_inputs(
    sources: Vec<String>,
    destination: String,
    conflict_action: &str,
) -> Result<(Vec<TransferPlan>, PathBuf), String> {
    if sources.is_empty() {
        return Err("No sources specified".to_string());
    }

    let dest_path = validate_existing_path_no_resolve(&destination)?;
    if !dest_path.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }

    let mut plans = Vec::with_capacity(sources.len());
    let mut planned_destinations = HashSet::new();
    let keep_both = is_keep_both_action(conflict_action);

    for source in sources {
        let source_path = validate_path_no_follow(&source)?;
        let Some((final_dest, replace_existing)) = resolve_destination(
            &source_path,
            &dest_path,
            conflict_action,
            &planned_destinations,
        )?
        else {
            continue;
        };

        ensure_not_copying_dir_into_itself(&source_path, &final_dest)?;
        planned_destinations.insert(path_collision_key(&final_dest));
        plans.push(TransferPlan {
            source_path,
            final_dest,
            replace_existing,
            allow_rename: !keep_both,
            file_count: 0,
        });
    }

    Ok((plans, dest_path))
}

fn remove_path(path: &Path, label: &str) -> Result<(), String> {
    let meta =
        fs::symlink_metadata(path).map_err(|error| format!("Failed to stat {label}: {error}"))?;
    if meta.file_type().is_symlink() {
        if meta.is_dir() {
            fs::remove_dir(path)
                .map_err(|error| format!("Failed to delete {label} symlink: {error}"))
        } else {
            fs::remove_file(path)
                .map_err(|error| format!("Failed to delete {label} symlink: {error}"))
        }
    } else if meta.is_dir() {
        fs::remove_dir_all(path)
            .map_err(|error| format!("Failed to delete {label} directory: {error}"))
    } else {
        fs::remove_file(path).map_err(|error| format!("Failed to delete {label} file: {error}"))
    }
}

fn check_cancelled(cancel: &Arc<AtomicBool>) -> Result<(), String> {
    if cancel.load(Ordering::Relaxed) {
        Err("Operation cancelled".to_string())
    } else {
        Ok(())
    }
}

struct ProgressContext<'a> {
    emit: &'a dyn Fn(ProgressUpdate),
    operation_id: &'a str,
    operation_type: &'a str,
    cancel: &'a Arc<AtomicBool>,
    total_bytes: &'a Arc<AtomicU64>,
    total_files: &'a Arc<AtomicU64>,
    completed_files: &'a Arc<AtomicU64>,
    last_running_emit: StdMutex<Instant>,
}

impl ProgressContext<'_> {
    fn check_cancelled(&self) -> Result<(), String> {
        check_cancelled(self.cancel)
    }

    fn emit(&self, current: u64, current_item: String, status: &str, error: Option<String>) {
        self.emit_with_total(
            current,
            self.total_bytes.load(Ordering::Relaxed),
            current_item,
            status,
            error,
        );
    }

    fn emit_with_total(
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

    fn emit_now(
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

    fn should_emit_running(&self) -> bool {
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

    fn complete_file(&self, current: u64, current_item: String) {
        self.complete_files(1, current, current_item);
    }

    fn complete_files(&self, count: u64, current: u64, current_item: String) {
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

struct CopyContext<'a> {
    progress: &'a ProgressContext<'a>,
    network: bool,
}

impl CopyContext<'_> {
    fn attempts(&self) -> u32 {
        if self.network {
            NETWORK_MAX_RETRIES
        } else {
            1
        }
    }

    fn buffer_size(&self) -> usize {
        if self.network {
            NETWORK_BUFFER_SIZE
        } else {
            LOCAL_BUFFER_SIZE
        }
    }
}

fn is_real_directory(path: &Path) -> bool {
    fs::symlink_metadata(path)
        .map(|meta| {
            let file_type = meta.file_type();
            file_type.is_dir() && !file_type.is_symlink()
        })
        .unwrap_or(false)
}

fn prepare_destination_for_copy(
    source_is_dir: bool,
    dst_path: &Path,
    replace_existing: bool,
) -> Result<bool, String> {
    if !path_exists_no_follow(dst_path) {
        return Ok(false);
    }

    if !replace_existing {
        return Err(conflict_for_existing_destination(dst_path));
    }

    if source_is_dir && is_real_directory(dst_path) {
        return Ok(true);
    }

    remove_path(dst_path, "destination")?;
    Ok(false)
}

fn copy_file_attempt(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: u64,
    buffer_size: usize,
) -> Result<u64, String> {
    let src_file =
        fs::File::open(src).map_err(|error| format!("Failed to open source file: {error}"))?;
    let dst_file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(dst)
        .map_err(|error| format!("Failed to create destination file: {error}"))?;
    let mut reader = BufReader::with_capacity(buffer_size, src_file);
    let mut writer = BufWriter::with_capacity(buffer_size, dst_file);
    let mut buffer = vec![0u8; buffer_size];
    let mut copied_this_attempt = 0u64;
    let mut next_emit_at = PROGRESS_BYTE_STEP;

    loop {
        ctx.progress.check_cancelled()?;
        let read = reader
            .read(&mut buffer)
            .map_err(|error| format!("Failed to read source file: {error}"))?;
        if read == 0 {
            break;
        }
        writer
            .write_all(&buffer[..read])
            .map_err(|error| format!("Failed to write destination file: {error}"))?;
        copied_this_attempt += read as u64;

        if copied_this_attempt >= next_emit_at {
            ctx.progress.emit(
                completed_bytes + copied_this_attempt,
                src.to_string_lossy().to_string(),
                "running",
                None,
            );
            while next_emit_at <= copied_this_attempt {
                next_emit_at = next_emit_at.saturating_add(PROGRESS_BYTE_STEP);
            }
        }
    }

    writer
        .flush()
        .map_err(|error| format!("Failed to flush destination file: {error}"))?;
    simplefile_core::file_ops::preserve_basic_metadata(src, dst)?;
    Ok(copied_this_attempt)
}

fn copy_file_with_progress(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
) -> Result<(), String> {
    if let Some(parent) = dst.parent() {
        fs::create_dir_all(parent)
            .map_err(|error| format!("Failed to create parent directory: {error}"))?;
    }

    let file_len = fs::symlink_metadata(src)
        .map_err(|error| format!("Failed to stat source file: {error}"))?
        .len();
    let known = ctx.progress.total_bytes.load(Ordering::Relaxed);
    let needed = (*completed_bytes).saturating_add(file_len);
    if needed > known {
        ctx.progress.total_bytes.store(needed, Ordering::Relaxed);
    }

    if file_len == 0 {
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(dst)
            .map_err(|error| format!("Failed to create destination file: {error}"))?;
        simplefile_core::file_ops::preserve_basic_metadata(src, dst)?;
        ctx.progress
            .complete_file(*completed_bytes, src.to_string_lossy().to_string());
        return Ok(());
    }

    let attempts = ctx.attempts();
    let buffer_size = ctx.buffer_size();
    let mut last_err = String::new();

    for attempt in 0..attempts {
        ctx.progress.check_cancelled()?;
        if attempt > 0 {
            std::thread::sleep(Duration::from_millis(500 * (1u64 << (attempt - 1))));
            let _ = fs::remove_file(dst);
        }

        match copy_file_attempt(src, dst, ctx, *completed_bytes, buffer_size) {
            Ok(written) => {
                *completed_bytes += written;
                if written > file_len {
                    let known = ctx.progress.total_bytes.load(Ordering::Relaxed);
                    let needed = (*completed_bytes).max(known);
                    if needed > known {
                        ctx.progress.total_bytes.store(needed, Ordering::Relaxed);
                    }
                }
                ctx.progress
                    .complete_file(*completed_bytes, src.to_string_lossy().to_string());
                return Ok(());
            }
            Err(error) if error == "Operation cancelled" => return Err(error),
            Err(error) => {
                last_err = error;
                let _ = fs::remove_file(dst);
            }
        }
    }

    Err(last_err)
}

fn copy_item_with_progress(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    replace_existing: bool,
) -> Result<(), String> {
    let mut stack: Vec<(PathBuf, PathBuf)> = vec![(src.to_path_buf(), dst.to_path_buf())];
    let mut copied_dirs: Vec<(PathBuf, PathBuf)> = Vec::new();

    while let Some((src_path, dst_path)) = stack.pop() {
        ctx.progress.check_cancelled()?;
        ctx.progress.emit(
            *completed_bytes,
            src_path.to_string_lossy().to_string(),
            "running",
            None,
        );

        let lstat = fs::symlink_metadata(&src_path)
            .map_err(|error| format!("Failed to stat source: {error}"))?;
        let file_type = lstat.file_type();
        let merged_existing_directory =
            prepare_destination_for_copy(file_type.is_dir(), &dst_path, replace_existing)?;

        if file_type.is_dir() {
            if !merged_existing_directory {
                create_dir_exclusive(&dst_path)?;
                copied_dirs.push((src_path.clone(), dst_path.clone()));
            }
            for entry in fs::read_dir(&src_path)
                .map_err(|error| format!("Failed to read directory: {error}"))?
            {
                let entry = entry.map_err(|error| format!("Failed to read entry: {error}"))?;
                stack.push((entry.path(), dst_path.join(entry.file_name())));
            }
        } else if file_type.is_symlink() {
            recreate_symlink(&src_path, &dst_path)?;
            ctx.progress
                .complete_file(*completed_bytes, src_path.to_string_lossy().to_string());
        } else {
            copy_file_with_progress(&src_path, &dst_path, ctx, completed_bytes)?;
        }
    }

    for (src_dir, dst_dir) in copied_dirs.into_iter().rev() {
        simplefile_core::file_ops::preserve_basic_metadata(&src_dir, &dst_dir)?;
    }

    Ok(())
}

fn ensure_destination_available(plan: &TransferPlan) -> Result<(), String> {
    if !plan.replace_existing && path_exists_no_follow(&plan.final_dest) {
        return Err(conflict_for_existing_destination(&plan.final_dest));
    }
    Ok(())
}

fn copy_plan_with_progress(
    plan: &TransferPlan,
    ctx: &ProgressContext,
    completed_bytes: &mut u64,
) -> Result<(), String> {
    ensure_destination_available(plan)?;

    let copy_ctx = CopyContext {
        progress: ctx,
        network: false,
    };
    copy_item_with_progress(
        &plan.source_path,
        &plan.final_dest,
        &copy_ctx,
        completed_bytes,
        plan.replace_existing,
    )
}

fn move_plan_with_progress(
    plan: &TransferPlan,
    ctx: &ProgressContext,
    completed_bytes: &mut u64,
) -> Result<(), String> {
    ensure_destination_available(plan)?;

    let rename_size = fs::symlink_metadata(&plan.source_path)
        .map(|meta| {
            if meta.file_type().is_file() {
                meta.len()
            } else {
                0
            }
        })
        .unwrap_or(0);
    if plan.allow_rename && fs::rename(&plan.source_path, &plan.final_dest).is_ok() {
        *completed_bytes = completed_bytes.saturating_add(rename_size.max(1));
        ctx.complete_files(
            plan.file_count,
            *completed_bytes,
            plan.source_path.to_string_lossy().to_string(),
        );
        return Ok(());
    }

    let copy_ctx = CopyContext {
        progress: ctx,
        network: false,
    };
    copy_item_with_progress(
        &plan.source_path,
        &plan.final_dest,
        &copy_ctx,
        completed_bytes,
        plan.replace_existing,
    )?;
    remove_path(&plan.source_path, "source")
        .map_err(|error| format!("Copied but failed to delete source: {error}"))?;
    Ok(())
}

fn choose_next_keep_both_destination(
    plan: &mut TransferPlan,
    reserved_destinations: &mut HashSet<String>,
) -> Result<(), String> {
    reserved_destinations.remove(&path_collision_key(&plan.final_dest));
    let dest_dir = plan
        .final_dest
        .parent()
        .ok_or_else(|| "Cannot get destination directory".to_string())?;
    let file_name = plan
        .source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let next_dest = unique_destination_path(dest_dir, file_name, reserved_destinations)?;
    ensure_not_copying_dir_into_itself(&plan.source_path, &next_dest)?;
    reserved_destinations.insert(path_collision_key(&next_dest));
    plan.final_dest = next_dest;
    Ok(())
}

fn estimate_path_transfer(
    path: &Path,
    cancel: &Arc<AtomicBool>,
) -> Result<TransferEstimate, String> {
    check_cancelled(cancel)?;
    let meta = fs::symlink_metadata(path)
        .map_err(|error| format!("Failed to stat {}: {error}", path.display()))?;
    let file_type = meta.file_type();
    if file_type.is_symlink() {
        return Ok(TransferEstimate { bytes: 0, files: 1 });
    }
    if file_type.is_file() {
        return Ok(TransferEstimate {
            bytes: meta.len(),
            files: 1,
        });
    }
    if !file_type.is_dir() {
        return Ok(TransferEstimate::default());
    }

    let mut estimate = TransferEstimate::default();
    let mut stack = vec![path.to_path_buf()];
    let mut visited = 0u32;
    while let Some(dir) = stack.pop() {
        check_cancelled(cancel)?;
        let entries = fs::read_dir(&dir)
            .map_err(|error| format!("Failed to read {}: {error}", dir.display()))?;
        for entry in entries {
            check_cancelled(cancel)?;
            let entry = entry.map_err(|error| format!("Failed to read entry: {error}"))?;
            let entry_path = entry.path();
            let entry_type = match entry.file_type() {
                Ok(ft) => ft,
                Err(_) => continue,
            };
            if entry_type.is_symlink() {
                estimate.files = estimate.files.saturating_add(1);
            } else if entry_type.is_dir() {
                stack.push(entry_path);
            } else if entry_type.is_file() {
                estimate.files = estimate.files.saturating_add(1);
                if let Ok(meta) = entry.metadata() {
                    estimate.bytes = estimate.bytes.saturating_add(meta.len());
                }
            }
            visited = visited.saturating_add(1);
            if visited.is_multiple_of(256) {
                check_cancelled(cancel)?;
            }
        }
    }
    Ok(estimate)
}

fn estimate_transfer(
    plans: &mut [TransferPlan],
    cancel: &Arc<AtomicBool>,
) -> Result<TransferEstimate, String> {
    let mut total = TransferEstimate::default();
    for plan in plans {
        check_cancelled(cancel)?;
        let estimate = estimate_path_transfer(&plan.source_path, cancel)?;
        plan.file_count = estimate.files;
        total.bytes = total.bytes.saturating_add(estimate.bytes);
        total.files = total.files.saturating_add(estimate.files);
    }
    Ok(total)
}

pub fn generate_transfer_operation_id() -> String {
    generate_operation_id()
}

pub fn transfer_with_progress_blocking(
    operation_type: &'static str,
    sources: Vec<String>,
    destination: String,
    operation_id: String,
    conflict_action: String,
    cancel: Arc<AtomicBool>,
    emit: &dyn Fn(ProgressUpdate),
) -> Result<Vec<TransferResult>, String> {
    let (mut plans, _dest_path) = prepare_transfer_inputs(sources, destination, &conflict_action)?;
    let keep_both = is_keep_both_action(&conflict_action);
    let mut reserved_destinations: HashSet<String> = plans
        .iter()
        .map(|plan| path_collision_key(&plan.final_dest))
        .collect();

    let total_bytes = Arc::new(AtomicU64::new(0));
    let total_files = Arc::new(AtomicU64::new(0));
    let completed_files = Arc::new(AtomicU64::new(0));
    let mut completed_bytes = 0u64;
    let mut transferred = Vec::with_capacity(plans.len());
    let progress = ProgressContext {
        emit,
        operation_id: &operation_id,
        operation_type,
        cancel: &cancel,
        total_bytes: &total_bytes,
        total_files: &total_files,
        completed_files: &completed_files,
        last_running_emit: StdMutex::new(Instant::now()),
    };

    progress.emit_now(0, 0, "Calculating size...".to_string(), "running", None);
    let estimated = match estimate_transfer(&mut plans, &cancel) {
        Ok(value) => value,
        Err(error) if error == "Operation cancelled" => {
            progress.emit_with_total(
                0,
                0,
                String::new(),
                "cancelled",
                Some("Operation cancelled".to_string()),
            );
            return Ok(Vec::new());
        }
        Err(error) => {
            eprintln!("simplefile-service: transfer size estimate failed: {error}");
            TransferEstimate::default()
        }
    };
    total_bytes.store(estimated.bytes, Ordering::Relaxed);
    total_files.store(estimated.files, Ordering::Relaxed);
    progress.emit_with_total(
        0,
        estimated.bytes,
        if estimated.bytes > 0 || estimated.files > 0 {
            "Starting transfer...".to_string()
        } else {
            String::new()
        },
        "running",
        None,
    );

    enum TransferOutcome {
        Completed(Vec<TransferResult>),
        Cancelled(Vec<TransferResult>),
        Failed(String),
    }

    let outcome = (|| -> TransferOutcome {
        for mut plan in plans {
            if cancel.load(Ordering::Relaxed) {
                return TransferOutcome::Cancelled(transferred);
            }
            let source = plan.source_path.to_string_lossy().to_string();
            let mut completed_plan = false;

            for _ in 0..100 {
                match operation_type {
                    "copy" => match copy_plan_with_progress(&plan, &progress, &mut completed_bytes)
                    {
                        Ok(()) => {
                            completed_plan = true;
                            break;
                        }
                        Err(error) if error == "Operation cancelled" => {
                            return TransferOutcome::Cancelled(transferred);
                        }
                        Err(error) if keep_both && error.starts_with("CONFLICT:") => {
                            if let Err(resolve_error) = choose_next_keep_both_destination(
                                &mut plan,
                                &mut reserved_destinations,
                            ) {
                                return TransferOutcome::Failed(resolve_error);
                            }
                        }
                        Err(error) => return TransferOutcome::Failed(error),
                    },
                    "move" => match move_plan_with_progress(&plan, &progress, &mut completed_bytes)
                    {
                        Ok(()) => {
                            completed_plan = true;
                            break;
                        }
                        Err(error) if error == "Operation cancelled" => {
                            return TransferOutcome::Cancelled(transferred);
                        }
                        Err(error) if keep_both && error.starts_with("CONFLICT:") => {
                            if let Err(resolve_error) = choose_next_keep_both_destination(
                                &mut plan,
                                &mut reserved_destinations,
                            ) {
                                return TransferOutcome::Failed(resolve_error);
                            }
                        }
                        Err(error) => return TransferOutcome::Failed(error),
                    },
                    _ => {
                        return TransferOutcome::Failed(format!(
                            "Unsupported operation: {operation_type}"
                        ));
                    }
                }
            }

            if !completed_plan {
                return TransferOutcome::Failed(
                    "Could not choose a unique destination after repeated conflicts".to_string(),
                );
            }

            transferred.push(TransferResult {
                source,
                destination: plan.final_dest.to_string_lossy().to_string(),
            });
        }
        TransferOutcome::Completed(transferred)
    })();

    let final_total = total_bytes.load(Ordering::Relaxed);

    match outcome {
        TransferOutcome::Completed(transferred) => {
            progress.emit_with_total(final_total, final_total, String::new(), "completed", None);
            Ok(transferred)
        }
        TransferOutcome::Cancelled(transferred) => {
            progress.emit_with_total(
                completed_bytes,
                final_total,
                String::new(),
                "cancelled",
                Some("Operation cancelled".to_string()),
            );
            Ok(transferred)
        }
        TransferOutcome::Failed(error) => {
            progress.emit_with_total(
                completed_bytes,
                final_total,
                String::new(),
                "error",
                Some(error.clone()),
            );
            Err(error)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{prepare_transfer_inputs, transfer_with_progress_blocking, ProgressContext};
    use simplefile_core::models::ProgressUpdate;
    use std::fs;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicBool, AtomicU64};
    use std::sync::{Arc, Mutex};
    use std::time::{Instant, SystemTime, UNIX_EPOCH};

    fn unique_temp_path(name: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("simplefile_service_progress_test_{name}_{nanos}"))
    }

    #[test]
    fn prepare_transfer_inputs_rejects_existing_destination() {
        let src_dir = unique_temp_path("conflict_src");
        let dst_dir = unique_temp_path("conflict_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("same.txt");
        let destination = dst_dir.join("same.txt");
        fs::write(&source, b"source").unwrap();
        fs::write(&destination, b"destination").unwrap();

        let result = prepare_transfer_inputs(
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "error",
        );

        assert!(result.unwrap_err().starts_with("CONFLICT:"));

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn keep_both_preserves_existing_destination() {
        let src_dir = unique_temp_path("keep_both_src");
        let dst_dir = unique_temp_path("keep_both_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("same.txt");
        let existing = dst_dir.join("same.txt");
        fs::write(&source, b"source").unwrap();
        fs::write(&existing, b"destination").unwrap();
        let cancel = Arc::new(AtomicBool::new(false));

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "op_keep_both_test".to_string(),
            "keep-both".to_string(),
            cancel,
            &|_| {},
        )
        .expect("keep-both copy should complete");

        assert_eq!(result.len(), 1);
        assert_eq!(fs::read(&existing).unwrap(), b"destination");
        assert_ne!(result[0].destination, existing.to_string_lossy());
        assert_eq!(fs::read(&result[0].destination).unwrap(), b"source");

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn replace_merges_existing_destination_directory() {
        let src_root = unique_temp_path("replace_merge_src");
        let dst_root = unique_temp_path("replace_merge_dst");
        let source = src_root.join("Repos");
        let existing = dst_root.join("Repos");
        fs::create_dir_all(source.join("nested")).unwrap();
        fs::create_dir_all(&existing).unwrap();
        fs::write(source.join("same.txt"), b"source").unwrap();
        fs::write(source.join("nested").join("new.txt"), b"new").unwrap();
        fs::write(existing.join("same.txt"), b"destination").unwrap();
        fs::write(existing.join("keep.txt"), b"keep").unwrap();
        let cancel = Arc::new(AtomicBool::new(false));

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            "op_replace_merge_test".to_string(),
            "replace".to_string(),
            cancel,
            &|_| {},
        )
        .expect("replace copy should merge existing directories");

        assert_eq!(result.len(), 1);
        assert_eq!(result[0].destination, existing.to_string_lossy());
        assert_eq!(fs::read(existing.join("same.txt")).unwrap(), b"source");
        assert_eq!(
            fs::read(existing.join("nested").join("new.txt")).unwrap(),
            b"new"
        );
        assert_eq!(fs::read(existing.join("keep.txt")).unwrap(), b"keep");
        assert!(source.exists());

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    #[test]
    fn transfer_emits_cancelled_when_cancelled_before_preflight() {
        let src_dir = unique_temp_path("cancel_src");
        let dst_dir = unique_temp_path("cancel_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("large.txt");
        fs::write(&source, vec![b'x'; 1024]).unwrap();
        let cancel = Arc::new(AtomicBool::new(true));
        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "op_cancel_test".to_string(),
            "error".to_string(),
            cancel,
            &|update| updates_target.lock().unwrap().push(update),
        )
        .expect("cancel should return partial results");

        assert!(result.is_empty());
        assert!(updates
            .lock()
            .unwrap()
            .iter()
            .any(|update| update.status == "cancelled"));
        assert!(!dst_dir.join("large.txt").exists());

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn transfer_emits_completed_progress() {
        let src_dir = unique_temp_path("complete_src");
        let dst_dir = unique_temp_path("complete_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("done.txt");
        fs::write(&source, b"done").unwrap();
        let cancel = Arc::new(AtomicBool::new(false));
        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "op_complete_test".to_string(),
            "error".to_string(),
            cancel,
            &|update| updates_target.lock().unwrap().push(update),
        )
        .expect("copy should complete");

        assert_eq!(result.len(), 1);
        assert_eq!(fs::read(dst_dir.join("done.txt")).unwrap(), b"done");
        let updates = updates.lock().unwrap();
        let completed = updates
            .iter()
            .find(|update| update.status == "completed")
            .expect("completed progress update");
        assert_eq!(completed.current_files, 1);
        assert_eq!(completed.total_files, 1);

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn progress_context_throttles_running_but_not_terminal_updates() {
        let total_bytes = Arc::new(AtomicU64::new(100));
        let total_files = Arc::new(AtomicU64::new(1));
        let completed_files = Arc::new(AtomicU64::new(0));
        let cancel = Arc::new(AtomicBool::new(false));
        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();
        let emit = |update| updates_target.lock().unwrap().push(update);
        let progress = ProgressContext {
            emit: &emit,
            operation_id: "op_throttle_test",
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total_bytes,
            total_files: &total_files,
            completed_files: &completed_files,
            last_running_emit: Mutex::new(Instant::now()),
        };

        progress.emit_with_total(1, 100, "first".to_string(), "running", None);
        progress.emit_with_total(100, 100, String::new(), "completed", None);

        let updates = updates.lock().unwrap();
        assert_eq!(updates.len(), 1);
        assert_eq!(updates[0].status, "completed");
    }
}
