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

fn enumerate_directory(
    path: &Path,
    on_entry: &mut dyn FnMut(FileEntry) -> Result<(), String>,
) -> Result<(), String> {
    #[cfg(windows)]
    {
        match enumerate_directory_windows(path, on_entry) {
            Ok(()) => return Ok(()),
            Err(err) => {
                // Fall back to portable read_dir if the find API fails.
                log::debug!("Windows fast enum failed for {}: {err}", path.display());
            }
        }
    }

    enumerate_directory_std(path, on_entry)
}

fn prepare_entries(mut entries: Vec<FileEntry>, options: &ListDirectoryOptions) -> Vec<FileEntry> {
    if !options.include_hidden {
        entries.retain(|entry| !entry.is_hidden && !entry.is_system);
    }

    if let Some(filter) = options
        .filter
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
    {
        let filter = filter.to_lowercase();
        entries.retain(|entry| entry.name.to_lowercase().contains(&filter));
    }

    sort_entries(&mut entries, options);

    if options.mode == ListingMode::Light {
        for entry in &mut entries {
            entry.path.clear();
            entry.extension.clear();
            entry.permissions = None;
            entry.symlink_target = None;
            entry.git_status = None;
        }
    }

    entries
}

fn sort_entries(entries: &mut [FileEntry], options: &ListDirectoryOptions) {
    let sort_by = options.sort_by.as_str();
    entries.sort_by(|left, right| {
        let mut ordering = left
            .is_dir
            .cmp(&right.is_dir)
            .reverse()
            .then_with(|| compare_entry_column(left, right, sort_by))
            .then_with(|| {
                crate::native_accel::case_fold_for_sort(&left.name)
                    .cmp(&crate::native_accel::case_fold_for_sort(&right.name))
            });
        if !options.sort_ascending && !matches!(sort_by, "name" | "") {
            ordering = left
                .is_dir
                .cmp(&right.is_dir)
                .reverse()
                .then_with(|| compare_entry_column(right, left, sort_by))
                .then_with(|| {
                    crate::native_accel::case_fold_for_sort(&left.name)
                        .cmp(&crate::native_accel::case_fold_for_sort(&right.name))
                });
        } else if !options.sort_ascending {
            ordering = left.is_dir.cmp(&right.is_dir).reverse().then_with(|| {
                crate::native_accel::case_fold_for_sort(&right.name)
                    .cmp(&crate::native_accel::case_fold_for_sort(&left.name))
            });
        }
        ordering
    });
}

fn compare_entry_column(left: &FileEntry, right: &FileEntry, sort_by: &str) -> std::cmp::Ordering {
    match sort_by {
        "size" => left.size.cmp(&right.size),
        "modified" | "date" => left.modified.cmp(&right.modified),
        "extension" | "type" => crate::native_accel::case_fold_for_sort(&left.extension)
            .cmp(&crate::native_accel::case_fold_for_sort(&right.extension)),
        "path" => crate::native_accel::case_fold_for_sort(&left.path)
            .cmp(&crate::native_accel::case_fold_for_sort(&right.path)),
        _ => crate::native_accel::case_fold_for_sort(&left.name)
            .cmp(&crate::native_accel::case_fold_for_sort(&right.name)),
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
) -> Result<(), String> {
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
        return Err(format!(
            "FindFirstFileExW failed for {} (os error {err})",
            path.display()
        ));
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

    result
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

    // Only pay for read_link when the find data marks a reparse point.
    let symlink_target = if is_reparse {
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
        is_symlink: is_reparse,
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
}
