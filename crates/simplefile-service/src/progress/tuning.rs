//! The stored transfer policy and expiring route history. No transfer data or
//! credentials are stored. The concurrency feedback itself (`Controller`) is
//! pure logic in `sumafile_transfer::tuning`; measurements are supplied by the
//! copy coordinator.

use serde::{Deserialize, Serialize};
use simplefile_core::settings_store::{get_db_setting, set_db_setting};
use std::collections::BTreeMap;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

pub(crate) const POLICY_KEY: &str = "transfer.policy";
const HISTORY_KEY: &str = "transfer.learning.v1";
const MAX_HISTORY: usize = 64;
const MAX_AGE: u64 = 30 * 24 * 60 * 60;
static HISTORY_LOCK: Mutex<()> = Mutex::new(());

pub(crate) use sumafile_transfer::policy::{CheckMode, Mode, OrderBy, Policy};
pub(crate) use sumafile_transfer::tuning::Controller;

/// The stored transfer policy, normalized; the default when none is stored.
pub(crate) fn load_policy() -> Policy {
    get_db_setting(POLICY_KEY.into())
        .ok()
        .flatten()
        .and_then(|s| serde_json::from_str::<Policy>(&s).ok())
        .unwrap_or_default()
        .normalized()
}

/// The per-operation override the Transfers window saved, or the stored policy.
pub(crate) fn policy_for_operation(operation_id: &str) -> Policy {
    // The UI keeps this document bounded and expires abandoned overrides.
    #[derive(Deserialize)]
    struct Override {
        policy: Policy,
        updated: u64,
    }
    let overrides: BTreeMap<String, Override> = get_db_setting("transfer.overrides.v1".into())
        .ok()
        .flatten()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default();
    let now = now_seconds();
    overrides
        .into_iter()
        .find_map(|(id, value)| {
            (id == operation_id && value.updated <= now && now - value.updated < 86400)
                .then(|| value.policy.normalized())
        })
        .unwrap_or_else(load_policy)
}

#[derive(Clone, Debug, Deserialize, Serialize)]
struct Learned {
    concurrency: usize,
    updated: u64,
}

#[derive(Default, Deserialize, Serialize)]
struct History {
    routes: BTreeMap<String, Learned>,
}

impl History {
    fn prune(&mut self, now: u64) {
        self.routes.retain(|_, v| {
            v.concurrency > 0
                && v.concurrency <= 32
                && v.updated <= now
                && now - v.updated <= MAX_AGE
        });
        while self.routes.len() > MAX_HISTORY {
            let oldest = self
                .routes
                .iter()
                .min_by_key(|(_, v)| v.updated)
                .map(|(k, _)| k.clone());
            if let Some(key) = oldest {
                self.routes.remove(&key);
            }
        }
    }
}

fn now_seconds() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

fn read_history() -> History {
    get_db_setting(HISTORY_KEY.into())
        .ok()
        .flatten()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

pub(crate) fn recall(key: &str) -> Option<usize> {
    let _guard = HISTORY_LOCK.lock().ok()?;
    let mut history = read_history();
    history.prune(now_seconds());
    history.routes.get(key).map(|v| v.concurrency)
}

pub(crate) fn remember(key: &str, concurrency: usize) -> Result<(), String> {
    let _guard = HISTORY_LOCK.lock().map_err(|e| e.to_string())?;
    let mut history = read_history();
    let now = now_seconds();
    history.routes.insert(
        key.into(),
        Learned {
            concurrency: concurrency.clamp(1, 32),
            updated: now,
        },
    );
    history.prune(now);
    set_db_setting(
        HISTORY_KEY.into(),
        serde_json::to_string(&history).map_err(|e| e.to_string())?,
    )
}

/// Ordered endpoints preserve direction. The endpoint identity should include
/// the actual mapped share / volume identity, not just its mutable drive letter.
pub(crate) fn route_key(source: &str, destination: &str, bytes: u64, files: u64) -> String {
    let average = bytes / files.max(1);
    let workload = if average < 1024 * 1024 {
        "small"
    } else if average < 16 * 1024 * 1024 {
        "medium"
    } else {
        "large"
    };
    let input = format!("copy-v2\0{source}\0{destination}\0{workload}");
    // Stable hash rather than storing account/share names or filesystem paths.
    let hash = input.bytes().fold(0xcbf29ce484222325u64, |h, b| {
        (h ^ u64::from(b)).wrapping_mul(0x100000001b3)
    });
    format!("{hash:016x}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn history_is_bounded_expires_and_rejects_future_records() {
        let now = MAX_AGE + 100;
        let mut h = History::default();
        for i in 0..80 {
            h.routes.insert(
                i.to_string(),
                Learned {
                    concurrency: 4,
                    updated: now - i,
                },
            );
        }
        h.routes.insert(
            "stale".into(),
            Learned {
                concurrency: 4,
                updated: 1,
            },
        );
        h.routes.insert(
            "future".into(),
            Learned {
                concurrency: 4,
                updated: now + 1,
            },
        );
        h.prune(now);
        assert_eq!(h.routes.len(), MAX_HISTORY);
        assert!(!h.routes.contains_key("stale"));
        assert!(!h.routes.contains_key("future"));
        let roundtrip: History = serde_json::from_str(&serde_json::to_string(&h).unwrap()).unwrap();
        assert_eq!(roundtrip.routes.len(), MAX_HISTORY);
    }

    /// The record an earlier release wrote must keep loading, and what is
    /// written now must still be that record.
    #[test]
    fn history_written_by_earlier_releases_still_loads() {
        let stored = r#"{"routes":{"00c0ffee00c0ffee":{"concurrency":6,"updated":1700000000}}}"#;
        let history: History = serde_json::from_str(stored).unwrap();
        assert_eq!(history.routes["00c0ffee00c0ffee"].concurrency, 6);
        assert_eq!(history.routes["00c0ffee00c0ffee"].updated, 1_700_000_000);
        assert_eq!(serde_json::to_string(&history).unwrap(), stored);
    }

    #[test]
    fn learning_is_direction_and_workload_specific() {
        assert_ne!(
            route_key("cloud", "nas", 100, 1),
            route_key("nas", "cloud", 100, 1)
        );
        assert_ne!(
            route_key("cloud", "nas", 100, 1),
            route_key("cloud", "nas", 32 << 20, 1)
        );
        assert_ne!(
            route_key("share1", "nas", 100, 1),
            route_key("share2", "nas", 100, 1)
        );
    }

    /// The controller's own tests live with it in `sumafile_transfer::tuning`;
    /// this only proves the service reaches the same type and defaults.
    #[test]
    fn the_service_uses_the_shared_controller() {
        let mut c = Controller::new(Policy::default(), 4 << 20, None);
        assert_eq!(c.concurrency, 4);
        assert_eq!(c.explanation, "Measuring transfer speed");
        c.sample(0, std::time::Duration::from_millis(1), false, 1);
        assert_eq!(c.concurrency, 2);
        assert_eq!(c.learned_concurrency(), None);
    }
}
