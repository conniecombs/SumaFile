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

/// A copied download keeps the mark that makes Windows ask before it is run,
/// in a copy and in a move that copies (here forced by a verbatim
/// destination, which the service treats as another drive). The mark is an
/// alternate data stream, and the copy used to carry only the file's bytes.
#[cfg(windows)]
#[test]
fn a_copied_or_moved_download_keeps_its_mark_and_its_other_streams() {
    let mark = b"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/\r\n".to_vec();
    for operation in ["copy", "move"] {
        let root = unique_root(&format!("streams_{operation}"));
        let source = root.join("source");
        fs::create_dir_all(source.join("folder")).unwrap();
        let file = source.join("folder").join("setup.exe");
        fs::write(&file, b"MZ").unwrap();
        fs::write(
            source.join("folder").join("setup.exe:Zone.Identifier"),
            &mark,
        )
        .unwrap();
        fs::write(source.join("folder").join("setup.exe:notes"), b"kept").unwrap();
        let destination = root.join("destination");
        fs::create_dir_all(&destination).unwrap();
        let destination = if operation == "move" {
            PathBuf::from(format!(r"\\?\{}", destination.display()))
        } else {
            destination
        };

        copy(operation, &source.join("folder"), &destination);

        let copied = destination.join("folder");
        assert_eq!(fs::read(copied.join("setup.exe")).unwrap(), b"MZ");
        assert_eq!(
            fs::read(copied.join("setup.exe:Zone.Identifier")).unwrap(),
            mark,
            "{operation}"
        );
        assert_eq!(fs::read(copied.join("setup.exe:notes")).unwrap(), b"kept");
        assert_eq!(file.exists(), operation == "copy");
        let _ = fs::remove_dir_all(&root);
    }
}

/// A file whose name is as long as a name can be is copied, and replaced.
/// The partial file's name added 35 characters to the file's own, more than
/// a name can hold, so any name longer than about 220 characters failed.
#[test]
fn a_file_with_a_name_as_long_as_a_name_can_be_is_copied_and_replaced() {
    let root = unique_root("long_name");
    let name = "n".repeat(251) + ".txt";
    let source = root.join("source");
    fs::create_dir_all(&source).unwrap();
    fs::write(source.join(&name), b"first").unwrap();
    let destination = root.join("destination");
    fs::create_dir_all(&destination).unwrap();

    copy("copy", &source.join(&name), &destination);
    assert_eq!(fs::read(destination.join(&name)).unwrap(), b"first");

    fs::write(source.join(&name), b"second").unwrap();
    transfer_with_progress_blocking(
        "copy",
        vec![source.join(&name).to_string_lossy().into_owned()],
        destination.to_string_lossy().into_owned(),
        super::generate_transfer_operation_id(),
        "replace".to_string(),
        Arc::new(AtomicBool::new(false)),
        &|_| {},
    )
    .unwrap_or_else(|error| panic!("the Replace succeeds: {}", error.message));

    assert_eq!(fs::read(destination.join(&name)).unwrap(), b"second");
    let left: Vec<String> = fs::read_dir(&destination)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .collect();
    assert_eq!(left, vec![name], "only the file is left");
    let _ = fs::remove_dir_all(&root);
}

/// A letter given to a folder with `subst`, removed again when dropped.
#[cfg(windows)]
pub(crate) struct Substituted(char);

#[cfg(windows)]
impl Substituted {
    pub(crate) fn new(folder: &Path) -> Option<Self> {
        let letter = ('M'..='Z')
            .rev()
            .find(|letter| !Path::new(&format!("{letter}:\\")).exists())?;
        let made = std::process::Command::new("subst")
            .arg(format!("{letter}:"))
            .arg(folder)
            .status()
            .ok()?
            .success();
        made.then_some(Self(letter))
    }
    pub(crate) fn path(&self, rest: &str) -> PathBuf {
        PathBuf::from(format!("{}:\\{rest}", self.0))
    }
}

#[cfg(windows)]
impl Drop for Substituted {
    fn drop(&mut self) {
        let _ = std::process::Command::new("subst")
            .arg(format!("{}:", self.0))
            .arg("/D")
            .status();
    }
}

/// A move from a `subst` letter into the folder's own drive renames each
/// file: nothing is written. It used to copy every file and delete its
/// source, because the two paths begin with different letters.
#[cfg(windows)]
#[test]
fn a_move_from_a_substituted_drive_into_its_own_volume_renames_the_file() {
    use sumafile_transfer::ops::{Counter, Op};

    let root = unique_root("subst_move");
    fs::create_dir_all(root.join("source")).unwrap();
    fs::create_dir_all(root.join("destination")).unwrap();
    fs::write(root.join("source").join("file.bin"), b"moved").unwrap();
    let Some(letter) = Substituted::new(&root) else {
        eprintln!("skipped: no free drive letter for subst");
        let _ = fs::remove_dir_all(&root);
        return;
    };
    let counter = Counter::under(&root);

    copy(
        "move",
        &letter.path(r"source\file.bin"),
        &root.join("destination"),
    );

    let counts = counter.counts();
    assert_eq!(
        fs::read(root.join("destination").join("file.bin")).unwrap(),
        b"moved"
    );
    assert!(!root.join("source").join("file.bin").exists());
    assert_eq!(counts.get(Op::Write), 0, "{}", counts.describe());
    drop(letter);
    let _ = fs::remove_dir_all(&root);
}
