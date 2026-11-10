# Task 5 report: carry-over (d), host-closed plugin windows release the app-wide dialog gate

Status: DONE. Nothing committed; all changes are in the working tree.

## What changed, per file

- `src-winui/SimpleFile.App/DialogHost.cs`
  - New `internal static int HideDialogsOn(XamlRoot? root)`. It hides every dialog shown through the host whose `XamlRoot` is `root` (`ReferenceEquals`; a root that cannot be read counts as no match). Each `Hide()` sits in try/catch. It completes that dialog's `Hidden` task and returns how many dialogs it hid. A null root returns 0.
  - `ShowAsync` registers a private `ShownDialog` (dialog plus a `TaskCompletionSource Hidden`, `RunContinuationsAsynchronously`) in a locked `List<ShownDialog> Shown`. It then awaits `Task.WhenAny(dialog.ShowAsync().AsTask(), shown.Hidden.Task)`.
    - If the dialog's own task wins, it returns that result.
    - Otherwise it returns `ContentDialogResult.None`, and a fault-only continuation observes the late task.
    - In both cases the `finally` removes the entry, decrements `_openCount` and releases `Gate`.
  - `RunWithProgressAsync<T>`: both `dialog.Hide()` calls (the `Opened` handler and the completion callback) return early when `dialog.XamlRoot is null`, and run inside try/catch.
- `src-winui/SimpleFile.App/PluginWindowHost.cs`
  - `CloseEach` calls `DialogHost.HideDialogsOn(XamlRootOf(window))` before `TryClose(window)`. The new `XamlRootOf` returns `window.Content?.XamlRoot`, or null if reading it throws.
  - `CloseAllAsync` (Disable, rescan, load failure) and `CloseAllNow` (main-window `OnClosed`) both go through `CloseEach`.
- `plugins/remote/ui/SumaFile.Remote/Views/RemoteManagerWindow.cs`: `RunWithProgressAsync<T>` gets the same null-`XamlRoot` check and try/catch around both of its `dialog.Hide()` calls. Its dialog goes through `IPluginDialogs.ShowDialogAsync`, then `PluginDialogs`, then `DialogHost.ShowAsync`, so `HideDialogsOn` also releases it when the window host closes the manager window.
- `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs` (`SmokePluginDisableClosesWindowsAsync`): before the disable, the smoke now does three things:
  1. It invokes `Hello.WindowDialog`, waits for "Hello window dialog" on the plugin window's XamlRoot, and asserts `DialogHost.IsDialogOpen`.
  2. After the disable, the existing 10 s wait loop also requires `!DialogHost.IsDialogOpen`, and its failure message reports `dialog gate held`.
  3. It then shows "Smoke gate check" on the main window through `DialogHost.ShowAsync`, opens it with `SmokeOpenDialogAsync`, closes it with `SmokeCloseDialog`, and awaits it within 10 s.
- Tests:
  - `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`: new `PluginWindowHost_HidesOpenDialogsBeforeClosingAWindow`, `DialogHost_ShowAsync_CompletesWhenTheHostHidesTheDialog` and `DialogHost_RunWithProgress_GuardsEveryHide`. Also a shared `internal static AssertEveryHideIsGuarded`, which checks that every occurrence sits inside some `try { … }` block's braces and that the count is exactly as expected, plus a private `IsInsideTry`.
  - `src-winui/SimpleFile.Tests/Remote/RemotePluginSourceShapeTests.cs`: new `ManagerWindow_ProgressDialogHide_IsGuarded`, which reuses `WinUiSourceShapeTests.AssertEveryHideIsGuarded`.
- Docs:
  - Spec §7, teardown-order bullet: "Open dialogs on a closing plugin window are hidden first; the gate is released even if WinUI never completes their `ShowAsync`." It also names the mechanism and the evidence.
  - Review notes, "Phase 4 closures": the closure of carry-over (d).

## TDD evidence

RED, with the tests and smoke edits written and before any implementation:

```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests|FullyQualifiedName~RemotePluginSourceShapeTests"
  Failed SimpleFile.Tests.Remote.RemotePluginSourceShapeTests.ManagerWindow_ProgressDialogHide_IsGuarded
  Failed SimpleFile.Tests.WinUiSourceShapeTests.DialogHost_RunWithProgress_GuardsEveryHide
  Failed SimpleFile.Tests.WinUiSourceShapeTests.DialogHost_ShowAsync_CompletesWhenTheHostHidesTheDialog   (Sub-string not found)
  Failed SimpleFile.Tests.WinUiSourceShapeTests.PluginWindowHost_HidesOpenDialogsBeforeClosingAWindow
Failed!  - Failed:     4, Passed:    55, Skipped:     0, Total:    59
```

GREEN, with the same filter after the implementation:

```
Passed!  - Failed:     0, Passed:    59, Skipped:     0, Total:    59
```

The smoke was also run as a negative check. With the implementation in place except the `HideDialogsOn` call in `CloseEach`, which was temporarily replaced and then restored from a scratch backup, the smoke failed:

```
npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello
UI smoke failed: System.InvalidOperationException: 10 s after the disable: window closed True, tracked 0, section shown False, dialog gate held True
```

So the defect is real: WinUI does not complete a dialog's `ShowAsync` when its window closes, and the gate stayed held. The new smoke assertion catches that. With the call restored:

```
npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello
    0 Warning(s)
    0 Error(s)
UI runtime smoke passed. Report: R:\Repos\SumaFile\test-results\ui-smoke\20261109-205916-54b1d2cf\report.json
Completed scenarios: plugins=1, sevenzip=1, sevenzip-backend-loss=1, remote=1, remote-backend-loss=1, large-folder=1, ...
```

## Full-suite results

- `dotnet build src-winui/SimpleFile.sln -c Debug`: Build succeeded, 0 warnings, 0 errors.
- `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug`: `Passed! - Failed: 0, Passed: 1194, Skipped: 0, Total: 1194`.
- `npm run check`: exit 0. The IPC schema is at 86 domain methods and the parity gate lists 86 commands; the packaging and workflow checks pass.
- `npm run test:maintenance`: 66 pass, 0 fail.
- Rust: no Rust file changed, so no cargo run was needed for this task.

## Deviations

- The plan's test text says "in `TryClose`'s caller, `HideDialogsOn(` precedes `.Close()`". `TryClose` itself holds `window.Close()`, and its caller `CloseEach` holds the `TryClose(` call. So the test asserts two things: in `CloseEach`, `DialogHost.HideDialogsOn(` comes before `TryClose(` and its argument reads the `XamlRoot`; and `TryClose` contains `window.Close()`. It also asserts that `CloseAllNow` and `CloseAllAsync` both go through `CloseEach`. The plan's Interfaces text says `CloseEach` and `CloseAllNow` call `HideDialogsOn`. `CloseAllNow` reaches it through `CloseEach`, as the plan's own file list (`CloseEach`/`TryClose` 103-125) implies.
- `window.Content?.XamlRoot` is read through a small `XamlRootOf` helper with try/catch, so a window whose content is already gone cannot throw out of `CloseEach` before the other windows close. The source still contains `window.Content?.XamlRoot`, and the test pins that too.
- `Attach`'s sealed-host path (`TryClose` of a freshly handed window) does not call `HideDialogsOn`. That window was never shown or tracked, so it cannot hold a dialog.

## Concerns

- `HideDialogsOn` matches by `ReferenceEquals` on `XamlRoot`. CsWinRT keeps one managed wrapper per live native object, and the smoke proves the match on the Hello window. If a future WinUI changed that, the result would be a silent no-match, and the smoke's `dialog gate held` assertion would catch it.
- A host-hidden dialog's `ShowAsync` result is `None`. Plugin code awaiting it (Hello writes `window:None`) carries on after its window closed. That is the plan's stated result.

## Fix round 1

Finding (Important, `PluginWindowHost.cs:51`): only a close started by the host released the gate. Suppose a plugin window is closed by the user (title-bar X, Alt+F4) or by the plugin's own `Close()` while one of its dialogs is open. That close reaches only `Attach`'s `Closed` handler, which only untracked the window, so `DialogHost`'s gate stayed held.

### Changes

- `src-winui/SimpleFile.App/PluginWindowHost.cs`: the `Closed` handler that `Attach` adds is now a block lambda. It calls `DialogHost.HideDialogsOn(XamlRootOf(pluginWindow))` first, then `_windows.Remove(pluginWindow)`. Running it after `CloseEach` is harmless: the entry has already left `Shown`, and `TrySetResult` and the guarded `Hide()` can both run twice safely.
- `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`: the new test `PluginWindowHost_HidesOpenDialogsWhenAWindowClosesOnItsOwn` checks `Attach`'s `pluginWindow.Closed +=` handler. The handler must be a block lambda that contains both `DialogHost.HideDialogsOn(XamlRootOf(pluginWindow))` and `_windows.Remove(pluginWindow)`.
- `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs`: a new step, `SmokePluginWindowSelfCloseReleasesGateAsync` (checkpoint `plugin-window-self-close`), runs after the disable/re-enable check. It does the following:
  1. Reopens the Hello window through `plugin:com.sumafile.hello:open-window` on the new, unsealed host.
  2. Opens "Hello window dialog" and asserts that `DialogHost.IsDialogOpen` is true.
  3. Calls `window.Close()` directly. This is the path a user's close takes; `CloseEach` is not involved.
  4. Within 10 s, requires that the window is closed, tracked 0 and the gate is free, and that the host is not sealed.
  5. Opens and closes "Smoke gate check after self-close" on the main window.
- Docs:
  - Review notes, carry-over (d): the heading now covers both host-initiated closes and closes by the user or the plugin. A new fix-round-1 bullet records the handler and the RED evidence. The new test and the smoke step are named in the "Pinned by" bullet.
  - Spec §7, teardown-order bullet: adds the user- or plugin-initiated close.

### TDD evidence

RED: the source-shape test, written before the fix.

```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests.PluginWindowHost"
  Failed SimpleFile.Tests.WinUiSourceShapeTests.PluginWindowHost_HidesOpenDialogsWhenAWindowClosesOnItsOwn [2 ms]
  Error Message:
   Attach's Closed handler must be a block lambda that hides the window's dialogs.
Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3
```

RED: the smoke, with the new step added and before the fix. This confirms the defect is real for a window that closes on its own:

```
npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello
UI smoke failed: System.InvalidOperationException: 10 s after the window closed itself: window closed True, tracked 0, dialog gate held True
   at SimpleFile.App.MainWindow.SmokePluginWindowSelfCloseReleasesGateAsync() in ...\MainWindow.UiSmoke.cs:line 680
Evidence: R:\Repos\SumaFile\test-results\ui-smoke\20261109-210452-5f441906\report.json
```

GREEN: after the fix.

```
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WinUiSourceShapeTests|FullyQualifiedName~RemotePluginSourceShapeTests"
Passed!  - Failed:     0, Passed:    60, Skipped:     0, Total:    60

npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello
    0 Warning(s)
    0 Error(s)
UI runtime smoke passed. Report: R:\Repos\SumaFile\test-results\ui-smoke\20261109-210533-552bb7ae\report.json
Completed scenarios: plugins=1, sevenzip=1, sevenzip-backend-loss=1, remote=1, remote-backend-loss=1, large-folder=1, ...
```

### Full-suite results after fix round 1

- `dotnet build src-winui/SimpleFile.sln -c Debug`: Build succeeded, 0 warnings, 0 errors.
- `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug`: `Passed! - Failed: 0, Passed: 1195, Skipped: 0, Total: 1195`.
- `npm run check`: exit 0. The parity gate lists 86 commands, and the packaging surface is wired.
- `npm run test:maintenance`: 66 pass, 0 fail.
- Rust: no Rust file changed.

### Notes

- The smoke closes the window with `Window.Close()`. That raises the same `Closed` event as the title-bar X and Alt+F4. The smoke cannot press the real title-bar button.
- When `Closed` fires, `window.Content.XamlRoot` can still be read and still matches the dialog's root. The GREEN smoke proves this: with the fix in place, the gate was released.
