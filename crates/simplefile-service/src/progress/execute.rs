//! Copy and move execution with progress callbacks.

use super::plan::{ensure_destination_available, TransferPlan};
use super::reporting::{CopyContext, ProgressContext, PROGRESS_BYTE_STEP};
use crate::transfer_staging::{
    conflict_for_existing_destination, existing_file_matches_source, promote_staged_path,
    remove_path, resumable_staging_path_for, staging_path_for,
};
use simplefile_core::path_conflict::{create_dir_exclusive, path_exists_no_follow};
use simplefile_core::utils::recreate_symlink;
use std::fs;
use std::io::{BufReader, BufWriter, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::time::Duration;

pub(crate) fn is_real_directory(path: &Path) -> bool {
    fs::symlink_metadata(path)
        .map(|meta| {
            let file_type = meta.file_type();
            file_type.is_dir() && !file_type.is_symlink()
        })
        .unwrap_or(false)
}

pub(crate) fn prepare_destination_for_copy(
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

pub(crate) fn copy_file_attempt(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: u64,
    buffer_size: usize,
    file_len: u64,
) -> Result<u64, String> {
    let mut resume_from = 0u64;
    if path_exists_no_follow(dst) {
        let partial_meta = fs::symlink_metadata(dst)
            .map_err(|error| format!("Failed to stat partial destination: {error}"))?;
        if partial_meta.file_type().is_file() && partial_meta.len() <= file_len {
            resume_from = partial_meta.len();
        } else {
            remove_path(dst, "partial destination")?;
        }
    }

    if resume_from == file_len {
        return Ok(file_len);
    }

    let mut src_file =
        fs::File::open(src).map_err(|error| format!("Failed to open source file: {error}"))?;
    if resume_from > 0 {
        src_file
            .seek(SeekFrom::Start(resume_from))
            .map_err(|error| format!("Failed to resume source file: {error}"))?;
    }
    let dst_file = if resume_from > 0 {
        fs::OpenOptions::new()
            .append(true)
            .open(dst)
            .map_err(|error| format!("Failed to resume destination file: {error}"))?
    } else {
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(dst)
            .map_err(|error| format!("Failed to create destination file: {error}"))?
    };
    let mut reader = BufReader::with_capacity(buffer_size, src_file);
    let mut writer = BufWriter::with_capacity(buffer_size, dst_file);
    let mut buffer = vec![0u8; buffer_size];
    let mut copied_this_attempt = resume_from;
    let mut next_emit_at = ((resume_from / PROGRESS_BYTE_STEP) + 1) * PROGRESS_BYTE_STEP;

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

    ctx.progress.finalizing(
        completed_bytes.saturating_add(copied_this_attempt),
        src.to_string_lossy().to_string(),
    );
    ctx.progress.check_cancelled()?;
    writer
        .flush()
        .map_err(|error| format!("Failed to flush destination file: {error}"))?;
    ctx.progress.check_cancelled()?;
    simplefile_core::file_ops::preserve_basic_metadata(src, dst)?;
    Ok(copied_this_attempt)
}

pub(crate) fn copy_file_with_progress(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    replace_existing: bool,
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

    if path_exists_no_follow(dst) && ctx.resume_existing {
        if existing_file_matches_source(src, dst)? {
            *completed_bytes = completed_bytes.saturating_add(file_len);
            ctx.progress
                .complete_file(*completed_bytes, src.to_string_lossy().to_string());
            return Ok(());
        }

        remove_path(dst, "resumable destination")?;
    }

    let staged_path = staging_path_for(dst, ctx.operation_id)?;
    let copy_result = if file_len == 0 {
        let result = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&staged_path)
            .map_err(|error| format!("Failed to create destination file: {error}"))
            .map(|_| 0);
        if result.is_ok() {
            if let Err(error) =
                simplefile_core::file_ops::preserve_basic_metadata(src, &staged_path)
            {
                let _ = fs::remove_file(&staged_path);
                return Err(error);
            }
        }
        result
    } else {
        let attempts = ctx.attempts();
        let buffer_size = ctx.buffer_size();
        let mut copied = Err(String::new());

        for attempt in 0..attempts {
            if let Err(error) = ctx.progress.check_cancelled() {
                let _ = fs::remove_file(&staged_path);
                return Err(error);
            }
            if attempt > 0 {
                std::thread::sleep(Duration::from_millis(500 * (1u64 << (attempt - 1))));
            }

            match copy_file_attempt(
                src,
                &staged_path,
                ctx,
                *completed_bytes,
                buffer_size,
                file_len,
            ) {
                Ok(written) => {
                    copied = Ok(written);
                    break;
                }
                Err(error) if error == "Operation cancelled" => {
                    let _ = fs::remove_file(&staged_path);
                    return Err(error);
                }
                Err(error) => {
                    copied = Err(error);
                }
            }
        }

        copied
    };

    match copy_result {
        Ok(written) => {
            ctx.progress.finalizing(
                completed_bytes.saturating_add(written),
                dst.to_string_lossy().to_string(),
            );
            if let Err(error) = ctx.progress.check_cancelled() {
                let _ = fs::remove_file(&staged_path);
                return Err(error);
            }
            if let Err(error) = promote_staged_path(&staged_path, dst, replace_existing) {
                let _ = fs::remove_file(&staged_path);
                return Err(error);
            }
            simplefile_core::file_ops::preserve_basic_metadata(src, dst)?;
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
            Ok(())
        }
        Err(error) => {
            let _ = fs::remove_file(&staged_path);
            Err(error)
        }
    }
}

pub(crate) fn copy_item_with_progress(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    replace_existing: bool,
) -> Result<(), String> {
    let mut stack: Vec<(PathBuf, PathBuf)> = vec![(src.to_path_buf(), dst.to_path_buf())];
    let mut copied_dirs: Vec<(PathBuf, PathBuf)> = Vec::new();

    let result = (|| -> Result<(), String> {
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
            if file_type.is_dir() {
                let merged_existing_directory =
                    if ctx.resume_existing && is_real_directory(&dst_path) {
                        true
                    } else {
                        prepare_destination_for_copy(true, &dst_path, replace_existing)?
                    };
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
                prepare_destination_for_copy(false, &dst_path, replace_existing)?;
                recreate_symlink(&src_path, &dst_path)?;
                ctx.progress
                    .complete_file(*completed_bytes, src_path.to_string_lossy().to_string());
            } else {
                if !replace_existing && !ctx.resume_existing && path_exists_no_follow(&dst_path) {
                    return Err(conflict_for_existing_destination(&dst_path));
                }
                copy_file_with_progress(
                    &src_path,
                    &dst_path,
                    ctx,
                    completed_bytes,
                    replace_existing,
                )?;
            }
        }

        Ok(())
    })();

    if result.is_err() {
        for (_, dst_dir) in copied_dirs.iter().rev() {
            let _ = remove_path(dst_dir, "partial destination directory");
        }
        return result;
    }

    for (src_dir, dst_dir) in copied_dirs.into_iter().rev() {
        simplefile_core::file_ops::preserve_basic_metadata(&src_dir, &dst_dir)?;
    }

    Ok(())
}

pub(crate) fn copy_plan_with_progress(
    plan: &TransferPlan,
    ctx: &ProgressContext,
    completed_bytes: &mut u64,
) -> Result<(), String> {
    ensure_destination_available(plan)?;

    let copy_ctx = CopyContext {
        progress: ctx,
        operation_id: ctx.operation_id,
        network: plan.network,
        resume_existing: false,
    };

    let source_meta = fs::symlink_metadata(&plan.source_path)
        .map_err(|error| format!("Failed to stat source: {error}"))?;
    if source_meta.file_type().is_dir() && !plan.replace_existing {
        let staged_dest = resumable_staging_path_for(&plan.final_dest)?;
        let resumable_ctx = CopyContext {
            progress: ctx,
            operation_id: ctx.operation_id,
            network: plan.network,
            resume_existing: true,
        };
        let result = copy_item_with_progress(
            &plan.source_path,
            &staged_dest,
            &resumable_ctx,
            completed_bytes,
            false,
        );
        result?;
        ctx.finalizing(
            *completed_bytes,
            plan.final_dest.to_string_lossy().to_string(),
        );
        if let Err(error) = ctx.check_cancelled() {
            let _ = remove_path(&staged_dest, "partial destination directory");
            return Err(error);
        }
        promote_staged_path(&staged_dest, &plan.final_dest, false)?;
        Ok(())
    } else {
        copy_item_with_progress(
            &plan.source_path,
            &plan.final_dest,
            &copy_ctx,
            completed_bytes,
            plan.replace_existing,
        )
    }
}

pub(crate) fn move_plan_with_progress(
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
        operation_id: ctx.operation_id,
        network: plan.network,
        resume_existing: false,
    };

    let source_meta = fs::symlink_metadata(&plan.source_path)
        .map_err(|error| format!("Failed to stat source: {error}"))?;
    if source_meta.file_type().is_dir() && !plan.replace_existing {
        let staged_dest = resumable_staging_path_for(&plan.final_dest)?;
        let resumable_ctx = CopyContext {
            progress: ctx,
            operation_id: ctx.operation_id,
            network: plan.network,
            resume_existing: true,
        };
        let result = copy_item_with_progress(
            &plan.source_path,
            &staged_dest,
            &resumable_ctx,
            completed_bytes,
            false,
        );
        result?;
        ctx.finalizing(
            *completed_bytes,
            plan.final_dest.to_string_lossy().to_string(),
        );
        if let Err(error) = ctx.check_cancelled() {
            let _ = remove_path(&staged_dest, "partial destination directory");
            return Err(error);
        }
        promote_staged_path(&staged_dest, &plan.final_dest, false)?;
    } else {
        copy_item_with_progress(
            &plan.source_path,
            &plan.final_dest,
            &copy_ctx,
            completed_bytes,
            plan.replace_existing,
        )?;
    }

    remove_path(&plan.source_path, "source")
        .map_err(|error| format!("Copied but failed to delete source: {error}"))?;
    Ok(())
}
