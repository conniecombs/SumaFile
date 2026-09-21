use super::secrets::RemoteSecret;
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteProfile, RemoteProtocol};

pub trait RemoteProvider: Send {
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String>;

    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String>;

    fn rename_entry(&mut self, path: &str, new_name: &str) -> Result<FileEntry, String>;

    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String>;

    fn read_file(&mut self, path: &str) -> Result<Vec<u8>, String>;

    fn write_file(&mut self, path: &str, bytes: &[u8]) -> Result<FileEntry, String>;

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
        match profile.protocol {
            RemoteProtocol::Sftp => crate::remote::sftp::connect(profile, secret),
            RemoteProtocol::Ftp | RemoteProtocol::Ftps => {
                crate::remote::ftp::connect(profile, secret)
            }
        }
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
