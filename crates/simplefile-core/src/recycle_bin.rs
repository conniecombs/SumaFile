//! Recycle Bin listing, restore, and empty for Windows `$Recycle.Bin`.

use crate::models::{DirectoryListing, FileEntry};
use crate::path_conflict::path_collision_key;
use crate::utils::{format_system_time, name_looks_hidden};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const RECYCLE_BIN_PATH: &str = "recycle-bin:";

pub fn is_recycle_bin_path(path: &str) -> bool {
    path.trim().eq_ignore_ascii_case(RECYCLE_BIN_PATH)
}

pub fn recycle_bin_entry() -> FileEntry {
    FileEntry {
        name: "Recycle Bin".to_string(),
        path: RECYCLE_BIN_PATH.to_string(),
        is_dir: true,
        is_symlink: false,
        is_hidden: false,
        is_system: false,
        size: 0,
        modified: "-".to_string(),
        extension: String::new(),
        permissions: None,
        symlink_target: None,
        git_status: None,
    }
}

pub fn list_recycle_bin() -> Result<DirectoryListing, String> {
    list_recycle_bin_from_roots(&recycle_bin_roots())
}

pub fn list_recycle_bin_from_roots(roots: &[PathBuf]) -> Result<DirectoryListing, String> {
    let mut entries = Vec::new();
    for root in roots {
        collect_sid_entries(root, &mut entries);
    }
    entries.sort_by_cached_key(|entry| {
        crate::native_accel::dirs_first_name_key(entry.is_dir, &entry.name)
    });
    Ok(DirectoryListing {
        path: RECYCLE_BIN_PATH.to_string(),
        parent: None,
        entries,
        is_network: false,
    })
}

pub fn restore_recycle_bin(paths: &[String]) -> Result<Vec<String>, String> {
    let mut restored = Vec::new();
    for path in paths {
        restored.push(restore_one(path)?);
    }
    Ok(restored)
}

pub fn empty_recycle_bin() -> Result<(), String> {
    empty_recycle_bin_from_roots(&recycle_bin_roots())
}

pub fn empty_recycle_bin_from_roots(roots: &[PathBuf]) -> Result<(), String> {
    let listing = list_recycle_bin_from_roots(roots)?;
    for entry in listing.entries {
        delete_recycle_item(&entry.path)?;
    }
    Ok(())
}

pub fn delete_recycle_item(path: &str) -> Result<(), String> {
    let data = PathBuf::from(path);
    let info = paired_info_path(&data);
    if data.is_dir() {
        fs::remove_dir_all(&data)
            .map_err(|error| format!("Failed to delete Recycle Bin item: {error}"))?;
    } else if data.exists() {
        fs::remove_file(&data)
            .map_err(|error| format!("Failed to delete Recycle Bin item: {error}"))?;
    }
    if info.exists() {
        let _ = fs::remove_file(&info);
    }
    Ok(())
}

pub(crate) fn recycle_bin_data_path_set() -> HashSet<String> {
    list_recycle_bin()
        .map(|listing| {
            listing
                .entries
                .into_iter()
                .map(|entry| path_collision_key(Path::new(&entry.path)))
                .collect()
        })
        .unwrap_or_default()
}

pub(crate) fn recycle_bin_paths_for_originals(
    original_paths: &[String],
    excluded_data_paths: &HashSet<String>,
) -> Vec<String> {
    list_recycle_bin()
        .map(|listing| {
            recycle_bin_paths_for_originals_from_entries(
                original_paths,
                &listing.entries,
                excluded_data_paths,
            )
        })
        .unwrap_or_default()
}

fn recycle_bin_paths_for_originals_from_entries(
    original_paths: &[String],
    entries: &[FileEntry],
    excluded_data_paths: &HashSet<String>,
) -> Vec<String> {
    let mut used_data_paths = HashSet::new();
    let mut matched = Vec::new();

    for original_path in original_paths {
        let original_key = path_collision_key(Path::new(original_path));
        if let Some(entry) = entries.iter().find(|entry| {
            let data_key = path_collision_key(Path::new(&entry.path));
            if excluded_data_paths.contains(&data_key) || used_data_paths.contains(&data_key) {
                return false;
            }

            entry
                .symlink_target
                .as_ref()
                .is_some_and(|target| path_collision_key(Path::new(target)) == original_key)
        }) {
            used_data_paths.insert(path_collision_key(Path::new(&entry.path)));
            matched.push(entry.path.clone());
        }
    }

    matched
}

pub fn paired_info_path(data_path: &Path) -> PathBuf {
    match data_path.file_name().and_then(|name| name.to_str()) {
        Some(name) if name.len() >= 2 && name.as_bytes()[1].eq_ignore_ascii_case(&b'R') => {
            let mut info_name = name.to_string();
            info_name.replace_range(1..2, "I");
            data_path.with_file_name(info_name)
        }
        _ => data_path.with_file_name("$I"),
    }
}

pub fn parse_recycle_info(bytes: &[u8]) -> Option<RecycleInfo> {
    if bytes.len() < 24 {
        return None;
    }
    let version = u64::from_le_bytes(bytes[0..8].try_into().ok()?);
    let size = u64::from_le_bytes(bytes[8..16].try_into().ok()?);
    let filetime = u64::from_le_bytes(bytes[16..24].try_into().ok()?);
    let original_path = if version >= 2 {
        if bytes.len() < 28 {
            return None;
        }
        let chars = u32::from_le_bytes(bytes[24..28].try_into().ok()?) as usize;
        let end = 28usize.saturating_add(chars.saturating_mul(2));
        if bytes.len() < end {
            return None;
        }
        utf16_lossy(&bytes[28..end])
    } else {
        utf16_lossy_nul(&bytes[24..])
    };
    if original_path.is_empty() {
        return None;
    }
    Some(RecycleInfo {
        size,
        original_path,
        deleted: filetime_to_system(filetime),
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RecycleInfo {
    pub size: u64,
    pub original_path: String,
    pub deleted: SystemTime,
}

fn restore_one(path: &str) -> Result<String, String> {
    let data = PathBuf::from(path);
    if !data.exists() {
        return Err(format!("Recycle Bin item is no longer available: {path}"));
    }
    let info_path = paired_info_path(&data);
    let bytes = fs::read(&info_path)
        .map_err(|error| format!("Could not read Recycle Bin info: {error}"))?;
    let parsed =
        parse_recycle_info(&bytes).ok_or_else(|| "Recycle Bin info is unreadable".to_string())?;
    let original = PathBuf::from(&parsed.original_path);
    if let Some(parent) = original.parent() {
        fs::create_dir_all(parent)
            .map_err(|error| format!("Could not recreate original folder: {error}"))?;
    }
    let destination = unique_destination(&original);
    rename_or_copy(&data, &destination)?;
    let _ = fs::remove_file(&info_path);
    Ok(destination.to_string_lossy().to_string())
}

fn unique_destination(path: &Path) -> PathBuf {
    if !path.exists() {
        return path.to_path_buf();
    }
    let parent = path.parent().unwrap_or(path);
    let stem = path
        .file_stem()
        .map(|value| value.to_string_lossy().to_string())
        .unwrap_or_else(|| "restored".to_string());
    let extension = path
        .extension()
        .map(|value| format!(".{}", value.to_string_lossy()))
        .unwrap_or_default();
    for index in 1..10_000 {
        let candidate = parent.join(format!("{stem} ({index}){extension}"));
        if !candidate.exists() {
            return candidate;
        }
    }
    path.to_path_buf()
}

fn rename_or_copy(source: &Path, destination: &Path) -> Result<(), String> {
    match fs::rename(source, destination) {
        Ok(()) => Ok(()),
        Err(_) => {
            if source.is_dir() {
                copy_dir_all(source, destination)?;
                fs::remove_dir_all(source).map_err(|error| {
                    format!("Restored, but could not remove Recycle Bin copy: {error}")
                })?;
            } else {
                fs::copy(source, destination)
                    .map_err(|error| format!("Failed to restore file: {error}"))?;
                fs::remove_file(source).map_err(|error| {
                    format!("Restored, but could not remove Recycle Bin copy: {error}")
                })?;
            }
            Ok(())
        }
    }
}

fn copy_dir_all(source: &Path, destination: &Path) -> Result<(), String> {
    fs::create_dir_all(destination)
        .map_err(|error| format!("Failed to restore folder: {error}"))?;
    for entry in
        fs::read_dir(source).map_err(|error| format!("Failed to restore folder: {error}"))?
    {
        let entry = entry.map_err(|error| format!("Failed to restore folder: {error}"))?;
        let next = destination.join(entry.file_name());
        if entry.path().is_dir() {
            copy_dir_all(&entry.path(), &next)?;
        } else {
            fs::copy(entry.path(), next)
                .map_err(|error| format!("Failed to restore file: {error}"))?;
        }
    }
    Ok(())
}

fn recycle_bin_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Ok(drives) = crate::drives::list_drives() {
        for drive in drives {
            let kind = drive.drive_type.to_ascii_lowercase();
            if kind == "network" || kind == "cd-rom" || kind == "optical" {
                continue;
            }
            if drive.drive_status != "available" {
                continue;
            }
            let mut root = PathBuf::from(&drive.path);
            root.push("$Recycle.Bin");
            if root.is_dir() {
                roots.push(root);
            }
        }
    }
    roots
}

fn collect_sid_entries(recycle_root: &Path, entries: &mut Vec<FileEntry>) {
    let Ok(sids) = fs::read_dir(recycle_root) else {
        return;
    };
    for sid in sids.flatten() {
        let sid_path = sid.path();
        if !sid_path.is_dir() {
            continue;
        }
        let Some(name) = sid_path.file_name().and_then(|value| value.to_str()) else {
            continue;
        };
        if !name.starts_with("S-1-") {
            continue;
        }
        let Ok(files) = fs::read_dir(&sid_path) else {
            continue;
        };
        for file in files.flatten() {
            let info_path = file.path();
            let Some(file_name) = info_path.file_name().and_then(|value| value.to_str()) else {
                continue;
            };
            if file_name.len() < 2 || !file_name.as_bytes()[1].eq_ignore_ascii_case(&b'I') {
                continue;
            }
            if !file_name.as_bytes()[0].eq_ignore_ascii_case(&b'$') {
                continue;
            }
            let Ok(bytes) = fs::read(&info_path) else {
                continue;
            };
            let Some(parsed) = parse_recycle_info(&bytes) else {
                continue;
            };
            let data_path = paired_data_path(&info_path);
            if !data_path.exists() {
                continue;
            }
            let original = PathBuf::from(&parsed.original_path);
            let display_name = original
                .file_name()
                .map(|value| value.to_string_lossy().to_string())
                .unwrap_or_else(|| parsed.original_path.clone());
            let is_dir = data_path.is_dir();
            let extension = if is_dir {
                String::new()
            } else {
                original
                    .extension()
                    .map(|value| value.to_string_lossy().to_string())
                    .unwrap_or_default()
            };
            entries.push(FileEntry {
                name: display_name,
                path: data_path.to_string_lossy().to_string(),
                is_dir,
                is_symlink: false,
                is_hidden: name_looks_hidden(&parsed.original_path),
                is_system: false,
                size: if is_dir { 0 } else { parsed.size },
                modified: format_system_time(parsed.deleted),
                extension,
                permissions: None,
                symlink_target: Some(parsed.original_path),
                git_status: None,
            });
        }
    }
}

fn paired_data_path(info_path: &Path) -> PathBuf {
    match info_path.file_name().and_then(|name| name.to_str()) {
        Some(name) if name.len() >= 2 => {
            let mut data_name = name.to_string();
            data_name.replace_range(1..2, "R");
            info_path.with_file_name(data_name)
        }
        _ => info_path.with_file_name("$R"),
    }
}

fn utf16_lossy(bytes: &[u8]) -> String {
    let units: Vec<u16> = bytes
        .as_chunks::<2>()
        .0
        .iter()
        .map(|chunk| u16::from_le_bytes(*chunk))
        .take_while(|unit| *unit != 0)
        .collect();
    String::from_utf16_lossy(&units)
}

fn utf16_lossy_nul(bytes: &[u8]) -> String {
    utf16_lossy(bytes)
}

fn filetime_to_system(filetime: u64) -> SystemTime {
    const EPOCH_DIFF: u64 = 116_444_736_000_000_000;
    if filetime <= EPOCH_DIFF {
        return UNIX_EPOCH;
    }
    let nanos = (filetime - EPOCH_DIFF).saturating_mul(100);
    UNIX_EPOCH + Duration::from_nanos(nanos)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn unique_temp(label: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "simplefile-recycle-{label}-{}-{nanos}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        path
    }

    fn write_info_v2(path: &Path, original: &str, size: u64) {
        let utf16: Vec<u16> = original.encode_utf16().chain(std::iter::once(0)).collect();
        let mut bytes = Vec::new();
        bytes.extend_from_slice(&2u64.to_le_bytes());
        bytes.extend_from_slice(&size.to_le_bytes());
        bytes.extend_from_slice(&132_000_000_000_000_000u64.to_le_bytes());
        bytes.extend_from_slice(&(utf16.len() as u32).to_le_bytes());
        for unit in utf16 {
            bytes.extend_from_slice(&unit.to_le_bytes());
        }
        fs::write(path, bytes).unwrap();
    }

    fn recycle_entry(data_path: &str, original_path: &str) -> FileEntry {
        FileEntry {
            name: Path::new(original_path)
                .file_name()
                .map(|value| value.to_string_lossy().to_string())
                .unwrap_or_else(|| original_path.to_string()),
            path: data_path.to_string(),
            is_dir: false,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: 0,
            modified: "-".to_string(),
            extension: String::new(),
            permissions: None,
            symlink_target: Some(original_path.to_string()),
            git_status: None,
        }
    }

    #[test]
    fn parse_version2_original_path() {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(&2u64.to_le_bytes());
        bytes.extend_from_slice(&12u64.to_le_bytes());
        bytes.extend_from_slice(&132_000_000_000_000_000u64.to_le_bytes());
        let name: Vec<u16> = "C:\\Users\\a\\notes.txt"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        bytes.extend_from_slice(&(name.len() as u32).to_le_bytes());
        for unit in name {
            bytes.extend_from_slice(&unit.to_le_bytes());
        }
        let parsed = parse_recycle_info(&bytes).unwrap();
        assert_eq!(parsed.size, 12);
        assert_eq!(parsed.original_path, r"C:\Users\a\notes.txt");
    }

    #[test]
    fn recycle_bin_path_matching_excludes_existing_duplicates() {
        let old = r"C:\$Recycle.Bin\S-1-5-21-1\$ROLD.txt";
        let new = r"C:\$Recycle.Bin\S-1-5-21-1\$RNEW.txt";
        let other = r"C:\$Recycle.Bin\S-1-5-21-1\$ROTHER.txt";
        let entries = vec![
            recycle_entry(old, r"C:\Users\a\notes.txt"),
            recycle_entry(other, r"C:\Users\a\other.txt"),
            recycle_entry(new, r"C:\Users\a\notes.txt"),
        ];
        let excluded = HashSet::from([path_collision_key(Path::new(old))]);

        let matched = recycle_bin_paths_for_originals_from_entries(
            &[r"C:\Users\a\notes.txt".to_string()],
            &entries,
            &excluded,
        );

        assert_eq!(matched, vec![new.to_string()]);
    }

    #[test]
    fn lists_and_restores_from_synthetic_roots() {
        let root = unique_temp("root");
        let sid = root.join("S-1-5-21-1");
        fs::create_dir_all(&sid).unwrap();
        let original_dir = unique_temp("dest");
        let original = original_dir.join("notes.txt");
        let info = sid.join("$Iabc.txt");
        let data = sid.join("$Rabc.txt");
        fs::write(&data, b"hello").unwrap();
        write_info_v2(&info, &original.to_string_lossy(), 5);

        let listing = list_recycle_bin_from_roots(std::slice::from_ref(&root)).unwrap();
        assert_eq!(listing.entries.len(), 1);
        assert_eq!(listing.entries[0].name, "notes.txt");
        assert_eq!(
            listing.entries[0].symlink_target.as_deref(),
            Some(original.to_string_lossy().as_ref())
        );

        let restored = restore_recycle_bin(&[data.to_string_lossy().to_string()]).unwrap();
        assert_eq!(restored.len(), 1);
        assert_eq!(fs::read(original).unwrap(), b"hello");
        assert!(!data.exists());
        assert!(!info.exists());

        let _ = fs::remove_dir_all(&root);
        let _ = fs::remove_dir_all(&original_dir);
    }

    #[test]
    fn empty_removes_info_and_data() {
        let root = unique_temp("empty");
        let sid = root.join("S-1-5-21-2");
        fs::create_dir_all(&sid).unwrap();
        let info = sid.join("$Izzz.bin");
        let data = sid.join("$Rzzz.bin");
        fs::write(&data, b"x").unwrap();
        write_info_v2(&info, r"C:\gone.bin", 1);
        empty_recycle_bin_from_roots(std::slice::from_ref(&root)).unwrap();
        assert!(!data.exists());
        assert!(!info.exists());
        let _ = fs::remove_dir_all(&root);
    }
}
