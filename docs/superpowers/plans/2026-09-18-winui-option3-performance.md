# WinUI Option 3 Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fully fix SumaFile WinUI startup and interaction choppiness by making drive probing lazy/cancellable and replacing the non-virtualized details list with a virtualized path.

**Architecture:** Startup uses only cheap drive metadata and never performs full network/provider-backed free-space probes automatically. Full drive details are resolved per drive when the user explicitly refreshes or navigates. Details mode renders through the same virtualized `ListView` surface as list/tile/content modes, while the existing details header and horizontal scroll state remain the presentation shell.

**Tech Stack:** C# WinUI 3, .NET 10, Rust service/core crates, named-pipe JSON-RPC IPC, xUnit, Rust tests, startup timing log checks.

**Spec:** User-selected option 3 from the September 18 performance diagnosis.

## Global Constraints

- Preserve existing dirty worktree changes; do not revert unrelated Phase 1-3 work.
- Startup must not automatically run full all-drive network/provider-backed probing.
- Remote-like fixed drives such as AirLiveDrive must be treated like network-backed drives for lazy capacity/status refresh.
- Details mode must not instantiate one row control for every folder entry up front.
- Resize/move should not repeatedly rebuild details rows or synchronously probe drives on the UI thread.
- Existing commands must keep working: `npm run check:winui`, `npm run build:winui`, `npm run check`, startup smoke budget.

---

### Task 1: Lazy Drive Metadata And Per-Drive Refresh

**Files:**
- Modify: `crates/simplefile-core/src/drives.rs`
- Modify: `crates/simplefile-core/src/utils.rs` if remote-like filesystem detection needs to be shared
- Modify: `crates/simplefile-service/src/dispatch/params.rs`
- Modify: `crates/simplefile-service/src/dispatch/handlers.rs`
- Modify: `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.cs`
- Modify: `src-winui/SimpleFile.Core/IExplorerBackend.cs`
- Modify: `src-winui/SimpleFile.Core/BackendSession.cs`
- Modify: `src-winui/SimpleFile.Core/ExplorerWorkspace.Navigation.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.xaml.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.FileListEvents.cs`
- Test: `src-winui/SimpleFile.Tests/FakeExplorerBackend.cs`
- Test: `src-winui/SimpleFile.Tests/ExplorerWorkspaceTests.cs`
- Test: Rust tests in `crates/simplefile-core/src/drives.rs`

**Interfaces:**
- Consumes: existing `list_drives` IPC method and `DriveInfo` model.
- Produces: `ListDriveAsync(string path, CancellationToken)` in the C# client/backend and `ExplorerWorkspace.RefreshDriveAsync(string path, bool quiet, CancellationToken)`.

- [ ] **Step 1: Write failing C# tests**

Add tests proving that `ExplorerWorkspace.RefreshDriveAsync` replaces only the requested drive and that startup no longer needs a bulk refresh to clear unknown drives.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ExplorerWorkspace"`
Expected: FAIL because `RefreshDriveAsync` and fake backend support do not exist.

- [ ] **Step 2: Write failing Rust tests**

Add Rust tests around drive classification helpers so remote-like file systems are marked lazy/unknown in light mode and eligible for full per-drive resolution.

Run: `cargo test -p simplefile-core drives --locked`
Expected: FAIL because the helper or behavior does not exist.

- [ ] **Step 3: Implement Rust drive modes**

Keep `list_drives_light()` cheap. Add a per-drive resolution path behind `list_drives` params `{ mode: "drive", path: "X:\\" }`. Light mode should skip `GetDiskFreeSpaceExW` for `DRIVE_REMOTE` and remote-like fixed file systems. Full per-drive mode may call `WNetGetConnectionW`, `GetFileAttributesW`, and `GetDiskFreeSpaceExW` for only the requested root.

- [ ] **Step 4: Wire C# IPC and workspace**

Add a non-generated `NamedPipeJsonClient` partial method for `ListDriveAsync`. Extend `IExplorerBackend`, `BackendSession`, and `FakeExplorerBackend`. Add `ExplorerWorkspace.RefreshDriveAsync` to merge one returned drive into `_drives` and raise one `Changed` event.

- [ ] **Step 5: Replace startup bulk refresh**

Remove `QueueStartupDriveRefresh` from successful startup. On drive click, if the clicked row status is `unknown`, first call `RefreshDriveAsync(row.Path, quiet: true)` with a cancellable operation, then navigate. The sidebar refresh button can keep bulk refresh because it is explicit.

- [ ] **Step 6: Verify Task 1**

Run:
`cargo test -p simplefile-core drives --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ExplorerWorkspace"`

Expected: both pass.

### Task 2: Virtualized Details List Rendering

**Files:**
- Modify: `src-winui/SimpleFile.App/PrimaryPaneView.xaml`
- Modify: `src-winui/SimpleFile.App/SecondaryPaneView.xaml`
- Modify: `src-winui/SimpleFile.App/MainWindow.Chrome.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.Columns.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.FileListEvents.cs`
- Modify: `src-winui/SimpleFile.App/FileRowView.xaml.cs`
- Retire or heavily reduce: `src-winui/SimpleFile.App/DetailsFileListView.cs`
- Test: `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`

**Interfaces:**
- Consumes: existing `PrimaryFiles` and `SecondaryFiles` observable collections.
- Produces: details mode rendered by `PrimaryFileList` and `SecondaryFileList` instead of `DetailsFileListView`.

- [ ] **Step 1: Write failing source-shape test**

Add a test asserting that details mode no longer uses `DetailsFileListView` in `PrimaryPaneView.xaml` or `SecondaryPaneView.xaml`, and that the custom details view no longer owns a `StackPanel _rowsHost`.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests"`
Expected: FAIL because both panes still instantiate `DetailsFileListView`.

- [ ] **Step 2: Route details mode through ListView**

Remove details view controls from pane XAML or keep them unused until build passes. Make `ApplyFileListSurfaceVisibility` show the virtualized `ListView` for all modes. Keep details headers visible only for effective details mode.

- [ ] **Step 3: Preserve details interaction behavior**

Update event handlers so double-click, selection, context menus, drag/drop, keyboard shortcuts, and marquee selection still target the active `ListView`. Keep `FileRowView.ApplyDetailsPresentation` for details rows; it should not subscribe every row to global details events when explicit presentation is supplied.

- [ ] **Step 4: Reduce resize work**

Coalesce pane size refreshes to a longer idle threshold and avoid calling full row layout work when the effective view/key did not change. Header widths and horizontal scroll state may update, but row recreation must not occur on every resize tick.

- [ ] **Step 5: Verify Task 2**

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests|FullyQualifiedName~ListReplaceTests"`
`npm run build:winui`

Expected: tests pass and WinUI builds.

### Task 3: Performance Budget And Runtime Verification

**Files:**
- Modify: `src-winui/SimpleFile.Core/StartupTimingBudget.cs`
- Modify: `src-winui/SimpleFile.Tests/StartupTimingBudgetTests.cs`
- Modify: `scripts/check-winui-startup-budget.mjs` if it needs a stricter assertion surface

**Interfaces:**
- Consumes: `%LOCALAPPDATA%\SumaFile\startup-timing.log`.
- Produces: a budget check that fails if startup performs `MainWindow.StartupDriveRefresh.refreshed`.

- [ ] **Step 1: Write failing budget test**

Add a test where a session containing `MainWindow.StartupDriveRefresh.refreshed` is a violation.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~StartupTimingBudgetTests"`
Expected: FAIL because the current budget allows deferred startup refresh.

- [ ] **Step 2: Implement budget rule**

Update `StartupTimingBudget` so automatic startup drive refresh completion is disallowed. Keep existing checks for backend ready and deferred navigation.

- [ ] **Step 3: Verify runtime**

Build and run a fresh debug or payload app with the current `simplefile-service.exe`. Confirm the latest `startup-timing.log` has no automatic `MainWindow.StartupDriveRefresh.refreshed` entry and that ready time remains within budget.

- [ ] **Step 4: Final gates**

Run:
`cargo test -p simplefile-core drives --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`
`npm run build:winui`
`npm run check:winui`
`npm run check`
`git diff --check`

Expected: all pass, allowing only known LF-to-CRLF warnings from Git.

## Self-Review

- The plan covers both measured root causes: startup drive probing and non-virtualized details rendering.
- The plan keeps explicit user scope: option 3, not the narrower quick mitigation.
- No task depends on unlisted hidden files or unspecified behavior.
- Verification includes unit/source-shape tests, builds, existing WinUI gate, repo gate, and runtime startup timing evidence.
