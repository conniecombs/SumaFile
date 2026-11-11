//! The one-time, read-only import of the 1.0.4 `remote_profiles` rows, and
//! the legacy transfer policy. Markers are kept per legacy database.

use crate::profiles::{set_meta_on, ProfileStore};
use chrono::Utc;
use rusqlite::types::Value;
use rusqlite::{Connection, OpenFlags, OptionalExtension, TransactionBehavior};
use std::path::{Path, PathBuf};
use std::time::Duration;

pub const LEGACY_PROFILES_IMPORTED: &str = "legacy_profiles_imported";
pub const LEGACY_TRANSFER_SETTINGS_SEEDED: &str = "legacy_transfer_settings_seeded";

/// The 1.0.4 settings key of the transfer policy JSON.
const LEGACY_POLICY_KEY: &str = "transfer.policy";
const METADATA_DB_NAME: &str = "metadata.db";
const METADATA_DB_ENV: &str = "SIMPLEFILE_METADATA_DB";
const APP_DATA_DIR_ENV: &str = "SIMPLEFILE_APP_DATA_DIR";
/// The service's folder names, in its candidate order under each root.
const APPDATA_FOLDERS: [&str; 3] = ["com.simplefile.desktop", "SumaFile", "SimpleFile"];
const LOCALAPPDATA_FOLDERS: [&str; 3] = ["SumaFile", "com.simplefile.desktop", "SimpleFile"];
/// The legacy database may be open in a running 1.0.4 service.
const LEGACY_BUSY_TIMEOUT: Duration = Duration::from_secs(5);

/// Every column of the 1.0.4 `remote_profiles` table, in insert order.
const LEGACY_COLUMNS: [&str; 14] = [
    "id",
    "name",
    "protocol",
    "host",
    "port",
    "username",
    "root_path",
    "auth_kind",
    "insecure_plain_ftp",
    "passive_mode",
    "credential_target",
    "private_key_path",
    "trusted_host_fingerprint",
    "updated_at",
];

#[derive(Debug, PartialEq, Eq)]
pub enum LegacyImport {
    NoLegacyTable,
    AlreadyImported,
    Imported(usize),
}

/// The metadata.db the 1.0.4 service used, resolved in the service's order; None when no candidate exists. Never creates anything.
pub fn legacy_metadata_db_path(env: &dyn Fn(&str) -> Option<String>) -> Option<PathBuf> {
    let non_empty = |key: &str| env(key).filter(|value| !value.is_empty());
    if let Some(explicit) = non_empty(METADATA_DB_ENV) {
        return Some(PathBuf::from(explicit));
    }

    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Some(app_data_dir) = non_empty(APP_DATA_DIR_ENV) {
        candidates.push(PathBuf::from(app_data_dir));
    }
    if let Some(roaming) = non_empty("APPDATA") {
        let root = PathBuf::from(roaming);
        candidates.extend(APPDATA_FOLDERS.iter().map(|folder| root.join(folder)));
    }
    if let Some(local) = non_empty("LOCALAPPDATA") {
        let root = PathBuf::from(local);
        candidates.extend(LOCALAPPDATA_FOLDERS.iter().map(|folder| root.join(folder)));
    }

    let mut unique: Vec<PathBuf> = Vec::new();
    for candidate in candidates {
        if !unique.contains(&candidate) {
            unique.push(candidate);
        }
    }
    unique
        .into_iter()
        .map(|folder| folder.join(METADATA_DB_NAME))
        .find(|path| path.exists())
}

/// "<marker>:<std::path::absolute(legacy_db), lowercased>". Markers are per legacy database.
pub fn legacy_marker_key(marker: &str, legacy_db: &Path) -> String {
    let absolute = std::path::absolute(legacy_db).unwrap_or_else(|_| legacy_db.to_path_buf());
    format!("{marker}:{}", absolute.to_string_lossy().to_lowercase())
}

/// Reads every legacy remote_profiles row with SQLITE_OPEN_READ_ONLY. Then, in one BEGIN IMMEDIATE transaction on the store:
/// returns AlreadyImported when the store's plugin_meta has legacy_marker_key(LEGACY_PROFILES_IMPORTED, legacy_db); otherwise
/// inserts every row verbatim with INSERT OR IGNORE (credential_target, trusted_host_fingerprint and updated_at included, so an
/// id the store already has keeps the store's row), writes that marker with an RFC 3339 time, and commits.
/// A missing file or a missing table returns NoLegacyTable and writes no marker.
pub fn import_legacy_profiles(
    store: &ProfileStore,
    legacy_db: &Path,
) -> Result<LegacyImport, String> {
    let Some(rows) = read_legacy_rows(legacy_db)? else {
        return Ok(LegacyImport::NoLegacyTable);
    };

    let marker = legacy_marker_key(LEGACY_PROFILES_IMPORTED, legacy_db);
    let mut connection = store.connection()?;
    // IMMEDIATE takes the write lock before the marker is read, so a second
    // backend importing at the same time waits (busy_timeout), then sees the marker.
    let transaction = connection
        .transaction_with_behavior(TransactionBehavior::Immediate)
        .map_err(|error| error.to_string())?;
    let already = transaction
        .query_row(
            "SELECT 1 FROM plugin_meta WHERE key = ?1",
            [&marker],
            |_| Ok(()),
        )
        .optional()
        .map_err(|error| error.to_string())?
        .is_some();
    if already {
        return Ok(LegacyImport::AlreadyImported);
    }

    let mut imported = 0;
    {
        let placeholders = (1..=LEGACY_COLUMNS.len())
            .map(|index| format!("?{index}"))
            .collect::<Vec<_>>()
            .join(", ");
        let mut insert = transaction
            .prepare(&format!(
                "INSERT OR IGNORE INTO remote_profiles ({}) VALUES ({placeholders})",
                LEGACY_COLUMNS.join(", ")
            ))
            .map_err(|error| error.to_string())?;
        for row in &rows {
            imported += insert
                .execute(rusqlite::params_from_iter(row.iter()))
                .map_err(|error| error.to_string())?;
        }
    }
    set_meta_on(&transaction, &marker, &Utc::now().to_rfc3339())?;
    transaction.commit().map_err(|error| error.to_string())?;
    Ok(LegacyImport::Imported(imported))
}

/// The `transfer.policy` JSON from the legacy settings table, if any.
pub fn legacy_transfer_policy(legacy_db: &Path) -> Result<Option<String>, String> {
    let Some(connection) = open_legacy_read_only(legacy_db)? else {
        return Ok(None);
    };
    if !table_exists(&connection, "settings")? {
        return Ok(None);
    }
    connection
        .query_row(
            "SELECT value FROM settings WHERE key = ?1",
            [LEGACY_POLICY_KEY],
            |row| row.get::<_, String>(0),
        )
        .optional()
        .map_err(|error| error.to_string())
}

/// Every legacy row as raw values in [`LEGACY_COLUMNS`] order; `None` when
/// the file or the table is missing. A column an older schema lacks reads as NULL.
fn read_legacy_rows(legacy_db: &Path) -> Result<Option<Vec<Vec<Value>>>, String> {
    let Some(connection) = open_legacy_read_only(legacy_db)? else {
        return Ok(None);
    };
    if !table_exists(&connection, "remote_profiles")? {
        return Ok(None);
    }

    let existing = {
        let mut statement = connection
            .prepare("PRAGMA table_info(remote_profiles)")
            .map_err(|error| error.to_string())?;
        let names = statement
            .query_map([], |row| row.get::<_, String>(1))
            .map_err(|error| error.to_string())?;
        names
            .collect::<Result<Vec<_>, _>>()
            .map_err(|error| error.to_string())?
    };
    let select = LEGACY_COLUMNS
        .iter()
        .map(|column| {
            if existing.iter().any(|name| name == column) {
                (*column).to_string()
            } else {
                format!("NULL AS {column}")
            }
        })
        .collect::<Vec<_>>()
        .join(", ");
    let mut statement = connection
        .prepare(&format!("SELECT {select} FROM remote_profiles ORDER BY id"))
        .map_err(|error| error.to_string())?;
    let rows = statement
        .query_map([], |row| {
            (0..LEGACY_COLUMNS.len())
                .map(|index| row.get::<_, Value>(index))
                .collect::<rusqlite::Result<Vec<_>>>()
        })
        .map_err(|error| error.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| error.to_string())?;
    Ok(Some(rows))
}

/// Opens the legacy database read-only; `None` when the file does not exist.
/// A read-only connection never creates the file, a journal or any other
/// file beside it.
fn open_legacy_read_only(legacy_db: &Path) -> Result<Option<Connection>, String> {
    if !legacy_db.is_file() {
        return Ok(None);
    }
    let connection = Connection::open_with_flags(
        legacy_db,
        OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
    )
    .map_err(|error| {
        format!(
            "cannot open the legacy database {}: {error}",
            legacy_db.display()
        )
    })?;
    connection
        .busy_timeout(LEGACY_BUSY_TIMEOUT)
        .map_err(|error| error.to_string())?;
    Ok(Some(connection))
}

fn table_exists(connection: &Connection, table: &str) -> Result<bool, String> {
    connection
        .query_row(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?1",
            [table],
            |_| Ok(()),
        )
        .optional()
        .map(|found| found.is_some())
        .map_err(|error| error.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn env_of(values: &[(&str, &Path)]) -> impl Fn(&str) -> Option<String> {
        let map: HashMap<String, String> = values
            .iter()
            .map(|(key, value)| (key.to_string(), value.to_string_lossy().into_owned()))
            .collect();
        move |key: &str| map.get(key).cloned()
    }

    fn touch_metadata(folder: &Path) -> PathBuf {
        std::fs::create_dir_all(folder).unwrap();
        let path = folder.join("metadata.db");
        std::fs::write(&path, b"").unwrap();
        path
    }

    #[test]
    fn metadata_path_prefers_simplefile_metadata_db() {
        let temp = tempfile::tempdir().unwrap();
        let explicit = temp.path().join("explicit").join("none.db");
        let app_data = temp.path().join("roaming");
        touch_metadata(&app_data.join("SumaFile"));

        let env = env_of(&[
            ("SIMPLEFILE_METADATA_DB", explicit.as_path()),
            ("APPDATA", app_data.as_path()),
        ]);

        // Taken even though the file does not exist, as the service does.
        assert_eq!(legacy_metadata_db_path(&env), Some(explicit.clone()));
        assert!(!explicit.exists(), "resolution must never create the file");
    }

    #[test]
    fn metadata_path_takes_the_first_existing_candidate_in_service_order() {
        let temp = tempfile::tempdir().unwrap();
        let roaming = temp.path().join("roaming");
        let local = temp.path().join("local");
        touch_metadata(&local.join("SumaFile"));
        let expected = touch_metadata(&roaming.join("SimpleFile"));
        // An empty SIMPLEFILE_METADATA_DB is ignored, and so is an app data
        // dir without a metadata.db.
        let empty_app_dir = temp.path().join("app-dir");
        std::fs::create_dir_all(&empty_app_dir).unwrap();

        let env = env_of(&[
            ("SIMPLEFILE_METADATA_DB", Path::new("")),
            ("SIMPLEFILE_APP_DATA_DIR", empty_app_dir.as_path()),
            ("APPDATA", roaming.as_path()),
            ("LOCALAPPDATA", local.as_path()),
        ]);

        assert_eq!(legacy_metadata_db_path(&env), Some(expected));
    }

    #[test]
    fn metadata_path_is_none_when_no_candidate_exists() {
        let temp = tempfile::tempdir().unwrap();
        let roaming = temp.path().join("roaming");
        let local = temp.path().join("local");
        std::fs::create_dir_all(roaming.join("SumaFile")).unwrap();

        let env = env_of(&[
            ("APPDATA", roaming.as_path()),
            ("LOCALAPPDATA", local.as_path()),
        ]);

        assert_eq!(legacy_metadata_db_path(&env), None);
        assert!(!local.exists(), "resolution must never create a folder");
        assert!(!roaming.join("SumaFile").join("metadata.db").exists());
        assert_eq!(legacy_metadata_db_path(&|_: &str| None), None);
    }
}
