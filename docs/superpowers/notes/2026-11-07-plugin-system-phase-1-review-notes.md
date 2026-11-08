# Plugin system phase 1: review notes and controller rulings

Branch `plugin-system-phase-1`, final commit 487a2df. Source: the subagent-driven-development ledger for docs/superpowers/plans/2026-11-07-plugin-system-phase-1.md. Phase 2 planning should start from the follow-ups below.

## Final whole-branch review

# Final review: plugin system phase 1 (18a3761..6a0f496)

## How this was reviewed

I worked in passes and did not use subagents. Pass 1 read the spec, the plan (Global Constraints, Review Focus and all 17 tasks), the ledger extract and the task reports. Pass 2 went through the 835 KB diff file by file. I left out `ad001e5` (`tools/sevenz-evaluation`, `third_party/7zip`) and the three docs commits. Pass 3 read the HEAD sources on the integration seams: PluginManager, PluginRuntime, PluginHostServices, PluginHostMethods, PluginBackendHost, PluginBackendConnection, ProcessLauncher, JobObject, PluginDiscovery, the manifest reader, the signature verifier, the approval store, the installer, the load context and activator, the `when` parser, MainWindow.Plugins, SettingsWindow.Plugins, PluginProgressSink, the NamedPipeJsonClient changes, the service `plugin_archive.rs`, core `archive/engine.rs` and its call sites, and the `sumafile-plugin` server and host. Pass 4 checked the wire shapes across all three parties. I did not run any tests; nothing I read needed a run to settle it.

## Strengths

- **Concurrency in the manager is careful.** PluginManager, PluginRuntime and PluginHostServices hold to one lock order (manager, then runtime, then services). The `Task.Yield` in `StartBackendAsync` leaves the services lock before any manager lock is taken. Each backend generation gets fresh `PluginHostServices`, so a cached failed start never replays. Event subscriptions re-attach on every generation. `Changed` is never raised under `_ops`. This was the hardest part of the phase and it holds together.
- **Pre-handshake configuration is correct end to end.** `PluginBackendHost.StartAsync(..., configure)` builds the connection, runs `PluginHostMethods.Register` and the pending event attaches, and only then sends `plugin.handshake`. PluginManager uses the hook as the ruling intended (`PluginManager.cs:462-479`).
- **The three wire parties agree.** I traced the C# host, the `sumafile-plugin` crate and the service by hand:
  - Handshake params and result.
  - Numeric ids in both directions, with C# `TryGetInt32` matching Rust `as_u64`.
  - A `result: null` reply: `ReplyJsonOptions` sets it explicitly, and the Rust side tests `get("result").is_some()`.
  - Error objects.
  - `cancel {operationId}` as a request. The operation id is the host's progress id, which the backend got from `host.progress_begin`.
  - The `client: "simplefile-service"` service mode.
  - The `archive.*` param names in the service and the Hello backend.
  - `PropertyNamingPolicy = null`, so anonymous C# objects serialize with their source names (`values`, `operationId`).
- **Hostile input is handled well:**
  - The manifest reader rejects bad ids before anything else is read.
  - The `when` parser caps depth at 32, refuses oversized numbers with a `SyntaxError` instead of an `OverflowException`, and caps length at 512.
  - Frame size caps apply on both sides.
  - JSON depth limits turn into a Disconnected/Lost on the C# side and a parse error reply in Rust.
  - Panicking Rust handlers get a -32603 reply.
  - Every service-side pipe exchange has a deadline, and a timeout cancels the I/O.
  - Pipe names are validated as tokens.
  - Zip entries are checked against `..`, rooted paths, `:` and full-path containment before anything is written.
- **Small UI and data-path details are right.** `TransferManagerViewModel.EnqueueExternal` keeps plugin rows out of the transfer concurrency slots. Progress reports are coalesced. The installer stages into dot-prefixed folders (which discovery ignores) and rolls back on a failed move. The XAML spike is recorded honestly in §5.3, and its leftovers are gone from the solution.
- **Tests are disciplined.** Process-wide statics are isolated in collections ("Plugin catalogs", the Hello marker collection). The Hello fixture reports through marker files instead of statics. The process launcher has a smoke test against real `cmd.exe`.

## Issues

### Critical (Must Fix)

**C1. The host never registers archive engines with the service, so the phase-1 service hook can't be reached.**
Files: `src-winui/SimpleFile.PluginHost/PluginManager.cs` (no caller); `scripts/generate-ipc-bindings.mjs` (the comment "The host's plugin layer sends these itself"); `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/plugin.json:8`.

- **What's wrong:**
  - Nothing in `src-winui` calls `register_archive_engine` or `unregister_archive_engine`.
  - The `archiveFormats` contribution is parsed (`PluginManifestReader.cs:186`) and then ignored.
  - Spec §6.4 says that when a plugin declares `archiveFormats`, the host starts its backend at startup and registers it. §10 says engines are unregistered when the backend exits.
  - The reader does not enforce "plugins that declare `archiveFormats` must set `acceptsMultipleClients` to true".
  - The fixture declares `.hello` with `activateOn: first-call`.
- **Why it matters:**
  - §14 phase 1 lists "service archive-engine registration", and the plan's Goal says the fixture "proves every phase-1 slot".
  - The Task 15 Step 4 expectation (".hello shows in Create archive's format list and extracts") cannot pass. Its manual check was skipped, as were those of Tasks 11 and 12, so nothing caught this.
  - The whole Rust half (registry, pipe engine, two IPC methods) is dead code in the product, and the C#-to-Rust registration path has never run.
  - This is a gap in the plan: no task owns the host-side call. That is why every per-task review passed.
- **How to fix (one of two):**
  - **(a) Implement it.**
    - Add an `IArchiveEngineRegistrar` to `PluginManagerOptions`; the App implements it over the service client.
    - Treat a plugin with `archiveFormats` as startup-activated.
    - After a backend is accepted in `StartBackendAsync`, call `register_archive_engine { pluginId, pipeName, formats }`.
    - Call `unregister_archive_engine` in `OnBackendLost`, `TeardownAsync` and dispose.
    - Add the `acceptsMultipleClients` rule to the reader.
    - Add one PluginManager test with a fake registrar, and one end-to-end check (smoke or manual) that a `.hello` file lists through `list_archive`.
  - **(b) Re-scope it.** Amend spec §14 and the plan to say the host-side registration lands in phase 2. Make the reader reject or warn on `archiveFormats` until then, and fix the misleading comment in `generate-ipc-bindings.mjs`.
  - I recommend (a). It is about a day of work, and phase 2 is the 7-Zip move that depends on it.

### Important (Should Fix)

**I1. Plugin backends' child processes escape the kill-on-close job.**
Files: `src-winui/SimpleFile.Core/JobObject.cs:16-17`, `src-winui/SimpleFile.PluginHost/Backend/IPluginProcessLauncher.cs:45`.

- **What's wrong:** `ProcessLauncher` uses `JobObject.Create()`, whose `DefaultLimitFlags` include `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`. Any process a backend starts (7za.exe in phase 2, git or ssh in phases 3 and 4) silently leaves the plugin's job.
- **Why it matters:** Spec §6.1 gives the per-plugin job precisely because silent breakaway lets processes outlive the app. The implementation repeats the problem one level down. `Kill(true)` covers graceful dispose, but on a crash or forced exit the grandchildren survive.
- **How to fix:** Add `JobObject.Create(uint limitFlags)` (or `CreateForPlugin()`) with `KILL_ON_JOB_CLOSE` only, and use it in `ProcessLauncher`. Extend `PluginProcessLauncherTests` with a backend that spawns a child, then assert the child dies when the job is disposed.

**I2. Declared setting keys are not checked against the key grammar, which breaks settings isolation.**
Files: `src-winui/SimpleFile.PluginHost/Manifest/PluginManifestReader.cs:208-226`, `src-winui/SimpleFile.App/PluginSettingsPanelBuilder.cs:145`.

- **What's wrong:**
  - Global Constraints fix the key grammar as `^[a-z0-9][a-zA-Z0-9_-]{0,63}$`. `host.*` and `IPluginSettings` enforce it.
  - `ReadSettings` accepts any non-empty key, and the Settings page writes `plugin.<id>.<key>` with it unchecked.
  - Plugin `com.a` declaring key `b.c` writes `plugin.com.a.b.c`, which is plugin `com.a.b`'s key `c`.
  - A key like `my.key` gives the user a control whose value the plugin can never read: `host.get_settings` rejects the key.
- **Why it matters:** The Review Focus singles out settings-key isolation between plugins. The guard exists on two of the three write paths but not this one.
- **How to fix:** In `ReadSettings`, reject keys that fail `PluginHostMethods.IsValidSettingKey`. Move the regex into the Manifest namespace or a shared helper so the reader does not depend on Backend. Add a reader test with `"a.b"`, `""` and a 65-character key. Also guard in `SettingControl`.

**I3. "Verified publisher" can be shown for code the signature does not cover, and approvals ignore which folder a plugin is in.**
Files: `src-winui/SimpleFile.PluginHost/Trust/PluginSignatureVerifier.cs:79-91`, `Trust/PluginApprovalStore.cs:37-44`, `PluginManager.cs:174`.

- **What's wrong:**
  - The signature covers `plugin.json` plus whatever `files` lists. Nothing requires `ui.assembly` or `backend.executable` to be listed.
  - Someone can keep `signature.json` and the listed files, swap the unlisted DLL or exe, and the plugin still shows Verified. The manifest hash and signature status are unchanged, so the approval holds and there is no re-consent.
  - Approvals are keyed by id only. A user-root copy with the same `plugin.json` as an approved install-root plugin shadows it and inherits Enabled without consent. Phase 2 makes this sharper, because the installer pre-approves by manifest hash.
- **Why it matters:** The trust model is informational (spec §2). But the one positive claim it makes, "Verified publisher · <fingerprint>", should be true about the code that runs. A reasonable user reads Verified as "this code is the publisher's".
- **How to fix:**
  - Treat a signature as Invalid unless `files` covers the declared `ui.assembly` and `backend.executable`. Better still: every file in the folder except `plugin.json` and `signature.json`.
  - Record the folder (or root kind) in `ApprovalRecord` and require it to match in `Resolve`.
  - Add one test for each.

**I4. Plugin startup work runs on the UI thread and grows with plugin size.**
Files: `PluginManager.cs:91-95` and `448`, `MainWindow.Plugins.cs:65`, `SettingsWindow.Plugins.cs:177,207`.

- **What's wrong:**
  - `ScanAsync` awaits `_ops.WaitAsync(ct).ConfigureAwait(false)`. That completes synchronously when the semaphore is free, so `PluginDiscovery.Scan` and `Evaluate` run on the UI thread before first paint.
  - `Evaluate` includes the manifest hash and `PluginSignatureVerifier.Verify`, which reads and SHA-256-hashes every listed file with `File.ReadAllBytes`.
  - `StartBackendAsync`'s `await Task.Yield()` resumes on the UI synchronization context. Startup-activated (and lazily started) backends therefore run `Process.Start`, job creation and `CreateDirectory` on the UI thread.
  - Spec §1 says "Plugin discovery reads manifests only".
- **Why it matters:**
  - A signed plugin listing a few hundred MB of assets stalls first paint for seconds.
  - The 50 ms budget only flags it in CI, with the unsigned fixture.
  - Rescan and Install freeze the Settings window in the same way.
- **How to fix:** Run the scan and evaluate off the UI thread, for example `var fresh = await Task.Run(() => PluginDiscovery.Scan(...).Select(Evaluate).ToList(), ct)`. This still completes before `ExplorerWorkspace.InitializeAsync`, so the ruling on discovery ordering still holds. Replace `Task.Yield()` with a hop to the thread pool (`await Task.Run(...)` or `ConfigureAwait(false)` on a real async boundary). Hash files with streaming `SHA256.HashData(Stream)`.

**I5. The C# host has never talked to a real Rust backend.**
Files: `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs` (the plugin smoke runs only `greet`), `crates/hello-plugin-backend/tests/process.rs` (Rust client only).

- **What's wrong:**
  - Every C# backend test uses `FakeIpcServer`. The smoke runs the UI-only `greet` command.
  - The manual checks that would have driven `backend-ping` (Tasks 11, 12 and 15) were all skipped.
  - So `PluginBackendConnection` against `sumafile-plugin`, real `host.get_settings` through `FileOperationService`, job assignment of a real backend, and Lost on a real exit were checked only by reading. I read the shapes and they line up today, but nothing keeps them aligned.
- **How to fix:**
  - Add a C# integration test that launches the staged `hello-plugin-backend.exe` (skip when it is missing) through `ProcessLauncher` and PluginManager, runs `backend-ping`, and asserts the greeting setting round-trips.
  - Have the smoke run `plugin:com.sumafile.hello:backend-ping` too. It is staged in CI already.
  - Do this together with C1's end-to-end check.

**I6. A plugin's own ActivateAsync or DeactivateAsync can wedge the plugin manager for the whole session.**
Files: `src-winui/SimpleFile.PluginHost/PluginRuntime.cs:144-151, 162-173`; `PluginManager.cs:126-129, 230, 388`.

- **What's wrong:**
  - `RetireAsync` waits on `_activation` with no timeout, then awaits `DeactivateAsync()`. The SDK gives it no token.
  - Both happen while `_ops` is held (Disable, Rescan, Dispose).
  - A plugin whose `ActivateAsync` or `DeactivateAsync` never completes therefore blocks every later Enable, Disable, Rescan, Restart and the dispose step of session cleanup.
- **Why it matters:** Third-party code with a bug, not even a malicious one, freezes every plugin action in Settings with no feedback.
- **How to fix:** Bound both waits, for example `await _activation.WaitAsync(TimeSpan.FromSeconds(5))` and `DeactivateAsync().WaitAsync(TimeSpan.FromSeconds(5))`. On timeout, log, abandon the instance and continue. Test it with an `InstanceFactory` whose `DeactivateAsync` never completes.

### Minor (Nice to Have)

1. **Which thread runs plugin code is not defined.** Files: `PluginManager.cs:309-342`, `PluginRuntime.cs:144`. `RunCommandAsync` continues after `ConfigureAwait(false)`, so if the first `ActivateAsync` completes asynchronously, `ExecuteAsync` runs on the thread pool; otherwise it runs on the UI thread. The SDK surface is thread-agnostic today (dialogs and workspace marshal), so nothing breaks in phase 1. Define the contract, preferably "ActivateAsync and ExecuteAsync run on the UI thread", before phase 3 adds WinUI-typed contracts.
2. **The SDK major is checked in three inconsistent ways.** `PluginBackendHost.cs:162` uses `Version.TryParse`, which rejects `1.0.0-beta`; the reader uses a semver regex. Use `PluginManifestReader.IsSdkCompatible` for both. The Rust `SDK_VERSION` (crate) and `PLUGIN_SDK_VERSION` (service) are not covered by `check-ipc-schema.mjs`; add them.
3. **The reader misses some cross-references.** It does not check that `contextMenu[].items[].command` and `toolbar[].command` name a declared command; such items fail only when clicked, in `RunCommandAsync`. An empty `contextMenu[].group` produces an empty-label submenu. Two groups from one plugin get duplicate `ctx-plugin-menu:<id>` ids (ledger Task 10).
4. **A backend fault withdraws all of the plugin's contributions.** `EnabledPlugins` excludes Faulted (`PluginManager.cs:65`), so a backend crash removes even commands that don't need the backend. Spec §10 only says in-flight calls fail and Restart is offered. Consider keeping contributions and failing backend calls with the "plugin stopped" message.
5. **The protocol docs are too thin for a third-party author.** `ipc/schema/plugin/README.md` and `protocol.json` give names only. They lack `host.*` and `archive.*` param and result shapes, error codes (-32600 before handshake, -32601, -32602, -32000), the command line (`--pipe-name`, `--data-dir`), lifetime rules (exit after the last client, `first_pipe_instance`) and service-connection mode. The README also says either side sends `cancel`; only the host does. This is fine for phase 1 and needed by phase 5 (non-Rust backends rely on it, §6.5).
6. **Neither side checks who it is talking to on the pipe.** The host does not verify the pipe server is the launched process (`GetNamedPipeServerProcessId` against `process.Id`). The service opens plugin pipes with default impersonation QoS; use `security_qos_flags(SECURITY_IDENTIFICATION)`. These are cheap hardening steps against a same-session pipe squatter.
7. **Plugin logging can be abused or flooded.** `host.log` text and backend stderr go unsanitised into `plugins.log` (log-line forging), with one `File.AppendAllText` per line and no rate limit (`MainWindow.Plugins.cs:278-295`).
8. **A restart can race the old backend's pipe.** `PluginBackendConnection.DisposeAsync` kills without waiting for exit. A restart can launch the new backend while the old one still holds the pipe; `first_pipe_instance(true)` then fails and counts toward Faulted (ledger Task 6).
9. **Plugin shortcuts are shown but do nothing.** They appear in the palette but are not bound (ledger Task 11). Either bind them or hide the label.
10. **Disposing a plugin operation always reports success.** `PluginHostServices.Operation.Dispose` calls `Complete()`, so `using var op = Progress.Begin(...)` reports success when an exception unwinds. Document this or report Fail when neither was called; the host cannot know, so at least document it in the SDK.
11. **The installer reads the manifest by declared size and has no total-size cap.** The cap trusts `entry.Length`; there is no limit on total extracted size or entry count (ledger Task 12). Read at most `MaxManifestBytes + 1` bytes from the stream.
12. **The installer writes files before consent.** Spec §4.1 says it unzips after the consent dialog; the code extracts first and consent follows from Review. This is harmless, since the plugin stays disabled, but update the spec wording.
13. **`PluginManager.cs` is 643 lines.** Acceptable today. When C1 adds registration, move the backend lifecycle (lines 397-580) into a `PluginManager.Backend.cs` partial.

## Deferred-minor triage

**Must fix before merge:** none of the ledger minors by themselves. Two are folded into the issues above and should go into that fix round:
- Task 16 "schema check proves literal presence not dispatch" and Task 11 "Hello fixture manual check not run". Both are part of I5. The manual check was the only planned end-to-end proof, and C1 shows what skipping it cost.

**Fix early in phase 2 (cheap, and phase 2 makes them real):**
- **Task 4a, approval store.** A transient `IOException` makes `Load()` return empty, so the next `Set` wipes every other approval. The `.tmp` name is fixed, so two SumaFile processes collide. Once the installer pre-approves first-party plugins, this pops consent dialogs for Git and 7-Zip. Fix: don't save when `Load` failed for I/O reasons, and use a unique temp name.
- **Task 6, "Kill not awaited before relaunch".** See Minor 8. It interacts with `first_pipe_instance`.
- **Task 13, "handshake reply sdkVersion unchecked".** The service should also check the reply's `pluginId`. Also sanitize `plugin_id` for the thread name (the interior-NUL panic) by validating `pluginId` against the id grammar in `register_archive_engine`.
- **Task 9, Disabled→LoadFailed overwrite.** `MarkLoadFailedAsync` should also require `State == Enabled`. It is one condition.
- **Task 10 and Task 11, `EnsurePrimaryItem` re-adds a removed button.** `ApplyPluginContributions` runs on every `Changed` event, including backend start, so a user can never remove a primary plugin button. No phase-1 plugin uses `primary`, but phase 4's Git toggle does. Track "offered once" state before then.
- **Task 8, `LoadUnmanagedDll` not overridden.** Needed before phase 3 if the Remote UI DLL carries native dependencies.
- **Task 16, env overrides active in release builds.** At least log a line when `SUMAFILE_PLUGIN_ROOTS` or `SUMAFILE_PLUGIN_APPROVALS_PATH` replaces the defaults; a stray user-level variable silently hides every installed plugin.

**Can wait (phase 5 polish, or test-only):** everything else in the ledger. That covers:
- Task 1: snapshot nullability and message, the long line, XML docs.
- Tasks 2 and 3: test gaps and nits.
- Task 5: the unserializable-result reply, and the ReplyJsonOptions nested-null note (document it in the protocol README).
- Task 6: the remaining minors (2x timeout, OperationCanceledException, hostVersion source, test gaps).
- Task 7: everything (all benign, or resolved by the generation model).
- Task 8: everything except `LoadUnmanagedDll`.
- Task 4b: everything.
- Task 9: test and clock items; the file size is covered by Minor 13.
- Task 10: test gaps.
- Task 11: double toolbar rebuild, SelectedPaths lag, folder-picker cancellation, logging the dropped Begin.
- Task 12: everything else. The `TryDelete` sleep concern is resolved, because Install and Remove run in `Task.Run`.
- Task 13 (service): duplicate capability rows, format structs, the CancelIoEx fallback, lock fairness.
- Task 14: backpressure, cancel-flag growth, the serve Err path, docs, the dev-dependency, `cfg(windows)`, `set_call_timeout` visibility.
- Task 15: everything (fixture only).
- Task 16: env-var collection, smoke writing to real `%LOCALAPPDATA%`, default smoke scanning the real root.

## Rulings review

I agree with the rulings, with these exceptions or caveats:

- **Discovery before workspace init.** The ordering is right; the strip done by `CommandSurfaceLayout.FromJson` forces it. The ruling's "cost if wrong" understates the risk: it assumes discovery is cheap, but discovery includes synchronous signature hashing on the UI thread (I4). Keep the ordering and move the work off the UI thread.
- **Startup backend starts are fire-and-forget.** Agreed, but the implementation does not achieve "never delay first paint": `Task.Yield` resumes on the UI context, so `Process.Start` still runs there (I4).
- **Task 4a/4b split.** Fine as executed, and it is fully closed at HEAD. Note, though, that the follow-ups folded "into 4b" (transient read IOException, fixed `.tmp` name) are still open (triage above).
- **No fix round for the non-restorable `514d32c`.** Acceptable only if the PR is squash-merged or nobody bisects through it. Say so in the PR description.
- **host.* fails fast on the service connection.** Agreed for phase 1. Phase 2 has to decide how a 7-Zip extract from a pane reports progress, because the service connection can't. Put that in the phase 2 plan, not just in this ruling.
- **JobObject made public.** Reusing `Create()` as-is brought along `SILENT_BREAKAWAY_OK` (I1). This was not a ruling, but it should have been one.

## Declined to judge

- `ad001e5` (`tools/sevenz-evaluation`, `third_party/7zip/source`): the user's own unrelated tooling, per the brief.
- `2628d73`, `d24e6a8`, `5d2c034`: the spec and plan text, judged only as requirements, not as deliverables.
- The sidebar, panel, row-decoration and `settingsPage` slots: phases 3 and 4 per spec §14.
- In-pane browsing and mutation of plugin archive formats: deferred to phase 2 by the plan's scope note.
- Moving Git, FTP/SFTP and 7-Zip into plugins: phases 2 to 4.
- Installer pre-approval, payload staging, `docs/PLUGINS.md`, NuGet and crates.io publishing: phases 2 and 5.
- Sandboxing and least privilege for plugin code: an explicit non-goal (spec §2).
- No timeouts on host→backend plugin method calls: spec §10 says "same timeouts as service calls", and service calls have none.
- Change detection limited to the manifest hash, so an unsigned plugin's DLL can be swapped without re-consent: a deliberate spec §9 choice. I raise only the Verified-signature consequence (I3). The spec should decide whether unsigned code changes re-prompt.
- `is_archive_path` now stats files that match a registered extension: negligible cost.
- Legacy device names (`CON`, `NUL`) as zip entry or bare file names: Windows 11 no longer reserves them in full paths, and the effect is harmless.
- `docs/winui-migration/parity-gate.md` and the `check-winui-parity-gate.mjs` count bump: mechanical follow-ons to 97→99, which I checked only for consistency.

## Recommendations

1. Fix round 1 (before merge): C1 (implement, or formally re-scope it), I1 and I2. All three are small and touch only a few files.
2. Same round if possible: I5 (it is the test that would have caught C1), I3 and I6. I4 can go in the same round; it is mechanical.
3. Plan issue to record: future plans need a "cross-task integration" task that owns calls spanning layers (host→service here), and manual checks should not be skippable without an automated replacement.
4. Spec issues to resolve: §9 change detection for unsigned code, what "Verified" covers, and the §4.1 ordering of install and consent.

## Assessment

**Ready to merge?** With fixes

**Reasoning:** The core of the branch is careful and well tested: the state machine, the bidirectional pipe and the hardening against hostile input are sound. But the phase-1 archive-engine hook is never called by the host (C1), plugin backends' children escape their kill-on-close job (I1), and declared setting keys can cross plugin namespaces (I2). Fix those three, and preferably I3 to I6, before this lands as phase 1.

## Parked after the fix wave

Parked minors (phase 2 follow-ups): late unregister after restart can remove the new generation's registration (use a per-registration token); RegisterAsync has no host-side timeout; one transient registration failure faults the plugin for good; engines not re-registered after a service reconnect; installer pre-approval must write Folder; "Verified" covers entry files only; synchronous hang in DeactivateAsync not bounded.

# Deferred minors, rulings and design notes from the phase 1 ledger

## Deferred minor findings (per task)
Task 1: minor (deferred): surface snapshot does not pin nullability, parameter names or default values (PluginSdkSurface.cs)
Task 1: minor (deferred): snapshot mismatch failure message is unhelpful; say to update PluginSdkSurface.v1.txt deliberately
Task 1: minor (deferred): spec §9 amended paragraph has one over-long line
Task 1: minor (deferred): SDK has no XML docs / GenerateDocumentationFile (matters for third-party authors; phase 5 docs)
Task 2: minor (deferred): `Contains("id")` assertion is weak; boundary-length id tests, invalid JSON / non-object root, wrong-type field tests, prerelease/non-semver via Read, null json throws instead of erroring, semver regex looseness, JSON helpers could move to own file
Task 2: minor (deferred): spacing nits PluginManifestReader.cs:25, PluginManifestReaderTests.cs:172
Task 3: minor (deferred): untested branches (plugin.json IO error, sort tie-break, unreadable root, multi-error sdkVersion, three roots); empty sumafile-tests parent left after cleanup; File.Exists+ReadAllText double stat
Task 5: minor (deferred): unserializable handler result is swallowed silently instead of replying -32603; no tests for unregister via IDisposable, dispose with in-flight handler, re-registration; TestToken CTS not disposed
Task 5: minor (deferred): ReplyJsonOptions sets DefaultIgnoreCondition=Never for the whole reply, so nested nullable properties inside a non-null inbound result now serialize as explicit null (wire-format note; only affects host.* replies to plugins)
Task 6: minor (deferred): 2x timeout worst case; cancellation surfaces as OperationCanceledException (document); CreateDirectory exceptions not wrapped; plugin state not checked; tests missing for DisposeAsync kill, Lost via pipe disconnect, HasExited-during-startup; hostVersion is the PluginHost assembly version; Kill not awaited before relaunch
Task 6: minor (deferred): pre-built connection not disposed on handshake failure (harmless); Handshake null if read inside configure; launcher Dispose test can leak a 30 s ping on early assertion failure; pid-reuse flake theoretical
Task 7: minor (deferred): faulted start task cached permanently (no retry; Task 9 restart must create a fresh services/factory); OnBackendEvent discards start failure silently; log lines not newline-sanitised; missing 64-char/newline key tests, two-Register isolation test; unused out var; redundant null check
Task 7: minor (deferred): Complete/Fail handlers may double-notify the sink if the backend dies mid-complete (unverified); `id!` captured before assignment in ProgressBegin if a sink cancels synchronously; Lost handler never unsubscribed (harmless); _pending subscriptions stay queued after a failed start
Task 8: minor (deferred): unwrap TargetInvocationException from plugin ctor; defense-in-depth path containment in activator (validator already rejects); plugin.State not checked in CreateInstance; LoadUnmanagedDll not overridden (plugin-local native libs); tests missing for non-IPlugin type, no parameterless ctor, plugin-local dependency, ActivateAsync; process-wide env var in tests
Task 8: minor (deferred): cache key not TrimEnd'ed for trailing separator; cached resolver bound to first assemblyPath; in-place plugin dll update needs restart (by design); csproj Error branch untested
Task 4a: minor (deferred → fold into 4b): Verify can throw on a null Ed25519Verify or primitive exception (wrap primitive, reject null setter); null record in approvals file throws (filter nulls); also: transient read IOException → empty → next Set drops records; fixed .tmp name; case-sensitive never-listed check; missing tests for traversal/rooted/plugin.json listing/bad base64/lengths/algorithm/malformed JSON; hash case normalization
Task 9: minor (deferred): ThreeFailures test drives via RestartBackendAsync not lazy InvokeBackendAsync; no deactivate test; failure window not reset on restart (undocumented); no injectable clock; RestartBackendAsync holds _ops across start and RaiseChanged runs inside (Changed handlers must not block on manager ops); DisposeAsync does not await in-flight startup starts; PluginManager.cs 582 lines (split backend lifecycle partial); non-discoverable records carry placeholder Unsigned signature
Task 9: minor (deferred): Disabled→LoadFailed overwrite window (TeardownAsync nulls Runtime in a later lock section than DisableAsync sets Disabled; MarkLoadFailedAsync should also require State==Enabled); dispose-time cancellation for hung starts (link a manager CTS into StartAsync); round-2 RED evidence missing for Dispose test and its pre-assert is timing-dependent; PluginManager.cs ~600 lines (split backend lifecycle partial); retired-during-activation surfaces as PluginLoadException message
Task 10: minor (deferred): duplicate ctx-plugin-menu:<id> entry ids when a plugin declares two groups; EnsurePrimaryItem re-adds a button the user removed (track offered state in Task 11/12); missing tests for unterminated '(' , hasExtension without ')', empty folderFlag, non-Enabled records in adapter; SetPluginCommands does not dedup ids; UiAuditSprint4Tests/ShortcutLabelTests read All outside the collection
Task 10: minor (deferred): reader substitutes `when = ""` on rejection (harmless: an invalid manifest never registers contributions); RED evidence incomplete in fix report
Task 4b: minor (deferred): static seam field not volatile; test try/finally duplication; unseeded key generation; transitive BouncyCastle use in tests
Task 11: minor (deferred): toolbar rebuilt twice per plugin change (direct ApplyCommandSurfaceLayout + Changed handler); consent dialog not hidden on session end and Enable may run against a disposed manager (re-check ReferenceEquals after the dialog); IPluginWorkspace.SelectedPaths snapshot can lag multi-selection changes; EnsurePrimaryItem re-adds removed button (plan-mandated; design pass); plugin shortcuts not key-bound; folder picker ignores cancellation once open; Begin before _transfer exists drops the row silently (log it); Hello fixture manual check not run (covered by Task 16 smoke)
Task 12: minor (deferred): manifest size cap bypassable (entry.Length is declared size; no total-size/entry-count limit); updating an enabled plugin with a loaded DLL fails until restart (no DisableAsync first); _pluginsCts cancelled on close aborts in-flight ops and is never disposed; Settings opened before _plugins exists shows "not available" until reopened; whole-panel re-render on Changed loses in-flight edits; TextBox saves on every LostFocus; DefaultRoots()[1] magic index; backup-restore double failure replaces original exception; no tests for staging cleanup / move rollback
Task 13: minor (deferred): two owners claiming one extension → capabilities lists an unreachable row; three near-identical format structs; to_ascii_lowercase vs to_lowercase mismatch and a double engine_for_path lookup; handshake reply sdkVersion unchecked; unregister/register swap not atomic
Task 12: minor (deferred): TryDelete retry uses Thread.Sleep(250) — confirm Install/Remove never run on the UI thread; failed Remove cleanup throws although the plugin is already gone (show as notice)
Task 14: minor (deferred): no backpressure on unbounded outbound channel/handler spawns; cancel flags never cleared; parse/invalid-request errors omit `"id": null`; serve returns Err while connections live if a new instance fails; missing docs on public items; test gaps (malformed frame, host error reply, unknown reply id, emit frame); duplicate tokio dev-dependency; library not cfg(windows)-gated (CI is Windows-only today)
Task 13: minor (deferred): if CancelIoEx fails the I/O thread leaks until the plugin closes its end; try_lock loop is unfair (bounded by deadline); poisoned-lock recovery may reuse a mid-exchange connection; thread name panics on interior NUL in plugin_id (sanitize); CancelSynchronousIo is the documented API for sync handles (CancelIoEx shown to work empirically)
Task 14: minor (deferred): set_call_timeout is pub #[doc(hidden)] (pub(crate)/feature-gate would be cleaner); timed-out call vs late reply benign race
Task 15: minor (deferred): innerPath is a raw string prefix (document or segment-match); no tests for malformed lines / missing archive; `size as usize` allocation on hostile listings (comment); create overwrites silently; ping propagates get_settings errors instead of defaulting; script's newest-dll glob could pick a stale TFM folder; unsafe-paths test shares one out path
Task 16: minor (deferred): SUMAFILE_PLUGIN_ROOTS / SUMAFILE_PLUGIN_APPROVALS_PATH overrides active in release builds (consider #if DEBUG or a log line); env-var tests not in a Collection; smoke writes plugin data and plugins.log to real %LOCALAPPDATA%; ipc/schema/plugin README+protocol.json lack param types/result shapes/error codes for third-party authors; schema check proves literal presence not dispatch; default smoke still scans the real user plugin root

## Design notes
Task 3: design note: a later invalid/incompatible user copy shadows a valid install copy (later root wins regardless of state); Settings > Plugins shows both states so the user can see why. Intended; revisit if users hit it.

## Controller rulings
Ruling: new library projects target `net10.0-windows` and the SDK takes no WindowsAppSDK reference in phase 1 — the test project targets `net10.0-windows` and cannot reference a 19041-targeted library; WinUI-typed contracts arrive with the phase that needs them — cost if wrong: a retarget plus SDK minor bump later.
Ruling: the Hello fixture reports activation and executed commands through marker files under `SUMAFILE_HELLO_MARKER_DIR` instead of statics — statics are unreachable across the load context and the no-reference boundary — cost if wrong: slightly slower tests.
Ruling: Task 5 also updates `NullIpc.cs` for the new interface member — otherwise the test project stops compiling — cost if wrong: none.
Ruling: toolbar `placement` omitted defaults to `overflow` — the spec lists it as an enum and the less intrusive slot is the safe default — cost if wrong: a plugin author adds one word.
Ruling: reader requires `enum` settings to have non-empty `options`, rejects `options` on other types, rejects duplicate setting keys — keys become settings-key suffixes so duplicates collide — cost if wrong: stricter manifests.
Ruling: `ui.assembly` and `backend.executable` must be bare file names (no `/`, `\`, `:` or `..` segments); the reader rejects anything else — the launched executable must live in the plugin folder — cost if wrong: plugins cannot nest binaries in subfolders in SDK 1.
Ruling: BouncyCastle.Cryptography is added at the latest stable 2.x on nuget.org rather than pinned to the plan's 2.5.1 — the plan's number may be stale and the lock file pins whatever restores — cost if wrong: none beyond a lock-file diff.
Ruling: PluginBackendHost.StartAsync gains an optional `Action<PluginBackendConnection>? configure` parameter applied before the handshake request is sent; Task 7's PluginHostMethods.Register is passed through it — the host must guarantee handlers exist before the backend can reach them — cost if wrong: one extra parameter on an internal API.
Ruling: the untested real ProcessLauncher enters this fix round as a smoke test (launch cmd.exe, assert Exited, stderr forwarding, dispose kills) although the reviewer rated it Minor — kill-on-close is a spec §6.1 property and the launcher is the only code that implements it — cost if wrong: one slower test.
Ruling: the sink's cancel callback sends `cancel { operationId }` to the backend as a fire-and-forget request (errors from a dead pipe swallowed), matching the `cancel` method the Rust helper crate answers in Task 14 — cost if wrong: one method name to align later.
Ruling: the backend factory given to PluginHostServices becomes `Func<Action<PluginBackendConnection>, CancellationToken, Task<PluginBackendConnection>>` so services can attach pending event subscriptions and host handlers inside the pre-handshake configure step — events emitted right after the handshake must not be lost — cost if wrong: a slightly wider factory signature carried into Task 9.
Ruling: Register hooks connection.Lost and fails every open operation it tracks with "Plugin backend stopped." — otherwise Transfers rows never finish — cost if wrong: none.
Ruling: PluginActivator caches one PluginLoadContext per plugin folder (full path, ordinal-insensitive) — the design says one context per plugin and rescans/re-enables call CreateInstance again — cost if wrong: none.
Ruling: the fixture copy target resolves the fixture output via MSBuild GetTargetPath and fails the build with an Error when the dll is missing — silent empty copies make tests fail confusingly — cost if wrong: none.
Ruling: Task 4 splits. 4a (now, BCL only): PluginPackageHash, PluginApprovalStore, SignatureStatus/SignatureState, PluginSignatureVerifier with all structural checks and an injectable Ed25519 primitive `PluginSignatureVerifier.Ed25519Verify` (Func<byte[] publicKey, byte[] payload, byte[] signature, bool>) whose default returns false with Invalid("Ed25519 verification is unavailable in this build"). 4b (when BouncyCastle is in the cache): set the default to BouncyCastle and add the real-keypair tests. Reason: Tasks 9-12 need the types, not the crypto — cost if wrong: a signed plugin shows "Invalid signature" until 4b lands.
Ruling: startup-activated backend starts are fire-and-forget from DiscoverAsync/RescanAsync/EnableAsync; the manager tracks the in-flight start task so DisposeAsync can await or cancel it; the startup test waits on the fake launcher's launch signal instead of relying on synchronous launch — the spec caps discovery at 50 ms and a plugin must never delay first paint — cost if wrong: a startup plugin's first command may arrive before its backend is ready and simply wait on the cached start.
Ruling: hostile `when` recursion (reviewer Minor 1) enters a fix round — manifests are third-party input and a stack overflow kills the host; PluginMenuWhen caps nesting depth at 32 (SyntaxError beyond) and PluginManifestReader rejects a `when` longer than 512 chars or that fails PluginMenuWhen.IsValid — cost if wrong: stricter manifests.
Ruling: plugin discovery runs in MainWindow.ConnectAsync right after FileOperationService is created and BEFORE ExplorerWorkspace.InitializeAsync, with the progress sink, dialogs and workspace adapters resolving their targets lazily — CommandSurfaceLayout.FromJson strips toolbar ids it does not know, so plugin toolbar actions must be registered before the saved layout is parsed — cost if wrong: discovery sits earlier in the startup trace than the plan's text says; the 50 ms budget is unchanged.
Ruling: Task 13 (Rust service + ipc/schema + generated bindings) is dispatched while Task 11/4b reviews are pending — its files (crates/, ipc/, scripts/check-ipc-schema.mjs, generated Protocol files) are disjoint from any C# fix round those reviews could trigger, so the one-implementer rule's purpose (no conflicting edits) holds — cost if wrong: a merge-free but interleaved commit history.
Ruling: no fix round — commit 8d4f745 (Task 4b) lands the PluginHost csproj + lock on top, so HEAD is consistent (verified: csproj and App lock both reference BouncyCastle at HEAD); the inconsistency exists only when checking out 514d32c alone, and the repository merges via PRs — cost if wrong: one non-restorable intermediate commit in history.
Ruling: Task 17 (XAML spike) dispatched alongside Task 13 (Rust) and the Task 12 review — the spike's files (a new spike project, PluginActivator/App.xaml.cs only on pass, spec §5.3) are disjoint from both — cost if wrong: interleaved commits.
Ruling: Task 14 (new crate crates/sumafile-plugin + root Cargo.toml member + Cargo.lock) dispatched while Task 13 review, Task 12 fix and Task 17 spike are in flight — disjoint files except Cargo.lock, which no in-flight work should touch — cost if wrong: a Cargo.lock merge the final review resolves.
Ruling: per-exchange deadlines on the plugin pipe — handshake 5 s, archive.list 30 s, archive.extract and archive.create 600 s — and a 30 s bounded wait for the connection mutex ("plugin is busy"); a timeout closes the handle so the next call reconnects. Long extracts are legitimately slow and the protocol has no progress heartbeat yet, so a generous ceiling bounds the hang without breaking real work — cost if wrong: a >10 min plugin extract fails; phase 2 can add heartbeats.
Ruling: pipeName is restricted to a simple token (`^[A-Za-z0-9._-]{1,200}$`) at register_archive_engine — the service must never open an attacker-chosen path read/write — cost if wrong: none.
Ruling: register_plugin_engine filters built-in/empty extensions BEFORE unregistering the previous registration and returns an application error when nothing remains — otherwise a bad re-registration silently wipes a working one — cost if wrong: none.
Ruling: on a connection whose handshake carried client == "simplefile-service", `host.log` is a local no-op and every other host.* call fails fast with PluginError::Application("host services are not available on the service connection"); HostClient::call gets a default 30 s timeout; a panicking handler yields a -32603 reply — third-party authors must not be able to hang their own backend by logging — cost if wrong: an archive handler cannot report progress through the service connection (phase 2 can add a service-side host relay).
Ruling: Task 16 dispatched while the Task 15 review is pending — Task 16's files (scripts/smoke-winui-ui.ps1, check-winui-packaging.mjs, check-ipc-schema.mjs, ipc/schema/plugin/, .github/workflows/ci.yml, MainWindow.UiSmoke.cs, PluginDiscovery.DefaultRoots env override) are disjoint from a Task 15 fix round — cost if wrong: interleaved commits.
