use crate::utils::validate_existing_path_no_resolve;
use std::path::{Component, Path, PathBuf};
use std::process::Command;

const DENIED_EXECUTABLE_NAMES: &[&str] = &[
    "bash",
    "bitsadmin",
    "certutil",
    "cmstp",
    "cmd",
    "control",
    "cscript",
    "dash",
    "fish",
    "forfiles",
    "installutil",
    "msbuild",
    "mshta",
    "msiexec",
    "node",
    "nodejs",
    "perl",
    "pwsh",
    "powershell",
    "python",
    "python3",
    "regasm",
    "regsvcs",
    "regsvr32",
    "rundll32",
    "ruby",
    "schtasks",
    "sh",
    "wmic",
    "wscript",
    "zsh",
];

const DENIED_TARGET_EXTENSIONS: &[&str] = &[
    "bat", "cmd", "com", "cpl", "dll", "exe", "hta", "inf", "ins", "iso", "jar", "js", "jse",
    "lnk", "msc", "msi", "msp", "pif", "ps1", "ps1xml", "ps2", "ps2xml", "psc1", "psc2", "reg",
    "scr", "sct", "sh", "sys", "vb", "vbe", "vbs", "ws", "wsc", "wsf", "wsh",
];

fn executable_stem(path: &Path) -> String {
    path.file_stem()
        .or_else(|| path.file_name())
        .map(|name| name.to_string_lossy().to_ascii_lowercase())
        .unwrap_or_default()
}

fn executable_name_is_denied(path: &Path) -> bool {
    let stem = executable_stem(path);
    DENIED_EXECUTABLE_NAMES.contains(&stem.as_str())
}

fn target_extension_is_denied(path: &Path) -> bool {
    path.extension()
        .map(|ext| ext.to_string_lossy().to_ascii_lowercase())
        .is_some_and(|ext| DENIED_TARGET_EXTENSIONS.contains(&ext.as_str()))
}

fn contains_path_separator(value: &str) -> bool {
    value.contains('/') || value.contains('\\')
}

fn normalized_windows_path(path: &Path) -> String {
    path.to_string_lossy()
        .replace('/', "\\")
        .trim_end_matches(['\\', '/'])
        .to_ascii_lowercase()
}

fn path_is_under_root(path: &Path, root: &Path) -> bool {
    let candidate = normalized_windows_path(path);
    let trusted = normalized_windows_path(root);
    candidate == trusted || candidate.starts_with(&format!("{trusted}\\"))
}

fn trusted_application_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Ok(value) = std::env::var("ProgramFiles") {
        let program_files = PathBuf::from(value);
        roots.push(program_files.join("WindowsApps"));
        roots.push(program_files);
    }
    if let Ok(value) = std::env::var("ProgramFiles(x86)") {
        roots.push(PathBuf::from(value));
    }
    if let Ok(value) = std::env::var("LOCALAPPDATA") {
        roots.push(PathBuf::from(value).join("Programs"));
    }
    if let Ok(value) = std::env::var("SystemRoot") {
        let system_root = PathBuf::from(value);
        roots.push(system_root.join("System32"));
        roots.push(system_root.join("SysWOW64"));
    }
    roots
}

fn is_trusted_application_root(path: &Path) -> bool {
    // Prefer a logical prefix check so WindowsApps paths stay trusted even when
    // canonicalize() is access-denied. Fall back to canonical comparison when it works.
    let roots = trusted_application_roots();
    if roots.iter().any(|root| path_is_under_root(path, root)) {
        return true;
    }
    if let Ok(canonical) = path.canonicalize() {
        return roots.iter().any(|root| {
            root.canonicalize()
                .map(|trusted| path_is_under_root(&canonical, &trusted))
                .unwrap_or(false)
        });
    }
    false
}

fn is_executable_file(path: &Path) -> bool {
    if !path.is_file() {
        return false;
    }

    matches!(
        path.extension()
            .map(|ext| ext.to_string_lossy().to_ascii_lowercase()),
        Some(ext) if ext == "exe" || ext == "com"
    )
}

fn candidate_with_platform_extensions(path: PathBuf) -> Vec<PathBuf> {
    if path.extension().is_some() {
        vec![path]
    } else {
        vec![path.with_extension("exe"), path.with_extension("com")]
    }
}

fn resolve_from_path(application: &str) -> Option<PathBuf> {
    if contains_path_separator(application) || application.contains(':') {
        return None;
    }

    let path_var = std::env::var_os("PATH")?;
    std::env::split_paths(&path_var)
        .flat_map(|dir| candidate_with_platform_extensions(dir.join(application)))
        .find(|candidate| is_executable_file(candidate))
}

fn has_parent_component(path: &Path) -> bool {
    path.components()
        .any(|component| matches!(component, Component::ParentDir))
}

fn resolve_allowed_application(application: &str) -> Result<PathBuf, String> {
    let application = application.trim();
    if application.is_empty() {
        return Err("Application cannot be empty".to_string());
    }

    let application_path = if contains_path_separator(application) || application.contains(':') {
        let path = PathBuf::from(application);
        if has_parent_component(&path) {
            return Err("Application path cannot contain '..' components".to_string());
        }
        path
    } else {
        resolve_from_path(application).ok_or_else(|| {
            format!(
                "Application '{application}' was not found on PATH. Choose an installed desktop application."
            )
        })?
    };

    if executable_name_is_denied(&application_path) {
        return Err("Shells and scripting runtimes are not allowed for Open With".to_string());
    }

    if !is_executable_file(&application_path) {
        return Err(format!(
            "Application is not an executable file: {}",
            application_path.to_string_lossy()
        ));
    }

    if !is_trusted_application_root(&application_path) {
        return Err(format!(
            "Application is outside trusted install locations: {}",
            application_path.to_string_lossy()
        ));
    }

    Ok(application_path)
}

/// Open a file using a specific trusted desktop application.
pub fn open_file_with(path: String, application: String) -> Result<(), String> {
    let target_path = resolve_readable_path(&path)?;
    if target_path.is_dir() {
        return Err("Open With is only supported for files".to_string());
    }
    if target_extension_is_denied(&target_path) {
        return Err("Open With does not allow executable or script payload files".to_string());
    }

    let application_path = resolve_allowed_application(&application)?;
    Command::new(&application_path)
        .arg(&target_path)
        .spawn()
        .map(|_| ())
        .map_err(|e| format!("failed to launch {}: {}", application_path.display(), e))
}

fn resolve_readable_path(path: &str) -> Result<PathBuf, String> {
    if crate::archive::is_archive_virtual_path(path) {
        return crate::archive::materialize_archive_entry_to_temp(path);
    }

    validate_existing_path_no_resolve(path)
}

#[cfg(test)]
mod tests {
    use super::{
        contains_path_separator, executable_name_is_denied, path_is_under_root,
        target_extension_is_denied,
    };
    use std::path::Path;

    #[test]
    fn denied_executable_names_include_shells_and_interpreters() {
        for name in ["cmd.exe", "powershell.exe", "python3", "node"] {
            assert!(executable_name_is_denied(Path::new(name)));
        }
    }

    #[test]
    fn denied_executable_names_include_common_lolbins() {
        for name in [
            "rundll32.exe",
            "mshta.exe",
            "regsvr32.exe",
            "certutil.exe",
            "wmic.exe",
            "bitsadmin.exe",
        ] {
            assert!(executable_name_is_denied(Path::new(name)));
        }
    }

    #[test]
    fn path_separator_detection_handles_all_platform_styles() {
        assert!(contains_path_separator("C:\\Program Files\\App\\app.exe"));
        assert!(contains_path_separator("/usr/bin/app"));
        assert!(!contains_path_separator("notepad"));
    }

    #[test]
    fn open_with_denies_executable_and_script_payload_extensions() {
        for name in [
            "payload.dll",
            "payload.hta",
            "installer.msi",
            "shortcut.lnk",
            "script.ps1",
            "program.exe",
        ] {
            assert!(target_extension_is_denied(Path::new(name)));
        }
        assert!(!target_extension_is_denied(Path::new("notes.txt")));
    }

    #[test]
    fn trusted_roots_cover_notepad_and_windowsapps_without_canonicalize() {
        assert!(path_is_under_root(
            Path::new(r"C:\Windows\System32\notepad.exe"),
            Path::new(r"C:\Windows\System32"),
        ));
        assert!(path_is_under_root(
            Path::new(r"C:\Program Files\WindowsApps\Microsoft.Windows.Photos\Photos.exe"),
            Path::new(r"C:\Program Files"),
        ));
        assert!(!path_is_under_root(
            Path::new(r"C:\Users\me\AppData\Roaming\payload.exe"),
            Path::new(r"C:\Program Files"),
        ));
        assert!(!path_is_under_root(
            Path::new(r"C:\Program Files Evil\app.exe"),
            Path::new(r"C:\Program Files"),
        ));
    }
}
