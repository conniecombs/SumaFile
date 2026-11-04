use super::secrets::RemoteSecret;
pub use crate::transfer_reliability::Failure as RemoteTransferError;
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteProfile, RemoteProtocol};
use std::io::{Read, Write};

pub trait RemoteProvider: Send {
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String>;

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

    fn reconnect(&mut self) -> Result<(), String> {
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
        let inner = connect_raw(profile, secret)?;
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
fn connect_raw(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<Box<dyn RemoteProvider>, String> {
    match profile.protocol {
        RemoteProtocol::Sftp => crate::remote::sftp::connect(profile, secret),
        RemoteProtocol::Ftp | RemoteProtocol::Ftps => crate::remote::ftp::connect(profile, secret),
    }
}
impl RemoteProvider for ReconnectingProvider {
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
    fn reconnect(&mut self) -> Result<(), String> {
        self.inner = connect_raw(&self.profile, self.secret.as_ref())?;
        Ok(())
    }
    fn disconnect(&mut self) -> Result<(), String> {
        self.inner.disconnect()
    }
}
