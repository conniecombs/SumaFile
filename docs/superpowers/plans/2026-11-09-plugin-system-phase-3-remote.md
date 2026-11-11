# Plugin System Phase 3: FTP/SFTP Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move SumaFile's whole FTP/FTPS/SFTP feature out of core into the first-party plugin `com.sumafile.remote`: a Rust backend that owns profiles, credentials, sessions, both providers and the transfer engine, and a C# UI assembly built in code that contributes the FTP/SFTP manager window, the Connections sidebar section, a custom settings page and the `ftp-sftp-manager` command. Remote transfers become Transfers-window rows with Cancel. The host gains the sidebar-section slot, plugin-owned secondary windows, dialogs through the host gate and the custom settings page slot. Core loses the 13 remote IPC methods. Saved profiles and passwords keep working after the upgrade.

**Architecture:** The local-file transfer primitives the remote engine shares with local copies (staging, journal, retry, rate limit, policy model, path conflicts) move out of the service into a new leaf crate, `crates/sumafile-transfer`, so the plugin backend can reuse the hardened code without depending on host crates. The backend `plugins/remote/backend` (`sumafile-remote-backend.exe`, laid out like the 7-Zip backend) serves eleven plugin-owned methods over the plugin pipe, reports each transfer through `host.progress_*` and honours `cancel` for both the request id and the Transfers-row id. It keeps profiles in its own data folder, imports the 1.0.4 `remote_profiles` rows once per legacy database, read-only, and reads credentials in place from Windows Credential Manager. The SDK stays WinUI-free and hands UI objects across as `object` (the host checks the WinUI type), so the SDK, the plugin host and the test project keep `net10.0-windows`. Only the App, the Hello fixture and the remote plugin's UI assembly target `net10.0-windows10.0.19041.0`; the plugin projects reference the Windows App SDK and the other host-shared packages compile-only. A WinUI-free model assembly holds the remote view model, so its tests run in `SimpleFile.Tests`. A loopback SFTP test server, built on the `russh` and `russh-sftp` server modules already in `Cargo.lock`, drives the real stack end to end in Rust tests, a C# host integration test and the UI smoke.

**Tech Stack:** Rust 2021 (tokio, russh 0.63 + russh-sftp 3 client and server, suppaftp 12 with native-tls, rusqlite 0.41 bundled, windows-sys Credential Manager), C# / .NET 10 / WinUI 3 (Windows App SDK 2.4.0, compile-only in plugin projects), xUnit, PowerShell 5.1 scripts, Node checks, NSIS and WiX v3.14.

**Spec:** `docs/superpowers/specs/2026-11-07-plugin-system-design.md`. This plan implements §14 phase 3 and §11.2, and refines §4.4, §5.1, §5.2, §6.3, §7, §8.2, §11.4, §12 and §13. Phase 1 plan: `docs/superpowers/plans/2026-11-07-plugin-system-phase-1.md`. Phase 1 review notes and rulings: `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md`. Phase 2 plan: `docs/superpowers/plans/2026-11-08-plugin-system-phase-2-sevenzip.md`, reports in `.superpowers/phase-2-sevenzip/`. Code maps for this phase: `.superpowers/phase-3-remote/maps/{remote-rust.md, csharp-remote-ui.md, sdk-host-slots.md, ipc-packaging-ci.md}`.

## Global Constraints

Copied from the spec and the phase 1 and phase 2 plans; binding values are verbatim. Every task's requirements include this section.

- All projects inherit `TreatWarningsAsErrors`, `Nullable` and `RestorePackagesWithLockFile` from `Directory.Build.props`. After any project change, run `dotnet restore --force-evaluate` on the project and keep its `packages.lock.json`.
- Target frameworks (phase 3 ruling, replacing phase 1's "No new project references `Microsoft.WindowsAppSDK`"):
  - `SimpleFile.PluginSdk`, `SimpleFile.PluginHost`, `SimpleFile.Tests` and the new `SumaFile.Remote.Model` target `net10.0-windows`, so the test project can reference them.
  - `SimpleFile.App`, the Hello fixture (from Task 8) and the new `SumaFile.Remote` target `net10.0-windows10.0.19041.0`. The two plugin projects reference `Microsoft.WindowsAppSDK` `2.4.0` and `Microsoft.Windows.SDK.BuildTools` `10.0.28000.2526` with `ExcludeAssets="runtime"` and `PrivateAssets="all"`. They set `TargetPlatformMinVersion` `10.0.19041.0` and `EnableDynamicLoading` `true`. They do not set `UseWinUI`.
  - `SumaFile.Remote` also references `CommunityToolkit.Mvvm` `8.4.0` with `ExcludeAssets="runtime"` and `PrivateAssets="all"` (Task 11). Its window code uses view model types that derive from `ObservableObject`, and the Model's own `PrivateAssets="all"` reference does not flow to it; without the direct reference the build fails with CS0012.
  - `SimpleFile.Tests` reaches a 19041 project only through `ReferenceOutputAssembly="false" SkipGetTargetFrameworkProperties="true"`. A scratch probe confirmed during planning that this restores and builds offline with 0 warnings.
- Spec §5.3: compiled XAML from a plugin folder fails. Plugin UI is built in C# code or with `XamlReader.Load` strings. No plugin project contains a `.xaml` file, `{Binding}` or `x:Bind`.
- Spec §12: "they reference only the SDK package and the helper crate, never the host projects." This phase refines that rule in Task 4: first-party plugin crates may also depend on the shared leaf crate `sumafile-transfer`, and on `simplefile-ipc` as spec §6.2 allows. They never depend on `simplefile-core` or `simplefile-service`. Plugin C# projects reference only `SimpleFile.PluginSdk`, their own projects, and `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` and `CommunityToolkit.Mvvm` compile-only (`ExcludeAssets="runtime"`, `PrivateAssets="all"`). At run time those assemblies come from the host through `PluginLoadContext`'s shared prefixes. This is a decision for the user to confirm (Self-review notes).
- Spec §2: "The FTP/SFTP plugin carries its own Credential Manager code."
- Rust: workspace edition 2021. `cargo fmt --all -- --check`, `cargo test --locked --all-features` and `cargo clippy --locked --all-targets --all-features -- -D warnings` must pass.
- `npm run check` must stay green. `scripts/check-ipc-schema.mjs` asserts the exact domain method count: **99** until Task 14, then **86**. Spec §11.2: "`domainMethodCount` drops to 86 after the two archive-engine methods are added." `check-winui-parity-gate.mjs` follows the same count.
- Spec §5.2: "Manifest reading and contribution rendering happen at startup and must stay under 50 ms for ten plugins in the startup trace." The budgets in `StartupTimingBudget` and `scripts/check-winui-startup-budget.mjs` stay as they are. The `MainWindow.Connect.plugins-discovered` delta must stay under 50 ms.
- Plugin id grammar: `^[a-z0-9][a-z0-9.-]{1,63}$`. Plugin settings key grammar: `^[a-z0-9][a-zA-Z0-9_-]{0,63}$`. Settings persist as `plugin.<id>.<key>`.
- Plugin command ids are exposed to the host as `plugin:<id>:<command>`. The remote manager command is `plugin:com.sumafile.remote:ftp-sftp-manager`.
- Pipe name for a plugin backend: `SumaFile.Plugin.<sessionId>.<pid>.<pluginId>`. Connect timeout 5 s. Backend data directory `%LOCALAPPDATA%\SumaFile\plugins-data\<id>\`.
- Plugin roots in scan order: `<AppContext.BaseDirectory>\plugins`, then `%LOCALAPPDATA%\SumaFile\plugins`. A duplicate id resolves to the later (user) root. Approval store file: `%LOCALAPPDATA%\SumaFile\plugin-approvals.json`.
- Spec §9 bundled list: `{"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"<64 lowercase hex>"}]}`, UTF-8 without a BOM. The hash is the lowercase SHA-256 of the staged `plugin.json` bytes. Phase 3 lists two plugins.
- Spec §11.2 folder: `plugin.json`, `SumaFile.Remote.dll`, `sumafile-remote-backend.exe`. Task 11 adds `SumaFile.Remote.Model.dll` and `SumaFile.Remote.deps.json` and records that in the spec.
- Product name in user-facing text is "SumaFile". Internal namespaces keep `SimpleFile`; the plugin's namespaces are `SumaFile.Remote` and `SumaFile.Remote.Model`.
- `PluginSdk.Version` stays `"1.0.0"` (phase 2 decision: nothing is published before phase 5). The pinned snapshot `Fixtures/PluginSdkSurface.v1.txt` changes only in Task 7, deliberately.
- Error texts the UI matches by prefix stay byte-identical:
  - `SFTP host fingerprint is not trusted. Check that this fingerprint belongs to the server, then trust it to connect: <fp>`
  - `SFTP host fingerprint mismatch. The server key changed since you trusted it. Expected <x>, got <y>`
  - `remote session not found: <id>`
  - `Remote session is busy; wait for or cancel its transfer`
  - `Transfer cancelled`
- Credentials:
  - Keep `CREDENTIAL_TARGET_PREFIX` `SumaFile.Remote.`, `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`, the UTF-8 blob `"password" 0x00 <value>` or `"private-key-passphrase" 0x00 <value>`, and each profile's `credential_target`.
  - Tests and smokes never leave a `SumaFile.Remote.*` credential behind, and never delete one they did not write. There are four writers:
    - Task 5 `windows_store_round_trips_a_real_credential_and_deletes_it`, Task 6 `upgraded_profile_connects_with_its_saved_password` and Task 6 `backend_process_imports_a_1_0_4_profile_and_connects_with_its_saved_password`. Each uses its own `SumaFile.Remote.test-<random>` target and deletes it from a drop guard, so a failed assertion still removes it.
    - The Task 15 UI smoke, step 4 (`SaveSecret` on). Its `finally` deletes the target of the profile it saved, then reports any other new `SumaFile.Remote.*` target without deleting it.
- Test data never touches the user's data:
  - `SUMAFILE_PLUGIN_DATA_ROOT` (Task 8) points every UI smoke mode, plain mode included (Task 8), and the startup and MSI smokes (Task 12) at a disposable folder.
  - Every test or smoke that launches the App or the remote backend sets `SIMPLEFILE_METADATA_DB` to a disposable path: `none.db`, or a 1.0.4 database the test wrote. The App smokes also set `SIMPLEFILE_APP_DATA_DIR`, because the service otherwise takes the first existing `metadata.db` among its candidates. So the legacy import reads nothing real, and the settings seed writes nothing real.
  - The legacy import and the settings seed record their markers per legacy database (Tasks 5 and 6). A run against a disposable database therefore never stops the real one from being imported later.
- Workspace rules for this phase (user instruction):
  - Work only in `R:\Repos\SumaFile` on branch `plugin-system-phase-1`.
  - **Never commit.** Never run `git commit/stash/reset/checkout/switch/restore/rebase/merge/push/add/clean`. Never write to GitHub.
  - Every "commit" step in this plan is **Snapshot: run `bash .superpowers/tools/snap.sh`**. It prints a tree id and creates no commit.
  - Do not dispatch subagents from inside a task.
- NuGet is offline:
  - Every restore must work from `%USERPROFILE%\.nuget\packages`; `dotnet restore --force-evaluate` is allowed.
  - Never add a NuGet package that is not already in that cache. If one is truly needed, stop and report BLOCKED.
  - Packages this plan uses are all cached: `Microsoft.WindowsAppSDK 2.4.0`, `Microsoft.Windows.SDK.BuildTools 10.0.28000.2526`, `CommunityToolkit.Mvvm 8.4.0` and the xUnit set.
  - cargo may fetch crates. Never publish anything (`cargo publish` only with `--dry-run`).
- Lock files stay consistent with project files: every `packages.lock.json`, `Cargo.lock` and `tools/sevenz-evaluation/Cargo.lock`.
- Use generous timeouts: builds and smokes take up to 10 minutes.
- CI is not run in this phase (user ruling: CI fixes wait until every phase is done). Workflow edits are validated statically by `npm run check` and `npm run test:maintenance`. Never run `smoke:winui-installer` or the upgrade smokes; they install over the user's real SumaFile.

## Review Focus

The five inputs a real user will hit that the spec implies but no test pins yet. Each line names the task and test that pin it.

1. **A 1.0.4 user upgrades with saved profiles and saved passwords.** On first launch the Connections section lists the same profiles, and Connect works without asking for the password again. Pinned by:
   - Task 5 `import_copies_every_legacy_profile_once`, `import_never_writes_the_legacy_database` and `concurrent_imports_insert_each_profile_once`;
   - Task 6 `upgraded_profile_connects_with_its_saved_password`, which uses a real Credential Manager entry written as 1.0.4 wrote it, against the SFTP test server, and `legacy_markers_are_scoped_to_the_database_they_read`;
   - Task 6 `backend_process_imports_a_1_0_4_profile_and_connects_with_its_saved_password`. The real exe resolves the legacy database from its environment, imports the row verbatim, announces it, and connects with the saved password;
   - Task 13 `UpgradedProfile_IsImportedAndConnectsThroughTheRealHost`, in which the real `PluginManager` launches the backend with the host's environment;
   - Task 15 smoke step 2. The Connections section lists the profile the smoke seeded into its 1.0.4 metadata database.
2. **A large download over an existing local file is cancelled from the Transfers window.** The row shows Cancelling until the backend has stopped, then Cancelled. The existing local file is byte-for-byte unchanged, and no `.sumafile-partial` file is left. Pinned by:
   - Task 6 `cancel_by_progress_row_id_keeps_the_existing_destination_and_leaves_no_partial_file`;
   - Task 9 `WaitAsync_CancelThenFail_ReportsCancelledOnlyAfterThePluginFinished`;
   - Task 13 `CancelFromTheProgressRow_StopsTheDownloadAndKeepsTheExistingFile`;
   - Task 15 smoke step 8.
3. **The server's host key changes after the user trusted it.** Connect fails with the mismatch dialog naming both fingerprints, and never connects silently. Pinned by:
   - Task 5 `another_servers_fingerprint_fails_with_a_mismatch`;
   - Task 10 `ConnectSelectedProfileAsync_ReturnsAMismatchOutcomeWithBothFingerprints`.
4. **Unicode and punctuated names survive every operation.** Remote names such as `café 資料`, `literal [brackets] & spaces.txt` and `renamed folder [ok]` list, upload, download, rename and delete through SFTP. Pinned by:
   - Task 5 `sftp_lists_creates_renames_and_deletes_unicode_and_punctuated_names`;
   - Task 13 `UploadAndDownloadAppearAsProgressRowsAndComplete` (Unicode file name);
   - Task 15 smoke steps 5-9.
5. **The user disables the FTP/SFTP plugin while the manager window is open and a transfer runs.** Expected:
   - the window closes and the Transfers row ends as failed with `Plugin backend stopped.`;
   - the backend process exits, and the Connections section and the command disappear, with no hang;
   - the staged download it left behind is recovered by the next download into that folder.

   Pinned by:
   - Task 7 `Disable_ClosesPluginWindowsBeforeTheBackendStops`;
   - Task 6 `next_download_recovers_a_partial_left_by_a_killed_backend`;
   - Task 13 `Disable_WhileATransferRuns_StopsTheBackendAndEndsTheRow`;
   - Task 15 smoke step 10.

---

## File structure

**Created**

- `crates/sumafile-transfer/`: package `sumafile-transfer`, lib `sumafile_transfer`, `publish = false`. Modules:
  - `path_conflict.rs`, moved from core;
  - `reliability.rs`, `staging.rs`, `journal.rs` and `manifest.rs`, moved from the service's `transfer_*.rs`;
  - `policy.rs`, the `Policy` model from `progress/tuning.rs`;
  - `resources.rs`, from `progress/resources.rs`;
  - `paths.rs`, with `unique_destination_path`;
  - `ids.rs`, with `generate_transfer_operation_id`, whose body moves from core's `utils::generate_operation_id` (core then forwards to it);
  - `test_io.rs`, behind feature `test-support`.
- `plugins/remote/plugin.json` and `plugins/remote/protocol.json` (the plugin-owned method and event table).
- `plugins/remote/backend/`: package `sumafile-remote-backend`, lib `sumafile_remote_backend`, bin `sumafile-remote-backend`.
  - `src/{lib.rs, main.rs, models.rs, profiles.rs, legacy.rs, secrets.rs, path.rs, provider.rs, ftp.rs, sftp.rs, session.rs, transfer.rs, settings.rs, bridge.rs, handler.rs}`
  - `tests/{profiles.rs, secrets.rs, sessions.rs, legacy_import.rs, sftp_test_server.rs, handler.rs, transfers.rs, process.rs, manifest_defaults.rs}`
- `plugins/remote/test-server/`: package `sumafile-remote-test-server`, lib plus bin, `publish = false`, never staged. `src/{lib.rs, main.rs, sftp.rs, legacy.rs}` (`legacy.rs` writes 1.0.4 metadata databases for tests and smokes).
- `plugins/remote/ui/Directory.Build.props` and `Directory.Build.targets`, which import `src-winui`'s.
- `plugins/remote/ui/SumaFile.Remote.Model/` (`net10.0-windows`):
  - `SumaFile.Remote.Model.csproj`
  - `RemoteModels.cs`, `RemoteProtocol.cs`, `IRemoteBackend.cs`, `RemoteBackendClient.cs`, `LocalFolderSource.cs`
  - `RemoteManagerViewModel.cs`, `RemoteTransferRecord.cs`, `RemoteProfileRow.cs`, `RemoteTransferSettings.cs`
- `plugins/remote/ui/SumaFile.Remote/` (19041):
  - `SumaFile.Remote.csproj`, `RemotePlugin.cs`
  - `Views/{RemoteManagerWindow.cs, RemoteManagerWindow.Lists.cs, RemoteProfileDialog.cs, RemoteHostKeyDialog.cs, RemoteNameDialog.cs, RemoteTransferOptionsDialog.cs, RemoteSidebarSection.cs, RemoteSettingsPage.cs, RemoteAutomationIds.cs, Xaml.cs}`
- SDK: `src-winui/SimpleFile.PluginSdk/{ISidebarSectionProvider.cs, ISettingsPageProvider.cs, IPluginWindows.cs, PluginUiContracts.cs}`.
- Plugin host: `src-winui/SimpleFile.PluginHost/{IPluginUiThread.cs, IPluginWindowHost.cs}`.
- Core: `src-winui/SimpleFile.Core/{PluginSidebarSection.cs, PluginSidebarLayout.cs, PluginOperationWait.cs, PluginPageCache.cs}`.
- App: `src-winui/SimpleFile.App/{MainWindow.PluginSidebar.cs, PluginUiThread.cs, PluginWindowHost.cs, MainWindow.UiSmoke.Remote.cs}`.
- Hello fixture: `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/HelloUi.cs`.
- Tests (`src-winui/SimpleFile.Tests/`):
  - `PluginSidebarLayoutTests.cs`, `PluginOperationWaitTests.cs`, `PluginPageCacheTests.cs`
  - `Remote/{FakePluginHost.cs, FakeRemoteBackend.cs, RemoteManagerViewModelTests.cs, RemoteBackendClientTests.cs, LocalFolderSourceTests.cs, RemoteProfileRowTests.cs, RemotePluginSourceShapeTests.cs, RemoteBackendHarness.cs, RemotePluginBackendTests.cs}`
- Scripts: `scripts/test-plugin-staging.ps1`.

**Deleted (Task 14)**

- `crates/simplefile-core/src/remote/` and `crates/simplefile-core/tests/remote_profiles.rs`.
- `crates/simplefile-service/src/remote/` and `crates/simplefile-service/tests/{remote_secrets.rs, remote_session.rs}`.
- `src-winui/SimpleFile.App/{MainWindow.Remote.cs, RemoteManagerWindow.xaml, RemoteManagerWindow.xaml.cs}`.
- `src-winui/SimpleFile.Core/RemoteManagerViewModel.cs` and `src-winui/SimpleFile.Tests/RemoteManagerViewModelTests.cs`.

**Modified**, grouped by area (each task lists exact files):

- Helper crate: `crates/sumafile-plugin/src/host.rs`.
- Service: `src/{lib.rs, progress.rs, progress/tuning.rs, progress/plan.rs, dispatch/*, session/*}` and `Cargo.toml`. Core: `src/{lib.rs, utils.rs}` and `Cargo.toml`. Root `Cargo.toml` and `Cargo.lock`, and `tools/sevenz-evaluation/Cargo.lock`.
- Plugin host:
  - `PluginManager.cs`, `PluginManager.Backend.cs`, `PluginRuntime.cs`, `PluginHostServices.cs`, `PluginRecord.cs`
  - `Loading/PluginLoadContext.cs`
  - `Discovery/{PluginDiscovery.cs, DiscoveredPlugin.cs}`
  - `Trust/{BundledPluginList.cs, PluginApprovalStore.cs}`
  - `Manifest/{PluginManifest.cs, PluginManifestReader.cs}`
  - `Contributions/PluginContributionAdapter.cs`
- SDK: `IPluginHost.cs`, `IPluginDialogs.cs`.
- App:
  - `SidebarView.xaml(.cs)`, `MainWindow.xaml.cs`, `MainWindow.Plugins.cs`, `MainWindow.UiSmoke.cs`
  - `SettingsWindow.Plugins.cs`, `SettingsWindow.xaml(.cs)`, `SettingsWindow.Tools.cs`
  - `PluginSettingsPanelBuilder.cs`, `PluginDialogs.cs`, `PluginProgressSink.cs`, `TransferOptionsDialog.cs`, `TransferProgressWindow.xaml(.cs)`
  - the remote removals of Task 14
- Core and IPC: `UiSettings.cs`, `WorkspaceSettingsStore.cs`, `SavedWorkspaceLayout.cs`, `TransferManagerViewModel.cs`, `FileOperationService.cs`, `AppCommandCatalog.cs`, `CommandIds.cs`, `CommandSurfaceLayout.cs`, `ToolbarOverflowPlanner.cs`, `ContextMenuBuilder.cs`; `SimpleFile.Ipc/{ISimpleFileIpc.cs, Models.cs}` and the generated bindings.
- Schema and checks:
  - `ipc/schema/v1/{commands.json, types.json}`
  - `scripts/{generate-ipc-bindings.mjs, check-ipc-schema.mjs, check-winui-parity-gate.mjs, check-winui-packaging.mjs, check-winui-startup-budget.mjs}`
- Packaging and smokes:
  - `scripts/lib/plugin-staging.ps1`
  - `scripts/{build-plugins.ps1, build-fixture-plugin.ps1, build-winui-release.ps1}`
  - `scripts/{smoke-winui-ui.ps1, smoke-winui-startup.ps1, smoke-winui-file-ops.ps1, smoke-winui-msi.ps1, smoke-winui-nsis.ps1, smoke-winui-upgrade.ps1}`
  - `packaging/winui/sumafile-winui.nsi`, `.github/workflows/ci.yml`, `src-winui/SimpleFile.sln`, `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj`
- Docs:
  - the spec sections listed above;
  - `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md`;
  - `docs/winui-migration/{parity-gate.md, inventory.md}`, `README.md`, `docs/SECURITY.md`, `docs/TRANSFER_TUNING.md`;
  - `ipc/schema/plugin/README.md` (plugin-owned method tables, Task 6).

## Cross-layer wiring ledger

Phase 1's critical defect was a producer nothing consumed. Every producer in this phase has a consumer task and a proof.

| Producer (task) | Consumer wired in | Proof |
| --- | --- | --- |
| `progress_begin` that never leaks an id after close (T1) | Remote transfer bridge (T6) | T1 unit tests; T6 bridge tests |
| `Write-BundledPluginList`, multi-plugin `Assert-BundledPlugins` (T2) | `Install-BundledPlugins` with two plugins (T12): dev, smoke and release staging | T2 `scripts/test-plugin-staging.ps1`; T12 `build-plugins.ps1` and the release build |
| BOM-tolerant `BundledPluginList.Read` (T2) | `PluginManager.Evaluate` (existing) | T2 C# tests |
| `PluginLoadContext.LoadUnmanagedDll` and shared WinUI names (T3) | Loading `SumaFile.Remote.dll` (T11) and the Hello fixture's WinUI code (T8, T9) | T3 tests; T8, T9, T12 (remote load) and T15 smokes |
| `DiscoveredPlugin.ManifestHash`, one approvals read per scan (T3) | `PluginManager.Evaluate` (T3) | T3 tests, including `Discover_TenPlugins_StaysUnderTheStartupBudget`; budget check in T3 and T15; the CI budget step (T3, static check only) |
| `sumafile-transfer` (T4) | The service (T4); the remote engine (T6) | T4 suites; T6 engine tests |
| `ProfileStore`, `import_legacy_profiles`, `WindowsRemoteSecretStore` (T5) | `RemoteHandler` lazy init and profile methods (T6) | T5 tests; T6 `first_call_imports_legacy_profiles_and_announces_them`, `upgraded_profile_connects_with_its_saved_password`, `backend_process_imports_a_1_0_4_profile_and_connects_with_its_saved_password`; T13 `UpgradedProfile_IsImportedAndConnectsThroughTheRealHost`; T15 smoke step 2 |
| SFTP test server (T5) | Backend tests (T5, T6), the C# host test (T13), the UI smoke (T15) | Those tests |
| `write_legacy_metadata_db` and the test server's `--legacy-db` (T5) | Backend legacy tests (T5, T6); the C# harness (T13); the UI smoke script (T15) | T6 process test; T13 legacy test; T15 smoke step 2 |
| Eleven backend methods, three events and five error texts in `plugins/remote/protocol.json` (T6) | `RemoteBackendClient` (T10) | T6 process test; T10 client tests; T13 real-backend tests; `check-ipc-schema.mjs`: method and event literals on both sides (T6 backend, T10 client), error texts in `sftp.rs`, `session.rs` and `handler.rs` (T6) |
| `host.progress_*` per transfer (T6) | `PluginHostMethods` → `PluginProgressSink` rows (existing); cancel-aware rows (T9) | T6 fake-host tests; T13 recording sink; T15 smoke |
| `cancel` honoured for the request id and the row id (T6) | `RemoteBackendClient.TransferAsync` (T10); Transfers-row Cancel → `SendCancel` (existing) | T6; T10; T13; T15 |
| Transfer settings keys read by the backend (T6) | Manifest declarations (T11); Settings > Plugins controls (existing); `RemoteTransferOptionsDialog` (T11) | T11 manifest test, `manifest_defaults_match_the_engine_defaults` and `RemoteTransferSettings_DefaultsMatchTheManifest`; T13 `TransferHonoursThePluginBandwidthSetting`; T15 smoke |
| SDK `ISidebarSectionProvider`, `ISettingsPageProvider`, `IPluginWindows`, `IPluginDialogs.ShowDialogAsync` (T7) | App slots (T8, T9); Hello (T8, T9); remote UI (T11) | T7 manager tests; T8 and T9 smokes; T15 |
| `PluginManager.CreateSidebarSectionAsync` (T7) | `MainWindow.PluginSidebar` (T8) | T7 tests; T8 smoke |
| `PluginManager.CreateSettingsPageAsync` (T7) | The Settings > Plugins presenter (T9) | T7 tests; T9 smoke |
| `IPluginWindowHost.CloseAllAsync` during teardown and dispose, bounded by `WindowCloseTimeout` (T7) | `PluginWindowHost` (T9); `MainWindow.OnClosed` closes plugin windows first (T9) | T7 `Disable_ClosesPluginWindowsBeforeTheBackendStops`, `Dispose_ClosesPluginWindowsBeforeTheBackendStops`, `Teardown_WindowsThatNeverClose_AreAbandonedAfterTheTimeout`; T9 `OnClosed_ClosesPluginWindowsBeforeTheSessionCleanup`; T9 and T15 smokes |
| `HelloWindow`, a code-built `Window` subclass with a `XamlReader` fragment and a dialog on its own `XamlRoot` (T9) | `PluginWindowHost`, `PluginDialogs.ShowDialogAsync` (T9) | T9 smoke steps 1 and 2 |
| `IPluginUiThread` (T7) | `PluginUiThread` (T8) | T7 tests; smokes |
| `[info] Discovered: <State>` lines after the discovery mark (T3) | Startup smoke (T12) | T12 release build |
| `SUMAFILE_PLUGIN_DATA_ROOT` (T8) | The UI smoke in every mode (T8); the startup and MSI smokes (T12) | T8 environment test and smoke check; T12 remote load smoke (`remote-profiles.db` under the disposable root) |
| `PluginOperationWait` (T9) | `PluginProgressSink` (T9) | T9 tests; T15 smoke step 8 |
| `TransferOperationViewModel.CanChangeOptions` (T9) | The Transfers window's per-task Options button (T9) | T9 `ExternalOperation_OffersNoPerTaskOptions`; T15 smoke steps 6 and 8 |
| `RemoteBackendClient`, `RemoteManagerViewModel` (T10) | Remote UI assembly (T11); the C# host test (T13) | T10 tests; T13; T15 |
| Remote manifest contributions (T11) | Adapter (T7); App slots (T8, T9); command routing (existing `plugin:` prefix) | T11 manifest test; T15 smoke |
| Staged `com.sumafile.remote` (T12) | App Debug output, the release payload (T12); the C# host test (T13); the UI smoke (T12 load check, T15) | T12 asserts and remote load smoke; T13; T15 |
| Core removal (T14) | The no-plugin smoke (T15) | T14 source-shape tests; T15 `none` mode |

---

### Task 1: Verify the phase 2 tree, stop progress ids from leaking after a close, and close the phase 1 notes phase 2 resolved

**Files:**
- Modify: `crates/sumafile-plugin/src/host.rs:279-288` (`begin_progress`) and `:490-506` (`progress_begin`). Add a `#[cfg(test)] mod tests` at the end of the file.
- Modify: `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md`.

**Interfaces:**
- Produces, in `crates/sumafile-plugin/src/host.rs`:
  - `fn begin_progress(&self, operation_id: &str) -> bool` returns false and changes nothing once `close()` has run. Today it returns `()`.
  - It checks `pending.closed` while it holds the `progress` lock. `close()` sets `closed` before it drains `progress`. So a begin either sees `closed`, or is drained by `close`.
  - `progress_begin` returns `Err("The host connection is closed".to_string())` when `begin_progress` returns false.
  - Nothing else in the public API changes.

- [ ] **Step 1: Re-verify the phase 2 fix wave (carry-over g)**

Run, in this order:
- `cargo test --locked -p sumafile-sevenzip-backend`
- `cargo test --locked -p simplefile-service --test sevenzip_plugin --test hello_plugin --test plugin_archive`

Expected: `test result: ok` for every target, with these counts at the time of writing:
- the backend lib 16 (`binary` 1, `entries` 3, `handler` 5, `ops` 4 including the two `cfg(windows)` junction tests, `run` 3), `process` 1 and `real_7za` 25;
- `sevenzip_plugin` 6, `hello_plugin` 8 and `plugin_archive` 12.

A different count is not by itself a deviation. Compare it with the test functions in the sources and note it in the task report. If anything fails, fix it test-first before Step 2, and record it under "Deviations" in the task report.

- [ ] **Step 2: Write the failing unit tests** in the new `host.rs` test module.

```rust
#[test] fn begin_progress_after_close_holds_nothing_in_flight()
// client = HostClient::pipe(tx, state.clone()); client.close();
// assert!(!client.begin_progress("op-1")); assert_eq!(client.cancel_flag_count(), 0)

#[tokio::test(flavor = "current_thread")]
async fn progress_begin_answered_after_its_connection_closed_fails_and_holds_nothing()
// spawn progress_begin("t"); yield_now; read the request frame from rx and take its id;
// client.complete(id, Ok(json!({"operationId":"op-9"}))); client.close();
// result == Err("The host connection is closed"); cancel_flag_count() == 0; !is_cancelled("op-9")
```

- [ ] **Step 3: Run them to verify they fail**

Run: `cargo test --locked -p sumafile-plugin -- begin_progress_after_close progress_begin_answered_after`
Expected:
- The first test fails to compile, because `begin_progress` returns `()`.
- Once it compiles, both FAIL. The second returns `Ok("op-9")` with one flag held.

- [ ] **Step 4: Implement the bool-returning `begin_progress` and the error in `progress_begin`**

- [ ] **Step 5: Run the crate suite**

Run: `cargo test --locked -p sumafile-plugin` then `cargo clippy --locked -p sumafile-plugin --all-targets --all-features -- -D warnings`
Expected: `test result: ok` (19 roundtrip tests plus the 2 new ones); clippy clean.

- [ ] **Step 6: Close the phase 1 items phase 2 resolved (carry-over e)**

Derive the list from the eight bullets under "Phase 1 follow-ups closed" in the Self-review notes of `docs/superpowers/plans/2026-11-08-plugin-system-phase-2-sevenzip.md`. For each bullet, mark every review-notes line it closes. Append ` — **Closed in phase 2** (phase 2 plan, "Phase 1 follow-ups closed": <the bullet in a few words>).`

| Phase 2 bullet | Review-notes lines to mark |
| --- | --- |
| Re-registration after a service reconnect, the host-side registration timeout, bounded retry, and the backend kill not being awaited | Minor 8 (`DisposeAsync` now waits up to 2 s for the killed backend); "Fix early in phase 2", Task 6 "Kill not awaited before relaunch"; ledger Task 6, "Kill not awaited before relaunch"; "Parked after the fix wave": no host-side `RegisterAsync` timeout, one transient registration failure faults the plugin for good, engines not re-registered after a service reconnect |
| The late unregister after a restart (registration tickets and the generation-checked final unregister) | "Parked after the fix wave": the late unregister after restart |
| Dispose-time cancellation for hung starts | ledger Task 9, "dispose-time cancellation for hung starts" |
| Approval-store I/O and temp-name hardening, env-override logging, pre-approval with folder | "Fix early in phase 2": Task 4a approval store, Task 16 env overrides; ledger Task 4a, the transient read `IOException` and the fixed `.tmp` name; ledger Task 16, overrides active in release builds; "Parked after the fix wave": installer pre-approval must write Folder (replaced by `bundled.json`); "Rulings review", the 4a/4b follow-ups that were "still open" |
| The env-var test race, now the `Plugin environment` collection | ledger Task 8, "process-wide env var in tests"; ledger Task 16, "env-var tests not in a Collection"; "Can wait", the Task 16 env-var collection |
| Handshake reply id and SDK checks, `pluginId` grammar, protocol docs, SDK constants in the schema check | "Fix early in phase 2": the Task 13 handshake reply checks, with the thread-name sanitising through the id grammar; ledger Task 13, "handshake reply sdkVersion unchecked" and "thread name panics on interior NUL in plugin_id"; Minor 2, second half only (below) |
| Progress and cancel from a pane | "Rulings review", host.* fails fast: "Phase 2 has to decide how a 7-Zip extract from a pane reports progress" |
| Cancel-flag growth | ledger Task 14, "cancel flags never cleared" |

Three lines get a different mark:
- Minor 2: ` — Second half closed in phase 2 (SDK_VERSION and PLUGIN_SDK_VERSION are in check-ipc-schema.mjs). The first half stays open: PluginBackendHost.cs:163 still compares majors with Version.TryParse.`
- Minor 5, and ledger Task 16's "README+protocol.json lack param types…": ` — **Partly closed in phase 2** (shapes, error codes, command line, lifetime and service mode are documented). Phase 3 Task 6 adds plugin-owned method tables.`
- "Parked after the fix wave", "synchronous hang in DeactivateAsync not bounded": ` — **Closed after the review** by commit fecf075 (DeactivateAsync runs on the thread pool before the bounded wait).` Phase 2 did not close it.

Add a section `## Phase 3 closures`. It lists:
- carry-over (b), closed by this task;
- the items later tasks close, each as `Open: closed by phase 3 Task <n>` until that task replaces the line:
  - the Disabled→LoadFailed overwrite (Task 3) and `LoadUnmanagedDll` (Task 3);
  - Minor 1, the threading contract (Tasks 7 and 8);
  - the ledger Task 16 "smoke writes plugin data … to real %LOCALAPPDATA%", for plugin data (Tasks 8 and 12). `plugins.log` and `startup-timing.log` stay in `%LOCALAPPDATA%\SumaFile`, where the startup checks read them.
- `EnsurePrimaryItem` re-adding a removed button stays parked for phase 4. The remote plugin's toolbar button uses `overflow`, so this phase does not touch it.

- [ ] **Step 7: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 2: Bundled trust for more than one plugin: a valid multi-entry `bundled.json` and a BOM-tolerant reader

**Files:**
- Modify: `scripts/lib/plugin-staging.ps1`: a plugin table instead of the single `$script:SevenZipPluginId`.
- Modify: `scripts/build-plugins.ps1:33` (message lists every id).
- Create: `scripts/test-plugin-staging.ps1`.
- Modify: `.github/workflows/ci.yml` (winui job, after "Build and stage the bundled plugins": step `Test plugin staging` running `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1`).
- Modify: `scripts/check-winui-packaging.mjs`: `requiredFiles` gains `scripts/test-plugin-staging.ps1`; `workflowSnippets` gains `['ci.yml', ciYml, 'test-plugin-staging.ps1']`.
- Modify: `src-winui/SimpleFile.PluginHost/Trust/BundledPluginList.cs:47-56`.
- Test: `src-winui/SimpleFile.Tests/PluginTrustTests.cs`.

**Interfaces:**
- Produces, in `scripts/lib/plugin-staging.ps1` (dot-sourced; existing names kept):
  - `$script:BundledPluginIds = @('com.sumafile.sevenzip')`. Task 12 appends `com.sumafile.remote`.
  - `Get-BundledPluginSources -Id <string> -Configuration <Debug|Release>` returns an ordered map from staged name to source path. Unknown id → throw `Unknown bundled plugin <id>.` The 7-Zip branch is today's `Get-SevenZipPluginSources`.
  - `Get-BundledPluginFreshnessFiles -Id <string>` returns the staged names `Assert-BundledPluginsFresh` compares. For 7-Zip: `sumafile-sevenzip-backend.exe` and `plugin.json`.
  - `Write-BundledPluginList -Root <dir> -PluginIds <string[]>` writes `<Root>\bundled.json`:
    - `{"plugins":[` + one `{"id":"<id>","manifestSha256":"<hash of <Root>\<id>\plugin.json>"}` per id, joined with `,` in the given order + `]}`;
    - UTF-8 without a BOM.
  - `Assert-BundledPluginList -Root <dir> -PluginIds <string[]>` parses with `ConvertFrom-Json`. It checks in this order and throws at the first failure:
    1. every id is listed, in `-PluginIds` order: `<path> does not list <id>.`
    2. every listed hash matches, in the same order: `<path> lists manifest hash '<h>' for <id>, but the staged plugin.json hashes to <a>.`
    3. the count: `<path> lists <n> plugins; expected <m>.`
  - `Install-BundledPlugins -Root -Configuration` stages every id in `$script:BundledPluginIds`, then calls `Write-BundledPluginList`.
  - `Assert-BundledPlugins -Root` checks, for every id, its staged files and its manifest id, then calls `Assert-BundledPluginList`.
  - `Assert-BundledPluginsFresh -Root -Configuration` throws `Bundled plugin <id> in <Root> is stale. Run scripts/build-plugins.ps1 and rebuild.`
- `scripts/test-plugin-staging.ps1` prints `Plugin staging tests passed.` and exits 0, or throws.
- `BundledPluginList.ReadEntries` skips a leading UTF-8 BOM (`EF BB BF`) before `JsonDocument.Parse`.

- [ ] **Step 1: Write the failing staging test script.** `scripts/test-plugin-staging.ps1` dot-sources the library. In a fresh temp root it writes `com.example.one\plugin.json` and `com.example.two\plugin.json` with different bytes. Then it asserts, in order:
  1. `Write-BundledPluginList -Root $tmp -PluginIds @('com.example.one','com.example.two')` writes a file whose first three bytes are not `EF BB BF`.
  2. `ConvertFrom-Json` parses it into exactly two entries, in order, each hash equal to `Get-PluginStagingSha256`.
  3. `Assert-BundledPluginList` passes.
  4. After rewriting `com.example.two\plugin.json`, it throws a message containing `hashes to`.
  5. After `Write-BundledPluginList` runs again for the two ids (so case 4's stale hash is gone), `-PluginIds @('com.example.one','com.example.two','com.example.three')` throws `does not list com.example.three`. Presence is checked before hashes and the count.

It removes the temp root in `finally`.

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1`
Expected: FAIL, `The term 'Write-BundledPluginList' is not recognized`.

- [ ] **Step 3: Generalise the library as above.** Keep `Get-SevenZipPluginSources` as the 7-Zip case. Change `build-plugins.ps1`'s message to `Bundled plugins verified: <ids joined with ", ">`.

- [ ] **Step 4: Run the script and the real staging**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1`, then `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1`
Expected: `Plugin staging tests passed.`, then `Bundled plugins verified: com.sumafile.sevenzip`.

- [ ] **Step 5: Write the failing C# tests** in `PluginTrustTests.cs`:

```csharp
[Fact] public void BundledList_WithUtf8Bom_ListsItsPlugins()          // bytes EF BB BF + {"plugins":[{"id":"com.a.b","manifestSha256":"<64 hex>"}]} -> Read(root) has com.a.b
[Fact] public void BundledList_WithTwoPlugins_ListsBoth()              // two entries -> both ids with their lowercase hashes
[Fact] public async Task BundledPlugins_TwoListedPlugins_AreBothEnabledWithoutConsent() // two plugin folders in BundledRoot, both listed -> both Enabled; approvals file not created
```

- [ ] **Step 6: Run them to verify they fail, implement the BOM skip, run again**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginTrustTests"`
Expected: `BundledList_WithUtf8Bom_ListsItsPlugins` FAILS before the change (the list is empty). After it, every `PluginTrustTests` test passes.

- [ ] **Step 7: Static checks**

Run: `npm run check && npm run test:maintenance`
Expected: `WinUI packaging surface is wired.` among the success lines; maintenance tests all pass.

- [ ] **Step 8: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 3: Plugin host hardening: Disabled stays Disabled, native DLLs and WinUI types from the right place, and cheaper discovery

**Files:**
- Modify: `src-winui/SimpleFile.PluginHost/PluginManager.cs`:
  - `:411-427`, `MarkLoadFailedAsync`;
  - `:278-301`, `DisableAsync` gains a test seam;
  - `:175-219`, `Evaluate` uses the discovered hash and one approvals snapshot.
- Modify: `src-winui/SimpleFile.PluginHost/Loading/PluginLoadContext.cs`.
- Modify: `src-winui/SimpleFile.PluginHost/Discovery/{PluginDiscovery.cs, DiscoveredPlugin.cs}`.
- Modify: `src-winui/SimpleFile.PluginHost/Trust/PluginApprovalStore.cs`: a snapshot API and source-generated JSON.
- Modify: `src-winui/SimpleFile.PluginHost/Manifest/PluginManifestReader.cs`: `[GeneratedRegex]` for the id and semver patterns; the class becomes `partial`.
- Modify: `src-winui/SimpleFile.App/MainWindow.Plugins.cs:41-45`. After `timer.Mark("plugins-discovered")` it writes the `DescribeOverrides` lines, then one `AppendPluginLog(record.Id, $"[info] Discovered: {record.State}")` per discovered plugin. Task 12's startup smoke reads that line as its proof of bundled trust.
- Modify: `scripts/check-winui-startup-budget.mjs`: also print `plugins-discovered delta <x> ms`.
- Modify: `.github/workflows/ci.yml`, winui job: a step `Check the WinUI startup budget` running `npm run check:winui-startup-budget`. It goes directly after the `-PluginsDir` UI smoke and before the `-NoPlugins` one, so it reads the session that had plugins. `scripts/check-winui-packaging.mjs` `workflowSnippets` gains `['ci.yml', ciYml, 'check:winui-startup-budget']`. CI is not run in this phase; `npm run check` validates the wiring.
- Test: `src-winui/SimpleFile.Tests/{PluginManagerTests.cs, PluginLoadingTests.cs, PluginDiscoveryTests.cs, PluginTrustTests.cs}`.
- Modify: spec §5.2 (shared names and the native rule); the review notes' Phase 3 closures (Disabled→LoadFailed, `LoadUnmanagedDll`).

**Interfaces:**
- `MarkLoadFailedAsync` returns at once unless `ReferenceEquals(record.Runtime, runtime) && record.State == PluginState.Enabled`.
- Test seam: `internal Func<Task>? AfterDisableStateSet { get; set; }` on `PluginManager`. `DisableAsync` awaits it between the lock section that sets Disabled and `TeardownAsync`.
- `PluginLoadContext`:
  - `internal PluginLoadContext(string pluginFolder, string mainAssemblyPath, string hostDirectory)`. The public constructor passes `AppContext.BaseDirectory`.
  - `internal static bool IsHostShared(string assemblyName)`. `SharedPrefixes` adds `"Microsoft.WinUI"`, `"Microsoft.InteractiveExperiences"`, `"Microsoft.Graphics"`, `"Microsoft.Web.WebView2"`, `"Microsoft.WindowsAppRuntime"` and `"Microsoft.Security.Authentication.OAuth"`.
  - `internal string? ResolveUnmanagedPath(string unmanagedDllName)`:
    - returns null when `<hostDirectory>\<name>` or `<hostDirectory>\<name>.dll` exists, so the host's copy wins;
    - otherwise returns `_resolver.ResolveUnmanagedDllToPath(name)`.
  - `protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)` returns `LoadUnmanagedDllFromPath(path)` for a resolved path, else `IntPtr.Zero`. `internal IntPtr LoadUnmanagedForTest(string name)` calls it.
- `DiscoveredPlugin` gains a trailing `string? ManifestHash = null`. `PluginDiscovery.Scan` reads each `plugin.json` once as bytes, hashes those bytes (lowercase hex SHA-256, as `PluginPackageHash`), and decodes them as UTF-8 with any BOM removed.
- `PluginApprovalStore`:
  - `internal ApprovalSnapshot Snapshot()` (records plus an unreadable flag);
  - `Resolve(string pluginId, string manifestHash, SignatureStatus signature, string folder, ApprovalSnapshot snapshot)` and `StoredState(string pluginId, string folder, ApprovalSnapshot snapshot, out bool unreadable)`;
  - `internal int LoadCount` (for tests);
  - (de)serialization through `internal partial class PluginApprovalJsonContext : JsonSerializerContext` with `UseStringEnumConverter = true` and `WriteIndented = true`. The file format is unchanged.
- `ScanAsync` takes one snapshot per scan.
- `PluginManager` gains `internal PluginApprovalStore Approvals => _approvals;`, so tests can read `LoadCount`. The plugin host already exposes its internals to `SimpleFile.Tests`.
- Budget fallbacks are decided now, so an over-budget result needs no new design.
  - Triggers: the Step 5 baseline (two plugins, cold) is above 25 ms; the ten-plugin test's best run is above 25 ms; or Task 15's three-plugin delta is above 50 ms.
  - First, profile the span with `StartupTimer` marks around the scan, `Evaluate` and `ApplyPluginContributions`. Then apply the fallback that matches the cost, F1 before F2.
  - F1, a hash cache, when hashing dominates. Signed plugins hash every file `signature.json` lists.
    - `internal sealed class PluginHashCache` in `Trust/` maps a full path, its length and its `LastWriteTimeUtc` to the lowercase SHA-256.
    - It is loaded once per scan from `plugin-hash-cache.json` beside the approvals file, so the smokes' approvals override keeps it disposable. It is saved after the scan only when it changed.
    - `PluginSignatureVerifier.Verify` takes it as an optional parameter.
    - Tests: `Verify_WithAWarmHashCache_ReadsNoListedFile` and `Verify_AFileWhoseWriteTimeChanged_IsHashedAgain`.
    - The cache lives in the same user-writable folder as the approvals file, so it adds no new trust.
  - F2, overlap, when cold start dominates (type loading, JSON and regex setup, the thread-pool hop).
    - `MainWindow.ConnectAsync` starts `PluginManager.PrepareScan(roots, bundledRoot, approvalsPath)` on the thread pool at `begin`, before `backend.StartAsync()`. It runs `PluginDiscovery.Scan`, reads `bundled.json` and takes the approvals snapshot.
    - `DiscoverAsync(PreparedScan? prepared, CancellationToken ct)` evaluates from it. The `plugins-discovered` span then holds only evaluation and contribution rendering. Contributions are still applied before the workspace loads its layout (phase 1 ruling).
    - Test: `Discover_WithAPreparedScan_ReadsNothingAgain`. The store's `LoadCount` stays 1, and no manifest is read twice.
  - Never raise the budget. If both fallbacks leave the delta above 50 ms, record the per-stage profile in the task report and stop for a ruling.

- [ ] **Step 1: Write the failing tests**

```csharp
// PluginManagerTests
[Fact] public async Task RunCommand_LoadFailureWhileDisabling_LeavesThePluginDisabled()
// InstanceFactory blocks on a gate, then throws; AfterDisableStateSet releases the gate and awaits the command's failure;
// after DisableAsync: State == Disabled, Error == record.Signature.Error, approvals file says Disabled

// PluginLoadingTests
[Fact] public void LoadContext_LoadsANativeDllListedInThePluginDepsJson()
// plugin folder: Hello dll copy + its deps.json rewritten so the main project library lists the native asset sumafile_native_probe.dll,
// a copy of %SystemRoot%\System32\version.dll in the folder root; ResolveUnmanagedPath("sumafile_native_probe") == that full path;
// LoadUnmanagedForTest returns non-zero (freed with NativeLibrary.Free)
[Fact] public void LoadContext_LeavesANativeDllTheHostShipsToTheHost()      // same, with sumafile_native_probe.dll also in hostDirectory -> null
[Theory]
[InlineData("Microsoft.WinUI", true)] [InlineData("Microsoft.InteractiveExperiences.Projection", true)]
[InlineData("Microsoft.Web.WebView2.Core", true)] [InlineData("Microsoft.Windows.SDK.NET", true)]
[InlineData("WinRT.Runtime", true)] [InlineData("SumaFile.Remote.Model", false)] [InlineData("Newtonsoft.Json", false)]
public void LoadContext_SharesWinUiAndWindowsAppSdkProjections(string name, bool shared)

// PluginDiscoveryTests
[Fact] public void Scan_RecordsTheManifestHashFromItsOnlyRead()   // ManifestHash == PluginPackageHash.ComputeManifestHash(folder)
[Fact] public void Scan_ManifestWithUtf8Bom_IsValid()

// PluginTrustTests
[Fact] public async Task Discover_ReadsTheApprovalsFileOncePerScan() // three plugins, one bundled -> manager.Approvals.LoadCount == 1 after DiscoverAsync
[Fact] public void ApprovalStore_KeepsItsFileFormat()               // a file in today's format (PascalCase keys, string enums, Folder) reads; Set writes the same shape
[Fact] public async Task Discover_TenPlugins_StaysUnderTheStartupBudget()
// spec §5.2's case: ten plugin folders, each manifest with a command, a toolbar item, a context-menu item and three settings.
// Two are listed in one bundled.json; six are approved in one approvals file, two of them signed (signature.json lists a 4 MiB payload);
// two need consent. One untimed DiscoverAsync warms the JIT. Then, five times, a fresh PluginManager plus DiscoverAsync is timed.
// The best of the five is under 50 ms, and each scan reads the approvals file once. The cold and best times go to the test output.
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManagerTests|FullyQualifiedName~PluginLoadingTests|FullyQualifiedName~PluginDiscoveryTests|FullyQualifiedName~PluginTrustTests"`
Expected: compile FAIL (`AfterDisableStateSet`, `ResolveUnmanagedPath`, `IsHostShared`, `ManifestHash`, `Approvals` and `LoadCount` not found).

- [ ] **Step 3: Implement the four changes as specified.** `Evaluate` uses `plugin.ManifestHash`, and falls back to `PluginPackageHash.ComputeManifestHash` only when it is null. Add the `Approvals` accessor, the CI budget step and its packaging snippet.

- [ ] **Step 4: Run the plugin test classes and the full C# suite**

Run: the Step 2 command, then `npm run check:winui`, then `npm run check`
Expected: all pass. `PluginSdkSurfaceTests` is unchanged, and `WinUI packaging surface is wired.` covers the new CI step.

- [ ] **Step 5: Check the startup budget with the plugins built so far**

Run: `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`, then `npm run check:winui-startup-budget`
Expected: `UI runtime smoke passed.`, then `WinUI startup budget passed: …` with a printed `plugins-discovered delta` below 50 ms.
- Note the measured delta, and the ten-plugin test's cold and best times, in the task report. The delta is the baseline for Task 15, which adds a third plugin.
- If the delta or the best ten-plugin run is above 25 ms, apply the budget fallbacks (Interfaces) in this task, before Step 6.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 4: Shared transfer primitives in a new leaf crate, `sumafile-transfer`

**Files:**
- Create: `crates/sumafile-transfer/Cargo.toml` and `src/{lib.rs, path_conflict.rs, reliability.rs, policy.rs, resources.rs, staging.rs, journal.rs, manifest.rs, paths.rs, ids.rs, test_io.rs}`.
- Move these sources into the crate. This is a real move, with their unit tests. Service:
  - `src/transfer_reliability.rs` → `reliability.rs`
  - `src/transfer_staging.rs` → `staging.rs`
  - `src/transfer_journal.rs` → `journal.rs`
  - `src/transfer_manifest.rs` → `manifest.rs`
  - `src/transfer_test_io.rs` → `test_io.rs`
  - `src/progress/resources.rs` → `resources.rs`
  - the `Mode`, `OrderBy`, `CheckMode` and `Policy` types, `Default`, `normalized` and `buffer_bytes` from `src/progress/tuning.rs:21-160` → `policy.rs`
  - `unique_destination_path` from `src/progress/plan.rs:98` → `paths.rs`
  - the operation id generator → `ids.rs`. `progress.rs:124` `generate_transfer_operation_id` only forwards to `simplefile_core::utils::generate_operation_id` (`utils.rs:298`), and the leaf crate cannot depend on core. So the generator body itself moves (below).

  Core: `crates/simplefile-core/src/path_conflict.rs` → `path_conflict.rs`.
- Modify: `crates/simplefile-service/src/lib.rs`. Drop the five `mod transfer_*` lines and add crate-level aliases, so `crate::transfer_*` paths keep compiling:
  - `use sumafile_transfer::{journal as transfer_journal, manifest as transfer_manifest, reliability as transfer_reliability, staging as transfer_staging};`
  - `#[cfg(test)] use sumafile_transfer::test_io as transfer_test_io;`
- Modify: `crates/simplefile-service/src/progress.rs` to re-export the moved resources and policy items under their old `crate::progress::` names.
- Modify: `src/progress/tuning.rs`. `Policy::load()` becomes `pub(crate) fn load_policy() -> Policy` and `Policy::for_operation(id)` becomes `pub(crate) fn policy_for_operation(operation_id: &str) -> Policy`. Update the call sites: `progress.rs:212, 236` and `remote/transfer.rs`, which stays until Task 14.
- Modify: `crates/simplefile-core/src/lib.rs`: `pub mod path_conflict;` becomes `pub use sumafile_transfer::path_conflict;`.
- Modify: `crates/simplefile-core/src/utils.rs:298`. `generate_operation_id()` forwards to `sumafile_transfer::ids::generate_transfer_operation_id()`, so the service keeps one counter for all of its operation ids.
- Modify: `crates/simplefile-core/Cargo.toml` and `crates/simplefile-service/Cargo.toml`: add `sumafile-transfer = { path = "../sumafile-transfer" }`. Also modify root `Cargo.toml` members, `Cargo.lock` and `tools/sevenz-evaluation/Cargo.lock`.
- Modify: `crates/simplefile-service/Cargo.toml` `[dev-dependencies]`: add `sumafile-transfer = { path = "../sumafile-transfer", features = ["test-support"] }`. The `#[cfg(test)]` alias and the `progress/parallel.rs` tests need `test_io`, and Step 4 runs `cargo test -p simplefile-service` without `--all-features`.
- Modify: `scripts/check-winui-packaging.mjs`: a new `pluginCrateManifests` check (below).
- Modify: spec §12 (the refinement in Global Constraints).

**Interfaces:**
- Crate `sumafile_transfer` depends only on `serde`, `serde_json`, `sha2`, `log` and, on Windows, `windows-sys` `0.61` with `Win32_Foundation` and `Win32_Storage_FileSystem`. It takes no `winapi`. Feature `test-support` exposes `test_io`.
  - `log`: `journal.rs` and `reliability.rs` keep their `log::warn!` calls (today `transfer_journal.rs:341` and `transfer_reliability.rs:220`). In the service they reach `service.log` as today. In the remote backend they reach `plugins.log` through stderr (Task 6).
  - `path_conflict::rename_exclusive` calls `windows_sys::Win32::Storage::FileSystem::MoveFileExW` with flags 0, in place of `winapi::um::winbase::MoveFileExW` (core `path_conflict.rs:35`). It is the same call: without `MOVEFILE_REPLACE_EXISTING`, Windows refuses to replace. The `AlreadyExists` mapping is unchanged.
  - `ids::generate_transfer_operation_id()` holds the body moved from `simplefile_core::utils::generate_operation_id`: `format!("op_{secs}_{count}")` from one process-wide `AtomicU64`. Core forwards to it. Two copies of the counter would let a transfer id and another operation id of the same second collide.
- Produces, crate `sumafile_transfer`. Former `pub(crate)` items become `pub`:
  - `path_conflict::{path_exists_no_follow, path_collision_key, paths_refer_to_same_entry, is_keep_both_action, is_already_exists_error, rename_exclusive, create_dir_exclusive}`
  - `reliability::{Failure, Retry, wait}`. `Retry::from_policy(&policy::Policy)` stays.
  - `policy::{Policy, Mode, OrderBy, CheckMode}`
  - `resources::{PooledBuffer, RateLimit, Permit}` and the memory-budget type `progress/resources.rs` defines today
  - `staging::*` and `journal::{StagingLease, recover_directory, …}`, with the journal record format unchanged
  - `manifest::{DiskManifest, CopiedFiles}`
  - `paths::unique_destination_path`
  - `ids::generate_transfer_operation_id() -> String`
- No behaviour change anywhere. The service and core public APIs keep every path they export today.
- `check-winui-packaging.mjs` `pluginCrateManifests`: each of these must not contain the text `simplefile-core` or `simplefile-service`:
  - `crates/sumafile-transfer/Cargo.toml`
  - `plugins/sevenzip/backend/Cargo.toml`
  - any later plugin crate the check lists (Task 5 adds the remote crates)

  On a violation it fails with `<file> must not depend on simplefile-core or simplefile-service (spec §12)`.

- [ ] **Step 1: Write the failing test.**
  - In `crates/sumafile-transfer/src/policy.rs`, add `#[test] fn default_policy_is_normalized()`, which asserts `Policy::default().normalized() == Policy::default()`.
  - In `ids.rs`, add `#[test] fn operation_ids_are_unique()`, which asserts 1000 ids are distinct and match `^op_\d+_\d+$`.
  - In core's `utils.rs` tests, add `#[test] fn core_and_transfer_operation_ids_share_one_counter()`. It alternates 500 calls to `generate_operation_id` with 500 calls to `sumafile_transfer::ids::generate_transfer_operation_id` and asserts all 1000 ids are distinct.

- [ ] **Step 2: Run to verify it fails**

Run: `cargo test --locked -p sumafile-transfer`
Expected: FAIL, `package ID specification 'sumafile-transfer' did not match any packages`.

- [ ] **Step 3: Create the crate and move the modules** as listed.
  - Inside the moved files, rewrite `crate::progress::…`, `crate::transfer_*::…` and `simplefile_core::path_conflict::…` to the new module paths.
  - Update `Cargo.lock` with a plain `cargo build`, then confirm it with `--locked`.
  - Update the evaluation tool's lock with `cargo build --manifest-path tools/sevenz-evaluation/Cargo.toml --target-dir target`.

- [ ] **Step 4: Run every affected suite**

Run:
- `cargo test --locked -p sumafile-transfer`
- `cargo test --locked -p simplefile-core`
- `cargo test --locked -p simplefile-service`
- `cargo build --locked --manifest-path tools/sevenz-evaluation/Cargo.toml --target-dir target`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`

Expected: `test result: ok` everywhere; the moved tests now run under `sumafile-transfer`. `cargo test -p simplefile-service` compiles without `--all-features`, through the dev-dependency. The evaluation tool builds, and clippy is clean.

- [ ] **Step 5: Static checks**

Run: `npm run check`
Expected: all success lines; still `99 domain methods`.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 5: Remote backend I: profile store with a legacy import that runs once per database, Credential Manager secrets, FTP/SFTP providers, sessions, and the SFTP test server

**Files:**
- Create the crate `plugins/remote/backend/` (`publish = false`, `[lib]` + `[[bin]]` as in `plugins/sevenzip/backend/Cargo.toml`), with `src/{lib.rs, models.rs, profiles.rs, legacy.rs, secrets.rs, path.rs, provider.rs, ftp.rs, sftp.rs, session.rs}`. `main.rs` is a stub that exits 2 until Task 6.
  - These files are **copied**, not moved, from `crates/simplefile-core/src/remote/*` and `crates/simplefile-service/src/remote/{path,provider,secrets,session,ftp,sftp}.rs`. The originals keep serving the service until Task 14 deletes them.
  - Edits while copying:
    - `crate::transfer_reliability::Failure` becomes `sumafile_transfer::reliability::Failure`;
    - `simplefile_core::models::{FileEntry, DirectoryListing}` becomes `crate::models::{…}`;
    - `settings_store::metadata_db_path()` becomes the store path.
  - Dead code that is not copied: `UnsupportedRemoteProviderFactory`, `RemoteConnectionTestResult::valid_profile`, `validate_profile_input`, `ftp::connect` and `sftp::connect` (the String wrappers).
- Create the tests `plugins/remote/backend/tests/{profiles.rs, secrets.rs, sessions.rs, legacy_import.rs, sftp_test_server.rs}`. `profiles.rs` ports `crates/simplefile-core/tests/remote_profiles.rs` (6 tests); `secrets.rs` ports `crates/simplefile-service/tests/remote_secrets.rs` (3); `sessions.rs` ports `tests/remote_session.rs` (3). The unit tests in the copied files come along (ftp 5, sftp 5, path 5, secrets 2).
- Create `plugins/remote/test-server/` (`Cargo.toml`, `src/{lib.rs, main.rs, sftp.rs, legacy.rs}`).
- Modify: root `Cargo.toml` (members `plugins/remote/backend`, `plugins/remote/test-server`) and `Cargo.lock`; `scripts/check-winui-packaging.mjs` (`pluginCrateManifests` adds both new `Cargo.toml` files).
- Modify: spec §11.2. Record the profile and credential migration decision (text below).

**Interfaces:**
- Dependencies of `sumafile-remote-backend`:
  - `sumafile-plugin`, `sumafile-transfer`, `async-trait`, `chrono`, `getrandom`, `log = "0.4"`, `serde`, `serde_json`, `sha2`, `tokio`;
  - `rusqlite = { version = "0.41.1", features = ["bundled"] }` (the version core uses; core, this backend and the test server moved together from `0.40.2` because `libsqlite3-sys` 0.38.x carries RUSTSEC-2026-0459, which fails the CI `cargo audit` gate, and its `links = "sqlite3"` allows one version per lock file);
  - `russh = { version = "0.63.3", default-features = false, features = ["flate2", "ring"] }`, `russh-sftp = "3.0.0"` and `suppaftp = { version = "12.0.1", features = ["native-tls"] }` (the service's versions);
  - `windows-sys` `0.61` with `Win32_Foundation` and `Win32_Security_Credentials`.

  The copied `ftp.rs:357`, `session.rs:179` and `transfer.rs:251` keep their `log::warn!` calls; Task 6's `main.rs` routes them to stderr. Dev-dependencies: `sumafile-plugin` with `test-support`, `sumafile-transfer = { path = "../../../crates/sumafile-transfer", features = ["test-support"] }` (the ported `transfer.rs` tests use `test_io::DelayedIo`), `sumafile-remote-test-server`, `tempfile`.
- `lib.rs`: `pub const PLUGIN_ID: &str = "com.sumafile.remote"; pub const PLUGIN_VERSION: &str = "1.0.0";`
- `models.rs`: `RemoteProtocol`, `RemoteAuthKind`, `RemoteProfile` and `RemoteProfileInput` as in core today. `FileEntry` and `DirectoryListing` serialize exactly like `simplefile_core::models`:
  - fields `name, path, is_dir, is_symlink, is_hidden, is_system, size, modified, extension, permissions, symlink_target, git_status`;
  - `is_hidden` and `is_system` are omitted when false; the three `Option`s are omitted when `None`;
  - `DirectoryListing { path, parent, entries, is_network }`.
- `profiles.rs`:
  ```rust
  pub const PROFILE_DB_FILE: &str = "remote-profiles.db";
  pub const CREDENTIAL_TARGET_PREFIX: &str = "SumaFile.Remote.";
  pub struct ProfileStore { /* path */ }
  impl ProfileStore {
      pub fn open(data_dir: &Path) -> ProfileStore;            // <data_dir>\remote-profiles.db; creates the folder and tables on first use
      pub fn list(&self) -> Result<Vec<RemoteProfile>, String>; // ORDER BY name COLLATE NOCASE, host COLLATE NOCASE, id (as today)
      pub fn save(&self, input: RemoteProfileInput) -> Result<RemoteProfile, String>;
      pub fn delete(&self, id: &str) -> Result<Option<RemoteProfile>, String>; // the deleted row, so its credential can be deleted
      pub fn prepare(input: RemoteProfileInput) -> Result<RemoteProfile, String>; // today's prepare_profile_input
      pub fn meta(&self, key: &str) -> Result<Option<String>, String>;          // table plugin_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)
      pub fn set_meta(&self, key: &str, value: &str) -> Result<(), String>;
  }
  ```
  - The table `remote_profiles` keeps today's schema.
  - Every connection sets `busy_timeout` 5 s. `pub(crate) fn connection(&self) -> Result<rusqlite::Connection, String>` (tables ensured) serves the import transaction.
- `legacy.rs`:
  ```rust
  pub const LEGACY_PROFILES_IMPORTED: &str = "legacy_profiles_imported";
  pub const LEGACY_TRANSFER_SETTINGS_SEEDED: &str = "legacy_transfer_settings_seeded";
  #[derive(Debug, PartialEq, Eq)] pub enum LegacyImport { NoLegacyTable, AlreadyImported, Imported(usize) }
  /// The metadata.db the 1.0.4 service used, resolved in the service's order; None when no candidate exists. Never creates anything.
  pub fn legacy_metadata_db_path(env: &dyn Fn(&str) -> Option<String>) -> Option<PathBuf>;
  /// "<marker>:<std::path::absolute(legacy_db), lowercased>". Markers are per legacy database.
  pub fn legacy_marker_key(marker: &str, legacy_db: &Path) -> String;
  /// Reads every legacy remote_profiles row with SQLITE_OPEN_READ_ONLY. Then, in one BEGIN IMMEDIATE transaction on the store:
  /// returns AlreadyImported when the store's plugin_meta has legacy_marker_key(LEGACY_PROFILES_IMPORTED, legacy_db); otherwise
  /// inserts every row verbatim with INSERT OR IGNORE (credential_target, trusted_host_fingerprint and updated_at included, so an
  /// id the store already has keeps the store's row), writes that marker with an RFC 3339 time, and commits.
  /// A missing file or a missing table returns NoLegacyTable and writes no marker.
  pub fn import_legacy_profiles(store: &ProfileStore, legacy_db: &Path) -> Result<LegacyImport, String>;
  /// The `transfer.policy` JSON from the legacy settings table, if any.
  pub fn legacy_transfer_policy(legacy_db: &Path) -> Result<Option<String>, String>;
  ```
  - Markers are per database for this reason. Before Task 14, the App's startup sidebar refresh (`remote_list_profiles`) creates an empty `remote_profiles` table in any metadata.db it opens, including a smoke's disposable one. A single marker written after importing 0 rows from such a database would stop the real 1.0.4 database from ever being imported.
  - Two backends can share a data dir: two app sessions, or a dev build beside an installed one. The marker check inside the IMMEDIATE transaction, the 5 s `busy_timeout` and `INSERT OR IGNORE` make concurrent imports safe: one imports, the other sees the marker.
  - Resolution order, exactly as `settings_store.rs:54-128`:
    1. `SIMPLEFILE_METADATA_DB` when set and non-empty;
    2. otherwise the first existing `metadata.db` under `SIMPLEFILE_APP_DATA_DIR`, `%APPDATA%\com.simplefile.desktop`, `%APPDATA%\SumaFile`, `%APPDATA%\SimpleFile`, `%LOCALAPPDATA%\SumaFile`, `%LOCALAPPDATA%\com.simplefile.desktop`, `%LOCALAPPDATA%\SimpleFile`, deduplicated;
    3. otherwise `None`.
  - The caller (Task 6) calls `import_legacy_profiles` on its first call whenever the legacy path resolves. The function owns the marker.
- `secrets.rs`: `RemoteSecret`, `RemoteSecretStore`, `MemoryRemoteSecretStore` and `WindowsRemoteSecretStore` as today (constants and blob format per Global Constraints).
- `provider.rs`: `RemoteProvider`, `RemoteProviderFactory` and `DefaultRemoteProviderFactory` (with `ReconnectingProvider`). `session.rs`: `RemoteSessionRegistry`, `SharedRemoteProvider`, `RemoteSession` and `RemoteConnectionTestResult` as today, with ids `remote-session-N` and the busy text from Global Constraints. `ftp.rs` and `sftp.rs` keep every error text.
- Test server, crate `sumafile_remote_test_server`:
  ```rust
  #[derive(Serialize, Deserialize, Clone)] #[serde(rename_all = "camelCase")]
  pub struct TestServerInfo { pub port: u16, pub fingerprint: String, pub user: String, pub password: String, pub root: PathBuf,
                              pub legacy_profile_id: Option<String> }   // set when the bin wrote a legacy database
  pub struct TestServer { /* listener task, shutdown */ }
  impl TestServer {
      /// Binds 127.0.0.1:0 with a fresh Ed25519 host key; user "smoke", a random 16-hex password; serves `root` over SFTP only.
      pub async fn start(root: PathBuf) -> Result<TestServer, String>;
      pub fn info(&self) -> &TestServerInfo;   // fingerprint as "SHA256:<base64>", the text sftp.rs compares
      pub async fn stop(self);
  }
  ```
  - It serves `realpath`, `opendir`, `readdir`, `close`, `stat`, `lstat`, `fstat`, `open` (read, write, create, truncate), `read`, `write`, `mkdir`, `rmdir`, `remove`, `rename` and `setstat` (accepted and ignored).
  - Every path stays inside `root`; a `..` escape answers `PermissionDenied`.
  - The bin `sumafile-remote-test-server --root <dir> --info <file> [--legacy-db <path>]` writes the info as JSON, then serves until it is killed.
  - With `--legacy-db`, the bin first writes that database. It holds one profile, `info.legacy_profile("legacy-sftp", "SumaFile.Remote.legacy-sftp")`, named `Legacy SFTP`, and the info gets `legacyProfileId: "legacy-sftp"`. It never writes a credential.
  - `src/legacy.rs` writes 1.0.4 metadata databases for the backend tests, the C# harness (Task 13) and the UI smoke (Task 15). C# has no SQLite library, and none may be added. The crate adds `rusqlite = { version = "0.41.1", features = ["bundled"] }`:
    ```rust
    /// One row of the 1.0.4 remote_profiles table.
    #[derive(Clone)] pub struct LegacyProfile { pub id: String, pub name: String, pub protocol: String, pub host: String, pub port: u16,
        pub username: String, pub root_path: String, pub auth_kind: String, pub insecure_plain_ftp: bool, pub passive_mode: bool,
        pub credential_target: Option<String>, pub private_key_path: Option<String>, pub trusted_host_fingerprint: Option<String>, pub updated_at: String }
    /// Creates `path` with the 1.0.4 schema: settings(key TEXT PRIMARY KEY, value TEXT NOT NULL) and remote_profiles with the DDL of
    /// crates/simplefile-core/src/remote/profiles.rs:274-289 (copied, since Task 14 deletes it); inserts `profiles` and, when given, the transfer.policy row.
    pub fn write_legacy_metadata_db(path: &Path, profiles: &[LegacyProfile], transfer_policy: Option<&str>) -> Result<(), String>;
    impl TestServerInfo {
        /// An SFTP password profile for this server: 127.0.0.1, its port and user, root "/", its fingerprint trusted.
        pub fn legacy_profile(&self, id: &str, credential_target: &str) -> LegacyProfile;
    }
    ```
- Spec §11.2 decision text, to add verbatim: "Profiles: the backend keeps them in `<data dir>\remote-profiles.db` (table `remote_profiles`, the 1.0.4 schema). On first use it imports, read-only, every row of the `remote_profiles` table in the metadata.db the 1.0.4 service used (resolved as the service does: `SIMPLEFILE_METADATA_DB`, else the first existing `metadata.db` under `SIMPLEFILE_APP_DATA_DIR`, `%APPDATA%\com.simplefile.desktop`, `%APPDATA%\SumaFile`, `%APPDATA%\SimpleFile`, `%LOCALAPPDATA%\SumaFile`, `%LOCALAPPDATA%\com.simplefile.desktop`, `%LOCALAPPDATA%\SimpleFile`), skips ids it already has, and records per legacy database that it ran, so a run against another database (a test's or a smoke's) never blocks the real import. It never writes the old table, which stays for a downgrade. Credentials are read in place: the plugin keeps the `SumaFile.Remote.` prefix, `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`, the UTF-8 `kind\0value` blob and each profile's `credential_target`, so saved passwords and passphrases keep working with no migration. Deleting a profile deletes its credential."

- [ ] **Step 1: Write the failing new tests** (the ported tests come along in Step 3).

```rust
// legacy.rs unit tests (pure: a fake env map, temp folders)
#[test] fn metadata_path_prefers_simplefile_metadata_db()
#[test] fn metadata_path_takes_the_first_existing_candidate_in_service_order()   // creates LOCALAPPDATA\SumaFile\metadata.db and APPDATA\SimpleFile\metadata.db -> APPDATA\SimpleFile wins
#[test] fn metadata_path_is_none_when_no_candidate_exists()

// tests/legacy_import.rs (legacy databases made with sumafile_remote_test_server::write_legacy_metadata_db)
#[test] fn import_copies_every_legacy_profile_once()        // 2 rows -> Imported(2); second call -> AlreadyImported; list() equals the legacy rows field by field
#[test] fn import_never_writes_the_legacy_database()        // SHA-256 and mtime of the legacy file unchanged
#[test] fn import_skips_ids_the_store_already_has()          // an edited store row with a legacy id -> Imported(1) of 2; the edit is kept
#[test] fn import_of_a_database_without_the_table_imports_nothing() // NoLegacyTable and no marker; once the table exists, the same path imports
#[test] fn import_marker_is_scoped_to_its_database()         // A (empty table) -> Imported(0); B (one row) -> Imported(1); A again -> AlreadyImported
#[test] fn concurrent_imports_insert_each_profile_once()      // two threads, two ProfileStores on one data dir, one Barrier: both Ok, counts sum to 2, list() has each row once
#[test] fn legacy_transfer_policy_reads_the_settings_row()  // settings(key='transfer.policy') -> Some(json)

// secrets.rs
#[cfg(windows)] #[test] fn windows_store_round_trips_a_real_credential_and_deletes_it() // target SumaFile.Remote.test-<random>; read == written; deleted -> read None

// models.rs
#[test] fn file_entry_serializes_like_the_service()          // exact JSON for one entry with false flags and None options

// tests/sftp_test_server.rs (TestServer in-process; DefaultRemoteProviderFactory)
#[tokio::test(flavor = "multi_thread", worker_threads = 2)] async fn untrusted_host_key_fails_with_the_fingerprint_to_trust()
// no trusted_host_fingerprint -> error starts "SFTP host fingerprint is not trusted" and ends with info.fingerprint
#[tokio::test(flavor = "multi_thread", worker_threads = 2)] async fn another_servers_fingerprint_fails_with_a_mismatch()
// trust server A's fingerprint, connect to server B -> "SFTP host fingerprint mismatch. … Expected <A>, got <B>"
#[tokio::test(flavor = "multi_thread", worker_threads = 2)] async fn sftp_lists_creates_renames_and_deletes_unicode_and_punctuated_names()
// root holds "café 資料/hello.txt" and "literal [brackets] & spaces.txt"; list "/" shows both; mkdir "new [x]"; rename to "renamed folder [ok]"; delete; disk agrees
#[tokio::test(flavor = "multi_thread", worker_threads = 2)] async fn sftp_streams_a_two_mebibyte_file_both_ways() // upload random 2 MiB, download, bytes equal
#[tokio::test(flavor = "multi_thread", worker_threads = 2)] async fn sftp_calls_work_from_spawn_blocking_on_a_two_worker_runtime() // connect/list inside spawn_blocking
#[tokio::test(flavor = "multi_thread", worker_threads = 2)] async fn wrong_password_fails_without_a_session()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `cargo test --locked -p sumafile-remote-backend`
Expected: FAIL, the package does not exist.

- [ ] **Step 3: Create both crates, copy the modules with the listed edits, and port the three test files.** Provider calls stay synchronous; Task 6 runs them on blocking threads.

- [ ] **Step 4: Run both crates and the workspace**

Run:
- `cargo test --locked -p sumafile-remote-backend -p sumafile-remote-test-server`
- `cargo test --locked --all-features`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`

Expected: `test result: ok` for every target. The service and core suites are unchanged. Clippy is clean.

- [ ] **Step 5: Static checks**

Run: `npm run check`
Expected: all success lines (`WinUI packaging surface is wired.` includes the new crate rule).

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 6: Remote backend II: the handler, the transfer engine, Transfers-row progress, two-id cancel, plugin transfer settings and the protocol doc

**Files:**
- Create: `plugins/remote/backend/src/{transfer.rs, settings.rs, bridge.rs, handler.rs}`; replace the `main.rs` stub.
  - `transfer.rs` is copied from `crates/simplefile-service/src/remote/transfer.rs`, with its 21 tests and `Fixture` and `DiskProvider`.
  - Its dependencies become `sumafile_transfer::{reliability, staging, journal, policy, resources, paths, ids}`.
  - `ProgressUpdate` and `TuningUpdate` become the crate's own `TransferProgress` and `TransferTuning`.
  - `transfer_single` is not copied.
- Create: `plugins/remote/backend/tests/{handler.rs, transfers.rs, process.rs}`. They port these service tests:
  - `dispatch/tests.rs:801-1598`, the remote block;
  - from `session/tests.rs:61-209`, only `remote_transfer_accepts_health_and_cancel_while_streaming_and_cleans_partial_file`. It becomes `cancel_by_request_id_answers_other_calls_and_cleans_the_partial_file` (below);
  - `session/tests.rs:597-669` (slow test profile), which becomes `slow_connect_does_not_block_other_requests`.

  `remote_transfer_disconnect_cancels_worker_and_cleans_partial_file` is not ported. At plugin level, a closed host connection drains the progress ids but sets no request's cancel flag, and the host then kills the backend (kill-on-close job, `DisposeAsync`), so no cleanup can run. That is a known gap; `next_download_recovers_a_partial_left_by_a_killed_backend` pins the recovery instead.
- Modify: `crates/sumafile-plugin/src/host.rs`, the `test-support` fake (recording below).
- Create: `plugins/remote/protocol.json`.
- Modify: `scripts/check-ipc-schema.mjs`: a remote plugin protocol block (methods, events and error texts).
- Modify: `ipc/schema/plugin/README.md` and `ipc/schema/plugin/v1/protocol.json` `cancel.notes`: plugin-owned method tables and their cancel outcome (below).
- Modify: spec §11.2 (method table, progress, cancel, settings, sessions).

**Interfaces:**
- `transfer.rs`:
  ```rust
  #[derive(Deserialize)] #[serde(rename_all = "camelCase")]
  pub struct RemoteTransferRequest { pub remote_session_id: String, pub direction: String, pub sources: Vec<String>, pub destination: String, pub conflict_action: String, pub operation_id: String }
  // RemoteTransferItem, RemoteTransferOutcome: as today (snake_case JSON: source, destination, is_dir, status, bytes, error; items, retry_sources, cancelled)
  #[derive(Clone, Serialize)] #[serde(rename_all = "camelCase")]
  pub struct TransferProgress { pub operation_id: String, pub current: u64, pub total: u64, pub current_files: u64, pub total_files: u64, pub current_item: String, pub status: String }
  #[derive(Clone, Serialize)] #[serde(rename_all = "camelCase")]
  pub struct TransferTuning { pub operation_id: String, pub mode: String, pub explanation: String }
  pub trait TransferObserver: Sync { fn progress(&self, update: &TransferProgress); fn tuning(&self, update: &TransferTuning); }
  pub fn validate_request(request: &RemoteTransferRequest) -> Result<(), String>;
  pub fn transfer_entries(provider: &mut dyn RemoteProvider, request: &RemoteTransferRequest, cancel: &AtomicBool,
                          observer: &dyn TransferObserver, read_policy: &dyn Fn() -> Policy) -> Result<RemoteTransferOutcome, String>;
  ```
  - The memory permits come from one process-wide budget in this crate.
- `settings.rs`:
  ```rust
  pub const TRANSFER_SETTING_KEYS: [&str; 8] = ["bandwidthMib", "checkMode", "inplace", "preallocate", "flushOnClose", "lowLevelRetries", "highLevelRetries", "retrySleepMs"];
  /// Policy::default() with these keys applied, then normalized(); a missing or unparsable value keeps the default.
  pub fn policy_from_settings(values: &HashMap<String, Option<String>>) -> Policy;
  /// Every key's plugin setting string for `policy`, in TRANSFER_SETTING_KEYS order ("0", "size_and_mod_time", "false", …).
  pub fn setting_values(policy: &Policy) -> Vec<(&'static str, String)>;
  /// Keys whose value in a legacy transfer.policy JSON differs from Policy::default(), as plugin setting strings (setting_values diffed).
  pub fn settings_from_legacy_policy(json: &str) -> Vec<(String, String)>;
  pub struct TransferSettingsCache { /* last good HashMap */ }
  impl TransferSettingsCache { pub async fn refresh(&self, host: &HostClient) -> Policy; } // host.get_settings(TRANSFER_SETTING_KEYS) under a 2 s timeout; a failure keeps the cache
  ```
  - The defaults match `Policy::default()`: `bandwidthMib` 0, `checkMode` `size_and_mod_time`, `inplace` false, `preallocate` true, `flushOnClose` true, `lowLevelRetries` 10, `highLevelRetries` 3 and `retrySleepMs` 1000.
- `bridge.rs`: `pub struct TransferBridge` implements `TransferObserver`. Rules:
  - It keeps only the latest progress.
  - One async pump task sends `host.progress_report(row_id, current, total, Some("<currentFiles>/<totalFiles> files · <currentItem>"))`, with at most one report in flight and at most one every 100 ms. It also `emit`s `remote.transfer-progress` and `remote.transfer-tuning`, each at most every 100 ms; `emit` never waits.
  - Every 50 ms it sets the engine's `AtomicBool` when `host.is_cancelled(request_operation_id) || host.is_cancelled(row_id)`.
  - Every 5 s it refreshes the policy through `TransferSettingsCache` into an `Arc<Mutex<Policy>>`, which `read_policy` reads.
- `handler.rs`:
  ```rust
  pub const PROGRESS_EVENT: &str = "remote.transfer-progress";
  pub const TUNING_EVENT: &str = "remote.transfer-tuning";
  pub const PROFILES_CHANGED_EVENT: &str = "remote.profiles-changed";
  pub const TRANSFER_CANCELLED: &str = "Transfer cancelled";
  pub struct RemoteHandler { /* data_dir, store, secrets, factory, sessions, settings cache, ready flag, legacy path */ }
  impl RemoteHandler {
      pub fn new(data_dir: PathBuf) -> RemoteHandler; // WindowsRemoteSecretStore, DefaultRemoteProviderFactory, legacy path from the process env
      pub fn with_parts(data_dir: PathBuf, secrets: Arc<dyn RemoteSecretStore>, factory: Arc<dyn RemoteProviderFactory>, legacy_db: Option<PathBuf>) -> RemoteHandler;
      pub fn shutdown(&self);                           // disconnects every session
  }
  #[async_trait] impl PluginHandler for RemoteHandler { /* the table below */ }
  ```
  - Every store and provider call runs in `tokio::task::spawn_blocking`.
  - `emit` is fire-and-forget: its errors are ignored, so a service-mode connection, where `emit` fails at once, still gets every reply.
  - The first call of any method runs the one-time setup when the legacy path resolves, in this order:
    1. The legacy profile import, `import_legacy_profiles(store, legacy_db)` (Task 5), which owns its per-database marker. It emits `remote.profiles-changed` when it returns `Imported(n)` with `n > 0`.
    2. The legacy transfer-settings seed, unless the store has `legacy_marker_key(LEGACY_TRANSFER_SETTINGS_SEEDED, legacy_db)`.
       - For each `settings_from_legacy_policy` pair whose key `host.get_settings` reports unset, it calls `host.set_setting`.
       - The marker is written only when the read and every write succeeded. A database without a `transfer.policy` row writes it after zero writes.
    3. A step that fails is logged through `host.log` (`warn`) and runs again on the next call. It never fails the call that ran it.
  - Methods. Params and results keep today's IPC shapes from `ipc/schema/v1/commands.json:158-193`:

    | Method | Params | Result |
    | --- | --- | --- |
    | `profiles.list` | none | `RemoteProfile[]` |
    | `profiles.save` | `{profile, secret?}` | `RemoteProfile`; a non-empty secret is written to the profile's target |
    | `profiles.delete` | `{profileId}` | `null`; deletes the row, then its credential |
    | `profiles.test` | `{profile, secret?}` | `RemoteConnectionTestResult`; nothing saved |
    | `session.connect` | `{profileId, secret?}` | `RemoteSession`; the secret comes from params, else from the store |
    | `session.disconnect` | `{remoteSessionId}` | `null` |
    | `fs.list` | `{remoteSessionId, path}` | `DirectoryListing` |
    | `fs.create_directory` | `{remoteSessionId, path, name}` | `FileEntry` |
    | `fs.rename` | `{remoteSessionId, path, newName}` | `FileEntry` |
    | `fs.delete` | `{remoteSessionId, paths}` | `string[]`, the errors |
    | `transfer.run` | `{remoteSessionId, direction, sources, destination, conflictAction, operationId}` | `RemoteTransferOutcome` |

    `profiles.save` and `profiles.delete` emit `remote.profiles-changed`.
  - `transfer.run` flow:
    1. `validate_request`, then the session's provider.
    2. `host.progress_begin(title)`. The title is `Uploading <n> item(s) to <host>:<destination>` or `Downloading <n> item(s) from <host>`, where `<n> item(s)` reads `1 item` or `<n> items`. When `progress_begin` fails, it logs `warn` and goes on without a row.
    3. The engine runs in `spawn_blocking` with a `TransferBridge`. It holds the session through `SharedRemoteProvider::with` for the whole transfer, so any other call on that session gets the busy text.
    4. The row ends:
       - with `progress_complete` when `!cancelled && retry_sources.is_empty()`;
       - with `progress_fail(row, "Transfer cancelled")` when cancelled;
       - otherwise with `progress_fail(row, "<k> of <n> selections failed: <first error>")`.
    5. A final progress event goes out, then it replies with the outcome. A cancel is not an error.
- `main.rs`: a multi-thread runtime with 2 workers. It builds `let handler = Arc::new(RemoteHandler::new(args.data_dir.clone()))` and runs `serve(args, info, false, handler.clone())`, then `handler.shutdown()` and `runtime.shutdown_timeout(Duration::from_secs(5))`.
  - Before anything else, it installs a small `log::Log` that writes `sumafile-remote-backend: [<level>] <message>` to stderr for `Warn` and above. So the copied `log::warn!` calls, and those in `sumafile-transfer`, reach `plugins.log` through the host's stderr forwarding.
- `crates/sumafile-plugin`, `test-support` only:
  ```rust
  impl HostClient {
      /// "begin <title>", "report <id> <current>/<total> <detail>", "complete <id>", "fail <id> <message>", in call order; empty for a real client.
      pub fn fake_progress_calls(&self) -> Vec<String>;
      /// Every emit(event, params) so far; empty for a real client.
      pub fn fake_events(&self) -> Vec<(String, serde_json::Value)>;
  }
  ```
  - The fake's `progress_begin` keeps returning `fake-operation-N`.
- `plugins/remote/protocol.json`, exactly. The `types` lists are those of `ipc/schema/v1/types.json:16-17, 32-40, 180-206`.
  ```json
  {
    "plugin": "com.sumafile.remote",
    "notes": "Methods the remote plugin's UI calls through IPluginHost.InvokeBackendAsync. Params are camelCase; results keep the 1.0.4 IPC shapes, with snake_case fields.",
    "methods": {
      "profiles.list": { "params": null, "result": "RemoteProfile[]" },
      "profiles.save": { "params": { "profile": "RemoteProfileInput", "secret": "string?" }, "result": "RemoteProfile", "emits": "remote.profiles-changed" },
      "profiles.delete": { "params": { "profileId": "string" }, "result": "null", "emits": "remote.profiles-changed" },
      "profiles.test": { "params": { "profile": "RemoteProfileInput", "secret": "string?" }, "result": "RemoteConnectionTestResult" },
      "session.connect": { "params": { "profileId": "string", "secret": "string?" }, "result": "RemoteSession" },
      "session.disconnect": { "params": { "remoteSessionId": "string" }, "result": "null" },
      "fs.list": { "params": { "remoteSessionId": "string", "path": "string" }, "result": "DirectoryListing" },
      "fs.create_directory": { "params": { "remoteSessionId": "string", "path": "string", "name": "string" }, "result": "FileEntry" },
      "fs.rename": { "params": { "remoteSessionId": "string", "path": "string", "newName": "string" }, "result": "FileEntry" },
      "fs.delete": { "params": { "remoteSessionId": "string", "paths": "string[]" }, "result": "string[]" },
      "transfer.run": {
        "params": { "remoteSessionId": "string", "direction": "string", "sources": "string[]", "destination": "string", "conflictAction": "string", "operationId": "string" },
        "result": "RemoteTransferOutcome",
        "progressEvent": "remote.transfer-progress",
        "notes": "Opens a Transfers row with host.progress_begin. A cancel naming either operationId or the row's id stops it; the reply is then an outcome with cancelled true, not an error."
      }
    },
    "events": {
      "remote.transfer-progress": { "fields": ["operationId", "current", "total", "currentFiles", "totalFiles", "currentItem", "status"] },
      "remote.transfer-tuning": { "fields": ["operationId", "mode", "explanation"] },
      "remote.profiles-changed": { "fields": [] }
    },
    "enums": {
      "RemoteProtocol": ["sftp", "ftp", "ftps"],
      "RemoteAuthKind": ["password", "private-key", "agent", "anonymous"]
    },
    "types": {
      "RemoteProfile": {
        "fields": ["id", "name", "protocol", "host", "port", "username", "root_path", "auth_kind", "insecure_plain_ftp", "passive_mode", "credential_target", "private_key_path", "trusted_host_fingerprint"],
        "optional": ["credential_target", "private_key_path", "trusted_host_fingerprint"]
      },
      "RemoteProfileInput": {
        "fields": ["id", "name", "protocol", "host", "port", "username", "root_path", "auth_kind", "insecure_plain_ftp", "passive_mode", "credential_target", "private_key_path", "trusted_host_fingerprint"],
        "optional": ["id", "credential_target", "private_key_path", "trusted_host_fingerprint"]
      },
      "RemoteSession": { "fields": ["session_id", "profile_id", "protocol", "root_path"] },
      "RemoteConnectionTestResult": { "fields": ["ok", "message", "capabilities"] },
      "RemoteTransferItem": { "fields": ["source", "destination", "is_dir", "status", "bytes", "error"], "optional": ["error"] },
      "RemoteTransferOutcome": { "fields": ["items", "retry_sources", "cancelled"] },
      "FileEntry": {
        "fields": ["name", "path", "is_dir", "is_symlink", "is_hidden", "is_system", "size", "modified", "extension", "permissions", "symlink_target", "git_status"],
        "optional": ["is_hidden", "is_system", "permissions", "symlink_target", "git_status"]
      },
      "DirectoryListing": { "fields": ["path", "parent", "entries", "is_network"] }
    },
    "errors": {
      "untrustedHostKey": { "text": "SFTP host fingerprint is not trusted. Check that this fingerprint belongs to the server, then trust it to connect: <fp>", "sources": ["plugins/remote/backend/src/sftp.rs"] },
      "hostKeyMismatch": { "text": "SFTP host fingerprint mismatch. The server key changed since you trusted it. Expected <x>, got <y>", "sources": ["plugins/remote/backend/src/sftp.rs"] },
      "sessionNotFound": { "text": "remote session not found: <id>", "sources": ["plugins/remote/backend/src/session.rs"] },
      "sessionBusy": { "text": "Remote session is busy; wait for or cancel its transfer", "sources": ["plugins/remote/backend/src/session.rs"] },
      "transferCancelled": { "text": "Transfer cancelled", "sources": ["plugins/remote/backend/src/handler.rs"] }
    }
  }
  ```
- `check-ipc-schema.mjs` remote block:
  - Every key of `methods` and `events` must be a quoted literal in `plugins/remote/backend/src/handler.rs`. Task 10 adds the C# client to the source list.
  - For every `errors` entry, the text before its first `<placeholder>` (the whole text when it has none) must follow a `"` in each file of its `sources`. The backend builds these texts with `format!`, so the check matches the start of the literal, as `"remote session not found: {session_id}"` matches `remote session not found: <id>`.
  - It prints `Checked remote plugin protocol: 11 methods, 3 events, 5 error texts.`
- `ipc/schema/plugin/README.md` gains a section `## Plugin-owned methods`, after "Cancellation":
  - A plugin's backend may serve its own methods to its own UI, through `IPluginHost.InvokeBackendAsync`. The plugin documents them in `plugins/<name>/protocol.json`; `plugins/remote/protocol.json` is the first.
  - `cancel` works for them as for any request. A cancel naming the request's `params.operationId`, or an id that `host.progress_begin` returned, sets the flag. The remote plugin watches both ids: the Transfers row's Cancel sends the progress id, and the plugin UI's own Cancel sends the request id.
  - The outcome of a cancel is the method's to define. Archive engine methods fail with `-32000` and `Archive operation cancelled`. The remote `transfer.run` replies normally with `cancelled: true` and ends its row with `host.progress_fail(row, "Transfer cancelled")`.
  - Under "Cancellation", "A cancelled call fails with `-32000`…" becomes "A cancelled archive-engine call fails with `-32000`…". `v1/protocol.json` `cancel.notes` gains: "Plugin-owned methods may report a cancel in their result instead; see the plugin's own protocol.json."
  - "Validation" gains the remote block of `check-ipc-schema.mjs`.

- [ ] **Step 1: Write the failing tests.** In `tests/handler.rs` and `tests/transfers.rs`, the harness is `RemoteHandler::with_parts` with `MemoryRemoteSecretStore`, `HostClient::fake` and the in-process `TestServer`, unless a test says otherwise. Every async test uses `#[tokio::test(flavor = "multi_thread", worker_threads = 2)]`, the backend's own runtime shape (written `#[tokio::test]` below for brevity).

```rust
// ported from the service (same assertions, plugin method names)
#[tokio::test] async fn profiles_save_and_list_never_return_secrets()
#[tokio::test] async fn saved_secret_is_used_on_connect_and_deleted_with_the_profile()
#[tokio::test] async fn private_key_passphrase_is_stored_under_the_profile_target()
#[tokio::test] async fn test_profile_reports_capabilities_without_saving()
#[tokio::test] async fn connect_list_and_disconnect_through_the_handler()      // after disconnect: "remote session not found: <id>"
#[tokio::test] async fn create_rename_and_delete_through_the_handler()
#[tokio::test] async fn slow_connect_does_not_block_other_requests()           // a slow factory; profiles.list answers within 1 s meanwhile
#[tokio::test] async fn busy_session_rejects_a_second_operation_while_a_transfer_runs() // exact busy text
// new
#[tokio::test] async fn transfer_run_opens_a_progress_row_reports_and_completes_it()
// fake_progress_calls()[0] == "begin Uploading 1 item to 127.0.0.1:/", at least one "report …", last "complete fake-operation-1"
#[tokio::test] async fn transfer_run_emits_progress_and_tuning_events_with_the_operation_id()
#[tokio::test] async fn cancel_by_request_id_answers_other_calls_and_cleans_the_partial_file()
// ported from session/tests.rs:62: bandwidthMib "1"; while the upload streams, profiles.list answers within 1 s;
// fake_cancel(operationId) after the first report -> outcome.cancelled; no "*.sumafile-partial*" left on the server
#[tokio::test] async fn cancel_by_progress_row_id_keeps_the_existing_destination_and_leaves_no_partial_file()
// download a 16 MiB file over an existing local file with conflictAction "replace"; fake_cancel("fake-operation-1"); outcome.cancelled;
// last call "fail fake-operation-1 Transfer cancelled"; local bytes unchanged; no "*.sumafile-partial*" in the folder
#[tokio::test] async fn transfer_reads_bandwidth_and_retry_settings_from_the_host() // fake settings bandwidthMib "1" -> a 4 MiB upload takes >= 3 s
#[tokio::test] async fn next_download_recovers_a_partial_left_by_a_killed_backend() // a stale staged file and journal from a dead pid -> removed, download succeeds
#[tokio::test] async fn first_call_imports_legacy_profiles_and_announces_them()   // legacy db with one row -> profiles.list has it; fake_events has "remote.profiles-changed"
#[tokio::test] async fn legacy_transfer_policy_seeds_unset_remote_settings_once()  // legacy {"bandwidthMib":5,"lowLevelRetries":4} -> settings set once; a second handler run sets nothing
#[tokio::test] async fn legacy_markers_are_scoped_to_the_database_they_read()
// one data dir. Handler 1 with legacy A (an empty remote_profiles table, no transfer.policy): profiles.list is empty, no event, no set_setting.
// Handler 2, same data dir, legacy B (one row, transfer.policy {"bandwidthMib":5}): the row is listed, "remote.profiles-changed" is emitted, bandwidthMib is set.
// Handler 3 with B again: no event and no set_setting.
#[tokio::test] async fn a_failed_legacy_import_is_retried_on_the_next_call()
// the legacy path holds bytes that are not a database: profiles.list still answers [] and fake_log() holds one "warn" entry;
// after a valid legacy database replaces it, the next call imports its row and emits "remote.profiles-changed"
#[cfg(windows)] #[tokio::test] async fn upgraded_profile_connects_with_its_saved_password()
// WindowsRemoteSecretStore; legacy row whose credential_target is SumaFile.Remote.test-<random>, credential written with the 1.0.4 blob;
// session.connect with no secret -> connects to the TestServer; a drop guard deletes the credential, then read == None

// settings.rs unit tests
#[test] fn policy_from_settings_applies_every_key_and_ignores_garbage()
#[test] fn settings_from_legacy_policy_lists_only_changed_keys()

// tests/process.rs (the real exe via env!("CARGO_BIN_EXE_sumafile-remote-backend"), a host-role test client that answers host.* requests)
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn backend_process_serves_profiles_sessions_listing_and_a_transfer()
// --data-dir temp, SIMPLEFILE_METADATA_DB=<temp>\none.db; save (no secret), connect with the secret in params, fs.list, transfer.run upload;
// the client saw host.progress_begin/report/complete; profiles.delete; no credential written
#[cfg(windows)] #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn backend_process_imports_a_1_0_4_profile_and_connects_with_its_saved_password()
// the real chain an upgrade takes: in-process TestServer; a legacy database from write_legacy_metadata_db holding
// info.legacy_profile(<id>, "SumaFile.Remote.test-<random>"); that credential written with the 1.0.4 blob ("password" 0x00 <password>)
// and deleted by a drop guard; the real exe with --data-dir temp and SIMPLEFILE_METADATA_DB=<legacy database>.
// profiles.list returns the row verbatim (credential_target and trusted_host_fingerprint included); the client received
// "remote.profiles-changed"; session.connect {profileId} with no secret connects; fs.list "/" lists the server root;
// the legacy file's SHA-256 is unchanged
```

Also add the ported engine tests (21) to `tests/transfers.rs` or keep them in `transfer.rs`, and helper-crate tests in `crates/sumafile-plugin/tests/roundtrip.rs`: `fake_host_records_progress_calls_and_events`.

- [ ] **Step 2: Run them to verify they fail**

Run: `cargo test --locked -p sumafile-remote-backend -p sumafile-plugin --all-features`
Expected: compile FAIL (`RemoteHandler`, `transfer_entries`, `fake_progress_calls` not found).

- [ ] **Step 3: Implement** `fake_progress_calls` and `fake_events`, then `transfer.rs`, `settings.rs`, `bridge.rs`, `handler.rs` and `main.rs`, as specified.

- [ ] **Step 4: Run the crate, the helper crate and clippy**

Run: `cargo test --locked -p sumafile-remote-backend -p sumafile-plugin --all-features` then `cargo clippy --locked --all-targets --all-features -- -D warnings`
Expected: `test result: ok` for every target; clippy clean.

- [ ] **Step 5: Write `plugins/remote/protocol.json`, extend the schema check, and run it**

Also write the `ipc/schema/plugin/README.md` section and the `cancel.notes` sentence.

Run: `npm run check:ipc-schema`
Expected: `Checked plugin protocol v1: 8 host methods, 7 archive engine methods, SDK 1.0.0.` and `Checked remote plugin protocol: 11 methods, 3 events, 5 error texts.`

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 7: SDK contracts and the plugin host model for sidebar sections, settings pages, plugin windows and dialogs

**Files:**
- Create: `src-winui/SimpleFile.PluginSdk/{ISidebarSectionProvider.cs, ISettingsPageProvider.cs, IPluginWindows.cs, PluginUiContracts.cs}`.
- Modify: `src-winui/SimpleFile.PluginSdk/{IPluginHost.cs, IPluginDialogs.cs, IPlugin.cs}` (XML docs: the thread contract).
- Modify: `src-winui/SimpleFile.Tests/Fixtures/PluginSdkSurface.v1.txt`, regenerated deliberately.
- Modify: `src-winui/SimpleFile.PluginHost/Manifest/{PluginManifest.cs, PluginManifestReader.cs}`, `Contributions/PluginContributionAdapter.cs`, `PluginManager.cs`, `PluginManager.Backend.cs`, `PluginRuntime.cs`, `PluginHostServices.cs`, `PluginRecord.cs`.
- Create: `src-winui/SimpleFile.PluginHost/{IPluginUiThread.cs, IPluginWindowHost.cs, PluginWindowSet.cs}`.
- Create: `src-winui/SimpleFile.Core/PluginSidebarSection.cs`.
- Test (fix round 1): `src-winui/SimpleFile.Tests/PluginWindowSetTests.cs`.
- Modify: `src-winui/SimpleFile.App/PluginDialogs.cs`: implement `ShowDialogAsync` (below). The App implements `IPluginDialogs`, so it must compile in this task.
- Test:
  - `src-winui/SimpleFile.Tests/{PluginManifestReaderTests.cs, PluginContributionAdapterTests.cs, PluginManagerTests.cs, PluginSdkSurfaceTests.cs}`;
  - every test fake that implements `IPluginHost` or `IPluginDialogs` (for example `PluginLoadingTests.FakePluginHost`).
- Modify: spec §4.4, §5.1, §7 and §8.2; the review notes (Minor 1 closed).

**Interfaces:**
- Produces, namespace `SimpleFile.PluginSdk` (version stays `1.0.0`):
  ```csharp
  public interface ISidebarSectionProvider { object CreateSidebarSection(PluginSidebarSectionRequest request); }   // returns a Microsoft.UI.Xaml.FrameworkElement
  public sealed record PluginSidebarSectionRequest(string SectionId, string Provider);
  public interface ISettingsPageProvider { object CreateSettingsPage(PluginSettingsPageRequest request); }        // returns a Microsoft.UI.Xaml.FrameworkElement
  public sealed record PluginSettingsPageRequest(string Provider);
  public interface IPluginWindows { void Attach(object window, PluginWindowOptions options); }                    // window: a Microsoft.UI.Xaml.Window
  public sealed record PluginWindowOptions(string Title, int Width, int Height);
  public enum PluginDialogResult { None = 0, Primary = 1, Secondary = 2 }
  // IPluginDialogs gains: Task<PluginDialogResult> ShowDialogAsync(object contentDialog, CancellationToken ct); // a Microsoft.UI.Xaml.Controls.ContentDialog; a null XamlRoot means the main window
  // IPluginHost gains:    IPluginWindows Windows { get; }
  ```
  - XML docs record the thread contract:
    - `ActivateAsync`, `ExecuteAsync`, `CreateSidebarSection` and `CreateSettingsPage` run on the UI thread;
    - `DeactivateAsync` runs on a thread-pool thread;
    - a provider may be asked again for the same section after the host dropped the previous element, and must then return a new element (or one with no parent);
    - the host closes attached windows before it stops the backend and deactivates the plugin.
- Produces, `SimpleFile.PluginHost`:
  ```csharp
  public sealed record PluginSidebarSpec(string Id, string Title, string Provider, string? Command);
  public sealed record PluginSettingsPageSpec(string Provider);
  // PluginContributions gains: IReadOnlyList<PluginSidebarSpec> Sidebar, PluginSettingsPageSpec? SettingsPage
  public interface IPluginUiThread { Task<T> RunAsync<T>(Func<Task<T>> work); }
  public interface IPluginWindowHost : IPluginWindows { Task CloseAllAsync(); }
  //   CloseAllAsync seals the host when it is called, for good: a later (or racing) Attach closes the window at once
  //   and tracks nothing, so a window attached while the plugin is being stopped never outlives DeactivateAsync.
  //   IPluginWindows.Attach documents it for plugins ("after the host starts stopping the plugin, Attach closes the window").
  public sealed class PluginWindowSet<TWindow> where TWindow : class   // the tracking and the seal, WinUI-free
  {
      public bool IsSealed { get; }
      public IReadOnlyList<TWindow> Open { get; }
      public bool TryTrack(TWindow window);         // false once sealed: the caller closes the window at once
      public bool Remove(TWindow window);           // from the window's Closed event
      public IReadOnlyList<TWindow> Seal();         // seals; returns the windows still tracked, for the caller to close
  }
  // PluginManagerOptions gains (all optional):
  //   IPluginUiThread? UiThread                    (default: runs inline)
  //   Func<string, IPluginWindowHost>? WindowsFor  (default: a host that ignores Attach; asked once per runtime and
  //                                                 must return a new host every call, since a closed host stays sealed)
  //   Func<object, string?>? ValidateUiElement     (null = valid, else the reason; default: any non-null object)
  // PluginManager gains:
  public Task<object> CreateSidebarSectionAsync(string pluginId, string sectionId, CancellationToken ct);
  public Task<object> CreateSettingsPageAsync(string pluginId, CancellationToken ct);
  // PluginRecord gains: public int Epoch { get; internal set; }   // bumped whenever the manager retires the record's runtime (teardown, load failure)
  // PluginContributionAdapter gains:
  public static IReadOnlyList<PluginSidebarSection> SidebarSections(IEnumerable<PluginRecord> enabled);
  ```
- Produces, `SimpleFile.Core`: `public sealed record PluginSidebarSection(string PluginId, string SectionId, string Title, string? CommandId, string? CommandLabel, int Epoch);`
  - `CommandId` is the host id `plugin:<id>:<command>`; `CommandLabel` is that command's label.
- Produces, App: `PluginDialogs.ShowDialogAsync`:
  - casts to `ContentDialog`, else throws `ArgumentException("contentDialog must be a Microsoft.UI.Xaml.Controls.ContentDialog.")`;
  - sets a null `XamlRoot` to the main window's;
  - shows the dialog through `DialogHost.ShowAsync` on the UI thread (`PluginUiDispatch.RunAsync`);
  - maps `ContentDialogResult` to `PluginDialogResult`.
- Reader rules for `contributions.sidebar[]`:
  - `id` follows the key grammar and is unique;
  - `title` and `provider` are required and non-empty;
  - an optional `command` must name a declared command.
- Reader rules for `contributions.settingsPage {provider}`: `provider` is required.
- Both kinds need `ui`. The errors read `contributions.sidebar needs a ui assembly.` and `contributions.settingsPage needs a ui assembly.`
- Any other key under `contributions` is rejected with `The contributions.<key> kind is not supported by this host.`, except `panels` and `rowDecorations`, which are accepted and ignored until phase 4.
- Manager behaviour:
  - One private activation path serves `RunCommandAsync` and both create methods: the Enabled check, `RuntimeFor`, `EnsureActivatedAsync`, `MarkLoadFailedAsync` on failure, and the retired re-check.
  - Activation, `ExecuteAsync` and provider calls run through `UiThread.RunAsync`.
  - `CreateSettingsPageAsync` never loads code for a record that is not Enabled.
  - These mark the plugin LoadFailed, with exactly these messages:
    - entry type lacks the interface: `Plugin '<id>' declares a sidebar section but its entry type does not implement ISidebarSectionProvider.`, or `… declares a settings page but its entry type does not implement ISettingsPageProvider.`;
    - provider returns null: `Plugin '<id>' returned no element for sidebar section '<sectionId>'.`, or `… returned no element for its settings page.`;
    - provider throws: `Plugin '<id>' failed to create sidebar section '<sectionId>': <message>`, or `… failed to create its settings page: <message>`;
    - `ValidateUiElement` rejects the element: `Plugin '<id>' sidebar section '<sectionId>' <reason>`, or `Plugin '<id>' settings page <reason>`.
  - An undeclared section or page throws `InvalidOperationException` (`Plugin '<id>' has no sidebar section '<sectionId>'.` or `Plugin '<id>' has no settings page.`) and changes no state.
  - `TeardownAsync` and `MarkLoadFailedAsync` order: windows, then backend, then deactivation. `DisposeAsync` gets the same order through `TeardownAsync`.
    1. They await `WindowsFor(id).CloseAllAsync()` first. The wait is bounded by `internal TimeSpan WindowCloseTimeout { get; set; } = TimeSpan.FromSeconds(5)`, the same 5 s as `PluginRuntime`'s retire timeout. On timeout, or when `CloseAllAsync` throws, they log `[warn] Plugin windows did not close within 5 s; stopping the plugin anyway.` (or the exception) and go on. Teardown can therefore never hang on a dispatcher that is shutting down.
    2. Then they stop the backend.
    3. Then the runtime is retired.

    Both bump `Epoch`.

- [ ] **Step 1: Write the failing tests**

```csharp
// PluginManifestReaderTests
[Fact] public void Read_SidebarAndSettingsPage_AreParsed()
[Fact] public void Read_SidebarWithoutUi_IsRejected()                 // "contributions.sidebar needs a ui assembly."
[Fact] public void Read_SidebarCommandMustBeDeclared()
[Fact] public void Read_DuplicateSidebarIds_AreRejected()
[Fact] public void Read_SidebarIdOutsideTheKeyGrammar_IsRejected()    // "Bad.Id"
[Fact] public void Read_UnknownContributionKind_IsRejected()         // "sidebars" -> "The contributions.sidebars kind is not supported by this host."
[Fact] public void Read_PanelsAndRowDecorations_AreAcceptedForLaterPhases()
// PluginContributionAdapterTests
[Fact] public void SidebarSections_ListEnabledPluginsInManifestOrderWithHostCommandIds()
// PluginManagerTests (InstanceFactory fakes implementing the providers; a recording IPluginUiThread)
[Fact] public async Task CreateSidebarSection_ActivatesOnceAndRunsTheProviderOnTheUiThread()
[Fact] public async Task CreateSidebarSection_ProviderThrows_MarksLoadFailedAndWithdrawsContributions()
[Fact] public async Task CreateSidebarSection_EntryWithoutTheProvider_MarksLoadFailed()
[Fact] public async Task CreateSidebarSection_RejectedElement_MarksLoadFailed()       // ValidateUiElement returns "returned System.String"
[Fact] public async Task CreateSidebarSection_UndeclaredSection_ThrowsAndChangesNothing()
[Fact] public async Task CreateSettingsPage_ForANeedsConsentPlugin_LoadsNoCode()       // InstanceFactory never called
[Fact] public async Task RunCommand_ExecutesThroughTheUiThread()
[Fact] public async Task Disable_ClosesPluginWindowsBeforeTheBackendStops()          // recorded order: "windows-closed", "backend-disposed", "deactivated"
[Fact] public async Task Dispose_ClosesPluginWindowsBeforeTheBackendStops()          // the same recorded order through DisposeAsync (app shutdown)
[Fact] public async Task Teardown_WindowsThatNeverClose_AreAbandonedAfterTheTimeout() // CloseAllAsync never completes; WindowCloseTimeout 100 ms -> DisableAsync returns, backend disposed, the warn line logged
[Fact] public async Task Teardown_SealsTheRuntimesWindowHost_AndTheNextRuntimeGetsAFreshOne() // fix round 1: a late Attach on the old host closes at once; re-enable gets a new, unsealed host
[Fact] public async Task Rescan_DroppedPlugin_ClosesItsWindows()
[Fact] public async Task Disable_BumpsTheRecordEpoch()
// PluginWindowSetTests (fix round 1)
[Fact] public void TryTrack_AfterTheSeal_IsRefusedAndLeavesNoTrackedWindowOpen()
[Fact] public async Task TryTrack_RacingTheSeal_EveryAcceptedWindowIsInTheSealResult()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManifestReaderTests|FullyQualifiedName~PluginContributionAdapterTests|FullyQualifiedName~PluginManagerTests"`
Expected: compile FAIL (the new SDK and host types are missing).

- [ ] **Step 3: Implement the SDK contracts, the manifest model and reader, the adapter, the shared activation path, `UiThread`, `WindowsFor`, `ValidateUiElement`, `WindowCloseTimeout` and `Epoch`.** `PluginRuntime` and `PluginHostServices` get `Windows` from `WindowsFor(record.Id)`. Keep one `IPluginWindowHost` per runtime.

- [ ] **Step 4: Regenerate the SDK surface snapshot deliberately.** Run `PluginSdkSurface.Describe` from a throwaway test, save the result as `Fixtures/PluginSdkSurface.v1.txt`, then delete the throwaway. Review the diff: it must add only the new SDK members.

- [ ] **Step 5: Run the full C# suite and the solution build**

Run: `npm run check:winui` then `npm run build:winui`
Expected: `Passed!`, including `PublicSurface_MatchesPinnedSnapshot`; the solution builds with 0 warnings.

- [ ] **Step 6: Spec and notes**
  - §4.4: `sidebar { id, title, provider, command? }`, with the header command button.
  - §5.1:
    - the SDK stays WinUI-free and hands UI objects across as `object`; the host checks the type;
    - plugin UI projects reference the Windows App SDK compile-only;
    - the thread contract;
    - the theme resource keys plugins may read: `SfTextPrimaryBrush`, `SfTextMutedBrush`, `SfBgPrimaryBrush`, `SfBgSecondaryBrush`, `SfBorderBrush`, `SfAccentSoftBrush`, `SfIconFont`, `SfGhostButtonStyle`, `SfTextBoxStyle`, `SfEmptyHintStyle`, `SfSidebarListStyle` and `SfNavListItemStyle`;
    - the version stays `1.0.0` until phase 5;
    - this replaces "It references the Windows App SDK so plugins can build WinUI controls", and the phase 1 ruling's expectation of WinUI-typed contracts with a retarget and an SDK minor bump. It fixes the shape of a public contract that phase 5 publishes, so Self-review lists it as a decision for the user to confirm. Record the user's answer here.
  - §7: plugin sections render below the built-in ones, with headers from the manifest; plugin windows; the teardown order, with its bounded window close.
  - §8.2: one page per Settings window.
  - Review notes: Minor 1 is closed by Tasks 7 and 8. Task 7 defines the contract; Task 8 supplies the App's UI thread.

- [ ] **Step 7: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 8: App: plugin sidebar sections, the UI-thread contract, a smoke data root and the Hello fixture's section

**Files:**
- Modify: `src-winui/SimpleFile.App/SidebarView.xaml`: after `TagsSection`, add `<StackPanel x:Name="PluginSectionsHost" Spacing="10" />`. Modify `SidebarView.xaml.cs`: accessor `public StackPanel PluginSections => PluginSectionsHost;`.
- Create: `src-winui/SimpleFile.App/MainWindow.PluginSidebar.cs` and `src-winui/SimpleFile.App/PluginUiThread.cs`.
- Modify: `src-winui/SimpleFile.App/MainWindow.Plugins.cs`:
  - `UiThread = new PluginUiThread(DispatcherQueue)`;
  - `ValidateUiElement = o => o is FrameworkElement ? null : $"returned {o.GetType().FullName}, not a Microsoft.UI.Xaml.FrameworkElement"`;
  - `DataRoot = PluginDiscovery.DataRoot()`;
  - `ApplyPluginContributions` calls `SyncPluginSidebarSections(enabled)`.
- Modify: `src-winui/SimpleFile.App/MainWindow.xaml.cs`: once startup marks `ready`, allow section activation at low dispatcher priority (beside `QueuePluginConsentPrompts`).
- Modify: `src-winui/SimpleFile.PluginHost/Discovery/PluginDiscovery.cs`: `public const string DataRootVariable = "SUMAFILE_PLUGIN_DATA_ROOT"`; `public static string DataRoot()`, which returns the override, else `%LOCALAPPDATA%\SumaFile\plugins-data`; `DescribeOverrides` adds `[warn] SUMAFILE_PLUGIN_DATA_ROOT replaces the plugin data folder: <path>`.
- Create: `src-winui/SimpleFile.Core/PluginSidebarLayout.cs`. Modify: `src-winui/SimpleFile.Core/{UiSettings.cs, WorkspaceSettingsStore.cs}` (setting `sidebar.pluginSectionsCollapsed`).
- Modify: `src-winui/SimpleFile.Core/SavedWorkspaceLayout.cs`. `WorkspaceChromeLayout` captures, applies and normalizes the new list as it does the three built-in collapse flags (`:167-174`, `:212-214`, `:236-238`). Switching a saved layout then restores plugin-section collapse too.
- Modify: `scripts/smoke-winui-ui.ps1`: export `SUMAFILE_PLUGIN_DATA_ROOT=<testRoot>\plugins-data` in every mode, plain included, before the plugin-mode branches. The plain run (`installer-smoke.yml:129`, or any local run without `-PluginsDir` or `-NoPlugins`) also stages the bundled plugins. From Task 12 on, its remote sidebar section starts the backend, which would otherwise write to the real `%LOCALAPPDATA%\SumaFile\plugins-data`.
- Modify the Hello fixture:
  - `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/SimpleFile.Tests.Fixtures.HelloPlugin.csproj`: TFM 19041, plus the compile-only WASDK and BuildTools references from Global Constraints;
  - `plugin.json`: `"sidebar": [ { "id": "hello", "title": "Hello section", "provider": "hello-section", "command": "greet" } ]`;
  - `HelloPlugin.cs`: implements `ISidebarSectionProvider`;
  - new `HelloUi.cs`.
- Modify: `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj`: the Hello `ProjectReference` gains `SkipGetTargetFrameworkProperties="true"`.
- Modify: `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs` (`SmokePluginsAsync`).
- Test: `src-winui/SimpleFile.Tests/{PluginSidebarLayoutTests.cs, WorkspaceSettingsStoreTests.cs, UiImprovementTests.cs, PluginEnvironmentTests.cs, PluginLoadingTests.cs}`.
- Modify: spec §7; the review notes' Phase 3 closures (the ledger Task 16 "smoke writes plugin data" line: the UI smoke part is closed).

**Interfaces:**
- Consumes: Task 7's `CreateSidebarSectionAsync`, `PluginContributionAdapter.SidebarSections`, `PluginSidebarSection`, `IPluginUiThread`, `ValidateUiElement`.
- Produces, `SimpleFile.Core`:
  ```csharp
  public static class PluginSidebarLayout
  {
      public static string Key(PluginSidebarSection section);        // "<pluginId>:<sectionId>:<epoch>"
      public static string CollapseKey(PluginSidebarSection section); // "<pluginId>:<sectionId>"
      public static (IReadOnlyList<PluginSidebarSection> Add, IReadOnlyList<string> RemoveKeys) Diff(IReadOnlyList<PluginSidebarSection> wanted, IReadOnlyCollection<string> shownKeys);
      public static bool ShouldActivate(bool startupReady, bool sidebarVisible, bool collapsed, bool activated);
  }
  // UiSettings gains: public List<string> CollapsedPluginSidebarSections { get; set; } = [];   // persisted as JSON array under "sidebar.pluginSectionsCollapsed"
  // WorkspaceChromeLayout gains: [JsonPropertyName("pluginSectionsCollapsed")] public List<string> CollapsedPluginSidebarSections { get; set; } = [];
  //   Normalize drops empty and duplicate keys; a layout saved before this field restores an empty list.
  ```
- Produces, App:
  - `PluginUiThread(DispatcherQueue)` implements `IPluginUiThread`: inline when it has thread access, otherwise `TryEnqueue` plus a `TaskCompletionSource`. It shares `PluginUiDispatch.RunAsync` with `PluginDialogs`.
  - Each plugin section is a `StackPanel` with `AutomationProperties.AutomationId` `PluginSection.<pluginId>.<sectionId>`. It holds:
    - a header in `SfSidebarHeaderStyle` with the manifest title;
    - a collapse button `PluginSection.<pluginId>.<sectionId>.Collapse`, glyph E70D;
    - when `CommandId` is set, an icon button `PluginSection.<pluginId>.<sectionId>.Command` (glyph E713, tooltip and automation name `CommandLabel`), which runs `RunPluginCommandAsync(CommandId)`;
    - a `ContentPresenter` body.
  - The body loads through `CreateSidebarSectionAsync` when `ShouldActivate` is true. That is re-checked at ready, when the sidebar is shown and when the section is expanded. A failure leaves the muted text `This section could not be loaded: <message>`.
- Hello:
  - `HelloPlugin` has no field, property or base type from WinUI. The WinUI code lives in `HelloUi`, which only provider methods reach.
  - `CreateSidebarSection` returns a `TextBlock` with `Text = "Hello from the fixture"` and AutomationId `Hello.SidebarSection`.
  - It writes the marker `sidebar.txt` containing the section id.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Diff_AddsNewSectionsAndRemovesWithdrawnOnes()
[Fact] public void Diff_ANewEpochReplacesTheSection()                     // same plugin/section, epoch 1 -> 2: remove old key, add new
[Theory] [InlineData(true, true, false, false, true)] [InlineData(false, true, false, false, false)] [InlineData(true, false, false, false, false)]
         [InlineData(true, true, true, false, false)] [InlineData(true, true, false, true, false)]
public void ShouldActivate_OnlyWhenReadyVisibleExpandedAndNotYetActivated(bool ready, bool visible, bool collapsed, bool activated, bool expected)
[Fact] public async Task PluginSectionCollapse_RoundTripsThroughWorkspaceSettings()   // WorkspaceSettingsStoreTests
[Fact] public void PluginSectionCollapse_RoundTripsThroughASavedLayout()               // UiImprovementTests: Capture -> JSON -> Apply keeps ["com.a.b:s"]; JSON without the field -> []
[Fact] public void DescribeOverrides_ReportsTheDataRootOverride()                      // PluginEnvironmentTests (Plugin environment collection)
[Fact] public void HelloEntry_ImplementsTheSidebarProviderAndLoadsWithoutWinUi()      // PluginLoadingTests: CreateInstance(Hello) is ISidebarSectionProvider
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginSidebarLayoutTests|FullyQualifiedName~WorkspaceSettingsStoreTests|FullyQualifiedName~UiImprovementTests|FullyQualifiedName~PluginEnvironmentTests|FullyQualifiedName~PluginLoadingTests"`
Expected: compile FAIL.

- [ ] **Step 3: Implement** the Core layout helpers, the setting and its `WorkspaceChromeLayout` field; `DataRoot` and the smoke script's export; retarget the Hello fixture; then the App section host, `PluginUiThread` and the activation triggers.

- [ ] **Step 4: Add the smoke checks.** In `SmokePluginsAsync` (`with` mode):
  - The sidebar has the element `PluginSection.com.sumafile.hello.hello`, whose header text is `Hello section`.
  - Within 10 s its body holds `Hello.SidebarSection`, and the marker `sidebar.txt` reads `hello`.
  - Invoking `PluginSection.com.sumafile.hello.hello.Command` through its automation peer appends `plugin:com.sumafile.hello:greet` to `commands.log`.
  - Collapse, then expand, keeps the body.

  In `none` mode: `PluginSections.Children.Count == 0`.

  In both modes: `PluginDiscovery.DataRoot()` equals `SUMAFILE_PLUGIN_DATA_ROOT` and lies under `SIMPLEFILE_UI_SMOKE_ROOT`.

- [ ] **Step 5: Run the suite and both smokes**

Run:
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`
- `npm run check:winui`
- `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`
- `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`

Expected: `Fixture plugin staged at:`, `Passed!`, then `UI runtime smoke passed.` twice, with `plugins=1` in both scenario lists.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 9: App: plugin windows, the custom settings page slot, cancel-aware plugin rows, and the Hello window, page and dialog checks

**Files:**
- Create: `src-winui/SimpleFile.App/PluginWindowHost.cs`.
- Modify: `src-winui/SimpleFile.App/MainWindow.Plugins.cs` (`WindowsFor`, which creates a new `PluginWindowHost` on every call and stores it in a per-plugin dictionary the smoke can read, replacing the previous runtime's sealed host; `ClosePluginWindows()`); `PluginProgressSink.cs:85-91` (the runner uses `PluginOperationWait`).
- Modify: `src-winui/SimpleFile.App/MainWindow.xaml.cs`. `OnClosed` calls `ClosePluginWindows()` right after `CloseRemoteManagerWindow()` (`:1355`, which Task 14 deletes), before the workspace-save awaits, where the remote manager window closes today.
- Modify: `src-winui/SimpleFile.Core/TransferManagerViewModel.cs` (`CanChangeOptions`) and `src-winui/SimpleFile.App/TransferProgressWindow.xaml(.cs)` (the per-task Options button, `:105-107` and `OnTaskOptionsClicked`).
- Modify: `src-winui/SimpleFile.App/{SettingsWindow.Plugins.cs, PluginSettingsPanelBuilder.cs, SettingsWindow.xaml.cs}`:
  - the page placeholder and the per-window cache;
  - `SearchableElements` also walks `UserControl.Content`;
  - the current search reruns after a page is inserted.
- Create: `src-winui/SimpleFile.Core/{PluginOperationWait.cs, PluginPageCache.cs}`.
- Modify the Hello fixture:
  - `plugin.json`: adds `"settingsPage": { "provider": "hello-page" }` and the commands `open-window` (label `Hello: open window`) and `show-dialog` (label `Hello: show dialog`);
  - `HelloPlugin.cs`: implements `ISettingsPageProvider` and the two commands;
  - `HelloUi.cs`.
- Modify: `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`; `src-winui/SimpleFile.Tests/UiAuditRegressionTests.cs:1007-1036` (the windows list adds `PluginWindowHost.cs`, which sizes through `SecondaryWindowChrome.Apply`).
- Test: `src-winui/SimpleFile.Tests/{PluginOperationWaitTests.cs, PluginPageCacheTests.cs, TransferManagerViewModelTests.cs, WinUiSourceShapeTests.cs}`.
- Modify: spec §6.3 (plugin rows after Cancel; their per-task Options button is disabled) and §8.2 (search re-indexes the inserted page).

**Interfaces:**
- Consumes: Task 7's `IPluginWindowHost` (with its seal), `PluginWindowSet<TWindow>`, `CreateSettingsPageAsync`, `PluginRecord.Epoch` and the App's `PluginDialogs.ShowDialogAsync`; `SecondaryWindowChrome.Apply` (App internal).
- Produces, App:
  - `PluginWindowHost(string pluginId, DispatcherQueue queue)` implements `IPluginWindowHost`.
    - `Attach` throws `ArgumentException("A plugin window must be a Microsoft.UI.Xaml.Window.")` for any other object.
    - It keeps a Task 7 `PluginWindowSet<Window>`. When `TryTrack(window)` returns false the host is sealed (Task 7's `IPluginWindowHost.CloseAllAsync` contract): `Attach` closes the window at once, applies no chrome and tracks nothing.
    - Otherwise it calls `SecondaryWindowChrome.Apply(window, options.Title, options.Width, options.Height)` and tracks the window until `Closed`, which calls `Remove`.
    - `CloseAllAsync` first calls `Seal()` on the calling thread, before anything is enqueued, so the seal holds even when the dispatcher is gone. It then closes the windows `Seal()` returned on the UI thread. It returns a completed task when no window is tracked, and when `TryEnqueue` returns false: the dispatcher is shutting down, and the windows go with it. A window that is already closed is skipped, never thrown on. The manager's `WindowCloseTimeout` (Task 7) bounds the rest.
    - `internal void CloseAllNow()` seals the host too, then closes every tracked window on the calling UI thread. `MainWindow.ClosePluginWindows()` calls it for every plugin from `OnClosed`, so plugin windows close as soon as the main window does. The later teardown then finds nothing to close.
    - `public IReadOnlyList<Window> OpenWindows { get; }` serves the smoke.
  - Transfers window: `TransferOperationViewModel` gains `public bool CanChangeOptions => CanCancel && !IsExternal;`, raised together with `CanCancel` (`:458`).
    - The per-task Options button binds `IsEnabled` to it, and `OnTaskOptionsClicked` returns at once for an external row.
    - External rows (plugin rows and archive-engine rows) never read `transfer.overrides.v1`. Only the service's local engine does, through `Policy::for_operation`. A remote transfer's settings live in the plugin's settings and its own Transfer options dialog (Task 11).
  - Settings page slot: for an Enabled record with `SettingsPage`, the card gets a `ContentPresenter` with AutomationId `PluginSettingsPage.<pluginId>`, below the declarative settings and above the status line.
    - It is filled from a `PluginPageCache<FrameworkElement>` keyed by `(pluginId, Epoch)`, or else from `CreateSettingsPageAsync`.
    - A cached element is detached from its old presenter before it is re-added.
    - A failure shows `This settings page could not be loaded: <message>`.
    - Records that are not Enabled never load code.
- Produces, Core:
  ```csharp
  public static class PluginOperationWait
  {
      /// The runner of a plugin's Transfers row. Before a cancel: Completed when `finished` completes, its exception when it fails.
      /// After `cancellation` fires the row stays Cancelling until `finished` ends (completed or failed) or `cancelGrace` passes, then reports Cancelled.
      public static Task<TransferOperationStatus> WaitAsync(Task finished, CancellationToken cancellation, TimeSpan cancelGrace);
  }
  public sealed class PluginPageCache<T> where T : class
  {
      public bool TryGet(string pluginId, int epoch, out T? page);
      public void Store(string pluginId, int epoch, T page);   // replaces any other epoch of that plugin
      public void Remove(string pluginId);
  }
  ```
  - `PluginProgressSink` calls `WaitAsync(entry.Finished.Task, cancellationToken, TimeSpan.FromSeconds(30))`.
- Hello:
  - `CreateSettingsPage` returns a `StackPanel` with a `TextBlock` `Hello page` (AutomationId `Hello.SettingsPage`) and a `ToggleSwitch` with header `Hello page switch`. It writes the marker `settings-page.txt`.
  - `open-window` creates a `HelloWindow`, calls `host.Windows.Attach(window, new PluginWindowOptions("Hello window", 480, 320))`, activates it and writes `window.txt`.
    - `HelloWindow` is a small `Window` subclass built in code in `HelloUi.cs`, with no `InitializeComponent`.
    - Its constructor sets `Content` from `XamlReader.Load` of a fragment: a `StackPanel` holding a `TextBlock` (AutomationId `Hello.WindowBody`, `Text="Hello window body"`, `Foreground="{ThemeResource SfTextPrimaryBrush}"`) and a `Button` named `WindowDialogButton` (AutomationId `Hello.WindowDialog`).
    - The code finds the button with `FindName` and wires `Click`. The click shows `ContentDialog { Title = "Hello window dialog", CloseButtonText = "Close", XamlRoot = Content.XamlRoot }` through `host.Dialogs.ShowDialogAsync`, then appends `window:<result>` to `dialog.txt`.
    - So the three patterns `SumaFile.Remote.dll` depends on run here before the remote plugin exists: a WinRT `Window` subclass from a plugin load context, theme-aware `XamlReader` fragments, and a dialog on a plugin window's own `XamlRoot`.
  - `show-dialog` shows a `ContentDialog { Title = "Hello dialog", CloseButtonText = "Close" }` through `host.Dialogs.ShowDialogAsync` and appends the result to `dialog.txt`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task WaitAsync_CompletedOperation_ReportsCompleted()
[Fact] public async Task WaitAsync_FailedOperation_Throws()
[Fact] public async Task WaitAsync_CancelThenFail_ReportsCancelledOnlyAfterThePluginFinished() // not completed 200 ms after cancel; Cancelled after the fail
[Fact] public async Task WaitAsync_CancelWithoutAnAnswer_ReportsCancelledAfterTheGrace()        // grace 100 ms
[Fact] public void Cache_ReturnsTheStoredPageForTheSameEpoch()
[Fact] public void Cache_AnotherEpochMisses()
[Fact] public void Cache_RemoveForgetsThePlugin()
[Fact] public void ExternalOperation_OffersNoPerTaskOptions()               // TransferManagerViewModelTests: an EnqueueExternal row -> CanCancel true, CanChangeOptions false; a local copy row -> both true
[Fact] public void TaskOptionsButton_IsBoundToCanChangeOptions()            // WinUiSourceShapeTests: TransferProgressWindow.xaml binds the per-task Options IsEnabled to CanChangeOptions
[Fact] public void OnClosed_ClosesPluginWindowsBeforeTheSessionCleanup()    // WinUiSourceShapeTests: in OnClosed, "ClosePluginWindows();" precedes "await CleanupSessionAsync("
[Fact] public void PluginWindowHost_AttachAfterTheSeal_ClosesTheWindow()   // WinUiSourceShapeTests: Attach closes the window when TryTrack returns false, before any chrome; CloseAllAsync and CloseAllNow call Seal() before TryEnqueue or any close. The seal's behaviour (an Attach after CloseAllAsync leaves no tracked window open, and the race) is pinned by Task 7's PluginWindowSetTests
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginOperationWaitTests|FullyQualifiedName~PluginPageCacheTests|FullyQualifiedName~TransferManagerViewModelTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: compile FAIL.

- [ ] **Step 3: Implement** the Core helpers, `PluginWindowHost` with `CloseAllNow`, `ClosePluginWindows` in `OnClosed`, `CanChangeOptions` and the Options binding, the settings page slot and the search changes, then the Hello additions (`HelloWindow` included).

- [ ] **Step 4: Add the smoke checks.** In `with` mode:
  1. Run `plugin:com.sumafile.hello:open-window`. Within 10 s Hello's `PluginWindowHost.OpenWindows` holds one window titled `Hello window`.
     - Its type is the plugin's `HelloWindow`, and its content's `RequestedTheme` equals the main window's.
     - `Hello.WindowBody` shows `Hello window body`, with the theme's `SfTextPrimaryBrush` as its foreground.
     - Invoke `Hello.WindowDialog` through its automation peer. A `ContentDialog` titled `Hello window dialog` opens on the Hello window's own `XamlRoot` (`VisualTreeHelper.GetOpenPopupsForXamlRoot`). Close it through its automation peer; the last line of `dialog.txt` reads `window:None`.
  2. Run `plugin:com.sumafile.hello:show-dialog`. A popup `ContentDialog` titled `Hello dialog` opens on the main window's XamlRoot, with `DialogHost.IsDialogOpen` true. Close it through its automation peer; the last line of `dialog.txt` reads `None`.
  3. Open Settings > Plugins through the existing Settings path. The Hello card contains `PluginSettingsPage.com.sumafile.hello`, which holds `Hello.SettingsPage`. Typing `Hello page switch` into settings search shows the Plugins panel with that switch.
  4. `manager.DisableAsync("com.sumafile.hello")` closes `Hello window` and removes the Hello sidebar section within 10 s. `EnableAsync` brings the section back.

- [ ] **Step 5: Run the suite and both smokes**

Run:
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`
- `npm run check:winui`
- `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`
- `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`

Expected: all pass; `UI runtime smoke passed.` twice.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 10: Remote plugin model assembly: DTOs, the backend client, the view model and their tests

**Files:**
- Create: `plugins/remote/ui/Directory.Build.props`, which imports `..\..\..\src-winui\Directory.Build.props` and sets `Version` and `InformationalVersion` `1.0.0`. Create `plugins/remote/ui/Directory.Build.targets`, which imports `src-winui`'s.
- Create: `plugins/remote/ui/SumaFile.Remote.Model/SumaFile.Remote.Model.csproj` (`net10.0-windows`), with:
  - a reference to `SimpleFile.PluginSdk` with `<Private>false</Private><ExcludeAssets>runtime</ExcludeAssets>`;
  - `CommunityToolkit.Mvvm` `8.4.0` with `ExcludeAssets="runtime" PrivateAssets="all"`;
  - its `packages.lock.json`.
- Create the sources in that folder: `RemoteModels.cs`, `RemoteProtocol.cs`, `IRemoteBackend.cs`, `RemoteBackendClient.cs`, `LocalFolderSource.cs`, `RemoteManagerViewModel.cs`, `RemoteTransferRecord.cs`, `RemoteProfileRow.cs`, `RemoteTransferSettings.cs`.
  - `RemoteManagerViewModel.cs` and `RemoteTransferRecord.cs` are ported from `src-winui/SimpleFile.Core/RemoteManagerViewModel.cs`, which stays until Task 14.
  - `RemoteProfileRow.cs` is ported from `ExplorerRows.cs:217-254`.
- Modify: `src-winui/SimpleFile.sln` (add the project); `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj` (`ProjectReference` to the Model project).
- Create the tests `src-winui/SimpleFile.Tests/Remote/{FakePluginHost.cs, FakeRemoteBackend.cs, RemoteManagerViewModelTests.cs, RemoteBackendClientTests.cs, LocalFolderSourceTests.cs, RemoteProfileRowTests.cs}`, in namespace `SimpleFile.Tests.Remote`.
- Modify: `scripts/check-ipc-schema.mjs`. The remote block also requires each method and event literal in `plugins/remote/ui/SumaFile.Remote.Model/RemoteProtocol.cs`.

**Interfaces:**
- Consumes: Task 6 wire shapes (`plugins/remote/protocol.json`); SDK `IPluginHost`, `IPluginSettings`.
- Produces, namespace `SumaFile.Remote.Model`:
  - DTOs with `[JsonPropertyName]` on every property, matching the backend byte for byte:
    - `RemoteProfile` and `RemoteProfileInput`, snake_case;
    - `RemoteSession` (`session_id`, `profile_id`, `protocol`, `root_path`);
    - `RemoteConnectionTestResult` (`ok`, `message`, `capabilities`);
    - `RemoteFileEntry` (the twelve `FileEntry` fields) and `RemoteDirectoryListing`;
    - `RemoteTransferRequest`, camelCase params;
    - `RemoteTransferItem` and `RemoteTransferOutcome`, snake_case;
    - `RemoteTransferProgress` and `RemoteTransferTuning`, camelCase events.
  - Method and event names:
    ```csharp
    public static class RemoteMethods { public const string ListProfiles = "profiles.list"; public const string SaveProfile = "profiles.save"; public const string DeleteProfile = "profiles.delete";
        public const string TestProfile = "profiles.test"; public const string Connect = "session.connect"; public const string Disconnect = "session.disconnect";
        public const string ListDirectory = "fs.list"; public const string CreateDirectory = "fs.create_directory"; public const string Rename = "fs.rename";
        public const string DeleteEntries = "fs.delete"; public const string Transfer = "transfer.run"; public const string Cancel = "cancel"; }
    public static class RemoteEvents { public const string TransferProgress = "remote.transfer-progress"; public const string TransferTuning = "remote.transfer-tuning"; public const string ProfilesChanged = "remote.profiles-changed"; }
    ```
  - The backend interface and its client:
    ```csharp
    public interface IRemoteBackend
    {
        Task<IReadOnlyList<RemoteProfile>> ListProfilesAsync(CancellationToken ct);
        Task<RemoteProfile> SaveProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct);
        Task DeleteProfileAsync(string profileId, CancellationToken ct);
        Task<RemoteConnectionTestResult> TestProfileAsync(RemoteProfileInput profile, string? secret, CancellationToken ct);
        Task<RemoteSession> ConnectAsync(string profileId, string? secret, CancellationToken ct);
        Task DisconnectAsync(string remoteSessionId, CancellationToken ct);
        Task<RemoteDirectoryListing> ListDirectoryAsync(string remoteSessionId, string path, CancellationToken ct);
        Task<RemoteFileEntry> CreateDirectoryAsync(string remoteSessionId, string path, string name, CancellationToken ct);
        Task<RemoteFileEntry> RenameEntryAsync(string remoteSessionId, string path, string newName, CancellationToken ct);
        Task<IReadOnlyList<string>> DeleteEntriesAsync(string remoteSessionId, IReadOnlyList<string> paths, CancellationToken ct);
        /// Sends transfer.run with CancellationToken.None. When `ct` fires it sends one `cancel {operationId}` with the request's id and keeps awaiting the outcome.
        Task<RemoteTransferOutcome> TransferAsync(RemoteTransferRequest request, IProgress<RemoteTransferProgress>? progress, IProgress<RemoteTransferTuning>? tuning, CancellationToken ct);
        IDisposable OnProfilesChanged(Action handler);
    }
    public sealed class RemoteBackendClient(IPluginHost host) : IRemoteBackend;   // progress and tuning events are filtered by operationId
    public interface ILocalFolderSource { Task<RemoteDirectoryListing> ListAsync(string path, CancellationToken ct); }
    public sealed class LocalFolderSource : ILocalFolderSource;   // System.IO; folders first, then names (ordinal-ignore-case); Modified as RFC 3339; Parent from DirectoryInfo.Parent
    ```
  - `RemoteManagerViewModel(IRemoteBackend backend, ILocalFolderSource localFolders)`:
    - keeps every public member of today's view model except `TransferSettingsBackend`;
    - keeps `UntrustedFingerprintPrefix`, `FingerprintMismatchPrefix`, `ParseConnectError`, `IsInsecureProfile` and `RequestProfileSelection`;
    - local refreshes compare paths with `string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)`.
  - New in the view model: an error that starts with `remote session not found` clears `CurrentSession` and sets `StatusText` to `The connection was lost. Connect again.`
  - `RemoteProfileRow` has `Name`, `Description`, `Protocol`, `IsInsecure` and `ProfileId`, and `static RemoteProfileRow From(RemoteProfile profile)`. `Description` is the exact text `ExplorerRows.FormatDescription` (`ExplorerRows.cs:244-253`) produces today; port that function unchanged.
  - `RemoteTransferSettings`: the eight keys of Task 6, their defaults, and `Task<RemoteTransferSettings> LoadAsync(IPluginSettings settings, CancellationToken ct)` / `Task SaveAsync(IPluginSettings settings, CancellationToken ct)`. It writes only changed keys.
    - `public static IReadOnlyDictionary<string, string> DefaultValues` gives each key's default as its plugin setting string (`"0"`, `"size_and_mod_time"`, `"false"`, `"true"`, `"true"`, `"10"`, `"3"`, `"1000"`). Task 11 checks it against the manifest.

- [ ] **Step 1: Port and write the failing tests**
  - `Remote/RemoteManagerViewModelTests.cs`: the 19 tests of `src-winui/SimpleFile.Tests/RemoteManagerViewModelTests.cs`, with the same names. The fake is `FakeRemoteBackend : IRemoteBackend` instead of `RemoteProfileIpc : NullIpc`, and `CancelWaitsForBackendCleanupAndRetainsPartialResults` keeps its semantics.
  - New tests:

```csharp
[Fact] public async Task ConnectSelectedProfileAsync_ReturnsAMismatchOutcomeWithBothFingerprints() // backend throws the exact mismatch text -> MismatchExpected/MismatchActual set, no session
[Fact] public async Task RemoteCalls_AfterTheBackendRestarted_DropTheLostSessionAndAskToReconnect() // "remote session not found: s1" -> CurrentSession null, StatusText "The connection was lost. Connect again."
// RemoteBackendClientTests (FakePluginHost records method, args JSON and cancel calls; raises events)
[Fact] public async Task ClientMethods_UseTheProtocolNamesAndParamShapes()     // each method -> name and camelCase params as protocol.json
[Fact] public async Task ListProfiles_ReadsSnakeCaseFieldsAndCarriesNoSecret()
[Fact] public async Task Transfer_CancelSendsOneCancelWithTheRequestIdAndReturnsTheOutcome()
[Fact] public async Task Transfer_ReportsOnlyItsOwnProgressAndTuningEvents()
[Fact] public async Task ProfilesChanged_InvokesTheHandlerUntilDisposed()
// LocalFolderSourceTests
[Fact] public async Task List_PutsFoldersFirstAndKeepsUnicodeNames()        // "café 資料", "literal [brackets] & spaces.txt"
[Fact] public async Task List_ReportsTheParent()
// RemoteProfileRowTests
[Fact] public void IsInsecure_OnlyForPlainFtpWithConsent()
[Fact] public void Description_MatchesTheTextTheBuiltInSidebarShowed() // the expected strings are copied from today's ExplorerRows.FormatDescription output
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~SimpleFile.Tests.Remote"`
Expected: compile FAIL, because the Model project does not exist yet.

- [ ] **Step 3: Create the project, restore offline and implement**

Run: `dotnet restore plugins/remote/ui/SumaFile.Remote.Model/SumaFile.Remote.Model.csproj --force-evaluate`, then `dotnet restore src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj --force-evaluate`
Expected: both restore from the local cache, and `packages.lock.json` is written.

- [ ] **Step 4: Run the new tests and the full suite**

Run: the Step 2 command, then `npm run check:winui`
Expected: `Passed!`, including the old `SimpleFile.Tests.RemoteManagerViewModelTests`, which Task 14 deletes.

- [ ] **Step 5: Schema check**

Run: `npm run check:ipc-schema`
Expected: `Checked remote plugin protocol: 11 methods, 3 events, 5 error texts.`, now covering the C# literals.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 11: Remote plugin UI assembly and manifest: the code-built manager window, dialogs, sidebar section, settings page and transfer options

**Files:**
- Create: `plugins/remote/plugin.json`.
- Create: `plugins/remote/ui/SumaFile.Remote/SumaFile.Remote.csproj`, `AssemblyName` `SumaFile.Remote`, with the 19041 settings and the compile-only WASDK and BuildTools references from Global Constraints. It references the SDK with `Private=false` and `ExcludeAssets=runtime`, references the Model project normally, and keeps its `packages.lock.json`.
  - It also has `<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" ExcludeAssets="runtime" PrivateAssets="all" />`. The Model's `PrivateAssets="all"` Mvvm reference does not flow to projects that reference it. Without this line, `vm.PropertyChanged` and every `ObservableObject` member fail with CS0012 and CS1061 (reproduced offline in a scratch probe during review).
  - With the line, the same probe built offline with 0 warnings and emitted no `Microsoft.WinUI.dll`. `CommunityToolkit.Mvvm` is a shared prefix in `PluginLoadContext`, so the runtime copy still comes from the host.
- Create: `plugins/remote/backend/tests/manifest_defaults.rs` (the manifest is first written in this task).
- Create, in that folder: `RemotePlugin.cs` and `Views/{RemoteManagerWindow.cs, RemoteManagerWindow.Lists.cs, RemoteProfileDialog.cs, RemoteHostKeyDialog.cs, RemoteNameDialog.cs, RemoteTransferOptionsDialog.cs, RemoteSidebarSection.cs, RemoteSettingsPage.cs, RemoteAutomationIds.cs, Xaml.cs}`.
  - `RemoteManagerWindow.cs` ports `RemoteManagerWindow.xaml` and `.xaml.cs` to code.
  - `RemoteTransferOptionsDialog.cs` ports the `remote: true` branch of `TransferOptionsDialog.cs:43-173`.
- Modify: `src-winui/SimpleFile.sln` (add the project).
- Test: `src-winui/SimpleFile.Tests/PluginManifestReaderTests.cs` and `src-winui/SimpleFile.Tests/Remote/RemotePluginSourceShapeTests.cs`.
- Modify: spec §11.2. The folder adds `SumaFile.Remote.Model.dll` and `SumaFile.Remote.deps.json`. Record the contributions, the eight settings and the window lifetime.

**Interfaces:**
- Consumes: Task 7 SDK contracts; Task 10 Model (`RemoteBackendClient`, `RemoteManagerViewModel`, `RemoteProfileRow`, `RemoteTransferSettings`, `LocalFolderSource`).
- `plugins/remote/plugin.json`:
  ```json
  {
    "id": "com.sumafile.remote", "name": "FTP/SFTP connections", "version": "1.0.0",
    "publisher": { "name": "SumaFile Team", "url": "https://github.com/conniecombs/SumaFile" },
    "sdkVersion": "1.0.0",
    "ui": { "assembly": "SumaFile.Remote.dll", "entryType": "SumaFile.Remote.RemotePlugin" },
    "backend": { "executable": "sumafile-remote-backend.exe", "args": [], "activateOn": "first-call", "acceptsMultipleClients": false },
    "permissions": ["network", "credentials", "backend-process"],
    "contributions": {
      "commands": [ { "id": "ftp-sftp-manager", "label": "FTP/SFTP manager", "group": "Tools" } ],
      "toolbar": [ { "command": "ftp-sftp-manager", "label": "FTP/SFTP manager", "glyph": "\uE968", "placement": "overflow" } ],
      "sidebar": [ { "id": "connections", "title": "Connections", "provider": "connections", "command": "ftp-sftp-manager" } ],
      "settingsPage": { "provider": "remote-settings" }
    },
    "settings": [
      { "key": "bandwidthMib", "type": "integer", "label": "Bandwidth limit (MB/s, 0 = unlimited)", "default": 0, "group": "Transfers" },
      { "key": "checkMode", "type": "enum", "label": "Decide a file is already up to date by", "options": ["size_and_mod_time","size_only","checksum","ignore_times"], "default": "size_and_mod_time", "group": "Transfers" },
      { "key": "inplace", "type": "boolean", "label": "Write downloads directly to the destination instead of a temporary file", "default": false, "group": "Transfers" },
      { "key": "preallocate", "type": "boolean", "label": "Pre-allocate local download space", "default": true, "group": "Transfers" },
      { "key": "flushOnClose", "type": "boolean", "label": "Flush local downloads to disk before finishing each file", "default": true, "group": "Transfers" },
      { "key": "lowLevelRetries", "type": "integer", "label": "Retry a failing file up to", "default": 10, "group": "Transfers" },
      { "key": "highLevelRetries", "type": "integer", "label": "Retry rounds per failed file", "default": 3, "group": "Transfers" },
      { "key": "retrySleepMs", "type": "integer", "label": "Wait before retrying (milliseconds)", "default": 1000, "group": "Transfers" }
    ]
  }
  ```
- `RemotePlugin : IPlugin, ICommandHandler, ISidebarSectionProvider, ISettingsPageProvider`:
  - `ActivateAsync` keeps the host and builds one `RemoteBackendClient`.
  - `ExecuteAsync("plugin:com.sumafile.remote:ftp-sftp-manager")` shows the single manager window, creating it or activating the existing one.
  - `internal void OpenManager(string? profileId)` serves the command, the sidebar rows and the settings page. It runs inside WinUI event handlers, where an escaping exception ends the App, so it goes through `SingleWindowSlot<RemoteManagerWindow>.TryShow` (Model, unit-tested): a failure is logged with `host.Logger.Error` and shown with `host.Dialogs.ShowMessageAsync`, and a window that failed to attach is closed and never kept. (Task 11 fix round 1.)
  - `CreateSidebarSection` returns a new `RemoteSidebarSection` per call.
  - `CreateSettingsPage` returns a new `RemoteSettingsPage` per call.
  - `DeactivateAsync` marshals to the window's `DispatcherQueue`, closes the window if it is still open, and disposes event subscriptions.
- The manager window is a plain `Window` subclass built in code (no `InitializeComponent`). Rules:
  - It calls `host.Windows.Attach(this, new PluginWindowOptions("FTP/SFTP manager", 1180, 680))`.
  - Every dialog goes through `host.Dialogs.ShowDialogAsync`. The connect progress dialog is a `ContentDialog` the window hides when the work ends.
  - Pickers use `WinRT.Interop.WindowNative.GetWindowHandle(this)`.
  - Layout, labels and glyphs follow `RemoteManagerWindow.xaml`. The plain-FTP consent text, the "Insecure" badge text and the conflict choices are copied.
  - The four lists stay virtualized: each binds a `CoalescingSnapshot<T>` copy of its view model collection (Model, unit-tested; one copy per UI-thread turn) as `ItemsSource`, with one `DataTemplate` parsed once. `ListRows.Attach` fills the realized rows in `ContainerContentChanging`; each realized container's `AutomationProperties.Name` is the shown name and its `Tag` the path or profile id. `ItemClick` handlers resolve the model through `ListRows.Clicked<T>`. (Task 11 fix round 1: one `ListViewItem` and one `XamlReader` parse per entry froze the host's UI thread on large folders.)
  - Theme-aware fragments come from `Xaml.Load(string)`, a thin `XamlReader.Load` wrapper, using `{ThemeResource …}` with the keys from spec §5.1.
  - The window never uses `{Binding}` or `x:Bind`: values come from `PropertyChanged`.
- `RemoteAutomationIds` constants. The smoke uses these exact strings:
  - Manager window: `Remote.ProfileList`, `Remote.NewProfile`, `Remote.EditProfile`, `Remote.DeleteProfile`, `Remote.Connect`, `Remote.Disconnect`, `Remote.Refresh`, `Remote.LocalPath`, `Remote.LocalGo`, `Remote.LocalList`, `Remote.RemotePath`, `Remote.RemoteList`, `Remote.Upload`, `Remote.Download`, `Remote.NewFolder`, `Remote.Rename`, `Remote.DeleteEntries`, `Remote.TransferList`, `Remote.CancelTransfer`, `Remote.RetryTransfer`, `Remote.TransferOptions`, `Remote.Conflict`, `Remote.Status`.
  - Profile dialog: `RemoteProfile.Name`, `RemoteProfile.Protocol`, `RemoteProfile.Host`, `RemoteProfile.Port`, `RemoteProfile.User`, `RemoteProfile.RootPath`, `RemoteProfile.Auth`, `RemoteProfile.Secret`, `RemoteProfile.SaveSecret`, `RemoteProfile.KeyPath`, `RemoteProfile.PassiveMode`, `RemoteProfile.AllowPlainFtp`.
  - Other dialogs: `RemoteHostKey.Fingerprint` and `Remote.NamePrompt`.
  - Sidebar and settings: `Remote.SidebarList`, `Remote.SidebarEmpty` and `Remote.SettingsOpenManager`.
- `RemoteSidebarSection`:
  - It shows `Remote.SidebarEmpty` (`No connections. Open the manager to add one.`) or the `Remote.SidebarList` rows. Each row shows the E968 icon, the name, `RemoteProfileRow.Description`, a protocol pill, an `Insecure` badge when `IsInsecure`, and the trailing E8A7 glyph.
  - It reloads on `OnProfilesChanged` and when it is first shown.
  - A row click calls `OpenManager(profileId)`.
  - A listing failure shows `Could not list connections: <message>`.
- `RemoteSettingsPage`: a `StackPanel` with a description and the button `Remote.SettingsOpenManager` (`Open FTP/SFTP manager`), which calls `OpenManager(null)`.
- `RemoteTransferOptionsDialog` edits the eight settings through `RemoteTransferSettings`, with the labels from the manifest. Its footnote is `Remote sessions copy one file at a time, and checksum verification downloads each file again.`

- [ ] **Step 1: Write the failing tests**

```csharp
// PluginManifestReaderTests
[Fact] public void RemoteManifest_InTheRepo_IsValid()  // reads plugins/remote/plugin.json: one command, overflow toolbar, sidebar "connections" with command, settingsPage, the 8 settings keys valid, each in group "Transfers"
[Fact] public void RemoteTransferSettings_DefaultsMatchTheManifest()  // the manifest declares exactly RemoteTransferSettings.DefaultValues' keys; each default, as a setting string, equals DefaultValues[key]
// Remote/RemotePluginSourceShapeTests (reads files under plugins/remote by repo path)
[Fact] public void RemoteUi_HasNoCompiledXamlOrBindings()          // no *.xaml under plugins/remote/ui; no "{Binding" or "x:Bind" in *.cs; csproj has no UseWinUI
[Fact] public void RemoteUi_UsesEveryAutomationId()                // every RemoteAutomationIds constant is referenced outside RemoteAutomationIds.cs
[Fact] public void RemoteUi_UsesTheHostWindowAndDialogServices()   // "Windows.Attach(" in RemoteManagerWindow.cs; no "ContentDialog.ShowAsync" or ".ShowAsync()" on dialogs
[Fact] public void RemoteUi_AsksForPlainFtpConsentAndTrustsHostKeysExplicitly() // the code-built forms of UxRecommendationsSettingsGitRemoteTests.cs:188-217 (list below)
[Fact] public void RemoteBackend_HostKeyMessagesKeepTheViewModelPrefixes() // RemoteManagerViewModel prefixes appear in plugins/remote/backend/src/sftp.rs
[Fact] public void RemotePlugin_ProjectsReferenceOnlyTheSdkAndEachOther()   // no SimpleFile.Core/Ipc/PluginHost/App in either csproj; every PackageReference is
                                                                            // Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools or CommunityToolkit.Mvvm, with ExcludeAssets="runtime" and PrivateAssets="all"
```

```rust
// plugins/remote/backend/tests/manifest_defaults.rs
#[test] fn manifest_defaults_match_the_engine_defaults()
// reads plugins/remote/plugin.json from CARGO_MANIFEST_DIR: its settings keys equal TRANSFER_SETTING_KEYS in order, each in group "Transfers";
// each default, rendered as a setting string (number as decimal, bool as "true"/"false", string as is), equals setting_values(&Policy::default())
```

`RemoteUi_AsksForPlainFtpConsentAndTrustsHostKeysExplicitly` searches the plugin's `Views/*.cs` sources. The ported dialog code keeps today's local names `protocolBox`, `insecureBox` and `rootBox`. It asserts:
- `"Connect without encryption. Passwords and files are sent in plain text."`, `"Confirm the unencrypted connection to save this profile"`, `IsPrimaryButtonEnabled = !needsConsent` and `DefaultPortFor(` are present; `Allow plain FTP` is absent.
- In `RemoteProfileDialog.cs`, in the slice from `"Connection",` to `rootBox),`, `protocolBox,` comes before `insecureBox,`. The consent box sits directly under Protocol.
- `Title = "Trust this server?"`, `PrimaryButtonText = "Trust and connect"`, `Title = "Server key changed"`, `PrimaryButtonText = "Replace trusted key"` and `TrustFingerprintAndConnectAsync(fingerprint, token)` are present.
- The slice from `Title = "Server key changed"` to the next `ShowDialogAsync(` contains `DefaultButton = ContentDialogButton.Close`. Today's anchor, `if (await DialogHost.ShowAsync(dialog)`, is host-only.
- `"Insecure"`, `"Transfer options..."` and `"Replace (keeps a backup until the transfer finishes)"` are string literals. `.IsInsecure` drives the badge in `RemoteManagerWindow.Lists.cs` and `RemoteSidebarSection.cs`; it replaces the XAML forms `Text="Insecure"` and `InsecureBadgeVisibility(Protocol, InsecurePlainFtp)`. No `"Reload"` literal is present.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RemoteManifest_InTheRepo_IsValid|FullyQualifiedName~RemoteTransferSettings_DefaultsMatchTheManifest|FullyQualifiedName~RemotePluginSourceShapeTests"`, then `cargo test --locked -p sumafile-remote-backend --test manifest_defaults`
Expected: FAIL (the files are missing).

- [ ] **Step 3: Create the project, restore offline and implement the UI**

Run: `dotnet restore plugins/remote/ui/SumaFile.Remote/SumaFile.Remote.csproj --force-evaluate`, then `dotnet build plugins/remote/ui/SumaFile.Remote/SumaFile.Remote.csproj -c Debug --nologo`
Expected: restore from the cache; `Build succeeded. 0 Warning(s) 0 Error(s)`. The output folder `bin\Debug\net10.0-windows10.0.19041.0\` holds `SumaFile.Remote.dll`, `SumaFile.Remote.Model.dll` and `SumaFile.Remote.deps.json`, and neither `Microsoft.WinUI.dll` nor `CommunityToolkit.Mvvm.dll`.

- [ ] **Step 4: Run the tests and the solution build**

Run: the Step 2 commands, then `npm run check:winui`, then `npm run build:winui`, then `cargo clippy --locked --all-targets --all-features -- -D warnings`
Expected: `Passed!` and `test result: ok`; the solution builds with 0 warnings; clippy is clean.

- [ ] **Step 5: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 12: Stage, package and ship the remote plugin: dev staging, a first real-host load smoke, the release payload, installers, CI and the packaged smokes

**Files:**
- Modify: `scripts/lib/plugin-staging.ps1`:
  - `$script:BundledPluginIds` becomes `@('com.sumafile.sevenzip','com.sumafile.remote')`;
  - add `Get-RemotePluginSources`;
  - the freshness files for remote are the backend exe, both DLLs and `plugin.json`.
- Modify: `scripts/build-plugins.ps1`:
  - `cargo build --locked -p sumafile-sevenzip-backend -p sumafile-remote-backend`, plus `--release` for Release;
  - then, unless `-SkipDotnet`, `dotnet build plugins/remote/ui/SumaFile.Remote/SumaFile.Remote.csproj -c <Configuration> --nologo`.
- Modify: `scripts/build-fixture-plugin.ps1`: also `cargo build --locked -p sumafile-remote-test-server`.
- Modify: `scripts/smoke-winui-ui.ps1:32`: `cargo build --locked -p simplefile-service -p sumafile-sevenzip-backend -p sumafile-remote-backend -p sumafile-remote-test-server`.
- Modify: `scripts/build-winui-release.ps1`:
  - cargo `sumafile-remote-backend` in release beside the 7-Zip build;
  - `dotnet build …SumaFile.Remote.csproj -c Release` before `New-WinUIPayload`;
  - `Get-LikelyWinUILockHolder` adds `sumafile-remote-backend.exe`.
- Modify: `packaging/winui/sumafile-winui.nsi`:
  - `.onInit` and Uninstall run `taskkill /F /IM sumafile-remote-backend.exe`;
  - before `File /r`, `RMDir /r "$INSTDIR\plugins\com.sumafile.remote"`.
- Modify: `scripts/check-winui-packaging.mjs`:
  - `requiredFiles`: `plugins/remote/plugin.json`, `plugins/remote/protocol.json`, `plugins/remote/backend/Cargo.toml`, `plugins/remote/test-server/Cargo.toml` and both remote csproj files;
  - `nsisSnippets`: the taskkill line and `$INSTDIR\\plugins\\com.sumafile.remote`;
  - `buildSnippets`: `sumafile-remote-backend` and `SumaFile.Remote.csproj`;
  - `stagingSnippets`: `com.sumafile.remote`, `sumafile-remote-backend.exe`, `SumaFile.Remote.dll`, `SumaFile.Remote.Model.dll` and `SumaFile.Remote.deps.json`.
- Modify: `scripts/smoke-winui-startup.ps1`:
  - set `SUMAFILE_PLUGIN_DATA_ROOT`, `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` to paths in its temp folder, each restored in `finally`. Today the smoke runs the payload against the user's real `metadata.db`. The remote section's backend would read the real `remote_profiles` and, on every run, seed `plugin.com.sumafile.remote.*` keys into the real settings table. `startup-timing.log` and `plugins.log` stay in `%LOCALAPPDATA%\SumaFile`, where this smoke reads them;
  - after `Connect.ready`, wait up to 20 s for a new `plugins.log` line containing `[com.sumafile.remote] [info] Discovered: Enabled` (Task 3), which does not depend on the user's sidebar state;
  - require that the approvals file holds no `com.sumafile.remote` record;
  - print `Bundled remote plugin trusted.`;
  - stop `sumafile-remote-backend` processes under the payload in `finally`, since the sidebar section may have started one.
- Modify: `scripts/smoke-winui-file-ops.ps1`: a new `Invoke-RemotePluginSmoke` after the 7-Zip section.
  - Launch `payload\plugins\com.sumafile.remote\sumafile-remote-backend.exe --pipe-name sumafile-remote-smoke-<guid> --data-dir <smoke>\remote-data`, with `SIMPLEFILE_METADATA_DB=<smoke>\none.db`.
  - Handshake with `client = "simplefile-service"`, so `host.*` fails fast.
  - `profiles.list` returns `[]`.
  - `profiles.save` an SFTP profile without a secret returns `credential_target` `SumaFile.Remote.<id>`.
  - `profiles.list` returns 1 item; `profiles.delete`; `profiles.list` returns `[]`.
  - Print `Payload remote plugin smoke passed.`, and kill the backend in `finally`.
- Modify: `scripts/smoke-winui-msi.ps1:51`, `smoke-winui-nsis.ps1:123` and `smoke-winui-upgrade.ps1:284`. Their messages say `bundled plugins` instead of 7-Zip; the assert calls are unchanged.
- Modify: `scripts/smoke-winui-msi.ps1:53`, which `release:build` runs. It launches the extracted app with no overrides, so in phase 3 the remote backend would start against the user's real data.
  - Before `Start-Process`, it sets these to paths under `$smokeRoot`: `SUMAFILE_PLUGIN_ROOTS` (the extracted `plugins` folder), `SUMAFILE_PLUGIN_APPROVALS_PATH`, `SUMAFILE_PLUGIN_DATA_ROOT`, `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR`. It restores them in `finally`.
  - In `finally` it also stops `sumafile-*-backend` processes under the extracted folder, before removing that folder.
- Create: `src-winui/SimpleFile.App/MainWindow.UiSmoke.Remote.cs` with `SmokeRemoteAsync(results, mode)`. `SmokePluginsAsync` calls it after the 7-Zip scenarios, for now in `with` mode only. This is the first time the real host loads `SumaFile.Remote.dll`, before Tasks 13 and 14. Task 15 extends it to the full scenario.
- Modify: `scripts/test-plugin-staging.ps1`: add a case that `Get-BundledPluginSources -Id com.sumafile.remote -Configuration Debug` lists the five remote files.
- Modify: spec §11.4 (the payload layout with both plugins) and `README.md:128` (the install list). In the review notes' Phase 3 closures, the ledger Task 16 "smoke writes plugin data" line becomes closed for plugin data (Tasks 8 and 12).

**Interfaces:**
- Consumes: Task 2 staging functions; Task 3 `[info] Discovered: <State>`; Task 6 exe; Task 11 DLLs and manifest; Task 8 `SUMAFILE_PLUGIN_DATA_ROOT`; Task 9's proven patterns (`HelloWindow`, dialogs on a plugin `XamlRoot`).
- `SmokeRemoteAsync`, `with` mode, as of this task. Every wait is bounded at 10 s and fails with what it waited for:
  1. `com.sumafile.remote` is discovered from `<exe dir>\plugins` and is Enabled through `bundled.json`. The disposable approvals file holds no record for it.
  2. The section `PluginSection.com.sumafile.remote.connections`, headed `Connections`, shows `Remote.SidebarEmpty`. `<SIMPLEFILE_UI_SMOKE_ROOT>\plugins-data\com.sumafile.remote\remote-profiles.db` exists, so the backend used the disposable data root.
  3. Running `plugin:com.sumafile.remote:ftp-sftp-manager` opens exactly one window in remote's `PluginWindowHost.OpenWindows`, titled `FTP/SFTP manager`. Running it again keeps one window.
  4. `Remote.NewProfile` opens the profile `ContentDialog` on the manager window's own `XamlRoot`. Its Close button closes it, and no profile is saved.
  5. Closing the manager window empties `OpenWindows`.
  - It records `results.Add(new { scenario = "remote", mode = "with", stage = "load", passed = true })`.
  - It creates the smoke helpers Task 15 reuses: `SmokeInvoke(Button)` through `ButtonAutomationPeer`, `SmokeFindById<T>(DependencyObject root, string automationId)`, and `SmokeOpenDialog(XamlRoot root)` through `VisualTreeHelper.GetOpenPopupsForXamlRoot`.
  - The core FTP/SFTP feature still exists until Task 14, so these checks use only the plugin's automation ids.
- `Get-RemotePluginSources $Configuration` is an ordered map:
  - `plugin.json` → `plugins\remote\plugin.json`;
  - `sumafile-remote-backend.exe` → `target\<debug|release>\sumafile-remote-backend.exe`;
  - `SumaFile.Remote.dll`, `SumaFile.Remote.Model.dll` and `SumaFile.Remote.deps.json` → `plugins\remote\ui\SumaFile.Remote\bin\<Configuration>\net10.0-windows10.0.19041.0\`.

  A missing remote source throws with the build command to run.
- `build-plugins.ps1` prints `Bundled plugins verified: com.sumafile.sevenzip, com.sumafile.remote`.

- [ ] **Step 1: Make the script, installer and check changes**

- [ ] **Step 2: Stage and verify for dev**

Run, in order:
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1`
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1`
- `npm run build:winui`
- the freshness asserts on the App output:

```powershell
powershell -NoProfile -Command ". scripts/lib/plugin-staging.ps1; Assert-BundledPlugins -Root 'src-winui/SimpleFile.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/plugins'; Assert-BundledPluginsFresh -Root 'src-winui/SimpleFile.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/plugins' -Configuration Debug"
```

Expected: `Plugin staging tests passed.`, `Bundled plugins verified: com.sumafile.sevenzip, com.sumafile.remote`, a clean build, and both asserts silent with exit 0. `bundled.json` lists both ids.

- [ ] **Step 3: Run the remote load smoke**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`, then `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -PluginsDir target\fixture-plugins\com.sumafile.hello`
Expected: `UI runtime smoke passed.`, with `remote=1` among the completed scenarios.
- Fix a failure here in this task, before Tasks 13 and 14. Typical causes are a WinRT subclass, load-context or dialog problem in `SumaFile.Remote.dll`.

- [ ] **Step 4: Static checks**

Run: `npm run check && npm run check:winui-release-hash`
Expected: all success lines, including `WinUI packaging surface is wired.`

- [ ] **Step 5: Release build with the packaged smokes.** This takes up to 10 minutes. Do not run `smoke:winui-installer` or the upgrade smokes.

Run: `npm run release:build`
Expected, in this order:
- `Bundled 7-Zip plugin trusted and registered.` and `Bundled remote plugin trusted.`;
- `Payload 7-Zip plugin smoke passed.` and `Payload remote plugin smoke passed.`;
- the MSI smoke passes on the extracted image;
- the artifacts table prints.

`dist\winui\payload\plugins\com.sumafile.remote` holds exactly `plugin.json`, `sumafile-remote-backend.exe`, `SumaFile.Remote.dll`, `SumaFile.Remote.Model.dll` and `SumaFile.Remote.deps.json`. Before the run, note whether `%LOCALAPPDATA%\SumaFile\plugins-data\com.sumafile.remote` exists and the newest `LastWriteTime` inside it. After the run, both are unchanged.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 13: The real host drives the real remote backend: C# integration tests against the SFTP test server

**Files:**
- Modify: `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj`: a `CopyRemotePluginFixture` target after Build. It copies three things when they exist:
  - `..\..\target\plugins\com.sumafile.remote\**` to `$(OutDir)Fixtures\remote\plugins\com.sumafile.remote\`;
  - `..\..\target\plugins\bundled.json` to `$(OutDir)Fixtures\remote\plugins\` (it lists the remote plugin's hash; its 7-Zip entry is simply absent from that root);
  - `..\..\target\debug\sumafile-remote-test-server.exe` to `$(OutDir)Fixtures\remote\`.
- Create: `src-winui/SimpleFile.Tests/Remote/RemoteBackendHarness.cs` and `Remote/RemotePluginBackendTests.cs`. The tests sit in a new collection `Remote plugin backend` with `DisableParallelization = true`.
- Modify: `scripts/build-winui-release.ps1:345-346`. Before `npm run check:winui`, it runs `scripts/build-fixture-plugin.ps1` and `scripts/build-plugins.ps1` (Debug), so the release pipeline always has these tests' fixtures. The CI `winui` job already runs both before `check:winui`.
- Modify: `scripts/check-winui-packaging.mjs`: `buildSnippets` gains `build-fixture-plugin.ps1`.

**Interfaces:**
- Consumes:
  - Task 12's staged folder;
  - Task 5's test server bin;
  - Task 10's `RemoteBackendClient`;
  - the real `PluginManager` with `ProcessLauncher`, Task 9's cancel-aware sink semantics through a recording `IPluginProgressSink`, and `FakeSettings` from `PluginHostMethodsTests.cs`.
- `RemoteBackendHarness : IAsyncDisposable`:
  - It fails with `The remote plugin is not staged. Run scripts/build-fixture-plugin.ps1 and scripts/build-plugins.ps1.` when either fixture is missing.
  - It starts the test server process with `--root <temp>\server --info <temp>\info.json`, and sets `SIMPLEFILE_METADATA_DB=<temp>\none.db`, restoring it on dispose.
  - An option `legacyDatabase` (default false) adds `--legacy-db <temp>\legacy\metadata.db` to the server's arguments and points `SIMPLEFILE_METADATA_DB` there. The backend that the real `PluginManager` launches inherits that environment, so this is the upgrade chain an installed host takes.
  - It builds a `PluginManager`:
    - `Roots` and `BundledRoot` are `Fixtures\remote\plugins`, whose `bundled.json` the copy carries, so the plugin is Enabled without consent;
    - `ApprovalStorePath = <temp>\plugin-approvals.json` and `DataRoot = <temp>\data`;
    - `InstanceFactory = _ => new CapturingPlugin()`, which captures the `IPluginHost` and handles the command as a no-op, so the WinUI assembly is never loaded here.
  - It runs `plugin:com.sumafile.remote:ftp-sftp-manager` once.
  - It exposes `Client` (a `RemoteBackendClient`), `Server` (the parsed `info.json` fields), `Rows` (the recording sink: titles, reports, completes, fails and each row's cancel callback), `Settings` and `Manager`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task SavesListsConnectsAndListsTheTestServer()
// save an SFTP profile (127.0.0.1, info.port, info.user, trusted fingerprint = info.fingerprint, no secret) -> list has it, no secret field;
// ConnectAsync(id, info.password) -> session; ListDirectoryAsync("/") shows the server fixture names
[Fact] public async Task UploadAndDownloadAppearAsProgressRowsAndComplete()
// upload "café 資料.bin" (2 MiB) -> one row "Uploading 1 item to 127.0.0.1:/", >= 1 report, completed; server bytes equal; download it back, bytes equal
[Fact] public async Task CancelFromTheProgressRow_StopsTheDownloadAndKeepsTheExistingFile()
// Settings plugin.com.sumafile.remote.bandwidthMib = "1"; existing local file with other bytes; conflict "replace"; download 64 MiB;
// on the first report call the row's cancel -> outcome.Cancelled; row failed "Transfer cancelled"; local bytes unchanged; no "*.sumafile-partial*"
[Fact] public async Task TransferHonoursThePluginBandwidthSetting() // bandwidthMib "1": a 4 MiB upload takes >= 3 s; "0": fast
[Fact] public async Task ProfilesChangedReachesTheClientAfterASave()
[Fact] public async Task Disable_WhileATransferRuns_StopsTheBackendAndEndsTheRow()
// slow download running; Manager.DisableAsync -> backend process exits within 5 s; row failed "Plugin backend stopped."; TransferAsync faults
[Fact] public async Task UpgradedProfile_IsImportedAndConnectsThroughTheRealHost()
// legacyDatabase: an OnProfilesChanged handler registered before the first call fires within 5 s; ListProfilesAsync returns exactly the
// legacy row (id info.legacyProfileId, name "Legacy SFTP", credential_target "SumaFile.Remote.legacy-sftp", trusted_host_fingerprint
// info.fingerprint); ConnectAsync(id, info.password) returns a session with no host-key error, because the trusted fingerprint came along
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RemotePluginBackendTests"`
Expected: compile FAIL (no harness).

- [ ] **Step 3: Implement the copy target and the harness**

- [ ] **Step 4: Stage the fixtures and run**

Run, in order:
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1`
- the Step 2 command
- `npm run check:winui`

Expected: the seven tests pass; the full suite passes. No `SumaFile.Remote.*` credential is created: no test saves a secret, and the legacy test connects with the password in params.

Then run: `npm run check`
Expected: `WinUI packaging surface is wired.` (the release script's new staging steps are checked).

- [ ] **Step 5: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 14: Remove remote from core, the service, the IPC schema and the App

**Files:**
- Rust, delete:
  - `crates/simplefile-core/src/remote/` and `pub mod remote;` (`lib.rs:26`);
  - `crates/simplefile-core/tests/remote_profiles.rs`;
  - `crates/simplefile-service/src/remote/` and `pub mod remote;` (`lib.rs:7`);
  - `crates/simplefile-service/tests/{remote_secrets.rs, remote_session.rs}`.
- Rust, edit:
  - `dispatch/mod.rs`: imports 5-11, the state fields 36-37 and 54-55, the test constructors 76-112, and `Dispatch::RemoteTransfer` 184-188;
  - `dispatch/handlers.rs`: imports 7-10, 17 and 20, the arms 137-376, and `secret_for_profile` 1125-1131;
  - `dispatch/params.rs`: line 5 and lines 230-306;
  - `session/mod.rs:87-105`, `session/jobs.rs:76-123`;
  - `dispatch/tests.rs`: lines 3-4, 7 and 801-1598;
  - `session/tests.rs`: lines 61-209 and 597-669.
- Rust, dependencies:
  - the service `Cargo.toml` drops `russh`, `russh-sftp`, `suppaftp` and the `windows-sys` feature `Win32_Security_Credentials`;
  - `Cargo.lock` is updated;
  - `tools/sevenz-evaluation/Cargo.lock` too, if core's dependencies changed.
- Schema:
  - `ipc/schema/v1/commands.json`: delete the 13 `remote_*` entries; `domainMethodCount` 86;
  - `ipc/schema/v1/types.json`: delete the enums `RemoteProtocol` and `RemoteAuthKind` and the six `Remote*` types. Leave `ArchiveFormat` last, with no trailing comma.
- Scripts:
  - `scripts/generate-ipc-bindings.mjs:188-200`: delete the 13 rows; then regenerate;
  - `scripts/check-ipc-schema.mjs:111-116`: 86 in both literals;
  - `scripts/check-winui-parity-gate.mjs:96-97`: 86.
- Docs:
  - `docs/winui-migration/parity-gate.md`:
    - the counts at lines 5, 33, 64 and 440 become 86;
    - §2.6 becomes "FTP/SFTP (plugin `com.sumafile.remote`)". It points at `plugins/remote/protocol.json` and keeps one row per coverage level, in the section's table format (`ID | Feature | WinUI verification | Automated | Manual | Status`):
      - `remote.sftp-password`: profiles, connect, browse, create, rename, delete, upload, download and cancel over SFTP with password auth. Automated by the Task 6 handler and process tests, Task 13 `RemotePluginBackendTests` and the Task 15 `remote` smoke. `PASS`.
      - `remote.legacy-import`: 1.0.4 profiles and saved passwords after the upgrade. Automated by Task 5 `legacy_import.rs`, Task 6 `backend_process_imports_a_1_0_4_profile_and_connects_with_its_saved_password`, Task 13 `UpgradedProfile_IsImportedAndConnectsThroughTheRealHost` and Task 15 smoke step 2. `PASS`.
      - `remote.ftp`: plain FTP after the consent, and anonymous login. Automated: only the ported `ftp.rs` tests (retry classification, list parsing, streaming against the scripted server). Manual: connect, browse, create, rename, delete, upload and download against a real FTP server. `MANUAL`.
      - `remote.ftps`: FTPS (explicit TLS). Automated: only the shared `ftp.rs` tests. Manual: the same steps against a real FTPS server whose certificate the system trusts. `MANUAL`.
      - `remote.sftp-key`: SFTP private-key auth, with and without a saved passphrase. Automated: none; the test server accepts passwords only. Manual: connect with a key file; with a saved passphrase, reconnect without retyping it. `MANUAL`.
      - `remote.sftp-agent`: SFTP agent auth. Automated: none. Manual: connect through a running OpenSSH agent or Pageant. `MANUAL`.
    - §10 gains step 17, which names the four `MANUAL` remote checks above;
    - row 351 becomes `plugin:com.sumafile.remote:ftp-sftp-manager`;
    - row 363 drops FTP/SFTP;
  - `docs/winui-migration/inventory.md`: count references.
- C#, core and IPC:
  - delete `SimpleFile.Ipc/ISimpleFileIpc.cs:49-61` and `Models.cs:659-723`;
  - regenerate `Protocol.Generated.cs` and `NamedPipeJsonClient.Generated.cs`;
  - delete `SimpleFile.Core/FileOperationService.cs:742-832` and `SimpleFile.Core/RemoteManagerViewModel.cs`;
  - `AppCommandCatalog.cs:55`, `CommandIds.cs:37`, `CommandSurfaceLayout.cs:379`, `ToolbarOverflowPlanner.cs:28`, `ContextMenuBuilder.cs:175`.
  - `CommandSurfaceLayout.cs:176`: the legacy-default check also matches that literal without `ftp-sftp-manager,`, because `Normalize` now strips that id first.
- C#, App:
  - delete `MainWindow.Remote.cs` and `RemoteManagerWindow.xaml(.cs)`;
  - `MainWindow.xaml.cs`: lines 51, 91, 194, 247-248 (the `remote-profiles` startup mark goes) and 1355;
  - `MainWindow.Controls.cs`: lines 20-22 and 159-160;
  - `MainWindow.Chrome.cs`: lines 329 and 347-348;
  - `MainWindow.FileListEvents.cs:190-198`, `MainWindow.CommandRouting.cs:67`;
  - `SidebarView.xaml:208-304` and `SidebarView.xaml.cs` lines 26-28, 55-56, 87 and 89;
  - `ExplorerRows.cs:217-254`;
  - `SettingsWindow.xaml:551-560` and `SettingsWindow.Tools.cs` lines 16 and 229-253;
  - `TransferOptionsDialog.cs`: the `remote` parameter and lines 156-173.
- C# tests:
  - delete `RemoteManagerViewModelTests.cs` (the root one), `NullIpc.cs:63-118` and `NamedPipeJsonClientTests.cs:661-868`;
  - update `WinUiSourceShapeTests.cs:85-146`, `UxShellTests.cs:125-145,190-205`, `UiAuditSprint3Tests.cs:44-45`, `UiAuditRegressionTests.cs:1029-1031`, `UxRecommendationsSettingsGitRemoteTests.cs:188-244`, `AppCommandCatalogTests.cs:52-77`, `ContextMenuBuilderTests.cs:166-168`, `ToolbarOverflowPlannerTests.cs:26,42` and `CommandSurfaceLayoutTests.cs`.
- Docs:
  - `README.md` lines 15, 78, 92, 109, 150-151, 279-340, 353, 507-539 and 676 describe the bundled plugin, its Settings > Plugins page, the Connections section below the built-in sections, and where profiles live;
  - `docs/SECURITY.md:5,38-45`;
  - `docs/TRANSFER_TUNING.md:41-44,75,87-93`: remote transfers use the plugin's settings;
  - spec §11.2: the removal is done, and the UX changes (Self-review notes) are recorded.

**Interfaces:**
- Consumes: Tasks 6-13; the plugin replaces every removed surface.
- Produces:
  - `npm run check` prints `Checked IPC v1 schema: 86 domain methods, 7 emitted events, 12 goldens.` and `WinUI parity gate lists 86 commands, 57 context ids, 83 palette ids.`
  - No App, Core or Ipc source contains `RemoteManagerWindow`, `remote_list_profiles` or `ShowRemoteManagerWindow`.

- [ ] **Step 1: Write the failing tests**

```csharp
// WinUiSourceShapeTests (replaces FtpSftpManager_HasCommandRouteAndWindowShell)
[Fact] public void RemoteUi_LivesOnlyInTheRemotePlugin()
// no RemoteManagerWindow.xaml(.cs) or MainWindow.Remote.cs in SimpleFile.App; CommandRouting has no "ftp-sftp-manager";
// SidebarView.xaml has no "Connections"; SettingsWindow.xaml has no SettingsRemoteManagerButton;
// TransferOptionsDialog.ShowAsync has no remote parameter; no "remote_" method in Protocol.Generated.cs
// CommandSurfaceLayoutTests
[Fact] public void LegacyDefaultToolbarWithTheFtpButton_StillUpgradesToTheCurrentDefault()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RemoteUi_LivesOnlyInTheRemotePlugin|FullyQualifiedName~LegacyDefaultToolbarWithTheFtpButton"`
Expected: FAIL (the remote files still exist).

- [ ] **Step 3: Delete and edit as listed, in one step.** Then regenerate the bindings.

Run: `npm run generate:ipc-bindings`
Expected: the three generated files are rewritten, with `DomainMethodCount = 86` and `DOMAIN_METHOD_COUNT: usize = 86`.

- [ ] **Step 4: Run everything**

Run:
- `npm run check`
- `cargo fmt --all -- --check`
- `cargo test --locked --all-features`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`
- `npm run check:winui`
- `npm run build:winui`

Expected: the two count lines from Interfaces, `test result: ok` for every Rust target, clippy clean, `Passed!`, and a 0-warning build.

- [ ] **Step 5: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

### Task 15: End-to-end UI smoke for the remote plugin, with and without plugins, and the release-build run

**Files:**
- Modify: `scripts/smoke-winui-ui.ps1`. In `-PluginsDir` mode:
  - Create `<testRoot>\remote-server` holding `café 資料\hello.txt` (`hello`), `literal [brackets] & spaces.txt` (`literal`) and `big.bin` (64 MiB random).
  - Create `<testRoot>\remote-local` holding `upload.txt` (`upload`) and `download\big.bin` (1 MiB of other bytes).
  - Before the App starts, start `target\debug\sumafile-remote-test-server.exe --root <testRoot>\remote-server --info <testRoot>\remote-server.json --legacy-db <testRoot>\state\metadata.db`, and wait for the info file.
    - The App's service then opens that 1.0.4 database as its settings database (`SIMPLEFILE_METADATA_DB`), and the remote backend imports its `Legacy SFTP` profile.
    - This is the UI-level proof of Review Focus 1.
  - Export `SUMAFILE_SMOKE_REMOTE=<that info path>` and `SUMAFILE_SMOKE_REMOTE_LOCAL=<testRoot>\remote-local`.
  - Kill the server in `finally`.

  `SUMAFILE_PLUGIN_DATA_ROOT` is already exported in every mode (Task 8).
- Modify: `src-winui/SimpleFile.App/MainWindow.UiSmoke.Remote.cs` (created in Task 12). `SmokeRemoteAsync(results, mode)` grows from its load checks to the full scenario below and gains the `none` mode. `SmokeRemoteBackendLossAsync(results)` is added. `SmokePluginsAsync` calls both after the 7-Zip scenarios.
- Modify: `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`. The `service-reconnect` scenario also asserts that the remote backend's process id is unchanged and the plugin is still Enabled after the service restart.
- Modify: spec §13 (the remote smoke and the no-plugin assertions).

**Interfaces:**
- Consumes the whole chain:
  - staging (T12) and trust (T2);
  - the slots (T7-T9);
  - the remote UI (T11) and the model (T10);
  - the backend (T5, T6) and the removal (T14).
- Smoke-only helpers in that file:
  - from Task 12: `SmokeInvoke(Button)`, `SmokeFindById<T>(DependencyObject root, string automationId)` and `SmokeOpenDialog(XamlRoot root)`;
  - `SmokeRemoteCredentialTargets()`: a `CredEnumerateW("SumaFile.Remote.*")` P/Invoke, returning the target names;
  - `SmokeDeleteRemoteCredential(string target)`: `CredDeleteW(target, CRED_TYPE_GENERIC, 0)`, ignoring `ERROR_NOT_FOUND`;
  - `SmokeRow(ListViewBase list, string name)`: the realized container whose `AutomationProperties.Name` is `name` (the remote lists are virtualized and hold model objects, so rows are read from their containers, `ContainerFromIndex`, never from `Items`);
  - `SmokeActivateRow(ListViewBase list, string name)`: raises the list's real `ItemClick` for that row. `ListViewItem` automation peers expose no Invoke pattern, so it focuses the container with `FocusState.Keyboard` and delivers Enter (`WM_KEYDOWN`/`WM_KEYUP` `VK_RETURN` posted to the window's focused input HWND), which `ListViewBase` turns into `ItemClick`. Added by the Task 11 fix round 1: nothing else clicks a row.

Assertions in `SmokeRemoteAsync`, `with` mode. Every wait is bounded (10 s unless stated) and fails with what it waited for:

1. `com.sumafile.remote` is discovered from `<exe dir>\plugins` and is Enabled, and the disposable approvals file holds no record for it. The palette has `plugin:com.sumafile.remote:ftp-sftp-manager` and no bare `ftp-sftp-manager`.
2. The sidebar holds exactly one plugin section for remote, `PluginSection.com.sumafile.remote.connections`, with the header `Connections`. Its `Remote.SidebarList` lists exactly `Legacy SFTP`, the profile imported from the smoke's 1.0.4 database.
3. Running the command opens exactly one window in remote's `PluginWindowHost.OpenWindows`, titled `FTP/SFTP manager`, whose `Remote.ProfileList` holds `Legacy SFTP`. Running it again keeps one window.
   - Sidebar row click: close the window, then `SmokeActivateRow(<Remote.SidebarList>, "Legacy SFTP")`. Exactly one manager window opens, and its `Remote.ProfileList` has the `Legacy SFTP` row selected. Activating the row again keeps one window.
4. Record `baseline = SmokeRemoteCredentialTargets()`. Steps 4 to 12 run inside `try`, with the credential `finally` below.
   - `Remote.NewProfile` opens the profile dialog. Fill it: name `Smoke SFTP`, protocol SFTP, `127.0.0.1`, the port, the user, the password, `RemoteProfile.SaveSecret` on. Primary saves.
   - The manager's `Remote.ProfileList` and, within 5 s, the sidebar's `Remote.SidebarList` show `Smoke SFTP` beside `Legacy SFTP`.
   - Record `smokeTarget = "SumaFile.Remote." + <the Tag of SmokeRow(Remote.ProfileList, "Smoke SFTP")>`. `SmokeRemoteCredentialTargets()` has gained exactly that one target.
5. `Remote.Connect` opens the host-key dialog. `RemoteHostKey.Fingerprint` text equals the info fingerprint. Primary trusts. `Remote.RemoteList` then shows `café 資料`, `literal [brackets] & spaces.txt` and `big.bin`.
   - Remote folder row click: `SmokeActivateRow(<Remote.RemoteList>, "café 資料")` makes `Remote.RemotePath` read `/café 資料` and the list show exactly `hello.txt`. The button named `Go to parent remote folder` returns to `/`.
6. Upload:
   - set `Remote.LocalPath` to `remote-local` and invoke `Remote.LocalGo`;
   - select `upload.txt` in `Remote.LocalList` and invoke `Remote.Upload`;
   - a Transfers row titled `Uploading 1 item to 127.0.0.1:/` reaches Completed within 30 s. Its `CanChangeOptions` is false, so the Transfers window offers no per-task Options for it;
   - `<server>\upload.txt` reads `upload`.
7. Download: select `literal [brackets] & spaces.txt` and invoke `Remote.Download`. Its row reaches Completed, and `remote-local\literal [brackets] & spaces.txt` reads `literal`.
8. Cancel:
   - set `plugin.com.sumafile.remote.bandwidthMib` to `1` through `FileOps.SetSettingAsync`;
   - go to `remote-local\download` by `SmokeActivateRow(<Remote.LocalList>, "download")` (the local folder row click); `Remote.LocalPath` then reads that folder; set `Remote.Conflict` to replace; select `big.bin` and download;
   - while the row runs, `row.CanCancel` is true and `row.CanChangeOptions` is false;
   - once the row's progress is above 0, call `_transfer.Cancel(row)`;
   - the row is Cancelling until the backend fails it, then Cancelled within 30 s;
   - `download\big.bin` is byte-for-byte the 1 MiB file, and no `*.sumafile-partial*` is left in `download`;
   - set `bandwidthMib` back to `0`.
9. Folders:
   - `Remote.NewFolder` with `smoke-folder`, through `Remote.NamePrompt`, makes the folder appear;
   - `Remote.Rename` to `renamed folder [ok]` makes it appear under the new name, and `<server>\renamed folder [ok]` exists;
   - `Remote.DeleteEntries`, confirmed, removes it, and the server folder is gone.
10. Disable during a transfer:
    - set `bandwidthMib` to `1` and start the `big.bin` download again;
    - call `manager.DisableAsync("com.sumafile.remote")`;
    - within 10 s: the manager window is closed, the row is Failed with `Plugin backend stopped.`, no `sumafile-remote-backend` process runs under `<exe dir>\plugins`, the remote section and the command are gone, and no consent dialog opened;
    - call `EnableAsync`; the section is back within 10 s. Set `bandwidthMib` to `0`.
11. Run the command again, select `Smoke SFTP`, then `Remote.DeleteProfile` and confirm. The manager list and the sidebar show only `Legacy SFTP`, and `SmokeRemoteCredentialTargets()` equals `baseline`.
12. Settings:
    - open Settings > Plugins: the remote card shows the declarative `Bandwidth limit (MB/s, 0 = unlimited)` control and `PluginSettingsPage.com.sumafile.remote` holding `Remote.SettingsOpenManager`;
    - invoking that button leaves exactly one manager window;
    - close it.
- The credential `finally` for steps 4 to 12, which runs whether or not a step failed:
  - If step 4 did not record `smokeTarget`, it first tries once to read the `Smoke SFTP` row's `Tag` (`SmokeRow`) from `Remote.ProfileList` or `Remote.SidebarList`.
  - It deletes `smokeTarget` with `SmokeDeleteRemoteCredential`.
  - Any other target in `SmokeRemoteCredentialTargets()` that is not in `baseline` fails the scenario, named in the failure message, and is not deleted. The smoke never deletes a credential it cannot attribute to its own profile, such as a password a real SumaFile saved meanwhile.
- `results.Add(new { scenario = "remote", mode, passed = true, … })`.

`SmokeRemoteBackendLossAsync` (`with` mode):
- Kill the remote backend, the `sumafile-remote-backend` process under `<exe dir>\plugins`.
- Within 15 s the plugin is Faulted and the remote section is gone.
- `RestartBackendAsync("com.sumafile.remote")` makes it Enabled, and the section returns within 15 s.
- Scenario name `remote-backend-loss`.

`none` mode:
- `PluginSections.Children.Count == 0`.
- No `AppCommandCatalog.All` id contains `ftp-sftp-manager`, and `ToolbarActionCatalog` has none either.
- The Pane More > Tools menu from `ContextMenuBuilder` has no `overflow-ftp-sftp-manager`.
- An opened Settings window has no element named `SettingsRemoteManagerButton` and no text `FTP/SFTP connections`.
- Scenario `remote`, mode `none`.

- [ ] **Step 1: Implement the script changes and the smoke file**

- [ ] **Step 2: Run the plugin-mode smoke**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`, then `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`
Expected: `UI runtime smoke passed.` `Completed scenarios` includes `plugins=1`, `sevenzip=1`, `sevenzip-backend-loss=1`, `remote=1`, `remote-backend-loss=1` and `service-reconnect=1`.

- [ ] **Step 3: Startup budget with three plugins**

Run: `npm run check:winui-startup-budget`, right after Step 2 and before any other app launch. The script reads the latest session, so this reads the one the Step 2 smoke logged.
Expected: `WinUI startup budget passed: …`, with the `plugins-discovered delta` under 50 ms. Compare it with the Task 3 baseline in the task report.
- If it is over 50 ms, apply Task 3's budget fallbacks: F1 when hashing dominates, F2 when cold start does. Then rerun Step 2 and this step, and record the numbers before and after.
- Do not raise the budget.

- [ ] **Step 4: Run the no-plugin smoke**

Run: `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`
Expected: `UI runtime smoke passed.` with `plugins=1`, `sevenzip=1` and `remote=1` (all `none`).

- [ ] **Step 5: Full suite and the phase's release-build smoke run**

Run:
- `npm run check`
- `npm run check:rust`
- `npm run check:winui`
- `npm run test:maintenance`
- `npm run release:build`
- `npm run smoke:winui`
- `npm run check:winui-startup-budget`

Expected:
- every check green;
- the release build prints the Task 12 smoke lines;
- `WinUI startup smoke passed: …`, `Bundled 7-Zip plugin trusted and registered.` and `Bundled remote plugin trusted.`;
- the budget passes on the payload run.

- [ ] **Step 6: Snapshot.** Run: `bash .superpowers/tools/snap.sh`

---

## Self-review notes

- **Spec coverage:**

  | Spec item | Tasks |
  | --- | --- |
  | §11.2 backend moves (profiles, sessions, secret store, both providers, transfer engine) | 4, 5, 6 |
  | §11.2 progress through `host.progress_*`, Transfers rows with cancel | 6, 9, 13, 15 |
  | §11.2 UI assembly (window, view model, sidebar section, transfer options, command, settings page), built in code (§5.3) | 10, 11 |
  | §11.2 contributions: one command, one sidebar section, one settings page | 11 |
  | §11.2 core removal and `domainMethodCount` 86 | 14 |
  | §11.2 no remote UI without the plugin | 14, 15 (`none`) |
  | §4.4 `sidebar` and `settingsPage` contributions | 7 |
  | §5.1 `ISidebarSectionProvider`, `ISettingsPageProvider`, dialogs ("show a content dialog") | 7, 9 |
  | §5.2 shared names, lazy load on first visibility, the 50 ms budget | 3, 8, 15 |
  | §7 sidebar slot below the built-ins, plugin-owned windows | 7, 8, 9 |
  | §8.2 custom settings page below the declarative controls, search indexes it | 9 |
  | §9 bundled trust for a second plugin | 2, 12 |
  | §10 load failure removes contributions; backend loss | 7, 15 |
  | §11.4 payload `plugins\com.sumafile.remote`, pre-approved | 12 |
  | §12 layout `plugins/remote/` and dependency rule | 4, 5, 11 |
  | §13 fixture exercises sidebar and custom page; smoke with and without plugins; packaging check | 8, 9, 12, 15 |
  | §14 phase 3 ends green with a release-build smoke | 12, 15 |

  Carry-overs:
  - (a) Task 2;
  - (b) Task 1;
  - (c) Task 2;
  - (d) Task 3, plus the threading contract in Tasks 7 and 8 (`EnsurePrimaryItem` stays parked for phase 4, as the remote toolbar button is `overflow`);
  - (e) Task 1, derived from the phase 2 plan's "Phase 1 follow-ups closed" list;
  - (f) Tasks 3 and 15: a ten-plugin discovery test, pre-committed fallbacks with triggers, and a CI budget step;
  - (g) Task 1.
- **Decisions recorded in the spec:**
  - §5.1: object-typed UI hand-offs and a WinUI-free SDK. This replaces "It references the Windows App SDK", because a WASDK-referencing SDK would make the xUnit test project a self-contained WinUI app.
  - §12: the shared `sumafile-transfer` crate. Copying ~2,900 lines of hardened staging and journal code would fork the journal format both processes recover.
  - §11.2: the profile and credential migration (import once per legacy database, read-only; credentials in place).
  - §11.2: remote transfer settings become plugin settings, seeded once per legacy database from the 1.0.4 `transfer.policy`.
  - §11.2: the eleven plugin-owned methods. `remote_download_file` and `remote_upload_file` are dropped: they had no production caller.
  - §6.3: plugin rows stay Cancelling until the plugin ends the operation, and their per-task Options button is disabled.
  - §5.1 and §11.2: the SDK version stays `1.0.0` until phase 5.
- **Decisions to confirm with the user**, before the task named:
  - **Object-typed UI hand-offs in a WinUI-free SDK at `1.0.0` (before Task 7).**
    - This replaces spec §5.1's "It references the Windows App SDK so plugins can build WinUI controls". It also replaces the phase 1 ruling, which expected WinUI-typed contracts with a retarget and an SDK minor bump.
    - Why: an SDK that references the Windows App SDK would make the xUnit test project a self-contained WinUI app.
    - Cost: plugin authors lose compile-time typing on four members: `CreateSidebarSection`, `CreateSettingsPage`, `IPluginWindows.Attach` and `ShowDialogAsync`. The host checks the types at run time (`ValidateUiElement`, and the casts in `PluginDialogs` and `PluginWindowHost`).
    - It fixes the shape of a public contract that phase 5 publishes. Typed members could be added later in a minor version, but removing the `object` ones would need a major version.
  - **Compile-only host packages in plugin C# projects (before Task 8, where the Hello fixture first takes them).** The projects reference `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` and, for `SumaFile.Remote` (Task 11), `CommunityToolkit.Mvvm`, all with `ExcludeAssets="runtime"` and `PrivateAssets="all"`. This widens spec §12's "reference only the SDK package and the helper crate" for C#. Without the Mvvm reference, `SumaFile.Remote` does not compile (CS0012). The runtime copies always come from the host.
- **UX changes to call out to the user** (spec §11.2):
  - The Connections section moves below the built-in sidebar sections.
  - The Settings > Tools FTP/SFTP card becomes the plugin's page under Settings > Plugins.
  - Pane More > Tools no longer lists FTP/SFTP; it stays in the palette and the toolbar overflow.
  - A saved custom primary toolbar that held `ftp-sftp-manager` loses that button. The legacy default still upgrades.
  - Remote transfers now also appear in the Transfers window. On their rows, as on the 7-Zip archive rows, the per-task Options button is disabled: it wrote `transfer.overrides.v1` entries that no plugin reads. Remote transfer options live in the manager's Transfer options dialog and in Settings > Plugins.
  - Remote sessions survive a file-service restart, and end with the plugin backend.
  - The Settings window and the main window share one manager window.
  - No keyboard shortcut is lost. `ftp-sftp-manager` never had one: `KeyboardShortcutMap.Defaults` does not list it, and `NormalizeOverrides` drops unknown ids, so no stored override can exist. The plugin command cannot get one either, because plugin shortcuts are not key-bound (phase 1 Minor 9).
- **Known gaps, kept and noted:**
  - A backend killed mid-transfer (disable, shutdown, crash) cannot clean up. Its staged download is recovered by the next download into that folder; an interrupted upload's `.sumafile-partial.<nonce>` stays on the server. For the same reason, the service's disconnect-cancels-the-worker test is not ported (Task 6).
  - A Faulted plugin withdraws its section and command until Restart (phase 1 Minor 4). An open manager window stays and shows errors.
  - FTP and FTPS are covered by the ported provider tests and the scripted fake FTP server in `ftp.rs`, not by the UI smoke. The smoke drives SFTP. FTP, FTPS, SFTP private-key auth and SFTP agent auth stay `MANUAL` in parity-gate §2.6 (Task 14), because the test server accepts passwords only.
  - After a remote download into a local folder, the service no longer invalidates its listing cache: `session/jobs.rs:110-111` goes with the remote job.
    - A local pane showing that folder refreshes from the folder watcher. A cached listing is re-validated by its directory stamp, and it expires after 10 s (`session_cache.rs:85-87`).
    - Only an in-place download (`inplace` on), which changes no directory stamp, can show stale sizes, for up to 10 s.
  - Import markers are per legacy database. So a profile the user deleted in the plugin comes back if the resolved legacy path later changes, for example when `SIMPLEFILE_METADATA_DB` is set. The old table is never written, and its rows stay for a downgrade.
  - The UI smoke's credential `finally` cannot attribute a target whose profile id it never read. It names such a target in the failure instead of deleting it.
  - The startup budget is enforced in CI only once CI runs again; this phase runs the check by hand (Tasks 3 and 15). The upgrade smokes do not seed a 1.0.4 profile: they install over the user's real SumaFile and are not run in this phase.

---

## Critique resolutions

Each finding from the plan critique, in the order it was raised. Duplicates point to the row that resolves them. No task was added or removed, so the task numbering is unchanged. Inside tasks, Task 12 gains a step (its later steps are renumbered), and Task 15 swaps Steps 3 and 4.

| # | Severity | Finding | Resolution |
| --- | --- | --- | --- |
| 1 | Important | `SUMAFILE_PLUGIN_DATA_ROOT` is set only in the plugin modes, so the plain UI smoke's remote backend writes `legacy_profiles_imported` into the real data folder after importing nothing, and the real 1.0.4 profiles are never imported. | Task 8 exports the variable in every UI smoke mode, plain included, and the smoke asserts it in both plugin modes. Task 12 keeps the startup-smoke override and gives the MSI smoke the same isolation. Import markers are now per legacy database and are written only when a `remote_profiles` table exists. The settings-seed marker is per database too. Tests: Task 5 `import_marker_is_scoped_to_its_database` and `import_of_a_database_without_the_table_imports_nothing`; Task 6 `legacy_markers_are_scoped_to_the_database_they_read`. After `release:build`, Task 12 checks that the real plugin data folder is unchanged. The startup smoke's database is row 21. |
| 2 | Important | Smoke step 4 writes a real credential that only step 11 deletes, so a failure in steps 5 to 10 leaves it behind. | `SmokeRemoteAsync` records `baseline` before step 4, and `smokeTarget` once the profile is listed. A `finally` around steps 4 to 12 deletes `smokeTarget`, then fails on any other new target without deleting it. Global Constraints name the smoke as a writer. This departs from the suggested fix on purpose: the smoke does not delete every new target, so a password that a real SumaFile saves during the run is never removed. A target whose profile id the smoke never read is reported instead (Known gaps). |
| 3 | Important | Nothing tests the real upgrade chain: App environment, backend environment, legacy path resolution, import, then listing. | Three automated replacements that run now. Task 6 `backend_process_imports_a_1_0_4_profile_and_connects_with_its_saved_password` runs the real exe with an environment-resolved legacy database, and checks the row verbatim, `remote.profiles-changed`, and a connect with the 1.0.4 credential blob. Task 13 `UpgradedProfile_IsImportedAndConnectsThroughTheRealHost` goes through the real `PluginManager` and its inherited environment. Task 15 smoke step 2 checks that the Connections section lists `Legacy SFTP`. The test server writes the 1.0.4 database (`write_legacy_metadata_db`, `--legacy-db`), because C# has no SQLite library. Review Focus 1 lists all three. Not taken: seeding the upgrade smokes, which install over the user's real SumaFile. |
| 4 | Minor | The Task 1 closure list misses several phase 1 items that phase 2 closed. | Task 1 Step 6 now maps each of the phase 2 plan's eight "Phase 1 follow-ups closed" bullets to the review-notes lines it closes. That adds Minor 2's second half, Minor 5 (partly closed), the dispose-time cancellation for hung starts, the env-var collection, progress and cancel from a pane, and the thread-name sanitising. It also labels the DeactivateAsync item as closed by commit `fecf075`, not by phase 2. |
| 5 | Minor | The startup budget is checked by hand on three plugins, with no ten-plugin measure and no planned fallback. | Task 3 adds `Discover_TenPlugins_StaysUnderTheStartupBudget`: ten manifests, one `bundled.json`, one approvals file, and two signed plugins with 4 MiB payloads. Task 3 also pre-commits two fallbacks, F1 (a size and mtime hash cache) and F2 (the scan overlapped with the service start). Their triggers are a Task 3 baseline or best ten-plugin run above 25 ms, or a Task 15 delta above 50 ms. A CI budget step runs after the `-PluginsDir` smoke; it is checked statically only, since CI is not run in this phase. Task 15 Step 3 applies the fallbacks when needed. |
| 6 | Minor | The per-task Options button on plugin rows writes overrides that the remote backend never reads. | Task 9 adds `TransferOperationViewModel.CanChangeOptions` (`CanCancel && !IsExternal`). The button binds to it, and the click handler ignores external rows. Tests: `ExternalOperation_OffersNoPerTaskOptions` and `TaskOptionsButton_IsBoundToCanChangeOptions`; Task 15 smoke steps 6 and 8 also check it. It also covers the phase 2 archive rows, which never read per-task overrides either. It is listed as a UX change. |
| 7 | Minor | Collapsing parity-gate §2.6 into one row could drop the manual FTP, FTPS, private-key and agent checks. | Task 14 keeps six §2.6 rows. `remote.sftp-password` and `remote.legacy-import` are `PASS`. `remote.ftp`, `remote.ftps`, `remote.sftp-key` and `remote.sftp-agent` are `MANUAL`, each naming its manual step, and §10 gains step 17. Not taken: public-key auth in the test server. |
| 8 | Minor | The eight setting defaults live in three places with no cross-check, and the protocol's error texts are not checked against the sources. | Task 6 adds `setting_values`. Task 11 adds the Rust `manifest_defaults_match_the_engine_defaults` and the C# `RemoteTransferSettings_DefaultsMatchTheManifest`, which uses Task 10's `DefaultValues`. In Task 6's `protocol.json`, each error has a `text` and `sources`. `check-ipc-schema.mjs` requires the text before its first placeholder to start a literal in `sftp.rs`, `session.rs` or `handler.rs`, and prints `5 error texts`. |
| 9 | Minor | `test_io` needs feature `test-support`, but no dev-dependency enables it. | Task 4 adds `sumafile-transfer` with `test-support` to the service's dev-dependencies. Task 5 adds it to the remote backend's dev-dependencies and names it in the dependency list. See row 18. |
| 10 | Minor | The plan does not say what the session disconnect test becomes. | Task 6 ports only the streaming variant, as `cancel_by_request_id_answers_other_calls_and_cleans_the_partial_file`. It states that the disconnect variant is dropped in favour of `next_download_recovers_a_partial_left_by_a_killed_backend` (Known gaps). |
| 11 | Minor | Plugin-section collapse does not round-trip through saved layouts. | Task 8 adds `CollapsedPluginSidebarSections` to `WorkspaceChromeLayout`'s capture, apply and normalize, with `UiImprovementTests.PluginSectionCollapse_RoundTripsThroughASavedLayout`. |
| 12 | Minor | Two side effects of removing the core command are not listed: orphaned shortcut overrides and the lost listing-cache invalidation. | Checked in the code: `ftp-sftp-manager` is not in `KeyboardShortcutMap.Defaults`, and `NormalizeOverrides` drops unknown ids, so no override can exist and none needs migrating. This is recorded under UX changes, with Minor 9. The lost invalidation is under Known gaps, bounded by the watcher, the directory stamp and the 10 s cache age. |
| 13 | Minor | The real host first loads `SumaFile.Remote.dll` in the last task. | Both suggestions are taken. Task 9's Hello `open-window` builds a code-only `HelloWindow : Window`. Its content is a theme-aware `XamlReader` fragment, and it shows a dialog on its own `XamlRoot` (smoke step 1). Task 12 adds `SmokeRemoteAsync`'s load checks and a smoke run (Step 3) before Tasks 13 and 14. That run loads `SumaFile.Remote.dll` in the real host and opens the manager window and the profile dialog. |
| 14 | Minor | Two backends importing at once can collide on the primary key. | Task 5's `import_legacy_profiles` checks the marker and inserts with `INSERT OR IGNORE`, inside one `BEGIN IMMEDIATE` transaction under the 5 s `busy_timeout`. Test: `concurrent_imports_insert_each_profile_once`. |
| 15 | Minor | Shutdown does not pin the order "windows, backend, deactivation", and plugin windows close only after the workspace save. | Task 7 adds `Dispose_ClosesPluginWindowsBeforeTheBackendStops`. Task 9 adds `PluginWindowHost.CloseAllNow` and `MainWindow.ClosePluginWindows()`. `OnClosed` calls it where `CloseRemoteManagerWindow()` runs today, before the workspace save, and `OnClosed_ClosesPluginWindowsBeforeTheSessionCleanup` pins that. |
| 16 | Minor | The plugin protocol docs describe `cancel` only in archive terms. | Task 6 adds a "Plugin-owned methods" section to `ipc/schema/plugin/README.md`: plugin method tables, the non-error cancel outcome, and the two-id cancel. It scopes the `-32000` sentence to archive engines and adds a sentence to `cancel.notes` in `v1/protocol.json`. |
| 17 | Important | `SumaFile.Remote.csproj` fails with CS0012, because the Model's Mvvm reference is `PrivateAssets="all"`. | The first suggested fix. `SumaFile.Remote.csproj` gets the compile-only Mvvm reference. The Global Constraints allow compile-only `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` and `CommunityToolkit.Mvvm` references, and `RemotePlugin_ProjectsReferenceOnlyTheSdkAndEachOther` enforces exactly that. Task 11 Step 3 also expects no `CommunityToolkit.Mvvm.dll` in the output. It is listed as a decision to confirm before Task 8. |
| 18 | Important | The `sumafile-transfer` dependency list does not fit the moved code: `log`, `winapi`, the id generator that lives in core, and `test_io`. | Task 4 allows `log`. It ports `MoveFileExW` to `windows-sys` 0.61 (`Win32_Storage_FileSystem`, flags 0) and takes no `winapi`. It moves the generator body into `ids.rs` with its `op_<secs>_<count>` format, and core's `generate_operation_id` forwards to it. Forwarding instead of copying keeps one counter: with a copy, a transfer id and another operation id from the same second could collide. Test: `core_and_transfer_operation_ids_share_one_counter`. The service dev-dependency is row 9. |
| 19 | Minor | The remote backend's dependencies lack `log`, and its dev-dependency lacks `test-support`. | Task 5 adds `log = "0.4"` and the `test-support` dev-dependency. Task 6's `main.rs` installs a stderr logger, so the warnings reach `plugins.log`. |
| 20 | Minor | The credential constraint names one writer, but there are more. | See row 2. The constraint now lists all four writers, each with its cleanup. |
| 21 | Minor | The startup smoke runs against the user's real `metadata.db`. | Task 12's startup smoke sets `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` to temp paths, as well as `SUMAFILE_PLUGIN_DATA_ROOT`. The MSI smoke, which `release:build` also runs and which had the same exposure, gets the same isolation. |
| 22 | Minor | The expected count of 15 for the 7-Zip lib target is stale. | Task 1 Step 1 expects 16, gives the per-file counts, and treats a different count as a note in the report, not a deviation. |
| 23 | Minor | No seam reaches the approval store's `LoadCount`. | Task 3 adds `internal PluginApprovalStore Approvals` to `PluginManager`. |
| 24 | Minor | `Assert-BundledPluginList`'s check order is unstated, so case 5 could get the count message. | Task 2 fixes the order: presence, then hashes, then count. Case 5 also rewrites the list first, because case 4 leaves a stale hash that would otherwise fail first. |
| 25 | Minor | Teardown's wait for `CloseAllAsync` has no bound. | Task 7 bounds the wait with `WindowCloseTimeout` (5 s, an internal seam), logs and continues. Test: `Teardown_WindowsThatNeverClose_AreAbandonedAfterTheTimeout`. Task 9's `CloseAllAsync` completes at once when nothing is tracked or `TryEnqueue` fails. |
| 26 | Minor | `plugin.json` has a placeholder comment, and `protocol.json` has `{...}` and cut-off texts. | Task 11's manifest has `"group": "Transfers"` on each setting. Task 6 gives `protocol.json` in full: methods, event fields, enums, types and the full error texts. |
| 27 | Minor | The plain-FTP and host-key source test asks for strings a code-built plugin cannot contain. | Task 11 lists the exact strings to carry over. It replaces the XAML-only forms with `"Insecure"` and `.IsInsecure`, and anchors the mismatch slice on `ShowDialogAsync(`. |
| 28 | Minor | Task 15 Step 4 has no concrete fallback. | See row 5. Task 15's budget step (now Step 3) runs right after the plugin-mode smoke, because the no-plugin smoke would otherwise be the latest session the script reads. It applies Task 3's fallbacks when needed. |
| 29 | Minor | The object-typed SDK fixes the shape of a public contract without the user's confirmation. | Self-review gains "Decisions to confirm with the user". The first is the object-typed, WinUI-free SDK at `1.0.0`, to confirm before Task 7, with its trade-off and cost. Task 7 Step 6 records the user's answer in §5.1. |

Also fixed while resolving these:
- The MSI smoke had the same real-data exposure as the startup smoke (row 21).
- A copied id generator would have created a second counter (row 18).
- Task 2 case 5 inherited a stale hash from case 4 (row 24).
- Task 15's budget check would have read the no-plugin session (row 28).
- In Task 9 smoke step 2, `dialog.txt` is now read by its last line, because step 1 appends to it too.
