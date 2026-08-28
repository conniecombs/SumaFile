# 2026-07-17 Remote Drive Removal Markers

Issue:
- Remote-drive and provider-backed mount code is no longer relevant to the
  Windows-only SimpleFile program.
- This pass marks confirmed stale code with `REMOVE_REMOTE_DRIVE` so a later
  removal pass can delete it intentionally.

Status:
- Superseded by `build_notes/2026-07-17-remote-drive-removal-complete.md`,
  which completed the removal of the code marked here.

Scope rule:
- Keep Windows mapped network drives as normal local Windows drive entries.
- Mark app-managed provider auth, provider-backed mount management, FUSE/FTP
  remnants, stale hidden mount UI, and generated audit copies of those remnants.

## Marked For Removal

### Provider Auth Build Hook

Files:
- `src-tauri/build.rs`

Reason:
- The build script still watches `SIMPLEFILE_GOOGLE_CLIENT_ID` and
  `google-oauth-client-id.txt`, then forwards that value as a Rust compile-time
  environment variable.
- No active frontend or backend command consumes this provider auth surface.

Recommended removal:
- Delete the env/file rerun hooks and the compile-time env forwarding block.

### Stale FUSE Mount State Comment

Files:
- `src-tauri/src/state.rs`

Reason:
- `AppState` still had a FUSE/curlftpfs mount-process note, but there is no
  mount-process state field left in the struct.

Recommended removal:
- Delete the stale marker/comment when the remote-drive cleanup pass removes
  the remaining marked mount-management UI.

### Hidden Mount-Management Sidebar Surface

Files:
- `frontend/src/lib/components/layout-shell/SidebarShell.svelte`
- `frontend/src/lib/components/places/NetworkDrivesList.svelte`
- `frontend/src/lib/components/places.ts`
- `frontend/src/css/modules/sidebar-nav.css`
- `frontend/src/css/modules/utilities.css`

Reason:
- The active sidebar now shows drives through the My PC tree.
- The separate `network-drives-section` is hidden with `display:none`.
- `NetworkDrivesList` and `renderNetworkDrivesList` still model a separate
  mount list with unmount controls.
- Utility CSS still includes old mount/FTP placeholder and unmount row helpers.

Recommended removal:
- Delete `NetworkDrivesList.svelte`.
- Remove the `NetworkDrivesList` import, props type, and
  `renderNetworkDrivesList` wrapper from `places.ts`.
- Remove the hidden `network-drives-section` host from `SidebarShell.svelte`.
- Remove `.network-drives-list`, `.mounts-empty-msg`, `.ftp-placeholder`,
  `.ftp-placeholder--error`, `.na-mount-row`, and `.na-unmount-btn` styles if no
  current source references remain.

### Generated Svelte Audit Copies

Files:
- `frontend/src/vanilla-js/generated-svelte/layout-shell.js`
- `frontend/src/vanilla-js/generated-svelte/places.js`
- `frontend/src/vanilla-js/generated-svelte/tree-view.js`

Reason:
- These generated audit artifacts still contain stale copies of the hidden
  mount list, mount-management component, and an `isCloud` disconnect branch.
- The live Svelte source for `TreeView` no longer exposes `isCloud`, but the
  generated audit copy still does.

Recommended removal:
- Either regenerate these audit artifacts from current Svelte source after the
  removal pass or delete the stale generated audit sections if the migration
  audit no longer needs them.

## Explicitly Kept

Files:
- `src-tauri/src/drives.rs`
- `frontend/src/lib/components/layout-shell/SidebarShell.svelte`

Reason:
- Windows mapped network drives remain supported as ordinary Windows drives.
- `WNetGetConnectionW` is used only to label mapped Windows network drives and
  should not be removed with app-managed provider integrations.
- The My PC tree is the current supported drive surface.

## Verification Notes

- Original search target for the follow-up cleanup was `REMOVE_REMOTE_DRIVE`.
- The existing provider-surface guard still passes because `build_notes/` is
  excluded and the source markers avoid reintroducing current-facing provider
  language.
- The follow-up removal pass deleted the marked code and ran the full check
  suite.
