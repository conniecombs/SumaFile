# 2026-07-23 Tauri Renderer Surface Hardening

Issue fixed:
- `build_notes/2026-07-17-next-recommended-work.md` issue 5:
  Tauri global API exposure appeared unused.

## Problem

The active frontend already used typed imports and local wrappers for backend
commands, but `src-tauri/tauri.conf.json` still enabled `withGlobalTauri`.
That exposed the global `__TAURI__` object even though current source did not
need it.

## Files Changed

### `src-tauri/tauri.conf.json`

Changes:
- Set `app.withGlobalTauri` to `false`.

### `frontend/src/lib/tauri.ts`

Changes:
- Kept Tauri command invocation and event listening behind the local wrapper.
- Added `convertFileSource()` so media preview URLs also use the same boundary.
- Re-exported Tauri event callback types for higher-level API helpers.

### `frontend/src/lib/api.ts`

Changes:
- Reads event callback types from the local Tauri wrapper instead of importing
  directly from `@tauri-apps/api`.

### `frontend/src/lib/components/preview-pane/PreviewContent.svelte`

Changes:
- Uses `convertFileSource()` from the local wrapper for audio and video preview
  URLs.

### `scripts/check-tauri-renderer-surface.mjs`

Changes:
- Fails if `withGlobalTauri` is re-enabled.
- Fails if active frontend source uses the global `__TAURI__` API.
- Fails if active frontend source imports `@tauri-apps/api/*` outside
  `frontend/src/lib/tauri.ts`.

### Documentation

Changes:
- Updated README verification and project layout notes.
- Updated the Svelte migration safety rules.
- Updated security and UI/backend review notes for the renderer bridge boundary.

## Validation Passed

Commands:

```powershell
npm run check:tauri-surface
npm run check
npm run check:rust
cargo tauri build --ci --config tauri.local.conf.json --bundles nsis
npm run smoke:release
git diff --check
```

The NSIS build produced:

```text
src-tauri/target/release/bundle/nsis/SimpleFile_1.1.0_x64-setup.exe
```
