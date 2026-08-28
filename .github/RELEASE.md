# Release Process

This document describes how to create a Windows-only SumaFile release from the
`main` branch. The shipping app is WinUI 3 + the Rust `simplefile-service` IPC
process.

## Automated Releases

Releases are automated with GitHub Actions through `.github/workflows/release.yml`.

## Windows Build Prerequisites

Local release validation requires Node.js 24 or newer, stable Rust, .NET SDK 8,
and optional NSIS / WiX for installer artifacts. GitHub-hosted `windows-latest`
runners provide the Windows SDK.

### 1. Update Version Numbers

Update the version in these files and keep them identical:

- `src-winui/Directory.Build.props` — `<Version>`
- `crates/simplefile-service/Cargo.toml` — package `version` field
- [`README.md`](../README.md) — version badge
- [`docs/CHANGELOG.md`](../docs/CHANGELOG.md) — release notes and compare links

Release workflow validation fails if the tag/manual version does not match both
the WinUI and service versions. Root `Cargo.lock` must also be committed and
current so release builds use the reviewed dependency graph.
For releases that change Windows drive enumeration, mapped network drive display,
process launching, updater behavior, installer behavior, or release smoke tests,
update [`docs/SECURITY.md`](../docs/SECURITY.md),
[`docs/SUPPORT.md`](../docs/SUPPORT.md), and the relevant README sections.

### 2. Merge the Version Bump

Open a pull request into `main`, wait for CI, then merge.

```bash
git checkout main
git pull origin main
```

### 3. Create a Git Tag

Tags must use `vMAJOR.MINOR.PATCH` format, for example `v1.0.0`.

```bash
git tag v1.0.0
git push origin v1.0.0
```

### 4. Automated Build Process

The release workflow will:

1. Validate the release version against `Directory.Build.props` and
   `crates/simplefile-service/Cargo.toml`.
2. Run release quality gates: Rust formatting, Clippy, tests, IPC/schema
   checks, updater/workflow checks, WinUI packaging/parity checks, and Rust
   dependency audit.
3. Build the Windows release target:
   - Windows x64 (`x86_64-pc-windows-msvc`)
4. Build the WinUI host and Rust IPC service (`scripts/build-winui-release.ps1`):
   `SumaFile_*_x64-winui-setup.exe`, `SumaFile_*_x64-winui.msi`,
   `SumaFile_*_x64-winui-portable.zip` (inner `SumaFile.exe` +
   `simplefile-service.exe`), and `latest-winui.json`.
5. Keep tag-triggered releases as drafts by default so assets can be reviewed
   before publishing.
6. Publish the release only after the Windows build succeeds when manual
   `draft=false` is selected.

### 5. Manual Release

You can also trigger a release manually:

1. Go to Actions → Release.
2. Click **Run workflow**.
3. Enter the version, for example `v1.0.0`.
4. Choose whether to create a draft release.

If `draft` is set to `false`, the workflow publishes the release after the
Windows build succeeds.

## Release Artifacts

| Platform | Installer Type | Example File |
|----------|----------------|--------------|
| Windows x64 | NSIS setup executable | `SumaFile_x.x.x_x64-winui-setup.exe` |
| Windows x64 | MSI installer | `SumaFile_x.x.x_x64-winui.msi` |
| Windows x64 | Portable zip | `SumaFile_x.x.x_x64-winui-portable.zip` |
| Windows updater | Static JSON / signatures | `latest-winui.json` and optional `.sig` files |

## Auto-Update

SumaFile publishes `latest-winui.json` to GitHub Releases. The app checks
`https://github.com/conniecombs/SimpleFile-Windows/releases/latest/download/latest-winui.json`.

### Setup Requirements

1. **Optional signing key** in GitHub secrets. These keep the existing
   `SIMPLEFILE_*` names for release compatibility:
   - `SIMPLEFILE_SIGNING_PRIVATE_KEY` — private signing key content
   - `SIMPLEFILE_SIGNING_PRIVATE_KEY_PASSWORD` — optional private key passphrase

2. Keep `scripts/write-latest-winui.mjs` pointed at the GitHub release
   `latest-winui.json` URL.

The first updater-enabled release must be installed manually by existing users.
After that, future published releases can be installed through Settings -> Updates.
See [`docs/UPDATER_RELEASE.md`](../docs/UPDATER_RELEASE.md) for the operational checklist.

## CI/CD Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `ci.yml` | Push/PR to `main`, manual dispatch | Rust format, Clippy, tests, repo checks, Rust dependency audit, service build, WinUI tests |
| `release.yml` | Tag push (`v*`), manual dispatch | Version validation, release quality gates, WinUI/NSIS/MSI packaging, asset upload, optional publishing |
| `dependabot.yml` | Weekly schedule | Dependency update pull requests for Cargo, npm, and GitHub Actions |

## Code Signing

### Windows

Add these secrets for Windows code signing when ready:

- `WINDOWS_CERTIFICATE` — base64-encoded `.pfx` file
- `WINDOWS_CERTIFICATE_PASSWORD` — certificate password

## Versioning

SumaFile follows Semantic Versioning:

- **MAJOR**: breaking changes
- **MINOR**: backward-compatible features
- **PATCH**: backward-compatible fixes and release/process improvements

Pre-release examples: `v1.0.0-beta.1`, `v1.0.0-rc.1`.
