use crate::models::{ArchiveEntry, ArchiveInfo, DirectoryListing, FileEntry};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use super::path::{
    archive_entry_relative_path, archive_format_for_path, build_virtual_archive_path,
    normal_components, split_archive_path, zip_entry_relative_path, ArchiveFormat,
};

pub fn list_archive(path: String) -> Result<ArchiveInfo, String> {
    match archive_format_for_path(Path::new(&path)) {
        Some(ArchiveFormat::Zip) => list_zip_archive(&path),
        Some(ArchiveFormat::Tar) => list_tar_archive(&path, None),
        Some(ArchiveFormat::TarGz) => list_tar_archive(&path, Some("gz")),
        Some(ArchiveFormat::Rar) => list_rar_archive(&path),
        None => Err(format!(
            "Unsupported archive format: {}",
            Path::new(&path)
                .extension()
                .and_then(|e| e.to_str())
                .unwrap_or("")
        )),
    }
}

fn list_zip_archive(path: &str) -> Result<ArchiveInfo, String> {
    let file = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    let mut entries = Vec::new();
    let mut unsafe_entries = Vec::new();
    let mut total_size = 0u64;
    let mut compressed_size = 0u64;
    for i in 0..archive.len() {
        let file = archive.by_index(i).map_err(|e| e.to_string())?;
        let entry_path = file.name().to_string();
        if listing_entry_relative_path(ArchiveFormat::Zip, &entry_path).is_err() {
            unsafe_entries.push(entry_path);
            continue;
        }

        let entry = ArchiveEntry {
            name: file
                .name()
                .split('/')
                .next_back()
                .unwrap_or(file.name())
                .to_string(),
            path: entry_path,
            is_dir: file.is_dir(),
            size: file.size(),
            compressed_size: file.compressed_size(),
        };
        total_size += file.size();
        compressed_size += file.compressed_size();
        entries.push(entry);
    }
    Ok(ArchiveInfo {
        path: path.to_string(),
        format: "zip".to_string(),
        entries,
        unsafe_entries,
        total_size,
        compressed_size,
    })
}

fn list_tar_archive(path: &str, compression: Option<&str>) -> Result<ArchiveInfo, String> {
    let file = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let mut entries = Vec::new();
    let mut unsafe_entries = Vec::new();
    let mut total_size = 0u64;
    match compression {
        Some("gz") => {
            let decoder = flate2::read::GzDecoder::new(file);
            let mut archive = tar::Archive::new(decoder);
            for entry in archive.entries().map_err(|e| e.to_string())? {
                let entry = entry.map_err(|e| e.to_string())?;
                let path_str = entry
                    .path()
                    .map_err(|e| e.to_string())?
                    .to_string_lossy()
                    .to_string();
                let size = entry.size();
                if listing_entry_relative_path(ArchiveFormat::TarGz, &path_str).is_err() {
                    unsafe_entries.push(path_str);
                    continue;
                }
                total_size += size;
                entries.push(ArchiveEntry {
                    name: path_str
                        .split('/')
                        .next_back()
                        .unwrap_or(&path_str)
                        .to_string(),
                    path: path_str,
                    is_dir: entry.header().entry_type().is_dir(),
                    size,
                    compressed_size: size,
                });
            }
        }
        None => {
            let mut archive = tar::Archive::new(file);
            for entry in archive.entries().map_err(|e| e.to_string())? {
                let entry = entry.map_err(|e| e.to_string())?;
                let path_str = entry
                    .path()
                    .map_err(|e| e.to_string())?
                    .to_string_lossy()
                    .to_string();
                let size = entry.size();
                if listing_entry_relative_path(ArchiveFormat::Tar, &path_str).is_err() {
                    unsafe_entries.push(path_str);
                    continue;
                }
                total_size += size;
                entries.push(ArchiveEntry {
                    name: path_str
                        .split('/')
                        .next_back()
                        .unwrap_or(&path_str)
                        .to_string(),
                    path: path_str,
                    is_dir: entry.header().entry_type().is_dir(),
                    size,
                    compressed_size: size,
                });
            }
        }
        _ => return Err("Unsupported compression".to_string()),
    }
    let compressed_size = std::fs::metadata(path).map_or(0, |m| m.len());
    Ok(ArchiveInfo {
        path: path.to_string(),
        format: if compression.is_some() {
            "tar.gz".to_string()
        } else {
            "tar".to_string()
        },
        entries,
        unsafe_entries,
        total_size,
        compressed_size,
    })
}

fn list_rar_archive(path: &str) -> Result<ArchiveInfo, String> {
    let archive = unrar::Archive::new(path)
        .open_for_listing()
        .map_err(|e| format!("Failed to open RAR archive: {e}"))?;
    let mut entries = Vec::new();
    let mut unsafe_entries = Vec::new();
    let mut total_size = 0u64;
    for entry_result in archive {
        let entry = entry_result.map_err(|e| format!("Failed to read RAR entry: {e}"))?;
        let filename_str = entry.filename.to_string_lossy().to_string();
        if listing_entry_relative_path(ArchiveFormat::Rar, &filename_str).is_err() {
            unsafe_entries.push(filename_str);
            continue;
        }

        total_size += entry.unpacked_size;
        entries.push(ArchiveEntry {
            name: entry
                .filename
                .file_name()
                .map_or_else(|| filename_str.clone(), |n| n.to_string_lossy().to_string()),
            path: filename_str,
            is_dir: entry.is_directory(),
            size: entry.unpacked_size,
            compressed_size: entry.unpacked_size,
        });
    }
    let compressed_size = std::fs::metadata(path).map_or(0, |m| m.len());
    Ok(ArchiveInfo {
        path: path.to_string(),
        format: "rar".to_string(),
        entries,
        unsafe_entries,
        total_size,
        compressed_size,
    })
}

pub fn list_archive_directory(path: &str) -> Result<Option<DirectoryListing>, String> {
    let Some(parsed) = split_archive_path(path)? else {
        return Ok(None);
    };

    let archive_path_string = parsed.archive_path.to_string_lossy().to_string();
    let archive_info = list_archive(archive_path_string.clone())?;
    let current_parts = normal_components(&parsed.inner_path);
    let mut entries: BTreeMap<String, FileEntry> = BTreeMap::new();

    for entry in archive_info.entries {
        let Ok(relative_path) = listing_entry_relative_path(parsed.format, &entry.path) else {
            continue;
        };
        let entry_parts = normal_components(&relative_path);
        if entry_parts.len() <= current_parts.len()
            || !entry_parts
                .iter()
                .zip(current_parts.iter())
                .all(|(entry, current)| entry == current)
        {
            continue;
        }

        let child_name = entry_parts[current_parts.len()].clone();
        let key = child_name.to_string_lossy().to_lowercase();
        let mut child_relative = PathBuf::new();
        for part in current_parts.iter().chain(std::iter::once(&child_name)) {
            child_relative.push(part);
        }

        let is_dir = entry.is_dir || entry_parts.len() > current_parts.len() + 1;
        entries
            .entry(key)
            .and_modify(|existing| {
                existing.is_dir |= is_dir;
                if !is_dir {
                    existing.size = entry.size;
                }
            })
            .or_insert_with(|| {
                file_entry_for_archive_child(
                    &parsed.archive_path,
                    &child_relative,
                    is_dir,
                    entry.size,
                )
            });
    }

    let parent = if parsed.inner_path.as_os_str().is_empty() {
        parsed
            .archive_path
            .parent()
            .map(|p| p.to_string_lossy().to_string())
    } else {
        parsed
            .inner_path
            .parent()
            .map(|inner_parent| build_virtual_archive_path(&parsed.archive_path, inner_parent))
            .or_else(|| Some(parsed.archive_path.to_string_lossy().to_string()))
    };

    let mut entries: Vec<FileEntry> = entries.into_values().collect();
    entries.sort_by_cached_key(|e| crate::native_accel::dirs_first_name_key(e.is_dir, &e.name));

    Ok(Some(DirectoryListing {
        path: build_virtual_archive_path(&parsed.archive_path, &parsed.inner_path),
        parent,
        entries,
        is_network: false,
    }))
}

fn file_entry_for_archive_child(
    archive_path: &Path,
    child_relative: &Path,
    is_dir: bool,
    size: u64,
) -> FileEntry {
    let name = child_relative
        .file_name()
        .map(|name| name.to_string_lossy().to_string())
        .unwrap_or_default();
    let extension = if is_dir {
        String::new()
    } else {
        Path::new(&name)
            .extension()
            .map(|ext| ext.to_string_lossy().to_string())
            .unwrap_or_default()
    };
    let is_hidden = crate::utils::name_looks_hidden(&name);

    FileEntry {
        name,
        path: build_virtual_archive_path(archive_path, child_relative),
        is_dir,
        is_symlink: false,
        is_hidden,
        is_system: false,
        size: if is_dir { 0 } else { size },
        modified: "-".to_string(),
        extension,
        permissions: None,
        symlink_target: None,
        git_status: None,
    }
}

fn listing_entry_relative_path(format: ArchiveFormat, entry_path: &str) -> Result<PathBuf, String> {
    match format {
        ArchiveFormat::Zip => zip_entry_relative_path(entry_path),
        ArchiveFormat::Tar | ArchiveFormat::TarGz => {
            archive_entry_relative_path(Path::new(entry_path), "Tar")
        }
        ArchiveFormat::Rar => archive_entry_relative_path(Path::new(entry_path), "RAR"),
    }
}
