//! Disk-backed thumbnail cache to avoid re-generating thumbnails on every visit.
//!
//! Cache key: SHA-256(canonical_path + ":" + file_modified_time + ":" + file_size + ":" + thumb_size)
//! Layout: `%LOCALAPPDATA%\SumaFile\thumbnail-cache\<hex[0..2]>\<hex[2..4]>\<hex>.jpg`
//!
//! The cache stores raw JPEG bytes (no Base64). On cache hit, the thumbnail is
//! served in ~1ms from a disk read instead of ~50ms from image decode + resize.

use sha2::{Digest, Sha256};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Written at the cache root the first time a thumbnail is stored. Eviction
/// and clearing refuse to touch a folder without it, so a cache path pointed
/// at the user's own files never deletes them.
const CACHE_MARKER: &str = ".sumafile-thumbnail-cache";

/// Eviction walks the whole cache; run it at most this often.
const EVICTION_INTERVAL: Duration = Duration::from_secs(60);

/// Default maximum cache size in bytes (500 MB).
const DEFAULT_MAX_CACHE_BYTES: u64 = 500 * 1024 * 1024;

/// Settings key for the user-configurable maximum cache size in MB.
/// Set to 0 to disable the thumbnail cache entirely.
const SETTING_MAX_KEY: &str = "thumbnailCacheMaxMb";

/// Settings key for the user-configurable cache directory path.
/// When empty or unset, defaults to `%LOCALAPPDATA%\SumaFile\thumbnail-cache`.
const SETTING_PATH_KEY: &str = "thumbnailCachePath";

/// Returns the user-configured max cache size in bytes, or the default (500 MB).
/// A value of 0 means the cache is disabled.
fn configured_max_bytes() -> u64 {
    match crate::settings_store::get_db_setting(SETTING_MAX_KEY.to_string()) {
        Ok(Some(value)) => value
            .parse::<u64>()
            .map(|mb| mb * 1024 * 1024)
            .unwrap_or(DEFAULT_MAX_CACHE_BYTES),
        _ => DEFAULT_MAX_CACHE_BYTES,
    }
}

/// Returns true if the thumbnail cache is enabled (max size > 0).
pub fn is_enabled() -> bool {
    configured_max_bytes() > 0
}

/// Returns the default cache root directory.
fn default_cache_root() -> PathBuf {
    let local_app_data = std::env::var("LOCALAPPDATA")
        .unwrap_or_else(|_| std::env::temp_dir().to_string_lossy().to_string());
    PathBuf::from(local_app_data)
        .join("SumaFile")
        .join("thumbnail-cache")
}

/// Returns the thumbnail cache root directory, respecting the user's
/// configured path if set.
fn cache_root() -> PathBuf {
    match crate::settings_store::get_db_setting(SETTING_PATH_KEY.to_string()) {
        Ok(Some(value)) if !value.trim().is_empty() => PathBuf::from(value.trim()),
        _ => default_cache_root(),
    }
}

/// Computes the cache key for a given file path, modification time, file size,
/// and requested thumbnail size.
fn cache_key(path: &str, modified: SystemTime, size: u64, thumb_size: u32) -> String {
    let modified_epoch = modified
        .duration_since(SystemTime::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);

    let mut hasher = Sha256::new();
    hasher.update(path.as_bytes());
    hasher.update(b":");
    hasher.update(modified_epoch.to_le_bytes());
    hasher.update(b":");
    hasher.update(size.to_le_bytes());
    hasher.update(b":");
    hasher.update(thumb_size.to_le_bytes());
    let hash = hasher.finalize();
    hex::encode(hash)
}

/// Returns the cache file path for a given hex key.
fn cache_path(key: &str) -> PathBuf {
    cache_path_in(&cache_root(), key)
}

fn cache_path_in(root: &Path, key: &str) -> PathBuf {
    // Use first 2 hex chars as first directory, next 2 as second directory.
    let (a, rest) = key.split_at(2.min(key.len()));
    let (b, _) = rest.split_at(2.min(rest.len()));
    root.join(a).join(b).join(format!("{key}.jpg"))
}

/// Attempts to read a cached thumbnail. Returns `Some(jpeg_bytes)` on hit.
pub fn get(path: &str, modified: SystemTime, size: u64, thumb_size: u32) -> Option<Vec<u8>> {
    let key = cache_key(path, modified, size, thumb_size);
    let cached = cache_path(&key);
    fs::read(&cached).ok()
}

/// Stores a thumbnail in the cache. Creates parent directories as needed.
/// If the cache write fails (e.g. disk full), the error is silently ignored.
pub fn put(path: &str, modified: SystemTime, size: u64, thumb_size: u32, jpeg_bytes: &[u8]) {
    let key = cache_key(path, modified, size, thumb_size);
    let root = cache_root();
    let cached = cache_path_in(&root, &key);
    if let Some(parent) = cached.parent() {
        let _ = fs::create_dir_all(parent);
    }
    ensure_marker(&root);
    // Write atomically: a per-call temp name, then rename. A shared `<key>.tmp`
    // let two generators of the same thumbnail truncate each other's file and
    // publish a half-written JPEG.
    let temp = temp_path_for(&cached);
    let written = if let Ok(mut file) = fs::File::create(&temp) {
        file.write_all(jpeg_bytes).is_ok()
    } else {
        false
    };
    if written {
        let _ = fs::rename(&temp, &cached);
    } else {
        let _ = fs::remove_file(&temp);
    }
}

fn temp_path_for(cached: &Path) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let sequence = COUNTER.fetch_add(1, Ordering::Relaxed);
    cached.with_extension(format!("{}.{sequence}.tmp", std::process::id()))
}

fn ensure_marker(root: &Path) {
    let marker = root.join(CACHE_MARKER);
    if !marker.exists() {
        let _ = fs::create_dir_all(root);
        let _ = fs::write(
            marker,
            b"SumaFile thumbnail cache. Files here are disposable.\n",
        );
    }
}

/// A folder is ours to evict from when it carries the marker, or when it is
/// still empty or missing (nothing to lose yet).
fn is_managed_cache_root(root: &Path) -> bool {
    if root.join(CACHE_MARKER).exists() {
        return true;
    }
    match fs::read_dir(root) {
        Ok(mut entries) => entries.next().is_none(),
        Err(_) => true,
    }
}

/// Returns the total size of the thumbnail cache in bytes.
pub fn total_size() -> u64 {
    total_size_in(&cache_root())
}

fn total_size_in(root: &Path) -> u64 {
    let mut total: u64 = 0;
    if let Ok(walker) = fs::read_dir(root) {
        for entry in walker.flatten() {
            let meta = match entry.metadata() {
                Ok(m) => m,
                Err(_) => continue,
            };
            if meta.is_dir() {
                total += total_size_in(&entry.path());
            } else if meta.is_file() {
                total += meta.len();
            }
        }
    }
    total
}

/// Evicts oldest cache entries until total size is under `max_bytes`.
/// Uses file modification time as the LRU proxy (last write = creation time for
/// cache entries since they are write-once).
pub fn evict_if_needed(max_bytes: u64) {
    evict_if_needed_in(&cache_root(), max_bytes);
}

fn evict_if_needed_in(root: &Path, max_bytes: u64) {
    if !is_managed_cache_root(root) {
        log::warn!(
            "Thumbnail cache path {} is not a SumaFile cache folder; refusing to evict from it",
            root.display()
        );
        return;
    }
    let current = total_size_in(root);
    if current <= max_bytes {
        return;
    }

    // Collect all cache files with their modification time and size.
    let mut files: Vec<(PathBuf, SystemTime, u64)> = Vec::new();
    collect_files(root, &mut files);
    files.retain(|(path, _, _)| path.file_name().is_none_or(|name| name != CACHE_MARKER));

    // Sort oldest first.
    files.sort_by_key(|(_, mtime, _)| *mtime);

    let mut freed: u64 = 0;
    let target = current.saturating_sub(max_bytes);
    for (path, _, size) in &files {
        if freed >= target {
            break;
        }
        if fs::remove_file(path).is_ok() {
            freed += size;
        }
    }
}

/// Evicts with the user-configured maximum cache size.
pub fn evict_default() {
    let max = configured_max_bytes();
    if max > 0 {
        evict_if_needed(max);
    }
}

static EVICTION_RUNNING: AtomicBool = AtomicBool::new(false);
static LAST_EVICTION_SECS: AtomicU64 = AtomicU64::new(0);

/// Decide whether an eviction pass should start now: at most one at a time and
/// at most once per `EVICTION_INTERVAL`. The caller that gets `true` owns the
/// pass and must call `finish_eviction` afterwards.
fn should_run_eviction(now: SystemTime) -> bool {
    let now_secs = now
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_secs())
        .unwrap_or(0);
    let last = LAST_EVICTION_SECS.load(Ordering::Acquire);
    if last != 0 && now_secs.saturating_sub(last) < EVICTION_INTERVAL.as_secs() {
        return false;
    }
    if EVICTION_RUNNING
        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
        .is_err()
    {
        return false;
    }
    LAST_EVICTION_SECS.store(now_secs, Ordering::Release);
    true
}

fn finish_eviction() {
    EVICTION_RUNNING.store(false, Ordering::Release);
}

/// Run eviction in the background if it is due. Generating a folder of
/// thumbnails used to start one full cache walk per thumbnail.
pub fn schedule_eviction() {
    if !should_run_eviction(SystemTime::now()) {
        return;
    }
    let spawned = std::thread::Builder::new()
        .name("thumb-cache-evict".into())
        .spawn(|| {
            evict_default();
            finish_eviction();
        });
    if spawned.is_err() {
        finish_eviction();
    }
}

fn collect_files(dir: &Path, out: &mut Vec<(PathBuf, SystemTime, u64)>) {
    if let Ok(walker) = fs::read_dir(dir) {
        for entry in walker.flatten() {
            let meta = match entry.metadata() {
                Ok(m) => m,
                Err(_) => continue,
            };
            if meta.is_dir() {
                collect_files(&entry.path(), out);
            } else if meta.is_file() {
                let mtime = meta.modified().unwrap_or(SystemTime::UNIX_EPOCH);
                out.push((entry.path(), mtime, meta.len()));
            }
        }
    }
}

/// Clears the entire thumbnail cache. Refuses a folder that is not ours.
pub fn clear() {
    clear_in(&cache_root());
}

fn clear_in(root: &Path) {
    if !is_managed_cache_root(root) {
        log::warn!(
            "Thumbnail cache path {} is not a SumaFile cache folder; refusing to clear it",
            root.display()
        );
        return;
    }
    let _ = fs::remove_dir_all(root);
}

// A simple inline hex encoder to avoid adding the `hex` crate.
mod hex {
    pub fn encode(bytes: impl AsRef<[u8]>) -> String {
        bytes.as_ref().iter().fold(
            String::with_capacity(bytes.as_ref().len() * 2),
            |mut s, b| {
                use std::fmt::Write;
                let _ = write!(s, "{b:02x}");
                s
            },
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cache_key_is_deterministic() {
        let t = SystemTime::UNIX_EPOCH;
        let k1 = cache_key(r"C:\test.jpg", t, 1234, 128);
        let k2 = cache_key(r"C:\test.jpg", t, 1234, 128);
        assert_eq!(k1, k2);
        assert_eq!(k1.len(), 64); // SHA-256 hex = 64 chars
    }

    #[test]
    fn cache_key_differs_on_size() {
        let t = SystemTime::UNIX_EPOCH;
        let k1 = cache_key(r"C:\test.jpg", t, 1234, 128);
        let k2 = cache_key(r"C:\test.jpg", t, 5678, 128);
        assert_ne!(k1, k2);
    }

    #[test]
    fn cache_key_differs_on_thumb_size() {
        let t = SystemTime::UNIX_EPOCH;
        let k1 = cache_key(r"C:\test.jpg", t, 1234, 128);
        let k2 = cache_key(r"C:\test.jpg", t, 1234, 256);
        assert_ne!(k1, k2);
    }

    #[test]
    fn cache_path_has_subdirectories() {
        let key = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        let path = cache_path(key);
        let _components: Vec<_> = path.components().collect();
        // Should contain .../ab/cd/<key>.jpg
        let filename = path.file_name().unwrap().to_string_lossy();
        assert!(filename.ends_with(".jpg"));
        assert!(filename.starts_with("ab"));
        let parent = path
            .parent()
            .unwrap()
            .file_name()
            .unwrap()
            .to_string_lossy();
        assert_eq!(parent, "cd");
    }

    #[test]
    fn put_and_get_roundtrip() {
        let _lock = crate::test_support::env_lock().lock().unwrap();
        let jpeg_data = b"fake jpeg bytes for test";
        let t = SystemTime::UNIX_EPOCH;
        let path = r"C:\test-roundtrip-thumb-cache.jpg";

        put(path, t, 999, 128, jpeg_data);
        let cached = get(path, t, 999, 128);

        assert_eq!(cached.as_deref(), Some(jpeg_data.as_slice()));

        // Clean up.
        let key = cache_key(path, t, 999, 128);
        let _ = fs::remove_file(cache_path(&key));
    }

    #[test]
    fn get_returns_none_on_miss() {
        let t = SystemTime::UNIX_EPOCH;
        let cached = get(r"C:\nonexistent-thumb-cache-test.jpg", t, 0, 128);
        assert!(cached.is_none());
    }
}

#[cfg(test)]
mod safety_tests {
    use super::{
        clear_in, evict_if_needed_in, is_managed_cache_root, should_run_eviction, temp_path_for,
        CACHE_MARKER,
    };
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::time::{Duration, SystemTime};

    fn scratch(label: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "sumafile-thumb-safety-{label}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(SystemTime::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    /// Two generators of the same thumbnail must never share a temp file.
    #[test]
    fn temp_paths_are_unique_per_write() {
        let cached = Path::new("C:/cache/ab/cd/abcd.jpg");
        let first = temp_path_for(cached);
        let second = temp_path_for(cached);
        assert_ne!(first, second);
        assert!(first.to_string_lossy().ends_with(".tmp"));
    }

    /// A cache path pointed at the user's own folder must never have its
    /// oldest files deleted or be wiped.
    #[test]
    fn eviction_and_clear_refuse_folders_without_the_marker() {
        let root = scratch("unmarked");
        fs::write(root.join("holiday.jpg"), vec![1u8; 4096]).unwrap();
        assert!(!is_managed_cache_root(&root));

        evict_if_needed_in(&root, 0);
        assert!(root.join("holiday.jpg").exists());
        clear_in(&root);
        assert!(root.join("holiday.jpg").exists());

        fs::write(root.join(CACHE_MARKER), b"marker").unwrap();
        assert!(is_managed_cache_root(&root));
        evict_if_needed_in(&root, 0);
        assert!(!root.join("holiday.jpg").exists());
        assert!(
            root.join(CACHE_MARKER).exists(),
            "the marker itself survives eviction"
        );
        clear_in(&root);
        assert!(!root.exists());
    }

    #[test]
    fn eviction_runs_at_most_once_per_interval() {
        let now = SystemTime::UNIX_EPOCH + Duration::from_secs(1_700_000_000);
        // Reset shared state from other tests by moving far ahead in time.
        let later = now + Duration::from_secs(10_000);
        assert!(should_run_eviction(later));
        super::finish_eviction();
        assert!(!should_run_eviction(later + Duration::from_secs(5)));
        assert!(should_run_eviction(later + Duration::from_secs(120)));
        super::finish_eviction();
    }
}
