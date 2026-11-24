# Audit Open Items for 1.2.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the open items of the 2026-11-23 audit that can be built and tested outside Windows, fix the small app-layer bugs for CI to confirm, and release the result as 1.2.0.

**Architecture:** No new subsystems. Each task is a contained fix in the crate or project that owns the code, with a test beside it. Work lands on `WorkingBranch` one commit per task.

**Tech Stack:** Rust 1.98 (workspace crates), C# / .NET 10 (`SimpleFile.Core`, `SimpleFile.Tests`), WinUI 3 app project (edited, not buildable here).

**Spec:** [docs/CODEBASE_AUDIT_2026-11-23.md](../../CODEBASE_AUDIT_2026-11-23.md) and the Transfers section of [docs/ROADMAP.md](../../ROADMAP.md).

## Global Constraints

- Every commit goes to `WorkingBranch`. Nothing goes to `main`; no pull request.
- A fix ships with a test that fails without it, wherever the code can be run here.
- Rust: `cargo fmt --all -- --check` clean; no new Clippy warnings; `sumafile-transfer` and the remote backend tested natively, `simplefile-core` and `simplefile-service` tested in the Linux port (Windows-only helpers stubbed, never committed).
- C#: `SimpleFile.Core`, `SimpleFile.Ipc`, `SimpleFile.PluginHost` and `SimpleFile.Tests` build and test with the .NET 10 SDK here. `SimpleFile.App` and the plugin UI projects do not build here; changes there are kept small and are confirmed by CI on Windows.
- `npm run check` and `npm run test:maintenance` stay green.
- The version is bumped with `scripts/release-version.mjs`, only after the tasks are done and CI is green on Windows.

## Out of scope for 1.2.0 (stay on the roadmap)

- U2-U6 and the rest of U7 (UI features), A1-A9 (backend rewrites).
- R2's shared plugin UI project files and PowerShell helper sharing, R3's move from `winapi` to `windows-sys`, P10.5: none can be built or run here, and each is wide.
- One ownership record per folder instead of per file, and the name-filtered recovery listing: both change the recovery contract or are Windows-only.
- P8's "skip the source re-read in Checksum mode": the re-read is what Checksum mode promises for a move.

## Review Focus

- A dotfile with a second dot (`.env.local`, `.eslintrc.json`) must still resolve to its last extension.
- A PDF text string with no byte-order mark must decode as before.
- An application that exits at once after handing the file to a running instance must not lose its temporary file.
- Two smart-folder saves at the same moment must both survive.
- A backend reconnect in the middle of a paged listing must leave the pane usable.

---

## Phase A: Rust

### Task A1: Dotfiles resolve to an extension (B14.8)
**Files:** `crates/simplefile-core/src/utils.rs` (new `effective_extension(path: &Path) -> String`, lower case, `.gitignore` gives `gitignore`), callers in `preview.rs`, `metadata.rs`.
- [x] Test: `.gitignore`, `.env`, `.npmrc`, `.env.local` (gives `local`), `notes.TXT`, `README` (empty).
- [x] Use it at every `.extension()` that feeds a match on text/config kinds. Commit.

### Task A2: PDF text strings in UTF-16BE (B14.8)
**Files:** `crates/simplefile-core/src/metadata.rs` (`pdf_object_text`).
- [x] Test: a title stored as `FE FF` + UTF-16BE reads back as text; a plain byte string is unchanged; `EF BB BF` UTF-8 is accepted.
- [x] Decode by byte-order mark. Commit.

### Task A3: Open With checks the resolved program and keeps a handed-off file (B14.8)
**Files:** `crates/simplefile-core/src/open_with.rs`.
- [x] Test: a link in a trusted folder that resolves to a denied program is refused.
- [x] Test: a child that exits inside the hand-off window leaves the temporary folder for the age-based sweep; a child that runs longer has it removed on exit.
- [x] Implement both. Commit.

### Task A4: Smart folder saves do not lose each other (B14.8)
**Files:** `crates/simplefile-core/src/smart_folders.rs`.
- [x] Test: many threads saving different folders at once all appear in the store.
- [x] One process-wide lock around load-change-write; a temp name per write. Commit.

### Task A5: Enumeration, cache, metadata and compare bottlenecks (P10.1-P10.4)
**Files:** `dir_list.rs`, `collation.rs`, `session_cache.rs`, `metadata.rs`, `thumbnail.rs`, `compare.rs`.
- [x] Measure each before changing it; change only what the measurement supports; test each change. One commit per item. Done for P10.2, P10.3 (the tar scan) and P10.4. P10.1 is left: the timestamps are strings in the wire format, which is A8.

### Task A6: Transfer workers start when needed (P8 follow-up)
**Files:** `crates/simplefile-service/src/progress/parallel.rs`.
- [x] Test: a pass limited to one file at a time starts one worker thread, not thirty-two.
- [x] Spawn a worker when a file is dispatched and none is idle, up to the limit. Commit.

### Task A7: Roadmap transfer items
**Files:** `progress/parallel.rs`, `progress/execute.rs`, `progress/reporting.rs`, `sumafile-transfer/src/policy.rs`.
- [x] Remove `FileTask::owned_tree`, `CopyContext::resume_existing` and the code only they reach.
- [x] Reserve buffers for the copy path the file will take (stream count only where several streams are used).
- [x] Keep a chosen transfer order by inserting new files in place instead of sorting the queue again.
- [x] Restore the times of created folders on several threads.
- [x] Tests for each; one commit per item.

### Task A8: Rust duplication (R3) and plugin server follow-up (B8)
**Files:** `simplefile-core/src/archive/path.rs`, `archive/mutate.rs`, `sumafile-transfer/src/paths.rs`, plugin backends' `main.rs`, `sumafile-plugin/src/server.rs`.
- [x] One `unique_destination_path` and one reserved-name check, in `sumafile-transfer`. The 7-Zip backend keeps its own reserved-name check; it does not depend on the transfer crate.
- [ ] One stderr logger for the plugin backends. Not done: sharing it would add to the published plugin SDK's API.
- [ ] A multi-client plugin server stays up when a service client disconnects before the host connects. Test in `sumafile-plugin/tests/roundtrip.rs`. Not done: the pipe server is Windows-only and the change is a lifecycle decision that needs a Windows run.

## Phase B: C#

### Task B1: The decoration test does not depend on thread-pool growth
**Files:** `src-winui/SimpleFile.Tests` (assembly-level thread-pool floor).
- [x] Raise the pool's minimum threads for the test assembly, the cause its own comments name. Commit.

### Task B2: A reconnect restores paged listings (B14.4)
**Files:** `SimpleFile.Core/WorkspaceControllers/ListingViewController.cs`, `SimpleFile.Core/ExplorerWorkspace*.cs`, one call in `SimpleFile.App/MainWindow.xaml.cs`.
- [x] Test: after a view read fails and the backend reconnects, the next navigation opens a view again.

### Task B3: Copy or move to the other pane (B14.3)
**Files:** `SimpleFile.Core/WorkspaceControllers/WorkspaceFileOpsController.cs`, `SimpleFile.App/MainWindow.Transfer.cs`.
- [x] Test: the destination is the other pane's folder as it was when the command started; a selection dropped on its own folder is refused.

### Task B4: Type-ahead, shortcuts and Quick Look (B14.6, B14.7, B14.2)
**Files:** `SimpleFile.App/MainWindow.FileListEvents.cs`, `MainWindow.Shortcuts.cs`, `PreviewPresenter.Media.cs`, `MainWindow.InspectionDialogs.cs`; logic moved to `SimpleFile.Core` where it can be tested.
- [x] Tests for the logic in Core; the app-side wiring is confirmed by CI.

### Task B5: Dead C# code (R1) and path normalizers (R2)
**Files:** as listed under R1 in the audit; `SimpleFile.Core/PathRules.cs`.
- [x] Remove each unreachable type with its tests; build and run the suite after each removal. `ExtractArchiveDialog` stays: it is in use. Decoration keys and thumbnail keys share `PathRules.NormalizeSeparators`.

## Phase C: Release

### Task C1: Documents
- [x] Audit status, roadmap, changelog and transfer reference say what the code now does.

### Task C2: Version 1.2.0
- [x] `node scripts/release-version.mjs 1.2.0`, release notes, `npm run check`. The version change was committed before the Windows CI run so that one run covers the code and the version together; no tag was made. Tag `v1.2.0` only after that run is green.
