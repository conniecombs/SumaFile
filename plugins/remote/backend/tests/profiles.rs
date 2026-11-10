use std::path::Path;

use sumafile_remote_backend::models::{RemoteAuthKind, RemoteProfileInput, RemoteProtocol};
use sumafile_remote_backend::profiles::{ProfileStore, PROFILE_DB_FILE};

fn sftp_input() -> RemoteProfileInput {
    RemoteProfileInput {
        id: None,
        name: "Production SFTP".to_string(),
        protocol: RemoteProtocol::Sftp,
        host: "files.example.com".to_string(),
        port: 22,
        username: "deploy".to_string(),
        root_path: "var/www".to_string(),
        auth_kind: RemoteAuthKind::Password,
        insecure_plain_ftp: false,
        passive_mode: true,
        credential_target: None,
        private_key_path: None,
        trusted_host_fingerprint: None,
    }
}

fn store_in(folder: &Path) -> ProfileStore {
    ProfileStore::open(&folder.join("plugins-data").join("com.sumafile.remote"))
}

#[test]
fn remote_profiles_persist_without_secret_material() {
    let temp = tempfile::tempdir().unwrap();
    let store = store_in(temp.path());
    let saved = store.save(sftp_input()).expect("save profile");

    assert!(!saved.id.is_empty());
    assert_eq!(saved.root_path, "/var/www");
    assert_eq!(
        saved.credential_target.as_deref(),
        Some(format!("SumaFile.Remote.{}", saved.id).as_str())
    );
    assert_eq!(
        store.path(),
        temp.path()
            .join("plugins-data")
            .join("com.sumafile.remote")
            .join(PROFILE_DB_FILE)
    );
    assert!(
        store.path().exists(),
        "the first call creates the folder and the database"
    );

    let profiles = store.list().expect("list profiles");
    assert_eq!(profiles.len(), 1);
    assert_eq!(profiles[0].id, saved.id);
    assert_eq!(profiles[0].name, "Production SFTP");
    assert_eq!(profiles[0].host, "files.example.com");
    assert_eq!(profiles[0].credential_target, saved.credential_target);
}

#[test]
fn remote_private_key_profiles_persist_key_path_metadata() {
    let temp = tempfile::tempdir().unwrap();
    let store = store_in(temp.path());
    let mut input = sftp_input();
    input.auth_kind = RemoteAuthKind::PrivateKey;
    input.private_key_path = Some(r" C:\Users\you\.ssh\id_ed25519 ".to_string());

    let saved = store.save(input).expect("save private-key profile");

    assert_eq!(
        saved.private_key_path.as_deref(),
        Some(r"C:\Users\you\.ssh\id_ed25519")
    );
    let profiles = store.list().expect("list profiles");
    assert_eq!(profiles[0].private_key_path, saved.private_key_path);
}

#[test]
fn remote_private_key_profiles_require_key_path() {
    let temp = tempfile::tempdir().unwrap();
    let store = store_in(temp.path());
    let mut input = sftp_input();
    input.auth_kind = RemoteAuthKind::PrivateKey;

    let error = store
        .save(input)
        .expect_err("private key path should be required");

    assert!(
        error.contains("private key path"),
        "unexpected error: {error}"
    );
}

#[test]
fn remote_profiles_reject_plain_ftp_without_explicit_insecure_acknowledgement() {
    let temp = tempfile::tempdir().unwrap();
    let store = store_in(temp.path());
    let mut input = sftp_input();
    input.protocol = RemoteProtocol::Ftp;
    input.port = 21;
    input.insecure_plain_ftp = false;

    let error = store
        .save(input)
        .expect_err("plain FTP should require acknowledgement");

    assert!(
        error.contains("Confirm the unencrypted connection to use plain FTP"),
        "unexpected error: {error}"
    );
}

#[test]
fn remote_profiles_delete_by_id() {
    let temp = tempfile::tempdir().unwrap();
    let store = store_in(temp.path());
    let saved = store.save(sftp_input()).expect("save profile");

    let deleted = store.delete(&saved.id).expect("delete profile");

    assert_eq!(deleted, Some(saved.clone()), "the deleted row comes back");
    assert!(store.list().expect("list after delete").is_empty());
    assert_eq!(store.delete(&saved.id).expect("delete again"), None);
    assert!(store.delete("  ").is_err());
}

#[test]
fn credential_targets_outside_the_app_namespace_are_rejected() {
    let input = |target: Option<&str>| RemoteProfileInput {
        id: None,
        name: "box".to_string(),
        protocol: RemoteProtocol::Sftp,
        host: "example.test".to_string(),
        port: 22,
        username: "user".to_string(),
        root_path: "/".to_string(),
        auth_kind: RemoteAuthKind::Password,
        insecure_plain_ftp: false,
        passive_mode: true,
        credential_target: target.map(str::to_string),
        private_key_path: None,
        trusted_host_fingerprint: None,
    };

    let error = ProfileStore::prepare(input(Some("git:https://github.com"))).unwrap_err();
    assert!(error.contains("SumaFile.Remote."), "{error}");

    let own = ProfileStore::prepare(input(Some("SumaFile.Remote.custom"))).unwrap();
    assert_eq!(
        own.credential_target.as_deref(),
        Some("SumaFile.Remote.custom")
    );

    let generated = ProfileStore::prepare(input(None)).unwrap();
    assert!(generated
        .credential_target
        .as_deref()
        .is_some_and(|target| target.starts_with("SumaFile.Remote.")));
}
