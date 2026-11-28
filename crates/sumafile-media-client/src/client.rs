//! The client: starts the media process on the first request, matches
//! replies to requests, and keeps a file that hangs or crashes the process
//! from costing anything but itself.
//!
//! - **Gates.** At most `max_thumbnails` thumbnail requests and
//!   `max_previews` preview requests are with the process at a time. Further
//!   calls wait here, so a request's timeout measures its decode and never
//!   time spent queued.
//! - **Timeout.** A request that outlasts its timeout ends the process, and
//!   its file is recorded as bad.
//! - **A process that stops under requests.** With one request in flight,
//!   that file is recorded as bad. With several, nobody can tell which file
//!   did it, so each is sent again with nothing else in flight; a file that
//!   fails alone is recorded as bad.
//! - **Bad files** answer `Unsupported` at once, without starting the
//!   process, until the file's size or modified time changes.
//! - **Idle.** A process with nothing to do for `idle_exit` is ended. The
//!   client does this, not the process: only the client knows that no request
//!   is on its way.
//! - **A process that never starts.** The process says that it is running
//!   before it reads a request. One that ends, or stays silent for
//!   `ready_timeout`, before saying so was never sent a file: none is
//!   recorded as bad, and starting waits `spawn_retry_interval` before it is
//!   tried again.

use crate::process::{self, Job};
use crate::protocol::{
    read_message, write_message, Decoder, FailureKind, ImageKind, Method, Request, ResponseHeader,
    SoftwareReason, READY_ID,
};
use std::collections::{HashMap, VecDeque};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError, RwLock, Weak};
use std::time::{Duration, Instant, SystemTime};

/// The media process's file name, beside the file service.
pub const HELPER_FILE_NAME: &str = if cfg!(windows) {
    "sumafile-media.exe"
} else {
    "sumafile-media"
};

/// Set to a path to use that media process instead of the one beside the
/// running program. For tests and development.
pub const HELPER_PATH_VARIABLE: &str = "SUMAFILE_MEDIA_HELPER";

/// The video process's file name, beside the file service.
pub const VIDEO_HELPER_FILE_NAME: &str = if cfg!(windows) {
    "sumafile-video.exe"
} else {
    "sumafile-video"
};

/// As `SUMAFILE_MEDIA_HELPER`, for the video process.
pub const VIDEO_HELPER_PATH_VARIABLE: &str = "SUMAFILE_VIDEO_HELPER";

/// Where the media process is: `SUMAFILE_MEDIA_HELPER` when set, else
/// `sumafile-media` beside the running program.
pub fn default_helper_path() -> PathBuf {
    beside_this_program(HELPER_PATH_VARIABLE, HELPER_FILE_NAME)
}

/// Where the video process is: `SUMAFILE_VIDEO_HELPER` when set, else
/// `sumafile-video` beside the running program.
pub fn default_video_helper_path() -> PathBuf {
    beside_this_program(VIDEO_HELPER_PATH_VARIABLE, VIDEO_HELPER_FILE_NAME)
}

fn beside_this_program(variable: &str, file_name: &str) -> PathBuf {
    if let Some(path) = std::env::var_os(variable).filter(|path| !path.is_empty()) {
        return PathBuf::from(path);
    }
    std::env::current_exe()
        .ok()
        .and_then(|program| program.parent().map(|folder| folder.join(file_name)))
        .unwrap_or_else(|| PathBuf::from(file_name))
}

#[derive(Debug, Clone)]
pub struct ClientConfig {
    pub helper_path: PathBuf,
    /// Arguments the process is started with. SumaFile passes none.
    pub helper_args: Vec<String>,
    /// How long a started process may take to say that it is running.
    pub ready_timeout: Duration,
    /// How long one thumbnail may take to decode.
    pub thumbnail_timeout: Duration,
    /// How long one preview may take to decode.
    pub preview_timeout: Duration,
    /// How long one video thumbnail may take. Opening a video on a network
    /// share is slow.
    pub video_timeout: Duration,
    /// How long the process may have nothing to do before it is ended.
    pub idle_exit: Duration,
    pub max_thumbnails: usize,
    pub max_previews: usize,
    /// How many video thumbnails are with the process at a time.
    pub max_videos: usize,
    pub max_bad_files: usize,
    /// The most memory the process may commit (Windows). 0 sets no limit.
    pub memory_limit_bytes: u64,
    /// After the process failed to start, how long until starting is tried again.
    pub spawn_retry_interval: Duration,
}

impl ClientConfig {
    /// SumaFile's numbers, for the media process at `helper_path`.
    pub fn for_helper(helper_path: impl Into<PathBuf>) -> Self {
        Self {
            helper_path: helper_path.into(),
            helper_args: Vec::new(),
            ready_timeout: Duration::from_secs(15),
            thumbnail_timeout: Duration::from_secs(20),
            preview_timeout: Duration::from_secs(60),
            video_timeout: Duration::from_secs(30),
            idle_exit: Duration::from_secs(120),
            max_thumbnails: 4,
            max_previews: 2,
            max_videos: 4,
            max_bad_files: 1024,
            memory_limit_bytes: 3 * 1024 * 1024 * 1024,
            spawn_retry_interval: Duration::from_secs(30),
        }
    }
}

/// An encoded picture from the media process.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Rendered {
    pub kind: ImageKind,
    pub width: u32,
    pub height: u32,
    pub bytes: Vec<u8>,
}

/// A video thumbnail from the video process, and who decoded its frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VideoRendered {
    pub rendered: Rendered,
    pub decoder: Decoder,
    /// Why the processor decoded it. `None` for a hardware decode.
    pub reason: Option<SoftwareReason>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MediaError {
    /// No picture can come from the media process for this file: it has no
    /// decoder for it, the process is not installed, or the file is recorded
    /// as bad. The caller's fallback applies.
    Unsupported,
    /// Decoding would take more memory than the process allows.
    TooLarge,
    /// The file's contents are not what its format says.
    Decode(String),
    /// The file could not be read.
    Io(String),
    /// The process could not be started, took too long, or stopped.
    Failed(String),
}

impl std::fmt::Display for MediaError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Unsupported => formatter.write_str("This image cannot be decoded by SumaFile."),
            Self::TooLarge => formatter.write_str("The image is too large to decode."),
            Self::Decode(message) | Self::Io(message) | Self::Failed(message) => {
                formatter.write_str(message)
            }
        }
    }
}

impl std::error::Error for MediaError {}

/// What the client has done so far, for logs and tests.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ClientStats {
    /// How many times the process was started.
    pub starts: u64,
    /// How many times starting it failed: it could not be run, or it ended
    /// or stayed silent before it said it was running.
    pub spawn_failures: u64,
    /// How many of the starts run without the memory limit and without being
    /// tied to this program's life, because Windows refused the job object.
    pub starts_without_limits: u64,
    /// Whether a process is running now.
    pub running: bool,
    pub process_id: Option<u32>,
}

pub struct MediaClient {
    inner: Arc<Inner>,
}

struct Inner {
    config: ClientConfig,
    state: Mutex<State>,
    /// Ordinary requests share it. A request that is sent again after the
    /// process stopped under it holds it alone.
    turn: RwLock<()>,
    thumbnails: Gate,
    previews: Gate,
    videos: Gate,
    next_id: AtomicU64,
    starts: AtomicU64,
    spawn_failures: AtomicU64,
    starts_without_limits: AtomicU64,
}

struct State {
    helper: Option<Helper>,
    bad: BadFiles,
    last_spawn_failure: Option<Instant>,
    idle_watch_running: bool,
}

struct Helper {
    child: Child,
    /// Locked for one whole message at a time. It is never held together
    /// with the client's state: a write into a full pipe waits, and ending
    /// the process, which is what releases that write, needs the state.
    stdin: Arc<Mutex<ChildStdin>>,
    shared: Arc<HelperShared>,
    _job: Option<Job>,
}

/// What a running process shares with the thread that reads its replies.
struct HelperShared {
    waiting: Mutex<Waiting>,
    /// Signalled when the process says it is running, and when its output ends.
    started: Condvar,
    /// Set, before the process is ended, by a client that ends it on purpose:
    /// the requests still in flight are then nobody's fault.
    ended: AtomicBool,
    last_activity: Mutex<Instant>,
}

struct Waiting {
    replies: HashMap<u64, mpsc::Sender<Reply>>,
    /// The process has said that it is running.
    ready: bool,
    /// The process's output has ended; no reply will come.
    gone: bool,
}

enum Reply {
    Answer(ResponseHeader, Vec<u8>),
    /// The process stopped. `alone`: this was the only request in flight,
    /// and the process was not ended on purpose.
    Stopped {
        alone: bool,
    },
}

/// A successful reply: the picture, and what a video reply adds.
struct Answer {
    rendered: Rendered,
    decoder: Option<Decoder>,
    reason: Option<SoftwareReason>,
}

/// What one request asks for, beyond its file.
#[derive(Clone, Copy)]
struct Ask {
    method: Method,
    /// The box's side for a thumbnail, the long side for a preview.
    side: u32,
    position_percent: Option<u8>,
    hardware: Option<bool>,
}

enum Outcome {
    Done(Result<Answer, MediaError>),
    /// The process stopped under this request, with others in flight.
    Interrupted,
}

#[derive(Clone, Copy)]
enum Turn {
    Shared,
    Alone,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct FileStamp {
    length: u64,
    modified: Option<SystemTime>,
}

impl FileStamp {
    fn of(path: &Path) -> std::io::Result<Self> {
        let metadata = std::fs::metadata(path)?;
        Ok(Self {
            length: metadata.len(),
            modified: metadata.modified().ok(),
        })
    }
}

/// Files that hung or crashed the process, oldest first, at most `limit`.
struct BadFiles {
    order: VecDeque<PathBuf>,
    stamps: HashMap<PathBuf, FileStamp>,
    limit: usize,
}

impl BadFiles {
    fn new(limit: usize) -> Self {
        Self {
            order: VecDeque::new(),
            stamps: HashMap::new(),
            limit,
        }
    }

    /// True while `path` is recorded and unchanged. A changed file is forgotten.
    fn holds(&mut self, path: &Path, stamp: &FileStamp) -> bool {
        match self.stamps.get(path) {
            Some(recorded) if recorded == stamp => true,
            Some(_) => {
                self.stamps.remove(path);
                self.order.retain(|recorded| recorded != path);
                false
            }
            None => false,
        }
    }

    fn record(&mut self, path: &Path, stamp: FileStamp) {
        if self.limit == 0 {
            return;
        }
        if self.stamps.insert(path.to_path_buf(), stamp).is_none() {
            self.order.push_back(path.to_path_buf());
        }
        while self.order.len() > self.limit {
            if let Some(oldest) = self.order.pop_front() {
                self.stamps.remove(&oldest);
            }
        }
    }
}

/// Lets at most a fixed number of callers through at a time.
struct Gate {
    free: Mutex<usize>,
    freed: Condvar,
}

struct Slot<'a>(&'a Gate);

impl Gate {
    fn new(slots: usize) -> Self {
        Self {
            free: Mutex::new(slots.max(1)),
            freed: Condvar::new(),
        }
    }

    fn enter(&self) -> Slot<'_> {
        let mut free = lock(&self.free);
        while *free == 0 {
            free = self
                .freed
                .wait(free)
                .unwrap_or_else(PoisonError::into_inner);
        }
        *free -= 1;
        Slot(self)
    }
}

impl Drop for Slot<'_> {
    fn drop(&mut self) {
        *lock(&self.0.free) += 1;
        self.0.freed.notify_one();
    }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

impl MediaClient {
    /// A client for the process `config` names. Nothing is started here.
    pub fn new(config: ClientConfig) -> Self {
        Self {
            inner: Arc::new(Inner {
                state: Mutex::new(State {
                    helper: None,
                    bad: BadFiles::new(config.max_bad_files),
                    last_spawn_failure: None,
                    idle_watch_running: false,
                }),
                turn: RwLock::new(()),
                thumbnails: Gate::new(config.max_thumbnails),
                previews: Gate::new(config.max_previews),
                videos: Gate::new(config.max_videos),
                next_id: AtomicU64::new(1),
                starts: AtomicU64::new(0),
                spawn_failures: AtomicU64::new(0),
                starts_without_limits: AtomicU64::new(0),
                config,
            }),
        }
    }

    /// A JPEG of `path` that fits a `size` by `size` box.
    pub fn thumbnail(&self, path: &Path, size: u32) -> Result<Rendered, MediaError> {
        self.request(path, Ask::picture(Method::Thumbnail, size))
            .map(|answer| answer.rendered)
    }

    /// `path` at screen size: at most `max_dimension` on its long side.
    pub fn preview(&self, path: &Path, max_dimension: u32) -> Result<Rendered, MediaError> {
        self.request(path, Ask::picture(Method::Preview, max_dimension))
            .map(|answer| answer.rendered)
    }

    /// A JPEG of the frame `position_percent` of the way through the video
    /// at `path`, that fits a `size` by `size` box. `hardware`: whether the
    /// graphics card may decode it. The answer says who did.
    pub fn video_thumbnail(
        &self,
        path: &Path,
        size: u32,
        position_percent: u8,
        hardware: bool,
    ) -> Result<VideoRendered, MediaError> {
        let answer = self.request(
            path,
            Ask {
                method: Method::VideoThumbnail,
                side: size,
                position_percent: Some(position_percent),
                hardware: Some(hardware),
            },
        )?;
        match answer.decoder {
            Some(decoder) => Ok(VideoRendered {
                rendered: answer.rendered,
                decoder,
                reason: answer.reason,
            }),
            None => Err(MediaError::Failed(NOT_UNDERSTOOD.to_string())),
        }
    }

    /// How many starts ran without the job object's limits. Unlike
    /// [`Self::stats`] this waits for nothing: it may be asked while a
    /// process is starting.
    pub fn starts_without_limits(&self) -> u64 {
        self.inner.starts_without_limits.load(Ordering::Relaxed)
    }

    pub fn stats(&self) -> ClientStats {
        let state = lock(&self.inner.state);
        let running = state
            .helper
            .as_ref()
            .filter(|helper| !lock(&helper.shared.waiting).gone);
        ClientStats {
            starts: self.inner.starts.load(Ordering::Relaxed),
            spawn_failures: self.inner.spawn_failures.load(Ordering::Relaxed),
            starts_without_limits: self.inner.starts_without_limits.load(Ordering::Relaxed),
            running: running.is_some(),
            process_id: running.map(|helper| helper.child.id()),
        }
    }

    fn request(&self, path: &Path, ask: Ask) -> Result<Answer, MediaError> {
        let inner = &self.inner;
        let Some(path_text) = path.to_str() else {
            return Err(MediaError::Unsupported);
        };
        let stamp = FileStamp::of(path)
            .map_err(|error| MediaError::Io(format!("Cannot read {}: {error}", path.display())))?;
        if lock(&inner.state).bad.holds(path, &stamp) || !inner.config.helper_path.is_file() {
            return Err(MediaError::Unsupported);
        }

        let (gate, timeout) = match ask.method {
            Method::Thumbnail => (&inner.thumbnails, inner.config.thumbnail_timeout),
            Method::Preview => (&inner.previews, inner.config.preview_timeout),
            Method::VideoThumbnail => (&inner.videos, inner.config.video_timeout),
        };
        let _slot = gate.enter();
        // Another call may have found this file bad while this one waited.
        if lock(&inner.state).bad.holds(path, &stamp) {
            return Err(MediaError::Unsupported);
        }

        let request = |id| Request {
            id,
            method: ask.method,
            path: path_text.to_string(),
            size: (!matches!(ask.method, Method::Preview)).then_some(ask.side),
            max_dimension: matches!(ask.method, Method::Preview).then_some(ask.side),
            position_percent: ask.position_percent,
            hardware: ask.hardware,
        };
        let file = (path, &stamp);
        match Inner::attempt(inner, &request, file, timeout, Turn::Shared) {
            Outcome::Done(result) => result,
            Outcome::Interrupted => {
                match Inner::attempt(inner, &request, file, timeout, Turn::Alone) {
                    Outcome::Done(result) => result,
                    Outcome::Interrupted => Err(inner.record_bad(file, STOPPED)),
                }
            }
        }
    }
}

impl Ask {
    fn picture(method: Method, side: u32) -> Self {
        Self {
            method,
            side,
            position_percent: None,
            hardware: None,
        }
    }
}

const NOT_UNDERSTOOD: &str = "The media process sent a reply SumaFile does not understand.";
const STOPPED: &str = "The media process stopped while decoding this file.";
const NOT_STARTED: &str = "The media process could not be started.";
/// How long a request that timed out waits for a reply the reader had
/// already picked up for it.
const LATE_REPLY_WAIT: Duration = Duration::from_millis(250);
const TOO_LONG: &str = "The media process took too long to decode this file.";

impl Inner {
    fn record_bad(&self, (path, stamp): (&Path, &FileStamp), message: &str) -> MediaError {
        lock(&self.state).bad.record(path, stamp.clone());
        MediaError::Failed(message.to_string())
    }

    fn attempt(
        inner: &Arc<Self>,
        request: &dyn Fn(u64) -> Request,
        file: (&Path, &FileStamp),
        timeout: Duration,
        turn: Turn,
    ) -> Outcome {
        let _shared;
        let _alone;
        match turn {
            Turn::Shared => _shared = inner.turn.read().unwrap_or_else(PoisonError::into_inner),
            Turn::Alone => _alone = inner.turn.write().unwrap_or_else(PoisonError::into_inner),
        }

        let id = inner.next_id.fetch_add(1, Ordering::Relaxed);
        let (shared, replies) = match Self::send(inner, &request(id)) {
            Ok(sent) => sent,
            Err(outcome) => return outcome,
        };

        let reply = match replies.recv_timeout(timeout) {
            Err(RecvTimeoutError::Timeout) => {
                // Take the request back. From here the process ends because
                // of this request, so nothing else in flight is to blame:
                // that is settled under the lock the reader assigns blame
                // under. If the reader took the request first, its reply is
                // on the way and counts.
                let took_back = {
                    let mut waiting = lock(&shared.waiting);
                    let took_back = waiting.replies.remove(&id).is_some();
                    if took_back {
                        shared.ended.store(true, Ordering::SeqCst);
                    }
                    took_back
                };
                if took_back {
                    Err(RecvTimeoutError::Timeout)
                } else {
                    replies.recv_timeout(LATE_REPLY_WAIT)
                }
            }
            reply => reply,
        };

        match reply {
            Ok(Reply::Answer(header, body)) => Outcome::Done(rendered(header, body)),
            Ok(Reply::Stopped { alone: true }) => {
                Outcome::Done(Err(inner.record_bad(file, STOPPED)))
            }
            Ok(Reply::Stopped { alone: false }) | Err(RecvTimeoutError::Disconnected) => {
                Outcome::Interrupted
            }
            Err(RecvTimeoutError::Timeout) => {
                let mut state = lock(&inner.state);
                if state
                    .helper
                    .as_ref()
                    .is_some_and(|helper| Arc::ptr_eq(&helper.shared, &shared))
                {
                    end(state.helper.take());
                }
                state.bad.record(file.0, file.1.clone());
                Outcome::Done(Err(MediaError::Failed(TOO_LONG.to_string())))
            }
        }
    }

    /// Hand `request` to the process, starting it when none is running.
    /// On success: the process's shared state and where the reply arrives.
    fn send(
        inner: &Arc<Self>,
        request: &Request,
    ) -> Result<(Arc<HelperShared>, mpsc::Receiver<Reply>), Outcome> {
        let (sender, replies) = mpsc::channel();
        let (shared, stdin) = {
            let mut state = lock(&inner.state);
            if state
                .helper
                .as_ref()
                .is_some_and(|helper| lock(&helper.shared.waiting).gone)
            {
                end(state.helper.take());
            }
            if state.helper.is_none() {
                state.helper = Some(Self::start(inner, &mut state)?);
            }
            let Some(helper) = state.helper.as_ref() else {
                return Err(Outcome::Interrupted);
            };

            {
                let mut waiting = lock(&helper.shared.waiting);
                if waiting.gone {
                    return Err(Outcome::Interrupted);
                }
                waiting.replies.insert(request.id, sender);
            }
            *lock(&helper.shared.last_activity) = Instant::now();
            (Arc::clone(&helper.shared), Arc::clone(&helper.stdin))
        };

        // The state is free again here: see `Helper::stdin`.
        if write_message(&mut *lock(&stdin), request, &[]).is_err() {
            // The process is going or gone. If its reader has not told this
            // request yet, take the request back.
            lock(&shared.waiting).replies.remove(&request.id);
            return Err(Outcome::Interrupted);
        }
        Ok((shared, replies))
    }

    /// Start the process and wait until it says that it is running. The
    /// caller holds the state, so every other request waits for this start
    /// instead of making one of its own.
    fn start(inner: &Arc<Self>, state: &mut State) -> Result<Helper, Outcome> {
        let failed = |message: String| Outcome::Done(Err(MediaError::Failed(message)));
        if state
            .last_spawn_failure
            .is_some_and(|at| at.elapsed() < inner.config.spawn_retry_interval)
        {
            return Err(failed(NOT_STARTED.to_string()));
        }
        let not_started = |state: &mut State, why: &dyn std::fmt::Display| {
            state.last_spawn_failure = Some(Instant::now());
            inner.spawn_failures.fetch_add(1, Ordering::Relaxed);
            failed(format!("{NOT_STARTED} {why}"))
        };

        let mut spawned = match process::spawn(
            &inner.config.helper_path,
            &inner.config.helper_args,
            inner.config.memory_limit_bytes,
        ) {
            Ok(spawned) => spawned,
            Err(error) => return Err(not_started(state, &error)),
        };

        let shared = Arc::new(HelperShared {
            waiting: Mutex::new(Waiting {
                replies: HashMap::new(),
                ready: false,
                gone: false,
            }),
            started: Condvar::new(),
            ended: AtomicBool::new(false),
            last_activity: Mutex::new(Instant::now()),
        });
        let reader_shared = Arc::clone(&shared);
        let output = spawned.stdout;
        let reader = std::thread::Builder::new()
            .name("media-replies".to_string())
            .spawn(move || read_replies(output, &reader_shared));

        let running = reader.is_ok() && {
            let waiting = lock(&shared.waiting);
            let (waiting, _) = shared
                .started
                .wait_timeout_while(waiting, inner.config.ready_timeout, |waiting| {
                    !waiting.ready && !waiting.gone
                })
                .unwrap_or_else(PoisonError::into_inner);
            waiting.ready && !waiting.gone
        };
        if !running {
            // It was sent no request, so no file is to blame.
            let _ = spawned.child.kill();
            let _ = spawned.child.wait();
            let why = match reader {
                Err(error) => error.to_string(),
                Ok(_) => "It ended or stayed silent as it started.".to_string(),
            };
            return Err(not_started(state, &why));
        }
        state.last_spawn_failure = None;
        inner.starts.fetch_add(1, Ordering::Relaxed);
        if matches!(spawned.job, process::Held::Refused) {
            inner.starts_without_limits.fetch_add(1, Ordering::Relaxed);
        }

        if !state.idle_watch_running {
            let watched = Arc::downgrade(inner);
            state.idle_watch_running = std::thread::Builder::new()
                .name("media-idle".to_string())
                .spawn(move || watch_idle(&watched))
                .is_ok();
        }

        Ok(Helper {
            child: spawned.child,
            stdin: Arc::new(Mutex::new(spawned.stdin)),
            shared,
            _job: spawned.job.into_job(),
        })
    }
}

impl Drop for Inner {
    fn drop(&mut self) {
        end(lock(&self.state).helper.take());
    }
}

/// End a process on purpose and reap it.
fn end(helper: Option<Helper>) {
    if let Some(mut helper) = helper {
        helper.shared.ended.store(true, Ordering::SeqCst);
        let _ = helper.child.kill();
        let _ = helper.child.wait();
    }
}

fn rendered(header: ResponseHeader, body: Vec<u8>) -> Result<Answer, MediaError> {
    if header.ok {
        return match (header.format, header.width, header.height) {
            (Some(kind), Some(width), Some(height)) => Ok(Answer {
                rendered: Rendered {
                    kind,
                    width,
                    height,
                    bytes: body,
                },
                decoder: header.decoder,
                reason: header.reason,
            }),
            _ => Err(MediaError::Failed(NOT_UNDERSTOOD.to_string())),
        };
    }
    let message = header.error.unwrap_or_default();
    Err(match header.kind {
        Some(FailureKind::Unsupported) | None => MediaError::Unsupported,
        Some(FailureKind::TooLarge) => MediaError::TooLarge,
        Some(FailureKind::Decode) => MediaError::Decode(message),
        Some(FailureKind::Io) => MediaError::Io(message),
    })
}

/// Deliver each reply to the request that waits for it. When the process's
/// output ends, tell every request still waiting.
fn read_replies(mut output: ChildStdout, shared: &HelperShared) {
    while let Ok(Some((header, body))) = read_message(&mut output) {
        let Ok(header) = serde_json::from_slice::<ResponseHeader>(&header) else {
            continue;
        };
        *lock(&shared.last_activity) = Instant::now();
        if header.id == READY_ID {
            lock(&shared.waiting).ready = true;
            shared.started.notify_all();
            continue;
        }
        let waiter = lock(&shared.waiting).replies.remove(&header.id);
        if let Some(waiter) = waiter {
            let _ = waiter.send(Reply::Answer(header, body));
        }
    }

    let mut waiting = lock(&shared.waiting);
    waiting.gone = true;
    shared.started.notify_all();
    let alone = waiting.replies.len() == 1 && !shared.ended.load(Ordering::SeqCst);
    for (_, waiter) in waiting.replies.drain() {
        let _ = waiter.send(Reply::Stopped { alone });
    }
}

/// End the process once it has had nothing to do for `idle_exit`. The thread
/// runs only while a process does, so an idle client costs nothing.
fn watch_idle(client: &Weak<Inner>) {
    loop {
        let Some(inner) = client.upgrade() else {
            return;
        };
        let pause =
            (inner.config.idle_exit / 4).clamp(Duration::from_millis(25), Duration::from_secs(5));
        {
            let mut state = lock(&inner.state);
            let idle = match state.helper.as_ref() {
                None => true,
                Some(helper) => {
                    let waiting = lock(&helper.shared.waiting);
                    waiting.gone
                        || (waiting.replies.is_empty()
                            && lock(&helper.shared.last_activity).elapsed()
                                >= inner.config.idle_exit)
                }
            };
            if idle {
                end(state.helper.take());
                state.idle_watch_running = false;
                return;
            }
        }
        drop(inner);
        std::thread::sleep(pause);
    }
}
