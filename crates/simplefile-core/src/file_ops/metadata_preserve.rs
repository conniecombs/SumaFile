use std::fs;
use std::path::Path;

/// The source's times and attributes applied to `dst` by path. Used for
/// directories and symlinks, and by callers without an open destination handle.
/// Files written through an open handle use `apply_basic_metadata_to_handle`.
///
/// Security descriptors are deliberately not copied: like Explorer, a copied
/// file inherits the permissions of the folder it lands in.
#[cfg(windows)]
pub fn preserve_basic_metadata(src: &Path, dst: &Path) -> Result<(), String> {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_READ_ATTRIBUTES, FILE_WRITE_ATTRIBUTES,
    };

    let metadata = fs::metadata(src).map_err(|e| format!("Failed to stat copied source: {e}"))?;
    // Attribute access is all that setting times and attributes needs, and
    // backup semantics is what opens a folder: one handle for files and
    // folders alike, with every time (the creation time too) and every
    // attribute set in one call.
    let handle = fs::OpenOptions::new()
        .access_mode(FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
        .open(dst)
        .map_err(|e| format!("Failed to open the copy to preserve its metadata: {e}"))?;
    apply_basic_metadata_to_handle(&handle, &metadata)
}

#[cfg(not(windows))]
pub fn preserve_basic_metadata(src: &Path, dst: &Path) -> Result<(), String> {
    let metadata = fs::metadata(src).map_err(|e| format!("Failed to stat copied source: {e}"))?;
    filetime::set_file_times(
        dst,
        filetime::FileTime::from_last_access_time(&metadata),
        filetime::FileTime::from_last_modification_time(&metadata),
    )
    .map_err(|e| format!("Failed to preserve file timestamps: {e}"))?;
    fs::set_permissions(dst, metadata.permissions())
        .map_err(|e| format!("Failed to preserve permissions: {e}"))
}

/// The attributes a copy takes from its source: read-only, hidden, system
/// and not content indexed. The others are the destination's own: archive,
/// which Windows sets on every new file, and those a copy cannot set this
/// way (compressed, encrypted, sparse, offline).
#[cfg(windows)]
const CARRIED_ATTRIBUTES: u32 = {
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_NOT_CONTENT_INDEXED, FILE_ATTRIBUTE_READONLY,
        FILE_ATTRIBUTE_SYSTEM,
    };
    FILE_ATTRIBUTE_READONLY
        | FILE_ATTRIBUTE_HIDDEN
        | FILE_ATTRIBUTE_SYSTEM
        | FILE_ATTRIBUTE_NOT_CONTENT_INDEXED
};

/// The attributes the copy ends with: its own, with the carried ones taken
/// from the source. A partial file's Hidden attribute goes with it unless the
/// source is hidden too.
#[cfg(windows)]
fn carried_attributes(destination: u32, source: u32) -> u32 {
    use windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_NORMAL;
    // The attributes a set may name (FILE_ATTRIBUTE_VALID_SET_FLAGS): read-only,
    // hidden, system, archive, normal, temporary, offline, not content indexed.
    // The folder attribute and the others are what the file is, not settings.
    const SETTABLE: u32 = 0x31a7;
    let attributes = (destination & SETTABLE & !FILE_ATTRIBUTE_NORMAL & !CARRIED_ATTRIBUTES)
        | (source & CARRIED_ATTRIBUTES);
    if attributes == 0 {
        FILE_ATTRIBUTE_NORMAL
    } else {
        attributes
    }
}

/// Apply the source's creation, access and write times and its carried
/// attributes (see `CARRIED_ATTRIBUTES`) to an open destination, in one call.
/// This also clears the Hidden attribute that marks an unpublished partial
/// file, unless the source is hidden. Call after the last write and flush: the
/// system stops updating a timestamp on a handle once it has been set
/// explicitly, so the values survive the close.
///
/// When the destination refuses the call (FAT keeps no date before 1980, and
/// some shares refuse a creation time), the attributes and the times are set
/// apart, so that the attributes, which decide whether the copy can be seen,
/// do not go with the times. The error then says what was not kept.
#[cfg(windows)]
pub fn apply_basic_metadata_to_handle(file: &fs::File, source: &fs::Metadata) -> Result<(), String> {
    use std::os::windows::fs::MetadataExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        FileBasicInfo, GetFileInformationByHandleEx, FILE_BASIC_INFO,
    };

    let handle = file.as_raw_handle() as _;
    let mut info: FILE_BASIC_INFO = unsafe { std::mem::zeroed() };
    let got = unsafe {
        GetFileInformationByHandleEx(
            handle,
            FileBasicInfo,
            (&mut info as *mut FILE_BASIC_INFO).cast(),
            std::mem::size_of::<FILE_BASIC_INFO>() as u32,
        )
    };
    if got == 0 {
        return Err(format!(
            "Failed to read destination attributes: {}",
            std::io::Error::last_os_error()
        ));
    }

    // File times are 100 ns ticks since 1601, well inside an i64.
    info.CreationTime = source.creation_time() as i64;
    info.LastAccessTime = source.last_access_time() as i64;
    info.LastWriteTime = source.last_write_time() as i64;
    // Zero leaves the change time to the system.
    info.ChangeTime = 0;
    info.FileAttributes = carried_attributes(info.FileAttributes, source.file_attributes());

    let Err(both) = set_basic_info(handle, &info) else {
        return Ok(());
    };
    // A zero time and zero attributes each mean "leave as it is".
    let attributes_only = FILE_BASIC_INFO {
        CreationTime: 0,
        LastAccessTime: 0,
        LastWriteTime: 0,
        ..info
    };
    let times_only = FILE_BASIC_INFO {
        FileAttributes: 0,
        ..info
    };
    match (
        set_basic_info(handle, &attributes_only),
        set_basic_info(handle, &times_only),
    ) {
        (Ok(()), Ok(())) => Ok(()),
        (Ok(()), Err(error)) => Err(format!("The destination refused the file's times: {error}")),
        (Err(error), Ok(())) => Err(format!(
            "The destination refused the file's attributes: {error}"
        )),
        (Err(_), Err(_)) => Err(format!(
            "The destination refused the file's times and attributes: {both}"
        )),
    }
}

#[cfg(windows)]
fn set_basic_info(
    handle: windows_sys::Win32::Foundation::HANDLE,
    info: &windows_sys::Win32::Storage::FileSystem::FILE_BASIC_INFO,
) -> std::io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::{
        FileBasicInfo, SetFileInformationByHandle, FILE_BASIC_INFO,
    };
    #[cfg(test)]
    if tests::refuses_times()
        && (info.CreationTime != 0 || info.LastAccessTime != 0 || info.LastWriteTime != 0)
    {
        // What FAT answers for a time it cannot hold.
        return Err(std::io::Error::from_raw_os_error(87));
    }
    let ok = unsafe {
        SetFileInformationByHandle(
            handle,
            FileBasicInfo,
            (info as *const FILE_BASIC_INFO).cast(),
            std::mem::size_of::<FILE_BASIC_INFO>() as u32,
        )
    };
    if ok == 0 {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(not(windows))]
pub fn apply_basic_metadata_to_handle(file: &fs::File, source: &fs::Metadata) -> Result<(), String> {
    let times = fs::FileTimes::new()
        .set_accessed(source.accessed().map_err(|e| e.to_string())?)
        .set_modified(source.modified().map_err(|e| e.to_string())?);
    file.set_times(times)
        .map_err(|e| format!("Failed to preserve file timestamps: {e}"))?;
    file.set_permissions(source.permissions())
        .map_err(|e| format!("Failed to preserve permissions: {e}"))
}

#[cfg(all(test, windows))]
mod tests {
    use super::{apply_basic_metadata_to_handle, preserve_basic_metadata};
    use std::cell::Cell;
    use std::fs;
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
    use std::path::{Path, PathBuf};
    use std::time::{Duration, SystemTime};
    use windows_sys::Win32::Storage::FileSystem::{
        SetFileAttributesW, FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_READONLY,
        FILE_ATTRIBUTE_SYSTEM,
    };

    thread_local! {
        static REFUSE_TIMES: Cell<bool> = const { Cell::new(false) };
    }

    /// Whether this thread's destination refuses every time it is given.
    pub(super) fn refuses_times() -> bool {
        REFUSE_TIMES.get()
    }

    fn folder(label: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "simplefile-metadata-{label}-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&path).unwrap();
        path
    }

    fn set_attributes(path: &Path, attributes: u32) {
        let wide: Vec<u16> = path.as_os_str().encode_wide().chain([0]).collect();
        assert_ne!(
            unsafe { SetFileAttributesW(wide.as_ptr(), attributes) },
            0,
            "{}",
            path.display()
        );
    }

    fn attributes(path: &Path) -> u32 {
        fs::symlink_metadata(path).unwrap().file_attributes()
    }

    /// A file created the way a partial file is: hidden.
    fn partial(path: &Path) -> fs::File {
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .attributes(FILE_ATTRIBUTE_HIDDEN)
            .open(path)
            .unwrap()
    }

    fn remove(root: &Path) {
        for entry in walk(root) {
            set_attributes(&entry, FILE_ATTRIBUTE_NORMAL);
        }
        let _ = fs::remove_dir_all(root);
    }

    fn walk(root: &Path) -> Vec<PathBuf> {
        let mut found = vec![root.to_path_buf()];
        let mut index = 0;
        while index < found.len() {
            if found[index].is_dir() {
                let children: Vec<PathBuf> = fs::read_dir(&found[index])
                    .unwrap()
                    .map(|entry| entry.unwrap().path())
                    .collect();
                found.extend(children);
            }
            index += 1;
        }
        found
    }

    #[test]
    fn a_copy_takes_hidden_system_and_read_only_from_its_source() {
        let root = folder("carried");
        let source = root.join("source.ini");
        fs::write(&source, b"[.ShellClassInfo]").unwrap();
        set_attributes(
            &source,
            FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_READONLY,
        );
        let copy = root.join("copy.ini");
        let file = partial(&copy);

        apply_basic_metadata_to_handle(&file, &fs::metadata(&source).unwrap()).unwrap();
        drop(file);

        let carried = FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_READONLY;
        assert_eq!(attributes(&copy) & carried, carried);
        remove(&root);
    }

    #[test]
    fn the_copy_of_a_visible_file_is_visible() {
        let root = folder("visible");
        let source = root.join("source.txt");
        fs::write(&source, b"visible").unwrap();
        let copy = root.join("copy.txt");
        let file = partial(&copy);

        apply_basic_metadata_to_handle(&file, &fs::metadata(&source).unwrap()).unwrap();
        drop(file);

        let shown = FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_READONLY;
        assert_eq!(attributes(&copy) & shown, 0);
        remove(&root);
    }

    #[test]
    fn a_destination_that_refuses_the_times_still_gets_the_attributes() {
        let root = folder("refused");
        let source = root.join("source.txt");
        fs::write(&source, b"refused").unwrap();
        let copy = root.join("copy.txt");
        let file = partial(&copy);

        REFUSE_TIMES.set(true);
        let applied = apply_basic_metadata_to_handle(&file, &fs::metadata(&source).unwrap());
        REFUSE_TIMES.set(false);
        drop(file);

        let error = applied.expect_err("the times were refused");
        assert!(error.contains("refused the file's times"), "{error}");
        assert_eq!(
            attributes(&copy) & FILE_ATTRIBUTE_HIDDEN,
            0,
            "the copy kept the Hidden attribute of its partial file"
        );
        remove(&root);
    }

    #[test]
    fn a_folder_takes_its_times_and_hidden_and_system_from_its_source() {
        let root = folder("folder");
        let source = root.join("source");
        let copy = root.join("copy");
        fs::create_dir(&source).unwrap();
        fs::create_dir(&copy).unwrap();
        let created = SystemTime::UNIX_EPOCH + Duration::from_secs(1_500_000_000);
        let handle = fs::OpenOptions::new()
            .access_mode(windows_sys::Win32::Storage::FileSystem::FILE_WRITE_ATTRIBUTES)
            .custom_flags(windows_sys::Win32::Storage::FileSystem::FILE_FLAG_BACKUP_SEMANTICS)
            .open(&source)
            .unwrap();
        handle
            .set_times(
                fs::FileTimes::new()
                    .set_modified(created)
                    .set_accessed(created),
            )
            .unwrap();
        drop(handle);
        set_attributes(&source, FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM);

        preserve_basic_metadata(&source, &copy).unwrap();

        let copied = fs::metadata(&copy).unwrap();
        let original = fs::metadata(&source).unwrap();
        assert!(copied.is_dir());
        assert_eq!(copied.modified().unwrap(), created);
        assert_eq!(copied.creation_time(), original.creation_time());
        let carried = FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM;
        assert_eq!(attributes(&copy) & carried, carried);
        remove(&root);
    }
}
