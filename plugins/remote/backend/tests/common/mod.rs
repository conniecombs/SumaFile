//! The harness the handler and transfer tests share: a `RemoteHandler` built
//! with `with_parts`, an in-memory secret store, `HostClient::fake` and the
//! in-process SFTP test server.

#![allow(dead_code)]

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use serde_json::{json, Value};
use sumafile_plugin::{HostClient, PluginError, PluginHandler};
use sumafile_remote_backend::handler::RemoteHandler;
use sumafile_remote_backend::provider::{DefaultRemoteProviderFactory, RemoteProviderFactory};
use sumafile_remote_backend::secrets::{MemoryRemoteSecretStore, RemoteSecretStore};
use sumafile_remote_test_server::{TestServer, TestServerInfo};
use tempfile::TempDir;

pub struct Harness {
    pub handler: Arc<RemoteHandler>,
    pub host: HostClient,
    pub secrets: Arc<MemoryRemoteSecretStore>,
    pub server: Option<TestServer>,
    pub info: TestServerInfo,
    /// The folder the test server serves.
    pub remote: TempDir,
    /// The backend's data folder.
    pub data: PathBuf,
    data_owner: Option<TempDir>,
    /// A local folder for uploads and downloads.
    pub local: TempDir,
}

#[derive(Default)]
pub struct HarnessOptions {
    pub settings: Vec<(&'static str, &'static str)>,
    pub factory: Option<Arc<dyn RemoteProviderFactory>>,
    pub legacy_db: Option<PathBuf>,
    /// A data folder the test owns, so two handlers can share it.
    pub data: Option<PathBuf>,
}

impl Harness {
    pub async fn start() -> Harness {
        Harness::with(HarnessOptions::default()).await
    }

    pub async fn with_settings(settings: &[(&'static str, &'static str)]) -> Harness {
        Harness::with(HarnessOptions {
            settings: settings.to_vec(),
            ..HarnessOptions::default()
        })
        .await
    }

    pub async fn with(options: HarnessOptions) -> Harness {
        let remote = tempfile::tempdir().unwrap();
        let (data, data_owner) = match options.data {
            Some(path) => (path, None),
            None => {
                let owner = tempfile::tempdir().unwrap();
                (owner.path().to_path_buf(), Some(owner))
            }
        };
        let local = tempfile::tempdir().unwrap();
        let server = TestServer::start(remote.path().to_path_buf())
            .await
            .expect("start the SFTP test server");
        let info = server.info().clone();
        let secrets = Arc::new(MemoryRemoteSecretStore::default());
        let factory = options
            .factory
            .unwrap_or_else(|| Arc::new(DefaultRemoteProviderFactory));
        let handler = Arc::new(RemoteHandler::with_parts(
            data.clone(),
            secrets.clone() as Arc<dyn RemoteSecretStore>,
            factory,
            options.legacy_db,
        ));
        let settings: HashMap<String, String> = options
            .settings
            .iter()
            .map(|(key, value)| (key.to_string(), value.to_string()))
            .collect();
        Harness {
            handler,
            host: HostClient::fake(settings),
            secrets,
            server: Some(server),
            info,
            remote,
            data,
            data_owner,
            local,
        }
    }

    pub async fn call(&self, method: &str, params: Value) -> Result<Value, PluginError> {
        self.handler.handle(method, params, &self.host).await
    }

    pub async fn ok(&self, method: &str, params: Value) -> Value {
        match self.call(method, params).await {
            Ok(value) => value,
            Err(error) => panic!("{method} failed: {error}"),
        }
    }

    pub async fn err(&self, method: &str, params: Value) -> String {
        match self.call(method, params).await {
            Ok(value) => panic!("{method} was expected to fail, got {value}"),
            Err(error) => error.message().to_string(),
        }
    }

    /// An SFTP password profile for the test server, its key trusted.
    pub fn profile(&self) -> Value {
        server_profile(&self.info)
    }

    /// Saves the server profile without a secret and connects with the
    /// password in params; returns the session id.
    pub async fn connect(&self) -> String {
        let saved = self
            .ok("profiles.save", json!({ "profile": self.profile() }))
            .await;
        let session = self
            .ok(
                "session.connect",
                json!({ "profileId": saved["id"], "secret": self.info.password }),
            )
            .await;
        session["session_id"].as_str().unwrap().to_string()
    }

    pub fn event_names(&self) -> Vec<String> {
        self.host
            .fake_events()
            .into_iter()
            .map(|(name, _)| name)
            .collect()
    }

    /// Waits until the fake host has seen a `report` call.
    pub async fn wait_for_report(&self) {
        tokio::time::timeout(Duration::from_secs(10), async {
            while !self
                .host
                .fake_progress_calls()
                .iter()
                .any(|call| call.starts_with("report "))
            {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .expect("no progress report within 10 s");
    }

    pub async fn stop(mut self) {
        let handler = self.handler.clone();
        tokio::task::spawn_blocking(move || handler.shutdown())
            .await
            .unwrap();
        if let Some(server) = self.server.take() {
            server.stop().await;
        }
    }
}

pub fn server_profile(info: &TestServerInfo) -> Value {
    json!({
        "id": null,
        "name": "Test server",
        "protocol": "sftp",
        "host": "127.0.0.1",
        "port": info.port,
        "username": info.user,
        "root_path": "/",
        "auth_kind": "password",
        "insecure_plain_ftp": false,
        "passive_mode": true,
        "credential_target": null,
        "private_key_path": null,
        "trusted_host_fingerprint": info.fingerprint
    })
}

/// Names in `folder`, sorted.
pub fn names(folder: &Path) -> Vec<String> {
    let mut names: Vec<String> = std::fs::read_dir(folder)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .collect();
    names.sort();
    names
}

pub fn no_partials(folder: &Path) -> bool {
    names(folder)
        .iter()
        .all(|name| !name.contains(".sumafile-partial"))
}

/// Bytes that are not all one value, so a misplaced chunk shows.
pub fn pattern(len: usize) -> Vec<u8> {
    (0..len).map(|n| (n % 251) as u8).collect()
}

pub fn random_hex() -> String {
    let mut bytes = [0u8; 8];
    getrandom::fill(&mut bytes).unwrap();
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Deletes a `SumaFile.Remote.test-*` credential this test wrote, even when
/// an assertion fails first.
#[cfg(windows)]
pub struct DeleteCredentialOnDrop(pub String);

#[cfg(windows)]
impl Drop for DeleteCredentialOnDrop {
    fn drop(&mut self) {
        use sumafile_remote_backend::secrets::WindowsRemoteSecretStore;
        assert!(self.0.starts_with("SumaFile.Remote.test-"));
        let _ = WindowsRemoteSecretStore.delete_secret(&self.0);
    }
}
