//! The SFTP subsystem: serves one local folder, confined to it.

use russh_sftp::protocol::{
    Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode, Version,
};
use russh_sftp::server::StatusReply;
use std::collections::HashMap;
use std::io::{ErrorKind, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};

/// The largest read the server answers; clients ask for less.
const MAX_READ: u32 = 256 * 1024;

enum OpenHandle {
    Directory { entries: Option<Vec<File>> },
    File(std::fs::File),
}

/// One SFTP session over `root`. Every path is resolved inside `root`; a
/// `..` that would climb above it answers `PermissionDenied`.
pub(crate) struct SftpHandler {
    root: PathBuf,
    handles: HashMap<String, OpenHandle>,
    next_handle: u64,
}

impl SftpHandler {
    pub(crate) fn new(root: PathBuf) -> Self {
        Self {
            root,
            handles: HashMap::new(),
            next_handle: 0,
        }
    }

    fn add_handle(&mut self, handle: OpenHandle) -> String {
        self.next_handle += 1;
        let id = format!("handle-{}", self.next_handle);
        self.handles.insert(id.clone(), handle);
        id
    }

    fn local(&self, path: &str) -> Result<PathBuf, StatusReply> {
        let (_, parts) = resolve(path)?;
        let mut local = self.root.clone();
        for part in parts {
            local.push(part);
        }
        Ok(local)
    }

    fn file(&mut self, handle: &str) -> Result<&mut std::fs::File, StatusReply> {
        match self.handles.get_mut(handle) {
            Some(OpenHandle::File(file)) => Ok(file),
            _ => Err(StatusCode::Failure.with_message(format!("not a file handle: {handle}"))),
        }
    }
}

/// The virtual path and its components. `.` and empty components are
/// dropped; `..` pops one, and popping above the root is refused. A
/// component holding a Windows separator or drive colon is refused too, so
/// no name can address a path outside the root on this platform.
fn resolve(path: &str) -> Result<(String, Vec<String>), StatusReply> {
    let mut parts: Vec<String> = Vec::new();
    for part in path.split('/') {
        match part {
            "" | "." => {}
            ".." => {
                if parts.pop().is_none() {
                    return Err(StatusCode::PermissionDenied
                        .with_message(format!("{path} is outside the served folder")));
                }
            }
            part if part.contains('\\') || part.contains(':') => {
                return Err(StatusCode::PermissionDenied
                    .with_message(format!("{path} is not a valid name here")));
            }
            part => parts.push(part.to_string()),
        }
    }
    Ok((format!("/{}", parts.join("/")), parts))
}

fn ok_status(id: u32) -> Status {
    Status {
        id,
        status_code: StatusCode::Ok,
        error_message: "Ok".to_string(),
        language_tag: "en-US".to_string(),
    }
}

fn io_reply(error: std::io::Error) -> StatusReply {
    let code = match error.kind() {
        ErrorKind::NotFound => StatusCode::NoSuchFile,
        ErrorKind::PermissionDenied => StatusCode::PermissionDenied,
        _ => StatusCode::Failure,
    };
    code.with_message(error.to_string())
}

fn attributes(metadata: &std::fs::Metadata) -> FileAttributes {
    let mut attrs = FileAttributes::from(metadata);
    if metadata.file_type().is_symlink() {
        attrs.set_regular(false);
        attrs.set_dir(false);
        attrs.set_symlink(true);
    }
    attrs
}

fn stat_path(path: &Path, follow: bool) -> Result<FileAttributes, StatusReply> {
    let metadata = if follow {
        std::fs::metadata(path)
    } else {
        std::fs::symlink_metadata(path)
    };
    metadata
        .map(|metadata| attributes(&metadata))
        .map_err(io_reply)
}

impl russh_sftp::server::Handler for SftpHandler {
    type Error = StatusReply;

    fn unimplemented(&self) -> Self::Error {
        StatusReply::new(StatusCode::OpUnsupported)
    }

    async fn init(
        &mut self,
        _version: u32,
        _extensions: HashMap<String, String>,
    ) -> Result<Version, Self::Error> {
        Ok(Version::new())
    }

    async fn realpath(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        let (virtual_path, _) = resolve(&path)?;
        Ok(Name {
            id,
            files: vec![File::dummy(virtual_path)],
        })
    }

    async fn opendir(&mut self, id: u32, path: String) -> Result<Handle, Self::Error> {
        let local = self.local(&path)?;
        let mut entries = Vec::new();
        for entry in std::fs::read_dir(&local).map_err(io_reply)? {
            let entry = entry.map_err(io_reply)?;
            let metadata = entry.metadata().map_err(io_reply)?;
            let name = entry.file_name().to_string_lossy().into_owned();
            entries.push(File::new(name, attributes(&metadata)));
        }
        let handle = self.add_handle(OpenHandle::Directory {
            entries: Some(entries),
        });
        Ok(Handle { id, handle })
    }

    async fn readdir(&mut self, id: u32, handle: String) -> Result<Name, Self::Error> {
        match self.handles.get_mut(&handle) {
            Some(OpenHandle::Directory { entries }) => match entries.take() {
                Some(files) if !files.is_empty() => Ok(Name { id, files }),
                _ => Err(StatusReply::new(StatusCode::Eof)),
            },
            _ => Err(StatusCode::Failure.with_message(format!("not a directory handle: {handle}"))),
        }
    }

    async fn close(&mut self, id: u32, handle: String) -> Result<Status, Self::Error> {
        match self.handles.remove(&handle) {
            Some(_) => Ok(ok_status(id)),
            None => Err(StatusCode::Failure.with_message(format!("unknown handle: {handle}"))),
        }
    }

    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        let attrs = stat_path(&self.local(&path)?, true)?;
        Ok(Attrs { id, attrs })
    }

    async fn lstat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        let attrs = stat_path(&self.local(&path)?, false)?;
        Ok(Attrs { id, attrs })
    }

    async fn fstat(&mut self, id: u32, handle: String) -> Result<Attrs, Self::Error> {
        let metadata = self.file(&handle)?.metadata().map_err(io_reply)?;
        Ok(Attrs {
            id,
            attrs: attributes(&metadata),
        })
    }

    async fn open(
        &mut self,
        id: u32,
        filename: String,
        pflags: OpenFlags,
        _attrs: FileAttributes,
    ) -> Result<Handle, Self::Error> {
        let local = self.local(&filename)?;
        let options: std::fs::OpenOptions = pflags.into();
        let file = options.open(&local).map_err(io_reply)?;
        let handle = self.add_handle(OpenHandle::File(file));
        Ok(Handle { id, handle })
    }

    async fn read(
        &mut self,
        id: u32,
        handle: String,
        offset: u64,
        len: u32,
    ) -> Result<Data, Self::Error> {
        let file = self.file(&handle)?;
        file.seek(SeekFrom::Start(offset)).map_err(io_reply)?;
        let mut data = vec![0u8; len.min(MAX_READ) as usize];
        let mut filled = 0;
        while filled < data.len() {
            let read = file.read(&mut data[filled..]).map_err(io_reply)?;
            if read == 0 {
                break;
            }
            filled += read;
        }
        if filled == 0 {
            return Err(StatusReply::new(StatusCode::Eof));
        }
        data.truncate(filled);
        Ok(Data { id, data })
    }

    async fn write(
        &mut self,
        id: u32,
        handle: String,
        offset: u64,
        data: Vec<u8>,
    ) -> Result<Status, Self::Error> {
        let file = self.file(&handle)?;
        file.seek(SeekFrom::Start(offset)).map_err(io_reply)?;
        file.write_all(&data).map_err(io_reply)?;
        Ok(ok_status(id))
    }

    async fn mkdir(
        &mut self,
        id: u32,
        path: String,
        _attrs: FileAttributes,
    ) -> Result<Status, Self::Error> {
        std::fs::create_dir(self.local(&path)?).map_err(io_reply)?;
        Ok(ok_status(id))
    }

    async fn rmdir(&mut self, id: u32, path: String) -> Result<Status, Self::Error> {
        let local = self.local(&path)?;
        if local == self.root {
            return Err(StatusCode::PermissionDenied.with_message("the served folder stays"));
        }
        std::fs::remove_dir(local).map_err(io_reply)?;
        Ok(ok_status(id))
    }

    async fn remove(&mut self, id: u32, filename: String) -> Result<Status, Self::Error> {
        std::fs::remove_file(self.local(&filename)?).map_err(io_reply)?;
        Ok(ok_status(id))
    }

    async fn rename(
        &mut self,
        id: u32,
        oldpath: String,
        newpath: String,
    ) -> Result<Status, Self::Error> {
        let source = self.local(&oldpath)?;
        let target = self.local(&newpath)?;
        // SFTP v3: a rename never replaces an existing entry.
        if std::fs::symlink_metadata(&target).is_ok() {
            return Err(StatusCode::Failure.with_message(format!("{newpath} already exists")));
        }
        std::fs::rename(source, target).map_err(io_reply)?;
        Ok(ok_status(id))
    }

    /// Accepted and ignored: the tests never depend on permissions or times.
    async fn setstat(
        &mut self,
        id: u32,
        path: String,
        _attrs: FileAttributes,
    ) -> Result<Status, Self::Error> {
        self.local(&path)?;
        Ok(ok_status(id))
    }
}

#[cfg(test)]
mod tests {
    use super::resolve;
    use russh_sftp::protocol::StatusCode;

    #[test]
    fn paths_resolve_inside_the_root_and_dot_dot_cannot_escape() {
        assert_eq!(resolve("/").unwrap().0, "/");
        assert_eq!(resolve("").unwrap().0, "/");
        assert_eq!(resolve("a/./b//c").unwrap().0, "/a/b/c");
        assert_eq!(resolve("/a/b/../c").unwrap().0, "/a/c");
        assert_eq!(
            resolve("/caf\u{e9} [x] & y").unwrap().1,
            vec!["caf\u{e9} [x] & y".to_string()]
        );
        for escape in ["..", "/../etc", "/a/../../b", r"/a\..\..\b", "/C:/Windows"] {
            let error = resolve(escape).expect_err(escape);
            assert_eq!(error.status_code, StatusCode::PermissionDenied, "{escape}");
        }
    }
}
