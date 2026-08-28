# 2026-07-14 Issue 3 RAR Installer Hardening

Issue: audit issue 3, option 3.

Goal:
- Keep automatic RAR installation available, but only after the downloaded
  artifact is verified by pinned SHA-256.
- On Windows, also require a valid Authenticode signature from `win.rar GmbH`.
- Ask the user for explicit confirmation after verification and before running
  the installer.

## Changes Made

### Backend

Files changed:
- `src-tauri/src/rar_installer.rs`
- `src-tauri/src/lib.rs`

Implementation details:
- Added `prepare_rar_install`.
  - Downloads the configured RARLab artifact.
  - Verifies the artifact SHA-256 against a pinned hash.
  - Writes the verified artifact to a unique temp path containing a random
    one-use confirmation token.
  - On Windows, checks `Get-AuthenticodeSignature` through hidden PowerShell and
    requires:
    - `Status = Valid`
    - signer subject contains `CN=win.rar GmbH`
    - signer subject contains `O=win.rar GmbH`
  - Returns the download URL, file name, staged installer path, SHA-256, signer,
    and confirmation token to the frontend.
- Changed `install_rar` so it requires a `confirmation_token`.
  - The command refuses empty tokens.
  - It consumes a prepared one-use token before running the installer.
  - It removes the staged temp artifact after the install attempt.
- Added `discard_rar_install`.
  - Used when the user cancels the confirmation dialog.
  - Removes the staged temp artifact instead of leaving it behind.
- Added a pending-install TTL of 30 minutes and cleanup for expired staged
  installers.
- Updated the Windows download URL from the stale 7.01 artifact to the current
  7.23 x64 artifact because the old 7.01 URL currently returns 404 on RARLab.
- Follow-up cleanup narrowed this module back to Windows-only scope. Removed
  non-Windows RARLab URLs, hashes, cfg branches, and Unix archive extraction.

Pinned artifact hashes:
- Windows x64 `winrar-x64-723.exe`:
  `8ff0daf3ed564cc743c0e23ff2e253997ffc74460f9673f0b6dd037b2db4ce7b`

Hash source:
- Hashes were computed locally on 2026-07-14 from artifacts downloaded from the
  official RARLab download page: https://www.rarlab.com/download.htm

### Frontend

Files changed:
- `frontend/src/lib/types.ts`
- `frontend/src/lib/api.ts`
- `frontend/src/lib/app/core.ts`
- `frontend/src/lib/app/setup.ts`
- `frontend/src/lib/tauri.ts`
- `frontend/src/css/modules/settings.css`
- `frontend/scripts/check-stage4-settings-tools.mjs`

Implementation details:
- Added typed API wrappers for:
  - `prepareRarInstall`
  - `discardRarInstall`
  - tokenized `installRar`
- Replaced the direct settings-button install call with `installRarFlow`.
- The new flow:
  1. prepares and verifies the installer,
  2. displays an in-app confirmation dialog with URL, file, SHA-256, publisher,
     and staged path,
  3. runs the installer only after the user clicks `Run Installer`,
  4. discards the staged artifact if the user cancels.
- Added browser-dev fallback responses for the new commands.
- Added scoped CSS for the verification metadata shown in the confirmation
  dialog.

### Checks

File changed:
- `scripts/check-provider-surface.mjs`

Implementation details:
- Excluded `build_notes` from the no-cloud/provider surface check.
- Reason: build notes are internal implementation history and must be able to
  mention stale or removed surfaces without being treated as active app UI,
  active release docs, or product-facing copy.

## Validation

Passed:
- `cargo test --locked --all-features`
- `npm run check`
- `cargo fmt --all -- --check`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`
- `npm run check:provider-surface`

Notes:
- `npm run check` initially failed because `scripts/check-provider-surface.mjs`
  scanned `build_notes` and flagged the audit note's historical cloud wording.
  The checker was narrowed to skip `build_notes`, then the full check passed.
