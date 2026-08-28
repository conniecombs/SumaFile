//! Named-pipe JSON-RPC service used by the WinUI host.

mod binary;
pub mod dispatch;
pub mod progress;
mod scheduler;
pub mod search;
pub mod session;
pub mod shell;
pub mod watcher;

pub use dispatch::SessionState;
pub use session::serve_connection;

pub fn pipe_path(name: &str) -> String {
    if name.starts_with(r"\\.\pipe\") {
        name.to_string()
    } else {
        format!(r"\\.\pipe\{name}")
    }
}
