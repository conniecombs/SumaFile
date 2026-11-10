# Task 6 report: carry-overs (b), (c), (e) and (f)

Status: DONE. Nothing committed; all changes are in the working tree.

## What changed, per file

### (b) Closing the manager during a transfer
- `plugins/remote/ui/SumaFile.Remote.Model/RemoteManagerViewModel.cs`
  - New `public const string SessionBusyText = "Remote session is busy; wait for or cancel its transfer"` and a private 200 ms `CloseRetryInterval`.
  - New `public Task<bool> CloseSessionAsync(TimeSpan transferWait, CancellationToken ct = default)`:
    - It computes one deadline (`Stopwatch`) from `transferWait`.
    - If a transfer runs, it calls `CancelTransfer()` and awaits the transfer's completion source with `WaitAsync(remaining, ct)`. A timeout is swallowed, and it then tries the disconnect anyway.
    - It then calls `_backend.DisconnectAsync` directly, so the transfer's `IsBusy` is not touched. On success it clears the session like `DisconnectAsync` does and returns true.
    - A `remote session not found` reply runs `DropLostSession()` and returns true. With no session open it returns true at once, with no backend call.
    - A busy reply (message contains `SessionBusyText`) retries after 200 ms while at least 200 ms of the deadline is left; otherwise it returns false. So there is never more than one attempt per 200 ms.
    - Other errors propagate.
  - `RunTransfersAsync` creates a `TaskCompletionSource` (`RunContinuationsAsynchronously`) once its guards have passed, stores it in `_transferFinished`, and completes it last in its `finally`.
  - (f) The `ReplaceLocalEntries` comment is now the plan's text verbatim.
- `plugins/remote/ui/SumaFile.Remote/Views/RemoteManagerWindow.cs`: `OnClosed` no longer calls `CancelTransfer()` and `DisconnectAsync` itself. It runs `_ = CloseSessionQuietlyAsync()`, which awaits `_viewModel.CloseSessionAsync(TimeSpan.FromSeconds(10))`.
  - A false result is logged at Info: `The closed manager's session was still busy after 10 seconds; it ends when the backend stops.`
  - An exception is logged at Info: `The closed manager's session did not disconnect: …`. The log call sits inside its own try/catch, so nothing escapes.
- `src-winui/SimpleFile.Tests/Remote/FakeRemoteBackend.cs`: new `CancellableTransferHandler` (it sees the transfer token and wins over `TransferHandler`), `DisconnectHandler` (throwing models a busy session) and `DisconnectAttempts` (Stopwatch timestamps).
- `src-winui/SimpleFile.Tests/Remote/RemoteManagerViewModelTests.cs`:
  - `CloseSessionAsync_DuringATransfer_WaitsForItToEndThenDisconnects`: the fake transfer ends 300 ms after its token is cancelled, and the disconnect fails busy while the transfer runs. The test asserts true, that the last disconnect ran after the transfer ended, and that the session is cleared.
  - `CloseSessionAsync_TransferThatNeverEnds_GivesUpAfterTheWait`: with a 1 s wait it asserts false within [0.95 s, 3 s], between 1 and 6 attempts, each at least 190 ms apart, and the session still set.
  - `CloseSessionAsync_NoSession_ReturnsTrueAtOnce`: the task is already completed and no disconnect is attempted.
- `src-winui/SimpleFile.Tests/Remote/RemotePluginBackendTests.cs`: `CloseSession_DuringAThrottledDownload_DisconnectsOnceTheTransferStops`.
  - It uses the real host and backend and the SFTP test server, with `bandwidthMib` 1 and a 64 MiB file.
  - The view model, built with `harness.Client` and `LocalFolderSource`, starts the download. After the row's first report, `CloseSessionAsync(15 s)` returns true and the download task ends.
  - `ListDirectoryAsync` on the old session id then fails with a message starting `remote session not found: `.
- `src-winui/SimpleFile.Tests/Remote/RemotePluginSourceShapeTests.cs`: `ManagerWindow_OnClosed_ClosesTheSessionAfterItsTransfer` pins the wiring. `OnClosed` must reach `CloseSessionAsync(TimeSpan.FromSeconds(10))` fire-and-forget and log at Info, with no direct `DisconnectAsync(` or `CancelTransfer()`. The plan did not list this test; see Deviations.

### (c) The 1.0.4 credential format
- `plugins/remote/backend/src/secrets.rs` gets three new tests:
  - `blob_matches_the_1_0_4_golden_bytes`;
  - `golden_1_0_4_blobs_decode`;
  - `#[cfg(windows)] windows_store_reads_a_credential_written_as_1_0_4_wrote_it`. It uses a `CredentialGuard` drop guard (`CredDeleteW`) and a `write_as_1_0_4` helper that calls `CredWriteW` directly with `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE` and target `SumaFile.Remote.test-<16 random hex>` from `getrandom::fill`.

  The golden bytes are `b"password\0p\xc3\xa4ssw\xc3\xb6rd"` and `b"private-key-passphrase\0k\xc3\xa9y"`. I checked them against `git show main:crates/simplefile-service/src/remote/secrets.rs`, whose `encode_secret` is identical.

### (e) The remote UI smoke
- New `scripts/lib/credentials.ps1`. `Add-Type` defines `SumaFile.Smoke.CredentialNative`, which P/Invokes `CredEnumerateW`, `CredDeleteW` and `CredFree`. It exposes two functions:
  - `Get-GenericCredentialTargets -Prefix <string>` returns `string[]`, empty when nothing matches (`ERROR_NOT_FOUND`).
  - `Remove-GenericCredential -Target <string>` treats a missing target as success.

  Both refuse a `*` in their argument.
- `scripts/smoke-winui-ui.ps1`:
  - It dot-sources the new lib and records `$credentialBaseline = Get-GenericCredentialTargets -Prefix 'SumaFile.Remote.'` before the App (and the SFTP server) start.
  - The `finally` has a third after-run check, which runs after the App and the server are stopped or killed:
    - When `<testRoot>\remote-credential.txt` names a target that is new and still present, the check calls `Remove-GenericCredential` and reports it with `Write-Host`.
    - A file naming anything outside the `SumaFile.Remote.` prefix fails and deletes nothing.
    - Any other new target fails the run and is not deleted.
  - As with the existing after-run checks, a failure fails a smoke that otherwise passed. When the App's report already failed, it is still reported, as `Write-Warning "After the failed run: …"`.
- `src-winui/SimpleFile.App/MainWindow.UiSmoke.Remote.cs`:
  - Right after the Save, once the target is known, the App writes it to `<testRoot>\remote-credential.txt` (`SmokeRemoteCredentialFile`).
  - Step 11 asserts `baseline.All(t => current.Contains(t, StringComparer.OrdinalIgnoreCase))`. Its message names the vanished targets.
  - Cleanup: a cleanup exception is now kept in `cleanupFailure` and no longer dropped by `stepFailure ??=`:
    - with both failures it throws `new AggregateException(stepFailure, cleanupFailure)`;
    - with stray targets, the stray `AggregateException` carries both failures;
    - with one failure, it rethrows that one.
  - `.WaitAsync(SmokeRemoteWait)` was added to the cross-process awaits:
    - the four `fileOps.SetSettingAsync(SmokeRemoteBandwidthKey, …)` calls (old lines 245, 276, 383, 422);
    - `RunAppCommandAsync(SmokeRemoteManagerCommand)` (108, 482);
    - `manager.RestartBackendAsync` (507);
    - `await open()` in `SmokeRemoteOpenManagerAsync` (620), which bounds its callers at 101 and 302 and the sidebar-row open.

    None of the listed awaits is a transfer, so `SmokeRemoteTransferWait` was not needed.

### (f) Small leftovers
- `crates/simplefile-service/Cargo.toml`: removed `sha2 = "0.11"`. Nothing in the service used it, even at HEAD (`git grep sha2:: HEAD -- crates/simplefile-service` is empty). `cargo build -p simplefile-service` refreshed `Cargo.lock`; the only change is the `"sha2",` line dropped from simplefile-service's dependency list. `cargo metadata --locked` is consistent.
- `scripts/lib/plugin-staging.ps1`: `Get-BundledPluginFreshnessFiles com.sumafile.remote` returns `sumafile-remote-backend.exe, SumaFile.Remote.dll, SumaFile.Remote.Model.dll, SumaFile.Remote.deps.json, plugin.json`. `scripts/test-plugin-staging.ps1` Case 7 expects that list.
- `src-winui/SimpleFile.App/SimpleFile.App.csproj`, `CopyBundledPlugins`:
  - It adds an item `_BundledPluginFolder Include="$([System.IO.Directory]::GetDirectories('$(BundledPluginsStagingDir)'))"`.
  - Before the `Copy` it runs `<RemoveDir Directories="@(_BundledPluginFolder->'$(OutDir)plugins\%(Filename)%(Extension)')" />`, so only the staged folder names are replaced. `%(Filename)%(Extension)` rebuilds `com.sumafile.remote` from a dotted directory name.
  - The comment explains why `plugins\` itself is never removed.
- `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj`, `CopyRemotePluginFixture`: it runs `<RemoveDir Directories="$(RemoteFixtureDest)plugins\com.sumafile.remote" Condition="Exists('$(_RepoTarget)plugins\com.sumafile.remote')" />` before its `Copy`.
- New `scripts/test-plugin-copy-targets.ps1`. It needs the App's restore output; when `src-winui/SimpleFile.App/obj/project.assets.json` is missing it fails with `Run npm run build:winui first.`
  - In a temp folder it builds the staging tree (`bundled.json`, `com.sumafile.remote\{plugin.json,a.txt}`) and the output tree (`plugins\com.sumafile.remote\stale.txt`, `plugins\com.example.user\plugin.json`, `plugins-data\x\keep.txt`).
  - It runs `dotnet msbuild src-winui/SimpleFile.App/SimpleFile.App.csproj -t:CopyBundledPlugins -p:Configuration=Debug -p:Platform=x64 -p:OutDir=<out>\ -p:BundledPluginsStagingDir=<staging>\ -nologo`. Debug|x64 is what the solution build gives the App.
  - It asserts that `stale.txt` is gone, `a.txt` and `bundled.json` were copied, and `com.example.user` and `keep.txt` are unchanged. It then prints `Plugin copy targets keep user plugins and app-owned files.` and removes the temp folder in `finally`.
- `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`: `PluginCopyTargets_ReplaceOnlyTheirOwnFolders`, with XML helpers `CopyTargetElements`, `CopyTargetSteps` and `AssertRemovesBeforeEveryCopy`.
  - `CopyBundledPlugins` must have exactly one `RemoveDir` of `@(<item>->'$(OutDir)plugins\%(Filename)%(Extension)')`, and that item must include `GetDirectories('$(BundledPluginsStagingDir)')`.
  - `CopyRemotePluginFixture` must have exactly one `RemoveDir` of `$(RemoteFixtureDest)plugins\com.sumafile.remote`, with the source-exists condition.
  - In both targets the `RemoveDir` must come before the first `Copy`, and no `RemoveDir` may name `$(OutDir)plugins`, `$(OutDir)`, `$(RemoteFixtureDest)` or `$(RemoteFixtureDest)plugins`, with or without a trailing backslash.
- `docs/winui-migration/parity-gate.md:5`: the line now reads "**Contract:** the live service method count is in `ipc/schema/v1/commands.json`; [`inventory.md`](inventory.md) is the historical Tauri inventory (74 commands / emitted events / Svelte workflows)".

### Docs
- Review notes, "Phase 4 closures": closures of (b), (c), (e) and (f), with the tests and proofs.
- Spec §11.2 (manager window): "disconnects its session once the transfer has stopped (`RemoteManagerViewModel.CloseSessionAsync`, at most 10 s, retrying a busy session every 200 ms; a session still busy then ends with the backend)". See Deviations.

## TDD evidence

### RED
- `cargo test --locked -p sumafile-remote-backend --lib secrets`: these are pinning tests, and as the plan predicts they passed at once (`test result: ok. 5 passed`). To show they bite, I temporarily changed `bytes.push(0)` to `bytes.push(b':')`, then restored the file from a scratch copy. With that change, `blob_matches_the_1_0_4_golden_bytes ... FAILED` and `secret_encoding_round_trips_passwords_and_passphrases ... FAILED` (`test result: FAILED. 3 passed; 2 failed`).
- `dotnet test … --filter "FullyQualifiedName~RemoteManagerViewModelTests|FullyQualifiedName~WinUiSourceShapeTests"`:
  ```
  RemoteManagerViewModelTests.cs(819,31): error CS1061: 'RemoteManagerViewModel' does not contain a definition for 'CloseSessionAsync' …
  RemoteManagerViewModelTests.cs(849,31) / (877,24), RemotePluginBackendTests.cs(217,38): the same
  ```
- After `CloseSessionAsync` existed and before the csproj change, the same filter gave:
  ```
  Failed SimpleFile.Tests.WinUiSourceShapeTests.PluginCopyTargets_ReplaceOnlyTheirOwnFolders
     Assert.Single() Failure: The collection did not contain any matching items
  Failed!  - Failed: 1, Passed: 76, Total: 77
  ```
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1`:
  ```
  Case 7: com.sumafile.remote freshness files are [sumafile-remote-backend.exe, SumaFile.Remote.dll, SumaFile.Remote.Model.dll, plugin.json]; expected [..., SumaFile.Remote.deps.json, plugin.json].
  ```
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-copy-targets.ps1`:
  ```
  CopyBundledPlugins left stale.txt in the bundled plugin folder.
  ```

### GREEN
- With the view-model change, the three `CloseSessionAsync_*` tests pass (the same filter: 76 passed, with only the copy-target test still red).
- After the csproj changes: `Plugin copy targets keep user plugins and app-owned files.`
- After the freshness change: `Plugin staging tests passed.`
- I rebuilt with cargo, `scripts/build-plugins.ps1 -SkipCargo`, `npm run build:winui` (0 warnings, 0 errors) and `scripts/build-fixture-plugin.ps1`. Then `dotnet test … --filter "…RemoteManagerViewModelTests|…WinUiSourceShapeTests|…RemotePluginSourceShapeTests|…RemotePluginBackendTests"` gave `Passed! - Failed: 0, Passed: 97`, real-backend test included.
- Mutation check for (b). I made two temporary changes, restored the file from a scratch copy, and confirmed it byte-identical with `cmp`:
  - `CloseSessionAsync` skips the transfer wait;
  - a busy reply returns false at once.

  With both in, `--filter FullyQualifiedName~CloseSession` failed all three behaviour tests, the real-backend one included (`Failed: 3, Passed: 1`).
- The new source-shape test `ManagerWindow_OnClosed_ClosesTheSessionAfterItsTransfer` was written after the window change, so it has no RED run. See Deviations.
- (e): the smoke's cleanup path only runs when the App dies before its own cleanup, so a passing smoke cannot show it. I checked it two ways:
  - The lib against a disposable `cmdkey /generic:SumaFile.Remote.test-…` target: enumerate, find, delete, delete again (no error), and refuse a pattern.
  - The exact cleanup scriptblock, extracted from `smoke-winui-ui.ps1` into a scratch script, against disposable targets. The output:
    ```
    Removed the smoke profile's credential SumaFile.Remote.test-4b3d…, which the App left behind.
    own target removed: True
    foreign reported: The UI smoke left Credential Manager targets it cannot attribute to its own profile (not deleted): SumaFile.Remote.test-0eb4…
    foreign kept: True
    outside prefix refused: …remote-credential.txt names 'Other.Target', which is not a SumaFile.Remote.* target; nothing was deleted.
    left after the simulation: 0
    ```

## Full-suite results
- `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`: `Passed! - Failed: 0, Passed: 1201, Skipped: 0, Total: 1201`.
- `cargo test --locked -p sumafile-remote-backend`: every binary reports `test result: ok` (lib 48 passed).
- `cargo test --locked -p simplefile-service`: every binary reports `test result: ok` (lib 109 passed).
- `cargo fmt --all -- --check`: clean. `cargo clippy --locked --all-targets --all-features -- -D warnings`: clean.
- `npm run build:winui`: 0 warnings, 0 errors.
- `npm run check`: exit 0 (`WinUI parity gate lists 86 commands …`). `npm run test:maintenance`: exit 0, fail 0.
- `scripts/test-plugin-staging.ps1`: `Plugin staging tests passed.`
- `scripts/test-plugin-copy-targets.ps1`, after `npm run build:winui`: `Plugin copy targets keep user plugins and app-owned files.`
- `npm run smoke:winui-ui -- -Counts 10000 -SkipBuild -PluginsDir target/fixture-plugins/com.sumafile.hello` gave `UI runtime smoke passed.`, with these scenarios:
  ```
  Completed scenarios: plugins=1, sevenzip=1, sevenzip-backend-loss=1, remote=1, remote-backend-loss=1, large-folder=1, …, service-reconnect=1
  ```
  - Evidence folder: `test-results\ui-smoke\20261109-212141-35f059aa`.
  - `remote-credential.txt` named `SumaFile.Remote.59fce48a…`.
  - `Get-GenericCredentialTargets -Prefix 'SumaFile.Remote.'` returned 0 afterwards, so no credential was left behind.
  - Under Git Bash the path must use forward slashes; the backslash form from the plan was eaten by the shell.
- The first smoke attempt failed in the script's own freshness check (`Bundled plugin com.sumafile.remote … is stale`). The mutation-check test runs had rebuilt the remote model after the last staging, and the freshness list now also checks `SumaFile.Remote.deps.json`. Rerunning `build-plugins.ps1 -SkipCargo` and `npm run build:winui` fixed it, and the smoke above then passed. This is the freshness guard working, not a defect.

## Deviations
1. **The spec §11.2 sentence was refined** ("disconnects its session once the transfer has stopped (… at most 10 s …)"). The plan lists only the review notes as docs for this task. The old sentence promised an unconditional disconnect, and with a transfer that never stops within 10 s that is not what happens.
2. **The source-shape test `ManagerWindow_OnClosed_ClosesTheSessionAfterItsTransfer` was added**, though the plan does not list it. The plan's tests prove `CloseSessionAsync` itself, but nothing else checks that `OnClosed` uses it, which the wiring ledger says T6 proves ("`RemoteManagerWindow.OnClosed` (T6) | T6 view-model and real-backend tests"). It was written after the change, so it has no RED run.
3. **`CloseSessionAsync` calls `IRemoteBackend.DisconnectAsync` directly instead of the view model's `DisconnectAsync`.** The view model's method sets and clears `IsBusy`, which would clobber the running transfer's busy state. On success it clears the session the same way `DisconnectAsync` does.
4. **With an unfinished transfer, the disconnect is still tried once after the wait.** `CloseSessionAsync` waits for the transfer with the whole budget, and if that wait times out it still makes one disconnect attempt, because the backend may already have released the session. After that it retries only while at least 200 ms remains. So `CloseSessionAsync_TransferThatNeverEnds_GivesUpAfterTheWait` sees exactly one attempt; its assertion allows up to one per 200 ms.
5. **The `.WaitAsync` bound sits on `open()` in `SmokeRemoteOpenManagerAsync`, not on the callers.** It is on `await open()` at line 620, which bounds the callers at 101 and 302 and the sidebar-row open. Wrapping the callers' whole `SmokeRemoteOpenManagerAsync` call in `SmokeRemoteWait` would also have cut into its own bounded waits that follow.
6. **Paths.** `scripts/test-plugin-copy-targets.ps1` passes `-p:Platform=x64` explicitly. It also doubles the trailing backslash of a directory argument when the path contains a space, because Windows PowerShell 5.1 would otherwise escape the closing quote. The temp root has no space here.

## Concerns
- `CopyBundledPlugins` now removes and recopies each bundled plugin folder on every Debug build. `SkipUnchangedFiles` no longer saves any copies, and the `RemoveDir` would fail if a Debug `SumaFile.exe` were holding a plugin DLL during a build. In that case the build already fails, because `SumaFile.exe` itself is locked.
- `scripts/test-plugin-copy-targets.ps1` is not wired into CI. The plan has Tasks 6, 14 and 16 run it by hand, and CI is not run in this phase.
- The smoke's script-side credential cleanup only runs when the App dies after saving its profile, so a passing smoke does not exercise it. It was verified with the extracted scriptblock against disposable Credential Manager targets, which were all removed afterwards.
- The Windows secrets test and the credential checks wrote and deleted throwaway `SumaFile.Remote.test-*` generic credentials in this user's Credential Manager. None is left, as `Get-GenericCredentialTargets` confirmed.

## Fix round 1

### Finding (Important): the busy-retry loop in `CloseSessionAsync` was untested
The reviewer was right. In `CloseSessionAsync_DuringATransfer_WaitsForItToEndThenDisconnects`, the fake marks the session free before the transfer returns, and `CloseSessionAsync` waits for the transfer before it first tries to disconnect. That first attempt therefore succeeds. In `CloseSessionAsync_TransferThatNeverEnds_GivesUpAfterTheWait`, the transfer wait uses the whole budget, so only one attempt is made and the cadence assertions check nothing. The earlier mutation check combined two changes, so it did not show that the retry itself is pinned. No production code changed in this round. The loop was already correct; only the tests were missing.

Tests added in `src-winui/SimpleFile.Tests/Remote/RemoteManagerViewModelTests.cs`:
- `CloseSessionAsync_SessionStillBusyAfterTheTransfer_RetriesEvery200MsUntilItIsFree`: the transfer answers as soon as it is cancelled. The fake backend keeps refusing the disconnect with `BusyText` until 500 ms after that.
- `CloseSessionAsync_SessionBusyWithoutATransfer_RetriesEvery200MsUntilItIsFree`: there is no transfer, and another call (the concurrent-listing case) holds the session for 400 ms.
- Both use the shared `AssertBusyRetries` helper. With a 5 s budget, it checks:
  - `CloseSessionAsync` returns true and the session is cleared;
  - there are at least 2 attempts;
  - consecutive attempts are at least 190 ms apart;
  - the second-to-last attempt fell inside the busy window and the last one came after it.

### Evidence
These tests pin behaviour that already existed, so they passed against the unchanged code. To show that they catch a broken retry loop, I ran two mutants. Each time I copied the view model to the scratchpad first, then restored it and confirmed with `cmp` that the file was byte-identical (`restored-identical`).

- Mutant A is the reviewer's surviving mutation: a busy reply returns false at once. The guard became `if (exception is not null)`, because writing a bare `return false;` stops the build with `CS0162: Unreachable code detected` under TreatWarningsAsErrors. Command: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~CloseSession"`
  ```
  Failed SimpleFile.Tests.Remote.RemoteManagerViewModelTests.CloseSessionAsync_SessionBusyWithoutATransfer_RetriesEvery200MsUntilItIsFree [7 ms]
  Failed SimpleFile.Tests.Remote.RemoteManagerViewModelTests.CloseSessionAsync_SessionStillBusyAfterTheTransfer_RetriesEvery200MsUntilItIsFree [2 ms]
  Failed!  - Failed:     2, Passed:     4, Skipped:     0, Total:     6
  ```
  The four tests that already existed still pass under this mutant, which confirms the finding.
- Mutant B changes the retry delay from `CloseRetryInterval` (200 ms) to 10 ms, with the same command:
  ```
  Failed ...CloseSessionAsync_SessionBusyWithoutATransfer_RetriesEvery200MsUntilItIsFree [427 ms]
     Two disconnect attempts were 20.6002 ms apart.
  Failed ...CloseSessionAsync_SessionStillBusyAfterTheTransfer_RetriesEvery200MsUntilItIsFree [504 ms]
     Two disconnect attempts were 12.0557 ms apart.
  Failed!  - Failed:     2, Passed:     4, Skipped:     0, Total:     6
  ```
- After the restore, I ran the same filter 3 times in a row. Each run gave `Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6`.
- Affected suites: `dotnet test … --no-build --filter "FullyQualifiedName~RemoteManagerViewModelTests|FullyQualifiedName~WinUiSourceShapeTests|FullyQualifiedName~RemotePluginSourceShapeTests|FullyQualifiedName~RemotePluginBackendTests"` gave `Passed!  - Failed: 0, Passed: 99, Skipped: 0, Total: 99`. The earlier run had 97; the difference is the 2 new tests.
- Full suite: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo` gave `Passed!  - Failed: 0, Passed: 1203, Skipped: 0, Total: 1203`.
- The mutant builds had rebuilt the remote model, so I staged the plugins again to keep the smoke's freshness guard consistent:
  - `scripts/build-plugins.ps1 -SkipCargo`: `Bundled plugins verified: com.sumafile.sevenzip, com.sumafile.remote`.
  - `npm run build:winui`: 0 warnings, 0 errors.
  - `scripts/test-plugin-staging.ps1`: `Plugin staging tests passed.`
