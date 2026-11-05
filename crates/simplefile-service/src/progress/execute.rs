//! Copy and move execution with progress callbacks.

use super::plan::{ensure_destination_available, TransferPlan};
use super::reporting::{CopyContext, ProgressContext, PROGRESS_BYTE_STEP};
use super::resources::PooledBuffer;
use super::tuning::CheckMode;
use crate::transfer_reliability::{Failure, Retry};
use crate::transfer_staging::{
    adopt_or_cleanup_legacy_dir_staging, capture_source_version_for_path,
    capture_source_version_from_open_file, cleanup_owned_path, cleanup_stale_file_staging,
    conflict_for_existing_destination, delete_verified_source, delete_verified_source_tree,
    existing_file_matches_source_with_mode, hash_file_contents, preallocate_file,
    promote_with_policy, remove_path, staging_path_for, verify_source_unchanged, ContentHasher,
    SourceVersion,
};
use simplefile_core::path_conflict::{
    is_already_exists_error, path_exists_no_follow, rename_exclusive,
};
use simplefile_core::utils::recreate_symlink;
use std::fs;
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc;
use std::time::Duration;

const OVERLAPPED_RING_SLOTS: usize = 3;

pub(crate) fn is_real_directory(path: &Path) -> bool {
    fs::symlink_metadata(path)
        .map(|meta| {
            let file_type = meta.file_type();
            file_type.is_dir() && !file_type.is_symlink()
        })
        .unwrap_or(false)
}

#[cfg(windows)]
fn open_source_sequential(src: &Path) -> std::io::Result<fs::File> {
    use std::os::windows::fs::OpenOptionsExt;
    const FILE_FLAG_SEQUENTIAL_SCAN: u32 = 0x0800_0000;
    fs::OpenOptions::new()
        .read(true)
        .custom_flags(FILE_FLAG_SEQUENTIAL_SCAN)
        .open(src)
}

#[cfg(not(windows))]
fn open_source_sequential(src: &Path) -> std::io::Result<fs::File> {
    fs::File::open(src)
}

#[cfg(windows)]
fn create_new_staging_sequential(dst: &Path) -> std::io::Result<fs::File> {
    use std::os::windows::fs::OpenOptionsExt;
    const FILE_FLAG_SEQUENTIAL_SCAN: u32 = 0x0800_0000;
    const FILE_ATTRIBUTE_HIDDEN: u32 = 0x0000_0002;
    let is_staging = dst
        .file_name()
        .and_then(|n| n.to_str())
        .is_some_and(|n| n.ends_with(".sumafile-partial"));
    let mut opts = fs::OpenOptions::new();
    opts.write(true)
        .create_new(true)
        .custom_flags(FILE_FLAG_SEQUENTIAL_SCAN);
    if is_staging {
        opts.attributes(FILE_ATTRIBUTE_HIDDEN);
    }
    opts.open(dst)
}

#[cfg(not(windows))]
fn create_new_staging_sequential(dst: &Path) -> std::io::Result<fs::File> {
    fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(dst)
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
    copy_file_attempt_versioned(src, dst, ctx, completed_bytes, buffer_size, file_len, true)
        .map(|(copied, _)| copied)
}

pub(crate) fn copy_file_attempt_versioned(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: u64,
    buffer_size: usize,
    file_len: u64,
    must_sync: bool,
) -> Result<(u64, SourceVersion), Failure> {
    #[cfg(windows)]
    use std::os::windows::fs::MetadataExt;

    let buffer_size = buffer_size.min(usize::try_from(file_len.max(1)).unwrap_or(usize::MAX));
    let mut src_file =
        open_source_sequential(src).map_err(|e| Failure::io("Open source", src, e))?;
    let source_initial = src_file
        .metadata()
        .map_err(|e| Failure::io("Inspect opened source", src, e))?;
    let source_modified = source_initial.modified().ok();
    #[cfg(windows)]
    let source_times = (
        source_initial.creation_time(),
        source_initial.last_write_time(),
    );
    let first_open = match create_new_staging_sequential(dst) {
        Ok(file) => Ok(file),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            if let Some(parent) = dst.parent() {
                fs::create_dir_all(parent)
                    .map_err(|e| Failure::io("Create parent directory", parent, e))?;
            }
            create_new_staging_sequential(dst)
        }
        Err(error) => Err(error),
    };
    let (resume_from, mut dst_file) = match first_open {
        Ok(file) => {
            if ctx.policy.preallocate {
                preallocate_file(&file, file_len);
            }
            (0u64, file)
        }
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

    let verify_checksum = ctx.policy.check_mode == CheckMode::Checksum;
    let need_hash = ctx.progress.operation_type != "copy" || verify_checksum;
    let mut stream_hasher = ContentHasher::new();
    let remaining = file_len.saturating_sub(resume_from);
    let multi_streams = ctx.policy.multi_thread_streams_count();
    let multi_cutoff = ctx.policy.multi_thread_cutoff_bytes();

    let copied = if cfg!(windows)
        && resume_from == 0
        && !need_hash
        && multi_streams >= 2
        && file_len >= multi_cutoff
        && file_len >= (buffer_size as u64).saturating_mul(2)
    {
        copy_file_multistream(
            &src_file,
            &mut dst_file,
            src,
            dst,
            ctx,
            completed_bytes,
            file_len,
            buffer_size,
            multi_streams,
        )?
    } else if remaining > buffer_size as u64 {
        copy_file_overlapped_with_hash(
            &mut src_file,
            &mut dst_file,
            src,
            dst,
            ctx,
            completed_bytes,
            resume_from,
            file_len,
            buffer_size,
            if need_hash && resume_from == 0 {
                Some(&mut stream_hasher)
            } else {
                None
            },
        )?
    } else {
        copy_file_stream_with_hash(
            &mut src_file,
            &mut dst_file,
            src,
            dst,
            ctx,
            completed_bytes,
            resume_from,
            file_len,
            buffer_size,
            if need_hash && resume_from == 0 {
                Some(&mut stream_hasher)
            } else {
                None
            },
        )?
    };

    let source_after = src_file
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
        if !need_hash {
            None
        } else if resume_from == 0 {
            Some(stream_hasher.finish())
        } else {
            Some(hash_file_contents(&mut src_file).map_err(|e| {
                Failure::new(format!("Hash source [{}]: {e}", src.display()), false)
            })?)
        };
    let version = capture_source_version_from_open_file(&src_file, &source_after, content_hash);
    ctx.progress.check_cancelled()?;
    if must_sync {
        dst_file
            .sync_all()
            .map_err(|e| Failure::io("Finalize temporary destination", dst, e))?;
    }
    drop(dst_file);
    if verify_checksum {
        let mut verify_dst = open_source_sequential(dst)
            .map_err(|e| Failure::io("Open destination for checksum", dst, e))?;
        let dst_hash = hash_file_contents(&mut verify_dst).map_err(|e| {
            Failure::new(
                format!("Verify destination checksum [{}]: {e}", dst.display()),
                true,
            )
        })?;
        if Some(dst_hash) != content_hash {
            return Err(Failure::new(
                format!(
                    "Destination checksum mismatch after transfer [{}]",
                    dst.display()
                ),
                true,
            ));
        }
    }
    ctx.progress.check_cancelled()?;
    simplefile_core::file_ops::preserve_basic_metadata(src, dst)
        .map_err(|e| Failure::new(format!("Preserve metadata [{}]: {e}", dst.display()), false))?;
    Ok((copied, version))
}

/// Overlapped ring-buffered pipeline (`rclone` `AsyncReader` style): a dedicated
/// scoped reader thread prefetches and hashes upcoming chunks into a 3-slot ring
/// of `PooledBuffer`s while the caller simultaneously writes previous chunks to
/// the destination file.
#[allow(clippy::too_many_arguments)]
fn copy_file_overlapped_with_hash(
    src_file: &mut fs::File,
    dst_file: &mut fs::File,
    src: &Path,
    dst: &Path,
    ctx: &CopyContext<'_>,
    completed_bytes: u64,
    resume_from: u64,
    file_len: u64,
    buffer_size: usize,
    mut hasher: Option<&mut ContentHasher>,
) -> Result<u64, Failure> {
    let (free_tx, free_rx) = mpsc::sync_channel::<PooledBuffer>(OVERLAPPED_RING_SLOTS);
    let (full_tx, full_rx) =
        mpsc::sync_channel::<Result<(PooledBuffer, usize), Failure>>(OVERLAPPED_RING_SLOTS);
    for _ in 0..OVERLAPPED_RING_SLOTS {
        let _ = free_tx.send(PooledBuffer::acquire(buffer_size));
    }

    let cancel = ctx.progress.cancel;
    let mut copied = resume_from;
    let mut next_emit_at = ((resume_from / PROGRESS_BYTE_STEP) + 1) * PROGRESS_BYTE_STEP;
    let mut write_result: Result<(), Failure> = Ok(());

    std::thread::scope(|scope| {
        scope.spawn(move || {
            let mut remaining = file_len.saturating_sub(resume_from);
            while remaining > 0 {
                if cancel.load(Ordering::Relaxed) {
                    let _ = full_tx.send(Err(Failure::new("Operation cancelled", false)));
                    break;
                }
                let Ok(mut buf) = free_rx.recv() else {
                    break;
                };
                let requested = buffer_size.min(usize::try_from(remaining).unwrap_or(usize::MAX));
                match src_file.read(&mut buf.as_mut_slice()[..requested]) {
                    Ok(0) => {
                        let _ = full_tx.send(Err(Failure::new(
                            format!("Source became shorter during transfer [{}]", src.display()),
                            false,
                        )));
                        break;
                    }
                    Ok(read) => {
                        if let Some(hasher) = hasher.as_deref_mut() {
                            hasher.update(&buf[..read]);
                        }
                        remaining = remaining.saturating_sub(read as u64);
                        if full_tx.send(Ok((buf, read))).is_err() {
                            break;
                        }
                    }
                    Err(error) => {
                        let _ = full_tx.send(Err(Failure::io("Read source", src, error)));
                        break;
                    }
                }
            }
        });

        while copied < file_len {
            if let Err(err) = ctx.progress.check_cancelled() {
                write_result = Err(Failure::new(err, false));
                break;
            }
            match full_rx.recv() {
                Ok(Ok((buf, read))) => {
                    if let Some(limiter) = ctx.progress.rate_limit {
                        if let Err(err) = limiter
                            .wait(read, ctx.progress.cancel)
                            .map_err(|e| Failure::new(e, false))
                        {
                            write_result = Err(err);
                            break;
                        }
                    }
                    if let Err(error) = dst_file.write_all(&buf[..read]) {
                        write_result = Err(Failure::io("Write temporary destination", dst, error));
                        break;
                    }
                    let _ = free_tx.send(buf);
                    copied = copied.saturating_add(read as u64);
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
                Ok(Err(read_failure)) => {
                    write_result = Err(read_failure);
                    break;
                }
                Err(_) => {
                    if copied < file_len {
                        write_result = Err(Failure::new(
                            format!("Source became shorter during transfer [{}]", src.display()),
                            false,
                        ));
                    }
                    break;
                }
            }
        }
        drop(full_rx);
        drop(free_tx);
    });

    write_result?;
    ctx.progress.finalizing(
        completed_bytes.saturating_add(copied),
        src.to_string_lossy().into(),
    );
    ctx.progress.check_cancelled()?;
    dst_file
        .flush()
        .map_err(|e| Failure::io("Flush temporary destination", dst, e))?;
    Ok(copied)
}

/// Multi-stream parallel byte-range copy (`rclone --multi-thread-streams` style)
/// for single large files on Windows using positional `seek_read` and `seek_write`.
#[cfg(windows)]
#[allow(clippy::too_many_arguments)]
pub(crate) fn copy_file_multistream(
    src_file: &fs::File,
    dst_file: &mut fs::File,
    src: &Path,
    dst: &Path,
    ctx: &CopyContext<'_>,
    completed_bytes: u64,
    file_len: u64,
    buffer_size: usize,
    streams: usize,
) -> Result<u64, Failure> {
    use std::os::windows::fs::FileExt;

    let stream_count = streams.clamp(1, 16);
    dst_file
        .set_len(file_len)
        .map_err(|e| Failure::io("Preallocate multistream destination", dst, e))?;

    let stripe_cancel = AtomicBool::new(false);
    let copied_atomic = AtomicU64::new(0);
    let (done_tx, done_rx) = mpsc::sync_channel::<Result<u64, Failure>>(stream_count);
    let cancel = ctx.progress.cancel;
    let rate_limit = ctx.progress.rate_limit;

    let chunk_align = (buffer_size as u64).max(64 * 1024);
    let raw_stripe = file_len.div_ceil(stream_count as u64);
    let aligned_stripe = raw_stripe.div_ceil(chunk_align) * chunk_align;
    let dst_shared: &fs::File = &*dst_file;

    let stripe_res = std::thread::scope(|scope| {
        for idx in 0..stream_count {
            let start = (idx as u64).saturating_mul(aligned_stripe).min(file_len);
            let end = if idx + 1 == stream_count {
                file_len
            } else {
                ((idx as u64 + 1).saturating_mul(aligned_stripe)).min(file_len)
            };
            let tx = done_tx.clone();
            let local_cancel = &stripe_cancel;
            let copied_ref = &copied_atomic;
            scope.spawn(move || {
                if start >= end {
                    let _ = tx.send(Ok(0));
                    return;
                }
                let mut buf = PooledBuffer::acquire(buffer_size);
                let mut offset = start;
                let mut stripe_copied = 0u64;
                while offset < end {
                    if cancel.load(Ordering::Relaxed) || local_cancel.load(Ordering::Relaxed) {
                        let _ = tx.send(Err(Failure::new("Operation cancelled", false)));
                        return;
                    }
                    let requested =
                        buffer_size.min(usize::try_from(end - offset).unwrap_or(usize::MAX));
                    if let Some(limiter) = rate_limit {
                        if let Err(err) = limiter.wait(requested, cancel) {
                            local_cancel.store(true, Ordering::Relaxed);
                            let _ = tx.send(Err(Failure::new(err, false)));
                            return;
                        }
                    }
                    let mut read_total = 0usize;
                    while read_total < requested {
                        match src_file.seek_read(
                            &mut buf.as_mut_slice()[read_total..requested],
                            offset + read_total as u64,
                        ) {
                            Ok(0) => {
                                local_cancel.store(true, Ordering::Relaxed);
                                let _ = tx.send(Err(Failure::new(
                                    format!(
                                        "Source became shorter during transfer [{}]",
                                        src.display()
                                    ),
                                    false,
                                )));
                                return;
                            }
                            Ok(n) => read_total += n,
                            Err(err) => {
                                local_cancel.store(true, Ordering::Relaxed);
                                let _ = tx.send(Err(Failure::io("Read source", src, err)));
                                return;
                            }
                        }
                    }
                    let mut written_total = 0usize;
                    while written_total < read_total {
                        match dst_shared.seek_write(
                            &buf[written_total..read_total],
                            offset + written_total as u64,
                        ) {
                            Ok(0) => {
                                local_cancel.store(true, Ordering::Relaxed);
                                let _ = tx.send(Err(Failure::new(
                                    format!(
                                        "Write temporary destination returned zero bytes [{}]",
                                        dst.display()
                                    ),
                                    false,
                                )));
                                return;
                            }
                            Ok(n) => written_total += n,
                            Err(err) => {
                                local_cancel.store(true, Ordering::Relaxed);
                                let _ = tx.send(Err(Failure::io(
                                    "Write temporary destination",
                                    dst,
                                    err,
                                )));
                                return;
                            }
                        }
                    }
                    offset += read_total as u64;
                    stripe_copied += read_total as u64;
                    copied_ref.fetch_add(read_total as u64, Ordering::Relaxed);
                }
                let _ = tx.send(Ok(stripe_copied));
            });
        }
        drop(done_tx);

        let mut finished = 0usize;
        let mut first_err: Option<Failure> = None;
        let mut next_emit_at = PROGRESS_BYTE_STEP;
        while finished < stream_count {
            if let Err(err) = ctx.progress.check_cancelled() {
                stripe_cancel.store(true, Ordering::Relaxed);
                if first_err.is_none() {
                    first_err = Some(Failure::new(err, false));
                }
            }
            match done_rx.recv_timeout(Duration::from_millis(25)) {
                Ok(Ok(_)) => finished += 1,
                Ok(Err(err)) => {
                    finished += 1;
                    stripe_cancel.store(true, Ordering::Relaxed);
                    if first_err.is_none() {
                        first_err = Some(err);
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }
            let current_copied = copied_atomic.load(Ordering::Relaxed);
            if current_copied >= next_emit_at && first_err.is_none() {
                ctx.progress.emit(
                    completed_bytes.saturating_add(current_copied),
                    src.to_string_lossy().into(),
                    "running",
                    None,
                );
                next_emit_at = current_copied.saturating_add(PROGRESS_BYTE_STEP);
            }
        }
        if let Some(err) = first_err {
            return Err(err);
        }
        Ok(copied_atomic.load(Ordering::Relaxed))
    });
    let copied = match stripe_res {
        Ok(copied) => copied,
        Err(err) => {
            let _ = dst_file.set_len(0);
            return Err(err);
        }
    };
    ctx.progress.finalizing(
        completed_bytes.saturating_add(copied),
        src.to_string_lossy().into(),
    );
    ctx.progress.check_cancelled().map_err(|err| {
        let _ = dst_file.set_len(0);
        Failure::new(err, false)
    })?;
    dst_file.flush().map_err(|e| {
        let _ = dst_file.set_len(0);
        Failure::io("Flush temporary destination", dst, e)
    })?;
    Ok(copied)
}

#[cfg(not(windows))]
#[allow(clippy::too_many_arguments)]
pub(crate) fn copy_file_multistream(
    _src_file: &fs::File,
    _dst_file: &mut fs::File,
    _src: &Path,
    _dst: &Path,
    _ctx: &CopyContext<'_>,
    _completed_bytes: u64,
    _file_len: u64,
    _buffer_size: usize,
    _streams: usize,
) -> Result<u64, Failure> {
    Err(Failure::new("Multistream copy not supported", false))
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
    let mut buffer = PooledBuffer::acquire(buffer_size);
    let mut copied = resume_from;
    let mut next_emit_at = ((resume_from / PROGRESS_BYTE_STEP) + 1) * PROGRESS_BYTE_STEP;
    while copied < file_len {
        ctx.progress.check_cancelled()?;
        let requested = buffer_size.min((file_len - copied) as usize);
        if let Some(limiter) = ctx.progress.rate_limit {
            limiter.wait(requested, ctx.progress.cancel)?;
        }
        let read = reader
            .read(&mut buffer.as_mut_slice()[..requested])
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
        if existing_file_matches_source_with_mode(src, dst, ctx.policy.check_mode)? {
            let version = capture_source_version_for_path(src)?;
            *completed_bytes = completed_bytes.saturating_add(file_len);
            ctx.progress
                .complete_file(*completed_bytes, src.to_string_lossy().to_string());
            return Ok(version);
        }

        remove_path(dst, "resumable destination")?;
    }

    let use_inplace = ctx.policy.inplace && !path_exists_no_follow(dst);
    let staged_path = if use_inplace {
        dst.to_path_buf()
    } else {
        let path = staging_path_for(dst, ctx.operation_id)?;
        cleanup_stale_file_staging(dst, &path);
        path
    };
    let must_sync = ctx.policy.flush_on_close
        && (replace_existing
            || ctx.resume_existing
            || ctx.network
            || ctx.progress.operation_type != "copy"
            || ctx.policy.check_mode == CheckMode::Checksum);
    let mut retry = Retry::from_policy(&ctx.policy);
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
            must_sync,
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
                let _ = cleanup_owned_path(&staged_path);
                return Err(error);
            }
            if !use_inplace {
                if let Err(error) = promote_with_policy(
                    &staged_path,
                    dst,
                    replace_existing || ctx.resume_existing,
                    ctx.progress.cancel,
                    &ctx.policy,
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
                            format!("Finalize destination [{}]: {error}", dst.display())
                        },
                    );
                }
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
            match cleanup_owned_path(&staged_path) {
                Ok(()) => Err(error),
                Err(cleanup) => Err(format!("{error}; {cleanup}")),
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
    if let Err(error) = promote_with_policy(
        &staged_path,
        dst_path,
        replace_existing || ctx.resume_existing,
        ctx.progress.cancel,
        &ctx.policy,
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

pub(crate) fn copy_plan_with_progress(
    plan: &TransferPlan,
    ctx: &ProgressContext,
    completed_bytes: &mut u64,
    session: &mut super::adaptive::Session<'_>,
) -> Result<(), String> {
    ensure_destination_available(plan)?;
    let active_policy = session.controller.policy.clone();

    let source_meta = fs::symlink_metadata(&plan.source_path)
        .map_err(|error| format!("Failed to stat source: {error}"))?;
    if source_meta.file_type().is_dir() {
        let adopted_legacy =
            adopt_or_cleanup_legacy_dir_staging(&plan.final_dest, ctx.operation_id);
        let tree_ctx = CopyContext {
            progress: ctx,
            operation_id: ctx.operation_id,
            network: plan.network,
            resume_existing: adopted_legacy,
            policy: active_policy.clone(),
        };
        let result = super::parallel::copy_tree(
            &plan.source_path,
            &plan.final_dest,
            &tree_ctx,
            completed_bytes,
            plan.replace_existing,
            session,
        );
        if let Err(error) = result {
            if adopted_legacy {
                let _ = cleanup_owned_path(&plan.final_dest);
            }
            return Err(error);
        }
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

    let active_policy = session.controller.policy.clone();

    let source_meta = fs::symlink_metadata(&plan.source_path)
        .map_err(|error| format!("Failed to stat source: {error}"))?;
    if source_meta.file_type().is_dir() {
        let adopted_legacy =
            adopt_or_cleanup_legacy_dir_staging(&plan.final_dest, ctx.operation_id);
        let tree_ctx = CopyContext {
            progress: ctx,
            operation_id: ctx.operation_id,
            network: plan.network,
            resume_existing: adopted_legacy,
            policy: active_policy.clone(),
        };
        let manifest = match super::parallel::copy_tree(
            &plan.source_path,
            &plan.final_dest,
            &tree_ctx,
            completed_bytes,
            plan.replace_existing,
            session,
        ) {
            Ok(manifest) => manifest,
            Err(error) => {
                if adopted_legacy {
                    let _ = cleanup_owned_path(&plan.final_dest);
                }
                return Err(error);
            }
        };
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
