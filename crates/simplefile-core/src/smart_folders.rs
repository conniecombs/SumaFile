use crate::models::SmartFolder;
use crate::settings_store::app_data_dir;
use std::fs;
use std::path::PathBuf;

const SMART_FOLDERS_FILE: &str = "smart_folders.json";

pub fn load_smart_folders() -> Result<Vec<SmartFolder>, String> {
    let path = smart_folders_path()?;
    if !path.exists() {
        return Ok(Vec::new());
    }

    let content = fs::read_to_string(&path).map_err(|error| error.to_string())?;
    // A store that fails to parse is reported, never treated as empty: a
    // following save would otherwise rewrite the file and wipe every folder.
    serde_json::from_str(&content).map_err(|error| {
        format!(
            "Smart folders file is unreadable ({}): {error}",
            path.display()
        )
    })
}

pub fn save_smart_folder(folder: SmartFolder) -> Result<Vec<SmartFolder>, String> {
    validate_folder(&folder)?;

    let mut folders = load_smart_folders()?;
    if let Some(existing) = folders.iter_mut().find(|existing| existing.id == folder.id) {
        *existing = folder;
    } else {
        folders.push(folder);
    }

    write_smart_folders(&folders)?;
    Ok(folders)
}

pub fn delete_smart_folder(id: String) -> Result<Vec<SmartFolder>, String> {
    if id.trim().is_empty() {
        return Err("smart folder id cannot be empty".to_string());
    }

    let mut folders = load_smart_folders()?;
    folders.retain(|folder| folder.id != id);
    write_smart_folders(&folders)?;
    Ok(folders)
}

fn smart_folders_path() -> Result<PathBuf, String> {
    Ok(app_data_dir()?.join(SMART_FOLDERS_FILE))
}

fn write_smart_folders(folders: &[SmartFolder]) -> Result<(), String> {
    let path = smart_folders_path()?;
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }

    let content = serde_json::to_string_pretty(folders).map_err(|error| error.to_string())?;
    // Write to a sibling temp file and rename over the store so a crash
    // mid-write leaves the previous contents intact.
    let temp = path.with_extension("json.tmp");
    fs::write(&temp, content).map_err(|error| error.to_string())?;
    fs::rename(&temp, &path).map_err(|error| {
        let _ = fs::remove_file(&temp);
        error.to_string()
    })
}

fn validate_folder(folder: &SmartFolder) -> Result<(), String> {
    if folder.id.trim().is_empty() {
        return Err("smart folder id cannot be empty".to_string());
    }
    if folder.name.trim().is_empty() {
        return Err("smart folder name cannot be empty".to_string());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::SearchOptions;
    use std::ffi::OsString;
    use std::time::{SystemTime, UNIX_EPOCH};

    const APP_DATA_DIR_ENV: &str = "SIMPLEFILE_APP_DATA_DIR";

    struct DataDirGuard {
        original: Option<OsString>,
        dir: PathBuf,
    }

    impl DataDirGuard {
        fn new(label: &str) -> Self {
            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let dir = std::env::temp_dir().join(format!(
                "simplefile-smart-folders-{label}-{}-{nanos}",
                std::process::id()
            ));
            fs::create_dir_all(&dir).unwrap();
            let original = std::env::var_os(APP_DATA_DIR_ENV);
            std::env::set_var(APP_DATA_DIR_ENV, &dir);
            Self { original, dir }
        }
    }

    impl Drop for DataDirGuard {
        fn drop(&mut self) {
            match &self.original {
                Some(value) => std::env::set_var(APP_DATA_DIR_ENV, value),
                None => std::env::remove_var(APP_DATA_DIR_ENV),
            }
            let _ = fs::remove_dir_all(&self.dir);
        }
    }

    fn folder(id: &str) -> SmartFolder {
        let search_options: SearchOptions = serde_json::from_str(
            r#"{"query":"*.txt","search_path":"C:\\","case_sensitive":false,"include_hidden":false}"#,
        )
        .unwrap();
        SmartFolder {
            id: id.to_string(),
            name: format!("Folder {id}"),
            icon: None,
            search_options,
        }
    }

    #[test]
    fn corrupt_store_is_reported_not_treated_as_empty() {
        let _lock = crate::test_support::env_lock().lock().unwrap();
        let guard = DataDirGuard::new("corrupt");
        let path = guard.dir.join(SMART_FOLDERS_FILE);
        fs::write(&path, b"[{\"id\": \"a\", \"name\": ").unwrap();

        assert!(load_smart_folders().is_err());
        assert!(save_smart_folder(folder("b")).is_err());
        assert!(delete_smart_folder("a".to_string()).is_err());
        assert_eq!(
            fs::read(&path).unwrap(),
            b"[{\"id\": \"a\", \"name\": ",
            "a corrupt store must not be overwritten"
        );
    }

    #[test]
    fn save_round_trips_and_leaves_no_temp_file() {
        let _lock = crate::test_support::env_lock().lock().unwrap();
        let guard = DataDirGuard::new("roundtrip");

        let saved = save_smart_folder(folder("one")).unwrap();
        assert_eq!(saved.len(), 1);
        let saved = save_smart_folder(folder("two")).unwrap();
        assert_eq!(saved.len(), 2);
        assert_eq!(load_smart_folders().unwrap().len(), 2);

        let leftovers: Vec<_> = fs::read_dir(&guard.dir)
            .unwrap()
            .filter_map(|entry| entry.ok())
            .map(|entry| entry.file_name().to_string_lossy().to_string())
            .filter(|name| name != SMART_FOLDERS_FILE)
            .collect();
        assert!(leftovers.is_empty(), "{leftovers:?}");
    }
}
