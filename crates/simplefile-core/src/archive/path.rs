use std::ffi::{OsStr, OsString};
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use super::engine::{ArchiveEngine, EngineFormat};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum ArchiveFormat {
    Zip,
    Tar,
    TarGz,
    Rar,
}

impl ArchiveFormat {
    pub(super) fn label(self) -> &'static str {
        match self {
            ArchiveFormat::Zip => "zip",
            ArchiveFormat::Tar => "tar",
            ArchiveFormat::TarGz => "tar.gz",
            ArchiveFormat::Rar => "rar",
        }
    }
}

/// How an archive is served: by one of the core's own formats, or by a
/// registered engine for the extension it claimed.
#[derive(Clone)]
pub(super) enum ArchiveKind {
    BuiltIn(ArchiveFormat),
    Engine {
        engine: Arc<dyn ArchiveEngine>,
        format: EngineFormat,
    },
}

impl std::fmt::Debug for ArchiveKind {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::BuiltIn(format) => f.debug_tuple("BuiltIn").field(format).finish(),
            Self::Engine { engine, format } => f
                .debug_struct("Engine")
                .field("engine", &engine.name())
                .field("extension", &format.extension)
                .finish(),
        }
    }
}

impl ArchiveKind {
    /// The format label reported in listings and error text (`zip`, `tar`,
    /// or an engine extension without its dot, such as `7z`).
    pub(super) fn label(&self) -> String {
        match self {
            Self::BuiltIn(format) => format.label().to_string(),
            Self::Engine { format, .. } => super::engine::format_label(format),
        }
    }

    /// The archive type unsafe-entry-name messages start with.
    pub(super) fn entry_type(&self) -> String {
        match self {
            Self::BuiltIn(ArchiveFormat::Zip) => "Zip".to_string(),
            Self::BuiltIn(ArchiveFormat::Tar | ArchiveFormat::TarGz) => "Tar".to_string(),
            Self::BuiltIn(ArchiveFormat::Rar) => "RAR".to_string(),
            Self::Engine { engine, .. } => engine.name().to_string(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct ArchivePath {
    pub(super) archive_path: PathBuf,
    pub(super) inner_path: PathBuf,
    pub(super) kind: ArchiveKind,
}

/// A built-in format first; otherwise the registered engine, if any.
pub(super) fn archive_kind_for_path(path: &Path) -> Option<ArchiveKind> {
    if let Some(format) = archive_format_for_path(path) {
        return Some(ArchiveKind::BuiltIn(format));
    }
    super::engine::engine_for_path(path)
        .map(|(engine, format)| ArchiveKind::Engine { engine, format })
}

pub(super) fn archive_format_for_path(path: &Path) -> Option<ArchiveFormat> {
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

/// Split `path` at the first archive file along it. A path inside a file
/// whose format only an absent plugin serves (`x.7z\docs`) fails with the
/// plugin note; that file itself is no archive (`Ok(None)`), so it can
/// still be copied, renamed or deleted.
pub fn split_archive_path(path: &str) -> Result<Option<ArchivePath>, String> {
    let input = PathBuf::from(path);
    let mut archive_path = PathBuf::new();
    let mut inner_path = PathBuf::new();
    let mut found_kind: Option<ArchiveKind> = None;
    let mut unavailable_note: Option<&'static str> = None;

    for component in input.components() {
        if let Some(note) = unavailable_note {
            if !matches!(component, Component::CurDir) {
                return Err(note.to_string());
            }
            continue;
        }
        if found_kind.is_some() {
            match component {
                Component::Normal(part) => inner_path.push(part),
                Component::CurDir => {}
                Component::ParentDir | Component::Prefix(_) | Component::RootDir => {
                    return Err(format!("Archive entry has unsafe path: {path}"));
                }
            }
            continue;
        }

        archive_path.push(component.as_os_str());
        if archive_path.is_file() {
            found_kind = archive_kind_for_path(&archive_path);
            if found_kind.is_none() {
                unavailable_note = super::placeholders::unavailable_note_for_path(&archive_path);
            }
        }
    }

    Ok(found_kind.map(|kind| ArchivePath {
        archive_path,
        inner_path,
        kind,
    }))
}

/// True for an archive file or any path inside one, a built-in format or an
/// extension a registered engine serves. No archive is opened to decide.
pub fn is_archive_path(path: &str) -> bool {
    split_archive_path(path).ok().flatten().is_some()
}

pub fn is_archive_virtual_path(path: &str) -> bool {
    split_archive_path(path)
        .ok()
        .flatten()
        .is_some_and(|parsed| !parsed.inner_path.as_os_str().is_empty())
}

pub(super) fn normal_components(path: &Path) -> Vec<OsString> {
    path.components()
        .filter_map(|component| match component {
            Component::Normal(part) => Some(part.to_os_string()),
            _ => None,
        })
        .collect()
}

pub(super) fn build_virtual_archive_path(archive_path: &Path, inner_path: &Path) -> String {
    let mut path = archive_path.to_string_lossy().to_string();
    if !inner_path.as_os_str().is_empty() {
        if !path.ends_with(['\\', '/']) {
            path.push(std::path::MAIN_SEPARATOR);
        }
        path.push_str(&inner_path.to_string_lossy());
    }
    path
}

pub(super) fn zip_entry_relative_path(entry_name: &str) -> Result<PathBuf, String> {
    archive_entry_relative_path_from_name(entry_name, "Zip")
}

pub(super) fn archive_entry_relative_path(
    entry_path: &Path,
    archive_type: &str,
) -> Result<PathBuf, String> {
    archive_entry_relative_path_from_name(&entry_path.to_string_lossy(), archive_type)
}

pub(super) fn archive_entry_relative_path_from_name(
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
pub(super) fn ensure_extract_path_within_destination(
    dest: &Path,
    candidate: &Path,
) -> Result<(), String> {
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

pub(super) fn top_level_remap(
    dest: &Path,
    relative_paths: &[PathBuf],
) -> Option<(OsString, OsString)> {
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

pub(super) fn output_path_for_entry(
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

pub(super) fn unique_file_path_if_needed(path: &Path) -> PathBuf {
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

pub(super) fn create_dir_all(path: &Path) -> Result<(), String> {
    std::fs::create_dir_all(path)
        .map_err(|e| format!("Failed to create directory {}: {}", path.display(), e))
}

pub(super) fn create_file(path: &Path) -> Result<std::fs::File, String> {
    std::fs::File::create(path)
        .map_err(|e| format!("Failed to create extracted file {}: {}", path.display(), e))
}

pub(super) fn copy_entry_data<R: std::io::Read, W: std::io::Write>(
    reader: &mut R,
    writer: &mut W,
    path: &Path,
) -> Result<(), String> {
    std::io::copy(reader, writer)
        .map(|_| ())
        .map_err(|e| format!("Failed to write extracted file {}: {}", path.display(), e))
}

/// Copy archive entry bytes while enforcing a running uncompressed-size budget.
pub(super) fn copy_entry_data_limited<R: std::io::Read, W: std::io::Write>(
    reader: &mut R,
    writer: &mut W,
    path: &Path,
    max_uncompressed_bytes: u64,
    written_total: &mut u64,
) -> Result<(), String> {
    let mut buffer = [0u8; 65536];
    loop {
        let n = std::io::Read::read(reader, &mut buffer)
            .map_err(|e| format!("Failed to read archive entry for {}: {}", path.display(), e))?;
        if n == 0 {
            break;
        }
        let next = written_total
            .checked_add(n as u64)
            .ok_or_else(|| "Archive extract size overflow".to_string())?;
        if next > max_uncompressed_bytes {
            return Err(format!(
                "Archive extract exceeds size limit of {max_uncompressed_bytes} bytes"
            ));
        }
        std::io::Write::write_all(writer, &buffer[..n])
            .map_err(|e| format!("Failed to write extracted file {}: {}", path.display(), e))?;
        *written_total = next;
    }
    Ok(())
}

/// True when `candidate` is exactly `prefix` or a nested path under it.
pub(super) fn path_is_within_prefix(candidate: &Path, prefix: &Path) -> bool {
    if candidate == prefix {
        return true;
    }
    let mut prefix_iter = prefix.components();
    let mut candidate_iter = candidate.components();
    loop {
        match (prefix_iter.next(), candidate_iter.next()) {
            (None, None) => return true,
            (None, Some(_)) => return true,
            (Some(_), None) => return false,
            (Some(left), Some(right)) => {
                if left.as_os_str() != right.as_os_str() {
                    return false;
                }
            }
        }
    }
}

pub(super) fn replace_archive(archive_path: &Path, new_archive_path: &Path) -> Result<(), String> {
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

pub(super) fn unique_temp_archive_path(path: &Path) -> Result<PathBuf, String> {
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

/// A free temp path beside `archive` for an engine to create a new archive
/// at: `.{stem}.simplefile-new-{nonce}-{i}{extension}`, where `stem` is the
/// file name without `extension`. Keeping the extension lets an engine that
/// picks its format from the name write the right one.
pub(super) fn unique_engine_temp_path(archive: &Path, extension: &str) -> Result<PathBuf, String> {
    let parent = archive.parent().unwrap_or_else(|| Path::new("."));
    let name = archive
        .file_name()
        .ok_or_else(|| format!("Cannot get archive file name for {}", archive.display()))?
        .to_string_lossy()
        .into_owned();
    let stem = if name.len() > extension.len()
        && name.to_lowercase().ends_with(&extension.to_lowercase())
        && name.is_char_boundary(name.len() - extension.len())
    {
        &name[..name.len() - extension.len()]
    } else {
        name.as_str()
    };
    let nonce = unique_nonce();
    for i in 0..10_000u32 {
        let candidate = parent.join(format!(".{stem}.simplefile-new-{nonce}-{i}{extension}"));
        if !candidate.exists() {
            return Ok(candidate);
        }
    }
    Err("Could not create a unique temporary archive path".to_string())
}

pub(super) fn unique_work_dir(label: &str) -> Result<PathBuf, String> {
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

pub(super) fn same_archive_path(left: &Path, right: &Path) -> bool {
    match (left.canonicalize(), right.canonicalize()) {
        (Ok(left), Ok(right)) => left == right,
        _ => left.to_string_lossy().to_lowercase() == right.to_string_lossy().to_lowercase(),
    }
}
