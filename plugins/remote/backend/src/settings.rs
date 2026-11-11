//! The plugin transfer settings the backend reads from the host
//! (`plugin.com.sumafile.remote.<key>`) and the transfer [`Policy`] they
//! make. The defaults are `Policy::default()`'s; Task 11's manifest declares
//! the same values.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::Duration;

use serde_json::Value;
use sumafile_plugin::HostClient;
use sumafile_transfer::policy::{CheckMode, Policy};

/// Every plugin setting the engine reads, in display order.
pub const TRANSFER_SETTING_KEYS: [&str; 8] = [
    "bandwidthMib",
    "checkMode",
    "inplace",
    "preallocate",
    "flushOnClose",
    "lowLevelRetries",
    "highLevelRetries",
    "retrySleepMs",
];

/// How long one settings read waits for the host before the cache is used.
const SETTINGS_TIMEOUT: Duration = Duration::from_secs(2);

fn parse_bool(value: &str) -> Option<bool> {
    match value.trim().to_ascii_lowercase().as_str() {
        "true" => Some(true),
        "false" => Some(false),
        _ => None,
    }
}

fn parse_number<T: std::str::FromStr>(value: &str) -> Option<T> {
    value.trim().parse().ok()
}

fn parse_check_mode(value: &str) -> Option<CheckMode> {
    match value.trim() {
        "size_only" => Some(CheckMode::SizeOnly),
        "checksum" => Some(CheckMode::Checksum),
        "ignore_times" => Some(CheckMode::IgnoreTimes),
        "size_and_mod_time" => Some(CheckMode::SizeAndModTime),
        _ => None,
    }
}

fn check_mode_name(mode: CheckMode) -> &'static str {
    match mode {
        CheckMode::SizeOnly => "size_only",
        CheckMode::Checksum => "checksum",
        CheckMode::IgnoreTimes => "ignore_times",
        CheckMode::SizeAndModTime => "size_and_mod_time",
    }
}

/// `Policy::default()` with these keys applied, then `normalized()`; a
/// missing or unparsable value keeps the default.
pub fn policy_from_settings(values: &HashMap<String, Option<String>>) -> Policy {
    let mut policy = Policy::default();
    let value = |key: &str| values.get(key).and_then(Option::as_deref);
    if let Some(mib) = value("bandwidthMib").and_then(parse_number) {
        policy.bandwidth_mib = mib;
    }
    if let Some(mode) = value("checkMode").and_then(parse_check_mode) {
        policy.check_mode = mode;
    }
    if let Some(flag) = value("inplace").and_then(parse_bool) {
        policy.inplace = flag;
    }
    if let Some(flag) = value("preallocate").and_then(parse_bool) {
        policy.preallocate = flag;
    }
    if let Some(flag) = value("flushOnClose").and_then(parse_bool) {
        policy.flush_on_close = flag;
    }
    if let Some(count) = value("lowLevelRetries").and_then(parse_number) {
        policy.low_level_retries = count;
    }
    if let Some(count) = value("highLevelRetries").and_then(parse_number) {
        policy.high_level_retries = count;
    }
    if let Some(millis) = value("retrySleepMs").and_then(parse_number) {
        policy.retry_sleep_ms = millis;
    }
    policy.normalized()
}

/// Every key's plugin setting string for `policy`, in
/// [`TRANSFER_SETTING_KEYS`] order (`"0"`, `"size_and_mod_time"`,
/// `"false"`, ...).
pub fn setting_values(policy: &Policy) -> Vec<(&'static str, String)> {
    vec![
        ("bandwidthMib", policy.bandwidth_mib.to_string()),
        ("checkMode", check_mode_name(policy.check_mode).to_string()),
        ("inplace", policy.inplace.to_string()),
        ("preallocate", policy.preallocate.to_string()),
        ("flushOnClose", policy.flush_on_close.to_string()),
        ("lowLevelRetries", policy.low_level_retries.to_string()),
        ("highLevelRetries", policy.high_level_retries.to_string()),
        ("retrySleepMs", policy.retry_sleep_ms.to_string()),
    ]
}

/// Keys whose value in a legacy `transfer.policy` JSON differs from
/// `Policy::default()`, as plugin setting strings ([`setting_values`]
/// diffed). JSON that is not a policy object gives nothing.
pub fn settings_from_legacy_policy(json: &str) -> Vec<(String, String)> {
    let Ok(Value::Object(object)) = serde_json::from_str::<Value>(json) else {
        return Vec::new();
    };
    let Ok(legacy) = serde_json::from_value::<Policy>(Value::Object(object)) else {
        return Vec::new();
    };
    let defaults = setting_values(&Policy::default());
    setting_values(&legacy.normalized())
        .into_iter()
        .zip(defaults)
        .filter(|((_, value), (_, default))| value != default)
        .map(|((key, value), _)| (key.to_string(), value))
        .collect()
}

/// The last settings read that succeeded. A failed or slow read keeps it,
/// so a transfer never stalls on the host and never loses a known setting.
#[derive(Default)]
pub struct TransferSettingsCache {
    last: Mutex<HashMap<String, Option<String>>>,
}

impl TransferSettingsCache {
    pub fn new() -> TransferSettingsCache {
        TransferSettingsCache::default()
    }

    /// `host.get_settings(TRANSFER_SETTING_KEYS)` under a 2 s timeout; a
    /// failure keeps the cache. Returns the policy of the cache afterwards.
    pub async fn refresh(&self, host: &HostClient) -> Policy {
        if let Ok(Ok(values)) =
            tokio::time::timeout(SETTINGS_TIMEOUT, host.get_settings(&TRANSFER_SETTING_KEYS)).await
        {
            *self.lock() = values;
        }
        self.policy()
    }

    /// The policy of the last good read; the defaults before any.
    pub fn policy(&self) -> Policy {
        policy_from_settings(&self.lock())
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<String, Option<String>>> {
        self.last
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use sumafile_transfer::policy::{CheckMode, Policy};

    fn values(pairs: &[(&str, Option<&str>)]) -> HashMap<String, Option<String>> {
        pairs
            .iter()
            .map(|(key, value)| (key.to_string(), value.map(str::to_string)))
            .collect()
    }

    #[test]
    fn policy_from_settings_applies_every_key_and_ignores_garbage() {
        assert_eq!(policy_from_settings(&HashMap::new()), Policy::default());

        let policy = policy_from_settings(&values(&[
            ("bandwidthMib", Some("12")),
            ("checkMode", Some("checksum")),
            ("inplace", Some("true")),
            ("preallocate", Some("false")),
            ("flushOnClose", Some("False")),
            ("lowLevelRetries", Some("4")),
            ("highLevelRetries", Some("2")),
            ("retrySleepMs", Some("250")),
        ]));
        assert_eq!(policy.bandwidth_mib, 12);
        assert_eq!(policy.check_mode, CheckMode::Checksum);
        assert!(policy.inplace);
        assert!(!policy.preallocate);
        assert!(!policy.flush_on_close);
        assert_eq!(policy.low_level_retries, 4);
        assert_eq!(policy.high_level_retries, 2);
        assert_eq!(policy.retry_sleep_ms, 250);

        let garbage = policy_from_settings(&values(&[
            ("bandwidthMib", Some("fast")),
            ("checkMode", Some("whatever")),
            ("inplace", Some("yes")),
            ("preallocate", None),
            ("lowLevelRetries", Some("-3")),
            ("highLevelRetries", Some("")),
            ("retrySleepMs", Some("1e9x")),
            ("unrelated", Some("1")),
        ]));
        assert_eq!(garbage, Policy::default());

        // Out-of-range values are clamped by Policy::normalized.
        let clamped = policy_from_settings(&values(&[
            ("lowLevelRetries", Some("500")),
            ("retrySleepMs", Some("1")),
        ]));
        assert_eq!(clamped.low_level_retries, 50);
        assert_eq!(clamped.retry_sleep_ms, 10);
    }

    #[test]
    fn setting_values_cover_every_key_with_the_engine_defaults() {
        let defaults = setting_values(&Policy::default());
        assert_eq!(
            defaults,
            vec![
                ("bandwidthMib", "0".to_string()),
                ("checkMode", "size_and_mod_time".to_string()),
                ("inplace", "false".to_string()),
                ("preallocate", "true".to_string()),
                ("flushOnClose", "true".to_string()),
                ("lowLevelRetries", "10".to_string()),
                ("highLevelRetries", "3".to_string()),
                ("retrySleepMs", "1000".to_string()),
            ]
        );
        let keys: Vec<&str> = defaults.iter().map(|(key, _)| *key).collect();
        assert_eq!(keys, TRANSFER_SETTING_KEYS.to_vec());

        // Every value reads back as the policy it came from.
        let custom = Policy {
            bandwidth_mib: 7,
            check_mode: CheckMode::SizeOnly,
            inplace: true,
            retry_sleep_ms: 40,
            ..Policy::default()
        };
        let round_trip: HashMap<String, Option<String>> = setting_values(&custom)
            .into_iter()
            .map(|(key, value)| (key.to_string(), Some(value)))
            .collect();
        assert_eq!(policy_from_settings(&round_trip), custom);
    }

    #[test]
    fn settings_from_legacy_policy_lists_only_changed_keys() {
        assert_eq!(
            settings_from_legacy_policy(r#"{"bandwidthMib":5,"lowLevelRetries":4}"#),
            vec![
                ("bandwidthMib".to_string(), "5".to_string()),
                ("lowLevelRetries".to_string(), "4".to_string()),
            ]
        );
        // Fields with no plugin setting (mode, concurrency) are not seeded.
        assert_eq!(
            settings_from_legacy_policy(
                r#"{"mode":"manual","manualConcurrency":9,"checkMode":"checksum","flushOnClose":false}"#
            ),
            vec![
                ("checkMode".to_string(), "checksum".to_string()),
                ("flushOnClose".to_string(), "false".to_string()),
            ]
        );
        assert!(settings_from_legacy_policy("{}").is_empty());
        assert!(settings_from_legacy_policy("not json").is_empty());
    }
}
