# Support

Use this file when collecting information for SumaFile Windows support issues.

## Before Reporting

1. Confirm you are on the `Windows-Focused` branch or a Windows release built from it.
2. Run `npm run check` when reporting a development issue.
3. For installer issues, also run `npm run smoke:winui-msi`, `npm run smoke:winui-installer`, and `npm run smoke:winui-upgrade`.
4. For startup issues, capture whether the app window appears, whether settings load, and whether the updater check starts.

## Include In Reports

- Windows version and architecture.
- SumaFile version from the About dialog.
- Whether the issue is in the dev app, unpacked payload, NSIS installer, or MSI installer.
- The exact folder path involved, with personal details redacted.
- Whether the path is local storage, removable media, or a mapped network drive.
- Any console output, Rust panic output, or installer smoke-test output.
- Relevant `%LOCALAPPDATA%\SumaFile\startup.log`, `%LOCALAPPDATA%\SumaFile\service.log`, or `%LOCALAPPDATA%\SumaFile\operations.jsonl` entries, with personal paths redacted.

## Common Areas

- Drive names and mapped network shares: check `crates/simplefile-core/src/drives.rs`.
- Folder navigation: check `src-winui/SimpleFile.Core/ExplorerWorkspace.cs` and `crates/simplefile-core/src/preview.rs`.
- Settings startup behavior: check `src-winui/SimpleFile.Core` settings/workspace persistence.
- Archive behavior: check `crates/simplefile-core/src/archive/`.
- Release and updater behavior: check `.github/RELEASE.md`, `docs/UPDATER_RELEASE.md`, and `scripts/check-updater-config.mjs`.
