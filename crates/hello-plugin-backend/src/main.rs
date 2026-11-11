//! Fixture backend for the Hello test plugin. It serves `ping` and a toy
//! `.hello` archive format: UTF-8 text, one `<path>\t<size>` entry per line.
//! A path that ends in `/` is a folder.
//!
//! It implements every archive engine method of the plugin protocol:
//! `archive.list`, `extract`, `read_entry`, `create`, `rename`, `delete` and
//! `add`. Extracted files hold `size` spaces. Every mutation rewrites the
//! listing through `<archive>.tmp` and a rename, so a failed one leaves the
//! archive as it was.
//!
//! An entry whose last segment is `__slow__`, with size N, makes the backend
//! sleep N ms in 50 ms steps before it does the work: `extract` and `add`
//! when the archive holds such an entry, `read_entry` when its selection
//! includes one. With an `operationId`, each step sends `archive.progress`
//! and the call fails with the cancelled message once that id is cancelled.

use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde_json::{json, Value};
use sumafile_plugin::{
    async_trait, serve, Args, HostClient, PluginError, PluginHandler, PluginInfo, CANCELLED_MESSAGE,
};

/// Last segment of an entry that makes an operation slow.
const SLOW_SEGMENT: &str = "__slow__";
const SLOW_STEP: Duration = Duration::from_millis(50);

struct Entry {
    /// `/`-separated, without a trailing slash.
    path: String,
    size: u64,
    is_dir: bool,
}

impl Entry {
    fn file(path: String, size: u64) -> Entry {
        Entry {
            path,
            size,
            is_dir: false,
        }
    }

    fn dir(path: String) -> Entry {
        Entry {
            path,
            size: 0,
            is_dir: true,
        }
    }

    /// The entry is `inner` or sits under it, by whole segments: `docs`
    /// never matches `docs2`.
    fn is_within(&self, inner: &str) -> bool {
        is_within(&self.path, inner)
    }

    fn is_slow(&self) -> bool {
        !self.is_dir && self.path.rsplit('/').next() == Some(SLOW_SEGMENT)
    }
}

fn is_within(path: &str, inner: &str) -> bool {
    path == inner
        || path
            .strip_prefix(inner)
            .is_some_and(|rest| rest.starts_with('/'))
}

struct HelloHandler;

fn application(message: impl Into<String>) -> PluginError {
    PluginError::Application(message.into())
}

fn io_error(path: &Path) -> impl Fn(std::io::Error) -> PluginError + '_ {
    move |e| application(format!("{}: {e}", path.display()))
}

fn str_param<'a>(params: &'a Value, key: &str) -> Result<&'a str, PluginError> {
    params
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| PluginError::InvalidParams(format!("missing string parameter '{key}'")))
}

fn u64_param(params: &Value, key: &str) -> Result<u64, PluginError> {
    params
        .get(key)
        .and_then(Value::as_u64)
        .ok_or_else(|| PluginError::InvalidParams(format!("missing number parameter '{key}'")))
}

fn strings_param<'a>(params: &'a Value, key: &str) -> Result<Vec<&'a str>, PluginError> {
    params
        .get(key)
        .and_then(Value::as_array)
        .ok_or_else(|| PluginError::InvalidParams(format!("missing array parameter '{key}'")))?
        .iter()
        .map(|value| {
            value
                .as_str()
                .ok_or_else(|| PluginError::InvalidParams(format!("'{key}' must contain strings")))
        })
        .collect()
}

fn read_listing(archive: &str) -> Result<Vec<Entry>, PluginError> {
    let text = std::fs::read_to_string(archive)
        .map_err(|e| application(format!("cannot read {archive}: {e}")))?;
    let mut entries = Vec::new();
    for line in text.lines().filter(|l| !l.trim().is_empty()) {
        let (path, size) = line
            .rsplit_once('\t')
            .and_then(|(p, s)| Some((p, s.trim().parse::<u64>().ok()?)))
            .ok_or_else(|| application(format!("malformed entry: {line}")))?;
        entries.push(match path.strip_suffix('/') {
            Some(folder) => Entry::dir(folder.to_string()),
            None => Entry::file(path.to_string(), size),
        });
    }
    Ok(entries)
}

fn listing_text(entries: &[Entry]) -> Result<String, PluginError> {
    let mut text = String::new();
    for entry in entries {
        if entry.path.contains(['\t', '\n', '\r']) {
            return Err(PluginError::InvalidParams(format!(
                "unsupported file name: {}",
                entry.path
            )));
        }
        if entry.is_dir {
            text.push_str(&format!("{}/\t0\n", entry.path));
        } else {
            text.push_str(&format!("{}\t{}\n", entry.path, entry.size));
        }
    }
    Ok(text)
}

/// Replaces the archive's listing: writes `<archive>.tmp`, then renames it
/// over the archive.
fn write_listing(archive: &str, entries: &[Entry]) -> Result<(), PluginError> {
    let text = listing_text(entries)?;
    let temp = PathBuf::from(format!("{archive}.tmp"));
    let written = std::fs::write(&temp, text).and_then(|()| std::fs::rename(&temp, archive));
    if let Err(error) = written {
        let _ = std::fs::remove_file(&temp);
        return Err(application(format!("cannot write {archive}: {error}")));
    }
    Ok(())
}

/// Joins an entry path under `destination`, refusing anything that could
/// escape it: rooted paths, drive prefixes and `..` segments (either slash).
fn safe_join(destination: &Path, entry: &str) -> Result<PathBuf, PluginError> {
    let bad = || PluginError::InvalidParams(format!("unsafe entry path: {entry}"));
    if entry.is_empty() || entry.starts_with(['/', '\\']) || entry.contains(':') {
        return Err(bad());
    }
    let mut out = destination.to_path_buf();
    for segment in entry.split(['/', '\\']).filter(|s| !s.is_empty()) {
        if Path::new(segment)
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
        {
            return Err(bad());
        }
        out.push(segment);
    }
    Ok(out)
}

/// A caller-supplied entry path, checked and without a trailing slash.
fn entry_path(value: &str) -> Result<String, PluginError> {
    safe_join(Path::new("."), value)?;
    Ok(value.trim_end_matches('/').to_string())
}

/// Lists `path` (a file or a tree) as entries at `relative`. An empty
/// folder becomes a folder entry.
fn collect(path: &Path, relative: &str, out: &mut Vec<Entry>) -> Result<(), PluginError> {
    let io = io_error(path);
    let meta = std::fs::metadata(path).map_err(&io)?;
    if meta.is_dir() {
        let mut children: Vec<_> = std::fs::read_dir(path)
            .map_err(&io)?
            .collect::<Result<_, _>>()
            .map_err(&io)?;
        if children.is_empty() {
            out.push(Entry::dir(relative.to_string()));
        }
        children.sort_by_key(|c| c.file_name());
        for child in children {
            let name = child.file_name().to_string_lossy().into_owned();
            collect(&child.path(), &format!("{relative}/{name}"), out)?;
        }
    } else {
        out.push(Entry::file(relative.to_string(), meta.len()));
    }
    Ok(())
}

/// `destination` must be an empty folder; a missing one is created.
fn ensure_empty_destination(destination: &Path) -> Result<(), PluginError> {
    let io = io_error(destination);
    if destination.exists() {
        if std::fs::read_dir(destination)
            .map_err(&io)?
            .next()
            .is_some()
        {
            return Err(application(format!(
                "Destination is not empty: {}",
                destination.display()
            )));
        }
        return Ok(());
    }
    std::fs::create_dir_all(destination).map_err(&io)
}

/// Writes `entries` under `destination`, after validating every path, so a
/// bad entry writes nothing.
fn plan_writes<'a>(
    destination: &Path,
    entries: impl Iterator<Item = &'a Entry>,
) -> Result<Vec<(PathBuf, &'a Entry)>, PluginError> {
    entries
        .map(|entry| Ok((safe_join(destination, &entry.path)?, entry)))
        .collect()
}

fn write_entries(targets: &[(PathBuf, &Entry)]) -> Result<(), PluginError> {
    for (target, entry) in targets {
        let io = io_error(target);
        if entry.is_dir {
            std::fs::create_dir_all(target).map_err(&io)?;
            continue;
        }
        if let Some(parent) = target.parent() {
            std::fs::create_dir_all(parent).map_err(&io)?;
        }
        std::fs::write(target, vec![b' '; entry.size as usize]).map_err(&io)?;
    }
    Ok(())
}

/// Sleeps for the largest `__slow__` entry among `entries`, in 50 ms steps.
/// With an `operationId`, each step reports progress and checks for cancel.
async fn slow_down<'a>(
    entries: impl Iterator<Item = &'a Entry>,
    params: &Value,
    host: &HostClient,
) -> Result<(), PluginError> {
    let Some(total) = entries.filter(|e| e.is_slow()).map(|e| e.size).max() else {
        return Ok(());
    };
    let operation_id = params.get("operationId").and_then(Value::as_str);
    let started = Instant::now();
    loop {
        let elapsed = u64::try_from(started.elapsed().as_millis())
            .unwrap_or(u64::MAX)
            .min(total);
        if let Some(operation_id) = operation_id {
            // A closed connection only loses the report.
            let _ = host.archive_progress(operation_id, elapsed, total, Some(SLOW_SEGMENT));
            if host.is_cancelled(operation_id) {
                return Err(application(CANCELLED_MESSAGE));
            }
        }
        if elapsed >= total {
            return Ok(());
        }
        tokio::time::sleep(SLOW_STEP.min(Duration::from_millis(total - elapsed))).await;
    }
}

fn list(params: &Value) -> Result<Value, PluginError> {
    let entries: Vec<Value> = read_listing(str_param(params, "archivePath")?)?
        .into_iter()
        .map(|e| {
            json!({ "path": e.path, "isDir": e.is_dir, "size": e.size, "compressedSize": e.size })
        })
        .collect();
    Ok(json!({ "entries": entries }))
}

async fn extract(params: &Value, host: &HostClient) -> Result<Value, PluginError> {
    let entries = read_listing(str_param(params, "archivePath")?)?;
    let destination = Path::new(str_param(params, "destination")?);
    let targets = plan_writes(destination, entries.iter())?;
    ensure_empty_destination(destination)?;
    slow_down(entries.iter(), params, host).await?;
    write_entries(&targets)?;
    Ok(Value::Null)
}

async fn read_entry(params: &Value, host: &HostClient) -> Result<Value, PluginError> {
    let entries = read_listing(str_param(params, "archivePath")?)?;
    let inner = str_param(params, "innerPath")?;
    let destination = Path::new(str_param(params, "destination")?);
    let max_bytes = u64_param(params, "maxBytes")?;
    let max_entries = u64_param(params, "maxEntries")?;
    let selection: Vec<&Entry> = entries.iter().filter(|e| e.is_within(inner)).collect();
    if selection.is_empty() {
        return Err(application(format!("Archive entry not found: {inner}")));
    }
    if selection.len() as u64 > max_entries {
        return Err(application(format!(
            "Archive extract exceeds entry limit of {max_entries} entries"
        )));
    }
    let declared: u64 = selection.iter().map(|e| e.size).sum();
    if declared > max_bytes {
        return Err(application(format!(
            "Archive extract exceeds size limit of {max_bytes} bytes"
        )));
    }
    let targets = plan_writes(destination, selection.iter().copied())?;
    ensure_empty_destination(destination)?;
    slow_down(selection.iter().copied(), params, host).await?;
    write_entries(&targets)?;
    Ok(Value::Null)
}

fn create(params: &Value) -> Result<Value, PluginError> {
    let archive = str_param(params, "archivePath")?;
    let mut entries = Vec::new();
    for value in strings_param(params, "paths")? {
        let path = Path::new(value);
        let name = path
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .ok_or_else(|| {
                PluginError::InvalidParams(format!("no file name: {}", path.display()))
            })?;
        collect(path, &name, &mut entries)?;
    }
    let text = listing_text(&entries)?;
    // A new file only: never append to or replace an existing archive.
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(archive)
        .map_err(|e| match e.kind() {
            std::io::ErrorKind::AlreadyExists => {
                application(format!("Archive already exists: {archive}"))
            }
            _ => application(format!("cannot create {archive}: {e}")),
        })?;
    std::io::Write::write_all(&mut file, text.as_bytes())
        .map_err(|e| application(format!("cannot write {archive}: {e}")))?;
    Ok(Value::Null)
}

fn rename(params: &Value) -> Result<Value, PluginError> {
    let archive = str_param(params, "archivePath")?;
    let inner = entry_path(str_param(params, "innerPath")?)?;
    let new_path = entry_path(str_param(params, "newPath")?)?;
    let mut entries = read_listing(archive)?;
    if !entries.iter().any(|e| e.is_within(&inner)) {
        return Err(application(format!("Archive entry not found: {inner}")));
    }
    if new_path == inner {
        return Ok(Value::Null);
    }
    if is_within(&new_path, &inner) {
        return Err(PluginError::InvalidParams(format!(
            "cannot move {inner} into itself"
        )));
    }
    if entries
        .iter()
        .any(|e| !e.is_within(&inner) && e.is_within(&new_path))
    {
        return Err(application(format!(
            "Archive entry already exists: {new_path}"
        )));
    }
    for entry in entries.iter_mut().filter(|e| e.is_within(&inner)) {
        entry.path = format!("{new_path}{}", &entry.path[inner.len()..]);
    }
    write_listing(archive, &entries)?;
    Ok(Value::Null)
}

fn delete(params: &Value) -> Result<Value, PluginError> {
    let archive = str_param(params, "archivePath")?;
    let inners = strings_param(params, "innerPaths")?
        .into_iter()
        .map(entry_path)
        .collect::<Result<Vec<_>, _>>()?;
    let mut entries = read_listing(archive)?;
    if let Some(missing) = inners
        .iter()
        .find(|inner| !entries.iter().any(|e| e.is_within(inner)))
    {
        return Err(application(format!("Archive entry not found: {missing}")));
    }
    entries.retain(|e| !inners.iter().any(|inner| e.is_within(inner)));
    write_listing(archive, &entries)?;
    Ok(Value::Null)
}

async fn add(params: &Value, host: &HostClient) -> Result<Value, PluginError> {
    let archive = str_param(params, "archivePath")?;
    let base = Path::new(str_param(params, "baseDir")?);
    let mut added = Vec::new();
    for value in strings_param(params, "paths")? {
        let relative = entry_path(value)?;
        collect(&safe_join(base, &relative)?, &relative, &mut added)?;
    }
    let mut entries = read_listing(archive)?;
    listing_text(&added)?;
    slow_down(entries.iter(), params, host).await?;
    entries.retain(|e| !added.iter().any(|new| new.path == e.path));
    entries.extend(added);
    write_listing(archive, &entries)?;
    Ok(Value::Null)
}

#[async_trait]
impl PluginHandler for HelloHandler {
    async fn handle(
        &self,
        method: &str,
        params: Value,
        host: &HostClient,
    ) -> Result<Value, PluginError> {
        match method {
            "ping" => {
                let settings = host
                    .get_settings(&["greeting"])
                    .await
                    .map_err(PluginError::Application)?;
                let greeting = settings
                    .get("greeting")
                    .cloned()
                    .flatten()
                    .unwrap_or_else(|| "Hello".to_string());
                Ok(json!({ "pong": true, "greeting": greeting }))
            }
            "archive.list" => list(&params),
            "archive.extract" => extract(&params, host).await,
            "archive.read_entry" => read_entry(&params, host).await,
            "archive.create" => create(&params),
            "archive.rename" => rename(&params),
            "archive.delete" => delete(&params),
            "archive.add" => add(&params, host).await,
            _ => Err(PluginError::MethodNotFound),
        }
    }
}

#[tokio::main]
async fn main() {
    let args = match Args::parse(std::env::args().skip(1)) {
        Ok(args) => args,
        Err(message) => {
            eprintln!("hello-plugin-backend: {message}");
            std::process::exit(2);
        }
    };
    let info = PluginInfo {
        id: "com.sumafile.hello".into(),
        version: "0.1.0".into(),
    };
    if let Err(message) = serve(args, info, true, Arc::new(HelloHandler)).await {
        eprintln!("hello-plugin-backend: {message}");
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn host() -> HostClient {
        HostClient::fake(HashMap::new())
    }

    #[tokio::test]
    async fn list_parses_hello_file() {
        let dir = tempfile::tempdir().unwrap();
        let archive = dir.path().join("a.hello");
        std::fs::write(&archive, "one.txt\t3\nsub/two.txt\t10\n").unwrap();
        let result = HelloHandler
            .handle("archive.list", json!({ "archivePath": archive }), &host())
            .await
            .unwrap();
        assert_eq!(
            result,
            json!({ "entries": [
                { "path": "one.txt", "isDir": false, "size": 3, "compressedSize": 3 },
                { "path": "sub/two.txt", "isDir": false, "size": 10, "compressedSize": 10 },
            ]})
        );
    }

    #[tokio::test]
    async fn extract_writes_sized_files() {
        let dir = tempfile::tempdir().unwrap();
        let archive = dir.path().join("a.hello");
        std::fs::write(&archive, "one.txt\t3\nsub/two.txt\t10\n").unwrap();
        let dest = dir.path().join("out");
        let params = json!({ "archivePath": archive, "destination": dest });
        HelloHandler
            .handle("archive.extract", params, &host())
            .await
            .unwrap();
        assert_eq!(std::fs::read(dest.join("one.txt")).unwrap(), b"   ");
        assert_eq!(std::fs::read(dest.join("sub/two.txt")).unwrap().len(), 10);
    }

    async fn call(method: &str, params: Value) -> Result<Value, PluginError> {
        HelloHandler.handle(method, params, &host()).await
    }

    fn archive_with(dir: &Path, text: &str) -> PathBuf {
        let archive = dir.join("a.hello");
        std::fs::write(&archive, text).unwrap();
        archive
    }

    fn empty_dir(dir: &Path, name: &str) -> PathBuf {
        let path = dir.join(name);
        std::fs::create_dir_all(&path).unwrap();
        path
    }

    fn listing(archive: &Path) -> String {
        std::fs::read_to_string(archive).unwrap()
    }

    fn tmp_of(archive: &Path) -> PathBuf {
        PathBuf::from(format!("{}.tmp", archive.display()))
    }

    fn application(error: PluginError) -> String {
        match error {
            PluginError::Application(message) => message,
            other => panic!("expected an application error, got {other:?}"),
        }
    }

    #[tokio::test]
    async fn list_reports_folder_lines_as_directories() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "docs/new/\t0\ndocs/a.txt\t1\n");
        let result = call("archive.list", json!({ "archivePath": archive }))
            .await
            .unwrap();
        assert_eq!(
            result,
            json!({ "entries": [
                { "path": "docs/new", "isDir": true, "size": 0, "compressedSize": 0 },
                { "path": "docs/a.txt", "isDir": false, "size": 1, "compressedSize": 1 },
            ]})
        );
    }

    #[tokio::test]
    async fn read_entry_extracts_a_segment_prefix_only() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "docs/a.txt\t4\ndocs2/b.txt\t2\n");
        let dest = empty_dir(dir.path(), "out");
        let params = json!({
            "archivePath": archive, "innerPath": "docs", "destination": dest,
            "maxBytes": 1000, "maxEntries": 10
        });
        assert_eq!(
            call("archive.read_entry", params).await.unwrap(),
            Value::Null
        );
        assert_eq!(std::fs::read(dest.join("docs/a.txt")).unwrap().len(), 4);
        assert!(!dest.join("docs2").exists());

        let dest = empty_dir(dir.path(), "out-file");
        let params = json!({
            "archivePath": archive, "innerPath": "docs2/b.txt", "destination": dest,
            "maxBytes": 1000, "maxEntries": 10
        });
        call("archive.read_entry", params).await.unwrap();
        assert_eq!(std::fs::read(dest.join("docs2/b.txt")).unwrap().len(), 2);
        assert!(!dest.join("docs").exists());

        let dest = empty_dir(dir.path(), "out-missing");
        let params = json!({
            "archivePath": archive, "innerPath": "doc", "destination": dest,
            "maxBytes": 1000, "maxEntries": 10
        });
        let error = application(call("archive.read_entry", params).await.unwrap_err());
        assert_eq!(error, "Archive entry not found: doc");
    }

    #[tokio::test]
    async fn read_entry_rejects_a_selection_over_limits() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "docs/a.txt\t4\ndocs/b.txt\t4\n");
        let dest = empty_dir(dir.path(), "out");
        let params = json!({
            "archivePath": archive, "innerPath": "docs", "destination": dest,
            "maxBytes": 1000, "maxEntries": 1
        });
        let error = application(call("archive.read_entry", params).await.unwrap_err());
        assert_eq!(error, "Archive extract exceeds entry limit of 1 entries");
        let params = json!({
            "archivePath": archive, "innerPath": "docs", "destination": dest,
            "maxBytes": 5, "maxEntries": 10
        });
        let error = application(call("archive.read_entry", params).await.unwrap_err());
        assert_eq!(error, "Archive extract exceeds size limit of 5 bytes");
        assert_eq!(std::fs::read_dir(&dest).unwrap().count(), 0);
    }

    const TREE: &str = "docs/a.txt\t1\ndocs/sub/b.txt\t2\ndocs2/c.txt\t3\ntop.txt\t4\n";

    #[tokio::test]
    async fn rename_rewrites_the_entry_and_its_descendants() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), TREE);
        let params = json!({ "archivePath": archive, "innerPath": "docs", "newPath": "papers" });
        assert_eq!(call("archive.rename", params).await.unwrap(), Value::Null);
        assert_eq!(
            listing(&archive),
            "papers/a.txt\t1\npapers/sub/b.txt\t2\ndocs2/c.txt\t3\ntop.txt\t4\n"
        );
        assert!(!tmp_of(&archive).exists());

        // A full new path also moves an entry between folders.
        let params =
            json!({ "archivePath": archive, "innerPath": "top.txt", "newPath": "docs2/top.txt" });
        call("archive.rename", params).await.unwrap();
        assert_eq!(
            listing(&archive),
            "papers/a.txt\t1\npapers/sub/b.txt\t2\ndocs2/c.txt\t3\ndocs2/top.txt\t4\n"
        );

        let before = listing(&archive);
        let params = json!({ "archivePath": archive, "innerPath": "nope", "newPath": "x" });
        let error = application(call("archive.rename", params).await.unwrap_err());
        assert_eq!(error, "Archive entry not found: nope");
        let params = json!({ "archivePath": archive, "innerPath": "papers", "newPath": "docs2" });
        let error = application(call("archive.rename", params).await.unwrap_err());
        assert_eq!(error, "Archive entry already exists: docs2");
        assert_eq!(listing(&archive), before);
    }

    #[tokio::test]
    async fn delete_drops_the_entry_and_its_descendants() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), TREE);
        let params = json!({ "archivePath": archive, "innerPaths": ["docs", "top.txt"] });
        assert_eq!(call("archive.delete", params).await.unwrap(), Value::Null);
        assert_eq!(listing(&archive), "docs2/c.txt\t3\n");
        assert!(!tmp_of(&archive).exists());

        let params = json!({ "archivePath": archive, "innerPaths": ["docs2/c.txt", "docs"] });
        let error = application(call("archive.delete", params).await.unwrap_err());
        assert_eq!(error, "Archive entry not found: docs");
        assert_eq!(listing(&archive), "docs2/c.txt\t3\n");
    }

    #[tokio::test]
    async fn add_stores_files_and_empty_folders_at_relative_paths() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "docs/a.txt\t1\n");
        let base = empty_dir(dir.path(), "base");
        empty_dir(&base, "docs/new");
        std::fs::write(base.join("docs/n.txt"), "abc").unwrap();
        empty_dir(&base, "tree/empty");
        empty_dir(&base, "tree/x");
        std::fs::write(base.join("tree/x/y.txt"), "xy").unwrap();
        let params = json!({
            "archivePath": archive, "baseDir": base, "paths": ["docs/new", "docs/n.txt", "tree"]
        });
        assert_eq!(call("archive.add", params).await.unwrap(), Value::Null);
        assert_eq!(
            listing(&archive),
            "docs/a.txt\t1\ndocs/new/\t0\ndocs/n.txt\t3\ntree/empty/\t0\ntree/x/y.txt\t2\n"
        );
        assert!(!tmp_of(&archive).exists());

        let params = json!({ "archivePath": archive, "baseDir": base, "paths": ["../escape"] });
        let error = call("archive.add", params).await.unwrap_err();
        assert!(matches!(error, PluginError::InvalidParams(_)), "{error:?}");
    }

    #[tokio::test]
    async fn create_refuses_an_existing_archive() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "x.txt\t1\n");
        let file = dir.path().join("f.txt");
        std::fs::write(&file, "abcd").unwrap();
        let params = json!({ "archivePath": archive, "paths": [file] });
        let error = application(call("archive.create", params).await.unwrap_err());
        assert!(error.contains("already exists"), "{error}");
        assert_eq!(listing(&archive), "x.txt\t1\n");
    }

    #[tokio::test]
    async fn extract_refuses_a_non_empty_destination() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "one.txt\t3\n");
        let dest = empty_dir(dir.path(), "out");
        std::fs::write(dest.join("keep.txt"), "k").unwrap();
        let params = json!({ "archivePath": archive, "destination": dest });
        let error = application(call("archive.extract", params).await.unwrap_err());
        assert!(error.contains("not empty"), "{error}");
        assert!(!dest.join("one.txt").exists());

        let dest = empty_dir(dir.path(), "out2");
        let params = json!({
            "archivePath": archive, "innerPath": "one.txt", "destination": dest,
            "maxBytes": 100, "maxEntries": 10
        });
        std::fs::write(dest.join("keep.txt"), "k").unwrap();
        let error = application(call("archive.read_entry", params).await.unwrap_err());
        assert!(error.contains("not empty"), "{error}");
    }

    async fn wait_for_progress(host: &HostClient) {
        for _ in 0..200 {
            if !host.fake_progress().is_empty() {
                return;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        panic!("no archive.progress within 2 s");
    }

    #[tokio::test]
    async fn slow_extract_reports_progress_and_honours_cancel() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "a.txt\t1\nslow/__slow__\t5000\n");
        let dest = empty_dir(dir.path(), "out");
        let host = host();
        let task = {
            let host = host.clone();
            let params =
                json!({ "archivePath": archive, "destination": dest, "operationId": "op-x" });
            tokio::spawn(async move { HelloHandler.handle("archive.extract", params, &host).await })
        };
        wait_for_progress(&host).await;
        host.fake_cancel("op-x");
        let outcome = tokio::time::timeout(std::time::Duration::from_secs(1), task)
            .await
            .expect("the cancelled extract did not stop within 1 s")
            .unwrap();
        assert_eq!(
            outcome,
            Err(PluginError::Application(CANCELLED_MESSAGE.to_string()))
        );
        let progress = host.fake_progress();
        assert!(progress
            .iter()
            .all(|(id, current, total, item)| id == "op-x"
                && *current <= *total
                && *total == 5000
                && item.as_deref() == Some("__slow__")));
        assert!(
            !dest.join("a.txt").exists(),
            "nothing is written once cancelled"
        );
    }

    #[tokio::test]
    async fn slow_read_entry_reports_progress() {
        let dir = tempfile::tempdir().unwrap();
        let archive = archive_with(dir.path(), "docs/__slow__\t200\ndocs/a.txt\t1\nb.txt\t1\n");
        let dest = empty_dir(dir.path(), "out");
        let host = host();
        let params = json!({
            "archivePath": archive, "innerPath": "docs", "destination": dest,
            "maxBytes": 1000, "maxEntries": 10, "operationId": "op-r"
        });
        HelloHandler
            .handle("archive.read_entry", params, &host)
            .await
            .unwrap();
        let progress = host.fake_progress();
        assert!(!progress.is_empty());
        assert!(progress
            .iter()
            .all(|(id, _, total, _)| id == "op-r" && *total == 200));
        assert!(dest.join("docs/a.txt").exists());

        // A selection without a slow entry does not wait.
        let host = super::tests::host();
        let dest = empty_dir(dir.path(), "out2");
        let params = json!({
            "archivePath": archive, "innerPath": "b.txt", "destination": dest,
            "maxBytes": 1000, "maxEntries": 10, "operationId": "op-q"
        });
        HelloHandler
            .handle("archive.read_entry", params, &host)
            .await
            .unwrap();
        assert!(host.fake_progress().is_empty());
    }

    #[tokio::test]
    async fn extract_refuses_unsafe_paths() {
        let dir = tempfile::tempdir().unwrap();
        for bad in ["..\\x\t1", "a/../../x\t1", "/abs\t1", "C:\\x\t1"] {
            let archive = dir.path().join("bad.hello");
            std::fs::write(&archive, format!("ok.txt\t1\n{bad}\n")).unwrap();
            let dest = dir.path().join("out");
            let params = json!({ "archivePath": archive, "destination": dest });
            let err = HelloHandler
                .handle("archive.extract", params, &host())
                .await
                .unwrap_err();
            assert!(matches!(err, PluginError::InvalidParams(_)), "{bad}");
            assert!(!dest.exists(), "nothing is written for {bad}");
        }
    }

    #[tokio::test]
    async fn create_writes_listing() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("f.txt");
        std::fs::write(&file, "abcd").unwrap();
        let folder = dir.path().join("d");
        std::fs::create_dir_all(folder.join("n")).unwrap();
        std::fs::write(folder.join("n").join("g.txt"), "xy").unwrap();
        let archive = dir.path().join("made.hello");
        let params = json!({ "archivePath": archive, "paths": [file, folder] });
        HelloHandler
            .handle("archive.create", params, &host())
            .await
            .unwrap();
        assert_eq!(
            std::fs::read_to_string(&archive).unwrap(),
            "f.txt\t4\nd/n/g.txt\t2\n"
        );
    }

    #[tokio::test]
    async fn ping_uses_greeting_setting() {
        let with = HostClient::fake(HashMap::from([(
            "greeting".to_string(),
            "Howdy".to_string(),
        )]));
        let result = HelloHandler.handle("ping", json!({}), &with).await.unwrap();
        assert_eq!(result, json!({ "pong": true, "greeting": "Howdy" }));
        let result = HelloHandler
            .handle("ping", json!({}), &host())
            .await
            .unwrap();
        assert_eq!(result, json!({ "pong": true, "greeting": "Hello" }));
        let err = HelloHandler
            .handle("nope", json!({}), &host())
            .await
            .unwrap_err();
        assert_eq!(err, PluginError::MethodNotFound);
    }
}
