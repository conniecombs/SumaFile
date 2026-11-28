//! The client against a scripted stand-in for the media process
//! (`sumafile-media-fake-helper`), which acts on the requested file's name.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};
use sumafile_media_client::protocol::ImageKind;
use sumafile_media_client::{ClientConfig, MediaClient, MediaError};

struct Folder(PathBuf);

impl Folder {
    fn new(name: &str) -> Self {
        let dir = std::env::temp_dir().join(format!(
            "sumafile-media-client-{name}-{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    /// A file whose name scripts the fake helper.
    fn file(&self, name: &str) -> PathBuf {
        let path = self.0.join(name);
        std::fs::write(&path, b"picture").unwrap();
        path
    }
}

impl Drop for Folder {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn fake_helper() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_sumafile-media-fake-helper"))
}

fn config() -> ClientConfig {
    let mut config = ClientConfig::for_helper(fake_helper());
    config.thumbnail_timeout = Duration::from_millis(1500);
    config.preview_timeout = Duration::from_millis(1500);
    config
}

fn client(config: ClientConfig) -> Arc<MediaClient> {
    Arc::new(MediaClient::new(config))
}

fn body(path: &Path) -> Vec<u8> {
    format!("OK:{}", path.file_name().unwrap().to_string_lossy()).into_bytes()
}

#[test]
fn a_thumbnail_and_a_preview_come_back() {
    let folder = Folder::new("basic");
    let client = client(config());
    let jpeg = folder.file("ok-a.x");
    let png = folder.file("ok-png.x");

    let thumbnail = client.thumbnail(&jpeg, 256).unwrap();
    let preview = client.preview(&png, 2560).unwrap();

    assert_eq!(thumbnail.kind, ImageKind::Jpeg);
    assert_eq!((thumbnail.width, thumbnail.height), (256, 256));
    assert_eq!(thumbnail.bytes, body(&jpeg));
    assert_eq!(preview.kind, ImageKind::Png);
    assert_eq!((preview.width, preview.height), (2560, 2560));
    assert_eq!(client.stats().starts, 1);
}

#[test]
fn nothing_starts_until_the_first_request() {
    let client = client(config());

    assert_eq!(client.stats().starts, 0);
}

#[test]
fn replies_are_matched_out_of_order() {
    let folder = Folder::new("order");
    let client = client(config());
    let slow = folder.file("slow400-a.x");
    let fast = folder.file("ok-b.x");

    let slow_call = {
        let (client, slow) = (Arc::clone(&client), slow.clone());
        std::thread::spawn(move || (client.thumbnail(&slow, 64), Instant::now()))
    };
    std::thread::sleep(Duration::from_millis(100));
    let fast_reply = client.thumbnail(&fast, 64).unwrap();
    let fast_done = Instant::now();
    let (slow_reply, slow_done) = slow_call.join().unwrap();

    assert_eq!(fast_reply.bytes, body(&fast));
    assert_eq!(slow_reply.unwrap().bytes, body(&slow));
    assert!(fast_done < slow_done, "the fast reply overtook the slow one");
}

/// The timeout is for the decode. Counted from the call, the third of three
/// 300 ms requests through a gate of one would have waited 600 ms and failed.
#[test]
fn calls_beyond_the_gate_wait_without_timing_out() {
    let folder = Folder::new("gate");
    let mut config = config();
    config.max_thumbnails = 1;
    config.thumbnail_timeout = Duration::from_millis(500);
    let client = client(config);
    let files: Vec<PathBuf> = (0..3)
        .map(|index| folder.file(&format!("slow300-{index}.x")))
        .collect();

    let started = Instant::now();
    let calls: Vec<_> = files
        .iter()
        .cloned()
        .map(|file| {
            let client = Arc::clone(&client);
            std::thread::spawn(move || client.thumbnail(&file, 64))
        })
        .collect();
    let replies: Vec<_> = calls.into_iter().map(|call| call.join().unwrap()).collect();

    assert!(replies.iter().all(Result::is_ok), "{replies:?}");
    assert!(started.elapsed() >= Duration::from_millis(900));
    assert_eq!(client.stats().starts, 1);
}

#[test]
fn a_timeout_ends_the_helper_and_records_the_file() {
    let folder = Folder::new("timeout");
    let mut config = config();
    config.thumbnail_timeout = Duration::from_millis(400);
    let client = client(config);
    let hang = folder.file("hang-a.x");
    let fine = folder.file("ok-b.x");

    let started = Instant::now();
    let timed_out = client.thumbnail(&hang, 64);
    let waited = started.elapsed();
    let after = client.thumbnail(&fine, 64);
    let again = client.thumbnail(&hang, 64);

    assert!(matches!(timed_out, Err(MediaError::Failed(_))), "{timed_out:?}");
    assert!(waited >= Duration::from_millis(400) && waited < Duration::from_millis(1400));
    assert_eq!(after.unwrap().bytes, body(&fine));
    assert!(matches!(again, Err(MediaError::Unsupported)), "{again:?}");
    assert_eq!(client.stats().starts, 2, "one restart, and none for the bad file");
}

/// One file that hangs must not cost the others their thumbnails: a request
/// that was in flight when the helper was ended is run again, on its own.
#[test]
fn a_timeout_retries_the_other_requests_alone() {
    let folder = Folder::new("timeout-others");
    let mut config = config();
    config.thumbnail_timeout = Duration::from_millis(800);
    let client = client(config);
    let hang = folder.file("hang-a.x");
    let slow = folder.file("slow600-b.x");

    let hanging = {
        let (client, hang) = (Arc::clone(&client), hang.clone());
        std::thread::spawn(move || client.thumbnail(&hang, 64))
    };
    std::thread::sleep(Duration::from_millis(500));
    // In flight until 1100 ms; the helper is ended at 800 ms.
    let bystander = client.thumbnail(&slow, 64);
    let hanging = hanging.join().unwrap();

    assert!(matches!(hanging, Err(MediaError::Failed(_))), "{hanging:?}");
    assert_eq!(bystander.unwrap().bytes, body(&slow));
    assert_eq!(client.thumbnail(&slow, 64).unwrap().bytes, body(&slow));
    assert!(matches!(client.thumbnail(&hang, 64), Err(MediaError::Unsupported)));
}

#[test]
fn a_crash_with_one_request_records_the_file() {
    let folder = Folder::new("crash-one");
    let client = client(config());
    let crash = folder.file("crash-a.x");

    let crashed = client.thumbnail(&crash, 64);
    let again = client.thumbnail(&crash, 64);

    assert!(matches!(crashed, Err(MediaError::Failed(_))), "{crashed:?}");
    assert!(matches!(again, Err(MediaError::Unsupported)), "{again:?}");
    assert_eq!(client.stats().starts, 1, "a bad file does not start the helper");
}

#[test]
fn a_crash_with_several_requests_retries_each_alone() {
    let folder = Folder::new("crash-several");
    let client = client(config());
    let slow = folder.file("slow600-a.x");
    let crash = folder.file("crash-b.x");

    let bystander = {
        let (client, slow) = (Arc::clone(&client), slow.clone());
        std::thread::spawn(move || client.thumbnail(&slow, 64))
    };
    std::thread::sleep(Duration::from_millis(200));
    let crashed = client.thumbnail(&crash, 64);
    let bystander = bystander.join().unwrap();

    assert_eq!(bystander.unwrap().bytes, body(&slow));
    assert!(matches!(crashed, Err(MediaError::Failed(_))), "{crashed:?}");
    assert!(matches!(client.thumbnail(&crash, 64), Err(MediaError::Unsupported)));
    assert_eq!(client.thumbnail(&slow, 64).unwrap().bytes, body(&slow));
}

#[test]
fn a_bad_file_is_tried_again_after_it_changes() {
    let folder = Folder::new("changed");
    let client = client(config());
    let crash = folder.file("crash-a.x");
    let _ = client.thumbnail(&crash, 64);
    assert!(matches!(client.thumbnail(&crash, 64), Err(MediaError::Unsupported)));
    let starts = client.stats().starts;

    std::fs::write(&crash, b"a different, longer picture").unwrap();
    let retried = client.thumbnail(&crash, 64);

    assert!(matches!(retried, Err(MediaError::Failed(_))), "{retried:?}");
    assert_eq!(client.stats().starts, starts + 1, "the changed file reached the helper");
}

#[test]
fn the_list_of_bad_files_is_bounded() {
    let folder = Folder::new("bounded");
    let mut config = config();
    config.max_bad_files = 2;
    let client = client(config);
    let crashes: Vec<PathBuf> = (0..3)
        .map(|index| folder.file(&format!("crash-{index}.x")))
        .collect();
    for crash in &crashes {
        let _ = client.thumbnail(crash, 64);
    }
    let starts = client.stats().starts;

    let newest = client.thumbnail(&crashes[2], 64);
    let oldest = client.thumbnail(&crashes[0], 64);

    assert!(matches!(newest, Err(MediaError::Unsupported)));
    assert!(matches!(oldest, Err(MediaError::Failed(_))), "forgotten, so tried again");
    assert_eq!(client.stats().starts, starts + 1);
}

#[test]
fn an_idle_helper_is_ended_and_started_again_on_the_next_request() {
    let folder = Folder::new("idle");
    let mut config = config();
    config.idle_exit = Duration::from_millis(300);
    let client = client(config);
    let file = folder.file("ok-a.x");

    client.thumbnail(&file, 64).unwrap();
    std::thread::sleep(Duration::from_millis(1200));
    let running = client.stats().running;
    client.thumbnail(&file, 64).unwrap();

    assert!(!running, "the idle helper was ended");
    assert_eq!(client.stats().starts, 2);
}

#[test]
fn a_busy_helper_is_not_ended_as_idle() {
    let folder = Folder::new("busy");
    let mut config = config();
    config.idle_exit = Duration::from_millis(200);
    let client = client(config);
    let slow = folder.file("slow900-a.x");

    let reply = client.thumbnail(&slow, 64);

    assert_eq!(reply.unwrap().bytes, body(&slow));
    assert_eq!(client.stats().starts, 1);
}

#[test]
fn a_missing_helper_answers_unsupported() {
    let folder = Folder::new("missing");
    let client = client(ClientConfig::for_helper(folder.0.join("no-such-helper")));
    let file = folder.file("ok-a.x");

    let reply = client.thumbnail(&file, 64);

    assert!(matches!(reply, Err(MediaError::Unsupported)), "{reply:?}");
    assert_eq!(client.stats().starts, 0);
}

#[test]
fn a_helper_that_cannot_start_is_not_retried_in_a_loop() {
    let folder = Folder::new("unstartable");
    let not_a_program = folder.file("helper.txt");
    let client = client(ClientConfig::for_helper(not_a_program));
    let file = folder.file("ok-a.x");

    let first = client.thumbnail(&file, 64);
    let second = client.thumbnail(&file, 64);

    assert!(matches!(first, Err(MediaError::Failed(_))), "{first:?}");
    assert!(matches!(second, Err(MediaError::Failed(_))), "{second:?}");
    assert_eq!(client.stats().spawn_failures, 1, "the second call did not try again");
}

#[test]
fn a_failure_kind_maps_to_its_error() {
    let folder = Folder::new("kinds");
    let client = client(config());

    let too_large = client.thumbnail(&folder.file("toolarge.x"), 64);
    let decode = client.thumbnail(&folder.file("undecodable.x"), 64);
    let unsupported = client.thumbnail(&folder.file("unsupported.x"), 64);
    let io = client.preview(&folder.file("ioerror.x"), 64);

    assert!(matches!(too_large, Err(MediaError::TooLarge)), "{too_large:?}");
    assert!(matches!(&decode, Err(MediaError::Decode(message)) if message == "bad data"), "{decode:?}");
    assert!(matches!(unsupported, Err(MediaError::Unsupported)), "{unsupported:?}");
    assert!(matches!(&io, Err(MediaError::Io(message)) if message == "cannot read"), "{io:?}");
    assert_eq!(client.stats().starts, 1, "a failure answer is not a crash");
}

#[test]
fn a_file_that_does_not_exist_is_an_io_error() {
    let folder = Folder::new("absent");
    let client = client(config());

    let reply = client.thumbnail(&folder.0.join("ok-gone.x"), 64);

    assert!(matches!(reply, Err(MediaError::Io(_))), "{reply:?}");
    assert_eq!(client.stats().starts, 0);
}

#[test]
fn dropping_the_client_ends_the_helper() {
    let folder = Folder::new("drop");
    let client = MediaClient::new(config());
    client.thumbnail(&folder.file("ok-a.x"), 64).unwrap();
    let pid = client.stats().process_id.expect("a running helper");

    drop(client);

    // Ended and reaped: no process with that id remains (Linux: no /proc entry).
    #[cfg(target_os = "linux")]
    assert!(!Path::new(&format!("/proc/{pid}")).exists());
    let _ = pid;
}
