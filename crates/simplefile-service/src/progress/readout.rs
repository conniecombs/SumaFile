//! What the Transfers window is told while a task runs: bytes done, bytes
//! transferred, the item named and the "Finishing" state.

use super::tuning::{Mode, Policy};
use simplefile_core::models::ProgressUpdate;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};

fn unique_root(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!(
        "sumafile_readout_{name}_{}",
        super::generate_transfer_operation_id()
    ))
}

/// Transfers `source` into `destination` and returns every update the task
/// sent, with what the service answered.
fn transfer(
    source: &Path,
    destination: &Path,
    conflict: &str,
    policy: Policy,
) -> (
    Vec<ProgressUpdate>,
    Result<Vec<super::TransferResult>, super::TransferBatchError>,
) {
    let updates = Mutex::new(Vec::new());
    let result = super::transfer_with_policy_blocking(
        "copy",
        super::Request::Selection {
            sources: vec![source.to_string_lossy().into_owned()],
            conflict_action: conflict.to_string(),
        },
        destination.to_string_lossy().into_owned(),
        super::generate_transfer_operation_id(),
        Arc::new(AtomicBool::new(false)),
        &|update| updates.lock().unwrap().push(update),
        &|_| {},
        &move || policy.clone(),
    );
    (updates.into_inner().unwrap(), result)
}

fn one_at_a_time() -> Policy {
    Policy {
        mode: Mode::Manual,
        manual_concurrency: 1,
        flush_on_close: false,
        ..Policy::default()
    }
}

/// Only bytes written count as transferred: a file skipped because the
/// destination holds it counts toward the bytes done, not toward the bytes
/// the speed is measured on.
#[test]
fn only_bytes_written_count_as_transferred() {
    let root = unique_root("transferred");
    let source = root.join("source").join("album");
    fs::create_dir_all(&source).unwrap();
    fs::write(source.join("kept.bin"), vec![1u8; 300 * 1024]).unwrap();
    fs::write(source.join("new.bin"), vec![2u8; 100 * 1024]).unwrap();
    let destination = root.join("destination");
    fs::create_dir_all(destination.join("album")).unwrap();
    fs::write(destination.join("album").join("kept.bin"), b"theirs").unwrap();

    let (updates, result) = transfer(&source, &destination, "skip", one_at_a_time());

    result.unwrap_or_else(|error| panic!("the copy succeeds: {}", error.message));
    let last = updates.last().expect("updates were sent");
    assert_eq!(last.current, 400 * 1024, "{last:?}");
    assert_eq!(last.transferred, 100 * 1024, "{last:?}");
    let mut written = 0;
    for update in &updates {
        assert!(update.transferred >= written, "transferred went down");
        written = update.transferred;
    }
    let _ = fs::remove_dir_all(&root);
}

/// The bytes done never go down in a pass, also when a file fails after it
/// was written: the "Finishing" update left a failed file's bytes out, and
/// the final one put them back.
#[test]
fn bytes_done_never_go_down_when_a_file_fails() {
    let root = unique_root("failed_bytes");
    let source = root.join("source").join("album");
    fs::create_dir_all(source.join("disc")).unwrap();
    let failing = "fails-readout-failed-bytes.bin";
    super::execute::test_faults::fail_files_named(failing);
    fs::write(source.join(failing), vec![3u8; 512 * 1024]).unwrap();
    fs::write(source.join("disc").join("ok.bin"), vec![4u8; 64 * 1024]).unwrap();
    let destination = root.join("destination");
    fs::create_dir_all(&destination).unwrap();

    let (updates, result) = transfer(&source, &destination, "error", one_at_a_time());

    assert!(result.is_err(), "one file fails");
    let mut done = 0;
    for update in &updates {
        assert!(
            update.current >= done,
            "bytes done went from {done} to {} ({})",
            update.current,
            update.status
        );
        done = update.current;
    }
    let _ = fs::remove_dir_all(&root);
}

/// Once the last file is in its last steps the task says so until it ends:
/// a "running" update sent after it took "Finishing" off the row within a
/// fifth of a second, while the file was still being flushed and renamed.
#[test]
fn the_task_finishes_without_going_back_to_running() {
    let root = unique_root("finishing");
    let source = root.join("source").join("one");
    fs::create_dir_all(&source).unwrap();
    fs::write(source.join("large.bin"), vec![5u8; 24 * 1024 * 1024]).unwrap();
    let destination = root.join("destination");
    fs::create_dir_all(&destination).unwrap();

    let (updates, result) = transfer(
        &source,
        &destination,
        "error",
        Policy {
            flush_on_close: true,
            ..one_at_a_time()
        },
    );

    result.unwrap_or_else(|error| panic!("the copy succeeds: {}", error.message));
    let statuses: Vec<&str> = updates
        .iter()
        .map(|update| update.status.as_str())
        .collect();
    let first = statuses
        .iter()
        .position(|status| *status == "finalizing")
        .expect("the last file's last steps are shown");
    assert!(
        statuses[first..].iter().all(|status| *status != "running"),
        "{:?}",
        &statuses[first..]
    );
    let _ = fs::remove_dir_all(&root);
}

/// With one file at a time, a file that is skipped is named too: such a
/// file reports nothing before it is done, and the row named the last file
/// copied, or no file at all ("Preparing transfer") when none was.
#[test]
fn with_one_file_at_a_time_skipped_files_are_named() {
    let root = unique_root("named_skips");
    let source = root.join("source").join("album");
    fs::create_dir_all(&source).unwrap();
    let destination = root.join("destination");
    fs::create_dir_all(destination.join("album")).unwrap();
    for index in 0..200 {
        let name = format!("photo-{index:03}.jpg");
        fs::write(source.join(&name), b"jpeg").unwrap();
        fs::write(destination.join("album").join(&name), b"theirs").unwrap();
    }

    let (updates, result) = transfer(&source, &destination, "skip", one_at_a_time());

    result.unwrap_or_else(|error| panic!("the copy succeeds: {}", error.message));
    let unnamed = updates
        .iter()
        .filter(|update| update.status == "running" && update.current_files > 0)
        .filter(|update| update.current_item.is_empty())
        .count();
    assert_eq!(unnamed, 0);
    let _ = fs::remove_dir_all(&root);
}
