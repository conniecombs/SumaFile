//! Copy and move execution with progress callbacks.

use super::parallel::ConflictAction;
use super::reporting::{CopyContext, PROGRESS_BYTE_STEP};
use super::resources::PooledBuffer;
use super::tuning::CheckMode;
use crate::transfer_reliability::{Failure, FailureKind, Retry};
use crate::transfer_staging::{
    capture_source_version_for_path, capture_source_version_from_open_file, cleanup_owned_path,
    conflict_for_existing_destination, existing_file_matches_source_with_mode, hash_file_contents,
    preallocate_file, promote_leased_with_policy, remove_path, staging_path_for,
    verify_source_unchanged, ContentHasher, SourceVersion,
};
use simplefile_core::path_conflict::path_exists_no_follow;
use simplefile_core::utils::recreate_symlink;
use std::fs;
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
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
    copy_file_attempt_versioned(
        src,
        dst,
        None,
        false,
        ctx,
        completed_bytes,
        buffer_size,
        file_len,
        true,
    )
    .map(|(copied, _)| copied)
}

/// `staged_file` is the destination handle the ownership lease created for this
/// operation (first attempt). Retries pass `None` and resume the existing partial.
/// `trusted_prefix` says the partial was written by this process's earlier
/// attempt on the same lease, so its bytes are resumed without being re-read
/// from the source; a partial of unknown origin is still compared byte for byte.
#[allow(clippy::too_many_arguments)]
pub(crate) fn copy_file_attempt_versioned(
    src: &Path,
    dst: &Path,
    staged_file: Option<fs::File>,
    trusted_prefix: bool,
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
    let first_open = match staged_file {
        Some(file) => Ok(file),
        None => match create_new_staging_sequential(dst) {
            Ok(file) => Ok(file),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                if let Some(parent) = dst.parent() {
                    fs::create_dir_all(parent)
                        .map_err(|e| Failure::io("Create parent directory", parent, e))?;
                }
                create_new_staging_sequential(dst)
            }
            Err(error) => Err(error),
        },
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
            if existing > 0 && !trusted_prefix {
                // Resume only a verified prefix. A reused operation ID or a
                // short write cannot silently splice stale bytes into a move.
                let mut partial = fs::File::open(dst)
                    .map_err(|e| Failure::io("Inspect resume prefix", dst, e))?;
                let chunk = crate::transfer_staging::VERIFY_CHUNK;
                let mut left = PooledBuffer::acquire(chunk);
                let mut right = PooledBuffer::acquire(chunk);
                let mut remaining = existing;
                while remaining > 0 {
                    ctx.progress.check_cancelled()?;
                    let count = remaining.min(chunk as u64) as usize;
                    src_file
                        .read_exact(&mut left.as_mut_slice()[..count])
                        .map_err(|e| Failure::io("Read source prefix", src, e))?;
                    partial
                        .read_exact(&mut right.as_mut_slice()[..count])
                        .map_err(|e| Failure::io("Read resume prefix", dst, e))?;
                    if left[..count] != right[..count] {
                        return Err(Failure::new(
                            "Temporary destination does not match the source prefix",
                            false,
                        ));
                    }
                    remaining -= count as u64;
                }
            }
            if existing > 0 {
                src_file
                    .seek(SeekFrom::Start(existing))
                    .map_err(|e| Failure::io("Resume source", src, e))?;
            }
            // An existing zero-byte, operation-owned partial is a valid resume point too.
            let mut file = fs::OpenOptions::new()
                .write(true)
                .open(dst)
                .map_err(|e| Failure::io("Open temporary destination", dst, e))?;
            file.seek(SeekFrom::Start(existing))
                .map_err(|e| Failure::io("Seek temporary destination", dst, e))?;
            if existing == 0 && ctx.policy.preallocate {
                preallocate_file(&file, file_len);
            }
            (existing, file)
        }
    };

    let verify_checksum = ctx.policy.check_mode == CheckMode::Checksum;
    // Size, timestamps and file identity guard the default modes; hashing every
    // byte (and re-reading the source to check it before a move deletes it) is
    // what the explicit checksum mode asks for.
    let need_hash = verify_checksum;
    let mut stream_hasher = ContentHasher::new();
    let remaining = file_len.saturating_sub(resume_from);
    let multi_streams = ctx.policy.budgeted_streams(ctx.network);
    let multi_cutoff = ctx.policy.multi_thread_cutoff_bytes();

    let multistream_eligible = cfg!(windows)
        && resume_from == 0
        && !need_hash
        && !ctx.network
        && multi_streams >= 2
        && file_len >= multi_cutoff
        && file_len >= (buffer_size as u64).saturating_mul(2)
        // Independent byte ranges only pay off on a local fixed disk; a share
        // or a cloud mount serves one sequential stream far better.
        && simplefile_core::utils::is_local_fixed_volume(src)
        && simplefile_core::utils::is_local_fixed_volume(dst);
    let copied = if multistream_eligible {
        copy_file_multistream(
            src,
            dst,
            &mut dst_file,
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
    // Timestamps, the read-only bit and the staging hidden flag in one call on
    // the handle we already hold, instead of reopening the file afterwards.
    let is_staging = dst
        .file_name()
        .and_then(|n| n.to_str())
        .is_some_and(|n| n.ends_with(".sumafile-partial"));
    // Every byte is already written and verified below; a target that refuses
    // timestamp or attribute updates (some SMB and FAT volumes) gets the same
    // treatment as directories: keep the file, log the loss.
    if let Err(error) = simplefile_core::file_ops::apply_basic_metadata_to_handle(
        &dst_file,
        &source_after,
        is_staging,
    ) {
        log::warn!(
            "Could not preserve file metadata for {}: {error}",
            dst.display()
        );
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
                    let write_started = ctx.progress.write_started(read);
                    if let Err(error) = dst_file.write_all(&buf[..read]) {
                        write_result = Err(Failure::io("Write temporary destination", dst, error));
                        break;
                    }
                    ctx.progress.write_finished(write_started, read);
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
/// for single large files on local fixed disks.
///
/// Every stripe opens its own source and destination handle: Windows serialises
/// I/O on a handle opened without `FILE_FLAG_OVERLAPPED`, so sharing one handle
/// between threads only interleaves offsets without concurrency. The staging
/// file is preallocated by allocation size and marked sparse for the duration of
/// the copy so out-of-order stripe writes do not make NTFS zero-fill the gap
/// between the valid data length and each write; the sparse flag is cleared
/// again once every byte is written.
#[cfg(windows)]
#[allow(clippy::too_many_arguments)]
pub(crate) fn copy_file_multistream(
    src: &Path,
    dst: &Path,
    dst_file: &mut fs::File,
    ctx: &CopyContext<'_>,
    completed_bytes: u64,
    file_len: u64,
    buffer_size: usize,
    streams: usize,
) -> Result<u64, Failure> {
    use std::os::windows::fs::FileExt;
    use sumafile_transfer::tuning::LatencyHistogram;

    let stream_count = streams.clamp(1, 16);
    preallocate_file(dst_file, file_len);
    let sparse = set_sparse(dst_file, true);

    let stripe_cancel = AtomicBool::new(false);
    let copied_atomic = AtomicU64::new(0);
    let (done_tx, done_rx) = mpsc::sync_channel::<Result<u64, Failure>>(stream_count);
    let cancel = ctx.progress.cancel;
    let rate_limit = ctx.progress.rate_limit;
    let write_latency = ctx.progress.write_latency;

    let chunk_align = (buffer_size as u64).max(64 * 1024);
    let raw_stripe = file_len.div_ceil(stream_count as u64);
    let aligned_stripe = raw_stripe.div_ceil(chunk_align) * chunk_align;

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
                let src_handle = match open_source_sequential(src) {
                    Ok(file) => file,
                    Err(err) => {
                        local_cancel.store(true, Ordering::Relaxed);
                        let _ = tx.send(Err(Failure::io("Open source stripe", src, err)));
                        return;
                    }
                };
                let dst_handle = match fs::OpenOptions::new().write(true).open(dst) {
                    Ok(file) => file,
                    Err(err) => {
                        local_cancel.store(true, Ordering::Relaxed);
                        let _ = tx.send(Err(Failure::io(
                            "Open temporary destination stripe",
                            dst,
                            err,
                        )));
                        return;
                    }
                };
                let mut buf = PooledBuffer::acquire(buffer_size);
                let mut offset = start;
                let mut stripe_copied = 0u64;
                while offset < end {
                    if cancel.load(Ordering::Relaxed) {
                        let _ = tx.send(Err(Failure::cancelled()));
                        return;
                    }
                    if local_cancel.load(Ordering::Relaxed) {
                        // A sibling owns the originating error. Do not race it
                        // with a synthetic user-cancellation result.
                        let _ = tx.send(Ok(0));
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
                        match src_handle.seek_read(
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
                    let write_started = write_latency
                        .filter(|_| LatencyHistogram::times(read_total))
                        .map(|_| std::time::Instant::now());
                    let mut written_total = 0usize;
                    while written_total < read_total {
                        match dst_handle.seek_write(
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
                    if let (Some(histogram), Some(started)) = (write_latency, write_started) {
                        histogram.record(started.elapsed(), read_total);
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
    if sparse {
        set_sparse(dst_file, false);
    }
    ctx.progress.finalizing(
        completed_bytes.saturating_add(copied),
        src.to_string_lossy().into(),
    );
    ctx.progress.check_cancelled().map_err(|err| {
        let _ = dst_file.set_len(0);
        Failure::new(err, false)
    })?;
    if copied != file_len {
        let _ = dst_file.set_len(0);
        return Err(Failure::new(
            format!(
                "Multistream copy wrote {copied} of {file_len} bytes [{}]",
                dst.display()
            ),
            false,
        ));
    }
    Ok(copied)
}

/// Mark or unmark the file sparse. Best effort: a filesystem without sparse
/// support simply pays the zero-fill cost. Returns whether the flag was set.
#[cfg(windows)]
fn set_sparse(file: &fs::File, sparse: bool) -> bool {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::System::Ioctl::{FILE_SET_SPARSE_BUFFER, FSCTL_SET_SPARSE};
    use windows_sys::Win32::System::IO::DeviceIoControl;

    let input = FILE_SET_SPARSE_BUFFER { SetSparse: sparse };
    let mut returned = 0u32;
    let ok = unsafe {
        DeviceIoControl(
            file.as_raw_handle() as _,
            FSCTL_SET_SPARSE,
            (&input as *const FILE_SET_SPARSE_BUFFER).cast(),
            std::mem::size_of::<FILE_SET_SPARSE_BUFFER>() as u32,
            std::ptr::null_mut(),
            0,
            &mut returned,
            std::ptr::null_mut(),
        )
    };
    if ok == 0 {
        log::debug!(
            "FSCTL_SET_SPARSE({sparse}) not applied: {}",
            std::io::Error::last_os_error()
        );
        return false;
    }
    true
}

#[cfg(not(windows))]
#[allow(clippy::too_many_arguments)]
pub(crate) fn copy_file_multistream(
    _src: &Path,
    _dst: &Path,
    _dst_file: &mut fs::File,
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
        let write_started = ctx.progress.write_started(read);
        writer
            .write_all(&buffer[..read])
            .map_err(|e| Failure::io("Write temporary destination", dst, e))?;
        ctx.progress.write_finished(write_started, read);
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

/// What happened to one file after its copy attempt(s).
#[derive(Debug)]
pub(crate) enum FileOutcome {
    Copied {
        version: SourceVersion,
        /// Where the file was published; differs from the plan after keep-both.
        destination: PathBuf,
    },
    /// The destination existed and the action was skip.
    Skipped,
}

#[cfg(test)]
pub(crate) fn copy_file_with_progress(
    src: &Path,
    dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    replace_existing: bool,
) -> Result<(), String> {
    copy_file_with_progress_versioned(
        src,
        dst,
        ctx,
        completed_bytes,
        if replace_existing {
            ConflictAction::Replace
        } else {
            ConflictAction::Error
        },
    )
    .map(|_| ())
    .map_err(|error| error.message)
}

/// Resolve the planned destination against what exists there right now.
/// Returns the destination to use and whether an existing file may be replaced,
/// or `None` when the file should be skipped.
fn resolve_file_conflict(
    dst: &Path,
    conflict: ConflictAction,
    resume_existing: bool,
) -> Result<Option<(PathBuf, bool)>, Failure> {
    match conflict {
        ConflictAction::Replace => {
            // Replace swaps a file for a file. A directory occupying the name is
            // a type conflict the user must resolve; never delete it to make room.
            let existing_is_dir = fs::symlink_metadata(dst)
                .is_ok_and(|meta| meta.file_type().is_dir() && !meta.file_type().is_symlink());
            if existing_is_dir {
                return Err(Failure::conflict(format!(
                    "destination exists with a different type: {}",
                    dst.display()
                )));
            }
            Ok(Some((dst.to_path_buf(), true)))
        }
        ConflictAction::Error => Ok(Some((dst.to_path_buf(), false))),
        ConflictAction::Skip => {
            if !resume_existing && path_exists_no_follow(dst) {
                Ok(None)
            } else {
                Ok(Some((dst.to_path_buf(), false)))
            }
        }
        ConflictAction::KeepBoth => {
            if !resume_existing && path_exists_no_follow(dst) {
                let parent = dst
                    .parent()
                    .ok_or_else(|| Failure::from("Cannot get destination directory"))?;
                let name = dst
                    .file_name()
                    .ok_or_else(|| Failure::from("Cannot get destination file name"))?;
                let unique = super::plan::unique_destination_path(
                    parent,
                    name,
                    &std::collections::HashSet::new(),
                )?;
                Ok(Some((unique, false)))
            } else {
                Ok(Some((dst.to_path_buf(), false)))
            }
        }
    }
}

pub(crate) fn copy_file_with_progress_versioned(
    src: &Path,
    planned_dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    conflict: ConflictAction,
) -> Result<FileOutcome, Failure> {
    let source_metadata =
        fs::symlink_metadata(src).map_err(|error| Failure::io("Inspect source", src, error))?;
    let file_len = source_metadata.len();
    let source_modified = source_metadata.modified().ok();
    let known = ctx.progress.total_bytes.load(Ordering::Relaxed);
    let needed = (*completed_bytes).saturating_add(file_len);
    if needed > known {
        ctx.progress.total_bytes.store(needed, Ordering::Relaxed);
    }

    if ctx.resume_existing && path_exists_no_follow(planned_dst) {
        if existing_file_matches_source_with_mode(src, planned_dst, ctx.policy.check_mode)? {
            let version = capture_source_version_for_path(src)?;
            *completed_bytes = completed_bytes.saturating_add(file_len);
            ctx.progress
                .complete_file(*completed_bytes, src.to_string_lossy().to_string());
            return Ok(FileOutcome::Copied {
                version,
                destination: planned_dst.to_path_buf(),
            });
        }

        remove_path(planned_dst, "resumable destination")?;
    }

    let Some((dst, replace_existing)) =
        resolve_file_conflict(planned_dst, conflict, ctx.resume_existing)?
    else {
        return Ok(FileOutcome::Skipped);
    };
    let dst = dst.as_path();

    let exists = path_exists_no_follow(dst);
    let use_inplace = ctx.policy.inplace;
    if use_inplace && exists && !replace_existing {
        return Err(Failure::conflict(conflict_for_existing_destination(dst)));
    }
    let staged_path = if use_inplace {
        dst.to_path_buf()
    } else {
        staging_path_for(dst, ctx.operation_id)?
    };
    let mut lease = Some(crate::transfer_journal::StagingLease::begin(
        &src.to_string_lossy(),
        dst,
        &staged_path,
        use_inplace && exists,
    )?);
    let must_sync = ctx.policy.flush_on_close;
    let mut retry = Retry::from_policy(&ctx.policy);
    let copy_result = loop {
        if ctx.progress.check_cancelled().is_err() {
            break Err(Failure::cancelled());
        }
        if retry.failures > 0
            && fs::metadata(src)
                .is_ok_and(|meta| meta.len() != file_len || meta.modified().ok() != source_modified)
        {
            break Err(Failure::from(format!(
                "Source changed before retry [{}]",
                src.display()
            )));
        }
        let staged_file = lease.as_mut().and_then(|owner| owner.take_staged_file());
        // The lease created the staging file exclusively, so any bytes already
        // in it came from this operation's previous attempt.
        let trusted_prefix = retry.failures > 0;
        match copy_file_attempt_versioned(
            src,
            &staged_path,
            staged_file,
            trusted_prefix,
            ctx,
            *completed_bytes,
            ctx.buffer_size(),
            file_len,
            must_sync,
        ) {
            Ok((written, version)) => break Ok((written, version)),
            Err(error) if error.is_cancelled() => break Err(error),
            Err(error) => {
                let reason = error.to_string();
                let transport_retry = error.kind == FailureKind::Retryable;
                match retry.failed_with(error) {
                    Err(error) => break Err(error),
                    Ok(delay) => {
                        // A file held open elsewhere says nothing about the
                        // transport; only transient I/O feeds the tuner.
                        if transport_retry {
                            if let Some(retries) = ctx.progress.retry_count {
                                retries.fetch_add(1, Ordering::Relaxed);
                            }
                        }
                        if let Some(limiter) = ctx.progress.rate_limit {
                            limiter.retrying(delay, &reason);
                        }
                        if crate::transfer_reliability::wait(delay, ctx.progress.cancel).is_err() {
                            break Err(Failure::cancelled());
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
            if ctx.progress.check_cancelled().is_err() {
                if let Some(owner) = lease.take() {
                    let _ = owner.abort();
                }
                return Err(Failure::cancelled());
            }
            if !use_inplace {
                if let Err(error) = promote_leased_with_policy(
                    &staged_path,
                    dst,
                    replace_existing || ctx.resume_existing,
                    ctx.progress.cancel,
                    &ctx.policy,
                    lease.as_mut().expect("staged copy has an ownership lease"),
                    |delay, reason| {
                        if let Some(limiter) = ctx.progress.rate_limit {
                            limiter.retrying(delay, reason);
                        }
                    },
                ) {
                    if let Some(owner) = lease.take() {
                        let _ = owner.abort();
                    }
                    let failure = error;
                    return Err(if failure.is_conflict() || failure.is_cancelled() {
                        failure
                    } else {
                        Failure::with_kind(
                            format!("Finalize destination [{}]: {}", dst.display(), failure),
                            failure.kind,
                        )
                    });
                }
            }
            if let Some(owner) = lease.take() {
                owner.finish()?;
            }
            *completed_bytes += written;
            ctx.progress
                .complete_file(*completed_bytes, src.to_string_lossy().to_string());
            Ok(FileOutcome::Copied {
                version,
                destination: dst.to_path_buf(),
            })
        }
        Err(error) => {
            if fs::symlink_metadata(&staged_path)
                .is_ok_and(|meta| !meta.file_type().is_file() || meta.len() > file_len)
            {
                return Err(error);
            }
            let cleanup = if let Some(owner) = lease.take() {
                owner.abort()
            } else {
                cleanup_owned_path(&staged_path)
            };
            match cleanup {
                Ok(()) => Err(error),
                Err(cleanup) => Err(Failure::with_kind(
                    format!("{error}; {cleanup}"),
                    error.kind,
                )),
            }
        }
    }
}

pub(crate) fn copy_symlink_with_progress(
    src_path: &Path,
    planned_dst: &Path,
    ctx: &CopyContext,
    completed_bytes: u64,
    conflict: ConflictAction,
) -> Result<FileOutcome, Failure> {
    let Some((dst_path, replace_existing)) =
        resolve_file_conflict(planned_dst, conflict, ctx.resume_existing)?
    else {
        return Ok(FileOutcome::Skipped);
    };
    let dst_path = dst_path.as_path();
    if !replace_existing && !ctx.resume_existing && path_exists_no_follow(dst_path) {
        return Err(Failure::conflict(conflict_for_existing_destination(
            dst_path,
        )));
    }
    let version = capture_source_version_for_path(src_path)?;
    let staged_path = staging_path_for(dst_path, ctx.operation_id)?;
    if path_exists_no_follow(&staged_path) {
        return Err(Failure::from(
            "Refusing an unowned temporary symlink destination",
        ));
    }
    recreate_symlink(src_path, &staged_path)?;
    let mut lease = crate::transfer_journal::StagingLease::record_created(
        &src_path.to_string_lossy(),
        dst_path,
        &staged_path,
    )?;
    if let Err(error) = verify_source_unchanged(src_path, &version) {
        let _ = lease.abort();
        return Err(Failure::from(error));
    }
    if ctx.progress.check_cancelled().is_err() {
        let _ = lease.abort();
        return Err(Failure::cancelled());
    }
    if let Err(error) = promote_leased_with_policy(
        &staged_path,
        dst_path,
        replace_existing || ctx.resume_existing,
        ctx.progress.cancel,
        &ctx.policy,
        &mut lease,
        |delay, reason| {
            if let Some(limiter) = ctx.progress.rate_limit {
                limiter.retrying(delay, reason);
            }
        },
    ) {
        let _ = lease.abort();
        let failure = error;
        return Err(if failure.is_conflict() || failure.is_cancelled() {
            failure
        } else {
            Failure::with_kind(
                format!("Finalize destination [{}]: {}", dst_path.display(), failure),
                failure.kind,
            )
        });
    }
    lease.finish()?;
    ctx.progress
        .complete_file(completed_bytes, src_path.to_string_lossy().to_string());
    Ok(FileOutcome::Copied {
        version,
        destination: dst_path.to_path_buf(),
    })
}

/// Same-volume moves rename instead of copying. `None` means "not applicable,
/// copy instead" (different volume, an unpublished tree, or a rename error that
/// a copy may still get around).
pub(crate) fn move_by_rename(
    source: &Path,
    planned_dst: &Path,
    conflict: ConflictAction,
    ctx: &CopyContext,
) -> Option<Result<FileOutcome, Failure>> {
    use simplefile_core::path_conflict::{is_already_exists_error, rename_exclusive};

    if ctx.resume_existing || !super::plan::same_volume(source, planned_dst) {
        return None;
    }
    let (dst, replace_existing) = match resolve_file_conflict(planned_dst, conflict, false) {
        Ok(Some(resolved)) => resolved,
        Ok(None) => return Some(Ok(FileOutcome::Skipped)),
        Err(error) => return Some(Err(error)),
    };
    if ctx.progress.check_cancelled().is_err() {
        return Some(Err(Failure::cancelled()));
    }
    match rename_exclusive(source, &dst) {
        Ok(()) => Some(renamed_outcome(&dst)),
        Err(error) if is_already_exists_error(&error) || path_exists_no_follow(&dst) => {
            if !replace_existing {
                return Some(Err(Failure::conflict(conflict_for_existing_destination(
                    &dst,
                ))));
            }
            let existing_is_dir = fs::symlink_metadata(&dst)
                .is_ok_and(|meta| meta.file_type().is_dir() && !meta.file_type().is_symlink());
            if existing_is_dir {
                return Some(Err(Failure::conflict(format!(
                    "destination exists with a different type: {}",
                    dst.display()
                ))));
            }
            // Swap through a unique backup name: the source is renamed, never
            // copied, so no journal entry may ever treat it as a disposable
            // staging object. A locked destination gets the same bounded,
            // visible retries as a staged promotion.
            let mut retry = Retry::from_policy(&ctx.policy);
            loop {
                if ctx.progress.check_cancelled().is_err() {
                    return Some(Err(Failure::cancelled()));
                }
                match swap_source_into_place(source, &dst) {
                    Ok(()) => return Some(renamed_outcome(&dst)),
                    Err(error) => {
                        let reason = error.to_string();
                        match retry.failed_with(error) {
                            Err(error) => return Some(Err(error)),
                            Ok(delay) => {
                                if let Some(limiter) = ctx.progress.rate_limit {
                                    limiter.retrying(delay, &reason);
                                }
                                if crate::transfer_reliability::wait(delay, ctx.progress.cancel)
                                    .is_err()
                                {
                                    return Some(Err(Failure::cancelled()));
                                }
                            }
                        }
                    }
                }
            }
        }
        Err(_) => None,
    }
}

/// Rename the existing destination aside, rename the source into place, then
/// remove the backup. Any failure leaves the original destination in place.
fn swap_source_into_place(source: &Path, dst: &Path) -> Result<(), Failure> {
    use simplefile_core::path_conflict::rename_exclusive;

    let backup = crate::transfer_staging::unique_backup_path(dst).map_err(Failure::from)?;
    rename_exclusive(dst, &backup)
        .map_err(|error| Failure::io("Prepare destination replacement", dst, error))?;
    if let Err(error) = rename_exclusive(source, dst) {
        let restore = rename_exclusive(&backup, dst);
        return Err(match restore {
            Ok(()) => Failure::io(
                "Finalize destination (original destination was restored)",
                dst,
                error,
            ),
            Err(restore) => Failure::from(format!(
                "Finalize destination [{}]: {error}; original remains [{}], restore failed: {restore}",
                dst.display(),
                backup.display()
            )),
        });
    }
    // Promotion already committed: never replay it just because backup cleanup failed.
    remove_path(&backup, "destination backup").map_err(|error| {
        Failure::from(format!(
            "Transferred, but backup remains [{}]: {error}",
            backup.display()
        ))
    })
}

fn renamed_outcome(dst: &Path) -> Result<FileOutcome, Failure> {
    let version = capture_source_version_for_path(dst)?;
    Ok(FileOutcome::Copied {
        version,
        destination: dst.to_path_buf(),
    })
}
