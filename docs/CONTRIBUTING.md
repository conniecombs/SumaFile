# Contributing

SumaFile is a Windows-only file manager: a WinUI 3 app in C#, a Rust file
service, and three bundled plugins. Changes should serve local files, Windows
drives, mapped network drives, archives, search, previews, transfers and the
plugin system. [Roadmap](ROADMAP.md) lists open work and what is out of scope.

Read [Architecture](ARCHITECTURE.md) before a first change: it says which
process and project owns what, and which contracts a change must keep.
Participation is covered by the [code of conduct](CODE_OF_CONDUCT.md).

## Setup

Work on Windows 10 version 2004 or later, or Windows 11, x64. The tool versions
are pinned exactly, and CI uses the same ones.

| Tool | Version | Pinned in |
| --- | --- | --- |
| Node.js | 24.20.0 | `.node-version` |
| Rust | 1.98.0, with rustfmt and clippy | `rust-toolchain.toml` |
| .NET SDK | 10.0.402 | `global.json`; roll-forward is disabled, so another SDK is refused |

```powershell
npm ci --ignore-scripts
npm run dev
```

`npm ci` installs the repository tooling that `npm run check` needs.
`npm run dev` builds the Rust service, builds and stages the bundled plugins,
and starts the app.

## Where things are

| Path | Contents |
| --- | --- |
| `src-winui/SimpleFile.App` | The WinUI 3 window, dialogs and controls |
| `src-winui/SimpleFile.Core` | Workspace state, navigation, transfers, menus, settings; no WinUI types |
| `src-winui/SimpleFile.Ipc` | The named-pipe client and the wire models |
| `src-winui/SimpleFile.PluginSdk`, `SimpleFile.PluginHost` | The plugin contract and its host |
| `src-winui/SimpleFile.Tests` | xUnit tests for all of the above |
| `crates/simplefile-service` | The file service process: sessions, dispatch, listing views, transfers, search, watcher |
| `crates/simplefile-core` | Filesystem and domain logic without transport |
| `crates/simplefile-ipc` | Framing and generated protocol names |
| `crates/sumafile-transfer` | Transfer policy, staging, ownership records, retry and tuning |
| `crates/sumafile-plugin` | The helper crate for plugin backends |
| `plugins/` | The bundled Git, FTP/SFTP and 7-Zip plugins |
| `templates/sumafile-plugin` | A starting point for a third-party plugin |
| `ipc/schema/` | The IPC contract both sides are generated from and checked against |
| `scripts/`, `.github/workflows/` | Checks, build, smoke and release tooling |

## Rules that keep the two sides in step

- **IPC is schema first.** A command, event or wire type is declared in
  `ipc/schema/v1`, then `npm run generate:ipc-bindings` writes the method
  constants and client wrappers for C# and Rust. Do not edit a `*.Generated.cs`
  or `protocol_generated.rs` file by hand. `npm run check` fails when the
  schema, the generated files, the service's handlers and the Rust models
  disagree. [Architecture](ARCHITECTURE.md) lists the steps to add a method.
- **A folder opens inside SumaFile.** Navigation keeps the intent "open this
  folder" until it reaches the pane; only files are handed to Windows.
- **Mapped network drives are ordinary drives.** They stay visible in the drive
  list with their share names. Drive behaviour lives in
  `crates/simplefile-core/src/drives.rs`.
- **The startup budget does not move.** `npm run check:winui-startup-budget`
  holds plugin discovery and the startup stages to fixed limits.
- **Plugin documentation is checked.** A new host method, contribution kind or
  host-shared assembly must be described in [the plugin guide](PLUGINS.md) or
  the checks fail.
- **NuGet versions are locked.** Every project commits a `packages.lock.json`;
  a package change must update the lock files of the projects that reach it.

## Checks

Before opening a pull request:

```powershell
npm run check
npm run test:maintenance
npm run check:rust
npm run check:security
```

`npm run check` covers the IPC schema, generated bindings, workflow policy,
packaging and identity checks. `npm run test:maintenance` runs the script
tests, including the ones that read the documentation. `npm run check:rust`
runs rustfmt, clippy with warnings denied, and the Rust tests.
`npm run check:security` runs the dependency audit and needs `cargo-audit`.

The C# tests start the real service and the plugins, so build those first:

```powershell
cargo build -p simplefile-service
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1
npm run check:winui
```

For a change to what the window shows or how it performs, also run the UI
smoke, described in [runtime verification](WINUI_RUNTIME_VERIFICATION.md):

```powershell
npm run smoke:winui-ui
```

For release or installer changes, follow [the release runbook](../.github/RELEASE.md).

Pull requests into `main` need the `CI required` check to pass on an up-to-date
branch and every review conversation resolved.
[CI and maintenance](CI_MAINTENANCE.md) describes the jobs and the automation
that maintains dependencies.

## Pull requests

- Keep unrelated refactors out of a focused fix.
- Add a test that fails without the fix, and say in the description what was
  verified on Windows and what was not.
- Update the documentation the change makes untrue: the feature reference in
  the [README](../README.md), the `[Unreleased]` section of the
  [changelog](CHANGELOG.md), and the reference for the area
  ([transfers](TRANSFER_TUNING.md), [plugins](PLUGINS.md),
  [architecture](ARCHITECTURE.md)).
- Do not commit signing keys, private settings, logs with personal paths, or
  built installers.
