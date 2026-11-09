# Plugin System Phase 4: Git Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** First keep every plugin file inside SumaFile's installation folder and close the phase 3 carry-overs. Then give the host its side-panel and row-decoration slots, and move SumaFile's whole Git feature out of core into the first-party plugin `com.sumafile.git`: a Rust backend built from `git.rs`, a code-built C# UI with the workbench panel, ten commands, a context-menu group, a toolbar toggle and a startup-activated row decoration provider. Core loses the 11 Git IPC methods and every Git surface, and a default install keeps working as before.

**Architecture:**
- **Locations.** One plugin root, `<AppContext.BaseDirectory>\plugins`, holds the bundled plugins and the ones the user installs. Plugin data, the approvals file and `plugins.log` sit beside it. Every plugin backend runs with `TMP` and `TEMP` inside its data folder, so its temporary files, and those of the processes it starts, stay in the install folder too. Trust stays keyed on `bundled.json`. NSIS and WiX keep these app-owned files on upgrade and remove them on uninstall, whatever the install folder is named.
- **Slots.**
  - The SDK gains `IPanelProvider` and `IRowDecorationProvider`. Their UI hand-offs stay `object` at SDK 1.0.0.
  - The App gains a generic panel host, which is what spec §7 calls `PluginPanelHost`: a bottom and a right region in `MainWindow.xaml`, driven by `MainWindow.PluginPanels.cs`. One panel is visible at a time, docked with a resize grip or detached into a host-owned `PluginPanelWindow`.
  - Core asks decoration providers once per folder sync, always from the thread pool, and waits at most 250 ms before it publishes the listing. Late results apply when they arrive. A listing whose decorations were asked before startup was ready, or before the provider set changed, is decorated again right after it is published.
  - The results fill `Row.DecorationText` and `DecorationBrushKey`, and `ContextMenuRequest.FolderFlags` and `SelectionDecorationPlugins`, so `when` expressions work.
- **Git.**
  - `plugins/git/backend` (`sumafile-git-backend.exe`) ports `git.rs` and its status cache, and fixes the Windows path defects.
  - `plugins/git/ui` has two assemblies. `SumaFile.Git.Model` is WinUI-free: the view model, the backend client, the decoration provider and the controller. `SumaFile.Git` is the 19041 code-built UI.
  - `enabled` replaces `EnableGitIntegration` through a declarative, bundled-only `migrateFrom`.

**Tech Stack:** C# / .NET 10 / WinUI 3 (Windows App SDK 2.4.0, compile-only in plugin projects), xUnit; Rust 2021 (tokio, serde, windows-sys job objects) on `crates/sumafile-plugin`; Git for Windows on PATH for tests; PowerShell 5.1 scripts; Node checks; NSIS and WiX v3.14 with WixUtilExtension.

**Spec:** `docs/superpowers/specs/2026-11-07-plugin-system-design.md`. This plan implements §14 phase 4 and §11.3. It refines §3, §4.1, §4.4, §5.1, §5.2, §5.3, §6.1, §7, §8.1, §8.3, §9, §10, §11.2, §11.4, §12 and §13, and each refinement is recorded by the task that makes it.
- Earlier plans: `docs/superpowers/plans/2026-11-07-plugin-system-phase-1.md` (Global Constraints), `…-phase-2-sevenzip.md` and `…-phase-3-remote.md`.
- Review notes and rulings: `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md`.
- Phase 3 carry-overs: `.superpowers/phase-3-remote/prior-minors.md`. The numbers #63, #65, #69, #78, #82 and #84 are its line numbers.
- Code maps for this phase: `.superpowers/phase-4-git/maps/{git-rust.md, csharp-git-ui.md, sdk-host-slots.md, ipc-packaging-ci.md}`. Line anchors below are against base tree `3e381e55`.

## Global Constraints

Copied from the spec, the phase 1 plan and the user's instructions; binding values are verbatim. Every task's requirements include this section.

- **Workspace (user instruction):**
  - Work only in `R:\Repos\SumaFile` on branch `plugin-system-phase-1`.
  - **Never commit.** Never run `git commit/stash/reset/checkout/switch/restore/rebase/merge/push/add/clean` or `git worktree`. Never write to GitHub.
  - Every snapshot step is **`bash .superpowers/tools/snap.sh`**. It prints a tree id and creates no commit.
  - Do not dispatch subagents from inside a task.
- **NuGet is offline:**
  - Every restore works from `%USERPROFILE%\.nuget\packages`; `dotnet restore --force-evaluate` is allowed.
  - Never add a NuGet package that is not in that cache. If one is truly needed, stop and report BLOCKED.
  - Packages this plan uses, all cached: `Microsoft.WindowsAppSDK` `2.4.0`, `Microsoft.Windows.SDK.BuildTools` `10.0.28000.2526`, and the xUnit set already in `SimpleFile.Tests`.
  - cargo may fetch crates. Never publish anything (`cargo publish` only with `--dry-run`).
- **Lock files stay consistent with project files:** every `packages.lock.json` (run `dotnet restore --force-evaluate` on each new or changed project), `Cargo.lock` and `tools/sevenz-evaluation/Cargo.lock`.
- Use generous timeouts: builds and smokes take up to 10 minutes; `release:build` takes longer.
- All C# projects inherit `TreatWarningsAsErrors`, `Nullable` and `RestorePackagesWithLockFile` from `Directory.Build.props`.
- **Target frameworks.**
  - `SumaFile.Git.Model` targets `net10.0-windows`, like `SimpleFile.PluginSdk`, `SimpleFile.PluginHost` and `SimpleFile.Tests`.
  - `SumaFile.Git` targets `net10.0-windows10.0.19041.0`. It sets `TargetPlatformMinVersion` `10.0.19041.0` and `EnableDynamicLoading` `true`, and does not set `UseWinUI`.
  - It references `Microsoft.WindowsAppSDK` `2.4.0` and `Microsoft.Windows.SDK.BuildTools` `10.0.28000.2526` with `ExcludeAssets="runtime"` and `PrivateAssets="all"`.
  - `SimpleFile.Tests` reaches 19041 projects only with `ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true"`.
- **Plugin UI and dependencies.**
  - Spec §5.3: compiled XAML from a plugin folder fails. Plugin UI is built in C# or with `XamlReader.Load` strings. No plugin project contains a `.xaml` file, `{Binding}` or `x:Bind`.
  - Spec §12 (confirmed by the user on 2026-11-09): plugin C# projects reference `SimpleFile.PluginSdk`, their own projects, and `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` and `CommunityToolkit.Mvvm` compile-only (`ExcludeAssets="runtime"`, `PrivateAssets="all"`).
  - Plugin crates never depend on `simplefile-core` or `simplefile-service`; `scripts/check-winui-packaging.mjs` (`pluginCrateManifests`) enforces it.
- **SDK.**
  - Spec §5.1 (user ruling 2026-11-09): UI hand-offs in the WinUI-free SDK stay `object`, and the host checks the WinUI type at run time. "Phase 4 (`IPanelProvider`, `IRowDecorationProvider`, the Git UI) follows the same pattern."
  - `PluginSdk.Version` stays `"1.0.0"`.
  - `Fixtures/PluginSdkSurface.v1.txt` changes only in Task 7, deliberately.
- Rust: workspace edition 2021. `cargo fmt --all -- --check`, `cargo test --locked --all-features` and `cargo clippy --locked --all-targets --all-features -- -D warnings` must pass.
- **`npm run check` must stay green.**
  - `scripts/check-ipc-schema.mjs` asserts the exact domain method count: **86** until Task 15, then **75**. Spec §11.3: "`domainMethodCount` drops to 75." `check-winui-parity-gate.mjs` follows the same count.
  - `npm run test:maintenance` must stay green.
- **Startup budget.**
  - Spec §5.2: "Manifest reading and contribution rendering happen at startup and must stay under 50 ms for ten plugins in the startup trace."
  - The `plugins-discovered` span budget stays **50 ms** and is never raised (carry-over h).
  - No plugin UI assembly load, activation, panel element or decoration batch runs before `MainWindow.Connect.plugins-discovered`, and none before `ready`.
- **Grammars and ids (phase 1 plan):**
  - Plugin id grammar: `^[a-z0-9][a-z0-9.-]{1,63}$`.
  - Plugin settings key grammar: `^[a-z0-9][a-zA-Z0-9_-]{0,63}$`. Settings persist as `plugin.<id>.<key>`.
  - Plugin command ids are exposed to the host as `plugin:<id>:<command>`.
  - Pipe name for a plugin backend: `SumaFile.Plugin.<sessionId>.<pid>.<pluginId>`. Connect timeout 5 s.
- **Plugin locations (user requirement 2026-11-09; replaces the phase 1 values):**
  - Plugin roots: `[<AppContext.BaseDirectory>\plugins]`, one root for bundled and user-installed plugins.
  - Backend data directory: `<AppContext.BaseDirectory>\plugins-data\<id>\`.
  - Approvals file: `<AppContext.BaseDirectory>\plugin-approvals.json`.
  - Plugin log: `<AppContext.BaseDirectory>\plugins.log` (rotating to `plugins.log.1` at 1 MB).
  - Overrides for tests and smokes: `SUMAFILE_PLUGIN_ROOTS`, `SUMAFILE_PLUGIN_DATA_ROOT`, `SUMAFILE_PLUGIN_APPROVALS_PATH` and the new `SUMAFILE_PLUGIN_LOG_PATH`.
  - **Temporary files.** Every plugin backend runs with `TMP` and `TEMP` set to `<data dir>\tmp`, which is `<AppContext.BaseDirectory>\plugins-data\<id>\tmp` by default. The host creates the folder before the launch. So the backend's own temporary files (the 7-Zip listfiles, `plugins/sevenzip/backend/src/ops.rs:590`) and those of its children (7za, git and git's helpers) stay in the install folder. Plugin UI code runs in SumaFile's process and never resolves a temp or user folder itself (`Path.GetTempPath`, `Path.GetTempFileName`, `Environment.SpecialFolder`); a source-shape test guards it.
  - **An unwritable install folder**, such as a portable copy under `Program Files` or an admin-protected custom folder, behaves like this:
    - Install from file fails with its message.
    - Enable fails with the approvals file's error, shown in the plugin's card, and changes nothing.
    - `plugins.log` writes are skipped, as every plugin log write is best effort today.
    - A backend fails to start with `SumaFile cannot create the data folder of plugin '<id>' at <path>: <message>`.
    - Nothing falls back to `%LOCALAPPDATA%`.
  - `startup-timing.log`, `startup.log` and `service.log` are the app's own and stay in `%LOCALAPPDATA%\SumaFile`. So do the settings database and the Credential Manager entries `SumaFile.Remote.*`, which are not plugin files.
  - **No migration.** The app never reads, writes or deletes `%LOCALAPPDATA%\SumaFile\plugins`, `plugins-data`, `plugin-approvals.json` or `plugins.log` again.
- **Trust (spec §9).** It is keyed on `bundled.json`, never on location.
  - A plugin the user installs into `<install>\plugins` needs consent.
  - Installing a package whose id is listed in `bundled.json`, or that would overwrite a bundled folder, is refused, and Remove refuses bundled plugins.
  - `bundled.json` is `{"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"<64 lowercase hex>"}]}`, UTF-8 without a BOM. Phase 4 lists three plugins: `com.sumafile.sevenzip`, `com.sumafile.remote` and `com.sumafile.git`.
- **Spec §7 (slots):**
  - "`MainWindow.xaml` replaces the inline `GitPanel` with a generic `PluginPanelHost` that supports one visible panel at a time, host-or-window toggle, and the same resize grip. `GitWorkbenchWindow` becomes a generic `PluginPanelWindow`." This plan builds the `PluginPanelHost` as two regions in `MainWindow.xaml`, driven by `MainWindow.PluginPanels.cs`, not as a separate class, and Task 10 records that naming in §7.
  - "`FileRowView` reads `Row.DecorationText` and `Row.DecorationBrushKey` instead of `Row.GitText`. `ExplorerWorkspace` asks every registered `IRowDecorationProvider` for decorations once per folder sync, off the UI thread, with a 250 ms budget after which the folder renders undecorated and updates when results arrive."
- **Spec §11.3:** "Folder: `plugin.json`, `SumaFile.Git.dll`, `sumafile-git-backend.exe`" (Task 13 adds `SumaFile.Git.Model.dll` and `SumaFile.Git.deps.json`, as phase 3 did for remote). "Contributions: ten commands, one context-menu group with `when` expressions, one toolbar toggle, one panel, one row decoration provider with `activateOn: startup`." "Settings: `enabled` replaces `EnableGitIntegration`. A migration copies the old value on first run."
- **Error texts the UI matches stay byte-identical:**
  - `The current path is not inside a Git repository.`
  - `Commit message is required.`
  - `Refusing to run a file-level Git action against the repository root.`
  - `Select at least one Git path.`
  - `git <name> timed out after <n> seconds`
  - New in this phase: `Git was not found. Install Git for Windows or add git.exe to PATH.` and `Git operation cancelled`.
- **Test data never touches the user's data:**
  - Every smoke that launches the App sets `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` to disposable paths.
  - Every UI, startup and MSI-extract smoke also points `SUMAFILE_PLUGIN_DATA_ROOT`, `SUMAFILE_PLUGIN_APPROVALS_PATH` and `SUMAFILE_PLUGIN_LOG_PATH` at disposable paths, so nothing is written into `dist\winui\payload` or the Debug `bin` folder. `SUMAFILE_PLUGIN_ROOTS` stays as each smoke sets it today.
  - The UI smoke sets `GIT_CEILING_DIRECTORIES=<testRoot>`, because `test-results\` lies inside the SumaFile checkout.
  - **Git isolation.** Every test and smoke that runs git, or launches an app or backend that can run it, isolates git from the developer's config with `GIT_CONFIG_GLOBAL=<temp file>` and `GIT_CONFIG_NOSYSTEM=1`:
    - the Rust Git tests and the C# Git harness;
    - the UI smoke;
    - the file-ops payload smoke (`Invoke-GitPluginSmoke`), for its own git calls and for the backend it starts;
    - the startup, MSI-extract, NSIS and MSI lifecycle smokes, whose app starts the Git backend.

    Repositories are created with `git init -b main` and get their identity per repository. So a global `core.hooksPath`, `commit.gpgsign` or `init.defaultBranch` can never run, prompt or change a result during a test.
- **Installer smokes (this phase):**
  - `smoke:winui-installer` and the new `smoke:winui-msi-lifecycle` install only into a disposable folder under `%TEMP%`, launch the app only from there, and uninstall in `finally`.
  - They refuse to run while:
    - a per-user (HKCU) uninstall entry has `DisplayName` "SumaFile";
    - either lifecycle MSI's ProductCode is registered;
    - a `SumaFile`, `simplefile-service` or `sumafile-*-backend` process runs. The NSIS `.onInit` ends those by image name, whichever install they belong to.
  - **Other installs are never touched.** This machine has SumaFile 1.0.4 installed per machine (HKLM `Software\WOW6432Node\…\Uninstall\{B5350282-C4EA-44DA-AA69-29DA039517BD}`), and a folder already exists at the default per-user path `%LOCALAPPDATA%\Programs\SumaFile-WinUI`. The smokes never launch, change or uninstall either. They snapshot the HKLM "SumaFile" entries and that folder (`Get-FolderSnapshot`) before the first install, and fail if either changed, after the reinstall or upgrade step and again at the end.
  - The launched app gets none of `SUMAFILE_PLUGIN_ROOTS`, `SUMAFILE_PLUGIN_DATA_ROOT`, `SUMAFILE_PLUGIN_APPROVALS_PATH` or `SUMAFILE_PLUGIN_LOG_PATH`, so the defaults are what is tested. `Start-InstalledSumaFile` removes them from the child's environment.
  - Processes are stopped by their path under the disposable folder, never by name alone.
  - Do not run `smoke:winui-upgrade` (it downloads a release with `gh`) or `smoke:winui-upgrade-from-ref` (it creates a git worktree). Both are updated and checked statically.
- CI is not run in this phase (nothing is pushed). Workflow edits are validated by `npm run check` and `npm run test:maintenance`. Two proofs therefore stay pending until the first CI run, and the closures say so:
  - the hosted-runner `plugins-discovered` span (74.2 ms at 46860dc, carry-over h);
  - the NSIS upgrade from the previous release (`smoke:winui-upgrade`).
- Product name in user-facing text is "SumaFile". Internal namespaces keep `SimpleFile`; the plugin's namespaces are `SumaFile.Git` and `SumaFile.Git.Model`.

## Spec refinements this plan records

Each is decided here and written into the spec by the named task.
- **T1: one plugin root.** It drops the user root, and shadowing happens only under `SUMAFILE_PLUGIN_ROOTS`.
- **T1: Remove.** Remove deletes only a non-bundled folder in `<install>\plugins`, and keeps its settings and `plugins-data\<id>`.
- **T1: `SUMAFILE_PLUGIN_LOG_PATH`.**
- **T1: backend temp folders.** Every backend runs with `TMP` and `TEMP` set to `plugins-data\<id>\tmp`.
- **T1: an unwritable install folder** fails Install and Enable with their messages, skips log writes and fails a backend start with a clear data-folder message. Nothing falls back to `%LOCALAPPDATA%`.
- **T3: uninstall purge.** WiX removes the app-owned items (`plugins\`, `plugins-data\`, `plugin-approvals.json*` and `plugins.log*`) on uninstall only, whatever the install folder is named. It never purges the folder itself, which a custom install may share with other files; MSI removes the folder once it is empty. NSIS keeps removing `$INSTDIR` whole. Both installers remember a custom install folder.
- **T4: toolbar items of absent plugins.** A saved toolbar keeps a well-formed `plugin:<id>:<command>` item, hidden and in place, while its plugin is disabled, failed or missing. A plugin's primary toolbar item is offered once (`OfferedPrimaryItems`); once the user removes it, it is never added again.
- **T4: load-failure teardown.** Rescan, Enable, Disable and Restart wait, bounded, for that plugin's load-failure teardown before they create a runtime or start a backend.
- **T7: manifest additions.** `panels[].command` toggles the panel. `toolbar[].when` hides a toolbar button. `settings[].migrateFrom` is honoured only for plugins whose id `bundled.json` lists. `activateOn: startup` stays a `backend` field: the provider's UI activates on the first decoration request, which waits for `ready`.
- **T7: SDK additions.** `IPluginHost.Panels`, `IPluginHost.Decorations`, `IPluginWorkspace.RefreshAsync` and `IPluginDialogs.Notify`. `IPluginPanels` may be called from any thread: `ShowAsync` and `Hide` are queued to the UI thread, never run inline.
- **T9: host decoration rules.**
  - The listing waits at most 250 ms for decorations before it is published; progressive chunks never wait.
  - Every provider call starts on the thread pool, never inline in the caller, so a provider's activation never runs inside a host layout pass (#82).
  - A listing whose decorations were asked before `ready`, or before the provider set changed, is decorated again right after it is published. When startup becomes ready, the shown panes are decorated again.
  - Of two decoration results for one pane, the newer request wins, whatever order they arrive in.
  - Decorations run on every folder sync whatever the column layout, because they also carry `inRepository` for menus and the toolbar.
  - The host never asks providers about network or Recycle Bin folders.
  - The first enabled plugin in id order owns a row's text; every decorating plugin counts for `hasDecoration`.
  - Row keys are matched after separator normalization, case-insensitively and without a trailing separator, both when the host filters a provider's keys and when a row looks up its decoration.
  - The status column keeps the id `git`, so saved layouts still load, and its header becomes "Status".
  - Brush keys outside an allow-list fall back to `SfTextMutedBrush`. New theme keys `SfDecoration{Added,Modified,Untracked,Deleted}Brush` replace `SfGit*Brush`.
- **T11: Settings > Plugins** writes declarative settings through `PluginManager.WriteSettingAsync`, so a change re-decorates at once. Each declarative control gets the AutomationId `PluginSetting.<pluginId>.<key>`.
- **T12: Git backend.**
  - It resolves `git.exe` from absolute `PATH` entries only.
  - Read-only queries run with `--no-optional-locks` and point `core.hooksPath` at a missing, randomly named folder under the data dir. This replaces `%TEMP%\sumafile-git-no-hooks`.
  - Each git process is created suspended, assigned to its own kill-on-close job and only then resumed. So cancel and timeout end git and every child it starts, including the real `git.exe` behind Git for Windows' `cmd\git.exe` launcher.
  - The status cache is per repository root.
  - Decorations mark direct children only, and spawn no git outside a repository.
- **T13 and T15: user-visible changes.**
  - Git menu items are hidden, not disabled, when their `when` is false.
  - The Git toggle moves from the status bar to the primary toolbar. It is visible inside a repository or while the panel is open.
  - "Git: show workbench" from the palette shows the panel; the toolbar button toggles it.
  - A saved custom toolbar that held a core `git-*` button loses it.
  - Fetch, pull and push become Transfers rows with Cancel.
  - Results show in the message bar through `IPluginDialogs.Notify`.
  - Plugin-run pull or discard no longer invalidates the service listing cache; the watcher, the directory stamp and the 10 s cache age bound the staleness, as phase 3 accepted for remote downloads.
  - **While `enabled` is false**, the ten Git commands stay on the palette and in the omnibar. Running one shows `Git integration is turned off. Turn it on in Settings > Plugins > Git.` in the message bar and runs no git. 1.0.4 hid them.
  - **Turning `enabled` off** clears the decorations, the `inRepository` flag, the Git menu group and the toolbar toggle, and hides an open workbench. That is 1.0.4's "complete disable".
  - **Inside a repository** the plugin runs one cached `git status` per folder sync, whatever the column layout, because the same result carries `inRepository`. 1.0.4 ran it only while the Git column was visible. Outside a repository it spawns nothing.
  - **Workspace profiles and saved layouts** no longer carry the Git flag. They never applied it: `WorkspaceChromeLayout.Apply` skips it, which `UiImprovementTests.ApplyingChromeLayout_LeavesGitAndFoldersOnTopSettingsAlone` pins today. So applying a profile or layout behaves as before.
- **T15: `FileEntry.git_status`.** It stays in the v1 wire format and binary frame, always absent. `get_git_status` (compatOnly) is removed with the other ten.

## Review Focus

The six inputs a real user will hit that the spec implies but no test pins yet. Each line names the task tests that pin it.

1. **A 1.0.4 user who turned Git integration off upgrades.** On first start nothing Git appears: no badge, no Git menu group, no toolbar toggle and no `git.exe`. Settings > Plugins > Git shows the setting off, and a later choice the user makes is never overwritten. Turning it on there decorates the open folder at once. Pinned by:
   - Task 8 `MigrateLegacySettings_CopiesAFalseLegacyValueOnce`, `MigrateLegacySettings_NeverOverwritesAValueTheUserSet` and `Activate_WaitsForTheLegacySettingsMigration`;
   - Task 11 `PluginSettingsCard_WritesThroughWriteSettingAsync`;
   - Task 13 `GetAsync_EnabledFalse_ReturnsNothingAndCallsNoBackend` and `GetAsync_TurnedOff_HidesAShownWorkbenchOnce`;
   - Task 16 smoke step 2, which reads and toggles the Settings switch itself.
2. **Discard on untracked files and folders, or on names with Unicode, brackets, spaces, `*` or a leading `-`.** Only the selected paths change. The confirmation lists them, warns that untracked files are deleted, and defaults to Cancel. Pinned by:
   - Task 12 `discard_removes_only_the_selected_untracked_paths` and `file_commands_treat_names_literally`;
   - Task 13 `DiscardConfirmationText_ListsPathsAndWarnsAboutUntrackedFiles` and `GitPrompts_DiscardDefaultsToCancel`;
   - Task 16 smoke step 5.
3. **Git's own path forms on Windows.** Git reports the root as `R:/Repos/x` with forward slashes, untracked folders as `dir/`, and case-variant paths. Badges must land on the matching rows, and stage, diff and discard from the file list must find their change. Pinned by:
   - Task 12 `decorations_mark_changed_children_with_native_paths`;
   - Task 9 `DecorateAsync_KeysDifferingInCaseOrSeparator_MatchTheListedRow`;
   - Task 11 `Decorate_KeysDifferingInCaseOrSeparator_AreKept`, because the host's key filter runs before the merge;
   - Task 13 `StageSelected_FallsBackToTheFileListSelectionFilteredByStatus`;
   - Task 14 `Decorations_ThroughTheRealHost_MarkTheChangedChildren`.
4. **Git is not installed or not on PATH.** The plugin stays Enabled and decorations stay empty, with no repeated spawn attempts. The panel and the commands say `Git was not found. Install Git for Windows or add git.exe to PATH.` Pinned by:
   - Task 12 `missing_git_reports_git_not_found_without_spawning`;
   - Task 13 `Refresh_GitNotFound_ShowsTheMessageAndKeepsThePanelUsable` and `GetAsync_GitNotFound_ReturnsNothingAndLogsOnce`.
5. **A slow or hanging repository**, such as a huge `git status`, or a remote that never answers during fetch. A folder still opens: the listing waits at most 250 ms and updates when the decorations arrive. Cancel on the fetch's Transfers row ends git and its child processes. Pinned by:
   - Task 9 `DecorateAsync_SlowSource_PublishesAfterTheBudgetAndAppliesTheLateResult`;
   - Task 11 smoke step "hello-slow";
   - Task 12 `cancelled_fetch_kills_git_and_its_children` and `spawn_in_job_assigns_the_job_before_the_process_runs`;
   - Task 14 `FetchCancelledFromItsRow_StopsGitAndFailsTheRow`.
6. **A user reopens SumaFile in a folder inside a repository**, with no navigation afterwards. The folder shown at startup gets its badges, its `inRepository` flag, so the Git menu group, and the toolbar toggle. The startup gate, which holds every decoration request until `ready`, must not leave that folder undecorated. Pinned by:
   - Task 9 `MarkStartupReady_RedecoratesTheShownFolders` and `PublishedListing_AskedBeforeTheGenerationChanged_IsDecoratedAgain`;
   - Task 11 `StartupReady_RedecoratesAfterMarkingReady`;
   - Task 11 smoke "startup folder" step.

---

## File structure

**Created**

- `plugins/git/plugin.json`, `plugins/git/protocol.json`.
- `plugins/git/backend/` (package `sumafile-git-backend`, lib `sumafile_git_backend`, bin `sumafile-git-backend`, `publish = false`):
  - `src/{lib.rs, main.rs, git.rs, run.rs, status.rs, paths.rs, commands.rs, decorations.rs, models.rs, handler.rs}`
  - `tests/{common/mod.rs, repo.rs, handler.rs, process.rs}`
- `plugins/git/ui/Directory.Build.props`, `Directory.Build.targets`: copies of the remote ones.
- `plugins/git/ui/SumaFile.Git.Model/` (`net10.0-windows`): `SumaFile.Git.Model.csproj`, `GitModels.cs`, `GitProtocol.cs`, `IGitBackend.cs`, `GitBackendClient.cs`, `GitText.cs`, `GitChangeRow.cs`, `GitWorkbenchViewModel.cs`, `GitWorkbenchController.cs`, `IGitPrompts.cs`, `GitDecorationProvider.cs`, `GitSettings.cs`, `GitPaths.cs`, `GitCommandIds.cs`.
- `plugins/git/ui/SumaFile.Git/` (19041): `SumaFile.Git.csproj`, `GitPlugin.cs`, `Views/{GitWorkbenchPanel.cs, GitDiffSplitter.cs, GitPrompts.cs, GitAutomationIds.cs, Xaml.cs, ListRows.cs}`.
- SDK (`src-winui/SimpleFile.PluginSdk/`): `IPanelProvider.cs`, `IRowDecorationProvider.cs`, `IPluginPanels.cs`, `IPluginDecorations.cs`.
- Core (`src-winui/SimpleFile.Core/`): `PluginBodyLoadQueue.cs`, `PluginPanel.cs`, `PluginPanelLayout.cs`, `RowDecorations.cs`, `RowDecorationCoordinator.cs`, `DecorationBrushKeys.cs`.
- PluginHost: `Contributions/PluginRowDecorationSource.cs`.
- App (`src-winui/SimpleFile.App/`): `MainWindow.PluginPanels.cs`, `PluginPanelWindow.cs`, `RowResizeGrip.cs`, `MainWindow.UiSmoke.Git.cs`.
- Hello fixture: `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/HelloPanels.cs`.
- Tests (`src-winui/SimpleFile.Tests/`):
  - `PluginBodyLoadQueueTests.cs`, `PluginPanelLayoutTests.cs`, `RowDecorationCoordinatorTests.cs`, `DecorationBrushKeysTests.cs`, `PluginRowDecorationSourceTests.cs`
  - `Git/{FakeGitBackend.cs, FakeGitHost.cs, GitTextTests.cs, GitBackendClientTests.cs, GitDecorationProviderTests.cs, GitWorkbenchViewModelTests.cs, GitWorkbenchControllerTests.cs, GitPluginManifestTests.cs, GitPluginSourceShapeTests.cs, GitBackendHarness.cs, GitPluginBackendTests.cs}`
- Scripts: `scripts/lib/wix-msi.ps1`, `scripts/lib/credentials.ps1`, `scripts/smoke-winui-msi-lifecycle.ps1`, `scripts/test-plugin-copy-targets.ps1`.

**Deleted (Task 15)**

- `crates/simplefile-core/src/git.rs`.
- `src-winui/SimpleFile.App/{MainWindow.Git.cs, GitWorkbenchView.xaml, GitWorkbenchView.xaml.cs, GitWorkbenchViewModel.cs, GitWorkbenchWindow.xaml, GitWorkbenchWindow.xaml.cs}`.

**Modified**, grouped by area (each task lists exact files):

- Plugin host: `Discovery/PluginDiscovery.cs`, `Trust/PluginApprovalStore.cs`, `Install/PluginPackageInstaller.cs`, `Backend/{PluginBackendHost.cs, IPluginProcessLauncher.cs}`, `PluginRecord.cs`, `PluginManager.cs`, `PluginManager.Backend.cs`, `PluginRuntime.cs`, `PluginHostServices.cs`, `Manifest/{PluginManifest.cs, PluginManifestReader.cs}`, `Contributions/PluginContributionAdapter.cs`.
- SDK: `IPlugin.cs`, `IPluginHost.cs`, `IPluginWorkspace.cs`, `IPluginDialogs.cs`, `PluginUiContracts.cs`.
- Core:
  - `CommandSurfaceLayout.cs`, `UiSettings.cs`, `WorkspaceSettingsStore.cs`, `ExplorerPane.cs`, `ExplorerWorkspace*.cs`, `WorkspaceControllers/WorkspaceNavigationController.cs`
  - `EntryPresentation.cs`, `ColumnLayout.cs`, `ToolbarActionCatalog` (in `CommandSurfaceLayout.cs`), `ContextMenuBuilder.cs`
  - plus the Task 15 removals
- App:
  - `MainWindow.xaml(.cs)`, `MainWindow.Plugins.cs`, `MainWindow.PluginSidebar.cs`, `MainWindow.Commands.cs`, `MainWindow.CommandRouting.cs`, `MainWindow.CommandSurface.cs`, `MainWindow.Chrome.cs`, `MainWindow.Columns.cs`
  - `ExplorerRows.cs`, `FileRowView.xaml.cs`, `DialogHost.cs`, `PluginWindowHost.cs`, `PluginDialogs.cs`, `PluginWorkspaceView.cs`
  - `SettingsWindow.Plugins.cs`, `SettingsWindow.CommandSurface.cs`, `SettingsWindow.SettingsState.cs`, `PluginSettingsPanelBuilder.cs`, `App.xaml`, `MainWindow.UiSmoke*.cs`, `SimpleFile.App.csproj`
- Test doubles that implement changed host interfaces: `PluginLoadingTests.cs` and `Remote/FakePluginHost.cs` (`IPluginHost`); `PluginBackendHostTests.cs`, `PluginManagerTests.cs`, `PluginTrustTests.cs` and `Remote/RemoteBackendHarness.cs` (`IPluginProcessLauncher`).
- Remote plugin: `plugins/remote/ui/SumaFile.Remote.Model/RemoteManagerViewModel.cs`, `plugins/remote/ui/SumaFile.Remote/Views/RemoteManagerWindow.cs`, `plugins/remote/backend/src/secrets.rs`.
- Rust: root `Cargo.toml`, `Cargo.lock`, `crates/simplefile-core/src/{lib.rs, models.rs}`, `crates/simplefile-service/{Cargo.toml, src/dispatch/*}`, `crates/simplefile-ipc/{src/protocol_generated.rs, tests/schema_consistency.rs}`.
- Schema and checks: `ipc/schema/v1/{commands.json, types.json, protocol.json}`, `ipc/schema/plugin/README.md`, `scripts/{generate-ipc-bindings.mjs, check-ipc-schema.mjs, check-winui-parity-gate.mjs, check-winui-packaging.mjs}`.
- Packaging and smokes:
  - `scripts/lib/plugin-staging.ps1`
  - `scripts/{build-plugins.ps1, build-winui-release.ps1, test-plugin-staging.ps1}`
  - `scripts/{smoke-winui-ui.ps1, smoke-winui-startup.ps1, smoke-winui-file-ops.ps1, smoke-winui-msi.ps1, smoke-winui-nsis.ps1, smoke-winui-upgrade.ps1}`
  - `packaging/winui/{sumafile-winui.nsi, Product.wxs}`, `package.json`, `.github/workflows/{installer-smoke.yml, release-build.yml, release.yml}`, `src-winui/SimpleFile.sln`, `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj`
- Docs: the spec, `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md`, `docs/winui-migration/{parity-gate.md, inventory.md, architecture.md, parity-remaining-workflows.md, rust-core-extraction.md}`, `README.md`, `docs/SECURITY.md`, `docs/RUST_BACKEND_AUDIT_2026-11-05.md`.

## Cross-layer wiring ledger

Phase 1's critical defect was a producer that nothing consumed. Every producer below has a consumer task and a proof.

| Producer (task) | Consumer wired in | Proof |
| --- | --- | --- |
| Install-folder defaults for roots, data, approvals; `PluginDiscovery.LogPath()` (T1) | `PreparePluginScan`/`InitializePluginsAsync`/`AppendPluginLog` (T1); smokes set the four overrides (T2); installed apps write into the install folder (T3) | T1 tests; T2 smoke assertions; T3 NSIS/MSI lifecycle smokes |
| Backend `TMP`/`TEMP` in `<data dir>\tmp` through the new `IPluginProcessLauncher` environment parameter (T1) | Every backend start in `PluginBackendHost` (T1), so the 7-Zip listfiles, 7za and git (T12) | T1 `Start_PointsTmpAndTempAtTheDataFolder` and `ProcessLauncher_PassesTheEnvironmentToTheBackend`; T2 smoke assertion on each running backend's `tmp` folder |
| `PluginRecord.IsBundledId`, `PluginPackageInstaller.IsRemovable`, installer refusals (T1) | Settings > Plugins Remove button, Install from file (T1); legacy migration gate (T8) | T1 tests; T8 tests |
| `Get-AppOwnedPluginItems`, legacy-location snapshot (T2) | `Assert-Payload` (T2); startup and UI smokes (T2); installer smokes (T3); final verification (T16) | `test-plugin-staging.ps1` cases; `release:build` |
| WiX remembered install dir and the uninstall-only purge of the four app-owned items; NSIS `InstallDirRegKey` (T3) | `smoke-winui-msi-lifecycle.ps1` (into a custom folder name), `smoke-winui-nsis.ps1` (T3) | T3 smoke runs |
| `Get-FolderSnapshot`, `Get-SumaFileUninstallEntries`, `Start-InstalledSumaFile` (T3) | NSIS and MSI lifecycle smokes (T3), `smoke-winui-upgrade.ps1` (T3, static) | `test-plugin-staging.ps1` Case 10; T3 smoke runs |
| `PluginBodyLoadQueue` with slot-prefixed keys (T4) | Sidebar body loads, `sidebar:` keys (T4); panel body loads, `panel:` keys (T10) | T4 tests and smoke; T10 smoke |
| Per-plugin load-failure teardown tracking and wait (T4) | `ScanAsync`, `EnableAsync`, `DisableAsync`, `RestartBackendAsync` and `DisposeAsync` (T4) | T4 `Rescan_AfterALoadFailure_WaitsForItsTeardown`, `Enable_AfterALoadFailure_WaitsForItsTeardown` and `Dispose_WaitsForALoadFailureTeardown` |
| `CommandSurfaceLayout.OfferedPrimaryItems`, hidden plugin items and `VisibleItems()` (T4) | `ApplyPluginToolbarPlacements` (existing); toolbar rendering, signature and overflow (T4); the Settings toolbar editor (T4); first exercised by the Git toggle (T13) | T4 tests, including `PluginToolbarItem_SurvivesASaveWhileThePluginIsDisabled`; T16 smoke steps 6 (a forced save between Disable and Enable) and 7 |
| `DialogHost.HideDialogsOn` (T5) | `PluginWindowHost.CloseEach`/`CloseAllNow` (T5), so also `PluginPanelWindow` (T10, tracked in the same set) | T5 and T10 smokes |
| `RemoteManagerViewModel.CloseSessionAsync` (T6) | `RemoteManagerWindow.OnClosed` (T6) | T6 view-model and real-backend tests |
| SDK `IPanelProvider`, `IRowDecorationProvider`, `IPluginPanels`, `IPluginDecorations`, `IPluginWorkspace.RefreshAsync`, `IPluginDialogs.Notify` (T7) | PluginManager (T8); App (T10, T11); Hello (T10, T11); Git UI (T13) | T7 surface test; T8 tests; T10, T11, T16 smokes |
| Manifest `panels`, `rowDecorations`, `toolbar[].when`, `settings[].migrateFrom`, and the adapter (T7) | App panels (T10); App decorations and toolbar `when` (T11); migration (T8); Git manifest (T13) | T7 reader and adapter tests; T13 manifest test |
| `PluginManager.CreatePanelAsync` (T8) | `MainWindow.PluginPanels` (T10) | T8 tests; T10 smoke |
| `PluginManager.GetRowDecorationsAsync` (T8) | `PluginRowDecorationSource` (T11) | T8 tests; T11 tests; T14 real-host test |
| `PluginManagerOptions.PanelsFor` → `IPluginHost.Panels` (T7, T8) | App `PluginPanelsService` (T10); Git `git-diff-selected` (T13) | T10 smoke (Hello `show-bottom` command); T16 smoke |
| `PluginManagerOptions.InvalidateDecorations` → `IPluginHost.Decorations` (T7, T8) | `ExplorerWorkspace.InvalidateDecorationsAsync` (T11); Git after stage, commit and discard (T13) | T11 tests; T16 smoke step 5 |
| `PluginManager.MigrateLegacySettingsAsync` (T8) | `InitializePluginsAsync` after the mark (T11) | T8 tests; T16 smoke step 2 |
| `PluginManager.WriteSettingAsync` (T8) | Settings > Plugins declarative controls (T11) | T8 tests; T11 `PluginSettingsCard_WritesThroughWriteSettingAsync`; T16 smoke step 2, through the Settings switch |
| `RowDecorationCoordinator` (thread-pool source calls, `Generation`) and the navigation hook's re-decoration (T9) | App source, startup gate and the ready callback's `InvalidateDecorationsAsync` (T11) | T9 tests, including `MarkStartupReady_RedecoratesTheShownFolders`; T11 source-shape tests; T11 smoke "startup folder" step |
| `RowDecorationKeys` (T9) | `PaneDecorations` (T9); `PluginRowDecorationSource`'s key filter (T11) | T9 `DecorateAsync_KeysDifferingInCaseOrSeparator_MatchTheListedRow`; T11 `Decorate_KeysDifferingInCaseOrSeparator_AreKept` |
| `PluginPanelLayout`, panel sizes in `UiSettings` (T9) | `MainWindow.PluginPanels` (T10) | T9 tests; T10 smoke |
| Git backend methods, error texts and Transfers rows (T12) | `GitBackendClient` (T13) | T12 process test; T13 client tests; T14 real-host tests; `check-ipc-schema.mjs` literals (T12 backend, T13 client) |
| Git manifest and UI assembly (T13) | Staging, release payload, NSIS (T14); App slots (T10, T11) | T14 staging and host tests; T16 smoke |
| Staged `com.sumafile.git` (T14) | Debug App output, payload, `GitBackendHarness` (T14); UI smoke (T16) | T14 asserts; T16 smoke |
| Core Git removal (T15) | No-plugin smoke (T16) | T15 guard tests; T16 `none` mode |

---

### Task 1: Keep every plugin file in the installation folder (host side)

**Files:**
- Modify `src-winui/SimpleFile.PluginHost/Discovery/PluginDiscovery.cs`:
  - `DefaultRoots` 20-26;
  - `DataRoot` 34-45;
  - `DescribeOverrides` 47-73;
  - delete `UserRoot` 78-81;
  - add `LogPath`.
- Modify `src-winui/SimpleFile.PluginHost/Trust/PluginApprovalStore.cs:34-41`.
- Modify `src-winui/SimpleFile.PluginHost/Backend/PluginBackendHost.cs` (`StartAsync` 52-75) and `Backend/IPluginProcessLauncher.cs` (`IPluginProcessLauncher.Launch`, `ProcessLauncher.Launch` 25-77): the backend environment.
- Modify `src-winui/SimpleFile.PluginHost/PluginRecord.cs`, and `PluginManager.cs` (`PluginEvaluation` 94-98, `Evaluate` 334-383).
- Modify `src-winui/SimpleFile.PluginHost/Install/PluginPackageInstaller.cs`: `Install` 27-101 and `Remove` 108-147.
- Modify `src-winui/SimpleFile.App/SettingsWindow.Plugins.cs`: 26, 215-251, 266-312 and 338-350.
- Modify `src-winui/SimpleFile.App/PluginSettingsPanelBuilder.cs`: the Remove confirmation near 330.
- Modify `src-winui/SimpleFile.App/MainWindow.Plugins.cs`: the 90 comment; delete `LocalSumaFileDirectory` 417-418; `AppendPluginLog` 436-454.
- Test: `src-winui/SimpleFile.Tests/PluginEnvironmentTests.cs` (rewrite), `PluginPackageInstallerTests.cs`, `PluginManagerTests.cs` (1794-1960), `PluginBackendHostTests.cs`, `WinUiSourceShapeTests.cs`.
- Update every `IPluginProcessLauncher` double for the new parameter: `PluginBackendHostTests.FakePluginProcessLauncher` (it records the environment), `PluginManagerTests.{BlockingThrowingLauncher, ThrowingLauncher}`, `PluginTrustTests.NeverLaunches` and `Remote/RemoteBackendHarness.RecordingLauncher`.
- Docs:
  - spec §4.1 66-78, §6.1 358-360, §7 572-575, §8.3 648-654, §9 668-677 and §11.2 841, plus a new §4.1 paragraph "Locations (phase 4 decision)";
  - `README.md:313-314`, `docs/SECURITY.md:48`;
  - the review notes: a "Phase 4 rulings" section with the user requirement.

**Interfaces:**
- Produces, in `SimpleFile.PluginHost.Discovery.PluginDiscovery`:
  - `DefaultRoots()` returns `[InstallRoot()]` unless `SUMAFILE_PLUGIN_ROOTS` is set.
  - `DataRoot()` returns `SUMAFILE_PLUGIN_DATA_ROOT` (trimmed) or `Path.Combine(AppContext.BaseDirectory, "plugins-data")`.
  - `public const string LogPathVariable = "SUMAFILE_PLUGIN_LOG_PATH";`
  - `public static string LogPath()` returns the variable (trimmed) when it is not blank, else `Path.Combine(AppContext.BaseDirectory, "plugins.log")`.
  - `DescribeOverrides()` adds, after the data-root line, `[warn] SUMAFILE_PLUGIN_LOG_PATH replaces the plugin log: <path>`.
  - `UserRoot()` no longer exists.
- Produces, in `PluginApprovalStore`: `DefaultPath()` returns the override or `Path.Combine(AppContext.BaseDirectory, "plugin-approvals.json")`.
- Produces, in `PluginRecord`: `public bool IsBundledId { get; internal set; }`. It is true when the install root's `bundled.json` lists the id, whatever the hash. `PluginEvaluation` gains a trailing `bool IsBundledId`, which `ToRecord()` copies.
- Produces, in `PluginPackageInstaller`:
  - `public static string Install(string sfpluginPath, string pluginsRoot)`: refuses before writing anything.
  - `public static void Remove(string pluginFolder, string pluginsRoot)`.
  - `public static bool IsRemovable(PluginRecord record, string pluginsRoot)`: the record's root is `pluginsRoot` (full path, case-insensitive) and `!record.IsBundledId`.
  - Exact messages, as `PluginInstallException`:
    - `"<id> ships with SumaFile and cannot be installed from a package."`
    - `"SumaFile cannot write to its plugins folder <root>: <inner message>"`, for an `UnauthorizedAccessException`.
    - `"<name> ships with SumaFile and cannot be removed. Disable it instead."`
    - `"Only plugins in SumaFile's plugins folder can be removed."`
- The App: Settings > Plugins installs into, removes from and opens `PluginDiscovery.InstallRoot()`. `CanRemove` is `PluginPackageInstaller.IsRemovable(record, PluginDiscovery.InstallRoot())`. The Remove confirmation reads `Remove <name>? Its folder in SumaFile's plugins folder is deleted. Its settings and data are kept.` `AppendPluginLog` writes to `PluginDiscovery.LogPath()`, resolved once into a static readonly field, and creates its parent folder. When that folder is not writable the write is skipped, as every log write already is on failure; it never falls back to `%LOCALAPPDATA%`.
- Produces, in `SimpleFile.PluginHost.Backend`:
  - `IPluginProcessLauncher.Launch(string executable, IReadOnlyList<string> args, string workingDirectory, IReadOnlyDictionary<string, string> environment, Action<string> onStderrLine)`. `ProcessLauncher` sets each entry in `ProcessStartInfo.Environment` on top of the inherited environment.
  - `PluginBackendHost.StartAsync` creates `<dataDir>` and `<dataDir>\tmp`, then launches with `TMP` and `TEMP` both set to `<dataDir>\tmp`. It does not empty the folder: the backend owns its contents.
  - When either folder cannot be created (`UnauthorizedAccessException` or `IOException`), it throws `PluginBackendException("SumaFile cannot create the data folder of plugin '<id>' at <path>: <inner message>")` and launches nothing.
- Unwritable approvals file: `PluginApprovalStore.Set` keeps throwing the write error. `EnableAsync` writes the approval before it changes any state, so a failed write leaves the plugin as it was and the Settings card shows the message through its existing `RunAsync` status line. `DisableAsync` keeps its order (state, teardown, approval): a failed write there stops the plugin for this session only, and the card shows the write error.

- [ ] **Step 1: Write the failing tests**

`PluginEnvironmentTests` (rewrite). Each test saves and restores every plugin variable, `SUMAFILE_PLUGIN_LOG_PATH` included.
- `DefaultRoots_AreTheInstallRootOnly`: equals `[Path.Combine(AppContext.BaseDirectory, "plugins")]`.
- `DefaultRoots_EnvOverride_ReplacesDefaultsAndSkipsEmpties`: keep the override half. `" ; "` gives `[InstallRoot()]`.
- `ApprovalStore_DefaultPath_IsInTheInstallFolderUnlessOverridden`: null and `"  "` give `Path.Combine(AppContext.BaseDirectory, "plugin-approvals.json")`; `@" C:\smoke\approvals.json "` gives `@"C:\smoke\approvals.json"`.
- `DataRoot_IsInTheInstallFolderUnlessOverridden`: `<base>\plugins-data`, with the same override cases.
- `LogPath_IsInTheInstallFolderUnlessOverridden`: `<base>\plugins.log`, with the same override cases.
- `DescribeOverrides_ReportsTheLogPathOverride`: exactly `[@"[warn] SUMAFILE_PLUGIN_LOG_PATH replaces the plugin log: C:\smoke\plugins.log"]`.
- `DefaultLocations_NeverPointIntoLocalAppDataSumaFile`: with every override unset, none of `DefaultRoots()`, `DataRoot()`, `PluginApprovalStore.DefaultPath()` or `LogPath()` starts with `Path.Combine(LocalAppData, "SumaFile")`, and each starts with `AppContext.BaseDirectory`.
- Delete `UserRoot_IsLocalAppData_EvenWithEnvOverride` and `DataRoot_IsLocalAppDataUnlessOverridden`.

`PluginPackageInstallerTests`. The root is `<temp>\plugins` with a `bundled.json` that lists `com.sumafile.sevenzip` and a folder `com.sumafile.sevenzip\plugin.json`.
- `Install_PackageWithABundledId_IsRefusedAndWritesNothing`: the message above. The bundled folder's bytes and `bundled.json` are unchanged, and no dot folder is left in the root.
- `Install_UserPackage_LandsBesideTheBundledFolders`: `com.example.pkg` is installed, and the bundled folder is untouched.
- `Install_UnwritableRoot_ExplainsTheFolderIsNotWritable`: add a Deny ACE for the current user (`CreateDirectories | CreateFiles | WriteData | AppendData`) on the root, then install. The message starts with `"SumaFile cannot write to its plugins folder "`. `Dispose` removes the ACE before deleting.
- `Remove_BundledPlugin_IsRefused`: the message above; the folder still exists.
- Rename `Remove_DeletesPluginFolderUnderUserRoot` to `Remove_DeletesAUserPluginFolderInThePluginsRoot`.
- Rename `Remove_RefusesFolderOutsideUserRoot` to `Remove_RefusesFolderOutsideThePluginsRoot`, with the new message.
- `IsRemovable_UserPluginInThePluginsRoot_IsTrue`, `IsRemovable_BundledIdWithAModifiedManifest_IsFalse`, `IsRemovable_PluginInAnotherRoot_IsFalse`.

`PluginManagerTests`:
- `UnlistedPluginInTheInstallRoot_NeedsConsent` replaces `BundledPlugin_UserRootCopy_NeedsConsent` (1851). The install root lists `com.sumafile.sevenzip` only, and `com.example.user` in the same root is `NeedsConsent` with `IsBundledId == false`.
- `BundledId_WithAModifiedManifest_NeedsConsentAndStaysABundledId`.
- `Enable_ApprovalsFolderNotWritable_ThrowsAndChangesNothing`. The approvals path lies in a temp folder with a Deny ACE for the current user (`CreateFiles | WriteData | AppendData`), as in the installer test. `EnableAsync` of a `NeedsConsent` plugin with a startup backend throws `UnauthorizedAccessException`. The record stays `NeedsConsent`, `Changed` is not raised and the launcher launched nothing. `Dispose` removes the ACE before deleting.

`PluginBackendHostTests`:
- `Start_PointsTmpAndTempAtTheDataFolder`: the recorded environment holds exactly `TMP` and `TEMP`, both `<dataRoot>\<id>\tmp`, and that folder exists.
- `Start_DataFolderNotWritable_FailsWithAClearMessage`: the data root carries the Deny ACE. The message starts with `SumaFile cannot create the data folder of plugin '`, and nothing was launched.
- `ProcessLauncher_PassesTheEnvironmentToTheBackend`: the real `ProcessLauncher` runs `cmd.exe` with the arguments `/d`, `/c` and `1>&2 echo %TMP%`, and `TMP=<temp>\x y`. Within 10 s the stderr callback receives a line that, trimmed, equals `<temp>\x y`.

`WinUiSourceShapeTests`:
- `PluginLocations_NeverResolveFromLocalAppData`: `PluginDiscovery.cs`, `PluginApprovalStore.cs`, `MainWindow.Plugins.cs` and `SettingsWindow.Plugins.cs` contain neither `LocalApplicationData` nor `UserRoot`.
- `PluginUiSources_NeverResolveTempOrUserFolders`: no `.cs` file under `plugins/*/ui` (skipping `bin` and `obj`) contains `GetTempPath`, `GetTempFileName` or `SpecialFolder.`. It covers the remote UI now and the Git UI from Task 13.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginEnvironmentTests|FullyQualifiedName~PluginPackageInstallerTests|FullyQualifiedName~PluginManagerTests|FullyQualifiedName~PluginBackendHostTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: the build fails with CS0117 (`LogPath`, `LogPathVariable`, `IsRemovable`), CS1061 (`IsBundledId`) and CS1501 (`Launch` with five arguments).

- [ ] **Step 3: Implement the resolvers, `IsBundledId` in `Evaluate`, the installer refusals and `IsRemovable`, the backend environment, the Settings targets and the log path**

`IsBundledId` is `bundledRoot is not null && bundled.ContainsKey(plugin.Id)`, computed in `Evaluate` for every discovery state. The installer reads `<pluginsRoot>\bundled.json` with `BundledPluginList.Read(pluginsRoot)` right after `ReadManifestId`. It maps `UnauthorizedAccessException` from `Directory.CreateDirectory(root)` or the staging folder to the unwritable message. Update every launcher double listed under Files: `RecordingLauncher` passes the environment on to the real `ProcessLauncher` it wraps, and the doubles that never start a process ignore it.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the filter from Step 2, then `npm run check:winui`.
Expected: `Passed!` with `Failed: 0` both times.

- [ ] **Step 5: Record the decision in the spec, README, SECURITY and the review notes**
- **Spec §4.1:** one root, `<install dir>\plugins\<plugin-id>\`. Shadowing happens only under `SUMAFILE_PLUGIN_ROOTS`. Install from file unzips into that root and refuses bundled ids. "Locations (phase 4 decision)" records:
  - the four defaults and their overrides;
  - that every backend runs with `TMP` and `TEMP` set to `plugins-data\<id>\tmp`, so its own temporary files and its children's (7-Zip listfiles, 7za, git) stay in the install folder, and that plugin UI code never resolves a temp or user folder;
  - that both installers are per user, so the install folder is normally writable. When it is not (a portable copy under `Program Files`, an admin-protected custom folder): Install from file fails with the message above; Enable fails with the approvals file's error and changes nothing, and a failed Disable write lasts for the session only; log writes are skipped; a backend fails with the data-folder message. Nothing falls back to `%LOCALAPPDATA%`;
  - **no migration**: the pre-release `%LOCALAPPDATA%\SumaFile\plugins`, `plugins-data`, `plugin-approvals.json` and `plugins.log`, used only by the unreleased commit 46860dc, are never read, written or deleted.
- **§5.3:** reword the rationale of the spike's last sentence so it no longer contradicts §4.1. Compiled XAML would need the plugin's files at the `ms-appx:///` root beside `SumaFile.exe`, outside the plugin's own folder, which §1's one-folder-per-plugin rule forbids.
- **§8.3:** Remove deletes a non-bundled plugin folder in `<install>\plugins` and keeps its settings and `plugins-data\<id>`.
- **§9:** the approval store path, and the trust sentence: "A plugin the user installed in the install root, and lists in any other root, always need consent".
- **§11.2:** profiles in `<install>\plugins-data\com.sumafile.remote\remote-profiles.db`.
- **README and SECURITY:** the same path.

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 2: Scripts, smokes and the payload follow the install-folder locations

**Files:**
- Modify `scripts/lib/plugin-staging.ps1`: add the five functions below.
- Modify `scripts/test-plugin-staging.ps1`: add Case 8 and Case 9.
- Modify `scripts/build-winui-release.ps1`: `Assert-Payload` 152-169.
- Modify `scripts/smoke-winui-startup.ps1`:
  - 9-10 `$pluginsLog`, 39-42 the offset, 85-102 the environment and comment;
  - the finally block: also `WaitForExit(5000)` on the stopped backends before removing the temp folder.
- Modify `scripts/smoke-winui-ui.ps1`: 117-157 (every mode) and the finally block.
- Modify `scripts/smoke-winui-msi.ps1`: 26-36.
- Modify `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`: `SmokePluginsAsync` 239-253.
- Modify `scripts/check-winui-packaging.mjs`: required snippets and a new forbidden-snippets list.
- Docs: spec §7 (the data-root bullet becomes "plugin locations"), §11.4 (the startup smoke reads `plugins.log` from its disposable `SUMAFILE_PLUGIN_LOG_PATH`).

**Interfaces:**
- Consumes (T1): `PluginDiscovery.LogPathVariable`, the default locations.
- Produces (PowerShell, `scripts/lib/plugin-staging.ps1`):
  - `Get-AppOwnedPluginItems -InstallDir <string> [-BundledIds <string[]>]` returns the sorted relative paths of app-owned items under an install folder:
    - `plugins.log`, `plugins.log.1`, `plugin-approvals.json` and `plugin-approvals.json.*.tmp`;
    - `plugins-data`;
    - every folder under `plugins` whose name is not a bundled id, dot folders included.
    - `-BundledIds` defaults to `$script:BundledPluginIds`.
  - `Get-AppOwnedPluginState -InstallDir <string>` returns an ordered hashtable of relative path → SHA-256 for every file under the items `Get-AppOwnedPluginItems` lists.
  - `Assert-AppOwnedPluginStateUnchanged -InstallDir <string> -Before <hashtable>` throws `App-owned plugin files changed under <dir>: <added, removed or changed paths>`.
  - `Get-LegacyPluginLocationSnapshot [-LocalAppData <string>]` returns an ordered hashtable. Each key is a path relative to `<LocalAppData>\SumaFile`, covering everything under `plugins` and `plugins-data` plus `plugin-approvals.json`, `plugins.log` and `plugins.log.1`. A file's value is `"<length>|<LastWriteTimeUtc.Ticks>"`, a folder's is `"<dir>"`. A missing item is absent.
  - `Assert-LegacyPluginLocationsUnchanged -Before <hashtable> [-LocalAppData <string>]` throws `The app changed a pre-release plugin location under %LOCALAPPDATA%\SumaFile: <added, removed or changed paths>`, naming the relative paths.
  - Both build the legacy paths from segments (`$sumaFile = Join-Path $LocalAppData 'SumaFile'`, then `Join-Path $sumaFile 'plugins.log'`), and so does Case 9. So neither the library nor the test contains a literal that the new forbidden-snippet check bans, and the check needs no exemption.
- Produces (smokes):
  - The startup, UI (every mode) and MSI smokes set `SUMAFILE_PLUGIN_LOG_PATH` to a file in their disposable folder.
  - The UI smoke also sets `SUMAFILE_PLUGIN_APPROVALS_PATH` in plain mode.

- [ ] **Step 1: Write the failing script tests and packaging checks**

`scripts/test-plugin-staging.ps1`:
- **Case 8:**
  - A temp install folder holds the staged bundled folders, `plugins\com.example.user\plugin.json`, `plugins\.com.example.user.installing-x\`, `plugins-data\x\a.txt`, `plugin-approvals.json`, `plugins.log` and `plugins.log.1`. `Get-AppOwnedPluginItems` returns exactly those six relative paths, sorted. On a clean `Install-BundledPlugins` root it returns `@()`.
  - `Get-AppOwnedPluginState` of that folder, taken twice, passes `Assert-AppOwnedPluginStateUnchanged`.
  - After changing `plugins-data\x\a.txt` the assert throws naming it; after deleting `plugins.log` it throws naming that.
- **Case 9:**
  - On a temp `-LocalAppData` folder, a snapshot with no `SumaFile` folder is empty, and an unchanged tree passes.
  - Appending to the legacy `plugins.log` (its path built with `Join-Path` segments) makes `Assert-LegacyPluginLocationsUnchanged` throw a message naming `plugins.log`.
  - A new legacy `plugins-data\x` folder throws naming `plugins-data\x`.

`scripts/check-winui-packaging.mjs`:
- `smoke-winui-startup.ps1`, `smoke-winui-ui.ps1` and `smoke-winui-msi.ps1` must contain `SUMAFILE_PLUGIN_LOG_PATH`.
- `build-winui-release.ps1` must contain `Get-AppOwnedPluginItems`.
- A new `forbiddenScriptSnippets` check: no `.ps1`, `.mjs` or `.js` file under `scripts/`, other than `check-winui-packaging.mjs` itself, contains `SumaFile\plugins.log`, `SumaFile\plugins-data` or `SumaFile\plugin-approvals.json`. There is no other exemption: the staging library and its test build these paths from segments.

`MainWindow.UiSmoke.cs`, `SmokePluginsAsync`, every mode:
- assert `PluginDiscovery.LogPath()`, `PluginApprovalStore.DefaultPath()` and `PluginDiscovery.DataRoot()` all lie under `SIMPLEFILE_UI_SMOKE_ROOT`;
- assert no `PluginDiscovery.DefaultRoots()` entry lies under `Path.Combine(LocalAppData, "SumaFile")`;
- for every plugin whose backend is running, assert that `<DataRoot>\<id>\tmp` exists: the backend got its own temp folder (Task 1).

- [ ] **Step 2: Run them to verify they fail**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1` and `npm run check:winui-packaging`
Expected: Case 8 fails with `Get-AppOwnedPluginItems is not recognized`. The packaging check prints `must include SUMAFILE_PLUGIN_LOG_PATH` and the forbidden `SumaFile\plugins.log` in `smoke-winui-startup.ps1`.

- [ ] **Step 3: Implement the five functions and change the scripts**
- `Assert-Payload` throws `WinUI payload must not contain app-owned plugin files: <list>` when `Get-AppOwnedPluginItems` returns anything.
- **The startup smoke:**
  - It points `SUMAFILE_PLUGIN_LOG_PATH` at `$approvalsDir\plugins.log`, which is a fresh file, so the offset logic goes.
  - It snapshots the legacy locations before the launch and asserts them unchanged in `finally`, after the processes are stopped.
  - It asserts `Get-AppOwnedPluginItems -InstallDir $payloadDir` is empty after the run.
- **The UI smoke:**
  - It sets `SUMAFILE_PLUGIN_LOG_PATH=<testRoot>\plugins.log` in every mode, and `SUMAFILE_PLUGIN_APPROVALS_PATH=<testRoot>\state\plugin-approvals.json` in plain mode too.
  - Before the launch it takes `Get-LegacyPluginLocationSnapshot` and `Get-AppOwnedPluginState -InstallDir <Debug exe folder>`. In `finally` it asserts both unchanged.
  - The 128-129 comment now names the Debug exe folder as the install folder it must never write into.
- **The MSI smoke:** it adds `SUMAFILE_PLUGIN_LOG_PATH = <smokeRoot>\plugins.log`.
- `scripts/check-winui-startup-budget.mjs` and `scripts/tests/startup-budget.test.mjs` read only `startup-timing.log`, which stays in `%LOCALAPPDATA%\SumaFile`, so they need no change. The forbidden-snippet check keeps any script from reading `plugins.log` from there again.

- [ ] **Step 4: Run them to verify they pass**

Run: the two commands from Step 2, then `npm run smoke:winui-ui -- -Counts 10000` and `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -PluginsDir target\fixture-plugins\com.sumafile.hello`. Before the second smoke, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1` if needed.
Expected:
- `Plugin staging tests passed.`;
- `WinUI packaging surface is wired.`;
- `UI runtime smoke passed` twice, with no legacy-location or app-owned-item failure.
The startup and MSI smokes run in Task 3's `release:build`.

- [ ] **Step 5: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 3: Installers keep user plugin files on upgrade and remove them on uninstall

**Files:**
- Modify `packaging/winui/sumafile-winui.nsi`:
  - `InstallDirRegKey`;
  - the comments at 55-58 and 63-64;
  - an Uninstall retry of `RMDir /r "$INSTDIR"`.
- Modify `packaging/winui/Product.wxs`: the util namespace, the remembered `INSTALLDIR`, and the uninstall-only purge of the four app-owned items.
- Create `scripts/lib/wix-msi.ps1`. Modify `scripts/build-winui-release.ps1` 428-507 to use it, and add `-ext WixUtilExtension`.
- Modify `scripts/lib/plugin-staging.ps1`: add `New-UserPluginSeed`, `Wait-FolderGone`, `Get-FolderSnapshot`, `Assert-FolderSnapshotUnchanged`, `Get-SumaFileUninstallEntries`, `Assert-NoSumaFileProcess` and `Start-InstalledSumaFile`. `Get-LegacyPluginLocationSnapshot` (Task 2) is rebuilt on `Get-FolderSnapshot`.
- Modify `scripts/smoke-winui-nsis.ps1` (rewritten flow below; `Find-WinUIExecutable` loses its default-path fallback at 41) and `scripts/smoke-winui-upgrade.ps1` (seed, guards and asserts, and the same fallback removed at 45; not run).
- Create `scripts/smoke-winui-msi-lifecycle.ps1`. Modify `package.json` and `.github/workflows/{installer-smoke.yml, release-build.yml, release.yml}`.
- Modify `scripts/check-winui-packaging.mjs`.
- Docs: spec §11.4 (NSIS: "user plugins, `plugins-data`, `plugin-approvals.json` and `plugins.log` under `$INSTDIR` are kept on reinstall and upgrade and removed on uninstall"; WiX: the same for any install folder name, through the four-item purge; the lifecycle smoke and its guards) and §13. The review notes record that the NSIS upgrade from the previous release is proven only by `smoke:winui-upgrade`, so that proof waits for its first CI run.

**Interfaces:**
- Consumes (T1, T2): the default locations, and `Get-AppOwnedPluginItems`, `Get-AppOwnedPluginState`, `Assert-AppOwnedPluginStateUnchanged`, `Get-LegacyPluginLocationSnapshot` and `Assert-LegacyPluginLocationsUnchanged`.
- **NSIS:**
  - `InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "InstallLocation"`.
  - The Install section still `RMDir /r`s each bundled plugin folder, and never `$INSTDIR\plugins` or `$INSTDIR\plugins-data`.
  - Uninstall runs `RMDir /r "$INSTDIR"`. If `$INSTDIR` still exists, it runs `Sleep 1500` and repeats once; a user plugin's backend can still be exiting after its kill-on-close job closed.
- **WiX** (`xmlns:util="http://schemas.microsoft.com/wix/UtilExtension"`):
  - `<Property Id="INSTALLDIR">` with `<RegistrySearch Root="HKCU" Key="Software\SumaFile\WinUI" Name="InstallDir" Type="raw" />`, so a major upgrade installs into the remembered folder.
  - `<Property Id="SUMAFILE_PURGE_SOURCE">` with the same search.
  - The purge names the four app-owned items, never the install folder itself, so it is safe for any folder name, including a custom folder that holds other files. Three properties are set in the execute sequence (`Sequence="execute"`), each before `WixRemoveFoldersEx`, which runs before `CostInitialize`. The purge condition, written out in each element, is `REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE AND SUMAFILE_PURGE_SOURCE`.
    - `<SetProperty Id="SUMAFILE_PURGE_PLUGINS" Value="[SUMAFILE_PURGE_SOURCE]plugins\" Before="WixRemoveFoldersEx" Sequence="execute">` with the purge condition.
    - `<SetProperty Id="SUMAFILE_PURGE_DATA" Value="[SUMAFILE_PURGE_SOURCE]plugins-data\" Before="WixRemoveFoldersEx" Sequence="execute">` with the purge condition.
    - `SUMAFILE_PURGE_FILES`, set twice: first with no condition to `[TempFolder]SumaFile-no-purge\`, a folder that is never created (`Action="SetSumaFilePurgeFilesDefault"`, `Before="WixRemoveFoldersEx"`); then, under the purge condition, to `[SUMAFILE_PURGE_SOURCE]` (`Action="SetSumaFilePurgeFiles"`, `After="SetSumaFilePurgeFilesDefault"`). So the two `RemoveFile` rows below always name a folder, and outside an uninstall it is a folder that does not exist. Their behaviour never depends on how Windows Installer treats an unset directory property.
  - A component `InstallDirMemory` (Directory `INSTALLDIR`, a new fixed GUID), referenced from `MainFeature`, holds:
    - `<RegistryValue Root="HKCU" Key="Software\SumaFile\WinUI" Name="InstallDir" Type="string" Value="[INSTALLDIR]" KeyPath="yes" />`;
    - `<util:RemoveFolderEx On="uninstall" Property="SUMAFILE_PURGE_PLUGINS" />` and `<util:RemoveFolderEx On="uninstall" Property="SUMAFILE_PURGE_DATA" />`;
    - `<RemoveFile Id="PurgePluginLog" Property="SUMAFILE_PURGE_FILES" Name="plugins.log*" On="uninstall" />` and `<RemoveFile Id="PurgePluginApprovals" Property="SUMAFILE_PURGE_FILES" Name="plugin-approvals.json*" On="uninstall" />`. The wildcards cover `plugins.log.1` and the approvals store's `*.tmp` files.
  - During a major upgrade the old product's removal runs with `UPGRADINGPRODUCTCODE` set. `RemoveFolderEx` skips its empty properties, and the `RemoveFile` rows point at the never-created folder, so every app-owned file is kept.
  - Once the four items are gone, Windows Installer removes the now-empty install folder, as it does today.
- **`scripts/lib/wix-msi.ps1`:**
  - `Resolve-WixBin` returns the folder holding `candle.exe`, `light.exe`, `heat.exe` and `WixUtilExtension.dll`, or `$null`. It searches the paths `build-winui-release.ps1` lists today.
  - `New-WinUIMsi -WixBin <string> -PayloadDir <string> -HarvestedWxs <string> -IconPath <string> -ProductVersion <string> -WorkDir <string> -MsiPath <string>` runs candle and light with `-ext WixUtilExtension` and the existing `-sice` list.
- **`scripts/lib/plugin-staging.ps1`:**
  - `New-UserPluginSeed -InstallDir <string>` writes `plugins\com.example.smoke-user\{plugin.json,payload.txt}` and `plugins-data\com.example.smoke-user\data.txt`. It writes `plugin-approvals.json` as `{}` only when that file is absent.
  - `Wait-FolderGone -Path <string> -TimeoutSeconds <int>` throws `<path> still exists <n> s after uninstall: <first 10 entries>`.
  - `Get-FolderSnapshot -Path <string>` returns an ordered hashtable of relative path → `"<length>|<LastWriteTimeUtc.Ticks>"` for files and `"<dir>"` for folders, or an empty one when the folder is missing. It hashes nothing, so it is quick on a full install folder. `Assert-FolderSnapshotUnchanged -Path <string> -Before <hashtable> -Label <string>` throws `<label> changed under <path>: <added, removed or changed paths>`.
  - `Get-SumaFileUninstallEntries -Scope CurrentUser|Machine` returns the uninstall entries whose `DisplayName` is "SumaFile": HKCU for `CurrentUser`; HKLM and HKLM `WOW6432Node` for `Machine`. Each comes with its key name, `DisplayVersion` and `InstallLocation`. Machine entries are only ever read, to snapshot them.
  - `Assert-NoSumaFileProcess` throws `Close SumaFile first: <name> (<pid>) <path> is running; the installer ends it by name.` for any `SumaFile`, `simplefile-service` or `sumafile-*-backend` process.
  - `Start-InstalledSumaFile -Exe <string> -SmokeRoot <string>` starts the app through a `ProcessStartInfo` whose environment has none of `SUMAFILE_PLUGIN_ROOTS`, `SUMAFILE_PLUGIN_DATA_ROOT`, `SUMAFILE_PLUGIN_APPROVALS_PATH` or `SUMAFILE_PLUGIN_LOG_PATH`. It sets `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` under `-SmokeRoot`, and sets `GIT_CONFIG_GLOBAL=<SmokeRoot>\gitconfig` (an empty file) and `GIT_CONFIG_NOSYSTEM=1`. It refuses an `-Exe` outside `-SmokeRoot`.
  - It uses Task 2's `Get-AppOwnedPluginState` and `Assert-AppOwnedPluginStateUnchanged`.
- **`package.json`:** `"smoke:winui-msi-lifecycle": "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/smoke-winui-msi-lifecycle.ps1"`.

- [ ] **Step 1: Write the failing packaging checks**
- `nsisSnippets` add `InstallDirRegKey HKCU`.
- A forbidden NSIS list of exact strings: `never touched`, `RMDir /r "$INSTDIR\plugins"` (the closing quote right after `plugins`, so the per-plugin lines do not match) and `RMDir /r "$INSTDIR\plugins-data"`.
- `wxsSnippets` add `xmlns:util="http://schemas.microsoft.com/wix/UtilExtension"`, `util:RemoveFolderEx`, `NOT UPGRADINGPRODUCTCODE`, `Name="InstallDir"`, `SUMAFILE_PURGE_PLUGINS`, `SUMAFILE_PURGE_DATA`, `SumaFile-no-purge`, `Name="plugins.log*"` and `Name="plugin-approvals.json*"`.
- A forbidden WiX snippet: `Property="SUMAFILE_PURGE_SOURCE"` inside a `RemoveFolderEx`, which would purge the whole install folder.
- `buildSnippets` add `WixUtilExtension` and `New-WinUIMsi`.
- `requiredFiles` add `scripts/lib/wix-msi.ps1` and `scripts/smoke-winui-msi-lifecycle.ps1`.
- `npmSnippets` add `"smoke:winui-msi-lifecycle"`.
- `workflowSnippets` add `smoke:winui-msi-lifecycle` for `installer-smoke.yml`, `release-build.yml` and `release.yml`.
- `installerSmokeSnippets` (new, over `smoke-winui-nsis.ps1`, `smoke-winui-msi-lifecycle.ps1` and `smoke-winui-upgrade.ps1`): `Get-SumaFileUninstallEntries -Scope CurrentUser`, `Assert-NoSumaFileProcess`, `Get-FolderSnapshot` and `Start-InstalledSumaFile`. A forbidden snippet in the same three files: `Programs\SumaFile-WinUI\SumaFile.exe`, the default-path fallback that `smoke-winui-nsis.ps1:41` and `smoke-winui-upgrade.ps1:45` have today. So no smoke can launch the default install folder's executable.

Add `test-plugin-staging.ps1` **Case 10**:
- On a temp install folder, after `New-UserPluginSeed`, `Get-AppOwnedPluginState` lists exactly the three seeded files and `plugin-approvals.json`.
- `Wait-FolderGone` on a missing folder returns at once, and on a folder that stays throws within the timeout.
- `Get-FolderSnapshot` of a missing folder is empty. After a file's length or time changes, or a file is added, `Assert-FolderSnapshotUnchanged` throws naming it.
- `Start-InstalledSumaFile` refuses an `-Exe` outside `-SmokeRoot`. It starts nothing for that check.

- [ ] **Step 2: Run them to verify they fail**

Run: `npm run check:winui-packaging` and the staging test script.
Expected: `packaging/winui/sumafile-winui.nsi must include InstallDirRegKey HKCU.`, and `New-UserPluginSeed is not recognized`.

- [ ] **Step 3: Implement the installer changes, `wix-msi.ps1`, the seed and state functions, and the smokes**

Both installer smokes start with the same guards, in this order:
- `Assert-NoSumaFileProcess`;
- refuse when `Get-SumaFileUninstallEntries -Scope CurrentUser` returns anything (an HKCU entry). A machine-wide entry, such as the 1.0.4 product on this machine, never makes them refuse;
- `$machineBefore = Get-SumaFileUninstallEntries -Scope Machine`;
- `$defaultBefore = Get-FolderSnapshot "$env:LOCALAPPDATA\Programs\SumaFile-WinUI"`;
- `$legacy = Get-LegacyPluginLocationSnapshot`.

Their `finally` blocks stop processes only by path under the smoke root. At the end they assert the machine entries equal `$machineBefore`, and run `Assert-FolderSnapshotUnchanged` on the default folder and `Assert-LegacyPluginLocationsUnchanged`.

`scripts/smoke-winui-nsis.ps1`, each step bounded:
1. Run the guards.
2. Install with `Start-Process $installer -ArgumentList "/S /D=$installDir" -Wait`, where `$installDir` is `%TEMP%\sumafile-nsis-smoke-<guid>\SumaFile-WinUI`. Pass one argument string: `/D=` must come last and must not be quoted.
3. Check version and bundled plugins as today. The executable is `<installDir>\SumaFile.exe` and nothing else: `Find-WinUIExecutable` reads only the HKCU entry's `InstallLocation` and fails when it is not `$installDir`. Launch it with `Start-InstalledSumaFile -SmokeRoot <the guid folder>`. Within 25 s require `<installDir>\plugins.log`, `<installDir>\plugins-data\com.sumafile.sevenzip` and `<installDir>\plugins-data\com.sumafile.sevenzip\tmp`: the defaults land in the install folder.
4. Close the app and stop the processes under `<installDir>`.
5. `New-UserPluginSeed`, then `$before = Get-AppOwnedPluginState`.
6. Before the reinstall, require the HKCU entry's `InstallLocation` to equal `$installDir`, which is the value `InstallDirRegKey` reads. Reinstall with `/S` and no `/D=`. Then:
   - `Assert-FolderSnapshotUnchanged` on the default folder against `$defaultBefore`. If `InstallDirRegKey` regressed, the reinstall went to the default folder; the smoke then fails here, names what changed, and does not go on;
   - assert the uninstall key's `InstallLocation` still equals `$installDir`;
   - `Assert-AppOwnedPluginStateUnchanged` and `Assert-BundledPlugins`.
7. Uninstall, then `Wait-FolderGone $installDir 60`.
8. Run the end assertions. The `finally` still uninstalls when the HKCU entry exists.

`scripts/smoke-winui-msi-lifecycle.ps1`:
1. Require `dist\winui\payload\SumaFile.exe`, `dist\wix\harvested.wxs` and `Resolve-WixBin`. Run the guards.
2. Build `v1.msi` at the payload's numeric version and `v2.msi` with the third field + 1, into `%TEMP%\sumafile-msi-lifecycle-<guid>\wix`, with `New-WinUIMsi`. Read each ProductCode through the `WindowsInstaller.Installer` COM object, and refuse when either is already registered.
3. Run `msiexec /i v1.msi /qn INSTALLDIR="<root>\SumaFile Custom" /l*v v1.log`, with no trailing backslash before the closing quote, and require exit code 0. The folder name is deliberately not `SumaFile-WinUI`, so the run proves the purge does not depend on the name.
4. Run `Assert-BundledPlugins`. Launch, close and seed exactly as steps 3-5 of the NSIS flow, with `HKCU\Software\SumaFile\WinUI\InstallDir` in place of the uninstall entry.
5. Before the upgrade, require `HKCU\Software\SumaFile\WinUI\InstallDir` to equal `<root>\SumaFile Custom\`. Run `msiexec /i v2.msi /qn /l*v v2.log`, with no `INSTALLDIR`, and require exit code 0 and that v1's ProductCode is no longer registered. Then:
   - `Assert-FolderSnapshotUnchanged` on the default folder, as in NSIS step 6;
   - `Assert-AppOwnedPluginStateUnchanged` and `Assert-BundledPlugins` on `<root>\SumaFile Custom`.
6. Run `msiexec /x <v2 ProductCode> /qn /l*v x.log`, then `Wait-FolderGone "<root>\SumaFile Custom" 60`, then the end assertions.
7. Print `WinUI MSI lifecycle smoke passed: the upgrade kept the user plugin and its data; uninstall left nothing in <dir>.`
8. `finally`: stop the processes under the folder, `msiexec /x` either ProductCode still registered, and remove the temp root.

`scripts/smoke-winui-upgrade.ps1`: install the previous release with `/D=<disposable>`, run the same guards, seed right after, assert after the upgrade, and `Wait-FolderGone` after the final uninstall. Checked statically only.

Workflows:
- `installer-smoke.yml`: a step `Smoke WinUI MSI install, upgrade, and uninstall` (`npm run smoke:winui-msi-lifecycle`) after the NSIS install step.
- `release-build.yml` and `release.yml`: the same step, after their `smoke:winui-upgrade` step. Both already install WiX (`Install WiX Toolset (MSI)`) and run `build-winui-release.ps1` before that point, so `dist\wix\harvested.wxs` and `Resolve-WixBin` are available.

- [ ] **Step 4: Run the checks and the installer smokes**

Close every running SumaFile first: the installer smokes refuse otherwise (`Assert-NoSumaFileProcess`).

Run, in order:
1. `npm run check:winui-packaging`, which also checks the NSIS and WiX snippets before any installer runs
2. `npm run test:maintenance`
3. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1`
4. `npm run release:build`, which builds both installers, `Assert-Payload`, and the startup, file-ops and MSI-extract smokes
5. `npm run smoke:winui-installer`
6. `npm run smoke:winui-msi-lifecycle`

Expected:
- `WinUI packaging surface is wired.`;
- `release:build` exit 0, including `WinUI startup smoke passed` and `WinUI MSI artifact smoke passed`;
- `WinUI NSIS install smoke passed` followed by a successful uninstall with the folder gone;
- `WinUI MSI lifecycle smoke passed: …`.

Afterwards no HKCU "SumaFile" uninstall entry and neither lifecycle ProductCode remains. The HKLM 1.0.4 entry and the default-path folder are unchanged; both smokes assert this themselves.

- [ ] **Step 5: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 4: Carry-overs (a), (g) and the (h) lever: plugin host concurrency, toolbar offers and a warm first render

**Files:**
- Modify `src-winui/SimpleFile.PluginHost/PluginManager.cs`: `MarkLoadFailedAsync` 745-765, `DisposeAsync` 767-803, `ScanAsync` 228-288, `EnableAsync` 412-439, `DisableAsync` 445-468, `RestartBackendAsync` 471-516, and a `_loadFailureTeardowns` map.
- Create `src-winui/SimpleFile.Core/PluginBodyLoadQueue.cs`. Modify `src-winui/SimpleFile.Core/PluginSidebarLayout.cs`: `BodyLoadKey`.
- Modify `src-winui/SimpleFile.App/MainWindow.PluginSidebar.cs`: `ActivateVisiblePluginSections` 239-255, `TogglePluginSection` 192-209 and `QueuePluginSectionActivation` 226-233.
- Modify `src-winui/SimpleFile.App/PluginWorkspaceView.cs` (`NavigateAsync` 41-50) and `PluginDialogs.cs` (a new `PluginUiDispatch.EnqueueAsync`).
- Modify `src-winui/SimpleFile.Core/CommandSurfaceLayout.cs`: `EnsurePrimaryItem` 66-87, `Normalize` 126-172, `UpgradeLegacyDefaultToolbar` 174-200 (carry-over f, the dead `previousDefault`), `VisiblePrimaryActionIds` 201-208 and `Signature` 209-215.
- Modify `src-winui/SimpleFile.App/MainWindow.CommandSurface.cs` (`ApplyCommandSurfaceLayout` 15-55: render `VisibleItems()`), `SettingsWindow.CommandSurface.cs` (`ToolbarEditorRow` 8-36, `CurrentCommandSurfaceLayout` 59-68, Reset 157-160) and `SettingsWindow.SettingsState.cs` (`ApplyTo` 59, `CloneCommandSurfaceLayout` 146-156).
- Modify `src-winui/SimpleFile.Core/WorkspaceSettingsStore.cs` (`SaveAsync` 138-232) and `MainWindow.xaml.cs` (`ConnectAsync` 201-276, the warm-up call).
- Test:
  - `PluginManagerTests.cs`, including the flaky test at its current line (carry-over g);
  - new `PluginBodyLoadQueueTests.cs`;
  - `CommandSurfaceLayoutTests.cs`, `PluginSidebarLayoutTests.cs`, `WorkspaceSettingsStoreTests.cs`, `WinUiSourceShapeTests.cs`.
- Docs: the review notes (closures for #63, all four of its operations; #65/#69; #78/#84; #82 for sidebar bodies and `NavigateAsync`, with decorations and panels following in Tasks 9-11; EnsurePrimaryItem and the toolbar rule; and (g)); spec §5.2 (the warm-up result); spec §7 (the toolbar rule).

**Interfaces:**
- Produces (PluginHost):
  - `MarkLoadFailedAsync` sets LoadFailed under the lock, raises `Changed` immediately, and then starts the teardown (windows, backend, deactivation) as a tracked task. It returns without awaiting it, so `LoadFailedAsync` hands the `PluginLoadException` back at once.
  - `_loadFailureTeardowns` is a `Dictionary<string, Task>` keyed by plugin id, under `_gate`. A second load failure of the same id chains onto the first task. An entry removes itself when its task ends.
  - `private Task WaitForLoadFailureTeardownAsync(string? pluginId)` awaits the tracked teardown of `pluginId`, or of every plugin when it is null. It returns a completed task, with no `await`, when nothing is tracked, so discovery's startup path on the UI thread gains no yield. The bound is `internal TimeSpan LoadFailureTeardownWait`, by default the one `DisposeAsync` uses: `WindowCloseTimeout` plus the runtime's retire timeouts. Past it, it logs `[warn] Plugin '<id>' is still tearing down after its load failure; continuing.` and returns.
  - `ScanAsync` (so `DiscoverAsync` and `RescanAsync`), `EnableAsync`, `DisableAsync` and `RestartBackendAsync` call it right after `_ops.WaitAsync`, before they create a runtime, retire one or start a backend. `ScanAsync` passes null and the other three their id. The teardown never takes `_ops`, so waiting under it cannot deadlock. A new backend therefore never starts while the old one still owns the pipe `SumaFile.Plugin.<session>.<pid>.<id>`, and a new `ActivateAsync` never races the old instance's `DeactivateAsync` in the same `PluginLoadContext`.
  - `DisposeAsync` awaits every tracked teardown after its own records' teardown, with the same bound.
- Produces (Core):
  - `public sealed class PluginBodyLoadQueue(Action<Action> enqueue)` with:
    - `public bool Request(string key, Func<Task> load)`: false when `key` is already pending; never runs `load` inline;
    - `public void Forget(string key)`: a queued load for a forgotten key does not run;
    - `public bool IsPending(string key)`.
  - The queued callback clears the pending mark before it starts `load`.
  - Keys carry their slot: `PluginSidebarLayout.BodyLoadKey(section)` is `"sidebar:" + Key(section)`, and Task 9 adds `PluginPanelLayout.BodyLoadKey(panel)` = `"panel:" + Key(panel)`. So a section and a panel with the same id never drop each other's load.
  - **Toolbar rule.** A saved toolbar keeps a plugin's item while the plugin is absent, and an offer is made once:
    - `CommandSurfaceLayout.IsPluginItemId(string id)` is true for `plugin:<id>:<command>`, where `<id>` follows the plugin id grammar and `<command>` is 1-128 characters with no whitespace.
    - `Normalize` keeps a command item whose id the catalog knows or that `IsPluginItemId` accepts, so a disabled, failed or missing plugin's item survives `Normalize`, `ToJson` and `FromJson` in place. It still drops unknown core ids, such as the retired `git-*` and `ftp-sftp-manager`.
    - `public IReadOnlyList<CommandSurfaceItem> VisibleItems()` is the toolbar as drawn: only items whose id the catalog knows, with no leading, trailing or doubled separators. `VisiblePrimaryActionIds()`, `Signature()` (so a plugin's return re-renders the toolbar), the overflow planner and the toolbar rendering in `MainWindow.CommandSurface.cs` all use it.
    - `OfferedPrimaryItems` is a `List<string>`, serialized with the layout (`offeredPrimaryItems`). `Normalize` deduplicates it and never filters it by the catalog.
    - `EnsurePrimaryItem(string commandId)` returns false, and changes nothing, when `OfferedPrimaryItems` already holds the id. Otherwise it inserts the id before Settings when absent, records it as offered, and returns true. So the user's removal of an offered item is final, and an item that is merely hidden comes back by itself when its plugin does.
  - `UpgradeLegacyDefaultToolbar` matches only `previousDefaultWithoutFtp`.
  - `WorkspaceSettingsStore.SaveAsync` computes every value it writes on the caller's thread, before its first `await`. That covers the normalized settings, the JSON of `CommandSurface`, `FolderViewSettings`, the shortcut overrides, the plugin section keys, the bookmarks and the recents. It never assigns a list property of `settings` after an `await`.
- Produces (App):
  - `TogglePluginSection` replaces `CollapsedPluginSidebarSections` copy-on-write: `settings.CollapsedPluginSidebarSections = [.. old, key]` or `[.. old.Where(k => k != key)]`.
  - `ActivateVisiblePluginSections` only calls `_pluginBodyLoads.Request(PluginSidebarLayout.BodyLoadKey(section), () => LoadPluginSectionBodyAsync(view))`. `_pluginBodyLoads` is a `PluginBodyLoadQueue` over `DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, …)`.
  - `PluginWorkspaceView.NavigateAsync` uses `PluginUiDispatch.EnqueueAsync`, which always queues, so a plugin can never re-enter a workspace sync inline.
  - `MainWindow.WarmPluginRendering()` is queued at Low priority right after `PreparePluginScan()` in `ConnectAsync`, as `DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => WarmPluginRendering())`. It runs the four adapters on empty lists, then builds and discards one sidebar section header (`CreatePluginSectionView` of a placeholder section). It never touches `PluginSectionsHost` or plugin state.
  - **Settings > Toolbar** keeps the offers and the hidden items:
    - `ToolbarEditorRow` shows an item whose plugin is absent with its raw id and the detail `Plugin not loaded`, so the user can still remove it;
    - `CurrentCommandSurfaceLayout` and `CloneCommandSurfaceLayout` copy `OfferedPrimaryItems`, and `ApplyTo` carries the current layout's offers into the new one;
    - Reset clears the carried offers, so the enabled plugins offer their items again after a reset.

- [ ] **Step 1: Write the failing tests**

`PluginManagerTests`:
- `LoadFailure_RaisesChangedBeforeTheTeardownFinishes`. The window host's `CloseAllAsync` returns a task the test completes. When the sidebar provider throws, `Changed` is observed and `EnabledPlugins` excludes the plugin while that task is still pending.
- `CreateSidebarSection_ProviderThrows_ThrowsBeforeTheTeardownFinishes`. With the same gate, the `PluginLoadException` arrives while the gate is closed.
- `Dispose_WaitsForALoadFailureTeardown`. After a load failure with the gate closed, `DisposeAsync()` has not completed after 300 ms; it completes within 2 s of opening the gate.
- `Rescan_AfterALoadFailure_WaitsForItsTeardown`, with the same gated window host and a plugin whose backend starts at startup (`activateOn: startup`), so discovery launches it once. When its sidebar provider throws, the plugin is LoadFailed with the gate closed, and `RescanAsync` then starts:
  - 300 ms later the launcher has still launched once, and `RescanAsync` has not completed;
  - within 2 s of opening the gate, `RescanAsync` completes and the launcher has launched exactly twice.
- `Disable_AfterALoadFailure_ReturnsOnlyAfterItsTeardown`: the same setup, then `DisableAsync` while the gate is closed. It has not completed after 300 ms, and completes within 2 s of opening the gate.
- `Enable_AfterALoadFailure_WaitsForItsTeardown`: the same setup, then `DisableAsync` and `EnableAsync` started while the gate is closed. No second launch happens until the gate opens, and exactly one follows within 2 s.
- `RestartBackendAsync` gets the same wait. No separate test: a plugin cannot be Enabled or Faulted again while its load-failure teardown runs without a Rescan or an Enable, which the tests above cover.
- `LoadFailureTeardownWait_IsBoundedAndLogged`: with `LoadFailureTeardownWait` at 200 ms and the window gate closed, `RescanAsync` completes within 2 s, and the log holds `is still tearing down after its load failure; continuing.` The test then opens the gate, so the teardown ends before the manager is disposed.
- Carry-over (g): rewrite `ArchivePlugin_SuccessfulRegistration_LogsTheRegisteredFormats` to wait, bounded at 10 s, for the log line `[info] Archive engines registered: .hello` instead of `BackendRunning`.

`PluginBodyLoadQueueTests`:
- `Request_NeverRunsTheLoadInline`: the load runs only when the captured enqueue callback runs.
- `Request_SameKeyWhilePending_IsQueuedOnce`.
- `Forget_DropsAQueuedLoad`.
- `Request_AfterTheLoadStarted_QueuesAgain`.

`PluginSidebarLayoutTests`:
- `BodyLoadKey_CarriesTheSidebarSlot`: `BodyLoadKey(section)` is `sidebar:` followed by `Key(section)`.

`CommandSurfaceLayoutTests` (in the existing `Plugin catalogs` collection; each test restores `ToolbarActionCatalog.SetPluginActions([])`):
- `EnsurePrimaryItem_AfterTheUserRemovedIt_DoesNotAddItAgain`.
- `OfferedPrimaryItems_SurviveNormalizeAndJsonWhileThePluginIsAbsent`: an id the catalog does not know stays in `OfferedPrimaryItems` across `ToJson`/`FromJson`.
- `PluginToolbarItem_SurvivesASaveWhileThePluginIsDisabled`:
  1. The catalog holds `plugin:com.a:run`; `EnsurePrimaryItem` adds it before Settings.
  2. The catalog is cleared (the plugin is disabled). After `ToJson` and `FromJson`, `PrimaryToolbar` still holds the item at the same index, and `VisibleItems()`, `VisiblePrimaryActionIds()` and `Signature()` leave it out.
  3. The catalog holds it again (the plugin is re-enabled). `VisiblePrimaryActionIds()` shows it at its old position, `Signature()` differs from step 2, and `EnsurePrimaryItem` returns false.
- `PluginToolbarItem_RemovedByTheUser_StaysRemovedAcrossDisableAndEnable`: as above, but the item is removed from `PrimaryToolbar` before step 2; after step 3 it is still absent.
- `Normalize_DropsUnknownCoreIdsButKeepsWellFormedPluginIds`: `missing`, `git-panel`, `plugin:` and `plugin:BAD ID:x` are dropped; `plugin:com.a:run` is kept.
- `VisibleItems_CollapseTheSeparatorsAroundHiddenItems`: `[settings, |, plugin:com.a:run, |, copy]` with the plugin absent draws `settings, |, copy`.
- `EnsurePrimaryItem_FirstOffer_InsertsBeforeSettings`: an update of `EnsurePrimaryItem_InsertsBeforeSettingsOnce`.
- `FromJson_PreviousDefaultWithTheCoreFtpCommand_UpgradesToTheCurrentDefault`: a stored 1.0.4 default that still contains `ftp-sftp-manager` upgrades.

`WorkspaceSettingsStoreTests`:
- `SaveAsync_ListsChangedDuringTheSave_SaveTheCallTimeValuesAndKeepTheChange`. A fake `ISettingsBackend` replaces `settings.CollapsedPluginSidebarSections` (copy-on-write) and calls `settings.CommandSurface.EnsurePrimaryItem("plugin:a:b")` inside its first `SetSettingAsync`. The save does not throw. The stored `sidebar.pluginSectionsCollapsed` and `commandSurface.layout` are the call-time values. Afterwards the live list still holds the added key.

`WinUiSourceShapeTests`:
- `ActivateVisiblePluginSections_OnlyQueuesBodyLoads`: the method body contains `_pluginBodyLoads.Request(PluginSidebarLayout.BodyLoadKey(` and no direct `LoadPluginSectionBodyAsync(` call outside that lambda.
- `PluginWorkspaceView_NavigateAsync_AlwaysQueues`: the body uses `EnqueueAsync` and not `RunAsync`.
- `TogglePluginSection_ReplacesTheCollapsedListInsteadOfMutatingIt`: no `.Add(` or `.Remove(` on `CollapsedPluginSidebarSections`.
- `ConnectAsync_WarmsPluginRenderingBeforeAwaitingTheService`: between `PreparePluginScan()` and the `backend-started` mark there is a `TryEnqueue(DispatcherQueuePriority.Low,` call whose argument contains `WarmPluginRendering(`. The test matches `WarmPluginRendering(`, not `WarmPluginRendering();`, so the queued lambda form passes.
- `PrimaryToolbar_RendersOnlyTheVisibleItems`: `ApplyCommandSurfaceLayout` in `MainWindow.CommandSurface.cs` iterates `VisibleItems()`, not `PrimaryToolbar`.
- `SettingsToolbarEditor_CarriesTheOfferedItems`: `CurrentCommandSurfaceLayout` and `CloneCommandSurfaceLayout` both assign `OfferedPrimaryItems`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManagerTests|FullyQualifiedName~PluginBodyLoadQueueTests|FullyQualifiedName~PluginSidebarLayoutTests|FullyQualifiedName~CommandSurfaceLayoutTests|FullyQualifiedName~WorkspaceSettingsStoreTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: the build fails (`PluginBodyLoadQueue`, `OfferedPrimaryItems`, `VisibleItems`, `BodyLoadKey`). After stubs: the new manager tests, the toolbar tests, the save test and the six source-shape tests FAIL.

- [ ] **Step 3: Implement the manager, queue, layout, toolbar rendering, Settings editor, store, sidebar, workspace-view and warm-up changes**

- [ ] **Step 4: Run them to verify they pass, then measure the span**

Run: the filter from Step 2, then `npm run check:winui`.
Expected: `Passed!` with `Failed: 0`.

Then measure the effect of the warm-up:
1. Start the Debug App five times in the `-PluginsDir` smoke's plugin environment, once on a build without the queued `WarmPluginRendering()` call (take this baseline before Step 3) and once with it. Use a scratch harness outside the repository, as the phase 3 fix report did.
2. Take the median of the `plugins-discovered` lap, which `npm run check:winui-startup-budget` prints.
3. Keep the warm-up unless its median is higher than the baseline, and record both medians in spec §5.2. If you remove it, also remove `ConnectAsync_WarmsPluginRenderingBeforeAwaitingTheService`.

Finally run `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -PluginsDir target\fixture-plugins\com.sumafile.hello` and then `npm run check:winui-startup-budget`.
Expected: `UI runtime smoke passed`, and `WinUI startup budget passed` with the span under 50 ms.

- [ ] **Step 5: Record the closures in the review notes and the measurements in spec §5.2**

The (h) closure records the local medians and says plainly that the hosted-runner evidence (74.2 ms at 46860dc) is still pending, because CI is not run in this phase. The #63 closure names all four operations and their tests. The #82 closure covers sidebar bodies and `NavigateAsync` here; Task 11 extends it to decorations and panels.

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 5: Carry-over (d): host-closed plugin windows release the app-wide dialog gate

**Files:**
- Modify `src-winui/SimpleFile.App/DialogHost.cs`: `ShowAsync` 20-35, `RunWithProgressAsync` 42-96, and a new `HideDialogsOn`.
- Modify `src-winui/SimpleFile.App/PluginWindowHost.cs`: `CloseEach`/`TryClose` 103-125 and `CloseAllNow` 97-101.
- Modify `plugins/remote/ui/SumaFile.Remote/Views/RemoteManagerWindow.cs`: the connect progress dialog's `Hide()` calls near 462-499.
- Modify `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`: `SmokePluginDisableClosesWindowsAsync` 561-586.
- Test: `WinUiSourceShapeTests.cs`, `Remote/RemotePluginSourceShapeTests.cs`.
- Docs: spec §7 teardown bullet ("open dialogs on a closing plugin window are hidden first; the gate is released even if WinUI never completes their `ShowAsync`"); review notes, closure of (d).

**Interfaces:**
- Produces (App, `DialogHost`):
  - `internal static int HideDialogsOn(XamlRoot? root)` hides, with try/catch, every dialog shown through the host whose `XamlRoot` is `root`, and returns how many it hid.
  - `ShowAsync` awaits `Task.WhenAny(dialog.ShowAsync().AsTask(), hidden.Task)`, where `HideDialogsOn` completes `hidden`. So the gate and `_openCount` are released even if WinUI never completes `ShowAsync`; the result is then `ContentDialogResult.None`.
  - `RunWithProgressAsync` guards both `dialog.Hide()` calls (the `Opened` handler and the completion callback) with a null-`XamlRoot` check and try/catch.
- `PluginWindowHost.CloseEach` and `CloseAllNow` call `DialogHost.HideDialogsOn(window.Content?.XamlRoot)` before `window.Close()`. This covers Disable, rescan, load failure and main-window close, which all go through these two methods.

- [ ] **Step 1: Write the failing source-shape tests and the smoke assertion**

`WinUiSourceShapeTests`:
- `PluginWindowHost_HidesOpenDialogsBeforeClosingAWindow`: in `TryClose`'s caller, `HideDialogsOn(` precedes `.Close()`.
- `DialogHost_ShowAsync_CompletesWhenTheHostHidesTheDialog`: `ShowAsync` contains `Task.WhenAny(`.
- `DialogHost_RunWithProgress_GuardsEveryHide`: every `dialog.Hide()` in `RunWithProgressAsync` sits inside a `try`.

`RemotePluginSourceShapeTests`:
- `ManagerWindow_ProgressDialogHide_IsGuarded`.

Smoke, in `SmokePluginDisableClosesWindowsAsync`, before `DisableAsync`:
1. Invoke `Hello.WindowDialog` and wait for "Hello window dialog" with `SmokeOpenDialogAsync(window.Content.XamlRoot, …)`. Assert `DialogHost.IsDialogOpen`.
2. After the disable, within 10 s, require `!DialogHost.IsDialogOpen`.
3. Then show and close a host dialog, "Smoke gate check", on the main window through `DialogHost.ShowAsync`, `SmokeOpenDialogAsync` and `SmokeCloseDialog`, within 10 s.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests|FullyQualifiedName~RemotePluginSourceShapeTests"`
Expected: the four new tests FAIL.

- [ ] **Step 3: Implement `HideDialogsOn`, the `WhenAny` completion, the guards and the window-host calls**

- [ ] **Step 4: Run them to verify they pass, and run the plugin smoke**

Run: the filter from Step 2, then `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`.
Expected: `Passed!`, then `UI runtime smoke passed` with `plugins=1`.

- [ ] **Step 5: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 6: Carry-overs (b), (c), (e) and (f): remote plugin hardening and small leftovers

**Files:**
- (b) Modify `plugins/remote/ui/SumaFile.Remote.Model/RemoteManagerViewModel.cs` (`RunTransfersAsync` near 780-860, a new `CloseSessionAsync`) and `plugins/remote/ui/SumaFile.Remote/Views/RemoteManagerWindow.cs` (`OnClosed` 355-378).
- (c) Modify `plugins/remote/backend/src/secrets.rs`: tests.
- (e) Modify `src-winui/SimpleFile.App/MainWindow.UiSmoke.Remote.cs`: step 11 at 300-312, cleanup at 324-361, and the awaits at 101, 108, 245, 276, 302, 383, 422, 482, 507 and 620. Modify `scripts/smoke-winui-ui.ps1`, and create `scripts/lib/credentials.ps1`.
- (f) Remove `sha2` from `crates/simplefile-service/Cargo.toml`, with `Cargo.lock`.
- (f) Fix the `ReplaceLocalEntries` comment in `RemoteManagerViewModel.cs` (near 900).
- (f) `scripts/lib/plugin-staging.ps1` `Get-BundledPluginFreshnessFiles` adds `SumaFile.Remote.deps.json`; update `scripts/test-plugin-staging.ps1` Case 7.
- (f) Prune stale copies in `src-winui/SimpleFile.App/SimpleFile.App.csproj` (`CopyBundledPlugins` 92-101) and `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj` (`CopyRemotePluginFixture` 57-68). Create `scripts/test-plugin-copy-targets.ps1`, which runs the real `CopyBundledPlugins` target against temp folders.
- (f) Fix the `docs/winui-migration/parity-gate.md:5` header.
- Test: `Remote/RemoteManagerViewModelTests.cs`, `Remote/RemotePluginBackendTests.cs`, `WinUiSourceShapeTests.cs`.
- Docs: the review notes, closures of (b), (c), (e) and (f).

**Interfaces:**
- Produces (remote model):
  - `public Task<bool> CloseSessionAsync(TimeSpan transferWait, CancellationToken ct = default)`. It cancels a running transfer, waits up to `transferWait` for `RunTransfersAsync` to finish, then disconnects, retrying `Remote session is busy; wait for or cancel its transfer` every 200 ms until the same deadline. It returns true once disconnected, or when no session was open.
  - `RunTransfersAsync` completes a `TaskCompletionSource` in its `finally`, which `CloseSessionAsync` awaits.
  - `RemoteManagerWindow.OnClosed` calls `CloseSessionAsync(TimeSpan.FromSeconds(10))` fire-and-forget, logs a false result at Info, and never throws.
- `ReplaceLocalEntries` comment: `// Materialize the order first: Clear raises a collection change before the new entries are added. Sorted by name only, as 1.0.4 did.`
- Produces (PowerShell, `scripts/lib/credentials.ps1`):
  - `Get-GenericCredentialTargets -Prefix <string>` returns string[], through P/Invoke `CredEnumerateW`.
  - `Remove-GenericCredential -Target <string>` uses `CredDeleteW`.
- MSBuild:
  - `CopyBundledPlugins` first `RemoveDir`s `$(OutDir)plugins\<name>` for each folder name under `$(BundledPluginsStagingDir)`. It never removes `$(OutDir)plugins` itself or any other folder, because user-installed plugins and the app-owned files live there now.
  - `CopyRemotePluginFixture` removes `$(RemoteFixtureDest)plugins\com.sumafile.remote` before copying, when the source exists.

- [ ] **Step 1: Write the failing tests**

(b) `RemoteManagerViewModelTests` with `FakeRemoteBackend`:
- `CloseSessionAsync_DuringATransfer_WaitsForItToEndThenDisconnects`. The fake transfer finishes 300 ms after its token is cancelled, and `DisconnectAsync` fails busy while the transfer runs. `CloseSessionAsync(5 s)` returns true, and the disconnect is called after the transfer ended.
- `CloseSessionAsync_TransferThatNeverEnds_GivesUpAfterTheWait`: false after about the wait, and at most one disconnect attempt per 200 ms.
- `CloseSessionAsync_NoSession_ReturnsTrueAtOnce`.

(b) `RemotePluginBackendTests`:
- `CloseSession_DuringAThrottledDownload_DisconnectsOnceTheTransferStops`. Use the real backend and test server, `bandwidthMib` 1 and the 64 MiB fixture. Start the download, then call `CloseSessionAsync(15 s)`, which returns true. Then `fs.list` on that session fails with a message starting `remote session not found: `.

(c) `secrets.rs` tests. These are pinning tests: `encode_secret` and `decode_secret` are private functions of the same module, which `mod tests { use super::*; }` (`secrets.rs:199-200`) already reaches, and the bytes match 1.0.4's `crates/simplefile-service/src/remote/secrets.rs` on `main`. So they compile and pass on the first run, and they fail only if a later change breaks the format.
- `blob_matches_the_1_0_4_golden_bytes`: `encode_secret(&RemoteSecret::Password("pässwörd".into()))` equals `b"password\0p\xc3\xa4ssw\xc3\xb6rd"`, and the passphrase variant equals `b"private-key-passphrase\0k\xc3\xa9y"`.
- `golden_1_0_4_blobs_decode`: the same bytes decode back.
- `#[cfg(windows)] windows_store_reads_a_credential_written_as_1_0_4_wrote_it`. A test helper calls `CredWriteW` directly with the golden password bytes, `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE` and target `SumaFile.Remote.test-<16 random hex>`. `WindowsRemoteSecretStore.read_secret` returns the password, and a drop guard deletes the target.

(e) The smoke, made to fail first:
- Step 11 asserts `baseline.All(t => current.Contains(t, StringComparer.OrdinalIgnoreCase))`, naming any vanished target.
- Every listed cross-process await gets `.WaitAsync(SmokeRemoteWait)`; transfers get `SmokeRemoteTransferWait`.
- The cleanup keeps both a step failure and a cleanup failure: `throw new AggregateException(stepFailure, cleanupFailure)`.
- The App writes the saved smoke profile's target to `<testRoot>\remote-credential.txt` right after the Save.

(f):
- `WinUiSourceShapeTests.PluginCopyTargets_ReplaceOnlyTheirOwnFolders`, over every plugin copy target:
  - `CopyBundledPlugins` (App) contains `RemoveDir` over the staging folder names under `$(OutDir)plugins\`, and no `RemoveDir` of `$(OutDir)plugins` itself;
  - `CopyRemotePluginFixture` (Tests) contains `RemoveDir` of `$(RemoteFixtureDest)plugins\com.sumafile.remote` before its `Copy`;
  - Task 14 adds `CopyGitPluginFixture` to the same test.
- `scripts/test-plugin-copy-targets.ps1` is the scripted check that replaces the manual one:
  - It needs the App's restore output: when `src-winui/SimpleFile.App/obj/project.assets.json` is missing it fails with `Run npm run build:winui first.`
  - In a temp folder it makes a staging root with `bundled.json` and `com.sumafile.remote\{plugin.json, a.txt}`, and an output folder with `plugins\com.sumafile.remote\stale.txt`, `plugins\com.example.user\plugin.json` and `plugins-data\x\keep.txt`.
  - It runs `dotnet msbuild src-winui/SimpleFile.App/SimpleFile.App.csproj -t:CopyBundledPlugins -p:Configuration=Debug -p:OutDir=<out>\ -p:BundledPluginsStagingDir=<staging>\ -nologo`, with the same platform properties `npm run build:winui` uses.
  - It requires `stale.txt` gone, `a.txt` copied, and `com.example.user` and `plugins-data\x\keep.txt` untouched. It prints `Plugin copy targets keep user plugins and app-owned files.`
- `test-plugin-staging.ps1` Case 7 expects `SumaFile.Remote.deps.json` among the remote freshness files.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RemoteManagerViewModelTests|FullyQualifiedName~WinUiSourceShapeTests"`, `cargo test --locked -p sumafile-remote-backend secrets`, the staging test script and `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-copy-targets.ps1`.
Expected: compile failure on `CloseSessionAsync`; the golden secret tests pass at once, as pinning tests do; Case 7 fails on the deps file; the copy-target script fails on `stale.txt`.

- [ ] **Step 3: Implement (b), (c), (e) and (f)**
- **(e), script side:** `smoke-winui-ui.ps1` records `Get-GenericCredentialTargets -Prefix 'SumaFile.Remote.'` before the App starts. In its `finally`, after the App is stopped or killed:
  - when `<testRoot>\remote-credential.txt` names a target that exists and was not in the baseline, it calls `Remove-GenericCredential` and reports it;
  - any other new target fails the script, and is not deleted;
  - a cleanup error is reported even when the App's report already failed.
- **(f), `sha2`:** remove the line, then run `cargo build -p simplefile-service` once to refresh `Cargo.lock`.

- [ ] **Step 4: Run them to verify they pass**

Run:
- the filters from Step 2, plus `--filter "FullyQualifiedName~RemotePluginBackendTests"` (needs `scripts/build-fixture-plugin.ps1` and `scripts/build-plugins.ps1`);
- `cargo test --locked -p sumafile-remote-backend`, `cargo test --locked -p simplefile-service` and `cargo clippy --locked --all-targets --all-features -- -D warnings`;
- `npm run build:winui`, then `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-copy-targets.ps1`;
- `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -PluginsDir target\fixture-plugins\com.sumafile.hello`.

Expected: everything passes, `test result: ok`, `Plugin copy targets keep user plugins and app-owned files.`, and the smoke reports `remote=1` with no credential left behind.

- [ ] **Step 5: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 7: SDK contracts, manifest and adapter for panels, row decorations, toolbar `when` and legacy settings

**Files:**
- Create `src-winui/SimpleFile.PluginSdk/{IPanelProvider.cs, IRowDecorationProvider.cs, IPluginPanels.cs, IPluginDecorations.cs}`.
- Modify `src-winui/SimpleFile.PluginSdk/{PluginUiContracts.cs, IPluginHost.cs, IPluginWorkspace.cs, IPluginDialogs.cs, IPlugin.cs}`.
- Modify `src-winui/SimpleFile.Tests/Fixtures/PluginSdkSurface.v1.txt`: regenerated.
- Modify `src-winui/SimpleFile.PluginHost/Manifest/{PluginManifest.cs, PluginManifestReader.cs}`: `ContributionKinds` 20-23, `ReadContributions` 140-241, `ReadSettings`.
- Modify `src-winui/SimpleFile.PluginHost/Contributions/PluginContributionAdapter.cs`.
- Modify `src-winui/SimpleFile.PluginHost/{PluginManager.cs (options 11-55, RuntimeFor 813-833), PluginRuntime.cs, PluginHostServices.cs}`: plumbing only.
- Create `src-winui/SimpleFile.Core/PluginPanel.cs` (`PluginPanel`, `PluginDecorationProvider`). Modify `ToolbarAction` in `src-winui/SimpleFile.Core/CommandSurfaceLayout.cs`.
- Modify `src-winui/SimpleFile.App/{PluginWorkspaceView.cs, PluginDialogs.cs, MainWindow.Plugins.cs}`: the new members.
- Test: `PluginSdkSurfaceTests.cs`, `PluginManifestReaderTests.cs` (rewrite `Read_PanelsAndRowDecorations_AreAcceptedForLaterPhases` 389-397, and change `Read_IgnoresUnknownTopLevelProperties` 134-138 to use `"futureField"`), `PluginContributionAdapterTests.cs`, and every test double implementing `IPluginHost`, `IPluginWorkspace` or `IPluginDialogs`. Today those are `Remote/FakePluginHost.cs` and `PluginLoadingTests.FakePluginHost` (`PluginLoadingTests.cs:172`); the `PluginManagerTests`, `PluginTrustTests` and `RemoteBackendHarness` options pass `null!` for dialogs and the workspace and need no change. Find them with a search for `: IPluginHost`, `IPluginWorkspace` and `IPluginDialogs` before Step 3, so none is missed.
- Docs:
  - spec §4.4: the `panels` shape gains `command?`, `toolbar` gains `when?`, the "accepted and ignored until phase 4" sentence is replaced by the reader rules, and `activateOn` stays a `backend` field;
  - §5.1: the new contracts, the thread contract, and that the stable theme keys gain `SfAccentBrush` and `SfDecoration{Added,Modified,Untracked,Deleted}Brush`;
  - §8.1: `migrateFrom`.

**Interfaces:**
- Produces (SDK, namespace `SimpleFile.PluginSdk`):
  ```csharp
  public interface IPanelProvider { object CreatePanel(PluginPanelRequest request); }   // UI thread; returns a FrameworkElement
  public sealed record PluginPanelRequest(string PanelId, string Provider, bool Detached);
  public interface IRowDecorationProvider                                               // thread pool; must not touch WinUI
  { Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct); }
  public sealed record PluginDecorationRequest(string Provider, string FolderPath, IReadOnlyList<string> Paths);
  public sealed record RowDecoration(string Text, string? BrushKey = null, string? Tooltip = null);
  public sealed record PluginFolderDecorations(IReadOnlyDictionary<string, RowDecoration> Rows, IReadOnlyList<string> FolderFlags)
  { public static PluginFolderDecorations Empty { get; } }
  public interface IPluginPanels { Task ShowAsync(string panelId, CancellationToken ct); void Hide(string panelId); bool IsVisible(string panelId); }
  // Any thread. ShowAsync and Hide are queued to the UI thread and never run inline, so a plugin calling them from a
  // command, a provider or a decoration request never re-enters a host pass; ShowAsync completes once the panel is shown.
  // IsVisible returns the state the UI thread last published.
  public interface IPluginDecorations { void Invalidate(); }   // re-asks this plugin's provider for the visible folders
  // IPluginHost gains: IPluginPanels Panels { get; }  IPluginDecorations Decorations { get; }
  // IPluginWorkspace gains: Task RefreshAsync(CancellationToken ct);   // re-lists the active folder, always queued
  // IPluginDialogs gains: void Notify(string message, bool isError);   // non-modal message-bar text, any thread
  ```
  The `IPlugin` remarks add: `CreatePanel` runs on the UI thread and may be asked again, once per presentation (`Detached` false, then true). `GetDecorationsAsync` runs on a thread-pool thread.
- Produces (PluginHost manifest):
  ```csharp
  public sealed record PluginPanelSpec(string Id, string Title, string Provider, string Placement, bool Detachable, string? Command);
  public sealed record PluginRowDecorationSpec(string Provider);
  // PluginContributions: IReadOnlyList<PluginPanelSpec> Panels { get; init; } = [];  PluginRowDecorationSpec? RowDecorations { get; init; }
  // PluginToolbarSpec: public string When { get; init; } = "";
  // PluginSettingSpec: public string? MigrateFrom { get; init; }
  ```
  Reader rules and exact errors:
  - **`panels`:**
    - `contributions.panels needs a ui assembly.`;
    - the id follows the settings key grammar, with the sidebar's message;
    - a repeated id gives `The panel id '<id>' is declared more than once in contributions.panels.`;
    - `title` and `provider` are required;
    - `placement` is `right` or `bottom`, default `bottom`; otherwise `The <path>.placement value '<v>' is not recognised; use 'right' or 'bottom'.`;
    - `detachable` defaults to false;
    - `command` must name a declared command (the sidebar's message), and a command toggles at most one panel: `The <path>.command value '<c>' already toggles another panel.`
  - **`rowDecorations`:** `{ provider }`, required; `contributions.rowDecorations needs a ui assembly.`
  - **`toolbar[].when`:** validated like the context menu's (`PluginMenuWhen.IsValid`, 512 characters).
  - **`settings[].migrateFrom`:** `^[A-Za-z][A-Za-z0-9._-]{0,127}$`, and it must not start with `plugin.`. Errors: `The <path>.migrateFrom value '<v>' is not a valid settings key.` and `The <path>.migrateFrom value '<v>' must not name a plugin setting.`
- Produces (Core):
  ```csharp
  public sealed record PluginPanel(string PluginId, string PanelId, string Title, string Placement, bool Detachable, string? CommandId, int Epoch);
  public sealed record PluginDecorationProvider(string PluginId, string Provider, int Epoch);
  // ToolbarAction gains: public string When { get; init; } = "";  public string? PluginId { get; init; }
  ```
- Produces (adapter):
  - `public static IReadOnlyList<PluginPanel> Panels(IEnumerable<PluginRecord> enabled)`: plugin by plugin, in manifest order, with the toggle command as a host command id and the record's epoch.
  - `public static IReadOnlyList<PluginDecorationProvider> RowDecorationProviders(IEnumerable<PluginRecord> enabled)`: ordered by plugin id (ordinal).
  - `ToolbarActions` sets `When` and `PluginId`.
- Produces (plumbing):
  - `PluginManagerOptions.PanelsFor : Func<string, IPluginPanels>?` and `InvalidateDecorations : Action<string>?`, both null by default.
  - `PluginRuntime` and `PluginHostServices` expose `Panels`, from `PanelsFor(id)` or a no-panels `IPluginPanels`. Its `ShowAsync` throws `InvalidOperationException("This host has no panels.")`, `IsVisible` returns false and `Hide` does nothing. So a provider that only asks whether a panel is shown, such as the Git decoration provider in Task 14's harness, works on a host without panels.
  - They expose `Decorations`, whose `Invalidate()` calls `InvalidateDecorations?.Invoke(id)`.
  - The App's `PluginWorkspaceView.RefreshAsync` queues `workspace.RefreshAsync()` through `PluginUiDispatch.EnqueueAsync`.
  - `PluginDialogs.Notify` queues `MainWindow.ShowMessage(PluginDisplayName(id), message, isError ? Error : Informational)` through a new constructor callback `Action<string, bool> notify`.

- [ ] **Step 1: Write the failing tests**

`PluginSdkSurfaceTests`:
- `SdkContracts_UiHandOffsStayObjectTyped`: `IPanelProvider.CreatePanel` returns `object`. No exported SDK type's members mention a `Microsoft.UI` type. The SDK assembly references no `Microsoft.WindowsAppSDK` or `Microsoft.WinUI` assembly.

`PluginManifestReaderTests`:
- `Read_Panels_AreValidated`: theory rows, one per error text above.
- `Read_Panel_DefaultsToBottomAndNotDetachable`.
- `Read_RowDecorations_NeedAUiAssembly`.
- `Read_ToolbarWhen_IsValidated`.
- `Read_SettingMigrateFrom_IsValidated`.
- `Read_ValidPanelAndRowDecorations_AreRead`.

`PluginContributionAdapterTests`:
- `Panels_CarryTheEpochAndTheHostToggleCommand`.
- `RowDecorationProviders_ListEnabledPluginsInIdOrder`.
- `ToolbarActions_CarryTheWhenExpressionAndThePluginId`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginSdkSurface|FullyQualifiedName~PluginManifestReaderTests|FullyQualifiedName~PluginContributionAdapterTests"`
Expected: the build fails on the missing SDK and manifest types.

- [ ] **Step 3: Implement the contracts, the reader, the adapter, the plumbing and the App members; update every test double**

- [ ] **Step 4: Regenerate the pinned surface deliberately**

Write the new surface with `PluginSdkSurface.Describe` from a throwaway test. Diff it against the old file: only the new members may be added, and nothing may be removed. Replace `Fixtures/PluginSdkSurface.v1.txt`, then delete the throwaway test.

- [ ] **Step 5: Run them to verify they pass**

Run: the filter from Step 2, then `npm run check:winui` and `npm run build:winui`.
Expected: `Passed!` with `Failed: 0`; `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: Update spec §4.4, §5.1 and §8.1**

- [ ] **Step 7: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 8: Plugin manager: panel creation, decoration calls, setting writes and the legacy settings migration

**Files:**
- Modify `src-winui/SimpleFile.PluginHost/PluginManager.cs`: next to `CreateSidebarSectionAsync` 556-585 and `ActivateAsync` 629-669.
- Test: `src-winui/SimpleFile.Tests/PluginManagerTests.cs`.
- Docs: spec §7, the panel and decoration bullets (manager behaviour); §8.1 (migration); §10 (a decoration provider that throws or times out is logged, not a load failure).

**Interfaces:**
- Consumes (T7): the SDK contracts, `PluginPanelSpec`, `PluginRowDecorationSpec`, `PluginSettingSpec.MigrateFrom`, `PluginManagerOptions.PanelsFor`/`InvalidateDecorations`. Consumes (T1): `PluginRecord.IsBundledId`.
- Produces:
  ```csharp
  // Activates on the UI thread, then calls IPanelProvider.CreatePanel there. Same failure rules as sidebar sections:
  // undeclared panel -> InvalidOperationException, nothing changes; provider throws/null/rejected or the entry type lacks
  // IPanelProvider -> LoadFailed. Messages: "Plugin '<id>' panel '<panelId>' ...".
  public Task<object> CreatePanelAsync(string pluginId, string panelId, bool detached, CancellationToken ct);

  // Activates (UI thread), then runs IRowDecorationProvider.GetDecorationsAsync on the thread pool (Task.Run).
  // Its one caller, the coordinator, calls it from the thread pool (Task 9), so the activation is queued to the UI thread
  // by UiThread.RunAsync and never runs inline inside a host pass (#82).
  // Entry type without the interface -> LoadFailed. Provider throw or null result -> null and one
  // "[warn] Row decorations failed: <message>" log line; the plugin stays Enabled.
  // Cancellation by the caller (an OperationCanceledException while ct.IsCancellationRequested) -> null and no log line:
  // the coordinator cancels every superseded navigation, so this is normal browsing, not a failure.
  public Task<PluginFolderDecorations?> GetRowDecorationsAsync(string pluginId, PluginDecorationRequest request, CancellationToken ct);

  // For every record with IsBundledId and a declared MigrateFrom: reads plugin.<id>.<key> and the legacy key in one
  // GetSettingsAsync; when the plugin key is null or whitespace and the legacy value passes PluginSettingsSchema.ValidateValue,
  // writes it once and logs "[info] Migrated setting <key> from <legacy>". Never writes or deletes the legacy key.
  // Returns the number of settings copied. Its task is kept: ActivateAsync awaits it (bounded 5 s, then logs and continues).
  public Task<int> MigrateLegacySettingsAsync(CancellationToken ct);

  // storageKey must equal PluginHostMethods.SettingsKey(pluginId, <valid key declared in the manifest>), else ArgumentException.
  // Writes through the options' ISettingsBackend, then calls InvalidateDecorations?.Invoke(pluginId).
  public Task WriteSettingAsync(string pluginId, string storageKey, string value, CancellationToken ct);
  ```

- [ ] **Step 1: Write the failing tests**

Panels:
- `CreatePanel_ActivatesOnceAndRunsTheProviderOnTheUiThread`: mirror `CreateSidebarSection_ActivatesOnceAndRunsTheProviderOnTheUiThread`.
- `CreatePanel_PassesDetachedToTheProvider`.
- `CreatePanel_UndeclaredPanel_ThrowsWithoutLoading`.
- `CreatePanel_ProviderThrows_MarksLoadFailedAndWithdrawsContributions`.
- `CreatePanel_EntryTypeWithoutIPanelProvider_MarksLoadFailed`.

Decorations:
- `GetRowDecorations_RunsTheProviderOffTheUiThread`: the recording UI thread sees activation, and the provider's thread is not the UI thread.
- `GetRowDecorations_ProviderThrows_ReturnsNullAndKeepsThePluginEnabled`: one `[warn] Row decorations failed:` line.
- `GetRowDecorations_CancelledByTheCaller_ReturnsNullWithoutALogLine`: the provider honours `ct`, and the test cancels it. The result is null, the plugin stays Enabled and no `[warn]` line is logged.
- `GetRowDecorations_EntryTypeWithoutTheInterface_MarksLoadFailed`.
- `GetRowDecorations_NotEnabled_ThrowsInvalidOperation`.

Migration and setting writes:
- `MigrateLegacySettings_CopiesAFalseLegacyValueOnce`: the second call copies 0.
- `MigrateLegacySettings_NeverOverwritesAValueTheUserSet`.
- `MigrateLegacySettings_IgnoresPluginsNotListedInBundledJson`.
- `MigrateLegacySettings_SkipsAnInvalidLegacyValue`: `"maybe"` for a boolean.
- `Activate_WaitsForTheLegacySettingsMigration`: a gated settings backend holds the migration, and a command's activation does not start until the gate opens.
- `WriteSetting_WritesTheKeyAndInvalidatesThatPluginsDecorations`.
- `WriteSetting_KeyOfAnotherPlugin_IsRefused`.

Host services:
- `Services_PanelsAndDecorations_ReachTheHostWithThePluginId`.
- `Services_WithoutPanelsFor_ReportNoPanelVisibleAndRefuseShow`: `IsVisible` is false, `Hide` does nothing, and `ShowAsync` throws `This host has no panels.`

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManagerTests"`
Expected: CS1061 for the four new methods.

- [ ] **Step 3: Implement the four methods**

Reuse `ActivateAsync` and `CreateElementAsync`. In `ActivateAsync`, await the stored migration task before `EnsureActivatedAsync`.

- [ ] **Step 4: Run them to verify they pass**

Run: the filter from Step 2, then `npm run check:winui`.
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Update spec §7, §8.1 and §10**

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 9: Core: per-folder decorations within 250 ms, the status column, and panel layout rules

**Files:**
- Create `src-winui/SimpleFile.Core/{RowDecorations.cs, RowDecorationCoordinator.cs, DecorationBrushKeys.cs, PluginPanelLayout.cs}`.
- Modify:
  - `src-winui/SimpleFile.Core/ExplorerPane.cs`: `Decorations` and the decoration request numbers beside `ContentVersion` 66-68;
  - `ExplorerWorkspace.cs`: a coordinator property and `InvalidateDecorationsAsync`;
  - `WorkspaceControllers/WorkspaceNavigationController.cs`: the publish at 203-216 and 520-536;
  - `EntryPresentation.cs`: the `"git"` sort at 366-367 and `ContentSecondaryText` 140-165;
  - `ColumnLayout.cs`: 53, 184-197 and 226-229;
  - `UiSettings.cs`, `WorkspaceSettingsStore.cs`: panel sizes.
- Test: create `RowDecorationCoordinatorTests.cs`, `DecorationBrushKeysTests.cs`, `PluginPanelLayoutTests.cs`; modify `ExplorerWorkspaceTests.cs`, `EntryPresentationTests.cs`, `ColumnLayoutTests.cs`, `WorkspaceSettingsStoreTests.cs`, `WinUiSourceShapeTests.cs`.
- Docs: spec §7, the decoration bullet (wait before publish, progressive chunks never wait, provider calls always on the thread pool, the re-decoration after `ready` or a provider-set change, newer requests win, decorations whatever the column layout, skipped locations, precedence, key matching, `git` column id, brush allow-list).

**Interfaces:**
- Consumes (T7): `PluginPanel`.
- Produces (Core):
  ```csharp
  public sealed record RowDecorationEntry(string PluginId, string Text, string? BrushKey, string? Tooltip);
  public sealed record FolderDecorationBatch(string PluginId, IReadOnlyDictionary<string, RowDecorationEntry> Rows,
      IReadOnlyList<string> FolderFlags);                  // flags already "<pluginId>:<flag>"
  public interface IRowDecorationSource
  {
      IReadOnlyList<string> PluginIds { get; }             // precedence order
      Task<FolderDecorationBatch?> DecorateAsync(string pluginId, string folderPath, IReadOnlyList<string> paths, CancellationToken ct);
  }
  public static class RowDecorationKeys                    // the one key rule, shared with PluginRowDecorationSource (Task 11)
  {
      public static string Normalize(string path);           // PathRules separators -> '\', trailing separator trimmed unless a root
      public static StringComparer Comparer { get; }         // OrdinalIgnoreCase over Normalize
  }
  public sealed class PaneDecorations
  {
      public static PaneDecorations Empty { get; }
      public string? FolderPath { get; }
      public IReadOnlySet<string> FolderFlags { get; }
      public RowDecorationEntry? For(string path);        // keys compared through RowDecorationKeys; the first plugin in order wins
      public IReadOnlySet<string> PluginsFor(IEnumerable<string> paths);   // every plugin decorating any of the paths
      public static PaneDecorations Merge(string folderPath, IReadOnlyList<string> pluginOrder, IEnumerable<FolderDecorationBatch> batches);
  }
  public sealed class RowDecorationCoordinator
  {
      public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(250);
      public RowDecorationCoordinator(TimeSpan? budget = null);
      public Action<string>? Log { get; set; }
      public bool SetSource(IRowDecorationSource? source);   // true when the plugin id set changed; then Generation++
      public bool HasProviders { get; }                      // a source with at least one plugin id is set
      public void MarkStartupReady();                        // until then DecorateAsync returns Empty and calls nothing; Generation++
      public bool IsStartupReady { get; }
      public int Generation { get; }                         // bumped by MarkStartupReady and by a SetSource that changed the set
      public DateTimeOffset? FirstRequestAt { get; }         // first call that reached the source (smoke evidence)
      public Task<PaneDecorations> DecorateAsync(string folderPath, bool isNetwork, IReadOnlyList<string> paths,
          Action<PaneDecorations> onLate, CancellationToken ct);   // every source call starts with Task.Run, never inline
  }
  public static class DecorationBrushKeys
  {
      public static IReadOnlySet<string> Allowed { get; }    // SfDecorationAddedBrush, SfDecorationModifiedBrush,
                                                             // SfDecorationUntrackedBrush, SfDecorationDeletedBrush,
                                                             // SfTextPrimaryBrush, SfTextMutedBrush, SfAccentBrush
      public static string Normalize(string? key);           // unknown/blank -> "SfTextMutedBrush"
  }
  public static class PluginPanelLayout
  {
      public const double BottomMinHeight = 200, BottomDefaultHeight = 320, BottomMaxFraction = 0.7;
      public const double RightMinWidth = 260, RightDefaultWidth = 360, RightMaxFraction = 0.6;
      public const int WindowWidth = 1180, WindowHeight = 740;
      public static string Key(PluginPanel panel);           // "<pluginId>:<panelId>:<epoch>"
      public static string BodyLoadKey(PluginPanel panel);   // "panel:" + Key(panel), the PluginBodyLoadQueue key (Task 4)
      public static string StateKey(PluginPanel panel);      // "<pluginId>:<panelId>"
      public static double ClampBottomHeight(double requested, double available);   // [min, max(min, fraction*available)]
      public static double ClampRightWidth(double requested, double available);
      public static PluginPanel? StillWanted(IReadOnlyList<PluginPanel> wanted, string? shownKey);
      public static PluginPanel? ForCommand(IReadOnlyList<PluginPanel> wanted, string hostCommandId);
  }
  // ExplorerPane: public PaneDecorations Decorations { get; set; } = PaneDecorations.Empty;  (set under workspace.Gate, with BumpContentVersion)
  //               internal long NextDecorationRequest();  internal long AppliedDecorationRequest { get; set; }   (under workspace.Gate)
  // ExplorerWorkspace: public RowDecorationCoordinator RowDecorations { get; }
  //                    public Task InvalidateDecorationsAsync();  internal Task InvalidateDecorationsAsync(PaneId pane);
  //                    internal void ApplyLateDecorations(PaneId pane, int navigationToken, long request, string folderPath, PaneDecorations decorations);
  // UiSettings: PluginPanelBottomHeight (320), PluginPanelRightWidth (360); store keys "pluginPanel.bottomHeight", "pluginPanel.rightWidth"
  // ColumnLayout: public const string DecorationColumnId = "git"; header "Status"; EffectiveForPaneWidth(..., bool decorationsAvailable)
  // EntryPresentation.ContentSecondaryText(parentText, decorationText, symlinkText, extensionText, includeParent): no "Git:" prefix
  ```
- `DecorateAsync`:
  - It returns `Empty` at once, calling no source, before startup is ready, without a source, for a network folder, and for `PathRules.IsRecycleBinPath(folderPath)`.
  - Otherwise it starts every source plugin's call with `Task.Run(() => source.DecorateAsync(…))`, so no source code ever runs on the caller's thread, and waits up to the budget in total. This holds for every caller: the navigation hook, `InvalidateDecorationsAsync` from the App's contribution pass, `IPluginDecorations.Invalidate` and `WriteSettingAsync`. So a provider's first activation, with its assembly load and `ActivateAsync`, is queued to the UI thread by the manager and never nests inside `ApplyPluginContributions` or a sync (#82).
  - It returns the merge of what answered. When the rest answer later, it calls `onLate` once with the merge of all.
  - A source that throws or returns null is skipped. The log gets `[warn] Row decorations from <id> failed: <message>`. A source cancelled through `ct` is skipped silently.
  - A folder flag that does not match `^[A-Za-z][A-Za-z0-9_-]{0,63}$` after the `<pluginId>:` prefix is dropped.
- **Navigation hook.** `NavigatePaneAsync` (after `navigation.Listing`, before the publish lock) and `RefreshAsync` (before its lock):
  1. read `var generation = workspace.RowDecorations.Generation;` and take `var request = state.NextDecorationRequest();`;
  2. await `workspace.RowDecorations.DecorateAsync(listing.Path, isNetwork, listing.Entries.Select(e => e.Path).ToList(), late => workspace.ApplyLateDecorations(target, token, request, listing.Path, late), ct)`;
  3. set `state.Decorations` and `state.AppliedDecorationRequest = request` inside the existing publish lock;
  4. after the lock, when `workspace.RowDecorations.Generation != generation`, start `workspace.InvalidateDecorationsAsync(target)` without awaiting it, logging a failure through `RowDecorations.Log`.

  Step 4 covers a listing whose decorations were asked before `ready` (so it got `Empty`) or before the provider set changed, but which is published after. A startup navigation that races the ready callback is decorated as soon as it is published. It runs on every folder sync, whatever the column layout: the same result carries `inRepository` for menus and the toolbar.
- **`ApplyLateDecorations`** applies only while the pane's `NavigationToken` and path are unchanged and `request >= AppliedDecorationRequest`. A newer request applied first makes an older late result a no-op, whatever order the results arrive in. It then sets `AppliedDecorationRequest = request`, bumps `ContentVersion` and calls `NotifyChanged`.
- **`InvalidateDecorationsAsync()`** re-decorates every shown pane whose listing is published (`ListingInProgress` false), and `InvalidateDecorationsAsync(pane)` one pane. Each takes a new request number, applies its immediate result through `ApplyLateDecorations` and its late result the same way, and never re-lists. A pane still listing is skipped: its own publish re-checks `Generation` (step 4).
- **Sorting.** The `"git"` sort reads `pane.Decorations.For(path)?.Text`.

- [ ] **Step 1: Write the failing tests**

`RowDecorationCoordinatorTests`: fake sources built on `TaskCompletionSource`, a 50 ms budget, no real-time assertions tighter than 2 s.
- `DecorateAsync_BeforeStartupReady_CallsNoSource`.
- `DecorateAsync_FastSources_AreMergedBeforeThePublish`.
- `DecorateAsync_SlowSource_PublishesAfterTheBudgetAndAppliesTheLateResult`: the returned task completes while the source is pending; releasing it calls `onLate` once with the merged rows.
- `DecorateAsync_NetworkFolderAndRecycleBin_CallNoSource`.
- `DecorateAsync_FirstPluginInOrderWinsTheRowText_AndEveryPluginCountsForHasDecoration`.
- `DecorateAsync_FailedSource_IsSkippedAndLogged`.
- `DecorateAsync_KeysDifferingInCaseOrSeparator_MatchTheListedRow`: the key `R:/repo/Dir/A.TXT` and the row `R:\repo\dir\a.txt` match, and so does `R:\repo\untracked\` against the row `R:\repo\untracked`.
- `DecorateAsync_InvalidFolderFlag_IsDropped`.
- `SetSource_ReportsWhetherThePluginSetChanged`.
- `MarkStartupReady_AndAChangedSourceSet_BumpTheGeneration`: an unchanged set leaves it as it is.
- `DecorateAsync_NeverRunsTheSourceOnTheCallingThread`. The source blocks synchronously, before it returns its task, until the test releases it. Under a 2 s watchdog, `DecorateAsync(…)` still returns a task, which completes with `Empty` after the budget. The source's recorded managed thread id differs from the caller's.
- `DecorateAsync_CancelledSource_IsSkippedWithoutALogLine`.

`ExplorerWorkspaceTests`, with the existing fake backend plus a fake source:
- `Navigate_PublishesTheListingWithItsDecorations`.
- `LateDecorations_ForAnOlderNavigation_AreDropped`.
- `LateDecorations_FromAnOlderRequest_AreDroppedOnceANewerOneApplied`: two invalidations on one pane whose results are released newest first; the older one's result changes nothing.
- `InvalidateDecorations_RedecoratesTheShownFoldersWithoutRelisting`.
- `InvalidateDecorations_SkipsAPaneStillListing`.
- `InvalidateDecorations_NeverRunsTheSourceOnTheCallingThread`. With a source that blocks until released, `InvalidateDecorationsAsync()` returns a task that is not yet complete, without blocking the caller. The source's thread differs from the caller's.
- `MarkStartupReady_RedecoratesTheShownFolders`:
  1. With a source set and the gate closed, navigate. The listing is published with `Empty`, and the source was never called.
  2. Call `MarkStartupReady()` and then `InvalidateDecorationsAsync()`, as the App's ready callback does.
  3. The rows get their decorations. The backend's listing call count is unchanged: nothing was listed again.
- `PublishedListing_AskedBeforeTheGenerationChanged_IsDecoratedAgain`:
  1. After ready, source A blocks past the budget. Navigate.
  2. While the navigation waits on the budget, `SetSource(B)` changes the set.
  3. The listing is published with `Empty`, because A has not answered, and is then decorated with B's result without re-listing.
  4. When A is released, its late result is dropped, because B's request is newer.

  This is the path a startup navigation takes when it asks before the ready callback and publishes after it: `MarkStartupReady` bumps `Generation` exactly as `SetSource` does.
- `Navigate_DecoratesWhateverTheColumnLayout`: with the `git` column hidden (the default preset), navigating still calls the source and fills `Decorations`.
- `Sort_ByTheStatusColumn_UsesTheDecorationText`.

`WinUiSourceShapeTests`:
- `RowDecorationCoordinator_StartsEverySourceCallOnTheThreadPool`: in `RowDecorationCoordinator.cs`, every `.DecorateAsync(` call on the source sits inside a `Task.Run(` lambda.

Other tests:
- `DecorationBrushKeysTests.Normalize_KnownKeysPass_UnknownKeysFallBackToMuted`.
- `PluginPanelLayoutTests.ClampBottomHeight_StaysBetweenMinAndSeventyPercent`, `ClampRightWidth_StaysBetweenMinAndSixtyPercent`, `StillWanted_DropsThePanelWhenItsPluginOrEpochLeaves`, `ForCommand_FindsThePanelItsCommandToggles`, and `BodyLoadKey_CarriesThePanelSlot`. The last also checks that a panel and a sidebar section with the same plugin, id and epoch get different body-load keys.
- `EntryPresentationTests.ContentSecondaryText_ShowsTheDecorationTextAsIs`.
- `ColumnLayoutTests.StatusColumn_KeepsTheIdGitWithTheHeaderStatus`.
- `WorkspaceSettingsStoreTests.PanelSizes_RoundTripAndClamp`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RowDecorationCoordinatorTests|FullyQualifiedName~ExplorerWorkspaceTests|FullyQualifiedName~DecorationBrushKeysTests|FullyQualifiedName~PluginPanelLayoutTests|FullyQualifiedName~EntryPresentationTests|FullyQualifiedName~ColumnLayoutTests|FullyQualifiedName~WorkspaceSettingsStoreTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: the build fails on the new types.

- [ ] **Step 3: Implement the Core types, the navigation hook, the sort, the column and the settings**

Callers of `ContentSecondaryText` and `EffectiveForPaneWidth` keep compiling: the App passes `row.GitText` and `IsGitIntegrationEnabled` until Task 11.

- [ ] **Step 4: Run them to verify they pass**

Run: the filter from Step 2, then `npm run check:winui`.
Expected: `Passed!` with `Failed: 0`.

- [ ] **Step 5: Update spec §7**

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 10: App: the side-panel slot (the panel host regions, `PluginPanelWindow`, resize grip, toggle) and the Hello panels

**Files:**
- Create `src-winui/SimpleFile.App/{MainWindow.PluginPanels.cs, PluginPanelWindow.cs, RowResizeGrip.cs}`.
- Modify `src-winui/SimpleFile.App/MainWindow.xaml`:
  - add `RowDefinition` 4 (Auto) to `FilePaneShell` with `PluginPanelBottomRegion` in `Grid.Row="3"`; Task 15 moves it to row 2;
  - wrap `PanesGrid` in a `PanesAndPanelGrid` with columns `*`, `0`, `0` holding `PanesGrid`, `PluginPanelRightGrip` and `PluginPanelRightRegion`.
- Modify `MainWindow.Plugins.cs`: `ApplyPluginContributions` 196-207, `DisposePluginsAsync` 373-406, and `PanelsFor` in the options at 85-117.
- Modify `MainWindow.CommandRouting.cs` (the plugin prefix at 18-22), `MainWindow.CommandSurface.cs` (`OnGeneratedToolbarCommandClick`, `ApplyPrimaryToolbarCommandVisibility` 304-313) and `PluginWindowHost.cs` (`TrackHostWindow`).
- Modify the Hello fixture: `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/{plugin.json, HelloPlugin.cs}`, and create `HelloPanels.cs`.
- Modify `MainWindow.UiSmoke.cs`: a new `SmokePluginPanelsAsync`, called after `SmokePluginSettingsPageAsync`.
- Test: `WinUiSourceShapeTests.cs`.
- Docs: spec §7, the panel bullet:
  - the naming: spec §7's `PluginPanelHost` is not a class. It is the `PluginPanelBottomRegion` and `PluginPanelRightRegion` regions in `MainWindow.xaml`, driven by `MainWindow.PluginPanels.cs`;
  - the host chrome, automation ids, sizes, one visible panel, detach and dock, the toggle semantics, the body failure text, no element while hidden, teardown through the window host;
  - `IPluginPanels` callable from any thread and always queued.

  And §13 (Hello exercises panels).

**Interfaces:**
- Consumes (T4): `PluginBodyLoadQueue`. (T7) `PluginContributionAdapter.Panels`, `IPluginPanels`, `PanelsFor`. (T8) `CreatePanelAsync`. (T9) `PluginPanelLayout` (with `BodyLoadKey`), `UiSettings.PluginPanelBottomHeight`/`PluginPanelRightWidth`. (T5) `DialogHost.HideDialogsOn`, through the window host.
- Produces (App, all on the UI thread):
  - `SyncPluginPanels(IReadOnlyList<PluginRecord> enabled)` is called from `ApplyPluginContributions`. It stores the wanted `PluginPanel`s and hides the shown panel when `PluginPanelLayout.StillWanted` drops it. It never builds an element, so it adds nothing to the startup span.
  - `internal Task ShowPluginPanelAsync(string pluginId, string panelId)`:
    - it hides any other shown panel, docked or detached;
    - it docks the panel in its placement's region, or activates its window when it is detached;
    - it builds the host chrome on first use and loads the body through `_pluginBodyLoads.Request(PluginPanelLayout.BodyLoadKey(panel), …)` with `CreatePanelAsync(…, detached: false)`. The load is queued at Low priority, never inline, so a panel's first activation never nests inside a host pass (#82);
    - a failure shows `This panel could not be loaded: <message>` in the body.
  - `internal Task TogglePluginPanelAsync(string hostCommandId)`, `internal void HidePluginPanel()`, `private Task DetachPluginPanelAsync()`, `private Task DockPluginPanelAsync()`, `private void ClearPluginPanels()`. `ClearPluginPanels` is called from `DisposePluginsAsync`.
  - **Command semantics:**
    - a panel's `CommandId` run from the palette, a menu or `RunAppCommandAsync` calls `ShowPluginPanelAsync`;
    - its generated toolbar button calls `TogglePluginPanelAsync` and is drawn with `SetToggleChromeState(button, shown, "Hide <title>", "Show <title>")`;
    - the plugin's `ICommandHandler` never receives a panel command.
  - **Host chrome** is a `Border` with:
    - AutomationId `PluginPanel.<pluginId>.<panelId>`;
    - a header with the title, a detach or dock button (`.Detach`; names `Detach <title>` and `Dock <title>`; only when `Detachable`) and a close button (`.Close`, name `Close <title>`);
    - a `ContentPresenter` (`.Body`).
  - **Grips:**
    - the bottom region's `RowResizeGrip` (NS cursor) has AutomationId `PluginPanel.BottomGrip`, and the right region uses a `PaneResizeGrip` with `PluginPanel.RightGrip`;
    - dragging clamps with `PluginPanelLayout`, writes `UiSettings`, and saves on release; a double-tap resets the default;
    - `internal void ResizePluginPanelBy(double delta)` is the one code path the drag and the smoke use.
  - **`PluginPanelWindow : Window`**, code-built:
    - `SecondaryWindowChrome.Apply(this, title, PluginPanelLayout.WindowWidth, PluginPanelLayout.WindowHeight)`;
    - the same header with `Dock <title>` and `Close <title>`, and AutomationId `PluginPanelWindow.<pluginId>.<panelId>`;
    - `IsClosed`, and a close-for-dock flag.
    - Closing the window, unless closed for docking, hides the panel. It is tracked through `PluginWindowHostFor(pluginId)!.TrackHostWindow(window)`, so teardown and `OnClosed` close it first.
  - `internal void TrackHostWindow(Window window)` in `PluginWindowHost` uses the same `PluginWindowSet` and seal as `Attach`, without applying chrome.
  - **`PluginPanelsService : IPluginPanels`**, per plugin, from `PanelsFor`, callable from any thread:
    - `ShowAsync` queues `ShowPluginPanelAsync` through `PluginUiDispatch.EnqueueAsync`, never inline, and completes once the panel is shown. It throws `InvalidOperationException("Plugin '<id>' has no panel '<panelId>'.")` for an undeclared or not-enabled panel;
    - `Hide` queues `HidePluginPanel` for that panel with `DispatcherQueue.TryEnqueue`; it does nothing when another panel is shown;
    - `IsVisible` reads a volatile snapshot of the shown panel's `StateKey`, which the UI thread updates whenever a panel is shown or hidden.
- **Hello fixture:**
  - `panels`: `{ id: "hello-panel", title: "Hello panel", provider: "hello-panel", placement: "right", detachable: true, command: "toggle-panel" }` and `{ id: "hello-bottom", title: "Hello bottom", provider: "hello-bottom", placement: "bottom", detachable: false }`;
  - commands `toggle-panel` ("Hello: toggle panel") and `show-bottom` ("Hello: show bottom panel"); `show-bottom` calls `host.Panels.ShowAsync("hello-bottom", ct)`;
  - `HelloPlugin : IPanelProvider` returns a `HelloPanels` element: a `TextBlock` (`Hello.PanelBody`), a `HelloGrip : Grid` subclass with `ProtectedCursor` (`Hello.PanelGrip`), and a button (`Hello.PanelDialog`) that shows "Hello panel dialog" on the panel's own `XamlRoot`;
  - it appends `<panelId>:<detached>` to the `panel.txt` marker.

- [ ] **Step 1: Write the failing source-shape tests and the smoke**

`WinUiSourceShapeTests`:
- `SyncPluginPanels_BuildsNoElement`: the body of `SyncPluginPanels` contains no `new `, `CreatePanelAsync` or `PluginPanelWindow`.
- `InitializePlugins_RendersOnceInsideTheBudgetedSpan_ThenSubscribesAndStartsBackends`: still passes unchanged.
- `PanelCommands_ShowFromCommandsAndToggleFromTheToolbar`: `RunPluginCommandAsync` consults `PluginPanelLayout.ForCommand` before `RunCommandAsync`, and the toolbar click path calls `TogglePluginPanelAsync`.
- `PluginPanelWindow_IsTrackedByThePluginsWindowHost`.
- `PanelBodies_LoadThroughTheQueueWithThePanelSlotKey`: `ShowPluginPanelAsync` calls `_pluginBodyLoads.Request(PluginPanelLayout.BodyLoadKey(` and contains no direct `CreatePanelAsync(` outside that lambda.
- `PluginPanelsService_QueuesShowAndHide`: `ShowAsync` uses `EnqueueAsync` and not `RunAsync`, and `Hide` uses `TryEnqueue`.

Smoke `SmokePluginPanelsAsync`, every wait bounded at 10 s:
1. `RunAppCommandAsync("plugin:com.sumafile.hello:toggle-panel")`. The right region shows `PluginPanel.com.sumafile.hello.hello-panel` with `Hello.PanelBody`, and `panel.txt` ends with `hello-panel:False`.
2. `ResizePluginPanelBy(+40)`: `PluginPanelRightWidth` grows by 40, clamped, and the region width follows.
3. `RunAppCommandAsync("plugin:com.sumafile.hello:show-bottom")`, which goes through `IPluginHost.Panels`. The bottom region shows `hello-bottom` and the right region is collapsed: one visible panel.
4. Show `hello-panel` again, then invoke `.Detach`. One `PluginPanelWindow` opens and is tracked by Hello's window host. The right region is empty, `panel.txt` ends with `hello-panel:True`, and `Hello.PanelGrip` is a `Grid` subclass from the plugin's load context.
5. Invoke the window's `.Detach` (Dock). The window closes, and the body is docked again with a new element.
6. Detach again, invoke `Hello.PanelDialog` and wait for the dialog on the window's `XamlRoot`. Then `DisableAsync(hello)`: the window closes, `!DialogHost.IsDialogOpen` (d), and no panel is shown. Then `EnableAsync`.
7. Show `hello-panel` again (it opens docked, not in a window), then invoke `.Close`: no panel is shown and `IsVisible("hello-panel")` is false.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests"`
Expected: the new tests FAIL.

- [ ] **Step 3: Implement the panel host, window, grips, routing, services and the Hello panels**

- [ ] **Step 4: Run them to verify they pass, and run the plugin smoke**

Run: the filter from Step 2; `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`; `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`; `npm run check:winui-startup-budget`.
Expected: `Passed!`; `UI runtime smoke passed` with `plugins=1`; `WinUI startup budget passed`.

- [ ] **Step 5: Update spec §7 and §13**

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 11: App: the row-decoration slot (rows, menus, toolbar `when`, startup gate) and the Hello decorations

**Files:**
- Create `src-winui/SimpleFile.PluginHost/Contributions/PluginRowDecorationSource.cs`.
- Modify:
  - `src-winui/SimpleFile.App/ExplorerRows.cs`: `FileRow` 9-75; `GitText` stays, unrendered, until Task 15;
  - `FileRowView.xaml.cs`: 340-361, 806-813 and 850-870;
  - `MainWindow.Chrome.cs`: `ToFileRow` 822-833, `SameFileRow` 916-934;
  - `MainWindow.Commands.cs`: `BuildContextMenuRequest` 879-911;
  - `MainWindow.CommandSurface.cs`: `ApplyPrimaryToolbarCommandVisibility`;
  - `MainWindow.Transfer.cs`: `EffectiveColumnsForPane` 1562-1569;
  - `MainWindow.Plugins.cs`: the options, `InitializePluginsAsync` after `StartStartupBackends()` 145, `ApplyPluginContributions`, `OnPluginWorkspaceInitialized` 165-177, `DisposePluginsAsync`;
  - `MainWindow.PluginSidebar.cs`: `QueuePluginSectionActivation`;
  - `SettingsWindow.Plugins.cs`: the write callback at 113;
  - `PluginSettingsPanelBuilder.cs`: the error brush at 165, and an AutomationId on each declarative control in `SettingControl` 157-260;
  - `App.xaml`: the four theme dictionaries, 229-295.
- Modify the Hello fixture: `plugin.json`, `HelloPlugin.cs`.
- Modify `scripts/smoke-winui-ui.ps1`: the `hello-decorated` and `hello-slow` folders in `-PluginsDir` mode. Modify `MainWindow.UiSmoke.cs`: the start of `RunUiSmokeIfRequestedAsync` (the startup-folder navigation) and a new `SmokePluginDecorationsAsync`.
- Test: create `PluginRowDecorationSourceTests.cs`; modify `WinUiSourceShapeTests.cs`.
- Docs:
  - spec §5.1 (the stable keys);
  - §5.2 (decoration activation waits for `ready`, and the shown panes are decorated again once it is reached);
  - §7 (rows, menus, toolbar `when`, precedence, the key filter, Settings writes through `WriteSettingAsync`);
  - the review notes: the #82 closure extends to decorations (Task 9's `Task.Run`, this task's queue-only wiring) and panels (Task 10's queued body loads and `IPluginPanels`).

**Interfaces:**
- Consumes (T7): `RowDecorationProviders`, `ToolbarAction.When`/`PluginId`, `InvalidateDecorations`. (T8): `GetRowDecorationsAsync`, `MigrateLegacySettingsAsync`, `WriteSettingAsync`. (T9): `IRowDecorationSource`, `RowDecorationCoordinator` (`MarkStartupReady`, `Generation`), `RowDecorationKeys`, `PaneDecorations`, `DecorationBrushKeys`, `ExplorerWorkspace.InvalidateDecorationsAsync`, `ColumnLayout.DecorationColumnId`.
- Produces (PluginHost):
  - `public sealed class PluginRowDecorationSource(PluginManager manager, IReadOnlyList<PluginDecorationProvider> providers) : IRowDecorationSource`.
  - It maps `RowDecoration` to `RowDecorationEntry`. Flags become `"<pluginId>:<flag>"`. A null result stays null.
  - **Key filter.** It keeps a returned key only when it matches a requested path under `RowDecorationKeys.Comparer`: the same separator normalization, case-insensitivity and trailing-separator trim that `PaneDecorations.For` uses. It stores each kept entry under the requested path's spelling. So git's `R:/repo/Dir/A.TXT` or `R:\repo\untracked\` reaches the row `R:\repo\dir\a.txt` or `R:\repo\untracked`, rather than being dropped before the merge. When two returned keys map to one requested path, the first wins.
- Produces (App):
  - `FileRow.DecorationText`, `DecorationBrushKey` and `DecorationTooltip` (string), and `DecorationPlugins` (`IReadOnlySet<string>`), filled in `ToFileRow` from the pane's `Decorations` and compared in `SameFileRow`.
  - `FileRow.ColumnText("git")` returns `DecorationText`. `FileRowView` renders `DecorationText` in the `git` cell and the content secondary text (through `EntryPresentation.ContentSecondaryText`), with `ThemeResourceLookup.Brush(this, DecorationBrushKeys.Normalize(Row.DecorationBrushKey))` and a tooltip. The lookup takes the owner element, as `ThemeResourceLookup.Brush(FrameworkElement owner, string key)` does today (`ThemeResourceLookup.cs:9`), so the brush follows the row's actual theme.
  - `EffectiveColumnsForPane` passes `decorationsAvailable: _workspace.RowDecorations.HasProviders` instead of `IsGitIntegrationEnabled`.
  - **`BuildContextMenuRequest`** sets `FolderFlags = pane.Decorations.FolderFlags` and `SelectionDecorationPlugins = pane.Decorations.PluginsFor(selectedPaths)`. It never calls a provider.
  - **`ApplyPrimaryToolbarCommandVisibility`** hides a plugin action whose `When` evaluates false for the active pane: `PluginMenuWhen.Evaluate(action.When, action.PluginId!, BuildContextMenuRequest(...))`. A panel toggle stays visible while its panel is shown. It re-runs after every sync and every decoration change.
  - **Wiring:**
    - `ApplyPluginContributions` and `OnPluginWorkspaceInitialized` call `ApplyRowDecorationSource()`, which is a no-op until the workspace exists. It sets `new PluginRowDecorationSource(_plugins, PluginContributionAdapter.RowDecorationProviders(enabled))`. When `SetSource` returns true, it starts `_ = ObserveAsync(workspace.InvalidateDecorationsAsync())` and returns.
    - `ApplyRowDecorationSource` only queues: it awaits nothing and calls no source. The coordinator starts every source call with `Task.Run` (Task 9). So a provider's activation can never run nested inside `ApplyPluginContributions`, before `ApplyPluginToolbarPlacements` (#82).
    - `DisposePluginsAsync` sets null.
    - `PluginManagerOptions.InvalidateDecorations` is `id => _ = ObserveAsync(_workspace?.InvalidateDecorationsAsync() ?? Task.CompletedTask)`. Called from plugin code or `WriteSettingAsync` on the UI thread, it too only starts thread-pool work.
  - **Startup gate (carry-over h):** `QueuePluginSectionActivation`'s Low-priority callback calls `_workspace.RowDecorations.MarkStartupReady()` and right after it `_ = ObserveAsync(_workspace.InvalidateDecorationsAsync())`. So no decoration request, and no plugin activation it would cause, can precede `ready`, and the folder shown when SumaFile starts is still decorated:
    - a pane whose listing was published before the callback is decorated again by that `InvalidateDecorationsAsync()`;
    - a startup navigation still listing then (`QueueDeferredStartupNavigation` lists on `Task.Run`, in parallel with the callback) asked with the gate closed and got `Empty`. It sees `Generation` changed when it publishes, and decorates again (Task 9, navigation hook step 4).
  - `InitializePluginsAsync` starts `_ = ObserveAsync(manager.MigrateLegacySettingsAsync(cts.Token))` right after `StartStartupBackends()`, after the mark.
  - **Settings > Plugins.** The declarative write callback at `SettingsWindow.Plugins.cs:113` becomes `(storageKey, value) => manager.WriteSettingAsync(record.Id, storageKey, value, token)`, replacing `fileOps.SetSettingAsync`. So turning a decorating plugin's setting on or off re-decorates the shown folders at once. `PluginSettingsPanelBuilder` gives each declarative control the AutomationId `PluginSetting.<pluginId>.<key>`, through a public `SettingAutomationId(pluginId, key)`.
  - **`App.xaml`:** add `SfDecorationAddedBrush`, `SfDecorationModifiedBrush`, `SfDecorationUntrackedBrush` and `SfDecorationDeletedBrush` with the `SfGit*Brush` colours to each theme dictionary. `PluginSettingsPanelBuilder` uses `SfDecorationDeletedBrush`.
- **Hello fixture:**
  - `rowDecorations: { provider: "hello-decorations" }`.
  - `HelloPlugin : IRowDecorationProvider` decorates every path in a folder named `hello-decorated` with `new RowDecoration("hello", "SfDecorationAddedBrush", "Hello decoration")` and the flag `helloFolder`.
  - In a folder named `hello-slow` it waits 600 ms, honouring `ct`, then decorates the same way. Elsewhere it returns `PluginFolderDecorations.Empty`.
  - It appends `<folder name>|<Environment.CurrentManagedThreadId>` to `decorations.log`.
  - A context-menu item: `{ command: "greet", label: "Say hello to decorated", when: "folderFlag(helloFolder) && hasDecoration" }`.

- [ ] **Step 1: Write the failing tests and the smoke**

`PluginRowDecorationSourceTests`, over a `PluginManager` with an `InstanceFactory` fake provider:
- `Decorate_MapsRowsAndPrefixesFlags`.
- `Decorate_DropsKeysThatWereNotRequested`.
- `Decorate_KeysDifferingInCaseOrSeparator_AreKept`. With the requested paths `R:\repo\dir\a.txt` and `R:\repo\untracked`, the provider returns `R:/repo/Dir/A.TXT` and `R:\repo\untracked\`. Both rows are kept, under the requested spellings `R:\repo\dir\a.txt` and `R:\repo\untracked`.
- `Decorate_ProviderFailure_ReturnsNull`.

`WinUiSourceShapeTests`:
- `BuildContextMenuRequest_FillsFolderFlagsAndSelectionDecorationPlugins`.
- `FileRowView_RendersDecorationsThroughTheBrushAllowList`: the decoration brush is `ThemeResourceLookup.Brush(this, DecorationBrushKeys.Normalize(`.
- `StartupReady_RedecoratesAfterMarkingReady`. `MarkStartupReady()` is called only inside `QueuePluginSectionActivation`'s callback, and an `InvalidateDecorationsAsync(` call follows it in the same callback.
- `ApplyRowDecorationSource_OnlyQueues`: its body contains `SetSource(` and `ObserveAsync(`, and no `await`, `GetRowDecorationsAsync(` or `.DecorateAsync(`.
- `PluginSettingsCard_WritesThroughWriteSettingAsync`: the write callback passed to `builder.Build(` in `SettingsWindow.Plugins.cs` contains `WriteSettingAsync(record.Id,` and no `SetSettingAsync(`.
- `InitializePlugins_StartsTheLegacyMigrationAfterTheMark`.

Smoke. The script creates `<testRoot>\hello-decorated\{a.txt,b.txt}` and `<testRoot>\hello-slow\c.txt`.

**Startup folder**, the gate's real path. At the very start of `RunUiSmokeIfRequestedAsync`, in the `-PluginsDir` mode, right after the smoke's settings are applied and before its first `await`:
- record `workspace.RowDecorations.IsStartupReady`, which must be false: the Low-priority ready callback cannot have run, because nothing has yielded since `QueuePluginSectionActivation()`;
- start `workspace.NavigatePaneAsync(PaneId.Primary, <testRoot>\hello-decorated)` without awaiting it, and keep the task and the pane's navigation token.

`SmokePluginsAsync` begins by awaiting that task. Then, with no further navigation or refresh, it waits up to 10 s until both rows have `DecorationText == "hello"`, and asserts that the pane's navigation token is unchanged. Whichever side wins the race (the navigation asking before or after the ready callback runs), the folder shown at startup must end up decorated.

`SmokePluginDecorationsAsync`:
1. Navigate to `hello-decorated`. Wait until both rows have `DecorationText == "hello"` and `DecorationBrushKey == "SfDecorationAddedBrush"`.
2. With `a.txt` selected, `BuildContextMenuRequest` gives `FolderFlags` containing `com.sumafile.hello:helloFolder`, and the menu's `ctx-plugin-menu:com.sumafile.hello` group shows "Say hello to decorated".
3. Navigate to `<testRoot>\empty`: no row has a decoration and that item is hidden.
4. The last `decorations.log` thread id differs from the UI thread's managed id.
5. `RowDecorations.FirstRequestAt` is later than the smoke's recorded `ready` time.
6. **`hello-slow`:** the listing's rows appear without decoration in under 600 ms of the navigation start, and `c.txt` gets "hello" within 10 s (the 250 ms budget, end to end).

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginRowDecorationSourceTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: the build fails on `PluginRowDecorationSource`; then the six source-shape tests FAIL.

- [ ] **Step 3: Implement the source, the row fields and rendering, the menu request, toolbar `when`, the wiring, the gate and its re-decoration, the Settings write path, the theme keys and the Hello decorations**

- [ ] **Step 4: Run them to verify they pass, and run both plugin modes**

Run: the filter from Step 2; `npm run check:winui`; `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`; `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`; `npm run check:winui-startup-budget`; `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`.
Expected: `Passed!`; both smokes `UI runtime smoke passed`; the budget passes under 50 ms.

- [ ] **Step 5: Update spec §5.1, §5.2 and §7, and extend the #82 closure in the review notes**

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 12: The Git backend `sumafile-git-backend`

**Files:**
- Create `plugins/git/backend/Cargo.toml`, `src/{lib.rs, main.rs, git.rs, run.rs, status.rs, paths.rs, commands.rs, decorations.rs, models.rs, handler.rs}` and `tests/{common/mod.rs, repo.rs, handler.rs, process.rs}`.
- Create `plugins/git/protocol.json`.
- Modify the root `Cargo.toml` (members) and `Cargo.lock`.
- Modify `scripts/check-ipc-schema.mjs` 490-567: generalize the remote block into a loop over plugin protocols.
- Modify `scripts/check-winui-packaging.mjs`: `requiredFiles` and `pluginCrateManifests` 244-249.
- Modify `ipc/schema/plugin/README.md`: the Git method table.
- Docs: spec §11.3, the backend paragraph (executable resolution, hooks folder, `--no-optional-locks`, per-command job, per-root cache, decorations shape, Transfers rows and cancel, errors); `docs/RUST_BACKEND_AUDIT_2026-11-05.md` (55, 101, 116: the evidence moves to the plugin crate's tests).

**Interfaces:**
- `Cargo.toml`:
  - `name = "sumafile-git-backend"`, `version = "1.0.0"`, `edition = "2021"`, `publish = false`;
  - `[lib] name = "sumafile_git_backend"`; `[[bin]] name = "sumafile-git-backend"`;
  - dependencies `sumafile-plugin`, `serde` (derive), `serde_json`, `log`, `tokio` (`macros`, `rt-multi-thread`, `sync`, `time`) and `getrandom = "0.4"`;
  - `[target.'cfg(windows)'.dependencies] windows-sys = { version = "0.61", features = ["Win32_Foundation", "Win32_Security", "Win32_System_Diagnostics_ToolHelp", "Win32_System_JobObjects", "Win32_System_Threading"] }`, the 0.61 line `Cargo.lock` already has;
  - dev-dependencies `sumafile-plugin` (`test-support`), `simplefile-ipc`, `tempfile`, and `tokio` with `process`;
  - never `simplefile-core` or `simplefile-service`.
- Produces (Rust):
  ```rust
  pub const PLUGIN_ID: &str = "com.sumafile.git";  pub const PLUGIN_VERSION: &str = "1.0.0";
  pub const GIT_NOT_FOUND: &str = "Git was not found. Install Git for Windows or add git.exe to PATH.";
  pub const GIT_CANCELLED: &str = "Git operation cancelled";
  // git.rs
  pub struct Git { /* exe: Option<PathBuf>, hooks_off: PathBuf, env: Vec<(OsString, OsString)>, spawns: AtomicU64 */ }
  impl Git {
      pub fn discover(data_dir: &Path) -> Git;   // exe = find_on_path(PATH); hooks_off = data_dir.join("no-hooks-<16 random hex>"), never created
      pub fn with_executable(exe: Option<PathBuf>, hooks_off: PathBuf) -> Git;
      pub fn with_env(self, env: Vec<(OsString, OsString)>) -> Git;   // tests: GIT_CONFIG_GLOBAL, GIT_CONFIG_NOSYSTEM
      pub fn spawn_count(&self) -> u64;
  }
  pub fn find_on_path(path: Option<&OsStr>) -> Option<PathBuf>;   // first absolute entry holding git.exe; relative entries skipped
  // run.rs
  pub type CancelCheck = Arc<dyn Fn() -> bool + Send + Sync>;
  pub struct RunOptions { pub name: String, pub timeout: Duration, pub cancel: Option<CancelCheck> }
  pub const GIT_LOCAL_TIMEOUT: Duration = Duration::from_secs(60);   pub const GIT_REMOTE_TIMEOUT: Duration = Duration::from_secs(600);
  #[cfg(windows)] pub struct KillOnCloseJob;   // owns the job handle; dropping it closes the job and ends every process in it
  #[cfg(windows)] pub fn spawn_in_job(command: &mut std::process::Command) -> std::io::Result<(std::process::Child, KillOnCloseJob)>;
  // creation_flags(CREATE_NO_WINDOW | CREATE_SUSPENDED) -> spawn -> CreateJobObjectW + KILL_ON_JOB_CLOSE -> AssignProcessToJobObject
  // -> resume the one thread (CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD), the entry whose th32OwnerProcessID is the child,
  //    OpenThread(THREAD_SUSPEND_RESUME), ResumeThread). A failed assignment or resume kills the child and returns the error.
  // status.rs
  pub struct StatusCache;  impl StatusCache { pub fn new(ttl: Duration, capacity: usize) -> Self; pub fn get(&self, root: &Path) -> Option<GitRepositoryStatus>;
                                              pub fn put(&self, root: &Path, status: GitRepositoryStatus); pub fn forget(&self, root: &Path); }
  pub const STATUS_CACHE_TTL: Duration = Duration::from_millis(1500);   pub const STATUS_CACHE_CAPACITY: usize = 16;
  pub fn repo_status(git: &Git, cache: &StatusCache, path: &Path) -> Result<GitRepositoryStatus, String>;
  // decorations.rs
  pub fn find_repository_marker(folder: &Path, ceilings: &[PathBuf]) -> Option<PathBuf>;   // the folder itself always checked
  pub fn ceilings_from(value: Option<&OsStr>) -> Vec<PathBuf>;                             // ';'-separated, absolute only
  pub fn marker_walk_applies(git_dir: Option<&OsStr>, git_work_tree: Option<&OsStr>) -> bool;   // false when either is set
  pub fn folder_decorations(git: &Git, cache: &StatusCache, folder: &Path) -> GitFolderDecorations;
  // folder_decorations reads GIT_CEILING_DIRECTORIES, GIT_DIR and GIT_WORK_TREE from the Git's env overrides first,
  // then from the process environment, so tests never change the process environment.
  // commands.rs: stage, unstage, discard (git_*_paths semantics), diff, commit(.., cancel), fetch/pull/push(.., cancel)
  // handler.rs
  pub struct GitHandler;  impl GitHandler { pub fn new(data_dir: PathBuf) -> Self; pub fn with_git(git: Git) -> Self; }  // impl PluginHandler
  ```
- **Process rules** (ported from `git.rs` 510-667 and `utils.rs:8-13`, plus the changes listed here):
  - `CREATE_NO_WINDOW`, stdin null, `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=Never`, stdout and stderr drained on threads, a 15 ms wait loop.
  - Each git process starts through `spawn_in_job`: it is created suspended, assigned to its own job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, and only then resumed. So no process git starts can begin outside the job, whatever the timing. That includes the real `mingw64\bin\git.exe` that Git for Windows' `cmd\git.exe` launcher starts at once, and git's remote helpers. A timeout or cancel closes the job, which ends git and all of its children. The backend itself already runs inside the host's kill-on-close job, so this job is nested in it, which Windows 10 supports.
  - Read-only queries run `git --no-optional-locks --literal-pathspecs -c core.fsmonitor=false -c core.hooksPath=<hooks_off> -C <path> …`.
  - Path commands keep `--literal-pathspecs -c core.fsmonitor=false`, with hooks on.
  - fetch, pull and push add nothing, as `git_remote_args_do_not_add_auth_config` pins.
  - With `exe == None`, every call fails with `GIT_NOT_FOUND` and spawns nothing.
- **Root and changes.** The status `root` and every change's `absolute_path` use `\`. An untracked directory's trailing separator is trimmed.
- **Status cache.** It is keyed by root, case-insensitively. Mutating commands `forget` the root before they run and again after they finish.
- **Decorations.**
  - A missing folder, or a folder below no `.git` marker under `GIT_CEILING_DIRECTORIES`, gives `{ in_repository: false, root: null, rows: [] }` with no spawn. The marker walk is skipped when `GIT_DIR` or `GIT_WORK_TREE` is set.
  - Otherwise one cached status run gives the rows of the folder's direct children (`path` = `<folder>\<name>`, native, `status` = label).
  - A git failure gives not-in-repository, and logs at debug.
- **Methods** (`protocol.json`, params camelCase, results snake_case like the 1.0.4 shapes):

  | Method | Params | Result |
  | --- | --- | --- |
  | `repo.status` | `{ path }` | `GitRepositoryStatus` |
  | `repo.decorations` | `{ folder }` | `GitFolderDecorations { in_repository, root, rows: [{ path, status }] }` |
  | `paths.stage`, `paths.unstage`, `paths.discard` | `{ path, paths }` | `GitCommandResult` |
  | `paths.diff` | `{ path, filePath }` | `string` |
  | `repo.commit` | `{ path, message, operationId }` | `GitCommandResult` |
  | `remote.fetch`, `remote.pull`, `remote.push` | `{ path, operationId }` | `GitCommandResult` |

  - fetch, pull and push open a Transfers row with `host.progress_begin("Fetching <repo folder name>")` (`Pulling …`, `Pushing …`). If no host connection exists, they run without a row.
  - They end the row with `progress_complete`, or with `progress_fail` and the error text. A `cancel` naming the request's `operationId` or the row's id stops them within 50 ms, and the call fails with `-32000` `Git operation cancelled`.
  - `repo.commit` honours `cancel` for its `operationId` and opens no row.
  - `protocol.json` has no events. Its `errors` section names `GIT_NOT_FOUND`, `GIT_CANCELLED`, `The current path is not inside a Git repository.`, `Commit message is required.` and `Refusing to run a file-level Git action against the repository root.`, each with `plugins/git/backend/src/*.rs` sources.
- **`check-ipc-schema.mjs`:** a table `pluginProtocols = [{ dir: 'plugins/remote', id: 'com.sumafile.remote', label: 'remote', literalSources: [...] }, { dir: 'plugins/git', id: 'com.sumafile.git', label: 'git', literalSources: ['plugins/git/backend/src/handler.rs'] }]`. `events` is optional per plugin. The remote line keeps its exact text, and the new line is `Checked git plugin protocol: 10 methods, 0 events, 5 error texts.`

- [ ] **Step 1: Write the failing tests**

Unit tests, in the modules:
- Port the 11 tests of `git.rs` 824-1000 with their names.
- `status_root_and_paths_use_native_separators`: a header root `C:/repo` gives `C:\repo`.
- `untracked_directory_loses_its_trailing_separator`.
- `status_cache_keeps_two_roots_apart` and `status_cache_entry_expires_after_the_ttl`.
- `find_on_path_skips_relative_and_missing_entries`.
- `marker_walk_honours_git_ceiling_directories`: a ceiling at `<t>` hides `<t>\.git` from `<t>\a\b`, but a marker at `<t>\a` is found.
- `marker_walk_applies_only_without_git_dir_or_work_tree`.
- `read_only_queries_point_hooks_at_a_missing_folder_inside_the_data_dir`: the args contain `--no-optional-locks` and `core.hooksPath=<data>\no-hooks-`, and that folder does not exist.
- `#[cfg(windows)] spawn_in_job_assigns_the_job_before_the_process_runs`:
  - `spawn_in_job` on `cmd /d /c ping -n 30 127.0.0.1`: `IsProcessInJob(child, job)` is true, and dropping the job ends the child within 5 s;
  - a second call on `cmd /d /c exit 7` exits with code 7, which proves the suspended thread was resumed.

`tests/repo.rs`, real git:
- `common::TestRepo` runs git with `-c init.defaultBranch=main -c commit.gpgsign=false -c core.autocrlf=false`, creates repositories with `git init -b main`, sets repo-local `user.name`/`user.email`, and gives both its own git calls and the `Git` under test `GIT_CONFIG_GLOBAL=<temp>\gitconfig` (an empty file) and `GIT_CONFIG_NOSYSTEM=1`; the `Git` gets them through `with_env`.
- `TestRepo::with_remote()` adds a bare `origin.git` (`git init --bare -b main`), commits, and pushes with `git push -u origin main`, so `main` tracks `origin/main`. Without an upstream, `behind` stays 0 and `git pull` fails with "There is no tracking information". `TestRepo::peer()` clones `origin.git` the same way.
- `decorations_mark_changed_children_with_native_paths`: a modified `tracked.txt`, an untracked `new.txt`, an untracked folder `newdir\` (row `<repo>\newdir`, no trailing separator), a staged `staged.txt`, and `café 資料\hello.txt` modified (decorated when the folder is `café 資料`).
- `decorations_in_a_subfolder_list_only_its_direct_children`.
- `decorations_outside_any_repository_spawn_no_git`: `spawn_count()` is unchanged.
- `file_commands_treat_names_literally`: `literal [brackets] & spaces.txt`, `-n.txt` and `*.txt` stage and unstage only themselves (checked with `git diff --cached --name-only`).
- `discard_removes_only_the_selected_untracked_paths`: the selected untracked folder and its files are deleted; an unselected untracked file stays.
- `discard_refuses_the_repository_root`.
- `diff_joins_staged_and_working_sections`, and the untracked message `No Git diff is available for untracked files until they are staged.`
- `commit_requires_a_message_and_commits_staged_changes`.
- `fetch_pull_push_against_a_local_bare_remote`, in this order, so the pull is a fast-forward and needs no `pull.rebase` setting:
  1. The peer commits and pushes `from-peer.txt`.
  2. `fetch` leaves the status `behind == 1` and `ahead == 0`.
  3. `pull` brings `from-peer.txt`.
  4. A local commit follows, then `push` updates the bare `main` to the local `HEAD`.
- `pull_with_diverged_branches_fails_with_gits_message`. The local repository commits and the peer pushes, so the branches diverge. The isolated config sets neither `pull.rebase` nor `pull.ff`. The call returns an error within 60 s, its text contains git's `divergent branches` message (`fatal: Need to specify how to reconcile divergent branches.`, as Git 2.56 on this machine prints it), and the working tree has no `MERGE_HEAD`. 1.0.4 ran `git pull` the same way, so this pins how the failure surfaces rather than changing it.
- `cancelled_fetch_kills_git_and_its_children`:
  - a `TcpListener` on `127.0.0.1:0` accepts and never answers;
  - the remote is `http://127.0.0.1:<port>/repo.git`, and cancel is set after 500 ms;
  - the call fails with `GIT_CANCELLED` within 5 s, and within 5 s the accepted socket reads EOF or a connection reset: git's remote helper died with the job.
- `missing_git_reports_git_not_found_without_spawning`: `Git::with_executable(None, …)`.
- `mutating_command_refreshes_the_cached_status`.

`tests/handler.rs`, with `HostClient::fake`:
- `methods_route_and_reject_bad_params`: `Invalid repo.status params`.
- `fetch_opens_a_transfers_row_and_completes_it`.
- `cancel_by_row_id_fails_the_fetch_with_git_operation_cancelled`.
- `failed_pull_fails_its_row_with_gits_message`: on the diverged setup above, the `Pulling …` row ends with `progress_fail` carrying git's text, and the reply is the same error.

`tests/process.rs`, the real exe over the pipe:
- `backend_process_serves_status_and_decorations`: handshake, `repo.status` on a temp repo, and `repo.decorations` on a non-repository folder. The exe exits when the host disconnects.

- [ ] **Step 2: Run them to verify they fail**

First add the crate to the workspace with its `Cargo.toml`, empty modules and these tests. Then run `cargo test -p sumafile-git-backend`; it updates `Cargo.lock` once.
Expected: the tests fail to compile on the missing items (`Git`, `StatusCache`, `folder_decorations`, `GitHandler`, …).

- [ ] **Step 3: Implement the crate, `protocol.json`, the generalized protocol check, the README table and the packaging entries**

- [ ] **Step 4: Run them to verify they pass**

Run: `cargo test --locked -p sumafile-git-backend`, `cargo clippy --locked --all-targets --all-features -- -D warnings`, `cargo fmt --all -- --check`, `npm run check:ipc-schema` and `npm run check:winui-packaging`.
Expected: `test result: ok` for lib, `repo`, `handler` and `process`; clippy and fmt clean; `Checked remote plugin protocol: 11 methods, 3 events, 5 error texts.` and `Checked git plugin protocol: 10 methods, 0 events, 5 error texts.`; `WinUI packaging surface is wired.`

- [ ] **Step 5: Update spec §11.3 and the audit document**

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 13: The Git plugin UI (model, code-built views) and its manifest

**Files:**
- Create `plugins/git/plugin.json`, `plugins/git/ui/{Directory.Build.props, Directory.Build.targets}`, the `SumaFile.Git.Model` project and the `SumaFile.Git` project (listed in File structure), with their `packages.lock.json` from `dotnet restore --force-evaluate`.
- Modify `src-winui/SimpleFile.sln`, `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj` (reference `SumaFile.Git.Model`) and `src-winui/SimpleFile.Tests/Remote/FakePluginHost.cs` (a `pluginId` constructor parameter, default `com.sumafile.remote`).
- Modify `scripts/check-ipc-schema.mjs`: the git literal sources add `plugins/git/ui/SumaFile.Git.Model/GitProtocol.cs`.
- Test: create `src-winui/SimpleFile.Tests/Git/{FakeGitBackend.cs, FakeGitHost.cs, GitTextTests.cs, GitBackendClientTests.cs, GitDecorationProviderTests.cs, GitWorkbenchViewModelTests.cs, GitWorkbenchControllerTests.cs, GitPluginManifestTests.cs, GitPluginSourceShapeTests.cs}`.
  - `FakeGitHost : IPluginHost` (`PluginId` `com.sumafile.git`) records `Dialogs.Notify`, `Decorations.Invalidate`, `Workspace.RefreshAsync`, `Panels.ShowAsync` and `Panels.Hide`, answers `Panels.IsVisible` from a settable flag, and serves `Settings` from a dictionary.
  - `FakeGitBackend : IGitBackend` answers from per-method delegates and records calls.
- Docs:
  - spec §11.3: UI assembly, contributions and settings;
  - the user-visible changes listed under "Spec refinements" (T13 and T15). Among them are the three decisions about what `EnableGitIntegration` used to gate: commands stay listed and refuse while disabled; turning Git off is still a complete disable, with the workbench hidden; decorations run whatever the column layout. Profiles and saved layouts never applied the flag;
  - spec §12 (`plugins/git/ui` with its two projects).

**Interfaces:**
- Consumes (T7): the SDK. (T12): the protocol.
- **`plugin.json`:**
  - `id` `com.sumafile.git`, `name` `Git`, `version` `1.0.0`, `sdkVersion` `1.0.0`, publisher as remote's;
  - `ui: { assembly: "SumaFile.Git.dll", entryType: "SumaFile.Git.GitPlugin" }`;
  - `backend: { executable: "sumafile-git-backend.exe", args: [], activateOn: "startup", acceptsMultipleClients: false }`;
  - `permissions: ["backend-process", "shell", "network"]`.
  - **Commands, group `Git`:** `git-panel` "Git: show workbench", `git-refresh` "Git: refresh status", `git-fetch` "Git: fetch", `git-pull` "Git: pull", `git-push` "Git: push", `git-commit` "Git: commit staged changes", `git-stage-selected` "Git: stage selected paths", `git-unstage-selected` "Git: unstage selected paths", `git-discard-selected` "Git: discard selected changes" and `git-diff-selected` "Git: show selected diff".
  - **One `contextMenu` group `Git`, items in this order:**
    - `git-panel` "Show Git workbench" and `git-refresh` "Refresh status": `folderFlag(inRepository)`;
    - `git-diff-selected` "Show diff": `selectionCount == 1 && hasDecoration`;
    - `git-stage-selected` "Stage selected", `git-unstage-selected` "Unstage selected" and `git-discard-selected` "Discard selected changes": `selectionCount >= 1 && hasDecoration`;
    - `git-fetch` "Fetch", `git-pull` "Pull", `git-push` "Push" and `git-commit` "Commit": `folderFlag(inRepository)`.
  - **Toolbar:** `[{ command: "git-panel", label: "Git", glyph: "\ue8ef", placement: "primary", when: "folderFlag(inRepository)" }]`.
  - **Panels:** `[{ id: "workbench", title: "Git workbench", provider: "workbench", placement: "bottom", detachable: true, command: "git-panel" }]`.
  - `rowDecorations: { provider: "status" }`.
  - **Settings:** `[{ key: "enabled", type: "boolean", label: "Show Git status, menus and the Git workbench", description: "Off: SumaFile runs no Git commands.", default: true, group: "General", migrateFrom: "enableGitIntegration" }]`.
- **`SumaFile.Git.Model`** (WinUI-free; references only the SDK, compile-only):
  - `GitModels.cs`: `GitRepositoryStatus`, `GitFileStatus`, `GitCommandResult`, `GitFolderDecorations`, `GitDecorationRow`, with the backend's snake_case `JsonPropertyName`s.
  - `GitProtocol.cs`:
    - `GitMethods` holds string literals `"repo.status"`, `"repo.decorations"`, `"paths.stage"`, `"paths.unstage"`, `"paths.discard"`, `"paths.diff"`, `"repo.commit"`, `"remote.fetch"`, `"remote.pull"`, `"remote.push"` and `"cancel"`;
    - `GitErrors` holds `NotFound` and `Cancelled` (the T12 texts).
  - `IGitBackend` (one method per backend method), and `GitBackendClient(IPluginHost host) : IGitBackend`. The client sends commit, fetch, pull and push with `CancellationToken.None` plus an `operationId`, and on the caller's cancellation sends `cancel { operationId }`: the `RemoteBackendClient.TransferAsync` pattern.
  - `GitText`:
    - `StatusSummary(GitRepositoryStatus)`, ported from `MainWindow.Git.cs` 394-433;
    - `ResultMessage(string command, GitCommandResult)`, from 775-786;
    - `DiscardConfirmationText(IReadOnlyList<GitChangeRow>)`, from 824-847;
    - `DecorationBrushKey(string status)`: `added` and `staged` → `SfDecorationAddedBrush`; `modified`, `renamed` and `staged + modified` → `SfDecorationModifiedBrush`; `untracked` → `SfDecorationUntrackedBrush`; `deleted` and `conflicted` → `SfDecorationDeletedBrush`; anything else → `SfTextMutedBrush`;
    - `DisabledMessage = "Git integration is turned off. Turn it on in Settings > Plugins > Git."`;
    - `NotARepositoryMessage = "Current folder is not inside a Git repository."`.
  - `GitChangeRow`, from `ExplorerRows.cs` 79-119.
  - `GitWorkbenchViewModel : INotifyPropertyChanged`, ported from `GitWorkbenchViewModel.cs` without `Visibility`, `IsDetached`, `SetHostMode` or `HostAction*`. It adds `bool ChangesEmpty` and `bool IsBusy`.
  - `IGitPrompts { Task<string?> PromptCommitMessageAsync(CancellationToken ct); Task<bool> ConfirmDiscardAsync(IReadOnlyList<GitChangeRow> rows, CancellationToken ct); }`.
  - **`GitWorkbenchController(IGitBackend backend, IPluginHost host, GitWorkbenchViewModel model, IGitPrompts prompts)`**, with:
    - `Task RefreshAsync(bool silent, CancellationToken ct)`;
    - `Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)`, which handles the nine non-panel commands;
    - `Task ShowDiffAsync(IReadOnlyList<string> paths, CancellationToken ct)`;
    - `void OnWorkspaceChanged()`: when the panel is visible and the active folder changed, refresh after a 250 ms debounce.

    Its rules:
    - it refuses Recycle Bin paths with `Git status is not available for this location.`;
    - it checks `GitSettings.IsEnabledAsync` first. While disabled, every one of the nine commands, and a refresh, notifies `GitText.DisabledMessage` (not as an error) and calls no backend method. The commands themselves stay on the palette and in the omnibar: the host has no per-command `when`, and the user can find out from the message why nothing ran;
    - stale-result guards use the CTS identity and the active path;
    - the selection falls back to the panel selection, then to `context.SelectedPaths` filtered by the last status's changes (`GitPaths.PathsEqual`);
    - after stage, unstage, discard, commit and pull it calls `host.Decorations.Invalidate()`, and after discard and pull also `host.Workspace.RefreshAsync`;
    - results and errors go to `host.Dialogs.Notify`;
    - `Git operation cancelled` becomes `<Command> was cancelled.`;
    - it never throws to its caller.
  - **`GitDecorationProvider(IGitBackend backend, IPluginSettings settings, IPluginPanels panels, IPluginLogger log)`** with `Task<PluginFolderDecorations> GetAsync(PluginDecorationRequest request, CancellationToken ct)`:
    - disabled or Recycle Bin returns `Empty` with no backend call;
    - the first disabled request after an enabled one (the provider remembers the last value it read) also calls `panels.Hide("workbench")` when `panels.IsVisible("workbench")`; `IPluginPanels` queues the hide to the UI thread. Turning Git off in Settings writes through `WriteSettingAsync`, which re-asks this provider at once. So the same write clears the badges, the `inRepository` flag (and with it the menu group and the toolbar toggle) and hides the open workbench, the complete disable of 1.0.4. A workbench the user opens on purpose while Git is off is not hidden again: it shows `GitText.DisabledMessage` until the user closes it;
    - rows become `new RowDecoration(status, GitText.DecorationBrushKey(status), "Git: " + status)`;
    - `in_repository` gives the flag `inRepository`;
    - a backend error returns `Empty` and logs once per distinct message.
  - `GitSettings`: `EnabledKey = "enabled"` and `Task<bool> IsEnabledAsync(IPluginSettings, CancellationToken)`; missing or unparsable reads as true.
  - `GitPaths`: `PathsEqual`, `PathContains` and `IsRecycleBinPath`, copied semantics of Core `PathRules`.
  - `GitCommandIds`: ten `const string`s `plugin:com.sumafile.git:<id>`.
- **`SumaFile.Git`** (19041):
  - `public sealed class GitPlugin : IPlugin, ICommandHandler, IPanelProvider, IRowDecorationProvider`:
    - `CreatePanel` returns `new GitWorkbenchPanel(controller, model, request.Detached).Root` and starts a silent refresh;
    - `GetDecorationsAsync` delegates to `GitDecorationProvider`;
    - `git-diff-selected` calls `host.Panels.ShowAsync("workbench", ct)` and then `ShowDiffAsync`;
    - `ActivateAsync` subscribes `Workspace.Changed`, and `DeactivateAsync` unsubscribes and cancels within 5 s.
  - `GitWorkbenchPanel`: `XamlReader.Load` fragments with `{ThemeResource}` keys only, and values set from `PropertyChanged`.
    - The changes `ListView` uses `ListRows.Attach` with `SelectionMode=Extended`.
    - The diff uses a read-only Consolas `TextBox`. A `GitDiffSplitter : Grid` with the `SizeWestEast` `ProtectedCursor` clamps the diff width to 260-960, and a double-tap resets it to 420.
    - It keeps the automation names "Refresh Git status", "Fetch", "Discard selected changes", "Git changes" and "Resize Git diff preview".
  - `GitPrompts : IGitPrompts`:
    - code-built `ContentDialog`s shown through `host.Dialogs.ShowDialogAsync` with `XamlRoot` = the shown panel element's `XamlRoot` (null for the main window when no panel is shown);
    - commit has a `TextBox` (`Git.CommitMessage`) and returns null on Cancel;
    - discard has `PrimaryButtonText = "Discard"`, `CloseButtonText = "Cancel"`, `DefaultButton = ContentDialogButton.Close`, the `SystemFillColorCriticalBrush` primary style, and `GitText.DiscardConfirmationText`.
  - `GitAutomationIds`: `Git.Workbench`, `Git.Branch`, `Git.Summary`, `Git.Refresh`, `Git.Fetch`, `Git.Pull`, `Git.Push`, `Git.Commit`, `Git.Stage`, `Git.Unstage`, `Git.Discard`, `Git.Diff`, `Git.Changes`, `Git.DiffTitle`, `Git.DiffText`, `Git.DiffSplitter`, `Git.CommitMessage`.

- [ ] **Step 1: Write the failing tests**

`GitTextTests` (port today's expectations where they exist):
- `StatusSummary_ListsEveryNonZeroCount`.
- `StatusSummary_CleanTreeAndNotARepository`.
- `ResultMessage_FallsBackToTheSummary`.
- `DiscardConfirmationText_ListsPathsAndWarnsAboutUntrackedFiles`, moved from `UxRecommendationsSettingsGitRemoteTests.GitDiscard_ListsPathsAndWarnsAboutUntrackedFiles`.
- `DecorationBrushKey_MapsEveryBackendLabel`: all eight labels, plus an unknown one.

`GitBackendClientTests`, with `FakePluginHost("com.sumafile.git")`:
- `EachMethod_SendsItsProtocolNameAndCamelCaseParams`.
- `Fetch_CancelledByTheCaller_SendsCancelWithItsOperationId`.

`GitDecorationProviderTests`, with `FakeGitBackend`, `FakeGitHost.Settings` and `FakeGitHost.Panels`:
- `GetAsync_MapsRowsAndTheInRepositoryFlag`.
- `GetAsync_EnabledFalse_ReturnsNothingAndCallsNoBackend`.
- `GetAsync_TurnedOff_HidesAShownWorkbenchOnce`: with `IsVisible("workbench")` true, an enabled request and then a disabled one record exactly one `Hide("workbench")`, and a further disabled request records no other. With `IsVisible` false, or when the first request already reads disabled, none is recorded.
- `GetAsync_RecycleBin_ReturnsNothing`.
- `GetAsync_GitNotFound_ReturnsNothingAndLogsOnce`.

`GitWorkbenchViewModelTests`: port today's view-model behaviour (`SetStatus`, `UpdateSelection`, `Can*` rules).

`GitWorkbenchControllerTests`, with `FakeGitBackend`, `FakeGitHost` and fake prompts:
- `Refresh_FailedStatus_ClearsBusyAndShowsTheError`, for audit P4.
- `Refresh_StaleResultForAnotherFolder_IsIgnored`.
- `Refresh_GitNotFound_ShowsTheMessageAndKeepsThePanelUsable`.
- `Fetch_CancelledFromItsRow_ReportsWasCancelledAndClearsBusy`, for audit P3.
- `StageSelected_FallsBackToTheFileListSelectionFilteredByStatus`.
- `DiscardSelected_Declined_RunsNothing`.
- `Commit_EmptyMessage_RunsNothing`.
- `Commands_WhenDisabled_NotifyTheDisabledMessageAndRunNothing`: each of the nine commands, with `enabled` false, notifies exactly `GitText.DisabledMessage` with `isError` false and makes no backend call.
- `AfterStageCommitAndDiscard_InvalidatesDecorations`.

`GitPluginManifestTests.GitManifest_InTheRepo_IsValidAndDeclaresExactlyThePlan`: the reader is valid, and every id, label, `when`, placement, panel field, provider, setting and backend field equals the values above.

`GitPluginSourceShapeTests`:
- `GitPlugin_HasNoCompiledXamlOrBindings`.
- `GitPlugin_ProjectsReferenceOnlyTheSdkAndEachOther`, the twin of the remote test.
- `GitAutomationIds_AreExactly`.
- `GitPrompts_DiscardDefaultsToCancel`.
- `GitPrompts_UseTheShownPanelsXamlRoot`.
- `GitPlugin_ImplementsTheFourContracts`.
- `GitPlugin_DecorationsTouchNoWinUi`: `GetDecorationsAsync` only delegates to `GitDecorationProvider`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~SimpleFile.Tests.Git."` (the tests live in namespace `SimpleFile.Tests.Git`)
Expected: the build fails (missing `SumaFile.Git.Model`).

- [ ] **Step 3: Create both projects and the manifest, implement the model and the views, and restore the lock files offline**

Run `dotnet restore --force-evaluate` on each of the two projects and on `SimpleFile.Tests`.

- [ ] **Step 4: Run them to verify they pass**

Run: the filter from Step 2, then `npm run build:winui`, `npm run check:winui` and `npm run check:ipc-schema`.
Expected:
- `Passed!` with `Failed: 0`;
- `0 Warning(s)`, `0 Error(s)`;
- `Checked git plugin protocol: 10 methods, 0 events, 5 error texts.`, now that both literal sources are checked.

- [ ] **Step 5: Update spec §11.3 and §12**

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 14: Bundle and pre-approve `com.sumafile.git`; drive the real backend through the real host

**Files:**
- Modify `scripts/lib/plugin-staging.ps1`:
  - `BundledPluginIds` 12 becomes `@('com.sumafile.sevenzip', 'com.sumafile.remote', 'com.sumafile.git')`;
  - add `GitBackendExe`, `GitUiProject` and `GitUiFiles`;
  - add the git entries to `BundledPluginBackends` and `BundledPluginUiProjects`;
  - add `Get-GitPluginSources`, and the git branches of `Get-BundledPluginSources` and `Get-BundledPluginFreshnessFiles`.
- Modify `scripts/build-plugins.ps1`: cargo gets `-p sumafile-git-backend`; the single remote `dotnet build` becomes a loop over `$script:BundledPluginUiProjects`.
- Modify `scripts/test-plugin-staging.ps1`: Case 7 ids and a git sources/freshness case.
- Modify `scripts/build-winui-release.ps1`: the process list at 232, `cargo build -p sumafile-git-backend --locked --release`, and the Release UI build loop at 378-384.
- Modify `packaging/winui/sumafile-winui.nsi`: `.onInit` and Uninstall get `taskkill /F /IM sumafile-git-backend.exe` (never `git.exe`); Install gets `RMDir /r "$INSTDIR\plugins\com.sumafile.git"`.
- Modify `scripts/check-winui-packaging.mjs`: `requiredFiles` (`plugins/git/plugin.json`, `plugins/git/protocol.json`, `plugins/git/backend/Cargo.toml`, both csproj), `nsisSnippets`, `buildSnippets` (`sumafile-git-backend`, `SumaFile.Git.csproj`), `stagingSnippets` (`com.sumafile.git`, `sumafile-git-backend.exe`, `SumaFile.Git.dll`, `SumaFile.Git.Model.dll`, `SumaFile.Git.deps.json`).
- Modify `scripts/smoke-winui-ui.ps1`: cargo gets `-p sumafile-git-backend`, and `GIT_CEILING_DIRECTORIES=<testRoot>` is set in every mode.
- Modify `scripts/smoke-winui-startup.ps1`: the git discovered line, approvals check and running backend; stop it with `WaitForExit(5000)`. The launched app gets `GIT_CONFIG_GLOBAL=<disposable>\gitconfig` (an empty file) and `GIT_CONFIG_NOSYSTEM=1`, because it starts the Git backend.
- Modify `scripts/smoke-winui-msi.ps1` (26-36): the same two git variables for the app it launches.
- Modify `scripts/smoke-winui-file-ops.ps1`: a new `Invoke-GitPluginSmoke` after `Invoke-RemotePluginSmoke`.
- Modify `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj`: `CopyGitPluginFixture` copies `target\plugins\com.sumafile.git\**` and `bundled.json` into `Fixtures\git\plugins\`, after a `RemoveDir` of `Fixtures\git\plugins\com.sumafile.git`.
- Test: create `src-winui/SimpleFile.Tests/Git/{GitBackendHarness.cs, GitPluginBackendTests.cs}`; extend `WinUiSourceShapeTests.PluginCopyTargets_ReplaceOnlyTheirOwnFolders` (Task 6) to `CopyGitPluginFixture`.
- Docs:
  - spec §11.4: a "Phase 4 adds the third, `com.sumafile.git`" paragraph with the payload layout of three plugins, NSIS and smokes;
  - spec §13: the Git smokes. Correct the sentence "`check-winui-packaging.mjs` checks the three plugin folders in the payload": that script checks source files only, that the staging library, the release build, the installers and the workflows name the three plugins. The payload itself is checked by `Assert-Payload` in `build-winui-release.ps1` and by `Assert-BundledPlugins` in the installer smokes.

**Interfaces:**
- Consumes (T12, T13): the backend exe, `SumaFile.Git.dll`, `SumaFile.Git.Model.dll` and `SumaFile.Git.deps.json` from `plugins\git\ui\SumaFile.Git\bin\<Configuration>\net10.0-windows10.0.19041.0\`. Consumes (T8, T9, T11): `GetRowDecorationsAsync`, `RowDecorationCoordinator`, `PluginRowDecorationSource`.
- Produces:
  - **Staged folder:** `plugins\com.sumafile.git\{plugin.json, sumafile-git-backend.exe, SumaFile.Git.dll, SumaFile.Git.Model.dll, SumaFile.Git.deps.json}`, listed in `bundled.json` third. The freshness files are the backend, both DLLs, the deps file and `plugin.json`.
  - **`GitBackendHarness`** follows `RemoteBackendHarness`:
    - the real `PluginManager` and `ProcessLauncher` on the staged `Fixtures\git\plugins` root, trusted through its `bundled.json`;
    - the entry type is replaced by a `CapturingGitPlugin : IPlugin, IRowDecorationProvider` that delegates to `GitDecorationProvider` over the captured host, so `SumaFile.Git.dll` (WinUI) never loads;
    - a `TestRepo` made with the git CLI; a recording progress sink; fake settings;
    - the harness runs in the `Plugin environment` collection. It sets `GIT_CONFIG_GLOBAL=<temp>\gitconfig` and `GIT_CONFIG_NOSYSTEM=1` for the test process, which the launched backend inherits, and restores both on dispose;
    - `NotStagedMessage = "The Git plugin is not staged. Run scripts/build-plugins.ps1."`.
  - **`Invoke-GitPluginSmoke`** starts `plugins\com.sumafile.git\sumafile-git-backend.exe` from the payload with a temp `--data-dir`:
    - it sets `GIT_CONFIG_GLOBAL=<smokeRoot>\gitconfig` (an empty file) and `GIT_CONFIG_NOSYSTEM=1` in the backend's `ProcessStartInfo` and for its own git calls, so a developer's `core.hooksPath`, `commit.gpgsign` or `init.defaultBranch` cannot run, prompt or change the result;
    - it creates the temp repo with `git init -b main`, sets a repo-local identity, and commits one file;
    - it calls `repo.status` (`is_repo` true, branch `main`) and `repo.decorations` on a non-repository folder (`in_repository` false), stages one file (checked with `git diff --cached`), and prints `Payload Git plugin smoke passed.`

- [ ] **Step 1: Write the failing tests**

`test-plugin-staging.ps1`:
- Case 7 expects three ids in order.
- Case 11: `Get-BundledPluginSources -Id com.sumafile.git` returns the five staged names, and `Get-BundledPluginFreshnessFiles` the five freshness files.

`WinUiSourceShapeTests.PluginCopyTargets_ReplaceOnlyTheirOwnFolders` also requires `CopyGitPluginFixture` to `RemoveDir` `Fixtures\git\plugins\com.sumafile.git` before its `Copy`.

`GitPluginBackendTests`, through the real host. Each test skips with `NotStagedMessage` when the folder is missing, and every wait is bounded:
- `GitPlugin_IsTrustedThroughBundledJsonAndStartsAtStartup`: Enabled without consent, nothing in the approvals file, and after `StartStartupBackends()` the backend runs under the staged folder.
- `Decorations_ThroughTheRealHost_MarkTheChangedChildren`: `PluginManager.GetRowDecorationsAsync` and `PluginRowDecorationSource` with the Core coordinator, on the temp repo's root and on `café 資料`. Rows are keyed by native paths and carry the `inRepository` flag.
- `Decorations_OutsideARepository_AreEmpty`.
- `StageCommitAndDiff_ThroughTheRealHost_ChangeTheRepository`, checked with the git CLI.
- `FetchCancelledFromItsRow_StopsGitAndFailsTheRow`: the hanging TCP remote. Cancel the recorded row, and the row fails with `Git operation cancelled` within 10 s.
- `DisablingThePlugin_StopsTheBackendProcess`.

- [ ] **Step 2: Run them to verify they fail**

Run: the staging test script, then `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~GitPluginBackendTests"`
Expected: Case 7 fails on the id list; the C# filter fails to build (no harness).

- [ ] **Step 3: Implement the staging, scripts, NSIS lines, packaging check, fixture copy, harness and file-ops smoke**

- [ ] **Step 4: Run them to verify they pass, and run the payload path**

Run, in order:
1. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1`, which prints `Bundled plugins verified: com.sumafile.sevenzip, com.sumafile.remote, com.sumafile.git`
2. the staging test script
3. the C# filter from Step 2
4. `npm run check:winui`
5. `npm run check:winui-packaging`
6. `npm run release:build`, which includes `Payload Git plugin smoke passed.` and the startup smoke's `Bundled Git plugin trusted.`
7. `npm run smoke:winui-installer`, with every SumaFile closed
8. `npm run smoke:winui-msi-lifecycle`
9. `npm run build:winui`, then `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-copy-targets.ps1`

Expected: all pass, and `Assert-BundledPlugins` covers three plugins in the payload and in both installs.

- [ ] **Step 5: Update spec §11.4 and §13**

- [ ] **Step 6: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 15: Remove Git from core: IPC, service, App and Core

**Files:**
- Rust:
  - delete `crates/simplefile-core/src/git.rs` and its `lib.rs:17` line;
  - `crates/simplefile-core/src/models.rs` 242-294: the four Git types; `FileEntry.git_status` stays;
  - `crates/simplefile-service/src/dispatch/handlers.rs`: 5 and 426-503;
  - `params.rs` 94-111;
  - the `dispatch/mod.rs:67` comment;
  - `dispatch/tests.rs` 638-648 and 767-777;
  - `crates/simplefile-ipc/tests/schema_consistency.rs:94`;
  - `utils::hidden_command`, which has no caller left.
- Schema and generated code:
  - `ipc/schema/v1/commands.json`: delete the 11 entries, and `domainMethodCount` goes from 86 to 75;
  - `types.json`: delete the four Git types; `git_status` stays, with a note;
  - `protocol.json:33` notes, to 75;
  - regenerate `crates/simplefile-ipc/src/protocol_generated.rs`, `src-winui/SimpleFile.Ipc/Protocol.Generated.cs` and `NamedPipeJsonClient.Generated.cs` with `npm run generate:ipc-bindings`;
  - `scripts/generate-ipc-bindings.mjs` 253-263;
  - `scripts/check-ipc-schema.mjs` 111-116 (75) and 184 (drop `get_git_status`);
  - `scripts/check-winui-parity-gate.mjs` 96-97 (75).
- C# IPC: `src-winui/SimpleFile.Ipc/ISimpleFileIpc.cs` 123-133, and the `Models.cs` DTOs at 738-786; `FileEntry.GitStatus` stays.
- Core:
  - `FileOperationService.cs` 531-559;
  - `AppCommandCatalog.cs` 99-108; `CommandIds.cs` 82-91; `CommandAliasCatalog.cs` 43-52;
  - `ContextMenuBuilder.cs`: 47-49, 123, 134, 151, 272-275 and 445-475;
  - `ContextMenuIconCatalog.cs` 81-88, keeping `ctx-refresh`;
  - `CommandSurfaceLayout.cs` 411-418;
  - `OmnibarIntent.cs` 107-110: drop `includeGit`;
  - `EntryPresentation.cs`: `GitStatusBrushKey` 117-138 and the `"git:"` handling (`ColumnLayout` was made generic in Task 9);
  - `ExplorerPane.cs` 33-47; `ExplorerWorkspace.FileOps.cs` 134-141;
  - `WorkspaceControllers/PaneEnrichmentController.cs` 9-145 and 240-264;
  - `WorkspaceNavigationController.cs` 495;
  - `UiSettings.cs` 36; `WorkspaceSettingsStore.cs` 30, 86 and 170;
  - `SavedWorkspaceLayout.cs` 137-138 and 206; `WorkspaceProfile.cs` 258-301 and 389-403. The built-in presets lose their `enableGitIntegration` argument. `WorkspaceChromeLayout.Apply` never applied that flag (it skips it today), so no preset or saved layout changes what a user sees. A stored layout's `enableGitIntegration` property is ignored when read and no longer written.
- App:
  - delete the six Git files;
  - `MainWindow.xaml`: `GitPanel` 125-135 goes, `PluginPanelBottomRegion` moves to row 2 and the extra `RowDefinition` goes, and `GitToggleButton` 314-332 goes;
  - `MainWindow.xaml.cs`: 77-84, 187-188, 644 and 1344-1348;
  - `MainWindow.Chrome.cs`: `ToFileRow` 822-833 no longer carries `GitText`, which it got through `FileRow.From` (`ExplorerRows.cs:67`), and `SameFileRow` 916-934 stops comparing it at 927. Task 11 added the decoration fields to both;
  - `MainWindow.Columns.cs`: 272 and 386-399;
  - `MainWindow.Commands.cs`: 226-227 (the palette's Git filter: plugin commands stay listed while `enabled` is false and refuse with `GitText.DisabledMessage`) and 906-908;
  - `MainWindow.CommandSurface.cs`: the `git-` clause;
  - `MainWindow.CommandRouting.cs` 114-123; `MainWindow.Omnibar.cs` 205 (the omnibar's `IsGitIntegrationEnabled` filter goes, as in the palette); `MainWindow.FileOperations.cs` 393-397 (`MainWindow.Transfer.cs` changed in Task 11);
  - `ExplorerRows.cs`: `GitText` and `GitChangeRow`;
  - `SettingsWindow.xaml` 198-205 and `SettingsWindow.SettingsState.cs` 56 and 133;
  - `App.xaml`: the `SfGit*Brush` keys.
- Tests:
  - `NullIpc.cs` 249-269; `ConfigurableIpc.cs` 11, 60 and 362-366;
  - `WinUiSourceShapeTests.cs`: replace `GitIntegration_UsesDockableWorkbenchAndCompleteDisableSwitch` 487-533 with `GitUi_LivesOnlyInTheGitPlugin` (Step 1), and update the 1029 chrome list in `UiAuditRegressionTests.cs` to name `PluginPanelWindow.cs`. The old test's "complete disable" half (`EnsureGitIntegrationEnabled`, the palette's Git filter, `ClearGitStatuses()`) is carried over by behaviour tests, not by source shape:
    - Task 13 `Commands_WhenDisabled_NotifyTheDisabledMessageAndRunNothing`, `GetAsync_EnabledFalse_ReturnsNothingAndCallsNoBackend` and `GetAsync_TurnedOff_HidesAShownWorkbenchOnce`;
    - Task 16 smoke step 2: with Git off, the commands are listed and refuse, and nothing Git is drawn;
  - `UiAuditRegressionTests.cs` 71-79; `UiAuditSprint3Tests.cs` 47-61 and 96-100; `UiAuditSprint4Tests.cs` 97;
  - `UxRecommendationsSettingsGitRemoteTests.cs` 93-166;
  - `ContextMenuBuilderTests.cs`, `AppCommandCatalogTests.cs`, `CommandSurfaceLayoutTests.cs`, `OmnibarIntentParserTests.cs`, `ColumnLayoutTests.cs`, `EntryPresentationTests.cs`;
  - `ExplorerWorkspaceTests.cs` 343-396 and the settings cases; `WorkspaceControllerTests.cs` 165-250; `WorkspaceSettingsStoreTests.cs`; `UiImprovementTests.cs` 112-124; `FolderViewSettingsTests.cs`.
- Docs:
  - `docs/winui-migration/parity-gate.md`: counts, §2.5, a new §2.7 "Git (plugin `com.sumafile.git`)", and rows 252, 351, 386 and 413;
  - `inventory.md:58`, plus a note at 659 that `get_git_status` went in phase 4;
  - `docs/winui-migration/architecture.md` 514-518: drop the eleven Git names from the frozen method list and `get_git_status` from the compatibility-only list, with a one-line note that they moved to the `com.sumafile.git` plugin protocol in phase 4;
  - `docs/winui-migration/parity-remaining-workflows.md` 96-106: the eleven Git rows get a "Moved to the Git plugin in phase 4 (`plugins/git/protocol.json`)" note in place of their service column;
  - `docs/winui-migration/rust-core-extraction.md`: a "Superseded in phase 4: `git.rs` left the core for `plugins/git/backend`" note at the `git.rs` section (307) and in the PR 9 row (364); the history stays;
  - `README.md` 15, 20, 108, 127-130 (Git for Windows on PATH), 268-274, 371, 383, 535 and 560;
  - spec §11.3, "Done in phase 4", like §11.2's Task 14 paragraph, which also records the decisions about what `EnableGitIntegration` gated (see "Spec refinements", T13 and T15); §3 (the counts).

**Interfaces:**
- Consumes (T11): decorations replace `GitText` everywhere. Consumes (T13): the plugin provides every removed surface.
- Produces: `domainMethodCount` 75. No `git` symbol is left in core except `FileEntry.git_status` / `FileEntry.GitStatus` (wire format) and the column id `git` (`ColumnLayout.DecorationColumnId`).

- [ ] **Step 1: Write the failing guard tests first**
- Rust, `dispatch/tests.rs`: `removed_git_methods_return_method_not_found`. All 11 names (`get_git_status`, `get_git_repository_status`, `get_git_file_statuses`, `git_stage_paths`, `git_unstage_paths`, `git_discard_paths`, `git_diff_path`, `git_commit`, `git_fetch`, `git_pull`, `git_push`) answer `ERR_METHOD_NOT_FOUND`.
- C#, `WinUiSourceShapeTests.GitUi_LivesOnlyInTheGitPlugin`:
  - no file under `src-winui/SimpleFile.App`, `SimpleFile.Core` or `SimpleFile.Ipc`, except `*.Generated.cs`, contains `GitWorkbench`, `EnableGitIntegration`, `GitText`, `ctx-git-`, `"git-panel"`, `GitRepositoryStatus` or `GetGitRepositoryStatusAsync`;
  - `MainWindow.xaml` has no `GitPanel` or `GitToggleButton`;
  - `plugins/git/ui/SumaFile.Git/Views/GitWorkbenchPanel.cs` exists.
- `UiImprovementTests.SavedLayout_WithTheRetiredGitFlag_StillLoadsAndApplies`: a stored layout with `"enableGitIntegration": false` parses and applies, and a captured layout's JSON has no `enableGitIntegration`. It replaces `ApplyingChromeLayout_LeavesGitAndFoldersOnTopSettingsAlone`, keeping its folders-on-top half.
- `UiImprovementTests.BuiltInProfiles_CarryNoGitFlag`: no built-in `WorkspaceProfile` serializes `enableGitIntegration`.
- `WorkspaceSettingsStoreTests.SaveAsync_NoLongerWritesEnableGitIntegration`: a recording backend sees no `enableGitIntegration` write, and the key is never read.

- [ ] **Step 2: Run them to verify they fail**

Run: `cargo test --locked -p simplefile-service removed_git_methods`, then `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~GitUi_LivesOnlyInTheGitPlugin|FullyQualifiedName~UiImprovementTests|FullyQualifiedName~WorkspaceSettingsStoreTests"`
Expected: FAIL. The Rust test sees successful replies, and the C# guard lists the core Git files.

- [ ] **Step 3: Remove everything listed, regenerate the bindings, fix the tests and update the docs**

Run `npm run generate:ipc-bindings` and never hand-edit a generated file. For each deleted test, move its behaviour to the plugin tests or delete it:
- the P4 and P3 audit tests now live in `GitWorkbenchControllerTests` (Task 13);
- the discard-text test lives in `GitTextTests`;
- the "status button only inside a repository" test becomes `GitPluginManifestTests` (`when: folderFlag(inRepository)`) plus the Task 16 smoke;
- the "complete disable" assertions become the Task 13 and Task 16 tests named under Files.

- [ ] **Step 4: Run them to verify they pass**

Run, in order:
1. `cargo test --locked --all-features`
2. `cargo clippy --locked --all-targets --all-features -- -D warnings`
3. `npm run check`
4. `npm run test:maintenance`
5. `npm run build:winui`
6. `npm run check:winui`

Expected:
- `test result: ok` for every target;
- `Checked IPC v1 schema: 75 domain methods, …` and `WinUI parity gate lists 75 commands, …`;
- `0 Error(s)`;
- `Passed!` with `Failed: 0`.

- [ ] **Step 5: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

### Task 16: Git and no-plugin UI smokes, the startup budget, and the phase verification

**Files:**
- Create `src-winui/SimpleFile.App/MainWindow.UiSmoke.Git.cs`.
- Modify:
  - `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`: call `SmokeGitAsync` after `remote-backend-loss`; `SmokeServiceReconnectAsync` 920-971 also requires the Git backend to survive;
  - `scripts/smoke-winui-ui.ps1`: the Git fixture in `-PluginsDir` and `-NoPlugins` modes.
- Docs:
  - spec §13: the Git smoke in both modes, and that `-NoPlugins` with one root equals "`payload\plugins\` deleted";
  - spec §5.2: the measured span with four plugins;
  - the review notes: phase 4 closures for (a)-(h) and the location requirement, each naming its task and test. Two say what is still pending because CI is not run: the (h) closure (the hosted-runner span, 74.2 ms at 46860dc) and the installer closure (the NSIS upgrade from the previous release, proven only by `smoke:winui-upgrade`);
  - `docs/winui-migration/parity-gate.md` §2.7: automation, naming this smoke and the Task 12 and 14 tests.

**Interfaces:**
- Consumes everything above.
- **Script.** `-PluginsDir` and `-NoPlugins` modes require `git` (`Get-Command git`, else throw `Git for Windows is required for the Git plugin smoke`). Every git call uses `-c core.fsmonitor=false` and a 30 s bounded `Start-Process`. The script creates `<testRoot>\git`:
  - a bare `origin.git` (`git init --bare -b main`) and a `repo` (`git init -b main`) with repo-local `user.name`, `user.email` (`smoke@sumafile.invalid`), `commit.gpgsign=false` and `core.autocrlf=false`;
  - in `repo`: `tracked.txt`, `café 資料\hello.txt`, `literal [brackets] & spaces.txt` and `discard-me.txt`, committed. `origin` is added and `git push -u origin main` sets the upstream, which the `behind` count and `git pull` both need;
  - a `peer` clone that commits and pushes `from-peer.txt`;
  - then, in `repo`: `tracked.txt` and `discard-me.txt` modified, an untracked `new.txt`, and a staged `staged.txt`;
  - a `not-a-repo\plain.txt`.

  `repo` makes no commit of its own until step 5 has pulled, so the pull is a fast-forward. With the isolated, empty config, a pull of diverged branches fails with `fatal: Need to specify how to reconcile divergent branches.` (Git 2.56), which Task 12 pins separately.

  It exports `SUMAFILE_SMOKE_GIT=<testRoot>\git`. The App env keeps `GIT_CEILING_DIRECTORIES=<testRoot>`, so `repo` finds its own `.git` and `not-a-repo` none. It also gets `GIT_CONFIG_GLOBAL=<testRoot>\git\gitconfig` (an empty file) and `GIT_CONFIG_NOSYSTEM=1`, and the script's own git calls use the same two variables.
- **App:** `private async Task SmokeGitAsync(List<object> results, string mode)`. Every wait is bounded (10 s for UI, 60 s for git network steps), and every git CLI verification process is bounded too.

- [ ] **Step 1: Write the smoke (it fails until every earlier task is in place)**

`with` mode, step by step:
1. **Trust and surface.**
   - `com.sumafile.git` is Enabled through `bundled.json` from `PluginDiscovery.InstallRoot()`, and the approvals file has no record for it.
   - All ten `plugin:com.sumafile.git:*` commands are on the palette, and no bare `git-*` id remains in `AppCommandCatalog.All`.
   - `CommandSurface.OfferedPrimaryItems` contains `plugin:com.sumafile.git:git-panel`.
   - A `sumafile-git-backend` process runs under the install root.
   - `RowDecorations.FirstRequestAt` is later than the smoke's recorded `ready` time (carry-over h).
2. **Migration, Git off, and the Settings switch.**
   - Write `plugin.com.sumafile.git.enabled` = `""` and `enableGitIntegration` = `"false"` through `FileOperationService.SetSettingAsync`. Then `MigrateLegacySettingsAsync` returns 1, and the plugin key reads `"false"`.
   - After `InvalidateDecorationsAsync`, navigating to `repo` shows no decoration and no Git menu group, and the Git toolbar button is hidden.
   - **Commands while off.** All ten `plugin:com.sumafile.git:*` commands are still on the palette (`RefreshCommandPalette("Git")` lists them). `RunAppCommandAsync("plugin:com.sumafile.git:git-refresh")` opens the message bar with `Title == "Git"` and `Message == "Git integration is turned off. Turn it on in Settings > Plugins > Git."` within 10 s. This also proves that `IPluginDialogs.Notify` reaches the message bar.
   - **The Settings switch.** Open Settings on "Plugins" (`ShowSettingsAsync("Plugins")`) and find the control with AutomationId `PluginSetting.com.sumafile.git.enabled` in the Git card. Within 10 s it reads `IsOn == false`, the migrated value. Turn it on through the control (`toggle.IsOn = true`), which runs the card's write callback, and so `WriteSettingAsync`. Close Settings.
   - Without navigating, `repo`'s rows get their decorations within 10 s, and the Git toolbar button shows: the switch's write re-decorated the open folder.
3. **Decorations.** In `repo`, wait for the rows' `DecorationText`:
   - `tracked.txt` "modified", `new.txt` "untracked", `staged.txt` "added" and `discard-me.txt` "modified";
   - `FolderFlags` contains `com.sumafile.git:inRepository`.

   In `not-a-repo`: no decoration, no flag, the `ctx-plugin-menu:com.sumafile.git` group hidden, and the toolbar toggle hidden.
4. **Panel.**
   - Back in `repo`, invoke the generated toolbar button of `plugin:com.sumafile.git:git-panel`. The bottom region shows `PluginPanel.com.sumafile.git.workbench`, and the button's toggle state is on.
   - `Git.Branch` names `main`.
   - `Git.Changes` holds as many rows as `git status --porcelain=v1` lines.
   - **The toggle outside a repository.** With the panel still shown, navigate to `not-a-repo`. The decorations and the `inRepository` flag go, but the toolbar toggle stays visible (its panel is shown) with its toggle state on. Navigate back to `repo`.
5. **Mutations**, each through the panel's automation ids or the context menu, and each verified with the git CLI and in the pane. In this order, so that `repo` pulls before it commits:
   - Stage `new.txt`: `git diff --cached --name-only` lists it. Then unstage it.
   - Diff `tracked.txt`: `Git.DiffText` contains `Working tree changes`.
   - Discard `discard-me.txt`, accepting the dialog's primary button. The file content equals the committed text, and the row loses its decoration.
   - Fetch: a Transfers row titled `Fetching repo` completes, the status is behind by 1 and ahead by 0, and the message bar shows a non-empty message titled "Git".
   - Pull, a fast-forward: `from-peer.txt` exists on disk and in the pane.
   - Commit with the message `smoke commit`: `git log -1 --format=%s` gives `smoke commit`, and the message bar, titled "Git", contains `smoke commit`. `GitText.ResultMessage` shows git's own output, `[main <sha>] smoke commit …`, ahead of the summary.
   - Push: `git -C origin.git rev-parse main` equals `repo`'s `HEAD`.
6. **Detach, resize and the dialog gate.**
   - `.Detach` opens one `PluginPanelWindow` titled "Git workbench". `.Detach` again docks it.
   - `ResizePluginPanelBy(-40)` changes `PluginPanelBottomHeight`, clamped.
   - Detach again and start a commit, so the commit dialog is open on the window. Then `DisableAsync(git)`:
     - within 10 s the window is closed, `!DialogHost.IsDialogOpen`, and the decorations, the menu group and the toolbar button are gone;
     - the backend process exits.
   - **A save while Git is disabled.** Force a settings save with `workspace.SaveUiSettingsAsync()`. Read back with `GetSettingAsync("commandSurface.layout")`, the stored layout still holds `plugin:com.sumafile.git:git-panel`; the toolbar does not draw it.
   - `EnableAsync` brings the decorations, the menu group and the toolbar button back, at its old position, after that save.
7. **A removed toolbar toggle stays removed.** Remove the Git item from `CommandSurface.PrimaryToolbar`, save, then `RestartBackendAsync(git)`, which raises `Changed`. The item is still absent. Then `DisableAsync(git)`, a save, and `EnableAsync(git)`: it is still absent.

`none` mode, `SmokeGitNoneAsync`:
- no `git-*` or `plugin:com.sumafile.git:*` command or toolbar action;
- no `GitPanel`, `GitToggleButton` or `PluginPanel.*` element;
- the context menus contain no `ctx-git-*` and no `ctx-plugin-menu:com.sumafile.git`;
- an opened Settings window has no "Git integration" text;
- navigating to `<testRoot>\git\repo` shows no `DecorationText`;
- no `sumafile-git-backend` process runs, and the coordinator reports `HasProviders == false`, so nothing in the App can start git.

`service-reconnect` also requires the `sumafile-git-backend` process to survive the service restart, and `repo` rows to be decorated again after relisting.

Each mode ends with `results.Add(new { scenario = "git", mode, …, passed = true })`, so the script's summary reads `git=1`.

- [ ] **Step 2: Run the smokes and the startup budget**

Run, in order:
1. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`
2. `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`
3. `npm run check:winui-startup-budget`
4. `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`
5. `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild`

Expected:
- `UI runtime smoke passed`, with `plugins=1`, `sevenzip=1`, `remote=1`, `git=1`, `service-reconnect=1`;
- `plugins-discovered span <50 ms> (budget 50 ms, count=4)` and `WinUI startup budget passed`;
- `UI runtime smoke passed` with `git=1` in `none` mode;
- the plain run passes.

Fix anything that fails test-first in the owning task's files, then rerun.

- [ ] **Step 3: Run the full phase verification**

Close every running SumaFile first; the installer smokes refuse otherwise. Before the first command, take three snapshots:

```powershell
. scripts/lib/plugin-staging.ps1
$legacy = Get-LegacyPluginLocationSnapshot
$machine = Get-SumaFileUninstallEntries -Scope Machine
$default = Get-FolderSnapshot "$env:LOCALAPPDATA\Programs\SumaFile-WinUI"
```

Then run, in order:
1. `npm run generate:ipc-bindings` (no diff), then `npm run check`, then `npm run test:maintenance`
2. `cargo fmt --all -- --check`, then `cargo clippy --locked --all-targets --all-features -- -D warnings`, then `cargo test --locked --all-features`
3. `scripts/build-fixture-plugin.ps1`, then `scripts/build-plugins.ps1`, then `scripts/test-plugin-staging.ps1`
4. `npm run check:winui`, then `npm run build:winui`, then `scripts/test-plugin-copy-targets.ps1`
5. The three UI smokes of Step 2, and the budget check right after the `-PluginsDir` one
6. `npm run release:build`, then `npm run smoke:winui`, then `npm run check:winui-startup-budget`, then `npm run smoke:winui-file-ops`, then `npm run smoke:winui-msi`, then `npm run smoke:winui-msi-lifecycle`, then `npm run smoke:winui-installer`
7. `Assert-LegacyPluginLocationsUnchanged -Before $legacy`, then `Assert-FolderSnapshotUnchanged -Path "$env:LOCALAPPDATA\Programs\SumaFile-WinUI" -Before $default -Label 'The default install folder'`, then compare `Get-SumaFileUninstallEntries -Scope Machine` with `$machine`

Also check that nothing is left behind:
- no process started from the test folders, `dist\winui` or the disposable install folders, whether `SumaFile`, `simplefile-service` or `sumafile-*`;
- no HKCU "SumaFile" uninstall entry;
- neither lifecycle ProductCode.

The machine-wide 1.0.4 entry stays, untouched.

Expected: every command exits 0 and prints the success lines named in Tasks 2, 3, 6, 12, 14 and 15. The legacy snapshot, the default folder and the machine entries are unchanged.

- [ ] **Step 4: Record the closures and measurements in the review notes and the spec**

The closures name, for each carry-over and for the location requirement, the task and the tests that close it. The (h) closure records the local span with four plugins and that the hosted-runner evidence is still pending. The installer closure records that the NSIS upgrade from the previous release waits for the first CI run of `smoke:winui-upgrade`.

- [ ] **Step 5: Snapshot:** run `bash .superpowers/tools/snap.sh`

---

## Critique resolutions

The plan review raised 32 items. Several name the same defect twice, and each pair is resolved once and cross-referenced. The task structure and numbering are unchanged: every fix lands in the task that owns the code.

1. **(Important) The folder open at startup is never decorated** (T11 with T9). Same defect as 17.
   - Task 9: the coordinator gains `Generation`, bumped by `MarkStartupReady` and by a `SetSource` that changes the set. The navigation hook reads it before `DecorateAsync`, and after publishing re-decorates the pane when it changed. `InvalidateDecorationsAsync` skips panes still listing.
   - Task 11: the ready callback runs `InvalidateDecorationsAsync()` right after `MarkStartupReady()`.
   - Pinned by Task 9 `MarkStartupReady_RedecoratesTheShownFolders` and `PublishedListing_AskedBeforeTheGenerationChanged_IsDecoratedAgain`; Task 11 `StartupReady_RedecoratesAfterMarkingReady`; and Task 11's "startup folder" smoke step. That step starts a navigation before the gate opens and requires decorations with no further navigation. Review Focus 6 is new.
2. **(Important) #82 comes back through the decoration slot** (T4/T9/T11). The coordinator starts every source call with `Task.Run` (Task 9), so `GetRowDecorationsAsync`'s activation is always queued to the UI thread. `ApplyRowDecorationSource` and `InvalidateDecorations` only queue (Task 11). `IPluginPanels` always queues, and panel bodies load through the queue (Tasks 7 and 10).
   - Pinned by `DecorateAsync_NeverRunsTheSourceOnTheCallingThread`, `InvalidateDecorations_NeverRunsTheSourceOnTheCallingThread`, `RowDecorationCoordinator_StartsEverySourceCallOnTheThreadPool`, `ApplyRowDecorationSource_OnlyQueues`, `PanelBodies_LoadThroughTheQueueWithThePanelSlotKey` and `PluginPanelsService_QueuesShowAndHide`.
   - Task 11 extends the #82 closure to decorations and panels.
3. **(Important) #63 only half closed** (T4). Same as 18. `_loadFailureTeardowns` is now per plugin id. `ScanAsync` (Discover and Rescan), `EnableAsync`, `DisableAsync` and `RestartBackendAsync` wait for it under `_ops` before they create a runtime or start a backend, bounded and logged. So the old backend releases its pipe before a new one starts.
   - Pinned by `Rescan_AfterALoadFailure_WaitsForItsTeardown` (no second launch while the gate is closed, exactly one after), `Disable_AfterALoadFailure_ReturnsOnlyAfterItsTeardown`, `Enable_AfterALoadFailure_WaitsForItsTeardown` and `LoadFailureTeardownWait_IsBoundedAndLogged`.
4. **(Important) `OfferedPrimaryItems` loses the toggle after a save while the plugin is absent** (T4, exercised by T16). Same as 16. One rule, recorded in "Spec refinements" T4:
   - `Normalize` keeps well-formed `plugin:<id>:<command>` items in place while the catalog lacks them;
   - `VisibleItems()` draws only known items, and the signature, the overflow planner and the rendering use it;
   - an offer is made once, so only the user's removal is final;
   - the Settings toolbar editor carries the offers and shows absent items as "Plugin not loaded".

   Pinned by `PluginToolbarItem_SurvivesASaveWhileThePluginIsDisabled`, `PluginToolbarItem_RemovedByTheUser_StaysRemovedAcrossDisableAndEnable`, `Normalize_DropsUnknownCoreIdsButKeepsWellFormedPluginIds`, `VisibleItems_CollapseTheSeparatorsAroundHiddenItems` and two source-shape tests. Task 16 step 6 forces a save between `DisableAsync` and `EnableAsync`, and step 7 repeats it for a removed item.
5. **(Important) No proof that Settings writes through `WriteSettingAsync`** (T11/T16). Task 11 adds `PluginSettingsCard_WritesThroughWriteSettingAsync` and an AutomationId on every declarative control (`PluginSetting.<pluginId>.<key>`). In Task 16 step 2 the smoke opens Settings > Plugins, asserts the Git `enabled` switch reads off after the migration, turns it on through the control, and requires the open folder to be decorated without navigating. Review Focus 1 now names both.
6. **(Important) `EnableGitIntegration`'s consumers have no recorded decision** (T15, T13). Each is decided and recorded under "Spec refinements" (T13 and T15) and in spec §11.3 (Tasks 13 and 15).
   - Profiles and saved layouts: the claim was checked against the code. `WorkspaceChromeLayout.Apply` never applied the flag (`ApplyingChromeLayout_LeavesGitAndFoldersOnTopSettingsAlone` pins that), so nothing a user sees changes. The presets drop the inert argument, a stored flag is ignored, and `BuiltInProfiles_CarryNoGitFlag` and `SavedLayout_WithTheRetiredGitFlag_StillLoadsAndApplies` pin the result.
   - Palette and omnibar: the commands stay listed while Git is off and refuse with `GitText.DisabledMessage`. Pinned by `Commands_WhenDisabled_NotifyTheDisabledMessageAndRunNothing` and Task 16 step 2, which checks palette visibility and the message-bar text.
   - "Complete disable" is kept: turning Git off clears the decorations, the flag, the menu group and the toggle, and hides an open workbench. The hide happens once, at the switch from on to off, so a workbench the user opens on purpose while Git is off stays and shows the disabled message (`GetAsync_TurnedOff_HidesAShownWorkbenchOnce`). Task 15 names these tests as the replacement for the old source-shape assertions.
   - Decorations run on every folder sync whatever the column layout, because they carry `inRepository`: one cached `git status` per sync inside a repository, none outside. Pinned by `Navigate_DecoratesWhateverTheColumnLayout`.
7. **(Important) Plugin temp files land in `%TEMP%`** (T1). Implemented rather than ruled out, since the user asked for no plugin files outside the install folder.
   - `PluginBackendHost` creates `plugins-data\<id>\tmp` and launches every backend with `TMP` and `TEMP` pointing there, through a new environment parameter on `IPluginProcessLauncher`. That covers the 7-Zip listfiles (`ops.rs:590`) and every child, 7za and git included.
   - Plugin UI code is guarded against resolving temp or user folders.
   - Pinned by `Start_PointsTmpAndTempAtTheDataFolder`, `ProcessLauncher_PassesTheEnvironmentToTheBackend`, `PluginUiSources_NeverResolveTempOrUserFolders`, Task 2's smoke assertion on each running backend's `tmp` folder, and the NSIS smoke's `tmp` check. Recorded in Global Constraints and spec §4.1.
8. **(Minor) The forbidden-snippet check trips on Task 2's own helpers** (T2). Same as 23. The legacy snapshot helpers and Case 9 build the paths from `Join-Path` segments, and the check skips only its own source, so there is no exemption list.
9. **(Minor) Partial upgrade proof; the release workflows lack the MSI lifecycle smoke** (T3). `smoke:winui-msi-lifecycle` is added to `release-build.yml` and `release.yml`, with `workflowSnippets` for both. Task 3's docs, Task 16's closures and the Global Constraints record that the NSIS upgrade from the previous release waits for the first CI run of `smoke:winui-upgrade`.
10. **(Minor) The WiX purge works only for a folder named `SumaFile-WinUI`** (T3). The WiX purge now removes exactly the four app-owned items (`plugins\`, `plugins-data\`, `plugin-approvals.json*` and `plugins.log*`) on uninstall only, whatever the folder is named, and never the folder itself. The lifecycle smoke installs into `SumaFile Custom` to prove it, and the packaging check forbids a whole-folder `RemoveFolderEx`.
11. **(Minor) The (f) prune is proven by hand** (T6). The new `scripts/test-plugin-copy-targets.ps1` runs the real `CopyBundledPlugins` target against temp folders and checks that the stale file goes while the user plugin and `plugins-data` stay. It runs in Tasks 6, 14 and 16. `PluginCopyTargets_ReplaceOnlyTheirOwnFolders` covers `RemoveDir` in `CopyBundledPlugins`, `CopyRemotePluginFixture` and (Task 14) `CopyGitPluginFixture`.
12. **(Minor) `Invoke-GitPluginSmoke` is not isolated from git config and depends on `init.defaultBranch`** (T14). Same as 27. The Global Constraints' git-isolation rule now lists the file-ops smoke and every smoke whose app starts the Git backend. `Invoke-GitPluginSmoke` sets `GIT_CONFIG_GLOBAL` and `GIT_CONFIG_NOSYSTEM` for the backend and its own git calls, and uses `git init -b main`. The startup and MSI-extract smokes set the same variables.
13. **(Minor) Docs that still name removed surfaces** (T15).
    - Task 15 adds `architecture.md` (the method and compatibility lists), `parity-remaining-workflows.md` (the eleven rows) and `rust-core-extraction.md` (a superseded note), and adds `MainWindow.Chrome.cs` (`ToFileRow`, `SameFileRow`) to the App list.
    - Task 14 corrects spec §13's packaging sentence.
    - Task 10 records in §7 that `PluginPanelHost` names the two regions driven by `MainWindow.PluginPanels.cs`, and the Architecture and Global Constraints say the same.
14. **(Minor) Untested behaviours, and (h) evidence never retaken** (T11/T16).
    - Task 16 step 4 asserts that the panel toggle stays visible in `not-a-repo` while the panel is shown.
    - Steps 2 and 5 assert message-bar text: the disabled message, the fetch result and the commit output. That proves `IPluginDialogs.Notify` reaches the message bar.
    - The (h) closure in Tasks 4 and 16 states that the hosted-runner evidence is still pending.
15. **(Minor) An unwritable install folder is handled only for Install** (T1). Global Constraints and spec §4.1 now state the behaviour for each operation:
    - Install fails with its message;
    - Enable fails with the approvals error and changes nothing; a failed Disable write lasts for the session;
    - log writes are skipped;
    - a backend fails with a clear data-folder message;
    - nothing falls back to `%LOCALAPPDATA%`.

    Pinned by `Enable_ApprovalsFolderNotWritable_ThrowsAndChangesNothing` and `Start_DataFolderNotWritable_FailsWithAClearMessage`.
16. **(Important) The offered-items fix loses the toggle for good**: see 4.
17. **(Important) Nothing re-decorates after the startup gate opens**: see 1.
18. **(Important) #63 also covers Enable and Restart**: see 3.
19. **(Important) The Git smoke fails at Pull** (T16, T12).
    - The smoke and `TestRepo` push with `git push -u origin main`, so the upstream exists.
    - Step 5 now runs Fetch (behind 1, ahead 0) and Pull, a fast-forward, before Commit and Push. The Rust `fetch_pull_push_against_a_local_bare_remote` uses the same order.
    - The diverged case is pinned on purpose by `pull_with_diverged_branches_fails_with_gits_message` and `failed_pull_fails_its_row_with_gits_message`.
20. **(Important) The installer smokes would refuse on this machine and could touch the 1.0.4 install** (T3, T14, T16). The guard looks only at HKCU entries, the lifecycle MSIs' own ProductCodes and running SumaFile processes.
    - The smokes snapshot the HKLM 1.0.4 entry and the default-path folder `%LOCALAPPDATA%\Programs\SumaFile-WinUI` and assert both unchanged. They never launch from the default path: the fallback is removed and forbidden by the packaging check.
    - The "nothing left" checks in Tasks 3 and 16 are scoped the same way, and Task 14 runs the same two smokes with every SumaFile closed.
21. **(Important) The source's key filter drops git's path forms** (T11 vs T9). The new `RowDecorationKeys` (Task 9) is the one key rule. `PluginRowDecorationSource` filters with it and stores kept entries under the requested spelling. Pinned by `Decorate_KeysDifferingInCaseOrSeparator_AreKept`, now listed in Review Focus 3.
22. **(Minor) Sidebar and panel body loads share one key shape** (T4/T10). Queue keys carry their slot: `PluginSidebarLayout.BodyLoadKey` (`sidebar:`) and `PluginPanelLayout.BodyLoadKey` (`panel:`). Pinned by `BodyLoadKey_CarriesTheSidebarSlot`, `BodyLoadKey_CarriesThePanelSlot` and the source-shape tests.
23. **(Minor) Forbidden snippets versus Case 9**: see 8.
24. **(Minor) The golden secret tests would not fail first** (T6). They are now described as pinning tests that pass on the first run, because `mod tests { use super::*; }` reaches the private functions. Step 2 expects them to pass.
25. **(Minor) `PluginLoadingTests.FakePluginHost` is missing from the doubles** (T7). It is added to Task 7's file list and to File structure. Step 3 asks for a search for every implementer.
26. **(Minor) A job-assignment race with Git for Windows' launcher** (T12). `spawn_in_job` creates git suspended, assigns the job and only then resumes it, using `Win32_System_Diagnostics_ToolHelp` for the thread. Pinned by `spawn_in_job_assigns_the_job_before_the_process_runs`, which keeps `cancelled_fetch_kills_git_and_its_children` deterministic.
27. **(Minor) The file-ops smoke is outside the git-isolation rule**: see 12.
28. **(Minor) The NSIS reinstall could write into the existing default folder** (T3). Before the reinstall, the smoke requires the HKCU `InstallLocation` that `InstallDirRegKey` reads. It snapshots the default folder at the start and fails if the reinstall changed it. The MSI lifecycle checks the remembered `InstallDir` value before its upgrade in the same way. The packaging check runs first, so a missing `InstallDirRegKey` stops the run before any installer does.
29. **(Minor) A developer's `SUMAFILE_PLUGIN_*` variables leak into the installed app** (T3/T16). `Start-InstalledSumaFile` removes the four variables from the launched app's environment. It also sets the disposable data paths and git isolation.
30. **(Minor) Profiles and saved layouts lose the Git switch** (T15): see 6. On checking, they never applied it, and this is now recorded.
31. **(Minor) Cancellations logged as warnings** (T8). `GetRowDecorationsAsync` returns null with no log line when the caller's token cancels; only throws and null results log. The coordinator skips cancelled sources silently. Pinned by `GetRowDecorations_CancelledByTheCaller_ReturnsNullWithoutALogLine` and `DecorateAsync_CancelledSource_IsSkippedWithoutALogLine`.
32. **(Minor) The `ThemeResourceLookup.Brush` arity and the warm-up source-shape test** (T11/T4). Task 11 writes `ThemeResourceLookup.Brush(this, DecorationBrushKeys.Normalize(…))`, which `FileRowView_RendersDecorationsThroughTheBrushAllowList` checks. `ConnectAsync_WarmsPluginRenderingBeforeAwaitingTheService` matches `WarmPluginRendering(` inside the `TryEnqueue(DispatcherQueuePriority.Low, …)` call.
