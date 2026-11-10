# Plugin system: cloud handoff (2026-11-09)

This folder hands the rest of the plugin-system work (phases 4 and 5, the final review and the CI fixes) from a local Claude Code session to a cloud session on the same branch, `plugin-system-phase-1` (PR conniecombs/SumaFile#57). Everything here was copied from the local git-ignored `.superpowers/` folder. Delete this folder in the last commit, once the work is finished.

## The user's rules

- **Commits:** the cloud session commits and pushes to `plugin-system-phase-1`. It never opens a new PR, never pushes to `main`, and never rewrites history (no force-push, rebase or amend of pushed commits). Before this handoff the user did all commits themselves.
- **Plugin files stay inside the install folder** (`%LOCALAPPDATA%\Programs\SumaFile-WinUI`, a per-user install). This covers user-installed plugins, `plugins-data`, `plugin-approvals.json` and `plugins.log`. Uninstall removes all of them; upgrades keep them. There is no migration from the old `%LOCALAPPDATA%\SumaFile\plugins*` locations. Phase 4 Tasks 1-3 implement this.
- **SDK rulings** (spec §5.1 and §12, review notes "Open decisions"):
  - UI hand-offs stay `object`-typed in the WinUI-free SDK at 1.0.0.
  - Plugin C# projects reference `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` and `CommunityToolkit.Mvvm` compile-only, and use the host's copies at run time.
- **Do not raise the startup budget** (50 ms for the `plugins-discovered` span). Make discovery fit it instead.
- **CI fixes last:** fix CI at the end, except for failures that block reading the remaining CI results.
- **No CPU-burner helper processes.** Never spawn `while($true){}` loops or similar to fake a loaded machine. An agent once left 64 of them pinning the user's PC. Simulate load inside the test process instead, for example with `DOTNET_ThreadPool_ForceMaxWorkerThreads=2`.
- **NuGet is offline only** on the user's machine, so never add a package that is not already in the lock files. cargo can fetch crates.

## Important: what the cloud machine cannot verify

SumaFile is Windows-only:
- the WinUI 3 app and the `net10.0-windows` tests;
- the Credential Manager and the NSIS/WiX installers;
- PowerShell smokes that start the real app;
- Rust code built for the `x86_64-pc-windows-msvc` target.

A Linux cloud container cannot build or run most of this. Treat GitHub Actions as the test bench:
1. Commit each finished task and push it.
2. Read the run with `gh pr checks 57 --repo conniecombs/SumaFile` and `gh run view <id> --log-failed`.
3. Fix what fails before moving on.

The `windows-2025` WinUI host job runs the C# tests, the UI smoke and the startup budget. Check `.github/workflows/ci.yml` for exactly which smokes run. The installer job is skipped on PRs.

The local-only checks are `npm run release:build`, the MSI and NSIS smokes, and the full `-Counts 10000` UI smoke variants. Unless CI runs them, list every one you could not run in the final report, so the user can run them locally.

Whatever does run on Linux, run it before pushing: `npm run check`, `node` script tests, cargo fmt/clippy/tests for crates that build there.

## Where things stand (at the commit that adds this folder)

- **Phases 1-3** are done. Phase 3 is FTP/SFTP as the plugin `com.sumafile.remote`.
- **Phase 4 (Git plugin `com.sumafile.git`)** follows the plan `docs/superpowers/plans/2026-11-09-plugin-system-phase-4-git.md` (16 tasks):
  - **Tasks 1-10 are done**, and each one passed its spec and robustness reviews (reports in `phase-4-reports/`).
  - **Task 11 is partly done.** It is "App: the row-decoration slot (rows, menus, toolbar `when`, startup gate) and the Hello decorations". It was stopped in the middle of its implementation, before its review. Its partial edits are in this commit, including `src-winui/SimpleFile.PluginHost/Contributions/PluginRowDecorationSource.cs`, `PluginRowDecorationSourceTests.cs` and edits across `src-winui/SimpleFile.App`. Re-read the Task 11 section of the plan, check the partial edits against it, and finish it.
  - **Tasks 12-16 are not started:**
    - 12: the Git backend `sumafile-git-backend`
    - 13: the Git plugin UI
    - 14: bundle and pre-approve the plugin
    - 15: remove Git from core
    - 16: the Git and no-plugin smokes, the startup budget, and phase verification
  - **After Task 16**, do a whole-phase review and a fix wave. Use `phase-4-args.json` (`goal`) for the phase's required outcomes and the carry-overs (a)-(h) it must confirm.
- **Two CI-only test failures** happened at `6175d73` (run 42716903378, WinUI host job):
  1. `PluginManagerTests.RunCommand_LoadFailureWhileDisabling_LeavesThePluginDisabled`
     - **Cause:** this is a real product race. A backend start or loss error, or an archive-registration failure, can land after `DisableAsync` has set `State = Disabled` but before teardown detaches the runtime. It then wrote `record.Error` onto the Disabled record.
     - **Fix (in this commit, not independently verified):** `OnBackendStartFailed`, `OnBackendLost` (in `PluginManager.Backend.cs`) and `FaultArchiveRegistrationAsync` (in `PluginManager.ArchiveEngines.cs`) no longer write `Error` while the plugin is Disabled.
     - **New tests:** three deterministic tests, `Disable_Backend*AfterStateSet_LeavesTheRecordClean` and a sibling in `PluginManagerTests.cs`.
     - **To do:** review this fix and confirm it on CI.
  2. `RowDecorationCoordinatorTests.DecorateAsync_LateSourcesThatAnswerNothing_StillDeliverTheFinalMerge(throws: True)` and its sibling `DecorateAsync_SlowSource_PublishesAfterTheBudgetAndAppliesTheLateResult`
     - **Cause:** they use a 50 ms budget with one instant source and one pending source, so on a loaded runner the instant source's `Task.Run` may not start in time.
     - **Status:** a fix was in progress; `RowDecorationCoordinatorTests.cs` has partial edits in this commit.
     - **To do:** finish it deterministically. For example, give the instant source a medium budget such as 2 s while the slow one stays pending. Do not weaken what the tests prove.
- **The startup budget has never been measured on the hosted runner since the phase 3 fix wave.** At `46860dc` the runner measured 74.2 ms against 50 ms. Locally the span is about 15 ms. Every later CI run failed at the C# tests before the budget step.

## Remaining work, in order

1. Push the work so far and get the C# tests green on CI (the two flakes above). Then you can read the startup budget result.
2. Finish phase 4: Task 11, Tasks 12-16, then the whole-phase review and the fix wave. Follow the plan's TDD steps and review each task for spec fidelity and robustness. Deferred minors:
   - `phase-4-deferred-minors.md` (from the Task 1-10 reviews)
   - `phase-3-deferred-minors.md`
   - the phase 3 review's triage in `phases-ledger.md`

   The phase review must triage them all, and anything parked "for phase 4" must be done.
3. Phase 5: scope in `later-phase-scopes.md` ("Phase 5: Third-party readiness"):
   - the plugin template folder;
   - `docs/PLUGINS.md`;
   - `dotnet pack` and `cargo package --dry-run` dry runs;
   - the signing tool;
   - the phase 1 deferred docs.

   Also add this: `scripts/smoke-winui-ui.ps1` must delete its `test-results\ui-smoke\<run>` folder after a passing run and keep it only on failure. 79 leftover runs had filled about 11 GB.

   Items parked "for phase 5" in the ledger:
   - #17/#22: the `Microsoft.Graphics` prefix captures Win2D;
   - #72/#92: `PluginWindowOptions` is never checked;
   - #73: manifest strings are unbounded;
   - #28: no size cap on `plugin.json`;
   - #70/#75: SDK doc wording;
   - the Mvvm host-shared prefix.

   Write a plan to `docs/superpowers/plans/` first.
4. Final whole-tree review (correctness, security, docs) and its fixes.
5. CI: every required check green on `plugin-system-phase-1`, including the startup budget on `windows-2025`. If it is still over 50 ms, use spec §5.2's lever: warm first-use render costs while the file service starts.
6. Delete this `handoff/` folder in the last commit, and give the user a final report:
   - what was done;
   - which Windows-only checks only CI ran, and which nothing ran (they run them locally: `npm run release:build`, MSI/NSIS smokes, `-Counts 10000` UI smokes);
   - anything parked.

## Files in this folder

- `phases-ledger.md`: the running log of every phase, tree id, ruling, parked item and CI result.
- `phase-4-args.json`: phase 4's full goal text, including carry-overs (a)-(h) and the install-folder requirement.
- `later-phase-scopes.md`: the phase 4 and phase 5 scopes.
- `phase-3-deferred-minors.md`, `phase-4-deferred-minors.md`: deferred review minors.
- `phase-3-fix-report.md`: the phase 3 fix wave and its verification evidence.
- `phase-4-reports/`: the Task 1-10 implementation reports.
- `phase-runner.js`, `tools/`: the local workflow script used for phases 2-4, for reference only. Its prompts assume Windows, local verification and "never commit", so do not run it unchanged.

Spec: `docs/superpowers/specs/2026-11-07-plugin-system-design.md`. Review notes and rulings: `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md`.
