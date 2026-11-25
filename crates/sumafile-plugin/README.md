# sumafile-plugin

Helper crate for the backend process of a [SumaFile](https://github.com/conniecombs/SumaFile) plugin. SumaFile is a
Windows file manager; a plugin can ship a backend executable that the host starts on demand and talks to over a named
pipe with length-prefixed JSON-RPC 2.0.

The crate owns the transport, so a backend only answers requests:

- the named pipe server (`\\.\pipe\<name>`), framing and the `plugin.handshake`;
- the launch token: `serve` reads the secret SumaFile writes to the backend's standard input and answers only a
  handshake that carries it;
- routing every request to your `PluginHandler`, one task per call, with JSON-RPC error codes from `PluginError`;
- backend-wide `cancel` flags and `archive.progress` notifications for archive engines;
- `HostClient`, for `host.*` calls back to SumaFile: settings, progress, log, notify and events.

The pipe server needs Windows. On other platforms the crate still builds, so a handler and its tests run anywhere,
and `serve` fails at once.

## Dependencies

The SumaFile repository packages this crate but does not publish it, so a backend takes it by path from a SumaFile
checkout (`<checkout>` below). The crate finds its own dependency, `simplefile-ipc`, in the checkout, and it needs
Rust 1.98.0 or newer (its `rust-version`). The example below also needs `serde_json` and `tokio`:

```toml
[dependencies]
serde_json = "1"
sumafile-plugin = { path = "<checkout>/crates/sumafile-plugin" }
tokio = { version = "1", features = ["macros", "rt-multi-thread"] }

[dev-dependencies]
sumafile-plugin = { path = "<checkout>/crates/sumafile-plugin", features = ["test-support"] }
```

`async_trait` is re-exported by the crate, so a backend needs no dependency of its own for it.

## Example

```rust,no_run
use std::sync::Arc;

use serde_json::{json, Value};
use sumafile_plugin::{async_trait, serve, Args, HostClient, PluginError, PluginHandler, PluginInfo};

struct Echo;

#[async_trait]
impl PluginHandler for Echo {
    async fn handle(&self, method: &str, params: Value, _host: &HostClient) -> Result<Value, PluginError> {
        match method {
            "example.echo" => {
                let text = params["text"]
                    .as_str()
                    .ok_or_else(|| PluginError::InvalidParams("text must be a string".into()))?;
                Ok(json!({ "text": text, "length": text.chars().count() }))
            }
            _ => Err(PluginError::MethodNotFound),
        }
    }
}

#[tokio::main]
async fn main() {
    // The host starts the backend with --pipe-name <name> --data-dir <folder>.
    let args = Args::parse(std::env::args().skip(1)).unwrap_or_else(|message| {
        eprintln!("{message}");
        std::process::exit(2);
    });
    let info = PluginInfo { id: "com.example.echo".into(), version: "1.0.0".into() };
    if let Err(message) = serve(args, info, false, Arc::new(Echo)).await {
        eprintln!("{message}");
        std::process::exit(1);
    }
}
```

`serve` returns when the last client disconnects, so the backend exits when SumaFile closes the connection. Pass
`true` for `accepts_multiple_clients` when the backend is also an archive engine: the SumaFile file service then
connects alongside the host, with up to 8 connections at once and one more when it sends a `cancel`.

## Testing a handler

With the `test-support` feature, `HostClient::fake(settings)` is an in-memory host with the same API. It reads and
writes the settings map you pass, hands out progress ids of its own and records what the handler did:

- `fake_log()`: the `(level, message)` pairs the handler logged;
- `fake_events()`: the `(event, params)` of every `emit`;
- `fake_progress_calls()`: every `host.progress_*` call in order, as text such as `begin <title>` and
  `complete <id>`;
- `fake_progress()`: the `(operationId, current, total, currentItem)` of every `archive_progress`;
- `fake_cancel(operation_id)`: marks an id cancelled, as a client's `cancel` would, so `is_cancelled` returns true.

## Protocol

The wire protocol, the handshake, every `host.*` method and the error codes are documented in
[`ipc/schema/plugin`](https://github.com/conniecombs/SumaFile/tree/main/ipc/schema/plugin) and in the plugin author
guide, [`docs/PLUGINS.md`](https://github.com/conniecombs/SumaFile/blob/main/docs/PLUGINS.md). Backends written in
another language implement the framing directly.

The crate speaks plugin SDK protocol `1.0.0` (`SDK_VERSION`). It also exports `CANCELLED_MESSAGE`, the message a
cancelled archive operation fails with, `ARCHIVE_PROGRESS_NOTIFICATION`, `DEFAULT_CALL_TIMEOUT` (30 s for a `host.*`
call), `EARLY_CANCEL_LIMIT` and `SERVICE_CONNECTION_ERROR`. The constants it shares with the SumaFile host live in
[`simplefile-ipc`](https://github.com/conniecombs/SumaFile/tree/main/crates/simplefile-ipc).

## License

MIT
