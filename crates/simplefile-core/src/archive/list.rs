use crate::models::{ArchiveEntry, ArchiveInfo, DirectoryListing, FileEntry};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use super::engine::{ArchiveEngine, EngineFormat};
use super::path::{
    archive_entry_relative_path_from_name, archive_kind_for_path, build_virtual_archive_path,
    normal_components, split_archive_path, ArchiveFormat, ArchiveKind,
};

pub fn list_archive(path: String) -> Result<ArchiveInfo, String> {
    match archive_kind_for_path(Path::new(&path)) {
        Some(kind) => list_archive_of_kind(&path, &kind),
        None => Err(unsupported_archive_error(Path::new(&path))),
    }
}

/// The error for an archive nothing serves: the plugin note for a format
/// only an absent plugin serves, otherwise "Unsupported archive format".
pub(super) fn unsupported_archive_error(archive: &Path) -> String {
    match super::placeholders::unavailable_note_for_path(archive) {
        Some(note) => note.to_string(),
        None => format!(
            "Unsupported archive format: {}",
            archive.extension().and_then(|e| e.to_str()).unwrap_or("")
        ),
    }
}

fn list_archive_of_kind(path: &str, kind: &ArchiveKind) -> Result<ArchiveInfo, String> {
    match kind {
        ArchiveKind::BuiltIn(ArchiveFormat::Zip) => list_zip_archive(path),
        ArchiveKind::BuiltIn(ArchiveFormat::Tar) => list_tar_archive(path, None),
        ArchiveKind::BuiltIn(ArchiveFormat::TarGz) => list_tar_archive(path, Some("gz")),
        ArchiveKind::BuiltIn(ArchiveFormat::Rar) => list_rar_archive(path),
        ArchiveKind::Engine { engine, format } => {
            list_engine_archive(path, engine.as_ref(), format)
        }
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
        // Raw access reads the central directory only, so password-protected
        // entries list like any other instead of failing the whole archive.
        let file = archive.by_index_raw(i).map_err(|e| e.to_string())?;
        let entry_path = match file.name() {
            Ok(name) => name.into_owned(),
            Err(_) => {
                unsafe_entries.push(String::from_utf8_lossy(file.name_raw()).into_owned());
                continue;
            }
        };
        if built_in_entry_relative_path(ArchiveFormat::Zip, &entry_path).is_err() {
            unsafe_entries.push(entry_path);
            continue;
        }

        let entry = ArchiveEntry {
            name: entry_path
                .split('/')
                .next_back()
                .unwrap_or(&entry_path)
                .to_string(),
            path: entry_path,
            is_dir: file.is_dir(),
            size: file.size(),
            compressed_size: file.compressed_size(),
        };
        total_size = total_size.saturating_add(file.size());
        compressed_size = compressed_size.saturating_add(file.compressed_size());
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

/// List an uncompressed tar by seeking over each entry's contents. The plain
/// `entries()` iterator reads every byte of the archive to reach the next
/// header, which turned listing a multi-gigabyte tar into a full read.
pub(super) fn collect_tar_entries_seekable<R: std::io::Read + std::io::Seek>(
    reader: R,
    format: ArchiveFormat,
) -> Result<(Vec<ArchiveEntry>, Vec<String>, u64), String> {
    let mut archive = tar::Archive::new(reader);
    let mut entries = Vec::new();
    let mut unsafe_entries = Vec::new();
    let mut total_size = 0u64;
    for entry in archive.entries_with_seek().map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        let path_str = entry
            .path()
            .map_err(|e| e.to_string())?
            .to_string_lossy()
            .to_string();
        let size = entry.size();
        if built_in_entry_relative_path(format, &path_str).is_err() {
            unsafe_entries.push(path_str);
            continue;
        }
        total_size = total_size.saturating_add(size);
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
    Ok((entries, unsafe_entries, total_size))
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
                if built_in_entry_relative_path(ArchiveFormat::TarGz, &path_str).is_err() {
                    unsafe_entries.push(path_str);
                    continue;
                }
                total_size = total_size.saturating_add(size);
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
            let (seek_entries, seek_unsafe, seek_total) =
                collect_tar_entries_seekable(file, ArchiveFormat::Tar)?;
            entries = seek_entries;
            unsafe_entries = seek_unsafe;
            total_size = seek_total;
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
        if built_in_entry_relative_path(ArchiveFormat::Rar, &filename_str).is_err() {
            unsafe_entries.push(filename_str);
            continue;
        }

        total_size = total_size.saturating_add(entry.unpacked_size);
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

fn list_engine_archive(
    path: &str,
    engine: &dyn ArchiveEngine,
    format: &EngineFormat,
) -> Result<ArchiveInfo, String> {
    if !format.can_list {
        return Err(format!(
            "{} cannot list {} archives",
            engine.name(),
            format.extension
        ));
    }
    let mut entries = Vec::new();
    let mut unsafe_entries = Vec::new();
    let mut total_size = 0u64;

    for entry in engine.list_entries(Path::new(path))? {
        if archive_entry_relative_path_from_name(&entry.path, engine.name()).is_err() {
            unsafe_entries.push(entry.path);
            continue;
        }

        total_size = total_size.saturating_add(entry.size);
        entries.push(ArchiveEntry {
            name: archive_entry_name(&entry.path),
            path: entry.path,
            is_dir: entry.is_dir,
            size: entry.size,
            compressed_size: entry.compressed_size,
        });
    }

    let compressed_size = std::fs::metadata(path).map_or(0, |m| m.len());
    Ok(ArchiveInfo {
        path: path.to_string(),
        format: super::engine::format_label(format),
        entries,
        unsafe_entries,
        total_size,
        compressed_size,
    })
}

/// The pane listing of an archive file or a folder inside one; `None` for
/// any other path. An archive file whose format only an absent plugin
/// serves fails with the plugin note, as does any path inside it.
pub fn list_archive_directory(path: &str) -> Result<Option<DirectoryListing>, String> {
    let Some(parsed) = split_archive_path(path)? else {
        if let Some(note) = super::placeholders::unavailable_archive_note(path) {
            return Err(note.to_string());
        }
        return Ok(None);
    };

    let archive_path_string = parsed.archive_path.to_string_lossy().to_string();
    let archive_info = list_archive_of_kind(&archive_path_string, &parsed.kind)?;
    let current_parts = normal_components(&parsed.inner_path);
    let mut entries: BTreeMap<String, FileEntry> = BTreeMap::new();

    for entry in archive_info.entries {
        let Ok(relative_path) = listing_entry_relative_path(&parsed.kind, &entry.path) else {
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

fn listing_entry_relative_path(kind: &ArchiveKind, entry_path: &str) -> Result<PathBuf, String> {
    archive_entry_relative_path_from_name(entry_path, &kind.entry_type())
}

fn built_in_entry_relative_path(
    format: ArchiveFormat,
    entry_path: &str,
) -> Result<PathBuf, String> {
    listing_entry_relative_path(&ArchiveKind::BuiltIn(format), entry_path)
}

fn archive_entry_name(entry_path: &str) -> String {
    entry_path
        .rsplit(['/', '\\'])
        .next()
        .unwrap_or(entry_path)
        .to_string()
}

#[cfg(test)]
mod seek_tests {
    use super::collect_tar_entries_seekable;
    use std::io::{Read, Seek, SeekFrom, Write};

    /// Counts bytes actually read so the test can prove that listing skips
    /// over entry contents instead of streaming the whole archive.
    struct CountingReader<R> {
        inner: R,
        read: u64,
    }

    impl<R: Read> Read for CountingReader<R> {
        fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
            let n = self.inner.read(buf)?;
            self.read += n as u64;
            Ok(n)
        }
    }

    impl<R: Seek> Seek for CountingReader<R> {
        fn seek(&mut self, pos: SeekFrom) -> std::io::Result<u64> {
            self.inner.seek(pos)
        }
    }

    #[test]
    fn listing_a_tar_seeks_past_entry_contents() {
        let payload = vec![b'x'; 8 * 1024 * 1024];
        let mut archive = Vec::new();
        {
            let mut builder = tar::Builder::new(&mut archive);
            let mut header = tar::Header::new_gnu();
            header.set_size(payload.len() as u64);
            header.set_mode(0o644);
            header.set_cksum();
            builder
                .append_data(&mut header, "big.bin", payload.as_slice())
                .unwrap();
            builder.finish().unwrap();
        }
        let path = std::env::temp_dir().join(format!(
            "sumafile-list-seek-{}-{}.tar",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::File::create(&path)
            .unwrap()
            .write_all(&archive)
            .unwrap();

        let mut reader = CountingReader {
            inner: std::fs::File::open(&path).unwrap(),
            read: 0,
        };
        let (entries, unsafe_entries, total) =
            collect_tar_entries_seekable(&mut reader, super::ArchiveFormat::Tar).unwrap();
        let _ = std::fs::remove_file(&path);

        assert_eq!(entries.len(), 1);
        assert!(unsafe_entries.is_empty());
        assert_eq!(total, payload.len() as u64);
        assert!(
            reader.read < 1024 * 1024,
            "listing read {} bytes of an 8 MiB archive",
            reader.read
        );
    }
}
