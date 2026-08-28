# 2026-07-17 Next Recommended Work

Status: audit and recommendation only. No program behavior was changed in this
pass.

Context:
- SimpleFile-Windows is now treated as a Windows-only program.
- Earlier P1 items are already fixed and committed: updater/repository metadata,
  license-key generator removal, WinRAR installer hardening, markdown preview
  sanitization, and stale remote-drive residue removal.
- This pass looked for the next highest-value cleanup after those changes.

## Recommendation

Next fix: clean the release, contribution, and issue-reporting docs so every
current-facing instruction matches the Windows-only release channel.

Why this should go first:
- It is lower risk than backend platform pruning.
- It removes incorrect operational guidance before the next release.
- It also gives the later Windows-only source cleanup a clear written boundary.

Recommended implementation:
- Rewrite `.github/RELEASE.md` to describe Windows x64 NSIS/MSI releases only.
- Remove the missing `docs/CLOUD_DRIVES.md` release-checklist link.
- Update `docs/RELEASE_1.1.0.md` if it is still intended to describe the
  actual 1.1.0 Windows release artifacts.
- Update `.github/PULL_REQUEST_TEMPLATE.md` so test-platform checkboxes are
  Windows-focused.
- Update `.github/ISSUE_TEMPLATE/bug_report.md` so examples do not imply Linux
  or macOS support.
- Leave historical changelog entries alone unless they are copied into
  current-facing process docs.

## 1. Current Release Docs Still Describe Non-Windows Artifacts

Severity: P2

Evidence:
- `.github/RELEASE.md` links to missing `docs/CLOUD_DRIVES.md`.
- `.github/RELEASE.md` says the release workflow builds Windows x64, macOS
  Intel, macOS Apple Silicon, and Linux x64.
- `.github/RELEASE.md` includes macOS DMG and Linux AppImage/Debian artifact
  rows.
- `.github/RELEASE.md` still documents macOS code-signing secrets.
- `docs/RELEASE_1.1.0.md` says the GitHub release workflow builds macOS Intel,
  macOS Apple Silicon, and Linux x64 artifacts.
- `.github/workflows/release.yml` currently builds only `Windows x64` with
  target `x86_64-pc-windows-msvc`.

Why it matters:
- Release operators may look for artifacts that cannot exist.
- A missing checklist link makes the release process unreliable.
- The docs conflict with the Windows-only product direction.

Fix options:
- Option A: Rewrite current release docs to match the Windows x64 workflow and
  remove the missing cloud-drive reference.
- Option B: Keep old platform notes only in archived/historical release notes,
  clearly marked as not current process.
- Option C: Restore macOS/Linux release jobs and missing docs if cross-platform
  packaging becomes intentional again.

Recommended path:
- Option A now. Use Option B only for historical notes that must be preserved.

## 2. PR And Bug Templates Still Invite Cross-Platform Reporting

Severity: P3

Evidence:
- `.github/PULL_REQUEST_TEMPLATE.md` still has Linux, macOS, and Windows test
  platform checkboxes.
- `.github/ISSUE_TEMPLATE/bug_report.md` still gives Ubuntu and macOS as OS
  examples.

Why it matters:
- Contributors get mixed signals about supported platforms.
- Windows-only testing expectations are less visible than they should be.

Fix options:
- Option A: Replace platform checkboxes/examples with Windows-only wording and
  fields for Windows version, installer type, and mapped/network drive context.
- Option B: Keep a free-form OS field but explicitly state that only Windows
  reports are in current scope.
- Option C: Add separate historical/cross-platform labels while keeping the
  default templates Windows-only.

Recommended path:
- Option A.

## 3. Backend Source Still Carries Non-Windows Branches And Dependencies

Severity: P2

Evidence:
- `src-tauri/Cargo.toml` still declares `[target.'cfg(unix)'.dependencies]`
  with `libc` and `xattr`.
- `src-tauri/src/drives.rs` still contains macOS `/Volumes` enumeration and
  Linux mount scanning.
- `src-tauri/src/open_with.rs` still contains macOS/Linux trusted-root and Unix
  executable-permission logic.
- `src-tauri/src/fs_ops.rs` still contains Linux/macOS `rename_no_replace`
  implementations, Unix xattr preservation, and non-Windows symlink branches.
- `src-tauri/src/terminal.rs` still contains Linux terminal-emulator and macOS
  Terminal launch paths.

Why it matters:
- Dead non-Windows branches increase maintenance surface.
- Cargo audit and dependency policy have to reason about dependencies that the
  Windows program should not need.
- Future changes can accidentally preserve or expand unsupported behavior.

Fix options:
- Option A: Remove non-Windows code paths and Unix-only dependencies in a
  focused Windows-only backend sweep.
- Option B: Keep cfg-gated code but add an explicit support-boundary note and a
  guard that prevents non-Windows release targets from returning.
- Option C: Move cross-platform code into an archive branch or document it as
  historical implementation reference only.

Recommended path:
- Option A, after the current docs cleanup. Preserve Windows local drive and
  mapped network drive behavior while pruning unsupported branches.

## 4. Live Frontend APIs Still Accept Raw HTML Strings

Severity: P2 hardening candidate

Evidence:
- `frontend/src/lib/app/core.ts` assigns `body.innerHTML = bodyHtml`.
- `frontend/src/lib/components/modal-body/ModalBody.svelte` renders
  `{@html bodyHtml}`.
- `frontend/src/lib/components/quick-look/QuickLookModal.svelte` accepts
  `legacyContent?: Node | string | null` and assigns string content with
  `contentElement.innerHTML = legacyContent`.
- Current call sites commonly escape interpolated data with `escapeHtml`, and
  the markdown preview path is already sanitized, so this is a regression risk
  rather than a confirmed active exploit from the inspected call sites.

Why it matters:
- The app is a file manager, so filenames, paths, archive entries, preview text,
  and metadata should be treated as untrusted.
- Generic raw-HTML entry points make future UI changes easier to get wrong.

Fix options:
- Option A: Replace generic HTML-string dialogs with typed Svelte components or
  DOM node builders.
- Option B: Keep HTML-string dialogs but require a sanitizer at the boundary and
  add a check that only allowlisted files may use `innerHTML` or `{@html}`.
- Option C: Remove the unused QuickLook string `legacyContent` path first, then
  tackle modal HTML separately.

Recommended path:
- Option C as a small first hardening step, followed by Option B or A for modal
  bodies.

## 5. Tauri Global API Exposure Appears Unused

Severity: P3 hardening candidate

Evidence:
- `src-tauri/tauri.conf.json` has `"withGlobalTauri": true`.
- A search of active frontend code found no `__TAURI__` usage.
- Active frontend code uses typed imports from `@tauri-apps/api` through local
  wrappers instead.

Why it matters:
- Disabling the global object reduces renderer API surface.
- The typed wrapper boundary is already the documented frontend pattern.

Fix options:
- Option A: Set `withGlobalTauri` to `false` and run frontend plus Tauri smoke
  checks.
- Option B: Keep it enabled only with a comment or note explaining the current
  consumer.
- Option C: Add a guard that fails if `withGlobalTauri` is enabled without an
  allowlisted reason.

Recommended path:
- Option A after the raw HTML hardening check, because both are renderer-surface
  hardening.

## 6. Cargo Audit Ignore Policy Is Duplicated

Severity: P3 process risk

Evidence:
- `.github/workflows/ci.yml` contains the full `cargo audit --deny warnings`
  ignore list inline.
- `.github/workflows/release.yml` contains the same inline ignore list.
- `scripts/cargo-audit-release.mjs` contains another copy of the ignore list.
- `package.json` already exposes this script as `npm run check:security`.

Why it matters:
- Security policy can drift between local checks, CI, and release checks.
- Updating accepted advisories requires editing multiple places.

Fix options:
- Option A: Have CI and release workflows call `node scripts/cargo-audit-release.mjs`.
- Option B: Add a parity check that verifies workflow ignore IDs match the
  script.
- Option C: Move advisory policy to a small JSON file consumed by both the
  script and workflow-check tooling.

Recommended path:
- Option A if Node is already available in the relevant jobs. Option B is the
  lightest guard if workflows must keep the command inline.

## 7. Migration Audit Bundles May Have Outlived Their Usefulness

Severity: P3 cleanup candidate

Evidence:
- `frontend/src/vanilla-js/generated-svelte/` contains 19 tracked generated
  JavaScript bundles totaling about 3.9 MB.
- `docs/svelte-migration-plan.md` says the Svelte migration is complete for the
  shipping frontend.
- `frontend/scripts/check-behavior-bridges.mjs` still relies on generated
  bundle artifacts for behavior-contract checks.

Why it matters:
- Large generated sources add review noise.
- Keeping them is valid only if they remain a deliberate regression-test input.

Fix options:
- Option A: Keep them, but add a short retention note explaining when they can be
  removed.
- Option B: Replace generated-bundle comparisons with source-level tests and
  delete the generated artifacts.
- Option C: Move generated artifacts outside the shipping source tree if the
  checks still need snapshots.

Recommended path:
- Option A for now. Revisit Option B after higher-priority Windows-only cleanup.

## 8. Small Confirmed Cleanup Leftovers Remain

Severity: P3

Evidence:
- `frontend/src/lib/components/layout-shell/ContentShell.svelte` still contains
  a UTF-8 mojibake sequence where the secondary path edit button should show a
  pencil/edit icon.
- `frontend/src/app.css` and `frontend/src/css/styles.css` have identical
  SHA-256 hashes, while `frontend/src/main.ts` imports `./css/styles.css`.
- `.github/dependabot.yml` still has two identical `/frontend` npm entries.

Why it matters:
- The glyph is visible UI polish debt.
- Duplicate CSS and duplicate Dependabot config are small drift traps.

Fix options:
- Option A: Fix all three in one small cleanup patch.
- Option B: Fold the Dependabot cleanup into workflow/docs cleanup and handle
  UI/CSS separately.
- Option C: Leave them until after P2 items, since they are low risk.

Recommended path:
- Option B if the next patch edits `.github` files anyway.

## 9. Non-Windows Packaging Artifacts Are Still Tracked

Severity: P3 cleanup candidate

Evidence:
- `src-tauri/icons/android/` and `src-tauri/icons/ios/` are tracked.
- `src-tauri/icons/icon.icns` is tracked and still listed in the Tauri icon
  array.
- `src-tauri/gen/schemas/linux-schema.json` is tracked.

Why it matters:
- These files can confuse the project scope in a Windows-only repo.
- Some may be generated or expected by Tauri tooling, so they should not be
  deleted blindly.

Fix options:
- Option A: Verify Tauri's Windows build requirements, then remove unused
  Android, iOS, macOS, and Linux schema artifacts.
- Option B: Keep generated/tooling artifacts but document why they remain.
- Option C: Regenerate icons/schemas from a Windows-only asset set if Tauri
  supports that cleanly.

Recommended path:
- Option A only after a local Tauri package smoke test. Otherwise Option B.

## Not Findings

- `linux-native-integration-plan.md` from the earlier audit no longer appears
  tracked in the repo.
- Historical entries in `docs/CHANGELOG.md` still mention old cloud and
  cross-platform work. That is expected history, not current-facing guidance.
- Windows mapped network drive support remains in scope and should be preserved.
- Linux/Tauri dependency installation in Ubuntu CI jobs may still be required by
  Tauri checks that run on Ubuntu; do not remove it without testing the workflow
  replacement.

## Suggested Fix Order

1. Update current-facing release, PR, and issue docs for Windows-only scope.
2. Remove duplicate `/frontend` Dependabot block while editing `.github`.
3. Fix the mojibake icon and duplicate CSS in a small frontend cleanup.
4. Prune non-Windows Rust branches and Unix-only dependencies.
5. Harden raw HTML dialog/QuickLook boundaries.
6. Disable `withGlobalTauri` if smoke checks confirm no dependency on it.
7. Single-source the cargo-audit ignore policy.
8. Decide retention for generated migration bundles and non-Windows packaging
   artifacts.

## Validation For This Audit

- Reviewed `.github/RELEASE.md`, `docs/RELEASE_1.1.0.md`,
  `.github/PULL_REQUEST_TEMPLATE.md`, `.github/ISSUE_TEMPLATE/bug_report.md`,
  `.github/workflows/release.yml`, `.github/workflows/ci.yml`,
  `.github/dependabot.yml`, `src-tauri/Cargo.toml`, selected Rust modules,
  frontend raw-HTML sinks, and migration-check scripts.
- Verified the working tree only had ignored build artifacts before this note.
