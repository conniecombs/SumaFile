# SumaFile IPC schema (v1)

Versioned request, response, and event schemas for the WinUI 3 ↔ Rust named-pipe JSON-RPC service.

| File | Role |
| --- | --- |
| `v1/protocol.json` | Framing, versioning, error codes, handshake, cancellation, operation IDs |
| `v1/types.json` | Shared DTO field maps (wire names) |
| `v1/commands.json` | `ipc.handshake` plus the 79 domain command names |
| `v1/events.json` | Emitted events, typed-but-not-emitted names, host-only drag events |
| `v1/goldens/` | Checked-in request/response/event samples |

Validation:

- `npm run check:ipc-schema` compares these files to `src-winui/SimpleFile.Ipc/Protocol.cs`, `crates/simplefile-service/src/dispatch.rs`, and `crates/simplefile-core/src/models.rs`
- `cargo test -p simplefile-ipc` loads the same JSON and asserts counts, casing, and golden keys
