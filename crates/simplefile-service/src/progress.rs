//! Progress tracking and cancellation for bulk file transfers.

mod adaptive;
mod execute;
mod parallel;
mod plan;
mod registry;
mod reporting;
mod tuning;

// Moved to the leaf crate `sumafile-transfer`; re-exported under the old names.
use sumafile_transfer::resources;

pub(crate) use parallel::ConflictAction;
#[cfg(test)]
pub(crate) use parallel::TuningUpdate;
pub use registry::OperationRegistry;
pub(crate) use tuning::policy_for_operation;
#[cfg(test)]
pub(crate) use tuning::Policy;

#[cfg(test)]
pub(crate) use plan::prepare_transfer_inputs;
pub(crate) use plan::unique_destination_path;
pub(crate) use reporting::ProgressContext;

#[cfg(test)]
pub(crate) use execute::{copy_file_attempt, copy_file_with_progress};
#[cfg(test)]
pub(crate) use reporting::CopyContext;

use crate::transfer_reliability::{Failure, FailureKind, CANCELLED_MESSAGE, CONFLICT_PREFIX};
use parallel::{FileTask, RootTask, TaskFailure};
use plan::{
    prepare_transfer_inputs_cached, prime_transfer, TransferEstimate, TransferPlan, VolumeKindCache,
};
use serde::Serialize;
use simplefile_core::models::ProgressUpdate;
use simplefile_core::path_conflict::{
    collapse_separators, is_already_exists_error, rename_exclusive,
};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex};
use std::time::Instant;

#[derive(Debug, Serialize, Clone)]
pub struct TransferResult {
    pub source: String,
    pub destination: String,
    /// Absent for a whole new item. A merge owns only the paths listed here.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub changes: Option<TransferChanges>,
}

#[derive(Debug, Default, Serialize, Clone)]
pub struct TransferChanges {
    /// Paths relative to the selected source/destination roots.
    pub files: Vec<String>,
    pub created_dirs: Vec<String>,
    /// Move only, deepest first. The empty path denotes the selected source root.
    pub removed_source_dirs: Vec<String>,
    /// Set by the IPC writer when these paths were sent before the response.
    #[serde(skip_serializing_if = "is_zero")]
    pub streamed_paths: usize,
}

fn is_zero(count: &usize) -> bool {
    *count == 0
}

/// One file or directory that was not transferred. `kind` is `conflict` when
/// the user has to decide (destination exists / different type) and `error`
/// otherwise; `destination` is the exact path the item was headed for, so the
/// UI can retry that item alone.
#[derive(Debug, Serialize, Clone)]
pub struct TransferFailure {
    pub source: String,
    pub destination: String,
    pub kind: String,
    pub error: String,
}

impl TransferFailure {
    fn from_task(failure: &TaskFailure) -> Self {
        Self::new(&failure.source, &failure.destination, &failure.failure)
    }

    fn new(source: &Path, destination: &Path, failure: &Failure) -> Self {
        Self {
            source: source.to_string_lossy().into_owned(),
            destination: destination.to_string_lossy().into_owned(),
            kind: failure_kind_label(failure.kind).into(),
            error: boundary_message(failure),
        }
    }

    pub fn is_conflict(&self) -> bool {
        self.kind == "conflict"
    }
}

fn failure_kind_label(kind: FailureKind) -> &'static str {
    match kind {
        FailureKind::Conflict => "conflict",
        FailureKind::Cancelled => "cancelled",
        _ => "error",
    }
}

/// Messages that leave the engine keep the legacy `CONFLICT:` prefix for conflicts.
fn boundary_message(failure: &Failure) -> String {
    if failure.kind == FailureKind::Conflict && !failure.message.starts_with(CONFLICT_PREFIX) {
        format!("{CONFLICT_PREFIX} {}", failure.message)
    } else {
        failure.message.clone()
    }
}

#[derive(Debug, Serialize, Clone)]
pub struct TransferBatchOutcome {
    pub operation_id: String,
    pub status: String,
    pub committed: Vec<TransferResult>,
    pub skipped: Vec<TransferResult>,
    pub failed: Vec<TransferFailure>,
    pub pending: Vec<String>,
}

#[derive(Debug, Clone)]
pub struct TransferBatchError {
    pub message: String,
    pub outcome: Box<TransferBatchOutcome>,
}

/// One exact file to transfer again after the user resolved its failure.
#[derive(Debug, Clone)]
pub struct TransferItem {
    pub source: String,
    pub destination: String,
    pub action: String,
}

pub use sumafile_transfer::ids::generate_transfer_operation_id;

/// Compatibility RPCs share the transfer engine instead of bypassing recovery.
pub(crate) fn transfer_single_compat(
    operation: &'static str,
    source: &str,
    destination: &str,
    action: &str,
) -> Result<String, String> {
    if simplefile_core::archive::should_handle_transfer(source, destination)? {
        return if operation == "move" {
            simplefile_core::archive::move_entry_resolved(
                source.to_string(),
                destination.to_string(),
                action.to_string(),
            )
        } else {
            simplefile_core::archive::copy_entry_resolved(
                source.to_string(),
                destination.to_string(),
                action.to_string(),
            )
        };
    }
    transfer_with_progress_blocking(
        operation,
        vec![source.into()],
        destination.into(),
        generate_transfer_operation_id(),
        action.into(),
        Arc::new(AtomicBool::new(false)),
        &|_| {},
    )
    .map(|results| {
        results
            .into_iter()
            .next()
            .map_or_else(|| format!("SKIPPED:{source}"), |r| r.destination)
    })
    .map_err(|error| error.message)
}

pub fn transfer_with_progress_blocking(
    operation_type: &'static str,
    sources: Vec<String>,
    destination: String,
    operation_id: String,
    conflict_action: String,
    cancel: Arc<AtomicBool>,
    emit: &dyn Fn(ProgressUpdate),
) -> Result<Vec<TransferResult>, TransferBatchError> {
    transfer_with_tuning_blocking(
        operation_type,
        sources,
        destination,
        operation_id,
        conflict_action,
        cancel,
        emit,
        &|_| {},
    )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn transfer_with_tuning_blocking(
    operation_type: &'static str,
    sources: Vec<String>,
    destination: String,
    operation_id: String,
    conflict_action: String,
    cancel: Arc<AtomicBool>,
    emit: &dyn Fn(ProgressUpdate),
    emit_tuning: &dyn Fn(parallel::TuningUpdate),
) -> Result<Vec<TransferResult>, TransferBatchError> {
    let policy_id = operation_id.clone();
    transfer_with_policy_blocking(
        operation_type,
        Request::Selection {
            sources,
            conflict_action,
        },
        destination,
        operation_id,
        cancel,
        emit,
        emit_tuning,
        &|| policy_for_operation(&policy_id),
    )
}

/// Targeted retry: transfer exactly these files to exactly these destinations.
#[allow(clippy::too_many_arguments)]
pub(crate) fn transfer_items_with_tuning_blocking(
    operation_type: &'static str,
    items: Vec<TransferItem>,
    destination: String,
    operation_id: String,
    cancel: Arc<AtomicBool>,
    emit: &dyn Fn(ProgressUpdate),
    emit_tuning: &dyn Fn(parallel::TuningUpdate),
) -> Result<Vec<TransferResult>, TransferBatchError> {
    let policy_id = operation_id.clone();
    transfer_with_policy_blocking(
        operation_type,
        Request::Items(items),
        destination,
        operation_id,
        cancel,
        emit,
        emit_tuning,
        &|| policy_for_operation(&policy_id),
    )
}

enum Request {
    Selection {
        sources: Vec<String>,
        conflict_action: String,
    },
    Items(Vec<TransferItem>),
}

impl Request {
    /// The same request with every path's separators collapsed; see
    /// [`collapse_separators`].
    fn normalized(self) -> Self {
        match self {
            Self::Selection {
                sources,
                conflict_action,
            } => Self::Selection {
                sources: sources
                    .iter()
                    .map(|source| collapse_separators(source))
                    .collect(),
                conflict_action,
            },
            Self::Items(items) => Self::Items(
                items
                    .into_iter()
                    .map(|item| TransferItem {
                        source: collapse_separators(&item.source),
                        destination: collapse_separators(&item.destination),
                        action: item.action,
                    })
                    .collect(),
            ),
        }
    }

    fn requested_sources(&self) -> Vec<String> {
        match self {
            Self::Selection { sources, .. } => sources.clone(),
            Self::Items(items) => items.iter().map(|item| item.source.clone()).collect(),
        }
    }
}

/// Plans for a targeted retry: each item is an exact source → destination pair.
fn plans_for_items(
    items: &[TransferItem],
    destination: &str,
    volumes: &mut VolumeKindCache,
) -> Result<Vec<TransferPlan>, String> {
    let dest_root = simplefile_core::utils::validate_existing_path_no_resolve(destination)?;
    if !dest_root.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }
    crate::transfer_journal::recover_directory(&dest_root)?;
    let dest_network = volumes.is_network(&dest_root);
    let mut plans = Vec::with_capacity(items.len());
    let mut scanned_dirs = HashSet::new();
    let mut reserved = HashSet::new();
    for item in items {
        let source_path = simplefile_core::utils::validate_path_no_follow(&item.source)?;
        let mut final_dest = PathBuf::from(item.destination.trim());
        if final_dest.file_name().is_none() || !final_dest.is_absolute() {
            return Err(format!(
                "Retry destination must be an absolute file path: {}",
                item.destination
            ));
        }
        let meta = std::fs::symlink_metadata(&source_path)
            .map_err(|error| format!("Failed to stat {}: {error}", source_path.display()))?;
        let file_type = meta.file_type();
        // A failed folder is walked again; what it already copied is
        // recognised file by file, so only the missing part is transferred.
        let is_dir = file_type.is_dir() && !file_type.is_symlink();
        let conflict = ConflictAction::parse(&item.action);
        if conflict == ConflictAction::CreateOnly
            && simplefile_core::path_conflict::path_exists_no_follow(&final_dest)
        {
            return Err(crate::transfer_staging::conflict_for_existing_destination(
                &final_dest,
            ));
        }
        if matches!(conflict, ConflictAction::Error | ConflictAction::Replace)
            && simplefile_core::path_conflict::paths_refer_to_same_entry(&source_path, &final_dest)
        {
            return Err("Cannot copy or move an item onto itself".into());
        }
        if is_dir {
            if conflict == ConflictAction::KeepBoth
                && (simplefile_core::path_conflict::path_exists_no_follow(&final_dest)
                    || reserved.contains(&simplefile_core::path_conflict::path_collision_key(
                        &final_dest,
                    )))
            {
                final_dest = unique_destination_path(
                    final_dest.parent().unwrap(),
                    final_dest.file_name().unwrap(),
                    &reserved,
                )?;
            }
            plan::ensure_dir_not_inside_itself(&source_path, &final_dest)?;
        }
        if let Some(parent) = final_dest.parent() {
            if scanned_dirs.insert(parent.to_path_buf()) {
                if !parent.is_dir() {
                    std::fs::create_dir_all(parent).map_err(|error| {
                        format!("Failed to create {}: {error}", parent.display())
                    })?;
                }
                crate::transfer_journal::recover_directory(parent)?;
            }
        }
        reserved.insert(simplefile_core::path_conflict::path_collision_key(
            &final_dest,
        ));
        let is_symlink = file_type.is_symlink();
        plans.push(TransferPlan {
            network: dest_network || volumes.is_network(&source_path),
            source_path,
            final_dest,
            conflict,
            file_count: u64::from(!is_dir),
            is_dir,
            is_symlink,
            byte_size: if file_type.is_file() { meta.len() } else { 0 },
        });
    }
    Ok(plans)
}

enum RenameOutcome {
    Done(PathBuf),
    Conflict(String),
    Fallback,
}

/// A whole selected directory moves by one rename when its destination is free.
fn rename_directory_move(plan: &TransferPlan, reserved: &mut HashSet<String>) -> RenameOutcome {
    if !plan::same_volume(&plan.source_path, &plan.final_dest)
        || simplefile_core::path_conflict::path_exists_no_follow(&plan.final_dest)
    {
        return RenameOutcome::Fallback;
    }
    let mut target = plan.final_dest.clone();
    for _ in 0..100 {
        match rename_exclusive(&plan.source_path, &target) {
            Ok(()) => return RenameOutcome::Done(target),
            Err(error) if is_already_exists_error(&error) => {
                if plan.conflict != ConflictAction::KeepBoth {
                    // Something appeared meanwhile; merge handles it per child.
                    return RenameOutcome::Fallback;
                }
                let Some(parent) = target.parent() else {
                    return RenameOutcome::Fallback;
                };
                let Some(name) = plan.source_path.file_name() else {
                    return RenameOutcome::Fallback;
                };
                match unique_destination_path(parent, name, reserved) {
                    Ok(next) => {
                        reserved.insert(simplefile_core::path_conflict::path_collision_key(&next));
                        target = next;
                    }
                    Err(error) => return RenameOutcome::Conflict(error),
                }
            }
            Err(_) => return RenameOutcome::Fallback,
        }
    }
    RenameOutcome::Fallback
}

#[allow(clippy::too_many_arguments)]
fn transfer_with_policy_blocking(
    operation_type: &'static str,
    request: Request,
    destination: String,
    operation_id: String,
    cancel: Arc<AtomicBool>,
    emit: &dyn Fn(ProgressUpdate),
    emit_tuning: &dyn Fn(parallel::TuningUpdate),
    policy: &dyn Fn() -> tuning::Policy,
) -> Result<Vec<TransferResult>, TransferBatchError> {
    // A doubled separator (`V:\\sync`, from a path the app assembled) survives
    // every create and write but fails the final rename; nothing downstream
    // sees one.
    let request = request.normalized();
    let destination = collapse_separators(&destination);
    let requested_sources = request.requested_sources();
    let total_bytes = Arc::new(AtomicU64::new(0));
    let total_files = Arc::new(AtomicU64::new(0));
    let completed_files = Arc::new(AtomicU64::new(0));
    let progress = ProgressContext {
        emit,
        operation_id: &operation_id,
        operation_type,
        cancel: &cancel,
        total_bytes: &total_bytes,
        total_files: &total_files,
        completed_files: &completed_files,
        last_running_emit: StdMutex::new(Instant::now()),
        retry_count: None,
        rate_limit: None,
        write_latency: None,
    };
    // The first event precedes every volume probe and destination scan.
    progress.emit_now(0, 0, "Preparing transfer...".to_string(), "running", None);

    let mut volumes = VolumeKindCache::default();
    let mut failed: Vec<TransferFailure> = Vec::new();
    let mut skipped: Vec<TransferResult> = Vec::new();
    let mut planned_skipped_bytes = 0u64;
    let mut planned_skipped_files = 0u64;
    let planned = match &request {
        Request::Selection {
            sources,
            conflict_action,
        } => prepare_transfer_inputs_cached(
            sources.clone(),
            destination.clone(),
            conflict_action,
            &mut volumes,
        )
        .map(|(plans, _, conflicts, planned_skips)| {
            for (source, final_dest, message) in conflicts {
                failed.push(TransferFailure::new(
                    &source,
                    &final_dest,
                    &Failure::conflict(message),
                ));
            }
            for skip in planned_skips {
                planned_skipped_bytes = planned_skipped_bytes.saturating_add(skip.byte_size);
                planned_skipped_files = planned_skipped_files.saturating_add(skip.file_count);
                skipped.push(TransferResult {
                    source: skip.source.to_string_lossy().into(),
                    destination: skip.destination.to_string_lossy().into(),
                    changes: None,
                });
            }
            plans
        }),
        Request::Items(items) => plans_for_items(items, &destination, &mut volumes),
    };
    let mut plans = match planned {
        Ok(plans) => plans,
        Err(error) => {
            let failure = Failure::from(error);
            progress.emit_with_total(
                0,
                0,
                String::new(),
                "error",
                Some(boundary_message(&failure)),
            );
            return Err(TransferBatchError::failed(
                operation_id,
                boundary_message(&failure),
                Vec::new(),
                Vec::new(),
                Vec::new(),
                requested_sources,
            ));
        }
    };
    let mut session = adaptive::Session::new(
        &operation_id,
        operation_type,
        &requested_sources,
        &destination,
        policy,
        emit_tuning,
    );

    let estimated = match prime_transfer(&mut plans, &cancel) {
        Ok(value) => value,
        Err(error) if error == CANCELLED_MESSAGE => {
            progress.emit_with_total(
                0,
                0,
                String::new(),
                "cancelled",
                Some(CANCELLED_MESSAGE.to_string()),
            );
            return Err(TransferBatchError::cancelled(
                operation_id,
                Vec::new(),
                Vec::new(),
                requested_sources,
            ));
        }
        Err(error) => {
            eprintln!("simplefile-service: transfer size estimate failed: {error}");
            TransferEstimate::default()
        }
    };
    let initial_total_bytes = estimated.bytes.saturating_add(planned_skipped_bytes);
    let initial_total_files = estimated.files.saturating_add(planned_skipped_files);
    total_bytes.store(initial_total_bytes, Ordering::Relaxed);
    total_files.store(initial_total_files, Ordering::Relaxed);
    completed_files.store(planned_skipped_files, Ordering::Relaxed);
    progress.emit_with_total(
        planned_skipped_bytes,
        initial_total_bytes,
        if initial_total_bytes > 0 || initial_total_files > 0 {
            "Starting transfer...".to_string()
        } else {
            String::new()
        },
        "running",
        None,
    );

    let is_move = operation_type == "move";
    let retry = matches!(request, Request::Items(_));
    let mut completed_bytes = planned_skipped_bytes;
    let mut transferred: Vec<TransferResult> = Vec::with_capacity(plans.len());
    let mut files: Vec<FileTask> = Vec::new();
    let mut roots: Vec<RootTask> = Vec::new();
    let mut reserved: HashSet<String> = plans
        .iter()
        .map(|plan| simplefile_core::path_conflict::path_collision_key(&plan.final_dest))
        .collect();

    for plan in &plans {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        if plan.is_dir {
            if is_move {
                match rename_directory_move(plan, &mut reserved) {
                    RenameOutcome::Done(target) => {
                        // Directories contribute no bytes to the estimate, so
                        // a renamed one adds none to the progress either.
                        progress.complete_files(
                            plan.file_count,
                            completed_bytes,
                            plan.source_path.to_string_lossy().to_string(),
                        );
                        transferred.push(TransferResult {
                            source: plan.source_path.to_string_lossy().into(),
                            destination: target.to_string_lossy().into(),
                            changes: None,
                        });
                        continue;
                    }
                    RenameOutcome::Conflict(message) => {
                        failed.push(TransferFailure::new(
                            &plan.source_path,
                            &plan.final_dest,
                            &Failure::conflict(message),
                        ));
                        continue;
                    }
                    RenameOutcome::Fallback => {}
                }
            }
            roots.push(RootTask {
                source: plan.source_path.clone(),
                destination: plan.final_dest.clone(),
                network: plan.network,
                conflict: plan.conflict,
                delete_source: is_move,
                retry,
            });
        } else {
            files.push(FileTask {
                source: plan.source_path.clone(),
                destination: plan.final_dest.clone(),
                size: plan.byte_size,
                network: plan.network,
                conflict: plan.conflict,
                owned_tree: false,
                symlink: plan.is_symlink,
                top_level: true,
                merge_root: None,
                delete_source: is_move,
                retry,
            });
        }
    }

    let cancelled_early = cancel.load(Ordering::Relaxed);
    let mut cancelled = cancelled_early;
    let mut partial_roots = HashSet::new();
    let mut failed_bytes = 0u64;
    if !cancelled_early && (!files.is_empty() || !roots.is_empty()) {
        let ctx = reporting::CopyContext {
            progress: &progress,
            operation_id: &operation_id,
            network: plans.iter().any(|plan| plan.network),
            resume_existing: false,
            policy: session.policy().clone(),
        };
        let result =
            parallel::copy_selection(&roots, files, &ctx, &mut completed_bytes, &mut session);
        cancelled = result.copy.cancelled || cancel.load(Ordering::Relaxed);
        failed_bytes = result.copy.failed_bytes;

        for index in &result.copy.completed {
            let Some(task) = result.copy.completed_tasks.get(index) else {
                continue;
            };
            let destination = result
                .copy
                .destinations
                .get(index)
                .cloned()
                .unwrap_or_else(|| task.destination.clone());
            transferred.push(TransferResult {
                source: task.source.to_string_lossy().into(),
                destination: destination.to_string_lossy().into(),
                changes: (result.copy.reused.contains(index)
                    || task.conflict == ConflictAction::Replace)
                    .then(TransferChanges::default),
            });
        }
        for (_, task) in &result.copy.skipped {
            if task.top_level {
                skipped.push(TransferResult {
                    source: task.source.to_string_lossy().into(),
                    destination: task.destination.to_string_lossy().into(),
                    changes: None,
                });
            }
        }
        for (_, failure) in &result.copy.failed {
            failed.push(TransferFailure::from_task(failure));
        }
        for failure in &result.dir_failures {
            failed.push(TransferFailure::from_task(failure));
        }
        if let Some(error) = &result.copy.scan_error {
            if !cancelled {
                failed.push(TransferFailure {
                    source: String::new(),
                    destination: destination.clone(),
                    kind: "error".into(),
                    error: error.clone(),
                });
            }
        }
        // Sources that stayed behind on purpose (skipped) or because they
        // failed both explain a source directory that cannot be removed.
        let failed_paths: Vec<PathBuf> = failed
            .iter()
            .filter(|f| !f.source.is_empty())
            .map(|f| PathBuf::from(&f.source))
            .chain(
                result
                    .copy
                    .skipped
                    .iter()
                    .map(|(_, task)| task.source.clone()),
            )
            .collect();
        for (root, mut outcome) in roots.iter().zip(result.roots) {
            let mut record_root = false;
            match outcome.error {
                Some(error) if error.is_cancelled() => cancelled = true,
                Some(error) => {
                    failed.push(TransferFailure::new(
                        &root.source,
                        &root.destination,
                        &error,
                    ));
                }
                None if cancelled => {}
                None => {
                    if is_move {
                        if let Err(error) =
                            crate::transfer_staging::delete_verified_source_tree_recording(
                                &root.source,
                                &outcome.manifest,
                                &failed_paths,
                                |path| {
                                    if let Some(changes) = &mut outcome.changes {
                                        changes.removed_source_dirs.push(
                                            path.strip_prefix(&root.source)
                                                .expect("source directory in root")
                                                .to_string_lossy()
                                                .into_owned(),
                                        );
                                    }
                                },
                            )
                        {
                            failed.push(TransferFailure::new(
                                &root.source,
                                &root.destination,
                                &Failure::from(format!(
                                    "Copied but failed to delete source: {error}"
                                )),
                            ));
                        } else {
                            record_root = true;
                        }
                    } else {
                        record_root = true;
                    }
                }
            }
            // A cancelled merge can have both committed additions and work left at the root.
            if cancelled && !record_root {
                partial_roots.insert(root.source.to_string_lossy().into_owned());
            }
            // A partial merge has individually undoable additions even after cancellation.
            if record_root
                || outcome.changes.as_ref().is_some_and(|changes| {
                    !changes.files.is_empty()
                        || !changes.created_dirs.is_empty()
                        || !changes.removed_source_dirs.is_empty()
                })
            {
                transferred.push(TransferResult {
                    source: root.source.to_string_lossy().into(),
                    destination: root.destination.to_string_lossy().into(),
                    changes: outcome.changes,
                });
            }
        }
    }

    let final_total = total_bytes.load(Ordering::Relaxed);
    session.display(Vec::new(), true);
    if !cancelled && failed.is_empty() {
        session.save();
    }

    if cancelled {
        // Files interrupted by the cancellation are pending, not failed.
        failed.retain(|item| item.kind != "cancelled");
        progress.emit_with_total(
            completed_bytes,
            final_total,
            String::new(),
            "cancelled",
            Some(CANCELLED_MESSAGE.to_string()),
        );
        let settled: HashSet<&str> = transferred
            .iter()
            .chain(skipped.iter())
            .map(|item| item.source.as_str())
            .chain(failed.iter().map(|item| item.source.as_str()))
            .collect();
        let pending = requested_sources
            .iter()
            .filter(|source| {
                !settled.contains(source.as_str()) || partial_roots.contains(source.as_str())
            })
            .cloned()
            .collect();
        return Err(TransferBatchError::cancelled(
            operation_id,
            transferred,
            skipped,
            pending,
        ));
    }

    if !failed.is_empty() {
        let message = failed[0].error.clone();
        // What was written counts, including the files that then failed: the
        // last update never shows less than the one before it.
        progress.emit_with_total(
            completed_bytes.saturating_add(failed_bytes),
            final_total,
            String::new(),
            "error",
            Some(message.clone()),
        );
        return Err(TransferBatchError::failed(
            operation_id,
            message,
            transferred,
            skipped,
            failed,
            Vec::new(),
        ));
    }

    progress.emit_with_total(final_total, final_total, String::new(), "completed", None);
    Ok(transferred)
}

impl TransferBatchError {
    fn cancelled(
        operation_id: String,
        committed: Vec<TransferResult>,
        skipped: Vec<TransferResult>,
        pending: Vec<String>,
    ) -> Self {
        Self {
            message: CANCELLED_MESSAGE.into(),
            outcome: Box::new(TransferBatchOutcome {
                operation_id,
                status: "cancelled".into(),
                committed,
                skipped,
                failed: Vec::new(),
                pending,
            }),
        }
    }

    fn failed(
        operation_id: String,
        message: String,
        committed: Vec<TransferResult>,
        skipped: Vec<TransferResult>,
        failed: Vec<TransferFailure>,
        pending: Vec<String>,
    ) -> Self {
        Self {
            message: message.clone(),
            outcome: Box::new(TransferBatchOutcome {
                operation_id,
                status: "failed".to_string(),
                committed,
                skipped,
                failed,
                pending,
            }),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{
        copy_file_attempt, copy_file_with_progress, prepare_transfer_inputs,
        transfer_with_progress_blocking, CopyContext, ProgressContext,
    };
    use crate::transfer_staging::staging_path_for;
    use simplefile_core::models::ProgressUpdate;
    use std::fs;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
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
    fn transfer_failure_reports_committed_failed_and_pending_items() {
        let src_dir = unique_temp_path("partial_failure_src");
        let dst_dir = unique_temp_path("partial_failure_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let first = src_dir.join("first.bin");
        let second = src_dir.join("second.txt");
        let third = src_dir.join("third.txt");
        fs::write(&first, vec![7u8; 5 * 1024 * 1024]).unwrap();
        fs::write(&second, b"second").unwrap();
        fs::write(&third, b"third").unwrap();

        let conflict = dst_dir.join("second.txt");
        let created_conflict = Arc::new(AtomicBool::new(false));
        let created_conflict_for_emit = created_conflict.clone();
        let first_text = first.to_string_lossy().to_string();
        let conflict_for_emit = conflict.clone();
        let cancel = Arc::new(AtomicBool::new(false));

        let error = super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: vec![
                    first.to_string_lossy().to_string(),
                    second.to_string_lossy().to_string(),
                    third.to_string_lossy().to_string(),
                ],
                conflict_action: "error".to_string(),
            },
            dst_dir.to_string_lossy().to_string(),
            "op_partial_failure_test".to_string(),
            cancel,
            &|update| {
                if update.current_item == first_text
                    && !created_conflict_for_emit.swap(true, Ordering::Relaxed)
                {
                    fs::write(&conflict_for_emit, b"conflict").unwrap();
                }
            },
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                ..super::tuning::Policy::default()
            },
        )
        .expect_err("second item should be reported after the others commit");

        assert!(error.message.starts_with("CONFLICT:"));
        assert_eq!(error.outcome.operation_id, "op_partial_failure_test");
        assert_eq!(error.outcome.status, "failed");
        // One conflict never stops the rest of the selection.
        assert_eq!(error.outcome.committed.len(), 2);
        assert_eq!(
            error.outcome.committed[0].source,
            first.to_string_lossy().to_string()
        );
        assert_eq!(
            error.outcome.committed[0].destination,
            dst_dir.join("first.bin").to_string_lossy().to_string()
        );
        assert_eq!(
            error.outcome.committed[1].source,
            third.to_string_lossy().to_string()
        );
        assert_eq!(error.outcome.failed.len(), 1);
        assert_eq!(
            error.outcome.failed[0].source,
            second.to_string_lossy().to_string()
        );
        assert_eq!(
            error.outcome.failed[0].destination,
            conflict.to_string_lossy().to_string()
        );
        assert_eq!(error.outcome.failed[0].kind, "conflict");
        assert!(error.outcome.failed[0].error.starts_with("CONFLICT:"));
        assert!(error.outcome.pending.is_empty());
        assert!(dst_dir.join("first.bin").exists());
        assert_eq!(fs::read(dst_dir.join("third.txt")).unwrap(), b"third");
        assert_eq!(fs::read(&conflict).unwrap(), b"conflict");

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn a_populated_tree_merges_into_an_empty_folder_for_copy_and_move() {
        for operation in ["copy", "move"] {
            let root = unique_temp_path(operation);
            let source = root.join("source").join("Photos");
            let destination = root.join("target");
            let target = destination.join("Photos");
            fs::create_dir_all(source.join("nested")).unwrap();
            fs::create_dir_all(&target).unwrap();
            fs::write(source.join("nested").join("a"), b"photo").unwrap();

            let results = transfer_with_progress_blocking(
                operation,
                vec![source.to_string_lossy().into()],
                destination.to_string_lossy().into(),
                super::generate_transfer_operation_id(),
                "error".into(),
                Arc::new(AtomicBool::new(false)),
                &|_| {},
            )
            .unwrap();
            let changes = results[0]
                .changes
                .as_ref()
                .expect("existing root must carry a merge receipt");
            assert_eq!(
                changes.files,
                [std::path::Path::new("nested")
                    .join("a")
                    .to_string_lossy()
                    .into_owned()]
            );
            assert_eq!(changes.created_dirs, ["nested"]);
            assert_eq!(fs::read(target.join("nested").join("a")).unwrap(), b"photo");
            if operation == "move" {
                assert_eq!(changes.removed_source_dirs, ["nested", ""]);
                assert!(!source.exists());
            } else {
                assert!(changes.removed_source_dirs.is_empty());
                assert!(source.is_dir());
            }
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn merging_reports_only_added_files_and_created_directories_for_undo() {
        let root = unique_temp_path("merge_undo");
        let source = root.join("src").join("Photos");
        let destination = root.join("dst");
        let photos = destination.join("Photos");
        fs::create_dir_all(source.join("nested")).unwrap();
        fs::create_dir_all(source.join("empty")).unwrap();
        fs::create_dir_all(photos.join("nested")).unwrap();
        fs::write(source.join("added.jpg"), b"new").unwrap();
        fs::write(source.join("nested").join("same.jpg"), b"incoming").unwrap();
        fs::write(photos.join("nested").join("same.jpg"), b"existing").unwrap();
        fs::write(photos.join("original.jpg"), b"keep").unwrap();

        let failure = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().into()],
            destination.to_string_lossy().into(),
            super::generate_transfer_operation_id(),
            "error".into(),
            Arc::new(AtomicBool::new(false)),
            &|_| {},
        )
        .unwrap_err();
        assert_eq!(failure.outcome.failed.len(), 1);
        assert_eq!(
            failure.outcome.failed[0].destination,
            photos.join("nested").join("same.jpg").to_string_lossy()
        );
        let changes = failure.outcome.committed[0].changes.as_ref().unwrap();
        assert_eq!(changes.files, ["added.jpg"]);
        assert_eq!(changes.created_dirs, ["empty"]);
        assert!(changes.removed_source_dirs.is_empty());
        assert_eq!(fs::read(photos.join("original.jpg")).unwrap(), b"keep");
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn keep_both_retry_honors_the_choice_for_matching_files_and_folders_over_files() {
        let root = unique_temp_path("merge_keep_both");
        let source = root.join("src");
        let destination = root.join("dst");
        fs::create_dir_all(source.join("album")).unwrap();
        fs::create_dir_all(&destination).unwrap();
        fs::write(source.join("album").join("photo"), b"photo").unwrap();
        fs::write(destination.join("album"), b"original").unwrap();
        fs::write(source.join("same"), b"same").unwrap();
        fs::copy(source.join("same"), destination.join("same")).unwrap();
        simplefile_core::file_ops::preserve_basic_metadata(
            &source.join("same"),
            &destination.join("same"),
        )
        .unwrap();

        for name in ["same", "album"] {
            let mut item = retry_item(&source.join(name), &destination.join(name));
            item.action = "keep-both".into();
            let copied = retry_items("copy", vec![item], &destination).unwrap();
            assert_eq!(
                copied[0].destination,
                destination.join(format!("{name} (1)")).to_string_lossy()
            );
        }
        assert_eq!(fs::read(destination.join("album")).unwrap(), b"original");
        assert_eq!(
            fs::read(destination.join("album (1)").join("photo")).unwrap(),
            b"photo"
        );
        fs::remove_dir_all(root).unwrap();
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
    fn replace_refuses_to_put_a_file_over_an_existing_directory() {
        let src_root = unique_temp_path("replace_type_src");
        let dst_root = unique_temp_path("replace_type_dst");
        let source = src_root.join("Repos");
        let existing = dst_root.join("Repos");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(existing.join("docs")).unwrap();
        fs::write(source.join("docs"), b"a file named docs").unwrap();
        fs::write(existing.join("docs").join("keep.txt"), b"keep").unwrap();
        let cancel = Arc::new(AtomicBool::new(false));

        let error = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            "op_replace_type_test".to_string(),
            "replace".to_string(),
            cancel,
            &|_| {},
        )
        .expect_err("a file must not replace a directory of the same name");

        assert!(
            existing.join("docs").is_dir(),
            "destination directory was deleted"
        );
        assert_eq!(
            fs::read(existing.join("docs").join("keep.txt")).unwrap(),
            b"keep"
        );
        assert_eq!(error.outcome.failed.len(), 1, "{:?}", error.outcome);
        assert_eq!(error.outcome.failed[0].kind, "conflict");
        assert_eq!(
            error.outcome.failed[0].destination,
            existing.join("docs").to_string_lossy()
        );

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    #[test]
    fn move_merge_removes_sources_under_new_subfolders() {
        let src_root = unique_temp_path("move_merge_src");
        let dst_root = unique_temp_path("move_merge_dst");
        let source = src_root.join("Proj");
        let existing = dst_root.join("Proj");
        fs::create_dir_all(source.join("new")).unwrap();
        fs::create_dir_all(existing.join("old")).unwrap();
        fs::create_dir_all(source.join("old")).unwrap();
        fs::write(source.join("new").join("fresh.txt"), b"fresh").unwrap();
        fs::write(source.join("old").join("moved.txt"), b"moved").unwrap();
        fs::write(existing.join("old").join("keep.txt"), b"keep").unwrap();
        let cancel = Arc::new(AtomicBool::new(false));

        let result = transfer_with_progress_blocking(
            "move",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            "op_move_merge_test".to_string(),
            "replace".to_string(),
            cancel,
            &|_| {},
        )
        .unwrap_or_else(|error| panic!("merge move failed: {:?}", error.outcome));

        assert_eq!(result.len(), 1);
        assert_eq!(
            fs::read(existing.join("new").join("fresh.txt")).unwrap(),
            b"fresh"
        );
        assert_eq!(
            fs::read(existing.join("old").join("moved.txt")).unwrap(),
            b"moved"
        );
        assert_eq!(
            fs::read(existing.join("old").join("keep.txt")).unwrap(),
            b"keep"
        );
        assert!(
            !source.exists(),
            "move must remove the whole source tree, including new subfolders"
        );

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    #[test]
    fn move_merge_with_skip_keeps_skipped_sources_without_failing() {
        let src_root = unique_temp_path("move_skip_src");
        let dst_root = unique_temp_path("move_skip_dst");
        let source = src_root.join("Proj");
        let existing = dst_root.join("Proj");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&existing).unwrap();
        fs::write(source.join("same.txt"), b"source").unwrap();
        fs::write(source.join("new.txt"), b"new").unwrap();
        fs::write(existing.join("same.txt"), b"destination").unwrap();
        let cancel = Arc::new(AtomicBool::new(false));

        let result = transfer_with_progress_blocking(
            "move",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            "op_move_skip_test".to_string(),
            "skip".to_string(),
            cancel,
            &|_| {},
        )
        .unwrap_or_else(|error| panic!("skipping is a choice, not a failure: {:?}", error.outcome));

        assert_eq!(result.len(), 1);
        assert_eq!(fs::read(existing.join("new.txt")).unwrap(), b"new");
        assert_eq!(fs::read(existing.join("same.txt")).unwrap(), b"destination");
        assert_eq!(
            fs::read(source.join("same.txt")).unwrap(),
            b"source",
            "a skipped source stays where it was"
        );
        assert!(!source.join("new.txt").exists());

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
        .expect_err("cancel must return an explicit terminal outcome");

        assert_eq!(result.outcome.status, "cancelled");
        assert!(result.outcome.committed.is_empty());
        assert!(updates
            .lock()
            .unwrap()
            .iter()
            .any(|update| update.status == "cancelled"));
        assert!(!dst_dir.join("large.txt").exists());

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    fn staging_artifacts_in(dir: &std::path::Path) -> Vec<String> {
        fs::read_dir(dir)
            .map(|entries| {
                entries
                    .flatten()
                    .map(|entry| entry.file_name().to_string_lossy().into_owned())
                    .filter(|name| {
                        name.ends_with(".sumafile-partial")
                            || name.ends_with(".sumafile-replace-backup")
                    })
                    .collect()
            })
            .unwrap_or_default()
    }

    #[test]
    fn inplace_policy_creates_folders_under_their_final_names() {
        let src_dir = unique_temp_path("inplace_dir_src");
        let dst_dir = unique_temp_path("inplace_dir_dst");
        fs::create_dir_all(src_dir.join("Folder").join("nested")).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let file = src_dir.join("Folder").join("nested").join("file.bin");
        fs::write(&file, vec![0x5Au8; 64 * 1024]).unwrap();
        let file_text = file.to_string_lossy().to_string();
        let final_dir = dst_dir.join("Folder");
        let final_nested = final_dir.join("nested");
        let seen = Arc::new(Mutex::new(
            Vec::<(bool, bool, Vec<String>, Vec<String>)>::new(),
        ));
        let seen_target = seen.clone();
        let dst_for_emit = dst_dir.clone();
        let final_for_emit = final_dir.clone();

        super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: vec![src_dir.join("Folder").to_string_lossy().to_string()],
                conflict_action: "error".to_string(),
            },
            dst_dir.to_string_lossy().to_string(),
            "op_inplace_dir_test".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| {
                if update.current_item == file_text {
                    seen_target.lock().unwrap().push((
                        final_for_emit.is_dir(),
                        final_for_emit.join("nested").is_dir(),
                        staging_artifacts_in(&dst_for_emit),
                        staging_artifacts_in(&final_for_emit),
                    ));
                }
            },
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                inplace: true,
                ..super::tuning::Policy::default()
            },
        )
        .expect("in-place folder copy should complete");

        let seen = seen.lock().unwrap();
        assert!(
            !seen.is_empty(),
            "the file inside the folder never reported progress"
        );
        for (root_final, nested_final, root_artifacts, nested_artifacts) in seen.iter() {
            assert!(
                root_final,
                "root folder was not created under its final name"
            );
            assert!(
                nested_final,
                "subfolder was not created under its final name"
            );
            assert!(
                root_artifacts.is_empty(),
                "staged folder in destination: {root_artifacts:?}"
            );
            assert!(
                nested_artifacts.is_empty(),
                "staged subfolder: {nested_artifacts:?}"
            );
        }
        assert_eq!(
            fs::read(final_nested.join("file.bin")).unwrap().len(),
            64 * 1024
        );
        assert!(staging_artifacts_in(&dst_dir).is_empty());

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn inplace_policy_replaces_existing_file_without_staging() {
        let src_dir = unique_temp_path("inplace_replace_src");
        let dst_dir = unique_temp_path("inplace_replace_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("a.txt");
        fs::write(&source, b"source").unwrap();
        let destination = dst_dir.join("a.txt");
        fs::write(&destination, b"original").unwrap();
        let artifacts = Arc::new(Mutex::new(Vec::<String>::new()));
        let artifacts_target = artifacts.clone();
        let finalizing_seen = Arc::new(AtomicBool::new(false));
        let finalizing_target = finalizing_seen.clone();
        let dst_for_emit = dst_dir.clone();

        super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: vec![source.to_string_lossy().to_string()],
                conflict_action: "replace".to_string(),
            },
            dst_dir.to_string_lossy().to_string(),
            "op_inplace_replace_test".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| {
                if update.status == "finalizing" {
                    finalizing_target.store(true, Ordering::Relaxed);
                }
                artifacts_target
                    .lock()
                    .unwrap()
                    .extend(staging_artifacts_in(&dst_for_emit));
            },
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                inplace: true,
                ..super::tuning::Policy::default()
            },
        )
        .expect("in-place replace should complete");

        assert!(
            finalizing_seen.load(Ordering::Relaxed),
            "no finalizing update was observed"
        );
        let artifacts = artifacts.lock().unwrap();
        assert!(
            artifacts.is_empty(),
            "replace used staging artifacts: {artifacts:?}"
        );
        assert_eq!(fs::read(&destination).unwrap(), b"source");
        assert_eq!(fs::read_dir(&dst_dir).unwrap().count(), 1);

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    /// "Write directly" with Replace empties the existing file when the copy
    /// starts and keeps no backup. A source that cannot even be opened used
    /// to cost the existing file all the same: it was emptied, then deleted.
    #[test]
    fn inplace_replace_keeps_the_existing_file_when_the_source_cannot_be_opened() {
        let src_dir = unique_temp_path("inplace_unopenable_src");
        let dst_dir = unique_temp_path("inplace_unopenable_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("report.txt");
        let destination = dst_dir.join("report.txt");
        fs::write(&destination, b"existing").unwrap();
        // On Windows another program holds the source and shares nothing.
        #[cfg(windows)]
        let _unopenable = {
            use std::os::windows::fs::OpenOptionsExt;
            fs::write(&source, b"incoming").unwrap();
            fs::OpenOptions::new()
                .read(true)
                .share_mode(0)
                .open(&source)
                .unwrap()
        };
        // Elsewhere a socket stands in: it exists and cannot be opened.
        #[cfg(unix)]
        let _unopenable = std::os::unix::net::UnixListener::bind(&source).unwrap();

        let error = super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: vec![source.to_string_lossy().to_string()],
                conflict_action: "replace".to_string(),
            },
            dst_dir.to_string_lossy().to_string(),
            "op_inplace_unopenable_source".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|_| {},
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                inplace: true,
                retry_sleep_ms: 10,
                ..super::tuning::Policy::default()
            },
        )
        .expect_err("a source that cannot be opened is not copied");

        assert_eq!(error.outcome.failed.len(), 1);
        assert!(error.message.contains("Open source"), "{}", error.message);
        assert_eq!(fs::read(&destination).unwrap(), b"existing");
        assert_eq!(
            fs::read_dir(&dst_dir).unwrap().count(),
            1,
            "nothing else was left in the destination folder"
        );

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
        assert!(!fs::read_dir(&dst_dir).unwrap().any(|entry| {
            entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .contains("sumafile-partial")
        }));
        let updates = updates.lock().unwrap();
        assert_eq!(updates[0].current_item, "Preparing transfer...");
        assert!(!updates
            .iter()
            .any(|update| update.current_item == "Calculating size..."));
        let completed = updates
            .iter()
            .find(|update| update.status == "completed")
            .expect("completed progress update");
        assert_eq!(completed.current_files, 1);
        assert_eq!(completed.total_files, 1);
        let finalizing_index = updates
            .iter()
            .position(|update| update.status == "finalizing")
            .expect("finalizing progress update");
        let completed_index = updates
            .iter()
            .position(|update| update.status == "completed")
            .expect("completed progress update");
        assert!(finalizing_index < completed_index);
        assert_eq!(
            updates[finalizing_index].current,
            updates[finalizing_index].total
        );
        assert!(updates[finalizing_index].current_item.ends_with("done.txt"));

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn replace_file_uses_completed_staged_copy() {
        let src_dir = unique_temp_path("replace_file_src");
        let dst_dir = unique_temp_path("replace_file_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("same.txt");
        let destination = dst_dir.join("same.txt");
        fs::write(&source, b"source").unwrap();
        fs::write(&destination, b"destination").unwrap();
        let cancel = Arc::new(AtomicBool::new(false));

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "op_replace_file_test".to_string(),
            "replace".to_string(),
            cancel,
            &|_| {},
        )
        .expect("replace copy should complete");

        assert_eq!(result.len(), 1);
        assert_eq!(fs::read(destination).unwrap(), b"source");
        assert!(!fs::read_dir(&dst_dir).unwrap().any(|entry| {
            entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .contains("sumafile-partial")
        }));

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn copy_file_cancellation_removes_staged_partial() {
        let src_dir = unique_temp_path("stage_cancel_src");
        let dst_dir = unique_temp_path("stage_cancel_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("large.txt");
        let destination = dst_dir.join("large.txt");
        fs::write(&source, b"complete content").unwrap();

        let operation_id = "op_stage_cancel_test";
        let staged = staging_path_for(&destination, operation_id).unwrap();
        let saw_owned_staging = AtomicBool::new(false);

        let total_bytes = Arc::new(AtomicU64::new(0));
        let total_files = Arc::new(AtomicU64::new(0));
        let completed_files = Arc::new(AtomicU64::new(0));
        let cancel = Arc::new(AtomicBool::new(false));
        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();
        let emit = |update: ProgressUpdate| {
            if update.status == "finalizing" {
                saw_owned_staging.store(staged.exists(), Ordering::Relaxed);
                cancel.store(true, Ordering::Relaxed);
            }
            updates_target.lock().unwrap().push(update);
        };
        let progress = ProgressContext {
            emit: &emit,
            operation_id,
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total_bytes,
            total_files: &total_files,
            completed_files: &completed_files,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
        };
        let ctx = CopyContext {
            progress: &progress,
            operation_id,
            network: false,
            resume_existing: false,
            policy: super::Policy::default(),
        };
        let mut completed_bytes = 0;

        let error =
            copy_file_with_progress(&source, &destination, &ctx, &mut completed_bytes, false)
                .expect_err("cancelled copy should fail");

        assert_eq!(error, "Operation cancelled");
        assert!(saw_owned_staging.load(Ordering::Relaxed));
        assert!(!staged.exists());
        assert!(!destination.exists());

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn copy_file_attempt_resumes_existing_partial_file() {
        let src_dir = unique_temp_path("resume_src");
        let dst_dir = unique_temp_path("resume_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("data.bin");
        let destination = dst_dir.join("data.bin.partial");
        fs::write(&source, b"abcdef").unwrap();
        fs::write(&destination, b"abc").unwrap();

        let operation_id = "op_resume_test";
        let total_bytes = Arc::new(AtomicU64::new(6));
        let total_files = Arc::new(AtomicU64::new(1));
        let completed_files = Arc::new(AtomicU64::new(0));
        let cancel = Arc::new(AtomicBool::new(false));
        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();
        let emit = |update| updates_target.lock().unwrap().push(update);
        let progress = ProgressContext {
            emit: &emit,
            operation_id,
            operation_type: "copy",
            cancel: &cancel,
            total_bytes: &total_bytes,
            total_files: &total_files,
            completed_files: &completed_files,
            last_running_emit: Mutex::new(Instant::now()),
            retry_count: None,
            rate_limit: None,
            write_latency: None,
        };
        let ctx = CopyContext {
            progress: &progress,
            operation_id,
            network: true,
            resume_existing: false,
            policy: super::Policy::default(),
        };

        for partial in [b"".as_slice(), b"abc".as_slice(), b"abcdef".as_slice()] {
            fs::write(&destination, partial).unwrap();
            let copied = copy_file_attempt(&source, &destination, &ctx, 0, 2, 6)
                .expect("empty, partial and complete operation-owned files should resume");
            assert_eq!(copied, 6);
            assert_eq!(fs::read(&destination).unwrap(), b"abcdef");
        }
        fs::write(&source, b"").unwrap();
        fs::write(&destination, b"").unwrap();
        assert_eq!(
            copy_file_attempt(&source, &destination, &ctx, 0, 2, 0).unwrap(),
            0
        );

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[cfg(windows)]
    #[test]
    fn storage_pressure_recovers_or_cancels_files_folders_and_moves() {
        use std::os::windows::fs::OpenOptionsExt;
        for directory in [false, true] {
            for moving in [false, true] {
                for cancelling in [false, true] {
                    let root = unique_temp_path("storage_pressure");
                    let src_root = root.join("src");
                    let dst_root = root.join("dst");
                    fs::create_dir_all(&src_root).unwrap();
                    fs::create_dir_all(&dst_root).unwrap();
                    let source = src_root.join(if directory { "album" } else { "photo.bin" });
                    let source_file = if directory {
                        fs::create_dir(&source).unwrap();
                        source.join("photo.bin")
                    } else {
                        source.clone()
                    };
                    let bytes: Vec<u8> = (0..65539).map(|n| (n % 251) as u8).collect();
                    fs::write(&source_file, &bytes).unwrap();
                    let operation_id = super::generate_transfer_operation_id();
                    let target = dst_root.join(source.file_name().unwrap());
                    let final_file = if directory {
                        target.join("photo.bin")
                    } else {
                        target.clone()
                    };
                    let staged =
                        crate::transfer_staging::staging_path_for(&final_file, &operation_id)
                            .unwrap();
                    if directory {
                        fs::create_dir_all(&target).unwrap();
                    }
                    fs::write(&final_file, b"original").unwrap();
                    // Lock the final destination, not an unowned temporary file.
                    let held = Mutex::new(Some(
                        fs::OpenOptions::new()
                            .read(true)
                            .share_mode(1)
                            .open(&final_file)
                            .unwrap(),
                    ));
                    let cancel = Arc::new(AtomicBool::new(false));
                    let waiting = AtomicBool::new(false);
                    let emit = |_: super::ProgressUpdate| {};
                    let tuning = |update: super::TuningUpdate| {
                        if update.explanation.starts_with("Retrying failed I/O") {
                            waiting.store(true, Ordering::Relaxed);
                            held.lock().unwrap().take();
                            assert_eq!(update.concurrency, 4);
                            if cancelling {
                                cancel.store(true, Ordering::Relaxed);
                            }
                        }
                    };
                    let result = super::transfer_with_policy_blocking(
                        if moving { "move" } else { "copy" },
                        super::Request::Selection {
                            sources: vec![source.to_string_lossy().into_owned()],
                            conflict_action: "replace".into(),
                        },
                        dst_root.to_string_lossy().into_owned(),
                        operation_id,
                        cancel.clone(),
                        &emit,
                        &tuning,
                        &|| super::Policy {
                            mode: super::tuning::Mode::Manual,
                            manual_concurrency: 4,
                            ..super::Policy::default()
                        },
                    );
                    assert!(
                        waiting.load(Ordering::Relaxed),
                        "No storage wait was reported"
                    );
                    if cancelling {
                        assert!(cancel.load(Ordering::Relaxed));
                        assert_eq!(result.unwrap_err().outcome.status, "cancelled");
                        assert_eq!(fs::read(&source_file).unwrap(), bytes);
                        assert_eq!(fs::read(&final_file).unwrap(), b"original");
                    } else {
                        result.unwrap();
                        let final_file = if directory {
                            target.join("photo.bin")
                        } else {
                            target.clone()
                        };
                        assert_eq!(fs::read(final_file).unwrap(), bytes);
                        assert_eq!(source.exists(), !moving);
                    }
                    assert!(!staged.exists());
                    fs::remove_dir_all(&root).unwrap();
                }
            }
        }
    }

    #[test]
    fn directory_copy_recovers_recorded_interruption_without_trusting_completed_names() {
        let src_root = unique_temp_path("resume_dir_src");
        let dst_root = unique_temp_path("resume_dir_dst");
        let source = src_root.join("Album");
        let final_destination = dst_root.join("Album");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dst_root).unwrap();
        let existing_source = source.join("one.txt");
        let missing_source = source.join("two.txt");
        fs::write(&existing_source, b"one").unwrap();
        fs::write(&missing_source, b"two").unwrap();

        let operation_id = "op_resume_dir_test";
        let staging =
            crate::transfer_staging::staging_path_for(&final_destination, operation_id).unwrap();
        fs::create_dir_all(&staging).unwrap();
        let lease = crate::transfer_journal::StagingLease::record_created(
            &source.to_string_lossy(),
            &final_destination,
            &staging,
        )
        .unwrap();
        let staged_existing = staging.join("one.txt");
        fs::copy(&existing_source, &staged_existing).unwrap();
        simplefile_core::file_ops::preserve_basic_metadata(&existing_source, &staged_existing)
            .unwrap();
        let staged_existing_modified = fs::metadata(&staged_existing).unwrap().modified().unwrap();
        drop(lease); // Simulate an interrupted process.
        let cancel = Arc::new(AtomicBool::new(false));

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            operation_id.to_string(),
            "error".to_string(),
            cancel,
            &|_| {},
        )
        .expect("recorded abandoned staging should be cleaned and copied afresh");

        assert_eq!(result.len(), 1);
        assert_eq!(fs::read(final_destination.join("one.txt")).unwrap(), b"one");
        assert_eq!(fs::read(final_destination.join("two.txt")).unwrap(), b"two");
        assert!(!staging.exists());
        assert_eq!(
            fs::metadata(final_destination.join("one.txt"))
                .unwrap()
                .modified()
                .unwrap(),
            staged_existing_modified
        );

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    #[test]
    fn new_directories_are_never_built_in_a_hidden_staging_folder() {
        let root = unique_temp_path("no_staged_dirs");
        let source = root.join("source");
        let destination = root.join("destination");
        fs::create_dir_all(source.join("one/two/three")).unwrap();
        fs::create_dir_all(&destination).unwrap();
        let file = source.join("one/two/three/file.bin");
        fs::write(&file, vec![7u8; 64 * 1024]).unwrap();
        let final_destination = destination.join("source");
        let staged_dirs = |dir: &std::path::Path| -> Vec<String> {
            staging_artifacts_in(dir)
                .into_iter()
                .filter(|name| dir.join(name).is_dir())
                .collect()
        };

        transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            destination.to_string_lossy().to_string(),
            "op_no_staged_dirs".to_string(),
            "error".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| {
                let staged = staged_dirs(&destination);
                assert!(staged.is_empty(), "staged folder: {staged:?}");
                if update.status == "finalizing" {
                    assert!(final_destination.join("one/two/three").is_dir());
                }
            },
        )
        .unwrap();

        assert_eq!(
            fs::read(final_destination.join("one/two/three/file.bin"))
                .unwrap()
                .len(),
            64 * 1024
        );
        assert!(staging_artifacts_in(&destination).is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    /// A failure inside a new folder names the path the file was headed for,
    /// so a targeted retry writes it where the user expects it.
    #[test]
    fn failure_inside_a_new_directory_reports_its_final_destination() {
        let root = unique_temp_path("failure_final_destination");
        let source = root.join("source");
        let destination = root.join("destination");
        fs::create_dir_all(source.join("nested")).unwrap();
        fs::create_dir_all(&destination).unwrap();
        fs::write(source.join("nested").join("good.txt"), b"good").unwrap();
        // A name longer than the staging suffix leaves room for: the hidden
        // partial file cannot be created, so this one file fails.
        let long_name = format!("{}.txt", "n".repeat(240));
        fs::write(source.join("nested").join(&long_name), b"long").unwrap();
        let expected = destination.join("source").join("nested").join(&long_name);

        let error = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            destination.to_string_lossy().to_string(),
            "op_failure_final_destination".to_string(),
            "error".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|_| {},
        )
        .expect_err("the over-long name cannot be staged");

        let failed: Vec<_> = error
            .outcome
            .failed
            .iter()
            .map(|failure| failure.destination.clone())
            .collect();
        assert_eq!(failed, vec![expected.to_string_lossy().to_string()]);
        assert_eq!(
            fs::read(destination.join("source").join("nested").join("good.txt")).unwrap(),
            b"good"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn same_volume_directory_move_is_one_rename() {
        let src_root = unique_temp_path("dir_rename_src");
        let dst_root = unique_temp_path("dir_rename_dst");
        let source = src_root.join("Movie");
        let final_destination = dst_root.join("Movie");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dst_root).unwrap();
        fs::write(source.join("feature.mkv"), b"feature").unwrap();
        let operation_id = "op_dir_rename_test";
        let staging = staging_path_for(&final_destination, operation_id).unwrap();
        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();

        let result = transfer_with_progress_blocking(
            "move",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            operation_id.to_string(),
            "keep-both".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| updates_target.lock().unwrap().push(update),
        )
        .expect("same-volume directory move renames");

        assert_eq!(result.len(), 1);
        assert_eq!(result[0].destination, final_destination.to_string_lossy());
        assert!(!source.exists());
        assert!(!staging.exists());
        assert_eq!(
            fs::read(final_destination.join("feature.mkv")).unwrap(),
            b"feature"
        );
        assert!(updates
            .lock()
            .unwrap()
            .iter()
            .any(|update| update.status == "completed"));

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    /// A program that holds a file and lets others rename or delete it, but
    /// not read it, does not stop a same-drive move: the rename is the move.
    /// Opening the renamed file afterwards reported it as failed.
    #[cfg(windows)]
    #[test]
    fn a_file_that_can_be_renamed_but_not_read_is_moved_by_rename() {
        use std::os::windows::fs::OpenOptionsExt;
        const FILE_SHARE_DELETE: u32 = 0x0000_0004;

        let src_dir = unique_temp_path("rename_held_src");
        let dst_dir = unique_temp_path("rename_held_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("held.bin");
        fs::write(&source, b"held").unwrap();
        let holder = fs::OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_DELETE)
            .open(&source)
            .unwrap();

        let results = transfer_with_progress_blocking(
            "move",
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "op_rename_held_file".to_string(),
            "error".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|_| {},
        )
        .expect("the file was renamed, so it was moved");

        assert_eq!(results.len(), 1);
        assert_eq!(
            results[0].destination,
            dst_dir.join("held.bin").to_string_lossy()
        );
        assert!(!source.exists());
        drop(holder);
        assert_eq!(fs::read(dst_dir.join("held.bin")).unwrap(), b"held");

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    /// A file that another program holds open cannot be renamed, and could
    /// not be deleted after a copy either. A same-drive move of it used to
    /// copy the whole file, fail to delete the source, and leave both.
    #[cfg(windows)]
    #[test]
    fn a_same_drive_move_of_a_file_in_use_reports_it_busy_without_copying_it() {
        use std::os::windows::fs::OpenOptionsExt;
        const FILE_SHARE_READ: u32 = 0x0000_0001;

        let src_dir = unique_temp_path("rename_busy_src");
        let dst_dir = unique_temp_path("rename_busy_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("open-elsewhere.bin");
        fs::write(&source, b"in use").unwrap();
        // Others may read the file; nobody may rename or delete it.
        let holder = fs::OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .open(&source)
            .unwrap();

        let error = transfer_with_progress_blocking(
            "move",
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "op_rename_busy_file".to_string(),
            "error".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|_| {},
        )
        .expect_err("a file in use cannot be moved");

        assert_eq!(error.outcome.failed.len(), 1);
        assert_eq!(error.outcome.failed[0].source, source.to_string_lossy());
        assert_eq!(error.outcome.failed[0].kind, "error");
        assert!(
            !error.message.contains("Copied but failed to delete source"),
            "{}",
            error.message
        );
        assert!(
            !dst_dir.join("open-elsewhere.bin").exists(),
            "the file was copied although its source could not be removed"
        );
        assert!(staging_artifacts_in(&dst_dir).is_empty());
        drop(holder);
        assert_eq!(fs::read(&source).unwrap(), b"in use");

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    /// With one file at a time nothing is in flight between two files. That
    /// used to force a tuning update after every file: one event, serialized
    /// and sent to the window, per copied file.
    #[test]
    fn one_file_at_a_time_does_not_send_a_tuning_update_for_every_file() {
        let src_root = unique_temp_path("tuning_flood_src");
        let dst_root = unique_temp_path("tuning_flood_dst");
        let source = src_root.join("batch");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dst_root).unwrap();
        let files = 400;
        for index in 0..files {
            fs::write(source.join(format!("file-{index:03}.txt")), b"tiny").unwrap();
        }
        let tuning_updates = AtomicU64::new(0);

        let results = super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: vec![source.to_string_lossy().to_string()],
                conflict_action: "error".to_string(),
            },
            dst_root.to_string_lossy().to_string(),
            "op_tuning_flood".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|_| {},
            &|_| {
                tuning_updates.fetch_add(1, Ordering::Relaxed);
            },
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                flush_on_close: false,
                ..super::tuning::Policy::default()
            },
        )
        .expect("the folder copies");

        assert_eq!(results.len(), 1);
        assert_eq!(fs::read_dir(dst_root.join("batch")).unwrap().count(), files);
        let sent = tuning_updates.load(Ordering::Relaxed);
        assert!(sent >= 1, "the final state is always shown");
        // Updates are spaced 200 ms apart; this copy would have to take
        // forty seconds to send this many.
        assert!(
            sent <= (files / 2) as u64,
            "{sent} tuning updates for {files} files"
        );

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    #[test]
    fn cancelled_directory_copy_keeps_the_files_already_copied() {
        let src_root = unique_temp_path("cancel_keeps_src");
        let dst_root = unique_temp_path("cancel_keeps_dst");
        let source = src_root.join("Album");
        let final_destination = dst_root.join("Album");
        fs::create_dir_all(source.join("disc")).unwrap();
        fs::create_dir_all(&dst_root).unwrap();
        for index in 0..24u8 {
            fs::write(
                source.join("disc").join(format!("{index:02}.bin")),
                vec![index; 64 * 1024],
            )
            .unwrap();
        }
        let cancel = Arc::new(AtomicBool::new(false));
        let cancel_for_emit = cancel.clone();
        let saw_cancelled = AtomicBool::new(false);

        let result = super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: vec![source.to_string_lossy().to_string()],
                conflict_action: "error".to_string(),
            },
            dst_root.to_string_lossy().to_string(),
            "op_cancel_keeps_test".to_string(),
            cancel,
            &|update| {
                if update.current_files >= 1 {
                    cancel_for_emit.store(true, Ordering::Relaxed);
                }
                if update.status == "cancelled" {
                    saw_cancelled.store(true, Ordering::Relaxed);
                }
            },
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                ..super::tuning::Policy::default()
            },
        )
        .expect_err("cancelled directory copy must return its terminal outcome");

        assert_eq!(result.outcome.status, "cancelled");
        assert!(saw_cancelled.load(Ordering::Relaxed));
        // The folder is incomplete, so it is still pending, not committed.
        assert!(result.outcome.committed.is_empty());
        let kept: Vec<_> = fs::read_dir(final_destination.join("disc"))
            .expect("the partly copied folder stays under its final name")
            .flatten()
            .map(|entry| entry.file_name().to_string_lossy().into_owned())
            .collect();
        assert!(!kept.is_empty(), "no copied file survived the cancellation");
        for name in &kept {
            // Whole files only: an interrupted partial is not left behind.
            assert!(!name.contains("sumafile-partial"), "left over: {name}");
            let index: u8 = name.trim_end_matches(".bin").parse().unwrap();
            assert_eq!(
                fs::read(final_destination.join("disc").join(name)).unwrap(),
                vec![index; 64 * 1024]
            );
        }
        assert!(staging_artifacts_in(&dst_root).is_empty());
        assert_eq!(fs::read_dir(source.join("disc")).unwrap().count(), 24);

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    fn retry_items(
        operation: &'static str,
        items: Vec<super::TransferItem>,
        destination: &std::path::Path,
    ) -> Result<Vec<super::TransferResult>, super::TransferBatchError> {
        super::transfer_with_policy_blocking(
            operation,
            super::Request::Items(items),
            destination.to_string_lossy().into(),
            super::generate_transfer_operation_id(),
            Arc::new(AtomicBool::new(false)),
            &|_| {},
            &|_| {},
            &super::tuning::Policy::default,
        )
    }

    fn retry_item(source: &std::path::Path, destination: &std::path::Path) -> super::TransferItem {
        super::TransferItem {
            source: source.to_string_lossy().into(),
            destination: destination.to_string_lossy().into(),
            action: "error".into(),
        }
    }

    /// A failed folder is retried together with failed files: the folder is
    /// walked again and only what is missing at the destination is copied.
    #[test]
    fn retry_accepts_a_directory_and_copies_only_what_is_missing() {
        let root = unique_temp_path("retry_directory");
        let source = root.join("source");
        let destination = root.join("destination");
        fs::create_dir_all(source.join("Folder").join("sub")).unwrap();
        fs::create_dir_all(destination.join("Folder")).unwrap();
        fs::write(source.join("Folder").join("a.txt"), b"already there").unwrap();
        fs::write(source.join("Folder").join("b.txt"), b"missing").unwrap();
        fs::write(source.join("Folder").join("sub").join("c.txt"), b"nested").unwrap();
        fs::write(source.join("loose.txt"), b"loose").unwrap();
        let copied = destination.join("Folder").join("a.txt");
        fs::copy(source.join("Folder").join("a.txt"), &copied).unwrap();
        simplefile_core::file_ops::preserve_basic_metadata(
            &source.join("Folder").join("a.txt"),
            &copied,
        )
        .unwrap();

        retry_items(
            "copy",
            vec![
                retry_item(&source.join("Folder"), &destination.join("Folder")),
                retry_item(&source.join("loose.txt"), &destination.join("loose.txt")),
            ],
            &destination,
        )
        .expect("a directory among the retry items must not fail the retry");

        assert_eq!(fs::read(&copied).unwrap(), b"already there");
        assert_eq!(
            fs::read(destination.join("Folder").join("b.txt")).unwrap(),
            b"missing"
        );
        assert_eq!(
            fs::read(destination.join("Folder").join("sub").join("c.txt")).unwrap(),
            b"nested"
        );
        assert_eq!(fs::read(destination.join("loose.txt")).unwrap(), b"loose");
        fs::remove_dir_all(root).unwrap();
    }

    /// A move that copied a file but could not delete its source is finished
    /// by the retry instead of colliding with its own copy.
    #[test]
    fn retry_finishes_a_move_whose_destination_already_holds_the_file() {
        let root = unique_temp_path("retry_identical");
        let source = root.join("source");
        let destination = root.join("destination");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&destination).unwrap();
        let file = source.join("file.bin");
        fs::write(&file, vec![3u8; 4096]).unwrap();
        let copied = destination.join("file.bin");
        fs::copy(&file, &copied).unwrap();
        simplefile_core::file_ops::preserve_basic_metadata(&file, &copied).unwrap();

        let results = retry_items("move", vec![retry_item(&file, &copied)], &destination)
            .expect("an identical destination is the retried file itself");

        assert_eq!(results.len(), 1);
        assert_eq!(results[0].destination, copied.to_string_lossy());
        assert!(!file.exists(), "the retried move left its source behind");
        assert_eq!(fs::read(&copied).unwrap(), vec![3u8; 4096]);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn retry_still_reports_a_conflict_for_a_different_destination() {
        let root = unique_temp_path("retry_conflict");
        let source = root.join("source");
        let destination = root.join("destination");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&destination).unwrap();
        let file = source.join("file.txt");
        fs::write(&file, b"new content").unwrap();
        let existing = destination.join("file.txt");
        fs::write(&existing, b"someone else's file").unwrap();

        let error = retry_items("copy", vec![retry_item(&file, &existing)], &destination)
            .expect_err("a different file at the destination is a conflict");

        assert_eq!(error.outcome.failed.len(), 1);
        assert!(error.outcome.failed[0].is_conflict());
        assert_eq!(fs::read(&existing).unwrap(), b"someone else's file");
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn concurrent_failure_partitions_every_selection_and_preserves_conflicting_data() {
        let source = unique_temp_path("concurrent_failure_src");
        let destination = unique_temp_path("concurrent_failure_dst");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&destination).unwrap();
        let sources: Vec<_> = (0..8)
            .map(|index| {
                let path = source.join(format!("{index}.bin"));
                fs::write(&path, vec![index as u8; 65536]).unwrap();
                path.to_string_lossy().into_owned()
            })
            .collect();
        let conflict = destination.join("1.bin");
        let error = super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: sources.clone(),
                conflict_action: "error".into(),
            },
            destination.to_string_lossy().into(),
            "concurrent-failure".into(),
            Arc::new(AtomicBool::new(false)),
            &|update| {
                if update.current_item == "Preparing transfer..." {
                    fs::write(&conflict, b"keep existing").unwrap();
                }
            },
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 3,
                ..super::tuning::Policy::default()
            },
        )
        .expect_err("conflict must fail safely");
        let reported: Vec<_> = error
            .outcome
            .committed
            .iter()
            .map(|item| item.source.clone())
            .chain(error.outcome.failed.iter().map(|item| item.source.clone()))
            .chain(error.outcome.pending.clone())
            .collect();
        assert_eq!(reported.len(), sources.len());
        assert_eq!(
            reported.iter().collect::<std::collections::HashSet<_>>(),
            sources.iter().collect()
        );
        assert_eq!(fs::read(&conflict).unwrap(), b"keep existing");
        for item in error.outcome.committed {
            assert_eq!(
                fs::read(item.source).unwrap(),
                fs::read(item.destination).unwrap()
            );
        }
        assert!(!fs::read_dir(&destination).unwrap().any(|entry| entry
            .unwrap()
            .file_name()
            .to_string_lossy()
            .contains("sumafile-partial")));
        fs::remove_dir_all(&source).unwrap();
        fs::remove_dir_all(&destination).unwrap();
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
            retry_count: None,
            rate_limit: None,
            write_latency: None,
        };

        progress.emit_with_total(1, 100, "first".to_string(), "running", None);
        progress.emit_with_total(100, 100, String::new(), "completed", None);

        let updates = updates.lock().unwrap();
        assert_eq!(updates.len(), 1);
        assert_eq!(updates[0].status, "completed");
    }

    #[test]
    fn destination_created_immediately_before_promotion_is_preserved_unless_replace_authorized() {
        let src_dir = unique_temp_path("atomic_promo_src");
        let dst_dir = unique_temp_path("atomic_promo_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let source = src_dir.join("report.txt");
        let destination = dst_dir.join("report.txt");
        fs::write(&source, b"incoming-content").unwrap();

        let destination_for_emit = destination.clone();
        let created = Arc::new(AtomicBool::new(false));
        let created_for_emit = created.clone();

        let err = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_dir.to_string_lossy().to_string(),
            "op_atomic_no_overwrite".to_string(),
            "error".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| {
                if update.status == "finalizing" && !created_for_emit.swap(true, Ordering::SeqCst) {
                    fs::write(&destination_for_emit, b"concurrent-destination").unwrap();
                }
            },
        )
        .expect_err("concurrent destination creation must report conflict");

        assert!(err.message.starts_with("CONFLICT:"));
        assert_eq!(fs::read(&destination).unwrap(), b"concurrent-destination");

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn folder_replacing_existing_file_is_a_type_conflict() {
        for operation in ["copy", "move"] {
            let src_root = unique_temp_path("folder_replace_file_src");
            let dst_root = unique_temp_path("folder_replace_file_dst");
            let source_folder = src_root.join("Item");
            let existing_dest_file = dst_root.join("Item");
            fs::create_dir_all(&source_folder).unwrap();
            fs::create_dir_all(&dst_root).unwrap();
            fs::write(source_folder.join("child.txt"), vec![b'a'; 256 * 1024]).unwrap();
            fs::write(&existing_dest_file, b"original-file-must-survive").unwrap();

            let result = transfer_with_progress_blocking(
                operation,
                vec![source_folder.to_string_lossy().to_string()],
                dst_root.to_string_lossy().to_string(),
                "op_folder_replaces_file".to_string(),
                "replace".to_string(),
                Arc::new(AtomicBool::new(false)),
                &|_| {},
            )
            .expect_err("a directory never replaces a file silently");

            assert_eq!(result.outcome.status, "failed");
            assert!(
                result.message.starts_with("CONFLICT:"),
                "{}",
                result.message
            );
            assert!(result.outcome.committed.is_empty());
            assert_eq!(result.outcome.failed.len(), 1);
            assert_eq!(result.outcome.failed[0].kind, "conflict");
            assert_eq!(
                result.outcome.failed[0].source,
                source_folder.to_string_lossy().to_string()
            );
            assert!(
                existing_dest_file.is_file(),
                "original destination file must remain a file"
            );
            assert_eq!(
                fs::read(&existing_dest_file).unwrap(),
                b"original-file-must-survive"
            );
            assert!(source_folder.join("child.txt").exists());

            let _ = fs::remove_dir_all(&src_root);
            let _ = fs::remove_dir_all(&dst_root);
        }
    }

    #[test]
    fn streamed_move_deletes_each_source_after_publication_and_leaves_later_writes() {
        let src_dir = unique_temp_path("move_mod_src");
        let dst_dir = unique_temp_path("move_mod_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();

        let first = src_dir.join("first.txt");
        let second = src_dir.join("second.txt");
        fs::write(&first, b"original-first").unwrap();
        fs::write(&second, vec![b's'; 512 * 1024]).unwrap();

        let first_for_emit = first.clone();
        let first_deleted_before_second_finalized = Arc::new(AtomicBool::new(false));
        let observed = first_deleted_before_second_finalized.clone();
        let modified = Arc::new(AtomicBool::new(false));
        let modified_for_emit = modified.clone();

        // A verbatim destination prefix forces streaming (copy + verified delete).
        let results = super::transfer_with_policy_blocking(
            "move",
            super::Request::Selection {
                sources: vec![
                    first.to_string_lossy().to_string(),
                    second.to_string_lossy().to_string(),
                ],
                conflict_action: "error".to_string(),
            },
            format!(r"\\?\{}", dst_dir.to_string_lossy()),
            "op_move_modified_source".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| {
                // When the second file is finalizing, the first has already been
                // published and its source deleted; a new file written at the
                // old name belongs to someone else.
                if update.status == "finalizing"
                    && update.current_item.ends_with("second.txt")
                    && !modified_for_emit.swap(true, Ordering::SeqCst)
                {
                    observed.store(!first_for_emit.exists(), Ordering::SeqCst);
                    fs::write(&first_for_emit, b"updated--first").unwrap();
                }
            },
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                ..super::tuning::Policy::default()
            },
        )
        .expect("both files move; the later write is not this operation's source");

        assert!(
            modified.load(Ordering::SeqCst),
            "finalizing hook did not run"
        );
        assert!(
            first_deleted_before_second_finalized.load(Ordering::SeqCst),
            "first source should be deleted right after it is published"
        );
        assert_eq!(results.len(), 2);
        assert_eq!(fs::read(&first).unwrap(), b"updated--first");
        assert_eq!(
            fs::read(dst_dir.join("first.txt")).unwrap(),
            b"original-first"
        );
        assert!(!second.exists());

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn folder_move_retains_uncopied_file_added_after_enumeration() {
        let src_root = unique_temp_path("folder_move_added_src");
        let dst_root = unique_temp_path("folder_move_added_dst");
        let source_folder = src_root.join("Project");
        let final_dest = dst_root.join("Project");
        fs::create_dir_all(&source_folder).unwrap();
        fs::create_dir_all(&dst_root).unwrap();
        let existing_file = source_folder.join("copied.txt");
        fs::write(&existing_file, b"copied-payload").unwrap();

        let new_file = source_folder.join("added_after_enum.txt");
        let new_file_for_emit = new_file.clone();
        // A verbatim destination prefix forces a staged tree copy + verified
        // tree deletion instead of a same-volume rename.
        let verbatim_root = format!(r"\\?\{}", dst_root.to_string_lossy());
        let final_dest_text = format!(r"{verbatim_root}\Project");
        let injected = Arc::new(AtomicBool::new(false));
        let injected_for_emit = injected.clone();

        let err = transfer_with_progress_blocking(
            "move",
            vec![source_folder.to_string_lossy().to_string()],
            verbatim_root.clone(),
            "op_folder_move_added_file".to_string(),
            "keep-both".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| {
                if update.status == "finalizing"
                    && update.current_item == final_dest_text
                    && !injected_for_emit.swap(true, Ordering::SeqCst)
                {
                    fs::write(&new_file_for_emit, b"uncopied-new-data").unwrap();
                }
            },
        )
        .expect_err(
            "folder move must report partial move when new file is added after enumeration",
        );

        assert!(
            err.message.contains("Copied but failed to delete source"),
            "unexpected error: {}",
            err.message
        );
        assert!(!existing_file.exists(), "copied file should be deleted");
        assert!(new_file.exists(), "uncopied added file must be retained");
        assert_eq!(fs::read(&new_file).unwrap(), b"uncopied-new-data");
        assert_eq!(
            fs::read(final_dest.join("copied.txt")).unwrap(),
            b"copied-payload"
        );

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    #[test]
    fn directory_transfer_preserves_unowned_legacy_partials() {
        let src_root = unique_temp_path("megapack_src");
        let dst_root = unique_temp_path("megapack_dst");
        let source = src_root.join("Megapack");
        let final_dest = dst_root.join("Megapack");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dst_root).unwrap();
        fs::write(source.join("a.bin"), b"alpha").unwrap();
        fs::write(source.join("b.bin"), b"bravo").unwrap();

        let legacy_one = dst_root.join(".Megapack.59c73a65be96f30b.sumafile-partial");
        let legacy_two = dst_root.join(".Megapack.b9702512292f3342.sumafile-partial");
        fs::create_dir_all(&legacy_one).unwrap();
        fs::create_dir_all(&legacy_two).unwrap();
        fs::write(legacy_one.join("a.bin"), b"alpha").unwrap();
        let ro_file = legacy_two.join("readonly.bin");
        fs::write(&ro_file, b"stale").unwrap();
        let mut perms = fs::metadata(&ro_file).unwrap().permissions();
        perms.set_readonly(true);
        fs::set_permissions(&ro_file, perms).unwrap();

        let held_reader = Mutex::new(None);
        let dest_for_emit = final_dest.clone();

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            "op_new_retry_id".to_string(),
            "error".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|_| {
                if held_reader.lock().unwrap().is_none() && dest_for_emit.join("a.bin").exists() {
                    if let Ok(file) = fs::File::open(dest_for_emit.join("a.bin")) {
                        *held_reader.lock().unwrap() = Some(file);
                    }
                }
            },
        )
        .expect("unowned legacy artifacts should not affect a new operation");

        assert_eq!(result.len(), 1);
        assert!(final_dest.is_dir());
        assert_eq!(fs::read(legacy_one.join("a.bin")).unwrap(), b"alpha");
        assert_eq!(fs::read(&ro_file).unwrap(), b"stale");
        assert!(!final_dest.join("readonly.bin").exists());
        assert_eq!(fs::read(final_dest.join("a.bin")).unwrap(), b"alpha");
        assert_eq!(fs::read(final_dest.join("b.bin")).unwrap(), b"bravo");
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            const FILE_ATTRIBUTE_HIDDEN: u32 = 0x0000_0002;
            let attrs = fs::metadata(final_dest.join("b.bin"))
                .unwrap()
                .file_attributes();
            assert_eq!(
                attrs & FILE_ATTRIBUTE_HIDDEN,
                0,
                "promoted file must not retain hidden staging attribute"
            );
        }
        drop(held_reader);

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }

    #[test]
    fn planning_time_skipped_files_count_as_completed_progress() {
        let src_dir = unique_temp_path("skip_progress_plan_src");
        let dst_dir = unique_temp_path("skip_progress_plan_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let existing_src = src_dir.join("existing.bin");
        let fresh_src = src_dir.join("fresh.bin");
        let existing_len = 128 * 1024u64;
        let fresh_len = 64 * 1024u64;
        fs::write(&existing_src, vec![1u8; existing_len as usize]).unwrap();
        fs::write(&fresh_src, vec![2u8; fresh_len as usize]).unwrap();
        fs::write(dst_dir.join("existing.bin"), b"already there").unwrap();

        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();
        let result = transfer_with_progress_blocking(
            "copy",
            vec![
                existing_src.to_string_lossy().to_string(),
                fresh_src.to_string_lossy().to_string(),
            ],
            dst_dir.to_string_lossy().to_string(),
            "op_skip_plan_progress".to_string(),
            "skip".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| updates_target.lock().unwrap().push(update),
        )
        .expect("copy with skip should succeed");

        assert_eq!(result.len(), 1);
        let updates = updates.lock().unwrap();
        let sized_running: Vec<&ProgressUpdate> = updates
            .iter()
            .filter(|u| u.status == "running" && u.total > 0)
            .collect();
        assert!(
            !sized_running.is_empty(),
            "expected at least one sized running update: {updates:?}"
        );
        for update in &sized_running {
            assert_eq!(update.total, existing_len + fresh_len);
            assert!(
                update.current >= existing_len,
                "skipped file bytes must be counted as completed: {update:?}"
            );
            assert_eq!(update.total_files, 2);
            assert!(update.current_files >= 1);
        }
        let completed = updates
            .iter()
            .find(|u| u.status == "completed")
            .expect("expected completed update");
        assert_eq!(completed.current, existing_len + fresh_len);
        assert_eq!(completed.total, existing_len + fresh_len);
        assert_eq!(completed.current_files, 2);
        assert_eq!(completed.total_files, 2);

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn merged_folder_skipped_files_count_as_completed_progress() {
        let src_root = unique_temp_path("skip_progress_merge_src");
        let dst_root = unique_temp_path("skip_progress_merge_dst");
        let src_folder = src_root.join("Proj");
        let dst_folder = dst_root.join("Proj");
        fs::create_dir_all(&src_folder).unwrap();
        fs::create_dir_all(&dst_folder).unwrap();
        let existing_len = 128 * 1024u64;
        let fresh_len = 64 * 1024u64;
        fs::write(
            src_folder.join("a_existing.bin"),
            vec![1u8; existing_len as usize],
        )
        .unwrap();
        fs::write(
            src_folder.join("b_fresh.bin"),
            vec![2u8; fresh_len as usize],
        )
        .unwrap();
        fs::write(dst_folder.join("a_existing.bin"), b"already there").unwrap();

        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();
        let result = super::transfer_with_policy_blocking(
            "copy",
            super::Request::Selection {
                sources: vec![src_folder.to_string_lossy().to_string()],
                conflict_action: "skip".to_string(),
            },
            dst_root.to_string_lossy().to_string(),
            "op_skip_merge_progress".to_string(),
            Arc::new(AtomicBool::new(false)),
            &|update| updates_target.lock().unwrap().push(update),
            &|_| {},
            &|| super::tuning::Policy {
                mode: super::tuning::Mode::Manual,
                manual_concurrency: 1,
                order_by: super::tuning::OrderBy::Name,
                ..super::tuning::Policy::default()
            },
        )
        .expect("merge copy with skip should succeed");

        assert_eq!(result.len(), 1);
        let updates = updates.lock().unwrap();
        let finalizing = updates
            .iter()
            .find(|u| u.status == "finalizing")
            .expect("expected finalizing update");
        assert_eq!(
            finalizing.current,
            existing_len + fresh_len,
            "finalizing progress must include skipped bytes: {finalizing:?}"
        );
        assert_eq!(finalizing.total, existing_len + fresh_len);
        let completed = updates
            .iter()
            .find(|u| u.status == "completed")
            .expect("expected completed update");
        assert_eq!(completed.current, existing_len + fresh_len);
        assert_eq!(completed.total, existing_len + fresh_len);
        assert_eq!(completed.current_files, 2);
        assert_eq!(completed.total_files, 2);

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
    }
}
