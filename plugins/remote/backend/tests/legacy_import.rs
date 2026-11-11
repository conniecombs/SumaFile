//! The one-time, read-only import of the 1.0.4 `remote_profiles` rows. The
//! legacy databases come from `sumafile_remote_test_server::write_legacy_metadata_db`.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Barrier};

use sha2::{Digest, Sha256};
use sumafile_remote_backend::legacy::{
    import_legacy_profiles, legacy_marker_key, legacy_transfer_policy, LegacyImport,
    LEGACY_PROFILES_IMPORTED,
};
use sumafile_remote_backend::models::{
    RemoteAuthKind, RemoteProfile, RemoteProfileInput, RemoteProtocol,
};
use sumafile_remote_backend::profiles::ProfileStore;
use sumafile_remote_test_server::{write_legacy_metadata_db, LegacyProfile};

fn sftp_row() -> LegacyProfile {
    LegacyProfile {
        id: "4f1c2a".to_string(),
        name: "Build box".to_string(),
        protocol: "sftp".to_string(),
        host: "build.example.test".to_string(),
        port: 2222,
        username: "deploy".to_string(),
        root_path: "/srv/builds".to_string(),
        auth_kind: "password".to_string(),
        insecure_plain_ftp: false,
        passive_mode: true,
        credential_target: Some("SumaFile.Remote.4f1c2a".to_string()),
        private_key_path: None,
        trusted_host_fingerprint: Some("SHA256:abcDEF0123456789+/xyz".to_string()),
        updated_at: "2026-08-01T10:11:12.131415+00:00".to_string(),
    }
}

fn ftps_row() -> LegacyProfile {
    LegacyProfile {
        id: "9b8e7d".to_string(),
        name: "archive [old] & caf\u{e9}".to_string(),
        protocol: "ftps".to_string(),
        host: "ftp.example.test".to_string(),
        port: 990,
        username: "archivist".to_string(),
        root_path: "/".to_string(),
        auth_kind: "anonymous".to_string(),
        insecure_plain_ftp: false,
        passive_mode: false,
        credential_target: None,
        private_key_path: Some(r"C:\keys\unused".to_string()),
        trusted_host_fingerprint: None,
        updated_at: "2025-12-31T23:59:59+00:00".to_string(),
    }
}

fn data_dir(temp: &Path) -> PathBuf {
    temp.join("plugins-data").join("com.sumafile.remote")
}

fn assert_profile_matches(profile: &RemoteProfile, row: &LegacyProfile) {
    assert_eq!(profile.id, row.id);
    assert_eq!(profile.name, row.name);
    assert_eq!(profile.protocol.as_str(), row.protocol);
    assert_eq!(profile.host, row.host);
    assert_eq!(profile.port, row.port);
    assert_eq!(profile.username, row.username);
    assert_eq!(profile.root_path, row.root_path);
    let auth_kind = serde_json::to_value(&profile.auth_kind).unwrap();
    assert_eq!(auth_kind, serde_json::Value::String(row.auth_kind.clone()));
    assert_eq!(profile.insecure_plain_ftp, row.insecure_plain_ftp);
    assert_eq!(profile.passive_mode, row.passive_mode);
    assert_eq!(profile.credential_target, row.credential_target);
    assert_eq!(profile.private_key_path, row.private_key_path);
    assert_eq!(
        profile.trusted_host_fingerprint,
        row.trusted_host_fingerprint
    );
}

fn updated_at(store: &ProfileStore, id: &str) -> String {
    let connection = rusqlite::Connection::open(store.path()).unwrap();
    connection
        .query_row(
            "SELECT updated_at FROM remote_profiles WHERE id = ?1",
            [id],
            |row| row.get(0),
        )
        .unwrap()
}

fn sha256_of(path: &Path) -> Vec<u8> {
    Sha256::digest(std::fs::read(path).unwrap()).to_vec()
}

#[test]
fn import_copies_every_legacy_profile_once() {
    let temp = tempfile::tempdir().unwrap();
    let legacy = temp.path().join("legacy").join("metadata.db");
    let rows = [sftp_row(), ftps_row()];
    write_legacy_metadata_db(&legacy, &rows, None).unwrap();
    let store = ProfileStore::open(&data_dir(temp.path()));

    assert_eq!(
        import_legacy_profiles(&store, &legacy),
        Ok(LegacyImport::Imported(2))
    );
    assert_eq!(
        import_legacy_profiles(&store, &legacy),
        Ok(LegacyImport::AlreadyImported)
    );

    let profiles = store.list().unwrap();
    assert_eq!(profiles.len(), 2);
    for row in &rows {
        let profile = profiles
            .iter()
            .find(|profile| profile.id == row.id)
            .unwrap_or_else(|| panic!("{} was not imported", row.id));
        assert_profile_matches(profile, row);
        assert_eq!(updated_at(&store, &row.id), row.updated_at);
    }

    let marker = store
        .meta(&legacy_marker_key(LEGACY_PROFILES_IMPORTED, &legacy))
        .unwrap()
        .expect("the marker is written");
    assert!(
        chrono::DateTime::parse_from_rfc3339(&marker).is_ok(),
        "the marker holds an RFC 3339 time: {marker}"
    );
}

#[test]
fn import_never_writes_the_legacy_database() {
    let temp = tempfile::tempdir().unwrap();
    let legacy_dir = temp.path().join("legacy");
    let legacy = legacy_dir.join("metadata.db");
    write_legacy_metadata_db(
        &legacy,
        &[sftp_row(), ftps_row()],
        Some("{\"bandwidthMib\":5}"),
    )
    .unwrap();
    let hash_before = sha256_of(&legacy);
    let modified_before = std::fs::metadata(&legacy).unwrap().modified().unwrap();
    let files_before = std::fs::read_dir(&legacy_dir).unwrap().count();
    let store = ProfileStore::open(&data_dir(temp.path()));

    assert_eq!(
        import_legacy_profiles(&store, &legacy),
        Ok(LegacyImport::Imported(2))
    );
    assert_eq!(
        legacy_transfer_policy(&legacy).unwrap().as_deref(),
        Some("{\"bandwidthMib\":5}")
    );
    assert_eq!(
        import_legacy_profiles(&store, &legacy),
        Ok(LegacyImport::AlreadyImported)
    );

    assert_eq!(sha256_of(&legacy), hash_before);
    assert_eq!(
        std::fs::metadata(&legacy).unwrap().modified().unwrap(),
        modified_before
    );
    assert_eq!(
        std::fs::read_dir(&legacy_dir).unwrap().count(),
        files_before,
        "no journal or other file is left beside the legacy database"
    );
}

#[test]
fn import_skips_ids_the_store_already_has() {
    let temp = tempfile::tempdir().unwrap();
    let legacy = temp.path().join("legacy").join("metadata.db");
    write_legacy_metadata_db(&legacy, &[sftp_row(), ftps_row()], None).unwrap();
    let store = ProfileStore::open(&data_dir(temp.path()));
    let edited = store
        .save(RemoteProfileInput {
            id: Some(sftp_row().id),
            name: "Edited in the plugin".to_string(),
            protocol: RemoteProtocol::Sftp,
            host: "new-host.example.test".to_string(),
            port: 22,
            username: "deploy".to_string(),
            root_path: "/".to_string(),
            auth_kind: RemoteAuthKind::Password,
            insecure_plain_ftp: false,
            passive_mode: true,
            credential_target: None,
            private_key_path: None,
            trusted_host_fingerprint: None,
        })
        .unwrap();

    assert_eq!(
        import_legacy_profiles(&store, &legacy),
        Ok(LegacyImport::Imported(1))
    );

    let profiles = store.list().unwrap();
    assert_eq!(profiles.len(), 2);
    let kept = profiles
        .iter()
        .find(|profile| profile.id == edited.id)
        .unwrap();
    assert_eq!(kept, &edited, "the store's own row wins");
    let imported = profiles
        .iter()
        .find(|profile| profile.id == ftps_row().id)
        .unwrap();
    assert_profile_matches(imported, &ftps_row());
}

#[test]
fn import_of_a_database_without_the_table_imports_nothing() {
    let temp = tempfile::tempdir().unwrap();
    let store = ProfileStore::open(&data_dir(temp.path()));
    let missing = temp.path().join("missing").join("metadata.db");
    assert_eq!(
        import_legacy_profiles(&store, &missing),
        Ok(LegacyImport::NoLegacyTable)
    );
    assert!(
        !missing.exists(),
        "a missing legacy database is never created"
    );
    assert!(!missing.parent().unwrap().exists());

    let legacy = temp.path().join("legacy").join("metadata.db");
    std::fs::create_dir_all(legacy.parent().unwrap()).unwrap();
    {
        let connection = rusqlite::Connection::open(&legacy).unwrap();
        connection
            .execute(
                "CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)",
                [],
            )
            .unwrap();
    }

    assert_eq!(
        import_legacy_profiles(&store, &legacy),
        Ok(LegacyImport::NoLegacyTable)
    );
    assert_eq!(
        store
            .meta(&legacy_marker_key(LEGACY_PROFILES_IMPORTED, &legacy))
            .unwrap(),
        None,
        "no marker without a table"
    );
    assert_eq!(
        store
            .meta(&legacy_marker_key(LEGACY_PROFILES_IMPORTED, &missing))
            .unwrap(),
        None
    );

    write_legacy_metadata_db(&legacy, &[sftp_row()], None).unwrap();
    assert_eq!(
        import_legacy_profiles(&store, &legacy),
        Ok(LegacyImport::Imported(1))
    );
    assert_eq!(store.list().unwrap().len(), 1);
}

#[test]
fn import_marker_is_scoped_to_its_database() {
    let temp = tempfile::tempdir().unwrap();
    let store = ProfileStore::open(&data_dir(temp.path()));
    let a = temp.path().join("a").join("metadata.db");
    let b = temp.path().join("b").join("metadata.db");
    write_legacy_metadata_db(&a, &[], None).unwrap();
    write_legacy_metadata_db(&b, &[sftp_row()], None).unwrap();

    assert_eq!(
        import_legacy_profiles(&store, &a),
        Ok(LegacyImport::Imported(0))
    );
    assert_eq!(
        import_legacy_profiles(&store, &b),
        Ok(LegacyImport::Imported(1))
    );
    assert_eq!(
        import_legacy_profiles(&store, &a),
        Ok(LegacyImport::AlreadyImported)
    );
    assert_eq!(
        import_legacy_profiles(&store, &b),
        Ok(LegacyImport::AlreadyImported)
    );
    assert_ne!(
        legacy_marker_key(LEGACY_PROFILES_IMPORTED, &a),
        legacy_marker_key(LEGACY_PROFILES_IMPORTED, &b)
    );
    assert_eq!(store.list().unwrap().len(), 1);
}

#[test]
fn legacy_marker_key_is_the_absolute_lowercased_path() {
    let temp = tempfile::tempdir().unwrap();
    let legacy = temp.path().join("Legacy").join("Metadata.DB");
    let expected = format!(
        "{LEGACY_PROFILES_IMPORTED}:{}",
        std::path::absolute(&legacy)
            .unwrap()
            .to_string_lossy()
            .to_lowercase()
    );

    assert_eq!(
        legacy_marker_key(LEGACY_PROFILES_IMPORTED, &legacy),
        expected
    );
}

#[test]
fn concurrent_imports_insert_each_profile_once() {
    let temp = tempfile::tempdir().unwrap();
    let legacy = temp.path().join("legacy").join("metadata.db");
    write_legacy_metadata_db(&legacy, &[sftp_row(), ftps_row()], None).unwrap();
    let data = data_dir(temp.path());
    let barrier = Arc::new(Barrier::new(2));

    let threads: Vec<_> = (0..2)
        .map(|_| {
            let barrier = barrier.clone();
            let data = data.clone();
            let legacy = legacy.clone();
            std::thread::spawn(move || {
                let store = ProfileStore::open(&data);
                barrier.wait();
                import_legacy_profiles(&store, &legacy)
            })
        })
        .collect();
    let results: Vec<_> = threads
        .into_iter()
        .map(|thread| thread.join().unwrap().expect("both imports succeed"))
        .collect();

    let imported: usize = results
        .iter()
        .map(|result| match result {
            LegacyImport::Imported(count) => *count,
            _ => 0,
        })
        .sum();
    assert_eq!(imported, 2, "{results:?}");
    let profiles = ProfileStore::open(&data).list().unwrap();
    assert_eq!(profiles.len(), 2);
    assert!(profiles.iter().any(|profile| profile.id == sftp_row().id));
    assert!(profiles.iter().any(|profile| profile.id == ftps_row().id));
}

#[test]
fn legacy_transfer_policy_reads_the_settings_row() {
    let temp = tempfile::tempdir().unwrap();
    let with_policy = temp.path().join("with").join("metadata.db");
    let policy = r#"{"bandwidthMib":5,"lowLevelRetries":4}"#;
    write_legacy_metadata_db(&with_policy, &[], Some(policy)).unwrap();
    let without_policy = temp.path().join("without").join("metadata.db");
    write_legacy_metadata_db(&without_policy, &[], None).unwrap();
    let missing = temp.path().join("missing").join("metadata.db");

    assert_eq!(
        legacy_transfer_policy(&with_policy).unwrap().as_deref(),
        Some(policy)
    );
    assert_eq!(legacy_transfer_policy(&without_policy).unwrap(), None);
    assert_eq!(legacy_transfer_policy(&missing).unwrap(), None);
    assert!(!missing.exists());
}
