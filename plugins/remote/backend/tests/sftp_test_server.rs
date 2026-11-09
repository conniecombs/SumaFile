//! The real SFTP provider against the in-process loopback test server.

use std::io::Cursor;
use std::path::Path;

use sumafile_remote_backend::models::{RemoteAuthKind, RemoteProfile, RemoteProtocol};
use sumafile_remote_backend::provider::{
    DefaultRemoteProviderFactory, RemoteProvider, RemoteProviderFactory,
};
use sumafile_remote_backend::secrets::RemoteSecret;
use sumafile_remote_backend::session::RemoteSessionRegistry;
use sumafile_remote_test_server::{TestServer, TestServerInfo};

const UNICODE_FOLDER: &str = "caf\u{e9} \u{8cc7}\u{6599}";
const PUNCTUATED_FILE: &str = "literal [brackets] & spaces.txt";

fn profile_for(info: &TestServerInfo, trusted: Option<&str>) -> RemoteProfile {
    RemoteProfile {
        id: "test-server".to_string(),
        name: "Test server".to_string(),
        protocol: RemoteProtocol::Sftp,
        host: "127.0.0.1".to_string(),
        port: info.port,
        username: info.user.clone(),
        root_path: "/".to_string(),
        auth_kind: RemoteAuthKind::Password,
        insecure_plain_ftp: false,
        passive_mode: true,
        credential_target: Some("SumaFile.Remote.test-server".to_string()),
        private_key_path: None,
        trusted_host_fingerprint: trusted.map(str::to_string),
    }
}

fn password(info: &TestServerInfo) -> RemoteSecret {
    RemoteSecret::Password(info.password.clone())
}

/// Connects on the calling thread. Provider calls block, so the async tests
/// call this inside `block_in_place` or `spawn_blocking`, as the handler will.
fn connect(info: &TestServerInfo) -> Box<dyn RemoteProvider> {
    DefaultRemoteProviderFactory
        .connect(
            &profile_for(info, Some(&info.fingerprint)),
            Some(&password(info)),
        )
        .expect("connect to the test server")
}

fn connect_error(profile: &RemoteProfile, secret: &RemoteSecret) -> String {
    match DefaultRemoteProviderFactory.connect(profile, Some(secret)) {
        Ok(mut provider) => {
            let _ = provider.disconnect();
            panic!("the connection was expected to fail");
        }
        Err(error) => error,
    }
}

fn names(provider: &mut dyn RemoteProvider, path: &str) -> Vec<String> {
    let mut names: Vec<String> = provider
        .list_directory(path)
        .unwrap_or_else(|error| panic!("list {path}: {error}"))
        .entries
        .into_iter()
        .map(|entry| entry.name)
        .collect();
    names.sort();
    names
}

fn disk_names(folder: &Path) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(folder)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .collect();
    names.sort();
    names
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn untrusted_host_key_fails_with_the_fingerprint_to_trust() {
    let root = tempfile::tempdir().unwrap();
    let server = TestServer::start(root.path().to_path_buf()).await.unwrap();
    let info = server.info().clone();
    assert!(
        info.fingerprint.starts_with("SHA256:"),
        "{}",
        info.fingerprint
    );

    let error =
        tokio::task::block_in_place(|| connect_error(&profile_for(&info, None), &password(&info)));

    assert!(
        error.starts_with("SFTP host fingerprint is not trusted. Check that this fingerprint belongs to the server, then trust it to connect: "),
        "{error}"
    );
    assert!(error.ends_with(&info.fingerprint), "{error}");
    server.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn another_servers_fingerprint_fails_with_a_mismatch() {
    let root_a = tempfile::tempdir().unwrap();
    let root_b = tempfile::tempdir().unwrap();
    let server_a = TestServer::start(root_a.path().to_path_buf())
        .await
        .unwrap();
    let server_b = TestServer::start(root_b.path().to_path_buf())
        .await
        .unwrap();
    let a = server_a.info().clone();
    let b = server_b.info().clone();
    assert_ne!(a.fingerprint, b.fingerprint, "every server has a fresh key");

    let error = tokio::task::block_in_place(|| {
        connect_error(&profile_for(&b, Some(&a.fingerprint)), &password(&b))
    });

    assert_eq!(
        error,
        format!(
            "SFTP host fingerprint mismatch. The server key changed since you trusted it. Expected {}, got {}",
            a.fingerprint, b.fingerprint
        )
    );
    server_a.stop().await;
    server_b.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sftp_lists_creates_renames_and_deletes_unicode_and_punctuated_names() {
    let root = tempfile::tempdir().unwrap();
    std::fs::create_dir(root.path().join(UNICODE_FOLDER)).unwrap();
    std::fs::write(root.path().join(UNICODE_FOLDER).join("hello.txt"), b"hello").unwrap();
    std::fs::write(root.path().join(PUNCTUATED_FILE), b"punctuated").unwrap();
    let server = TestServer::start(root.path().to_path_buf()).await.unwrap();
    let info = server.info().clone();
    let disk = root.path().to_path_buf();

    tokio::task::block_in_place(|| {
        let mut provider = connect(&info);
        let provider = provider.as_mut();

        let listing = provider.list_directory("/").unwrap();
        assert_eq!(listing.path, "/");
        assert_eq!(listing.parent, None);
        assert!(listing.is_network);
        let folder = listing
            .entries
            .iter()
            .find(|entry| entry.name == UNICODE_FOLDER)
            .expect("the unicode folder is listed");
        assert!(folder.is_dir);
        assert_eq!(folder.path, format!("/{UNICODE_FOLDER}"));
        let file = listing
            .entries
            .iter()
            .find(|entry| entry.name == PUNCTUATED_FILE)
            .expect("the punctuated file is listed");
        assert!(!file.is_dir);
        assert_eq!(file.size, 10);
        assert_eq!(file.extension, "txt");
        assert_eq!(
            names(provider, &format!("/{UNICODE_FOLDER}")),
            vec!["hello.txt".to_string()]
        );

        let created = provider.create_directory("/", "new [x]").unwrap();
        assert_eq!(created.name, "new [x]");
        assert_eq!(created.path, "/new [x]");
        assert!(created.is_dir);
        assert!(disk.join("new [x]").is_dir());

        let renamed = provider
            .rename_entry("/new [x]", "renamed folder [ok]")
            .unwrap();
        assert_eq!(renamed.name, "renamed folder [ok]");
        assert_eq!(renamed.path, "/renamed folder [ok]");
        assert!(renamed.is_dir);
        assert!(!disk.join("new [x]").exists());
        assert!(disk.join("renamed folder [ok]").is_dir());
        assert_eq!(
            names(provider, "/"),
            // Sorted: "caf…" < "literal…" < "renamed…".
            vec![
                UNICODE_FOLDER.to_string(),
                PUNCTUATED_FILE.to_string(),
                "renamed folder [ok]".to_string()
            ]
        );

        let deleted = provider
            .delete_entries(&[
                "/renamed folder [ok]".to_string(),
                format!("/{PUNCTUATED_FILE}"),
            ])
            .unwrap();
        assert_eq!(
            deleted,
            vec![
                "/renamed folder [ok]".to_string(),
                format!("/{PUNCTUATED_FILE}")
            ]
        );
        assert_eq!(disk_names(&disk), vec![UNICODE_FOLDER.to_string()]);
        assert_eq!(names(provider, "/"), vec![UNICODE_FOLDER.to_string()]);

        let missing = provider.stat("/nothing here").unwrap();
        assert!(missing.is_none());
        let hello = provider
            .stat(&format!("/{UNICODE_FOLDER}/hello.txt"))
            .unwrap()
            .expect("stat finds the file");
        assert_eq!(hello.size, 5);

        provider.disconnect().unwrap();
    });
    server.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sftp_streams_a_two_mebibyte_file_both_ways() {
    let root = tempfile::tempdir().unwrap();
    let server = TestServer::start(root.path().to_path_buf()).await.unwrap();
    let info = server.info().clone();
    let mut bytes = vec![0u8; 2 * 1024 * 1024];
    getrandom::fill(&mut bytes).unwrap();

    let downloaded = tokio::task::block_in_place(|| {
        let mut provider = connect(&info);
        let written = provider
            .upload("/big [payload].bin", &mut Cursor::new(&bytes))
            .expect("upload");
        assert_eq!(written, bytes.len() as u64);
        let mut downloaded = Vec::new();
        let read = provider
            .download("/big [payload].bin", &mut downloaded)
            .expect("download");
        assert_eq!(read, bytes.len() as u64);
        provider.disconnect().unwrap();
        downloaded
    });

    assert_eq!(
        std::fs::read(root.path().join("big [payload].bin")).unwrap(),
        bytes
    );
    assert!(downloaded == bytes, "the downloaded bytes differ");
    server.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn sftp_calls_work_from_spawn_blocking_on_a_two_worker_runtime() {
    let root = tempfile::tempdir().unwrap();
    std::fs::write(root.path().join("one.txt"), b"1").unwrap();
    let server = TestServer::start(root.path().to_path_buf()).await.unwrap();
    let info = server.info().clone();

    // The handler's shape: a registry shared with blocking threads, with
    // connect, list and disconnect each on its own spawn_blocking call.
    let registry = RemoteSessionRegistry::new(DefaultRemoteProviderFactory);
    let session = {
        let registry = registry.clone();
        let profile = profile_for(&info, Some(&info.fingerprint));
        let secret = password(&info);
        tokio::task::spawn_blocking(move || registry.connect_profile(profile, Some(secret)))
            .await
            .unwrap()
            .expect("connect")
    };
    assert_eq!(session.profile_id, "test-server");
    assert!(session.session_id.starts_with("remote-session-"));

    let listing = {
        let registry = registry.clone();
        let id = session.session_id.clone();
        tokio::task::spawn_blocking(move || registry.list_directory(&id, "/"))
            .await
            .unwrap()
            .expect("list")
    };
    assert_eq!(listing.entries.len(), 1);
    assert_eq!(listing.entries[0].name, "one.txt");

    {
        let registry = registry.clone();
        let id = session.session_id.clone();
        tokio::task::spawn_blocking(move || registry.disconnect(&id))
            .await
            .unwrap()
            .expect("disconnect");
    }
    assert!(registry.get(&session.session_id).is_none());
    server.stop().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn wrong_password_fails_without_a_session() {
    let root = tempfile::tempdir().unwrap();
    let server = TestServer::start(root.path().to_path_buf()).await.unwrap();
    let info = server.info().clone();
    let registry = RemoteSessionRegistry::new(DefaultRemoteProviderFactory);

    let result = {
        let registry = registry.clone();
        let profile = profile_for(&info, Some(&info.fingerprint));
        tokio::task::spawn_blocking(move || {
            registry.connect_profile(
                profile,
                Some(RemoteSecret::Password("not the password".to_string())),
            )
        })
        .await
        .unwrap()
    };

    let error = result.expect_err("a wrong password must not connect");
    assert_eq!(
        error,
        "SFTP password authentication was rejected by the server"
    );
    assert!(registry.get("remote-session-1").is_none());
    let missing = registry
        .list_directory("remote-session-1", "/")
        .expect_err("no session was registered");
    assert_eq!(missing, "remote session not found: remote-session-1");
    server.stop().await;
}
