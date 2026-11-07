//! Fast directory enumeration with optional progressive chunk streaming.
//!
//! On Windows, uses `FindFirstFileExW` + `FIND_FIRST_EX_LARGE_FETCH` so network
//! shares receive larger SMB buffers. Entry metadata is taken from the find
//! data (or `DirEntry` on the fallback path) without re-statting every path.
//! `read_link` only runs for reparse points / symlinks.

use crate::models::{DirectoryListing, DirectoryListingChunk, FileEntry};
use crate::utils::{format_system_time, is_network_path, validate_existing_path_no_resolve};
use std::fs;
use std::path::{Path, PathBuf};

/// First paint budget: enough rows for a typical viewport.
pub const FIRST_CHUNK_SIZE: usize = 96;
/// Subsequent chunks trade IPC overhead vs. progressive updates.
pub const LATER_CHUNK_SIZE: usize = 256;

#[derive(Debug, Clone, Copy, Default, Eq, PartialEq)]
pub enum ListingMode {
    #[default]
    Full,
    Light,
}

#[derive(Debug, Clone)]
pub struct ListDirectoryOptions {
    pub mode: ListingMode,
    pub final_entries: bool,
    pub sort_by: String,
    pub sort_ascending: bool,
    pub filter: Option<String>,
    pub include_hidden: bool,
}

impl Default for ListDirectoryOptions {
    fn default() -> Self {
        Self {
            mode: ListingMode::Full,
            final_entries: true,
            sort_by: "name".to_string(),
            sort_ascending: true,
            filter: None,
            include_hidden: true,
        }
    }
}

/// List a real filesystem directory or archive virtual directory, invoking
/// `on_chunk` as pages fill for normal filesystem directories.
pub fn list_directory(
    path: String,
    mut on_chunk: impl FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<DirectoryListing, String> {
    if crate::recycle_bin::is_recycle_bin_path(&path) {
        let listing = crate::recycle_bin::list_recycle_bin()?;
        on_chunk(DirectoryListingChunk {
            path: listing.path.clone(),
            parent: listing.parent.clone(),
            entries: listing.entries.clone(),
            chunk_index: 0,
            done: true,
            is_network: false,
        })?;
        return Ok(listing);
    }

    if let Some(listing) = crate::archive::list_archive_directory(&path)? {
        on_chunk(DirectoryListingChunk {
            path: listing.path.clone(),
            parent: listing.parent.clone(),
            entries: listing.entries.clone(),
            chunk_index: 0,
            done: true,
            is_network: false,
        })?;
        return Ok(listing);
    }

    let path_buf = validate_existing_path_no_resolve(&path)?;
    if !path_buf.is_dir() {
        return Err(format!("Path is not a directory: {path}"));
    }

    let current_path = path_buf.to_string_lossy().to_string();
    let parent = path_buf.parent().map(|p| p.to_string_lossy().to_string());
    let is_network = is_network_path(&path_buf);

    let mut all_entries: Vec<FileEntry> = Vec::new();
    let mut pending: Vec<FileEntry> = Vec::with_capacity(FIRST_CHUNK_SIZE);
    let mut chunk_index: u32 = 0;

    let flush = |entries: &mut Vec<FileEntry>,
                 all: &mut Vec<FileEntry>,
                 index: &mut u32,
                 done: bool,
                 on_chunk: &mut dyn FnMut(DirectoryListingChunk) -> Result<(), String>|
     -> Result<(), String> {
        if entries.is_empty() && !done {
            return Ok(());
        }
        let chunk_entries = std::mem::take(entries);
        all.extend(chunk_entries.iter().cloned());
        on_chunk(DirectoryListingChunk {
            path: current_path.clone(),
            parent: parent.clone(),
            entries: chunk_entries,
            chunk_index: *index,
            done,
            is_network,
        })?;
        *index = index.saturating_add(1);
        Ok(())
    };

    let mut on_entry = |entry: FileEntry| -> Result<(), String> {
        pending.push(entry);
        let threshold = if chunk_index == 0 {
            FIRST_CHUNK_SIZE
        } else {
            LATER_CHUNK_SIZE
        };
        if pending.len() >= threshold {
            flush(
                &mut pending,
                &mut all_entries,
                &mut chunk_index,
                false,
                &mut on_chunk,
            )?;
        }
        Ok(())
    };

    enumerate_directory(&path_buf, &mut on_entry)?;
    flush(
        &mut pending,
        &mut all_entries,
        &mut chunk_index,
        true,
        &mut on_chunk,
    )?;

    all_entries.sort_by_cached_key(|e| crate::native_accel::dirs_first_name_key(e.is_dir, &e.name));

    Ok(DirectoryListing {
        path: current_path,
        parent,
        entries: all_entries,
        is_network,
    })
}

/// List without streaming chunks (tests and callers that only need the final page).
pub fn list_directory_collect(path: String) -> Result<DirectoryListing, String> {
    list_directory(path, |_| Ok(()))
}

/// List with explicit wire-shape options. This path collects entries before
/// chunking so filters and sort order can be applied service-side.
pub fn list_directory_with_options(
    path: String,
    options: ListDirectoryOptions,
    mut on_chunk: impl FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<DirectoryListing, String> {
    if crate::recycle_bin::is_recycle_bin_path(&path) {
        let listing = crate::recycle_bin::list_recycle_bin()?;
        let mut entries = prepare_entries(listing.entries, &options);
        emit_prepared_chunks(
            &listing.path,
            listing.parent.as_deref(),
            listing.is_network,
            &entries,
            &mut on_chunk,
        )?;
        if !options.final_entries {
            entries.clear();
        }
        return Ok(DirectoryListing {
            path: listing.path,
            parent: listing.parent,
            entries,
            is_network: listing.is_network,
        });
    }

    if let Some(listing) = crate::archive::list_archive_directory(&path)? {
        let mut entries = prepare_entries(listing.entries, &options);
        emit_prepared_chunks(
            &listing.path,
            listing.parent.as_deref(),
            listing.is_network,
            &entries,
            &mut on_chunk,
        )?;
        if !options.final_entries {
            entries.clear();
        }
        return Ok(DirectoryListing {
            path: listing.path,
            parent: listing.parent,
            entries,
            is_network: listing.is_network,
        });
    }

    let path_buf = validate_existing_path_no_resolve(&path)?;
    if !path_buf.is_dir() {
        return Err(format!("Path is not a directory: {path}"));
    }

    let current_path = path_buf.to_string_lossy().to_string();
    let parent = path_buf.parent().map(|p| p.to_string_lossy().to_string());
    let is_network = is_network_path(&path_buf);
    if !options.final_entries {
        return list_directory_streaming_options(
            &path_buf,
            current_path,
            parent,
            is_network,
            &options,
            &mut on_chunk,
        );
    }

    let mut entries = Vec::new();
    enumerate_directory(&path_buf, &mut |entry| {
        entries.push(entry);
        Ok(())
    })?;
    let mut entries = prepare_entries(entries, &options);
    emit_prepared_chunks(
        &current_path,
        parent.as_deref(),
        is_network,
        &entries,
        &mut on_chunk,
    )?;
    if !options.final_entries {
        entries.clear();
    }

    Ok(DirectoryListing {
        path: current_path,
        parent,
        entries,
        is_network,
    })
}

/// Enumerates a directory while capturing the raw (unfiltered, full-metadata) `DirectoryListing`
/// for session-only RAM caching when the path is a local filesystem directory.
pub fn list_directory_capturing_raw(
    path: String,
    options: Option<ListDirectoryOptions>,
    mut on_chunk: impl FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<(DirectoryListing, Option<DirectoryListing>), String> {
    let Some(options) = options else {
        let listing = list_directory(path, on_chunk)?;
        let cacheable = if !listing.is_network
            && !crate::recycle_bin::is_recycle_bin_path(&listing.path)
            && !crate::archive::is_archive_path(&listing.path)
        {
            Some(listing.clone())
        } else {
            None
        };
        return Ok((listing, cacheable));
    };

    // Routing only needs to know whether this is an archive path; parsing the
    // archive here meant every archive listing was parsed twice.
    if crate::recycle_bin::is_recycle_bin_path(&path) || crate::archive::is_archive_path(&path) {
        let listing = list_directory_with_options(path, options, on_chunk)?;
        return Ok((listing, None));
    }

    let path_buf = validate_existing_path_no_resolve(&path)?;
    if !path_buf.is_dir() {
        return Err(format!("Path is not a directory: {path}"));
    }

    let current_path = path_buf.to_string_lossy().to_string();
    let parent = path_buf.parent().map(|p| p.to_string_lossy().to_string());
    let is_network = is_network_path(&path_buf);

    if !options.final_entries {
        let mut raw_entries = Vec::new();
        let listing = list_directory_streaming_options_internal(
            &path_buf,
            current_path.clone(),
            parent.clone(),
            is_network,
            &options,
            if is_network {
                None
            } else {
                Some(&mut raw_entries)
            },
            &mut on_chunk,
        )?;
        let raw_listing = if is_network {
            None
        } else {
            Some(DirectoryListing {
                path: current_path,
                parent,
                entries: raw_entries,
                is_network: false,
            })
        };
        return Ok((listing, raw_listing));
    }

    let mut raw_entries = Vec::new();
    enumerate_directory(&path_buf, &mut |entry| {
        raw_entries.push(entry);
        Ok(())
    })?;
    let raw_listing = if is_network {
        None
    } else {
        Some(DirectoryListing {
            path: current_path.clone(),
            parent: parent.clone(),
            entries: raw_entries.clone(),
            is_network: false,
        })
    };

    let mut entries = prepare_entries(raw_entries, &options);
    emit_prepared_chunks(
        &current_path,
        parent.as_deref(),
        is_network,
        &entries,
        &mut on_chunk,
    )?;
    if !options.final_entries {
        entries.clear();
    }

    Ok((
        DirectoryListing {
            path: current_path,
            parent,
            entries,
            is_network,
        },
        raw_listing,
    ))
}

/// Replays a cached raw `DirectoryListing` with the requested `ListDirectoryOptions`,
/// emitting progressive chunks and returning the final `DirectoryListing`.
pub fn emit_cached_directory_listing(
    cached: &DirectoryListing,
    options: Option<ListDirectoryOptions>,
    mut on_chunk: impl FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<DirectoryListing, String> {
    let Some(options) = options else {
        let mut entries = cached.entries.clone();
        emit_prepared_chunks(
            &cached.path,
            cached.parent.as_deref(),
            cached.is_network,
            &entries,
            &mut on_chunk,
        )?;
        entries.sort_by_cached_key(|e| crate::native_accel::dirs_first_name_key(e.is_dir, &e.name));
        return Ok(DirectoryListing {
            path: cached.path.clone(),
            parent: cached.parent.clone(),
            entries,
            is_network: cached.is_network,
        });
    };

    let mut entries = prepare_entries(cached.entries.clone(), &options);
    emit_prepared_chunks(
        &cached.path,
        cached.parent.as_deref(),
        cached.is_network,
        &entries,
        &mut on_chunk,
    )?;
    if !options.final_entries {
        entries.clear();
    }
    Ok(DirectoryListing {
        path: cached.path.clone(),
        parent: cached.parent.clone(),
        entries,
        is_network: cached.is_network,
    })
}

fn enumerate_directory(
    path: &Path,
    on_entry: &mut dyn FnMut(FileEntry) -> Result<(), String>,
) -> Result<(), String> {
    #[cfg(windows)]
    {
        match enumerate_directory_windows(path, on_entry) {
            Ok(()) => return Ok(()),
            Err(WindowsEnumError::Open(err)) => {
                // Only fall back to portable read_dir when the find API could
                // not open the directory at all. Once entries have been
                // streamed, a second pass would deliver every one of them
                // again, and the consumer cannot take them back.
                log::debug!("Windows fast enum failed for {}: {err}", path.display());
            }
            Err(WindowsEnumError::MidStream(err)) => return Err(err),
        }
    }

    enumerate_directory_std(path, on_entry)
}

#[cfg(windows)]
enum WindowsEnumError {
    /// `FindFirstFileExW` failed; nothing was delivered yet.
    Open(String),
    /// Enumeration or the consumer failed after entries were delivered.
    MidStream(String),
}

fn list_directory_streaming_options(
    path_buf: &Path,
    current_path: String,
    parent: Option<String>,
    is_network: bool,
    options: &ListDirectoryOptions,
    on_chunk: &mut dyn FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<DirectoryListing, String> {
    list_directory_streaming_options_internal(
        path_buf,
        current_path,
        parent,
        is_network,
        options,
        None,
        on_chunk,
    )
}

fn list_directory_streaming_options_internal(
    path_buf: &Path,
    current_path: String,
    parent: Option<String>,
    is_network: bool,
    options: &ListDirectoryOptions,
    mut raw_sink: Option<&mut Vec<FileEntry>>,
    on_chunk: &mut dyn FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<DirectoryListing, String> {
    let filter = normalized_filter(options);
    let mut pending: Vec<FileEntry> = Vec::with_capacity(FIRST_CHUNK_SIZE);
    let mut chunk_index: u32 = 0;

    enumerate_directory(path_buf, &mut |entry| {
        if let Some(raw) = raw_sink.as_deref_mut() {
            raw.push(entry.clone());
        }

        if !entry_matches_options(&entry, options, filter.as_deref()) {
            return Ok(());
        }

        pending.push(entry);
        let threshold = if chunk_index == 0 {
            FIRST_CHUNK_SIZE
        } else {
            LATER_CHUNK_SIZE
        };
        if pending.len() >= threshold {
            flush_streaming_options_chunk(
                &current_path,
                parent.as_deref(),
                is_network,
                options,
                &mut pending,
                &mut chunk_index,
                false,
                on_chunk,
            )?;
        }
        Ok(())
    })?;
    flush_streaming_options_chunk(
        &current_path,
        parent.as_deref(),
        is_network,
        options,
        &mut pending,
        &mut chunk_index,
        true,
        on_chunk,
    )?;

    Ok(DirectoryListing {
        path: current_path,
        parent,
        entries: Vec::new(),
        is_network,
    })
}

#[allow(clippy::too_many_arguments)]
fn flush_streaming_options_chunk(
    path: &str,
    parent: Option<&str>,
    is_network: bool,
    options: &ListDirectoryOptions,
    entries: &mut Vec<FileEntry>,
    chunk_index: &mut u32,
    done: bool,
    on_chunk: &mut dyn FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<(), String> {
    if entries.is_empty() && !done {
        return Ok(());
    }

    let mut chunk_entries = std::mem::take(entries);
    if chunk_entries.len() > 1 {
        sort_entries(&mut chunk_entries, options);
    }
    for entry in &mut chunk_entries {
        apply_light_mode(entry, options.mode);
    }
    on_chunk(DirectoryListingChunk {
        path: path.to_string(),
        parent: parent.map(str::to_string),
        entries: chunk_entries,
        chunk_index: *chunk_index,
        done,
        is_network,
    })?;
    *chunk_index = (*chunk_index).saturating_add(1);
    Ok(())
}

fn prepare_entries(mut entries: Vec<FileEntry>, options: &ListDirectoryOptions) -> Vec<FileEntry> {
    let filter = normalized_filter(options);
    entries.retain(|entry| entry_matches_options(entry, options, filter.as_deref()));

    sort_entries(&mut entries, options);

    for entry in &mut entries {
        apply_light_mode(entry, options.mode);
    }

    entries
}

fn normalized_filter(options: &ListDirectoryOptions) -> Option<String> {
    options
        .filter
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_lowercase)
}

fn entry_matches_options(
    entry: &FileEntry,
    options: &ListDirectoryOptions,
    filter: Option<&str>,
) -> bool {
    if !options.include_hidden && (entry.is_hidden || entry.is_system) {
        return false;
    }

    filter.is_none_or(|value| crate::native_accel::contains_case_insensitive(&entry.name, value))
}

fn apply_light_mode(entry: &mut FileEntry, mode: ListingMode) {
    if mode != ListingMode::Light {
        return;
    }

    entry.path.clear();
    entry.extension.clear();
    entry.permissions = None;
    entry.symlink_target = None;
    entry.git_status = None;
}

/// Sort key computed once per entry. The old comparator case-folded names
/// (and the sort column) on every comparison: millions of allocations for a
/// large folder, repeated for each streamed chunk.
struct EntrySortKey {
    column: ColumnKey,
    name: String,
}

enum ColumnKey {
    Number(u64),
    Text(String),
    Name,
}

fn entry_sort_key(entry: &FileEntry, sort_by: &str) -> EntrySortKey {
    let column = match sort_by {
        "size" => ColumnKey::Number(entry.size),
        "modified" | "date" => ColumnKey::Text(entry.modified.clone()),
        "extension" | "type" => {
            ColumnKey::Text(crate::native_accel::case_fold_for_sort(&entry.extension))
        }
        "path" => ColumnKey::Text(crate::native_accel::case_fold_for_sort(&entry.path)),
        _ => ColumnKey::Name,
    };
    EntrySortKey {
        column,
        name: crate::native_accel::case_fold_for_sort(&entry.name),
    }
}

fn compare_column_keys(left: &EntrySortKey, right: &EntrySortKey) -> std::cmp::Ordering {
    match (&left.column, &right.column) {
        (ColumnKey::Number(a), ColumnKey::Number(b)) => a.cmp(b),
        (ColumnKey::Text(a), ColumnKey::Text(b)) => a.cmp(b),
        _ => left.name.cmp(&right.name),
    }
}

fn sort_entries(entries: &mut [FileEntry], options: &ListDirectoryOptions) {
    let sort_by = options.sort_by.as_str();
    let by_name = matches!(sort_by, "name" | "");
    let ascending = options.sort_ascending;
    let keys: Vec<EntrySortKey> = entries
        .iter()
        .map(|entry| entry_sort_key(entry, sort_by))
        .collect();

    // Directories first, always. Then the sort column in the requested
    // direction, then the name as an ascending tiebreak (descending only when
    // the name itself is the sort column).
    let mut order: Vec<usize> = (0..entries.len()).collect();
    order.sort_by(|&a, &b| {
        let (left, right) = (&entries[a], &entries[b]);
        let dirs_first = left.is_dir.cmp(&right.is_dir).reverse();
        let (key_a, key_b) = (&keys[a], &keys[b]);
        let column = if by_name {
            if ascending {
                key_a.name.cmp(&key_b.name)
            } else {
                key_b.name.cmp(&key_a.name)
            }
        } else if ascending {
            compare_column_keys(key_a, key_b).then_with(|| key_a.name.cmp(&key_b.name))
        } else {
            compare_column_keys(key_b, key_a).then_with(|| key_a.name.cmp(&key_b.name))
        };
        dirs_first.then(column)
    });

    // Apply the permutation in place with swaps: new[i] = old[order[i]].
    let mut visited = vec![false; entries.len()];
    for start in 0..entries.len() {
        if visited[start] {
            continue;
        }
        let mut index = start;
        loop {
            visited[index] = true;
            let next = order[index];
            if next == start {
                break;
            }
            entries.swap(index, next);
            index = next;
        }
    }
}

fn emit_prepared_chunks(
    path: &str,
    parent: Option<&str>,
    is_network: bool,
    entries: &[FileEntry],
    on_chunk: &mut dyn FnMut(DirectoryListingChunk) -> Result<(), String>,
) -> Result<(), String> {
    if entries.is_empty() {
        return on_chunk(DirectoryListingChunk {
            path: path.to_string(),
            parent: parent.map(str::to_string),
            entries: Vec::new(),
            chunk_index: 0,
            done: true,
            is_network,
        });
    }

    let mut chunk_index = 0u32;
    let mut start = 0usize;
    while start < entries.len() {
        let chunk_size = if chunk_index == 0 {
            FIRST_CHUNK_SIZE
        } else {
            LATER_CHUNK_SIZE
        };
        let end = (start + chunk_size).min(entries.len());
        on_chunk(DirectoryListingChunk {
            path: path.to_string(),
            parent: parent.map(str::to_string),
            entries: entries[start..end].to_vec(),
            chunk_index,
            done: end == entries.len(),
            is_network,
        })?;
        chunk_index = chunk_index.saturating_add(1);
        start = end;
    }
    Ok(())
}

fn enumerate_directory_std(
    path: &Path,
    on_entry: &mut dyn FnMut(FileEntry) -> Result<(), String>,
) -> Result<(), String> {
    let read_dir = fs::read_dir(path).map_err(|e| format!("Failed to read directory: {e}"))?;
    for entry in read_dir.flatten() {
        if let Some(file_entry) = crate::utils::get_file_entry_from_dir_entry(&entry) {
            on_entry(file_entry)?;
        }
    }
    Ok(())
}

#[cfg(windows)]
fn enumerate_directory_windows(
    path: &Path,
    on_entry: &mut dyn FnMut(FileEntry) -> Result<(), String>,
) -> Result<(), WindowsEnumError> {
    use std::mem::MaybeUninit;
    use std::os::windows::ffi::OsStrExt;
    use winapi::um::errhandlingapi::GetLastError;
    use winapi::um::fileapi::{FindClose, FindFirstFileExW, FindNextFileW};
    use winapi::um::handleapi::INVALID_HANDLE_VALUE;
    use winapi::um::minwinbase::{
        FindExInfoBasic, FindExSearchNameMatch, FIND_FIRST_EX_LARGE_FETCH, WIN32_FIND_DATAW,
    };

    let pattern: Vec<u16> = path
        .join("*")
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    let mut data = MaybeUninit::<WIN32_FIND_DATAW>::zeroed();
    let handle = unsafe {
        FindFirstFileExW(
            pattern.as_ptr(),
            FindExInfoBasic,
            data.as_mut_ptr() as *mut _,
            FindExSearchNameMatch,
            std::ptr::null_mut(),
            FIND_FIRST_EX_LARGE_FETCH,
        )
    };

    if handle == INVALID_HANDLE_VALUE {
        let err = unsafe { GetLastError() };
        return Err(WindowsEnumError::Open(format!(
            "FindFirstFileExW failed for {} (os error {err})",
            path.display()
        )));
    }

    let result = (|| -> Result<(), String> {
        loop {
            let find_data = unsafe { data.assume_init_ref() };
            if let Some(entry) = file_entry_from_find_data(path, find_data) {
                on_entry(entry)?;
            }

            let ok = unsafe { FindNextFileW(handle, data.as_mut_ptr()) };
            if ok == 0 {
                let err = unsafe { GetLastError() };
                // ERROR_NO_MORE_FILES = 18
                if err != 18 {
                    return Err(format!(
                        "FindNextFileW failed for {} (os error {err})",
                        path.display()
                    ));
                }
                break;
            }
        }
        Ok(())
    })();

    unsafe {
        FindClose(handle);
    }

    result.map_err(WindowsEnumError::MidStream)
}

#[cfg(windows)]
fn file_entry_from_find_data(
    parent: &Path,
    data: &winapi::um::minwinbase::WIN32_FIND_DATAW,
) -> Option<FileEntry> {
    use crate::utils::hidden_system_from_attrs;
    use std::os::windows::ffi::OsStringExt;
    use winapi::um::winnt::{FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_REPARSE_POINT};

    let name_len = data
        .cFileName
        .iter()
        .position(|&c| c == 0)
        .unwrap_or(data.cFileName.len());
    if name_len == 0 {
        return None;
    }

    let name = std::ffi::OsString::from_wide(&data.cFileName[..name_len]);
    let name_str = name.to_string_lossy();
    if name_str == "." || name_str == ".." {
        return None;
    }

    let attrs = data.dwFileAttributes;
    let is_dir = attrs & FILE_ATTRIBUTE_DIRECTORY != 0;
    let is_reparse = attrs & FILE_ATTRIBUTE_REPARSE_POINT != 0;
    let (is_hidden, is_system) = hidden_system_from_attrs(&name_str, attrs);
    let size = ((data.nFileSizeHigh as u64) << 32) | (data.nFileSizeLow as u64);
    let modified = filetime_to_string(data.ftLastWriteTime);

    let path_buf: PathBuf = parent.join(&name);
    let file_path = path_buf.to_string_lossy().to_string();
    let extension = if is_dir {
        String::new()
    } else {
        path_buf
            .extension()
            .map(|e| e.to_string_lossy().to_string())
            .unwrap_or_default()
    };

    // A reparse point is a symlink only for the symlink and mount-point
    // (junction) tags. Cloud placeholders (OneDrive, Dropbox) and dedup stubs
    // are reparse points too, and read_link on each of them failed anyway.
    const IO_REPARSE_TAG_MOUNT_POINT: u32 = 0xA000_0003;
    const IO_REPARSE_TAG_SYMLINK: u32 = 0xA000_000C;
    let is_symlink = is_reparse
        && matches!(
            data.dwReserved0,
            IO_REPARSE_TAG_MOUNT_POINT | IO_REPARSE_TAG_SYMLINK
        );
    let symlink_target = if is_symlink {
        fs::read_link(&path_buf)
            .ok()
            .map(|t| t.to_string_lossy().to_string())
    } else {
        None
    };

    Some(FileEntry {
        name: name_str.into_owned(),
        path: file_path,
        is_dir,
        is_symlink,
        is_hidden,
        is_system,
        size,
        modified,
        extension,
        permissions: None,
        symlink_target,
        git_status: None,
    })
}

#[cfg(windows)]
fn filetime_to_string(ft: winapi::shared::minwindef::FILETIME) -> String {
    use std::time::{Duration, UNIX_EPOCH};

    let ticks = ((ft.dwHighDateTime as u64) << 32) | (ft.dwLowDateTime as u64);
    // Windows FILETIME epoch (1601-01-01) to Unix epoch delta in 100ns units.
    const EPOCH_DIFF: u64 = 116_444_736_000_000_000;
    if ticks <= EPOCH_DIFF {
        return String::from("-");
    }
    let nanos = (ticks - EPOCH_DIFF).saturating_mul(100);
    let system_time = UNIX_EPOCH + Duration::from_nanos(nanos);
    format_system_time(system_time)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs::File;
    use std::io::Write;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn unique_temp(label: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "simplefile-dirlist-{label}-{}-{nanos}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn lists_files_without_channel() {
        let dir = unique_temp("basic");
        File::create(dir.join("a.txt"))
            .unwrap()
            .write_all(b"hi")
            .unwrap();
        fs::create_dir(dir.join("sub")).unwrap();

        let listing = list_directory_collect(dir.to_string_lossy().to_string()).unwrap();
        assert_eq!(listing.entries.len(), 2);
        assert!(listing.entries[0].is_dir);
        assert_eq!(listing.entries[0].name, "sub");
        assert_eq!(listing.entries[1].name, "a.txt");
        assert_eq!(listing.entries[1].size, 2);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn listing_options_light_mode_filters_and_omits_final_entries() {
        let dir = unique_temp("options");
        fs::write(dir.join("b.log"), b"log").unwrap();
        fs::write(dir.join("a.txt"), b"text").unwrap();
        fs::write(dir.join(".secret.txt"), b"hidden").unwrap();

        let mut chunks = Vec::new();
        let listing = list_directory_with_options(
            dir.to_string_lossy().to_string(),
            ListDirectoryOptions {
                mode: ListingMode::Light,
                final_entries: false,
                sort_by: "name".to_string(),
                sort_ascending: true,
                filter: Some("txt".to_string()),
                include_hidden: false,
            },
            |chunk| {
                chunks.push(chunk);
                Ok(())
            },
        )
        .unwrap();

        assert!(listing.entries.is_empty());
        let chunk = chunks.iter().find(|chunk| chunk.done).unwrap();
        assert_eq!(chunk.entries.len(), 1);
        let entry = &chunk.entries[0];
        assert_eq!(entry.name, "a.txt");
        assert!(entry.path.is_empty());
        assert!(entry.extension.is_empty());

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn listing_options_light_without_final_entries_emits_progressive_chunks() {
        let dir = unique_temp("options-progressive");
        for index in 0..(FIRST_CHUNK_SIZE + 2) {
            fs::write(dir.join(format!("{index:03}.txt")), b"x").unwrap();
        }

        let mut chunks = Vec::new();
        let listing = list_directory_with_options(
            dir.to_string_lossy().to_string(),
            ListDirectoryOptions {
                mode: ListingMode::Light,
                final_entries: false,
                sort_by: "name".to_string(),
                sort_ascending: true,
                filter: None,
                include_hidden: true,
            },
            |chunk| {
                chunks.push(chunk);
                Ok(())
            },
        )
        .unwrap();

        assert!(listing.entries.is_empty());
        assert!(chunks.len() >= 2);
        assert_eq!(chunks[0].entries.len(), FIRST_CHUNK_SIZE);
        assert!(!chunks[0].done);
        assert!(chunks.last().unwrap().done);
        assert!(chunks[0].entries.iter().all(|entry| entry.path.is_empty()));
        assert!(chunks[0]
            .entries
            .iter()
            .all(|entry| entry.extension.is_empty()));

        let _ = fs::remove_dir_all(&dir);
    }

    #[cfg(windows)]
    #[test]
    fn listing_hides_windows_hidden_attribute() {
        use std::os::windows::ffi::OsStrExt;
        use winapi::um::fileapi::SetFileAttributesW;
        use winapi::um::winnt::FILE_ATTRIBUTE_HIDDEN;

        let dir = unique_temp("win-hidden");
        let hidden_path = dir.join("secret.txt");
        fs::write(&hidden_path, b"n").unwrap();
        let wide: Vec<u16> = hidden_path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let ok = unsafe { SetFileAttributesW(wide.as_ptr(), FILE_ATTRIBUTE_HIDDEN) };
        assert_ne!(ok, 0);

        let hidden_listing = list_directory_with_options(
            dir.to_string_lossy().to_string(),
            ListDirectoryOptions {
                include_hidden: false,
                ..Default::default()
            },
            |_| Ok(()),
        )
        .unwrap();
        assert!(hidden_listing
            .entries
            .iter()
            .all(|entry| entry.name != "secret.txt"));

        let shown = list_directory_collect(dir.to_string_lossy().to_string()).unwrap();
        let secret = shown
            .entries
            .iter()
            .find(|entry| entry.name == "secret.txt")
            .expect("hidden file should be listed when include_hidden is default");
        assert!(secret.is_hidden);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn hidden_files_setting_governs_internal_transfer_artifacts() {
        let dir = unique_temp("transfer-artifacts");
        fs::write(dir.join("visible.txt"), b"ok").unwrap();
        fs::write(
            dir.join(".visible.txt.59c73a65be96f30b.sumafile-partial"),
            b"part",
        )
        .unwrap();
        fs::write(
            dir.join(".visible.txt.59c73a65be96f30b.sumafile-partial.state"),
            b"{}",
        )
        .unwrap();
        fs::write(
            dir.join(".visible.txt.1234.0.sumafile-replace-backup"),
            b"bak",
        )
        .unwrap();
        fs::create_dir(dir.join(".Megapack.b9702512292f3342.sumafile-partial")).unwrap();

        // Hidden files shown: every artifact is listed, flagged hidden.
        let shown = list_directory_collect(dir.to_string_lossy().to_string()).unwrap();
        let mut names: Vec<&str> = shown.entries.iter().map(|e| e.name.as_str()).collect();
        names.sort_unstable();
        assert_eq!(
            names,
            vec![
                ".Megapack.b9702512292f3342.sumafile-partial",
                ".visible.txt.1234.0.sumafile-replace-backup",
                ".visible.txt.59c73a65be96f30b.sumafile-partial",
                ".visible.txt.59c73a65be96f30b.sumafile-partial.state",
                "visible.txt",
            ]
        );
        assert!(shown
            .entries
            .iter()
            .filter(|e| e.name != "visible.txt")
            .all(|e| e.is_hidden));

        // Hidden files hidden: only the real file remains.
        let hidden = list_directory_with_options(
            dir.to_string_lossy().to_string(),
            ListDirectoryOptions {
                include_hidden: false,
                ..ListDirectoryOptions::default()
            },
            |_| Ok(()),
        )
        .unwrap();
        assert_eq!(hidden.entries.len(), 1);
        assert_eq!(hidden.entries[0].name, "visible.txt");

        let _ = fs::remove_dir_all(&dir);
    }
}

#[cfg(test)]
mod enumeration_tests {
    use super::enumerate_directory;
    use std::path::Path;

    fn scratch(label: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "sumafile-enum-{label}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    /// When the consumer stops accepting entries (client gone) after the
    /// fast enumeration has already streamed some, the portable fallback must
    /// not re-enumerate: the client would receive every entry twice.
    #[test]
    fn a_failure_after_entries_were_streamed_does_not_restart_from_scratch() {
        let dir = scratch("midstream");
        for index in 0..4 {
            std::fs::write(dir.join(format!("f{index}.txt")), b"x").unwrap();
        }
        let mut delivered = 0usize;
        let result = enumerate_directory(&dir, &mut |_entry| {
            delivered += 1;
            Err("client closed".to_string())
        });
        let _ = std::fs::remove_dir_all(&dir);
        assert!(result.is_err());
        assert_eq!(
            delivered, 1,
            "entries were streamed again by a fallback pass"
        );
    }

    /// Reparse points that are not links (cloud placeholders, dedup stubs)
    /// must not be shown as symlinks; real junctions still are.
    #[cfg(windows)]
    #[test]
    fn directory_junction_is_a_symlink_but_plain_entries_are_not() {
        let dir = scratch("junction");
        let target = dir.join("target");
        std::fs::create_dir_all(&target).unwrap();
        std::fs::write(dir.join("plain.txt"), b"x").unwrap();
        let link = dir.join("link");
        let status = std::process::Command::new("cmd")
            .args(["/c", "mklink", "/J"])
            .arg(&link)
            .arg(&target)
            .output()
            .expect("run mklink");
        assert!(
            status.status.success(),
            "{}",
            String::from_utf8_lossy(&status.stderr)
        );

        let mut entries = Vec::new();
        enumerate_directory(Path::new(&dir), &mut |entry| {
            entries.push(entry);
            Ok(())
        })
        .unwrap();
        let _ = std::fs::remove_dir_all(&dir);

        let link_entry = entries
            .iter()
            .find(|e| e.name == "link")
            .expect("junction listed");
        assert!(link_entry.is_symlink);
        assert!(link_entry.symlink_target.is_some());
        let plain = entries.iter().find(|e| e.name == "plain.txt").unwrap();
        assert!(!plain.is_symlink);
    }
}

#[cfg(test)]
mod sort_tests {
    use super::{sort_entries, ListDirectoryOptions};
    use crate::models::FileEntry;

    fn entry(name: &str, is_dir: bool, size: u64) -> FileEntry {
        FileEntry {
            name: name.to_string(),
            path: format!("C:/x/{name}"),
            is_dir,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size,
            modified: String::new(),
            extension: name
                .rsplit_once('.')
                .map(|(_, e)| e.to_string())
                .unwrap_or_default(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        }
    }

    fn names(entries: &[FileEntry]) -> Vec<&str> {
        entries.iter().map(|e| e.name.as_str()).collect()
    }

    #[test]
    fn size_descending_keeps_directories_first_and_breaks_ties_by_name() {
        let mut entries = vec![
            entry("b.txt", false, 10),
            entry("Zeta", true, 0),
            entry("a.txt", false, 10),
            entry("alpha", true, 0),
            entry("c.txt", false, 99),
        ];
        let options = ListDirectoryOptions {
            sort_by: "size".to_string(),
            sort_ascending: false,
            ..ListDirectoryOptions::default()
        };
        sort_entries(&mut entries, &options);
        assert_eq!(
            names(&entries),
            vec!["alpha", "Zeta", "c.txt", "a.txt", "b.txt"]
        );
    }

    #[test]
    fn name_descending_reverses_names_but_not_directory_grouping() {
        let mut entries = vec![
            entry("b.txt", false, 1),
            entry("Dir", true, 0),
            entry("A.txt", false, 1),
        ];
        let options = ListDirectoryOptions {
            sort_by: "name".to_string(),
            sort_ascending: false,
            ..ListDirectoryOptions::default()
        };
        sort_entries(&mut entries, &options);
        assert_eq!(names(&entries), vec!["Dir", "b.txt", "A.txt"]);
    }

    #[test]
    fn extension_ascending_is_case_insensitive_with_name_tiebreak() {
        let mut entries = vec![
            entry("z.PNG", false, 1),
            entry("a.txt", false, 1),
            entry("m.png", false, 1),
        ];
        let options = ListDirectoryOptions {
            sort_by: "extension".to_string(),
            sort_ascending: true,
            ..ListDirectoryOptions::default()
        };
        sort_entries(&mut entries, &options);
        assert_eq!(names(&entries), vec!["m.png", "z.PNG", "a.txt"]);
    }
}
