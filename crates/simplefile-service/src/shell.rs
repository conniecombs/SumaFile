//! Shell integration: open files and reveal in Explorer.

use std::process::Command;

/// Open a file with its default associated application.
pub fn open_file(path: &str) -> Result<(), String> {
    let materialized = if simplefile_core::archive::is_archive_virtual_path(path) {
        simplefile_core::archive::materialize_archive_entry_to_temp(path)?
    } else {
        simplefile_core::archive::MaterializedSource::local(
            simplefile_core::utils::validate_path_no_follow(path)?,
        )
    };
    let path_buf = materialized.path();
    if path_buf.is_dir() {
        return Err("Cannot open a directory as a file".to_string());
    }

    opener::open(path_buf).map_err(|e| format!("Failed to open file: {e}"))
}

/// Reveal a file or folder in Windows Explorer, selecting it.
pub fn reveal_in_folder(path: &str) -> Result<(), String> {
    let materialized = if simplefile_core::archive::is_archive_virtual_path(path) {
        simplefile_core::archive::materialize_archive_entry_to_temp(path)?
    } else {
        simplefile_core::archive::MaterializedSource::local(
            simplefile_core::utils::validate_path_no_follow(path)?,
        )
    };
    Command::new("explorer.exe")
        .args(["/select,", &materialized.path().to_string_lossy()])
        .spawn()
        .map_err(|e| format!("Failed to reveal in folder: {e}"))?;
    Ok(())
}

/// Open an external URL in the default browser. Matches the Tauri opener
/// contract by accepting only http/https URLs.
pub fn open_external_url(url: &str) -> Result<(), String> {
    let url = url.trim();
    if !(url.starts_with("http://") || url.starts_with("https://")) {
        return Err("Unsupported URL scheme".to_string());
    }

    opener::open(url).map_err(|e| format!("Failed to open browser: {e}"))
}
