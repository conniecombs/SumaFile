//! Disk-backed completion records keep recursive moves bounded in memory.
use crate::staging::SourceVersion;
use serde::{Deserialize, Serialize};
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, BufWriter, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

#[derive(Serialize, Deserialize)]
struct Entry(PathBuf, SourceVersion);

#[derive(Debug)]
pub struct DiskManifest {
    path: PathBuf,
    /// Created by the first record. A move deletes each source right after
    /// its file is published and usually records nothing here, so a selection
    /// of many folders no longer creates, holds open and syncs one temporary
    /// file per folder.
    ///
    /// Buffered: one record is many small JSON fragments, and a write syscall
    /// per fragment per completed file adds up over a large tree.
    file: Mutex<Option<BufWriter<File>>>,
}

impl DiskManifest {
    pub fn new() -> Result<Arc<Self>, String> {
        let path = std::env::temp_dir().join(format!(
            "sumafile-completed-{}.jsonl",
            crate::ids::generate_transfer_operation_id()
        ));
        Ok(Arc::new(Self {
            path,
            file: Mutex::new(None),
        }))
    }
    fn create(path: &Path) -> Result<BufWriter<File>, String> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(path)
            .map_err(|e| format!("Create completion manifest: {e}"))?;
        file.try_lock().map_err(|e| e.to_string())?;
        Ok(BufWriter::with_capacity(64 * 1024, file))
    }
    pub fn push(&self, path: &Path, version: &SourceVersion) -> Result<(), String> {
        let mut slot = self.file.lock().map_err(|e| e.to_string())?;
        let file = match &mut *slot {
            Some(file) => file,
            None => slot.insert(Self::create(&self.path)?),
        };
        serde_json::to_writer(&mut *file, &Entry(path.into(), version.clone()))
            .map_err(|e| e.to_string())?;
        file.write_all(b"\n").map_err(|e| e.to_string())
    }
    pub fn visit(
        &self,
        mut action: impl FnMut(&Path, &SourceVersion) -> Result<(), String>,
    ) -> Result<(), String> {
        let mut slot = self.file.lock().map_err(|e| e.to_string())?;
        let Some(writer) = slot.as_mut() else {
            // Nothing was recorded.
            return Ok(());
        };
        writer.flush().map_err(|e| e.to_string())?;
        let file = writer.get_mut();
        // Persist all copy completions before source deletion starts.
        file.sync_all()
            .map_err(|e| format!("Persist move completion manifest: {e}"))?;
        file.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
        for line in BufReader::new(&mut *file).lines() {
            let line = line.map_err(|e| e.to_string())?;
            let Entry(path, version) = serde_json::from_str(&line)
                .map_err(|e| format!("Read completion manifest: {e}"))?;
            action(&path, &version)?;
        }
        Ok(())
    }
}
impl Drop for DiskManifest {
    fn drop(&mut self) {
        let created = self.file.get_mut().map_or_else(
            |poisoned| poisoned.into_inner().is_some(),
            |slot| slot.is_some(),
        );
        if created {
            let _ = fs::remove_file(&self.path);
        }
    }
}

#[derive(Debug, Clone)]
pub enum CopiedFiles {
    Memory(Vec<(PathBuf, SourceVersion)>),
    Disk(Arc<DiskManifest>),
}
impl Default for CopiedFiles {
    fn default() -> Self {
        Self::Memory(Vec::new())
    }
}
impl From<Vec<(PathBuf, SourceVersion)>> for CopiedFiles {
    fn from(value: Vec<(PathBuf, SourceVersion)>) -> Self {
        Self::Memory(value)
    }
}
impl CopiedFiles {
    pub fn visit(
        &self,
        mut action: impl FnMut(&Path, &SourceVersion) -> Result<(), String>,
    ) -> Result<(), String> {
        match self {
            Self::Memory(files) => {
                for (path, version) in files {
                    action(path, version)?;
                }
                Ok(())
            }
            Self::Disk(manifest) => manifest.visit(action),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::DiskManifest;

    /// The manifest of a folder whose files were deleted one by one as they
    /// were moved: nothing is recorded, so nothing is created on disk.
    #[test]
    fn a_manifest_without_records_creates_no_file() {
        let manifest = DiskManifest::new().unwrap();
        let path = manifest.path.clone();
        let mut visited = 0;
        manifest
            .visit(|_, _| {
                visited += 1;
                Ok(())
            })
            .unwrap();
        assert_eq!(visited, 0);
        assert!(!path.exists());
        drop(manifest);
        assert!(!path.exists());
    }

    #[test]
    fn records_are_read_back_in_order_and_the_file_is_removed() {
        let manifest = DiskManifest::new().unwrap();
        let path = manifest.path.clone();
        let source = std::env::temp_dir().join(format!(
            "sumafile-manifest-test-{}",
            crate::ids::generate_transfer_operation_id()
        ));
        std::fs::write(&source, b"moved").unwrap();
        let version = crate::staging::capture_source_version_for_path(&source).unwrap();
        for name in ["first", "second"] {
            manifest.push(std::path::Path::new(name), &version).unwrap();
        }
        assert!(path.exists());
        let mut seen = Vec::new();
        manifest
            .visit(|path, recorded| {
                assert_eq!(recorded, &version);
                seen.push(path.to_path_buf());
                Ok(())
            })
            .unwrap();
        assert_eq!(
            seen,
            [
                std::path::PathBuf::from("first"),
                std::path::PathBuf::from("second")
            ]
        );
        drop(manifest);
        assert!(!path.exists());
        std::fs::remove_file(source).unwrap();
    }
}
