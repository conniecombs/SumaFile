//! The per-plugin connection pool.
//!
//! A call takes an idle connection, or opens one while fewer than
//! `max_connections` are open; otherwise it waits up to `lock`, then fails
//! with `plugin {id} is busy`. A transport error or timeout closes that
//! connection and discards every idle one, so the next call reconnects and
//! handshakes again. A cancel travels on a connection of its own: an idle
//! one, or a new one even beyond the limit.

use super::connection::{CallError, CancelWatch, Connection, Watch};
use super::{is_valid_pipe_name, PluginTimeouts, CANCEL_METHOD};
use serde_json::{json, Value};
use simplefile_core::archive::{EngineProgress, CANCELLED_MESSAGE};
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::time::Instant;

struct PoolState {
    idle: Vec<Connection>,
    /// Idle plus leased connections.
    open: usize,
}

struct Pool {
    plugin_id: String,
    pipe_name: String,
    timeouts: PluginTimeouts,
    state: Mutex<PoolState>,
    freed: Condvar,
}

/// The connections a plugin's engines share.
pub struct PluginConnection {
    pool: Arc<Pool>,
}

impl PluginConnection {
    /// Connect and handshake now, so a plugin that is not serving fails
    /// registration instead of the first archive operation.
    pub fn connect(plugin_id: &str, pipe_name: &str) -> Result<Arc<Self>, String> {
        Self::connect_with_timeouts(plugin_id, pipe_name, PluginTimeouts::default())
    }

    pub fn connect_with_timeouts(
        plugin_id: &str,
        pipe_name: &str,
        timeouts: PluginTimeouts,
    ) -> Result<Arc<Self>, String> {
        if !is_valid_pipe_name(pipe_name) {
            return Err(format!("Invalid plugin pipe name: {pipe_name}"));
        }
        let first = Connection::open(pipe_name, plugin_id, &timeouts)?;
        Ok(Arc::new(Self {
            pool: Arc::new(Pool {
                plugin_id: plugin_id.to_string(),
                pipe_name: pipe_name.to_string(),
                timeouts,
                state: Mutex::new(PoolState {
                    idle: vec![first],
                    open: 1,
                }),
                freed: Condvar::new(),
            }),
        }))
    }

    /// One call under its method's deadline. A string `params.operationId`
    /// is the operation whose progress extends an idle deadline.
    pub fn call(&self, method: &str, params: Value) -> Result<Value, String> {
        let operation_id = params
            .get("operationId")
            .and_then(Value::as_str)
            .map(str::to_string);
        let watch = Watch {
            operation_id: operation_id.as_deref(),
            ..Watch::default()
        };
        self.pool.run(method, params, watch)
    }

    /// A call that belongs to `operation_id`: it carries the id, relays the
    /// plugin's progress for it, and while it waits it polls `cancel` every
    /// 50 ms. The first time `cancel` is set it sends `cancel
    /// {operationId}` on another connection, waits at most the cancel
    /// deadline for its own reply (closing the connection if none comes),
    /// and fails with the cancelled message.
    pub fn call_tracked(
        &self,
        method: &str,
        mut params: Value,
        operation_id: &str,
        cancel: &AtomicBool,
        progress: &(dyn Fn(EngineProgress) + Sync),
    ) -> Result<Value, String> {
        if let Some(object) = params.as_object_mut() {
            object.insert("operationId".to_string(), json!(operation_id));
        }
        let pool = Arc::clone(&self.pool);
        let cancel_id = operation_id.to_string();
        let send_cancel = move || {
            let pool = Arc::clone(&pool);
            let operation_id = cancel_id.clone();
            // The cancelled call keeps waiting for its own reply meanwhile.
            let _ = std::thread::Builder::new()
                .name("plugin-cancel".to_string())
                .spawn(move || pool.send_cancel(&operation_id));
        };
        let watch = Watch {
            operation_id: Some(operation_id),
            progress: Some(progress),
            cancel: Some(CancelWatch {
                flag: cancel,
                send_cancel: &send_cancel,
                wait: self.pool.timeouts.cancel,
            }),
        };
        self.pool.run(method, params, watch)
    }

    /// Idle plus leased connections.
    pub fn open_connections(&self) -> usize {
        self.pool.lock_state().open
    }
}

impl Pool {
    fn lock_state(&self) -> MutexGuard<'_, PoolState> {
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn run(&self, method: &str, params: Value, watch: Watch<'_>) -> Result<Value, String> {
        let mut connection = self.acquire()?;
        let result = connection.call(method, params, self.timeouts.for_method(method), watch);
        match result {
            Ok(value) => {
                self.release(connection);
                Ok(value)
            }
            Err(CallError::Remote(message)) => {
                self.release(connection);
                Err(message)
            }
            Err(CallError::Cancelled) => {
                // A connection that did not answer in time was closed.
                self.release(connection);
                Err(CANCELLED_MESSAGE.to_string())
            }
            Err(CallError::Transport(message)) => {
                self.release(connection);
                self.discard_idle();
                Err(message)
            }
        }
    }

    /// An idle connection, or a new one while the pool has room. Otherwise
    /// wait at most `timeouts.lock` for one to come back, so queued callers
    /// give their lane permit back instead of waiting forever.
    fn acquire(&self) -> Result<Connection, String> {
        let deadline = Instant::now() + self.timeouts.lock;
        let limit = self.timeouts.max_connections.max(1);
        let mut state = self.lock_state();
        loop {
            if let Some(connection) = state.idle.pop() {
                return Ok(connection);
            }
            if state.open < limit {
                state.open += 1;
                drop(state);
                return self.open_counted();
            }
            let now = Instant::now();
            if now >= deadline {
                return Err(format!("plugin {} is busy", self.plugin_id));
            }
            state = self
                .freed
                .wait_timeout(state, deadline - now)
                .unwrap_or_else(PoisonError::into_inner)
                .0;
        }
    }

    /// Open a connection whose slot is already counted in `open`.
    fn open_counted(&self) -> Result<Connection, String> {
        Connection::open(&self.pipe_name, &self.plugin_id, &self.timeouts).inspect_err(|_| {
            self.lock_state().open -= 1;
            self.freed.notify_one();
        })
    }

    /// Return a connection: back to the idle list while it is open and the
    /// pool is within its limit (a cancel may have opened one beyond it),
    /// otherwise give its slot back.
    fn release(&self, connection: Connection) {
        let limit = self.timeouts.max_connections.max(1);
        let closed = {
            let mut state = self.lock_state();
            if connection.is_open() && state.open <= limit {
                state.idle.push(connection);
                None
            } else {
                state.open -= 1;
                Some(connection)
            }
        };
        self.freed.notify_one();
        drop(closed);
    }

    /// Drop every idle connection after a transport error: the plugin may
    /// have restarted or died, so the next call reconnects.
    fn discard_idle(&self) {
        let idle = {
            let mut state = self.lock_state();
            let idle = std::mem::take(&mut state.idle);
            state.open -= idle.len();
            idle
        };
        self.freed.notify_all();
        drop(idle);
    }

    /// Send `cancel {operationId}` on an idle connection, or on a new one
    /// even beyond the limit: the cancelled call holds its own.
    fn send_cancel(&self, operation_id: &str) {
        let idle = {
            let mut state = self.lock_state();
            let idle = state.idle.pop();
            if idle.is_none() {
                state.open += 1;
            }
            idle
        };
        let mut connection = match idle {
            Some(connection) => connection,
            None => match self.open_counted() {
                Ok(connection) => connection,
                Err(error) => {
                    log::warn!(
                        "Could not send cancel for {operation_id} to plugin {}: {error}",
                        self.plugin_id
                    );
                    return;
                }
            },
        };
        let result = connection.call(
            CANCEL_METHOD,
            json!({ "operationId": operation_id }),
            self.timeouts.for_method(CANCEL_METHOD),
            Watch::default(),
        );
        if let Err(error) = &result {
            log::warn!(
                "Plugin {} did not take the cancel for {operation_id}: {error:?}",
                self.plugin_id
            );
        }
        self.release(connection);
    }
}
