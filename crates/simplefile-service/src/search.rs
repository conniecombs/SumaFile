use chrono::{DateTime, Local, NaiveDateTime, TimeZone};
use glob::Pattern;
use simplefile_core::models::{SearchOptions, SearchResult};
use simplefile_core::utils::{
    hidden_system_from_metadata, name_looks_hidden, validate_existing_path_no_resolve,
};
use std::collections::VecDeque;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

fn parse_search_datetime(value: &str) -> Option<DateTime<Local>> {
    DateTime::parse_from_rfc3339(value)
        .ok()
        .map(|dt| dt.with_timezone(&Local))
        .or_else(|| {
            NaiveDateTime::parse_from_str(value, "%Y-%m-%dT%H:%M:%S")
                .ok()
                .and_then(|naive| Local.from_local_datetime(&naive).single())
        })
}

fn is_cancelled(flag: &Arc<AtomicBool>) -> bool {
    flag.load(Ordering::Relaxed)
}

fn ascii_contains_ignore_case(haystack: &[u8], needle: &[u8]) -> bool {
    if needle.is_empty() {
        return true;
    }
    if needle.len() > haystack.len() {
        return false;
    }
    let first = needle[0];
    let rest = &needle[1..];
    haystack.windows(needle.len()).any(|window| {
        window[0].eq_ignore_ascii_case(&first) && window[1..].eq_ignore_ascii_case(rest)
    })
}

fn name_matches(
    name: &str,
    query: &str,
    case_sensitive: bool,
    glob_pattern: &Option<Pattern>,
) -> bool {
    if query.is_empty() {
        return true;
    }

    if glob_pattern.is_none() {
        if case_sensitive {
            return name.contains(query);
        }
        if name.is_ascii() && query.is_ascii() {
            return ascii_contains_ignore_case(name.as_bytes(), query.as_bytes());
        }
    }

    let name_to_match = if case_sensitive {
        name.to_string()
    } else {
        simplefile_core::native_accel::case_fold_for_sort(name)
    };

    if let Some(pattern) = glob_pattern {
        pattern.matches(&name_to_match)
    } else {
        name_to_match.contains(query)
    }
}

fn entry_metadata_raw(path: &Path) -> (bool, bool, u64, Option<std::time::SystemTime>) {
    match fs::metadata(path) {
        Ok(metadata) => {
            let file_type = metadata.file_type();
            let is_dir = file_type.is_dir();
            let is_file = file_type.is_file();
            let modified_time = metadata.modified().ok();
            (
                is_dir,
                is_file,
                if is_dir { 0 } else { metadata.len() },
                modified_time,
            )
        }
        Err(_) => (false, false, 0, None),
    }
}

fn format_search_modified(modified_time: Option<std::time::SystemTime>) -> String {
    modified_time
        .map(|time| {
            DateTime::<Local>::from(time)
                .format("%Y-%m-%d %H:%M")
                .to_string()
        })
        .unwrap_or_else(|| "-".to_string())
}

struct FilterCandidate<'a> {
    is_dir: bool,
    is_file: bool,
    size: u64,
    extension: &'a str,
    after_dt: &'a Option<DateTime<Local>>,
    before_dt: &'a Option<DateTime<Local>>,
    modified_time: Option<std::time::SystemTime>,
}

fn extension_matches_any(ext: &str, types: &[String]) -> bool {
    if ext.is_ascii() && types.iter().all(|item| item.is_ascii()) {
        return types.iter().any(|item| item.eq_ignore_ascii_case(ext));
    }
    let ext_lower = ext.to_lowercase();
    types.iter().any(|item| item.to_lowercase() == ext_lower)
}

fn passes_filters(candidate: &FilterCandidate<'_>, options: &SearchOptions) -> bool {
    if let Some(types) = options
        .file_types
        .as_ref()
        .filter(|types| !types.is_empty())
    {
        // A type filter asks for files of those kinds; a directory whose name
        // happens to match the query is not one of them.
        if !candidate.is_file || !extension_matches_any(candidate.extension, types) {
            return false;
        }
    }

    if let Some(min) = options.min_size {
        if !candidate.is_dir && candidate.size < min {
            return false;
        }
    }

    if let Some(max) = options.max_size {
        if !candidate.is_dir && candidate.size > max {
            return false;
        }
    }

    // A date bound can only be satisfied by an entry whose time is known.
    if let Some(ref after) = candidate.after_dt {
        let after_sys = std::time::SystemTime::from(*after);
        match candidate.modified_time {
            Some(mod_time) if mod_time >= after_sys => {}
            _ => return false,
        }
    }

    if let Some(ref before) = candidate.before_dt {
        let before_sys = std::time::SystemTime::from(*before);
        match candidate.modified_time {
            Some(mod_time) if mod_time <= before_sys => {}
            _ => return false,
        }
    }

    true
}

fn content_matches_text(content: &str, needle: &str, case_sensitive: bool) -> bool {
    if case_sensitive {
        content.contains(needle)
    } else if content.is_ascii() && needle.is_ascii() {
        ascii_contains_ignore_case(content.as_bytes(), needle.as_bytes())
    } else {
        simplefile_core::native_accel::contains_case_insensitive(content, needle)
    }
}

fn content_matches_file(path: &Path, options: &SearchOptions, size: u64) -> bool {
    use std::io::Read;

    if !options.content_search || size == 0 || size >= 2_000_000 {
        return false;
    }

    // Wildcards describe file names, not file contents: `*report*` means the
    // text "report". An interior wildcard has no content meaning at all.
    let needle = options.query.trim_matches(['*', '?']);
    if needle.is_empty() || needle.contains(['*', '?']) {
        return false;
    }

    let Ok(mut file) = fs::File::open(path) else {
        return false;
    };

    // Sniff the first 8 KiB before allocating up to 2 MiB on binary files.
    let mut head = [0u8; 8192];
    let mut head_len = 0usize;
    while head_len < head.len() {
        match file.read(&mut head[head_len..]) {
            Ok(0) => break,
            Ok(n) => head_len += n,
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(_) => return false,
        }
    }
    if head_len == 0 {
        return false;
    }

    let is_utf16_le = head_len >= 2 && head[0] == 0xFF && head[1] == 0xFE;
    let is_utf16_be = head_len >= 2 && head[0] == 0xFE && head[1] == 0xFF;
    if !is_utf16_le && !is_utf16_be && head[..head_len].contains(&0) {
        return false;
    }

    let mut bytes = Vec::with_capacity((size as usize).min(2_000_000));
    bytes.extend_from_slice(&head[..head_len]);
    if (head_len as u64) < size && file.take(2_000_000).read_to_end(&mut bytes).is_err() {
        return false;
    }

    if is_utf16_le {
        let units: Vec<u16> = bytes[2..]
            .as_chunks::<2>()
            .0
            .iter()
            .copied()
            .map(u16::from_le_bytes)
            .collect();
        let text = String::from_utf16_lossy(&units);
        return content_matches_text(&text, needle, options.case_sensitive);
    }
    if is_utf16_be {
        let units: Vec<u16> = bytes[2..]
            .as_chunks::<2>()
            .0
            .iter()
            .copied()
            .map(u16::from_be_bytes)
            .collect();
        let text = String::from_utf16_lossy(&units);
        return content_matches_text(&text, needle, options.case_sensitive);
    }

    let Ok(content) = std::str::from_utf8(&bytes) else {
        return false;
    };
    content_matches_text(content, needle, options.case_sensitive)
}

pub fn search_files_blocking(
    options: SearchOptions,
    cancel: Arc<AtomicBool>,
    emit_batch: &dyn Fn(Vec<SearchResult>),
) -> Result<Vec<SearchResult>, String> {
    let search_path = validate_existing_path_no_resolve(&options.search_path)?;
    if !search_path.is_dir() {
        return Err(format!(
            "Search path is not a directory: {}",
            options.search_path
        ));
    }

    let query = if options.case_sensitive {
        options.query.clone()
    } else {
        simplefile_core::native_accel::case_fold_for_sort(&options.query)
    };
    let glob_query = query.clone();
    let glob_pattern = if options.query.contains('*') || options.query.contains('?') {
        Pattern::new(&glob_query).ok()
    } else {
        None
    };

    let max_results = options.max_results.unwrap_or(1000);
    let max_depth = options.max_depth.unwrap_or(10);
    let after_dt = options
        .date_after
        .as_deref()
        .and_then(parse_search_datetime);
    let before_dt = options
        .date_before
        .as_deref()
        .and_then(parse_search_datetime);

    let mut results: Vec<SearchResult> = Vec::new();
    let mut batch: Vec<SearchResult> = Vec::with_capacity(64);
    let batch_size = 64;
    let batch_interval = Duration::from_millis(80);
    let mut last_batch_emit = Instant::now();
    let mut queue: VecDeque<(PathBuf, usize)> = VecDeque::new();
    queue.push_back((search_path, 0));

    while let Some((dir, depth)) = queue.pop_front() {
        if results.len() >= max_results || is_cancelled(&cancel) {
            break;
        }

        if depth > max_depth {
            continue;
        }

        let read = match fs::read_dir(&dir) {
            Ok(read) => read,
            Err(_) => continue,
        };

        for entry in read.flatten() {
            if results.len() >= max_results || is_cancelled(&cancel) {
                break;
            }

            let path = entry.path();
            let name = entry.file_name().to_string_lossy().to_string();
            let metadata = entry.metadata().ok();

            if !options.include_hidden {
                let skip = match &metadata {
                    Some(meta) => {
                        let (hidden, system) = hidden_system_from_metadata(&name, meta);
                        hidden || system
                    }
                    None => name_looks_hidden(&name),
                };
                if skip {
                    continue;
                }
            }

            let (is_dir, is_file, size, modified_time) = match metadata {
                Some(metadata) => {
                    let file_type = metadata.file_type();
                    let is_dir = file_type.is_dir();
                    let is_file = file_type.is_file();
                    let modified_time = metadata.modified().ok();
                    (
                        is_dir,
                        is_file,
                        if is_dir { 0 } else { metadata.len() },
                        modified_time,
                    )
                }
                None => entry_metadata_raw(&path),
            };

            if is_dir && depth < max_depth {
                queue.push_back((path.clone(), depth + 1));
            }

            let raw_extension = if is_dir {
                std::borrow::Cow::Borrowed("")
            } else {
                path.extension()
                    .map(|ext| ext.to_string_lossy())
                    .unwrap_or(std::borrow::Cow::Borrowed(""))
            };

            let candidate = FilterCandidate {
                is_dir,
                is_file,
                size,
                extension: raw_extension.as_ref(),
                after_dt: &after_dt,
                before_dt: &before_dt,
                modified_time,
            };
            if !passes_filters(&candidate, &options) {
                continue;
            }

            let matched_name = name_matches(&name, &query, options.case_sensitive, &glob_pattern);
            let matched_content =
                !matched_name && is_file && content_matches_file(&path, &options, size);
            if !matched_name && !matched_content {
                continue;
            }

            let modified = format_search_modified(modified_time);
            let extension = if is_dir || raw_extension.is_empty() {
                String::new()
            } else {
                raw_extension.to_lowercase()
            };

            let result = SearchResult {
                name,
                path: path.to_string_lossy().to_string(),
                is_dir,
                size,
                modified,
                extension,
                match_type: if matched_name {
                    "name".to_string()
                } else {
                    "content".to_string()
                },
            };
            batch.push(result.clone());
            results.push(result);

            if batch.len() >= batch_size || last_batch_emit.elapsed() >= batch_interval {
                emit_batch(std::mem::take(&mut batch));
                last_batch_emit = Instant::now();
            }
        }
    }

    if !batch.is_empty() {
        emit_batch(batch);
    }

    results.sort_by_cached_key(|entry| {
        simplefile_core::native_accel::dirs_first_name_key(entry.is_dir, &entry.name)
    });
    Ok(results)
}

#[cfg(test)]
mod tests {
    use super::{name_matches, search_files_blocking};
    use simplefile_core::models::SearchOptions;
    use std::fs;
    use std::path::PathBuf;
    use std::sync::atomic::AtomicBool;
    use std::sync::{Arc, Mutex};
    use std::time::{SystemTime, UNIX_EPOCH};

    fn unique_temp_dir(label: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or(0);
        let dir = std::env::temp_dir().join(format!("simplefile-service-search-{label}-{nanos}"));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).expect("create temp dir");
        dir
    }

    #[test]
    fn name_matches_substring_case_insensitive() {
        assert!(name_matches("2000 Mules (2022)", "2000", false, &None));
        assert!(name_matches("2000 Mules (2022)", "mules", false, &None));
        assert!(!name_matches("2000 Mules (2022)", "1999", false, &None));
        assert!(!name_matches("2000 Mules (2022)", "MULES", true, &None));
        assert!(name_matches("2000 Mules (2022)", "Mules", true, &None));
    }

    #[test]
    fn search_streams_batches_and_respects_cancel() {
        let root = unique_temp_dir("cancel");
        fs::write(root.join("alpha.txt"), b"alpha").unwrap();
        fs::write(root.join("beta.txt"), b"beta").unwrap();

        let cancel = Arc::new(AtomicBool::new(false));
        let batches = Arc::new(Mutex::new(Vec::new()));
        let batch_target = batches.clone();
        let results = search_files_blocking(
            SearchOptions {
                query: ".txt".to_string(),
                search_path: root.to_string_lossy().to_string(),
                case_sensitive: false,
                include_hidden: false,
                file_types: None,
                max_results: Some(10),
                max_depth: Some(1),
                search_id: Some("search-test".to_string()),
                content_search: false,
                min_size: None,
                max_size: None,
                date_after: None,
                date_before: None,
            },
            cancel,
            &|batch| {
                batch_target.lock().unwrap().push(batch);
            },
        )
        .expect("search should succeed");

        assert_eq!(results.len(), 2);
        assert!(!batches.lock().unwrap().is_empty());

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn name_matches_glob() {
        let pattern = glob::Pattern::new("*mules*").unwrap();
        let folded = simplefile_core::native_accel::case_fold_for_sort("2000 Mules (2022)");
        assert!(pattern.matches(&folded));
        assert!(name_matches(
            "2000 Mules (2022)",
            "*mules*",
            false,
            &Some(pattern)
        ));
    }
}

#[cfg(test)]
mod filter_tests {
    use super::{passes_filters, FilterCandidate};
    use simplefile_core::models::SearchOptions;

    fn options(json: &str) -> SearchOptions {
        serde_json::from_str(json).unwrap()
    }

    fn candidate<'a>(is_dir: bool, extension: &'a str) -> FilterCandidate<'a> {
        FilterCandidate {
            is_dir,
            is_file: !is_dir,
            size: 10,
            extension,
            after_dt: &None,
            before_dt: &None,
            modified_time: None,
        }
    }

    #[test]
    fn a_type_filter_excludes_directories() {
        let options = options(
            r#"{"query":"report","search_path":"C:/","case_sensitive":false,"include_hidden":false,"file_types":["txt"]}"#,
        );
        assert!(passes_filters(&candidate(false, "txt"), &options));
        assert!(!passes_filters(&candidate(false, "png"), &options));
        assert!(!passes_filters(&candidate(true, ""), &options));
    }

    #[test]
    fn date_bounds_exclude_entries_without_a_known_time() {
        let options = options(
            r#"{"query":"x","search_path":"C:/","case_sensitive":false,"include_hidden":false}"#,
        );
        let after = Some(chrono::Local::now());
        let mut unknown = candidate(false, "txt");
        unknown.after_dt = &after;
        assert!(!passes_filters(&unknown, &options));
    }
}
