use super::path::{
    extension_for_name, join_remote_path, normalize_remote_path, parent_remote_path,
    sibling_remote_path, system_time_to_rfc3339,
};
use super::provider::RemoteProvider;
use super::secrets::RemoteSecret;
use simplefile_core::models::{DirectoryListing, FileEntry};
use simplefile_core::remote::{RemoteAuthKind, RemoteProfile, RemoteProtocol};
use std::convert::TryFrom;
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
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
    // Configure the control socket before reading the greeting or negotiating TLS.
    // Data completion and cleanup commands must also stop on an unresponsive server.
    let socket = TcpStream::connect_timeout(&address, REMOTE_CONNECT_TIMEOUT)
        .map_err(|error| format!("FTP connection failed: {error}"))?;
    socket
        .set_read_timeout(Some(REMOTE_CONNECT_TIMEOUT))
        .map_err(|e| e.to_string())?;
    socket
        .set_write_timeout(Some(REMOTE_CONNECT_TIMEOUT))
        .map_err(|e| e.to_string())?;
    let client = match profile.protocol {
        RemoteProtocol::Ftp => {
            let mut stream = FtpStream::connect_with_stream(socket)
                .map_err(|error| format!("FTP connection failed: {error}"))?;
            stream = stream.passive_stream_builder(connect_data_socket);
            if !profile.passive_mode {
                stream = stream.active_mode(REMOTE_CONNECT_TIMEOUT);
            }
            stream
                .login(&username, &password)
                .map_err(|error| format!("FTP login failed: {error}"))?;
            FtpClient::Plain(stream)
        }
        RemoteProtocol::Ftps => {
            let mut stream = NativeTlsFtpStream::connect_with_stream(socket)
                .map_err(|error| format!("FTPS connection failed: {error}"))?;
            stream = stream.passive_stream_builder(connect_data_socket);
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

fn connect_data_socket(address: SocketAddr) -> suppaftp::FtpResult<TcpStream> {
    let socket = TcpStream::connect_timeout(&address, REMOTE_CONNECT_TIMEOUT)
        .map_err(suppaftp::FtpError::ConnectionError)?;
    socket
        .set_read_timeout(Some(REMOTE_CONNECT_TIMEOUT))
        .map_err(suppaftp::FtpError::ConnectionError)?;
    socket
        .set_write_timeout(Some(REMOTE_CONNECT_TIMEOUT))
        .map_err(suppaftp::FtpError::ConnectionError)?;
    Ok(socket)
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

    fn download(&mut self, path: &str, destination: &mut dyn Write) -> Result<u64, String> {
        self.client
            .download(&normalize_remote_path(path), destination)
    }

    fn upload(&mut self, path: &str, source: &mut dyn Read) -> Result<u64, String> {
        self.client.upload(&normalize_remote_path(path), source)
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

    fn download(&mut self, path: &str, destination: &mut dyn Write) -> Result<u64, String> {
        macro_rules! transfer {
            ($client:expr) => {{
                let mut stream = $client.retr_as_stream(path).map_err(|e| e.to_string())?;
                stream
                    .get_ref()
                    .get_ref()
                    .set_read_timeout(Some(REMOTE_CONNECT_TIMEOUT))
                    .map_err(|e| e.to_string())?;
                let result = super::transfer::copy_stream(&mut stream, destination);
                let finished = stream.finish().map_err(|e| e.to_string());
                result.and_then(|count| finished.map(|()| count))
            }};
        }
        match self {
            Self::Plain(client) => transfer!(client),
            Self::Tls(client) => transfer!(client),
        }
    }

    fn upload(&mut self, path: &str, source: &mut dyn Read) -> Result<u64, String> {
        macro_rules! transfer {
            ($client:expr) => {{
                let mut stream = $client.put_with_stream(path).map_err(|e| e.to_string())?;
                stream
                    .get_ref()
                    .get_ref()
                    .set_write_timeout(Some(REMOTE_CONNECT_TIMEOUT))
                    .map_err(|e| e.to_string())?;
                let result = super::transfer::copy_stream(source, &mut stream);
                let finished = stream.finish().map_err(|e| e.to_string());
                result.and_then(|count| finished.map(|()| count))
            }};
        }
        match self {
            Self::Plain(client) => transfer!(client),
            Self::Tls(client) => transfer!(client),
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
    fn streams_binary_data_and_checks_server_completion_over_ftp() {
        use std::io::{BufRead, BufReader, Cursor};
        use std::net::TcpListener;

        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = std::thread::spawn(move || {
            let (mut control, _) = listener.accept().unwrap();
            control
                .set_read_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            control
                .set_write_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            let mut reader = BufReader::new(control.try_clone().unwrap());
            control.write_all(b"220 Local transfer test\r\n").unwrap();
            let mut passive = None;
            let mut uploaded = Vec::new();
            loop {
                let mut line = String::new();
                if reader.read_line(&mut line).unwrap() == 0 {
                    break;
                }
                let (command, argument) = line
                    .trim_end()
                    .split_once(' ')
                    .unwrap_or((line.trim_end(), ""));
                match command {
                    "USER" => control.write_all(b"230 Logged in\r\n").unwrap(),
                    "TYPE" => control.write_all(b"200 Binary mode\r\n").unwrap(),
                    "PASV" => {
                        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
                        let port = listener.local_addr().unwrap().port();
                        write!(
                            control,
                            "227 Entering Passive Mode (127,0,0,1,{},{})\r\n",
                            port / 256,
                            port % 256
                        )
                        .unwrap();
                        passive = Some(listener);
                    }
                    "STOR" | "RETR" => {
                        control
                            .write_all(b"150 Opening data connection\r\n")
                            .unwrap();
                        let (mut data, _) = passive.take().unwrap().accept().unwrap();
                        data.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
                        data.set_write_timeout(Some(Duration::from_secs(5)))
                            .unwrap();
                        if command == "STOR" {
                            uploaded.clear();
                            data.read_to_end(&mut uploaded).unwrap();
                        } else {
                            data.write_all(&uploaded).unwrap();
                        }
                        drop(data);
                        control
                            .write_all(if argument == "/rejected" {
                                b"426 Transfer failed\r\n"
                            } else {
                                b"226 Transfer complete\r\n"
                            })
                            .unwrap();
                    }
                    "QUIT" => {
                        control.write_all(b"221 Goodbye\r\n").unwrap();
                        break;
                    }
                    _ => panic!("Unexpected FTP command: {line}"),
                }
            }
        });
        let socket = connect_data_socket(address).unwrap();
        let mut stream = FtpStream::connect_with_stream(socket)
            .unwrap()
            .passive_stream_builder(connect_data_socket);
        stream.login("test", "test").unwrap();
        let mut client = FtpClient::Plain(stream);
        let bytes: Vec<u8> = (0..super::super::transfer::TRANSFER_BUFFER_SIZE * 19 + 7)
            .map(|index| (index % 256) as u8)
            .collect();
        assert_eq!(
            client.upload("/data", &mut Cursor::new(&bytes)).unwrap(),
            bytes.len() as u64
        );
        let mut downloaded = Vec::new();
        assert_eq!(
            client.download("/data", &mut downloaded).unwrap(),
            bytes.len() as u64
        );
        assert_eq!(downloaded, bytes);
        // A successful data copy is insufficient when the server rejects completion.
        assert!(client.download("/rejected", &mut std::io::sink()).is_err());
        assert!(client
            .upload("/rejected", &mut Cursor::new(&bytes))
            .is_err());
        client.quit().unwrap();
        server.join().unwrap();
    }

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
