//! What a copy keeps of a file besides its bytes: attributes, alternate data
//! streams, a long name.

use super::transfer_with_progress_blocking;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

fn unique_root(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!(
        "sumafile_fidelity_{name}_{}",
        super::generate_transfer_operation_id()
    ))
}

fn copy(operation: &'static str, source: &Path, destination: &Path) {
    transfer_with_progress_blocking(
        operation,
        vec![source.to_string_lossy().into_owned()],
        destination.to_string_lossy().into_owned(),
        super::generate_transfer_operation_id(),
        "error".to_string(),
        Arc::new(AtomicBool::new(false)),
        &|_| {},
    )
    .unwrap_or_else(|error| panic!("the {operation} succeeds: {}", error.message));
}

#[cfg(windows)]
mod windows {
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::fs::MetadataExt;
    use std::path::Path;
    pub use windows_sys::Win32::Storage::FileSystem::{
        FILE_ATTRIBUTE_HIDDEN as HIDDEN, FILE_ATTRIBUTE_READONLY as READONLY,
        FILE_ATTRIBUTE_SYSTEM as SYSTEM,
    };

    pub fn set_attributes(path: &Path, attributes: u32) {
        use windows_sys::Win32::Storage::FileSystem::SetFileAttributesW;
        let wide: Vec<u16> = path.as_os_str().encode_wide().chain([0]).collect();
        assert_ne!(
            unsafe { SetFileAttributesW(wide.as_ptr(), attributes) },
            0,
            "{}",
            path.display()
        );
    }

    /// The read-only, hidden and system attributes of `path`.
    pub fn shown(path: &Path) -> u32 {
        std::fs::symlink_metadata(path).unwrap().file_attributes() & (READONLY | HIDDEN | SYSTEM)
    }
}

/// A copied file or folder keeps its Hidden and System attributes, as in
/// Explorer. Only the read-only attribute used to be kept, so a copied
/// `desktop.ini` showed up in every folder it was copied into.
#[cfg(windows)]
#[test]
fn a_copied_tree_keeps_hidden_and_system_on_its_files_and_folders() {
    use windows::{set_attributes, shown, HIDDEN, SYSTEM};

    let root = unique_root("attributes");
    let source = root.join("source").join("tree");
    let hidden_folder = source.join("hidden");
    fs::create_dir_all(&hidden_folder).unwrap();
    fs::write(source.join("desktop.ini"), b"[.ShellClassInfo]").unwrap();
    fs::write(source.join("plain.txt"), b"plain").unwrap();
    fs::write(hidden_folder.join("inside.txt"), b"inside").unwrap();
    set_attributes(&source.join("desktop.ini"), HIDDEN | SYSTEM);
    set_attributes(&hidden_folder.join("inside.txt"), HIDDEN);
    set_attributes(&hidden_folder, HIDDEN);
    let destination = root.join("destination");
    fs::create_dir_all(&destination).unwrap();

    copy("copy", &source, &destination);

    let copied = destination.join("tree");
    assert_eq!(shown(&copied.join("desktop.ini")), HIDDEN | SYSTEM);
    assert_eq!(shown(&copied.join("plain.txt")), 0);
    assert_eq!(shown(&copied.join("hidden")), HIDDEN);
    assert_eq!(shown(&copied.join("hidden").join("inside.txt")), HIDDEN);
    assert_eq!(
        fs::read(copied.join("hidden").join("inside.txt")).unwrap(),
        b"inside"
    );
    let _ = fs::remove_dir_all(&root);
}
