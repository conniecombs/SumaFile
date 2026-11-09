//! The plugin's own profile store, `<data dir>\remote-profiles.db`. The
//! `remote_profiles` table keeps the 1.0.4 schema (copied from
//! `simplefile_core::remote::profiles`); `plugin_meta` holds the backend's
//! one-time markers.

use crate::models::{RemoteAuthKind, RemoteProfile, RemoteProfileInput, RemoteProtocol};
use chrono::Utc;
use rusqlite::{params, Connection, OptionalExtension};
use std::path::{Path, PathBuf};
use std::time::Duration;

pub const PROFILE_DB_FILE: &str = "remote-profiles.db";
pub const CREDENTIAL_TARGET_PREFIX: &str = "SumaFile.Remote.";

/// How long a connection waits for another connection's write lock, so two
/// backends sharing one data folder never fail on a busy database.
const BUSY_TIMEOUT: Duration = Duration::from_secs(5);

const SELECT_PROFILE_COLUMNS: &str =
    "SELECT id, name, protocol, host, port, username, root_path, auth_kind,
                insecure_plain_ftp, passive_mode, credential_target, private_key_path,
                trusted_host_fingerprint
         FROM remote_profiles";

#[derive(Debug, Clone)]
pub struct ProfileStore {
    path: PathBuf,
}

impl ProfileStore {
    /// The store at `<data_dir>\remote-profiles.db`. Nothing is touched until
    /// the first call, which creates the folder and the tables.
    pub fn open(data_dir: &Path) -> ProfileStore {
        ProfileStore {
            path: data_dir.join(PROFILE_DB_FILE),
        }
    }

    /// The database file this store reads and writes.
    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn list(&self) -> Result<Vec<RemoteProfile>, String> {
        let connection = self.connection()?;
        let mut statement = connection
            .prepare(&format!(
                "{SELECT_PROFILE_COLUMNS}
                 ORDER BY name COLLATE NOCASE, host COLLATE NOCASE, id"
            ))
            .map_err(|error| error.to_string())?;
        let rows = statement
            .query_map([], read_raw_profile)
            .map_err(|error| error.to_string())?;

        let mut profiles = Vec::new();
        for row in rows {
            profiles.push(row.map_err(|error| error.to_string())?.into_profile()?);
        }

        Ok(profiles)
    }

    pub fn save(&self, input: RemoteProfileInput) -> Result<RemoteProfile, String> {
        let profile = ProfileStore::prepare(input)?;
        let connection = self.connection()?;
        connection
            .execute(
                "INSERT INTO remote_profiles (
                    id, name, protocol, host, port, username, root_path, auth_kind,
                    insecure_plain_ftp, passive_mode, credential_target,
                    private_key_path, trusted_host_fingerprint, updated_at
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    protocol = excluded.protocol,
                    host = excluded.host,
                    port = excluded.port,
                    username = excluded.username,
                    root_path = excluded.root_path,
                    auth_kind = excluded.auth_kind,
                    insecure_plain_ftp = excluded.insecure_plain_ftp,
                    passive_mode = excluded.passive_mode,
                    credential_target = excluded.credential_target,
                    private_key_path = excluded.private_key_path,
                    trusted_host_fingerprint = excluded.trusted_host_fingerprint,
                    updated_at = excluded.updated_at",
                params![
                    profile.id,
                    profile.name,
                    profile.protocol.as_str(),
                    profile.host,
                    i64::from(profile.port),
                    profile.username,
                    profile.root_path,
                    profile.auth_kind.as_str(),
                    bool_to_i64(profile.insecure_plain_ftp),
                    bool_to_i64(profile.passive_mode),
                    profile.credential_target,
                    profile.private_key_path,
                    profile.trusted_host_fingerprint,
                    Utc::now().to_rfc3339(),
                ],
            )
            .map_err(|error| error.to_string())?;

        Ok(profile)
    }

    /// Deletes the profile and returns the deleted row, so the caller can
    /// delete its credential; `None` when no row had that id.
    pub fn delete(&self, id: &str) -> Result<Option<RemoteProfile>, String> {
        let id = id.trim();
        if id.is_empty() {
            return Err("remote profile id cannot be empty".to_string());
        }

        let connection = self.connection()?;
        let existing = connection
            .query_row(
                &format!("{SELECT_PROFILE_COLUMNS} WHERE id = ?1"),
                [id],
                read_raw_profile,
            )
            .optional()
            .map_err(|error| error.to_string())?;
        connection
            .execute("DELETE FROM remote_profiles WHERE id = ?1", [id])
            .map_err(|error| error.to_string())?;
        existing.map(RawProfile::into_profile).transpose()
    }

    /// Validates and normalizes a profile without saving it.
    pub fn prepare(input: RemoteProfileInput) -> Result<RemoteProfile, String> {
        normalize_profile_input(input)
    }

    pub fn meta(&self, key: &str) -> Result<Option<String>, String> {
        let connection = self.connection()?;
        connection
            .query_row(
                "SELECT value FROM plugin_meta WHERE key = ?1",
                [key],
                |row| row.get::<_, String>(0),
            )
            .optional()
            .map_err(|error| error.to_string())
    }

    pub fn set_meta(&self, key: &str, value: &str) -> Result<(), String> {
        let connection = self.connection()?;
        set_meta_on(&connection, key, value)
    }

    /// A connection with the tables ensured and the busy timeout set.
    pub(crate) fn connection(&self) -> Result<Connection, String> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
        }

        let connection = Connection::open(&self.path).map_err(|error| error.to_string())?;
        connection
            .busy_timeout(BUSY_TIMEOUT)
            .map_err(|error| error.to_string())?;
        connection
            .execute("PRAGMA foreign_keys = ON", [])
            .map_err(|error| error.to_string())?;
        init_schema(&connection).map_err(|error| error.to_string())?;
        Ok(connection)
    }
}

pub(crate) fn set_meta_on(connection: &Connection, key: &str, value: &str) -> Result<(), String> {
    connection
        .execute(
            "INSERT INTO plugin_meta (key, value) VALUES (?1, ?2)
             ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            params![key, value],
        )
        .map(|_| ())
        .map_err(|error| error.to_string())
}

struct RawProfile {
    id: String,
    name: String,
    protocol: String,
    host: String,
    port: i64,
    username: String,
    root_path: String,
    auth_kind: String,
    insecure_plain_ftp: i64,
    passive_mode: i64,
    credential_target: Option<String>,
    private_key_path: Option<String>,
    trusted_host_fingerprint: Option<String>,
}

fn read_raw_profile(row: &rusqlite::Row<'_>) -> rusqlite::Result<RawProfile> {
    Ok(RawProfile {
        id: row.get(0)?,
        name: row.get(1)?,
        protocol: row.get(2)?,
        host: row.get(3)?,
        port: row.get(4)?,
        username: row.get(5)?,
        root_path: row.get(6)?,
        auth_kind: row.get(7)?,
        insecure_plain_ftp: row.get(8)?,
        passive_mode: row.get(9)?,
        credential_target: row.get(10)?,
        private_key_path: row.get(11)?,
        trusted_host_fingerprint: row.get(12)?,
    })
}

impl RawProfile {
    fn into_profile(self) -> Result<RemoteProfile, String> {
        let port = self.port;
        Ok(RemoteProfile {
            id: self.id,
            name: self.name,
            protocol: RemoteProtocol::parse(&self.protocol)?,
            host: self.host,
            port: u16::try_from(port)
                .map_err(|_| format!("Invalid remote profile port: {port}"))?,
            username: self.username,
            root_path: self.root_path,
            auth_kind: RemoteAuthKind::parse(&self.auth_kind)?,
            insecure_plain_ftp: self.insecure_plain_ftp != 0,
            passive_mode: self.passive_mode != 0,
            credential_target: self.credential_target,
            private_key_path: self.private_key_path,
            trusted_host_fingerprint: self.trusted_host_fingerprint,
        })
    }
}

fn normalize_profile_input(input: RemoteProfileInput) -> Result<RemoteProfile, String> {
    if input.protocol == RemoteProtocol::Ftp && !input.insecure_plain_ftp {
        return Err("Confirm the unencrypted connection to use plain FTP".to_string());
    }
    if matches!(input.protocol, RemoteProtocol::Ftp | RemoteProtocol::Ftps)
        && !matches!(
            input.auth_kind,
            RemoteAuthKind::Password | RemoteAuthKind::Anonymous
        )
    {
        return Err(
            "FTP and FTPS profiles support password or anonymous authentication".to_string(),
        );
    }
    if input.auth_kind == RemoteAuthKind::PrivateKey
        && input
            .private_key_path
            .as_deref()
            .and_then(non_empty_str)
            .is_none()
    {
        return Err("SFTP private key path cannot be empty".to_string());
    }

    let id = match input.id {
        Some(value) if !value.trim().is_empty() => value.trim().to_string(),
        _ => generate_profile_id()?,
    };
    let name = required("remote profile name", input.name)?;
    let host = required("remote profile host", input.host)?;
    let username = match input.auth_kind {
        RemoteAuthKind::Anonymous => input.username.trim().to_string(),
        _ => required("remote profile username", input.username)?,
    };
    let root_path = normalize_root_path(&input.root_path);
    let credential_target = match input.credential_target.and_then(non_empty) {
        // Only targets in our own namespace: a caller must not be able to
        // overwrite or delete another application's stored credential.
        Some(target) if target.starts_with(CREDENTIAL_TARGET_PREFIX) => Some(target),
        Some(target) => {
            return Err(format!(
            "remote profile credential target must start with {CREDENTIAL_TARGET_PREFIX}: {target}"
        ))
        }
        None => Some(format!("{CREDENTIAL_TARGET_PREFIX}{id}")),
    };
    let private_key_path = input.private_key_path.and_then(non_empty);

    Ok(RemoteProfile {
        id,
        name,
        protocol: input.protocol,
        host,
        port: input.port,
        username,
        root_path,
        auth_kind: input.auth_kind,
        insecure_plain_ftp: input.insecure_plain_ftp,
        passive_mode: input.passive_mode,
        credential_target,
        private_key_path,
        trusted_host_fingerprint: input.trusted_host_fingerprint.and_then(non_empty),
    })
}

fn normalize_root_path(value: &str) -> String {
    let replaced = value.trim().replace('\\', "/");
    let trimmed = replaced.trim_matches('/');
    if trimmed.is_empty() {
        return "/".to_string();
    }

    format!("/{trimmed}")
}

fn required(label: &str, value: String) -> Result<String, String> {
    non_empty(value).ok_or_else(|| format!("{label} cannot be empty"))
}

fn non_empty(value: String) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

fn non_empty_str(value: &str) -> Option<&str> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed)
    }
}

fn init_schema(connection: &Connection) -> rusqlite::Result<()> {
    connection.execute(
        "CREATE TABLE IF NOT EXISTS remote_profiles (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            protocol TEXT NOT NULL,
            host TEXT NOT NULL,
            port INTEGER NOT NULL,
            username TEXT NOT NULL,
            root_path TEXT NOT NULL,
            auth_kind TEXT NOT NULL,
            insecure_plain_ftp INTEGER NOT NULL,
            passive_mode INTEGER NOT NULL,
            credential_target TEXT,
            private_key_path TEXT,
            trusted_host_fingerprint TEXT,
            updated_at TEXT NOT NULL
        )",
        [],
    )?;
    connection.execute(
        "CREATE TABLE IF NOT EXISTS plugin_meta (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )",
        [],
    )?;
    Ok(())
}

fn generate_profile_id() -> Result<String, String> {
    let mut bytes = [0u8; 16];
    getrandom::fill(&mut bytes).map_err(|error| error.to_string())?;
    Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
}

fn bool_to_i64(value: bool) -> i64 {
    if value {
        1
    } else {
        0
    }
}
