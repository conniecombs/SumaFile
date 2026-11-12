# Changelog

All notable changes to SumaFile are documented here.
This project follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Beta: the plugin system

The Beta branch. It has no version number yet; it gets one when it is merged
into `main`. See the [Beta notes](RELEASE_BETA.md).

- A plugin system: a plugin is a folder with a `plugin.json` manifest, an
  optional C# UI half that SumaFile loads into its own process, and an
  optional backend process that talks to SumaFile over a named pipe. The
  WinUI plugin host renders commands, context menus, toolbar buttons, sidebar
  sections, panels, file row decorations, declarative settings, settings
  pages and archive formats from the manifest before any plugin code runs.
  Settings -> Plugins lists every plugin, with Install from file (`.sfplugin`
  packages), Rescan and Open plugins folder, and gives each plugin its own
  page under Plugins for its switch, settings and Remove.
- SumaFile asks before it enables a plugin it did not ship (**Enable
  plugin?**) and asks again when the plugin's `plugin.json`, its signature
  state or its folder changes, and after Install from file or Remove. There
  is no sandbox; see [Security](SECURITY.md#plugins).
- Optional Ed25519 signatures (`signature.json`): the plugin card shows
  "Signed · key <fingerprint>". A signature proves that the files are
  unchanged since they were signed with the key in the plugin's own
  `plugin.json`, not who the publisher is. A file in the folder that
  `signature.json` does not list makes the signature invalid.
- Git, FTP/FTPS/SFTP and 7-Zip moved out of the core product into three
  first-party plugins that install with SumaFile and are trusted without a
  prompt: `com.sumafile.git`, `com.sumafile.remote` and
  `com.sumafile.sevenzip`. A default install keeps the 1.0.4 features. FTP/SFTP
  profiles are imported, read-only, from the 1.0.4 `metadata.db`, and saved
  passwords stay in Windows Credential Manager under `SumaFile.Remote.`.
- The file service's IPC schema drops the 13 remote and 11 Git methods and
  adds `register_archive_engine` and `unregister_archive_engine`: 97 domain
  methods become 75.
- Every plugin file stays in SumaFile's installation folder: `plugins\`,
  `plugins-data\` (each backend's data and temporary files),
  `plugin-approvals.json` and `plugins.log`. Upgrades keep them. Uninstall
  removes them, including user-installed plugins and the FTP/SFTP profiles
  in `plugins-data\com.sumafile.remote`; saved passwords and passphrases
  stay in Windows Credential Manager (`SumaFile.Remote.*`).
- Third-party readiness: the author guide [PLUGINS.md](PLUGINS.md), the
  template in `templates/sumafile-plugin`, the signing tool
  `scripts/sign-plugin.mjs`, and dry-run packages of the SDK
  (`SumaFile.PluginSdk`, `npm run pack:sdk`) and the Rust helper crate
  (`sumafile-plugin`, `npm run pack:crates`). Nothing is published yet.

### Other changes

- "Write directly to the destination" now applies to folder copies, replaced
  files and remote downloads, not only to new single files. Folders are
  created under their final names and replaced files are overwritten in
  place; the option's note states that a cancelled or failed transfer leaves
  partial files behind.
- Show hidden files now lists SumaFile's own transfer artifacts (partial
  files, ownership records and replacement backups) instead of always
  dropping them; they stay hidden while hidden files are hidden.
- Automatic transfer tuning decides sooner and with more to go on. The first
  number of simultaneous files depends on the drives (one or two for a hard
  disk, more between solid-state drives, in between for a network share).
  Speed is read in windows of one to two seconds instead of pairs of five
  seconds, so a decision takes about three to five seconds on a steady
  transfer. Files under 1 MiB and larger files are scheduled in two lanes
  that share the file ceiling, so a few huge files no longer slow a batch of
  small ones. A sustained rise in write latency ends a test of more files
  early. Custom mode and chosen queue orders are unchanged. See
  [Transfer tuning](TRANSFER_TUNING.md#how-the-scheduler-decides).
- Many small files to or from a network share or cloud drive copy with more
  files at once. The small-file lane is now judged on files per second
  instead of bytes per second, starts with 16 files on such routes, tries
  more before it tries fewer, and may grow to 32 while the file ceiling is
  still the default 16 (a ceiling you changed is kept). Repeated bouts of
  I/O retries hold the reduced setting longer each time. Files that fit in
  one copy buffer are no longer pre-allocated, which saves one request per
  small file on a network share.
- The status bar's item count, with the selection's count and size, now sits
  beside the folder address at the bottom left instead of at the far right.
- Properties is grouped: the item's name and type as a heading, then General,
  Attributes, the file's metadata groups (the ones the preview pane uses),
  and Checksums for a file or Contents for a folder. Values can be selected
  and copied.
- A folder's Properties has a **Calculate totals** button that counts every
  file and folder inside it, including subfolders: Files, Folders, Total
  items and Total size, plus the number of items directly inside. Properties
  no longer walks a folder as soon as it opens, so the size also waits for
  the button; the Folder metrics command on one folder still measures at
  once. The row that used to read "Total Items" counted only the top level
  and is now "Directly inside". `get_folder_metrics` returns two new fields,
  `total_files` and `total_folders`.
- Maintenance pull requests can now reach the automatic merge check: their
  CI run starts it directly when it passes, and a pull request whose only
  outstanding check is its own approval-held CI run is no longer refused.
  Branch protection on `main` is still required before anything merges. See
  [CI maintenance](CI_MAINTENANCE.md).

---

## [1.0.4] - prepared 2026-11-06

- Context menus grey out unavailable commands, Delete is a direct item, and
  the More menu keeps only toolbar, Tools, Profiles and pane commands.
- Tab, Space and Backspace act on the file list only; new default chords for
  new tab (Ctrl+N), back (Backspace), terminal (Ctrl+`), Settings (Ctrl+,),
  shortcut help (Ctrl+/) and the preview pane (Alt+P).
- The omnibar returns to Path mode after a search or filter and resolves bare
  names against the current folder before falling back to a filter.
- Conflict dialog with one choice list and Continue; the Transfers window no
  longer activates itself; transfer options split into basic and Advanced;
  failure text in plain language; Recycle Bin prompt defaults to confirm.
- Advanced Rename drops its second confirmation; Large files and duplicates
  replaces Disk cleanup; Duplicate Checker hides engine settings; Quick Look
  becomes a lightbox; inline rename in Details.
- Preview collapses only when cramped; Source/Rendered switch; Git UI only
  inside repositories; Discard warns about untracked files.
- FTP/SFTP connections: visible plain-FTP consent, SFTP trust prompt, hidden
  credential target. Settings search covers controls, Light theme, sentence
  case labels, Developer group, Transfer options row.
- One name per concept: Labels, sidebar, Profiles, Connections.
- See the [v1.0.4 release notes](RELEASE_1.0.4.md).

---

## [1.0.3] - prepared 2026-11-06

- The omnibar combines navigation, commands, search and filtering. Multi-selection
  survives refreshes, previews release media when closed, and dialogs respect DPI,
  theme, keyboard focus and customized shortcuts.
- One scheduler serves an entire selection. Transfers continue past individual
  failures and retry the exact failed paths. Folder merges preserve skipped move
  sources, and the receipt distinguishes them from completed items.
- Improved Windows staging and metadata handling, bounded busy-file retries,
  adaptive concurrency, opt-in service-level multistream and checksum verification.
- Slow operations release the IPC loop. Archive extraction, search, recycle-bin
  restore, multi-tag persistence, Git status, SFTP sessions and updater downloads
  have fixes detailed in the [v1.0.3 release notes](RELEASE_1.0.3.md).
- Updated the WinUI, Rust, installer and lockfile versions and refreshed the
  README screenshots using generated files on a hosted Windows runner.

---

## [1.0.2] - 2026-11-05

Published November 5, 2026. The [v1.0.2 release](https://github.com/conniecombs/SumaFile/releases/tag/v1.0.2) contains the Windows downloads.
The baseline is the published v1.0.1 tag (October 20, 2026), not the earlier
checklist date. See [complete release notes](RELEASE_1.0.2.md) for all 19
application commits, dependency changes, upgrade instructions and limitations.

### Added
- Automatic, Conservative, Maximum throughput and Custom transfer policies;
  persisted defaults, per-job overrides, active-file rows and tuning explanations.
- Controls for concurrency, directory checkers, ordering/backlog, buffer budgets,
  Windows multistream copying, integrity, preallocation, in-place behavior,
  flush-on-close, bandwidth and retry limits.
- Bounded route-specific transfer learning, shared worker/buffer resource limits,
  exact-capacity buffer reuse and disk-backed recursive completion manifests.
- Locked local staging ownership journals, identity-based interrupted-replacement
  recovery, SHA-256 verification and verified-handle Windows move deletion.
- FTP MLST/SFTP stat paths, effective native remote checksum verification and
  bounded retry passes with explicit provider capability limits.
- Persistent pane filter/search controls, preview collapse/pinning, conflict
  comparisons and Keep both previews, and highlighted Rename Before/After states.
- Daily bounded security repair and timeout scenario checkpoints retained by CI.

### Changed
- Unified filesystem/compatibility transfer paths and native remote policy handling.
  Same-volume moves retain direct rename; native remote sessions retain one stream.
- Improved directory scanning and scheduling, policy refresh, cancellation/history,
  conflict retries and source/destination protection during copy/move finalization.
- Details remains usable in narrow panes. Refined toolbar defaults/directional
  transfer labels, pane/drop styling, path-bar editing, Git status colors and
  duplicate metadata presentation.
- Updated locked dependencies: crypto-bigint, ctutils, lofty, log, lzma-rust2,
  notify, ogg_pager, suppaftp, tokio, typed-path, ureq, ureq-proto and zip;
  updated target-specific transitive graph. The service directly uses locked sha2.
- Aligned runtime, installer, manifest, lockfile and documentation identities at
  1.0.2. Release preparation now includes the Windows manifest and NSIS fallback;
  publishing requires and uses version-specific release notes.

### Fixed
- Unrelated/stale partial adoption and unsafe cleanup, replacement rollback gaps,
  premature directory publication, read-only source deletion and hidden promoted files.
- Source pathname replacement races, missing identity handling, hashing that
  depended on read boundaries, and serialization of modification dates before 1970.
- Incorrect stripe/scratch memory accounting and oversized pooled allocations.
- Remote downloads ignoring failed/missing final stats, ineffective checksum/retry
  settings, and retries replaying already completed selections.
- Cancellation misreported as success, sibling cancellation masking the real I/O
  failure, structured conflicts bypassing the UI, and work stranded on IPC disconnect.
- Inconsistent flush-on-close behavior and stale policy snapshots for new workers.
- Dependency audit/maintenance permission failures and missing formatting diagnostics.
- UI smoke timeout coupling: retain 10k/100k rendering/navigation checks while
  transfer regressions use the smallest fixture. Explicit 100k copy stress remains
  available with an appropriate timeout; this is not a 100k-copy speed claim.

### Validation
- The integrated application baseline passed Rust and .NET tests, Windows/XAML
  builds, UI smoke checks, MSI/NSIS install and both release/baseline upgrades.
- Release preparation receives its own CI. Live physical NAS/cloud throughput,
  provider durability and signed in-app delivery require their respective environments.

---

## [1.0.1] - 2026-10-20

### Added
- Settings now opens as a standalone, resizable WinUI window with `NavigationView`
  categories, a scrollable workspace, explicit Save/Cancel commands, and normal
  title-bar close behavior.
- Toolbar customization is available from Settings -> Toolbar. Users can choose
  icon-only or labeled buttons, reorder the primary toolbar, insert separators,
  remove commands, and reset the command surface.
- Git integration now has a bottom panel with repository summary, changed-path
  rows, diff, stage, unstage, discard, fetch, pull, push, and commit actions.
  Settings -> Behavior can completely disable the Git UI and backend calls.

### Changed
- The published tag includes MIT licensing consistently across the license text,
  README, npm metadata and Rust crates (previously left under Unreleased).
- Version identity is now **1.0.1**. About, Settings, handshake `appVersion`,
  README badge, artifact names, Cargo package versions, MSBuild `<Version>`,
  assembly identity, installer DisplayVersion, WiX Product Version, and NSIS
  VIProductVersion all use `1.0.1`.
- Toolbar overflow and context menu labels now resolve through the shared app
  command catalog so customized command surfaces keep consistent naming.

### Fixed
- Removed stale merge-conflict markers from the WinUI settings branch.
- Restored WinUI thumbnail preview loading to the byte-backed image pipeline.
- Release builds now report MSI/package lock holders without failing on
  singleton PowerShell pipeline output.

---

## [1.0.0] - 2026-09-02

### Added
- Recycle Bin is a first-class location (Quick Access). Restore, permanent delete, and Empty Recycle Bin work from the pane.
- Properties, clipboard history (`Ctrl+Shift+V`), and operation history are real dialogs instead of text dumps.
- `Ctrl+H` now follows Windows Hidden and System attributes, not just leading-dot names.
- Empty panes distinguish loading, search, filters, access errors, and hidden-only folders.
- Conflict dialog “Apply to all remaining conflicts” is honored for the rest of the transfer.
- Search uses the Show Hidden setting and can include file contents from the search box toggle.
- Path bar suggests folders while typing. Settings → Shortcuts lists the live keybindings.
- `Ctrl+Mouse wheel` changes icon size; `Alt+Enter`, `Ctrl+Enter`, `Ctrl+1–9`, and `Ctrl+B` match the command list.

### Changed
- Version identity is now **1.0.0**. About, Settings, handshake `appVersion`,
  README badge, installer DisplayVersion/branding, artifact names, Cargo
  package versions, MSBuild `<Version>`, assembly identity, WiX Product
  Version, and NSIS VIProductVersion all use `1.0.0`.
- Product branding and runtime identity changed from SimpleFile to SumaFile:
  visible app chrome/About/Settings text, installer display names, shortcuts,
  release artifact names, packaged host executable, and startup log path now
  use SumaFile. Internal namespaces, crate names, environment variables, and
  `com.simplefile.desktop` remain stable for compatibility.
- Settings → Behavior now has “Keep folders on top when sorting” (on by
  default, matching Explorer). Turning it off mixes files and folders in
  one column order.
- Details-view columns now resize like Windows File Explorer: each column,
  including Name, has its own width; drag a header divider to resize the
  column on the left; double-click a divider to size that column to fit
  (Shift+double-click sizes all); right-click a header for Size Column/All
  Columns to Fit; extra-wide columns scroll horizontally with the header.
- Shipping UI is now the WinUI 3 host plus the Rust `simplefile-service` IPC
  process. The Svelte/Tauri renderer, Tauri packaging hooks, and unused Tauri
  glue have been retired. Reusable Rust domain lives in `crates/simplefile-core`.
- Tags, smart folders, git, cleanup, RAR, updater, and terminal commands now
  live in `simplefile-core` and are dispatched by `simplefile-service`. Unknown
  methods return a normal method-not-found error instead of the leftover IPC
  MVP stub.

### Fixed
- Large folder lists append incoming chunks instead of rebuilding the whole collection on every page.
- Closing SumaFile no longer closes files opened in external programs
  (Notepad, image viewers, video players). The service job still dies with
  the UI; shell children break away, matching Explorer.
- Cancelling a copy/move no longer races operation history into "Completed".
  History stays cancelled, partial results can be undone, and success toasts are
  suppressed. The backend returns any completed items with a `cancelled`
  progress status instead of treating cancel as a hard error.
- Archive extraction now re-checks that final output paths stay under the chosen
  destination after join/rename (ZIP/TAR/RAR), and TAR skips symlink/special
  entries so unpack cannot plant out-of-tree links.

---

## [1.1.0] - 2026-05-29

### Added
- Local release validation now includes repeatable Windows smoke tests for the
  release executable, MSI artifact extraction, NSIS installer install/launch/
  uninstall behavior, and startup settings persistence.
- Added startup-location resolution coverage for Home, Last Used, Custom Path,
  blank custom-path fallback, persisted tabs, and stale active-tab fallback.
- Added filesystem command smoke coverage for create, rename, copy, and move
  operations, plus conflict-behavior coverage for copy/move skip, rename,
  replace, and conflict refusal paths.
- Added `src-tauri/tauri.local.conf.json` so local release bundles can be built
  without requiring updater signing secrets.
- Added dedicated 1.1.0 release documentation in
  [`RELEASE_1.1.0.md`](RELEASE_1.1.0.md).

### Changed
- SimpleFile is now proprietary software. The former MIT license text was
  replaced with an all-rights-reserved project license, package metadata now
  declares the app as unlicensed/proprietary, and contributor terms now grant
  conniecombs the rights needed to incorporate contributions into SimpleFile.
- The repository and updater documentation now point to
  `conniecombs/SimpleFile-Windows`.
- The desktop app now ships from the Svelte/Vite entry generated into
  `svelte-frontend/dist`, while the old `frontend/index.html` entry is retired.
- Root release validation now includes combined Svelte, legacy frontend, Rust,
  and Rust dependency-audit checks through `npm run check:release`.
- The Start Location setting now supports selecting and persisting a custom path
  instead of only exposing the option.
- File-list column resizing now behaves like Windows File Explorer, with resize
  handles between visible columns instead of a trailing handle that implied a
  blank extra column.
- Copy, cut, paste, drag/drop, and dual-pane transfers now queue through the
  transfer manager with stable operation IDs so mounted remote-drive transfers
  can report progress without freezing the whole UI.
- Progress copy/move commands now run on a blocking worker and emit chunk-level
  byte progress while preserving network-mount buffering and retries.
- Version metadata now targets `1.1.0` in `src-tauri/Cargo.toml`,
  `src-tauri/Cargo.lock`, `src-tauri/tauri.conf.json`, and the browser mock
  About response.
- README badges now include the Release workflow and Security Policy links.

### Fixed
- Copy and move commands now explicitly refuse destination conflicts in their
  non-resolved public command paths so existing files are not silently
  overwritten.
- Custom Path startup falls back safely when the stored custom path is blank.
- Last Used startup recovers when persisted tab state points at a missing active
  tab.

---

## [SimpleFile 1.0.3] - 2026-05-26

### Added
- Recursive Advanced Rename targeting for files inside selected folders, with
  filters, template tokens, regex remove/replace, whitespace cleanup,
  separator conversion, extension transforms, sanitization, and duplicate or
  invalid-name preview warnings.
- Browser mock coverage for nested rename targets and a Rust regression test
  for batch renames across nested directories.
- Header-only column menu with visible-column toggles, resizable column widths,
  and an optional folder item-count column.
- Folder size and folder item counts now load for visible folders and cache
  results so list refreshes stay responsive.
- Status bar drive-space meter for the active location.

### Changed
- `formatSize()` now treats missing or invalid byte counts as unavailable
  instead of throwing during list refresh.
- Version metadata now targets `1.0.3` in `src-tauri/Cargo.toml`,
  `src-tauri/Cargo.lock`, `src-tauri/tauri.conf.json`, and the browser mock
  About response.

---

## [SimpleFile 1.0.2] - 2026-05-26

### Added
- **Compare Files** workflow for two selected UTF-8 text files, including a Rust
  `compare_files` command, frontend API wrapper, context-menu action, `Ctrl+=`
  shortcut, and side-by-side added/removed/modified row display.
- Unit coverage for the line-diff pairing behavior used by file comparison.

### Changed
- Version metadata now targets `1.0.2` in `src-tauri/Cargo.toml`,
  `src-tauri/Cargo.lock`, and `src-tauri/tauri.conf.json`.
- README now better reflects SimpleFile's distinctive feature set, including
  advanced rename, clipboard history, file comparison, metadata inspection,
  cloud-to-cloud transfers, rclone/WinFsp drive mounts, safety guardrails, and
  updater behavior.
- The browser mock page now includes representative files and a mocked
  comparison response so the compare dialog can be exercised outside Tauri.

---

## [SimpleFile 1.0.1] - 2026-05-25

### Added
- GitHub-hosted Tauri updater configuration with a signed updater public key,
  `createUpdaterArtifacts: true`, and a GitHub Releases `latest.json` endpoint.
- `UPDATER_RELEASE.md` with the updater signing, GitHub secret, release, and
  validation checklist.
- `frontend/scripts/check-updater-config.mjs` and `npm run check:updater` to
  keep updater config and release workflow signing settings from regressing.

### Changed
- Release workflow now requires `TAURI_SIGNING_PRIVATE_KEY`, passes updater
  signing environment variables into Tauri builds, uploads updater signatures,
  uploads `latest.json`, and prefers the NSIS updater asset on Windows.
- README and release documentation now describe the active updater channel
  instead of the former disabled placeholder state.
- Version metadata now targets `1.0.1` in `src-tauri/Cargo.toml`,
  `src-tauri/Cargo.lock`, `src-tauri/tauri.conf.json`, and release-facing
  README status text.

---

## [SimpleFile 1.0.0] - 2026-05-24

### Added
- Committed `src-tauri/Cargo.lock` so CI, local builds, and release builds use the same dependency graph.
- `frontend/scripts/check-js-syntax.mjs` and `npm run check:js` for local and CI JavaScript module syntax checks.
- `frontend/scripts/check-tauri-invokes.mjs` and `npm run check:invokes` for verifying frontend
  Tauri IPC wrappers against backend command registration.
- Startup panic logging to `%LOCALAPPDATA%\SimpleFile\startup.log` on Windows, with app-data/home fallbacks on other platforms.
- **About SimpleFile dialog** in the More Actions dropdown, with live backend metadata, major
  feature details, project links, and explicit rclone/WinFsp credits.
- **Disk Cleanup workflow** with backend progress events, cancellation, large-file results,
  SHA-256 duplicate grouping, and a frontend results modal.
- **WinFsp installer/status support** in Settings -> Cloud Tools. The UI exposes a separate
  **Install WinFsp Driver** button and explains that WinFsp is a Windows filesystem driver/runtime
  used by rclone mounts.
- **Windows rclone drive-letter mounts**. Cloud mount configs now store `mount_point`; Windows
  mounts use available high drive letters such as `Z:\` instead of app-data mount folders.
- `CLOUD_DRIVES.md` documenting rclone, WinFsp, Windows drive-letter mounts, install behavior,
  freeze/WinFsp safeguards, troubleshooting, security notes, and credits.
- **Plugin-based cloud drive system** — replaced hardcoded per-provider code with a
  trait-based plugin registry. Each cloud provider is now fully self-contained.
  - `src-tauri/src/cloud/mod.rs` — `CloudPlugin` trait, `all_plugins()` / `find_plugin()`
    registry, and the new `cloud_list_plugins` Tauri command that exposes provider metadata
    to the frontend.
  - `src-tauri/src/cloud/gdrive_plugin.rs` — Google Drive `CloudPlugin` implementation
    (metadata for rclone-backed Google Drive sign-in and mount restore).
  - `src-tauri/src/cloud/pcloud_plugin.rs` — pCloud `CloudPlugin` implementation
    (metadata for rclone-backed pCloud sign-in and mount restore).
  - `src-tauri/src/cloud/onedrive_plugin.rs` — OneDrive `CloudPlugin` implementation
    (metadata for rclone-backed Microsoft sign-in and mount restore).
  - `src-tauri/src/rclone.rs` — Generic rclone command adapter for remote creation, listing,
    upload/download, folder creation, rename/delete, mounting, and restore.
  - `src-tauri/src/models.rs` — New `CloudPluginMeta`, `AuthField`, and `SelectOption`
    types so the backend can describe any provider's connect form to the frontend.
  - `svelte-frontend/src/lib/legacyCloudPluginRegistry.ts` — Frontend registry that
    discovers Svelte-side plugin modules and merges them with backend metadata from
    `cloud_list_plugins` at startup.
  - `svelte-frontend/src/lib/legacy-cloud-plugins/gdrive.ts` — Self-contained Google
    Drive frontend plugin: SVG icon, rclone-backed Sign in with Google dialog, and
    cloud operation wrappers.
  - `svelte-frontend/src/lib/legacy-cloud-plugins/pcloud.ts` — Self-contained pCloud
    frontend plugin: rclone-backed pCloud sign-in with US/EU region selection and cloud
    operation wrappers.
  - `svelte-frontend/src/lib/legacy-cloud-plugins/onedrive.ts` — Self-contained
    OneDrive frontend plugin: rclone-backed Microsoft sign-in dialog and cloud
    operation wrappers.
  - `svelte-frontend/src/lib/legacyRclonePluginCommon.ts` — Shared frontend helpers
    for rclone-backed providers.
  - `svelte-frontend/src/lib/cloudTransferWorkflow.ts` — Shared cloud-to-cloud transfer picker and
    Transfer Manager queue integration for rclone remotes.
  - `svelte-frontend/src/lib/legacyCloudManager.ts` — Generic cloud UI with zero provider-specific
    code: provider picker dialog, file browser (list / navigate / rename / delete /
    upload / create folder), auth dialog mounting, and session management with automatic
    OAuth2 token refresh.
  - `frontend/js/api.js` — New `cloudListPlugins()` wrapper for the backend command.
- `ROADMAP.md` — phased development plan from v0.3 to v1.0
- `CONTRIBUTING.md` — contributor guide covering setup, style, and PR process
- `CHANGELOG.md` — this file
- `CODE_OF_CONDUCT.md` — Contributor Covenant v2.1
- `SECURITY.md` — vulnerability reporting policy
- `SUPPORT.md` — support channels and guidance
- `LICENSE` — standalone project license file at repository root
- GitHub issue templates for bug reports and feature requests

### Changed
- Version metadata is aligned at `1.0.0` in `src-tauri/Cargo.toml`,
  `src-tauri/Cargo.lock`, `src-tauri/tauri.conf.json`, the README badge, and
  release documentation.
- CI now uses the committed lockfile instead of generating a fresh lockfile inside each job.
- CI backend build now uses `--locked --all-features` for release-mode build verification.
- CI now runs the frontend/backend invoke consistency check and covers both macOS Intel and
  Apple Silicon backend targets.
- Release builds are now gated by Rust formatting, Clippy, tests, frontend syntax checks,
  frontend/backend invoke checks, and `cargo audit` before installer assets are uploaded.
- Release workflow now uses the resolvable `tauri-apps/tauri-action@action-v0.6.2`
  release tag instead of the non-existent `v1` tag.
- Updater artifact generation is disabled until a signed production updater channel has a real public key and endpoint.
- Rust dependency audit remains a hard failure, with explicit accepted RustSec advisory IDs for current Tauri/Linux GTK transitive dependencies.
- `reqwest` 0.13 feature flags now use the current `rustls`, `form`, and `query` features.
- Tauri bundle identifier changed from `com.simplefile.app` to `com.simplefile.desktop` to avoid macOS `.app` bundle-extension conflicts.
- Google Drive, OneDrive, Dropbox, pCloud, and S3-compatible storage now use rclone as the shared cloud
  backbone for normal sign-in/configuration, browsing, transfers, and mounts.
- Google Drive, OneDrive, and pCloud no longer require app-owned OAuth client IDs or password forms
  for normal users; rclone
  opens the provider browser sign-in and stores provider tokens in the local rclone config.
- Mounted rclone cloud paths on Windows are treated as cloud/network paths rather than normal local
  folders for expensive background behavior. SimpleFile avoids watchers, automatic previews,
  automatic thumbnails, folder-size scans, recursive properties, and Windows volume/free-space
  probing for known cloud drive-letter mounts.
- Known rclone mount folders are listed through `rclone lsjson` and mapped back to local
  drive-letter child paths instead of being listed through WinFsp with `std::fs::read_dir`.
- Cloud-to-cloud copy and move flows now transfer selected files or folders between configured
  rclone remotes from the provider panels and generic cloud browser.
- `src-tauri/src/mounts.rs` — `restore_mounts` and `unmount_drive` now dispatch cloud
  provider operations through the plugin registry (`find_plugin`) instead of a hardcoded
  `match` on provider name strings. Adding a new cloud provider no longer requires editing
  `mounts.rs`.
- Terminal launching no longer depends on `tauri-plugin-shell`; platform terminal commands are
  launched from scoped Rust backend commands with separated arguments.
- `Open With...` now launches only trusted executable targets and blocks shells/scripting runtimes
  such as `cmd`, `powershell`, `bash`, `python`, `node`, and similar interpreters.
- `README.md` — Updated feature list, comparison table, and project structure tree to
  reflect the cloud plugin system and OneDrive support.

### Fixed
- Cargo dependency resolution failure caused by the old `reqwest` `rustls-tls` feature name.
- Rust Clippy failures under `-D warnings`.
- Segmented FTP, Google Drive, pCloud, and OneDrive downloads now clamp worker counts so tiny files cannot produce zero-length chunks or underflowed byte ranges.
- Disk cleanup duplicate hashing now hex-encodes SHA-256 digests without relying on removed digest formatting behavior.
- Startup no longer panics before the main window opens when the updater plugin is present but production updater endpoints are not configured.
- Google Drive and OneDrive authorization now open the system browser from backend commands, fixing
  auth flows where the frontend opener API was unavailable.
- Missing Google sign-in build configuration is now reported as a broken build/configured-release
  issue instead of telling normal users to set developer environment variables.
- rclone-backed sign-in now serializes OAuth setup and clears stale SimpleFile rclone auth
  listeners on Windows before starting a new sign-in, preventing Google Drive, OneDrive, and
  pCloud from failing when an abandoned rclone process is still holding the local callback port.
- Remote Drives actions keep a selected row after cloud refreshes, nested prompts now appear above
  the Remote Drives window, and Windows rclone mounts no longer pre-create the mount folder that
  rclone expects to own.
- Browsing mounted rclone/WinFsp cloud drives on Windows no longer fails with
  "Failed to resolve path" / OS error 1005; file listing and file actions now preserve the
  mounted path instead of canonicalizing through the provider junction.
- Accessing known mounted rclone/WinFsp cloud drives no longer triggers the background filesystem
  probes that could hard-freeze the app when the rclone process or WinFsp mount became wedged.
- rclone mount liveness checks on Windows avoid `read_dir` probing of drive roots when the saved
  mount process cannot be found.
- Windows drive listing skips `GetVolumeInformationW` and `GetDiskFreeSpaceExW` probes for saved
  cloud drive letters, avoiding another WinFsp blocking path.
- PowerShell-as-administrator launch now uses an encoded PowerShell command with a literal path
  rather than interpolating untrusted path text into shell syntax.
- Disk cleanup scans can be cancelled through the progress dialog and no longer rely on blocking
  alert output for results.

### Removed
- Miller columns (column view) toolbar button and feature — the sidebar directory tree
  provides equivalent hierarchical navigation with less UI clutter
- `tauri-plugin-shell` dependency and broad shell plugin exposure; terminal and Open With flows use
  scoped backend process-launch commands instead.

---

## [0.2.1] — 2026-04-30

### Added
- `UI_BACKEND_REVIEW.md` with the v0.2.1 GUI, UI usability, Rust backend wiring,
  reliability, and CI/CD review.
- Frontend JavaScript module syntax sanity checks in CI.
- Pull-request dependency review in CI.
- Release workflow version validation that checks release tags/manual versions against
  both `src-tauri/Cargo.toml` and `src-tauri/tauri.conf.json`.

### Changed
- `src-tauri/Cargo.toml` and `src-tauri/tauri.conf.json` bumped from `0.2.0` to `0.2.1`.
- CI now runs on pushes and pull requests targeting the actual default branch,
  `prototype`, instead of `main`, `master`, and `develop`.
- CI is split into explicit quality, frontend sanity, security, and platform build jobs.
- Release workflow supports draft/manual releases, validates version consistency, and
  builds Windows x64, macOS x64, macOS ARM64, and Linux x64 Tauri artifacts.
- README updated with the v0.2.1 badge, correct CI branch, review-status section,
  current CI/CD behavior, and release version rules.
- `.github/RELEASE.md` updated to use `prototype` and the current release workflow.

### Fixed
- CI badge branch mismatch in README.
- Release documentation that still referenced pushing tags to `main` and a stale
  updater workflow.

---

## [0.2.0] — 2025-01-01

### Added
- Google Drive and pCloud cloud storage mounting with multi-threaded transfers
- Intelligent network drive detection and per-drive optimization
- File checksum calculation (MD5, SHA-1, SHA-256) via `checksum.rs`
- `BUGS.md` — comprehensive 20-bug analysis with fix status
- `FEATURE_OPPORTUNITIES.md` — 20 prioritized feature proposals
- `CODE_ANALYSIS.md` — initial architecture review (23 issues, most now fixed)
- Cross-platform CI/CD pipeline (`ci.yml`, `release.yml`)
- `dependabot.yml` for automated dependency updates

### Fixed
- B01: `get_entry_info` now uses `validate_existing_path()` (path traversal)
- B02: `copy_entry` / `move_entry` now validate destination paths
- B03: `move_to_trash` now canonicalizes paths before deletion
- B04/B05: Shell metacharacter injection via `validate_shell_path()` on Windows
- B06: `move_entry` falls back to copy+delete on cross-device moves (`EXDEV`)
- B07: `getParentPath` preserves trailing backslash for Windows drive roots (`C:\`)
- B08: Drag-and-drop JSON parse wrapped in try-catch; drag state always cleaned up
- B09: Progress dialog now hidden in all `paste()` / `copyTo()` / `moveTo()` error paths
- B10: `_savedEntries` added to `initialState` so `resetState()` clears it
- B11: Tab and bookmark IDs use a monotonic counter instead of `Date.now()` (collision fix)
- B13: Array/Set mutations replaced with assignment patterns to trigger reactive proxy
- B14: `count_items` and `calculate_size_recursive` converted to iterative stacks (stack overflow fix)
- B15: `read_file_preview` reads only first 8 KB for binary detection (was reading up to 1 MB)
- B17: Redundant thumbnail dimension pre-calculation removed; `image::thumbnail()` handles it
- B18: Linux drive listing now checks `/run/media/$USER` for modern udisks2 mounts
- B19: File watcher changed from global 2 s debounce to per-path 500 ms debounce
- B20: `createElement` utility no longer exposes raw `innerHTML` assignment
- TOCTOU race in `create_file` fixed with `OpenOptions::create_new()` (atomic)
- Folder size calculation made cancellable to prevent UI freezes
- Folder size calculated on highlight (on-demand) instead of eagerly on navigation
- `reqwest` upgraded from 0.12 to 0.13; CI/CD workflow updated accordingly

---

## [0.1.0] — 2024-06-01

### Added
- Initial release
- File browsing with list and grid view modes
- Core file operations: create, rename, delete (to trash), copy, cut, paste
- Multiple tab support
- Folder tree sidebar and breadcrumb navigation
- Archive support: ZIP, TAR, TAR.GZ, TAR.BZ2, RAR
- Recursive file search
- Git status integration
- File preview (text and image thumbnails)
- File system watcher (auto-refresh)
- Terminal integration (platform-specific)
- FTP browsing
- Internal drag-and-drop
- Dark and light theming
- Settings persistence via localStorage
- Keyboard shortcuts
- Internationalization scaffolding (i18n.js)

---

[Unreleased]: https://github.com/conniecombs/SumaFile/compare/v1.0.3...HEAD
[1.0.3]: https://github.com/conniecombs/SumaFile/compare/v1.0.2...v1.0.3
[1.0.2]: https://github.com/conniecombs/SumaFile/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/conniecombs/SumaFile/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/conniecombs/SumaFile/releases/tag/v1.0.0
[1.1.0]: https://github.com/conniecombs/SimpleFile-Windows/compare/v1.0.3...v1.1.0
[SimpleFile 1.0.3]: https://github.com/conniecombs/SimpleFile-Windows/compare/v1.0.2...v1.0.3
[SimpleFile 1.0.2]: https://github.com/conniecombs/SimpleFile-Windows/compare/v1.0.1...v1.0.2
[SimpleFile 1.0.1]: https://github.com/conniecombs/SimpleFile-Windows/compare/v1.0.0...v1.0.1
[SimpleFile 1.0.0]: https://github.com/conniecombs/SimpleFile-Windows/compare/v0.2.1...v1.0.0
[0.2.1]: https://github.com/conniecombs/SimpleFile-Windows/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/conniecombs/SimpleFile-Windows/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/conniecombs/SimpleFile-Windows/releases/tag/v0.1.0
