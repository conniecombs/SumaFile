//! Disk cleanup and duplicate-file scans used by the WinUI IPC service.

use crate::models::{
    CleanupFile, CleanupResult, DuplicateCheckFile, DuplicateCheckGroup, DuplicateCheckResult,
    DuplicateGroup,
};
use crate::utils::{
    format_system_time, hex_encode, is_network_path, validate_existing_path_no_resolve,
};
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::fs;
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};

pub const DEFAULT_LARGE_FILE_THRESHOLD: u64 = 100 * 1024 * 1024;
pub const DEFAULT_DUPLICATE_MIN_SIZE: u64 = 1;
pub const DEFAULT_PARTIAL_HASH_BYTES: u64 = 1024 * 1024;
pub const NETWORK_DUPLICATE_MIN_SIZE: u64 = 64 * 1024;
pub const NETWORK_PARTIAL_HASH_BYTES: u64 = 256 * 1024;
pub const MAX_PARTIAL_HASH_BYTES: u64 = 16 * 1024 * 1024;
const FIRST_PASS_PROGRESS_INTERVAL: u64 = 128;
const HASH_PROGRESS_INTERVAL: u64 = 16;
const LOCAL_HASH_BUFFER_BYTES: usize = 64 * 1024;
const NETWORK_HASH_BUFFER_BYTES: usize = 1024 * 1024;

#[derive(Debug, Clone, Default)]
pub struct DuplicateScanOptions {
    pub min_size: Option<u64>,
    pub partial_hash_bytes: Option<u64>,
    pub max_depth: Option<usize>,
    pub exclude_patterns: Vec<String>,
    pub network_mode: Option<bool>,
}

impl DuplicateScanOptions {
    pub fn from_params(
        min_size: Option<u64>,
        partial_hash_bytes: Option<u64>,
        max_depth: Option<usize>,
        exclude_patterns: Vec<String>,
        network_mode: Option<bool>,
    ) -> Self {
        Self {
            min_size,
            partial_hash_bytes,
            max_depth,
            exclude_patterns,
            network_mode,
        }
    }
}

struct ResolvedDuplicateScanOptions {
    min_size: u64,
    partial_hash_bytes: u64,
    max_depth: Option<usize>,
    exclude_matcher: ExcludeMatcher,
    hash_buffer_bytes: usize,
}

impl DuplicateScanOptions {
    fn resolve_for_root(self, root: &Path) -> ResolvedDuplicateScanOptions {
        let network_mode = self.network_mode.unwrap_or_else(|| is_network_path(root));
        let min_size = self.min_size.unwrap_or(if network_mode {
            NETWORK_DUPLICATE_MIN_SIZE
        } else {
            DEFAULT_DUPLICATE_MIN_SIZE
        });
        let partial_hash_bytes = self
            .partial_hash_bytes
            .unwrap_or(if network_mode {
                NETWORK_PARTIAL_HASH_BYTES
            } else {
                DEFAULT_PARTIAL_HASH_BYTES
            })
            .clamp(4096, MAX_PARTIAL_HASH_BYTES);
        ResolvedDuplicateScanOptions {
            min_size,
            partial_hash_bytes,
            max_depth: self.max_depth,
            exclude_matcher: ExcludeMatcher::new(self.exclude_patterns),
            hash_buffer_bytes: if network_mode {
                NETWORK_HASH_BUFFER_BYTES
            } else {
                LOCAL_HASH_BUFFER_BYTES
            },
        }
    }
}

#[derive(Clone)]
struct DuplicateCandidate {
    path: PathBuf,
    size: u64,
    modified: String,
}

pub fn scan_duplicate_check(
    directory: &str,
    options: DuplicateScanOptions,
    cancel: &AtomicBool,
    mut progress: impl FnMut(u64, u64, &str),
) -> Result<DuplicateCheckResult, String> {
    let root = validate_scan_directory(directory)?;
    let ResolvedDuplicateScanOptions {
        min_size,
        partial_hash_bytes,
        max_depth,
        exclude_matcher,
        hash_buffer_bytes,
    } = options.resolve_for_root(&root);
    check_cancelled(cancel)?;

    let mut scanned_files = 0u64;
    let mut skipped_files = 0u64;
    let mut errors = Vec::new();
    let mut size_map: HashMap<u64, Vec<DuplicateCandidate>> = HashMap::new();

    progress(0, 0, "Walking files");

    walk_files(
        &root,
        ScanWalkOptions {
            max_depth,
            exclude_matcher,
        },
        cancel,
        |path, error| {
            skipped_files += 1;
            errors.push(format!("{}: {error}", path.display()));
        },
        |path, metadata| -> Result<(), String> {
            scanned_files += 1;
            if scanned_files == 1 || scanned_files.is_multiple_of(FIRST_PASS_PROGRESS_INTERVAL) {
                progress(scanned_files, 0, &path.to_string_lossy());
            }

            let size = metadata.len();
            if size < min_size {
                return Ok(());
            }

            let modified = metadata
                .modified()
                .map(format_system_time)
                .unwrap_or_else(|_| "-".to_string());
            size_map.entry(size).or_default().push(DuplicateCandidate {
                path,
                size,
                modified,
            });
            Ok(())
        },
    )?;

    let candidate_files: u64 = size_map
        .values()
        .filter(|files| files.len() > 1)
        .map(|files| files.len() as u64)
        .sum();

    if candidate_files == 0 {
        return Ok(DuplicateCheckResult {
            groups: Vec::new(),
            scanned_files,
            candidate_files,
            hashed_files: 0,
            skipped_files,
            errors,
            total_reclaimable_bytes: 0,
        });
    }

    let mut partial_checked = 0u64;
    let mut full_hash_candidates: Vec<Vec<DuplicateCandidate>> = Vec::new();
    for files in size_map.into_values() {
        check_cancelled(cancel)?;
        if files.len() < 2 {
            continue;
        }

        let mut partial_map: HashMap<String, Vec<DuplicateCandidate>> = HashMap::new();
        for candidate in files {
            check_cancelled(cancel)?;
            partial_checked += 1;
            if partial_checked == 1 || partial_checked.is_multiple_of(HASH_PROGRESS_INTERVAL) {
                progress(
                    partial_checked,
                    candidate_files.saturating_mul(2),
                    &candidate.path.to_string_lossy(),
                );
            }

            match compute_partial_sha256(
                &candidate.path,
                partial_hash_bytes,
                hash_buffer_bytes,
                cancel,
            ) {
                Ok(partial_hash) => {
                    partial_map.entry(partial_hash).or_default().push(candidate);
                }
                Err(error) if error.kind() == io::ErrorKind::Interrupted => {
                    return Err("cancelled".to_string());
                }
                Err(error) => {
                    skipped_files += 1;
                    errors.push(format!("{}: {error}", candidate.path.display()));
                }
            }
        }

        full_hash_candidates.extend(partial_map.into_values().filter(|group| group.len() > 1));
    }

    let full_total: u64 = full_hash_candidates
        .iter()
        .map(|group| group.len() as u64)
        .sum();
    let progress_offset = candidate_files;
    let progress_total = progress_offset.saturating_add(full_total);
    let mut hashed_files = 0u64;
    let mut full_hash_map: HashMap<(u64, String), Vec<DuplicateCandidate>> = HashMap::new();

    for group in full_hash_candidates {
        for candidate in group {
            check_cancelled(cancel)?;
            hashed_files += 1;
            if hashed_files == 1 || hashed_files.is_multiple_of(HASH_PROGRESS_INTERVAL) {
                progress(
                    progress_offset.saturating_add(hashed_files),
                    progress_total,
                    &candidate.path.to_string_lossy(),
                );
            }

            match compute_sha256(&candidate.path, hash_buffer_bytes, cancel) {
                Ok(hash) => {
                    full_hash_map
                        .entry((candidate.size, hash))
                        .or_default()
                        .push(candidate);
                }
                Err(error) if error.kind() == io::ErrorKind::Interrupted => {
                    return Err("cancelled".to_string());
                }
                Err(error) => {
                    skipped_files += 1;
                    errors.push(format!("{}: {error}", candidate.path.display()));
                }
            }
        }
    }

    let mut groups: Vec<DuplicateCheckGroup> = full_hash_map
        .into_iter()
        .filter_map(|((size, hash), mut files)| {
            if files.len() < 2 {
                return None;
            }
            files.sort_by(|a, b| a.path.cmp(&b.path));
            let wasted_bytes = size.saturating_mul(files.len().saturating_sub(1) as u64);
            Some(DuplicateCheckGroup {
                id: duplicate_group_id(size, &hash),
                hash,
                size,
                wasted_bytes,
                files: files
                    .into_iter()
                    .map(duplicate_file_from_candidate)
                    .collect(),
            })
        })
        .collect();

    groups.sort_by(|a, b| {
        b.wasted_bytes
            .cmp(&a.wasted_bytes)
            .then_with(|| {
                a.files
                    .first()
                    .map(|file| &file.path)
                    .cmp(&b.files.first().map(|file| &file.path))
            })
            .then_with(|| a.hash.cmp(&b.hash))
    });

    let total_reclaimable_bytes = groups.iter().map(|group| group.wasted_bytes).sum::<u64>();
    progress(progress_total, progress_total, "Duplicate scan complete");

    Ok(DuplicateCheckResult {
        groups,
        scanned_files,
        candidate_files,
        hashed_files,
        skipped_files,
        errors,
        total_reclaimable_bytes,
    })
}

pub fn scan_disk_cleanup(
    directory: &str,
    size_threshold: Option<u64>,
    cancel: &AtomicBool,
    mut progress: impl FnMut(u64, u64, &str),
) -> Result<CleanupResult, String> {
    let root = validate_scan_directory(directory)?;
    let threshold = size_threshold.unwrap_or(DEFAULT_LARGE_FILE_THRESHOLD);
    check_cancelled(cancel)?;

    let mut large_files: Vec<CleanupFile> = Vec::new();
    let mut size_map: HashMap<u64, Vec<PathBuf>> = HashMap::new();
    let mut scanned_files = 0u64;

    progress(0, 0, "Scanning files");

    walk_files(
        &root,
        ScanWalkOptions::default(),
        cancel,
        |_, _| {},
        |path, metadata| -> Result<(), String> {
            scanned_files += 1;
            if scanned_files == 1 || scanned_files.is_multiple_of(FIRST_PASS_PROGRESS_INTERVAL) {
                progress(scanned_files, 0, &path.to_string_lossy());
            }

            let size = metadata.len();
            if size >= threshold {
                large_files.push(CleanupFile {
                    path: path.to_string_lossy().to_string(),
                    size,
                });
            }
            size_map.entry(size).or_default().push(path);
            Ok(())
        },
    )?;

    let duplicate_candidates: u64 = size_map
        .values()
        .filter(|files| files.len() > 1)
        .map(|files| files.len() as u64)
        .sum();

    let mut hashed_files = 0u64;
    let mut duplicates: Vec<DuplicateGroup> = Vec::new();
    for files in size_map.into_values() {
        check_cancelled(cancel)?;
        if files.len() < 2 {
            continue;
        }

        let mut hash_map: HashMap<String, Vec<String>> = HashMap::new();
        for file_path in files {
            check_cancelled(cancel)?;
            hashed_files += 1;
            if hashed_files == 1 || hashed_files.is_multiple_of(HASH_PROGRESS_INTERVAL) {
                progress(
                    hashed_files,
                    duplicate_candidates,
                    &file_path.to_string_lossy(),
                );
            }

            match compute_sha256(&file_path, LOCAL_HASH_BUFFER_BYTES, cancel) {
                Ok(hash) => {
                    hash_map
                        .entry(hash)
                        .or_default()
                        .push(file_path.to_string_lossy().to_string());
                }
                Err(_) => continue,
            }
        }

        for (hash, mut group) in hash_map {
            if group.len() > 1 {
                group.sort();
                duplicates.push(DuplicateGroup { hash, files: group });
            }
        }
    }

    large_files.sort_by(|a, b| b.size.cmp(&a.size).then_with(|| a.path.cmp(&b.path)));
    duplicates.sort_by(|a, b| {
        a.files
            .first()
            .cmp(&b.files.first())
            .then_with(|| a.hash.cmp(&b.hash))
    });

    Ok(CleanupResult {
        large_files,
        duplicates,
        scanned_files,
    })
}

fn validate_scan_directory(directory: &str) -> Result<PathBuf, String> {
    let root = validate_existing_path_no_resolve(directory)?;
    if !root.is_dir() {
        return Err(format!("Path is not a directory: {directory}"));
    }
    Ok(root)
}

#[derive(Default)]
struct ScanWalkOptions {
    max_depth: Option<usize>,
    exclude_matcher: ExcludeMatcher,
}

#[derive(Default)]
struct ExcludeMatcher {
    patterns: Vec<String>,
}

impl ExcludeMatcher {
    fn new(patterns: Vec<String>) -> Self {
        let patterns = patterns
            .into_iter()
            .map(|pattern| normalize_exclude_pattern(&pattern))
            .filter(|pattern| !pattern.is_empty())
            .take(128)
            .collect();
        Self { patterns }
    }

    fn is_excluded(&self, path: &Path, name: &str) -> bool {
        if self.patterns.is_empty() {
            return false;
        }

        let name = name.to_ascii_lowercase();
        let path = normalize_exclude_pattern(&path.to_string_lossy());
        self.patterns
            .iter()
            .any(|pattern| exclude_pattern_matches(pattern, &name, &path))
    }
}

fn normalize_exclude_pattern(pattern: &str) -> String {
    pattern
        .trim()
        .replace('\\', "/")
        .to_ascii_lowercase()
        .chars()
        .take(256)
        .collect()
}

fn exclude_pattern_matches(pattern: &str, name: &str, path: &str) -> bool {
    if pattern.contains('*') || pattern.contains('?') {
        return wildcard_matches(pattern, name) || wildcard_matches(pattern, path);
    }

    if pattern.contains('/') {
        path.contains(pattern)
    } else {
        name == pattern || path.split('/').any(|part| part == pattern)
    }
}

fn wildcard_matches(pattern: &str, text: &str) -> bool {
    let pattern = pattern.as_bytes();
    let text = text.as_bytes();
    let mut pattern_index = 0usize;
    let mut text_index = 0usize;
    let mut star_index = None;
    let mut match_index = 0usize;

    while text_index < text.len() {
        if pattern_index < pattern.len()
            && (pattern[pattern_index] == b'?' || pattern[pattern_index] == text[text_index])
        {
            pattern_index += 1;
            text_index += 1;
        } else if pattern_index < pattern.len() && pattern[pattern_index] == b'*' {
            star_index = Some(pattern_index);
            match_index = text_index;
            pattern_index += 1;
        } else if let Some(star) = star_index {
            pattern_index = star + 1;
            match_index += 1;
            text_index = match_index;
        } else {
            return false;
        }
    }

    while pattern_index < pattern.len() && pattern[pattern_index] == b'*' {
        pattern_index += 1;
    }

    pattern_index == pattern.len()
}

fn walk_files(
    root: &Path,
    options: ScanWalkOptions,
    cancel: &AtomicBool,
    mut on_error: impl FnMut(&Path, String),
    mut on_file: impl FnMut(PathBuf, fs::Metadata) -> Result<(), String>,
) -> Result<(), String> {
    let mut stack = vec![root.to_path_buf()];
    let mut depths = vec![0usize];

    while let (Some(dir), Some(depth)) = (stack.pop(), depths.pop()) {
        check_cancelled(cancel)?;
        let entries = match fs::read_dir(&dir) {
            Ok(entries) => entries,
            Err(error) => {
                on_error(&dir, format!("{}: {error}", dir.display()));
                continue;
            }
        };

        for entry in entries {
            check_cancelled(cancel)?;
            let entry = match entry {
                Ok(entry) => entry,
                Err(error) => {
                    on_error(&dir, format!("{}: {error}", dir.display()));
                    continue;
                }
            };
            let path = entry.path();
            let name = entry.file_name().to_string_lossy().to_string();
            if options.exclude_matcher.is_excluded(&path, &name) {
                continue;
            }

            let file_type = match entry.file_type() {
                Ok(file_type) => file_type,
                Err(error) => {
                    on_error(&path, format!("{}: {error}", path.display()));
                    continue;
                }
            };

            if file_type.is_symlink() {
                continue;
            }
            if file_type.is_dir() {
                if options.max_depth.is_none_or(|max_depth| depth < max_depth) {
                    stack.push(path);
                    depths.push(depth + 1);
                }
            } else if file_type.is_file() {
                match entry.metadata() {
                    Ok(metadata) => on_file(path, metadata)?,
                    Err(error) => on_error(&path, format!("{}: {error}", path.display())),
                }
            }
        }
    }

    Ok(())
}

fn duplicate_group_id(size: u64, hash: &str) -> String {
    let prefix_len = hash.len().min(16);
    format!("{size}-{}", &hash[..prefix_len])
}

fn duplicate_file_from_candidate(candidate: DuplicateCandidate) -> DuplicateCheckFile {
    let name = candidate
        .path
        .file_name()
        .map(|name| name.to_string_lossy().to_string())
        .unwrap_or_else(|| candidate.path.to_string_lossy().to_string());
    DuplicateCheckFile {
        path: candidate.path.to_string_lossy().to_string(),
        name,
        size: candidate.size,
        modified: candidate.modified,
    }
}

fn check_cancelled(cancel: &AtomicBool) -> Result<(), String> {
    if cancel.load(Ordering::Relaxed) {
        Err("cancelled".to_string())
    } else {
        Ok(())
    }
}

fn compute_partial_sha256(
    path: &Path,
    max_bytes: u64,
    buffer_size: usize,
    cancel: &AtomicBool,
) -> Result<String, io::Error> {
    let mut file = fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0u8; buffer_size.max(4096)];
    let mut remaining = max_bytes;
    loop {
        if cancel.load(Ordering::Relaxed) {
            return Err(io::Error::new(io::ErrorKind::Interrupted, "cancelled"));
        }
        if remaining == 0 {
            break;
        }

        let limit = remaining.min(buffer.len() as u64) as usize;
        let n = file.read(&mut buffer[..limit])?;
        if n == 0 {
            break;
        }
        hasher.update(&buffer[..n]);
        remaining = remaining.saturating_sub(n as u64);
    }
    Ok(hex_encode(hasher.finalize()))
}

fn compute_sha256(
    path: &Path,
    buffer_size: usize,
    cancel: &AtomicBool,
) -> Result<String, io::Error> {
    let mut file = fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0u8; buffer_size.max(4096)];
    loop {
        if cancel.load(Ordering::Relaxed) {
            return Err(io::Error::new(io::ErrorKind::Interrupted, "cancelled"));
        }
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        hasher.update(&buffer[..n]);
    }
    Ok(hex_encode(hasher.finalize()))
}

#[cfg(test)]
mod tests {
    use super::{scan_disk_cleanup, scan_duplicate_check, DuplicateScanOptions};
    use std::fs;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    fn unique_temp_path(name: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("simplefile_core_cleanup_{name}_{nanos}"))
    }

    #[test]
    fn scan_disk_cleanup_finds_large_and_duplicate_files() {
        let root = unique_temp_path("scan");
        fs::create_dir_all(&root).unwrap();
        fs::write(root.join("large.bin"), vec![1u8; 16]).unwrap();
        fs::write(root.join("dupe_a.txt"), b"same").unwrap();
        fs::write(root.join("dupe_b.txt"), b"same").unwrap();
        fs::write(root.join("unique.txt"), b"different").unwrap();

        let cancel = AtomicBool::new(false);
        let result =
            scan_disk_cleanup(&root.to_string_lossy(), Some(10), &cancel, |_, _, _| {}).unwrap();

        assert_eq!(result.large_files.len(), 1);
        assert!(result.large_files[0].path.ends_with("large.bin"));
        assert_eq!(result.large_files[0].size, 16);
        assert_eq!(result.duplicates.len(), 1);
        assert_eq!(result.duplicates[0].files.len(), 2);
        assert_eq!(result.scanned_files, 4);

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn scan_duplicate_check_uses_full_hash_after_partial_match() {
        let root = unique_temp_path("duplicates");
        fs::create_dir_all(&root).unwrap();
        fs::write(root.join("same-a.txt"), b"same content").unwrap();
        fs::write(root.join("same-b.txt"), b"same content").unwrap();
        fs::write(root.join("prefix-a.txt"), b"prefix-one").unwrap();
        fs::write(root.join("prefix-b.txt"), b"prefix-two").unwrap();

        let cancel = AtomicBool::new(false);
        let result = scan_duplicate_check(
            &root.to_string_lossy(),
            DuplicateScanOptions {
                min_size: Some(1),
                partial_hash_bytes: Some(6),
                ..DuplicateScanOptions::default()
            },
            &cancel,
            |_, _, _| {},
        )
        .unwrap();

        assert_eq!(result.groups.len(), 1);
        assert_eq!(result.groups[0].files.len(), 2);
        assert!(result.groups[0]
            .files
            .iter()
            .all(|file| file.name.starts_with("same-")));
        assert_eq!(result.total_reclaimable_bytes, b"same content".len() as u64);

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn scan_duplicate_check_respects_min_size_and_cancellation() {
        let root = unique_temp_path("duplicate_cancel");
        fs::create_dir_all(&root).unwrap();
        fs::write(root.join("empty-a.txt"), b"").unwrap();
        fs::write(root.join("empty-b.txt"), b"").unwrap();
        fs::write(root.join("tiny-a.txt"), b"x").unwrap();
        fs::write(root.join("tiny-b.txt"), b"x").unwrap();

        let cancel = AtomicBool::new(false);
        let result = scan_duplicate_check(
            &root.to_string_lossy(),
            DuplicateScanOptions {
                min_size: Some(2),
                partial_hash_bytes: Some(4096),
                ..DuplicateScanOptions::default()
            },
            &cancel,
            |_, _, _| {},
        )
        .unwrap();
        assert!(result.groups.is_empty());

        cancel.store(true, Ordering::Relaxed);
        let cancelled = scan_duplicate_check(
            &root.to_string_lossy(),
            DuplicateScanOptions {
                min_size: Some(0),
                partial_hash_bytes: Some(4096),
                ..DuplicateScanOptions::default()
            },
            &cancel,
            |_, _, _| {},
        );
        assert_eq!(cancelled.unwrap_err(), "cancelled");

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn scan_duplicate_check_respects_depth_exclusions_and_streaming_progress() {
        let root = unique_temp_path("duplicate_options");
        let keep = root.join("keep");
        let skip = root.join("skip-me");
        let deep = keep.join("deep");
        fs::create_dir_all(&deep).unwrap();
        fs::create_dir_all(&skip).unwrap();
        fs::write(root.join("same-a.txt"), b"same content").unwrap();
        fs::write(keep.join("same-b.txt"), b"same content").unwrap();
        fs::write(skip.join("same-c.txt"), b"same content").unwrap();
        fs::write(deep.join("same-d.txt"), b"same content").unwrap();

        let cancel = AtomicBool::new(false);
        let mut progress = Vec::new();
        let result = scan_duplicate_check(
            &root.to_string_lossy(),
            DuplicateScanOptions {
                min_size: Some(1),
                partial_hash_bytes: Some(4096),
                max_depth: Some(1),
                exclude_patterns: vec!["skip-*".to_string()],
                network_mode: Some(true),
            },
            &cancel,
            |current, total, item| progress.push((current, total, item.to_string())),
        )
        .unwrap();

        assert_eq!(result.scanned_files, 2);
        assert_eq!(result.groups.len(), 1);
        let paths: Vec<_> = result.groups[0]
            .files
            .iter()
            .map(|file| file.path.as_str())
            .collect();
        assert!(paths.iter().any(|path| path.ends_with("same-a.txt")));
        assert!(paths.iter().any(|path| path.ends_with("same-b.txt")));
        assert!(progress
            .iter()
            .any(|(current, total, _)| *current == 1 && *total == 0));

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn scan_disk_cleanup_respects_cancellation() {
        let root = unique_temp_path("cancel");
        fs::create_dir_all(&root).unwrap();
        fs::write(root.join("file.txt"), b"content").unwrap();

        let cancel = AtomicBool::new(true);
        let result = scan_disk_cleanup(&root.to_string_lossy(), Some(1), &cancel, |_, _, _| {});
        assert_eq!(result.unwrap_err(), "cancelled");

        let _ = fs::remove_dir_all(&root);
    }
}
