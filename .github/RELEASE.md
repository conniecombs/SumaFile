# Release Process

This is the release runbook for SumaFile: how a version is prepared, merged,
tagged, built and published from the `main` branch. SumaFile ships for Windows
x64 as a WinUI 3 host with the Rust `simplefile-service` IPC process.

The updater's signing key, and what an installed app checks before it installs
an update, are described in
[`docs/UPDATER_RELEASE.md`](../docs/UPDATER_RELEASE.md).

In the commands below `<version>` is the new version without a leading `v`,
and `v<version>` is its tag. See [Versioning](#versioning) for the accepted
form.

## Prerequisites

Local release work uses the exact Node.js version in `.node-version`, the Rust
version in `rust-toolchain.toml` and the .NET SDK version in `global.json`.
Run `npm ci --ignore-scripts` first.

- `npm run check:security` needs `cargo-audit`.
- Building the installers locally needs NSIS (`makensis.exe`) and WiX Toolset
  v3, 3.14.1 or later, with `WixUtilExtension`.

The release workflow builds on GitHub-hosted `windows-2025` runners and
installs WiX and NSIS with Chocolatey when the runner does not have them.

## Release Steps

### 1. Prepare the Version

Release preparation is a manual step run locally. Preview the change, then
apply it:

```powershell
npm run release:prepare -- <version>
npm run release:prepare -- <version> --write
```

Without `--write` the command prints the files it would change and writes
nothing. Either way it first reads the current version and applies the
version-field checks of step 4, so it stops on a tree whose version fields
already disagree.

With `--write` it rewrites these files and no others:

| File | What is rewritten |
|------|-------------------|
| `src-winui/Directory.Build.props` | `<Version>` (the numeric `MAJOR.MINOR.PATCH`) and `<InformationalVersion>` (the full version) |
| `crates/simplefile-core/Cargo.toml`, `crates/simplefile-ipc/Cargo.toml`, `crates/simplefile-service/Cargo.toml` | Package `version` (numeric) |
| `crates/sumafile-plugin/Cargo.toml` | The version of its `simplefile-ipc` dependency; the crate's own version is not changed |
| `Cargo.lock` | The entries of those three packages |
| `crates/simplefile-core/src/lib.rs` | `APP_DISPLAY_VERSION` (the full version) |
| `src-winui/SimpleFile.App/app.manifest` | The `assemblyIdentity` version (`<numeric>.0`) |
| `packaging/winui/sumafile-winui.nsi` | The `NUMERIC_VERSION` fallback |
| Every `packages.lock.json` in the repository | The versions of `SimpleFile.*` project references; a lock file without such an entry is left unchanged |
| [`README.md`](../README.md) | The version badge, the `Product version` table row, the `SumaFile_<version>_` artifact names, the `docs/RELEASE_<version>.md` links, and the sentence "SumaFile `<version>` is Windows-only." |

The automated `release` recipe of the maintenance workflow does not currently
cover this step: its allowlist omits files the command now edits, so it cannot
publish the change.

These are still written by hand:

1. **Release notes**, in a new `docs/RELEASE_<version>.md`. Start from the
   previous release's notes. `npm run check` fails until the file exists for
   the version in `Directory.Build.props`, and it requires the file to:
   - begin with `# SumaFile <version> Release ` (for example
     `# SumaFile <version> Release Notes`);
   - name the three artifacts `SumaFile_<version>_x64-winui-setup.exe`,
     `SumaFile_<version>_x64-winui.msi` and
     `SumaFile_<version>_x64-winui-portable.zip`;
   - mention `latest-winui.json` and `smoke:winui-upgrade-from-ref`;
   - contain the headings `## Dogfood 10-Step Script`,
     `## SimpleFile Data Import` and `## Known Limitations`;
   - contain the phrase
     `signed test release before claiming in-app updater installation is proven`.

   The release workflow publishes this file as the body of the GitHub release.
2. **The changelog section** for the version in
   [`docs/CHANGELOG.md`](../docs/CHANGELOG.md), below `## [Unreleased]`. Keep
   the sections and notes files of earlier releases.
3. **Other mentions of the previous version.** The command rewrites only the
   README items in the table. Search `README.md` and `docs/` for the previous
   version and update the text that describes the release.

`npm run check` also requires `README.md` to contain `SumaFile <version>`,
`docs/RELEASE_<version>.md`, the heading `## Known Limitations` and the
sentence "No manual import is needed for normal SimpleFile-to-SumaFile use."

For a release that changes Windows drive enumeration, mapped network drive
display, process launching, updater behavior, installer behavior or the
release smoke tests, also update [`docs/SECURITY.md`](../docs/SECURITY.md),
[`docs/SUPPORT.md`](../docs/SUPPORT.md) and the relevant README sections.

Run the gates before opening the pull request:

```powershell
npm run check:release
npm run test:maintenance
```

`check:release` runs the repository checks (`npm run check`), Rust formatting,
tests and Clippy (`check:rust`), and the Rust dependency audit
(`check:security`). `test:maintenance` runs the Node tests under
`scripts/tests/`, which cover the release-version script and the workflow
policy.

To build and smoke-test the installers locally, see
[Local Packaging](#local-packaging).

### 2. Merge the Version Bump

Open a pull request into `main` with the version change, the release notes and
the changelog section. The branch protection baseline in
`.github/branch-protection.json` requires the `CI required` check on an
up-to-date branch and resolved conversations. Merge when CI passes.

### 3. Tag the Release

The tag is `v` followed by `<InformationalVersion>` from
`src-winui/Directory.Build.props`. Tag the merged commit on `main`:

```bash
git checkout main
git pull origin main
git tag v<version>
git push origin v<version>
```

Pushing a tag that starts with `v` starts the release workflow. A tag push
publishes the release when every job succeeds.

### 4. What the Release Workflow Does

`.github/workflows/release.yml` runs four jobs in order.

1. **Validate release version.** `node scripts/release-version.mjs --resolve`
   reads the version from `src-winui/Directory.Build.props` and fails unless
   all of these agree with it:
   - `<Version>` is the numeric part of `<InformationalVersion>`;
   - the package versions of `simplefile-core`, `simplefile-ipc` and
     `simplefile-service` (the other workspace crates version on their own and
     are not compared);
   - `APP_DISPLAY_VERSION` in `crates/simplefile-core/src/lib.rs`;
   - the `assemblyIdentity` version in `app.manifest`;
   - the `NUMERIC_VERSION` fallback in the NSIS script;
   - the `simplefile-ipc` requirement in `crates/sumafile-plugin/Cargo.toml`;
   - every `SimpleFile.*` project-reference version in every
     `packages.lock.json`;
   - `docs/RELEASE_<version>.md` exists and begins with
     `# SumaFile <version> Release `;
   - the pushed tag, or the version entered for a manual run, equals
     `v<version>`.

   `node scripts/release-version.mjs --require-unpublished` then stops the run
   when that version is already published.
2. **Release quality gates.** `cargo fmt --all -- --check`, Clippy with
   warnings denied, `cargo test --locked --all-features`,
   `npm ci --ignore-scripts`, `npm run check`, `npm run test:maintenance` and
   the Rust dependency audit (`node scripts/cargo-audit-release.mjs`).
3. **Build Windows x64.** Runs `scripts/build-winui-release.ps1 -SkipChecks
   -RequireInstaller -RequireUpdaterSignature`. The script builds
   `simplefile-service` and the three bundled plugin backends, publishes the
   self-contained WinUI host for `win-x64`, stages the payload with the
   bundled plugins, and produces the portable zip, the NSIS setup and the MSI.
   It signs the NSIS setup for the updater, writes `latest-winui.json`, and
   runs the payload startup smoke, the packaged file-operation smoke and the
   MSI extract-and-launch smoke. The job then runs:
   - `npm run smoke:winui-file-ops`: packaged file operations;
   - `npm run smoke:winui-installer`: NSIS install, launch and uninstall;
   - `npm run smoke:winui-upgrade`: NSIS upgrade from the latest published
     release (it skips with a message when no published installer can be
     downloaded);
   - `npm run smoke:winui-msi-lifecycle`: MSI install, upgrade and uninstall.

   It then uploads the artifacts to a draft GitHub release named
   `SumaFile v<version>`, with `docs/RELEASE_<version>.md` as its body. A
   version with a prerelease suffix is marked as a prerelease.
4. **Publish release.** Publishes the draft. It runs for every tag push, and
   for a manual run only when `draft` is set to `false`.

Every Cargo build, test and Clippy command in the workflow uses `--locked`, so
the root `Cargo.lock` must be committed and current.

The release workflow does not run the C# tests (`npm run check:winui`) or the
UI smoke (`npm run smoke:winui-ui`). CI runs both on the pull request and on
the push to `main`.

A run stops at validation when its version is already published. The
installer, its signature and `latest-winui.json` of a published release are
what people installed and what the updater verifies, so a run never replaces
them. A draft can be rebuilt as often as needed. To rebuild a published
version on purpose, delete that release or return it to draft first; otherwise
bump the version.

### 5. Manual Run

The release workflow can also be started by hand:

1. Go to Actions → Release.
2. After the version pull request has merged, click **Run workflow** and
   select `main`. Start a fresh run so the checkout contains the new version
   files.
3. Leave the version blank to use the version in the checked-out files, or
   enter `v<version>` to assert it; the run fails if they differ.
4. Leave `draft` checked to stop at a draft release for review, or clear it
   to publish after the build succeeds.

If the run fails in **Validate release version** with "already published",
`main` still holds a version that has been released: merge a version bump and
start a new run.

## Local Packaging

```powershell
npm run release:build
```

`release:build` runs `scripts/build-winui-release.ps1` with no switches. It
runs the packaging check and the C# tests, builds the artifacts into
`dist/winui/`, and runs the payload startup smoke, the packaged file-operation
smoke and the MSI extract-and-launch smoke. Without NSIS or WiX it warns and
skips that installer. `npm run release:local` runs `check:release` first.

The install-level smokes run separately, against the artifacts in
`dist/winui/`:

| Command | What it does |
|---------|--------------|
| `npm run smoke:winui-installer` | Installs the NSIS setup into a temporary folder, launches it, reinstalls and uninstalls |
| `npm run smoke:winui-upgrade` | Installs the latest published NSIS setup, upgrades to the local one and uninstalls |
| `npm run smoke:winui-upgrade-from-ref -- -PreviousRef <git-ref>` | Builds `<git-ref>` in a temporary worktree and upgrades from its NSIS setup to the local one |
| `npm run smoke:winui-msi-lifecycle` | Builds two MSIs from the local payload, then installs, upgrades and uninstalls them under a separate smoke identity |

These smokes refuse to run while SumaFile is running or installed for the
current user. `smoke:winui-upgrade` downloads the published installer with the
GitHub CLI (`gh`); without `gh`, or when no published installer can be
downloaded, it prints a skip message and exits successfully. Pass
`-PreviousInstaller <path>` or set `SUMAFILE_PREVIOUS_INSTALLER` to upgrade
from a local installer instead.

To build the same artifacts on GitHub without creating a release, run the
**Release build** workflow from the Actions tab. It uploads an Actions
artifact and never creates or publishes a GitHub release. It has no signing
key, so its NSIS setup is unsigned and its `latest-winui.json` carries no
signature. Its `run_smoke` input controls the extra packaged file-operation
smoke; `full_installer_smoke` controls the NSIS upgrade, MSI lifecycle and
baseline-upgrade smokes; `clean` has no effect.

## Release Artifacts

| Artifact | File |
|----------|------|
| NSIS setup executable | `SumaFile_<version>_x64-winui-setup.exe` |
| MSI installer | `SumaFile_<version>_x64-winui.msi` |
| Portable zip of the staged payload (`SumaFile.exe`, `simplefile-service.exe`, the bundled plugins and the self-contained runtime) | `SumaFile_<version>_x64-winui-portable.zip` |
| Updater manifest: version, download URL, size, SHA-256 and signature of the NSIS setup | `latest-winui.json` |
| Updater signature of the NSIS setup | `SumaFile_<version>_x64-winui-setup.exe.sig` |

## Signing

The release build reads three repository settings. They keep the
`SIMPLEFILE_*` names for release compatibility:

- `SIMPLEFILE_SIGNING_PRIVATE_KEY`: secret, the updater's Ed25519 private key.
- `SIMPLEFILE_SIGNING_PRIVATE_KEY_PASSWORD`: secret, the key's passphrase when
  it has one.
- `SIMPLEFILE_UPDATER_PUBLIC_KEY`: repository variable, the matching public
  key, compiled into the service.

With `-RequireUpdaterSignature` the build fails when the private key or the
public key is missing, when they do not match, or when `latest-winui.json`
would lack a signature, hash or size, so unsigned updater metadata cannot be
published. [`docs/UPDATER_RELEASE.md`](../docs/UPDATER_RELEASE.md) describes
how the key is created and what the app verifies.

The updater signature is the only signature. No workflow or script signs the
executables or installers with a code-signing certificate.

## Workflows

| File | Name | Trigger | Purpose |
|------|------|---------|---------|
| `ci.yml` | CI | Push or pull request to `main` or `C#`, manual dispatch | Rust formatting, Clippy, tests and a packaging dry run of the plugin crates; `npm run check` and `npm run test:maintenance`; Rust dependency audit; service release build; the WinUI host job (plugin build and staging, C# tests, SDK pack dry run, app build, release-hash check, UI smoke with and without plugins, startup budget, packaging check); installer verification for manual runs, Dependabot pull requests and maintenance branches; the `CI required` gate |
| `installer-smoke.yml` | Installer smoke | Daily at 06:00 UTC, manual dispatch, called by CI | Builds unsigned installers and runs the startup, file-operation, UI, MSI, NSIS, MSI lifecycle and upgrade smokes |
| `release.yml` | Release | Push of a `v*` tag, manual dispatch | Version validation, quality gates, signed build, smokes, draft upload and publishing |
| `release-build.yml` | Release build | Manual dispatch | Builds the release artifacts as an Actions artifact without a GitHub release |
| `maintenance.yml` | Bounded maintenance | A failed CI or Installer smoke run, Mondays at 09:30 UTC, daily at 10:30 UTC, manual dispatch | Diagnoses failures, retries transport failures once and proposes bounded repair pull requests |
| `dependabot-automerge.yml` | Validated maintenance automerge | A completed CI run, dispatch from CI | Merges eligible Dependabot and maintenance pull requests after re-checking them |

`.github/dependabot.yml` is configuration, not a workflow: it schedules weekly
Dependabot updates for Cargo, GitHub Actions, npm and NuGet.
[`docs/CI_MAINTENANCE.md`](../docs/CI_MAINTENANCE.md) describes CI, the
maintenance workflow and the merge gate.

## Versioning

Versions follow Semantic Versioning:

- **MAJOR**: breaking changes
- **MINOR**: backward-compatible features
- **PATCH**: backward-compatible fixes and release/process improvements

`release:prepare` accepts `MAJOR.MINOR.PATCH` with an optional prerelease
suffix such as `-beta.1` or `-rc.1`, and ignores a leading `v`. MAJOR and
MINOR may be at most 255 and PATCH at most 65535, which keeps the numeric
fields valid for the MSI.

For a prerelease, `<Version>`, the crate versions, the Windows manifest and
the MSI product version carry the numeric part. `<InformationalVersion>`,
`APP_DISPLAY_VERSION`, the artifact names, the release notes file name and the
tag carry the full version, suffix included.
