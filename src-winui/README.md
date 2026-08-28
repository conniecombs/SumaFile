# SumaFile WinUI 3 host

Native Windows file-manager UI. It starts `simplefile-service` (job object
`KILL_ON_JOB_CLOSE` plus silent breakaway so opened documents outlive the
app) and speaks named-pipe JSON-RPC.

`SimpleFile.Ipc` multiplexes request/response, `list_directory.chunk`
notifications, transfer progress, watcher/search notifications, client-side
cancellation, and typed `IpcException`s. `SimpleFile.Core.ExplorerWorkspace`
owns dual-pane navigation, tabs, sidebar, transfers, search, and persistence.

## Projects

| Project | Role |
| --- | --- |
| `SimpleFile.App` | Unpackaged WinUI 3 explorer window |
| `SimpleFile.Ipc` | Length-prefixed JSON-RPC named-pipe client |
| `SimpleFile.Core` | Service lifetime + explorer workspace |
| `SimpleFile.Tests` | Framing, DTO, client, path, and navigation tests |

Target: Windows 10 2004+ / Windows 11 x64, `net10.0-windows10.0.19041.0`, Windows App SDK self-contained.

## Build

From the repository root:

```powershell
cargo build -p simplefile-service
dotnet build src-winui/SimpleFile.sln -c Debug
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug
```

Or:

```powershell
npm run build:winui
npm run check:winui
npm run dev:winui
```

`dev:winui` builds `simplefile-service` then runs `SimpleFile.App`. Override the service path with `SIMPLEFILE_SERVICE_PATH` if needed.

## Runnable folder (Release)

The unpackaged exe needs `resources.pri` (WinUI theme map) and the `*.xbf` pages next to `SumaFile.exe`. A normal `dotnet publish` omitted those; the project now copies them.

```powershell
npm run build:winui:release
Start-Process dist\winui\payload\SumaFile.exe
```

If the window never appears, check `%LOCALAPPDATA%\SumaFile\startup.log`. The usual cause is a missing `resources.pri` or `MainWindow.xbf`.

## Packaging

```powershell
npm run build:winui:release
```

Writes `dist/winui/`:

- `payload\` — `SumaFile.exe` + `simplefile-service.exe` + WASDK files
- `SumaFile_*_x64-winui-portable.zip`
- `SumaFile_*_x64-winui-setup.exe` (NSIS, if `makensis` is installed)
- `SumaFile_*_x64-winui.msi` (WiX v3, if `candle`/`heat`/`light` are installed)
- `latest-winui.json`

Smokes: `npm run smoke:winui`, `npm run smoke:winui-msi`, `npm run smoke:winui-installer`.
