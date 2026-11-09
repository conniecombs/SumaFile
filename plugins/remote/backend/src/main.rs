//! `sumafile-remote-backend`: the backend process of the SumaFile FTP/SFTP
//! plugin. The host launches it with `--pipe-name` and `--data-dir`; it
//! serves the plugin-owned `profiles.*`, `session.*`, `fs.*` and
//! `transfer.run` methods on that pipe until the host disconnects.

use std::sync::Arc;
use std::time::Duration;

use sumafile_plugin::{serve, Args, PluginInfo};
use sumafile_remote_backend::handler::RemoteHandler;
use sumafile_remote_backend::{PLUGIN_ID, PLUGIN_VERSION};

/// Writes `sumafile-remote-backend: [<level>] <message>` to stderr for
/// warnings and errors, so the `log::warn!` calls of the providers, the
/// engine and `sumafile-transfer` reach `plugins.log` through the host's
/// stderr forwarding.
struct StderrLogger;

impl log::Log for StderrLogger {
    fn enabled(&self, metadata: &log::Metadata<'_>) -> bool {
        metadata.level() <= log::Level::Warn
    }

    fn log(&self, record: &log::Record<'_>) {
        if self.enabled(record.metadata()) {
            eprintln!(
                "sumafile-remote-backend: [{}] {}",
                record.level(),
                record.args()
            );
        }
    }

    fn flush(&self) {}
}

static LOGGER: StderrLogger = StderrLogger;

fn main() {
    if log::set_logger(&LOGGER).is_ok() {
        log::set_max_level(log::LevelFilter::Warn);
    }
    let args = match Args::parse(std::env::args().skip(1)) {
        Ok(args) => args,
        Err(message) => {
            eprintln!("sumafile-remote-backend: {message}");
            std::process::exit(2);
        }
    };
    // Two workers only route frames and pump progress: provider calls and
    // the transfer engine run on blocking threads.
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("sumafile-remote-backend: cannot start the runtime: {error}");
            std::process::exit(1);
        }
    };
    let info = PluginInfo {
        id: PLUGIN_ID.into(),
        version: PLUGIN_VERSION.into(),
    };
    let handler = Arc::new(RemoteHandler::new(args.data_dir.clone()));
    // Only the host connects: the service never dials this plugin.
    let served = runtime.block_on(serve(args, info, false, handler.clone()));
    handler.shutdown();
    runtime.shutdown_timeout(Duration::from_secs(5));
    if let Err(message) = served {
        eprintln!("sumafile-remote-backend: {message}");
        std::process::exit(1);
    }
}
