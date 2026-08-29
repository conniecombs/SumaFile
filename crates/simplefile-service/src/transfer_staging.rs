use simplefile_core::path_conflict::path_exists_no_follow;
use std::fs;
use std::hash::{Hash, Hasher};
use std::path::{Path, PathBuf};

pub(crate) fn conflict_for_existing_destination(path: &Path) -> String {
    format!(
        "CONFLICT: destination already exists: {}",
        path.to_string_lossy()
    )
}

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
        fs::remove_dir_all(path)
            .map_err(|error| format!("Failed to delete {label} directory: {error}"))
    } else {
        fs::remove_file(path).map_err(|error| format!("Failed to delete {label} file: {error}"))
    }
}

pub(crate) fn staging_path_for(final_path: &Path, operation_id: &str) -> Result<PathBuf, String> {
    staging_path_with_salt(final_path, Some(operation_id))
}

pub(crate) fn resumable_staging_path_for(final_path: &Path) -> Result<PathBuf, String> {
    staging_path_with_salt(final_path, None)
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

pub(crate) fn promote_staged_path(
    staged_path: &Path,
    final_path: &Path,
    replace_existing: bool,
) -> Result<(), String> {
    if path_exists_no_follow(final_path) {
        if !replace_existing {
            return Err(conflict_for_existing_destination(final_path));
        }
        remove_path(final_path, "destination")?;
    }

    fs::rename(staged_path, final_path)
        .map_err(|error| format!("Failed to finish destination file: {error}"))
}

pub(crate) fn existing_file_matches_source(src: &Path, dst: &Path) -> Result<bool, String> {
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

    match (source.modified(), destination.modified()) {
        (Ok(source_modified), Ok(destination_modified)) => {
            Ok(source_modified == destination_modified)
        }
        _ => Ok(false),
    }
}
