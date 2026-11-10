# Task 10 report: the side-panel slot and the Hello panels

Status: DONE. Everything is uncommitted in the working tree. Nothing was committed, stashed or pushed.

## What was implemented, per file

### Created
- `src-winui/SimpleFile.App/MainWindow.PluginPanels.cs`. This is the panel host, which spec §7 calls `PluginPanelHost`. All of it runs on the UI thread.
  - **`SyncPluginPanels(IReadOnlyList<PluginRecord> enabled)`** (private). `ApplyPluginContributions` calls it. It stores `PluginContributionAdapter.Panels(enabled)` and hides the shown panel when `PluginPanelLayout.StillWanted` drops it. It also forgets the chrome of panels that left, through the helper `ForgetUnwantedPluginPanelChromes`. Its body contains no `new `, no `CreatePanelAsync` and no `PluginPanelWindow`.
  - **`internal Task ShowPluginPanelAsync(pluginId, panelId)`**:
    - It returns a faulted task with `InvalidOperationException("Plugin '<id>' has no panel '<panelId>'.")` for a panel that is undeclared or not enabled.
    - It hides any other shown panel, docked or detached.
    - When this panel is detached, it activates the panel's window.
    - Otherwise it builds the docked chrome on first use, places it in its placement's region, and calls `_pluginBodyLoads.Request(PluginPanelLayout.BodyLoadKey(panel), () => LoadPluginPanelBodyAsync(chrome, presentation, detached: false))`.
  - **`internal Task TogglePluginPanelAsync(hostCommandId)`**: hides the panel when it is shown (docked or detached) and shows it otherwise. A command that toggles no panel runs as a plugin command.
  - **`internal void HidePluginPanel()`**: forgets the queued body load, empties the body, collapses both regions, and closes a detached window. A hidden panel keeps no element.
  - **`private Task DetachPluginPanelAsync()`**: drops the docked body and opens a `PluginPanelWindow`. The window is tracked with `PluginWindowHostFor(id).TrackHostWindow(window)` (see deviation 2). The body is queued with `detached: true`.
  - **`private Task DockPluginPanelAsync()`**: `CloseForDock()` on the window, then docks again and loads a new body (`detached: false`).
  - **`OnPluginPanelWindowClosed`**: any close that was not for docking hides the panel.
  - **`private void ClearPluginPanels()`**: `DisposePluginsAsync` calls it before the manager stops.
  - **Layout**:
    - `ApplyPluginPanelLayout()` sets the right column widths (grip 8 px, panel clamped by `PluginPanelLayout.ClampRightWidth` against `PanesAndPanelGrid.ActualWidth`) and the bottom region height (clamped against `FilePaneShell.ActualHeight`).
    - `OnPluginPanelHostSizeChanged` re-applies the layout on resize.
    - **`internal void ResizePluginPanelBy(double delta)`** is the one resize path. The grip handlers drag from the press point (start size + pointer travel) through it, save on release, and reset to the default on a double-tap.
  - **Toggle chrome.** `RefreshPluginPanelToggleButtons()` draws each panel's generated toolbar button with `SetToggleChromeState(button, shown, "Hide <title>", "Show <title>")`.
  - **Visibility snapshot.** `_shownPluginPanelStateKey` is a `volatile string?` that holds the shown panel's `StateKey`. It is published whenever a panel is shown or hidden.
  - **Body load.** `LoadPluginPanelBodyAsync` calls `manager.CreatePanelAsync(…, detached, …)`. That is the file's one call. A result whose presentation ended is dropped. On failure the body shows `This panel could not be loaded: <message>`, with AutomationId `<chrome id>.Error`, and the error is logged to `plugins.log`.
  - **`PluginPanelsService : IPluginPanels`** (nested, one per runtime, created through `CreatePluginPanels`, which is `PanelsFor`). It is callable from any thread:
    - `ShowAsync` queues with `PluginUiDispatch.EnqueueAsync` and never runs inline. It completes once the panel is shown, and fails for an undeclared or disabled panel.
    - `Hide` uses `TryEnqueue` with `HidePluginPanelIfShown(stateKey)`, so it does nothing when another panel is shown.
    - `IsVisible` reads the volatile snapshot.
    - The dispatcher is the `_pluginWindowQueue` captured on the UI thread, because `PanelsFor` can run off the UI thread.
- `src-winui/SimpleFile.App/PluginPanelWindow.cs`:
  - **`PluginPanelChrome`**: the code-built host chrome shared by the docked and windowed presentations:
    - a `Border` with AutomationId `PluginPanel.<pluginId>.<panelId>` (docked) or `PluginPanelWindow.<pluginId>.<panelId>` (window);
    - a header with the title (`.Title`);
    - `.Detach`, named `Detach <title>` docked or `Dock <title>` in the window, present only when `Detachable`;
    - `.Close`, named `Close <title>`;
    - a `ContentPresenter` (`.Body`);
    - `Presentation` and `Removed` guards for late body loads.
  - **`PluginPanelWindow : Window`**: code-built, with `SecondaryWindowChrome.Apply(this, title, PluginPanelLayout.WindowWidth, PluginPanelLayout.WindowHeight)`. It has `IsClosed`, `ClosingForDock`/`CloseForDock()` and `TrackedByHost`.
- `src-winui/SimpleFile.App/RowResizeGrip.cs`: the north-south twin of `PaneResizeGrip`, with `ProtectedCursor` `SizeNorthSouth` and a hover line.
- `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/HelloPanels.cs`:
  - `HelloPanels.Create(host, request)` (NoInlining, no WinUI type in its signature) builds a `StackPanel` with three children:
    - a `TextBlock` `Hello.PanelBody`;
    - a `HelloGrip` `Hello.PanelGrip`;
    - a button `Hello.PanelDialog`, which shows "Hello panel dialog" on the button's own `XamlRoot` through `host.Dialogs.ShowDialogAsync` and appends `panel:<result>` to `dialog.txt`.
  - `HelloGrip : Grid` sets `ProtectedCursor`.

### Modified
- `src-winui/SimpleFile.App/MainWindow.xaml`:
  - `FilePaneShell` gains a fourth `RowDefinition` (`Auto`) and `SizeChanged="OnPluginPanelHostSizeChanged"`.
  - `PluginPanelBottomRegion` is a `Grid` in `Grid.Row="3"`, Collapsed. It holds a `RowResizeGrip` (`PluginPanel.BottomGrip`) and the `PluginPanelBottomHost` border.
  - `PanesGrid` (now `Grid.Column="0"`) is wrapped in `PanesAndPanelGrid` with columns `*`, `0` (`PluginPanelRightGripColumn`) and `0` (`PluginPanelRightColumn`). That grid holds `PanesGrid`, a `PaneResizeGrip` `PluginPanelRightGrip` (`PluginPanel.RightGrip`, column 1) and the Border `PluginPanelRightRegion` (column 2).
- `src-winui/SimpleFile.App/MainWindow.Plugins.cs`:
  - the options get `PanelsFor = CreatePluginPanels`;
  - `ApplyPluginContributions` calls `SyncPluginPanels(enabled)`;
  - `DisposePluginsAsync` calls `ClearPluginPanels()`;
  - `RunPluginCommandAsync` consults `PluginPanelLayout.ForCommand` before `RunCommandAsync` and shows the panel, so a panel command never reaches the plugin's `ICommandHandler`;
  - `WarmPluginRendering` also runs `PluginContributionAdapter.Panels(none)`, and its stray `}` placement is fixed;
  - `ClosePluginWindows` also hides a detached panel whose window no host tracks yet.
- `src-winui/SimpleFile.App/MainWindow.CommandSurface.cs`:
  - `OnGeneratedToolbarCommandClick` routes a panel command to `TogglePluginPanelAsync`;
  - `ApplyPrimaryToolbarCommandVisibility` ends with `RefreshPluginPanelToggleButtons()`.
- `src-winui/SimpleFile.App/PluginWindowHost.cs`: adds `internal void TrackHostWindow(Window window)`. It uses the same `PluginWindowSet` and seal as `Attach`, applies no chrome, closes the window at once when the host is sealed, and has a `Closed` handler that runs `DialogHost.HideDialogsOn(XamlRootOf(window))` and then `_windows.Remove(window)`.
- Hello fixture:
  - `plugin.json`: commands `toggle-panel` ("Hello: toggle panel") and `show-bottom` ("Hello: show bottom panel"), and the two `panels` entries exactly as the plan gives them.
  - `HelloPlugin.cs`: implements `IPanelProvider`. `CreatePanel` appends `<panelId>:<detached>` to `panel.txt` and delegates to `HelloPanels`. `show-bottom` awaits `host.Panels.ShowAsync("hello-bottom", ct)`.
- `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`: the new `SmokePluginPanelsAsync` (checkpoint `plugin-panels`) runs all seven plan steps, with every wait bounded at 10 s. The report's `plugins` scenario gains `pluginPanels`. Extra assertions:
  - Hello's `commands.log` never receives `toggle-panel`;
  - the bottom panel has no `.Detach`;
  - the window's title and content automation id;
  - the panel dialog is not on the main window;
  - `dialog.txt` ends `panel:None` after the disable;
  - `IsVisible` after each step.
- `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`: the new tests (listed below).
- `docs/superpowers/specs/2026-11-07-plugin-system-design.md`:
  - §7 panel bullet: "The App side, as built (phase 4 Task 10)" covers the naming (not a class: the two regions, driven by `MainWindow.PluginPanels.cs`), host chrome and automation ids, one visible panel and sizes, the grips, detach and dock, toggle semantics, the body (queue, failure text, no element while hidden), contribution changes, teardown through the window host, and `IPluginPanels` callable from any thread and always queued.
  - §13: Hello exercises both panel placements, and the smoke step `plugin-panels` covers them.

## TDD evidence

### RED
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests"`, run before any implementation:
```
Failed SimpleFile.Tests.WinUiSourceShapeTests.PluginPanelWindow_IsTrackedByThePluginsWindowHost   (FileNotFound MainWindow.PluginPanels.cs)
Failed SimpleFile.Tests.WinUiSourceShapeTests.PluginPanelRegions_AreDeclaredInMainWindowXaml      (Expected: 4  Actual: 3)
Failed SimpleFile.Tests.WinUiSourceShapeTests.PanelCommands_ShowFromCommandsAndToggleFromTheToolbar (RunPluginCommandAsync does not consult PluginPanelLayout.ForCommand.)
Failed SimpleFile.Tests.WinUiSourceShapeTests.PanelBodies_LoadThroughTheQueueWithThePanelSlotKey   (FileNotFound MainWindow.PluginPanels.cs)
... SyncPluginPanels_BuildsNoElement, PluginPanelsService_QueuesShowAndHide (FileNotFound)
Failed!  - Failed:     6, Passed:    51, Skipped:     0, Total:    57
```
`InitializePlugins_RendersOnceInsideTheBudgetedSpan_ThenSubscribesAndStartsBackends` passed unchanged, before and after.

### GREEN
The same filter after the implementation:
```
Passed!  - Failed:     0, Passed:    57, Skipped:     0, Total:    57
```
An intermediate run failed `PanelBodies_LoadThroughTheQueueWithThePanelSlotKey`, because the `Request(` call was split across lines. The fix put each `_pluginBodyLoads.Request(PluginPanelLayout.BodyLoadKey(panel), …)` on one line.

### Plugin smoke and startup budget
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1` printed `Fixture plugin staged at: R:\Repos\SumaFile\target\fixture-plugins\com.sumafile.hello`.
- `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target/fixture-plugins/com.sumafile.hello` exited 0:
  - `UI runtime smoke passed. Report: …\test-results\ui-smoke\20261109-224920-fb0bb9aa\report.json`;
  - `Completed scenarios: plugins=1, sevenzip=1, sevenzip-backend-loss=1, remote=1, remote-backend-loss=1, …, service-reconnect=1`;
  - the `plugins` scenario reports `pluginPanels: "hello-panel,hello-bottom"`, `passed: true`.

  It passed on the first run. The backslash form `target\fixture-plugins\…` is mangled by Git Bash's npm, so the forward-slash path was used.
- `npm run check:winui-startup-budget`:
  - `plugins-discovered span 13.7 ms (budget 50 ms, count=3)`;
  - `WinUI startup budget passed: ready in 413.2 ms`.

## Full-suite results
- `dotnet build src-winui/SimpleFile.sln -c Debug`: `0 Warning(s)`, `0 Error(s)`.
- `npm run check:winui`: `Passed! - Failed: 0, Passed: 1309, Skipped: 0, Total: 1309`.
- `npm run check`: exit 0. The parity gate reports 86 commands.
- `npm run test:maintenance`: exit 0 (`fail 0`).
- Rust: no Rust file changed, so cargo was not run. No NuGet or crate dependency changed, so no lock file changed.

## Deviations
1. **Where the smoke step runs.**
   - The plan says `SmokePluginPanelsAsync` is "called after `SmokePluginSettingsPageAsync`". It is called after the window self-close step instead, which still comes after the settings page.
   - Its step 6 disables and re-enables Hello. Run straight after the settings page, it would close the Hello window that `SmokePluginDisableClosesWindowsAsync` still needs.
   - Its step 4 also requires Hello's window host to track exactly the panel window.
2. **Window tracking when no unsealed host exists yet.**
   - `DetachPluginPanelAsync` calls `PluginWindowHostFor(id).TrackHostWindow(window)` when that host exists and is not sealed. The plan writes `PluginWindowHostFor(pluginId)!`.
   - Before the plugin's first activation creates its runtime there may be no host, or only the sealed host of a retired runtime. In that case the queued body load tracks the window right after `CreatePanelAsync` has created the runtime (`TrackedByHost`).
   - `ClosePluginWindows` (the main window's `OnClosed`) also hides a detached panel whose window is not tracked yet.
3. **`MainWindow.CommandRouting.cs` is unchanged.** The plugin prefix there already hands every `plugin:` id to `RunPluginCommandAsync`. The palette, context menus and sidebar section buttons all reach that method too, so the panel check sits in `RunPluginCommandAsync` (as the plan's test text requires) and covers every command path in one place.
4. **Toggle while detached.** `TogglePluginPanelAsync` hides a detached panel (it closes its window). It does not activate the window. The toolbar button reads "Hide <title>" while the panel is shown, so hiding is what that label promises.
5. **A detached panel remembers nothing once its window closes.** Closing the window any way other than Dock hides the panel. Detached exists only while the window is open, so the panel shows docked next time, as the plan's smoke step 7 requires. The SDK's `IPluginPanels.ShowAsync` doc says "docked or in its window as the user last left it". That stays true while the window is open: `ShowAsync` activates it.
6. **Extra test and assertions.**
   - `PluginPanelRegions_AreDeclaredInMainWindowXaml` pins the XAML structure: four `FilePaneShell` rows, the bottom region in row 3 with `PluginPanel.BottomGrip`, and `PanesAndPanelGrid` with `*`/`0`/`0`, `PluginPanel.RightGrip` and the right region.
   - The plan's tests also assert the wiring: `SyncPluginPanels(enabled);` in `ApplyPluginContributions`, `ClearPluginPanels();` in `DisposePluginsAsync`, `PanelsFor = ` in the options, and `RefreshPluginPanelToggleButtons();` in `ApplyPrimaryToolbarCommandVisibility`. They also check that `TrackHostWindow` applies no chrome and that `CreatePanelAsync(` appears exactly once in the file.
7. **The `ShowPluginPanelAsync` failure is a faulted task, not a synchronous throw**, as Task 7 did for `NoPluginPanels`. Callers await it, so they see the same exception.

## Concerns
- **`ShowAsync` completes when the chrome is shown, not when the body has loaded.** The body load is queued at Low priority by design (#82). A plugin that needs its body to exist right after `ShowAsync` would have to wait for its own `CreatePanel` call.
- **The Hello fixture has no toolbar entry for `toggle-panel`.** The toolbar toggle path (`OnGeneratedToolbarCommandClick` to `TogglePluginPanelAsync`, and the toggle chrome) is pinned only by source-shape tests here. The Git toggle (Task 13) and the Task 16 smoke exercise it for real.
- **A failure text may never be seen.** When `CreatePanelAsync` fails because the provider throws, the manager marks the plugin LoadFailed. The `Changed` render then hides the panel, so the user may see the failure text only briefly; the text stays only for failures that do not withdraw the plugin. The message is in `plugins.log` either way.
- **Install-folder requirement.** The user's standing requirement is that no plugin file is stored outside the installation folder. This task adds no file, path or storage location. Panel sizes go to the app's settings database (`pluginPanel.*`, Task 9). The Hello fixture's marker directory is the smoke's `SUMAFILE_HELLO_MARKER_DIR`, as before.

## Snapshot
The tree id is returned with the status (`bash .superpowers/tools/snap.sh`, run after this report was written).

## Fix round 1

Both review findings are fixed. Everything is still uncommitted. Nothing was committed, stashed or pushed.

### Finding 1: the panel toggle chrome was lost on toolbar rebuilds and chrome refreshes
- **Cause.** `ConfigureToolbarButton` resets a button's name, tooltip and background. It runs for every new button (a rebuild) and for every chrome refresh. The toggle was only redrawn from `PublishShownPluginPanel`, `SyncPluginPanels` and `ApplyPrimaryToolbarCommandVisibility`. The last of these is skipped when the overflow set is unchanged.
- **Fix.**
  - `MainWindow.CommandSurface.cs`: `ConfigureToolbarButton` now ends with `ApplyPluginPanelToggleChrome(button, action.CommandId ?? action.Id)`. It runs after the label's name and tooltip, so both `ApplyCommandSurfaceLayout` branches (rebuild and chrome refresh) keep the toggle.
  - `MainWindow.PluginPanels.cs`: the new `ApplyPluginPanelToggleChrome(Button, commandId)` draws `SetToggleChromeState(button, shown, "Hide <title>", "Show <title>")` for a panel command and does nothing for any other command. `RefreshPluginPanelToggleButtons` now calls it for each button.
- **Hello fixture.** `plugin.json` gains a primary toolbar entry `{ "command": "toggle-panel", "label": "Hello panel", "placement": "primary" }`, so the UI smoke drives the generated toggle button for real.

### Finding 2: the body could load twice in one presentation
- **Cause.** `PluginBodyLoadQueue.Run` removes the key from `_pending` before it starts the load. A show while that load awaits activation therefore passed the `Body.Content is null` check, and `Request` accepted it. Both loads passed `Current()`.
- **Fix.**
  - `PluginPanelChrome.LoadingPresentation` (`int?`) records the presentation whose load is running.
  - The new WinUI-free rule `PluginPanelLayout.NeedsBodyLoad(hasBody, presentation, loadingPresentation)` is `!hasBody && loadingPresentation != presentation`.
  - `ShowPluginPanelAsync` requests a body only when `NeedsBodyLoad` says so.
  - `LoadPluginPanelBodyAsync`:
    - returns at once unless `NeedsBodyLoad` holds for its presentation;
    - sets `chrome.LoadingPresentation = presentation` before `CreatePanelAsync`;
    - skips placing the element when `chrome.Body.Content is not null`;
    - clears its own mark in a `finally` block (only when the mark is still its presentation).
  - The detach and dock paths go through the same guarded load.
- **Spec.** §7 ("The App side, as built") now records both rules: the toggle is drawn last in `ConfigureToolbarButton`, and `LoadingPresentation` with `NeedsBodyLoad` ensures the body is created once per presentation.

### Tests added or changed (test-first)
- `PluginPanelLayoutTests.NeedsBodyLoad_OncePerPresentation` (new) covers four cases: no body and no load; a body; a load for the same presentation; a load for an ended presentation.
- `WinUiSourceShapeTests.PanelToggleChrome_SurvivesEveryToolbarBuildAndChromeRefresh` (new) checks that:
  - `ConfigureToolbarButton` calls `ApplyPluginPanelToggleChrome(button, …)` after its `SetName` and `SetToolTip`;
  - both the rebuild path and the refresh path reach `ConfigureToolbarButton`;
  - Hello declares `toggle-panel` as a primary toolbar entry.
- `WinUiSourceShapeTests.PanelBodies_LoadOncePerPresentation` (new) checks that:
  - the show asks `NeedsBodyLoad` before `Request`;
  - the load runs the guard, then sets the mark, then calls `CreatePanelAsync`;
  - the mark is cleared in `finally`;
  - the element is placed only when the body is still empty;
  - `PluginPanelChrome` has `LoadingPresentation`.
- `WinUiSourceShapeTests.PanelCommands_ShowFromCommandsAndToggleFromTheToolbar` (changed) now expects `RefreshPluginPanelToggleButtons` to call `ApplyPluginPanelToggleChrome`, and that helper to call `SetToggleChromeState(`.
- `MainWindow.UiSmoke.cs`, `SmokePluginPanelsAsync`:
  - **Toggle button.** The button must read `Show Hello panel`, or `Hide Hello panel` with the `SfBgSelectedBrush` background, with a tooltip equal to its name. This is asserted:
    - before any panel is shown;
    - after a forced full rebuild (`_appliedPrimaryToolbarSignature = null; ApplyCommandSurfaceLayout()`), with no panel shown and again with the panel shown;
    - after a chrome refresh (`ApplyCommandSurfaceLayout()` with the same signature) while the panel is shown;
    - after the disable and re-enable rebuilt the toolbar;
    - after Close.
  - **Once per presentation.** After step 1 the smoke calls `ShowPluginPanelAsync` again and then `LoadPluginPanelBodyAsync(chrome, chrome.Presentation, …)` for the current presentation, and drains the low-priority queue. `panel.txt` must gain no `hello-panel:False` line, and the body must be the same element.
  - **New step 8.** The toolbar button's click handler (`OnGeneratedToolbarCommandClick`) shows the panel, then hides it, and the button follows. Hello's `commands.log` still never receives `toggle-panel`.

### RED
- `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests|FullyQualifiedName~PluginPanelLayoutTests"`:
  - The first run failed to compile: `error CS0117: 'PluginPanelLayout' does not contain a definition for 'NeedsBodyLoad'`.
  - With a throwing stub of `NeedsBodyLoad` in place, the run gave:
    ```
    Failed SimpleFile.Tests.PluginPanelLayoutTests.NeedsBodyLoad_OncePerPresentation (NotImplementedException)
    Failed SimpleFile.Tests.WinUiSourceShapeTests.PanelBodies_LoadOncePerPresentation (ShowPluginPanelAsync does not ask PluginPanelLayout.NeedsBodyLoad.)
    Failed SimpleFile.Tests.WinUiSourceShapeTests.PanelToggleChrome_SurvivesEveryToolbarBuildAndChromeRefresh (ConfigureToolbarButton does not redraw a panel's toggle chrome.)
    Failed SimpleFile.Tests.WinUiSourceShapeTests.PanelCommands_ShowFromCommandsAndToggleFromTheToolbar (Sub-string not found)
    Failed!  - Failed:     4, Passed:    61, Skipped:     0, Total:    65
    ```
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`, then `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target/fixture-plugins/com.sumafile.hello`, run before any fix, exited 1. This is finding 1, example 2:
  ```
  UI smoke failed: System.InvalidOperationException: Before any panel is shown: toggle button 'Hello panel', tooltip 'Hello panel', selected False, expected Show Hello panel
  Evidence: R:\Repos\SumaFile\test-results\ui-smoke\20261109-230346-d8aa6bf9\report.json
  ```
- The same smoke with only the finding 1 fix in place passed every toggle check up to the rebuild and refresh while the panel was shown. It then failed on finding 2:
  ```
  UI smoke failed: System.InvalidOperationException: A second show or body load of the same presentation built another body: panel.txt has 2 docked bodies, expected 1; body replaced True
  Evidence: R:\Repos\SumaFile\test-results\ui-smoke\20261109-230424-227b818d\report.json
  ```

### GREEN
- The same filter, plus `PluginBodyLoadQueueTests`: `Passed!  - Failed:     0, Passed:    69, Skipped:     0, Total:    69`.
- The fixture was staged again, then `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target/fixture-plugins/com.sumafile.hello` exited 0:
  - `UI runtime smoke passed. Report: R:\Repos\SumaFile\test-results\ui-smoke\20261109-230520-968989b7\report.json`;
  - `Completed scenarios: plugins=1, sevenzip=1, sevenzip-backend-loss=1, remote=1, remote-backend-loss=1, large-folder=1, navigate-during-loading=1, dual-pane-layout=12, single-pane-layout=4, pane-filter-search-controls=1, preview-collapse-expand-pin=1, conflict-comparison-dialog=1, rename-before-after-preview=1, transfer-cancellation=1, adaptive-transfer-ui=1, storage-pressure-recovery=1, adaptive-bulk-copy=1, service-reconnect=1`;
  - `pluginPanels: "hello-panel,hello-bottom"`.
- `npm run check:winui-startup-budget` passed: `plugins-discovered span 11.5 ms (budget 50 ms, count=3)` and `WinUI startup budget passed: ready in 326.6 ms`.

### Affected suites
- `dotnet build src-winui/SimpleFile.sln -c Debug`: `0 Warning(s)`, `0 Error(s)`.
- `npm run check:winui`: `Passed!  - Failed:     0, Passed:  1312, Skipped:     0, Total:  1312`.
- `npm run check` exited 0, run both before and after the spec edit. The parity gate lists 86 commands, 57 context ids and 83 palette ids.
- `npm run test:maintenance` exited 0 (`pass 66`, `fail 0`).
- Rust: no Rust file changed, so cargo was not run. No NuGet package or crate changed, so no lock file changed.

### Files touched in this round
- `src-winui/SimpleFile.App/MainWindow.CommandSurface.cs`
- `src-winui/SimpleFile.App/MainWindow.PluginPanels.cs`
- `src-winui/SimpleFile.App/PluginPanelWindow.cs`
- `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`
- `src-winui/SimpleFile.Core/PluginPanelLayout.cs`
- `src-winui/SimpleFile.Tests/PluginPanelLayoutTests.cs`
- `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`
- `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/plugin.json`
- `docs/superpowers/specs/2026-11-07-plugin-system-design.md`

### Notes
- **Smoke coverage of finding 2.** For a plugin that is already activated, `CreatePanelAsync` completes synchronously on the UI thread, so the smoke cannot hold a load in flight with certainty. It checks the observable rule directly instead: a second load for the same presentation creates no body. The in-flight case is pinned by `NeedsBodyLoad_OncePerPresentation` and by the source-shape test of the mark.
- **Visible change in the Hello fixture.** Hello now adds a `Hello panel` button to the primary toolbar of any session that loads it. No other smoke step asserts the toolbar contents, and the full smoke passed.
- **Install folder.** The user does not want plugins stored outside the program installation folder. This round adds no file, path or storage location.
