//! Loopback SFTP test server for the remote plugin's tests and smokes. It is
//! never staged: no installer or plugin folder carries it.
//!
//! [`TestServer::start`] binds `127.0.0.1:0` with a fresh Ed25519 host key and
//! serves one folder over SFTP to the user `smoke` with a random password.
//! [`legacy::write_legacy_metadata_db`] writes 1.0.4 metadata databases.

pub mod legacy;
mod sftp;

pub use legacy::{write_legacy_metadata_db, LegacyProfile, LEGACY_UPDATED_AT};

use russh::keys::ssh_key::private::Ed25519Keypair;
use russh::keys::{HashAlg, PrivateKey};
use russh::server::{Auth, ChannelOpenHandle, Msg, Session};
use russh::{Channel, ChannelId, Disconnect};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tokio::net::TcpListener;
use tokio::sync::watch;
use tokio::task::JoinHandle;

/// The one user the server accepts.
pub const TEST_SERVER_USER: &str = "smoke";

/// How long [`TestServer::stop`] waits for open connections to close.
const STOP_TIMEOUT: Duration = Duration::from_secs(5);

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TestServerInfo {
    pub port: u16,
    /// `SHA256:<base64>`, the text `sftp.rs` compares.
    pub fingerprint: String,
    pub user: String,
    pub password: String,
    pub root: PathBuf,
    /// Set when the bin wrote a legacy database.
    pub legacy_profile_id: Option<String>,
}

pub struct TestServer {
    info: TestServerInfo,
    shutdown: watch::Sender<bool>,
    listener: Option<JoinHandle<()>>,
}

impl TestServer {
    /// Binds 127.0.0.1:0 with a fresh Ed25519 host key; user "smoke", a random 16-hex password; serves `root` over SFTP only.
    pub async fn start(root: PathBuf) -> Result<TestServer, String> {
        if !root.is_dir() {
            return Err(format!("{} is not a folder", root.display()));
        }
        let mut seed = [0u8; 32];
        getrandom::fill(&mut seed).map_err(|error| error.to_string())?;
        let key = PrivateKey::from(Ed25519Keypair::from_seed(&seed));
        let fingerprint = key.public_key().fingerprint(HashAlg::Sha256).to_string();
        let mut secret = [0u8; 8];
        getrandom::fill(&mut secret).map_err(|error| error.to_string())?;
        let password: String = secret.iter().map(|byte| format!("{byte:02x}")).collect();

        let listener = TcpListener::bind(("127.0.0.1", 0))
            .await
            .map_err(|error| format!("cannot bind the test server: {error}"))?;
        let port = listener
            .local_addr()
            .map_err(|error| error.to_string())?
            .port();
        let config = Arc::new(russh::server::Config {
            keys: vec![key],
            auth_rejection_time: Duration::from_millis(100),
            auth_rejection_time_initial: Some(Duration::ZERO),
            inactivity_timeout: Some(Duration::from_secs(120)),
            nodelay: true,
            ..russh::server::Config::default()
        });
        let info = TestServerInfo {
            port,
            fingerprint,
            user: TEST_SERVER_USER.to_string(),
            password: password.clone(),
            root: root.clone(),
            legacy_profile_id: None,
        };
        let (shutdown, shutdown_rx) = watch::channel(false);
        let listener = tokio::spawn(accept_loop(listener, config, root, password, shutdown_rx));

        Ok(TestServer {
            info,
            shutdown,
            listener: Some(listener),
        })
    }

    pub fn info(&self) -> &TestServerInfo {
        &self.info
    }

    /// Records the legacy profile id the bin wrote (see `--legacy-db`).
    pub fn set_legacy_profile_id(&mut self, id: Option<String>) {
        self.info.legacy_profile_id = id;
    }

    /// Stops accepting, disconnects every client and waits for them.
    pub async fn stop(mut self) {
        let _ = self.shutdown.send(true);
        if let Some(listener) = self.listener.take() {
            let _ = tokio::time::timeout(STOP_TIMEOUT, listener).await;
        }
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        // A server dropped without stop() still stops: the accept loop and
        // every connection watch this flag.
        let _ = self.shutdown.send(true);
    }
}

async fn accept_loop(
    listener: TcpListener,
    config: Arc<russh::server::Config>,
    root: PathBuf,
    password: String,
    mut shutdown: watch::Receiver<bool>,
) {
    let mut connections = tokio::task::JoinSet::new();
    let connection_shutdown = shutdown.clone();
    loop {
        tokio::select! {
            _ = stopped(&mut shutdown) => break,
            accepted = listener.accept() => {
                let Ok((socket, _)) = accepted else { break };
                let _ = socket.set_nodelay(true);
                let handler = SshHandler {
                    root: root.clone(),
                    password: password.clone(),
                    channels: HashMap::new(),
                };
                let config = config.clone();
                let mut shutdown = connection_shutdown.clone();
                connections.spawn(async move {
                    let Ok(session) = russh::server::run_stream(config, socket, handler).await else {
                        return;
                    };
                    let handle = session.handle();
                    tokio::select! {
                        _ = stopped(&mut shutdown) => {
                            let _ = handle
                                .disconnect(Disconnect::ByApplication, "test server stopped".into(), "en".into())
                                .await;
                        }
                        _ = session => {}
                    }
                });
            }
        }
    }
    drop(listener);
    while connections.join_next().await.is_some() {}
}

struct SshHandler {
    root: PathBuf,
    password: String,
    channels: HashMap<ChannelId, Channel<Msg>>,
}

impl russh::server::Handler for SshHandler {
    type Error = russh::Error;

    async fn auth_password(&mut self, user: &str, password: &str) -> Result<Auth, Self::Error> {
        if user == TEST_SERVER_USER && password == self.password {
            Ok(Auth::Accept)
        } else {
            Ok(Auth::reject())
        }
    }

    async fn channel_open_session(
        &mut self,
        channel: Channel<Msg>,
        reply: ChannelOpenHandle,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.channels.insert(channel.id(), channel);
        reply.accept().await;
        Ok(())
    }

    async fn channel_eof(
        &mut self,
        channel: ChannelId,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        session.close(channel)?;
        Ok(())
    }

    async fn subsystem_request(
        &mut self,
        channel_id: ChannelId,
        name: &str,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        match (name, self.channels.remove(&channel_id)) {
            ("sftp", Some(channel)) => {
                session.channel_success(channel_id)?;
                russh_sftp::server::run(
                    channel.into_stream(),
                    sftp::SftpHandler::new(self.root.clone()),
                )
                .await;
            }
            _ => session.channel_failure(channel_id)?,
        }
        Ok(())
    }
}

/// Resolves once the server is told to stop or the server is dropped. The
/// watch guard is released here, so the caller's future stays `Send`.
async fn stopped(shutdown: &mut watch::Receiver<bool>) {
    let _ = shutdown.wait_for(|stop| *stop).await.map(|_| ());
}
