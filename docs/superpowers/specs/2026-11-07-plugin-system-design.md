# SumaFile plugin system

**Status:** implemented through phase 5 (§14) on the `plugin-system-phase-1`
branch; unreleased. Where this design and the code or `docs/PLUGINS.md` differ,
the code is right.
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
- Since phase 4 the service has 75 domain methods (`domainMethodCount`): phase 3
  moved the 13 remote methods and phase 4 the 11 Git methods into their bundled
  plugins, and Git's core module, UI and setting are gone (§11.3).

## 4. Package format

### 4.1 Locations

The host scans one directory at startup: `<install dir>\plugins\<plugin-id>\`
(`<AppContext.BaseDirectory>\plugins`). It holds the plugins shipped with the
installer and the ones the user installs; only `bundled.json` (§9) tells them
apart. Shadowing happens only when `SUMAFILE_PLUGIN_ROOTS` names several roots
(tests and smokes): a plugin id found in more than one of them resolves to the
copy in the later root, and Settings shows the other copy as shadowed.

A `.sfplugin` file is the plugin folder zipped with `plugin.json` at the root.
Settings > Plugins > Install from file unzips it into that same plugins folder,
and the plugin then waits for the consent dialog in section 9. A package whose
id the install root's `bundled.json` lists is refused before anything is
written (`<id> ships with SumaFile and cannot be installed from a package.`),
so a package can never replace a bundled plugin.

**Locations (phase 4 decision, user requirement 2026-11-09: no plugin file
outside the installation folder).** Every plugin file lives in the
installation folder. The defaults, and the overrides tests and smokes use:

| Item | Default | Override |
| --- | --- | --- |
| Plugin root | `<AppContext.BaseDirectory>\plugins` | `SUMAFILE_PLUGIN_ROOTS` (semicolon-separated) |
| Backend data folders | `<AppContext.BaseDirectory>\plugins-data\<id>\` | `SUMAFILE_PLUGIN_DATA_ROOT` |
| Approvals file | `<AppContext.BaseDirectory>\plugin-approvals.json` | `SUMAFILE_PLUGIN_APPROVALS_PATH` |
| Plugin log | `<AppContext.BaseDirectory>\plugins.log` (rotating to `plugins.log.1` at 1 MB) | `SUMAFILE_PLUGIN_LOG_PATH` |

`plugins.log` gets one `[warn]` line for each override in effect, for example
`[warn] SUMAFILE_PLUGIN_LOG_PATH replaces the plugin log: <path>`.

- **Temporary files.** Every plugin backend runs with `TMP` and `TEMP` set to
  `plugins-data\<id>\tmp`, which the host creates before the launch and never
  empties (the backend owns its contents). So the backend's own temporary
  files (the 7-Zip listfiles) and those of the processes it starts (7za, git
  and git's helpers) stay in the install folder. Plugin UI code runs in
  SumaFile's process and never resolves a temp or user folder itself
  (`Path.GetTempPath`, `Path.GetTempFileName`, `Environment.SpecialFolder`); a
  source-shape test guards it.
- **An unwritable install folder.** Both installers are per user, so the
  install folder is normally writable. When it is not (a portable copy under
  `Program Files`, an admin-protected custom folder): Install from file fails
  with `SumaFile cannot write to its plugins folder <root>: <message>`; Enable
  fails with the approvals file's error, shown on the plugin's card, and
  changes nothing, while a failed Disable write lasts for the session only;
  `plugins.log` writes are skipped, as every plugin log write is best effort;
  a backend fails to start with
  `SumaFile cannot create the data folder of plugin '<id>' at <path>: <message>`.
  Nothing falls back to `%LOCALAPPDATA%`.
- **No migration.** The pre-release `%LOCALAPPDATA%\SumaFile\plugins`,
  `plugins-data`, `plugin-approvals.json` and `plugins.log`, used only by the
  unreleased commit 46860dc, are never read, written or deleted.
- `startup-timing.log`, `startup.log` and `service.log` are the app's own and
  stay in `%LOCALAPPDATA%\SumaFile`, as do the settings database and the
  Credential Manager entries `SumaFile.Remote.*`; none of them is a plugin
  file.

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
| `version` | yes | Version of the plugin, shown to the user; semantic versioning by convention (the reader requires only a non-empty string). |
| `publisher` | yes | `{ "name", "url", "publicKey" }`. `publicKey` is optional base64 ed25519. |
| `sdkVersion` | yes | Semantic version of the SDK the plugin was built against. The host loads a plugin only when the major matches a major it supports and the minor is not above the minor of its `PluginSdk.Version` (final fix wave: the SDK assembly's version is major.minor.0.0, so a newer minor could not bind; such a plugin is IncompatibleSdk, "needs a newer SumaFile"). |
| `ui` | no | `{ "assembly": "<file>.dll", "entryType": "<namespace>.<type>" }`. The entry type implements `IPlugin`. |
| `backend` | no | `{ "executable": "<file>.exe", "args": [], "activateOn": "first-call" \| "startup", "acceptsMultipleClients": false }`. |
| `contributions` | no | Declarative contributions, section 4.4. May be absent or empty. |
| `settings` | no | Declarative settings schema, section 8. |
| `permissions` | no | Informational list shown in the consent dialog: `network`, `credentials`, `shell`, `backend-process`. |

Phase 5 limits (parked #28 and #73), for third-party input. `plugin.json` may be
at most 1 MiB (`PluginManifestReader.MaxManifestBytes`, which the installer
shares). A larger one is invalid with `plugin.json is larger than 1 MiB.`;
discovery reads at most the cap plus one byte and never holds it whole. Free-text
strings are bounded, measured in UTF-16 characters. A longer value makes the
manifest invalid with `The <field> field is longer than <n> characters.`:

| Field | Limit |
| --- | --- |
| `name`, `publisher.name` | 100 |
| `description` (optional, a string; not shown yet) | 2000 |
| each contribution `label`, `title` and `group`, and a setting's `label` and `group` | 200 |
| a setting's `description` | 1000 |
| `toolbar[].glyph` | 8 |
| `version`, `commands[].shortcut` (final fix wave) | 64 |
| `publisher.url` | 2048 |
| `ui.entryType`, each `provider` (sidebar, panels, settings page, row decorations) | 256 |
| `archiveFormats[].engineName` | 100 |
| `archiveFormats[].extension` | 32 |
| each enum setting `options` entry (at most 256 entries) | 200 |
| a setting's `default` (a string by its characters, any other value by its JSON text) | 1000 |

The id stays bounded by its grammar, and a `when` expression by its existing
512 characters (section 4.4). An error message repeats at most 80 characters of
an invalid value, then `…` (`PluginManifestReader.Echo`).

### 4.4 Contributions

Everything in `contributions` is rendered from the manifest before any plugin
code loads. Each contribution names the command or provider the host invokes
when the user acts on it, and that first invocation loads the UI assembly or
starts the backend.

| Contribution | Shape | Host slot |
| --- | --- | --- |
| `commands` | `{ id, label, group?, shortcut? }` | Command palette and command routing. Ids are prefixed with the plugin id by the host. `shortcut` is display text in the palette; the host binds no key for it. |
| `contextMenu` | `{ group, items: [{ command, label, when? }] }` | `ContextMenuBuilder` appends plugin groups after the built-in ones. `when` is a small expression over selection facts: `selectionCount`, `singleFile`, `singleFolder`, `hasExtension(ext)`, `hasDecoration` (the selection carries this plugin's row decoration) and `folderFlag(name)` (a per-folder flag the plugin's row decoration provider sets, such as `inRepository`). |
| `toolbar` | `{ command, label, glyph, placement: "primary" \| "overflow", when? }` | Primary toolbar and overflow planner. A `when` expression (the context menu's grammar) hides the button while it is false. |
| `sidebar` | `{ id, title, provider, command? }` | A collapsible section in `SidebarView`. The host draws its header from the manifest: the title, a collapse button and, when `command` names a declared command, an icon button that runs it. The provider implements `ISidebarSectionProvider` and fills the body. `id` follows the settings key grammar and is unique within the plugin. |
| `panels` | `{ id, title, provider, placement: "right" \| "bottom", detachable, command? }` | A dockable side panel next to the file list, with the same host-or-window toggle the Git workbench has today. The provider implements `IPanelProvider`. When `command` names a declared command, that command toggles the panel. |
| `rowDecorations` | `{ provider }` | Per-row text and brush key plus per-folder flags supplied by an `IRowDecorationProvider`, batched per folder. |
| `archiveFormats` | `{ extension, canList, canExtract, canCreate, canModify, engineName }` | Registered with the Rust service as an archive engine served by the plugin backend, section 6.4. |
| `settingsPage` | `{ provider }` | A custom settings control for plugins whose settings exceed the declarative schema. |

`sidebar` and `settingsPage` need a `ui` assembly; the reader rejects them
without one (`contributions.sidebar needs a ui assembly.`). Phase 3 also makes
the reader reject any key under `contributions` it does not know, with
`The contributions.<key> kind is not supported by this host.`, so a typo never
loads as a silently missing slot.

Phase 4 reader rules for the slots it implements:

- `panels` needs a `ui` assembly (`contributions.panels needs a ui assembly.`).
  Each `id` follows the settings key grammar, with the sidebar's message, and is
  unique (`The panel id '<id>' is declared more than once in contributions.panels.`).
  `title` and `provider` are required. `placement` is `right` or `bottom`, and
  defaults to `bottom`; any other value gives
  `The <path>.placement value '<v>' is not recognised; use 'right' or 'bottom'.`
  `detachable` defaults to false. `command` must name a declared command (the
  sidebar's message), and a command toggles at most one panel
  (`The <path>.command value '<c>' already toggles another panel.`).
- `rowDecorations` is one object, `{ provider }`, with `provider` required, and
  needs a `ui` assembly (`contributions.rowDecorations needs a ui assembly.`).
- `toolbar[].when` is validated like a context-menu item's `when`: at most 512
  characters, in the same grammar.
- Every command id, declared (`commands[].id`) or referenced
  (`contextMenu[].items[].command`, `toolbar[].command`), follows the toolbar's
  command grammar (`CommandSurfaceLayout.IsPluginCommand`: 1-128 characters
  with no whitespace), so every plugin command makes an item id a saved toolbar
  keeps; another gives `The <path> value '<c>' is invalid: use 1 to 128
  characters with no whitespace.` A command appears at most once in `toolbar`
  (`The toolbar command '<c>' is declared more than once in
  contributions.toolbar.`), since the catalog keys toolbar actions by command.
- `activateOn: "startup"` stays a `backend` field and starts only the backend. A
  row decoration provider's UI assembly activates on the first decoration
  request, which the host holds until startup is `ready`.

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
  `Progress`, `Dialogs`, `Workspace`, `Logger`, `OnBackendEvent<T>(eventName,
  handler)` for backend events, and `Windows`, `Panels` and `Decorations`
  (below).
- `IPluginSettings`: `GetAsync` and `SetAsync` of string values over the
  plugin's own keys.
- `IPluginProgress`: `Begin(title)` returning an operation handle with
  `Report(current, total, detail)`, `Complete()`, `Fail(message)` and a
  `CancellationToken` the host cancels from the Transfers window.
- `IPluginDialogs`: show a content dialog, pick a folder, confirm.
- `IPluginWorkspace`: read-only view of the active pane path and selection,
  and a `Changed` event. Navigation requests go through
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

Phase 4 shapes, added to SDK `1.0.0` the same way:

- `IPanelProvider.CreatePanel(PluginPanelRequest(PanelId, Provider, Detached))`
  returns `object`, which must be a `Microsoft.UI.Xaml.FrameworkElement`.
- `IRowDecorationProvider.GetDecorationsAsync(PluginDecorationRequest(Provider, FolderPath, Paths), ct)`
  returns a `PluginFolderDecorations(Rows, FolderFlags)`: `Rows` maps a row path
  to a `RowDecoration(Text, BrushKey?, Tooltip?)`, and `FolderFlags` lists the
  folder's flags, such as `inRepository`, which the plugin's `when` expressions
  read with `folderFlag(name)`. `PluginFolderDecorations.Empty` has neither.
- `IPluginHost.Panels` is an `IPluginPanels` with `ShowAsync(panelId, ct)`,
  `Hide(panelId)` and `IsVisible(panelId)`. A host without panels reports no
  panel visible, ignores `Hide`, and fails `ShowAsync` with
  `This host has no panels.`
- `IPluginHost.Decorations` is an `IPluginDecorations` whose `Invalidate()`
  asks the plugin's provider again for the folders on screen.
- `IPluginWorkspace.RefreshAsync(ct)` lists the active folder again.
- `IPluginDialogs.Notify(message, isError)` shows non-modal text in the message
  bar, titled with the plugin's name.

The thread contract, recorded in the SDK's XML docs:

- `ActivateAsync`, `ExecuteAsync`, `CreateSidebarSection`,
  `CreateSettingsPage` and `CreatePanel` run on the UI thread (the host's
  `IPluginUiThread`).
- `GetDecorationsAsync` runs on a thread-pool thread and must not touch WinUI
  objects; the plugin is activated on the UI thread before the first call.
- `DeactivateAsync` runs on a thread-pool thread.
- A provider may be asked again for the same section or page after the host
  dropped the previous element, and must then return a new element (or one with
  no parent). `CreatePanel` is asked once per presentation, docked (`Detached`
  false) and then in the panel's window (`Detached` true), because a WinUI
  element cannot move to another window.
- `IPluginPanels`, `IPluginDecorations`, `IPluginWorkspace.RefreshAsync` and
  `IPluginDialogs.Notify` may be called from any thread. `ShowAsync`, `Hide`,
  `RefreshAsync` and `Notify` are always queued to the UI thread, never run
  inline, so a plugin calling them from a command, a provider or a decoration
  request never re-enters a host pass. `ShowAsync` completes once the panel is
  shown; `IsVisible` returns the state the UI thread last published.
- The host closes attached windows before it stops the backend and deactivates
  the plugin.

Plugin UI projects reference the Windows App SDK compile-only
(`ExcludeAssets="runtime"`, `PrivateAssets="all"`); at run time the WinUI types
come from the host through the shared prefixes of section 5.2. Section 12
records the full set of compile-only packages, which the user has yet to
confirm.

Plugin UI may read these theme resource keys from
`Application.Current.Resources` (or as `{ThemeResource}` in `XamlReader.Load`
strings), which the host keeps stable:
`SfTextPrimaryBrush`, `SfTextMutedBrush`, `SfBgPrimaryBrush`,
`SfBgSecondaryBrush`, `SfBorderBrush`, `SfAccentSoftBrush`, `SfIconFont`,
`SfGhostButtonStyle`, `SfTextBoxStyle`, `SfEmptyHintStyle`,
`SfSidebarListStyle` and `SfNavListItemStyle`. Phase 4 adds `SfAccentBrush`
and the decoration brushes `SfDecorationAddedBrush`,
`SfDecorationModifiedBrush`, `SfDecorationUntrackedBrush` and
`SfDecorationDeletedBrush`, which a `RowDecoration.BrushKey` may name.
`App.xaml` defines the four decoration brushes in each of its theme
dictionaries (`Default`, `Dark`, `Light` and `HighContrast`) with the colours
of the `SfGit*Brush` keys they replace; those went with core Git in phase 4.
Settings > Plugins draws a declarative setting's error text with
`SfDecorationDeletedBrush`.

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
major version. User confirmation: confirmed by the user on 2026-11-09
(keep the `object` hand-offs at `1.0.0`; typed members may be added in a minor
version). Phase 4 (`IPanelProvider`, `IRowDecorationProvider`, the Git UI)
follows the same pattern.

### 5.2 Loading

Each plugin assembly loads in its own non-collectible `AssemblyLoadContext`.
The context resolves `SimpleFile.PluginSdk`, `Microsoft.WindowsAppSDK`,
`CommunityToolkit.Mvvm` and the .NET runtime from the host so types unify. Other
dependencies resolve from the plugin folder.

Phase 3 made the shared set exact. An assembly whose name starts with one of
these prefixes always comes from the host: `SimpleFile.PluginSdk`,
`Microsoft.WindowsAppSDK`, `Microsoft.WindowsAppRuntime`, `Microsoft.UI`,
`Microsoft.WinUI`, `Microsoft.InteractiveExperiences`,
`Microsoft.Graphics.Display`, `Microsoft.Graphics.Imaging`,
`Microsoft.Web.WebView2`, `Microsoft.Security.Authentication.OAuth`,
`Microsoft.Windows` (which covers `Microsoft.Windows.SDK.NET`), `WinRT`,
`CommunityToolkit.Mvvm`, `System.` and `Microsoft.Extensions.`. So a plugin
that references WinUI and the Windows App SDK compile-only gets the host's
projections, and a `Window` or `UIElement` it builds is the host's type.

Final fix wave: `System.` and `Microsoft.Extensions.` captured packages the
host does not ship (a plugin bundling System.Reactive got FileNotFound), and
`Microsoft.Windows` also matched `Microsoft.WindowsAzure.*`. The prefix is now
`Microsoft.Windows.`, and the prefixes come in two kinds
(`PluginLoadContext.Classify`). `System.` and `Microsoft.Extensions.` are
host-first: the host's copy when the default context can provide the name (a
trusted platform assembly, or one already loaded there), otherwise the plugin's
own copy through its `deps.json`; a bundled copy of a name the host has is never
used. Every other prefix (the SDK, the Windows App SDK and WinUI families, the
Windows SDK projection, WinRT, the MVVM Toolkit) stays host-only: a plugin never
loads its own copy. `PluginLoadContextFallbackTests` pins both, and the
`docs/PLUGINS.md` table gained a "Comes from" column the doc test checks.

Phase 5 (parked #17/#22): the prefix was `Microsoft.Graphics`, which also
matched Win2D (`Microsoft.Graphics.Canvas*`), a library the host does not ship.
It is now the two families the host ships, `Microsoft.Graphics.Display` and
`Microsoft.Graphics.Imaging`, so Win2D's managed assemblies load from a plugin's folder (running Win2D from a plugin is not tested end to end)
(`SharedPrefixes_DoNotCaptureWin2D`). `docs/PLUGINS.md` ("Host-shared
assemblies") prints the list and the package versions a plugin is bound to
(`Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`,
`CommunityToolkit.Mvvm`); `PluginSharedAssembliesDocTests` pins the table to
`PluginLoadContext` and the versions to the App's lock file.

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
the bytes it hashes for trust and checks against `signature.json`), reads `bundled.json` and the approvals file once,
and judges every plugin against those snapshots
(`Discover_TenPlugins_StaysUnderTheStartupBudget` pins the ten-plugin case).
At startup the App does those reads on the thread pool while the file service
starts (`PluginManager.PrepareScan`). Phase 3 measured the cold two-plugin
delta at about 36 ms before this and about 12 ms after it. Rescans read afresh.

A limit the budget does not cover (final fix wave). A signed plugin's check
streams every file in its folder through SHA-256 (`signature.json` lists them
all) and enumerates the folder for unlisted files, on every scan. At startup that
runs inside `PrepareScan`, on the thread pool while the file service starts, so
it reaches the span only as `plugins-scan-awaited`, once it outlasts the service
start. A plugin of tens of megabytes costs tens of milliseconds there, roughly a
millisecond per megabyte, which the ten-plugin pin (small, mostly unsigned
plugins, as the bundled ones are) does not exercise. No hash cache was added: the
startup scan is the first in its process, so an in-memory cache would help only
rescans, which run off the UI thread anyway, and a cache keyed on path, length
and write time would let a same-size swap that restores the write time keep its
Signed state for the session.

The budgeted span runs from `MainWindow.Connect.backend-started` to
`MainWindow.Connect.plugins-discovered`. Since the phase 3 review it holds only
this:

- `plugins-begin`: the file-operation service the manager needs (not plugin
  work, but inside the span since phase 1).
- `plugins-manager-built`: the manager and its services.
- `plugins-scan-awaited`: the wait for the prepared scan, normally 0 ms.
  `PrepareScan` now also judges every plugin on the thread pool, signatures
  included.
- `plugins-evaluated`: the hand-over. It completes on the UI thread, with no
  hop to the thread pool and back.
- `plugins-discovered`: one contribution render.

The first four are laps. `StartupTimer.Lap` writes them with the closing mark,
so they add no file write to the span. The budget check
(`check-winui-startup-budget.mjs`, `StartupTimingBudget.PluginDiscoverySpan`)
adds the stages up and prints each one, so a CI log shows which part dominates.
A log from before the split budgets the single `plugins-discovered` delta.
`Changed` is subscribed only after the render, so the scan's own `Changed` no
longer queues a second render into the span. The backends of startup plugins
start (`PluginManager.StartStartupBackends`) only after the mark, so no launch
competes with the span and no `Changed` they raise is missed.

The phase 3 review's evidence. The span was measured with three plugins
(Hello, 7-Zip, remote), in the Debug build and in the `-PluginsDir` smoke's
environment. Each figure is the median of five starts on a Ryzen 9 9950X,
limited to fewer CPUs by affinity:

| CPUs | Before | After |
| --- | --- | --- |
| 32 | 20.6 ms | 15.0 ms |
| 4 | 22.5 ms | 15.6 ms |
| 2 | 25.9 ms | 16.1 ms |
| 1 | 22.9 ms | 15.6 ms |

- After the fix, the stages read 1.5, 1.2, 0.0, 1.5 and about 11 ms, whatever
  the CPU count. Before it, the span grew when CPUs were taken away (up to
  25.9 ms on two). The render's 11 ms
  is first-use cost: JIT and the first code-built WinUI elements. The first
  sidebar section header alone takes about 5 ms, and a warm re-render about
  0.2 ms.
- The hosted CI runner (windows-2025, 4 vCPUs) measured 74.2 ms at `46860dc`,
  as one stage, against the 50 ms budget. The fix has not run on a hosted
  runner yet. The next CI run prints the split.
- The budget stays 50 ms. It is raised or split only on that hosted-runner
  evidence, which is recorded here.
- If the render still dominates there, the next lever is to warm the render's
  first-use cost while the file service starts, as F2 did for the reads.

Phase 4 Task 4 pulled that lever (carry-over h). `ConnectAsync` queues
`MainWindow.WarmPluginRendering()` right after `PreparePluginScan()`, before
its first await. The warm-up runs the contribution adapters on empty lists and
builds and drops one sidebar section header for a placeholder section. It
touches no plugin state, no catalog and not `PluginSectionsHost`. Measured
locally in the same environment (three plugins, Debug build, `-PluginsDir`
smoke environment, 32 CPUs, five starts each, median):

| Build | Span median (range) | `plugins-discovered` stage |
| --- | --- | --- |
| Without the warm-up | 15.0 ms (15.0–16.7) | about 10.8 ms |
| Warm-up queued at `Low` priority | 15.9 ms (15.2–17.4) | about 10.9 ms |
| Warm-up queued at `Normal` priority | 10.4 ms (9.7–10.6) | about 5.5 ms |

- At `Low` priority, the plan's first choice, the warm-up ran only after
  `ready`, about 300 ms after the span had closed, so it could not help. The
  App queues it at `Normal` priority. It then runs while the service starts,
  before the `backend-started` continuation, which is queued after it at the
  same priority. `backend-started` moved from about 80 ms to about 88 ms;
  `ready` stayed at about 300 ms.
- The UI smoke with the Hello fixture, followed by the budget check, printed
  `plugins-discovered span 10.0 ms (budget 50 ms, count=3)`.
- The hosted runner has since retaken the evidence (carry-over h, recorded by
  phase 4 Task 16). On windows-2025 the budget check printed
  `plugins-discovered span 31.7 ms (budget 50 ms, count=3)` at `7a931df`
  (CI run 42727627508), down from 74.2 ms at `46860dc`. The budget stays 50 ms.
- With four plugins (Hello, 7-Zip, remote and Git, now bundled), the budget
  check that follows the `-PluginsDir` UI smoke in the WinUI CI job printed
  `plugins-discovered span 32.9 ms (budget 50 ms, count=4)` at `99b2bca`
  (CI run 42738570057).

Row decorations follow the same startup rule (phase 4 Task 11). The
workspace's `RowDecorationCoordinator` starts with its gate closed, and only the
Low-priority callback that `QueuePluginSectionActivation` queues once startup
marked `ready` opens it (`MarkStartupReady`). Until then no decoration request
reaches a provider, so no decoration-driven plugin activation (an assembly load
and `ActivateAsync`) runs before `plugins-discovered` or `ready`. Right after
opening the gate the same callback starts `InvalidateDecorationsAsync()`, so the
folder shown when SumaFile starts is still decorated: a pane whose listing was
published before the callback is decorated again then, and a startup navigation
still listing (it lists on the thread pool, in parallel with the callback) asked
with the gate closed and sees the changed `Generation` when it publishes. The
legacy settings migration also starts after the mark, right after
`StartStartupBackends()`.

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
plugin files at the `ms-appx:///` root beside `SumaFile.exe`, outside the
plugin's own folder, breaks §1's rule that a plugin is a single folder
(assembly-named folders would also collide between plugins), so phase 4
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
(`<install dir>\plugins-data\<plugin-id>\`, §4.1) as command-line
arguments, and starts the backend with `TMP` and `TEMP` set to that
directory's `tmp` folder. The backend creates the pipe server; the host connects within a
five-second timeout and performs a handshake that exchanges plugin id, plugin
version and SDK version.

The backend exits when its last client disconnects. The host also terminates it
on shutdown and on plugin disable, and kills a backend whose start fails; three
failed starts within 60 s mark the plugin Faulted.

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
| `host.notify` | A message-bar notice titled with the plugin's name. |

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
and progress helpers for Rust backends. It is meant for crates.io with the
SDK. Backends in other languages implement the framing directly; the spec in
`ipc/schema/plugin/v1` documents it.

Packaging (phase 5) is a dry run only; nothing is published. The crate depends
on `simplefile-ipc` by path, so the two are packaged together:
`cargo package --locked -p simplefile-ipc -p sumafile-plugin`, which also
builds each package from its packaged sources against a temporary local
registry (`--no-verify` is never used). The path dependency names
`version = "1.0.4"`, the registry requirement a package carries in place of
the path; a `simplefile-ipc` release bump stays compatible, a new major raises
it. Both manifests carry `readme = "README.md"` (each crate has one),
`license`, `repository`, `keywords`, `categories` and a `rust-version` taken
from the `rust-toolchain.toml` channel (never newer than it). The Rust quality
gate in CI runs the dry run after its tests on the clean checkout;
`npm run pack:crates` is the local form and adds `--allow-dirty`, so it works
in a working tree with edits. `scripts/tests/plugin-crates-package.test.mjs`
pins the metadata, the version requirement, both commands, and that no
workflow or npm script runs `cargo publish` or `dotnet nuget push`.

## 7. Host slots added to the WinUI app

- `ContextMenuBuilder.Build` takes an `IReadOnlyList<PluginMenuContribution>`
  and evaluates `when` expressions against the request. The hard-coded
  `GitMenu` is deleted when Git moves.
- `AppCommandCatalog.All` becomes `AppCommandCatalog.BuiltIn` plus a
  `PluginCommands` list merged at startup.
- `MainWindow.CommandRouting` routes any command id with a plugin prefix to the
  plugin manager, which loads the plugin and calls its `ICommandHandler`.
- `PrimaryToolbarView` and `ToolbarOverflowPlanner` accept plugin buttons.
  The toolbar rule (phase 4 Task 4):
  - A saved toolbar keeps a well-formed `plugin:<id>:<command>` item
    (`CommandSurfaceLayout.IsPluginItemId`: the id follows the plugin id
    grammar, the command follows `IsPluginCommand`, 1-128 characters with no
    whitespace, which the manifest reader enforces) in place while
    its plugin is disabled, failed or missing. `Normalize` still drops unknown
    core ids, such as the retired `git-*` and `ftp-sftp-manager`.
  - `CommandSurfaceLayout.VisibleItems()` is the toolbar as drawn: only the
    items the catalog knows, with no leading, trailing or doubled separators.
    The toolbar rendering, `VisiblePrimaryActionIds()`, `Signature()` (so a
    returning plugin re-renders the toolbar) and the overflow planner use it.
  - A plugin's `primary` item is offered once. `EnsurePrimaryItem` records it
    in `OfferedPrimaryItems` (saved as `offeredPrimaryItems`, deduplicated,
    never filtered by the catalog) and never adds an offered item again, so the
    user's removal is final and a merely hidden item comes back by itself.
  - Settings > Toolbar lists an absent plugin's item with its raw id and the
    detail `Plugin not loaded`, so it can still be removed. The editor carries
    the offers through a save, and Reset clears them, so the enabled plugins
    offer their items again after a reset: once Settings applies its layout,
    MainWindow re-applies the enabled plugins' placements before it rebuilds
    the toolbar, and the Settings save writes them.
  - `WorkspaceSettingsStore.SaveAsync` computes every value it writes on the
    caller's thread before its first await, and the sidebar's collapse toggle
    replaces `CollapsedPluginSidebarSections` instead of editing it, so a toggle
    or an offer that lands during a save is neither lost nor read half-way.
    That needs the save to start on the UI thread, which owns `UiSettings`:
    every App caller is there, and the profile apply
    (`ApplyWorkspaceProfileAsync`) keeps the caller's context up to its save.
- Plugin body loads are queued, never run inline (phase 4 Task 4, carry-over
  #82). `ActivateVisiblePluginSections` only asks `PluginBodyLoadQueue` (Core)
  for a load, keyed `sidebar:<section key>` (`PluginSidebarLayout.BodyLoadKey`;
  panels use `panel:`), and the queue runs it later at low dispatcher priority,
  at most once per waiting key. `IPluginWorkspace.NavigateAsync` always queues
  to the UI thread (`PluginUiDispatch.EnqueueAsync`), so a plugin can never
  re-enter a workspace sync inline.
- A load failure (phase 4 Task 4, carry-overs #63, #65, #69) sets LoadFailed
  and raises `Changed` at once, and the caller gets its `PluginLoadException`
  at once. The teardown (windows, backend, deactivation) then runs as a task
  the manager tracks per plugin id. Rescan, Enable, Disable, Restart and
  Dispose wait for it, and so does every backend start of that plugin (a
  failure during a scan's read comes after the scan's own wait), bounded by
  the sum of the teardown's own bounds (`WindowCloseTimeout`, the archive
  engine unregister, the old backend's exit and the runtime's retire timeouts)
  and logged past that, before they create a runtime or start a backend. So a
  new backend never starts while the old one still owns the plugin's pipe.
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
- Plugin locations: `PluginDiscovery.DataRoot()`, `PluginDiscovery.LogPath()`
  and `PluginApprovalStore.DefaultPath()` resolve to
  `<AppContext.BaseDirectory>\plugins-data`, `plugins.log` and
  `plugin-approvals.json` (§4.1), or to `SUMAFILE_PLUGIN_DATA_ROOT`,
  `SUMAFILE_PLUGIN_LOG_PATH` and `SUMAFILE_PLUGIN_APPROVALS_PATH` when set, and
  `plugins.log` gets one `[warn]` line for each override in effect. Every UI
  smoke mode, plain included, points all three into its disposable test root,
  so it never writes into the Debug exe folder, which is its install folder.
  `SmokePluginsAsync` asserts that all three lie under the test root, that no
  plugin root lies under `%LOCALAPPDATA%\SumaFile`, and that every plugin whose
  backend runs has its `<data root>\<id>\tmp` folder. The script snapshots the
  pre-release plugin locations under `%LOCALAPPDATA%\SumaFile`
  (`Get-LegacyPluginLocationSnapshot`) and the Debug exe folder's app-owned
  plugin files (`Get-AppOwnedPluginState`) before the launch and fails when
  either changed.
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
  DPI-aware size, the app theme).
  Phase 5 (parked #72/#92): the host checks the options before it tracks the
  window. `PluginWindowOptionsPolicy.Normalize` clamps the width to 320-3840 and
  the height to 240-2160 DIPs, replaces a null, empty or whitespace title with
  the plugin's manifest name, and cuts titles to 120 characters (never inside a
  surrogate pair). The manager hands every runtime's host to the plugin through
  `PluginWindowOptionsGuard`, which normalizes with the manifest name, so every
  `IPluginWindowHost` sees checked options; `PluginWindowHost.Attach` normalizes
  again (idempotent) and, first of all, throws
  `InvalidOperationException("Attach must be called on the UI thread.")` off the
  UI thread, before anything is tracked. `SecondaryWindowChrome.Resize`
  saturates its DIP-to-pixel conversion, and the clamped sizes times any DPI
  scale Windows offers stay far inside `int`. When the main window closes, `OnClosed`
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
  Open dialogs on a closing plugin window are hidden first; the gate is
  released even if WinUI never completes their `ShowAsync`. `PluginWindowHost`
  calls `DialogHost.HideDialogsOn(window.Content?.XamlRoot)` before every
  `window.Close()` (Disable, rescan, load failure and main-window close all
  go through it), and `DialogHost.ShowAsync` returns `None` when the host hid
  its dialog. WinUI does not complete a dialog's `ShowAsync` when its window
  closes, so without this the app-wide dialog gate stayed held.
  The same holds for a plugin window closed by the user (title-bar X, Alt+F4)
  or by the plugin itself: the window host's `Closed` handler, attached in
  `Attach`, also calls `DialogHost.HideDialogsOn` on the window's `XamlRoot`
  before it untracks the window, so any close releases the gate.
- `MainWindow.xaml` replaces the inline `GitPanel` with a generic `PluginPanelHost`
  that supports one visible panel at a time, host-or-window toggle, and the
  same resize grip. `GitWorkbenchWindow` becomes a generic `PluginPanelWindow`.
  A panel's body comes from `PluginManager.CreatePanelAsync(pluginId, panelId,
  detached, ct)` (phase 4 Task 8), which activates the plugin on the UI thread
  through the same path as commands and sidebar sections and then calls
  `IPanelProvider.CreatePanel(new PluginPanelRequest(id, provider, detached))`
  there, once per presentation: docked (`Detached` false), then detached into
  the panel window (`Detached` true). A panel the manifest does not declare
  throws `InvalidOperationException` (`Plugin '<id>' has no panel '<panelId>'.`)
  and changes nothing. An entry type without `IPanelProvider`, or a provider
  that throws, returns null or returns something `ValidateUiElement` rejects,
  marks the plugin LoadFailed and withdraws its contributions, with
  `Plugin '<id>' panel '<panelId>' …` messages as for sidebar sections.
  The App side, as built (phase 4 Task 10):
  - **Naming.** `PluginPanelHost` is not a class. It is two regions of
    `MainWindow.xaml`, driven by `MainWindow.PluginPanels.cs`:
    `PluginPanelBottomRegion`, in a fourth (`Auto`) row of `FilePaneShell`
    below the panes, and `PluginPanelRightRegion`, the third column of
    `PanesAndPanelGrid` (`*`, `0`, `0`), which wraps `PanesGrid`.
  - **Host chrome.** Built in code from the manifest, so no plugin code runs
    until the body is needed: a `Border` with AutomationId
    `PluginPanel.<pluginId>.<panelId>` whose header holds the title, a detach
    button (`.Detach`, named `Detach <title>`, only when `detachable`) and a
    close button (`.Close`, named `Close <title>`), above a `ContentPresenter`
    (`.Body`).
  - **One visible panel.** Showing a panel hides any other, docked or
    detached. Spec §7's sizes apply: the bottom region's `RowResizeGrip`
    (north-south cursor, `PluginPanel.BottomGrip`) and the right region's
    `PaneResizeGrip` (`PluginPanel.RightGrip`) clamp through
    `PluginPanelLayout`, write `UiSettings`, and save on release; a lost
    capture or a cancelled pointer ends the drag as a release does. A
    double-tap resets the default. `ResizePluginPanelBy(delta)` is the one
    resize path, for the drag and the UI smoke.
  - **Detach and dock.** Detach drops the docked body and opens a host-owned
    `PluginPanelWindow` (code-built, `SecondaryWindowChrome`, 1180 x 740,
    AutomationId `PluginPanelWindow.<pluginId>.<panelId>`, header buttons
    `Dock <title>` and `Close <title>`). The plugin is then asked again with
    `Detached` true. Dock closes the window and asks again with `Detached`
    false. Closing the window any other way hides the panel, so a panel shown
    again opens docked.
  - **Toggle semantics.** A panel's `command` run from the palette, a menu or
    `RunAppCommandAsync` shows the panel. Its generated toolbar button
    toggles it and is drawn as a toggle (`Hide <title>` / `Show <title>`).
    `ConfigureToolbarButton` draws that toggle last, so every toolbar
    rebuild and chrome refresh keeps it, whether or not the overflow set
    changed. The plugin's `ICommandHandler` never receives a panel command.
  - **Body.** Every body load goes through the `PluginBodyLoadQueue` under
    `panel:<key>` at low priority, never inline, so a panel's first
    activation never nests inside a host pass (#82). A failure shows
    `This panel could not be loaded: <message>` in the body. The queue only
    drops a request while its key waits, so the chrome also marks the
    presentation whose load is running (`LoadingPresentation`), and
    `PluginPanelLayout.NeedsBodyLoad` asks for a body only when there is
    none and no load runs for this presentation. A show during a slow first
    activation therefore never calls `CreatePanel` twice. A hidden panel
    keeps no element: hiding, detaching and docking drop the body, and a
    body that arrives after its presentation ended is dropped.
  - **Contribution changes.** The contribution render only records the
    wanted panels (`SyncPluginPanels`, which builds no element and so adds
    nothing to the startup span) and hides the shown panel when
    `PluginPanelLayout.StillWanted` drops it: its plugin was disabled or
    failed, or its runtime was retired (a new epoch).
  - **Teardown.** The panel window is tracked by the plugin's window host
    (`PluginWindowHost.TrackHostWindow`: the same window set and seal as
    `Attach`, without chrome), so Disable, rescan, load failure and the main
    window's close close it first, and a dialog open on it is hidden and
    releases the gate. A window opened before the plugin's first activation
    created its runtime is tracked once the body load created it.
  - **`IPluginHost.Panels`** (`PluginPanelsService`, one per runtime through
    `PanelsFor`) is callable from any thread. `ShowAsync` is always queued
    with `PluginUiDispatch.EnqueueAsync`, never inline, and completes once
    the panel is shown; it fails with `Plugin '<id>' has no panel '<panelId>'.`
    for a panel that is not declared or not enabled. `Hide` is queued with
    `TryEnqueue` and does nothing when another panel is shown. `IsVisible`
    reads a volatile snapshot that the UI thread updates whenever a panel is
    shown or hidden.
- `FileRowView` reads `Row.DecorationText` and `Row.DecorationBrushKey`
  instead of `Row.GitText`. `ExplorerWorkspace` asks every registered
  `IRowDecorationProvider` for decorations once per folder sync, off the UI
  thread, with a 250 ms budget after which the folder renders undecorated and
  updates when results arrive.
  The host's rules (phase 4 Task 9, `SimpleFile.Core`'s `RowDecorationCoordinator`,
  `RowDecorations.cs` and `DecorationBrushKeys`):
  - **Wait before publish.** A navigation or refresh asks for decorations after
    its listing arrives and waits at most 250 ms
    (`RowDecorationCoordinator.DefaultBudget`) before it publishes the listing
    with what answered. The progressive chunks published while the folder
    streams never wait. A result that arrives after the budget is merged with
    the rest and applied once, when it arrives.
  - **Thread pool only.** Every call into the decoration source starts with
    `Task.Run`, never inline on the caller's thread, whoever the caller is: the
    navigation hook, `ExplorerWorkspace.InvalidateDecorationsAsync`,
    `IPluginDecorations.Invalidate` or `WriteSettingAsync`. So a provider's first
    activation is always queued to the UI thread by the manager and never nests
    inside a contribution pass or a workspace sync (#82). A source-shape test
    pins it.
  - **Startup gate and re-decoration.** Before `MarkStartupReady` the coordinator
    asks nothing and returns no decorations. Its `Generation` changes when
    startup becomes ready and when the set of decorating plugins changes. A
    listing whose decorations were asked before either change, but that is
    published after it, is decorated again right after it is published, without
    listing it again. When startup becomes ready the App calls
    `InvalidateDecorationsAsync()`, which decorates every shown pane whose
    listing is published; a pane still listing is skipped, because its own
    publish checks `Generation`.
  - **Newer requests win.** Each pane numbers its decoration requests. A result
    applies only while the pane still shows the folder of the navigation that
    asked, and only when no newer request's result was applied, whatever order
    results arrive in; a request's late merge supersedes its own budget-cut
    result. A newer request cancels the older one's token; a request cancelled
    before its result is handed back gets an incomplete result, in which every
    plugin without an answer is pending, never a complete one. An invalidation,
    and a same-folder re-list (the refresh after a rename, delete or create),
    applies its immediate result at once: each plugin that answered within the
    budget (or answered nothing) replaces its part, a plugin that left the set
    loses its part, and each plugin still pending keeps its shown part, so a slow
    provider never blanks badges and folder flags in between. Once the pending
    plugins settle, the final merge always follows unless the request was
    cancelled, even when every late plugin answered null or threw, so an
    invalidation always converges and never pins stale decorations or flags.
    The caveat: the late merge waits at most `DefaultLateLimit` (30 s from the
    request). A plugin still unsettled then is logged and stays pending in that
    merge, so it keeps its last part (the other plugins' late answers still
    apply), and its answer after the limit is dropped; a provider that never
    settles therefore converges only at the folder's next request. Swapping the
    panes starts new navigation tokens, which drop the late merges both panes
    waited for, so a swap decorates both shown panes again.
  - **Whatever the column layout.** Decorations are asked on every folder sync,
    whether or not the status column is shown, because they also carry folder
    flags such as `inRepository` for menus and the toolbar.
  - **Skipped locations.** The host never asks about network folders or the
    Recycle Bin.
  - **Precedence.** The first decorating plugin in id order owns a row's text;
    every plugin that decorates a row counts for `hasDecoration`. Folder flags
    are `<pluginId>:<flag>`; a flag outside `^[A-Za-z][A-Za-z0-9_-]{0,63}$`, or
    under another plugin's prefix, is dropped. A source that throws or returns
    null is skipped and logged as `[warn] Row decorations from <id> failed:
    <message>`; a cancelled one is skipped silently.
  - **Key matching.** `RowDecorationKeys` is the one rule: separators become
    `\`, a trailing separator is dropped unless the path is a drive root, and
    the comparison ignores case. It applies both when the host filters a
    provider's keys and when a row looks up its decoration, so git's
    `R:/repo/Dir/A.TXT` and `R:\repo\untracked\` land on `R:\repo\dir\a.txt` and
    `R:\repo\untracked`.
  - **The status column.** It keeps the id `git`
    (`ColumnLayout.DecorationColumnId`), so saved layouts, presets and sort
    settings still load; its header becomes "Status". It shows and sorts by the
    decoration text, as the plugin gave it, with no `Git:` prefix.
  - **Brushes.** A decoration's brush key is drawn only when
    `DecorationBrushKeys.Allowed` lists it (`SfDecoration{Added,Modified,
    Untracked,Deleted}Brush`, `SfTextPrimaryBrush`, `SfTextMutedBrush`,
    `SfAccentBrush`); any other key falls back to `SfTextMutedBrush`.
  The App side, as built (phase 4 Task 11):
  - **Source.** `PluginRowDecorationSource` (PluginHost) is the coordinator's
    `IRowDecorationSource`: one `PluginManager.GetRowDecorationsAsync` call per
    plugin and folder, with the provider the manifest names. It maps each
    `RowDecoration` to a `RowDecorationEntry`, prefixes each folder flag with
    `<pluginId>:`, and keeps a null result null. Its key filter keeps a
    returned key only when it matches a requested path under
    `RowDecorationKeys.Comparer`, the rule the row lookup uses, and stores the
    entry under the requested path's spelling; of two returned keys for one
    path the first wins. So the filter, which runs before the merge, never
    drops git's `R:/repo/Dir/A.TXT` or `R:\repo\untracked\`.
  - **Wiring.** `ApplyPluginContributions` and `OnPluginWorkspaceInitialized`
    call `ApplyRowDecorationSource()`, a no-op until the workspace exists. It
    sets a new source over `PluginContributionAdapter.RowDecorationProviders`
    and, when `SetSource` reports a changed plugin set, starts
    `InvalidateDecorationsAsync()` without awaiting it. It only queues: it
    awaits nothing and calls no source (#82). `DisposePluginsAsync` sets no
    source. `PluginManagerOptions.InvalidateDecorations`, which
    `IPluginDecorations.Invalidate` and `WriteSettingAsync` call, starts
    `InvalidateDecorationsAsync()` the same way.
  - **Rows.** `FileRow` carries `DecorationText`, `DecorationBrushKey`,
    `DecorationTooltip` and `DecorationPlugins`, filled from the pane's
    `Decorations` when the row is built and compared when rows are replaced.
    The status column (`git`) and the row's secondary text show
    `DecorationText`; both are drawn with
    `ThemeResourceLookup.Brush(row, DecorationBrushKeys.Normalize(key))`, so the
    brush follows the row's own theme, and the status cell's tooltip is the
    decoration's tooltip, else its text. The status column is available when a
    decoration provider is set (`RowDecorations.HasProviders`), no longer with
    the legacy Git switch. `Row.GitText` stays, unrendered, until core Git is
    removed.
  - **Menus.** The context menu request takes `FolderFlags` and
    `SelectionDecorationPlugins` from the active pane's `Decorations`
    (`PluginsFor` over the selected paths). Building a menu never asks a
    provider.
  - **Toolbar `when`.** A plugin toolbar button whose `when` is false for the
    active pane is hidden; the request is the menu's, without the shell
    queries (Windows clipboard, Open with) no `when` reads. A panel's toggle
    stays visible while its panel is shown, so it can always be hidden. The
    visibility is applied after every workspace sync, and every decoration
    change raises one, and again whenever a panel is shown or hidden.
  - **Settings.** Settings > Plugins writes each declarative setting through
    `PluginManager.WriteSettingAsync`, so a decorating plugin's setting
    re-decorates the shown folders at once. Each declarative control has the
    AutomationId `PluginSetting.<pluginId>.<key>`
    (`PluginSettingsPanelBuilder.SettingAutomationId`); for a `path` setting it
    is the text box.
  Panel layout rules live in `PluginPanelLayout` (Core): a docked bottom panel is
  200 px to 70% of the available height (default 320), a docked right panel 260
  px to 60% of the available width (default 360), a detached panel window is
  1180 x 740, and the sizes persist as `pluginPanel.bottomHeight` and
  `pluginPanel.rightWidth`. A panel is keyed `<pluginId>:<panelId>:<epoch>`, its
  body load `panel:<key>`, and its state `<pluginId>:<panelId>`.
  Each provider call goes through `PluginManager.GetRowDecorationsAsync(pluginId,
  request, ct)` (phase 4 Task 8). It activates the plugin on the UI thread
  through `PluginManagerOptions.UiThread`; its one caller, the decoration
  coordinator, calls it from the thread pool, so the activation is queued and
  never runs inline inside a host pass (#82). It then runs
  `IRowDecorationProvider.GetDecorationsAsync` on the thread pool
  (`Task.Run`). A request for a provider the manifest does not declare, or for
  a plugin that is not Enabled, throws `InvalidOperationException`; an entry
  type without `IRowDecorationProvider` marks the plugin LoadFailed. A
  provider that throws or returns null gives null and one
  `[warn] Row decorations failed: <message>` line in `plugins.log`, and the
  plugin stays Enabled (§10). A call the caller cancels gives null with no log
  line, because the host cancels every superseded navigation: it returns at
  the cancel even when the provider ignores its token, and whatever the
  provider throws after the cancel is observed but not logged. For the same
  reason the caller's token never reaches `IPlugin.ActivateAsync` on this
  path: the activation runs under `CancellationToken.None` and the caller's
  token only stops that caller's wait. A cancel during activation therefore
  never leaves a half-activated instance behind: the activation finishes, the
  runtime keeps the one instance (the next call reuses it, and disable,
  rescan or dispose deactivate it), and an activation that fails after its
  caller stopped waiting still marks the plugin LoadFailed.
  `IPluginHost.Panels` is the App's panel service for that plugin
  (`PluginManagerOptions.PanelsFor`), or, on a host without panels, a service
  whose `IsVisible` is false, whose `Hide` does nothing and whose `ShowAsync`
  fails with `This host has no panels.` `IPluginHost.Decorations.Invalidate()`
  hands the plugin id to `PluginManagerOptions.InvalidateDecorations`.
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

A setting may also name `migrateFrom`, a SumaFile settings key whose value the
host copies into the setting once, while the setting has no value of its own
(phase 4: the Git plugin's `enabled` from `enableGitIntegration`). The key
matches `^[A-Za-z][A-Za-z0-9._-]{0,127}$` and must not start with `plugin.`,
so a plugin can never read another plugin's settings this way. The reader's
errors are `The <path>.migrateFrom value '<v>' is not a valid settings key.`
and `The <path>.migrateFrom value '<v>' must not name a plugin setting.` The
host honours `migrateFrom` only for plugins whose id `bundled.json` lists; for
any other plugin the field is read and ignored.

`PluginManager.MigrateLegacySettingsAsync` (phase 4 Task 8) runs the
migration. For every record whose id `bundled.json` lists and every setting
with `migrateFrom`, it reads `plugin.<id>.<key>` and the legacy key, all of
them in one `GetSettingsAsync`. When the plugin's value is unset or blank and
the legacy value passes `PluginSettingsSchema.ValidateValue` (so `maybe` is
never copied into a boolean), it writes the value once and logs
`[info] Migrated setting <key> from <legacy>`. It never writes or deletes the
legacy key, and it never replaces a value the plugin setting already has, so a
choice the user makes later is kept. It returns the number of settings copied.
Every plugin activation (commands, sidebar sections, settings pages, panels,
decorations) waits for the latest migration, at most 5 s from its start; past
that the host logs
`[warn] The legacy settings migration is still running after 5 s; activating plugins anyway.`
once and activates. A failed migration is logged
(`[warn] The legacy settings migration failed: <message>`) and does not block
activation.

Settings > Plugins writes a declarative setting through
`PluginManager.WriteSettingAsync(pluginId, storageKey, value, ct)`. The key
must be `plugin.<pluginId>.<key>` for a key the plugin's manifest declares and
the value must be valid for that setting, else `ArgumentException` and nothing
is written. After the write it calls
`PluginManagerOptions.InvalidateDecorations(pluginId)`, so a setting such as
Git's `enabled` re-decorates the shown folders at once. It loads no plugin
code.

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
from file, Rescan, Open plugins folder (`<install dir>\plugins`), and
per-plugin Enable, Disable and Remove. Remove is offered only for a plugin in
`<install dir>\plugins` whose id `bundled.json` does not list
(`PluginPackageInstaller.IsRemovable`). After the confirmation
`Remove <name>? Its folder in SumaFile's plugins folder is deleted. Its settings and data are kept.`
it deletes that folder only, and keeps the plugin's settings and
`plugins-data\<id>`. A bundled plugin is disabled, never removed
(`<name> ships with SumaFile and cannot be removed. Disable it instead.`).

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
  for bundled trust. A plugin the user installed in the install root, and
  lists in any other root, always need consent: trust is keyed on
  `bundled.json`, never on location. A listed id whose manifest hash no longer
  matches needs consent too, but stays a bundled id, so Settings never offers
  to remove it. This replaces "the
  installer writing their manifest hashes to the approval store", which NSIS,
  MSI admin installs and the portable zip cannot do reliably.
- **Approval store.** `<install dir>\plugin-approvals.json` (§4.1), or
  `SUMAFILE_PLUGIN_APPROVALS_PATH` when set. Enable writes it before it
  changes any state, so a write that fails (an unwritable install folder)
  leaves the plugin as it was. A read
  that fails (the file is held by another process) is retried three times,
  50 ms apart. If it still fails, lookups treat the store as empty, but
  enabling or disabling a plugin fails with "Could not read the plugin
  approvals file: …" and saves nothing, so other plugins' records are never
  lost. A bundled plugin scanned while the store cannot be read is
  NeedsConsent, not Enabled, because a Disabled the user stored cannot be
  seen; the next scan with a readable store restores its state. Saves go through a temp file unique to the process and call, which is
  removed when the final move fails.
- **Signature.** `signature.json` holds an ed25519 signature over
  `plugin.json` plus the SHA-256 of every file listed in a `files` object. The
  host verifies it with the publisher's `publicKey` using an Ed25519
  verifier in the host (BouncyCastle) with the same raw 32-byte base64 public
  key format the updater uses, and shows "Signed · key <fingerprint>". The
  key comes from the plugin's own `plugin.json`, so a good signature proves
  the files match that key, not who the publisher is; the final fix wave
  dropped the earlier "Verified publisher" wording for that reason. Unsigned
  or mismatched plugins show a warning but can still be enabled. Signed means
  every file: a regular file anywhere in the folder (hidden ones included)
  that `files` does not list, other than the root `plugin.json` and
  `signature.json`, or any link to a folder, makes the state Invalid with
  `"<path>" is in the plugin folder but not listed in signature.json files.`
  (or `"<first>" and <n> other files in the plugin folder are not listed in
  signature.json files.`). That check runs last, after the signature itself
  verified, so the state keeps the key fingerprint. `signature.json` is read
  bounded at 1 MiB (`signature.json is larger than 1 MiB.`), a leading UTF-8
  byte order mark is skipped, and listed files are hashed as streams.
- **Signing tool (phase 5).** `scripts/sign-plugin.mjs` (`npm run sign:plugin`,
  `node:crypto` Ed25519) writes exactly what `PluginSignatureVerifier` checks:
  - `keygen --out <dir>` writes `signing-key.pem` (PKCS#8, mode 0600 where the
    OS has modes, never overwritten, refused inside a git work tree) and prints
    the raw 32-byte base64 key for `publisher.publicKey` and its fingerprint
    (the first 16 hex characters of its SHA-256);
  - `sign <folder> --key <pem>` refuses a key that does not match
    `publisher.publicKey`, a declared `ui.assembly` or `backend.executable`
    missing from the folder, a symbolic link, a path the host cannot open and
    two paths that differ only in case. It lists every file except `plugin.json`
    and `signature.json` as a bare forward-slash relative path with its
    lowercase SHA-256 hex, and signs the canonical payload (the `plugin.json`
    bytes, `"\n"`, then `"<path>\n<hash>\n"` per file in ordinal path order);
    `signature.json` is `{ "algorithm": "ed25519", "files": { ... },
    "signature": "<base64 of 64 bytes>" }`;
  - `verify <folder>` repeats the host's checks in the host's order and exits
    0 only for Verified; since the final fix wave a file `signature.json`
    does not cover makes it Invalid there too, as in the host.
  The private key never enters a repository. The committed fixture
  `SimpleFile.Tests/Fixtures/signed-plugin` was signed once with a throwaway
  key that was then deleted; only its public key is kept, and every file in it
  is one line so a Windows checkout hashes the same bytes. Both the node tests
  and `PluginTrustTests.SignedFixture_FromTheSigningTool_Verifies` verify it.
- **Change detection.** The approval store records the manifest hash and
  signature state. When either changes, the plugin returns to the consent
  dialog. The hash covers only `plugin.json`, so a package with the same
  manifest and other code would match an approval: Install from file (also
  over an installed copy) and Remove clear the plugin's stored approval
  (`PluginManager.InstallPackageAsync`, `RemovePackageAsync`; the installer
  calls back once the package is staged, before the folder changes, and an
  approvals file that cannot be written fails the install and keeps the old
  version), so the next scan asks again. A folder copied in by hand is not
  covered: its approval still keys on the manifest alone.
- **Backend pipe identity (final fix wave).** The host connects to a
  backend's pipe with `PipeOptions.CurrentUserOnly`, and on Windows requires
  `GetNamedPipeServerProcessId` to name the process it launched; otherwise
  the start fails with "The pipe of plugin '<id>' is served by process <n>,
  not by the backend SumaFile started (process <m>); SumaFile did not
  connect to it." So a backend must serve its pipe itself, not from a child
  process.
- **Compatibility.** A `sdkVersion` major the host does not support, or a
  minor above the host SDK's, lists the plugin as Incompatible and never
  loads it.

## 10. Failure handling

| Failure | Behaviour |
| --- | --- |
| Manifest invalid | Listed in Settings with the parse error. Nothing loads. |
| UI assembly fails to load or `ActivateAsync` throws | Listed as Load failed with the exception message. Contributions are removed. Rest of the app unaffected. |
| Backend fails to start or handshake | The invoking command fails with a message-bar error. The plugin is marked Faulted after three failures within a minute. |
| Backend exits unexpectedly | In-flight calls fail with a plugin-faulted error. Status center offers Restart. Archive engines are unregistered from the service until restart; the host raises its change notification again once that unregister has finished, so the archive capabilities never keep a lost engine. If the file service itself restarts, the host registers every live engine with the new service in the background, with the same bounded attempts as below; the reconnect does not wait for it. |
| Backend call times out | The host sets no deadline on `InvokeBackendAsync` (its token only ends the wait), and the service applies the archive-engine deadlines of section 6.4. Registering an archive engine with the service makes up to 3 attempts of 15 s each, 1 s and then 3 s apart. After the third failure the plugin is Faulted, its backend is stopped and its engines are unregistered once more, unless a restart has registered a newer backend meanwhile. |
| Row decoration provider is slow | Folder renders undecorated after 250 ms; decorations apply when they arrive. |
| Row decoration provider throws (its own timeout included, such as `git <name> timed out after <n> seconds`) or returns null | Logged as `[warn] Row decorations failed: <message>`; the host's 250 ms budget only stops waiting and logs nothing. The folder shows no decorations from that plugin for that request. Not a load failure: the plugin stays Enabled and is asked again on the next folder sync. A request the host cancels is not logged. |
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
- Profiles and credentials (phase 3 decision). Profiles: the backend keeps them in `<data dir>\remote-profiles.db`, which is `<install dir>\plugins-data\com.sumafile.remote\remote-profiles.db` (§4.1) (table `remote_profiles`, the 1.0.4 schema). On first use it imports, read-only, every row of the `remote_profiles` table in the metadata.db the 1.0.4 service used (resolved as the service does: `SIMPLEFILE_METADATA_DB`, else the first existing `metadata.db` under `SIMPLEFILE_APP_DATA_DIR`, `%APPDATA%\com.simplefile.desktop`, `%APPDATA%\SumaFile`, `%APPDATA%\SimpleFile`, `%LOCALAPPDATA%\SumaFile`, `%LOCALAPPDATA%\com.simplefile.desktop`, `%LOCALAPPDATA%\SimpleFile`), skips ids it already has, and records per legacy database that it ran, so a run against another database (a test's or a smoke's) never blocks the real import. It never writes the old table, which stays for a downgrade. Credentials are read in place: the plugin keeps the `SumaFile.Remote.` prefix, `CRED_TYPE_GENERIC`, `CRED_PERSIST_LOCAL_MACHINE`, the UTF-8 `kind\0value` blob and each profile's `credential_target`, so saved passwords and passphrases keep working with no migration. Deleting a profile deletes its credential. The backend reads, writes and deletes only targets that start with `SumaFile.Remote.` (phase 4 fix): a 1.0.1/1.0.2 row imported verbatim can name another application's Credential Manager entry, so for such a target a read finds nothing (the connect asks for the password), a delete leaves the entry and logs a warning, and a save with a secret is refused, as a new save naming a foreign target always was.
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
  Phase 4 review decision: unless `checkMode` is `checksum`, an upload proves
  its local source unchanged by size, write and creation times and file id.
  A same-length rewrite within one file-time tick (about 15 ms on Windows) is
  not detected, as in 1.0.4 and the local copy engine; hashing every upload
  would read each source twice. `checksum` re-reads the content.
- Window lifetime: the plugin owns at most one manager window, shared by the
  command, the sidebar rows and the settings page; opening it again activates
  it (a sidebar row also selects its profile while no session is open).
  Those entry points run in WinUI event handlers, where an exception would
  end the App, so opening never throws: a failure is logged and shown as a
  message, and a window that failed to attach is closed and not kept. The
  window attaches itself through `IPluginHost.Windows.Attach` with
  `FTP/SFTP manager`, 1180 x 680. Closing it cancels its running transfer and
  disconnects its session once the transfer has stopped
  (`RemoteManagerViewModel.CloseSessionAsync`, at most 10 s, retrying a busy
  session every 200 ms; a session still busy then ends with the backend). The
  host closes it before it stops the backend;
  `DeactivateAsync` closes a straggler on the window's own dispatcher and ends
  the plugin's backend event subscriptions.
- Core removes 13 remote IPC methods, the `RemoteProviderFactory` trait, the
  remote session registry and the remote secret store. `domainMethodCount`
  drops to 86 after the two archive-engine methods are added.
  Done in phase 3 (Task 14): `crates/simplefile-core/src/remote/`,
  `crates/simplefile-service/src/remote/`, their tests and the service's
  `russh`, `russh-sftp` and `suppaftp` dependencies are gone; `ipc/schema/v1`
  has no `remote_*` method and no `Remote*` type, and `check-ipc-schema.mjs`
  and `check-winui-parity-gate.mjs` assert 86. The App has no
  `RemoteManagerWindow`, `MainWindow.Remote.cs`, Connections sidebar section,
  Settings > Tools FTP/SFTP card or remote mode in `TransferOptionsDialog`,
  and Core has no `RemoteManagerViewModel` or `ftp-sftp-manager` command.
  Parity-gate §2.6 tracks the feature by coverage level: SFTP with password
  auth and the 1.0.4 import are automated (`PASS`); FTP, FTPS, SFTP
  private-key auth and SFTP agent auth stay `MANUAL`.
- UX changes from the move (phase 3):
  - The Connections section moves below the built-in sidebar sections.
  - The Settings > Tools FTP/SFTP card becomes the plugin's page under
    Settings > Plugins.
  - Pane More > Tools no longer lists FTP/SFTP; it stays in the palette and
    the toolbar overflow.
  - A saved custom primary toolbar that held `ftp-sftp-manager` loses that
    button. The 1.0.4 default toolbar still upgrades to the current default:
    `CommandSurfaceLayout` matches the old default with and without that id,
    because `Normalize` drops ids the catalog no longer knows.
  - Remote transfers now also appear in the Transfers window. On their rows,
    as on the 7-Zip archive rows, the per-task Options button is disabled;
    remote transfer options live in the manager's Transfer options dialog and
    in Settings > Plugins.
  - Remote sessions survive a file-service restart, and end with the plugin
    backend.
  - The Settings window and the main window share one manager window.
  - No keyboard shortcut is lost: `ftp-sftp-manager` never had a default
    shortcut, `NormalizeOverrides` drops unknown ids, and plugin commands are
    not key-bound.
  - After a remote download, the file service no longer invalidates its
    listing cache for the destination; the folder watcher, the directory
    stamp and the 10 s cache age bound the staleness (only an in-place
    download can show stale sizes, for up to 10 s).
- Without the plugin, SumaFile shows no remote UI and the sidebar has no
  Remote profiles section.

### 11.3 Git plugin, `com.sumafile.git`

- Folder: `plugin.json`, `SumaFile.Git.dll`, `SumaFile.Git.Model.dll`,
  `SumaFile.Git.deps.json` and `sumafile-git-backend.exe` (phase 4, as phase 3
  did for remote: the WinUI-free model assembly holds the view model, the
  controller, the backend client and the decoration provider, so their tests
  run in `SimpleFile.Tests`; the deps file lets the plugin's load context
  resolve it from the plugin folder).
- Backend: `simplefile-core/src/git.rs` as its own process,
  `sumafile-git-backend` (`plugins/git/backend`), serving the ten methods of
  `plugins/git/protocol.json`. Its status cache moves with it. Phase 4 fixes
  the Windows path defects and settles these rules:
  - **Executable.** `git.exe` is resolved once, from absolute `PATH` entries
    only; relative entries are skipped. Without it every method except
    `repo.decorations` fails with `Git was not found. Install Git for Windows
    or add git.exe to PATH.` and spawns nothing.
  - **Read-only queries** (rev-parse, status and the diff preview, which
    run on whatever repository the user browses) run no program the
    repository's config names. Every query runs `git --no-optional-locks
    --literal-pathspecs -c core.fsmonitor=false -c core.hooksPath=<data
    dir>\no-hooks-<16 random hex> -C <path> …`; the hooks folder is never
    created, so no hook or fsmonitor program runs (this replaces
    `%TEMP%\sumafile-git-no-hooks`). Status and the diff preview first list
    the config (`git config --list -z`, which runs nothing) and override
    every configured filter driver with `-c filter.<name>.clean= -c
    filter.<name>.smudge= -c filter.<name>.process= -c
    filter.<name>.required=false`, so no clean filter or filter process runs
    whichever attributes file names it (`.gitattributes`,
    `.git/info/attributes`, `core.attributesFile`); a driver name `-c`
    cannot spell (one with `=`) makes the status changeless and the diff
    fail rather than run it. Both pass `--ignore-submodules=dirty`, so git
    starts no `git status` inside a submodule, whose own filters would run:
    a submodule shows as changed only when its checked-out commit moved. The
    diff preview adds `--no-ext-diff --no-textconv --submodule=short`
    against `diff.external`, diff driver commands, textconv and a diff
    inside a submodule. Phase 4 fix decision: `--attr-source=<empty tree>`
    is not used. It still reads `.git/info/attributes`, is fatal in a
    SHA-256 repository, and drops the work tree's `text`/`eol` attributes,
    so a fresh checkout under `* text eol=crlf` would show every file as
    modified; the override works on every Git version, so no version check
    is needed. Path commands (stage, unstage, discard) keep
    `--literal-pathspecs -c core.fsmonitor=false` with hooks and filters on:
    they are the user's own actions, and staging must run clean filters by
    design. Commit, like them, disables only fsmonitor and keeps hooks on.
    Fetch, pull and push add nothing.
    User-facing rule (README, Git and Developer Tools): Git hooks and filters
    still run for the user's own stage, commit, fetch, pull and push, as with
    the git command line; the read-only views (status column, workbench
    status, diff preview) run none.
  - **One job per command.** Each git process is created suspended
    (`CREATE_NO_WINDOW | CREATE_SUSPENDED`), assigned to its own
    `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` job and only then resumed, so
    nothing it starts, such as the real `git.exe` behind Git for Windows'
    `cmd\git.exe` launcher or a remote helper, runs outside the job. A timeout
    (60 s local, 600 s for fetch, pull and push) or a cancel closes the job,
    which ends git and all of its children. The job nests in the host's
    kill-on-close job of the backend.
  - **Paths.** The status `root` and every change's `absolute_path` use `\`;
    the root keeps the caller's spelling (`rev-parse --show-cdup`) and falls
    back to git's `--show-toplevel`. Git counts `--show-cdup` on the resolved
    folder, so through a junction or a symbolic link the rebuilt folder can
    be another one: it is used only when it resolves to the same folder as
    `--show-toplevel` (or, where a path cannot be resolved, holds a `.git`).
    An untracked directory's trailing separator is trimmed. File-level
    commands resolve `.` and `..` lexically before the root check, so
    `<root>\sub\..` is refused as the root and a path that leaves the root
    as outside the repository.
  - **Cache.** One status run per repository root is reused for 1.5 s, keyed
    by root case-insensitively, at most 16 roots. Callers that ask for a
    root while its status runs wait for that run instead of starting one.
    Every command that changes the repository drops its root's entry before
    it runs and after it ends; a run in flight then still answers its
    waiters but is not cached, and later callers start a new one.
  - **Decorations.** `repo.decorations { folder }` answers
    `{ in_repository, root, rows: [{ path, status }] }` for the folder's
    direct children (`<folder>\<name>`). A missing folder, a folder below no
    `.git` marker (the walk honours `GIT_CEILING_DIRECTORIES` and is skipped
    when `GIT_DIR` or `GIT_WORK_TREE` is set) and any git failure give
    `in_repository: false` with no rows; outside a repository no git runs.
    Without git it answers the same with no spawn and logs `Git was not
    found…` as a warning once per backend process. An optional
    `operationId` makes it cancellable: a `cancel` naming it kills the
    request's git process (or ends its wait for a shared status run, which
    goes on for the others), and the call fails with `-32000` `Git operation
    cancelled`. `--untracked-files=normal` stays (phase 4 fix decision): it
    overrides a repository's `status.showUntrackedFiles=no`, as 1.0.4 did,
    because the badges must show untracked files; the shared run, the cache
    and cancel bound the cost in a large repository.
  - **Transfers rows and cancel.** fetch, pull and push open a row with
    `host.progress_begin("Fetching <repository folder name>")` (`Pulling …`,
    `Pushing …`), or run without one when no host connection exists, and end
    it with `progress_complete` or with `progress_fail` and the error text. A
    `cancel` naming the request's `operationId` or the row's id stops them
    within 50 ms, and the call fails with `-32000` `Git operation cancelled`.
    `repo.commit` honours `cancel` for its `operationId` and opens no row.
  - **Errors** the UI matches stay byte-identical: `The current path is not
    inside a Git repository.`, `Commit message is required.`, `Refusing to
    run a file-level Git action against the repository root.`, `Select at
    least one Git path.` and `git <name> timed out after <n> seconds`, plus
    the new `Git was not found. Install Git for Windows or add git.exe to
    PATH.` and `Git operation cancelled`.
- UI assembly: `GitWorkbenchView`, `GitWorkbenchViewModel`, the detachable
  window, and the logic in `MainWindow.Git.cs`.
  Phase 4 shape (`plugins/git/ui`, two projects):
  - `SumaFile.Git.Model` (`net10.0-windows`, WinUI-free, references only the
    SDK, compile-only) holds the backend DTOs with their snake_case names,
    `GitProtocol` (the method literals and the two error texts the UI
    matches), `GitBackendClient`, `GitText`, `GitChangeRow`,
    `GitWorkbenchViewModel` (1.0.4's, without the host-mode members, plus
    `IsBusy` and `ChangesEmpty`), `GitWorkbenchController`, `IGitPrompts`,
    `GitDecorationProvider`, `GitSettings`, `GitPaths` (Core `PathRules`
    semantics: `\` and `/` are both separators, case is ignored) and
    `GitCommandIds`.
  - `SumaFile.Git` (19041) holds the entry type `SumaFile.Git.GitPlugin`,
    which implements `IPlugin`, `ICommandHandler`, `IPanelProvider` and
    `IRowDecorationProvider`, and the code-built views: `GitWorkbenchPanel`
    (`XamlReader` fragments with `{ThemeResource}` keys only, values set from
    `PropertyChanged`, a virtualized `Extended` changes list through
    `ListRows`, a read-only Consolas diff), `GitDiffSplitter` (a `Grid` with
    the `SizeWestEast` cursor that clamps the diff to 260-960 and resets it to
    420 on a double-tap) and `GitPrompts`. The host draws the panel's chrome
    (title, dock or window toggle, close button, resize grip).
  - `CreatePanel` returns a new workbench body per presentation and starts a
    silent refresh; the state lives in the view model, so docking and
    detaching keep it. While the workbench is shown, a change of the active
    folder refreshes it after 250 ms.
  - Commit and discard are code-built `ContentDialog`s shown through
    `IPluginDialogs.ShowDialogAsync`, on the shown workbench element's
    `XamlRoot` (docked or in its window), or on the main window when no
    workbench is shown. The discard dialog lists at most five paths, warns
    that untracked files and files staged as added (index status `A`, which
    discard unstages and then cleans) are deleted from disk, uses a
    `SystemFillColorCriticalBrush` primary button and defaults to Cancel.
  - The controller never throws to the host. Results and errors go to the
    message bar through `IPluginDialogs.Notify`; a command that ends with
    `Git operation cancelled`, or that its caller cancels, shows
    `<Command> was cancelled.`. A refresh's result applies only while it is
    the latest refresh and the active folder is still the one it asked
    about. A path command runs on the workbench's selected rows, or else on
    the file list's selection filtered by the changes of a status it asks for
    then (the backend's 1.5 s cache keeps that cheap; a cached one went stale
    while the workbench was hidden). After
    stage, unstage, discard, commit and pull it asks the host to decorate
    again (`IPluginHost.Decorations.Invalidate`); after discard and pull it
    also lists the folder again (`IPluginWorkspace.RefreshAsync`).
  - `GitDecorationProvider` runs on the thread pool and touches no WinUI
    object. A row gets its status label, a brush (`added` and `staged`:
    `SfDecorationAddedBrush`; `modified`, `renamed` and `staged + modified`:
    `SfDecorationModifiedBrush`; `untracked`: `SfDecorationUntrackedBrush`;
    `deleted` and `conflicted`: `SfDecorationDeletedBrush`; anything else:
    `SfTextMutedBrush`) and the tooltip `Git: <label>`; a folder inside a
    repository gets the flag `inRepository`. A backend error gives no
    decorations and is logged once per distinct message.
  - The UI's automation ids (`GitAutomationIds`: `Git.Workbench`,
    `Git.Branch`, `Git.Summary`, `Git.Refresh`, `Git.Fetch`, `Git.Pull`,
    `Git.Push`, `Git.Commit`, `Git.Stage`, `Git.Unstage`, `Git.Discard`,
    `Git.Diff`, `Git.Changes`, `Git.DiffTitle`, `Git.DiffText`,
    `Git.DiffSplitter`, `Git.CommitMessage`) are the UI smoke's contract, and
    the 1.0.4 automation names `Refresh Git status`, `Fetch`, `Discard
    selected changes`, `Git changes` and `Resize Git diff preview` stay.
- Contributions: ten commands, one context-menu group with `when`
  expressions, one toolbar toggle, one panel, one row decoration provider with
  `activateOn: startup`.
  Phase 4 (`plugins/git/plugin.json`, publisher as remote's, permissions
  `backend-process`, `shell` and `network`, backend `activateOn: startup`):
  - Commands, group `Git`: `git-panel` (`Git: show workbench`), `git-refresh`
    (`Git: refresh status`), `git-fetch` (`Git: fetch`), `git-pull`
    (`Git: pull`), `git-push` (`Git: push`), `git-commit` (`Git: commit
    staged changes`), `git-stage-selected` (`Git: stage selected paths`),
    `git-unstage-selected` (`Git: unstage selected paths`),
    `git-discard-selected` (`Git: discard selected changes`) and
    `git-diff-selected` (`Git: show selected diff`), exposed as
    `plugin:com.sumafile.git:<id>`.
  - One context-menu group `Git`: `Show Git workbench` and `Refresh status`
    (`folderFlag(inRepository)`), `Show diff` (`selectionCount == 1 &&
    hasDecoration`), `Stage selected`, `Unstage selected` and `Discard
    selected changes` (`selectionCount >= 1 && hasDecoration`), then `Fetch`,
    `Pull`, `Push` and `Commit` (`folderFlag(inRepository)`).
  - One primary toolbar button `Git` (glyph E8EF) running `git-panel`, with
    `when: folderFlag(inRepository)`.
  - One panel `workbench` (`Git workbench`, provider `workbench`, `bottom`,
    detachable) toggled by `git-panel`: the host shows and toggles it itself,
    and `git-diff-selected` shows it through `IPluginHost.Panels` before it
    previews the diff.
  - `rowDecorations: { provider: "status" }`.
- Settings: `enabled` replaces `EnableGitIntegration`. A migration copies the
  old value on first run.
  Phase 4: one declarative boolean `enabled` (`Show Git status, menus and the
  Git workbench`, description `Off: SumaFile runs no Git commands.`, default
  true, group `General`, `migrateFrom: "enableGitIntegration"`); a missing or
  unparsable value reads as true. A settings store that fails to answer (a
  service restart) reads as the last value read from it, true only before any
  read succeeded, so Git stays off for a user who turned it off. It gates what 1.0.4's
  `EnableGitIntegration` gated, with three decisions:
  - **Commands stay listed and refuse while it is off.** The ten commands
    stay on the palette and in the omnibar, since the host has no
    per-command `when`. Running one, or a refresh, shows `Git integration is
    turned off. Turn it on in Settings > Plugins > Git.` in the message bar
    and runs no git. 1.0.4 hid them.
  - **Turning it off is still a complete disable.** The write goes through
    `WriteSettingAsync`, which asks the decoration provider again at once.
    The provider reads the setting first, so with it off it returns nothing
    and calls no backend method: the badges, the `inRepository` flag and with
    it the Git menu group and the toolbar toggle go. The first such request
    after one that read it on also hides a shown workbench. A workbench the
    user opens on purpose while Git is off is not hidden again; it shows the
    message until the user closes it.
  - **Decorations run whatever the column layout.** Inside a repository the
    plugin runs one cached `git status` per folder sync, because the same
    result carries `inRepository`; 1.0.4 ran it only while the Git column was
    visible. Outside a repository it spawns nothing.
  Workspace profiles and saved layouts no longer carry the Git flag. They
  never applied it (`WorkspaceChromeLayout.Apply` skips it), so applying a
  profile or a layout behaves as before.
- Core removes 11 Git IPC methods, `GitText` on rows, the Git menu, the Git
  commands, the inline panel and the toolbar button. `domainMethodCount`
  drops to 75.
  Done in phase 4 (Task 15): `crates/simplefile-core/src/git.rs`, the four
  Git model types, `utils::hidden_command` and the service's eleven Git
  handlers are gone, and the service answers all eleven names (including
  the compatOnly `get_git_status`) `ERR_METHOD_NOT_FOUND`
  (`removed_git_methods_return_method_not_found`). `ipc/schema/v1` has no
  Git method and no `Git*` type, and `check-ipc-schema.mjs` and
  `check-winui-parity-gate.mjs` assert 75. `FileEntry.git_status` /
  `FileEntry.GitStatus` stay in the v1 wire format and the binary listing
  frame, always absent, and the status column keeps the id `git`
  (`ColumnLayout.DecorationColumnId`). The App has no `MainWindow.Git.cs`,
  `GitWorkbenchView`, `GitWorkbenchViewModel`, `GitWorkbenchWindow`,
  `GitPanel` row (the bottom plugin panel region moved up to its row),
  status-bar `GitToggleButton`, Settings > Behavior Developer group or
  `SfGit*Brush` theme keys; Core has no `git-*` command, `ctx-git-*` menu,
  Git toolbar glyphs, `GitStatusBrushKey`, repository probe or
  `EnableGitIntegration`. `UiSettings`, `WorkspaceSettingsStore`, saved
  layouts and the built-in profiles drop the flag: core never reads or
  writes the `enableGitIntegration` key again, so the plugin's migration
  finds the user's last value, and a stored layout's
  `enableGitIntegration` is ignored when read and no longer written. The
  three decisions about what the flag gated are recorded under Settings
  above: the commands stay listed and refuse while `enabled` is off (the
  palette's and omnibar's Git filter is gone), turning it off is still a
  complete disable with the workbench hidden, and decorations run whatever
  the column layout. Profiles and saved layouts never applied the flag.
  `GitUi_LivesOnlyInTheGitPlugin` guards that no App, Core or Ipc source
  names the Git UI again; the 1.0.4 "complete disable" source-shape checks
  became the Git plugin's behaviour tests
  (`Commands_WhenDisabled_NotifyTheDisabledMessageAndRunNothing`,
  `GetAsync_EnabledFalse_ReturnsNothingAndCallsNoBackend`,
  `GetAsync_TurnedOff_HidesAShownWorkbenchOnce`) and the Task 16 smoke.
  Parity-gate §2.7 tracks the feature by coverage level.
- UX changes from the move (phase 4):
  - Git menu items are hidden, not disabled, when their `when` is false.
  - The Git toggle moves from the status bar to the primary toolbar. It is
    visible inside a repository or while the workbench is open.
  - `Git: show workbench` from the palette shows the workbench; the toolbar
    button toggles it.
  - A saved custom toolbar that held a core `git-*` button loses it.
  - Fetch, pull and push become Transfers rows with Cancel.
  - Results show in the message bar through `IPluginDialogs.Notify`.
  - A pull or discard the plugin runs no longer invalidates the service's
    listing cache; the folder watcher, the directory stamp and the 10 s cache
    age bound the staleness, as phase 3 accepted for remote downloads.
  - The commands' and the open workbench's behaviour while `enabled` is off,
    the complete disable and the decorations' independence from the column
    layout are the three decisions under Settings above.
- Without the plugin, SumaFile shows no Git UI: no badges, no Git menu group,
  no toolbar toggle and no workbench.

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
  stays behind. Only the bundled folders are replaced; since phase 4 the
  user's plugins live beside them in `$INSTDIR\plugins` and are kept (see the
  phase 4 installers below).
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
  copies the payload. Profiles live in the plugin's data folder,
  `$INSTDIR\plugins-data\com.sumafile.remote` since phase 4, which a
  reinstall or upgrade keeps; saved passwords live in Windows Credential
  Manager.
- Release smokes, none of which touches the user's data:
  - `smoke-winui-startup.ps1` also sets `SUMAFILE_PLUGIN_DATA_ROOT`,
    `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` to its temp folder.
    Since phase 4 it also sets `SUMAFILE_PLUGIN_LOG_PATH` to a fresh
    `plugins.log` in that folder and reads the plugin lines from there;
    `startup-timing.log` is the app's own and stays in
    `%LOCALAPPDATA%\SumaFile`, where it reads it. It requires the new
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
    approvals path, plugin data root, plugin log (phase 4), metadata database
    and app data folder under its temp folder, and stops `sumafile-*-backend`
    processes under the extracted folder before removing it.
- The Debug UI smoke's `with` mode loads the remote plugin in the real host
  (`MainWindow.UiSmoke.Remote.cs`): bundled trust, the Connections section,
  one manager window per command, the profile dialog on the window's own
  `XamlRoot`, and the window host emptied on close.

Phase 4 adds the third, `com.sumafile.git` (§11.3):

- Payload layout with three plugins. `$script:BundledPluginIds` lists
  `com.sumafile.sevenzip`, `com.sumafile.remote` and `com.sumafile.git`, in
  this order, and `bundled.json` carries one entry for each:

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
      com.sumafile.git\
        plugin.json, sumafile-git-backend.exe,
        SumaFile.Git.dll, SumaFile.Git.Model.dll, SumaFile.Git.deps.json
  ```

  The folder carries no git: the backend runs `git.exe` from `PATH` (§11.3).
- Sources. `Get-GitPluginSources` takes the backend from
  `target\<debug|release>` and the three UI files from
  `plugins\git\ui\SumaFile.Git\bin\<Configuration>\net10.0-windows10.0.19041.0\`,
  as for the remote plugin. The freshness check compares the backend, both
  DLLs, the deps file and `plugin.json`.
- Builds. `scripts/build-plugins.ps1` adds `-p sumafile-git-backend` to its
  `cargo build` and, unless `-SkipDotnet`, runs `dotnet build` for every UI
  project in `$script:BundledPluginUiProjects`, in staging order
  (`SumaFile.Remote.csproj`, then `SumaFile.Git.csproj`). The release build
  adds `sumafile-git-backend` in release and builds both UI projects in
  Release from one list, which it requires to match the staging table.
- NSIS: `.onInit` and the uninstaller also stop `sumafile-git-backend.exe`,
  never `git.exe`, which could be the user's own; `check-winui-packaging.mjs`
  fails when the script ends `git.exe` or `7za.exe` by name. The installer
  removes `$INSTDIR\plugins\com.sumafile.git` before it copies the payload.
  The plugin's data folder, `$INSTDIR\plugins-data\com.sumafile.git`, is
  kept on reinstall and upgrade like every plugin's.
- Release smokes. None touches the user's data, and each one that runs git or
  launches an app or backend that can run it isolates git from the
  developer's config with `GIT_CONFIG_GLOBAL=<disposable>\gitconfig` (an
  empty file) and `GIT_CONFIG_NOSYSTEM=1`:
  - `smoke-winui-startup.ps1` sets both for the app it launches. It requires
    the `plugins.log` line `[com.sumafile.git] [info] Discovered: Enabled`,
    an approvals file with no record for the plugin, and, because the
    plugin starts at startup, a running
    `payload\plugins\com.sumafile.git\sumafile-git-backend.exe`. It prints
    `Bundled Git plugin trusted.` and, when it ends, stops payload backends
    of all three plugins by their path under the payload and waits up to
    5 s for each.
  - `smoke-winui-file-ops.ps1` (`Invoke-GitPluginSmoke`, after the remote
    plugin's) starts the payload's `sumafile-git-backend.exe` with a
    disposable `--data-dir`, `TMP` and `TEMP` in it, and the two git
    variables plus `GIT_CEILING_DIRECTORIES=<smoke root>`, which its own git
    calls use too. It creates a repository with `git init -b main`, a
    repository-local identity and one commit, and handshakes with the
    service's client name, as for the remote plugin. It checks `repo.status`
    (`is_repo` true, branch `main`), `repo.decorations` on a folder outside
    any repository (`in_repository` false, no rows) and `paths.stage` of one
    new file (checked with `git diff --cached`), and prints
    `Payload Git plugin smoke passed.`
  - `smoke-winui-msi.ps1` sets the same two variables for the extracted app.
  - The installer smokes launch their copy through `Start-InstalledSumaFile`,
    which already isolates git.
- Debug runs. The UI smoke builds `sumafile-git-backend` with the other
  backends and, in every mode, sets `GIT_CEILING_DIRECTORIES=<testRoot>`
  (because `test-results\` lies inside the SumaFile checkout),
  `GIT_CONFIG_GLOBAL=<testRoot>\state\gitconfig` and `GIT_CONFIG_NOSYSTEM=1`
  for the app. The test project's `CopyGitPluginFixture` copies the staged
  `target\plugins\com.sumafile.git\**` and `bundled.json` into
  `Fixtures\git\plugins\`, after removing only its own
  `com.sumafile.git` folder there, for `GitPluginBackendTests` (§13).

Phase 4 keeps every plugin file in the installation folder (§4.1), so the
payload and the smokes follow those locations:

- App-owned plugin items. `Get-AppOwnedPluginItems` in
  `scripts/lib/plugin-staging.ps1` lists what the app writes into an install
  folder: `plugins.log`, `plugins.log.1`, `plugin-approvals.json` and its
  `plugin-approvals.json.*.tmp` leftovers, `plugins-data`, and every folder
  under `plugins` whose name is not a bundled id, dot folders included.
  `Assert-Payload` fails with
  `WinUI payload must not contain app-owned plugin files: <list>` when it
  lists anything, so a payload never ships one machine's plugin state.
- `smoke-winui-startup.ps1` refuses a payload that already holds app-owned
  plugin items, and after the run, once the app and the payload backends
  have exited, fails when the payload holds any or when anything changed in
  the pre-release locations under `%LOCALAPPDATA%\SumaFile`
  (`Assert-LegacyPluginLocationsUnchanged`).
- No script under `scripts/` names a pre-release plugin location
  (`SumaFile\plugins.log`, `SumaFile\plugins-data`,
  `SumaFile\plugin-approvals.json`); `check-winui-packaging.mjs` enforces it,
  and the staging library builds those paths from segments.

Phase 4 installers: user plugins, `plugins-data`, `plugin-approvals.json` and
`plugins.log` under the install folder are the app's own files. Both
installers keep them on reinstall and upgrade and remove them on uninstall:

- NSIS. `InstallDirRegKey HKCU
  "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI"
  "InstallLocation"`, so a reinstall or upgrade without `/D=` goes to the
  folder the last install recorded there (the Install section writes it). The
  Install section still replaces each bundled plugin folder and never
  `$INSTDIR\plugins` or `$INSTDIR\plugins-data` as a whole. Uninstall keeps
  removing `$INSTDIR` whole; when it still exists it waits 1.5 s and tries
  once more, because a user plugin's backend can still be exiting after the
  app's kill-on-close job closed.
- WiX, for any install folder name. `MainFeature` gains the component
  `InstallDirMemory`. It writes `[INSTALLDIR]` to
  `HKCU\Software\SumaFile\WinUI\InstallDir`, and a `RegistrySearch` reads it
  back into `INSTALLDIR`, so a major upgrade installs into the remembered
  folder. AppSearch runs after the command line is read, so a remembered
  folder also overrides an `INSTALLDIR=` given on the `msiexec` command line;
  that is intended, as it keeps the user's plugins with the app. On uninstall only (`REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE`) the
  same component purges the four app-owned items in that folder:
  - `util:RemoveFolderEx` on `plugins\` and on `plugins-data\`;
  - `RemoveFile` on `plugins.log*` and on `plugin-approvals.json*`.

  It never purges the folder itself, which a custom install may share with
  other files. A `RemoveFolder` row on the same property removes the folder
  only once the purge has emptied it. Without that row Windows Installer
  leaves the empty folder behind, because `plugins\` and `plugins-data\` were
  still there when the folder's last harvested file went. Outside
  an uninstall the purge properties point at a folder under
  `[TempFolder]SumaFile-no-purge\` that is never created. That covers the old
  product's removal during a major upgrade too. WiX 3.14's
  `WixRemoveFoldersEx` fails ("Missing folder property") instead of skipping
  an unset property, so none is ever left unset. `scripts/lib/wix-msi.ps1`
  (`Resolve-WixBin`, `New-WinUIMsi`) builds the MSI with `WixUtilExtension`,
  for the release and for the lifecycle smoke. `Resolve-WixBin` looks on
  `PATH`, under `%WIX%` and in the fixed `WiX Toolset v3.14\bin` folders first
  and searches both Program Files trees only when none fits. It requires
  `candle.exe` 3.14.1 or later by file version, because older `RemoveFolderEx`
  (3.11 included) follows junctions, which the uninstall purge must not.
- `smoke-winui-installer` (`smoke-winui-nsis.ps1`) installs with `/S /D=` into
  `%TEMP%\sumafile-nsis-smoke-<guid>\SumaFile-WinUI`. It launches that copy,
  and requires `plugins.log`, `plugins-data\com.sumafile.sevenzip` and its
  `tmp` folder in the install folder. Then it seeds a user plugin
  (`New-UserPluginSeed`), reinstalls with `/S` alone and requires the same
  folder and an unchanged app-owned state. Finally it uninstalls and requires
  the folder gone (`Wait-FolderGone`).
- `smoke-winui-msi-lifecycle` (`smoke-winui-msi-lifecycle.ps1`) builds v1 and
  v2 MSIs from the release payload. It installs v1 per user into
  `<temp>\SumaFile Custom`, a name other than `SumaFile-WinUI`, launches it
  and seeds a user plugin. It upgrades to v2 without `INSTALLDIR` and requires
  the remembered folder, v1 gone and the app-owned state unchanged. Then it
  uninstalls and requires the folder gone. Its MSIs use Product.wxs's
  `LifecycleSmoke` identity: their own UpgradeCode, registry key
  (`Software\SumaFile\WinUI-LifecycleSmoke`), Start menu folder and component
  GUIDs. So the smoke never upgrades away, retargets or removes a SumaFile MSI
  the user has installed; 1.0.4 is one and shares the release UpgradeCode.
- Guards, shared by both smokes and `smoke-winui-upgrade.ps1`:
  - They refuse while a `SumaFile`, `SimpleFile`, `SimpleFile.App`,
    `simplefile-service` or `sumafile-*-backend` process runs
    (`Assert-NoSumaFileProcess`), because the NSIS installer ends those by
    image name.
  - They refuse while an HKCU "SumaFile" uninstall entry exists, and the
    lifecycle smoke also while a lifecycle product, key or Start menu folder
    is left over. The NSIS smokes also refuse while an NSIS shortcut exists.
  - They snapshot the machine "SumaFile" uninstall entries, the default
    folder `%LOCALAPPDATA%\Programs\SumaFile-WinUI` (`Get-FolderSnapshot`)
    and the pre-release plugin locations, and fail if any changed after the
    reinstall or upgrade and again at the end. The lifecycle smoke leaves its
    own ProductCodes out: Windows Installer lists a per-user MSI's entry under
    HKLM `WOW6432Node`.
  - They launch only their own copy, through `Start-InstalledSumaFile`. That
    drops the four `SUMAFILE_PLUGIN_*` overrides, puts the metadata database
    and app data under the smoke root, and isolates git. They stop processes
    only by path under the smoke folder. `check-winui-packaging.mjs` forbids
    the old `Programs\SumaFile-WinUI\SumaFile.exe` fallback in all three.
- `smoke-winui-upgrade.ps1` installs the previous release with `/D=` into a
  disposable folder, seeds a user plugin, upgrades without `/D=` and asserts
  the same, then uninstalls and waits for the folder to go. It downloads a
  release with `gh`, so this phase checks it statically. The NSIS upgrade
  from the previous release is proven by its first CI run.

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
templates/sumafile-plugin/           phase 5: the plugin template (com.example.template), never shipped
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

Phase 3 refinement, C# side (confirmed by the user on 2026-11-09): plugin C# projects
reference `SimpleFile.PluginSdk`, their own projects, and these packages
compile-only (`ExcludeAssets="runtime"`, `PrivateAssets="all"`):

- `Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools`, which the
  Hello fixture and `SumaFile.Remote` take;
- `CommunityToolkit.Mvvm`, which `SumaFile.Remote` and `SumaFile.Remote.Model`
  take. Without it `SumaFile.Remote` does not compile (CS0012).

No copy of these packages ships in a plugin folder. At run time
`PluginLoadContext`'s shared prefixes (§5.2, `CommunityToolkit.Mvvm` included)
force the host's copies, so a plugin is bound to the host's versions.
`RemotePlugin_ProjectsReferenceOnlyTheSdkAndEachOther` enforces the set for the
remote plugin's projects, and `GitPlugin_ProjectsReferenceOnlyTheSdkAndEachOther`
for the Git plugin's. This
widens "reference only the SDK package and the helper crate" for C#, and phase 4
(Git) and the phase 5 template inherit it.

Phase 4 refinement: `plugins/git/ui` has the same two-project shape as
`plugins/remote/ui`, with copies of its `Directory.Build.props` and
`Directory.Build.targets`:

```text
plugins/git/
  plugin.json, protocol.json
  backend/                           sumafile-git-backend (Rust)
  ui/
    SumaFile.Git.Model/              net10.0-windows, WinUI-free: DTOs, client, view model, controller, decorations
    SumaFile.Git/                    net10.0-windows10.0.19041.0: GitPlugin and the code-built views
```

`SumaFile.Git.Model` references only `SimpleFile.PluginSdk` (compile-only) and
no package; `SimpleFile.Tests` references it directly. `SumaFile.Git`
references the SDK, the model and `Microsoft.WindowsAppSDK` and
`Microsoft.Windows.SDK.BuildTools` compile-only; it needs no
`CommunityToolkit.Mvvm`, because its view model implements
`INotifyPropertyChanged` itself. Both projects are in `src-winui/SimpleFile.sln`.

Phase 5 refinement: `templates/sumafile-plugin` is a copyable starter for a
third-party author, `com.example.template`:

```text
templates/sumafile-plugin/
  plugin.json                        one command, one declarative setting, a first-call backend
  stage.ps1                          builds both halves, stages target\template-plugins\com.example.template
  README.md                          build, stage, SUMAFILE_PLUGIN_ROOTS, consent, signing, .sfplugin, packages
  ui/Example.Plugin/                 net10.0-windows, WinUI-free: ExamplePlugin (IPlugin, ICommandHandler)
  backend/                           example-plugin-backend (Rust, publish = false): example.echo
```

`Example.Plugin` references only `SimpleFile.PluginSdk` (compile-only) and no
package; it is in `src-winui/SimpleFile.sln`, and `SimpleFile.Tests` builds it
through a `ReferenceOutputAssembly="false"` reference and loads it through the
real `PluginManager` against a fake backend. The backend is a Cargo workspace
member and is listed in `pluginCrateManifests`. Nothing stages the template
into the payload or writes it into `bundled.json`, so it always needs consent.

Phase 5 refinement (#70/#75): `SimpleFile.PluginSdk` packs as
`SumaFile.PluginSdk`, as a dry run only. `npm run pack:sdk`
(`dotnet pack src-winui/SimpleFile.PluginSdk -c Release -o target/packages`)
writes `target/packages/SumaFile.PluginSdk.1.0.0.nupkg`, and the WinUI host job
in CI runs it after the tests ("Pack the plugin SDK (dry run)"); nothing pushes
it. The package holds `lib/net10.0-windows7.0/SimpleFile.PluginSdk.dll`, its XML
documentation `SimpleFile.PluginSdk.xml` and `README.md`, and declares the MIT
license, the repository and no dependencies. Its version is the SDK contract
version, `PluginSdk.Version`, not the app's. The project sets
`GenerateDocumentationFile`, so every public type and member is documented (CS1591
is an error); the docs state the thread contract, the `object` hand-off rule,
the window-option ranges, the folder-flag grammar and the host's deadlines.
`PackageId` and `PackageVersion` apply only while `dotnet pack` runs
(`_IsPacking`): NuGet lock files name a project reference by its package id and
version, so unconditional ones would rewrite every referencing project's lock
file. The assembly keeps its name, `SimpleFile.PluginSdk`, and its assembly
version follows the app's. `PluginSdkPackageTests` pins the metadata and the
documentation, and `scripts/tests/plugin-sdk-package.test.mjs` the script and
the CI step.

## 13. Testing

- SDK contract tests pin every public type and member; a change fails until the
  major is bumped or the test is updated deliberately.
- A fixture plugin under `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin`
  exercises every contribution kind, both declarative settings and a custom
  page, both panel placements (phase 4: `hello-panel`, right and detachable,
  toggled by `toggle-panel`; `hello-bottom`, shown through
  `IPluginHost.Panels` by `show-bottom`; the UI smoke step `plugin-panels`
  covers show, resize, one visible panel, detach, dock, a dialog on the
  panel window, Disable and Close), a backend with `host.*` calls, and an archive engine for a fake
  `.hello` extension.
- Loader tests cover shadowing, incompatible SDK major, invalid manifest,
  consent state transitions and signature verification.
- Service tests cover `register_archive_engine`, forwarding, fault handling
  and capability merging with a mock engine.
- `check-ipc-schema.mjs` learns the new method count and validates
  `ipc/schema/plugin/v1`.
- `check-winui-packaging.mjs` checks source files only, never a payload: that
  the three plugins' sources exist (manifests, protocols, backend crates and
  UI projects), that the staging library names each plugin's staged files,
  that the release build names each backend and UI project and the NSIS
  script each backend and bundled folder, and that the workflows run the
  staging, test and smoke scripts. The payload itself is checked by
  `Assert-Payload` in `build-winui-release.ps1` and by `Assert-BundledPlugins`
  in the MSI, NSIS and upgrade smokes, which require all three plugin folders
  and `bundled.json`.
- Git plugin through the real host (phase 4). `GitPluginBackendTests` run the
  staged `com.sumafile.git` (`CopyGitPluginFixture`) with the real
  `PluginManager` and `ProcessLauncher`, trusted through `bundled.json`.
  `GitBackendHarness` replaces the entry type with a capturing plugin that
  delegates to `GitDecorationProvider`, so `SumaFile.Git.dll` never loads;
  makes the repository with the git CLI (`git init -b main`, a
  repository-local identity); and sets `GIT_CONFIG_GLOBAL`,
  `GIT_CONFIG_NOSYSTEM=1` and `GIT_CEILING_DIRECTORIES` for the test process,
  which the backend inherits, so the tests run in the `Plugin environment`
  collection. Every wait is bounded. They cover:
  - bundled trust without an approvals record, and the backend started by
    `StartStartupBackends()` from the staged folder;
  - decorations through `GetRowDecorationsAsync` and through
    `PluginRowDecorationSource` with the Core coordinator, on the repository
    root and on `café 資料`: native-path keys, the status text, brush and
    tooltip, and the `inRepository` flag; none outside a repository;
  - stage, unstage, diff and commit, checked with the git CLI;
  - a fetch from a loopback remote that never answers, cancelled from its
    Transfers row, which fails with `Git operation cancelled` within 10 s
    and closes git's connection;
  - Disable stopping the backend process.

  Without the staged plugin they skip with `The Git plugin is not staged. Run
  scripts/build-plugins.ps1.`, except on GitHub Actions, where the WinUI job
  stages it first and a missing plugin fails them. The release smokes add the
  Git checks of §11.4: `Bundled Git plugin trusted.` in the startup smoke and
  `Payload Git plugin smoke passed.` in the file-ops smoke.
- Installer lifecycle (phase 4, §11.4). `smoke:winui-installer` (NSIS) and
  `smoke:winui-msi-lifecycle` (WiX) install into a disposable folder, seed a
  user plugin, reinstall or upgrade, and require the user plugin, its data,
  the approvals and the plugin log unchanged. Then they uninstall and require
  the folder gone. The MSI run uses a folder not named `SumaFile-WinUI`.
  `check-winui-packaging.mjs` pins:
  - NSIS: `InstallDirRegKey` and no whole-folder `RMDir` of `plugins` or
    `plugins-data`;
  - WiX: the remembered folder, the four-item purge, and no `RemoveFolderEx`
    on the install folder itself;
  - the smokes' guards.

  `scripts/test-plugin-staging.ps1` Case 10 covers the smokes' helpers.
- UI smoke adds a run with no plugins to prove the app starts and shows no
  Git, remote or 7-Zip UI. It is the `-NoPlugins` mode: the script points
  `SUMAFILE_PLUGIN_ROOTS` at one empty folder. Since phase 4 there is one
  plugin root, `<exe dir>\plugins`, and the override replaces it, so that run
  discovers no plugin at all, bundled ones included, exactly as an install
  whose `payload\plugins\` was deleted.
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
- UI smoke covers the FTP/SFTP plugin in both plugin modes (phase 3, as
  built; `MainWindow.UiSmoke.Remote.cs`):
  - With plugins (`-PluginsDir`), the script first starts
    `sumafile-remote-test-server.exe` on loopback, serving
    `<testRoot>\remote-server` (`café 資料\hello.txt`,
    `literal [brackets] & spaces.txt`, a 64 MiB `big.bin`), and writes a
    1.0.4 metadata database with one `Legacy SFTP` profile at the App's
    `SIMPLEFILE_METADATA_DB`. It exports `SUMAFILE_SMOKE_REMOTE` (the
    server's info file) and `SUMAFILE_SMOKE_REMOTE_LOCAL`
    (`<testRoot>\remote-local`: `upload.txt` and a 1 MiB `download\big.bin`),
    and kills the server in `finally`. The `remote` scenario then checks, with
    every wait bounded:
    - `com.sumafile.remote` is trusted through `bundled.json` alone and its
      command is on the palette; no bare `ftp-sftp-manager` remains;
    - the one Connections section lists exactly the imported `Legacy SFTP`
      (the UI proof of the 1.0.4 upgrade);
    - the command and a sidebar row click each open exactly one manager
      window, the row click with its profile selected;
    - a new SFTP profile with a saved password adds exactly one
      `SumaFile.Remote.<id>` credential; Connect asks to trust the server's
      exact fingerprint; a folder row click browses `café 資料`;
    - upload and download run as Transfers rows without per-task Options;
      a throttled (`bandwidthMib` 1) download over an existing file is
      cancelled from its row, which reads Cancelling and then Cancelled, and
      leaves the file byte-for-byte and no `.sumafile-partial` file;
    - new folder, rename to `renamed folder [ok]` and delete, on the server;
    - disabling the plugin during a transfer closes the window, fails the row
      with `Plugin backend stopped.`, ends the backend process and removes
      the section and the command with no dialog; enabling brings them back;
    - deleting the profile removes its credential; the Settings > Plugins
      card shows the declarative bandwidth control and the plugin page,
      whose button opens one manager window.
    A credential cleanup runs whatever fails: it deletes the smoke profile's
    target and fails, without deleting it, on any other new
    `SumaFile.Remote.*` target. `remote-backend-loss` kills the backend
    (Faulted, section gone) and restarts it (Enabled, section back), and
    `service-reconnect` also requires the remote backend's process to
    survive the service restart with the plugin still Enabled.
  - Without plugins (`-NoPlugins`): no sidebar section, no command or
    toolbar action naming `ftp-sftp-manager`, no
    `overflow-ftp-sftp-manager` in the Pane More > Tools menu, and an opened
    Settings window has no `SettingsRemoteManagerButton` and no
    `FTP/SFTP connections` text.
- UI smoke covers the Git plugin in both plugin modes (phase 4, as built;
  `MainWindow.UiSmoke.Git.cs`). Both modes need git on `PATH` (the script
  throws `Git for Windows is required for the Git plugin smoke` otherwise).
  The script makes `<testRoot>\git` with its own git calls, each bounded at
  30 s and isolated as the App is (`GIT_CONFIG_GLOBAL` at an empty
  `<testRoot>\state\gitconfig`, `GIT_CONFIG_NOSYSTEM=1`,
  `GIT_CEILING_DIRECTORIES=<testRoot>`, `-c core.fsmonitor=false`):
  - a bare `origin.git` and `repo` (`git init -b main`, a repository-local
    identity, `commit.gpgsign=false`, `core.autocrlf=false`) holding
    `tracked.txt`, `café 資料\hello.txt`, `literal [brackets] & spaces.txt`
    and `discard-me.txt`, committed and pushed with `git push -u origin main`;
  - a `peer` clone that commits and pushes `from-peer.txt`, so `repo` is
    behind by one and its pull is a fast-forward;
  - in `repo`: `tracked.txt` and `discard-me.txt` modified, an untracked
    `new.txt` and a staged `staged.txt`; and `not-a-repo\plain.txt`.

  It exports `SUMAFILE_SMOKE_GIT`. The App checks the isolation variables
  and verifies with the git command line itself (bounded at 30 s,
  `--no-optional-locks`). Every UI wait is bounded at 10 s and every git
  network step at 60 s. With plugins (`-PluginsDir`) the `git` scenario
  checks, in order:
  - trust and surface: `com.sumafile.git` is Enabled through `bundled.json`
    from the install root with no approvals record, all ten
    `plugin:com.sumafile.git:*` commands are on the palette and no bare
    `git-*` remains, the toggle is in `OfferedPrimaryItems`, one
    `sumafile-git-backend` runs under the install root, and the first
    decoration request came after `ready`;
  - the 1.0.4 migration: with `enableGitIntegration` = `false`,
    `MigrateLegacySettingsAsync` copies it once; `repo` then shows no badge,
    flag, menu group or toggle; the commands stay on the palette and
    `git-refresh` shows `Git integration is turned off. Turn it on in
    Settings > Plugins > Git.` in the message bar titled `Git`; the Settings
    > Plugins switch `PluginSetting.com.sumafile.git.enabled` reads off and,
    turned on through the control, decorates the open folder with no
    navigation;
  - decorations: the four changed rows with their labels and brush keys and
    the `com.sumafile.git:inRepository` flag in `repo`; nothing in
    `not-a-repo`;
  - the panel from its toolbar toggle: `PluginPanel.com.sumafile.git.workbench`
    docked at the bottom, the branch `main`, as many rows as
    `git status --porcelain=v1` lines, and the toggle kept, toggled on, in
    `not-a-repo` while the panel is shown;
  - stage and unstage `new.txt`, the working tree diff of `tracked.txt`,
    discard `discard-me.txt` through the confirmation, fetch (a
    `Fetching repo` Transfers row, ahead 0 and behind 1), the fast-forward
    pull, a commit whose message-bar text starts with git's
    `[main <sha>] smoke commit`, and a push that moves `origin.git`'s `main`;
  - detach and dock (`PluginPanelWindow` titled `Git workbench`), a
    `ResizePluginPanelBy(-40)`, then Disable with the commit dialog open on
    the detached window: the window closes, the dialog gate is free, the
    badges, menu group, toggle and backend go; a save while disabled keeps
    the toolbar item in place, and Enable draws it again there;
  - a toggle the user removed stays removed across a backend restart and a
    disable, save and enable.

  `service-reconnect` also requires the Git backend's process to survive the
  service restart and `repo` to be decorated again after it is listed
  through the new service. Without plugins (`-NoPlugins`): no `git-*` or
  `plugin:com.sumafile.git:*` command or toolbar action, no `GitPanel`,
  `GitToggleButton` or `PluginPanel.*` element, no menu entry naming git for
  a file in `repo`, no `Git integration` text in Settings, no decoration in
  `repo`, no `sumafile-git-backend` process and `HasProviders == false`.
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
   packaging. Phase 5 does dry runs only (`npm run pack:sdk`,
   `npm run pack:crates`); publishing is a later, separate step.

Each phase ends with the full check suite green and a release-build smoke run.
