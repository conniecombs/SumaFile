use crate::transfer_reliability::{Failure, Retry};
use simplefile_core::path_conflict::{
    is_already_exists_error, path_exists_no_follow, rename_exclusive,
};
use std::fs;
use std::hash::{Hash, Hasher};
use std::io::{BufReader, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0100_0000_01b3;

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct SourceVersion {
    pub(crate) is_symlink: bool,
    pub(crate) is_dir: bool,
    pub(crate) len: u64,
    pub(crate) modified: Option<SystemTime>,
    #[cfg(windows)]
    pub(crate) creation_time: u64,
    #[cfg(windows)]
    pub(crate) last_write_time: u64,
    #[cfg(windows)]
    pub(crate) file_id: Option<(u32, u64)>,
    pub(crate) content_hash: Option<u64>,
}

#[derive(Debug, Default, Clone)]
pub(crate) struct TreeCopyManifest {
    pub(crate) files: Vec<(PathBuf, SourceVersion)>,
    pub(crate) source_dirs: Vec<PathBuf>,
}

#[derive(Debug, Clone, Copy)]
pub(crate) struct ContentHasher {
    state: u64,
}

impl Default for ContentHasher {
    fn default() -> Self {
        Self::new()
    }
}

impl ContentHasher {
    pub(crate) fn new() -> Self {
        Self {
            state: FNV_OFFSET_BASIS,
        }
    }

    pub(crate) fn update(&mut self, bytes: &[u8]) {
        let mut chunks = bytes.chunks_exact(8);
        for chunk in &mut chunks {
            let word = u64::from_le_bytes([
                chunk[0], chunk[1], chunk[2], chunk[3], chunk[4], chunk[5], chunk[6], chunk[7],
            ]);
            self.state = (self.state ^ word).wrapping_mul(FNV_PRIME);
        }
        for &b in chunks.remainder() {
            self.state ^= b as u64;
            self.state = self.state.wrapping_mul(FNV_PRIME);
        }
    }

    pub(crate) fn finish(self) -> u64 {
        self.state
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

pub(crate) fn hash_file_contents(file: &mut fs::File) -> Result<u64, String> {
    file.seek(SeekFrom::Start(0))
        .map_err(|e| format!("Failed to seek source file for hashing: {e}"))?;
    let mut reader = BufReader::with_capacity(64 * 1024, file);
    let mut buffer = [0u8; 64 * 1024];
    let mut hasher = ContentHasher::new();
    loop {
        let read = reader
            .read(&mut buffer)
            .map_err(|e| format!("Failed to read source file for hashing: {e}"))?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hasher.finish())
}

pub(crate) fn capture_source_version_from_open_file(
    file: &fs::File,
    meta: &fs::Metadata,
    content_hash: Option<u64>,
) -> SourceVersion {
    #[cfg(windows)]
    use std::os::windows::fs::MetadataExt;

    SourceVersion {
        is_symlink: meta.file_type().is_symlink(),
        is_dir: meta.is_dir(),
        len: meta.len(),
        modified: meta.modified().ok(),
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
            modified: meta.modified().ok(),
            #[cfg(windows)]
            creation_time: meta.creation_time(),
            #[cfg(windows)]
            last_write_time: meta.last_write_time(),
            #[cfg(windows)]
            file_id: None,
            content_hash: None,
        });
    }

    let mut file = fs::File::open(path)
        .map_err(|e| format!("Failed to open source [{}]: {e}", path.display()))?;
    let file_meta = file
        .metadata()
        .map_err(|e| format!("Failed to stat opened source [{}]: {e}", path.display()))?;
    let content_hash = Some(hash_file_contents(&mut file)?);
    Ok(capture_source_version_from_open_file(
        &file,
        &file_meta,
        content_hash,
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
    if meta.modified().ok() != expected.modified {
        return false;
    }
    #[cfg(windows)]
    {
        if meta.creation_time() != expected.creation_time
            || meta.last_write_time() != expected.last_write_time
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
    if let (Some(expected_id), Some(actual_id)) = (expected.file_id, file_id_from_handle(&file)) {
        if expected_id != actual_id {
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
fn open_source_for_verification(path: &Path) -> Result<fs::File, String> {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{FILE_SHARE_DELETE, FILE_SHARE_READ};

    fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_DELETE)
        .open(path)
        .map_err(|e| {
            format!(
                "Failed to lock source for move verification [{}]: {e}",
                path.display()
            )
        })
}

#[cfg(not(windows))]
fn open_source_for_verification(path: &Path) -> Result<fs::File, String> {
    fs::File::open(path).map_err(|e| {
        format!(
            "Failed to open source for move verification [{}]: {e}",
            path.display()
        )
    })
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

    if expected.is_symlink {
        if lstat.is_dir() {
            return fs::remove_dir(path)
                .map_err(|e| format!("Failed to delete source symlink [{}]: {e}", path.display()));
        }
        return fs::remove_file(path)
            .map_err(|e| format!("Failed to delete source symlink [{}]: {e}", path.display()));
    }

    if expected.is_dir {
        return fs::remove_dir(path).map_err(|e| {
            format!(
                "Source directory retained because new or uncopied items remain in {}: {e}",
                path.display()
            )
        });
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
    if let (Some(expected_id), Some(actual_id)) = (expected.file_id, file_id_from_handle(&file)) {
        if expected_id != actual_id {
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

    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(first_err) => {
            drop(file);
            if first_err.kind() == std::io::ErrorKind::PermissionDenied {
                let recheck = fs::symlink_metadata(path)
                    .map_err(|e| format!("Failed to stat source [{}]: {e}", path.display()))?;
                if !metadata_matches_source_version(&recheck, expected) {
                    return Err(format!(
                        "Source changed before move completion and was retained: {}",
                        path.display()
                    ));
                }
                #[cfg(windows)]
                #[allow(clippy::permissions_set_readonly_false)]
                {
                    let mut perms = recheck.permissions();
                    if perms.readonly() {
                        perms.set_readonly(false);
                        let _ = fs::set_permissions(path, perms);
                    }
                }
                return fs::remove_file(path).map_err(|e| {
                    format!("Failed to delete source file [{}]: {e}", path.display())
                });
            }
            Err(format!(
                "Failed to delete source file [{}]: {first_err}",
                path.display()
            ))
        }
    }
}

pub(crate) fn delete_verified_source_tree(
    root: &Path,
    manifest: &TreeCopyManifest,
) -> Result<(), String> {
    let mut errors = Vec::new();
    for (file_path, expected) in &manifest.files {
        if let Err(err) = delete_verified_source(file_path, expected) {
            errors.push(err);
        }
    }

    let mut dirs_to_remove: Vec<&Path> = manifest
        .source_dirs
        .iter()
        .rev()
        .map(PathBuf::as_path)
        .collect();
    if !dirs_to_remove.iter().any(|&d| d == root) {
        dirs_to_remove.push(root);
    }

    for dir_path in dirs_to_remove {
        match fs::remove_dir(dir_path) {
            Ok(()) => {}
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => {}
            Err(err) => {
                #[cfg(windows)]
                #[allow(clippy::permissions_set_readonly_false)]
                if err.kind() == std::io::ErrorKind::PermissionDenied {
                    if let Ok(meta) = fs::symlink_metadata(dir_path) {
                        let mut perms = meta.permissions();
                        if perms.readonly() {
                            perms.set_readonly(false);
                            let _ = fs::set_permissions(dir_path, perms);
                            if fs::remove_dir(dir_path).is_ok() {
                                continue;
                            }
                        }
                    }
                }
                if errors.is_empty() {
                    errors.push(format!(
                        "Source directory retained because new or uncopied items remain in {} ({err})",
                        dir_path.display()
                    ));
                }
            }
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

#[allow(dead_code)]
pub(crate) fn resumable_staging_path_for(
    final_path: &Path,
    operation_id: &str,
) -> Result<PathBuf, String> {
    staging_path_with_salt(final_path, Some(operation_id))
}

fn is_matching_staging_name(entry_name: &str, target_file_name: &str) -> bool {
    const SUFFIX: &str = ".sumafile-partial";
    let Some(rest) = entry_name
        .strip_prefix('.')
        .and_then(|s| s.strip_suffix(SUFFIX))
    else {
        return false;
    };
    let Some(after_target) = rest.strip_prefix(target_file_name) else {
        return false;
    };
    let Some(hex_key) = after_target.strip_prefix('.') else {
        return false;
    };
    hex_key.len() == 16 && hex_key.bytes().all(|b| b.is_ascii_hexdigit())
}

/// Adopts a legacy `.{folder}.{hash}.sumafile-partial` staging directory into
/// `final_dest` if `final_dest` does not yet exist, and cleans up any remaining
/// duplicate `.sumafile-partial` directories for the same target folder.
pub(crate) fn adopt_or_cleanup_legacy_dir_staging(
    final_dest: &Path,
    operation_id: &str,
) -> bool {
    let Some(parent) = final_dest.parent() else {
        return false;
    };
    let Some(file_name) = final_dest.file_name().and_then(|n| n.to_str()) else {
        return false;
    };

    let exact_match = resumable_staging_path_for(final_dest, operation_id).ok();
    let mut candidates: Vec<(PathBuf, Option<SystemTime>)> = Vec::new();

    if let Ok(entries) = fs::read_dir(parent) {
        for entry in entries.flatten() {
            let name = entry.file_name();
            let Some(name_str) = name.to_str() else {
                continue;
            };
            if !is_matching_staging_name(name_str, file_name) {
                continue;
            }
            let path = entry.path();
            if let Ok(meta) = fs::symlink_metadata(&path) {
                if meta.file_type().is_dir() {
                    candidates.push((path, meta.modified().ok()));
                }
            }
        }
    }

    if candidates.is_empty() {
        return false;
    }

    candidates.sort_by(|a, b| {
        let a_exact = exact_match.as_ref().is_some_and(|ex| ex == &a.0);
        let b_exact = exact_match.as_ref().is_some_and(|ex| ex == &b.0);
        b_exact.cmp(&a_exact).then_with(|| b.1.cmp(&a.1))
    });

    let mut adopted = false;
    for (candidate, _) in candidates {
        if !adopted && !path_exists_no_follow(final_dest) {
            if rename_exclusive(&candidate, final_dest).is_ok() {
                adopted = true;
                continue;
            }
        }
        let _ = cleanup_owned_path(&candidate);
    }
    adopted
}

/// Cleans up any stale `.{file}.{hash}.sumafile-partial` files in `final_dest.parent()`
/// left behind by previous interrupted operations with different operation IDs.
pub(crate) fn cleanup_stale_file_staging(final_dest: &Path, current_staged: &Path) {
    let Some(parent) = final_dest.parent() else {
        return;
    };
    let Some(file_name) = final_dest.file_name().and_then(|n| n.to_str()) else {
        return;
    };
    let Ok(entries) = fs::read_dir(parent) else {
        return;
    };
    for entry in entries.flatten() {
        let name = entry.file_name();
        let Some(name_str) = name.to_str() else {
            continue;
        };
        if !is_matching_staging_name(name_str, file_name) {
            continue;
        }
        let path = entry.path();
        if path == current_staged {
            continue;
        }
        if fs::symlink_metadata(&path).is_ok_and(|m| m.file_type().is_file()) {
            let _ = cleanup_owned_path(&path);
        }
    }
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

#[cfg(windows)]
fn clear_hidden_after_promote(staged_path: &Path, final_path: &Path) {
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::fs::MetadataExt;
    use windows_sys::Win32::Storage::FileSystem::{
        SetFileAttributesW, FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_NORMAL,
    };

    let is_staging = staged_path
        .file_name()
        .and_then(|n| n.to_str())
        .is_some_and(|n| n.ends_with(".sumafile-partial"));
    if !is_staging {
        return;
    }
    let Ok(meta) = fs::symlink_metadata(final_path) else {
        return;
    };
    if meta.file_type().is_symlink() || meta.is_dir() {
        return;
    }
    let attrs = meta.file_attributes();
    if attrs & FILE_ATTRIBUTE_HIDDEN == 0 {
        return;
    }
    let mut new_attrs = attrs & !FILE_ATTRIBUTE_HIDDEN;
    if new_attrs == 0 {
        new_attrs = FILE_ATTRIBUTE_NORMAL;
    }
    let wide: Vec<u16> = final_path
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    unsafe {
        let _ = SetFileAttributesW(wide.as_ptr(), new_attrs);
    }
}

#[cfg(not(windows))]
fn clear_hidden_after_promote(_staged_path: &Path, _final_path: &Path) {}

#[cfg(test)]
pub(crate) fn promote_staged_path(
    staged: &Path,
    destination: &Path,
    replace: bool,
) -> Result<(), String> {
    promote_attempt(staged, destination, replace).map_err(String::from)
}

pub(crate) fn promote_with_policy(
    staged: &Path,
    destination: &Path,
    replace: bool,
    cancel: &std::sync::atomic::AtomicBool,
    policy: &crate::progress::Policy,
    waiting: impl FnMut(std::time::Duration, &str),
) -> Result<(), String> {
    promote_with_retry(
        staged,
        destination,
        replace,
        cancel,
        Retry::from_policy(policy),
        waiting,
    )
}

fn promote_with_retry(
    staged: &Path,
    destination: &Path,
    replace: bool,
    cancel: &std::sync::atomic::AtomicBool,
    mut retry: Retry,
    mut waiting: impl FnMut(std::time::Duration, &str),
) -> Result<(), String> {
    loop {
        crate::transfer_reliability::wait(std::time::Duration::ZERO, cancel)?;
        match promote_attempt(staged, destination, replace) {
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

fn promote_attempt(
    staged_path: &Path,
    final_path: &Path,
    replace_existing: bool,
) -> Result<(), Failure> {
    if !replace_existing {
        if path_exists_no_follow(final_path) {
            return Err(conflict_for_existing_destination(final_path).into());
        }
        return rename_exclusive(staged_path, final_path)
            .map(|()| clear_hidden_after_promote(staged_path, final_path))
            .map_err(|e| {
                if is_already_exists_error(&e) || path_exists_no_follow(final_path) {
                    conflict_for_existing_destination(final_path).into()
                } else {
                    Failure::io("Finalize destination", final_path, e)
                }
            });
    }

    if !path_exists_no_follow(final_path) {
        match rename_exclusive(staged_path, final_path) {
            Ok(()) => {
                clear_hidden_after_promote(staged_path, final_path);
                return Ok(());
            }
            Err(e) if is_already_exists_error(&e) || path_exists_no_follow(final_path) => {}
            Err(e) => return Err(Failure::io("Finalize destination", final_path, e)),
        }
    }

    let backup_path = move_existing_to_backup(final_path)?;
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
    clear_hidden_after_promote(staged_path, final_path);
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

fn move_existing_to_backup(final_path: &Path) -> Result<PathBuf, Failure> {
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
        match rename_exclusive(final_path, &candidate) {
            Ok(()) => return Ok(candidate),
            Err(e) if is_already_exists_error(&e) => continue,
            Err(e) => return Err(Failure::io("Prepare destination replacement", final_path, e)),
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

pub(crate) fn backup_existing_destination(final_path: &Path) -> Result<PathBuf, String> {
    move_existing_to_backup(final_path).map_err(String::from)
}

pub(crate) fn restore_backed_up_destination(final_path: &Path, backup_path: &Path) {
    if path_exists_no_follow(final_path) {
        let _ = cleanup_owned_path(final_path);
    }
    if path_exists_no_follow(backup_path) {
        let _ = rename_exclusive(backup_path, final_path);
    }
}

#[cfg(test)]
pub(crate) fn existing_file_matches_source(src: &Path, dst: &Path) -> Result<bool, String> {
    existing_file_matches_source_with_mode(
        src,
        dst,
        crate::progress::CheckMode::SizeAndModTime,
    )
}

pub(crate) fn existing_file_matches_source_with_mode(
    src: &Path,
    dst: &Path,
    check_mode: crate::progress::CheckMode,
) -> Result<bool, String> {
    if check_mode == crate::progress::CheckMode::IgnoreTimes {
        return Ok(false);
    }
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
    if check_mode == crate::progress::CheckMode::SizeOnly {
        return Ok(true);
    }

    files_have_same_contents(src, dst)
}

fn files_have_same_contents(left: &Path, right: &Path) -> Result<bool, String> {
    let mut left = BufReader::new(
        fs::File::open(left).map_err(|error| format!("Failed to open source file: {error}"))?,
    );
    let mut right = BufReader::new(
        fs::File::open(right)
            .map_err(|error| format!("Failed to open resumable destination file: {error}"))?,
    );
    let mut left_buffer = [0u8; 64 * 1024];
    let mut right_buffer = [0u8; 64 * 1024];

    loop {
        let left_read = left
            .read(&mut left_buffer)
            .map_err(|error| format!("Failed to read source file: {error}"))?;
        let right_read = right
            .read(&mut right_buffer)
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

#[cfg(test)]
mod tests {
    use super::{
        capture_source_version_for_path, delete_verified_source, delete_verified_source_tree,
        existing_file_matches_source, promote_staged_path, resumable_staging_path_for,
        TreeCopyManifest,
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
        let snapshot = capture_source_version_for_path(&source).unwrap();

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
            )],
            source_dirs: vec![src_dir.clone(), nested_dir.clone()],
        };

        // Another application adds a new file after folder enumeration
        let new_file = nested_dir.join("uncopied.txt");
        fs::write(&new_file, b"must-not-be-lost").unwrap();

        let err = delete_verified_source_tree(&src_dir, &manifest)
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
}
