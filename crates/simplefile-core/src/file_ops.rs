//! Reusable file operation logic for the SumaFile backend.
//!
//! This module contains host-independent file system operations that are callable
//! from the service crate, tests, and future tools.

use crate::models::{FileEntry, TreeNode};
use crate::path_conflict::{
    create_dir_exclusive, is_keep_both_action, path_collision_key, path_exists_no_follow,
};
use crate::utils::{
    get_file_entry, recreate_symlink, validate_existing_path_no_resolve, validate_name,
    validate_path_no_follow,
};
use serde::Deserialize;
use std::collections::HashSet;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

mod folder_metrics;
mod metadata_preserve;

pub use folder_metrics::{calculate_folder_size, count_folder_items, get_folder_metrics};
pub use metadata_preserve::preserve_basic_metadata;

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

    let in_recycle_bin = path.contains("$Recycle.Bin") || path.contains("$recycle.bin");
    let recycle_data = Path::new(path)
        .file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| name.len() >= 2 && name.as_bytes()[1].eq_ignore_ascii_case(&b'R'));
    if in_recycle_bin
        && (crate::recycle_bin::paired_info_path(Path::new(path)).exists() || recycle_data)
    {
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

    for path in paths {
        if crate::archive::is_archive_virtual_path(path) {
            crate::archive::delete_archive_entry(path)?;
            continue;
        }

        let validated = validate_path_no_follow(path)?;
        trash::delete(&validated).map_err(|e| format!("TRASH_UNAVAILABLE: {e}"))?;
        trashed_original_paths.push(validated.to_string_lossy().to_string());
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
    if new_path.exists() && !same_target_on_case_insensitive_fs {
        return Err(format!(
            "A file or directory with that name already exists: {new_name}"
        ));
    }

    if same_target_on_case_insensitive_fs && new_path.exists() {
        let temp_path = move_to_unique_rename_temp(&path_buf, parent, 0)?;
        if let Err(e) = rename_no_replace(&temp_path, &new_path) {
            let _ = fs::rename(&temp_path, &path_buf);
            return Err(format!("Failed to rename: {e}"));
        }
    } else {
        fs::rename(&path_buf, &new_path).map_err(|e| format!("Failed to rename: {e}"))?;
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
        if plan.final_path.exists() && !source_keys.contains(&final_key) {
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
                    if let Err(rollback_err) = fs::rename(temp, source) {
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
                if let Err(rollback_err) = fs::rename(final_path, source_path) {
                    recovery_failures.push(rollback_detail(final_path, source_path, &rollback_err));
                }
            }
            for remaining in &plans {
                if let Some(temp_path) = remaining.temp_path.as_ref() {
                    if temp_path.exists() {
                        if let Err(rollback_err) = fs::rename(temp_path, &remaining.source_path) {
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
// Simple Copy/Move
// ============================================================================

pub fn copy_entry(source: &str, destination: &str) -> Result<String, String> {
    let source_path = validate_path_no_follow(source)?;
    let dest_path = validate_existing_path_no_resolve(destination)?;
    if !dest_path.is_dir() {
        return Err("Destination must be a directory".into());
    }
    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_path.join(file_name);

    if path_exists_no_follow(&final_dest) {
        return Err(format!(
            "CONFLICT: destination already exists: {}",
            final_dest.to_string_lossy()
        ));
    }

    let source_meta =
        fs::symlink_metadata(&source_path).map_err(|e| format!("Failed to stat source: {e}"))?;
    let source_type = source_meta.file_type();
    if source_type.is_dir() {
        copy_dir_iterative(&source_path, &final_dest)?;
    } else if source_type.is_symlink() {
        recreate_symlink(&source_path, &final_dest)?;
    } else {
        copy_file_exclusive_preserve_times(&source_path, &final_dest)?;
    }
    Ok(final_dest.to_string_lossy().to_string())
}

pub fn move_entry(source: &str, destination: &str) -> Result<String, String> {
    let source_path = validate_path_no_follow(source)?;
    let dest_path = validate_existing_path_no_resolve(destination)?;
    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_path.join(file_name);

    if path_exists_no_follow(&final_dest) {
        return Err(format!(
            "CONFLICT: destination already exists: {}",
            final_dest.to_string_lossy()
        ));
    }

    if fs::rename(&source_path, &final_dest).is_err() {
        let source_meta = fs::symlink_metadata(&source_path)
            .map_err(|e| format!("Failed to stat source: {e}"))?;
        let source_type = source_meta.file_type();
        if source_type.is_dir() {
            if let Err(copy_err) = copy_dir_iterative(&source_path, &final_dest) {
                let _ = fs::remove_dir_all(&final_dest);
                return Err(copy_err);
            }
            fs::remove_dir_all(&source_path)
                .map_err(|e| format!("Copied but failed to delete source: {e}"))?;
        } else if source_type.is_symlink() {
            recreate_symlink(&source_path, &final_dest)?;
            if source_meta.is_dir() {
                fs::remove_dir(&source_path)
                    .map_err(|e| format!("Copied but failed to delete source symlink: {e}"))?;
            } else {
                fs::remove_file(&source_path)
                    .map_err(|e| format!("Copied but failed to delete source symlink: {e}"))?;
            }
        } else {
            match copy_file_exclusive_preserve_times(&source_path, &final_dest) {
                Ok(_) => {}
                Err(e) => {
                    let _ = fs::remove_file(&final_dest);
                    return Err(e);
                }
            }
            fs::remove_file(&source_path)
                .map_err(|e| format!("Copied but failed to delete source: {e}"))?;
        }
    }
    Ok(final_dest.to_string_lossy().to_string())
}

// ============================================================================
// Conflict-aware Copy/Move
// ============================================================================

pub fn copy_entry_resolved(
    source: &str,
    destination: &str,
    conflict_action: &str,
) -> Result<String, String> {
    if crate::archive::should_handle_transfer(source, destination)? {
        return crate::archive::copy_entry_resolved(
            source.to_string(),
            destination.to_string(),
            conflict_action.to_string(),
        );
    }

    let source_path = validate_path_no_follow(source)?;
    let dest_dir = validate_existing_path_no_resolve(destination)?;
    if !dest_dir.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }
    let retry_keep_both = is_keep_both_action(conflict_action);
    for _ in 0..100 {
        match resolve_destination(&source_path, &dest_dir, conflict_action)? {
            Some(final_dest) => match copy_path_to_destination(&source_path, &final_dest) {
                Ok(()) => return Ok(final_dest.to_string_lossy().to_string()),
                Err(e) if retry_keep_both && e.starts_with("CONFLICT:") => continue,
                Err(e) => return Err(e),
            },
            None => return Ok(format!("SKIPPED:{source}")),
        }
    }
    Err("Could not choose a unique destination after repeated conflicts".to_string())
}

pub fn move_entry_resolved(
    source: &str,
    destination: &str,
    conflict_action: &str,
) -> Result<String, String> {
    if crate::archive::should_handle_transfer(source, destination)? {
        return crate::archive::move_entry_resolved(
            source.to_string(),
            destination.to_string(),
            conflict_action.to_string(),
        );
    }

    let source_path = validate_path_no_follow(source)?;
    let dest_dir = validate_existing_path_no_resolve(destination)?;
    if !dest_dir.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }
    let retry_keep_both = is_keep_both_action(conflict_action);
    for _ in 0..100 {
        match resolve_destination(&source_path, &dest_dir, conflict_action)? {
            Some(final_dest) => {
                let allow_rename = !retry_keep_both;
                match move_path_to_destination(&source_path, &final_dest, allow_rename) {
                    Ok(()) => return Ok(final_dest.to_string_lossy().to_string()),
                    Err(e) if retry_keep_both && e.starts_with("CONFLICT:") => continue,
                    Err(e) => return Err(e),
                }
            }
            None => return Ok(format!("SKIPPED:{source}")),
        }
    }
    Err("Could not choose a unique destination after repeated conflicts".to_string())
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
    if destination.exists() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::AlreadyExists,
            "destination already exists",
        ));
    }
    fs::rename(source, destination)
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

fn remove_existing_path(path: &Path) -> Result<(), String> {
    let meta = fs::symlink_metadata(path)
        .map_err(|e| format!("Failed to stat existing destination: {e}"))?;
    if meta.file_type().is_symlink() {
        if meta.is_dir() {
            fs::remove_dir(path)
                .map_err(|e| format!("Failed to remove destination symlink: {}", e))?;
        } else {
            fs::remove_file(path)
                .map_err(|e| format!("Failed to remove destination symlink: {}", e))?;
        }
    } else if meta.is_dir() {
        fs::remove_dir_all(path)
            .map_err(|e| format!("Failed to remove destination directory: {e}"))?;
    } else {
        fs::remove_file(path).map_err(|e| format!("Failed to remove destination file: {e}"))?;
    }
    Ok(())
}

fn unique_destination_path(dest_dir: &Path, file_name: &std::ffi::OsStr) -> PathBuf {
    let original = std::path::Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |s| s.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|e| e.to_string_lossy().to_string());
    for i in 1..10_000u32 {
        let candidate_name = match &ext {
            Some(ext) if !ext.is_empty() => format!("{} ({}){}.{}", stem, i, "", ext),
            _ => format!("{stem} ({i})"),
        };
        let candidate = dest_dir.join(candidate_name);
        if !path_exists_no_follow(&candidate) {
            return candidate;
        }
    }
    dest_dir.join(file_name)
}

fn resolve_destination(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
) -> Result<Option<PathBuf>, String> {
    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_dir.join(file_name);
    if !path_exists_no_follow(&final_dest) {
        return Ok(Some(final_dest));
    }

    match conflict_action.to_ascii_lowercase().as_str() {
        "skip" => Ok(None),
        "replace" => {
            if let (Ok(src_can), Ok(dst_can)) =
                (source_path.canonicalize(), final_dest.canonicalize())
            {
                if src_can == dst_can {
                    return Ok(Some(final_dest));
                }
            }
            remove_existing_path(&final_dest)?;
            Ok(Some(final_dest))
        }
        "rename" | "keep-both" | "keep_both" => {
            Ok(Some(unique_destination_path(dest_dir, file_name)))
        }
        _ => Err(format!(
            "CONFLICT: destination already exists: {}",
            final_dest.to_string_lossy()
        )),
    }
}

fn copy_path_to_destination(source_path: &Path, final_dest: &Path) -> Result<(), String> {
    let meta =
        fs::symlink_metadata(source_path).map_err(|e| format!("Failed to stat source: {e}"))?;
    if meta.file_type().is_dir() {
        copy_dir_iterative(source_path, final_dest)
    } else if meta.file_type().is_symlink() {
        recreate_symlink(source_path, final_dest)
    } else {
        if let Some(parent) = final_dest.parent() {
            fs::create_dir_all(parent)
                .map_err(|e| format!("Failed to create parent directory: {e}"))?;
        }
        copy_file_exclusive_preserve_times(source_path, final_dest).map(|_| ())
    }
}

fn move_path_to_destination(
    source_path: &Path,
    final_dest: &Path,
    allow_rename: bool,
) -> Result<(), String> {
    if allow_rename && fs::rename(source_path, final_dest).is_ok() {
        return Ok(());
    }
    copy_path_to_destination(source_path, final_dest)?;
    remove_existing_path(source_path)
        .map_err(|e| format!("Copied but failed to delete source: {e}"))?;
    Ok(())
}

pub(crate) fn copy_dir_iterative(src: &Path, dst: &Path) -> Result<(), String> {
    if let Ok(canonical_src) = src.canonicalize() {
        let canonical_dst = dst
            .parent()
            .and_then(|p| p.canonicalize().ok())
            .map(|p| p.join(dst.file_name().unwrap_or_default()));
        if let Some(cdst) = canonical_dst {
            if cdst.starts_with(&canonical_src) {
                return Err(
                    "Cannot copy a directory into itself or one of its subdirectories".to_string(),
                );
            }
        }
    }

    let mut stack: Vec<(PathBuf, PathBuf)> = vec![(src.to_path_buf(), dst.to_path_buf())];
    let mut copied_dirs: Vec<(PathBuf, PathBuf)> = Vec::new();

    while let Some((src_path, dst_path)) = stack.pop() {
        let lstat = fs::symlink_metadata(&src_path).ok();
        let ft = lstat.as_ref().map(std::fs::Metadata::file_type);
        let is_real_dir = ft.as_ref().is_some_and(std::fs::FileType::is_dir);
        let is_symlink = ft.as_ref().is_some_and(std::fs::FileType::is_symlink);

        if is_real_dir {
            create_dir_exclusive(&dst_path)?;
            copied_dirs.push((src_path.clone(), dst_path.clone()));
            for entry in
                fs::read_dir(&src_path).map_err(|e| format!("Failed to read directory: {e}"))?
            {
                let entry = entry.map_err(|e| format!("Failed to read entry: {e}"))?;
                let child_src = entry.path();
                let child_dst = dst_path.join(entry.file_name());
                stack.push((child_src, child_dst));
            }
        } else if is_symlink {
            recreate_symlink(&src_path, &dst_path)?;
        } else {
            if let Some(parent) = dst_path.parent() {
                fs::create_dir_all(parent)
                    .map_err(|e| format!("Failed to create parent directory: {e}"))?;
            }
            if std::fs::symlink_metadata(&dst_path).is_ok() {
                return Err(format!(
                    "CONFLICT: destination already exists: {}",
                    dst_path.to_string_lossy()
                ));
            }
            copy_file_exclusive_preserve_times(&src_path, &dst_path)?;
        }
    }
    for (src_dir, dst_dir) in copied_dirs.into_iter().rev() {
        preserve_basic_metadata(&src_dir, &dst_dir)?;
    }
    Ok(())
}

fn copy_file_exclusive_preserve_times(src: &Path, dst: &Path) -> Result<u64, String> {
    let mut created_destination = false;
    let result = (|| -> Result<u64, String> {
        let mut source =
            fs::File::open(src).map_err(|e| format!("Failed to open source file: {e}"))?;
        let mut destination = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(dst)
            .map_err(|e| {
                if e.kind() == std::io::ErrorKind::AlreadyExists {
                    format!(
                        "CONFLICT: destination already exists: {}",
                        dst.to_string_lossy()
                    )
                } else {
                    format!("Failed to create destination file: {e}")
                }
            })?;
        created_destination = true;
        let copied = io::copy(&mut source, &mut destination)
            .map_err(|e| format!("Failed to copy file: {e}"))?;
        preserve_basic_metadata(src, dst)?;
        Ok(copied)
    })();

    if created_destination && result.is_err() {
        let _ = fs::remove_file(dst);
    }
    result
}

#[cfg(all(test, windows))]
mod tests {
    use super::{copy_file_exclusive_preserve_times, delete_entry};
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

        let copied = copy_file_exclusive_preserve_times(&src, &dst).expect("copy readonly file");

        assert_eq!(copied, b"readonly payload".len() as u64);
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
