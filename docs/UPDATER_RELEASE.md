# SumaFile Updater Releases

SumaFile publishes WinUI updater metadata to GitHub Releases. Installed apps
check:

```text
https://github.com/conniecombs/SumaFile/releases/latest/download/latest-winui.json
```

## One-time signing setup

The updater private key must never be committed. Store it locally under
`.secrets/` (gitignored) and in GitHub secrets:

```text
SIMPLEFILE_SIGNING_PRIVATE_KEY
SIMPLEFILE_SIGNING_PRIVATE_KEY_PASSWORD
```

Store the matching public key as a GitHub Actions repository variable and pass
it when building updater-enabled local releases:

```text
SIMPLEFILE_UPDATER_PUBLIC_KEY
```

`SIMPLEFILE_SIGNING_PRIVATE_KEY` must be an Ed25519 private key in PEM/PKCS#8
form. `scripts/sign-update-payload.mjs` signs the NSIS setup executable and
checks that the derived public key matches `SIMPLEFILE_UPDATER_PUBLIC_KEY`.

These `SIMPLEFILE_*` names stay in place as release compatibility anchors even
after the user-facing product name changed to SumaFile.

## Release flow

1. Update the version in `src-winui/Directory.Build.props`,
   `crates/simplefile-core/Cargo.toml`, `crates/simplefile-ipc/Cargo.toml`,
   `crates/simplefile-service/Cargo.toml`, `Cargo.lock`, the
   `APP_DISPLAY_VERSION` constant, and the README badge.
2. Commit the version bump and release notes.
3. Create a tag matching `Directory.Build.props` `<Version>` (currently `v1.0.0`), or run the `Release` GitHub Actions workflow
   manually with that version.
4. The release workflow runs quality gates, builds the WinUI host and Rust IPC
   service, signs the NSIS setup executable, uploads NSIS/MSI/portable
   artifacts, and uploads `latest-winui.json`.
5. Publish the GitHub release when ready. Draft releases are not returned by the
   `releases/latest` endpoint, so installed apps only see published releases.

## Validation

Run these before pushing a release branch:

```powershell
npm run check:release
```

That command runs IPC schema/updater/workflow checks, WinUI packaging and
parity-gate checks, Rust formatting, Rust tests, Clippy, and the Rust
dependency audit using the same advisory ignore policy as CI.

To also prove that local Windows installer packaging works, run:

```powershell
npm run release:build
```

That command runs the WinUI release script, smoke-tests the payload executable
and available installers, and prints the generated artifact paths.

To prove upgrade resiliency, run:

```powershell
npm run smoke:winui-upgrade
```

That command installs the latest published NSIS package when one is available,
lays down a persisted app-data sentinel, upgrades to the local NSIS artifact,
checks that the new version launches, and verifies the app-data sentinel
survived.

For a first release or another case where no published previous SumaFile NSIS
installer exists, run a previous-ref upgrade smoke instead:

```powershell
npm run smoke:winui-upgrade-from-ref -- -PreviousRef <git-ref-before-this-release>
```

That command builds the previous ref in a temporary worktree, uses its NSIS
installer as the old version, upgrades to the current local installer, and then
removes the temporary worktree.

To build release-quality artifacts on GitHub without publishing a release, run
the `Release build` workflow from the Actions tab.

## What Settings -> Updates does

`check_for_update` reads `latest-winui.json` and reports whether a newer version exists. It also tells the WinUI Settings page whether the release is installable in-app.

`install_update` is **fail-closed**. The service downloads only trusted SumaFile GitHub release setup URLs, checks the byte count and SHA-256 from `latest-winui.json`, verifies the Ed25519 signature against the public key embedded at build time, and only then launches the NSIS installer. If any metadata is missing, unsigned, mismatched, or this build lacks `SIMPLEFILE_UPDATER_PUBLIC_KEY`, Settings falls back to GitHub Releases.

Do not publish updater-enabled releases without `-RequireUpdaterSignature`.
