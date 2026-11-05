use std::fs;
use std::path::Path;

/// Timestamps and the read-only bit from `src` applied to `dst` by path. Used for
/// directories and symlinks, and by callers without an open destination handle.
/// Files written through an open handle use `apply_basic_metadata_to_handle`.
///
/// Security descriptors are deliberately not copied: like Explorer, a copied
/// file inherits the permissions of the folder it lands in.
pub fn preserve_basic_metadata(src: &Path, dst: &Path) -> Result<(), String> {
    let metadata = fs::metadata(src).map_err(|e| format!("Failed to stat copied source: {e}"))?;
    filetime::set_file_times(
        dst,
        filetime::FileTime::from_last_access_time(&metadata),
        filetime::FileTime::from_last_modification_time(&metadata),
    )
    .map_err(|e| format!("Failed to preserve file timestamps: {e}"))?;
    preserve_creation_time(&metadata, dst)?;
    fs::set_permissions(dst, metadata.permissions())
        .map_err(|e| format!("Failed to preserve permissions: {e}"))
}

/// Apply the source's creation, access and write times and its read-only bit to
/// an open destination file in one call, and clear the hidden attribute that
/// marks an unpublished staging file. Call after the last write and flush: the
/// system stops updating a timestamp on a handle once it has been set
/// explicitly, so the values survive the close.
#[cfg(windows)]
pub fn apply_basic_metadata_to_handle(
    file: &fs::File,
    source: &fs::Metadata,
    clear_hidden: bool,
) -> Result<(), String> {
    use std::os::windows::fs::MetadataExt;
    use std::os::windows::io::AsRawHandle;
    use winapi::um::fileapi::{GetFileInformationByHandleEx, SetFileInformationByHandle};
    use winapi::um::minwinbase::{FileBasicInfo, FILE_BASIC_INFO};
    use winapi::um::winnt::{
        FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_READONLY, LARGE_INTEGER,
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

    fn large(value: u64) -> LARGE_INTEGER {
        let mut out: LARGE_INTEGER = unsafe { std::mem::zeroed() };
        unsafe {
            *out.QuadPart_mut() = value as i64;
        }
        out
    }

    info.CreationTime = large(source.creation_time());
    info.LastAccessTime = large(source.last_access_time());
    info.LastWriteTime = large(source.last_write_time());
    // Zero leaves the change time to the system.
    info.ChangeTime = large(0);
    let mut attributes = info.FileAttributes;
    if clear_hidden {
        attributes &= !FILE_ATTRIBUTE_HIDDEN;
    }
    if source.permissions().readonly() {
        attributes |= FILE_ATTRIBUTE_READONLY;
    } else {
        attributes &= !FILE_ATTRIBUTE_READONLY;
    }
    if attributes == 0 {
        attributes = FILE_ATTRIBUTE_NORMAL;
    }
    info.FileAttributes = attributes;

    let ok = unsafe {
        SetFileInformationByHandle(
            handle,
            FileBasicInfo,
            (&info as *const FILE_BASIC_INFO).cast(),
            std::mem::size_of::<FILE_BASIC_INFO>() as u32,
        )
    };
    if ok == 0 {
        return Err(format!(
            "Failed to preserve file metadata: {}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(())
}

#[cfg(not(windows))]
pub fn apply_basic_metadata_to_handle(
    file: &fs::File,
    source: &fs::Metadata,
    _clear_hidden: bool,
) -> Result<(), String> {
    let times = fs::FileTimes::new()
        .set_accessed(source.accessed().map_err(|e| e.to_string())?)
        .set_modified(source.modified().map_err(|e| e.to_string())?);
    file.set_times(times)
        .map_err(|e| format!("Failed to preserve file timestamps: {e}"))?;
    file.set_permissions(source.permissions())
        .map_err(|e| format!("Failed to preserve permissions: {e}"))
}

#[cfg(windows)]
fn preserve_creation_time(metadata: &fs::Metadata, dst: &Path) -> Result<(), String> {
    use std::os::windows::fs::MetadataExt;
    use std::os::windows::io::AsRawHandle;
    use winapi::shared::minwindef::FILETIME;
    use winapi::um::fileapi::SetFileTime;

    if !metadata.is_file() {
        return Ok(());
    }

    let created = metadata.creation_time();
    let creation_time = FILETIME {
        dwLowDateTime: created as u32,
        dwHighDateTime: (created >> 32) as u32,
    };
    let file = fs::OpenOptions::new().write(true).open(dst).map_err(|e| {
        format!(
            "Failed to open destination to preserve creation time: {}",
            e
        )
    })?;
    let ok = unsafe {
        SetFileTime(
            file.as_raw_handle() as _,
            &creation_time,
            std::ptr::null(),
            std::ptr::null(),
        )
    };
    if ok == 0 {
        Err(format!(
            "Failed to preserve creation time: {}",
            std::io::Error::last_os_error()
        ))
    } else {
        Ok(())
    }
}

#[cfg(not(windows))]
fn preserve_creation_time(_metadata: &fs::Metadata, _dst: &Path) -> Result<(), String> {
    Ok(())
}
