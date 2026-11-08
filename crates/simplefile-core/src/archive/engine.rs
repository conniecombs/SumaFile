//! Archive engines contributed from outside the core, such as a plugin's
//! backend process. The registry is consulted only after every built-in
//! format has declined a path, so a plugin can add formats but never take
//! over zip, tar, rar or 7z.

use std::path::{Path, PathBuf};
use std::sync::{Arc, OnceLock, RwLock};

/// One entry as an engine reports it; the archive layer decides which
/// entries are safe to show.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EngineEntry {
    pub path: String,
    pub is_dir: bool,
    pub size: u64,
    pub compressed_size: u64,
}

pub trait ArchiveEngine: Send + Sync {
    fn name(&self) -> &str;
    fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String>;
    fn extract(
        &self,
        archive: &Path,
        destination: &Path,
        inner_path: Option<&Path>,
    ) -> Result<(), String>;
    fn create(&self, archive: &Path, paths: &[PathBuf]) -> Result<(), String>;
}

/// A file extension an engine serves and what it can do with it. The
/// extension is stored lower case with a leading dot (`.tar.hello`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EngineFormat {
    pub extension: String,
    pub can_list: bool,
    pub can_extract: bool,
    pub can_create: bool,
    pub can_modify: bool,
}

struct Registration {
    owner: String,
    formats: Vec<EngineFormat>,
    engine: Arc<dyn ArchiveEngine>,
}

fn registry() -> &'static RwLock<Vec<Registration>> {
    static REGISTRY: OnceLock<RwLock<Vec<Registration>>> = OnceLock::new();
    REGISTRY.get_or_init(|| RwLock::new(Vec::new()))
}

fn normalize_extension(extension: &str) -> String {
    let trimmed = extension.trim().trim_start_matches('.').to_lowercase();
    format!(".{trimmed}")
}

/// Extensions a built-in format already handles never reach an engine, so
/// registering them would only advertise a capability nobody can use.
fn is_built_in_extension(extension: &str) -> bool {
    super::path::archive_format_for_path(Path::new(&format!("archive{extension}"))).is_some()
}

/// True when `extension` (with or without its dot, any case) could be
/// served by an engine: it is not empty and no built-in format claims it.
pub fn is_registrable_extension(extension: &str) -> bool {
    let normalized = normalize_extension(extension);
    normalized.len() > 1 && !is_built_in_extension(&normalized)
}

fn registrable(formats: Vec<EngineFormat>) -> Vec<EngineFormat> {
    formats
        .into_iter()
        .map(|format| EngineFormat {
            extension: normalize_extension(&format.extension),
            ..format
        })
        .filter(|format| is_registrable_extension(&format.extension))
        .collect()
}

/// Add `formats` served by `engine` on behalf of `owner`. An owner may
/// register several engines; `unregister_engine` removes all of them.
/// Formats with an empty or built-in extension are dropped.
pub fn register_engine(owner: &str, formats: Vec<EngineFormat>, engine: Arc<dyn ArchiveEngine>) {
    let formats = registrable(formats);
    if formats.is_empty() {
        return;
    }
    let mut registrations = registry()
        .write()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    registrations.push(Registration {
        owner: owner.to_string(),
        formats,
        engine,
    });
}

/// Replace everything `owner` registered with `engines` in one step, so
/// lookups never see the owner half-registered. Formats are filtered as in
/// `register_engine`; returns false and changes nothing when no
/// registrable format remains.
pub fn replace_engines(
    owner: &str,
    engines: Vec<(Vec<EngineFormat>, Arc<dyn ArchiveEngine>)>,
) -> bool {
    let replacements: Vec<Registration> = engines
        .into_iter()
        .map(|(formats, engine)| Registration {
            owner: owner.to_string(),
            formats: registrable(formats),
            engine,
        })
        .filter(|registration| !registration.formats.is_empty())
        .collect();
    if replacements.is_empty() {
        return false;
    }
    let mut registrations = registry()
        .write()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    registrations.retain(|registration| registration.owner != owner);
    registrations.extend(replacements);
    true
}

/// Remove every engine `owner` registered. Unknown owners are a no-op.
pub fn unregister_engine(owner: &str) {
    registry()
        .write()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .retain(|registration| registration.owner != owner);
}

/// The engine whose registered extension is the longest case-insensitive
/// suffix of the file name; among equal lengths the latest registration
/// wins. Built-in formats always resolve to `None` here.
pub fn engine_for_path(path: &Path) -> Option<(Arc<dyn ArchiveEngine>, EngineFormat)> {
    if super::path::archive_format_for_path(path).is_some() {
        return None;
    }
    let name = path.file_name()?.to_string_lossy().to_lowercase();
    let registrations = registry()
        .read()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut best: Option<(&Registration, &EngineFormat)> = None;
    for registration in registrations.iter().rev() {
        for format in &registration.formats {
            if name.len() > format.extension.len()
                && name.ends_with(&format.extension)
                && best.is_none_or(|(_, current)| format.extension.len() > current.extension.len())
            {
                best = Some((registration, format));
            }
        }
    }
    best.map(|(registration, format)| (Arc::clone(&registration.engine), format.clone()))
}

/// Every registered format as `(owner, format, engine name)`, in
/// registration order.
pub fn registered_formats() -> Vec<(String, EngineFormat, String)> {
    let registrations = registry()
        .read()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    registrations
        .iter()
        .flat_map(|registration| {
            registration.formats.iter().map(|format| {
                (
                    registration.owner.clone(),
                    format.clone(),
                    registration.engine.name().to_string(),
                )
            })
        })
        .collect()
}

/// The extension without its leading dot, used as the format label.
pub(super) fn format_label(format: &EngineFormat) -> String {
    format.extension.trim_start_matches('.').to_string()
}
