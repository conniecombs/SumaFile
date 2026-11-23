# SumaFile IPC schema (v1)

The contract between the WinUI app and the Rust file service: JSON-RPC over a
named pipe, with binary frames for the largest payloads. Both sides are
generated from these files and checked against them.

| File | Role |
| --- | --- |
| `v1/protocol.json` | Framing, size limits, binary frames, error codes, the handshake, cancellation, operation ids |
| `v1/commands.json` | Every method: the `ipc.*` control methods and the domain commands, with parameters and result types. `domainMethodCount` is the number of domain commands |
| `v1/events.json` | Notifications. `emitted` lists the ones the service sends |
| `v1/types.json` | The wire types and their field names |
| `v1/goldens/` | Checked-in samples of requests, responses and events |
| `v1/open-with-policy.json` | The Open With policy both sides share |
| `plugin/` | The plugin protocol; see [its README](plugin/README.md) |

## Transfer receipts

A committed folder merge includes a `changes` object listing the relative
files added, directories created, and move-source directories removed. A
client must use those changes for Undo. For a large receipt, collect the
`transfer-changes` events for the operation before processing the terminal
reply. Each event identifies a result index and field. The `streamed_paths`
count lets the client check completeness on success, partial failure or
cancellation. The [wire types](v1/types.json) define both shapes.

## Changing the contract

1. Edit the JSON in `v1/`.
2. Run `npm run generate:ipc-bindings`. It writes
   `src-winui/SimpleFile.Ipc/Protocol.Generated.cs`,
   `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.Generated.cs` and
   `crates/simplefile-ipc/src/protocol_generated.rs`. Do not edit those by
   hand. `npm run generate:open-with-policy` does the same for the Open With
   policy.
3. Implement the handler in `crates/simplefile-service/src/dispatch/` and the
   models on both sides.

## Validation

- `npm run check:ipc-generated` and `npm run check:open-with-policy` fail when
  a generated file is not what the schema produces.
- `npm run check:ipc-schema` compares the schema with the generated C#
  constants, the service's dispatch modules, the Rust models in
  `crates/simplefile-core/src/models.rs`, and the plugin protocols.
- `cargo test -p simplefile-ipc` loads the same JSON and checks counts, casing
  and the golden samples.

The first two are part of `npm run check` and the third of `npm run check:rust`;
CI runs all of them.
[Architecture](../../docs/ARCHITECTURE.md) describes framing, the handshake and
the steps for adding a method in full.
