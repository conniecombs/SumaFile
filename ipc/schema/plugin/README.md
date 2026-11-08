# SumaFile plugin backend protocol (v1)

JSON-RPC 2.0 spoken over a named pipe between a plugin's backend executable and
either the WinUI host or the Rust service. Framing is the same as the main IPC
schema (`ipc/schema/v1`): little-endian `uint32` length prefix, UTF-8 JSON body.

| File | Role |
| --- | --- |
| `v1/protocol.json` | Handshake, `host.*` methods, archive-engine methods, cancellation, SDK version |

- `plugin.handshake` is the first request on every connection. Params:
  `pluginId`, `hostVersion`, `sdkVersion`, and optional `client` (the Rust
  service sends `"simplefile-service"`). Result: `pluginId`, `pluginVersion`,
  `sdkVersion`.
- `host.*` methods are requests the backend sends to the WinUI host (settings,
  progress, log, notify).
- `archive.list`, `archive.extract`, `archive.create` are requests the Rust
  service sends to a backend that registers an archive engine.
- `cancel {operationId}` is a request either side sends to stop an operation.

Validation: `npm run check:ipc-schema` asserts every `hostMethods` entry is a
string literal in `src-winui/SimpleFile.PluginHost/Backend/PluginHostMethods.cs`
and `crates/sumafile-plugin/src/host.rs`, and that `sdkVersion` equals
`PluginSdk.Version` in `src-winui/SimpleFile.PluginSdk/PluginSdk.cs`.
