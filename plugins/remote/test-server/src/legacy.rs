//! Writes 1.0.4 metadata databases for the backend tests, the C# harness and
//! the UI smoke. C# has no SQLite library, so they all come through here.

use crate::TestServerInfo;
use rusqlite::{params, Connection};
use std::path::Path;

/// One row of the 1.0.4 remote_profiles table.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LegacyProfile {
    pub id: String,
    pub name: String,
    pub protocol: String,
    pub host: String,
    pub port: u16,
    pub username: String,
    pub root_path: String,
    pub auth_kind: String,
    pub insecure_plain_ftp: bool,
    pub passive_mode: bool,
    pub credential_target: Option<String>,
    pub private_key_path: Option<String>,
    pub trusted_host_fingerprint: Option<String>,
    pub updated_at: String,
}

/// The `updated_at` every profile from [`TestServerInfo::legacy_profile`] carries.
pub const LEGACY_UPDATED_AT: &str = "2026-09-21T08:30:00.000000+00:00";

/// Creates `path` with the 1.0.4 schema: settings(key TEXT PRIMARY KEY, value TEXT NOT NULL) and remote_profiles with the DDL of
/// crates/simplefile-core/src/remote/profiles.rs:274-289 (copied, since Task 14 deletes it); inserts `profiles` and, when given, the transfer.policy row.
///
/// An existing database at `path` is opened and the tables are added when
/// missing, so a test can add the table to a database that lacked it.
pub fn write_legacy_metadata_db(
    path: &Path,
    profiles: &[LegacyProfile],
    transfer_policy: Option<&str>,
) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let mut connection = Connection::open(path).map_err(|error| error.to_string())?;
    let transaction = connection
        .transaction()
        .map_err(|error| error.to_string())?;
    transaction
        .execute(
            "CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )",
            [],
        )
        .map_err(|error| error.to_string())?;
    transaction
        .execute(
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
        )
        .map_err(|error| error.to_string())?;
    for profile in profiles {
        transaction
            .execute(
                "INSERT OR REPLACE INTO remote_profiles (
                    id, name, protocol, host, port, username, root_path, auth_kind,
                    insecure_plain_ftp, passive_mode, credential_target,
                    private_key_path, trusted_host_fingerprint, updated_at
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14)",
                params![
                    profile.id,
                    profile.name,
                    profile.protocol,
                    profile.host,
                    i64::from(profile.port),
                    profile.username,
                    profile.root_path,
                    profile.auth_kind,
                    i64::from(profile.insecure_plain_ftp),
                    i64::from(profile.passive_mode),
                    profile.credential_target,
                    profile.private_key_path,
                    profile.trusted_host_fingerprint,
                    profile.updated_at,
                ],
            )
            .map_err(|error| error.to_string())?;
    }
    if let Some(policy) = transfer_policy {
        transaction
            .execute(
                "INSERT OR REPLACE INTO settings (key, value) VALUES ('transfer.policy', ?1)",
                [policy],
            )
            .map_err(|error| error.to_string())?;
    }
    transaction.commit().map_err(|error| error.to_string())
}

impl TestServerInfo {
    /// An SFTP password profile for this server: 127.0.0.1, its port and user, root "/", its fingerprint trusted.
    pub fn legacy_profile(&self, id: &str, credential_target: &str) -> LegacyProfile {
        LegacyProfile {
            id: id.to_string(),
            name: "Legacy SFTP".to_string(),
            protocol: "sftp".to_string(),
            host: "127.0.0.1".to_string(),
            port: self.port,
            username: self.user.clone(),
            root_path: "/".to_string(),
            auth_kind: "password".to_string(),
            insecure_plain_ftp: false,
            passive_mode: true,
            credential_target: Some(credential_target.to_string()),
            private_key_path: None,
            trusted_host_fingerprint: Some(self.fingerprint.clone()),
            updated_at: LEGACY_UPDATED_AT.to_string(),
        }
    }
}
