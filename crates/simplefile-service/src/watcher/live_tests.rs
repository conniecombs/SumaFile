//! The watcher against real folders.

use super::{
    unwatch_directory, watch_directories, watch_directory, EntryNow, WatchChange, WatcherState,
};
use simplefile_core::models::FileChangeEvent;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

type Seen = Arc<Mutex<Vec<WatchChange>>>;

/// A folder of its own under the temporary folder, removed when dropped.
struct Scratch(PathBuf);

impl Scratch {
    fn new(tag: &str) -> Self {
        let dir = std::env::temp_dir().join(format!(
            "sumafile-watch-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    fn path(&self) -> &Path {
        &self.0
    }

    fn dir(&self, name: &str) -> PathBuf {
        let dir = self.0.join(name);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }
}

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn text(path: &Path) -> String {
    path.to_string_lossy().to_string()
}

/// Watches `folders` and collects what is told.
fn watch(state: &mut WatcherState, folders: &[&Path]) -> Seen {
    let seen: Seen = Arc::default();
    let sink = seen.clone();
    watch_directories(
        folders.iter().map(|folder| text(folder)).collect(),
        state,
        move |change| sink.lock().unwrap().push(change),
    )
    .expect("the folders can be watched");
    seen
}

/// Waits until a change that `matches` was told; the changes told so far otherwise.
fn wait_for(seen: &Seen, matches: impl Fn(&WatchChange) -> bool) -> Result<WatchChange, String> {
    let deadline = Instant::now() + Duration::from_secs(8);
    loop {
        if let Some(found) = seen.lock().unwrap().iter().find(|change| matches(change)) {
            return Ok(found.clone());
        }
        if Instant::now() >= deadline {
            return Err(format!("changes told: {:#?}", seen.lock().unwrap()));
        }
        std::thread::sleep(Duration::from_millis(25));
    }
}

fn named<'a>(name: &'a str, kind: &'a str) -> impl Fn(&WatchChange) -> bool + 'a {
    move |change| change.event.kind == kind && change.event.path.ends_with(name)
}

/// Long enough for a batch that was pending to have been told.
fn settle() {
    std::thread::sleep(Duration::from_millis(900));
}

/// A file created and removed within the old 500 ms debounce window must
/// still produce the removal: otherwise the UI shows a ghost entry.
#[test]
fn remove_shortly_after_create_is_not_dropped() {
    let scratch = Scratch::new("flash");
    let mut state = WatcherState::default();
    let seen: Seen = Arc::default();
    let sink = seen.clone();
    watch_directory(text(scratch.path()), &mut state, move |change| {
        sink.lock().unwrap().push(change)
    })
    .expect("watch temp dir");

    let file = scratch.path().join("flash.txt");
    std::fs::write(&file, b"x").unwrap();
    std::thread::sleep(Duration::from_millis(50));
    std::fs::remove_file(&file).unwrap();

    let removal = wait_for(&seen, named("flash.txt", "remove")).unwrap();
    assert!(matches!(removal.now, EntryNow::Gone));
}

#[test]
fn in_place_file_modification_and_multi_root_watches_emit_events() {
    let scratch = Scratch::new("dual");
    let left = scratch.dir("left");
    let right = scratch.dir("right");
    let existing = left.join("notes.txt");
    std::fs::write(&existing, b"v1").unwrap();

    let mut state = WatcherState::default();
    let seen = watch(&mut state, &[&left, &right]);

    // Modify existing file in-place in left pane AND create file in right pane.
    std::fs::write(&existing, b"v2-longer").unwrap();
    std::fs::write(right.join("created.txt"), b"hello").unwrap();

    wait_for(&seen, named("notes.txt", "modify")).unwrap();
    wait_for(&seen, |change| change.event.path.ends_with("created.txt")).unwrap();
}

/// The extended change records say what an entry is now. What they say must
/// be what reading the entry would say, or the rows shown drift from the
/// disk.
#[test]
fn a_change_carries_the_entry_as_the_disk_has_it() {
    let scratch = Scratch::new("facts");
    let mut state = WatcherState::default();
    let seen = watch(&mut state, &[scratch.path()]);

    // Written in pieces through one handle, then closed.
    let grown = scratch.path().join("grown.bin");
    {
        use std::io::Write;
        let mut file = std::fs::File::create(&grown).unwrap();
        for _ in 0..4 {
            file.write_all(&[7u8; 300_000]).unwrap();
            file.flush().unwrap();
            std::thread::sleep(Duration::from_millis(60));
        }
    }
    // A copy, which sets the dates of its source at the end.
    let copied = scratch.path().join("copied.bin");
    std::fs::copy(&grown, &copied).unwrap();
    // A folder, and a file made read-only and hidden.
    let folder = scratch.path().join("Folder");
    std::fs::create_dir(&folder).unwrap();
    let marked = scratch.path().join("marked.txt");
    std::fs::write(&marked, b"read-only").unwrap();
    let status = std::process::Command::new("attrib")
        .args(["+R", "+H"])
        .arg(&marked)
        .status()
        .unwrap();
    assert!(status.success());
    settle();

    for path in [&grown, &copied, &folder, &marked] {
        let name = path.file_name().unwrap().to_string_lossy().to_string();
        let last = seen
            .lock()
            .unwrap()
            .iter()
            .rev()
            .find(|change| change.event.path.ends_with(&name))
            .cloned()
            .unwrap_or_else(|| panic!("no change was told for {name}"));
        let EntryNow::Is(told) = last.now else {
            panic!("the change of {name} does not say what it is: {last:?}");
        };
        let read = simplefile_core::file_ops::get_entry_info_simple(&text(path)).unwrap();
        assert_eq!(told.name, read.name);
        assert_eq!(told.path, read.path);
        assert_eq!(told.is_dir, read.is_dir, "{name}");
        assert_eq!(told.size, read.size, "{name}");
        assert_eq!(told.modified, read.modified, "{name}");
        assert_eq!(told.created, read.created, "{name}");
        assert_eq!(told.extension, read.extension, "{name}");
        assert_eq!(told.attributes, read.attributes, "{name}");
        assert_eq!(told.is_hidden, read.is_hidden, "{name}");
        assert_eq!(told.is_system, read.is_system, "{name}");
        assert_eq!(told.is_symlink, read.is_symlink, "{name}");
    }
    // Clear the attribute so the folder can be removed.
    let _ = std::process::Command::new("attrib")
        .args(["-R", "-H"])
        .arg(&marked)
        .status();
}

/// On a file system without the extended records the changes are the same,
/// but do not say what the entry is: it is read when the change is applied.
#[test]
fn plain_records_tell_the_same_changes_without_the_entry() {
    let scratch = Scratch::new("plain");
    let mut state = WatcherState::with_plain_records();
    let seen = watch(&mut state, &[scratch.path()]);

    let file = scratch.path().join("a.txt");
    std::fs::write(&file, b"one").unwrap();
    let created = wait_for(&seen, |change| change.event.path.ends_with("a.txt")).unwrap();
    assert!(matches!(created.now, EntryNow::Unknown), "{created:?}");
    settle();

    std::fs::rename(&file, scratch.path().join("b.txt")).unwrap();
    let renamed = wait_for(&seen, named("b.txt", "rename")).unwrap();
    assert_eq!(renamed.renamed_from.as_deref(), Some(text(&file).as_str()));
    assert!(matches!(renamed.now, EntryNow::Unknown));
    settle();

    std::fs::remove_file(scratch.path().join("b.txt")).unwrap();
    let removed = wait_for(&seen, named("b.txt", "remove")).unwrap();
    assert!(matches!(removed.now, EntryNow::Gone));
}

#[test]
fn a_rename_is_one_change_that_names_both_ends_and_what_the_entry_is() {
    let scratch = Scratch::new("rename");
    let before = scratch.path().join("before.txt");
    std::fs::write(&before, b"content").unwrap();
    let mut state = WatcherState::default();
    let seen = watch(&mut state, &[scratch.path()]);

    let after = scratch.path().join("after.txt");
    std::fs::rename(&before, &after).unwrap();

    let renamed = wait_for(&seen, named("after.txt", "rename")).unwrap();
    assert_eq!(
        renamed.renamed_from.as_deref(),
        Some(text(&before).as_str())
    );
    let EntryNow::Is(entry) = &renamed.now else {
        panic!("the rename does not say what the entry is: {renamed:?}");
    };
    assert_eq!((entry.name.as_str(), entry.size), ("after.txt", 7));
    // The old name is told too, after the rename, as gone.
    let seen_now = seen.lock().unwrap();
    let rename_at = seen_now
        .iter()
        .position(|change| change.event.path.ends_with("after.txt"))
        .unwrap();
    let old_at = seen_now
        .iter()
        .position(|change| change.event.path.ends_with("before.txt"))
        .expect("the old name is told");
    assert!(rename_at < old_at);
    assert!(matches!(seen_now[old_at].now, EntryNow::Gone));
    assert_eq!(seen_now[old_at].event.kind, "rename");
}

#[test]
fn a_watched_folder_that_is_deleted_is_told_as_gone() {
    let scratch = Scratch::new("deleted");
    let watched = scratch.dir("watched");
    let other = scratch.dir("other");
    let mut state = WatcherState::default();
    let seen = watch(&mut state, &[&watched, &other]);

    std::fs::remove_dir(&watched).unwrap();
    let gone = wait_for(&seen, |change| change.event.kind == FileChangeEvent::GONE).unwrap();
    assert_eq!(gone.event.path, text(&watched));

    // The other folder is still watched.
    std::fs::write(other.join("still.txt"), b"x").unwrap();
    wait_for(&seen, |change| change.event.path.ends_with("still.txt")).unwrap();
    // And the folder can be watched again once it is back.
    std::fs::create_dir(&watched).unwrap();
    let seen = watch(&mut state, &[&watched]);
    std::fs::write(watched.join("back.txt"), b"x").unwrap();
    wait_for(&seen, |change| change.event.path.ends_with("back.txt")).unwrap();
}

/// A renamed folder keeps its handle and reports nothing about itself: the
/// watcher has to notice that the path no longer names it.
#[test]
fn a_watched_folder_that_is_renamed_or_moved_is_told_as_gone() {
    let scratch = Scratch::new("moved");
    let renamed = scratch.dir("renamed");
    let moved = scratch.dir("moved");
    let elsewhere = scratch.dir("elsewhere");
    let mut state = WatcherState::default();
    let seen = watch(&mut state, &[&renamed, &moved]);

    std::fs::rename(&renamed, scratch.path().join("renamed-to")).unwrap();
    std::fs::rename(&moved, elsewhere.join("moved")).unwrap();

    for folder in [&renamed, &moved] {
        let path = text(folder);
        wait_for(&seen, |change| {
            change.event.kind == FileChangeEvent::GONE && change.event.path == path
        })
        .unwrap();
    }
    // A folder that only changed the case of its letters is still at its path.
    let cased = scratch.dir("cased");
    let seen = watch(&mut state, &[&cased]);
    std::fs::rename(&cased, scratch.path().join("CASED")).unwrap();
    std::thread::sleep(Duration::from_secs(5));
    assert!(
        !seen
            .lock()
            .unwrap()
            .iter()
            .any(|change| change.event.kind == FileChangeEvent::GONE),
        "{:?}",
        seen.lock().unwrap()
    );
    std::fs::write(scratch.path().join("CASED").join("in.txt"), b"x").unwrap();
    let told = wait_for(&seen, |change| change.event.path.ends_with("in.txt")).unwrap();
    // Told under the path the host named.
    assert_eq!(told.event.path, text(&cased.join("in.txt")));
}

/// More changes than the system's buffer for a folder holds: the system
/// drops them and says so. That folder, and only that folder, is to be read
/// again; the watch goes on.
#[test]
fn a_buffer_that_runs_over_asks_for_that_folder_to_be_read_again() {
    let scratch = Scratch::new("overflow");
    let busy = scratch.dir("busy");
    let calm = scratch.dir("calm");

    // The sink holds the watcher's thread at the first change, so nothing
    // is read while the files below are created.
    let seen: Seen = Arc::default();
    let gate = Arc::new((Mutex::new(false), Condvar::new()));
    let mut state = WatcherState::default();
    {
        let sink = seen.clone();
        let gate = gate.clone();
        watch_directories(vec![text(&busy), text(&calm)], &mut state, move |change| {
            sink.lock().unwrap().push(change);
            let (open, opened) = &*gate;
            let mut open = open.lock().unwrap();
            while !*open {
                open = opened.wait(open).unwrap();
            }
        })
        .unwrap();
    }
    std::fs::write(busy.join("first.txt"), b"x").unwrap();
    wait_for(&seen, |change| change.event.path.ends_with("first.txt")).unwrap();

    // Each record is some 280 bytes: 1,200 of them are five times the buffer.
    let long = "n".repeat(96);
    for index in 0..1200 {
        std::fs::write(busy.join(format!("{long}-{index:04}")), b"").unwrap();
    }
    std::fs::write(calm.join("quiet.txt"), b"x").unwrap();
    {
        let (open, opened) = &*gate;
        *open.lock().unwrap() = true;
        opened.notify_all();
    }

    let busy_path = text(&busy);
    wait_for(&seen, |change| {
        change.event.kind == "modify" && change.event.path == busy_path
    })
    .unwrap();
    wait_for(&seen, |change| change.event.path.ends_with("quiet.txt")).unwrap();
    settle();
    let calm_path = text(&calm);
    assert!(!seen
        .lock()
        .unwrap()
        .iter()
        .any(|change| change.event.path == calm_path));

    // The busy folder is still watched after it ran over.
    std::fs::write(busy.join("after.txt"), b"x").unwrap();
    wait_for(&seen, |change| change.event.path.ends_with("after.txt")).unwrap();
}

#[test]
fn watching_again_keeps_what_is_named_and_lets_the_rest_go() {
    let scratch = Scratch::new("again");
    let (a, b, c) = (scratch.dir("a"), scratch.dir("b"), scratch.dir("c"));
    let mut state = WatcherState::default();
    let first = watch(&mut state, &[&a, &b]);
    let second = watch(&mut state, &[&b, &c]);

    std::fs::write(a.join("in-a.txt"), b"x").unwrap();
    std::fs::write(b.join("in-b.txt"), b"x").unwrap();
    std::fs::write(c.join("in-c.txt"), b"x").unwrap();
    wait_for(&second, |change| change.event.path.ends_with("in-b.txt")).unwrap();
    wait_for(&second, |change| change.event.path.ends_with("in-c.txt")).unwrap();
    settle();
    let told = |seen: &Seen, name: &str| {
        seen.lock()
            .unwrap()
            .iter()
            .any(|change| change.event.path.ends_with(name))
    };
    assert!(
        !told(&second, "in-a.txt"),
        "a folder that was let go is still told"
    );
    // The sink of the earlier request hears nothing more.
    assert!(!told(&first, "in-b.txt") && !told(&first, "in-a.txt"));
}

#[test]
fn a_folder_that_cannot_be_watched_fails_the_request_and_leaves_the_watch_as_it_was() {
    let scratch = Scratch::new("refused");
    let watched = scratch.dir("watched");
    let file = scratch.path().join("a-file.txt");
    std::fs::write(&file, b"x").unwrap();
    let mut state = WatcherState::default();
    let seen = watch(&mut state, &[&watched]);

    let missing = watch_directories(
        vec![text(&watched), text(&scratch.path().join("missing"))],
        &mut state,
        |_| {},
    );
    assert!(missing.is_err());
    let not_a_folder = watch_directories(vec![text(&file)], &mut state, |_| {});
    assert_eq!(not_a_folder.unwrap_err(), "Watch path must be a directory");

    std::fs::write(watched.join("still.txt"), b"x").unwrap();
    wait_for(&seen, |change| change.event.path.ends_with("still.txt")).unwrap();
}

#[test]
fn unwatching_and_dropping_the_watcher_stop_the_reports_and_free_the_sink() {
    let scratch = Scratch::new("drop");
    let mut state = WatcherState::default();
    let seen = watch(&mut state, &[scratch.path()]);
    // Nothing is watched, and nothing fails, when no folder is named.
    watch_directories(vec![String::new(), "  ".to_string()], &mut state, |_| {}).unwrap();
    std::fs::write(scratch.path().join("unseen.txt"), b"x").unwrap();
    settle();
    assert!(
        seen.lock().unwrap().is_empty(),
        "{:?}",
        seen.lock().unwrap()
    );

    let seen = watch(&mut state, &[scratch.path()]);
    unwatch_directory(&mut state);
    drop(state);
    // The thread ends on its own and lets go of the sink, which held `seen`.
    let deadline = Instant::now() + Duration::from_secs(5);
    while Arc::strong_count(&seen) > 1 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(25));
    }
    assert_eq!(Arc::strong_count(&seen), 1);

    // A watcher that never watched anything has no thread to end.
    let mut idle = WatcherState::default();
    unwatch_directory(&mut idle);
    watch_directories(Vec::new(), &mut idle, |_| {}).unwrap();
}
