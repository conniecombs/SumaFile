# Column picker and lazy columns — implementation plan

> **Status:** implemented; shipped in 1.1.4. This is a record of the plan as written; the task boxes below were not kept up to date.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A right-click column picker in the Details view with three new free columns (Created, Accessed, Attributes) and five lazy columns (Dimensions, Duration, Date taken, Camera, Pages) whose values, like folder Size and Items, are read on demand through one streaming service call, cached, and filled for the rows on screen.

**Architecture:** The Rust service gains three fields on `FileEntry` (binary frame version 2) and a `get_entry_facts` streaming request backed by per-kind readers, a persistent `entry_facts` table and the session cache. The C# Core gains column metadata, per-pane visible-column settings and a `LazyColumnController` that turns the visible range plus visible lazy columns into requests and applies the answers to entries; the App gains the picker menu, viewport reporting, "…" cells and the Windows duration fallback.

**Tech Stack:** Rust (image, lofty, lopdf, kamadak-exif, new: mp4 0.14, matroska 0.30; rusqlite), C# .NET 10 WinUI 3, the v1 JSON-RPC/SFB binary protocol, xUnit, cargo test (Windows target; pure modules via `#[path]` probes on Linux).

**Spec:** `docs/superpowers/specs/2026-11-19-column-picker-and-lazy-columns-design.md`

## Global Constraints

- Never add a NuGet package that is not already in the lock files. New Rust crates: `mp4`, `matroska` only (MIT).
- Binary frame version goes 1 → 2 in `crates/simplefile-ipc/src/protocol_generated.rs` (via `ipc/schema/v1/protocol.json`) and `Protocol.Generated.cs`; both goldens change in the same commit.
- `npm run check` (schema, generated bindings, parity gate: 79 → 82 domain methods) must pass after every task that touches `ipc/schema/v1`.
- Startup budget (50 ms) unchanged: no new work on the startup path.
- Automatic lazy columns never run for a pane whose `PathIsNetwork` is true.
- Caps: 64 paths per request, look-ahead 24 rows each way, 2 concurrent folder walks and 4 file reads in the service, fill-all cap 5,000 rows, 2 concurrent Windows fallback lookups.
- Local verification: `npm run check`, `npm run test:maintenance`, the Linux C# harness (`src-winui/SimpleFile.Tests/bin/linuxtests`), `cargo fmt --all -- --check`, Windows-target clippy (`xcheck.sh clippy --locked --all-targets --all-features -- -D warnings`), pure Rust modules through a `#[path]` probe crate.

## Review Focus

1. A folder of 20,000 files with Dimensions visible: only the visible rows and look-ahead are read; scrolling fast never queues more than 64 paths per request and cancels the previous one. (Task 9 test `OnlyTheVisibleRangeAndLookAheadAreRequested`, `ANewRangeReplacesTheOutstandingRequest`.)
2. A file renamed or rewritten while shown: the cached fact is keyed by size and modified time, so a changed file is re-read; an unchanged one is not. (Task 7 test `a_cached_fact_is_returned_only_for_the_same_size_and_modified_time`.)
3. A damaged media file or a 0-byte image: the reader returns `failed`/`none`, the cell shows blank, nothing is retried in a loop. (Task 6 tests `a_damaged_file_is_failed_not_an_error`, Task 9 `AFailedFactIsNotRequestedAgain`.)
4. Navigating away mid-request: no result from the old folder lands on the new one. (Task 9 `ResultsForAnEarlierNavigationAreDropped`; Rust Task 8 `cancel_stops_new_facts_and_drops_results`.)
5. Sorting by Duration in a 12,000-file folder: the fill stops at 5,000, the status line says so, and the list is still usable. (Task 10 `SortingByALazyColumnBeyondTheCapSortsWhatIsKnownAndSaysSo`.)

---

### Task 1: Free fields on `FileEntry`

**Files:**
- Modify: `crates/simplefile-core/src/models.rs` (FileEntry), `crates/simplefile-core/src/utils.rs`, `crates/simplefile-core/src/dir_list.rs` (both enumeration paths and every `FileEntry { .. }` literal), `crates/simplefile-core/src/metadata.rs`/others that construct `FileEntry` (grep `git_status: None`).
- Test: `crates/simplefile-core/src/utils.rs` tests; probe crate for `attribute_letters`.

**Interfaces:**
- Produces: `FileEntry { created: String, accessed: String, attributes: String }` (serde `default`, `skip_serializing_if = "String::is_empty"`), `pub fn attribute_letters(attrs: u32) -> String` in `utils.rs` (order R H S A C E O T L; `L` for `FILE_ATTRIBUTE_REPARSE_POINT`), `created`/`accessed` formatted by the existing `filetime_to_string`/`format_system_time` ("-" when unknown).

- [ ] Write failing tests: `attribute_letters_follows_explorers_order` (0x0001|0x0002|0x0020 → "RHA"; 0x0400|0x1000 → "LO"; 0 → ""), `a_find_data_entry_carries_created_accessed_and_attributes` (Windows-only, `#[cfg(windows)]`).
- [ ] Run via probe (utils is pure) and Windows-target clippy; expect failures/compile errors.
- [ ] Implement; fill from `ftCreationTime`/`ftLastAccessTime`/`dwFileAttributes` on Windows and `Metadata::created()/accessed()`/readonly on others.
- [ ] `cargo fmt`, probe tests pass, `xcheck.sh clippy -p simplefile-core` clean. Commit.

### Task 2: Wire format v2

**Files:**
- Modify: `ipc/schema/v1/protocol.json` (binaryFrames.version 2), `ipc/schema/v1/types.json` (FileEntry fields + optional), `crates/simplefile-service/src/binary.rs` (both entry encoders + goldens), `src-winui/SimpleFile.Ipc/BinaryFrameCodec.cs` (ReadEntries, ReadListingViewPage, WriteEntries, listing view writer), `src-winui/SimpleFile.Ipc/Models.cs` (FileEntry `Created`, `Accessed`, `Attributes`), `src-winui/SimpleFile.Tests/ListingViewIpcTests.cs`, `ModelsTests.cs`; regenerate bindings (`npm run generate:ipc-bindings`).

**Interfaces:**
- Produces: entry encodings append `string created, string accessed, string attributes` after `symlink_target` (listing view page) / after `git_status` (list_directory frames). `Protocol.BinaryFrameVersion == 2`, `BINARY_FRAME_VERSION == 2`.

- [ ] Update the C# golden test and Rust golden to the new bytes (compute from the Rust encoder once, paste into both; the test names stay). Add `FileEntry_DeserializesTheFreeFields` (JSON with `created`, `attributes`) and `FileEntry_OmitsEmptyFreeFields`.
- [ ] Run `npm run check` → schema mismatch expected; C# tests fail.
- [ ] Implement both sides; regenerate bindings; `npm run check` passes; Linux C# harness green; Windows-target clippy for the service. Commit.

### Task 3: Sorting and showing the free columns

**Files:**
- Modify: `crates/simplefile-core/src/listing_view.rs` (`column_key`: `"created"`, `"accessed"` → Bytes of the text; `"attributes"` → collator key), `src-winui/SimpleFile.Core/PaneListing.cs` (`IsServiceSortColumn`), `ColumnLayout.cs` (columns `created` "Created" 160, `accessed` "Accessed" 160, `attributes` "Attributes" 90), `EntryPresentation.cs` (`ColumnText`, `CompareColumn`, `FormatModified` reuse), `src-winui/SimpleFile.App/ExplorerRows.cs` (`CreatedText`, `AccessedText`, `AttributesText`).
- Test: probe test `a_view_sorts_by_created_accessed_and_attributes`; C# `EntryPresentationTests` for text and compare; `ListingViewWorkspaceTests.IsServiceSortColumn_NamesTheColumnsTheServiceOrders` rows.

- [ ] Tests first (fail), implement, pass, commit.

### Task 4: Column model and settings

**Files:**
- Modify: `src-winui/SimpleFile.Core/ColumnLayout.cs` (`FileListColumn.FactKind: string?`, `FileListColumn.CanHaveValue(FileEntry) : bool`, lazy columns `dimensions` "Dimensions" 120, `duration` "Duration" 90, `date_taken` "Date taken" 160, `camera` "Camera" 140, `pages` "Pages" 70; `SetVisible(string id, bool visible)`, `ApplyVisible(IEnumerable<string> ids)`), `UiSettings.cs` (`VisibleColumns`, `SecondaryVisibleColumns`: `List<string>`; `LazyColumnModes: Dictionary<string,string>` with `LazyColumnMode.Automatic/OnRequest` constants and `DefaultLazyColumnMode(kind)`: `folder_size` → on-request, others automatic), `WorkspaceSettingsStore.cs` (`visibleColumns` JSON {primary, secondary}; `lazyColumnModes`), `WorkspaceControllers/FolderViewController.cs` (`ApplyColumnSettings`: visible list when non-empty else preset; `SaveUiSettingsAsync` snapshots visible ids), `ExplorerWorkspace` (`SetColumnVisible(pane, id, visible)`, `ApplyColumnPreset(pane, preset)`, `SetLazyColumnMode(kind, mode)`).
- Test: `ColumnLayoutTests` (new or existing) and `WorkspaceSettingsStoreTests` round trip; `EntryPresentation`/`ColumnLayout` `CanHaveValue` per kind (dimensions: jpg/jpeg/png/gif/webp/bmp; duration: mp3/flac/wav/m4a/ogg/aac/mp4/m4v/mov/mkv/webm/avi/wmv; pages: pdf; date_taken/camera: jpg/jpeg/heic; folder_size/item_count: IsDir).

- [ ] Tests first, implement, pass, commit.

### Task 5: The picker menu

**Files:**
- Modify: `src-winui/SimpleFile.App/MainWindow.Columns.cs` (`CreateColumnHeaderFlyout` → `CreateColumnsFlyout(pane, columnId?)`: `ToggleMenuFlyoutItem` per column except `name`, `MenuFlyoutSubItem` "Presets", "Calculate for this folder" sub-items per visible lazy column (hook to Task 10's `workspace.FillFolderFactsAsync(pane, kind)`), separator, the two size-to-fit items), `MainWindow.Commands.cs` (`ShowDetailsBackgroundContextMenu`: append `MenuFlyoutSubItem` "Columns" built by the same method), `SettingsWindow` preset combo keeps working (preset → `ApplyColumnPreset`).
- Test: `WinUiSourceShapeTests`/`UiAuditRegressionTests` assertions (`ToggleMenuFlyoutItem`, "Presets", "Calculate for this folder", "Columns" in the background menu); app type-check in the scratch `appcheck` project.

- [ ] Tests first, implement, pass, commit.

### Task 6: Fact readers in the service core

**Files:**
- Create: `crates/simplefile-core/src/entry_facts.rs`.
- Modify: `crates/simplefile-core/Cargo.toml` (add `mp4 = "0.14"`, `matroska = "0.30"`), `lib.rs`, `models.rs` (`EntryFact { path, kind, state, text, sort: Option<f64> }`, `EntryFactsBatch { request_id, facts }`).
- Test: probe crate `factprobe` (`#[path]` entry_facts.rs + models) with generated fixtures: PNG/JPEG via `image`, WAV header by hand, MP4 via the `mp4` writer, a minimal MKV (EBML header + Segment/Info with TimecodeScale 1_000_000 and Duration), PDF via `lopdf::Document` with two pages, JPEG with Exif IFD0 Make/Model and Exif-IFD DateTimeOriginal.

**Interfaces:**
- Produces: `pub enum FactKind { FolderSize, ItemCount, Dimensions, Duration, DateTaken, Camera, Pages }` with `from_wire(&str)`/`as_wire()`, `pub enum FactState { Ready, None, Unsupported, Failed }`, `pub fn read_file_fact(path: &Path, kind: FactKind) -> EntryFact` (never panics; size caps as `metadata.rs`), `pub fn format_duration(seconds: f64) -> String` ("1:02:03", "0:45"), `pub fn format_dimensions(w,h) -> String` ("6000 × 4000"), `pub fn duration_of(path) -> Option<f64>` (lofty → mp4 → matroska by extension → `None` → state Unsupported for video extensions, None otherwise).

- [ ] Tests: `dimensions_come_from_the_header_only` (a 4000×3000 PNG with a truncated body still gives 4000 × 3000), `a_wav_has_a_duration`, `an_mp4_has_a_duration`, `an_mkv_has_a_duration`, `an_avi_is_unsupported_for_the_app_to_fall_back`, `a_pdf_counts_its_pages`, `date_taken_and_camera_come_from_exif`, `a_damaged_file_is_failed_not_an_error`, `durations_format_like_a_clock`.
- [ ] Run probe (fail), implement, pass; fmt; Windows-target clippy. Commit.

### Task 7: Persistent fact cache

**Files:**
- Create: `crates/simplefile-core/src/fact_cache.rs` (table `entry_facts(path TEXT, kind TEXT, size INTEGER, modified TEXT, state TEXT, text TEXT, sort REAL, PRIMARY KEY(path, kind))` in `metadata.db` through `settings_store::open_metadata_db`).
- Test: in-crate tests with a temp DB path (pattern of `settings_store` tests), run on CI; probe where possible.

**Interfaces:**
- Produces: `pub fn get_cached_fact(path: &str, kind: FactKind, size: u64, modified: &str) -> Option<EntryFact>`, `pub fn put_cached_fact(fact: &EntryFact, size: u64, modified: &str) -> Result<(), String>`; `Unsupported` and `Failed` are cached too (a failed file is not re-read until it changes).

- [ ] Tests `a_cached_fact_is_returned_only_for_the_same_size_and_modified_time`, `a_failed_fact_is_cached_until_the_file_changes`. Implement, pass, commit.

### Task 8: `get_entry_facts` over IPC

**Files:**
- Modify: `ipc/schema/v1/commands.json` (`get_entry_facts { requestId, paths[], kinds[] } → EntryFactsResult { requestId, settled: number }`, `cancel_entry_facts { requestId }` cancellation, `put_entry_facts { facts: EntryFact[] } → null`), `events.json` (`entry-facts: EntryFactsBatch`), `types.json`, `scripts/check-ipc-schema.mjs`/`check-winui-parity-gate.mjs` counts (79 → 82), `docs/winui-migration/parity-gate.md`; Rust `dispatch/{params,async_ops,handlers,mod}.rs`, `session/mod.rs`, new `session/fact_jobs.rs` (`spawn_entry_facts`: per request a cancel flag in a map keyed by requestId; folder kinds via `file_ops::get_folder_metrics` + session cache, file kinds via cache then `read_file_fact`; semaphores 2 walks / 4 reads; batches flushed every 16 facts or 50 ms as `entry-facts` notifications; final response `settled`), `session/jobs.rs` (`EventSink::emit_entry_facts`); C# `ISimpleFileIpc` (`GetEntryFactsAsync`, `CancelEntryFactsAsync`, `PutEntryFactsAsync`, `event Action<EntryFactsBatch>? EntryFactsReceived`), `NamedPipeJsonClient.cs` notification switch, `Models.cs`, regenerated bindings.
- Test: Rust `session/tests.rs` (`entry_facts_stream_in_batches_and_settle`, `cancel_stops_new_facts_and_drops_results`, `put_entry_facts_is_served_from_the_cache_next_time`), C# `ListingViewIpcTests`-style decode test for the event, script tests for the counts.

- [ ] Tests first, implement, `npm run check` green, commit.

### Task 9: `LazyColumnController` in Core

**Files:**
- Create: `src-winui/SimpleFile.Core/WorkspaceControllers/LazyColumnController.cs`, `src-winui/SimpleFile.Core/IEntryFactsBackend.cs`.
- Modify: `IExplorerBackend` (`IEntryFactsBackend? EntryFacts => null`), `BackendSession` (implements it over the IPC client), `FileEntry`-side storage: `ExplorerPane.Facts: Dictionary<string /*path*/, Dictionary<string /*kind*/, EntryFact>>`-equivalent `PaneFacts` class, `ExplorerWorkspace` (`ReportViewport(PaneId, int first, int last)`, `RequestFact(PaneId, string path, string kind)`, `FactFor(PaneId, path, kind)`), `EntryPresentation.ColumnText`/`CompareColumn` reading facts (text / sort), `PaneEnrichmentController` retired for sizes/items (the controller sets `entry.Size`/`ItemCount` from `folder_size`/`item_count` facts), `MainWindow.Columns.cs` `QueueColumnEnrichment` → `ReportViewport`.
- Test: `src-winui/SimpleFile.Tests/FakeEntryFactsBackend.cs`, `LazyColumnWorkspaceTests.cs`: `OnlyTheVisibleRangeAndLookAheadAreRequested`, `ANewRangeReplacesTheOutstandingRequest`, `ResultsForAnEarlierNavigationAreDropped`, `AFailedFactIsNotRequestedAgain`, `OnRequestColumnsWaitForAnExplicitRequest`, `NothingIsRequestedOnANetworkPath`, `FolderSizeAndItemCountFactsUpdateTheEntry`, `AnUnsupportedDurationIsHandedToTheFallback`.

**Interfaces:**
- Produces: `interface IEntryFactsBackend { Task<int> GetEntryFactsAsync(string requestId, IReadOnlyList<string> paths, IReadOnlyList<string> kinds, CancellationToken); Task CancelEntryFactsAsync(string requestId, CancellationToken); Task PutEntryFactsAsync(IReadOnlyList<EntryFact>, CancellationToken); event Action<EntryFactsBatch>? EntryFactsReceived; }`; `ExplorerWorkspace.FactFallback: Func<string path, string kind, CancellationToken, Task<EntryFact?>>?` (set by the App for `duration`).

- [ ] Tests first, implement, Linux C# harness green, commit.

### Task 10: App wiring, "…" cells, fallback, sort fill

**Files:**
- Modify: `src-winui/SimpleFile.App/DetailsFileListView.cs` (report `ItemsStackPanel.FirstVisibleIndex/LastVisibleIndex` on `ViewChanged` and after updates → `MainWindow` → `workspace.ReportViewport`), `FileRowView.xaml.cs` (lazy cell text "…" when `CanHaveValue` and no fact; `Tapped` on such a cell → `workspace.RequestFact`), `MainWindow.xaml.cs` (set `FactFallback` using `StorageFile.Properties.GetVideoPropertiesAsync`/`GetMusicPropertiesAsync`, `SemaphoreSlim(2)`), `ExplorerWorkspace.FillFolderFactsAsync(PaneId, string kind, CancellationToken)` in Core with progress through `StatusMessage` ("Reading durations… 1,240 of 4,000", cap message "Sorted by the 5,000 durations read; 7,000 more were not read"), `FolderViewController.SetSort` → starts the fill when the sort column is lazy; the pane re-sorts when the fill completes (`BumpContentVersion` + `NotifyChanged`).
- Test: Core tests `SortingByALazyColumnFillsTheFolderThenSorts`, `SortingByALazyColumnBeyondTheCapSortsWhatIsKnownAndSaysSo`, `CancellingTheFillKeepsWhatWasRead`; source-shape tests for the App pieces; `appcheck` build.

- [ ] Tests first, implement, pass, commit.

### Task 11: Docs, changelog, checks, push

- [ ] `docs/CHANGELOG.md` Unreleased; `README.md` feature list (Details view columns); `docs/winui-migration/parity-gate.md` counts; `docs/WINUI_RUNTIME_VERIFICATION.md` if the smoke gains a check.
- [ ] `npm run check`, `npm run test:maintenance`, Linux C# harness (no new failures), `cargo fmt --all -- --check`, Windows-target clippy for the workspace, `appcheck` build.
- [ ] Push the branch; report what CI must still prove (Rust tests on Windows, the app).
