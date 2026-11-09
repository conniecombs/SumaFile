//! Named-pipe JSON-RPC service used by the WinUI host.

mod binary;
pub mod dispatch;
pub mod plugin_archive;
pub mod progress;
pub mod remote;
mod scheduler;
pub mod search;
pub mod session;
pub mod session_cache;
pub mod shell;
pub mod watcher;

// The local-file transfer primitives live in the leaf crate `sumafile-transfer`
// so plugin backends can share them; these aliases keep `crate::transfer_*` paths.
#[cfg(test)]
use sumafile_transfer::test_io as transfer_test_io;
use sumafile_transfer::{
    journal as transfer_journal, manifest as transfer_manifest,
    reliability as transfer_reliability, staging as transfer_staging,
};

pub use dispatch::SessionState;
pub use session::serve_connection;
pub use session_cache::SessionCache;

pub fn pipe_path(name: &str) -> String {
    if name.starts_with(r"\\.\pipe\") {
        name.to_string()
    } else {
        format!(r"\\.\pipe\{name}")
    }
}
