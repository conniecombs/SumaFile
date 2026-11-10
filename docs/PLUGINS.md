# Writing SumaFile plugins

This guide is for authors of SumaFile plugins. It covers the plugin folder and its `plugin.json`, every contribution
kind, declarative settings, the C# SDK, the backend protocol, the limits and deadlines the host applies, where
SumaFile keeps plugin files, trust and signing, packaging, the plugin template and the packages.

Where this guide and the code disagree, the code is right; please report the difference. The sources are:

- `src-winui/SimpleFile.PluginHost/Manifest/PluginManifestReader.cs`: what a `plugin.json` may contain;
- `src-winui/SimpleFile.PluginSdk`: the C# contracts, with XML documentation on every member;
- `ipc/schema/plugin/v1/protocol.json` and [`ipc/schema/plugin/README.md`](../ipc/schema/plugin/README.md): the
  backend wire protocol;
- `crates/sumafile-plugin`: the Rust helper crate for backends.

`scripts/tests/plugins-doc.test.mjs` fails when this guide stops naming a `host.*` method, an archive-engine method,
an error code, a contribution kind or a setting type that those sources define.

## Contents

- [How a plugin works](#how-a-plugin-works)
- [Folder layout](#folder-layout)
- [`plugin.json` reference](#pluginjson-reference)
- [Contributions](#contributions)
- [Declarative settings](#declarative-settings)
- [The UI half: the SDK](#the-ui-half-the-sdk)
- [The backend](#the-backend)
- [Limits and deadlines](#limits-and-deadlines)
- [Where SumaFile keeps plugin files](#where-sumafile-keeps-plugin-files)
- [Trust and consent](#trust-and-consent)
- [Signing](#signing)
- [Packaging and installing a `.sfplugin`](#packaging-and-installing-a-sfplugin)
- [The plugin template](#the-plugin-template)
- [Packages](#packages)
- [Plugin states and troubleshooting](#plugin-states-and-troubleshooting)
- [Host-shared assemblies](#host-shared-assemblies)

## How a plugin works

A plugin is a folder with a `plugin.json` manifest and up to two halves:

- a **UI half**, a .NET assembly that SumaFile loads into its own process. It implements interfaces from the SDK
  (`SimpleFile.PluginSdk`, packaged as `SumaFile.PluginSdk`) and can run commands, build WinUI elements for sidebar
  sections, panels and a settings page, decorate file rows, show dialogs and open windows;
- a **backend**, an executable in any language that SumaFile starts as a separate process and talks to over a named
  pipe with length-prefixed JSON-RPC 2.0. It can serve the UI half's requests, call back into SumaFile (settings,
  progress rows, the log, notices), and serve archive formats to SumaFile's file service.

Both halves are optional; a plugin needs at least one contribution to be useful. A plugin with only a backend can
contribute archive formats (the bundled 7-Zip plugin does). Commands, sidebar sections, panels, row decorations and
a settings page need the UI half.

SumaFile renders every contribution from the manifest before any plugin code runs. Plugin code loads on first use:
the first command, the first time a sidebar section, panel or row decoration needs its provider, or the first time
the plugin's settings page opens. A backend starts on the UI half's first backend call, or at startup when the
manifest asks for it or the plugin serves archive formats.

There is no sandbox. A plugin runs with the user's permissions, which is why SumaFile asks the user before it enables
one (see [Trust and consent](#trust-and-consent)).

The quickest start is the template in `templates/sumafile-plugin` (see [The plugin template](#the-plugin-template)).

## Folder layout

```text
plugins\<plugin id>\
  plugin.json            the manifest, required; the folder name must equal its id
  <ui assembly>.dll      the UI half, optional
  <ui assembly>.deps.json
  <other assemblies>     the UI half's own dependencies (never the host-shared ones)
  <backend>.exe          the backend, optional, any language
  signature.json         the publisher signature, optional
  ...                    anything else the plugin needs: assets, licenses, bundled tools
```

- The folder name must be exactly the plugin id. A folder whose name starts with `.` is never a plugin (the
  installer uses such names for its staging folders).
- `ui.assembly` and `backend.executable` are bare file names in the plugin folder: no directories, no drive letters,
  no `..`.
- No copy of `SimpleFile.PluginSdk.dll` or of any other host-shared assembly belongs in the folder; SumaFile loads its
  own (see [Host-shared assemblies](#host-shared-assemblies)). Build the UI project with `EnableDynamicLoading` so it
  writes the `.deps.json` the host resolves the plugin's own dependencies from.
- Compiled XAML does not load from a plugin folder. Build UI in code, or load XAML strings with `XamlReader.Load`.

## `plugin.json` reference

`plugin.json` is a UTF-8 JSON object (a leading byte order mark is skipped). The host reads every manifest at startup
and on every rescan. A manifest that breaks any rule below is invalid: the plugin is listed in Settings > Plugins as
**Invalid plugin.json** with every error, and none of its code loads. An `sdkVersion` problem lists it as **Not
compatible with this version of SumaFile** instead.

A complete example (the template's manifest):

```json
{
  "id": "com.example.template",
  "name": "Example plugin",
  "version": "0.1.0",
  "publisher": { "name": "Example Publisher" },
  "sdkVersion": "1.0.0",
  "ui": { "assembly": "Example.Plugin.dll", "entryType": "Example.Plugin.ExamplePlugin" },
  "backend": { "executable": "example-plugin-backend.exe", "args": [], "activateOn": "first-call", "acceptsMultipleClients": false },
  "permissions": ["backend-process"],
  "contributions": {
    "commands": [
      { "id": "echo", "label": "Example: echo the greeting", "group": "Example" }
    ]
  },
  "settings": [
    { "key": "greeting", "type": "string", "label": "Greeting", "description": "The text the Example: echo the greeting command sends to the backend.", "default": "Hello from the example plugin", "group": "General" }
  ]
}
```

### Top-level fields

| Field | Required | Type | Rules |
| --- | --- | --- | --- |
| `id` | yes | string | `^[a-z0-9][a-z0-9.-]{1,63}$`: 2 to 64 lowercase letters, digits, dots or hyphens, starting with a letter or digit. Reverse-DNS style, such as `com.example.tool`. Must equal the folder name. |
| `name` | yes | string | The display name, at most 100 characters. |
| `version` | yes | string | The plugin's version, shown in the consent dialog and Settings > Plugins. Any non-empty string of at most 64 characters is accepted; use semantic versioning. |
| `publisher` | yes | object | See below. |
| `sdkVersion` | yes | string | The SDK version the plugin was built against, `major.minor.patch` (a `-` or `+` suffix is allowed). The host loads the plugin only when the major equals the major of its `PluginSdk.Version` (`1.0.0`) and the minor is not above its minor: the SDK assembly binds by major.minor, so a plugin built against SDK `1.1` cannot load in a `1.0` host. |
| `description` | no | string | At most 2000 characters. Read and bounded, not shown yet. |
| `ui` | no | object | The UI half; see below. |
| `backend` | no | object | The backend; see below. |
| `contributions` | no | object | The contributions; see [Contributions](#contributions). An absent or empty object contributes nothing. |
| `settings` | no | array | Declarative settings; see [Declarative settings](#declarative-settings). |
| `permissions` | no | array of strings | Informational: shown in the consent dialog, never enforced. The bundled plugins use `backend-process`, `network`, `credentials` and `shell`. |

Other top-level fields are ignored. Under `contributions`, an unknown key is an error
(`The contributions.<key> kind is not supported by this host.`), so a typo never loads as a missing slot.

`publisher`:

| Field | Required | Rules |
| --- | --- | --- |
| `name` | yes | At most 100 characters. |
| `url` | no | A string of at most 2048 characters. |
| `publicKey` | no | The publisher's raw 32-byte Ed25519 public key in base64. Needed only for a signed plugin (see [Signing](#signing)). |

`ui`:

| Field | Required | Rules |
| --- | --- | --- |
| `assembly` | yes | A bare file name, such as `Example.Plugin.dll`. |
| `entryType` | yes | The full name of a public type in that assembly that implements `IPlugin` and has a public parameterless constructor. |

`backend`:

| Field | Required | Rules |
| --- | --- | --- |
| `executable` | yes | A bare file name, such as `example-plugin-backend.exe`. |
| `args` | no | Strings passed after `--pipe-name` and `--data-dir`. Default `[]`. |
| `activateOn` | no | `first-call` (the default): the backend starts on the UI half's first `InvokeBackendAsync`. `startup`: it starts once SumaFile has started. A plugin with `archiveFormats` starts at startup whatever this says. |
| `acceptsMultipleClients` | no | `true` when the backend accepts more than one connection at once. Required for `archiveFormats`, because the file service connects alongside the UI host. Default `false`. |

### String limits

Free-text strings that SumaFile shows are bounded, in UTF-16 characters. A longer value makes the manifest invalid
with `The <field> field is longer than <n> characters.` (for `when`, `The <path>.when expression is longer than 512
characters.`).

| Field | Limit |
| --- | --- |
| `name`, `publisher.name` | 100 |
| `version` | 64 |
| `publisher.url` | 2048 |
| `description` | 2000 |
| each contribution `label`, `title` and `group`, and a setting's `label` and `group` | 200 |
| `commands[].shortcut` | 64 |
| a setting's `description` | 1000 |
| a setting's `default` | 1000 |
| each of an `enum` setting's `options` (at most 256 options) | 200 |
| `when` (context menu items and toolbar buttons) | 512 |
| `toolbar[].glyph` | 8 |
| `archiveFormats[].engineName` | 100 |
| `archiveFormats[].extension` | 32 |
| `ui.entryType`, and the `provider` of `sidebar`, `panels`, `rowDecorations` and `settingsPage` | 256 |

Ids are bounded by their grammars: the plugin id, command ids (1 to 128 characters), and setting, sidebar and panel
ids (1 to 64 characters). Everything else is bounded by the size of the file.

### Size and the startup budget

`plugin.json` may be at most 1 MiB (`PluginManifestReader.MaxManifestBytes`). A larger one is invalid with
`plugin.json is larger than 1 MiB.`, and the host never reads more than the cap plus one byte of it. The installer
applies the same cap.

Reading every manifest and rendering every contribution is part of SumaFile's startup. The design target is 50 ms
for ten plugins; CI checks the span in its UI smoke session, which has four plugins, against 50 ms. No plugin code
runs in it. The scan runs on a thread-pool thread while the file service starts: it reads each `plugin.json` once,
hashes the same bytes for trust, and checks `signature.json`, which hashes every file it lists. The budgeted span
then waits for the scan and renders the contributions. Hashes are computed at scan time, so a very large signed
plugin adds to startup once its hashing outlasts the file service's start. Keep the manifest small, declare only what
the plugin uses, and prefer `activateOn: first-call`: a startup backend is launched after this part of startup, but it
still costs the user a process for as long as SumaFile runs.

## Contributions

Every contribution is rendered from the manifest while the plugin is enabled, before its code loads. Commands are
named by their id; the host calls them `plugin:<plugin id>:<command id>` (that is `PluginCommandContext.CommandId`).
A command id is 1 to 128 characters with no whitespace, wherever it is declared or referenced, else
`The <path> value '<c>' is invalid: use 1 to 128 characters with no whitespace.`

### `commands`

```json
"commands": [
  { "id": "word-count", "label": "Example: count words in the selection", "group": "Example", "shortcut": "Ctrl+Alt+W" }
]
```

| Field | Required | Rules |
| --- | --- | --- |
| `id` | yes | The command grammar; unique within the plugin (`The command id '<id>' is declared more than once in contributions.commands.`). |
| `label` | yes | The text in the command palette (`Ctrl+Shift+P`). |
| `group` | no | The palette group, and the toolbar group in Settings > Toolbar. Default empty. |
| `shortcut` | no | Display text only: the palette shows it next to the command. This host does not bind it to a key. |

The entry type handles every command in `ICommandHandler.ExecuteAsync`, on the UI thread, with the active folder and
selection. Commands need a `ui` assembly: the reader accepts them without one, but running one marks the plugin Load
failed. An exception from `ExecuteAsync` other than a cancellation is shown in SumaFile's message bar.

```csharp
public async Task ExecuteAsync(PluginCommandContext context, CancellationToken ct)
{
    if (context.CommandId != "plugin:com.example.tool:word-count") return;
    var reply = await _host!.InvokeBackendAsync<CountReply>("words.count", new { paths = context.SelectedPaths }, ct);
    _host.Dialogs.Notify($"{reply.Words} words", isError: false);
}
```

### `contextMenu`

```json
"contextMenu": [
  { "group": "Example", "items": [
    { "command": "word-count", "label": "Count words", "when": "selectionCount >= 1 && !singleFolder" },
    { "command": "open-readme", "label": "Open README", "when": "hasExtension(.md)" }
  ] }
]
```

Each entry is a submenu, titled `group`, appended after SumaFile's own entries. `items[].command` names a command the
plugin declares in `commands` (the reader checks only its grammar; an undeclared command fails when it runs);
`items[].label` is required. An item whose `when` is false is hidden, and a submenu whose items are all hidden is
hidden too. Without `when` the item always shows.

#### `when` expressions

The grammar, shared by context-menu items and toolbar buttons:

- operators: `||`, `&&`, `!` and parentheses, nested at most 32 deep;
- `selectionCount <op> <integer>`, with `<op>` one of `==`, `!=`, `<`, `<=`, `>`, `>=`;
- `singleFile`: exactly one item is selected and it is a file;
- `singleFolder`: exactly one item is selected and it is a folder;
- `hasExtension(.ext)`: exactly one file is selected and its extension is `ext` (case-insensitive, the dot optional);
- `hasDecoration`: at least one selected row carries a decoration from this plugin (see
  [`rowDecorations`](#rowdecorations));
- `folderFlag(name)`: this plugin's row decoration provider set the flag `name` for the active folder.

An empty expression is true. An invalid one makes the manifest invalid with
`The <path>.when expression is invalid: <reason>`. `hasDecoration` and `folderFlag` read only this plugin's
decorations: one plugin can never satisfy another plugin's `when`. They read the decorations the pane already has;
evaluating a `when` never asks a provider.

### `toolbar`

```json
"toolbar": [
  { "command": "word-count", "label": "Count words", "glyph": "\uE8EF", "placement": "primary", "when": "selectionCount >= 1" }
]
```

| Field | Required | Rules |
| --- | --- | --- |
| `command` | yes | A declared command. A command appears at most once in `toolbar` (`The toolbar command '<c>' is declared more than once in contributions.toolbar.`). |
| `label` | yes | The button's text and tooltip. |
| `glyph` | no | An icon character from SumaFile's icon font (Segoe Fluent Icons), at most 8 characters. Default empty. |
| `placement` | no | `primary` (on the toolbar) or `overflow` (in the overflow menu, the default). |
| `when` | no | Hides the button while it is false, evaluated for the active pane's selection. A button that toggles a panel stays visible while its panel is shown, so the user can always hide it. |

Users arrange plugin buttons like SumaFile's own in Settings > Toolbar, where a button is listed under its command's
`group`. A saved toolbar keeps a plugin button while the plugin is absent.

### `sidebar`

```json
"sidebar": [
  { "id": "bookmarks", "title": "Example bookmarks", "provider": "bookmarks", "command": "add-bookmark" }
]
```

A collapsible section in the sidebar, below SumaFile's own sections. The host draws the header from the manifest:
`title`, a collapse button and, when `command` names a declared command, a button that runs it. `id` follows the
setting key grammar (`^[a-z0-9][a-zA-Z0-9_-]{0,63}$`) and is unique within the plugin; `title` and `provider` are
required. The section needs a `ui` assembly (`contributions.sidebar needs a ui assembly.`).

The entry type implements `ISidebarSectionProvider`. `CreateSidebarSection(PluginSidebarSectionRequest(SectionId,
Provider))` runs on the UI thread and returns the body as a `Microsoft.UI.Xaml.FrameworkElement` (typed `object`; see
[The `object` hand-off rule](#the-object-hand-off-rule)). The host may ask again after it dropped the element: return
a new element each time, or one that has no parent.

```csharp
public object CreateSidebarSection(PluginSidebarSectionRequest request) =>
    new TextBlock { Text = "No bookmarks yet", Margin = new Thickness(12, 4, 12, 4) };
```

### `panels`

```json
"panels": [
  { "id": "inspector", "title": "Example inspector", "provider": "inspector", "placement": "bottom", "detachable": true, "command": "toggle-inspector" }
]
```

| Field | Required | Rules |
| --- | --- | --- |
| `id` | yes | The setting key grammar; unique within the plugin. |
| `title` | yes | The header the host draws. |
| `provider` | yes | Passed to the provider in the request. |
| `placement` | no | `bottom` (the default) or `right` of the file list. |
| `detachable` | no | Whether the host offers to move the panel into its own window. Default `false`. |
| `command` | no | A declared command that toggles the panel; a command toggles at most one panel. |

Panels need a `ui` assembly. The host draws the chrome (title, dock-or-window toggle, close button, resize grip) and
shows one plugin panel at a time. The entry type implements `IPanelProvider`; `CreatePanel(PluginPanelRequest(PanelId,
Provider, Detached))` runs on the UI thread, once per presentation: docked (`Detached` false), and again for the
panel's window when the user detaches it (`Detached` true), because a WinUI element cannot move to another window.
Return a new element each time and keep the panel's state outside it. The plugin shows and hides its panels with
`IPluginHost.Panels` (`ShowAsync`, `Hide`, `IsVisible`).

### `rowDecorations`

```json
"rowDecorations": { "provider": "status" }
```

One object; `provider` is required, and the plugin needs a `ui` assembly. The entry type implements
`IRowDecorationProvider`. SumaFile asks it once per folder listing, for every listed row in one batch:

```csharp
public async Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct)
{
    var reply = await _host!.InvokeBackendAsync<FolderStatus>("status.folder", new { folder = request.FolderPath }, ct);
    var rows = reply.Rows.ToDictionary(
        row => row.Path,
        row => new RowDecoration(row.Letter, "SfDecorationModifiedBrush", row.Detail));
    return new PluginFolderDecorations(rows, reply.Tracked ? ["tracked"] : []);
}
```

- It runs on a thread-pool thread and must not touch WinUI objects. The plugin is activated on the UI thread before
  the first call.
- SumaFile waits 250 ms for the result before it shows the folder, and applies a later result when it arrives, up to
  30 s after the request. A later result is dropped, and the folder is asked again on its next listing. The token is
  cancelled when the user navigates on; honour it.
- An exception or a null result is logged as `[warn] Row decorations failed: <message>` and the folder shows no
  decorations from this plugin. The plugin stays enabled.
- `Rows` keys are the request's paths, matched case-insensitively and whatever the separators; a key that names no
  listed row is ignored. `RowDecoration(Text, BrushKey?, Tooltip?)`: `BrushKey` is one of `SfDecorationAddedBrush`,
  `SfDecorationModifiedBrush`, `SfDecorationUntrackedBrush`, `SfDecorationDeletedBrush`, `SfTextPrimaryBrush`,
  `SfTextMutedBrush` or `SfAccentBrush`; anything else draws with `SfTextMutedBrush`.
- `FolderFlags` are names for `folderFlag(name)`, each matching `^[A-Za-z][A-Za-z0-9_-]{0,63}$`; others and duplicates
  are dropped.
- When two plugins decorate the same row, the plugin whose id sorts first (ordinal) owns its text.
- Nothing is asked before SumaFile has finished starting, or for network and Recycle Bin folders.
- `IPluginHost.Decorations.Invalidate()` asks the provider again for the folders on screen, for example after the
  plugin's state changed. Writing one of the plugin's declarative settings in Settings > Plugins does the same.

### `settingsPage`

```json
"settingsPage": { "provider": "example-settings" }
```

A custom page on the plugin's own page under Settings > Plugins, below its declarative settings, for settings the
declarative schema cannot express. SumaFile asks for it when the user first opens that plugin's page, not when Settings
opens, so a plugin is not activated for a page nobody looks at; settings search finds the custom page's controls once
it has been created. `provider` is required, and the plugin needs a `ui` assembly
(`contributions.settingsPage needs a ui assembly.`). The entry type implements `ISettingsPageProvider`;
`CreateSettingsPage(PluginSettingsPageRequest(Provider))` runs on the UI thread, once per Settings window, and returns
a `FrameworkElement`. Settings search indexes its labelled controls. The page is shown only while the plugin is
enabled; a failure shows `This settings page could not be loaded: <message>` and marks the plugin Load failed.

### `archiveFormats`

```json
"backend": { "executable": "example-archive-backend.exe", "acceptsMultipleClients": true },
"contributions": {
  "archiveFormats": [
    { "extension": ".xar", "canList": true, "canExtract": true, "canCreate": false, "canModify": false, "engineName": "Example XAR" }
  ]
}
```

The backend serves the format to SumaFile's file service, which then browses, extracts, creates and changes such
archives in its panes and Transfers window. The plugin needs a backend with `acceptsMultipleClients: true`
(`A plugin that declares contributions.archiveFormats needs a backend with backend.acceptsMultipleClients set to
true.`), and its backend always starts at startup.

| Field | Required | Rules |
| --- | --- | --- |
| `extension` | yes | With or without the dot, any case. The extensions SumaFile handles itself (`.zip`, `.tar`, `.tar.gz`, `.tgz`, `.rar`) are dropped; a plugin left with no other format fails to register. |
| `engineName` | yes | The engine's display name. Formats with the same name share one engine. |
| `canList`, `canExtract`, `canCreate`, `canModify` | no | What the engine supports; each defaults to `false`. `canModify` covers rename, delete and add inside the archive. |

Once the backend has handshaken, the host registers the formats with the file service, which connects to the
backend's pipe as further clients. The methods the service then calls are in [Archive engines](#archive-engines).

## Declarative settings

```json
"settings": [
  { "key": "enabled", "type": "boolean", "label": "Show example status", "default": true, "group": "General" },
  { "key": "mode", "type": "enum", "label": "Mode", "options": ["fast", "thorough"], "default": "fast", "group": "General" },
  { "key": "limit", "type": "integer", "label": "Item limit", "default": 100, "group": "Advanced" },
  { "key": "tool", "type": "path", "label": "Tool folder", "description": "Leave empty to use the bundled copy.", "group": "Advanced" }
]
```

Each plugin has its own page under Settings > Plugins (the Plugins item itself is an overview of all plugins). The page
renders each setting as a control, grouped by `group`, and settings search indexes them for every plugin and opens the
page of the plugin that matches. The value is saved as soon as the user changes it.

| Field | Required | Rules |
| --- | --- | --- |
| `key` | yes | `^[a-z0-9][a-zA-Z0-9_-]{0,63}$`; unique within the plugin. |
| `type` | yes | One of the types below. |
| `label` | yes | At most 200 characters. |
| `description` | no | Shown under the control; at most 1000 characters. |
| `group` | no | A heading on the plugin's page; at most 200 characters. |
| `options` | for `enum` | A non-empty array of strings; allowed only on `enum`. |
| `default` | no | Any JSON value; see below. |
| `migrateFrom` | no | Bundled plugins only; see below. |

| Type | Control | Stored value |
| --- | --- | --- |
| `boolean` | toggle | `true` or `false` |
| `string` | text box | any text |
| `integer` | number box | a whole number in invariant decimal |
| `enum` | drop-down | one of `options` |
| `path` | text box with a Browse button (a folder picker) | any text |

Every value is stored as a string under `plugin.<plugin id>.<key>` in SumaFile's settings, so a plugin can never read
or write another plugin's settings. The UI half reads and writes them with `IPluginHost.Settings` (`GetAsync`,
`SetAsync`), the backend with `host.get_settings` and `host.set_setting`. Both may also use keys the manifest does not
declare, under the same grammar; only declared keys appear in Settings > Plugins.

`default` is what Settings > Plugins shows while nothing is stored (a declared default that is not valid for the type
falls back to `false`, `0`, the first option or empty). It is not stored, and reading an unset setting returns null:
apply the same default in code.

`migrateFrom` names a SumaFile settings key (`^[A-Za-z][A-Za-z0-9._-]{0,127}$`, never one starting with `plugin.`)
whose value the host copies into the setting once, while the setting has no value of its own and the old value is
valid for its type. It never changes or deletes the old key. SumaFile honours it only for the plugins its own
`bundled.json` lists, which moved features out of SumaFile; for any other plugin the field is checked and ignored.
Activation waits for the copy, at most 5 s.

## The UI half: the SDK

### Project setup

The SDK targets `net10.0-windows` and references no WinUI. Reference it compile-only, so no copy goes into the plugin
folder:

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="SumaFile.PluginSdk" Version="1.0.0" ExcludeAssets="runtime" PrivateAssets="all" />
</ItemGroup>
```

The package is not published yet: outside this repository, pack it from a SumaFile checkout and add it as a local
source (see [Packages](#packages)); inside this repository the template uses a project reference instead. A plugin that builds WinUI elements targets `net10.0-windows10.0.19041.0` and also references
`Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools` compile-only, at the versions SumaFile ships
([Host-shared assemblies](#host-shared-assemblies)), as `plugins/git/ui/SumaFile.Git` does.

### Interfaces

The entry type (`ui.entryType`) implements `IPlugin` and whichever of the others its contributions need:

| Interface | Needed for | Members |
| --- | --- | --- |
| `IPlugin` | always | `ActivateAsync(IPluginHost host, CancellationToken ct)`, `DeactivateAsync()` |
| `ICommandHandler` | `commands` | `ExecuteAsync(PluginCommandContext context, CancellationToken ct)` |
| `ISidebarSectionProvider` | `sidebar` | `object CreateSidebarSection(PluginSidebarSectionRequest request)` |
| `IPanelProvider` | `panels` | `object CreatePanel(PluginPanelRequest request)` |
| `IRowDecorationProvider` | `rowDecorations` | `Task<PluginFolderDecorations> GetDecorationsAsync(PluginDecorationRequest request, CancellationToken ct)` |
| `ISettingsPageProvider` | `settingsPage` | `object CreateSettingsPage(PluginSettingsPageRequest request)` |

A contribution whose interface the entry type lacks marks the plugin Load failed when it is first used.

`ActivateAsync` hands the plugin an `IPluginHost`, which it keeps for its lifetime:

| Member | What it does |
| --- | --- |
| `PluginId` | The manifest's `id`. |
| `InvokeBackendAsync<T>(method, args, ct)` | Calls a backend method and deserializes its result as `T` with System.Text.Json (property names as declared, case-sensitive, null properties left out). The first call starts the backend when it is not running. A backend error fails the call with the backend's message. |
| `OnBackendEvent<T>(eventName, handler)` | Subscribes to the backend's notifications named `eventName`; dispose the result to unsubscribe. It works before the backend starts and carries over a restart. |
| `Settings` (`IPluginSettings`) | `GetAsync(key, ct)` (null when unset) and `SetAsync(key, value, ct)`, string values; see [Declarative settings](#declarative-settings). |
| `Progress` (`IPluginProgress`) | `Begin(title)` adds a Transfers row and returns an `IPluginOperation` with `Report(current, total, detail)`, `Complete()`, `Fail(message)` and a `CancellationToken` that the row's Cancel button cancels. Disposing it completes a row that has not ended. |
| `Dialogs` (`IPluginDialogs`) | `ShowMessageAsync`, `ConfirmAsync`, `PickFolderAsync`, `ShowDialogAsync(object contentDialog, ct)` and `Notify(message, isError)` (a non-modal notice in the message bar, titled with the plugin's name). Dialogs queue behind any other open dialog. |
| `Workspace` (`IPluginWorkspace`) | `ActivePath` and `SelectedPaths` of the active pane, a `Changed` event, `NavigateAsync(path, ct)` and `RefreshAsync(ct)`. |
| `Logger` (`IPluginLogger`) | `Info`, `Warn`, `Error(message, exception?)`, written to `plugins.log` with the plugin id. |
| `Windows` (`IPluginWindows`) | `Attach(object window, PluginWindowOptions(Title, Width, Height))` hands a plugin-created window to the host, which gives it SumaFile's chrome and closes it before it stops the plugin. Width is clamped to 320-3840 and height to 240-2160 device-independent pixels; a title is cut to 120 characters, and a blank one becomes the plugin's name. |
| `Panels` (`IPluginPanels`) | `ShowAsync(panelId, ct)`, `Hide(panelId)`, `IsVisible(panelId)` for the plugin's own panels. |
| `Decorations` (`IPluginDecorations`) | `Invalidate()` asks the plugin's row decoration provider again. |

The host sets no deadline on `InvokeBackendAsync`, and cancelling its token only ends the wait: no `cancel` reaches the
backend. A long backend operation therefore takes an operation id, and the plugin cancels it through its own method,
or through a progress row (see [Cancellation](#cancellation)).

A plugin that is disabled and enabled again gets a new `IPluginHost`. The old one stays usable as an object, but its
windows close at once and its backend calls start no backend. Plugin code is never unloaded while SumaFile runs.

The UI half has no data folder of its own. Keep small state in settings, and files in the backend's data folder.

Theme resources the host keeps stable, for `Application.Current.Resources` or `{ThemeResource}` in `XamlReader.Load`
strings: `SfTextPrimaryBrush`, `SfTextMutedBrush`, `SfBgPrimaryBrush`, `SfBgSecondaryBrush`, `SfBorderBrush`,
`SfAccentSoftBrush`, `SfAccentBrush`, `SfIconFont`, `SfGhostButtonStyle`, `SfTextBoxStyle`, `SfEmptyHintStyle`,
`SfSidebarListStyle`, `SfNavListItemStyle` and the four `SfDecoration*Brush` keys.

### The `object` hand-off rule

The SDK is WinUI-free, so every UI object crosses it typed as `object`:

- `CreateSidebarSection`, `CreatePanel` and `CreateSettingsPage` return a `Microsoft.UI.Xaml.FrameworkElement`;
- `IPluginWindows.Attach` takes a `Microsoft.UI.Xaml.Window`;
- `IPluginDialogs.ShowDialogAsync` takes a `Microsoft.UI.Xaml.Controls.ContentDialog`; a null `XamlRoot` means the main
  window, and a plugin window's `Content.XamlRoot` shows it there.

The host checks the type at run time. A provider that returns anything else, returns null or throws marks the plugin
Load failed; `Attach` and `ShowDialogAsync` throw `ArgumentException`. Because the plugin references the Windows App
SDK compile-only, the WinUI types it builds are the host's.

### Threading rules

- `ActivateAsync`, `ExecuteAsync`, `CreateSidebarSection`, `CreatePanel` and `CreateSettingsPage` run on the UI
  thread, so they may create WinUI objects. Their awaits resume there unless they use `ConfigureAwait(false)`.
- `GetDecorationsAsync` runs on a thread-pool thread and must not touch WinUI objects.
- `DeactivateAsync` runs on a thread-pool thread; marshal to the UI thread yourself before touching UI objects.
- `OnBackendEvent` handlers run on the connection's receive thread, one notification at a time. Keep them short, and
  marshal to the UI thread before touching WinUI. An exception they throw is swallowed.
- `IPluginWorkspace.Changed` is raised on the UI thread. The workspace's properties are a snapshot and may be read
  from any thread.
- Every host service may be called from any thread, with one exception: call `IPluginWindows.Attach` on the UI thread,
  before activating the window (from any other thread it throws `InvalidOperationException` and the window is not
  tracked). Create a `ContentDialog` on the UI thread too.
- `IPluginPanels.ShowAsync` and `Hide`, `IPluginWorkspace.NavigateAsync` and `RefreshAsync`, `IPluginDialogs.Notify`
  and `IPluginDecorations.Invalidate` are always queued, never run inline, so calling them from a command, a provider
  or a decoration request never re-enters a host pass. `ShowAsync` completes once the panel is shown; `IsVisible`
  returns what the UI thread last published.

### Lifecycle and deadlines

- **Activation.** The entry type is created and `ActivateAsync` runs once, on the UI thread, before the first command
  or provider call; that call waits for it, so keep it short. The token is the token of the call that caused the
  activation, except for a row decoration request, whose activation always runs to its end. An exception marks the
  plugin Load failed.
- **Stopping.** When the user disables or removes the plugin, a rescan finds its manifest changed, or SumaFile shuts
  down, the host closes the windows attached through `IPluginHost.Windows` (waiting at most 5 s), stops the backend,
  then calls `DeactivateAsync`. From the moment the windows start closing, `Attach` closes a new window at once.
- **Deadlines.** A plugin stopped while it is still activating gets 5 s to finish. `DeactivateAsync` gets 5 s; past
  that the host abandons the instance and records `plugin did not deactivate in time`. A progress row whose Cancel was
  pressed reads Cancelling until the plugin completes or fails the operation, or 30 s pass. The backend's start and
  handshake deadlines are in [Limits and deadlines](#limits-and-deadlines).

## The backend

The backend is an executable that serves a named pipe. Any language works; Rust backends use the `sumafile-plugin`
crate, which implements everything in this section ([The Rust helper crate](#the-rust-helper-crate)).

### Command line and environment

The host starts `<plugin folder>\<backend.executable>` with the plugin folder as its working directory:

```text
<backend>.exe --pipe-name <name> --data-dir <dir> [backend.args...]
```

- `--pipe-name`: the bare pipe name. The backend creates the pipe server `\\.\pipe\<name>`; the host connects as a
  client. Do not rely on the name's format.
- `--data-dir`: the plugin's data folder, `<install folder>\plugins-data\<plugin id>\`, which the host creates first.
  The backend owns its contents. SumaFile never empties it and removing the plugin keeps it, but uninstalling
  SumaFile deletes it with the rest of the installation folder.
- `TMP` and `TEMP` are set to `<data dir>\tmp`, which the host also creates, so the backend's temporary files and those
  of every process it starts stay in the installation folder. The rest of the environment is SumaFile's.
- Anything the backend writes to stderr goes to `plugins.log`, line by line, tagged with the plugin id. Standard
  output is not read, and nothing is written to standard input.

The host puts the backend in a job object with kill-on-close, so the backend and every process it starts end with
SumaFile, even when SumaFile crashes.

### Lifetime

- The backend starts on the UI half's first `InvokeBackendAsync` (`activateOn: first-call`), or once SumaFile has
  started (`activateOn: startup`, and every plugin with `archiveFormats`).
- It should exit when its last client disconnects (the crate's `serve` returns then). To stop a plugin the host kills
  the process and closes its connection; there is no shutdown message, so write files so that a kill at any point
  leaves them valid.
- A backend that exits or drops the connection on its own marks the plugin **Stopped** (Faulted). Its calls fail, its
  progress rows fail with `Plugin backend stopped.`, its archive engines are unregistered, and SumaFile's status center
  offers Restart.
- A failed start (no connection within 5 s, no handshake within 5 s more, a wrong handshake, an exit during startup)
  fails the call that started it. Three failed starts within 60 s mark the plugin Faulted.
- With `acceptsMultipleClients: false` the host opens one connection. With `true`, the file service connects as well
  (up to 8 connections, plus one more to send a `cancel`), so the pipe must accept several clients at once.

### Framing

Each message is a frame: a little-endian `uint32` byte length, then that many bytes of UTF-8 JSON, one JSON-RPC 2.0
object (no batches). A frame may be at most 80 MiB (83,886,080 bytes) in either direction; a longer length ends the
connection. The JSON-RPC envelope is the usual one:

```json
{ "jsonrpc": "2.0", "id": 7, "method": "words.count", "params": { "paths": ["C:\\notes\\a.txt"] } }
{ "jsonrpc": "2.0", "id": 7, "result": { "words": 412 } }
{ "jsonrpc": "2.0", "id": 7, "error": { "code": -32000, "message": "The file is locked." } }
```

Both sides send requests on the same pipe, so a frame with `method` and `id` is a request, one with `id` and `result`
or `error` is a reply, and one with `method` and no `id` is a notification. The host's request ids are integers; the
backend's requests to the host must use integer ids too (32-bit), or the host treats them as notifications and never
answers.

### Handshake

`plugin.handshake` is the first request on every connection. The host sends:

```json
{ "jsonrpc": "2.0", "id": 1, "method": "plugin.handshake", "params": { "pluginId": "com.example.tool", "hostVersion": "1.0.4.0", "sdkVersion": "1.0.0" } }
```

The file service sends the same params plus `"client": "simplefile-service"`, which tells a backend which connection
is the service's. The backend replies:

```json
{ "jsonrpc": "2.0", "id": 1, "result": { "pluginId": "com.example.tool", "pluginVersion": "1.2.0", "sdkVersion": "1.0.0" } }
```

The reply's `pluginId` must equal the manifest's id and its `sdkVersion` must be a plain `major.minor.patch` with
major `1`, or the start fails. The host waits at most 5 s for the reply, the service 5 s too. Any other request before
the handshake fails with `-32600`.

### Requests and routing

- **Host to backend:** the plugin's own methods, named without a prefix (`words.count`); the plugin owns its method
  namespace. The UI half calls them with `InvokeBackendAsync`, and `args` arrive as `params`. Document them the way
  the bundled plugins do, in a `protocol.json` beside `plugin.json` (`plugins/remote/protocol.json`,
  `plugins/git/protocol.json`).
- **Service to backend:** the `archive.*` methods, for a plugin with `archiveFormats`.
- **Either client to backend:** `cancel` (see [Cancellation](#cancellation)).
- **Backend to host:** `host.*` requests ([Host methods](#host-methods)), and notifications, which the UI half receives
  through `OnBackendEvent` by method name; notifications nobody subscribed to are dropped.
- Answer requests in any order; a slow request must not hold up the others, and the backend must keep reading while
  it waits for the host's reply to its own `host.*` request.
- Send `host.*` requests only on the host's connection. The service never answers a request; the crate fails every
  `host.*` call on the service connection except `host.log`, which it drops.
- An unknown method fails with `-32601`.

### Events

A backend sends events as notifications with any method name of its own:

```json
{ "jsonrpc": "2.0", "method": "status.changed", "params": { "folder": "C:\\repo" } }
```

`IPluginHost.OnBackendEvent<T>("status.changed", handler)` receives the `params`.

### Cancellation

`cancel { "operationId": "<id>" }` asks the backend to stop an operation. Only clients send it, the host or the
service, as a request (replied `null`) or as a notification, on any of their connections; the backend never sends it.

- A cancel is backend-wide: it applies to the operation whichever connection carries it.
- An id is "in flight" while a request carrying it as `params.operationId` runs, and, for an id the host returned from
  `host.progress_begin`, until the backend calls `host.progress_complete` or `host.progress_fail` for it or the
  connection that began it closes. That second case is how the Cancel button of a backend's own Transfers row reaches
  the backend: the host sends `cancel` with the row's id.
- Keep a cancel that arrives for an id not yet in flight (the crate keeps the 64 most recent): the request it names
  may still be unread on another connection.
- What a cancelled operation answers is the method's to define. A cancelled `archive.*` call fails with `-32000` and
  the exact message `Archive operation cancelled`.
- The crate answers a `cancel` request without a string `operationId` with `-32602`.

### Error codes

| Code | Meaning |
| --- | --- |
| `-32700` | The frame is not valid JSON (the reply's `id` is `null`). |
| `-32600` | A request before `plugin.handshake`, or a frame that is not a valid request or reply. |
| `-32601` | Method not found: an unknown plugin method from the backend, or an unknown `host.*` method from the host (`Unknown host method '<method>'.`). |
| `-32602` | Invalid params. The host's messages are listed under [Host methods](#host-methods). |
| `-32603` | Internal error: a handler that panicked (`plugin handler panicked` from the crate), or a host-side failure answering a `host.*` request. |
| `-32000` | Application error: the method's own failure, with a message for the user. `Archive operation cancelled` after a `cancel`. |

A backend error reaches the UI half as the exception of `InvokeBackendAsync`, with the error's `message` as its
message, and a command that lets it propagate shows that message in SumaFile's message bar. Write messages for the
user.

### Host methods

Requests the backend sends to the host. `params` is always an object.

| Method | Params | Result | Errors (`-32602` unless noted) |
| --- | --- | --- | --- |
| `host.get_settings` | `{ "keys": ["<key>", ...] }` | `{ "values": { "<key>": "<value>" or null } }` | `'keys' must be an array.`, `Invalid settings key.` |
| `host.set_setting` | `{ "key": "<key>", "value": "<value>" }` | `null` | `'key' must be a string.`, `'value' must be a string.`, `Invalid settings key.` |
| `host.progress_begin` | `{ "title": "<row title>" }` | `{ "operationId": "<id>" }` | `'title' must be a string.` |
| `host.progress_report` | `{ "operationId", "current": <int64>, "total": <int64>, "detail"?: "<text>" }` | `null` | `'current' must be an integer.`, `'total' must be an integer.`, `Unknown operationId.` |
| `host.progress_complete` | `{ "operationId" }` | `null` | `Unknown operationId.` |
| `host.progress_fail` | `{ "operationId", "message": "<text>" }` | `null` | `'message' must be a string.`, `Unknown operationId.` |
| `host.log` | `{ "level": "info" or "warn" or "error", "message": "<text>" }` | `null` | `'level' must be a string.`, `'level' must be info, warn or error.`, `'message' must be a string.` |
| `host.notify` | `{ "message": "<text>", "isError"?: true }` | `null` | `'message' must be a string.` |

- Every method that takes `operationId` also fails with `'operationId' must be a string.`, and a missing or
  non-object `params` fails like a missing field.
- Settings keys follow `^[a-z0-9][a-zA-Z0-9_-]{0,63}$` and are the plugin's own keys (see
  [Declarative settings](#declarative-settings)). An unset key reads as `null`.
- `host.progress_begin` adds a row to SumaFile's Transfers window, titled `title`, with a Cancel button; the returned
  id is opaque. Report with `current <= total` (`total` 0 means unknown) and end the row with
  `host.progress_complete` or `host.progress_fail`. Only ids this backend began are accepted; when the backend stops,
  its open rows fail with `Plugin backend stopped.` After the user presses Cancel, the host sends `cancel` with the
  row's id, and the row reads Cancelling until the backend ends it, or 30 s pass.
- `host.log` writes `[<level>] <message>` to `plugins.log`, tagged with the plugin id.
- `host.notify` shows the message in SumaFile's message bar, titled with the plugin's name, as an error when `isError`
  is `true`.
- A host-side failure (the settings store could not be written, say) fails with `-32603` and its message.
- The host sets no deadline on its answers; the crate gives each `host.*` call 30 s (`DEFAULT_CALL_TIMEOUT`).

### Archive engines

Requests the file service sends to a backend whose plugin declares `archiveFormats`. The service holds up to 8
connections to the backend and keeps at most one call in flight on each, so a listing never waits behind a long
extract. `archivePath`, `destination`, `baseDir` and the `paths` of `archive.create` are full Windows paths. Entry
paths (`innerPath`, `newPath`, `innerPaths`, the `paths` of `archive.add` and the listed `path`) are `/`-separated and
relative to the archive root.

| Method | Params | Result | Deadline |
| --- | --- | --- | --- |
| `archive.list` | `{ archivePath }` | `{ "entries": [{ "path", "isDir", "size", "compressedSize" }] }` | 30 s |
| `archive.extract` | `{ archivePath, destination, operationId? }` | `null` | 600 s idle |
| `archive.read_entry` | `{ archivePath, innerPath, destination, maxBytes, maxEntries, operationId? }` | `null` | 120 s idle |
| `archive.create` | `{ archivePath, paths, operationId? }` | `null` | 600 s idle |
| `archive.rename` | `{ archivePath, innerPath, newPath, operationId? }` | `null` | 600 s idle |
| `archive.delete` | `{ archivePath, innerPaths, operationId? }` | `null` | 600 s idle |
| `archive.add` | `{ archivePath, baseDir, paths, operationId? }` | `null` | 600 s idle |

- `archive.list` entries need `path`; `isDir` defaults to `false`, `size` and `compressedSize` to 0.
- `innerPath` matches the entry and its descendants by whole segments, so `docs` never matches `docs2`.
- `destination` for `archive.extract` and `archive.read_entry` is an empty folder the service created. Refuse a
  non-empty destination and never write outside it.
- `archive.read_entry` writes the selection at its own relative paths inside `destination`. It refuses a selection
  over `maxEntries` entries or `maxBytes` bytes by declared sizes, and fails with
  `Archive entry not found: <innerPath>` when nothing matches.
- `archive.create` makes a new archive at `archivePath`, which does not exist yet, from the files and folders in
  `paths`. It must fail rather than append.
- `archive.rename` moves an entry and its descendants to the full path `newPath` (which covers moves inside the
  archive). `archive.rename`, `archive.delete` and `archive.add` change the archive in place and atomically: on failure
  or cancel the archive is left as it was.
- `archive.add` stores `baseDir\<p>` (a file or a tree) at the entry path `<p>`. The caller guarantees that nothing
  but parent folders exists at those paths.
- `operationId` is optional in the schema (`string?` in `ipc/schema/plugin/v1/protocol.json`), but SumaFile's service
  sends one with every call except `archive.list`: the operation's own id, or `svc-<pid>-<n>`. An engine should still
  accept a call without one, and then reports no progress for it and cannot be cancelled. While working, send the notification `archive.progress` with
  `{ operationId, current, total, currentItem? }` on the connection that carries the request, at least every 5 s,
  with `current <= total` (`total` 0 means unknown). Each one restarts the call's idle deadline, and the service shows it as the operation's progress.
- A call that passes its deadline fails and the service closes that connection. After a `cancel`, the service waits
  at most 5 s for the cancelled call's reply.

### The Rust helper crate

`crates/sumafile-plugin` (the `sumafile-plugin` crate, see [Packages](#packages)) owns the transport, so a backend only
answers requests:

```rust
use std::sync::Arc;

use serde_json::{json, Value};
use sumafile_plugin::{async_trait, serve, Args, HostClient, PluginError, PluginHandler, PluginInfo};

struct Handler;

#[async_trait]
impl PluginHandler for Handler {
    async fn handle(&self, method: &str, params: Value, host: &HostClient) -> Result<Value, PluginError> {
        match method {
            "example.echo" => {
                let text = params["text"]
                    .as_str()
                    .ok_or_else(|| PluginError::InvalidParams("text must be a string".into()))?;
                host.log("info", "echo").await.ok();
                Ok(json!({ "text": text, "length": text.chars().count() }))
            }
            _ => Err(PluginError::MethodNotFound),
        }
    }
}

#[tokio::main]
async fn main() {
    let args = Args::parse(std::env::args().skip(1)).unwrap_or_else(|message| {
        eprintln!("{message}");
        std::process::exit(2);
    });
    let info = PluginInfo { id: "com.example.tool".into(), version: env!("CARGO_PKG_VERSION").into() };
    if let Err(message) = serve(args, info, false, Arc::new(Handler)).await {
        eprintln!("{message}");
        std::process::exit(1);
    }
}
```

- `Args::parse` reads `--pipe-name` and `--data-dir` (both required) and keeps the rest in `Args::rest`.
- `serve(args, info, accepts_multiple_clients, handler)` serves the pipe, answers `plugin.handshake` and `cancel`
  itself, runs every other request on its own task, and returns when the last client disconnects. Pass `true` for an
  archive engine. It needs Windows; elsewhere it fails at once, but handlers and their tests build and run anywhere.
- `PluginError::InvalidParams` is `-32602`, `PluginError::Application` is `-32000`, `PluginError::MethodNotFound` is
  `-32601`; a panic is answered with `-32603`.
- `HostClient` makes the `host.*` calls (`get_settings`, `set_setting`, `progress_begin`, `progress_report`,
  `progress_complete`, `progress_fail`, `log`, `notify`), sends events (`emit`) and archive progress
  (`archive_progress`), and reads cancel flags (`is_cancelled(operation_id)`). On the service connection,
  `host_connection()` returns the host's connection, for settings.
- With the `test-support` feature, `HostClient::fake(settings)` is an in-memory host for handler tests.

The crate's README (`crates/sumafile-plugin/README.md`) has the details. The template's backend
(`templates/sumafile-plugin/backend`) is a complete example with tests.

### Backends in other languages

Implement [Framing](#framing), [Handshake](#handshake), [Cancellation](#cancellation) and the error codes directly:

1. Parse `--pipe-name` and `--data-dir`; create `\\.\pipe\<name>` as the first instance (and more instances as clients
   connect, when `acceptsMultipleClients` is `true`).
2. Read frames in a loop. Answer `plugin.handshake` with `pluginId`, `pluginVersion` and `sdkVersion` `1.0.0`, and
   answer every other request before it with `-32600`.
3. Run each request concurrently and keep reading; reply with the request's `id`.
4. Keep a backend-wide set of in-flight operation ids and their cancel flags.
5. Use integer ids for `host.*` requests and match the host's replies by id.
6. Exit when the last client disconnects.

## Limits and deadlines

| What | Limit | Where |
| --- | --- | --- |
| `plugin.json` size | 1 MiB | discovery and the installer |
| `signature.json` size | 1 MiB | discovery |
| `.sfplugin` package | 10,000 entries, 512 MiB uncompressed | the installer |
| Manifest strings | see [String limits](#string-limits) | the manifest reader |
| Discovery and contribution rendering at startup | design target 50 ms for ten plugins; CI checks its four-plugin smoke session against 50 ms | no plugin code runs in it |
| Pipe frame | 80 MiB (83,886,080 bytes), either direction | host, service and crate |
| Backend connect | 5 s from the launch | host |
| Handshake | 5 s | host and service |
| Failed backend starts | 3 within 60 s mark the plugin Faulted | host |
| `InvokeBackendAsync` | no deadline; the token ends the wait | host |
| `host.*` calls from the crate | 30 s (`DEFAULT_CALL_TIMEOUT`) | crate |
| Archive engine registration | 3 attempts of 15 s, 1 s and then 3 s apart; then Faulted | host |
| `archive.list` | 30 s | service |
| `archive.read_entry` | 120 s idle | service |
| Other `archive.*` methods | 600 s idle | service |
| `cancel` to an archive engine, and the wait for the cancelled call | 5 s each | service |
| A free archive connection | 30 s wait, then `plugin <id> is busy` | service |
| Row decorations | 250 ms before the folder shows; a later result applies up to 30 s after the request | host |
| Activation finishing after a stop | 5 s | host |
| `DeactivateAsync` | 5 s | host |
| Closing attached windows | 5 s | host |
| Killed backend's exit | 2 s | host |
| Progress row after Cancel | Cancelling for at most 30 s | host |
| Legacy settings migration before activation | 5 s | host |
| Early cancels kept | 64 | crate |
| `plugins.log` | rotates to `plugins.log.1` at 1 MB | host |

## Where SumaFile keeps plugin files

Every plugin file lives in SumaFile's installation folder, next to `SumaFile.exe`:

| Item | Location | Override (tests and development) |
| --- | --- | --- |
| Plugin root | `<install folder>\plugins\<plugin id>\` | `SUMAFILE_PLUGIN_ROOTS`: semicolon-separated roots that replace the default |
| Bundled plugin list | `<install folder>\plugins\bundled.json` | none |
| Backend data folders | `<install folder>\plugins-data\<plugin id>\` (with `tmp\`) | `SUMAFILE_PLUGIN_DATA_ROOT` |
| Approvals | `<install folder>\plugin-approvals.json` | `SUMAFILE_PLUGIN_APPROVALS_PATH` |
| Plugin log | `<install folder>\plugins.log`, rotating to `plugins.log.1` at 1 MB | `SUMAFILE_PLUGIN_LOG_PATH` |

- The plugins folder holds the plugins that ship with SumaFile and the ones the user installs. Settings > Plugins >
  Open plugins folder opens it; copying a plugin folder into it and choosing Rescan works as well as Install from file.
- `SUMAFILE_PLUGIN_ROOTS` is for development and tests. It replaces the default, so include the app's `plugins`
  folder if you want the bundled plugins too. When it names several roots, a plugin id found in more than one resolves
  to the copy in the later root, and Settings lists the other as overridden. Install from file still writes to the
  app's own `plugins` folder.
- `plugins.log` gets a `[warn]` line for each override in effect, every load error, the plugin's log lines and its
  backend's stderr, each tagged with the plugin id.
- When the installation folder is not writable (a portable copy under `Program Files`, say), installing, enabling and
  starting a backend fail with a message naming the folder; nothing falls back to another folder.
- Upgrades and reinstalls keep all of these. Uninstalling SumaFile (the setup's uninstaller or the MSI) removes them:
  the user's plugins, `plugins-data`, `plugin-approvals.json` and `plugins.log`. Secrets a backend stored elsewhere,
  such as the FTP/SFTP plugin's passwords in Windows Credential Manager (`SumaFile.Remote.*`), stay where they are.
- The `SUMAFILE_PLUGIN_*` overrides are honoured in release builds too, and each one in effect is logged as a warning.

## Trust and consent

There is no sandbox, so SumaFile asks before it runs a plugin.

- **Consent.** A plugin SumaFile has not been told to trust waits for consent. After startup SumaFile asks about each
  one in turn: **Enable plugin?**, with its name, publisher, version and id, its signature state, its `permissions`,
  and whether it runs a background process. **Enable** enables it; **Keep disabled** leaves it waiting, with a
  **Review…** button on its page in Settings > Plugins that asks again.
- **The approval store.** Enable and disable decisions go to `plugin-approvals.json`, one record per plugin id: the
  SHA-256 of the `plugin.json` bytes, the signature state, the decision, the time and the plugin folder.
- **Change detection.** A decision holds only while the plugin's `plugin.json` hash, its signature state and its
  folder match the record. When any of them changes, the plugin waits for consent again. Install from file and Remove
  clear the plugin's stored decision, so the next scan asks again. Other files are not hashed for this: an unsigned
  plugin whose DLL changes keeps its approval (whoever can write to the installation folder already controls
  SumaFile). Change `version` in `plugin.json` with every
  release, so users confirm each release; sign the plugin so that any change to a listed file breaks the signature.
- **Bundled plugins.** The release build writes `plugins\bundled.json` with the id and `plugin.json` SHA-256 of each
  plugin that ships with SumaFile (`{"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"<64 hex>"}]}`). A listed
  plugin found in the app's own `plugins` folder with that hash is enabled without consent, unless the user disabled
  it. A copy in another root, or with another hash, needs consent. A package can never replace a bundled plugin, and
  Remove is never offered for one.
- **Compatibility.** A plugin whose `sdkVersion` major is not `1`, or whose minor is above the host SDK's minor (`0`),
  is listed as not compatible and never loads.
- **Backend pipes.** The host checks that the server of a plugin's pipe is the backend process it launched and that
  it runs as the current user.

## Signing

A signature lets the consent dialog and the plugin's Settings page show **Signed · key <fingerprint>**. It proves that the
plugin's files are unchanged since they were signed with that key. It does not prove who the publisher is: the key
comes from the plugin's own `plugin.json`, so anyone can sign a plugin with a key of their own. Unsigned plugins and
plugins with an invalid signature can still be enabled; the dialog says **Unsigned plugin** or **Invalid signature:
<reason>**.

The host verifies the signature when it scans plugins (at startup and on Rescan), not on each load. Verification
hashes every listed file at scan time, so a very large signed plugin adds to startup (see
[Size and the startup budget](#size-and-the-startup-budget)).

`signature.json` in the plugin folder:

```json
{
  "algorithm": "ed25519",
  "files": { "Example.Plugin.dll": "<sha256 hex>", "assets/icon.png": "<sha256 hex>" },
  "signature": "<base64 of 64 bytes>"
}
```

- `files` lists files as bare forward-slash relative paths with their SHA-256 hex. It must not list `plugin.json` or
  `signature.json`, and it must cover the declared `ui.assembly` and `backend.executable`. Any other file in the plugin
  folder that it does not list makes the signature Invalid, so sign the folder exactly as you ship it.
- `signature.json` is at most 1 MiB, UTF-8 (a leading byte order mark is accepted).
- `signature` is Ed25519 over the canonical payload: the `plugin.json` bytes, `"\n"`, then `"<path>\n<hash>\n"` for
  each file in ordinal path order.
- `publisher.publicKey` in `plugin.json` is the raw 32-byte public key in base64. The fingerprint SumaFile shows is the
  first 16 hex characters of its SHA-256.

`scripts/sign-plugin.mjs` (Node 22, no dependencies; `npm run sign:plugin -- <args>` in this repository) writes exactly
what the host checks:

```powershell
# Once: a key pair, in a folder outside every repository.
node scripts\sign-plugin.mjs keygen --out $HOME\sumafile-keys
# Put the printed publisher.publicKey into plugin.json, build the folder you ship, then sign it last:
node scripts\sign-plugin.mjs sign target\template-plugins\com.example.template --key $HOME\sumafile-keys\signing-key.pem
node scripts\sign-plugin.mjs verify target\template-plugins\com.example.template
```

- `keygen --out <dir> [--allow-repository]` writes `<dir>\signing-key.pem` (PKCS#8, mode 0600 where the OS has
  modes) and prints `publisher.publicKey` and the fingerprint. It never overwrites a key, and it refuses a folder inside
  a git work tree unless `--allow-repository` is passed (for an ignored folder that is never pushed).
- `sign <plugin folder> --key <pem>` refuses a key that does not match `publisher.publicKey`, a declared
  `ui.assembly` or `backend.executable` that is not in the folder, symbolic links, paths the host cannot open and two
  paths that differ only in case. It hashes every other file and writes `signature.json`.
- `verify <plugin folder>` repeats the host's checks in the host's order and exits 0 only for a valid signature. A
  file in the folder that `signature.json` does not list makes the result Invalid, as it does in SumaFile, and so does a
  `signature.json` larger than 1 MiB. `sign` compares the decoded 32 key bytes with `publisher.publicKey`, so
  whitespace in the manifest's key does not matter.

Adding `publicKey` changes `plugin.json`, and so its hash: users confirm the plugin again. Sign after every build,
since any change to a listed file breaks the signature.

## Packaging and installing a `.sfplugin`

A `.sfplugin` is a zip of the plugin folder's contents, with `plugin.json` at the root of the zip (not inside a
folder named after the id):

```powershell
$folder = "target\template-plugins\com.example.template"
Compress-Archive -Path "$folder\*" -DestinationPath target\com.example.template.zip -Force
Move-Item target\com.example.template.zip target\com.example.template-0.1.0.sfplugin -Force
```

Settings > Plugins > **Install from file…** installs one into `<install folder>\plugins\<id>\`. The installer refuses,
without writing anything:

- a file that is not a readable zip;
- a package without `plugin.json` at its root;
- a `plugin.json` larger than 1 MiB, by its declared or its actual size;
- an invalid `plugin.json`, with the reader's errors (an incompatible `sdkVersion` included);
- a package whose id `bundled.json` lists (`<id> ships with SumaFile and cannot be installed from a package.`);
- more than 10,000 entries, or more than 512 MiB uncompressed;
- an entry with an absolute path, a drive or `:`, or a `..` segment, or one that would land outside the plugin folder;
- an entry with a Windows device name as a segment (`CON`, `PRN`, `AUX`, `NUL`, `COM1`, `LPT1` and the like, with or
  without an extension), or a segment that ends in a dot or a space.

It extracts into a hidden staging folder and moves it into place. An existing copy of the plugin is replaced: it is
renamed aside first and put back if the move fails, and a copy whose files are in use fails with a message asking the
user to disable the plugin and restart SumaFile. Installing clears the plugin's stored approval, so the installed
plugin waits for consent, even when it replaces an approved copy. Ship a new version as a new package with a new
`version`.

**Remove** on a plugin's Settings page deletes its folder, after a confirmation. It is offered only for plugins in SumaFile's
own `plugins` folder that `bundled.json` does not list; bundled plugins are disabled instead. Remove stops the plugin
first, clears its stored approval, and keeps its settings and its `plugins-data` folder.

## The plugin template

`templates/sumafile-plugin` is a copyable starter, `com.example.template`, with both halves:

- `plugin.json`: one command (**Example: echo the greeting**), one declarative setting (**Greeting**) and a
  `first-call` backend;
- `ui/Example.Plugin`: the UI half (`net10.0-windows`, WinUI-free), whose command reads the setting, calls the
  backend's `example.echo` and shows the reply with `IPluginDialogs.Notify`;
- `backend/`: `example-plugin-backend`, on the `sumafile-plugin` crate, serving `example.echo { text } -> { text,
  length }`, with handler tests on `HostClient::fake`;
- `stage.ps1`: builds both halves and stages `target\template-plugins\com.example.template`.

Inside this repository the template builds with everything else (the backend is a Cargo workspace member, the UI
project is in `src-winui/SimpleFile.sln`), and `SimpleFile.Tests` loads it through the real plugin host. To try it in
SumaFile, stage it, point `SUMAFILE_PLUGIN_ROOTS` at `target\template-plugins`, start SumaFile and enable it. Its
README, `templates/sumafile-plugin/README.md`, walks through building, staging, consent, signing, packaging, renaming
it, and building a copy outside this repository against a local SumaFile checkout (see [Packages](#packages)).

## Packages

The SDK and the helper crate are packaged, but only as dry runs: neither is published yet.

- **`SumaFile.PluginSdk`** (NuGet), from `src-winui/SimpleFile.PluginSdk`. `npm run pack:sdk` (`dotnet pack
  src-winui/SimpleFile.PluginSdk -c Release -o target/packages`) writes `target/packages/SumaFile.PluginSdk.1.0.0.nupkg`
  with `lib/net10.0-windows7.0/SimpleFile.PluginSdk.dll`, its XML documentation and `README.md`. The package version
  is the SDK contract version, `PluginSdk.Version`; the assembly keeps the name `SimpleFile.PluginSdk`. CI runs the
  pack after the WinUI tests.
- **`sumafile-plugin`** (crates.io), from `crates/sumafile-plugin`. It depends on `simplefile-ipc`, so the two are
  packaged together: `npm run pack:crates` (`cargo package --locked --allow-dirty -p simplefile-ipc -p
  sumafile-plugin`) builds each package from its packaged sources against a temporary local registry. CI runs it on a
  clean checkout without `--allow-dirty`.

Until they are published, a plugin outside this repository builds against a local SumaFile checkout
(`<checkout>`):

1. Run `npm run pack:sdk` in the checkout; it writes `<checkout>/target/packages/SumaFile.PluginSdk.1.0.0.nupkg`.
2. Add a `nuget.config` beside the plugin's project (or any folder above it) with that folder as a package source,
   here for a checkout in `C:\src\SumaFile`:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="sumafile-local" value="C:\src\SumaFile\target\packages" />
     </packageSources>
   </configuration>
   ```

3. Reference the package compile-only, as in [Project setup](#project-setup) (the template's in-repository
   `ProjectReference` has `<Private>false</Private>` and `<ExcludeAssets>runtime</ExcludeAssets>`; the package
   reference is its equivalent), then run `dotnet restore --force-evaluate` when the project has a
   `packages.lock.json`:

   ```xml
   <PackageReference Include="SumaFile.PluginSdk" Version="1.0.0" ExcludeAssets="runtime" PrivateAssets="all" />
   ```

   NuGet caches a package by id and version: after packing the SDK again at the same version, delete
   `%USERPROFILE%\.nuget\packages\sumafile.pluginsdk\1.0.0` before restoring.
4. Take the crate by path, in `[dependencies]` and, with `features = ["test-support"]`, in `[dev-dependencies]`. It
   finds `simplefile-ipc` in the checkout by itself, and it needs Rust 1.98.0 or newer (the checkout's
   `rust-toolchain.toml` does not apply outside it):

   ```toml
   sumafile-plugin = { path = "<checkout>/crates/sumafile-plugin" }
   ```

Once the packages are published, drop the local source and use `sumafile-plugin = "1"`. The template's README,
`templates/sumafile-plugin/README.md` ("Outside this repository"), walks through both with the template's files.

## Plugin states and troubleshooting

Settings > Plugins lists every plugin found with its state, and each plugin's own page under it repeats the state
with its details:

| State | Shown as | Meaning |
| --- | --- | --- |
| NeedsConsent | Waiting for your approval | Found, not yet approved; **Review…** asks. |
| Enabled | Enabled (· background process running) | Contributions are shown; code loads on first use. |
| Disabled | Disabled | The user turned it off. |
| InvalidManifest | Invalid plugin.json | The manifest broke a rule; the plugin's page lists the errors. |
| IncompatibleSdk | Not compatible with this version of SumaFile | The `sdkVersion` major is not `1`, or its minor is above the host SDK's. |
| LoadFailed | Failed to load: <message> | The UI assembly or entry type could not load, `ActivateAsync` threw, or a provider failed. Disable and enable it, or rescan, to try again. |
| Faulted | Stopped: <message> | The backend stopped, failed to start three times within 60 s, or its archive engine could not register. The status center offers Restart. |
| Shadowed | Overridden by <folder> | The same id is in a later root (only with `SUMAFILE_PLUGIN_ROOTS`). |

`plugins.log` has the details of every failure. Rescan reads every manifest again; an enabled plugin whose
manifest and signature state did not change keeps running.

## Host-shared assemblies

A plugin's UI assembly loads in its own assembly load context. An assembly whose name starts with one of these
prefixes is shared with SumaFile, so the types a plugin uses from it are the host's types. Every other managed
assembly the plugin's `deps.json` lists loads from the plugin folder. The third column says where a shared name
comes from:

- **SumaFile only**: always SumaFile's copy, never one from the plugin folder. A plugin that needs an assembly of
  such a name SumaFile does not have fails to load it.
- **SumaFile first**: SumaFile's copy when SumaFile has one (one of its trusted platform assemblies, or an assembly
  already loaded into its default context); otherwise the plugin's own copy, when its `deps.json` lists one. So a
  plugin can bundle `System.Reactive` or a `Microsoft.Extensions.*` package SumaFile does not ship, but never
  replaces one SumaFile has: a bundled `System.Text.Json` is ignored, even a newer one.

`PluginLoadContext_SharedPrefixes_MatchTheTableInPluginsDoc` checks this table against the host, so it is the
complete list.

| Prefix | What it covers | Comes from |
| --- | --- | --- |
| `SimpleFile.PluginSdk` | The SDK, the `SumaFile.PluginSdk` package. | SumaFile only |
| `Microsoft.WindowsAppSDK` | The Windows App SDK's own assemblies. | SumaFile only |
| `Microsoft.WindowsAppRuntime` | The Windows App SDK runtime's assemblies. | SumaFile only |
| `Microsoft.UI` | WinUI 3 (`Microsoft.UI.Xaml` and the rest of `Microsoft.UI`). | SumaFile only |
| `Microsoft.WinUI` | The WinUI 3 projection. | SumaFile only |
| `Microsoft.InteractiveExperiences` | The Windows App SDK's input and windowing projection. | SumaFile only |
| `Microsoft.Graphics.Display` | The Windows App SDK's display information. | SumaFile only |
| `Microsoft.Graphics.Imaging` | The Windows App SDK's imaging projection. Win2D (`Microsoft.Graphics.Canvas`) is not shared, so its managed assemblies load from the plugin's folder; running Win2D from a plugin is not tested (CsWinRT may look for the native `Microsoft.Graphics.Canvas.dll` next to SumaFile, so preload it by full path in `ActivateAsync` if needed). | SumaFile only |
| `Microsoft.Web.WebView2` | WebView2. | SumaFile only |
| `Microsoft.Security.Authentication.OAuth` | The Windows App SDK's OAuth projection. | SumaFile only |
| `Microsoft.Windows.` | The Windows App SDK's `Microsoft.Windows.*` projections and the Windows SDK projection (`Microsoft.Windows.SDK.NET`). The dot matters: `Microsoft.WindowsAzure.*` is not shared. | SumaFile only |
| `WinRT` | The C#/WinRT runtime. | SumaFile only |
| `CommunityToolkit.Mvvm` | The MVVM Toolkit. | SumaFile only |
| `System.` | The .NET runtime libraries. | SumaFile first |
| `Microsoft.Extensions.` | The `Microsoft.Extensions.*` libraries. | SumaFile first |

So a plugin is bound to the versions SumaFile ships of the packages behind these assemblies. Reference them
compile-only, at the versions below or lower, so no copy goes into the plugin folder:

```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" ExcludeAssets="runtime" PrivateAssets="all" />
```

| Package | Version SumaFile ships |
| --- | --- |
| `Microsoft.WindowsAppSDK` | `2.4.0` |
| `Microsoft.Windows.SDK.BuildTools` | `10.0.28000.2526` |
| `CommunityToolkit.Mvvm` | `8.4.0` |

A plugin built against a newer version than SumaFile ships can fail to load, or fail when it first calls a member
the older version lacks. The same test checks these versions against the ones SumaFile builds with.

Native DLLs follow one rule. A native DLL the plugin's `deps.json` lists loads from the plugin folder, unless
SumaFile's own folder ships a DLL of that name. Then SumaFile's copy wins, because SumaFile's code already uses it.
