use crate::models::{ArchiveEntry, ArchiveInfo, DirectoryListing, FileEntry};
use std::collections::BTreeMap;
use std::ffi::{OsStr, OsString};
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::process::Stdio;
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ArchiveFormat {
    Zip,
    Tar,
    TarGz,
    Rar,
}

impl ArchiveFormat {
    fn label(self) -> &'static str {
        match self {
            ArchiveFormat::Zip => "zip",
            ArchiveFormat::Tar => "tar",
            ArchiveFormat::TarGz => "tar.gz",
            ArchiveFormat::Rar => "rar",
        }
    }
}

#[derive(Debug, Clone)]
pub struct ArchivePath {
    archive_path: PathBuf,
    inner_path: PathBuf,
    format: ArchiveFormat,
}

fn archive_format_for_path(path: &Path) -> Option<ArchiveFormat> {
    let name = path.file_name()?.to_string_lossy().to_lowercase();
    if name.ends_with(".tar.gz") || name.ends_with(".tgz") {
        return Some(ArchiveFormat::TarGz);
    }
    match path
        .extension()
        .and_then(|e| e.to_str())
        .map(str::to_ascii_lowercase)
        .as_deref()
    {
        Some("zip") => Some(ArchiveFormat::Zip),
        Some("tar") => Some(ArchiveFormat::Tar),
        Some("rar") => Some(ArchiveFormat::Rar),
        _ => None,
    }
}

pub fn split_archive_path(path: &str) -> Result<Option<ArchivePath>, String> {
    let input = PathBuf::from(path);
    let mut archive_path = PathBuf::new();
    let mut inner_path = PathBuf::new();
    let mut found_format = None;

    for component in input.components() {
        if let Some(format) = found_format {
            match component {
                Component::Normal(part) => inner_path.push(part),
                Component::CurDir => {}
                Component::ParentDir | Component::Prefix(_) | Component::RootDir => {
                    return Err(format!("Archive entry has unsafe path: {path}"));
                }
            }
            found_format = Some(format);
            continue;
        }

        archive_path.push(component.as_os_str());
        if archive_path.is_file() {
            found_format = archive_format_for_path(&archive_path);
        }
    }

    Ok(found_format.map(|format| ArchivePath {
        archive_path,
        inner_path,
        format,
    }))
}

pub fn is_archive_virtual_path(path: &str) -> bool {
    split_archive_path(path)
        .ok()
        .flatten()
        .is_some_and(|parsed| !parsed.inner_path.as_os_str().is_empty())
}

fn normal_components(path: &Path) -> Vec<OsString> {
    path.components()
        .filter_map(|component| match component {
            Component::Normal(part) => Some(part.to_os_string()),
            _ => None,
        })
        .collect()
}

fn build_virtual_archive_path(archive_path: &Path, inner_path: &Path) -> String {
    let mut path = archive_path.to_string_lossy().to_string();
    if !inner_path.as_os_str().is_empty() {
        if !path.ends_with(['\\', '/']) {
            path.push(std::path::MAIN_SEPARATOR);
        }
        path.push_str(&inner_path.to_string_lossy());
    }
    path
}

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

    FileEntry {
        name,
        path: build_virtual_archive_path(archive_path, child_relative),
        is_dir,
        is_symlink: false,
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

pub fn extract_archive(archive_path: String, destination: String) -> Result<(), String> {
    let archive = Path::new(&archive_path);
    let dest = Path::new(&destination);
    create_dir_all(dest)?;
    extract_archive_to_directory(archive, dest)
}

fn extract_archive_to_directory(archive: &Path, dest: &Path) -> Result<(), String> {
    let archive_path = archive.to_string_lossy();
    match archive_format_for_path(archive) {
        Some(ArchiveFormat::Zip) => extract_zip(&archive_path, dest),
        Some(ArchiveFormat::Tar) => extract_tar(&archive_path, dest, None),
        Some(ArchiveFormat::TarGz) => extract_tar(&archive_path, dest, Some("gz")),
        Some(ArchiveFormat::Rar) => extract_rar(&archive_path, dest),
        None => Err(format!(
            "Unsupported archive format: {}",
            archive.extension().and_then(|e| e.to_str()).unwrap_or("")
        )),
    }
}

fn extract_zip(path: &str, dest: &Path) -> Result<(), String> {
    let file = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;

    let mut planned_paths = Vec::with_capacity(archive.len());
    for i in 0..archive.len() {
        let file = archive.by_index(i).map_err(|e| e.to_string())?;
        planned_paths.push(zip_entry_relative_path(file.name())?);
    }

    let root_remap = top_level_remap(&dest_canonical, &planned_paths);
    for (i, relative_path) in planned_paths.iter().enumerate() {
        let mut file = archive.by_index(i).map_err(|e| e.to_string())?;
        let outpath = output_path_for_entry(&dest_canonical, relative_path, root_remap.as_ref());
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if file.is_dir() {
            create_dir_all(&outpath)?;
        } else {
            if let Some(parent) = outpath.parent() {
                create_dir_all(parent)?;
            }
            let final_outpath = unique_file_path_if_needed(&outpath);
            ensure_extract_path_within_destination(&dest_canonical, &final_outpath)?;
            let mut outfile = create_file(&final_outpath)?;
            copy_entry_data(&mut file, &mut outfile, &final_outpath)?;
        }
    }
    Ok(())
}

fn extract_tar(path: &str, dest: &Path, compression: Option<&str>) -> Result<(), String> {
    use std::io::Read;
    let file = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;

    fn extract_tar_entries<R: Read>(
        archive: &mut tar::Archive<R>,
        dest_canonical: &Path,
    ) -> Result<(), String> {
        for entry_result in archive.entries().map_err(|e| e.to_string())? {
            let mut entry = entry_result.map_err(|e| e.to_string())?;
            let entry_path = entry.path().map_err(|e| e.to_string())?.into_owned();

            let relative_path = archive_entry_relative_path(&entry_path, "Tar")?;
            let outpath = dest_canonical.join(&relative_path);
            ensure_extract_path_within_destination(dest_canonical, &outpath)?;
            let entry_type = entry.header().entry_type();

            // Only extract regular files and directories. Symlinks, hard links,
            // and special device nodes are skipped so archives cannot plant
            // out-of-tree link targets during extraction.
            if entry_type.is_dir() {
                create_dir_all(&outpath)?;
            } else if entry_type.is_file() {
                if let Some(parent) = outpath.parent() {
                    create_dir_all(parent)?;
                }
                let final_outpath = unique_file_path_if_needed(&outpath);
                ensure_extract_path_within_destination(dest_canonical, &final_outpath)?;
                entry.unpack(&final_outpath).map_err(|e| {
                    format!(
                        "Failed to extract tar entry to {}: {}",
                        final_outpath.display(),
                        e
                    )
                })?;
            }
        }
        Ok(())
    }

    match compression {
        Some("gz") => {
            let decoder = flate2::read::GzDecoder::new(file);
            let mut archive = tar::Archive::new(decoder);
            extract_tar_entries(&mut archive, &dest_canonical)?;
        }
        None => {
            let mut archive = tar::Archive::new(file);
            extract_tar_entries(&mut archive, &dest_canonical)?;
        }
        _ => return Err("Unsupported compression".to_string()),
    }
    Ok(())
}

fn extract_rar(path: &str, dest: &Path) -> Result<(), String> {
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;
    let mut archive = unrar::Archive::new(path)
        .open_for_processing()
        .map_err(|e| format!("Failed to open RAR for extraction: {e}"))?;
    while let Some(header) = archive
        .read_header()
        .map_err(|e| format!("Failed to read RAR header: {e}"))?
    {
        let entry_path = header.entry().filename.clone();
        let outpath = dest_canonical.join(archive_entry_relative_path(&entry_path, "RAR")?);
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if header.entry().is_directory() {
            create_dir_all(&outpath)?;
            archive = header
                .skip()
                .map_err(|e| format!("Failed to skip RAR directory: {e}"))?;
        } else {
            if let Some(parent) = outpath.parent() {
                create_dir_all(parent)?;
            }
            let final_outpath = unique_file_path_if_needed(&outpath);
            ensure_extract_path_within_destination(&dest_canonical, &final_outpath)?;
            archive = header
                .extract_to(&final_outpath)
                .map_err(|e| format!("Failed to extract RAR entry: {e}"))?;
        }
    }
    Ok(())
}

fn zip_entry_relative_path(entry_name: &str) -> Result<PathBuf, String> {
    archive_entry_relative_path_from_name(entry_name, "Zip")
}

fn archive_entry_relative_path(entry_path: &Path, archive_type: &str) -> Result<PathBuf, String> {
    archive_entry_relative_path_from_name(&entry_path.to_string_lossy(), archive_type)
}

fn archive_entry_relative_path_from_name(
    entry_name: &str,
    archive_type: &str,
) -> Result<PathBuf, String> {
    if entry_name.contains('\0') {
        return Err(format!(
            "{archive_type} entry has unsafe path: {entry_name}"
        ));
    }

    let normalized = entry_name.replace('\\', "/");
    if normalized.starts_with('/') {
        return Err(format!(
            "{archive_type} entry has unsafe path: {entry_name}"
        ));
    }

    let mut relative_path = PathBuf::new();
    for part in normalized.split('/') {
        if part.is_empty() || part == "." {
            continue;
        }
        if part == ".."
            || is_windows_special_component(part)
            || crate::utils::validate_name(part).is_err()
        {
            return Err(format!(
                "{archive_type} entry has unsafe path: {entry_name}"
            ));
        }
        relative_path.push(part);
    }

    if relative_path.as_os_str().is_empty() {
        return Err(format!(
            "{archive_type} entry has unsafe path: {entry_name}"
        ));
    }

    Ok(relative_path)
}

/// Belt-and-suspenders check: after joining a validated relative entry onto the
/// canonical destination, refuse any output path that escapes the extract root.
///
/// Uses a case-insensitive prefix compare on Windows so mixed-case dest paths
/// cannot bypass the guard, and requires a path-separator boundary so
/// `C:\dest-evil` is not treated as inside `C:\dest`.
fn ensure_extract_path_within_destination(dest: &Path, candidate: &Path) -> Result<(), String> {
    let dest_key = path_prefix_key(dest);
    let candidate_key = path_prefix_key(candidate);

    if candidate_key == dest_key {
        return Ok(());
    }

    let dest_with_sep = if dest_key.ends_with(['\\', '/']) {
        dest_key.clone()
    } else {
        format!("{dest_key}\\")
    };

    // Compare with both separators normalized so mixed slash styles still match.
    let dest_norm = dest_with_sep.replace('/', "\\");
    let candidate_norm = candidate_key.replace('/', "\\");

    if candidate_norm.starts_with(&dest_norm) {
        return Ok(());
    }

    Err(format!(
        "Archive entry escapes destination: {}",
        candidate.display()
    ))
}

fn path_prefix_key(path: &Path) -> String {
    path.to_string_lossy().to_lowercase()
}

fn is_windows_special_component(part: &str) -> bool {
    part.contains(':')
}

fn top_level_remap(dest: &Path, relative_paths: &[PathBuf]) -> Option<(OsString, OsString)> {
    let mut top_level: Option<OsString> = None;
    for relative_path in relative_paths {
        let component = first_normal_component(relative_path)?;
        if let Some(current) = &top_level {
            if current != &component {
                return None;
            }
        } else {
            top_level = Some(component);
        }
    }

    let original = top_level?;
    let existing_path = dest.join(&original);
    if !existing_path.exists() {
        return None;
    }

    let replacement = unique_sibling_path(&existing_path)
        .file_name()?
        .to_os_string();
    Some((original, replacement))
}

fn first_normal_component(path: &Path) -> Option<OsString> {
    path.components().find_map(|component| match component {
        Component::Normal(part) => Some(part.to_os_string()),
        _ => None,
    })
}

fn output_path_for_entry(
    dest: &Path,
    relative_path: &Path,
    root_remap: Option<&(OsString, OsString)>,
) -> PathBuf {
    let rewritten = root_remap.map_or_else(
        || relative_path.to_path_buf(),
        |(original, replacement)| rewrite_first_component(relative_path, original, replacement),
    );
    dest.join(rewritten)
}

fn rewrite_first_component(path: &Path, original: &OsStr, replacement: &OsStr) -> PathBuf {
    let mut rewritten = PathBuf::new();
    let mut replaced = false;
    for component in path.components() {
        if let Component::Normal(part) = component {
            if !replaced && part == original {
                rewritten.push(replacement);
                replaced = true;
            } else {
                rewritten.push(part);
            }
        }
    }
    rewritten
}

fn unique_file_path_if_needed(path: &Path) -> PathBuf {
    if !path.exists() {
        return path.to_path_buf();
    }
    unique_sibling_path(path)
}

fn unique_sibling_path(path: &Path) -> PathBuf {
    let parent = path.parent().unwrap_or_else(|| Path::new(""));
    let file_name = path.file_name().unwrap_or_else(|| OsStr::new("extracted"));
    let original = Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |s| s.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|e| e.to_string_lossy().to_string());

    for i in 1..10_000u32 {
        let candidate_name = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({i}).{ext}"),
            _ => format!("{stem} ({i})"),
        };
        let candidate = parent.join(candidate_name);
        if !candidate.exists() {
            return candidate;
        }
    }

    path.to_path_buf()
}

fn create_dir_all(path: &Path) -> Result<(), String> {
    std::fs::create_dir_all(path)
        .map_err(|e| format!("Failed to create directory {}: {}", path.display(), e))
}

fn create_file(path: &Path) -> Result<std::fs::File, String> {
    std::fs::File::create(path)
        .map_err(|e| format!("Failed to create extracted file {}: {}", path.display(), e))
}

fn copy_entry_data<R: std::io::Read, W: std::io::Write>(
    reader: &mut R,
    writer: &mut W,
    path: &Path,
) -> Result<(), String> {
    std::io::copy(reader, writer)
        .map(|_| ())
        .map_err(|e| format!("Failed to write extracted file {}: {}", path.display(), e))
}

pub fn should_handle_transfer(source: &str, destination: &str) -> Result<bool, String> {
    let source_is_archive_entry =
        split_archive_path(source)?.is_some_and(|parsed| !parsed.inner_path.as_os_str().is_empty());
    let destination_is_archive = split_archive_path(destination)?.is_some();
    Ok(source_is_archive_entry || destination_is_archive)
}

pub fn copy_entry_resolved(
    source: String,
    destination: String,
    conflict_action: String,
) -> Result<String, String> {
    let source_archive =
        split_archive_path(&source)?.filter(|parsed| !parsed.inner_path.as_os_str().is_empty());
    let destination_archive = split_archive_path(&destination)?;

    match (source_archive, destination_archive) {
        (Some(source_parsed), Some(destination_parsed))
            if same_archive_path(
                &source_parsed.archive_path,
                &destination_parsed.archive_path,
            ) =>
        {
            let result =
                mutate_archive(&source_parsed.archive_path, source_parsed.format, |root| {
                    let source_path = root.join(&source_parsed.inner_path);
                    let dest_dir = root.join(&destination_parsed.inner_path);
                    copy_with_conflict(&source_path, &dest_dir, &conflict_action)
                })?;
            Ok(result.unwrap_or_else(|| format!("SKIPPED:{source}")))
        }
        (source_parsed, Some(destination_parsed)) => {
            let materialized = materialize_transfer_source(&source, source_parsed.as_ref())?;
            let result = mutate_archive(
                &destination_parsed.archive_path,
                destination_parsed.format,
                |root| {
                    let dest_dir = root.join(&destination_parsed.inner_path);
                    copy_with_conflict(&materialized.path, &dest_dir, &conflict_action)
                },
            );
            materialized.cleanup();
            let result = result?;
            Ok(result.unwrap_or_else(|| format!("SKIPPED:{source}")))
        }
        (Some(source_parsed), None) => {
            copy_archive_entry_to_local(&source_parsed, &destination, &conflict_action)
        }
        (None, None) => Err("No archive path was involved in the copy operation".to_string()),
    }
}

pub fn move_entry_resolved(
    source: String,
    destination: String,
    conflict_action: String,
) -> Result<String, String> {
    let source_archive =
        split_archive_path(&source)?.filter(|parsed| !parsed.inner_path.as_os_str().is_empty());
    let destination_archive = split_archive_path(&destination)?;

    match (source_archive, destination_archive) {
        (Some(source_parsed), Some(destination_parsed))
            if same_archive_path(
                &source_parsed.archive_path,
                &destination_parsed.archive_path,
            ) =>
        {
            let result =
                mutate_archive(&source_parsed.archive_path, source_parsed.format, |root| {
                    let source_path = root.join(&source_parsed.inner_path);
                    let dest_dir = root.join(&destination_parsed.inner_path);
                    move_with_conflict(&source_path, &dest_dir, &conflict_action)
                })?;
            Ok(result.unwrap_or_else(|| format!("SKIPPED:{source}")))
        }
        (Some(source_parsed), Some(_destination_parsed)) => {
            let result = copy_entry_resolved(source.clone(), destination, conflict_action)?;
            delete_archive_entry_parsed(&source_parsed)?;
            Ok(result)
        }
        (None, Some(destination_parsed)) => {
            let source_path = crate::utils::validate_path_no_follow(&source)?;
            if same_archive_path(&source_path, &destination_parsed.archive_path) {
                return Err("Cannot move an archive into itself".to_string());
            }
            let result = copy_entry_resolved(source.clone(), destination, conflict_action)?;
            remove_local_path(&source_path)
                .map_err(|e| format!("Copied into archive but failed to delete source: {e}"))?;
            Ok(result)
        }
        (Some(source_parsed), None) => {
            let result =
                copy_archive_entry_to_local(&source_parsed, &destination, &conflict_action)?;
            delete_archive_entry_parsed(&source_parsed)?;
            Ok(result)
        }
        (None, None) => Err("No archive path was involved in the move operation".to_string()),
    }
}

pub fn delete_archive_entry(path: &str) -> Result<(), String> {
    let parsed = split_archive_path(path)?
        .filter(|parsed| !parsed.inner_path.as_os_str().is_empty())
        .ok_or_else(|| format!("Path is not an archive entry: {path}"))?;
    delete_archive_entry_parsed(&parsed)
}

pub fn create_archive_directory(path: String, name: String) -> Result<String, String> {
    crate::utils::validate_name(&name)?;
    let parsed = split_archive_path(&path)?
        .ok_or_else(|| format!("Path is not inside an archive: {path}"))?;
    let result = mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let dir_path = root.join(&parsed.inner_path).join(&name);
        if dir_path.exists() {
            return Err(format!("Directory already exists: {name}"));
        }
        fs::create_dir(&dir_path).map_err(|e| format!("Failed to create directory: {e}"))?;
        Ok(Some(dir_path))
    })?;
    result.ok_or_else(|| "Archive directory was not created".to_string())
}

pub fn create_archive_file(path: String, name: String) -> Result<String, String> {
    crate::utils::validate_name(&name)?;
    let parsed = split_archive_path(&path)?
        .ok_or_else(|| format!("Path is not inside an archive: {path}"))?;
    let result = mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let file_path = root.join(&parsed.inner_path).join(&name);
        if let Some(parent) = file_path.parent() {
            create_dir_all(parent)?;
        }
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&file_path)
            .map_err(|e| {
                if e.kind() == std::io::ErrorKind::AlreadyExists {
                    format!("File already exists: {name}")
                } else {
                    format!("Failed to create file: {e}")
                }
            })?;
        Ok(Some(file_path))
    })?;
    result.ok_or_else(|| "Archive file was not created".to_string())
}

pub fn rename_archive_entry(path: String, new_name: String) -> Result<String, String> {
    crate::utils::validate_name(&new_name)?;
    let parsed = split_archive_path(&path)?
        .filter(|parsed| !parsed.inner_path.as_os_str().is_empty())
        .ok_or_else(|| format!("Path is not an archive entry: {path}"))?;
    let result = mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let source_path = root.join(&parsed.inner_path);
        if !source_path.exists() {
            return Err(format!("Archive entry not found: {path}"));
        }
        let parent = source_path
            .parent()
            .ok_or_else(|| "Cannot get parent directory".to_string())?;
        let new_path = parent.join(&new_name);
        if new_path.exists() && !same_archive_path(&source_path, &new_path) {
            return Err(format!(
                "A file or directory with that name already exists: {new_name}"
            ));
        }
        if !same_archive_path(&source_path, &new_path) {
            fs::rename(&source_path, &new_path).map_err(|e| format!("Failed to rename: {e}"))?;
        }
        Ok(Some(new_path))
    })?;
    result.ok_or_else(|| "Archive entry was not renamed".to_string())
}

pub fn materialize_archive_entry_to_temp(path: &str) -> Result<PathBuf, String> {
    let parsed = split_archive_path(path)?
        .filter(|parsed| !parsed.inner_path.as_os_str().is_empty())
        .ok_or_else(|| format!("Path is not an archive entry: {path}"))?;
    let work_root = unique_work_dir("open")?;
    extract_archive_to_directory(&parsed.archive_path, &work_root)?;
    let materialized = work_root.join(&parsed.inner_path);
    if !materialized.exists() {
        let _ = fs::remove_dir_all(&work_root);
        return Err(format!("Archive entry not found: {path}"));
    }
    Ok(materialized)
}

fn delete_archive_entry_parsed(parsed: &ArchivePath) -> Result<(), String> {
    mutate_archive(&parsed.archive_path, parsed.format, |root| {
        let path = root.join(&parsed.inner_path);
        remove_local_path(&path)?;
        Ok(None)
    })?;
    Ok(())
}

struct MaterializedSource {
    path: PathBuf,
    cleanup_root: Option<PathBuf>,
}

impl MaterializedSource {
    fn cleanup(&self) {
        if let Some(root) = &self.cleanup_root {
            let _ = fs::remove_dir_all(root);
        }
    }
}

fn materialize_transfer_source(
    source: &str,
    parsed: Option<&ArchivePath>,
) -> Result<MaterializedSource, String> {
    if let Some(parsed) = parsed {
        let work_root = unique_work_dir("source")?;
        extract_archive_to_directory(&parsed.archive_path, &work_root)?;
        let path = work_root.join(&parsed.inner_path);
        if !path.exists() {
            let _ = fs::remove_dir_all(&work_root);
            return Err(format!("Archive entry not found: {source}"));
        }
        Ok(MaterializedSource {
            path,
            cleanup_root: Some(work_root),
        })
    } else {
        Ok(MaterializedSource {
            path: crate::utils::validate_path_no_follow(source)?,
            cleanup_root: None,
        })
    }
}

fn copy_archive_entry_to_local(
    parsed: &ArchivePath,
    destination: &str,
    conflict_action: &str,
) -> Result<String, String> {
    let dest_dir = crate::utils::validate_existing_path_no_resolve(destination)?;
    if !dest_dir.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }

    let work_root = unique_work_dir("extract-entry")?;
    let result = (|| {
        extract_archive_to_directory(&parsed.archive_path, &work_root)?;
        let source_path = work_root.join(&parsed.inner_path);
        if !source_path.exists() {
            return Err(format!(
                "Archive entry not found: {}",
                build_virtual_archive_path(&parsed.archive_path, &parsed.inner_path)
            ));
        }
        let final_dest = copy_with_conflict(&source_path, &dest_dir, conflict_action)?;
        Ok(final_dest.map(|path| path.to_string_lossy().to_string()))
    })();
    let _ = fs::remove_dir_all(&work_root);

    match result? {
        Some(path) => Ok(path),
        None => Ok(format!(
            "SKIPPED:{}",
            build_virtual_archive_path(&parsed.archive_path, &parsed.inner_path)
        )),
    }
}

fn mutate_archive<F>(
    archive_path: &Path,
    format: ArchiveFormat,
    mut mutate: F,
) -> Result<Option<String>, String>
where
    F: FnMut(&Path) -> Result<Option<PathBuf>, String>,
{
    let work_root = unique_work_dir("mutate")?;
    let new_archive = unique_temp_archive_path(archive_path)?;
    let result = (|| {
        extract_archive_to_directory(archive_path, &work_root)?;
        let final_path = mutate(&work_root)?;
        rebuild_archive_from_directory(archive_path, format, &work_root, &new_archive)?;
        replace_archive(archive_path, &new_archive)?;
        Ok(final_path.map(|path| {
            let relative = path.strip_prefix(&work_root).unwrap_or(&path);
            build_virtual_archive_path(archive_path, relative)
        }))
    })();

    let _ = fs::remove_dir_all(&work_root);
    let _ = fs::remove_file(&new_archive);

    result
}

fn copy_with_conflict(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
) -> Result<Option<PathBuf>, String> {
    let final_dest = resolve_transfer_destination(source_path, dest_dir, conflict_action)?;
    if let Some(final_dest) = &final_dest {
        copy_path_to_destination(source_path, final_dest)?;
    }
    Ok(final_dest)
}

fn move_with_conflict(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
) -> Result<Option<PathBuf>, String> {
    let final_dest = resolve_transfer_destination(source_path, dest_dir, conflict_action)?;
    if let Some(final_dest) = &final_dest {
        if same_archive_path(source_path, final_dest) {
            return Ok(Some(final_dest.clone()));
        }
        if let Some(parent) = final_dest.parent() {
            create_dir_all(parent)?;
        }
        if let Ok(()) = fs::rename(source_path, final_dest) {
        } else {
            copy_path_to_destination(source_path, final_dest)?;
            remove_local_path(source_path)?;
        }
    }
    Ok(final_dest)
}

fn resolve_transfer_destination(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
) -> Result<Option<PathBuf>, String> {
    if dest_dir.exists() && !dest_dir.is_dir() {
        return Err(format!(
            "Destination is not a directory: {}",
            dest_dir.display()
        ));
    }
    create_dir_all(dest_dir)?;

    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_dir.join(file_name);
    if !final_dest.exists() {
        return Ok(Some(final_dest));
    }

    match conflict_action.to_ascii_lowercase().as_str() {
        "skip" => Ok(None),
        "replace" => {
            if same_archive_path(source_path, &final_dest) {
                return Ok(Some(final_dest));
            }
            remove_local_path(&final_dest)?;
            Ok(Some(final_dest))
        }
        "rename" | "keep-both" | "keep_both" => {
            Ok(Some(unique_destination_path(dest_dir, file_name)))
        }
        _ => Err(format!(
            "CONFLICT: destination already exists: {}",
            final_dest.to_string_lossy()
        )),
    }
}

fn copy_path_to_destination(source_path: &Path, final_dest: &Path) -> Result<(), String> {
    if same_archive_path(source_path, final_dest) {
        return Ok(());
    }

    let meta =
        fs::symlink_metadata(source_path).map_err(|e| format!("Failed to stat source: {e}"))?;
    if meta.file_type().is_symlink() {
        return Err("Copying symlinks into or out of archives is not supported".to_string());
    }
    if meta.is_dir() {
        copy_dir_for_archive(source_path, final_dest)
    } else {
        if let Some(parent) = final_dest.parent() {
            create_dir_all(parent)?;
        }
        fs::copy(source_path, final_dest)
            .map(|_| ())
            .map_err(|e| format!("Failed to copy file: {e}"))
    }
}

fn copy_dir_for_archive(src: &Path, dst: &Path) -> Result<(), String> {
    let src_canonical = src.canonicalize().ok();
    let mut stack = vec![(src.to_path_buf(), dst.to_path_buf())];

    while let Some((src_path, dst_path)) = stack.pop() {
        if let (Some(src_root), Some(dst_parent)) = (
            src_canonical.as_ref(),
            dst_path.parent().and_then(|p| p.canonicalize().ok()),
        ) {
            if dst_parent.starts_with(src_root) {
                return Err(
                    "Cannot copy a directory into itself or one of its subdirectories".to_string(),
                );
            }
        }

        let meta =
            fs::symlink_metadata(&src_path).map_err(|e| format!("Failed to stat source: {e}"))?;
        if meta.file_type().is_symlink() {
            continue;
        }
        if meta.is_dir() {
            create_dir_all(&dst_path)?;
            for entry in
                fs::read_dir(&src_path).map_err(|e| format!("Failed to read directory: {e}"))?
            {
                let entry = entry.map_err(|e| format!("Failed to read directory entry: {e}"))?;
                stack.push((entry.path(), dst_path.join(entry.file_name())));
            }
        } else {
            if let Some(parent) = dst_path.parent() {
                create_dir_all(parent)?;
            }
            fs::copy(&src_path, &dst_path).map_err(|e| format!("Failed to copy file: {e}"))?;
        }
    }

    Ok(())
}

fn remove_local_path(path: &Path) -> Result<(), String> {
    let meta = fs::symlink_metadata(path).map_err(|e| format!("Failed to stat path: {e}"))?;
    if meta.file_type().is_symlink() {
        if meta.is_dir() {
            fs::remove_dir(path).map_err(|e| format!("Failed to delete symlink: {}", e))?;
        } else {
            fs::remove_file(path).map_err(|e| format!("Failed to delete symlink: {}", e))?;
        }
    } else if meta.is_dir() {
        fs::remove_dir_all(path).map_err(|e| format!("Failed to delete directory: {e}"))?;
    } else {
        fs::remove_file(path).map_err(|e| format!("Failed to delete file: {e}"))?;
    }
    Ok(())
}

fn unique_destination_path(dest_dir: &Path, file_name: &OsStr) -> PathBuf {
    let original = Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |s| s.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|e| e.to_string_lossy().to_string());

    for i in 1..10_000u32 {
        let candidate_name = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({i}).{ext}"),
            _ => format!("{stem} ({i})"),
        };
        let candidate = dest_dir.join(candidate_name);
        if !candidate.exists() {
            return candidate;
        }
    }

    dest_dir.join(file_name)
}

fn rebuild_archive_from_directory(
    original_archive_path: &Path,
    format: ArchiveFormat,
    work_root: &Path,
    new_archive_path: &Path,
) -> Result<(), String> {
    let child_paths = archive_root_children(work_root)?;
    let archive_path = new_archive_path.to_string_lossy().to_string();
    match format {
        ArchiveFormat::Zip => create_zip_archive(&child_paths, &archive_path),
        ArchiveFormat::Tar => create_tar_archive(&child_paths, &archive_path, None),
        ArchiveFormat::TarGz => create_tar_archive(&child_paths, &archive_path, Some("gz")),
        ArchiveFormat::Rar => {
            if child_paths.is_empty() {
                return Err("RAR archives cannot be rewritten with no entries".to_string());
            }
            let binary = resolve_rar_binary().ok_or_else(|| {
                "RAR command not found. Install it from Settings -> RAR Tools.".to_string()
            })?;
            create_rar_archive(&child_paths, &archive_path, &binary)
        }
    }
    .map_err(|e| {
        format!(
            "Failed to rebuild {} archive {}: {}",
            format.label(),
            original_archive_path.display(),
            e
        )
    })
}

fn archive_root_children(root: &Path) -> Result<Vec<String>, String> {
    let mut paths = Vec::new();
    for entry in fs::read_dir(root).map_err(|e| format!("Failed to read archive workspace: {e}"))? {
        let entry = entry.map_err(|e| format!("Failed to read archive workspace entry: {e}"))?;
        paths.push(entry.path().to_string_lossy().to_string());
    }
    paths.sort_by_cached_key(|path| path.to_lowercase());
    Ok(paths)
}

fn replace_archive(archive_path: &Path, new_archive_path: &Path) -> Result<(), String> {
    let backup_path = unique_backup_path(archive_path)?;
    fs::rename(archive_path, &backup_path)
        .map_err(|e| format!("Failed to prepare archive replacement: {e}"))?;

    if let Err(e) = fs::rename(new_archive_path, archive_path) {
        let _ = fs::rename(&backup_path, archive_path);
        return Err(format!("Failed to replace archive: {e}"));
    }

    let _ = fs::remove_file(&backup_path);
    Ok(())
}

fn unique_backup_path(path: &Path) -> Result<PathBuf, String> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let name = path
        .file_name()
        .ok_or_else(|| format!("Cannot get archive file name for {}", path.display()))?
        .to_string_lossy();
    for i in 0..10_000u32 {
        let candidate = parent.join(format!(".{name}.simplefile-backup-{i}"));
        if !candidate.exists() {
            return Ok(candidate);
        }
    }
    Err("Could not create a unique archive backup path".to_string())
}

fn unique_temp_archive_path(path: &Path) -> Result<PathBuf, String> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let name = path
        .file_name()
        .ok_or_else(|| format!("Cannot get archive file name for {}", path.display()))?
        .to_string_lossy();
    for i in 0..10_000u32 {
        let candidate = parent.join(format!(".{}.simplefile-new-{}-{}", name, unique_nonce(), i));
        if !candidate.exists() {
            return Ok(candidate);
        }
    }
    Err("Could not create a unique temporary archive path".to_string())
}

fn unique_work_dir(label: &str) -> Result<PathBuf, String> {
    let base = std::env::temp_dir().join("SimpleFile");
    create_dir_all(&base)?;
    for i in 0..10_000u32 {
        let candidate = base.join(format!("archive-{}-{}-{}", label, unique_nonce(), i));
        if !candidate.exists() {
            create_dir_all(&candidate)?;
            return Ok(candidate);
        }
    }
    Err("Could not create a unique archive workspace".to_string())
}

fn unique_nonce() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos()
}

fn same_archive_path(left: &Path, right: &Path) -> bool {
    match (left.canonicalize(), right.canonicalize()) {
        (Ok(left), Ok(right)) => left == right,
        _ => left.to_string_lossy().to_lowercase() == right.to_string_lossy().to_lowercase(),
    }
}

pub fn create_archive(
    paths: Vec<String>,
    archive_path: String,
    format: String,
) -> Result<(), String> {
    match format.as_str() {
        "zip" => create_zip_archive(&paths, &archive_path),
        "tar" => create_tar_archive(&paths, &archive_path, None),
        "tar.gz" | "tgz" => create_tar_archive(&paths, &archive_path, Some("gz")),
        "rar" => {
            let binary = resolve_rar_binary().ok_or_else(|| {
                "RAR command not found. Install it from Settings -> RAR Tools.".to_string()
            })?;
            create_rar_archive(&paths, &archive_path, &binary)
        }
        _ => Err(format!("Unsupported format: {format}")),
    }
}

fn create_zip_archive(paths: &[String], archive_path: &str) -> Result<(), String> {
    let file = std::fs::File::create(archive_path).map_err(|e| e.to_string())?;
    let mut zip = zip::ZipWriter::new(file);
    let options = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated);
    for path_str in paths {
        let path = Path::new(path_str);
        let name = path
            .file_name()
            .ok_or_else(|| format!("Cannot get file name for: {path_str}"))?
            .to_string_lossy();
        if path.is_file() {
            zip.start_file(name.as_ref(), options)
                .map_err(|e| e.to_string())?;
            // Stream file contents rather than reading into a Vec to avoid
            // memory exhaustion when archiving large files.
            let mut src = std::fs::File::open(path).map_err(|e| e.to_string())?;
            std::io::copy(&mut src, &mut zip).map_err(|e| e.to_string())?;
        } else if path.is_dir() {
            zip.add_directory(name.as_ref(), options)
                .map_err(|e| e.to_string())?;
            add_dir_to_zip(&mut zip, path, name.as_ref(), options)?;
        }
    }
    zip.finish().map_err(|e| e.to_string())?;
    Ok(())
}

fn add_dir_to_zip<W: std::io::Write + std::io::Seek>(
    zip: &mut zip::ZipWriter<W>,
    dir: &Path,
    prefix: &str,
    options: zip::write::SimpleFileOptions,
) -> Result<(), String> {
    for entry in std::fs::read_dir(dir).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        let path = entry.path();
        let name = format!("{}/{}", prefix, entry.file_name().to_string_lossy());
        // Use symlink_metadata so we never follow circular symlinks into
        // infinite recursion during directory traversal.
        let Ok(ft) = entry.file_type() else { continue };
        if ft.is_file() {
            zip.start_file(&name, options).map_err(|e| e.to_string())?;
            // Stream rather than buffering the whole file to prevent OOM.
            let mut src = std::fs::File::open(&path).map_err(|e| e.to_string())?;
            std::io::copy(&mut src, zip).map_err(|e| e.to_string())?;
        } else if ft.is_dir() {
            zip.add_directory(&name, options)
                .map_err(|e| e.to_string())?;
            add_dir_to_zip(zip, &path, &name, options)?;
        }
        // Symlinks are intentionally skipped to avoid loops.
    }
    Ok(())
}

pub fn resolve_rar_binary() -> Option<String> {
    if let Ok(path) = std::env::var("SIMPLEFILE_RAR") {
        let trimmed = path.trim();
        if !trimmed.is_empty() && Path::new(trimmed).exists() {
            return Some(trimmed.to_string());
        }
    }

    if std::process::Command::new("rar")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .output()
        .is_ok()
    {
        return Some("rar".to_string());
    }

    for path in [
        r"C:\Program Files\WinRAR\rar.exe",
        r"C:\Program Files (x86)\WinRAR\rar.exe",
    ] {
        if Path::new(path).exists() {
            return Some(path.to_string());
        }
    }

    if let Ok(local_app_data) = std::env::var("LOCALAPPDATA") {
        let path = PathBuf::from(local_app_data)
            .join("Programs")
            .join("WinRAR")
            .join("rar.exe");
        if path.exists() {
            return Some(path.to_string_lossy().to_string());
        }
    }

    None
}

fn create_rar_archive(
    paths: &[String],
    archive_path: &str,
    rar_binary: &str,
) -> Result<(), String> {
    if paths.is_empty() {
        return Err("No files specified".to_string());
    }

    // Single invocation with absolute paths.
    // -ep1 strips the leading path components so files appear as basenames inside the archive,
    // consistent with ZIP/TAR behaviour.
    // -r recurses into subdirectories.
    // stdin is set to null to prevent the process from blocking on terminal input.

    // Prevent argument injection: rar parses switches by position, so archive_path
    // is consumed before the "--" end-of-options delimiter and can still be
    // misinterpreted as a flag if it starts with '-'.  Prepend "./" to any
    // path that begins with '-' so the rar binary always sees it as a filename.
    let archive_arg: std::borrow::Cow<str> = if archive_path.starts_with('-') {
        format!("./{archive_path}").into()
    } else {
        archive_path.into()
    };

    let output = std::process::Command::new(rar_binary)
        .arg("a")
        .arg("-r")
        .arg("-ep1")
        .arg(archive_arg.as_ref())
        .arg("--") // POSIX end-of-options: prevents filenames starting with '-' being
        // interpreted as flags by the rar binary (argument injection guard).
        .args(paths)
        .stdin(std::process::Stdio::null())
        .output()
        .map_err(|e| format!("Failed to run rar command: {e}"))?;

    // Exit code 0 = success; 1 = warning (non-fatal, archive was still written).
    // Treat anything above 1 as a hard failure.
    let code = output.status.code().unwrap_or(2);
    if code > 1 {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        let detail = format!("{stderr}{stdout}").trim().to_string();
        return Err(if detail.is_empty() {
            format!("RAR creation failed (exit code {code})")
        } else {
            format!("RAR creation failed: {detail}")
        });
    }

    Ok(())
}

fn create_tar_archive(
    paths: &[String],
    archive_path: &str,
    compression: Option<&str>,
) -> Result<(), String> {
    let file = std::fs::File::create(archive_path).map_err(|e| e.to_string())?;

    fn add_paths_to_tar<W: std::io::Write>(
        archive: &mut tar::Builder<W>,
        paths: &[String],
    ) -> Result<(), String> {
        for path_str in paths {
            let path = Path::new(path_str);
            let name = path
                .file_name()
                .ok_or_else(|| format!("Cannot get file name for: {path_str}"))?
                .to_string_lossy();
            if path.is_file() {
                archive
                    .append_path_with_name(path, name.as_ref())
                    .map_err(|e| e.to_string())?;
            } else if path.is_dir() {
                archive
                    .append_dir_all(name.as_ref(), path)
                    .map_err(|e| e.to_string())?;
            }
        }
        Ok(())
    }

    match compression {
        Some("gz") => {
            let encoder = flate2::write::GzEncoder::new(file, flate2::Compression::default());
            let mut archive = tar::Builder::new(encoder);
            add_paths_to_tar(&mut archive, paths)?;
            archive.finish().map_err(|e| e.to_string())?;
        }
        None => {
            let mut archive = tar::Builder::new(file);
            add_paths_to_tar(&mut archive, paths)?;
            archive.finish().map_err(|e| e.to_string())?;
        }
        _ => return Err("Unsupported compression".to_string()),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn unique_temp_dir(name: &str) -> PathBuf {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock should be after epoch")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("simplefile-archive-test-{}-{}", name, nonce));
        fs::create_dir_all(&dir).expect("create temp test directory");
        dir
    }

    fn write_test_zip(zip_path: &Path, entries: &[(&str, &[u8])]) {
        let file = fs::File::create(zip_path).expect("create test zip");
        let mut zip = zip::ZipWriter::new(file);
        let options = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Stored);

        for (name, contents) in entries {
            zip.start_file(name, options).expect("start zip entry");
            zip.write_all(contents).expect("write zip entry");
        }

        zip.finish().expect("finish test zip");
    }

    fn write_test_tar(tar_path: &Path, entries: &[(&str, &[u8])]) {
        let file = fs::File::create(tar_path).expect("create test tar");
        let mut archive = tar::Builder::new(file);

        for &(name, contents) in entries {
            let mut header = tar::Header::new_gnu();
            header.set_path(name).expect("set tar entry path");
            header.set_size(contents.len() as u64);
            header.set_mode(0o644);
            header.set_cksum();
            let mut reader = contents;
            archive
                .append(&header, &mut reader)
                .expect("append tar entry");
        }

        archive.finish().expect("finish test tar");
    }

    #[test]
    fn extract_zip_allows_nested_folder_that_does_not_exist_yet() {
        let root = unique_temp_dir("nested");
        let dest = root.join("out");
        fs::create_dir_all(&dest).expect("create destination");
        let zip_path = root.join("nested.zip");
        let entry_name = "218. 2025 - Latex and the City - Awlivv/01-218. 2025 - Latex and the City - Awlivv.jpeg";
        write_test_zip(&zip_path, &[(entry_name, b"image-data")]);

        extract_zip(zip_path.to_str().unwrap(), &dest).expect("nested zip should extract");

        assert_eq!(
            fs::read(
                dest.join("218. 2025 - Latex and the City - Awlivv")
                    .join("01-218. 2025 - Latex and the City - Awlivv.jpeg")
            )
            .expect("read extracted file"),
            b"image-data"
        );

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn extract_zip_rejects_parent_traversal() {
        let root = unique_temp_dir("traversal");
        let dest = root.join("out");
        fs::create_dir_all(&dest).expect("create destination");
        let zip_path = root.join("evil.zip");
        write_test_zip(&zip_path, &[("../evil.txt", b"bad")]);

        let err = extract_zip(zip_path.to_str().unwrap(), &dest)
            .expect_err("traversal path should be rejected");

        assert!(err.contains("Zip entry has unsafe path"));
        assert!(!root.join("evil.txt").exists());

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn zip_entry_paths_reject_windows_drive_relative_names() {
        assert!(zip_entry_relative_path("C:evil.txt").is_err());
        assert!(zip_entry_relative_path("C:/evil.txt").is_err());
        assert!(zip_entry_relative_path("folder/foo:bar.txt").is_err());
        assert!(zip_entry_relative_path("folder/CON.txt").is_err());
        assert!(zip_entry_relative_path("folder/bad?.txt").is_err());
        assert!(zip_entry_relative_path("folder\0evil.txt").is_err());
    }

    #[test]
    fn extract_path_must_remain_within_destination() {
        let dest = PathBuf::from(r"C:\Users\demo\extract-root");
        ensure_extract_path_within_destination(&dest, &dest.join("safe.txt"))
            .expect("nested safe path");
        ensure_extract_path_within_destination(&dest, &dest.join("folder").join("nested.bin"))
            .expect("deep nested safe path");
        ensure_extract_path_within_destination(&dest, &dest).expect("destination itself");

        let sibling_escape = PathBuf::from(r"C:\Users\demo\extract-root-evil\file.txt");
        assert!(
            ensure_extract_path_within_destination(&dest, &sibling_escape).is_err(),
            "prefix sibling must not count as inside dest"
        );

        let parent_escape = PathBuf::from(r"C:\Users\demo\outside.txt");
        assert!(
            ensure_extract_path_within_destination(&dest, &parent_escape).is_err(),
            "parent path must be rejected"
        );
    }

    #[test]
    fn tar_and_rar_entry_paths_reject_windows_special_names() {
        for archive_type in ["Tar", "RAR"] {
            assert!(archive_entry_relative_path(Path::new("C:evil.txt"), archive_type).is_err());
            assert!(
                archive_entry_relative_path(Path::new("folder/foo:bar.txt"), archive_type).is_err()
            );
            assert!(
                archive_entry_relative_path(Path::new("folder/CON.txt"), archive_type).is_err()
            );
            assert!(
                archive_entry_relative_path(Path::new("folder/bad?.txt"), archive_type).is_err()
            );
            assert!(archive_entry_relative_path(Path::new("/absolute.txt"), archive_type).is_err());
            assert!(archive_entry_relative_path(Path::new("../escape.txt"), archive_type).is_err());
            assert!(archive_entry_relative_path(Path::new("."), archive_type).is_err());
        }

        assert_eq!(
            archive_entry_relative_path(Path::new("folder/safe.txt"), "Tar").unwrap(),
            PathBuf::from("folder").join("safe.txt")
        );
    }

    #[test]
    fn extract_tar_rejects_ads_like_entry_names() {
        let root = unique_temp_dir("tar-ads");
        let dest = root.join("out");
        fs::create_dir_all(&dest).expect("create destination");
        let tar_path = root.join("evil.tar");
        write_test_tar(&tar_path, &[("foo:bar.txt", b"bad")]);

        let err = extract_tar(tar_path.to_str().unwrap(), &dest, None)
            .expect_err("ADS-like tar path should be rejected");

        assert!(err.contains("Tar entry has unsafe path"));
        assert!(!dest.join("foo:bar.txt").exists());

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn virtual_archive_listing_skips_unsafe_entries() {
        let root = unique_temp_dir("virtual-listing-unsafe");
        let zip_path = root.join("sample.zip");
        write_test_zip(
            &zip_path,
            &[("safe.txt", b"safe"), ("folder/foo:bar.txt", b"bad")],
        );

        let root_listing = list_archive_directory(zip_path.to_str().unwrap())
            .expect("list archive root")
            .expect("archive root listing");

        assert_eq!(root_listing.entries.len(), 1);
        assert_eq!(root_listing.entries[0].name, "safe.txt");

        let archive_info =
            list_archive(zip_path.to_str().unwrap().to_string()).expect("list archive info");
        assert_eq!(archive_info.entries.len(), 1);
        assert_eq!(archive_info.entries[0].path, "safe.txt");
        assert_eq!(archive_info.unsafe_entries, vec!["folder/foo:bar.txt"]);

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn extract_zip_renames_colliding_top_level_folder() {
        let root = unique_temp_dir("collision");
        let dest = root.join("out");
        fs::create_dir_all(dest.join("SimpleFile")).expect("create existing top folder");
        fs::write(dest.join("SimpleFile").join("existing.txt"), b"original")
            .expect("write existing file");

        let zip_path = root.join("SimpleFile.zip");
        write_test_zip(
            &zip_path,
            &[
                ("SimpleFile/existing.txt", b"from-archive"),
                ("SimpleFile/nested/new.txt", b"nested"),
            ],
        );

        extract_zip(zip_path.to_str().unwrap(), &dest)
            .expect("colliding top-level folder should keep both");

        assert_eq!(
            fs::read(dest.join("SimpleFile").join("existing.txt")).expect("read original"),
            b"original"
        );
        assert_eq!(
            fs::read(dest.join("SimpleFile (1)").join("existing.txt"))
                .expect("read renamed extraction"),
            b"from-archive"
        );
        assert_eq!(
            fs::read(dest.join("SimpleFile (1)").join("nested").join("new.txt"))
                .expect("read nested renamed extraction"),
            b"nested"
        );

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn list_archive_directory_projects_zip_as_folders() {
        let root = unique_temp_dir("virtual-listing");
        let zip_path = root.join("sample.zip");
        write_test_zip(
            &zip_path,
            &[
                ("folder/a.txt", b"a"),
                ("folder/nested/b.txt", b"b"),
                ("root.txt", b"root"),
            ],
        );

        let root_listing = list_archive_directory(zip_path.to_str().unwrap())
            .expect("list archive root")
            .expect("archive root listing");
        assert_eq!(root_listing.entries.len(), 2);
        assert!(root_listing
            .entries
            .iter()
            .any(|entry| entry.name == "folder" && entry.is_dir));
        assert!(root_listing
            .entries
            .iter()
            .any(|entry| entry.name == "root.txt" && !entry.is_dir));

        let nested_path = build_virtual_archive_path(&zip_path, Path::new("folder"));
        let nested_listing = list_archive_directory(&nested_path)
            .expect("list nested archive folder")
            .expect("nested listing");
        assert!(nested_listing
            .entries
            .iter()
            .any(|entry| entry.name == "a.txt" && !entry.is_dir));
        assert!(nested_listing
            .entries
            .iter()
            .any(|entry| entry.name == "nested" && entry.is_dir));

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn copy_local_file_into_zip_archive_root() {
        let root = unique_temp_dir("copy-into-zip");
        let zip_path = root.join("sample.zip");
        let source = root.join("source.txt");
        fs::write(&source, b"from-local").expect("write source");
        write_test_zip(&zip_path, &[("existing.txt", b"existing")]);

        let result = copy_entry_resolved(
            source.to_string_lossy().to_string(),
            zip_path.to_string_lossy().to_string(),
            "error".to_string(),
        )
        .expect("copy into zip");

        assert_eq!(
            result,
            build_virtual_archive_path(&zip_path, Path::new("source.txt"))
        );

        let out = root.join("out");
        fs::create_dir_all(&out).expect("create out dir");
        extract_archive_to_directory(&zip_path, &out).expect("extract updated zip");
        assert_eq!(
            fs::read(out.join("source.txt")).expect("read copied entry"),
            b"from-local"
        );
        assert_eq!(
            fs::read(out.join("existing.txt")).expect("read existing entry"),
            b"existing"
        );

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn copy_zip_archive_entry_out_to_local_folder() {
        let root = unique_temp_dir("copy-out-of-zip");
        let zip_path = root.join("sample.zip");
        write_test_zip(&zip_path, &[("folder/source.txt", b"from-archive")]);
        let out = root.join("out");
        fs::create_dir_all(&out).expect("create out dir");

        let source =
            build_virtual_archive_path(&zip_path, Path::new("folder").join("source.txt").as_path());
        let result = copy_entry_resolved(
            source,
            out.to_string_lossy().to_string(),
            "error".to_string(),
        )
        .expect("copy out of zip");

        assert_eq!(PathBuf::from(result), out.join("source.txt"));
        assert_eq!(
            fs::read(out.join("source.txt")).expect("read copied file"),
            b"from-archive"
        );

        let _ = fs::remove_dir_all(root);
    }
}
