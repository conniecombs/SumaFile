//! `JsonEngine`: an archive engine test double whose archive file is JSON,
//! `{"entries":[{"path":"docs/a.txt","dir":false,"data":"text"}]}`.
//!
//! It does real work (it writes the files it is asked to extract and
//! rewrites the archive on a mutation, through a temp file and a rename),
//! so the core archive layer can be tested end to end without a plugin.
//! Knobs let a test make it misbehave the ways a real engine might.

use super::super::engine::{ArchiveEngine, EngineContext, EngineEntry, EngineLimits};
use super::super::{register_engine, EngineFormat, CANCELLED_MESSAGE};
use super::EngineRegistration;
use serde_json::{json, Value};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

/// One entry of a JSON archive: `(path, is_dir, data)`.
pub(super) type JsonEntry = (String, bool, String);

#[derive(Default)]
pub(super) struct JsonEngine {
    /// `extract` also writes an unlisted `stray.txt`.
    pub stray_file: bool,
    /// `extract` creates this listed folder as a junction (`mklink /J`)
    /// pointing outside the destination, then writes through it.
    pub junction_dir: Option<&'static str>,
    /// `read_entry` writes this many bytes for every file instead of its data.
    pub oversize_bytes: Option<u64>,
    /// The next mutation or create fails with this message; a failed create
    /// leaves a partial file behind.
    pub fail_with: Mutex<Option<String>>,
    /// Every call, as `"<method> <args>"`.
    pub calls: Mutex<Vec<String>>,
}

impl JsonEngine {
    pub(super) fn calls(&self) -> Vec<String> {
        self.calls.lock().unwrap().clone()
    }

    fn record(&self, call: String) {
        self.calls.lock().unwrap().push(call);
    }

    fn take_failure(&self) -> Result<(), String> {
        match self.fail_with.lock().unwrap().take() {
            Some(message) => Err(message),
            None => Ok(()),
        }
    }
}

pub(super) fn write_json_archive(archive: &Path, entries: &[(&str, bool, &str)]) {
    let owned: Vec<JsonEntry> = entries
        .iter()
        .map(|(path, dir, data)| ((*path).to_string(), *dir, (*data).to_string()))
        .collect();
    write_entries(archive, &owned).expect("write JSON archive");
}

/// The entries a JSON archive holds now, in stored order.
pub(super) fn read_json_archive(archive: &Path) -> Vec<JsonEntry> {
    read_entries(archive).expect("read JSON archive")
}

fn read_entries(archive: &Path) -> Result<Vec<JsonEntry>, String> {
    let text = fs::read_to_string(archive).map_err(|e| format!("json-engine read: {e}"))?;
    let value: Value =
        serde_json::from_str(&text).map_err(|e| format!("json-engine parse: {e}"))?;
    let entries = value["entries"]
        .as_array()
        .ok_or_else(|| "json-engine: no entries array".to_string())?;
    Ok(entries
        .iter()
        .map(|entry| {
            (
                entry["path"].as_str().unwrap_or_default().to_string(),
                entry["dir"].as_bool().unwrap_or(false),
                entry["data"].as_str().unwrap_or_default().to_string(),
            )
        })
        .collect())
}

fn entries_json(entries: &[JsonEntry]) -> Value {
    json!({
        "entries": entries
            .iter()
            .map(|(path, dir, data)| json!({ "path": path, "dir": dir, "data": data }))
            .collect::<Vec<_>>()
    })
}

fn write_entries(archive: &Path, entries: &[JsonEntry]) -> Result<(), String> {
    fs::write(archive, entries_json(entries).to_string())
        .map_err(|e| format!("json-engine write: {e}"))
}

/// Rewrite an existing archive in place: write a temp file beside it, then
/// rename it over the original.
fn rewrite_entries(archive: &Path, entries: &[JsonEntry]) -> Result<(), String> {
    let temp = archive.with_extension("json-engine-tmp");
    write_entries(&temp, entries)?;
    fs::rename(&temp, archive).map_err(|e| format!("json-engine replace: {e}"))
}

fn is_at_or_under(path: &str, entry: &str) -> bool {
    path == entry || path.starts_with(&format!("{entry}/"))
}

fn write_entry(destination: &Path, path: &str, is_dir: bool, data: &[u8]) -> Result<(), String> {
    let target = destination.join(path);
    if is_dir {
        return fs::create_dir_all(&target).map_err(|e| format!("json-engine mkdir: {e}"));
    }
    if let Some(parent) = target.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("json-engine mkdir: {e}"))?;
    }
    fs::write(&target, data).map_err(|e| format!("json-engine write entry: {e}"))
}

pub(super) fn make_junction(link: &Path, target: &Path) -> Result<(), String> {
    fs::create_dir_all(target).map_err(|e| format!("json-engine junction target: {e}"))?;
    let status = std::process::Command::new("cmd")
        .arg("/c")
        .arg("mklink")
        .arg("/J")
        .arg(link)
        .arg(target)
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .status()
        .map_err(|e| format!("json-engine mklink: {e}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("json-engine mklink failed: {status}"))
    }
}

/// `base/<relative>` as entries stored at `<relative>`, a tree included.
fn collect_tree(base: &Path, relative: &str, entries: &mut Vec<JsonEntry>) -> Result<(), String> {
    let path = base.join(relative);
    let meta = fs::symlink_metadata(&path).map_err(|e| format!("json-engine stat: {e}"))?;
    if meta.is_dir() {
        entries.push((relative.to_string(), true, String::new()));
        let mut children: Vec<_> = fs::read_dir(&path)
            .map_err(|e| format!("json-engine read_dir: {e}"))?
            .filter_map(Result::ok)
            .map(|child| child.file_name().to_string_lossy().into_owned())
            .collect();
        children.sort();
        for child in children {
            collect_tree(base, &format!("{relative}/{child}"), entries)?;
        }
    } else {
        let data = fs::read_to_string(&path).map_err(|e| format!("json-engine read: {e}"))?;
        entries.push((relative.to_string(), false, data));
    }
    Ok(())
}

impl ArchiveEngine for JsonEngine {
    fn name(&self) -> &str {
        "json-engine"
    }

    fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String> {
        self.record("list".to_string());
        Ok(read_entries(archive)?
            .into_iter()
            .map(|(path, is_dir, data)| EngineEntry {
                path,
                is_dir,
                size: data.len() as u64,
                compressed_size: data.len() as u64,
            })
            .collect())
    }

    fn extract(
        &self,
        archive: &Path,
        destination: &Path,
        ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.record("extract".to_string());
        if ctx.is_cancelled() {
            return Err(CANCELLED_MESSAGE.to_string());
        }
        if let Some(junction) = self.junction_dir {
            let outside = archive
                .parent()
                .unwrap_or_else(|| Path::new("."))
                .join("json-engine-junction-target");
            make_junction(&destination.join(junction), &outside)?;
        }
        for (path, is_dir, data) in read_entries(archive)? {
            write_entry(destination, &path, is_dir, data.as_bytes())?;
        }
        if self.stray_file {
            write_entry(destination, "stray.txt", false, b"not listed")?;
        }
        Ok(())
    }

    fn read_entry(
        &self,
        archive: &Path,
        entry: &str,
        destination: &Path,
        limits: EngineLimits,
    ) -> Result<(), String> {
        self.record(format!(
            "read_entry {entry} {} {}",
            limits.max_bytes, limits.max_entries
        ));
        // A plain string prefix on purpose: `docs` also matches `docs2/...`,
        // so tests prove the core picks entries by path segment itself.
        for (path, is_dir, data) in read_entries(archive)? {
            if !path.starts_with(entry) {
                continue;
            }
            let bytes = match self.oversize_bytes {
                Some(size) if !is_dir => vec![b'x'; usize::try_from(size).unwrap_or(0)],
                _ => data.into_bytes(),
            };
            write_entry(destination, &path, is_dir, &bytes)?;
        }
        Ok(())
    }

    fn create(
        &self,
        archive: &Path,
        paths: &[PathBuf],
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.record(format!("create {}", paths.len()));
        if archive.exists() {
            return Err(format!("json-engine: {} exists", archive.display()));
        }
        if let Err(message) = self.take_failure() {
            // Leave a partial file behind, as a real engine might.
            let _ = fs::write(archive, b"partial");
            return Err(message);
        }
        let mut entries = Vec::new();
        for path in paths {
            let base = path.parent().unwrap_or_else(|| Path::new("."));
            let name = path
                .file_name()
                .ok_or_else(|| format!("json-engine: no file name in {}", path.display()))?
                .to_string_lossy()
                .into_owned();
            collect_tree(base, &name, &mut entries)?;
        }
        write_entries(archive, &entries)
    }

    fn rename(
        &self,
        archive: &Path,
        entry: &str,
        new_path: &str,
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.record(format!("rename {entry} {new_path}"));
        self.take_failure()?;
        let entries = read_entries(archive)?
            .into_iter()
            .map(|(path, is_dir, data)| {
                if is_at_or_under(&path, entry) {
                    (format!("{new_path}{}", &path[entry.len()..]), is_dir, data)
                } else {
                    (path, is_dir, data)
                }
            })
            .collect::<Vec<_>>();
        rewrite_entries(archive, &entries)
    }

    fn delete(
        &self,
        archive: &Path,
        entries: &[String],
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.record(format!("delete {}", entries.join(",")));
        self.take_failure()?;
        let kept = read_entries(archive)?
            .into_iter()
            .filter(|(path, _, _)| !entries.iter().any(|entry| is_at_or_under(path, entry)))
            .collect::<Vec<_>>();
        rewrite_entries(archive, &kept)
    }

    fn add(
        &self,
        archive: &Path,
        base_dir: &Path,
        paths: &[String],
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.record(format!("add {}", paths.join(",")));
        self.take_failure()?;
        let mut entries = read_entries(archive)?;
        let mut added = Vec::new();
        for path in paths {
            collect_tree(base_dir, path, &mut added)?;
        }
        entries.retain(|(path, _, _)| !added.iter().any(|(new, _, _)| new == path));
        entries.extend(added);
        rewrite_entries(archive, &entries)
    }
}

/// Register a default `JsonEngine` for `extension` under `owner`; the
/// registration is removed when the guard drops.
pub(super) fn register_json_engine(
    owner: &'static str,
    extension: &str,
    can_modify: bool,
) -> (Arc<JsonEngine>, EngineRegistration) {
    register_json_engine_with(owner, extension, can_modify, JsonEngine::default())
}

/// As `register_json_engine`, with the knobs already set on `engine`.
pub(super) fn register_json_engine_with(
    owner: &'static str,
    extension: &str,
    can_modify: bool,
    engine: JsonEngine,
) -> (Arc<JsonEngine>, EngineRegistration) {
    let engine = Arc::new(engine);
    register_engine(
        owner,
        vec![EngineFormat {
            extension: extension.to_string(),
            can_list: true,
            can_extract: true,
            can_create: true,
            can_modify,
        }],
        engine.clone(),
    );
    (engine, EngineRegistration(owner))
}
