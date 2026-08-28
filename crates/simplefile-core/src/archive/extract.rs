use std::path::Path;

use super::path::{
    archive_entry_relative_path, archive_format_for_path, copy_entry_data, create_dir_all,
    create_file, ensure_extract_path_within_destination, output_path_for_entry, top_level_remap,
    unique_file_path_if_needed, zip_entry_relative_path, ArchiveFormat,
};

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
