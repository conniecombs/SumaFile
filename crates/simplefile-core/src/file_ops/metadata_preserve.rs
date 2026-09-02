use std::fs;
use std::path::Path;

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
        .map_err(|e| format!("Failed to preserve permissions: {e}"))?;
    preserve_platform_metadata(src, dst)
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

#[cfg(windows)]
fn preserve_platform_metadata(src: &Path, dst: &Path) -> Result<(), String> {
    preserve_windows_dacl(src, dst)
}

#[cfg(not(windows))]
fn preserve_platform_metadata(_src: &Path, _dst: &Path) -> Result<(), String> {
    Ok(())
}

#[cfg(windows)]
fn preserve_windows_dacl(src: &Path, dst: &Path) -> Result<(), String> {
    use std::os::windows::ffi::OsStrExt;
    use winapi::um::accctrl::SE_FILE_OBJECT;
    use winapi::um::aclapi::{GetNamedSecurityInfoW, SetNamedSecurityInfoW};
    use winapi::um::winbase::LocalFree;
    use winapi::um::winnt::{
        DACL_SECURITY_INFORMATION, PACL, PSECURITY_DESCRIPTOR, SECURITY_INFORMATION,
    };

    let mut src_wide = src
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<u16>>();
    let mut dst_wide = dst
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect::<Vec<u16>>();
    let mut dacl: PACL = std::ptr::null_mut();
    let mut descriptor: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    let security_info: SECURITY_INFORMATION = DACL_SECURITY_INFORMATION;

    let get_result = unsafe {
        GetNamedSecurityInfoW(
            src_wide.as_mut_ptr(),
            SE_FILE_OBJECT,
            security_info,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            &mut dacl,
            std::ptr::null_mut(),
            &mut descriptor,
        )
    };
    if get_result != 0 {
        return Ok(());
    }

    let set_result = unsafe {
        SetNamedSecurityInfoW(
            dst_wide.as_mut_ptr(),
            SE_FILE_OBJECT,
            security_info,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            dacl,
            std::ptr::null_mut(),
        )
    };

    if !descriptor.is_null() {
        unsafe {
            LocalFree(descriptor as _);
        }
    }

    if set_result != 0 {
        return Ok(());
    }

    Ok(())
}
