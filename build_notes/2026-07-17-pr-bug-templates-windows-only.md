# 2026-07-17 PR And Bug Templates Windows-Only Cleanup

Issue fixed:
- `build_notes/2026-07-17-next-recommended-work.md` issue 2:
  PR and bug templates still invited cross-platform reporting.

## Problem

The program is Windows-only, but the pull request template still asked authors
to check Linux, macOS, and Windows test platforms. The bug report template also
gave Ubuntu and macOS as operating-system examples.

## Files Changed

### `.github/PULL_REQUEST_TEMPLATE.md`

Changes:
- Replaced Linux/macOS/Windows platform checkboxes with Windows-specific test
  context.
- Added checkboxes for Windows 10, Windows 11, and other Windows versions.
- Added run/install mode coverage for Tauri dev, local executable, NSIS
  installer, MSI installer, and not-applicable changes.
- Added storage-context coverage for local drives, mapped network drives or UNC
  paths, removable drives, and not-applicable changes.
- Added a checklist item requiring Windows testing or an explanation when
  runtime testing does not apply.

### `.github/ISSUE_TEMPLATE/bug_report.md`

Changes:
- Added an explicit note that SimpleFile-Windows is currently supported on
  Windows only.
- Replaced the generic OS field and Ubuntu/macOS examples with
  Windows-specific version/build details.
- Added fields for install/source, affected storage, and Rust/Tauri version only
  when relevant to local/dev builds.

## Deliberately Not Changed

- `.github/ISSUE_TEMPLATE/feature_request.md` still needs a separate cleanup
  pass for unrelated current-facing wording and links. This issue was scoped to
  PR and bug-report templates.

## Validation Passed

- Searches for `Linux`, `macOS`, and `Ubuntu` in
  `.github/PULL_REQUEST_TEMPLATE.md` and
  `.github/ISSUE_TEMPLATE/bug_report.md` returned no matches.
- Searching for `Windows version` in `.github/PULL_REQUEST_TEMPLATE.md` and
  `.github/ISSUE_TEMPLATE/bug_report.md` found the replacement Windows fields.
- `rg -n -F "Run/install mode tested" .github/PULL_REQUEST_TEMPLATE.md` found
  the PR run/install mode section.
- `rg -n -F "Affected storage" .github/ISSUE_TEMPLATE/bug_report.md` found the
  bug-report storage context field.
- `git diff --check` passed.
