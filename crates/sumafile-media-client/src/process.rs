//! Starting the media process: piped input and output, no window, a low
//! priority, and on Windows a job object that caps its memory and ends it
//! with the file service.

use std::io;
use std::path::Path;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

pub(crate) struct Spawned {
    pub(crate) child: Child,
    pub(crate) stdin: ChildStdin,
    pub(crate) stdout: ChildStdout,
    /// Held for as long as the process may run: closing it ends the process.
    pub(crate) job: Option<Job>,
}

pub(crate) fn spawn(helper: &Path, memory_limit_bytes: u64) -> io::Result<Spawned> {
    let mut command = Command::new(helper);
    command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    configure(&mut command);

    let mut child = command.spawn()?;
    let job = Job::holding(&child, memory_limit_bytes);
    let pipes = child.stdin.take().zip(child.stdout.take());
    let Some((stdin, stdout)) = pipes else {
        let _ = child.kill();
        let _ = child.wait();
        return Err(io::Error::other("the media process has no pipes"));
    };
    Ok(Spawned {
        child,
        stdin,
        stdout,
        job,
    })
}

#[cfg(windows)]
fn configure(command: &mut Command) {
    use std::os::windows::process::CommandExt;
    use windows_sys::Win32::System::Threading::{BELOW_NORMAL_PRIORITY_CLASS, CREATE_NO_WINDOW};

    command.creation_flags(CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS);
}

#[cfg(not(windows))]
fn configure(_command: &mut Command) {}

/// A Windows job object the media process runs in. Elsewhere it is empty.
pub(crate) struct Job {
    #[cfg(windows)]
    handle: windows_sys::Win32::Foundation::HANDLE,
}

// The handle is only ever closed, once, when the job is dropped.
#[cfg(windows)]
unsafe impl Send for Job {}

impl Job {
    /// A job that holds `child`, limits its memory and ends it when the job
    /// is closed. `None` when the job cannot be made: the process then runs
    /// without the limit, and still ends when its input closes.
    #[cfg(windows)]
    fn holding(child: &Child, memory_limit_bytes: u64) -> Option<Self> {
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::Foundation::CloseHandle;
        use windows_sys::Win32::System::JobObjects::{
            AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
            SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, JOB_OBJECT_LIMIT_PROCESS_MEMORY,
        };

        // SAFETY: a new, unnamed job object with default security.
        let handle = unsafe { CreateJobObjectW(std::ptr::null(), std::ptr::null()) };
        if handle.is_null() {
            return None;
        }
        let job = Self { handle };

        // SAFETY: the structure is plain data; zero is its empty state.
        let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { std::mem::zeroed() };
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if let Ok(limit) = usize::try_from(memory_limit_bytes) {
            if limit > 0 {
                limits.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                limits.ProcessMemoryLimit = limit;
            }
        }
        // SAFETY: `limits` is the structure this information class takes, and
        // both handles are open: the job's is ours, the child's lives in `child`.
        let held = unsafe {
            SetInformationJobObject(
                job.handle,
                JobObjectExtendedLimitInformation,
                std::ptr::from_ref(&limits).cast(),
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            ) != 0
                && AssignProcessToJobObject(job.handle, child.as_raw_handle().cast()) != 0
        };
        if !held {
            // SAFETY: the handle is open and nothing else refers to it. Closing
            // a job the process did not join ends nothing.
            unsafe { CloseHandle(job.handle) };
            std::mem::forget(job);
            return None;
        }
        Some(job)
    }

    #[cfg(not(windows))]
    fn holding(_child: &Child, _memory_limit_bytes: u64) -> Option<Self> {
        None
    }
}

#[cfg(windows)]
impl Drop for Job {
    fn drop(&mut self) {
        // SAFETY: the handle is open and is closed nowhere else.
        unsafe { windows_sys::Win32::Foundation::CloseHandle(self.handle) };
    }
}
