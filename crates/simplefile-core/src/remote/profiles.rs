use super::{RemoteAuthKind, RemoteProfile, RemoteProfileInput, RemoteProtocol};
use chrono::Utc;
use rusqlite::{params, Connection};
use std::path::Path;

const CREDENTIAL_TARGET_PREFIX: &str = "SumaFile.Remote.";

pub fn list_profiles() -> Result<Vec<RemoteProfile>, String> {
    let path = crate::settings_store::metadata_db_path()?;
    list_profiles_at(&path)
}

pub fn save_profile(input: RemoteProfileInput) -> Result<RemoteProfile, String> {
    let path = crate::settings_store::metadata_db_path()?;
    save_profile_at(&path, input)
}

pub fn delete_profile(id: &str) -> Result<(), String> {
    let path = crate::settings_store::metadata_db_path()?;
    delete_profile_at(&path, id)
}

pub fn validate_profile_input(input: &RemoteProfileInput) -> Result<(), String> {
    normalize_profile_input(input.clone()).map(|_| ())
}

pub fn list_profiles_at(path: &Path) -> Result<Vec<RemoteProfile>, String> {
    let connection = open_remote_db_at(path)?;
    let mut statement = connection
        .prepare(
            "SELECT id, name, protocol, host, port, username, root_path, auth_kind,
                    insecure_plain_ftp, passive_mode, credential_target, private_key_path,
                    trusted_host_fingerprint
             FROM remote_profiles
             ORDER BY name COLLATE NOCASE, host COLLATE NOCASE, id",
        )
        .map_err(|error| error.to_string())?;

    let rows = statement
        .query_map([], |row| {
            let protocol: String = row.get(2)?;
            let auth_kind: String = row.get(7)?;
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, String>(1)?,
                protocol,
                row.get::<_, String>(3)?,
                row.get::<_, i64>(4)?,
                row.get::<_, String>(5)?,
                row.get::<_, String>(6)?,
                auth_kind,
                row.get::<_, i64>(8)?,
                row.get::<_, i64>(9)?,
                row.get::<_, Option<String>>(10)?,
                row.get::<_, Option<String>>(11)?,
                row.get::<_, Option<String>>(12)?,
            ))
        })
        .map_err(|error| error.to_string())?;

    let mut profiles = Vec::new();
    for row in rows {
        let (
            id,
            name,
            protocol,
            host,
            port,
            username,
            root_path,
            auth_kind,
            insecure_plain_ftp,
            passive_mode,
            credential_target,
            private_key_path,
            trusted_host_fingerprint,
        ) = row.map_err(|error| error.to_string())?;
        profiles.push(RemoteProfile {
            id,
            name,
            protocol: RemoteProtocol::parse(&protocol)?,
            host,
            port: u16::try_from(port)
                .map_err(|_| format!("Invalid remote profile port: {port}"))?,
            username,
            root_path,
            auth_kind: RemoteAuthKind::parse(&auth_kind)?,
            insecure_plain_ftp: insecure_plain_ftp != 0,
            passive_mode: passive_mode != 0,
            credential_target,
            private_key_path,
            trusted_host_fingerprint,
        });
    }

    Ok(profiles)
}

pub fn save_profile_at(path: &Path, input: RemoteProfileInput) -> Result<RemoteProfile, String> {
    let profile = normalize_profile_input(input)?;
    let connection = open_remote_db_at(path)?;
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

pub fn delete_profile_at(path: &Path, id: &str) -> Result<(), String> {
    let id = id.trim();
    if id.is_empty() {
        return Err("remote profile id cannot be empty".to_string());
    }

    let connection = open_remote_db_at(path)?;
    connection
        .execute("DELETE FROM remote_profiles WHERE id = ?1", [id])
        .map_err(|error| error.to_string())?;
    Ok(())
}

fn normalize_profile_input(input: RemoteProfileInput) -> Result<RemoteProfile, String> {
    if input.protocol == RemoteProtocol::Ftp && !input.insecure_plain_ftp {
        return Err("Plain FTP requires insecure_plain_ftp acknowledgement".to_string());
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
    let credential_target = input
        .credential_target
        .and_then(non_empty)
        .or_else(|| Some(format!("{CREDENTIAL_TARGET_PREFIX}{id}")));
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

fn open_remote_db_at(path: &Path) -> Result<Connection, String> {
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
    ensure_column(connection, "private_key_path", "TEXT")?;
    Ok(())
}

fn ensure_column(connection: &Connection, name: &str, definition: &str) -> rusqlite::Result<()> {
    let mut statement = connection.prepare("PRAGMA table_info(remote_profiles)")?;
    let columns = statement.query_map([], |row| row.get::<_, String>(1))?;
    for column in columns {
        if column? == name {
            return Ok(());
        }
    }

    connection.execute(
        &format!("ALTER TABLE remote_profiles ADD COLUMN {name} {definition}"),
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
