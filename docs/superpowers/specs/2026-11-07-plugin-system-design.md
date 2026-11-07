# SumaFile plugin system

**Status:** approved design, awaiting implementation plan
**Date:** 2026-11-07
**Scope:** a third-party plugin system for SumaFile, and the migration of Git,
FTP/SFTP and 7-Zip support out of the core product into three first-party
plugins that ship with the installer.

## 1. Goals

- A plugin is a single folder. Copying the folder into a plugin directory
  installs it. Deleting the folder removes it. No rebuild of SumaFile.
- A plugin bundles everything it needs: UI contributions, backend code, assets
  and settings. The host provides slots; it never carries feature-specific code
  for a plugin.
- Third parties can build plugins against a published SDK with a versioned
  contract.
- Git, FTP/SFTP and 7-Zip become plugins. A default install behaves exactly as
  1.0.4 does because those three plugins ship inside the installer.
- Startup stays inside the existing CI budget. Plugin discovery reads manifests
  only; code loads on first use.
- The Rust service stays plugin-agnostic except for one archive-engine
  registration hook.

## 2. Non-goals

- Sandboxing. Plugin code runs with the user's privileges in the UI process and
  in child processes. The trust model is informational, not enforcing.
- A plugin marketplace, automatic plugin updates or remote installation.
- Hot reload. Adding, removing or updating a plugin takes effect on the next
  start or on an explicit rescan from Settings.
- Plugin-to-plugin communication.
- A general host secret store API. The FTP/SFTP plugin carries its own
  Credential Manager code. Revisit when a second plugin needs secrets.
- Replacing the built-in zip, tar, tar.gz and rar engines. Only 7-Zip moves.

## 3. Current state the design builds on

- Two processes, two languages. `SumaFile.exe` is an unpackaged WinUI 3 app in
  C#. `simplefile-service.exe` is a Rust named-pipe JSON-RPC service. The UI
  owns the service through a job object with kill-on-close and silent
  breakaway.
- `ipc/schema/v1` is a closed list of 97 domain methods. `check-ipc-schema.mjs`
  asserts the exact count and that generated C# and Rust bindings match.
- `NamedPipeJsonClient` already exposes a generic `InvokeAsync(method, args)`.
  The frame codec is length-prefixed JSON and carries requests, results and
  events.
- Settings persist in a SQLite table through `get_db_setting`,
  `get_db_settings` and `set_db_setting`. Settings search indexes every
  labelled control in the Settings window.
- The updater verifies ed25519 signatures with `ed25519-dalek`.
- 7-Zip is already out of process. `archive/seven_zip.rs` shells out to a
  bundled `7za.exe` under `tools\7zip`. Capabilities report it as a separate
  engine beside the built-in formats.
- Remote support spans 13 IPC methods, `simplefile-core/src/remote`,
  `simplefile-service/src/remote` including a 2,300-line transfer module, the
  Remote Manager window, a sidebar section, a Settings button and a palette
  command.
- Git support spans 11 IPC methods, a 1,000-line Rust module, `MainWindow.Git.cs`,
  the inline Git panel in `MainWindow.xaml`, a toolbar toggle, hard-coded
  entries in `ContextMenuBuilder` and `AppCommandCatalog`, per-row status
  colouring in `FileRowView`, and the `EnableGitIntegration` setting.

## 4. Package format

### 4.1 Locations

The host scans two directories at startup, in this order:

1. `<install dir>\plugins\<plugin-id>\` for plugins shipped with the installer.
2. `%LOCALAPPDATA%\SumaFile\plugins\<plugin-id>\` for user-installed plugins.

A plugin id found in both places resolves to the user copy, and Settings shows
that the installed copy is shadowed.

A `.sfplugin` file is the plugin folder zipped with `plugin.json` at the root.
Settings > Plugins > Install from file unzips it into the user directory after
the consent dialog in section 9.

### 4.2 Folder contents

```text
plugins\<plugin-id>\
  plugin.json          manifest, required
  <ui assembly>.dll    C# UI half, optional
  <backend>.exe        backend process, optional, any language
  signature.json       publisher signature, optional
  ...                  assets, licenses, anything the plugin needs
```

### 4.3 Manifest

`plugin.json` is UTF-8 JSON with these top-level fields.

| Field | Required | Meaning |
| --- | --- | --- |
| `id` | yes | Reverse-DNS style identifier, lowercase, `[a-z0-9.-]`. Folder name must match. |
| `name` | yes | Display name. |
| `version` | yes | Semantic version of the plugin. |
| `publisher` | yes | `{ "name", "url", "publicKey" }`. `publicKey` is optional base64 ed25519. |
| `sdkVersion` | yes | Semantic version of the SDK the plugin was built against. The host loads a plugin only when the major matches a major it supports. |
| `ui` | no | `{ "assembly": "<file>.dll", "entryType": "<namespace>.<type>" }`. The entry type implements `IPlugin`. |
| `backend` | no | `{ "executable": "<file>.exe", "args": [], "activateOn": "first-call" \| "startup", "acceptsMultipleClients": false }`. |
| `contributions` | yes | Declarative contributions, section 4.4. May be empty. |
| `settings` | no | Declarative settings schema, section 8. |
| `permissions` | no | Informational list shown in the consent dialog: `network`, `credentials`, `shell`, `backend-process`. |

### 4.4 Contributions

Everything in `contributions` is rendered from the manifest before any plugin
code loads. Each contribution names the command or provider the host invokes
when the user acts on it, and that first invocation loads the UI assembly or
starts the backend.

| Contribution | Shape | Host slot |
| --- | --- | --- |
| `commands` | `{ id, label, group, shortcut? }` | Command palette, shortcut map, command routing. Ids are prefixed with the plugin id by the host. |
| `contextMenu` | `{ group, items: [{ command, label, when }] }` | `ContextMenuBuilder` appends plugin groups after the built-in ones. `when` is a small expression over selection facts: `selectionCount`, `singleFile`, `singleFolder`, `hasExtension(ext)`, `hasDecoration` (the selection carries this plugin's row decoration) and `folderFlag(name)` (a per-folder flag the plugin's row decoration provider sets, such as `inRepository`). |
| `toolbar` | `{ command, label, glyph, placement: "primary" \| "overflow" }` | Primary toolbar and overflow planner. |
| `sidebar` | `{ id, title, provider }` | A collapsible section in `SidebarView`. The provider implements `ISidebarSectionProvider`. |
| `panels` | `{ id, title, provider, placement: "right" \| "bottom", detachable }` | A dockable side panel next to the file list, with the same host-or-window toggle the Git workbench has today. |
| `rowDecorations` | `{ provider }` | Per-row text and brush key plus per-folder flags supplied by an `IRowDecorationProvider`, batched per folder. |
| `archiveFormats` | `{ extension, canList, canExtract, canCreate, canModify, engineName }` | Registered with the Rust service as an archive engine served by the plugin backend, section 6.4. |
| `settingsPage` | `{ provider }` | A custom settings control for plugins whose settings exceed the declarative schema. |

## 5. UI half

### 5.1 SDK

A new project `src-winui/SimpleFile.PluginSdk` holds only contracts and is
published as a NuGet package named `SumaFile.PluginSdk`. It references the
Windows App SDK so plugins can build WinUI controls. The public surface:

- `IPlugin`: `Task ActivateAsync(IPluginHost host, CancellationToken ct)` and
  `Task DeactivateAsync()`.
- `IPluginHost`: `InvokeBackendAsync<T>(method, args, ct)`, `Settings`,
  `Progress`, `Dialogs`, `Workspace`, `Logger`, and `Events` for backend
  events.
- `IPluginSettings`: typed get and set over the plugin's own keys.
- `IPluginProgress`: `Begin(title)` returning an operation handle with
  `Report(current, total, detail)`, `Complete()`, `Fail(message)` and a
  `CancellationToken` the host cancels from the Transfers window.
- `IPluginDialogs`: show a content dialog, pick a folder, confirm.
- `IPluginWorkspace`: read-only view of the active pane path, selection,
  provider flags, and a `Changed` event. Navigation requests go through
  `NavigateAsync(path)`.
- `ICommandHandler`, `ISidebarSectionProvider`, `IPanelProvider`,
  `IRowDecorationProvider`, `ISettingsPageProvider`.
- `RowDecoration`: `{ Text, BrushKey, Tooltip }`. Brush keys resolve through
  the host theme so plugins honour light and dark themes.

The SDK follows semantic versioning. The host declares the SDK majors it
supports, initially one. A breaking change to any contract bumps the major.

### 5.2 Loading

Each plugin assembly loads in its own non-collectible `AssemblyLoadContext`.
The context resolves `SimpleFile.PluginSdk`, `Microsoft.WindowsAppSDK`,
`CommunityToolkit.Mvvm` and the .NET runtime from the host so types unify. Other
dependencies resolve from the plugin folder.

Loading happens on first use: the first command invocation, the first time a
contributed sidebar section, panel or row decoration becomes visible, or the
first time the settings page opens. Manifest reading and contribution rendering
happen at startup and must stay under 50 ms for ten plugins in the startup trace.

### 5.3 XAML

Guaranteed in phase 1: UI built in C# code, and XAML loaded at runtime through
`XamlReader.Load` from strings embedded in the plugin assembly.

Spike in phase 1: compiled XAML from a WinUI class library, which ships its own
XBF files and resource index and registers its generated
`IXamlMetadataProvider` through the `OtherProviders` list the host's generated
provider already has. The spike moves the Git workbench view into a separate
assembly and loads it from the plugins folder. If the spike passes, compiled
XAML becomes supported and the Git plugin keeps its XAML. If it fails, the Git
plugin converts its three XAML files to code and compiled XAML stays
unsupported in SDK 1.

## 6. Backend half

### 6.1 Ownership

The host spawns a plugin backend on first call, or at startup when
`activateOn` is `startup`. The host assigns each backend to its own job object
with kill-on-close. This is necessary because the existing UI job uses silent
breakaway, so a backend spawned as a child of the service would outlive the
app.

The host passes the pipe name and the plugin's data directory
(`%LOCALAPPDATA%\SumaFile\plugins-data\<plugin-id>\`) as command-line
arguments. The backend creates the pipe server; the host connects within a
five-second timeout and performs a handshake that exchanges plugin id, plugin
version and SDK version.

The backend exits when its last client disconnects. The host also terminates it
on shutdown, on plugin disable, and after three consecutive failed restarts.

### 6.2 Transport

The pipe speaks the same length-prefixed JSON-RPC framing as the service, so
the C# `FrameCodec` and `NamedPipeJsonClient` are reused and a Rust backend can
use `simplefile-ipc` directly. The protocol adds one thing the current pipe
does not have: requests that flow from backend to host.

- Host to backend: plugin methods, named without any prefix. The plugin owns
  its method namespace.
- Backend to host: events, as today, and `host.*` requests.
- The host adds an inbound request dispatcher to the client. Unknown `host.*`
  methods return the existing method-not-found error.

Cancellation keeps the existing convention. The host sends `cancel` with the
operation id, and the backend is responsible for honouring it.

### 6.3 Host methods available to backends

| Method | Purpose |
| --- | --- |
| `host.get_settings` / `host.set_setting` | The plugin's own keys, section 8. |
| `host.progress_begin` / `host.progress_report` / `host.progress_complete` / `host.progress_fail` | Appears in the Transfers window with cancel. |
| `host.log` | Written to the SumaFile log with the plugin id. |
| `host.notify` | A status-center message. |

### 6.4 Archive engines: the one service hook

Browsing an archive inside a pane and dropping files into an archive run inside
the service's archive module, so the service must be able to call the 7-Zip
plugin.

- When a plugin declares `archiveFormats`, the host starts its backend at
  startup and calls a new service method `register_archive_engine` with the
  plugin id, pipe name and format table.
- The service dials the plugin pipe as a second client. Plugins that declare
  `archiveFormats` must set `acceptsMultipleClients` to `true`.
- `archive/mod.rs` gains an `ArchiveEngine` trait with `list`, `extract`,
  `create`, and the mutate operations, keyed by extension. The built-in
  formats implement it in-process. `PluginArchiveEngine` forwards over the
  pipe using methods `archive.list`, `archive.extract`, `archive.create`,
  `archive.rename`, `archive.delete`, `archive.add`, `archive.read_entry`.
- `get_archive_capabilities` merges the built-in table with registered engines.
- If the plugin is absent or faulted, `.7z` reports `can_* = false` with the
  note "7-Zip support requires the SumaFile 7-Zip plugin."

`register_archive_engine` and a matching `unregister_archive_engine` are the
only additions to the fixed method list.

### 6.5 Rust helper crate

`crates/sumafile-plugin` wraps the pipe server, framing, handshake, host client
and progress helpers for Rust backends. It is published to crates.io with the
SDK. Backends in other languages implement the framing directly; the spec in
`ipc/schema/plugin/v1` documents it.

## 7. Host slots added to the WinUI app

- `ContextMenuBuilder.Build` takes an `IReadOnlyList<PluginMenuContribution>`
  and evaluates `when` expressions against the request. The hard-coded
  `GitMenu` is deleted when Git moves.
- `AppCommandCatalog.All` becomes `AppCommandCatalog.BuiltIn` plus a
  `PluginCommands` list merged at startup.
- `MainWindow.CommandRouting` routes any command id with a plugin prefix to the
  plugin manager, which loads the plugin and calls its `ICommandHandler`.
- `PrimaryToolbarView` and `ToolbarOverflowPlanner` accept plugin buttons.
- `SidebarView` renders plugin sections below the built-in sections.
- `MainWindow.xaml` replaces the inline `GitPanel` with a generic `PluginPanelHost`
  that supports one visible panel at a time, host-or-window toggle, and the
  same resize grip. `GitWorkbenchWindow` becomes a generic `PluginPanelWindow`.
- `FileRowView` reads `Row.DecorationText` and `Row.DecorationBrushKey`
  instead of `Row.GitText`. `ExplorerWorkspace` asks every registered
  `IRowDecorationProvider` for decorations once per folder sync, off the UI
  thread, with a 250 ms budget after which the folder renders undecorated and
  updates when results arrive.
- Settings gains a Plugins category, section 8.
- `StatusCenter` shows plugin faults with a Restart action.

## 8. Settings

### 8.1 Declarative settings

The manifest `settings` array declares entries of
`{ key, type, label, description, default, group }` with `type` in
`boolean`, `string`, `integer`, `enum` (with `options`), `path`.

The host renders each plugin as a section under Settings > Plugins with the
plugin name, version, publisher, an enable toggle, and the declared controls.
Settings search indexes them like any other control. Values persist in the
existing settings table as `plugin.<plugin-id>.<key>` through the existing
`set_db_setting`, so no storage changes are needed. The plugin reads them
through `IPluginSettings` in C# or `host.get_settings` from a backend.

### 8.2 Custom settings page

A plugin with a `settingsPage` contribution supplies an `ISettingsPageProvider`
that returns a `FrameworkElement`. The host renders it below the declarative
controls. Search indexes its labelled controls once it has been created.

### 8.3 Plugins category

Settings > Plugins lists every discovered plugin with state: Enabled,
Disabled, Incompatible SDK, Load failed, Faulted, Shadowed. It offers Install
from file, Rescan, Open plugins folder, and per-plugin Enable, Disable and
Remove. Remove deletes a user-directory plugin folder after confirmation and
never touches the install directory.

## 9. Trust model

There is no sandbox. The design is explicit with users about what runs.

- **First-load consent.** The first time a plugin is discovered, the host
  shows a dialog with name, publisher, version, the permissions list, whether
  it runs a backend process, and the signature state. The user chooses Enable
  or Keep disabled. Plugins stay disabled until enabled. Plugins in the
  install directory that shipped with the installer are pre-approved by the
  installer writing their manifest hashes to the approval store.
- **Signature.** `signature.json` holds an ed25519 signature over
  `plugin.json` plus the SHA-256 of every file listed in a `files` array. The
  host verifies it with the publisher's `publicKey` using the updater's
  verifier and shows "Verified publisher" with the key fingerprint. Unsigned
  or mismatched plugins show a warning but can still be enabled.
- **Change detection.** The approval store records the manifest hash and
  signature state. When either changes, the plugin returns to the consent
  dialog.
- **Compatibility.** A `sdkVersion` major the host does not support lists the
  plugin as Incompatible and never loads it.

## 10. Failure handling

| Failure | Behaviour |
| --- | --- |
| Manifest invalid | Listed in Settings with the parse error. Nothing loads. |
| UI assembly fails to load or `ActivateAsync` throws | Listed as Load failed with the exception message. Contributions are removed. Rest of the app unaffected. |
| Backend fails to start or handshake | The invoking command fails with a status-center message. The plugin is marked Faulted after three failures within a minute. |
| Backend exits unexpectedly | In-flight calls fail with a plugin-faulted error. Status center offers Restart. Archive engines are unregistered from the service until restart. |
| Backend call times out | Same timeouts as service calls. |
| Row decoration provider is slow | Folder renders undecorated after 250 ms; decorations apply when they arrive. |
| Plugin faulted while a 7-Zip archive is open in a pane | The pane shows the existing archive-unavailable state. |

## 11. Migration of the three features

### 11.1 7-Zip plugin, `com.sumafile.sevenzip`

- Folder: `plugin.json`, `sumafile-sevenzip-backend.exe` (Rust, from
  `archive/seven_zip.rs`), `7za.exe`, `7za.dll`, `7zxa.dll`, `License.txt`,
  `SUMAFILE_BUNDLE.md`.
- Contributions: `archiveFormats` for `.7z` with all four capabilities.
- Settings: `binaryOverride` (path) replacing the `SIMPLEFILE_7Z` environment
  variable for development.
- No UI assembly.
- Core removes `seven_zip.rs` and the `tools\7zip` payload. The capability
  table drops its 7-Zip row and merges registered engines.
- `check-winui-packaging.mjs` and `build-winui-release.ps1` move their 7-Zip
  checks to the plugin folder.
- `tools/sevenz-evaluation` keeps working against the plugin's bundled binary.

### 11.2 FTP/SFTP plugin, `com.sumafile.remote`

- Folder: `plugin.json`, `SumaFile.Remote.dll`, `sumafile-remote-backend.exe`.
- Backend: `simplefile-core/src/remote`, `simplefile-service/src/remote`
  including `transfer.rs`, `secrets.rs` and both providers move as they are.
  Transfer progress is reported through `host.progress_*` so remote transfers
  keep appearing in the Transfers window with cancel.
- UI assembly: `RemoteManagerWindow`, `RemoteManagerViewModel`, the sidebar
  profiles section as an `ISidebarSectionProvider`, the remote parts of
  `TransferOptionsDialog`, and the `ftp-sftp-manager` command.
- Contributions: one command, one sidebar section, one Settings button via a
  custom settings page.
- Core removes 13 remote IPC methods, the `RemoteProviderFactory` trait, the
  remote session registry and the remote secret store. `domainMethodCount`
  drops to 86 after the two archive-engine methods are added.
- Without the plugin, SumaFile shows no remote UI and the sidebar has no
  Remote profiles section.

### 11.3 Git plugin, `com.sumafile.git`

- Folder: `plugin.json`, `SumaFile.Git.dll`, `sumafile-git-backend.exe`.
- Backend: `simplefile-core/src/git.rs` as its own process. Its status cache
  moves with it.
- UI assembly: `GitWorkbenchView`, `GitWorkbenchViewModel`, the detachable
  window, and the logic in `MainWindow.Git.cs`.
- Contributions: ten commands, one context-menu group with `when`
  expressions, one toolbar toggle, one panel, one row decoration provider with
  `activateOn: startup`.
- Settings: `enabled` replaces `EnableGitIntegration`. A migration copies the
  old value on first run.
- Core removes 11 Git IPC methods, `GitText` on rows, the Git menu, the Git
  commands, the inline panel and the toolbar button. `domainMethodCount`
  drops to 75.

### 11.4 Installer

The three plugins build in the same CI run and are staged under
`payload\plugins\`. The installer writes their manifest hashes to the approval
store so no consent dialog appears on a fresh install. The portable zip has the
same layout.

## 12. Repository layout

```text
src-winui/SimpleFile.PluginSdk/      contracts, published as NuGet
src-winui/SimpleFile.PluginHost/     discovery, loading, consent, settings rendering, backend client
crates/sumafile-plugin/              Rust backend helper crate
plugins/
  sevenzip/                          plugin.json, Rust backend, bundled 7-Zip
  remote/                            plugin.json, C# UI, Rust backend
  git/                               plugin.json, C# UI, Rust backend
ipc/schema/plugin/v1/                backend pipe protocol and host methods
docs/PLUGINS.md                      author guide
```

The three plugins live in the same solution and Cargo workspace so CI builds
them, but they reference only the SDK package and the helper crate, never the
host projects. That keeps them honest as third-party examples.

## 13. Testing

- SDK contract tests pin every public type and member; a change fails until the
  major is bumped or the test is updated deliberately.
- A fixture plugin under `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin`
  exercises every contribution kind, both declarative settings and a custom
  page, a backend with `host.*` calls, and an archive engine for a fake
  `.hello` extension.
- Loader tests cover shadowing, incompatible SDK major, invalid manifest,
  consent state transitions and signature verification.
- Service tests cover `register_archive_engine`, forwarding, fault handling
  and capability merging with a mock engine.
- `check-ipc-schema.mjs` learns the new method count and validates
  `ipc/schema/plugin/v1`.
- `check-winui-packaging.mjs` checks the three plugin folders in the payload.
- UI smoke adds a run with `payload\plugins\` deleted to prove the app starts
  and shows no Git, remote or 7-Zip UI.
- The startup budget check adds a stage for plugin discovery.

## 14. Phases

1. SDK, plugin host, backend client with inbound `host.*` dispatch, service
   archive-engine registration, fixture plugin, Settings > Plugins, consent
   dialog, XAML spike. Git, remote and 7-Zip stay internal.
2. 7-Zip plugin. Exercises the service hook.
3. FTP/SFTP plugin. Exercises the direct host path, progress slot, sidebar
   slot and custom settings page.
4. Git plugin. Adds the panel and row-decoration slots.
5. Plugin template repository, `docs/PLUGINS.md`, NuGet and crates.io
   publishing.

Each phase ends with the full check suite green and a release-build smoke run.
