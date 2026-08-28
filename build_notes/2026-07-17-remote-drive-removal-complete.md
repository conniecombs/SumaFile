# 2026-07-17 Remote Drive Removal Complete

Issue:
- The previous pass marked stale remote-drive and provider-backed mount remnants
  with `REMOVE_REMOTE_DRIVE`.
- This pass completes that removal.

## Removed

### Retired Google OAuth Build Hook

Files changed:
- `.gitignore`
- `src-tauri/build.rs`
- `scripts/check-provider-surface.mjs`

Details:
- Removed the `SIMPLEFILE_GOOGLE_CLIENT_ID` Cargo rerun hook.
- Removed the `google-oauth-client-id.txt` Cargo rerun hook.
- Removed compile-time forwarding of the Google OAuth client id into Rust.
- Removed the obsolete ignored credential-file path from `.gitignore`.
- Added provider-surface guard patterns for the old env var and credential file.
- Included `.gitignore` in the provider-surface scan so the removed credential
  file ignore entry cannot silently return.

### Hidden Mount-Management Sidebar Surface

Files changed:
- `frontend/src/lib/components/layout-shell/SidebarShell.svelte`
- `frontend/src/lib/components/places.ts`
- `frontend/src/lib/components/places/NetworkDrivesList.svelte`
- `frontend/src/css/modules/sidebar-nav.css`
- `frontend/src/css/modules/utilities.css`

Details:
- Deleted `NetworkDrivesList.svelte`.
- Removed the `NetworkDrivesList` import, props type, and
  `renderNetworkDrivesList` wrapper from `places.ts`.
- Removed the hidden `network-drives-section` sidebar host.
- Removed CSS for the hidden network-drive list, mount rows, unmount buttons,
  old mount-empty messaging, and FTP placeholder states.

### Generated Svelte Audit Copies

Files changed:
- `frontend/src/vanilla-js/generated-svelte/layout-shell.js`
- `frontend/src/vanilla-js/generated-svelte/places.js`
- `frontend/src/vanilla-js/generated-svelte/tree-view.js`

Details:
- Removed the generated hidden network-drive sidebar host.
- Removed the generated `NetworkDrivesList` component and
  `renderNetworkDrivesList` export.
- Removed the generated `isCloud` tree-node disconnect branch and its
  `simplefile:tree-node-unmount` event.

### Stale Backend State Note

Files changed:
- `src-tauri/src/state.rs`

Details:
- Removed the stale FUSE/curlftpfs mount-process note from `AppState`.

## Explicitly Preserved

Files:
- `src-tauri/src/drives.rs`

Reason:
- Windows mapped network drives remain normal Windows drive support.
- The UNC display-name helpers use `WNetGetConnectionW` only to label mapped
  drives in the My PC drive list.

## Validation Passed

- `rg` searches for the removed identifiers outside `build_notes`.
- `npm run check:provider-surface`.
- `cargo fmt --all -- --check`.
- `npm run check`.
- `cargo test --locked --all-features`.
- `cargo clippy --locked --all-targets --all-features -- -D warnings`.
- `git diff --check`.
