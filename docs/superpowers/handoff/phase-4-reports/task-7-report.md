# Task 7 report: SDK contracts, manifest and adapter for panels, row decorations, toolbar `when` and legacy settings

Status: DONE. Nothing committed; all changes are in the working tree. No NuGet package, project or lock file changed.

## What changed, per file

### SDK (`src-winui/SimpleFile.PluginSdk`, still WinUI-free, `PluginSdk.Version` still `1.0.0`)
- New `IPanelProvider.cs`: `object CreatePanel(PluginPanelRequest request)`. The XML docs say it runs on the UI thread, must return a `FrameworkElement`, and is asked once per presentation (docked, then detached), so each call needs a new element.
- New `IRowDecorationProvider.cs`: `Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest, CancellationToken)`. The docs say it runs on the thread pool, must not touch WinUI, should honour `ct`, and that a throw or a null result is logged and is not a load failure.
- New `IPluginPanels.cs`: `ShowAsync(panelId, ct)`, `Hide(panelId)` and `IsVisible(panelId)`. It may be called from any thread. `ShowAsync` and `Hide` are queued to the UI thread and never run inline. `IsVisible` returns the state the UI thread last published.
- New `IPluginDecorations.cs`: `void Invalidate()`.
- `PluginUiContracts.cs` gains these records:
  - `PluginPanelRequest(PanelId, Provider, Detached)`;
  - `PluginDecorationRequest(Provider, FolderPath, Paths)`;
  - `RowDecoration(Text, BrushKey = null, Tooltip = null)`;
  - `PluginFolderDecorations(Rows, FolderFlags)`, with `static Empty`, built on `ReadOnlyDictionary<,>.Empty` and `[]`.
- `IPluginHost.cs` gains `IPluginPanels Panels` and `IPluginDecorations Decorations`.
- `IPluginWorkspace.cs` gains `Task RefreshAsync(CancellationToken ct)`, which is always queued.
- `IPluginDialogs.cs` gains `void Notify(string message, bool isError)`, which is non-modal and may be called from any thread.
- `IPlugin.cs`: the remarks name the two new provider interfaces and extend the thread contract. `CreatePanel` runs on the UI thread and is asked once per presentation (`Detached` false, then true). `GetDecorationsAsync` runs on a thread-pool thread, after a UI-thread activation.
- `src-winui/SimpleFile.Tests/Fixtures/PluginSdkSurface.v1.txt`: regenerated deliberately (see Step 4 below). It has 91 lines added and 0 removed.

### PluginHost manifest
- `Manifest/PluginManifest.cs` gains these types and members:
  - `PluginPanelSpec(Id, Title, Provider, Placement, Detachable, Command)`;
  - `PluginRowDecorationSpec(Provider)`;
  - `PluginContributions.Panels` (init, default `[]`) and `RowDecorations` (init, nullable);
  - `PluginToolbarSpec.When` (init, default `""`);
  - `PluginSettingSpec.MigrateFrom` (init, nullable).

  All additions are init-only properties or new records, so no existing positional construction changed.
- `Manifest/PluginManifestReader.cs` changes:
  - `ContributionKinds` keeps the same list. Its comment no longer says "accepted and ignored until phase 4".
  - It has a new `PanelPlacementValues = ["bottom", "right"]` and a source-generated `LegacySettingsKeyPattern()` `^[A-Za-z][A-Za-z0-9._-]{0,127}\z` (generated, so no Regex is constructed on the startup path).
  - New `ReadWhen(item, path, errors)` is shared by context-menu items (the existing behaviour, factored out unchanged) and `toolbar[]`. It applies the 512-character bound and `PluginMenuWhen.IsValid`, and an invalid value is reported and read as `""`.
  - New `ReadPanels`, modelled on `ReadSidebar`, emits the plan's exact errors:
    - `contributions.panels needs a ui assembly.`;
    - the sidebar id-grammar message;
    - `The panel id '<id>' is declared more than once in contributions.panels.`;
    - the required title and provider;
    - `The <path>.placement value '<v>' is not recognised; use 'right' or 'bottom'.`, with a `bottom` default;
    - `detachable` through `OptionalBool`, default false;
    - the sidebar's undeclared-command message;
    - `The <path>.command value '<c>' already toggles another panel.`
  - `rowDecorations` is read as `{ provider }` through `OptionalObject`/`RequiredString`, with `contributions.rowDecorations needs a ui assembly.`
  - `ReadSettings` reads `migrateFrom`. It fails with `The <path>.migrateFrom value '<v>' is not a valid settings key.` or `The <path>.migrateFrom value '<v>' must not name a plugin setting.`, and the `plugin.` check ignores case.
- `Contributions/PluginContributionAdapter.cs`:
  - `ToolbarActions` sets `When` and `PluginId`.
  - New `Panels(enabled)` lists panels plugin by plugin, in manifest order. Each carries the host toggle command id `plugin:<id>:<command>` (or null) and the record's epoch.
  - New `RowDecorationProviders(enabled)` lists only records that declare `rowDecorations`, ordered by plugin id (`StringComparer.Ordinal`).

### PluginHost plumbing
- `PluginManager.cs`: `PluginManagerOptions.PanelsFor : Func<string, IPluginPanels>?` and `InvalidateDecorations : Action<string>?`, both null by default and documented. `RuntimeFor` passes `PanelsFor?.Invoke(id) ?? NoPluginPanels.Instance` and `new PluginDecorationsFacade(id, InvalidateDecorations)` to the runtime, once per runtime, under `_gate`, like `WindowsFor`.
- `PluginRuntime.cs`: the constructor takes `panels` and `decorations` and exposes `Panels` and `Decorations`, which stay the same across backend generations. It hands them to every `PluginHostServices` it creates.
- `PluginHostServices.cs`:
  - The constructor takes `IPluginPanels panels, IPluginDecorations decorations` after `windows`, and the class exposes them.
  - New `internal sealed class NoPluginPanels`:
    - `ShowAsync` returns a faulted task with `InvalidOperationException("This host has no panels.")`;
    - `IsVisible` returns false;
    - `Hide` does nothing.
  - New `internal sealed class PluginDecorationsFacade(pluginId, invalidate)`: its `Invalidate()` calls `invalidate?.Invoke(pluginId)`.

### Core
- New `src-winui/SimpleFile.Core/PluginPanel.cs` with these records:
  - `PluginPanel(PluginId, PanelId, Title, Placement, Detachable, CommandId, Epoch)`;
  - `PluginDecorationProvider(PluginId, Provider, Epoch)`.
- `CommandSurfaceLayout.cs`: `ToolbarAction` gains `public string When { get; init; } = ""` and `public string? PluginId { get; init; }`.

### App
- `PluginWorkspaceView.cs`: `RefreshAsync(ct)` queues `workspace.RefreshAsync(ct)` through `PluginUiDispatch.EnqueueAsync`, which is always queued, even on the UI thread (#82). It checks `ct` first and throws "SumaFile's workspace is not ready." while the workspace is null.
- `PluginDialogs.cs`: a new constructor callback `Action<string, bool> notify`. `Notify(message, isError)` checks the message for null and calls `_dispatcher.TryEnqueue(() => _notify(message, isError))`, so it is always queued and returns at once.
- `MainWindow.Plugins.cs`: `DialogsFor` passes `(message, isError) => ShowMessage(PluginDisplayName(pluginId), message, isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational)`. `PanelsFor` and `InvalidateDecorations` stay unset here; Tasks 10 and 11 wire them.

### Test doubles
- `Remote/FakePluginHost.cs`: `Panels` and `Decorations` throw `NotSupportedException`, like its other unused services.
- `PluginLoadingTests.FakePluginHost`: `Panels` and `Decorations` return `null!`.
- `PluginHostMethodsTests.NewServices`: it constructs `PluginHostServices` directly, so it now passes two more `null!` arguments. The plan's list did not name it; see Deviations.

### Tests
- `PluginSdkSurfaceTests.SdkContracts_UiHandOffsStayObjectTyped` checks three things:
  - `IPanelProvider.CreatePanel` returns `object` and takes `PluginPanelRequest`;
  - no exported member of any SDK type uses a `Microsoft.UI.*` or `Windows.UI.*` type (parameters, return types, properties, fields, events and generic arguments, checked by reflection), and no surface line contains `Microsoft.UI`;
  - no referenced assembly name starts with `Microsoft.WindowsAppSDK` or `Microsoft.WinUI`.
- `PluginManifestReaderTests`:
  - `Read_Panels_AreValidated`: 9 theory rows, one per error text. They cover the plan's rows plus `detachable` of the wrong type.
  - `Read_Panel_DefaultsToBottomAndNotDetachable`.
  - `Read_RowDecorations_NeedAUiAssembly`: no ui, no provider, and not an object; each gives exactly one error.
  - `Read_ToolbarWhen_IsValidated`: a valid value, an omitted one (`""`), one over 512 characters, invalid grammar, and the wrong type.
  - `Read_SettingMigrateFrom_IsValidated`: 7 theory rows. Two are valid, three fail the grammar (including `""`), and two are `plugin.` keys (including `Plugin.`).
  - `Read_SettingMigrateFrom_IsOptionalAndBoundedInLength`: an omitted value reads as null, 128 characters are accepted, and 129 are rejected.
  - `Read_ValidPanelAndRowDecorations_AreRead`: rewrites `Read_PanelsAndRowDecorations_AreAcceptedForLaterPhases`.
  - `Read_IgnoresUnknownTopLevelProperties` now uses `"futureField"`.
- `PluginContributionAdapterTests`:
  - `Panels_CarryTheEpochAndTheHostToggleCommand`.
  - `RowDecorationProviders_ListEnabledPluginsInIdOrder`: ordinal order (an upper-case `Beta` sorts before `alpha`), and a plugin without `rowDecorations` is skipped.
  - `ToolbarActions_CarryTheWhenExpressionAndThePluginId`: also checks that a built-in `ToolbarAction` has `When == ""` and `PluginId == null`.

### Docs (spec `docs/superpowers/specs/2026-11-07-plugin-system-design.md`)
- §4.4:
  - The `toolbar` shape gains `when?` and `panels` gains `command?`.
  - The "accepted and ignored until phase 4" sentence is replaced by the phase 4 reader rules: panels, rowDecorations and `toolbar[].when`.
  - `activateOn: "startup"` stays a `backend` field. The provider's UI activates on the first decoration request, which waits for `ready`.
- §5.1:
  - The phase 4 contract shapes.
  - The extended thread contract, including that `IPluginPanels`, `IPluginDecorations`, `RefreshAsync` and `Notify` may be called from any thread and are queued, never run inline.
  - `This host has no panels.`
  - The stable theme keys gain `SfAccentBrush` and `SfDecoration{Added,Modified,Untracked,Deleted}Brush`.
- §8.1: `migrateFrom`, covering its grammar, the `plugin.` ban, both error texts, copy-once-while-unset, and that it is honoured only for ids listed in `bundled.json`.

## TDD evidence

RED, after writing the tests and before any production change:
```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginSdkSurface|FullyQualifiedName~PluginManifestReaderTests|FullyQualifiedName~PluginContributionAdapterTests"
exit=1
PluginContributionAdapterTests.cs(101,23): error CS0246: The type or namespace name 'PluginPanelSpec' could not be found
PluginContributionAdapterTests.cs(102,9): error CS0246: The type or namespace name 'PluginRowDecorationSpec' could not be found
```
The build failed on the missing manifest types, as the plan expected. The compiler stops at the declaration-level errors first.

The first run after implementing found one more constructor caller, which I fixed:
```
PluginHostMethodsTests.cs(331,9): error CS7036: There is no argument given that corresponds to the required parameter 'decorations' of 'PluginHostServices.PluginHostServices(...)'
```

Next, before the snapshot was regenerated, only the pinned surface failed:
```
Failed SimpleFile.Tests.PluginSdkSurfaceTests.PublicSurface_MatchesPinnedSnapshot
Failed!  - Failed:     1, Passed:    68, Skipped:     0, Total:    69
```

Step 4 regenerated the surface:
1. A throwaway test (`ThrowawaySurfaceDump.cs`) wrote `PluginSdkSurface.Describe(...)` to the scratchpad.
2. The diff against the old fixture showed 0 lines removed and 91 added. Every added line belongs to the new types (`IPanelProvider`, `IRowDecorationProvider`, `IPluginPanels`, `IPluginDecorations` and the four records) or to the new members (`IPluginHost.Panels`/`Decorations`, `IPluginWorkspace.RefreshAsync`, `IPluginDialogs.Notify`).
3. The fixture was replaced, keeping the original LF line endings, and the throwaway test was deleted.

GREEN, with the same filter:
```
exit=0
Passed!  - Failed:     0, Passed:    69, Skipped:     0, Total:    69
```

## Full-suite results
- `npm run build:winui` (the whole solution, Debug): exit 0, `0 Warning(s)`, `0 Error(s)`.
- `npm run check:winui`: `Passed!  - Failed: 0, Passed: 1227, Skipped: 0, Total: 1227`.
- `npm run check`: exit 0. The parity gate lists 86 commands; no IPC method changed.
- `npm run test:maintenance`: 66 tests, 66 passed, 0 failed.
- No Rust code changed in this task, so cargo was not run.
- `packages.lock.json` files are unchanged.

## Deviations
1. `PluginHostMethodsTests.NewServices` also constructs `PluginHostServices` directly. The plan's list of test doubles did not name it. It now passes `null!` for the two new parameters, as it already did for dialogs, workspace and windows.
2. `NoPluginPanels.ShowAsync` reports `InvalidOperationException("This host has no panels.")` as a faulted task, not a synchronous throw. `await` and `Assert.ThrowsAsync` see the same exception, and a caller that discards the task cannot crash on it.
3. `PluginWorkspaceView.RefreshAsync` passes the plugin's `ct` to `ExplorerWorkspace.RefreshAsync(ct)` (the plan wrote `workspace.RefreshAsync()`) and checks it before the refresh starts.
4. Extra test rows beyond the plan's minimum:
   - `detachable` of the wrong type;
   - `rowDecorations` without a provider, and not an object;
   - `toolbar[].when` of the wrong type;
   - `migrateFrom` set to `""`, a `Plugin.` key, and the 128/129-character bound.
5. The `migrateFrom` `plugin.` ban ignores case, which is stricter than the plan's literal "must not start with `plugin.`".
6. The reader validates an unknown panel `placement`, and checks its `command` too, before it drops the entry. So one manifest reports every error at once.

## Concerns
- Task 8 owns the tests that exercise the plumbing: `Services_PanelsAndDecorations_ReachTheHostWithThePluginId` and `Services_WithoutPanelsFor_ReportNoPanelVisibleAndRefuseShow`. This task implements the plumbing those tests pin but did not write them, to stay within its scope. Today it is covered only by compilation and the existing suite.
- `PanelsFor` and `InvalidateDecorations` stay unset in the App until Tasks 10 and 11. Until then a plugin on the real App gets the no-panels service, and `Decorations.Invalidate()` is a no-op.
- The user's standing requirement that no plugin file is stored outside the installation folder is unaffected: this task adds no file, path or storage location.
