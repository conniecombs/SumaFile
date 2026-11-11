//! Helper crate for SumaFile plugin backends.
//!
//! A backend executable calls [`Args::parse`] on its command line, then
//! [`serve`] with a [`PluginHandler`]. The crate owns the named pipe server,
//! the `plugin.handshake`, routing of requests to the handler, backend-wide
//! `cancel` flags, `archive.progress` notifications and the [`HostClient`]
//! used for `host.*` calls back to the host.
//!
//! The pipe server needs Windows. Elsewhere the crate still builds, so a
//! backend's handler and its `HostClient::fake` tests run on any platform,
//! and [`serve`] fails at once.

// Off Windows nothing calls the server-side plumbing `serve` would use.
#![cfg_attr(not(windows), allow(dead_code))]

use std::path::PathBuf;

use serde_json::Value;
use simplefile_ipc::{ERR_APPLICATION, ERR_INVALID_PARAMS, ERR_METHOD_NOT_FOUND};

mod handshake;
mod host;
#[cfg(windows)]
mod server;

pub use async_trait::async_trait;
pub use host::{
    HostClient, ARCHIVE_PROGRESS_NOTIFICATION, DEFAULT_CALL_TIMEOUT, EARLY_CANCEL_LIMIT,
    SERVICE_CONNECTION_ERROR,
};
#[cfg(windows)]
pub use server::serve;

/// Named pipes need Windows: off Windows, `serve` fails at once.
#[cfg(not(windows))]
pub async fn serve(
    _args: Args,
    _info: PluginInfo,
    _accepts_multiple_clients: bool,
    _handler: std::sync::Arc<dyn PluginHandler>,
) -> Result<(), String> {
    Err("SumaFile plugin backends serve a named pipe, which needs Windows".to_string())
}

/// Version of the plugin SDK protocol this crate speaks.
pub const SDK_VERSION: &str = "1.0.0";

/// Error message an archive operation fails with after its `cancel`. The
/// service and the host recognise a cancelled operation by this exact text.
pub const CANCELLED_MESSAGE: &str = "Archive operation cancelled";

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
