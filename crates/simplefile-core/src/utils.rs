use crate::models::FileEntry;
use chrono::{DateTime, Local};
use std::ffi::OsStr;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

pub fn hidden_command<S: AsRef<OsStr>>(program: S) -> Command {
    use std::os::windows::process::CommandExt;
    let mut command = Command::new(program);
    command.creation_flags(0x08000000);
    command
}

pub fn dirs_home() -> Result<String, String> {
    std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .map_err(|_| "Could not determine home directory".to_string())
}

pub fn format_system_time(time: std::time::SystemTime) -> String {
    let datetime: DateTime<Local> = time.into();
    datetime.format("%Y-%m-%d %H:%M").to_string()
}

fn format_modified(meta: &fs::Metadata) -> String {
    meta.modified()
        .map(format_system_time)
        .unwrap_or_else(|_| String::from("-"))
}

fn build_file_entry(
    path: &Path,
    is_dir: bool,
    is_symlink: bool,
    size: u64,
    modified: String,
    symlink_target: Option<String>,
) -> Option<FileEntry> {
    let name = path.file_name()?.to_string_lossy().to_string();
    let file_path = path.to_string_lossy().to_string();
    let extension = if is_dir {
        String::new()
    } else {
        path.extension()
            .map(|e| e.to_string_lossy().to_string())
            .unwrap_or_default()
    };

    Some(FileEntry {
        name,
        path: file_path,
        is_dir,
        is_symlink,
        size,
        modified,
        extension,
        permissions: None,
        symlink_target,
        git_status: None,
    })
}

/// Build a `FileEntry` from a path. Uses a single `symlink_metadata` call for
/// normal files/dirs. Only follows / `read_link`s when the node is a symlink.
pub fn get_file_entry(path: &PathBuf) -> Option<FileEntry> {
    let symlink_meta = fs::symlink_metadata(path).ok()?;
    let is_symlink = symlink_meta.file_type().is_symlink();

    // Properties / single-item info: follow symlink targets so size/type match
    // what users open. Listing uses DirEntry / FindFirstFile and never follows.
    let (is_dir, size, modified) = if is_symlink {
        match fs::metadata(path) {
            Ok(followed) => (
                followed.is_dir(),
                followed.len(),
                format_modified(&followed),
            ),
            Err(_) => (
                symlink_meta.is_dir(),
                symlink_meta.len(),
                format_modified(&symlink_meta),
            ),
        }
    } else {
        (
            symlink_meta.is_dir(),
            symlink_meta.len(),
            format_modified(&symlink_meta),
        )
    };

    let symlink_target = if is_symlink {
        fs::read_link(path)
            .ok()
            .map(|t| t.to_string_lossy().to_string())
    } else {
        None
    };

    build_file_entry(path, is_dir, is_symlink, size, modified, symlink_target)
}

/// Build a `FileEntry` from a `DirEntry` without re-opening the path for normal
/// files. `DirEntry::metadata()` reuses find-data on Windows; we only
/// `read_link` (never full follow-stat) when the entry is a symlink/reparse.
pub fn get_file_entry_from_dir_entry(entry: &fs::DirEntry) -> Option<FileEntry> {
    let path = entry.path();
    let file_type = entry.file_type().ok()?;
    let is_symlink = file_type.is_symlink();

    // Prefer DirEntry metadata (cheap / cached from enumeration).
    let meta = entry.metadata().ok()?;
    let is_dir = if is_symlink {
        // Symlink file_type is not a dir; directory symlinks still carry the
        // directory attribute on the reparse metadata on Windows.
        meta.is_dir() || file_type.is_dir()
    } else {
        file_type.is_dir() || meta.is_dir()
    };

    let symlink_target = if is_symlink {
        fs::read_link(&path)
            .ok()
            .map(|t| t.to_string_lossy().to_string())
    } else {
        None
    };

    build_file_entry(
        &path,
        is_dir,
        is_symlink,
        meta.len(),
        format_modified(&meta),
        symlink_target,
    )
}

/// True for UNC paths (`\\server\share`) and mapped network drive letters.
pub fn is_network_path(path: &Path) -> bool {
    let raw = path.to_string_lossy();
    let trimmed = raw.trim();
    if trimmed.starts_with("\\\\") || trimmed.starts_with("//") {
        return true;
    }

    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use winapi::um::fileapi::GetDriveTypeW;
        use winapi::um::winbase::DRIVE_REMOTE;

        let bytes = trimmed.as_bytes();
        if bytes.len() >= 2 && bytes[1] == b':' {
            let letter = bytes[0].to_ascii_uppercase() as char;
            if letter.is_ascii_alphabetic() {
                let root = format!("{letter}:\\");
                let wide: Vec<u16> = std::ffi::OsStr::new(&root)
                    .encode_wide()
                    .chain(std::iter::once(0))
                    .collect();
                let drive_type = unsafe { GetDriveTypeW(wide.as_ptr()) };
                return drive_type == DRIVE_REMOTE;
            }
        }
    }

    let _ = path;
    false
}

pub fn generate_operation_id() -> String {
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let count = COUNTER.fetch_add(1, Ordering::Relaxed);
    use std::time::{SystemTime, UNIX_EPOCH};
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    format!("op_{secs}_{count}")
}

/// Validate a path that must exist
pub fn validate_existing_path(path: &str) -> Result<PathBuf, String> {
    let path_buf = PathBuf::from(path);

    if !path_buf.exists() {
        return Err(format!("Path does not exist: {path}"));
    }

    // Canonicalize to resolve symlinks and ".." components
    path_buf
        .canonicalize()
        .map_err(|e| format!("Failed to resolve path: {e}"))
}

/// Validate a path that must exist while preserving the exact path supplied.
///
/// Some Windows filesystem targets can be opened and listed normally but fail
/// `canonicalize()` with OS error 1005. File-browser operations should keep the
/// supplied path intact instead of resolving it first.
pub fn validate_existing_path_no_resolve(path: &str) -> Result<PathBuf, String> {
    let path_buf = PathBuf::from(path);
    fs::metadata(&path_buf)
        .map_err(|e| format!("Path does not exist or is not accessible: {path} ({e})"))?;
    Ok(path_buf)
}

/// Validate a path that must exist **without following symlinks** (lstat).
///
/// Use this instead of `validate_existing_path` whenever the operation must
/// act on the symlink itself rather than its target — e.g. delete, rename,
/// move, or `get_entry_info`.  Canonicalising the path first would silently
/// redirect all of those operations to the symlink target, which:
///   • causes `remove_dir_all` to wipe the target directory contents instead
///     of unlinking the shortcut;
///   • causes `rename`/`rename` to move the target instead of the symlink;
///   • causes `get_file_entry` to report `is_symlink = false` because it
///     never sees the symlink node.
pub fn validate_path_no_follow(path: &str) -> Result<PathBuf, String> {
    let path_buf = PathBuf::from(path);
    // symlink_metadata (lstat) succeeds for both regular files *and* symlinks,
    // but does not resolve the link — so the returned PathBuf still points to
    // the symlink itself.
    fs::symlink_metadata(&path_buf).map_err(|_| format!("Path does not exist: {path}"))?;
    Ok(path_buf)
}

/// Validate a single Windows file/directory name.
pub fn validate_name(name: &str) -> Result<(), String> {
    if name.is_empty() || name.trim().is_empty() {
        return Err("Name cannot be empty".to_string());
    }

    if name == "." || name == ".." {
        return Err("Invalid name".to_string());
    }

    if name.ends_with(' ') || name.ends_with('.') {
        return Err("Invalid name: cannot end with a space or period".to_string());
    }

    if name.chars().any(is_windows_invalid_name_character) {
        return Err(
            "Invalid name: cannot contain Windows reserved characters or control characters"
                .to_string(),
        );
    }

    if is_windows_reserved_device_name(name) {
        return Err("Invalid name: reserved Windows device name".to_string());
    }

    Ok(())
}

fn is_windows_invalid_name_character(ch: char) -> bool {
    ch.is_control() || matches!(ch, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*')
}

fn is_windows_reserved_device_name(name: &str) -> bool {
    let base_name = name
        .split('.')
        .next()
        .unwrap_or(name)
        .trim_end_matches(' ')
        .to_ascii_uppercase();

    matches!(base_name.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || matches!(
            base_name.as_str(),
            "COM1"
                | "COM2"
                | "COM3"
                | "COM4"
                | "COM5"
                | "COM6"
                | "COM7"
                | "COM8"
                | "COM9"
                | "LPT1"
                | "LPT2"
                | "LPT3"
                | "LPT4"
                | "LPT5"
                | "LPT6"
                | "LPT7"
                | "LPT8"
                | "LPT9"
        )
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SymlinkTargetKind {
    File,
    Directory,
}

fn symlink_target_classification_path(source_path: &Path, link_target: &Path) -> PathBuf {
    if link_target.is_absolute() {
        link_target.to_path_buf()
    } else {
        source_path.parent().map_or_else(
            || link_target.to_path_buf(),
            |parent| parent.join(link_target),
        )
    }
}

fn classify_symlink_target(
    source_path: &Path,
    link_target: &Path,
) -> Result<SymlinkTargetKind, String> {
    let classification_path = symlink_target_classification_path(source_path, link_target);
    if let Ok(metadata) = fs::metadata(&classification_path) {
        return Ok(if metadata.is_dir() {
            SymlinkTargetKind::Directory
        } else {
            SymlinkTargetKind::File
        });
    }

    let source_metadata =
        fs::symlink_metadata(source_path).map_err(|e| format!("Failed to stat symlink: {e}"))?;
    Ok(if source_metadata.is_dir() {
        SymlinkTargetKind::Directory
    } else {
        SymlinkTargetKind::File
    })
}

pub fn recreate_symlink(source_path: &Path, dst_path: &Path) -> Result<(), String> {
    if let Some(parent) = dst_path.parent() {
        fs::create_dir_all(parent)
            .map_err(|e| format!("Failed to create parent directory: {e}"))?;
    }

    match fs::symlink_metadata(dst_path) {
        Ok(_) => {
            return Err(format!(
                "CONFLICT: destination already exists: {}",
                dst_path.to_string_lossy()
            ));
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(e) => return Err(format!("Failed to stat destination: {e}")),
    }

    let link_target =
        fs::read_link(source_path).map_err(|e| format!("Failed to read symlink target: {e}"))?;
    let target_kind = classify_symlink_target(source_path, &link_target)?;
    let result = match target_kind {
        SymlinkTargetKind::Directory => std::os::windows::fs::symlink_dir(&link_target, dst_path),
        SymlinkTargetKind::File => std::os::windows::fs::symlink_file(&link_target, dst_path),
    };

    result.map_err(|e| {
        if e.kind() == std::io::ErrorKind::AlreadyExists {
            format!(
                "CONFLICT: destination already exists: {}",
                dst_path.to_string_lossy()
            )
        } else if target_kind == SymlinkTargetKind::Directory {
            format!("Failed to create directory symlink: {e}")
        } else {
            format!("Failed to create file symlink: {e}")
        }
    })
}

fn should_cancel(
    cancel: &std::sync::atomic::AtomicBool,
    generation: Option<(&std::sync::atomic::AtomicU64, u64)>,
) -> bool {
    use std::sync::atomic::Ordering;

    cancel.load(Ordering::Relaxed)
        || generation.is_some_and(|(current, expected)| current.load(Ordering::Relaxed) != expected)
}

/// Count direct children under `path`, excluding the root directory itself.
/// Returns `None` if cancelled or superseded by a newer count request.
pub fn count_directory_entries(
    path: &Path,
    cancel: &std::sync::atomic::AtomicBool,
    generation: Option<(&std::sync::atomic::AtomicU64, u64)>,
) -> Option<u64> {
    let entries = std::fs::read_dir(path).ok()?;
    let mut count = 0u64;
    for entry in entries {
        if should_cancel(cancel, generation) {
            return None;
        }
        if entry.is_ok() {
            count += 1;
        }
    }
    Some(count)
}

/// Recursively count all entries under `path`, excluding the root directory itself.
/// Returns `None` if cancelled or superseded by a newer count request.
pub fn count_items_scoped(
    path: &Path,
    cancel: &std::sync::atomic::AtomicBool,
    generation: Option<(&std::sync::atomic::AtomicU64, u64)>,
) -> Option<u64> {
    let mut count = 0u64;
    let mut stack = vec![path.to_path_buf()];
    while let Some(current) = stack.pop() {
        if should_cancel(cancel, generation) {
            return None;
        }
        if let Ok(entries) = fs::read_dir(&current) {
            for entry in entries.flatten() {
                if should_cancel(cancel, generation) {
                    return None;
                }
                count += 1;
                let Ok(ft) = entry.file_type() else { continue };
                if ft.is_dir() {
                    stack.push(entry.path());
                }
            }
        }
    }
    Some(count)
}

#[cfg(test)]
mod tests {
    use super::{
        classify_symlink_target, recreate_symlink, symlink_target_classification_path,
        validate_name, SymlinkTargetKind,
    };
    use std::fs;
    use std::io::ErrorKind;
    use std::path::{Path, PathBuf};
    use std::time::{SystemTime, UNIX_EPOCH};

    fn test_temp_dir(label: &str) -> PathBuf {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "simplefile-{label}-{}-{unique}",
            std::process::id()
        ));
        fs::create_dir_all(&path).unwrap();
        path
    }

    fn remove_temp_dir(path: &Path) {
        let _ = fs::remove_dir_all(path);
    }

    #[cfg(windows)]
    fn symlink_unavailable(error: &std::io::Error) -> bool {
        error.kind() == ErrorKind::PermissionDenied || error.raw_os_error() == Some(1314)
    }

    #[cfg(windows)]
    fn try_symlink_file(target: &Path, link: &Path) -> bool {
        match std::os::windows::fs::symlink_file(target, link) {
            Ok(()) => true,
            Err(e) if symlink_unavailable(&e) => false,
            Err(e) => panic!("failed to create test file symlink: {e}"),
        }
    }

    #[cfg(windows)]
    fn try_symlink_dir(target: &Path, link: &Path) -> bool {
        match std::os::windows::fs::symlink_dir(target, link) {
            Ok(()) => true,
            Err(e) if symlink_unavailable(&e) => false,
            Err(e) => panic!("failed to create test directory symlink: {e}"),
        }
    }

    #[test]
    fn validate_name_rejects_windows_reserved_characters() {
        for name in [
            "bad<name.txt",
            "bad>name.txt",
            "bad:name.txt",
            "bad\"name.txt",
            "bad/name.txt",
            "bad\\name.txt",
            "bad|name.txt",
            "bad?name.txt",
            "bad*name.txt",
            "bad\u{0007}name.txt",
        ] {
            assert!(validate_name(name).is_err(), "{name} should be rejected");
        }
    }

    #[test]
    fn validate_name_rejects_windows_reserved_device_names() {
        for name in [
            "CON", "CON.txt", "PRN", "AUX", "NUL", "COM1", "COM9.log", "LPT1", "LPT9.txt",
        ] {
            assert!(validate_name(name).is_err(), "{name} should be rejected");
        }
    }

    #[test]
    fn validate_name_rejects_empty_dot_and_trailing_space_or_period() {
        for name in ["", "   ", ".", "..", "trailing.", "trailing "] {
            assert!(validate_name(name).is_err(), "{name:?} should be rejected");
        }
    }

    #[test]
    fn validate_name_allows_normal_windows_names() {
        for name in [
            "Report 2026.txt",
            "photo.final.jpg",
            "archive (1)",
            "name..with..dots.txt",
            " leading-space.txt",
        ] {
            assert!(validate_name(name).is_ok(), "{name} should be accepted");
        }
    }

    #[test]
    fn relative_symlink_targets_are_classified_from_source_parent() {
        let root = test_temp_dir("relative-symlink-classification");
        let source_parent = root.join("source");
        let unrelated_parent = root.join("unrelated");
        fs::create_dir_all(&source_parent).unwrap();
        fs::create_dir_all(&unrelated_parent).unwrap();
        fs::create_dir(source_parent.join("target")).unwrap();
        fs::write(unrelated_parent.join("target"), b"wrong target").unwrap();

        let source_link = source_parent.join("link");
        let raw_target = Path::new("target");

        assert_eq!(
            symlink_target_classification_path(&source_link, raw_target),
            source_parent.join("target")
        );
        assert_eq!(
            classify_symlink_target(&source_link, raw_target).unwrap(),
            SymlinkTargetKind::Directory
        );

        remove_temp_dir(&root);
    }

    #[cfg(windows)]
    #[test]
    fn recreate_symlink_preserves_relative_file_target() {
        let root = test_temp_dir("recreate-file-symlink");
        let source_dir = root.join("source");
        let dest_dir = root.join("destination");
        fs::create_dir_all(&source_dir).unwrap();
        fs::create_dir_all(&dest_dir).unwrap();
        fs::write(source_dir.join("target.txt"), b"target").unwrap();

        let source_link = source_dir.join("link.txt");
        if !try_symlink_file(Path::new("target.txt"), &source_link) {
            remove_temp_dir(&root);
            return;
        }

        let dest_link = dest_dir.join("link.txt");
        recreate_symlink(&source_link, &dest_link).unwrap();

        assert!(fs::symlink_metadata(&dest_link)
            .unwrap()
            .file_type()
            .is_symlink());
        assert_eq!(
            fs::read_link(&dest_link).unwrap(),
            PathBuf::from("target.txt")
        );

        remove_temp_dir(&root);
    }

    #[cfg(windows)]
    #[test]
    fn recreate_symlink_preserves_relative_directory_target() {
        let root = test_temp_dir("recreate-directory-symlink");
        let source_dir = root.join("source");
        let dest_dir = root.join("destination");
        fs::create_dir_all(source_dir.join("target-dir")).unwrap();
        fs::create_dir_all(dest_dir.join("target-dir")).unwrap();

        let source_link = source_dir.join("link-dir");
        if !try_symlink_dir(Path::new("target-dir"), &source_link) {
            remove_temp_dir(&root);
            return;
        }

        let dest_link = dest_dir.join("link-dir");
        recreate_symlink(&source_link, &dest_link).unwrap();

        assert!(fs::symlink_metadata(&dest_link)
            .unwrap()
            .file_type()
            .is_symlink());
        assert_eq!(
            fs::read_link(&dest_link).unwrap(),
            PathBuf::from("target-dir")
        );
        assert!(fs::metadata(&dest_link).unwrap().is_dir());

        remove_temp_dir(&root);
    }

    #[cfg(windows)]
    #[test]
    fn recreate_symlink_reports_conflict_when_destination_exists() {
        let root = test_temp_dir("recreate-symlink-conflict");
        let source_dir = root.join("source");
        let dest_dir = root.join("destination");
        fs::create_dir_all(&source_dir).unwrap();
        fs::create_dir_all(&dest_dir).unwrap();
        fs::write(source_dir.join("target.txt"), b"target").unwrap();

        let source_link = source_dir.join("link.txt");
        if !try_symlink_file(Path::new("target.txt"), &source_link) {
            remove_temp_dir(&root);
            return;
        }

        let dest_link = dest_dir.join("link.txt");
        fs::write(&dest_link, b"occupied").unwrap();

        let err = recreate_symlink(&source_link, &dest_link).unwrap_err();
        assert!(err.starts_with("CONFLICT: destination already exists: "));
        assert_eq!(fs::read_to_string(&dest_link).unwrap(), "occupied");

        remove_temp_dir(&root);
    }
}
