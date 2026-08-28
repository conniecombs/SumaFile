use crate::models::DriveInfo;
use std::sync::mpsc;
use std::time::Duration;

/// Bound network probe time so one dead mapped drive cannot hang listing.
const NETWORK_PROBE_TIMEOUT: Duration = Duration::from_secs(3);

/// Enumerate fixed, removable, and mapped drives. Blocking.
pub fn list_drives() -> Result<Vec<DriveInfo>, String> {
    list_drives_blocking()
}

fn string_from_wide_buffer(buffer: &[u16]) -> Option<String> {
    let len = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
    let value = String::from_utf16_lossy(&buffer[..len]).trim().to_string();
    if value.is_empty() {
        None
    } else {
        Some(value)
    }
}

fn windows_volume_label(wide_path: &[u16]) -> Option<String> {
    use std::ptr::null_mut;

    let mut volume_name = [0u16; 260];
    let has_label = unsafe {
        winapi::um::fileapi::GetVolumeInformationW(
            wide_path.as_ptr(),
            volume_name.as_mut_ptr(),
            volume_name.len() as u32,
            null_mut(),
            null_mut(),
            null_mut(),
            null_mut(),
            0,
        ) != 0
            && volume_name[0] != 0
    };

    has_label
        .then(|| string_from_wide_buffer(&volume_name))
        .flatten()
}

fn mapped_network_remote_path(drive_path: &str) -> Option<String> {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use winapi::shared::minwindef::DWORD;
    use winapi::shared::winerror::{ERROR_MORE_DATA, NO_ERROR};
    use winapi::um::winnetwk::WNetGetConnectionW;

    let local_name = drive_path.trim_end_matches(['\\', '/']);
    let wide_local: Vec<u16> = OsStr::new(local_name)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    let mut len: DWORD = 260;
    let mut remote_name = vec![0u16; len as usize];
    let mut result =
        unsafe { WNetGetConnectionW(wide_local.as_ptr(), remote_name.as_mut_ptr(), &mut len) };

    if result == ERROR_MORE_DATA {
        remote_name.resize(len as usize, 0);
        result =
            unsafe { WNetGetConnectionW(wide_local.as_ptr(), remote_name.as_mut_ptr(), &mut len) };
    }

    if result != NO_ERROR {
        return None;
    }

    string_from_wide_buffer(&remote_name)
}

fn network_remote_display_name(remote_path: &str) -> Option<String> {
    let trimmed = remote_path.trim();
    let without_unc_prefix = trimmed.trim_start_matches(['\\', '/']);
    let mut parts = without_unc_prefix
        .split(['\\', '/'])
        .filter(|part| !part.is_empty());

    match (parts.next(), parts.next()) {
        (Some(server), Some(share)) => Some(format!("{share} on {server}")),
        _ if !trimmed.is_empty() => Some(trimmed.to_string()),
        _ => None,
    }
}

fn windows_error_detail(error_code: u32) -> String {
    use winapi::shared::winerror::{
        ERROR_ACCESS_DENIED, ERROR_BAD_NETPATH, ERROR_BAD_NET_NAME, ERROR_NOT_READY,
        ERROR_PATH_NOT_FOUND,
    };

    match error_code {
        ERROR_BAD_NETPATH => {
            "Network path was not found. Check the server name or VPN connection.".to_string()
        }
        ERROR_BAD_NET_NAME => {
            "Network share was not found. The server may be online but the share is unavailable."
                .to_string()
        }
        ERROR_PATH_NOT_FOUND => {
            "Mapped path was not found. Open the drive to reconnect or remap it.".to_string()
        }
        ERROR_ACCESS_DENIED => {
            "Access was denied. Reconnect with the right credentials.".to_string()
        }
        ERROR_NOT_READY => "Drive is not ready. Open it to reconnect.".to_string(),
        0 => "Windows reported the mapped drive as unavailable.".to_string(),
        code => format!("Windows error {code}. Open the drive to reconnect or check credentials."),
    }
}

/// Probe a network root with a timeout so disconnected maps fail fast.
/// Returns Ok(attributes) when reachable, Err(Some(code)) on Windows error,
/// Err(None) on probe timeout.
fn probe_network_attributes(wide_path: &[u16]) -> Result<u32, Option<u32>> {
    use winapi::um::fileapi::{GetFileAttributesW, INVALID_FILE_ATTRIBUTES};

    let (tx, rx) = mpsc::channel();
    let wide = wide_path.to_vec();
    std::thread::spawn(move || {
        let attributes = unsafe { GetFileAttributesW(wide.as_ptr()) };
        let result = if attributes == INVALID_FILE_ATTRIBUTES {
            let error_code = std::io::Error::last_os_error()
                .raw_os_error()
                .unwrap_or(0)
                .max(0) as u32;
            Err(Some(error_code))
        } else {
            Ok(attributes)
        };
        let _ = tx.send(result);
    });

    match rx.recv_timeout(NETWORK_PROBE_TIMEOUT) {
        Ok(Ok(attributes)) => Ok(attributes),
        Ok(Err(code)) => Err(code),
        Err(mpsc::RecvTimeoutError::Timeout) => Err(None),
        Err(mpsc::RecvTimeoutError::Disconnected) => Err(Some(0)),
    }
}

fn network_drive_status(wide_path: &[u16], remote_path: Option<&str>) -> (String, Option<String>) {
    match probe_network_attributes(wide_path) {
        Ok(_) => {
            let detail = remote_path
                .map(|remote| format!("Connected to {remote}"))
                .or_else(|| {
                    Some("Network drive is reachable; share name is unavailable.".to_string())
                });
            ("available".to_string(), detail)
        }
        Err(None) => {
            let share = remote_path
                .map(|remote| format!(" Share: {remote}."))
                .unwrap_or_default();
            (
                "offline".to_string(),
                Some(format!(
                    "Timed out after {}s waiting for the network share.{share} Check VPN, Wi‑Fi, or the server, then retry.",
                    NETWORK_PROBE_TIMEOUT.as_secs()
                )),
            )
        }
        Err(Some(error_code)) if remote_path.is_some() => (
            "offline".to_string(),
            Some(format!(
                "{} The mapping is still present; open the drive or retry to reconnect.",
                windows_error_detail(error_code)
            )),
        ),
        Err(Some(error_code)) => (
            "stale".to_string(),
            Some(format!(
                "{} Windows did not return a share name for this mapping; reconnect or remove the stale drive.",
                windows_error_detail(error_code)
            )),
        ),
    }
}

fn windows_drive_display_name(
    drive_type: u32,
    wide_path: &[u16],
    remote_path: Option<&str>,
    fallback_name: &str,
) -> String {
    match drive_type {
        3 => windows_volume_label(wide_path),
        4 => remote_path.and_then(network_remote_display_name),
        _ => None,
    }
    .unwrap_or_else(|| fallback_name.to_string())
}

fn list_drives_blocking() -> Result<Vec<DriveInfo>, String> {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    struct PendingDrive {
        letter: u8,
        drive_path: String,
        wide_path: Vec<u16>,
        dt: u32,
        drive_type: String,
        fallback_name: &'static str,
        remote_path: Option<String>,
    }

    let mut pending: Vec<PendingDrive> = Vec::new();

    for letter in b'A'..=b'Z' {
        let drive_path = format!("{}:\\", letter as char);
        let wide_path: Vec<u16> = OsStr::new(&drive_path)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();

        let dt = unsafe { winapi::um::fileapi::GetDriveTypeW(wide_path.as_ptr()) };
        if dt <= 1 {
            continue;
        }

        let drive_type = match dt {
            2 => "Removable",
            3 => "Fixed",
            4 => "Network",
            5 => "CD-ROM",
            6 => "RAM Disk",
            _ => "Unknown",
        }
        .to_string();

        let fallback_name = match dt {
            2 => "Removable Drive",
            3 => "Local Disk",
            4 => "Network Drive",
            5 => "Optical Drive",
            6 => "RAM Disk",
            _ => "Drive",
        };
        let remote_path = if dt == 4 {
            mapped_network_remote_path(&drive_path)
        } else {
            None
        };

        pending.push(PendingDrive {
            letter,
            drive_path,
            wide_path,
            dt,
            drive_type,
            fallback_name,
            remote_path,
        });
    }

    // Probe all network drives in parallel.
    let probe_results: Vec<Option<(String, Option<String>)>> = std::thread::scope(|scope| {
        let handles: Vec<_> = pending
            .iter()
            .map(|drive| {
                if drive.dt == 4 {
                    let wide = drive.wide_path.clone();
                    let remote = drive.remote_path.as_deref().map(|s| s.to_string());
                    Some(scope.spawn(move || network_drive_status(&wide, remote.as_deref())))
                } else {
                    None
                }
            })
            .collect();

        handles
            .into_iter()
            .map(|handle| handle.map(|h| h.join().unwrap()))
            .collect()
    });

    let mut drives = Vec::new();
    for (drive, probe) in pending.iter().zip(probe_results) {
        let (drive_status, status_detail) =
            probe.unwrap_or_else(|| ("available".to_string(), None));

        let display_name = windows_drive_display_name(
            drive.dt,
            &drive.wide_path,
            drive.remote_path.as_deref(),
            drive.fallback_name,
        );

        let (total_space, free_space) =
            if drive.dt == 3 || (drive.dt == 4 && drive_status == "available") {
                unsafe {
                    let mut free_bytes_available: u64 = 0;
                    let mut total_bytes: u64 = 0;
                    let mut total_free_bytes: u64 = 0;

                    if winapi::um::fileapi::GetDiskFreeSpaceExW(
                        drive.wide_path.as_ptr(),
                        &mut free_bytes_available as *mut u64 as *mut _,
                        &mut total_bytes as *mut u64 as *mut _,
                        &mut total_free_bytes as *mut u64 as *mut _,
                    ) != 0
                    {
                        (total_bytes, free_bytes_available)
                    } else {
                        (0, 0)
                    }
                }
            } else {
                (0, 0)
            };

        drives.push(DriveInfo {
            name: format!("{} ({}:)", display_name, drive.letter as char),
            path: drive.drive_path.clone(),
            drive_type: drive.drive_type.clone(),
            total_space,
            free_space,
            remote_path: drive.remote_path.clone(),
            drive_status,
            status_detail,
        });
    }

    Ok(drives)
}

#[cfg(test)]
mod tests {
    use super::{network_remote_display_name, windows_error_detail};

    #[test]
    fn network_remote_display_name_formats_unc_share() {
        assert_eq!(
            network_remote_display_name(r"\\nas\media"),
            Some("media on nas".to_string())
        );
    }

    #[test]
    fn network_remote_display_name_preserves_unusual_paths() {
        assert_eq!(
            network_remote_display_name("Network Root"),
            Some("Network Root".to_string())
        );
    }

    #[test]
    fn windows_error_detail_covers_common_network_failures() {
        use winapi::shared::winerror::{
            ERROR_ACCESS_DENIED, ERROR_BAD_NETPATH, ERROR_BAD_NET_NAME, ERROR_NOT_READY,
        };

        assert!(windows_error_detail(ERROR_BAD_NETPATH).contains("Network path"));
        assert!(windows_error_detail(ERROR_BAD_NET_NAME).contains("share"));
        assert!(windows_error_detail(ERROR_ACCESS_DENIED).contains("Access was denied"));
        assert!(windows_error_detail(ERROR_NOT_READY).contains("not ready"));
        assert!(windows_error_detail(0).contains("unavailable"));
        assert!(windows_error_detail(12345).contains("12345"));
    }
}
