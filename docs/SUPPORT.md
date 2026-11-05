# Support

Use this file when collecting information for SumaFile 1.0.2 Windows support issues.
See [release notes](RELEASE_1.0.2.md) for upgrade behavior and known limitations.

## Before Reporting

1. Record the installed version or development commit from `main`; the current release target is v1.0.2.
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

## Transfer and UI timeout reports

Include the transfer mode, concurrency/buffer limits, integrity and retry settings,
source/destination storage types, and whether the operation was a copy or move.
Record committed/pending outcomes and any recovery message. Preserve ownership
records and replacement backups for diagnosis; filenames alone do not establish
which data may be discarded. Redact paths and account details before sharing.
For UI smoke failures, include both `report.json` (if written) and `progress.json`
from `test-results/ui-smoke/<run>/`, plus the workflow run URL. The checkpoint
identifies the last active scenario when the process timeout prevents a final report.
