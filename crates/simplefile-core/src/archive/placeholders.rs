//! First-party plugin formats the core knows by name but cannot serve
//! itself. While no engine serves one, its capability row says why, and
//! every archive operation on such a file, or on a path inside one, fails
//! with that note instead of a generic "unsupported format" or "not a
//! directory" error. The file itself stays an ordinary file, so it can
//! still be copied, renamed or deleted.

use std::path::{Component, Path, PathBuf};

pub(super) struct PluginFormatPlaceholder {
    pub extension: &'static str,
    pub label: &'static str,
    pub note: &'static str,
}

pub(super) const PLUGIN_FORMAT_PLACEHOLDERS: &[PluginFormatPlaceholder] =
    &[PluginFormatPlaceholder {
        extension: ".7z",
        label: "7z",
        note: "7-Zip support requires the SumaFile 7-Zip plugin.",
    }];

/// True when no registered engine serves `placeholder`'s extension.
pub(super) fn is_unserved(placeholder: &PluginFormatPlaceholder) -> bool {
    super::engine::engine_for_path(Path::new(&format!("archive{}", placeholder.extension)))
        .is_none()
}

/// The note for a format `label` (`7z`) that no engine serves.
pub(super) fn unavailable_note_for_label(label: &str) -> Option<&'static str> {
    PLUGIN_FORMAT_PLACEHOLDERS
        .iter()
        .find(|placeholder| placeholder.label.eq_ignore_ascii_case(label))
        .filter(|placeholder| is_unserved(placeholder))
        .map(|placeholder| placeholder.note)
}

/// The note when `path`'s name ends with a placeholder extension (any case)
/// and no engine serves it. Nothing on disk is consulted.
pub(super) fn unavailable_note_for_path(path: &Path) -> Option<&'static str> {
    let name = path.file_name()?.to_string_lossy().to_lowercase();
    PLUGIN_FORMAT_PLACEHOLDERS
        .iter()
        .find(|placeholder| {
            name.len() > placeholder.extension.len() && name.ends_with(placeholder.extension)
        })
        .filter(|_| super::engine::engine_for_path(path).is_none())
        .map(|placeholder| placeholder.note)
}

/// The note when `path` is, or is inside, a file whose format only an
/// absent plugin serves. The first existing file along `path` decides:
/// an archive some engine or built-in format serves, or any other file,
/// gives `None`.
pub fn unavailable_archive_note(path: &str) -> Option<&'static str> {
    let mut prefix = PathBuf::new();
    for component in Path::new(path).components() {
        prefix.push(component.as_os_str());
        if matches!(component, Component::Normal(_)) && prefix.is_file() {
            if super::path::archive_kind_for_path(&prefix).is_some() {
                return None;
            }
            return unavailable_note_for_path(&prefix);
        }
    }
    None
}
