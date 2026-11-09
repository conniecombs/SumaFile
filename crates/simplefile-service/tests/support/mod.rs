//! Shared harness for the service's plugin integration tests: an in-process
//! service that several tasks can drive at once, the engine registry lock, a
//! fake host connection, and staging and launch of the real plugin exes.
#![allow(dead_code)]

use serde_json::{json, Value};
use simplefile_ipc::frame::{decode_length, encode_frame};
use simplefile_ipc::rpc::JsonRpcRequest;
use simplefile_ipc::{HANDSHAKE_METHOD, PROTOCOL_VERSION};
use simplefile_service::{serve_connection, SessionState};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, Once};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::io::{duplex, AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, DuplexStream};
use tokio::io::{ReadHalf, WriteHalf};
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};

/// The engine registry is process-wide, and the tests of one binary share
/// extensions (`.hello`, `.7z`). Without this lock a later registration
/// would route one test's calls to another test's backend, which dies when
/// that test ends. Every test that registers an engine takes it first.
pub static REGISTRY_LOCK: tokio::sync::Mutex<()> = tokio::sync::Mutex::const_new(());

/// How long a test waits for any one service reply before failing.
const REPLY_TIMEOUT: Duration = Duration::from_secs(120);

/// Unregisters the plugin's formats even when an assertion fails.
pub struct Unregister(pub String);

impl Drop for Unregister {
    fn drop(&mut self) {
        simplefile_service::plugin_archive::unregister_plugin_engine(&self.0);
    }
}

pub fn nonce() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("clock after epoch")
        .as_nanos()
}

/// A fresh folder under the system temp directory.
pub fn temp_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "simplefile-plugin-{name}-{}-{}",
        std::process::id(),
        nonce()
    ));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

pub async fn read_message<R: AsyncRead + Unpin>(reader: &mut R) -> Option<Value> {
    let mut header = [0u8; 4];
    reader.read_exact(&mut header).await.ok()?;
    let length = decode_length(header).ok()? as usize;
    let mut payload = vec![0u8; length];
    reader.read_exact(&mut payload).await.ok()?;
    serde_json::from_slice(&payload).ok()
}

pub async fn write_message<W: AsyncWrite + Unpin>(writer: &mut W, message: &Value) {
    let payload = serde_json::to_vec(message).unwrap();
    writer
        .write_all(&encode_frame(&payload).unwrap())
        .await
        .unwrap();
    writer.flush().await.unwrap();
}

/// Point the settings store at a private temp folder, once per process, so
/// `get_db_settings` never touches the user's database.
fn isolate_settings() {
    static ONCE: Once = Once::new();
    ONCE.call_once(|| {
        let dir = temp_dir("settings");
        std::env::set_var("SIMPLEFILE_METADATA_DB", dir.join("metadata.db"));
        std::env::set_var("SIMPLEFILE_APP_DATA_DIR", &dir);
    });
}

struct ServiceInner {
    writer: tokio::sync::Mutex<WriteHalf<DuplexStream>>,
    next_id: AtomicU64,
    replies: Mutex<HashMap<u64, Value>>,
    notifications: Mutex<Vec<Value>>,
    server: Mutex<Option<tokio::task::JoinHandle<Result<(), String>>>>,
}

/// An in-process service session. One reader task routes each reply to its
/// waiter by id and collects every notification, so several tasks (a test
/// and a [`FakeHost`]) can call it at once.
#[derive(Clone)]
pub struct Service {
    inner: Arc<ServiceInner>,
}

impl Service {
    pub async fn start() -> Self {
        isolate_settings();
        let (client, server) = duplex(1 << 20);
        let (server_read, server_write) = tokio::io::split(server);
        let server = tokio::spawn(serve_connection(
            server_read,
            server_write,
            SessionState {
                expected_token: Some("dev".to_string()),
                ..SessionState::default()
            },
        ));
        let (client_read, client_write) = tokio::io::split(client);
        let service = Self {
            inner: Arc::new(ServiceInner {
                writer: tokio::sync::Mutex::new(client_write),
                next_id: AtomicU64::new(0),
                replies: Mutex::new(HashMap::new()),
                notifications: Mutex::new(Vec::new()),
                server: Mutex::new(Some(server)),
            }),
        };
        tokio::spawn(route_messages(client_read, Arc::clone(&service.inner)));
        let handshake = service
            .call(
                HANDSHAKE_METHOD,
                json!({
                    "protocolVersion": PROTOCOL_VERSION,
                    "clientName": "plugin-test",
                    "authToken": "dev"
                }),
            )
            .await;
        assert_eq!(handshake["result"]["protocolVersion"], PROTOCOL_VERSION);
        service
    }

    /// Send a request without waiting; returns its id for [`Service::reply`].
    pub async fn send(&self, method: &str, params: Value) -> u64 {
        let id = self.inner.next_id.fetch_add(1, Ordering::SeqCst) + 1;
        let request = JsonRpcRequest {
            jsonrpc: "2.0".into(),
            id: Some(json!(id)),
            method: method.into(),
            params: Some(params),
        };
        let message = serde_json::to_value(&request).unwrap();
        let mut writer = self.inner.writer.lock().await;
        write_message(&mut *writer, &message).await;
        id
    }

    /// The reply to request `id`, once it has arrived.
    pub async fn reply(&self, id: u64) -> Value {
        let deadline = Instant::now() + REPLY_TIMEOUT;
        loop {
            if let Some(reply) = self.try_reply(id) {
                return reply;
            }
            assert!(
                Instant::now() < deadline,
                "no service reply to request {id} within {REPLY_TIMEOUT:?}"
            );
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    }

    /// The reply to request `id` if it has already arrived.
    pub fn try_reply(&self, id: u64) -> Option<Value> {
        self.inner.replies.lock().unwrap().remove(&id)
    }

    pub async fn call(&self, method: &str, params: Value) -> Value {
        let id = self.send(method, params).await;
        self.reply(id).await
    }

    /// Every `operation-progress` notification for `operation_id`, in order.
    pub fn progress_events(&self, operation_id: &str) -> Vec<Value> {
        self.inner
            .notifications
            .lock()
            .unwrap()
            .iter()
            .filter(|message| message["method"] == "operation-progress")
            .map(|message| message["params"].clone())
            .filter(|params| params["operation_id"] == operation_id)
            .collect()
    }

    pub async fn stop(&self) {
        let _ = self.call("ipc.shutdown", json!({})).await;
        let server = self.inner.server.lock().unwrap().take();
        if let Some(server) = server {
            let _ = server.await;
        }
    }
}

async fn route_messages(mut reader: ReadHalf<DuplexStream>, inner: Arc<ServiceInner>) {
    while let Some(message) = read_message(&mut reader).await {
        let is_reply = message.get("method").is_none()
            && (message.get("result").is_some() || message.get("error").is_some());
        match message.get("id").and_then(Value::as_u64) {
            Some(id) if is_reply => {
                inner.replies.lock().unwrap().insert(id, message);
            }
            _ => inner.notifications.lock().unwrap().push(message),
        }
    }
}

/// `<CARGO_TARGET_TMPDIR>/../<debug|release>/<package>.exe`.
pub fn built_exe(package: &str) -> PathBuf {
    let profile = if cfg!(debug_assertions) {
        "debug"
    } else {
        "release"
    };
    let exe = Path::new(env!("CARGO_TARGET_TMPDIR"))
        .parent()
        .expect("target dir")
        .join(profile)
        .join(format!("{package}.exe"));
    assert!(
        exe.is_file(),
        "{} is missing: build it with `cargo build --locked -p {package}` (the workspace `cargo test` builds it)",
        exe.display()
    );
    exe
}

/// A plugin backend launched from a temp plugin folder. It holds one pipe
/// client open, never handshaken, for its whole lifetime: a backend exits
/// when its last client disconnects, and the pool may drop every connection
/// after an error. In production the host connection plays this role. A
/// client that never handshakes never becomes the backend's host
/// connection, so a backend never waits on settings because of it.
pub struct PluginProcess {
    pub pipe_name: String,
    pub dir: PathBuf,
    child: std::process::Child,
    keep_alive: Option<std::fs::File>,
}

impl Drop for PluginProcess {
    fn drop(&mut self) {
        self.keep_alive = None;
        let _ = self.child.kill();
        let _ = self.child.wait();
        let _ = std::fs::remove_dir_all(&self.dir);
    }
}

/// Copy `exe` and `extra_files` into a temp plugin folder and launch it
/// with `--pipe-name` and `--data-dir`, then wait (retrying a client connect
/// every 10 ms for up to 5 s) until its pipe accepts clients.
pub fn launch_plugin(exe: &Path, extra_files: &[PathBuf]) -> PluginProcess {
    let dir = temp_dir("backend");
    let staged_exe = dir.join(exe.file_name().expect("exe name"));
    std::fs::copy(exe, &staged_exe).expect("stage plugin exe");
    for file in extra_files {
        std::fs::copy(file, dir.join(file.file_name().expect("file name")))
            .expect("stage plugin file");
    }
    let data_dir = dir.join("data");
    std::fs::create_dir_all(&data_dir).unwrap();
    let pipe_name = format!("sumafile-test-plugin-{}-{}", std::process::id(), nonce());
    let child = std::process::Command::new(&staged_exe)
        .arg("--pipe-name")
        .arg(&pipe_name)
        .arg("--data-dir")
        .arg(&data_dir)
        .spawn()
        .expect("launch plugin backend");
    let mut process = PluginProcess {
        pipe_name,
        dir,
        child,
        keep_alive: None,
    };
    let path = format!(r"\\.\pipe\{}", process.pipe_name);
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        match std::fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
        {
            Ok(client) => {
                process.keep_alive = Some(client);
                return process;
            }
            Err(error) => {
                assert!(
                    Instant::now() < deadline,
                    "plugin pipe {path} never appeared: {error}"
                );
                std::thread::sleep(Duration::from_millis(10));
            }
        }
    }
}

/// Acts as the C# host on a backend: it handshakes without `client`, so it
/// becomes the backend's host connection, and it answers each
/// `host.get_settings` the way the host does, with `get_db_settings` for the
/// `plugin.<id>.<key>` names on `service`.
pub struct FakeHost {
    plugin_id: String,
    writer: Arc<tokio::sync::Mutex<WriteHalf<NamedPipeClient>>>,
    replies: Arc<Mutex<HashMap<u64, Value>>>,
    served: Arc<Mutex<Vec<String>>>,
    next_id: AtomicU64,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for FakeHost {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl FakeHost {
    pub async fn connect(pipe_name: &str, service: Service) -> FakeHost {
        let path = format!(r"\\.\pipe\{pipe_name}");
        let deadline = Instant::now() + Duration::from_secs(5);
        let mut client = loop {
            match ClientOptions::new().open(&path) {
                Ok(client) => break client,
                Err(error) => {
                    assert!(Instant::now() < deadline, "cannot reach {path}: {error}");
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            }
        };
        write_message(
            &mut client,
            &json!({
                "jsonrpc": "2.0",
                "id": 1,
                "method": "plugin.handshake",
                "params": {
                    "hostVersion": simplefile_core::APP_DISPLAY_VERSION,
                    "sdkVersion": "1.0.0"
                }
            }),
        )
        .await;
        let handshake = tokio::time::timeout(Duration::from_secs(5), read_message(&mut client))
            .await
            .expect("handshake reply in time")
            .expect("handshake reply");
        let plugin_id = handshake["result"]["pluginId"]
            .as_str()
            .unwrap_or_else(|| panic!("handshake reply without pluginId: {handshake}"))
            .to_string();

        let (reader, writer) = tokio::io::split(client);
        let writer = Arc::new(tokio::sync::Mutex::new(writer));
        let replies = Arc::new(Mutex::new(HashMap::new()));
        let served = Arc::new(Mutex::new(Vec::new()));
        let task = tokio::spawn(serve_host(
            reader,
            Arc::clone(&writer),
            Arc::clone(&replies),
            Arc::clone(&served),
            plugin_id.clone(),
            service,
        ));
        FakeHost {
            plugin_id,
            writer,
            replies,
            served,
            next_id: AtomicU64::new(1),
            task,
        }
    }

    pub fn plugin_id(&self) -> &str {
        &self.plugin_id
    }

    /// Every `host.*` method the backend asked this host for, in order.
    pub fn served(&self) -> Vec<String> {
        self.served.lock().unwrap().clone()
    }

    /// Send a request to the backend as the host and wait for its reply.
    pub async fn request(&self, method: &str, params: Value) -> Value {
        let id = self.next_id.fetch_add(1, Ordering::SeqCst) + 1;
        {
            let mut writer = self.writer.lock().await;
            write_message(
                &mut *writer,
                &json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }),
            )
            .await;
        }
        let deadline = Instant::now() + REPLY_TIMEOUT;
        loop {
            if let Some(reply) = self.replies.lock().unwrap().remove(&id) {
                return reply;
            }
            assert!(Instant::now() < deadline, "no backend reply to {method}");
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    }
}

async fn serve_host(
    mut reader: ReadHalf<NamedPipeClient>,
    writer: Arc<tokio::sync::Mutex<WriteHalf<NamedPipeClient>>>,
    replies: Arc<Mutex<HashMap<u64, Value>>>,
    served: Arc<Mutex<Vec<String>>>,
    plugin_id: String,
    service: Service,
) {
    while let Some(message) = read_message(&mut reader).await {
        let Some(method) = message.get("method").and_then(Value::as_str) else {
            if let Some(id) = message.get("id").and_then(Value::as_u64) {
                replies.lock().unwrap().insert(id, message);
            }
            continue;
        };
        if message.get("id").is_none_or(Value::is_null) {
            continue;
        }
        served.lock().unwrap().push(method.to_string());
        let reply = match method {
            "host.get_settings" => {
                let keys: Vec<String> = message["params"]["keys"]
                    .as_array()
                    .map(|keys| {
                        keys.iter()
                            .filter_map(Value::as_str)
                            .map(str::to_string)
                            .collect()
                    })
                    .unwrap_or_default();
                let names: Vec<String> = keys
                    .iter()
                    .map(|key| format!("plugin.{plugin_id}.{key}"))
                    .collect();
                let stored = service
                    .call("get_db_settings", json!({ "keys": names }))
                    .await;
                let values: serde_json::Map<String, Value> = keys
                    .iter()
                    .zip(&names)
                    .map(|(key, name)| (key.clone(), stored["result"][name].clone()))
                    .collect();
                json!({ "jsonrpc": "2.0", "id": message["id"], "result": { "values": values } })
            }
            "host.log" => json!({ "jsonrpc": "2.0", "id": message["id"], "result": null }),
            other => json!({
                "jsonrpc": "2.0",
                "id": message["id"],
                "error": { "code": -32601, "message": format!("fake host does not serve {other}") }
            }),
        };
        let mut writer = writer.lock().await;
        write_message(&mut *writer, &reply).await;
    }
}
