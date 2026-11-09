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
| `sidebar` | `{ id, title, provider, command? }` | A collapsible section in `SidebarView`. The host draws its header from the manifest: the title, a collapse button and, when `command` names a declared command, an icon button that runs it. The provider implements `ISidebarSectionProvider` and fills the body. `id` follows the settings key grammar and is unique within the plugin. |
| `panels` | `{ id, title, provider, placement: "right" \| "bottom", detachable }` | A dockable side panel next to the file list, with the same host-or-window toggle the Git workbench has today. |
| `rowDecorations` | `{ provider }` | Per-row text and brush key plus per-folder flags supplied by an `IRowDecorationProvider`, batched per folder. |
| `archiveFormats` | `{ extension, canList, canExtract, canCreate, canModify, engineName }` | Registered with the Rust service as an archive engine served by the plugin backend, section 6.4. |
| `settingsPage` | `{ provider }` | A custom settings control for plugins whose settings exceed the declarative schema. |

`sidebar` and `settingsPage` need a `ui` assembly; the reader rejects them
without one (`contributions.sidebar needs a ui assembly.`). Phase 3 also makes
the reader reject any key under `contributions` it does not know, with
`The contributions.<key> kind is not supported by this host.`, so a typo never
loads as a silently missing slot. `panels` and `rowDecorations` are accepted and
ignored until phase 4 implements them.

## 5. UI half

### 5.1 SDK

A new project `src-winui/SimpleFile.PluginSdk` holds only contracts and is
published as a NuGet package named `SumaFile.PluginSdk`. It stays WinUI-free
(`net10.0-windows`, no Windows App SDK reference) and hands UI objects across
as `object`; the host checks the WinUI type at run time (phase 3 decision,
below). The public surface:

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

Phase 3 shapes of the UI contracts (namespace `SimpleFile.PluginSdk`):

- `ISidebarSectionProvider.CreateSidebarSection(PluginSidebarSectionRequest(SectionId, Provider))`
  and `ISettingsPageProvider.CreateSettingsPage(PluginSettingsPageRequest(Provider))`
  return `object`, which must be a `Microsoft.UI.Xaml.FrameworkElement`. The
  entry type implements them, as it implements `ICommandHandler`.
- `IPluginHost.Windows` is an `IPluginWindows` with
  `Attach(object window, PluginWindowOptions(Title, Width, Height))`. The window
  must be a `Microsoft.UI.Xaml.Window`. The host gives it SumaFile's window
  chrome and tracks it until it closes.
- `IPluginDialogs.ShowDialogAsync(object contentDialog, ct)` returns a
  `PluginDialogResult` (`None`, `Primary`, `Secondary`). The dialog must be a
  `Microsoft.UI.Xaml.Controls.ContentDialog`. It is shown through the host's
  dialog gate; a null `XamlRoot` means the main window.
- The host checks each type: the App's `ValidateUiElement` for provider
  results (a failure marks the plugin LoadFailed), and an `ArgumentException`
  from `Attach` and `ShowDialogAsync`.

The thread contract, recorded in the SDK's XML docs:

- `ActivateAsync`, `ExecuteAsync`, `CreateSidebarSection` and
  `CreateSettingsPage` run on the UI thread (the host's `IPluginUiThread`).
- `DeactivateAsync` runs on a thread-pool thread.
- A provider may be asked again for the same section or page after the host
  dropped the previous element, and must then return a new element (or one with
  no parent).
- The host closes attached windows before it stops the backend and deactivates
  the plugin.

Plugin UI projects reference the Windows App SDK compile-only
(`ExcludeAssets="runtime"`, `PrivateAssets="all"`); at run time the WinUI types
come from the host through the shared prefixes of section 5.2. Plugin UI may
read these theme resource keys from `Application.Current.Resources` (or as
`{ThemeResource}` in `XamlReader.Load` strings), which the host keeps stable:
`SfTextPrimaryBrush`, `SfTextMutedBrush`, `SfBgPrimaryBrush`,
`SfBgSecondaryBrush`, `SfBorderBrush`, `SfAccentSoftBrush`, `SfIconFont`,
`SfGhostButtonStyle`, `SfTextBoxStyle`, `SfEmptyHintStyle`,
`SfSidebarListStyle` and `SfNavListItemStyle`.

`PluginSdk.Version` stays `1.0.0` until phase 5 publishes the package; the
additions above change the pinned surface snapshot
(`Fixtures/PluginSdkSurface.v1.txt`) deliberately.

Phase 3 decision: object-typed UI hand-offs in a WinUI-free SDK at `1.0.0`.
This replaces "It references the Windows App SDK so plugins can build WinUI
controls", and the phase 1 ruling's expectation of WinUI-typed contracts with a
retarget and an SDK minor bump. Why: an SDK that references the Windows App SDK
would make the xUnit test project a self-contained WinUI app. Cost: plugin
authors lose compile-time typing on `CreateSidebarSection`,
`CreateSettingsPage`, `IPluginWindows.Attach` and `ShowDialogAsync`. It fixes
the shape of a public contract that phase 5 publishes: typed members could be
added later in a minor version, but removing the `object` ones would need a
major version. User confirmation: pending. The plan lists this as a decision to
confirm with the user before Task 7, and the Task 7 implementer could not ask;
record the user's answer here.

### 5.2 Loading

Each plugin assembly loads in its own non-collectible `AssemblyLoadContext`.
The context resolves `SimpleFile.PluginSdk`, `Microsoft.WindowsAppSDK`,
`CommunityToolkit.Mvvm` and the .NET runtime from the host so types unify. Other
dependencies resolve from the plugin folder.

Phase 3 made the shared set exact. An assembly whose name starts with one of
these prefixes always comes from the host: `SimpleFile.PluginSdk`,
`Microsoft.WindowsAppSDK`, `Microsoft.WindowsAppRuntime`, `Microsoft.UI`,
`Microsoft.WinUI`, `Microsoft.InteractiveExperiences`, `Microsoft.Graphics`,
`Microsoft.Web.WebView2`, `Microsoft.Security.Authentication.OAuth`,
`Microsoft.Windows` (which covers `Microsoft.Windows.SDK.NET`), `WinRT`,
`CommunityToolkit.Mvvm`, `System.` and `Microsoft.Extensions.`. So a plugin
that references WinUI and the Windows App SDK compile-only gets the host's
projections, and a `Window` or `UIElement` it builds is the host's type.

Native DLLs follow one rule. A native DLL the plugin's `deps.json` lists loads
from the plugin folder, unless the host's own folder ships a DLL of that name
(`<name>` or `<name>.dll`). Then the host's copy wins, because the host's code
already uses it and a second copy of the same module would split its state.
`PluginLoadContext.LoadUnmanagedDll` implements this.

Loading happens on first use: the first command invocation, the first time a
contributed sidebar section, panel or row decoration becomes visible, or the
first time the settings page opens. Manifest reading and contribution rendering
happen at startup and must stay under 50 ms for ten plugins in the startup trace.
To keep it there, a scan reads each `plugin.json` once (the bytes it parses are
the bytes it hashes for trust), reads `bundled.json` and the approvals file once,
and judges every plugin against those snapshots
(`Discover_TenPlugins_StaysUnderTheStartupBudget` pins the ten-plugin case).
At startup the App does those reads on the thread pool while the file service
starts (`PluginManager.PrepareScan`), so the `plugins-discovered` span holds only
evaluation and contribution rendering. Phase 3 measured the cold two-plugin
delta at about 36 ms before this and about 12 ms after it. Rescans read afresh.

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

Spike outcome (2026-11-07, plan task 17): **failed; compiled XAML stays
unsupported in SDK 1.** A WinUI class library (`UseWinUI`, WASDK 2.4.0) holding
a copy of `GitWorkbenchView` was loaded through `PluginActivator` from
`target\fixture-plugins\com.sumafile.spike\`; the assembly loaded into its
`plugin:` context, but `new SpikeWorkbenchView()` threw
`Microsoft.UI.Xaml.Markup.XamlParseException` (HRESULT `0x802B000A`, "XAML
parsing failed.") from `Application.LoadComponent` in the generated
`InitializeComponent` under (a) nothing extra, (b) the library's
`SimpleFile_Spike_XamlLib_XamlTypeInfo.XamlMetaDataProvider` added to the App
provider's `OtherProviders`, and (c) (b) plus the library's `.pri` copied next
to `SumaFile.exe` and opened with `ResourceManager`. The cause is the URI, not
the type metadata: the generated locator is
`ms-appx:///SimpleFile.Spike.XamlLib/SpikeWorkbenchView.xaml`, and in this
unpackaged app `ms-appx:///` is rooted at the executable's folder (the copied
PRI itself resolves the XBF to `<exe dir>\SimpleFile.Spike.XamlLib\SpikeWorkbenchView.xbf`).
Copying the plugin's `SimpleFile.Spike.XamlLib\` folder under the app
directory made the view load and render in a `ContentDialog` with or without
lever (b), and a plugin-side `Application.LoadComponent` with a `file:///` URI
into the plugin folder (XBF or XAML) failed with the same `0x802B000A`. Writing
plugin files into the install folder is not acceptable (per-machine installs
are read-only, assembly-named folders collide, upgrades wipe them), so phase 4
converts the Git plugin's three XAML views to code-built UI (or `XamlReader.Load`
strings, which remain supported), and the spike project and probe were deleted.

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

Cancellation keeps the existing convention: a client sends `cancel` with the
operation id, and the backend is responsible for honouring it. Only clients
send `cancel`, whether the client is the host or the service, as a request or
as a notification, on any of its connections. The backend never sends it.

Cancel flags are backend-wide (phase 2 decision). A `cancel` on any connection
is visible to every connection of the backend while its id is in flight. The
helper crate owns each flag's lifetime, so handlers never clear one. An id is
in flight in two cases. First, while a request carrying it as
`params.operationId` runs: the server records the id before the handler starts
and drops the id and its flag when the handler returns. Second, for an id the
host returned from `host.progress_begin`, from that reply until the backend
calls `host.progress_complete` or `host.progress_fail` for it, or the
connection that began it closes. The second case is how the host's Cancel on a
plugin's own progress row reaches the plugin, since no request carries that id.
A `cancel` for an id that is not in flight (unknown, finished, or for a request
not yet read on another connection) is kept among the 64 most recent early
cancels. It is not a flag: the id reads as not cancelled until it goes in
flight, and then it starts cancelled. In-flight flags are never evicted. The
backend therefore never holds more than its in-flight ids plus 64.

### 6.3 Host methods available to backends

| Method | Purpose |
| --- | --- |
| `host.get_settings` / `host.set_setting` | The plugin's own keys, section 8. |
| `host.progress_begin` / `host.progress_report` / `host.progress_complete` / `host.progress_fail` | Appears in the Transfers window with cancel. |
| `host.log` | Written to the SumaFile log with the plugin id. |
| `host.notify` | A status-center message. |

A plugin's progress row is an external Transfers row (`EnqueueExternal`). Its
runner is `PluginOperationWait.WaitAsync(finished, cancellation, 30 s)`
(`SimpleFile.Core`): before a cancel it reports Completed when the plugin
completes the operation and fails with the plugin's message when it fails it.
Cancel asks the plugin to stop (`cancel` with the row id); the row then stays
Cancelling until the plugin has completed or failed the operation, or 30 s
pass, and only then reads Cancelled. So a Cancelled row means the plugin has
stopped writing, unless it ignored the cancel for 30 s. A plugin row (and an
archive-engine row) has no per-task Options: its Options button is disabled
(`TransferOperationViewModel.CanChangeOptions` is `CanCancel && !IsExternal`)
and the click does nothing, because only the service's local engine reads
`transfer.overrides.v1`. A plugin's transfer settings live in its own settings.

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
  `create`, and the mutate operations, keyed by extension.
  `PluginArchiveEngine` forwards over the pipe using methods `archive.list`,
  `archive.extract`, `archive.create`, `archive.rename`, `archive.delete`,
  `archive.add`, `archive.read_entry`.
- Decision (phase 2), replacing "The built-in formats implement it
  in-process": the built-in zip, tar, tar.gz and rar formats keep their
  in-core code paths (`ArchiveKind::BuiltIn`) and do not implement
  `ArchiveEngine`. The trait is the plugin contract only.
- `get_archive_capabilities` merges the built-in table with registered engines.
- If the plugin is absent or faulted, `.7z` reports `can_* = false` with the
  note "7-Zip support requires the SumaFile 7-Zip plugin."

`register_archive_engine` and a matching `unregister_archive_engine` are the
only additions to the fixed method list.

Engine protocol. The wire shapes and deadlines are in
`ipc/schema/plugin/v1/protocol.json`; the semantics are these.

- Entry paths are `/`-separated and relative. `innerPath` matches the entry
  and its descendants by whole segments, so `docs` never matches `docs2`.
- `destination` for `archive.extract` and `archive.read_entry` is an empty
  folder the service created. Engines must refuse a non-empty destination and
  must never write outside it. Core moves the listed entries out of it, with
  its own containment, conflict and link checks. A link is a symlink or a
  junction, that is a reparse point whose tag is a name surrogate. Other
  reparse points, such as cloud-file placeholders, are ordinary files and
  folders.
- `archive.read_entry` refuses a selection over `maxEntries` or `maxBytes` by
  declared sizes. It fails with `Archive entry not found: <innerPath>` when
  nothing matches.
- `archive.create` makes a new file and must fail rather than append.
- `archive.rename` (to a full new path, which also covers moves inside an
  archive), `archive.delete` and `archive.add` change the archive in place and
  atomically. On failure or cancel the archive is left as it was.
- `archive.add` stores `baseDir/<p>` at `<p>`. The caller guarantees that
  nothing but parent folders exists at those paths.
- `archive.progress {operationId, current, total, currentItem?}` is a
  notification from the backend to the service. It carries the request's
  `operationId`, with `current <= total`; `total` 0 means unknown. Backends
  should send it at least every 5 s while working, `read_entry` included:
  reading one entry of a solid archive can mean decompressing a whole block
  first. Each one resets the caller's idle deadline.
- After a `cancel`, the call fails with `-32000` and the exact message
  `Archive operation cancelled` (`cancel.cancelledMessage` in the protocol).

Service side (phase 2). These rules are how the service keeps browsing
responsive while a plugin works.

- Pool. The service talks to each plugin through a small pool of pipe
  connections (`max_connections` 8), with at most one call in flight on each.
  A call takes an idle connection, or opens one while the pool has room.
  Otherwise it waits up to 30 s and fails with `plugin <id> is busy`. A
  transport error or timeout closes that connection and discards every idle
  one, so the next call reconnects and handshakes again. The handshake reply
  must name the registered plugin id and SDK major 1.
- Deadlines. Handshake 5 s, `archive.list` 30 s and `cancel` 5 s are fixed.
  `archive.read_entry` (120 s) and every other `archive.*` method (600 s) are
  idle deadlines.
- Heartbeat and relay. Every read- and transfer-class call carries an
  `operationId`: the operation's own id, or `svc-<pid>-<n>`. Each
  `archive.progress` for that id resets the call's idle deadline, and the
  service relays it to the operation's progress. A tracked `extract_archive`
  or `create_archive` (one sent with `operationId`) reports it to the client
  as `operation-progress`, with `operation_type` `extract-archive` or
  `create-archive`.
- Cancel. `cancel_operation` sets the operation's flag. The waiting call sees
  it within 50 ms and sends `cancel {operationId}` on another pooled
  connection (an idle one, or a new one even beyond the limit). It then waits
  at most 5 s for its own reply, closes the connection if none comes, and
  fails with the cancelled message.
- Registration ticket. The service numbers each plugin's register and
  unregister calls in the order it receives them. `register_archive_engine`
  takes its number on the session loop before it defers the connect, and it
  commits only if no later register or unregister arrived meanwhile.
  Otherwise it fails with `Registration of plugin <id> was superseded by a
  later registration change` and changes nothing.
- Off the session loop. A plugin's `host.get_settings` reaches the host, and
  the host answers it with `get_db_settings` on the same service connection.
  A call that reaches an engine therefore never runs on the session loop,
  where that lookup would wait behind the call it serves. Inside an archive,
  `create_directory`, `create_file`, `rename_entry`, `open_file`,
  `reveal_in_folder` and `open_file_with` run on the General blocking lane,
  like `delete_entry` and `move_to_trash`. Tracked extract and create run on
  the Transfer lane.

UI side (phase 2). Extract and create for a plugin format (an archive whose
extension a plugin engine serves) appear in the Transfers window as a row
titled `Extracting <archive>` or `Creating <archive>`, with the service's
byte progress and Cancel. The app sends them as tracked operations with an
`archive-<guid>` id. Cancel sends `cancel_operation` once and the row stays
open until the service replies, so it reaches Cancelled only after the engine
has stopped. The built-in formats keep the modal progress dialog.

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
- `SidebarView` renders plugin sections below the built-in sections, with
  headers from the manifest (title, collapse button, optional command button).
  The body comes from `PluginManager.CreateSidebarSectionAsync`, which activates
  the plugin on the UI thread through the same path as commands; a provider
  that throws, returns null or returns something other than a
  `FrameworkElement` marks the plugin LoadFailed and withdraws its sections.
  Each section carries the plugin record's `Epoch`, which changes whenever the
  host retires the plugin's runtime, so a stale body is replaced.
  The App (`MainWindow.PluginSidebar`, rules in `SimpleFile.Core`'s
  `PluginSidebarLayout`) keys each rendered section `<pluginId>:<sectionId>:<epoch>`
  and diffs the wanted sections against the shown ones on every contribution
  change, so a kept section keeps its body and a new epoch gets a new section.
  The header renders from the manifest alone. A body loads only when startup has
  marked ready (queued at low dispatcher priority), the sidebar is shown and the
  section is expanded, and it is asked for once per rendered section; those
  three moments re-check it. A failure leaves the muted text
  `This section could not be loaded: <message>`. Automation ids:
  `PluginSection.<pluginId>.<sectionId>` for the section, with `.Collapse` and
  `.Command` for its header buttons. The collapse state is the host's, not the
  plugin's: `UiSettings.CollapsedPluginSidebarSections`, saved as a JSON array
  of `<pluginId>:<sectionId>` under `sidebar.pluginSectionsCollapsed` and
  captured by saved workspace layouts (`pluginSectionsCollapsed`).
- Plugin code runs on the main window's thread: the App passes
  `PluginUiThread` (inline on the dispatcher thread, otherwise queued with
  `TryEnqueue`) as `PluginManagerOptions.UiThread`, and `ValidateUiElement`
  accepts only a `Microsoft.UI.Xaml.FrameworkElement`
  (`returned <type>, not a Microsoft.UI.Xaml.FrameworkElement`).
- Plugin data root: `PluginDiscovery.DataRoot()` is
  `%LOCALAPPDATA%\SumaFile\plugins-data`, or `SUMAFILE_PLUGIN_DATA_ROOT` when set.
  Every UI smoke mode sets it to a disposable folder, and `plugins.log` gets
  `[warn] SUMAFILE_PLUGIN_DATA_ROOT replaces the plugin data folder: <path>`.
- Plugin windows: a plugin creates its own `Window` in code and hands it to
  `IPluginHost.Windows.Attach`. The App's `IPluginWindowHost` gives it the
  host's window chrome and tracks it. Each plugin runtime gets a new host from
  `WindowsFor`. Teardown seals that host: from the moment `CloseAllAsync` is
  called, `Attach` closes the window it is handed at once, so a window a plugin
  opens while it is being stopped (an error window after its backend died,
  say) never outlives `DeactivateAsync`. A sealed host is never reused; a
  re-enabled plugin gets a new one. `PluginWindowSet<TWindow>`
  (`SimpleFile.PluginHost`) holds the tracking and the seal.
  The App's host is `PluginWindowHost`: `Attach` accepts only a
  `Microsoft.UI.Xaml.Window` and applies `SecondaryWindowChrome` (icon, Mica,
  DPI-aware size, the app theme). When the main window closes, `OnClosed`
  seals every plugin's host and closes its windows at once (`CloseAllNow`),
  before the session cleanup awaits, so the later teardown finds none open.
- Teardown order (disable, rescan away, load failure, shutdown): the plugin's
  windows close first (and its window host is sealed), then its backend stops,
  then `DeactivateAsync` runs.
  The wait for the windows is bounded by 5 s (`WindowCloseTimeout`, the same as
  the runtime's retire timeout); after that, or when the close throws, the host
  logs `[warn] Plugin windows did not close within 5 s; stopping the plugin anyway.`
  (or the exception) and stops the plugin anyway, so teardown never hangs on a
  dispatcher that is shutting down.
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

One page per Settings window: an element has one parent and one `XamlRoot`, so
each Settings window asks `PluginManager.CreateSettingsPageAsync` for its own
page, only for an Enabled plugin (no code loads for any other state), and
reuses it while the plugin record's `Epoch` is unchanged.

The slot is a `ContentPresenter` with AutomationId
`PluginSettingsPage.<pluginId>` in the plugin's card, below the declarative
settings and above the status line. The Settings window keeps a
`PluginPageCache<FrameworkElement>` (`SimpleFile.Core`) keyed by plugin id and
`Epoch`; the card is rebuilt on every plugin change, so a cached page is
detached from its old slot before it is added to the new one, and a page is
asked for at most once at a time. A failure shows
`This settings page could not be loaded: <message>` in the slot (a provider
failure also marks the plugin LoadFailed, whose card has no slot). Settings
search walks into the slot's content and into `UserControl.Content`, and the
current query runs again whenever a page is inserted, so the page's labelled
controls are found even when the page arrives after the query was typed.

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
  or Keep disabled. Plugins stay disabled until enabled. First-party plugins
  that ship with SumaFile need no consent (phase 2 decision): the release
  build writes `plugins\bundled.json` next to them, listing each one's id and
  the SHA-256 of its `plugin.json`
  (`{"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"<64 lowercase hex>"}]}`).
  The host enables a listed plugin found in its install root
  (`<AppContext.BaseDirectory>\plugins`, compared as a full path,
  case-insensitively) with that manifest hash, unless the user stored Disabled
  for that folder; a stored Disabled holds whatever manifest it was stored
  with, so it survives an upgrade. Nothing is written to the approval store
  for bundled trust. User-root plugins, a copy of a bundled plugin in the user
  root, and lists in any other root always need consent. This replaces "the
  installer writing their manifest hashes to the approval store", which NSIS,
  MSI admin installs and the portable zip cannot do reliably.
- **Approval store.** `%LOCALAPPDATA%\SumaFile\plugin-approvals.json`. A read
  that fails (the file is held by another process) is retried three times,
  50 ms apart. If it still fails, lookups treat the store as empty, but
  enabling or disabling a plugin fails with "Could not read the plugin
  approvals file: …" and saves nothing, so other plugins' records are never
  lost. A bundled plugin scanned while the store cannot be read is
  NeedsConsent, not Enabled, because a Disabled the user stored cannot be
  seen; the next scan with a readable store restores its state. Saves go through a temp file unique to the process and call, which is
  removed when the final move fails.
- **Signature.** `signature.json` holds an ed25519 signature over
  `plugin.json` plus the SHA-256 of every file listed in a `files` array. The
  host verifies it with the publisher's `publicKey` using an Ed25519
  verifier in the host (BouncyCastle) with the same raw 32-byte base64 public
  key format the updater uses, and shows "Verified publisher" with the key fingerprint. Unsigned
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
| Backend exits unexpectedly | In-flight calls fail with a plugin-faulted error. Status center offers Restart. Archive engines are unregistered from the service until restart; the host raises its change notification again once that unregister has finished, so the archive capabilities never keep a lost engine. If the file service itself restarts, the host registers every live engine with the new service in the background, with the same bounded attempts as below; the reconnect does not wait for it. |
| Backend call times out | Same timeouts as service calls. Registering an archive engine with the service makes up to 3 attempts of 15 s each, 1 s and then 3 s apart. After the third failure the plugin is Faulted, its backend is stopped and its engines are unregistered once more, unless a restart has registered a newer backend meanwhile. |
| Row decoration provider is slow | Folder renders undecorated after 250 ms; decorations apply when they arrive. |
| Plugin faulted while a 7-Zip archive is open in a pane | The pane re-lists and shows the listing error that carries the note `7-Zip support requires the SumaFile 7-Zip plugin.` Its old rows are dropped. Once the engine registers again, the pane re-lists without user action and the note is cleared. |

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
  It links the plugin's library, resolves the binary as the plugin does and
  registers it with the core as the `.7z` engine, so its baseline still runs
  through core validation and staging.

Decisions (phase 2, as built):

- Version. The plugin's version, `1.0.0` in `plugin.json` and in the backend
  crate, is independent of the app version. `release:prepare` does not bump
  it.
- Core without the plugin. `.7z` is no built-in format, so only an engine
  serves it and a `.7z` registration is accepted. A first-party placeholder
  table (`archive/placeholders.rs`) lists `.7z` with the label `7z` and the
  note "7-Zip support requires the SumaFile 7-Zip plugin." While no engine
  serves `.7z`:
  - `get_archive_capabilities` adds, after the built-in and engine rows, one
    row `{ format: "7z", extension: ".7z", can_* false, engine:
    "unavailable", note }`.
  - `list_archive`, `extract_archive`, `create_archive(.., "7z")` and the pane
    listing of the file fail with the note. `extract_archive` fails before it
    creates the destination.
  - Any path inside the file (`x.7z\docs`) fails with the note in
    `split_archive_path`. The pane listing routes such a path to the archive
    listing, so the pane gets the note, not "Path is not a directory". The
    file itself is an ordinary file, so it can still be copied, renamed or
    deleted. An error is never cached.
  - RAR creation fails with "RAR creation is not supported by SumaFile. Use
    ZIP, TAR or TAR.GZ, or an archive format a plugin adds."
- Binary resolution:
  - An override that names an existing file wins. An override folder
    resolves to the first of `7zz.exe`, `7z.exe` and `7za.exe` inside it.
  - Otherwise the backend uses `<backend dir>\7za.exe`. There is no
    `SIMPLEFILE_7Z`, PATH or Program Files fallback.
  - A missing override falls back to the bundled binary and logs a warning
    through the host connection, once per distinct value.
  - With neither present it fails with "7-Zip is unavailable: the bundled
    7za.exe is missing from <dir>. Reinstall SumaFile or set the 7-Zip
    executable override."
- Settings:
  - Before each operation the backend reads `binaryOverride` with
    `host.host_connection()?.get_settings(["binaryOverride"])` under a 2 s
    timeout. A success updates its cached value. A failure, or no host
    connection, uses the cache, which starts as no override. A change
    therefore applies on the next operation.
  - The lookup goes back through the host to `get_db_settings` on the same
    service connection. It answers quickly only because the service runs
    every engine-reaching call off its session loop.
  - `SIMPLEFILE_7Z` is no longer read. While it is set and the override is
    empty, the first operation logs, once per backend process:
    "SIMPLEFILE_7Z is no longer read. Set Settings > Plugins > 7-Zip archives
    > 7-Zip executable override instead."
- Threads:
  - Every operation runs 7za inside `tokio::task::spawn_blocking`, after the
    settings lookup on the async side.
  - The blocking closure owns a progress bridge (a `HostClient` clone plus
    the `operationId`) as its observer. The bridge sends `archive.progress`,
    which never waits, and reads the backend-wide cancel flag.
  - The runtime has two worker threads. They only route frames, so a
    `cancel` or a new `archive.list` is read while 7za processes run on every
    connection.
- Processes:
  - One 7za process per command, never one per entry. Extract and read are
    a list plus one `x`; rename and delete are a list plus `rn` or `d`;
    create and add are a single `a`.
  - Every spawn uses `CREATE_NO_WINDOW`, stdin null, and stdout and stderr
    drained on their own threads. Exit code 1 or lower is success.
  - Errors read `<action> failed: <detail>`, with the detail capped at 4000
    bytes on a character boundary.
  - Switches are `-sccUTF-8 -scsUTF-8 -spd -ssc -y`. Working commands add
    `-bsp1 -bso0`, and the percentage is parsed from stdout. List and extract
    pass an empty password (`-p`), so an encrypted archive fails with "Wrong
    password" instead of prompting.
  - `-ssc` makes 7-Zip match names by exact case, as the backend's own
    checks do; core sends every entry name spelled exactly as listed.
    Without it, 7-Zip on Windows ignores case: in an archive holding `a.txt`
    and `A.TXT`, reading, deleting or renaming `a.txt` also reads, deletes
    or renames `A.TXT`, and adding `A.TXT` replaces `a.txt`.
  - Names travel in UTF-8 listfiles through `-i@`, one quoted name per line,
    because 7-Zip trims each line before matching it. `rn` takes its two
    names after `--`.
  - Update commands (`a`, `d` and `rn` on an existing archive) pass
    `-w<dot folder beside the archive>`, which is removed afterwards.
- Safety:
  - Every listed name passes the backend's entry-name validation before
    anything is written; core validates the listing again.
  - Extract and read refuse a non-empty destination. Afterwards they walk it
    and fail, removing the output, if they find a symlink or junction. As in
    core, a cloud-file placeholder that a sync client made of an extracted
    file is not a link.
  - Read checks declared sizes against its limits before 7za runs, and the
    written bytes afterwards.
  - Create refuses an existing archive and removes a partial one on failure
    or cancel.
  - Delete refuses to remove every entry with "7-Zip archives cannot be
    rewritten with no entries" and leaves the archive unchanged.
- Progress and cancel:
  - Every operation that carries an `operationId` reports progress, reads
    included: `(0, total)` first and `(total, total)` on success, where
    `total` is the declared or source bytes. In between it reports each
    percentage change, and at least every 5 s even when 7za prints nothing.
  - Cancel is polled every 50 ms. When it is set, 7za is killed and waited
    for, its drain threads are joined, and the call fails with "Archive
    operation cancelled". When the backend's serve loop ends, every running
    7za is killed.

### 11.2 FTP/SFTP plugin, `com.sumafile.remote`

- Folder: `plugin.json`, `SumaFile.Remote.dll`, `SumaFile.Remote.Model.dll`,
  `SumaFile.Remote.deps.json` and `sumafile-remote-backend.exe` (phase 3: the
  WinUI-free model assembly holds the view model and the backend client, so
  their tests run in `SimpleFile.Tests`; the deps file lets the plugin's load
  context resolve it from the plugin folder).
- Backend: `simplefile-core/src/remote`, `simplefile-service/src/remote`
  including `transfer.rs`, `secrets.rs` and both providers move as they are.
  Transfer progress is reported through `host.progress_*` so remote transfers
  keep appearing in the Transfers window with cancel.
- Profiles and credentials (phase 3 decision). Profiles: the backend keeps them in `<data dir>\remote-profiles.db` (table `remote_profiles`, the 1.0.4 schema). On first use it imports, read-only, every row of the `remote_profiles` table in the metadata.db the 1.0.4 service used (resolved as the service does: `SIMPLEFILE_METADATA_DB`, else the first existing `metadata.db` under `SIMPLEFILE_APP_DATA_DIR`, `%APPDATA%\com.simplefile.desktop`, `%APPDATA%\SumaFile`, `%APPDATA%\SimpleFile`, `%LOCALAPPDATA%\SumaFile`, `%LOCALAPPDATA%\com.simplefile.desktop`, `%LOCALAPPDATA%\SimpleFile`), skips ids it already has, and records per legacy database that it ran, so a run against another database (a test's or a smoke's) never blocks the real import. It never writes the old table, which stays for a downgrade. Credentials are read in place: the plugin keeps the `SumaFile.Remote.` prefix, `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`, the UTF-8 `kind\0value` blob and each profile's `credential_target`, so saved passwords and passphrases keep working with no migration. Deleting a profile deletes its credential.
- Backend methods (phase 3). The backend serves eleven plugin-owned methods to
  its UI through `IPluginHost.InvokeBackendAsync`, documented with their
  params, results, events, types and error texts in
  `plugins/remote/protocol.json` and checked by `check-ipc-schema.mjs`:
  `profiles.list`, `profiles.save`, `profiles.delete`, `profiles.test`,
  `session.connect`, `session.disconnect`, `fs.list`, `fs.create_directory`,
  `fs.rename`, `fs.delete` and `transfer.run`. Params are camelCase; results
  keep the 1.0.4 IPC shapes with snake_case fields. `profiles.save` and
  `profiles.delete` emit `remote.profiles-changed`.
- Progress. Each `transfer.run` opens a Transfers row with
  `host.progress_begin` (`Uploading <n> item(s) to <host>:<destination>` or
  `Downloading <n> item(s) from <host>`), reports at most every 100 ms with
  at most one report in flight (`<files>/<total files> files · <item>`), and
  ends it with `progress_complete`, or `progress_fail` with
  `Transfer cancelled` or `<k> of <n> selections failed: <first error>`. The
  manager window gets `remote.transfer-progress` and `remote.transfer-tuning`
  events, each at most every 100 ms.
- Cancel. The backend stops a transfer when the host cancels either the
  request's `operationId` (the manager window's Cancel) or the row's id (the
  Transfers window's Cancel), polled every 50 ms. A cancel is an outcome, not
  an error: the reply carries `cancelled: true`. A backend killed mid-transfer
  cannot clean up; its staged download is recovered by the next download into
  that folder, and an interrupted upload's `.sumafile-partial.<nonce>` stays
  on the server.
- Transfer settings. The engine reads the plugin settings `bandwidthMib`,
  `checkMode`, `inplace`, `preallocate`, `flushOnClose`, `lowLevelRetries`,
  `highLevelRetries` and `retrySleepMs` (`plugin.com.sumafile.remote.<key>`)
  at the start of each transfer and every 5 s while it runs; a missing or
  unparsable value keeps the `Policy::default()` value. On first use the
  backend seeds the settings the host reports unset from the 1.0.4
  `transfer.policy` row, once per legacy database.
- Sessions. Sessions live in the backend process (`remote-session-N`) and end
  with it; a call on a session that is gone fails with
  `remote session not found: <id>`, and a call on a session whose transfer is
  running fails with `Remote session is busy; wait for or cancel its transfer`.
- UI assembly: `RemoteManagerWindow`, `RemoteManagerViewModel`, the sidebar
  profiles section as an `ISidebarSectionProvider`, the remote parts of
  `TransferOptionsDialog`, and the `ftp-sftp-manager` command.
  Phase 3 shape: the entry type `SumaFile.Remote.RemotePlugin` implements
  `IPlugin`, `ICommandHandler`, `ISidebarSectionProvider` and
  `ISettingsPageProvider`. Everything is built in code (section 5.3): the
  windows and lists are C# objects and theme-aware `XamlReader.Load`
  fragments using `{ThemeResource}` with the keys of section 5.1 (plus
  WinUI's own `SystemFillColorCriticalBrush` for the Insecure badge), with no
  `{Binding}` or `x:Bind`; values come from the view model's
  `PropertyChanged`. Every dialog (profile, host key, name, confirmations,
  errors, the connect progress dialog and Transfer options) goes through
  `IPluginDialogs.ShowDialogAsync` on the manager window's own `XamlRoot`.
  Pickers use the manager window's handle. The UI's automation ids
  (`RemoteAutomationIds`) are the UI smoke's contract. Lists stay
  virtualized, since plugin windows share the host's UI thread: each binds a
  copy of its view model collection as `ItemsSource` (one copy per UI-thread
  turn, so a folder filled one entry at a time updates once), with one row
  `DataTemplate` parsed once and filled per realized row in
  `ContainerContentChanging`. A realized row's container carries the shown
  name as its automation name and the path or profile id as its `Tag`.
- Contributions (`plugins/remote/plugin.json`): one command
  `ftp-sftp-manager` (`FTP/SFTP manager`, group `Tools`, host id
  `plugin:com.sumafile.remote:ftp-sftp-manager`), an overflow toolbar button
  with glyph E968, one sidebar section `connections` titled `Connections`
  (provider `connections`, header button running the command), and a custom
  settings page (provider `remote-settings`) with the `Open FTP/SFTP manager`
  button, which replaces the 1.0.4 Settings > Tools card. The section lists
  the profiles (`No connections. Open the manager to add one.` when there are
  none, `Could not list connections: <message>` when the listing fails),
  reloads when it is shown and on `remote.profiles-changed`, and opens the
  manager on the clicked profile.
- Settings: eight declarative settings in group `Transfers`, the keys the
  engine reads: `bandwidthMib` (integer, 0), `checkMode` (enum
  `size_and_mod_time`, `size_only`, `checksum`, `ignore_times`; default
  `size_and_mod_time`), `inplace` (false), `preallocate` (true),
  `flushOnClose` (true), `lowLevelRetries` (10), `highLevelRetries` (3) and
  `retrySleepMs` (1000). The manager's Transfer options dialog edits the same
  eight keys and writes only the changed ones. Tests pin the manifest defaults
  to the engine's `Policy::default()` (Rust) and to
  `RemoteTransferSettings.DefaultValues` (C#).
- Window lifetime: the plugin owns at most one manager window, shared by the
  command, the sidebar rows and the settings page; opening it again activates
  it (a sidebar row also selects its profile while no session is open).
  Those entry points run in WinUI event handlers, where an exception would
  end the App, so opening never throws: a failure is logged and shown as a
  message, and a window that failed to attach is closed and not kept. The
  window attaches itself through `IPluginHost.Windows.Attach` with
  `FTP/SFTP manager`, 1180 x 680. Closing it cancels its running transfer and
  disconnects its session. The host closes it before it stops the backend;
  `DeactivateAsync` closes a straggler on the window's own dispatcher and ends
  the plugin's backend event subscriptions.
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
`payload\plugins\`. The release build writes `payload\plugins\bundled.json`
with each staged plugin's id and manifest SHA-256 (§9), so no consent dialog
appears on a fresh install or after an upgrade; the installers write nothing to
the approval store. The portable zip has the same layout.

Phase 2 ships the first of them, `com.sumafile.sevenzip`:

- Payload layout. `scripts/build-winui-release.ps1` builds
  `sumafile-sevenzip-backend` in release, then stages it with
  `Install-BundledPlugins` from `scripts/lib/plugin-staging.ps1`, the same
  function dev runs and smokes use:

  ```text
  payload\
    SumaFile.exe, simplefile-service.exe, ...
    plugins\
      bundled.json
      com.sumafile.sevenzip\
        plugin.json, sumafile-sevenzip-backend.exe,
        7za.exe, 7za.dll, 7zxa.dll, License.txt, SUMAFILE_BUNDLE.md
  ```

- `bundled.json` is `{"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"<hex>"}]}`,
  UTF-8 without a BOM, where the hash is the lowercase SHA-256 of the staged
  `plugin.json` bytes (`PluginPackageHash`). The host honours it only for
  plugins in its install root (§9).
- The payload no longer carries `tools\7zip`. `Assert-Payload` fails when it is
  present and runs `Assert-BundledPlugins` on `payload\plugins`.
- NSIS: `.onInit` and the uninstaller also stop
  `sumafile-sevenzip-backend.exe`, never `7za.exe`, which could be the user's
  own job. Before copying files the installer removes 1.0.4's
  `$INSTDIR\tools\7zip`, then `$INSTDIR\tools` when it is empty, and the old
  `$INSTDIR\plugins\com.sumafile.sevenzip`, so no file from an older plugin
  stays behind. The user root under `%LOCALAPPDATA%\SumaFile\plugins` is never
  touched.
- WiX harvests `plugins\**` with the rest of the payload, and its
  `MajorUpgrade` removes 1.0.4's `tools\7zip`.
- Release smokes:
  - `smoke-winui-startup.ps1` runs the payload with `SUMAFILE_PLUGIN_ROOTS`
    set to the payload's `plugins` folder and a disposable
    `SUMAFILE_PLUGIN_APPROVALS_PATH`. It requires the `plugins.log` line
    `[com.sumafile.sevenzip] [info] Archive engines registered: .7z` and an
    approvals file with no record for the plugin.
  - `smoke-winui-file-ops.ps1` launches the payload backend and drives it
    through the payload service: register, create, list, browse and extract a
    `.7z` with Unicode and punctuated names, the capabilities row, then
    unregister and the plugin-absent note.
  - The MSI, NSIS and upgrade smokes run `Assert-BundledPlugins` on the
    installed or extracted `plugins` folder and fail when `tools\7zip` is
    present.

Phase 3 adds the second, `com.sumafile.remote` (§11.2):

- Payload layout with both plugins. `$script:BundledPluginIds` in
  `scripts/lib/plugin-staging.ps1` lists both, in this order, and
  `bundled.json` carries one entry for each:

  ```text
  payload\
    SumaFile.exe, simplefile-service.exe, ...
    plugins\
      bundled.json
      com.sumafile.sevenzip\
        plugin.json, sumafile-sevenzip-backend.exe,
        7za.exe, 7za.dll, 7zxa.dll, License.txt, SUMAFILE_BUNDLE.md
      com.sumafile.remote\
        plugin.json, sumafile-remote-backend.exe,
        SumaFile.Remote.dll, SumaFile.Remote.Model.dll, SumaFile.Remote.deps.json
  ```

- Sources. `Get-RemotePluginSources` takes the backend from
  `target\<debug|release>` and the three UI files from
  `plugins\remote\ui\SumaFile.Remote\bin\<Configuration>\net10.0-windows10.0.19041.0\`.
  The other files a 19041 library build emits (projections, `.pri`,
  `runtimes\`) are never staged: the host supplies them. A missing source
  fails with the `cargo build` or `dotnet build` command to run. The freshness
  check compares the backend, both DLLs and `plugin.json`.
- Builds. `scripts/build-plugins.ps1` runs
  `cargo build --locked -p sumafile-sevenzip-backend -p sumafile-remote-backend`
  and, unless `-SkipDotnet`, `dotnet build` of `SumaFile.Remote.csproj`. The
  release build adds `sumafile-remote-backend` in release and a Release build
  of `SumaFile.Remote.csproj` before it stages the payload, because the App
  publish does not build plugin projects. The SFTP test server
  (`sumafile-remote-test-server`) is built by `build-fixture-plugin.ps1` and
  the UI smoke, and never staged.
- NSIS: `.onInit` and the uninstaller also stop `sumafile-remote-backend.exe`,
  and the installer removes `$INSTDIR\plugins\com.sumafile.remote` before it
  copies the payload. Profiles and saved passwords live in the plugin's data
  folder and Windows Credential Manager, outside `$INSTDIR`.
- Release smokes, none of which touches the user's data:
  - `smoke-winui-startup.ps1` also sets `SUMAFILE_PLUGIN_DATA_ROOT`,
    `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` to its temp folder
    (`startup-timing.log` and `plugins.log` stay in
    `%LOCALAPPDATA%\SumaFile`, where it reads them). It requires the new
    `plugins.log` line `[com.sumafile.remote] [info] Discovered: Enabled`,
    an approvals file with no record for the plugin, and prints
    `Bundled remote plugin trusted.`. It stops payload backends of both
    plugins when it ends.
  - `smoke-winui-file-ops.ps1` launches the payload's remote backend with a
    disposable data folder and `SIMPLEFILE_METADATA_DB`, handshakes with the
    service's client name so `host.*` fails fast, and checks
    `profiles.list`, `profiles.save` (no secret; the profile's
    `credential_target` is `SumaFile.Remote.<id>`) and `profiles.delete`.
  - `smoke-winui-msi.ps1` runs the extracted app with
    `SUMAFILE_PLUGIN_ROOTS` set to the extracted `plugins` folder and the
    approvals path, plugin data root, metadata database and app data folder
    under its temp folder, and stops `sumafile-*-backend` processes under the
    extracted folder before removing it.
- The Debug UI smoke's `with` mode loads the remote plugin in the real host
  (`MainWindow.UiSmoke.Remote.cs`): bundled trust, the Connections section,
  one manager window per command, the profile dialog on the window's own
  `XamlRoot`, and the window host emptied on close.

## 12. Repository layout

```text
src-winui/SimpleFile.PluginSdk/      contracts, published as NuGet
src-winui/SimpleFile.PluginHost/     discovery, loading, consent, settings rendering, backend client
crates/sumafile-plugin/              Rust backend helper crate
crates/sumafile-transfer/            shared leaf crate: local-file transfer primitives
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

Phase 3 refinement: first-party plugin crates may also depend on the shared
leaf crate `crates/sumafile-transfer` (staging, the recovery journal, retries,
rate and memory limits, the transfer policy model, path conflicts and operation
ids, moved out of the service so the remote backend reuses the hardened code),
and on `simplefile-ipc` as §6.2 allows. They never depend on `simplefile-core`
or `simplefile-service`; `sumafile-transfer` itself depends on neither.
`scripts/check-winui-packaging.mjs` (`pluginCrateManifests`) fails the build
when a listed plugin crate manifest, or the leaf crate's, names either one.

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
- UI smoke covers `.7z` in both plugin modes (phase 2, as built), against a
  `sample.7z` the script makes with the bundled `7za.exe`
  (`SUMAFILE_SMOKE_ARCHIVES`):
  - With the plugin bundled in the install root (`-PluginsDir`): the plugin
    roots are `<exe dir>\plugins` and then the fixture roots, and only the
    fixtures are pre-approved. `com.sumafile.sevenzip` must therefore be
    trusted through `bundled.json`, and the script stops when that file is
    missing. The smoke covers these parts of the real host, service and
    backend chain:
    - capabilities and recognition;
    - listing and browsing Unicode and punctuation names;
    - a mutation;
    - create and extract as Transfers rows, and Cancel of a 256 MiB create,
      sent only once 7za reports progress, after which 7za has exited;
    - `binaryOverride`;
    - a killed backend (`sevenzip-backend-loss`: Faulted, the note in the
      pane, and the listing back after a restart, all with no user action);
    - a killed service (`service-reconnect`: `.7z` served again by the new
      service).
  - Without plugins (`-NoPlugins`): the `.7z` row is `unavailable` with the
    note. Recognition is off, the listing and the pane report the note, and
    `7z` is not offered for create.
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
