//! Mutations of archives a registered engine serves.
//!
//! Every operation lists the archive first, and the core resolves names and
//! conflicts against that fresh listing. The engine only changes the archive
//! in place, through `add`, `rename` and `delete`. New content is staged in a
//! private work folder first, and every path handed to the engine is a
//! validated, `/`-separated entry path.

use std::collections::HashMap;
use std::ffi::OsStr;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use super::engine::{engine_entries, ArchiveEngine, EngineContext, EngineFormat};
use super::mutate::{copy_path_to_destination, materialize_archive_entry_to_temp, WorkRootGuard};
use super::path::{
    archive_entry_relative_path_from_name, build_virtual_archive_path, create_dir_all,
    same_archive_path, ArchiveKind, ArchivePath,
};

/// The engine and format behind an engine-backed archive path.
pub(super) struct EngineArchive<'a> {
    archive: &'a Path,
    engine: &'a Arc<dyn ArchiveEngine>,
    format: &'a EngineFormat,
}

impl<'a> EngineArchive<'a> {
    /// `None` for a built-in archive.
    pub(super) fn of(parsed: &'a ArchivePath) -> Option<Self> {
        match &parsed.kind {
            ArchiveKind::Engine { engine, format } => Some(Self {
                archive: &parsed.archive_path,
                engine,
                format,
            }),
            ArchiveKind::BuiltIn(_) => None,
        }
    }

    fn name(&self) -> &str {
        self.engine.name()
    }

    fn ensure_can_modify(&self) -> Result<(), String> {
        ensure_engine_can_modify(self.name(), self.format)
    }

    fn listing(&self) -> Result<Listing, String> {
        Ok(Listing::new(engine_entries(
            self.engine.as_ref(),
            self.archive,
        )?))
    }

    fn virtual_path(&self, inner: &str) -> String {
        build_virtual_archive_path(self.archive, &inner_path_buf(inner))
    }

    /// A new entry path built by the core (a destination folder plus a
    /// name), checked by the same rule the listing is.
    fn validated(&self, inner: &str) -> Result<String, String> {
        archive_entry_relative_path_from_name(inner, self.name()).map(|path| inner_string(&path))
    }

    fn add(&self, work: &Path, inner: &str) -> Result<(), String> {
        self.engine.add(
            self.archive,
            work,
            &[inner.to_string()],
            &EngineContext::untracked(),
        )
    }

    fn delete(&self, inners: &[String]) -> Result<(), String> {
        self.engine
            .delete(self.archive, inners, &EngineContext::untracked())
    }

    fn rename(&self, inner: &str, new_path: &str) -> Result<(), String> {
        self.engine
            .rename(self.archive, inner, new_path, &EngineContext::untracked())
    }
}

/// `{engine name} cannot modify {extension} archives` unless the format
/// declares `can_modify`.
pub(super) fn ensure_engine_can_modify(
    engine_name: &str,
    format: &EngineFormat,
) -> Result<(), String> {
    if format.can_modify {
        Ok(())
    } else {
        Err(format!(
            "{engine_name} cannot modify {} archives",
            format.extension
        ))
    }
}

#[derive(Debug, Clone)]
struct ListedEntry {
    /// The entry path as the archive spells it, `/`-separated.
    path: String,
    is_dir: bool,
}

/// One fresh listing. Folders that only exist as a prefix of a listed path
/// are included.
///
/// An archive can hold names that differ only by case (`A.txt` and `a.txt`,
/// common in archives made on Linux), and the engine acts on exact names.
/// So an existing entry is found by its exact spelling first, and by a
/// case-insensitive match only when exactly one listed entry has it; more
/// than one is an ambiguity error. Conflict checks for new names stay
/// case-insensitive, which is how the built-in formats see names once they
/// are extracted to a Windows folder.
struct Listing {
    /// Keyed by the exact `/`-separated spelling.
    exact: HashMap<String, ListedEntry>,
    /// Case-folded key to every exact spelling that folds to it.
    folded: HashMap<String, Vec<String>>,
}

impl Listing {
    fn new(listed: Vec<(super::engine::EngineEntry, PathBuf)>) -> Self {
        let mut exact = HashMap::new();
        for (entry, relative) in listed {
            let parts: Vec<String> = relative
                .components()
                .map(|component| component.as_os_str().to_string_lossy().into_owned())
                .collect();
            for depth in 1..parts.len() {
                let folder = parts[..depth].join("/");
                exact.entry(folder.clone()).or_insert(ListedEntry {
                    path: folder,
                    is_dir: true,
                });
            }
            let path = parts.join("/");
            exact.insert(
                path.clone(),
                ListedEntry {
                    path,
                    is_dir: entry.is_dir,
                },
            );
        }
        let mut folded: HashMap<String, Vec<String>> = HashMap::new();
        for path in exact.keys() {
            folded.entry(key(path)).or_default().push(path.clone());
        }
        Self { exact, folded }
    }

    /// The listed entry `inner` names: its exact spelling, or the one entry
    /// that matches it case-insensitively. `virtual_path` names `inner` in
    /// the ambiguity error.
    fn find(&self, inner: &str, virtual_path: &str) -> Result<Option<&ListedEntry>, String> {
        if let Some(entry) = self.exact.get(inner) {
            return Ok(Some(entry));
        }
        match self.folded.get(&key(inner)).map(Vec::as_slice) {
            None | Some([]) => Ok(None),
            Some([only]) => Ok(self.exact.get(only)),
            Some(_) => Err(format!("Ambiguous archive entry name: {virtual_path}")),
        }
    }

    /// Whether any listed entry has `inner`'s name, ignoring case.
    fn contains(&self, inner: &str) -> bool {
        self.folded.contains_key(&key(inner))
    }

    /// Whether a listed entry other than `except` has `inner`'s name,
    /// ignoring case.
    fn taken_by_other(&self, inner: &str, except: &str) -> bool {
        self.folded
            .get(&key(inner))
            .is_some_and(|paths| paths.iter().any(|path| path != except))
    }
}

fn key(inner: &str) -> String {
    inner.to_lowercase()
}

/// `inner` as a `/`-separated entry path.
fn inner_string(inner: &Path) -> String {
    inner
        .components()
        .map(|component| component.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

fn inner_path_buf(inner: &str) -> PathBuf {
    inner.split('/').filter(|part| !part.is_empty()).collect()
}

fn join_inner(folder: &str, name: &str) -> String {
    if folder.is_empty() {
        name.to_string()
    } else {
        format!("{folder}/{name}")
    }
}

fn parent_inner(inner: &str) -> &str {
    inner.rsplit_once('/').map_or("", |(parent, _)| parent)
}

/// Exact and segment-wise: both sides are listed (or canonical) spellings.
fn is_at_or_under(inner: &str, folder: &str) -> bool {
    inner == folder || inner.starts_with(&format!("{folder}/"))
}

/// The listed entry at `inner`, or `Archive entry not found: <path>`, or
/// an ambiguity error when `inner` only matches several entries by case.
fn require_entry<'l>(
    listing: &'l Listing,
    inner: &str,
    path: &str,
) -> Result<&'l ListedEntry, String> {
    listing
        .find(inner, path)?
        .ok_or_else(|| format!("Archive entry not found: {path}"))
}

/// A destination folder inside the archive, spelled as listed segment by
/// segment so new entries land in the folder the archive already holds.
/// No segment may be a listed file. Segments may be missing; the engine
/// creates them with the first entry under them.
fn require_folder(
    target: &EngineArchive<'_>,
    listing: &Listing,
    folder: &str,
) -> Result<String, String> {
    let mut canonical = String::new();
    for part in folder.split('/').filter(|part| !part.is_empty()) {
        let candidate = join_inner(&canonical, part);
        canonical = match listing.find(&candidate, &target.virtual_path(&candidate))? {
            Some(entry) if !entry.is_dir => {
                return Err(format!(
                    "Destination is not a directory: {}",
                    target.virtual_path(folder)
                ))
            }
            Some(entry) => entry.path.clone(),
            None => candidate,
        };
    }
    Ok(canonical)
}

pub(super) enum NewEntry {
    Directory,
    File,
}

/// Stage an empty folder or file at `<work>/<inner>/<name>` and add it.
pub(super) fn create_entry(
    parsed: &ArchivePath,
    name: &str,
    kind: NewEntry,
) -> Result<String, String> {
    let target = engine_archive(parsed)?;
    target.ensure_can_modify()?;
    let listing = target.listing()?;
    let folder = require_folder(&target, &listing, &inner_string(&parsed.inner_path))?;
    let inner = target.validated(&join_inner(&folder, name))?;
    if listing.contains(&inner) {
        return Err(match kind {
            NewEntry::Directory => format!("Directory already exists: {name}"),
            NewEntry::File => format!("File already exists: {name}"),
        });
    }

    let work = WorkRootGuard::create("engine-add")?;
    let staged = work.path().join(inner_path_buf(&inner));
    match kind {
        NewEntry::Directory => create_dir_all(&staged)?,
        NewEntry::File => {
            if let Some(parent) = staged.parent() {
                create_dir_all(parent)?;
            }
            fs::File::create(&staged).map_err(|e| format!("Failed to create file: {e}"))?;
        }
    }
    target.add(work.path(), &inner)?;
    Ok(target.virtual_path(&inner))
}

/// Rename an entry in place: one `engine.rename` to `<parent>/<new_name>`.
pub(super) fn rename_entry(
    parsed: &ArchivePath,
    path: &str,
    new_name: &str,
) -> Result<String, String> {
    let target = engine_archive(parsed)?;
    target.ensure_can_modify()?;
    let listing = target.listing()?;
    let source = require_entry(&listing, &inner_string(&parsed.inner_path), path)?.clone();
    let current_name = source.path.rsplit('/').next().unwrap_or(&source.path);
    if current_name == new_name {
        return Ok(target.virtual_path(&source.path));
    }
    let new_path = target.validated(&join_inner(parent_inner(&source.path), new_name))?;
    // A change of case only is still a rename of the same entry, unless
    // another entry already has that name.
    if listing.taken_by_other(&new_path, &source.path) {
        return Err(format!(
            "A file or directory with that name already exists: {new_name}"
        ));
    }
    target.rename(&source.path, &new_path)?;
    Ok(target.virtual_path(&new_path))
}

/// Delete `(path, inner)` pairs that all sit in `parsed`'s archive with one
/// `engine.delete`. A path missing from the fresh listing fails the whole
/// call before the engine runs.
pub(super) fn delete_entries(
    parsed: &ArchivePath,
    entries: &[(String, PathBuf)],
) -> Result<(), String> {
    let target = engine_archive(parsed)?;
    target.ensure_can_modify()?;
    let listing = target.listing()?;
    let mut resolved: Vec<String> = Vec::new();
    for (path, inner) in entries {
        let entry = require_entry(&listing, &inner_string(inner), path)?;
        if !resolved.contains(&entry.path) {
            resolved.push(entry.path.clone());
        }
    }
    // An entry under another selected folder goes with that folder.
    let selection: Vec<String> = resolved
        .iter()
        .filter(|inner| {
            !resolved
                .iter()
                .any(|other| other != *inner && is_at_or_under(inner, other))
        })
        .cloned()
        .collect();
    target.delete(&selection)
}

/// Where a copy or move lands once the conflict action is applied.
enum Placement {
    Skip,
    Place {
        inner: String,
        /// The listed entry `replace` removes first.
        replaces: Option<String>,
    },
}

fn resolve_placement(
    target: &EngineArchive<'_>,
    listing: &Listing,
    folder: &str,
    name: &OsStr,
    conflict_action: &str,
) -> Result<Placement, String> {
    let name = name.to_string_lossy();
    let inner = target.validated(&join_inner(folder, &name))?;
    if !listing.contains(&inner) {
        return Ok(Placement::Place {
            inner,
            replaces: None,
        });
    }
    match conflict_action.to_ascii_lowercase().as_str() {
        "skip" => Ok(Placement::Skip),
        "replace" => {
            // Remove the exact spelling the new entry lands on, or the one
            // entry it matches by case; never a guess between several.
            let virtual_path = target.virtual_path(&inner);
            let existing = listing
                .find(&inner, &virtual_path)?
                .ok_or_else(|| format!("Archive entry not found: {virtual_path}"))?;
            Ok(Placement::Place {
                replaces: Some(existing.path.clone()),
                inner,
            })
        }
        "rename" | "keep-both" | "keep_both" => Ok(Placement::Place {
            inner: unique_inner(target, listing, folder, &name)?,
            replaces: None,
        }),
        _ => Err(format!(
            "CONFLICT: destination already exists: {}",
            target.virtual_path(&inner)
        )),
    }
}

/// `stem (i).ext` in `folder`, the first one the listing does not hold.
fn unique_inner(
    target: &EngineArchive<'_>,
    listing: &Listing,
    folder: &str,
    name: &str,
) -> Result<String, String> {
    let original = Path::new(name);
    let stem = original
        .file_stem()
        .map_or_else(|| name.to_string(), |s| s.to_string_lossy().into_owned());
    let ext = original
        .extension()
        .map(|e| e.to_string_lossy().into_owned());
    for i in 1..10_000u32 {
        let candidate = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({i}).{ext}"),
            _ => format!("{stem} ({i})"),
        };
        let inner = join_inner(folder, &candidate);
        if !listing.contains(&inner) {
            return target.validated(&inner);
        }
    }
    Err(format!("Could not find a free name for {name}"))
}

/// Copy a local path, or an entry of any archive, into the engine archive
/// `destination`: the source is materialized, staged at its final entry
/// path and added.
pub(super) fn copy_into(
    source: &str,
    source_parsed: Option<&ArchivePath>,
    destination: &ArchivePath,
    conflict_action: &str,
) -> Result<String, String> {
    let target = engine_archive(destination)?;
    target.ensure_can_modify()?;
    let mut materialized = match source_parsed {
        Some(_) => materialize_archive_entry_to_temp(source)?,
        None => {
            super::mutate::MaterializedSource::local(crate::utils::validate_path_no_follow(source)?)
        }
    };
    let result = copy_materialized_into(
        &target,
        source,
        source_parsed,
        materialized.path(),
        destination,
        conflict_action,
    );
    materialized.cleanup();
    result
}

fn copy_materialized_into(
    target: &EngineArchive<'_>,
    source: &str,
    source_parsed: Option<&ArchivePath>,
    source_path: &Path,
    destination: &ArchivePath,
    conflict_action: &str,
) -> Result<String, String> {
    let listing = target.listing()?;
    let folder = require_folder(target, &listing, &inner_string(&destination.inner_path))?;
    let name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let (inner, replaces) =
        match resolve_placement(target, &listing, &folder, name, conflict_action)? {
            Placement::Skip => return Ok(format!("SKIPPED:{source}")),
            Placement::Place { inner, replaces } => (inner, replaces),
        };
    // A copy that stays in this archive and lands on its own source entry
    // changes nothing.
    let source_entry = match source_parsed {
        Some(parsed) if same_archive_path(&parsed.archive_path, target.archive) => {
            listing.find(&inner_string(&parsed.inner_path), source)?
        }
        _ => None,
    };
    if let Some(entry) = source_entry {
        if entry.path == inner || replaces.as_deref() == Some(entry.path.as_str()) {
            return Ok(target.virtual_path(&entry.path));
        }
    }

    // Stage before anything is removed, so a failed copy changes nothing.
    let work = WorkRootGuard::create("engine-add")?;
    copy_path_to_destination(source_path, &work.path().join(inner_path_buf(&inner)))?;
    if let Some(existing) = replaces {
        target.delete(&[existing])?;
    }
    target.add(work.path(), &inner)?;
    Ok(target.virtual_path(&inner))
}

/// Move an entry to another folder of the same engine archive: one
/// `engine.rename` to the full destination path, after the conflict rules.
pub(super) fn move_within(
    source: &str,
    source_parsed: &ArchivePath,
    destination: &ArchivePath,
    conflict_action: &str,
) -> Result<String, String> {
    let target = engine_archive(destination)?;
    target.ensure_can_modify()?;
    let listing = target.listing()?;
    let moving = require_entry(&listing, &inner_string(&source_parsed.inner_path), source)?.clone();
    let folder = require_folder(&target, &listing, &inner_string(&destination.inner_path))?;
    if moving.is_dir && is_at_or_under(&folder, &moving.path) {
        return Err("Cannot move a directory into itself or one of its subdirectories".to_string());
    }
    let name = moving
        .path
        .rsplit('/')
        .next()
        .unwrap_or(&moving.path)
        .to_string();
    let (inner, replaces) = match resolve_placement(
        &target,
        &listing,
        &folder,
        OsStr::new(&name),
        conflict_action,
    )? {
        Placement::Skip => return Ok(format!("SKIPPED:{source}")),
        Placement::Place { inner, replaces } => (inner, replaces),
    };
    if inner == moving.path {
        return Ok(target.virtual_path(&inner));
    }
    if let Some(existing) = replaces {
        if is_at_or_under(&moving.path, &existing) {
            return Err(format!(
                "Cannot replace a folder that contains the moved entry: {}",
                target.virtual_path(&existing)
            ));
        }
        target.delete(&[existing])?;
    }
    target.rename(&moving.path, &inner)?;
    Ok(target.virtual_path(&inner))
}

fn engine_archive(parsed: &ArchivePath) -> Result<EngineArchive<'_>, String> {
    EngineArchive::of(parsed).ok_or_else(|| {
        format!(
            "{} is not served by an archive engine",
            parsed.archive_path.display()
        )
    })
}
