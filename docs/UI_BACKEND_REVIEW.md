# UI And Backend Review

## Current State

The Windows-focused branch ships a WinUI 3 host plus a Rust named-pipe IPC
service. The active surface includes drive listing, directory browsing,
dual-pane navigation, tabs, transfers, archive operations, search, smart
folders, previews, metadata, checksums, Git status, cleanup tools, updater
actions, and Windows installer support.

## Strengths

- The 76-command IPC schema is checked by `npm run check:ipc-schema` against
  `SimpleFile.Ipc.Protocol` and `crates/simplefile-service/src/dispatch/`.
- WinUI parity-gate required rows stay `PASS` or `WAIVED`.
- Windows drive display names use native volume and mapped-share lookups.
- Directory opens from file list, tree, and breadcrumb events stay in-app.
- Archive paths are handled before normal filesystem commands.
- Release checks cover updater metadata and workflow configuration.

## Risks

- Large local folders can still make metadata operations expensive.
- Installer and updater behavior need smoke testing on real Windows machines
  before release.

## Recommended Checks

```powershell
npm run check
npm run check:winui
npm run check:rust
npm run check:security
npm run build:winui:release
```
