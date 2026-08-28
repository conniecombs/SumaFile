use notify::{Config, Event, RecommendedWatcher, RecursiveMode, Watcher};
use simplefile_core::models::FileChangeEvent;
use simplefile_core::utils::validate_existing_path_no_resolve;
use std::collections::HashMap;
use std::path::Path;
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

const WATCHER_DEBOUNCE: Duration = Duration::from_millis(500);
const WATCHER_COALESCE_FLUSH: Duration = Duration::from_millis(150);
const WATCHER_STORM_COLLAPSE_LIMIT: usize = 512;

#[derive(Default)]
pub struct WatcherState {
    watcher: Option<RecommendedWatcher>,
    coalescer: Option<WatcherCoalescer>,
    watched_path: Option<String>,
}

pub fn watch_directory<F>(path: String, state: &mut WatcherState, emit: F) -> Result<(), String>
where
    F: Fn(FileChangeEvent) + Send + Sync + 'static,
{
    let validated_path = validate_existing_path_no_resolve(&path)?;
    if !validated_path.is_dir() {
        return Err("Watch path must be a directory".to_string());
    }

    let coalescer = WatcherCoalescer::new(path.clone(), emit)?;
    let event_sender = coalescer.sender();
    let path_timestamps: Arc<Mutex<HashMap<String, Instant>>> =
        Arc::new(Mutex::new(HashMap::new()));

    let mut watcher = RecommendedWatcher::new(
        {
            let path_timestamps = path_timestamps.clone();
            move |res: Result<Event, notify::Error>| {
                let Ok(event) = res else {
                    return;
                };

                let kind = match event.kind {
                    notify::EventKind::Create(_) => "create",
                    notify::EventKind::Remove(_) => "remove",
                    notify::EventKind::Modify(kind) => match kind {
                        notify::event::ModifyKind::Name(_) => "rename",
                        notify::event::ModifyKind::Metadata(_) | notify::event::ModifyKind::Any => {
                            return
                        }
                        _ => "modify",
                    },
                    notify::EventKind::Access(_)
                    | notify::EventKind::Any
                    | notify::EventKind::Other => return,
                };

                let now = Instant::now();
                for path in event.paths {
                    let path_str = path.to_string_lossy().to_string();
                    if is_ignored_watcher_path(&path) {
                        continue;
                    }

                    {
                        let Ok(mut timestamps) = path_timestamps.lock() else {
                            continue;
                        };
                        if let Some(last) = timestamps.get(&path_str) {
                            if now.duration_since(*last) < WATCHER_DEBOUNCE {
                                continue;
                            }
                        }
                        timestamps.insert(path_str.clone(), now);
                        if timestamps.len() > 1000 {
                            let cutoff = debounce_eviction_cutoff(now);
                            timestamps.retain(|_, value| *value > cutoff);
                        }
                    }

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

    watcher
        .watch(validated_path.as_path(), RecursiveMode::NonRecursive)
        .map_err(|error| format!("Failed to watch directory: {error}"))?;

    unwatch_directory(state);
    state.watcher = Some(watcher);
    state.coalescer = Some(coalescer);
    state.watched_path = Some(path);
    Ok(())
}

pub fn unwatch_directory(state: &mut WatcherState) {
    state.watcher = None;
    state.coalescer = None;
    state.watched_path = None;
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

fn debounce_eviction_cutoff(now: Instant) -> Instant {
    now.checked_sub(Duration::from_secs(10)).unwrap_or(now)
}

struct WatcherCoalescer {
    sender: Option<mpsc::Sender<FileChangeEvent>>,
    worker: Option<thread::JoinHandle<()>>,
}

impl WatcherCoalescer {
    fn new<F>(watched_path: String, emit: F) -> Result<Self, String>
    where
        F: Fn(FileChangeEvent) + Send + Sync + 'static,
    {
        let emit = Arc::new(emit);
        let (sender, receiver) = mpsc::channel();
        let worker = thread::Builder::new()
            .name("simplefile-watch-coalescer".to_string())
            .spawn(move || coalesce_worker(watched_path, receiver, emit))
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

fn coalesce_worker<F>(watched_path: String, receiver: mpsc::Receiver<FileChangeEvent>, emit: Arc<F>)
where
    F: Fn(FileChangeEvent) + Send + Sync + 'static,
{
    let mut pending = HashMap::new();
    loop {
        match receiver.recv_timeout(WATCHER_COALESCE_FLUSH) {
            Ok(event) => {
                queue_coalesced_event(&mut pending, event, &watched_path);
                while let Ok(event) = receiver.try_recv() {
                    queue_coalesced_event(&mut pending, event, &watched_path);
                }
                if pending.len() >= WATCHER_STORM_COLLAPSE_LIMIT {
                    flush_coalesced_events(&mut pending, emit.as_ref());
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

fn queue_coalesced_event(
    pending: &mut HashMap<String, FileChangeEvent>,
    event: FileChangeEvent,
    watched_path: &str,
) {
    if pending.contains_key(watched_path) {
        return;
    }

    pending.insert(event.path.clone(), event);
    if pending.len() > WATCHER_STORM_COLLAPSE_LIMIT {
        pending.clear();
        pending.insert(
            watched_path.to_string(),
            FileChangeEvent {
                path: watched_path.to_string(),
                kind: "modify".to_string(),
            },
        );
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
        debounce_eviction_cutoff, flush_coalesced_events, is_ignored_watcher_path,
        queue_coalesced_event, WATCHER_STORM_COLLAPSE_LIMIT,
    };
    use simplefile_core::models::FileChangeEvent;
    use std::collections::HashMap;
    use std::path::Path;
    use std::sync::Mutex;
    use std::time::Instant;

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
    fn debounce_eviction_cutoff_never_panics() {
        let now = Instant::now();

        assert!(debounce_eviction_cutoff(now) <= now);
    }

    #[test]
    fn coalescer_keeps_latest_event_per_path() {
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
                path: "C:/Work/a.txt".to_string(),
                kind: "modify".to_string(),
            },
            "C:/Work",
        );

        let emitted = Mutex::new(Vec::new());
        flush_coalesced_events(&mut pending, &|event| emitted.lock().unwrap().push(event));
        let emitted = emitted.into_inner().unwrap();
        assert_eq!(emitted.len(), 1);
        assert_eq!(emitted[0].path, "C:/Work/a.txt");
        assert_eq!(emitted[0].kind, "modify");
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

        let event = pending.get("C:/Work").expect("collapsed refresh event");
        assert_eq!(event.kind, "modify");
        assert_eq!(pending.len(), 1);
    }
}
