//! Running one git process: no window, no prompt, output drained on threads,
//! and a timeout and a cancel check that end git together with every process
//! it started.
//!
//! On Windows each process starts through [`spawn_in_job`]: it is created
//! suspended, assigned to a kill-on-close job of its own and only then
//! resumed, so nothing git starts can run outside the job. Closing the job on
//! timeout or cancel ends git and all of its children, including the real
//! `git.exe` behind Git for Windows' `cmd\git.exe` launcher and the remote
//! helpers. Closing it after a normal exit ends anything git left behind.
//!
//! Off Windows (development machines only) git leads a process group of its
//! own, and timeout and cancel kill the whole group.

use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::Arc;
use std::time::{Duration, Instant};

use crate::models::GitCommandResult;
use crate::GIT_CANCELLED;

/// True once the caller wants the running command stopped.
pub type CancelCheck = Arc<dyn Fn() -> bool + Send + Sync>;

/// Local git invocations never wait longer than this: a repository on a
/// disconnected share must not freeze the request.
pub const GIT_LOCAL_TIMEOUT: Duration = Duration::from_secs(60);

/// Fetch, pull and push talk to a remote and may legitimately take minutes.
pub const GIT_REMOTE_TIMEOUT: Duration = Duration::from_secs(600);

/// How often the wait loop looks at the process, the clock and the cancel
/// check; a cancel is seen within this.
const POLL_INTERVAL: Duration = Duration::from_millis(15);

pub struct RunOptions {
    /// `<name>` in `git <name>`, the result's command and the timeout text.
    pub name: String,
    pub timeout: Duration,
    pub cancel: Option<CancelCheck>,
}

impl RunOptions {
    /// A local command: [`GIT_LOCAL_TIMEOUT`], no cancel.
    pub fn local(name: &str) -> RunOptions {
        RunOptions {
            name: name.to_string(),
            timeout: GIT_LOCAL_TIMEOUT,
            cancel: None,
        }
    }

    fn is_cancelled(&self) -> bool {
        self.cancel.as_ref().is_some_and(|cancel| cancel())
    }
}

/// Runs `command` to completion and returns its output.
///
/// Fails with `git <name> timed out after <n> seconds` once `timeout`
/// passes, with [`GIT_CANCELLED`] once the cancel check returns true (it is
/// asked before the spawn and every 15 ms after it), and otherwise, for a
/// non-zero exit, with git's stderr, its stdout or `git <name> exited with
/// code <n>`, the first that is not empty.
pub fn run_command(
    mut command: Command,
    options: &RunOptions,
    summary: &str,
) -> Result<GitCommandResult, String> {
    if options.is_cancelled() {
        return Err(GIT_CANCELLED.to_string());
    }
    command
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GCM_INTERACTIVE", "Never")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let (mut child, tree) = spawn_tree(&mut command).map_err(|error| error.to_string())?;
    let stdout_reader = child.stdout.take().map(drain_in_background);
    let stderr_reader = child.stderr.take().map(drain_in_background);

    let started = Instant::now();
    let status: ExitStatus = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) if options.is_cancelled() => {
                tree.kill(&mut child);
                return Err(GIT_CANCELLED.to_string());
            }
            Ok(None) if started.elapsed() >= options.timeout => {
                tree.kill(&mut child);
                return Err(format!(
                    "git {} timed out after {} seconds",
                    options.name,
                    options.timeout.as_secs()
                ));
            }
            Ok(None) => std::thread::sleep(POLL_INTERVAL),
            Err(error) => {
                tree.kill(&mut child);
                return Err(error.to_string());
            }
        }
    };
    // Ends whatever git left running, so no leftover child keeps the pipes
    // open while the readers are joined.
    tree.finish();
    let stdout = join_reader(stdout_reader);
    let stderr = join_reader(stderr_reader);
    let exit_code = status.code().unwrap_or(-1);
    if status.success() {
        Ok(GitCommandResult {
            command: format!("git {}", options.name),
            exit_code,
            stdout,
            stderr,
            summary: summary.to_string(),
        })
    } else {
        Err(first_non_empty([
            stderr.trim(),
            stdout.trim(),
            &format!("git {} exited with code {exit_code}", options.name),
        ]))
    }
}

fn drain_in_background<R: std::io::Read + Send + 'static>(
    mut reader: R,
) -> std::thread::JoinHandle<Vec<u8>> {
    std::thread::spawn(move || {
        let mut buffer = Vec::new();
        let _ = reader.read_to_end(&mut buffer);
        buffer
    })
}

fn join_reader(reader: Option<std::thread::JoinHandle<Vec<u8>>>) -> String {
    let bytes = reader
        .map(|reader| reader.join().unwrap_or_default())
        .unwrap_or_default();
    String::from_utf8_lossy(&bytes).to_string()
}

fn first_non_empty(values: [&str; 3]) -> String {
    values
        .iter()
        .find(|value| !value.trim().is_empty())
        .map(|value| value.to_string())
        .unwrap_or_else(|| "Git command failed.".to_string())
}

/// What ends a process and everything it started.
struct ProcessTree {
    #[cfg(windows)]
    job: KillOnCloseJob,
    #[cfg(unix)]
    group: i32,
}

impl ProcessTree {
    /// After a normal exit: on Windows, closing the job ends anything the
    /// process left running. Elsewhere leftovers are left alone, since the
    /// reaped leader's group id may already be reused.
    fn finish(self) {
        #[cfg(windows)]
        drop(self.job);
    }

    /// Ends the process and its descendants, then reaps the process.
    fn kill(self, child: &mut Child) {
        #[cfg(windows)]
        drop(self.job);
        #[cfg(unix)]
        // SAFETY: plain syscall. The group id is the child's pid, which stays
        // reserved until the `wait` below reaps it, so no other group can
        // hold it.
        unsafe {
            libc::kill(-self.group, libc::SIGKILL);
        }
        let _ = child.kill();
        let _ = child.wait();
    }
}

#[cfg(windows)]
fn spawn_tree(command: &mut Command) -> std::io::Result<(Child, ProcessTree)> {
    let (child, job) = spawn_in_job(command)?;
    Ok((child, ProcessTree { job }))
}

#[cfg(unix)]
fn spawn_tree(command: &mut Command) -> std::io::Result<(Child, ProcessTree)> {
    use std::os::unix::process::CommandExt;
    command.process_group(0);
    let child = command.spawn()?;
    let group = i32::try_from(child.id()).map_err(std::io::Error::other)?;
    Ok((child, ProcessTree { group }))
}

#[cfg(not(any(windows, unix)))]
fn spawn_tree(command: &mut Command) -> std::io::Result<(Child, ProcessTree)> {
    Ok((command.spawn()?, ProcessTree {}))
}

#[cfg(windows)]
pub use job::{spawn_in_job, KillOnCloseJob};

#[cfg(windows)]
mod job {
    use std::os::windows::io::AsRawHandle;
    use std::os::windows::process::CommandExt;
    use std::process::{Child, Command};

    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Thread32First, Thread32Next, TH32CS_SNAPTHREAD, THREADENTRY32,
    };
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
        SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };
    use windows_sys::Win32::System::Threading::{
        OpenThread, ResumeThread, CREATE_NO_WINDOW, CREATE_SUSPENDED, THREAD_SUSPEND_RESUME,
    };

    /// Owns a job object created with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.
    /// Dropping it closes the job, which ends every process in it.
    pub struct KillOnCloseJob(pub(crate) HANDLE);

    // SAFETY: a job handle is a kernel handle, usable from any thread.
    unsafe impl Send for KillOnCloseJob {}

    impl KillOnCloseJob {
        fn new() -> std::io::Result<KillOnCloseJob> {
            // SAFETY: no attributes and no name; the handle is owned below.
            let handle = unsafe { CreateJobObjectW(std::ptr::null(), std::ptr::null()) };
            if handle.is_null() {
                return Err(std::io::Error::last_os_error());
            }
            let job = KillOnCloseJob(handle);
            // SAFETY: an all-zero JOBOBJECT_EXTENDED_LIMIT_INFORMATION is valid.
            let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { std::mem::zeroed() };
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            // SAFETY: the pointer and size describe `limits`, which outlives the call.
            let set = unsafe {
                SetInformationJobObject(
                    job.0,
                    JobObjectExtendedLimitInformation,
                    std::ptr::from_ref(&limits).cast(),
                    std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
                )
            };
            if set == 0 {
                return Err(std::io::Error::last_os_error());
            }
            Ok(job)
        }
    }

    impl Drop for KillOnCloseJob {
        fn drop(&mut self) {
            // SAFETY: the handle is owned and closed once.
            unsafe {
                CloseHandle(self.0);
            }
        }
    }

    /// Starts `command` with `CREATE_NO_WINDOW | CREATE_SUSPENDED`, assigns
    /// it to a new kill-on-close job and only then resumes its one thread,
    /// so the process and everything it starts run inside the job. A failed
    /// job creation, assignment or resume kills the child and returns the
    /// error.
    pub fn spawn_in_job(command: &mut Command) -> std::io::Result<(Child, KillOnCloseJob)> {
        command.creation_flags(CREATE_NO_WINDOW | CREATE_SUSPENDED);
        let mut child = command.spawn()?;
        let assigned = KillOnCloseJob::new().and_then(|job| {
            // SAFETY: both handles are valid for the duration of the call.
            let ok = unsafe { AssignProcessToJobObject(job.0, child.as_raw_handle() as HANDLE) };
            if ok == 0 {
                return Err(std::io::Error::last_os_error());
            }
            resume_main_thread(child.id())?;
            Ok(job)
        });
        match assigned {
            Ok(job) => Ok((child, job)),
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                Err(error)
            }
        }
    }

    /// Resumes the thread of a process created suspended. Such a process has
    /// exactly one thread.
    fn resume_main_thread(process_id: u32) -> std::io::Result<()> {
        // SAFETY: a thread snapshot of the whole system; closed below.
        let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) };
        if snapshot == INVALID_HANDLE_VALUE || snapshot.is_null() {
            return Err(std::io::Error::last_os_error());
        }
        let result = (|| {
            // SAFETY: an all-zero THREADENTRY32 with dwSize set is valid input.
            let mut entry: THREADENTRY32 = unsafe { std::mem::zeroed() };
            entry.dwSize = std::mem::size_of::<THREADENTRY32>() as u32;
            // SAFETY: `entry` is a valid, sized THREADENTRY32.
            let mut more = unsafe { Thread32First(snapshot, &mut entry) } != 0;
            while more {
                if entry.th32OwnerProcessID == process_id {
                    // SAFETY: the thread id comes from the snapshot.
                    let thread =
                        unsafe { OpenThread(THREAD_SUSPEND_RESUME, 0, entry.th32ThreadID) };
                    if thread.is_null() {
                        return Err(std::io::Error::last_os_error());
                    }
                    // SAFETY: `thread` was opened with THREAD_SUSPEND_RESUME.
                    let resumed = unsafe { ResumeThread(thread) };
                    let error = std::io::Error::last_os_error();
                    // SAFETY: `thread` is owned and closed once.
                    unsafe {
                        CloseHandle(thread);
                    }
                    return if resumed == u32::MAX {
                        Err(error)
                    } else {
                        Ok(())
                    };
                }
                // SAFETY: as for Thread32First.
                more = unsafe { Thread32Next(snapshot, &mut entry) } != 0;
            }
            Err(std::io::Error::other(format!(
                "no thread of process {process_id} to resume"
            )))
        })();
        // SAFETY: the snapshot handle is owned and closed once.
        unsafe {
            CloseHandle(snapshot);
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::{run_command, RunOptions};
    use std::process::Command;
    use std::time::{Duration, Instant};

    #[test]
    fn hung_git_process_is_killed_after_the_timeout() {
        #[cfg(windows)]
        let command = {
            let mut command = Command::new("powershell");
            command.args(["-NoProfile", "-Command", "Start-Sleep -Seconds 20"]);
            command
        };
        #[cfg(not(windows))]
        let command = {
            let mut command = Command::new("sleep");
            command.arg("20");
            command
        };
        let started = Instant::now();
        let error = run_command(
            command,
            &RunOptions {
                name: "sleep".to_string(),
                timeout: Duration::from_millis(300),
                cancel: None,
            },
            "unused",
        )
        .expect_err("a process that never exits must time out");
        assert!(error.contains("timed out"), "{error}");
        assert!(started.elapsed() < Duration::from_secs(5));
    }

    #[cfg(windows)]
    #[test]
    fn spawn_in_job_assigns_the_job_before_the_process_runs() {
        use std::os::windows::io::AsRawHandle;
        use std::process::Stdio;
        use windows_sys::Win32::Foundation::HANDLE;
        use windows_sys::Win32::System::JobObjects::IsProcessInJob;

        let mut ping = Command::new("cmd");
        ping.args(["/d", "/c", "ping", "-n", "30", "127.0.0.1"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null());
        let (mut child, job) = super::spawn_in_job(&mut ping).unwrap();
        let mut in_job = 0;
        // SAFETY: both handles are valid; `in_job` receives the answer.
        let asked = unsafe { IsProcessInJob(child.as_raw_handle() as HANDLE, job.0, &mut in_job) };
        assert_ne!(asked, 0, "{}", std::io::Error::last_os_error());
        assert_ne!(in_job, 0, "the child is not in its job");

        drop(job);
        let deadline = Instant::now() + Duration::from_secs(5);
        let exited = loop {
            if let Some(status) = child.try_wait().unwrap() {
                break Some(status);
            }
            if Instant::now() >= deadline {
                break None;
            }
            std::thread::sleep(Duration::from_millis(15));
        };
        if exited.is_none() {
            let _ = child.kill();
            panic!("closing the job did not end the child within 5 s");
        }

        let mut exit = Command::new("cmd");
        exit.args(["/d", "/c", "exit 7"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null());
        let (mut child, job) = super::spawn_in_job(&mut exit).unwrap();
        let status = child.wait().unwrap();
        drop(job);
        assert_eq!(status.code(), Some(7));
    }
}
