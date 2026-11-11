//! The manifest's transfer settings are the engine's: `plugins/remote/plugin.json` declares exactly
//! `TRANSFER_SETTING_KEYS`, in order and in group "Transfers", and each default, as a plugin setting
//! string, is `setting_values(&Policy::default())`. The C# side pins the same defaults against
//! `RemoteTransferSettings.DefaultValues` (`RemoteTransferSettings_DefaultsMatchTheManifest`).

use std::path::Path;

use serde_json::Value;
use sumafile_remote_backend::settings::{setting_values, TRANSFER_SETTING_KEYS};
use sumafile_transfer::policy::Policy;

fn manifest() -> Value {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("plugin.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()));
    serde_json::from_str(&text).expect("plugin.json is JSON")
}

/// A manifest default as the plugin setting string the host stores and the backend parses.
fn as_setting_string(value: &Value) -> String {
    match value {
        Value::Number(number) => number.to_string(),
        Value::Bool(flag) => flag.to_string(),
        Value::String(text) => text.clone(),
        other => panic!("unexpected default {other}"),
    }
}

#[test]
fn manifest_defaults_match_the_engine_defaults() {
    let manifest = manifest();
    let settings = manifest["settings"]
        .as_array()
        .expect("plugin.json declares settings");

    let keys: Vec<&str> = settings
        .iter()
        .map(|setting| setting["key"].as_str().expect("each setting has a key"))
        .collect();
    assert_eq!(keys, TRANSFER_SETTING_KEYS.to_vec());

    for setting in settings {
        assert_eq!(
            setting["group"].as_str(),
            Some("Transfers"),
            "setting {} is not in group Transfers",
            setting["key"]
        );
    }

    let engine = setting_values(&Policy::default());
    let declared: Vec<(&str, String)> = settings
        .iter()
        .map(|setting| {
            (
                setting["key"].as_str().unwrap(),
                as_setting_string(&setting["default"]),
            )
        })
        .collect();
    assert_eq!(declared, engine);
}
