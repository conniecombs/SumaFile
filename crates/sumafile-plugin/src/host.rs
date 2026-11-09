//! `HostClient`: `host.*` calls back to the host, events and cancel flags.
//!
//! The real client sends requests on the connection's pipe and waits for the
//! reply the connection loop routes back by id. With the `test-support`
//! feature, [`HostClient::fake`] gives an in-memory host with the same API.
//!
//! Cancel flags and the live host connection are backend-wide: every
//! connection's client shares one [`BackendState`], which the server owns.
//! An operation is in flight, and keeps its cancel flag, while a request
//! carrying its `operationId` runs, or from the `host.progress_begin` that
//! returned the id until `progress_complete`, `progress_fail` or the close of
//! the connection that began it.

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, Weak};
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

/// Notification a backend sends while an archive operation runs.
pub const ARCHIVE_PROGRESS_NOTIFICATION: &str = "archive.progress";

/// How many cancels the server keeps for ids that are not in flight: unknown
/// ids, finished ones, and requests not yet read on another connection.
pub const EARLY_CANCEL_LIMIT: usize = 64;

/// State every connection of one backend shares: the cancel flags of the
/// operations in flight (requests carrying an `operationId`, and progress
/// operations begun with `host.progress_begin`), the most recent early
/// cancels and the live host connection. It never holds more than the
/// in-flight ids plus [`EARLY_CANCEL_LIMIT`].
#[derive(Default)]
pub(crate) struct BackendState {
    inner: Mutex<StateInner>,
}

#[derive(Default)]
struct StateInner {
    in_flight: HashMap<String, InFlight>,
    early: VecDeque<String>,
    host: Option<Weak<Inner>>,
}

struct InFlight {
    /// Requests and progress operations holding the id.
    requests: usize,
    cancelled: bool,
}

impl BackendState {
    /// Records a request or progress operation carrying `operation_id` as in
    /// flight. It starts cancelled when a cancel for that id arrived early.
    pub(crate) fn begin(&self, operation_id: &str) {
        let mut state = lock(&self.inner);
        let early = match state.early.iter().position(|id| id == operation_id) {
            Some(index) => state.early.remove(index).is_some(),
            None => false,
        };
        let entry = state
            .in_flight
            .entry(operation_id.to_string())
            .or_insert(InFlight {
                requests: 0,
                cancelled: false,
            });
        entry.requests += 1;
        entry.cancelled |= early;
    }

    /// Drops one holder of the id, and the id's flag once no holder is left.
    pub(crate) fn finish(&self, operation_id: &str) {
        let mut state = lock(&self.inner);
        if let Some(entry) = state.in_flight.get_mut(operation_id) {
            entry.requests -= 1;
            if entry.requests == 0 {
                state.in_flight.remove(operation_id);
            }
        }
    }

    /// Sets the flag of an in-flight id, or keeps the cancel among the most
    /// recent early cancels.
    pub(crate) fn cancel(&self, operation_id: &str) {
        let mut state = lock(&self.inner);
        if let Some(entry) = state.in_flight.get_mut(operation_id) {
            entry.cancelled = true;
            return;
        }
        if let Some(index) = state.early.iter().position(|id| id == operation_id) {
            state.early.remove(index);
        }
        state.early.push_back(operation_id.to_string());
        while state.early.len() > EARLY_CANCEL_LIMIT {
            state.early.pop_front();
        }
    }

    /// The flag of an in-flight id. An id that is not in flight is never
    /// cancelled, even while a cancel for it waits on the early list.
    fn is_cancelled(&self, operation_id: &str) -> bool {
        lock(&self.inner)
            .in_flight
            .get(operation_id)
            .is_some_and(|entry| entry.cancelled)
    }

    fn count(&self) -> usize {
        let state = lock(&self.inner);
        state.in_flight.len() + state.early.len()
    }

    fn host(&self) -> Option<HostClient> {
        lock(&self.inner)
            .host
            .as_ref()
            .and_then(Weak::upgrade)
            .map(|inner| HostClient { inner })
    }

    fn set_host(&self, client: &HostClient) {
        lock(&self.inner).host = Some(Arc::downgrade(&client.inner));
    }

    fn clear_host(&self, client: &HostClient) {
        let mut state = lock(&self.inner);
        if state
            .host
            .as_ref()
            .is_some_and(|host| Weak::ptr_eq(host, &Arc::downgrade(&client.inner)))
        {
            state.host = None;
        }
    }
}

/// Talks to the host on behalf of one connection. Cheap to clone.
///
/// Each `host.*` call waits at most [`DEFAULT_CALL_TIMEOUT`] (30 s) for the
/// reply and then fails with an error naming the method.
///
/// On the connection of the Rust service (handshake `client` is
/// `"simplefile-service"`), which never answers inbound requests, the client
/// is in service mode: `log` is a local no-op returning `Ok(())`,
/// `is_cancelled` and `archive_progress` work as usual, and every other
/// `host.*` method and `emit` fail at once with [`SERVICE_CONNECTION_ERROR`]
/// without sending anything. A handler on the service connection reaches the
/// host through [`HostClient::host_connection`].
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
    state: Arc<BackendState>,
    /// Ids this connection's `progress_begin` returned that are not yet
    /// completed or failed; each holds its id in flight.
    progress: Mutex<HashSet<String>>,
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
    cancelled: Mutex<HashSet<String>>,
    progress: Mutex<Vec<FakeProgress>>,
    progress_calls: Mutex<Vec<String>>,
    events: Mutex<Vec<(String, Value)>>,
}

#[cfg(feature = "test-support")]
type FakeProgress = (String, u64, u64, Option<String>);

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

impl HostClient {
    /// A client that writes frames to `outbound`, the connection's writer,
    /// and shares `state` with every other connection of the backend.
    pub(crate) fn pipe(
        outbound: mpsc::UnboundedSender<Vec<u8>>,
        state: Arc<BackendState>,
    ) -> HostClient {
        HostClient {
            inner: Arc::new(Inner::Pipe(PipeHost {
                outbound,
                pending: Mutex::new(Pending::default()),
                next_id: AtomicU64::new(0),
                state,
                progress: Mutex::new(HashSet::new()),
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
    /// It stops being the backend's host connection, and the progress
    /// operations it began are no longer in flight.
    pub(crate) fn close(&self) {
        if let Some(pipe) = self.pipe_host() {
            {
                let mut pending = lock(&pipe.pending);
                pending.closed = true;
                pending.waiters.clear();
            }
            pipe.state.clear_host(self);
            let operations: Vec<String> = lock(&pipe.progress).drain().collect();
            for operation_id in operations {
                pipe.state.finish(&operation_id);
            }
        }
    }

    /// Holds a progress operation's id in flight, so a `cancel` for it sets
    /// a flag that no number of other cancels evicts.
    ///
    /// Returns false, and holds nothing, once [`HostClient::close`] has run.
    /// It reads `closed` while it holds the `progress` lock, and `close` sets
    /// `closed` before it drains `progress`, so a begin either sees the close
    /// or is drained by it; no id stays in flight after its connection
    /// closed.
    fn begin_progress(&self, operation_id: &str) -> bool {
        let Some(pipe) = self.pipe_host() else {
            return true;
        };
        let mut progress = lock(&pipe.progress);
        if lock(&pipe.pending).closed {
            return false;
        }
        if progress.insert(operation_id.to_string()) {
            pipe.state.begin(operation_id);
        }
        true
    }

    /// Ends a progress operation this connection began; other ids are
    /// ignored.
    fn end_progress(&self, operation_id: &str) {
        if let Some(pipe) = self.pipe_host() {
            if lock(&pipe.progress).remove(operation_id) {
                pipe.state.finish(operation_id);
            }
        }
    }

    /// Puts this connection's client in service mode (see [`HostClient`]).
    pub(crate) fn set_service_mode(&self) {
        if let Some(pipe) = self.pipe_host() {
            pipe.service.store(true, Ordering::Relaxed);
            pipe.state.clear_host(self);
        }
    }

    /// Makes this connection, handshaken by the host, the one
    /// [`HostClient::host_connection`] returns.
    pub(crate) fn set_host_connection(&self) {
        if let Some(pipe) = self.pipe_host() {
            pipe.state.set_host(self);
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

    /// True once any client, on any connection of this backend, sent `cancel`
    /// for `operation_id` while it is in flight.
    ///
    /// An id is in flight while a request carrying it as `operationId` runs
    /// (the server drops it when the handler returns), and, for an id that
    /// [`HostClient::progress_begin`] returned, until
    /// [`HostClient::progress_complete`] or [`HostClient::progress_fail`] is
    /// called for it or the connection that began it closes. A cancel that
    /// arrives while the id is not in flight is kept among the
    /// [`EARLY_CANCEL_LIMIT`] most recent early cancels and makes the id
    /// start cancelled when it next goes in flight; until then this returns
    /// false.
    pub fn is_cancelled(&self, operation_id: &str) -> bool {
        match &*self.inner {
            Inner::Pipe(pipe) => pipe.state.is_cancelled(operation_id),
            #[cfg(feature = "test-support")]
            Inner::Fake(fake) => lock(&fake.cancelled).contains(operation_id),
        }
    }

    /// Cancel flags plus early cancels the server holds right now; for tests.
    #[doc(hidden)]
    pub fn cancel_flag_count(&self) -> usize {
        match &*self.inner {
            Inner::Pipe(pipe) => pipe.state.count(),
            #[cfg(feature = "test-support")]
            Inner::Fake(fake) => lock(&fake.cancelled).len(),
        }
    }

    /// The client of the connected host: the latest connection whose
    /// handshake did not carry `client: "simplefile-service"` and is still
    /// open. A handler serving the service connection uses it to read
    /// settings. `None` while no host is connected.
    pub fn host_connection(&self) -> Option<HostClient> {
        match &*self.inner {
            Inner::Pipe(pipe) => pipe.state.host(),
            #[cfg(feature = "test-support")]
            Inner::Fake(_) => Some(self.clone()),
        }
    }

    /// Queues `archive.progress {operationId, current, total, currentItem?}`
    /// to this connection's client. Allowed on the service connection; never
    /// waits. `total` 0 means unknown.
    pub fn archive_progress(
        &self,
        operation_id: &str,
        current: u64,
        total: u64,
        current_item: Option<&str>,
    ) -> Result<(), String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            lock(&fake.progress).push((
                operation_id.to_string(),
                current,
                total,
                current_item.map(str::to_string),
            ));
            return Ok(());
        }
        let mut params = json!({ "operationId": operation_id, "current": current, "total": total });
        if let Some(item) = current_item {
            params["currentItem"] = json!(item);
        }
        if self.send_message(&JsonRpcNotification::new(
            ARCHIVE_PROGRESS_NOTIFICATION,
            params,
        )) {
            Ok(())
        } else {
            Err("The connection is closed".to_string())
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

    /// Opens a progress row on the host and returns its id. The id is in
    /// flight (see [`HostClient::is_cancelled`]) until `progress_complete` or
    /// `progress_fail` is called for it on this client or the connection
    /// closes, so the host's `cancel` for it, sent when the user cancels the
    /// row, is seen however many other cancels arrive meanwhile. Fails with
    /// `The host connection is closed` when the connection closed before the
    /// reply was taken, so no id is held for a connection that is gone.
    pub async fn progress_begin(&self, title: &str) -> Result<String, String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            let next = fake.next_operation.fetch_add(1, Ordering::Relaxed) + 1;
            lock(&fake.progress_calls).push(format!("begin {title}"));
            return Ok(format!("fake-operation-{next}"));
        }
        let result = self
            .call("host.progress_begin", json!({ "title": title }))
            .await?;
        let operation_id = result
            .get("operationId")
            .and_then(Value::as_str)
            .map(str::to_string)
            .ok_or_else(|| "host.progress_begin returned no operationId".to_string())?;
        if !self.begin_progress(&operation_id) {
            return Err("The host connection is closed".to_string());
        }
        Ok(operation_id)
    }

    pub async fn progress_report(
        &self,
        id: &str,
        current: u64,
        total: u64,
        detail: Option<&str>,
    ) -> Result<(), String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            let call = match detail {
                Some(detail) => format!("report {id} {current}/{total} {detail}"),
                None => format!("report {id} {current}/{total}"),
            };
            lock(&fake.progress_calls).push(call);
            return Ok(());
        }
        if self.is_fake() {
            return Ok(());
        }
        let mut params = json!({ "operationId": id, "current": current, "total": total });
        if let Some(detail) = detail {
            params["detail"] = json!(detail);
        }
        self.call("host.progress_report", params).await.map(drop)
    }

    /// Closes the progress row; `id` stops being in flight whatever the
    /// host answers.
    pub async fn progress_complete(&self, id: &str) -> Result<(), String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            lock(&fake.progress_calls).push(format!("complete {id}"));
            return Ok(());
        }
        if self.is_fake() {
            return Ok(());
        }
        let reply = self
            .call("host.progress_complete", json!({ "operationId": id }))
            .await;
        self.end_progress(id);
        reply.map(drop)
    }

    /// Fails the progress row; `id` stops being in flight whatever the host
    /// answers.
    pub async fn progress_fail(&self, id: &str, message: &str) -> Result<(), String> {
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            lock(&fake.progress_calls).push(format!("fail {id} {message}"));
            return Ok(());
        }
        if self.is_fake() {
            return Ok(());
        }
        let reply = self
            .call(
                "host.progress_fail",
                json!({ "operationId": id, "message": message }),
            )
            .await;
        self.end_progress(id);
        reply.map(drop)
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
        #[cfg(feature = "test-support")]
        if let Inner::Fake(fake) = &*self.inner {
            lock(&fake.events).push((event.to_string(), params));
            return Ok(());
        }
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
                cancelled: Mutex::new(HashSet::new()),
                progress: Mutex::new(Vec::new()),
                progress_calls: Mutex::new(Vec::new()),
                events: Mutex::new(Vec::new()),
            })),
        }
    }

    /// Marks `operation_id` cancelled, as a client's `cancel` would.
    /// Does nothing on a real client.
    pub fn fake_cancel(&self, operation_id: &str) {
        if let Inner::Fake(fake) = &*self.inner {
            lock(&fake.cancelled).insert(operation_id.to_string());
        }
    }

    /// The `(operationId, current, total, currentItem)` of every
    /// `archive_progress` so far; empty for a real client.
    pub fn fake_progress(&self) -> Vec<(String, u64, u64, Option<String>)> {
        match &*self.inner {
            Inner::Fake(fake) => lock(&fake.progress).clone(),
            Inner::Pipe(_) => Vec::new(),
        }
    }

    /// Every `host.progress_*` call so far, in call order: `"begin <title>"`,
    /// `"report <id> <current>/<total> <detail>"` (no detail part without
    /// one), `"complete <id>"` and `"fail <id> <message>"`; empty for a real
    /// client.
    pub fn fake_progress_calls(&self) -> Vec<String> {
        match &*self.inner {
            Inner::Fake(fake) => lock(&fake.progress_calls).clone(),
            Inner::Pipe(_) => Vec::new(),
        }
    }

    /// Every `emit(event, params)` so far; empty for a real client.
    pub fn fake_events(&self) -> Vec<(String, Value)> {
        match &*self.inner {
            Inner::Fake(fake) => lock(&fake.events).clone(),
            Inner::Pipe(_) => Vec::new(),
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

#[cfg(test)]
mod tests {
    use super::*;

    fn pipe_client() -> (HostClient, mpsc::UnboundedReceiver<Vec<u8>>) {
        let (tx, rx) = mpsc::unbounded_channel();
        let state = Arc::new(BackendState::default());
        (HostClient::pipe(tx, state), rx)
    }

    /// The id of the request in one frame the client queued.
    fn request_id(frame: &[u8]) -> u64 {
        let header: [u8; 4] = frame[..4].try_into().expect("frame header");
        let length = simplefile_ipc::frame::decode_length(header).expect("frame length") as usize;
        let request: Value = serde_json::from_slice(&frame[4..4 + length]).expect("request json");
        assert_eq!(request["method"], "host.progress_begin");
        request["id"].as_u64().expect("request id")
    }

    #[test]
    fn begin_progress_after_close_holds_nothing_in_flight() {
        let (client, _rx) = pipe_client();
        client.close();

        assert!(!client.begin_progress("op-1"));
        assert_eq!(client.cancel_flag_count(), 0);
    }

    #[tokio::test(flavor = "current_thread")]
    async fn progress_begin_answered_after_its_connection_closed_fails_and_holds_nothing() {
        let (client, mut rx) = pipe_client();
        let caller = client.clone();
        let call = tokio::spawn(async move { caller.progress_begin("t").await });

        let frame = loop {
            tokio::task::yield_now().await;
            if let Ok(frame) = rx.try_recv() {
                break frame;
            }
        };
        let id = request_id(&frame);
        client.complete(id, Ok(json!({ "operationId": "op-9" })));
        client.close();

        let result = call.await.expect("progress_begin task");
        assert_eq!(result, Err("The host connection is closed".to_string()));
        assert_eq!(client.cancel_flag_count(), 0);
        assert!(!client.is_cancelled("op-9"));
    }
}
