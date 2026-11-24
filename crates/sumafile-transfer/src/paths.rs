//! Destination naming shared by transfer engines.

use crate::path_conflict::{path_collision_key, path_exists_no_follow};
use std::collections::HashSet;
use std::path::{Path, PathBuf};

/// The first `name (n).ext` beside `file_name` in `dest_dir` that neither
/// exists nor is already planned (`planned_destinations` holds collision keys).
pub fn unique_destination_path(
    dest_dir: &Path,
    file_name: &std::ffi::OsStr,
    planned_destinations: &HashSet<String>,
) -> Result<PathBuf, String> {
    let original = Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |stem| stem.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|extension| extension.to_string_lossy().to_string());

    for index in 1..10_000u32 {
        let candidate_name = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({index}).{ext}"),
            _ => format!("{stem} ({index})"),
        };
        let candidate = dest_dir.join(candidate_name);
        let candidate_key = path_collision_key(&candidate);
        if !path_exists_no_follow(&candidate) && !planned_destinations.contains(&candidate_key) {
            return Ok(candidate);
        }
    }

    Err(format!(
        "Could not choose a unique destination for {}",
        original.to_string_lossy()
    ))
}

/// Whether Windows reads `name` as a device, not a file: `CON`, `PRN`, `AUX`,
/// `NUL`, `COM1`-`COM9` and `LPT1`-`LPT9`, in any case, with any extension,
/// and with spaces before the dot (`con .txt`).
pub fn is_windows_reserved_device_name(name: &str) -> bool {
    let base = name
        .split('.')
        .next()
        .unwrap_or(name)
        .trim_end_matches(' ')
        .to_ascii_uppercase();
    if matches!(base.as_str(), "CON" | "PRN" | "AUX" | "NUL") {
        return true;
    }
    ["COM", "LPT"].iter().any(|prefix| {
        base.strip_prefix(prefix)
            .is_some_and(|digit| digit.len() == 1 && matches!(digit.as_bytes()[0], b'1'..=b'9'))
    })
}

#[cfg(test)]
mod tests {
    use super::is_windows_reserved_device_name;

    #[test]
    fn device_names_are_reserved_with_any_case_extension_or_padding() {
        for name in [
            "CON",
            "con",
            "CON.txt",
            "con .txt",
            "PRN",
            "AUX",
            "NUL",
            "nul.tar.gz",
            "COM1",
            "com9.log",
            "LPT1",
            "LPT9.txt",
        ] {
            assert!(is_windows_reserved_device_name(name), "{name}");
        }
        for name in [
            "CONSOLE",
            "con1",
            "COM0",
            "COM10",
            "LPT",
            "LPT0",
            "notes.con",
            "a.NUL",
            "",
            ".con",
            "COM\u{b9}",
        ] {
            assert!(!is_windows_reserved_device_name(name), "{name}");
        }
    }
}
