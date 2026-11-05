//! Disk-backed completion records keep recursive moves bounded in memory.
use crate::transfer_staging::SourceVersion;
use serde::{Deserialize, Serialize};
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, BufWriter, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

#[derive(Serialize, Deserialize)]
struct Entry(PathBuf, SourceVersion);

#[derive(Debug)]
pub(crate) struct DiskManifest {
    path: PathBuf,
    /// Buffered: one record is many small JSON fragments, and a write syscall
    /// per fragment per completed file adds up over a large tree.
    file: Mutex<BufWriter<File>>,
}

impl DiskManifest {
    pub fn new() -> Result<Arc<Self>, String> {
        let path = std::env::temp_dir().join(format!(
            "sumafile-completed-{}.jsonl",
            crate::progress::generate_transfer_operation_id()
        ));
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(&path)
            .map_err(|e| format!("Create completion manifest: {e}"))?;
        file.try_lock().map_err(|e| e.to_string())?;
        Ok(Arc::new(Self {
            path,
            file: Mutex::new(BufWriter::with_capacity(64 * 1024, file)),
        }))
    }
    pub fn push(&self, path: &Path, version: &SourceVersion) -> Result<(), String> {
        let mut file = self.file.lock().map_err(|e| e.to_string())?;
        serde_json::to_writer(&mut *file, &Entry(path.into(), version.clone()))
            .map_err(|e| e.to_string())?;
        file.write_all(b"\n").map_err(|e| e.to_string())
    }
    pub fn visit(
        &self,
        mut action: impl FnMut(&Path, &SourceVersion) -> Result<(), String>,
    ) -> Result<(), String> {
        let mut writer = self.file.lock().map_err(|e| e.to_string())?;
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
        let _ = fs::remove_file(&self.path);
    }
}

#[derive(Debug, Clone)]
pub(crate) enum CopiedFiles {
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
