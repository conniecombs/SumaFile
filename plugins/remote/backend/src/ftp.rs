use super::path::{
    extension_for_name, join_remote_path, normalize_remote_path, parent_remote_path,
    sibling_remote_path, system_time_to_rfc3339,
};
use super::provider::RemoteProvider;
use super::secrets::RemoteSecret;
use crate::models::{DirectoryListing, FileEntry};
use crate::models::{RemoteAuthKind, RemoteProfile, RemoteProtocol};
use std::convert::TryFrom;
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::time::Duration;
use sumafile_transfer::reliability::Failure;
use suppaftp::list::{File as FtpListFile, PosixPexQuery};
use suppaftp::{FtpStream, NativeTlsConnector, NativeTlsFtpStream};

const REMOTE_CONNECT_TIMEOUT: Duration = Duration::from_secs(15);

pub fn connect_typed(
    profile: &RemoteProfile,
    secret: Option<&RemoteSecret>,
) -> Result<Box<dyn RemoteProvider>, Failure> {
    if profile.protocol == RemoteProtocol::Ftp && !profile.insecure_plain_ftp {
        return Err(Failure::new(
            "Confirm the unencrypted connection to use plain FTP",
            false,
        ));
    }

    let (username, password) =
        ftp_credentials(profile, secret).map_err(|msg| Failure::new(msg, false))?;
    let address = first_socket_addr_typed(profile)?;
    // Configure the control socket before reading the greeting or negotiating TLS.
    // Data completion and cleanup commands must also stop on an unresponsive server.
    let socket = TcpStream::connect_timeout(&address, REMOTE_CONNECT_TIMEOUT).map_err(|error| {
        let message = format!("FTP connection failed: {error}");
        let retryable = Failure::io("FTP connection", &profile.host, error).retryable();
        Failure::new(message, retryable)
    })?;
    socket
        .set_read_timeout(Some(REMOTE_CONNECT_TIMEOUT))
        .map_err(|e| Failure::new(e.to_string(), false))?;
    socket
        .set_write_timeout(Some(REMOTE_CONNECT_TIMEOUT))
        .map_err(|e| Failure::new(e.to_string(), false))?;
    let client = match profile.protocol {
        RemoteProtocol::Ftp => {
            let mut stream = FtpStream::connect_with_stream(socket).map_err(|error| {
                let message = format!("FTP connection failed: {error}");
                let retryable = ftp_failure(error).retryable();
                Failure::new(message, retryable)
            })?;
            stream = stream.passive_stream_builder(connect_data_socket);
            if !profile.passive_mode {
                stream = stream.active_mode(REMOTE_CONNECT_TIMEOUT);
            }
            stream.login(&username, &password).map_err(|error| {
                let message = format!("FTP login failed: {error}");
                let retryable = ftp_failure(error).retryable();
                Failure::new(message, retryable)
            })?;
            FtpClient::Plain(stream)
        }
        RemoteProtocol::Ftps => {
            let mut stream = NativeTlsFtpStream::connect_with_stream(socket).map_err(|error| {
                let message = format!("FTPS connection failed: {error}");
                let retryable = ftp_failure(error).retryable();
                Failure::new(message, retryable)
            })?;
            stream = stream.passive_stream_builder(connect_data_socket);
            if !profile.passive_mode {
                stream = stream.active_mode(REMOTE_CONNECT_TIMEOUT);
            }
            let connector = suppaftp::native_tls::TlsConnector::new()
                .map(NativeTlsConnector::from)
                .map_err(|error| Failure::new(format!("FTPS TLS setup failed: {error}"), false))?;
            let mut stream = stream
                .into_secure(connector, &profile.host)
                .map_err(|error| {
                    Failure::new(format!("FTPS TLS negotiation failed: {error}"), false)
                })?;
            stream.login(&username, &password).map_err(|error| {
                let message = format!("FTPS login failed: {error}");
                let retryable = ftp_failure(error).retryable();
                Failure::new(message, retryable)
            })?;
            FtpClient::Tls(stream)
        }
        RemoteProtocol::Sftp => {
            return Err(Failure::new(
                "FTP adapter cannot connect SFTP profiles",
                false,
            ));
        }
    };

    Ok(Box::new(FtpRemoteProvider {
        client,
        mlst_supported: true,
    }))
}

struct FtpRemoteProvider {
    client: FtpClient,
    mlst_supported: bool,
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
    fn stat(&mut self, path: &str) -> Result<Option<FileEntry>, String> {
        let path = normalize_remote_path(path);
        if self.mlst_supported {
            match self.client.mlst(Some(&path)) {
                Ok(line) => {
                    let mut entry = parse_ftp_list_line(
                        &parent_remote_path(&path).unwrap_or_else(|| "/".into()),
                        line.trim(),
                    )?;
                    entry.path = path.clone();
                    entry.name = super::path::remote_file_name(&path);
                    return Ok(Some(entry));
                }
                Err(suppaftp::FtpError::UnexpectedResponse(response)) => {
                    match response.status as u32 {
                        500 | 501 | 502 | 504 => self.mlst_supported = false,
                        // 550 can mean missing OR permission denied. A successful
                        // parent listing is required before declaring it absent.
                        550 => {}
                        _ => return Err(format!("FTP stat failed for {path}: {response:?}")),
                    }
                }
                Err(error) => return Err(format!("FTP stat failed for {path}: {error}")),
            }
        }
        let parent = parent_remote_path(&path).ok_or("Remote path has no parent")?;
        Ok(self
            .list_directory(&parent)?
            .entries
            .into_iter()
            .find(|entry| entry.path == path))
    }

    fn list_directory(&mut self, path: &str) -> Result<DirectoryListing, String> {
        let path = normalize_remote_path(path);
        let lines = self
            .client
            .list(Some(&path))
            .map_err(|error| format!("FTP list failed for {path}: {error}"))?;
        let entries = entries_from_list_lines(&path, &lines);

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

    fn download(
        &mut self,
        path: &str,
        destination: &mut dyn Write,
    ) -> Result<u64, crate::provider::RemoteTransferError> {
        self.client
            .download(&normalize_remote_path(path), destination)
    }

    fn upload(
        &mut self,
        path: &str,
        source: &mut dyn Read,
    ) -> Result<u64, crate::provider::RemoteTransferError> {
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
        self.stat(path)?
            .ok_or_else(|| format!("FTP entry not found after operation: {path}"))
    }
}

enum FtpClient {
    Plain(FtpStream),
    Tls(NativeTlsFtpStream),
}

impl FtpClient {
    fn mlst(&mut self, path: Option<&str>) -> suppaftp::FtpResult<String> {
        match self {
            Self::Plain(stream) => stream.mlst(path),
            Self::Tls(stream) => stream.mlst(path),
        }
    }

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

    fn download(
        &mut self,
        path: &str,
        destination: &mut dyn Write,
    ) -> Result<u64, crate::provider::RemoteTransferError> {
        macro_rules! transfer {
            ($client:expr) => {{
                let mut stream = $client.retr_as_stream(path).map_err(ftp_failure)?;
                stream
                    .get_ref()
                    .get_ref()
                    .set_read_timeout(Some(REMOTE_CONNECT_TIMEOUT))
                    .map_err(|e| Failure::io("Configure remote data stream", path, e))?;
                let result = super::transfer::copy_stream(&mut stream, destination);
                let finished = stream.finish().map_err(ftp_failure);
                result.and_then(|count| finished.map(|()| count))
            }};
        }
        match self {
            Self::Plain(client) => transfer!(client),
            Self::Tls(client) => transfer!(client),
        }
    }

    fn upload(
        &mut self,
        path: &str,
        source: &mut dyn Read,
    ) -> Result<u64, crate::provider::RemoteTransferError> {
        macro_rules! transfer {
            ($client:expr) => {{
                let mut stream = $client.put_with_stream(path).map_err(ftp_failure)?;
                stream
                    .get_ref()
                    .get_ref()
                    .set_write_timeout(Some(REMOTE_CONNECT_TIMEOUT))
                    .map_err(|e| Failure::io("Configure remote data stream", path, e))?;
                let result = super::transfer::copy_stream(source, &mut stream);
                let finished = stream.finish().map_err(ftp_failure);
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

/// Parse a LIST response. A line the parser does not understand (a `total N`
/// banner, a vendor-specific format) is logged and skipped rather than making
/// the whole directory unlistable.
pub(crate) fn entries_from_list_lines(parent_path: &str, lines: &[String]) -> Vec<FileEntry> {
    let mut entries = Vec::with_capacity(lines.len());
    for line in lines {
        match parse_ftp_list_line(parent_path, line) {
            Ok(entry) if entry.name == "." || entry.name == ".." => {}
            Ok(entry) => entries.push(entry),
            Err(error) => log::warn!("Skipping FTP list line in {parent_path}: {error}"),
        }
    }
    entries
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

fn first_socket_addr_typed(profile: &RemoteProfile) -> Result<SocketAddr, Failure> {
    (profile.host.as_str(), profile.port)
        .to_socket_addrs()
        .map_err(|error| {
            let message = format!(
                "Could not resolve {}:{}: {error}",
                profile.host, profile.port
            );
            let retryable = Failure::io("Resolve host", &profile.host, error).retryable();
            Failure::new(message, retryable)
        })?
        .next()
        .ok_or_else(|| {
            Failure::new(
                format!("Could not resolve {}:{}", profile.host, profile.port),
                false,
            )
        })
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

fn ftp_failure(error: suppaftp::FtpError) -> Failure {
    match error {
        suppaftp::FtpError::ConnectionError(error) => {
            Failure::io("FTP connection", "data stream", error)
        }
        suppaftp::FtpError::UnexpectedResponse(response) => {
            let code = response.status as u32;
            Failure::new(
                format!("FTP response: {response}"),
                (400..500).contains(&code) && code != 430,
            )
        }
        error => Failure::new(error.to_string(), false),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ftp_and_ftps_retry_temporary_responses_but_not_auth_or_permission_failures() {
        for (code, retryable) in [
            (426, true),
            (451, true),
            (452, true),
            (430, false),
            (530, false),
            (550, false),
        ] {
            let error = ftp_failure(suppaftp::FtpError::UnexpectedResponse(
                suppaftp::types::Response {
                    status: suppaftp::Status::from(code),
                    body: b"test response".to_vec(),
                },
            ));
            assert_eq!(error.retryable(), retryable, "{code}");
        }
    }

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

#[cfg(test)]
mod listing_tests {
    use super::entries_from_list_lines;

    /// The listing helper drops the `.` and `..` pseudo-entries and keeps the
    /// rest in server order. (suppaftp 12 parses even banner lines into
    /// entries, so an unparseable line cannot be produced here; the helper
    /// still skips any line the parser rejects instead of failing the listing.)
    #[test]
    fn dot_entries_are_filtered_from_listings() {
        let lines = vec![
            "drwxr-xr-x   2 user group     4096 Jan 01 12:00 .".to_string(),
            "drwxr-xr-x   2 user group     4096 Jan 01 12:00 ..".to_string(),
            "-rw-r--r--   1 user group        5 Jan 01 12:00 notes.txt".to_string(),
            "drwxr-xr-x   2 user group     4096 Jan 01 12:00 sub".to_string(),
        ];
        let entries = entries_from_list_lines("/var", &lines);
        let names: Vec<&str> = entries.iter().map(|entry| entry.name.as_str()).collect();
        assert_eq!(names, vec!["notes.txt", "sub"]);
        assert_eq!(entries[0].path, "/var/notes.txt");
    }
}
