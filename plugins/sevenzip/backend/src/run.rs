//! Runs one 7-Zip process: no console window, stdin null, stdout and
//! stderr drained on their own threads, cancel polled every 50 ms and the
//! percentage parsed from `-bsp1` progress output.

use std::ffi::OsString;
use std::io::Read;
use std::path::Path;
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::mpsc;
use std::time::{Duration, Instant};

use sumafile_plugin::CANCELLED_MESSAGE;

/// How often a running process is checked for exit and cancel.
pub const POLL_INTERVAL: Duration = Duration::from_millis(50);

/// The longest a running process goes without a progress report, even
/// when 7-Zip prints nothing: each report resets the caller's idle
/// deadline.
pub const HEARTBEAT_INTERVAL: Duration = Duration::from_secs(5);

/// Failure detail is capped at this many bytes.
const DETAIL_LIMIT: usize = 4000;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

static SPAWNS: AtomicUsize = AtomicUsize::new(0);
static SHUTDOWN: AtomicBool = AtomicBool::new(false);

/// Watches one operation's 7-Zip processes.
pub trait RunObserver {
    /// A process started.
    fn spawned(&mut self, _pid: u32) {}
    /// The bytes the operation's progress counts against: declared sizes
    /// for extract and read, source bytes for create and add, the
    /// archive's declared bytes for rename and delete. Called before the
    /// operation's first `progress`.
    fn total(&mut self, _bytes: u64) {}
    /// The operation is `percent` done; `item` is the entry 7-Zip is on.
    fn progress(&mut self, percent: u8, item: Option<&str>);
    /// Polled every [`POLL_INTERVAL`]; true kills the running process.
    fn is_cancelled(&self) -> bool;
}

/// How many 7-Zip processes this process has started.
pub fn spawn_count() -> usize {
    SPAWNS.load(Ordering::SeqCst)
}

/// Kills every running process and makes every later run fail as
/// cancelled. The backend calls it once its last client has gone, so no
/// 7-Zip process outlives it.
pub fn shutdown() {
    SHUTDOWN.store(true, Ordering::SeqCst);
}

/// [`run_7za_as`] with the action named after the 7-Zip command in
/// `args[0]`.
pub fn run_7za(
    binary: &Path,
    args: &[OsString],
    cwd: Option<&Path>,
    observer: &mut dyn RunObserver,
) -> Result<Vec<u8>, String> {
    let command = args.first().and_then(|arg| arg.to_str()).unwrap_or("");
    run_7za_as(action_for(command), binary, args, cwd, observer)
}

fn action_for(command: &str) -> &'static str {
    match command {
        "l" => "7-Zip list",
        "x" | "e" => "7-Zip extraction",
        "a" => "7-Zip update",
        "d" => "7-Zip delete",
        "rn" => "7-Zip rename",
        _ => "7-Zip",
    }
}

/// Runs `binary` with `args` and returns its stdout. Exit code 0 or 1 is
/// success; anything else fails with `<action> failed: <detail>`. A
/// cancel (or [`shutdown`]) kills the process, waits for it and its drain
/// threads, and fails with [`CANCELLED_MESSAGE`]; a cancel that is already
/// set starts nothing.
///
/// With `-bsp1` among `args`, stdout carries progress: each rise in the
/// percentage is reported, and the last one is repeated at least every
/// [`HEARTBEAT_INTERVAL`]. Progress output is then left out of the
/// failure detail.
pub fn run_7za_as(
    action: &str,
    binary: &Path,
    args: &[OsString],
    cwd: Option<&Path>,
    observer: &mut dyn RunObserver,
) -> Result<Vec<u8>, String> {
    if is_cancelled(observer) {
        return Err(CANCELLED_MESSAGE.to_string());
    }
    let progress_mode = args.iter().any(|arg| arg == "-bsp1");
    let mut command = Command::new(binary);
    command
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(cwd) = cwd {
        command.current_dir(cwd);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    let mut child = command
        .spawn()
        .map_err(|e| format!("{action} failed: cannot start {}: {e}", binary.display()))?;
    SPAWNS.fetch_add(1, Ordering::SeqCst);
    observer.spawned(child.id());

    let (sender, receiver) = mpsc::channel();
    let stdout = child.stdout.take();
    let stderr = child.stderr.take();
    let stdout_thread = std::thread::spawn(move || drain(stdout, progress_mode.then_some(sender)));
    let stderr_thread = std::thread::spawn(move || drain(stderr, None));
    let join = |thread: std::thread::JoinHandle<Vec<u8>>| thread.join().unwrap_or_default();

    let mut progress = Some(receiver);
    let mut percent = 0u8;
    let mut item: Option<String> = None;
    let mut last_report = Instant::now();
    let status = loop {
        let mut risen = false;
        match &progress {
            Some(receiver) => match receiver.recv_timeout(POLL_INTERVAL) {
                Ok(first) => {
                    for (next_percent, next_item) in
                        std::iter::once(first).chain(receiver.try_iter())
                    {
                        if next_item.is_some() {
                            item = next_item;
                        }
                        if next_percent > percent {
                            percent = next_percent;
                            risen = true;
                        }
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => progress = None,
            },
            None => std::thread::sleep(POLL_INTERVAL),
        }
        if risen || last_report.elapsed() >= HEARTBEAT_INTERVAL {
            observer.progress(percent, item.as_deref());
            last_report = Instant::now();
        }
        if is_cancelled(observer) {
            let _ = child.kill();
            let _ = child.wait();
            join(stdout_thread);
            join(stderr_thread);
            return Err(CANCELLED_MESSAGE.to_string());
        }
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) => {}
            Err(e) => {
                let _ = child.kill();
                let _ = child.wait();
                join(stdout_thread);
                join(stderr_thread);
                return Err(format!("{action} failed: {e}"));
            }
        }
    };
    let stdout = join(stdout_thread);
    let stderr = join(stderr_thread);
    let code = status.code().unwrap_or(2);
    if code <= 1 {
        return Ok(stdout);
    }
    let shown_stdout: &[u8] = if progress_mode { b"" } else { &stdout };
    Err(format_failure(action, code, shown_stdout, &stderr))
}

fn is_cancelled(observer: &dyn RunObserver) -> bool {
    SHUTDOWN.load(Ordering::SeqCst) || observer.is_cancelled()
}

/// Reads `reader` to its end. With a `progress` sender, every segment
/// between backspaces, carriage returns and newlines that is a progress
/// line is sent on.
fn drain(
    reader: Option<impl Read>,
    progress: Option<mpsc::Sender<(u8, Option<String>)>>,
) -> Vec<u8> {
    let mut all = Vec::new();
    let Some(mut reader) = reader else {
        return all;
    };
    let mut segment = Vec::new();
    let flush = |segment: &mut Vec<u8>| {
        if let Some(sender) = &progress {
            if let Some(parsed) = parse_progress(&String::from_utf8_lossy(segment)) {
                let _ = sender.send(parsed);
            }
        }
        segment.clear();
    };
    let mut buffer = [0u8; 8192];
    loop {
        let read = match reader.read(&mut buffer) {
            Ok(0) | Err(_) => break,
            Ok(read) => read,
        };
        all.extend_from_slice(&buffer[..read]);
        if progress.is_some() {
            for &byte in &buffer[..read] {
                if matches!(byte, b'\x08' | b'\r' | b'\n') {
                    flush(&mut segment);
                } else {
                    segment.push(byte);
                }
            }
        }
    }
    flush(&mut segment);
    all
}

/// Parses one `-bsp1` progress segment such as ` 45% 3 - folder\a.txt`:
/// the percentage, then an optional file count, an optional one-character
/// operation marker and the current item.
pub fn parse_progress(segment: &str) -> Option<(u8, Option<String>)> {
    let text = segment.trim_matches(|c: char| c == '\x08' || c.is_whitespace());
    let (digits, rest) = text.split_once('%')?;
    if digits.is_empty() || digits.len() > 3 || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let percent: u8 = digits.parse().ok().filter(|percent| *percent <= 100)?;
    let mut rest = rest.trim_start();
    let is_count = |word: &str| !word.is_empty() && word.bytes().all(|b| b.is_ascii_digit());
    match rest.split_once(' ') {
        Some((count, after)) if is_count(count) => rest = after.trim_start(),
        None if is_count(rest) => rest = "",
        _ => {}
    }
    if let Some((marker, after)) = rest.split_once(' ') {
        if matches!(marker, "+" | "-" | "U" | "=" | "D" | "R" | "A" | "T" | ".") {
            rest = after;
        }
    }
    let item = rest.trim();
    Some((percent, (!item.is_empty()).then(|| item.to_string())))
}

/// `<action> failed: <detail>`, where the detail is stderr then stdout
/// without blank lines, capped at 4000 bytes; `<action> failed (exit code
/// N)` when both are empty.
pub fn format_failure(action: &str, code: i32, stdout: &[u8], stderr: &[u8]) -> String {
    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(stderr),
        String::from_utf8_lossy(stdout)
    );
    let mut detail = combined
        .lines()
        .map(str::trim_end)
        .filter(|line| !line.is_empty())
        .collect::<Vec<_>>()
        .join("\n");
    if detail.len() > DETAIL_LIMIT {
        let mut end = DETAIL_LIMIT;
        while !detail.is_char_boundary(end) {
            end -= 1;
        }
        detail.truncate(end);
        detail.push_str("\n...");
    }
    if detail.is_empty() {
        format!("{action} failed (exit code {code})")
    } else {
        format!("{action} failed: {detail}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use std::time::{Duration, Instant};

    use sumafile_plugin::CANCELLED_MESSAGE;

    struct CancelAfter {
        started: Instant,
        after: Duration,
        pid: Option<u32>,
    }

    impl RunObserver for CancelAfter {
        fn spawned(&mut self, pid: u32) {
            self.pid = Some(pid);
        }
        fn progress(&mut self, _percent: u8, _item: Option<&str>) {}
        fn is_cancelled(&self) -> bool {
            self.started.elapsed() >= self.after
        }
    }

    struct Never;

    impl RunObserver for Never {
        fn progress(&mut self, _percent: u8, _item: Option<&str>) {}
        fn is_cancelled(&self) -> bool {
            false
        }
    }

    fn system32(exe: &str) -> PathBuf {
        let root = std::env::var_os("SystemRoot").unwrap_or_else(|| OsString::from(r"C:\Windows"));
        PathBuf::from(root).join("System32").join(exe)
    }

    fn os(args: &[&str]) -> Vec<OsString> {
        args.iter().map(OsString::from).collect()
    }

    fn is_listed(pid: u32) -> bool {
        let output = std::process::Command::new(system32("tasklist.exe"))
            .args(["/FI", &format!("PID eq {pid}"), "/NH", "/FO", "CSV"])
            .output()
            .unwrap();
        String::from_utf8_lossy(&output.stdout).contains(&format!("\"{pid}\""))
    }

    #[test]
    fn cancel_kills_a_running_process() {
        let _serial = crate::test_lock();
        let mut observer = CancelAfter {
            started: Instant::now(),
            after: Duration::from_millis(200),
            pid: None,
        };
        let started = Instant::now();
        let result = run_7za(
            &system32("PING.EXE"),
            &os(&["-n", "30", "127.0.0.1"]),
            None,
            &mut observer,
        );
        assert_eq!(result, Err(CANCELLED_MESSAGE.to_string()));
        assert!(
            started.elapsed() < Duration::from_secs(3),
            "cancel took {:?}",
            started.elapsed()
        );
        let pid = observer.pid.expect("spawned() reports the pid");
        assert!(!is_listed(pid), "PING {pid} is still running");
    }

    #[test]
    fn progress_lines_are_parsed_from_7za_output() {
        assert_eq!(
            parse_progress("\x08\x08 45% 3 - folder\\a.txt"),
            Some((45, Some("folder\\a.txt".to_string())))
        );
        assert_eq!(
            parse_progress(" 18% + big.bin"),
            Some((18, Some("big.bin".to_string())))
        );
        assert_eq!(parse_progress("  0%"), Some((0, None)));
        assert_eq!(
            parse_progress(" 12% 4 U literal [brackets] & spaces.txt"),
            Some((12, Some("literal [brackets] & spaces.txt".to_string())))
        );
        assert_eq!(parse_progress("100%"), Some((100, None)));
        assert_eq!(parse_progress("  0M Scan  R:\\Temp\\"), None);
        assert_eq!(parse_progress("Path = 45% done.txt"), None);
        assert_eq!(parse_progress("    "), None);
    }

    #[test]
    fn exit_code_one_is_success_and_two_is_an_error_with_capped_detail() {
        let _serial = crate::test_lock();
        let cmd = system32("cmd.exe");
        assert!(run_7za(&cmd, &os(&["/c", "exit", "0"]), None, &mut Never).is_ok());
        assert!(run_7za(&cmd, &os(&["/c", "exit", "1"]), None, &mut Never).is_ok());
        let error = run_7za_as(
            "7-Zip test",
            &cmd,
            &os(&["/c", "echo", "boom&exit", "2"]),
            None,
            &mut Never,
        )
        .unwrap_err();
        assert_eq!(error, "7-Zip test failed: boom");
        let error = run_7za_as(
            "7-Zip test",
            &cmd,
            &os(&["/c", "exit", "2"]),
            None,
            &mut Never,
        )
        .unwrap_err();
        assert_eq!(error, "7-Zip test failed (exit code 2)");

        let long = "x".repeat(5000);
        let detail = format_failure("7-Zip list", 2, long.as_bytes(), b"");
        assert_eq!(
            detail,
            format!("7-Zip list failed: {}\n...", "x".repeat(4000))
        );
    }
}
