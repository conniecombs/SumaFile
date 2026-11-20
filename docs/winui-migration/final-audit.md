# Final migration audit

> **Status: historical, not maintained.** This is the audit made on 2026-08-15, the day the Svelte/Tauri application was retired. It is a snapshot of that day, kept as the record of the retirement. It is not updated.
>
> The main things in it that no longer hold:
>
> - **Counts.** The "Check suite" table gives the figures of 2026-08-15: version 1.1.0, 76 domain methods, 6 emitted events, 29 context ids, 40 palette ids, 120 xUnit tests, 77 Rust tests and 213 lockfile crates. On 2026-11-20 the version is 1.1.4 and the tree has 82 domain methods, 9 emitted events, 46 context ids, 73 palette ids, 363 lockfile packages, and about 1,320 xUnit test methods and 785 Rust tests (counted in the source, not by a run).
> - **Updater.** "Residual risk" says in-app `install_update` stays blocked until installer signature verification is in place. It is in place: the service checks an Ed25519 signature and a SHA-256 hash before it starts the installer.
> - **Service lookup.** "Intentionally left" says `ServiceLocator` keeps `src-tauri/target` candidates. They were removed.
> - **Documents.** Several documents named in the tables were later merged into other documents or deleted, so some of those paths no longer exist. `docs/IMPROVEMENT_PLAN.md`, `docs/RUST_MIGRATION_FEASIBILITY.md`, `build_notes/`, `rust-core-extraction.md` and the `parity-*.md` files other than `parity-gate.md` are gone.
>
> The current description of the system is in [`../ARCHITECTURE.md`](../ARCHITECTURE.md). The plugin system is in [`../PLUGINS.md`](../PLUGINS.md), the IPC contract in [`../../ipc/schema/README.md`](../../ipc/schema/README.md), and the parity record in [`parity-gate.md`](parity-gate.md).

**Date:** 2026-08-15  
**Source tree:** `R:\Repos\SimpleFile-Windows`  
**Shipping host:** WinUI 3 unpackaged app + Rust `simplefile-service` (named-pipe JSON-RPC)  
**Parity gate:** [`parity-gate.md`](parity-gate.md) — required `OPEN` rows: none. Retirement completed.

This audit searched the live tree for stale Svelte / Tauri / Vite / `frontend/` references, obsolete commands, dead scripts, unused Rust dependencies, old current-facing docs, and broken workflow assumptions. Historical records (`docs/CHANGELOG.md` past versions, `docs/RELEASE_SIMPLEFILE_1.1.0.md`, `build_notes/`) were left as written.

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
| `docs/RELEASE_SIMPLEFILE_1.1.0.md` | Notes for that shipped version |
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
