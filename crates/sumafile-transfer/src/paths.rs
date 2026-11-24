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
    unique_name(file_name, |candidate| {
        let candidate = dest_dir.join(candidate);
        path_exists_no_follow(&candidate)
            || planned_destinations.contains(&path_collision_key(&candidate))
    })
    .map(|name| dest_dir.join(name))
}

/// The first `name (n).ext` for `file_name`, counting from 1, that `taken`
/// does not report as in use. This is the "Keep both" naming rule, for a
/// place that is not a folder on a disk (the inside of an archive) as well.
pub fn unique_name(
    file_name: &std::ffi::OsStr,
    mut taken: impl FnMut(&str) -> bool,
) -> Result<String, String> {
    let original = Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |stem| stem.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|extension| extension.to_string_lossy().to_string());

    for index in 1..10_000u32 {
        let candidate = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({index}).{ext}"),
            _ => format!("{stem} ({index})"),
        };
        if !taken(&candidate) {
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
    use super::{is_windows_reserved_device_name, unique_name};
    use std::ffi::OsStr;

    #[test]
    fn a_unique_name_is_the_first_numbered_one_that_is_free() {
        let taken = ["report (1).txt", "report (2).txt", "notes (1)"];
        let free =
            |name: &str| unique_name(OsStr::new(name), |candidate| taken.contains(&candidate));

        assert_eq!(free("report.txt").unwrap(), "report (3).txt");
        assert_eq!(free("notes").unwrap(), "notes (2)");
        assert_eq!(free("archive.tar.gz").unwrap(), "archive.tar (1).gz");
        assert_eq!(free(".gitignore").unwrap(), ".gitignore (1)");
        assert!(unique_name(OsStr::new("always.txt"), |_| true).is_err());
    }

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
