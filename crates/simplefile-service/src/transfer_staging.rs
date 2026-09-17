use simplefile_core::path_conflict::path_exists_no_follow;
use std::fs;
use std::hash::{Hash, Hasher};
use std::io::{BufReader, Read};
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

pub(crate) fn promote_staged_path(
    staged_path: &Path,
    final_path: &Path,
    replace_existing: bool,
) -> Result<(), String> {
    if path_exists_no_follow(final_path) {
        if !replace_existing {
            return Err(conflict_for_existing_destination(final_path));
        }
        let backup_path = replacement_backup_path(final_path)?;
        fs::rename(final_path, &backup_path)
            .map_err(|error| format!("Failed to prepare destination replacement: {error}"))?;
        if let Err(error) = fs::rename(staged_path, final_path) {
            match fs::rename(&backup_path, final_path) {
                Ok(()) => {
                    return Err(format!(
                        "Failed to finish destination file; original destination was restored: {error}"
                    ));
                }
                Err(restore_error) => {
                    return Err(format!(
                        "Failed to finish destination file and could not restore original destination from {}: {error}; restore failed: {restore_error}",
                        backup_path.to_string_lossy()
                    ));
                }
            }
        }

        remove_path(&backup_path, "destination backup")?;
        return Ok(());
    }

    fs::rename(staged_path, final_path)
        .map_err(|error| format!("Failed to finish destination file: {error}"))
}

fn replacement_backup_path(final_path: &Path) -> Result<PathBuf, String> {
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
    use super::{existing_file_matches_source, promote_staged_path, resumable_staging_path_for};
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
