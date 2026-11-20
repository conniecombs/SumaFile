# SumaFile plugin backend protocol (v1)

JSON-RPC 2.0 spoken over a named pipe between a plugin's backend executable and
either the WinUI host or the Rust service. Framing is the same as the main IPC
schema (`ipc/schema/v1`): little-endian `uint32` length prefix, UTF-8 JSON body,
at most 80 MiB per frame in either direction.

The author guide, [docs/PLUGINS.md](../../../docs/PLUGINS.md), explains the
protocol for backend authors ("The backend"), with the lifetime, limits and
deadlines around it. This file is the reference for the wire shapes.

The helper crate, `crates/sumafile-plugin`, implements the backend side of
this protocol. The repository packs the crate but does not publish it; the
guide's "Packages" section says how a backend outside the repository takes it.

| File | Role |
| --- | --- |
| `v1/protocol.json` | Command line, handshake, `host.*` methods, archive-engine methods with their params, results and deadlines, the `archive.progress` notification, error codes, cancellation, SDK version |
| `plugins/remote/protocol.json` (repository root) | The remote plugin's own methods, events, enums, types and error texts (see Plugin-owned methods) |
| `plugins/git/protocol.json` (repository root) | The Git plugin's own methods, types and error texts (see Plugin-owned methods) |

## Connections

- The host launches the backend with `--pipe-name <name> --data-dir <dir>`.
- `plugin.handshake` is the first request on every connection. Params:
  `pluginId`, `hostVersion`, `sdkVersion`, and optional `client` (the Rust
  service sends `"simplefile-service"`). Result: `pluginId`, `pluginVersion`,
  `sdkVersion`. Any other request before it fails with `-32600`.
- `hostVersion` is the sender's own version. The host sends the assembly
  version of `SimpleFile.PluginHost`, four numeric parts; the service sends
  SumaFile's display version (`APP_DISPLAY_VERSION` in
  `crates/simplefile-core`).
- The handshake reply's `pluginId` must equal the manifest id and its
  `sdkVersion` must have major 1, or the start fails. The host parses the
  `sdkVersion` as a numeric version of two to four parts; the service compares
  the text before the first dot. The host waits 5 s for the backend to
  connect, then 5 s for the reply.
- `host.*` methods are requests the backend sends to the WinUI host (settings,
  progress, log, notify). On the service connection `host.log` is a local no-op
  and every other `host.*` call fails at once; a handler serving the service
  reaches the host through the host's own connection
  (`HostClient::host_connection` in the helper crate). The service never
  answers a request from a backend.
- The host's request ids are integers, and a backend's `host.*` requests must
  use 32-bit integer ids too: the host reads a request with any other id as a
  notification and never answers it.

## Host methods

Requests from the backend to the WinUI host. `params` is an object; every
error below is `-32602` (invalid params) unless noted.

| Method | Params | Result | Errors |
| --- | --- | --- | --- |
| `host.get_settings` | `{ keys: string[] }` | `{ values: { <key>: string \| null } }` | `'keys' must be an array.`, `Invalid settings key.` |
| `host.set_setting` | `{ key: string, value: string }` | `null` | `'key' must be a string.`, `'value' must be a string.`, `Invalid settings key.` |
| `host.progress_begin` | `{ title: string }` | `{ operationId: string }` | `'title' must be a string.` |
| `host.progress_report` | `{ operationId: string, current: int64, total: int64, detail?: string }` | `null` | `'current' must be an integer.`, `'total' must be an integer.`, `Unknown operationId.` |
| `host.progress_complete` | `{ operationId: string }` | `null` | `Unknown operationId.` |
| `host.progress_fail` | `{ operationId: string, message: string }` | `null` | `'message' must be a string.`, `Unknown operationId.` |
| `host.log` | `{ level: "info" \| "warn" \| "error", message: string }` | `null` | `'level' must be a string.`, `'level' must be info, warn or error.`, `'message' must be a string.` |
| `host.notify` | `{ message: string, isError?: boolean }` | `null` | `'message' must be a string.` |

- Settings keys match `^[a-z0-9][a-zA-Z0-9_-]{0,63}$`; the host stores them as
  `plugin.<plugin id>.<key>`. An unset key reads as `null`.
- Methods that take `operationId` also fail with `'operationId' must be a
  string.` Only ids this backend's `host.progress_begin` returned are accepted;
  rows still open when the backend stops fail with `Plugin backend stopped.`
- `host.notify` shows the message in SumaFile's message bar, as an error when
  `isError` is `true`. `host.log` writes `[<level>] <message>` to `plugins.log`.
- An unknown `host.*` method fails with `-32601`
  (`Unknown host method '<method>'.`); a host-side failure, such as a settings
  write that failed, with `-32603` and its message.
- The host sets no deadline on its replies; the helper crate waits 30 s
  (`DEFAULT_CALL_TIMEOUT`).

## Archive engine methods

Requests the Rust service sends to a backend that registers an archive engine.
Each replies `null` except `archive.list`. `operationId` is the id that
`cancel` and `archive.progress` use. The schema marks it optional
(`string?`), and an engine must accept a call without one (it then reports no
progress and cannot be cancelled), but SumaFile's service sends one on every
read- and transfer-class call: the operation's own id when it is tracked,
otherwise a service id `svc-<pid>-<n>`. `archive.list` carries none.

The service holds a small pool of connections per plugin (up to 8) and keeps
at most one call in flight on each, so a listing never waits behind a long
extract. A backend that declares archive formats must therefore accept several
service connections at once. When the service's caller cancels, the service
sends `cancel` on another connection of the pool, or on a new one beyond the 8
when none is idle, and waits at most the cancel deadline (5 s) for the
cancelled call's reply.

| Method | Params | Result | Deadline |
| --- | --- | --- | --- |
| `archive.list` | `{ archivePath: string }` | `{ entries: [{ path: string, isDir?: boolean, size?: number, compressedSize?: number }] }` | list, 30 s |
| `archive.extract` | `{ archivePath: string, destination: string, operationId?: string }` | `null` | transfer, 600 s idle |
| `archive.read_entry` | `{ archivePath: string, innerPath: string, destination: string, maxBytes: number, maxEntries: number, operationId?: string }` | `null` | read, 120 s idle |
| `archive.create` | `{ archivePath: string, paths: string[], operationId?: string }` | `null` | transfer, 600 s idle |
| `archive.rename` | `{ archivePath: string, innerPath: string, newPath: string, operationId?: string }` | `null` | transfer, 600 s idle |
| `archive.delete` | `{ archivePath: string, innerPaths: string[], operationId?: string }` | `null` | transfer, 600 s idle |
| `archive.add` | `{ archivePath: string, baseDir: string, paths: string[], operationId?: string }` | `null` | transfer, 600 s idle |

A listed entry needs `path`; `isDir` defaults to `false`, `size` and
`compressedSize` to 0. `archivePath`, `destination`, `baseDir` and the `paths`
of `archive.create` are full Windows paths; `innerPath`, `newPath`,
`innerPaths` and the `paths` of `archive.add` are entry paths. A failure is an
error reply, normally `-32000` with a message the user sees.

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

A plugin's backend may serve its own methods to its own UI, through
`IPluginHost.InvokeBackendAsync`. The plugin documents them in
`plugins/<name>/protocol.json`: methods with their params and results, the
events the backend sends with `emit`, and the error texts its UI matches.

Two bundled plugins have such a file. Each file is the reference for its
plugin's methods: `notes` texts, for the file and for the methods that need
one, describe the behaviour. This README does not repeat them:

| File | Backend | Top-level keys |
| --- | --- | --- |
| `plugins/remote/protocol.json` | `sumafile-remote-backend` (`plugins/remote/backend`) | `plugin`, `notes`, `methods`, `events`, `enums`, `types`, `errors` |
| `plugins/git/protocol.json` | `sumafile-git-backend` (`plugins/git/backend`) | `plugin`, `notes`, `methods`, `types`, `errors`; the backend sends no events |

`npm run check:ipc-schema` checks both files against the code (see
Validation).

- `cancel` works for these methods as for any request. A cancel naming the
  request's `params.operationId`, or an id that `host.progress_begin` returned,
  sets the flag. The remote plugin watches both ids: the Transfers row's Cancel
  sends the progress id, and the plugin UI's own Cancel sends the request id.
- The outcome of a cancel is the method's to define. Archive engine methods
  fail with `-32000` and `Archive operation cancelled`. The remote
  `transfer.run` replies normally with `cancelled: true` and ends its row with
  `host.progress_fail(row, "Transfer cancelled")`. The Git plugin's fetch,
  pull and push fail with `-32000` and `Git operation cancelled`, and end
  their row with `host.progress_fail(row, "Git operation cancelled")`.

## Errors

| Code | Meaning |
| --- | --- |
| `-32700` | parse error: the frame is not valid JSON (the reply's `id` is `null`) |
| `-32600` | request before `plugin.handshake`, or a frame that is neither a request nor a reply |
| `-32601` | method not found |
| `-32602` | invalid params |
| `-32603` | internal error: a handler that panicked, or a host-side failure answering a `host.*` request |
| `-32000` | application error (`Archive operation cancelled` after a cancel) |

Who answers with which code:

- The backend (the helper crate's server, which other backends should match):
  `-32600` for any request before `plugin.handshake`
  (`plugin.handshake must come before <method>`) and for a frame that is
  neither a request nor a reply; `-32700` with `"id": null` for a frame that is
  not JSON; `-32601` for a method it does not serve; `-32602` for invalid
  params, including a `cancel` without a string `operationId`; `-32603` when a
  handler panicked; `-32000` for the method's own failure.
- The host, answering `host.*`: `-32601` for an unknown method, `-32602` for
  invalid params (see Host methods), `-32603` for a host-side failure.
- `-32001` (host-owned) belongs to the main IPC schema; no plugin connection
  uses it.

## Validation

`npm run check:ipc-schema` (`scripts/check-ipc-schema.mjs`) asserts for
`v1/protocol.json`:

- every `hostMethods` entry is a string literal in
  `src-winui/SimpleFile.PluginHost/Backend/PluginHostMethods.cs` and
  `crates/sumafile-plugin/src/host.rs`;
- `handshake.method` is `plugin.handshake` and `cancel.method` is `cancel`;
- every `archiveEngineMethods` entry has a `params` object, a `result` and a
  `deadline` that `deadlines` defines, and its key is a string literal in
  `crates/simplefile-service/src/plugin_archive/mod.rs` (the sender) and in
  the two backends that serve the methods,
  `crates/hello-plugin-backend/src/main.rs` (the test fixture) and
  `plugins/sevenzip/backend/src/handler.rs` (the 7-Zip plugin);
- `notifications` documents `archive.progress`, and `"archive.progress"` is a
  string literal in `crates/sumafile-plugin/src/host.rs` (the producer) and
  `crates/simplefile-service/src/plugin_archive/connection.rs` (the consumer);
- `cancel.cancelledMessage` is a string literal in
  `crates/simplefile-core/src/archive/engine.rs`,
  `crates/sumafile-plugin/src/lib.rs` and
  `src-winui/SimpleFile.Core/FileOperationService.cs`;
- `sdkVersion` equals `PluginSdk.Version` in
  `src-winui/SimpleFile.PluginSdk/PluginSdk.cs`, `SDK_VERSION` in
  `crates/sumafile-plugin/src/lib.rs` and `PLUGIN_SDK_VERSION` in
  `crates/simplefile-service/src/plugin_archive/mod.rs`.

When no check has failed up to that point, it prints a line that starts
`Checked plugin protocol v1:` and gives the number of host methods, the number
of archive engine methods and the SDK version.

For each plugin protocol in its `pluginProtocols` table
(`plugins/remote/protocol.json` and `plugins/git/protocol.json`) it asserts:

- `plugin` is the plugin's id (`com.sumafile.remote`, `com.sumafile.git`);
- `methods` is not empty, and every method has `params` and a `result`;
- every key of `methods` and `events` is a string literal in each of the
  plugin's literal sources: the backend's handler, which serves them
  (`plugins/remote/backend/src/handler.rs`,
  `plugins/git/backend/src/handler.rs`), and the C# client's names for them
  (`plugins/remote/ui/SumaFile.Remote.Model/RemoteProtocol.cs`,
  `plugins/git/ui/SumaFile.Git.Model/GitProtocol.cs`);
- every event a method `emits` or names as its `progressEvent` is documented
  under `events`, which is optional but never empty;
- every `errors` entry has a `text` and at least one file in `sources`, and
  the text before its first `<placeholder>` (the whole text when it has none)
  starts a string literal in each of those files. The backend builds these
  texts with `format!`, so `"remote session not found: {session_id}"` matches
  `remote session not found: <id>`.

For each file that passes it prints
`Checked <label> plugin protocol: <n> methods, <n> events, <n> error texts.`
