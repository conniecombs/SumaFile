# SumaFile Full-Codebase Audit — 2026-11-23

> **Status, updated 2026-11-23.** All **Critical** and **High** Bugs (`B1–B13`) and Performance Bottlenecks (`P1–P9`) identified in this audit have been repaired and verified against the full Debug/Release build, C# test suite, Rust workspace test suite, Clippy, crate packaging, and packaged release smoke tests. Medium bugs (`B14`), Medium bottlenecks (`P10`), dead-code cleanup (`R1–R3`), remaining UI polish (`U2–U7`), and architectural Rust upgrades (`A1–A9`) remain tracked as open follow-up work below.
>
> **Follow-up, 2026-11-24 (release 1.2.0).** The remaining `B14` items, most of `P10`, `R1`, and parts of `R2`/`R3` are done; each is marked below, and items that were looked at and left are marked with the reason. `U2–U7` and `A1–A9` are unchanged and stay open. This follow-up was developed on Linux: the transfer crate was tested natively and type-checked for Windows, the other Rust crates were tested through a local Linux port of their Windows-only modules, and the C# libraries and test suite were built and run on Linux. `SimpleFile.App` cannot be built there, so its changes are confirmed by the Windows CI run for the release, not by the table below, which records the 2026-11-23 run.

**Scope:** All 11 Rust workspace crates (`simplefile-core`, `simplefile-ipc`, `simplefile-service`, `sumafile-transfer`, `sumafile-plugin`, bundled `git`, `sevenzip`, and `remote` backends, plus test/template fixtures), all WinUI 3 / .NET 10 projects (`SimpleFile.App`, `SimpleFile.Core`, `SimpleFile.Ipc`, `SimpleFile.PluginHost`, `SimpleFile.PluginSdk`, bundled `Git` & `Remote` UI plugins, `SimpleFile.Tests`), and packaging/CI scripts.

Every finding below was verified directly against live source code and cross-checked against [ROADMAP.md](ROADMAP.md), [UI_AUDIT_2026-11-05.md](UI_AUDIT_2026-11-05.md), and [RUST_BACKEND_AUDIT_2026-11-05.md](RUST_BACKEND_AUDIT_2026-11-05.md).

## Verification Summary (Post-Repair)

| Check | Result |
| :--- | :--- |
| Static & schema gates (`check:ipc-generated`, `check:open-with-policy`, `check:ipc-schema`, `check:identity`, `check:updater`, `check:provider-surface`, `check:windows-assets`, `check:winui-packaging`, `check:winui-parity-gate`, `check:dotnet-packages`) | All passed |
| `dotnet build src-winui/SimpleFile.sln -c Debug` + Release `SumaFile.exe` / `SumaFile.Remote` / `SumaFile.Git` | Succeeded, 0 warnings, 0 errors |
| `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug` | 1,719 passed, 0 failed, 0 skipped |
| `cargo fmt --all -- --check` | Passed (0 diffs) |
| `cargo test --locked --all-features` (all 11 workspace crates) | All passed, 0 failed |
| `cargo clippy --locked --all-targets --all-features -- -D warnings` | Passed, 0 warnings, 0 errors |
| `cargo package --locked -p simplefile-ipc -p sumafile-plugin` | Passed |
| `scripts/smoke-winui-file-ops.ps1` (packaged Release payload) | Passed (file ops, 7-Zip, Remote, Git plugins) |

---

## Executive Priority Overview

| Pillar | Scope | Status |
| :--- | :--- | :--- |
| **1. Bugs** | **3 Critical (`B1–B3`), 10 High (`B4–B13`), 1 Consolidated Medium (`B14`)** | `B1–B14` **Complete** |
| **2. Bottlenecks** | **1 Critical (`P1`), 8 High (`P2–P9`), 1 Consolidated Medium (`P10`)** | `P1–P9` **Complete**; `P10.2–P10.4` **Complete**; `P10.1` and `P10.5` open |
| **3. Redundant Code** | **~1,200+ LOC dead (`R1`), duplicated C#/Rust subsystems (`R2–R3`)** | `R1` **Complete**; `R2` and `R3` partially complete (see the sub-checklists) |
| **4. UI Improvements** | **7 workflow & polish groups (`U1–U7`)** | `U1` **Complete**; `TransferProgressWindow` Details vertical resize **Complete**; `U2–U7` open |
| **5. Rust Backend** | **9 architectural upgrades (`A1–A9`)** | Open (`A7` SQLite WAL/NORMAL/`prepare_cached` foundation in place) |

---

## 1. Bugs

### Status Checklist

- [x] **B1.** [Critical — Rust Watcher] Windows `ModifyKind::Any` drops, Dual-Pane multi-root watches, and post-storm starvation
- [x] **B2.** [Critical — Remote Plugin] FTP/FTPS `TYPE I` binary mode, `MLSD`/`LIST` timestamp mismatch, passive NAT workaround, and stream `abort()`
- [x] **B3.** [Critical — Transfer Engine] ReFS / Dev Drive 128-bit File ID collision (`0xFFFFFFFF_FFFFFFFF`) and deterministic staging hashes
- [x] **B4.** [High — C# Dual-Pane] Pane swap and closing left pane corrupting `ExplorerPane.Listing`
- [x] **B5.** [High — C# Clipboard] Clipboard History `"Paste this"` and internal fallback self-wipe
- [x] **B6.** [High — C# Undo/Redo] Collision-renamed move Undo/Redo corruption and missing `PackIntoFolder`/`UnpackFolder` undo entries
- [x] **B7.** [High — C# PathRules / Breadcrumbs] UNC share root handling (`\\server\share`) and `recycle-bin:` tab labels
- [x] **B8.** [High — Plugin SDK Security & Lifecycle] Creator-only pipe DACL, `reject_remote_clients`, 64 KiB pre-handshake cap, and single-host hijack prevention *(note: per-launch stdin token and pre-host multi-client exit hardening remain follow-ups)*
- [x] **B9.** [High — Remote UI] Ref-counted `RemoteManagerViewModel.IsBusy` and concurrent transfer cancellation guard
- [x] **B10.** [High — Sidebar & Tags] Collapsible indented Folder Tree, toggleable/clearable Label Filter, no `DefaultTags` resurrection, and case-insensitive tag keys
- [x] **B11.** [High — Rust Path, Drive & Duplicate-Scan Bugs] `\\?\C:\` local vs `\\?\UNC\` network classification, removable/RAM-disk volume details, and hardlink deduplication
- [x] **B12.** [High — Git Plugin] Unicode-safe case-preserving `repo_relative_path` and staged-rename / untracked-directory `discard` & `diff`
- [x] **B13.** [High — Built-In & 7-Zip Archives] 7-Zip strict exit code `0`, RAR symlink blocking, read-only cleanup, work-dir stem truncation, and batched multi-delete
- [x] **B14.** [Medium — Consolidated Edge-Case & State Bugs] Completed (see sub-checklist below)

---

### [x] B1. [Critical — Rust Watcher] 100% of Windows In-Place File Modifications Dropped, Secondary Pane Unwatched, and Post-Storm Starvation
- **Locations:** [watcher.rs](../crates/simplefile-service/src/watcher.rs), [listing_views.rs](../crates/simplefile-service/src/listing_views.rs), [listing_jobs.rs](../crates/simplefile-service/src/session/listing_jobs.rs), [NamedPipeJsonClient.cs](../src-winui/SimpleFile.Ipc/NamedPipeJsonClient.cs), [FileOperationService.cs](../src-winui/SimpleFile.Core/FileOperationService.cs), [MainWindow.xaml.cs](../src-winui/SimpleFile.App/MainWindow.xaml.cs).
- **Mechanism:**
  1. In `notify` 8.x (`ReadDirectoryChangesW`), `FILE_ACTION_MODIFIED` maps to `EventKind::Modify(ModifyKind::Any)`, which was previously ignored, dropping 100% of in-place file edits.
  2. Single-slot `WatcherState` unwatched the first pane whenever Dual-Pane switched focus.
  3. Post-storm coalescing could starve UI flushes under continuous trickle writes, keyed paths case-sensitively, failed on root-drive storm refreshes, and ran storm enumerations synchronously on the coalescer thread.
- **Resolution:** Mapped `ModifyKind::Any | Data(_) | Metadata(_) | Other` to `"modify"`; added multi-directory `watch_directories` for Dual-Pane; blocked idle coalescer on `recv()` with a hard `500 ms` `WATCHER_MAX_BATCH_AGE` anti-starvation deadline and Windows case-folded keys; fixed root-drive storm refreshes; offloaded storm enumerations onto `scheduler.run_general`; and watched both visible local panes while unwatching on network, archive, or `recycle-bin:` paths. Verified with live Rust test `watcher::live_tests::in_place_file_modification_and_multi_root_watches_emit_events`.

### [x] B2. [Critical — Remote Plugin] FTP/FTPS Missing `TYPE I` Binary Mode, `LIST` vs `MLST` Timestamp Mismatch Breaking Folder Downloads, and Passive NAT Failure
- **Locations:** [ftp.rs](../plugins/remote/backend/src/ftp.rs), [provider.rs](../plugins/remote/backend/src/provider.rs), [transfer.rs](../plugins/remote/backend/src/transfer.rs).
- **Mechanism:**
  1. `FtpClient::connect_sync` never issued `TYPE I` (`FileType::Binary`), leaving servers in RFC 959 ASCII mode.
  2. Recursive FTP downloads listed entries via `LIST` (minute/local-TZ precision) and verified via `MLST` (UTC second precision), causing `"Remote source changed during download"` failures.
  3. Passive NAT workaround was disabled, and failed/cancelled streams finalized instead of aborting.
- **Resolution:** Issued `transfer_type(FileType::Binary)` immediately after FTP/FTPS login; enabled `set_passive_nat_workaround(true)`; upgraded FTP `list_directory` to prefer `MLSD` with `500|501|502|504` fallback to `LIST` plus `list_matches_stat_precision` baselining; and called `abort(stream)` on failed/cancelled FTP transfers.

### [x] B3. [Critical — Transfer Engine] ReFS / Windows 11 Dev Drive 128-Bit File ID Collision (`0xFFFFFFFF_FFFFFFFF`) During Crash Recovery
- **Locations:** [staging.rs](../crates/sumafile-transfer/src/staging.rs), [journal.rs](../crates/sumafile-transfer/src/journal.rs).
- **Mechanism:** Legacy 64-bit `GetFileInformationByHandle` returns `FILE_INVALID_FILE_ID` (`u64::MAX`) on ReFS / Windows 11 Dev Drives for 128-bit IDs, causing unrelated files to collide in crash recovery and risking deletion of `.sumafile-replace-backup`.
- **Resolution:** Upgraded `FileIdentity` to `(u64, u128)` via `GetFileInformationByHandleEx(FileIdInfo)`, rejecting `0` and legacy `u64::MAX` sentinels on fallback and backward-compatible 2-tuple/3-tuple serde, and replaced `DefaultHasher` with deterministic 64-bit FNV-1a for staging paths.

### [x] B4. [High — C# Dual-Pane] Swapping Panes or Closing the Left Pane Corrupts `ExplorerPane.Listing` and Overwrites File Lists
- **Locations:** [ExplorerPane.cs](../src-winui/SimpleFile.Core/ExplorerPane.cs), [DualPaneTabController.cs](../src-winui/SimpleFile.Core/WorkspaceControllers/DualPaneTabController.cs), [ListingViewController.cs](../src-winui/SimpleFile.Core/WorkspaceControllers/ListingViewController.cs).
- **Resolution:** `SwapContents` now swaps `ListingViewId`, `Listing`, `ListingViewUnavailable`, `ListingSortsFolderSizes`, `ListingInProgress`, and `PendingListingDeltas`, increments `ListingEpoch`, and calls `BumpContentVersion()`. `SwapFilePanes` notifies the workspace and restarts `StartFill` on swapped partial views; `ListingViewController.Reconcile` skips `Secondary` when `!DualPaneEnabled`.

### [x] B5. [High — C# Clipboard] Clipboard History "Paste this" and Internal Fallback Wipe `ClipboardState` Before Reading It
- **Locations:** [ClipboardState.cs](../src-winui/SimpleFile.Core/ClipboardState.cs), [MainWindow.FileOperations.cs](../src-winui/SimpleFile.App/MainWindow.FileOperations.cs), [MainWindow.InspectionDialogs.cs](../src-winui/SimpleFile.App/MainWindow.InspectionDialogs.cs).
- **Resolution:** Added `IsSystemSynced` and `MarkLocalOnly()` on `ClipboardState` so Clipboard History `"Paste this"` (`systemSynced: false`) and failed OS clipboard exports are never cleared by `ReconcileWith(systemStamp)`.

### [x] B6. [High — C# Undo/Redo] Collision-Renamed Moves Corrupt Filenames on Undo & Move Wrong File on Redo; `PackIntoFolder` / `UnpackFolder` Push No Undo Entry
- **Locations:** [UndoStack.cs](../src-winui/SimpleFile.Core/UndoStack.cs), [WorkspaceFileOpsController.cs](../src-winui/SimpleFile.Core/WorkspaceControllers/WorkspaceFileOpsController.cs).
- **Resolution:** Tracked actual collision-renamed destinations/sources dynamically across `PushCopy` and `PushMove` Undo/Redo, restored original filenames via `RenameAsync` on `PushMove.Undo`, and recorded composite Undo/Redo entries in `PackIntoFolderAsync` and `UnpackFolderAsync`.

### [x] B7. [High — C# PathRules / Breadcrumbs] UNC Paths (`\\server\share`) Lose `\\` Prefix & Root Detection, and `recycle-bin:` Tabs Render as `"/"`
- **Locations:** [PathRules.cs](../src-winui/SimpleFile.Core/PathRules.cs), [BreadcrumbBuilder.cs](../src-winui/SimpleFile.Core/BreadcrumbBuilder.cs), [ExplorerPane.cs](../src-winui/SimpleFile.Core/ExplorerPane.cs).
- **Resolution:** Added `PathRules.IsUncRoot` and `TrySplitUncRoot` (`\\server\share`), emitted valid navigable UNC root + child segments in `BreadcrumbBuilder.FromPath`, preserved `"recycle-bin:"` in `TrimTrailingSeparators`, and returned `"Recycle Bin"` from `Basename` (and therefore `ExplorerPane.Title`).

### [x] B8. [High — Plugin SDK Security & Lifecycle] Unauthenticated Named Pipes Without Creator-Only DACL Allow Host Hijack and Pre-Handshake DoS
- **Locations:** [server.rs](../crates/sumafile-plugin/src/server.rs), [host.rs](../crates/sumafile-plugin/src/host.rs).
- **Resolution:** Applied `.reject_remote_clients(true)`, `.write_dac(true)` with a creator-only SID DACL (`apply_creator_only_dacl`), a `64 KiB` pre-handshake frame cap (`PRE_HANDSHAKE_MAX_FRAME_BYTES`), and single-host hijack rejection in `BackendState::set_host` / `Connection::request`.
- **Follow-up (optional hardening):**
  - [ ] Add a per-launch stdin session token to `plugin.handshake` (matching `simplefile-service`).
  - [ ] Keep multi-client plugin servers (`accepts_multiple_clients = true`) alive if an auxiliary service client disconnects before the primary host connects.

### [x] B9. [High — Remote UI] Non-Refcounted `RemoteManagerViewModel.IsBusy` Cleared by Local Folder Clicks Mid-Transfer, Breaking Transfer Cancellation
- **Locations:** [RemoteManagerViewModel.cs](../plugins/remote/ui/SumaFile.Remote.Model/RemoteManagerViewModel.cs).
- **Resolution:** Replaced direct `IsBusy` overwrites with ref-counted `EnterBusy()` / `ExitBusy()` and guarded `RunTransfersAsync` against concurrent `_transferCancellation` overwrite. *(Alphabetical `ReplaceLocalEntries`/`ReplaceRemoteEntries` order was retained to preserve the pinned 1.0.4 sort contract in `RemoteManagerViewModelTests`.)*

### [x] B10. [High — Sidebar & Tags] Uncollapsible Sidebar Folder Tree, Irreversible Label Filter Trap, Tag Resurrection, and Case-Sensitive Tag Keys
- **Locations:** [FolderTreeController.cs](../src-winui/SimpleFile.Core/WorkspaceControllers/FolderTreeController.cs), [ParityFeatures.cs](../src-winui/SimpleFile.Core/ParityFeatures.cs), [SidebarView.xaml](../src-winui/SimpleFile.App/SidebarView.xaml), [MainWindow.Places.cs](../src-winui/SimpleFile.App/MainWindow.Places.cs), [MainWindow.PaneContext.cs](../src-winui/SimpleFile.App/MainWindow.PaneContext.cs), [PaneContextBar.xaml.cs](../src-winui/SimpleFile.App/PaneContextBar.xaml.cs), [EmptyPaneState.cs](../src-winui/SimpleFile.Core/EmptyPaneState.cs), [WorkspaceTagController.cs](../src-winui/SimpleFile.Core/WorkspaceControllers/WorkspaceTagController.cs), [tags.rs](../crates/simplefile-core/src/tags.rs).
- **Resolution:** Preserved nested subtrees, `Depth * 14` indentation, chevron state, and collapse behavior in `FolderTreeController`, `SidebarView.xaml`, and `MainWindow.Places.cs`; toggled active label filters off on repeat click and surfaced a `Label: <Name>` chip + clear action in `PaneContextBar` and label empty-state messaging in `EmptyPaneState`; stopped `DefaultTags` resurrection after deletion; and made file tag keys case-insensitive (`StringComparer.OrdinalIgnoreCase` + SQLite `COLLATE NOCASE`).

### [x] B11. [High — Rust Path, Drive & Duplicate-Scan Bugs] `\\?\C:\` Misclassified as Network Share, Removable/RamDisk Drives Zeroed Out, and Hardlinks Flagged as Duplicates
- **Locations:** [utils.rs](../crates/simplefile-core/src/utils.rs), [path_conflict.rs](../crates/sumafile-transfer/src/path_conflict.rs), [drives.rs](../crates/simplefile-core/src/drives.rs), [cleanup.rs](../crates/simplefile-core/src/cleanup.rs), [plan.rs](../crates/simplefile-service/src/progress/plan.rs).
- **Resolution:** Fixed `is_network_path` (`\\?\C:\` local vs `\\?\UNC\` network) and `wide_path` slash normalization; enabled volume label & free/total space queries for `DRIVE_RAMDISK` and non-floppy (`A:`/`B:`) `DRIVE_REMOVABLE` drives; and deduplicated hardlinks via `file_identity` (plus added a partial-hash pre-pass in `scan_disk_cleanup`). *(Component-prefix comparison in `plan.rs::same_volume` was intentionally preserved because single-drive move tests in `progress.rs` use a verbatim destination prefix to force cross-volume streaming.)*

### [x] B12. [High — Git Plugin] Non-ASCII Repo Root Panics/Corrupts Pathspecs and `discard` Fails on Staged Renames & Untracked Subtrees
- **Locations:** [paths.rs](../plugins/git/backend/src/paths.rs), [commands.rs](../plugins/git/backend/src/commands.rs).
- **Resolution:** Rewrote `repo_relative_path` case-insensitive root prefix matching to walk character-by-character and preserve exact relative pathspec casing without panicking on Unicode case-expansion; fixed `discard` and `diff` on staged renames/copies (`original_path`) and files inside untracked directories (`path_is_within`). *(Direct-child-only `folder_decorations` behavior in `decorations.rs` was kept unchanged to preserve pinned tests in `plugins/git/backend/tests/repo.rs` and `GitPluginBackendTests.cs`.)*

### [x] B13. [High — Built-In & 7-Zip Archives] 7-Zip Exit Code `1` Treated as Success, Unchecked RAR Symlinks, Read-Only Cleanup Failure, and Multi-Delete N-Repacks
- **Locations:** [sevenzip/run.rs](../plugins/sevenzip/backend/src/run.rs), [sevenzip/ops.rs](../plugins/sevenzip/backend/src/ops.rs), [extract.rs](../crates/simplefile-core/src/archive/extract.rs), [mutate.rs](../crates/simplefile-core/src/archive/mutate.rs), [path.rs](../crates/simplefile-core/src/archive/path.rs), [recycle_bin.rs](../crates/simplefile-core/src/recycle_bin.rs).
- **Resolution:** Added `run_7za_strict` (requiring exit code `0` for `extract`, `create`, `rename`, `delete`, and `add`); blocked extracted RAR symlinks/junctions (`ensure_no_link_ancestors` + `reject_extracted_link`); cleared `readonly` attributes recursively during temp cleanup and built-in deletes; truncated `WorkDir::beside` archive stem to 64 chars; batched built-in `delete_archive_entries` into a single unpack/repack pass per archive; and returned `Err` instead of silently overwriting on 9,999-collision exhaustion across archive and Recycle Bin helpers.

### [x] B14. [Medium — Consolidated Verified Edge-Case & State Bugs]
- [x] **B14.1 Watcher RPC spam inside archives & `recycle-bin:`:** Fixed in [MainWindow.xaml.cs](../src-winui/SimpleFile.App/MainWindow.xaml.cs) and [ArchivePaths.cs](../src-winui/SimpleFile.Core/ArchivePaths.cs) (`IsInsideArchivePath` + `IsRecycleBinPath` + `PathIsNetwork` unwatch).
- [x] **B14.2 Quick Look `WebView2` `ArgumentNullException`, leaked `MediaSource` file locks, and unhardened Quick Look `CoreWebView2.Settings`:** Fixed in [PreviewPresenter.Html.cs](../src-winui/SimpleFile.App/PreviewPresenter.Html.cs) and [PreviewPresenter.Media.cs](../src-winui/SimpleFile.App/PreviewPresenter.Media.cs). The Quick Look PDF view gets the pane's lockdown before the file loads and is closed instead of being given a null source; Quick Look and pane media sources are disposed when released.
- [x] **B14.3 F5 / F6 `CopyOrMoveToOtherPaneAsync` post-await pane race & missing self-drop check:** Fixed in [MainWindow.Transfer.cs](../src-winui/SimpleFile.App/MainWindow.Transfer.cs) with `PathRules.AllLieDirectlyIn`. The workspace and destination pane are captured before the transfer, a copy or move onto the sources' own folder is refused, and the pane is refreshed only if it still shows the destination.
- [x] **B14.4 Backend reconnect permanently disabling paged `ListingView`s:** Fixed with `ExplorerWorkspace.RestoreListingViews`, called from `ReconnectBackendAsync` in [MainWindow.xaml.cs](../src-winui/SimpleFile.App/MainWindow.xaml.cs).
- [x] **B14.5 Tokio async worker starvation & leaked cancel tokens in `fact_jobs.rs`:** Fixed in [fact_jobs.rs](../crates/simplefile-service/src/session/fact_jobs.rs) and [session/mod.rs](../crates/simplefile-service/src/session/mod.rs).
- [x] **B14.6 Type-ahead keyboard navigation ignoring search results & multi-selection:** Fixed with `ExplorerWorkspace.MatchTypeAheadPath(character, pane, searchResults)`. The match runs over the pane that got the key and over its search results when it shows them, and it replaces a multi-selection.
- [x] **B14.7 Keyboard shortcut & preview toggle state bugs (`Alt+P` responsive collapse, `Ctrl+1..9` under open modal dialogs):** Fixed in [MainWindow.Shortcuts.cs](../src-winui/SimpleFile.App/MainWindow.Shortcuts.cs). `Alt+P` records the request to open the pane as the toolbar button does, and the tab-jump keys leave the key to an open dialog.
- [x] **B14.8 Rust metadata/preview/listing/transfer edge cases:**
  - [x] `ListingView::same_entry` now checks `created`, `accessed`, and `attributes` ([listing_view.rs](../crates/simplefile-core/src/listing_view.rs)).
  - [x] `Permit::drop` made poison-safe ([resources.rs](../crates/sumafile-transfer/src/resources.rs)) and `staging_path_for` switched to deterministic FNV-1a ([staging.rs](../crates/sumafile-transfer/src/staging.rs)).
  - [x] Dotfiles are classified by `utils::effective_extension`, so `.gitignore` and `.env` reach the text arms in `preview.rs` and `metadata.rs`.
  - [x] PDF metadata strings with a UTF-16BE or UTF-8 byte-order mark are decoded (`metadata.rs`).
  - [x] `open_with.rs` checks the deny list against the resolved program name as well as the requested one, and leaves a materialised temporary file in place when the program exits within three seconds, which means it handed the file to an instance already running; the day-old sweep removes it later.
  - [x] `smart_folders.rs` serialises its read-modify-write on the store.

---

## 2. Performance Bottlenecks

### Status Checklist

- [x] **P1.** [Critical — Rust Hot Path] Check archive/placeholder extension before `is_file()` stat
- [x] **P2.** [High — C# Virtualization] Bound `DetailsFileListView` / Transfer / Marquee container walks to realized indices
- [x] **P3.** [High — C# Paging & Selection] Pre-sized `FillAsync` page list and `O(N)` `DetailsFileListView` selection sync
- [x] **P4.** [High — C# Workspace Sync] Coalesced workspace sync, inactive secondary-pane skip, `O(1)` cut lookup, cached `UserDataPaths`, and fast timestamp span parser
- [x] **P5.** [High — C# UI-Thread Stalls] Known metadata thumbnail keys, `Bitmap.LockBits` + gated icons, cached `Open With`, off-thread `WSearch` + `System.FileAttributes`, reused measurement `TextBlock`, non-blocking UNC Omnibar, and precompiled `AdvancedRename` regexes
- [x] **P6.** [High — IPC & SQLite] Dirty-key `WorkspaceSettingsStore`, SQLite `WAL` / `NORMAL` / `prepare_cached`, cached `metadata_db_path()`, batched fact writes, and persistent `ProfileStore` connection
- [x] **P7.** [High — Rust Search] Zero-alloc ASCII filtering, raw `SystemTime` bounds before formatting, and 8 KiB NUL sniffing + UTF-16 BOM content search
- [x] **P8.** [High — Remote SFTP & Local Transfers] Raised SFTP window/packet concurrency, dropped redundant upload/rename `stat` RTTs, and skipped idle checker threads
- [x] **P9.** [High — Git & 7-Zip Plugins] O(1) `NtResumeProcess`, bounded pipe capture, and `-bsp1` stdout buffer skip
- [~] **P10.** [Medium — Enumeration, Cache, Metadata, Compare & Build Script Bottlenecks] `P10.2–P10.4` completed; `P10.1` and `P10.5` open (see sub-checklist below)

---

### [x] P1. [Critical — Rust Hot Path] `split_archive_path` & `unavailable_archive_note` Stat Every Ancestor Directory Before Checking Extension
- **Locations:** [path.rs](../crates/simplefile-core/src/archive/path.rs), [placeholders.rs](../crates/simplefile-core/src/archive/placeholders.rs).
- **Resolution:** Flipped short-circuit order to check archive/unavailable suffixes before calling `is_file()`, eliminating 8–20 wasted `GetFileAttributesExW` kernel syscalls per normal path operation.

### [x] P2. [High — C# Virtualization] `O(N)` `ContainerFromIndex` / `ContainerFromItem` Loops Over All 100k Rows
- **Locations:** [DetailsFileListView.cs](../src-winui/SimpleFile.App/DetailsFileListView.cs), [MainWindow.Transfer.cs](../src-winui/SimpleFile.App/MainWindow.Transfer.cs), [MainWindow.Marquee.cs](../src-winui/SimpleFile.App/MainWindow.Marquee.cs).
- **Resolution:** Added `DetailsFileListView.RealizedContainers(ListViewBase)` (`FirstVisibleIndex..LastVisibleIndex`) and replaced all five `O(N)` full-list container loops with `O(visible)` iteration.

### [x] P3. [High — C# Paging & Selection] `O(N²)` Array Spread in `ListingViewController.FillAsync` & `O(S × N)` Selection Sync in `DetailsFileListView`
- **Locations:** [ListingViewController.cs](../src-winui/SimpleFile.Core/WorkspaceControllers/ListingViewController.cs), [DetailsFileListView.cs](../src-winui/SimpleFile.App/DetailsFileListView.cs).
- **Resolution:** Replaced per-page `[.. pane.Entries, .. page.Entries]` spreads with a pre-sized `staged` `List<FileEntry>` in `FillAsync`, and added an `O(1)` `_selectedPathSet` plus `O(N)` `SetSelection` / `SelectAll()` fast path in `DetailsFileListView`.

### [x] P4. [High — C# Workspace Sync] Uncoalesced `SyncFromWorkspaceCore` Dual-Pane Rebuilds, Per-Row Timestamp Parsing, and Repeated Lookups
- **Locations:** [MainWindow.xaml.cs](../src-winui/SimpleFile.App/MainWindow.xaml.cs), [MainWindow.Chrome.cs](../src-winui/SimpleFile.App/MainWindow.Chrome.cs), [ExplorerRows.cs](../src-winui/SimpleFile.App/ExplorerRows.cs), [EntryPresentation.cs](../src-winui/SimpleFile.Core/EntryPresentation.cs).
- **Resolution:** Coalesced `OnWorkspaceChanged` via `_workspaceSyncQueued`, skipped secondary-pane row/tab/breadcrumb syncs when `!DualPaneEnabled`, precomputed `_activeSyncCutPaths` (`HashSet<string>`), cached `UserDataPaths.GetDefault()`, and added a fast span parser for `"yyyy-MM-dd HH:mm"` in `EntryPresentation.FormatModified`.

### [x] P5. [High — C# UI-Thread Stalls] Synchronous `FileInfo` Stats, 65k `GetPixel` P/Invokes per Icon, Uncached `Open With` Registry Walk, and UI-Thread `WSearch`
- **Locations:** [ThumbnailCacheKey.cs](../src-winui/SimpleFile.Core/ThumbnailCacheKey.cs), [ShellIconLoader.cs](../src-winui/SimpleFile.App/ShellIconLoader.cs), [FileRowView.xaml.cs](../src-winui/SimpleFile.App/FileRowView.xaml.cs), [OpenWithApplicationDiscovery.cs](../src-winui/SimpleFile.App/OpenWithApplicationDiscovery.cs), [WindowsSearchService.cs](../src-winui/SimpleFile.Core/WindowsSearchService.cs), [MainWindow.Columns.cs](../src-winui/SimpleFile.App/MainWindow.Columns.cs), [MainWindow.Omnibar.cs](../src-winui/SimpleFile.App/MainWindow.Omnibar.cs), [AdvancedRename.cs](../src-winui/SimpleFile.Core/AdvancedRename.cs).
- **Resolution:** Passed known `Size`/`ModifiedText` into `ThumbnailCacheKey.CreateIconKey`; replaced 65,536 `GetPixel` calls with `Bitmap.LockBits`, bounded icon extraction with `SemaphoreSlim(4)`, and filtered `ShellIconLoader.Changed` by path; cached tag `SolidColorBrush`es in `FileRowView`; cached `OpenWithApplicationDiscovery` per extension (60s TTL); moved `WindowsSearchService` onto `Task.Run` and queried `System.FileAttributes` directly from `SystemIndex`; reused a single measurement `TextBlock` and single `ApplyColumnWidths()` in `SizeAllColumnsToFit`; avoided synchronous UNC `Directory.Exists` in `Omnibar`; and precompiled `AdvancedRename` regexes.

### [x] P6. [High — IPC & SQLite] 50 Sequential Settings RPCs + SQLite Fsyncs Without WAL, `prepare_cached`, or Cached DB Path
- **Locations:** [WorkspaceSettingsStore.cs](../src-winui/SimpleFile.Core/WorkspaceSettingsStore.cs), [settings_store.rs](../crates/simplefile-core/src/settings_store.rs), [fact_cache.rs](../crates/simplefile-core/src/fact_cache.rs), [fact_jobs.rs](../crates/simplefile-service/src/session/fact_jobs.rs), [profiles.rs](../plugins/remote/backend/src/profiles.rs).
- **Resolution:** Tracked per-backend snapshots in `WorkspaceSettingsStore` so `SaveAsync` writes only dirty keys; cached `metadata_db_path()` with environment fingerprinting; enabled `PRAGMA journal_mode = WAL`, `synchronous = NORMAL`, `busy_timeout(5s)`, and `prepare_cached`; added transactional `set_db_settings` and `put_cached_facts_batch`; and kept a persistent WAL SQLite connection in `ProfileStore`.

### [x] P7. [High — Rust Search] Per-Entry `chrono::Local` Formatting & Lowercasing Before Filter Checks + Full 2 MiB `read_to_string` on Binary Files
- **Locations:** [search.rs](../crates/simplefile-service/src/search.rs).
- **Resolution:** Switched name/extension filtering to zero-alloc ASCII case-insensitive paths, compared raw `SystemTime` bounds before `chrono::Local` formatting (formatting only matched results), and added 8 KiB NUL binary sniffing plus UTF-16LE/BE BOM decoding before allocating full content buffers.

### [x] P8. [High — Remote SFTP & Local Checksum Transfers] 32 KiB SFTP Packet Cap, Extra Stat RTTs, and Idle Checker Threads
- **Locations:** [sftp.rs](../plugins/remote/backend/src/sftp.rs), [transfer.rs](../plugins/remote/backend/src/transfer.rs), [parallel.rs](../crates/simplefile-service/src/progress/parallel.rs).
- **Resolution:** Raised SFTP SSH window to `16 MiB`, channel packet to `256 KiB`, `max_write_packet_len = 131_072`, and `max_concurrent_reads/writes = 64`; used `symlink_metadata` (`LSTAT`) on SFTP rename and eliminated redundant upload staging and post-rename `stat` RTTs; and skipped spawning checker threads when `initial_dirs.is_empty()` in `parallel.rs`.
- **Follow-up (optional):**
  - [ ] Avoid redundant full-file re-reads in `CheckMode::Checksum` remote download (`verify_remote_checksum`) and local cross-volume move (`delete_verified_source`) when file identity + mtime + size are unchanged.
  - [ ] Spawn transfer worker threads lazily up to `session.total_limit()`.

### [x] P9. [High — Git & 7-Zip Plugins] System-Wide `TH32CS_SNAPTHREAD` Walk and `-bsp1` Stdout Buffering
- **Locations:** [git/run.rs](../plugins/git/backend/src/run.rs), [sevenzip/run.rs](../plugins/sevenzip/backend/src/run.rs).
- **Resolution:** Replaced `CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)` with O(1) `NtResumeProcess` and capped background pipe capture at `64 MiB` in `git/run.rs`; skipped buffering megabytes of `-bsp1` progress stdout in `sevenzip/run.rs`. *(Cached `find_root` and `binary_for` IPC lookups remain compatible with pinned `spawn_count` and `binary_for` test contracts.)*

### [~] P10. [Medium — Enumeration, Cache, Metadata, Compare & Build Script Bottlenecks]
- [ ] **P10.1** Eager timestamp strings & sort-key reallocations (`dir_list.rs`, `collation.rs`). Left open: the timestamps are strings in the wire format, so this belongs with the shared wire types in `A8`.
- [x] **P10.2** `SessionCache` linear invalidation scans & `VecDeque` sort: a batch of paths is invalidated in one pass over the cache, and removal no longer rebuilds the eviction order ([session_cache.rs](../crates/simplefile-service/src/session_cache.rs)).
- [x] **P10.3** Full `.tar.gz` decompression for metadata: the scan stops after 64 MiB of decompressed data and reports that it counted the start of the archive only ([metadata.rs](../crates/simplefile-core/src/metadata.rs)). Image decoding was already bounded by the decoder limits and is unchanged.
- [x] **P10.4** `compare.rs` opens each file once ([compare.rs](../crates/simplefile-core/src/compare.rs)); `cleanup.rs` partial-hash pre-pass done in [cleanup.rs](../crates/simplefile-core/src/cleanup.rs).
- [ ] **P10.5** Release & smoke script slowdowns (`build-winui-release.ps1`, `smoke-winui-ui.ps1`). Left open: the scripts run only on Windows, so a change could not be measured.

---

## 3. Redundant & Dead Code

### [x] R1. Verified Dead / Unreachable C# Production Code (~1,200+ LOC)
- [x] Removed `TransferViewModel.cs` and its container registration; `DescribeSource` is on `TransferOperationViewModel`, which uses it.
- [x] `ExtractArchiveDialog` kept: it is in use. `FileOperationDialogService.ShowExtractDialogAsync` opens it for Extract, so the finding was wrong.
- [x] Removed `PaneEnrichmentController` and the `ExplorerWorkspace` wrappers.
- [x] Removed `WorkspaceFileOpsController.CopyOrMoveToOtherPaneAsync` and its workspace wrapper.
- [x] Removed the 14 unwired event handlers and three unused private helpers.
- [x] Removed `PhotoFolderActive`, its five recomputations and `PhotoFolder.IsPhotoFolder`.
- [x] Removed `VisibleEntriesPreSorted`, `ColumnLayoutHost.Shared`, `ConflictingNames`, the legacy `AdvancedRename.Apply`/`Build`, and `SharedMemoryReader`.

### [~] R2. Duplicated C# & Script Subsystems
- [ ] Share `Views/Xaml.cs`, `Views/ListRows.cs`, and `Directory.Build.{props,targets}` between `SumaFile.Git` and `SumaFile.Remote`.
- [x] Path normalizers: row decoration keys and thumbnail cache keys share `PathRules.NormalizeSeparators`. `PathRules.NormalizeComparablePath` is a comparison key in a different form and stays. `GitPaths` keeps its copy because the plugin cannot reference Core.
- [ ] Deduplicate `FindAncestor<T>`/`FindDescendant<T>`, `InitializeWithWindow.Initialize`, and Trash-to-permanent-delete fallback blocks.
- [ ] Deduplicate `dispatch/handlers.rs` parsing across `check-ipc-schema.mjs` and `check-winui-parity-gate.mjs` and shared PowerShell helpers across `scripts/smoke-*.ps1`.

### [~] R3. Duplicated & Dead Rust Subsystems
- [x] Eliminated silent 9,999-collision overwrite fallbacks in [archive/path.rs](../crates/simplefile-core/src/archive/path.rs), [archive/mutate.rs](../crates/simplefile-core/src/archive/mutate.rs), and [recycle_bin.rs](../crates/simplefile-core/src/recycle_bin.rs), and fixed `"CON .txt"` trailing-space validation in [remote/transfer.rs](../plugins/remote/backend/src/transfer.rs).
- [~] `sumafile-transfer` owns the Windows reserved-name check (used by `simplefile-core` and the Remote backend) and the unique-name rule the archive code uses. The 7-Zip backend keeps its own reserved-name check, having no dependency on the transfer crate, and recursive removal is not unified.
- [~] Deleted the unused `SessionState` fields (`duplicate_check_cancel`, `disk_cleanup_cancel`). `StderrLogger` is not shared: moving it would add to the published plugin SDK's API. `parse_params<T>`, `metadata.db` resolution and the wire DTOs are unchanged (the DTOs are `A8`).
- [ ] Standardize C-ABI Win32 syscalls onto `windows-sys = "0.61"` (retiring duplicate `winapi` 0.3 / manual `extern "system"` declarations). Left open: it touches every Windows-only module and needs a Windows build to check.

---

## 4. Potential UI & UX Improvements

- [x] **U1. Complete Sidebar Label Filter & Indented Folder Tree UX:** Repeat-click label toggle-off, `Label: <Name>` chip & clear button in `PaneContextBar`, label empty-pane message, and `Depth * 14px` Folder Tree indentation + chevron collapse/expand.
- [ ] **U2. Bring `RemoteManagerWindow` (`FTP/SFTP`) to File-Manager Parity:** Double-click/`Enter` to open folders, single-click multi-select, editable `RemotePathBox`, `Size` column, splitters, context menus, keyboard shortcuts, and drag-and-drop.
- [ ] **U3. Enable Rubber-Band Marquee Selection in `Details` View & Highlight Hovered Drop Target Folders.**
- [ ] **U4. Drop-Onto-Tabs/Breadcrumbs/Sidebar, Middle-Click Open-in-New-Tab, Tab Reordering, and Mouse `XButton1`/`XButton2` Back/Forward.**
- [ ] **U5. Persist Window Size, Monitor & Maximized State Across Sessions.**
- [ ] **U6. Omnibar Path/Command Autocomplete, Inline Rename in Non-Details Views, and Column Drag-Reordering.**
- [~] **U7. Workbench, Conflict Dialog, Transfers, Quick Look & Accessibility Polish:**
  - [x] `TransferProgressWindow` Details pane now resizes vertically with the window ([TransferWindowLayout.cs](../src-winui/SimpleFile.Core/TransferWindowLayout.cs), [TransferProgressWindow.xaml](../src-winui/SimpleFile.App/TransferProgressWindow.xaml), [TransferProgressWindow.xaml.cs](../src-winui/SimpleFile.App/TransferProgressWindow.xaml.cs)).
  - [ ] Remaining polish: Git diff colorization & inline commit box, Conflict Dialog thumbnails, Transfers tab-order/actions, Quick Look item counter/zoom/Markdown toggle, and High Contrast + keyboard resize grips.

---

## 5. Potential Rust Backend Improvements

- [ ] **A1.** Instant CoW block cloning (`FSCTL_DUPLICATE_EXTENTS_TO_FILE`) on ReFS / Dev Drives + SMB2/3 server-side copy offload (`CopyFileExW` / `FSCTL_SRV_COPYCHUNK`).
- [ ] **A2.** Native multi-root IOCP `ReadDirectoryChangesExW` watcher (`ReadDirectoryNotifyExtendedInformation`).
- [ ] **A3.** Unified mount-point & 128-bit File-ID volume layer (`GetVolumePathNameW` + `FILE_ID_INFO`).
- [ ] **A4.** Overlapped async Tokio named-pipe client pool for plugins.
- [ ] **A5.** Batched COM `IFileOperation` + `IFileOperationProgressSink` for Recycle Bin operations.
- [ ] **A6.** Hardware-accelerated Windows Shell / WIC thumbnail provider (`IShellItemImageFactory`).
- [~] **A7.** SQLite `WAL` + `synchronous = NORMAL` + `prepare_cached` (**done**); read/write connection split and bounded `entry_facts` pruning remain open.
- [ ] **A8.** Raw `u64` epoch/FILETIME timestamps on `FileEntry` + binary IPC wire format.
- [ ] **A9.** In-place ZIP entry copy (`ZipWriter::raw_copy_file`) and bounded `service.log` ring buffer.
