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
use std::time::SystemTime;

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
    let root = cache_root();
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
    let cached = cache_path(&key);
    if let Some(parent) = cached.parent() {
        let _ = fs::create_dir_all(parent);
    }
    // Write atomically: write to a temp file then rename to prevent partial reads.
    let temp = cached.with_extension("tmp");
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
    let root = cache_root();
    let current = total_size_in(&root);
    if current <= max_bytes {
        return;
    }

    // Collect all cache files with their modification time and size.
    let mut files: Vec<(PathBuf, SystemTime, u64)> = Vec::new();
    collect_files(&root, &mut files);

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

/// Clears the entire thumbnail cache.
pub fn clear() {
    let root = cache_root();
    let _ = fs::remove_dir_all(&root);
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
