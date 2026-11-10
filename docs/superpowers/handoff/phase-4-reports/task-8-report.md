# Task 8 report: Plugin manager: panel creation, decoration calls, setting writes and the legacy settings migration

Status: DONE. Nothing committed; all changes are in the working tree. No project, package or lock file changed. Base snapshot for this task: `16bab507bfbfd430676d7849a879b08830938601`.

## What changed, per file

### `src-winui/SimpleFile.PluginHost/PluginManager.cs`
The new members sit between `CreateSettingsPageAsync` and `ActivateAsync`.
- **`CreatePanelAsync(pluginId, panelId, detached, ct)`** follows the same pattern as `CreateSidebarSectionAsync`:
  - The declared check runs inside `ActivateAsync`. An undeclared panel throws `InvalidOperationException("Plugin '<id>' has no panel '<panelId>'.")` before any state changes.
  - An entry type without `IPanelProvider` gives LoadFailed with `Plugin '<id>' declares a panel but its entry type does not implement IPanelProvider.`
  - Otherwise `CreateElementAsync` calls `provider.CreatePanel(new PluginPanelRequest(spec.Id, spec.Provider, detached))` on the UI thread. A throw, a null result or an element `ValidateUiElement` rejects marks the plugin LoadFailed with one of these messages:
    - `Plugin '<id>' failed to create panel '<panelId>': …`
    - `Plugin '<id>' returned no element for panel '<panelId>'.`
    - `Plugin '<id>' panel '<panelId>' <reason>`
- **`GetRowDecorationsAsync(pluginId, request, ct)`**:
  - `ActivateAsync` activates the plugin on the UI thread through `IPluginUiThread.RunAsync`.
  - It throws `InvalidOperationException` when the plugin is not Enabled, or when `request.Provider` is not the declared `rowDecorations.provider` (`Plugin '<id>' has no row decoration provider '<p>'.`).
  - An entry type without `IRowDecorationProvider` gives LoadFailed with `Plugin '<id>' declares row decorations but its entry type does not implement IRowDecorationProvider.`
  - The provider runs through `Task.Run(() => provider.GetDecorationsAsync(request, ct), ct)`.
  - A throw (synchronous or a faulted task) or a null result gives `null` and one `[warn] Row decorations failed: <message>` line. For a null result the message is `the provider returned no result.` The plugin stays Enabled and its epoch does not change.
  - An `OperationCanceledException` while `ct.IsCancellationRequested`, during the migration wait, the activation or the provider call, gives `null` and logs nothing.
- **`MigrateLegacySettingsAsync(ct)`**:
  1. Under `_gate` it collects the `(id, setting)` pairs of every non-Shadowed record with `IsBundledId` and a manifest whose setting declares `MigrateFrom`, deduplicated by storage key.
  2. One `GetSettingsAsync` reads every `plugin.<id>.<key>` and every legacy key.
  3. For each pair where the plugin value is null or whitespace and the legacy value is non-null and passes `PluginSettingsSchema.ValidateValue`, it calls `SetSettingAsync` once and logs `[info] Migrated setting <key> from <legacy>` under the plugin id.
  4. It returns the count. It never writes or deletes the legacy key.

  The method stores `BoundMigrationAsync(migration)` in `_legacyMigration` (`Volatile`). That task never faults: it completes when the migration ends, or after `LegacyMigrationWait` (internal seam, default 5 s). On timeout it logs `host [warn] The legacy settings migration is still running after 5 s; activating plugins anyway.` once. A failure is logged once as `host [warn] The legacy settings migration failed: <message>`. A migration cancelled through its own token is not logged.
- **`ActivateAsync`** awaits `_legacyMigration` (with the caller's `ct`) first, before the Enabled check and `EnsureActivatedAsync`. So commands, sidebar sections, settings pages, panels and decorations all wait for the latest migration. Its summary now lists them.
- **`WriteSettingAsync(pluginId, storageKey, value, ct)`**:
  - Under `_gate` it finds the live record's declared setting whose `PluginHostMethods.SettingsKey(pluginId, key)` equals `storageKey` (ordinal). Otherwise it throws `ArgumentException` (param `storageKey`). That covers another plugin's key, an undeclared key, a legacy key, a case variant and an unknown plugin.
  - A value that `ValidateValue` refuses throws `ArgumentException` (param `value`) with the schema message.
  - It writes through `_options.Settings.SetSettingAsync`, then calls `_options.InvalidateDecorations?.Invoke(pluginId)`. A throwing callback is logged as `[warn]` because the write already happened.
  - It does not require the plugin to be Enabled, and it loads no plugin code.
- `using System.Globalization;` for the invariant number format in the timeout line.

### `src-winui/SimpleFile.Tests/PluginManagerTests.cs`
- The `Manager(...)` helper gains `settings`, `panelsFor` and `invalidateDecorations` parameters.
- `WriteUiPlugin` now always declares one panel (`bottom`, provider `bottom-panel`, `detachable: true`) and `rowDecorations: { provider: "badges" }`. With `legacySetting: true` it also declares the boolean `enabled` (`migrateFrom: "enableGitIntegration"`) and the string `label`.
- `UiPlugin` also implements `IPanelProvider` and `IRowDecorationProvider`. It records requests and threads, and its `Panel` and `Decorate` behaviour can be replaced.
- New test doubles:
  - `RecordingPanels` (`IPluginPanels`);
  - `StoreSettings` (`ISettingsBackend`), which reads back its writes, records reads and writes, and can gate reads.
- New tests:
  - Panels:
    - `CreatePanel_ActivatesOnceAndRunsTheProviderOnTheUiThread`
    - `CreatePanel_PassesDetachedToTheProvider`
    - `CreatePanel_UndeclaredPanel_ThrowsWithoutLoading`
    - `CreatePanel_ProviderThrows_MarksLoadFailedAndWithdrawsContributions` (it also covers a null element and a rejected element)
    - `CreatePanel_EntryTypeWithoutIPanelProvider_MarksLoadFailed`
  - Decorations:
    - `GetRowDecorations_RunsTheProviderOffTheUiThread`
    - `GetRowDecorations_ProviderThrows_ReturnsNullAndKeepsThePluginEnabled` (a synchronous throw, a faulted task and a null result each give exactly one `[warn]` line, and there are no `[error]` lines)
    - `GetRowDecorations_CancelledByTheCaller_ReturnsNullWithoutALogLine`
    - `GetRowDecorations_EntryTypeWithoutTheInterface_MarksLoadFailed`
    - `GetRowDecorations_NotEnabled_ThrowsInvalidOperation` (it also covers an undeclared provider name)
  - Migration and setting writes:
    - `MigrateLegacySettings_CopiesAFalseLegacyValueOnce` (one read holds both keys, the second call copies 0, and the legacy key is untouched)
    - `MigrateLegacySettings_NeverOverwritesAValueTheUserSet`
    - `MigrateLegacySettings_IgnoresPluginsNotListedInBundledJson` (the legacy key is never even read)
    - `MigrateLegacySettings_SkipsAnInvalidLegacyValue` (`"maybe"`)
    - `MigrateLegacySettings_ReplacesABlankValueAndIgnoresAMissingLegacyKey` (extra)
    - `Activate_WaitsForTheLegacySettingsMigration`
    - `Activate_MigrationThatNeverEnds_ActivatesAfterTheBoundAndLogs` (extra: it pins the 5 s bound through the seam, and that the warning is logged once)
    - `WriteSetting_WritesTheKeyAndInvalidatesThatPluginsDecorations` (the write comes before the invalidation, and no plugin code loads)
    - `WriteSetting_KeyOfAnotherPlugin_IsRefused` (it also covers an undeclared key, a legacy key, a case variant, an unknown plugin and an invalid value; nothing is written and nothing is invalidated)
  - Host services:
    - `Services_PanelsAndDecorations_ReachTheHostWithThePluginId`
    - `Services_WithoutPanelsFor_ReportNoPanelVisibleAndRefuseShow`

### `docs/superpowers/specs/2026-11-07-plugin-system-design.md`
- §7, panel bullet: `CreatePanelAsync`, its presentations (`Detached` false, then true), and its failure rules and messages.
- §7, decoration bullet: `GetRowDecorationsAsync` (UI-thread activation, queued because its caller is on the thread pool, #82), the thread-pool provider call, the throw, null and cancel rules, `IPluginHost.Panels` with the no-panels service, and `IPluginHost.Decorations`.
- §8.1: `MigrateLegacySettingsAsync`, which covers bundled only, one read, copy once while unset or blank, a validated value, never touching the legacy key, the count, the 5 s activation wait and its log lines. It also covers `WriteSettingAsync`, with its key and value rules and the invalidation.
- §10: a new row says that a decoration provider that throws (its own timeout included) or returns null is logged as `[warn] Row decorations failed: …` and is not a load failure. It also says that the host's 250 ms budget logs nothing and that cancelled requests are not logged.

## TDD evidence

RED, with the tests written and no production change:
```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManagerTests"
exit=1
     10 error CS1061: 'PluginManager' does not contain a definition for 'CreatePanelAsync'
      9 error CS1061: 'PluginManager' does not contain a definition for 'GetRowDecorationsAsync'
      1 error CS1061: 'PluginManager' does not contain a definition for 'LegacyMigrationWait'
     10 error CS1061: 'PluginManager' does not contain a definition for 'MigrateLegacySettingsAsync'
      6 error CS1061: 'PluginManager' does not contain a definition for 'WriteSettingAsync'
```
These are the expected CS1061 errors for the four new methods, plus the test seam.

GREEN, with the same filter after the implementation:
```
exit=0
Passed!  - Failed:     0, Passed:    93, Skipped:     0, Total:    93, Duration: 17 s
```

Behavioural RED check: commenting out the migration wait in `ActivateAsync` made both activation tests fail:
```
Failed SimpleFile.Tests.PluginManagerTests.Activate_WaitsForTheLegacySettingsMigration   Assert.False() Failure
Failed SimpleFile.Tests.PluginManagerTests.Activate_MigrationThatNeverEnds_ActivatesAfterTheBoundAndLogs   Assert.Single() Failure
```
The line was then restored. The 21 new tests also passed three times in a row (`--no-build`), about 0.7 s per run.

## Full-suite results
- `npm run build:winui`: exit 0, `0 Warning(s)`, `0 Error(s)`.
- `npm run check:winui`: `Passed!  - Failed: 0, Passed: 1248, Skipped: 0, Total: 1248`.
- `npm run check`: exit 0. The parity gate lists 86 commands, and no IPC method changed.
- `npm run test:maintenance`: 66 tests, 66 passed, 0 failed.
- No Rust code changed, so cargo was not run. No `packages.lock.json` changed. `Cargo.lock` was already modified before this task and was not touched.

## Deviations
1. **Where the migration bound counts from.** The plan says "ActivateAsync awaits it (bounded 5 s, then logs and continues)". The bound is one task per migration, counted from the migration's start, not 5 s per activation. So a hung settings backend delays activations by at most 5 s in total, and the warning is logged once, not once per activation.
2. **Where the wait sits.** The wait comes first in `ActivateAsync`, before the Enabled check, rather than just before `EnsureActivatedAsync`. So the Enabled and declared checks see the state after the wait, and no captured record or runtime goes stale while it waits.
3. **`WriteSettingAsync` also validates the value** with `PluginSettingsSchema.ValidateValue` and throws `ArgumentException` (param `value`), so nothing invalid is ever stored. The plan only named the key rule. It accepts any live record with a manifest, not only Enabled ones, so Settings can edit a disabled plugin's settings.
4. **`GetRowDecorationsAsync` checks `request.Provider`** against the declared `rowDecorations.provider`. A different name is treated like an undeclared contribution (`InvalidOperationException`, nothing changes). It also returns null without logging when the caller cancels during the migration wait or the activation, not only during the provider call.
5. **A failed migration is logged by the manager** (`host [warn] The legacy settings migration failed: …`), and the returned task still faults. Task 11's caller therefore does not need to log the failure again.
6. **Extra tests:** `MigrateLegacySettings_ReplacesABlankValueAndIgnoresAMissingLegacyKey` and `Activate_MigrationThatNeverEnds_ActivatesAfterTheBoundAndLogs`, with the internal seam `LegacyMigrationWait`.

## Concerns
- **A migration that runs during an activation.** `MigrateLegacySettingsAsync` publishes its bound only after the migration's synchronous part has started. An activation already past its wait on another thread at that moment does not wait. Task 11 calls the migration in `InitializePluginsAsync` right after the mark, before the startup gate opens decoration requests, so this cannot happen in the App's order. The Review Focus 1 path, a first start with Git's `enabled` false, is pinned by `Activate_WaitsForTheLegacySettingsMigration`.
- **A retired runtime.** If a plugin is disabled while `GetRowDecorationsAsync` waits, the `PluginRetiredException` from `ActivateAsync` propagates. The coordinator (Task 9) and `PluginRowDecorationSource` (Task 11) skip a failing source and log it, and they need to keep doing so.
- **Nothing in the App calls the four methods yet.** Tasks 10 and 11 wire them, through `MainWindow.PluginPanels`, `PluginRowDecorationSource`, `InitializePluginsAsync` and Settings > Plugins.
- **The install-folder requirement.** The user's standing requirement is that no plugin file is stored outside the installation folder. This task does not affect it: it adds no file, path or storage location. Migrated values go to the existing settings database as `plugin.<id>.<key>`, which the Global Constraints name as the app's own store. They are not plugin files.

## Fix round 1

### Finding (Important): a cancelled decoration call could abandon a half-activated instance
`GetRowDecorationsAsync` passed the caller's per-navigation token through `ActivateAsync` and `PluginRuntime.EnsureActivatedAsync` into `IPlugin.ActivateAsync`. When a superseded navigation cancelled the call mid-activation, the instance was dropped without `DeactivateAsync`, and the next call created and activated a second instance.

### Fix
- **`src-winui/SimpleFile.PluginHost/PluginManager.cs`**
  - `ActivateAsync` gains `bool detachActivation = false`. The create, UI-thread activation and LoadFailed mapping moved unchanged into a new private `ActivateInstanceAsync(record, runtime, ct)`.
  - With `detachActivation: true`, the manager starts `ActivateInstanceAsync(record, runtime, CancellationToken.None)` and awaits it with `.WaitAsync(ct)`. The caller's token only stops that caller's wait, and the plugin's `ActivateAsync` never sees it. If the wait is cancelled, a continuation (`OnlyOnFaulted`) observes the orphaned task's exception. The task has already recorded any failure through `MarkLoadFailedAsync`, so the exception is not unobserved.
  - `GetRowDecorationsAsync` is the only caller that passes `detachActivation: true`. It keeps the rule that a cancel returns null without a log line. Commands, sidebar sections, settings pages and panels behave as before.
  - **Why `CancellationToken.None` and not the manager's `_disposeCts.Token`:** on dispose, `PluginRuntime.RetireAsync` already waits up to `RetireTimeout` for a running activation and then deactivates the stored instance. With the dispose token, a dispose would cancel the activation and abandon a half-activated instance, which is the leak this fix removes.
  - The `GetRowDecorationsAsync` doc summary now states the rule.
- **`docs/superpowers/specs/2026-11-07-plugin-system-design.md`**: the §7 decoration bullet now states that the caller's token never reaches `IPlugin.ActivateAsync` on this path, and what follows from that.
- **`src-winui/SimpleFile.Tests/PluginManagerTests.cs`**:
  - `UiPlugin` gains an optional `Activate` hook (`Func<CancellationToken, Task>`), which its `ActivateAsync` awaits with the token it receives.
  - New `GetRowDecorations_CancelledDuringActivation_ActivatesOneInstanceOnce`. A plugin's `ActivateAsync` awaits a gate with `ct`. The first call is cancelled mid-activation and returns null. A second call (uncancelled) is started, and then the gate opens. It asserts:
    - exactly one instance was created, with one activation;
    - the second call gets the provider's result;
    - the plugin stays Enabled, and no `[warn]` or `[error]` line is logged;
    - `DisableAsync` deactivates that one instance (`["deactivated"]`), so nothing leaked.
  - New `GetRowDecorations_CancelledDuringAnActivationThatLaterFails_StillMarksLoadFailed`. The caller cancels mid-activation and gets null, and the plugin is still Enabled. The activation then throws `boom`, and the plugin becomes LoadFailed with error `boom`. That is the existing convention: the record stores the raw activation message.

### TDD evidence
RED, with the tests and the `Activate` hook added and no production change:
```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~GetRowDecorations_Cancelled"
  Failed SimpleFile.Tests.PluginManagerTests.GetRowDecorations_CancelledDuringActivation_ActivatesOneInstanceOnce [25 ms]
   Assert.Single() Failure: The collection contained 2 items
  Failed SimpleFile.Tests.PluginManagerTests.GetRowDecorations_CancelledDuringAnActivationThatLaterFails_StillMarksLoadFailed [5 s]
   Assert.Equal() Failure: Values differ
Expected: LoadFailed
Actual:   Enabled
Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3
```
- The first failure is the reported bug: a second instance was created. The second shows that the cancelled activation never finished, so its failure was never recorded.
- After the fix, the second test's error assertion first expected the wrapped `PluginLoadException` text. The record stores the raw activation message (`boom`), as for every other activation failure, so the expectation was corrected. The RED failure above comes before that assertion.

GREEN:
```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManagerTests"
Passed!  - Failed:     0, Passed:    95, Skipped:     0, Total:    95, Duration: 17 s
```
The 7 `GetRowDecorations*` tests also passed three times in a row (`--no-build`), in about 150 ms per run.

### Suites
- `npm run build:winui`: exit 0, `0 Warning(s)`, `0 Error(s)`.
- `npm run check:winui`: `Passed!  - Failed: 0, Passed: 1250, Skipped: 0, Total: 1250`.
- `npm run check`: exit 0. The parity gate lists 86 commands, and no IPC method changed.
- No project, package or lock file changed. `Cargo.lock` was already modified before this task and was not touched. No Rust code changed.
- The install-folder requirement is unaffected: this fix adds no file, path or storage location.
