use super::path::{
    extension_for_name, join_remote_path, normalize_remote_path, parent_remote_path,
    sibling_remote_path, system_time_to_rfc3339,
};
use super::provider::RemoteProvider;
use super::secrets::RemoteSecret;
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteAuthKind, RemoteProfile, RemoteProtocol};
use std::convert::TryFrom;
use std::io::Cursor;
use std::net::{SocketAddr, ToSocketAddrs};
use std::time::Duration;
use suppaftp::list::{File as FtpListFile, PosixPexQuery};
use suppaftp::{FtpStream, NativeTlsConnector, NativeTlsFtpStream};

const REMOTE_CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

pub fn connect(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<Box<dyn RemoteProvider>, String> {
    if profile.protocol == RemoteProtocol::Ftp && !profile.insecure_plain_ftp {
        return Err("Plain FTP requires insecure_plain_ftp acknowledgement".to_string());
    }

    let (username, password) = ftp_credentials(profile, secret)?;
    let address = first_socket_addr(profile)?;
    let client = match profile.protocol {
        RemoteProtocol::Ftp => {
            let mut stream = FtpStream::connect_timeout(address, REMOTE_CONNECT_TIMEOUT)
                .map_err(|error| format!("FTP connection failed: {error}"))?;
            if !profile.passive_mode {
                stream = stream.active_mode(REMOTE_CONNECT_TIMEOUT);
            }
            stream
                .login(&username, &password)
                .map_err(|error| format!("FTP login failed: {error}"))?;
            FtpClient::Plain(stream)
        }
        RemoteProtocol::Ftps => {
            let mut stream = NativeTlsFtpStream::connect_timeout(address, REMOTE_CONNECT_TIMEOUT)
                .map_err(|error| format!("FTPS connection failed: {error}"))?;
            if !profile.passive_mode {
                stream = stream.active_mode(REMOTE_CONNECT_TIMEOUT);
            }
            let connector = suppaftp::native_tls::TlsConnector::new()
                .map(NativeTlsConnector::from)
                .map_err(|error| format!("FTPS TLS setup failed: {error}"))?;
            let mut stream = stream
                .into_secure(connector, &profile.host)
                .map_err(|error| format!("FTPS TLS negotiation failed: {error}"))?;
            stream
                .login(&username, &password)
                .map_err(|error| format!("FTPS login failed: {error}"))?;
            FtpClient::Tls(stream)
        }
        RemoteProtocol::Sftp => {
            return Err("FTP adapter cannot connect SFTP profiles".to_string());
        }
    };

    Ok(Box::new(FtpRemoteProvider { client }))
}

struct FtpRemoteProvider {
    client: FtpClient,
}

impl RemoteProvider for FtpRemoteProvider {
    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
        let path = normalize_remote_path(path);
        let lines = self
            .client
            .list(Some(&path))
            .map_err(|error| format!("FTP list failed for {path}: {error}"))?;
        let mut entries = Vec::new();

        for line in lines {
            let entry = parse_ftp_list_line(&path, &line)?;
            if entry.name != "." && entry.name != ".." {
                entries.push(entry);
            }
        }

        Ok(DirectoryListing {
            parent: parent_remote_path(&path),
            path,
            entries,
            is_network: true,
        })
    }

    fn create_directory(&mut self, path: &str, name: &str) -> Result<FileEntry, String> {
        let target = join_remote_path(path, name);
        self.client
            .mkdir(&target)
            .map_err(|error| format!("FTP create directory failed for {target}: {error}"))?;
        Ok(basic_remote_entry(&target, true, 0))
    }

    fn rename_entry(&mut self, path: &str, new_name: &str) -> Result<FileEntry, String> {
        let source = normalize_remote_path(path);
        let target = sibling_remote_path(&source, new_name);
        self.client
            .rename(&source, &target)
            .map_err(|error| format!("FTP rename failed for {source}: {error}"))?;
        match self.find_entry(&target) {
            Ok(entry) => Ok(entry),
            Err(_) => Ok(basic_remote_entry(&target, false, 0)),
        }
    }

    fn delete_entries(&mut self, paths: &[String]) -> Result<Vec<String>, String> {
        let mut deleted = Vec::new();
        for path in paths {
            let normalized = normalize_remote_path(path);
            if let Err(file_error) = self.client.rm(&normalized) {
                self.client.rmdir(&normalized).map_err(|directory_error| {
                    format!(
                        "FTP delete failed for {normalized}: file delete failed ({file_error}); directory delete failed ({directory_error})"
                    )
                })?;
            }
            deleted.push(normalized);
        }
        Ok(deleted)
    }

    fn read_file(&mut self, path: &str) -> Result<Vec<u8>, String> {
        let path = normalize_remote_path(path);
        self.client
            .retr_as_buffer(&path)
            .map(|cursor| cursor.into_inner())
            .map_err(|error| format!("FTP download failed for {path}: {error}"))
    }

    fn write_file(&mut self, path: &str, bytes: &[u8]) -> Result<FileEntry, String> {
        let path = normalize_remote_path(path);
        let mut cursor = Cursor::new(bytes);
        self.client
            .put_file(&path, &mut cursor)
            .map_err(|error| format!("FTP upload failed for {path}: {error}"))?;
        match self.find_entry(&path) {
            Ok(entry) => Ok(entry),
            Err(_) => Ok(basic_remote_entry(&path, false, bytes.len() as u64)),
        }
    }

    fn disconnect(&mut self) -> Result<(), String> {
        self.client
            .quit()
            .map_err(|error| format!("FTP disconnect failed: {error}"))
    }
}

impl FtpRemoteProvider {
    fn find_entry(&mut self, path: &str) -> Result<FileEntry, String> {
        let parent = parent_remote_path(path).unwrap_or_else(|| "/".to_string());
        self.list_directory(&parent)?
            .entries
            .into_iter()
            .find(|entry| normalize_remote_path(&entry.path) == normalize_remote_path(path))
            .ok_or_else(|| format!("FTP entry not found after operation: {path}"))
    }
}

enum FtpClient {
    Plain(FtpStream),
    Tls(NativeTlsFtpStream),
}

impl FtpClient {
    fn list(&mut self, path: Option<&str>) -> suppaftp::FtpResult<Vec<String>> {
        match self {
            Self::Plain(stream) => stream.list(path),
            Self::Tls(stream) => stream.list(path),
        }
    }

    fn mkdir(&mut self, path: &str) -> suppaftp::FtpResult<()> {
        match self {
            Self::Plain(stream) => stream.mkdir(path),
            Self::Tls(stream) => stream.mkdir(path),
        }
    }

    fn rename(&mut self, source: &str, target: &str) -> suppaftp::FtpResult<()> {
        match self {
            Self::Plain(stream) => stream.rename(source, target),
            Self::Tls(stream) => stream.rename(source, target),
        }
    }

    fn rm(&mut self, path: &str) -> suppaftp::FtpResult<()> {
        match self {
            Self::Plain(stream) => stream.rm(path),
            Self::Tls(stream) => stream.rm(path),
        }
    }

    fn rmdir(&mut self, path: &str) -> suppaftp::FtpResult<()> {
        match self {
            Self::Plain(stream) => stream.rmdir(path),
            Self::Tls(stream) => stream.rmdir(path),
        }
    }

    fn retr_as_buffer(&mut self, path: &str) -> suppaftp::FtpResult<Cursor<Vec<u8>>> {
        match self {
            Self::Plain(stream) => stream.retr_as_buffer(path),
            Self::Tls(stream) => stream.retr_as_buffer(path),
        }
    }

    fn put_file(&mut self, path: &str, cursor: &mut Cursor<&[u8]>) -> suppaftp::FtpResult<u64> {
        match self {
            Self::Plain(stream) => stream.put_file(path, cursor),
            Self::Tls(stream) => stream.put_file(path, cursor),
        }
    }

    fn quit(&mut self) -> suppaftp::FtpResult<()> {
        match self {
            Self::Plain(stream) => stream.quit(),
            Self::Tls(stream) => stream.quit(),
        }
    }
}

pub(crate) fn parse_ftp_list_line(parent_path: &str, line: &str) -> Result<FileEntry, String> {
    let file = FtpListFile::try_from(line)
        .map_err(|error| format!("Could not parse FTP list entry '{line}': {error}"))?;
    let name = file.name().to_string();
    let is_dir = file.is_directory();
    let is_symlink = file.is_symlink();
    let symlink_target = file
        .symlink()
        .map(|target| target.to_string_lossy().to_string());

    Ok(FileEntry {
        path: join_remote_path(parent_path, &name),
        extension: extension_for_name(&name, is_dir),
        is_hidden: name.starts_with('.'),
        is_system: false,
        size: file.size() as u64,
        modified: system_time_to_rfc3339(file.modified()),
        permissions: Some(ftp_permissions(&file)),
        symlink_target,
        git_status: None,
        name,
        is_dir,
        is_symlink,
    })
}

fn ftp_credentials(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<(String, String), String> {
    match profile.auth_kind {
        RemoteAuthKind::Anonymous => {
            let username = if profile.username.trim().is_empty() {
                "anonymous".to_string()
            } else {
                profile.username.trim().to_string()
            };
            Ok((username, "anonymous@".to_string()))
        }
        RemoteAuthKind::Password => match secret {
            Some(RemoteSecret::Password(password)) if !password.is_empty() => {
                Ok((profile.username.clone(), password.clone()))
            }
            _ => Err(format!(
                "{} password authentication requires a saved or supplied password",
                profile.protocol.as_str()
            )),
        },
        RemoteAuthKind::PrivateKey | RemoteAuthKind::Agent => Err(format!(
            "{} supports password or anonymous authentication in this adapter",
            profile.protocol.as_str()
        )),
    }
}

fn first_socket_addr(profile: &RemoteProfile) -> Result<SocketAddr, String> {
    (profile.host.as_str(), profile.port)
        .to_socket_addrs()
        .map_err(|error| {
            format!(
                "Could not resolve {}:{}: {error}",
                profile.host, profile.port
            )
        })?
        .next()
        .ok_or_else(|| format!("Could not resolve {}:{}", profile.host, profile.port))
}

fn basic_remote_entry(path: &str, is_dir: bool, size: u64) -> FileEntry {
    let normalized = normalize_remote_path(path);
    let name = super::path::remote_file_name(&normalized);
    FileEntry {
        extension: extension_for_name(&name, is_dir),
        is_hidden: name.starts_with('.'),
        is_system: false,
        is_symlink: false,
        modified: chrono::Utc::now().to_rfc3339(),
        name,
        path: normalized,
        permissions: None,
        size,
        symlink_target: None,
        git_status: None,
        is_dir,
    }
}

fn ftp_permissions(file: &FtpListFile) -> String {
    let mut permissions = String::with_capacity(9);
    for query in [
        PosixPexQuery::Owner,
        PosixPexQuery::Group,
        PosixPexQuery::Others,
    ] {
        permissions.push(if file.can_read(query) { 'r' } else { '-' });
        permissions.push(if file.can_write(query) { 'w' } else { '-' });
        permissions.push(if file.can_execute(query) { 'x' } else { '-' });
    }
    permissions
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_posix_ftp_list_entries_into_file_entries() {
        let entry = parse_ftp_list_line(
            "/var/www",
            "-rw-r--r-- 1 deploy staff 123 Sep 21 12:35 app.tar.gz",
        )
        .expect("parse file");

        assert_eq!(entry.name, "app.tar.gz");
        assert_eq!(entry.path, "/var/www/app.tar.gz");
        assert!(!entry.is_dir);
        assert_eq!(entry.size, 123);
        assert_eq!(entry.extension, "gz");
        assert!(!entry.modified.is_empty());
    }

    #[test]
    fn parses_mlsd_directories_and_hidden_entries() {
        let entry = parse_ftp_list_line("/var/www", "type=dir;modify=20260921123456; .well-known")
            .expect("parse directory");

        assert_eq!(entry.name, ".well-known");
        assert_eq!(entry.path, "/var/www/.well-known");
        assert!(entry.is_dir);
        assert!(entry.is_hidden);
        assert_eq!(entry.extension, "");
    }
}
