//! Locked ownership records for temporary files and replacement rollback.
//! Recovery only touches recorded file identities. It never deletes a source.
//!
//! There are two kinds of record, both named `*.sumafile-partial.state`:
//!
//! - A file's own record (version 1), beside the file it describes. It is
//!   written to disk before the step it describes. Writes straight to the
//!   destination, and links and folders created under a temporary name, use it.
//! - A folder record (version 2): one per destination folder of a pass, shared
//!   by every file the pass copies into that folder, a line per file. See
//!   [`FolderRecords`].
use crate::path_conflict::{path_collision_key, path_exists_no_follow, rename_exclusive};
use crate::staging::{cleanup_owned_file, cleanup_owned_path, file_identity, FileIdentity};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

const RECORD_LIMIT: u64 = 4 * 1024 * 1024;

/// A file's own record.
const OWN_RECORD: u32 = 1;
/// A line of a folder record.
const FOLDER_RECORD: u32 = 2;

#[derive(Clone, Serialize, Deserialize)]
struct Record {
    version: u32,
    source: String,
    destination: PathBuf,
    staged: PathBuf,
    staged_id: FileIdentity,
    backup: Option<(PathBuf, FileIdentity)>,
    #[serde(default)]
    committed: bool,
    /// Set for a file whose source is deleted once the file has its final
    /// name (a move): the length of the complete file. When the source is
    /// gone, what lies under the partial name is the only copy.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    moved: Option<u64>,
}

pub struct StagingLease {
    record: Record,
    /// The staging file this lease created, still open, for the first copy
    /// attempt to write through (no second create/open round trip).
    staged_file: Option<File>,
    home: Home,
    /// Set by `finish` and by an `abort` that cleaned up. A lease that ends
    /// any other way leaves its record for recovery.
    settled: bool,
}

/// Where a lease's record is kept.
enum Home {
    /// The file's own record, open and locked.
    Own { path: PathBuf, file: File },
    /// A line in the folder record.
    Folder {
        record: Arc<FolderRecord>,
        unsettled: Arc<Unsettled>,
    },
}

/// Where the own record of the file staged at `staged` lies. `None` for a
/// path without a file name, which no transfer stages; a record that names
/// one is not a transfer's.
fn record_path(staged: &Path) -> Option<PathBuf> {
    let base = if staged
        .file_name()
        .is_some_and(|n| n.to_string_lossy().ends_with(".sumafile-partial"))
    {
        staged.to_path_buf()
    } else {
        crate::staging::staging_path_for(staged, "ownership").ok()?
    };
    let mut name = base.as_os_str().to_os_string();
    name.push(".state");
    Some(PathBuf::from(name))
}

/// A record left by an earlier attempt at the same staged path whose cleanup
/// failed (the partial was still held open, say): nobody holds it any more, so
/// its artifacts are recovered and the record reused, instead of every retry of
/// the file failing on "record exists" until the folder is visited again.
fn reclaim_abandoned(path: &Path, staged: &Path, original: String) -> Result<File, String> {
    let Ok(mut file) = open_locked(path, false) else {
        // Held by a live lease: the original refusal stands.
        return Err(original);
    };
    let mut bytes = Vec::new();
    (&mut file)
        .take(RECORD_LIMIT)
        .read_to_end(&mut bytes)
        .map_err(|e| format!("Read abandoned transfer record [{}]: {e}", path.display()))?;
    let last = bytes
        .iter()
        .rposition(|byte| *byte == b'\n')
        .map(|end| {
            bytes[..end]
                .rsplit(|byte| *byte == b'\n')
                .next()
                .unwrap_or_default()
        })
        .unwrap_or_default();
    let Ok(record) = serde_json::from_slice::<Record>(last) else {
        return Err(original);
    };
    if record.version != OWN_RECORD || !same_path(&record.staged, staged) {
        return Err(original);
    }
    recover(&record)?;
    file.set_len(0)
        .and_then(|()| file.seek(SeekFrom::Start(0)).map(|_| ()))
        .map_err(|e| format!("Reset abandoned transfer record [{}]: {e}", path.display()))?;
    Ok(file)
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
    /// `overwrite` is only for an in-place replacement (`staged == destination`):
    /// the existing destination is truncated and rewritten, so an interrupted
    /// copy leaves no original to restore.
    pub fn begin(
        source: &str,
        destination: &Path,
        staged: &Path,
        overwrite: bool,
    ) -> Result<Self, String> {
        let path = record_path(staged).ok_or_else(|| {
            format!(
                "Transfer destination has no file name: {}",
                staged.display()
            )
        })?;
        // Recovery runs once per directory before scheduling. An existing
        // unrecorded partial cannot establish ownership and is left intact.
        let file = match open_locked(&path, true) {
            Ok(file) => file,
            Err(error) if path_exists_no_follow(&path) => reclaim_abandoned(&path, staged, error)?,
            Err(error) => return Err(error),
        };
        let stage = match open_staged(
            staged,
            overwrite && staged == destination,
            staged != destination,
        ) {
            Ok(file) => file,
            Err(error) => {
                let _ = fs::remove_file(&path);
                return Err(format!(
                    "Create owned transfer staging [{}]: {error}",
                    staged.display()
                ));
            }
        };
        Self::with_record(source, destination, staged, path, file, Some(stage))
    }

    pub fn record_created(source: &str, destination: &Path, staged: &Path) -> Result<Self, String> {
        let path = record_path(staged).ok_or_else(|| {
            format!(
                "Transfer destination has no file name: {}",
                staged.display()
            )
        })?;
        let file = open_locked(&path, true)?;
        Self::with_record(source, destination, staged, path, file, None)
    }

    fn with_record(
        source: &str,
        destination: &Path,
        staged: &Path,
        path: PathBuf,
        file: File,
        staged_file: Option<File>,
    ) -> Result<Self, String> {
        let staged_id = match &staged_file {
            Some(handle) => identity_from_handle(handle, staged)?,
            None => file_identity(staged)?,
        };
        let mut lease = Self {
            record: Record {
                version: OWN_RECORD,
                source: source.into(),
                destination: destination.into(),
                staged: staged.into(),
                staged_id,
                backup: None,
                committed: false,
                moved: None,
            },
            staged_file,
            home: Home::Own { path, file },
            settled: false,
        };
        if let Err(error) = lease.persist() {
            // Nothing was recorded, so nothing must be left behind: an empty
            // record plus an unowned partial would otherwise live on forever.
            let staged = lease.record.staged.clone();
            let owns_stage = lease.staged_file.is_some();
            let record_path = match &lease.home {
                Home::Own { path, .. } => Some(path.clone()),
                Home::Folder { .. } => None,
            };
            drop(lease);
            if let Some(record_path) = record_path {
                let _ = fs::remove_file(&record_path);
            }
            if owns_stage {
                let _ = fs::remove_file(&staged);
            }
            return Err(error);
        }
        Ok(lease)
    }

    /// The open staging handle, once. Later calls return `None` so retries
    /// reopen the partial file and resume it.
    pub fn take_staged_file(&mut self) -> Option<File> {
        self.staged_file.take()
    }

    /// Writes the record as it now stands: to the file's own record, synced,
    /// or as a line of the folder record, synced when `durable`.
    fn persist_as(&mut self, durable: bool) -> Result<(), String> {
        match &mut self.home {
            Home::Own { path, file } => persist_own(file, path, &self.record),
            Home::Folder { record, .. } => record.append(&self.record, durable),
        }
    }

    fn persist(&mut self) -> Result<(), String> {
        self.persist_as(true)
    }

    /// Records where the existing destination is moved aside to, before it is
    /// moved. This is written to disk whichever kind of record the lease has:
    /// it is what brings the original back after an interrupted Replace.
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

    /// Ends the lease of a file that is complete. It fails only when the file
    /// was written under its final name and its record could neither be
    /// removed nor marked complete: the caller must then treat the file as
    /// failed, because recovery will delete it.
    pub fn finish(mut self) -> Result<(), String> {
        if matches!(self.home, Home::Folder { .. }) {
            // A line needs no ending: the file is no longer under its partial
            // name, which is all recovery looks for. The folder record goes
            // when the pass is done with the folder.
            self.settled = true;
            return Ok(());
        }
        let Home::Own { path, .. } = &self.home else {
            self.settled = true;
            return Ok(());
        };
        let path = path.clone();
        // Removing the record is the completion: with no record, recovery has
        // nothing to act on. It needs no space, where writing a completion
        // mark first failed on a full volume and left a finished file to be
        // deleted as unfinished.
        let removed = fs::remove_file(&path)
            .map_err(|e| format!("Remove transfer ownership record [{}]: {e}", path.display()));
        let Err(error) = removed else {
            self.settled = true;
            return Ok(());
        };
        if self.record.staged == self.record.destination {
            // A file written under its final name whose record cannot be
            // removed: the record must at least say that the file is
            // complete. When that cannot be written either, the lease stays
            // unsettled and the file must not be reported as copied, because
            // recovery will delete it. This is the one failure of `finish`.
            self.record.committed = true;
            self.persist().map_err(|mark| format!("{error}; {mark}"))?;
        }
        // The file is complete and recovery will leave it alone: a record that
        // names a partial file that is gone, or one marked complete, is
        // removed the next time the folder is visited.
        log::warn!("{error}");
        self.settled = true;
        Ok(())
    }

    pub fn abort(self) -> Result<(), String> {
        // A clean-up that fails leaves the lease unsettled, and with it the
        // record, for the next try of this file or the next visit to the folder.
        recover(&self.record)?;
        self.finish()
    }
}

impl Drop for StagingLease {
    fn drop(&mut self) {
        if self.settled {
            return;
        }
        // The file's own record simply stays on disk. A line of the folder
        // record is remembered, so that the record is kept when the pass ends
        // and a later try of the same file can take the partial over.
        if let Home::Folder { record, unsettled } = &self.home {
            unsettled.add(self.record.clone(), record.clone());
        }
    }
}

/// Creates the staging file for exclusive use. `overwrite` is for a write
/// straight over an existing destination; `hidden` for a file under a
/// temporary name, which is hidden while it is written.
#[cfg_attr(not(windows), allow(unused_variables))]
fn open_staged(staged: &Path, overwrite: bool, hidden: bool) -> std::io::Result<File> {
    let mut options = OpenOptions::new();
    if overwrite {
        options.write(true).create(true).truncate(true);
    } else {
        options.write(true).create_new(true);
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_ATTRIBUTE_HIDDEN, FILE_FLAG_SEQUENTIAL_SCAN,
        };
        options.custom_flags(FILE_FLAG_SEQUENTIAL_SCAN);
        if hidden {
            options.attributes(FILE_ATTRIBUTE_HIDDEN);
        }
    }
    options.open(staged)
}

/// The record of every destination folder a pass writes into: one file per
/// folder, shared by the files the pass copies there, a line per file.
///
/// A file's own record costs a create, a write, a sync and a delete for every
/// file copied. Here a file costs one line appended to a record that is
/// already open. A line holds what a file's own record holds, so recovery
/// still acts only on the very file that was recorded.
///
/// A line is not written to disk before the file's bytes are. After a power
/// loss a partial file may therefore have no line; recovery leaves such a file
/// alone, as it leaves every unrecorded file. The line that says where a
/// replaced file was moved aside to is written to disk first, as before.
///
/// A folder's record is replaced by a fresh one when it is full, and is
/// deleted when the last file recorded in it is done. A pass keeps the records
/// of the folders it used last open, and closes them all when it ends.
pub struct FolderRecords {
    operation: String,
    rotate_at: u64,
    keep_open: usize,
    open: Mutex<OpenRecords>,
    unsettled: Arc<Unsettled>,
}

#[derive(Default)]
struct OpenRecords {
    by_folder: HashMap<String, OpenRecord>,
    clock: u64,
}

struct OpenRecord {
    record: Arc<FolderRecord>,
    used: u64,
}

/// A folder record holds this much before a fresh one takes over.
const ROTATE_BYTES: u64 = 256 * 1024;
/// How many folder records a pass keeps open for folders it is not writing to.
const OPEN_FOLDER_RECORDS: usize = 64;

impl FolderRecords {
    pub fn new(operation_id: &str) -> Self {
        Self::with_limits(operation_id, ROTATE_BYTES, OPEN_FOLDER_RECORDS)
    }

    fn with_limits(operation_id: &str, rotate_at: u64, keep_open: usize) -> Self {
        Self {
            operation: operation_id.to_string(),
            rotate_at,
            keep_open,
            open: Mutex::new(OpenRecords::default()),
            unsettled: Arc::new(Unsettled::default()),
        }
    }

    /// Creates the partial file `staged` for `destination` and records it in
    /// the record of its folder. A partial file already there is taken over
    /// only when an earlier try of this pass left it.
    ///
    /// `moved` is the source's length when the source is deleted once the
    /// file has its final name; recovery then never deletes the file while
    /// its source is gone.
    pub fn begin(
        &self,
        source: &str,
        destination: &Path,
        staged: &Path,
        moved: Option<u64>,
    ) -> Result<StagingLease, String> {
        let directory = staged
            .parent()
            .ok_or_else(|| "Cannot get destination directory".to_string())?;
        let create = || open_staged(staged, false, true);
        let stage = match create() {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
                match self.unsettled.reclaim(staged) {
                    Ok(()) => {}
                    // Not left by this pass. An earlier pass of the task may
                    // have left it with its record, which is no longer held:
                    // recovery of the folder deals with exactly that, and
                    // with nothing a running pass or anyone else owns.
                    Err(Reclaim::NotOurs) => {
                        let _ = recover_directory(directory);
                    }
                    Err(Reclaim::Failed(reason)) => {
                        return Err(format!(
                            "Create owned transfer staging [{}]: {error}; {reason}",
                            staged.display()
                        ))
                    }
                }
                create().map_err(|error| {
                    format!(
                        "Create owned transfer staging [{}]: {error}",
                        staged.display()
                    )
                })?
            }
            Err(error) => {
                return Err(format!(
                    "Create owned transfer staging [{}]: {error}",
                    staged.display()
                ))
            }
        };
        // From here the partial file is this pass's own: it was created just
        // now, so removing it when it cannot be recorded loses nothing.
        let recorded = identity_from_handle(&stage, staged).and_then(|staged_id| {
            let record = Record {
                version: FOLDER_RECORD,
                source: source.into(),
                destination: destination.into(),
                staged: staged.into(),
                staged_id,
                backup: None,
                committed: false,
                moved,
            };
            let home = self.record_for(directory)?;
            home.append(&record, false)?;
            Ok((record, home))
        });
        match recorded {
            Ok((record, home)) => Ok(StagingLease {
                record,
                staged_file: Some(stage),
                home: Home::Folder {
                    record: home,
                    unsettled: self.unsettled.clone(),
                },
                settled: false,
            }),
            Err(error) => {
                drop(stage);
                let _ = fs::remove_file(staged);
                Err(error)
            }
        }
    }

    /// The open record of `directory`, a fresh one when there is none or the
    /// open one is full.
    fn record_for(&self, directory: &Path) -> Result<Arc<FolderRecord>, String> {
        let key = path_collision_key(directory);
        let mut open = self
            .open
            .lock()
            .map_err(|_| "Transfer ownership records are unusable".to_string())?;
        open.clock += 1;
        let now = open.clock;
        if let Some(current) = open.by_folder.get_mut(&key) {
            if current.record.written.load(Ordering::Relaxed) < self.rotate_at {
                current.used = now;
                return Ok(current.record.clone());
            }
        }
        let record = FolderRecord::create(directory, &self.operation)?;
        // A full record that this replaces is deleted when the last file
        // recorded in it is done.
        open.by_folder.insert(
            key.clone(),
            OpenRecord {
                record: record.clone(),
                used: now,
            },
        );
        while open.by_folder.len() > self.keep_open {
            let idle = open
                .by_folder
                .iter()
                .filter(|(folder, entry)| **folder != key && Arc::strong_count(&entry.record) == 1)
                .min_by_key(|(_, entry)| entry.used)
                .map(|(folder, _)| folder.clone());
            let Some(idle) = idle else {
                // Every open record has a file being copied.
                break;
            };
            open.by_folder.remove(&idle);
        }
        Ok(record)
    }
}

/// One folder record on disk, open and locked for as long as anything refers
/// to it. It is deleted when the last reference goes, unless a file recorded
/// in it was left for recovery.
struct FolderRecord {
    path: PathBuf,
    file: Mutex<File>,
    written: AtomicU64,
    /// Files recorded here whose clean-up is still owed.
    unsettled: AtomicUsize,
    /// The last write failed and may have left a fragment of a line.
    torn: AtomicBool,
}

impl FolderRecord {
    fn create(directory: &Path, operation: &str) -> Result<Arc<Self>, String> {
        let key = crate::staging::fnv1a_64(&[
            path_collision_key(directory).as_bytes(),
            &[0xff],
            operation.as_bytes(),
        ]);
        let mut refused = None;
        // The number tells apart the records one pass has in a folder: a full
        // one and its successor, or one an earlier pass of the task left.
        for number in 0..1000u32 {
            let path = directory.join(format!(
                "{FOLDER_RECORD_PREFIX}{key:016x}.{number}{RECORD_SUFFIX}"
            ));
            match open_locked(&path, true) {
                Ok(file) => {
                    return Ok(Arc::new(Self {
                        path,
                        file: Mutex::new(file),
                        written: AtomicU64::new(0),
                        unsettled: AtomicUsize::new(0),
                        torn: AtomicBool::new(false),
                    }))
                }
                Err(_) if path_exists_no_follow(&path) => continue,
                Err(error) => {
                    refused = Some(error);
                    break;
                }
            }
        }
        Err(refused.unwrap_or_else(|| {
            format!(
                "Could not choose a transfer ownership record name in [{}]",
                directory.display()
            )
        }))
    }

    /// Adds `record` as one line, in one write. `durable` writes it to disk
    /// before returning.
    fn append(&self, record: &Record, durable: bool) -> Result<(), String> {
        let mut file = self
            .file
            .lock()
            .map_err(|_| "Transfer ownership record is unusable".to_string())?;
        let mut line = Vec::new();
        if self.torn.load(Ordering::Relaxed) {
            // The write before this one failed and may have left part of a
            // line. This line starts on one of its own; recovery skips the
            // fragment and reads this one.
            line.push(b'\n');
        }
        serde_json::to_writer(&mut line, record).map_err(|e| e.to_string())?;
        line.push(b'\n');
        let written = self.written.load(Ordering::Relaxed);
        if written.saturating_add(line.len() as u64) > RECORD_LIMIT {
            return Err(
                "Transfer ownership record is full; artifacts retained for recovery".into(),
            );
        }
        // Nothing else writes to this file, so it is always at its end.
        if let Err(error) = file.write_all(&line) {
            self.torn.store(true, Ordering::Relaxed);
            return Err(format!(
                "Write transfer ownership [{}]: {error}",
                self.path.display()
            ));
        }
        self.torn.store(false, Ordering::Relaxed);
        self.written
            .store(written + line.len() as u64, Ordering::Relaxed);
        if durable {
            file.sync_all().map_err(|e| {
                format!("Persist transfer ownership [{}]: {e}", self.path.display())
            })?;
        }
        Ok(())
    }
}

impl Drop for FolderRecord {
    fn drop(&mut self) {
        if self.unsettled.load(Ordering::Relaxed) == 0 {
            // Every file recorded here was published or cleaned up.
            let _ = fs::remove_file(&self.path);
        }
    }
}

/// The files of a pass whose lease ended without `finish` or a clean `abort`:
/// what recovery, or a later try of the same file, still has to deal with.
#[derive(Default)]
struct Unsettled {
    by_staged: Mutex<HashMap<String, (Record, Arc<FolderRecord>)>>,
}

impl Unsettled {
    fn add(&self, record: Record, home: Arc<FolderRecord>) {
        let Ok(mut by_staged) = self.by_staged.lock() else {
            // The count is never taken back, so the record stays on disk.
            home.unsettled.fetch_add(1, Ordering::Relaxed);
            return;
        };
        home.unsettled.fetch_add(1, Ordering::Relaxed);
        if let Some((_, previous)) =
            by_staged.insert(path_collision_key(&record.staged), (record, home))
        {
            // The same partial again: the earlier line is superseded.
            previous.unsettled.fetch_sub(1, Ordering::Relaxed);
        }
    }

    /// Cleans up what an earlier try of this pass left at `staged`.
    fn reclaim(&self, staged: &Path) -> Result<(), Reclaim> {
        let key = path_collision_key(staged);
        let Ok(mut by_staged) = self.by_staged.lock() else {
            return Err(Reclaim::NotOurs);
        };
        let Some((record, _)) = by_staged.get(&key) else {
            return Err(Reclaim::NotOurs);
        };
        recover(record).map_err(Reclaim::Failed)?;
        if let Some((_, home)) = by_staged.remove(&key) {
            home.unsettled.fetch_sub(1, Ordering::Relaxed);
        }
        Ok(())
    }
}

/// Why a partial file already in place was not taken over.
enum Reclaim {
    /// This pass has no unsettled file under that name.
    NotOurs,
    /// It is this pass's, and cleaning it up failed.
    Failed(String),
}

/// The record of a same-volume Replace done by two renames: the file at the
/// destination is moved aside to a backup name, then the source is renamed
/// into its place.
///
/// It is the destination's own record, on disk before the first rename. It
/// says where the original goes and which file takes its place, so recovery
/// reads it like the record of any other Replace: it puts the original back
/// while the destination's name is free, and removes the backup once the
/// destination is the file that was moved in. It names no partial file, so
/// recovery removes neither the source nor the destination.
pub struct SwapRecord {
    path: PathBuf,
    /// Held, and so locked, for as long as the swap runs.
    _file: File,
}

impl SwapRecord {
    /// Records the swap before anything is renamed. `source` and
    /// `destination` are still where they were; nothing is at `backup`.
    pub fn begin(source: &Path, destination: &Path, backup: &Path) -> Result<Self, String> {
        let path = record_path(destination).ok_or_else(|| {
            format!(
                "Transfer destination has no file name: {}",
                destination.display()
            )
        })?;
        let record = Record {
            version: OWN_RECORD,
            source: source.to_string_lossy().into_owned(),
            destination: destination.into(),
            // The destination stands for its own partial file, complete from
            // the start: with `committed`, recovery never removes it.
            staged: destination.into(),
            // What the destination is once the swap is done: the source,
            // which keeps its identity under its new name.
            staged_id: file_identity(source)?,
            backup: Some((backup.into(), file_identity(destination)?)),
            committed: true,
            moved: None,
        };
        let mut file = match open_locked(&path, true) {
            Ok(file) => file,
            Err(error) if path_exists_no_follow(&path) => {
                reclaim_abandoned(&path, destination, error)?
            }
            Err(error) => return Err(error),
        };
        if let Err(error) = persist_own(&mut file, &path, &record) {
            drop(file);
            let _ = fs::remove_file(&path);
            return Err(error);
        }
        Ok(Self { path, _file: file })
    }

    /// The swap is over: both renames are done and the backup is gone, or
    /// the destination is back under its name. A swap that ends any other
    /// way drops its record without this, and recovery finishes it.
    pub fn finish(self) {
        if let Err(error) = fs::remove_file(&self.path) {
            // Recovery removes it: the state it describes needs nothing done.
            log::warn!(
                "Remove transfer ownership record [{}]: {error}",
                self.path.display()
            );
        }
    }
}

fn persist_own(file: &mut File, path: &Path, record: &Record) -> Result<(), String> {
    // The line and its terminator go out in one write. Recovery ignores a
    // record without a complete line, so a stop between two writes would
    // leave a record, and the partial file it names, that nothing ever
    // cleans up. It is also one request fewer for every file copied.
    let mut line = Vec::new();
    let end = file.seek(SeekFrom::End(0)).map_err(|e| e.to_string())?;
    if end > 0 {
        // An earlier write that failed may have left part of a line. Glued
        // to it, this line could not be read, and it is the record's state.
        let mut last = [0u8; 1];
        file.seek(SeekFrom::Start(end - 1))
            .and_then(|_| file.read_exact(&mut last))
            .map_err(|e| e.to_string())?;
        if last[0] != b'\n' {
            line.push(b'\n');
        }
        file.seek(SeekFrom::End(0)).map_err(|e| e.to_string())?;
    }
    serde_json::to_writer(&mut line, record).map_err(|e| e.to_string())?;
    line.push(b'\n');
    if end.saturating_add(line.len() as u64) > RECORD_LIMIT {
        return Err("Transfer ownership record is full; artifacts retained for recovery".into());
    }
    file.write_all(&line).map_err(|e| e.to_string())?;
    file.sync_all()
        .map_err(|e| format!("Persist transfer ownership [{}]: {e}", path.display()))
}

#[cfg(windows)]
fn identity_from_handle(handle: &File, staged: &Path) -> Result<FileIdentity, String> {
    crate::staging::file_id_from_handle(handle)
        .ok_or_else(|| format!("Cannot establish file identity [{}]", staged.display()))
}

#[cfg(not(windows))]
fn identity_from_handle(handle: &File, staged: &Path) -> Result<FileIdentity, String> {
    use std::os::unix::fs::MetadataExt;
    let meta = handle
        .metadata()
        .map_err(|e| format!("Cannot establish file identity [{}]: {e}", staged.display()))?;
    Ok(FileIdentity(meta.dev(), u128::from(meta.ino())))
}

fn matches(path: &Path, id: &FileIdentity) -> bool {
    crate::staging::is_recorded_file(path, id)
}

/// A move deletes its source once the file has its final name. If that rename
/// did not survive (a power loss, a drive pulled out), the file is back under
/// its partial name while its source is gone: it is the only copy, and an
/// "abandoned partial" only by its name.
fn is_only_copy(record: &Record) -> bool {
    record.moved.is_some()
        && !record.committed
        && path_exists_no_follow(&record.staged)
        && !path_exists_no_follow(Path::new(&record.source))
}

/// Gives the only copy of a moved file its final name when it is complete and
/// the name is free; otherwise keeps it, with its record. Never deletes it.
fn keep_only_copy(record: &Record) -> Result<(), String> {
    if !matches(&record.staged, &record.staged_id) {
        return Err(format!(
            "Recovery staging identity changed; retained {}",
            record.staged.display()
        ));
    }
    // The partial file is hidden until its last byte is written and flushed,
    // so a full length alone (a copy written in several streams reaches it
    // early) is not taken for a complete file.
    let complete = fs::symlink_metadata(&record.staged)
        .is_ok_and(|meta| meta.is_file() && Some(meta.len()) == record.moved)
        && !crate::staging::is_hidden(&record.staged);
    if !complete || path_exists_no_follow(&record.destination) {
        return Err(format!(
            "The source of a moved file is gone; its copy is kept at {}",
            record.staged.display()
        ));
    }
    rename_exclusive(&record.staged, &record.destination)
        .map_err(|e| format!("Finish interrupted move [{}]: {e}", record.staged.display()))?;
    // The file a Replace had moved aside: the move is now finished.
    if let Some((backup, id)) = &record.backup {
        if matches(backup, id) {
            cleanup_owned_file(backup)?;
        }
    }
    Ok(())
}

fn recover(record: &Record) -> Result<(), String> {
    if is_only_copy(record) {
        return keep_only_copy(record);
    }
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
                cleanup_owned_file(backup)?;
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
        // Versions that staged whole folders left a tree under a partial
        // name, and its record is the folder's own. Everything else a record
        // names is one file: a file written under its final name can carry
        // any name, so a folder found under it is never removed.
        let staged_tree = record.version == OWN_RECORD
            && record.staged != record.destination
            && record
                .staged
                .file_name()
                .is_some_and(|name| name.to_string_lossy().ends_with(PARTIAL_SUFFIX));
        if staged_tree {
            cleanup_owned_path(&record.staged)?;
        } else {
            cleanup_owned_file(&record.staged)?;
        }
    }
    Ok(())
}

/// Windows paths differ only by spelling (`C:` vs `c:`, mapped drive vs UNC
/// prefix); a record written under one spelling must still be recovered when
/// the directory is visited under another.
fn same_path(left: &Path, right: &Path) -> bool {
    left == right
        || crate::path_conflict::path_collision_key(left)
            == crate::path_conflict::path_collision_key(right)
}

/// The end of an ownership record's file name.
const RECORD_SUFFIX: &str = ".sumafile-partial.state";
/// The end of a partial file's name.
const PARTIAL_SUFFIX: &str = ".sumafile-partial";
/// The start of a folder record's file name. A file's own record starts with
/// the name of the file it describes.
const FOLDER_RECORD_PREFIX: &str = ".sumafile-transfer.";

fn is_record_name(name: &std::ffi::OsStr) -> bool {
    name.to_string_lossy().ends_with(RECORD_SUFFIX)
}

/// The ownership records directly in `directory`.
///
/// On Windows the filesystem is asked for the records by name, so a folder of
/// a hundred thousand files on a share is not listed over the network to find
/// none. Where that is not possible the folder is listed and filtered here.
fn record_files(directory: &Path) -> std::io::Result<Vec<PathBuf>> {
    #[cfg(windows)]
    if let Some(records) = record_files_by_pattern(directory) {
        return Ok(records);
    }
    let mut records = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        if is_record_name(&entry.file_name()) {
            records.push(entry.path());
        }
    }
    Ok(records)
}

/// `None` when the filtered search could not be made or did not finish, which
/// leaves the answer to the full listing: a folder that does not exist, a
/// filesystem that refuses the pattern, or a search that broke off part-way.
#[cfg(windows)]
fn record_files_by_pattern(directory: &Path) -> Option<Vec<PathBuf>> {
    use std::os::windows::ffi::OsStringExt;
    use windows_sys::Win32::Foundation::{
        GetLastError, ERROR_FILE_NOT_FOUND, ERROR_NO_MORE_FILES, INVALID_HANDLE_VALUE,
    };
    use windows_sys::Win32::Storage::FileSystem::{
        FindClose, FindExInfoBasic, FindExSearchNameMatch, FindFirstFileExW, FindNextFileW,
        FIND_FIRST_EX_LARGE_FETCH, WIN32_FIND_DATAW,
    };

    let pattern = crate::path_conflict::wide_path(&directory.join(format!("*{RECORD_SUFFIX}")));
    // SAFETY: an all-zero WIN32_FIND_DATAW is a valid value of that plain data struct.
    let mut data: WIN32_FIND_DATAW = unsafe { std::mem::zeroed() };
    // SAFETY: `pattern` is nul-terminated and outlives the call; `data` is a
    // WIN32_FIND_DATAW, which is what FindExInfoBasic writes.
    let handle = unsafe {
        FindFirstFileExW(
            pattern.as_ptr(),
            FindExInfoBasic,
            (&mut data as *mut WIN32_FIND_DATAW).cast(),
            FindExSearchNameMatch,
            std::ptr::null(),
            FIND_FIRST_EX_LARGE_FETCH,
        )
    };
    if handle == INVALID_HANDLE_VALUE {
        // SAFETY: reads the calling thread's last error code.
        let error = unsafe { GetLastError() };
        // The folder is there and holds no record. A folder that is missing
        // answers ERROR_PATH_NOT_FOUND and is left to the full listing.
        return matches!(error, ERROR_FILE_NOT_FOUND | ERROR_NO_MORE_FILES).then(Vec::new);
    }

    let mut records = Vec::new();
    let complete = loop {
        let length = data
            .cFileName
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(data.cFileName.len());
        let name = std::ffi::OsString::from_wide(&data.cFileName[..length]);
        // The filesystem matches without regard to case and may match a short
        // name; the record name is checked here as the full listing checks it.
        if is_record_name(&name) {
            records.push(directory.join(name));
        }
        // SAFETY: `handle` is the open search handle and `data` a valid buffer.
        if unsafe { FindNextFileW(handle, &mut data) } == 0 {
            // SAFETY: reads the calling thread's last error code.
            break unsafe { GetLastError() } == ERROR_NO_MORE_FILES;
        }
    };
    // SAFETY: `handle` came from FindFirstFileExW and is closed once.
    unsafe {
        FindClose(handle);
    }
    complete.then_some(records)
}

/// One scan per destination directory, rather than one scan per copied file.
/// A live lock, an invalid/torn record, or an unrelated partial is preserved.
pub fn recover_directory(directory: &Path) -> Result<(), String> {
    let records = match record_files(directory) {
        Ok(records) => records,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(format!("Read transfer recovery directory: {e}")),
    };
    for path in records {
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
            // Not one complete line: the record names nothing. A pass that
            // stopped right after creating its record leaves such a file.
            // It is removed once it is old; a new one may belong to a pass
            // that is creating it at this moment and has yet to lock it.
            if names_nothing_for_long(&file) {
                let _ = fs::remove_file(&path);
            }
            continue;
        };
        let lines: Vec<&[u8]> = bytes[..end].split(|byte| *byte == b'\n').collect();
        let last = lines
            .last()
            .and_then(|line| serde_json::from_slice::<Record>(line).ok());
        let owned = match last {
            // A file's own record: its last line is its state.
            Some(last)
                if last.version == OWN_RECORD
                    && record_path(&last.staged).is_some_and(|own| same_path(&own, &path))
                    && within(&last, directory) =>
            {
                vec![last]
            }
            // A folder record: every line is a file. When its last line
            // cannot be read, the others still stand. A file's own record is
            // its last line, so with that line unreadable nothing is known.
            Some(Record {
                version: FOLDER_RECORD,
                ..
            })
            | None => match folder_record_lines(&path, &lines, directory) {
                Some(records) => records,
                None => continue,
            },
            Some(_) => continue,
        };
        // A record that cannot be recovered safely (its artifact changed
        // identity) is retained for inspection and logged. It must not fail
        // every later transfer into this directory, nor keep the other files
        // of a folder record from being cleaned up.
        let mut retained = false;
        for record in &owned {
            if let Err(error) = recover(record) {
                log::warn!(
                    "Retained unrecoverable transfer record [{}]: {error}",
                    path.display()
                );
                retained = true;
            }
        }
        if retained {
            continue;
        }
        fs::remove_file(&path)
            .map_err(|e| format!("Remove recovered transfer record [{}]: {e}", path.display()))?;
    }
    Ok(())
}

/// A record without a complete line is removed when it is at least this old.
const EMPTY_RECORD_AGE: Duration = Duration::from_secs(24 * 60 * 60);

/// Whether an open record that names nothing has done so for a long time.
fn names_nothing_for_long(record: &File) -> bool {
    record
        .metadata()
        .and_then(|meta| meta.modified())
        .ok()
        .and_then(|modified| SystemTime::now().duration_since(modified).ok())
        .is_some_and(|age| age >= EMPTY_RECORD_AGE)
}

/// Whether everything `record` names lies directly in `directory`, and its
/// backup, if any, has a backup's name.
fn within(record: &Record, directory: &Path) -> bool {
    let inside = |path: &Path| path.parent().is_some_and(|p| same_path(p, directory));
    inside(&record.staged)
        && inside(&record.destination)
        && record.backup.as_ref().is_none_or(|(backup, _)| {
            inside(backup)
                && backup
                    .file_name()
                    .is_some_and(|n| n.to_string_lossy().ends_with(".sumafile-replace-backup"))
        })
}

/// The files a folder record names, each as its last line has it. `None` when
/// the file is not a folder record of this folder: a wrong name, no line that
/// can be read, a line that is not a folder record's, or a line that names
/// anything but a partial file in this folder.
///
/// A line that cannot be read at all is what a failed write leaves, and is
/// skipped: it names nothing, and the lines around it are as true as before.
fn folder_record_lines(path: &Path, lines: &[&[u8]], directory: &Path) -> Option<Vec<Record>> {
    let named = path.file_name().is_some_and(|name| {
        let name = name.to_string_lossy();
        name.starts_with(FOLDER_RECORD_PREFIX) && name.ends_with(RECORD_SUFFIX)
    });
    if !named || !path.parent().is_some_and(|p| same_path(p, directory)) {
        return None;
    }
    let mut records = Vec::<Record>::new();
    let mut position = HashMap::<String, usize>::new();
    let mut read = false;
    for line in lines {
        let Ok(record) = serde_json::from_slice::<Record>(line) else {
            continue;
        };
        read = true;
        let partial = record
            .staged
            .file_name()
            .is_some_and(|name| name.to_string_lossy().ends_with(PARTIAL_SUFFIX));
        if record.version != FOLDER_RECORD
            || record.committed
            || !partial
            || record.staged == record.destination
            || !within(&record, directory)
        {
            return None;
        }
        // A later line for the same partial file replaces the earlier one.
        match position.get(&path_collision_key(&record.staged)) {
            Some(&index) => records[index] = record,
            None => {
                position.insert(path_collision_key(&record.staged), records.len());
                records.push(record);
            }
        }
    }
    read.then_some(records)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn root() -> PathBuf {
        let root = std::env::temp_dir().join(crate::ids::generate_transfer_operation_id());
        fs::create_dir(&root).unwrap();
        root
    }
    /// An attempt whose cleanup failed leaves its record and partial behind; the
    /// retry of the same file takes them over instead of failing on the record.
    #[test]
    fn a_new_lease_reclaims_the_abandoned_record_of_the_same_staged_path() {
        let root = root();
        let destination = root.join("movie.mp4");
        let stage = crate::staging::staging_path_for(&destination, "op").unwrap();
        let record = record_path(&stage).unwrap();
        {
            let mut abandoned = StagingLease::begin("source", &destination, &stage, false).unwrap();
            let mut partial = abandoned.take_staged_file().unwrap();
            partial.write_all(b"half of the movie").unwrap();
            drop(partial);
            // Dropped without finish or abort: the record and the partial stay.
        }
        assert!(record.exists());
        assert_eq!(fs::read(&stage).unwrap(), b"half of the movie");

        let mut retried = StagingLease::begin("source", &destination, &stage, false).unwrap();
        // The retry owns a fresh, empty partial under the same name and the one record.
        assert_eq!(fs::metadata(&stage).unwrap().len(), 0);
        assert!(retried.owns_staged());
        drop(retried.take_staged_file());
        retried.finish().unwrap();
        assert!(!record.exists());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_record_held_by_a_live_lease_is_not_reclaimed() {
        let root = root();
        let destination = root.join("file");
        let stage = crate::staging::staging_path_for(&destination, "live").unwrap();
        let live = StagingLease::begin("source", &destination, &stage, false).unwrap();

        let error = StagingLease::begin("source", &destination, &stage, false)
            .err()
            .expect("a second lease on a live record is refused");

        assert!(
            error.contains("in use") || error.contains("exists"),
            "{error}"
        );
        assert!(live.owns_staged());
        drop(live);
        fs::remove_dir_all(root).unwrap();
    }

    /// Recovery asks for the ownership records of a folder, not for the folder.
    #[test]
    fn record_files_are_the_ownership_records_of_a_folder_and_nothing_else() {
        let root = root();
        for index in 0..200 {
            fs::write(root.join(format!("photo-{index}.jpg")), b"x").unwrap();
        }
        fs::write(root.join("notes.sumafile-partial"), b"partial").unwrap();
        fs::write(root.join("notes.sumafile-partial.state.bak"), b"other").unwrap();
        fs::create_dir(root.join("sub")).unwrap();
        fs::write(
            root.join("sub").join("deep.sumafile-partial.state"),
            b"deeper",
        )
        .unwrap();
        let first = root.join("a.sumafile-partial.state");
        let second = root.join(".b.1234.sumafile-partial.state");
        fs::write(&first, b"{}\n").unwrap();
        fs::write(&second, b"{}\n").unwrap();

        let mut found = record_files(&root).unwrap();
        found.sort();
        let mut expected = vec![first, second];
        expected.sort();
        assert_eq!(found, expected);

        fs::remove_dir_all(&root).unwrap();
        fs::create_dir(&root).unwrap();
        assert!(record_files(&root).unwrap().is_empty());

        fs::remove_dir_all(&root).unwrap();
        let missing = record_files(&root).unwrap_err();
        assert_eq!(missing.kind(), std::io::ErrorKind::NotFound);
        recover_directory(&root).expect("a folder that is gone has nothing to recover");
    }

    #[test]
    fn recovery_preserves_live_and_unowned_artifacts() {
        let root = root();
        let destination = root.join("file");
        let stage = crate::staging::staging_path_for(&destination, "active").unwrap();
        let lease = StagingLease::begin("source", &destination, &stage, false).unwrap();
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
            let stage = crate::staging::staging_path_for(&destination, "crash").unwrap();
            let backup = root.join(".file.original.sumafile-replace-backup");
            fs::write(&destination, b"old").unwrap();
            let mut lease = StagingLease::begin("source", &destination, &stage, false).unwrap();
            fs::write(&stage, b"new").unwrap();
            lease.backup_intent(&backup, &destination).unwrap();
            fs::rename(&destination, &backup).unwrap();
            if promoted {
                fs::rename(&stage, &destination).unwrap();
            }
            // A torn later update must not erase the synced backup intent.
            let Home::Own { file, .. } = &mut lease.home else {
                panic!("a file's own record");
            };
            file.write_all(b"{\"version\":").unwrap();
            drop(lease);
            recover_directory(&root).unwrap();
            assert_eq!(
                fs::read(&destination).unwrap(),
                if promoted { b"new" } else { b"old" }
            );
            assert!(!backup.exists());
            assert!(!stage.exists());
            assert!(!record_path(&stage).unwrap().exists());
            fs::remove_dir_all(root).unwrap();
        }
    }

    /// A same-volume Replace by rename: the destination is moved aside, then
    /// the source is renamed into its place. The fixture stops after `renames`
    /// of the two, as a crash would, and returns (root, source, destination,
    /// backup).
    fn stopped_swap(renames: u32) -> (PathBuf, PathBuf, PathBuf, PathBuf) {
        let root = root();
        let elsewhere = root.join("elsewhere");
        let folder = root.join("folder");
        fs::create_dir_all(&elsewhere).unwrap();
        fs::create_dir_all(&folder).unwrap();
        let source = elsewhere.join("file");
        let destination = folder.join("file");
        let backup = crate::staging::unique_backup_path(&destination).unwrap();
        fs::write(&source, b"new").unwrap();
        fs::write(&destination, b"old").unwrap();
        let record = SwapRecord::begin(&source, &destination, &backup).unwrap();
        if renames >= 1 {
            fs::rename(&destination, &backup).unwrap();
        }
        if renames >= 2 {
            fs::rename(&source, &destination).unwrap();
        }
        // The process stops here: the record is closed and stays on disk.
        drop(record);
        (root, source, destination, backup)
    }

    /// Between the two renames of a swap the destination's name is free and
    /// its file lies under a hidden backup name. No record said so, and
    /// recovery left the destination missing for good.
    #[test]
    fn recovery_puts_back_the_destination_of_a_swap_that_stopped_between_its_renames() {
        let (root, source, destination, backup) = stopped_swap(1);
        let folder = destination.parent().unwrap();
        assert!(!destination.exists());

        recover_directory(folder).unwrap();

        assert_eq!(fs::read(&destination).unwrap(), b"old");
        assert_eq!(
            fs::read(&source).unwrap(),
            b"new",
            "the source is never touched"
        );
        assert!(!backup.exists());
        assert_eq!(fs::read_dir(folder).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_removes_the_backup_of_a_swap_that_stopped_after_both_renames() {
        let (root, source, destination, backup) = stopped_swap(2);
        let folder = destination.parent().unwrap();

        recover_directory(folder).unwrap();

        assert_eq!(fs::read(&destination).unwrap(), b"new");
        assert!(!source.exists());
        assert!(!backup.exists());
        assert_eq!(fs::read_dir(folder).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_changes_nothing_for_a_swap_that_stopped_before_its_first_rename() {
        let (root, source, destination, _) = stopped_swap(0);
        let folder = destination.parent().unwrap();

        recover_directory(folder).unwrap();

        assert_eq!(fs::read(&destination).unwrap(), b"old");
        assert_eq!(fs::read(&source).unwrap(), b"new");
        assert_eq!(fs::read_dir(folder).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }

    /// Something else took the destination's name while its file lay under
    /// the backup name: neither is removed, and the record stays.
    #[test]
    fn recovery_keeps_the_backup_of_a_swap_whose_destination_is_now_another_file() {
        let (root, source, destination, backup) = stopped_swap(1);
        let folder = destination.parent().unwrap();
        fs::write(&destination, b"somebody else's").unwrap();

        recover_directory(folder).unwrap();

        assert_eq!(fs::read(&destination).unwrap(), b"somebody else's");
        assert_eq!(fs::read(&backup).unwrap(), b"old");
        assert_eq!(fs::read(&source).unwrap(), b"new");
        assert_eq!(fs::read_dir(folder).unwrap().count(), 3);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_finished_swap_leaves_no_record() {
        let root = root();
        let source = root.join("source");
        let folder = root.join("folder");
        fs::create_dir_all(&folder).unwrap();
        let destination = folder.join("file");
        let backup = crate::staging::unique_backup_path(&destination).unwrap();
        fs::write(&source, b"new").unwrap();
        fs::write(&destination, b"old").unwrap();

        let record = SwapRecord::begin(&source, &destination, &backup).unwrap();
        assert_eq!(fs::read_dir(&folder).unwrap().count(), 2);
        record.finish();

        assert_eq!(fs::read_dir(&folder).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }

    /// A swap in progress holds its record, so recovery by another transfer
    /// into the folder does not act on a state that is still changing.
    #[test]
    fn recovery_leaves_a_swap_that_is_still_running() {
        let root = root();
        let source = root.join("source");
        let folder = root.join("folder");
        fs::create_dir_all(&folder).unwrap();
        let destination = folder.join("file");
        let backup = crate::staging::unique_backup_path(&destination).unwrap();
        fs::write(&source, b"new").unwrap();
        fs::write(&destination, b"old").unwrap();
        let record = SwapRecord::begin(&source, &destination, &backup).unwrap();
        fs::rename(&destination, &backup).unwrap();

        recover_directory(&folder).unwrap();

        assert!(
            !destination.exists(),
            "the running swap was interfered with"
        );
        assert_eq!(fs::read(&backup).unwrap(), b"old");
        fs::rename(&source, &destination).unwrap();
        fs::remove_file(&backup).unwrap();
        record.finish();
        assert_eq!(fs::read(&destination).unwrap(), b"new");
        assert_eq!(fs::read_dir(&folder).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_distinguishes_committed_and_abandoned_inplace_files() {
        for committed in [false, true] {
            let root = root();
            let destination = root.join("inplace");
            let mut lease =
                StagingLease::begin("source", &destination, &destination, false).unwrap();
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

    /// Finishing a file written under its final name must not need to write
    /// anything. On a full volume the completion mark could not be written,
    /// the file was reported as copied all the same, and the next recovery
    /// deleted it as unfinished. Removing the record is the completion.
    #[test]
    fn finishing_a_write_directly_file_writes_nothing_to_its_record() {
        let root = root();
        let destination = root.join("inplace");
        let lease = StagingLease::begin("source", &destination, &destination, false).unwrap();
        fs::write(&destination, b"downloaded").unwrap();
        let record = record_path(&destination).unwrap();
        // A second name for the record shows what it held when it was removed.
        let as_finished = root.join("record-as-finished");
        fs::hard_link(&record, &as_finished).unwrap();

        lease.finish().unwrap();

        assert!(!record.exists());
        let lines = fs::read_to_string(&as_finished).unwrap();
        assert_eq!(
            lines.lines().count(),
            1,
            "finish wrote to the record: {lines}"
        );
        recover_directory(&root).unwrap();
        assert_eq!(fs::read(&destination).unwrap(), b"downloaded");
        fs::remove_dir_all(root).unwrap();
    }

    /// A crash during a transfer planned as `C:\\Dest` is still recovered when
    /// the folder is next visited as `c:\\dest`.
    #[test]
    fn recovery_ignores_path_spelling_differences() {
        let root = root();
        let destination = root.join("file");
        let stage = crate::staging::staging_path_for(&destination, "case").unwrap();
        let lease = StagingLease::begin("source", &destination, &stage, false).unwrap();
        drop(lease);
        assert!(stage.exists());

        let respelled = PathBuf::from(root.to_string_lossy().to_ascii_lowercase());
        recover_directory(&respelled).unwrap();
        assert!(!stage.exists(), "abandoned partial was not cleaned up");
        assert!(!record_path(&stage).unwrap().exists());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_retains_replaced_artifact_without_blocking_the_directory() {
        let root = root();
        let destination = root.join("file");
        let stage = crate::staging::staging_path_for(&destination, "swap").unwrap();
        let lease = StagingLease::begin("source", &destination, &stage, false).unwrap();
        fs::rename(&stage, root.join("original-stage")).unwrap();
        fs::write(&stage, b"another transfer").unwrap();
        drop(lease);
        // A record whose artifact changed identity is kept for inspection, but
        // it must not make every later transfer into this folder fail.
        recover_directory(&root).expect("conflicting record is retained, not fatal");
        assert_eq!(fs::read(&stage).unwrap(), b"another transfer");
        assert!(record_path(&stage).unwrap().exists());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn owned_staging_identity_holds_while_handles_are_open() {
        let root = root();
        let destination = root.join("file");
        let stage = crate::staging::staging_path_for(&destination, "open").unwrap();
        let mut lease = StagingLease::begin("source", &destination, &stage, false).unwrap();
        // The lease still holds the handle it created the staging file with.
        assert!(lease.owns_staged());
        // A writer that keeps the partial open is not a replacement either.
        let writer = OpenOptions::new().write(true).open(&stage).unwrap();
        assert!(lease.owns_staged());
        drop(writer);
        drop(lease.take_staged_file());
        assert!(lease.owns_staged());
        drop(lease);
        fs::remove_dir_all(root).unwrap();
    }

    // The folder records: one per destination folder of a pass, a line per file.

    fn record_count(directory: &Path) -> usize {
        record_files(directory).unwrap().len()
    }

    /// Begins a file of the pass in `directory` and writes `contents` to its partial.
    fn begin_in(
        records: &FolderRecords,
        directory: &Path,
        name: &str,
        contents: &[u8],
    ) -> (StagingLease, PathBuf, PathBuf) {
        let destination = directory.join(name);
        let stage = crate::staging::staging_path_for(&destination, "pass").unwrap();
        let mut lease = records.begin("source", &destination, &stage, None).unwrap();
        let mut partial = lease.take_staged_file().unwrap();
        partial.write_all(contents).unwrap();
        drop(partial);
        (lease, stage, destination)
    }

    /// Begins a file of a move in `directory`: `source` is deleted once the
    /// file has its final name, and is `length` bytes long.
    fn begin_moved(
        records: &FolderRecords,
        directory: &Path,
        source: &Path,
        length: u64,
        contents: &[u8],
    ) -> (StagingLease, PathBuf, PathBuf) {
        let destination = directory.join("file");
        let stage = crate::staging::staging_path_for(&destination, "pass").unwrap();
        let mut lease = records
            .begin(
                &source.to_string_lossy(),
                &destination,
                &stage,
                Some(length),
            )
            .unwrap();
        let mut partial = lease.take_staged_file().unwrap();
        partial.write_all(contents).unwrap();
        drop(partial);
        (lease, stage, destination)
    }

    /// A move deleted its source after giving the file its final name, and the
    /// power failed before that rename reached the disk: the complete file is
    /// back under its partial name and is the only copy. Recovery gives it
    /// its name; it must never delete it.
    #[test]
    fn recovery_publishes_a_complete_moved_file_whose_source_is_gone() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (lease, stage, destination) =
            begin_moved(&records, &root, &root.join("gone-source"), 5, b"moved");
        drop(lease);
        drop(records);

        recover_directory(&root).unwrap();

        assert_eq!(fs::read(&destination).unwrap(), b"moved");
        assert!(!stage.exists());
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    /// The same, but the copy is shorter than its source was: it cannot be
    /// given the final name, and it is still the only copy. It stays, with
    /// its record.
    #[test]
    fn recovery_keeps_an_incomplete_moved_file_whose_source_is_gone() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (lease, stage, destination) =
            begin_moved(&records, &root, &root.join("gone-source"), 50, b"half");
        drop(lease);
        drop(records);

        recover_directory(&root).unwrap();

        assert_eq!(fs::read(&stage).unwrap(), b"half");
        assert!(!destination.exists());
        assert_eq!(record_count(&root), 1);
        fs::remove_dir_all(root).unwrap();
    }

    /// A move that stopped before it deleted its source lost nothing: the
    /// partial file is removed like any other.
    #[test]
    fn recovery_removes_a_moved_partial_whose_source_is_still_there() {
        let root = root();
        let source = root.join("source-kept");
        fs::write(&source, b"moved").unwrap();
        let records = FolderRecords::new("pass");
        let (lease, stage, destination) = begin_moved(&records, &root, &source, 5, b"moved");
        drop(lease);
        drop(records);

        recover_directory(&root).unwrap();

        assert!(!stage.exists());
        assert!(!destination.exists());
        assert_eq!(fs::read(&source).unwrap(), b"moved");
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    /// A move with Replace stopped between its two renames after the power
    /// loss took the second one back: the old file is under its backup name,
    /// the complete new one under its partial name, and the source is gone.
    /// The user chose Replace and the new file is the only copy of what was
    /// moved: it gets the name, and the old file goes.
    #[test]
    fn recovery_finishes_a_replacing_move_whose_source_is_gone() {
        let root = root();
        let records = FolderRecords::new("pass");
        let destination = root.join("file");
        fs::write(&destination, b"old").unwrap();
        let (mut lease, stage, _) =
            begin_moved(&records, &root, &root.join("gone-source"), 3, b"new");
        let backup = root.join(".file.original.sumafile-replace-backup");
        lease.backup_intent(&backup, &destination).unwrap();
        fs::rename(&destination, &backup).unwrap();
        drop(lease);
        drop(records);

        recover_directory(&root).unwrap();

        assert_eq!(fs::read(&destination).unwrap(), b"new");
        assert!(!backup.exists());
        assert!(!stage.exists());
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    /// A complete moved file whose name is taken (the old file was never moved
    /// aside) cannot be published and must not be deleted.
    #[test]
    fn recovery_keeps_a_complete_moved_file_when_its_name_is_taken() {
        let root = root();
        let records = FolderRecords::new("pass");
        let destination = root.join("file");
        fs::write(&destination, b"old").unwrap();
        let (lease, stage, _) = begin_moved(&records, &root, &root.join("gone-source"), 3, b"new");
        drop(lease);
        drop(records);

        recover_directory(&root).unwrap();

        assert_eq!(fs::read(&destination).unwrap(), b"old");
        assert_eq!(fs::read(&stage).unwrap(), b"new");
        assert_eq!(record_count(&root), 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_folder_has_one_record_for_all_the_files_a_pass_copies_into_it() {
        let root = root();
        let records = FolderRecords::new("pass");
        for index in 0..5 {
            let (lease, stage, destination) =
                begin_in(&records, &root, &format!("file-{index}"), b"contents");
            assert!(lease.owns_staged());
            assert_eq!(record_count(&root), 1, "file {index}");
            fs::rename(&stage, &destination).unwrap();
            lease.finish().unwrap();
        }
        // The record stays open between files and goes with the pass.
        assert_eq!(record_count(&root), 1);
        drop(records);
        assert_eq!(record_count(&root), 0);
        assert_eq!(fs::read_dir(&root).unwrap().count(), 5);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_removes_what_a_stopped_pass_left_and_keeps_what_it_published() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (published, stage, destination) = begin_in(&records, &root, "published", b"whole");
        fs::rename(&stage, &destination).unwrap();
        published.finish().unwrap();
        let (first, first_stage, _) = begin_in(&records, &root, "first", b"half");
        let (second, second_stage, _) = begin_in(&records, &root, "second", b"half");

        // A running pass is left alone.
        recover_directory(&root).unwrap();
        assert!(first_stage.exists() && second_stage.exists());

        // The pass stops without cleaning up.
        drop(first);
        drop(second);
        drop(records);
        assert_eq!(record_count(&root), 1);
        assert!(first_stage.exists() && second_stage.exists());

        recover_directory(&root).unwrap();
        assert!(!first_stage.exists());
        assert!(!second_stage.exists());
        assert_eq!(fs::read(&destination).unwrap(), b"whole");
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_keeps_a_folder_record_whose_partial_is_now_another_file() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (swapped, swapped_stage, _) = begin_in(&records, &root, "swapped", b"half");
        let (plain, plain_stage, _) = begin_in(&records, &root, "plain", b"half");
        fs::rename(&swapped_stage, root.join("moved-away")).unwrap();
        fs::write(&swapped_stage, b"another transfer").unwrap();
        drop(swapped);
        drop(plain);
        drop(records);

        recover_directory(&root).expect("a record that is kept does not fail the folder");

        // The file that is not the recorded one is kept, and so is the record
        // that names it; the other file of the same record is cleaned up.
        assert_eq!(fs::read(&swapped_stage).unwrap(), b"another transfer");
        assert!(!plain_stage.exists());
        assert_eq!(record_count(&root), 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_restores_a_replaced_file_from_a_folder_record() {
        for promoted in [false, true] {
            let root = root();
            let records = FolderRecords::new("pass");
            let destination = root.join("file");
            fs::write(&destination, b"old").unwrap();
            let (mut lease, stage, _) = begin_in(&records, &root, "file", b"new");
            let backup = root.join(".file.original.sumafile-replace-backup");
            lease.backup_intent(&backup, &destination).unwrap();
            fs::rename(&destination, &backup).unwrap();
            if promoted {
                fs::rename(&stage, &destination).unwrap();
            }
            drop(lease);
            drop(records);

            recover_directory(&root).unwrap();

            assert_eq!(
                fs::read(&destination).unwrap(),
                if promoted { b"new" } else { b"old" }
            );
            assert!(!backup.exists());
            assert!(!stage.exists());
            assert_eq!(record_count(&root), 0);
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn a_full_folder_record_is_replaced_and_removed_when_its_files_are_done() {
        let root = root();
        // Room for three lines or so.
        let records = FolderRecords::with_limits("pass", 1024, OPEN_FOLDER_RECORDS);
        let mut most = 0;
        let (held, held_stage, held_destination) = begin_in(&records, &root, "held", b"x");
        for index in 0..40 {
            let (lease, stage, destination) =
                begin_in(&records, &root, &format!("file-{index}"), b"x");
            fs::rename(&stage, &destination).unwrap();
            lease.finish().unwrap();
            most = most.max(record_count(&root));
        }
        // The first record is kept for the file still being copied; every
        // other full one went when its last file was done.
        assert_eq!(most, 2);
        assert_eq!(record_count(&root), 2);
        fs::rename(&held_stage, &held_destination).unwrap();
        held.finish().unwrap();
        assert_eq!(record_count(&root), 1);
        drop(records);
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_pass_keeps_a_limited_number_of_idle_folder_records_open() {
        let root = root();
        let records = FolderRecords::with_limits("pass", ROTATE_BYTES, 2);
        let folders: Vec<PathBuf> = (0..5)
            .map(|index| {
                let folder = root.join(format!("folder-{index}"));
                fs::create_dir(&folder).unwrap();
                folder
            })
            .collect();
        for folder in &folders {
            let (lease, stage, destination) = begin_in(&records, folder, "file", b"x");
            fs::rename(&stage, &destination).unwrap();
            lease.finish().unwrap();
        }
        let open: usize = folders.iter().map(|folder| record_count(folder)).sum();
        assert_eq!(open, 2);
        // The folders used last are the ones still open.
        assert_eq!(record_count(&folders[4]), 1);
        assert_eq!(record_count(&folders[0]), 0);
        drop(records);
        let open: usize = folders.iter().map(|folder| record_count(folder)).sum();
        assert_eq!(open, 0);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_retry_takes_over_the_partial_its_own_pass_left() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (abandoned, stage, destination) = begin_in(&records, &root, "movie.mp4", b"half");
        // Dropped without finish or abort, as when its clean-up failed.
        drop(abandoned);
        assert_eq!(fs::read(&stage).unwrap(), b"half");

        let mut retried = records.begin("source", &destination, &stage, None).unwrap();
        assert_eq!(fs::metadata(&stage).unwrap().len(), 0);
        assert!(retried.owns_staged());
        drop(retried.take_staged_file());
        fs::rename(&stage, &destination).unwrap();
        retried.finish().unwrap();
        drop(records);
        // Nothing is left for a later visit to the folder.
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    /// A later pass of the same task finds what an earlier one left: the
    /// earlier pass's record is no longer held, so the folder is recovered.
    #[test]
    fn a_later_pass_takes_over_the_partial_an_earlier_pass_left() {
        let root = root();
        let earlier = FolderRecords::new("pass");
        let (abandoned, stage, destination) = begin_in(&earlier, &root, "movie.mp4", b"half");
        drop(abandoned);
        drop(earlier);
        assert_eq!(record_count(&root), 1);
        assert_eq!(fs::read(&stage).unwrap(), b"half");

        let later = FolderRecords::new("pass");
        let mut retried = later.begin("source", &destination, &stage, None).unwrap();
        assert_eq!(fs::metadata(&stage).unwrap().len(), 0);
        drop(retried.take_staged_file());
        fs::rename(&stage, &destination).unwrap();
        retried.finish().unwrap();
        drop(later);
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_partial_that_no_record_names_is_not_taken_over() {
        let root = root();
        let records = FolderRecords::new("pass");
        let destination = root.join("file");
        let stage = crate::staging::staging_path_for(&destination, "pass").unwrap();
        fs::write(&stage, b"someone else's").unwrap();

        let error = records
            .begin("source", &destination, &stage, None)
            .err()
            .expect("an unrecorded partial is not this pass's");

        assert!(error.contains("staging"), "{error}");
        assert_eq!(fs::read(&stage).unwrap(), b"someone else's");
        drop(records);
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    /// A build from before folder records reads the last line of a record and
    /// acts only on version 1, so it leaves these alone.
    /// Writes `record` as the one line of a record file, the way a hand-made
    /// or damaged record would lie in a folder.
    fn plant_record(path: &Path, record: &Record) {
        let mut line = serde_json::to_vec(record).unwrap();
        line.push(b'\n');
        fs::write(path, line).unwrap();
    }

    /// A record of a file written under its final name can name anything in
    /// its folder. What it names is a file: a folder under that name is
    /// somebody's data and recovery must leave it alone.
    #[test]
    fn recovery_never_removes_a_folder_that_a_write_directly_record_names() {
        let root = root();
        let folder = root.join("Projects");
        fs::create_dir(&folder).unwrap();
        fs::write(folder.join("report.txt"), b"work").unwrap();
        let record = Record {
            version: OWN_RECORD,
            source: "source".into(),
            destination: folder.clone(),
            staged: folder.clone(),
            staged_id: file_identity(&folder).unwrap(),
            backup: None,
            committed: false,
            moved: None,
        };
        let mut name = crate::staging::staging_path_for(&folder, "ownership")
            .unwrap()
            .into_os_string();
        name.push(".state");
        plant_record(Path::new(&name), &record);

        recover_directory(&root).unwrap();

        assert_eq!(fs::read(folder.join("report.txt")).unwrap(), b"work");
        fs::remove_dir_all(root).unwrap();
    }

    /// A record whose staged path has no file name is nobody's record. It is
    /// skipped; it must not stop the transfer that found it.
    #[test]
    fn recovery_skips_a_record_whose_staged_path_has_no_file_name() {
        for staged in ["", "..", "/"] {
            let root = root();
            let kept = root.join("kept.txt");
            fs::write(&kept, b"x").unwrap();
            let record = Record {
                version: OWN_RECORD,
                source: "source".into(),
                destination: PathBuf::from(staged),
                staged: PathBuf::from(staged),
                staged_id: file_identity(&kept).unwrap(),
                backup: None,
                committed: false,
                moved: None,
            };
            plant_record(
                &root.join(".x.0000000000000000.sumafile-partial.state"),
                &record,
            );

            recover_directory(&root).expect("a malformed record is skipped");

            assert!(kept.exists(), "staged path {staged:?}");
            fs::remove_dir_all(root).unwrap();
        }
    }

    /// A write that fails part-way leaves a fragment of a line. Every line
    /// after it used to be glued to the fragment, and one line that could not
    /// be read kept recovery away from every file the record names.
    #[test]
    fn recovery_acts_on_the_readable_lines_of_a_folder_record() {
        for damage in [&b"{\"version\":2,\"sou\n"[..], &b"not a line at all\n"[..]] {
            let root = root();
            let records = FolderRecords::new("pass");
            let (first, first_stage, _) = begin_in(&records, &root, "first", b"half");
            // What a failed write leaves, between two good lines. It goes
            // through the record's own handle, which writes where it stands.
            let Home::Folder { record: home, .. } = &first.home else {
                panic!("a line of the folder record");
            };
            home.file.lock().unwrap().write_all(damage).unwrap();
            let (second, second_stage, _) = begin_in(&records, &root, "second", b"half");
            let text = fs::read(record_files(&root).unwrap().remove(0)).unwrap();
            assert_eq!(text.split(|byte| *byte == b'\n').count(), 4, "three lines");
            drop(first);
            drop(second);
            drop(records);
            assert_eq!(record_count(&root), 1);

            recover_directory(&root).unwrap();

            assert!(
                !first_stage.exists(),
                "the line before the damage was not read"
            );
            assert!(
                !second_stage.exists(),
                "the line after the damage was not read"
            );
            assert_eq!(record_count(&root), 0);
            fs::remove_dir_all(root).unwrap();
        }
    }

    /// The same when the damaged line is the last one.
    #[test]
    fn recovery_acts_on_a_folder_record_whose_last_line_is_damaged() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (lease, stage, _) = begin_in(&records, &root, "file", b"half");
        let record = record_files(&root).unwrap().remove(0);
        drop(lease);
        drop(records);
        let mut file = OpenOptions::new().append(true).open(&record).unwrap();
        file.write_all(b"{\"version\":2,\"sou\n").unwrap();
        drop(file);

        recover_directory(&root).unwrap();

        assert!(!stage.exists());
        assert_eq!(record_count(&root), 0);
        fs::remove_dir_all(root).unwrap();
    }

    /// After a write that failed, the next line starts on a line of its own.
    #[test]
    fn a_line_written_after_a_failed_write_is_not_glued_to_the_fragment() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (first, _, _) = begin_in(&records, &root, "first", b"half");
        let record = record_files(&root).unwrap().remove(0);
        // A write that stopped part-way: a fragment with no line end.
        let Home::Folder { record: home, .. } = &first.home else {
            panic!("a line of the folder record");
        };
        home.file
            .lock()
            .unwrap()
            .write_all(b"{\"version\":2,\"sou")
            .unwrap();
        home.torn.store(true, Ordering::Relaxed);

        let (second, _, _) = begin_in(&records, &root, "second", b"half");

        let text = fs::read_to_string(&record).unwrap();
        let lines: Vec<&str> = text.lines().collect();
        assert_eq!(lines.len(), 3, "{text}");
        assert!(serde_json::from_str::<Record>(lines[0]).is_ok());
        assert!(serde_json::from_str::<Record>(lines[1]).is_err());
        assert!(serde_json::from_str::<Record>(lines[2]).is_ok());
        drop(first);
        drop(second);
        fs::remove_dir_all(root).unwrap();
    }

    /// A pass that stopped right after creating its folder record left an
    /// empty record that nothing ever removed.
    #[test]
    fn recovery_removes_an_old_record_that_names_nothing() {
        let root = root();
        let old = SystemTime::now() - Duration::from_secs(2 * 24 * 60 * 60);
        let empty = root.join(".sumafile-transfer.0123456789abcdef.0.sumafile-partial.state");
        let torn = root.join(".sumafile-transfer.0123456789abcdef.1.sumafile-partial.state");
        let own = root.join(".file.0123456789abcdef.sumafile-partial.state");
        fs::write(&empty, b"").unwrap();
        fs::write(&torn, b"{\"version\":2,\"sou").unwrap();
        fs::write(&own, b"").unwrap();
        for path in [&empty, &torn, &own] {
            let file = OpenOptions::new().write(true).open(path).unwrap();
            file.set_times(fs::FileTimes::new().set_modified(old))
                .unwrap();
        }

        recover_directory(&root).unwrap();

        assert_eq!(fs::read_dir(&root).unwrap().count(), 0);
        fs::remove_dir_all(root).unwrap();
    }

    /// A record that names nothing yet may be one a pass is creating at this
    /// moment: only an old one is removed.
    #[test]
    fn recovery_keeps_a_new_record_that_names_nothing() {
        let root = root();
        let empty = root.join(".sumafile-transfer.0123456789abcdef.0.sumafile-partial.state");
        fs::write(&empty, b"").unwrap();

        recover_directory(&root).unwrap();

        assert!(empty.exists());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_folder_record_is_not_version_one() {
        let root = root();
        let records = FolderRecords::new("pass");
        let (lease, _, _) = begin_in(&records, &root, "file", b"x");
        let record = record_files(&root).unwrap().remove(0);
        // Left as a stopped pass leaves it: the record stays, unlocked.
        drop(lease);
        drop(records);
        let text = fs::read_to_string(&record).unwrap();
        let last: serde_json::Value = serde_json::from_str(text.lines().last().unwrap()).unwrap();
        assert_eq!(last["version"], 2);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn recovery_ignores_a_folder_record_that_names_files_elsewhere() {
        let root = root();
        let other = root.join("other");
        fs::create_dir(&other).unwrap();
        let records = FolderRecords::new("pass");
        let (lease, stage, _) = begin_in(&records, &other, "file", b"half");
        let record = record_files(&other).unwrap().remove(0);
        drop(lease);
        drop(records);
        // The same record, found in a folder it does not describe.
        let planted = root.join(record.file_name().unwrap());
        fs::copy(&record, &planted).unwrap();
        fs::remove_file(&record).unwrap();

        recover_directory(&root).unwrap();

        assert_eq!(fs::read(&stage).unwrap(), b"half");
        assert!(planted.exists());
        fs::remove_dir_all(root).unwrap();
    }
}
