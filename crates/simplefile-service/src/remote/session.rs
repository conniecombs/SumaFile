use super::provider::{RemoteProvider, RemoteProviderFactory};
use super::secrets::RemoteSecret;
use serde::{Deserialize, Serialize};
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteProfile, RemoteProtocol};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
pub struct RemoteConnectionTestResult {
    pub ok: bool,
    pub message: String,
    pub capabilities: Vec<String>,
}

impl RemoteConnectionTestResult {
    pub fn valid_profile() -> Self {
        Self {
            ok: true,
            message: "Profile settings are valid. Use Connect to open a live remote session."
                .to_string(),
            capabilities: vec![
                "profiles".to_string(),
                "credential-target".to_string(),
                "ftp".to_string(),
                "ftps".to_string(),
                "sftp".to_string(),
            ],
        }
    }

    pub fn connection_succeeded() -> Self {
        Self {
            ok: true,
            message:
                "Connection succeeded. The profile can browse, mutate, and transfer remote files."
                    .to_string(),
            capabilities: vec![
                "browse".to_string(),
                "mutate".to_string(),
                "transfer".to_string(),
            ],
        }
    }

    pub fn connection_failed(message: String) -> Self {
        Self {
            ok: false,
            message,
            capabilities: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct RemoteSession {
    pub session_id: String,
    pub profile_id: String,
    pub protocol: RemoteProtocol,
    pub root_path: String,
}

pub struct RemoteSessionRegistry {
    factory: Box<dyn RemoteProviderFactory>,
    sessions: HashMap<String, RemoteSessionHandle>,
    next_session_number: u64,
}

struct RemoteSessionHandle {
    session: RemoteSession,
    provider: SharedRemoteProvider,
}

impl RemoteSessionRegistry {
    pub fn new(factory: impl RemoteProviderFactory + 'static) -> Self {
        Self {
            factory: Box::new(factory),
            sessions: HashMap::new(),
            next_session_number: 0,
        }
    }

    pub fn connect_profile(
        &mut self,
        profile: RemoteProfile,
        secret: Option<RemoteSecret>,
    ) -> Result<RemoteSession, String> {
        let provider = self.factory.connect(&profile, secret.as_ref())?;
        self.next_session_number += 1;
        let session = RemoteSession {
            session_id: format!("remote-session-{}", self.next_session_number),
            profile_id: profile.id,
            protocol: profile.protocol,
            root_path: profile.root_path,
        };

        self.sessions.insert(
            session.session_id.clone(),
            RemoteSessionHandle {
                session: session.clone(),
                provider: SharedRemoteProvider(Arc::new(Mutex::new(provider))),
            },
        );

        Ok(session)
    }

    pub fn test_profile(
        &self,
        profile: RemoteProfile,
        secret: Option<RemoteSecret>,
    ) -> RemoteConnectionTestResult {
        match self.factory.connect(&profile, secret.as_ref()) {
            Ok(mut provider) => match provider.disconnect() {
                Ok(()) => RemoteConnectionTestResult::connection_succeeded(),
                Err(message) => RemoteConnectionTestResult::connection_failed(message),
            },
            Err(message) => RemoteConnectionTestResult::connection_failed(message),
        }
    }

    pub fn list_directory(
        &mut self,
        session_id: &str,
        path: &str,
    ) -> Result<DirectoryListing, String> {
        let handle = self
            .sessions
            .get_mut(session_id)
            .ok_or_else(|| format!("remote session not found: {session_id}"))?;
        handle
            .provider
            .with(|provider| provider.list_directory(path))
    }

    pub fn create_directory(
        &mut self,
        session_id: &str,
        path: &str,
        name: &str,
    ) -> Result<FileEntry, String> {
        let handle = self
            .sessions
            .get_mut(session_id)
            .ok_or_else(|| format!("remote session not found: {session_id}"))?;
        handle
            .provider
            .with(|provider| provider.create_directory(path, name))
    }

    pub fn rename_entry(
        &mut self,
        session_id: &str,
        path: &str,
        new_name: &str,
    ) -> Result<FileEntry, String> {
        let handle = self
            .sessions
            .get_mut(session_id)
            .ok_or_else(|| format!("remote session not found: {session_id}"))?;
        handle
            .provider
            .with(|provider| provider.rename_entry(path, new_name))
    }

    pub fn delete_entries(
        &mut self,
        session_id: &str,
        paths: &[String],
    ) -> Result<Vec<String>, String> {
        let handle = self
            .sessions
            .get_mut(session_id)
            .ok_or_else(|| format!("remote session not found: {session_id}"))?;
        handle
            .provider
            .with(|provider| provider.delete_entries(paths))
    }

    pub fn provider(&self, session_id: &str) -> Result<SharedRemoteProvider, String> {
        self.sessions
            .get(session_id)
            .map(|handle| handle.provider.clone())
            .ok_or_else(|| format!("remote session not found: {session_id}"))
    }

    pub fn disconnect(&mut self, session_id: &str) -> Result<(), String> {
        self.provider(session_id)?.with(|provider| {
            // A lost connection must still be removable so the user can reconnect
            // and retry. Failure to acquire the busy-session lock still propagates.
            if let Err(error) = provider.disconnect() {
                log::warn!("Remote disconnect cleanup failed for {session_id}: {error}");
            }
            Ok(())
        })?;
        self.sessions.remove(session_id);
        Ok(())
    }

    pub fn get(&self, session_id: &str) -> Option<&RemoteSession> {
        self.sessions.get(session_id).map(|handle| &handle.session)
    }
}

#[derive(Clone)]
pub struct SharedRemoteProvider(Arc<Mutex<Box<dyn RemoteProvider>>>);

impl std::fmt::Debug for SharedRemoteProvider {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("SharedRemoteProvider")
    }
}

impl SharedRemoteProvider {
    pub fn with<T>(
        &self,
        action: impl FnOnce(&mut dyn RemoteProvider) -> Result<T, String>,
    ) -> Result<T, String> {
        // Never block the IPC reader behind a transfer: it must still accept Cancel.
        let mut provider = self
            .0
            .try_lock()
            .map_err(|_| "Remote session is busy; wait for or cancel its transfer".to_string())?;
        action(provider.as_mut())
    }
}
