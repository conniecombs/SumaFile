use notify::{Config, Event, RecommendedWatcher, RecursiveMode, Watcher};
use simplefile_core::models::FileChangeEvent;
use simplefile_core::utils::validate_existing_path_no_resolve;
use std::collections::HashMap;
use std::path::Path;
use std::sync::{mpsc, Arc};
use std::thread;
use std::time::{Duration, Instant};

const WATCHER_COALESCE_FLUSH: Duration = Duration::from_millis(150);
const WATCHER_MAX_BATCH_AGE: Duration = Duration::from_millis(500);
const WATCHER_STORM_COLLAPSE_LIMIT: usize = 512;

#[derive(Default)]
pub struct WatcherState {
    watcher: Option<RecommendedWatcher>,
    coalescer: Option<WatcherCoalescer>,
    watched_paths: Vec<String>,
}

pub fn watch_directory<F>(path: String, state: &mut WatcherState, emit: F) -> Result<(), String>
where
    F: Fn(FileChangeEvent) + Send + Sync + 'static,
{
    watch_directories(vec![path], state, emit)
}

pub fn watch_directories<F>(
    paths: Vec<String>,
    state: &mut WatcherState,
    emit: F,
) -> Result<(), String>
where
    F: Fn(FileChangeEvent) + Send + Sync + 'static,
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

    if unique_paths.is_empty() {
        unwatch_directory(state);
        return Ok(());
    }

    let mut validated_paths = Vec::with_capacity(unique_paths.len());
    for path in &unique_paths {
        let validated = validate_existing_path_no_resolve(path)?;
        if !validated.is_dir() {
            return Err("Watch path must be a directory".to_string());
        }
        validated_paths.push(validated);
    }

    let coalescer = WatcherCoalescer::new(unique_paths.clone(), emit)?;
    let event_sender = coalescer.sender();

    let mut watcher = RecommendedWatcher::new(
        {
            move |res: Result<Event, notify::Error>| {
                let Ok(event) = res else {
                    return;
                };

                let Some(kind) = classify_notify_event(&event.kind) else {
                    return;
                };

                for path in event.paths {
                    if is_ignored_watcher_path(&path) {
                        continue;
                    }
                    let path_str = path.to_string_lossy().to_string();
                    let _ = event_sender.send(FileChangeEvent {
                        path: path_str,
                        kind: kind.to_string(),
                    });
                }
            }
        },
        Config::default(),
    )
    .map_err(|error| format!("Failed to create watcher: {error}"))?;

    for validated in &validated_paths {
        watcher
            .watch(validated.as_path(), RecursiveMode::NonRecursive)
            .map_err(|error| format!("Failed to watch directory: {error}"))?;
    }

    unwatch_directory(state);
    state.watcher = Some(watcher);
    state.coalescer = Some(coalescer);
    state.watched_paths = unique_paths;
    Ok(())
}

pub fn unwatch_directory(state: &mut WatcherState) {
    state.watcher = None;
    state.coalescer = None;
    state.watched_paths.clear();
}

fn classify_notify_event(kind: &notify::EventKind) -> Option<&'static str> {
    match kind {
        notify::EventKind::Create(_) => Some("create"),
        notify::EventKind::Remove(_) => Some("remove"),
        notify::EventKind::Modify(modify_kind) => match modify_kind {
            notify::event::ModifyKind::Name(_) => Some("rename"),
            // On Windows, ReadDirectoryChangesW emits FILE_ACTION_MODIFIED as ModifyKind::Any.
            notify::event::ModifyKind::Data(_)
            | notify::event::ModifyKind::Metadata(_)
            | notify::event::ModifyKind::Any
            | notify::event::ModifyKind::Other => Some("modify"),
        },
        notify::EventKind::Access(_) | notify::EventKind::Any | notify::EventKind::Other => None,
    }
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

struct WatcherCoalescer {
    sender: Option<mpsc::Sender<FileChangeEvent>>,
    worker: Option<thread::JoinHandle<()>>,
}

impl WatcherCoalescer {
    fn new<F>(watched_paths: Vec<String>, emit: F) -> Result<Self, String>
    where
        F: Fn(FileChangeEvent) + Send + Sync + 'static,
    {
        let emit = Arc::new(emit);
        let (sender, receiver) = mpsc::channel();
        let worker = thread::Builder::new()
            .name("simplefile-watch-coalescer".to_string())
            .spawn(move || coalesce_worker(watched_paths, receiver, emit))
            .map_err(|error| format!("Failed to start watcher coalescer: {error}"))?;
        Ok(Self {
            sender: Some(sender),
            worker: Some(worker),
        })
    }

    fn sender(&self) -> mpsc::Sender<FileChangeEvent> {
        self.sender.as_ref().expect("coalescer sender").clone()
    }
}

impl Drop for WatcherCoalescer {
    fn drop(&mut self) {
        self.sender.take();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn coalesce_worker<F>(
    watched_paths: Vec<String>,
    receiver: mpsc::Receiver<FileChangeEvent>,
    emit: Arc<F>,
) where
    F: Fn(FileChangeEvent) + Send + Sync + 'static,
{
    let mut pending = HashMap::new();
    let mut batch_started: Option<Instant> = None;
    let mut quiet_deadline: Option<Instant> = None;

    loop {
        if pending.is_empty() {
            match receiver.recv() {
                Ok(event) => {
                    let now = Instant::now();
                    batch_started = Some(now);
                    quiet_deadline = Some(now + WATCHER_COALESCE_FLUSH);
                    queue_coalesced_events(&mut pending, event, &watched_paths);
                    while let Ok(next) = receiver.try_recv() {
                        queue_coalesced_events(&mut pending, next, &watched_paths);
                    }
                    if pending.len() >= WATCHER_STORM_COLLAPSE_LIMIT {
                        flush_coalesced_events(&mut pending, emit.as_ref());
                    }
                }
                Err(_) => break,
            }
            continue;
        }

        let now = Instant::now();
        let max_deadline = batch_started.expect("batch started") + WATCHER_MAX_BATCH_AGE;
        let next_quiet = quiet_deadline.expect("quiet deadline");
        let deadline = next_quiet.min(max_deadline);
        if now >= deadline {
            flush_coalesced_events(&mut pending, emit.as_ref());
            continue;
        }

        let wait = deadline.saturating_duration_since(now);
        match receiver.recv_timeout(wait) {
            Ok(event) => {
                queue_coalesced_events(&mut pending, event, &watched_paths);
                while let Ok(next) = receiver.try_recv() {
                    queue_coalesced_events(&mut pending, next, &watched_paths);
                }
                let after = Instant::now();
                if after >= max_deadline || pending.len() >= WATCHER_STORM_COLLAPSE_LIMIT {
                    flush_coalesced_events(&mut pending, emit.as_ref());
                } else {
                    quiet_deadline = Some(after + WATCHER_COALESCE_FLUSH);
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                flush_coalesced_events(&mut pending, emit.as_ref());
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                flush_coalesced_events(&mut pending, emit.as_ref());
                break;
            }
        }
    }
}

#[cfg(test)]
fn queue_coalesced_event(
    pending: &mut HashMap<String, FileChangeEvent>,
    event: FileChangeEvent,
    watched_path: &str,
) {
    queue_coalesced_events(pending, event, &[watched_path.to_string()]);
}

fn matching_watched_root<'a>(event_path: &str, watched_paths: &'a [String]) -> Option<&'a str> {
    let event_key = coalesce_key(event_path);
    let parent_key = Path::new(event_path)
        .parent()
        .map(|parent| coalesce_key(&parent.to_string_lossy()));
    for root in watched_paths {
        let root_key = coalesce_key(root);
        if event_key == root_key || parent_key.as_deref() == Some(root_key.as_str()) {
            return Some(root.as_str());
        }
    }
    watched_paths.first().map(String::as_str)
}

fn queue_coalesced_events(
    pending: &mut HashMap<String, FileChangeEvent>,
    event: FileChangeEvent,
    watched_paths: &[String],
) {
    if let Some(root) = matching_watched_root(&event.path, watched_paths) {
        let root_key = coalesce_key(root);
        if pending.contains_key(&root_key) {
            return;
        }
    }

    pending.insert(coalesce_key(&event.path), event);
    if pending.len() > WATCHER_STORM_COLLAPSE_LIMIT {
        pending.clear();
        for root in watched_paths {
            pending.insert(
                coalesce_key(root),
                FileChangeEvent {
                    path: root.clone(),
                    kind: "modify".to_string(),
                },
            );
        }
    }
}

fn flush_coalesced_events<F>(pending: &mut HashMap<String, FileChangeEvent>, emit: &F)
where
    F: Fn(FileChangeEvent),
{
    let mut events: Vec<_> = pending.drain().map(|(_, event)| event).collect();
    events.sort_by(|left, right| left.path.cmp(&right.path));
    for event in events {
        emit(event);
    }
}

#[cfg(test)]
mod tests {
    use super::{
        classify_notify_event, coalesce_key, flush_coalesced_events, is_ignored_watcher_path,
        queue_coalesced_event, WATCHER_STORM_COLLAPSE_LIMIT,
    };
    use simplefile_core::models::FileChangeEvent;
    use std::collections::HashMap;
    use std::path::Path;
    use std::sync::Mutex;

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
    fn windows_modify_any_and_metadata_events_map_to_modify() {
        assert_eq!(
            Some("modify"),
            classify_notify_event(&notify::EventKind::Modify(notify::event::ModifyKind::Any))
        );
        assert_eq!(
            Some("modify"),
            classify_notify_event(&notify::EventKind::Modify(
                notify::event::ModifyKind::Metadata(notify::event::MetadataKind::Any)
            ))
        );
    }

    #[test]
    fn coalescer_keeps_latest_event_per_path_case_insensitively_on_windows() {
        let mut pending = HashMap::new();
        queue_coalesced_event(
            &mut pending,
            FileChangeEvent {
                path: "C:/Work/a.txt".to_string(),
                kind: "create".to_string(),
            },
            "C:/Work",
        );
        queue_coalesced_event(
            &mut pending,
            FileChangeEvent {
                path: "c:\\work\\A.TXT".to_string(),
                kind: "modify".to_string(),
            },
            "C:/Work",
        );

        let emitted = Mutex::new(Vec::new());
        flush_coalesced_events(&mut pending, &|event| emitted.lock().unwrap().push(event));
        let emitted = emitted.into_inner().unwrap();
        #[cfg(windows)]
        {
            assert_eq!(emitted.len(), 1);
            assert_eq!(emitted[0].kind, "modify");
        }
        #[cfg(not(windows))]
        assert!(!emitted.is_empty());
    }

    #[test]
    fn coalescer_collapses_large_storm_to_watched_path() {
        let mut pending = HashMap::new();
        for index in 0..=WATCHER_STORM_COLLAPSE_LIMIT {
            queue_coalesced_event(
                &mut pending,
                FileChangeEvent {
                    path: format!("C:/Work/{index}.txt"),
                    kind: "modify".to_string(),
                },
                "C:/Work",
            );
        }

        let event = pending
            .get(&coalesce_key("C:/Work"))
            .expect("collapsed refresh event");
        assert_eq!(event.path, "C:/Work");
        assert_eq!(event.kind, "modify");
        assert_eq!(pending.len(), 1);
    }
}

#[cfg(test)]
mod live_tests {
    use super::{unwatch_directory, watch_directories, watch_directory, WatcherState};
    use std::sync::{Arc, Mutex};
    use std::time::{Duration, Instant};

    /// A file created and removed within the old 500 ms debounce window must
    /// still produce the removal: otherwise the UI shows a ghost entry.
    #[test]
    fn remove_shortly_after_create_is_not_dropped() {
        let dir = std::env::temp_dir().join(format!(
            "sumafile-watch-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let events = Arc::new(Mutex::new(Vec::new()));
        let sink = events.clone();
        let mut state = WatcherState::default();
        watch_directory(
            dir.to_string_lossy().to_string(),
            &mut state,
            move |change| {
                sink.lock().unwrap().push(change.clone());
            },
        )
        .expect("watch temp dir");
        std::thread::sleep(Duration::from_millis(200));

        let file = dir.join("flash.txt");
        std::fs::write(&file, b"x").unwrap();
        std::thread::sleep(Duration::from_millis(50));
        std::fs::remove_file(&file).unwrap();

        let deadline = Instant::now() + Duration::from_secs(3);
        let mut saw_remove = false;
        while Instant::now() < deadline && !saw_remove {
            std::thread::sleep(Duration::from_millis(50));
            saw_remove = events
                .lock()
                .unwrap()
                .iter()
                .any(|event| event.kind == "remove" && event.path.ends_with("flash.txt"));
        }
        unwatch_directory(&mut state);
        let _ = std::fs::remove_dir_all(&dir);
        assert!(saw_remove, "events seen: {:?}", events.lock().unwrap());
    }

    #[test]
    fn in_place_file_modification_and_multi_root_watches_emit_events() {
        let base = std::env::temp_dir().join(format!(
            "sumafile-watch-dual-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let left = base.join("left");
        let right = base.join("right");
        std::fs::create_dir_all(&left).unwrap();
        std::fs::create_dir_all(&right).unwrap();
        let existing = left.join("notes.txt");
        std::fs::write(&existing, b"v1").unwrap();

        let events = Arc::new(Mutex::new(Vec::new()));
        let sink = events.clone();
        let mut state = WatcherState::default();
        watch_directories(
            vec![
                left.to_string_lossy().to_string(),
                right.to_string_lossy().to_string(),
            ],
            &mut state,
            move |change| {
                sink.lock().unwrap().push(change);
            },
        )
        .expect("watch dual dirs");
        std::thread::sleep(Duration::from_millis(200));

        // Modify existing file in-place in left pane AND create file in right pane.
        std::fs::write(&existing, b"v2-longer").unwrap();
        std::fs::write(right.join("created.txt"), b"hello").unwrap();

        let deadline = Instant::now() + Duration::from_secs(3);
        let mut saw_both = false;
        while Instant::now() < deadline && !saw_both {
            std::thread::sleep(Duration::from_millis(50));
            let guard = events.lock().unwrap();
            let saw_modify = guard
                .iter()
                .any(|e| e.kind == "modify" && e.path.ends_with("notes.txt"));
            let saw_right = guard.iter().any(|e| e.path.ends_with("created.txt"));
            saw_both = saw_modify && saw_right;
        }
        unwatch_directory(&mut state);
        let _ = std::fs::remove_dir_all(&base);
        assert!(saw_both, "events seen: {:?}", events.lock().unwrap());
    }
}
