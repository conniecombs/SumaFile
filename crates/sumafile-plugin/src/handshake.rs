//! The `plugin.handshake` reply, answered by the crate itself.

use serde_json::{json, Value};

use crate::{PluginInfo, SDK_VERSION};

pub(crate) const HANDSHAKE_METHOD: &str = "plugin.handshake";

/// `client` value the Rust service sends in its handshake.
pub(crate) const SERVICE_CLIENT: &str = "simplefile-service";

/// True when the handshake comes from the Rust service, which is routed to
/// the same handler but never answers `host.*` requests.
pub(crate) fn is_service_client(params: &Value) -> bool {
    params.get("client").and_then(Value::as_str) == Some(SERVICE_CLIENT)
}

/// Why a handshake is refused by a backend that was launched with a token,
/// or `None` when its `authToken` is the launch token.
pub(crate) fn token_refusal(expected: &str, params: &Value) -> Option<&'static str> {
    match params.get("authToken").and_then(Value::as_str) {
        None => Some("authToken is required"),
        Some(provided) if !constant_time_eq(expected.as_bytes(), provided.as_bytes()) => {
            Some("authToken does not match")
        }
        Some(_) => None,
    }
}

/// Compares every byte whatever the first difference, so the time taken says
/// nothing about how much of a guess was right.
fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    let mut difference = left.len() ^ right.len();
    for index in 0..left.len().max(right.len()) {
        let a = left.get(index).copied().unwrap_or(0);
        let b = right.get(index).copied().unwrap_or(0);
        difference |= usize::from(a ^ b);
    }
    difference == 0
}

/// Reads the launch token the host wrote to standard input, when the host
/// said it would: [`crate::AUTH_STDIN_ENV`] is `1` in the environment. The
/// token is the first line, without its line ending.
pub(crate) fn read_launch_token() -> Result<Option<String>, String> {
    if std::env::var(crate::AUTH_STDIN_ENV).as_deref() != Ok("1") {
        return Ok(None);
    }
    let mut line = String::new();
    std::io::stdin()
        .read_line(&mut line)
        .map_err(|error| format!("Could not read the launch token: {error}"))?;
    launch_token_from_line(&line).map(Some)
}

/// At most this many bytes of token. The host sends 64.
const MAX_TOKEN_BYTES: usize = 512;

fn launch_token_from_line(line: &str) -> Result<String, String> {
    let token = line.trim_end_matches(['\r', '\n']);
    if token.is_empty() {
        return Err("The host announced a launch token and sent none".to_string());
    }
    if token.len() > MAX_TOKEN_BYTES || !token.bytes().all(|byte| byte.is_ascii_graphic()) {
        return Err("The launch token is not a line of printable characters".to_string());
    }
    Ok(token.to_string())
}

/// Reply to `plugin.handshake`. `pluginId`, `hostVersion` and `sdkVersion`
/// are not checked here: the caller compares the reply's `pluginId` and
/// `sdkVersion` itself.
pub(crate) fn handshake_result(info: &PluginInfo) -> Value {
    json!({
        "pluginId": info.id,
        "pluginVersion": info.version,
        "sdkVersion": SDK_VERSION,
    })
}
