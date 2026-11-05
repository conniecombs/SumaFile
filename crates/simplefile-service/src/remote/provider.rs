use super::secrets::RemoteSecret;
pub use crate::transfer_reliability::Failure as RemoteTransferError;
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteProfile, RemoteProtocol};
use std::io::{Read, Write};

pub trait RemoteProvider: Send {
    fn transfer_buffer_bytes(&self) -> usize { super::transfer::TRANSFER_BUFFER_SIZE }
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String>;

    /// Inspect one entry without following links. Providers with a stat command
    /// override this fallback to avoid repeatedly listing large parent folders.
    fn stat(&mut self, path: &str) -> Result<Option<FileEntry>, String> {
        let path = super::path::normalize_remote_path(path);
        let parent = super::path::parent_remote_path(&path)
            .ok_or("A server root cannot be transferred as a file")?;
        let name = super::path::remote_file_name(&path);
        Ok(self.list_directory(&parent)?.entries.into_iter().find(|entry| entry.name == name))
    }

    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String>;

    fn rename_entry(&mut self, path: &str, new_name: &str) -> Result<FileEntry, String>;

    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String>;

    fn download(
        &mut self,
        path: &str,
        destination: &mut dyn Write,
    ) -> Result<u64, crate::remote::provider::RemoteTransferError>;

    fn upload(
        &mut self,
        path: &str,
        source: &mut dyn Read,
    ) -> Result<u64, crate::remote::provider::RemoteTransferError>;

    fn reconnect(&mut self) -> Result<(), crate::remote::provider::RemoteTransferError> {
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

pub struct UnsupportedRemoteProviderFactory;

impl RemoteProviderFactory for UnsupportedRemoteProviderFactory {
    fn connect(
        &self,
        profile: &RemoteProfile,
        _secret: Option<&RemoteSecret>,
    ) -> Result<Box<dyn RemoteProvider>, String> {
        Err(format!(
            "{} provider sessions are not enabled yet",
            profile.protocol.as_str()
        ))
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
        RemoteProtocol::Sftp => crate::remote::sftp::connect_typed(profile, secret),
        RemoteProtocol::Ftp | RemoteProtocol::Ftps => {
            crate::remote::ftp::connect_typed(profile, secret)
        }
    }
}
impl RemoteProvider for ReconnectingProvider {
    fn transfer_buffer_bytes(&self) -> usize { self.inner.transfer_buffer_bytes() }
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
