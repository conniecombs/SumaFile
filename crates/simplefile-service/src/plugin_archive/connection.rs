//! One pipe connection to a plugin backend: its I/O thread, the handshake
//! check and the call loop.
//!
//! While a call waits for its reply it reads every frame the backend sends.
//! An `archive.progress` for the call's operation restarts an idle deadline
//! and is relayed to the caller; other notifications and stale replies are
//! dropped. A tracked call also watches its cancel flag every 50 ms. The
//! first time the flag is set it asks the pool to send `cancel` on another
//! connection, then waits at most the cancel deadline for its own reply.

use super::{DeadlineClass, PluginTimeouts, PLUGIN_HANDSHAKE_METHOD, PLUGIN_SDK_VERSION};
use serde_json::{json, Value};
use simplefile_core::archive::EngineProgress;
use simplefile_ipc::frame::{decode_length, encode_frame};
use simplefile_ipc::rpc::JsonRpcRequest;
use std::fs::File;
use std::io::{Read, Write};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::Arc;
use std::time::{Duration, Instant};

const CLIENT_NAME: &str = "simplefile-service";
const PROGRESS_NOTIFICATION: &str = "archive.progress";
/// The SDK major this service speaks.
const SDK_MAJOR: &str = "1";

/// Windows reports a pipe whose every instance is in use as busy; the
/// plugin opens a fresh instance right away, so retry briefly.
const ERROR_PIPE_BUSY: i32 = 231;
const BUSY_RETRIES: u32 = 20;
const BUSY_RETRY_DELAY: Duration = Duration::from_millis(50);
/// How often a tracked call looks at its cancel flag.
const CANCEL_POLL: Duration = Duration::from_millis(50);

/// A failure talking to the plugin. Transport errors (including timeouts)
/// mean the connection can no longer be trusted; remote errors are the
/// plugin's own answer. `Cancelled` is a tracked call whose caller
/// cancelled it.
#[derive(Debug)]
pub(super) enum CallError {
    Transport(String),
    Remote(String),
    Cancelled,
}

impl CallError {
    fn into_message(self) -> String {
        match self {
            Self::Transport(message) | Self::Remote(message) => message,
            Self::Cancelled => simplefile_core::archive::CANCELLED_MESSAGE.to_string(),
        }
    }
}

/// What a call watches while it waits for its reply.
#[derive(Default)]
pub(super) struct Watch<'a> {
    /// Progress for this operation id extends an idle deadline.
    pub(super) operation_id: Option<&'a str>,
    pub(super) progress: Option<&'a (dyn Fn(EngineProgress) + Sync)>,
    pub(super) cancel: Option<CancelWatch<'a>>,
}

pub(super) struct CancelWatch<'a> {
    pub(super) flag: &'a AtomicBool,
    /// Called once, the first time `flag` is seen set.
    pub(super) send_cancel: &'a dyn Fn(),
    /// How long the call then waits for its own reply.
    pub(super) wait: Duration,
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
/// synchronous handle. A healthy connection never has a read pending
/// between calls.
pub(super) struct Connection {
    pipe: Arc<File>,
    jobs: Option<Sender<IoJob>>,
    results: Receiver<IoResult>,
    io_exited: Arc<AtomicBool>,
    next_id: u64,
    plugin_id: String,
}

impl Connection {
    /// Connect to `pipe_name` and handshake. The reply must name
    /// `plugin_id` and an SDK whose major this service speaks.
    pub(super) fn open(
        pipe_name: &str,
        plugin_id: &str,
        timeouts: &PluginTimeouts,
    ) -> Result<Self, String> {
        let pipe = Arc::new(open_pipe(&crate::pipe_path(pipe_name))?);
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
        let reply = connection
            .call(
                PLUGIN_HANDSHAKE_METHOD,
                json!({
                    "pluginId": plugin_id,
                    "hostVersion": simplefile_core::APP_DISPLAY_VERSION,
                    "sdkVersion": PLUGIN_SDK_VERSION,
                    "client": CLIENT_NAME,
                }),
                timeouts.for_method(PLUGIN_HANDSHAKE_METHOD),
                Watch::default(),
            )
            .map_err(|error| {
                format!(
                    "Plugin {plugin_id} handshake failed: {}",
                    error.into_message()
                )
            })?;
        let answered_as = reply
            .get("pluginId")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if answered_as != plugin_id {
            return Err(format!(
                "Plugin pipe {pipe_name} answered as {answered_as}, expected {plugin_id}"
            ));
        }
        let sdk_version = reply
            .get("sdkVersion")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if sdk_version.split('.').next() != Some(SDK_MAJOR) {
            return Err(format!(
                "Plugin {plugin_id} speaks SDK {sdk_version}; this service speaks SDK {PLUGIN_SDK_VERSION}"
            ));
        }
        Ok(connection)
    }

    /// False once a transport error or timeout has closed the connection.
    pub(super) fn is_open(&self) -> bool {
        self.jobs.is_some() && !self.io_exited.load(Ordering::SeqCst)
    }

    /// Send `method` and wait for its reply under `deadline`.
    pub(super) fn call(
        &mut self,
        method: &str,
        params: Value,
        deadline: DeadlineClass,
        watch: Watch<'_>,
    ) -> Result<Value, CallError> {
        let timeout = deadline.duration();
        let mut expires = Instant::now() + timeout;
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
        self.submit(IoJob::Write(frame))?;
        self.wait_for_write(method, timeout, expires)?;

        let mut read_pending = false;
        let mut cancel_sent = false;
        loop {
            if let Some(cancel) = &watch.cancel {
                if !cancel_sent && cancel.flag.load(Ordering::SeqCst) {
                    cancel_sent = true;
                    (cancel.send_cancel)();
                    expires = Instant::now() + cancel.wait;
                }
            }
            if !read_pending {
                self.submit(IoJob::Read)?;
                read_pending = true;
            }
            let now = Instant::now();
            if now >= expires {
                self.close();
                if cancel_sent {
                    return Err(CallError::Cancelled);
                }
                return Err(self.timed_out(method, timeout));
            }
            let mut slice = expires - now;
            if watch.cancel.is_some() && !cancel_sent {
                slice = slice.min(CANCEL_POLL);
            }
            let message = match self.results.recv_timeout(slice) {
                Ok(Ok(Some(message))) => {
                    read_pending = false;
                    message
                }
                Ok(Ok(None)) => continue,
                Ok(Err(error)) => {
                    self.close();
                    return Err(CallError::Transport(error));
                }
                Err(RecvTimeoutError::Timeout) => continue,
                Err(RecvTimeoutError::Disconnected) => {
                    self.close();
                    return Err(CallError::Transport("Plugin pipe thread stopped".into()));
                }
            };

            if message.get("id") == Some(&id) {
                if cancel_sent {
                    return Err(CallError::Cancelled);
                }
                return reply_result(method, &message);
            }
            if message.get("method").and_then(Value::as_str) == Some(PROGRESS_NOTIFICATION) {
                let params = message.get("params").cloned().unwrap_or(Value::Null);
                let ours = watch.operation_id.is_some()
                    && params.get("operationId").and_then(Value::as_str) == watch.operation_id;
                if ours {
                    if !cancel_sent && matches!(deadline, DeadlineClass::Idle(_)) {
                        expires = Instant::now() + timeout;
                    }
                    if let Some(progress) = watch.progress {
                        progress(progress_update(&params));
                    }
                }
            }
            // Other notifications and stale replies are not ours to answer.
        }
    }

    fn submit(&mut self, job: IoJob) -> Result<(), CallError> {
        let sent = self
            .jobs
            .as_ref()
            .is_some_and(|jobs| jobs.send(job).is_ok());
        if sent {
            Ok(())
        } else {
            Err(CallError::Transport("Plugin pipe is closed".into()))
        }
    }

    fn wait_for_write(
        &mut self,
        method: &str,
        timeout: Duration,
        expires: Instant,
    ) -> Result<(), CallError> {
        let remaining = expires.saturating_duration_since(Instant::now());
        match self.results.recv_timeout(remaining) {
            Ok(Ok(_)) => Ok(()),
            Ok(Err(error)) => {
                self.close();
                Err(CallError::Transport(error))
            }
            Err(RecvTimeoutError::Timeout) => {
                self.close();
                Err(self.timed_out(method, timeout))
            }
            Err(RecvTimeoutError::Disconnected) => {
                self.close();
                Err(CallError::Transport("Plugin pipe thread stopped".into()))
            }
        }
    }

    fn timed_out(&self, method: &str, timeout: Duration) -> CallError {
        CallError::Transport(format!(
            "Plugin {} did not answer {method} within {}",
            self.plugin_id,
            describe(timeout)
        ))
    }

    /// Stop the I/O thread: no more jobs, and cancel the operation it is
    /// blocked in so it lets go of the pipe. The cancel is repeated briefly
    /// in case the thread had not yet entered the blocking call.
    pub(super) fn close(&mut self) {
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

fn reply_result(method: &str, message: &Value) -> Result<Value, CallError> {
    if let Some(error) = message.get("error").filter(|error| !error.is_null()) {
        let text = error
            .get("message")
            .and_then(Value::as_str)
            .map(str::to_string)
            .unwrap_or_else(|| error.to_string());
        return Err(CallError::Remote(text));
    }
    match message.get("result") {
        Some(result) => Ok(result.clone()),
        None => Err(CallError::Transport(format!(
            "Plugin reply to {method} has neither result nor error"
        ))),
    }
}

fn progress_update(params: &Value) -> EngineProgress {
    EngineProgress {
        current: params.get("current").and_then(Value::as_u64).unwrap_or(0),
        total: params.get("total").and_then(Value::as_u64).unwrap_or(0),
        current_item: params
            .get("currentItem")
            .and_then(Value::as_str)
            .map(str::to_string),
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
