//! Fixture backend for the Hello test plugin. It serves `ping` and a toy
//! `.hello` archive format: UTF-8 text, one `<path>\t<size>` entry per line.

use std::path::{Component, Path, PathBuf};
use std::sync::Arc;

use serde_json::{json, Value};
use sumafile_plugin::{
    async_trait, serve, Args, HostClient, PluginError, PluginHandler, PluginInfo,
};

struct Entry {
    path: String,
    size: u64,
}

struct HelloHandler;

fn str_param<'a>(params: &'a Value, key: &str) -> Result<&'a str, PluginError> {
    params
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| PluginError::InvalidParams(format!("missing string parameter '{key}'")))
}

fn read_listing(archive: &str) -> Result<Vec<Entry>, PluginError> {
    let text = std::fs::read_to_string(archive)
        .map_err(|e| PluginError::Application(format!("cannot read {archive}: {e}")))?;
    let mut entries = Vec::new();
    for line in text.lines().filter(|l| !l.trim().is_empty()) {
        let (path, size) = line
            .rsplit_once('\t')
            .and_then(|(p, s)| Some((p, s.trim().parse::<u64>().ok()?)))
            .ok_or_else(|| PluginError::Application(format!("malformed entry: {line}")))?;
        entries.push(Entry {
            path: path.to_string(),
            size,
        });
    }
    Ok(entries)
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

fn collect(path: &Path, relative: &str, out: &mut Vec<Entry>) -> Result<(), PluginError> {
    let io = |e: std::io::Error| PluginError::Application(format!("{}: {e}", path.display()));
    let meta = std::fs::metadata(path).map_err(io)?;
    if meta.is_dir() {
        let mut children: Vec<_> = std::fs::read_dir(path)
            .map_err(io)?
            .collect::<Result<_, _>>()
            .map_err(io)?;
        children.sort_by_key(|c| c.file_name());
        for child in children {
            let name = child.file_name().to_string_lossy().into_owned();
            collect(&child.path(), &format!("{relative}/{name}"), out)?;
        }
    } else {
        out.push(Entry {
            path: relative.to_string(),
            size: meta.len(),
        });
    }
    Ok(())
}

fn list(params: &Value) -> Result<Value, PluginError> {
    let entries: Vec<Value> = read_listing(str_param(params, "archivePath")?)?
        .into_iter()
        .map(
            |e| json!({ "path": e.path, "isDir": false, "size": e.size, "compressedSize": e.size }),
        )
        .collect();
    Ok(json!({ "entries": entries }))
}

fn extract(params: &Value) -> Result<Value, PluginError> {
    let entries = read_listing(str_param(params, "archivePath")?)?;
    let destination = Path::new(str_param(params, "destination")?);
    let inner = params
        .get("innerPath")
        .and_then(Value::as_str)
        .unwrap_or("");
    // Validate everything first so a bad entry writes nothing.
    let mut targets = Vec::new();
    for entry in entries.iter().filter(|e| e.path.starts_with(inner)) {
        targets.push((safe_join(destination, &entry.path)?, entry.size));
    }
    let io = |e: std::io::Error| PluginError::Application(e.to_string());
    for (target, size) in targets {
        if let Some(parent) = target.parent() {
            std::fs::create_dir_all(parent).map_err(io)?;
        }
        std::fs::write(&target, vec![b' '; size as usize]).map_err(io)?;
    }
    Ok(Value::Null)
}

fn create(params: &Value) -> Result<Value, PluginError> {
    let archive = str_param(params, "archivePath")?;
    let paths = params
        .get("paths")
        .and_then(Value::as_array)
        .ok_or_else(|| PluginError::InvalidParams("missing array parameter 'paths'".into()))?;
    let mut entries = Vec::new();
    for value in paths {
        let path =
            Path::new(value.as_str().ok_or_else(|| {
                PluginError::InvalidParams("'paths' must contain strings".into())
            })?);
        let name = path
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .ok_or_else(|| {
                PluginError::InvalidParams(format!("no file name: {}", path.display()))
            })?;
        collect(path, &name, &mut entries)?;
    }
    let mut text = String::new();
    for entry in &entries {
        if entry.path.contains(['\t', '\n', '\r']) {
            return Err(PluginError::InvalidParams(format!(
                "unsupported file name: {}",
                entry.path
            )));
        }
        text.push_str(&format!("{}\t{}\n", entry.path, entry.size));
    }
    std::fs::write(archive, text).map_err(|e| PluginError::Application(e.to_string()))?;
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
            "archive.extract" => extract(&params),
            "archive.create" => create(&params),
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

        let dest2 = dir.path().join("out2");
        let params = json!({ "archivePath": archive, "destination": dest2, "innerPath": "sub/" });
        HelloHandler
            .handle("archive.extract", params, &host())
            .await
            .unwrap();
        assert!(!dest2.join("one.txt").exists());
        assert!(dest2.join("sub/two.txt").exists());
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
