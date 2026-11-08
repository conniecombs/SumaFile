//! `HostClient`: `host.*` calls back to the host, events and cancel flags.
//!
//! The real client sends requests on the connection's pipe and waits for the
//! reply the connection loop routes back by id. With the `test-support`
//! feature, [`HostClient::fake`] gives an in-memory host with the same API.

use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use serde_json::{json, Value};
use simplefile_ipc::rpc::JsonRpcNotification;
use simplefile_ipc::JSONRPC_VERSION;
use tokio::sync::{mpsc, oneshot};

type Reply = Result<Value, String>;

/// How long a `host.*` call waits for the host's reply by default.
pub const DEFAULT_CALL_TIMEOUT: Duration = Duration::from_secs(30);

/// Error every `host.*` call except `log` returns on the service connection.
pub const SERVICE_CONNECTION_ERROR: &str =
    "host services are not available on the service connection";

/// Talks to the host on behalf of one connection. Cheap to clone.
///
/// Each `host.*` call waits at most [`DEFAULT_CALL_TIMEOUT`] (30 s) for the
/// reply and then fails with an error naming the method.
///
/// On the connection of the Rust service (handshake `client` is
/// `"simplefile-service"`), which never answers inbound requests, the client
/// is in service mode: `log` is a local no-op returning `Ok(())`,
/// `is_cancelled` works as usual, and every other `host.*` method and `emit`
/// fail at once with [`SERVICE_CONNECTION_ERROR`] without sending anything.
#[derive(Clone)]
pub struct HostClient {
    inner: Arc<Inner>,
}

enum Inner {
    Pipe(PipeHost),
    #[cfg(feature = "test-support")]
    Fake(FakeHost),
}

pub(crate) struct PipeHost {
    outbound: mpsc::UnboundedSender<Vec<u8>>,
    pending: Mutex<Pending>,
    next_id: AtomicU64,
    cancelled: Mutex<HashSet<String>>,
    service: AtomicBool,
    timeout_ms: AtomicU64,
}

#[derive(Default)]
struct Pending {
    closed: bool,
    waiters: HashMap<u64, oneshot::Sender<Reply>>,
}

#[cfg(feature = "test-support")]
struct FakeHost {
    settings: Mutex<HashMap<String, String>>,
    log: Mutex<Vec<(String, String)>>,
    next_operation: AtomicU64,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

impl HostClient {
    /// A client that writes frames to `outbound`, the connection's writer.
    pub(crate) fn pipe(outbound: mpsc::UnboundedSender<Vec<u8>>) -> HostClient {
        HostClient {
            inner: Arc::new(Inner::Pipe(PipeHost {
                outbound,
                pending: Mutex::new(Pending::default()),
                next_id: AtomicU64::new(0),
                cancelled: Mutex::new(HashSet::new()),
                service: AtomicBool::new(false),
                timeout_ms: AtomicU64::new(DEFAULT_CALL_TIMEOUT.as_millis() as u64),
            })),
        }
    }

    fn pipe_host(&self) -> Option<&PipeHost> {
        match &*self.inner {
            Inner::Pipe(pipe) => Some(pipe),
            #[cfg(feature = "test-support")]
            Inner::Fake(_) => None,
        }
    }

    /// Queues `message` as a frame on the connection. Returns false once the
    /// connection's writer is gone.
    pub(crate) fn send_message(&self, message: &impl serde::Serialize) -> bool {
        let Some(pipe) = self.pipe_host() else {
            return false;
        };
        let Ok(payload) = serde_json::to_vec(message) else {
            return false;
        };
        match simplefile_ipc::frame::encode_frame(&payload) {
            Ok(frame) => pipe.outbound.send(frame).is_ok(),
            Err(_) => false,
        }
    }

    /// Hands a host reply to the `host.*` call waiting on `id`. Unknown ids
    /// are ignored.
    pub(crate) fn complete(&self, id: u64, reply: Reply) {
        if let Some(pipe) = self.pipe_host() {
            if let Some(waiter) = lock(&pipe.pending).waiters.remove(&id) {
                let _ = waiter.send(reply);
            }
        }
    }

    /// Fails every waiting call and every later one: the connection is gone.
    pub(crate) fn close(&self) {
        if let Some(pipe) = self.pipe_host() {
            let mut pending = lock(&pipe.pending);
            pending.closed = true;
            pending.waiters.clear();
        }
    }

    pub(crate) fn mark_cancelled(&self, operation_id: &str) {
        if let Some(pipe) = self.pipe_host() {
            lock(&pipe.cancelled).insert(operation_id.to_string());
        }
    }

    /// Puts this connection's client in service mode (see [`HostClient`]).
    pub(crate) fn set_service_mode(&self) {
        if let Some(pipe) = self.pipe_host() {
            pipe.service.store(true, Ordering::Relaxed);
        }
    }

    fn is_service(&self) -> bool {
        self.pipe_host()
            .is_some_and(|pipe| pipe.service.load(Ordering::Relaxed))
    }

    /// Changes how long `host.*` calls on this connection wait for a reply.
    /// Meant for tests; the default is [`DEFAULT_CALL_TIMEOUT`].
    #[doc(hidden)]
    pub fn set_call_timeout(&self, timeout: Duration) {
        if let Some(pipe) = self.pipe_host() {
            let millis = u64::try_from(timeout.as_millis()).unwrap_or(u64::MAX);
            pipe.timeout_ms.store(millis, Ordering::Relaxed);
        }
    }

    /// True once this connection's client sent `cancel` for `operation_id`.
    pub fn is_cancelled(&self, operation_id: &str) -> bool {
        match &*self.inner {
            Inner::Pipe(pipe) => lock(&pipe.cancelled).contains(operation_id),
            #[cfg(feature = "test-support")]
            Inner::Fake(_) => false,
        }
    }

    async fn call(&self, method: &str, params: Value) -> Reply {
        let Some(pipe) = self.pipe_host() else {
            return Err(format!("{method} is not available"));
        };
        if self.is_service() {
            return Err(SERVICE_CONNECTION_ERROR.to_string());
        }
        let id = pipe.next_id.fetch_add(1, Ordering::Relaxed) + 1;
        let (sender, receiver) = oneshot::channel();
        {
            let mut pending = lock(&pipe.pending);
            if pending.closed {
                return Err("The host connection is closed".to_string());
            }
            pending.waiters.insert(id, sender);
        }
        let request = json!({
            "jsonrpc": JSONRPC_VERSION,
            "id": id,
            "method": method,
            "params": params,
        });
        if !self.send_message(&request) {
            lock(&pipe.pending).waiters.remove(&id);
            return Err("The host connection is closed".to_string());
        }
        let timeout = Duration::from_millis(pipe.timeout_ms.load(Ordering::Relaxed));
        match tokio::time::timeout(timeout, receiver).await {
            Ok(reply) => reply.unwrap_or_else(|_| Err("The host connection is closed".to_string())),
            Err(_) => {
                lock(&pipe.pending).waiters.remove(&id);
                Err(format!(
                    "{method} timed out after {} ms waiting for the host",
                    timeout.as_millis()
                ))
            }
        }
    }

    pub async fn get_settings(
        &self,
        keys: &[&str],
    ) -> Result<HashMap<String, Option<String>>, String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            let settings = lock(&fake.settings);
            return Ok(keys
                .iter()
                .map(|key| (key.to_string(), settings.get(*key).cloned()))
                .collect());
        }
        let result = self
            .call("host.get_settings", json!({ "keys": keys }))
            .await?;
        let values = result.get("values").and_then(Value::as_object);
        Ok(keys
            .iter()
            .map(|key| {
                let value = values
                    .and_then(|values| values.get(*key))
                    .and_then(Value::as_str)
                    .map(str::to_string);
                (key.to_string(), value)
            })
            .collect())
    }

    pub async fn set_setting(&self, key: &str, value: &str) -> Result<(), String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            lock(&fake.settings).insert(key.to_string(), value.to_string());
            return Ok(());
        }
        self.call("host.set_setting", json!({ "key": key, "value": value }))
            .await
            .map(drop)
    }

    pub async fn progress_begin(&self, title: &str) -> Result<String, String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            let next = fake.next_operation.fetch_add(1, Ordering::Relaxed) + 1;
            return Ok(format!("fake-operation-{next}"));
        }
        let result = self
            .call("host.progress_begin", json!({ "title": title }))
            .await?;
        result
            .get("operationId")
            .and_then(Value::as_str)
            .map(str::to_string)
            .ok_or_else(|| "host.progress_begin returned no operationId".to_string())
    }

    pub async fn progress_report(
        &self,
        id: &str,
        current: u64,
        total: u64,
        detail: Option<&str>,
    ) -> Result<(), String> {
        if self.is_fake() {
            return Ok(());
        }
        let mut params = json!({ "operationId": id, "current": current, "total": total });
        if let Some(detail) = detail {
            params["detail"] = json!(detail);
        }
        self.call("host.progress_report", params).await.map(drop)
    }

    pub async fn progress_complete(&self, id: &str) -> Result<(), String> {
        if self.is_fake() {
            return Ok(());
        }
        self.call("host.progress_complete", json!({ "operationId": id }))
            .await
            .map(drop)
    }

    pub async fn progress_fail(&self, id: &str, message: &str) -> Result<(), String> {
        if self.is_fake() {
            return Ok(());
        }
        self.call(
            "host.progress_fail",
            json!({ "operationId": id, "message": message }),
        )
        .await
        .map(drop)
    }

    /// `level` is `info`, `warn` or `error`.
    pub async fn log(&self, level: &str, message: &str) -> Result<(), String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            lock(&fake.log).push((level.to_string(), message.to_string()));
            return Ok(());
        }
        if self.is_service() {
            return Ok(());
        }
        self.call("host.log", json!({ "level": level, "message": message }))
            .await
            .map(drop)
    }

    pub async fn notify(&self, message: &str, is_error: bool) -> Result<(), String> {
        if self.is_fake() {
            return Ok(());
        }
        self.call(
            "host.notify",
            json!({ "message": message, "isError": is_error }),
        )
        .await
        .map(drop)
    }

    /// Sends `event` to the host as a notification; does not wait.
    pub async fn emit(&self, event: &str, params: Value) -> Result<(), String> {
        if self.is_fake() {
            return Ok(());
        }
        if self.is_service() {
            return Err(SERVICE_CONNECTION_ERROR.to_string());
        }
        if self.send_message(&JsonRpcNotification::new(event, params)) {
            Ok(())
        } else {
            Err("The host connection is closed".to_string())
        }
    }

    fn is_fake(&self) -> bool {
        self.pipe_host().is_none()
    }
}

#[cfg(feature = "test-support")]
impl HostClient {
    /// An in-memory host: `get_settings` reads `settings`, `set_setting`
    /// writes it, `log` is recorded, progress ids are synthetic and every
    /// other call succeeds and is dropped.
    pub fn fake(settings: HashMap<String, String>) -> HostClient {
        HostClient {
            inner: Arc::new(Inner::Fake(FakeHost {
                settings: Mutex::new(settings),
                log: Mutex::new(Vec::new()),
                next_operation: AtomicU64::new(0),
            })),
        }
    }

    /// The `(level, message)` pairs logged so far; empty for a real client.
    pub fn fake_log(&self) -> Vec<(String, String)> {
        match &*self.inner {
            Inner::Fake(fake) => lock(&fake.log).clone(),
            Inner::Pipe(_) => Vec::new(),
        }
    }
}
