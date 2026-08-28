use rusqlite::{Connection, OptionalExtension};
use std::path::{Path, PathBuf};

const APP_IDENTIFIER: &str = "com.simplefile.desktop";
const PRODUCT_NAME: &str = "SumaFile";
const LEGACY_PRODUCT_NAME: &str = "SimpleFile";
const METADATA_DB_NAME: &str = "metadata.db";
const METADATA_DB_ENV: &str = "SIMPLEFILE_METADATA_DB";
const APP_DATA_DIR_ENV: &str = "SIMPLEFILE_APP_DATA_DIR";

pub fn get_db_setting(key: String) -> Result<Option<String>, String> {
    validate_key(&key)?;
    let path = metadata_db_path()?;
    get_db_setting_at(&path, &key)
}

pub fn set_db_setting(key: String, value: String) -> Result<(), String> {
    validate_key(&key)?;
    let path = metadata_db_path()?;
    set_db_setting_at(&path, &key, &value)
}

pub fn metadata_db_path() -> Result<PathBuf, String> {
    if let Some(path) = std::env::var_os(METADATA_DB_ENV).filter(|value| !value.is_empty()) {
        return Ok(PathBuf::from(path));
    }

    let candidates = app_data_dir_candidates();
    if candidates.is_empty() {
        return Err("Could not resolve an app data directory for settings".to_string());
    }

    if let Some(existing) = candidates
        .iter()
        .map(|candidate| candidate.join(METADATA_DB_NAME))
        .find(|candidate| candidate.exists())
    {
        return Ok(existing);
    }

    Ok(candidates[0].join(METADATA_DB_NAME))
}

pub fn app_data_dir() -> Result<PathBuf, String> {
    let candidates = app_data_dir_candidates();
    if candidates.is_empty() {
        return Err("Could not resolve an app data directory".to_string());
    }

    if let Some(existing) = candidates.iter().find(|candidate| candidate.exists()) {
        return Ok(existing.clone());
    }

    Ok(candidates[0].clone())
}

fn app_data_dir_candidates() -> Vec<PathBuf> {
    let mut candidates = Vec::new();

    if let Some(path) = std::env::var_os(APP_DATA_DIR_ENV).filter(|value| !value.is_empty()) {
        candidates.push(PathBuf::from(path));
    }

    #[cfg(windows)]
    {
        if let Some(app_data) = std::env::var_os("APPDATA").filter(|value| !value.is_empty()) {
            let root = PathBuf::from(app_data);
            candidates.push(root.join(APP_IDENTIFIER));
            candidates.push(root.join(PRODUCT_NAME));
            candidates.push(root.join(LEGACY_PRODUCT_NAME));
        }

        if let Some(local_app_data) =
            std::env::var_os("LOCALAPPDATA").filter(|value| !value.is_empty())
        {
            let root = PathBuf::from(local_app_data);
            candidates.push(root.join(PRODUCT_NAME));
            candidates.push(root.join(APP_IDENTIFIER));
            candidates.push(root.join(LEGACY_PRODUCT_NAME));
        }
    }

    #[cfg(not(windows))]
    {
        if let Some(xdg_config_home) =
            std::env::var_os("XDG_CONFIG_HOME").filter(|value| !value.is_empty())
        {
            candidates.push(PathBuf::from(xdg_config_home).join(APP_IDENTIFIER));
        }

        if let Some(home) = std::env::var_os("HOME").filter(|value| !value.is_empty()) {
            candidates.push(PathBuf::from(home).join(".config").join(APP_IDENTIFIER));
        }
    }

    dedupe_paths(candidates)
}

fn dedupe_paths(paths: Vec<PathBuf>) -> Vec<PathBuf> {
    let mut unique = Vec::new();
    for path in paths {
        if !unique.iter().any(|candidate| candidate == &path) {
            unique.push(path);
        }
    }

    unique
}

fn validate_key(key: &str) -> Result<(), String> {
    if key.trim().is_empty() {
        return Err("settings key cannot be empty".to_string());
    }

    Ok(())
}

pub fn open_metadata_db() -> Result<Connection, String> {
    let path = metadata_db_path()?;
    open_metadata_db_at(&path)
}

fn open_metadata_db_at(path: &Path) -> Result<Connection, String> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }

    let connection = Connection::open(path).map_err(|error| error.to_string())?;
    connection
        .execute("PRAGMA foreign_keys = ON", [])
        .map_err(|error| error.to_string())?;
    init_schema(&connection).map_err(|error| error.to_string())?;
    Ok(connection)
}

fn init_schema(connection: &Connection) -> rusqlite::Result<()> {
    connection.execute(
        "CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )",
        [],
    )?;
    connection.execute(
        "CREATE TABLE IF NOT EXISTS tags (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE,
            color TEXT NOT NULL
        )",
        [],
    )?;
    connection.execute(
        "CREATE TABLE IF NOT EXISTS file_tags (
            file_path TEXT NOT NULL,
            tag_id INTEGER NOT NULL,
            PRIMARY KEY (file_path, tag_id),
            FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
        )",
        [],
    )?;

    seed_default_tags(connection)?;

    Ok(())
}

fn seed_default_tags(connection: &Connection) -> rusqlite::Result<()> {
    let count: i64 = connection.query_row("SELECT COUNT(*) FROM tags", [], |row| row.get(0))?;
    if count > 0 {
        return Ok(());
    }

    for (name, color) in [
        ("Important", "#ff3b30"),
        ("Work", "#ff9500"),
        ("Personal", "#4cd964"),
        ("To Do", "#5ac8fa"),
        ("Later", "#007aff"),
    ] {
        connection.execute(
            "INSERT INTO tags (name, color) VALUES (?1, ?2)",
            [name, color],
        )?;
    }

    Ok(())
}

fn get_db_setting_at(path: &Path, key: &str) -> Result<Option<String>, String> {
    let connection = open_metadata_db_at(path)?;
    connection
        .query_row("SELECT value FROM settings WHERE key = ?1", [key], |row| {
            row.get(0)
        })
        .optional()
        .map_err(|error| error.to_string())
}

fn set_db_setting_at(path: &Path, key: &str, value: &str) -> Result<(), String> {
    let connection = open_metadata_db_at(path)?;
    connection
        .execute(
            "INSERT INTO settings (key, value) VALUES (?1, ?2)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            [key, value],
        )
        .map_err(|error| error.to_string())?;

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_db(name: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("time")
            .as_nanos();
        std::env::temp_dir().join(format!("simplefile-settings-{name}-{nanos}.db"))
    }

    #[test]
    fn settings_round_trip_uses_metadata_db_table() {
        let db = temp_db("round-trip");

        set_db_setting_at(&db, "workspace", r#"{"path":"C:\\Users\\test"}"#).expect("set");
        let value = get_db_setting_at(&db, "workspace").expect("get");

        assert_eq!(value.as_deref(), Some(r#"{"path":"C:\\Users\\test"}"#));
        assert_eq!(get_db_setting_at(&db, "missing").expect("missing"), None);

        let connection = Connection::open(&db).expect("open");
        let table_count: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'settings'",
                [],
                |row| row.get(0),
            )
            .expect("query table");
        assert_eq!(table_count, 1);

        let _ = std::fs::remove_file(db);
    }

    #[test]
    fn setting_keys_must_not_be_empty() {
        assert!(validate_key("workspace").is_ok());
        assert!(validate_key("  ").is_err());
    }
}
