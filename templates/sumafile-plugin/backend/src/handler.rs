//! The plugin's own methods. After the handshake, every request the plugin's
//! UI half sends (`IPluginHost.InvokeBackendAsync`) arrives in
//! [`ExampleHandler::handle`].

use serde::{Deserialize, Serialize};
use serde_json::Value;
use sumafile_plugin::{async_trait, HostClient, PluginError, PluginHandler};

/// The plugin id, as in `plugin.json`.
pub const PLUGIN_ID: &str = "com.example.template";

/// `example.echo { text } -> { text, length }`.
pub const ECHO: &str = "example.echo";

#[derive(Deserialize)]
struct EchoParams {
    text: String,
}

#[derive(Serialize)]
struct EchoResult {
    text: String,
    /// In Unicode scalar values (Rust `char`s), not bytes.
    length: usize,
}

pub struct ExampleHandler;

#[async_trait]
impl PluginHandler for ExampleHandler {
    /// `host` calls back into SumaFile (`host.get_settings`, `host.notify`,
    /// `host.progress_*`, ...); `example.echo` needs none of them.
    async fn handle(
        &self,
        method: &str,
        params: Value,
        _host: &HostClient,
    ) -> Result<Value, PluginError> {
        match method {
            ECHO => echo(params),
            // -32601 "Method not found": InvokeBackendAsync throws in the UI half.
            _ => Err(PluginError::MethodNotFound),
        }
    }
}

/// Bad params are -32602 (`InvalidParams`); a failure while doing the work
/// would be -32000 (`Application`), whose message the UI half receives.
fn echo(params: Value) -> Result<Value, PluginError> {
    let EchoParams { text } = serde_json::from_value(params)
        .map_err(|error| PluginError::InvalidParams(format!("{ECHO}: {error}")))?;
    let length = text.chars().count();
    serde_json::to_value(EchoResult { text, length })
        .map_err(|error| PluginError::Application(format!("{ECHO}: {error}")))
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use serde_json::json;

    use super::*;

    async fn call(method: &str, params: Value) -> Result<Value, PluginError> {
        let host = HostClient::fake(HashMap::new());
        ExampleHandler.handle(method, params, &host).await
    }

    #[tokio::test]
    async fn echo_returns_the_text_and_its_length() {
        let reply = call(ECHO, json!({ "text": "Hello" })).await;

        assert_eq!(reply, Ok(json!({ "text": "Hello", "length": 5 })));
    }

    #[tokio::test]
    async fn echo_counts_characters_not_bytes() {
        let reply = call(ECHO, json!({ "text": "añ😀" })).await;

        assert_eq!(reply, Ok(json!({ "text": "añ😀", "length": 3 })));
    }

    #[tokio::test]
    async fn echo_of_an_empty_text_has_length_zero() {
        let reply = call(ECHO, json!({ "text": "" })).await;

        assert_eq!(reply, Ok(json!({ "text": "", "length": 0 })));
    }

    #[tokio::test]
    async fn echo_without_a_text_string_is_invalid_params() {
        for params in [json!({}), json!({ "text": 5 }), Value::Null] {
            let error = call(ECHO, params.clone()).await.unwrap_err();

            assert!(
                matches!(&error, PluginError::InvalidParams(message) if message.starts_with("example.echo: ")),
                "{params} gave {error:?}"
            );
            assert_eq!(error.code(), -32602);
        }
    }

    #[tokio::test]
    async fn another_method_is_not_found() {
        let error = call("example.unknown", json!({})).await.unwrap_err();

        assert_eq!(error, PluginError::MethodNotFound);
        assert_eq!(error.code(), -32601);
    }
}
