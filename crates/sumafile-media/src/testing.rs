//! Shared by the unit tests: a folder that is removed afterwards.

use std::path::PathBuf;

pub(crate) struct Folder(pub(crate) PathBuf);

impl Folder {
    pub(crate) fn new(name: &str) -> Self {
        let dir = std::env::temp_dir().join(format!(
            "sumafile-media-{name}-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    pub(crate) fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }

    pub(crate) fn write(&self, name: &str, bytes: &[u8]) -> PathBuf {
        let path = self.path(name);
        std::fs::write(&path, bytes).unwrap();
        path
    }
}

impl Drop for Folder {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
