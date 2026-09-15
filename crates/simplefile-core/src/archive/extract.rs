use std::path::Path;
use std::process::Stdio;

use super::path::{
    archive_entry_relative_path, archive_entry_relative_path_from_name, archive_format_for_path,
    copy_entry_data, copy_entry_data_limited, create_dir_all, create_file,
    ensure_extract_path_within_destination, output_path_for_entry, path_is_within_prefix,
    top_level_remap, unique_file_path_if_needed, zip_entry_relative_path, ArchiveFormat,
};
use std::path::PathBuf;

pub fn extract_archive(archive_path: String, destination: String) -> Result<(), String> {
    let archive = Path::new(&archive_path);
    let dest = Path::new(&destination);
    create_dir_all(dest)?;
    extract_archive_to_directory(archive, dest)
}

pub(super) fn extract_archive_to_directory(archive: &Path, dest: &Path) -> Result<(), String> {
    let archive_path = archive.to_string_lossy();
    match archive_format_for_path(archive) {
        Some(ArchiveFormat::Zip) => extract_zip(&archive_path, dest),
        Some(ArchiveFormat::Tar) => extract_tar(&archive_path, dest, None),
        Some(ArchiveFormat::TarGz) => extract_tar(&archive_path, dest, Some("gz")),
        Some(ArchiveFormat::Rar) => extract_rar(&archive_path, dest),
        Some(ArchiveFormat::SevenZip) => extract_seven_zip(&archive_path, dest),
        None => Err(format!(
            "Unsupported archive format: {}",
            archive.extension().and_then(|e| e.to_str()).unwrap_or("")
        )),
    }
}

pub(super) fn extract_zip(path: &str, dest: &Path) -> Result<(), String> {
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

pub(super) fn extract_tar(
    path: &str,
    dest: &Path,
    compression: Option<&str>,
) -> Result<(), String> {
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

fn extract_seven_zip(path: &str, dest: &Path) -> Result<(), String> {
    let binary = super::seven_zip::require_seven_zip_binary()?;
    let entries = super::seven_zip::list_seven_zip_entries_with_binary(&binary, path)?;
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;

    let mut planned = Vec::with_capacity(entries.len());
    let mut relative_paths = Vec::with_capacity(entries.len());
    for entry in entries {
        let relative_path = archive_entry_relative_path_from_name(&entry.path, "7-Zip")?;
        relative_paths.push(relative_path.clone());
        planned.push((entry, relative_path));
    }

    let root_remap = top_level_remap(&dest_canonical, &relative_paths);
    for (entry, relative_path) in planned {
        let outpath = output_path_for_entry(&dest_canonical, &relative_path, root_remap.as_ref());
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if entry.is_dir {
            create_dir_all(&outpath)?;
        } else {
            if let Some(parent) = outpath.parent() {
                create_dir_all(parent)?;
            }
            let final_outpath = unique_file_path_if_needed(&outpath);
            ensure_extract_path_within_destination(&dest_canonical, &final_outpath)?;
            extract_seven_zip_entry_to_file(&binary, path, &entry.path, &final_outpath)?;
        }
    }

    Ok(())
}

fn extract_seven_zip_entry_to_file(
    binary: &str,
    archive_path: &str,
    entry_path: &str,
    output_path: &Path,
) -> Result<(), String> {
    let outfile = create_file(output_path)?;
    let output = std::process::Command::new(binary)
        .arg("e")
        .arg("-so")
        .arg("-bd")
        .arg("-bb0")
        .arg("-sccUTF-8")
        .arg("-y")
        .arg("-spd")
        .arg("--")
        .arg(archive_path)
        .arg(entry_path)
        .stdin(Stdio::null())
        .stdout(Stdio::from(outfile))
        .output()
        .map_err(|e| format!("Failed to run 7-Zip extract command: {e}"))?;

    if let Err(error) = super::seven_zip::ensure_seven_zip_success(&output, "7-Zip extraction") {
        let _ = std::fs::remove_file(output_path);
        return Err(error);
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

/// Caps for selective archive materialization (preview/open/checksum paths).
#[derive(Debug, Clone, Copy)]
pub(super) struct ExtractLimits {
    pub max_uncompressed_bytes: u64,
    pub max_entries: u32,
}

impl ExtractLimits {
    pub(super) const fn materialize_defaults() -> Self {
        Self {
            // Keep preview/open extracts bounded against zip bombs / disk fill.
            max_uncompressed_bytes: 512 * 1024 * 1024,
            max_entries: 4_096,
        }
    }
}

/// Extract only `inner_path` (and nested children if it is a directory prefix).
pub(super) fn extract_archive_entry_to_directory(
    archive: &Path,
    dest: &Path,
    inner_path: &Path,
    limits: ExtractLimits,
) -> Result<(), String> {
    let archive_path = archive.to_string_lossy();
    match archive_format_for_path(archive) {
        Some(ArchiveFormat::Zip) => extract_zip_entry(&archive_path, dest, inner_path, limits),
        Some(ArchiveFormat::Tar) => {
            extract_tar_entry(&archive_path, dest, inner_path, None, limits)
        }
        Some(ArchiveFormat::TarGz) => {
            extract_tar_entry(&archive_path, dest, inner_path, Some("gz"), limits)
        }
        Some(ArchiveFormat::Rar) => extract_rar_entry(&archive_path, dest, inner_path, limits),
        Some(ArchiveFormat::SevenZip) => {
            extract_seven_zip_entry_matching(&archive_path, dest, inner_path, limits)
        }
        None => Err(format!(
            "Unsupported archive format: {}",
            archive.extension().and_then(|e| e.to_str()).unwrap_or("")
        )),
    }
}

fn reject_if_over_budget(
    declared_total: u64,
    entry_count: u32,
    limits: ExtractLimits,
) -> Result<(), String> {
    if entry_count > limits.max_entries {
        return Err(format!(
            "Archive extract exceeds entry limit of {} entries",
            limits.max_entries
        ));
    }
    if declared_total > limits.max_uncompressed_bytes {
        return Err(format!(
            "Archive extract exceeds size limit of {} bytes",
            limits.max_uncompressed_bytes
        ));
    }
    Ok(())
}

fn extract_zip_entry(
    path: &str,
    dest: &Path,
    inner_path: &Path,
    limits: ExtractLimits,
) -> Result<(), String> {
    let file = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let mut archive = zip::ZipArchive::new(file).map_err(|e| e.to_string())?;
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;

    let mut selected: Vec<(usize, PathBuf, bool, u64)> = Vec::new();
    let mut declared_total = 0u64;
    for i in 0..archive.len() {
        let file = archive.by_index(i).map_err(|e| e.to_string())?;
        let relative_path = zip_entry_relative_path(file.name())?;
        if !path_is_within_prefix(&relative_path, inner_path) {
            continue;
        }
        let size = file.size();
        declared_total = declared_total
            .checked_add(size)
            .ok_or_else(|| "Archive extract size overflow".to_string())?;
        selected.push((i, relative_path, file.is_dir(), size));
    }

    if selected.is_empty() {
        return Err(format!("Archive entry not found: {}", inner_path.display()));
    }
    reject_if_over_budget(declared_total, selected.len() as u32, limits)?;

    let mut written_total = 0u64;
    for (index, relative_path, is_dir, _size) in selected {
        let mut file = archive.by_index(index).map_err(|e| e.to_string())?;
        let outpath = dest_canonical.join(&relative_path);
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if is_dir {
            create_dir_all(&outpath)?;
        } else {
            if let Some(parent) = outpath.parent() {
                create_dir_all(parent)?;
            }
            ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
            let mut outfile = create_file(&outpath)?;
            copy_entry_data_limited(
                &mut file,
                &mut outfile,
                &outpath,
                limits.max_uncompressed_bytes,
                &mut written_total,
            )?;
        }
    }
    Ok(())
}

fn extract_tar_entry(
    path: &str,
    dest: &Path,
    inner_path: &Path,
    compression: Option<&str>,
    limits: ExtractLimits,
) -> Result<(), String> {
    use std::io::Read;
    let file = std::fs::File::open(path).map_err(|e| e.to_string())?;
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;

    fn extract_matching<R: Read>(
        archive: &mut tar::Archive<R>,
        dest_canonical: &Path,
        inner_path: &Path,
        limits: ExtractLimits,
    ) -> Result<(), String> {
        let mut written_total = 0u64;
        let mut entry_count = 0u32;
        let mut found = false;

        for entry_result in archive.entries().map_err(|e| e.to_string())? {
            let mut entry = entry_result.map_err(|e| e.to_string())?;
            let entry_path = entry.path().map_err(|e| e.to_string())?.into_owned();
            let relative_path = archive_entry_relative_path(&entry_path, "Tar")?;
            if !path_is_within_prefix(&relative_path, inner_path) {
                continue;
            }
            found = true;
            entry_count = entry_count
                .checked_add(1)
                .ok_or_else(|| "Archive extract entry overflow".to_string())?;
            if entry_count > limits.max_entries {
                return Err(format!(
                    "Archive extract exceeds entry limit of {} entries",
                    limits.max_entries
                ));
            }

            let outpath = dest_canonical.join(&relative_path);
            ensure_extract_path_within_destination(dest_canonical, &outpath)?;
            let entry_type = entry.header().entry_type();
            if entry_type.is_dir() {
                create_dir_all(&outpath)?;
            } else if entry_type.is_file() {
                let declared = entry.header().size().unwrap_or(0);
                let projected = written_total
                    .checked_add(declared)
                    .ok_or_else(|| "Archive extract size overflow".to_string())?;
                if projected > limits.max_uncompressed_bytes {
                    return Err(format!(
                        "Archive extract exceeds size limit of {} bytes",
                        limits.max_uncompressed_bytes
                    ));
                }
                if let Some(parent) = outpath.parent() {
                    create_dir_all(parent)?;
                }
                ensure_extract_path_within_destination(dest_canonical, &outpath)?;
                let mut outfile = create_file(&outpath)?;
                copy_entry_data_limited(
                    &mut entry,
                    &mut outfile,
                    &outpath,
                    limits.max_uncompressed_bytes,
                    &mut written_total,
                )?;
            }
        }

        if !found {
            return Err(format!("Archive entry not found: {}", inner_path.display()));
        }
        Ok(())
    }

    match compression {
        Some("gz") => {
            let decoder = flate2::read::GzDecoder::new(file);
            let mut archive = tar::Archive::new(decoder);
            extract_matching(&mut archive, &dest_canonical, inner_path, limits)?;
        }
        None => {
            let mut archive = tar::Archive::new(file);
            extract_matching(&mut archive, &dest_canonical, inner_path, limits)?;
        }
        _ => return Err("Unsupported compression".to_string()),
    }
    Ok(())
}

fn extract_seven_zip_entry_matching(
    path: &str,
    dest: &Path,
    inner_path: &Path,
    limits: ExtractLimits,
) -> Result<(), String> {
    let binary = super::seven_zip::require_seven_zip_binary()?;
    let entries = super::seven_zip::list_seven_zip_entries_with_binary(&binary, path)?;
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;

    let mut selected = Vec::new();
    let mut declared_total = 0u64;
    for entry in entries {
        let relative_path = archive_entry_relative_path_from_name(&entry.path, "7-Zip")?;
        if !path_is_within_prefix(&relative_path, inner_path) {
            continue;
        }
        declared_total = declared_total
            .checked_add(entry.size)
            .ok_or_else(|| "Archive extract size overflow".to_string())?;
        selected.push((entry, relative_path));
    }

    if selected.is_empty() {
        return Err(format!("Archive entry not found: {}", inner_path.display()));
    }
    reject_if_over_budget(declared_total, selected.len() as u32, limits)?;

    let mut written_total = 0u64;
    for (entry, relative_path) in selected {
        let outpath = dest_canonical.join(&relative_path);
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if entry.is_dir {
            create_dir_all(&outpath)?;
        } else {
            if let Some(parent) = outpath.parent() {
                create_dir_all(parent)?;
            }
            ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
            extract_seven_zip_entry_to_file(&binary, path, &entry.path, &outpath)?;
            let written = std::fs::metadata(&outpath).map(|m| m.len()).unwrap_or(0);
            written_total = written_total
                .checked_add(written)
                .ok_or_else(|| "Archive extract size overflow".to_string())?;
            if written_total > limits.max_uncompressed_bytes {
                let _ = std::fs::remove_file(&outpath);
                return Err(format!(
                    "Archive extract exceeds size limit of {} bytes",
                    limits.max_uncompressed_bytes
                ));
            }
        }
    }
    Ok(())
}

fn extract_rar_entry(
    path: &str,
    dest: &Path,
    inner_path: &Path,
    limits: ExtractLimits,
) -> Result<(), String> {
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;
    let mut archive = unrar::Archive::new(path)
        .open_for_processing()
        .map_err(|e| format!("Failed to open RAR for extraction: {e}"))?;

    let mut written_total = 0u64;
    let mut entry_count = 0u32;
    let mut found = false;

    while let Some(header) = archive
        .read_header()
        .map_err(|e| format!("Failed to read RAR header: {e}"))?
    {
        let entry_path = header.entry().filename.clone();
        let relative_path = archive_entry_relative_path(&entry_path, "RAR")?;
        if !path_is_within_prefix(&relative_path, inner_path) {
            archive = header
                .skip()
                .map_err(|e| format!("Failed to skip RAR entry: {e}"))?;
            continue;
        }

        found = true;
        entry_count = entry_count
            .checked_add(1)
            .ok_or_else(|| "Archive extract entry overflow".to_string())?;
        if entry_count > limits.max_entries {
            return Err(format!(
                "Archive extract exceeds entry limit of {} entries",
                limits.max_entries
            ));
        }

        let outpath = dest_canonical.join(&relative_path);
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if header.entry().is_directory() {
            create_dir_all(&outpath)?;
            archive = header
                .skip()
                .map_err(|e| format!("Failed to skip RAR directory: {e}"))?;
        } else {
            let declared = header.entry().unpacked_size;
            let projected = written_total
                .checked_add(declared)
                .ok_or_else(|| "Archive extract size overflow".to_string())?;
            if projected > limits.max_uncompressed_bytes {
                return Err(format!(
                    "Archive extract exceeds size limit of {} bytes",
                    limits.max_uncompressed_bytes
                ));
            }
            if let Some(parent) = outpath.parent() {
                create_dir_all(parent)?;
            }
            ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
            archive = header
                .extract_to(&outpath)
                .map_err(|e| format!("Failed to extract RAR entry: {e}"))?;
            let written = std::fs::metadata(&outpath)
                .map(|m| m.len())
                .unwrap_or(declared);
            written_total = written_total
                .checked_add(written)
                .ok_or_else(|| "Archive extract size overflow".to_string())?;
            if written_total > limits.max_uncompressed_bytes {
                let _ = std::fs::remove_file(&outpath);
                return Err(format!(
                    "Archive extract exceeds size limit of {} bytes",
                    limits.max_uncompressed_bytes
                ));
            }
        }
    }

    if !found {
        return Err(format!("Archive entry not found: {}", inner_path.display()));
    }
    Ok(())
}
