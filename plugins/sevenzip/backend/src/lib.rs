//! Backend of the first-party SumaFile 7-Zip plugin (`com.sumafile.sevenzip`).

pub mod binary;
pub mod entries;
pub mod handler;
pub mod ops;
pub mod run;

/// The plugin id, as in `plugins/sevenzip/plugin.json`.
pub const PLUGIN_ID: &str = "com.sumafile.sevenzip";

/// The plugin version reported in the handshake, as in `plugin.json`.
pub const PLUGIN_VERSION: &str = "1.0.0";

/// Serializes the unit tests that start processes, so a test that asserts
/// on [`run::spawn_count`] sees only its own spawns.
#[cfg(test)]
pub(crate) fn test_lock() -> std::sync::MutexGuard<'static, ()> {
    static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
    LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}
