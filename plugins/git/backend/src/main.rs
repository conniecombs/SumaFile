//! `sumafile-git-backend`: the backend process of the SumaFile Git plugin.
//! The host launches it with `--pipe-name` and `--data-dir`; it serves the
//! plugin-owned `repo.*`, `paths.*` and `remote.*` methods on that pipe until
//! the host disconnects.

use std::sync::Arc;
use std::time::Duration;

use sumafile_git_backend::handler::GitHandler;
use sumafile_git_backend::{PLUGIN_ID, PLUGIN_VERSION};
use sumafile_plugin::{serve, Args, PluginInfo};

/// Writes `sumafile-git-backend: [<level>] <message>` to stderr for warnings
/// and errors, so they reach `plugins.log` through the host's stderr
/// forwarding.
struct StderrLogger;

impl log::Log for StderrLogger {
    fn enabled(&self, metadata: &log::Metadata<'_>) -> bool {
        metadata.level() <= log::Level::Warn
    }

    fn log(&self, record: &log::Record<'_>) {
        if self.enabled(record.metadata()) {
            eprintln!(
                "sumafile-git-backend: [{}] {}",
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
            eprintln!("sumafile-git-backend: {message}");
            std::process::exit(2);
        }
    };
    // Two workers only route frames: git runs on blocking threads.
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("sumafile-git-backend: cannot start the runtime: {error}");
            std::process::exit(1);
        }
    };
    let info = PluginInfo {
        id: PLUGIN_ID.into(),
        version: PLUGIN_VERSION.into(),
    };
    let handler = Arc::new(GitHandler::new(args.data_dir.clone()));
    // Only the host connects: the service never dials this plugin.
    let served = runtime.block_on(serve(args, info, false, handler));
    runtime.shutdown_timeout(Duration::from_secs(5));
    if let Err(message) = served {
        eprintln!("sumafile-git-backend: {message}");
        std::process::exit(1);
    }
}
