use super::path::{
    extension_for_name, join_remote_path, normalize_remote_path, parent_remote_path,
    sibling_remote_path, unix_timestamp_to_rfc3339,
};
use super::provider::RemoteProvider;
use super::secrets::RemoteSecret;
use russh::client::{self, Handle};
use russh::keys::{
    agent::client::{AgentClient, AgentStream},
    load_secret_key, HashAlg, PrivateKeyWithHashAlg, PublicKeyOrCertificate,
};
use russh::Disconnect;
use russh_sftp::client::fs::Metadata;
use russh_sftp::client::SftpSession;
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteAuthKind, RemoteProfile};
use std::future::Future;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::AsyncWriteExt;
use tokio::runtime::{Builder, Runtime};

const REMOTE_CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

pub fn connect(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<Box<dyn RemoteProvider>, String> {
    let runtime = Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|error| format!("SFTP runtime setup failed: {error}"))?;
    let profile = profile.clone();
    let secret = secret.cloned();
    let (session, sftp) = block_on_runtime(&runtime, connect_async(profile, secret))?;

    Ok(Box::new(SftpRemoteProvider {
        runtime,
        session,
        sftp,
    }))
}

struct SftpRemoteProvider {
    runtime: Runtime,
    session: Handle<VerifiedHostKeyHandler>,
    sftp: SftpSession,
}

impl RemoteProvider for SftpRemoteProvider {
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
        let path = normalize_remote_path(path);
        let entries = block_on_runtime(&self.runtime, self.sftp.read_dir(path.clone()))
            .map_err(|error| format!("SFTP list failed for {path}: {error}"))?
            .map(|entry| file_entry_from_sftp_metadata(&path, &entry.path(), entry.metadata()))
            .collect();

        Ok(DirectoryListing {
            parent: parent_remote_path(&path),
            path,
            entries,
            is_network: true,
        })
    }

    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
        let target = join_remote_path(path, name);
        block_on_runtime(&self.runtime, self.sftp.create_dir(target.clone()))
            .map_err(|error| format!("SFTP create directory failed for {target}: {error}"))?;
        let metadata = block_on_runtime(&self.runtime, self.sftp.metadata(target.clone()))
            .map_err(|error| format!("SFTP stat failed for {target}: {error}"))?;
        Ok(file_entry_from_sftp_metadata(
            &parent_remote_path(&target).unwrap_or_else(|| "/".to_string()),
            &target,
            metadata,
        ))
    }

    fn rename_entry(&mut self, path: &str, new_name: &str) -> Result<FileEntry, String> {
        let source = normalize_remote_path(path);
        let target = sibling_remote_path(&source, new_name);
        block_on_runtime(
            &self.runtime,
            self.sftp.rename(source.clone(), target.clone()),
        )
        .map_err(|error| format!("SFTP rename failed for {source}: {error}"))?;
        let metadata = block_on_runtime(&self.runtime, self.sftp.metadata(target.clone()))
            .map_err(|error| format!("SFTP stat failed for {target}: {error}"))?;
        Ok(file_entry_from_sftp_metadata(
            &parent_remote_path(&target).unwrap_or_else(|| "/".to_string()),
            &target,
            metadata,
        ))
    }

    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
        let mut deleted = Vec::new();
        for path in paths {
            let normalized = normalize_remote_path(path);
            let metadata = block_on_runtime(&self.runtime, self.sftp.metadata(normalized.clone()))
                .map_err(|error| format!("SFTP stat failed for {normalized}: {error}"))?;
            if metadata.is_dir() {
                block_on_runtime(&self.runtime, self.sftp.remove_dir(normalized.clone())).map_err(
                    |error| format!("SFTP remove directory failed for {normalized}: {error}"),
                )?;
            } else {
                block_on_runtime(&self.runtime, self.sftp.remove_file(normalized.clone()))
                    .map_err(|error| {
                        format!("SFTP delete file failed for {normalized}: {error}")
                    })?;
            }
            deleted.push(normalized);
        }
        Ok(deleted)
    }

    fn read_file(&mut self, path: &str) -> Result<Vec<u8>, String> {
        let path = normalize_remote_path(path);
        block_on_runtime(&self.runtime, self.sftp.read(path.clone()))
            .map_err(|error| format!("SFTP download failed for {path}: {error}"))
    }

    fn write_file(&mut self, path: &str, bytes: &[u8]) -> Result<FileEntry, String> {
        let path = normalize_remote_path(path);
        let data = bytes.to_vec();
        block_on_runtime(&self.runtime, async {
            let mut file = self
                .sftp
                .create(path.clone())
                .await
                .map_err(|error| format!("SFTP upload open failed for {path}: {error}"))?;
            file.write_all(&data)
                .await
                .map_err(|error| format!("SFTP upload write failed for {path}: {error}"))?;
            file.close()
                .await
                .map_err(|error| format!("SFTP upload close failed for {path}: {error}"))
        })?;
        let metadata = block_on_runtime(&self.runtime, self.sftp.metadata(path.clone()))
            .map_err(|error| format!("SFTP stat failed for {path}: {error}"))?;
        Ok(file_entry_from_sftp_metadata(
            &parent_remote_path(&path).unwrap_or_else(|| "/".to_string()),
            &path,
            metadata,
        ))
    }

    fn disconnect(&mut self) -> Result<(), String> {
        block_on_runtime(&self.runtime, async {
            let close_result = self.sftp.close().await;
            let disconnect_result = self
                .session
                .disconnect(Disconnect::ByApplication, "", "English")
                .await;
            close_result.map_err(|error| format!("SFTP close failed: {error}"))?;
            disconnect_result.map_err(|error| format!("SFTP disconnect failed: {error}"))
        })
    }
}

async fn connect_async(
    profile: RemoteProfile,
    secret: Option<RemoteSecret>,
) -> Result<(Handle<VerifiedHostKeyHandler>, SftpSession), String> {
    let config = client::Config {
        inactivity_timeout: Some(REMOTE_CONNECT_TIMEOUT),
        keepalive_interval: Some(Duration::from_secs(10)),
        nodelay: true,
        ..client::Config::default()
    };

    let handler = VerifiedHostKeyHandler {
        expected_fingerprint: profile.trusted_host_fingerprint.clone(),
    };
    let address = (profile.host.clone(), profile.port);
    let mut session = client::connect(Arc::new(config), address, handler)
        .await
        .map_err(format_client_error)?;
    authenticate(&profile, secret.as_ref(), &mut session).await?;

    let channel = session
        .channel_open_session()
        .await
        .map_err(|error| format!("SFTP channel open failed: {error}"))?;
    channel
        .request_subsystem(true, "sftp")
        .await
        .map_err(|error| format!("SFTP subsystem request failed: {error}"))?;
    let sftp = SftpSession::new(channel.into_stream())
        .await
        .map_err(|error| format!("SFTP subsystem setup failed: {error}"))?;
    sftp.set_timeout(15);

    Ok((session, sftp))
}

pub(crate) fn file_entry_from_sftp_metadata(
    parent_path: &str,
    path: &str,
    metadata: Metadata,
) -> FileEntry {
    let name = super::path::remote_file_name(path);
    let is_dir = metadata.is_dir();
    let is_symlink = metadata.is_symlink();

    FileEntry {
        path: join_remote_path(parent_path, &name),
        extension: extension_for_name(&name, is_dir),
        is_hidden: name.starts_with('.'),
        is_system: false,
        size: metadata.len(),
        modified: metadata
            .mtime
            .map(|seconds| unix_timestamp_to_rfc3339(u64::from(seconds)))
            .unwrap_or_default(),
        permissions: metadata.permissions.map(mode_to_permissions),
        symlink_target: None,
        git_status: None,
        name,
        is_dir,
        is_symlink,
    }
}

async fn authenticate(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
    session: &mut Handle<VerifiedHostKeyHandler>,
) -> Result<(), String> {
    match profile.auth_kind {
        RemoteAuthKind::Password => match secret {
            Some(RemoteSecret::Password(password)) if !password.is_empty() => {
                let result = session
                    .authenticate_password(profile.username.clone(), password.clone())
                    .await
                    .map_err(|error| format!("SFTP password authentication failed: {error}"))?;
                if result.success() {
                    Ok(())
                } else {
                    Err("SFTP password authentication was rejected by the server".to_string())
                }
            }
            _ => Err(
                "SFTP password authentication requires a saved or supplied password".to_string(),
            ),
        },
        RemoteAuthKind::PrivateKey => authenticate_with_private_key(profile, secret, session).await,
        RemoteAuthKind::Agent => authenticate_with_agent(profile, session).await,
        RemoteAuthKind::Anonymous => {
            Err("SFTP does not support anonymous authentication".to_string())
        }
    }
}

async fn authenticate_with_private_key(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
    session: &mut Handle<VerifiedHostKeyHandler>,
) -> Result<(), String> {
    let key_path = profile
        .private_key_path
        .as_deref()
        .and_then(non_empty_str)
        .ok_or_else(|| "SFTP private key path cannot be empty".to_string())?;
    let passphrase = match secret {
        Some(RemoteSecret::PrivateKeyPassphrase(passphrase)) if !passphrase.is_empty() => {
            Some(passphrase.as_str())
        }
        Some(RemoteSecret::Password(passphrase)) if !passphrase.is_empty() => {
            Some(passphrase.as_str())
        }
        _ => None,
    };
    let key_pair = load_secret_key(key_path, passphrase)
        .map_err(|error| format!("SFTP private key load failed for {key_path}: {error}"))?;
    let hash_alg = session
        .best_supported_rsa_hash()
        .await
        .map_err(|error| format!("SFTP private key negotiation failed: {error}"))?
        .flatten();

    let result = session
        .authenticate_publickey(
            profile.username.clone(),
            PrivateKeyWithHashAlg::new(Arc::new(key_pair), hash_alg),
        )
        .await
        .map_err(|error| format!("SFTP private key authentication failed: {error}"))?;
    if result.success() {
        Ok(())
    } else {
        Err("SFTP private key authentication was rejected by the server".to_string())
    }
}

async fn authenticate_with_agent(
    profile: &RemoteProfile,
    session: &mut Handle<VerifiedHostKeyHandler>,
) -> Result<(), String> {
    let mut agent = connect_agent_client().await?;
    let identities = agent
        .request_identities()
        .await
        .map_err(|error| format!("SFTP agent identity lookup failed: {error}"))?;
    if identities.is_empty() {
        return Err("SFTP agent has no available identities".to_string());
    }

    let hash_alg = session
        .best_supported_rsa_hash()
        .await
        .map_err(|error| format!("SFTP agent negotiation failed: {error}"))?
        .flatten();
    let mut rejected = 0usize;
    let mut last_error = None;
    for identity in identities {
        let public_key = identity.public_key().into_owned();
        match session
            .authenticate_publickey_with(profile.username.clone(), public_key, hash_alg, &mut agent)
            .await
        {
            Ok(result) if result.success() => return Ok(()),
            Ok(_) => rejected += 1,
            Err(error) => last_error = Some(error.to_string()),
        }
    }

    match last_error {
        Some(error) => Err(format!("SFTP agent authentication failed: {error}")),
        None => Err(format!(
            "SFTP agent authentication was rejected by the server for {rejected} identities"
        )),
    }
}

async fn connect_agent_client() -> Result<AgentClient<Box<dyn AgentStream + Send + Unpin>>, String>
{
    #[cfg(unix)]
    {
        return AgentClient::connect_env()
            .await
            .map(|client| client.dynamic())
            .map_err(|error| format!("SFTP agent connection failed: {error}"));
    }

    #[cfg(windows)]
    {
        let mut errors = Vec::new();
        if let Ok(named_pipe) = std::env::var("SSH_AUTH_SOCK") {
            if !named_pipe.trim().is_empty() {
                match AgentClient::connect_named_pipe(named_pipe.trim()).await {
                    Ok(client) => return Ok(client.dynamic()),
                    Err(error) => errors.push(format!("SSH_AUTH_SOCK: {error}")),
                }
            }
        }

        match AgentClient::connect_named_pipe(r"\\.\pipe\openssh-ssh-agent").await {
            Ok(client) => return Ok(client.dynamic()),
            Err(error) => errors.push(format!("OpenSSH agent pipe: {error}")),
        }

        match AgentClient::connect_pageant().await {
            Ok(client) => return Ok(client.dynamic()),
            Err(error) => errors.push(format!("Pageant: {error}")),
        }

        Err(format!(
            "SFTP agent connection failed: {}",
            errors.join("; ")
        ))
    }

    #[cfg(not(any(unix, windows)))]
    {
        Err("SFTP agent authentication is not supported on this platform".to_string())
    }
}

fn non_empty_str(value: &str) -> Option<&str> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed)
    }
}

#[derive(Clone, Debug)]
struct VerifiedHostKeyHandler {
    expected_fingerprint: Option<String>,
}

impl client::Handler for VerifiedHostKeyHandler {
    type Error = SftpClientError;

    async fn check_server_key(
        &mut self,
        server_public_key: &PublicKeyOrCertificate,
    ) -> Result<bool, Self::Error> {
        let fingerprint = server_public_key
            .public_key()
            .fingerprint(HashAlg::Sha256)
            .to_string();
        let Some(expected) = self.expected_fingerprint.as_deref() else {
            return Err(SftpClientError::HostKey(format!(
                "SFTP host fingerprint is not trusted. Save this fingerprint on the profile and reconnect: {fingerprint}"
            )));
        };

        if normalize_fingerprint(expected) == normalize_fingerprint(&fingerprint) {
            Ok(true)
        } else {
            Err(SftpClientError::HostKey(format!(
                "SFTP host fingerprint mismatch. Expected {expected}, got {fingerprint}"
            )))
        }
    }
}

#[derive(Debug)]
enum SftpClientError {
    Ssh(russh::Error),
    HostKey(String),
}

impl From<russh::Error> for SftpClientError {
    fn from(error: russh::Error) -> Self {
        Self::Ssh(error)
    }
}

fn format_client_error(error: SftpClientError) -> String {
    match error {
        SftpClientError::Ssh(error) => format!("SFTP connection failed: {error}"),
        SftpClientError::HostKey(message) => message,
    }
}

fn block_on_runtime<F>(runtime: &Runtime, future: F) -> F::Output
where
    F: Future,
{
    if tokio::runtime::Handle::try_current().is_ok() {
        tokio::task::block_in_place(|| runtime.block_on(future))
    } else {
        runtime.block_on(future)
    }
}

fn normalize_fingerprint(value: &str) -> String {
    value.trim().replace('=', "").to_ascii_lowercase()
}

fn mode_to_permissions(mode: u32) -> String {
    let mut output = String::with_capacity(9);
    for bit in [
        0o400, 0o200, 0o100, 0o040, 0o020, 0o010, 0o004, 0o002, 0o001,
    ] {
        output.push(match bit {
            0o400 | 0o040 | 0o004 => {
                if mode & bit != 0 {
                    'r'
                } else {
                    '-'
                }
            }
            0o200 | 0o020 | 0o002 => {
                if mode & bit != 0 {
                    'w'
                } else {
                    '-'
                }
            }
            _ => {
                if mode & bit != 0 {
                    'x'
                } else {
                    '-'
                }
            }
        });
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;
    use russh_sftp::protocol::FileAttributes;

    #[test]
    fn converts_sftp_file_metadata_into_file_entry() {
        let entry = file_entry_from_sftp_metadata(
            "/srv",
            "payload.zip",
            FileAttributes {
                size: Some(4096),
                uid: Some(1000),
                user: None,
                gid: Some(1000),
                group: None,
                permissions: Some(0o100644),
                atime: None,
                mtime: Some(1_790_000_000),
            },
        );

        assert_eq!(entry.name, "payload.zip");
        assert_eq!(entry.path, "/srv/payload.zip");
        assert!(!entry.is_dir);
        assert!(!entry.is_symlink);
        assert_eq!(entry.size, 4096);
        assert_eq!(entry.extension, "zip");
        assert!(entry
            .permissions
            .as_deref()
            .unwrap_or_default()
            .ends_with("rw-r--r--"));
        assert!(entry.modified.ends_with('Z'));
    }

    #[test]
    fn converts_sftp_directory_metadata_into_file_entry() {
        let entry = file_entry_from_sftp_metadata(
            "/srv",
            ".cache",
            FileAttributes {
                size: Some(1024),
                uid: None,
                user: None,
                gid: None,
                group: None,
                permissions: Some(0o040755),
                atime: None,
                mtime: None,
            },
        );

        assert_eq!(entry.name, ".cache");
        assert_eq!(entry.path, "/srv/.cache");
        assert!(entry.is_dir);
        assert!(entry.is_hidden);
        assert_eq!(entry.extension, "");
    }
}
