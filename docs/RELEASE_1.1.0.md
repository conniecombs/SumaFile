# SimpleFile 1.1.0 Release Notes

Release date: 2026-05-29

> [!NOTE]
> Licensing update (2026-09-02): the current SumaFile source repository is
> licensed under the MIT License. The licensing language below records the
> terms under which SimpleFile 1.1.0 was originally published.
> This file is a historical SimpleFile 1.1.0 snapshot. Current SumaFile
> release validation, packaging, and runtime behavior use the WinUI host plus
> Rust IPC service documented in `README.md` and `docs/RELEASE_1.0.0.md`.

## Summary

SimpleFile 1.1.0 is the first public release from the `SimpleFile-Windows`
repository and supersedes the unreleased local `v1.0.4` tag. It combines the
Svelte/Vite shell migration, release-readiness hardening, Windows installer
smoke coverage, startup-location fixes, safer file-operation conflict behavior,
and the new proprietary project license.

## Licensing at Release

SimpleFile 1.1.0 was originally published as proprietary software. All rights are reserved
by conniecombs. Access to the repository or a copy of the software does not
grant permission to use, copy, modify, redistribute, sublicense, host, resell,
or create derivative works without prior written permission.

Third-party libraries and tools remain governed by their own license terms.

## Installation

Windows users should install the NSIS setup executable:

```text
SimpleFile_1.1.0_x64-setup.exe
```

The MSI is also produced for environments that prefer MSI-based deployment:

```text
SimpleFile_1.1.0_x64_en-US.msi
```

The first release must be installed manually. Settings -> Updates can check for a newer version, but in-app install is disabled until installer signatures exist. Download later releases from GitHub.

## Major Changes

- Svelte/Vite frontend shell is now the release entry point.
- File-list column resizing now matches Windows File Explorer behavior.
- Start Location -> Custom Path now supports selecting and persisting a path.
- Copy, cut, paste, drag/drop, and dual-pane transfers now flow through the
  transfer manager with stable operation IDs and progress events.
- Copy and move conflict paths are covered so existing destination files are not
  silently overwritten.
- Release build tooling now includes local unsigned bundle support plus release
  executable, MSI, NSIS installer, and settings startup smoke tests.
- Project metadata, README, updater configuration, support links, release docs,
  and contribution terms now point at `conniecombs/SimpleFile-Windows`.

## Validation

The local 1.1.0 release candidate was validated with:

```powershell
npm run check:release
npm run smoke:settings
npm run build:tauri:local
npm run smoke:release
npm run smoke:installer
```

`npm run check:release` includes Svelte checks/builds, legacy frontend checks,
Tauri invoke parity checks, updater configuration checks, Rust formatting, Rust
tests, Clippy with warnings denied, and Rust dependency audit.

## Release Artifacts

The GitHub release workflow builds:

- Windows x64 NSIS setup executable
- Windows x64 MSI installer
- Signed Windows updater artifacts and `latest.json`

The release workflow requires the GitHub secret `TAURI_SIGNING_PRIVATE_KEY`.
If the signing key is password-protected, it also requires
`TAURI_SIGNING_PRIVATE_KEY_PASSWORD`.

## Known Operational Notes

- The local `build:tauri:local` command intentionally disables updater artifact
  signing through `src-tauri/tauri.local.conf.json`; it is for local packaging
  validation only.
- The real GitHub release workflow keeps updater signing enabled and fails if
  the signing secret is missing.
- Older Git tags and commits preserve the licensing terms that applied at the
  time. The current SumaFile source repository is licensed under MIT.
