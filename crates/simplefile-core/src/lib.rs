//! Host-independent SimpleFile backend types and utilities.
//!
//! Shared domain logic lives here so the WinUI 3 named-pipe service can use it
//! without depending on a UI host.

/// User-facing SumaFile version shown in About, Settings, handshake, and updater.
/// Cargo / MSBuild `<Version>` stay numeric (`0.1.0`) for packaging APIs that require x.y.z.
pub const APP_DISPLAY_VERSION: &str = "BETA";

pub mod archive;
pub mod checksum;
pub mod cleanup;
pub mod compare;
pub mod dir_list;
pub mod drives;
pub mod file_ops;
pub mod git;
pub mod metadata;
pub mod models;
pub mod native_accel;
pub mod open_with;
pub mod path_conflict;
pub mod preview;
pub mod rar;
pub mod settings_store;
pub mod smart_folders;
pub mod tags;
pub mod terminal;
pub mod updater;
pub mod utils;
