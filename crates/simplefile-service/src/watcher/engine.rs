//! The Windows half of the watcher: one thread that reads the changes of
//! every watched folder from one completion port.
//!
//! Each folder is an open directory handle with one `ReadDirectoryChangesExW`
//! request outstanding. The extended form of the request carries each
//! entry's size, dates and attributes with the change; a file system without
//! it is asked in the plain form, and its entries are read when a change is
//! applied.
//!
//! The thread waits on the port for a completed read, a command from the
//! session, or the time the next batch of changes is due. Nothing else waits
//! on it.

use super::coalesce::{Coalescer, RawChange};
use super::{coalesce_key, Emit, EntryNow};
use simplefile_core::utils::{
    file_entry_from_watched_facts, validate_existing_path_no_resolve, WatchedEntryFacts,
};
use std::collections::{HashMap, VecDeque};
use std::fs::File;
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsRawHandle;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::sync::oneshot;
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_IO_PENDING, ERROR_NOTIFY_ENUM_DIR, ERROR_OPERATION_ABORTED,
    HANDLE, INVALID_HANDLE_VALUE,
};
use windows_sys::Win32::Storage::FileSystem::{
    FileStandardInfo, GetFileInformationByHandleEx, GetFinalPathNameByHandleW,
    ReadDirectoryChangesExW, ReadDirectoryChangesW, ReadDirectoryNotifyExtendedInformation,
    FILE_ACTION_ADDED, FILE_ACTION_MODIFIED, FILE_ACTION_REMOVED, FILE_ACTION_RENAMED_NEW_NAME,
    FILE_ACTION_RENAMED_OLD_NAME, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OVERLAPPED,
    FILE_LIST_DIRECTORY, FILE_NOTIFY_CHANGE_ATTRIBUTES, FILE_NOTIFY_CHANGE_CREATION,
    FILE_NOTIFY_CHANGE_DIR_NAME, FILE_NOTIFY_CHANGE_FILE_NAME, FILE_NOTIFY_CHANGE_LAST_WRITE,
    FILE_NOTIFY_CHANGE_SECURITY, FILE_NOTIFY_CHANGE_SIZE, FILE_SHARE_DELETE, FILE_SHARE_READ,
    FILE_SHARE_WRITE, FILE_STANDARD_INFO,
};
use windows_sys::Win32::System::Threading::INFINITE;
use windows_sys::Win32::System::IO::{
    CancelIoEx, CreateIoCompletionPort, GetQueuedCompletionStatus, PostQueuedCompletionStatus,
    OVERLAPPED,
};

/// The size of a folder's change buffer. The largest a network share takes;
/// four times what the watcher used to ask for.
const BUFFER_BYTES: usize = 64 * 1024;

/// What a watched folder is asked to report: names, attributes, sizes,
/// write and creation times, and security.
const NOTIFY_FILTER: u32 = FILE_NOTIFY_CHANGE_FILE_NAME
    | FILE_NOTIFY_CHANGE_DIR_NAME
    | FILE_NOTIFY_CHANGE_ATTRIBUTES
    | FILE_NOTIFY_CHANGE_SIZE
    | FILE_NOTIFY_CHANGE_LAST_WRITE
    | FILE_NOTIFY_CHANGE_CREATION
    | FILE_NOTIFY_CHANGE_SECURITY;

/// How often each watched folder is asked whether it is still at its path.
/// A folder that is renamed or moved keeps its handle and reports nothing
/// about itself, so this is the only way to learn of it.
const PLACE_CHECK_EVERY: Duration = Duration::from_secs(2);

/// How long a closing watcher waits for its cancelled reads to come back.
const SHUTDOWN_PATIENCE: Duration = Duration::from_secs(2);

/// The completion key of a command; folders count up from one.
const COMMAND_KEY: usize = 0;

pub(super) enum Command {
    /// Watch exactly `paths` from now on and report to `emit`.
    SetRoots {
        paths: Vec<String>,
        emit: Emit,
        reply: Option<oneshot::Sender<Result<(), String>>>,
    },
    Shutdown,
}

/// The completion port; closed when the session's handle and the thread have
/// both let go of it.
struct Port(HANDLE);

// A completion port is made to be used from several threads.
unsafe impl Send for Port {}
unsafe impl Sync for Port {}

impl Drop for Port {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

struct Shared {
    port: Port,
    commands: Mutex<VecDeque<Command>>,
}

/// The session's handle on the watcher thread. Dropping it ends the thread,
/// without waiting for it.
pub(super) struct Engine {
    shared: Arc<Shared>,
}

impl Engine {
    pub(super) fn start(extended_records: bool) -> Result<Self, String> {
        let port =
            unsafe { CreateIoCompletionPort(INVALID_HANDLE_VALUE, std::ptr::null_mut(), 0, 1) };
        if port.is_null() {
            return Err(format!(
                "Failed to create the folder watcher: {}",
                std::io::Error::last_os_error()
            ));
        }
        let shared = Arc::new(Shared {
            port: Port(port),
            commands: Mutex::new(VecDeque::new()),
        });
        let for_thread = shared.clone();
        std::thread::Builder::new()
            .name("simplefile-watch".to_string())
            .spawn(move || Worker::new(for_thread, extended_records).run())
            .map_err(|error| format!("Failed to start the folder watcher: {error}"))?;
        Ok(Self { shared })
    }

    pub(super) fn send(&self, command: Command) {
        self.shared
            .commands
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .push_back(command);
        unsafe {
            PostQueuedCompletionStatus(self.shared.port.0, 0, COMMAND_KEY, std::ptr::null());
        }
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.send(Command::Shutdown);
    }
}

/// One watched folder. Boxed for as long as a read is outstanding: the
/// system writes into `buffer` and `overlapped` at the addresses it was
/// given.
struct Root {
    /// The folder as the host named it; the paths told are built on it.
    path: String,
    key: String,
    directory: File,
    overlapped: OVERLAPPED,
    /// 64-bit words, so the records in it are aligned as the system writes them.
    buffer: Vec<u64>,
    /// Whether the outstanding read asks for the extended records.
    extended: bool,
    /// Whether a read for the extended records has answered: until then a
    /// failure may only mean the file system has none.
    extended_proven: bool,
    /// A read is outstanding: the system still holds `buffer` and `overlapped`.
    reading: bool,
    /// Let go; kept only until the cancelled read comes back.
    closing: bool,
    /// The path the handle had when it was opened, as the system spells it.
    opened_as: Option<String>,
    /// Whether the handle answered when asked if it is being deleted.
    answers_standard_info: bool,
    /// The old name of a rename whose new name was not in the same buffer.
    held_old_name: Option<String>,
}

struct Worker {
    shared: Arc<Shared>,
    extended_records: bool,
    roots: HashMap<usize, Box<Root>>,
    next_key: usize,
    coalescer: Coalescer,
    emit: Option<Emit>,
    next_place_check: Instant,
}

impl Worker {
    fn new(shared: Arc<Shared>, extended_records: bool) -> Self {
        Self {
            shared,
            extended_records,
            roots: HashMap::new(),
            next_key: COMMAND_KEY + 1,
            coalescer: Coalescer::default(),
            emit: None,
            next_place_check: Instant::now() + PLACE_CHECK_EVERY,
        }
    }

    fn run(mut self) {
        loop {
            let completion = self.wait(self.wait_millis());
            let now = Instant::now();
            match completion {
                Completion::TimedOut => {}
                Completion::PortFailed => break,
                Completion::Io {
                    key: COMMAND_KEY, ..
                } => {
                    if !self.run_commands() {
                        break;
                    }
                }
                Completion::Io {
                    key,
                    ok,
                    code,
                    bytes,
                } => self.on_read(key, ok, code, bytes, now),
            }

            let now = Instant::now();
            if now >= self.next_place_check {
                self.next_place_check = now + PLACE_CHECK_EVERY;
                self.check_places(now);
            }
            let batch = self.coalescer.take_due(now);
            self.tell(batch);
        }
        self.shut_down();
    }

    /// How long to wait on the port: until the pending batch is due or the
    /// folders are next checked, for ever with nothing to do.
    fn wait_millis(&self) -> u32 {
        let watching = self.roots.values().any(|root| !root.closing);
        let due = match (self.coalescer.deadline(), watching) {
            (Some(batch), true) => Some(batch.min(self.next_place_check)),
            (Some(batch), false) => Some(batch),
            (None, true) => Some(self.next_place_check),
            (None, false) => None,
        };
        match due {
            None => INFINITE,
            Some(due) => {
                let left = due.saturating_duration_since(Instant::now());
                // Rounded up, so the wait does not end just before the time.
                (left.as_millis() as u64 + u64::from(left.subsec_nanos() % 1_000_000 != 0))
                    .min(u64::from(INFINITE - 1)) as u32
            }
        }
    }

    fn wait(&self, millis: u32) -> Completion {
        let mut bytes = 0u32;
        let mut key = 0usize;
        let mut overlapped: *mut OVERLAPPED = std::ptr::null_mut();
        let ok = unsafe {
            GetQueuedCompletionStatus(
                self.shared.port.0,
                &mut bytes,
                &mut key,
                &mut overlapped,
                millis,
            )
        } != 0;
        if ok {
            return Completion::Io {
                key,
                ok: true,
                code: 0,
                bytes,
            };
        }
        let code = unsafe { GetLastError() };
        if !overlapped.is_null() {
            // A read that failed: its packet names the folder all the same.
            return Completion::Io {
                key,
                ok: false,
                code,
                bytes,
            };
        }
        if code == windows_sys::Win32::Foundation::WAIT_TIMEOUT {
            Completion::TimedOut
        } else {
            Completion::PortFailed
        }
    }

    /// Runs the commands that are waiting. False when the watcher is to end.
    fn run_commands(&mut self) -> bool {
        loop {
            let command = self
                .shared
                .commands
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .pop_front();
            match command {
                None => return true,
                Some(Command::Shutdown) => return false,
                Some(Command::SetRoots { paths, emit, reply }) => {
                    let result = self.set_roots(paths, emit);
                    if let Some(reply) = reply {
                        let _ = reply.send(result);
                    }
                }
            }
        }
    }

    fn set_roots(&mut self, paths: Vec<String>, emit: Emit) -> Result<(), String> {
        // What was gathered for the folders watched until now is told first,
        // to the sink they were watched for.
        let batch = self.coalescer.take_all();
        self.tell(batch);

        // Open what is new before anything is let go: a folder that cannot
        // be watched leaves the watch as it was.
        let mut opened: Vec<Box<Root>> = Vec::new();
        for path in &paths {
            let key = coalesce_key(path);
            if self.live_key_of(&key).is_some() || opened.iter().any(|root| root.key == key) {
                continue;
            }
            opened.push(open_root(path, key, self.extended_records)?);
        }

        let mut added = Vec::new();
        let mut failure = None;
        for root in opened {
            let completion_key = self.next_key;
            self.next_key += 1;
            let attached = unsafe {
                CreateIoCompletionPort(
                    root.directory.as_raw_handle() as HANDLE,
                    self.shared.port.0,
                    completion_key,
                    0,
                )
            };
            if attached.is_null() {
                failure.get_or_insert_with(|| {
                    format!(
                        "Failed to watch directory: {} ({})",
                        root.path,
                        std::io::Error::last_os_error()
                    )
                });
                continue;
            }
            let path = root.path.clone();
            self.roots.insert(completion_key, root);
            added.push(completion_key);
            let root = self.roots.get_mut(&completion_key).expect("just inserted");
            if let Err(code) = begin_read(root) {
                failure.get_or_insert_with(|| {
                    format!(
                        "Failed to watch directory: {path} ({})",
                        std::io::Error::from_raw_os_error(code as i32)
                    )
                });
            }
        }
        if let Some(message) = failure {
            for key in added {
                self.let_go(key);
            }
            return Err(message);
        }

        let wanted: Vec<String> = paths.iter().map(|path| coalesce_key(path)).collect();
        let dropped: Vec<usize> = self
            .roots
            .iter()
            .filter(|(_, root)| !root.closing && !wanted.contains(&root.key))
            .map(|(&key, _)| key)
            .collect();
        for key in dropped {
            self.let_go(key);
        }
        // The host may name a folder it already watches in other letters.
        for path in &paths {
            if let Some(key) = self.live_key_of(&coalesce_key(path)) {
                if let Some(root) = self.roots.get_mut(&key) {
                    root.path = path.clone();
                }
            }
        }

        self.coalescer.set_roots(&paths);
        self.emit = Some(emit);
        self.next_place_check = Instant::now() + PLACE_CHECK_EVERY;
        Ok(())
    }

    fn live_key_of(&self, key: &str) -> Option<usize> {
        self.roots
            .iter()
            .find(|(_, root)| !root.closing && root.key == key)
            .map(|(&completion_key, _)| completion_key)
    }

    /// Stops watching a folder. Its buffer stays until the cancelled read
    /// has come back.
    fn let_go(&mut self, key: usize) {
        let Some(root) = self.roots.get_mut(&key) else {
            return;
        };
        if root.reading {
            root.closing = true;
            unsafe {
                CancelIoEx(root.directory.as_raw_handle() as HANDLE, std::ptr::null());
            }
        } else {
            self.roots.remove(&key);
        }
    }

    fn on_read(&mut self, key: usize, ok: bool, code: u32, bytes: u32, now: Instant) {
        let Some(root) = self.roots.get_mut(&key) else {
            return;
        };
        root.reading = false;
        if root.closing {
            self.roots.remove(&key);
            return;
        }

        if ok && bytes > 0 {
            root.extended_proven |= root.extended;
            let filled = (bytes as usize).min(root.buffer.len() * 8);
            // The system wrote `bytes` bytes of records into the buffer.
            let records = parse_records(
                unsafe { std::slice::from_raw_parts(root.buffer.as_ptr().cast::<u8>(), filled) },
                root.extended,
            );
            let changes = changes_of(&root.path, &mut root.held_old_name, records);
            let restarted = begin_read(root).is_ok();
            for change in changes {
                self.coalescer.push(change, now);
            }
            if !restarted {
                self.recover(key, now);
            }
        } else if ok || code == ERROR_NOTIFY_ENUM_DIR {
            // More changed than the buffer holds: the folder is read again.
            root.held_old_name = None;
            let path = root.path.clone();
            let restarted = begin_read(root).is_ok();
            self.coalescer.push(RawChange::Overflow { root: path }, now);
            if !restarted {
                self.recover(key, now);
            }
        } else if code == ERROR_OPERATION_ABORTED {
            // Cancelled, but not by this watcher: ask again.
            if begin_read(root).is_err() {
                self.recover(key, now);
            }
        } else if root.extended && !root.extended_proven {
            // The file system has no extended records: ask in the plain form.
            root.extended = false;
            if start_read(root).is_err() {
                self.recover(key, now);
            }
        } else {
            self.recover(key, now);
        }
    }

    /// A folder's read failed. Opened again, it is still there and changes
    /// may have been missed, so it is to be read again; otherwise it is gone.
    fn recover(&mut self, key: usize, now: Instant) {
        let Some(root) = self.roots.remove(&key) else {
            return;
        };
        let path = root.path.clone();
        let root_key = root.key.clone();
        // No read is outstanding here, so the handle and its buffer can go:
        // a folder that is being deleted disappears once the handle closes.
        drop(root);

        let reopened = open_root(&path, root_key, self.extended_records)
            .ok()
            .and_then(|root| {
                let completion_key = self.next_key;
                self.next_key += 1;
                let attached = unsafe {
                    CreateIoCompletionPort(
                        root.directory.as_raw_handle() as HANDLE,
                        self.shared.port.0,
                        completion_key,
                        0,
                    )
                };
                if attached.is_null() {
                    return None;
                }
                self.roots.insert(completion_key, root);
                let root = self.roots.get_mut(&completion_key).expect("just inserted");
                if begin_read(root).is_err() {
                    self.roots.remove(&completion_key);
                    return None;
                }
                Some(())
            });
        let change = match reopened {
            Some(()) => RawChange::Overflow { root: path },
            None => RawChange::Gone { root: path },
        };
        self.coalescer.push(change, now);
    }

    /// Reports the folders that are no longer at the path they were opened
    /// at: renamed, moved, or being deleted.
    fn check_places(&mut self, now: Instant) {
        let moved: Vec<usize> = self
            .roots
            .iter()
            .filter(|(_, root)| !root.closing && !is_at_its_path(root))
            .map(|(&key, _)| key)
            .collect();
        for key in moved {
            if let Some(root) = self.roots.get(&key) {
                self.coalescer.push(
                    RawChange::Gone {
                        root: root.path.clone(),
                    },
                    now,
                );
            }
            self.let_go(key);
        }
    }

    fn tell(&self, batch: Vec<super::WatchChange>) {
        if let Some(emit) = &self.emit {
            for change in batch {
                emit(change);
            }
        }
    }

    fn shut_down(mut self) {
        let keys: Vec<usize> = self.roots.keys().copied().collect();
        for key in keys {
            self.let_go(key);
        }
        let give_up = Instant::now() + SHUTDOWN_PATIENCE;
        while !self.roots.is_empty() && Instant::now() < give_up {
            if let Completion::Io { key, .. } = self.wait(100) {
                if key != COMMAND_KEY {
                    self.roots.remove(&key);
                }
            }
        }
        // A read that never came back still owns its buffer: better lost
        // than freed under the system's feet.
        for (_, root) in self.roots.drain() {
            Box::leak(root);
        }
    }
}

enum Completion {
    TimedOut,
    PortFailed,
    Io {
        key: usize,
        ok: bool,
        code: u32,
        bytes: u32,
    },
}

fn open_root(path: &str, key: String, extended: bool) -> Result<Box<Root>, String> {
    let validated = validate_existing_path_no_resolve(path)?;
    if !validated.is_dir() {
        return Err("Watch path must be a directory".to_string());
    }
    let directory = std::fs::OpenOptions::new()
        .access_mode(FILE_LIST_DIRECTORY)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OVERLAPPED)
        .open(&validated)
        .map_err(|error| format!("Failed to watch directory: {error}"))?;
    let handle = directory.as_raw_handle() as HANDLE;
    Ok(Box::new(Root {
        path: path.to_string(),
        key,
        opened_as: final_path(handle),
        answers_standard_info: delete_pending(handle).is_some(),
        directory,
        // Zeroed is what an unused OVERLAPPED is.
        overlapped: unsafe { std::mem::zeroed() },
        buffer: vec![0u64; BUFFER_BYTES / 8],
        extended,
        extended_proven: false,
        reading: false,
        closing: false,
        held_old_name: None,
    }))
}

/// Starts the folder's next read, in the plain form when the file system
/// turns the extended one down before it ever answered.
fn begin_read(root: &mut Root) -> Result<(), u32> {
    match start_read(root) {
        Err(_) if root.extended && !root.extended_proven => {
            root.extended = false;
            start_read(root)
        }
        result => result,
    }
}

fn start_read(root: &mut Root) -> Result<(), u32> {
    root.overlapped = unsafe { std::mem::zeroed() };
    let handle = root.directory.as_raw_handle() as HANDLE;
    let buffer = root.buffer.as_mut_ptr().cast::<core::ffi::c_void>();
    let length = (root.buffer.len() * 8) as u32;
    // The buffer and the OVERLAPPED live in the boxed root, which is kept
    // until this read has come back from the port.
    let started = unsafe {
        if root.extended {
            ReadDirectoryChangesExW(
                handle,
                buffer,
                length,
                0,
                NOTIFY_FILTER,
                std::ptr::null_mut(),
                &mut root.overlapped,
                None,
                ReadDirectoryNotifyExtendedInformation,
            )
        } else {
            ReadDirectoryChangesW(
                handle,
                buffer,
                length,
                0,
                NOTIFY_FILTER,
                std::ptr::null_mut(),
                &mut root.overlapped,
                None,
            )
        }
    } != 0;
    if !started {
        let code = unsafe { GetLastError() };
        if code != ERROR_IO_PENDING {
            return Err(code);
        }
    }
    root.reading = true;
    Ok(())
}

/// Whether the folder is still what its path names: not being deleted, and
/// at the path it was opened at (whatever the letter case).
fn is_at_its_path(root: &Root) -> bool {
    let handle = root.directory.as_raw_handle() as HANDLE;
    if root.answers_standard_info && delete_pending(handle) != Some(false) {
        return false;
    }
    match &root.opened_as {
        Some(opened_as) => {
            final_path(handle).is_some_and(|now| now.to_lowercase() == opened_as.to_lowercase())
        }
        None => true,
    }
}

/// Whether the open file is marked for deletion; `None` when it does not say.
fn delete_pending(handle: HANDLE) -> Option<bool> {
    let mut info: FILE_STANDARD_INFO = unsafe { std::mem::zeroed() };
    let answered = unsafe {
        GetFileInformationByHandleEx(
            handle,
            FileStandardInfo,
            (&mut info as *mut FILE_STANDARD_INFO).cast(),
            std::mem::size_of::<FILE_STANDARD_INFO>() as u32,
        )
    } != 0;
    answered.then_some(info.DeletePending)
}

/// The path of the open file as the system spells it now.
fn final_path(handle: HANDLE) -> Option<String> {
    let mut buffer = vec![0u16; 512];
    loop {
        let length = unsafe {
            GetFinalPathNameByHandleW(handle, buffer.as_mut_ptr(), buffer.len() as u32, 0)
        } as usize;
        if length == 0 {
            return None;
        }
        if length < buffer.len() {
            return Some(String::from_utf16_lossy(&buffer[..length]));
        }
        // Too small: `length` is the size it takes, with the terminator.
        buffer = vec![0u16; length + 1];
    }
}

/// One change record of a folder's buffer.
#[derive(Debug, PartialEq)]
struct Record {
    action: u32,
    name: String,
    /// What the extended form says the entry is now.
    facts: Option<WatchedEntryFacts>,
}

/// Reads the records the system wrote into a folder's buffer: the layout of
/// `FILE_NOTIFY_INFORMATION`, or of `FILE_NOTIFY_EXTENDED_INFORMATION` when
/// `extended`. A record that does not fit the buffer ends the reading.
fn parse_records(buffer: &[u8], extended: bool) -> Vec<Record> {
    // Offsets of the name's length and of the name itself.
    let (name_length_at, name_at) = if extended { (80, 84) } else { (8, 12) };
    let u32_at = |bytes: &[u8], at: usize| {
        u32::from_le_bytes([bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]])
    };
    let i64_at = |bytes: &[u8], at: usize| {
        let mut value = [0u8; 8];
        value.copy_from_slice(&bytes[at..at + 8]);
        i64::from_le_bytes(value)
    };

    let mut records = Vec::new();
    let mut offset = 0usize;
    while let Some(header) = offset
        .checked_add(name_at)
        .and_then(|end| buffer.get(offset..end))
    {
        let next = u32_at(header, 0) as usize;
        let name_length = u32_at(header, name_length_at) as usize;
        let Some(name) = (offset + name_at)
            .checked_add(name_length)
            .and_then(|end| buffer.get(offset + name_at..end))
        else {
            break;
        };
        let name: Vec<u16> = name
            .as_chunks::<2>()
            .0
            .iter()
            .map(|pair| u16::from_le_bytes(*pair))
            .collect();
        records.push(Record {
            action: u32_at(header, 4),
            name: String::from_utf16_lossy(&name),
            facts: extended.then(|| WatchedEntryFacts {
                attributes: u32_at(header, 56),
                size: i64_at(header, 48).max(0) as u64,
                modified: system_time(i64_at(header, 16)),
                created: system_time(i64_at(header, 8)),
                accessed: system_time(i64_at(header, 32)),
            }),
        });
        if next == 0 {
            break;
        }
        let Some(following) = offset.checked_add(next) else {
            break;
        };
        offset = following;
    }
    records
}

/// A file time (100 ns units since 1601) as a system time; `None` for the
/// zero the system writes when it has no time.
fn system_time(file_time: i64) -> Option<SystemTime> {
    // From 1601-01-01 to 1970-01-01, in 100 ns units.
    const UNIX_EPOCH_AS_FILE_TIME: i64 = 116_444_736_000_000_000;
    if file_time <= 0 {
        return None;
    }
    let since_unix = file_time - UNIX_EPOCH_AS_FILE_TIME;
    let magnitude = Duration::from_nanos(since_unix.unsigned_abs().saturating_mul(100));
    if since_unix >= 0 {
        UNIX_EPOCH.checked_add(magnitude)
    } else {
        UNIX_EPOCH.checked_sub(magnitude)
    }
}

/// Turns a buffer's records into the changes of the folder at `root`. The
/// two records of a rename become one change; `held_old_name` carries the
/// old name of a rename whose new name is in the next buffer.
fn changes_of(
    root: &str,
    held_old_name: &mut Option<String>,
    records: Vec<Record>,
) -> Vec<RawChange> {
    let mut changes = Vec::with_capacity(records.len());
    // An old name that its new name did not follow: the entry left that name.
    let left = |old: String| RawChange::Entry {
        root: root.to_string(),
        path: old,
        kind: "rename",
        now: EntryNow::Gone,
    };
    for record in records {
        let path = Path::new(root)
            .join(&record.name)
            .to_string_lossy()
            .to_string();
        let now = |path: &str| match &record.facts {
            Some(facts) => match file_entry_from_watched_facts(Path::new(path), facts) {
                Some(entry) => EntryNow::Is(Box::new(entry)),
                // A link or a placeholder: the record does not say what it points to.
                None => EntryNow::Unknown,
            },
            None => EntryNow::Unknown,
        };
        match record.action {
            FILE_ACTION_RENAMED_OLD_NAME => {
                if let Some(old) = held_old_name.replace(path) {
                    changes.push(left(old));
                }
                continue;
            }
            FILE_ACTION_RENAMED_NEW_NAME => {
                let now = now(&path);
                changes.push(match held_old_name.take() {
                    Some(from) => RawChange::Renamed {
                        root: root.to_string(),
                        from,
                        to: path,
                        now,
                    },
                    None => RawChange::Entry {
                        root: root.to_string(),
                        path,
                        kind: "rename",
                        now,
                    },
                });
                continue;
            }
            _ => {}
        }
        if let Some(old) = held_old_name.take() {
            changes.push(left(old));
        }
        let (kind, now) = match record.action {
            FILE_ACTION_ADDED => ("create", now(&path)),
            FILE_ACTION_MODIFIED => ("modify", now(&path)),
            FILE_ACTION_REMOVED => ("remove", EntryNow::Gone),
            _ => continue,
        };
        changes.push(RawChange::Entry {
            root: root.to_string(),
            path,
            kind,
            now,
        });
    }
    changes
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A buffer of records in the system's layout.
    fn buffer_of(records: &[(u32, &str)], extended: bool) -> Vec<u8> {
        let name_at = if extended { 84 } else { 12 };
        let mut buffer = Vec::new();
        for (index, (action, name)) in records.iter().enumerate() {
            let name: Vec<u8> = name.encode_utf16().flat_map(u16::to_le_bytes).collect();
            let unpadded = name_at + name.len();
            let align = if extended { 8 } else { 4 };
            let size = unpadded.div_ceil(align) * align;
            let mut record = vec![0u8; size];
            let next = if index + 1 == records.len() {
                0
            } else {
                size as u32
            };
            record[0..4].copy_from_slice(&next.to_le_bytes());
            record[4..8].copy_from_slice(&action.to_le_bytes());
            if extended {
                // Created, written, changed and read at one known time; 1,234 bytes; archive.
                let time: i64 = 116_444_736_000_000_000 + 1_700_000_000 * 10_000_000;
                for at in [8, 16, 24, 32] {
                    record[at..at + 8].copy_from_slice(&time.to_le_bytes());
                }
                record[48..56].copy_from_slice(&1234i64.to_le_bytes());
                record[56..60].copy_from_slice(&0x20u32.to_le_bytes());
                record[80..84].copy_from_slice(&(name.len() as u32).to_le_bytes());
            } else {
                record[8..12].copy_from_slice(&(name.len() as u32).to_le_bytes());
            }
            record[name_at..unpadded].copy_from_slice(&name);
            buffer.extend_from_slice(&record);
        }
        buffer
    }

    fn names(records: &[Record]) -> Vec<(u32, &str)> {
        records
            .iter()
            .map(|record| (record.action, record.name.as_str()))
            .collect()
    }

    #[test]
    fn the_records_of_a_buffer_are_read_in_both_layouts() {
        let written = [
            (FILE_ACTION_ADDED, "new.txt"),
            (FILE_ACTION_MODIFIED, "r\u{e9}sum\u{e9} \u{1F600}.docx"),
            (FILE_ACTION_REMOVED, "x"),
        ];
        let plain = parse_records(&buffer_of(&written, false), false);
        assert_eq!(names(&plain), written);
        assert!(plain.iter().all(|record| record.facts.is_none()));

        let extended = parse_records(&buffer_of(&written, true), true);
        assert_eq!(names(&extended), written);
        let facts = extended[0]
            .facts
            .expect("the extended form carries the facts");
        assert_eq!(facts.size, 1234);
        assert_eq!(facts.attributes, 0x20);
        let at = UNIX_EPOCH + Duration::from_secs(1_700_000_000);
        assert_eq!(facts.modified, Some(at));
        assert_eq!(facts.created, Some(at));
        assert_eq!(facts.accessed, Some(at));
    }

    #[test]
    fn a_record_that_runs_past_the_buffer_ends_the_reading() {
        let written = [
            (FILE_ACTION_ADDED, "first.txt"),
            (FILE_ACTION_ADDED, "second.txt"),
        ];
        let mut buffer = buffer_of(&written, false);
        // The second record's name is cut off.
        buffer.truncate(buffer.len() - 6);
        assert_eq!(
            names(&parse_records(&buffer, false)),
            [(FILE_ACTION_ADDED, "first.txt")]
        );
        assert!(parse_records(&[], false).is_empty());
        assert!(parse_records(&[0u8; 7], true).is_empty());

        // A next-record offset that points past the end.
        let mut wild = buffer_of(&[(FILE_ACTION_ADDED, "only.txt")], false);
        wild[0..4].copy_from_slice(&u32::MAX.to_le_bytes());
        assert_eq!(
            names(&parse_records(&wild, false)),
            [(FILE_ACTION_ADDED, "only.txt")]
        );
    }

    #[test]
    fn a_file_time_is_a_system_time_and_zero_is_none() {
        assert_eq!(system_time(0), None);
        assert_eq!(system_time(116_444_736_000_000_000), Some(UNIX_EPOCH));
        assert_eq!(
            system_time(116_444_736_000_000_000 + 10_000_000),
            Some(UNIX_EPOCH + Duration::from_secs(1))
        );
        // Before 1970.
        assert_eq!(
            system_time(116_444_736_000_000_000 - 10_000_000),
            Some(UNIX_EPOCH - Duration::from_secs(1))
        );
    }

    fn told(changes: &[RawChange]) -> Vec<String> {
        changes
            .iter()
            .map(|change| match change {
                RawChange::Entry {
                    path, kind, now, ..
                } => format!(
                    "{kind} {path}{}",
                    match now {
                        EntryNow::Gone => " (gone)",
                        EntryNow::Is(_) => " (known)",
                        EntryNow::Unknown => "",
                    }
                ),
                RawChange::Renamed { from, to, .. } => format!("rename {from} -> {to}"),
                RawChange::Overflow { root } => format!("overflow {root}"),
                RawChange::Gone { root } => format!("gone {root}"),
            })
            .collect()
    }

    #[test]
    fn the_two_records_of_a_rename_become_one_change() {
        let records = parse_records(
            &buffer_of(
                &[
                    (FILE_ACTION_ADDED, "new.txt"),
                    (FILE_ACTION_RENAMED_OLD_NAME, "a.txt"),
                    (FILE_ACTION_RENAMED_NEW_NAME, "b.txt"),
                    (FILE_ACTION_REMOVED, "old.txt"),
                ],
                true,
            ),
            true,
        );
        let mut held = None;
        assert_eq!(
            told(&changes_of(r"C:\work", &mut held, records)),
            [
                r"create C:\work\new.txt (known)",
                r"rename C:\work\a.txt -> C:\work\b.txt",
                r"remove C:\work\old.txt (gone)",
            ]
        );
        assert_eq!(held, None);
    }

    #[test]
    fn a_rename_split_over_two_buffers_is_still_one_change() {
        let mut held = None;
        let first = parse_records(
            &buffer_of(&[(FILE_ACTION_RENAMED_OLD_NAME, "a.txt")], false),
            false,
        );
        assert!(changes_of(r"C:\", &mut held, first).is_empty());
        assert_eq!(held.as_deref(), Some(r"C:\a.txt"));
        let second = parse_records(
            &buffer_of(&[(FILE_ACTION_RENAMED_NEW_NAME, "b.txt")], false),
            false,
        );
        assert_eq!(
            told(&changes_of(r"C:\", &mut held, second)),
            [r"rename C:\a.txt -> C:\b.txt"]
        );
    }

    #[test]
    fn half_a_rename_is_told_as_the_half_it_is() {
        let mut held = None;
        // An old name that no new name follows left the folder's names; a
        // new name alone arrived.
        let records = parse_records(
            &buffer_of(
                &[
                    (FILE_ACTION_RENAMED_OLD_NAME, "left.txt"),
                    (FILE_ACTION_MODIFIED, "other.txt"),
                    (FILE_ACTION_RENAMED_NEW_NAME, "arrived.txt"),
                ],
                false,
            ),
            false,
        );
        assert_eq!(
            told(&changes_of(r"C:\work", &mut held, records)),
            [
                r"rename C:\work\left.txt (gone)",
                r"modify C:\work\other.txt",
                r"rename C:\work\arrived.txt",
            ]
        );
    }
}
