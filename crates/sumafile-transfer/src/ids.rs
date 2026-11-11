//! Process-wide operation ids shared by every transfer engine.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

/// One counter for the whole process. `simplefile_core::utils::generate_operation_id`
/// forwards here, so a transfer id and any other operation id minted in the same
/// second can never collide.
static COUNTER: AtomicU64 = AtomicU64::new(0);

/// A new `op_<unix seconds>_<counter>` id.
pub fn generate_transfer_operation_id() -> String {
    let count = COUNTER.fetch_add(1, Ordering::Relaxed);
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    format!("op_{secs}_{count}")
}

#[cfg(test)]
mod tests {
    use super::generate_transfer_operation_id;
    use std::collections::HashSet;

    fn matches_operation_id_shape(id: &str) -> bool {
        let Some(rest) = id.strip_prefix("op_") else {
            return false;
        };
        let Some((secs, count)) = rest.split_once('_') else {
            return false;
        };
        [secs, count]
            .iter()
            .all(|part| !part.is_empty() && part.bytes().all(|b| b.is_ascii_digit()))
    }

    #[test]
    fn operation_ids_are_unique() {
        let ids: Vec<String> = (0..1000)
            .map(|_| generate_transfer_operation_id())
            .collect();
        for id in &ids {
            assert!(matches_operation_id_shape(id), "unexpected id shape: {id}");
        }
        let distinct: HashSet<&String> = ids.iter().collect();
        assert_eq!(distinct.len(), 1000);
    }
}
