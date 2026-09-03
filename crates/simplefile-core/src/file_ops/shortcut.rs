use crate::path_conflict::path_exists_no_follow;
use crate::utils::{validate_existing_path_no_resolve, validate_name, validate_path_no_follow};
use serde::Deserialize;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShortcutRequest {
    pub path: String,
    pub name: String,
    pub target_path: String,
    #[serde(default)]
    pub arguments: Option<String>,
    #[serde(default)]
    pub working_directory: Option<String>,
    #[serde(default)]
    pub icon_path: Option<String>,
}

pub fn create_shortcut(request: ShortcutRequest) -> Result<String, String> {
    let parent = validate_existing_path_no_resolve(&request.path)?;
    if !parent.is_dir() {
        return Err("Shortcut destination must be a folder".to_string());
    }

    let name = normalize_shortcut_name(&request.name)?;
    let shortcut_path = parent.join(&name);
    if path_exists_no_follow(&shortcut_path) {
        return Err(format!("File already exists: {name}"));
    }

    let target = validate_path_no_follow(request.target_path.trim())?;
    let working_directory = optional_existing_directory(request.working_directory.as_deref())?;
    let icon_path = optional_existing_path(request.icon_path.as_deref())?;
    write_shortcut(
        &shortcut_path,
        &target,
        trimmed_optional(request.arguments.as_deref()).as_deref(),
        working_directory.as_deref(),
        icon_path.as_deref(),
    )?;

    Ok(shortcut_path.to_string_lossy().to_string())
}

fn normalize_shortcut_name(name: &str) -> Result<String, String> {
    let trimmed = name.trim();
    let normalized = if trimmed.to_ascii_lowercase().ends_with(".lnk") {
        trimmed.to_string()
    } else {
        format!("{trimmed}.lnk")
    };
    validate_name(&normalized)?;
    Ok(normalized)
}

fn trimmed_optional(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToOwned::to_owned)
}

fn optional_existing_path(value: Option<&str>) -> Result<Option<PathBuf>, String> {
    trimmed_optional(value)
        .map(|path| validate_path_no_follow(&path))
        .transpose()
}

fn optional_existing_directory(value: Option<&str>) -> Result<Option<PathBuf>, String> {
    let Some(path) = trimmed_optional(value) else {
        return Ok(None);
    };
    let path_buf = validate_existing_path_no_resolve(&path)?;
    if !path_buf.is_dir() {
        return Err(format!("Working directory is not a folder: {path}"));
    }

    Ok(Some(path_buf))
}

#[cfg(windows)]
fn write_shortcut(
    shortcut_path: &Path,
    target: &Path,
    arguments: Option<&str>,
    working_directory: Option<&Path>,
    icon_path: Option<&Path>,
) -> Result<(), String> {
    use windows::core::{Interface, HSTRING};
    use windows::Win32::Foundation::{RPC_E_CHANGED_MODE, S_FALSE, S_OK, TRUE};
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CoUninitialize, IPersistFile, CLSCTX_INPROC_SERVER,
        COINIT_APARTMENTTHREADED,
    };
    use windows::Win32::UI::Shell::{IShellLinkW, ShellLink};

    let init = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    let should_uninitialize = match init {
        S_OK | S_FALSE => true,
        RPC_E_CHANGED_MODE => false,
        error => {
            return Err(format!(
                "Failed to initialize Windows shortcut support: {error:?}"
            ))
        }
    };

    let result = (|| unsafe {
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)
            .map_err(|error| format!("Failed to create shortcut object: {error}"))?;
        link.SetPath(&hstring(target))
            .map_err(|error| format!("Failed to set shortcut target: {error}"))?;
        link.SetDescription(&HSTRING::from("SumaFile shortcut"))
            .map_err(|error| format!("Failed to set shortcut description: {error}"))?;

        if let Some(arguments) = arguments {
            link.SetArguments(&HSTRING::from(arguments))
                .map_err(|error| format!("Failed to set shortcut arguments: {error}"))?;
        }
        if let Some(working_directory) = working_directory {
            link.SetWorkingDirectory(&hstring(working_directory))
                .map_err(|error| format!("Failed to set shortcut working directory: {error}"))?;
        }
        if let Some(icon_path) = icon_path {
            link.SetIconLocation(&hstring(icon_path), 0)
                .map_err(|error| format!("Failed to set shortcut icon: {error}"))?;
        }

        let persist: IPersistFile = link
            .cast()
            .map_err(|error| format!("Failed to prepare shortcut save: {error}"))?;
        persist
            .Save(&hstring(shortcut_path), TRUE)
            .map_err(|error| format!("Failed to save shortcut: {error}"))
    })();

    if should_uninitialize {
        unsafe { CoUninitialize() };
    }

    result
}

#[cfg(windows)]
fn hstring(path: &Path) -> windows::core::HSTRING {
    windows::core::HSTRING::from(path.to_string_lossy().as_ref())
}

#[cfg(not(windows))]
fn write_shortcut(
    _shortcut_path: &Path,
    _target: &Path,
    _arguments: Option<&str>,
    _working_directory: Option<&Path>,
    _icon_path: Option<&Path>,
) -> Result<(), String> {
    Err("Windows shortcut creation is only supported on Windows".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(label: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time")
            .as_nanos();
        std::env::temp_dir().join(format!("sumafile-shortcut-{label}-{nanos}"))
    }

    #[test]
    fn shortcut_name_appends_lnk_and_preserves_existing_lnk() {
        assert_eq!(normalize_shortcut_name("Docs").unwrap(), "Docs.lnk");
        assert_eq!(normalize_shortcut_name("Docs.LNK").unwrap(), "Docs.LNK");
    }

    #[test]
    fn shortcut_creation_rejects_existing_name_before_write() {
        let dir = temp_dir("existing");
        fs::create_dir(&dir).expect("create temp dir");
        let target = dir.join("target.txt");
        let shortcut = dir.join("Target.lnk");
        fs::write(&target, b"target").expect("write target");
        fs::write(&shortcut, b"existing").expect("write shortcut");

        let error = create_shortcut(ShortcutRequest {
            path: dir.to_string_lossy().to_string(),
            name: "Target".to_string(),
            target_path: target.to_string_lossy().to_string(),
            arguments: None,
            working_directory: None,
            icon_path: None,
        })
        .expect_err("existing shortcut should be rejected");

        assert_eq!(error, "File already exists: Target.lnk");
        let _ = fs::remove_dir_all(dir);
    }

    #[cfg(windows)]
    #[test]
    fn creates_windows_shell_link_file() {
        let dir = temp_dir("create");
        fs::create_dir(&dir).expect("create temp dir");
        let target = dir.join("target.txt");
        fs::write(&target, b"target").expect("write target");

        let created = create_shortcut(ShortcutRequest {
            path: dir.to_string_lossy().to_string(),
            name: "Target".to_string(),
            target_path: target.to_string_lossy().to_string(),
            arguments: None,
            working_directory: Some(dir.to_string_lossy().to_string()),
            icon_path: None,
        })
        .expect("create shortcut");

        let bytes = fs::read(created).expect("read shortcut");
        assert_eq!(&bytes[..4], &[0x4c, 0, 0, 0]);
        let _ = fs::remove_dir_all(dir);
    }
}
