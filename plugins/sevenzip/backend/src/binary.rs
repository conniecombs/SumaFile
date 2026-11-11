//! Which 7-Zip executable the backend runs.
//!
//! An override (the plugin setting `binaryOverride`) that names an existing
//! file wins. An override folder resolves to the first of `7zz.exe`,
//! `7z.exe` and `7za.exe` inside it. Otherwise the backend uses the
//! `7za.exe` bundled beside it. There is no `SIMPLEFILE_7Z`, PATH or
//! Program Files fallback.

use std::path::{Path, PathBuf};

/// The plugin setting that names a 7-Zip executable, or a folder holding
/// one, to use instead of the bundled copy.
pub const BINARY_OVERRIDE_KEY: &str = "binaryOverride";

/// The bundled 7-Zip executable's file name.
pub const BUNDLED_BINARY: &str = "7za.exe";

/// Executables an override folder is searched for, in order.
const FOLDER_CANDIDATES: [&str; 3] = ["7zz.exe", "7z.exe", "7za.exe"];

/// The executable an override names: the file itself, or the first
/// candidate inside a folder. `None` for an empty value and for one that
/// names nothing usable.
pub fn override_target(override_value: &str) -> Option<PathBuf> {
    let trimmed = override_value.trim();
    if trimmed.is_empty() {
        return None;
    }
    let path = Path::new(trimmed);
    if path.is_file() {
        return Some(path.to_path_buf());
    }
    if path.is_dir() {
        return FOLDER_CANDIDATES
            .iter()
            .map(|name| path.join(name))
            .find(|candidate| candidate.is_file());
    }
    None
}

/// The executable to run: the override's target when it has one, else
/// `<backend_dir>\7za.exe`. A missing override falls back to the bundled
/// binary; the caller logs that.
pub fn resolve_binary(override_value: Option<&str>, backend_dir: &Path) -> Result<PathBuf, String> {
    if let Some(target) = override_value.and_then(override_target) {
        return Ok(target);
    }
    let bundled = backend_dir.join(BUNDLED_BINARY);
    if bundled.is_file() {
        return Ok(bundled);
    }
    Err(format!(
        "7-Zip is unavailable: the bundled 7za.exe is missing from {}. Reinstall SumaFile or set the 7-Zip executable override.",
        backend_dir.display()
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn override_file_folder_and_bundled_resolution() {
        let dir = tempfile::tempdir().unwrap();
        let backend = dir.path().join("backend");
        std::fs::create_dir_all(&backend).unwrap();
        let custom = dir.path().join("custom.exe");
        std::fs::write(&custom, b"x").unwrap();
        let folder = dir.path().join("tools");
        std::fs::create_dir_all(&folder).unwrap();
        std::fs::write(folder.join("7z.exe"), b"x").unwrap();
        std::fs::write(folder.join("7za.exe"), b"x").unwrap();

        // Neither an override nor the bundled binary: the exact error.
        assert_eq!(
            resolve_binary(None, &backend),
            Err(format!(
                "7-Zip is unavailable: the bundled 7za.exe is missing from {}. Reinstall SumaFile or set the 7-Zip executable override.",
                backend.display()
            ))
        );

        std::fs::write(backend.join("7za.exe"), b"x").unwrap();
        let bundled = backend.join("7za.exe");
        assert_eq!(
            resolve_binary(Some(custom.to_str().unwrap()), &backend),
            Ok(custom.clone())
        );
        assert_eq!(
            resolve_binary(Some(folder.to_str().unwrap()), &backend),
            Ok(folder.join("7z.exe"))
        );
        let missing = dir.path().join("missing.exe");
        assert_eq!(
            resolve_binary(Some(missing.to_str().unwrap()), &backend),
            Ok(bundled.clone())
        );
        assert_eq!(resolve_binary(Some("  "), &backend), Ok(bundled.clone()));
        assert_eq!(resolve_binary(None, &backend), Ok(bundled));
        assert_eq!(BINARY_OVERRIDE_KEY, "binaryOverride");
    }
}
