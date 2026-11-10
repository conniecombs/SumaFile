# Phase 3 (FTP/SFTP plugin): whole-phase review, fix wave report

- Branch `plugin-system-phase-1`, working tree only. No commit, stash, reset, checkout, add, push or GitHub write.
- Base tree before the fix wave: `fbe073bae746172131a52538d2d8a7dd50cc7626` (from `bash .superpowers/tools/snap.sh`).
- Final tree: `f77c05f036eff1da3440ed9d44b9ae3cccb52762`.
- Diff: `snapdiff.sh fbe073ba… f77c05f0…` shows 15 files, 793 insertions and 87 deletions. No project file, package, `packages.lock.json` or `Cargo.lock` change.

## Finding 1 (Important): carry-over (f), the startup budget on CI

At `46860dc` the CI WinUI host job failed the budget step: `plugins-discovered` took 74.2 ms against 50 ms (count=3).

### What changed

- **Sub-stages without measurement cost** (`src-winui/SimpleFile.Core/StartupTrace.cs`).
  - `StartupTimer.Lap(stage, detail)` records a sub-stage like `Mark`. It writes the sub-stage only with the next `Mark`, after that mark's time is taken. So a lap adds no file write to the span it splits.
  - An internal constructor takes the writer, so tests do not touch the real `startup-timing.log`.
- **The budget covers the whole span** (`StartupTimingBudget.cs` and `scripts/check-winui-startup-budget.mjs`).
  - New `StartupTimingBudget.PluginDiscoverySpan` and `StartupStageSpan`. The span is `plugins-discovered` plus the `MainWindow.Connect.plugins-*` stages just before it, skipping other scopes. It runs from `backend-started` to `plugins-discovered`, the same quantity CI measured before, however the App splits it.
  - A log from before the split budgets the single delta.
  - The constant `PluginDiscoveryMaxDeltaMs` is now `PluginDiscoveryMaxSpanMs`. It had no other references, and its value is unchanged at 50.
  - The JS check mirrors the C# one and prints every stage, for example `plugins-discovered span 15.4 ms (budget 50 ms, count=3): plugins-begin 1.6 ms, …`, so the CI log shows which part dominates.
- **No hop and no evaluation in the span** (`src-winui/SimpleFile.PluginHost/PluginManager.cs`).
  - `PrepareScan` now also judges every plugin: signature verification, the approvals snapshot and bundled trust. `Evaluate` is static and returns an immutable `PluginEvaluation`.
  - `PreparedScan` holds the evaluations. Its internal `Plugins`, `Bundled` and `ApprovalSnapshot` are gone.
  - `ScanAsync` uses a matching prepared scan with no `Task.Run`, so discovery from it completes synchronously on the caller's thread. A fresh scan (rescan, mismatch, or a failed prepare) still runs on the pool.
- **Backends start after the span** (`PluginManager.cs`).
  - New `DiscoverAsync(PreparedScan?, bool startBackends, CancellationToken)`. The existing overloads keep starting backends.
  - New `StartStartupBackends()` starts every enabled `activateOn: startup` or archive-engine plugin. It is idempotent per services generation, because the start is cached.
- **One render, inside the span** (`src-winui/SimpleFile.App/MainWindow.Plugins.cs`, `InitializePluginsAsync`).
  - Laps `plugins-begin`, `plugins-manager-built`, `plugins-scan-awaited` and `plugins-evaluated`, then `DiscoverAsync(prepared, startBackends: false, …)`.
  - Then one `ApplyPluginContributions()`, then `timer.Mark("plugins-discovered")`.
  - Only then `manager.Changed += OnPluginsChanged` and `manager.StartStartupBackends()`, then the log lines as before.
  - Nothing can change a plugin between the scan and the subscription: no backend runs yet, and the user cannot act yet. So no `Changed` is missed.
  - The `RefreshStatusCenter` and capability refresh that the scan's `Changed` used to queue no longer run inside the span. Status rows list only Faulted plugins, and there are none at discovery. The capability refresh still runs after `workspace-initialized` and on every later `Changed`.
  - `MainWindow.xaml.cs` changes one comment only.
- **Spec §5.2** records the span definition, the sub-stages, the measurements below, the 74.2 ms CI value, and the rule: the budget stays 50 ms and is raised or split only on hosted-runner evidence. It also names the next lever, warming the render's first-use cost while the service starts.
- **Review notes**: a new "Open decisions for the user" section carries the open item, a hosted-runner measurement.

### Tests, written first

RED, all observed before the implementation:

- C# compile failure, 16 errors:
  - CS1739: no `startBackends` parameter;
  - CS1061: no `StartStartupBackends` and no `Lap`;
  - CS1729: no 2-argument `StartupTimer` constructor;
  - CS0117: no `PluginDiscoverySpan`.
- JS `scripts/tests/startup-budget.test.mjs`: 3 of 4 failed. The fourth is the no-plugin regression guard.
- After the Core and PluginHost changes, before the App change: `InitializePlugins_RendersOnceInsideTheBudgetedSpan_ThenSubscribesAndStartsBackends [FAIL]` (Failed 1, Passed 119).

GREEN: every new test passes. The new tests:

- `StartupTimerTests`, new file:
  - `Lap_IsWrittenWithTheNextMark_AndTheLapsAddUpToTheSpan`;
  - `Mark_TakesItsTimeBeforeWritingTheLaps` (a 200 ms lap write lands in the next span, not in this one);
  - `Mark_WithoutLaps_WritesOnlyItself`.
- `StartupTimingBudgetTests`:
  - `Evaluate_BudgetsThePluginSpanAcrossItsSubStages` (61 ms over 5 stages fails, although no stage alone does);
  - `Evaluate_PassesAPluginSpanUnderBudget`;
  - `PluginDiscoverySpan_RunsFromTheMarkBeforeTheSubStagesAndSkipsOtherScopes`.
- `PluginTrustTests`:
  - `Discover_WithAPreparedScan_CompletesOnTheCallersThread`: the task has completed, and `Changed` was raised on the calling thread, when `DiscoverAsync` returns;
  - `PrepareScan_VerifiesSignaturesBeforeDiscovery`: a payload tampered after `PrepareScan` still reads Verified and Enabled from the prepared scan, while a rescan reads Invalid and NeedsConsent.
- `PluginManagerTests`:
  - `Backend_StartupActivate_DeferredByDiscovery_StartsOnStartStartupBackends`: no launch until the call, then exactly 1 launch across 3 calls;
  - `StartStartupBackends_LeavesFirstCallBackendsAlone`;
  - `StartStartupBackends_LeavesPluginsThatAreNotEnabledAlone`.
- `WinUiSourceShapeTests.InitializePlugins_RendersOnceInsideTheBudgetedSpan_ThenSubscribesAndStartsBackends`:
  - the order is laps, `DiscoverAsync(prepared, startBackends: false,`, render, mark, subscribe, `StartStartupBackends()`, log lines;
  - exactly one render;
  - one subscription;
  - one mark.
- `scripts/tests/startup-budget.test.mjs`, new, run by `npm run test:maintenance` and in CI: 4 tests over the real script with `SUMAFILE_STARTUP_TIMING_LOG`. They cover the sum, the printed split, a pre-split log at 74.2 ms, and no plugin stage.
- Test infrastructure: `FakePluginProcessLauncher.Launches`, a counter.

### Measurements (local; no hosted runner was available)

Method:

- A scratch harness, not in the repo: `R:\Temp\claude\c48dfaf19294613c\4e549058-5b59-447a-927b-0fc453a48aa7\scratchpad\measure-startup.ps1`.
- It starts the Debug App in the `-PluginsDir` smoke's plugin environment: bundled 7-Zip and remote plus the Hello fixture (count=3), with disposable state.
- It waits for `ready`, kills the App and sums the `plugins-*` stages.
- Five starts per row, median. The CPU count is limited by inherited affinity (`start /affinity`). Ryzen 9 9950X.

| CPUs | Before (`fbe073ba`) | After |
| --- | --- | --- |
| 32 | 20.6 ms (20.4–24.0) | 15.0 ms (14.8–15.5) |
| 4 | 22.5 ms (21.9–24.9) | 15.6 ms (14.9–17.1) |
| 2 | 25.9 ms (24.2–26.3) | 16.1 ms (15.8–16.4) |
| 1 | 22.9 ms (22.9–23.3) | 15.6 ms (15.2–15.8) |

- After the fix the stages read: `plugins-begin` 1.5, `plugins-manager-built` 1.2, `plugins-scan-awaited` 0.0, `plugins-evaluated` 1.5 and `plugins-discovered` (the one render) about 11 ms, independent of the CPU count. Before the fix, taking CPUs away lengthened the span.
- Temporary profiling, removed afterwards, broke the 11 ms render down:
  - adapters and catalogs about 3.4 ms of first-use JIT;
  - the first sidebar section header about 5 ms (its `ColumnDefinitions` alone 2 ms);
  - inserting the sections 1.6 ms;
  - the second header 0.2 ms;
  - a warm re-render 0.1–0.3 ms.

  So what remains is one-time cold cost, not per-plugin cost.
- Release payload (count=2: 7-Zip and remote): `npm run check:winui-startup-budget` after `npm run smoke:winui` printed `plugins-discovered span 14.5 ms`. Earlier payload sessions in the log, before the fix, read 20.0–22.4 ms.

### Not done here

- **Re-measure on a hosted runner.** That needs a push, which is the user's call, and the user deferred CI fixes to the end. The next CI run's budget step prints the split.
- **No budget change.** The budget was neither raised nor split, because there is no hosted-runner evidence yet; spec §5.2 says so.
- **The CI result is uncertain.** At the old local-to-CI ratio (74.2 / 20.6 ≈ 3.6) the new span would project to about 54 ms. But much of that ratio likely came from parts that are now gone: the pool and dispatcher hops (the dispatcher hop on a GPU-less runner can wait behind a render pass), the second render, and the backend launches competing for 4 vCPUs. CPU speed alone (about 2.5x) projects about 38 ms.
- If CI still exceeds 50 ms, the printed split decides between warming the render's first-use cost during the service start and splitting the budget. Spec §5.2 records that.

## Finding 2 (Important): two contract decisions never confirmed by the user

### What changed

The user was **not** asked. This step runs as a workflow subagent with no channel to the user, and no answer may be invented. So the docs now state accurately that both decisions are pending, and when and where each answer goes:

- **Spec §5.1.** The "User confirmation: pending" sentence now says the answer is needed before phase 4 starts. Phase 4 builds `IPanelProvider`, `IRowDecorationProvider` and the Git UI on the same pattern. The sentence notes that no one in phase 3 could ask the user. The user's answer replaces it, and is recorded as a ruling in the review notes. The compile-only paragraph in §5.1 now points to §12 for the full, unconfirmed package set.
- **Spec §12.** A new "Phase 3 refinement, C# side (user confirmation pending)" paragraph records:
  - the decision that was recorded nowhere before: `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` and `CommunityToolkit.Mvvm`, compile-only, with the host's copies forced by `PluginLoadContext`'s shared prefixes;
  - who takes which package, and the CS0012 reason for Mvvm;
  - the test that enforces the set for the remote plugin;
  - that it widens "reference only the SDK package and the helper crate", and that the question must be asked before phase 4.
- **Review notes.** A new "Open decisions for the user" section gives both decisions with their trade-offs, marked `Open: awaiting the user's answer`. Each answer becomes a `Ruling:` line there.

### The two questions to ask the user before phase 4

1. **Object-typed UI hand-offs in a WinUI-free SDK at 1.0.0.**
   - Today `CreateSidebarSection` and `CreateSettingsPage` return `object`, `IPluginWindows.Attach` and `ShowDialogAsync` take `object`, and the host checks the WinUI type at run time.
   - Choice A: keep it. Typed members could still be added later in a minor version.
   - Choice B: make the SDK reference the Windows App SDK so these members are WinUI-typed. The xUnit test project then becomes a self-contained WinUI app, or the tests need another arrangement.
   - Removing the `object` members after phase 5 publishes the SDK needs a major version.
2. **Compile-only host packages in plugin C# projects.**
   - Plugin projects reference `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` and `CommunityToolkit.Mvvm` with `ExcludeAssets="runtime"` and `PrivateAssets="all"`.
   - The host's copies are always used at run time, so a plugin is bound to the host's versions of those three.
   - Accepting this widens spec §12's rule for C#. `SumaFile.Remote` does not compile without the Mvvm reference (CS0012).

## Full verification (in order)

1. Rust:
   - `cargo fmt --all -- --check`: exit 0, no output.
   - `cargo clippy --locked --all-targets --all-features -- -D warnings`: `Finished dev profile`, exit 0.
   - `cargo test --locked --all-features`: 39 result lines, 582 passed, 0 failed, 0 ignored, exit 0.
2. Service and fixture:
   - `cargo build -p simplefile-service`: `Finished dev profile`, exit 0.
   - `powershell … scripts/build-fixture-plugin.ps1`: `Fixture plugin staged at: R:\Repos\SumaFile\target\fixture-plugins\com.sumafile.hello`, exit 0.
3. WinUI build and tests:
   - `dotnet build src-winui/SimpleFile.sln -c Debug`: `0 Warning(s)`, `0 Error(s)`.
   - `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`: `Passed! - Failed: 0, Passed: 1143, Skipped: 0, Total: 1143`.
4. `npm run check`: exit 0.
   - `Checked IPC v1 schema: 86 domain methods, 7 emitted events, 12 goldens.`
   - `Checked plugin protocol v1: 8 host methods, 7 archive engine methods, SDK 1.0.0.`
   - `Checked remote plugin protocol: 11 methods, 3 events, 5 error texts.`
   - `WinUI packaging surface is wired.`
   - `WinUI parity gate lists 86 commands, 57 context ids, 83 palette ids.`
5. UI smokes:
   - `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins\com.sumafile.hello`: `UI runtime smoke passed`, with `plugins=1, sevenzip=1, sevenzip-backend-loss=1, remote=1, remote-backend-loss=1, …, service-reconnect=1`. This run is the end-to-end remote smoke.
   - `npm run check:winui-startup-budget`, right after that smoke as in CI: `plugins-discovered span 15.4 ms (budget 50 ms, count=3): plugins-begin 1.6 ms, plugins-manager-built 1.2 ms, plugins-scan-awaited 0.0 ms, plugins-evaluated 1.5 ms, plugins-discovered 11.1 ms.` and `WinUI startup budget passed: ready in 309.6 ms`.
   - `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -NoPlugins`: `UI runtime smoke passed`, with `plugins=1, sevenzip=1, remote=1, …`. This run is the no-remote-plugin smoke.
   - `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild`: `UI runtime smoke passed`.
6. Startup smoke. I ran `npm run release:build` first (phase plan Task 15 Step 5), so the startup smoke runs the current payload.
   - `npm run release:build`: exit 0. It printed:
     - `check:winui` 1143 passed;
     - `WinUI startup smoke passed`, `Bundled 7-Zip plugin trusted and registered.`, `Bundled remote plugin trusted.`;
     - `WinUI packaged file-operation smoke passed`, `Payload 7-Zip plugin smoke passed.`, `Payload remote plugin smoke passed.`;
     - `Extracted MSI carries the bundled plugins.`, `WinUI MSI artifact smoke passed`.
   - `npm run smoke:winui`: `WinUI startup smoke passed: PID 30432, title 'SumaFile', backend ready.`, `Bundled 7-Zip plugin trusted and registered.`, `Bundled remote plugin trusted.`
   - `npm run check:winui-startup-budget`, on the payload run: `plugins-discovered span 14.5 ms (budget 50 ms, count=2): plugins-begin 1.6 ms, plugins-manager-built 1.1 ms, plugins-scan-awaited 0.0 ms, plugins-evaluated 1.4 ms, plugins-discovered 10.4 ms.` and `WinUI startup budget passed: ready in 300.0 ms`.
7. Additional:
   - `npm run test:maintenance`: `tests 66, pass 66, fail 0`, the 4 new startup-budget tests included.
   - `npm run smoke:winui-file-ops`: `WinUI packaged file-operation smoke passed: …`, `Payload 7-Zip plugin smoke passed.`, `Payload remote plugin smoke passed.`
   - `powershell … scripts/build-plugins.ps1`: `Bundled plugins verified: com.sumafile.sevenzip, com.sumafile.remote`, exit 0.
   - `powershell … scripts/test-plugin-staging.ps1`: `Plugin staging tests passed.`, exit 0.
   - `cargo test --locked -p sumafile-remote-backend -p sumafile-remote-test-server`, all ok:
     - backend: lib 45, handler 12, legacy_import 8, manifest_defaults 1, process 2, profiles 6, secrets 4, sessions 3, sftp_test_server 6, transfers 7;
     - test server: lib 1, bin 2.
   - The C# end-to-end remote tests (`RemotePluginBackendTests` through the real `PluginManager`) are in the 1143.
   - No stray `SumaFile`, `simplefile-service` or `sumafile-*` process was left afterwards.

## Observations

- **Pre-existing flaky test.** `PluginManagerTests.ArchivePlugin_SuccessfulRegistration_LogsTheRegisteredFormats` failed once in a filtered run during this wave. It passed 5 of 5 reruns and in every full suite (including the one inside `release:build`).
  - The cause is a race in the test. `RegisterOnceAsync` sets `BackendRunning = true` (`PluginManager.ArchiveEngines.cs:219`). The caller writes `[info] Archive engines registered: …` only afterwards (`:129`), and the test asserts the log right after it sees `BackendRunning`.
  - It is unrelated to this wave and was not fixed here. It belongs to the ledger's deferred "timing-sensitive C# tests".
  - The fix would be to wait for the log line instead.
- **Earlier signature checks.** Signature verification of the startup scan now happens while the service starts instead of at the `DiscoverAsync` call. The approvals snapshot was already taken then, and rescans verify afresh.
