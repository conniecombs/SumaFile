//! The `plugin.handshake` reply, answered by the crate itself.

use serde_json::{json, Value};

use crate::{PluginInfo, SDK_VERSION};

pub(crate) const HANDSHAKE_METHOD: &str = "plugin.handshake";

/// Reply to `plugin.handshake`. The params (`pluginId`, `hostVersion`,
/// `sdkVersion`, and `client` from the service) are not checked here: the
/// caller compares the reply's `pluginId` and `sdkVersion` itself, and the
/// service client is routed to the same handler as the host.
pub(crate) fn handshake_result(info: &PluginInfo) -> Value {
    json!({
        "pluginId": info.id,
        "pluginVersion": info.version,
        "sdkVersion": SDK_VERSION,
    })
}
