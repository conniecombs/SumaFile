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

    /// Remove `operation_id` only while it still maps to `token`. A job that
    /// finishes after a newer job reused its id must not strand the newer one.
    pub async fn remove_token(&self, operation_id: &str, token: &Arc<AtomicBool>) {
        let mut operations = self.operations.lock().await;
        if operations
            .get(operation_id)
            .is_some_and(|current| Arc::ptr_eq(current, token))
        {
            operations.remove(operation_id);
        }
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

    /// A client that reuses an operation id while the first job still runs
    /// must not be able to strand the second job: finishing the first job
    /// removes only its own token.
    #[tokio::test]
    async fn finishing_an_older_job_keeps_the_newer_token_cancellable() {
        let registry = OperationRegistry::default();
        let first = registry.register("op-1").await;
        let second = registry.register("op-1").await;
        assert!(!std::sync::Arc::ptr_eq(&first, &second));

        registry.remove_token("op-1", &first).await;
        assert!(registry.cancel("op-1").await, "newer job lost its token");
        assert!(second.load(Ordering::Relaxed));

        registry.remove_token("op-1", &second).await;
        assert!(!registry.cancel("op-1").await);
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
