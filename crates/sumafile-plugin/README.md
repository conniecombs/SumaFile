# sumafile-plugin

Helper crate for the backend process of a [SumaFile](https://github.com/conniecombs/SumaFile) plugin. SumaFile is a
Windows file manager; a plugin can ship a backend executable that the host starts on demand and talks to over a named
pipe with length-prefixed JSON-RPC 2.0.

The crate owns the transport, so a backend only answers requests:

- the named pipe server (`\\.\pipe\<name>`), framing and the `plugin.handshake`;
- routing every request to your `PluginHandler`, one task per call, with JSON-RPC error codes from `PluginError`;
- backend-wide `cancel` flags and `archive.progress` notifications for archive engines;
- `HostClient`, for `host.*` calls back to SumaFile: settings, progress, log, notify and events.

The pipe server needs Windows. On other platforms the crate still builds, so a handler and its tests run anywhere,
and `serve` fails at once.

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
`true` for `accepts_multiple_clients` when the backend is also an archive engine, which the SumaFile service connects
to as a second client.

## Testing a handler

With the `test-support` feature, `HostClient::fake` is an in-memory host with the same API: settings you choose, and
the progress, log and event calls your handler made.

```toml
[dev-dependencies]
sumafile-plugin = { version = "1", features = ["test-support"] }
```

## Protocol

The wire protocol, the handshake, every `host.*` method and the error codes are documented in
[`ipc/schema/plugin`](https://github.com/conniecombs/SumaFile/tree/main/ipc/schema/plugin) and in the plugin author
guide, [`docs/PLUGINS.md`](https://github.com/conniecombs/SumaFile/blob/main/docs/PLUGINS.md). Backends written in
another language implement the framing directly.

The crate speaks plugin SDK protocol `1.0.0` (`SDK_VERSION`). The constants it shares with the SumaFile host live in
[`simplefile-ipc`](https://github.com/conniecombs/SumaFile/tree/main/crates/simplefile-ipc).

## License

MIT
