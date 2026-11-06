use super::path::{
    extension_for_name, join_remote_path, normalize_remote_path, parent_remote_path,
    sibling_remote_path, unix_timestamp_to_rfc3339,
};
use super::provider::RemoteProvider;
use super::secrets::RemoteSecret;
use crate::transfer_reliability::Failure;
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
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::runtime::{Builder, Runtime};

const REMOTE_CONNECT_TIMEOUT: Duration = Duration::from_secs(15);
const SFTP_STREAM_BUFFER_SIZE: usize = 512 * 1024;

pub fn connect(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<Box<dyn RemoteProvider>, String> {
    connect_typed(profile, secret).map_err(String::from)
}

pub fn connect_typed(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<Box<dyn RemoteProvider>, Failure> {
    let runtime = sftp_runtime()?;
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
    fn transfer_buffer_bytes(&self) -> usize {
        SFTP_STREAM_BUFFER_SIZE
    }
    fn stat(&mut self, path: &str) -> Result<Option<FileEntry>, String> {
        use russh_sftp::client::error::Error;
        use russh_sftp::protocol::StatusCode;
        let path = normalize_remote_path(path);
        match block_on_runtime(&self.runtime, self.sftp.symlink_metadata(path.clone())) {
            Ok(metadata) => Ok(Some(file_entry_from_sftp_metadata(
                &parent_remote_path(&path).unwrap_or_else(|| "/".into()),
                &path,
                metadata,
            ))),
            Err(Error::Status(status)) if status.status_code == StatusCode::NoSuchFile => Ok(None),
            Err(error) => Err(format!("SFTP stat failed for {path}: {error}")),
        }
    }

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
            // lstat, not stat: a symlink to a directory is removed as the link
            // it is; following it would try `rmdir` on the link and fail.
            let metadata = block_on_runtime(
                &self.runtime,
                self.sftp.symlink_metadata(normalized.clone()),
            )
            .map_err(|error| format!("SFTP stat failed for {normalized}: {error}"))?;
            if metadata.is_dir() && !metadata.is_symlink() {
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

    fn download(
        &mut self,
        path: &str,
        destination: &mut dyn std::io::Write,
    ) -> Result<u64, crate::remote::provider::RemoteTransferError> {
        block_on_runtime(&self.runtime, async {
            let mut file = self
                .sftp
                .open(normalize_remote_path(path))
                .await
                .map_err(sftp_failure)?;
            let result = async {
                let mut buffer = vec![0; SFTP_STREAM_BUFFER_SIZE];
                let mut count = 0;
                loop {
                    // Call write directly: write_all skips the writer for empty buffers.
                    destination.write(&[]).map_err(|e| sftp_io(path, e))?;
                    let read = tokio::time::timeout(REMOTE_CONNECT_TIMEOUT, file.read(&mut buffer))
                        .await
                        .map_err(|_| Failure::new("SFTP read timed out", true))?
                        .map_err(|e| sftp_io(path, e))?;
                    if read == 0 {
                        break;
                    }
                    destination
                        .write_all(&buffer[..read])
                        .map_err(|e| sftp_io(path, e))?;
                    count += read as u64;
                }
                Ok(count)
            }
            .await;
            let closed = file.close().await.map_err(|e| sftp_io(path, e));
            result.and_then(|count| closed.map(|()| count))
        })
    }

    fn upload(
        &mut self,
        path: &str,
        source: &mut dyn std::io::Read,
    ) -> Result<u64, crate::remote::provider::RemoteTransferError> {
        block_on_runtime(&self.runtime, async {
            let mut file = self
                .sftp
                .create(normalize_remote_path(path))
                .await
                .map_err(sftp_failure)?;
            let result = async {
                let mut buffer = vec![0; SFTP_STREAM_BUFFER_SIZE];
                let mut count = 0;
                loop {
                    let read = source.read(&mut buffer).map_err(|e| sftp_io(path, e))?;
                    if read == 0 {
                        break;
                    }
                    tokio::time::timeout(REMOTE_CONNECT_TIMEOUT, file.write_all(&buffer[..read]))
                        .await
                        .map_err(|_| Failure::new("SFTP write timed out", true))?
                        .map_err(|e| sftp_io(path, e))?;
                    count += read as u64;
                }
                Ok(count)
            }
            .await;
            // Close on failure as well, before the caller tries to remove staging.
            let closed = file.close().await.map_err(|e| sftp_io(path, e));
            result.and_then(|count| closed.map(|()| count))
        })
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
) -> Result<(Handle<VerifiedHostKeyHandler>, SftpSession), Failure> {
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
        .map_err(|err| classify_client_error(&profile.host, err))?;
    authenticate(&profile, secret.as_ref(), &mut session)
        .await
        .map_err(|msg| Failure::new(msg, false))?;

    let channel = session
        .channel_open_session()
        .await
        .map_err(|error| Failure::new(format!("SFTP channel open failed: {error}"), true))?;
    channel
        .request_subsystem(true, "sftp")
        .await
        .map_err(|error| Failure::new(format!("SFTP subsystem request failed: {error}"), true))?;
    let sftp = SftpSession::new(channel.into_stream())
        .await
        .map_err(|error| Failure::new(format!("SFTP subsystem setup failed: {error}"), true))?;
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

fn classify_client_error(host: &str, error: SftpClientError) -> Failure {
    match error {
        SftpClientError::HostKey(message) => Failure::new(message, false),
        SftpClientError::Ssh(russh::Error::IO(io_err)) => {
            let message = format!("SFTP connection failed: {io_err}");
            let retryable = Failure::io("SFTP connection", host, io_err).retryable();
            Failure::new(message, retryable)
        }
        SftpClientError::Ssh(error) => {
            let retryable = matches!(
                error,
                russh::Error::Elapsed(_)
                    | russh::Error::InactivityTimeout
                    | russh::Error::Disconnect
                    | russh::Error::HUP
            );
            Failure::new(format!("SFTP connection failed: {error}"), retryable)
        }
    }
}

/// The provider's private runtime. It owns a worker thread of its own, so the
/// russh session task (keepalives, inactivity timer, server-side liveness
/// probes) keeps running between RPCs. A `current_thread` runtime only made
/// progress inside `block_on`, which let the 15 s inactivity timer expire
/// while the user was idle and tear the session down on the next click.
fn sftp_runtime() -> Result<Runtime, Failure> {
    Builder::new_multi_thread()
        .worker_threads(1)
        .thread_name("sumafile-sftp")
        .enable_all()
        .build()
        .map_err(|error| Failure::new(format!("SFTP runtime setup failed: {error}"), false))
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

fn sftp_failure(error: russh_sftp::client::error::Error) -> Failure {
    use russh_sftp::client::error::Error;
    use russh_sftp::protocol::StatusCode;
    let retryable = matches!(&error, Error::Timeout | Error::IO(_) | Error::Limited(_))
        || matches!(&error, Error::Status(status) if matches!(status.status_code, StatusCode::NoConnection | StatusCode::ConnectionLost));
    Failure::new(error.to_string(), retryable)
}
fn sftp_io(path: &str, error: std::io::Error) -> Failure {
    if let Some(remote) = error
        .get_ref()
        .and_then(|e| e.downcast_ref::<russh_sftp::client::error::Error>())
    {
        return sftp_failure(remote.clone()).context("SFTP stream", path);
    }
    Failure::io("SFTP stream", path, error)
}

#[cfg(test)]
mod tests {
    use super::*;
    use russh_sftp::protocol::FileAttributes;

    #[test]
    fn sftp_stream_errors_preserve_permission_vs_connection_classification() {
        use russh_sftp::protocol::{Status, StatusCode};
        for (code, retryable) in [
            (StatusCode::PermissionDenied, false),
            (StatusCode::NoSuchFile, false),
            (StatusCode::ConnectionLost, true),
        ] {
            let error = russh_sftp::client::error::Error::Status(Status {
                id: 1,
                status_code: code,
                error_message: "server detail".into(),
                language_tag: "en".into(),
            });
            let failure = sftp_io("/photo.jpg", std::io::Error::from(error));
            assert_eq!(failure.retryable(), retryable);
            assert!(failure.message.contains("/photo.jpg"));
            assert!(failure.message.contains("server detail"));
        }
    }

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

#[cfg(test)]
mod runtime_tests {
    use super::sftp_runtime;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::Arc;
    use std::time::Duration;

    /// russh drives keepalives and the inactivity timer as a spawned task. It
    /// must keep running between RPCs, i.e. while nothing calls `block_on`,
    /// or an idle session is torn down the moment the user clicks again.
    #[test]
    fn spawned_tasks_progress_while_no_call_is_blocking() {
        let runtime = sftp_runtime().expect("runtime");
        let ticked = Arc::new(AtomicBool::new(false));
        let flag = ticked.clone();
        runtime.spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            flag.store(true, Ordering::SeqCst);
        });
        std::thread::sleep(Duration::from_millis(400));
        assert!(
            ticked.load(Ordering::SeqCst),
            "background task did not run without block_on"
        );
    }
}
