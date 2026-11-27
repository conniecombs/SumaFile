//! Copy and move execution with progress callbacks.

use super::parallel::ConflictAction;
use super::reporting::{CopyContext, PROGRESS_BYTE_STEP};
use super::resources::PooledBuffer;
use super::tuning::CheckMode;
use crate::transfer_reliability::{Failure, FailureKind, Retry};
use crate::transfer_staging::{
    capture_source_version_for_path, capture_source_version_from_open_file, cleanup_owned_path,
    conflict_for_existing_destination, hash_file_contents, preallocate_file,
    promote_leased_with_policy, source_version_from_metadata, staging_path_for,
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
use sumafile_transfer::ops::{record as count, Op};

const OVERLAPPED_RING_SLOTS: usize = 3;

pub(crate) fn is_real_directory(path: &Path) -> bool {
    count(Op::Stat, path);
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
    count(Op::Open, src);
    const FILE_FLAG_SEQUENTIAL_SCAN: u32 = 0x0800_0000;
    fs::OpenOptions::new()
        .read(true)
        .custom_flags(FILE_FLAG_SEQUENTIAL_SCAN)
        .open(src)
}

#[cfg(not(windows))]
fn open_source_sequential(src: &Path) -> std::io::Result<fs::File> {
    count(Op::Open, src);
    fs::File::open(src)
}

#[cfg(windows)]
fn create_new_staging_sequential(dst: &Path) -> std::io::Result<fs::File> {
    use std::os::windows::fs::OpenOptionsExt;
    count(Op::Open, dst);
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
    count(Op::Open, dst);
    fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(dst)
}

/// What an attempt does with the bytes a partial file already holds.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Partial {
    /// Of unknown origin: continued only after its bytes were compared with
    /// the start of the source.
    Compare,
    /// Written by this operation's earlier attempt, which stopped for a
    /// reason on the source's side: continued where it stopped, unread.
    Continue,
    /// A write or a flush failed, or the checksum did not match
    /// ([`Failure::restart`]), so the bytes may be wrong: the file is emptied
    /// and written again from its first byte.
    Discard,
}

#[cfg(test)]
pub(crate) fn copy_file_attempt(
    src: &Path,
    dst: &Path,
    partial: Partial,
    ctx: &CopyContext,
    completed_bytes: u64,
    buffer_size: usize,
    file_len: u64,
) -> Result<u64, Failure> {
    copy_file_attempt_versioned(
        src,
        dst,
        None,
        None,
        partial,
        ctx,
        completed_bytes,
        buffer_size,
        file_len,
        true,
    )
    .map(|(copied, _, _)| copied)
}

/// Faults a test can plant in a copy attempt.
#[cfg(test)]
pub(crate) mod test_faults {
    use std::io::{Read, Seek, SeekFrom, Write};
    use std::path::{Path, PathBuf};
    use std::sync::Mutex;

    static CORRUPT_ONCE: Mutex<Vec<PathBuf>> = Mutex::new(Vec::new());
    static FAIL: Mutex<Vec<String>> = Mutex::new(Vec::new());

    /// Every file whose partial file's name holds `name` fails once its
    /// bytes are written, for good: the failure is not retried. For a test
    /// that needs one file of a tree to fail; the name should be its own.
    pub(crate) fn fail_files_named(name: &str) {
        FAIL.lock().unwrap().push(name.to_string());
    }

    pub(super) fn injected_failure(written: &Path) -> Option<crate::transfer_reliability::Failure> {
        let name = written.file_name()?.to_string_lossy().into_owned();
        FAIL.lock()
            .unwrap()
            .iter()
            .any(|failing| name.contains(failing.as_str()))
            .then(|| {
                crate::transfer_reliability::Failure::new(
                    format!("Injected failure [{}]", written.display()),
                    false,
                )
            })
    }

    /// The next file written under `dir` gets its first byte changed after
    /// it was written and before it is verified. Once.
    pub(crate) fn corrupt_once_under(dir: &Path) {
        CORRUPT_ONCE.lock().unwrap().push(dir.to_path_buf());
    }

    /// Whether the fault planted for `dir` is still waiting for a file.
    pub(crate) fn pending_under(dir: &Path) -> bool {
        CORRUPT_ONCE.lock().unwrap().iter().any(|d| d == dir)
    }

    pub(super) fn after_write(written: &Path) {
        let planted = {
            let mut dirs = CORRUPT_ONCE.lock().unwrap();
            let found = dirs.iter().position(|dir| written.starts_with(dir));
            found.map(|index| dirs.remove(index))
        };
        if planted.is_none() {
            return;
        }
        let mut file = std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(written)
            .unwrap();
        let mut first = [0u8; 1];
        file.read_exact(&mut first).unwrap();
        file.seek(SeekFrom::Start(0)).unwrap();
        file.write_all(&[!first[0]]).unwrap();
    }
}

/// `staged_file` is the destination handle the ownership lease created for this
/// operation (first attempt). Retries pass `None` and say with `partial` what
/// to do with the bytes the earlier attempt left.
/// `source` is the source already opened for the first attempt, with its
/// metadata; later attempts open it again. On success, returns the bytes
/// copied, the source's version and the destination still open.
#[allow(clippy::too_many_arguments)]
pub(crate) fn copy_file_attempt_versioned(
    src: &Path,
    dst: &Path,
    staged_file: Option<fs::File>,
    source: Option<(fs::File, fs::Metadata)>,
    partial: Partial,
    ctx: &CopyContext,
    completed_bytes: u64,
    buffer_size: usize,
    file_len: u64,
    must_sync: bool,
) -> Result<(u64, SourceVersion, fs::File), Failure> {
    #[cfg(windows)]
    use std::os::windows::fs::MetadataExt;

    let buffer_size = buffer_size.min(usize::try_from(file_len.max(1)).unwrap_or(usize::MAX));
    let (mut src_file, source_initial) = match source {
        Some(opened) => opened,
        None => {
            let file =
                open_source_sequential(src).map_err(|e| Failure::io("Open source", src, e))?;
            count(Op::Query, src);
            let meta = file
                .metadata()
                .map_err(|e| Failure::io("Inspect opened source", src, e))?;
            (file, meta)
        }
    };
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
                    count(Op::CreateDir, parent);
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
            if ctx.policy.preallocates(ctx.network, file_len) {
                count(Op::Set, dst);
                preallocate_file(&file, file_len);
            }
            (0u64, file)
        }
        Err(open_error) => {
            count(Op::Stat, dst);
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
            // Bytes in doubt are not continued: the file is emptied below.
            let existing = if partial == Partial::Discard {
                0
            } else {
                existing
            };
            if existing > 0 && partial == Partial::Compare {
                // Resume only a verified prefix. A reused operation ID or a
                // short write cannot silently splice stale bytes into a move.
                count(Op::Open, dst);
                let mut partial = fs::File::open(dst)
                    .map_err(|e| Failure::io("Inspect resume prefix", dst, e))?;
                let chunk = crate::transfer_staging::VERIFY_CHUNK;
                let mut left = PooledBuffer::acquire(chunk);
                let mut right = PooledBuffer::acquire(chunk);
                let mut remaining = existing;
                while remaining > 0 {
                    ctx.progress.check_cancelled()?;
                    count(Op::Read, src);
                    count(Op::Read, dst);
                    let length = remaining.min(chunk as u64) as usize;
                    src_file
                        .read_exact(&mut left.as_mut_slice()[..length])
                        .map_err(|e| Failure::io("Read source prefix", src, e))?;
                    partial
                        .read_exact(&mut right.as_mut_slice()[..length])
                        .map_err(|e| Failure::io("Read resume prefix", dst, e))?;
                    if left[..length] != right[..length] {
                        return Err(Failure::new(
                            "Temporary destination does not match the source prefix",
                            false,
                        ));
                    }
                    remaining -= length as u64;
                }
            }
            if existing > 0 {
                src_file
                    .seek(SeekFrom::Start(existing))
                    .map_err(|e| Failure::io("Resume source", src, e))?;
            }
            // An existing zero-byte, operation-owned partial is a valid resume point too.
            count(Op::Open, dst);
            let mut file = fs::OpenOptions::new()
                .write(true)
                .open(dst)
                .map_err(|e| Failure::io("Open temporary destination", dst, e))?;
            if partial == Partial::Discard {
                count(Op::Set, dst);
                // Still in doubt when this fails, so the attempt after it
                // empties the file too.
                file.set_len(0).map_err(|e| {
                    Failure::io("Empty temporary destination", dst, e).restart_file()
                })?;
            }
            file.seek(SeekFrom::Start(existing))
                .map_err(|e| Failure::io("Seek temporary destination", dst, e))?;
            if existing == 0 && ctx.policy.preallocates(ctx.network, file_len) {
                count(Op::Set, dst);
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
        // A file written directly under its final name is written from its
        // first byte on: at full length it is whole, which is all recovery
        // can go by for it. Ranges written out of order reach full length
        // with the first range.
        && dst
            .file_name()
            .is_some_and(|name| name.to_string_lossy().ends_with(".sumafile-partial"))
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

    count(Op::Query, src);
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
    #[cfg(windows)]
    copy_alternate_streams(
        &src_file,
        src,
        &dst_file,
        dst,
        must_sync || verify_checksum,
        ctx.progress.operation_type == "move",
    )?;
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
    // Only the deletion of a moved source compares the file ID.
    let version = if ctx.progress.operation_type == "move" {
        count(Op::Query, src);
        capture_source_version_from_open_file(&src_file, &source_after, content_hash)
    } else {
        source_version_from_metadata(&source_after, content_hash)
    };
    ctx.progress.check_cancelled()?;
    // The checksum is of what the file holds once every write has reached
    // the drive, so Checksum flushes whatever the flush setting says: a
    // write the system reports as failed only later is caught here instead
    // of after the file was verified from memory.
    if must_sync || verify_checksum {
        count(Op::Flush, dst);
        dst_file
            .sync_all()
            .map_err(|e| Failure::io("Finalize temporary destination", dst, e).restart_file())?;
    }
    #[cfg(test)]
    test_faults::after_write(dst);
    #[cfg(test)]
    if let Some(failure) = test_faults::injected_failure(dst) {
        return Err(failure);
    }
    // Verified before the attributes are applied: a copy that is read-only
    // by then could not be opened for writing by the attempt that follows a
    // mismatch.
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
            )
            .restart_file());
        }
    }
    // The source's times and its read-only, hidden and system attributes in
    // one call on the handle already held, which also clears the Hidden
    // attribute of a partial file whose source is not hidden.
    // Every byte is written and verified by now; a target that refuses
    // timestamp or attribute updates (some SMB and FAT volumes) gets the same
    // treatment as directories: keep the file, log the loss.
    count(Op::Query, dst);
    count(Op::Set, dst);
    if let Err(error) =
        simplefile_core::file_ops::apply_basic_metadata_to_handle(&dst_file, &source_after)
    {
        log::warn!(
            "Could not preserve file metadata for {}: {error}",
            dst.display()
        );
    }
    ctx.progress.check_cancelled()?;
    Ok((copied, version, dst_file))
}

/// Gives the copy the source's alternate data streams: the mark Windows puts
/// on a downloaded file, which makes Windows ask before the file is run, and
/// whatever else programs keep beside a file's bytes.
///
/// A destination that cannot hold them (FAT, some shares) gets the file
/// without them, and the service log says so, where Explorer would ask. Any
/// other failure (a stream another program holds, a full disk) fails a move,
/// so that its source, which would be deleted with its streams, is kept; the
/// failure is retried. A copy keeps its source and only logs it.
#[cfg(windows)]
fn copy_alternate_streams(
    source: &fs::File,
    src: &Path,
    destination: &fs::File,
    dst: &Path,
    sync: bool,
    moving: bool,
) -> Result<(), Failure> {
    use simplefile_core::file_ops::StreamsNotCopied;
    count(Op::Query, src);
    match simplefile_core::file_ops::copy_alternate_streams(source, src, destination, dst, sync) {
        Ok(streams) => {
            for _ in 0..streams {
                // Each stream is opened on both sides, read to its end (the
                // last read finds it), emptied, written and, with the file,
                // forced to disk.
                count(Op::Open, src);
                count(Op::Read, src);
                count(Op::Read, src);
                count(Op::Open, dst);
                count(Op::Set, dst);
                count(Op::Write, dst);
                if sync {
                    count(Op::Flush, dst);
                }
            }
            Ok(())
        }
        Err(StreamsNotCopied::Unsupported) => {
            log::warn!(
                "{} was copied without its alternate data streams: the destination cannot hold them",
                src.display()
            );
            Ok(())
        }
        Err(StreamsNotCopied::Failed(error)) if moving => Err(Failure::new(
            format!(
                "Copy the alternate data streams of [{}]: {error}",
                src.display()
            ),
            true,
        )),
        Err(StreamsNotCopied::Failed(error)) => {
            log::warn!(
                "{} was copied without some of its alternate data streams: {error}",
                src.display()
            );
            Ok(())
        }
    }
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
                count(Op::Read, src);
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
                    count(Op::Write, dst);
                    if let Err(error) = dst_file.write_all(&buf[..read]) {
                        write_result =
                            Err(Failure::io("Write temporary destination", dst, error)
                                .restart_file());
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
    count(Op::Set, dst);
    preallocate_file(dst_file, file_len);
    count(Op::Set, dst);
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
                        count(Op::Read, src);
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
                        count(Op::Write, dst);
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
        Err(err) => return Err(discard_stripes(dst_file, dst, err)),
    };
    if sparse {
        count(Op::Set, dst);
        set_sparse(dst_file, false);
    }
    ctx.progress.finalizing(
        completed_bytes.saturating_add(copied),
        src.to_string_lossy().into(),
    );
    ctx.progress
        .check_cancelled()
        .map_err(|err| discard_stripes(dst_file, dst, Failure::new(err, false)))?;
    if copied != file_len {
        return Err(discard_stripes(
            dst_file,
            dst,
            Failure::new(
                format!(
                    "Multistream copy wrote {copied} of {file_len} bytes [{}]",
                    dst.display()
                ),
                false,
            ),
        ));
    }
    Ok(copied)
}

/// Empty a partial file whose stripes did not all arrive, and return the
/// failure to report.
///
/// The next try continues a partial file at its current length without
/// reading it again. A multistream partial has that length from its first
/// write (the last stripe starts near the end) and holes below it, so a try
/// that found it non-empty would write nothing and publish the holes. When
/// the file cannot be emptied (the disk error that stopped the stripes
/// usually stops this too) the failure is therefore not tried again: the
/// partial is deleted, and a new pass starts from a new file.
#[cfg(windows)]
fn discard_stripes(dst_file: &fs::File, dst: &Path, failure: Failure) -> Failure {
    match dst_file.set_len(0) {
        Ok(()) => failure,
        Err(_) if failure.is_cancelled() => failure,
        Err(error) => Failure::with_kind(
            format!(
                "{failure}; the partial file could not be emptied [{}]: {error}",
                dst.display()
            ),
            FailureKind::Permanent,
        ),
    }
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
    // A buffer cut to this file's length is of no use to any other file;
    // only the configured size is worth keeping for the next one.
    let mut buffer = if buffer_size < ctx.buffer_size() {
        PooledBuffer::exact(buffer_size)
    } else {
        PooledBuffer::acquire(buffer_size)
    };
    let mut copied = resume_from;
    let mut next_emit_at = ((resume_from / PROGRESS_BYTE_STEP) + 1) * PROGRESS_BYTE_STEP;
    while copied < file_len {
        ctx.progress.check_cancelled()?;
        let requested = buffer_size.min((file_len - copied) as usize);
        if let Some(limiter) = ctx.progress.rate_limit {
            limiter.wait(requested, ctx.progress.cancel)?;
        }
        count(Op::Read, src);
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
        count(Op::Write, dst);
        writer
            .write_all(&buffer[..read])
            .map_err(|e| Failure::io("Write temporary destination", dst, e).restart_file())?;
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
        .map_err(|e| Failure::io("Flush temporary destination", dst, e).restart_file())?;
    Ok(copied)
}

/// What happened to one file after its copy attempt(s).
#[derive(Debug)]
pub(crate) enum FileOutcome {
    Copied {
        reused: bool,
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
        false,
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
) -> Result<Option<(PathBuf, bool)>, Failure> {
    match conflict {
        ConflictAction::Replace => {
            // Replace swaps a file for a file. A directory occupying the name is
            // a type conflict the user must resolve; never delete it to make room.
            count(Op::Stat, dst);
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
        ConflictAction::Error | ConflictAction::CreateOnly => Ok(Some((dst.to_path_buf(), false))),
        ConflictAction::Skip => {
            if path_exists_no_follow(dst) {
                Ok(None)
            } else {
                Ok(Some((dst.to_path_buf(), false)))
            }
        }
        ConflictAction::KeepBoth => {
            if path_exists_no_follow(dst) {
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

/// Waits before the next attempt at a file, or returns the failure to report.
fn wait_to_retry(ctx: &CopyContext, retry: &mut Retry, error: Failure) -> Result<(), Failure> {
    let reason = error.to_string();
    let transport_retry = error.kind == FailureKind::Retryable;
    let delay = retry.failed_with(error)?;
    // A file held open elsewhere says nothing about the transport; only
    // transient I/O feeds the tuner.
    if transport_retry {
        if let Some(retries) = ctx.progress.retry_count {
            retries.fetch_add(1, Ordering::Relaxed);
        }
    }
    if let Some(limiter) = ctx.progress.rate_limit {
        limiter.retrying(delay, &reason);
    }
    crate::transfer_reliability::wait(delay, ctx.progress.cancel).map_err(|_| Failure::cancelled())
}

/// Opens the source and reads its size and times, trying again as an attempt
/// at copying it would. The first attempt copies through this handle.
fn open_source(
    src: &Path,
    ctx: &CopyContext,
    retry: &mut Retry,
) -> Result<(fs::File, fs::Metadata), Failure> {
    loop {
        if ctx.progress.check_cancelled().is_err() {
            return Err(Failure::cancelled());
        }
        let opened = open_source_sequential(src)
            .map_err(|e| Failure::io("Open source", src, e))
            .and_then(|file| {
                count(Op::Query, src);
                let meta = file
                    .metadata()
                    .map_err(|e| Failure::io("Inspect opened source", src, e))?;
                Ok((file, meta))
            });
        match opened {
            Ok(opened) => return Ok(opened),
            Err(error) if error.is_cancelled() => return Err(error),
            Err(error) => wait_to_retry(ctx, retry, error)?,
        }
    }
}

/// `in_new_folder` says that this transfer created the destination's folder,
/// so that nothing else can have the destination's name yet.
pub(crate) fn copy_file_with_progress_versioned(
    src: &Path,
    planned_dst: &Path,
    ctx: &CopyContext,
    completed_bytes: &mut u64,
    conflict: ConflictAction,
    in_new_folder: bool,
) -> Result<FileOutcome, Failure> {
    // Settled first: a skipped file costs its source nothing.
    let Some((dst, replace_existing)) = resolve_file_conflict(planned_dst, conflict)? else {
        return Ok(FileOutcome::Skipped);
    };
    let dst = dst.as_path();

    // In a folder this transfer created, no other item has the name. The
    // rename that gives the file its name refuses one that appeared since
    // all the same. Writing directly has no such rename, so it asks.
    let exists = if in_new_folder
        && !ctx.policy.inplace
        && matches!(conflict, ConflictAction::Error | ConflictAction::CreateOnly)
    {
        false
    } else {
        path_exists_no_follow(dst)
    };
    // Writing directly is for new files. A file that replaces an existing one
    // is always copied under a temporary name first: written directly, the
    // existing file would be emptied before the first byte was copied, and a
    // failed or cancelled copy would leave neither the old file nor the new.
    let use_inplace = ctx.policy.inplace && !exists;
    if exists && is_real_directory(dst) {
        return Err(Failure::conflict(format!(
            "destination exists with a different type: {}",
            dst.display()
        )));
    }
    if exists && !replace_existing {
        return Err(Failure::conflict(conflict_for_existing_destination(dst)));
    }
    let staged_path = if use_inplace {
        dst.to_path_buf()
    } else {
        staging_path_for(dst, ctx.operation_id)?
    };
    // The length and times the copy must reproduce come from the handle the
    // first attempt reads through, not from asking the path beforehand.
    let mut retry = Retry::from_policy(&ctx.policy);
    let (source_file, source_metadata) = open_source(src, ctx, &mut retry)?;
    let file_len = source_metadata.len();
    let source_modified = source_metadata.modified().ok();
    let known = ctx.progress.total_bytes.load(Ordering::Relaxed);
    let needed = (*completed_bytes).saturating_add(file_len);
    if needed > known {
        ctx.progress.total_bytes.store(needed, Ordering::Relaxed);
    }
    let mut opened_source = Some((source_file, source_metadata));

    // A move deletes this file's source once the file has its final name. Its
    // record line says so, with the length of the complete file, so recovery
    // never deletes what may by then be the only copy; and its bytes are
    // flushed before it gets that name, whatever the setting says.
    let moving = ctx.progress.operation_type == "move";
    // A file written under a temporary name is a line in its folder's
    // record. One written straight to its final name keeps a record of its
    // own, which carries the mark that says the file was completed.
    let mut lease = Some(if use_inplace {
        crate::transfer_journal::StagingLease::begin_for_move(
            &src.to_string_lossy(),
            dst,
            &staged_path,
            moving.then_some(file_len),
        )?
    } else {
        ctx.records.begin(
            &src.to_string_lossy(),
            dst,
            &staged_path,
            moving.then_some(file_len),
        )?
    });
    let must_sync = ctx.policy.flush_on_close || moving;
    // Set by a failure that leaves the partial file's bytes in doubt, and
    // kept for the rest of this file's attempts: each of them writes the
    // file again from its first byte. An attempt can fail before it has
    // emptied the partial file, so a later failure on the source's side
    // does not make the bytes trusted again.
    let mut distrusted = false;
    let copy_result = loop {
        if ctx.progress.check_cancelled().is_err() {
            break Err(Failure::cancelled());
        }
        if retry.failures > 0 && {
            count(Op::Stat, src);
            fs::metadata(src)
                .is_ok_and(|meta| meta.len() != file_len || meta.modified().ok() != source_modified)
        } {
            break Err(Failure::from(format!(
                "Source changed before retry [{}]",
                src.display()
            )));
        }
        let staged_file = lease.as_mut().and_then(|owner| owner.take_staged_file());
        // The lease created the staging file exclusively, so any bytes already
        // in it came from this operation's previous attempt. They are
        // continued when that attempt stopped on the source's side, and
        // written again when it stopped on the destination's.
        let partial = if distrusted {
            Partial::Discard
        } else if retry.failures > 0 {
            Partial::Continue
        } else {
            Partial::Compare
        };
        match copy_file_attempt_versioned(
            src,
            &staged_path,
            staged_file,
            opened_source.take(),
            partial,
            ctx,
            *completed_bytes,
            ctx.buffer_size(),
            file_len,
            must_sync,
        ) {
            Ok(copied) => break Ok(copied),
            Err(error) if error.is_cancelled() => break Err(error),
            Err(error) => {
                distrusted |= error.restart;
                if let Err(error) = wait_to_retry(ctx, &mut retry, error) {
                    break Err(error);
                }
            }
        }
    };

    match copy_result {
        Ok((written, version, written_file)) => {
            // Kept open until the file has its name: it is renamed through
            // this handle. Closed before anything removes the partial file.
            let mut written_file = Some(written_file);
            ctx.progress.finalizing(
                completed_bytes.saturating_add(written),
                dst.to_string_lossy().to_string(),
            );
            if ctx.progress.check_cancelled().is_err() {
                drop(written_file.take());
                let cleanup = lease.take().map_or(Ok(()), |owner| owner.abort());
                return Err(Failure::cancelled().after_cleanup(cleanup));
            }
            if !use_inplace {
                if let Err(error) = promote_leased_with_policy(
                    &staged_path,
                    dst,
                    replace_existing,
                    ctx.progress.cancel,
                    &ctx.policy,
                    lease.as_mut().expect("staged copy has an ownership lease"),
                    written_file.as_ref(),
                    |delay, reason| {
                        if let Some(limiter) = ctx.progress.rate_limit {
                            limiter.retrying(delay, reason);
                        }
                    },
                ) {
                    drop(written_file.take());
                    // The clean-up's own failure used to be dropped here, so
                    // the report did not say a partial file was left behind.
                    let cleanup = lease.take().map_or(Ok(()), |owner| owner.abort());
                    let failure = error;
                    return Err(if failure.is_conflict() || failure.is_cancelled() {
                        failure
                    } else {
                        Failure::with_kind(
                            format!("Finalize destination [{}]: {}", dst.display(), failure),
                            failure.kind,
                        )
                    }
                    .after_cleanup(cleanup));
                }
            }
            drop(written_file);
            if let Some(owner) = lease.take() {
                // `finish` fails only for a file written under its final name
                // whose record could be neither removed nor marked complete.
                // Recovery would delete such a file, so it is not reported as
                // copied, and a move keeps its source.
                owner.finish().map_err(|error| {
                    Failure::from(format!("Finish destination [{}]: {error}", dst.display()))
                })?;
            }
            *completed_bytes += written;
            ctx.progress
                .complete_file(*completed_bytes, src.to_string_lossy().to_string());
            Ok(FileOutcome::Copied {
                reused: false,
                version,
                destination: dst.to_path_buf(),
            })
        }
        Err(error) => {
            count(Op::Stat, &staged_path);
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
    let Some((dst_path, replace_existing)) = resolve_file_conflict(planned_dst, conflict)? else {
        return Ok(FileOutcome::Skipped);
    };
    let dst_path = dst_path.as_path();
    if !replace_existing && path_exists_no_follow(dst_path) {
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
    let mut lease = match crate::transfer_journal::StagingLease::record_created(
        &src_path.to_string_lossy(),
        dst_path,
        &staged_path,
    ) {
        Ok(lease) => lease,
        Err(error) => {
            // Without a record nothing would ever claim the link just
            // created: recovery acts on recorded files only.
            let _ = cleanup_owned_path(&staged_path);
            return Err(Failure::from(error));
        }
    };
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
        replace_existing,
        ctx.progress.cancel,
        &ctx.policy,
        &mut lease,
        None,
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
    // A link's record names a partial name that is gone by now: `finish`
    // cannot fail for it.
    if let Err(error) = lease.finish() {
        log::warn!("{error}");
    }
    ctx.progress
        .complete_file(completed_bytes, src_path.to_string_lossy().to_string());
    Ok(FileOutcome::Copied {
        reused: false,
        version,
        destination: dst_path.to_path_buf(),
    })
}

/// Whether a rename was refused because the two paths are on different
/// volumes (`ERROR_NOT_SAME_DEVICE`, `EXDEV`).
fn crosses_volumes(error: &std::io::Error) -> bool {
    error.kind() == std::io::ErrorKind::CrossesDevices
        || (cfg!(windows) && error.raw_os_error() == Some(17))
}

/// Same-volume moves rename instead of copying. `None` means "not applicable,
/// copy instead" (different volume, an unpublished tree, or a rename error that
/// a copy may still get around). A source held by another program is not such
/// an error: it is reported as busy.
pub(crate) fn move_by_rename(
    source: &Path,
    planned_dst: &Path,
    conflict: ConflictAction,
    ctx: &CopyContext,
) -> Option<Result<FileOutcome, Failure>> {
    use simplefile_core::path_conflict::{is_already_exists_error, rename_exclusive};

    if !super::plan::same_volume(source, planned_dst) {
        return None;
    }
    let (dst, replace_existing) = match resolve_file_conflict(planned_dst, conflict) {
        Ok(Some(resolved)) => resolved,
        Ok(None) => return Some(Ok(FileOutcome::Skipped)),
        Err(error) => return Some(Err(error)),
    };
    if ctx.progress.check_cancelled().is_err() {
        return Some(Err(Failure::cancelled()));
    }
    match rename_exclusive(source, &dst) {
        Ok(()) => Some(Ok(renamed_outcome(&dst))),
        // Another volume after all: a folder where one is mounted, a letter
        // remapped since it was looked up. The item is copied. A name that
        // exists took this refusal into the Replace swap below, refused the
        // same way, and the item failed.
        Err(error) if crosses_volumes(&error) => None,
        Err(error) if is_already_exists_error(&error) || path_exists_no_follow(&dst) => {
            if !replace_existing {
                return Some(Err(Failure::conflict(conflict_for_existing_destination(
                    &dst,
                ))));
            }
            count(Op::Stat, &dst);
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
                    Ok(Swapped::Yes) => return Some(Ok(renamed_outcome(&dst))),
                    // The existing file is back under its name; the item is
                    // copied over it instead.
                    Ok(Swapped::OtherVolume) => return None,
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
        Err(error) => {
            // Another program holds the source and lets nobody rename or
            // delete it. Copying instead cannot finish this move, because
            // the source could not be deleted afterwards either: it would
            // read and write the whole file and leave it in both places.
            // The file is reported as busy and gets another round later,
            // like any other busy file.
            let failure = Failure::io("Move source", source, error);
            (failure.kind == FailureKind::Busy).then_some(Err(failure))
        }
    }
}

/// Rename the existing destination aside, rename the source into place, then
/// remove the backup. Any failure leaves the original destination in place.
///
/// Between the two renames the destination's name is free and its file lies
/// under a hidden backup name. The swap is therefore recorded first, on disk,
/// so that recovery puts the original back after a stop at that point, and
/// removes the backup after a stop one step later. The record is removed when
/// the swap is over, and left for recovery when it ends with a file still to
/// put back or a backup still to remove.
/// What a swap through a backup name did, when it failed nothing.
enum Swapped {
    Yes,
    /// The source is on another volume after all: the existing file was put
    /// back under its name, and nothing else changed.
    OtherVolume,
}

fn swap_source_into_place(source: &Path, dst: &Path) -> Result<Swapped, Failure> {
    use crate::transfer_journal::SwapRecord;
    use simplefile_core::path_conflict::rename_exclusive;

    let backup = crate::transfer_staging::unique_backup_path(dst).map_err(Failure::from)?;
    // A record that cannot be written does not stop the swap: the renames
    // need no free space, and a full drive is when they are wanted most.
    let record = match SwapRecord::begin(source, dst, &backup) {
        Ok(record) => Some(record),
        Err(error) => {
            log::warn!(
                "Replacing [{}] without a recovery record: {error}",
                dst.display()
            );
            None
        }
    };
    let over = |record: Option<SwapRecord>| {
        if let Some(record) = record {
            record.finish();
        }
    };
    if let Err(error) = rename_exclusive(dst, &backup) {
        over(record);
        return Err(Failure::io("Prepare destination replacement", dst, error));
    }
    // The caller saw a file under this name a moment ago. A folder that has
    // taken it since is somebody's data: it goes back under its name and is
    // reported, never deleted as the backup of a replaced file.
    if is_real_directory(&backup) {
        let restored = rename_exclusive(&backup, dst);
        // The record is about a file. It can do nothing for a folder.
        over(record);
        return Err(match restored {
            Ok(()) => Failure::conflict(format!(
                "destination exists with a different type: {}",
                dst.display()
            )),
            Err(restore) => Failure::from(format!(
                "A folder took the name [{}] and was moved to [{}]; moving it back failed: {restore}",
                dst.display(),
                backup.display()
            )),
        });
    }
    if let Err(error) = rename_exclusive(source, dst) {
        return match rename_exclusive(&backup, dst) {
            Ok(()) if crosses_volumes(&error) => {
                over(record);
                Ok(Swapped::OtherVolume)
            }
            Ok(()) => Err({
                over(record);
                Failure::io(
                    "Finalize destination (original destination was restored)",
                    dst,
                    error,
                )
            }),
            // The record stays: recovery puts the original back.
            Err(restore) => Err(Failure::from(format!(
                "Finalize destination [{}]: {error}; original remains [{}], restore failed: {restore}",
                dst.display(),
                backup.display()
            ))),
        };
    }
    // Promotion already committed: never replay it just because backup cleanup failed.
    match crate::transfer_staging::remove_file_or_link(&backup, "destination backup") {
        Ok(()) => {
            over(record);
            Ok(Swapped::Yes)
        }
        // The record stays: recovery removes the backup.
        Err(error) => Err(Failure::from(format!(
            "Transferred, but backup remains [{}]: {error}",
            backup.display()
        ))),
    }
}

/// The file is at `dst` and its source name is gone: the move is complete.
///
/// Nothing is read back from the renamed file. Its version has no reader (no
/// source is left to verify or delete), opening it to capture one cost several
/// requests for every file of a same-drive move, and a file that could be
/// renamed but not opened for reading (held by another program, or not
/// readable by this user) was reported as failed after it had been moved.
fn renamed_outcome(dst: &Path) -> FileOutcome {
    FileOutcome::Copied {
        reused: false,
        version: SourceVersion::renamed(),
        destination: dst.to_path_buf(),
    }
}
