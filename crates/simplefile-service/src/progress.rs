//! Progress tracking and cancellation for bulk file transfers.

mod execute;
mod plan;
mod registry;
mod reporting;

pub use registry::OperationRegistry;

pub(crate) use plan::prepare_transfer_inputs;
pub(crate) use reporting::ProgressContext;

#[cfg(test)]
pub(crate) use execute::{copy_file_attempt, copy_file_with_progress};
#[cfg(test)]
pub(crate) use reporting::CopyContext;

use execute::{copy_plan_with_progress, move_plan_with_progress};
use plan::{choose_next_keep_both_destination, prime_transfer, TransferEstimate};
use serde::Serialize;
use simplefile_core::models::ProgressUpdate;
use simplefile_core::path_conflict::{is_keep_both_action, path_collision_key};
use simplefile_core::utils::generate_operation_id;
use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex};
use std::time::Instant;

#[derive(Debug, Serialize, Clone)]
pub struct TransferResult {
    pub source: String,
    pub destination: String,
}

#[derive(Debug, Serialize, Clone)]
pub struct TransferFailure {
    pub source: String,
    pub error: String,
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
) -> Result<Vec<TransferResult>, TransferBatchError> {
    let (mut plans, _dest_path) = prepare_transfer_inputs(sources, destination, &conflict_action)
        .map_err(|error| {
        TransferBatchError::failed(
            operation_id.clone(),
            error,
            Vec::new(),
            Vec::new(),
            Vec::new(),
        )
    })?;
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

    progress.emit_now(0, 0, "Preparing transfer...".to_string(), "running", None);
    let estimated = match prime_transfer(&mut plans, &cancel) {
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
        Failed {
            error: String,
            source: String,
            committed: Vec<TransferResult>,
            pending: Vec<String>,
        },
    }

    let outcome = (|| -> TransferOutcome {
        for index in 0..plans.len() {
            let mut plan = plans[index].clone();
            if cancel.load(Ordering::Relaxed) {
                return TransferOutcome::Cancelled(transferred);
            }
            let source = plan.source_path.to_string_lossy().to_string();
            let pending_after_current = pending_sources(&plans, index + 1);
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
                                return TransferOutcome::Failed {
                                    error: resolve_error,
                                    source,
                                    committed: transferred,
                                    pending: pending_after_current,
                                };
                            }
                        }
                        Err(error) => {
                            return TransferOutcome::Failed {
                                error,
                                source,
                                committed: transferred,
                                pending: pending_after_current,
                            };
                        }
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
                                return TransferOutcome::Failed {
                                    error: resolve_error,
                                    source,
                                    committed: transferred,
                                    pending: pending_after_current,
                                };
                            }
                        }
                        Err(error) => {
                            return TransferOutcome::Failed {
                                error,
                                source,
                                committed: transferred,
                                pending: pending_after_current,
                            };
                        }
                    },
                    _ => {
                        return TransferOutcome::Failed {
                            error: format!("Unsupported operation: {operation_type}"),
                            source,
                            committed: transferred,
                            pending: pending_after_current,
                        };
                    }
                }
            }

            if !completed_plan {
                return TransferOutcome::Failed {
                    error: "Could not choose a unique destination after repeated conflicts"
                        .to_string(),
                    source,
                    committed: transferred,
                    pending: pending_after_current,
                };
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
        TransferOutcome::Failed {
            error,
            source,
            committed,
            pending,
        } => {
            progress.emit_with_total(
                completed_bytes,
                final_total,
                String::new(),
                "error",
                Some(error.clone()),
            );
            Err(TransferBatchError::failed(
                operation_id,
                error.clone(),
                committed,
                vec![TransferFailure { source, error }],
                pending,
            ))
        }
    }
}

fn pending_sources(plans: &[plan::TransferPlan], start: usize) -> Vec<String> {
    plans
        .iter()
        .skip(start)
        .map(|plan| plan.source_path.to_string_lossy().to_string())
        .collect()
}

impl TransferBatchError {
    fn failed(
        operation_id: String,
        message: String,
        committed: Vec<TransferResult>,
        failed: Vec<TransferFailure>,
        pending: Vec<String>,
    ) -> Self {
        Self {
            message: message.clone(),
            outcome: Box::new(TransferBatchOutcome {
                operation_id,
                status: "failed".to_string(),
                committed,
                skipped: Vec::new(),
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
    use crate::transfer_staging::{resumable_staging_path_for, staging_path_for};
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

        let error = transfer_with_progress_blocking(
            "copy",
            vec![
                first.to_string_lossy().to_string(),
                second.to_string_lossy().to_string(),
                third.to_string_lossy().to_string(),
            ],
            dst_dir.to_string_lossy().to_string(),
            "op_partial_failure_test".to_string(),
            "error".to_string(),
            cancel,
            &|update| {
                if update.current_item == first_text
                    && !created_conflict_for_emit.swap(true, Ordering::Relaxed)
                {
                    fs::write(&conflict_for_emit, b"conflict").unwrap();
                }
            },
        )
        .expect_err("second item should fail after first commits");

        assert!(error.message.starts_with("CONFLICT:"));
        assert_eq!(error.outcome.operation_id, "op_partial_failure_test");
        assert_eq!(error.outcome.status, "failed");
        assert_eq!(error.outcome.committed.len(), 1);
        assert_eq!(
            error.outcome.committed[0].source,
            first.to_string_lossy().to_string()
        );
        assert_eq!(
            error.outcome.committed[0].destination,
            dst_dir.join("first.bin").to_string_lossy().to_string()
        );
        assert_eq!(error.outcome.failed.len(), 1);
        assert_eq!(
            error.outcome.failed[0].source,
            second.to_string_lossy().to_string()
        );
        assert_eq!(
            error.outcome.pending,
            vec![third.to_string_lossy().to_string()]
        );
        assert!(dst_dir.join("first.bin").exists());

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
        fs::write(&staged, b"partial").unwrap();

        let total_bytes = Arc::new(AtomicU64::new(0));
        let total_files = Arc::new(AtomicU64::new(0));
        let completed_files = Arc::new(AtomicU64::new(0));
        let cancel = Arc::new(AtomicBool::new(true));
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
        };
        let ctx = CopyContext {
            progress: &progress,
            operation_id,
            network: false,
            resume_existing: false,
        };
        let mut completed_bytes = 0;

        let error =
            copy_file_with_progress(&source, &destination, &ctx, &mut completed_bytes, false)
                .expect_err("cancelled copy should fail");

        assert_eq!(error, "Operation cancelled");
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
        };
        let ctx = CopyContext {
            progress: &progress,
            operation_id,
            network: true,
            resume_existing: false,
        };

        let copied = copy_file_attempt(&source, &destination, &ctx, 0, 2, 6)
            .expect("partial destination should resume");

        assert_eq!(copied, 6);
        assert_eq!(fs::read(&destination).unwrap(), b"abcdef");

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    #[test]
    fn directory_copy_resumes_completed_files_from_staging_directory() {
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
        let staging = resumable_staging_path_for(&final_destination, operation_id).unwrap();
        fs::create_dir_all(&staging).unwrap();
        let staged_existing = staging.join("one.txt");
        fs::copy(&existing_source, &staged_existing).unwrap();
        simplefile_core::file_ops::preserve_basic_metadata(&existing_source, &staged_existing)
            .unwrap();
        let staged_existing_modified = fs::metadata(&staged_existing).unwrap().modified().unwrap();
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
        .expect("directory copy should resume staged files and complete");

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
    fn directory_copy_cancel_during_finalizing_does_not_promote_staging() {
        let src_root = unique_temp_path("finalizing_cancel_src");
        let dst_root = unique_temp_path("finalizing_cancel_dst");
        let source = src_root.join("Movie");
        let final_destination = dst_root.join("Movie");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir_all(&dst_root).unwrap();
        fs::write(source.join("feature.mkv"), b"feature").unwrap();

        let operation_id = "op_finalizing_cancel_test";
        let staging = resumable_staging_path_for(&final_destination, operation_id).unwrap();
        let final_destination_text = final_destination.to_string_lossy().to_string();
        let cancel = Arc::new(AtomicBool::new(false));
        let cancel_for_emit = cancel.clone();
        let updates = Arc::new(Mutex::new(Vec::<ProgressUpdate>::new()));
        let updates_target = updates.clone();

        let result = transfer_with_progress_blocking(
            "copy",
            vec![source.to_string_lossy().to_string()],
            dst_root.to_string_lossy().to_string(),
            operation_id.to_string(),
            "error".to_string(),
            cancel,
            &|update| {
                if update.status == "finalizing" && update.current_item == final_destination_text {
                    cancel_for_emit.store(true, Ordering::Relaxed);
                }
                updates_target.lock().unwrap().push(update);
            },
        )
        .expect("cancelled directory copy should return partial results");

        assert!(result.is_empty());
        assert!(!final_destination.exists());
        assert!(!staging.exists());
        let updates = updates.lock().unwrap();
        assert!(updates.iter().any(|update| {
            update.status == "finalizing" && update.current_item == final_destination_text
        }));
        assert!(updates.iter().any(|update| update.status == "cancelled"));

        let _ = fs::remove_dir_all(&src_root);
        let _ = fs::remove_dir_all(&dst_root);
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
