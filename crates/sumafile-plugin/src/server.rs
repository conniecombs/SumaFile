//! Named pipe server: accepting clients and running each connection.
//!
//! Every connection handshakes on its own. Its read loop demultiplexes the
//! frames that share the pipe: a frame with `method` and an `id` is a request
//! to the plugin, a frame with `id` and `result` or `error` is the reply to
//! one of this backend's `host.*` requests, and a frame with `method` but no
//! `id` is a notification. Handler calls run on spawned tasks so a slow
//! handler never stops the loop from reading the replies it waits for.

use std::sync::Arc;

use serde_json::Value;
use simplefile_ipc::frame::decode_length;
use simplefile_ipc::rpc::JsonRpcResponse;
use simplefile_ipc::{
    ERR_INTERNAL, ERR_INVALID_PARAMS, ERR_INVALID_REQUEST, ERR_PARSE, JSONRPC_VERSION,
};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use tokio::sync::mpsc;

use crate::handshake::{handshake_result, is_service_client, HANDSHAKE_METHOD};
use crate::{Args, HostClient, PluginHandler, PluginInfo};

const CANCEL_METHOD: &str = "cancel";

/// Serves `\\.\pipe\<args.pipe_name>` until the last client disconnects.
///
/// With `accepts_multiple_clients` the pipe allows unlimited instances (the
/// host and the service may both connect) and a new listening instance is
/// created after each accept; otherwise a single instance serves one client.
pub async fn serve(
    args: Args,
    info: PluginInfo,
    accepts_multiple_clients: bool,
    handler: Arc<dyn PluginHandler>,
) -> Result<(), String> {
    let path = format!(r"\\.\pipe\{}", args.pipe_name);
    let info = Arc::new(info);

    if !accepts_multiple_clients {
        let server = ServerOptions::new()
            .first_pipe_instance(true)
            .max_instances(1)
            .create(&path)
            .map_err(|e| format!("Could not create pipe {path}: {e}"))?;
        server
            .connect()
            .await
            .map_err(|e| format!("Pipe {path} accept failed: {e}"))?;
        run_connection(server, info, handler).await;
        return Ok(());
    }

    // Unlimited instances is the default when `max_instances` is not set.
    let mut listener = ServerOptions::new()
        .first_pipe_instance(true)
        .create(&path)
        .map_err(|e| format!("Could not create pipe {path}: {e}"))?;
    let (done_sender, mut done_receiver) = mpsc::unbounded_channel::<()>();
    let mut active = 0usize;
    loop {
        tokio::select! {
            biased;
            Some(()) = done_receiver.recv() => {
                active -= 1;
                if active == 0 {
                    return Ok(());
                }
            }
            connected = listener.connect() => {
                let next = ServerOptions::new()
                    .create(&path)
                    .map_err(|e| format!("Could not create pipe {path}: {e}"))?;
                let server = std::mem::replace(&mut listener, next);
                if connected.is_err() {
                    // That client vanished mid-accept; keep listening.
                    continue;
                }
                active += 1;
                let done = done_sender.clone();
                let info = info.clone();
                let handler = handler.clone();
                tokio::spawn(async move {
                    run_connection(server, info, handler).await;
                    let _ = done.send(());
                });
            }
        }
    }
}

enum Incoming {
    Message(Value),
    Malformed,
}

/// Reads one frame; `None` at end of stream or on an unrecoverable error.
async fn read_incoming(reader: &mut (impl AsyncRead + Unpin)) -> Option<Incoming> {
    let mut header = [0u8; 4];
    reader.read_exact(&mut header).await.ok()?;
    let length = decode_length(header).ok()? as usize;
    let mut payload = vec![0u8; length];
    reader.read_exact(&mut payload).await.ok()?;
    Some(match serde_json::from_slice(&payload) {
        Ok(message) => Incoming::Message(message),
        Err(_) => Incoming::Malformed,
    })
}

struct Connection {
    info: Arc<PluginInfo>,
    handler: Arc<dyn PluginHandler>,
    host: HostClient,
    handshaken: bool,
}

async fn run_connection(
    server: NamedPipeServer,
    info: Arc<PluginInfo>,
    handler: Arc<dyn PluginHandler>,
) {
    let (mut reader, mut writer) = tokio::io::split(server);
    let (outbound, mut frames) = mpsc::unbounded_channel::<Vec<u8>>();
    tokio::spawn(async move {
        while let Some(frame) = frames.recv().await {
            if writer.write_all(&frame).await.is_err() || writer.flush().await.is_err() {
                break;
            }
        }
    });

    let mut connection = Connection {
        info,
        handler,
        host: HostClient::pipe(outbound),
        handshaken: false,
    };
    while let Some(incoming) = read_incoming(&mut reader).await {
        match incoming {
            Incoming::Message(message) => connection.route(message),
            Incoming::Malformed => connection.reply_null_id(ERR_PARSE, "Parse error"),
        }
    }
    // Wakes every `host.*` call still waiting; handler tasks may still be
    // running and finish on their own.
    connection.host.close();
}

impl Connection {
    fn reply(&self, response: JsonRpcResponse) {
        self.host.send_message(&response);
    }

    /// Parse and invalid-request errors carry `"id": null`, which
    /// `JsonRpcResponse` would omit.
    fn reply_null_id(&self, code: i32, message: &str) {
        self.host.send_message(&serde_json::json!({
            "jsonrpc": JSONRPC_VERSION,
            "id": null,
            "error": { "code": code, "message": message },
        }));
    }

    fn route(&mut self, message: Value) {
        let id = message.get("id").filter(|id| !id.is_null()).cloned();
        if let Some(method) = message.get("method").and_then(Value::as_str) {
            let params = message.get("params").cloned().unwrap_or(Value::Null);
            match id {
                Some(id) => self.request(id, method, params),
                None => self.notification(method, &params),
            }
            return;
        }
        let Some(id) = id else {
            self.reply_null_id(ERR_INVALID_REQUEST, "Invalid request");
            return;
        };
        let error = message.get("error").filter(|error| !error.is_null());
        if error.is_none() && message.get("result").is_none() {
            self.reply(JsonRpcResponse::error(
                Some(id),
                ERR_INVALID_REQUEST,
                "Invalid request",
            ));
            return;
        }
        // A reply to one of our `host.*` requests; their ids are numeric.
        let Some(id) = id.as_u64() else {
            return;
        };
        let reply = match error {
            Some(error) => Err(error
                .get("message")
                .and_then(Value::as_str)
                .map(str::to_string)
                .unwrap_or_else(|| error.to_string())),
            None => Ok(message.get("result").cloned().unwrap_or(Value::Null)),
        };
        self.host.complete(id, reply);
    }

    fn request(&mut self, id: Value, method: &str, params: Value) {
        if method == HANDSHAKE_METHOD {
            self.handshaken = true;
            if is_service_client(&params) {
                self.host.set_service_mode();
            }
            self.reply(JsonRpcResponse::result(
                Some(id),
                handshake_result(&self.info),
            ));
            return;
        }
        if !self.handshaken {
            self.reply(JsonRpcResponse::error(
                Some(id),
                ERR_INVALID_REQUEST,
                format!("{HANDSHAKE_METHOD} must come before {method}"),
            ));
            return;
        }
        if method == CANCEL_METHOD {
            let response = match operation_id(&params) {
                Some(operation_id) => {
                    self.host.mark_cancelled(operation_id);
                    JsonRpcResponse::result(Some(id), Value::Null)
                }
                None => JsonRpcResponse::error(
                    Some(id),
                    ERR_INVALID_PARAMS,
                    "cancel needs a string operationId",
                ),
            };
            self.reply(response);
            return;
        }

        let handler = self.handler.clone();
        let host = self.host.clone();
        let method = method.to_string();
        tokio::spawn(async move {
            // The handler runs on its own task so a panic surfaces as a
            // `JoinError` here and the request still gets an answer.
            let call = {
                let host = host.clone();
                tokio::spawn(async move { handler.handle(&method, params, &host).await })
            };
            let response = match call.await {
                Ok(Ok(result)) => JsonRpcResponse::result(Some(id), result),
                Ok(Err(error)) => JsonRpcResponse::error(Some(id), error.code(), error.message()),
                Err(_) => JsonRpcResponse::error(Some(id), ERR_INTERNAL, "plugin handler panicked"),
            };
            host.send_message(&response);
        });
    }

    fn notification(&mut self, method: &str, params: &Value) {
        if self.handshaken && method == CANCEL_METHOD {
            if let Some(operation_id) = operation_id(params) {
                self.host.mark_cancelled(operation_id);
            }
        }
    }
}

fn operation_id(params: &Value) -> Option<&str> {
    params.get("operationId").and_then(Value::as_str)
}
