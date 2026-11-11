#![cfg(windows)]

//! Drives the bundled `third_party/7zip/win-x64/7za.exe` through the
//! library. These tests fail, never skip, when that binary is missing.

use std::collections::{BTreeSet, HashMap};
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, RwLock, RwLockReadGuard, RwLockWriteGuard};
use std::time::{Duration, Instant};

use serde_json::json;
use sumafile_plugin::{HostClient, PluginHandler};
use sumafile_sevenzip_backend::entries::SevenZipEntry;
use sumafile_sevenzip_backend::handler::SevenZipHandler;
use sumafile_sevenzip_backend::ops::{Limits, SevenZip};
use sumafile_sevenzip_backend::run::{spawn_count, RunObserver};

/// The test that counts spawns takes this for writing; every other test
/// takes it for reading, so no other spawn lands inside its count.
static SPAWNS: RwLock<()> = RwLock::new(());

fn shared() -> RwLockReadGuard<'static, ()> {
    SPAWNS
        .read()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn exclusive() -> RwLockWriteGuard<'static, ()> {
    SPAWNS
        .write()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn bundled_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("third_party")
        .join("7zip")
        .join("win-x64")
}

fn seven_za() -> PathBuf {
    let binary = bundled_dir().join("7za.exe");
    assert!(
        binary.is_file(),
        "the bundled 7za.exe is missing: {}",
        binary.display()
    );
    binary
}

fn engine() -> SevenZip {
    SevenZip::new(seven_za())
}

/// Runs the bundled 7za directly, for fixtures the library cannot make.
fn cli(args: &[&OsString]) {
    let output = std::process::Command::new(seven_za())
        .args(args)
        .stdin(std::process::Stdio::null())
        .output()
        .unwrap();
    assert!(
        output.status.code().unwrap_or(2) <= 1,
        "7za failed: {}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

struct Quiet;

impl RunObserver for Quiet {
    fn progress(&mut self, _percent: u8, _item: Option<&str>) {}
    fn is_cancelled(&self) -> bool {
        false
    }
}

#[derive(Default)]
struct Recording {
    reports: Vec<(u8, Option<String>)>,
    totals: Vec<u64>,
}

impl RunObserver for Recording {
    fn total(&mut self, bytes: u64) {
        self.totals.push(bytes);
    }
    fn progress(&mut self, percent: u8, item: Option<&str>) {
        self.reports.push((percent, item.map(str::to_string)));
    }
    fn is_cancelled(&self) -> bool {
        false
    }
}

fn write(path: &Path, content: impl AsRef<[u8]>) {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).unwrap();
    }
    std::fs::write(path, content).unwrap();
}

/// Pseudo-random bytes, so 7-Zip has real work to do.
fn noise(len: usize) -> Vec<u8> {
    let mut state = 0x9E37_79B9_7F4A_7C15u64;
    (0..len)
        .map(|_| {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state as u8
        })
        .collect()
}

/// `/`-separated listed paths.
fn paths(entries: &[SevenZipEntry]) -> BTreeSet<String> {
    entries.iter().map(|e| e.path.replace('\\', "/")).collect()
}

fn listed(archive: &Path) -> BTreeSet<String> {
    paths(&engine().list(archive).unwrap())
}

fn set(items: &[&str]) -> BTreeSet<String> {
    items.iter().map(|s| s.to_string()).collect()
}

fn is_empty_dir(path: &Path) -> bool {
    std::fs::read_dir(path).unwrap().next().is_none()
}

/// A `.7z` holding `docs/a.txt`, `docs/sub/b.txt` and `docs2/c.txt`.
fn docs_archive(root: &Path) -> PathBuf {
    let source = root.join("src");
    write(&source.join("docs").join("a.txt"), "alpha");
    write(&source.join("docs").join("sub").join("b.txt"), "beta");
    write(&source.join("docs2").join("c.txt"), "gamma");
    let archive = root.join("docs.7z");
    engine()
        .create(
            &archive,
            &[source.join("docs"), source.join("docs2")],
            &mut Quiet,
        )
        .unwrap();
    archive
}

#[test]
fn round_trip_create_list_extract_preserves_tree() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path();
    let alpha = root.join("alpha.txt");
    write(&alpha, "hello");
    let folder = root.join("folder");
    write(&folder.join("beta.txt"), "world");

    let archive = root.join("sample.7z");
    engine()
        .create(&archive, &[alpha, folder], &mut Quiet)
        .unwrap();

    let entries = engine().list(&archive).unwrap();
    assert_eq!(
        paths(&entries),
        set(&["alpha.txt", "folder", "folder/beta.txt"])
    );
    let folder_entry = entries.iter().find(|e| e.path == "folder").unwrap();
    assert!(folder_entry.is_dir);
    let alpha_entry = entries.iter().find(|e| e.path == "alpha.txt").unwrap();
    assert_eq!(alpha_entry.size, 5);

    let out = root.join("out");
    std::fs::create_dir_all(&out).unwrap();
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(std::fs::read(out.join("alpha.txt")).unwrap(), b"hello");
    assert_eq!(
        std::fs::read(out.join("folder").join("beta.txt")).unwrap(),
        b"world"
    );
}

/// Extraction never launches one 7-Zip process per entry: for a solid
/// archive that re-decompresses the whole block for every file.
#[test]
fn extract_uses_a_bounded_number_of_7za_processes() {
    let _spawns = exclusive();
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("src");
    for index in 0..6 {
        write(
            &source.join(format!("file{index}.txt")),
            format!("payload {index}"),
        );
    }
    write(&source.join("nested").join("deep.txt"), "deep");
    let archive = dir.path().join("batch.7z");
    engine()
        .create(&archive, std::slice::from_ref(&source), &mut Quiet)
        .unwrap();

    let out = dir.path().join("out");
    let before = spawn_count();
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    let spawned = spawn_count() - before;

    assert_eq!(
        std::fs::read(out.join("src").join("file3.txt")).unwrap(),
        b"payload 3"
    );
    assert_eq!(
        std::fs::read(out.join("src").join("nested").join("deep.txt")).unwrap(),
        b"deep"
    );
    assert!(
        spawned <= 2,
        "extracting 7 entries launched {spawned} 7-Zip processes"
    );
}

#[test]
fn extract_refuses_a_non_empty_destination() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = docs_archive(dir.path());
    let out = dir.path().join("out");
    write(&out.join("existing.txt"), "keep");

    let error = engine()
        .extract_all(&archive, &out, &mut Quiet)
        .unwrap_err();

    assert_eq!(
        error,
        format!("Destination is not empty: {}", out.display())
    );
    let names: Vec<_> = std::fs::read_dir(&out)
        .unwrap()
        .map(|e| e.unwrap().file_name())
        .collect();
    assert_eq!(names, vec![OsString::from("existing.txt")]);
}

#[test]
fn read_entry_selects_by_segment_prefix() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = docs_archive(dir.path());
    let out = dir.path().join("read");
    std::fs::create_dir_all(&out).unwrap();
    let limits = Limits {
        max_bytes: 1 << 20,
        max_entries: 16,
    };

    engine()
        .read_entry(&archive, "docs", &out, limits, &mut Quiet)
        .unwrap();

    assert_eq!(
        std::fs::read(out.join("docs").join("a.txt")).unwrap(),
        b"alpha"
    );
    assert_eq!(
        std::fs::read(out.join("docs").join("sub").join("b.txt")).unwrap(),
        b"beta"
    );
    assert!(!out.join("docs2").exists());

    let single = dir.path().join("single");
    engine()
        .read_entry(&archive, "docs/sub/b.txt", &single, limits, &mut Quiet)
        .unwrap();
    assert_eq!(
        std::fs::read(single.join("docs").join("sub").join("b.txt")).unwrap(),
        b"beta"
    );
    assert!(!single.join("docs").join("a.txt").exists());

    let missing = dir.path().join("missing");
    assert_eq!(
        engine().read_entry(&archive, "doc", &missing, limits, &mut Quiet),
        Err("Archive entry not found: doc".to_string())
    );
}

#[test]
fn read_entry_rejects_a_selection_over_limits() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = docs_archive(dir.path());
    let out = dir.path().join("read");
    std::fs::create_dir_all(&out).unwrap();

    // docs, docs/sub, docs/a.txt and docs/sub/b.txt: four entries, 9 bytes.
    assert_eq!(
        engine().read_entry(
            &archive,
            "docs",
            &out,
            Limits {
                max_bytes: 1 << 20,
                max_entries: 3,
            },
            &mut Quiet,
        ),
        Err("Archive extract exceeds entry limit of 3 entries".to_string())
    );
    assert_eq!(
        engine().read_entry(
            &archive,
            "docs",
            &out,
            Limits {
                max_bytes: 8,
                max_entries: 16,
            },
            &mut Quiet,
        ),
        Err("Archive extract exceeds size limit of 8 bytes".to_string())
    );
    assert!(is_empty_dir(&out));
}

#[test]
fn read_entry_reports_progress_through_the_observer() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let big = dir.path().join("src").join("big.bin");
    let size = 32 * 1024 * 1024;
    write(&big, noise(size));
    let archive = dir.path().join("big.7z");
    engine().create(&archive, &[big], &mut Quiet).unwrap();

    let out = dir.path().join("read");
    let mut observer = Recording::default();
    engine()
        .read_entry(
            &archive,
            "big.bin",
            &out,
            Limits {
                max_bytes: 1 << 30,
                max_entries: 16,
            },
            &mut observer,
        )
        .unwrap();

    assert_eq!(
        std::fs::metadata(out.join("big.bin")).unwrap().len(),
        size as u64
    );
    assert_eq!(observer.totals, vec![size as u64]);
    assert_eq!(
        observer.reports.first().map(|r| r.0),
        Some(0),
        "{:?}",
        observer.reports
    );
    assert_eq!(
        observer.reports.last().map(|r| r.0),
        Some(100),
        "{:?}",
        observer.reports
    );
    assert!(
        observer
            .reports
            .windows(2)
            .all(|pair| pair[0].0 <= pair[1].0),
        "progress went backwards: {:?}",
        observer.reports
    );
}

#[test]
fn handler_runs_7za_off_the_async_workers() {
    let _spawns = shared();
    // A current-thread runtime: the ticker only advances while the handler
    // leaves the one worker free.
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
        .block_on(create_64_mib_through_the_handler_while_ticking());
}

async fn create_64_mib_through_the_handler_while_ticking() {
    let dir = tempfile::tempdir().unwrap();
    let big = dir.path().join("big.bin");
    write(&big, noise(64 * 1024 * 1024));
    let archive = dir.path().join("big.7z");
    let handler = SevenZipHandler::with_legacy_env(bundled_dir(), None);
    let host = HostClient::fake(HashMap::new());

    let ticks = Arc::new(AtomicU64::new(0));
    let ticker = {
        let ticks = ticks.clone();
        tokio::spawn(async move {
            loop {
                tokio::time::sleep(Duration::from_millis(10)).await;
                ticks.fetch_add(1, Ordering::Relaxed);
            }
        })
    };
    let started = Instant::now();
    handler
        .handle(
            "archive.create",
            json!({ "archivePath": archive, "paths": [big], "operationId": "op-create" }),
            &host,
        )
        .await
        .unwrap();
    let elapsed = started.elapsed();
    ticker.abort();

    let ticked = ticks.load(Ordering::Relaxed);
    let expected = (elapsed.as_millis() / 10 / 4) as u64;
    assert!(
        elapsed >= Duration::from_millis(200),
        "the create was too quick to prove anything: {elapsed:?}"
    );
    assert!(
        ticked >= expected,
        "the ticker ticked {ticked} times in {elapsed:?}; 7za ran on the async worker"
    );
    assert!(archive.is_file());
    let progress = host.fake_progress();
    assert!(
        progress.iter().all(|(op, ..)| op == "op-create"),
        "{progress:?}"
    );
    let total = 64 * 1024 * 1024;
    assert_eq!(progress.first().map(|p| (p.1, p.2)), Some((0, total)));
    assert_eq!(progress.last().map(|p| (p.1, p.2)), Some((total, total)));
}

#[test]
fn rename_moves_a_folder_with_its_descendants() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = docs_archive(dir.path());

    engine()
        .rename(&archive, "docs", "papers", &mut Quiet)
        .unwrap();

    assert_eq!(
        listed(&archive),
        set(&[
            "docs2",
            "docs2/c.txt",
            "papers",
            "papers/a.txt",
            "papers/sub",
            "papers/sub/b.txt",
        ])
    );

    engine()
        .rename(&archive, "papers/sub/b.txt", "papers/b2.txt", &mut Quiet)
        .unwrap();
    assert!(listed(&archive).contains("papers/b2.txt"));
    assert!(!listed(&archive).contains("papers/sub/b.txt"));

    assert_eq!(
        engine().rename(&archive, "nothing", "else", &mut Quiet),
        Err("Archive entry not found: nothing".to_string())
    );
    assert_eq!(
        engine().rename(&archive, "papers", "docs2", &mut Quiet),
        Err("Archive entry already exists: docs2".to_string())
    );
}

#[test]
fn delete_removes_a_folder_with_its_descendants() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = docs_archive(dir.path());

    engine()
        .delete(&archive, &["docs".to_string()], &mut Quiet)
        .unwrap();

    assert_eq!(listed(&archive), set(&["docs2", "docs2/c.txt"]));
    assert_eq!(
        engine().delete(&archive, &["docs".to_string()], &mut Quiet),
        Err("Archive entry not found: docs".to_string())
    );
}

#[test]
fn deleting_the_last_entry_fails_and_leaves_the_archive_unchanged() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let only = dir.path().join("only.txt");
    write(&only, "the only entry");
    let archive = dir.path().join("only.7z");
    engine().create(&archive, &[only], &mut Quiet).unwrap();
    let before = std::fs::read(&archive).unwrap();

    assert_eq!(
        engine().delete(&archive, &["only.txt".to_string()], &mut Quiet),
        Err("7-Zip archives cannot be rewritten with no entries".to_string())
    );
    assert_eq!(std::fs::read(&archive).unwrap(), before);

    // Deleting every top-level folder is the same as deleting everything.
    let docs = docs_archive(dir.path());
    let before = std::fs::read(&docs).unwrap();
    assert_eq!(
        engine().delete(
            &docs,
            &["docs".to_string(), "docs2".to_string()],
            &mut Quiet
        ),
        Err("7-Zip archives cannot be rewritten with no entries".to_string())
    );
    assert_eq!(std::fs::read(&docs).unwrap(), before);
}

#[test]
fn add_stores_files_and_empty_folders_at_relative_paths() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let top = dir.path().join("top.txt");
    write(&top, "top");
    let archive = dir.path().join("pack.7z");
    engine().create(&archive, &[top], &mut Quiet).unwrap();
    let base = dir.path().join("base");
    write(&base.join("docs").join("new").join("n.txt"), "new");
    std::fs::create_dir_all(base.join("docs").join("empty")).unwrap();
    write(&base.join("loose [1].txt"), "loose");

    engine()
        .add(
            &archive,
            &base,
            &[
                "docs/new".to_string(),
                "docs/empty".to_string(),
                "loose [1].txt".to_string(),
            ],
            &mut Quiet,
        )
        .unwrap();

    assert_eq!(
        listed(&archive),
        set(&[
            "docs/empty",
            "docs/new",
            "docs/new/n.txt",
            "loose [1].txt",
            "top.txt",
        ])
    );
    let entries = engine().list(&archive).unwrap();
    let empty = entries
        .iter()
        .find(|e| e.path.replace('\\', "/") == "docs/empty")
        .unwrap();
    assert!(empty.is_dir);

    let out = dir.path().join("out");
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(
        std::fs::read(out.join("docs").join("new").join("n.txt")).unwrap(),
        b"new"
    );
    assert!(out.join("docs").join("empty").is_dir());
}

#[test]
fn unicode_and_punctuation_names_survive_every_operation() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("source");
    write(&source.join("café 資料").join("hello.txt"), "bonjour");
    write(&source.join("literal [brackets] & spaces.txt"), "literal");
    let archive = dir.path().join("names.7z");

    engine()
        .create(
            &archive,
            &[
                source.join("café 資料"),
                source.join("literal [brackets] & spaces.txt"),
            ],
            &mut Quiet,
        )
        .unwrap();
    assert_eq!(
        listed(&archive),
        set(&[
            "café 資料",
            "café 資料/hello.txt",
            "literal [brackets] & spaces.txt",
        ])
    );

    engine()
        .rename(
            &archive,
            "literal [brackets] & spaces.txt",
            "café 資料/renamed [x] & y.txt",
            &mut Quiet,
        )
        .unwrap();
    assert_eq!(
        listed(&archive),
        set(&[
            "café 資料",
            "café 資料/hello.txt",
            "café 資料/renamed [x] & y.txt",
        ])
    );

    let read = dir.path().join("read");
    engine()
        .read_entry(
            &archive,
            "café 資料/renamed [x] & y.txt",
            &read,
            Limits {
                max_bytes: 1 << 20,
                max_entries: 16,
            },
            &mut Quiet,
        )
        .unwrap();
    assert_eq!(
        std::fs::read(read.join("café 資料").join("renamed [x] & y.txt")).unwrap(),
        b"literal"
    );
    assert!(!read.join("café 資料").join("hello.txt").exists());

    engine()
        .delete(&archive, &["café 資料/hello.txt".to_string()], &mut Quiet)
        .unwrap();
    assert_eq!(
        listed(&archive),
        set(&["café 資料", "café 資料/renamed [x] & y.txt"])
    );

    let out = dir.path().join("out");
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(
        std::fs::read(out.join("café 資料").join("renamed [x] & y.txt")).unwrap(),
        b"literal"
    );
    assert!(!out.join("café 資料").join("hello.txt").exists());
}

/// A `.7z` holding ` lead.txt`, `lead.txt`, ` docs/inner.txt` and
/// `docs/inner.txt`. 7-Zip trims every listfile line, so the names with a
/// leading space must reach it intact and never select their trimmed
/// siblings.
fn leading_space_archive(root: &Path) -> PathBuf {
    let source = root.join("source");
    write(&source.join(" lead.txt"), "spaced");
    write(&source.join("lead.txt"), "plain");
    write(&source.join(" docs").join("inner.txt"), "spaced inner");
    write(&source.join("docs").join("inner.txt"), "plain inner");
    let archive = root.join("lead.7z");
    engine()
        .create(
            &archive,
            &[
                source.join(" lead.txt"),
                source.join("lead.txt"),
                source.join(" docs"),
                source.join("docs"),
            ],
            &mut Quiet,
        )
        .unwrap();
    assert_eq!(listed(&archive), leading_space_names(&[]));
    archive
}

/// The fixture's names, without `removed`.
fn leading_space_names(removed: &[&str]) -> BTreeSet<String> {
    let mut names = set(&[
        " docs",
        " docs/inner.txt",
        " lead.txt",
        "docs",
        "docs/inner.txt",
        "lead.txt",
    ]);
    for name in removed {
        names.remove(*name);
    }
    names
}

#[test]
fn read_entry_keeps_a_leading_space_in_a_top_level_name() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = leading_space_archive(dir.path());
    let limits = Limits {
        max_bytes: 1 << 20,
        max_entries: 16,
    };

    let read = dir.path().join("read-file");
    engine()
        .read_entry(&archive, " lead.txt", &read, limits, &mut Quiet)
        .unwrap();
    assert_eq!(std::fs::read(read.join(" lead.txt")).unwrap(), b"spaced");
    assert!(!read.join("lead.txt").exists());

    let read = dir.path().join("read-folder");
    engine()
        .read_entry(&archive, " docs", &read, limits, &mut Quiet)
        .unwrap();
    assert_eq!(
        std::fs::read(read.join(" docs").join("inner.txt")).unwrap(),
        b"spaced inner"
    );
    assert!(!read.join("docs").exists());
}

#[test]
fn delete_keeps_a_leading_space_in_a_top_level_name() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = leading_space_archive(dir.path());

    engine()
        .delete(&archive, &[" lead.txt".to_string()], &mut Quiet)
        .unwrap();
    assert_eq!(listed(&archive), leading_space_names(&[" lead.txt"]));

    engine()
        .delete(&archive, &[" docs".to_string()], &mut Quiet)
        .unwrap();
    assert_eq!(
        listed(&archive),
        leading_space_names(&[" lead.txt", " docs", " docs/inner.txt"])
    );

    let out = dir.path().join("out");
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(std::fs::read(out.join("lead.txt")).unwrap(), b"plain");
    assert_eq!(
        std::fs::read(out.join("docs").join("inner.txt")).unwrap(),
        b"plain inner"
    );
}

#[test]
fn add_keeps_a_leading_space_in_a_top_level_name() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = leading_space_archive(dir.path());
    let base = dir.path().join("base");
    write(&base.join(" only.txt"), "spaced only");
    write(&base.join("only.txt"), "plain only");
    write(&base.join(" more").join("m.txt"), "spaced more");
    write(&base.join("more").join("m.txt"), "plain more");

    engine()
        .add(
            &archive,
            &base,
            &[" only.txt".to_string(), " more".to_string()],
            &mut Quiet,
        )
        .unwrap();

    let mut expected = leading_space_names(&[]);
    expected.extend(set(&[" only.txt", " more", " more/m.txt"]));
    assert_eq!(listed(&archive), expected);
    let out = dir.path().join("out");
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(
        std::fs::read(out.join(" only.txt")).unwrap(),
        b"spaced only"
    );
    assert_eq!(
        std::fs::read(out.join(" more").join("m.txt")).unwrap(),
        b"spaced more"
    );
}

/// A `.7z` holding `a.txt` ("lower"), `A.TXT` ("upper case") and, with
/// `keep`, `keep.txt`: names that differ only by case, as archives made on
/// Linux often hold. A Windows folder cannot hold both names, so `A.TXT`
/// is stored as `b.txt` and renamed with the bundled 7za.
fn case_archive(root: &Path, keep: bool) -> PathBuf {
    let source = root.join("case-source");
    write(&source.join("a.txt"), "lower");
    write(&source.join("b.txt"), "upper case");
    let mut sources = vec![source.join("a.txt"), source.join("b.txt")];
    if keep {
        write(&source.join("keep.txt"), "keep");
        sources.push(source.join("keep.txt"));
    }
    let archive = root.join("case.7z");
    engine().create(&archive, &sources, &mut Quiet).unwrap();
    cli(&[
        &"rn".into(),
        &"-ssc".into(),
        &"-bso0".into(),
        &"-bsp0".into(),
        &"--".into(),
        &archive.clone().into_os_string(),
        &"b.txt".into(),
        &"A.TXT".into(),
    ]);
    let mut expected = set(&["A.TXT", "a.txt"]);
    if keep {
        expected.insert("keep.txt".to_string());
    }
    assert_eq!(listed(&archive), expected);
    archive
}

/// Every entry's listed size, by exact name.
fn sizes(archive: &Path) -> Vec<(String, u64)> {
    let mut sizes: Vec<_> = engine()
        .list(archive)
        .unwrap()
        .into_iter()
        .map(|entry| (entry.path.replace('\\', "/"), entry.size))
        .collect();
    sizes.sort();
    sizes
}

fn sized(items: &[(&str, u64)]) -> Vec<(String, u64)> {
    items
        .iter()
        .map(|(name, size)| (name.to_string(), *size))
        .collect()
}

#[test]
fn read_entry_reads_only_the_named_spelling() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = case_archive(dir.path(), true);
    let limits = Limits {
        max_bytes: 1 << 20,
        max_entries: 16,
    };

    let lower = dir.path().join("read-lower");
    engine()
        .read_entry(&archive, "a.txt", &lower, limits, &mut Quiet)
        .unwrap();
    assert_eq!(std::fs::read(lower.join("a.txt")).unwrap(), b"lower");

    let upper = dir.path().join("read-upper");
    engine()
        .read_entry(&archive, "A.TXT", &upper, limits, &mut Quiet)
        .unwrap();
    assert_eq!(std::fs::read(upper.join("A.TXT")).unwrap(), b"upper case");

    assert_eq!(
        engine().read_entry(
            &archive,
            "A.txt",
            &dir.path().join("none"),
            limits,
            &mut Quiet
        ),
        Err("Archive entry not found: A.txt".to_string())
    );
}

#[test]
fn delete_removes_only_the_named_spelling() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = case_archive(dir.path(), true);
    let before = std::fs::read(&archive).unwrap();

    assert_eq!(
        engine().delete(&archive, &["A.txt".to_string()], &mut Quiet),
        Err("Archive entry not found: A.txt".to_string())
    );
    assert_eq!(std::fs::read(&archive).unwrap(), before);

    engine()
        .delete(&archive, &["a.txt".to_string()], &mut Quiet)
        .unwrap();

    assert_eq!(sizes(&archive), sized(&[("A.TXT", 10), ("keep.txt", 4)]));
    let out = dir.path().join("out");
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(std::fs::read(out.join("A.TXT")).unwrap(), b"upper case");
}

/// Review Focus 3 with names that differ only by case: deleting both is
/// refused and changes nothing, and deleting one never empties the
/// archive.
#[test]
fn deletes_on_two_names_that_differ_only_by_case_never_empty_the_archive() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = case_archive(dir.path(), false);
    let before = std::fs::read(&archive).unwrap();

    assert_eq!(
        engine().delete(
            &archive,
            &["a.txt".to_string(), "A.TXT".to_string()],
            &mut Quiet
        ),
        Err("7-Zip archives cannot be rewritten with no entries".to_string())
    );
    assert_eq!(std::fs::read(&archive).unwrap(), before);

    engine()
        .delete(&archive, &["a.txt".to_string()], &mut Quiet)
        .unwrap();
    assert_eq!(sizes(&archive), sized(&[("A.TXT", 10)]));
}

#[test]
fn rename_renames_only_the_named_spelling() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = case_archive(dir.path(), true);

    engine()
        .rename(&archive, "a.txt", "c.txt", &mut Quiet)
        .unwrap();
    assert_eq!(
        sizes(&archive),
        sized(&[("A.TXT", 10), ("c.txt", 5), ("keep.txt", 4)])
    );

    // A rename that only changes case.
    engine()
        .rename(&archive, "keep.txt", "KEEP.txt", &mut Quiet)
        .unwrap();
    assert_eq!(
        sizes(&archive),
        sized(&[("A.TXT", 10), ("KEEP.txt", 4), ("c.txt", 5)])
    );

    let out = dir.path().join("out");
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(std::fs::read(out.join("c.txt")).unwrap(), b"lower");
    assert_eq!(std::fs::read(out.join("A.TXT")).unwrap(), b"upper case");
}

/// `add` stores each path exactly as named. Core checks new names
/// case-insensitively, so it does not send this today, but adding `A.TXT`
/// must still never replace an `a.txt` the caller did not name.
#[test]
fn add_keeps_an_entry_whose_name_differs_only_by_case() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let lower = dir.path().join("a.txt");
    write(&lower, "lower");
    let archive = dir.path().join("pack.7z");
    engine().create(&archive, &[lower], &mut Quiet).unwrap();
    let base = dir.path().join("base");
    write(&base.join("A.TXT"), "upper case");

    engine()
        .add(&archive, &base, &["A.TXT".to_string()], &mut Quiet)
        .unwrap();

    assert_eq!(sizes(&archive), sized(&[("A.TXT", 10), ("a.txt", 5)]));
}

#[test]
fn deflate64_archive_lists_extracts_and_reads() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("source");
    write(
        &source.join("café 資料").join("hello.txt"),
        "Deflate64 ".repeat(1000),
    );
    let archive = dir.path().join("deflate64.7z");
    cli(&[
        &"a".into(),
        &"-t7z".into(),
        &"-m0=Deflate64".into(),
        &"-sccUTF-8".into(),
        &"-bso0".into(),
        &"-bsp0".into(),
        &"--".into(),
        &archive.clone().into_os_string(),
        &source.join("café 資料").into_os_string(),
    ]);

    assert_eq!(listed(&archive), set(&["café 資料", "café 資料/hello.txt"]));
    let out = dir.path().join("out");
    engine().extract_all(&archive, &out, &mut Quiet).unwrap();
    assert_eq!(
        std::fs::read_to_string(out.join("café 資料").join("hello.txt")).unwrap(),
        "Deflate64 ".repeat(1000)
    );
    let read = dir.path().join("read");
    engine()
        .read_entry(
            &archive,
            "café 資料/hello.txt",
            &read,
            Limits {
                max_bytes: 1 << 20,
                max_entries: 16,
            },
            &mut Quiet,
        )
        .unwrap();
    assert_eq!(
        std::fs::read_to_string(read.join("café 資料").join("hello.txt")).unwrap(),
        "Deflate64 ".repeat(1000)
    );
}

fn encrypted_archive(dir: &Path, encrypted_headers: bool) -> PathBuf {
    let source = dir.join("secret");
    write(&source.join("hidden.txt"), "secret content");
    let archive = dir.join(if encrypted_headers {
        "headers.7z"
    } else {
        "content.7z"
    });
    let headers = if encrypted_headers {
        "-mhe=on"
    } else {
        "-mhe=off"
    };
    cli(&[
        &"a".into(),
        &"-t7z".into(),
        &"-pFixturePassword".into(),
        &headers.into(),
        &"-bso0".into(),
        &"-bsp0".into(),
        &"--".into(),
        &archive.clone().into_os_string(),
        &source.into_os_string(),
    ]);
    archive
}

#[test]
fn encrypted_content_lists_names_but_extract_fails() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = encrypted_archive(dir.path(), false);

    assert_eq!(listed(&archive), set(&["secret", "secret/hidden.txt"]));

    let out = dir.path().join("out");
    let error = engine()
        .extract_all(&archive, &out, &mut Quiet)
        .unwrap_err();
    assert!(error.starts_with("7-Zip extraction failed:"), "{error}");
    assert!(error.contains("Wrong password"), "{error}");
    assert!(is_empty_dir(&out), "a failed extract leaves no output");
}

#[test]
fn encrypted_headers_fail_to_list() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let archive = encrypted_archive(dir.path(), true);

    let started = Instant::now();
    let error = engine().list(&archive).unwrap_err();
    assert!(error.starts_with("7-Zip list failed:"), "{error}");
    assert!(error.contains("encrypted"), "{error}");
    assert!(started.elapsed() < Duration::from_secs(10));
}

#[test]
fn create_refuses_an_existing_archive() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("a.txt");
    write(&file, "a");
    let archive = dir.path().join("taken.7z");
    write(&archive, "not an archive");

    assert_eq!(
        engine().create(&archive, &[file], &mut Quiet),
        Err(format!("Archive already exists: {}", archive.display()))
    );
    assert_eq!(std::fs::read(&archive).unwrap(), b"not an archive");
}

#[test]
fn update_commands_leave_no_temporary_files_beside_the_archive() {
    let _spawns = shared();
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("source");
    write(&source.join("docs").join("a.txt"), "a");
    write(&source.join("keep.txt"), "keep");
    let base = dir.path().join("base");
    write(&base.join("added.txt"), "added");
    let folder = dir.path().join("archive-folder");
    std::fs::create_dir_all(&folder).unwrap();
    let archive = folder.join("pack.7z");
    engine()
        .create(
            &archive,
            &[source.join("docs"), source.join("keep.txt")],
            &mut Quiet,
        )
        .unwrap();
    let only_the_archive = || {
        let names: Vec<_> = std::fs::read_dir(&folder)
            .unwrap()
            .map(|e| e.unwrap().file_name())
            .collect();
        assert_eq!(names, vec![OsString::from("pack.7z")]);
    };

    engine()
        .rename(&archive, "docs", "papers", &mut Quiet)
        .unwrap();
    only_the_archive();
    engine()
        .add(&archive, &base, &["added.txt".to_string()], &mut Quiet)
        .unwrap();
    only_the_archive();
    engine()
        .delete(&archive, &["papers".to_string()], &mut Quiet)
        .unwrap();
    only_the_archive();
    assert_eq!(listed(&archive), set(&["added.txt", "keep.txt"]));
}
