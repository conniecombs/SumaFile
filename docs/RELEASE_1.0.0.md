# SumaFile 1.0.0 Release Checklist

SumaFile 1.0.0 is the first numbered release of the WinUI 3 + Rust IPC
desktop app in this repository.

## Required Artifacts

| Artifact | Purpose |
| --- | --- |
| `SumaFile_1.0.0_x64-winui-setup.exe` | Recommended per-user NSIS installer |
| `SumaFile_1.0.0_x64-winui.msi` | MSI deployment package |
| `SumaFile_1.0.0_x64-winui-portable.zip` | Portable payload with `SumaFile.exe` and `simplefile-service.exe` |
| `latest-winui.json` | Signed updater manifest for Settings -> Updates |

## Local Verification

Run these from the repository root before creating a release tag:

```powershell
npm run check:release
npm run build:winui
npm run check:winui
npm run smoke:winui
npm run smoke:winui-file-ops
npm run smoke:winui-msi
npm run smoke:winui-installer
npm run smoke:winui-upgrade
npm run smoke:winui-upgrade-from-ref -- -PreviousRef b80caed932ab567695bb3ce38d5659217a7cb176
git diff --check
```

`npm run check:release` covers repo guard scripts, Rust formatting, Rust tests,
Clippy, and the Rust dependency audit. The WinUI test and smoke commands are
kept explicit because they prove the app host and generated release payloads.
For the first numbered SumaFile release, the published-release upgrade smoke can
skip when no previous GitHub release exists; the `smoke:winui-upgrade-from-ref`
command above builds the actual merged BETA baseline in a temporary worktree and
upgrades it to the local 1.0.0 installer.

## Clean VM Verification

Before publishing, the GitHub `Installer smoke` workflow must run successfully
on the same commit that will be released. The workflow builds the WinUI payload,
portable ZIP, NSIS installer, MSI package, and `latest-winui.json`, then runs:

```powershell
npm run smoke:winui
npm run smoke:winui-file-ops
npm run smoke:winui-msi
npm run smoke:winui-installer
npm run smoke:winui-upgrade
npm run smoke:winui-upgrade-from-ref -- -PreviousRef b80caed932ab567695bb3ce38d5659217a7cb176
```

For the first 1.0.0 release, treat a skipped published-release upgrade smoke as
insufficient by itself. The workflow must also pass the previous-ref upgrade
smoke against the merged PR #10 BETA baseline above; keep that result with the
release notes until a published SumaFile release exists for future upgrade runs.

## Dogfood 10-Step Script

Use a clean Windows 10 2004+ or Windows 11 x64 profile.

1. Install `SumaFile_1.0.0_x64-winui-setup.exe` silently or from Explorer.
2. Launch SumaFile from the Start menu and confirm the window title and About
   version show `SumaFile` and `1.0.0`.
3. Open a local folder, create a `dogfood-sumafile` folder, then create and
   rename a text file inside it.
4. Copy that file to a second folder, then cut and paste it back to verify move
   progress and operation history.
5. Trigger a copy conflict and verify Skip, Replace, and Keep Both choices.
6. Toggle dual-pane mode, copy a file to the opposite pane, then close the second
   pane.
7. Open Quick Look or the preview pane for text, image, and PDF samples.
8. Run recursive search, cancel it, and confirm no stale results replace the
   current pane.
9. Open Settings -> Updates and confirm signed metadata either reports no newer
   release or offers an installable update.
10. Uninstall SumaFile, reinstall with the MSI or portable ZIP smoke path, and
    confirm `%APPDATA%\com.simplefile.desktop` data still loads.

## SimpleFile Data Import

No manual import is required for normal SimpleFile-to-SumaFile use. SumaFile
continues to prefer the stable `%APPDATA%\com.simplefile.desktop` data folder
used by earlier releases, and the settings store also falls back to legacy
`SimpleFile` and `SumaFile` app-data folders when an existing `metadata.db` is
present.

## GitHub Release Checklist

1. Confirm `main` is at the release commit and `npm run check:release` is green.
2. Confirm the latest `Installer smoke` workflow succeeded on that exact commit.
   If its published-release upgrade step skipped, confirm
   `npm run smoke:winui-upgrade-from-ref -- -PreviousRef b80caed932ab567695bb3ce38d5659217a7cb176`
   passed locally.
3. Confirm `src-winui/Directory.Build.props`, all `crates/*/Cargo.toml` files,
   `Cargo.lock`, `APP_DISPLAY_VERSION`, and the README badge all say `1.0.0`.
4. Create or dispatch a `v1.0.0` release through `.github/workflows/release.yml`.
5. Verify the GitHub Release includes the NSIS installer, MSI, portable ZIP, and
   `latest-winui.json`.
6. Install the released NSIS build, then use Settings -> Updates against a newer
   signed test release before claiming in-app updater installation is proven.

## Known Limitations

- SumaFile 1.0.0 is Windows-only and targets Windows 10 2004+ / Windows 11 x64.
- The first 1.0.0 install is manual; in-app installation requires a newer
  published release with signed `latest-winui.json` metadata.
- Optional archive tooling remains external: `.7z` workflows require 7-Zip, and
  RAR creation/extraction uses Settings -> Tools.
- Account-backed storage integrations are not part of 1.0.0. Supported storage
  surfaces are local folders, local drives, removable media, mapped network
  shares, and archives.
