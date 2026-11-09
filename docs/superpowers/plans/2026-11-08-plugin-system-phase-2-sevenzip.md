# Plugin System Phase 2: 7-Zip Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move 7-Zip out of SumaFile's core into the first-party plugin `com.sumafile.sevenzip`. Plugin archive formats get full in-pane browsing, reading and mutation. Plugin-format extract and create run as cancellable Transfers rows with progress. Bundled plugins are trusted without a consent dialog, and the release payload ships the plugin under `plugins\com.sumafile.sevenzip`.

**Architecture:** The core archive module gains an engine-backed archive kind. Browsing, materializing and mutation route to the registered `ArchiveEngine`, and core keeps every safety rule: entry-name validation, private staging with containment, size budgets, conflict resolution and the "create into a temp file, then rename" step. The built-in formats keep their own code paths. The plugin pipe protocol grows `archive.read_entry/rename/delete/add`, an `archive.progress` notification and backend-wide cancel flags that live as long as the request they cancel. The service talks to each plugin through a small connection pool and runs every call that can reach an engine off its session loop, so a long call never blocks browsing or the host's own settings lookups. It treats progress as a heartbeat for read and transfer calls and relays it as `operation-progress`. A cancel travels on a second pooled connection. A per-plugin registration ticket makes the last `register_archive_engine` or `unregister_archive_engine` the service received win. A new Rust backend, `sumafile-sevenzip-backend` in `plugins/sevenzip/`, drives the bundled `7za.exe` on blocking threads, with a bounded number of processes per operation. The host trusts first-party plugins through `<install root>\bundled.json`, retries and bounds engine registration, and re-registers after a service restart without holding up the reconnect. It raises `Changed` only once the service's engine set has changed.

**Tech Stack:** Rust 2021 (tokio, serde_json, std::process), C# / .NET 10 / WinUI 3 (Windows App SDK 2.4.0), xUnit, PowerShell 5.1 scripts, Node checks, NSIS and WiX v3.14, 7-Zip Extra 26.03 from `third_party/7zip/win-x64`.

**Spec:** `docs/superpowers/specs/2026-11-07-plugin-system-design.md`. This plan implements §14 phase 2 and §11.1, and refines §6.2, §6.4, §9, §10, §11.4 and §13. It also closes the phase 1 follow-ups that phase 2 depends on, listed in `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md`. Phase 1 plan: `docs/superpowers/plans/2026-11-07-plugin-system-phase-1.md`.

## Global Constraints

These are copied from the spec and the phase 1 plan; the binding values are verbatim.

- All projects inherit `TreatWarningsAsErrors`, `Nullable` and `RestorePackagesWithLockFile` from `Directory.Build.props`. After any project change, run `dotnet restore --force-evaluate` on the project. Phase 2 creates no new C# project and no new project reference to `Microsoft.WindowsAppSDK`.
- Rust: workspace edition 2021. `cargo fmt --all -- --check`, `cargo test --locked --all-features` and `cargo clippy --locked --all-targets --all-features -- -D warnings` must pass.
- `npm run check` must stay green. `scripts/check-ipc-schema.mjs` asserts the exact domain method count, and it stays **99**. Spec §6.4: "`register_archive_engine` and a matching `unregister_archive_engine` are the only additions to the fixed method list." Phase 2 adds or removes no domain method; `extract_archive` and `create_archive` only gain an optional `operationId`. `check-winui-parity-gate.mjs` keeps 99.
- The startup budgets in `StartupTimingBudget` and `scripts/check-winui-startup-budget.mjs` stay as they are. The `MainWindow.Connect.plugins-discovered` delta must stay under 50 ms.
- Plugin id grammar: `^[a-z0-9][a-z0-9.-]{1,63}$`. Plugin settings key grammar: `^[a-z0-9][a-zA-Z0-9_-]{0,63}$`. Settings persist as `plugin.<id>.<key>`, so this phase uses `plugin.com.sumafile.sevenzip.binaryOverride`.
- Pipe name for a plugin backend: `SumaFile.Plugin.<sessionId>.<pid>.<pluginId>`. Connect timeout 5 s. Backend data directory `%LOCALAPPDATA%\SumaFile\plugins-data\<id>\`.
- Plugin roots in scan order: `<AppContext.BaseDirectory>\plugins`, then `%LOCALAPPDATA%\SumaFile\plugins`. A duplicate id resolves to the later (user) root.
- Approval store file: `%LOCALAPPDATA%\SumaFile\plugin-approvals.json`.
- Product name in user-facing text is "SumaFile". Internal namespaces keep `SimpleFile`.
- Plugin pipe deadlines (phase 1 ruling): handshake 5 s, `archive.list` 30 s, `archive.extract` and `archive.create` 600 s, and a 30 s bounded wait ("plugin is busy"). On a connection whose handshake carried `client == "simplefile-service"`, `host.log` is a local no-op and every other `host.*` call fails fast.
- Spec §6.4 note, exact text: `7-Zip support requires the SumaFile 7-Zip plugin.`
- Spec §11.1 folder: `plugin.json`, `sumafile-sevenzip-backend.exe`, `7za.exe`, `7za.dll`, `7zxa.dll`, `License.txt`, `SUMAFILE_BUNDLE.md`. Contributions: `archiveFormats` for `.7z` with all four capabilities. Setting `binaryOverride` (path) replaces the `SIMPLEFILE_7Z` environment variable. No UI assembly.
- Spec §12: plugins reference only the SDK package and the helper crate, never the host projects. The backend crate depends on `sumafile-plugin` and never on `simplefile-core` or `simplefile-service`.
- Spec §2: the built-in zip, tar, tar.gz and rar engines keep their behaviour. Two things change on their path:
  - Task 4 changes scheduling. In-archive `create_directory`, `create_file` and `rename_entry`, and `open_file`, `reveal_in_folder` and `open_file_with` of an archive entry, leave the session loop for the General blocking lane.
  - Task 6 drops `7z` from the RAR-creation error text.
- The engine registry is process-wide. Every test binary therefore serializes the tests that register an engine or assert that an extension has none:
  - Service integration tests take `support::REGISTRY_LOCK`.
  - Core tests that touch `.7z` take `SEVEN_Z_REGISTRY_LOCK`.
  - Other core tests keep using their own owner and extension.
- Spec §14: the phase ends with the full check suite green and a release-build smoke run. That is Task 12's release build plus Task 13's UI smokes.
- Workspace rules for this phase (user instruction):
  - Work only in `R:\Repos\SumaFile` on branch `plugin-system-phase-1`.
  - **Never commit.** Never run `git commit/stash/reset/checkout/switch/restore/rebase/merge/push/add/clean`. Never write to GitHub.
  - Every "commit" step in this plan is **Snapshot: run `bash .superpowers/tools/snap.sh`**. It prints a tree id and creates no commit.
- NuGet is offline:
  - Every restore must work from `%USERPROFILE%\.nuget\packages`; `dotnet restore --force-evaluate` is allowed.
  - Never add a NuGet package that is not already in that cache. If one is truly needed, stop and report BLOCKED.
  - cargo may fetch crates. Never publish anything (`cargo publish` only with `--dry-run`).
- Lock files stay consistent with project files: `packages.lock.json`, `Cargo.lock` and `tools/sevenz-evaluation/Cargo.lock`.
- Use generous timeouts: builds and smokes take up to 10 minutes.
- CI is not run in this phase. The user ruled that CI fixes wait until every phase is done. Workflow files are edited here and validated statically by `npm run check`.

## Review Focus

1. A user browses a `.7z` pane, or the pane loads its folder metrics, while a ten-minute 7-Zip extract runs. The listing must still answer within its 30 s deadline and must never fail with "plugin com.sumafile.sevenzip is busy".
   - The pool proves this against Hello in Task 4 (`concurrent_list_is_answered_while_a_slow_extract_runs`).
   - The 7-Zip backend runs every 7za process on a blocking thread over a two-worker runtime. Task 6 proves it end to end (`sevenzip_plugin_answers_a_listing_while_two_long_creates_run_and_cancels_them`).
2. A tab restored at startup inside `pack.7z\docs`, before the 7-Zip engine has registered, shows the plugin note. Once `.7z` registers it re-lists without user action, and it shows the note again if the plugin is later disabled or faults. Three layers prove it:
   - The service returns the note for a pane listing of `pack.7z` and of `pack.7z\docs`: Task 6 (`seven_zip_listing_without_an_engine_reports_the_note_through_dir_list`, `list_directory_on_an_unserved_7z_reports_the_plugin_note`).
   - The host raises `Changed` after a lost backend's engines are unregistered: Task 7 (`ArchivePlugin_Lost_RaisesChangedAfterTheUnregisterCompletes`).
   - The workspace re-lists the affected panes: Task 9 (`RefreshArchivePanes_RelistsPanesInsideArchivesWhoseEngineChanged`).
   - End to end, Task 13 kills the real backend (`sevenzip-backend-loss`).
3. Deleting the last entry inside a `.7z` fails with "7-Zip archives cannot be rewritten with no entries" and leaves the archive byte-for-byte unchanged. Test in Task 5 (`deleting_the_last_entry_fails_and_leaves_the_archive_unchanged`).
4. Entry names with Unicode, spaces, brackets and `&` survive create, list, browse, rename, read (open or preview), delete and extract. Examples: `café 資料/hello.txt` and `literal [brackets] & spaces.txt`. Tests in Task 5 (`unicode_and_punctuation_names_survive_every_operation`) and Task 6 (service path).
5. A user disabled the bundled 7-Zip plugin, then upgraded SumaFile, which changed its `plugin.json` hash. The plugin stays disabled and no consent dialog appears. Test in Task 8 (`BundledPlugin_UserDisabled_StaysDisabledAfterUpgrade`).
6. Opening, previewing or copying a file out of a `.7z` never stalls on the backend's `binaryOverride` lookup. That lookup goes back through the host to `get_db_settings` on the same service connection, so no call that reaches an engine may run on the session loop. Tests:
   - Task 4: `open_file_on_an_archive_entry_does_not_block_the_session_loop` and `archive_entry_shell_calls_and_mutations_run_on_the_general_lane`.
   - Task 6: `sevenzip_read_with_a_host_settings_lookup_completes_quickly`.

---

## File structure

**Created**

- `crates/simplefile-core/src/archive/mutate_engine.rs`: engine-backed rename, delete, add, new folder or file, and copy and move routing.
- `crates/simplefile-core/src/archive/placeholders.rs`: first-party plugin formats that report unavailable when no engine serves them (`.7z`).
- `crates/simplefile-core/src/archive/tests/json_engine.rs`: the `JsonEngine` test double. Its archive file is JSON.
- `crates/simplefile-service/src/plugin_archive/{mod.rs, connection.rs, pool.rs}`: replaces `plugin_archive.rs`.
  - `mod.rs`: registration and `PipeArchiveEngine`.
  - `connection.rs`: one pipe, its I/O thread, and the call loop with progress, idle deadline and cancel watch.
  - `pool.rs`: the per-plugin pool.
- `crates/simplefile-service/tests/support/mod.rs`: holds four things:
  - the in-process service harness that collects events;
  - `REGISTRY_LOCK` and `Unregister`, moved from `tests/plugin_archive.rs`;
  - a fake host connection;
  - staging and launch of the real plugin exes.
- `crates/simplefile-service/tests/hello_plugin.rs`: drives the real Hello backend exe through the service.
- `crates/simplefile-service/tests/sevenzip_plugin.rs`: drives the real 7-Zip backend exe with the bundled 7za through the service.
- `plugins/sevenzip/plugin.json` and the crate `plugins/sevenzip/backend/` (package `sumafile-sevenzip-backend`, lib `sumafile_sevenzip_backend`, bin `sumafile-sevenzip-backend`):
  - `Cargo.toml`
  - `src/lib.rs`, `binary.rs`, `entries.rs`, `run.rs`, `ops.rs`, `handler.rs`, `main.rs`
  - `tests/real_7za.rs`, `tests/process.rs`
- `tools/sevenz-evaluation/src/plugin_engine.rs`: implements the core `ArchiveEngine` over the plugin library, for the evaluation baseline.
- `src-winui/SimpleFile.PluginHost/Trust/BundledPluginList.cs`: reads `<install root>\bundled.json`.
- `src-winui/SimpleFile.Tests/ArchiveEngineNavigationTests.cs`: engine-format navigation tests. They sit in `[Collection("Archive formats")]` because the `ArchivePaths` engine state is process-wide.
- `src-winui/SimpleFile.Tests/PluginEnvironmentTests.cs`: every test that sets or reads a plugin environment variable. They sit in the `Plugin environment` collection, which has parallelization disabled.
- `src-winui/SimpleFile.App/MainWindow.ArchiveTransfers.cs`: Transfers rows for plugin-format extract and create, and the archive capability refresh.
- `scripts/build-plugins.ps1` and `scripts/lib/plugin-staging.ps1`: one staging function shared by dev runs, smokes and the release.

**Modified**, grouped by area (each task lists exact files):

- Core archive:
  - `engine.rs`, `path.rs`, `list.rs`, `extract.rs`, `create.rs`, `mutate.rs`, `capabilities.rs`, `mod.rs`, `tests.rs`
  - `crates/simplefile-core/src/file_ops.rs`, `crates/simplefile-core/src/dir_list.rs`
  - `seven_zip.rs` is deleted.
- Service:
  - `dispatch/{handlers.rs, params.rs, mod.rs, tests.rs}`, `session/{mod.rs, jobs.rs, tests.rs}`, `lib.rs`
  - `tests/plugin_archive.rs`
- Helper crate and fixture:
  - `crates/sumafile-plugin/src/{lib.rs, host.rs, server.rs}`, `tests/roundtrip.rs`
  - `crates/hello-plugin-backend/src/main.rs`, `tests/process.rs`
  - `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/plugin.json`
- Protocol and schema:
  - `ipc/schema/plugin/v1/protocol.json`, `ipc/schema/plugin/README.md`
  - `ipc/schema/v1/commands.json`, `ipc/schema/v1/events.json`
  - `scripts/check-ipc-schema.mjs`, `scripts/generate-ipc-bindings.mjs`
  - generated `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.Generated.cs`
- C# plugin host: `PluginManager.cs`, `PluginManager.ArchiveEngines.cs`, `PluginManager.Backend.cs`, `Backend/PluginBackendConnection.cs`, `Trust/PluginApprovalStore.cs`, `Discovery/PluginDiscovery.cs`.
- C# core and IPC:
  - `SimpleFile.Ipc/ISimpleFileIpc.cs`
  - `SimpleFile.Core/{ArchivePaths.cs, FileOperationService.cs, BackendSession.cs, ExplorerWorkspace.Navigation.cs, WorkspaceControllers/WorkspaceNavigationController.cs}`
- C# app: `SimpleFile.App/{MainWindow.xaml.cs, MainWindow.Plugins.cs, MainWindow.Transfer.cs, MainWindow.UiSmoke.cs, FileOperationDialogService.cs, CreateArchiveDialog.xaml.cs, SettingsWindow.xaml, SettingsWindow.Tools.cs, SimpleFile.App.csproj}`.
- C# tests (`SimpleFile.Tests`):
  - `ConfigurableIpc.cs`, `NullIpc.cs`
  - `ArchivePathsTests.cs`, `ExplorerWorkspaceTests.cs`, `FileOperationServiceTests.cs`, `NamedPipeJsonClientTests.cs`
  - `PluginManagerTests.cs`, `PluginTrustTests.cs`, `PluginBackendHostTests.cs`, `PluginDiscoveryTests.cs`, `PluginManifestReaderTests.cs`
  - `TransferManagerViewModelTests.cs`, `WinUiSourceShapeTests.cs`
- Packaging:
  - `scripts/{build-winui-release.ps1, check-winui-packaging.mjs, smoke-winui-ui.ps1, smoke-winui-file-ops.ps1}`
  - `scripts/{smoke-winui-startup.ps1, smoke-winui-msi.ps1, smoke-winui-nsis.ps1, smoke-winui-upgrade.ps1}`
  - `packaging/winui/sumafile-winui.nsi`, `.github/workflows/ci.yml`, `package.json`
  - root `Cargo.toml` and `Cargo.lock`
  - `tools/sevenz-evaluation/{Cargo.toml, Cargo.lock, src/main.rs, README.md}`
  - `third_party/7zip/win-x64/SUMAFILE_BUNDLE.md`
- Docs: spec §6.2, §6.4, §9, §10, §11.1, §11.4 and §13; `README.md`; `docs/winui-migration/{parity-gate.md, inventory.md}`.

## Cross-layer wiring ledger

Phase 1's critical defect was a producer that nothing consumed. Every producer in this phase is listed below with the task that wires its consumer and the test that proves the pair.

| Producer (task) | Consumer wired in | Proof |
| --- | --- | --- |
| `ArchiveEngine` v2 and `EngineContext` (T1) | `PipeArchiveEngine` forwarding (T1, pooled in T4); core browse and read (T1); core mutation (T2) | T4 `hello_engine_serves_browse_read_and_mutation_through_the_service` |
| Pipe methods `archive.read_entry`, `archive.rename`, `archive.delete`, `archive.add` (T3 protocol and Hello; T5 7-Zip) | Sent by the service (T1, T4) | T4 Hello e2e; T6 `sevenzip_plugin_serves_create_list_browse_extract_and_mutation` |
| `archive.progress` notification, `HostClient::archive_progress` (T3) | Pool relays it to `EngineContext.progress` (T4); service emits `operation-progress` (T4); Transfers row (T10) | T4 `tracked_extract_relays_plugin_progress_as_operation_progress`; T13 smoke |
| Backend-wide `cancel` flags, kept while their request runs (T3) | Service sends cancel on a second connection (T4); 7-Zip kills 7za (T5) | T3 roundtrip tests; T4 `cancel_operation_stops_a_slow_plugin_extract`; T5 `run::cancel_kills_a_running_process`; T6 `sevenzip_plugin_answers_a_listing_while_two_long_creates_run_and_cancels_them`; T13 smoke |
| `archive.progress` on read-class calls (T3 Hello, T5 7-Zip) | Read idle deadline in the pool (T4) | T4 `slow_read_entry_progress_extends_the_read_deadline`; T5 `read_entry_reports_progress_through_the_observer` |
| `HostClient::host_connection` (T3) | 7-Zip handler reads `binaryOverride` (T5) | T3 roundtrip test; T5 handler test; T6 `sevenzip_read_with_a_host_settings_lookup_completes_quickly`; T13 smoke ("binaryOverride is honoured") |
| `cancelledMessage` in the protocol doc (T3) | Constants in core and the helper crate (T3) and in `FileOperationService` (T10) | `check-ipc-schema.mjs` (T3, T10) |
| Registration ticket in the service (T4) | Host register and unregister order (T7) | T4 `register_that_completes_after_a_later_unregister_registers_nothing`; T7 `ArchivePlugin_FinalUnregisterAfterFailedAttempts_SkipsWhenANewerGenerationRegistered` |
| `operationId` on `extract_archive` and `create_archive` (T4) | `FileOperationService.*TrackedAsync` (T10) | T4 dispatch tests; T10 unit tests; T13 smoke |
| `cancel_operation` for archive operations (T4) | Transfers row Cancel (T10) | T4 Hello cancel test; T10 `ExtractArchiveTrackedAsync_CancelSendsCancelOperationOnceAndThrowsCanceled`; T13 smoke |
| `.7z` placeholder row and note (T6) | `dir_list` pane-listing routing (T6); pane, dialog and Settings surfaces (T9) | T6 core, session and `sevenzip_plugin` tests; T13 none-mode smoke |
| `PluginManager.ReregisterArchiveEnginesAsync` (T7) | `MainWindow.ReconnectBackendAsync`, without awaiting it (T7) | T7 unit tests; T13 smoke reconnect scenario |
| `PluginManager.Changed` after every engine-set change, the Lost path included (T7) | `MainWindow.RefreshArchiveCapabilitiesAsync` (T9) | T7 `ArchivePlugin_Lost_RaisesChangedAfterTheUnregisterCompletes`; T13 `sevenzip-backend-loss` |
| `[info] Archive engines registered: …` log line (T7) | Startup smoke (T12) | T12 release build |
| `bundled.json` (T11 dev and smoke staging; T12 payload) | `PluginManager.Evaluate` (T8); `BundledRoot` set in `MainWindow.Plugins.cs` (T8) | T8 unit tests; T12 `Assert-BundledPlugins` and the startup smoke's registration line; T13 smoke (7-Zip Enabled with no approval record) |
| `ArchivePaths.SetEngineFormats` and `ExplorerWorkspace.RefreshArchivePanesAsync` (T9) | `MainWindow` capability refresh on plugin change, startup and reconnect (T9) | T9 unit tests; T13 smoke |
| `binaryOverride` setting (T5 manifest and handler) | The phase-1 Settings > Plugins panel writes it; `host.get_settings` reads it | T13 smoke |
| `Install-BundledPlugins` staged folder (T11) | `dev:winui`, the App Debug copy and the smoke script (T11); the release payload and CI (T12) | T11 verification; T12 release build; T13 smoke |
| `Assert-BundledPluginsFresh` (T11) | `smoke-winui-ui.ps1`, with or without `-SkipBuild` (T11) | T11 verification; T13 smoke |
| `BackendSession.ServiceProcessId` (T13) | Smoke reconnect scenario (T13) | T13 |

---

### Task 1: Core engine model v2: browsing and reading engine archives

**Files:**
- Modify: `crates/simplefile-core/src/archive/engine.rs`:
  - trait v2 and the context types;
  - drop the module-doc promise that plugins never take over "zip, tar, rar or 7z" (7z stays built in until Task 6).
- Modify: `crates/simplefile-core/src/archive/path.rs:6-103`: `ArchiveKind`, `ArchivePath.kind`, `split_archive_path`, `is_archive_path`, plus the temp-path helper.
- Modify: `crates/simplefile-core/src/archive/list.rs:289-408`: `list_archive_directory` over `kind`; engine arm in `listing_entry_relative_path`.
- Modify: `crates/simplefile-core/src/archive/extract.rs`:
  - add `extract_archive_with`;
  - engine full extract and engine selective read go through core staging;
  - rename `SevenZipStaging` to `ArchiveStaging` with folder prefix `.sumafile-extract-`.
- Modify: `crates/simplefile-core/src/archive/create.rs:4-51`: `create_archive_with`, which forwards the context.
- Modify: `crates/simplefile-core/src/archive/mutate.rs`: field `format` becomes `kind`; `ensure_archive_can_modify(&ArchiveKind)`. Until Task 2 every engine kind answers `"<engine name> cannot modify <ext> archives"`.
- Modify: `crates/simplefile-core/src/archive/mod.rs`: exports.
- Modify: `crates/simplefile-service/src/plugin_archive.rs:416-492`: `PipeArchiveEngine` implements trait v2 by forwarding (wire shapes below). It no longer sends `innerPath` on `archive.extract`.
- Modify: `crates/simplefile-service/tests/plugin_archive.rs:115-123`: the fake `archive.extract` writes the listed `hello/world.txt` (11 bytes) plus an unlisted `stray.txt`.
- Create: `crates/simplefile-core/src/archive/tests/json_engine.rs`. Modify: `crates/simplefile-core/src/archive/tests.rs` (MockEngine moves to v2, add `mod json_engine;`, new tests).

**Interfaces:**
- Produces, in `simplefile_core::archive::engine` and re-exported from `archive`:
  ```rust
  pub const CANCELLED_MESSAGE: &str = "Archive operation cancelled";
  #[derive(Debug, Clone, PartialEq, Eq)]
  pub struct EngineProgress { pub current: u64, pub total: u64, pub current_item: Option<String> }
  pub struct EngineContext<'a> {
      pub operation_id: Option<&'a str>,
      pub cancel: &'a AtomicBool,
      pub progress: &'a (dyn Fn(EngineProgress) + Sync),
  }
  impl EngineContext<'static> { pub fn untracked() -> Self }   // no id, never cancelled, progress dropped
  impl EngineContext<'_> {
      pub fn is_cancelled(&self) -> bool;
      pub fn report(&self, current: u64, total: u64, current_item: Option<&str>);
  }
  #[derive(Debug, Clone, Copy, PartialEq, Eq)]
  pub struct EngineLimits { pub max_bytes: u64, pub max_entries: u32 }
  pub trait ArchiveEngine: Send + Sync {
      fn name(&self) -> &str;
      fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String>;
      /// Every entry into `destination`, an empty folder the archive layer owns.
      fn extract(&self, archive: &Path, destination: &Path, ctx: &EngineContext<'_>) -> Result<(), String>;
      /// `entry` (`/`-separated) and everything under it, at its own relative path inside the empty folder `destination`.
      fn read_entry(&self, archive: &Path, entry: &str, destination: &Path, limits: EngineLimits) -> Result<(), String>;
      /// A new archive at `archive`, which does not exist yet.
      fn create(&self, archive: &Path, paths: &[PathBuf], ctx: &EngineContext<'_>) -> Result<(), String>;
      /// Move `entry` and its descendants to the full path `new_path`. Changes the archive in place, atomically.
      fn rename(&self, archive: &Path, entry: &str, new_path: &str, ctx: &EngineContext<'_>) -> Result<(), String>;
      /// Remove each entry and its descendants, in place and atomically.
      fn delete(&self, archive: &Path, entries: &[String], ctx: &EngineContext<'_>) -> Result<(), String>;
      /// Store `base_dir/<p>` (a file or a tree) at `<p>` for each `/`-separated `p`, in place and atomically.
      fn add(&self, archive: &Path, base_dir: &Path, paths: &[String], ctx: &EngineContext<'_>) -> Result<(), String>;
  }
  pub fn extract_archive_with(archive_path: String, destination: String, ctx: &EngineContext<'_>) -> Result<(), String>;
  pub fn create_archive_with(paths: Vec<String>, archive_path: String, format: String, ctx: &EngineContext<'_>) -> Result<(), String>;
  // extract_archive / create_archive keep their signatures and call the *_with forms with EngineContext::untracked()
  ```
- Produces, crate-internal (used by Tasks 2 and 6):
  - `pub(super) enum ArchiveKind { BuiltIn(ArchiveFormat), Engine { engine: Arc<dyn ArchiveEngine>, format: EngineFormat } }`: `Clone`, with a manual `Debug` that prints the engine name and extension. It has `fn label(&self) -> String` and `fn entry_type(&self) -> String`. `entry_type` gives `"Zip"`, `"Tar"`, `"RAR"`, `"7-Zip"` or the engine name, which unsafe-name messages use.
  - `pub(super) fn archive_kind_for_path(path: &Path) -> Option<ArchiveKind>`: built-in first, then `engine_for_path`.
  - `pub(super) fn engine_entries(engine: &dyn ArchiveEngine, archive: &Path) -> Result<Vec<(EngineEntry, PathBuf)>, String>`: the listing with validated relative paths. Any unsafe name is an error, matching the old 7z fail-fast rule.
  - `pub(super) fn unique_engine_temp_path(archive: &Path, extension: &str) -> Result<PathBuf, String>`: `.{stem}.simplefile-new-{nonce}-{i}{extension}` beside `archive`.
  - `pub(super) struct ArchiveStaging` with `create(dest)` and `path()`. It is named `.sumafile-extract-<pid>-<nanos>` and is removed on drop. `move_entry(relative: &Path, to: &Path)` refuses a staged entry that is, or sits under, a symlink or junction, with error `Archive entry is a link: <entry>`. It also refuses a listed file the engine did not write: `<engine> did not extract <entry>`.
- Produces, wire shapes that `PipeArchiveEngine` sends (documented in Task 3):
  - `archive.extract {archivePath, destination, operationId?}`
  - `archive.read_entry {archivePath, innerPath, destination, maxBytes, maxEntries, operationId?}`
  - `archive.create {archivePath, paths, operationId?}`
  - `archive.rename {archivePath, innerPath, newPath, operationId?}`
  - `archive.delete {archivePath, innerPaths, operationId?}`
  - `archive.add {archivePath, baseDir, paths, operationId?}`
  - Every one of these replies `null`. `operationId` is sent when `ctx.operation_id` is `Some`.
- `JsonEngine` test double, in `tests/json_engine.rs`:
  - `name()` is `"json-engine"`.
  - The archive file holds `{"entries":[{"path":"docs/a.txt","dir":false,"data":"text"}]}`. Mutations rewrite the file through a temp file and a rename.
  - Knobs:
    - `stray_file`: extract also writes an unlisted `stray.txt`.
    - `junction_dir: Option<&str>`: creates that listed folder as a junction with `cmd /c mklink /J`.
    - `oversize_bytes: Option<u64>`: `read_entry` writes this many bytes.
    - `fail_with: Option<String>`: the next mutation fails.
  - `calls: Mutex<Vec<String>>` records every call as `"<method> <args>"`:
    - `"list"`, `"extract"`
    - `"read_entry docs 536870912 4096"`
    - `"create <n paths>"`
    - `"rename docs papers"`
    - `"delete a.txt,b.txt"`
    - `"add docs/new"`
  - `fn register_json_engine(owner: &'static str, extension: &str, can_modify: bool) -> (Arc<JsonEngine>, EngineRegistration)`.

- [ ] **Step 1: Write the failing core tests** in `tests.rs`. Each registers its own owner and extension, following the existing `EngineRegistration` drop-guard pattern.

```rust
#[test] fn engine_split_archive_path_parses_paths_inside_engine_archives()   // x.engsplit\docs\a.txt -> Some, inner "docs\\a.txt"; is_archive_virtual_path && is_archive_path true
#[test] fn engine_list_archive_directory_lists_root_and_nested_folders()     // root: docs (dir, implied) + top.txt; docs: a.txt; parent of docs == archive path
#[test] fn engine_full_extract_moves_only_listed_entries()                  // stray_file: dest has docs/a.txt, no stray.txt, no ".sumafile-extract-" left
#[test] fn engine_full_extract_refuses_unsafe_entry_names_before_writing()  // entry "../evil.txt" -> Err contains "unsafe path"; dest empty; calls has no "extract"
#[test] fn engine_full_extract_renames_an_existing_top_level_folder()       // dest already has docs/ -> files land in "docs (1)"
#[test] fn engine_full_extract_refuses_links_left_in_staging()              // junction_dir "docs" with listed docs/a.txt -> Err "Archive entry is a link: docs"
#[test] fn engine_read_entry_selects_by_segment_prefix()                    // docs/a.txt + docs2/b.txt; materialize x\docs -> calls contains "read_entry docs 536870912 4096"; only docs/a.txt
#[test] fn engine_read_entry_rejects_a_selection_over_limits_before_calling_the_engine() // max_entries 1, two files -> Err "Archive extract exceeds entry limit of 1 entries"
#[test] fn engine_read_entry_removes_output_over_the_byte_limit()           // oversize_bytes 64, max_bytes 10 -> Err "Archive extract exceeds size limit of 10 bytes"; no output file
#[test] fn engine_copy_entry_out_to_a_local_folder()                        // copy_entry_resolved(x\docs\a.txt, local, "error") -> local\a.txt == "text"
#[test] fn engine_extract_with_a_cancelled_context_stops()                  // cancel=true -> Err(CANCELLED_MESSAGE); no staging left
#[test] fn built_in_extract_with_a_cancelled_context_does_nothing()         // zip, cancel=true -> Err(CANCELLED_MESSAGE); dest empty
#[test] fn engine_mutation_is_refused_until_engines_can_modify()            // create_archive_directory on x.engmut -> Err "json-engine cannot modify .engmut archives" (replaced in Task 2)
```

Also update `engine_extract_and_create_forward_to_engine`: extract now receives a staging folder inside `destination`.

- [ ] **Step 2: Run them to verify they fail**

Run: `cargo test --locked -p simplefile-core engine_ -- --nocapture`
Expected: compile FAIL (`EngineContext` and `extract_archive_with` not found).

- [ ] **Step 3: Implement trait v2, `ArchiveKind`, engine listing, extract and read in core**
  - `split_archive_path` resolves file components with `archive_kind_for_path`. `is_archive_path` becomes the split check alone.
  - Engine full extract runs: list and validate, then `top_level_remap`, then `ArchiveStaging` inside the destination, then `engine.extract(archive, staging, ctx)`. After that, each listed entry is moved with `ensure_extract_path_within_destination` and `unique_file_path_if_needed`.
  - The engine arm of `extract_archive_entry_to_directory` selects with `path_is_within_prefix` on validated relative paths. It pre-checks with `reject_if_over_budget`, calls `read_entry` into staging, moves the selection, then post-checks the written bytes.
  - Built-in arms ignore `ctx` except for one `is_cancelled()` check before any work.

- [ ] **Step 4: Move `PipeArchiveEngine` to v2 and update the fake plugin test**

Make `registered_plugin_engine_lists_and_extracts_hello_archives` assert two things: `hello/world.txt` (11 bytes) lands in the destination, and `stray.txt` does not.

- [ ] **Step 5: Run the tests**

Run: `cargo test --locked -p simplefile-core` then `cargo test --locked -p simplefile-service --test plugin_archive` then `cargo clippy --locked --all-targets --all-features -- -D warnings`
Expected: all `test result: ok`, clippy clean.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`. It prints a tree id; nothing is committed.

---

### Task 2: Core engine mutations

**Files:**
- Create: `crates/simplefile-core/src/archive/mutate_engine.rs`
- Modify: `crates/simplefile-core/src/archive/mutate.rs`: dispatch every public entry point on `parsed.kind`. Built-in kinds keep `mutate_archive` unchanged; engine kinds call `mutate_engine`.
- Modify: `crates/simplefile-core/src/archive/create.rs:32-51`: an engine create writes to `unique_engine_temp_path`, then renames into place.
- Modify: `crates/simplefile-core/src/archive/mod.rs`: export `delete_archive_entries`.
- Modify: `crates/simplefile-core/src/file_ops.rs:240-255`: `move_to_trash` sends every archive virtual path through `delete_archive_entries`.
- Test: `crates/simplefile-core/src/archive/tests.rs`

**Interfaces:**
- Consumes: Task 1's `ArchiveKind`, `engine_entries`, `unique_engine_temp_path`, `ArchiveStaging`, trait v2, `JsonEngine`.
- Produces:
  - `pub fn delete_archive_entries(paths: &[String]) -> Vec<String>` returns the error messages; empty means everything was deleted.
    - It groups paths by archive. An engine archive gets one `engine.delete` per archive with all of its inner paths.
    - Any path missing from the fresh listing fails its group with `Archive entry not found: <path>` before the engine is called.
    - Built-in archives keep one delete per path.
  - Engine semantics behind the existing public functions. Each lists first; core resolves names and conflicts; the engine changes the archive in place.
    - `create_archive_directory` and `create_archive_file`: stage an empty folder or empty file at `<work>/<inner>/<name>` in a `WorkRootGuard`, then call `engine.add(archive, work, ["<inner>/<name>"])`. They keep the built-in messages `Directory already exists: {name}` and `File already exists: {name}`.
    - `rename_archive_entry`: calls `engine.rename(archive, inner, "<parent>/<new_name>")`. The same name is a no-op that returns the path with no engine call. A taken sibling fails with `A file or directory with that name already exists: {new_name}`.
    - `delete_archive_entry`: one `engine.delete`.
    - Copy into an archive, from a local path or from an entry another archive materialized:
      - Resolve against the listing. `skip` returns `SKIPPED:<source>`.
      - `replace` calls `engine.delete` on the existing path first.
      - `rename` and `keep-both` use `stem (i).ext`.
      - Anything else fails with `CONFLICT: destination already exists: <archive>\<inner path>`.
      - The source is copied into `<work>/<final inner path>` (`copy_path_to_destination`, which refuses symlinks), then `engine.add` is called.
    - Move within one archive: one `engine.rename` to the full destination path, after the same conflict rules.
    - Move into an archive: copy in, then remove the local source.
    - Copy or move between archives: `read_entry` (materialize), then add. Moving out deletes from the source.
    - `ensure_archive_can_modify`: an engine kind needs `format.can_modify`. Otherwise it fails with `{engine name} cannot modify {extension} archives`.

- [ ] **Step 1: Write the failing tests**

```rust
#[test] fn engine_create_directory_and_file_add_staged_entries()      // calls has "add docs/new" and "add docs/n.txt"; JSON lists both; repeats -> "Directory already exists: new" / "File already exists: n.txt"
#[test] fn engine_rename_entry_moves_descendants_and_refuses_taken_names() // docs -> papers: "rename docs papers", listing has papers/a.txt; onto top.txt -> exact conflict text; same name -> no call
#[test] fn engine_delete_entry_calls_the_engine_once()               // "delete docs"; missing -> "Archive entry not found: <archive>\\nope"
#[test] fn engine_multi_delete_issues_one_engine_call_per_archive()  // file_ops::move_to_trash(3 paths in x.engdel) -> one "delete a.txt,b.txt,docs"
#[test] fn engine_copy_into_archive_resolves_conflicts()             // skip -> "SKIPPED:<src>"; replace -> calls ["delete docs/a.txt","add docs/a.txt"]; rename -> stored "docs/a (1).txt"; "error" -> Err starts "CONFLICT: destination already exists: "
#[test] fn engine_move_within_an_archive_renames_with_a_full_path()   // move x\docs\a.txt -> x\papers : "rename docs/a.txt papers/a.txt"
#[test] fn engine_move_into_an_archive_removes_the_local_source()
#[test] fn engine_copy_between_archives_reads_then_adds()             // x.engjson -> y.engjson: x read_entry, y add
#[test] fn engine_mutation_requires_can_modify()                      // can_modify false -> "json-engine cannot modify .engro archives"; no call
#[test] fn engine_create_archive_writes_a_temp_file_then_renames()     // success: target exists, no ".simplefile-new-" sibling; engine failure: no target, no temp
#[test] fn engine_mutation_failure_is_reported_unchanged()            // fail_with "boom" on delete -> Err contains "boom"
#[test] fn engine_batch_rename_inside_an_archive_is_refused_before_any_engine_call() // file_ops::batch_rename of x.engbr\a.txt and x.engbr\b.txt -> Err "Path does not exist: ..." as today for every format; calls empty; archive bytes unchanged
```

Delete `engine_mutation_is_refused_until_engines_can_modify` from Task 1.

- [ ] **Step 2: Run to verify they fail**

Run: `cargo test --locked -p simplefile-core engine_`
Expected: FAIL (the engine mutations still error with "cannot modify").

- [ ] **Step 3: Implement `mutate_engine.rs`, the dispatch in `mutate.rs`, the temp-then-rename create and `delete_archive_entries`**

- [ ] **Step 4: Run the core and service suites**

Run: `cargo test --locked -p simplefile-core` and `cargo test --locked -p simplefile-service`
Expected: `test result: ok` for both.

- [ ] **Step 5: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 3: Plugin protocol additions: helper crate, protocol doc and the Hello fixture

**Files:**
- Modify: `crates/sumafile-plugin/src/host.rs`, `src/server.rs`, `src/lib.rs`
- Test: `crates/sumafile-plugin/tests/roundtrip.rs`
- Modify: `crates/hello-plugin-backend/src/main.rs`. Test: its unit tests and `tests/process.rs`.
- Modify: `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/plugin.json:17`: `canModify` becomes `true`.
- Modify: `ipc/schema/plugin/v1/protocol.json`, `ipc/schema/plugin/README.md`, `scripts/check-ipc-schema.mjs:357-401`.
- Modify: spec §6.2. Clients send `cancel`, whether the host or the service. The flag is visible to every connection of the backend while the request with that operation id runs.
- Modify: spec §6.4, in two places:
  - It gets an "Engine protocol" paragraph with the semantics below.
  - It records a decision that replaces "The built-in formats implement it in-process": the built-in formats keep their in-core code paths (`ArchiveKind::BuiltIn`) and do not implement `ArchiveEngine`. The trait is the plugin contract only.

**Interfaces:**
- Produces, crate `sumafile-plugin`:
  ```rust
  // lib.rs
  pub const ARCHIVE_PROGRESS_NOTIFICATION: &str = "archive.progress";
  pub const CANCELLED_MESSAGE: &str = "Archive operation cancelled";
  pub const EARLY_CANCEL_LIMIT: usize = 64;
  impl HostClient {
      /// Queues archive.progress {operationId, current, total, currentItem?} to this connection's client; allowed on the service connection; never waits.
      pub fn archive_progress(&self, operation_id: &str, current: u64, total: u64, current_item: Option<&str>) -> Result<(), String>;
      /// The client of the connected host (handshake without client "simplefile-service"), so a service-connection handler can read settings.
      pub fn host_connection(&self) -> Option<HostClient>;
      /// Cancel flags plus early cancels the server holds right now; for tests.
      #[doc(hidden)] pub fn cancel_flag_count(&self) -> usize;
  }
  #[cfg(feature = "test-support")] impl HostClient {
      pub fn fake_cancel(&self, operation_id: &str);
      pub fn fake_progress(&self) -> Vec<(String, u64, u64, Option<String>)>;
  }
  ```
  - Cancel flags move from each `PipeHost` into one server-wide shared state. A `cancel` on any connection is visible through `is_cancelled` on every connection. The same shared state records the live host connection for `host_connection`.
  - The server owns the flags' lifetime, so handlers never clear a flag:
    - Before it spawns a handler, the server records the request's `params.operationId` (when it is a string) as in flight. When the handler returns, it drops that id and its flag.
    - A `cancel` for an in-flight id sets its flag.
    - A `cancel` for any other id goes on a list of the `EARLY_CANCEL_LIMIT` most recent early cancels; the oldest falls off. "Any other id" covers an unknown id, a finished one, and one whose request has not yet been read on another connection.
    - A request whose id is on that list starts cancelled and takes the id off the list.
    - The shared state therefore never holds more than the in-flight ids plus 64.
  - On a fake host, `host_connection()` returns the fake itself.
- Produces, in protocol `ipc/schema/plugin/v1/protocol.json`. `archiveEngineMethods` becomes an object keyed by method name:
  ```json
  "commandLine": ["--pipe-name <name>", "--data-dir <dir>"],
  "archiveEngineMethods": {
    "archive.list":       { "params": { "archivePath": "string" }, "result": { "entries": "[{path,isDir,size,compressedSize}]" }, "deadline": "list" },
    "archive.extract":    { "params": { "archivePath": "string", "destination": "string", "operationId": "string?" }, "result": "null", "deadline": "transfer" },
    "archive.read_entry": { "params": { "archivePath": "string", "innerPath": "string", "destination": "string", "maxBytes": "number", "maxEntries": "number", "operationId": "string?" }, "result": "null", "deadline": "read" },
    "archive.create":     { "params": { "archivePath": "string", "paths": "string[]", "operationId": "string?" }, "result": "null", "deadline": "transfer" },
    "archive.rename":     { "params": { "archivePath": "string", "innerPath": "string", "newPath": "string", "operationId": "string?" }, "result": "null", "deadline": "transfer" },
    "archive.delete":     { "params": { "archivePath": "string", "innerPaths": "string[]", "operationId": "string?" }, "result": "null", "deadline": "transfer" },
    "archive.add":        { "params": { "archivePath": "string", "baseDir": "string", "paths": "string[]", "operationId": "string?" }, "result": "null", "deadline": "transfer" }
  },
  "notifications": { "archive.progress": { "direction": "backend to service", "params": { "operationId": "string", "current": "number", "total": "number", "currentItem": "string?" } } },
  "deadlines": { "handshake": "5 s", "list": "30 s", "read": "120 s idle", "transfer": "600 s idle", "cancel": "5 s" },
  "errors": { "-32600": "request before plugin.handshake", "-32601": "method not found", "-32602": "invalid params", "-32603": "handler panicked", "-32000": "application error (\"Archive operation cancelled\" after a cancel)" },
  "cancel": { "method": "cancel", "params": ["operationId"], "cancelledMessage": "Archive operation cancelled", "notes": "Sent by a client (host or service) as a request or a notification on any of its connections; visible to every connection of the backend while the request with that operationId runs. A cancel that arrives before its request is kept among the 64 most recent early cancels." }
  ```
- Semantics, documented in the README and spec §6.4:
  - Entry paths are `/`-separated and relative. `innerPath` matches the entry and its descendants by whole segments, so `docs` never matches `docs2`.
  - `destination` for `extract` and `read_entry` is an empty folder the service created. Engines must refuse a non-empty destination and must never write outside it.
  - `read_entry` refuses a selection over `maxEntries` or `maxBytes` by declared sizes. It fails with `Archive entry not found: <innerPath>` when nothing matches.
  - `create` makes a new file and must fail rather than append.
  - `rename`, `delete` and `add` change the archive in place and atomically. On failure or cancel the archive is left as it was.
  - `add` stores `baseDir/<p>` at `<p>`. The caller guarantees that nothing but parent folders exists at those paths.
  - `archive.progress` carries the request's `operationId`, with `current <= total`; `total` 0 means unknown. Backends should send it at least every 5 s while working, `read_entry` included: reading one entry of a solid archive can mean decompressing a whole block first. Each one resets the caller's idle deadline.
  - The README fixes "either side sends cancel": only clients do. The README and spec §6.2 also state how long a flag lives (see above).

- [ ] **Step 1: Write the failing helper-crate tests** in `roundtrip.rs`. Extend `EchoHandler` with `slow_progress`, `settings_via_host` and `cancel_flags`, which returns `host.cancel_flag_count()`.

```rust
#[tokio::test] async fn cancel_on_any_connection_is_seen_by_every_connection()      // replaces cancel_sets_a_per_connection_flag: cancel on host conn -> check_cancel true on the service conn
#[tokio::test] async fn cancel_flag_is_dropped_when_its_request_finishes()          // slow_progress op-1, cancel -> CANCELLED reply; then cancel_flags == 0
#[tokio::test] async fn cancel_that_overtakes_its_request_still_cancels_it()        // cancel op-2 on conn B first, then slow_progress op-2 on conn A -> CANCELLED at once
#[tokio::test] async fn cancels_for_unknown_or_finished_ids_are_bounded()           // 100 cancels for unknown ids plus one for a finished id -> cancel_flags <= 64
#[tokio::test] async fn archive_progress_is_sent_on_the_service_connection()        // service-mode client reads {"method":"archive.progress","params":{"operationId":"op-1","current":1,"total":2,"currentItem":"a"}} before the reply
#[tokio::test] async fn host_connection_reaches_the_host_from_a_service_connection_handler() // service conn calls settings_via_host; host test client answers host.get_settings {"values":{"k":"v"}} -> result "v"
#[tokio::test] async fn host_connection_is_none_without_a_host_client()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `cargo test --locked -p sumafile-plugin`
Expected: compile FAIL (`archive_progress`, `host_connection` and `cancel_flag_count` not found).

- [ ] **Step 3: Implement the helper-crate changes.** Run the same command; expected `test result: ok`.

- [ ] **Step 4: Write the failing Hello tests.** These are unit tests in `main.rs` using `HostClient::fake`.
  - A listing line whose path ends in `/` is a folder (`isDir: true`; the reported path has no trailing slash).
  - An entry whose last segment is `__slow__`, with size N, makes the backend sleep N ms in 50 ms steps:
    - `extract` and `add` sleep when the archive holds such an entry;
    - `read_entry` sleeps when its selection includes one.
    - Each step calls `archive_progress(operationId, elapsed_ms, N, Some("__slow__"))` and returns `CANCELLED_MESSAGE` once `is_cancelled(operationId)`.
  - Every mutation rewrites the listing through `<archive>.tmp` and a rename.

```rust
#[test] fn read_entry_extracts_a_segment_prefix_only()          // docs/a.txt, docs2/b.txt, innerPath "docs" -> only docs/a.txt
#[test] fn read_entry_rejects_a_selection_over_limits()         // maxEntries 1 -> "Archive extract exceeds entry limit of 1 entries"
#[test] fn rename_rewrites_the_entry_and_its_descendants()
#[test] fn delete_drops_the_entry_and_its_descendants()
#[test] fn add_stores_files_and_empty_folders_at_relative_paths() // "docs/new/" line for an empty folder
#[test] fn create_refuses_an_existing_archive()
#[test] fn extract_refuses_a_non_empty_destination()
#[tokio::test] async fn slow_extract_reports_progress_and_honours_cancel() // fake_cancel after first fake_progress -> Err(Application(CANCELLED_MESSAGE)) within 1 s
#[tokio::test] async fn slow_read_entry_reports_progress()      // docs/__slow__ 200, innerPath "docs", operationId "op-r" -> fake_progress has entries for "op-r"
```

In `tests/process.rs`, add `backend_process_serves_the_v2_archive_methods`. It handshakes as the service, then runs add, list, rename, delete and read_entry against the real exe.

- [ ] **Step 5: Run to verify they fail, implement, run again**

Run: `cargo test --locked -p hello-plugin-backend`
Expected: FAIL, then `test result: ok`.

- [ ] **Step 6: Protocol doc and schema check**
  - Write the protocol file and the README.
  - Extend `check-ipc-schema.mjs`:
    - Every key of `archiveEngineMethods` must be a quoted string literal in `crates/simplefile-service/src/plugin_archive.rs` and in `crates/hello-plugin-backend/src/main.rs`.
    - `"archive.progress"` must be a literal in `crates/sumafile-plugin/src/host.rs`. Task 4 also requires it in the service, its consumer.
    - `cancel.cancelledMessage` must be a quoted literal in `crates/simplefile-core/src/archive/engine.rs` and `crates/sumafile-plugin/src/lib.rs`. Task 10 adds the C# constant to the list.
    - `pub const SDK_VERSION: &str = "<v>"` in `crates/sumafile-plugin/src/lib.rs` and `pub const PLUGIN_SDK_VERSION: &str = "<v>"` in `plugin_archive.rs` must both equal `sdkVersion`.

Run: `npm run check:ipc-schema`
Expected: `Checked plugin protocol v1: 8 host methods, 7 archive engine methods, SDK 1.0.0.`

- [ ] **Step 7: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 4: Service: pooled plugin connections, progress, cancel and tracked archive operations

**Files:**
- Replace: `crates/simplefile-service/src/plugin_archive.rs` with `plugin_archive/mod.rs`, `connection.rs` and `pool.rs`. The public paths stay the same, so `crates/simplefile-service/src/lib.rs:5` is unchanged.
- Modify: `crates/simplefile-service/src/dispatch/{handlers.rs:390-495,587-605,720-787,830-838, params.rs:323-360, mod.rs:162-190, tests.rs:273-355}` and `session/{mod.rs:118-163, jobs.rs, tests.rs}`.
- Modify: `ipc/schema/v1/commands.json:139-143`: `"operationId": "string?"` and `"progressEvent": "operation-progress"` on `extract_archive` and `create_archive`.
- Modify: `ipc/schema/v1/events.json:13-18`: in the `operation-progress` `source` list, replace the stale `crates/simplefile-service/src/session.rs` with `crates/simplefile-service/src/session/jobs.rs`. The schema check does not read `source`, so this only keeps the documentation true.
- Modify: `scripts/generate-ipc-bindings.mjs:220-221`, then regenerate `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.Generated.cs`.
- Modify: `scripts/check-ipc-schema.mjs`:
  - The archive-method literals and `PLUGIN_SDK_VERSION` that Task 3 checks in `plugin_archive.rs` now live in `crates/simplefile-service/src/plugin_archive/mod.rs`.
  - `"archive.progress"` must also be a literal in `crates/simplefile-service/src/plugin_archive/connection.rs`, the service's consumer.
- Modify: `src-winui/SimpleFile.Ipc/ISimpleFileIpc.cs:89-90`, `src-winui/SimpleFile.Core/FileOperationService.cs:588-600` (pass `operationId: null`), and `src-winui/SimpleFile.Tests/{ConfigurableIpc.cs:43-44,290-298, NullIpc.cs:173-177, FileOperationServiceTests.cs, NamedPipeJsonClientTests.cs}`.
- Create: `crates/simplefile-service/tests/support/mod.rs` and `tests/hello_plugin.rs`.
- Modify: `tests/plugin_archive.rs`. It declares `mod support;` and uses the harness, `REGISTRY_LOCK` and `Unregister` from there instead of its own copies.
- Modify: `docs/winui-migration/parity-gate.md:139-142`. Extract and create report `operation-progress`, and the register row's WinUI caller is `ArchiveEngineRegistrar`.
- Modify: spec §6.4: the pool, the heartbeat and relay, the registration ticket, and the rule that engine-reaching calls run off the session loop.

**Interfaces:**
- Consumes: Task 1 trait v2 and `CANCELLED_MESSAGE`; Task 3 wire shapes, `archive.progress` and backend-wide cancel.
- Produces, `simplefile_service::plugin_archive`:
  ```rust
  pub struct PluginTimeouts { pub handshake: Duration, pub list: Duration, pub read: Duration, pub transfer: Duration, pub cancel: Duration, pub lock: Duration, pub max_connections: usize }
  // Default: 5 s, 30 s, 120 s (idle), 600 s (idle), 5 s, 30 s, 8
  pub fn is_valid_plugin_id(id: &str) -> bool;                 // ^[a-z0-9][a-z0-9.-]{1,63}$
  impl PluginConnection {
      pub fn connect(plugin_id: &str, pipe_name: &str) -> Result<Arc<Self>, String>;
      pub fn connect_with_timeouts(plugin_id: &str, pipe_name: &str, timeouts: PluginTimeouts) -> Result<Arc<Self>, String>;
      pub fn call(&self, method: &str, params: Value) -> Result<Value, String>;
      pub fn call_tracked(&self, method: &str, params: Value, operation_id: &str, cancel: &AtomicBool, progress: &(dyn Fn(EngineProgress) + Sync)) -> Result<Value, String>;
      pub fn open_connections(&self) -> usize;
  }
  ```
  - The deadline class comes from the method:
    - `plugin.handshake` uses handshake.
    - `archive.list` uses list.
    - `cancel` uses cancel.
    - `archive.read_entry` uses read.
    - every other `archive.*` method uses transfer.
  - The read and transfer classes are idle deadlines. Any `archive.progress` for the call's `operationId` resets them. Other notifications are dropped.
  - Pool behaviour:
    - A call takes an idle connection, or opens one while fewer than `max_connections` are open. Otherwise it waits up to `lock`, then fails with `plugin {id} is busy`.
    - A transport error or timeout closes that connection and discards every idle one. The next call reconnects.
    - `PipeArchiveEngine` gives every read- and transfer-class call an `operationId`: `ctx.operation_id`, or `svc-<pid>-<n>` from a process-wide counter.
  - Cancel behaviour:
    - While a tracked call waits, it polls `cancel` every 50 ms.
    - The first time the flag is set, it sends `cancel {operationId}` as a request on another connection: an idle one, or a new one even beyond the limit.
    - It then waits at most `cancel` more for the reply, closes the connection if none arrives, and returns `CANCELLED_MESSAGE`.
  - `Connection::open` checks the handshake reply. A different `pluginId` fails with `Plugin pipe {pipe} answered as {got}, expected {id}`. An SDK major other than 1 also fails.
  - Registration tickets, so the last register or unregister the service received for a plugin wins whatever order the work finishes in:
    ```rust
    pub struct RegistrationTicket { /* plugin id and sequence number */ }
    pub fn registration_ticket(plugin_id: &str) -> RegistrationTicket;   // advances the plugin's sequence
    pub fn register_plugin_engine(ticket: RegistrationTicket, pipe_name: &str, formats: Vec<PluginArchiveFormat>) -> Result<(), String>;
    pub fn unregister_plugin_engine(plugin_id: &str);                     // advances the sequence and unregisters, both under the ticket lock
    ```
    - The `register_archive_engine` handler takes its ticket on the session loop, before it defers the connect to the General lane. `unregister_archive_engine` stays inline. Both therefore advance the sequence in the order the host sent them.
    - `register_plugin_engine` connects and handshakes, then commits while holding the ticket lock, and only if its ticket is still the latest. Otherwise it changes nothing and fails with `Registration of plugin {id} was superseded by a later registration change`.
    - A register the host timed out may still be queued or connecting. It can never land after the host's unregister or after a newer register.
- Produces, dispatch and session:
  - `extract_archive` and `create_archive` with `operationId` dispatch as `Dispatch::ArchiveOperation { id, operation_id, operation: ArchiveOperation::{Extract{archive_path, destination}, Create{paths, archive_path, format}} }`.
  - The session registers the cancel token in `operations` before it spawns. `cancel_operation` therefore works.
  - It runs `extract_archive_with` or `create_archive_with` on the Transfer lane and emits `operation-progress`:
    - `operation_type` is `"extract-archive"` or `"create-archive"`.
    - `current` and `total` are engine bytes; `current_files` and `total_files` are 0.
    - First comes `status: "running"` (0/0), then one event per engine progress, then a final event with `"completed"`, `"cancelled"` or `"error"` (with `error`).
  - The reply is `null`. After a cancel it is the application error `Archive operation cancelled`. The cache is invalidated as today.
  - Without `operationId` the behaviour is as today.
  - Every handler that can reach an engine leaves the session loop. A plugin's `host.get_settings` reaches the host, which answers it with `get_db_settings` on this same service connection. On the session loop, that lookup would wait behind the very call it serves.
    - `create_directory`, `create_file` and `rename_entry` move to `reply_blocking(…, BlockingLane::General, …)` when `archive::is_archive_path(&p.path)`.
    - `open_file`, `reveal_in_folder` and `open_file_with` move there when `archive::is_archive_virtual_path(&p.path)`, because they materialize the entry through `read_entry`.
    - Local paths stay inline.
    - The lane is General, like `delete_entry` and `move_to_trash`, so a new folder or a rename never queues behind the two Transfer permits that long extracts hold.
    - `batch_rename` stays inline. It refuses archive entries for every format before any engine call (`validate_path_no_follow` fails), as today.
  - `register_archive_engine` rejects a `pluginId` that fails `is_valid_plugin_id` with `ERR_INVALID_PARAMS` "invalid params: pluginId must match ^[a-z0-9][a-z0-9.-]{1,63}$". Then it takes its registration ticket.
- Produces, C# bindings:
  - `Task ExtractArchiveAsync(string archivePath, string destination, string? operationId = null, CancellationToken ct = default)`
  - `Task CreateArchiveAsync(string[] paths, string archivePath, string format, string? operationId = null, CancellationToken ct = default)`
  - Both are in `ISimpleFileIpc` and the generated client. The anonymous params gain `operationId`.
  - `ConfigurableIpc.ExtractArchiveHandler` becomes `Func<string, string, string?, CancellationToken, Task>?` and `CreateArchiveHandler` becomes `Func<string[], string, string, string?, CancellationToken, Task>?`.
- Produces, `tests/support/mod.rs`. It starts with `#![allow(dead_code)]`, because each test binary uses a different part of it.
  - `pub static REGISTRY_LOCK: tokio::sync::Mutex<()>` and `Unregister(String)`, both moved from `tests/plugin_archive.rs`.
    - Every test in `plugin_archive.rs`, `hello_plugin.rs` and `sevenzip_plugin.rs` takes the lock first.
    - The engine registry is per process, and those tests share `.hello` or `.7z`. Without the lock, a later registration would route one test's calls to another test's backend, which dies when that test ends.
  - `Service`, moved from `plugin_archive.rs` and extended, is `Clone`:
    - It offers `start()`, `call(method, params) -> Value`, `send(method, params) -> u64`, `reply(id) -> Value`, `progress_events(operation_id) -> Vec<Value>` and `stop()`.
    - One reader task routes replies to their waiters by id and collects every notification, so several tasks can call it at once.
    - The first `start()` in a process points `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` at a temp folder (`std::sync::Once`), so `get_db_settings` never touches the user's database.
  - `built_exe(package: &str) -> PathBuf` resolves `<CARGO_TARGET_TMPDIR>/../<debug|release>/<package>.exe`. When the exe is missing it panics with "build it with `cargo build --locked -p <package>` (the workspace `cargo test` builds it)".
  - `launch_plugin(exe: &Path, extra_files: &[PathBuf]) -> PluginProcess`:
    - It copies the exe and the extra files into a temp plugin folder, then launches with `--pipe-name` and `--data-dir`.
    - It waits by retrying a client connect every 10 ms for up to 5 s. It keeps that client open, never handshaken, for the `PluginProcess` lifetime.
    - The keep-alive matters because a backend exits when its last client disconnects, and the pool may drop every connection after an error. In production the host connection plays this role.
    - A client that never handshakes never becomes `host_connection()`, so `binary_for` does not wait for settings.
    - `PluginProcess` has a `pipe_name` field and kills the child on drop.
  - `FakeHost::connect(pipe_name: &str, service: Service) -> FakeHost`:
    - It handshakes without `client`, so it becomes the backend's host connection.
    - It answers each `host.get_settings` the way the C# host does: it calls `get_db_settings` with the `plugin.<id>.<key>` names on `service`, the same connection the test drives.
    - It answers `host.log` with `null` and records every method it served.

- [ ] **Step 1: Write the failing service tests**

In `tests/hello_plugin.rs`, using the real Hello exe. Every test takes `REGISTRY_LOCK` first:

```rust
#[tokio::test(flavor = "multi_thread", worker_threads = 4)] async fn hello_engine_serves_browse_read_and_mutation_through_the_service()
    // register .hello all four true; list_directory root + nested; read_file_preview on x.hello\docs\a.txt; create_directory; create_file;
    // rename_entry; copy_entry_resolved in and out; move_entry_resolved; delete_entry -> each followed by list_archive showing the change;
    // get_folder_metrics and count_folder_items on x.hello\docs -> item count and size from the listing; get_file_metadata on x.hello\docs\a.txt;
    // compare_files of x.hello\docs\a.txt and a local copy -> identical; a two-entry batch_rename inside x.hello -> error "Path does not exist",
    // no archive.rename reached the backend, listing unchanged
#[tokio::test(...)] async fn open_file_on_an_archive_entry_does_not_block_the_session_loop()
    // Review Focus 6. x.hello holds docs/a.txt and docs/__slow__ (2000). open_file(x.hello\docs), a folder so nothing is launched,
    // then get_archive_capabilities: the capabilities reply arrives first; open_file then fails with "Cannot open a directory as a file"
#[tokio::test(...)] async fn slow_read_entry_progress_extends_the_read_deadline()
    // PluginConnection::connect_with_timeouts with read 500 ms; PipeArchiveEngine::read_entry of "docs" with docs/__slow__ (1500) -> Ok after about 1.5 s
#[tokio::test(...)] async fn tracked_extract_relays_plugin_progress_as_operation_progress()
    // archive with "__slow__\t300": events for "op-progress" start "running", contain current>0 total=300, end "completed"
#[tokio::test(...)] async fn cancel_operation_stops_a_slow_plugin_extract()
    // "__slow__\t20000"; after first progress cancel_operation -> true; reply error "Archive operation cancelled" within 5 s; last event "cancelled"; destination has no ".sumafile-extract-"
#[tokio::test(...)] async fn concurrent_list_is_answered_while_a_slow_extract_runs()   // Review Focus 1: list_archive on b.hello replies within 5 s during a "__slow__\t20000" extract
#[tokio::test(...)] async fn archive_mutation_does_not_block_the_session_loop()         // create_directory in a "__slow__\t2000" archive; get_archive_capabilities replies first
```

In `tests/plugin_archive.rs`, using the fake plugin:
- Its handshake reply gains `"pluginId"`.
- It accepts a `transfer`-idle progress script and answers `archive.read_entry` after a scripted delay.
- On a pipe whose name contains `slow-handshake`, it answers the handshake after 1 s.

```rust
#[tokio::test(...)] async fn handshake_reply_with_another_plugin_id_is_refused()
#[tokio::test(...)] async fn read_entry_without_progress_times_out_at_the_read_deadline()      // read 500 ms; the fake answers archive.read_entry after 1.5 s with no progress -> "did not answer archive.read_entry within 500ms"
#[tokio::test(...)] async fn register_that_completes_after_a_later_unregister_registers_nothing() // slow-handshake pipe: send register, then unregister -> the register reply is the superseded error; capabilities have no .hello row
#[tokio::test(...)] async fn second_caller_gets_its_own_connection_when_the_pool_allows() // max_connections 2: second list answered while the first hangs; open_connections() == 2
#[tokio::test(...)] async fn progress_notifications_extend_the_transfer_deadline()       // transfer 500 ms idle; fake sends archive.progress every 100 ms for 1.5 s then replies -> Ok
#[tokio::test(...)] async fn untracked_transfer_calls_carry_a_service_operation_id()     // fake records operationId starting "svc-"
```

Keep `second_caller_on_a_busy_plugin_gets_a_busy_error_within_its_bound` with `max_connections: 1`.

In `dispatch/tests.rs`, `session/tests.rs` (whose harness already reads `operation-progress` notifications, see `:155`) and the `plugin_archive/mod.rs` unit tests:

```rust
#[test] fn register_archive_engine_rejects_invalid_plugin_ids()                 // dispatch/tests.rs: "Bad.Id", "a", "x/y" -> -32602
#[test] fn archive_entry_shell_calls_and_mutations_run_on_the_general_lane()   // dispatch/tests.rs: open_file, reveal_in_folder, open_file_with, create_directory, create_file and rename_entry on paths inside x.zip
                                                                                // -> Dispatch::Blocking with lane General; the jobs are dropped unrun, so nothing is launched; open_file on a missing local path -> Dispatch::Reply
#[tokio::test] async fn extract_archive_with_operation_id_emits_running_then_completed_for_zip() // session/tests.rs
#[test] fn timeouts_by_method_follow_the_phase_2_table()                        // plugin_archive/mod.rs: defaults and for_method classes
```

- [ ] **Step 2: Run them to verify they fail**

Run: `cargo test --locked -p simplefile-service`
Expected: compile FAIL (`max_connections`, `call_tracked`, `registration_ticket`, `FakeHost` and `Dispatch::ArchiveOperation` missing).

- [ ] **Step 3: Implement the module split, the pool, progress and cancel, then the dispatch and session changes**

- [ ] **Step 4: Schema and bindings**
  - Edit `commands.json`, `events.json` and the two wrapper strings in `generate-ipc-bindings.mjs`.
  - Run `npm run generate:ipc-bindings`.
  - Update the C# callers and test doubles listed above.
  - Extend `NamedPipeJsonClientTests.ArchiveMethods_UseContractNamesAndCasing`: `ExtractArchiveAsync(@"C:\pack.zip", @"C:\out", operationId: "archive-1")` sends `operationId == "archive-1"`.
  - Update `FileOperationServiceTests.ArchiveMethods_CallTypedIpc`: the untracked calls pass `operationId` null.

Run: `npm run check:ipc-generated && npm run check:ipc-schema && npm run check:winui-parity-gate`
Expected: all succeed, and the schema check prints `99 domain methods`.

- [ ] **Step 5: Run every suite this task touches**

Run: `cargo test --locked --all-features` then `cargo clippy --locked --all-targets --all-features -- -D warnings` then `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`
Expected: PASS everywhere, clippy clean.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 5: The 7-Zip plugin backend and manifest

**Files:**
- Create: `plugins/sevenzip/plugin.json`.
- Create: `plugins/sevenzip/backend/Cargo.toml`: `publish = false`, version `1.0.0`, `[lib]` plus a `[[bin]]` named `sumafile-sevenzip-backend`. Dependencies: `async-trait`, `serde_json`, `sumafile-plugin`, `tokio` (macros, rt-multi-thread, time). Dev-dependencies: `sumafile-plugin` with `test-support`, `simplefile-ipc`, `tempfile`, `tokio`.
- Create: `plugins/sevenzip/backend/src/{lib.rs, binary.rs, entries.rs, run.rs, ops.rs, handler.rs, main.rs}` and `tests/{real_7za.rs, process.rs}`.
- Modify: root `Cargo.toml` (member `plugins/sevenzip/backend`) and `Cargo.lock`.
- Modify: `scripts/check-ipc-schema.mjs`: add `plugins/sevenzip/backend/src/handler.rs` to the archive-method servers.
- Test: `src-winui/SimpleFile.Tests/PluginManifestReaderTests.cs`. The real manifest must parse with the host's reader, so its errors show up here and not first in the Task 13 smoke.

**Interfaces:**
- Consumes: Task 3 `HostClient::{archive_progress, host_connection, is_cancelled}` and `CANCELLED_MESSAGE`, the server-owned flag lifetime, and the protocol semantics.
- `plugins/sevenzip/plugin.json`:
  ```json
  { "id": "com.sumafile.sevenzip", "name": "7-Zip archives", "version": "1.0.0",
    "publisher": { "name": "SumaFile Team", "url": "https://github.com/conniecombs/SumaFile" }, "sdkVersion": "1.0.0",
    "backend": { "executable": "sumafile-sevenzip-backend.exe", "args": [], "activateOn": "startup", "acceptsMultipleClients": true },
    "permissions": ["backend-process"],
    "contributions": { "archiveFormats": [ { "extension": ".7z", "canList": true, "canExtract": true, "canCreate": true, "canModify": true, "engineName": "7-Zip" } ] },
    "settings": [ { "key": "binaryOverride", "type": "path", "label": "7-Zip executable override", "description": "A 7za.exe, 7z.exe or 7zz.exe file, or a folder that holds one, used instead of the bundled 7-Zip. Leave empty to use the bundled copy.", "default": "", "group": "Advanced" } ] }
  ```
- Produces, crate `sumafile_sevenzip_backend`:
  ```rust
  // binary.rs
  pub const BINARY_OVERRIDE_KEY: &str = "binaryOverride";
  pub fn resolve_binary(override_value: Option<&str>, backend_dir: &Path) -> Result<PathBuf, String>;
  // entries.rs
  pub struct SevenZipEntry { pub path: String, pub is_dir: bool, pub size: u64, pub compressed_size: u64 }
  pub fn parse_list_output(output: &str) -> Vec<SevenZipEntry>;              // port of seven_zip.rs:162-224
  pub fn validate_entry_path(name: &str) -> Result<String, String>;          // `/`-normalized; same rules as core archive_entry_relative_path_from_name + utils::validate_name; "7-Zip entry has unsafe path: <name>"
  pub fn is_within(entry: &str, prefix: &str) -> bool;                       // whole-segment prefix
  // run.rs
  pub trait RunObserver { fn spawned(&mut self, _pid: u32) {} fn progress(&mut self, percent: u8, item: Option<&str>); fn is_cancelled(&self) -> bool; }
  pub fn run_7za(binary: &Path, args: &[OsString], cwd: Option<&Path>, observer: &mut dyn RunObserver) -> Result<Vec<u8> /*stdout*/, String>;
  pub fn spawn_count() -> usize;
  // ops.rs
  pub struct Limits { pub max_bytes: u64, pub max_entries: u32 }
  pub struct SevenZip { /* binary */ }
  impl SevenZip {
      pub fn new(binary: PathBuf) -> Self;
      pub fn list(&self, archive: &Path) -> Result<Vec<SevenZipEntry>, String>;
      pub fn extract_all(&self, archive: &Path, destination: &Path, observer: &mut dyn RunObserver) -> Result<(), String>;
      pub fn read_entry(&self, archive: &Path, entry: &str, destination: &Path, limits: Limits, observer: &mut dyn RunObserver) -> Result<(), String>;
      pub fn create(&self, archive: &Path, paths: &[PathBuf], observer: &mut dyn RunObserver) -> Result<(), String>;
      pub fn rename(&self, archive: &Path, entry: &str, new_path: &str, observer: &mut dyn RunObserver) -> Result<(), String>;
      pub fn delete(&self, archive: &Path, entries: &[String], observer: &mut dyn RunObserver) -> Result<(), String>;
      pub fn add(&self, archive: &Path, base_dir: &Path, paths: &[String], observer: &mut dyn RunObserver) -> Result<(), String>;
  }
  // handler.rs
  pub struct SevenZipHandler { /* backend_dir, override cache */ }
  impl SevenZipHandler {
      pub fn new(backend_dir: PathBuf) -> Self;                                            // reads SIMPLEFILE_7Z once
      pub fn with_legacy_env(backend_dir: PathBuf, simplefile_7z: Option<String>) -> Self; // for tests
      pub async fn binary_for(&self, host: &HostClient) -> Result<PathBuf, String>;
  }
  // implements sumafile_plugin::PluginHandler for the seven archive.* methods
  // main.rs: Args::parse; a multi-thread tokio runtime built with worker_threads(2);
  //          PluginInfo { id: "com.sumafile.sevenzip", version: "1.0.0" }; serve(args, info, true, handler)
  ```
- Decisions, also recorded in spec §11.1 by Task 6:
  - **Binary resolution:**
    - An override that names an existing file wins. An override folder resolves to the first of `7zz.exe`, `7z.exe` and `7za.exe` inside it.
    - Otherwise the backend uses `<backend dir>\7za.exe`. No `SIMPLEFILE_7Z`, PATH or Program Files fallback.
    - A missing override falls back to the bundled binary and logs a warning through the host connection.
    - With neither present it fails with: `7-Zip is unavailable: the bundled 7za.exe is missing from <dir>. Reinstall SumaFile or set the 7-Zip executable override.`
  - **Settings:**
    - `binary_for` reads `binaryOverride` with `host.host_connection()?.get_settings(&[BINARY_OVERRIDE_KEY])` under a 2 s timeout.
    - A success updates the cached value. A failure or a missing host connection uses the cache, which starts as no override. Changes therefore apply on the next operation.
    - The lookup only answers quickly because the service runs every engine-reaching call off its session loop (Task 4).
    - While `SIMPLEFILE_7Z` is set and the override is empty, the first operation logs a warning through the host connection, once per backend process: `SIMPLEFILE_7Z is no longer read. Set Settings > Plugins > 7-Zip archives > 7-Zip executable override instead.`
  - **Threads:**
    - Every operation that runs 7za runs it inside `tokio::task::spawn_blocking`. `binary_for` runs first, on the async side.
    - The blocking closure owns a `ProgressBridge` (a `HostClient` clone plus the `operationId`) as its `RunObserver`. Its `progress` calls `archive_progress`, which never waits, and its `is_cancelled` reads the shared flag.
    - `main.rs` builds the runtime with two worker threads. Those workers only route frames, so a `cancel` or a new `archive.list` is read even while 7za processes run on every connection. Two workers also keep the concurrency tests meaningful on a machine with many cores.
  - **Processes:**
    - One 7za process per command, never one per entry. `extract_all` and `read_entry` are list plus `x`; `rename` and `delete` are list plus `rn`/`d`; `create` and `add` are a single `a`.
    - Every spawn uses `CREATE_NO_WINDOW`, stdin null, and stdout and stderr drained on threads. Exit code ≤ 1 is success.
    - Errors read `<action> failed: <detail>`, with the detail capped at 4000 chars (port of `seven_zip.rs:132-160`).
    - Switches are `-sccUTF-8 -scsUTF-8 -spd -y`. Commands that need progress use `-bsp1 -bso0`, with the percentage parsed from stdout.
    - Listfiles are UTF-8. `--` also stops `@listfile` parsing, so a listfile goes before it, or through `-i@`.
    - Update commands (`a`, `d`, `rn` on an existing archive) pass `-w<dot folder beside the archive>`, which is removed afterwards.
  - **Safety:**
    - Every listed name passes `validate_entry_path` before anything is written.
    - `extract_all` and `read_entry` refuse a non-empty `destination`. Afterwards they walk it and fail, removing the output, if they find a symlink or junction.
    - `read_entry` checks declared sizes against `Limits` before it runs 7za and checks written bytes afterwards.
    - `create` refuses an existing `archive`.
    - `delete` refuses to remove every entry: `7-Zip archives cannot be rewritten with no entries`.
  - **Progress and cancel:**
    - Every operation that carries an `operationId` reports progress, `read_entry` included. The service sends one on every read and transfer call.
      - It reports `archive_progress(op, 0, total)` first and `(op, total, total)` on success. `total` is the declared or source bytes.
      - In between it reports on each 7za percentage change, and at least every 5 s even when 7za prints nothing. A selective read from a large solid archive therefore keeps resetting the service's 120 s read idle deadline.
    - Cancel is polled every 50 ms. When it is set, 7za is killed and waited for, its drain threads are joined, and the call fails with `CANCELLED_MESSAGE`. The server drops the flag when the request returns (Task 3).

- [ ] **Step 1: Write the failing unit tests** in the `src` modules.

```rust
binary::override_file_folder_and_bundled_resolution()       // file -> file; folder with 7z.exe -> it; missing override -> <dir>\7za.exe; neither -> exact error text
entries::parser_reads_files_and_directories()                // ported from core tests.rs:282-319, with the bundled binary's header "7-Zip (a) 26.03 (x64)" instead of 26.02
entries::validate_entry_path_rejects_unsafe_names()          // "../x", "/abs", "C:x", "a/../b", "CON", "a\0b", "dot.", "" all Err; "café 資料/hello.txt" Ok
entries::is_within_is_segment_aware()                        // ("docs/a","docs") true, ("docs2/a","docs") false, ("docs","docs") true
run::cancel_kills_a_running_process()                        // binary %SystemRoot%\System32\PING.EXE itself, args -n 30 127.0.0.1: no cmd, so no grandchild keeps the pipes open;
                                                             // cancel after 200 ms -> Err(CANCELLED_MESSAGE) within 3 s; tasklist /FI "PID eq <pid from spawned()>" no longer lists it
run::progress_lines_are_parsed_from_7za_output()             // "\x08\x08 45% 3 - folder\\a.txt" -> (45, Some("folder\\a.txt"))
run::exit_code_one_is_success_and_two_is_an_error_with_capped_detail()
handler::binary_for_reads_the_override_through_the_host_connection() // fake settings {"binaryOverride": <temp file>} -> that file
handler::cancelled_before_start_fails_without_running_7za()   // fake_cancel("op-1"); archive.extract op-1 -> Err(Application(CANCELLED_MESSAGE)); spawn_count unchanged
handler::legacy_simplefile_7z_env_logs_one_warning()          // with_legacy_env(Some(..)), empty override, two operations -> exactly one "warn" in fake_log, naming the setting
```

- [ ] **Step 2: Write the failing real-7za tests** in `tests/real_7za.rs`.
  - They use `CARGO_MANIFEST_DIR/../../../third_party/7zip/win-x64/7za.exe` and fail, never skip, when it is missing.
  - Fixtures are made with the library, or with the bundled 7za for codec cases.

```rust
round_trip_create_list_extract_preserves_tree()                // ported core tests.rs:322-369
extract_uses_a_bounded_number_of_7za_processes()               // ported :372-424: spawn_count delta <= 2 for 7 entries
extract_refuses_a_non_empty_destination()
read_entry_selects_by_segment_prefix()                         // docs vs docs2
read_entry_rejects_a_selection_over_limits()
read_entry_reports_progress_through_the_observer()             // a RecordingObserver sees progress for a 32 MiB entry, first and last reports included
handler_runs_7za_off_the_async_workers()                       // current-thread runtime: a 10 ms ticker keeps ticking while archive.create of 64 MiB runs through SevenZipHandler
rename_moves_a_folder_with_its_descendants()
delete_removes_a_folder_with_its_descendants()
deleting_the_last_entry_fails_and_leaves_the_archive_unchanged() // Review Focus 3: exact message; archive bytes equal before/after
add_stores_files_and_empty_folders_at_relative_paths()
unicode_and_punctuation_names_survive_every_operation()        // Review Focus 4: create, list, rename, read_entry, delete, extract
deflate64_archive_lists_extracts_and_reads()                   // fixture: 7za a -t7z -m0=Deflate64 (tools/sevenz-evaluation case)
encrypted_content_lists_names_but_extract_fails()              // -pFixturePassword -mhe=off
encrypted_headers_fail_to_list()                               // -mhe=on
create_refuses_an_existing_archive()
update_commands_leave_no_temporary_files_beside_the_archive()  // after rename/delete/add the folder holds only the archive
```

In `tests/process.rs`, add `backend_process_creates_and_lists_a_7z_through_its_pipe`. It stages a temp folder with the exe and the four `third_party/7zip/win-x64` binaries and License, handshakes as the service, runs create and list, then checks that the process exits within 5 s of the disconnect.

- [ ] **Step 3: Run to verify they fail**

Run: `cargo test --locked -p sumafile-sevenzip-backend`
Expected: compile FAIL until the modules exist.

- [ ] **Step 4: Implement the crate.** Run the same command.
Expected: `test result: ok` for the lib, `real_7za` and `process` targets.

- [ ] **Step 5: The host reads the real manifest.** Add `PluginManifestReaderTests.SevenZipManifest_InTheRepo_IsValid`:
  - Find the repo root by walking up from `AppContext.BaseDirectory` to the folder that holds both `Cargo.toml` and `src-winui`.
  - Read `plugins/sevenzip/plugin.json` with `PluginManifestReader.Read(json, "com.sumafile.sevenzip")`.
  - Assert no errors, and one archive format: `.7z` with all four capabilities and engine name `7-Zip`.
  - Assert `Backend.AcceptsMultipleClients` is true and `Backend.ActivateOn == "startup"`, and that `Ui` is null.
  - Assert one setting: `binaryOverride`, of type `path`, with default `""`.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManifestReaderTests"`
Expected: PASS.

- [ ] **Step 6: Workspace checks**

Run: `cargo fmt --all -- --check && cargo clippy --locked --all-targets --all-features -- -D warnings && npm run check:ipc-schema`
Expected: clean. `Cargo.lock` gains only `sumafile-sevenzip-backend` (`cargo tree -p sumafile-sevenzip-backend` lists no new third-party crate).

- [ ] **Step 7: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 6: Remove 7-Zip from core and report `.7z` unavailable without its plugin

**Files:**
- Delete: `crates/simplefile-core/src/archive/seven_zip.rs`.
- Modify `crates/simplefile-core/src/archive/`:
  - `mod.rs:8`
  - `path.rs:12,22,48`
  - `list.rs:17,215-246,406`
  - `extract.rs`: the 7z functions and the 7z listfile
  - `create.rs:16-23,178-204`: the 7z arm, and the RAR message, which no longer suggests `7z`
  - `mutate.rs:5,685-692`
  - `capabilities.rs:4,17-35`
  - `engine.rs` doc
- Modify: `crates/simplefile-core/src/dir_list.rs:252-279`. A pane listing (`list_directory_capturing_raw` with options) routes a path that the plugin note covers to the archive listing. Today it routes on `is_archive_path` alone, which is false for an unserved `.7z` and for paths inside it, so the pane got "Path is not a directory" instead of the note.
- Create: `crates/simplefile-core/src/archive/placeholders.rs`.
- Modify: `crates/simplefile-core/src/archive/tests.rs`:
  - Remove the 7z tests and imports.
  - `SEVEN_ZIP_TEST_LOCK`, which guarded the 7-Zip launch counter, becomes `SEVEN_Z_REGISTRY_LOCK`. Every test that registers `.7z` or asserts that it has no engine takes it.
  - Add the placeholder tests.
- Modify: `crates/simplefile-service/src/session/tests.rs`: the unserved `.7z` listing test.
- Create: `crates/simplefile-service/tests/sevenzip_plugin.rs`.
- Modify: `tools/sevenz-evaluation/{Cargo.toml, src/main.rs, Cargo.lock, README.md}`. Create `tools/sevenz-evaluation/src/plugin_engine.rs`.
- Modify: `README.md:128,243,437,462,705-707`, `docs/winui-migration/inventory.md:131,466`, `docs/winui-migration/parity-gate.md:138-141` and spec §11.1 (the Task 5 decisions; plugin version `1.0.0` is independent of the app version and not bumped by `release:prepare`).

**Interfaces:**
- Consumes: Tasks 1 and 2 engine kinds, the Task 4 harness and the Task 5 exe and library.
- Produces:
  ```rust
  pub(super) struct PluginFormatPlaceholder { pub extension: &'static str, pub label: &'static str, pub note: &'static str }
  pub(super) const PLUGIN_FORMAT_PLACEHOLDERS: &[PluginFormatPlaceholder] = &[PluginFormatPlaceholder { extension: ".7z", label: "7z", note: "7-Zip support requires the SumaFile 7-Zip plugin." }];
  pub(super) fn unavailable_note_for_path(path: &Path) -> Option<&'static str>; // name ends with a placeholder extension and no engine serves it
  pub fn unavailable_archive_note(path: &str) -> Option<&'static str>;          // exported from archive: the note when `path` is, or is inside, such a file
  ```
  - `get_archive_capabilities` lists zip, tar, tar.gz and rar as today, then the registered engines. It then adds one row per placeholder that no engine serves: `{ format: "7z", extension: ".7z", can_* false, engine: "unavailable", note: Some(note) }`.
  - With no engine, `list_archive`, `extract_archive(_with)`, `create_archive(_with)` (format `7z`) and `list_archive_directory` on the `.7z` file all fail with the note.
  - `split_archive_path` fails with the note for any path *inside* such a file. The file itself splits to `Ok(None)`, so copying, renaming or deleting a `.7z` file still works.
  - Pane listings reach the note:
    - `dir_list::list_directory_capturing_raw` with options routes a path to `list_directory_with_options` when `unavailable_archive_note(&path)` is `Some`, as it already does for archive paths. The listing then reaches `list_archive_directory` and fails with the note.
    - Without options, `list_directory` already returns `list_archive_directory`'s error.
    - An error is never cached, and a placeholder path never gets a raw-listing cache entry.
  - `create_archive(.., "rar")` fails with `RAR creation is not supported by SumaFile. Use ZIP, TAR or TAR.GZ, or an archive format a plugin adds.`
- Removed tests and their replacements. No coverage is lost:

  | Removed core test | Replacement |
  | --- | --- |
  | `archive_format_recognizes_7z_extension` | `seven_z_is_registrable_now` |
  | `bundled_seven_zip_candidates_prefer_payload_tool_directory` | `binary::override_file_folder_and_bundled_resolution` |
  | `seven_zip_listing_parser_reads_files_and_directories` | `entries::parser_reads_files_and_directories` |
  | `seven_zip_create_list_extract_round_trip_when_available` | `round_trip_create_list_extract_preserves_tree`, which never skips |
  | `seven_zip_extraction_uses_a_bounded_number_of_processes_when_available` | `extract_uses_a_bounded_number_of_7za_processes` |

- Evaluation tool:
  - `PluginEngine` in the tool implements `simplefile_core::archive::ArchiveEngine` over `sumafile_sevenzip_backend::SevenZip`.
  - `main` drops `std::env::set_var("SIMPLEFILE_7Z", …)`. It resolves the reference binary the way the plugin does, `resolve_binary(None, <repo>/third_party/7zip/win-x64)`. The vendored binaries stay in `third_party` because the plugin folder copies them at staging, so this is the plugin's bundled binary.
  - It then calls `register_engine("com.sumafile.sevenzip", [.7z all four true], …)` and keeps calling `list_archive` and `extract_archive`, so the baseline still exercises core validation and staging.

- [ ] **Step 1: Write the failing core tests.** Every test that registers `.7z` or asserts that it has no engine takes `SEVEN_Z_REGISTRY_LOCK` first.

```rust
#[test] fn capabilities_report_7z_unavailable_without_its_plugin()   // exact row incl. note "7-Zip support requires the SumaFile 7-Zip plugin."
#[test] fn capabilities_list_7z_from_its_engine_once_registered()     // owner com.sumafile.sevenzip -> engine "plugin:com.sumafile.sevenzip", no unavailable .7z row
#[test] fn seven_zip_operations_without_an_engine_fail_with_the_plugin_note() // list_archive, extract_archive, create_archive(..,"7z"), list_archive_directory(file and file\inner)
#[test] fn seven_zip_listing_without_an_engine_reports_the_note_through_dir_list() // dir_list::list_directory_capturing_raw on x.7z and x.7z\docs,
                                                                     // with Some(ListDirectoryOptions::default()) and with None -> Err == note
#[test] fn seven_zip_file_without_an_engine_is_an_ordinary_file()     // split -> Ok(None); is_archive_path false; should_handle_transfer(x.7z, dir) Ok(false)
#[test] fn seven_z_is_registrable_now()                              // is_registrable_extension(".7z")
```

Also extend `create_archive_rejects_rar_without_external_install_guidance`: the message no longer contains `7z`.

In `crates/simplefile-service/src/session/tests.rs`. No service lib test registers `.7z`, so this one can assert that it has no engine:

```rust
#[tokio::test] async fn list_directory_on_an_unserved_7z_reports_the_plugin_note() // list_directory on x.7z and x.7z\docs, with the options PaneNavigator sends and without -> application error == note
```

Run: `cargo test --locked -p simplefile-core -- capabilities_ seven_z` then `cargo test --locked -p simplefile-service list_directory_on_an_unserved_7z`
Expected: FAIL, because `.7z` is still built in, so neither the capability row nor the listing errors carry the note. Without Step 2's `dir_list` routing, the session test would still fail after the removal, with "Path is not a directory".

- [ ] **Step 2: Delete the 7z code, add the placeholders and the `dir_list` routing, and run both suites**

Run: `cargo test --locked -p simplefile-core` then `cargo test --locked -p simplefile-service --lib`
Expected: `test result: ok` for both.

- [ ] **Step 3: Write the service e2e test** (`tests/sevenzip_plugin.rs`).
  - It uses `support::launch_plugin(built_exe("sumafile-sevenzip-backend"), third_party 7za.exe/7za.dll/7zxa.dll/License.txt)`.
  - It registers `.7z` with all four capabilities and engine name `7-Zip`.
  - Every test takes `support::REGISTRY_LOCK` first, since one of them unregisters `.7z` while the others use it.

```rust
sevenzip_plugin_serves_create_list_browse_extract_and_mutation()
    // create_archive 7z of a tree incl. "café 資料/hello.txt" and "literal [brackets] & spaces.txt"; list_archive; list_directory root and nested;
    // read_file_preview + compute_checksum on a virtual path; extract_archive -> tree equal; create_directory, create_file, rename_entry,
    // copy_entry_resolved (in, out, conflict "rename"), move_entry_resolved, delete_entry, move_to_trash of two entries -> re-list after each
sevenzip_plugin_reads_a_deflate64_archive_through_the_service()     // fixture made with bundled 7za -m0=Deflate64
sevenzip_plugin_reports_tracked_progress_and_completes()            // extract_archive operationId -> first "running", last "completed", one event with total > 0
sevenzip_plugin_answers_a_listing_while_two_long_creates_run_and_cancels_them()
    // Review Focus 1. Two tracked create_archive calls of one 256 MiB random file (big-1.7z, big-2.7z). Once both have a "running" event with total > 0,
    // list_archive of small.7z replies within 5 s while neither create has replied. cancel_operation on both -> both reply
    // "Archive operation cancelled" within 5 s; no big-*.7z and no ".simplefile-new-" file remains
sevenzip_read_with_a_host_settings_lookup_completes_quickly()
    // Review Focus 6. A FakeHost on the plugin pipe serves host.get_settings through get_db_settings on the same Service.
    // open_file(x.7z\docs), a folder so nothing is launched, replies "Cannot open a directory as a file" within 1.5 s (the settings timeout is 2 s),
    // and the FakeHost served at least one host.get_settings
sevenzip_plugin_unregistered_7z_reports_the_plugin_note()           // unregister -> capabilities unavailable row; list_archive error == note;
    // list_directory of x.7z and x.7z\docs, with and without options -> error == note
```

Run: `cargo test --locked --all-features`
Expected: `test result: ok`. The workspace run builds the backend exe first.

- [ ] **Step 4: Repoint the evaluation tool**
  - Add the path dependency `sumafile-sevenzip-backend = { path = "../../plugins/sevenzip/backend" }`.
  - Run `cargo build --release --manifest-path tools/sevenz-evaluation/Cargo.toml --target-dir target` once without `--locked`. This adds the new packages to its lock and keeps existing pins.
  - In the tool README, the reference engine becomes "the SumaFile 7-Zip plugin with the bundled 26.03 `7za.exe`".

Run: `cargo run --release --locked --manifest-path tools/sevenz-evaluation/Cargo.toml --target-dir target -- --compatibility-only`
Expected: exit code 2, which is the documented candidate gate failure. The printed `results.json` has `compatibility.codecs[*].sumafile.passed == true` for all 13 cases, including `deflate64`, and every `password_behavior[*].sumafile_rejects_extract_without_password == true`.

- [ ] **Step 5: Docs**
  - In `README.md`, the 7-Zip row reads `.7z` | "SumaFile 7-Zip plugin (`com.sumafile.sevenzip`), installed with SumaFile".
  - Replace the `SIMPLEFILE_7Z` row with the setting "Settings > Plugins > 7-Zip archives > 7-Zip executable override". Add a note under it: `SIMPLEFILE_7Z` is no longer read, and while it is set the backend logs a one-time warning that names the setting.
  - Keep the License link and note that `License.txt` ships in `plugins\com.sumafile.sevenzip`.
  - Update the inventory and parity-gate rows.
  - Run: `npm run check`. Expected: all checks print success.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 7: Host: registration timeout, bounded retry, change ordering and re-registration after a service reconnect

**Files:**
- Modify: `src-winui/SimpleFile.PluginHost/PluginManager.ArchiveEngines.cs:27-111`.
- Modify: `src-winui/SimpleFile.PluginHost/PluginManager.Backend.cs`:
  - `:20-35`: `StartInBackground` passes the dispose token instead of `CancellationToken.None`.
  - `:155-181`: `OnBackendLost` raises `Changed` again once its unregister has finished.
- Modify: `src-winui/SimpleFile.PluginHost/PluginManager.cs:36-52,372-400`: a dispose token, cancelled first in `DisposeAsync`.
- Modify: `src-winui/SimpleFile.PluginHost/Backend/PluginBackendConnection.cs:65-76`: wait up to 2 s for the backend to exit after `Kill()`. This is phase 1 Minor 8; a restart must not race the old pipe's `first_pipe_instance`.
- Modify: `src-winui/SimpleFile.App/MainWindow.xaml.cs:369-424` (`ReconnectBackendAsync`) and `src-winui/SimpleFile.App/MainWindow.Plugins.cs:59-60` (comment).
- Test: `src-winui/SimpleFile.Tests/PluginManagerTests.cs`:
  - `RecordingRegistrar` gains `FailuresBeforeSuccess`, `NeverComplete` and a per-attempt gate.
  - It also gains `Note(string)`, which appends to the same ordered `Calls` log, so a test can record its `Changed` handler there.
- Test: `PluginBackendHostTests.cs`. `FakePluginProcess` gains `ExitDelay: TimeSpan?`, where null means it never exits.
- Test: `WinUiSourceShapeTests.cs`.
- Modify: spec §10, in the "Backend exits unexpectedly" and "Backend call times out" rows: registration retries, the timeout, and re-registration after a service restart.

**Interfaces:**
- Produces, `PluginManager`:
  ```csharp
  internal const int ArchiveRegisterAttempts = 3;
  internal TimeSpan ArchiveRegisterTimeout { get; set; } = TimeSpan.FromSeconds(15);            // per attempt
  internal Func<int, CancellationToken, Task> ArchiveRetryDelay { get; set; }                     // default: after attempt 1 wait 1 s, after attempt 2 wait 3 s
  /// Registers again every engine the host believes registered (Enabled, live connection, ArchiveEnginesGeneration != 0) with the service the registrar now reaches.
  public Task ReregisterArchiveEnginesAsync(CancellationToken ct);
  ```
  - `RegisterArchiveEnginesAsync` makes up to three attempts. Each attempt takes the plugin's registrar lock, which is not held across the delay. It re-checks the generation and `IsLiveConnection`, then calls `registrar.RegisterAsync(...).WaitAsync(ArchiveRegisterTimeout, ct)`.
  - Every exception except cancellation of `ct` counts as a failed attempt and is logged as `[warn] Archive engine registration attempt {n} of 3 failed: {message}`.
  - A connection that is lost, disabled or restarted during the delay stops the loop silently.
  - After the third failure the plugin faults as today, with the message `Archive engine registration failed: {last message}`. Then it unregisters once more:
    - It goes through `UnregisterArchiveEnginesAsync(record, generation)`, so it holds the plugin's registration lock and checks the generation, best effort within 5 s.
    - A Restart that registered a newer generation meanwhile is left alone.
    - This unregister finishes before `RaiseChanged()`.
    - The service's registration ticket (Task 4) means a register that is still running on the service side cannot land after it.
  - A successful registration logs `[info] Archive engines registered: {extensions}`; for 7-Zip that is `.7z`. The Task 12 startup smoke reads this line.
  - `OnBackendLost` raises `Changed` a second time when it unregistered anything: inside its `Task.Run`, after `UnregisterArchiveEnginesAsync` completes. Every change to the service's engine set is therefore followed by a `Changed`. Task 9 refreshes capabilities on it, so a fault never leaves `.7z` looking registered.
  - The manager owns a dispose token:
    - `DisposeAsync` cancels it first.
    - `StartInBackground` passes it instead of `CancellationToken.None`.
    - The attempt loop links it with `ct` for its lock waits, `WaitAsync` and `ArchiveRetryDelay`.
  - `ReregisterArchiveEnginesAsync` runs the same attempt loop for each live registered plugin, all of them concurrently, and swallows the resulting `PluginBackendException`. `BackendRunning` stays true while it re-registers. It raises `Changed` once, when every loop has ended.
  - In `MainWindow.ReconnectBackendAsync`, re-registration starts in the background, right after `_workspace.FileOps?.ReplaceIpc(backend.Client)`:
    - When `_plugins` exists, the call is `_ = ObserveAsync(_plugins.ReregisterArchiveEnginesAsync(_pluginCts?.Token ?? CancellationToken.None))`. `ObserveAsync` is a small helper that logs a failure to `plugins.log`.
    - The drive refresh, the pane refresh and the "reconnected" message do not wait for it. A stuck service would otherwise hold the reconnect for about 50 s: 3 × 15 s plus the 1 s and 3 s delays.
    - The `Changed` it raises when done drives the Task 9 capability refresh.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task ArchivePlugin_TransientRegistrationFailure_RetriesAndRegisters()          // FailuresBeforeSuccess 1 -> Enabled, BackendRunning, 2 register calls
[Fact] public async Task ArchivePlugin_RegistrationFailsEveryAttempt_FaultsAfterThreeAttemptsAndUnregisters() // replaces ArchivePlugin_RegistrationFails_SetsFaultedAndStopsBackend: 3 calls, Faulted, killed, 1 unregister
[Fact] public async Task ArchivePlugin_RegistrationThatNeverCompletes_TimesOutPerAttempt()        // ArchiveRegisterTimeout 200 ms, NeverComplete -> Faulted within 5 s, 3 calls
[Fact] public async Task ArchivePlugin_LostDuringRetryDelay_StopsRetrying()                      // gate the delay; backend Lost meanwhile -> no 2nd register call
[Fact] public async Task ArchivePlugin_ReregisterAfterServiceReconnect_RegistersLiveEnginesAgain() // 2nd register call with the same pipe; BackendRunning stays true
[Fact] public async Task ArchivePlugin_ReregisterSkipsDisabledAndFaultedPlugins()
[Fact] public async Task ArchivePlugin_ReregisterFailingEveryAttempt_FaultsThePlugin()
[Fact] public async Task ArchivePlugin_ReregisterAfterServiceReconnect_RaisesChangedWhenDone()
[Fact] public async Task ArchivePlugin_FinalUnregisterAfterFailedAttempts_SkipsWhenANewerGenerationRegistered() // gate the 3rd attempt; RestartBackendAsync registers generation 2 meanwhile -> no final unregister; gen 2's pipe stays registered
[Fact] public async Task ArchivePlugin_Lost_RaisesChangedAfterTheUnregisterCompletes()   // Changed handler Notes "changed"; SimulateExit -> Calls has a "changed" after "unregister"
[Fact] public async Task ArchivePlugin_DisposeDuringRetryDelay_StopsRetrying()           // a gated ArchiveRetryDelay sees its token cancelled by DisposeAsync; no 2nd register call; dispose < 5 s
[Fact] public async Task ArchivePlugin_SuccessfulRegistration_LogsTheRegisteredFormats() // log has "[info] Archive engines registered: .hello"
// WinUiSourceShapeTests
[Fact] public void MainWindow_ReconnectDoesNotAwaitArchiveReregistration()                // ReconnectBackendAsync calls ReregisterArchiveEnginesAsync( and never awaits it
// PluginBackendHostTests
[Fact] public async Task DisposeAsync_WaitsUpToTwoSecondsForTheBackendToExit()                    // ExitDelay 300 ms -> dispose >= 300 ms; ExitDelay null -> dispose < 2.5 s
```

Tests set `ArchiveRetryDelay = (_, _) => Task.CompletedTask` unless a test gates it.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManagerTests|FullyQualifiedName~PluginBackendHostTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: FAIL (`ReregisterArchiveEnginesAsync` and the timeout seams are missing).

- [ ] **Step 3: Implement, wire `ReconnectBackendAsync`, and run the full C# suite**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo` then `dotnet build src-winui/SimpleFile.sln -c Debug`
Expected: PASS, zero warnings.

- [ ] **Step 4: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 8: Host: bundled-plugin trust and approval-store hardening

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/Trust/BundledPluginList.cs`.
- Modify: `src-winui/SimpleFile.PluginHost/Trust/PluginApprovalStore.cs:43-106`, `PluginManager.cs:10-26,155-186` and `Discovery/PluginDiscovery.cs:12-23`.
- Modify: `src-winui/SimpleFile.App/MainWindow.Plugins.cs:41-62`: set `BundledRoot`, and log active environment overrides to `plugins.log`.
- Test: `src-winui/SimpleFile.Tests/PluginTrustTests.cs`, `PluginManagerTests.cs` and `PluginDiscoveryTests.cs`.
- Create: `src-winui/SimpleFile.Tests/PluginEnvironmentTests.cs`. It closes the phase-1 env-var race, in which `PluginTrustTests` and `PluginDiscoveryTests` changed process-wide variables in parallel:
  - It declares `[CollectionDefinition("Plugin environment", DisableParallelization = true)]`.
  - These tests move into it: `PluginDiscoveryTests.DefaultRoots_InstallThenLocalAppData`, `DefaultRoots_EnvOverride_ReplacesDefaultsAndSkipsEmpties` and `UserRoot_IsLocalAppData_EvenWithEnvOverride`, and `PluginTrustTests.ApprovalStore_DefaultPath_HonorsEnvOverrideOnlyWhenSet`.
- Modify: spec §9 (the first-load consent bullet) and §11.4. Record this decision: *the release build writes `plugins\bundled.json` listing each first-party plugin's manifest SHA-256. The host enables a listed plugin found in its install root without consent, unless the user stored Disabled for that folder. User-root plugins always need consent. This replaces "the installer writing their manifest hashes to the approval store", which NSIS, MSI admin installs and the portable zip cannot do reliably.*

**Interfaces:**
- Produces:
  ```csharp
  public sealed record BundledPlugin(string Id, string ManifestSha256);
  public static class BundledPluginList
  {
      public const string FileName = "bundled.json";            // {"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"<64 lowercase hex>"}]}
      public static IReadOnlyDictionary<string, string> Read(string root);   // id -> hash; empty when missing/unreadable/malformed; malformed entries skipped
  }
  // PluginApprovalStore
  public ApprovalState? StoredState(string pluginId, string folder);         // stored state for the same folder whatever the hash or signature; null when none
  // PluginManagerOptions
  public string? BundledRoot { get; init; }                                  // null disables bundled trust
  // PluginDiscovery
  public static string InstallRoot();                                        // Path.Combine(AppContext.BaseDirectory, "plugins"); DefaultRoots uses it
  public static IReadOnlyList<string> DescribeOverrides();                   // "[warn] SUMAFILE_PLUGIN_ROOTS replaces the plugin roots: <v>" and the approvals equivalent, when set
  ```
  - `Evaluate`: when `Resolve` returns NeedsConsent, the plugin's `Root` equals `BundledRoot` and the bundled list maps its id to the computed manifest hash, the state becomes `StoredState(id, folder) == Disabled ? Disabled : Enabled`.
    - The roots compare as `Path.GetFullPath` forms with trailing separators trimmed, case-insensitively. The root from `SUMAFILE_PLUGIN_ROOTS` in the smokes therefore matches `AppContext.BaseDirectory\plugins`.
    - Nothing is written to the store. The list is read once per scan.
  - Store hardening:
    - `Load` retries `IOException` up to three times, 50 ms apart.
    - If it still fails, `Resolve` and `StoredState` treat the store as empty, but `Set` and `Remove` throw `IOException("Could not read the plugin approvals file: …")` and save nothing.
    - `Save` writes `{path}.{ProcessId}.{Guid:N}.tmp` and deletes it if the move fails.

- [ ] **Step 1: Write the failing tests**

```csharp
// PluginTrustTests
[Fact] public void BundledPluginList_ReadsIdsAndHashes_AndSkipsMalformedEntries()
[Fact] public void BundledPluginList_MissingOrCorruptFile_IsEmpty()
[Fact] public void StoredState_IgnoresHashButRequiresTheSameFolder()
[Fact] public void Set_WhenTheFileCannotBeRead_ThrowsAndKeepsOtherApprovals()   // hold the file with FileShare.None during Set -> IOException; afterwards the other plugin's record is intact
[Fact] public async Task Save_FromParallelStores_NeverCollidesOnTheTempFile()  // two stores, 50 parallel Sets, no exception
// PluginManagerTests
[Fact] public async Task BundledPlugin_InInstallRoot_IsEnabledWithoutConsent()    // no approvals file afterwards
[Fact] public async Task BundledPlugin_ManifestHashNotListed_NeedsConsent()
[Fact] public async Task BundledPlugin_UserRootCopy_NeedsConsent()                // user copy NeedsConsent, install copy Shadowed
[Fact] public async Task BundledPlugin_UserDisabled_StaysDisabledAfterUpgrade()   // Review Focus 5: Disabled record (old hash, same folder) + list with new hash -> Disabled, no consent queued
[Fact] public async Task BundledPlugin_ListInAnotherRoot_IsIgnored()
// PluginEnvironmentTests ("Plugin environment" collection, parallelization disabled)
[Fact] public void DescribeOverrides_ReportsActiveEnvironmentOverrides()   // sets both variables and restores them in finally
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginTrustTests|FullyQualifiedName~PluginManagerTests|FullyQualifiedName~PluginDiscoveryTests|FullyQualifiedName~PluginEnvironmentTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement it, wire `BundledRoot = PluginDiscovery.InstallRoot()` and log `DescribeOverrides()` in `InitializePluginsAsync`, then run the full C# suite**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`
Expected: PASS.

- [ ] **Step 4: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 9: UI: capability-driven archive recognition and clean degradation

**Files:**
- Modify: `src-winui/SimpleFile.Core/ArchivePaths.cs`: drop `.7z` from `KnownArchiveExtensions` and the `"7z" => ".7z"` case from `ExtensionForFormat`, and add the engine format set. `RemoveKnownArchiveExtension` also strips registered engine extensions. Otherwise a zip made from a selected `report.7z` would be named `report.7z.zip` even while 7-Zip is registered.
- Modify: `src-winui/SimpleFile.Core/WorkspaceControllers/WorkspaceNavigationController.cs:641-650`: `IsSupportedArchivePath` delegates to `ArchivePaths.IsBrowsableArchive`.
- Modify: `src-winui/SimpleFile.Core/ExplorerWorkspace.Navigation.cs`: `RefreshArchivePanesAsync`.
- Modify: `src-winui/SimpleFile.App/CreateArchiveDialog.xaml.cs:106-112`: the fallback formats are zip, tar and tar.gz only. The `"7z" => "7-Zip (.7z)"` label case stays; it is cosmetic, and with no engine the format is never offered.
- Modify: `src-winui/SimpleFile.App/SettingsWindow.xaml:561-564` and `SettingsWindow.Tools.cs`:
  - The static text becomes "ZIP, TAR and TAR.GZ are built in. RAR archives can be listed and extracted but not created."
  - A new `TextBlock x:Name="ArchivePluginFormatsText"` is filled from capabilities: `"{format} archives: {owner} plugin"` for each `plugin:<owner>` row, and the row's note for each `unavailable` row.
- Modify: `src-winui/SimpleFile.App/MainWindow.Plugins.cs:98-111` (`OnPluginsChanged`) and `MainWindow.xaml.cs`. In `MainWindow.xaml.cs` the refresh runs after `OnPluginWorkspaceInitialized`, and in `ReconnectBackendAsync` right after `ReplaceIpc`, before the pane refresh. Create the `RefreshArchiveCapabilitiesAsync` part of `src-winui/SimpleFile.App/MainWindow.ArchiveTransfers.cs`.
- Test:
  - `src-winui/SimpleFile.Tests/ArchivePathsTests.cs` and `WinUiSourceShapeTests.cs:582-595`.
  - `ExplorerWorkspaceTests.cs:245-262`: the `OpenArchiveFile_NavigatesIntoArchive` theory keeps only `pack.zip`.
  - Create `src-winui/SimpleFile.Tests/ArchiveEngineNavigationTests.cs`. The engine-format tests live there, because `ArchivePaths` engine state is process-wide.
- Modify: spec §10, the "Plugin faulted while a 7-Zip archive is open in a pane" row. It becomes: the pane re-lists and shows the listing error that carries the note.

**Interfaces:**
- Consumes: Task 6 placeholder rows (`engine == "unavailable"`, with a note) and plugin rows (`engine == "plugin:<owner>"`); Task 6's pane-listing note; Task 7's guarantee that `Changed` follows every engine-set change.
- Produces:
  ```csharp
  public sealed record ArchiveFormatChange(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);
  public static class ArchivePaths
  {
      public static ArchiveFormatChange SetEngineFormats(IEnumerable<ArchiveFormatCapability> capabilities); // keeps rows with Engine starting "plugin:" and CanList; atomic swap
      public static bool IsArchiveFile(string? path);      // built-ins .tar.gz .tgz .zip .tar .gz .rar, or a listable engine extension
      public static bool IsBrowsableArchive(string? path); // built-ins without .gz, or a listable engine extension
      public static bool IsEngineArchive(string? path);    // served by a plugin engine (Task 10 routes these to Transfers rows)
      // ExtensionForFormat: built-in labels as today; otherwise the engine format whose label matches; otherwise ".zip"
      // RemoveKnownArchiveExtension (WithArchiveExtension, ExtractFolderName): the longest registered engine extension first, then the built-ins
  }
  public Task RefreshArchivePanesAsync(IReadOnlyCollection<string> extensions, CancellationToken cancellationToken = default); // ExplorerWorkspace: re-lists visible panes whose path is, or is inside, a file ending with one of the extensions
  ```
  - `MainWindow.RefreshArchiveCapabilitiesAsync()` runs on the UI thread. It calls `GetArchiveCapabilitiesAsync`, then `SetEngineFormats`, then `RefreshArchivePanesAsync(Added ∪ Removed)` when either is non-empty. A failure is logged to `plugins.log` and ignored.
  - It runs on every `PluginManager.Changed`. Task 7 raises `Changed` after every engine-set change, including after a lost backend's unregister. The refresh therefore never reads capabilities from before the change, which would leave `.7z` in `ArchivePaths` with nothing to re-list it later.
  - In `ReconnectBackendAsync` it runs right after `ReplaceIpc`, before the pane refresh:
    - The new service has no engines until re-registration ends. The first refresh therefore removes `.7z`, and `.7z` panes show the note.
    - The `Changed` raised when re-registration ends refreshes again, adds `.7z` and re-lists those panes.
    - Without the first refresh, `ArchivePaths` would still hold `.7z` from before the restart. The second refresh would then see no change, and nothing would re-list.

- [ ] **Step 1: Write the failing tests.** Every test that sets or depends on engine formats sits in `[Collection("Archive formats")]` and resets to an empty set on dispose. That covers `ArchivePathsTests` and the new `ArchiveEngineNavigationTests`.

```csharp
[Fact] public void ArchivePaths_RecognizeCompoundExtensions()        // updated: IsArchiveFile("bundle.7z") false; WithArchiveExtension("report.v1.zip","7z") == "report.v1.zip"
[Fact] public void ArchivePaths_EngineFormatsAreRecognizedOnlyWhileRegistered() // .7z plugin row -> IsArchiveFile/IsBrowsableArchive/IsEngineArchive true, ExtensionForFormat("7z") ".7z", Added [".7z"],
                                                                                // WithArchiveExtension("pack.7z","zip") == "pack.zip", ExtractFolderName("pack.7z") == "pack"; then [] -> Removed [".7z"]
[Fact] public void ArchivePaths_UnavailableRowsAreNotArchives()
// ArchiveEngineNavigationTests
[Fact] public async Task OpenSevenZipFile_WithItsEngine_NavigatesIntoArchive()       // the old pack.7z theory case, with the engine format set first
[Fact] public async Task OpenSevenZipFile_WithoutItsEngine_OpensWithTheDefaultApp()   // OpenFileAsync called, no navigation
[Fact] public async Task RefreshArchivePanes_RelistsPanesInsideArchivesWhoseEngineChanged() // Review Focus 2: listing of C:\Users\test\pack.7z\docs throws the note, then succeeds; a C:\Users\test pane is not re-listed
[Fact] public void CreateArchiveDialog_UsesCapabilitiesAndDoesNotOfferRarCreation()   // add: DoesNotContain("Format = \"7z\"", dialogCode)
[Fact] public void SettingsArchiveSupport_IsBuiltFromCapabilities()                    // XAML no longer contains "7z, and RAR extraction are handled by SumaFile"; Tools.cs calls GetArchiveCapabilitiesAsync and sets ArchivePluginFormatsText
[Fact] public void MainWindow_RefreshesArchiveCapabilitiesOnPluginChangesAndReconnect() // OnPluginsChanged calls RefreshArchiveCapabilitiesAsync; ReconnectBackendAsync calls it before RefreshAffectedPanesAsync
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ArchivePathsTests|FullyQualifiedName~ArchiveEngineNavigationTests|FullyQualifiedName~ExplorerWorkspaceTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: FAIL.

- [ ] **Step 3: Implement it and run the full C# suite and the solution build**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo` and `dotnet build src-winui/SimpleFile.sln -c Debug`
Expected: PASS, zero warnings.

- [ ] **Step 4: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 10: UI: plugin-format extract and create as Transfers rows with progress and Cancel

**Files:**
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`: add the tracked methods next to `ExtractArchiveAsync`.
- Modify: `src-winui/SimpleFile.App/MainWindow.ArchiveTransfers.cs` (`EnqueueArchiveTransfer`) and `FileOperationDialogService.cs:36-84,733-800,1040-1090`. The constructor gains `Func<ArchiveTransferRequest, TransferOperationViewModel?> enqueueArchiveTransfer`.
- Modify: `MainWindow.Transfer.cs:1128-1186` (context-menu extract) and `MainWindow.xaml.cs:145-168` (pass the delegate).
- Modify: `scripts/check-ipc-schema.mjs`: `cancel.cancelledMessage` must also be a quoted literal in `src-winui/SimpleFile.Core/FileOperationService.cs`. The three copies of the sentinel (core, helper crate, C#) are then checked against the protocol doc.
- Test: `src-winui/SimpleFile.Tests/FileOperationServiceTests.cs`, `TransferManagerViewModelTests.cs` and `WinUiSourceShapeTests.cs`.
- Modify: spec §6.4: plugin-format extract and create appear in the Transfers window with Cancel; built-in formats keep the modal dialog.

**Interfaces:**
- Consumes: Task 4 `operationId` bindings, `operation-progress` and `cancel_operation`; Task 9 `ArchivePaths.IsEngineArchive`.
- Produces:
  ```csharp
  // FileOperationService
  internal const string ArchiveCancelledMessage = "Archive operation cancelled";   // protocol cancel.cancelledMessage
  public Task ExtractArchiveTrackedAsync(string archivePath, string destination, IProgress<ProgressUpdate> progress, Action<string>? operationStarted, CancellationToken ct);
  public Task CreateArchiveTrackedAsync(string[] paths, string archivePath, string format, IProgress<ProgressUpdate> progress, Action<string>? operationStarted, CancellationToken ct);
  // App
  internal sealed record ArchiveTransferRequest(string Title, string Detail, Func<IProgress<ProgressUpdate>, Action<string>, CancellationToken, Task> Run, IReadOnlyList<string> RefreshPaths);
  internal TransferOperationViewModel? EnqueueArchiveTransfer(ArchiveTransferRequest request);   // MainWindow
  ```
  - Each tracked method generates `archive-<guid:N>`, reports it through `operationStarted`, and subscribes to `OperationProgressEvent` filtered by that id. It sends the request with `CancellationToken.None`.
  - When `ct` is cancelled, the method sends `cancel_operation` exactly once, best effort, and keeps awaiting the reply.
  - When `ct` was cancelled or the reply's message equals `ArchiveCancelledMessage`, it throws `OperationCanceledException(ct)`.
  - `EnqueueArchiveTransfer` uses `TransferManagerViewModel.EnqueueExternal(Title, Detail, runner)`. The runner applies progress with `row.ApplyProgress` through a `Progress<ProgressUpdate>` created on the UI thread, sets `row.SetOperationId`, and on success calls `RefreshAffectedPanesAsync(RefreshPaths)`.
  - Titles are `Extracting {archive name}` and `Creating {archive name}`. The Detail is the destination or the archive path.
  - The dialogs and the context-menu extract take this path when `ArchivePaths.IsEngineArchive(archive)` is true. For create, that is the chosen archive path. Otherwise the existing modal path runs unchanged.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task ExtractArchiveTrackedAsync_SendsOperationIdAndReportsMatchingProgress()  // ConfigurableIpc: operationId starts "archive-"; events for another id are not reported
[Fact] public async Task ExtractArchiveTrackedAsync_CancelSendsCancelOperationOnceAndThrowsCanceled() // reply "Archive operation cancelled" -> OperationCanceledException; one CancelOperationAsync
[Fact] public async Task CreateArchiveTrackedAsync_SendsOperationIdAndFormat()
[Fact] public void ExternalRow_ApplyProgress_ShowsBytePercent()                                   // TransferManagerViewModelTests: current 50 of 100 -> ProgressPercent 50, OperationId set
[Fact] public void PluginFormatArchiveOperations_UseTransfersRows()                               // WinUiSourceShapeTests: dialog service + MainWindow.Transfer use IsEngineArchive -> enqueueArchiveTransfer
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~FileOperationServiceTests|FullyQualifiedName~TransferManagerViewModelTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: FAIL.

- [ ] **Step 3: Implement it and run the full C# suite, the build and the schema check**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo` and `dotnet build src-winui/SimpleFile.sln -c Debug`, then `npm run check:ipc-schema`
Expected: PASS, zero warnings. The schema check finds the cancelled message in all three sources.

- [ ] **Step 4: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 11: Plugin staging for dev runs and smokes

**Files:**
- Create: `scripts/lib/plugin-staging.ps1` and `scripts/build-plugins.ps1`.
- Modify: `src-winui/SimpleFile.App/SimpleFile.App.csproj`: add a `CopyBundledPlugins` target.
- Modify: `package.json`: `dev:winui` and a new `build:plugins` alias.
- Modify: `scripts/smoke-winui-ui.ps1:28-40`:
  - Unless `-SkipBuild`, it runs `cargo build --locked -p simplefile-service -p sumafile-sevenzip-backend`, then `scripts/build-plugins.ps1 -SkipCargo`, then `npm run build:winui`. A smoke build therefore always restages from fresh `target\debug` output.
  - With or without `-SkipBuild`, when `<exe dir>\plugins\bundled.json` exists, it calls `Assert-BundledPluginsFresh -Root <exe dir>\plugins -Configuration Debug`.
  - The check is needed because a plain `npm run build:winui` copies whatever `target\plugins` holds, so `-SkipBuild` could otherwise test an old backend.
- Modify: `scripts/check-winui-packaging.mjs`.

**Interfaces:**
- Produces, in `scripts/lib/plugin-staging.ps1` (dot-sourced):
  - `Install-BundledPlugins -Root <dir> -Configuration <Debug|Release>`:
    - Removes and recreates `<Root>\com.sumafile.sevenzip\`.
    - Copies `plugins\sevenzip\plugin.json`, `target\<debug|release>\sumafile-sevenzip-backend.exe` and `third_party\7zip\win-x64\{7za.exe,7za.dll,7zxa.dll,License.txt,SUMAFILE_BUNDLE.md}` into it. `readme.txt` is not copied.
    - Writes `<Root>\bundled.json` with the lowercase SHA-256 of the **staged** `plugin.json` bytes, the same as `PluginPackageHash`.
  - `Assert-BundledPlugins -Root <dir>` throws on a missing file, a `plugin.json` whose id is not `com.sumafile.sevenzip`, or a `bundled.json` hash that differs from the staged manifest.
  - `Assert-BundledPluginsFresh -Root <dir> -Configuration <Debug|Release>` throws `Bundled 7-Zip plugin in <Root> is stale. Run scripts/build-plugins.ps1 and rebuild.` It does so when the SHA-256 of a staged file differs from its source:
    - the staged `sumafile-sevenzip-backend.exe` against `target\<debug|release>\sumafile-sevenzip-backend.exe`;
    - the staged `plugin.json` against `plugins\sevenzip\plugin.json`.
- `scripts/build-plugins.ps1 [-Configuration Debug] [-Destination target\plugins] [-SkipCargo]`:
  - Runs `cargo build --locked -p sumafile-sevenzip-backend`, with `--release` for Release.
  - Then calls `Install-BundledPlugins` and `Assert-BundledPlugins`.
  - Prints `Bundled plugins verified: com.sumafile.sevenzip`, then the root.
- `SimpleFile.App.csproj`: target `CopyBundledPlugins` (`AfterTargets="Build"`, condition `'$(Configuration)' == 'Debug'` and the repo's `target\plugins\bundled.json` exists) copies `target\plugins\**` into `$(OutDir)plugins\`. In a Debug run the install root therefore holds the plugin and `bundled.json`.
- `package.json`:
  - `"build:plugins": "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1"`
  - `"dev:winui": "cargo build -p simplefile-service && npm run build:plugins && dotnet run --project src-winui/SimpleFile.App/SimpleFile.App.csproj -c Debug"`
- `check-winui-packaging.mjs`:
  - `requiredFiles` adds `scripts/build-plugins.ps1`, `scripts/lib/plugin-staging.ps1`, `plugins/sevenzip/plugin.json` and `plugins/sevenzip/backend/Cargo.toml`.
  - App csproj snippet `CopyBundledPlugins`; npm snippet `"build:plugins"`.
  - A new `stagingSnippets` list over `scripts/lib/plugin-staging.ps1`. It requires:
    - `third_party\\7zip\\win-x64`, `7za.exe`, `7za.dll`, `7zxa.dll`, `License.txt`, `SUMAFILE_BUNDLE.md` and `bundled.json`;
    - `com.sumafile.sevenzip`, `sumafile-sevenzip-backend.exe` and `Assert-BundledPluginsFresh`.
  - Once Task 12 takes the 7-Zip files out of the build script, this list is what polices the plugin folder's contents.

- [ ] **Step 1: Write the scripts and the csproj target**

- [ ] **Step 2: Stage and verify**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1`
Expected: `Bundled plugins verified: com.sumafile.sevenzip`, followed by the `target\plugins` path.

- [ ] **Step 3: Build the app and check that the output carries the plugin**

Run: `dotnet build src-winui/SimpleFile.App/SimpleFile.App.csproj -c Debug --nologo`, then:

```powershell
powershell -NoProfile -Command ". scripts/lib/plugin-staging.ps1; Assert-BundledPlugins -Root 'src-winui/SimpleFile.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/plugins'; Assert-BundledPluginsFresh -Root 'src-winui/SimpleFile.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/plugins' -Configuration Debug"
```

Expected: build succeeds, and both asserts print nothing and exit 0.

- [ ] **Step 4: Static checks**

Run: `npm run check`
Expected: every check prints success, including `WinUI packaging surface is wired.`

- [ ] **Step 5: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 12: Release payload, installers, packaging checks, CI workflow and the payload smoke

**Files:**
- Modify: `scripts/build-winui-release.ps1`:
  - Dot-source `scripts/lib/plugin-staging.ps1` at the top.
  - Delete `:21` (`$sevenZipBundleDir`) and `:168-178` (`Copy-BundledArchiveTools`).
  - Right after the service build (`:359`), add `Invoke-Native cargo @("build", "-p", "sumafile-sevenzip-backend", "--locked", "--release")`.
  - In `New-WinUIPayload` (`:316-330`), replace the `Copy-BundledArchiveTools` call with `Install-BundledPlugins -Root (Join-Path $Destination "plugins") -Configuration Release`.
  - `Assert-Payload` (`:150-166`) now requires `tools\7zip` to be **absent** and calls `Assert-BundledPlugins -Root <payload>\plugins`.
  - `Get-LikelyWinUILockHolder` (`:241`) adds `sumafile-sevenzip-backend.exe`.
- Modify: `packaging/winui/sumafile-winui.nsi`:
  - `.onInit` and the Uninstall section `taskkill /F /IM sumafile-sevenzip-backend.exe`. Never kill `7za.exe`, which could be the user's own job.
  - Before `File /r`: `RMDir /r "$INSTDIR\tools\7zip"`, `RMDir "$INSTDIR\tools"` (removes it only when empty) and `RMDir /r "$INSTDIR\plugins\com.sumafile.sevenzip"`. The user root is never touched.
- `packaging/winui/Product.wxs`: no change. heat harvests `plugins\**` and `MajorUpgrade` removes 1.0.4's `tools\7zip`.
- Modify: `scripts/check-winui-packaging.mjs`:
  - `buildSnippets` drops `'third_party\\7zip\\win-x64'` and `'7za.exe'`. Both leave the build script along with `$sevenZipBundleDir`, `Copy-BundledArchiveTools` and the `Assert-Payload` 7za loop; Task 11's `stagingSnippets` check them in `scripts/lib/plugin-staging.ps1` instead.
  - `buildSnippets` keeps `'tools\\7zip'`, which the absence check still contains.
  - `buildSnippets` adds `'sumafile-sevenzip-backend'`, `'Install-BundledPlugins'` and `'Assert-BundledPlugins'`.
  - `nsisSnippets` adds `'taskkill /F /IM sumafile-sevenzip-backend.exe'`, `'RMDir /r "$INSTDIR\\tools\\7zip"'` and `'$INSTDIR\\plugins\\com.sumafile.sevenzip'`.
  - `workflowSnippets` adds `['ci.yml', ciYml, 'build-plugins.ps1']`.
  - The `third_party/7zip/win-x64` required files stay: they remain the vendored source.
- Modify: `scripts/smoke-winui-file-ops.ps1`, with a new section after the copy and move checks:
  - Launch `payload\plugins\com.sumafile.sevenzip\sumafile-sevenzip-backend.exe --pipe-name sumafile-7z-smoke-<guid> --data-dir <smoke>\plugin-data`.
  - Over the payload service: `register_archive_engine` with the `.7z` row from `plugin.json`; `create_archive` (7z); `list_archive`; `list_directory` on the archive; `extract_archive`, with the contents compared.
  - Check that `get_archive_capabilities` lists `.7z` with engine `plugin:com.sumafile.sevenzip`.
  - `unregister_archive_engine`, after which the `.7z` row has engine `unavailable` and the note.
  - Print `Payload 7-Zip plugin smoke passed.` and kill the backend in `finally`.
- Modify: `scripts/smoke-winui-startup.ps1`, so the payload run proves bundled trust. A payload whose `bundled.json` failed would otherwise still pass on the title and `Connect.ready` alone.
  - Start the app with `SUMAFILE_PLUGIN_ROOTS=<payload>\plugins` and `SUMAFILE_PLUGIN_APPROVALS_PATH=<temp>\plugin-approvals.json`. Set them in `$env:` and restore them in `finally`.
  - After `Connect.ready`, wait up to 20 s for a new `%LOCALAPPDATA%\SumaFile\plugins.log` line that contains `[com.sumafile.sevenzip] [info] Archive engines registered: .7z`. Read from an offset, as the script already does for the timing log.
  - Check that the temp approvals file holds no `com.sumafile.sevenzip` record, then print `Bundled 7-Zip plugin trusted and registered.`
- Modify: `scripts/smoke-winui-msi.ps1`: after the `/a` extraction, dot-source `plugin-staging.ps1` and run `Assert-BundledPlugins` on the extracted `plugins` folder next to `SumaFile.exe`.
- Modify: `scripts/smoke-winui-nsis.ps1` and `scripts/smoke-winui-upgrade.ps1`:
  - The NSIS smoke checks after install; the upgrade smoke checks after the upgrade from 1.0.4, which has `tools\7zip`.
  - Both run `Assert-BundledPlugins` on `<install dir>\plugins` and throw when `<install dir>\tools\7zip` exists.
  - They are not run in this phase, because they install over the user's real SumaFile. The later CI pass verifies them.
- Modify: `.github/workflows/ci.yml:174-175`: after the Hello fixture step, add the step `Build and stage the bundled plugins` running `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1`. The smoke command lines stay the same; Task 13 teaches the script to add the install root.
- Modify: `third_party/7zip/win-x64/SUMAFILE_BUNDLE.md`. The plugin folder ships this file as its provenance note, so it must match what ships:
  - The shipped file list becomes the plugin folder list, at `plugins\com.sumafile.sevenzip`.
  - The version stays 26.03, which `7za.exe` reports (`7-Zip (a) 26.03 (x64)`).
  - A new "Source" note says `third_party/7zip/source/7z2606-src.tar.xz` is 26.06 source, added with `tools/sevenz-evaluation`, and not the source of these binaries. The matching 26.03 source is the one published with the upstream 26.03 release.
- Modify: spec §11.4: payload layout, `bundled.json`, NSIS cleanup of `tools\7zip`, and the payload smoke.

**Interfaces:**
- Consumes: Task 11 `Install-BundledPlugins`, `Assert-BundledPlugins` and `stagingSnippets`; Task 5 exe; Task 6 placeholder note; Task 7 registration log line; Task 8 bundled trust.

- [ ] **Step 1: Make the script, installer and check changes**

- [ ] **Step 2: Static checks**

Run: `npm run check && npm run check:winui-release-hash`
Expected: all succeed. The release-hash check still finds `Get-Sha256Hash`.

- [ ] **Step 3: Release build with the packaged smokes.** This is the phase's release-build smoke run. It takes up to 10 minutes.
  - Do not run `smoke:winui-installer` or the upgrade smokes. They install over the user's real per-user SumaFile.

Run: `npm run release:build`
Expected, in this order:
- The smokes pass:
  - `smoke-winui-startup.ps1`, including `Bundled 7-Zip plugin trusted and registered.`
  - `smoke-winui-file-ops.ps1`, including `Payload 7-Zip plugin smoke passed.`
  - `smoke-winui-msi.ps1`, when the MSI was built.
- Then the script prints the `WinUI artifacts` table: portable zip, `-setup.exe` and `.msi`. NSIS and WiX v3.14 are installed locally; if either is missing, the script warns and skips that installer.
- `dist\winui\payload\tools\7zip` does not exist.

- [ ] **Step 4: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 13: End-to-end UI smoke for 7-Zip through the real host, service and backend

**Files:**
- Modify: `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs:228-296`. `SmokePluginsAsync` calls a new `SmokeSevenZipAsync(results, mode)` in the same file, then `SmokeSevenZipBackendLossAsync`. A final `SmokeServiceReconnectAsync` scenario runs just before the report is written.
- Modify: `src-winui/SimpleFile.Core/BackendSession.cs`: add `public int? ServiceProcessId => _service?.Id;`.
- Modify: `scripts/smoke-winui-ui.ps1`:
  - In both plugin modes, create `<testRoot>\archives\sample.7z` with `third_party\7zip\win-x64\7za.exe a -t7z`. It holds `docs\a.txt`, `café 資料\hello.txt` and `literal [brackets] & spaces.txt`. Export `SUMAFILE_SMOKE_ARCHIVES=<testRoot>\archives`.
  - With `-PluginsDir`, `SUMAFILE_PLUGIN_ROOTS` becomes `<exe dir>\plugins;<plugin roots>`. Only the `-PluginsDir` folders are pre-approved, so the bundled 7-Zip plugin must be trusted through `bundled.json`.
  - With `-PluginsDir`, the script throws `Bundled plugins missing from <exe dir>\plugins. Run scripts/build-plugins.ps1 and rebuild.` when `<exe dir>\plugins\bundled.json` is absent. The 7-Zip checks can never be skipped silently.
- Modify: spec §13: the UI smoke covers `.7z` with the plugin bundled in the install root, and without plugins.

**Interfaces:**
- Consumes the real chain end to end:
  - Task 11 staging, freshness check and App copy; Task 8 bundled trust.
  - Task 7 registration, re-registration and `Changed` ordering.
  - Tasks 1-6 engine path, including Task 6's pane-listing note.
  - Task 9 recognition, Task 10 Transfers rows, Task 5 `binaryOverride`.

Assertions in `SmokeSevenZipAsync`:

- `with` mode (`com.sumafile.sevenzip` is always required):
  1. `com.sumafile.sevenzip` is discovered from `<exe dir>\plugins`, is `Enabled` and reaches `BackendRunning` within 15 s. The disposable approvals file holds **no** record for it.
  2. The capabilities `.7z` row has engine `plugin:com.sumafile.sevenzip`, all four `Can*` true and `Note` null. `ArchivePaths.IsArchiveFile("x.7z")` is true.
  3. `ListArchiveAsync(sample.7z)` returns the three files with their exact names.
  4. Browse: `NavigatePaneAsync(Primary, sample.7z)` shows `docs`, `café 資料` and `literal [brackets] & spaces.txt`. Navigating into `sample.7z\docs` shows `a.txt`. Opening `sample.7z` from its parent folder through `OpenEntryAsync` navigates into it.
  5. Mutate: `FileOps.CreateFolderAsync(sample.7z, "smoke-folder")`, then a re-list shows `smoke-folder`.
  6. Create: `EnqueueArchiveTransfer` with `CreateArchiveTrackedAsync` of the fixture folder into `created.7z`. The row reaches `Completed` at 100%, and `ListArchiveAsync(created.7z)` lists the fixture.
  7. Extract: an `EnqueueArchiveTransfer` extract of `sample.7z` to `extracted\` reaches `Completed`, and the file bytes equal the fixture.
  8. Cancel from the Transfers window:
     - Write a 256 MiB random file. Start a tracked create of `big.7z` through `EnqueueArchiveTransfer`.
     - Once `OperationId` is set, call `_transfer.Cancel(row)`.
     - Within 30 s the row is `Cancelled`, `big.7z` does not exist, and no `.simplefile-new-` file remains.
  9. `binaryOverride` is honoured. Set `plugin.com.sumafile.sevenzip.binaryOverride` to `%SystemRoot%\System32\where.exe`; `ListArchiveAsync(sample.7z)` then throws or returns zero entries. Set it to `""`; the listing returns the fixture again.
- `none` mode:
  - The `.7z` row has engine `unavailable`, all `Can*` false and `Note == "7-Zip support requires the SumaFile 7-Zip plugin."`.
  - `ArchivePaths.IsArchiveFile("x.7z")` and `WorkspaceNavigationController.IsSupportedArchivePath("x.7z")` are false.
  - `ListArchiveAsync(sample.7z)` throws an `IpcException` whose message is the note.
  - `NavigatePaneAsync(Primary, sample.7z)` leaves `workspace.ErrorMessage` containing the note. The service side of this is Task 6's `dir_list` routing, already covered by its core and session tests.
  - The capabilities filtered by `CanCreate` exclude `7z`.
- `SmokeSevenZipBackendLossAsync` (`with` mode only) covers the fault case of Review Focus 2:
  - Navigate the primary pane to `sample.7z\docs`.
  - Kill the 7-Zip backend: the `sumafile-sevenzip-backend` process whose `MainModule.FileName` lies under `<exe dir>\plugins`.
  - Within 15 s, check three things with no user action:
    - the plugin is `Faulted`;
    - the `.7z` capabilities row has engine `unavailable` and the note;
    - the primary pane's `ErrorMessage` contains the note, because `RefreshArchivePanesAsync` re-listed it.
  - Then call `RestartBackendAsync("com.sumafile.sevenzip")`. Within 15 s `BackendRunning` is true and the pane lists `a.txt` again, also with no user action.
- `SmokeServiceReconnectAsync` (`with` mode only):
  - Kill `Process.GetProcessById(_backend.ServiceProcessId!.Value)`. Wait until `_backend.ReconnectCount` grows.
  - Then, within 30 s, the capabilities from the new client list `.7z` with engine `plugin:com.sumafile.sevenzip`, and `ListArchiveAsync(sample.7z)` succeeds.
- Each block adds `results.Add(new { scenario = "sevenzip", mode, passed = true, … })`, or `scenario = "sevenzip-backend-loss"` or `"service-reconnect"`.

- [ ] **Step 1: Implement the smoke checks, `ServiceProcessId` and the script changes**

- [ ] **Step 2: Build and run the plugin-mode smoke**

Run: `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1` first if the Hello fixture is not staged.
Expected: `UI runtime smoke passed.` `Completed scenarios` includes `plugins=1`, `sevenzip=1`, `sevenzip-backend-loss=1` and `service-reconnect=1`.

- [ ] **Step 3: Run the no-plugins smoke**

Run: `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`
Expected: `Assert-BundledPluginsFresh` passes on the copy Step 2 built, then `UI runtime smoke passed.` `Completed scenarios` includes `sevenzip=1`, from the none mode.

- [ ] **Step 4: Full suite**

Run: `npm run check && npm run check:rust && npm run check:winui`
Expected: all green.

- [ ] **Step 5: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

## Self-review notes

- **Spec coverage:**

  | Spec item | Tasks |
  | --- | --- |
  | §6.2 cancel sent by clients, flags visible to every connection | 3 |
  | §6.4 trait with mutate operations and the seven pipe methods | 1, 2, 3, 4 |
  | §6.4 capabilities merge and the plugin-absent note, in panes too | 6 |
  | §6.5 helper crate | 3 |
  | §9 trust for shipped plugins | 8, decision recorded in the spec |
  | §10 fault while a 7z pane is open | 6, 7, 9, 13 |
  | §10 engines unregistered and restored | 7 |
  | §10 timeouts | 4, 7 |
  | §11.1 folder, contributions, setting, no UI | 5 |
  | §11.1 core removal | 6 |
  | §11.1 packaging checks | 11, 12 |
  | §11.1 `tools/sevenz-evaluation` | 6 |
  | §11.4 payload and portable layout | 12 |
  | §12 layout `plugins/sevenzip/` and references | 5 |
  | §13 service tests, schema check, packaging check, UI smoke without plugins | 4, 6, 3, 11, 12, 13 |
  | §14 phase 2 ends green with a release-build smoke | 12, 13 |

  Phase 1 follow-ups closed:
  - re-registration after a service reconnect, the host-side registration timeout, bounded retry, and the backend kill not being awaited (7);
  - the late unregister after a restart: the service's registration ticket (4) and the generation-checked final unregister (7);
  - dispose-time cancellation for hung starts: the manager's dispose token (7);
  - approval-store I/O and temp-name hardening, env-override logging, and pre-approval with folder (8);
  - the env-var test race: the `Plugin environment` collection (8);
  - handshake reply id and SDK checks, `pluginId` grammar, protocol docs, and SDK constants in the schema check (3, 4);
  - progress and cancel from a pane (4, 10);
  - cancel-flag growth, now bounded by in-flight tracking and the 64-entry early-cancel list (3).
- **Decisions recorded in the spec:**
  - core-owned staging and limits, with engine-owned in-place atomic mutation;
  - `rename` to a full path, which also covers moves inside an archive;
  - `add` from a staging base folder, which also covers new folders and files;
  - `extract` no longer carries `innerPath`, replaced by `read_entry`;
  - SDK stays `1.0.0`, since nothing is published before phase 5;
  - the plugin version is independent;
  - `binaryOverride` accepts a file or a folder;
  - `bundled.json` replaces installer-written approvals;
  - plugin-format extract and create use Transfers rows;
  - the built-in formats keep their in-core paths (`ArchiveKind::BuiltIn`) and do not implement `ArchiveEngine`, which replaces §6.4's "The built-in formats implement it in-process" (Task 3);
  - clients, host or service, send `cancel`. Flags are backend-wide and live while their request runs (§6.2, Task 3);
  - the last `register_archive_engine` or `unregister_archive_engine` the service received for a plugin wins, through registration tickets (§6.4, Task 4);
  - every service handler that can reach an engine runs off the session loop (§6.4, Task 4).
- **Known gaps, kept as they are and noted in the spec:**
  - Paste and drag into any archive pane go through `copy_with_progress`, which rejects archive destinations for every format today. This is unchanged.
  - `batch_rename` inside any archive fails before any engine call (`Path does not exist`), as it does today for every format. Phase 2 adds no archive batch rename. Task 2 and Task 4 pin this behaviour.
  - Any approved plugin may register `.7z`; the latest registration wins.
  - Single-entry engine mutations and opens of archive entries share the General lane with previews and listings. Only extract, create and copies use the two Transfer permits.
  - The two smokes that install over the user's SumaFile, `smoke:winui-installer` and the upgrade smoke, gain their plugin checks in this phase. They first run in the later CI pass.

---

## Critique resolutions

Each finding from the plan critique, in the order it was raised. Duplicates point to the row that resolves them. No task was added or removed, so the numbering is unchanged.

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | Important | Without an engine the `.7z` note never reaches a pane. A pane listing (`list_directory_capturing_raw` with options) routes on `is_archive_path`, which is false for an unserved `.7z` and for paths inside it. | Task 6 adds `dir_list.rs` and `archive::unavailable_archive_note`. The options branch routes those paths to the archive listing, and the no-options branch already returns its error. Tests: core `seven_zip_listing_without_an_engine_reports_the_note_through_dir_list`, session `list_directory_on_an_unserved_7z_reports_the_plugin_note`, and `list_directory` cases in `sevenzip_plugin_unregistered_7z_reports_the_plugin_note`. |
| 2 | Important | After a fault, the capability refresh can read stale capabilities, because `OnBackendLost` raises `Changed` before its unregister runs. | Task 7 makes `OnBackendLost` raise `Changed` again once the unregister completes. Test: `ArchivePlugin_Lost_RaisesChangedAfterTheUnregisterCompletes`. Task 9 relies on this ordering, and Task 13 adds the `sevenzip-backend-loss` smoke. |
| 3 | Important | `open_file`, `reveal_in_folder` and `open_file_with` run on the session loop, so the 7-Zip settings lookup, answered over the same connection, stalls for 2 s. | Task 4 moves them to the General lane for archive entries. Tests: `archive_entry_shell_calls_and_mutations_run_on_the_general_lane` and `open_file_on_an_archive_entry_does_not_block_the_session_loop`. Task 6 adds `sevenzip_read_with_a_host_settings_lookup_completes_quickly`, which uses the new `FakeHost`. This is Review Focus 6. |
| 4 | Important | `read_entry` reports no progress, so a large solid archive can outlast the 120 s read idle deadline. | Task 5 gives `SevenZip::read_entry` an observer, and every operation with an `operationId` reports progress at least every 5 s. Task 3 states this in the protocol and makes Hello's `read_entry` slow on `__slow__`. Tests: Task 4 `slow_read_entry_progress_extends_the_read_deadline` and `read_entry_without_progress_times_out_at_the_read_deadline`; Task 5 `read_entry_reports_progress_through_the_observer`. |
| 5 | Important | Synchronous 7za waits can occupy every tokio worker. Cancels and new listings are then never read. | Task 5 runs every 7za process through `spawn_blocking` with a `ProgressBridge`, on a runtime with two workers. Tests: `handler_runs_7za_off_the_async_workers`, and Task 6 `sevenzip_plugin_answers_a_listing_while_two_long_creates_run_and_cancels_them`. |
| 6 | Important | The new tests share the process-wide engine registry with nothing serializing them. | A Global Constraint, plus Tasks 4 and 6. `support::REGISTRY_LOCK` is taken by every test in `plugin_archive.rs`, `hello_plugin.rs` and `sevenzip_plugin.rs`. Core `.7z` tests take `SEVEN_Z_REGISTRY_LOCK`. |
| 7 | Important | `npm run check` fails in Task 12, because `buildSnippets` still requires `third_party\7zip\win-x64` and `7za.exe`. | Task 12 drops both and keeps `tools\7zip`. Task 11's new `stagingSnippets` check the plugin folder's file list in `scripts/lib/plugin-staging.ps1`. |
| 8 | Minor | The new NSIS behaviour has no runtime check. | Task 12: `smoke-winui-nsis.ps1` and `smoke-winui-upgrade.ps1` run `Assert-BundledPlugins` and require `tools\7zip` to be gone. They are not run now; the later CI pass verifies them. |
| 9 | Minor | The packaged smokes do not prove bundled trust. | Task 7 logs `[info] Archive engines registered: …`. Task 12's startup smoke runs with a disposable approvals path and requires that line for `.7z`, with no approval record written. The MSI smoke runs `Assert-BundledPlugins` on the extracted image. |
| 10 | Minor | The final unregister skips the lock and the generation check, and a timed-out register can land after it. | Task 7 sends the final unregister through `UnregisterArchiveEnginesAsync(record, generation)`, before `RaiseChanged`. Test: `ArchivePlugin_FinalUnregisterAfterFailedAttempts_SkipsWhenANewerGenerationRegistered`. Task 4 adds registration tickets. Test: `register_that_completes_after_a_later_unregister_registers_nothing`. |
| 11 | Minor | Several engine-reaching service paths have no engine-format test. | Task 4 extends the Hello e2e with `get_folder_metrics`, `count_folder_items`, `get_file_metadata`, `compare_files` and a two-entry `batch_rename`. Open, reveal and open-with are covered by the lane test and the open-file e2e. `batch_rename` turned out to fail for every archive format before any engine call. The plan now states that, and Tasks 2 and 4 pin it, in place of the claim of one call per entry. |
| 12 | Minor | The cancel sentinel exists in three copies that nothing compares, and `archive.progress` is checked only in `host.rs`. | The protocol gains `cancel.cancelledMessage`. `check-ipc-schema.mjs` requires it in core and in the helper crate (Task 3) and in the C# `ArchiveCancelledMessage` (Task 10). It also requires `"archive.progress"` in `plugin_archive/connection.rs` (Task 4). |
| 13 | Minor | A cancel for a finished or unknown id stays in the shared state forever. | Task 3: the server tracks in-flight request ids and drops a flag when its handler returns. Any other cancel goes on a 64-entry early-cancel list, and `clear_cancelled` is gone. Tests: `cancel_flag_is_dropped_when_its_request_finishes`, `cancel_that_overtakes_its_request_still_cancels_it` and `cancels_for_unknown_or_finished_ids_are_bounded`. |
| 14 | Minor | No unit test parses the real `plugin.json` with the C# reader. | Task 5 Step 5: `PluginManifestReaderTests.SevenZipManifest_InTheRepo_IsValid`. |
| 15 | Minor | The plan does not record its departure from §6.4's "The built-in formats implement it in-process". | Task 3's §6.4 edit records it, and the self-review decision list includes it. |
| 16 | Minor | Provenance disagrees: 26.03 binaries, 26.06 source tarball, 26.02 parser fixture. | Task 12 aligns `SUMAFILE_BUNDLE.md` at 26.03 and adds a note on the 26.06 tarball. Task 5's parser port uses the real 26.03 header. |
| 17 | Minor | `SIMPLEFILE_7Z` users lose it silently. | Task 5 logs a one-time warning through `host.log`. Test: `legacy_simplefile_7z_env_logs_one_warning`. Task 6 adds a README note. |
| 18 | Minor | `RemoveKnownArchiveExtension` ignores engine extensions. | Task 9 strips registered engine extensions. `ArchivePaths_EngineFormatsAreRecognizedOnlyWhileRegistered` asserts `pack.zip` and `pack`. |
| 19 | Minor | `CopyBundledPlugins` can ship a stale backend into Debug output. | Task 11 adds `Assert-BundledPluginsFresh`. The UI smoke restages unless `-SkipBuild`, and checks freshness either way. |
| 20 | Important | Duplicate of 1: `dir_list.rs` is not in Task 6. | As 1. |
| 21 | Important | Duplicate of 6: the plan deletes `SEVEN_ZIP_TEST_LOCK`. | As 6. The lock is renamed to `SEVEN_Z_REGISTRY_LOCK` and repurposed, not deleted. |
| 22 | Important | Duplicate of 7. | As 7. |
| 23 | Important | Duplicate of 2. | As 2. |
| 24 | Minor | A backend exits when its client count reaches zero, and the test harness has no host connection to keep it alive. | Task 4: `launch_plugin` waits by connecting, then keeps that unhandshaken client open as a keep-alive. It never becomes `host_connection()`. |
| 25 | Minor | `cmd /c ping` leaves a grandchild that holds the output pipes. | Task 5 spawns `PING.EXE` directly. `RunObserver::spawned` supplies the pid for the `tasklist` check. |
| 26 | Minor | The "env-var collection" does not exist. | Task 8 creates `PluginEnvironmentTests.cs` in a `DisableParallelization` collection. It holds the moved env tests and `DescribeOverrides_ReportsActiveEnvironmentOverrides`. |
| 27 | Minor | §6.2 still says the host sends cancel, and the §6.4 built-ins decision is unrecorded. | Task 3 edits §6.2 and §6.4. |
| 28 | Minor | The RAR message still suggests 7z, and `events.json` lists a nonexistent `session.rs`. | Task 6 changes the RAR text and tests it. Task 4 replaces `session.rs` with `session/jobs.rs`. |
| 29 | Minor | Duplicate of 18. | As 18. |
| 30 | Minor | Reconnect can wait about 50 s for re-registration, and the retry path has no dispose token. | Task 7 runs re-registration in the background and raises `Changed` when it is done. The dispose token covers start and retry. Tests: `MainWindow_ReconnectDoesNotAwaitArchiveReregistration`, `ArchivePlugin_DisposeDuringRetryDelay_StopsRetrying` and `ArchivePlugin_ReregisterAfterServiceReconnect_RaisesChangedWhenDone`. Task 9 refreshes capabilities right after `ReplaceIpc`, so the later `Changed` re-lists `.7z` panes. |
| 31 | Minor | `support/mod.rs` duplicates the `plugin_archive.rs` harness, and the 2-permit Transfer lane queues in-archive mutations behind extracts. | Task 4 moves `Service`, `REGISTRY_LOCK` and `Unregister` into `support` and reuses them. Single-entry engine mutations use the General lane. |
| 32 | Minor | Task 12 Step 3 has the output order wrong. | Reworded: the smokes pass first, then the artifacts table prints. |
