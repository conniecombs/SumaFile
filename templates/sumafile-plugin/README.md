# SumaFile plugin template

A starter plugin, `com.example.template`, to copy and make your own. It has both halves a SumaFile plugin can have:

- a C# UI half, `ui/Example.Plugin`, which SumaFile loads into its own process;
- a Rust backend, `backend/` (`example-plugin-backend`), which SumaFile starts as a separate process and talks to over
  a named pipe.

It contributes one command, **Example: echo the greeting**, and one setting, **Greeting**. The command reads the
setting, sends it to the backend's `example.echo` method and shows the reply in SumaFile's message bar.

The full author guide, with every manifest field and its limits, every contribution kind, the threading rules and the
backend protocol, is [docs/PLUGINS.md](../../docs/PLUGINS.md). It also holds the rules for consent, signing, packaging
and building outside the repository; this README has the template's commands for each and links to the guide for the
rest. In a copy outside the SumaFile repository those links do not resolve: read `docs/PLUGINS.md` in the SumaFile
checkout the copy builds against.

To build a copy outside the SumaFile repository, see [Outside this repository](#outside-this-repository): the
repository packs the SDK and the helper crate but does not publish them, so a copy builds against a local SumaFile
checkout.

## What is in the folder

```text
templates/sumafile-plugin/
  plugin.json                  the manifest: id, UI assembly, backend, the command and the setting
  stage.ps1                    builds both halves and stages a plugin folder
  ui/
    Directory.Build.props      build settings (shared with SumaFile's src-winui inside this repository)
    Directory.Build.targets
    Example.Plugin/
      Example.Plugin.csproj    net10.0-windows, WinUI-free; the SDK is a compile-only reference
      ExamplePlugin.cs         IPlugin + ICommandHandler: the echo command
      packages.lock.json
  backend/
    Cargo.toml                 example-plugin-backend, on the sumafile-plugin helper crate
    src/main.rs                parses --pipe-name and --data-dir, then serves the pipe
    src/handler.rs             example.echo { text } -> { text, length }, and its tests
```

`plugin.json` declares the backend with `"activateOn": "first-call"`: SumaFile starts it the first time the UI half
calls `InvokeBackendAsync`, not at startup. The UI half needs no WinUI. A plugin that builds WinUI elements (panels,
sidebar sections, a settings page, its own windows) targets `net10.0-windows10.0.19041.0` instead and takes
`Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools` compile-only (`ExcludeAssets="runtime"`,
`PrivateAssets="all"`), as `plugins/git/ui/SumaFile.Git` does. Build that UI in code: compiled XAML does not load from
a plugin folder.

## Build and test

Inside the SumaFile repository the template is part of the build: the backend is a member of the Cargo workspace and
the UI project is in `src-winui/SimpleFile.sln`, so CI builds and tests both. By hand, from the repository root:

```powershell
cargo test -p example-plugin-backend
dotnet build templates\sumafile-plugin\ui\Example.Plugin\Example.Plugin.csproj -c Debug
```

`src-winui/SimpleFile.Tests` also builds the UI project and loads it through SumaFile's real plugin host against a
fake backend (`TemplatePluginTests`).

## Stage a plugin folder

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File templates\sumafile-plugin\stage.ps1
# or: -Configuration Release
```

`stage.ps1` builds both halves, writes a fresh plugin folder and prints its path:

```text
target\template-plugins\com.example.template\
  plugin.json
  Example.Plugin.dll
  Example.Plugin.deps.json
  example-plugin-backend.exe
```

The folder is `template-plugins\com.example.template` under Cargo's target folder. Inside the SumaFile repository
that is `target\`, as shown and as the commands below assume. A copy outside any Cargo workspace stages under its own
`backend\target\` instead: use the path `stage.ps1` prints.

The folder name must be the plugin id. No copy of `SimpleFile.PluginSdk.dll` belongs in it: SumaFile loads its own.
The staged folder is only for development; SumaFile's own build never stages or ships the template.

## Load it in SumaFile

SumaFile scans the `plugins` folder next to `SumaFile.exe`. For development, point `SUMAFILE_PLUGIN_ROOTS` at the
staging folder instead. It is a semicolon-separated list that **replaces** the default, so name the app's own `plugins`
folder too if you want the bundled plugins (7-Zip, FTP/SFTP, Git) alongside yours:

```powershell
$env:SUMAFILE_PLUGIN_ROOTS = (Resolve-Path target\template-plugins).Path
# with the bundled plugins: "<SumaFile folder>\plugins;" + (Resolve-Path target\template-plugins).Path
npm run dev:winui          # or start SumaFile.exe from this PowerShell window
```

`plugins.log` (next to `SumaFile.exe`) gets a `[warn]` line naming the roots in effect, and every load error.

## Consent

SumaFile never runs a plugin it has not been told to trust. The first time it finds the plugin it asks **Enable
plugin?**, showing its name, publisher, version, permissions (`backend-process`), the fact that it runs a backend
process, and its signature state (Unsigned until you sign it). Choose **Enable**. After **Keep disabled**, Settings >
Plugins lists **Example plugin** as waiting for your approval; open the plugin's own page from there and choose
**Review…** to be asked again. Once enabled, the command appears in the command palette (`Ctrl+Shift+P`) and the
**Greeting** setting on the plugin's page under Settings > Plugins.

The plugin waits for consent again when its `plugin.json`, its signature state or its folder changes, and after it is
installed from a file or removed. The guide has the rules under
[Trust and consent](../../docs/PLUGINS.md#trust-and-consent).

## Sign it

A signature lets the consent dialog and the plugin's page in Settings show **Signed · key <fingerprint>**; an unsigned
plugin can still be enabled. The repository's signing tool is `scripts/sign-plugin.mjs`, one Node.js file with no
dependencies (`.node-version` in the repository pins the Node.js version its CI uses).

```powershell
# Once: a key pair. Keep signing-key.pem private, in a folder outside every repository (keygen refuses a git work tree).
node scripts\sign-plugin.mjs keygen --out $HOME\sumafile-keys
```

`keygen` prints `publisher.publicKey`. Put it in `plugin.json`:

```json
"publisher": { "name": "Example Publisher", "publicKey": "<the printed base64 key>" }
```

Then stage the folder again and sign it as the last step. `stage.ps1` starts from an empty folder each time, and any
change to the folder after signing breaks the signature:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File templates\sumafile-plugin\stage.ps1 -Configuration Release
node scripts\sign-plugin.mjs sign target\template-plugins\com.example.template --key $HOME\sumafile-keys\signing-key.pem
node scripts\sign-plugin.mjs verify target\template-plugins\com.example.template
```

`verify` checks the folder the way SumaFile does. What a signature proves, the `signature.json` format and what `sign`
refuses are in the guide under [Signing](../../docs/PLUGINS.md#signing).

## Package it as a `.sfplugin`

A `.sfplugin` is a zip of the plugin folder's contents, with `plugin.json` at the root of the zip (not inside a
`com.example.template` folder). Windows PowerShell's `Compress-Archive` only writes `.zip` names, so rename it after:

```powershell
$folder = "target\template-plugins\com.example.template"
Compress-Archive -Path "$folder\*" -DestinationPath target\com.example.template.zip -Force
Move-Item target\com.example.template.zip target\com.example.template-0.1.0.sfplugin -Force
```

Users install it with Settings > Plugins > Install from file; the plugin then waits for their consent. What the
installer refuses is in the guide under
[Packaging and installing a `.sfplugin`](../../docs/PLUGINS.md#packaging-and-installing-a-sfplugin).

## Make it yours

Rename consistently, then rebuild and re-stage. `stage.ps1` names the UI project, the UI assembly and the backend
executable itself, so it changes with them:

- the id: `plugin.json` `id`, `PLUGIN_ID` in `backend/src/handler.rs`, `EchoCommand` in `ExamplePlugin.cs` and
  `$pluginId` in `stage.ps1` (the staged folder is named after it);
- `name`, `version` (keep `plugin.json`, `backend/Cargo.toml` and `ui/Directory.Build.props` on the same version) and
  `publisher` (`plugin.json`, and `<Authors>` in `ui/Directory.Build.props`);
- the UI assembly: `ui.assembly` in `plugin.json`, `<AssemblyName>` in `Example.Plugin.csproj`, and
  `Example.Plugin.dll` in `stage.ps1`. When the project folder or the `.csproj` file is renamed, `$uiProject` in
  `stage.ps1` (`ui\Example.Plugin\Example.Plugin.csproj`) changes too;
- the entry type: `ui.entryType` in `plugin.json` is the namespace and class name in `ExamplePlugin.cs`
  (`Example.Plugin.ExamplePlugin`); `<RootNamespace>` in `Example.Plugin.csproj` names the same namespace;
- the backend executable: `backend.executable` in `plugin.json`, the `[[bin]]` name in `backend/Cargo.toml` and
  `example-plugin-backend.exe` in `stage.ps1`;
- the methods: add them to `ExampleHandler::handle`, with tests on `HostClient::fake`.

## Outside this repository

Inside SumaFile the template takes the SDK and the helper crate from the repository by path. The repository does not
publish either, so a copy outside it builds against a local SumaFile checkout (`<checkout>` below, such as
`C:\src\SumaFile`). The guide explains these steps under [Packages](../../docs/PLUGINS.md#packages), including the
NuGet cache to clear after packing the SDK again. With the template's files they are:

1. In the checkout, pack the SDK. This writes `<checkout>\target\packages\SumaFile.PluginSdk.1.0.0.nupkg`:

   ```powershell
   cd <checkout>
   npm run pack:sdk
   ```

2. Next to the copy's `plugin.json`, add a `nuget.config` with that folder as a package source:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <add key="sumafile-local" value="C:\src\SumaFile\target\packages" />
     </packageSources>
   </configuration>
   ```

3. In `ui/Example.Plugin/Example.Plugin.csproj`, replace the `ProjectReference` to `SimpleFile.PluginSdk.csproj`
   (the whole element, with its `Private` and `ExcludeAssets`) with the same compile-only reference to the package,
   then run `dotnet restore --force-evaluate` in `ui/Example.Plugin` to rewrite `packages.lock.json`:

   ```xml
   <PackageReference Include="SumaFile.PluginSdk" Version="1.0.0" ExcludeAssets="runtime" PrivateAssets="all" />
   ```

4. In `backend/Cargo.toml`, point both dependencies on `sumafile-plugin` at the checkout's crate. The crate needs
   Rust 1.98.0 or newer, and the checkout's `rust-toolchain.toml` does not apply outside the checkout; with an older
   default toolchain, use `rustup toolchain install 1.98.0` and `cargo +1.98.0 test`:

   ```toml
   [dependencies]
   sumafile-plugin = { path = "C:/src/SumaFile/crates/sumafile-plugin" }

   [dev-dependencies]
   sumafile-plugin = { path = "C:/src/SumaFile/crates/sumafile-plugin", features = ["test-support"] }
   ```

5. Copy `scripts/sign-plugin.mjs` from the checkout; it is a single file with no dependencies.

`ui/Directory.Build.props` and `ui/Directory.Build.targets` import SumaFile's build settings only when they find them;
outside the repository the props file sets the same defaults itself. A copy outside any Cargo workspace builds on its
own, with `backend\target` as its target folder; a copy inside another workspace joins that workspace's `members`.

The SDK version in the package reference is the plugin's `sdkVersion` (`1.0.0`). Which SDK versions a SumaFile build
loads, and the assembly versions a plugin is bound to, are in the guide under
[`plugin.json` reference](../../docs/PLUGINS.md#pluginjson-reference) and
[Host-shared assemblies](../../docs/PLUGINS.md#host-shared-assemblies).
