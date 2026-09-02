# Code Cleanup Candidates

Status: living cleanup log. The original pass was analysis-only; the decision log records follow-up cleanup passes that changed code.

These are structural cleanups that can be done without changing functionality or performance, if implemented as file/type splits, helper extraction with identical signatures, or dead-code removal.

Line counts are approximate (non-blank vs total lines differ). Generated IPC files should stay generator-owned: `Protocol.Generated.cs`, `NamedPipeJsonClient.Generated.cs`, `crates/simplefile-ipc/src/protocol_generated.rs`.

---

## Suggested first-pass order

1. Keep `npm run check` / `npm run check:release` green when command, context-menu, or parity rows change.
2. Completed in the current pass: split search glue into `MainWindow.Search.cs`.
3. Completed in the current pass: added `CommandAliasCatalog` so palette, context, overflow, and accelerator IDs share one router.
4. Completed in the current pass: moved matching filesystem conflict helpers into `simplefile-core::path_conflict` with focused behavior tests.
5. Completed in the current pass: removed stale dialog comments and replaced the reachable job-object source-string assertions with compiled behavior assertions.
6. Next cleanup candidates: `ExplorerWorkspace`, backend file splits, and dialog/helper deduplication that needs broader WinUI coverage.

For each finding, pick **A / B / C** before implementing.

---

## 1. `MainWindow` is still too large

**Files**

- `src-winui/SimpleFile.App/MainWindow.xaml.cs` (~3,100 lines)
- `src-winui/SimpleFile.App/MainWindow.Commands.cs` (~1,560)
- `src-winui/SimpleFile.App/MainWindow.Search.cs` (~90)
- `src-winui/SimpleFile.App/MainWindow.Transfer.cs` (~830)
- `src-winui/SimpleFile.App/MainWindow.OpenWith.cs` (~380)
- `src-winui/SimpleFile.App/MainWindow.xaml` (~1,490)

The shell is smaller than it was: preview rendering now lives in `PreviewPresenter`, dialog-driven file operations in `FileOperationDialogService`, search state in `SearchViewModel`, and search event/result glue in `MainWindow.Search.cs`. The remaining partials still do not match concerns cleanly.

- `xaml.cs` still owns session/IPC, workspace sync, dual-pane, sidebar, tabs, columns, archives, tags, marquee, clipboard, and settings.
- `Commands.cs` mixes shortcuts, command palette, context menus, Quick Look, properties, Git, and PowerShell.
- `Transfer.cs` mixes drag-drop with divider thumbs, pack/unpack, extract, advanced rename, and undo/redo.

Large methods include `SyncFromWorkspaceCore`, `ShowOpenWithChooserAsync`, `RunAppCommandAsync`, and `RunContextCommandAsync`. There are many `OnPrimary*` / `OnSecondary*` event twins.

**Options**

- **A (lowest risk):** Split into more partials on the same class (`Preview`, `Search`, `Sidebar`, `FileList`, `FileOps`, `Tools`, `Marquee`). Zero type changes; XAML event names stay put.
- **B:** Extract UserControls from `MainWindow.xaml` (sidebar, pane chrome, preview pane, toolbar) so the window only hosts layout.
- **C:** Extract real types (preview presenter, search host, file-op dialogs) and leave the window as a thin event router. More work; same behavior if existing methods move unchanged.

---

## 2. ViewModel cutover still has window glue to finish

**Files**

- `src-winui/SimpleFile.Core/SearchViewModel.cs`
- `src-winui/SimpleFile.Core/TransferViewModel.cs`
- `src-winui/SimpleFile.Core/ToolbarViewModel.cs`
- `src-winui/SimpleFile.Core/AppServices.cs`

Wired in `ConnectAsync` (`AppServices.Configure` then `_search` / `_transfer` / `_toolbar`) and now actively used by `MainWindow`.

`SearchViewModel` owns search state/results/cancellation, `TransferViewModel` owns operation identity/progress/cancellation, and `ToolbarViewModel` owns toolbar/status snapshots. Remaining cleanup is mostly structural: the window still hosts adapter methods, direct control writes, and a global `AppServices` container for three transients.

**Options**

- **A:** Finish the cutover: keep the window as UI binding/dispatch glue while ViewModels own search/transfer/toolbar behavior.
- **B:** Remove `AppServices` and construct the three ViewModels directly from the workspace; lower indirection if no broader DI plan exists.
- **C:** Leave the hybrid shape. Acceptable short term, but future command/search edits need extra care.

---

## 3. `ExplorerWorkspace.cs` is a domain god class

**File:** `src-winui/SimpleFile.Core/ExplorerWorkspace.cs` (~2,100 lines)

One type owns navigation streaming, dual-pane/tabs, settings persistence, bookmarks/recents, tags/smart folders, git, folder metrics, tree merge, clipboard/operation log, layout restore, pack/unpack, and cross-pane copy/move. Shared `_gate` + `RaiseChanged()` everywhere. `NavigatePaneAsync` and `OpenPathAsync` are large nested chunk-callback methods.

**Options**

- **A:** Partials on the same class (`Navigation`, `Tabs`, `Settings`, `Places`, `Tags`). Same lock/events; safest.
- **B:** Extract helpers that the workspace owns (`WorkspaceSettingsStore`, `PlacesController`, `PaneNavigator`) without changing the public API.
- **C:** Leave it. Readable enough if you only touch one method at a time.

---

## 4. `dispatch` is a copy-paste RPC switch

**Original file:** `crates/simplefile-service/src/dispatch.rs` (~1,800 lines); split target is `crates/simplefile-service/src/dispatch/`.

- Production ~1–1195; tests ~1196–end.
- ~35 one-off param structs (`PathParams`, `NameParams`, `RenameParams`, `TagCreateParams`, …).
- A ~700-line `match request.method.as_str()` with the same `parse_params` / `Ok` / `Err` / `application_error` pattern for 76 methods.
- Before cleanup, domain arms used string literals instead of generated `METHOD_*` from `protocol_generated.rs`.
- `HandshakeParams.client_name` is `#[allow(dead_code)]`.

**Options**

- **A:** Add `reply_ok` / `reply_err` helpers and keep the match. Shrinks noise, no routing change.
- **B:** Split the module: `params.rs`, `handlers.rs` (sync replies), `async_ops.rs` (list/copy/search/thumbs), `tests.rs`. Re-export `dispatch()`.
- **C:** Generate the match from `ipc/schema/v1`. Highest DRY, highest miss risk for host-owned vs async arms.

---

## 5. `archive.rs` mixes five archive jobs

**Original file:** `crates/simplefile-core/src/archive.rs` (~1,700–1,900 lines)
**Split target:** `crates/simplefile-core/src/archive/`

One file owns:

- virtual-path parsing (`split_archive_path`, `is_archive_virtual_path`)
- list zip/tar/rar
- extract + zip-slip / remap
- virtual FS mutate: copy/move/delete/rename/create
- create zip/tar/rar + `resolve_rar_binary`

RAR installer already lives in `rar.rs`. Two unique-name loops in the same file (`unique_sibling_path` and `unique_destination_path`).

**Options**

- **A:** Split into `archive/{mod,path,list,extract,mutate,create}.rs` with the same public fns re-exported.
- **B:** Also move `resolve_rar_binary` next to `rar.rs`.
- **C:** Leave as one file; it is cohesive as “archives” even if huge.

---

## 6. Three copy/conflict engines

**Files**

- `crates/simplefile-core/src/file_ops.rs`
- `crates/simplefile-service/src/progress.rs`
- `crates/simplefile-core/src/archive/`

Several private helpers existed in all three: `unique_destination_path`, `is_keep_both_action`, `resolve_destination`, `path_exists_no_follow`, `path_collision_key`, `create_dir_exclusive`.

The matching conflict helpers now live in `crates/simplefile-core/src/path_conflict.rs` and are reused by `file_ops` and service progress. They are **not all identical**, so unique-name loops remain local: progress also tracks `planned_destinations`, `file_ops` has its own sibling naming, and archive list/extract uniqueness often uses `exists()` while file operations use `symlink_metadata`.

**Options**

- **A (safe):** Dedup only helpers that already match byte-for-byte (`is_keep_both_action`, collision key). Leave unique-name loops alone until compared.
- **B:** Shared `path_conflict` module used by all three, with explicit variants for `exists()` vs `symlink_metadata` vs planned-dest set.
- **C:** Do not unify yet. Easy to change keep-both / skip / replace behavior.

---

## 7. `session` is connection I/O plus a job farm

**Original file:** `crates/simplefile-service/src/session.rs` (~1,200 lines); split target is `crates/simplefile-service/src/session/`.

`serve_connection` re-matches the entire `Dispatch` enum. Eight near-identical spawners (`spawn_install_update`, `spawn_folder_size`, `spawn_folder_item_count`, `spawn_folder_metrics`, `spawn_copy_move_with_progress`, `spawn_search_files`, `spawn_duplicate_check`, `spawn_disk_cleanup`) each do `scheduler.run_*` then write JSON. Framing (`read_frame`, writer loop) sits in the same file.

**Options**

- **A:** Extract `spawn_blocking_reply` and collapse the eight spawners.
- **B:** Split `session/{mod,jobs,io}.rs`.
- **C:** Leave; the spawners are verbose but local.

---

## 8. Dead Tauri-era `AppState` and unused helpers - completed

**Files / evidence**

- `crates/simplefile-core/src/state.rs` has been removed.
- Live watcher is `crates/simplefile-service/src/watcher.rs`.
- Live cancel flags are `SessionState` in `dispatch/mod.rs`.
- `notify` and `parking_lot` are no longer core dependencies for `state.rs`.
- The old `src-tauri/target/...` service lookup is gone; `ServiceLocator` looks in root `target` paths and `PATH`.

**Options**

- No current action. Keep this entry as a completion note and re-open only if live code grows new Tauri-era scaffolding.

---

## 9. Fat `ISimpleFileIpc` explodes tests

**Files**

- `src-winui/SimpleFile.Ipc/ISimpleFileIpc.cs` (~110 methods)
- `src-winui/SimpleFile.Core/IExplorerBackend.cs` (3 methods)
- `src-winui/SimpleFile.Tests/ExplorerWorkspaceTests.cs`
- `src-winui/SimpleFile.Tests/FileOperationServiceTests.cs`

`ISimpleFileIpc` is one interface for files, search, git, tags, RAR, updates, and archives. Tests used to implement the full IPC surface directly.

Previous independent full stubs:

- `FakeExplorerBackend` + `WorkspaceSettingsIpc` in `ExplorerWorkspaceTests.cs` (fake started after the test class)
- `StubIpc` in `FileOperationServiceTests.cs`

Current test double shape: `NullIpc` provides strict default throws, `ConfigurableIpc` centralizes the few handlers/dictionaries tests need, and `FakeExplorerBackend` lives in its own file.

**Options**

- **A:** Shared `NullIpc` / `ConfigurableIpc` base with default throws; tests override the few methods they need. Move `FakeExplorerBackend` to its own file.
- **B:** Split the interface (`IFileOps`, `ISearch`, `ITags`, …). Cleaner long-term; touches generated client + every stub.
- **C:** Leave; adding a method means updating three stubs.

---

## 10. Duplicate command routers

**Files**

- `src-winui/SimpleFile.App/MainWindow.Commands.cs` — `RunAppCommandAsync`, `RunContextCommandAsync`, `OnRootPreviewKeyDown`
- `src-winui/SimpleFile.App/MainWindow.xaml.cs` — accelerator handlers
- `src-winui/SimpleFile.Core/AppCommandCatalog.cs`
- `src-winui/SimpleFile.Core/ContextMenuBuilder.cs`

The same actions used to be mapped three times (`rename` / `ctx-rename` / F2; `delete` vs `ctx-delete-recycle`). `CommandAliasCatalog` now normalizes context-menu and overflow IDs into app-command IDs before `RunAppCommandAsync` handles them. Remaining cleanup: `SearchTextBoxFor(PaneId)` and friends take a pane and ignore it (always primary chrome), and `PromptAndCreateFolder` / `PromptAndCreateFile` are almost copies.

**Options**

- **A:** One dispatcher table plus aliases (`ctx-open` → `open`). Accelerators call the same IDs as the palette.
- **B:** Keep three routers; only extract shared `PromptAndCreateFolder` / `PromptAndCreateFile`.
- **C:** Leave. Risk is missing a shortcut when adding a command.

---

## 11. Other large files that should be split

### 11.1 `metadata.rs` (~900+ lines)

**Mix:** EXIF, PDF, audio, MP4 atoms, Office ZIP+XML.

**Options**

- **A:** Split `metadata/{mod,image,pdf,audio,video,office}.rs`.
- **B:** Leave.

### 11.2 `preview.rs` (~370+ lines)

**Mix:** text preview, thumbnails, ~120-arm MIME table. Also has its own `classify_extension` / `resolve_readable_path` copies.

**Options**

- **A:** Split `classify` / `text_image` / `thumbnail`.
- **B:** Leave.

### 11.3 `progress.rs` (~1,000+ lines)

**Mix:** operation registry + transfer + conflict helpers (see finding 6).

**Options**

- **A:** Split `registry.rs` + `transfer.rs`.
- **B:** Wait until finding 6 is decided.

### 11.4 `file_ops.rs` (~1,000+ lines)

**Mix:** CRUD, trash, batch rename, Windows delete/DACL, folder size. Module docs still say extracted from Tauri `fs_ops.rs`. `#[cfg(not(windows))]` stubs remain in a Windows-only product.

**Options**

- **A:** Split `create_delete` / `rename` / `copy_move` / `windows_delete` / `folder_metrics`.
- **B:** Leave DACL / unsafe Windows paths in place; only split the rest.

### 11.5 `models.rs` and `Models.cs` (~350 / ~550 lines)

**Mix:** every DTO in one bag (listing, drives, search, tags, cleanup, git, RAR, updater, preview, archives).

**Options**

- **A:** Files per domain, re-export so call sites stay `models::*`.
- **B:** Leave (JSON types are stable).

### 11.6 `ParityFeatures.cs` (~200 lines)

**Types in one file:** `BookmarkItem`, `FolderTreeItem`, `ClipboardHistoryEntry`, `OperationRecord`, `PlacesStore`, `TypeAheadBuffer`, `TypeAhead`, `ClipboardHistory`, `PhotoFolder`, `MarqueeSelection`, `FolderTree`.

**Options**

- **A:** One type (or tight pair) per file.
- **B:** Leave.

### 11.7 `FileOperationService.cs` (~450–510 lines)

**Mix:** ~80 IPC pass-throughs. Real logic is journaling + progress subscribe + cancel: `RunTransferAsync`, `DiskCleanupAsync`, `DuplicateCheckAsync`, `InstallUpdateAsync`. Cleanup/duplicate are copy-paste of the same subscribe/journal/try/finally.

**Options**

- **A:** Extract `RunJournaledAsync` / `WithProgressAsync` to kill copy-paste.
- **B:** Split by domain (`TransferOperations`, `ArchiveOperations`, `SettingsOperations`) behind one `FileOps` facade.

### 11.8 `AdvancedRename.cs` (~750 lines)

**Mix:** plan/preview/transform/sanitize/template/legacy in one static class.

**Options**

- **A:** Partials `Plan` / `Preview` / `Transform`.
- **B:** Leave.

### 11.9 `utils.rs` (~500–640 lines)

**Mix:** path validation, `FileEntry` builders, symlink recreate, dir counts, `hidden_command`.

**Options**

- **A:** Split `path_validate` / `file_entry` / `process`.
- **B:** Leave.

### 11.10 `cleanup.rs` (~530–600 lines)

**Mix:** duplicate scan + disk cleanup + a third `hex_encode`.

**Options**

- **A:** Split `duplicates.rs` + `large_files.rs`.
- **B:** Leave.

### 11.11 `settings_store.rs` (~210–250 lines)

**Mix:** app-data paths (including unused non-Windows `XDG`/`HOME`), SQLite open, **tags/file_tags schema + seed**, and settings get/set. `tags.rs` opens the same DB via `open_metadata_db`.

**Options**

- **A:** Split `paths` / `db` / `settings`; keep tags schema with `tags.rs`.
- **B:** Leave.

### 11.12 `native_accel.rs` (~400 lines)

**Mix:** portable vs x86_64 twins at every entry, then a long SIMD block.

**Options**

- **A:** Split `portable.rs` + `x86_64.rs`. Do not change `unsafe` / feature-detect.
- **B:** Leave.

### 11.13 `NamedPipeJsonClient.cs` (~650–740 lines)

**Mix:** connect, framing, receive loop, binary/JSON dispatch, **and** special-cased `ListDirectoryAsync` / `SearchFilesAsync`. Generated one-liners live in `NamedPipeJsonClient.Generated.cs`.

**Options**

- **A:** Move receive/framing into `NamedPipeJsonClient.Transport.cs` (same partial). Leave generated file alone.
- **B:** Leave.

### 11.14 `generate-ipc-bindings.mjs` (424 lines)

**Mix:** Rust + C# protocol + client emitters in one script.

**Options**

- **A:** Split emitters (Rust vs C#).
- **B:** Leave; it works.

### 11.15 `SettingsDialog.xaml.cs` (~470–510 lines)

**Mix:** category/search UI, bind/apply `UiSettings`, RAR installer, updater, GitHub link. `LoadSettingsAsync` re-reads the same IPC keys as `ExplorerWorkspace.LoadUiSettingsAsync`.

**Options**

- **A:** Split `SettingsDialog.Tools.cs` (RAR/updates), or child UserControls per category panel.
- **B:** Bind already-loaded `UiSettings` instead of a second IPC read (keep defaults/fallbacks).

### 11.16 `BackendSession.cs` (~320 lines)

**Mix:** job object, spawn `simplefile-service`, stderr log rotate, handshake, reconnect, **and** `IExplorerBackend`.

**Options**

- **A:** Partials `BackendSession.Process.cs` vs `BackendSession.Ipc.cs`.
- **B:** Split process supervisor vs session (medium: startup/reconnect timing).
- **C:** Leave.

### 11.17 `updater.rs` + `rar.rs`

**Mix:** both download a URL, SHA-256, `hex_encode`, then install. Updater adds Ed25519 + `get_app_about_info`. `check_rar_installed` just calls `archive::resolve_rar_binary`.

**Options**

- **A:** Share `hex_encode` only (see finding 13).
- **B:** Share HTTP download (timeouts/UA must stay identical — medium risk).
- **C:** Split updater into `about.rs` / `manifest.rs` / `download.rs`; keep rar installer separate.

### 11.18 Smaller mixed files

| File | Mix | Options |
| --- | --- | --- |
| `dir_list.rs` | std listing + Windows `FindFirstFileExW` + sort/filter | **A** `dir_list/windows.rs` · **B** leave |
| `drives.rs` | listing vs network probe vs display names | **A** optional `probe.rs` · **B** leave |
| `BinaryFrameCodec.cs` | encode/decode + nested writer | **A** partial split · **B** leave |
| `JsonRpcModels.cs` | JSON-RPC envelope **and** Handshake/Health/PathParams | **A** envelope vs handshake params · **B** leave |
| `OpenWithApplicationDiscovery.cs` | registry scrape in App | **A** keep; move chooser UI to `OpenWithDialog.xaml` · **B** leave |

**Not messy enough to rank:** `shell.rs`, `scheduler.rs`, `binary.rs`, `rpc.rs`, `frame.rs`, `git.rs`, `open_with.rs` (Rust), `watcher.rs`, `search.rs`.

---

## 12. Tests, dialogs, and source-shape checks

**Original test files**

- `src-winui/SimpleFile.Tests/ExplorerWorkspaceTests.cs` — tests + `FakeExplorerBackend` + `WorkspaceSettingsIpc`
- `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs` — source-string tripwires for shell polish regressions
- Historical: `DesktopPolishTests.cs` and `ParityFeaturesTests.cs` were split into focused Core test files.
- `src-winui/SimpleFile.Tests/NamedPipeJsonClientTests.cs`
- `src-winui/SimpleFile.Tests/FileOperationServiceTests.cs` — nested `StubIpc`

Current split keeps `ExplorerWorkspaceTests.cs`, `FileOperationServiceTests.cs`, and `NamedPipeJsonClientTests.cs` type-focused, moves `FakeExplorerBackend` / `NullIpc` / `ConfigurableIpc` / `InlineProgress` to shared helper files, and replaces `DesktopPolishTests.cs` plus `ParityFeaturesTests.cs` with Core-type test files such as `ContextMenuBuilderTests.cs`, `ToolbarOverflowPlannerTests.cs`, `AdvancedRenameTests.cs`, and `FolderTreeTests.cs`. `WinUiSourceShapeTests.cs` still contains WinUI source-substring tripwires where behavior is hard to reach without launching WinUI, but the job-object flag checks now assert compiled constants instead of source text.

**Dialog / host duplication**

- `ShowDuplicateCheckerAsync` / `ShowDiskCleanupAsync` in `MainWindow.xaml.cs` share the same scan-host skeleton (show-config → scan CTS → progress enqueue → results).
- `FormatSize` is copied in `ArchiveViewerDialog`, `ExtractArchiveDialog`, `DuplicateCheckerDialog`, and `DiskCleanupDialog` — and the formats differ (`F1` vs `0.##`).
- ViewModels live inside dialog code-behind (`DuplicateCheckerDialog`, `DiskCleanupDialog`, `AdvancedRenameDialog`, `TagPickerDialog`).

**Options**

- **A:** Completed for the old dump files: tests now match Core types and share one IPC stub family.
- **B:** One `FormatSize` helper **per format**, not one helper for all four (do not unify `F1` with `0.##`).
- **C:** Extract a shared “scan while dialog is open” helper for duplicates/cleanup. Move dialog VMs to sibling files.
- **D:** Replace source-string assertions with behavior tests when the behavior is reachable without launching WinUI.

---

## 13. Small copy-pastes

Low risk; skip if the pass is only file splits.

- `resolve_readable_path` copied in `checksum.rs`, `compare.rs`, `metadata.rs`, `open_with.rs`, `preview.rs`.
- `hex_encode` copied in `checksum.rs`, `cleanup.rs`, `rar.rs`, `updater.rs`.
- `APP_IDENTIFIER = "com.simplefile.desktop"` in `simplefile-ipc/src/lib.rs`, `updater.rs`, and `settings_store.rs`.
- `PromptAndCreateFolder` / `PromptAndCreateFile` are the same dialog.

**Options**

- **A:** One shared helper with the current signature.
- **B:** Leave; each is 6–15 lines.

---

## 14. IPC generated vs handwritten mix

**Files**

- `crates/simplefile-ipc/src/lib.rs` — handwritten constants; crate docs still say “until a C# project exists”
- `crates/simplefile-ipc/src/protocol_generated.rs` — generated method names / binary tags
- `src-winui/SimpleFile.Ipc/Protocol.cs` vs `Protocol.Generated.cs`
- `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.cs` vs `NamedPipeJsonClient.Generated.cs`

Service dispatch ignores most generated `METHOD_*` constants (finding 4).

**Options**

- **A:** Keep generated files isolated; refresh crate docs; handwritten `lib.rs` only re-exports + framing/RPC.
- **B:** Point dispatch match arms at generated `METHOD_*`.
- **C:** Also generate error-code constants (must keep `schema_consistency.rs` green). Do not hand-edit generated files.

---

## Decision log

Record chosen options here when a cleanup pass starts.

| Finding | Chosen option | Notes |
| --- | --- | --- |
| 1 MainWindow | C + A | Completed: extracted preview rendering/actions into `PreviewPresenter`, dialog-driven file operations into `FileOperationDialogService`, current search event/result glue into `MainWindow.Search.cs`, and app-command routing into `MainWindow.CommandRouting.cs`; `MainWindow` now delegates those workflows while preserving XAML event names. |
| 2 Unused ViewModels | B | Completed: finished the ViewModel cutover so `SearchViewModel` owns live search state/results/cancellation, `TransferViewModel` owns transfer operation identity/progress/cancellation, and `ToolbarViewModel` owns toolbar/status snapshots; removed the app-side `SearchHost` duplicate. |
| 3 ExplorerWorkspace | B | Completed for profiles/layouts: extracted workspace profile persistence and saved layout persistence into owned services while keeping the existing `ExplorerWorkspace` public API. |
| 4 dispatch.rs | B | Completed: split the service dispatcher into `dispatch/{mod,params,handlers,async_ops,tests}.rs`, moved async arm construction behind `async_ops`, kept `dispatch()` re-exported from the module, replaced domain match arms with generated `METHOD_*` constants, and updated schema/parity checks for the split module. |
| 5 archive.rs | A | Completed: split archive handling into `archive/{mod,path,list,extract,mutate,create,tests}.rs` with public functions re-exported from `archive/mod.rs`; kept `resolve_rar_binary` on the archive API and did not move RAR installer behavior in this pass. |
| 6 Copy/conflict engines | B | Completed for matching helpers: added `simplefile-core::path_conflict` for no-follow existence, collision keys, same-entry checks, keep-both aliases, and exclusive directory creation; left divergent unique-name loops local. |
| 7 session.rs | B | Completed: split the session runtime into `session/{mod,jobs,io,tests}.rs`; `serve_connection` now stays in `mod.rs`, pipe framing/outbound batching lives in `io.rs`, spawned service jobs live in `jobs.rs`, session tests moved to `tests.rs`, and shared spawned-job response helpers reduce repeated scheduler/result handling. |
| 8 Dead AppState | A | Completed: removed dead core `state.rs`, unused helpers/deps, stale `src-tauri` service lookup, and live-code Tauri-era comments. |
| 9 ISimpleFileIpc / tests | A | Completed: added shared `NullIpc` / `ConfigurableIpc` test doubles, removed the per-file `WorkspaceSettingsIpc` and `StubIpc` full-interface stubs, and moved `FakeExplorerBackend` into its own helper file. |
| 10 Command routers | A | Completed: added `CommandAliasCatalog`, routed app/context/overflow aliases through `RunAppCommandAsync`, and added tests for shared alias normalization. |
| 11 Other splits | A | Completed for the transfer/file-op pair: split `progress::OperationRegistry` into `progress/registry.rs`, and split folder metrics plus metadata preservation out of `file_ops.rs` while preserving public re-exports. |
| 12 Tests / dialogs | A + D + B | Completed the test-file part: split `DesktopPolishTests.cs` and `ParityFeaturesTests.cs` into type-focused test classes, reused the shared IPC fake from Finding 9, replaced reachable job-object flag source checks with compiled assertions, and extracted the duplicate-checker/disk-cleanup scan skeleton into `FileOperationDialogService.Scans.cs`. |
| 13 Small copy-pastes | | |
| 14 IPC generated mix | A | Started Core-side facade cleanup: added small file/settings/tag/smart-folder operation interfaces over the generated IPC surface so Core consumers can depend on domain-sized contracts without editing generated files. |
