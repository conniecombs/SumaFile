//! Helper crate for SumaFile plugin backends.
//!
//! A backend executable calls [`Args::parse`] on its command line, then
//! [`serve`] with a [`PluginHandler`]. The crate owns the named pipe server,
//! the `plugin.handshake`, routing of requests to the handler, `cancel`
//! flags and the [`HostClient`] used for `host.*` calls back to the host.

use std::path::PathBuf;

use serde_json::Value;
use simplefile_ipc::{ERR_APPLICATION, ERR_INVALID_PARAMS, ERR_METHOD_NOT_FOUND};

mod handshake;
mod host;
mod server;

pub use async_trait::async_trait;
pub use host::{HostClient, DEFAULT_CALL_TIMEOUT, SERVICE_CONNECTION_ERROR};
pub use server::serve;

/// Version of the plugin SDK protocol this crate speaks.
pub const SDK_VERSION: &str = "1.0.0";

/// Identity reported in the handshake reply.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginInfo {
    pub id: String,
    pub version: String,
}

/// Command line the host launches a backend with.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Args {
    /// Bare pipe name, without the `\\.\pipe\` prefix.
    pub pipe_name: String,
    pub data_dir: PathBuf,
    /// Every argument other than `--pipe-name` and `--data-dir`, in order.
    pub rest: Vec<String>,
}

impl Args {
    /// Parses the arguments after the program name, e.g.
    /// `Args::parse(std::env::args().skip(1))`. Both `--pipe-name` and
    /// `--data-dir` are required.
    pub fn parse(args: impl Iterator<Item = String>) -> Result<Args, String> {
        let mut pipe_name = None;
        let mut data_dir = None;
        let mut rest = Vec::new();
        let mut args = args;
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--pipe-name" => {
                    pipe_name = Some(args.next().ok_or("--pipe-name needs a value")?);
                }
                "--data-dir" => {
                    data_dir = Some(PathBuf::from(
                        args.next().ok_or("--data-dir needs a value")?,
                    ));
                }
                _ => rest.push(arg),
            }
        }
        let pipe_name = pipe_name
            .filter(|name| !name.is_empty())
            .ok_or("missing --pipe-name")?;
        let data_dir = data_dir.ok_or("missing --data-dir")?;
        Ok(Args {
            pipe_name,
            data_dir,
            rest,
        })
    }
}

/// Error a handler returns; mapped to a JSON-RPC error code.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginError {
    /// `-32602`
    InvalidParams(String),
    /// `-32000`
    Application(String),
    /// `-32601`
    MethodNotFound,
}

impl PluginError {
    pub fn code(&self) -> i32 {
        match self {
            Self::InvalidParams(_) => ERR_INVALID_PARAMS,
            Self::Application(_) => ERR_APPLICATION,
            Self::MethodNotFound => ERR_METHOD_NOT_FOUND,
        }
    }

    pub fn message(&self) -> &str {
        match self {
            Self::InvalidParams(message) | Self::Application(message) => message,
            Self::MethodNotFound => "Method not found",
        }
    }
}

impl std::fmt::Display for PluginError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.message())
    }
}

impl std::error::Error for PluginError {}

/// Handles every request a connected client sends after its handshake,
/// whether the client is the host or the service.
#[async_trait]
pub trait PluginHandler: Send + Sync + 'static {
    /// Answers one request. Each call runs on its own task; a panic is
    /// answered with `-32603` "plugin handler panicked".
    ///
    /// `archive.*` requests arrive on the service connection; do not rely on
    /// host services there: `host.log` is a no-op and every other `host.*`
    /// call fails at once with [`SERVICE_CONNECTION_ERROR`].
    async fn handle(
        &self,
        method: &str,
        params: Value,
        host: &HostClient,
    ) -> Result<Value, PluginError>;
}
