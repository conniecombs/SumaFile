# 2026-07-14 P1 Repo Metadata And License Cleanup

Goal:
- Finish the remaining priority 1 repairs after the RAR installer hardening.
- The RAR issue was already handled separately.

## P1: Active Updater And Repository Metadata

Problem:
- Active updater, repository, release, security, and About surfaces pointed at
  `conniecombs/SimpleFile-Svelte` even though this program lives in
  `conniecombs/SimpleFile-Windows`.
- `scripts/check-updater-config.mjs` also expected the stale updater endpoint,
  so the check approved the wrong release channel.

Changes made:
- Updated the Tauri updater endpoint in `src-tauri/tauri.conf.json`.
- Updated the updater configuration check in `scripts/check-updater-config.mjs`.
- Updated package repository metadata in `src-tauri/Cargo.toml`.
- Updated the runtime fallback repository URL in `src-tauri/src/updater.rs`.
- Updated release workflow text in `.github/workflows/release.yml`.
- Updated release/updater docs in `.github/RELEASE.md` and
  `docs/UPDATER_RELEASE.md`.
- Updated security advisory link in `docs/CODE_OF_CONDUCT.md`.
- Updated the About dialog repository link in
  `frontend/src/lib/components/legacy-shell-template.html`.
- Updated current changelog compare links in `docs/CHANGELOG.md`.
- Updated `docs/RELEASE_1.1.0.md` metadata language to use
  `conniecombs/SimpleFile-Windows`.

Current intentional exception:
- `frontend/scripts/check-migration-complete.mjs` and
  `frontend/scripts/check-behavior-bridges.mjs` still contain
  `R:\SimpleFile-Svelte` as a forbidden retired migration-script path. Those
  strings are guard literals, not active app metadata, updater configuration, or
  user-facing release links.

## P1: Committed License Signing Private Key

Problem:
- `scripts/generate-license.mjs` committed a private signing key in
  `PRIVATE_KEY_HEX`.
- No active license verification surface justified keeping it in source.

Changes made:
- Deleted `scripts/generate-license.mjs`.
- Confirmed no remaining `PRIVATE_KEY_HEX`, `generate-license`, or license-key
  generation script references outside build notes.
- Existing behavior checks still guard against old frontend license invokes:
  `get_license_status` and `verify_license`.

Operational note:
- Treat the deleted private key as exposed if it was ever used outside local
  testing.

## Validation Passed

- `npm run check`
- `cargo fmt --all -- --check`
- `cargo test --locked --all-features`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`
- Targeted search for active `SimpleFile-Svelte` links outside build notes.
- Targeted search for the removed license private-key generator.
