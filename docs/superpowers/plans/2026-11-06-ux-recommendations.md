# UX Recommendations (1.0.4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement every item in the 1.0.4 UX spec and ship it as version 1.0.4.

**Architecture:** Pure decisions (menus, intents, shortcuts, settings, copy helpers) live in `SimpleFile.Core` and are pinned by xUnit tests. WinUI code-behind in `SimpleFile.App` consumes them. Tasks own disjoint files so they can run in parallel; cross-task names are fixed in each task's Interfaces block.

**Tech Stack:** C# / WinUI 3 (.NET 10), xUnit, Rust (service messages only), Node release scripts.

**Spec:** `docs/superpowers/specs/2026-11-06-ux-recommendations.md`

## Global Constraints

- On this Linux container only `SimpleFile.Core`, `SimpleFile.Ipc` and `SimpleFile.Tests` compile:
  `dotnet build src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug -p:EnableWindowsTargeting=true`
  and `dotnet test ... --no-build`. `SimpleFile.App` cannot compile here (XAML compiler); App edits must be
  reviewed line by line against neighbouring code for correct names, `using`s and XAML syntax. CI on Windows is the gate.
- 13 tests fail on Linux before any change (path separators, junctions, shared memory, job objects). They are listed in the
  scratchpad `baseline-failures.txt`; do not chase them, do not add to them.
- Source-shape tests read App source text (`UiAuditSprint3Tests.Read`, `WinUiSourceShapeTests`, `CommandIdConsistencyTests`,
  automation-name scan). Run the full suite after every task.
- `scripts/check-winui-parity-gate.mjs` requires every `ctx-*` id in `ContextMenuBuilder.cs` and every palette id in
  `AppCommandCatalog.cs` to appear in `docs/winui-migration/parity-gate.md`. Run `npm run check` at the end.
- Sentence case for all user-facing labels unless a proper noun ("Recycle Bin", "Git", "FTP/SFTP").
- Nouns: "Labels", "sidebar", "Profiles" (workspace), "Connections" (FTP/SFTP), "Large files and duplicates".
- No model identifiers in commits or code.

## Review Focus

1. Right-click with nothing selected in a non-Git folder: no Git submenu, no greyed Git items.
2. Omnibar: type `report` in Path mode when `.\report` does not exist -> filter applied, status message, mode stays Filter; type `..` -> navigates up.
3. Press Tab while the sidebar has focus in dual-pane mode -> focus moves, panes do not switch.
4. Conflict dialog with Skip selected and Enter -> Skip, transfer continues; Escape -> Stop transfer.
5. Apply the built-in "Photos" profile -> Settings > Developer "Git" toggle is unchanged.

---

### Task 1: Core menus and command catalog

**Files:**
- Modify: `src-winui/SimpleFile.Core/ContextMenuBuilder.cs`, `CommandIds.cs`, `AppCommandCatalog.cs`, `CommandAliasCatalog.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.CommandRouting.cs` (routing table entries only)
- Modify: `docs/winui-migration/parity-gate.md` (new ids)
- Test: `src-winui/SimpleFile.Tests/ContextMenuBuilderTests.cs`, `AppCommandCatalogTests.cs`, `UiAuditSprint4Tests.cs` (only if an assertion encodes old behaviour)

**Interfaces:**
- Produces: `ContextMenuEntry.Disabled` is now emitted (greyed) by `VisibleEntries`; `Hidden` is the only skip.
- Produces palette ids: `save-smart-folder` (label "Save search as smart folder…", group "Organization"); labels `search` -> "Search this folder", `filter` -> "Filter this folder", `disk-cleanup` -> "Large files and duplicates", `color-label` -> "Set label", `clear-tag-filter` -> "Clear label filter", `toggle-side-menu` -> "Show or hide sidebar", icon sizes reduced to small/medium/large/extra-large/huge (remove `icon-size-jumbo`, `icon-size-maximum`; `huge` maps to 256).
- Produces routing: `["save-smart-folder"] = SaveSearchAsSmartFolderAsync` (method implemented in Task 3).
- Context ids unchanged except removal of `ctx-delete-menu`; More menu gains container id `overflow-tools-menu`.

- [ ] Write failing tests: disabled entries present with `Disabled == true`; Git submenu absent when `!InGitRepository`; "Delete" direct item then "Delete permanently"; Send to has exactly the two cross-pane children; top level contains "Copy path" and "Bookmark folder"; More menu has no `ctx-rename`/`ctx-delete-*`/archive/tools items but has `overflow-tools-menu` with FTP/SFTP, Operation history, Large files and duplicates, Terminal.
- [ ] Update the 31 existing assertions that encode hiding.
- [ ] Implement; run the suite; `node scripts/check-winui-parity-gate.mjs`.
- [ ] Commit.

### Task 2: Core input, settings and copy helpers

**Files:**
- Modify: `src-winui/SimpleFile.Core/OmnibarIntent.cs`, `KeyboardShortcutMap.cs`, `CommandShortcutMap.cs` (if ids change), `UiSettings.cs`, `PreviewLayoutPolicy.cs`, `SavedWorkspaceLayout.cs`, `WorkspaceProfile.cs`, `PaneRoleFormatter.cs`, `EmptyPaneState.cs`, `UndoStack.cs`, `StatusCenterFormatter.cs`, `OperationHistoryFormatter.cs`
- Create: `src-winui/SimpleFile.Core/TransferFailureText.cs`
- Test: matching `*Tests.cs` plus new `TransferFailureTextTests.cs`

**Interfaces:**
- Produces: `OmnibarIntentParser.Parse(string? text, OmnibarMode mode, string? currentPath, Func<string, bool>? directoryExists = null)`; returns `OmnibarIntent` with new `string? StatusMessage` ("No folder named 'X' here. Filtering instead.").
- Produces: shortcut defaults `file.newFile` unbound, `tab.new` keeps Ctrl+T and gains Ctrl+N, `nav.up` loses Backspace, `nav.back` gains Backspace, `tools.terminal` = "Ctrl+`" (add "`"/"backtick" -> "Oem3" to `NormalizeKey` and the VirtualKey map), `settings.open` = "Ctrl+," (OemComma), `help.keyboard` = F1 and "Ctrl+/" mapping to both `Divide` and `Oem2`, `preview.toggle` = "Alt+P". `KeyboardShortcutMap.IsFileListScopedKey(commandId)` returns true for `pane.switch`, `preview.quickLook` (Space) and the Backspace binding.
- Produces: `UiSettings.NormalizeTheme("light") == "light"`; `IconSizeOptions` = 16 Small icons, 32 Medium icons, 48 Large icons, 96 Extra large icons, 256 Huge icons; `UiSettings.NormalizeIconSize(int)` maps 128->96, 192->256.
- Produces: `PreviewLayoutPolicy.ShouldCollapse` ignores `hasContent`.
- Produces: `WorkspaceChromeLayout.Apply` no longer writes `EnableGitIntegration` or `KeepFoldersOnTop`.
- Produces: `PaneRoleFormatter` captions "Left · Active" / "Right" (inactive pane has no suffix).
- Produces: `TransferFailureText.Describe(string? kind, string? rawError, string? path) -> (string Summary, string Details)`; maps access denied, disk full, not found, connection lost, cancelled; default "Couldn't finish {path}".
- Produces: `UndoStack.NextUndoDescription` with real plurals ("Move 3 items", "Rename 1 item").

- [ ] Tests first for each behaviour; implement; full suite; commit.

### Task 3: App shell (toolbar, omnibar, panes, sidebar, tabs, places)

**Files:**
- Modify: `src-winui/SimpleFile.App/MainWindow.Shortcuts.cs`, `MainWindow.Omnibar.cs`, `MainWindow.Search.cs`, `MainWindow.Commands.cs`, `MainWindow.CommandSurface.cs`, `MainWindow.Chrome.cs`, `MainWindow.PreviewLayout.cs`, `MainWindow.Places.cs`, `MainWindow.FileListEvents.cs`, `MainWindow.xaml`, `PrimaryToolbarView.xaml(.cs)`, `SidebarView.xaml(.cs)`, `PaneView.xaml(.cs)`, `PaneContextBar.xaml(.cs)`, `DetailsFileListView.cs`, `FileRowView.xaml(.cs)`

**Interfaces:**
- Consumes Task 2 `Parse(..., directoryExists: Directory.Exists)`, `IsFileListScopedKey`, `NormalizeIconSize`.
- Produces: `private Task SaveSearchAsSmartFolderAsync()` in `MainWindow.Places.cs` (routes from Task 1's table).
- Produces: `MainWindow.TryBeginInlineRename()` returning bool, used by Task 4's `PromptAndRename` wrapper (Task 4 calls it first; if true, returns).

- [ ] Tab/Space/Backspace guard in `CanRunShortcutNow` via a `IsFileListFocused()` helper walking `FocusManager.GetFocusedElement(Content.XamlRoot)`.
- [ ] Omnibar: reset to Navigate in `OnSearchCleared`, in the Escape branch that clears search/filter, and on active-pane navigation while in Search/Filter mode; permanent placeholder/tooltip; mode toggles focusable with focus restore; automation name.
- [ ] Toolbar: `IsEnabled` gating for the cross-pane buttons; View flyout trimmed; "Remember this view for" dropdown.
- [ ] Tabs: context flyout; hide close glyph on the last tab in dual-pane; tooltip "New tab".
- [ ] Sidebar: "Labels", "Connections", "sidebar" copy; connections rows with trailing glyph; empty states.
- [ ] Places: smart-folder save command + banner button + delete confirmation.
- [ ] Preview toggle strict; `preview.toggle` handler unchanged.
- [ ] Inline rename in Details.
- [ ] Automation-name scan test and source-shape tests pass; commit.

### Task 4: Dialogs, file operations and transfers

**Files:**
- Modify: `src-winui/SimpleFile.App/ConflictDialog.xaml(.cs)`, `MainWindow.Transfer.cs`, `MainWindow.FileOperations.cs`, `TransferProgressWindow.xaml(.cs)`, `TransferOptionsDialog.cs`, `FileOperationDialogService.cs`, `FileOperationDialogService.Scans.cs`, `DiskCleanupDialog.xaml(.cs)`, `DuplicateCheckerDialog.xaml(.cs)`, `SecondaryWindowChrome.cs`, `AdvancedRenameDialog.xaml(.cs)`, `TagPickerDialog.xaml(.cs)`, `MainWindow.InspectionDialogs.cs`, `MainWindow.StatusCenter.cs`
- Test: `src-winui/SimpleFile.Tests/TransferConflictPromptFormatterTests.cs` and source-shape tests as needed

**Interfaces:**
- Consumes Task 2 `TransferFailureText`, `UndoStack.NextUndoDescription`; Task 3 `TryBeginInlineRename()`.
- Produces: `TransferOptionsDialog.ShowDefaultsAsync(XamlRoot root)` static (resolves the settings backend via `AppServices`), used by Task 5's Settings row.
- Produces: `SecondaryWindowChrome.Apply(Window, string? title, int w, int h, bool mica = true, Action? cancel = null, Action? primary = null)`.

- [ ] Conflict dialog rebuilt per spec; `MainWindow.Transfer.cs` maps the radio result; Enter = Continue.
- [ ] Transfers window: no activation on auto-create; 3 s delayed show; Details expander; Options in overflow.
- [ ] Transfer options restructure + `ShowDefaultsAsync`; failure text via `TransferFailureText`.
- [ ] Recycle Bin prompt: default Primary, item name; one helper for both call sites.
- [ ] Remove `ConfirmAdvancedRenameAsync`; template token flyout in Advanced Rename.
- [ ] Large files and duplicates dialog: title/copy, footer, Reveal/Recycle actions, Duplicate Finder link.
- [ ] Duplicate Checker: Advanced expander, "Keep the copy in the current folder", themed button.
- [ ] Secondary windows: Esc/Ctrl+Enter accelerators.
- [ ] Undo surfacing; Status Center progress bar rows and Undo.
- [ ] Quick Look lightbox; folder branch removed.
- [ ] Tag picker copy ("Choose a label", preselect current).
- [ ] Commit.

### Task 5: Settings, Git, preview header, remote manager, Open With

**Files:**
- Modify: `src-winui/SimpleFile.App/SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `SettingsWindow.SettingsState.cs`, `SettingsWindow.Tools.cs`, `MainWindow.Git.cs`, `GitWorkbenchView.xaml`, `PreviewPaneView.xaml(.cs)`, `PreviewPresenter.cs`, `RemoteManagerWindow.xaml(.cs)`, `MainWindow.Remote.cs`, `MainWindow.OpenWith.cs`, `App.xaml` (critical button style if missing)
- Modify: `src-winui/SimpleFile.Core/RemoteManagerViewModel.cs`, `WorkspaceControllers/PaneEnrichmentController.cs`
- Modify: `crates/simplefile-core/src/remote/profiles.rs`, `crates/simplefile-service/src/remote/ftp.rs`, `crates/simplefile-service/src/remote/sftp.rs` (message text only; keep a stable prefix `SFTP host fingerprint is not trusted` / `SFTP host fingerprint mismatch`)
- Test: `RemoteManagerViewModelTests.cs`, `WorkspaceControllerTests.cs`, `UiSettingsTests.cs` as needed

**Interfaces:**
- Consumes Task 2 `NormalizeTheme("light")`; Task 4 `TransferOptionsDialog.ShowDefaultsAsync`.
- Produces: `RemoteManagerViewModel.ConnectResult` with `UntrustedFingerprint`/`MismatchExpected` parsed from the error prefix; `TrustFingerprintAndConnectAsync(string fingerprint)`.
- Produces: `PaneEnrichmentController.ApplyGitStatusesAsync` short-circuits when `state.GitRepositoryRoot is null` (set from the repo probe).

- [ ] Settings: control-level search index; Light theme; sentence-case labels; "Ask before moving to Recycle Bin"; Start location labels; "Developer" group; "Transfers" row; preview switch labels.
- [ ] Git lazy visibility; Discard copy and style.
- [ ] Preview header Source | Rendered segmented control (SelectorBar or two ToggleButtons); same persistence.
- [ ] Remote: plain-FTP consent, Save gating, Insecure badge, trust prompt, mismatch warning, hidden credential target, port from protocol, "Refresh" everywhere, "Transfer options...".
- [ ] Open With copy ("Installed"/"Suggested"/"Other", no "payload", no "yet"); pre-validate with `OpenWithPolicy.IsLaunchableApplication`.
- [ ] Commit.

### Task 6: Docs and version 1.0.4 (lead)

- [ ] README: shortcuts table, "Profiles", More -> Tools path, "Labels", icon sizes, version strings.
- [ ] `docs/RELEASE_1.0.4.md`, CHANGELOG entry, UI audit status note.
- [ ] `npm run release:prepare -- 1.0.4 --write`; `npm run check`; full test suite; commit; push.
