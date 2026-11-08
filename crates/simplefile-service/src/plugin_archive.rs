//! Archive engines served by a plugin's backend process over a named pipe.
//!
//! The plugin creates the pipe server; the service connects as a client and
//! speaks the same length-prefixed JSON-RPC framing as the host connection.
//! Calls run on blocking lanes, are serialized behind one connection per
//! plugin, and every exchange is bounded: a plugin that stops answering
//! costs a lane worker for at most one deadline, never indefinitely.

use serde_json::{json, Value};
use simplefile_core::archive::{self, ArchiveEngine, EngineEntry, EngineFormat};
use simplefile_ipc::frame::{decode_length, encode_frame};
use simplefile_ipc::rpc::JsonRpcRequest;
use std::fs::File;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex, MutexGuard, TryLockError};
use std::time::{Duration, Instant};

pub const PLUGIN_HANDSHAKE_METHOD: &str = "plugin.handshake";
pub const PLUGIN_SDK_VERSION: &str = "1.0.0";
const CLIENT_NAME: &str = "simplefile-service";
const MAX_PIPE_NAME_LEN: usize = 200;

/// Windows reports a pipe whose every instance is in use as busy; the
/// plugin opens a fresh instance right away, so retry briefly.
const ERROR_PIPE_BUSY: i32 = 231;
const BUSY_RETRIES: u32 = 20;
const BUSY_RETRY_DELAY: Duration = Duration::from_millis(50);
const LOCK_POLL: Duration = Duration::from_millis(10);

/// How long each kind of exchange with a plugin may take.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PluginTimeouts {
    pub handshake: Duration,
    pub list: Duration,
    /// `archive.extract` and `archive.create`.
    pub transfer: Duration,
    /// How long a caller waits for another call on the same plugin.
    pub lock: Duration,
}

impl Default for PluginTimeouts {
    fn default() -> Self {
        Self {
            handshake: Duration::from_secs(5),
            list: Duration::from_secs(30),
            transfer: Duration::from_secs(600),
            lock: Duration::from_secs(30),
        }
    }
}

impl PluginTimeouts {
    fn for_method(&self, method: &str) -> Duration {
        match method {
            PLUGIN_HANDSHAKE_METHOD => self.handshake,
            "archive.list" => self.list,
            _ => self.transfer,
        }
    }
}

/// Plugin pipe names are bare names: `[A-Za-z0-9._-]{1,200}`. Anything
/// else (separators, a `\\.\pipe\` prefix, a drive path) is refused so the
/// service never opens a path a plugin chose.
pub fn is_valid_pipe_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= MAX_PIPE_NAME_LEN
        && name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
}

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

/// A failure talking to the plugin. Transport errors (including timeouts)
/// mean the connection can no longer be trusted; remote errors are the
/// plugin's own answer.
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

enum IoJob {
    Write(Vec<u8>),
    Read,
}

/// `Some` for a decoded frame, `None` for a completed write.
type IoResult = Result<Option<Value>, String>;

/// One pipe connection. All pipe I/O happens on a dedicated thread that
/// performs one job at a time, so the caller can stop waiting at a
/// deadline and cancel the stuck operation. Reads are only issued when a
/// reply is expected, so a write never queues behind a pending read on the
/// synchronous handle.
struct Connection {
    pipe: Arc<File>,
    jobs: Option<Sender<IoJob>>,
    results: Receiver<IoResult>,
    io_exited: Arc<AtomicBool>,
    next_id: u64,
    plugin_id: String,
}

impl Connection {
    fn open(pipe_path: &str, plugin_id: &str, timeouts: &PluginTimeouts) -> Result<Self, String> {
        let pipe = Arc::new(open_pipe(pipe_path)?);
        let (jobs, job_receiver) = mpsc::channel::<IoJob>();
        let (result_sender, results) = mpsc::channel::<IoResult>();
        let io_exited = Arc::new(AtomicBool::new(false));
        let thread_pipe = Arc::clone(&pipe);
        let thread_exited = Arc::clone(&io_exited);
        std::thread::Builder::new()
            .name(format!("plugin-pipe-{plugin_id}"))
            .spawn(move || {
                io_loop(&thread_pipe, &job_receiver, &result_sender);
                thread_exited.store(true, Ordering::SeqCst);
            })
            .map_err(|e| format!("Could not start plugin pipe thread: {e}"))?;

        let mut connection = Self {
            pipe,
            jobs: Some(jobs),
            results,
            io_exited,
            next_id: 0,
            plugin_id: plugin_id.to_string(),
        };
        connection
            .call(
                PLUGIN_HANDSHAKE_METHOD,
                json!({
                    "pluginId": plugin_id,
                    "hostVersion": simplefile_core::APP_DISPLAY_VERSION,
                    "sdkVersion": PLUGIN_SDK_VERSION,
                    "client": CLIENT_NAME,
                }),
                timeouts.handshake,
            )
            .map_err(|error| {
                format!(
                    "Plugin {plugin_id} handshake failed: {}",
                    error.into_message()
                )
            })?;
        Ok(connection)
    }

    fn call(&mut self, method: &str, params: Value, timeout: Duration) -> Result<Value, CallError> {
        let deadline = Instant::now() + timeout;
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
        self.run(IoJob::Write(frame), method, timeout, deadline)?;

        loop {
            let message = self
                .run(IoJob::Read, method, timeout, deadline)?
                .ok_or_else(|| CallError::Transport("Plugin pipe read returned nothing".into()))?;
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

    fn run(
        &mut self,
        job: IoJob,
        method: &str,
        timeout: Duration,
        deadline: Instant,
    ) -> Result<Option<Value>, CallError> {
        let sent = self
            .jobs
            .as_ref()
            .is_some_and(|jobs| jobs.send(job).is_ok());
        if !sent {
            return Err(CallError::Transport("Plugin pipe is closed".into()));
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        match self.results.recv_timeout(remaining) {
            Ok(result) => result.map_err(CallError::Transport),
            Err(RecvTimeoutError::Timeout) => {
                self.close();
                Err(CallError::Transport(format!(
                    "Plugin {} did not answer {method} within {}",
                    self.plugin_id,
                    describe(timeout)
                )))
            }
            Err(RecvTimeoutError::Disconnected) => {
                Err(CallError::Transport("Plugin pipe thread stopped".into()))
            }
        }
    }

    /// Stop the I/O thread: no more jobs, and cancel the operation it is
    /// blocked in so it lets go of the pipe. The cancel is repeated briefly
    /// in case the thread had not yet entered the blocking call.
    fn close(&mut self) {
        self.jobs = None;
        for _ in 0..50 {
            if self.io_exited.load(Ordering::SeqCst) {
                return;
            }
            cancel_pipe_io(&self.pipe);
            std::thread::sleep(Duration::from_millis(10));
        }
    }
}

impl Drop for Connection {
    fn drop(&mut self) {
        // An idle thread exits once the job channel closes; one stuck in a
        // read is cancelled.
        self.jobs = None;
        if !self.io_exited.load(Ordering::SeqCst) {
            cancel_pipe_io(&self.pipe);
        }
    }
}

fn io_loop(pipe: &File, jobs: &Receiver<IoJob>, results: &Sender<IoResult>) {
    let mut pipe = pipe;
    while let Ok(job) = jobs.recv() {
        let result = match job {
            IoJob::Write(frame) => pipe
                .write_all(&frame)
                .and_then(|()| pipe.flush())
                .map(|()| None)
                .map_err(|e| format!("Plugin pipe write failed: {e}")),
            IoJob::Read => read_frame(&mut pipe).map(Some),
        };
        let failed = result.is_err();
        if results.send(result).is_err() || failed {
            return;
        }
    }
}

fn read_frame(pipe: &mut &File) -> Result<Value, String> {
    let mut header = [0u8; 4];
    pipe.read_exact(&mut header)
        .map_err(|e| format!("Plugin pipe read failed: {e}"))?;
    let length = decode_length(header).map_err(|e| e.to_string())?;
    let mut payload = vec![0u8; length as usize];
    pipe.read_exact(&mut payload)
        .map_err(|e| format!("Plugin pipe read failed: {e}"))?;
    serde_json::from_slice(&payload).map_err(|e| format!("Plugin sent malformed JSON: {e}"))
}

#[cfg(windows)]
fn cancel_pipe_io(pipe: &File) {
    use std::os::windows::io::AsRawHandle;
    // SAFETY: the handle stays open while `pipe` is borrowed; a null
    // OVERLAPPED cancels every pending operation on it, including the
    // synchronous read the I/O thread is blocked in.
    unsafe {
        windows_sys::Win32::System::IO::CancelIoEx(pipe.as_raw_handle() as _, std::ptr::null());
    }
}

#[cfg(not(windows))]
fn cancel_pipe_io(_pipe: &File) {}

fn describe(duration: Duration) -> String {
    if duration.subsec_millis() == 0 {
        format!("{}s", duration.as_secs())
    } else {
        format!("{}ms", duration.as_millis())
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

/// The connection a plugin's engines share. `None` after a transport error
/// or timeout; the next call reconnects (and handshakes again) once.
pub struct PluginConnection {
    plugin_id: String,
    pipe_path: String,
    timeouts: PluginTimeouts,
    connection: Mutex<Option<Connection>>,
}

impl PluginConnection {
    /// Connect and handshake now, so a plugin that is not serving fails
    /// registration instead of the first archive operation.
    pub fn connect(plugin_id: &str, pipe_name: &str) -> Result<Arc<Self>, String> {
        Self::connect_with_timeouts(plugin_id, pipe_name, PluginTimeouts::default())
    }

    pub fn connect_with_timeouts(
        plugin_id: &str,
        pipe_name: &str,
        timeouts: PluginTimeouts,
    ) -> Result<Arc<Self>, String> {
        if !is_valid_pipe_name(pipe_name) {
            return Err(format!("Invalid plugin pipe name: {pipe_name}"));
        }
        let pipe_path = crate::pipe_path(pipe_name);
        let connection = Connection::open(&pipe_path, plugin_id, &timeouts)?;
        Ok(Arc::new(Self {
            plugin_id: plugin_id.to_string(),
            pipe_path,
            timeouts,
            connection: Mutex::new(Some(connection)),
        }))
    }

    pub fn call(&self, method: &str, params: Value) -> Result<Value, String> {
        let mut guard = self.lock()?;
        if guard.is_none() {
            *guard = Some(Connection::open(
                &self.pipe_path,
                &self.plugin_id,
                &self.timeouts,
            )?);
        }
        let connection = guard.as_mut().expect("connection was just opened");
        match connection.call(method, params, self.timeouts.for_method(method)) {
            Ok(result) => Ok(result),
            Err(CallError::Remote(message)) => Err(message),
            Err(CallError::Transport(message)) => {
                *guard = None;
                Err(message)
            }
        }
    }

    /// Wait at most `timeouts.lock` for the call in progress, so queued
    /// callers give their lane permit back instead of waiting forever.
    fn lock(&self) -> Result<MutexGuard<'_, Option<Connection>>, String> {
        let deadline = Instant::now() + self.timeouts.lock;
        loop {
            match self.connection.try_lock() {
                Ok(guard) => return Ok(guard),
                Err(TryLockError::Poisoned(poisoned)) => return Ok(poisoned.into_inner()),
                Err(TryLockError::WouldBlock) => {
                    if Instant::now() >= deadline {
                        return Err(format!("plugin {} is busy", self.plugin_id));
                    }
                    std::thread::sleep(LOCK_POLL);
                }
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

/// Connect to `pipe_name`, handshake, then replace whatever `plugin_id`
/// registered before with `formats`. Formats that share an engine name
/// share one engine; all share one connection. Formats no engine may serve
/// (empty or built-in extensions) are dropped first; if none remain, or on
/// any failure, the existing registration is left untouched.
pub fn register_plugin_engine(
    plugin_id: &str,
    pipe_name: &str,
    formats: Vec<PluginArchiveFormat>,
) -> Result<(), String> {
    if plugin_id.trim().is_empty() {
        return Err("pluginId must not be empty".to_string());
    }
    if !is_valid_pipe_name(pipe_name) {
        return Err(format!("Invalid plugin pipe name: {pipe_name}"));
    }
    let formats: Vec<PluginArchiveFormat> = formats
        .into_iter()
        .filter(|format| archive::is_registrable_extension(&format.extension))
        .collect();
    if formats.is_empty() {
        return Err(format!(
            "Plugin {plugin_id} has no registrable formats (empty or built-in extensions only)"
        ));
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

    let engines: Vec<(Vec<EngineFormat>, Arc<dyn ArchiveEngine>)> = groups
        .into_iter()
        .map(|(engine_name, group)| {
            let engine: Arc<dyn ArchiveEngine> =
                Arc::new(PipeArchiveEngine::new(engine_name, Arc::clone(&connection)));
            (group, engine)
        })
        .collect();
    if !archive::replace_engines(plugin_id, engines) {
        return Err(format!("Plugin {plugin_id} has no registrable formats"));
    }
    Ok(())
}

/// Drop every engine `plugin_id` registered. Unknown ids are a no-op.
pub fn unregister_plugin_engine(plugin_id: &str) {
    archive::unregister_engine(plugin_id);
}
