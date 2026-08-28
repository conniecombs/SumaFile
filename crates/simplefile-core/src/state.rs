use notify::RecommendedWatcher;
use parking_lot::Mutex;
use std::collections::HashMap;
use std::sync::{
    atomic::{AtomicBool, AtomicU64, Ordering},
    Arc,
};

pub struct WatcherState {
    pub watcher: Option<RecommendedWatcher>,
    pub watched_path: Option<String>,
}

pub struct AppState {
    pub watcher_state: Mutex<WatcherState>,
    pub cancelled_operations: Mutex<HashMap<String, bool>>,
    /// Cancellation flag for the in-progress folder size calculation.
    /// Set to `true` to abort; reset to `false` at the start of each new calculation.
    pub folder_size_cancel: Arc<AtomicBool>,
    /// Monotonic token for folder size requests. A newer request invalidates
    /// older blocking traversals even if it immediately resets the cancel flag.
    pub folder_size_generation: AtomicU64,
    /// Cancellation flag for the in-progress item count.
    /// Set to `true` to abort; reset to `false` at the start of each new count.
    pub item_count_cancel: Arc<AtomicBool>,
    /// Monotonic token for item count requests.
    pub item_count_generation: AtomicU64,
    /// Cancellation flag for passive direct child-count requests in the file list.
    pub folder_item_count_cancel: Arc<AtomicBool>,
    /// Monotonic token for passive direct child-count requests.
    pub folder_item_count_generation: AtomicU64,
    /// Cancellation flag for the in-progress disk cleanup scan.
    /// Set to `true` to abort; reset to `false` at the start of each new scan.
    pub disk_cleanup_cancel: Arc<AtomicBool>,
    /// Cancellation flag for the in-progress duplicate checker scan.
    /// Set to `true` to abort; reset to `false` at the start of each new scan.
    pub duplicate_check_cancel: Arc<AtomicBool>,
}

impl Default for AppState {
    fn default() -> Self {
        Self {
            watcher_state: Mutex::new(WatcherState {
                watcher: None,
                watched_path: None,
            }),
            cancelled_operations: Mutex::new(HashMap::new()),
            folder_size_cancel: Arc::new(AtomicBool::new(false)),
            folder_size_generation: AtomicU64::new(0),
            item_count_cancel: Arc::new(AtomicBool::new(false)),
            item_count_generation: AtomicU64::new(0),
            folder_item_count_cancel: Arc::new(AtomicBool::new(false)),
            folder_item_count_generation: AtomicU64::new(0),
            disk_cleanup_cancel: Arc::new(AtomicBool::new(false)),
            duplicate_check_cancel: Arc::new(AtomicBool::new(false)),
        }
    }
}

impl AppState {
    /// Start a new folder-size traversal and return the token it must match.
    pub fn begin_folder_size(&self) -> u64 {
        self.folder_size_cancel.store(false, Ordering::Relaxed);
        self.folder_size_generation
            .fetch_add(1, Ordering::Relaxed)
            .wrapping_add(1)
    }

    /// Signal any in-progress folder size calculation to stop.
    pub fn cancel_folder_size(&self) {
        self.folder_size_cancel.store(true, Ordering::Relaxed);
        self.folder_size_generation.fetch_add(1, Ordering::Relaxed);
    }

    /// Start a new item-count traversal and return the token it must match.
    pub fn begin_item_count(&self) -> u64 {
        self.item_count_cancel.store(false, Ordering::Relaxed);
        self.item_count_generation
            .fetch_add(1, Ordering::Relaxed)
            .wrapping_add(1)
    }

    /// Signal any in-progress item count to stop.
    pub fn cancel_item_count(&self) {
        self.item_count_cancel.store(true, Ordering::Relaxed);
        self.item_count_generation.fetch_add(1, Ordering::Relaxed);
    }

    /// Start a passive direct child-count traversal and return the token it must match.
    pub fn begin_folder_item_count(&self) -> u64 {
        self.folder_item_count_cancel
            .store(false, Ordering::Relaxed);
        self.folder_item_count_generation
            .fetch_add(1, Ordering::Relaxed)
            .wrapping_add(1)
    }

    /// Signal any passive direct child-count traversal to stop.
    pub fn cancel_folder_item_count(&self) {
        self.folder_item_count_cancel.store(true, Ordering::Relaxed);
        self.folder_item_count_generation
            .fetch_add(1, Ordering::Relaxed);
    }

    /// Signal any in-progress disk cleanup scan to stop.
    pub fn cancel_disk_cleanup(&self) {
        self.disk_cleanup_cancel.store(true, Ordering::Relaxed);
    }

    /// Signal any in-progress duplicate checker scan to stop.
    pub fn cancel_duplicate_check(&self) {
        self.duplicate_check_cancel.store(true, Ordering::Relaxed);
    }
}
