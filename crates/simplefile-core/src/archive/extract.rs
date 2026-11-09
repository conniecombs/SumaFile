use std::path::Path;

use super::engine::{engine_entries, ArchiveEngine, EngineContext, EngineFormat, EngineLimits};
use super::path::{
    archive_entry_relative_path, archive_format_for_path, archive_kind_for_path, copy_entry_data,
    copy_entry_data_limited, create_dir_all, create_file, ensure_extract_path_within_destination,
    output_path_for_entry, path_is_within_prefix, top_level_remap, unique_file_path_if_needed,
    zip_entry_relative_path, ArchiveFormat, ArchiveKind,
};
use std::path::PathBuf;

pub fn extract_archive(archive_path: String, destination: String) -> Result<(), String> {
    extract_archive_with(archive_path, destination, &EngineContext::untracked())
}

/// Extract every entry of `archive_path` into `destination`. Built-in
/// formats only honour a cancel that is already set; an engine gets the
/// context itself.
pub fn extract_archive_with(
    archive_path: String,
    destination: String,
    ctx: &EngineContext<'_>,
) -> Result<(), String> {
    ctx.check_cancelled()?;
    let archive = Path::new(&archive_path);
    let dest = Path::new(&destination);
    match archive_kind_for_path(archive) {
        Some(ArchiveKind::Engine { engine, format }) => {
            return extract_engine_archive(engine.as_ref(), &format, archive, dest, ctx);
        }
        Some(ArchiveKind::BuiltIn(_)) => {}
        // Refuse before the destination is created.
        None => return Err(super::list::unsupported_archive_error(archive)),
    }
    create_dir_all(dest)?;
    extract_archive_to_directory(archive, dest)
}

/// List and validate first, so an unsafe name fails before the engine
/// writes anything. The engine then extracts into private staging, and
/// only the listed entries are moved out, under the same containment and
/// collision rules as the built-in formats.
fn extract_engine_archive(
    engine: &dyn ArchiveEngine,
    format: &EngineFormat,
    archive: &Path,
    dest: &Path,
    ctx: &EngineContext<'_>,
) -> Result<(), String> {
    if !format.can_extract {
        return Err(format!(
            "{} cannot extract {} archives",
            engine.name(),
            format.extension
        ));
    }
    let planned = engine_entries(engine, archive)?;
    create_dir_all(dest)?;
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;
    let relative_paths: Vec<PathBuf> = planned
        .iter()
        .map(|(_, relative)| relative.clone())
        .collect();
    let root_remap = top_level_remap(&dest_canonical, &relative_paths);

    let staging = ArchiveStaging::create(&dest_canonical)?.produced_by(engine.name());
    engine.extract(archive, staging.path(), ctx)?;
    ctx.check_cancelled()?;
    // Check every entry before moving any, so a refusal leaves the
    // destination as it was.
    for (entry, relative) in &planned {
        staging.check_entry(relative, entry.is_dir)?;
    }

    for (entry, relative) in planned {
        let outpath = output_path_for_entry(&dest_canonical, &relative, root_remap.as_ref());
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if entry.is_dir {
            create_dir_all(&outpath)?;
            continue;
        }
        if let Some(parent) = outpath.parent() {
            create_dir_all(parent)?;
        }
        let final_outpath = unique_file_path_if_needed(&outpath);
        ensure_extract_path_within_destination(&dest_canonical, &final_outpath)?;
        staging.move_entry(&relative, &final_outpath)?;
    }
    Ok(())
}

pub(super) fn extract_archive_to_directory(archive: &Path, dest: &Path) -> Result<(), String> {
    let archive_path = archive.to_string_lossy();
    match archive_format_for_path(archive) {
        Some(ArchiveFormat::Zip) => extract_zip(&archive_path, dest),
        Some(ArchiveFormat::Tar) => extract_tar(&archive_path, dest, None),
        Some(ArchiveFormat::TarGz) => extract_tar(&archive_path, dest, Some("gz")),
        Some(ArchiveFormat::Rar) => extract_rar(&archive_path, dest),
        None => Err(super::list::unsupported_archive_error(archive)),
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
        let file = archive.by_index_raw(i).map_err(|e| e.to_string())?;
        if file.encrypted() {
            // Refuse before anything is written: failing on the first encrypted
            // entry mid-way left a partial extraction behind.
            return Err(format!(
                "Archive entry is password protected and cannot be extracted: {}",
                String::from_utf8_lossy(file.name_raw())
            ));
        }
        let name = file
            .name()
            .map_err(|e| format!("Invalid ZIP entry name: {e}"))?;
        planned_paths.push(zip_entry_relative_path(&name)?);
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
            drop(outfile);
            restore_zip_mtime(&final_outpath, file.last_modified());
        }
    }
    Ok(())
}

/// Apply the entry's recorded modification time to the extracted file so an
/// extract-and-rebuild round trip keeps the original timestamps.
fn restore_zip_mtime(path: &Path, stamp: Option<zip::DateTime>) {
    use chrono::TimeZone;
    let Some(stamp) = stamp else {
        return;
    };
    let Some(naive) = chrono::NaiveDate::from_ymd_opt(
        i32::from(stamp.year()),
        u32::from(stamp.month()),
        u32::from(stamp.day()),
    )
    .and_then(|date| {
        date.and_hms_opt(
            u32::from(stamp.hour()),
            u32::from(stamp.minute()),
            u32::from(stamp.second()),
        )
    }) else {
        return;
    };
    let Some(local) = chrono::Local.from_local_datetime(&naive).single() else {
        return;
    };
    let mtime = filetime::FileTime::from_unix_time(local.timestamp(), 0);
    let _ = filetime::set_file_mtime(path, mtime);
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

/// A private folder inside the destination that one engine run extracts
/// into. Removed on drop, so a failed extraction
/// leaves nothing behind. Its dot-prefixed name keeps it out of sight
/// while it briefly exists.
pub(super) struct ArchiveStaging {
    path: PathBuf,
    producer: String,
}

impl ArchiveStaging {
    pub(super) fn create(dest: &Path) -> Result<Self, String> {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|elapsed| elapsed.as_nanos())
            .unwrap_or(0);
        let path = dest.join(format!(".sumafile-extract-{}-{nanos}", std::process::id()));
        std::fs::create_dir(&path)
            .map_err(|e| format!("Failed to create archive staging folder: {e}"))?;
        Ok(Self {
            path,
            producer: "The archive engine".to_string(),
        })
    }

    /// Name what writes into this staging, for the error about a listed
    /// entry it did not write.
    pub(super) fn produced_by(mut self, producer: &str) -> Self {
        self.producer = producer.to_string();
        self
    }

    pub(super) fn path(&self) -> &Path {
        &self.path
    }

    /// Refuse a staged entry that is, or sits under, a symlink or junction,
    /// and a listed file that was not written. `relative` is a validated
    /// entry path.
    pub(super) fn check_entry(&self, relative: &Path, is_dir: bool) -> Result<(), String> {
        let mut staged = self.path.clone();
        let mut shown = Vec::new();
        for component in relative.components() {
            staged.push(component);
            shown.push(component.as_os_str().to_string_lossy().into_owned());
            match std::fs::symlink_metadata(&staged) {
                Ok(meta) if is_link(&meta) => {
                    return Err(format!("Archive entry is a link: {}", shown.join("/")));
                }
                Ok(_) => {}
                Err(_) => break,
            }
        }
        if !is_dir && !staged.is_file() {
            return Err(format!(
                "{} did not extract {}",
                self.producer,
                entry_display(relative)
            ));
        }
        Ok(())
    }

    /// Move one extracted file out of staging. Every entry is written at
    /// its archive path relative to the staging folder.
    pub(super) fn move_entry(&self, relative: &Path, to: &Path) -> Result<(), String> {
        self.check_entry(relative, false)?;
        let staged = self.path.join(relative);
        if std::fs::rename(&staged, to).is_ok() {
            return Ok(());
        }
        std::fs::copy(&staged, to).map(|_| ()).map_err(|e| {
            format!(
                "Failed to place extracted entry {}: {e}",
                entry_display(relative)
            )
        })
    }
}

impl Drop for ArchiveStaging {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

/// A relative entry path as the archive shows it, `/`-separated.
fn entry_display(relative: &Path) -> String {
    relative
        .components()
        .map(|component| component.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

/// A symlink or a junction. On Windows `is_symlink` is true for every
/// reparse point whose tag is a name surrogate, junctions included. Other
/// reparse points, such as cloud-file placeholders (OneDrive Files
/// On-Demand) and deduplicated or WOF-compressed files, are ordinary files
/// and folders.
fn is_link(meta: &std::fs::Metadata) -> bool {
    meta.file_type().is_symlink()
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
    match archive_kind_for_path(archive) {
        Some(ArchiveKind::BuiltIn(ArchiveFormat::Zip)) => {
            extract_zip_entry(&archive_path, dest, inner_path, limits)
        }
        Some(ArchiveKind::BuiltIn(ArchiveFormat::Tar)) => {
            extract_tar_entry(&archive_path, dest, inner_path, None, limits)
        }
        Some(ArchiveKind::BuiltIn(ArchiveFormat::TarGz)) => {
            extract_tar_entry(&archive_path, dest, inner_path, Some("gz"), limits)
        }
        Some(ArchiveKind::BuiltIn(ArchiveFormat::Rar)) => {
            extract_rar_entry(&archive_path, dest, inner_path, limits)
        }
        Some(ArchiveKind::Engine { engine, format }) => {
            read_engine_entry(engine.as_ref(), &format, archive, dest, inner_path, limits)
        }
        None => Err(super::list::unsupported_archive_error(archive)),
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
        let name = file
            .name()
            .map_err(|e| format!("Invalid ZIP entry name: {e}"))?;
        let relative_path = zip_entry_relative_path(&name)?;
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

/// One selection of an engine archive: the entries at or under
/// `inner_path` by path segment. The budget is checked by declared sizes
/// before the engine runs and by written sizes before anything leaves
/// staging.
fn read_engine_entry(
    engine: &dyn ArchiveEngine,
    format: &EngineFormat,
    archive: &Path,
    dest: &Path,
    inner_path: &Path,
    limits: ExtractLimits,
) -> Result<(), String> {
    if !format.can_extract {
        return Err(format!(
            "{} cannot extract {} archives",
            engine.name(),
            format.extension
        ));
    }
    let dest_canonical = dest
        .canonicalize()
        .map_err(|e| format!("Failed to resolve destination: {e}"))?;

    let mut selected = Vec::new();
    let mut declared_total = 0u64;
    for (entry, relative) in engine_entries(engine, archive)? {
        if !path_is_within_prefix(&relative, inner_path) {
            continue;
        }
        if !entry.is_dir {
            declared_total = declared_total
                .checked_add(entry.size)
                .ok_or_else(|| "Archive extract size overflow".to_string())?;
        }
        selected.push((entry, relative));
    }
    if selected.is_empty() {
        return Err(format!("Archive entry not found: {}", inner_path.display()));
    }
    let entry_count = u32::try_from(selected.len()).unwrap_or(u32::MAX);
    reject_if_over_budget(declared_total, entry_count, limits)?;

    let staging = ArchiveStaging::create(&dest_canonical)?.produced_by(engine.name());
    engine.read_entry(
        archive,
        &entry_display(inner_path),
        staging.path(),
        EngineLimits {
            max_bytes: limits.max_uncompressed_bytes,
            max_entries: limits.max_entries,
        },
    )?;

    let mut written_total = 0u64;
    for (entry, relative) in &selected {
        staging.check_entry(relative, entry.is_dir)?;
        if entry.is_dir {
            continue;
        }
        let written = std::fs::metadata(staging.path().join(relative))
            .map(|meta| meta.len())
            .map_err(|e| format!("Failed to read extracted entry: {e}"))?;
        written_total = written_total
            .checked_add(written)
            .ok_or_else(|| "Archive extract size overflow".to_string())?;
        if written_total > limits.max_uncompressed_bytes {
            return Err(format!(
                "Archive extract exceeds size limit of {} bytes",
                limits.max_uncompressed_bytes
            ));
        }
    }

    for (entry, relative) in selected {
        let outpath = dest_canonical.join(&relative);
        ensure_extract_path_within_destination(&dest_canonical, &outpath)?;
        if entry.is_dir {
            create_dir_all(&outpath)?;
            continue;
        }
        if let Some(parent) = outpath.parent() {
            create_dir_all(parent)?;
        }
        staging.move_entry(&relative, &outpath)?;
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
