//! Named shared memory support for transferring large payloads (e.g. large images or thumbnails
//! when cache is disabled) with zero pipe copy overhead.

#[cfg(windows)]
pub mod windows {
    use std::ptr;
    use std::sync::atomic::{AtomicU64, Ordering};
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Memory::{
        CreateFileMappingW, MapViewOfFile, UnmapViewOfFile, FILE_MAP_WRITE, PAGE_READWRITE,
    };

    static COUNTER: AtomicU64 = AtomicU64::new(1);

    /// Represents an active named shared memory section holding data for the client.
    pub struct SharedMemorySection {
        pub name: String,
        handle: HANDLE,
        pub size: usize,
    }

    unsafe impl Send for SharedMemorySection {}
    unsafe impl Sync for SharedMemorySection {}

    impl SharedMemorySection {
        /// Creates a named shared memory section containing the provided bytes.
        pub fn create(data: &[u8]) -> Result<Self, String> {
            if data.is_empty() {
                return Err("Cannot create empty shared memory section".to_string());
            }

            let id = COUNTER.fetch_add(1, Ordering::Relaxed);
            let pid = std::process::id();
            let name = format!("Local\\SumaFile_SHM_{pid}_{id}");
            let wide_name: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();

            let size = data.len();
            let high_size = (size >> 32) as u32;
            let low_size = (size & 0xFFFFFFFF) as u32;

            unsafe {
                let handle = CreateFileMappingW(
                    INVALID_HANDLE_VALUE,
                    ptr::null(),
                    PAGE_READWRITE,
                    high_size,
                    low_size,
                    wide_name.as_ptr(),
                );

                if handle == 0 as HANDLE || handle == INVALID_HANDLE_VALUE {
                    return Err(format!(
                        "Failed to create file mapping {name}: {}",
                        std::io::Error::last_os_error()
                    ));
                }

                let view = MapViewOfFile(handle, FILE_MAP_WRITE, 0, 0, size);
                if view.Value.is_null() {
                    CloseHandle(handle);
                    return Err(format!(
                        "Failed to map view of file {name}: {}",
                        std::io::Error::last_os_error()
                    ));
                }

                ptr::copy_nonoverlapping(data.as_ptr(), view.Value as *mut u8, size);
                UnmapViewOfFile(view);

                Ok(Self { name, handle, size })
            }
        }
    }

    impl Drop for SharedMemorySection {
        fn drop(&mut self) {
            unsafe {
                if self.handle != 0 as HANDLE && self.handle != INVALID_HANDLE_VALUE {
                    CloseHandle(self.handle);
                }
            }
        }
    }
}

#[cfg(not(windows))]
pub mod windows {
    pub struct SharedMemorySection {
        pub name: String,
        pub size: usize,
    }

    impl SharedMemorySection {
        pub fn create(_data: &[u8]) -> Result<Self, String> {
            Err("Shared memory is only supported on Windows".to_string())
        }
    }
}

pub use windows::SharedMemorySection;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shared_memory_creation_test() {
        let test_data = b"Hello from SumaFile shared memory test!";
        let section = SharedMemorySection::create(test_data).expect("create shm");
        assert!(section.name.starts_with("Local\\SumaFile_SHM_"));
        assert_eq!(section.size, test_data.len());
    }
}
