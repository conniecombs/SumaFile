//! Reusable file operation logic for the SumaFile backend.
//!
//! This module contains host-independent file system operations that are callable
//! from the service crate, tests, and future tools.

use crate::models::{FileEntry, TreeNode};
use crate::path_conflict::{path_collision_key, path_exists_no_follow, rename_exclusive};
use crate::utils::{
    get_file_entry, validate_existing_path_no_resolve, validate_name, validate_path_no_follow,
};
use serde::Deserialize;
use std::collections::HashSet;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

mod folder_metrics;
mod metadata_preserve;
mod shortcut;

pub use folder_metrics::{calculate_folder_size, count_folder_items, get_folder_metrics};
pub use metadata_preserve::{apply_basic_metadata_to_handle, preserve_basic_metadata};
pub use shortcut::{create_shortcut, ShortcutRequest};

// ============================================================================
// Data types
// ============================================================================

#[derive(Debug, Deserialize)]
pub struct RenameRequest {
    pub path: String,
    pub new_name: String,
}

#[derive(Debug)]
struct RenamePlan {
    source_path: PathBuf,
    temp_path: Option<PathBuf>,
    final_path: PathBuf,
}

// ============================================================================
// Basic File Operations
// ============================================================================

pub fn create_directory(path: &str, name: &str) -> Result<String, String> {
    if crate::archive::split_archive_path(path)?.is_some() {
        return crate::archive::create_archive_directory(path.to_string(), name.to_string());
    }

    validate_name(name)?;
    let parent = validate_existing_path_no_resolve(path)?;
    let new_path = parent.join(name);
    fs::create_dir(&new_path).map_err(|e| {
        if e.kind() == std::io::ErrorKind::AlreadyExists {
            format!("Directory already exists: {name}")
        } else {
            format!("Failed to create directory: {e}")
        }
    })?;
    Ok(new_path.to_string_lossy().to_string())
}

pub fn create_file(path: &str, name: &str) -> Result<String, String> {
    if crate::archive::split_archive_path(path)?.is_some() {
        return crate::archive::create_archive_file(path.to_string(), name.to_string());
    }

    validate_name(name)?;
    let parent = validate_existing_path_no_resolve(path)?;
    let new_path = parent.join(name);
    fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&new_path)
        .map_err(|e| {
            if e.kind() == std::io::ErrorKind::AlreadyExists {
                format!("File already exists: {name}")
            } else {
                format!("Failed to create file: {e}")
            }
        })?;
    Ok(new_path.to_string_lossy().to_string())
}

pub fn delete_entry(path: &str) -> Result<(), String> {
    if crate::archive::is_archive_virtual_path(path) {
        return crate::archive::delete_archive_entry(path);
    }

    if crate::recycle_bin::is_recycle_bin_item_path(Path::new(path)) {
        return crate::recycle_bin::delete_recycle_item(path);
    }

    let path_buf = validate_path_no_follow(path)?;
    let lstat = fs::symlink_metadata(&path_buf).map_err(|e| format!("Failed to stat path: {e}"))?;
    if lstat.file_type().is_symlink() {
        if lstat.is_dir() {
            fs::remove_dir(&path_buf)
                .map_err(|e| format!("Failed to delete directory symlink: {}", e))?;
        } else {
            fs::remove_file(&path_buf).map_err(|e| format!("Failed to delete symlink: {}", e))?;
        }
    } else if lstat.is_dir() {
        delete_filesystem_entry(&path_buf, true)
            .map_err(|e| format!("Failed to delete directory: {e}"))?;
    } else {
        delete_filesystem_entry(&path_buf, false)
            .map_err(|e| format!("Failed to delete file: {e}"))?;
    }
    Ok(())
}

fn delete_filesystem_entry(path: &Path, is_dir: bool) -> io::Result<()> {
    let result = if is_dir {
        fs::remove_dir_all(path)
    } else {
        fs::remove_file(path)
    };

    match result {
        Ok(()) => Ok(()),
        Err(error) => delete_filesystem_entry_after_error(path, is_dir, error),
    }
}

#[cfg(windows)]
fn delete_filesystem_entry_after_error(
    path: &Path,
    is_dir: bool,
    original_error: io::Error,
) -> io::Result<()> {
    if original_error.kind() != io::ErrorKind::PermissionDenied {
        return Err(original_error);
    }

    clear_readonly_attributes_for_delete(path);

    let retry = if is_dir {
        fs::remove_dir_all(path)
    } else {
        fs::remove_file(path)
    };

    match retry {
        Ok(()) => Ok(()),
        Err(retry_error) if retry_error.kind() == io::ErrorKind::PermissionDenied => {
            delete_with_shell_permanently(path).map_err(|_| retry_error)
        }
        Err(retry_error) => Err(retry_error),
    }
}

#[cfg(not(windows))]
fn delete_filesystem_entry_after_error(
    _path: &Path,
    _is_dir: bool,
    original_error: io::Error,
) -> io::Result<()> {
    Err(original_error)
}

#[cfg(windows)]
#[allow(clippy::permissions_set_readonly_false)]
fn clear_readonly_attributes_for_delete(path: &Path) {
    fn clear_one(path: &Path) {
        let Ok(metadata) = fs::symlink_metadata(path) else {
            return;
        };
        if metadata.file_type().is_symlink() {
            return;
        }

        let mut permissions = metadata.permissions();
        if permissions.readonly() {
            permissions.set_readonly(false);
            let _ = fs::set_permissions(path, permissions);
        }
    }

    let Ok(metadata) = fs::symlink_metadata(path) else {
        return;
    };
    if metadata.is_dir() && !metadata.file_type().is_symlink() {
        if let Ok(entries) = fs::read_dir(path) {
            for entry in entries.flatten() {
                let child = entry.path();
                let Ok(child_metadata) = fs::symlink_metadata(&child) else {
                    clear_one(&child);
                    continue;
                };

                if child_metadata.is_dir() && !child_metadata.file_type().is_symlink() {
                    clear_readonly_attributes_for_delete(&child);
                } else {
                    clear_one(&child);
                }
            }
        }
    }

    clear_one(path);
}

#[cfg(windows)]
fn delete_with_shell_permanently(path: &Path) -> io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::{null, null_mut};
    use winapi::um::shellapi::{
        SHFileOperationW, FOF_NOCONFIRMATION, FOF_NOERRORUI, FOF_SILENT, FO_DELETE, SHFILEOPSTRUCTW,
    };

    let from: Vec<u16> = path.as_os_str().encode_wide().chain([0, 0]).collect();
    let mut operation = SHFILEOPSTRUCTW {
        hwnd: null_mut(),
        wFunc: FO_DELETE as u32,
        pFrom: from.as_ptr(),
        pTo: null(),
        fFlags: FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        fAnyOperationsAborted: 0,
        hNameMappings: null_mut(),
        lpszProgressTitle: null(),
    };

    let result = unsafe { SHFileOperationW(&mut operation) };
    if result == 0 && operation.fAnyOperationsAborted == 0 {
        return Ok(());
    }

    if result != 0 {
        return Err(io::Error::from_raw_os_error(result));
    }

    Err(io::Error::new(
        io::ErrorKind::Interrupted,
        "delete operation was aborted",
    ))
}

pub fn move_to_trash(paths: &[String]) -> Result<Vec<String>, String> {
    let previous_recycle_paths = crate::recycle_bin::recycle_bin_data_path_set();
    let mut trashed_original_paths = Vec::new();
    let mut failures = Vec::new();
    let mut trash_unavailable = false;

    // Keep going past a failed item so one locked or missing file does not
    // abandon the rest of the selection; report every failure at the end.
    for path in paths {
        if crate::archive::is_archive_virtual_path(path) {
            if let Err(error) = crate::archive::delete_archive_entry(path) {
                failures.push(error);
            }
            continue;
        }

        let validated = match validate_path_no_follow(path) {
            Ok(validated) => validated,
            Err(error) => {
                failures.push(error);
                continue;
            }
        };
        if let Err(error) = trash::delete(&validated) {
            trash_unavailable = true;
            failures.push(format!("{}: {error}", validated.display()));
            continue;
        }
        trashed_original_paths.push(validated.to_string_lossy().to_string());
    }

    if !failures.is_empty() {
        let message = failures.join("; ");
        return Err(if trash_unavailable {
            format!("TRASH_UNAVAILABLE: {message}")
        } else {
            message
        });
    }

    if trashed_original_paths.is_empty() {
        return Ok(Vec::new());
    }

    Ok(crate::recycle_bin::recycle_bin_paths_for_originals(
        &trashed_original_paths,
        &previous_recycle_paths,
    ))
}

// ============================================================================
// Rename
// ============================================================================

pub fn rename_entry(path: &str, new_name: &str) -> Result<String, String> {
    if crate::archive::is_archive_virtual_path(path) {
        return crate::archive::rename_archive_entry(path.to_string(), new_name.to_string());
    }

    validate_name(new_name)?;
    let path_buf = validate_path_no_follow(path)?;
    let parent = path_buf
        .parent()
        .ok_or_else(|| "Cannot get parent directory".to_string())?;
    let new_path = parent.join(new_name);
    if new_path == path_buf {
        return Ok(new_path.to_string_lossy().to_string());
    }

    let same_target_on_case_insensitive_fs =
        path_collision_key(&path_buf) == path_collision_key(&new_path);
    if path_exists_no_follow(&new_path) && !same_target_on_case_insensitive_fs {
        return Err(format!(
            "A file or directory with that name already exists: {new_name}"
        ));
    }

    if same_target_on_case_insensitive_fs && path_exists_no_follow(&new_path) {
        let temp_path = move_to_unique_rename_temp(&path_buf, parent, 0)?;
        if let Err(e) = rename_no_replace(&temp_path, &new_path) {
            let _ = rename_no_replace(&temp_path, &path_buf);
            return Err(format!("Failed to rename: {e}"));
        }
    } else {
        rename_no_replace(&path_buf, &new_path).map_err(|e| {
            if e.kind() == std::io::ErrorKind::AlreadyExists {
                format!("A file or directory with that name already exists: {new_name}")
            } else {
                format!("Failed to rename: {e}")
            }
        })?;
    }
    Ok(new_path.to_string_lossy().to_string())
}

pub fn batch_rename(entries: Vec<RenameRequest>) -> Result<Vec<String>, String> {
    if entries.is_empty() {
        return Ok(Vec::new());
    }

    let mut plans: Vec<RenamePlan> = Vec::with_capacity(entries.len());
    let mut source_keys = HashSet::new();
    let mut final_keys = HashSet::new();

    for req in &entries {
        validate_name(&req.new_name)?;
        let source_path = validate_path_no_follow(&req.path)?;
        let parent = source_path
            .parent()
            .ok_or_else(|| "Cannot get parent directory".to_string())?;
        let final_path = parent.join(&req.new_name);
        let source_key = path_collision_key(&source_path);
        let final_key = path_collision_key(&final_path);

        if !source_keys.insert(source_key) {
            return Err(format!("Duplicate source in batch rename: {}", req.path));
        }
        if !final_keys.insert(final_key) {
            return Err(format!(
                "Two selected files would be renamed to the same name: {}",
                req.new_name
            ));
        }

        plans.push(RenamePlan {
            source_path,
            temp_path: None,
            final_path,
        });
    }

    for plan in &plans {
        let final_key = path_collision_key(&plan.final_path);
        if path_exists_no_follow(&plan.final_path) && !source_keys.contains(&final_key) {
            let name = plan.final_path.file_name().map_or_else(
                || plan.final_path.to_string_lossy().to_string(),
                |n| n.to_string_lossy().to_string(),
            );
            return Err(format!(
                "A file or directory with that name already exists: {name}"
            ));
        }
    }

    // Phase 1: move all to temp names
    let mut moved_to_temp: Vec<(PathBuf, PathBuf)> = Vec::new();
    for (idx, plan) in plans.iter_mut().enumerate() {
        let parent = plan
            .source_path
            .parent()
            .ok_or_else(|| "Cannot get parent directory".to_string())?;
        match move_to_unique_rename_temp(&plan.source_path, parent, idx) {
            Ok(temp_path) => {
                moved_to_temp.push((temp_path.clone(), plan.source_path.clone()));
                plan.temp_path = Some(temp_path);
            }
            Err(e) => {
                let mut recovery_failures = Vec::new();
                for (temp, source) in moved_to_temp.iter().rev() {
                    if let Err(rollback_err) = rename_no_replace(temp, source) {
                        recovery_failures.push(rollback_detail(temp, source, &rollback_err));
                    }
                }
                if recovery_failures.is_empty() {
                    return Err(e);
                }
                return Err(format!(
                    "{}. Some paths could not be restored: {}",
                    e,
                    recovery_failures.join("; ")
                ));
            }
        }
    }

    // Phase 2: move from temp to final names
    let mut finalized: Vec<(PathBuf, PathBuf)> = Vec::new();
    for plan in &plans {
        let temp_path = plan
            .temp_path
            .as_ref()
            .ok_or_else(|| "Batch rename temp path was not prepared".to_string())?;
        if let Err(e) = rename_no_replace(temp_path, &plan.final_path) {
            let mut recovery_failures = Vec::new();
            for (final_path, source_path) in finalized.iter().rev() {
                if let Err(rollback_err) = rename_no_replace(final_path, source_path) {
                    recovery_failures.push(rollback_detail(final_path, source_path, &rollback_err));
                }
            }
            for remaining in &plans {
                if let Some(temp_path) = remaining.temp_path.as_ref() {
                    if path_exists_no_follow(temp_path) {
                        if let Err(rollback_err) =
                            rename_no_replace(temp_path, &remaining.source_path)
                        {
                            recovery_failures.push(rollback_detail(
                                temp_path,
                                &remaining.source_path,
                                &rollback_err,
                            ));
                        }
                    }
                }
            }
            return Err(batch_rename_recovery_error(&e, recovery_failures));
        }
        finalized.push((plan.final_path.clone(), plan.source_path.clone()));
    }

    Ok(plans
        .into_iter()
        .map(|p| p.final_path.to_string_lossy().to_string())
        .collect())
}

// ============================================================================
// Directory Tree / Info
// ============================================================================

pub fn list_subdirectories(path: &str) -> Result<Vec<TreeNode>, String> {
    if crate::recycle_bin::is_recycle_bin_path(path) {
        return Ok(Vec::new());
    }

    let path_buf = validate_existing_path_no_resolve(path)?;
    if !path_buf.is_dir() {
        return Err(format!("Path is not a directory: {path}"));
    }

    let mut nodes: Vec<TreeNode> = Vec::new();
    let read_dir = fs::read_dir(&path_buf).map_err(|e| format!("Failed to read directory: {e}"))?;

    for entry in read_dir.flatten() {
        if let Ok(file_type) = entry.file_type() {
            if file_type.is_dir() {
                let entry_path = entry.path();
                let name = entry_path
                    .file_name()
                    .map(|n| n.to_string_lossy().to_string())
                    .unwrap_or_default();
                if name.starts_with('.') {
                    continue;
                }
                let has_children = fs::read_dir(&entry_path).is_ok_and(|entries| {
                    entries
                        .filter_map(std::result::Result::ok)
                        .any(|e| e.file_type().is_ok_and(|ft| ft.is_dir()))
                });
                nodes.push(TreeNode {
                    name,
                    path: entry_path.to_string_lossy().to_string(),
                    has_children,
                    children: Vec::new(),
                });
            }
        }
    }
    nodes.sort_by_cached_key(|node| crate::native_accel::case_fold_for_sort(&node.name));
    Ok(nodes)
}

pub fn get_entry_info_simple(path: &str) -> Result<FileEntry, String> {
    if crate::recycle_bin::is_recycle_bin_path(path) {
        return Ok(crate::recycle_bin::recycle_bin_entry());
    }

    let path_buf = validate_path_no_follow(path)?;
    get_file_entry(&path_buf).ok_or_else(|| "Failed to get file info".to_string())
}

// ============================================================================
// Private helpers
// ============================================================================

fn unique_rename_temp_path(parent: &Path, idx: usize) -> Result<PathBuf, String> {
    let mut random = [0u8; 16];
    getrandom::fill(&mut random)
        .map_err(|e| format!("Failed to generate secure temporary name: {e}"))?;
    let token = random
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect::<String>();
    Ok(parent.join(format!(".simplefile-rename-tmp-{idx}-{token}")))
}

fn rename_no_replace(source: &Path, destination: &Path) -> std::io::Result<()> {
    rename_exclusive(source, destination)
}

fn move_to_unique_rename_temp(source: &Path, parent: &Path, idx: usize) -> Result<PathBuf, String> {
    for _ in 0..128 {
        let candidate = unique_rename_temp_path(parent, idx)?;
        match rename_no_replace(source, &candidate) {
            Ok(()) => return Ok(candidate),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(format!("Failed to prepare rename: {e}")),
        }
    }
    Err("Failed to choose a unique temporary rename path".to_string())
}

fn rollback_detail(from: &Path, to: &Path, error: &std::io::Error) -> String {
    format!("{} -> {} ({})", from.display(), to.display(), error)
}

fn batch_rename_recovery_error(
    phase_error: &std::io::Error,
    recovery_failures: Vec<String>,
) -> String {
    if recovery_failures.is_empty() {
        format!("Failed to finish batch rename: {phase_error}")
    } else {
        format!(
            "Failed to finish batch rename: {}. Some paths could not be restored: {}",
            phase_error,
            recovery_failures.join("; ")
        )
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::{delete_entry, move_to_trash, preserve_basic_metadata};
    use std::fs;
    use std::path::Path;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(label: &str) -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time after unix epoch")
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "simplefile-file-ops-{label}-{}-{nanos}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create temp dir");
        path
    }

    #[allow(clippy::permissions_set_readonly_false)]
    fn clear_readonly(path: &Path) {
        if let Ok(metadata) = fs::metadata(path) {
            let mut permissions = metadata.permissions();
            if permissions.readonly() {
                permissions.set_readonly(false);
                let _ = fs::set_permissions(path, permissions);
            }
        }
    }

    fn set_readonly(path: &Path) {
        let mut permissions = fs::metadata(path).expect("metadata").permissions();
        permissions.set_readonly(true);
        fs::set_permissions(path, permissions).expect("set readonly");
    }

    #[test]
    fn copy_readonly_file_sets_timestamps_before_permissions() {
        let root = temp_dir("readonly-copy");
        let src = root.join("source.txt");
        let dst = root.join("destination.txt");
        fs::write(&src, b"readonly payload").expect("write source");
        let mut permissions = fs::metadata(&src).expect("source metadata").permissions();
        permissions.set_readonly(true);
        fs::set_permissions(&src, permissions).expect("make source readonly");

        fs::write(&dst, b"readonly payload").expect("write destination");
        preserve_basic_metadata(&src, &dst).expect("preserve metadata on readonly file");

        assert_eq!(
            fs::read(&dst).expect("read destination"),
            b"readonly payload"
        );
        assert!(fs::metadata(&dst)
            .expect("destination metadata")
            .permissions()
            .readonly());

        clear_readonly(&src);
        clear_readonly(&dst);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn delete_entry_outside_a_real_recycle_root_never_touches_siblings() {
        // A folder whose name merely contains "$Recycle.Bin" is an ordinary
        // folder: deleting "Archive.zip" must not also remove "AIchive.zip".
        let root = temp_dir("not-a-recycle-root").join("$Recycle.Bin-old");
        fs::create_dir_all(&root).unwrap();
        let archive = root.join("Archive.zip");
        let lookalike = root.join("AIchive.zip");
        fs::write(&archive, b"zip").unwrap();
        fs::write(&lookalike, b"other").unwrap();

        delete_entry(&archive.to_string_lossy()).expect("delete the archive");

        assert!(!archive.exists());
        assert!(lookalike.exists(), "unrelated sibling was deleted");
        let _ = fs::remove_dir_all(root.parent().unwrap());
    }

    #[test]
    fn delete_entry_of_missing_path_is_an_error_even_near_recycle_bin() {
        let root = temp_dir("missing-recycle").join("$Recycle.Bin-copy");
        fs::create_dir_all(&root).unwrap();
        let missing = root.join("$Rnothing.txt");
        assert!(delete_entry(&missing.to_string_lossy()).is_err());
        let _ = fs::remove_dir_all(root.parent().unwrap());
    }

    #[test]
    fn move_to_trash_reports_every_failed_path_instead_of_stopping_at_the_first() {
        let root = temp_dir("trash-missing");
        let first = root.join("missing-one.txt");
        let second = root.join("missing-two.txt");
        let error = move_to_trash(&[
            first.to_string_lossy().to_string(),
            second.to_string_lossy().to_string(),
        ])
        .expect_err("missing paths cannot be trashed");
        assert!(error.contains("missing-one.txt"), "{error}");
        assert!(error.contains("missing-two.txt"), "{error}");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn delete_entry_removes_readonly_directory_tree() {
        let root = temp_dir("readonly-delete");
        let target = root.join("target");
        let nested = target.join("nested");
        let readonly_file = nested.join("readonly.txt");
        fs::create_dir_all(&nested).expect("create nested directory");
        fs::write(&readonly_file, b"readonly payload").expect("write readonly file");
        set_readonly(&readonly_file);
        set_readonly(&nested);
        set_readonly(&target);

        delete_entry(&target.to_string_lossy()).expect("delete readonly directory tree");

        assert!(!target.exists());
        let _ = fs::remove_dir_all(root);
    }
}
