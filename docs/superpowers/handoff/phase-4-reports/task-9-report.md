# Task 9 report: Core: per-folder decorations within 250 ms, the status column, and panel layout rules

Status: DONE_WITH_CONCERNS (all tests green; the concerns are small and listed below). Nothing committed; every change is in the working tree. Base snapshot before this task: `fb4af2dcc6398b0ad4d967b9317b47f667e189c5`. No project, package or lock file changed. No plugin file location changed: this task adds no file I/O. The two new panel sizes go to the settings database, like the other UI settings.

## What changed, per file

### Created (Core)
- `src-winui/SimpleFile.Core/RowDecorations.cs`
  - `RowDecorationEntry`, `FolderDecorationBatch` and `IRowDecorationSource`, as in the plan's Interfaces block.
  - `RowDecorationKeys.Normalize`: `/` becomes `\`; the trailing separator is dropped unless the path is a drive root (`R:/` gives `R:\`); a path made only of separators gives `\`. `RowDecorationKeys.Comparer` is a `StringComparer` (Equals, GetHashCode and Compare) that applies `OrdinalIgnoreCase` to the normalized form.
  - `PaneDecorations`, which is immutable:
    - `Empty`, `FolderPath`, `FolderFlags`, `For(path)` (the first plugin in order wins) and `PluginsFor(paths)` (every decorating plugin).
    - `Merge(folderPath, pluginOrder, batches)`. It walks `pluginOrder`, so a batch from a plugin outside the order is ignored, and it stamps each entry's `PluginId` with the plugin that answered.
    - Internal `IsComplete`: false for a result the budget cut short. Internal `HasNoDecorations`.
- `src-winui/SimpleFile.Core/RowDecorationCoordinator.cs`
  - `DefaultBudget` is 250 ms. Also `Log`, `SetSource`, `HasProviders`, `MarkStartupReady`, `IsStartupReady`, `Generation` and `FirstRequestAt`.
  - `SetSource` returns true, and bumps `Generation`, only when the ordered, deduplicated id list changes. `MarkStartupReady` always bumps it.
  - `DecorateAsync(folderPath, isNetwork, paths, onLate, ct)`:
    - It returns `Empty` at once, calling nothing, in these cases: before ready, with no source or no ids, for a network folder, for a blank path, and for `PathRules.IsRecycleBinPath`.
    - Otherwise it starts each plugin's call as `Task.Run(() => source.DecorateAsync(pluginId, folderPath, requested, ct))` and waits up to the budget in total. The wait is `Task.Delay` on a linked token, cancelled afterwards.
    - It returns the merge of what answered. When something was pending, it returns that merge with `IsComplete = false`, and `onLate` gets the merge of all exactly once. This happens only when at least one late batch is non-null and `ct` was not cancelled.
  - `SettleAsync` never faults:
    - a throw is logged as `[warn] Row decorations from <id> failed: <message>`;
    - a null result is logged as `… failed: the source returned no result.`;
    - an `OperationCanceledException` while `ct` is cancelled, or a null result after `ct` was cancelled, is skipped silently.
  - `Sanitize` keeps only the flags `<requested pluginId>:<name>` where the name matches `^[A-Za-z][A-Za-z0-9_-]{0,63}\z`. The regex is generated and uses `\z`, so a trailing newline cannot slip through as it could with `$`.
  - A throwing `onLate` or `Log` callback is caught. The first is logged; the second is ignored.
- `src-winui/SimpleFile.Core/DecorationBrushKeys.cs`: the seven allowed keys (ordinal), `Normalize` (unknown or blank gives `SfTextMutedBrush`), and a `Fallback` const.
- `src-winui/SimpleFile.Core/PluginPanelLayout.cs`
  - Constants, verbatim from the plan.
  - `Key`, `BodyLoadKey` (`panel:` + Key), `StateKey`, `StillWanted` and `ForCommand` (ordinal).
  - `ClampBottomHeight` and `ClampRightWidth` clamp to `[min, max(min, fraction*available)]`. A requested size that is not finite gives the default. An `available` that is not finite or is <= 0 (not laid out yet) enforces only the minimum. See Deviations.

### Modified (Core)
- `ExplorerPane.cs`:
  - `public PaneDecorations Decorations { get; set; } = PaneDecorations.Empty;`
  - `internal long AppliedDecorationRequest`, and `internal bool AppliedDecorationLate`.
  - `internal long NextDecorationRequest()`. It is called under the gate, and it cancels the previous request's CTS through `CancelAsync`, so the callbacks never run under the gate.
  - `internal CancellationToken DecorationCancellation`.
  - `internal string? DecorationTextFor(path)`.
  - `VisibleEntries` and `VisibleViewportEntries` pass `DecorationTextFor` to the sort.
  - `SwapContents` swaps `Decorations` with the rest of the contents.
- `ExplorerWorkspace.cs`:
  - The constructor gains an optional `RowDecorationCoordinator? rowDecorations = null`, which tests use for short budgets.
  - New `public RowDecorationCoordinator RowDecorations { get; }`.
  - The file is 166 lines, within the 200-line source-shape limit.
- `ExplorerWorkspace.Navigation.cs`:
  - `public Task InvalidateDecorationsAsync()`: the primary pane, plus the secondary one when dual pane is on.
  - `internal Task InvalidateDecorationsAsync(PaneId)`:
    - it skips a pane with `ListingInProgress` or no path;
    - under the gate it captures the path, network flag, row paths, navigation token, a new request number and its token;
    - it applies the immediate result only when `IsComplete` (see Deviations), and the late merge through `ApplyLateDecorations`.
  - `internal void ApplyLateDecorations(...)` and the private `ApplyDecorations(..., late)`:
    - a result applies only while the `NavigationToken` and the path (`PathsEqual`) are unchanged, and `ShouldApplyDecorationsLocked` holds: `request > applied`, or `request == applied && late && !appliedLate`;
    - it then sets the request, the late flag and the decorations, bumps `ContentVersion` and raises `Changed`;
    - replacing empty with empty raises nothing.
  - `internal void StartDecorationRefresh(PaneId)` runs `InvalidateDecorationsAsync(pane)` fire-and-forget and logs a fault through `RowDecorations.Log`.
  - `PaneFor` resolves the pane itself, never normalized, so a secondary result never lands on the primary pane.
- `WorkspaceControllers/WorkspaceNavigationController.cs`:
  - `NavigatePaneAsync`:
    - Its start resets `state.Decorations` to `Empty`, so the old folder's flags never show on the new one.
    - After `navigation.Listing`, it reads `Generation` and runs `DecorateListingAsync`. That helper re-checks the token under the gate, takes the request number and token, computes `isNetwork` under the gate, and awaits `DecorateAsync` with an `onLate` that calls `ApplyLateDecorations(target, token, request, listing.Path, late)`.
    - The publish lock now re-checks `token != state.NavigationToken`, because the code awaited. `PublishDecorationsLocked` keeps the result unless that request's late merge has already landed, then bumps `ContentVersion`.
    - The `finally` block, after it clears `ListingInProgress`, calls `RedecorateIfGenerationChanged`. That starts `StartDecorationRefresh(target)` when the listing was published and `Generation` changed.
  - `RefreshAsync` gets the same hook, before its lock and in its `finally`.
  - The progressive chunks (`PaneNavigator`) are untouched, so they never wait.
- `EntryPresentation.cs`:
  - `ContentSecondaryText(parentText, decorationText, symlinkText, extensionText, includeParent)` shows the decoration text trimmed, with no `Git:` prefix.
  - `SortEntries`, `SortEntriesInPlace`, `VisibleEntries` and `VisibleViewportEntries` gain an optional `Func<string, string?>? decorationText`.
  - The `ColumnLayout.DecorationColumnId` (`"git"`) sort compares `decorationText(path) ?? ""`.
- `ColumnLayout.cs`:
  - `public const string DecorationColumnId = "git"`, and the column's header is "Status".
  - `EffectiveForPaneWidth(paneWidth, dualPane, bool decorationsAvailable)`.
  - The compact widths use the constant.
- `UiSettings.cs`:
  - `PluginPanelBottomHeight` (320) and `PluginPanelRightWidth` (360).
  - `NormalizePluginPanelBottomHeight` and `NormalizePluginPanelRightWidth`: a non-finite value gives the default; otherwise the value is clamped to [min, `PluginPanelMaxStoredSize` = 10000].
- `WorkspaceSettingsStore.cs`: the keys `pluginPanel.bottomHeight` and `pluginPanel.rightWidth`, in the load batch, in the load (normalized), and in the save (normalized, invariant culture).

### Tests
- New `RowDecorationCoordinatorTests.cs`. It has the twelve named tests, with a 50 ms budget and a 2 s watchdog, plus the shared `FakeRowDecorationSource`. In `DecorateAsync_NeverRunsTheSourceOnTheCallingThread`, a dedicated caller thread calls `DecorateAsync` while the source blocks synchronously; a `ManualResetEventSlim` with the 2 s watchdog proves the call returned.
- New `DecorationBrushKeysTests.cs`: `Normalize_KnownKeysPass_UnknownKeysFallBackToMuted` (a theory), plus `Allowed_IsExactlyTheStableDecorationAndTextKeys`.
- New `PluginPanelLayoutTests.cs`: the five named tests. `BodyLoadKey_CarriesThePanelSlot` also checks that a panel and a sidebar section with the same plugin, id and epoch share `Key` but not their body-load keys.
- `ExplorerWorkspaceTests.cs`: the ten named tests, with a `DecoratedWorkspace` helper.
- `EntryPresentationTests.cs`: the existing secondary-text test now expects no prefix, and is renamed `…FormatsDecorationAndSymlink`. New `ContentSecondaryText_ShowsTheDecorationTextAsIs`.
- `ColumnLayoutTests.cs`: the named argument becomes `decorationsAvailable:`. New `StatusColumn_KeepsTheIdGitWithTheHeaderStatus`.
- `WorkspaceSettingsStoreTests.cs`: `PanelSizes_RoundTripAndClamp`. It covers the round trip, a low value clamping up, a non-number, a save clamping NaN and a too-small value, and missing keys.
- `WinUiSourceShapeTests.cs`: `RowDecorationCoordinator_StartsEverySourceCallOnTheThreadPool`. Every `.DecorateAsync(` in the coordinator must follow `Task.Run(() => <identifier>` on its line, and at least one must exist.

### Docs
- Spec §7, the decoration bullet. A new block covers each rule in the plan's docs list:
  - wait before publish, and the progressive chunks never wait;
  - thread pool only (#82);
  - the startup gate, `Generation` and re-decoration;
  - newer requests win;
  - decorations whatever the column layout;
  - skipped locations;
  - precedence and flag rules;
  - key matching;
  - the `git` column id with the "Status" header;
  - the brush allow-list.

  It also gives a short paragraph on the `PluginPanelLayout` rules and the two settings keys.

## TDD evidence

RED (tests written, no production code):
```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RowDecorationCoordinatorTests|FullyQualifiedName~ExplorerWorkspaceTests|FullyQualifiedName~DecorationBrushKeysTests|FullyQualifiedName~PluginPanelLayoutTests|FullyQualifiedName~EntryPresentationTests|FullyQualifiedName~ColumnLayoutTests|FullyQualifiedName~WorkspaceSettingsStoreTests|FullyQualifiedName~WinUiSourceShapeTests"
  error CS0246: The type or namespace name 'FolderDecorationBatch' could not be found (x4)
  error CS0246: The type or namespace name 'IRowDecorationSource' could not be found (x2)
  error CS0246: The type or namespace name 'RowDecorationCoordinator' could not be found (x1)
```
The build failed on the new types, as the plan expects.

First GREEN attempt, with the same filter: `Failed: 2, Passed: 181`. Both failures were in the tests:
- In `LateDecorations_ForAnOlderNavigation_AreDropped`, the fake's default batch for the Desktop folder carried the home folder's `notes.txt` key, so the final `For(DecoratedNotes)` was not null. The fix gives Desktop its own batch and asserts it.
- `DecorateAsync_NeverRunsTheSourceOnTheCallingThread` asserted `Assert.Same(PaneDecorations.Empty, …)`, but a budget-cut result is a separate incomplete instance (see Deviations). It now asserts no rows and no flags.

GREEN:
```
same filter -> Passed!  - Failed: 0, Passed: 183, Skipped: 0, Total: 183
```
Flakiness check: `RowDecorationCoordinatorTests|ExplorerWorkspaceTests` ran 5 times in a row, and each run gave `Passed: 78, Failed: 0`.

## Full-suite results
- `npm run build:winui` (the whole `SimpleFile.sln`, the App included): `0 Warning(s)`, `0 Error(s)`. The App's callers of `ContentSecondaryText` and `EffectiveForPaneWidth` pass positional arguments, so they compile unchanged and still pass `row.GitText` and `IsGitIntegrationEnabled` until Task 11.
- `npm run check:winui`: `Passed! - Failed: 0, Passed: 1296, Skipped: 0, Total: 1296`.
- `npm run check`: exit 0. The parity gate still lists 86 commands.
- No Rust code changed, so cargo was not run.

## Deviations
1. **An invalidation applies its immediate result only when it is complete.** The plan says `InvalidateDecorationsAsync` "applies its immediate result through `ApplyLateDecorations` and its late result the same way". Taken literally, a provider slower than 250 ms (a large repository's `git status` after a stage or commit) would replace the pane's decorations with a budget-cut result, often `Empty`. Badges, `inRepository`, the Git menu group and the toolbar toggle would then blink off until the late merge. Instead, the coordinator marks a budget-cut result `IsComplete = false`, and an invalidation keeps the shown decorations until the late merge. A navigation still publishes its budget-cut result, because a new folder must not keep the old folder's decorations, and the navigation start resets them to `Empty`.
2. **Results order by (request, late), not by `request >= applied` alone.** A request's late merge can land before its own immediate result is applied. This happens when the late batch arrives between the budget expiry and the publish or apply lock. `request >= applied` would then let the partial result overwrite the full one. `ShouldApplyDecorationsLocked` accepts `request > applied`, or the same request's late merge over its budget-cut result. The plan's "newer request wins, whatever order" holds unchanged.
3. **Superseded requests are cancelled.** The plan's hook passes the navigation's `ct` to `DecorateAsync`. The hook passes the pane's decoration token instead: `NextDecorationRequest` cancels the previous request through `CancelAsync`. Task 8's manager comment assumes that "the coordinator cancels every superseded navigation". The caller's navigation token was never cancelled for a superseded navigation, so with it a superseded `git status` would always run to the end. Linking the two tokens would leak a registration per navigation on the app-wide token, and that token is not needed: `DecorateAsync` never waits longer than the budget.
4. **The `PluginPanelLayout` clamps with an unknown `available`.** The plan's formula is `[min, max(min, fraction*available)]`. When `available` is 0, negative or not a number (the area is not laid out yet), the formula would shrink a saved size to the minimum. The clamp then enforces only the minimum. A requested size that is not finite gives the default.
5. **Panel sizes saved in settings** are clamped to [min, 10000] (`UiSettings.PluginPanelMaxStoredSize`). The plan gives no stored upper bound; the window-relative bound is applied at layout time by `PluginPanelLayout`.
6. **Extra additions** beyond the Interfaces block:
   - the internal `PaneDecorations.IsComplete` and `HasNoDecorations`;
   - the internal `ExplorerPane.AppliedDecorationLate`, `DecorationCancellation` and `DecorationTextFor`;
   - the internal `ExplorerWorkspace.StartDecorationRefresh` and `ShouldApplyDecorationsLocked`;
   - `DecorationBrushKeys.Fallback`;
   - the optional `rowDecorations` constructor parameter;
   - the optional `decorationText` parameter on the `EntryPresentation` sort functions.

   The coordinator also drops flags under another plugin's prefix, not only malformed names. The spec records this.

## Concerns
- **The sort reads only decorations.** From now until Task 11 and Task 13 deliver decorations, sorting by the status column in a real run sorts every row as undecorated. The column itself still shows `FileEntry.GitStatus` through `row.GitText`, and the secondary text shows it without the `Git:` prefix. Both follow the plan (the sort and the prefix are its spec), and the plan's Step 3 accepts this interim state.
- **A rare dropped late merge.** The late merge can arrive in the microseconds between the budget expiry and the publish lock. In that window the pane's `Path` is still the requested path, so `ApplyLateDecorations` matches it against `listing.Path` and drops the result when the backend returned a differently spelled path. The pane then shows the budget-cut result until the next sync. This needs both the race and a canonicalized path.
- **Re-enabling dual pane.** When dual pane is turned back on and the secondary pane keeps an earlier path (`ToggleDualPaneAsync` does not re-list), that pane keeps the decorations it had when hidden. The App's Task 11 invalidation hooks may want to call `InvalidateDecorationsAsync()` after the toggle.
- `FirstRequestAt` is set when a request passes the gate, just before the `Task.Run` calls start, not when the source method actually begins. That is within microseconds.

## Snapshot
See the tree id returned with the status (`bash .superpowers/tools/snap.sh` after this report).

## Fix round 1

Two Important findings, one root cause. An invalidation applied its immediate result only when it was complete (deviation 1). `DeliverLateAsync` also skipped `onLate` when every late batch was null. Together, an invalidation whose slow plugin answered null, threw or hung applied nothing: stale rows and folder flags stayed, the fast plugins' fresh results were lost, and a plugin that had left the set kept its decorations.

### Changes
- `src-winui/SimpleFile.Core/RowDecorationCoordinator.cs`
  - `DeliverLateAsync` now awaits `Task.WhenAll(calls)` (the settled calls never fault). It always calls `onLate` with the final merge unless `ct` was cancelled, even when every late batch is null. The all-null shortcut is gone.
  - The result within the budget comes from one snapshot of which calls had settled. It records the pending plugin ids in the result, so a call that settles between two reads is never counted both answered and pending.
  - The doc comment says the final merge comes "even when none of them had anything".
- `src-winui/SimpleFile.Core/RowDecorations.cs` (`PaneDecorations`)
  - It keeps its plugin order, the batches it used and the pending plugin ids. `IsComplete` now means "no pending plugin". The internal `Merge(..., bool isComplete)` becomes `Merge(..., IEnumerable<string>? pendingPlugins)`.
  - New internal `KeepingPendingFrom(shown)`. For an incomplete result of the same folder:
    - each plugin that answered, or answered nothing, replaces its part;
    - each plugin still pending keeps its batch from `shown`;
    - a plugin outside the current order (it left the set) is dropped.

    A complete result, or a result for another folder, is returned unchanged.
  - New internal `HasSameContentAs(other)`: same folder, and the very same batches (reference equality) in the same order.
- `src-winui/SimpleFile.Core/ExplorerWorkspace.Navigation.cs`
  - `InvalidateDecorationsAsync(pane)` always applies its immediate result. `ApplyDecorations` (non-late) overlays it on the shown decorations through `KeepingPendingFrom`, under the gate. The late merge follows as before.
  - Deviation 1 is therefore replaced. The pane no longer waits for the late merge: it applies fresh parts at once and never blanks pending ones.
  - `ApplyDecorations` skips the `ContentVersion` bump and `Changed` when the new result has the same content as the shown one. Example: a navigation's late merge in which every late plugin answered null, which is the case now delivered and was skipped before.
- Spec §7, "Newer requests win": rewritten for the overlay and the convergence rule.

A plugin whose call never completes keeps only its own last-shown part. The fast plugins' fresh results and the removal of plugins that left the set no longer wait on it.

### Tests (written first)
- `RowDecorationCoordinatorTests.DecorateAsync_LateSourcesThatAnswerNothing_StillDeliverTheFinalMerge(throws: false|true)`: the slow source returns null or throws after the budget; `onLate` runs once with the fast plugin's rows and flags only.
- `RowDecorationCoordinatorTests.DecorateAsync_CancelledRequest_GetsNoLateMerge`: a guard on the cancelled path. It passed before the fix and still passes.
- `ExplorerWorkspaceTests.InvalidateDecorations_SlowSourceThatAnswersNothing_ClearsItsStaleDecorations(throws: false|true)`: plugins com.a (fast) and com.b (slow), both shown. After `InvalidateDecorationsAsync`, com.a's new text `A2` shows at once and com.b's old part stays while pending. When com.b returns null or throws, its row and `com.b:inRepository` are cleared, and `A2` and `com.a:inRepository` stay.
- `ExplorerWorkspaceTests.InvalidateDecorations_FastResultApplies_WhileASlowSourceHangs`: com.b never answers during the test. com.a's fresh text and its dropped flag apply at once, and com.b's shown part stays.
- `ExplorerWorkspaceTests.InvalidateDecorations_PluginThatLeftTheSet_IsClearedWhileTheRestArePending`: `SetSource` {com.a, com.b} -> {com.b}, and com.b hangs. After the invalidation, com.a's row, flag and `PluginsFor` entry are gone at once. When com.b finally returns null, every flag is cleared.

RED (tests added, production unchanged):
```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RowDecorationCoordinatorTests|FullyQualifiedName~ExplorerWorkspaceTests"
  Failed ExplorerWorkspaceTests.InvalidateDecorations_SlowSourceThatAnswersNothing_ClearsItsStaleDecorations(throws: True)
  Failed ExplorerWorkspaceTests.InvalidateDecorations_SlowSourceThatAnswersNothing_ClearsItsStaleDecorations(throws: False)
  Failed ExplorerWorkspaceTests.InvalidateDecorations_FastResultApplies_WhileASlowSourceHangs
  Failed ExplorerWorkspaceTests.InvalidateDecorations_PluginThatLeftTheSet_IsClearedWhileTheRestArePending
  Failed RowDecorationCoordinatorTests.DecorateAsync_LateSourcesThatAnswerNothing_StillDeliverTheFinalMerge(throws: False)
  Failed RowDecorationCoordinatorTests.DecorateAsync_LateSourcesThatAnswerNothing_StillDeliverTheFinalMerge(throws: True)
Failed!  - Failed:     6, Passed:    79, Skipped:     0, Total:    85
```

GREEN:
```
same filter -> Passed!  - Failed:     0, Passed:    85, Skipped:     0, Total:    85
same filter, --no-build, 5 runs in a row -> Passed: 85, Failed: 0 each time
```

### Full results
- `npm run build:winui`: `0 Warning(s)`, `0 Error(s)`.
- `npm run check:winui`: `Passed!  - Failed: 0, Passed: 1303, Skipped: 0, Total: 1303`.
- `npm run check`: exit 0, `WinUI parity gate lists 86 commands, 57 context ids, 83 palette ids.`
- No project, package, lock, IPC schema or Rust file changed. No plugin file location changed.
