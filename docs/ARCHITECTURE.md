# Architecture

SumaFile is a Windows file manager made of a WinUI 3 app in C#, a Rust file service the app starts
as a child process, and plugin backends that run as further child processes. This document says
which process does what, where each kind of change goes, and which contracts a change must keep.
[Contributing](CONTRIBUTING.md) covers tools and checks; the [README](../README.md) the product.

## Overview

```text
   SumaFile.exe  (C#, WinUI 3): the window, the workspace, the IPC client, the plugin host
   and the plugins' UI assemblies
        | client                                 | client, one pipe per plugin
        | \\.\pipe\SumaFile.{session}.{pid}      | \\.\pipe\SumaFile.Plugin.{session}.{pid}.{id}
        v server                                 v server
   simplefile-service.exe  ---- client ---->  plugin backend processes
   session, dispatch, listing views,          sumafile-git-backend.exe
   transfers, search, watcher                 sumafile-remote-backend.exe
   (links simplefile-core)                    sumafile-sevenzip-backend.exe
```

Every connection carries length-prefixed JSON-RPC 2.0 over a Windows named pipe. The app is a
client of the service and of each plugin backend; the service is also a client of a backend that
serves an archive format.

The Rust side owns everything that reads or changes files and everything that is stored: folder
enumeration, sorting and filtering, file operations, transfers, archives, search, the folder
watcher, thumbnails, metadata, tags, the settings database and the updater. The C# side owns what
is on screen and the state behind it: panes, tabs, selection, commands, menus, dialogs, preview
rendering and the plugin host; its settings go through the service. The app keeps a few jobs that
need the UI process or WinRT: shell icons (`ShellIconLoader`), video thumbnail frames
(`VideoThumbnailExtractor`), the Windows Search index (`WindowsSearchService`), Open With
discovery, the Windows clipboard and the folder picker.

The split and the transport come from the August 2026 design,
[winui-migration/architecture.md](winui-migration/architecture.md), kept as history; it no longer
describes the code. Its reasons still hold: a pipe is local and limited to one user, methods that
return a value or a message map onto JSON-RPC, request ids allow concurrent calls, and a length
prefix lets a reader refuse a frame before allocating for it. It rejected gRPC (a second toolchain
and a port) and an in-process Rust library (a panic would take the window down).

## Processes and lifetime

### Starting the file service

`BackendSession.StartAsync` (`SimpleFile.Core`) runs when the main window is first activated.

1. `ServiceLocator.FindServiceExecutable` returns `SIMPLEFILE_SERVICE_PATH` when it names a file.
   Otherwise it looks in the app's folder and each parent, then the current directory and each
   parent, for `target\debug\simplefile-service.exe`, `target\release\simplefile-service.exe` or
   `simplefile-service.exe`.
2. It creates a job object (`JobObject.Create`), starts the service with
   `--pipe-name SumaFile.{sessionId}.{pid} --parent-pid {pid}` and assigns it to the job. A service
   that cannot be put in the job is killed and the start fails.
3. It writes a new random 32-byte token, as hex, to the service's standard input and closes it.
   The service refuses to listen without a token and rejects `--auth-token` on the command line.
4. `NamedPipeJsonClient.ConnectAsync` waits for the pipe for `Protocol.ConnectTimeout` (2 s in
   Release, 5 s in Debug). The session then calls `ipc.handshake`, `ipc.health`, `get_app_version`,
   `get_home_dir` and `list_drives` with `mode: "light"`, and keeps the answers for the workspace.

The pipe name carries the Windows session id and the app's process id, so each app process has its
own service; a service process serves one connection and exits when it ends.
`crates/simplefile-service/src/main.rs` creates the pipe with `FILE_FLAG_FIRST_PIPE_INSTANCE`, one
instance and `PIPE_REJECT_REMOTE_CLIENTS`, then sets its access control list to
`D:P(A;;GA;;;{sid})`, which grants access to the current user only. A client must still present
the token in `ipc.handshake`. The service compares it in constant time, accepts at most 64 KiB per
frame until the handshake succeeds (`PRE_HANDSHAKE_FRAME_LIMIT`) and closes the session after five
wrong tokens (`MAX_HANDSHAKE_FAILURES`).

### When either side stops

- **The app exits or crashes.** The job object has `KILL_ON_JOB_CLOSE`, so the service dies with
  the app's handle to the job. The service also holds a handle to the process named by
  `--parent-pid` and exits when it ends. The job allows silent breakaway, so programs the service
  starts (an opened document, the update installer) are not in the job and keep running.
- **The service stops.** The client fails every pending request with a transport `IpcException`
  and raises `Disconnected`. `MainWindow.ReconnectBackendAsync` shows a message, clears transfer,
  search and watch state, and calls `BackendSession.ReconnectAsync`, which starts a new service on
  the pipe `SumaFile.{sessionId}.{pid}.{generation}` with a new token. The window then gives
  `FileOperationService` the new client, registers the plugins' archive engines again, watches the
  active folder and refreshes the drives and visible panes. A restarted service has no listing
  views; the workspace opens them again.
- **The window closes.** `MainWindow` saves the layout and settings, stops the plugins, then
  disposes the session: `ipc.shutdown` (1 s), a 1.5 s wait for the process, then a kill.

## The C# solution

`src-winui/SimpleFile.sln` holds six application projects, plus the UI projects of the bundled
plugins, the plugin template and a fixture plugin.

| Project | Owns | References |
| --- | --- | --- |
| `SimpleFile.Ipc` | `NamedPipeJsonClient`, `ISimpleFileIpc`, `FrameCodec`, `BinaryFrameCodec`, `Protocol`, the wire models (`Models.cs`), `IpcException` | none |
| `SimpleFile.Core` | `BackendSession`, `ServiceLocator`, `JobObject`, `ExplorerWorkspace` and its controllers, `FileOperationService`, the search, transfer and toolbar view models, command, menu and shortcut tables, settings and layout stores, startup tracing | `Ipc` |
| `SimpleFile.PluginSdk` | The contracts a plugin's UI assembly implements and receives (`IPlugin`, `IPluginHost`, ...). Versioned by `PluginSdk.Version`, not by the app; packed as `SumaFile.PluginSdk` | none |
| `SimpleFile.PluginHost` | `PluginManager`; `Discovery`, `Manifest`, `Trust`, `Install`, `Loading`, `Backend`, `Contributions` | `PluginSdk`, `Ipc`, `Core` |
| `SimpleFile.App` | The window (`MainWindow.*.cs` partial classes and XAML views), dialogs, `SettingsWindow`, the preview presenter, the WinUI side of each plugin service, the Debug-only UI smoke. Builds `SumaFile.exe` | `Core`, `PluginHost`, `PluginSdk` |
| `SimpleFile.Tests` | xUnit tests | `Ipc`, `Core`, `PluginSdk`, `PluginHost`, the plugins' model projects |

Of these, only `SimpleFile.App` references the Windows App SDK. `Core`, `Ipc`, `PluginSdk` and
`PluginHost` use no WinUI types (a UI object crosses the SDK typed as `object`), which is what lets
the tests cover them: `SimpleFile.Tests` does not reference the App, and tests about the App read
its source text (`WinUiSourceShapeTests`). Logic that needs a test belongs in `Core`.

### The workspace and its controllers

`ExplorerWorkspace` (the `ExplorerWorkspace.*.cs` partial class) is the state the window shows: two
`ExplorerPane`s with their tabs, history and selection, the settings, clipboard and undo stack. It
is a facade. Its methods forward to controllers in `SimpleFile.Core/WorkspaceControllers/`, which
receive the workspace as an argument and change its state under one lock, `ExplorerWorkspace.Gate`.

| Area | Controllers |
| --- | --- |
| Navigation and places | `WorkspaceNavigationController`, `DriveController`, `FolderTreeController`, `PlacesController` |
| Panes, tabs, selection | `DualPaneTabController`, `ClosedTabController`, `WorkspaceSelectionController` |
| Rows and columns | `ListingViewController`, `LazyColumnController`, `PaneEnrichmentController`, `FolderViewController` |
| Operations | `WorkspaceFileOpsController`, `OperationLogController`, `WorkspaceTagController` |
| Persistence | `WorkspaceLayoutController` |

The workspace raises `Changed`; `MainWindow` queues a sync and copies the state into its controls.
The workspace reaches the service through two seams. `IExplorerBackend` (drives, home, listings,
and optionally `IListingViewBackend` and `IEntryFactsBackend`) is implemented by `BackendSession`.
`FileOperationService` wraps `ISimpleFileIpc` for everything else, records operations in
`operations.jsonl` and implements `ISettingsBackend`.

Both seams have test-only stand-ins in `SimpleFile.Tests`: `FakeExplorerBackend`,
`FakeListingViewBackend` and `FakeEntryFactsBackend`; `NullIpc` and `ConfigurableIpc` for
`ISimpleFileIpc`; `FakeIpcServer`, a pipe server for `NamedPipeJsonClient`; and fakes of
`IPluginProcessLauncher`. A backend without listing views or entry facts returns `null` for those
properties; the workspace then uses `list_directory` and leaves lazy columns empty. Other tests
start the real service and the staged plugins, which must be built first.

## The Rust workspace

| Crate | Owns | Depends on |
| --- | --- | --- |
| `crates/simplefile-ipc` | Protocol constants (`PROTOCOL_VERSION`, `MAX_FRAME_BYTES`, error codes, prefixes), `frame`, the `rpc` types, generated method and event names | none |
| `crates/sumafile-transfer` | Transfer primitives: `staging`, `journal`, `manifest`, `reliability`, `policy`, `resources`, `tuning`, `ids`, `paths`, `path_conflict`. A leaf crate: it never depends on `simplefile-core` or `simplefile-service` | none |
| `crates/simplefile-core` | The domain, with no transport: `dir_list`, `listing_view`, `collation`, `file_ops`, `archive`, `preview`, thumbnails, `metadata`, `entry_facts`, `fact_cache`, `drives`, `recycle_bin`, `tags`, `smart_folders`, `settings_store`, `cleanup`, `checksum`, `compare`, `open_with`, `terminal`, `updater`, and the wire types in `models.rs` | `sumafile-transfer` |
| `crates/simplefile-service` | The process: the pipe (`main.rs`), the session loop (`session/`), `dispatch/`, `scheduler.rs`, `binary.rs`, `listing_views.rs`, `session_cache.rs`, the transfer engine (`progress.rs`, `progress/`), `search.rs`, `watcher.rs`, `shell.rs`, `plugin_archive/` | `simplefile-core`, `simplefile-ipc`, `sumafile-transfer` |
| `crates/sumafile-plugin` | The helper crate for backends: the pipe server, `plugin.handshake`, request routing, cancel flags, `HostClient` | `simplefile-ipc` |
| `crates/hello-plugin-backend` | The backend of the Hello fixture plugin the tests use | `sumafile-plugin` |

The root `Cargo.toml` also lists the plugin backends as members: `plugins/sevenzip/backend`,
`plugins/git/backend`, `plugins/remote/backend` and `templates/sumafile-plugin/backend`, plus
`plugins/remote/test-server`, a loopback SFTP server for tests. A backend depends on
`sumafile-plugin`, the remote one also on `sumafile-transfer`, and never on `simplefile-core` or
`simplefile-service`.

## The IPC contract

### Schema first

`ipc/schema/v1` is the contract: `protocol.json` (framing, errors, handshake), `commands.json`
(every method with its parameters and result), `events.json` and `types.json` (wire types and
field names), with samples in `goldens/`. Top-level parameters are camelCase; nested objects and
results are snake_case. `scripts/generate-ipc-bindings.mjs` writes `Protocol.Generated.cs` and
`NamedPipeJsonClient.Generated.cs` in `SimpleFile.Ipc` and `protocol_generated.rs` in
`simplefile-ipc`: method and event names, binary frame tags and the C# client wrappers.

Three checks hold both sides to the schema. `npm run check:ipc-generated` fails when a generated
file differs from what the schema produces. `npm run check:ipc-schema` compares the schema with
the match arms in `crates/simplefile-service/src/dispatch/`, the generated constants and the fields
of the Rust structs. `cargo test -p simplefile-ipc` loads the same JSON.

### Framing, handshake and binary frames

A frame is a little-endian `uint32` length followed by that many bytes: one UTF-8 JSON-RPC 2.0
object, or a binary frame. Batch arrays are not used and `params` is always an object. The limit is
80 MiB in both directions (`MAX_FRAME_BYTES`, `Protocol.MaxFrameBytes`). The service ends the
session on a larger inbound frame. A result that would exceed the limit is replaced by an error
whose message starts with `RESULT_TOO_LARGE:`, and the pipe stays open.

`ipc.handshake` must be the first request. Its parameters are `protocolVersion`, `clientName`,
`authToken` and `binaryHotFrames`; the result carries `protocolVersion`, `appVersion`, `identifier`,
`methodCount`, `binaryHotFrames` and `binaryFrameVersion`. It settles the protocol version (the
service speaks 1 and answers any other with an application error), the token, and whether the
service may send binary frames. `ipc.health` and `ipc.shutdown` are the other control methods.

The largest payloads skip JSON when the client asked for binary frames, as `HandshakeParams` does
by default. A binary frame is `SFB1`, a version byte (2), a tag byte and a payload. The tags, 1 to
9 in `protocol.json`, cover `list_directory` chunks and results, search batches and results,
`operation-progress`, `file-change`, thumbnail results and listing view pages.
`crates/simplefile-service/src/binary.rs` encodes and `BinaryFrameCodec.cs` decodes, so a change to
an encoded type changes both; the client rejects a version other than
`Protocol.BinaryFrameVersion`. Each message keeps its JSON form for a client that did not ask.

### How the service runs a request

The session loop (`session/mod.rs`) reads a frame and calls `dispatch`, which returns a `Dispatch`
value: an immediate reply, or work to run off the loop so the loop keeps reading frames, cancels
included. All outbound frames pass through one writer task (`session/io.rs`). `BlockingScheduler`
(`scheduler.rs`) runs the work on lanes. The general lane has one permit per core, at least two.
Background work (folder sizes, thumbnails, facts, scans) has one permit fewer and waits, for a
bounded time, while foreground work such as a listing or a search runs. Transfers and archive jobs
have their own lane of two permits, so a long copy never holds up a listing. A few short calls
still answer on the loop: settings reads and writes, and creates and renames outside an archive.

### Errors

| Code | Meaning |
| --- | --- |
| `-32700` | The frame is not valid JSON; the session continues |
| `-32600` | Invalid request: not JSON-RPC 2.0, a method before the handshake, a wrong token, a second handshake |
| `-32601` | Unknown method |
| `-32602` | Parameters do not match the method |
| `-32000` | Application error: the `Err(String)` of the domain function, message unchanged |
| `-32001` | Host-owned: the app must do this itself (`select_directory`) |

Application errors are told apart by message prefix: `CONFLICT:`, `TRASH_UNAVAILABLE:`,
`RESULT_TOO_LARGE:` and `HOST_OWNED:`. In C# an error is an `IpcException` with `Code`, `Kind` and
`IsConflict`, `IsTrashUnavailable`, `IsResultTooLarge`, `IsHostOwned`; a broken pipe has kind
`Transport`.

### Events and requests in the other direction

Events are notifications, sent with `EventSink` (`session/jobs.rs`) and received with
`ISimpleFileIpc.On<T>(Protocol.<Name>Event, handler)`: `list_directory.chunk`,
`listing-view-changed`, `entry-facts`, `file-change`, `operation-progress`, `transfer-tuning`,
`transfer-changes`, `search-results-batch`, `search-complete` and `update-chunk`. Several pieces of work can be in
flight, so an event says whose it is: `list_directory.chunk` carries the JSON-RPC id of its
request, a view delta the view id, and progress, tuning, search and fact events the operation,
search or request id of their work. The file service never sends a request to the app;
`NamedPipeJsonClient.OnRequest` exists for plugin backends, which call the host's `host.*` methods
over their own pipe (`PluginHostMethods`).

### Cancellation

There is no JSON-RPC cancel. Cancelling the `CancellationToken` of a client call only stops the
caller waiting; the service keeps working. Work is stopped with a named command.
`cancel_operation` (transfers and archive jobs, by `operationId`), `cancel_search` (`searchId`) and
`cancel_entry_facts` (`requestId`) name the work they stop. `cancel_disk_cleanup` and
`cancel_duplicate_check` take an optional `operationId` and stop every such scan without one.
`cancel_folder_size`, `cancel_folder_item_count` and `cancel_folder_metrics` take no id. The
service registers an id before it starts the work, so a cancel sent straight after the request
finds it. When the connection ends, it cancels the session's transfers, searches and scans.

### Adding a method

1. Declare it in `ipc/schema/v1/commands.json` and raise `domainMethodCount`. Add new wire types to
   `types.json` and new events to `events.json`.
2. In `scripts/generate-ipc-bindings.mjs`, add its C# wrapper to `wrappers`, or name it in
   `manualClientMethods` and write the wrapper in `NamedPipeJsonClient.cs`. The generator refuses
   a method it has no entry for.
3. Run `npm run generate:ipc-bindings`. Never edit a generated file.
4. Add the method to `ISimpleFileIpc` and to `NullIpc` in the tests, and the models to `Models.cs`
   and `crates/simplefile-core/src/models.rs`.
5. Implement it: the logic in `simplefile-core`, the parameter struct in `dispatch/params.rs`, and
   a `METHOD_<NAME> =>` arm in `dispatch/handlers.rs`. Use `reply_blocking` for anything that can
   block: a folder walk, a network path, another process.
6. Update the expected count in `scripts/check-ipc-schema.mjs` and
   `scripts/check-winui-parity-gate.mjs`, and name the method in
   `docs/winui-migration/parity-gate.md`, which the second script requires.
7. Run `npm run check`, `npm run check:rust` and `npm run check:winui`.

Within protocol version 1 a method keeps its name and a change only adds methods or optional
fields; a rename needs a new protocol version (`protocol.json`, `compatibility`).

## Listing

A pane does not receive a folder and sort it. The service holds a **listing view** per pane
(`crates/simplefile-core/src/listing_view.rs`, `crates/simplefile-service/src/listing_views.rs`,
`session/listing_jobs.rs`): the folder's entries in the order and with the filter the pane named.

- `open_listing_view` enumerates the folder and returns the first page, or only re-sorts when the
  view already shows that folder. While a slow folder is being enumerated, its first two chunks
  arrive as `list_directory.chunk` previews. A later open of a view id supersedes an earlier one.
- `get_listing_view_range` returns rows by offset, at most 8,192 per request (`MAX_PAGE_ROWS`).
  `ListingViewController` shows a first page of 512 rows and reads the rest in pages of 4,096, so
  the pane ends up holding every row, in the service's order.
- A change arrives as a `ListingViewDelta`: inserts, removals and updates by row index, with the
  view's revision, which comes from one counter per session. A refresh that changes more than 256
  rows (`MAX_DELTA_CHANGES`) reports `reset`, and the pane reads its pages again.
- The service sorts by name, size, dates, attributes, extension and type, in the app's locale
  (`collation.rs`). The pane sorts by any other column itself (`PaneListing.IsServiceSortColumn`).

`SessionCache` (`session_cache.rs`) keeps listings, entry information, metadata, folder metrics and
thumbnails in memory for the session, each stamped with the size and modified time of its path.
Network paths are never cached. A cached listing older than 10 s (`DIRECTORY_LISTING_MAX_AGE`) is
shown at once and enumerated again behind the view. Watcher events and mutating commands
invalidate the affected paths.

The watcher (`watcher.rs`) follows one folder per session, without subfolders. The app points it at
the active pane's folder with `watch_directory` and skips network paths. Changes are coalesced for
150 ms; more than 512 pending paths collapse into one change of the folder itself. Each change is
sent as `file-change` and applied to the open views of that folder. A folder that is not watched
changes on screen only when the app refreshes it, as it does after its own operations.
`list_directory`, which streams a whole folder in chunks, remains for backends without views.

## On-demand column values

Columns that cost a read per entry are **entry facts**: `folder_size`, `item_count`, `dimensions`,
`duration`, `date_taken`, `camera`, `pages`.

- **Request.** `LazyColumnController` asks for the rows on screen plus a look-ahead of 24 rows, at
  most 64 paths per request, when `MainWindow` reports the viewport (120 ms after the last scroll).
  It also asks for one clicked cell, and for a whole folder, up to 5,000 rows, on request or for a
  sort by that column. `get_entry_facts` takes a request id, paths and kinds. Values come back as
  `entry-facts` notifications in batches of 16 or after 50 ms. A new request with the same id, or
  `cancel_entry_facts`, stops the earlier one.
- **Readers.** Folder facts come from one folder-metrics walk per folder. File facts are read by
  `crates/simplefile-core/src/entry_facts.rs`: image dimensions, audio and video duration, Exif
  date and camera, PDF page count. A fact's state is `ready`, `none`, `unsupported` or `failed`.
  Per request the service takes at most 256 paths and runs two folder walks and four file reads at
  a time, on the background lane (`session/fact_jobs.rs`).
- **Cache.** `fact_cache.rs` stores file facts, failed and unsupported ones included, in the
  `entry_facts` table of `metadata.db`, valid while the file's size and modified time match.
  Folder facts use the session cache only.
- **Windows fallback.** For a duration the service reports as `unsupported`, the app asks the
  Windows property system (`MainWindow.LazyColumns.cs`), two reads at a time, and stores a result
  with `put_entry_facts`.

## Plugins

A plugin is a folder with a `plugin.json` and up to two halves. `SimpleFile.PluginHost` runs in the
app process: `PluginDiscovery` scans `plugins\` beside the executable, `PluginManifestReader`
validates each manifest, and `PluginManager` holds every plugin's state. Contributions (commands,
context menu and toolbar items, sidebar sections, panels, row decorations, a settings page, archive
formats) are rendered from the manifest. The UI half is a .NET assembly written against
`SimpleFile.PluginSdk` and loaded into the app in its own `PluginLoadContext`. Nothing is
sandboxed, so a plugin runs only after the user consents (`PluginConsentDialog`); the decision is
stored in `plugin-approvals.json` against the manifest's hash. Plugins listed in
`plugins\bundled.json` with a matching hash are enabled without a prompt.

The backend is a separate process. `PluginBackendHost` starts it with
`--pipe-name SumaFile.Plugin.{session}.{pid}.{pluginId} --data-dir {folder}` in its own job object
(`JobObject.CreateKillOnCloseOnly`: no breakaway, so the backend's children die with it). The
backend creates the pipe. The host connects with `PipeOptions.CurrentUserOnly`, checks that the
pipe's server is the process it started, and sends `plugin.handshake`, allowing 5 s to connect and
5 s for the reply.

Archive formats take a second route. The host registers a backend's formats with the service
(`register_archive_engine`). The service then connects to that backend's pipe itself, with up to
eight connections per plugin (`plugin_archive/`), and `crates/simplefile-core/src/archive/engine.rs`
consults registered engines only after every built-in format has declined a path. The 7-Zip plugin
serves `.7z` this way; without it, operations on a `.7z` fail with a note that names the plugin.

[The plugin guide](PLUGINS.md) covers everything else: manifests, the SDK, the backend protocol,
limits, trust, signing and packaging. The wire reference is
[`ipc/schema/plugin/README.md`](../ipc/schema/plugin/README.md).

## Transfers

Copies and moves run in the service. `copy_with_progress` and `move_with_progress` register their
operation id in an `OperationRegistry` and run on the transfer lane. `progress.rs` and
`progress/plan.rs`, `execute.rs` and `parallel.rs` plan destinations, copy files and coordinate the
workers: one coordinator owns ordering, aggregate progress and the active-file list, and a failed
or conflicting file is recorded while the rest of the selection continues. Progress is reported as
`operation-progress`, scheduler decisions as `transfer-tuning`.

The Windows client requests individual conflict decisions for matching files.
Matching folders merge recursively. The service records newly published files and
created directories relative to each existing destination root, plus source
directories removed by a move. These changes travel with the committed result
on success, partial failure or cancellation. Large change lists arrive in ordered
`transfer-changes` notifications before the result. Undo and Redo use the recorded
paths and remove directories only while empty.

The parts that do not depend on the service live in `sumafile-transfer`, so the FTP/SFTP plugin's
backend uses the same code. Unless the policy asks for in-place writes, a file is written to
`.{name}.{hash}.sumafile-partial` beside its destination and moved into place when complete.
`journal.rs` keeps a locked ownership record next to each staged file (`.state`), and
`recover_directory` cleans up what an interrupted transfer left, touching only files whose recorded
identity matches and never a source. The adaptive scheduler is split the same way:
`sumafile_transfer::tuning` holds the decisions as pure functions (the starting concurrency, how
throughput and write latency are read, when to try more or fewer simultaneous files), and
`progress/adaptive.rs` and `progress/tuning.rs` supply the drive kinds, the clock, the stored
policy (re-read every 5 s) and the remembered routes. [TRANSFER_TUNING.md](TRANSFER_TUNING.md)
describes the modes and limits.

## Storage and logs

**Application data** is the first of these folders that exists: the one `SIMPLEFILE_APP_DATA_DIR`
names, then `com.simplefile.desktop`, `SumaFile` and `SimpleFile` under `%APPDATA%`, then
`SumaFile`, `com.simplefile.desktop` and `SimpleFile` under `%LOCALAPPDATA%`
(`settings_store::app_data_dir`). When none exists, the first candidate is used, which without the
override is `%APPDATA%\com.simplefile.desktop`. That identifier predates the product's rename to
SumaFile and is kept so that existing installations keep their data. The app process never opens
this folder; it reaches it through the service. The FTP/SFTP plugin's backend reads `metadata.db`
once, read-only, to import what earlier versions stored there (`legacy.rs` in
`plugins/remote/backend/src`).

| Item | Location | Notes |
| --- | --- | --- |
| `metadata.db` | The file `SIMPLEFILE_METADATA_DB` names; else the first candidate folder that already holds one; else the first candidate | SQLite, opened on first use (`settings_store.rs`). Tables: `settings` (key and value), `tags`, `file_tags`, `entry_facts` |
| `smart_folders.json` | The application data folder | `smart_folders.rs` |
| Thumbnail cache | `%LOCALAPPDATA%\SumaFile\thumbnail-cache`, or the `thumbnailCachePath` setting | JPEG files keyed by path, modified time, size and thumbnail size; 500 MB unless `thumbnailCacheMaxMb` says otherwise, 0 disables it |
| Plugin files | `plugins\`, `plugins-data\`, `plugin-approvals.json` beside the executable | See [the plugin guide](PLUGINS.md) |

Every app setting is a row of the `settings` table, read in one `get_db_settings` batch at startup
by `WorkspaceSettingsStore` and written with `set_db_setting`. Larger state is JSON in one row
each: the last session (`workspace-layout`), named profiles (`workspace-profiles`), per-folder view
rules (`folder-view-settings`), shortcut overrides (`shortcutOverrides`), the toolbar layout
(`commandSurface.layout`) and the transfer policy (`transfer.policy`). A plugin's settings are
rows named `plugin.<plugin id>.<key>`.

| Log | Location | Written by |
| --- | --- | --- |
| `startup.log` | `%LOCALAPPDATA%\SumaFile` | `App.LogCrash`: launch stages and unhandled exceptions |
| `startup-timing.log` | `%LOCALAPPDATA%\SumaFile` | `StartupTrace`: one line per startup stage |
| `service.log` | `%LOCALAPPDATA%\SumaFile` | `BackendSession`: each line the service writes to standard error |
| `operations.jsonl` | `%LOCALAPPDATA%\SumaFile` | `OperationJournal`: started, completed, cancelled and failed operations |
| `plugins.log` | Beside the executable | The app: plugin load errors, plugin log lines, each backend's standard error |

All of them except `operations.jsonl` rotate to `<name>.1` at 1 MB. The service installs no
backend for the `log` crate, so only `eprintln!` output reaches `service.log`.

Environment overrides not named above: `SIMPLEFILE_SERVICE_PATH` (the service executable),
`SIMPLEFILE_UPDATE_MANIFEST_JSON` and `SIMPLEFILE_UPDATE_MANIFEST_PATH` (the updater's manifest),
the `SUMAFILE_PLUGIN_*` variables (plugin files) and, in Debug builds, `SIMPLEFILE_UI_SMOKE_ROOT`
(runs the UI smoke).

## Startup budget

`StartupTimer` writes a line to `startup-timing.log` at each stage with the time since its scope
began. `scripts/check-winui-startup-budget.mjs` reads the latest session that reached ready and
fails when a limit is exceeded or a stage is missing:

| Stage | Limit |
| --- | --- |
| `BackendSession.Start.ready` (service started, handshake and first calls done) | 1,500 ms |
| `MainWindow.Connect.workspace-initialized` | 6,000 ms |
| `MainWindow.Connect.first-sync` | 7,000 ms |
| `MainWindow.Connect.ready` | 8,000 ms |
| Plugin discovery: the `MainWindow.Connect.plugins-*` stages together | 50 ms |

CI runs the check after the UI smoke with a plugin loaded (`.github/workflows/ci.yml`). The same
limits are in `StartupTimingBudget.cs` for the unit tests. To stay inside them, startup scans
plugins on the thread pool while the service starts, starts plugin backends only after the
discovery stages are marked, loads settings in one batch and navigates after `ready`.

## Versioning and updates

The app's version is in `src-winui/Directory.Build.props` (`Version` and `InformationalVersion`)
and is repeated in the `Cargo.toml` of `simplefile-core`, `simplefile-ipc` and
`simplefile-service`, `Cargo.lock`, `APP_DISPLAY_VERSION` in `crates/simplefile-core/src/lib.rs`,
`app.manifest`, the NSIS script, the NuGet lock files and the README.
`npm run release:prepare -- X.Y.Z --write` (`scripts/release-version.mjs`) rewrites all of them,
and `npm run check` fails when they disagree. The plugin SDK, `sumafile-plugin`,
`sumafile-transfer` and the plugins have their own versions.

`crates/simplefile-core/src/updater.rs` reads `latest-winui.json` from the latest GitHub release.
It installs an update only when the build was compiled with `SIMPLEFILE_UPDATER_PUBLIC_KEY`, the
installer's URL is under the repository's releases and ends in `_x64-winui-setup.exe`, and the
downloaded file matches the manifest's size and SHA-256 and carries a valid Ed25519 signature. It
then starts the installer with `/S`. [UPDATER_RELEASE.md](UPDATER_RELEASE.md) covers keys and the
manifest; [the release runbook](../.github/RELEASE.md) covers the release itself.

## Contracts a change must keep

The sections above state most of them: the schema, bindings, handlers and models agree; `Core`,
`Ipc`, `PluginSdk` and `PluginHost` stay free of WinUI types; `sumafile-transfer` and the plugin
backends stay independent of the core and the service; a handler that can block stays off the
session loop; the data folder keeps its identifier; the startup limits hold. Three more:

- A folder opens inside SumaFile. `ExplorerWorkspace.OpenPathAsync` carries whether the target is a
  folder, and only a path known to be a file is passed to `open_file`.
- Mapped network drives are ordinary drives and stay in the drive list, named by their volume
  label and carrying their share as the remote path (`crates/simplefile-core/src/drives.rs`). The
  light drive list at startup does not probe them.
- A path inside an archive is routed to the archive code before the ordinary file-system code
  (`file_ops.rs`, `dispatch/handlers.rs`).

## Where to change what

| Change | Files | Check |
| --- | --- | --- |
| An IPC method, event or wire type | [Adding a method](#adding-a-method) | `npm run check`, `npm run check:rust`, `npm run check:winui` |
| File-system behaviour: listing, file operations, archives, previews, metadata | `crates/simplefile-core/src` | `npm run check:rust` |
| How a request is scheduled, streamed or cancelled | `crates/simplefile-service/src/session`, `dispatch`, `scheduler.rs` | `npm run check:rust` |
| Transfers | `crates/simplefile-service/src/progress*`, `crates/sumafile-transfer`; in the app `TransferManagerViewModel` (the queue), `TransferPolicy` (options) and `TransferConflictSession` (conflicts); [TRANSFER_TUNING.md](TRANSFER_TUNING.md) | `npm run check:rust`, `npm run check:winui` |
| Navigation, tabs, selection, sorting | `SimpleFile.Core/WorkspaceControllers`, `ExplorerWorkspace.*.cs` | `npm run check:winui` |
| Anything on screen | `SimpleFile.App` | `npm run check:winui`, `npm run smoke:winui-ui` |
| A command, shortcut or context menu item | `AppCommandCatalog.cs`, `CommandIds.cs`, `KeyboardShortcutMap.cs`, `ContextMenuBuilder.cs`; new ids are listed in `winui-migration/parity-gate.md` | `npm run check`, `npm run check:winui` |
| A setting | `UiSettings.cs`, `WorkspaceSettingsStore.cs`, `SettingsWindow.*.cs` | `npm run check:winui` |
| The plugin contract | `SimpleFile.PluginSdk`, `SimpleFile.PluginHost`, `ipc/schema/plugin/v1`, `crates/sumafile-plugin`, [PLUGINS.md](PLUGINS.md) | `npm run check`, `npm run test:maintenance`, `npm run check:winui` |
| A bundled plugin | `plugins/<name>` | `npm run build:plugins`, `npm run check:rust`, `npm run check:winui` |
| The startup path | `MainWindow.ConnectAsync`, `BackendSession`, `WorkspaceSettingsStore` | `npm run smoke:winui-ui`, then `npm run check:winui-startup-budget` |
| The version | `npm run release:prepare -- X.Y.Z --write` | `npm run check` |
| Installers and the payload | `packaging/winui`, `scripts/build-winui-release.ps1` | `npm run check:winui-packaging`, the `smoke:winui-*` scripts |
