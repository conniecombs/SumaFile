use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use simplefile_core::remote::profiles::{delete_profile_at, list_profiles_at, save_profile_at};
use simplefile_core::remote::{RemoteAuthKind, RemoteProfileInput, RemoteProtocol};

fn temp_db(name: &str) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("time")
        .as_nanos();
    std::env::temp_dir().join(format!("sumafile-remote-profiles-{name}-{nanos}.db"))
}

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

fn cleanup(path: &Path) {
    let _ = std::fs::remove_file(path);
}

#[test]
fn remote_profiles_persist_without_secret_material() {
    let db = temp_db("persist");
    let saved = save_profile_at(&db, sftp_input()).expect("save profile");

    assert!(!saved.id.is_empty());
    assert_eq!(saved.root_path, "/var/www");
    assert_eq!(
        saved.credential_target.as_deref(),
        Some(format!("SumaFile.Remote.{}", saved.id).as_str())
    );

    let profiles = list_profiles_at(&db).expect("list profiles");
    assert_eq!(profiles.len(), 1);
    assert_eq!(profiles[0].id, saved.id);
    assert_eq!(profiles[0].name, "Production SFTP");
    assert_eq!(profiles[0].host, "files.example.com");
    assert_eq!(profiles[0].credential_target, saved.credential_target);

    cleanup(&db);
}

#[test]
fn remote_private_key_profiles_persist_key_path_metadata() {
    let db = temp_db("private-key");
    let mut input = sftp_input();
    input.auth_kind = RemoteAuthKind::PrivateKey;
    input.private_key_path = Some(r" C:\Users\raz00\.ssh\id_ed25519 ".to_string());

    let saved = save_profile_at(&db, input).expect("save private-key profile");

    assert_eq!(
        saved.private_key_path.as_deref(),
        Some(r"C:\Users\raz00\.ssh\id_ed25519")
    );
    let profiles = list_profiles_at(&db).expect("list profiles");
    assert_eq!(profiles[0].private_key_path, saved.private_key_path);

    cleanup(&db);
}

#[test]
fn remote_private_key_profiles_require_key_path() {
    let db = temp_db("private-key-missing");
    let mut input = sftp_input();
    input.auth_kind = RemoteAuthKind::PrivateKey;

    let error = save_profile_at(&db, input).expect_err("private key path should be required");

    assert!(
        error.contains("private key path"),
        "unexpected error: {error}"
    );

    cleanup(&db);
}

#[test]
fn remote_profiles_reject_plain_ftp_without_explicit_insecure_acknowledgement() {
    let db = temp_db("plain-ftp");
    let mut input = sftp_input();
    input.protocol = RemoteProtocol::Ftp;
    input.port = 21;
    input.insecure_plain_ftp = false;

    let error = save_profile_at(&db, input).expect_err("plain FTP should require acknowledgement");

    assert!(
        error.contains("Plain FTP requires insecure_plain_ftp"),
        "unexpected error: {error}"
    );

    cleanup(&db);
}

#[test]
fn remote_profiles_delete_by_id() {
    let db = temp_db("delete");
    let saved = save_profile_at(&db, sftp_input()).expect("save profile");

    delete_profile_at(&db, &saved.id).expect("delete profile");

    assert!(list_profiles_at(&db).expect("list after delete").is_empty());

    cleanup(&db);
}
