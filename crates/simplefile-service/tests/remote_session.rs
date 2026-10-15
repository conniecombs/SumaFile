use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteAuthKind, RemoteProfile, RemoteProtocol};
use simplefile_service::remote::provider::{RemoteProvider, RemoteProviderFactory};
use simplefile_service::remote::secrets::RemoteSecret;
use simplefile_service::remote::session::RemoteSessionRegistry;

struct FakeProvider {
    fail_disconnect: bool,
}

impl RemoteProvider for FakeProvider {
    fn disconnect(&mut self) -> Result<(), String> {
        if self.fail_disconnect {
            Err("connection was lost".into())
        } else {
            Ok(())
        }
    }
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
        if path != "/" {
            return Err(format!("missing fake path {path}"));
        }

        Ok(DirectoryListing {
            path: "/".to_string(),
            parent: None,
            entries: vec![FileEntry {
                name: "release".to_string(),
                path: "/release".to_string(),
                is_dir: true,
                is_symlink: false,
                is_hidden: false,
                is_system: false,
                size: 0,
                modified: "2026-09-21T00:00:00Z".to_string(),
                extension: "".to_string(),
                permissions: None,
                symlink_target: None,
                git_status: None,
            }],
            is_network: true,
        })
    }

    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
        if path != "/" || name != "incoming" {
            return Err(format!("unexpected create {path}/{name}"));
        }

        Ok(FileEntry {
            name: "incoming".to_string(),
            path: "/incoming".to_string(),
            is_dir: true,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: 0,
            modified: "2026-09-21T00:00:00Z".to_string(),
            extension: "".to_string(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        })
    }

    fn rename_entry(&mut self, path: &str, new_name: &str) -> Result<FileEntry, String> {
        if path != "/incoming" || new_name != "archive" {
            return Err(format!("unexpected rename {path} -> {new_name}"));
        }

        Ok(FileEntry {
            name: "archive".to_string(),
            path: "/archive".to_string(),
            is_dir: true,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: 0,
            modified: "2026-09-21T00:00:00Z".to_string(),
            extension: "".to_string(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        })
    }

    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
        if paths != ["/archive"] {
            return Err(format!("unexpected delete {paths:?}"));
        }

        Ok(paths.to_vec())
    }

    fn download(
        &mut self,
        path: &str,
        destination: &mut dyn std::io::Write,
    ) -> Result<u64, String> {
        if path != "/release/app.txt" {
            return Err(format!("unexpected read {path}"));
        }
        destination
            .write_all(b"remote bytes")
            .map_err(|e| e.to_string())?;
        Ok(12)
    }
    fn upload(&mut self, path: &str, source: &mut dyn std::io::Read) -> Result<u64, String> {
        let mut bytes = Vec::new();
        source.read_to_end(&mut bytes).map_err(|e| e.to_string())?;
        if path != "/incoming/app.txt" || bytes != b"local bytes" {
            return Err(format!("unexpected write {path}"));
        }
        Ok(bytes.len() as u64)
    }
}

struct FakeFactory;

impl RemoteProviderFactory for FakeFactory {
    fn connect(
        &self,
        profile: &RemoteProfile,
        _secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        if profile.id != "prod" {
            return Err("unexpected profile".to_string());
        }

        Ok(Box::new(FakeProvider {
            fail_disconnect: profile.host == "disconnected.example.com",
        }))
    }
}

#[test]
fn remote_session_registry_connects_lists_and_disconnects() {
    let profile = RemoteProfile {
        id: "prod".to_string(),
        name: "Production".to_string(),
        protocol: RemoteProtocol::Sftp,
        host: "files.example.com".to_string(),
        port: 22,
        username: "deploy".to_string(),
        root_path: "/".to_string(),
        auth_kind: RemoteAuthKind::Password,
        insecure_plain_ftp: false,
        passive_mode: true,
        credential_target: Some("SumaFile.Remote.prod".to_string()),
        private_key_path: None,
        trusted_host_fingerprint: None,
    };
    let mut registry = RemoteSessionRegistry::new(FakeFactory);

    let session = registry
        .connect_profile(profile.clone(), None)
        .expect("connect");

    assert!(!session.session_id.is_empty());
    assert_eq!(session.profile_id, "prod");
    assert_eq!(session.protocol, RemoteProtocol::Sftp);

    let listing = registry
        .list_directory(&session.session_id, "/")
        .expect("list");
    assert_eq!(listing.path, "/");
    assert_eq!(listing.entries[0].name, "release");
    assert!(listing.is_network);

    registry
        .disconnect(&session.session_id)
        .expect("disconnect");
    let missing = registry
        .list_directory(&session.session_id, "/")
        .expect_err("session should be gone");
    assert!(missing.contains("remote session not found"));

    let mut disconnected_profile = profile;
    disconnected_profile.host = "disconnected.example.com".into();
    let failed_session = registry
        .connect_profile(disconnected_profile, None)
        .unwrap();
    registry
        .disconnect(&failed_session.session_id)
        .expect("remove lost connection");
    assert!(registry.get(&failed_session.session_id).is_none());
}

#[test]
fn remote_session_registry_mutates_entries_through_provider() {
    let profile = RemoteProfile {
        id: "prod".to_string(),
        name: "Production".to_string(),
        protocol: RemoteProtocol::Sftp,
        host: "files.example.com".to_string(),
        port: 22,
        username: "deploy".to_string(),
        root_path: "/".to_string(),
        auth_kind: RemoteAuthKind::Password,
        insecure_plain_ftp: false,
        passive_mode: true,
        credential_target: Some("SumaFile.Remote.prod".to_string()),
        private_key_path: None,
        trusted_host_fingerprint: None,
    };
    let mut registry = RemoteSessionRegistry::new(FakeFactory);
    let session = registry.connect_profile(profile, None).expect("connect");

    let created = registry
        .create_directory(&session.session_id, "/", "incoming")
        .expect("create remote directory");
    assert_eq!(created.name, "incoming");
    assert_eq!(created.path, "/incoming");
    assert!(created.is_dir);

    let renamed = registry
        .rename_entry(&session.session_id, "/incoming", "archive")
        .expect("rename remote entry");
    assert_eq!(renamed.name, "archive");
    assert_eq!(renamed.path, "/archive");

    let deleted = registry
        .delete_entries(&session.session_id, &["/archive".to_string()])
        .expect("delete remote entry");
    assert_eq!(deleted, vec!["/archive".to_string()]);
}

#[test]
fn remote_session_registry_reads_and_writes_files_through_provider() {
    let profile = RemoteProfile {
        id: "prod".to_string(),
        name: "Production".to_string(),
        protocol: RemoteProtocol::Sftp,
        host: "files.example.com".to_string(),
        port: 22,
        username: "deploy".to_string(),
        root_path: "/".to_string(),
        auth_kind: RemoteAuthKind::Password,
        insecure_plain_ftp: false,
        passive_mode: true,
        credential_target: Some("SumaFile.Remote.prod".to_string()),
        private_key_path: None,
        trusted_host_fingerprint: None,
    };
    let mut registry = RemoteSessionRegistry::new(FakeFactory);
    let session = registry.connect_profile(profile, None).expect("connect");

    let mut bytes = Vec::new();
    registry
        .provider(&session.session_id)
        .unwrap()
        .with(|provider| provider.download("/release/app.txt", &mut bytes))
        .unwrap();
    assert_eq!(bytes, b"remote bytes");
    let written = registry
        .provider(&session.session_id)
        .unwrap()
        .with(|provider| {
            provider.upload(
                "/incoming/app.txt",
                &mut std::io::Cursor::new(b"local bytes"),
            )
        })
        .unwrap();
    assert_eq!(written, 11);
}
