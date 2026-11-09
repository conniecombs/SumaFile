//! Archive engines contributed from outside the core, such as a plugin's
//! backend process. The registry is consulted only after every built-in
//! format (zip, tar, tar.gz and rar) has declined a path, so a plugin can
//! add formats but never take over a format the core handles itself.
//! `.7z` is served only by an engine, normally the SumaFile 7-Zip plugin;
//! while none is registered, `placeholders` reports it unavailable.
//!
//! Engines do the format work only. The archive layer keeps every safety
//! rule: it validates entry names from the engine's own listing, hands the
//! engine an empty private folder to write into, and moves only the listed
//! entries out of it, within the size and entry budgets.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock, RwLock};

/// The error text of an archive operation stopped through its
/// `EngineContext`.
pub const CANCELLED_MESSAGE: &str = "Archive operation cancelled";

/// One entry as an engine reports it; the archive layer decides which
/// entries are safe to show.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EngineEntry {
    pub path: String,
    pub is_dir: bool,
    pub size: u64,
    pub compressed_size: u64,
}

/// How far a long engine operation has got. `total` 0 means unknown.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EngineProgress {
    pub current: u64,
    pub total: u64,
    pub current_item: Option<String>,
}

/// What a caller passes along with an extract, create or mutation: the
/// operation it belongs to, a flag that asks the engine to stop, and where
/// progress goes.
pub struct EngineContext<'a> {
    pub operation_id: Option<&'a str>,
    pub cancel: &'a AtomicBool,
    pub progress: &'a (dyn Fn(EngineProgress) + Sync),
}

/// Shared by every untracked context. Nothing in the crate stores to it,
/// and engines must only ever read a context's flag.
static NEVER_CANCELLED: AtomicBool = AtomicBool::new(false);

fn drop_progress(_progress: EngineProgress) {}

impl EngineContext<'static> {
    /// No operation id, never cancelled, progress dropped.
    pub fn untracked() -> Self {
        Self {
            operation_id: None,
            cancel: &NEVER_CANCELLED,
            progress: &drop_progress,
        }
    }
}

impl EngineContext<'_> {
    pub fn is_cancelled(&self) -> bool {
        self.cancel.load(Ordering::SeqCst)
    }

    /// Report progress; `current` is capped at a known `total`.
    pub fn report(&self, current: u64, total: u64, current_item: Option<&str>) {
        let current = if total > 0 {
            current.min(total)
        } else {
            current
        };
        (self.progress)(EngineProgress {
            current,
            total,
            current_item: current_item.map(str::to_string),
        });
    }

    /// `Err(CANCELLED_MESSAGE)` once the caller has asked to stop.
    pub(super) fn check_cancelled(&self) -> Result<(), String> {
        if self.is_cancelled() {
            Err(CANCELLED_MESSAGE.to_string())
        } else {
            Ok(())
        }
    }
}

/// The budget for a selective read: the most bytes and entries the
/// selection may hold.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EngineLimits {
    pub max_bytes: u64,
    pub max_entries: u32,
}

/// Entry paths an engine receives and returns are `/`-separated and
/// relative to the archive root.
pub trait ArchiveEngine: Send + Sync {
    fn name(&self) -> &str;
    fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String>;
    /// Every entry into `destination`, an empty folder the archive layer owns.
    fn extract(
        &self,
        archive: &Path,
        destination: &Path,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String>;
    /// `entry` (`/`-separated) and everything under it, at its own relative
    /// path inside the empty folder `destination`.
    fn read_entry(
        &self,
        archive: &Path,
        entry: &str,
        destination: &Path,
        limits: EngineLimits,
    ) -> Result<(), String>;
    /// A new archive at `archive`, which does not exist yet.
    fn create(
        &self,
        archive: &Path,
        paths: &[PathBuf],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String>;
    /// Move `entry` and its descendants to the full path `new_path`.
    /// Changes the archive in place, atomically.
    fn rename(
        &self,
        archive: &Path,
        entry: &str,
        new_path: &str,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String>;
    /// Remove each entry and its descendants, in place and atomically.
    fn delete(
        &self,
        archive: &Path,
        entries: &[String],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String>;
    /// Store `base_dir/<p>` (a file or a tree) at `<p>` for each
    /// `/`-separated `p`, in place and atomically.
    fn add(
        &self,
        archive: &Path,
        base_dir: &Path,
        paths: &[String],
        ctx: &EngineContext<'_>,
    ) -> Result<(), String>;
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

/// The engine's listing with every entry's validated relative path. Any
/// unsafe name fails the whole listing, so nothing is written for an
/// archive that carries one.
pub(super) fn engine_entries(
    engine: &dyn ArchiveEngine,
    archive: &Path,
) -> Result<Vec<(EngineEntry, PathBuf)>, String> {
    engine
        .list_entries(archive)?
        .into_iter()
        .map(|entry| {
            let relative =
                super::path::archive_entry_relative_path_from_name(&entry.path, engine.name())?;
            Ok((entry, relative))
        })
        .collect()
}
