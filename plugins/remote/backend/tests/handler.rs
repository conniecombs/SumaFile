//! `RemoteHandler` through `PluginHandler::handle`: profiles, secrets,
//! sessions, file operations and the one-time legacy setup. The harness is
//! `RemoteHandler::with_parts` with a memory secret store, `HostClient::fake`
//! and the in-process SFTP test server.

mod common;

use std::sync::Arc;
use std::time::{Duration, Instant};

use common::{Harness, HarnessOptions};
use serde_json::{json, Value};
use sumafile_remote_backend::models::RemoteProfile;
use sumafile_remote_backend::provider::{RemoteProvider, RemoteProviderFactory};
use sumafile_remote_backend::secrets::{RemoteSecret, RemoteSecretStore};
use sumafile_remote_test_server::{write_legacy_metadata_db, LegacyProfile};

const PROFILES_CHANGED: &str = "remote.profiles-changed";

fn example_profile(auth_kind: &str) -> Value {
    json!({
        "id": null,
        "name": "Production SFTP",
        "protocol": "sftp",
        "host": "files.example.com",
        "port": 22,
        "username": "deploy",
        "root_path": "var/www",
        "auth_kind": auth_kind,
        "insecure_plain_ftp": false,
        "passive_mode": true,
        "credential_target": null,
        "private_key_path": if auth_kind == "private-key" { json!("C:\\keys\\id_ed25519") } else { Value::Null },
        "trusted_host_fingerprint": null
    })
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn profiles_save_and_list_never_return_secrets() {
    let harness = Harness::start().await;

    let saved = harness
        .ok(
            "profiles.save",
            json!({ "profile": example_profile("password"), "secret": "not-returned" }),
        )
        .await;
    assert_eq!(saved["name"], "Production SFTP");
    assert_eq!(saved["root_path"], "/var/www");
    assert!(saved["id"].as_str().is_some_and(|value| !value.is_empty()));
    assert!(saved.get("secret").is_none());
    assert!(!saved.to_string().contains("not-returned"));

    let profiles = harness.ok("profiles.list", Value::Null).await;
    let profiles = profiles.as_array().expect("profiles array");
    assert_eq!(profiles.len(), 1);
    assert_eq!(profiles[0]["credential_target"], saved["credential_target"]);
    assert!(profiles[0].get("secret").is_none());
    assert!(!profiles[0].to_string().contains("not-returned"));
    assert_eq!(harness.event_names(), vec![PROFILES_CHANGED.to_string()]);
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn saved_secret_is_used_on_connect_and_deleted_with_the_profile() {
    let harness = Harness::start().await;

    let saved = harness
        .ok(
            "profiles.save",
            json!({ "profile": harness.profile(), "secret": harness.info.password }),
        )
        .await;
    let profile_id = saved["id"].as_str().unwrap().to_string();
    let target = saved["credential_target"].as_str().unwrap().to_string();
    assert_eq!(
        harness.secrets.read_secret(&target).unwrap(),
        Some(RemoteSecret::Password(harness.info.password.clone()))
    );

    // No secret in params: the saved one is read from the store, and the
    // test server only accepts the right password.
    let session = harness
        .ok("session.connect", json!({ "profileId": profile_id }))
        .await;
    assert_eq!(session["profile_id"], profile_id.as_str());
    harness
        .ok(
            "session.disconnect",
            json!({ "remoteSessionId": session["session_id"] }),
        )
        .await;

    let deleted = harness
        .ok("profiles.delete", json!({ "profileId": profile_id }))
        .await;
    assert_eq!(deleted, Value::Null);
    assert_eq!(harness.secrets.read_secret(&target).unwrap(), None);
    assert_eq!(harness.ok("profiles.list", Value::Null).await, json!([]));
    assert_eq!(
        harness.event_names(),
        vec![PROFILES_CHANGED.to_string(), PROFILES_CHANGED.to_string()]
    );
    harness.stop().await;
}

struct FakeProvider;

impl RemoteProvider for FakeProvider {
    fn list_directory(
        &mut self,
        path: &str,
    ) -> Result<sumafile_remote_backend::models::DirectoryListing, String> {
        Ok(sumafile_remote_backend::models::DirectoryListing {
            path: path.to_string(),
            parent: None,
            entries: Vec::new(),
            is_network: true,
        })
    }
    fn create_directory(
        &mut self,
        _: &str,
        _: &str,
    ) -> Result<sumafile_remote_backend::models::FileEntry, String> {
        Err("not supported".into())
    }
    fn rename_entry(
        &mut self,
        _: &str,
        _: &str,
    ) -> Result<sumafile_remote_backend::models::FileEntry, String> {
        Err("not supported".into())
    }
    fn delete_entries(&mut self, _: &[String]) -> Result<Vec<String>, String> {
        Err("not supported".into())
    }
    fn download(
        &mut self,
        _: &str,
        _: &mut dyn std::io::Write,
    ) -> Result<u64, sumafile_remote_backend::provider::RemoteTransferError> {
        Err("not supported".into())
    }
    fn upload(
        &mut self,
        _: &str,
        _: &mut dyn std::io::Read,
    ) -> Result<u64, sumafile_remote_backend::provider::RemoteTransferError> {
        Err("not supported".into())
    }
}

struct PrivateKeySecretCheckingFactory;

impl RemoteProviderFactory for PrivateKeySecretCheckingFactory {
    fn connect(
        &self,
        _profile: &RemoteProfile,
        secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        match secret {
            Some(RemoteSecret::PrivateKeyPassphrase(value)) if value == "key-passphrase" => {
                Ok(Box::new(FakeProvider))
            }
            other => Err(format!("unexpected private key secret: {other:?}")),
        }
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn private_key_passphrase_is_stored_under_the_profile_target() {
    let harness = Harness::with(HarnessOptions {
        factory: Some(Arc::new(PrivateKeySecretCheckingFactory)),
        ..HarnessOptions::default()
    })
    .await;

    let saved = harness
        .ok(
            "profiles.save",
            json!({ "profile": example_profile("private-key"), "secret": "key-passphrase" }),
        )
        .await;
    let target = saved["credential_target"].as_str().unwrap().to_string();
    assert_eq!(
        harness.secrets.read_secret(&target).unwrap(),
        Some(RemoteSecret::PrivateKeyPassphrase(
            "key-passphrase".to_string()
        ))
    );

    harness
        .ok("session.connect", json!({ "profileId": saved["id"] }))
        .await;
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn test_profile_reports_capabilities_without_saving() {
    let harness = Harness::start().await;

    let result = harness
        .ok(
            "profiles.test",
            json!({ "profile": harness.profile(), "secret": harness.info.password }),
        )
        .await;
    assert_eq!(result["ok"], true, "{result}");
    assert!(result["message"]
        .as_str()
        .unwrap()
        .contains("Connection succeeded"));
    assert_eq!(
        result["capabilities"],
        json!(["browse", "mutate", "transfer"])
    );

    let wrong = harness
        .ok(
            "profiles.test",
            json!({ "profile": harness.profile(), "secret": "wrong password" }),
        )
        .await;
    assert_eq!(wrong["ok"], false, "{wrong}");
    assert_eq!(wrong["capabilities"], json!([]));

    assert_eq!(harness.ok("profiles.list", Value::Null).await, json!([]));
    assert!(harness.host.fake_events().is_empty());
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn connect_list_and_disconnect_through_the_handler() {
    let harness = Harness::start().await;
    std::fs::create_dir(harness.remote.path().join("release")).unwrap();
    std::fs::write(harness.remote.path().join("hello.txt"), b"hello").unwrap();

    let session_id = harness.connect().await;
    assert!(session_id.starts_with("remote-session-"), "{session_id}");

    let listing = harness
        .ok(
            "fs.list",
            json!({ "remoteSessionId": session_id, "path": "/" }),
        )
        .await;
    assert_eq!(listing["path"], "/");
    assert_eq!(listing["is_network"], true);
    let mut names: Vec<&str> = listing["entries"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| entry["name"].as_str().unwrap())
        .collect();
    names.sort();
    assert_eq!(names, vec!["hello.txt", "release"]);

    let disconnected = harness
        .ok(
            "session.disconnect",
            json!({ "remoteSessionId": session_id }),
        )
        .await;
    assert_eq!(disconnected, Value::Null);

    let error = harness
        .err(
            "fs.list",
            json!({ "remoteSessionId": session_id, "path": "/" }),
        )
        .await;
    assert_eq!(error, format!("remote session not found: {session_id}"));
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn create_rename_and_delete_through_the_handler() {
    let harness = Harness::start().await;
    let session_id = harness.connect().await;

    let created = harness
        .ok(
            "fs.create_directory",
            json!({ "remoteSessionId": session_id, "path": "/", "name": "incoming" }),
        )
        .await;
    assert_eq!(created["name"], "incoming");
    assert_eq!(created["path"], "/incoming");
    assert_eq!(created["is_dir"], true);
    assert!(harness.remote.path().join("incoming").is_dir());

    let renamed = harness
        .ok(
            "fs.rename",
            json!({ "remoteSessionId": session_id, "path": "/incoming", "newName": "archive" }),
        )
        .await;
    assert_eq!(renamed["name"], "archive");
    assert_eq!(renamed["path"], "/archive");
    assert!(harness.remote.path().join("archive").is_dir());

    let deleted = harness
        .ok(
            "fs.delete",
            json!({ "remoteSessionId": session_id, "paths": ["/archive"] }),
        )
        .await;
    assert_eq!(deleted, json!(["/archive"]));
    assert!(common::names(harness.remote.path()).is_empty());
    harness.stop().await;
}

struct StallingFactory;

impl RemoteProviderFactory for StallingFactory {
    fn connect(
        &self,
        _: &RemoteProfile,
        _: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        std::thread::sleep(Duration::from_millis(1500));
        Err("host unreachable".to_string())
    }
}

/// A remote handshake that stalls must not stall the backend: other calls
/// are answered while the connect attempt is in flight.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn slow_connect_does_not_block_other_requests() {
    let harness = Arc::new(
        Harness::with(HarnessOptions {
            factory: Some(Arc::new(StallingFactory)),
            ..HarnessOptions::default()
        })
        .await,
    );
    let mut stalling = harness.profile();
    stalling["host"] = json!("203.0.113.1");
    // The first store call opens the database and runs the legacy import, which a cold Windows runner can take over
    // a second to do (CI run 42742758345). Do it first, so the timed call below measures only whether the stalled
    // connect blocks it.
    assert_eq!(harness.ok("profiles.list", Value::Null).await, json!([]));

    let slow = {
        let harness = harness.clone();
        tokio::spawn(async move {
            harness
                .ok("profiles.test", json!({ "profile": stalling }))
                .await
        })
    };
    tokio::time::sleep(Duration::from_millis(100)).await;
    let started = Instant::now();
    assert_eq!(harness.ok("profiles.list", Value::Null).await, json!([]));
    let latency = started.elapsed();
    assert!(latency < Duration::from_secs(1), "{latency:?}");
    assert!(!slow.is_finished(), "the stalled connect is still running");

    let result = tokio::time::timeout(Duration::from_secs(5), slow)
        .await
        .expect("profiles.test still replies after the stall")
        .unwrap();
    assert_eq!(result["ok"], false);
    assert_eq!(result["message"], "host unreachable");
    Arc::try_unwrap(harness)
        .ok()
        .expect("one harness")
        .stop()
        .await;
}

fn legacy_row(info: &sumafile_remote_test_server::TestServerInfo, id: &str) -> LegacyProfile {
    info.legacy_profile(id, &format!("SumaFile.Remote.{id}"))
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn first_call_imports_legacy_profiles_and_announces_them() {
    let legacy_dir = tempfile::tempdir().unwrap();
    let legacy = legacy_dir.path().join("metadata.db");
    let probe = Harness::start().await;
    write_legacy_metadata_db(&legacy, &[legacy_row(&probe.info, "legacy-sftp")], None).unwrap();
    probe.stop().await;
    let harness = Harness::with(HarnessOptions {
        legacy_db: Some(legacy.clone()),
        ..HarnessOptions::default()
    })
    .await;

    let profiles = harness.ok("profiles.list", Value::Null).await;
    let profiles = profiles.as_array().unwrap();
    assert_eq!(profiles.len(), 1, "{profiles:?}");
    assert_eq!(profiles[0]["id"], "legacy-sftp");
    assert_eq!(profiles[0]["name"], "Legacy SFTP");
    assert_eq!(
        profiles[0]["credential_target"],
        "SumaFile.Remote.legacy-sftp"
    );
    assert_eq!(harness.event_names(), vec![PROFILES_CHANGED.to_string()]);

    // The setup runs once: a second call imports and announces nothing.
    harness.ok("profiles.list", Value::Null).await;
    assert_eq!(harness.event_names(), vec![PROFILES_CHANGED.to_string()]);
    harness.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn legacy_transfer_policy_seeds_unset_remote_settings_once() {
    let legacy_dir = tempfile::tempdir().unwrap();
    let legacy = legacy_dir.path().join("metadata.db");
    write_legacy_metadata_db(
        &legacy,
        &[],
        Some(r#"{"bandwidthMib":5,"lowLevelRetries":4}"#),
    )
    .unwrap();
    let data = tempfile::tempdir().unwrap();

    let first = Harness::with(HarnessOptions {
        // A value the user already set is never overwritten.
        settings: vec![("lowLevelRetries", "7")],
        legacy_db: Some(legacy.clone()),
        data: Some(data.path().to_path_buf()),
        ..HarnessOptions::default()
    })
    .await;
    first.ok("profiles.list", Value::Null).await;
    let values = first
        .host
        .get_settings(&["bandwidthMib", "lowLevelRetries", "checkMode"])
        .await
        .unwrap();
    assert_eq!(values["bandwidthMib"], Some("5".to_string()));
    assert_eq!(values["lowLevelRetries"], Some("7".to_string()));
    assert_eq!(values["checkMode"], None);
    first.stop().await;

    // A second handler on the same data folder and legacy database sets
    // nothing, even on a host where the settings are unset again.
    let second = Harness::with(HarnessOptions {
        legacy_db: Some(legacy),
        data: Some(data.path().to_path_buf()),
        ..HarnessOptions::default()
    })
    .await;
    second.ok("profiles.list", Value::Null).await;
    let values = second
        .host
        .get_settings(&["bandwidthMib", "lowLevelRetries"])
        .await
        .unwrap();
    assert_eq!(values["bandwidthMib"], None);
    assert_eq!(values["lowLevelRetries"], None);
    second.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn legacy_markers_are_scoped_to_the_database_they_read() {
    let legacy_dir = tempfile::tempdir().unwrap();
    // A: an empty remote_profiles table and no transfer.policy, as a smoke's
    // disposable database holds after the old startup refresh.
    let legacy_a = legacy_dir.path().join("a").join("metadata.db");
    std::fs::create_dir_all(legacy_a.parent().unwrap()).unwrap();
    write_legacy_metadata_db(&legacy_a, &[], None).unwrap();
    let data = tempfile::tempdir().unwrap();

    let first = Harness::with(HarnessOptions {
        legacy_db: Some(legacy_a.clone()),
        data: Some(data.path().to_path_buf()),
        ..HarnessOptions::default()
    })
    .await;
    assert_eq!(first.ok("profiles.list", Value::Null).await, json!([]));
    assert!(first.host.fake_events().is_empty());
    assert_eq!(
        first.host.get_settings(&["bandwidthMib"]).await.unwrap()["bandwidthMib"],
        None
    );

    // B: the real 1.0.4 database, one row and a transfer policy.
    let legacy_b = legacy_dir.path().join("b").join("metadata.db");
    std::fs::create_dir_all(legacy_b.parent().unwrap()).unwrap();
    write_legacy_metadata_db(
        &legacy_b,
        &[legacy_row(&first.info, "legacy-b")],
        Some(r#"{"bandwidthMib":5}"#),
    )
    .unwrap();
    first.stop().await;

    let second = Harness::with(HarnessOptions {
        legacy_db: Some(legacy_b.clone()),
        data: Some(data.path().to_path_buf()),
        ..HarnessOptions::default()
    })
    .await;
    let profiles = second.ok("profiles.list", Value::Null).await;
    assert_eq!(profiles.as_array().unwrap().len(), 1, "{profiles}");
    assert_eq!(profiles[0]["id"], "legacy-b");
    assert_eq!(second.event_names(), vec![PROFILES_CHANGED.to_string()]);
    assert_eq!(
        second.host.get_settings(&["bandwidthMib"]).await.unwrap()["bandwidthMib"],
        Some("5".to_string())
    );
    second.stop().await;

    let third = Harness::with(HarnessOptions {
        legacy_db: Some(legacy_b),
        data: Some(data.path().to_path_buf()),
        ..HarnessOptions::default()
    })
    .await;
    assert_eq!(
        third
            .ok("profiles.list", Value::Null)
            .await
            .as_array()
            .unwrap()
            .len(),
        1
    );
    assert!(third.host.fake_events().is_empty());
    assert_eq!(
        third.host.get_settings(&["bandwidthMib"]).await.unwrap()["bandwidthMib"],
        None
    );
    third.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn a_failed_legacy_import_is_retried_on_the_next_call() {
    let legacy_dir = tempfile::tempdir().unwrap();
    let legacy = legacy_dir.path().join("metadata.db");
    std::fs::write(&legacy, vec![0x5a; 4096]).unwrap();
    let harness = Harness::with(HarnessOptions {
        legacy_db: Some(legacy.clone()),
        ..HarnessOptions::default()
    })
    .await;

    assert_eq!(harness.ok("profiles.list", Value::Null).await, json!([]));
    let warnings: Vec<(String, String)> = harness
        .host
        .fake_log()
        .into_iter()
        .filter(|(level, _)| level == "warn")
        .collect();
    assert_eq!(warnings.len(), 1, "{warnings:?}");
    assert!(harness.host.fake_events().is_empty());

    std::fs::remove_file(&legacy).unwrap();
    write_legacy_metadata_db(&legacy, &[legacy_row(&harness.info, "legacy-retry")], None).unwrap();
    let profiles = harness.ok("profiles.list", Value::Null).await;
    assert_eq!(profiles.as_array().unwrap().len(), 1, "{profiles}");
    assert_eq!(profiles[0]["id"], "legacy-retry");
    assert_eq!(harness.event_names(), vec![PROFILES_CHANGED.to_string()]);
    harness.stop().await;
}

#[cfg(windows)]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn upgraded_profile_connects_with_its_saved_password() {
    use sumafile_remote_backend::handler::RemoteHandler;
    use sumafile_remote_backend::provider::DefaultRemoteProviderFactory;
    use sumafile_remote_backend::secrets::WindowsRemoteSecretStore;
    use sumafile_remote_test_server::TestServer;

    let remote = tempfile::tempdir().unwrap();
    std::fs::write(remote.path().join("upgraded.txt"), b"still here").unwrap();
    let server = TestServer::start(remote.path().to_path_buf())
        .await
        .unwrap();
    let info = server.info().clone();
    let target = format!("SumaFile.Remote.test-{}", common::random_hex());
    let store = WindowsRemoteSecretStore;
    assert_eq!(store.read_secret(&target).unwrap(), None, "a new target");
    {
        let _guard = common::DeleteCredentialOnDrop(target.clone());
        // Written the way 1.0.4 wrote it: "password" 0x00 <value>.
        store
            .write_secret(&target, &RemoteSecret::Password(info.password.clone()))
            .unwrap();
        let legacy_dir = tempfile::tempdir().unwrap();
        let legacy = legacy_dir.path().join("metadata.db");
        write_legacy_metadata_db(&legacy, &[info.legacy_profile("upgraded", &target)], None)
            .unwrap();
        let data = tempfile::tempdir().unwrap();
        let handler = Arc::new(RemoteHandler::with_parts(
            data.path().to_path_buf(),
            Arc::new(WindowsRemoteSecretStore),
            Arc::new(DefaultRemoteProviderFactory),
            Some(legacy),
        ));
        let host = sumafile_plugin::HostClient::fake(Default::default());
        use sumafile_plugin::PluginHandler;

        let session = handler
            .handle("session.connect", json!({ "profileId": "upgraded" }), &host)
            .await
            .expect("connects with the saved 1.0.4 password");
        let listing = handler
            .handle(
                "fs.list",
                json!({ "remoteSessionId": session["session_id"], "path": "/" }),
                &host,
            )
            .await
            .unwrap();
        assert_eq!(listing["entries"][0]["name"], "upgraded.txt");
        tokio::task::spawn_blocking(move || handler.shutdown())
            .await
            .unwrap();
    }
    assert_eq!(store.read_secret(&target).unwrap(), None);
    server.stop().await;
}
