//! Watches the folders the host shows and tells it what changed in them.
//!
//! One thread per session reads the changes of every watched folder from a
//! completion port ([`engine`]), gathers them into batches ([`coalesce`])
//! and calls the session's sink. Folders are added and taken away without
//! starting over, and nothing here waits on that thread: the session awaits
//! the answer to a watch request.
//!
//! What the host is told, as [`FileChangeEvent`]s:
//!
//! - `create`, `modify`, `remove`, `rename` for an entry of a watched folder;
//! - `modify` with the path of a watched folder itself: more changed there
//!   than could be listed (a storm, or the system's own buffer ran over), so
//!   the folder is to be read again;
//! - `gone` with the path of a watched folder: it is no longer at that path
//!   and no longer watched.

// The service runs on Windows only; elsewhere this module answers that it cannot
// watch, and the gathering of changes is compiled for its tests alone.
#![cfg_attr(not(windows), allow(dead_code))]

mod coalesce;
#[cfg(windows)]
mod engine;

use simplefile_core::models::{FileChangeEvent, FileEntry};
use std::path::Path;
use std::sync::Arc;
use tokio::sync::oneshot;

/// What a changed path is now, as far as the change itself says.
#[derive(Debug, Clone)]
pub enum EntryNow {
    /// The change does not say: read the entry.
    Unknown,
    /// Nothing is at the path.
    Gone,
    /// This entry. The system reported its size, dates and attributes with
    /// the change, so it was not opened to learn them.
    Is(Box<FileEntry>),
}

/// One change of a watched folder, as the session's sink receives it.
#[derive(Debug, Clone)]
pub struct WatchChange {
    /// What the host is told.
    pub event: FileChangeEvent,
    pub now: EntryNow,
    /// The path the entry had before it was renamed inside its folder.
    pub renamed_from: Option<String>,
}

pub(crate) type Emit = Arc<dyn Fn(WatchChange) + Send + Sync>;

/// The session's watcher. Its thread starts with the first watch request and
/// ends when this is dropped.
pub struct WatcherState {
    #[cfg(windows)]
    engine: Option<engine::Engine>,
    /// Whether to ask the system for the change records that carry each
    /// entry's size, dates and attributes. Off only in tests of the plain
    /// records, which some file systems are limited to.
    #[cfg_attr(not(windows), allow(dead_code))]
    extended_records: bool,
}

impl Default for WatcherState {
    fn default() -> Self {
        Self {
            #[cfg(windows)]
            engine: None,
            extended_records: true,
        }
    }
}

impl WatcherState {
    /// A watcher that asks for the plain change records only, as on a file
    /// system that has no others.
    #[cfg(test)]
    pub(crate) fn with_plain_records() -> Self {
        Self {
            extended_records: false,
            ..Self::default()
        }
    }
}

/// The answer to a watch request, which the watcher's thread gives.
pub struct PendingWatch(Result<oneshot::Receiver<Result<(), String>>, String>);

impl PendingWatch {
    /// Awaits the answer, on the async runtime.
    pub async fn done(self) -> Result<(), String> {
        match self.0 {
            Ok(reply) => reply.await.unwrap_or_else(|_| Err(watcher_ended())),
            Err(message) => Err(message),
        }
    }

    /// Waits for the answer, on a plain thread.
    pub fn wait(self) -> Result<(), String> {
        match self.0 {
            Ok(reply) => reply
                .blocking_recv()
                .unwrap_or_else(|_| Err(watcher_ended())),
            Err(message) => Err(message),
        }
    }
}

fn watcher_ended() -> String {
    "The folder watcher has ended".to_string()
}

/// Asks for exactly `paths` to be watched from now on, reporting to `emit`.
/// Folders already watched stay watched without a gap; the others are
/// opened, and the folders no longer named are let go. When a folder cannot
/// be watched the request fails and what was watched before stays so.
///
/// Returns at once: the paths are checked and opened on the watcher's thread.
pub fn start_watch<F>(paths: Vec<String>, state: &mut WatcherState, emit: F) -> PendingWatch
where
    F: Fn(WatchChange) + Send + Sync + 'static,
{
    let mut unique_paths = Vec::new();
    let mut seen_keys = std::collections::HashSet::new();
    for path in paths {
        let trimmed = path.trim();
        if trimmed.is_empty() {
            continue;
        }
        if seen_keys.insert(coalesce_key(trimmed)) {
            unique_paths.push(trimmed.to_string());
        }
    }

    #[cfg(windows)]
    {
        if state.engine.is_none() {
            if unique_paths.is_empty() {
                return PendingWatch(Ok(answered(Ok(()))));
            }
            match engine::Engine::start(state.extended_records) {
                Ok(engine) => state.engine = Some(engine),
                Err(message) => return PendingWatch(Err(message)),
            }
        }
        let (reply, answer) = oneshot::channel();
        state
            .engine
            .as_ref()
            .expect("the engine was just started")
            .send(engine::Command::SetRoots {
                paths: unique_paths,
                emit: Arc::new(emit),
                reply: Some(reply),
            });
        PendingWatch(Ok(answer))
    }
    #[cfg(not(windows))]
    {
        let _ = (state, emit);
        if unique_paths.is_empty() {
            PendingWatch(Ok(answered(Ok(()))))
        } else {
            PendingWatch(Err("Watching folders requires Windows".to_string()))
        }
    }
}

/// A reply that is already there.
fn answered(result: Result<(), String>) -> oneshot::Receiver<Result<(), String>> {
    let (reply, answer) = oneshot::channel();
    let _ = reply.send(result);
    answer
}

/// [`start_watch`], waiting for the answer. Not for the async runtime.
pub fn watch_directories<F>(
    paths: Vec<String>,
    state: &mut WatcherState,
    emit: F,
) -> Result<(), String>
where
    F: Fn(WatchChange) + Send + Sync + 'static,
{
    start_watch(paths, state, emit).wait()
}

/// [`watch_directories`] for one folder.
pub fn watch_directory<F>(path: String, state: &mut WatcherState, emit: F) -> Result<(), String>
where
    F: Fn(WatchChange) + Send + Sync + 'static,
{
    watch_directories(vec![path], state, emit)
}

/// Lets every watched folder go. Changes already gathered are still told.
/// Returns at once.
pub fn unwatch_directory(state: &mut WatcherState) {
    #[cfg(windows)]
    if let Some(engine) = state.engine.as_ref() {
        engine.send(engine::Command::SetRoots {
            paths: Vec::new(),
            emit: Arc::new(|_| {}),
            reply: None,
        });
    }
    #[cfg(not(windows))]
    let _ = state;
}

fn is_ignored_watcher_path(path: &Path) -> bool {
    if path.extension().is_some_and(|ext| {
        let ext = ext.to_ascii_lowercase();
        ext == "tmp" || ext == "part" || ext == "crdownload"
    }) {
        return true;
    }

    path.file_name()
        .map(|name| name.to_string_lossy().to_ascii_lowercase())
        .is_some_and(|name| matches!(name.as_str(), ".ds_store" | "desktop.ini" | "thumbs.db"))
}

fn coalesce_key(path: &str) -> String {
    let unified = path.replace('\\', "/");
    let trimmed = if unified.len() > 3 && unified.ends_with('/') {
        unified.trim_end_matches('/')
    } else if unified.len() == 3 && unified.as_bytes()[1] == b':' && unified.ends_with('/') {
        &unified
    } else if unified.len() > 1 && unified.ends_with('/') {
        unified.trim_end_matches('/')
    } else {
        &unified
    };
    #[cfg(windows)]
    {
        trimmed.to_lowercase()
    }
    #[cfg(not(windows))]
    {
        trimmed.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::{coalesce_key, is_ignored_watcher_path};
    use std::path::Path;

    #[test]
    fn ignored_watcher_path_matches_noise_names_case_insensitively() {
        assert!(is_ignored_watcher_path(Path::new("C:/Users/me/Thumbs.db")));
        assert!(is_ignored_watcher_path(Path::new(
            "C:/Users/me/DESKTOP.INI"
        )));
        assert!(is_ignored_watcher_path(Path::new("C:/Users/me/.DS_Store")));
    }

    #[test]
    fn ignored_watcher_path_matches_temporary_extensions_case_insensitively() {
        assert!(is_ignored_watcher_path(Path::new("download.CRDOWNLOAD")));
        assert!(is_ignored_watcher_path(Path::new("archive.part")));
        assert!(is_ignored_watcher_path(Path::new("draft.TMP")));
        assert!(!is_ignored_watcher_path(Path::new("real-file.txt")));
    }

    #[test]
    fn a_folder_has_one_key_however_its_path_is_written() {
        assert_eq!(coalesce_key("C:/Work/"), coalesce_key("C:/Work"));
        assert_eq!(coalesce_key(r"C:\Work\sub"), coalesce_key("C:/Work/sub"));
        // A drive's root keeps its slash: "C:" alone is another place.
        assert_eq!(coalesce_key(r"C:\"), coalesce_key("C:/"));
        assert_ne!(coalesce_key("C:/"), coalesce_key("C:"));
        #[cfg(windows)]
        assert_eq!(coalesce_key("C:/WORK"), coalesce_key("c:/work"));
    }
}

#[cfg(all(test, windows))]
mod live_tests;
