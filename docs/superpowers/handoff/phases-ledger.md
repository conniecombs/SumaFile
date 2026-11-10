# Phases 2-5 ledger (local, uncommitted)

Branch plugin-system-phase-1 @ 9a1393c. User rule: complete phases 2-5 locally; never commit, push, stash, reset, switch branches or open PRs.
Baseline tree (HEAD): b6e94f62e250f5f6506bea8971e05a79edf7d55a
Snapshot tooling: .superpowers/tools/snap.sh (tree id via throwaway index), snapdiff.sh BASE HEAD OUT.

## Approach
- Baseline workflow wf_0553bdc9-58a: reproduce/diagnose/fix the CI plugin-smoke crash (0xC000027B), uncommitted.
- Phase runner script: R:\Temp\claude\c48dfaf19294613c\4e549058-5b59-447a-927b-0fc453a48aa7\scratchpad\phase-runner.js — Understand (parallel maps) → Plan (writing-plans structure, snapshots instead of commits) → Critique (coverage/integration + consistency critics, revision) → Implement (per task: implementer, 2 lens reviewers, ≤3 fix rounds with scoped re-review) → Verify (full suites + checks + smokes, ≤3 fix rounds) → Phase review (max effort, one fix wave, scoped re-review).
- One workflow per phase, sequential (all agents mutate the same checkout); ledger updated between phases with tree ids, parked items and minors.

## Change of order (user)
- Baseline CI-crash workflow wf_0553bdc9-58a stopped during Diagnose at the user's request: fix CI once after all phases. Findings so far: crash does NOT reproduce locally (5/5 smokes passed at 9a1393c incl. 3x plugin smoke, release + debug service built). Hypothesis agents (recent-diff, ci-environment, teardown) were cut off; rerun the baseline workflow at the end.

## Resume documents (user request)
- Root master: PLUGIN_PHASES_PLAN.md (regenerated every ~4 s: status, resume steps, per-phase task checklists, scopes, deferred CI item).
- Root plan copies: PLUGIN_PHASE_<N>_PLAN.md (phase 2 mirrors docs/superpowers/plans/2026-11-08-plugin-system-phase-2-sevenzip.md; phases 3-5 will write plans to the root directly).
- Durable copies of the machinery: .superpowers/tools/phase-runner.js, .superpowers/tools/statuspage/{gen.py,plan.json}, .superpowers/phase-args/phase-2.json and later-phase-scopes.md.

## Phase 2: 7-Zip plugin — DONE
- Workflow wf_629232b9-7bd (64 agents). Plan: docs/superpowers/plans/2026-11-08-plugin-system-phase-2-sevenzip.md (root mirror PLUGIN_PHASE_2_PLAN.md). Reports: .superpowers/phase-2-sevenzip/.
- 13/13 tasks DONE; full verify passed; nothing parked; phase review: no Critical, 2 Important fixed in the fix wave (7za case-insensitive include/rename matching), re-review clean.
- Base b6e94f62 -> final 350cc06f (snapshot before phase 3: 36636883, adds only the regenerated root docs).
- Carried into phase 3 (fix first): bundled.json $OFS join, begin_progress after close, BundledPluginList BOM, MarkLoadFailedAsync state, LoadUnmanagedDll, review-notes bookkeeping, startup budget headroom, full verify of the phase 2 fix wave.
- Deferred to the final review / CI pass: engine full extract of names differing only by case collides on disk (phase-fix-report Concern 1); read_entry holds a pool connection without cancel (pool exhaustion -> "busy"); replace = delete+add; progress relay has no throttle; relative binaryOverride resolution; extra stat-walk per listing; register-after-stop race; timing-sensitive C# tests; DisableAsync before Set; Task 9/10/12/13 cosmetic items; NSIS/upgrade smokes not yet run; Settings > Tools shows the plugin id instead of its name.

## Phase 3: FTP/SFTP plugin — STARTED
- Args: .superpowers/phase-args/phase-3.json. Plan: PLUGIN_PHASE_3_PLAN.md (root). Base tree 36636883.
- Workflow run: wf_c411160a-ed4 (task wigkl5dxc).

## Pause for a user commit (2026-11-09)
- User: "Get to a place where I can push a commit safely" -> chose "Phase 2 + phase 3 so far".
- Phase 3 run wf_c411160a-ed4 stopped after Task 12 implement (tree 3b0bd8c4); Tasks 1-11 reviewed DONE; Task 12 review not run. Tasks 13-15 remain.
- Phase 3 plan moved to docs/superpowers/plans/2026-11-09-plugin-system-phase-3-remote.md (root PLUGIN_PHASE_3_PLAN.md is now a generated mirror); phase-3.json planPath updated.
- Root PLUGIN_PHASES_PLAN.md and PLUGIN_PHASE_*_PLAN.md added to .git/info/exclude (local resume docs, not for commit).
- Commit-readiness workflow wf_f057f121-fa4 (task wlhp68kbu): Task 12 review, interim coherence, git hygiene, CI preflight, fixes, full verify, checklist at .superpowers/commit-readiness/CHECKLIST.md and COMMIT_MESSAGE.txt.
- Resume phase 3 afterwards with a continuation run for Tasks 13-15 + verify + phase review (not a resume of wf_c411160a-ed4).
- Result: READY. 1 fix round (rusqlite 0.40.2 -> 0.41.1 for RUSTSEC-2026-0459, CI Security job). Full verify passed on tree df929072 (cargo 642, dotnet 1154, npm check, UI smokes x3, release:build + startup/file-ops/MSI payload smokes). Checklist .superpowers/commit-readiness/CHECKLIST.md, message COMMIT_MESSAGE.txt.

## Phase 3 resumed (2026-11-09)
- User committed and pushed 46860dc "plugin architecture" (tree df929072 = readiness tree). Standing rule unchanged: I never commit.
- Phase runner gained a resume mode (args.resume: fromTree, tasks, priorMinors). Run wf_11ecb8bf-901 (task wjkcsig7q): Tasks 13-15, verify, whole-phase review from base 36636883. Args .superpowers/phase-args/phase-3-resume.json; prior minors .superpowers/phase-3-remote/prior-minors.md.

## CI at 46860dc (Auto-fix event, 2026-11-09)
- WinUI host failed at the startup budget step: plugins-discovered 74.2 ms vs 50 ms on the runner (count=3); the plugin UI smoke passed (no 0xC000027B this run). CI required fails as a consequence. Other jobs passed.
- Not fixed or pushed now: the user deferred CI fixes until all phases are done, and phase 3 Tasks 13-15 are mid-flight in the working tree, so a commit now would sweep in partial work. Fed into the phase 3 review via prior-minors.md; otherwise part of the final CI pass.

## Phase 3: FTP/SFTP plugin — DONE (wf_11ecb8bf-901)
- Tasks 13-15 DONE; full verify passed (cargo 582, dotnet 1143, npm run check, all UI smokes incl. end-to-end remote and no-plugin, release:build, MSI). Final tree f77c05f0.
- Phase review fix wave: startup span 20.6 -> 15.0 ms locally (laps split printed by the budget check). Not yet proven on the hosted runner (74.2 ms at 46860dc); projected 38-54 ms. Deferred to the final CI pass; lever: warm first-use render cost while the service starts (spec 5.2).
- Open for the user before phase 4: SDK object-typed UI hand-offs (A keep / B WinUI-typed SDK) and compile-only host packages in plugin C# projects. See .superpowers/phase-3-remote/phase-fix-report.md.
- Deferred minors: phaseReviewMinors in the workflow result (manager close during transfer leaks a session; blob-format pin test; DialogHost gate on host-closed plugin windows; smoke credential cleanup; developer's real remote-profiles.db import marker; small leftovers). Parked to phase 4: #82, #78/#84, #65/#69, #63. Phase 5: #17/#22, #72/#92, #73, #28, #70/#75, Mvvm prefix.

- User rulings 2026-11-09: keep object-typed SDK UI hand-offs at 1.0.0; accept compile-only host packages (WinAppSDK, BuildTools, Mvvm). Recorded in spec 5.1/12 and the review notes.

## Phase 4: Git plugin — STARTED
- Args: .superpowers/phase-args/phase-4.json. Plan: docs/superpowers/plans/2026-11-09-plugin-system-phase-4-git.md. Base tree 3e381e55.
- Workflow wf_3337173a-a6e (task w0jl7r0q0).
- Stopped wf_3337173a-a6e during write-plan (no code edits) to add a user requirement: no plugin file outside the install folder (user chose all plugin files: user plugins, plugins-data, approvals, plugins.log; uninstall removes everything; no migration from the pre-release %LOCALAPPDATA% locations). Folded into phase-4.json as the first tasks.
- Relaunched as wf_6f3618eb-9f5 (task weoi3j33m).
- User request (16:33): before phase 5 starts, clear unnecessary files safely. Plan once phase 4 is done and no build or smoke is running: cargo clean (target, ~33 GB, regenerable; includes target/plugins and fixture-plugins, restaged by scripts); delete all test-results/ui-smoke run folders. Keep the C# bin/obj (offline NuGet restore, only 1.3 GB), dist (latest installers), .superpowers reports and R:/Temp/claude (workflow resume data). Then rebuild and restage before phase 5 verifies.
- CI at a57c4f0 (user commit 'Plugin stuff', phase 4 Tasks 1-9): WinUI host failed on RowDecorationCoordinatorTests.DecorateAsync_FirstPluginInOrderWinsTheRowText_AndEveryPluginCountsForHasDecoration (null text: 50 ms test budget vs Task.Run start on a loaded runner). Fixed locally in the test file (ShortBudget only for pending-source tests, AmpleBudget 5 s otherwise, Watchdog 10 s); 15/15 x3. Not committed. Startup budget step was not reached in that run.
- User pushed 6175d73 'Add dockable plugin panels' (phase 4 Tasks 1-9 plus Task 10 before its review fix round, and the RowDecoration test fix). CI run 42716903378 started. Working tree now holds only the Task 10 fix-1 edits.
- CI at 6175d73 (run 42716903378): WinUI host failed on 2 CI-only flakes: RowDecorationCoordinatorTests.DecorateAsync_LateSourcesThatAnswerNothing (ShortBudget with an instant source) and PluginManagerTests.RunCommand_LoadFailureWhileDisabling_LeavesThePluginDisabled (backend start error lands on a Disabled record). Fix workflow wf_ff2d389a-cbc (task wxl1kcg71) running locally; not committed. Startup budget step again not reached.
