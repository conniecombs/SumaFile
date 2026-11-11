//! The plugin handler: the seven `archive.*` methods over [`SevenZip`].
//!
//! `binary_for` runs first, on the async side: it reads `binaryOverride`
//! through the host connection under a 2 s timeout. Every operation then
//! runs 7-Zip inside `spawn_blocking`, so the runtime's workers only route
//! frames and a `cancel` or a new `archive.list` is read while 7-Zip
//! processes run on every connection. The blocking closure owns a
//! [`ProgressBridge`] as its observer.

use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard};
use std::time::Duration;

use serde_json::{json, Value};
use sumafile_plugin::{async_trait, HostClient, PluginError, PluginHandler, CANCELLED_MESSAGE};

use crate::binary::{override_target, resolve_binary, BINARY_OVERRIDE_KEY};
use crate::ops::{Limits, SevenZip};
use crate::run::RunObserver;

/// How long `binary_for` waits for the host's settings, and for a log call.
pub const SETTINGS_TIMEOUT: Duration = Duration::from_secs(2);

/// Logged once per backend process while `SIMPLEFILE_7Z` is set and the
/// override is empty.
pub const LEGACY_ENV_WARNING: &str = "SIMPLEFILE_7Z is no longer read. Set Settings > Plugins > 7-Zip archives > 7-Zip executable override instead.";

/// The environment variable phase 1 read instead of the setting.
const LEGACY_ENV: &str = "SIMPLEFILE_7Z";

/// Serves the `archive.*` methods with the 7-Zip executable the settings
/// choose.
pub struct SevenZipHandler {
    backend_dir: PathBuf,
    legacy_env: Option<String>,
    state: Mutex<SettingsState>,
}

#[derive(Default)]
struct SettingsState {
    /// The last `binaryOverride` the host returned; no override until the
    /// first successful lookup.
    override_value: Option<String>,
    legacy_warned: bool,
    /// The missing override last warned about, so each is reported once.
    missing_warned: Option<String>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

impl SevenZipHandler {
    /// A handler for the backend in `backend_dir`, the folder holding the
    /// bundled `7za.exe`. Reads `SIMPLEFILE_7Z` once, only to warn that it
    /// is no longer used.
    pub fn new(backend_dir: PathBuf) -> Self {
        Self::with_legacy_env(backend_dir, std::env::var(LEGACY_ENV).ok())
    }

    /// [`SevenZipHandler::new`] with the value of `SIMPLEFILE_7Z` given;
    /// for tests.
    pub fn with_legacy_env(backend_dir: PathBuf, simplefile_7z: Option<String>) -> Self {
        SevenZipHandler {
            backend_dir,
            legacy_env: simplefile_7z.filter(|value| !value.trim().is_empty()),
            state: Mutex::new(SettingsState::default()),
        }
    }

    /// The executable for the next operation. A successful settings lookup
    /// updates the cached override; a failed one, or no host connection,
    /// uses the cache. Warnings go to the host's log.
    pub async fn binary_for(&self, host: &HostClient) -> Result<PathBuf, String> {
        let host_connection = host.host_connection();
        if let Some(connection) = &host_connection {
            let lookup = tokio::time::timeout(
                SETTINGS_TIMEOUT,
                connection.get_settings(&[BINARY_OVERRIDE_KEY]),
            )
            .await;
            if let Ok(Ok(values)) = lookup {
                lock(&self.state).override_value =
                    values.get(BINARY_OVERRIDE_KEY).cloned().flatten();
            }
        }

        let (override_value, warnings) = {
            let mut state = lock(&self.state);
            let override_value = state
                .override_value
                .as_deref()
                .map(str::trim)
                .filter(|value| !value.is_empty())
                .map(str::to_string);
            let mut warnings = Vec::new();
            match &override_value {
                None => {
                    if self.legacy_env.is_some() && !state.legacy_warned {
                        state.legacy_warned = true;
                        warnings.push(LEGACY_ENV_WARNING.to_string());
                    }
                }
                Some(value) if override_target(value).is_none() => {
                    if state.missing_warned.as_deref() != Some(value.as_str()) {
                        state.missing_warned = Some(value.clone());
                        warnings.push(format!(
                            "The 7-Zip executable override was not found: {value}. Using the bundled 7-Zip instead."
                        ));
                    }
                }
                Some(_) => state.missing_warned = None,
            }
            (override_value, warnings)
        };

        let logger = host_connection.as_ref().unwrap_or(host);
        for warning in warnings {
            let _ = tokio::time::timeout(SETTINGS_TIMEOUT, logger.log("warn", &warning)).await;
        }
        resolve_binary(override_value.as_deref(), &self.backend_dir)
    }
}

/// Reports one operation's progress as `archive.progress` on the
/// connection its request came on, and reads its cancel flag. Without an
/// `operationId` it reports nothing and is never cancelled.
struct ProgressBridge {
    host: HostClient,
    operation_id: Option<String>,
    total: u64,
}

impl RunObserver for ProgressBridge {
    fn total(&mut self, bytes: u64) {
        self.total = bytes;
    }

    fn progress(&mut self, percent: u8, item: Option<&str>) {
        let Some(operation_id) = &self.operation_id else {
            return;
        };
        let current = u128::from(self.total) * u128::from(percent.min(100)) / 100;
        let current = u64::try_from(current).unwrap_or(self.total);
        let item = item.map(|item| item.replace('\\', "/"));
        // Never waits; a closed connection only loses the report.
        let _ = self
            .host
            .archive_progress(operation_id, current, self.total, item.as_deref());
    }

    fn is_cancelled(&self) -> bool {
        self.operation_id
            .as_deref()
            .is_some_and(|operation_id| self.host.is_cancelled(operation_id))
    }
}

/// One parsed `archive.*` request.
enum Request {
    List {
        archive: PathBuf,
    },
    Extract {
        archive: PathBuf,
        destination: PathBuf,
    },
    ReadEntry {
        archive: PathBuf,
        inner_path: String,
        destination: PathBuf,
        limits: Limits,
    },
    Create {
        archive: PathBuf,
        paths: Vec<PathBuf>,
    },
    Rename {
        archive: PathBuf,
        inner_path: String,
        new_path: String,
    },
    Delete {
        archive: PathBuf,
        inner_paths: Vec<String>,
    },
    Add {
        archive: PathBuf,
        base_dir: PathBuf,
        paths: Vec<String>,
    },
}

fn invalid(message: String) -> PluginError {
    PluginError::InvalidParams(message)
}

fn str_param<'a>(params: &'a Value, key: &str) -> Result<&'a str, PluginError> {
    params
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| invalid(format!("missing string parameter '{key}'")))
}

fn path_param(params: &Value, key: &str) -> Result<PathBuf, PluginError> {
    str_param(params, key).map(PathBuf::from)
}

fn u64_param(params: &Value, key: &str) -> Result<u64, PluginError> {
    params
        .get(key)
        .and_then(Value::as_u64)
        .ok_or_else(|| invalid(format!("missing number parameter '{key}'")))
}

fn strings_param(params: &Value, key: &str) -> Result<Vec<String>, PluginError> {
    params
        .get(key)
        .and_then(Value::as_array)
        .ok_or_else(|| invalid(format!("missing array parameter '{key}'")))?
        .iter()
        .map(|value| {
            value
                .as_str()
                .map(str::to_string)
                .ok_or_else(|| invalid(format!("'{key}' must contain strings")))
        })
        .collect()
}

impl Request {
    /// Fails with `MethodNotFound` for a method this handler does not
    /// serve and `InvalidParams` for a missing parameter.
    fn parse(method: &str, params: &Value) -> Result<Request, PluginError> {
        let archive = || path_param(params, "archivePath");
        Ok(match method {
            "archive.list" => Request::List {
                archive: archive()?,
            },
            "archive.extract" => Request::Extract {
                archive: archive()?,
                destination: path_param(params, "destination")?,
            },
            "archive.read_entry" => Request::ReadEntry {
                archive: archive()?,
                inner_path: str_param(params, "innerPath")?.to_string(),
                destination: path_param(params, "destination")?,
                limits: Limits {
                    max_bytes: u64_param(params, "maxBytes")?,
                    max_entries: u32::try_from(u64_param(params, "maxEntries")?)
                        .unwrap_or(u32::MAX),
                },
            },
            "archive.create" => Request::Create {
                archive: archive()?,
                paths: strings_param(params, "paths")?
                    .into_iter()
                    .map(PathBuf::from)
                    .collect(),
            },
            "archive.rename" => Request::Rename {
                archive: archive()?,
                inner_path: str_param(params, "innerPath")?.to_string(),
                new_path: str_param(params, "newPath")?.to_string(),
            },
            "archive.delete" => Request::Delete {
                archive: archive()?,
                inner_paths: strings_param(params, "innerPaths")?,
            },
            "archive.add" => Request::Add {
                archive: archive()?,
                base_dir: path_param(params, "baseDir")?,
                paths: strings_param(params, "paths")?,
            },
            _ => return Err(PluginError::MethodNotFound),
        })
    }

    fn run(self, engine: &SevenZip, observer: &mut ProgressBridge) -> Result<Value, String> {
        match self {
            Request::List { archive } => list(engine, &archive),
            Request::Extract {
                archive,
                destination,
            } => engine
                .extract_all(&archive, &destination, observer)
                .map(|()| Value::Null),
            Request::ReadEntry {
                archive,
                inner_path,
                destination,
                limits,
            } => engine
                .read_entry(&archive, &inner_path, &destination, limits, observer)
                .map(|()| Value::Null),
            Request::Create { archive, paths } => engine
                .create(&archive, &paths, observer)
                .map(|()| Value::Null),
            Request::Rename {
                archive,
                inner_path,
                new_path,
            } => engine
                .rename(&archive, &inner_path, &new_path, observer)
                .map(|()| Value::Null),
            Request::Delete {
                archive,
                inner_paths,
            } => engine
                .delete(&archive, &inner_paths, observer)
                .map(|()| Value::Null),
            Request::Add {
                archive,
                base_dir,
                paths,
            } => engine
                .add(&archive, &base_dir, &paths, observer)
                .map(|()| Value::Null),
        }
    }
}

/// `{"entries": [{path, isDir, size, compressedSize}]}` with `/`-separated
/// paths and no trailing slash. Names are reported as listed; the caller
/// decides which are safe to show.
fn list(engine: &SevenZip, archive: &Path) -> Result<Value, String> {
    let entries: Vec<Value> = engine
        .list(archive)?
        .into_iter()
        .map(|entry| {
            let path = entry.path.replace('\\', "/");
            json!({
                "path": path.trim_end_matches('/'),
                "isDir": entry.is_dir,
                "size": entry.size,
                "compressedSize": entry.compressed_size,
            })
        })
        .collect();
    Ok(json!({ "entries": entries }))
}

#[async_trait]
impl PluginHandler for SevenZipHandler {
    async fn handle(
        &self,
        method: &str,
        params: Value,
        host: &HostClient,
    ) -> Result<Value, PluginError> {
        let request = Request::parse(method, &params)?;
        let operation_id = params
            .get("operationId")
            .and_then(Value::as_str)
            .map(str::to_string);
        if operation_id
            .as_deref()
            .is_some_and(|operation_id| host.is_cancelled(operation_id))
        {
            return Err(PluginError::Application(CANCELLED_MESSAGE.to_string()));
        }
        let binary = self
            .binary_for(host)
            .await
            .map_err(PluginError::Application)?;
        let mut observer = ProgressBridge {
            host: host.clone(),
            operation_id,
            total: 0,
        };
        tokio::task::spawn_blocking(move || request.run(&SevenZip::new(binary), &mut observer))
            .await
            .map_err(|e| PluginError::Application(format!("7-Zip worker failed: {e}")))?
            .map_err(PluginError::Application)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    use serde_json::json;
    use sumafile_plugin::{HostClient, PluginError, PluginHandler, CANCELLED_MESSAGE};

    use crate::binary::BINARY_OVERRIDE_KEY;

    /// A backend folder holding a placeholder `7za.exe`.
    fn backend_dir() -> tempfile::TempDir {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("7za.exe"), b"placeholder").unwrap();
        dir
    }

    #[tokio::test]
    async fn binary_for_reads_the_override_through_the_host_connection() {
        let backend = backend_dir();
        let custom = backend.path().join("custom-7z.exe");
        std::fs::write(&custom, b"x").unwrap();
        let host = HostClient::fake(HashMap::from([(
            BINARY_OVERRIDE_KEY.to_string(),
            custom.display().to_string(),
        )]));
        let handler = SevenZipHandler::with_legacy_env(backend.path().to_path_buf(), None);

        assert_eq!(handler.binary_for(&host).await, Ok(custom));

        // An empty override goes back to the bundled binary.
        host.set_setting(BINARY_OVERRIDE_KEY, "").await.unwrap();
        assert_eq!(
            handler.binary_for(&host).await,
            Ok(backend.path().join("7za.exe"))
        );
    }

    /// A current-thread runtime, like `#[tokio::test]`, for a test that
    /// holds a std lock around its async part.
    fn block_on<F: std::future::Future>(future: F) -> F::Output {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
            .block_on(future)
    }

    #[test]
    fn cancelled_before_start_fails_without_running_7za() {
        let _serial = crate::test_lock();
        let backend = backend_dir();
        let dir = tempfile::tempdir().unwrap();
        let host = HostClient::fake(HashMap::new());
        host.fake_cancel("op-1");
        let handler = SevenZipHandler::with_legacy_env(backend.path().to_path_buf(), None);
        let before = crate::run::spawn_count();

        let result = block_on(handler.handle(
            "archive.extract",
            json!({
                "archivePath": dir.path().join("pack.7z"),
                "destination": dir.path().join("out"),
                "operationId": "op-1",
            }),
            &host,
        ));

        assert_eq!(
            result,
            Err(PluginError::Application(CANCELLED_MESSAGE.to_string()))
        );
        assert_eq!(crate::run::spawn_count(), before);
    }

    #[tokio::test]
    async fn legacy_simplefile_7z_env_logs_one_warning() {
        let backend = backend_dir();
        let host = HostClient::fake(HashMap::new());
        let handler = SevenZipHandler::with_legacy_env(
            backend.path().to_path_buf(),
            Some(r"C:\Tools\7z.exe".to_string()),
        );

        handler.binary_for(&host).await.unwrap();
        handler.binary_for(&host).await.unwrap();

        let warnings: Vec<String> = host
            .fake_log()
            .into_iter()
            .filter(|(level, _)| level == "warn")
            .map(|(_, message)| message)
            .collect();
        assert_eq!(
            warnings,
            vec![
                "SIMPLEFILE_7Z is no longer read. Set Settings > Plugins > 7-Zip archives > 7-Zip executable override instead."
                    .to_string()
            ]
        );
    }

    #[tokio::test]
    async fn missing_override_falls_back_to_the_bundled_binary_and_warns_once() {
        let backend = backend_dir();
        let missing = backend.path().join("gone").join("7z.exe");
        let host = HostClient::fake(HashMap::from([(
            BINARY_OVERRIDE_KEY.to_string(),
            missing.display().to_string(),
        )]));
        let handler = SevenZipHandler::with_legacy_env(backend.path().to_path_buf(), None);

        for _ in 0..2 {
            assert_eq!(
                handler.binary_for(&host).await,
                Ok(backend.path().join("7za.exe"))
            );
        }

        let warnings: Vec<String> = host
            .fake_log()
            .into_iter()
            .filter(|(level, _)| level == "warn")
            .map(|(_, message)| message)
            .collect();
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains(&missing.display().to_string()));
    }
}
