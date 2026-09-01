# SumaFile

[![CI](https://github.com/conniecombs/SumaFile/actions/workflows/ci.yml/badge.svg)](https://github.com/conniecombs/SumaFile/actions/workflows/ci.yml)
[![Release](https://github.com/conniecombs/SumaFile/actions/workflows/release.yml/badge.svg)](https://github.com/conniecombs/SumaFile/actions/workflows/release.yml)
[![Installer Smoke](https://github.com/conniecombs/SumaFile/actions/workflows/installer-smoke.yml/badge.svg)](https://github.com/conniecombs/SumaFile/actions/workflows/installer-smoke.yml)
![Version](https://img.shields.io/badge/version-1.0.0-2563eb)
![Platform](https://img.shields.io/badge/platform-Windows%2010%202004%2B-0078D4?logo=windows)
![License](https://img.shields.io/badge/license-proprietary-444444)

SumaFile is a Windows file manager for people who keep several folders, tools,
and inspections open at once. It combines a native WinUI 3 interface with a Rust
filesystem service so everyday browsing stays responsive while heavier work
runs out of the UI process.

It is built for local Windows workflows: dual panes, per-pane tabs, fast
search, rich previews, archives, checksums, Git status, batch rename, cleanup
tools, saved layouts, and installer/update plumbing in one desktop app.

## Contents

- [Status](#status)
- [Highlights](#highlights)
- [Install](#install)
- [Use SumaFile](#use-sumafile)
- [Feature Reference](#feature-reference)
- [Settings and Saved State](#settings-and-saved-state)
- [Keyboard Shortcuts](#keyboard-shortcuts)
- [Develop](#develop)
- [Project Layout](#project-layout)
- [Architecture](#architecture)
- [Testing and Verification](#testing-and-verification)
- [Release and Packaging](#release-and-packaging)
- [Documentation Map](#documentation-map)
- [Security Notes](#security-notes)
- [Known Limitations](#known-limitations)
- [Support](#support)
- [License](#license)

## Status

| Area | Current state |
| --- | --- |
| Product | SumaFile 1.0.0 |
| Platform | Windows 10 2004+ / Windows 11, x64 |
| UI | WinUI 3, unpackaged desktop host |
| Backend | Rust `simplefile-service` over named-pipe JSON-RPC |
| Release formats | NSIS setup, MSI, portable zip |
| Update channel | GitHub Releases plus signed `latest-winui.json` metadata |
| License | Proprietary |

The active product is the WinUI app in `src-winui/` plus the Rust crates in
`crates/`. The older Svelte/Tauri surface has been retired from this branch;
historical migration notes remain under `docs/winui-migration/`.

## Highlights

| Need | SumaFile surface |
| --- | --- |
| Work in two places | Dual-pane mode with independent tabs, history, view, sort, and selection |
| Keep contexts ready | Named layouts for panes, tabs, columns, preview width, sidebar state, and pane split |
| Move files safely | Copy/move progress, cancel, conflict choices, Recycle Bin delete, undo/redo |
| Inspect files quickly | Preview pane and Quick Look for images, PDF, audio, video, text, source, Markdown, and fonts |
| Understand files | Properties, metadata, checksums, text diff, and binary hex/ASCII compare |
| Organize projects | Tags, bookmarks, recents, smart folders, advanced rename, duplicate finder |
| Stay Windows-native | Drive labels, mapped network share status, Open With, terminals, NSIS/MSI packaging |

## Install

Download the latest Windows build from
[GitHub Releases](https://github.com/conniecombs/SumaFile/releases).

| Artifact | Use it when |
| --- | --- |
| `SumaFile_1.0.0_x64-winui-setup.exe` | You want the normal per-user installer |
| `SumaFile_1.0.0_x64-winui.msi` | You need MSI-style Windows deployment |
| `SumaFile_1.0.0_x64-winui-portable.zip` | You want to run the app from an extracted folder |

Requirements:

- Windows 10 2004 or newer, or Windows 11
- x64 Windows
- For `.7z` list/create/extract: install 7-Zip or set `SIMPLEFILE_7Z` to
  `7z.exe`
- For RAR creation/extraction workflows: use the optional RAR tooling in
  Settings -> Tools

After installing, open Settings -> Updates to check published releases. Builds
with trusted updater metadata can download, verify, and launch the NSIS setup
from inside the app. Builds without complete trusted metadata fall back to the
release page.

## Use SumaFile

1. Open SumaFile.
2. Pick a location from drives, Quick Access, bookmarks, recent locations, smart
   folders, or the folder tree.
3. Press `F6` to open or close the second pane.
4. Use `Ctrl+T` for tabs. Each pane has its own tab set and navigation history.
5. Press `Space` for Quick Look, or leave the preview pane open for continuous
   inspection.
6. Use View options -> Layouts to save named workspaces such as "Code review",
   "Photos", or "Archive cleanup".

## Feature Reference

### Navigation

- Dual-pane browsing with independent active pane state
- Per-pane tabs, tab history, and tab switching shortcuts
- Breadcrumb navigation and editable path bar
- Folder suggestions while typing a path
- Quick Access, bookmarks, recent locations, smart folders, and optional tree
- Type-ahead selection in file lists
- Marquee selection in list and grid presentations
- Live directory watching for changed folder contents
- Recycle Bin as a browsable location with restore and empty actions

### Views and Layouts

- Details, list, tiles, and content views
- Per-pane view style, icon size, sort column, and sort direction
- Column presets: default, details, media, developer, and photo
- Explorer-style column resizing, size-to-fit, and horizontal scrolling
- Dark, light, and Windows-default theme modes
- Show or hide Windows hidden/system files
- Optional folder size and Git status columns
- Named saved layouts that capture tabs, panes, view settings, columns, preview,
  sidebar, and pane split state

### File Operations

- Copy, cut, paste, move, rename, new file, and new folder
- Recycle Bin delete and permanent delete
- Transfer progress with bytes, total, rate, ETA, and cancelling feedback
- Conflict handling for skip, replace, rename/keep-both, or fail
- Drag and drop between panes, tabs, and external applications
- Cross-pane copy/move commands
- Undo/redo for create, rename, copy, move, and Recycle Bin delete flows
- Clipboard history for recent copy/cut sets
- Operation history for recent long-running work

### Search and Smart Folders

- Quick filter for the visible directory
- Recursive filename search
- Optional content search from the search box
- Filters for size, date, depth, and hidden-file inclusion
- Batched, cancellable results for large trees
- Smart folders saved from reusable search criteria

### Preview and Inspection

| Kind | Support |
| --- | --- |
| Images | PNG, JPG/JPEG, GIF, SVG, WebP, BMP, ICO, TIFF |
| PDF | Path-backed PDF preview plus metadata |
| Video | MP4, WebM, AVI, MOV, MKV, FLV, WMV, OGG |
| Audio | MP3, WAV, FLAC, OGG, AAC, WMA, M4A, AIFF |
| Text/code | Plain text, source files, scripts, config, logs |
| Markdown | Text preview |
| Fonts | TTF, OTF, WOFF, WOFF2 |

Inspection tools:

- Properties dialog with size, type, timestamps, attributes, and path details
- Metadata for images, PDF, audio, video, and Office package properties
- Checksums: MD5, SHA-1, SHA-256
- Compare files:
  - small UTF-8 files render as side-by-side text diffs
  - binary files, images, executables, invalid UTF-8, and oversized text render
    as byte comparison with hex and ASCII rows

### Archives

SumaFile can list, create, extract, and browse supported archive contents.

| Format | Extension(s) | Notes |
| --- | --- | --- |
| ZIP | `.zip` | Built in |
| TAR | `.tar` | Built in |
| TAR.GZ | `.tar.gz`, `.tgz` | Built in |
| RAR | `.rar` | Uses optional RAR tooling |
| 7-Zip | `.7z` | Uses installed 7-Zip or `SIMPLEFILE_7Z` |

Archive extraction validates entry paths before writing so files stay inside the
chosen destination. TAR extraction skips links and special entries that could
escape the output folder.

### Organization

- Color labels/tags on files and folders
- Bookmarks and recent locations
- Smart folders
- Advanced rename:
  - live preview
  - template tokens
  - regex remove/replace
  - whitespace cleanup and separator conversion
  - extension transforms
  - optional recursive targeting
  - duplicate/invalid-name warnings before applying

### Windows and Developer Tools

- Git branch/status indicators and per-file Git column
- Git pull and push for the current directory
- Open terminal here (`F4`)
- Elevated PowerShell launch
- Open With menu and chooser
- Pinned/recent Open With apps per extension
- About dialog with live version metadata

## Settings and Saved State

Settings is searchable and organized into:

| Section | Controls |
| --- | --- |
| Appearance | Theme, default display style, icon size, column preset, hidden files |
| Navigation | start location, custom path, tab behavior, side menu sections, recent history |
| Behavior | delete confirmation, folder sorting, folder sizes, Git integration |
| Shortcuts | live shortcut list; remapping is not available yet |
| Tools | RAR tooling status/install |
| Updates | current version, update check/install action |
| About | version, project link, app metadata |

Startup can open Home, the last used workspace, or a custom path. The last-used
workspace persists panes, active pane, tabs, view mode, icon size, and sort.
Named layouts are separate saved snapshots available from View options ->
Layouts.

Persistent app data stays on the compatibility path used by earlier releases:

```text
%APPDATA%\com.simplefile.desktop
```

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile reads
the stable `com.simplefile.desktop` data folder and falls back to legacy
`SimpleFile` / `SumaFile` app-data folders when an existing metadata database is
found there.

Runtime logs are written outside that compatibility folder:

```text
%LOCALAPPDATA%\SumaFile\startup.log
%LOCALAPPDATA%\SumaFile\operations.jsonl
```

## Keyboard Shortcuts

Press `F1` or `Ctrl+?` in the app for the live shortcut list.

### Navigation

| Shortcut | Action |
| --- | --- |
| `Ctrl+L` / `Alt+D` | Focus path bar |
| `Enter` in path bar | Go to typed path |
| `Alt+Home` | Go home |
| `Alt+Up` / `Backspace` | Parent folder |
| `Alt+Left` / `Alt+Right` | Back / forward |
| `F5` | Refresh |
| Type letters | Select matching item |
| Arrow keys | Move selection |
| `Shift` + selection keys | Extend selection |

### Files

| Shortcut | Action |
| --- | --- |
| `Enter` | Open selected item |
| `Ctrl+Enter` | Open folder in new tab |
| `Alt+Enter` | Properties |
| `F2` | Rename |
| `Delete` | Move to Recycle Bin |
| `Shift+Delete` | Delete permanently |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | Copy / cut / paste |
| `Ctrl+Shift+C` | Copy path |
| `Ctrl+Shift+V` | Clipboard history |
| `Ctrl+A` | Select all |
| `Ctrl+N` | New file |
| `Ctrl+Shift+N` | New folder |
| `Ctrl+Z` | Undo |
| `Ctrl+Y` / `Ctrl+Shift+Z` | Redo |

### Panes, Tabs, and Tools

| Shortcut | Action |
| --- | --- |
| `F6` | Open or close second pane |
| `Tab` | Switch active pane when dual pane is open |
| `Alt+1` / `Alt+2` | Focus left / right pane |
| `Ctrl+Shift+Left` / `Ctrl+Shift+Right` | Focus left / right pane |
| `Ctrl+T` | New tab |
| `Ctrl+W` | Close tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+1`-`Ctrl+9` | Jump to tab; 9 means last tab |
| `Ctrl+Alt+C` / `Ctrl+Alt+M` | Copy / move to other pane |
| `Space` | Quick Look |
| `Ctrl+H` | Show or hide hidden files |
| `Ctrl+B` | Bookmark current folder |
| `Ctrl+Mouse wheel` | Change icon size |
| `Ctrl+F` / `F3` | Focus search |
| `Ctrl+Shift+P` | Command palette |
| `F4` | Open terminal here |
| `Escape` | Close transient UI, clear filter, or clear selection |

## Develop

### Prerequisites

| Tool | Version | Why |
| --- | --- | --- |
| Windows | 10 2004+ or 11, x64 | App target |
| Node.js | 24+ | Repo orchestration and guard scripts |
| Rust | Stable MSVC toolchain | `simplefile-service` and `simplefile-core` |
| .NET SDK | 10+ | `net10.0-windows` WinUI projects |
| Windows SDK | 10.0.19041+ | WinUI target platform |
| NSIS | Optional | Local setup executable |
| WiX v3 | Optional | Local MSI |
| 7-Zip | Optional | `.7z` archive operations |

### Local App

```powershell
git clone https://github.com/conniecombs/SumaFile.git
cd SumaFile
npm run dev
```

`npm run dev` builds the Rust service and starts the WinUI host. To use a
specific service binary while developing the UI, set:

```powershell
$env:SIMPLEFILE_SERVICE_PATH = "R:\path\to\simplefile-service.exe"
npm run dev:winui
```

### Common Commands

Run these from the repository root.

| Command | Purpose |
| --- | --- |
| `npm run dev` | Build service and run the WinUI app |
| `npm run build:winui` | Build the WinUI solution in Debug |
| `npm run check:winui` | Run the WinUI xUnit suite |
| `npm run check:rust` | Rust format check, tests, and Clippy with warnings denied |
| `npm run check` | Repo guard scripts: IPC, identity, updater, workflows, packaging, parity |
| `npm run check:release` | `check` + Rust checks + security audit |
| `npm run build` | Build release payload/installers through the WinUI release script |
| `npm run release:build` | Local release pipeline plus smoke checks |
| `npm run generate:ipc-bindings` | Regenerate schema-derived Rust/C# IPC bindings |
| `npm run smoke:winui` | Launch smoke for built payload |
| `npm run smoke:winui-msi` | MSI extraction/launch smoke |
| `npm run smoke:winui-installer` | NSIS install/launch/uninstall smoke |
| `npm run smoke:winui-upgrade` | Previous installer -> local installer upgrade smoke |

## Project Layout

```text
SumaFile/
|-- src-winui/
|   |-- SimpleFile.App/       WinUI window, dialogs, preview, app chrome
|   |-- SimpleFile.Core/      Workspace, settings, menus, transfers, layout state
|   |-- SimpleFile.Ipc/       Named-pipe JSON-RPC client and generated bindings
|   `-- SimpleFile.Tests/     xUnit tests for WinUI/core/IPC behavior
|-- crates/
|   |-- simplefile-core/      Host-independent filesystem, archive, preview logic
|   |-- simplefile-ipc/       Protocol constants, framing, schema tests
|   `-- simplefile-service/   Rust named-pipe service process
|-- ipc/schema/               JSON-RPC command/type/event contract
|-- packaging/winui/          NSIS, WiX, and icon assets
|-- scripts/                  Checks, release scripts, smoke tests, codegen
|-- docs/                     Roadmap, support, updater, release, migration docs
|-- build_notes/              Maintainer notes from hardening/migration work
|-- .github/workflows/        CI, release, release-build, installer smoke
|-- package.json              Root command runner
|-- Cargo.toml                Rust workspace
`-- LICENSE                   Proprietary license
```

## Architecture

```text
SumaFile.exe
  WinUI 3 window
  ExplorerWorkspace
  Settings, dialogs, preview, toolbar, panes
        |
        | length-prefixed named-pipe JSON-RPC
        v
simplefile-service.exe
  dispatch handlers
  progress events, search events, watcher events
        |
        v
simplefile-core
  file ops, archive, preview, metadata, checksum,
  compare, drives, tags, smart folders, updater
```

Important contracts:

- The app owns one service process per UI process.
- The service is tied to the UI process with a Windows job object; shell-opened
  files are allowed to outlive SumaFile.
- IPC contracts live in `ipc/schema/v1` and generated bindings must stay
  current.
- The app data directory remains `%APPDATA%\com.simplefile.desktop` for
  compatibility.
- Startup panic/log output uses `%LOCALAPPDATA%\SumaFile`.
- Windows packaging emits `SumaFile.exe` and `simplefile-service.exe` together.

## Testing and Verification

### Everyday development

```powershell
npm run build:winui
npm run check:winui
npm run check
npm run check:rust
git diff --check
```

### Release-quality proof

```powershell
npm run check:release
npm run release:build
```

### What the gates cover

| Gate | Coverage |
| --- | --- |
| `check:ipc-generated` | generated Rust/C# IPC bindings match schema |
| `check:ipc-schema` | schema, service dispatcher, and C# client stay aligned |
| `check:identity` | current-facing files use the SumaFile repository identity |
| `check:updater` | updater URL, signature, and manifest wiring |
| `check:workflows` | GitHub Actions workflow surface |
| `check:provider-surface` | excludes out-of-scope external storage integration surfaces |
| `check:windows-assets` | Windows app and packaging assets |
| `check:winui-packaging` | NSIS/MSI/release-script contracts |
| `check:winui-parity-gate` | WinUI parity rows remain PASS/WAIVED |
| `check:winui` | xUnit coverage for app/core/IPC behavior |
| `check:rust` | Rust fmt, tests, and Clippy |
| `check:security` | Rust dependency audit |

Installer smoke coverage is slower by design. Use the nightly/manual Installer
Smoke workflow or local smoke commands before shipping release assets.

## Release and Packaging

### Outputs

`scripts/build-winui-release.ps1` writes release output under `dist/winui/`.

| Output | Contents |
| --- | --- |
| `payload/` | runnable unpackaged app folder |
| `SumaFile_<version>_x64-winui-portable.zip` | portable app package |
| `SumaFile_<version>_x64-winui-setup.exe` | NSIS per-user installer |
| `SumaFile_<version>_x64-winui.msi` | MSI package |
| `latest-winui.json` | updater manifest for published releases |

The portable and installed app folder must contain both `SumaFile.exe` and
`simplefile-service.exe`.

### Version Fields

User-facing version is `1.0.0`. Numeric version is also `1.0.0` for Windows,
MSBuild, WiX, NSIS, and Cargo package identifiers.

Keep these synchronized:

- `src-winui/Directory.Build.props`
  - `<InformationalVersion>`: user-facing version
  - `<Version>`: numeric package identity
- `crates/simplefile-core/src/lib.rs`
  - `APP_DISPLAY_VERSION`
- `crates/simplefile-core/Cargo.toml`
  - package `version`
- `crates/simplefile-ipc/Cargo.toml`
  - package `version`
- `crates/simplefile-service/Cargo.toml`
  - package `version`
- `Cargo.lock`
- README badge
- `docs/CHANGELOG.md`

Release process details live in [.github/RELEASE.md](.github/RELEASE.md).
Updater signing details live in [docs/UPDATER_RELEASE.md](docs/UPDATER_RELEASE.md).

## Documentation Map

| Document | Use it for |
| --- | --- |
| [docs/CHANGELOG.md](docs/CHANGELOG.md) | Current and historical changes |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Near-term priorities and branch scope |
| [docs/FEATURE_OPPORTUNITIES.md](docs/FEATURE_OPPORTUNITIES.md) | Candidate product gaps and recently closed gaps |
| [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) | Contributor workflow and expectations |
| [docs/SUPPORT.md](docs/SUPPORT.md) | Useful details for reports |
| [docs/SECURITY.md](docs/SECURITY.md) | Vulnerability reporting and sensitive-file rules |
| [docs/UPDATER_RELEASE.md](docs/UPDATER_RELEASE.md) | Signed updater releases |
| [docs/RELEASE_1.0.0.md](docs/RELEASE_1.0.0.md) | 1.0.0 release checklist, dogfood script, and limitations |
| [.github/RELEASE.md](.github/RELEASE.md) | Release checklist |
| [src-winui/README.md](src-winui/README.md) | WinUI host build/run notes |
| [docs/winui-migration/](docs/winui-migration/) | Historical migration architecture and parity records |

## Security Notes

- Do not commit updater private keys, signing keys, `.env` files, local secrets,
  personal settings exports, or logs with private paths.
- Gitignored sensitive locations include `.secrets/`, `*.key`, `.env`, and
  `.env.*`.
- Archive extraction validates destination paths before writing output.
- TAR extraction skips links and special entries.
- Open With refuses executable/script payload files as targets.
- Report vulnerabilities through [docs/SECURITY.md](docs/SECURITY.md).

## Scope

In scope for this branch:

- Local disks, removable media, and mapped network shares
- WinUI desktop browsing, dual panes, tabs, layouts, search, previews, metadata,
  Git status/actions, archives, cleanup, updater, and Windows installers

Out of scope for this branch:

- App-managed external storage-account integrations
- macOS or Linux desktop packages
- Shortcut remapping

## Known Limitations

- SumaFile 1.0.0 is Windows-only and targets Windows 10 2004+ / Windows 11 x64.
- The first 1.0.0 install is manual. In-app updates require a published GitHub
  release with signed `latest-winui.json` metadata.
- Optional archive tooling is still external: `.7z` workflows require 7-Zip, and
  RAR creation/extraction uses the Settings -> Tools flow.
- Account-backed storage integrations are intentionally out of scope for this
  release; local folders, local drives, mapped network shares, and archives are
  the supported storage surfaces.

## Support

When reporting a problem, include:

- Windows version
- SumaFile version from Settings -> About or the About dialog
- Whether you used installer, MSI, portable zip, or dev run
- The exact path/workflow where the issue happened, with private path parts
  redacted if needed
- Relevant log snippets from:

```text
%LOCALAPPDATA%\SumaFile\startup.log
%LOCALAPPDATA%\SumaFile\operations.jsonl
```

For installer/update issues, also say whether these commands pass locally:

```powershell
npm run smoke:winui
npm run smoke:winui-installer
npm run smoke:winui-msi
npm run smoke:winui-upgrade
```

More detail: [docs/SUPPORT.md](docs/SUPPORT.md).

## License

SumaFile is proprietary software. Copyright (c) 2024-2026 conniecombs.
All rights reserved.

Access to this repository or possession of a copy does not grant permission to
use, copy, modify, redistribute, sublicense, host, resell, or create derivative
works without prior written permission.

See [LICENSE](LICENSE) for the full terms. Third-party libraries remain under
their own licenses.
