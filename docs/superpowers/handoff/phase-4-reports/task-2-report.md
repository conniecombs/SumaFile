# Phase 4 Task 2 report: scripts, smokes and the payload follow the install-folder locations

Status: DONE. Nothing committed; all work is in the working tree.

## What was implemented, per file

### `scripts/lib/plugin-staging.ps1`: the five functions, plus private helpers
- `Get-AppOwnedPluginItems -InstallDir [-BundledIds]`. `-BundledIds` defaults to `$script:BundledPluginIds`. It returns the relative paths of the app-owned items, sorted ordinal and case-insensitive:
  - `plugins.log`, `plugins.log.1`, `plugin-approvals.json` and any `plugin-approvals.json.*.tmp`;
  - `plugins-data`;
  - every folder under `plugins` whose name is not a bundled id, dot folders included.

  `bundled.json` and the bundled folders are never listed. The bundled-id match ignores case, as Windows folder names do. The result comes back unrolled, and callers wrap it in `@()`.
- `Get-AppOwnedPluginState -InstallDir`: an ordered map from relative path to SHA-256 for every file among and under those items. Every folder among and under them gets `<dir>` (see Deviations). Files are hashed with `FileShare.ReadWrite|Delete`, so a log that is still open can be read.
- `Assert-AppOwnedPluginStateUnchanged -InstallDir -Before`: throws `App-owned plugin files changed under <dir>: <path> (added|removed|changed), ...`.
- `Get-LegacyPluginLocationSnapshot [-LocalAppData]` (default `$env:LOCALAPPDATA`): an ordered map with keys relative to `<LocalAppData>\SumaFile`. It covers `plugin-approvals.json`, `plugins.log`, `plugins.log.1`, and the `plugins` and `plugins-data` folders with everything under them. A file maps to `<length>|<LastWriteTimeUtc.Ticks>` and a folder to `<dir>`; a missing item is absent. The paths are built from segments.
- `Assert-LegacyPluginLocationsUnchanged -Before [-LocalAppData]`: throws `The app changed a pre-release plugin location under %LOCALAPPDATA%\SumaFile: <path> (added|removed|changed), ...`.
- Private helpers: `Get-PluginStagingRelativePath`, `Get-PluginStagingSha256Shared` and `Get-PluginStateChanges`. The last one does the sorted added/removed/changed diff that both asserts share.

### `scripts/test-plugin-staging.ps1`: Case 8 and Case 9
- **Case 8**
  - It stages the real Debug bundled plugins with `Install-BundledPlugins` into `<temp>\plugins`, then checks:
    - the clean install folder lists `@()`;
    - with the six named items added, exactly `plugin-approvals.json, plugins-data, plugins.log, plugins.log.1, plugins\.com.example.user.installing-x, plugins\com.example.user`;
    - an extra `plugin-approvals.json.1234.0a1b.tmp` is listed too.
  - Two state snapshots are equal and pass the assert.
  - A changed `plugins-data\x\a.txt` throws, naming it and the `App-owned plugin files changed under <dir>` prefix.
  - A deleted `plugins.log` throws, naming it.
  - The header comment notes that Case 8 needs `scripts/build-plugins.ps1` first, as CI already runs it before this script.
- **Case 9**
  - A temp `-LocalAppData` with no `SumaFile` folder gives an empty snapshot, which passes.
  - A seeded legacy tree gives the expected keys. A change to `startup-timing.log`, which is not a plugin location, still passes.
  - Appending to the legacy `plugins.log` throws, naming `plugins.log` and the exact message prefix.
  - A new `plugins-data\x` folder throws, naming `plugins-data\x`.
  - All legacy paths are built with `Join-Path` segments.

### `scripts/build-winui-release.ps1`
`Assert-Payload` throws `WinUI payload must not contain app-owned plugin files: <list>` when `Get-AppOwnedPluginItems -InstallDir $Directory` returns anything.

### `scripts/smoke-winui-startup.ps1`
- It dot-sources the staging library.
- `$pluginsLog` is now `$approvalsDir\plugins.log`, and `SUMAFILE_PLUGIN_LOG_PATH` is set to it. The plugins.log offset logic is gone (reads use offset 0), and the failure messages name the log path. The comment is rewritten: only `startup-timing.log` stays in `%LOCALAPPDATA%\SumaFile`.
- Before the launch:
  - it refuses a payload that already holds app-owned plugin items;
  - it takes `Get-LegacyPluginLocationSnapshot`.
- In `finally`:
  - it stops the payload backends and then calls `WaitForExit(5000)` on each;
  - then it checks that `Get-AppOwnedPluginItems -InstallDir $payloadDir` is empty and runs `Assert-LegacyPluginLocationsUnchanged`;
  - then it removes the temp folder.

  If the run had otherwise passed (`$smokeCompleted`), a failed check throws. After an earlier failure it is written as a warning, so the first error stays the one thrown.

### `scripts/smoke-winui-ui.ps1`
- In every mode it sets `SUMAFILE_PLUGIN_DATA_ROOT`, `SUMAFILE_PLUGIN_APPROVALS_PATH=<testRoot>\state\plugin-approvals.json` and `SUMAFILE_PLUGIN_LOG_PATH=<testRoot>\plugins.log`. The approvals path is new in plain mode; the `-NoPlugins` branch no longer sets it separately.
- The old 128-129 comment now names the Debug exe folder as the install folder the smoke must never write into.
- Before the launch it takes `Get-LegacyPluginLocationSnapshot` and `Get-AppOwnedPluginState -InstallDir <Debug exe folder>`.
- In `finally`, after the app has been killed and waited for (`WaitForExit(5000)` added), it asserts both unchanged, with the same completed-or-warn rule as the startup smoke, and adds `Evidence: <testRoot>`.

### `scripts/smoke-winui-msi.ps1`
It adds `SUMAFILE_PLUGIN_LOG_PATH = <smokeRoot>\plugins.log`, and the comment mentions the plugin log.

### `scripts/check-winui-packaging.mjs`
- `build-winui-release.ps1` must contain `Get-AppOwnedPluginItems`.
- `smoke-winui-startup.ps1`, `smoke-winui-ui.ps1` and `smoke-winui-msi.ps1` must contain `SUMAFILE_PLUGIN_LOG_PATH`.
- New `forbiddenScriptSnippets`: `SumaFile\plugins.log`, `SumaFile\plugins-data` and `SumaFile\plugin-approvals.json`.
  - It scans every `.ps1`, `.mjs` and `.js` file under `scripts/`, recursively and skipping `node_modules`. Only `check-winui-packaging.mjs` itself is exempt.
  - Matching ignores case, after `\\` and `/` are normalized to `\`.

### `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs` (`SmokePluginsAsync`)
- New `SmokePluginLocations(dataRoot)` runs in every mode. It asserts that `PluginDiscovery.LogPath()`, `PluginApprovalStore.DefaultPath()` and `PluginDiscovery.DataRoot()` lie under `SIMPLEFILE_UI_SMOKE_ROOT`. It also asserts that no `PluginDiscovery.DefaultRoots()` entry is `%LOCALAPPDATA%\SumaFile` or lies under it.
- New `SmokePluginBackendTempFolders(manager, dataRoot)`: for every record with `BackendRunning`, `<DataRoot>\<id>\tmp` must exist. It returns the ids it checked. It runs:
  - in `with` mode, right after the Hello backend is seen running, and the result must include `com.sumafile.hello`;
  - in every mode, at the end of `SmokePluginsAsync`.

### Spec (`docs/superpowers/specs/2026-11-07-plugin-system-design.md`)
- **§7:** the "Plugin data root" bullet is now "Plugin locations". It covers all three resolvers and their overrides, the smoke pointing all three into its test root, the in-app assertions, and the script-side snapshots.
- **§11.4, startup smoke:** it now sets `SUMAFILE_PLUGIN_LOG_PATH` to a fresh `plugins.log` in its temp folder and reads the plugin lines there. Only `startup-timing.log` stays in `%LOCALAPPDATA%\SumaFile`.
- **§11.4, MSI smoke:** the bullet now mentions the plugin log.
- **§11.4, new "Phase 4" paragraph:** app-owned plugin items, the `Assert-Payload` refusal, the startup smoke's before and after checks, and the forbidden-snippet rule.

## TDD evidence

RED: the tests and checks were written first.
```
> powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1
R:\Repos\SumaFile\scripts\test-plugin-staging.ps1 : The term 'Get-AppOwnedPluginItems' is not recognized as the name
of a cmdlet, function, script file, or operable program. ...
    + FullyQualifiedErrorId : CommandNotFoundException,test-plugin-staging.ps1

> npm run check:winui-packaging
WinUI packaging check failed: scripts/build-winui-release.ps1 must include Get-AppOwnedPluginItems.
WinUI packaging check failed: scripts/smoke-winui-startup.ps1 must include SUMAFILE_PLUGIN_LOG_PATH.
WinUI packaging check failed: scripts/smoke-winui-ui.ps1 must include SUMAFILE_PLUGIN_LOG_PATH.
WinUI packaging check failed: scripts/smoke-winui-msi.ps1 must include SUMAFILE_PLUGIN_LOG_PATH.
WinUI packaging check failed: scripts/smoke-winui-startup.ps1 must not reference the pre-release plugin location SumaFile\plugins.log; plugin files live in the installation folder.
WinUI packaging check failed: scripts/smoke-winui-ui.ps1 must not reference the pre-release plugin location SumaFile\plugins-data; plugin files live in the installation folder.
```
The last line is the UI smoke's old comment `%LOCALAPPDATA%\SumaFile\plugins-data`, which is now rewritten.

The first implementation run failed Case 8: `a clean staged install folder lists app-owned items: [System.String[]]`. I had returned the array with `, $sorted`, and `@()` wrapped it into a one-element array. Returning it unrolled fixed that.

GREEN:
```
> powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1
Plugin staging tests passed.
> npm run check:winui-packaging
WinUI packaging surface is wired.
```
Extra checks:
- A probe file under `scripts/`, deleted afterwards, holding `SumaFile\plugins-data` and `x/SumaFile/plugin-approvals.json` was reported by the forbidden-snippet check.
- A probe of the assert reported `plugins-data (added), plugins.log (changed)`.
- All six edited PowerShell scripts parse with 0 errors.

UI smokes:
- `npm run smoke:winui-ui -- -Counts 10000` (full build, plain mode): `UI runtime smoke passed`, exit 0.
  - Its `plugins.log` is in the test root and begins with the three `[warn] ... replaces ...` override lines, `SUMAFILE_PLUGIN_LOG_PATH` included.
  - `plugins-data\com.sumafile.sevenzip\tmp` was created there.
  - No legacy-location or app-owned-item failure.
- `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -PluginsDir target/fixture-plugins/com.sumafile.hello` (after `scripts/build-fixture-plugin.ps1`): `UI runtime smoke passed`, exit 0. Its scenarios included plugins, sevenzip, sevenzip-backend-loss, remote, remote-backend-loss and service-reconnect.
- Also run: `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`: `UI runtime smoke passed`, exit 0.

## Full-suite results
- `npm run check`: exit 0. It reported 86 domain methods and `WinUI packaging surface is wired.`
- `npm run test:maintenance`: 66 tests, 66 passed.
- `npm run check:winui`:
  - First run: 1158/1159. `CommandIdConsistencyTests.EveryCatalogCommand_HasAnAppHandler` failed once. It passed alone (4/4).
  - Full rerun: `Passed! - Failed: 0, Passed: 1159`.
  - That test shares the static `AppCommandCatalog` with tests running in parallel, and this task changed no code it covers.
- The App's Debug build (inside the smoke's `build:winui` and a direct `dotnet build`): `0 Warning(s) 0 Error(s)`.
- No Rust changed.
- The startup and MSI smokes need a fresh release payload and MSI. As the plan says, they run in Task 3's `release:build`, so they were not run here. The existing `dist\winui\payload` predates Task 1: its app would ignore `SUMAFILE_PLUGIN_LOG_PATH`, so a run against it would prove nothing.

## Deviations
1. **`Get-AppOwnedPluginState` also records folders as `<dir>`**, besides the SHA-256 of every file. Without this, an app that wrongly created an empty `plugins-data` (or an empty plugin folder) in the Debug exe folder would pass the UI smoke's before/after comparison.
2. **The legacy snapshot includes the `plugins` and `plugins-data` folders themselves** as `<dir>`, not only what is under them, so creating either one is caught.
3. **The forbidden-snippet check is stricter than the plan's wording.** It ignores case and normalizes `\\` and `/` to `\`, so an escaped JS string or a forward-slash path cannot slip through. There are still no exemptions besides the check itself.
4. **`smoke-winui-startup.ps1` also refuses a payload that already holds app-owned plugin items before it launches**, so a stale payload is reported as such rather than blamed on the run.
5. **`smoke-winui-ui.ps1` now waits up to 5 s for the killed app to exit** before the after-run checks.
6. **The after-run checks in both smokes only warn when the run had already failed**, so the original error is not masked. They throw when the run had otherwise passed.

## Concerns
- **Case 8 needs built inputs.** Because the plan names `Install-BundledPlugins`, it needs the Debug bundled-plugin sources (`target\debug\*-backend.exe` and the remote UI's Debug build). CI runs `build-plugins.ps1` before this script, and `release:build` runs it in Debug before `check:winui`. Running the script on a clean checkout without building fails with the staging library's "Run cargo build ..." message.
- **Pre-existing, outside this task's scope:** the startup and MSI smokes still stop `simplefile-service` by name in `finally`. Task 3's installer smokes stop processes by path; these two may want the same later.
- **One timing-dependent C# test** failed once (see Full-suite results) and passed on every rerun.
