use crate::utils::{count_directory_entries, validate_existing_path_no_resolve};
use std::fs;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};

pub fn calculate_folder_size(path: &str, cancel: &AtomicBool) -> Option<u64> {
    let path_buf = match validate_existing_path_no_resolve(path) {
        Ok(p) => p,
        Err(_) => return None,
    };
    if !path_buf.is_dir() {
        return None;
    }
    calculate_size_recursive(&path_buf, cancel)
}

pub fn count_folder_items(path: &str, cancel: &AtomicBool) -> Option<u64> {
    if let Ok(Some(listing)) = crate::archive::list_archive_directory(path) {
        return Some(listing.entries.len() as u64);
    }

    let path_buf = match validate_existing_path_no_resolve(path) {
        Ok(p) => p,
        Err(_) => return None,
    };
    if !path_buf.is_dir() {
        return None;
    }
    count_directory_entries(&path_buf, cancel, None)
}

fn calculate_size_recursive(path: &Path, cancel: &AtomicBool) -> Option<u64> {
    let mut total = 0u64;
    let mut stack = vec![path.to_path_buf()];
    while let Some(current) = stack.pop() {
        if cancel.load(Ordering::Relaxed) {
            return None;
        }
        if let Ok(entries) = fs::read_dir(&current) {
            for entry in entries.flatten() {
                if cancel.load(Ordering::Relaxed) {
                    return None;
                }
                let Ok(ft) = entry.file_type() else { continue };
                if ft.is_dir() {
                    stack.push(entry.path());
                } else if ft.is_file() {
                    if let Ok(metadata) = entry.metadata() {
                        total += metadata.len();
                    }
                }
            }
        }
    }
    Some(total)
}
