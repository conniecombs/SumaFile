# Startup fix notes

This file records Windows startup diagnostics for the shipping WinUI 3 host
and Rust IPC service.

## Current host

- `src-winui/SimpleFile.App` is the unpackaged WinUI 3 window.
- `BackendSession` starts `simplefile-service` under a job object
  (`KILL_ON_JOB_CLOSE` plus silent breakaway so opened documents survive)
  and completes `ipc.handshake` before UI work.
- Unpackaged launches need `resources.pri` and `*.xbf` beside `SumaFile.exe`.
  `scripts/build-winui-release.ps1` stages those in `dist/winui/payload`.
- Override the service path with `SIMPLEFILE_SERVICE_PATH` when needed.

## Logs

Host and service panics are appended to:

`%LOCALAPPDATA%\SumaFile\startup.log`

If the window never appears, that file is the first place to look. The usual
unpackaged cause is a missing `resources.pri` or `MainWindow.xbf`.

## Updates

Production updater metadata is `latest-winui.json` on GitHub Releases. See
[`UPDATER_RELEASE.md`](UPDATER_RELEASE.md) and [`.github/RELEASE.md`](../.github/RELEASE.md).
