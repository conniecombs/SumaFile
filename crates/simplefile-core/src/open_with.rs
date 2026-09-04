use crate::open_with_policy_generated::{DENIED_EXECUTABLE_NAMES, DENIED_TARGET_EXTENSIONS};
use crate::utils::resolve_readable_path;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::process::{Child, Command};
use std::time::{Duration, SystemTime};

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
    if target_extension_is_denied(target_path.as_ref()) {
        return Err("Open With does not allow executable or script payload files".to_string());
    }

    let application_path = resolve_allowed_application(&application)?;
    // Archive materializations need the temp work root kept until the launched app exits.
    let (launch_path, cleanup_root) = target_path.into_open_with_handoff();
    let child = Command::new(&application_path)
        .arg(&launch_path)
        .spawn()
        .map_err(|e| format!("failed to launch {}: {}", application_path.display(), e))?;

    if let Some(root) = cleanup_root {
        // Best-effort orphan sweep for prior Open With temps left behind when the
        // service exited before a wait thread finished (or delete was locked).
        sweep_expired_archive_open_temps(ARCHIVE_OPEN_TEMP_MAX_AGE);
        schedule_materialized_temp_cleanup(child, root);
    }

    Ok(())
}

/// Age after which leftover `%TEMP%\SimpleFile\archive-open-*` dirs may be swept.
const ARCHIVE_OPEN_TEMP_MAX_AGE: Duration = Duration::from_secs(24 * 60 * 60);

const REMOVE_RETRY_ATTEMPTS: u32 = 8;
const REMOVE_RETRY_BASE_DELAY_MS: u64 = 250;

/// Wait for `child` to exit, then best-effort delete the archive materialization work root.
fn schedule_materialized_temp_cleanup(mut child: Child, cleanup_root: PathBuf) {
    let _ = std::thread::Builder::new()
        .name("sf-archive-open-cleanup".into())
        .spawn(move || {
            let _ = child.wait();
            remove_path_with_retry(&cleanup_root);
        });
}

/// Best-effort recursive delete with short retries for sharing/lock races.
fn remove_path_with_retry(path: &Path) {
    for attempt in 1..=REMOVE_RETRY_ATTEMPTS {
        if !path.exists() {
            return;
        }
        match fs::remove_dir_all(path) {
            Ok(()) => return,
            Err(_) if attempt < REMOVE_RETRY_ATTEMPTS => {
                let delay_ms = REMOVE_RETRY_BASE_DELAY_MS.saturating_mul(u64::from(attempt));
                std::thread::sleep(Duration::from_millis(delay_ms));
            }
            Err(_) => return,
        }
    }
}

fn archive_open_temp_base() -> PathBuf {
    std::env::temp_dir().join("SimpleFile")
}

fn is_archive_open_work_dir(name: &str) -> bool {
    name.starts_with("archive-open-")
}

/// Delete aged `archive-open-*` work roots under `%TEMP%\SimpleFile` (best-effort).
///
/// Only removes directories older than `max_age` so concurrent Open With launches
/// are not raced. Locked temps are left alone (`remove_dir_all` fails).
pub(crate) fn sweep_expired_archive_open_temps(max_age: Duration) {
    let base = archive_open_temp_base();
    let entries = match fs::read_dir(&base) {
        Ok(entries) => entries,
        Err(_) => return,
    };
    let now = SystemTime::now();
    for entry in entries.flatten() {
        let Ok(file_type) = entry.file_type() else {
            continue;
        };
        if !file_type.is_dir() {
            continue;
        }
        let name = entry.file_name();
        let Some(name) = name.to_str() else {
            continue;
        };
        if !is_archive_open_work_dir(name) {
            continue;
        }
        let path = entry.path();
        let Ok(modified) = entry
            .metadata()
            .and_then(|meta| meta.modified())
            .or_else(|_| fs::metadata(&path).and_then(|meta| meta.modified()))
        else {
            continue;
        };
        let Ok(age) = now.duration_since(modified) else {
            continue;
        };
        if age >= max_age {
            remove_path_with_retry(&path);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{
        contains_path_separator, executable_name_is_denied, is_archive_open_work_dir,
        path_is_under_root, remove_path_with_retry, schedule_materialized_temp_cleanup,
        sweep_expired_archive_open_temps, target_extension_is_denied,
    };
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::process::{Command, Stdio};
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

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

    fn unique_test_dir(label: &str) -> PathBuf {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "simplefile-open-with-test-{label}-{}-{unique}",
            std::process::id()
        ));
        fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn archive_open_work_dir_prefix_matches_unique_work_dir_naming() {
        assert!(is_archive_open_work_dir("archive-open-123-0"));
        assert!(!is_archive_open_work_dir("archive-source-123-0"));
        assert!(!is_archive_open_work_dir("other"));
    }

    #[test]
    fn remove_path_with_retry_deletes_work_root() {
        let root = unique_test_dir("remove-retry");
        let nested = root.join("nested");
        fs::create_dir_all(&nested).unwrap();
        fs::write(nested.join("file.txt"), b"data").unwrap();
        assert!(root.exists());
        remove_path_with_retry(&root);
        assert!(!root.exists(), "work root should be removed");
    }

    #[test]
    fn schedule_cleanup_deletes_temp_after_child_exits() {
        let root = unique_test_dir("cleanup-on-exit");
        fs::write(root.join("payload.txt"), b"open-with").unwrap();

        // Short-lived process we can wait on (not routed through Open With policy).
        let child = if cfg!(windows) {
            Command::new("cmd.exe")
                .args(["/C", "exit", "0"])
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .expect("spawn cmd")
        } else {
            Command::new("true")
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .expect("spawn true")
        };

        schedule_materialized_temp_cleanup(child, root.clone());

        let deadline = SystemTime::now() + Duration::from_secs(5);
        while root.exists() && SystemTime::now() < deadline {
            std::thread::sleep(Duration::from_millis(50));
        }
        assert!(
            !root.exists(),
            "archive-open work root must be removed after child exit"
        );
    }

    #[test]
    fn sweep_expired_archive_open_temps_only_removes_aged_open_dirs() {
        let base = std::env::temp_dir().join("SimpleFile");
        fs::create_dir_all(&base).unwrap();

        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let stale = base.join(format!("archive-open-stale-{unique}-0"));
        let fresh = base.join(format!("archive-open-fresh-{unique}-0"));
        let other = base.join(format!("archive-source-{unique}-0"));
        fs::create_dir_all(&stale).unwrap();
        fs::create_dir_all(&fresh).unwrap();
        fs::create_dir_all(&other).unwrap();
        fs::write(stale.join("a.txt"), b"stale").unwrap();
        fs::write(fresh.join("b.txt"), b"fresh").unwrap();
        fs::write(other.join("c.txt"), b"other").unwrap();

        // Force stale mtime into the past.
        let old = filetime::FileTime::from_unix_time(1_600_000_000, 0);
        filetime::set_file_mtime(&stale, old).expect("set stale mtime");

        sweep_expired_archive_open_temps(Duration::from_secs(60));

        assert!(!stale.exists(), "aged archive-open dir should be swept");
        assert!(fresh.exists(), "recent archive-open dir must be kept");
        assert!(other.exists(), "non-open work dirs must be untouched");

        let _ = fs::remove_dir_all(&fresh);
        let _ = fs::remove_dir_all(&other);
    }
}
