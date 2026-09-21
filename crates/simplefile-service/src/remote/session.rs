use super::provider::{RemoteProvider, RemoteProviderFactory};
use super::secrets::RemoteSecret;
use serde::{Deserialize, Serialize};
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteProfile, RemoteProtocol};
use std::collections::HashMap;

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
    provider: Box<dyn RemoteProvider>,
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
                provider,
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
        handle.provider.list_directory(path)
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
        handle.provider.create_directory(path, name)
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
        handle.provider.rename_entry(path, new_name)
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
        handle.provider.delete_entries(paths)
    }

    pub fn read_file(&mut self, session_id: &str, path: &str) -> Result<Vec<u8>, String> {
        let handle = self
            .sessions
            .get_mut(session_id)
            .ok_or_else(|| format!("remote session not found: {session_id}"))?;
        handle.provider.read_file(path)
    }

    pub fn write_file(
        &mut self,
        session_id: &str,
        path: &str,
        bytes: &[u8],
    ) -> Result<FileEntry, String> {
        let handle = self
            .sessions
            .get_mut(session_id)
            .ok_or_else(|| format!("remote session not found: {session_id}"))?;
        handle.provider.write_file(path, bytes)
    }

    pub fn disconnect(&mut self, session_id: &str) -> Result<(), String> {
        let mut handle = self
            .sessions
            .remove(session_id)
            .ok_or_else(|| format!("remote session not found: {session_id}"))?;
        handle.provider.disconnect()
    }

    pub fn get(&self, session_id: &str) -> Option<&RemoteSession> {
        self.sessions.get(session_id).map(|handle| &handle.session)
    }
}
