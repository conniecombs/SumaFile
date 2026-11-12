//! Path conflict helpers shared by the service, core and plugin transfer engines.

use std::fs;
use std::path::Path;

pub fn path_exists_no_follow(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok()
}

pub fn path_collision_key(path: &Path) -> String {
    path.to_string_lossy().to_lowercase()
}

pub fn paths_refer_to_same_entry(a: &Path, b: &Path) -> bool {
    if let (Ok(a_canonical), Ok(b_canonical)) = (a.canonicalize(), b.canonicalize()) {
        return path_collision_key(&a_canonical) == path_collision_key(&b_canonical);
    }

    path_collision_key(a) == path_collision_key(b)
}

pub fn is_keep_both_action(conflict_action: &str) -> bool {
    matches!(
        conflict_action.to_ascii_lowercase().as_str(),
        "rename" | "keep-both" | "keep_both"
    )
}

pub fn is_already_exists_error(error: &std::io::Error) -> bool {
    error.kind() == std::io::ErrorKind::AlreadyExists
        || matches!(error.raw_os_error(), Some(80 | 183))
}

/// A nul-terminated path for a raw Win32 call. Those calls stop at `MAX_PATH`
/// unless the path is verbatim (`\\?\`), and this process is not marked
/// long-path aware; `std::fs` converts long paths the same way, so a file it
/// wrote can otherwise not be renamed. Short paths are passed through as given.
#[cfg(windows)]
fn wide_path(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    use std::path::{Component, Prefix};

    /// Shortest limit among the Win32 path calls (directory creation).
    const LEGACY_LIMIT: usize = 248;
    let plain = || -> Vec<u16> { path.as_os_str().encode_wide().chain(Some(0)).collect() };
    if path.as_os_str().encode_wide().count() < LEGACY_LIMIT {
        return plain();
    }
    let Ok(absolute) = std::path::absolute(path) else {
        return plain();
    };
    let Some(Component::Prefix(prefix)) = absolute.components().next() else {
        return plain();
    };
    let units = absolute.as_os_str().encode_wide();
    let wide: Vec<u16> = match prefix.kind() {
        Prefix::Disk(_) => r"\\?\".encode_utf16().chain(units).collect(),
        // `\\server\share\..` becomes `\\?\UNC\server\share\..`.
        Prefix::UNC(..) => r"\\?\UNC\".encode_utf16().chain(units.skip(2)).collect(),
        // Already verbatim, or a device path.
        _ => return plain(),
    };
    wide.into_iter().chain(Some(0)).collect()
}

#[cfg(windows)]
pub fn rename_exclusive(source: &Path, destination: &Path) -> std::io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::MoveFileExW;

    let src_wide = wide_path(source);
    let dst_wide = wide_path(destination);
    // Flags = 0: omit MOVEFILE_REPLACE_EXISTING so Windows atomically refuses replacement.
    let ok = unsafe { MoveFileExW(src_wide.as_ptr(), dst_wide.as_ptr(), 0) };
    if ok != 0 {
        Ok(())
    } else {
        let err = std::io::Error::last_os_error();
        if is_already_exists_error(&err) {
            Err(std::io::Error::new(
                std::io::ErrorKind::AlreadyExists,
                err.to_string(),
            ))
        } else {
            Err(err)
        }
    }
}

#[cfg(not(windows))]
pub fn rename_exclusive(source: &Path, destination: &Path) -> std::io::Result<()> {
    if path_exists_no_follow(destination) {
        return Err(std::io::Error::new(
            std::io::ErrorKind::AlreadyExists,
            "destination already exists",
        ));
    }
    fs::rename(source, destination)
}

pub fn create_dir_exclusive(path: &Path) -> Result<(), String> {
    fs::create_dir(path).map_err(|error| {
        if is_already_exists_error(&error) {
            format!(
                "CONFLICT: destination already exists: {}",
                path.to_string_lossy()
            )
        } else {
            format!("Failed to create directory: {error}")
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn temp_path(name: &str) -> PathBuf {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("system time")
            .as_nanos();
        std::env::temp_dir().join(format!("sumafile-path-conflict-{name}-{nanos}"))
    }

    #[test]
    fn keep_both_aliases_are_case_insensitive() {
        assert!(is_keep_both_action("rename"));
        assert!(is_keep_both_action("KEEP-BOTH"));
        assert!(is_keep_both_action("keep_both"));
        assert!(!is_keep_both_action("replace"));
        assert!(!is_keep_both_action("skip"));
    }

    #[test]
    fn collision_key_case_folds_paths() {
        assert_eq!(
            path_collision_key(Path::new(r"C:\Temp\Report.TXT")),
            path_collision_key(Path::new(r"c:\temp\report.txt"))
        );
    }

    #[test]
    fn no_follow_existence_uses_symlink_metadata_semantics() {
        let path = temp_path("exists");
        fs::write(&path, b"sample").expect("write temp file");

        assert!(path_exists_no_follow(&path));
        assert!(!path_exists_no_follow(&path.with_extension("missing")));

        let _ = fs::remove_file(path);
    }

    #[test]
    fn exclusive_directory_reports_conflict_for_existing_path() {
        let path = temp_path("exclusive-dir");
        create_dir_exclusive(&path).expect("create temp dir");

        let error = create_dir_exclusive(&path).expect_err("existing path must conflict");
        assert!(error.starts_with("CONFLICT: destination already exists:"));

        let _ = fs::remove_dir(path);
    }

    /// The staging name pushes many ordinary destinations past 260 characters;
    /// the finalize rename has to work there like every other file call does.
    #[test]
    fn rename_exclusive_handles_paths_longer_than_max_path() {
        let root = temp_path("long");
        let mut dir = root.clone();
        while dir.as_os_str().len() < 250 {
            dir.push("nested-directory-name");
        }
        fs::create_dir_all(&dir).expect("create deep directory");
        let staged = dir.join(".photo.jpg.0123456789abcdef.sumafile-partial");
        let final_path = dir.join("photo.jpg");
        fs::write(&staged, b"data").expect("write staged file");

        rename_exclusive(&staged, &final_path).expect("rename beyond MAX_PATH");
        assert_eq!(fs::read(&final_path).expect("read renamed"), b"data");

        fs::write(&staged, b"second").expect("write staged file again");
        let error = rename_exclusive(&staged, &final_path).expect_err("must not replace");
        assert_eq!(error.kind(), std::io::ErrorKind::AlreadyExists);
        assert_eq!(fs::read(&final_path).expect("read kept"), b"data");

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn rename_exclusive_refuses_to_overwrite_existing_file() {
        let src = temp_path("rename-src");
        let dst = temp_path("rename-dst");
        fs::write(&src, b"new-data").expect("write src");
        fs::write(&dst, b"existing-data").expect("write dst");

        let error = rename_exclusive(&src, &dst).expect_err("existing destination must fail");
        assert_eq!(error.kind(), std::io::ErrorKind::AlreadyExists);
        assert_eq!(fs::read(&dst).expect("read dst"), b"existing-data");
        assert_eq!(fs::read(&src).expect("read src"), b"new-data");

        let _ = fs::remove_file(src);
        let _ = fs::remove_file(dst);
    }
}
