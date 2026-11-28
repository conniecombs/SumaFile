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
| `crates/sumafile-transfer` | Transfer primitives: `staging`, `journal`, `reliability`, `policy`, `resources`, `tuning`, `ids`, `paths`, `path_conflict`. A leaf crate: it never depends on `simplefile-core` or `simplefile-service` | none |
| `crates/sumafile-imaging` | The thumbnail pipeline (`thumbnail`: decode, fit, scale, encode as JPEG) and the generated list of formats the media process decodes (`formats`). A leaf crate | none |
| `crates/sumafile-media-client` | The file service's side of the media process: the wire format (`protocol`), and the client that starts the process, gates and times its requests and isolates files that crash it. Standard library and `serde` only | none |
| `crates/sumafile-media` | The media process, `sumafile-media.exe`: the decoders (`decoders/`), the render functions and the request loop. It ships beside the service | `sumafile-imaging`, `sumafile-media-client` |
| `crates/sumafile-video` | The video process, `sumafile-video.exe`: video thumbnails through Windows' Media Foundation (`mf.rs`), and the rules that need no Windows (`rules`, `pixels`, `attempt`). It ships beside the service | `sumafile-imaging`, `sumafile-media-client`, `sumafile-media` |
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
by default. A binary frame is `SFB1`, a version byte (3), a tag byte and a payload. The tags, 1 to
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
`cancel_folder_size`, `cancel_folder_item_count` and `cancel_folder_metrics` take an optional
`path`: with it they stop the requests for that folder, without it every running request of the
kind. The
service registers an id before it starts the work, so a cancel sent straight after the request
finds it. When the connection ends, it cancels the session's transfers, searches, scans and folder size
and count requests.

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

The watcher (`watcher.rs`, `watcher/engine.rs`, `watcher/coalesce.rs`) follows the folders of the
visible panes, without subfolders. The app names them with `watch_directory` (`paths`) each time
they change and skips network paths, archives and the Recycle Bin. One thread per session reads
every watched folder from one I/O completion port; it starts with the first request and ends with
the session. A request names the whole set: folders already watched stay watched without a gap, new
ones are opened on that thread and the others are let go, and the session awaits the answer
instead of waiting on a disk. A folder that cannot be watched fails the request and leaves the set
as it was.

- Each folder has one `ReadDirectoryChangesExW` request outstanding, with a 64 KB buffer. Its
  extended records carry each entry's size, dates and attributes, so a changed row is built from
  the record (`file_entry_from_watched_facts`) and the entry is not opened. A reparse point (a
  link, a cloud placeholder) is still read, as the record does not say what it points to. A file
  system without the extended records is asked in the plain form, and then every changed entry is
  read.
- Changes are gathered for 150 ms after the last one, 500 ms after the first at the latest. The
  last state of each path is told, as `file-change` and as a delta of the open views of its
  folder. The names `.tmp`, `.part`, `.crdownload`, `desktop.ini`, `Thumbs.db` and `.DS_Store`
  are not told.
- The two records of a rename become one change. The views get one delta for it, whose arriving
  row carries `renamed_from` (the name it had); the app moves its selection from the old path to
  the new one. `file-change` still names both paths, the new one first.
- More than 512 pending paths in one folder, or a buffer the system reports as run over, become
  one `modify` of that folder itself. The service then enumerates the folder again for its views
  and sends what differs. Only that folder is read again.
- A watched folder that is deleted, renamed or moved, or whose volume leaves, is told as `gone`
  with its own path, and is no longer watched. A deleted folder fails its pending read at once. A
  renamed or moved one keeps its handle and reports nothing about itself, so each folder is asked
  every two seconds whether it is still at the path it was opened at. The app then shows the
  nearest folder above that still exists (`ExplorerWorkspace.LeaveGoneFolderAsync`), or the same
  folder again when it was removed and made again. The views of a gone folder keep their rows,
  but the next open of each enumerates (`ListingViews::mark_folder_gone`): the rows are not
  shown again as they are, and are not handed to the cache.

A folder that is not watched changes on screen only when the app refreshes it, as it does after
its own operations.
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

## The media process

SumaFile decodes some picture formats itself, for thumbnails and for the preview pane, so that
they show whatever codecs Windows has. Six common formats (JPEG, PNG, GIF, WebP, BMP) are decoded
inside the file service, by `sumafile-imaging`. The rest are decoded by a second program,
`sumafile-media.exe`, which the file service starts and owns. A file that crashes a decoder,
hangs it or exhausts memory then costs the service nothing.

**Formats.** `ipc/schema/v1/media-formats.json` lists them: Photoshop (PSD, PSB), OpenEXR,
Radiance HDR, JPEG XL, TIFF, SVG, DDS, Targa, QOI, the portable anymap family, Krita, OpenRaster
and camera RAW. `npm run generate:media-formats` writes the Rust list
(`sumafile_imaging::formats`) and the C# one (`MediaFormats`); `npm run check` fails when either is
stale. A test in `sumafile-media` fails when a listed extension has no decoder. HEIC, AVIF and
JPEG 2000 are not here: they are left to Windows' own codecs.

**Decoders** (`crates/sumafile-media/src/decoders`), one per format, picked by the file's
extension. EXR, TIFF and SVG are ported from [Oculante](https://github.com/woelper/oculante) (MIT;
its licence text ships as `LICENSE-oculante.txt`). Photoshop documents and camera RAW files are
read by SumaFile's own readers:

- A Photoshop document gives the JPEG thumbnail Photoshop saved when that is large enough, else
  its flattened image. The layers are skipped, never loaded.
- A camera RAW file gives the largest JPEG preview the camera embedded, turned the way the camera
  was held. Sensor data is not developed. The reader walks a TIFF file's directories and reads
  Fujifilm's RAF header and Canon's CR3 boxes; it does not read maker notes. A thumbnail takes a
  preview of at least half its size; the preview pane needs at least 1,000 pixels.

No decoder is a C or C++ library, and none is LGPL or GPL.

**The pixel budget.** A decoded picture may take at most 1 GiB (`limits::PIXEL_BUDGET_BYTES`):
its width times its height times its channels times the bytes of a sample. A decoder works this
out from the file's header and answers `too_large` before it reads or allocates the pixels. What
is counted, format by format:

- Targa, QOI, the anymap family, Radiance HDR, and the PNG inside a Krita or OpenRaster file: the
  picture as the `image` library returns it. The library's own allocation limit is set to the
  same 1 GiB. The PNG may be at most 512 MiB long.
- TIFF: the picture with its samples at 8, 16 or 32 bits. The `tiff` library's limits on its two
  buffers are set to 1 GiB each.
- JPEG XL: the picture as the decoder returns it. The decoder works in buffers several times that
  size. It counts them itself, against a second 1 GiB; by its own account the count is not strict.
- OpenEXR: every layer, as four float channels. A file with no colour channels is shown by its
  first channel, and the library loads every channel to get it. Before that, every layer is
  counted with all of its channels.
- DDS: the first picture at full size, as RGBA. The headers are read alone first. Then at most 16
  bytes a pixel are read from the file, and never more than 1 GiB. The other faces of a cube map,
  the other layers of an array, the other slices of a volume and the smaller mipmaps are not read.
- Photoshop: the flattened image at 8 bits a channel.
- SVG: the size the drawing is drawn at. A file longer than 16 MiB is refused unread. A
  gzip-compressed SVG is refused too: `usvg` is built without its `svgz` feature.
- Camera RAW: an embedded JPEG, by the size in the JPEG's own header. A preview over the budget,
  or longer than 64 MiB, is passed over for the next one.

The budget counts the decoded picture and nothing more. It does not count the bytes of the file a
decoder holds while it works. It does not count a library's working buffers, except where said
above: OpenEXR, for one, unpacks every channel of a block at once. It does not count the parsed
form of an SVG (`usvg` stops at one million elements) or the pictures embedded in one. It does
not count the copies made to convert, scale and encode a picture: scaling a preview that has
transparency copies the picture once. The job object's limit, below, is the guard for all of
these.

**The process.** The service finds `sumafile-media.exe` beside its own executable
(`SUMAFILE_MEDIA_HELPER` names another, for tests and development). `media_provider::install`
registers a provider with the core at service start and starts nothing; the process starts with
the first thumbnail or preview that needs it. Requests and replies cross its standard input and
output:

```text
u32 LE header length | header (UTF-8 JSON) | u32 LE body length | body
```

`{ id, method: "thumbnail", path, size }` and `{ id, method: "preview", path, maxDimension }` are
answered with `{ id, ok: true, format, width, height }` and the encoded picture as the body, or
with `{ id, ok: false, kind, error }` where `kind` is `unsupported`, `too_large`, `decode` or
`io`. A picture whose encoded bytes are over the wire's 256 MiB is answered with `too_large`.
Each request runs on its own thread, and a decoder that panics fails only its own request. The
process leaves when its input closes.

Before it reads its first request, the process sends one message with the id 0 and no body: it is
running. No request has that id.

**The client** (`sumafile-media-client`) keeps one bad file from costing more than itself:

- At most four thumbnail requests and two preview requests are with the process at a time. A
  request's timeout (20 s for a thumbnail, 60 s for a preview) therefore measures its decode.
- A request that outlasts its timeout ends the process, and its file is recorded as bad.
- When the process stops with one request in flight, that file is recorded as bad. With several,
  each is sent again with nothing else in flight, and a file that fails alone is recorded as bad.
- A bad file answers at once, without starting the process, until its size or modified time
  changes. At most 1,024 are remembered, for the life of the service.
- A process that ends, or stays silent for 15 s, before it says it is running never started. No
  request was sent to it, so no file is recorded as bad, and starting is tried again after 30 s.
  Until then these formats fall back to Windows, as they do when the process is not installed.
- The client ends the process after 120 s with nothing to do. On Windows the process runs at
  below-normal priority in a job object that limits it to 3 GiB and ends it with the service. If
  Windows refuses the job object, the process runs without the limit and the service logs it.

**In the service.** `simplefile_core::media` holds the provider registry, shaped like the archive
engine registry. `preview::generate_thumbnail` asks the provider for a media-process format and
keeps the disk thumbnail cache for both kinds. Such a thumbnail waits on another process, not on a
core, so it runs on the scheduler's media lane (`run_media`: four thumbnail permits, two preview
permits) and never holds a background permit. `render_image_preview` answers the preview pane on
the preview permits.

**In the app.** `ThumbnailRouting` (`SimpleFile.Core`) says which extensions the service makes
thumbnails for. Media-process formats load through their own gate in `FileListThumbnailHost`, two
at a time. Windows' thumbnail stays the fallback for every format. The preview pane asks the
service first for these formats, except SVG, which WinUI draws as vectors.

To add a format: add it to `media-formats.json`, run the generator, write the decoder with its
tests in `crates/sumafile-media/src/decoders`, and add its extension to the dispatch in
`decoders/mod.rs`.

## The video process

Video thumbnails are made by a second helper, `sumafile-video.exe` (`crates/sumafile-video`). It
speaks the media process's wire format, runs its request loop, and is owned by a second
`MediaClient` in the service's provider. The client's rules are the same: timeouts, bad files, a
failed start, the idle exit, the job object.

**Why a program of its own.** A video that hangs a decoder or crashes a graphics driver ends only
this process, and picture thumbnails carry on. And it needs Media Foundation, which some Windows
editions (the "N" editions) ship without. A program that needs it does not start there. Were the
picture decoders in the same program, those PCs would lose the picture formats too. So
`sumafile-media` must never depend on the `windows` crate or on `sumafile-video`; a maintenance test
(`scripts/tests/video-program.test.mjs`) holds it to that. On such a PC the video process fails to
start, the client treats that as a failed start, and video thumbnails fall back to Windows' own.
The service turns Windows' error boxes off for itself and the programs it starts
(`media_provider::quiet_error_boxes`), or each attempt there would put up a "DLL was not found" box.

**The request.** `{ id, method: "video_thumbnail", path, size, positionPercent, hardware }` is
answered with `{ id, ok: true, format: "jpeg", width, height, decoder, reason }` and the JPEG.
`decoder` is `hardware` or `software`. `reason`, present with `software`, is `turned_off`,
`no_device`, `no_hardware_decoder` or `hardware_failed`.

**One thumbnail** (`mf.rs`, Windows only). The file is opened with Media Foundation's source
reader. The reader is asked for frames in the decoder's own pixel format, with its converters off,
so a frame that is only stepped over is never converted. It seeks to the position
(`rules::position_for`: the percent of the duration, at most 250 ms before the end), and steps
forward to the first frame at or past it (`rules::choose_frame`; after three seconds the frame
reached is used). That one frame is converted to 32-bit RGB and scaled by Windows' video processor,
copied out, turned upright (`pixels::upright_rgb`) and encoded by the thumbnail pipeline. A decoder
pads a frame to its block size (1080 rows are decoded into 1088) and states which part is the
picture; only that part is taken, and the thumbnail has its shape (`rules::visible_area`).

**What "hardware" means.** A thumbnail is tried with a Direct3D 11 device first, when the request
allows it and Windows has one on a graphics card, and without it second (`attempt`). Windows'
own adapters (vendor 0x1414: its software renderer, the Basic Display Adapter, a virtual
machine's or a remote session's) are refused, flagged as software or not: on a virtual machine
without a graphics card, and on a PC whose display driver is off, the software renderer is the
main adapter and has no software flag (`attempt::decodes_video`). Windows decodes on the processor without any error when it has no hardware decoder
for a file, so the attempt alone says nothing. A frame the graphics card decoded arrives as a
Direct3D texture; one the processor decoded arrives in ordinary memory. Only the first is reported
as `hardware`. A frame the processor decoded is also converted on the processor: nothing is gained
by sending it to the graphics card and back.

**In the service.** A video extension (the `video` list of `media-formats.json`) takes the video
route in `preview::generate_thumbnail_at`. The position is part of the disk cache's key for a
video; a picture's key is unchanged. A video inside an archive is refused before its path is
resolved, so it is never unpacked for a thumbnail. `videoThumbnailsUseGpu` (on unless it holds
`false`) is read for every request and sent as `hardware`. Video thumbnails wait in their own lane
(`MediaLane::Video`, four permits: two local list thumbnails, one from a network drive, one for the
preview pane), and do not wait for foreground work.

**The counts.** The provider counts each decode by who made it and keeps the last reason for a
processor decode (`media_status.rs`). `get_media_status` answers from those counts without asking
the video process. A thumbnail that comes from the cache was not decoded and is not counted. The
log gets a line at the first decode and whenever the decoder in use changes.

**In the app.** `FileListThumbnailHost` asks the service for a video's thumbnail with the position
of its frame, then Windows' own thumbnail. Local videos load two at a time; videos on a network
drive (`NetworkPaths`) one at a time, through a slot of their own. A video thumbnail that reaches
the front of its queue with nobody waiting (`WantedRequests`) is dropped. The preview pane's frame
picker makes the same request at 144 pixels, outside those queues: after the selection has rested
150 ms, and never more than one at a time (`LatestRequest`), so it takes one of the lane's four
permits at most. Settings shows the counts under
"Use the graphics card for video thumbnails" (`MediaStatusText`).

**What only a real PC can prove.** The test machines have no graphics card, and the test video is
uncompressed, so every automated run takes the processor path. The hardware path, a phone video's
turn, and non-square pixels are checked by hand: see
[`WINUI_RUNTIME_VERIFICATION.md`](WINUI_RUNTIME_VERIFICATION.md#video-thumbnails-on-a-real-pc).

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
(`JobObject.CreateKillOnCloseOnly`: no breakaway, so the backend's children die with it). It
writes a new random 32-byte token, as hex, to the backend's standard input and sets
`SUMAFILE_PLUGIN_AUTH_STDIN=1`; a backend that reads it answers only a handshake that carries it.
The backend creates the pipe. The host connects with `PipeOptions.CurrentUserOnly`, checks that the
pipe's server is the process it started, and sends `plugin.handshake` with the token, allowing 5 s
to connect and 5 s for the reply. The token goes to the file service with the pipe name when the
backend's archive formats are registered.

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

The same folder holds `window.json`: where the main window was when it was last closed. The app
reads it before the window is shown, which is before the file service and its settings are
there, and writes it when the window closes.

All of the logs rotate to `<name>.1`: `operations.jsonl` at 4 MiB, the others at 1 MB. The service
sends the warnings and errors of the `log` crate to its error stream (`logging.rs`), so they reach
`service.log` beside its `eprintln!` output.

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
| Video thumbnails | [The video process](#the-video-process): `crates/sumafile-video`, `ipc/schema/v1/media-formats.json` (`video`) | `npm run check`, `npm run check:rust`, `npm run check:winui`, then the real-PC check |
| A picture format SumaFile decodes itself | [The media process](#the-media-process): `ipc/schema/v1/media-formats.json`, `crates/sumafile-media/src/decoders` | `npm run check`, `npm run check:rust`, `npm run check:winui` |
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
