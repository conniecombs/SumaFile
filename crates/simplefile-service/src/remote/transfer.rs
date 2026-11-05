use super::path::{join_remote_path, normalize_remote_path, parent_remote_path};
use super::provider::RemoteProvider;
use crate::progress::{ActiveFile, Policy, RateLimit, TuningUpdate};
use crate::transfer_reliability::{Failure, Retry};
use crate::transfer_staging::{
    capture_source_version_from_open_file, verify_source_unchanged, ContentHasher,
};
use serde::{Deserialize, Serialize};
use simplefile_core::models::{FileEntry, ProgressUpdate};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

pub const TRANSFER_BUFFER_SIZE: usize = 128 * 1024;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteTransferRequest {
    pub remote_session_id: String,
    pub direction: String,
    pub sources: Vec<String>,
    pub destination: String,
    pub conflict_action: String,
    pub operation_id: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct RemoteTransferItem {
    pub source: String,
    pub destination: String,
    pub is_dir: bool,
    pub status: String,
    pub bytes: u64,
    pub error: Option<String>,
}

#[derive(Debug, Default, Serialize)]
pub struct RemoteTransferOutcome {
    pub items: Vec<RemoteTransferItem>,
    pub retry_sources: Vec<String>,
    pub cancelled: bool,
}

pub fn copy_stream(source: &mut dyn Read, destination: &mut dyn Write) -> Result<u64, Failure> {
    let mut buffer = vec![0; TRANSFER_BUFFER_SIZE];
    let mut count = 0;
    loop {
        // Gives a download destination a chance to apply backpressure before the next network read.
        destination
            .write(&[])
            .map_err(|e| Failure::io("Prepare destination", "stream", e))?;
        let read = source
            .read(&mut buffer)
            .map_err(|e| Failure::io("Read source", "stream", e))?;
        if read == 0 {
            break;
        }
        destination
            .write_all(&buffer[..read])
            .map_err(|e| Failure::io("Write destination", "stream", e))?;
        count += read as u64;
    }
    destination
        .flush()
        .map_err(|e| Failure::io("Flush destination", "stream", e))?;
    Ok(count)
}

fn check_cancel(cancel: &AtomicBool) -> Result<(), String> {
    if cancel.load(Ordering::Relaxed) {
        Err("Transfer cancelled".into())
    } else {
        Ok(())
    }
}

struct Progress<'a> {
    request: &'a RemoteTransferRequest,
    cancel: &'a AtomicBool,
    emit: &'a dyn Fn(ProgressUpdate),
    bytes: u64,
    files: u64,
    current: String,
    last: Instant,
    tuning: &'a dyn Fn(TuningUpdate),
    policy: Policy,
    limiter: RateLimit,
    policy_checked: Instant,
    read_policy: &'a dyn Fn() -> Policy,
    file_start: u64,
}

impl Progress<'_> {
    fn report(&mut self, bytes: u64) -> std::io::Result<()> {
        check_cancel(self.cancel).map_err(std::io::Error::other)?;
        if self.policy_checked.elapsed() >= Duration::from_secs(5) {
            self.policy = (self.read_policy)();
            self.limiter.update(self.policy.bandwidth_mib);
            self.policy_checked = Instant::now();
        }
        self.bytes += bytes;
        if self.last.elapsed() >= Duration::from_millis(100) {
            self.send("running", None);
        }
        Ok(())
    }

    fn send(&mut self, status: &str, error: Option<String>) {
        self.last = Instant::now();
        (self.emit)(ProgressUpdate {
            operation_id: self.request.operation_id.clone(),
            operation_type: format!("remote_{}", self.request.direction),
            current: self.bytes,
            total: 0,
            current_files: self.files,
            total_files: 0,
            current_item: self.current.clone(),
            status: status.into(),
            error,
        });
        (self.tuning)(TuningUpdate {
            operation_id: self.request.operation_id.clone(),
            mode: self.policy.mode,
            concurrency: 1,
            explanation: self.limiter.explanation().unwrap_or_else(|| {
                "One remote file stream; configured bandwidth limit applies".into()
            }),
            active_files: if status == "running" && self.current != "Preparing transfer" {
                vec![ActiveFile {
                    path: self.current.clone(),
                    current: self.bytes.saturating_sub(self.file_start),
                    total: 0,
                }]
            } else {
                Vec::new()
            },
        });
    }
}

struct ObservedReader<'a, 'b> {
    source: File,
    progress: &'a mut Progress<'b>,
    bytes_read: u64,
    hasher: ContentHasher,
}
impl Read for ObservedReader<'_, '_> {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        self.progress.report(0)?;
        let read = self.source.read(buffer).map_err(|e| {
            std::io::Error::other(Failure::io("Read local source", &self.progress.current, e))
        })?;
        self.bytes_read = self.bytes_read.saturating_add(read as u64);
        self.hasher.update(&buffer[..read]);
        // Charge actual bytes, not a full buffer for every small file or EOF.
        // At most one provider buffer is held while waiting for the user limit.
        self.progress
            .limiter
            .wait(read, self.progress.cancel)
            .map_err(std::io::Error::other)?;
        self.progress.report(read as u64)?;
        Ok(read)
    }
}

struct ObservedWriter<'a, 'b> {
    destination: File,
    progress: &'a mut Progress<'b>,
    path: String,
}
impl Write for ObservedWriter<'_, '_> {
    fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
        self.progress.report(0)?;
        self.progress
            .limiter
            .wait(buffer.len(), self.progress.cancel)
            .map_err(std::io::Error::other)?;
        match self.destination.write(buffer) {
            Ok(written) => {
                self.progress.limiter.refund(buffer.len() - written);
                self.progress.report(written as u64)?;
                Ok(written)
            }
            Err(error) => {
                self.progress.limiter.refund(buffer.len());
                Err(std::io::Error::other(Failure::io(
                    "Write local temporary destination",
                    &self.path,
                    error,
                )))
            }
        }
    }
    fn flush(&mut self) -> std::io::Result<()> {
        self.destination.flush().map_err(|e| {
            std::io::Error::other(Failure::io(
                "Flush local temporary destination",
                &self.path,
                e,
            ))
        })
    }
}

fn retry_stream(
    provider: &mut dyn RemoteProvider,
    progress: &mut Progress<'_>,
    source: &str,
    target: &str,
    mut transfer: impl FnMut(&mut dyn RemoteProvider, &mut Progress<'_>) -> Result<u64, Failure>,
) -> Result<u64, String> {
    let mut retry = Retry::default();
    loop {
        check_cancel(progress.cancel)?;
        progress.limiter.wait(0, progress.cancel)?;
        progress.bytes = progress.file_start;
        match transfer(provider, progress) {
            Ok(count) => {
                check_cancel(progress.cancel)?;
                return Ok(count);
            }
            Err(error) => {
                if progress.cancel.load(Ordering::Relaxed) {
                    return Err("Transfer cancelled".into());
                }
                let error = error
                    .context("Transfer source", source)
                    .context("Transfer destination", target);
                let mut reason = error.to_string();
                let mut delay = retry.failed(error)?;
                loop {
                    progress.limiter.retrying(delay, &reason);
                    progress.send("running", None);
                    crate::transfer_reliability::wait(delay, progress.cancel)?;
                    match provider.reconnect() {
                        Ok(()) => break,
                        Err(reconnect) => {
                            if progress.cancel.load(Ordering::Relaxed) {
                                return Err("Transfer cancelled".into());
                            }
                            let reconnect = reconnect
                                .context("Reconnect for source", source)
                                .context("Transfer destination", target);
                            reason = reconnect.to_string();
                            delay = retry.failed(reconnect)?;
                        }
                    }
                }
            }
        }
    }
}

#[derive(Debug, Clone, Copy)]
struct RemoteFinalizationState {
    staging_exists: bool,
    target_exists: bool,
    backup_exists: bool,
}

fn inspect_remote_finalization_state(
    provider: &mut dyn RemoteProvider,
    parent: &str,
    staging: &str,
    target: &str,
    backup: Option<&str>,
) -> Result<RemoteFinalizationState, String> {
    let staging_name = staging.rsplit('/').next().unwrap_or("");
    let target_name = target.rsplit('/').next().unwrap_or("");
    let backup_name = backup.and_then(|b| b.rsplit('/').next());
    let listing = provider.list_directory(parent)?;
    let mut state = RemoteFinalizationState {
        staging_exists: false,
        target_exists: false,
        backup_exists: false,
    };
    for entry in listing.entries {
        if entry.name == staging_name {
            state.staging_exists = true;
        } else if entry.name == target_name {
            state.target_exists = true;
        } else if backup_name.is_some_and(|name| entry.name == name) {
            state.backup_exists = true;
        }
    }
    Ok(state)
}

fn abort_remote_upload_finalization(
    provider: &mut dyn RemoteProvider,
    error: String,
    parent: &str,
    staging: &str,
    target: &str,
    backup: Option<&str>,
    need_restore_fallback: bool,
) -> String {
    let target_name = target.rsplit('/').next().unwrap_or("");
    let state = inspect_remote_finalization_state(provider, parent, staging, target, backup)
        .or_else(|_| {
            let _ = provider.reconnect();
            inspect_remote_finalization_state(provider, parent, staging, target, backup)
        });

    match state {
        Ok(state) => {
            let mut combined = error;
            if let Some(backup_path) = backup {
                if state.backup_exists && !state.target_exists {
                    if let Err(restore_err) = provider.rename_entry(backup_path, target_name) {
                        combined =
                            format!("{combined}; original remains at {backup_path}: {restore_err}");
                    }
                }
            }
            if state.staging_exists {
                cleanup_error(
                    combined,
                    provider.delete_entries(&[staging.to_string()]).map(|_| ()),
                    staging,
                )
            } else {
                combined
            }
        }
        Err(_) => {
            let mut combined = error;
            if need_restore_fallback {
                if let Some(backup_path) = backup {
                    if let Err(restore_err) = provider.rename_entry(backup_path, target_name) {
                        combined =
                            format!("{combined}; original remains at {backup_path}: {restore_err}");
                    }
                }
            }
            cleanup_error(
                combined,
                provider.delete_entries(&[staging.to_string()]).map(|_| ()),
                staging,
            )
        }
    }
}

fn finalize_remote_upload(
    provider: &mut dyn RemoteProvider,
    progress: &mut Progress<'_>,
    parent: &str,
    staging: &str,
    target: &str,
    backup: Option<&str>,
) -> Result<(), String> {
    let mut retry = Retry::default();
    let mut backed_up = backup.is_none();
    let mut promoted = false;
    let target_name = target.rsplit('/').next().unwrap_or("");

    loop {
        if let Err(cancel_err) = check_cancel(progress.cancel) {
            return Err(abort_remote_upload_finalization(
                provider,
                cancel_err,
                parent,
                staging,
                target,
                backup,
                backed_up && backup.is_some(),
            ));
        }

        let step_result: Result<(), String> = (|| {
            if !backed_up {
                if let Some(backup_path) = backup {
                    let backup_name = backup_path.rsplit('/').next().unwrap_or("");
                    provider.rename_entry(target, backup_name)?;
                }
                backed_up = true;
            }
            if !promoted {
                provider.rename_entry(staging, target_name)?;
                promoted = true;
            }
            Ok(())
        })();

        match step_result {
            Ok(()) => break,
            Err(step_error) => {
                if progress.cancel.load(Ordering::Relaxed) {
                    return Err(abort_remote_upload_finalization(
                        provider,
                        "Transfer cancelled".into(),
                        parent,
                        staging,
                        target,
                        backup,
                        backed_up && backup.is_some(),
                    ));
                }

                let failure = Failure::new(
                    format!("Finalize remote upload [{target}]: {step_error}"),
                    true,
                );
                let mut reason = failure.to_string();
                let mut delay = match retry.failed(failure) {
                    Ok(delay) => delay,
                    Err(_) => {
                        return Err(abort_remote_upload_finalization(
                            provider,
                            step_error,
                            parent,
                            staging,
                            target,
                            backup,
                            backed_up && backup.is_some(),
                        ));
                    }
                };

                loop {
                    progress.limiter.retrying(delay, &reason);
                    progress.send("running", None);
                    if let Err(wait_err) = crate::transfer_reliability::wait(delay, progress.cancel)
                    {
                        return Err(abort_remote_upload_finalization(
                            provider,
                            wait_err,
                            parent,
                            staging,
                            target,
                            backup,
                            backed_up && backup.is_some(),
                        ));
                    }
                    match provider.reconnect() {
                        Ok(()) => break,
                        Err(reconnect_err) => {
                            if progress.cancel.load(Ordering::Relaxed) {
                                return Err(abort_remote_upload_finalization(
                                    provider,
                                    "Transfer cancelled".into(),
                                    parent,
                                    staging,
                                    target,
                                    backup,
                                    backed_up && backup.is_some(),
                                ));
                            }
                            let reconnect_err =
                                reconnect_err.context("Reconnect for upload finalization", target);
                            reason = reconnect_err.to_string();
                            delay = match retry.failed(reconnect_err) {
                                Ok(next_delay) => next_delay,
                                Err(_) => {
                                    return Err(abort_remote_upload_finalization(
                                        provider,
                                        step_error,
                                        parent,
                                        staging,
                                        target,
                                        backup,
                                        backed_up && backup.is_some(),
                                    ));
                                }
                            };
                        }
                    }
                }

                match inspect_remote_finalization_state(provider, parent, staging, target, backup) {
                    Ok(state) => {
                        if !state.staging_exists && state.target_exists {
                            break;
                        }
                        if state.staging_exists {
                            if backup.is_some() {
                                if state.backup_exists && !state.target_exists {
                                    backed_up = true;
                                } else if state.target_exists && !state.backup_exists {
                                    backed_up = false;
                                }
                            }
                            continue;
                        }
                        return Err(abort_remote_upload_finalization(
                            provider,
                            step_error,
                            parent,
                            staging,
                            target,
                            backup,
                            backed_up && backup.is_some(),
                        ));
                    }
                    Err(inspect_err) => {
                        return Err(abort_remote_upload_finalization(
                            provider,
                            format!("{step_error}; inspect failed: {inspect_err}"),
                            parent,
                            staging,
                            target,
                            backup,
                            backed_up && backup.is_some(),
                        ));
                    }
                }
            }
        }
    }

    if let Some(backup_path) = backup {
        if let Err(first_err) = provider.delete_entries(&[backup_path.to_string()]) {
            let _ = provider.reconnect();
            let still_exists =
                inspect_remote_finalization_state(provider, parent, staging, target, backup)
                    .map(|s| s.backup_exists)
                    .unwrap_or(true);
            if still_exists {
                provider
                    .delete_entries(&[backup_path.to_string()])
                    .map_err(|_| {
                        format!(
                            "Transferred, but original backup remains at {backup_path}: {first_err}"
                        )
                    })?;
            }
        }
    }

    Ok(())
}

pub fn validate_request(request: &RemoteTransferRequest) -> Result<(), String> {
    if !matches!(request.direction.as_str(), "upload" | "download") {
        return Err("Remote transfer direction must be upload or download".into());
    }
    if !matches!(
        request.conflict_action.as_str(),
        "error" | "skip" | "replace" | "keep-both"
    ) {
        return Err("Unknown remote transfer conflict policy".into());
    }
    if request.operation_id.trim().is_empty()
        || request.sources.is_empty()
        || request.destination.trim().is_empty()
    {
        return Err("Remote transfer requires an operation ID, sources, and destination".into());
    }
    Ok(())
}

// Remote names become Windows path components on download. Reject names that
// could escape the chosen directory, alias another file, or address a device/ADS.
fn validate_name(name: &str) -> Result<(), String> {
    let stem = name.split('.').next().unwrap_or("").to_ascii_uppercase();
    let reserved = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || ((stem.starts_with("COM") || stem.starts_with("LPT"))
            && stem.len() == 4
            && matches!(stem.as_bytes()[3], b'1'..=b'9'));
    if name.is_empty()
        || name == "."
        || name == ".."
        || name.ends_with(['.', ' '])
        || name
            .chars()
            .any(|c| c.is_control() || "<>:\"/\\|?*".contains(c))
        || reserved
    {
        return Err(format!(
            "Cannot transfer unsafe or unsupported file name: {name}"
        ));
    }
    Ok(())
}

fn remote_entry(
    provider: &mut dyn RemoteProvider,
    path: &str,
) -> Result<Option<FileEntry>, String> {
    let path = normalize_remote_path(path);
    let parent =
        parent_remote_path(&path).ok_or("A server root cannot be transferred as a file")?;
    let name = path.rsplit('/').next().unwrap_or("");
    Ok(provider
        .list_directory(&parent)?
        .entries
        .into_iter()
        .find(|entry| entry.name == name))
}

fn local_exists(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok()
}

fn unique_remote_sibling(
    provider: &mut dyn RemoteProvider,
    path: &str,
    suffix: &str,
) -> Result<String, String> {
    let parent = parent_remote_path(path).ok_or("Remote destination has no parent")?;
    let name = path.rsplit('/').next().unwrap_or("");
    // One snapshot, rather than one network listing per candidate.
    let names: std::collections::HashSet<_> = provider
        .list_directory(&parent)?
        .entries
        .into_iter()
        .map(|e| e.name)
        .collect();
    for index in 1..10000 {
        let candidate = format!("{name}.{suffix}-{index}");
        if !names.contains(&candidate) {
            return Ok(join_remote_path(&parent, &candidate));
        }
    }
    Err("Could not allocate a unique remote destination".into())
}

fn cleanup_error(original: String, cleanup: Result<(), String>, temporary: &str) -> String {
    match cleanup {
        Ok(()) => original,
        Err(error) => format!("{original}; temporary file remains at {temporary}: {error}"),
    }
}

fn transfer_file(
    provider: &mut dyn RemoteProvider,
    source: &str,
    destination: &str,
    source_entry: Option<&FileEntry>,
    progress: &mut Progress<'_>,
) -> Result<(String, u64, bool), String> {
    check_cancel(progress.cancel)?;
    progress.current = source.to_string();
    let _permit =
        crate::progress::TransferPermit::acquire(TRANSFER_BUFFER_SIZE * 3, progress.cancel)?;
    progress.file_start = progress.bytes;
    progress.send("running", None);
    let upload = progress.request.direction == "upload";
    let policy = progress.request.conflict_action.as_str();
    let mut target = destination.to_string();
    let exists = if upload {
        if let Some(entry) = remote_entry(provider, &target)? {
            if entry.is_dir || entry.is_symlink {
                return Err("Destination is a directory or symbolic link".into());
            }
            true
        } else {
            false
        }
    } else {
        match fs::symlink_metadata(&target) {
            Ok(meta) => {
                if !meta.is_file() || meta.file_type().is_symlink() {
                    return Err("Destination is not a regular file".into());
                }
                true
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => false,
            Err(e) => return Err(e.to_string()),
        }
    };
    if exists {
        match policy {
            "skip" => return Ok((target, 0, true)),
            "error" => return Err(format!("Destination already exists: {target}")),
            "keep-both" => {
                if upload {
                    target = unique_remote_sibling(provider, &target, "copy")?;
                } else {
                    let original = target.clone();
                    target = (1..10000)
                        .map(|n| format!("{original}.copy-{n}"))
                        .find(|candidate| !local_exists(Path::new(candidate)))
                        .ok_or("Could not allocate destination")?;
                }
            }
            _ => {}
        }
    }
    let replace = exists && policy == "replace";
    if upload {
        // Distinct sessions must never reuse one another's staging or backup files.
        let nonce = format!(
            "{}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos(),
            crate::progress::generate_transfer_operation_id()
        );
        let parent = parent_remote_path(&target).ok_or("Remote destination has no parent")?;
        let staging = unique_remote_sibling(
            provider,
            &join_remote_path(&parent, ".sumafile-partial"),
            &nonce,
        )?;
        // Allocate before uploading so a failed listing cannot strand a completed temporary file.
        let backup = if replace {
            Some(unique_remote_sibling(
                provider,
                &join_remote_path(&parent, ".sumafile-backup"),
                &nonce,
            )?)
        } else {
            None
        };
        let transferred = retry_stream(
            provider,
            progress,
            source,
            &target,
            |provider, progress| {
                let source_file =
                    File::open(source).map_err(|e| Failure::io("Open local source", source, e))?;
                let initial_meta = source_file
                    .metadata()
                    .map_err(|e| Failure::io("Inspect local source", source, e))?;
                let expected_len = initial_meta.len();
                let mut reader = ObservedReader {
                    source: source_file,
                    progress,
                    bytes_read: 0,
                    hasher: ContentHasher::new(),
                };
                let count = provider.upload(&staging, &mut reader)?;
                if count != expected_len || reader.bytes_read != expected_len {
                    return Err(Failure::new(
                        format!(
                            "Local source size changed during upload [{source}]: expected {expected_len} bytes, uploaded {count}"
                        ),
                        false,
                    ));
                }
                let expected_version = capture_source_version_from_open_file(
                    &reader.source,
                    &initial_meta,
                    Some(reader.hasher.finish()),
                );
                drop(reader);
                verify_source_unchanged(Path::new(source), &expected_version).map_err(|err| {
                    Failure::new(
                        format!("Local source changed during upload [{source}]: {err}"),
                        false,
                    )
                })?;
                Ok(count)
            },
        );
        let count = match transferred {
            Ok(count) => count,
            Err(error) => {
                return Err(cleanup_error(
                    error,
                    provider
                        .delete_entries(std::slice::from_ref(&staging))
                        .map(|_| ()),
                    &staging,
                ))
            }
        };
        finalize_remote_upload(
            provider,
            progress,
            &parent,
            &staging,
            &target,
            backup.as_deref(),
        )?;
        Ok((target, count, false))
    } else {
        let expected_entry = match source_entry {
            Some(entry) => entry.clone(),
            None => remote_entry(provider, source)?
                .ok_or_else(|| format!("Remote source does not exist: {source}"))?,
        };
        let expected_size = expected_entry.size;
        let expected_modified = expected_entry.modified.clone();

        let staging = crate::transfer_staging::staging_path_for(
            Path::new(&target),
            &progress.request.operation_id,
        )?;
        let mut created = false;
        let result = retry_stream(provider, progress, source, &target, |provider, progress| {
            let file = if created {
                OpenOptions::new().write(true).truncate(true).open(&staging)
            } else {
                OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&staging)
            }
            .map_err(|e| Failure::io("Open local temporary destination", &staging, e))?;
            created = true;
            let mut writer = ObservedWriter {
                destination: file,
                progress,

                path: staging.to_string_lossy().into_owned(),
            };
            let count = provider.download(source, &mut writer)?;
            if count != expected_size {
                return Err(Failure::new(
                    format!(
                        "Remote source size mismatch for {source}: expected {expected_size} bytes, received {count}"
                    ),
                    false,
                ));
            }
            writer
                .destination
                .sync_all()
                .map_err(|e| Failure::io("Finalize local temporary destination", &staging, e))?;
            Ok(count)
        });
        let result = result.and_then(|count| {
            if let Ok(Some(after_entry)) = remote_entry(provider, source) {
                if after_entry.size != expected_size
                    || (!expected_modified.is_empty()
                        && !after_entry.modified.is_empty()
                        && after_entry.modified != expected_modified)
                {
                    return Err(format!("Remote source changed during download: {source}"));
                }
            }
            crate::transfer_staging::promote_with_recovery(
                &staging,
                Path::new(&target),
                replace,
                progress.cancel,
                |delay, reason| {
                    progress.limiter.retrying(delay, reason);
                    progress.send("running", None);
                },
            )?;
            Ok((target, count, false))
        });
        if let Err(error) = result {
            return Err(if created && local_exists(&staging) {
                cleanup_error(
                    error,
                    fs::remove_file(&staging).map_err(|e| e.to_string()),
                    &staging.to_string_lossy(),
                )
            } else {
                error
            });
        }
        result
    }
}

fn transfer_entry(
    provider: &mut dyn RemoteProvider,
    source: &str,
    destination: &str,
    depth: usize,
    source_entry: Option<&FileEntry>,
    progress: &mut Progress<'_>,
    outcome: &mut RemoteTransferOutcome,
) -> Result<(), String> {
    check_cancel(progress.cancel)?;
    if depth > 256 {
        return Err("Folder nesting exceeds 256 levels".into());
    }
    let upload = progress.request.direction == "upload";
    let fetched;
    let resolved_entry = if upload {
        None
    } else if let Some(entry) = source_entry {
        Some(entry)
    } else {
        fetched = remote_entry(provider, source)?
            .ok_or_else(|| format!("Remote source does not exist: {source}"))?;
        Some(&fetched)
    };
    let (is_dir, is_link) = if upload {
        let meta = fs::symlink_metadata(source).map_err(|e| e.to_string())?;
        if !meta.is_dir() && !meta.is_file() && !meta.file_type().is_symlink() {
            return Err("Source is not a regular file or folder".into());
        }
        (meta.is_dir(), meta.file_type().is_symlink())
    } else {
        let entry = resolved_entry.expect("resolved remote entry");
        (entry.is_dir, entry.is_symlink)
    };
    if is_link {
        return Err("Symbolic links are not followed during folder transfers".into());
    }
    if !is_dir {
        let (destination, bytes, skipped) =
            transfer_file(provider, source, destination, resolved_entry, progress)?;
        progress.files += u64::from(!skipped);
        outcome.items.push(RemoteTransferItem {
            source: source.into(),
            destination,
            is_dir: false,
            status: if skipped { "skipped" } else { "completed" }.into(),
            bytes,
            error: None,
        });
        return Ok(());
    }
    if upload {
        match remote_entry(provider, destination)? {
            Some(entry) if entry.is_dir && !entry.is_symlink => {}
            Some(_) => return Err("Destination folder is a file or symbolic link".into()),
            None => {
                let parent =
                    parent_remote_path(destination).ok_or("Remote destination has no parent")?;
                provider.create_directory(&parent, destination.rsplit('/').next().unwrap_or(""))?;
            }
        }
    } else {
        match fs::symlink_metadata(destination) {
            Ok(meta) if meta.is_dir() && !meta.file_type().is_symlink() => {}
            Ok(_) => return Err("Destination folder is a file or symbolic link".into()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                fs::create_dir(destination).map_err(|e| e.to_string())?;
            }
            Err(e) => return Err(e.to_string()),
        }
    }
    let children: Vec<(String, String, Option<FileEntry>)> = if upload {
        fs::read_dir(source)
            .map_err(|e| e.to_string())?
            .map(|entry| {
                let entry = entry.map_err(|e| e.to_string())?;
                Ok((
                    entry.file_name().to_string_lossy().into_owned(),
                    entry.path().to_string_lossy().into_owned(),
                    None,
                ))
            })
            .collect::<Result<_, String>>()?
    } else {
        provider
            .list_directory(source)?
            .entries
            .into_iter()
            .filter(|entry| entry.name != "." && entry.name != "..")
            .map(|entry| {
                (
                    entry.name.clone(),
                    join_remote_path(source, &entry.name),
                    Some(entry),
                )
            })
            .collect()
    };
    let mut failed = false;
    for (name, child, entry) in children {
        check_cancel(progress.cancel)?;
        let result = validate_name(&name).and_then(|()| {
            let target = if upload {
                join_remote_path(destination, &name)
            } else {
                Path::new(destination)
                    .join(&name)
                    .to_string_lossy()
                    .into_owned()
            };
            transfer_entry(
                provider,
                &child,
                &target,
                depth + 1,
                entry.as_ref(),
                progress,
                outcome,
            )
        });
        if let Err(error) = result {
            failed = true;
            outcome.items.push(RemoteTransferItem {
                source: child,
                destination: destination.into(),
                is_dir: false,
                status: if progress.cancel.load(Ordering::Relaxed) {
                    "cancelled"
                } else {
                    "failed"
                }
                .into(),
                bytes: 0,
                error: Some(error),
            });
        }
    }
    if failed {
        return Err("One or more items in this folder could not be transferred".into());
    }
    outcome.items.push(RemoteTransferItem {
        source: source.into(),
        destination: destination.into(),
        is_dir: true,
        status: "completed".into(),
        bytes: 0,
        error: None,
    });
    Ok(())
}

pub fn transfer_entries(
    provider: &mut dyn RemoteProvider,
    request: &RemoteTransferRequest,
    cancel: &AtomicBool,
    emit: &dyn Fn(ProgressUpdate),
) -> Result<RemoteTransferOutcome, String> {
    transfer_entries_with_tuning(provider, request, cancel, emit, &|_| {})
}

pub(crate) fn transfer_entries_with_tuning(
    provider: &mut dyn RemoteProvider,
    request: &RemoteTransferRequest,
    cancel: &AtomicBool,
    emit: &dyn Fn(ProgressUpdate),
    tuning: &dyn Fn(TuningUpdate),
) -> Result<RemoteTransferOutcome, String> {
    transfer_entries_with_policy(provider, request, cancel, emit, tuning, &|| {
        Policy::for_operation(&request.operation_id)
    })
}

fn transfer_entries_with_policy(
    provider: &mut dyn RemoteProvider,
    request: &RemoteTransferRequest,
    cancel: &AtomicBool,
    emit: &dyn Fn(ProgressUpdate),
    tuning: &dyn Fn(TuningUpdate),
    read_policy: &dyn Fn() -> Policy,
) -> Result<RemoteTransferOutcome, String> {
    validate_request(request)?;
    let destination = if request.direction == "download" {
        fs::canonicalize(&request.destination)
            .map_err(|e| e.to_string())?
            .to_string_lossy()
            .into_owned()
    } else {
        normalize_remote_path(&request.destination)
    };
    let mut progress = Progress {
        request,
        cancel,
        emit,
        bytes: 0,
        files: 0,
        current: "Preparing transfer".into(),
        last: Instant::now(),
        tuning,
        policy: read_policy(),
        limiter: RateLimit::new(read_policy().bandwidth_mib),
        policy_checked: Instant::now(),
        read_policy,
        file_start: 0,
    };
    progress.send("running", None);
    let mut outcome = RemoteTransferOutcome::default();
    for source in &request.sources {
        let name = if request.direction == "upload" {
            Path::new(source)
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or("")
        } else {
            source
                .trim_end_matches('/')
                .rsplit('/')
                .next()
                .unwrap_or("")
        };
        let target = if request.direction == "upload" {
            join_remote_path(&destination, name)
        } else {
            Path::new(&destination)
                .join(name)
                .to_string_lossy()
                .into_owned()
        };
        if let Err(error) = validate_name(name).and_then(|()| {
            transfer_entry(
                provider,
                source,
                &target,
                0,
                None,
                &mut progress,
                &mut outcome,
            )
        }) {
            outcome.retry_sources.push(source.clone());
            outcome.items.push(RemoteTransferItem {
                source: source.clone(),
                destination: target,
                is_dir: false,
                status: if cancel.load(Ordering::Relaxed) {
                    "cancelled"
                } else {
                    "failed"
                }
                .into(),
                bytes: 0,
                error: Some(error),
            });
        }
    }
    outcome.cancelled = cancel.load(Ordering::Relaxed);
    progress.send(
        if outcome.cancelled {
            "cancelled"
        } else if outcome.retry_sources.is_empty() {
            "completed"
        } else {
            "error"
        },
        None,
    );
    Ok(outcome)
}

// Compatibility for the original single-file RPCs, with the same streaming and staging guarantees.
pub fn transfer_single(
    provider: &mut dyn RemoteProvider,
    upload: bool,
    source: &str,
    destination: &str,
) -> Result<(), String> {
    let request = RemoteTransferRequest {
        remote_session_id: String::new(),
        direction: if upload { "upload" } else { "download" }.into(),
        sources: vec![source.into()],
        destination: destination.into(),
        conflict_action: "replace".into(),
        operation_id: crate::progress::generate_transfer_operation_id(),
    };
    let cancel = AtomicBool::new(false);
    let mut progress = Progress {
        request: &request,
        cancel: &cancel,
        emit: &|_| {},
        bytes: 0,
        files: 0,
        current: source.into(),
        last: Instant::now(),
        tuning: &|_| {},
        policy: Policy::load(),
        limiter: RateLimit::new(Policy::load().bandwidth_mib),
        policy_checked: Instant::now(),
        read_policy: &Policy::load,
        file_start: 0,
    };
    if !upload {
        if let Some(parent) = Path::new(destination).parent() {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
    }
    transfer_file(provider, source, destination, None, &mut progress).map(|_| ())
}

#[cfg(test)]
mod tests {
    use super::*;
    use simplefile_core::models::DirectoryListing;
    use std::path::PathBuf;
    use std::sync::Arc;

    struct Fixture {
        root: PathBuf,
    }
    impl Fixture {
        fn new() -> Self {
            let root = std::env::temp_dir().join(format!(
                "sumafile-remote-test-{}",
                crate::progress::generate_transfer_operation_id()
            ));
            fs::create_dir_all(root.join("local")).unwrap();
            fs::create_dir(root.join("remote")).unwrap();
            Self { root }
        }
        fn local(&self) -> String {
            self.root.join("local").to_string_lossy().into_owned()
        }
        fn provider(&self) -> DiskProvider {
            DiskProvider {
                root: self.root.join("remote"),
                fail_upload: false,
                fail_promote: false,
                transient_promote_failures: 0,
                commit_before_promote_error: false,
                truncate_download_bytes: None,
                mutate_local_during_upload: None,
                transient_reconnect_failures: 0,
                permanent_reconnect_failure: false,
                cancel: None,
                latency: false,
                transient_failures: 0,
                attempts: 0,
                reconnects: 0,
            }
        }
        fn request(&self, direction: &str, sources: Vec<String>) -> RemoteTransferRequest {
            RemoteTransferRequest {
                remote_session_id: "test".into(),
                direction: direction.into(),
                sources,
                destination: if direction == "upload" {
                    "/".into()
                } else {
                    self.local()
                },
                conflict_action: "replace".into(),
                operation_id: crate::progress::generate_transfer_operation_id(),
            }
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.root);
        }
    }

    struct DiskProvider {
        root: PathBuf,
        fail_upload: bool,
        fail_promote: bool,
        transient_promote_failures: usize,
        commit_before_promote_error: bool,
        truncate_download_bytes: Option<usize>,
        mutate_local_during_upload: Option<(PathBuf, Vec<u8>)>,
        transient_reconnect_failures: usize,
        permanent_reconnect_failure: bool,
        cancel: Option<Arc<AtomicBool>>,
        latency: bool,
        transient_failures: usize,
        attempts: usize,
        reconnects: usize,
    }
    impl DiskProvider {
        fn path(&self, path: &str) -> PathBuf {
            self.root.join(path.trim_start_matches('/'))
        }
        fn entry(&self, path: &str) -> FileEntry {
            let meta = fs::symlink_metadata(self.path(path)).unwrap();
            FileEntry {
                name: path.rsplit('/').next().unwrap().into(),
                path: path.into(),
                is_dir: meta.is_dir(),
                is_symlink: meta.file_type().is_symlink(),
                is_hidden: false,
                is_system: false,
                size: meta.len(),
                modified: "".into(),
                extension: "".into(),
                permissions: None,
                symlink_target: None,
                git_status: None,
            }
        }
    }
    impl RemoteProvider for DiskProvider {
        fn reconnect(&mut self) -> Result<(), crate::remote::provider::RemoteTransferError> {
            self.reconnects += 1;
            if self.permanent_reconnect_failure {
                return Err(Failure::new("authentication rejected on reconnect", false));
            }
            if self.transient_reconnect_failures > 0 {
                self.transient_reconnect_failures -= 1;
                return Err(Failure::io(
                    "Reconnect remote",
                    "test-host",
                    std::io::Error::from(std::io::ErrorKind::TimedOut),
                ));
            }
            Ok(())
        }
        fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
            let entries = fs::read_dir(self.path(path))
                .map_err(|e| e.to_string())?
                .map(|entry| {
                    entry
                        .map(|entry| {
                            self.entry(&join_remote_path(
                                path,
                                &entry.file_name().to_string_lossy(),
                            ))
                        })
                        .map_err(|e| e.to_string())
                })
                .collect::<Result<_, _>>()?;
            Ok(DirectoryListing {
                path: path.into(),
                parent: parent_remote_path(path),
                entries,
                is_network: true,
            })
        }
        fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
            let target = join_remote_path(path, name);
            fs::create_dir(self.path(&target)).map_err(|e| e.to_string())?;
            Ok(self.entry(&target))
        }
        fn rename_entry(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
            let target = join_remote_path(&parent_remote_path(path).unwrap(), name);
            if path.contains("sumafile-partial") {
                if self.fail_promote {
                    return Err("injected promotion failure".into());
                }
                if self.transient_promote_failures > 0 {
                    self.transient_promote_failures -= 1;
                    if self.commit_before_promote_error {
                        fs::rename(self.path(path), self.path(&target))
                            .map_err(|e| e.to_string())?;
                    }
                    return Err("transient promotion connection lost".into());
                }
            }
            fs::rename(self.path(path), self.path(&target)).map_err(|e| e.to_string())?;
            Ok(self.entry(&target))
        }
        fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
            for path in paths {
                fs::remove_file(self.path(path)).map_err(|e| e.to_string())?;
            }
            Ok(paths.to_vec())
        }
        fn download(
            &mut self,
            path: &str,
            destination: &mut dyn Write,
        ) -> Result<u64, crate::remote::provider::RemoteTransferError> {
            self.attempts += 1;
            if self.transient_failures > 0 {
                self.transient_failures -= 1;
                destination.write_all(b"bad partial").unwrap();
                return Err(Failure::io(
                    "Read remote",
                    path,
                    std::io::Error::from(std::io::ErrorKind::TimedOut),
                ));
            }
            if let Some(limit) = self.truncate_download_bytes {
                let bytes = fs::read(self.path(path)).map_err(|e| e.to_string())?;
                let slice = &bytes[..limit.min(bytes.len())];
                destination.write_all(slice).map_err(|e| e.to_string())?;
                destination.flush().map_err(|e| e.to_string())?;
                return Ok(slice.len() as u64);
            }
            let mut source = crate::transfer_test_io::DelayedIo::new(
                File::open(self.path(path)).map_err(|e| e.to_string())?,
                self.latency,
            );
            if let Some(cancel) = &self.cancel {
                let mut buffer = vec![0; TRANSFER_BUFFER_SIZE];
                let read = source.read(&mut buffer).unwrap();
                destination
                    .write_all(&buffer[..read])
                    .map_err(|e| e.to_string())?;
                cancel.store(true, Ordering::Relaxed);
            }
            copy_stream(&mut source, destination)
        }
        fn upload(
            &mut self,
            path: &str,
            source: &mut dyn Read,
        ) -> Result<u64, crate::remote::provider::RemoteTransferError> {
            self.attempts += 1;
            let mut file = crate::transfer_test_io::DelayedIo::new(
                File::create(self.path(path)).map_err(|e| e.to_string())?,
                self.latency,
            );
            if self.transient_failures > 0 {
                self.transient_failures -= 1;
                file.write_all(b"bad partial").unwrap();
                return Err(Failure::io(
                    "Write remote",
                    path,
                    std::io::Error::from(std::io::ErrorKind::TimedOut),
                ));
            }
            if self.fail_upload {
                file.write_all(b"partial").unwrap();
                return Err("injected network failure".into());
            }
            let copied = copy_stream(source, &mut file)?;
            if let Some((local_path, replacement)) = &self.mutate_local_during_upload {
                fs::write(local_path, replacement).unwrap();
            }
            Ok(copied)
        }
    }

    fn delayed_remote_transfer(upload: bool) {
        use crate::progress::Mode;
        for mode in [
            Mode::Automatic,
            Mode::Conservative,
            Mode::Maximum,
            Mode::Manual,
        ] {
            let fixture = Fixture::new();
            let mut provider = fixture.provider();
            provider.latency = true;
            let bytes: Vec<u8> = (0..256 * 1024).map(|n| (n % 251) as u8).collect();
            let mut sources = Vec::new();
            for index in 0..3 {
                let name = format!("image-{index}.bin");
                let source = if upload {
                    fixture.root.join("local").join(&name)
                } else {
                    provider.path(&name)
                };
                fs::write(&source, &bytes).unwrap();
                sources.push(if upload {
                    source.to_string_lossy().into_owned()
                } else {
                    format!("/{name}")
                });
            }
            let request = fixture.request(if upload { "upload" } else { "download" }, sources);
            let policy = || Policy {
                mode,
                manual_concurrency: 16,
                bandwidth_mib: 0,
                ..Policy::default()
            };
            let snapshots = std::sync::Mutex::new(Vec::new());
            let started = Instant::now();
            let result = transfer_entries_with_policy(
                &mut provider,
                &request,
                &AtomicBool::new(false),
                &|_| {},
                &|update| snapshots.lock().unwrap().push(update),
                &policy,
            )
            .unwrap();
            assert!(result.retry_sources.is_empty());
            assert_eq!(provider.attempts, 3);
            assert_eq!(provider.reconnects, 0);
            assert!(snapshots
                .lock()
                .unwrap()
                .iter()
                .all(|s| !s.explanation.contains("safety limit")
                    && !s.explanation.contains("Retrying")));
            for index in 0..3 {
                let name = format!("image-{index}.bin");
                let output = if upload {
                    provider.path(&name)
                } else {
                    fixture.root.join("local").join(&name)
                };
                assert_eq!(fs::read(output).unwrap(), bytes);
            }
            eprintln!("latency remote upload={upload} mode={mode:?}: 3 files, exact contents, no hidden cap, {:.2}s", started.elapsed().as_secs_f64());
        }
    }

    #[test]
    fn native_upload_with_repeated_nas_write_and_flush_latency() {
        delayed_remote_transfer(true);
    }
    #[test]
    fn native_download_with_repeated_cold_cloud_read_latency() {
        delayed_remote_transfer(false);
    }

    #[test]
    fn native_custom_bandwidth_is_enforced_for_both_directions() {
        for upload in [true, false] {
            let fixture = Fixture::new();
            let mut provider = fixture.provider();
            let bytes = vec![19; 1024 * 1024];
            let local = fixture.root.join("local/limited.bin");
            let source = if upload {
                fs::write(&local, &bytes).unwrap();
                local.to_string_lossy().into_owned()
            } else {
                fs::write(provider.path("/limited.bin"), &bytes).unwrap();
                "/limited.bin".into()
            };
            let request = fixture.request(if upload { "upload" } else { "download" }, vec![source]);
            let policy = || Policy {
                mode: crate::progress::Mode::Manual,
                bandwidth_mib: 1,
                ..Policy::default()
            };
            let started = Instant::now();
            let result = transfer_entries_with_policy(
                &mut provider,
                &request,
                &AtomicBool::new(false),
                &|_| {},
                &|_| {},
                &policy,
            )
            .unwrap();
            assert!(result.retry_sources.is_empty());
            assert!(
                started.elapsed() >= Duration::from_millis(900),
                "Custom bandwidth was ignored"
            );
            assert!(
                started.elapsed() < Duration::from_secs(3),
                "Unexpected additional throttling"
            );
            let output = if upload {
                provider.path("/limited.bin")
            } else {
                local
            };
            assert_eq!(fs::read(output).unwrap(), bytes);
        }
    }

    #[test]
    fn remote_upload_and_download_restart_owned_partial_and_report_waiting() {
        for upload in [false, true] {
            let fixture = Fixture::new();
            let mut provider = fixture.provider();
            let bytes: Vec<u8> = (0..TRANSFER_BUFFER_SIZE + 17)
                .map(|n| (n % 253) as u8)
                .collect();
            let source = if upload {
                let local = fixture.root.join("local/photo.bin");
                fs::write(&local, &bytes).unwrap();
                local.to_string_lossy().into_owned()
            } else {
                fs::write(provider.path("/photo.bin"), &bytes).unwrap();
                "/photo.bin".into()
            };
            provider.transient_failures = 1;
            let request = fixture.request(if upload { "upload" } else { "download" }, vec![source]);
            let waiting = AtomicBool::new(false);
            let outcome = transfer_entries_with_tuning(
                &mut provider,
                &request,
                &AtomicBool::new(false),
                &|_| {},
                &|update| {
                    if update.explanation.starts_with("Retrying failed I/O") {
                        waiting.store(true, Ordering::Relaxed);
                    }
                },
            )
            .unwrap();
            assert!(outcome.retry_sources.is_empty(), "{:?}", outcome.items);
            assert!(waiting.load(Ordering::Relaxed));
            assert_eq!(provider.attempts, 2);
            assert_eq!(provider.reconnects, 1);
            let output = if upload {
                provider.path("/photo.bin")
            } else {
                fixture.root.join("local/photo.bin")
            };
            assert_eq!(fs::read(output).unwrap(), bytes);
            assert!(fs::read_dir(&provider.root).unwrap().all(|e| !e
                .unwrap()
                .file_name()
                .to_string_lossy()
                .contains("partial")));
        }
    }

    #[test]
    fn remote_retry_wait_can_be_cancelled_without_promotion_or_reconnect() {
        for upload in [false, true] {
            let fixture = Fixture::new();
            let mut provider = fixture.provider();
            let local = fixture.root.join("local/photo.bin");
            fs::write(&local, b"original local").unwrap();
            fs::write(provider.path("/photo.bin"), b"original remote").unwrap();
            provider.transient_failures = 1;
            let mut request = fixture.request(
                if upload { "upload" } else { "download" },
                vec![if upload {
                    local.to_string_lossy().into_owned()
                } else {
                    "/photo.bin".into()
                }],
            );
            request.conflict_action = "replace".into();
            let cancel = AtomicBool::new(false);
            let result = transfer_entries_with_tuning(
                &mut provider,
                &request,
                &cancel,
                &|_| {},
                &|update| {
                    if update.explanation.starts_with("Retrying failed I/O") {
                        cancel.store(true, Ordering::Relaxed);
                    }
                },
            )
            .unwrap();
            assert!(result.cancelled);
            assert_eq!(provider.attempts, 1);
            assert_eq!(provider.reconnects, 0);
            assert_eq!(fs::read(&local).unwrap(), b"original local");
            assert_eq!(
                fs::read(provider.path("/photo.bin")).unwrap(),
                b"original remote"
            );
        }
    }

    #[test]
    fn folders_round_trip_including_empty_folders_and_large_files() {
        let fixture = Fixture::new();
        let mut provider = fixture.provider();
        let local = fixture.root.join("local/site");
        fs::create_dir_all(local.join("empty")).unwrap();
        let bytes = vec![42; TRANSFER_BUFFER_SIZE * 17 + 3];
        fs::write(local.join("large.bin"), &bytes).unwrap();
        let request = fixture.request("upload", vec![local.to_string_lossy().into_owned()]);
        let uploaded =
            transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {}).unwrap();
        assert!(uploaded.retry_sources.is_empty());
        assert_eq!(uploaded.items.iter().filter(|i| !i.is_dir).count(), 1);
        assert_eq!(fs::read(provider.path("/site/large.bin")).unwrap(), bytes);
        fs::remove_file(local.join("large.bin")).unwrap();
        let downloaded = transfer_entries(
            &mut provider,
            &fixture.request("download", vec!["/site".into()]),
            &AtomicBool::new(false),
            &|_| {},
        )
        .unwrap();
        assert!(downloaded.retry_sources.is_empty());
        assert!(local.join("empty").is_dir());
        assert_eq!(fs::read(local.join("large.bin")).unwrap(), bytes);
    }

    #[test]
    fn interrupted_upload_and_failed_promotion_preserve_original_and_clean_staging() {
        let fixture = Fixture::new();
        let mut provider = fixture.provider();
        let source = fixture.root.join("local/app.txt");
        fs::write(&source, b"new").unwrap();
        fs::write(provider.path("/app.txt"), b"original").unwrap();
        let request = fixture.request("upload", vec![source.to_string_lossy().into_owned()]);
        for fail_upload in [true, false] {
            provider.fail_upload = fail_upload;
            provider.fail_promote = !fail_upload;
            let outcome =
                transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {})
                    .unwrap();
            assert_eq!(outcome.retry_sources, request.sources);
            assert_eq!(fs::read(provider.path("/app.txt")).unwrap(), b"original");
            assert_eq!(fs::read_dir(&provider.root).unwrap().count(), 1);
        }
        provider.fail_promote = false;
        assert!(
            transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {})
                .unwrap()
                .retry_sources
                .is_empty()
        );
        assert_eq!(fs::read(provider.path("/app.txt")).unwrap(), b"new");
    }

    #[test]
    fn cancelled_download_preserves_destination_and_reports_retry_sources() {
        let fixture = Fixture::new();
        let mut provider = fixture.provider();
        let cancel = Arc::new(AtomicBool::new(false));
        provider.cancel = Some(cancel.clone());
        fs::write(
            provider.path("/large.bin"),
            vec![1; TRANSFER_BUFFER_SIZE * 3],
        )
        .unwrap();
        fs::write(fixture.root.join("local/large.bin"), b"original").unwrap();
        let request = fixture.request("download", vec!["/large.bin".into()]);
        let outcome = transfer_entries(&mut provider, &request, &cancel, &|_| {}).unwrap();
        assert!(outcome.cancelled);
        assert_eq!(outcome.retry_sources, request.sources);
        assert_eq!(
            fs::read(fixture.root.join("local/large.bin")).unwrap(),
            b"original"
        );
        assert_eq!(fs::read_dir(fixture.root.join("local")).unwrap().count(), 1);
    }

    #[test]
    fn conflict_policies_preserve_or_replace_only_the_selected_file() {
        let fixture = Fixture::new();
        let mut provider = fixture.provider();
        fs::write(provider.path("/a.txt"), b"new").unwrap();
        fs::write(fixture.root.join("local/a.txt"), b"old").unwrap();
        let mut request = fixture.request("download", vec!["/a.txt".into()]);
        for (policy, status) in [
            ("error", "failed"),
            ("skip", "skipped"),
            ("keep-both", "completed"),
        ] {
            request.conflict_action = policy.into();
            let outcome =
                transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {})
                    .unwrap();
            assert_eq!(outcome.items[0].status, status);
            assert_eq!(fs::read(fixture.root.join("local/a.txt")).unwrap(), b"old");
        }
        assert_eq!(
            fs::read(fixture.root.join("local/a.txt.copy-1")).unwrap(),
            b"new"
        );
    }

    #[test]
    fn unsafe_names_cannot_escape_download_destination() {
        for name in [
            "..",
            "../escape",
            "a\\b",
            "C:stream",
            "NUL.txt",
            "COM1",
            "name.",
        ] {
            assert!(validate_name(name).is_err(), "{name}");
        }
        assert!(validate_name("photos 2026.jpg").is_ok());
    }

    #[test]
    fn stream_copy_never_requests_a_whole_file_buffer() {
        struct Generated {
            remaining: usize,
            largest_request: usize,
        }
        impl Read for Generated {
            fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
                self.largest_request = self.largest_request.max(buffer.len());
                let read = self.remaining.min(buffer.len());
                buffer[..read].fill(7);
                self.remaining -= read;
                Ok(read)
            }
        }
        let mut source = Generated {
            remaining: 128 * 1024 * 1024,
            largest_request: 0,
        };
        assert_eq!(
            copy_stream(&mut source, &mut std::io::sink()).unwrap(),
            128 * 1024 * 1024
        );
        assert!(source.largest_request <= TRANSFER_BUFFER_SIZE);
    }

    #[test]
    fn download_returning_eof_before_expected_remote_size_rejects_promotion() {
        let fixture = Fixture::new();
        let mut provider = fixture.provider();
        fs::write(provider.path("/truncated.bin"), b"0123456789abcdef").unwrap();
        let local_target = fixture.root.join("local/truncated.bin");
        fs::write(&local_target, b"existing-valid-destination").unwrap();

        // Provider returns Ok(5) (premature EOF) instead of expected 16 bytes
        provider.truncate_download_bytes = Some(5);

        let request = fixture.request("download", vec!["/truncated.bin".into()]);
        let outcome =
            transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {}).unwrap();

        assert_eq!(outcome.retry_sources, vec!["/truncated.bin".to_string()]);
        assert_eq!(outcome.items[0].status, "failed");
        assert!(outcome.items[0]
            .error
            .as_deref()
            .unwrap_or("")
            .contains("Remote source size mismatch"));
        assert_eq!(
            fs::read(&local_target).unwrap(),
            b"existing-valid-destination"
        );
        assert_eq!(fs::read_dir(fixture.root.join("local")).unwrap().count(), 1);
    }

    #[test]
    fn upload_source_modified_during_transfer_rejects_promotion() {
        let fixture = Fixture::new();
        let mut provider = fixture.provider();
        let local_source = fixture.root.join("local/mutable.txt");
        fs::write(&local_source, b"version-one").unwrap();
        fs::write(provider.path("/mutable.txt"), b"original-remote").unwrap();

        // Same-length modification during upload to ensure content/metadata check catches it
        provider.mutate_local_during_upload = Some((local_source.clone(), b"version-two".to_vec()));

        let request = fixture.request("upload", vec![local_source.to_string_lossy().into_owned()]);
        let outcome =
            transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {}).unwrap();

        assert_eq!(outcome.retry_sources.len(), 1);
        assert_eq!(outcome.items[0].status, "failed");
        assert_eq!(
            fs::read(provider.path("/mutable.txt")).unwrap(),
            b"original-remote"
        );
        assert_eq!(fs::read_dir(&provider.root).unwrap().count(), 1);
    }

    #[test]
    fn temporary_reconnect_failure_continues_bounded_recovery() {
        let fixture = Fixture::new();
        let mut provider = fixture.provider();
        let local_source = fixture.root.join("local/recover.bin");
        fs::write(&local_source, b"recoverable-payload").unwrap();

        // First upload attempt fails transiently, AND the first reconnect attempt also fails transiently!
        provider.transient_failures = 1;
        provider.transient_reconnect_failures = 1;

        let request = fixture.request("upload", vec![local_source.to_string_lossy().into_owned()]);
        let outcome =
            transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {}).unwrap();

        assert!(outcome.retry_sources.is_empty(), "{:?}", outcome.items);
        assert_eq!(provider.attempts, 2);
        assert_eq!(provider.reconnects, 2);
        assert_eq!(
            fs::read(provider.path("/recover.bin")).unwrap(),
            b"recoverable-payload"
        );
    }

    #[test]
    fn upload_finalization_recovers_from_transient_and_already_committed_rename_failures() {
        for committed_on_server in [false, true] {
            let fixture = Fixture::new();
            let mut provider = fixture.provider();
            let local_source = fixture.root.join("local/final.txt");
            fs::write(&local_source, b"new-remote-payload").unwrap();
            fs::write(provider.path("/final.txt"), b"old-remote-payload").unwrap();

            provider.transient_promote_failures = 1;
            provider.commit_before_promote_error = committed_on_server;

            let request =
                fixture.request("upload", vec![local_source.to_string_lossy().into_owned()]);
            let outcome =
                transfer_entries(&mut provider, &request, &AtomicBool::new(false), &|_| {})
                    .unwrap();

            assert!(outcome.retry_sources.is_empty(), "{:?}", outcome.items);
            assert_eq!(provider.attempts, 1, "upload itself should not be repeated");
            assert_eq!(provider.reconnects, 1);
            assert_eq!(
                fs::read(provider.path("/final.txt")).unwrap(),
                b"new-remote-payload"
            );
            assert_eq!(fs::read_dir(&provider.root).unwrap().count(), 1);
        }
    }
}
