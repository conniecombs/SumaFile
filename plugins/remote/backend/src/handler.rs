//! `RemoteHandler`: the eleven plugin-owned methods of `com.sumafile.remote`
//! (see `plugins/remote/protocol.json`), served over the plugin pipe.
//!
//! Every store, secret and provider call runs in `spawn_blocking`, so the
//! connection keeps reading cancels and other calls while a connect or a
//! transfer runs. `emit` is fire-and-forget. The first call of any method
//! runs the one-time legacy setup when a 1.0.4 metadata database resolves.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, MutexGuard};

use serde::de::DeserializeOwned;
use serde::Deserialize;
use serde_json::{json, Value};
use sumafile_plugin::{async_trait, HostClient, PluginError, PluginHandler};

use crate::bridge::TransferBridge;
use crate::legacy::{
    import_legacy_profiles, legacy_marker_key, legacy_metadata_db_path, legacy_transfer_policy,
    LegacyImport, LEGACY_TRANSFER_SETTINGS_SEEDED,
};
use crate::models::{RemoteAuthKind, RemoteProfile, RemoteProfileInput};
use crate::profiles::ProfileStore;
use crate::provider::{DefaultRemoteProviderFactory, RemoteProviderFactory};
use crate::secrets::{RemoteSecret, RemoteSecretStore, WindowsRemoteSecretStore};
use crate::session::RemoteSessionRegistry;
use crate::settings::{settings_from_legacy_policy, TransferSettingsCache};
use crate::transfer::{transfer_entries, validate_request, RemoteTransferRequest};

pub const PROGRESS_EVENT: &str = "remote.transfer-progress";
pub const TUNING_EVENT: &str = "remote.transfer-tuning";
pub const PROFILES_CHANGED_EVENT: &str = "remote.profiles-changed";
pub const TRANSFER_CANCELLED: &str = "Transfer cancelled";

const PROFILES_LIST: &str = "profiles.list";
const PROFILES_SAVE: &str = "profiles.save";
const PROFILES_DELETE: &str = "profiles.delete";
const PROFILES_TEST: &str = "profiles.test";
const SESSION_CONNECT: &str = "session.connect";
const SESSION_DISCONNECT: &str = "session.disconnect";
const FS_LIST: &str = "fs.list";
const FS_CREATE_DIRECTORY: &str = "fs.create_directory";
const FS_RENAME: &str = "fs.rename";
const FS_DELETE: &str = "fs.delete";
const TRANSFER_RUN: &str = "transfer.run";

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// What the one-time legacy setup still has to do.
#[derive(Default)]
struct Setup {
    profiles_imported: bool,
    settings_seeded: bool,
}

pub struct RemoteHandler {
    store: ProfileStore,
    secrets: Arc<dyn RemoteSecretStore>,
    sessions: RemoteSessionRegistry,
    /// The host each session connected to, for the Transfers row's title.
    session_hosts: Mutex<HashMap<String, String>>,
    settings: Arc<TransferSettingsCache>,
    setup: tokio::sync::Mutex<Setup>,
    legacy_db: Option<PathBuf>,
}

#[derive(Deserialize)]
struct ProfileParams {
    profile: RemoteProfileInput,
    #[serde(default)]
    secret: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProfileIdParams {
    profile_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ConnectParams {
    profile_id: String,
    #[serde(default)]
    secret: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct SessionParams {
    remote_session_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ListParams {
    remote_session_id: String,
    path: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct CreateDirectoryParams {
    remote_session_id: String,
    path: String,
    name: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RenameParams {
    remote_session_id: String,
    path: String,
    new_name: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeleteParams {
    remote_session_id: String,
    paths: Vec<String>,
}

fn parse<T: DeserializeOwned>(method: &str, params: Value) -> Result<T, PluginError> {
    let params = if params.is_null() { json!({}) } else { params };
    serde_json::from_value(params)
        .map_err(|error| PluginError::InvalidParams(format!("Invalid {method} params: {error}")))
}

fn to_value(value: impl serde::Serialize) -> Result<Value, PluginError> {
    serde_json::to_value(value).map_err(|error| PluginError::Application(error.to_string()))
}

/// Runs a blocking call on the blocking pool; its error is the reply.
async fn blocking<T, F>(call: F) -> Result<T, PluginError>
where
    T: Send + 'static,
    F: FnOnce() -> Result<T, String> + Send + 'static,
{
    tokio::task::spawn_blocking(call)
        .await
        .map_err(|error| PluginError::Application(format!("remote call failed: {error}")))?
        .map_err(PluginError::Application)
}

fn secret_for_profile(profile: &RemoteProfile, secret: String) -> Option<RemoteSecret> {
    match profile.auth_kind {
        RemoteAuthKind::Password => Some(RemoteSecret::Password(secret)),
        RemoteAuthKind::PrivateKey => Some(RemoteSecret::PrivateKeyPassphrase(secret)),
        RemoteAuthKind::Agent | RemoteAuthKind::Anonymous => None,
    }
}

fn given_secret(profile: &RemoteProfile, secret: Option<String>) -> Option<RemoteSecret> {
    secret
        .filter(|secret| !secret.is_empty())
        .and_then(|secret| secret_for_profile(profile, secret))
}

/// `emit` is fire-and-forget: a connection where it fails still gets every reply.
async fn emit_profiles_changed(host: &HostClient) {
    let _ = host.emit(PROFILES_CHANGED_EVENT, json!({})).await;
}

/// `1 item` or `<n> items`.
fn item_count(count: usize) -> String {
    if count == 1 {
        "1 item".to_string()
    } else {
        format!("{count} items")
    }
}

/// The Transfers row's title.
fn transfer_title(request: &RemoteTransferRequest, host: &str) -> String {
    let items = item_count(request.sources.len());
    if request.direction == "upload" {
        format!("Uploading {items} to {host}:{}", request.destination)
    } else {
        format!("Downloading {items} from {host}")
    }
}

impl RemoteHandler {
    /// The production handler: Windows Credential Manager secrets, the real
    /// providers, and the legacy database the process environment resolves.
    pub fn new(data_dir: PathBuf) -> RemoteHandler {
        let legacy_db = legacy_metadata_db_path(&|name| std::env::var(name).ok());
        RemoteHandler::with_parts(
            data_dir,
            Arc::new(WindowsRemoteSecretStore),
            Arc::new(DefaultRemoteProviderFactory),
            legacy_db,
        )
    }

    pub fn with_parts(
        data_dir: PathBuf,
        secrets: Arc<dyn RemoteSecretStore>,
        factory: Arc<dyn RemoteProviderFactory>,
        legacy_db: Option<PathBuf>,
    ) -> RemoteHandler {
        RemoteHandler {
            store: ProfileStore::open(&data_dir),
            secrets,
            sessions: RemoteSessionRegistry::with_factory(factory),
            session_hosts: Mutex::new(HashMap::new()),
            settings: Arc::new(TransferSettingsCache::new()),
            setup: tokio::sync::Mutex::new(Setup::default()),
            legacy_db,
        }
    }

    /// Disconnects every session. Safe from any thread: the providers own
    /// runtimes of their own, which may only be dropped where blocking is
    /// allowed, so the work runs on a thread of its own.
    pub fn shutdown(&self) {
        let sessions = self.sessions.clone();
        let ids: Vec<String> = lock(&self.session_hosts)
            .drain()
            .map(|(id, _)| id)
            .collect();
        let worker = std::thread::spawn(move || {
            for id in ids {
                if let Err(error) = sessions.disconnect(&id) {
                    log::warn!("Remote disconnect at shutdown failed for {id}: {error}");
                }
            }
            sessions.clear();
        });
        let _ = worker.join();
    }

    /// The one-time legacy setup, in order: the profile import, then the
    /// transfer-settings seed. A step that fails is logged as a warning and
    /// runs again on the next call; it never fails the call that ran it.
    async fn ensure_setup(&self, host: &HostClient) {
        let Some(legacy_db) = self.legacy_db.clone() else {
            return;
        };
        let mut setup = self.setup.lock().await;
        if !setup.profiles_imported {
            let store = self.store.clone();
            let db = legacy_db.clone();
            match blocking(move || import_legacy_profiles(&store, &db)).await {
                Ok(imported) => {
                    setup.profiles_imported = true;
                    if matches!(imported, LegacyImport::Imported(count) if count > 0) {
                        emit_profiles_changed(host).await;
                    }
                }
                Err(error) => {
                    let _ = host
                        .log(
                            "warn",
                            &format!(
                                "Importing the 1.0.4 remote profiles from {} failed; it runs again on the next call: {error}",
                                legacy_db.display()
                            ),
                        )
                        .await;
                    return;
                }
            }
        }
        if !setup.settings_seeded {
            match self.seed_transfer_settings(host, &legacy_db).await {
                Ok(()) => setup.settings_seeded = true,
                Err(error) => {
                    let _ = host
                        .log(
                            "warn",
                            &format!(
                                "Seeding the remote transfer settings from {} failed; it runs again on the next call: {error}",
                                legacy_db.display()
                            ),
                        )
                        .await;
                }
            }
        }
    }

    /// Copies the legacy `transfer.policy` values that differ from the
    /// defaults into plugin settings the host reports unset. The marker is
    /// written only when the read and every write succeeded.
    async fn seed_transfer_settings(
        &self,
        host: &HostClient,
        legacy_db: &std::path::Path,
    ) -> Result<(), String> {
        let marker = legacy_marker_key(LEGACY_TRANSFER_SETTINGS_SEEDED, legacy_db);
        let store = self.store.clone();
        let db = legacy_db.to_path_buf();
        let check = marker.clone();
        let policy = blocking(move || {
            if store.meta(&check)?.is_some() {
                return Ok(None);
            }
            legacy_transfer_policy(&db).map(Some)
        })
        .await
        .map_err(|error| error.message().to_string())?;
        let Some(policy) = policy else {
            return Ok(());
        };
        let pairs = policy
            .as_deref()
            .map(settings_from_legacy_policy)
            .unwrap_or_default();
        if !pairs.is_empty() {
            let keys: Vec<&str> = pairs.iter().map(|(key, _)| key.as_str()).collect();
            let current = host.get_settings(&keys).await?;
            for (key, value) in &pairs {
                if current.get(key).is_none_or(Option::is_none) {
                    host.set_setting(key, value).await?;
                }
            }
        }
        let store = self.store.clone();
        let stamp = chrono::Utc::now().to_rfc3339();
        blocking(move || store.set_meta(&marker, &stamp))
            .await
            .map_err(|error| error.message().to_string())
    }

    async fn profile_by_id(&self, profile_id: String) -> Result<RemoteProfile, PluginError> {
        let store = self.store.clone();
        blocking(move || {
            store
                .list()?
                .into_iter()
                .find(|profile| profile.id == profile_id)
                .ok_or_else(|| format!("remote profile not found: {profile_id}"))
        })
        .await
    }

    async fn save(&self, params: ProfileParams, host: &HostClient) -> Result<Value, PluginError> {
        let store = self.store.clone();
        let secrets = self.secrets.clone();
        let profile = blocking(move || {
            let profile = store.save(params.profile)?;
            if let Some(secret) = given_secret(&profile, params.secret) {
                let target = profile
                    .credential_target
                    .as_deref()
                    .ok_or("saved remote profile did not include a credential target")?;
                secrets.write_secret(target, &secret)?;
            }
            Ok(profile)
        })
        .await?;
        emit_profiles_changed(host).await;
        to_value(profile)
    }

    async fn delete(&self, profile_id: String, host: &HostClient) -> Result<Value, PluginError> {
        let store = self.store.clone();
        let secrets = self.secrets.clone();
        blocking(move || {
            if let Some(target) = store
                .delete(&profile_id)?
                .and_then(|profile| profile.credential_target)
            {
                secrets.delete_secret(&target)?;
            }
            Ok(())
        })
        .await?;
        emit_profiles_changed(host).await;
        Ok(Value::Null)
    }

    async fn test(&self, params: ProfileParams) -> Result<Value, PluginError> {
        let secrets = self.secrets.clone();
        let sessions = self.sessions.clone();
        let result = blocking(move || {
            let profile = ProfileStore::prepare(params.profile)?;
            let secret = given_secret(&profile, params.secret).or_else(|| {
                profile
                    .credential_target
                    .as_deref()
                    .and_then(|target| secrets.read_secret(target).ok().flatten())
            });
            Ok(sessions.test_profile(profile, secret))
        })
        .await?;
        to_value(result)
    }

    async fn connect(&self, params: ConnectParams) -> Result<Value, PluginError> {
        let profile = self.profile_by_id(params.profile_id).await?;
        let host = profile.host.clone();
        let secrets = self.secrets.clone();
        let sessions = self.sessions.clone();
        let session = blocking(move || {
            let secret = match given_secret(&profile, params.secret) {
                Some(secret) => Some(secret),
                None => match profile.credential_target.as_deref() {
                    Some(target) => secrets.read_secret(target)?,
                    None => None,
                },
            };
            sessions.connect_profile(profile, secret)
        })
        .await?;
        lock(&self.session_hosts).insert(session.session_id.clone(), host);
        to_value(session)
    }

    async fn disconnect(&self, session_id: String) -> Result<Value, PluginError> {
        let sessions = self.sessions.clone();
        let id = session_id.clone();
        blocking(move || sessions.disconnect(&id)).await?;
        lock(&self.session_hosts).remove(&session_id);
        Ok(Value::Null)
    }

    async fn transfer(&self, params: Value, host: &HostClient) -> Result<Value, PluginError> {
        let request: RemoteTransferRequest = parse(TRANSFER_RUN, params)?;
        validate_request(&request).map_err(PluginError::Application)?;
        let provider = self
            .sessions
            .provider(&request.remote_session_id)
            .map_err(PluginError::Application)?;
        let remote_host = lock(&self.session_hosts)
            .get(&request.remote_session_id)
            .cloned()
            .unwrap_or_else(|| "remote".to_string());

        let row_id = match host
            .progress_begin(&transfer_title(&request, &remote_host))
            .await
        {
            Ok(row_id) => Some(row_id),
            Err(error) => {
                let _ = host
                    .log(
                        "warn",
                        &format!(
                            "The Transfers row for {} could not be opened; the transfer runs without one: {error}",
                            request.operation_id
                        ),
                    )
                    .await;
                None
            }
        };
        let policy = self.settings.refresh(host).await;
        let bridge = Arc::new(TransferBridge::start(
            host.clone(),
            row_id.clone(),
            request.operation_id.clone(),
            self.settings.clone(),
            policy,
        ));

        let engine = {
            let bridge = bridge.clone();
            let request = request.clone();
            tokio::task::spawn_blocking(move || {
                // The session stays held for the whole transfer: any other
                // call on it gets the busy text.
                provider.with(|provider| {
                    transfer_entries(
                        provider,
                        &request,
                        bridge.cancel_flag(),
                        bridge.as_ref(),
                        &|| bridge.read_policy(),
                    )
                })
            })
        };
        let result = engine
            .await
            .map_err(|error| format!("remote transfer failed: {error}"))
            .and_then(|result| result);
        let (last_progress, last_tuning) = bridge.finish().await;

        if let Some(row_id) = &row_id {
            let _ = match &result {
                Ok(outcome) if outcome.cancelled => {
                    host.progress_fail(row_id, TRANSFER_CANCELLED).await
                }
                Ok(outcome) if outcome.retry_sources.is_empty() => {
                    host.progress_complete(row_id).await
                }
                Ok(outcome) => {
                    let first = outcome
                        .items
                        .iter()
                        .find_map(|item| item.error.as_deref())
                        .unwrap_or("unknown error");
                    host.progress_fail(
                        row_id,
                        &format!(
                            "{} of {} selections failed: {first}",
                            outcome.retry_sources.len(),
                            request.sources.len()
                        ),
                    )
                    .await
                }
                Err(error) => host.progress_fail(row_id, error).await,
            };
        }
        if let Some(tuning) = last_tuning {
            let _ = host.emit(TUNING_EVENT, json!(tuning)).await;
        }
        if let Some(progress) = last_progress {
            let _ = host.emit(PROGRESS_EVENT, json!(progress)).await;
        }
        to_value(result.map_err(PluginError::Application)?)
    }
}

impl Drop for RemoteHandler {
    fn drop(&mut self) {
        self.shutdown();
    }
}

#[async_trait]
impl PluginHandler for RemoteHandler {
    async fn handle(
        &self,
        method: &str,
        params: Value,
        host: &HostClient,
    ) -> Result<Value, PluginError> {
        self.ensure_setup(host).await;
        match method {
            PROFILES_LIST => {
                let store = self.store.clone();
                to_value(blocking(move || store.list()).await?)
            }
            PROFILES_SAVE => self.save(parse(method, params)?, host).await,
            PROFILES_DELETE => {
                let params: ProfileIdParams = parse(method, params)?;
                self.delete(params.profile_id, host).await
            }
            PROFILES_TEST => self.test(parse(method, params)?).await,
            SESSION_CONNECT => self.connect(parse(method, params)?).await,
            SESSION_DISCONNECT => {
                let params: SessionParams = parse(method, params)?;
                self.disconnect(params.remote_session_id).await
            }
            FS_LIST => {
                let params: ListParams = parse(method, params)?;
                let sessions = self.sessions.clone();
                to_value(
                    blocking(move || {
                        sessions.list_directory(&params.remote_session_id, &params.path)
                    })
                    .await?,
                )
            }
            FS_CREATE_DIRECTORY => {
                let params: CreateDirectoryParams = parse(method, params)?;
                let sessions = self.sessions.clone();
                to_value(
                    blocking(move || {
                        sessions.create_directory(
                            &params.remote_session_id,
                            &params.path,
                            &params.name,
                        )
                    })
                    .await?,
                )
            }
            FS_RENAME => {
                let params: RenameParams = parse(method, params)?;
                let sessions = self.sessions.clone();
                to_value(
                    blocking(move || {
                        sessions.rename_entry(
                            &params.remote_session_id,
                            &params.path,
                            &params.new_name,
                        )
                    })
                    .await?,
                )
            }
            FS_DELETE => {
                let params: DeleteParams = parse(method, params)?;
                let sessions = self.sessions.clone();
                to_value(
                    blocking(move || {
                        sessions.delete_entries(&params.remote_session_id, &params.paths)
                    })
                    .await?,
                )
            }
            TRANSFER_RUN => self.transfer(params, host).await,
            _ => Err(PluginError::MethodNotFound),
        }
    }
}
