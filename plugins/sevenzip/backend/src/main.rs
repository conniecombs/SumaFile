//! `sumafile-sevenzip-backend`: the backend process of the SumaFile 7-Zip
//! plugin. The host launches it with `--pipe-name` and `--data-dir`; it
//! serves the `archive.*` methods on that pipe until its last client
//! disconnects.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use sumafile_plugin::{serve, Args, PluginInfo};
use sumafile_sevenzip_backend::handler::SevenZipHandler;
use sumafile_sevenzip_backend::run;
use sumafile_sevenzip_backend::{PLUGIN_ID, PLUGIN_VERSION};

fn main() {
    let args = match Args::parse(std::env::args().skip(1)) {
        Ok(args) => args,
        Err(message) => {
            eprintln!("sumafile-sevenzip-backend: {message}");
            std::process::exit(2);
        }
    };
    // The bundled 7za.exe sits beside the backend exe.
    let backend_dir = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(Path::to_path_buf))
        .unwrap_or_else(|| PathBuf::from("."));
    // Two workers only route frames: 7-Zip runs on blocking threads, so a
    // cancel or a new listing is read while processes run on every
    // connection.
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("sumafile-sevenzip-backend: cannot start the runtime: {error}");
            std::process::exit(1);
        }
    };
    let info = PluginInfo {
        id: PLUGIN_ID.into(),
        version: PLUGIN_VERSION.into(),
    };
    let handler = Arc::new(SevenZipHandler::new(backend_dir));
    let served = runtime.block_on(serve(args, info, true, handler));
    // No 7-Zip process outlives the backend.
    run::shutdown();
    runtime.shutdown_timeout(Duration::from_secs(5));
    if let Err(message) = served {
        eprintln!("sumafile-sevenzip-backend: {message}");
        std::process::exit(1);
    }
}
