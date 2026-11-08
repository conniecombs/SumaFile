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
