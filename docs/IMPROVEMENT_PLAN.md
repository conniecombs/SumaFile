# Improvement Plan

**Historical:** file paths under `frontend/` and `src-tauri/` below describe
the pre-retirement Svelte/Tauri tree. The shipping host is now WinUI 3 +
`simplefile-service`. Product goals in this plan still apply.

This document expands the seven highest-value improvement areas for the
Windows-focused SimpleFile branch. The intent is to keep the product direction
tight: local files, Windows drives, mapped network drives, archives, search,
previews, metadata, Git helpers, cleanup tools, and Windows installers.

The current baseline is healthy. The standard repo checks passed during the
review that produced this plan:

```powershell
npm run check
npm run check:rust
```

## 1. Tighten Archive Extraction Safety For TAR And RAR

Status: Completed in the current worktree.

### Why This Matters

SimpleFile is a file manager, so archive extraction is one of the highest-risk
surfaces. A crafted archive should never be able to write outside the selected
destination, create Windows alternate data streams, or create ambiguous
drive-relative paths.

ZIP handling already has stronger Windows-specific validation. TAR and RAR
entry handling should match it so every supported archive format follows the
same safety contract.

### Completed Change

- ZIP, TAR, and RAR entry normalization share
  `archive_entry_relative_path_from_name`.
- Shared validation rejects parent traversal, absolute/rooted entries, Windows
  drive-relative names (`C:evil.txt`), ADS-like components (`file.txt:stream`),
  reserved device names, invalid Windows filename characters, empty paths, and
  NUL bytes.
- Listing skips unsafe entries and reports them in `unsafe_entries`; extraction
  fails loudly on unsafe paths.
- Extraction also runs `ensure_extract_path_within_destination` after path join
  (and after collision rename) so zip-slip style escapes cannot write outside
  the chosen destination even if relative validation is bypassed.
- TAR extraction only unpacks regular files and directories (symlinks / special
  nodes are skipped).

### Files To Inspect

- `src-tauri/src/archive.rs`
- `src-tauri/src/fs_ops.rs`
- `src-tauri/src/progress.rs`

### Verification

Add Rust tests for:

- ZIP rejects `C:evil.txt`.
- TAR rejects `C:evil.txt`.
- RAR path validation rejects `C:evil.txt` or equivalent validator input.
- TAR rejects `foo:bar.txt` on Windows.
- RAR path validation rejects `foo:bar.txt` on Windows.
- Existing nested safe archive paths still extract correctly.

Run:

```powershell
npm run check:rust
npm run check
```

## 2. Make Backend Filename Validation Fully Windows-Aware

Status: Completed in the current worktree.

### Why This Matters

The frontend rejects Windows-invalid filename characters, but the backend is the
real security and correctness boundary. If any future command path bypasses the
frontend helper, the backend should still reject invalid names before calling
filesystem operations.

### Completed Change

`validate_name` in `src-tauri/src/utils.rs` rejects empty/whitespace names,
`.` / `..`, separators, Windows-invalid characters, control characters,
reserved device names (with or without extension), and trailing spaces/periods.
Focused Rust unit tests cover the policy.

### Files To Inspect

- `src-tauri/src/utils.rs`
- `frontend/src/lib/coreFileManager.ts`
- `src-tauri/src/fs_ops.rs`
- `src-tauri/src/archive.rs`
- `frontend/src/lib/app/advanced_rename.ts`

### Verification

Add Rust tests for backend validation:

- Rejects `bad:name.txt`.
- Rejects `CON`, `CON.txt`, `NUL`, `COM1`, and `LPT9.log`.
- Rejects `trailing.` and `trailing `.
- Rejects control characters.
- Accepts normal names such as `Report 2026.txt`.

Then run:

```powershell
npm run check:rust
npm run check
```

## 3. Consolidate Symlink Copy Handling

Status: Completed in the current worktree.

### Why This Matters

Symlink handling is easy to get subtly wrong on Windows. The app has already
been hardened to operate on symlinks themselves for delete, rename, and move
paths. Copy behavior should be just as consistent.

### Completed Change

Shared `recreate_symlink` in `src-tauri/src/utils.rs` classifies targets
relative to the symlink parent, preserves relative link text, reports
destination conflicts, and is used from both `fs_ops` and `progress` copy
paths. Unit tests cover relative file/dir links and conflicts.

## 4. Improve Huge-Folder Responsiveness

Status: Completed in the current worktree.

### Why This Matters

Large folders are a core file-manager stress case. The current UI already has
good feature coverage, but rendering and metadata work can still become costly
when a directory has thousands of entries.

The goal is not to add more features first. The goal is to make existing
navigation, filtering, selection, thumbnails, preview, and folder metrics stay
responsive under heavy local folders and network drives.

### Current Evidence

- `frontend/src/lib/components/file-list/FileList.svelte` maps all filtered
  entries into display items.
- `frontend/src/lib/components/file-list/FileListItems.svelte` has a `virtual`
  mode branch, but it still renders all `items`.
- Roadmap and review docs already call out large-folder progress, cancellation,
  and expensive metadata work as areas to improve.

### Completed Change

- `FileList.svelte` virtualizes both list and grid modes: visible range from
  scroll position, overscan rows, spacer height, and `translateY` windowing so
  only the on-screen slice is mapped into DOM items.
- Keyboard focus changes call `scrollIndexIntoView` so arrow / type-ahead
  navigation keeps the focused row in the viewport.
- `fileListLazyData.ts` loads image thumbnails only for the current visible
  grid window (batched, cached, cancelled on navigation / large icon-size
  changes).
- Visible folders passively calculate size (when **Calculate Folder Sizes** is
  enabled) and item counts (when the Items column is visible), with concurrency
  limits, in-flight dedupe, and cancel tokens shared with explicit metric work.
- Navigation bumps freshness tokens, cancels in-flight size/count work, and
  clears the thumbnail cache so stale results cannot paint after a path change.
- Git status enrichment remains non-blocking after `list_directory` with
  navigation-token guards on primary and secondary panes.
- Explicit folder-metrics progress text still reports
  `Folder N of M: name` with cancel support.

### Files To Inspect

- `frontend/src/lib/components/file-list/FileList.svelte`
- `frontend/src/lib/components/file-list/FileListItems.svelte`
- `frontend/src/lib/app/core.ts`
- `src-tauri/src/fs_ops.rs`
- `src-tauri/src/progress.rs`
- `src-tauri/src/preview.rs`
- `src-tauri/src/metadata.rs`

### Verification

Add or run manual smoke tests with disposable folders containing:

- 1,000 files.
- 10,000 files.
- Mixed image and non-image files.
- Nested folders where folder size calculation can be cancelled.
- A mapped network drive or network-like path if available.

Run:

```powershell
npm run check
npm run check:rust
```

Also verify that scrolling, selection, `Ctrl+A`, type-ahead, sorting, and dual
pane navigation still behave correctly.

## 5. Retire Remaining Migration Glue

Status: Completed in the current worktree.

### Why This Matters

The Svelte migration is documented as complete, but some compatibility pieces
remain. They are useful as guard rails during migration, but over time they
become maintenance cost: static HTML overlay injection, generated audit bundles,
`@ts-ignore` imports, and broad `any` state make future refactors harder to
review safely.

This is a code-health improvement that should reduce drift and make later UI
work easier.

### Completed Change

- `frontend/src/App.svelte` now renders the native
  `OverlayShell.svelte` component instead of injecting `legacyOverlayMarkup`.
- `frontend/src/lib/components/legacy-overlays.ts` and
  `legacy-shell-template.html` are retired.
- `frontend/src/lib/app/localState.svelte.ts` uses explicit local state types
  for overlay paths, operation IDs, timers, progress cancellation, and advanced
  rename plans.
- The runtime helpers were converted from imported plain JavaScript files to
  typed TypeScript sources under `frontend/src/vanilla-js/runtime/`.
- Generated Svelte audit artifacts under
  `frontend/src/vanilla-js/generated-svelte/` are retired.
- Migration and behavior-bridge checks now verify live Svelte source contracts
  and fail if the retired artifacts or raw overlay HTML sink return.

### Remaining Follow-Up

Progress, generic modal/settings, search chrome, and archive create/list are
component-owned (`progressUi`, `modalUi`, `searchUi`, `archiveUi` + matching
Svelte modals). Advanced rename and Quick Look still preserve DOM IDs for
workflow controllers; a later slice can migrate those the same way.

### Files To Inspect

- `frontend/src/App.svelte`
- `frontend/src/lib/components/OverlayShell.svelte`
- `frontend/src/lib/app/localState.svelte.ts`
- `frontend/src/vanilla-js/runtime/state.svelte.ts`
- `frontend/src/vanilla-js/runtime/startup-location.ts`
- `frontend/scripts/check-behavior-bridges.mjs`
- `frontend/scripts/check-migration-complete.mjs`

### Verification

After each slice:

```powershell
npm run check
```

For overlay replacement, manually verify:

- Context menu.
- Generic modal.
- Progress modal.
- Quick Look.
- Archive modal.
- Advanced rename.
- Keyboard shortcuts modal.
- About modal.

## 6. Add Installer Smoke Coverage To CI Or A Scheduled Workflow

Status: Completed in the current worktree.

### Why This Matters

The normal CI builds the Rust backend, but packaging failures can still appear
only when Tauri creates NSIS/MSI installers. Since SimpleFile ships as a Windows
desktop app, installer health is part of product health.

The repo already has good smoke scripts. The improvement is to run them earlier
and more consistently.

### Completed Change

- Added `.github/workflows/installer-smoke.yml` with:
  - Manual `workflow_dispatch`
  - Nightly schedule (`0 6 * * *` UTC)
  - Windows x64 local Tauri package build via `npm run build:tauri:local`
    (`tauri.local.conf.json`, no updater signing)
  - `npm run smoke:release`, `smoke:msi`, and `smoke:installer`
  - Artifact upload of NSIS/MSI outputs for debugging
- `scripts/check-github-workflows.mjs` asserts the smoke workflow stays wired.

Full packaging is intentionally not on every PR (cost). Run the workflow
manually before a release, or rely on the nightly run.

### Files To Inspect

- `.github/workflows/ci.yml`
- `.github/workflows/release.yml`
- `scripts/smoke-release-startup.ps1`
- `scripts/smoke-msi-artifact.ps1`
- `scripts/smoke-nsis-install.ps1`
- `src-tauri/tauri.local.conf.json`
- `src-tauri/tauri.conf.json`

### Verification

The workflow should prove:

- Tauri can build NSIS and MSI artifacts.
- The unpacked release executable launches and exposes the expected window
  title.
- The MSI contains `simplefile.exe` and launches after administrative
  extraction.
- The NSIS installer installs silently, writes an uninstall entry, launches the
  installed executable, and uninstalls cleanly.

## 7. Add Persisted Layouts And Shortcut Customization

Status: Completed in the current worktree.

### Why This Matters

A file manager is used repeatedly, so tabs, panes, columns, preview visibility,
icon size, and keyboard shortcuts should feel personal and durable.

### Completed Change

- Workspace layout persistence via `simplefile-workspace-layout` (tabs, active
  pane, dual-pane, paths, columns, preview, icon size, view mode).
- Tabs no longer dual-write to legacy `simplefile-tabs`; workspace layout is
  the single source of truth (legacy keys migrate once then clear).
- Shortcut registry + Settings → Shortcuts customization with overrides,
  conflict detection, and reset-to-default.

### Remaining Optional Follow-Ups

- Advanced rename / Quick Look off remaining DOM IDs (see `fixit.txt`).
- Folder-metrics progress polish for huge trees.

## Suggested Implementation Order

Historical order (all primary items complete; see `fixit.txt` for remaining
medium-priority work):

1. Archive extraction safety — done.
2. Backend Windows filename validation — done.
3. Shared symlink-copy helper and tests — done.
4. Huge-folder virtualization and lazy metadata — done.
5. Migration-glue retirement — done (search/archive included).
6. Installer smoke workflow — done.
7. Persisted layouts and shortcut customization — done.
