# Final migration audit

**Date:** 2026-08-15  
**Source tree:** `R:\Repos\SimpleFile-Windows`  
**Shipping host:** WinUI 3 unpackaged app + Rust `simplefile-service` (named-pipe JSON-RPC)  
**Parity gate:** [`parity-gate.md`](parity-gate.md) — required `OPEN` rows: none. Retirement completed.

This audit searched the live tree for stale Svelte / Tauri / Vite / `frontend/` references, obsolete commands, dead scripts, unused Rust dependencies, old current-facing docs, and broken workflow assumptions. Historical records (`docs/CHANGELOG.md` past versions, `docs/RELEASE_1.1.0.md`, `build_notes/`) were left as written.

---

## Search method

Inspected with `rg` / `Get-Content` over `.yml`, `.json`, `.mjs`, `.ps1`, `.md`, `.toml`, `.cs`, `.csproj`, `.nsi`, `.wxs`, `.gitignore`, and crate sources:

- `tauri`, `svelte`, `vite`, `frontend/`
- `cargo tauri`, `build:tauri`, `smoke:release`, `smoke:msi`, `smoke:installer`, `check:invokes`, `check:tauri`, `check:frontend`, `tauri.conf`
- Workspace / crate `Cargo.toml` dependency use sites
- `.github/workflows/*`, `package.json`, `scripts/`

---

## Confirmed stale artifacts (fixed)

| Area | Finding | Fix |
| --- | --- | --- |
| `docs/BUGS.md` | Instructed `smoke:settings` / `smoke:release` / `smoke:msi` / `smoke:installer` and `frontend/` regression paths | Rewrote to WinUI smokes and `crates/` / `src-winui` paths |
| `docs/STARTUP_FIX_NOTES.md` | Presented Svelte/Vite/Tauri updater as current | Rewrote for WinUI host, service job object, `startup.log`, `latest-winui.json` |
| `docs/UI_BACKEND_REVIEW.md` | “Current state” was typed Tauri + deleted `check-tauri-invokes` / `build:tauri:local` | Rewrote to WinUI + IPC checks |
| `docs/CODE_ANALYSIS.md` | Mapped the deleted Svelte/Tauri tree as the live app | Rewrote to `src-winui` + `crates/` |
| `docs/IMPROVEMENT_PLAN.md` | Completed plan still read as current Svelte/Tauri paths | Added historical banner; body kept |
| `docs/winui-migration/inventory.md` | Still said “do not delete Svelte/Tauri” | Marked historical; retirement complete |
| `docs/winui-migration/rust-core-extraction.md` | Same pre-retirement constraint | Marked historical |
| `docs/winui-migration/parity-*.md` | Said Svelte/Tauri remains the shipping UI | Historical status line |
| `.github/ISSUE_TEMPLATE/bug_report.md` | “Tauri dev” / `cargo tauri dev` | WinUI install sources + `startup.log` |
| `.github/workflows/ci.yml` | Job id `frontend-sanity` | Renamed to `repo-checks` (`needs` updated) |
| `.gitignore` | `.vite/` leftover | Removed |
| `scripts/check-ipc-schema.mjs` | Dead `TauriCommandMap` / `TauriEventMap` helpers; “Tauri handlers” copy | Removed unused helpers; domain-handler wording |
| `scripts/check-provider-surface.mjs` | `.svelte-kit` ignore + `.svelte` scan | Removed |
| `scripts/build-winui-release.ps1` | Unused `Read-JsonFile` after Tauri version source dropped | Removed |
| `crates/simplefile-service/Cargo.toml` | Direct `getrandom` unused (core still uses it) | Removed unused direct dependency |

---

## Intentionally left

| Item | Why |
| --- | --- |
| `ServiceLocator` `src-tauri/target/{debug,release}` candidates | Existing local service binaries may still live there |
| `.gitignore` `/src-tauri/target/` | Same leftover build dir |
| C# comments citing `frontend/src/...` | Provenance of the port, not live commands |
| `events.json` `tauri://drag-*` | Host-only schema names, marked as such |
| `docs/CHANGELOG.md` historical entries | Past release record |
| `docs/RELEASE_1.1.0.md` | Notes for that shipped version |
| `docs/RUST_MIGRATION_FEASIBILITY.md` | Already labeled historical (native Rust GUI, not this migration) |
| `build_notes/` | Dated audit/hardening notes |
| `Cargo.lock` `tauri*` crates | None remain |

Unused workspace crate dependencies besides service `getrandom` were not found: core still uses chrono, log, notify, parking_lot, rusqlite, trash, getrandom, filetime, base64, flate2, image, md-5, sha1, sha2, tar, unrar, exif, lopdf, lofty, zip, and winapi.

---

## Workflow / command assumptions

Current root scripts (`package.json`) are WinUI-only: `dev`, `build`, `check*`, `smoke:winui*`, `release:*`. Retired Tauri npm scripts are absent.

Workflows:

- `ci.yml` — Rust quality, `npm run check`, audit, `simplefile-service` build, WinUI tests
- `release.yml` — versions from `Directory.Build.props` + `crates/simplefile-service/Cargo.toml`; WinUI NSIS/MSI/portable + `latest-winui.json`
- `release-build.yml` / `installer-smoke.yml` — WinUI artifacts and smokes
- `dependabot.yml` — cargo + root npm + GitHub Actions (no `/frontend`, no `tauri*` group)

---

## Check suite

| Command | Result |
| --- | --- |
| `npm run check` | Pass - 76 domain methods, 6 emitted events, 12 goldens; updater 1.1.0; workflows; provider-surface guard; WinUI packaging; parity gate 76 commands / 29 context ids / 40 palette ids |
| `npm run check:winui` | Pass — 120 xUnit tests |
| `npm run check:rust` | Pass — `cargo fmt --check`; 77 Rust tests; Clippy `-D warnings` |
| `npm run check:security` | Pass — `cargo-audit` on 213 lockfile crates |
| `npm run smoke:winui` / `smoke:winui-msi` / `smoke:winui-installer` | Not run — need `npm run build:winui:release` plus NSIS/WiX |

---

## Residual risk (not stale artifacts)

- Unpackaged WinUI still requires `resources.pri` + `*.xbf` beside the exe (covered by the publish targets and `build-winui-release.ps1`).
- In-app `install_update` stays blocked until installer signature verification is in place; `check_for_update` still reads `latest-winui.json`.
