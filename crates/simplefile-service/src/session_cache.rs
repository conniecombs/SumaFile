//! Session-only RAM cache for filesystem listings, file/image metadata,
//! thumbnails, and recursive folder metrics.
//!
//! Entries live strictly in process memory for the lifetime of the service
//! session and are selectively invalidated on `notify` watcher events and on
//! mutating file/archive/trash operations.

use simplefile_core::models::{
    DirectoryListing, FileEntry, FileMetadata, FolderMetrics, ImageMetadata, TreeNode,
};
use simplefile_core::utils::is_network_path;
use std::collections::{HashMap, VecDeque};
use std::fs;
use std::hash::Hash;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::UNIX_EPOCH;

const DEFAULT_DIR_LISTING_MAX_ENTRIES: usize = 256;
const DEFAULT_DIR_LISTING_MAX_BYTES: usize = 32 * 1024 * 1024;

const DEFAULT_ENTRY_INFO_MAX_ENTRIES: usize = 4_096;
const DEFAULT_ENTRY_INFO_MAX_BYTES: usize = 4 * 1024 * 1024;

const DEFAULT_FILE_METADATA_MAX_ENTRIES: usize = 2_048;
const DEFAULT_FILE_METADATA_MAX_BYTES: usize = 8 * 1024 * 1024;

const DEFAULT_IMAGE_METADATA_MAX_ENTRIES: usize = 2_048;
const DEFAULT_IMAGE_METADATA_MAX_BYTES: usize = 8 * 1024 * 1024;

const DEFAULT_FOLDER_METRICS_MAX_ENTRIES: usize = 1_024;
const DEFAULT_FOLDER_METRICS_MAX_BYTES: usize = 8 * 1024 * 1024;

const DEFAULT_SUBDIRS_MAX_ENTRIES: usize = 1_024;
const DEFAULT_SUBDIRS_MAX_BYTES: usize = 4 * 1024 * 1024;

const DEFAULT_THUMBNAILS_MAX_ENTRIES: usize = 1_024;
const DEFAULT_THUMBNAILS_MAX_BYTES: usize = 24 * 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LruStats {
    pub count: usize,
    pub max_entries: usize,
    pub estimated_bytes: usize,
    pub max_bytes: usize,
    pub hits: u64,
    pub misses: u64,
    pub evictions: u64,
    pub bypasses: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PathStamp {
    pub is_dir: bool,
    pub size: u64,
    pub modified_nanos: u128,
}

impl PathStamp {
    pub fn for_path(path: &Path) -> Option<Self> {
        if is_network_path(path) {
            return None;
        }
        let metadata = fs::symlink_metadata(path).ok()?;
        let modified_nanos = metadata
            .modified()
            .ok()
            .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
            .map(|dur| dur.as_nanos())
            .unwrap_or(0);
        Some(Self {
            is_dir: metadata.is_dir(),
            size: metadata.len(),
            modified_nanos,
        })
    }
}

#[derive(Debug, Clone)]
struct StampedValue<T> {
    stamp: PathStamp,
    value: T,
}

struct LruSlot<V> {
    value: V,
    estimated_bytes: usize,
    generation: u64,
}

struct BoundedByteLruInner<K, V> {
    max_entries: usize,
    max_bytes: usize,
    estimated_bytes: usize,
    next_generation: u64,
    hits: u64,
    misses: u64,
    evictions: u64,
    bypasses: u64,
    map: HashMap<K, LruSlot<V>>,
    order: VecDeque<(K, u64)>,
}

pub struct BoundedByteLru<K, V> {
    inner: Mutex<BoundedByteLruInner<K, V>>,
}

impl<K, V> BoundedByteLru<K, V>
where
    K: Eq + Hash + Clone,
    V: Clone,
{
    pub fn new(max_entries: usize, max_bytes: usize) -> Self {
        let max_entries = max_entries.max(1);
        let max_bytes = max_bytes.max(1);
        Self {
            inner: Mutex::new(BoundedByteLruInner {
                max_entries,
                max_bytes,
                estimated_bytes: 0,
                next_generation: 0,
                hits: 0,
                misses: 0,
                evictions: 0,
                bypasses: 0,
                map: HashMap::new(),
                order: VecDeque::new(),
            }),
        }
    }

    pub fn get(&self, key: &K) -> Option<V> {
        let mut guard = self.inner.lock().ok()?;
        guard.next_generation = guard.next_generation.wrapping_add(1);
        let generation = guard.next_generation;
        if let Some(slot) = guard.map.get_mut(key) {
            slot.generation = generation;
            let value = slot.value.clone();
            guard.order.push_back((key.clone(), generation));
            guard.hits = guard.hits.saturating_add(1);
            Self::compact_order_if_needed(&mut guard);
            Some(value)
        } else {
            guard.misses = guard.misses.saturating_add(1);
            None
        }
    }

    pub fn put(&self, key: K, value: V, estimated_bytes: usize) {
        let Ok(mut guard) = self.inner.lock() else {
            return;
        };
        let item_bytes = estimated_bytes.max(1);
        if item_bytes > guard.max_bytes {
            if let Some(removed) = guard.map.remove(&key) {
                guard.estimated_bytes = guard
                    .estimated_bytes
                    .saturating_sub(removed.estimated_bytes);
            }
            guard.bypasses = guard.bypasses.saturating_add(1);
            return;
        }

        if let Some(previous) = guard.map.remove(&key) {
            guard.estimated_bytes = guard
                .estimated_bytes
                .saturating_sub(previous.estimated_bytes);
        }

        while (guard.map.len() >= guard.max_entries
            || guard.estimated_bytes.saturating_add(item_bytes) > guard.max_bytes)
            && !guard.map.is_empty()
        {
            if !Self::evict_oldest(&mut guard) {
                break;
            }
        }

        guard.next_generation = guard.next_generation.wrapping_add(1);
        let generation = guard.next_generation;
        guard.estimated_bytes = guard.estimated_bytes.saturating_add(item_bytes);
        guard.map.insert(
            key.clone(),
            LruSlot {
                value,
                estimated_bytes: item_bytes,
                generation,
            },
        );
        guard.order.push_back((key, generation));
        Self::compact_order_if_needed(&mut guard);
    }

    pub fn remove(&self, key: &K) -> bool {
        let Ok(mut guard) = self.inner.lock() else {
            return false;
        };
        if let Some(removed) = guard.map.remove(key) {
            guard.estimated_bytes = guard
                .estimated_bytes
                .saturating_sub(removed.estimated_bytes);
            true
        } else {
            false
        }
    }

    pub fn remove_where(&self, predicate: impl Fn(&K, &V) -> bool) -> usize {
        let Ok(mut guard) = self.inner.lock() else {
            return 0;
        };
        let matching_keys: Vec<K> = guard
            .map
            .iter()
            .filter_map(|(k, slot)| {
                if predicate(k, &slot.value) {
                    Some(k.clone())
                } else {
                    None
                }
            })
            .collect();
        let mut removed_count = 0usize;
        for key in matching_keys {
            if let Some(removed) = guard.map.remove(&key) {
                guard.estimated_bytes = guard
                    .estimated_bytes
                    .saturating_sub(removed.estimated_bytes);
                removed_count += 1;
            }
        }
        if removed_count > 0 {
            Self::rebuild_order(&mut guard);
        }
        removed_count
    }

    pub fn clear(&self) {
        if let Ok(mut guard) = self.inner.lock() {
            guard.map.clear();
            guard.order.clear();
            guard.estimated_bytes = 0;
        }
    }

    pub fn stats(&self) -> LruStats {
        let Ok(guard) = self.inner.lock() else {
            return LruStats::default();
        };
        LruStats {
            count: guard.map.len(),
            max_entries: guard.max_entries,
            estimated_bytes: guard.estimated_bytes,
            max_bytes: guard.max_bytes,
            hits: guard.hits,
            misses: guard.misses,
            evictions: guard.evictions,
            bypasses: guard.bypasses,
        }
    }

    fn evict_oldest(guard: &mut BoundedByteLruInner<K, V>) -> bool {
        while let Some((oldest_key, generation)) = guard.order.pop_front() {
            if let Some(slot) = guard.map.get(&oldest_key) {
                if slot.generation == generation {
                    if let Some(removed) = guard.map.remove(&oldest_key) {
                        guard.estimated_bytes = guard
                            .estimated_bytes
                            .saturating_sub(removed.estimated_bytes);
                        guard.evictions = guard.evictions.saturating_add(1);
                        return true;
                    }
                }
            }
        }
        false
    }

    fn compact_order_if_needed(guard: &mut BoundedByteLruInner<K, V>) {
        if guard.order.len() <= guard.max_entries.saturating_mul(4).max(64) {
            return;
        }
        Self::rebuild_order(guard);
    }

    fn rebuild_order(guard: &mut BoundedByteLruInner<K, V>) {
        let mut valid: Vec<(K, u64)> = guard
            .map
            .iter()
            .map(|(k, slot)| (k.clone(), slot.generation))
            .collect();
        valid.sort_by_key(|(_, generation)| *generation);
        guard.order.clear();
        guard.order.extend(valid);
    }
}

#[derive(Clone)]
pub struct SessionCache {
    directory_listings: Arc<BoundedByteLru<String, StampedValue<DirectoryListing>>>,
    entry_info: Arc<BoundedByteLru<String, StampedValue<FileEntry>>>,
    file_metadata: Arc<BoundedByteLru<String, StampedValue<FileMetadata>>>,
    image_metadata: Arc<BoundedByteLru<String, StampedValue<ImageMetadata>>>,
    folder_metrics: Arc<BoundedByteLru<String, StampedValue<FolderMetrics>>>,
    subdirectories: Arc<BoundedByteLru<String, StampedValue<Vec<TreeNode>>>>,
    thumbnails: Arc<BoundedByteLru<String, StampedValue<Vec<u8>>>>,
}

impl Default for SessionCache {
    fn default() -> Self {
        Self::new()
    }
}

impl SessionCache {
    pub fn new() -> Self {
        Self {
            directory_listings: Arc::new(BoundedByteLru::new(
                DEFAULT_DIR_LISTING_MAX_ENTRIES,
                DEFAULT_DIR_LISTING_MAX_BYTES,
            )),
            entry_info: Arc::new(BoundedByteLru::new(
                DEFAULT_ENTRY_INFO_MAX_ENTRIES,
                DEFAULT_ENTRY_INFO_MAX_BYTES,
            )),
            file_metadata: Arc::new(BoundedByteLru::new(
                DEFAULT_FILE_METADATA_MAX_ENTRIES,
                DEFAULT_FILE_METADATA_MAX_BYTES,
            )),
            image_metadata: Arc::new(BoundedByteLru::new(
                DEFAULT_IMAGE_METADATA_MAX_ENTRIES,
                DEFAULT_IMAGE_METADATA_MAX_BYTES,
            )),
            folder_metrics: Arc::new(BoundedByteLru::new(
                DEFAULT_FOLDER_METRICS_MAX_ENTRIES,
                DEFAULT_FOLDER_METRICS_MAX_BYTES,
            )),
            subdirectories: Arc::new(BoundedByteLru::new(
                DEFAULT_SUBDIRS_MAX_ENTRIES,
                DEFAULT_SUBDIRS_MAX_BYTES,
            )),
            thumbnails: Arc::new(BoundedByteLru::new(
                DEFAULT_THUMBNAILS_MAX_ENTRIES,
                DEFAULT_THUMBNAILS_MAX_BYTES,
            )),
        }
    }

    pub fn normalize_key(path: &str) -> String {
        let mut normalized = path.trim().replace('/', "\\");
        while normalized.len() > 3 && normalized.ends_with('\\') {
            normalized.pop();
        }
        normalized.to_lowercase()
    }

    fn parent_key(normalized_key: &str) -> Option<String> {
        let path = Path::new(normalized_key);
        let parent = path.parent()?.to_string_lossy();
        if parent.is_empty() || parent == normalized_key {
            return None;
        }
        Some(Self::normalize_key(&parent))
    }

    pub fn get_directory_listing(&self, path: &str) -> Option<DirectoryListing> {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return None;
        }
        let current_stamp = PathStamp::for_path(Path::new(path))?;
        let cached = self.directory_listings.get(&key)?;
        if cached.stamp == current_stamp {
            Some(cached.value)
        } else {
            self.directory_listings.remove(&key);
            None
        }
    }

    pub fn put_directory_listing(&self, path: &str, listing: DirectoryListing) {
        if listing.is_network {
            return;
        }
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return;
        }
        let Some(stamp) = PathStamp::for_path(Path::new(path)) else {
            return;
        };
        let estimated_bytes = estimate_directory_listing_bytes(&listing);
        self.directory_listings.put(
            key,
            StampedValue {
                stamp,
                value: listing,
            },
            estimated_bytes,
        );
    }

    pub fn get_entry_info(&self, path: &str) -> Option<FileEntry> {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return None;
        }
        let current_stamp = PathStamp::for_path(Path::new(path))?;
        let cached = self.entry_info.get(&key)?;
        if cached.stamp == current_stamp {
            Some(cached.value)
        } else {
            self.entry_info.remove(&key);
            None
        }
    }

    pub fn put_entry_info(&self, path: &str, entry: FileEntry) {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return;
        }
        let Some(stamp) = PathStamp::for_path(Path::new(path)) else {
            return;
        };
        let bytes = estimate_file_entry_bytes(&entry);
        self.entry_info.put(
            key,
            StampedValue {
                stamp,
                value: entry,
            },
            bytes,
        );
    }

    pub fn get_file_metadata(&self, path: &str) -> Option<FileMetadata> {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return None;
        }
        let current_stamp = PathStamp::for_path(Path::new(path))?;
        let cached = self.file_metadata.get(&key)?;
        if cached.stamp == current_stamp {
            Some(cached.value)
        } else {
            self.file_metadata.remove(&key);
            None
        }
    }

    pub fn put_file_metadata(&self, path: &str, metadata: FileMetadata) {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return;
        }
        let Some(stamp) = PathStamp::for_path(Path::new(path)) else {
            return;
        };
        let bytes = estimate_file_metadata_bytes(&metadata);
        self.file_metadata.put(
            key,
            StampedValue {
                stamp,
                value: metadata,
            },
            bytes,
        );
    }

    pub fn get_image_metadata(&self, path: &str) -> Option<ImageMetadata> {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return None;
        }
        let current_stamp = PathStamp::for_path(Path::new(path))?;
        let cached = self.image_metadata.get(&key)?;
        if cached.stamp == current_stamp {
            Some(cached.value)
        } else {
            self.image_metadata.remove(&key);
            None
        }
    }

    pub fn put_image_metadata(&self, path: &str, metadata: ImageMetadata) {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return;
        }
        let Some(stamp) = PathStamp::for_path(Path::new(path)) else {
            return;
        };
        let bytes = estimate_image_metadata_bytes(&metadata);
        self.image_metadata.put(
            key,
            StampedValue {
                stamp,
                value: metadata,
            },
            bytes,
        );
    }

    pub fn get_folder_metrics(&self, path: &str) -> Option<FolderMetrics> {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return None;
        }
        let current_stamp = PathStamp::for_path(Path::new(path))?;
        let cached = self.folder_metrics.get(&key)?;
        if cached.stamp == current_stamp {
            Some(cached.value)
        } else {
            self.folder_metrics.remove(&key);
            None
        }
    }

    pub fn put_folder_metrics(&self, path: &str, metrics: FolderMetrics) {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return;
        }
        let Some(stamp) = PathStamp::for_path(Path::new(path)) else {
            return;
        };
        let bytes = estimate_folder_metrics_bytes(&metrics);
        self.folder_metrics.put(
            key,
            StampedValue {
                stamp,
                value: metrics,
            },
            bytes,
        );
    }

    pub fn get_subdirectories(&self, path: &str) -> Option<Vec<TreeNode>> {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return None;
        }
        let current_stamp = PathStamp::for_path(Path::new(path))?;
        let cached = self.subdirectories.get(&key)?;
        if cached.stamp == current_stamp {
            Some(cached.value)
        } else {
            self.subdirectories.remove(&key);
            None
        }
    }

    pub fn put_subdirectories(&self, path: &str, nodes: Vec<TreeNode>) {
        let key = Self::normalize_key(path);
        if key.is_empty() {
            return;
        }
        let Some(stamp) = PathStamp::for_path(Path::new(path)) else {
            return;
        };
        let bytes = nodes
            .iter()
            .map(estimate_tree_node_bytes)
            .sum::<usize>()
            .saturating_add(64);
        self.subdirectories.put(
            key,
            StampedValue {
                stamp,
                value: nodes,
            },
            bytes,
        );
    }

    fn thumbnail_key(path: &str, size: Option<u32>) -> String {
        format!("{}\u{1f}{}", Self::normalize_key(path), size.unwrap_or(0))
    }

    fn extract_thumbnail_path(cache_key: &str) -> &str {
        cache_key
            .split_once('\u{1f}')
            .map(|(path, _)| path)
            .unwrap_or(cache_key)
    }

    pub fn get_thumbnail(&self, path: &str, size: Option<u32>) -> Option<Vec<u8>> {
        let norm_path = Self::normalize_key(path);
        if norm_path.is_empty() {
            return None;
        }
        let current_stamp = PathStamp::for_path(Path::new(path))?;
        let key = Self::thumbnail_key(path, size);
        let cached = self.thumbnails.get(&key)?;
        if cached.stamp == current_stamp {
            Some(cached.value)
        } else {
            self.thumbnails.remove(&key);
            None
        }
    }

    pub fn put_thumbnail(&self, path: &str, size: Option<u32>, bytes: Vec<u8>) {
        let norm_path = Self::normalize_key(path);
        if norm_path.is_empty() {
            return;
        }
        let Some(stamp) = PathStamp::for_path(Path::new(path)) else {
            return;
        };
        let key = Self::thumbnail_key(path, size);
        let estimated = bytes.len().saturating_add(key.len()).saturating_add(64);
        self.thumbnails.put(
            key,
            StampedValue {
                stamp,
                value: bytes,
            },
            estimated,
        );
    }

    /// Selectively invalidates cached entries affected by a mutation or watcher event at `path`.
    /// - Evicts exact path and descendant subtree entries across all caches.
    /// - Evicts the parent directory from `directory_listings` and `subdirectories`.
    /// - Evicts all ancestor directories from `folder_metrics` since recursive size/count changed.
    pub fn invalidate_path(&self, path: &str) {
        let target = Self::normalize_key(path);
        if target.is_empty() {
            return;
        }
        let parent = Self::parent_key(&target);

        self.directory_listings.remove_where(|key, _| {
            path_is_same_or_descendant(key, &target)
                || parent
                    .as_deref()
                    .is_some_and(|parent_key| key == parent_key)
        });

        self.subdirectories.remove_where(|key, _| {
            path_is_same_or_descendant(key, &target)
                || parent
                    .as_deref()
                    .is_some_and(|parent_key| key == parent_key)
        });

        self.entry_info
            .remove_where(|key, _| path_is_same_or_descendant(key, &target));
        self.file_metadata
            .remove_where(|key, _| path_is_same_or_descendant(key, &target));
        self.image_metadata
            .remove_where(|key, _| path_is_same_or_descendant(key, &target));
        self.thumbnails.remove_where(|key, _| {
            path_is_same_or_descendant(Self::extract_thumbnail_path(key), &target)
        });

        // Recursive folder metrics are affected if the cached folder is `target`,
        // a descendant of `target`, OR an ancestor of `target`.
        self.folder_metrics.remove_where(|key, _| {
            path_is_same_or_descendant(key, &target) || path_is_same_or_descendant(&target, key)
        });
    }

    pub fn invalidate_paths<I, S>(&self, paths: I)
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        for path in paths {
            self.invalidate_path(path.as_ref());
        }
    }

    pub fn clear_all(&self) {
        self.directory_listings.clear();
        self.entry_info.clear();
        self.file_metadata.clear();
        self.image_metadata.clear();
        self.folder_metrics.clear();
        self.subdirectories.clear();
        self.thumbnails.clear();
    }

    pub fn directory_listing_stats(&self) -> LruStats {
        self.directory_listings.stats()
    }

    pub fn entry_info_stats(&self) -> LruStats {
        self.entry_info.stats()
    }

    pub fn file_metadata_stats(&self) -> LruStats {
        self.file_metadata.stats()
    }

    pub fn image_metadata_stats(&self) -> LruStats {
        self.image_metadata.stats()
    }

    pub fn folder_metrics_stats(&self) -> LruStats {
        self.folder_metrics.stats()
    }
}

fn path_is_same_or_descendant(candidate: &str, root: &str) -> bool {
    if candidate == root {
        return true;
    }
    let Some(rest) = candidate.strip_prefix(root) else {
        return false;
    };
    root.ends_with('\\') || rest.starts_with('\\')
}

fn estimate_file_entry_bytes(entry: &FileEntry) -> usize {
    std::mem::size_of::<FileEntry>()
        .saturating_add(entry.name.len())
        .saturating_add(entry.path.len())
        .saturating_add(entry.modified.len())
        .saturating_add(entry.extension.len())
        .saturating_add(entry.permissions.as_ref().map_or(0, String::len))
        .saturating_add(entry.symlink_target.as_ref().map_or(0, String::len))
        .saturating_add(entry.git_status.as_ref().map_or(0, String::len))
}

fn estimate_directory_listing_bytes(listing: &DirectoryListing) -> usize {
    let entries_bytes: usize = listing.entries.iter().map(estimate_file_entry_bytes).sum();
    std::mem::size_of::<DirectoryListing>()
        .saturating_add(listing.path.len())
        .saturating_add(listing.parent.as_ref().map_or(0, String::len))
        .saturating_add(entries_bytes)
}

fn estimate_file_metadata_bytes(metadata: &FileMetadata) -> usize {
    let fields_bytes: usize = metadata
        .fields
        .iter()
        .map(|(k, v)| k.len().saturating_add(v.len()).saturating_add(32))
        .sum();
    std::mem::size_of::<FileMetadata>()
        .saturating_add(metadata.kind.len())
        .saturating_add(metadata.summary.as_ref().map_or(0, String::len))
        .saturating_add(fields_bytes)
}

fn estimate_image_metadata_bytes(metadata: &ImageMetadata) -> usize {
    let exif_bytes: usize = metadata
        .exif
        .iter()
        .map(|(k, v)| k.len().saturating_add(v.len()).saturating_add(32))
        .sum();
    std::mem::size_of::<ImageMetadata>().saturating_add(exif_bytes)
}

fn estimate_tree_node_bytes(node: &TreeNode) -> usize {
    let children_bytes: usize = node.children.iter().map(estimate_tree_node_bytes).sum();
    std::mem::size_of::<TreeNode>()
        .saturating_add(node.name.len())
        .saturating_add(node.path.len())
        .saturating_add(children_bytes)
}

fn estimate_folder_metrics_bytes(metrics: &FolderMetrics) -> usize {
    let subdirs_bytes: usize = metrics
        .subdirectories
        .iter()
        .map(estimate_tree_node_bytes)
        .sum();
    std::mem::size_of::<FolderMetrics>().saturating_add(subdirs_bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::SystemTime;

    fn unique_temp_dir(label: &str) -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "simplefile-session-cache-{label}-{}-{nanos}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn bounded_byte_lru_evicts_least_recently_used_by_byte_budget() {
        let cache = BoundedByteLru::<String, Vec<u8>>::new(16, 100);
        cache.put("a".to_string(), vec![1; 40], 40);
        cache.put("b".to_string(), vec![2; 40], 40);
        // Touch "a" so "b" is oldest
        assert!(cache.get(&"a".to_string()).is_some());
        // Adding "c" (40 bytes) exceeds 100-byte budget -> "b" must be evicted
        cache.put("c".to_string(), vec![3; 40], 40);

        assert!(cache.get(&"a".to_string()).is_some());
        assert!(cache.get(&"b".to_string()).is_none());
        assert!(cache.get(&"c".to_string()).is_some());
        let stats = cache.stats();
        assert_eq!(stats.count, 2);
        assert_eq!(stats.estimated_bytes, 80);
        assert_eq!(stats.evictions, 1);
    }

    #[test]
    fn session_cache_selectively_invalidates_parent_listing_and_ancestor_metrics() {
        let root = unique_temp_dir("invalidate");
        let sub = root.join("sub");
        let nested = sub.join("nested");
        fs::create_dir_all(&nested).unwrap();
        let file = nested.join("note.txt");
        fs::write(&file, b"hello").unwrap();

        let sibling = root.join("sibling");
        fs::create_dir_all(&sibling).unwrap();

        let cache = SessionCache::new();
        let root_str = root.to_string_lossy().to_string();
        let sub_str = sub.to_string_lossy().to_string();
        let nested_str = nested.to_string_lossy().to_string();
        let file_str = file.to_string_lossy().to_string();
        let sibling_str = sibling.to_string_lossy().to_string();

        cache.put_directory_listing(
            &nested_str,
            DirectoryListing {
                path: nested_str.clone(),
                parent: Some(sub_str.clone()),
                entries: vec![],
                is_network: false,
            },
        );
        cache.put_directory_listing(
            &sibling_str,
            DirectoryListing {
                path: sibling_str.clone(),
                parent: Some(root_str.clone()),
                entries: vec![],
                is_network: false,
            },
        );
        cache.put_folder_metrics(
            &root_str,
            FolderMetrics {
                size: 5,
                item_count: 1,
                subdirectories: vec![],
            },
        );
        cache.put_folder_metrics(
            &sub_str,
            FolderMetrics {
                size: 5,
                item_count: 1,
                subdirectories: vec![],
            },
        );
        cache.put_folder_metrics(
            &sibling_str,
            FolderMetrics {
                size: 0,
                item_count: 0,
                subdirectories: vec![],
            },
        );
        cache.put_file_metadata(
            &file_str,
            FileMetadata {
                kind: "text".to_string(),
                summary: None,
                fields: vec![],
            },
        );

        // Mutating `root/sub/nested/note.txt` should invalidate:
        // - `file_metadata` for `note.txt`
        // - `directory_listings` for `nested` (its parent)
        // - `folder_metrics` for `sub` and `root` (its ancestors)
        // While keeping `sibling` directory listing and `sibling` folder metrics intact!
        cache.invalidate_path(&file_str);

        assert!(cache.get_file_metadata(&file_str).is_none());
        assert!(cache.get_directory_listing(&nested_str).is_none());
        assert!(cache.get_folder_metrics(&sub_str).is_none());
        assert!(cache.get_folder_metrics(&root_str).is_none());

        assert!(cache.get_directory_listing(&sibling_str).is_some());
        assert!(cache.get_folder_metrics(&sibling_str).is_some());

        let _ = fs::remove_dir_all(&root);
    }
}
