//! Copy and move execution with progress callbacks.

use super::plan::{ensure_destination_available, TransferPlan};
use super::reporting::{CopyContext, ProgressContext, PROGRESS_BYTE_STEP};
use crate::transfer_reliability::{Failure, Retry};
use crate::transfer_staging::{
    backup_existing_destination, capture_source_version_for_path,
    capture_source_version_from_open_file, cleanup_owned_path, conflict_for_existing_destination,
    delete_verified_source, delete_verified_source_tree, existing_file_matches_source,
    hash_file_contents, promote_with_recovery, remove_path, restore_backed_up_destination,
    resumable_staging_path_for, staging_path_for, verify_source_unchanged, ContentHasher,
    SourceVersion,
};
use simplefile_core::path_conflict::{
    create_dir_exclusive, is_already_exists_error, path_exists_no_follow, rename_exclusive,
};
use simplefile_core::utils::recreate_symlink;
use std::fs;
use std::io::{BufReader, BufWriter, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;

pub(crate) fn is_real_directory(path: &Path) -> bool {
    fs::symlink_metadata(path)
        .map(|meta| {
            let file_type = meta.file_type();
            file_type.is_dir() && !file_type.is_symlink()
        })
        .unwrap_or(false)
}

#[cfg(test)]
pub(crate) fn copy_file_attempt(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: u64,
    buffer_size: usize,
    file_len: u64,
) -> Result<u64, Failure> {
    copy_file_attempt_versioned(src, dst, ctx, completed_bytes, buffer_size, file_len)
        .map(|(copied, _)| copied)
}

pub(crate) fn copy_file_attempt_versioned(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: u64,
    buffer_size: usize,
    file_len: u64,
) -> Result<(u64, SourceVersion), Failure> {
    #[cfg(windows)]
    use std::os::windows::fs::MetadataExt;

    let buffer_size = buffer_size.min(usize::try_from(file_len.max(1)).unwrap_or(usize::MAX));
    let mut src_file = fs::File::open(src).map_err(|e| Failure::io("Open source", src, e))?;
    let source_initial = src_file
        .metadata()
        .map_err(|e| Failure::io("Inspect opened source", src, e))?;
    let source_modified = source_initial.modified().ok();
    #[cfg(windows)]
    let source_times = (
        source_initial.creation_time(),
        source_initial.last_write_time(),
    );
    let create_new_staging = || {
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(dst)
    };
    let first_open = match create_new_staging() {
        Ok(file) => Ok(file),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            if let Some(parent) = dst.parent() {
                fs::create_dir_all(parent)
                    .map_err(|e| Failure::io("Create parent directory", parent, e))?;
            }
            create_new_staging()
        }
        Err(error) => Err(error),
    };
    let (resume_from, dst_file) = match first_open {
        Ok(file) => (0u64, file),
        Err(open_error) => {
            let existing = match fs::symlink_metadata(dst) {
                Ok(meta) if meta.file_type().is_file() && meta.len() <= file_len => meta.len(),
                Ok(_) => {
                    return Err(Failure::new(
                        format!(
                            "Refusing unexpected temporary destination [{}]",
                            dst.display()
                        ),
                        false,
                    ))
                }
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                    return Err(Failure::io("Open temporary destination", dst, open_error))
                }
                Err(error) => return Err(Failure::io("Inspect temporary destination", dst, error)),
            };
            if existing > 0 {
                src_file
                    .seek(SeekFrom::Start(existing))
                    .map_err(|e| Failure::io("Resume source", src, e))?;
            }
            // An existing zero-byte, operation-owned partial is a valid resume point too.
            let file = fs::OpenOptions::new()
                .append(true)
                .open(dst)
                .map_err(|e| Failure::io("Open temporary destination", dst, e))?;
            (existing, file)
        }
    };
    let mut reader = BufReader::with_capacity(buffer_size, src_file);
    let mut writer = BufWriter::with_capacity(buffer_size, dst_file);
    let mut stream_hasher = ContentHasher::new();
    let copied = copy_file_stream_with_hash(
        &mut reader,
        &mut writer,
        src,
        dst,
        ctx,
        completed_bytes,
        resume_from,
        file_len,
        buffer_size,
        if resume_from == 0 {
            Some(&mut stream_hasher)
        } else {
            None
        },
    )?;
    let source_after = reader
        .get_ref()
        .metadata()
        .map_err(|e| Failure::io("Recheck source", src, e))?;
    #[cfg(windows)]
    let times_changed =
        (source_after.creation_time(), source_after.last_write_time()) != source_times;
    #[cfg(not(windows))]
    let times_changed = false;
    if source_after.len() != file_len
        || source_after.modified().ok() != source_modified
        || times_changed
    {
        return Err(Failure::new(
            format!("Source changed during transfer [{}]", src.display()),
            false,
        ));
    }
    let content_hash =
        if resume_from == 0 {
            Some(stream_hasher.finish())
        } else {
            Some(hash_file_contents(reader.get_mut()).map_err(|e| {
                Failure::new(format!("Hash source [{}]: {e}", src.display()), false)
            })?)
        };
    let version =
        capture_source_version_from_open_file(reader.get_ref(), &source_after, content_hash);
    ctx.progress.check_cancelled()?;
    // A successful flush of BufWriter alone does not establish that storage accepted the file.
    writer
        .get_ref()
        .sync_all()
        .map_err(|e| Failure::io("Finalize temporary destination", dst, e))?;
    ctx.progress.check_cancelled()?;
    simplefile_core::file_ops::preserve_basic_metadata(src, dst)
        .map_err(|e| Failure::new(format!("Preserve metadata [{}]: {e}", dst.display()), false))?;
    Ok((copied, version))
}

/// Shared by filesystem copies, folder workers and moves that copy across volumes.
/// Successful I/O may take arbitrarily long; duration is not an error signal.
#[cfg(test)]
#[allow(clippy::too_many_arguments)]
pub(super) fn copy_file_stream(
    reader: &mut dyn Read,
    writer: &mut dyn Write,
    src: &Path,
    dst: &Path,
    ctx: &CopyContext<'_>,
    completed_bytes: u64,
    resume_from: u64,
    file_len: u64,
    buffer_size: usize,
) -> Result<u64, Failure> {
    copy_file_stream_with_hash(
        reader,
        writer,
        src,
        dst,
        ctx,
        completed_bytes,
        resume_from,
        file_len,
        buffer_size,
        None,
    )
}

#[allow(clippy::too_many_arguments)]
fn copy_file_stream_with_hash(
    reader: &mut dyn Read,
    writer: &mut dyn Write,
    src: &Path,
    dst: &Path,
    ctx: &CopyContext<'_>,
    completed_bytes: u64,
    resume_from: u64,
    file_len: u64,
    buffer_size: usize,
    mut hasher: Option<&mut ContentHasher>,
) -> Result<u64, Failure> {
    let mut buffer = vec![0u8; buffer_size];
    let mut copied = resume_from;
    let mut next_emit_at = ((resume_from / PROGRESS_BYTE_STEP) + 1) * PROGRESS_BYTE_STEP;
    while copied < file_len {
        ctx.progress.check_cancelled()?;
        let requested = buffer_size.min((file_len - copied) as usize);
        if let Some(limiter) = ctx.progress.rate_limit {
            limiter.wait(requested, ctx.progress.cancel)?;
        }
        let read = reader
            .read(&mut buffer[..requested])
            .map_err(|e| Failure::io("Read source", src, e))?;
        if let Some(limiter) = ctx.progress.rate_limit {
            limiter.refund(requested - read);
        }
        if read == 0 {
            return Err(Failure::new(
                format!("Source became shorter during transfer [{}]", src.display()),
                false,
            ));
        }
        if let Some(hasher) = hasher.as_deref_mut() {
            hasher.update(&buffer[..read]);
        }
        writer
            .write_all(&buffer[..read])
            .map_err(|e| Failure::io("Write temporary destination", dst, e))?;
        copied += read as u64;
        if copied >= next_emit_at {
            ctx.progress.emit(
                completed_bytes + copied,
                src.to_string_lossy().into(),
                "running",
                None,
            );
            next_emit_at = copied.saturating_add(PROGRESS_BYTE_STEP);
        }
    }
    ctx.progress.finalizing(
        completed_bytes.saturating_add(copied),
        src.to_string_lossy().into(),
    );
    ctx.progress.check_cancelled()?;
    writer
        .flush()
        .map_err(|e| Failure::io("Flush temporary destination", dst, e))?;
    Ok(copied)
}

#[cfg(test)]
pub(crate) fn copy_file_with_progress(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    replace_existing: bool,
) -> Result<(), String> {
    copy_file_with_progress_versioned(src, dst, ctx, completed_bytes, replace_existing).map(|_| ())
}

pub(crate) fn copy_file_with_progress_versioned(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    replace_existing: bool,
) -> Result<SourceVersion, String> {
    let source_metadata = fs::symlink_metadata(src)
        .map_err(|error| format!("Inspect source [{}]: {error}", src.display()))?;
    let file_len = source_metadata.len();
    let source_modified = source_metadata.modified().ok();
    let known = ctx.progress.total_bytes.load(Ordering::Relaxed);
    let needed = (*completed_bytes).saturating_add(file_len);
    if needed > known {
        ctx.progress.total_bytes.store(needed, Ordering::Relaxed);
    }

    if ctx.resume_existing && path_exists_no_follow(dst) {
        if existing_file_matches_source(src, dst)? {
            let version = capture_source_version_for_path(src)?;
            *completed_bytes = completed_bytes.saturating_add(file_len);
            ctx.progress
                .complete_file(*completed_bytes, src.to_string_lossy().to_string());
            return Ok(version);
        }

        remove_path(dst, "resumable destination")?;
    }

    let staged_path = staging_path_for(dst, ctx.operation_id)?;
    let mut retry = Retry::default();
    let copy_result = loop {
        if let Err(error) = ctx.progress.check_cancelled() {
            break Err(error);
        }
        if retry.failures > 0
            && fs::metadata(src)
                .is_ok_and(|meta| meta.len() != file_len || meta.modified().ok() != source_modified)
        {
            break Err(format!("Source changed before retry [{}]", src.display()));
        }
        match copy_file_attempt_versioned(
            src,
            &staged_path,
            ctx,
            *completed_bytes,
            ctx.buffer_size(),
            file_len,
        ) {
            Ok((written, version)) => break Ok((written, version)),
            Err(error) if error.message == "Operation cancelled" => break Err(error.message),
            Err(error) => {
                let reason = error.to_string();
                match retry.failed(error) {
                    Err(error) => break Err(error),
                    Ok(delay) => {
                        if let Some(retries) = ctx.progress.retry_count {
                            retries.fetch_add(1, Ordering::Relaxed);
                        }
                        if let Some(limiter) = ctx.progress.rate_limit {
                            limiter.retrying(delay, &reason);
                        }
                        if let Err(error) =
                            crate::transfer_reliability::wait(delay, ctx.progress.cancel)
                        {
                            break Err(error);
                        }
                    }
                }
            }
        }
    };

    match copy_result {
        Ok((written, version)) => {
            ctx.progress.finalizing(
                completed_bytes.saturating_add(written),
                dst.to_string_lossy().to_string(),
            );
            if let Err(error) = ctx.progress.check_cancelled() {
                let _ = fs::remove_file(&staged_path);
                return Err(error);
            }
            if let Err(error) = promote_with_recovery(
                &staged_path,
                dst,
                replace_existing || ctx.resume_existing,
                ctx.progress.cancel,
                |delay, reason| {
                    if let Some(limiter) = ctx.progress.rate_limit {
                        limiter.retrying(delay, reason);
                    }
                },
            ) {
                let _ = fs::remove_file(&staged_path);
                return Err(
                    if error.starts_with("CONFLICT:") || error == "Operation cancelled" {
                        error
                    } else {
                        format!("Finalize destination [{}]: {error}", dst.display())
                    },
                );
            }
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
            Ok(version)
        }
        Err(error) => {
            if fs::symlink_metadata(&staged_path)
                .is_ok_and(|meta| !meta.file_type().is_file() || meta.len() > file_len)
            {
                return Err(error);
            }
            match fs::remove_file(&staged_path) {
                Ok(()) => Err(error),
                Err(cleanup) if cleanup.kind() == std::io::ErrorKind::NotFound => Err(error),
                Err(cleanup) => Err(format!(
                    "{error}; temporary file remains [{}]: {cleanup}",
                    staged_path.display()
                )),
            }
        }
    }
}

pub(crate) fn copy_symlink_with_progress(
    src_path: &Path,
    dst_path: &Path,
    ctx: &CopyContext,
    completed_bytes: u64,
    replace_existing: bool,
) -> Result<SourceVersion, String> {
    if !replace_existing && !ctx.resume_existing && path_exists_no_follow(dst_path) {
        return Err(conflict_for_existing_destination(dst_path));
    }
    let version = capture_source_version_for_path(src_path)?;
    let staged_path = staging_path_for(dst_path, ctx.operation_id)?;
    if path_exists_no_follow(&staged_path) {
        let _ = cleanup_owned_path(&staged_path);
    }
    recreate_symlink(src_path, &staged_path)?;
    if let Err(error) = verify_source_unchanged(src_path, &version) {
        let _ = cleanup_owned_path(&staged_path);
        return Err(error);
    }
    if let Err(error) = ctx.progress.check_cancelled() {
        let _ = cleanup_owned_path(&staged_path);
        return Err(error);
    }
    if let Err(error) = promote_with_recovery(
        &staged_path,
        dst_path,
        replace_existing || ctx.resume_existing,
        ctx.progress.cancel,
        |delay, reason| {
            if let Some(limiter) = ctx.progress.rate_limit {
                limiter.retrying(delay, reason);
            }
        },
    ) {
        let _ = cleanup_owned_path(&staged_path);
        return Err(
            if error.starts_with("CONFLICT:") || error == "Operation cancelled" {
                error
            } else {
                format!("Finalize destination [{}]: {error}", dst_path.display())
            },
        );
    }
    ctx.progress
        .complete_file(completed_bytes, src_path.to_string_lossy().to_string());
    Ok(version)
}

#[allow(dead_code)]
pub(crate) fn copy_item_with_progress(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    replace_existing: bool,
) -> Result<(), String> {
    let mut stack: Vec<(PathBuf, PathBuf)> = vec![(src.to_path_buf(), dst.to_path_buf())];
    let mut copied_dirs: Vec<(PathBuf, PathBuf)> = Vec::new();
    let mut replaced_backups: Vec<(PathBuf, PathBuf)> = Vec::new();

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
                let merged_existing_directory = if path_exists_no_follow(&dst_path) {
                    if is_real_directory(&dst_path) {
                        if !replace_existing && !ctx.resume_existing {
                            return Err(conflict_for_existing_destination(&dst_path));
                        }
                        true
                    } else if replace_existing || ctx.resume_existing {
                        let backup = backup_existing_destination(&dst_path)?;
                        replaced_backups.push((dst_path.clone(), backup));
                        false
                    } else {
                        return Err(conflict_for_existing_destination(&dst_path));
                    }
                } else {
                    false
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
                copy_symlink_with_progress(
                    &src_path,
                    &dst_path,
                    ctx,
                    *completed_bytes,
                    replace_existing,
                )?;
            } else {
                if !replace_existing && !ctx.resume_existing && path_exists_no_follow(&dst_path) {
                    return Err(conflict_for_existing_destination(&dst_path));
                }
                copy_file_with_progress_versioned(
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
            let _ = cleanup_owned_path(dst_dir);
        }
        for (dst_path, backup_path) in replaced_backups.iter().rev() {
            restore_backed_up_destination(dst_path, backup_path);
        }
        return result;
    }

    for (src_dir, dst_dir) in copied_dirs.into_iter().rev() {
        simplefile_core::file_ops::preserve_basic_metadata(&src_dir, &dst_dir)?;
    }
    for (_, backup_path) in replaced_backups {
        let _ = cleanup_owned_path(&backup_path);
    }

    Ok(())
}

pub(crate) fn copy_plan_with_progress(
    plan: &TransferPlan,
    ctx: &ProgressContext,
    completed_bytes: &mut u64,
    session: &mut super::adaptive::Session<'_>,
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
    let merge_into_existing_dir = source_meta.file_type().is_dir()
        && plan.replace_existing
        && is_real_directory(&plan.final_dest);
    if source_meta.file_type().is_dir() && !merge_into_existing_dir {
        let staged_dest = resumable_staging_path_for(&plan.final_dest, ctx.operation_id)?;
        let resumable_ctx = CopyContext {
            progress: ctx,
            operation_id: ctx.operation_id,
            network: plan.network,
            resume_existing: true,
        };
        let result = super::parallel::copy_tree(
            &plan.source_path,
            &staged_dest,
            &resumable_ctx,
            completed_bytes,
            false,
            session,
        );
        result?;
        ctx.finalizing(
            *completed_bytes,
            plan.final_dest.to_string_lossy().to_string(),
        );
        if let Err(error) = ctx.check_cancelled() {
            let _ = cleanup_owned_path(&staged_dest);
            return Err(error);
        }
        if let Err(error) = promote_with_recovery(
            &staged_dest,
            &plan.final_dest,
            plan.replace_existing,
            ctx.cancel,
            |delay, reason| {
                session.limiter.retrying(delay, reason);
                session.display(Vec::new(), true);
            },
        ) {
            let _ = cleanup_owned_path(&staged_dest);
            return Err(error);
        }
        Ok(())
    } else if source_meta.file_type().is_dir() {
        super::parallel::copy_tree(
            &plan.source_path,
            &plan.final_dest,
            &copy_ctx,
            completed_bytes,
            plan.replace_existing,
            session,
        )
        .map(|_| ())
    } else {
        let task = super::parallel::FileTask {
            source: plan.source_path.clone(),
            destination: plan.final_dest.clone(),
            size: if source_meta.file_type().is_symlink() {
                0
            } else {
                source_meta.len()
            },
            network: plan.network,
            replace_existing: plan.replace_existing,
            resume_existing: false,
            symlink: source_meta.file_type().is_symlink(),
        };
        let copied = super::parallel::copy_files(&[task], ctx, session, *completed_bytes);
        *completed_bytes = completed_bytes.saturating_add(copied.bytes);
        copied.failed.map_or(Ok(()), |(_, error)| Err(error))
    }
}

pub(crate) fn move_plan_with_progress(
    plan: &TransferPlan,
    ctx: &ProgressContext,
    completed_bytes: &mut u64,
    session: &mut super::adaptive::Session<'_>,
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
    if plan.allow_rename && (!plan.replace_existing || !path_exists_no_follow(&plan.final_dest)) {
        match rename_exclusive(&plan.source_path, &plan.final_dest) {
            Ok(()) => {
                *completed_bytes = completed_bytes.saturating_add(rename_size.max(1));
                ctx.complete_files(
                    plan.file_count,
                    *completed_bytes,
                    plan.source_path.to_string_lossy().to_string(),
                );
                return Ok(());
            }
            Err(error)
                if !plan.replace_existing
                    && (is_already_exists_error(&error)
                        || path_exists_no_follow(&plan.final_dest)) =>
            {
                return Err(conflict_for_existing_destination(&plan.final_dest));
            }
            Err(_) => {}
        }
    }

    let copy_ctx = CopyContext {
        progress: ctx,
        operation_id: ctx.operation_id,
        network: plan.network,
        resume_existing: false,
    };

    let source_meta = fs::symlink_metadata(&plan.source_path)
        .map_err(|error| format!("Failed to stat source: {error}"))?;
    let merge_into_existing_dir = source_meta.file_type().is_dir()
        && plan.replace_existing
        && is_real_directory(&plan.final_dest);
    if source_meta.file_type().is_dir() && !merge_into_existing_dir {
        let staged_dest = resumable_staging_path_for(&plan.final_dest, ctx.operation_id)?;
        let resumable_ctx = CopyContext {
            progress: ctx,
            operation_id: ctx.operation_id,
            network: plan.network,
            resume_existing: true,
        };
        let manifest = super::parallel::copy_tree(
            &plan.source_path,
            &staged_dest,
            &resumable_ctx,
            completed_bytes,
            false,
            session,
        )?;
        ctx.finalizing(
            *completed_bytes,
            plan.final_dest.to_string_lossy().to_string(),
        );
        if let Err(error) = ctx.check_cancelled() {
            let _ = cleanup_owned_path(&staged_dest);
            return Err(error);
        }
        if let Err(error) = promote_with_recovery(
            &staged_dest,
            &plan.final_dest,
            plan.replace_existing,
            ctx.cancel,
            |delay, reason| {
                session.limiter.retrying(delay, reason);
                session.display(Vec::new(), true);
            },
        ) {
            let _ = cleanup_owned_path(&staged_dest);
            return Err(error);
        }
        delete_verified_source_tree(&plan.source_path, &manifest)
            .map_err(|error| format!("Copied but failed to delete source: {error}"))?;
        Ok(())
    } else if source_meta.file_type().is_dir() {
        let manifest = super::parallel::copy_tree(
            &plan.source_path,
            &plan.final_dest,
            &copy_ctx,
            completed_bytes,
            plan.replace_existing,
            session,
        )?;
        delete_verified_source_tree(&plan.source_path, &manifest)
            .map_err(|error| format!("Copied but failed to delete source: {error}"))?;
        Ok(())
    } else {
        let task = super::parallel::FileTask {
            source: plan.source_path.clone(),
            destination: plan.final_dest.clone(),
            size: if source_meta.file_type().is_symlink() {
                0
            } else {
                source_meta.len()
            },
            network: plan.network,
            replace_existing: plan.replace_existing,
            resume_existing: false,
            symlink: source_meta.file_type().is_symlink(),
        };
        let copied = super::parallel::copy_files(&[task], ctx, session, *completed_bytes);
        *completed_bytes = completed_bytes.saturating_add(copied.bytes);
        if let Some((_, error)) = copied.failed {
            return Err(error);
        }
        let version = copied.versions.get(&0).ok_or_else(|| {
            format!(
                "Missing source verification snapshot for {}",
                plan.source_path.display()
            )
        })?;
        delete_verified_source(&plan.source_path, version)
            .map_err(|error| format!("Copied but failed to delete source: {error}"))?;
        Ok(())
    }
}
