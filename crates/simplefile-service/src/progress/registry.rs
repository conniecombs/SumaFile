use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tokio::sync::Mutex;

#[derive(Default)]
pub struct OperationRegistry {
    operations: Mutex<HashMap<String, Arc<AtomicBool>>>,
}

impl OperationRegistry {
    pub async fn register(&self, operation_id: &str) -> Arc<AtomicBool> {
        let cancel = Arc::new(AtomicBool::new(false));
        self.operations
            .lock()
            .await
            .insert(operation_id.to_string(), cancel.clone());
        cancel
    }

    pub async fn cancel(&self, operation_id: &str) -> bool {
        if let Some(cancel) = self.operations.lock().await.get(operation_id) {
            cancel.store(true, Ordering::Relaxed);
            true
        } else {
            false
        }
    }

    pub async fn cancel_all(&self) {
        for cancel in self.operations.lock().await.values() {
            cancel.store(true, Ordering::Relaxed);
        }
    }

    pub async fn remove(&self, operation_id: &str) {
        self.operations.lock().await.remove(operation_id);
    }
}

#[cfg(test)]
mod tests {
    use super::OperationRegistry;
    use std::sync::atomic::Ordering;

    #[tokio::test]
    async fn cancel_reports_unknown_operation_without_creating_one() {
        let registry = OperationRegistry::default();

        assert!(!registry.cancel("missing").await);
        assert!(!registry.cancel("missing").await);
    }

    #[tokio::test]
    async fn cancel_marks_registered_operation() {
        let registry = OperationRegistry::default();
        let cancel = registry.register("op-1").await;

        assert!(!cancel.load(Ordering::Relaxed));
        assert!(registry.cancel("op-1").await);
        assert!(cancel.load(Ordering::Relaxed));
    }
}
