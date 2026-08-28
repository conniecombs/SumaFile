# 2026-07-14 Audit Issues And Fix Options

This note starts the `build_notes` trail for SimpleFile-Windows. Future program
modifications should get a matching markdown note in this folder that explains
what changed, why it changed, and how it was verified.

Status: audit only. No source fixes have been applied yet.

## 1. Updater And Release Metadata Point At The Wrong Repository

Severity: P1

Evidence:
- `git remote -v` points at `https://github.com/conniecombs/SimpleFile-Windows.git`.
- `src-tauri/tauri.conf.json` updater endpoint points at `conniecombs/SimpleFile-Svelte`.
- `scripts/check-updater-config.mjs` expects the same stale `SimpleFile-Svelte` endpoint, so the check currently blesses the wrong repo.
- `src-tauri/Cargo.toml`, `src-tauri/src/updater.rs`, `.github/workflows/release.yml`, `.github/RELEASE.md`, `docs/UPDATER_RELEASE.md`, `docs/CODE_OF_CONDUCT.md`, and `frontend/src/lib/components/legacy-shell-template.html` also contain current-facing `SimpleFile-Svelte` links.

Why it matters:
- A Windows release could check/download updates from the wrong repository.
- Release notes, security advisory links, and about/repository links would send users to the wrong project.
- The existing updater check gives false confidence because it encodes the stale expected value.

Fix options:
- Option A: Update all current-facing links/endpoints/metadata to `conniecombs/SimpleFile-Windows`, including the check script expected endpoint. Keep historical changelog text only where it is explicitly release history.
- Option B: Disable updater artifacts and updater endpoints until the Windows release channel is intentionally ready, then re-enable with `SimpleFile-Windows`.
- Option C: Keep the Svelte repo as a historical upstream reference only in archived docs, while adding a migration note that active release infrastructure moved to `SimpleFile-Windows`.

Recommended path:
- Option A, plus a focused search for `SimpleFile-Svelte` after the patch to classify each remaining occurrence as historical or active.

## 2. Committed Private License Signing Key

Severity: P1

Evidence:
- `scripts/generate-license.mjs` says the private key must never be committed, then commits `PRIVATE_KEY_HEX`.
- No active app-side license verification path was found. The current behavior check explicitly rejects old `get_license_status` and `verify_license` invokes.

Why it matters:
- A committed signing private key should be treated as exposed.
- The script appears unrelated to the current file-manager surface and does not belong in source as-is.

Fix options:
- Option A: Delete `scripts/generate-license.mjs` and document that no license-key activation system exists in this release.
- Option B: Replace the private key with a placeholder fixture and move real key handling to local ignored files or CI secrets.
- Option C: Build a complete licensing subsystem with public-key verification in the app, secret generation outside the repo, tests, and release docs.

Recommended path:
- Option A unless licensing is a near-term product requirement. If the key was ever used outside tests, rotate it.

## 3. RAR Installer Executes A Downloaded Installer Without Integrity Verification

Severity: P1

Evidence:
- `src-tauri/src/rar_installer.rs` downloads WinRAR from RARLab.
- On Windows it writes a fixed `%TEMP%\winrar_setup.exe` and runs it with `/S`.
- `download_bytes` only checks HTTP success; it does not verify a pinned checksum, Authenticode signature, or release manifest.

Why it matters:
- A compromised download path or upstream artifact could become silent code execution.
- The fixed temp filename also creates avoidable collision/race risk.

Fix options:
- Option A: Remove automatic installer execution. Show a manual download/open-in-browser path instead.
- Option B: Keep automatic install, but pin the expected SHA-256 per version, write to a unique temp filename, verify before execution, and fail closed.
- Option C: Verify both SHA-256 and Authenticode publisher, then execute only after an explicit user confirmation.
- Option D: Bundle a known `rar.exe`/helper only if redistribution terms allow it, and avoid runtime downloads entirely.

Recommended path:
- Option C if auto-install is kept. Option A is the safest and simplest if install convenience is not critical.

## 4. Markdown Preview Renders Unsanitized HTML

Severity: P2

Evidence:
- `frontend/src/lib/components/preview-pane/PreviewContent.svelte` parses markdown with `marked.parse`.
- The parsed result is inserted using Svelte `{@html}`.
- No sanitizer dependency or sanitization step was found.

Why it matters:
- In a file manager, local markdown files are untrusted input.
- CSP reduces some script risk, but raw HTML injection can still affect the app surface and may become worse if CSP or preview behavior changes.

Fix options:
- Option A: Disable raw HTML in markdown output and render only safe markdown constructs.
- Option B: Add a sanitizer such as DOMPurify or sanitize-html and allow a conservative HTML subset.
- Option C: Render markdown in an isolated preview context with tight sandboxing and no Tauri API exposure.

Recommended path:
- Option B for a useful markdown preview, paired with a regression test containing unsafe HTML.

## 5. Stale Google/Cloud Build Hook Remains

Severity: P2

Evidence:
- `src-tauri/build.rs` watches `SIMPLEFILE_GOOGLE_CLIENT_ID` and `google-oauth-client-id.txt`.
- It exports `SIMPLEFILE_GOOGLE_CLIENT_ID` at build time if present.
- `.gitignore` still contains `/src-tauri/google-oauth-client-id.txt`.
- No live consumer for this value was found.

Why it matters:
- This is stale cloud/provider configuration in the Windows no-cloud line.
- It can confuse future release setup and is not caught by the current provider-surface check.

Fix options:
- Option A: Remove the Google OAuth build hook and the ignored credential-file entry.
- Option B: Keep it only if a documented feature still needs it, and add an explicit current-facing test proving the consumer exists.
- Option C: Move old provider material into archived documentation and update the provider-surface check to catch OAuth-client residue.

Recommended path:
- Option A, plus a provider-surface check rule for `SIMPLEFILE_GOOGLE_CLIENT_ID` and `google-oauth-client-id.txt`.

## 6. Release Documentation Is Stale Or Broken

Severity: P2

Evidence:
- `.github/RELEASE.md` links to missing `docs/CLOUD_DRIVES.md`.
- `.github/RELEASE.md` and `docs/RELEASE_1.1.0.md` describe macOS and Linux artifacts.
- `.github/workflows/release.yml` currently builds Windows x64 only.
- `.github/RELEASE.md` says CI builds Linux/macOS/Windows backend builds, while the CI matrix is Windows x64 only for the release build job.

Why it matters:
- Release operators could expect artifacts that are not produced.
- Missing docs links make the release checklist unreliable.

Fix options:
- Option A: Rewrite release docs to match the current Windows x64 workflow and remove missing cloud-drive references.
- Option B: Restore cross-platform release jobs and missing docs if cross-platform packaging is actually intended.
- Option C: Split docs into `current-release.md` and archived historical release notes so old platform/cloud references are not mistaken for current process.

Recommended path:
- Option A now. Option C can follow if historical notes need preservation.

## 7. Linux Native Integration Plan Does Not Belong In The Repo Root

Severity: P3

Evidence:
- `linux-native-integration-plan.md` is a Linux default-file-manager plan.
- It contains old absolute `file:///home/vox/Desktop/Ramdisk/SimpleFile-Svelte/...` links.
- Current roadmap marks Linux-only desktop integration internals and macOS/Linux installer targets as out of scope.

Why it matters:
- The root directory presents this as current project direction even though it conflicts with the Windows-focused line.
- Absolute old local paths are brittle and noisy.

Fix options:
- Option A: Delete the file if it is obsolete.
- Option B: Move it to `docs/archive/` and mark it historical, cleaning absolute local paths.
- Option C: Convert it into a future roadmap appendix only if Linux support is intentionally planned again.

Recommended path:
- Option B if the history is useful; otherwise Option A.

## 8. Corrupted Glyph In Live UI

Severity: P3

Evidence:
- `frontend/src/lib/components/layout-shell/ContentShell.svelte` contains `âœŽ` for the secondary path edit button icon.

Why it matters:
- The visible UI likely shows mojibake instead of the intended edit symbol.

Fix options:
- Option A: Replace it with the intended Unicode pencil/edit glyph.
- Option B: Replace text glyphs with a local icon component or existing icon strategy.
- Option C: Use accessible text hidden visually and a CSS mask/icon asset.

Recommended path:
- Option A as a quick fix, unless a broader icon cleanup is planned.

## 9. Duplicate Frontend CSS File

Severity: P3

Evidence:
- `frontend/src/main.ts` imports `./css/styles.css`.
- `frontend/src/app.css` has the same SHA-256 hash as `frontend/src/css/styles.css`.
- No live import of `frontend/src/app.css` was found.

Why it matters:
- Two identical stylesheet files can drift apart later.
- It makes the source layout less clear.

Fix options:
- Option A: Delete `frontend/src/app.css`.
- Option B: Make `frontend/src/app.css` the canonical file and update imports.
- Option C: Keep both only if a tool requires them, with a comment or check proving they must stay identical.

Recommended path:
- Option A, after confirming no template/tool references `app.css`.

## 10. Duplicate Dependabot Frontend NPM Entry

Severity: P3

Evidence:
- `.github/dependabot.yml` defines two identical `package-ecosystem: "npm"` entries for `directory: "/frontend"`.

Why it matters:
- Duplicate dependency update rules can create noise or confusing Dependabot behavior.

Fix options:
- Option A: Remove one duplicate `/frontend` npm block.
- Option B: Keep two blocks only if they intentionally have different labels, schedules, groups, or dependency filters.
- Option C: Consolidate all npm Dependabot rules with grouping if dependency update volume becomes noisy.

Recommended path:
- Option A.

## Validation Already Run

The audit pass ran these checks successfully:
- `npm run check`
- `cargo fmt --all -- --check`
- `cargo test --locked --all-features`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`
- `npm run check:security`
- `npm run smoke:settings`

Working tree note:
- Source files were clean after the audit.
- Ignored validation artifacts remained under `frontend/node_modules/`, `frontend/dist/`, `src-tauri/target/`, and `src-tauri/.secrets/`.

## Proposed Fix Order

1. Fix repository/updater/release-channel metadata.
2. Remove or quarantine the committed license-key generator.
3. Harden or remove the automatic RAR installer path.
4. Sanitize markdown preview output.
5. Remove stale Google/cloud build residue.
6. Correct release docs and archive/remove wrong-scope docs.
7. Clean small maintenance issues: mojibake icon, duplicate CSS, duplicate Dependabot block.
