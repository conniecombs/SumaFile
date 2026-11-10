# SumaFile plugin backend protocol (v1)

JSON-RPC 2.0 spoken over a named pipe between a plugin's backend executable and
either the WinUI host or the Rust service. Framing is the same as the main IPC
schema (`ipc/schema/v1`): little-endian `uint32` length prefix, UTF-8 JSON body.

| File | Role |
| --- | --- |
| `v1/protocol.json` | Command line, handshake, `host.*` methods, archive-engine methods with their params, results and deadlines, the `archive.progress` notification, error codes, cancellation, SDK version |
| `plugins/remote/protocol.json` (repository root) | The remote plugin's own methods, events, enums, types and error texts (see Plugin-owned methods) |
| `plugins/git/protocol.json` (repository root) | The Git plugin's own methods, types and error texts (see Git plugin methods) |

## Connections

- The host launches the backend with `--pipe-name <name> --data-dir <dir>`.
- `plugin.handshake` is the first request on every connection. Params:
  `pluginId`, `hostVersion`, `sdkVersion`, and optional `client` (the Rust
  service sends `"simplefile-service"`). Result: `pluginId`, `pluginVersion`,
  `sdkVersion`. Any other request before it fails with `-32600`.
- `host.*` methods are requests the backend sends to the WinUI host (settings,
  progress, log, notify). On the service connection `host.log` is a local no-op
  and every other `host.*` call fails at once; a handler serving the service
  reaches the host through the host's own connection (`HostClient::host_connection`
  in the helper crate).

## Archive engine methods

Requests the Rust service sends to a backend that registers an archive engine.
Each replies `null` except `archive.list`. `operationId` is the id that
`cancel` and `archive.progress` use. The service sends one on every read- and
transfer-class call: the operation's own id when it is tracked, otherwise a
service id `svc-<pid>-<n>`.

The service holds a small pool of connections per plugin (up to 8) and keeps
at most one call in flight on each, so a listing never waits behind a long
extract. A backend that declares archive formats must therefore accept several
service connections at once. When the service's caller cancels, the service
sends `cancel` on another connection of the pool and waits at most the cancel
deadline (5 s) for the cancelled call's reply.

| Method | Params | Deadline |
| --- | --- | --- |
| `archive.list` | `archivePath` | list, 30 s |
| `archive.extract` | `archivePath`, `destination`, `operationId?` | transfer, 600 s idle |
| `archive.read_entry` | `archivePath`, `innerPath`, `destination`, `maxBytes`, `maxEntries`, `operationId?` | read, 120 s idle |
| `archive.create` | `archivePath`, `paths`, `operationId?` | transfer, 600 s idle |
| `archive.rename` | `archivePath`, `innerPath`, `newPath`, `operationId?` | transfer, 600 s idle |
| `archive.delete` | `archivePath`, `innerPaths`, `operationId?` | transfer, 600 s idle |
| `archive.add` | `archivePath`, `baseDir`, `paths`, `operationId?` | transfer, 600 s idle |

Semantics:

- Entry paths are `/`-separated and relative. `innerPath` matches the entry and
  its descendants by whole segments, so `docs` never matches `docs2`.
- `destination` for `extract` and `read_entry` is an empty folder the service
  created. Engines must refuse a non-empty destination and must never write
  outside it.
- `read_entry` writes the selection at its own relative paths inside
  `destination`. It refuses a selection over `maxEntries` or `maxBytes` by
  declared sizes, and fails with `Archive entry not found: <innerPath>` when
  nothing matches.
- `create` makes a new file and must fail rather than append.
- `rename` moves an entry and its descendants to the full path `newPath`, which
  also covers moves inside the archive. `rename`, `delete` and `add` change the
  archive in place and atomically. On failure or cancel the archive is left as
  it was.
- `add` stores `baseDir/<p>` (a file or a tree) at `<p>`. The caller guarantees
  that nothing but parent folders exists at those paths.

## Progress

`archive.progress {operationId, current, total, currentItem?}` is a
notification from the backend to the service, on the connection that carries
the request. It carries the request's `operationId`, with `current <= total`;
`total` 0 means unknown. Backends should send it at least every 5 s while
working, `read_entry` included: reading one entry of a solid archive can mean
decompressing a whole block first. Each one resets the caller's idle deadline.

## Cancellation

`cancel {operationId}` stops an operation. Only clients send it, whether the
client is the host or the service, as a request (replied `null`) or as a
notification, on any of its connections. The backend never sends `cancel`.

- A cancel is backend-wide: it is visible to every connection of the backend
  while its id is in flight.
- An id is in flight in two cases, and its flag lives exactly that long:
  - while a request carrying it as `params.operationId` runs. The helper
    crate's server records the id before the handler starts and drops the id
    and its flag when the handler returns;
  - for an id the host returned from `host.progress_begin`, from that reply
    until the backend calls `host.progress_complete` or `host.progress_fail`
    for it, or the connection that began it closes. This is how the host's
    Cancel on a plugin's own progress row reaches the plugin: no request
    carries that id.

  Handlers never clear a flag.
- A cancel for an id that is not in flight (it arrived before its request, or
  the id is unknown or finished) is kept among the 64 most recent early
  cancels. It is not a flag: `is_cancelled` reads false for that id until it
  goes in flight, and then it starts cancelled and leaves the list. In-flight
  flags are never evicted by later cancels.
- A cancelled archive-engine call fails with `-32000` and the exact message
  `Archive operation cancelled` (`cancel.cancelledMessage`).

## Plugin-owned methods

- A plugin's backend may serve its own methods to its own UI, through
  `IPluginHost.InvokeBackendAsync`. The plugin documents them in
  `plugins/<name>/protocol.json`: methods with their params and results, the
  events the backend sends with `emit`, and the error texts its UI matches.
  `plugins/remote/protocol.json` is the first.
- `cancel` works for them as for any request. A cancel naming the request's
  `params.operationId`, or an id that `host.progress_begin` returned, sets the
  flag. The remote plugin watches both ids: the Transfers row's Cancel sends
  the progress id, and the plugin UI's own Cancel sends the request id.
- The outcome of a cancel is the method's to define. Archive engine methods
  fail with `-32000` and `Archive operation cancelled`. The remote
  `transfer.run` replies normally with `cancelled: true` and ends its row with
  `host.progress_fail(row, "Transfer cancelled")`. The Git plugin's fetch,
  pull and push fail with `-32000` and `Git operation cancelled`, and end
  their row with `host.progress_fail(row, "Git operation cancelled")`.

### Git plugin methods

`plugins/git/protocol.json` documents them; `sumafile-git-backend`
(`plugins/git/backend`) serves them. Params are camelCase; results keep the
1.0.4 shapes, with snake_case fields, and their paths use native separators.
The backend sends no events.

| Method | Params | Result |
| --- | --- | --- |
| `repo.status` | `{ path }` | `GitRepositoryStatus` |
| `repo.decorations` | `{ folder }` | `GitFolderDecorations { in_repository, root, rows: [{ path, status }] }` |
| `paths.stage`, `paths.unstage`, `paths.discard` | `{ path, paths }` | `GitCommandResult` |
| `paths.diff` | `{ path, filePath }` | `string` |
| `repo.commit` | `{ path, message, operationId }` | `GitCommandResult` |
| `remote.fetch`, `remote.pull`, `remote.push` | `{ path, operationId }` | `GitCommandResult` |

- `repo.decorations` lists the folder's direct children only, as
  `<folder>\<name>`. Below no `.git` marker (the walk honours
  `GIT_CEILING_DIRECTORIES` and is skipped when `GIT_DIR` or `GIT_WORK_TREE`
  is set), for a missing folder and on any git failure it answers
  `{ in_repository: false, root: null, rows: [] }`, and outside a repository
  it runs no git.
- fetch, pull and push open a Transfers row, `Fetching <repository folder
  name>` (`Pulling …`, `Pushing …`), or run without one when no host
  connection exists. The row ends with `progress_complete`, or with
  `progress_fail` and the error text. A `cancel` naming the request's
  `operationId` or the row's id stops git and every process it started
  within 50 ms. `repo.commit` honours `cancel` for its `operationId` and opens
  no row.
- Without `git.exe` on an absolute `PATH` entry every method except
  `repo.decorations` fails with `Git was not found. Install Git for Windows or
  add git.exe to PATH.` and runs nothing.

## Errors

| Code | Meaning |
| --- | --- |
| `-32600` | request before `plugin.handshake` |
| `-32601` | method not found |
| `-32602` | invalid params |
| `-32603` | handler panicked |
| `-32000` | application error (`Archive operation cancelled` after a cancel) |

## Validation

`npm run check:ipc-schema` asserts:

- every `hostMethods` entry is a string literal in
  `src-winui/SimpleFile.PluginHost/Backend/PluginHostMethods.cs` and
  `crates/sumafile-plugin/src/host.rs`;
- every `archiveEngineMethods` key is a string literal in
  `crates/simplefile-service/src/plugin_archive/mod.rs` (the sender) and
  `crates/hello-plugin-backend/src/main.rs` (the fixture that serves them), and
  names a deadline from `deadlines`;
- `"archive.progress"` is a string literal in `crates/sumafile-plugin/src/host.rs`
  (the producer) and `crates/simplefile-service/src/plugin_archive/connection.rs`
  (the consumer);
- `cancel.cancelledMessage` is a string literal in
  `crates/simplefile-core/src/archive/engine.rs` and
  `crates/sumafile-plugin/src/lib.rs`;
- `sdkVersion` equals `PluginSdk.Version` in
  `src-winui/SimpleFile.PluginSdk/PluginSdk.cs`, `SDK_VERSION` in
  `crates/sumafile-plugin/src/lib.rs` and `PLUGIN_SDK_VERSION` in
  `crates/simplefile-service/src/plugin_archive/mod.rs`.

For each plugin protocol in its `pluginProtocols` table
(`plugins/remote/protocol.json` and `plugins/git/protocol.json`) it asserts:

- every key of `methods` and `events` is a string literal in each of the
  plugin's literal sources: the backend's handler, which serves them
  (`plugins/remote/backend/src/handler.rs`,
  `plugins/git/backend/src/handler.rs`), and the C# client's names for them
  where one exists (`plugins/remote/ui/SumaFile.Remote.Model/RemoteProtocol.cs`);
  every event a method `emits` or names as its `progressEvent` is documented
  under `events`, which is optional but never empty;
- for every `errors` entry, the text before its first `<placeholder>` (the
  whole text when it has none) starts a string literal in each file of its
  `sources`. The backend builds these texts with `format!`, so
  `"remote session not found: {session_id}"` matches
  `remote session not found: <id>`;
- it prints `Checked <label> plugin protocol: <n> methods, <n> events, <n> error texts.`,
  e.g. `Checked git plugin protocol: 10 methods, 0 events, 5 error texts.`
