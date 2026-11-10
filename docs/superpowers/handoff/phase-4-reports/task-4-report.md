# Phase 4 Task 4 report: plugin host concurrency, toolbar offers and a warm first render

Status: DONE_WITH_CONCERNS. The concerns are at the end, with one deviation from a verbatim plan value: the warm-up priority. Nothing is committed; all work is in the working tree.

The user's requirement, "I do not want plugins stored outside of the program installation folder", is not touched by this task. Nothing here reads, writes or moves a plugin file. The warm-up builds UI objects in memory only, and the settings changes stay in the settings database, which is not a plugin file.

## What was implemented, per file

### `src-winui/SimpleFile.PluginHost/PluginManager.cs` (carry-overs #63, #65, #69)
- `MarkLoadFailedAsync` no longer awaits the teardown. Under `_gate` it:
  - sets LoadFailed;
  - detaches the runtime and bumps the epoch;
  - starts the teardown (`StopRuntimeAsync`: windows, backend, deactivation) through `TrackLoadFailureTeardownLocked`.

  It then logs `[error] <message>`, raises `Changed` at once and returns a completed task. So `LoadFailedAsync` hands the `PluginLoadException` back at once.
- `_loadFailureTeardowns`: a `Dictionary<string, Task>` keyed by plugin id, read and written under `_gate`.
  - `TrackLoadFailureTeardownLocked` runs the teardown on the thread pool (`ForceYielding` first), after any earlier teardown of the same id, so a second failure chains onto the first.
  - A failure inside the teardown is logged as `[warn] Plugin teardown after its load failure failed: …`.
  - A `ContinueWith` removes the entry when its task ends, but only if the entry is still that task.
- `WaitForLoadFailureTeardownAsync(string? pluginId)` waits for one id, or for all ids when it is null.
  - When nothing is tracked it returns `Task.CompletedTask` with no `await`, so discovery's startup path gains no yield.
  - The bound is `internal TimeSpan LoadFailureTeardownWait`. It defaults to `WindowCloseTimeout + PluginRuntime.RetireTimeouts` (5 s + 10 s) and test code can set it.
  - Past the bound it logs `[warn] Plugin '<id>' is still tearing down after its load failure; continuing.` for each unfinished id.
- `ScanAsync` (so `DiscoverAsync` and `RescanAsync`; it passes null), `EnableAsync`, `DisableAsync` and `RestartBackendAsync` (each passes its id) call it as the first statement after `_ops.WaitAsync`. That is before they create a runtime, retire one or start a backend.
- `DisposeAsync` calls `WaitForLoadFailureTeardownAsync(null)` after its own records' teardown.

### `src-winui/SimpleFile.PluginHost/PluginRuntime.cs`
- `internal static TimeSpan RetireTimeouts` is `RetireTimeout + RetireTimeout`: the activation wait plus the deactivation wait in `RetireAsync`.

### `src-winui/SimpleFile.Core/PluginBodyLoadQueue.cs` (new)
- `public sealed class PluginBodyLoadQueue(Action<Action> enqueue)` exposes `Request(key, load)`, `Forget(key)` and `IsPending(key)`.
- `Request` returns false when the key is already pending. It never runs `load` inline.
- Each request gets a ticket. The queued callback runs only if its ticket is still the key's, so a forgotten load never runs, even after a later request for the same key.
- The callback clears the pending mark before it starts `load`. A synchronous throw from `load` is turned into a faulted task and observed, so it never reaches the dispatcher.

### `src-winui/SimpleFile.Core/PluginSidebarLayout.cs`
- `BodyLoadKey(section)` is `"sidebar:" + Key(section)`.

### `src-winui/SimpleFile.Core/CommandSurfaceLayout.cs` (the toolbar rule; carry-over f)
- `OfferedPrimaryItems` is a `List<string>`, serialized as `offeredPrimaryItems`. `Normalize` drops empty ids, deduplicates it and handles a null value from JSON. It never filters by the catalog.
- `IsPluginItemId(string?)` checks `plugin:<id>:<command>` by hand, without a regex, to keep startup cost down:
  - the id is 2-64 characters, `[a-z0-9]` first, then `[a-z0-9.-]`;
  - the command is 1-128 characters with no whitespace.
- `Normalize` keeps a command item whose id the catalog knows or that `IsPluginItemId` accepts. Unknown core ids are still dropped.
- `VisibleItems()` calls `Normalize`, then returns only the catalog-known items, with no leading, trailing or doubled separators. `VisiblePrimaryActionIds()` and `Signature()` are built on it, and so is the overflow planner, through `VisiblePrimaryActionIds()`.
- `EnsurePrimaryItem(id)` behaves as follows:
  - it returns false, and changes nothing, when the id is blank or already offered;
  - otherwise it records the id as offered, inserts it before Settings (or at the end) when absent, and returns true;
  - an id that is already present but was never offered is recorded and returns true, so the caller saves the offer.
- `UpgradeLegacyDefaultToolbar` matches only `previousDefaultWithoutFtp`; the dead `previousDefault` is removed.

### `src-winui/SimpleFile.Core/WorkspaceSettingsStore.cs` (#78/#84)
- `SaveAsync` does all of its work before the first await:
  - it normalizes every setting, including `ShortcutOverrides`, `CommandSurface` and `CollapsedPluginSidebarSections`;
  - it builds a `(Key, Value)[]` of all 43 writes, in the same order and with the same keys and value formats as before. That includes the JSON of `CommandSurface`, `FolderViewSettings`, the shortcut overrides, the section keys, the bookmarks and the recents.

  Only then does it await the writes in a loop. It assigns no list property of `settings` after an await.

### `src-winui/SimpleFile.App/MainWindow.PluginSidebar.cs` (#82, #78/#84)
- `_pluginBodyLoads` is a `PluginBodyLoadQueue` over `DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, …)`. It is built in the field initializer from `DispatcherQueue.GetForCurrentThread()`, because the window is constructed on the UI thread.
- `ActivateVisiblePluginSections` only calls `_pluginBodyLoads.Request(PluginSidebarLayout.BodyLoadKey(view.Section), () => LoadPluginSectionBodyAsync(view))`.
- `RemovePluginSection` forgets the section's waiting load.
- `LoadPluginSectionBodyAsync` returns at once when the section was removed before the queued load ran.
- `TogglePluginSection` replaces `CollapsedPluginSidebarSections` copy-on-write: `[.. old, key]` or `[.. old.Where(k => k != key)]`.
- `QueuePluginSectionActivation`: only its doc comment changed. It still sets the ready flag in a low-priority callback; the loads are then queued again.

### `src-winui/SimpleFile.App/PluginDialogs.cs` and `PluginWorkspaceView.cs` (#82)
- New `PluginUiDispatch.EnqueueAsync<T>`, which always queues, even with thread access. `RunAsync` now delegates to it when off-thread.
- `PluginWorkspaceView.NavigateAsync` uses `EnqueueAsync`.

### `src-winui/SimpleFile.App/MainWindow.CommandSurface.cs`
- `ApplyCommandSurfaceLayout` iterates `layout.VisibleItems()`.

### `src-winui/SimpleFile.App/SettingsWindow.CommandSurface.cs` and `SettingsWindow.SettingsState.cs`
- `ToolbarEditorRow`: an item the catalog does not know but `IsPluginItemId` accepts shows its raw id as the label, with the detail `Plugin not loaded`.
- New fields `_toolbarOfferedItems` and `_toolbarOffersReset`.
  - `ApplyCommandSurfaceLayout(layout)` loads the offers.
  - `CurrentCommandSurfaceLayout` and `CloneCommandSurfaceLayout` assign `OfferedPrimaryItems`.
  - Reset applies the default layout, which has no offers, and sets `_toolbarOffersReset`.
  - `ApplySettingsSnapshot` clears that flag.
- `ApplyTo` merges the live layout's offers (`settings.CommandSurface.OfferedPrimaryItems`) into the new layout, unless Reset was pressed. So an offer a plugin made while Settings was open survives a save.

### `src-winui/SimpleFile.App/MainWindow.xaml.cs` and `MainWindow.Plugins.cs` (the (h) lever)
- `ConnectAsync` calls `DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => WarmPluginRendering())` right after `PreparePluginScan()`, before the first await. See Deviation 1.
- An alias `using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;` is added, because the file also imports `Windows.System`.
- `WarmPluginRendering()` runs `PluginContributionAdapter.Commands`, `ContextMenus`, `ToolbarActions`, `PrimaryToolbarCommandIds` and `SidebarSections` on an empty array. It then builds and drops one `CreatePluginSectionView` of a placeholder section that has a command button. It touches no catalog, no plugin state and not `PluginSectionsHost`, and any exception only goes to `Debug.WriteLine`.

### Tests
- `PluginManagerTests.cs`:
  - New tests: `LoadFailure_RaisesChangedBeforeTheTeardownFinishes`, `CreateSidebarSection_ProviderThrows_ThrowsBeforeTheTeardownFinishes`, `Dispose_WaitsForALoadFailureTeardown`, `Rescan_AfterALoadFailure_WaitsForItsTeardown`, `Disable_AfterALoadFailure_ReturnsOnlyAfterItsTeardown`, `Enable_AfterALoadFailure_WaitsForItsTeardown` and `LoadFailureTeardownWait_IsBoundedAndLogged`.
  - Their shared setup is the helper `LoadFailedBehindAGate`: a `RecordingWindowHost` whose close waits for a test-held gate, and `pipePerStart: true` so a restart never reuses a pipe.
  - Carry-over (g): `ArchivePlugin_SuccessfulRegistration_LogsTheRegisteredFormats` now waits, bounded at 10 s, for the log line `[info] Archive engines registered: .hello` itself.
  - `CreateSidebarSection_ProviderThrows_MarksLoadFailedAndWithdrawsContributions` now waits for the teardown order, because the teardown runs after the throw.
- New `PluginBodyLoadQueueTests.cs`: the four named tests.
- `PluginSidebarLayoutTests.cs`: `BodyLoadKey_CarriesTheSidebarSlot`.
- `CommandSurfaceLayoutTests.cs`:
  - `EnsurePrimaryItem_InsertsBeforeSettingsOnce` became `EnsurePrimaryItem_FirstOffer_InsertsBeforeSettings`.
  - New tests: `EnsurePrimaryItem_AfterTheUserRemovedIt_DoesNotAddItAgain`, `OfferedPrimaryItems_SurviveNormalizeAndJsonWhileThePluginIsAbsent`, `PluginToolbarItem_SurvivesASaveWhileThePluginIsDisabled`, `PluginToolbarItem_RemovedByTheUser_StaysRemovedAcrossDisableAndEnable`, `Normalize_DropsUnknownCoreIdsButKeepsWellFormedPluginIds` (see Deviation 2), `VisibleItems_CollapseTheSeparatorsAroundHiddenItems` and `FromJson_PreviousDefaultWithTheCoreFtpCommand_UpgradesToTheCurrentDefault`.
  - Every catalog-touching test restores `SetPluginActions([])` in `finally`.
- `WorkspaceSettingsStoreTests.cs`: `SaveAsync_ListsChangedDuringTheSave_SaveTheCallTimeValuesAndKeepTheChange`, with a fake `RacingSettingsBackend` whose first `SetSettingAsync` makes the copy-on-write toggle and calls `EnsurePrimaryItem("plugin:a:b")`.
- `WinUiSourceShapeTests.cs`: `ActivateVisiblePluginSections_OnlyQueuesBodyLoads`, `PluginWorkspaceView_NavigateAsync_AlwaysQueues`, `TogglePluginSection_ReplacesTheCollapsedListInsteadOfMutatingIt`, `ConnectAsync_WarmsPluginRenderingBeforeAwaitingTheService` (matches `TryEnqueue(DispatcherQueuePriority.Normal,`; see Deviation 1), `PrimaryToolbar_RendersOnlyTheVisibleItems` and `SettingsToolbarEditor_CarriesTheOfferedItems`.

### Docs
- Spec §5.2: the warm-up and its measurements table (baseline, `Low`, `Normal`), and the pending hosted-runner evidence.
- Spec §7: the toolbar rule, the queued body loads, `EnqueueAsync`, and the load-failure teardown rule.
- Review notes, new section "Phase 4 closures":
  - #63, with all four operations, dispose and the bound, and their tests;
  - #65/#69, #78/#84, and #82 for sidebar bodies and `NavigateAsync`, with decorations and panels left to Tasks 9-11;
  - the parked `EnsurePrimaryItem` item and the toolbar rule;
  - (f), the dead `previousDefault`; (g); and (h), with the local medians. It says plainly that the hosted-runner evidence (74.2 ms at `46860dc`) is still pending.

## TDD evidence

RED 1 (build), after writing the tests:
`dotnet build src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug` failed with 20 errors, as the plan expected:
- `CS1061 'CommandSurfaceLayout' does not contain a definition for 'OfferedPrimaryItems'` and `… 'VisibleItems'`;
- `CS0117 … 'IsPluginItemId'`;
- `CS0246 The type or namespace name 'PluginBodyLoadQueue' could not be found`;
- `CS1061 'PluginManager' does not contain a definition for 'LoadFailureTeardownWait'`;
- `CS0117 'PluginSidebarLayout' does not contain a definition for 'BodyLoadKey'`.

RED 2, with compile-only stubs, running the Step 2 filter:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManagerTests|FullyQualifiedName~PluginBodyLoadQueueTests|FullyQualifiedName~PluginSidebarLayoutTests|FullyQualifiedName~CommandSurfaceLayoutTests|FullyQualifiedName~WorkspaceSettingsStoreTests|FullyQualifiedName~WinUiSourceShapeTests"`
`Failed!  - Failed: 25, Passed: 131, Total: 156`. The failures:
- all 7 new manager tests;
- 3 of the 4 queue tests;
- `BodyLoadKey_CarriesTheSidebarSlot`;
- 7 toolbar tests;
- the save test;
- all 6 source-shape tests.

Two new tests passed against the stubs:
- `Request_AfterTheLoadStarted_QueuesAgain`, because the stub queued every request;
- `FromJson_PreviousDefaultWithTheCoreFtpCommand_UpgradesToTheCurrentDefault`, because that behaviour already worked; the change there only removes dead code.

GREEN, the same filter after the implementation: `Passed!  - Failed: 0, Passed: 156, Skipped: 0, Total: 156`. It was repeated 3 times with `--no-build` over the manager, queue, toolbar and store tests, and gave `Passed: 100` each time, with no flakes.

## Full-suite results
- `dotnet build src-winui/SimpleFile.sln -c Debug`: `0 Warning(s)`, `0 Error(s)`.
- `npm run check:winui`: `Passed!  - Failed: 0, Passed: 1185, Skipped: 0, Total: 1185`. It was run after the final edit as well.
- `npm run check`: exit 0. The parity gate prints `WinUI parity gate lists 86 commands`.
- `npm run test:maintenance`: 66 passed, 0 failed.
- `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -PluginsDir target\fixture-plugins\com.sumafile.hello` printed `UI runtime smoke passed`. The Debug build used had been rebuilt with the final code.
- `npm run check:winui-startup-budget` printed:
  - `plugins-discovered span 10.0 ms (budget 50 ms, count=3): plugins-begin 1.6 ms, plugins-manager-built 1.3 ms, plugins-scan-awaited 0.0 ms, plugins-evaluated 1.9 ms, plugins-discovered 5.2 ms.`
  - `WinUI startup budget passed: ready in 300.9 ms`
- No Rust, IPC, schema or package change, so cargo, the lock files and the NuGet restores are untouched.

## Warm-up measurement (Step 4)
The harness lives outside the repo: `R:\Temp\claude\c48dfaf19294613c\4e549058-5b59-447a-927b-0fc453a48aa7\scratchpad\measure-warmup.ps1`. It is phase 3's `measure-startup.ps1` plus `SUMAFILE_PLUGIN_LOG_PATH` in the disposable folder, so `plugins.log` is not written into the Debug exe folder.

Each row is five starts in the `-PluginsDir` smoke environment: 7-Zip, remote and Hello (count=3), on 32 CPUs.

| Build | Spans | Median |
| --- | --- | --- |
| Baseline, taken before Step 3 on a fresh build | 15.0, 16.7, 15.0, 15.1, 15.0 | **15.0 ms** |
| Warm-up at `Low`, as the plan wrote it | 16.4, 15.9, 15.3, 17.4, 15.2 | **15.9 ms** (no better) |
| Warm-up at `Normal` (final code) | 9.7, 10.3, 10.6, 10.4, 10.6 | **10.4 ms** |

With the final code, the `plugins-discovered` stage fell from about 10.8 to about 5.5 ms, `backend-started` rose from about 80 to about 86-92 ms, and `ready` stayed at about 295-317 ms.

A temporary `StartupTimer("TMPWARM")` mark, since removed, showed:
- at `Low`, the warm-up ran about 300 ms after `plugins-discovered`, after `ready`;
- at `Normal`, it ran about 13 ms into `ConnectAsync`, well before `backend-started`.

## Deviations
1. **Warm-up priority `Normal` instead of `Low`.**
   - The plan's verbatim call is `TryEnqueue(DispatcherQueuePriority.Low, () => WarmPluginRendering())`, and its rule is to remove the warm-up when its median is higher than the baseline.
   - At `Low` the median was higher (15.9 against 15.0 ms), because the warm-up only ran after `ready`, so as written it can never help.
   - The smallest correct change that keeps the lever is to queue it at `Normal` priority, before the first await. It then runs while the service starts, and at the same priority it runs ahead of the `backend-started` continuation, which is queued after it. That gives a median of 10.4 ms.
   - The cost is about 13 ms of UI-thread work during the service start, before the window has any workspace. `backend-started` moves about 6-10 ms later.
   - `ConnectAsync_WarmsPluginRenderingBeforeAwaitingTheService` matches `TryEnqueue(DispatcherQueuePriority.Normal,` instead of `…Low,`.
   - If the reviewer prefers the plan's rule, remove the call, `WarmPluginRendering` and that test. Spec §5.2 and the review notes record all three medians either way.
2. **`Normalize_DropsUnknownCoreIdsButKeepsWellFormedPluginIds` does not list `git-panel` among the dropped ids.** `git-panel` is still a core command (`AppCommandCatalog.BuiltIn`) until Task 15 removes it, so `Normalize` rightly keeps it today. The test uses the retired `ftp-sftp-manager` as its unknown core id instead, and adds `plugin:com.a:` and `plugin:com.a:has space`. **Task 15 should add `git-panel` to this test.**
3. **`EnsurePrimaryItem` returns true for an id that is already in the toolbar but was never offered.** The interface text says it inserts "when absent". It records the offer and returns true for this case, so the caller saves the offer. This happens, for example, for a toolbar saved before offers existed.
4. **`ApplyTo` merges the live layout's offers unless Reset was pressed** (`_toolbarOffersReset`). This is one reading of "ApplyTo carries the current layout's offers into the new one" that still lets Reset clear them.
5. **`WarmPluginRendering` runs five adapter calls, not four.** It adds `PrimaryToolbarCommandIds` to the four the plan names; the render calls it too.

## Concerns
- **Hosted-runner proof for (h) is pending.** No CI was run. All figures are local, on a Ryzen 9 9950X.
- **Timing-based tests.** The manager tests assert "not completed after 300 ms" and "completed within 2 s". On a very slow agent the 2 s bounds are the risk. All of them passed in 4 consecutive runs locally.
- **A swallowed exception.** `PluginBodyLoadQueue` swallows a fault of the load task. `LoadPluginSectionBodyAsync` already catches and reports every exception, so nothing is lost today. A future load that does not handle its own errors would fail silently.
- **The waits can add up.** `WaitForLoadFailureTeardownAsync` runs under `_ops`, so a Rescan right after a load failure whose windows never close can now take up to 15 s (`LoadFailureTeardownWait`) before it proceeds. That is by design, and it is bounded and logged.
- **A broken path in the spec, from an earlier task.** Spec §7, in the "Plugin locations" bullet, contains `<data root>\<id>` followed by a literal TAB character and then `mp`, where `\tmp` was meant. The text came from an earlier task, and this task did not edit it.

## Fix round 1

### Finding: a plugin enabled while Settings is open could lose its toolbar item for good (Important)
Here is how it happened:
1. While Settings was open, `ApplyPluginToolbarPlacements` inserted the item into the live layout and recorded the offer.
2. The editor's rows were still the snapshot taken when Settings opened.
3. On Save, `ApplyTo` replaced the toolbar with those rows and merged the live offers.

The item was then gone but marked as offered, so `EnsurePrimaryItem` never added it again.

What changed:
- **`src-winui/SimpleFile.Core/CommandSurfaceLayout.cs`**:
  - New `MergeOffersFrom(CommandSurfaceLayout live, CommandSurfaceLayout baseline)`. It does three things:
    - it unions the live offers into the edited layout, as before;
    - it carries the item of each offer that is in `live` but not in `baseline`, meaning an offer made while Settings was open. The item goes before Settings, or at the end, but only when the live toolbar has it and neither the baseline nor the edited rows do;
    - it then normalizes.
  - So the edited rows still win for anything the user saw: an item the user removed in the editor stays removed, including a pre-offers item that was already in the toolbar and whose offer arrived later.
  - The insert-before-Settings code moved into a private `InsertBeforeSettings`, which `EnsurePrimaryItem` also uses.
- **`src-winui/SimpleFile.App/SettingsWindow.CommandSurface.cs`**: new field `_toolbarBaseline`. `ApplyCommandSurfaceLayout` sets it to `CloneCommandSurfaceLayout(layout)`.
- **`src-winui/SimpleFile.App/SettingsWindow.SettingsState.cs`**: `ApplyTo` calls `surface.MergeOffersFrom(current, _toolbarBaseline)` in place of the plain union. Reset still drops every offer, and the enabled plugins then offer their items again.
- The `HasUnsavedChanges` probe is unaffected. It runs `ApplyTo` against the loaded settings JSON, whose layout equals the baseline, so the merge adds nothing there.

### Tests (test-first)
- New Core tests in `CommandSurfaceLayoutTests.cs`:
  - `MergeOffersFrom_AnOfferMadeWhileEditing_KeepsItsItem` is the finding's scenario. After the merge the edited layout equals the live order, keeps the edited display mode, and records the offer. After a JSON round trip, `EnsurePrimaryItem` returns false and the item is there once.
  - `MergeOffersFrom_AnOfferMadeWhileEditing_GoesBeforeTheEditedSettingsItem`;
  - `MergeOffersFrom_AnItemTheUserRemovedInTheEditor_StaysRemoved`;
  - `MergeOffersFrom_AnItemShownInTheEditorAndRemovedThere_StaysRemovedWhenItsOfferArrivesLater`;
  - `MergeOffersFrom_KeepsTheEditedOrderAndAddsNothingTwice`, where the user added the item by hand, plus a new offer whose item the live toolbar no longer has.
- `WinUiSourceShapeTests.SettingsToolbarEditor_CarriesTheOfferedItems` now also checks three things:
  - `ApplyCommandSurfaceLayout` records `_toolbarBaseline = CloneCommandSurfaceLayout(`;
  - `ApplyTo` calls `.MergeOffersFrom(current, _toolbarBaseline)`;
  - `ApplyTo` no longer contains the plain `.. current.OfferedPrimaryItems` union.

RED 1, after writing the tests: `dotnet build src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug` failed with `5 Error(s)`, all `CS1061 'CommandSurfaceLayout' does not contain a definition for 'MergeOffersFrom'`.

RED 2, behavioural: a stub `MergeOffersFrom` that only did the old union, the behaviour `ApplyTo` had before this fix.
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~CommandSurfaceLayoutTests.MergeOffersFrom"` printed:
```
  Failed SimpleFile.Tests.CommandSurfaceLayoutTests.MergeOffersFrom_AnOfferMadeWhileEditing_KeepsItsItem [16 ms]
  Failed SimpleFile.Tests.CommandSurfaceLayoutTests.MergeOffersFrom_AnOfferMadeWhileEditing_GoesBeforeTheEditedSettingsItem [< 1 ms]
Failed!  - Failed:     2, Passed:     3, Skipped:     0, Total:     5
```
Both tests that pin the bug failed. The three guard tests, which check that removals stay final and nothing is added twice, passed against the old behaviour, as they should.

RED 3, the App wiring, after the Core implementation: the filter `...MergeOffersFrom|FullyQualifiedName~SettingsToolbarEditor_CarriesTheOfferedItems` printed `Failed SimpleFile.Tests.WinUiSourceShapeTests.SettingsToolbarEditor_CarriesTheOfferedItems` (`Assert.Contains() Failure: Sub-string not found`) and `Failed: 1, Passed: 5`.

GREEN:
- `dotnet build src-winui/SimpleFile.sln -c Debug`: `0 Warning(s)`, `0 Error(s)`.
- `dotnet test ... --no-build --filter "FullyQualifiedName~CommandSurfaceLayoutTests|FullyQualifiedName~WinUiSourceShapeTests|FullyQualifiedName~WorkspaceSettingsStoreTests"`: `Passed!  - Failed: 0, Passed: 74, Skipped: 0, Total: 74`.
- `npm run check:winui`: `Passed!  - Failed: 0, Passed: 1190, Skipped: 0, Total: 1190`. That is 1185 plus the 5 new tests.
- `npm run check`: exit 0, printing `WinUI parity gate lists 86 commands, 57 context ids, 83 palette ids.`

There was no Rust, IPC, schema, package or lock-file change. Nothing here reads, writes or moves a plugin file, so the user's requirement that plugins stay inside the program installation folder is untouched.

Not re-run in this round: the UI smoke and the startup budget. The change touches only the Settings save path, which neither of them exercises.
