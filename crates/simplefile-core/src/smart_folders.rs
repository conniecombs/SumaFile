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

    let content = fs::read_to_string(path).map_err(|error| error.to_string())?;
    Ok(serde_json::from_str(&content).unwrap_or_default())
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
    fs::write(path, content).map_err(|error| error.to_string())
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
