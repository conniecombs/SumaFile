//! Archive engines served by a plugin's backend process over a named pipe.
//!
//! The plugin creates the pipe server; the service connects as a client and
//! speaks the same length-prefixed JSON-RPC framing as the host connection.
//! Calls are synchronous (they run on blocking lanes) and serialized behind
//! one connection per plugin.

use serde_json::{json, Value};
use simplefile_core::archive::{self, ArchiveEngine, EngineEntry, EngineFormat};
use simplefile_ipc::frame::{decode_length, encode_frame};
use simplefile_ipc::rpc::JsonRpcRequest;
use std::fs::File;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

pub const PLUGIN_HANDSHAKE_METHOD: &str = "plugin.handshake";
pub const PLUGIN_SDK_VERSION: &str = "1.0.0";
const CLIENT_NAME: &str = "simplefile-service";

/// Windows reports a pipe whose every instance is in use as busy; the
/// plugin opens a fresh instance right away, so retry briefly.
const ERROR_PIPE_BUSY: i32 = 231;
const BUSY_RETRIES: u32 = 20;
const BUSY_RETRY_DELAY: Duration = Duration::from_millis(50);

/// One format of a registration request, as the host sends it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginArchiveFormat {
    pub extension: String,
    pub can_list: bool,
    pub can_extract: bool,
    pub can_create: bool,
    pub can_modify: bool,
    pub engine_name: String,
}

/// A failure talking to the plugin. Transport errors mean the connection
/// can no longer be trusted; remote errors are the plugin's own answer.
#[derive(Debug)]
enum CallError {
    Transport(String),
    Remote(String),
}

impl CallError {
    fn into_message(self) -> String {
        match self {
            Self::Transport(message) | Self::Remote(message) => message,
        }
    }
}

struct Connection {
    pipe: File,
    next_id: u64,
}

impl Connection {
    fn open(pipe_path: &str, plugin_id: &str) -> Result<Self, String> {
        let pipe = open_pipe(pipe_path)?;
        let mut connection = Self { pipe, next_id: 0 };
        connection
            .call(
                PLUGIN_HANDSHAKE_METHOD,
                json!({
                    "pluginId": plugin_id,
                    "hostVersion": simplefile_core::APP_DISPLAY_VERSION,
                    "sdkVersion": PLUGIN_SDK_VERSION,
                    "client": CLIENT_NAME,
                }),
            )
            .map_err(|error| {
                format!(
                    "Plugin {plugin_id} handshake failed: {}",
                    error.into_message()
                )
            })?;
        Ok(connection)
    }

    fn call(&mut self, method: &str, params: Value) -> Result<Value, CallError> {
        self.next_id += 1;
        let id = json!(self.next_id);
        let request = JsonRpcRequest {
            jsonrpc: simplefile_ipc::JSONRPC_VERSION.to_string(),
            id: Some(id.clone()),
            method: method.to_string(),
            params: Some(params),
        };
        let payload =
            serde_json::to_vec(&request).map_err(|e| CallError::Transport(e.to_string()))?;
        let frame = encode_frame(&payload).map_err(|e| CallError::Transport(e.to_string()))?;
        self.pipe
            .write_all(&frame)
            .and_then(|()| self.pipe.flush())
            .map_err(|e| CallError::Transport(format!("Plugin pipe write failed: {e}")))?;

        loop {
            let message = self.read_message()?;
            // Notifications and stale replies are not ours to answer.
            if message.get("id") != Some(&id) {
                continue;
            }
            if let Some(error) = message.get("error").filter(|error| !error.is_null()) {
                let text = error
                    .get("message")
                    .and_then(Value::as_str)
                    .map(str::to_string)
                    .unwrap_or_else(|| error.to_string());
                return Err(CallError::Remote(text));
            }
            return match message.get("result") {
                Some(result) => Ok(result.clone()),
                None => Err(CallError::Transport(format!(
                    "Plugin reply to {method} has neither result nor error"
                ))),
            };
        }
    }

    fn read_message(&mut self) -> Result<Value, CallError> {
        let mut header = [0u8; 4];
        self.pipe
            .read_exact(&mut header)
            .map_err(|e| CallError::Transport(format!("Plugin pipe read failed: {e}")))?;
        let length = decode_length(header).map_err(|e| CallError::Transport(e.to_string()))?;
        let mut payload = vec![0u8; length as usize];
        self.pipe
            .read_exact(&mut payload)
            .map_err(|e| CallError::Transport(format!("Plugin pipe read failed: {e}")))?;
        serde_json::from_slice(&payload)
            .map_err(|e| CallError::Transport(format!("Plugin sent malformed JSON: {e}")))
    }
}

fn open_pipe(pipe_path: &str) -> Result<File, String> {
    let mut attempts = 0;
    loop {
        match std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(pipe_path)
        {
            Ok(pipe) => return Ok(pipe),
            Err(error)
                if error.raw_os_error() == Some(ERROR_PIPE_BUSY) && attempts < BUSY_RETRIES =>
            {
                attempts += 1;
                std::thread::sleep(BUSY_RETRY_DELAY);
            }
            Err(error) => {
                return Err(format!(
                    "Could not connect to plugin pipe {pipe_path}: {error}"
                ))
            }
        }
    }
}

/// The connection a plugin's engines share. `None` after a transport
/// error; the next call reconnects (and handshakes again) once.
pub struct PluginConnection {
    plugin_id: String,
    pipe_path: String,
    connection: Mutex<Option<Connection>>,
}

impl PluginConnection {
    /// Connect and handshake now, so a plugin that is not serving fails
    /// registration instead of the first archive operation.
    pub fn connect(plugin_id: &str, pipe_name: &str) -> Result<Arc<Self>, String> {
        let pipe_path = crate::pipe_path(pipe_name);
        let connection = Connection::open(&pipe_path, plugin_id)?;
        Ok(Arc::new(Self {
            plugin_id: plugin_id.to_string(),
            pipe_path,
            connection: Mutex::new(Some(connection)),
        }))
    }

    pub fn call(&self, method: &str, params: Value) -> Result<Value, String> {
        let mut guard = self
            .connection
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if guard.is_none() {
            *guard = Some(Connection::open(&self.pipe_path, &self.plugin_id)?);
        }
        let connection = guard.as_mut().expect("connection was just opened");
        match connection.call(method, params) {
            Ok(result) => Ok(result),
            Err(CallError::Remote(message)) => Err(message),
            Err(CallError::Transport(message)) => {
                *guard = None;
                Err(message)
            }
        }
    }
}

/// An archive engine that forwards every operation to a plugin backend.
pub struct PipeArchiveEngine {
    name: String,
    connection: Arc<PluginConnection>,
}

impl PipeArchiveEngine {
    pub fn new(name: impl Into<String>, connection: Arc<PluginConnection>) -> Self {
        Self {
            name: name.into(),
            connection,
        }
    }
}

impl ArchiveEngine for PipeArchiveEngine {
    fn name(&self) -> &str {
        &self.name
    }

    fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String> {
        let result = self.connection.call(
            "archive.list",
            json!({ "archivePath": archive.to_string_lossy() }),
        )?;
        let entries = result
            .get("entries")
            .and_then(Value::as_array)
            .ok_or_else(|| format!("{} returned no entries array", self.name))?;
        entries
            .iter()
            .map(|entry| {
                let path = entry
                    .get("path")
                    .and_then(Value::as_str)
                    .ok_or_else(|| format!("{} returned an entry without a path", self.name))?;
                Ok(EngineEntry {
                    path: path.to_string(),
                    is_dir: entry.get("isDir").and_then(Value::as_bool).unwrap_or(false),
                    size: entry.get("size").and_then(Value::as_u64).unwrap_or(0),
                    compressed_size: entry
                        .get("compressedSize")
                        .and_then(Value::as_u64)
                        .unwrap_or(0),
                })
            })
            .collect()
    }

    fn extract(
        &self,
        archive: &Path,
        destination: &Path,
        inner_path: Option<&Path>,
    ) -> Result<(), String> {
        let mut params = json!({
            "archivePath": archive.to_string_lossy(),
            "destination": destination.to_string_lossy(),
        });
        if let Some(inner_path) = inner_path {
            params["innerPath"] = json!(inner_path.to_string_lossy());
        }
        self.connection.call("archive.extract", params).map(|_| ())
    }

    fn create(&self, archive: &Path, paths: &[PathBuf]) -> Result<(), String> {
        let paths: Vec<String> = paths
            .iter()
            .map(|path| path.to_string_lossy().into_owned())
            .collect();
        self.connection
            .call(
                "archive.create",
                json!({ "archivePath": archive.to_string_lossy(), "paths": paths }),
            )
            .map(|_| ())
    }
}

/// Connect to `pipe_name`, handshake, then register `formats` for
/// `plugin_id`, replacing whatever that plugin registered before. Formats
/// that share an engine name share one engine; all share one connection.
/// On any failure nothing new is registered.
pub fn register_plugin_engine(
    plugin_id: &str,
    pipe_name: &str,
    formats: Vec<PluginArchiveFormat>,
) -> Result<(), String> {
    if plugin_id.trim().is_empty() {
        return Err("pluginId must not be empty".to_string());
    }
    if pipe_name.trim().is_empty() {
        return Err("pipeName must not be empty".to_string());
    }
    if formats.is_empty() {
        return Err(format!("Plugin {plugin_id} registered no archive formats"));
    }

    let connection = PluginConnection::connect(plugin_id, pipe_name)?;

    let mut groups: Vec<(String, Vec<EngineFormat>)> = Vec::new();
    for format in formats {
        let engine_format = EngineFormat {
            extension: format.extension,
            can_list: format.can_list,
            can_extract: format.can_extract,
            can_create: format.can_create,
            can_modify: format.can_modify,
        };
        let engine_name = if format.engine_name.trim().is_empty() {
            plugin_id.to_string()
        } else {
            format.engine_name
        };
        match groups.iter_mut().find(|(name, _)| *name == engine_name) {
            Some((_, group)) => group.push(engine_format),
            None => groups.push((engine_name, vec![engine_format])),
        }
    }

    archive::unregister_engine(plugin_id);
    for (engine_name, group) in groups {
        let engine = PipeArchiveEngine::new(engine_name, Arc::clone(&connection));
        archive::register_engine(plugin_id, group, Arc::new(engine));
    }
    Ok(())
}

/// Drop every engine `plugin_id` registered. Unknown ids are a no-op.
pub fn unregister_plugin_engine(plugin_id: &str) {
    archive::unregister_engine(plugin_id);
}
