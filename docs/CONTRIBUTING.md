# Contributing

This branch targets the Windows SumaFile release. Keep changes aligned with local filesystem workflows, Windows drives, mapped network drives, archive tools, search, previews, metadata, Git status, cleanup tools, updater metadata, and Windows installers.

## Development Setup

```powershell
npm run dev
npm run check
npm run check:winui
npm run check:rust
```

Use Node.js 24 or newer, stable Rust, and .NET SDK 10 or newer.

## Project Layout

- `src-winui/SimpleFile.App` is the WinUI 3 explorer host.
- `src-winui/SimpleFile.Core` owns workspace, menus, transfers, and settings.
- `src-winui/SimpleFile.Ipc` is the named-pipe JSON-RPC client.
- `crates/simplefile-service` is the shipping Rust backend process.
- `crates/simplefile-core` holds reusable domain logic, including tags, smart
  folders, git, cleanup, RAR, updater, and terminal.
- `scripts/` contains release and parity checks.

## Backend Boundaries

Keep IPC methods explicit in `ipc/schema/v1` and mirrored in
`src-winui/SimpleFile.Ipc/Protocol.cs`. After changing a command, run:

```powershell
npm run check:ipc-schema
```

Windows drive behavior belongs in `crates/simplefile-core/src/drives.rs`.
Preserve mapped network share naming through the Windows APIs already in that
module.

## Checks

Before opening a PR:

```powershell
npm run check
npm run check:winui
npm run check:rust
npm run check:security
```

For release or installer changes:

```powershell
npm run check:release
npm run release:build
```

For GitHub-built release candidate artifacts, run the `Release build` workflow
from the Actions tab. It uses the same `release:build` script and uploads the
Windows release artifacts without publishing a GitHub Release.

## Pull Request Notes

- Keep unrelated refactors out of focused fixes.
- Preserve the root `README.md`.
- Update docs when user-visible behavior changes.
- Do not commit local signing keys, private settings, or generated installer outputs.
