//! Locked, durable ownership records for temporary files and replacement rollback.
//! Recovery only touches recorded file identities. It never deletes a source.
use crate::transfer_staging::{cleanup_owned_path, file_identity, FileIdentity};
use serde::{Deserialize, Serialize};
use simplefile_core::path_conflict::{path_exists_no_follow, rename_exclusive};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};

const RECORD_LIMIT: u64 = 4 * 1024 * 1024;

#[derive(Serialize, Deserialize)]
struct Record {
    version: u32,
    source: String,
    destination: PathBuf,
    staged: PathBuf,
    staged_id: FileIdentity,
    backup: Option<(PathBuf, FileIdentity)>,
    #[serde(default)]
    committed: bool,
}

pub(crate) struct StagingLease {
    path: PathBuf,
    file: File,
    record: Record,
}

fn record_path(staged: &Path) -> PathBuf {
    let base = if staged
        .file_name()
        .is_some_and(|n| n.to_string_lossy().ends_with(".sumafile-partial"))
    {
        staged.to_path_buf()
    } else {
        crate::transfer_staging::staging_path_for(staged, "ownership")
            .expect("transfer has a destination filename")
    };
    let mut name = base.as_os_str().to_os_string();
    name.push(".state");
    PathBuf::from(name)
}

fn open_locked(path: &Path, create: bool) -> Result<File, String> {
    let file = OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(create)
        .open(path)
        .map_err(|e| format!("Open transfer ownership record [{}]: {e}", path.display()))?;
    file.try_lock()
        .map_err(|e| format!("Transfer artifact is in use [{}]: {e}", path.display()))?;
    Ok(file)
}

impl StagingLease {
    pub fn begin(source: &str, destination: &Path, staged: &Path) -> Result<Self, String> {
        let path = record_path(staged);
        // Recovery runs once per directory before scheduling. An existing
        // unrecorded partial cannot establish ownership and is left intact.
        let file = open_locked(&path, true)?;
        let mut options = OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(windows)]
        if staged != destination {
            use std::os::windows::fs::OpenOptionsExt;
            use windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_HIDDEN;
            options.attributes(FILE_ATTRIBUTE_HIDDEN);
        }
        let stage = match options.open(staged) {
            Ok(file) => file,
            Err(error) => {
                let _ = fs::remove_file(&path);
                return Err(format!(
                    "Create owned transfer staging [{}]: {error}",
                    staged.display()
                ));
            }
        };
        drop(stage);
        Self::with_record(source, destination, staged, path, file)
    }

    pub fn record_created(source: &str, destination: &Path, staged: &Path) -> Result<Self, String> {
        let path = record_path(staged);
        let file = open_locked(&path, true)?;
        Self::with_record(source, destination, staged, path, file)
    }

    fn with_record(
        source: &str,
        destination: &Path,
        staged: &Path,
        path: PathBuf,
        file: File,
    ) -> Result<Self, String> {
        let staged_id = file_identity(staged)?;
        let mut lease = Self {
            path,
            file,
            record: Record {
                version: 1,
                source: source.into(),
                destination: destination.into(),
                staged: staged.into(),
                staged_id,
                backup: None,
                committed: false,
            },
        };
        lease.persist()?;
        Ok(lease)
    }

    fn persist(&mut self) -> Result<(), String> {
        let data = serde_json::to_vec(&self.record).map_err(|e| e.to_string())?;
        let end = self.file.seek(SeekFrom::End(0)).map_err(|e| e.to_string())?;
        if end.saturating_add(data.len() as u64 + 1) > RECORD_LIMIT {
            return Err(
                "Transfer ownership record is full; artifacts retained for recovery".into(),
            );
        }
        self.file.write_all(&data).map_err(|e| e.to_string())?;
        self.file.write_all(b"\n").map_err(|e| e.to_string())?;
        self.file
            .sync_all()
            .map_err(|e| format!("Persist transfer ownership [{}]: {e}", self.path.display()))
    }

    pub fn backup_intent(&mut self, path: &Path, original: &Path) -> Result<(), String> {
        let backup = (path.into(), file_identity(original)?);
        if self.record.backup.as_ref() == Some(&backup) {
            return Ok(());
        }
        let previous = self.record.backup.replace(backup);
        if let Err(error) = self.persist() {
            self.record.backup = previous;
            return Err(error);
        }
        Ok(())
    }

    pub fn owns_staged(&self) -> bool {
        file_identity(&self.record.staged).is_ok_and(|id| id == self.record.staged_id)
    }

    pub fn finish(mut self) -> Result<(), String> {
        if self.record.staged == self.record.destination {
            // In-place writes need a completion marker. For a renamed staging
            // object its identity at the final name already proves promotion.
            self.record.committed = true;
            self.persist()?;
        }
        fs::remove_file(&self.path).map_err(|e| {
            format!(
                "Remove transfer ownership record [{}]: {e}",
                self.path.display()
            )
        })
    }

    pub fn abort(self) -> Result<(), String> {
        recover(&self.record)?;
        self.finish()
    }
}

fn matches(path: &Path, id: &FileIdentity) -> bool {
    file_identity(path).is_ok_and(|actual| actual == *id)
}

fn recover(record: &Record) -> Result<(), String> {
    if let Some((backup, id)) = &record.backup {
        if matches(backup, id) {
            if !path_exists_no_follow(&record.destination) {
                rename_exclusive(backup, &record.destination).map_err(|e| {
                    format!(
                        "Restore interrupted replacement [{}]: {e}",
                        backup.display()
                    )
                })?;
            } else if matches(&record.destination, &record.staged_id) {
                cleanup_owned_path(backup)?;
            } else {
                return Err(format!(
                    "Interrupted replacement needs recovery; original retained at {}",
                    backup.display()
                ));
            }
        } else if path_exists_no_follow(backup) {
            return Err(format!(
                "Recovery backup identity changed; retained {}",
                backup.display()
            ));
        }
    }
    if !record.committed && path_exists_no_follow(&record.staged) {
        if !matches(&record.staged, &record.staged_id) {
            return Err(format!(
                "Recovery staging identity changed; retained {}",
                record.staged.display()
            ));
        }
        cleanup_owned_path(&record.staged)?;
    }
    Ok(())
}

/// One scan per destination directory, rather than one scan per copied file.
/// A live lock, an invalid/torn record, or an unrelated partial is preserved.
pub(crate) fn recover_directory(directory: &Path) -> Result<(), String> {
    let entries = match fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(format!("Read transfer recovery directory: {e}")),
    };
    for entry in entries {
        let entry = entry.map_err(|e| e.to_string())?;
        let name = entry.file_name();
        if !name.to_string_lossy().ends_with(".sumafile-partial.state") {
            continue;
        }
        let path = entry.path();
        let Ok(mut file) = open_locked(&path, false) else {
            continue;
        };
        if file.metadata().map_err(|e| e.to_string())?.len() > RECORD_LIMIT {
            continue;
        }
        let mut bytes = Vec::new();
        (&mut file)
            .take(RECORD_LIMIT)
            .read_to_end(&mut bytes)
            .map_err(|e| e.to_string())?;
        // Append-only records preserve the last synced state if a subsequent
        // update is torn. Ignore an incomplete trailing line.
        let Some(end) = bytes.iter().rposition(|byte| *byte == b'\n') else {
            continue;
        };
        let last = bytes[..end]
            .rsplit(|byte| *byte == b'\n')
            .next()
            .unwrap_or_default();
        let Ok(record) = serde_json::from_slice::<Record>(last) else {
            continue;
        };
        if record.version != 1
            || record_path(&record.staged) != path
            || record.staged.parent() != Some(directory)
            || record.destination.parent() != Some(directory)
            || record.backup.as_ref().is_some_and(|(p, _)| {
                p.parent() != Some(directory)
                    || !p
                        .file_name()
                        .is_some_and(|n| n.to_string_lossy().ends_with(".sumafile-replace-backup"))
            })
        {
            continue;
        }
        recover(&record)?;
        fs::remove_file(&path)
            .map_err(|e| format!("Remove recovered transfer record [{}]: {e}", path.display()))?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn root() -> PathBuf {
        let root = std::env::temp_dir().join(crate::progress::generate_transfer_operation_id());
        fs::create_dir(&root).unwrap();
        root
    }
    #[test]
    fn recovery_preserves_live_and_unowned_artifacts() {
        let root = root();
        let destination = root.join("file");
        let stage = crate::transfer_staging::staging_path_for(&destination, "active").unwrap();
        let lease = StagingLease::begin("source", &destination, &stage).unwrap();
        fs::write(&stage, b"active").unwrap();
        let unknown = root.join(".file.legacy.sumafile-partial");
        fs::write(&unknown, b"unrelated").unwrap();
        recover_directory(&root).unwrap();
        assert_eq!(fs::read(&stage).unwrap(), b"active");
        drop(lease);
        recover_directory(&root).unwrap();
        assert!(!stage.exists());
        assert_eq!(fs::read(&unknown).unwrap(), b"unrelated");
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn recovery_restores_backup_or_keeps_committed_destination() {
        for promoted in [false, true] {
            let root = root();
            let destination = root.join("file");
            let stage = crate::transfer_staging::staging_path_for(&destination, "crash").unwrap();
            let backup = root.join(".file.original.sumafile-replace-backup");
            fs::write(&destination, b"old").unwrap();
            let mut lease = StagingLease::begin("source", &destination, &stage).unwrap();
            fs::write(&stage, b"new").unwrap();
            lease.backup_intent(&backup, &destination).unwrap();
            fs::rename(&destination, &backup).unwrap();
            if promoted {
                fs::rename(&stage, &destination).unwrap();
            }
            // A torn later update must not erase the synced backup intent.
            lease.file.write_all(b"{\"version\":").unwrap();
            drop(lease);
            recover_directory(&root).unwrap();
            assert_eq!(
                fs::read(&destination).unwrap(),
                if promoted { b"new" } else { b"old" }
            );
            assert!(!backup.exists());
            assert!(!stage.exists());
            assert!(!record_path(&stage).exists());
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn recovery_distinguishes_committed_and_abandoned_inplace_files() {
        for committed in [false, true] {
            let root = root();
            let destination = root.join("inplace");
            let mut lease = StagingLease::begin("source", &destination, &destination).unwrap();
            fs::write(&destination, b"downloaded").unwrap();
            if committed {
                lease.record.committed = true;
                lease.persist().unwrap();
            }
            drop(lease);
            recover_directory(&root).unwrap();
            assert_eq!(destination.exists(), committed);
            if committed {
                assert_eq!(fs::read(&destination).unwrap(), b"downloaded");
            }
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn recovery_refuses_replaced_artifact_identity() {
        let root = root();
        let destination = root.join("file");
        let stage = crate::transfer_staging::staging_path_for(&destination, "swap").unwrap();
        let lease = StagingLease::begin("source", &destination, &stage).unwrap();
        fs::rename(&stage, root.join("original-stage")).unwrap();
        fs::write(&stage, b"another transfer").unwrap();
        drop(lease);
        assert!(recover_directory(&root)
            .unwrap_err()
            .contains("identity changed"));
        assert_eq!(fs::read(&stage).unwrap(), b"another transfer");
        assert!(record_path(&stage).exists());
        fs::remove_dir_all(root).unwrap();
    }
}
