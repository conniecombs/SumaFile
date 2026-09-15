use crate::models::{FolderMetrics, TreeNode};
use crate::utils::{count_directory_entries, validate_existing_path_no_resolve};
use std::fs;
use std::path::{Path, PathBuf};
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

pub fn get_folder_metrics(path: &str, cancel: &AtomicBool) -> Option<FolderMetrics> {
    if let Ok(Some(listing)) = crate::archive::list_archive_directory(path) {
        let mut subdirectories: Vec<TreeNode> = listing
            .entries
            .iter()
            .filter(|entry| entry.is_dir)
            .map(|entry| TreeNode {
                name: entry.name.clone(),
                path: entry.path.clone(),
                has_children: false,
                children: Vec::new(),
            })
            .collect();
        subdirectories
            .sort_by_cached_key(|node| crate::native_accel::case_fold_for_sort(&node.name));
        return Some(FolderMetrics {
            size: listing.entries.iter().map(|entry| entry.size).sum(),
            item_count: listing.entries.len() as u64,
            subdirectories,
        });
    }

    let path_buf = match validate_existing_path_no_resolve(path) {
        Ok(p) => p,
        Err(_) => return None,
    };
    if !path_buf.is_dir() {
        return None;
    }
    calculate_metrics_recursive(&path_buf, cancel)
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

fn calculate_metrics_recursive(path: &Path, cancel: &AtomicBool) -> Option<FolderMetrics> {
    let mut size = 0u64;
    let mut item_count = 0u64;
    let mut subdirectories: Vec<TreeNode> = Vec::new();
    let mut root_subdirectory_indexes: Vec<(PathBuf, usize)> = Vec::new();
    let mut stack = Vec::new();

    let entries = fs::read_dir(path).ok()?;
    for entry in entries.flatten() {
        if cancel.load(Ordering::Relaxed) {
            return None;
        }

        item_count += 1;
        let Ok(ft) = entry.file_type() else { continue };
        if ft.is_dir() {
            let entry_path = entry.path();
            let index = subdirectories.len();
            subdirectories.push(TreeNode {
                name: entry.file_name().to_string_lossy().to_string(),
                path: entry_path.to_string_lossy().to_string(),
                has_children: false,
                children: Vec::new(),
            });
            root_subdirectory_indexes.push((entry_path.clone(), index));
            stack.push(entry_path);
        } else if ft.is_file() {
            if let Ok(metadata) = entry.metadata() {
                size += metadata.len();
            }
        }
    }

    while let Some(current) = stack.pop() {
        if cancel.load(Ordering::Relaxed) {
            return None;
        }

        let root_index = root_subdirectory_indexes
            .iter()
            .find_map(|(root, index)| (root == &current).then_some(*index));
        if let Ok(entries) = fs::read_dir(&current) {
            for entry in entries.flatten() {
                if cancel.load(Ordering::Relaxed) {
                    return None;
                }

                let Ok(ft) = entry.file_type() else { continue };
                if ft.is_dir() {
                    if let Some(index) = root_index {
                        subdirectories[index].has_children = true;
                    }
                    stack.push(entry.path());
                } else if ft.is_file() {
                    if let Ok(metadata) = entry.metadata() {
                        size += metadata.len();
                    }
                }
            }
        }
    }

    subdirectories.sort_by_cached_key(|node| crate::native_accel::case_fold_for_sort(&node.name));
    Some(FolderMetrics {
        size,
        item_count,
        subdirectories,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicBool;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir() -> PathBuf {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock should be after epoch")
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "simplefile-folder-metrics-{}-{suffix}",
            std::process::id()
        ));
        fs::create_dir_all(&root).expect("create temp folder");
        root
    }

    #[test]
    fn get_folder_metrics_combines_size_items_and_subdirectories() {
        let root = temp_dir();
        let photos = root.join("Photos");
        let projects = root.join("Projects");
        fs::create_dir_all(photos.join("Nested")).expect("create nested folder");
        fs::create_dir_all(&projects).expect("create sibling folder");
        fs::write(root.join("readme.txt"), b"abcd").expect("write root file");
        fs::write(photos.join("image.bin"), b"abc").expect("write direct child file");
        fs::write(photos.join("Nested").join("raw.bin"), b"abcde").expect("write nested file");
        fs::write(projects.join("notes.txt"), b"ab").expect("write sibling child file");

        let cancel = AtomicBool::new(false);
        let metrics = get_folder_metrics(root.to_string_lossy().as_ref(), &cancel)
            .expect("folder metrics should be returned");

        assert_eq!(14, metrics.size);
        assert_eq!(3, metrics.item_count);
        assert_eq!(2, metrics.subdirectories.len());
        assert_eq!("Photos", metrics.subdirectories[0].name);
        assert!(metrics.subdirectories[0].has_children);
        assert_eq!("Projects", metrics.subdirectories[1].name);
        assert!(!metrics.subdirectories[1].has_children);

        fs::remove_dir_all(root).expect("cleanup temp folder");
    }

    #[test]
    fn get_folder_metrics_respects_pre_cancelled_token() {
        let root = temp_dir();
        fs::write(root.join("readme.txt"), b"abcd").expect("write root file");

        let cancel = AtomicBool::new(true);
        let metrics = get_folder_metrics(root.to_string_lossy().as_ref(), &cancel);

        assert!(metrics.is_none());
        fs::remove_dir_all(root).expect("cleanup temp folder");
    }
}
