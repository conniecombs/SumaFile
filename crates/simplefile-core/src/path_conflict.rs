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

#[cfg(windows)]
pub fn rename_exclusive(source: &Path, destination: &Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use winapi::um::winbase::MoveFileExW;

    let src_wide: Vec<u16> = source
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let dst_wide: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
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
