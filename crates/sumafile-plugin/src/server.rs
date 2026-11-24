//! Named pipe server: accepting clients and running each connection.
//!
//! Every connection handshakes on its own. Its read loop demultiplexes the
//! frames that share the pipe: a frame with `method` and an `id` is a request
//! to the plugin, a frame with `id` and `result` or `error` is the reply to
//! one of this backend's `host.*` requests, and a frame with `method` but no
//! `id` is a notification. Handler calls run on spawned tasks so a slow
//! handler never stops the loop from reading the replies it waits for.
//!
//! Cancel flags are backend-wide and owned here. Before a request's handler
//! is spawned, its string `params.operationId` is recorded as in flight; the
//! id and its flag are dropped when the handler returns, before the reply is
//! sent. (`HostClient::progress_begin` holds the ids it returns in flight
//! the same way, until `progress_complete`, `progress_fail` or the close of
//! its connection.) A `cancel` from any connection sets the flag of an
//! in-flight id; any other cancel is kept among the `EARLY_CANCEL_LIMIT` most
//! recent early cancels, and an id among them starts cancelled when it goes
//! in flight.

use std::sync::Arc;

use serde_json::Value;
use simplefile_ipc::frame::decode_length;
use simplefile_ipc::rpc::JsonRpcResponse;
use simplefile_ipc::{
    ERR_INTERNAL, ERR_INVALID_PARAMS, ERR_INVALID_REQUEST, ERR_PARSE, JSONRPC_VERSION,
    MAX_FRAME_BYTES,
};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use tokio::sync::mpsc;

use crate::handshake::{handshake_result, is_service_client, HANDSHAKE_METHOD};
use crate::host::BackendState;
use crate::{Args, HostClient, PluginHandler, PluginInfo};

const CANCEL_METHOD: &str = "cancel";
/// Unhandshaken clients may only send a small `plugin.handshake` request; full
/// payload frames are enabled only after handshake succeeds.
const PRE_HANDSHAKE_MAX_FRAME_BYTES: usize = 64 * 1024;

fn create_pipe_server(
    path: &str,
    first_instance: bool,
    accepts_multiple_clients: bool,
) -> Result<NamedPipeServer, String> {
    let mut options = ServerOptions::new();
    options.reject_remote_clients(true);
    if first_instance {
        options.first_pipe_instance(true);
        options.write_dac(true);
    }
    if !accepts_multiple_clients {
        options.max_instances(1);
    }
    let server = options
        .create(path)
        .map_err(|e| format!("Could not create pipe {path}: {e}"))?;
    if first_instance {
        apply_creator_only_dacl(&server)?;
    }
    Ok(server)
}

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
    let state = Arc::new(BackendState::default());

    if !accepts_multiple_clients {
        let server = create_pipe_server(&path, true, false)?;
        server
            .connect()
            .await
            .map_err(|e| format!("Pipe {path} accept failed: {e}"))?;
        run_connection(server, info, handler, state).await;
        return Ok(());
    }

    // Unlimited instances is the default when `max_instances` is not set.
    let mut listener = create_pipe_server(&path, true, true)?;
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
                let next = create_pipe_server(&path, false, true)?;
                let server = std::mem::replace(&mut listener, next);
                if connected.is_err() {
                    // That client vanished mid-accept; keep listening.
                    continue;
                }
                active += 1;
                let done = done_sender.clone();
                let info = info.clone();
                let handler = handler.clone();
                let state = state.clone();
                tokio::spawn(async move {
                    run_connection(server, info, handler, state).await;
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

/// Reads one frame up to `max_len` bytes; `None` at end of stream or on an
/// unrecoverable or oversized-frame error.
async fn read_incoming(reader: &mut (impl AsyncRead + Unpin), max_len: usize) -> Option<Incoming> {
    let mut header = [0u8; 4];
    reader.read_exact(&mut header).await.ok()?;
    let length = decode_length(header).ok()? as usize;
    if length > max_len {
        return None;
    }
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
    state: Arc<BackendState>,
    handshaken: bool,
}

/// Drops a request's operation id from the in-flight set when the request's
/// task ends, however it ends.
struct InFlightGuard {
    state: Arc<BackendState>,
    operation_id: Option<String>,
}

impl InFlightGuard {
    fn begin(state: &Arc<BackendState>, params: &Value) -> InFlightGuard {
        let operation_id = operation_id(params).map(str::to_string);
        if let Some(operation_id) = &operation_id {
            state.begin(operation_id);
        }
        InFlightGuard {
            state: state.clone(),
            operation_id,
        }
    }
}

impl Drop for InFlightGuard {
    fn drop(&mut self) {
        if let Some(operation_id) = self.operation_id.take() {
            self.state.finish(&operation_id);
        }
    }
}

async fn run_connection(
    server: NamedPipeServer,
    info: Arc<PluginInfo>,
    handler: Arc<dyn PluginHandler>,
    state: Arc<BackendState>,
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
        host: HostClient::pipe(outbound, state.clone()),
        state,
        handshaken: false,
    };
    while let Some(incoming) = read_incoming(
        &mut reader,
        if connection.handshaken {
            MAX_FRAME_BYTES as usize
        } else {
            PRE_HANDSHAKE_MAX_FRAME_BYTES
        },
    )
    .await
    {
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
            if is_service_client(&params) {
                self.handshaken = true;
                self.host.set_service_mode();
            } else if self.host.set_host_connection() {
                self.handshaken = true;
            } else {
                self.reply(JsonRpcResponse::error(
                    Some(id),
                    ERR_INVALID_REQUEST,
                    "host connection is already established",
                ));
                return;
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
                    self.state.cancel(operation_id);
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
        // Recorded before the spawn, so a cancel read after this request
        // finds it in flight.
        let in_flight = InFlightGuard::begin(&self.state, &params);
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
            drop(in_flight);
            host.send_message(&response);
        });
    }

    fn notification(&mut self, method: &str, params: &Value) {
        if self.handshaken && method == CANCEL_METHOD {
            if let Some(operation_id) = operation_id(params) {
                self.state.cancel(operation_id);
            }
        }
    }
}

fn operation_id(params: &Value) -> Option<&str> {
    params.get("operationId").and_then(Value::as_str)
}

fn apply_creator_only_dacl(server: &NamedPipeServer) -> Result<(), String> {
    use std::ffi::c_void;
    use std::os::windows::io::AsRawHandle;

    type Handle = *mut c_void;

    const TOKEN_QUERY: u32 = 0x0008;
    const TOKEN_USER_CLASS: i32 = 1;
    const SDDL_REVISION_1: u32 = 1;
    const DACL_SECURITY_INFORMATION: u32 = 0x0000_0004;

    #[repr(C)]
    struct SidAndAttributes {
        sid: *mut c_void,
        attributes: u32,
    }

    #[repr(C)]
    struct TokenUser {
        user: SidAndAttributes,
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn GetCurrentProcess() -> Handle;
        fn CloseHandle(handle: Handle) -> i32;
        fn LocalFree(mem: *mut c_void) -> *mut c_void;
    }

    #[link(name = "advapi32")]
    extern "system" {
        fn OpenProcessToken(
            process_handle: Handle,
            desired_access: u32,
            token_handle: *mut Handle,
        ) -> i32;
        fn GetTokenInformation(
            token_handle: Handle,
            token_information_class: i32,
            token_information: *mut c_void,
            token_information_length: u32,
            return_length: *mut u32,
        ) -> i32;
        fn ConvertSidToStringSidW(sid: *mut c_void, string_sid: *mut *mut u16) -> i32;
        fn ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string_sd: *const u16,
            string_sd_revision: u32,
            security_descriptor: *mut *mut c_void,
            security_descriptor_size: *mut u32,
        ) -> i32;
        fn SetKernelObjectSecurity(
            handle: Handle,
            security_information: u32,
            security_descriptor: *mut c_void,
        ) -> i32;
    }

    // SAFETY: standard Win32 token query, SDDL conversion, and DACL update on
    // our newly created `NamedPipeServer` handle before any client connects.
    unsafe {
        let mut token: Handle = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return Err("failed to open process token for plugin pipe DACL".to_string());
        }

        let mut needed = 0u32;
        GetTokenInformation(
            token,
            TOKEN_USER_CLASS,
            std::ptr::null_mut(),
            0,
            &mut needed,
        );
        let mut buffer = vec![0u8; needed.max(1) as usize];
        if GetTokenInformation(
            token,
            TOKEN_USER_CLASS,
            buffer.as_mut_ptr().cast(),
            needed,
            &mut needed,
        ) == 0
        {
            CloseHandle(token);
            return Err("failed to query process user SID for plugin pipe DACL".to_string());
        }
        CloseHandle(token);

        let user = &*(buffer.as_ptr() as *const TokenUser);
        let mut sid_string: *mut u16 = std::ptr::null_mut();
        if ConvertSidToStringSidW(user.user.sid, &mut sid_string) == 0 {
            return Err("failed to convert user SID for plugin pipe DACL".to_string());
        }
        let sid = {
            let mut len = 0usize;
            while *sid_string.add(len) != 0 {
                len += 1;
            }
            String::from_utf16_lossy(std::slice::from_raw_parts(sid_string, len))
        };
        LocalFree(sid_string.cast());

        let sddl = format!("D:P(A;;GA;;;{sid})");
        let sddl_wide: Vec<u16> = sddl.encode_utf16().chain(std::iter::once(0)).collect();
        let mut sd: *mut c_void = std::ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl_wide.as_ptr(),
            SDDL_REVISION_1,
            &mut sd,
            std::ptr::null_mut(),
        ) == 0
        {
            return Err("failed to build creator-only security descriptor".to_string());
        }

        let ok = SetKernelObjectSecurity(
            server.as_raw_handle() as Handle,
            DACL_SECURITY_INFORMATION,
            sd,
        );
        LocalFree(sd);
        if ok == 0 {
            return Err("failed to apply creator-only DACL to plugin named pipe".to_string());
        }
    }
    Ok(())
}
