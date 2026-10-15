use super::path::{join_remote_path, normalize_remote_path, parent_remote_path};
use super::provider::RemoteProvider;
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

pub fn copy_stream(source: &mut dyn Read, destination: &mut dyn Write) -> Result<u64, String> {
    let mut buffer = vec![0; TRANSFER_BUFFER_SIZE];
    let mut count = 0;
    loop {
        let read = source.read(&mut buffer).map_err(|e| e.to_string())?;
        if read == 0 {
            break;
        }
        destination
            .write_all(&buffer[..read])
            .map_err(|e| e.to_string())?;
        count += read as u64;
    }
    destination.flush().map_err(|e| e.to_string())?;
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
}

impl Progress<'_> {
    fn report(&mut self, bytes: u64) -> std::io::Result<()> {
        check_cancel(self.cancel).map_err(std::io::Error::other)?;
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
    }
}

struct ObservedReader<'a, 'b> {
    source: File,
    progress: &'a mut Progress<'b>,
}
impl Read for ObservedReader<'_, '_> {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        self.progress.report(0)?;
        let read = self.source.read(buffer)?;
        self.progress.report(read as u64)?;
        Ok(read)
    }
}

struct ObservedWriter<'a, 'b> {
    destination: File,
    progress: &'a mut Progress<'b>,
}
impl Write for ObservedWriter<'_, '_> {
    fn write(&mut self, buffer: &[u8]) -> std::io::Result<usize> {
        self.progress.report(0)?;
        let written = self.destination.write(buffer)?;
        self.progress.report(written as u64)?;
        Ok(written)
    }
    fn flush(&mut self) -> std::io::Result<()> {
        self.destination.flush()
    }
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
    progress: &mut Progress<'_>,
) -> Result<(String, u64, bool), String> {
    check_cancel(progress.cancel)?;
    progress.current = source.to_string();
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
        let mut reader = ObservedReader {
            source: File::open(source).map_err(|e| e.to_string())?,
            progress,
        };
        let transferred = provider.upload(&staging, &mut reader).and_then(|count| {
            check_cancel(reader.progress.cancel)?;
            Ok(count)
        });
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
        if let Some(backup) = &backup {
            if let Err(error) =
                provider.rename_entry(&target, backup.rsplit('/').next().unwrap_or(""))
            {
                return Err(cleanup_error(
                    error,
                    provider
                        .delete_entries(std::slice::from_ref(&staging))
                        .map(|_| ()),
                    &staging,
                ));
            }
        }
        if let Err(error) = provider.rename_entry(&staging, target.rsplit('/').next().unwrap_or(""))
        {
            let restore = if let Some(backup) = &backup {
                provider
                    .rename_entry(backup, target.rsplit('/').next().unwrap_or(""))
                    .map(|_| ())
            } else {
                Ok(())
            };
            let error = match restore {
                Ok(()) => error,
                Err(e) => format!(
                    "{error}; original remains at {}: {e}",
                    backup.unwrap_or_default()
                ),
            };
            return Err(cleanup_error(
                error,
                provider
                    .delete_entries(std::slice::from_ref(&staging))
                    .map(|_| ()),
                &staging,
            ));
        }
        if let Some(backup) = backup {
            provider
                .delete_entries(std::slice::from_ref(&backup))
                .map_err(|e| {
                    format!("Transferred, but original backup remains at {backup}: {e}")
                })?;
        }
        Ok((target, count, false))
    } else {
        let staging = crate::transfer_staging::staging_path_for(
            Path::new(&target),
            &progress.request.operation_id,
        )?;
        let file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&staging)
            .map_err(|e| e.to_string())?;
        let result = {
            let mut writer = ObservedWriter {
                destination: file,
                progress,
            };
            provider.download(source, &mut writer).and_then(|count| {
                writer.destination.sync_all().map_err(|e| e.to_string())?;
                check_cancel(writer.progress.cancel)?;
                Ok(count)
            })
        };
        let result = result.and_then(|count| {
            crate::transfer_staging::promote_staged_path(&staging, Path::new(&target), replace)?;
            Ok((target, count, false))
        });
        if let Err(error) = result {
            return Err(if local_exists(&staging) {
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
    let (is_dir, is_link) = if upload {
        let meta = fs::symlink_metadata(source).map_err(|e| e.to_string())?;
        if !meta.is_dir() && !meta.is_file() && !meta.file_type().is_symlink() {
            return Err("Source is not a regular file or folder".into());
        }
        (meta.is_dir(), meta.file_type().is_symlink())
    } else {
        let fetched;
        let entry = if let Some(entry) = source_entry {
            entry
        } else {
            fetched = remote_entry(provider, source)?
                .ok_or_else(|| format!("Remote source does not exist: {source}"))?;
            &fetched
        };
        (entry.is_dir, entry.is_symlink)
    };
    if is_link {
        return Err("Symbolic links are not followed during folder transfers".into());
    }
    if !is_dir {
        let (destination, bytes, skipped) = transfer_file(provider, source, destination, progress)?;
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
    };
    if !upload {
        if let Some(parent) = Path::new(destination).parent() {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
    }
    transfer_file(provider, source, destination, &mut progress).map(|_| ())
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
                cancel: None,
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
        cancel: Option<Arc<AtomicBool>>,
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
            if self.fail_promote && path.contains("sumafile-partial") {
                return Err("injected promotion failure".into());
            }
            let target = join_remote_path(&parent_remote_path(path).unwrap(), name);
            fs::rename(self.path(path), self.path(&target)).map_err(|e| e.to_string())?;
            Ok(self.entry(&target))
        }
        fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
            for path in paths {
                fs::remove_file(self.path(path)).map_err(|e| e.to_string())?;
            }
            Ok(paths.to_vec())
        }
        fn download(&mut self, path: &str, destination: &mut dyn Write) -> Result<u64, String> {
            let mut source = File::open(self.path(path)).map_err(|e| e.to_string())?;
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
        fn upload(&mut self, path: &str, source: &mut dyn Read) -> Result<u64, String> {
            let mut file = File::create(self.path(path)).map_err(|e| e.to_string())?;
            if self.fail_upload {
                file.write_all(b"partial").unwrap();
                return Err("injected network failure".into());
            }
            copy_stream(source, &mut file)
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
}
