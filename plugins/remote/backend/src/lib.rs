//! Backend of the first-party SumaFile FTP/SFTP plugin (`com.sumafile.remote`).
//!
//! It owns the remote profiles (in its own data folder, with a one-time,
//! read-only import of the 1.0.4 `remote_profiles` rows per legacy database),
//! the Windows Credential Manager secrets, both providers and the sessions.
//! Provider calls are synchronous; the handler runs them on blocking threads.

pub mod bridge;
pub mod ftp;
pub mod handler;
pub mod legacy;
pub mod models;
pub mod path;
pub mod profiles;
pub mod provider;
pub mod secrets;
pub mod session;
pub mod settings;
pub mod sftp;
pub mod transfer;

/// The plugin id, as in `plugins/remote/plugin.json`.
pub const PLUGIN_ID: &str = "com.sumafile.remote";

/// The plugin version reported in the handshake, as in `plugin.json`.
pub const PLUGIN_VERSION: &str = "1.0.0";
