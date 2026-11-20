# SumaFile WinUI solution

The C# side of SumaFile: the WinUI 3 window, its logic, the named-pipe client
for the Rust file service, and the plugin host. The repository
[README](../README.md) describes the product, and
[Architecture](../docs/ARCHITECTURE.md) describes how the processes fit
together.

## Projects

`SimpleFile.sln` holds the six application projects, the bundled plugins' UI
projects from `../plugins`, the plugin template's UI project and the test
fixture plugin.

| Project | Role |
| --- | --- |
| `SimpleFile.App` | The unpackaged WinUI 3 window: XAML, dialogs, controls, and the Debug-only UI smoke |
| `SimpleFile.Core` | Workspace state and controllers, navigation, transfers, menus, settings, the service's lifetime. No WinUI types |
| `SimpleFile.Ipc` | The length-prefixed JSON-RPC client for the named pipe, binary frame decoding, and the wire models |
| `SimpleFile.PluginSdk` | The contract a plugin's UI assembly is built against; packed as the `SumaFile.PluginSdk` package |
| `SimpleFile.PluginHost` | Plugin discovery, manifests, consent, loading and backend processes |
| `SimpleFile.Tests` | xUnit tests for all of the above |

Target: Windows 10 version 2004 or later and Windows 11, x64,
`net10.0-windows10.0.19041.0`, with the Windows App SDK self-contained.

## Build and test

From the repository root:

```powershell
npm run dev
```

builds the Rust service, builds and stages the bundled plugins, and runs the
app. To build without running, use `npm run build:winui`.

The tests start the real service and load real plugins, so those are built
first:

```powershell
cargo build -p simplefile-service
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugins.ps1
npm run check:winui
```

The app finds `simplefile-service.exe` beside itself or in the Cargo output
folders. Set `SIMPLEFILE_SERVICE_PATH` to use another build.

## Release payload

```powershell
npm run build:winui:release
Start-Process dist\winui\payload\SumaFile.exe
```

writes `dist/winui/`:

- `payload\` with `SumaFile.exe`, `simplefile-service.exe`, the Windows App SDK
  files and the `plugins\` folder
- `SumaFile_<version>_x64-winui-portable.zip`
- `SumaFile_<version>_x64-winui-setup.exe`, when NSIS (`makensis`) is installed
- `SumaFile_<version>_x64-winui.msi`, when WiX 3.14.1 or later is installed
- `latest-winui.json`, the updater manifest

An unpackaged WinUI executable needs `resources.pri` and its `.xbf` files
beside it; the build copies them. If the window never appears, read
`%LOCALAPPDATA%\SumaFile\startup.log`; see [Support](../docs/SUPPORT.md).

The smokes that exercise the payload and the installers are listed in the
repository README under Testing and Verification.
