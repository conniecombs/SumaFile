use std::ffi::OsStr;
use std::fs;
use std::path::{Path, PathBuf};

use super::create::{
    create_rar_archive, create_seven_zip_archive, create_tar_archive, create_zip_archive,
    resolve_rar_binary,
};
use super::extract::{extract_archive_entry_to_directory, extract_archive_to_directory, ExtractLimits};
use super::path::{
    build_virtual_archive_path, create_dir_all, replace_archive, same_archive_path,
    split_archive_path, unique_temp_archive_path, unique_work_dir, ArchiveFormat, ArchivePath,
};

pub fn should_handle_transfer(source: &str, destination: &str) -> Result<bool, String> {
    let source_is_archive_entry =
        split_archive_path(source)?.is_some_and(|parsed| !parsed.inner_path.as_os_str().is_empty());
    let destination_is_archive = split_archive_path(destination)?.is_some();
    Ok(source_is_archive_entry || destination_is_archive)
}

pub fn copy_entry_resolved(
    source: String,
    destination: String,
    conflict_action: String,
) -> Result<String, String> {
    let source_archive =
        split_archive_path(&source)?.filter(|parsed| !parsed.inner_path.as_os_str().is_empty());
    let destination_archive = split_archive_path(&destination)?;

    match (source_archive, destination_archive) {
        (Some(source_parsed), Some(destination_parsed))
            if same_archive_path(
                &source_parsed.archive_path,
                &destination_parsed.archive_path,
            ) =>
        {
            let result =
                mutate_archive(&source_parsed.archive_path, source_parsed.format, |root| {
                    let source_path = root.join(&source_parsed.inner_path);
                    let dest_dir = root.join(&destination_parsed.inner_path);
                    copy_with_conflict(&source_path, &dest_dir, &conflict_action)
                })?;
            Ok(result.unwrap_or_else(|| format!("SKIPPED:{source}")))
        }
        (source_parsed, Some(destination_parsed)) => {
            let materialized = materialize_transfer_source(&source, source_parsed.as_ref())?;
            let mut materialized = materialized;
            let result = mutate_archive(
                &destination_parsed.archive_path,
                destination_parsed.format,
                |root| {
                    let dest_dir = root.join(&destination_parsed.inner_path);
                    copy_with_conflict(&materialized.path, &dest_dir, &conflict_action)
                },
            );
            materialized.cleanup();
            let result = result?;
            Ok(result.unwrap_or_else(|| format!("SKIPPED:{source}")))
        }
        (Some(source_parsed), None) => {
            copy_archive_entry_to_local(&source_parsed, &destination, &conflict_action)
        }
        (None, None) => Err("No archive path was involved in the copy operation".to_string()),
    }
}

pub fn move_entry_resolved(
    source: String,
    destination: String,
    conflict_action: String,
) -> Result<String, String> {
    let source_archive =
        split_archive_path(&source)?.filter(|parsed| !parsed.inner_path.as_os_str().is_empty());
    let destination_archive = split_archive_path(&destination)?;

    match (source_archive, destination_archive) {
        (Some(source_parsed), Some(destination_parsed))
            if same_archive_path(
                &source_parsed.archive_path,
                &destination_parsed.archive_path,
            ) =>
        {
            let result =
                mutate_archive(&source_parsed.archive_path, source_parsed.format, |root| {
                    let source_path = root.join(&source_parsed.inner_path);
                    let dest_dir = root.join(&destination_parsed.inner_path);
                    move_with_conflict(&source_path, &dest_dir, &conflict_action)
                })?;
            Ok(result.unwrap_or_else(|| format!("SKIPPED:{source}")))
        }
        (Some(source_parsed), Some(_destination_parsed)) => {
            let result = copy_entry_resolved(source.clone(), destination, conflict_action)?;
            delete_archive_entry_parsed(&source_parsed)?;
            Ok(result)
        }
        (None, Some(destination_parsed)) => {
            let source_path = crate::utils::validate_path_no_follow(&source)?;
            if same_archive_path(&source_path, &destination_parsed.archive_path) {
                return Err("Cannot move an archive into itself".to_string());
            }
            let result = copy_entry_resolved(source.clone(), destination, conflict_action)?;
            remove_local_path(&source_path)
                .map_err(|e| format!("Copied into archive but failed to delete source: {e}"))?;
            Ok(result)
        }
        (Some(source_parsed), None) => {
            let result =
                copy_archive_entry_to_local(&source_parsed, &destination, &conflict_action)?;
            delete_archive_entry_parsed(&source_parsed)?;
            Ok(result)
        }
        (None, None) => Err("No archive path was involved in the move operation".to_string()),
    }
}

pub fn delete_archive_entry(path: &str) -> Result<(), String> {
    let parsed = split_archive_path(path)?
        .filter(|parsed| !parsed.inner_path.as_os_str().is_empty())
        .ok_or_else(|| format!("Path is not an archive entry: {path}"))?;
    delete_archive_entry_parsed(&parsed)
}

pub fn create_archive_directory(path: String, name: String) -> Result<String, String> {
    crate::utils::validate_name(&name)?;
    let parsed = split_archive_path(&path)?
        .ok_or_else(|| format!("Path is not inside an archive: {path}"))?;
    let result = mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let dir_path = root.join(&parsed.inner_path).join(&name);
        if dir_path.exists() {
            return Err(format!("Directory already exists: {name}"));
        }
        fs::create_dir(&dir_path).map_err(|e| format!("Failed to create directory: {e}"))?;
        Ok(Some(dir_path))
    })?;
    result.ok_or_else(|| "Archive directory was not created".to_string())
}

pub fn create_archive_file(path: String, name: String) -> Result<String, String> {
    crate::utils::validate_name(&name)?;
    let parsed = split_archive_path(&path)?
        .ok_or_else(|| format!("Path is not inside an archive: {path}"))?;
    let result = mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let file_path = root.join(&parsed.inner_path).join(&name);
        if let Some(parent) = file_path.parent() {
            create_dir_all(parent)?;
        }
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&file_path)
            .map_err(|e| {
                if e.kind() == std::io::ErrorKind::AlreadyExists {
                    format!("File already exists: {name}")
                } else {
                    format!("Failed to create file: {e}")
                }
            })?;
        Ok(Some(file_path))
    })?;
    result.ok_or_else(|| "Archive file was not created".to_string())
}

pub fn rename_archive_entry(path: String, new_name: String) -> Result<String, String> {
    crate::utils::validate_name(&new_name)?;
    let parsed = split_archive_path(&path)?
        .filter(|parsed| !parsed.inner_path.as_os_str().is_empty())
        .ok_or_else(|| format!("Path is not an archive entry: {path}"))?;
    let result = mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let source_path = root.join(&parsed.inner_path);
        if !source_path.exists() {
            return Err(format!("Archive entry not found: {path}"));
        }
        let parent = source_path
            .parent()
            .ok_or_else(|| "Cannot get parent directory".to_string())?;
        let new_path = parent.join(&new_name);
        if new_path.exists() && !same_archive_path(&source_path, &new_path) {
            return Err(format!(
                "A file or directory with that name already exists: {new_name}"
            ));
        }
        if !same_archive_path(&source_path, &new_path) {
            fs::rename(&source_path, &new_path).map_err(|e| format!("Failed to rename: {e}"))?;
        }
        Ok(Some(new_path))
    })?;
    result.ok_or_else(|| "Archive entry was not renamed".to_string())
}

pub fn materialize_archive_entry_to_temp(path: &str) -> Result<MaterializedSource, String> {
    materialize_archive_entry_to_temp_with_limits(path, ExtractLimits::materialize_defaults())
}

pub(super) fn materialize_archive_entry_to_temp_with_limits(
    path: &str,
    limits: ExtractLimits,
) -> Result<MaterializedSource, String> {
    let parsed = split_archive_path(path)?
        .filter(|parsed| !parsed.inner_path.as_os_str().is_empty())
        .ok_or_else(|| format!("Path is not an archive entry: {path}"))?;
    let mut work_root = WorkRootGuard::create("open")?;
    extract_archive_entry_to_directory(
        &parsed.archive_path,
        work_root.path(),
        &parsed.inner_path,
        limits,
    )?;
    let materialized = work_root.path().join(&parsed.inner_path);
    if !materialized.exists() {
        return Err(format!("Archive entry not found: {path}"));
    }
    Ok(MaterializedSource {
        path: materialized,
        cleanup_root: Some(work_root.take()),
    })
}

/// Deletes `unique_work_dir` on drop unless `take()` transfers ownership.
struct WorkRootGuard {
    root: Option<PathBuf>,
}

impl WorkRootGuard {
    fn create(label: &str) -> Result<Self, String> {
        Ok(Self {
            root: Some(unique_work_dir(label)?),
        })
    }

    fn path(&self) -> &Path {
        self.root.as_ref().expect("work root still owned")
    }

    fn take(&mut self) -> PathBuf {
        self.root.take().expect("work root still owned")
    }
}

impl Drop for WorkRootGuard {
    fn drop(&mut self) {
        if let Some(root) = self.root.take() {
            let _ = fs::remove_dir_all(root);
        }
    }
}

fn delete_archive_entry_parsed(parsed: &ArchivePath) -> Result<(), String> {
    mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let path = root.join(&parsed.inner_path);
        remove_local_path(&path)?;
        Ok(None)
    })?;
    Ok(())
}

/// Temp materialization of an archive entry (or a passthrough local path).
///
/// When `cleanup_root` is set, the work directory is deleted on `cleanup()` / Drop.
#[derive(Debug)]
pub struct MaterializedSource {
    path: PathBuf,
    cleanup_root: Option<PathBuf>,
}

impl MaterializedSource {
    pub fn local(path: PathBuf) -> Self {
        Self {
            path,
            cleanup_root: None,
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn cleanup_root(&self) -> Option<&Path> {
        self.cleanup_root.as_deref()
    }

    pub fn cleanup(&mut self) {
        if let Some(root) = self.cleanup_root.take() {
            let _ = fs::remove_dir_all(root);
        }
    }

    /// Keep the materialized path on disk (e.g. Open With handed off to another process).
    pub fn into_path(mut self) -> PathBuf {
        self.cleanup_root = None;
        std::mem::take(&mut self.path)
    }
}

impl Drop for MaterializedSource {
    fn drop(&mut self) {
        self.cleanup();
    }
}

impl AsRef<Path> for MaterializedSource {
    fn as_ref(&self) -> &Path {
        &self.path
    }
}

impl std::ops::Deref for MaterializedSource {
    type Target = Path;

    fn deref(&self) -> &Path {
        &self.path
    }
}

fn materialize_transfer_source(
    source: &str,
    parsed: Option<&ArchivePath>,
) -> Result<MaterializedSource, String> {
    if let Some(parsed) = parsed {
        let work_root = unique_work_dir("source")?;
        extract_archive_to_directory(&parsed.archive_path, &work_root)?;
        let path = work_root.join(&parsed.inner_path);
        if !path.exists() {
            let _ = fs::remove_dir_all(&work_root);
            return Err(format!("Archive entry not found: {source}"));
        }
        Ok(MaterializedSource {
            path,
            cleanup_root: Some(work_root),
        })
    } else {
        Ok(MaterializedSource::local(crate::utils::validate_path_no_follow(
            source,
        )?))
    }
}

fn copy_archive_entry_to_local(
    parsed: &ArchivePath,
    destination: &str,
    conflict_action: &str,
) -> Result<String, String> {
    let dest_dir = crate::utils::validate_existing_path_no_resolve(destination)?;
    if !dest_dir.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }

    let work_root = unique_work_dir("extract-entry")?;
    let result = (|| {
        extract_archive_to_directory(&parsed.archive_path, &work_root)?;
        let source_path = work_root.join(&parsed.inner_path);
        if !source_path.exists() {
            return Err(format!(
                "Archive entry not found: {}",
                build_virtual_archive_path(&parsed.archive_path, &parsed.inner_path)
            ));
        }
        let final_dest = copy_with_conflict(&source_path, &dest_dir, conflict_action)?;
        Ok(final_dest.map(|path| path.to_string_lossy().to_string()))
    })();
    let _ = fs::remove_dir_all(&work_root);

    match result? {
        Some(path) => Ok(path),
        None => Ok(format!(
            "SKIPPED:{}",
            build_virtual_archive_path(&parsed.archive_path, &parsed.inner_path)
        )),
    }
}

fn mutate_archive<F>(
    archive_path: &Path,
    format: ArchiveFormat,
    mut mutate: F,
) -> Result<Option<String>, String>
where
    F: FnMut(&Path) -> Result<Option<PathBuf>, String>,
{
    let work_root = unique_work_dir("mutate")?;
    let new_archive = unique_temp_archive_path(archive_path)?;
    let result = (|| {
        extract_archive_to_directory(archive_path, &work_root)?;
        let final_path = mutate(&work_root)?;
        rebuild_archive_from_directory(archive_path, format, &work_root, &new_archive)?;
        replace_archive(archive_path, &new_archive)?;
        Ok(final_path.map(|path| {
            let relative = path.strip_prefix(&work_root).unwrap_or(&path);
            build_virtual_archive_path(archive_path, relative)
        }))
    })();

    let _ = fs::remove_dir_all(&work_root);
    let _ = fs::remove_file(&new_archive);

    result
}

fn copy_with_conflict(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
) -> Result<Option<PathBuf>, String> {
    let final_dest = resolve_transfer_destination(source_path, dest_dir, conflict_action)?;
    if let Some(final_dest) = &final_dest {
        copy_path_to_destination(source_path, final_dest)?;
    }
    Ok(final_dest)
}

fn move_with_conflict(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
) -> Result<Option<PathBuf>, String> {
    let final_dest = resolve_transfer_destination(source_path, dest_dir, conflict_action)?;
    if let Some(final_dest) = &final_dest {
        if same_archive_path(source_path, final_dest) {
            return Ok(Some(final_dest.clone()));
        }
        if let Some(parent) = final_dest.parent() {
            create_dir_all(parent)?;
        }
        if let Ok(()) = fs::rename(source_path, final_dest) {
        } else {
            copy_path_to_destination(source_path, final_dest)?;
            remove_local_path(source_path)?;
        }
    }
    Ok(final_dest)
}

fn resolve_transfer_destination(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
) -> Result<Option<PathBuf>, String> {
    if dest_dir.exists() && !dest_dir.is_dir() {
        return Err(format!(
            "Destination is not a directory: {}",
            dest_dir.display()
        ));
    }
    create_dir_all(dest_dir)?;

    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_dir.join(file_name);
    if !final_dest.exists() {
        return Ok(Some(final_dest));
    }

    match conflict_action.to_ascii_lowercase().as_str() {
        "skip" => Ok(None),
        "replace" => {
            if same_archive_path(source_path, &final_dest) {
                return Ok(Some(final_dest));
            }
            remove_local_path(&final_dest)?;
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
    if same_archive_path(source_path, final_dest) {
        return Ok(());
    }

    let meta =
        fs::symlink_metadata(source_path).map_err(|e| format!("Failed to stat source: {e}"))?;
    if meta.file_type().is_symlink() {
        return Err("Copying symlinks into or out of archives is not supported".to_string());
    }
    if meta.is_dir() {
        copy_dir_for_archive(source_path, final_dest)
    } else {
        if let Some(parent) = final_dest.parent() {
            create_dir_all(parent)?;
        }
        fs::copy(source_path, final_dest)
            .map(|_| ())
            .map_err(|e| format!("Failed to copy file: {e}"))
    }
}

fn copy_dir_for_archive(src: &Path, dst: &Path) -> Result<(), String> {
    let src_canonical = src.canonicalize().ok();
    let mut stack = vec![(src.to_path_buf(), dst.to_path_buf())];

    while let Some((src_path, dst_path)) = stack.pop() {
        if let (Some(src_root), Some(dst_parent)) = (
            src_canonical.as_ref(),
            dst_path.parent().and_then(|p| p.canonicalize().ok()),
        ) {
            if dst_parent.starts_with(src_root) {
                return Err(
                    "Cannot copy a directory into itself or one of its subdirectories".to_string(),
                );
            }
        }

        let meta =
            fs::symlink_metadata(&src_path).map_err(|e| format!("Failed to stat source: {e}"))?;
        if meta.file_type().is_symlink() {
            continue;
        }
        if meta.is_dir() {
            create_dir_all(&dst_path)?;
            for entry in
                fs::read_dir(&src_path).map_err(|e| format!("Failed to read directory: {e}"))?
            {
                let entry = entry.map_err(|e| format!("Failed to read directory entry: {e}"))?;
                stack.push((entry.path(), dst_path.join(entry.file_name())));
            }
        } else {
            if let Some(parent) = dst_path.parent() {
                create_dir_all(parent)?;
            }
            fs::copy(&src_path, &dst_path).map_err(|e| format!("Failed to copy file: {e}"))?;
        }
    }

    Ok(())
}

fn remove_local_path(path: &Path) -> Result<(), String> {
    let meta = fs::symlink_metadata(path).map_err(|e| format!("Failed to stat path: {e}"))?;
    if meta.file_type().is_symlink() {
        if meta.is_dir() {
            fs::remove_dir(path).map_err(|e| format!("Failed to delete symlink: {}", e))?;
        } else {
            fs::remove_file(path).map_err(|e| format!("Failed to delete symlink: {}", e))?;
        }
    } else if meta.is_dir() {
        fs::remove_dir_all(path).map_err(|e| format!("Failed to delete directory: {e}"))?;
    } else {
        fs::remove_file(path).map_err(|e| format!("Failed to delete file: {e}"))?;
    }
    Ok(())
}

fn unique_destination_path(dest_dir: &Path, file_name: &OsStr) -> PathBuf {
    let original = Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |s| s.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|e| e.to_string_lossy().to_string());

    for i in 1..10_000u32 {
        let candidate_name = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({i}).{ext}"),
            _ => format!("{stem} ({i})"),
        };
        let candidate = dest_dir.join(candidate_name);
        if !candidate.exists() {
            return candidate;
        }
    }

    dest_dir.join(file_name)
}

fn rebuild_archive_from_directory(
    original_archive_path: &Path,
    format: ArchiveFormat,
    work_root: &Path,
    new_archive_path: &Path,
) -> Result<(), String> {
    let child_paths = archive_root_children(work_root)?;
    let archive_path = new_archive_path.to_string_lossy().to_string();
    match format {
        ArchiveFormat::Zip => create_zip_archive(&child_paths, &archive_path),
        ArchiveFormat::Tar => create_tar_archive(&child_paths, &archive_path, None),
        ArchiveFormat::TarGz => create_tar_archive(&child_paths, &archive_path, Some("gz")),
        ArchiveFormat::Rar => {
            if child_paths.is_empty() {
                return Err("RAR archives cannot be rewritten with no entries".to_string());
            }
            let binary = resolve_rar_binary().ok_or_else(|| {
                "RAR command not found. Install it from Settings -> RAR Tools.".to_string()
            })?;
            create_rar_archive(&child_paths, &archive_path, &binary)
        }
        ArchiveFormat::SevenZip => {
            if child_paths.is_empty() {
                return Err("7-Zip archives cannot be rewritten with no entries".to_string());
            }
            let binary = super::seven_zip::require_seven_zip_binary()?;
            create_seven_zip_archive(&child_paths, &archive_path, &binary)
        }
    }
    .map_err(|e| {
        format!(
            "Failed to rebuild {} archive {}: {}",
            format.label(),
            original_archive_path.display(),
            e
        )
    })
}

fn archive_root_children(root: &Path) -> Result<Vec<String>, String> {
    let mut paths = Vec::new();
    for entry in fs::read_dir(root).map_err(|e| format!("Failed to read archive workspace: {e}"))? {
        let entry = entry.map_err(|e| format!("Failed to read archive workspace entry: {e}"))?;
        paths.push(entry.path().to_string_lossy().to_string());
    }
    paths.sort_by_cached_key(|path| path.to_lowercase());
    Ok(paths)
}
