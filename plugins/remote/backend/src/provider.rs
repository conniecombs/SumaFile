use super::secrets::RemoteSecret;
use crate::models::{DirectoryListing, FileEntry};
use crate::models::{RemoteProfile, RemoteProtocol};
use std::io::{Read, Write};
pub use sumafile_transfer::reliability::Failure as RemoteTransferError;

pub trait RemoteProvider: Send {
    fn transfer_buffer_bytes(&self) -> usize {
        super::transfer::TRANSFER_BUFFER_SIZE
    }

    /// True when `list_directory` entries carry the same modification-time
    /// precision and timezone as `stat`, so downloads can trust `source_entry`
    /// without issuing an extra baseline `stat` round-trip.
    fn list_matches_stat_precision(&self) -> bool {
        true
    }

    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String>;

    /// Inspect one entry without following links. Providers with a stat command
    /// override this fallback to avoid repeatedly listing large parent folders.
    fn stat(&mut self, path: &str) -> Result<Option<FileEntry>, String> {
        let path = super::path::normalize_remote_path(path);
        let parent = super::path::parent_remote_path(&path)
            .ok_or("A server root cannot be transferred as a file")?;
        let name = super::path::remote_file_name(&path);
        Ok(self
            .list_directory(&parent)?
            .entries
            .into_iter()
            .find(|entry| entry.name == name))
    }

    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String>;

    fn rename_entry(&mut self, path: &str, new_name: &str) -> Result<FileEntry, String>;

    /// Rename without requiring a post-rename `stat` round-trip when the caller
    /// does not need the resulting `FileEntry` (e.g. staging promotion).
    fn rename_path(&mut self, path: &str, new_name: &str) -> Result<(), String> {
        self.rename_entry(path, new_name).map(|_| ())
    }

    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String>;

    fn download(
        &mut self,
        path: &str,
        destination: &mut dyn Write,
    ) -> Result<u64, crate::provider::RemoteTransferError>;

    fn upload(
        &mut self,
        path: &str,
        source: &mut dyn Read,
    ) -> Result<u64, crate::provider::RemoteTransferError>;

    fn reconnect(&mut self) -> Result<(), crate::provider::RemoteTransferError> {
        Ok(())
    }

    fn disconnect(&mut self) -> Result<(), String> {
        Ok(())
    }
}

pub trait RemoteProviderFactory: Send + Sync {
    fn connect(
        &self,
        profile: &RemoteProfile,
        secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String>;
}

pub struct DefaultRemoteProviderFactory;

impl RemoteProviderFactory for DefaultRemoteProviderFactory {
    fn connect(
        &self,
        profile: &RemoteProfile,
        secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        let inner = connect_raw_typed(profile, secret).map_err(String::from)?;
        Ok(Box::new(ReconnectingProvider {
            inner,
            profile: profile.clone(),
            secret: secret.cloned(),
        }))
    }
}

// Credentials stay in memory with the session. Reconnect always repeats the original
// TLS certificate / SSH host-key verification; it never downgrades the protocol.
struct ReconnectingProvider {
    inner: Box<dyn RemoteProvider>,
    profile: RemoteProfile,
    secret: Option<RemoteSecret>,
}
fn connect_raw_typed(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<Box<dyn RemoteProvider>, RemoteTransferError> {
    match profile.protocol {
        RemoteProtocol::Sftp => crate::sftp::connect_typed(profile, secret),
        RemoteProtocol::Ftp | RemoteProtocol::Ftps => crate::ftp::connect_typed(profile, secret),
    }
}
impl RemoteProvider for ReconnectingProvider {
    fn transfer_buffer_bytes(&self) -> usize {
        self.inner.transfer_buffer_bytes()
    }
    fn list_matches_stat_precision(&self) -> bool {
        self.inner.list_matches_stat_precision()
    }
    fn stat(&mut self, path: &str) -> Result<Option<FileEntry>, String> {
        self.inner.stat(path)
    }
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
        self.inner.list_directory(path)
    }
    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
        self.inner.create_directory(path, name)
    }
    fn rename_entry(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
        self.inner.rename_entry(path, name)
    }
    fn rename_path(&mut self, path: &str, name: &str) -> Result<(), String> {
        self.inner.rename_path(path, name)
    }
    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
        self.inner.delete_entries(paths)
    }
    fn download(
        &mut self,
        path: &str,
        destination: &mut dyn Write,
    ) -> Result<u64, RemoteTransferError> {
        self.inner.download(path, destination)
    }
    fn upload(&mut self, path: &str, source: &mut dyn Read) -> Result<u64, RemoteTransferError> {
        self.inner.upload(path, source)
    }
    fn reconnect(&mut self) -> Result<(), RemoteTransferError> {
        self.inner = connect_raw_typed(&self.profile, self.secret.as_ref())?;
        Ok(())
    }
    fn disconnect(&mut self) -> Result<(), String> {
        self.inner.disconnect()
    }
}
