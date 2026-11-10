# SumaFile plugin template

A starter plugin, `com.example.template`, to copy and make your own. It has both halves a SumaFile plugin can have:

- a C# UI half, `ui/Example.Plugin`, which SumaFile loads into its own process;
- a Rust backend, `backend/` (`example-plugin-backend`), which SumaFile starts as a separate process and talks to over
  a named pipe.

It contributes one command, **Example: echo the greeting**, and one setting, **Greeting**. The command reads the
setting, sends it to the backend's `example.echo` method and shows the reply in SumaFile's message bar.

The full author guide, with every manifest field and its limits, every contribution kind, the threading rules and the
backend protocol, is [docs/PLUGINS.md](../../docs/PLUGINS.md).

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
the UI project is in `src-winui/SimpleFile.sln`, so CI builds and tests both. By hand:

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

`stage.ps1` builds both halves and writes a fresh plugin folder:

```text
target\template-plugins\com.example.template\
  plugin.json
  Example.Plugin.dll
  Example.Plugin.deps.json
  example-plugin-backend.exe
```

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
Plugins lists **Example plugin** as waiting for your approval, with a **Review…** button that asks again. Once enabled,
the command appears in the command palette (`Ctrl+Shift+P`) and the **Greeting** setting on the plugin's card in
Settings > Plugins.

SumaFile records the approval with the SHA-256 of `plugin.json`. When `plugin.json` changes (a new version, a new
permission, a public key) or the signature state changes, the plugin waits for consent again.

## Sign it

A signature lets the consent dialog show **Verified publisher** with your key's fingerprint; an unsigned plugin can
still be enabled. The repository's signing tool is `scripts/sign-plugin.mjs` (Node 22):

```powershell
# Once: a key pair. Keep signing-key.pem private, in a folder outside every repository (keygen refuses a git work tree).
node scripts\sign-plugin.mjs keygen --out $HOME\sumafile-keys
```

`keygen` writes `signing-key.pem` (PKCS#8) and prints the raw 32-byte public key in base64. Put it in `plugin.json`:

```json
"publisher": { "name": "Example Publisher", "publicKey": "<the printed base64 key>" }
```

Then stage the folder again and sign what you ship, as the last step (any later change to a file breaks the signature):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File templates\sumafile-plugin\stage.ps1 -Configuration Release
node scripts\sign-plugin.mjs sign target\template-plugins\com.example.template --key $HOME\sumafile-keys\signing-key.pem
node scripts\sign-plugin.mjs verify target\template-plugins\com.example.template
```

`sign` refuses a key that does not match `publisher.publicKey`. It writes `signature.json`: the SHA-256 of every file
in the folder except `plugin.json` and `signature.json`, and an Ed25519 signature over `plugin.json` and that list,
the payload SumaFile's verifier checks. `verify` checks the folder the way SumaFile does and prints the fingerprint.
`stage.ps1` always starts from an empty folder, so re-sign after every stage.

## Package it as a `.sfplugin`

A `.sfplugin` is a zip of the plugin folder's contents, with `plugin.json` at the root of the zip (not inside a
`com.example.template` folder). Windows PowerShell's `Compress-Archive` only writes `.zip` names, so rename it after:

```powershell
$folder = "target\template-plugins\com.example.template"
Compress-Archive -Path "$folder\*" -DestinationPath target\com.example.template.zip -Force
Move-Item target\com.example.template.zip target\com.example.template-0.1.0.sfplugin -Force
```

Users install it with Settings > Plugins > Install from file, which unzips it into SumaFile's `plugins` folder; the
plugin then waits for their consent.

## Make it yours

Rename consistently, then rebuild and re-stage:

- the id: `plugin.json` `id`, `PLUGIN_ID` in `backend/src/handler.rs`, `EchoCommand` in `ExamplePlugin.cs` and
  `$pluginId` in `stage.ps1` (the staged folder is named after it);
- `name`, `version` (keep `plugin.json`, `backend/Cargo.toml` and `ui/Directory.Build.props` on the same version) and
  `publisher`;
- the UI assembly and entry type (`ui.assembly`, `ui.entryType`), and the backend executable (`backend.executable`,
  the crate's `[[bin]]` name);
- the methods: add them to `ExampleHandler::handle`, with tests on `HostClient::fake`.

## Outside this repository

Inside SumaFile the template takes the SDK and the helper crate from the repository by path. A standalone copy uses
the published packages instead, once they are published (neither is yet):

- `ui/Example.Plugin/Example.Plugin.csproj`: replace the `ProjectReference` to `SimpleFile.PluginSdk.csproj` with

  ```xml
  <PackageReference Include="SumaFile.PluginSdk" Version="1.0.0" ExcludeAssets="runtime" PrivateAssets="all" />
  ```

  then run `dotnet restore --force-evaluate` to rewrite `packages.lock.json`. `ui/Directory.Build.props` and
  `ui/Directory.Build.targets` import SumaFile's build settings only when they find them, and otherwise set the same
  defaults themselves.
- `backend/Cargo.toml`: replace both path dependencies on `sumafile-plugin` with the published crate:

  ```toml
  [dependencies]
  sumafile-plugin = "1"

  [dev-dependencies]
  sumafile-plugin = { version = "1", features = ["test-support"] }
  ```

  A copy outside any Cargo workspace builds on its own (its target folder is `backend\target`, which `stage.ps1`
  finds through `cargo metadata`); a copy inside another workspace joins that workspace's `members`.
- `scripts/sign-plugin.mjs` is a single Node file with no dependencies: copy it alongside.

The SDK version is the plugin's `sdkVersion` (`1.0.0`); SumaFile loads a plugin whose major version matches its own.
A plugin is bound to the versions of the assemblies SumaFile shares with it (the SDK, WinUI and the Windows App SDK,
`CommunityToolkit.Mvvm`); docs/PLUGINS.md lists them.
