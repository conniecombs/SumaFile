# 2026-07-17 Release Docs Windows-Only Cleanup

Issue fixed:
- `build_notes/2026-07-17-next-recommended-work.md` issue 1:
  current release docs still described non-Windows artifacts.

## Problem

The release workflow builds only the Windows x64 target, but the release
process docs still described macOS and Linux release artifacts. The release
process also linked to the removed `docs/CLOUD_DRIVES.md` checklist item.

## Files Changed

### `.github/RELEASE.md`

Changes:
- Described the release as a Windows-only release from `main`.
- Removed the missing `docs/CLOUD_DRIVES.md` checklist link.
- Replaced the old all-platform build description with the actual Windows x64
  target: `x86_64-pc-windows-msvc`.
- Updated the publish rule to say releases publish after the Windows build
  succeeds, not after all platform builds succeed.
- Replaced the artifact table with Windows x64 NSIS, Windows x64 MSI, and
  Windows updater artifacts.
- Updated CI/CD workflow descriptions to say Windows x64 packaging/builds
  instead of cross-platform packaging.
- Removed macOS code-signing and notarization setup instructions from the
  current release process.

### `docs/RELEASE_1.1.0.md`

Changes:
- Replaced macOS and Linux artifact bullets with the actual Windows release
  artifacts:
  - Windows x64 NSIS setup executable.
  - Windows x64 MSI installer.
  - Signed Windows updater artifacts and `latest.json`.

## Deliberately Not Changed

- Historical `docs/CHANGELOG.md` entries still mention older cloud and
  cross-platform work. Those entries are release history, not current process
  guidance.
- `.github/PULL_REQUEST_TEMPLATE.md`, `.github/ISSUE_TEMPLATE/bug_report.md`,
  and `.github/dependabot.yml` are tracked as separate follow-up cleanup items.

## Validation Passed

- `rg -n -F "macOS" .github/RELEASE.md docs/RELEASE_1.1.0.md` returned no
  matches.
- `rg -n -F "Linux" .github/RELEASE.md docs/RELEASE_1.1.0.md` returned no
  matches.
- `rg -n -F "CLOUD_DRIVES.md" .github/RELEASE.md docs/RELEASE_1.1.0.md`
  returned no matches.
- `rg -n -F "cross-platform" .github/RELEASE.md docs/RELEASE_1.1.0.md`
  returned no matches.
- `rg -n -F "all platform" .github/RELEASE.md docs/RELEASE_1.1.0.md` returned
  no matches.
- `npm run check:workflows` passed.
- `npm run check:provider-surface` passed.
- `git diff --check` passed.
