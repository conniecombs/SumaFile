# Phase 4 Task 1 report: keep every plugin file in the installation folder (host side)

Status: DONE. Nothing committed; all work is in the working tree.

## What was implemented, per file

### Plugin host (`src-winui/SimpleFile.PluginHost`)
- `Discovery/PluginDiscovery.cs`
  - `DefaultRoots()` returns `[InstallRoot()]` unless `SUMAFILE_PLUGIN_ROOTS` is set.
  - `DataRoot()` returns the trimmed `SUMAFILE_PLUGIN_DATA_ROOT`, else `Path.Combine(AppContext.BaseDirectory, "plugins-data")`.
  - New `public const string LogPathVariable = "SUMAFILE_PLUGIN_LOG_PATH";` and `public static string LogPath()`: the trimmed override when it is not blank, else `Path.Combine(AppContext.BaseDirectory, "plugins.log")`.
  - `DescribeOverrides()` adds `[warn] SUMAFILE_PLUGIN_LOG_PATH replaces the plugin log: <path>` after the data-root line.
  - `UserRoot()` is deleted. A shared private `Override(variable)` helper trims and drops blanks for all three single-value overrides.
- `Trust/PluginApprovalStore.cs`: `DefaultPath()` returns the override or `Path.Combine(AppContext.BaseDirectory, "plugin-approvals.json")`.
- `Backend/IPluginProcessLauncher.cs`: `Launch(executable, args, workingDirectory, IReadOnlyDictionary<string,string> environment, onStderrLine)`. `ProcessLauncher` sets each entry in `ProcessStartInfo.Environment`, on top of the inherited environment.
- `Backend/PluginBackendHost.cs`: `StartAsync` creates `<dataDir>` and `<dataDir>\tmp` (new `CreateDataFolders`), then launches with `TMP` and `TEMP` both set to `<dataDir>\tmp`. The folder is never emptied. An `UnauthorizedAccessException` or `IOException` while creating either folder throws `PluginBackendException("SumaFile cannot create the data folder of plugin '<id>' at <dataDir>: <inner message>")`, and nothing is launched.
- `PluginRecord.cs`: `public bool IsBundledId { get; internal set; }`.
- `PluginManager.cs`
  - `PluginEvaluation` gains a trailing `bool IsBundledId`, and `ToRecord()` copies it.
  - `Evaluate` computes `bundledRoot is not null && bundled.ContainsKey(plugin.Id)` once, for every discovery state.
  - A record kept across a rescan takes the candidate's `IsBundledId`.
  - The `IsBundled` doc comment no longer mentions a user root.
- `Install/PluginPackageInstaller.cs`
  - `Install(sfpluginPath, pluginsRoot)`: right after `ReadManifestId`, it reads `BundledPluginList.Read(root)` and refuses a listed id with `"<id> ships with SumaFile and cannot be installed from a package."`. This happens before any disk write.
  - An `UnauthorizedAccessException` from `Directory.CreateDirectory(root)` or from creating the staging folder becomes `"SumaFile cannot write to its plugins folder <root>: <inner message>"`. An `IOException` there becomes the existing "could not be installed" message; before, an `IOException` creating the root escaped unwrapped.
  - `Remove(pluginFolder, pluginsRoot)`: the outside-the-root message is now `"Only plugins in SumaFile's plugins folder can be removed."`. A folder whose name `bundled.json` lists (case-insensitive) is refused with `"<name> ships with SumaFile and cannot be removed. Disable it instead."`.
  - New `IsRemovable(PluginRecord record, string pluginsRoot)`: the record's root equals `pluginsRoot` (full path, case-insensitive, trailing separator ignored), and `!record.IsBundledId`.

### App (`src-winui/SimpleFile.App`)
- `SettingsWindow.Plugins.cs`
  - `UserPluginRoot` is replaced by `PluginsRoot => PluginDiscovery.InstallRoot()`, which Install from file, Remove and Open plugins folder use.
  - `CanRemove` calls `PluginPackageInstaller.IsRemovable(record, PluginDiscovery.InstallRoot())`.
  - The App-side guard in `RemovePluginAsync` uses the new messages: the bundled message for a bundled id, else the outside-the-folder message.
- `PluginSettingsPanelBuilder.cs`: the Remove confirmation now reads `Remove <name>? Its folder in SumaFile's plugins folder is deleted. Its settings and data are kept.`
- `MainWindow.Plugins.cs`
  - `LocalSumaFileDirectory()` is deleted.
  - `AppendPluginLog` writes to `PluginDiscovery.LogPath()`, resolved once into `private static readonly string PluginLogPath`, and creates the parent folder. A failed write is still swallowed, with no `%LOCALAPPDATA%` fallback.
  - The data-root comment (line 90) and the overrides comment are updated.

### Tests (`src-winui/SimpleFile.Tests`)
- `PluginEnvironmentTests.cs` (rewrite)
  - Every test saves, clears and restores all four plugin variables through `WithCleanPluginEnvironment`.
  - New tests: `DefaultRoots_AreTheInstallRootOnly`, `ApprovalStore_DefaultPath_IsInTheInstallFolderUnlessOverridden`, `DataRoot_IsInTheInstallFolderUnlessOverridden`, `LogPath_IsInTheInstallFolderUnlessOverridden`, `DescribeOverrides_ReportsTheLogPathOverride` and `DefaultLocations_NeverPointIntoLocalAppDataSumaFile`.
  - Kept and adapted: `DefaultRoots_EnvOverride_ReplacesDefaultsAndSkipsEmpties` (`" ; "` gives `[InstallRoot()]`), `DescribeOverrides_ReportsActiveEnvironmentOverrides` (now includes the log line) and `DescribeOverrides_ReportsTheDataRootOverride`.
  - Deleted: `UserRoot_IsLocalAppData_EvenWithEnvOverride`, `DataRoot_IsLocalAppDataUnlessOverridden` and `DefaultRoots_InstallThenLocalAppData`.
- `PluginPackageInstallerTests.cs`
  - The root is now `<temp>\plugins`. `SeedBundledRoot()` writes `com.sumafile.sevenzip\plugin.json` and a `bundled.json` that lists it.
  - New tests: `Install_PackageWithABundledId_IsRefusedAndWritesNothing`, `Install_UserPackage_LandsBesideTheBundledFolders`, `Install_UnwritableRoot_ExplainsTheFolderIsNotWritable`, `Remove_BundledPlugin_IsRefused`, `IsRemovable_UserPluginInThePluginsRoot_IsTrue`, `IsRemovable_BundledIdWithAModifiedManifest_IsFalse` and `IsRemovable_PluginInAnotherRoot_IsFalse`.
  - Renamed: `Remove_DeletesAUserPluginFolderInThePluginsRoot` and `Remove_RefusesFolderOutsideThePluginsRoot`. The latter now asserts the new message.
  - New internal helper `DenyAce.Add(folder, rights)`: a non-inherited Deny ACE for the current user, which `Dispose` removes before the folder is deleted. The manager and backend-host tests reuse it.
- `PluginManagerTests.cs`
  - `UnlistedPluginInTheInstallRoot_NeedsConsent` replaces `BundledPlugin_UserRootCopy_NeedsConsent`.
  - New tests: `BundledId_WithAModifiedManifest_NeedsConsentAndStaysABundledId` and `Enable_ApprovalsFolderNotWritable_ThrowsAndChangesNothing`.
  - `Manager()` gains an optional `approvalStorePath`. `UserRoot` is renamed `OtherRoot` (a root only `SUMAFILE_PLUGIN_ROOTS` can add). `ThrowingLauncher` and `BlockingThrowingLauncher` take the new parameter.
- `PluginBackendHostTests.cs`
  - New tests: `Start_PointsTmpAndTempAtTheDataFolder`, `Start_DataFolderNotWritable_FailsWithAClearMessage` and `ProcessLauncher_PassesTheEnvironmentToTheBackend`. The last one runs real `cmd.exe /d /c "1>&2 echo %TMP%"` with `TMP=<temp>\x y`.
  - `FakePluginProcessLauncher` records `LastEnvironment`.
- `WinUiSourceShapeTests.cs`: new `PluginLocations_NeverResolveFromLocalAppData` and `PluginUiSources_NeverResolveTempOrUserFolders`.
- Launcher doubles updated for the new parameter:
  - `PluginTrustTests.NeverLaunches`;
  - `Remote/RemoteBackendHarness.RecordingLauncher`, which passes the environment on to the real `ProcessLauncher`;
  - `PluginProcessLauncherTests`, which passes an empty environment. This file was not in the plan's list; see Deviations.

### Docs
- Spec (`docs/superpowers/specs/2026-11-07-plugin-system-design.md`):
  - §4.1: one root, shadowing only under `SUMAFILE_PLUGIN_ROOTS`, Install from file into that root with bundled ids refused. A new paragraph "Locations (phase 4 decision)" records the four defaults and their overrides in a table, the `TMP`/`TEMP` rule and the UI-code rule, the unwritable install folder behaviour, no migration, and the app-owned files that stay in `%LOCALAPPDATA%`.
  - §5.3: the spike rationale now says compiled XAML would need files at the `ms-appx:///` root beside `SumaFile.exe`, outside the plugin's own folder, which breaks §1's single-folder rule.
  - §6.1: the data dir is `<install dir>\plugins-data\<plugin-id>\`, plus `TMP`/`TEMP`.
  - §7: the data-root bullet path. Task 2 renames the bullet.
  - §8.3: Remove semantics, the confirmation text and the bundled refusal.
  - §9: the trust sentence ("A plugin the user installed in the install root, and lists in any other root, always need consent"), the bundled-id-with-modified-manifest case, and the approval store path and its Enable ordering.
  - §11.2: profiles in `<install dir>\plugins-data\com.sumafile.remote\remote-profiles.db`.
- `README.md` and `docs/SECURITY.md`: the profiles path is now under the install folder. Both keep their CRLF line endings.
- Review notes: a new "Phase 4 rulings" section quotes the user requirement and records the decisions. It also notes that the phase 3 closure citing `DataRoot_IsLocalAppDataUnlessOverridden` is superseded.

## TDD evidence

RED: the tests were written first, then the step 2 filter was run.

```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginEnvironmentTests|FullyQualifiedName~PluginPackageInstallerTests|FullyQualifiedName~PluginManagerTests|FullyQualifiedName~PluginBackendHostTests|FullyQualifiedName~WinUiSourceShapeTests"
PluginBackendHostTests.cs(416,51): error CS0535: 'FakePluginProcessLauncher' does not implement interface member 'IPluginProcessLauncher.Launch(string, IReadOnlyList<string>, string, Action<string>)'
PluginManagerTests.cs(2162,53): error CS0535: 'PluginManagerTests.BlockingThrowingLauncher' does not implement interface member 'IPluginProcessLauncher.Launch(...)'
PluginManagerTests.cs(2186,45): error CS0535: 'PluginManagerTests.ThrowingLauncher' does not implement interface member ...
PluginTrustTests.cs(966,42): error CS0535: 'PluginTrustTests.NeverLaunches' does not implement interface member ...
Remote\RemoteBackendHarness.cs(327,46): error CS0535: 'RemoteBackendHarness.RecordingLauncher' does not implement interface member ...
```
The build failed as expected, but only the CS0535 errors appeared: Roslyn stops at the declaration-level CS0535 errors before it binds method bodies. So the CS0117 errors (`LogPath`, `LogPathVariable`, `IsRemovable`), CS1061 (`IsBundledId`) and CS1501 that the plan expected were not reached. The cause is the same: the test code uses APIs that did not exist yet.

The first GREEN run caught one bug in a test's expected value:
```
Failed SimpleFile.Tests.PluginPackageInstallerTests.Install_UserPackage_LandsBesideTheBundledFolders
Expected: ["com.sumafile.sevenzip", "com.example.pkg"]   Actual: ["com.example.pkg", "com.sumafile.sevenzip"]
Failed!  - Failed: 1, Passed: 141, Skipped: 0, Total: 142
```
The test listed the folders in the wrong ordinal order. After fixing that expectation, GREEN with the same filter:
```
Passed!  - Failed:     0, Passed:   142, Skipped:     0, Total:   142, Duration: 15 s - SimpleFile.Tests.dll (net10.0)
```

## Full-suite results
- `npm run check:winui`, which is the whole `SimpleFile.Tests` project:
  - First run: 1158/1159 passed. `TransferManagerViewModelTests.EnqueueExternal_RunsAtOnceWithItsOwnTitleAndLeavesTransferSlotsFree` failed once. It is a timing test in code this task does not touch.
  - That class rerun alone 3 times: 13/13 passed each time.
  - Full rerun: `Passed! - Failed: 0, Passed: 1159, Skipped: 0, Total: 1159`.
- `dotnet build src-winui/SimpleFile.sln -c Debug`: `Build succeeded. 0 Warning(s) 0 Error(s)`. This covers the App, which the test project does not build.
- `npm run check`: exit 0, with 86 domain methods and the parity gate at 86.
- `npm run test:maintenance`: 66/66 passed.
- No Rust files changed, so no cargo runs were needed.

## Deviations
1. **`PluginProcessLauncherTests.cs`** also calls `ProcessLauncher.Launch` and had to take the new `environment` parameter (an empty dictionary). The plan's list of doubles left it out.
2. **`DenyAce` helper.** It lives as an `internal static class` in `PluginPackageInstallerTests.cs`, and `PluginManagerTests` and `PluginBackendHostTests` use it too. The plan does not name a helper; this avoids three copies of the ACL code. It uses only in-box `System.Security.AccessControl` and `System.Security.Principal`, so no new package.
3. **`Install` maps `UnauthorizedAccessException` only from creating the root or the staging folder.** That is how Step 3 words it. A later access error, during extraction or the final move, keeps the existing "could not be installed" message, so a locked plugin folder is not misreported as an unwritable plugins folder.
4. **`Remove`'s bundled message uses the folder name as `<name>`.** The method gets only the folder, and the folder name is the id. The App-side guard in `RemovePluginAsync` shows the manifest's display name when it has one. It is defence in depth only, because `CanRemove` hides the button for bundled plugins.
5. **Kept `DescribeOverrides_ReportsActiveEnvironmentOverrides` and `DescribeOverrides_ReportsTheDataRootOverride`.** I adapted them rather than deleting them, which extends coverage; the plan lists only the tests to add and the two to delete.
6. **Spec §11.4 is not touched here.** Its lines about the startup smoke reading `plugins.log` from `%LOCALAPPDATA%` and NSIS never touching the user root belong to Task 2 and Task 3, which the plan assigns them to.

## Concerns
- The plan expected RED to show CS0117, CS1061 and CS1501. Only CS0535 appeared, for the reason given under TDD evidence. The RED still comes from the missing APIs.
- `TransferManagerViewModelTests.EnqueueExternal_RunsAtOnceWithItsOwnTitleAndLeavesTransferSlotsFree` failed once in a full run under load and passed on every rerun. It looks flaky and is unrelated to this task.
- The smoke scripts still assume the old log location: `smoke-winui-startup.ps1` reads `plugins.log` from `%LOCALAPPDATA%\SumaFile`. Those smokes were not run in this task; Task 2 updates them.
