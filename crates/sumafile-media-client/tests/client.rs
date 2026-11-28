//! The client against a scripted stand-in for the media process
//! (`sumafile-media-fake-helper`), which acts on the requested file's name.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};
use sumafile_media_client::protocol::{Decoder, ImageKind, SoftwareReason};
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
    assert!(
        fast_done < slow_done,
        "the fast reply overtook the slow one"
    );
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

    assert!(
        matches!(timed_out, Err(MediaError::Failed(_))),
        "{timed_out:?}"
    );
    assert!(waited >= Duration::from_millis(400) && waited < Duration::from_millis(1400));
    assert_eq!(after.unwrap().bytes, body(&fine));
    assert!(matches!(again, Err(MediaError::Unsupported)), "{again:?}");
    assert_eq!(
        client.stats().starts,
        2,
        "one restart, and none for the bad file"
    );
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
    assert!(matches!(
        client.thumbnail(&hang, 64),
        Err(MediaError::Unsupported)
    ));
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
    assert_eq!(
        client.stats().starts,
        1,
        "a bad file does not start the helper"
    );
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
    assert!(matches!(
        client.thumbnail(&crash, 64),
        Err(MediaError::Unsupported)
    ));
    assert_eq!(client.thumbnail(&slow, 64).unwrap().bytes, body(&slow));
}

#[test]
fn a_bad_file_is_tried_again_after_it_changes() {
    let folder = Folder::new("changed");
    let client = client(config());
    let crash = folder.file("crash-a.x");
    let _ = client.thumbnail(&crash, 64);
    assert!(matches!(
        client.thumbnail(&crash, 64),
        Err(MediaError::Unsupported)
    ));
    let starts = client.stats().starts;

    std::fs::write(&crash, b"a different, longer picture").unwrap();
    let retried = client.thumbnail(&crash, 64);

    assert!(matches!(retried, Err(MediaError::Failed(_))), "{retried:?}");
    assert_eq!(
        client.stats().starts,
        starts + 1,
        "the changed file reached the helper"
    );
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
    assert!(
        matches!(oldest, Err(MediaError::Failed(_))),
        "forgotten, so tried again"
    );
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
    assert_eq!(
        client.stats().spawn_failures,
        1,
        "the second call did not try again"
    );
}

/// A media process that cannot run (a missing library, a security tool that
/// ends it) is gone before it reads anything. No file is to blame for that,
/// and starting it again for every file would only repeat it.
#[test]
fn a_helper_that_dies_at_start_blames_no_file_and_is_not_started_in_a_loop() {
    let folder = Folder::new("dies-at-start");
    let broken = folder.file("broken.marker");
    let mut config = config();
    config.helper_args = vec![
        "--die-while-exists".to_string(),
        broken.to_string_lossy().into_owned(),
    ];
    config.spawn_retry_interval = Duration::from_millis(300);
    let client = client(config);
    let first = folder.file("ok-a.x");
    let second = folder.file("ok-b.x");

    let first_reply = client.thumbnail(&first, 64);
    let second_reply = client.thumbnail(&second, 64);

    assert!(
        matches!(first_reply, Err(MediaError::Failed(_))),
        "{first_reply:?}"
    );
    assert!(
        matches!(second_reply, Err(MediaError::Failed(_))),
        "{second_reply:?}"
    );
    let stats = client.stats();
    assert_eq!(
        (stats.starts, stats.spawn_failures),
        (0, 1),
        "one start was tried, it failed, and the second call did not try again"
    );

    // Repaired. After the interval the process starts, and the first file
    // answers: it was never recorded as bad.
    std::fs::remove_file(&broken).unwrap();
    std::thread::sleep(Duration::from_millis(350));
    let repaired = client.thumbnail(&first, 64);

    assert_eq!(repaired.unwrap().bytes, body(&first));
    assert_eq!(client.stats().starts, 1);
}

#[test]
fn a_helper_that_never_says_it_is_running_is_a_failed_start() {
    let folder = Folder::new("mute");
    let mut config = config();
    config.helper_args = vec!["--mute".to_string()];
    config.ready_timeout = Duration::from_millis(300);
    let client = client(config);
    let file = folder.file("ok-a.x");

    let started = Instant::now();
    let reply = client.thumbnail(&file, 64);
    let took = started.elapsed();

    assert!(matches!(reply, Err(MediaError::Failed(_))), "{reply:?}");
    assert!(
        took < Duration::from_millis(1200),
        "the start was given up after its own limit, not the request's 1500 ms: {took:?}"
    );
    let stats = client.stats();
    assert_eq!((stats.starts, stats.spawn_failures), (0, 1));
    assert!(!stats.running, "the silent process was ended");
}

/// A request is written to the process's input pipe. If the process has
/// stopped reading and the pipe is full, that write waits. It must not wait
/// while holding what a timed-out request needs to end the process: then
/// nothing would ever end it. A Linux pipe holds 64 KiB, so twenty requests
/// with paths of about 3,800 bytes fill it.
#[cfg(unix)]
#[test]
fn a_helper_that_stops_reading_cannot_block_the_client() {
    let folder = Folder::new("deaf");
    let mut deep = folder.0.clone();
    for _ in 0..15 {
        deep.push("d".repeat(250));
    }
    std::fs::create_dir_all(&deep).unwrap();
    let mut config = config();
    config.helper_args = vec!["--deaf".to_string()];
    config.thumbnail_timeout = Duration::from_millis(100);
    config.max_thumbnails = 20;
    let client = client(config);

    let (done, results) = std::sync::mpsc::channel();
    for index in 0..20 {
        let path = deep.join(format!("ok-{index}.x"));
        std::fs::write(&path, b"picture").unwrap();
        let (client, done) = (Arc::clone(&client), done.clone());
        std::thread::spawn(move || {
            let _ = done.send(client.thumbnail(&path, 64));
        });
    }
    drop(done);

    let deadline = Instant::now() + Duration::from_secs(20);
    let mut answered = 0;
    while answered < 20 {
        let left = deadline.saturating_duration_since(Instant::now());
        match results.recv_timeout(left) {
            Ok(reply) => {
                assert!(matches!(reply, Err(MediaError::Failed(_))), "{reply:?}");
                answered += 1;
            }
            Err(_) => break,
        }
    }

    assert_eq!(answered, 20, "every call came back");
}

fn video_body(path: &Path, percent: u8) -> Vec<u8> {
    format!(
        "OK:{}@{percent}",
        path.file_name().unwrap().to_string_lossy()
    )
    .into_bytes()
}

#[test]
fn a_video_thumbnail_comes_back_with_who_decoded_it() {
    let folder = Folder::new("video");
    let client = client(config());
    let on_the_card = folder.file("gpu-a.mp4");
    let on_the_processor = folder.file("b.mp4");
    let no_device = folder.file("nodevice-c.mp4");

    let hardware = client.video_thumbnail(&on_the_card, 256, 40, true).unwrap();
    let turned_off = client
        .video_thumbnail(&on_the_processor, 96, 25, false)
        .unwrap();
    let missing = client.video_thumbnail(&no_device, 96, 25, true).unwrap();

    assert_eq!(hardware.decoder, Decoder::Hardware);
    assert_eq!(hardware.reason, None);
    assert_eq!(hardware.rendered.bytes, video_body(&on_the_card, 40));
    assert_eq!(hardware.rendered.kind, ImageKind::Jpeg);
    assert_eq!(
        (hardware.rendered.width, hardware.rendered.height),
        (256, 256)
    );
    assert_eq!(turned_off.decoder, Decoder::Software);
    assert_eq!(turned_off.reason, Some(SoftwareReason::TurnedOff));
    assert_eq!(missing.reason, Some(SoftwareReason::NoDevice));
    assert_eq!(client.stats().starts, 1);
}

/// A slow picture must not hold up a video, nor a video a picture.
#[test]
fn videos_wait_in_a_gate_of_their_own() {
    let folder = Folder::new("video-gate");
    let mut config = config();
    config.max_thumbnails = 1;
    config.max_videos = 1;
    let client = client(config);
    let slow_picture = folder.file("slow600-a.x");
    let video = folder.file("b.mp4");

    let picture_call = {
        let (client, slow_picture) = (Arc::clone(&client), slow_picture.clone());
        std::thread::spawn(move || (client.thumbnail(&slow_picture, 64), Instant::now()))
    };
    std::thread::sleep(Duration::from_millis(100));
    let video_reply = client.video_thumbnail(&video, 64, 25, false);
    let video_done = Instant::now();
    let (picture_reply, picture_done) = picture_call.join().unwrap();

    assert!(video_reply.is_ok(), "{video_reply:?}");
    assert!(picture_reply.is_ok(), "{picture_reply:?}");
    assert!(
        video_done < picture_done,
        "the video did not wait for the picture"
    );
}

#[test]
fn a_video_that_takes_too_long_is_recorded_as_bad() {
    let folder = Folder::new("video-timeout");
    let mut config = config();
    config.video_timeout = Duration::from_millis(300);
    let client = client(config);
    let hanging = folder.file("hang-a.mp4");

    let started = Instant::now();
    let first = client.video_thumbnail(&hanging, 64, 25, true);
    let took = started.elapsed();
    let second = client.video_thumbnail(&hanging, 64, 25, true);

    assert!(matches!(first, Err(MediaError::Failed(_))), "{first:?}");
    assert!(
        took < Duration::from_millis(1200),
        "the video's own limit applied, not the thumbnail's 1500 ms: {took:?}"
    );
    assert!(matches!(second, Err(MediaError::Unsupported)), "{second:?}");
    assert_eq!(client.stats().starts, 1, "the bad file started nothing");
}

/// Video has a program and a client of its own, so that a video which ends
/// its program costs the pictures nothing.
#[test]
fn one_clients_video_timeout_leaves_another_clients_picture_running() {
    let folder = Folder::new("two-clients");
    let mut video_config = config();
    video_config.video_timeout = Duration::from_millis(300);
    let videos = client(video_config);
    let pictures = client(config());
    let hanging = folder.file("hang-a.mp4");
    let slow_picture = folder.file("slow900-b.x");

    let picture_call = {
        let (pictures, slow_picture) = (Arc::clone(&pictures), slow_picture.clone());
        std::thread::spawn(move || pictures.thumbnail(&slow_picture, 64))
    };
    std::thread::sleep(Duration::from_millis(100));
    let video_reply = videos.video_thumbnail(&hanging, 64, 25, true);
    let picture_reply = picture_call.join().unwrap();

    assert!(
        matches!(video_reply, Err(MediaError::Failed(_))),
        "{video_reply:?}"
    );
    assert_eq!(picture_reply.unwrap().bytes, body(&slow_picture));
    assert_eq!(pictures.stats().starts, 1, "the picture program ran on");
}

/// An older media process would answer a video request without saying who
/// decoded the frame. That is not an answer the service can count.
#[test]
fn a_video_reply_that_names_no_decoder_is_a_failure() {
    let folder = Folder::new("video-nameless");
    let client = client(config());
    let nameless = folder.file("nameless-a.mp4");

    let reply = client.video_thumbnail(&nameless, 64, 25, true);

    assert!(matches!(reply, Err(MediaError::Failed(_))), "{reply:?}");
}

#[test]
fn a_failure_kind_maps_to_its_error() {
    let folder = Folder::new("kinds");
    let client = client(config());

    let too_large = client.thumbnail(&folder.file("toolarge.x"), 64);
    let decode = client.thumbnail(&folder.file("undecodable.x"), 64);
    let unsupported = client.thumbnail(&folder.file("unsupported.x"), 64);
    let io = client.preview(&folder.file("ioerror.x"), 64);

    assert!(
        matches!(too_large, Err(MediaError::TooLarge)),
        "{too_large:?}"
    );
    assert!(
        matches!(&decode, Err(MediaError::Decode(message)) if message == "bad data"),
        "{decode:?}"
    );
    assert!(
        matches!(unsupported, Err(MediaError::Unsupported)),
        "{unsupported:?}"
    );
    assert!(
        matches!(&io, Err(MediaError::Io(message)) if message == "cannot read"),
        "{io:?}"
    );
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
