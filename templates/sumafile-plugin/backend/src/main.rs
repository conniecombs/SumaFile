//! `example-plugin-backend`: the backend process of the SumaFile plugin
//! template, `com.example.template`.
//!
//! SumaFile starts it on the plugin's first backend call (`activateOn:
//! first-call` in `plugin.json`) with `--pipe-name <name> --data-dir <dir>`,
//! and with `TMP` and `TEMP` pointing into that data folder. It serves the
//! methods of [`handler::ExampleHandler`] on the pipe and exits once SumaFile
//! disconnects. Anything written to stderr reaches SumaFile's `plugins.log`.

mod handler;

use std::sync::Arc;

use sumafile_plugin::{serve, Args, PluginInfo};

#[tokio::main]
async fn main() {
    let args = match Args::parse(std::env::args().skip(1)) {
        Ok(args) => args,
        Err(message) => {
            eprintln!("example-plugin-backend: {message}");
            std::process::exit(2);
        }
    };
    // The handshake reports the id and version of plugin.json.
    let info = PluginInfo {
        id: handler::PLUGIN_ID.into(),
        version: env!("CARGO_PKG_VERSION").into(),
    };
    // Only SumaFile's UI half connects (plugin.json: acceptsMultipleClients
    // false); a plugin with an archive engine also serves the file service.
    if let Err(message) = serve(args, info, false, Arc::new(handler::ExampleHandler)).await {
        eprintln!("example-plugin-backend: {message}");
        std::process::exit(1);
    }
}
