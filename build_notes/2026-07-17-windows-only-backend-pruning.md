# 2026-07-17 Windows-Only Backend Pruning

Issue fixed:
- `build_notes/2026-07-17-next-recommended-work.md` issue 3:
  backend source still carried non-Windows branches and Unix-only dependencies.

Related cleanup:
- `build_notes/2026-07-17-next-recommended-work.md` issue 6:
  workflow cargo-audit ignore policy was duplicated inline.

## Problem

The program is Windows-only, but backend Rust modules still contained macOS,
Linux, Unix metadata, Unix symlink, and Unix terminal paths. `Cargo.toml` also
declared Unix-only direct dependencies for code that should no longer be part
of this repository's supported runtime. CI still ran Rust quality gates on
Ubuntu, which would conflict with a Windows-only backend.

## Files Changed

### `src-tauri/Cargo.toml` And `src-tauri/Cargo.lock`

Changes:
- Removed the direct `[target.'cfg(unix)'.dependencies]` section.
- Removed direct `libc` and `xattr` dependencies from the `simplefile` lockfile
  dependency list.
- Left transitive lockfile entries alone where other dependencies still own
  them.

### `src-tauri/src/drives.rs`

Changes:
- Removed Unix `statvfs` disk-space helper.
- Removed macOS `/Volumes` enumeration.
- Removed Linux root/mount-directory scanning.
- Kept Windows drive enumeration, volume labels, and mapped-network-drive
  display naming.

### `src-tauri/src/open_with.rs`

Changes:
- Removed macOS/Linux trusted application roots.
- Made executable detection Windows-extension based.
- Kept trusted Windows install-root enforcement and denied executable/script
  payload handling.

### `src-tauri/src/terminal.rs`

Changes:
- Removed Linux terminal emulator probing.
- Removed macOS Terminal launch path.
- Kept PowerShell and elevated PowerShell launch flows.

### `src-tauri/src/fs_ops.rs`

Changes:
- Removed Linux/macOS no-replace rename implementations that depended on
  `libc`.
- Removed Unix xattr metadata preservation.
- Removed Unix symlink creation/removal branches.
- Kept Windows symlink, creation-time, DACL preservation, conflict handling,
  and case-insensitive destination collision behavior.

### `src-tauri/src/progress.rs`

Changes:
- Removed non-Windows symlink deletion.
- Removed Unix symlink creation.
- Kept Windows symlink copy and conflict behavior.
- Simplified path collision keys to Windows case-insensitive behavior.

### `src-tauri/src/archive.rs`

Changes:
- Removed non-Windows symlink deletion.
- Simplified archive path comparison fallback to Windows case-insensitive
  behavior.

### `src-tauri/src/utils.rs`

Changes:
- Removed non-Windows `hidden_command` branch.
- Removed Unix permission string generation.
- Kept hidden Windows process launch behavior.

### `.github/workflows/ci.yml`

Changes:
- Moved the Rust quality gate to `windows-latest`.
- Removed Linux/Tauri dependency installation from the Rust quality gate.
- Routed the security job through `node scripts/cargo-audit-release.mjs`.
- Added Node setup to the security job so the existing audit script owns the
  advisory ignore policy.

### `.github/workflows/release.yml`

Changes:
- Moved release quality gates to `windows-latest`.
- Removed Linux/Tauri dependency installation from release quality gates.
- Routed release cargo-audit through `node scripts/cargo-audit-release.mjs`.

### `scripts/check-github-workflows.mjs`

Changes:
- Updated workflow expectations to require `node scripts/cargo-audit-release.mjs`.
- Added guards requiring Rust CI and release quality gates to run on
  `windows-latest`.
- Added guards rejecting the retired Linux/Tauri dependency install and inline
  `--ignore RUSTSEC-` workflow policy.

## Deliberately Preserved

- Windows mapped network drive display helpers in `src-tauri/src/drives.rs`.
- Windows symlink handling for directory symlinks/junctions and file symlinks.
- Windows DACL preservation in copy operations.
- Historical docs/build notes that mention older macOS/Linux work.

## Validation Passed

- Searches across `src-tauri/src/*.rs` and `src-tauri/Cargo.toml` for
  `cfg(unix)`, `not(windows)`, `target_os = "linux"`, `target_os = "macos"`,
  `std::os::unix`, `libc::`, and `xattr` returned no matches.
- Searches in `src-tauri/Cargo.toml` for `cfg(unix)`, `libc`, and `xattr`
  returned no matches.
- Workflow search showed:
  - CI Rust quality gate runs on `windows-latest`.
  - Release quality gates run on `windows-latest`.
  - CI and release audits call `node scripts/cargo-audit-release.mjs`.
  - Retired Linux/Tauri install snippets and inline RustSec ignore snippets are
    absent from CI and release workflows.
- `cargo test --offline --all-features` updated the lockfile without network and
  passed.
- `cargo fmt --all -- --check` passed.
- `cargo test --locked --all-features` passed.
- `cargo clippy --locked --all-targets --all-features -- -D warnings` passed.
- `npm run check:workflows` passed.
- `npm run check:security` passed after allowing cargo-audit to access the
  user Cargo advisory database outside the workspace sandbox.
- `npm run check` passed.
- `git diff --check` passed.
