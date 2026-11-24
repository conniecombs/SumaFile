# Batch 1 Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the small open items left after 1.2.0 that can be developed and tested without a Windows machine.

**Architecture:** Each task is independent. Logic goes where it can be tested on Linux (the Rust crates, `SimpleFile.Core`); the WinUI project gets only wiring, checked by a parser-only syntax pass here and by Windows CI.

**Tech Stack:** Rust (`sumafile-transfer`, `simplefile-core`), C# (.NET 10, `SimpleFile.Core`, `SimpleFile.App`), SQLite through `rusqlite`, the `zip` crate.

**Spec:** [CODEBASE_AUDIT_2026-11-23.md](../../CODEBASE_AUDIT_2026-11-23.md) items U5, A7, A9, and the Transfers and Search sections of [ROADMAP.md](../../ROADMAP.md).

## Global Constraints

- All work stays on `WorkingBranch`. No pull request, no tag.
- No protocol change between the app and the file service.
- A behaviour that cannot be run here is written so that an existing Windows CI test exercises it, and is named as unverified in the hand-off.

## Review Focus

- A saved window position on a monitor that is no longer attached: the window must open on a screen that exists.
- A saved size larger than the current screen, or a corrupt placement file: defaults, no crash.
- A ZIP whose entry names differ only by case, repeat, or are not plain relative paths: the old extract-and-rebuild path, not the raw copy.
- A destination folder on a filesystem where the filtered listing fails: recovery falls back to the full listing.
- A fact cache at its limit while facts are being read: readers never see an error, old rows go first.

---

### Task 1: Search results are appended to the pane

**Files:** `src-winui/SimpleFile.App/MainWindow.Search.cs`, `src-winui/SimpleFile.Tests/UiAuditRegressionTests.cs`

- [x] `ApplySearchRows` updates the pane's rows with `ReplaceIfChanged(rows, list, SameFileRow)` instead of `Replace`, so a batch that extends the list is an append and scroll and selection survive.
- [x] Source-shape test: `ApplySearchRows` contains no `Replace(PrimaryFiles` / `Replace(SecondaryFiles`. `ListReplace` already has tests for the append case.
- [x] Commit.

### Task 2: Recovery lists only ownership records

**Files:** `crates/sumafile-transfer/src/journal.rs`

**Interfaces:** Produces `fn record_files(directory: &Path) -> std::io::Result<Vec<PathBuf>>` (private). On Windows it asks the filesystem for `*.sumafile-partial.state` with `FindFirstFileExW` (`FindExInfoBasic`, `FIND_FIRST_EX_LARGE_FETCH`); elsewhere, and when that call fails with anything but "not found", it lists the folder and filters by name.

- [x] Test (runs everywhere): a folder with two records and 200 other files yields exactly the two record paths; a missing folder yields `NotFound`; an empty folder yields none.
- [x] `recover_directory` iterates `record_files`. The existing recovery tests are the Windows check.
- [x] `cargo clippy -p sumafile-transfer --target x86_64-pc-windows-gnu --all-targets -- -D warnings`. Commit.

### Task 3: A reader connection for `metadata.db`, and a bound on `entry_facts`

**Files:** `crates/simplefile-core/src/settings_store.rs`, `crates/simplefile-core/src/fact_cache.rs`

**Interfaces:**
- Produces `pub fn open_metadata_db_reader() -> Result<MetadataDb, String>` and `pub(crate) fn open_metadata_db_reader_at(path: &Path)`: a second cached connection per path with `PRAGMA query_only = ON`, opened after the writer has created the schema.
- Produces `pub(crate) const MAX_ENTRY_FACTS: i64 = 200_000`, `pub(crate) fn prune_entry_facts(connection: &Connection, limit: i64) -> rusqlite::Result<usize>`: when the table holds more than `limit` rows, deletes the oldest written (lowest `rowid`) down to nine tenths of `limit`.

- [x] Tests: a value written through the writer is read through the reader at once; the reader refuses a write; a reader lock held on one thread does not block a write on another; pruning a table of `limit + 50` rows leaves `limit * 9 / 10` and keeps the newest; a fact written again counts as new.
- [x] `get_db_setting(s)`, `get_cached_fact` and `get_cached_facts_for_path` use the reader. `put_cached_facts_batch` prunes after every 4,096 facts written, and once on the first write of a process.
- [x] Commit.

### Task 4: The main window opens where it was closed (U5)

**Files:** create `src-winui/SimpleFile.Core/WindowPlacement.cs`, `src-winui/SimpleFile.Tests/WindowPlacementTests.cs`; modify `src-winui/SimpleFile.App/MainWindow.xaml.cs` (new partial `MainWindow.Placement.cs`).

**Interfaces:**
- `public readonly record struct ScreenRect(int X, int Y, int Width, int Height)`.
- `public sealed record WindowPlacement(ScreenRect Bounds, bool Maximized)` with `public ScreenRect FitTo(ScreenRect workArea)`: size clamped to the work area and to a minimum of 640 by 400, then moved so that all of it is inside.
- `public static class WindowPlacementStore`: `Load(string path) -> WindowPlacement?` (null for a missing, unreadable or implausible file), `Save(string path, WindowPlacement placement)` (write to a temporary name, then replace), `DefaultPath()` = `%LOCALAPPDATA%\SumaFile\window.json`.
- App: after the default `Resize`, load the placement, find the nearest display with `DisplayArea.GetFromRect(..., DisplayAreaFallback.Nearest)`, `AppWindow.MoveAndResize(placement.FitTo(workArea))`, then `Maximize()` if it was maximized. `AppWindow.Changed` remembers the last bounds seen in the restored state; `OnClosed` saves them with whether the window was maximized. Neither runs when `UiSmokeRoot()` is set, so smoke runs keep their fixed size.

- [x] Tests for `FitTo` (inside stays put; off-screen to the left, right, above; larger than the screen; a second monitor at negative coordinates), and for the store (round trip, garbage, zero size, missing file).
- [x] App wiring; parser-only check; source-shape test that the smoke guard is there. Commit.

### Task 5: ZIP delete and rename without recompressing (A9)

**Files:** create `crates/simplefile-core/src/archive/zip_inplace.rs`; modify `crates/simplefile-core/src/archive/mutate.rs`, `archive/mod.rs`, `archive/tests.rs`.

**Interfaces:** Produces `pub(super) fn try_delete(archive: &Path, inner: &[PathBuf]) -> Result<Option<Vec<PathBuf>>, String>` and `pub(super) fn try_rename(archive: &Path, inner: &Path, new_name: &str) -> Result<Option<PathBuf>, String>`. `Ok(None)` means "not a plain archive, use the rebuild path": some entry name is not a plain relative path, or two names are equal ignoring case. Otherwise every kept entry is copied with `ZipWriter::raw_copy_file` (renamed ones with `raw_copy_file_rename`) into the temporary archive, which replaces the original as the rebuild does.

- [x] Tests: after a delete, the remaining entries have the same names, the same compressed bytes (CRC and compressed size) and the same modified times as before; deleting a folder removes everything under it; a missing entry is reported and the archive is unchanged; rename of a file and of a folder; rename onto an existing name is refused; an archive with a `../` name or two names differing by case returns `None`. Each result equals what the rebuild path lists.
- [x] Wire into `delete_builtin_archive_entries_batch` and `rename_archive_entry` for `ArchiveFormat::Zip`. Commit.

### Task 6: Documents

- [x] `service.log` (A9) is already bounded: `BackendSession` rotates it at 1 MB and keeps one earlier file. Record that in the audit instead of changing it.
- [x] Audit, roadmap and changelog (`[Unreleased]`) say what the code now does. The duplicate search reply stays on the roadmap: removing it is a protocol change.
- [x] `npm run check`, `npm run test:maintenance`, `cargo fmt --all -- --check`. Commit and push both branches.
