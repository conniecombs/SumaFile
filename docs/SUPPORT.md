# Support

SumaFile is a Windows file manager. This page says where to report a problem,
what to include, where the logs are, and where in the code a few common
problems start.

## Reporting a problem

Open a [bug report](https://github.com/conniecombs/SumaFile/issues/new/choose).
The template asks for the items below. Report security problems privately
instead; see the [security policy](SECURITY.md).

Include:

- The Windows version and architecture.
- The SumaFile version from **Settings > About**, or the commit when you run
  from source.
- How it was installed: NSIS setup, MSI, portable ZIP, or `npm run dev`.
- The exact path involved, with personal details removed, and whether it is on
  a local disk, removable media, a mapped network drive, or an FTP/SFTP
  connection.
- Which plugin is involved, if any (Git, FTP/SFTP, 7-Zip, or a third-party
  plugin).
- The relevant log lines from the files listed under [Logs](#logs).

When you report from a development checkout, run `npm run check` first and say
whether it passes.

## Logs

All paths are per user. Remove personal paths before sharing a log.

| File | Written by | Contents |
| --- | --- | --- |
| `%LOCALAPPDATA%\SumaFile\startup.log` | The app | Failures while the window starts, with the exception. |
| `%LOCALAPPDATA%\SumaFile\service.log` | The app, from the file service's error output | What the Rust service printed to its error stream. Rotated at 1 MB to `service.log.1`. |
| `%LOCALAPPDATA%\SumaFile\operations.jsonl` | The app | One line per started, completed, cancelled or failed file operation. A failed line carries the service's error text. |
| `%LOCALAPPDATA%\SumaFile\startup-timing.log` | The app | How long each startup stage took. |
| `%LOCALAPPDATA%\SumaFile\window.json` | The app | The main window's size, position and whether it was maximized. Delete it to open at the default size. |
| `plugins.log` in the installation folder | The app | Plugin discovery and loading, what plugins log through the host, and plugin failures. Rotated at 1 MB. |

The error text in `operations.jsonl` is the most useful single item for a
failed copy or move: it names the step, the path and the Windows error.

## Startup problems

- **The window never appears.** Read `startup.log`. In an unpackaged build the
  usual cause is a missing `resources.pri` or a missing `.xbf` file beside
  `SumaFile.exe`. `scripts/build-winui-release.ps1` stages both into
  `dist/winui/payload`.
- **"simplefile-service.exe was not found".** The app starts the Rust service
  as a child process and looks for it beside itself and in the Cargo output
  folders. Build it with `cargo build -p simplefile-service`, or point the
  environment variable `SIMPLEFILE_SERVICE_PATH` at the executable.
- **The service stops while the app is open.** The app starts it again,
  reconnects and shows a message. `service.log` has what the service printed
  before it stopped.
- The service runs in a job object that ends it when the app exits, so no
  service process should outlive the window. Programs opened from SumaFile are
  not part of that job and keep running.

[Architecture](ARCHITECTURE.md) describes the processes and how they find each
other.

## Transfer problems

For a failed or stuck copy or move, include:

- The failed line from `operations.jsonl`.
- The transfer mode and any changed options from **Transfers > Transfer
  defaults** or the task's own Options.
- The kind of source and destination storage, and whether it was a copy or a
  move.
- The counts the failure dialog showed and what you chose there.

A transfer may leave `.sumafile-partial` files, `.state` records or replace
backups in the destination folder. Their names begin with a dot, so SumaFile
lists them only when hidden files are shown. Do not delete them before reading
[Transfers](TRANSFER_TUNING.md#what-a-transfer-can-leave-in-a-destination-folder),
which says what each one is and when removing it is safe.

## UI smoke failures

For a failure of `npm run smoke:winui-ui`, include `report.json` if it was
written and `progress.json` from `test-results/ui-smoke/<run>/`, plus the
workflow run URL when it happened in CI. `progress.json` names the last
scenario that was running when a timeout prevented a final report.
[Runtime verification](WINUI_RUNTIME_VERIFICATION.md) describes the smoke.

## Where problems usually start

| Symptom | Start reading in |
| --- | --- |
| Drive names, mapped network shares, offline drives | `crates/simplefile-core/src/drives.rs` |
| A folder opens in Windows Explorer instead of in the pane | `src-winui/SimpleFile.Core/WorkspaceControllers/WorkspaceNavigationController.cs`, then `crates/simplefile-service/src/shell.rs` |
| A folder lists slowly, rows are missing or stale | `crates/simplefile-service/src/listing_views.rs`, `src-winui/SimpleFile.Core/PaneNavigator.cs` |
| Settings or layout are not kept | `src-winui/SimpleFile.Core/WorkspaceSettingsStore.cs`, `crates/simplefile-core/src/settings_store.rs` |
| Archives | `crates/simplefile-core/src/archive/`; 7-Zip formats in `plugins/sevenzip/` |
| Copy and move | `crates/simplefile-service/src/progress/`, `crates/sumafile-transfer/src/` |
| Git status, FTP/SFTP | `plugins/git/`, `plugins/remote/` |
| Updates and releases | [`.github/RELEASE.md`](../.github/RELEASE.md), [updater](UPDATER_RELEASE.md) |
