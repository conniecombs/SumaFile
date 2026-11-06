use crate::transfer_reliability::{Failure, Retry};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use simplefile_core::path_conflict::{
    is_already_exists_error, path_exists_no_follow, rename_exclusive,
};
use std::fs;
use std::hash::{Hash, Hasher};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct FileIdentity(pub(crate) u64, pub(crate) u64);

pub(crate) fn file_identity(path: &Path) -> Result<FileIdentity, String> {
    #[cfg(windows)]
    {
        let file = open_for_identity(path)?;
        let (volume, index) = file_id_from_handle(&file)
            .ok_or_else(|| format!("Cannot establish file identity [{}]", path.display()))?;
        Ok(FileIdentity(volume as u64, index))
    }
    #[cfg(not(windows))]
    {
        use std::os::unix::fs::MetadataExt;
        let meta = fs::symlink_metadata(path).map_err(|e| e.to_string())?;
        Ok(FileIdentity(meta.dev(), meta.ino()))
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct SourceVersion {
    pub(crate) is_symlink: bool,
    pub(crate) is_dir: bool,
    pub(crate) len: u64,
    // Signed nanoseconds also round-trip valid filesystem dates before 1970.
    pub(crate) modified: Option<i128>,
    #[cfg(windows)]
    pub(crate) creation_time: u64,
    #[cfg(windows)]
    pub(crate) last_write_time: u64,
    #[cfg(windows)]
    pub(crate) file_id: Option<(u32, u64)>,
    pub(crate) content_hash: Option<[u8; 32]>,
}

#[derive(Debug, Default, Clone)]
pub(crate) struct TreeCopyManifest {
    pub(crate) files: crate::transfer_manifest::CopiedFiles,
    pub(crate) source_dirs: Vec<(PathBuf, SourceVersion)>,
}

/// SHA-256 is independent of read boundaries, including short and resumed reads.
#[derive(Clone, Default)]
pub(crate) struct ContentHasher(Sha256);

impl ContentHasher {
    pub(crate) fn new() -> Self {
        Self::default()
    }

    pub(crate) fn update(&mut self, bytes: &[u8]) {
        self.0.update(bytes);
    }

    pub(crate) fn finish(self) -> [u8; 32] {
        self.0.finalize().into()
    }
}

#[cfg(windows)]
pub(crate) fn preallocate_file(file: &fs::File, size: u64) {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        FileAllocationInfo, SetFileInformationByHandle, FILE_ALLOCATION_INFO,
    };
    if size < 64 * 1024 || size > i64::MAX as u64 {
        return;
    }
    let info = FILE_ALLOCATION_INFO {
        AllocationSize: size as i64,
    };
    unsafe {
        let _ = SetFileInformationByHandle(
            file.as_raw_handle() as _,
            FileAllocationInfo,
            (&info as *const FILE_ALLOCATION_INFO).cast(),
            std::mem::size_of::<FILE_ALLOCATION_INFO>() as u32,
        );
    }
}

#[cfg(not(windows))]
pub(crate) fn preallocate_file(_file: &fs::File, _size: u64) {}

#[cfg(windows)]
pub(crate) fn file_id_from_handle(file: &fs::File) -> Option<(u32, u64)> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        GetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION,
    };

    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    let ok = unsafe { GetFileInformationByHandle(file.as_raw_handle() as _, &mut info) };
    if ok != 0 {
        let index = ((info.nFileIndexHigh as u64) << 32) | (info.nFileIndexLow as u64);
        Some((info.dwVolumeSerialNumber, index))
    } else {
        None
    }
}

/// Read size for verification passes: large enough to keep a network share or
/// cloud mount streaming, small enough to come from the shared buffer pool.
pub(crate) const VERIFY_CHUNK: usize = 1024 * 1024;

pub(crate) fn hash_file_contents(file: &mut fs::File) -> Result<[u8; 32], String> {
    file.seek(SeekFrom::Start(0))
        .map_err(|e| format!("Failed to seek source file for hashing: {e}"))?;
    let mut buffer = crate::progress::PooledBuffer::acquire(VERIFY_CHUNK);
    let mut hasher = ContentHasher::new();
    loop {
        let read = file
            .read(buffer.as_mut_slice())
            .map_err(|e| format!("Failed to read source file for hashing: {e}"))?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hasher.finish())
}

fn modified_timestamp(meta: &fs::Metadata) -> Option<i128> {
    meta.modified()
        .ok()
        .map(|time| match time.duration_since(SystemTime::UNIX_EPOCH) {
            Ok(duration) => duration.as_nanos() as i128,
            Err(error) => -(error.duration().as_nanos() as i128),
        })
}

pub(crate) fn capture_source_version_from_open_file(
    file: &fs::File,
    meta: &fs::Metadata,
    content_hash: Option<[u8; 32]>,
) -> SourceVersion {
    #[cfg(windows)]
    use std::os::windows::fs::MetadataExt;

    SourceVersion {
        is_symlink: meta.file_type().is_symlink(),
        is_dir: meta.is_dir(),
        len: meta.len(),
        modified: modified_timestamp(meta),
        #[cfg(windows)]
        creation_time: meta.creation_time(),
        #[cfg(windows)]
        last_write_time: meta.last_write_time(),
        #[cfg(windows)]
        file_id: file_id_from_handle(file),
        content_hash,
    }
}

pub(crate) fn capture_source_version_for_path(path: &Path) -> Result<SourceVersion, String> {
    let meta = fs::symlink_metadata(path)
        .map_err(|e| format!("Failed to stat source [{}]: {e}", path.display()))?;
    #[cfg(windows)]
    use std::os::windows::fs::MetadataExt;

    if meta.file_type().is_symlink() || meta.is_dir() {
        return Ok(SourceVersion {
            is_symlink: meta.file_type().is_symlink(),
            is_dir: meta.is_dir(),
            len: meta.len(),
            modified: modified_timestamp(&meta),
            #[cfg(windows)]
            creation_time: meta.creation_time(),
            #[cfg(windows)]
            last_write_time: meta.last_write_time(),
            #[cfg(windows)]
            file_id: open_source_for_verification(path)
                .ok()
                .and_then(|file| file_id_from_handle(&file)),
            content_hash: None,
        });
    }

    let file = open_source_for_verification(path)?;
    let file_meta = file
        .metadata()
        .map_err(|e| format!("Failed to stat opened source [{}]: {e}", path.display()))?;
    Ok(capture_source_version_from_open_file(
        &file, &file_meta, None,
    ))
}

/// Like `capture_source_version_for_path`, with a SHA-256 of a regular file's
/// content so a later verification can detect same-size, same-time rewrites.
#[allow(dead_code)]
pub(crate) fn capture_source_version_for_path_hashed(path: &Path) -> Result<SourceVersion, String> {
    let mut version = capture_source_version_for_path(path)?;
    if !version.is_symlink && !version.is_dir {
        let mut file = fs::File::open(path)
            .map_err(|e| format!("Failed to open source [{}]: {e}", path.display()))?;
        version.content_hash = Some(hash_file_contents(&mut file)?);
    }
    Ok(version)
}

/// A sibling name that does not exist yet, for swapping a destination aside.
pub(crate) fn unique_backup_path(final_path: &Path) -> Result<PathBuf, String> {
    let parent = final_path
        .parent()
        .ok_or_else(|| "Cannot get destination directory".to_string())?;
    let file_name = final_path
        .file_name()
        .ok_or_else(|| "Cannot get destination file name".to_string())?
        .to_string_lossy();
    let process_id = std::process::id();
    for index in 0..10_000u32 {
        let candidate = parent.join(format!(
            ".{file_name}.{process_id}.{index}.sumafile-replace-backup"
        ));
        if !path_exists_no_follow(&candidate) {
            return Ok(candidate);
        }
    }
    Err(format!(
        "Could not choose replacement backup path for {}",
        final_path.to_string_lossy()
    ))
}

fn metadata_matches_source_version(meta: &fs::Metadata, expected: &SourceVersion) -> bool {
    #[cfg(windows)]
    use std::os::windows::fs::MetadataExt;

    if meta.file_type().is_symlink() != expected.is_symlink || meta.is_dir() != expected.is_dir {
        return false;
    }
    if !expected.is_symlink && !expected.is_dir && meta.len() != expected.len {
        return false;
    }
    if !expected.is_dir && modified_timestamp(meta) != expected.modified {
        return false;
    }
    #[cfg(windows)]
    {
        if meta.creation_time() != expected.creation_time
            || (!expected.is_dir && meta.last_write_time() != expected.last_write_time)
        {
            return false;
        }
    }
    true
}

pub(crate) fn verify_source_unchanged(path: &Path, expected: &SourceVersion) -> Result<(), String> {
    let lstat = fs::symlink_metadata(path)
        .map_err(|e| format!("Failed to stat source [{}]: {e}", path.display()))?;
    if !metadata_matches_source_version(&lstat, expected) {
        return Err(format!(
            "Source changed before move completion and was retained: {}",
            path.display()
        ));
    }
    #[cfg(not(windows))]
    if expected.is_symlink || expected.is_dir {
        return Ok(());
    }

    let mut file = open_source_for_verification(path)?;
    let file_meta = file
        .metadata()
        .map_err(|e| format!("Failed to stat source handle [{}]: {e}", path.display()))?;
    if !metadata_matches_source_version(&file_meta, expected) {
        return Err(format!(
            "Source changed before move completion and was retained: {}",
            path.display()
        ));
    }
    #[cfg(windows)]
    {
        if expected.file_id.is_none() || expected.file_id != file_id_from_handle(&file) {
            return Err(format!(
                "Source identity changed before move completion and was retained: {}",
                path.display()
            ));
        }
    }
    if let Some(expected_hash) = expected.content_hash {
        let actual_hash = hash_file_contents(&mut file)?;
        if actual_hash != expected_hash {
            return Err(format!(
                "Source content changed before move completion and was retained: {}",
                path.display()
            ));
        }
    }
    Ok(())
}

#[cfg(windows)]
fn open_source_handle(path: &Path, deleting: bool) -> Result<fs::File, String> {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_DELETE,
        FILE_SHARE_READ,
    };
    const GENERIC_READ: u32 = 0x8000_0000;
    const DELETE_ACCESS: u32 = 0x0001_0000;
    const FILE_WRITE_ATTRIBUTES_ACCESS: u32 = 0x100;
    fs::OpenOptions::new()
        .access_mode(
            GENERIC_READ
                | if deleting {
                    DELETE_ACCESS | FILE_WRITE_ATTRIBUTES_ACCESS
                } else {
                    0
                },
        )
        .share_mode(FILE_SHARE_READ | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
        .map_err(|e| {
            format!(
                "Failed to lock source for verification [{}]: {e}",
                path.display()
            )
        })
}

#[cfg(windows)]
fn open_source_for_verification(path: &Path) -> Result<fs::File, String> {
    open_source_handle(path, false)
}

/// Identity only: attribute access with full sharing, so the check succeeds while
/// this process (the lease still holds the handle it created the staging file
/// with) or anyone else has the object open for writing. `open_source_handle`
/// deliberately refuses writers; used here it reported every live owned staging
/// file as "identity changed".
#[cfg(windows)]
fn open_for_identity(path: &Path) -> Result<fs::File, String> {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_DELETE,
        FILE_SHARE_READ, FILE_SHARE_WRITE,
    };
    const FILE_READ_ATTRIBUTES_ACCESS: u32 = 0x80;
    fs::OpenOptions::new()
        .access_mode(FILE_READ_ATTRIBUTES_ACCESS)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
        .map_err(|e| format!("Failed to open [{}] for identity: {e}", path.display()))
}

#[cfg(not(windows))]
fn open_source_for_verification(path: &Path) -> Result<fs::File, String> {
    fs::File::open(path).map_err(|e| format!("Failed to open source [{}]: {e}", path.display()))
}

#[cfg(windows)]
fn delete_open_source(file: &fs::File, path: &Path) -> Result<(), String> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        FileBasicInfo, FileDispositionInfo, FileDispositionInfoEx, GetFileInformationByHandleEx,
        SetFileInformationByHandle, FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_READONLY,
        FILE_BASIC_INFO, FILE_DISPOSITION_FLAG_DELETE,
        FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE, FILE_DISPOSITION_INFO,
        FILE_DISPOSITION_INFO_EX,
    };
    let handle = file.as_raw_handle() as _;
    let info = FILE_DISPOSITION_INFO_EX {
        Flags: FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
    };
    // Delete the verified object, never whichever object now occupies `path`.
    if unsafe {
        SetFileInformationByHandle(
            handle,
            FileDispositionInfoEx,
            (&info as *const FILE_DISPOSITION_INFO_EX).cast(),
            std::mem::size_of_val(&info) as u32,
        )
    } != 0
    {
        return Ok(());
    }
    let extended_error = std::io::Error::last_os_error();
    if !matches!(extended_error.raw_os_error(), Some(1 | 50 | 87)) {
        return Err(format!(
            "Failed to delete verified source [{}]: {extended_error}",
            path.display()
        ));
    }
    // Older filesystems/SMB servers need the original disposition class.
    // Read-only changes also use this same handle and are restored on failure.
    let mut original: FILE_BASIC_INFO = unsafe { std::mem::zeroed() };
    let got_basic = unsafe {
        GetFileInformationByHandleEx(
            handle,
            FileBasicInfo,
            (&mut original as *mut FILE_BASIC_INFO).cast(),
            std::mem::size_of_val(&original) as u32,
        )
    } != 0;
    let readonly = got_basic && original.FileAttributes & FILE_ATTRIBUTE_READONLY != 0;
    if readonly {
        let mut writable = original;
        writable.FileAttributes &= !FILE_ATTRIBUTE_READONLY;
        if writable.FileAttributes == 0 {
            writable.FileAttributes = FILE_ATTRIBUTE_NORMAL;
        }
        if unsafe {
            SetFileInformationByHandle(
                handle,
                FileBasicInfo,
                (&writable as *const FILE_BASIC_INFO).cast(),
                std::mem::size_of_val(&writable) as u32,
            )
        } == 0
        {
            return Err(format!(
                "Failed to clear verified source read-only attribute [{}]: {}",
                path.display(),
                std::io::Error::last_os_error()
            ));
        }
    }
    let info = FILE_DISPOSITION_INFO { DeleteFile: true };
    if unsafe {
        SetFileInformationByHandle(
            handle,
            FileDispositionInfo,
            (&info as *const FILE_DISPOSITION_INFO).cast(),
            std::mem::size_of_val(&info) as u32,
        )
    } != 0
    {
        return Ok(());
    }
    let error = std::io::Error::last_os_error();
    if readonly {
        unsafe {
            SetFileInformationByHandle(
                handle,
                FileBasicInfo,
                (&original as *const FILE_BASIC_INFO).cast(),
                std::mem::size_of_val(&original) as u32,
            );
        }
    }
    Err(format!(
        "Failed to delete verified source [{}]: {error}",
        path.display()
    ))
}

pub(crate) fn delete_verified_source(path: &Path, expected: &SourceVersion) -> Result<(), String> {
    let lstat = fs::symlink_metadata(path)
        .map_err(|e| format!("Failed to stat source [{}]: {e}", path.display()))?;
    if !metadata_matches_source_version(&lstat, expected) {
        return Err(format!(
            "Source changed before move completion and was retained: {}",
            path.display()
        ));
    }

    #[cfg(not(windows))]
    if expected.is_symlink {
        if lstat.is_dir() {
            return fs::remove_dir(path)
                .map_err(|e| format!("Failed to delete source symlink [{}]: {e}", path.display()));
        }
        return fs::remove_file(path)
            .map_err(|e| format!("Failed to delete source symlink [{}]: {e}", path.display()));
    }

    #[cfg(not(windows))]
    if expected.is_dir {
        return fs::remove_dir(path).map_err(|e| {
            format!(
                "Source directory retained because new or uncopied items remain in {}: {e}",
                path.display()
            )
        });
    }

    #[cfg(windows)]
    let mut file = open_source_handle(path, true)?;
    #[cfg(not(windows))]
    let mut file = open_source_for_verification(path)?;
    let file_meta = file
        .metadata()
        .map_err(|e| format!("Failed to stat source handle [{}]: {e}", path.display()))?;
    if !metadata_matches_source_version(&file_meta, expected) {
        return Err(format!(
            "Source changed before move completion and was retained: {}",
            path.display()
        ));
    }
    #[cfg(windows)]
    {
        if expected.file_id.is_none() || expected.file_id != file_id_from_handle(&file) {
            return Err(format!(
                "Source identity changed before move completion and was retained: {}",
                path.display()
            ));
        }
    }
    if let Some(expected_hash) = expected.content_hash {
        let actual_hash = hash_file_contents(&mut file)?;
        if actual_hash != expected_hash {
            return Err(format!(
                "Source content changed before move completion and was retained: {}",
                path.display()
            ));
        }
    }

    #[cfg(windows)]
    {
        delete_open_source(&file, path)
    }
    #[cfg(not(windows))]
    {
        fs::remove_file(path)
            .map_err(|e| format!("Failed to delete source [{}]: {e}", path.display()))
    }
}

/// `failed_sources` are files or directories reported as failed for this
/// operation; a source directory that stays because one of them lives in it is
/// not a second error.
pub(crate) fn delete_verified_source_tree(
    root: &Path,
    manifest: &TreeCopyManifest,
    failed_sources: &[PathBuf],
) -> Result<(), String> {
    let mut errors = Vec::new();
    manifest.files.visit(|file_path, expected| {
        if let Err(err) = delete_verified_source(file_path, expected) {
            errors.push(err);
        }
        Ok(())
    })?;

    if !manifest.source_dirs.iter().any(|(path, _)| path == root) {
        return Err(
            "Move completion manifest is missing the source root; directory retained".into(),
        );
    }
    for (dir_path, version) in manifest.source_dirs.iter().rev() {
        if let Err(error) = delete_verified_source(dir_path, version) {
            let explained = failed_sources
                .iter()
                .any(|failed| failed.starts_with(dir_path));
            if explained {
                continue;
            }
            errors.push(format!(
                "Source directory retained [{}]: {error}",
                dir_path.display()
            ));
        }
    }

    if errors.is_empty() {
        Ok(())
    } else {
        Err(errors.join("; "))
    }
}

pub(crate) fn conflict_for_existing_destination(path: &Path) -> String {
    format!(
        "CONFLICT: destination already exists: {}",
        path.to_string_lossy()
    )
}

#[cfg(windows)]
#[allow(clippy::permissions_set_readonly_false)]
fn clear_readonly_for_delete(path: &Path) {
    fn clear_one(target: &Path) {
        let Ok(meta) = fs::symlink_metadata(target) else {
            return;
        };
        if meta.file_type().is_symlink() {
            return;
        }
        let mut perms = meta.permissions();
        if perms.readonly() {
            perms.set_readonly(false);
            let _ = fs::set_permissions(target, perms);
        }
    }

    let Ok(meta) = fs::symlink_metadata(path) else {
        return;
    };
    if meta.is_dir() && !meta.file_type().is_symlink() {
        if let Ok(entries) = fs::read_dir(path) {
            for entry in entries.flatten() {
                let child = entry.path();
                if fs::symlink_metadata(&child)
                    .is_ok_and(|m| m.is_dir() && !m.file_type().is_symlink())
                {
                    clear_readonly_for_delete(&child);
                } else {
                    clear_one(&child);
                }
            }
        }
    }
    clear_one(path);
}

#[cfg(not(windows))]
fn clear_readonly_for_delete(_path: &Path) {}

pub(crate) fn remove_path(path: &Path, label: &str) -> Result<(), String> {
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
        match fs::remove_dir_all(path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
                clear_readonly_for_delete(path);
                fs::remove_dir_all(path)
                    .map_err(|retry| format!("Failed to delete {label} directory: {retry}"))
            }
            Err(error) => Err(format!("Failed to delete {label} directory: {error}")),
        }
    } else {
        match fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => {
                clear_readonly_for_delete(path);
                fs::remove_file(path)
                    .map_err(|retry| format!("Failed to delete {label} file: {retry}"))
            }
            Err(error) => Err(format!("Failed to delete {label} file: {error}")),
        }
    }
}

/// Workers must already be joined. Windows directory watchers can briefly keep
/// deletion pending; finish bounded cleanup even when the job was cancelled.
pub(crate) fn cleanup_owned_path(path: &Path) -> Result<(), String> {
    let mut last = String::new();
    for attempt in 0..5 {
        match fs::symlink_metadata(path) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(error) => last = error.to_string(),
            Ok(_) => {
                if let Err(error) = remove_path(path, "operation temporary path") {
                    last = error;
                }
                if matches!(fs::symlink_metadata(path), Err(error) if error.kind() == std::io::ErrorKind::NotFound)
                {
                    return Ok(());
                }
            }
        }
        if attempt < 4 {
            std::thread::sleep(std::time::Duration::from_millis(25 << attempt));
        }
    }
    Err(format!(
        "Temporary path remains [{}]: {last}",
        path.display()
    ))
}

pub(crate) fn staging_path_for(final_path: &Path, operation_id: &str) -> Result<PathBuf, String> {
    staging_path_with_salt(final_path, Some(operation_id))
}

#[cfg(test)]
pub(crate) fn resumable_staging_path_for(
    final_path: &Path,
    operation_id: &str,
) -> Result<PathBuf, String> {
    staging_path_with_salt(final_path, Some(operation_id))
}

fn staging_path_with_salt(final_path: &Path, salt: Option<&str>) -> Result<PathBuf, String> {
    let parent = final_path
        .parent()
        .ok_or_else(|| "Cannot get destination directory".to_string())?;
    let file_name = final_path
        .file_name()
        .ok_or_else(|| "Cannot get destination file name".to_string())?
        .to_string_lossy();
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    final_path.to_string_lossy().hash(&mut hasher);
    if let Some(salt) = salt {
        salt.hash(&mut hasher);
    }
    let key = hasher.finish();

    Ok(parent.join(format!(".{file_name}.{key:016x}.sumafile-partial")))
}

#[cfg(test)]
pub(crate) fn promote_staged_path(
    staged: &Path,
    destination: &Path,
    replace: bool,
) -> Result<(), String> {
    promote_attempt(staged, destination, replace).map_err(String::from)
}

#[cfg(test)]
fn promote_attempt(
    staged_path: &Path,
    final_path: &Path,
    replace_existing: bool,
) -> Result<(), Failure> {
    promote_attempt_leased(staged_path, final_path, replace_existing, None)
}

pub(crate) fn promote_leased_with_policy(
    staged: &Path,
    destination: &Path,
    replace: bool,
    cancel: &std::sync::atomic::AtomicBool,
    policy: &crate::progress::Policy,
    lease: &mut crate::transfer_journal::StagingLease,
    mut waiting: impl FnMut(std::time::Duration, &str),
) -> Result<(), String> {
    let mut retry = Retry::from_policy(policy);
    loop {
        crate::transfer_reliability::wait(std::time::Duration::ZERO, cancel)?;
        if !lease.owns_staged() {
            return Err("Transfer staging identity changed; artifact retained".into());
        }
        match promote_attempt_leased(staged, destination, replace, Some(lease)) {
            Ok(()) => return Ok(()),
            Err(error) => {
                let reason = error.to_string();
                let delay = retry.failed(error)?;
                waiting(delay, &reason);
                crate::transfer_reliability::wait(delay, cancel)?;
            }
        }
    }
}

fn promote_attempt_leased(
    staged_path: &Path,
    final_path: &Path,
    replace_existing: bool,
    lease: Option<&mut crate::transfer_journal::StagingLease>,
) -> Result<(), Failure> {
    if !replace_existing {
        // The exclusive rename reports an existing destination itself; no
        // separate existence probe is needed.
        return rename_exclusive(staged_path, final_path).map_err(|e| {
            if is_already_exists_error(&e) || path_exists_no_follow(final_path) {
                conflict_for_existing_destination(final_path).into()
            } else {
                Failure::io("Finalize destination", final_path, e)
            }
        });
    }

    match rename_exclusive(staged_path, final_path) {
        Ok(()) => return Ok(()),
        Err(e) if is_already_exists_error(&e) || path_exists_no_follow(final_path) => {}
        Err(e) => return Err(Failure::io("Finalize destination", final_path, e)),
    }

    let backup_path = move_existing_to_backup_leased(final_path, lease)?;
    if let Err(error) = rename_exclusive(staged_path, final_path) {
        return Err(match rename_exclusive(&backup_path, final_path) {
            Ok(()) => Failure::io(
                "Finalize destination (original destination was restored)",
                final_path,
                error,
            ),
            Err(restore) => Failure::new(
                format!(
                    "Finalize destination [{}]: {error}; original remains [{}], restore failed: {restore}",
                    final_path.display(),
                    backup_path.display()
                ),
                false,
            ),
        });
    }
    // Promotion already committed: never replay it just because backup cleanup failed.
    remove_path(&backup_path, "destination backup").map_err(|e| {
        Failure::new(
            format!(
                "Transferred, but backup remains [{}]: {e}",
                backup_path.display()
            ),
            false,
        )
    })?;
    Ok(())
}

fn move_existing_to_backup_leased(
    final_path: &Path,
    mut lease: Option<&mut crate::transfer_journal::StagingLease>,
) -> Result<PathBuf, Failure> {
    let parent = final_path
        .parent()
        .ok_or_else(|| Failure::new("Cannot get destination directory", false))?;
    let file_name = final_path
        .file_name()
        .ok_or_else(|| Failure::new("Cannot get destination file name", false))?
        .to_string_lossy();
    let process_id = std::process::id();
    for index in 0..10_000u32 {
        let candidate = parent.join(format!(
            ".{file_name}.{process_id}.{index}.sumafile-replace-backup"
        ));
        if path_exists_no_follow(&candidate) {
            continue;
        }
        if let Some(owner) = lease.as_deref_mut() {
            owner.backup_intent(&candidate, final_path)?;
        }
        match rename_exclusive(final_path, &candidate) {
            Ok(()) => return Ok(candidate),
            Err(e) if is_already_exists_error(&e) => continue,
            Err(e) => {
                return Err(Failure::io(
                    "Prepare destination replacement",
                    final_path,
                    e,
                ))
            }
        }
    }

    Err(Failure::new(
        format!(
            "Could not choose replacement backup path for {}",
            final_path.to_string_lossy()
        ),
        false,
    ))
}

#[cfg(test)]
pub(crate) fn existing_file_matches_source(src: &Path, dst: &Path) -> Result<bool, String> {
    existing_file_matches_source_with_mode(src, dst, crate::progress::CheckMode::Checksum)
}

/// Whether an existing destination already holds the source, by the selected
/// integrity mode: `SizeOnly` compares sizes; `SizeAndModTime` sizes and
/// modification times; `IgnoreTimes` and `Checksum` compare the bytes.
pub(crate) fn existing_file_matches_source_with_mode(
    src: &Path,
    dst: &Path,
    check_mode: crate::progress::CheckMode,
) -> Result<bool, String> {
    use crate::progress::CheckMode;

    let source = fs::symlink_metadata(src)
        .map_err(|error| format!("Failed to stat source file: {error}"))?;
    let destination = fs::symlink_metadata(dst)
        .map_err(|error| format!("Failed to stat resumable destination file: {error}"))?;
    if !source.file_type().is_file()
        || !destination.file_type().is_file()
        || source.len() != destination.len()
    {
        return Ok(false);
    }
    match check_mode {
        CheckMode::SizeOnly => Ok(true),
        CheckMode::SizeAndModTime => Ok(modified_timestamp(&source).is_some()
            && modified_timestamp(&source) == modified_timestamp(&destination)),
        CheckMode::IgnoreTimes | CheckMode::Checksum => files_have_same_contents(src, dst),
    }
}

fn files_have_same_contents(left: &Path, right: &Path) -> Result<bool, String> {
    let mut left =
        fs::File::open(left).map_err(|error| format!("Failed to open source file: {error}"))?;
    let mut right = fs::File::open(right)
        .map_err(|error| format!("Failed to open resumable destination file: {error}"))?;
    let mut left_buffer = crate::progress::PooledBuffer::acquire(VERIFY_CHUNK);
    let mut right_buffer = crate::progress::PooledBuffer::acquire(VERIFY_CHUNK);

    loop {
        let left_read = read_fully(&mut left, left_buffer.as_mut_slice())
            .map_err(|error| format!("Failed to read source file: {error}"))?;
        let right_read = read_fully(&mut right, right_buffer.as_mut_slice())
            .map_err(|error| format!("Failed to read resumable destination file: {error}"))?;
        if left_read != right_read {
            return Ok(false);
        }
        if left_read == 0 {
            return Ok(true);
        }
        if left_buffer[..left_read] != right_buffer[..right_read] {
            return Ok(false);
        }
    }
}

/// Fill `buffer` as far as the file allows; short reads from network
/// filesystems must not be mistaken for a length difference.
pub(crate) fn read_fully(file: &mut fs::File, buffer: &mut [u8]) -> std::io::Result<usize> {
    let mut filled = 0;
    while filled < buffer.len() {
        match file.read(&mut buffer[filled..]) {
            Ok(0) => break,
            Ok(n) => filled += n,
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        }
    }
    Ok(filled)
}

#[cfg(test)]
mod tests {
    use super::{
        capture_source_version_for_path, capture_source_version_for_path_hashed,
        delete_verified_source, delete_verified_source_tree, existing_file_matches_source,
        promote_staged_path, resumable_staging_path_for, TreeCopyManifest,
    };
    use std::fs;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn unique_temp_dir(name: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let dir =
            std::env::temp_dir().join(format!("simplefile_service_staging_test_{name}_{nanos}"));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn sha256_is_independent_of_short_read_boundaries() {
        let mut abc = super::ContentHasher::new();
        abc.update(b"a");
        abc.update(b"bc");
        assert_eq!(
            abc.finish(),
            [
                0xba, 0x78, 0x16, 0xbf, 0x8f, 0x01, 0xcf, 0xea, 0x41, 0x41, 0x40, 0xde, 0x5d, 0xae,
                0x22, 0x23, 0xb0, 0x03, 0x61, 0xa3, 0x96, 0x17, 0x7a, 0x9c, 0xb4, 0x10, 0xff, 0x61,
                0xf2, 0x00, 0x15, 0xad,
            ]
        );
        let bytes: Vec<u8> = (0..65539).map(|i| (i % 251) as u8).collect();
        let mut complete = super::ContentHasher::new();
        complete.update(&bytes);
        let expected = complete.finish();
        for size in [1, 3, 4, 7, 8, 9, 63, 64, 65536] {
            let mut partitioned = super::ContentHasher::new();
            for chunk in bytes.chunks(size) {
                partitioned.update(chunk);
            }
            assert_eq!(partitioned.finish(), expected, "chunk size {size}");
        }
    }

    #[test]
    fn completion_manifest_preserves_pre_epoch_source_versions() {
        let root = unique_temp_dir("pre_epoch_manifest");
        let source = root.join("old.txt");
        fs::write(&source, b"old content").unwrap();
        let file = fs::OpenOptions::new().write(true).open(&source).unwrap();
        let modified = UNIX_EPOCH - std::time::Duration::from_secs(1);
        file.set_times(fs::FileTimes::new().set_modified(modified))
            .unwrap();
        drop(file);
        let expected = capture_source_version_for_path(&source).unwrap();
        assert_eq!(expected.modified, Some(-1_000_000_000));
        let manifest = crate::transfer_manifest::DiskManifest::new().unwrap();
        manifest.push(&source, &expected).unwrap();
        let mut visited = 0;
        manifest
            .visit(|path, version| {
                assert_eq!(path, source.as_path());
                assert_eq!(version, &expected);
                visited += 1;
                super::verify_source_unchanged(path, version)
            })
            .unwrap();
        assert_eq!(visited, 1);
        drop(manifest);
        fs::remove_dir_all(root).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn deletion_uses_verified_handle_after_path_replacement_and_handles_readonly() {
        for readonly in [false, true] {
            let root = unique_temp_dir("source_handle_delete");
            let source = root.join("source");
            let moved = root.join("renamed-source");
            fs::write(&source, b"copied").unwrap();
            if readonly {
                let mut permissions = fs::metadata(&source).unwrap().permissions();
                permissions.set_readonly(true);
                fs::set_permissions(&source, permissions).unwrap();
            }
            let handle = super::open_source_handle(&source, true).unwrap();
            fs::rename(&source, &moved).unwrap();
            fs::write(&source, b"replacement-must-survive").unwrap();
            super::delete_open_source(&handle, &source).unwrap();
            drop(handle);
            assert!(!moved.exists());
            assert_eq!(fs::read(&source).unwrap(), b"replacement-must-survive");
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn promote_replace_restores_original_when_staged_rename_fails() {
        let root = unique_temp_dir("restore_original");
        let destination = root.join("item.txt");
        let staged = root.join("missing-staged.txt");
        fs::write(&destination, b"original").unwrap();

        let error = promote_staged_path(&staged, &destination, true)
            .expect_err("missing staged file should fail promotion");

        assert!(error.contains("original destination was restored"));
        assert_eq!(fs::read(&destination).unwrap(), b"original");
        assert_eq!(
            fs::read_dir(&root).unwrap().count(),
            1,
            "backup should not be left behind after successful restore"
        );

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn promote_no_replace_preserves_destination_created_before_promotion() {
        let root = unique_temp_dir("no_replace_atomic");
        let destination = root.join("item.txt");
        let staged = root.join(".item.txt.partial");
        fs::write(&staged, b"staged-replacement").unwrap();
        // Destination created immediately before promotion
        fs::write(&destination, b"concurrently-created-destination").unwrap();

        let error = promote_staged_path(&staged, &destination, false)
            .expect_err("existing destination must not be overwritten when replace is false");

        assert!(
            error.starts_with("CONFLICT: destination already exists:"),
            "unexpected error: {error}"
        );
        assert_eq!(
            fs::read(&destination).unwrap(),
            b"concurrently-created-destination"
        );
        assert_eq!(fs::read(&staged).unwrap(), b"staged-replacement");

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn delete_verified_source_retains_modified_file() {
        let root = unique_temp_dir("verified_source_modified");
        let source = root.join("source.txt");
        fs::write(&source, b"v1").unwrap();
        let snapshot = capture_source_version_for_path_hashed(&source).unwrap();

        // Modify file with same length so even size-preserving edits are caught
        fs::write(&source, b"v2").unwrap();

        let err = delete_verified_source(&source, &snapshot)
            .expect_err("modified source must not be deleted");
        assert!(err.contains("retained"), "unexpected error: {err}");
        assert_eq!(fs::read(&source).unwrap(), b"v2");

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn delete_verified_source_tree_retains_newly_added_file() {
        let root = unique_temp_dir("verified_tree_added_file");
        let src_dir = root.join("folder");
        let nested_dir = src_dir.join("sub");
        fs::create_dir_all(&nested_dir).unwrap();
        let copied_file = nested_dir.join("copied.txt");
        fs::write(&copied_file, b"copied").unwrap();

        let manifest = TreeCopyManifest {
            files: vec![(
                copied_file.clone(),
                capture_source_version_for_path(&copied_file).unwrap(),
            )]
            .into(),
            source_dirs: vec![
                (
                    src_dir.clone(),
                    capture_source_version_for_path(&src_dir).unwrap(),
                ),
                (
                    nested_dir.clone(),
                    capture_source_version_for_path(&nested_dir).unwrap(),
                ),
            ],
        };

        // Another application adds a new file after folder enumeration
        let new_file = nested_dir.join("uncopied.txt");
        fs::write(&new_file, b"must-not-be-lost").unwrap();

        let err = delete_verified_source_tree(&src_dir, &manifest, &[])
            .expect_err("tree with new uncopied file must report partial move");
        assert!(err.contains("retained"), "unexpected error: {err}");
        assert!(!copied_file.exists(), "verified copied file is deleted");
        assert!(new_file.exists(), "new uncopied file must be retained");
        assert_eq!(fs::read(&new_file).unwrap(), b"must-not-be-lost");

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn resumable_staging_path_is_operation_owned() {
        let root = unique_temp_dir("operation_owned");
        let destination = root.join("Album");

        let first = resumable_staging_path_for(&destination, "op-one").unwrap();
        let second = resumable_staging_path_for(&destination, "op-two").unwrap();

        assert_ne!(first, second);
        assert_eq!(first.parent(), Some(root.as_path()));
        assert_eq!(second.parent(), Some(root.as_path()));

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn existing_file_matches_source_rejects_same_size_different_bytes() {
        let root = unique_temp_dir("content_identity");
        let source = root.join("source.txt");
        let partial = root.join("partial.txt");
        fs::write(&source, b"NEW").unwrap();
        fs::write(&partial, b"OLD").unwrap();

        assert!(!existing_file_matches_source(&source, &partial).unwrap());

        fs::write(&partial, b"NEW").unwrap();
        assert!(existing_file_matches_source(&source, &partial).unwrap());

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn existing_file_check_modes_do_what_their_names_say() {
        use crate::progress::CheckMode;
        let root = unique_temp_dir("check_modes");
        let source = root.join("source.txt");
        let other = root.join("other.txt");
        fs::write(&source, b"NEW").unwrap();
        fs::write(&other, b"OLD").unwrap();
        let stamp =
            std::time::SystemTime::UNIX_EPOCH + std::time::Duration::from_secs(1_700_000_000);
        for path in [&source, &other] {
            let file = fs::OpenOptions::new().write(true).open(path).unwrap();
            file.set_times(fs::FileTimes::new().set_modified(stamp))
                .unwrap();
        }
        // Same size and time, different bytes.
        assert!(super::existing_file_matches_source_with_mode(
            &source,
            &other,
            CheckMode::SizeOnly
        )
        .unwrap());
        assert!(super::existing_file_matches_source_with_mode(
            &source,
            &other,
            CheckMode::SizeAndModTime
        )
        .unwrap());
        assert!(!super::existing_file_matches_source_with_mode(
            &source,
            &other,
            CheckMode::IgnoreTimes
        )
        .unwrap());
        assert!(!super::existing_file_matches_source_with_mode(
            &source,
            &other,
            CheckMode::Checksum
        )
        .unwrap());
        // Same bytes, different time.
        fs::write(&other, b"NEW").unwrap();
        assert!(!super::existing_file_matches_source_with_mode(
            &source,
            &other,
            CheckMode::SizeAndModTime
        )
        .unwrap());
        assert!(super::existing_file_matches_source_with_mode(
            &source,
            &other,
            CheckMode::IgnoreTimes
        )
        .unwrap());
        let _ = fs::remove_dir_all(root);
    }
}
