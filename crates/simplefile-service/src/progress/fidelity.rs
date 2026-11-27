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

/// Transfers `source` into `destination` with the conflict answer and the
/// settings given, and returns what the service answered.
#[cfg(windows)]
fn transfer_with(
    operation: &'static str,
    source: &Path,
    destination: &Path,
    conflict: &str,
    policy: super::tuning::Policy,
) -> Result<Vec<super::TransferResult>, super::TransferBatchError> {
    super::transfer_with_policy_blocking(
        operation,
        super::Request::Selection {
            sources: vec![source.to_string_lossy().into_owned()],
            conflict_action: conflict.to_string(),
        },
        destination.to_string_lossy().into_owned(),
        super::generate_transfer_operation_id(),
        Arc::new(AtomicBool::new(false)),
        &|_| {},
        &|_| {},
        &move || policy.clone(),
    )
}

/// Settings that give up on a failing file at once.
#[cfg(windows)]
fn without_retries() -> super::tuning::Policy {
    super::tuning::Policy {
        low_level_retries: 1,
        high_level_retries: 1,
        retry_sleep_ms: 10,
        ..super::tuning::Policy::default()
    }
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

/// Runs `icacls` with `arguments`; false when it is missing or refuses.
#[cfg(windows)]
fn icacls(arguments: &[&std::ffi::OsStr]) -> bool {
    std::process::Command::new("icacls")
        .args(arguments)
        .stdout(std::process::Stdio::null())
        .status()
        .is_ok_and(|status| status.success())
}

/// "Write directly" asked for delete access on the file it creates, which it
/// never uses: only a partial file is renamed through its handle. A folder
/// where users may create files but not delete them, a drop box, refused
/// every file, and "Write directly" was the one setting that worked there.
#[cfg(windows)]
#[test]
fn write_directly_copies_into_a_folder_where_files_cannot_be_deleted() {
    use std::ffi::OsStr;

    let root = unique_root("no_delete");
    let source = root.join("source");
    fs::create_dir_all(&source).unwrap();
    fs::write(source.join("report.txt"), b"report").unwrap();
    let drop_box = root.join("drop box");
    fs::create_dir_all(&drop_box).unwrap();
    let user = std::env::var("USERNAME").unwrap_or_default();
    let denied = !user.is_empty()
        && icacls(&[
            drop_box.as_os_str(),
            OsStr::new("/deny"),
            OsStr::new(&format!("{user}:(DC)")),
        ])
        && icacls(&[
            drop_box.as_os_str(),
            OsStr::new("/deny"),
            OsStr::new(&format!("{user}:(OI)(IO)(D)")),
        ]);
    if !denied {
        eprintln!("skipped: icacls could not deny deleting in the folder");
        let _ = fs::remove_dir_all(&root);
        return;
    }

    let result = transfer_with(
        "copy",
        &source.join("report.txt"),
        &drop_box,
        "error",
        super::tuning::Policy {
            inplace: true,
            ..super::tuning::Policy::default()
        },
    );

    icacls(&[
        drop_box.as_os_str(),
        OsStr::new("/reset"),
        OsStr::new("/T"),
        OsStr::new("/Q"),
    ]);
    result.unwrap_or_else(|error| panic!("the copy succeeds: {}", error.message));
    assert_eq!(fs::read(drop_box.join("report.txt")).unwrap(), b"report");
    let _ = fs::remove_dir_all(&root);
}

/// A move whose source's streams could not be copied keeps its source. The
/// failure was only logged; the file got its name and the source was deleted
/// with the streams, among them the mark that makes Windows ask before a
/// downloaded file is run.
#[cfg(windows)]
#[test]
fn a_move_whose_streams_cannot_be_copied_fails_and_keeps_its_source() {
    use std::os::windows::fs::OpenOptionsExt;

    let root = unique_root("held_stream_move");
    let source = root.join("source");
    fs::create_dir_all(&source).unwrap();
    let file = source.join("setup.exe");
    fs::write(&file, b"MZ").unwrap();
    let mark = source.join("setup.exe:Zone.Identifier");
    fs::write(&mark, b"[ZoneTransfer]\r\nZoneId=3\r\n").unwrap();
    let destination = root.join("destination");
    fs::create_dir_all(&destination).unwrap();
    // A verbatim destination is another drive to the service: a copy and a
    // delete, not a rename.
    let destination = PathBuf::from(format!(r"\\?\{}", destination.display()));
    // Nobody else may read the stream, as while an antivirus scans it.
    let holder = fs::OpenOptions::new()
        .read(true)
        .share_mode(0)
        .open(&mark)
        .unwrap();

    let result = transfer_with("move", &file, &destination, "error", without_retries());

    drop(holder);
    let error = result.expect_err("the move fails");
    assert!(
        error.message.contains("alternate data streams"),
        "{}",
        error.message
    );
    assert_eq!(fs::read(&file).unwrap(), b"MZ", "the source is kept");
    assert!(fs::read(&mark).unwrap().starts_with(b"[ZoneTransfer]"));
    assert!(!destination.join("setup.exe").exists());
    let _ = fs::remove_dir_all(&root);
}

/// A move that takes two paths for one volume when they are not (a folder
/// where another volume is mounted) has its rename refused, and copies the
/// item instead. With Replace and a destination that existed, the refusal
/// went into the swap with the existing file, which was refused the same way,
/// and the item failed. Here `/dev/shm` is the other volume.
#[cfg(target_os = "linux")]
#[test]
fn a_replace_move_onto_a_volume_taken_for_the_same_one_is_copied() {
    use std::os::unix::fs::MetadataExt;

    let other = Path::new("/dev/shm");
    let root = unique_root("cross_volume_replace");
    fs::create_dir_all(&root).unwrap();
    let separate =
        fs::metadata(other).is_ok_and(|meta| meta.dev() != fs::metadata(&root).unwrap().dev());
    if !separate {
        eprintln!("skipped: /dev/shm is not a volume of its own");
        let _ = fs::remove_dir_all(&root);
        return;
    }
    let destination = other.join(format!(
        "sumafile_fidelity_cross_{}",
        super::generate_transfer_operation_id()
    ));
    fs::create_dir_all(&destination).unwrap();
    fs::write(destination.join("a.txt"), b"old").unwrap();
    fs::write(root.join("a.txt"), b"new").unwrap();

    let result = transfer_with_progress_blocking(
        "move",
        vec![root.join("a.txt").to_string_lossy().into_owned()],
        destination.to_string_lossy().into_owned(),
        super::generate_transfer_operation_id(),
        "replace".to_string(),
        Arc::new(AtomicBool::new(false)),
        &|_| {},
    );

    let outcome = result.map(|_| ()).map_err(|error| error.message);
    let copied = fs::read(destination.join("a.txt"));
    let _ = fs::remove_dir_all(&destination);
    let _ = fs::remove_dir_all(&root);
    outcome.unwrap();
    assert_eq!(copied.unwrap(), b"new");
}
