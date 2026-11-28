//! The real video process, driven by the file service's client. Windows
//! only: the frames come from Media Foundation.
//!
//! The video is one the tests write themselves (see `support/avi.rs`). It is
//! uncompressed, so nothing here depends on the codecs a PC has, and no PC
//! has a hardware decoder for it: these tests prove the processor path, the
//! seek, and everything around them. The graphics-card path needs a real
//! graphics card and a compressed video.

#![cfg(windows)]

#[path = "support/avi.rs"]
mod avi;

use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};
use sumafile_media_client::protocol::{Decoder, ImageKind, SoftwareReason};
use sumafile_media_client::{ClientConfig, MediaClient, MediaError, VideoRendered};
use sumafile_video::rules::position_for;

const WIDTH: u32 = 96;
const HEIGHT: u32 = 64;
const FRAMES: usize = 60;
const FRAMES_PER_SECOND: u32 = 10;
/// In units of 100 nanoseconds: six seconds, and one frame.
const DURATION: u64 = 60_000_000;
const FRAME_TIME: u64 = 1_000_000;

struct Folder(PathBuf);

impl Folder {
    fn new(name: &str) -> Self {
        let dir =
            std::env::temp_dir().join(format!("sumafile-video-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    fn video(&self, name: &str) -> PathBuf {
        let path = self.0.join(name);
        avi::write(&path, WIDTH, HEIGHT, FRAMES_PER_SECOND, FRAMES).unwrap();
        path
    }

    fn file(&self, name: &str, bytes: &[u8]) -> PathBuf {
        let path = self.0.join(name);
        std::fs::write(&path, bytes).unwrap();
        path
    }
}

impl Drop for Folder {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn program() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_sumafile-video"))
}

fn client() -> MediaClient {
    MediaClient::new(ClientConfig::for_helper(program()))
}

fn picture(thumbnail: &VideoRendered) -> image::RgbImage {
    image::load_from_memory(&thumbnail.rendered.bytes)
        .expect("the thumbnail is a JPEG")
        .to_rgb8()
}

/// The first frame at or past the position the rule gives.
fn frame_at(percent: u8) -> usize {
    position_for(DURATION, percent).div_ceil(FRAME_TIME) as usize
}

#[test]
fn the_frame_at_each_position_is_the_one_asked_for() {
    let folder = Folder::new("positions");
    let video = folder.video("bars.avi");
    let client = client();
    assert_eq!((frame_at(25), frame_at(1), frame_at(99)), (15, 1, 58));

    for percent in [25, 1, 99] {
        let thumbnail = client
            .video_thumbnail(&video, 256, percent, false)
            .unwrap_or_else(|error| panic!("{percent} %: {error:?}"));
        let picture = picture(&thumbnail);

        assert_eq!(thumbnail.rendered.kind, ImageKind::Jpeg);
        assert_eq!(
            (thumbnail.rendered.width, thumbnail.rendered.height),
            (WIDTH, HEIGHT),
            "a small video is not enlarged"
        );
        assert_eq!(picture.dimensions(), (WIDTH, HEIGHT));
        assert!(
            avi::frame_is_upright(&picture),
            "{percent} %: the picture is the right way up"
        );
        assert_eq!(
            avi::frame_index_in(&picture),
            frame_at(percent),
            "{percent} %"
        );
    }
}

#[test]
fn a_thumbnail_smaller_than_the_video_keeps_its_shape() {
    let folder = Folder::new("small");
    let video = folder.video("bars.avi");

    let thumbnail = client().video_thumbnail(&video, 48, 25, false).unwrap();
    let picture = picture(&thumbnail);

    assert_eq!(picture.dimensions(), (48, 32));
    assert!(avi::frame_is_upright(&picture));
    assert_eq!(avi::frame_index_in(&picture), frame_at(25));
}

/// An uncompressed video has no decoder at all, so the graphics card cannot
/// have decoded it, on this PC or any other. The reason says why not.
#[test]
fn with_the_switch_on_and_off_an_uncompressed_video_says_software() {
    let folder = Folder::new("decoder");
    let video = folder.video("bars.avi");
    let client = client();

    let off = client.video_thumbnail(&video, 96, 25, false).unwrap();
    let on = client.video_thumbnail(&video, 96, 25, true).unwrap();

    assert_eq!(off.decoder, Decoder::Software);
    assert_eq!(off.reason, Some(SoftwareReason::TurnedOff));
    assert_eq!(on.decoder, Decoder::Software);
    assert!(
        matches!(
            on.reason,
            Some(SoftwareReason::NoDevice | SoftwareReason::NoHardwareDecoder)
        ),
        "{:?}",
        on.reason
    );
    assert_eq!(avi::frame_index_in(&picture(&on)), frame_at(25));
    assert!(avi::frame_is_upright(&picture(&on)));
}

#[test]
fn a_file_that_is_not_a_video_answers_decode() {
    let folder = Folder::new("not-a-video");
    let notes = folder.file("notes.avi", b"These are notes, not a video.\r\n");

    let reply = client().video_thumbnail(&notes, 96, 25, true);

    assert!(matches!(reply, Err(MediaError::Decode(_))), "{reply:?}");
}

#[test]
fn a_video_cut_inside_its_headers_answers_decode() {
    let folder = Folder::new("cut");
    let whole = avi::bytes(WIDTH, HEIGHT, FRAMES_PER_SECOND, FRAMES);
    let cut = folder.file("cut.avi", &whole[..200]);

    let reply = client().video_thumbnail(&cut, 96, 25, true);

    assert!(matches!(reply, Err(MediaError::Decode(_))), "{reply:?}");
}

/// A download that has not started, or a file still being created.
#[test]
fn an_empty_file_answers_decode() {
    let folder = Folder::new("empty");
    let empty = folder.file("empty.mp4", b"");

    let started = Instant::now();
    let reply = client().video_thumbnail(&empty, 96, 25, true);

    assert!(matches!(reply, Err(MediaError::Decode(_))), "{reply:?}");
    assert!(started.elapsed() < Duration::from_secs(10), "at once");
}

/// One second of silence as a WAV file: sound, and no picture.
fn wav() -> Vec<u8> {
    let samples = vec![0u8; 8000 * 2];
    let mut format = Vec::new();
    format.extend_from_slice(&1u16.to_le_bytes()); // PCM
    format.extend_from_slice(&1u16.to_le_bytes()); // one channel
    format.extend_from_slice(&8000u32.to_le_bytes()); // samples a second
    format.extend_from_slice(&16_000u32.to_le_bytes()); // bytes a second
    format.extend_from_slice(&2u16.to_le_bytes()); // bytes a sample
    format.extend_from_slice(&16u16.to_le_bytes()); // bits a sample
    let mut file = Vec::new();
    file.extend_from_slice(b"RIFF");
    file.extend_from_slice(&(4 + 8 + format.len() as u32 + 8 + samples.len() as u32).to_le_bytes());
    file.extend_from_slice(b"WAVE");
    file.extend_from_slice(b"fmt ");
    file.extend_from_slice(&(format.len() as u32).to_le_bytes());
    file.extend_from_slice(&format);
    file.extend_from_slice(b"data");
    file.extend_from_slice(&(samples.len() as u32).to_le_bytes());
    file.extend_from_slice(&samples);
    file
}

/// A sound file with a video's extension, or a recording whose picture
/// track is missing: there is no frame to show.
#[test]
fn a_file_without_a_video_stream_answers_decode() {
    let folder = Folder::new("sound");
    let sound = folder.file("sound.wav", &wav());

    let reply = client().video_thumbnail(&sound, 96, 25, true);

    assert!(matches!(reply, Err(MediaError::Decode(_))), "{reply:?}");
}

#[test]
fn a_unicode_path_gets_its_thumbnail() {
    let folder = Folder::new("unicode");
    let video = folder.video("vidéo 日本.avi");

    let thumbnail = client().video_thumbnail(&video, 96, 25, false).unwrap();

    assert_eq!(avi::frame_index_in(&picture(&thumbnail)), frame_at(25));
}

#[test]
fn a_missing_file_is_an_io_error() {
    let folder = Folder::new("missing");

    let reply = client().video_thumbnail(&folder.0.join("gone.mp4"), 96, 25, true);

    assert!(matches!(reply, Err(MediaError::Io(_))), "{reply:?}");
}

/// Four thumbnails of one file at once, as a folder of videos asks for them.
#[test]
fn several_thumbnails_at_once_all_come_back() {
    let folder = Folder::new("together");
    let video = folder.video("bars.avi");
    let client = std::sync::Arc::new(client());

    let calls: Vec<_> = [10u8, 30, 50, 70]
        .into_iter()
        .map(|percent| {
            let (client, video) = (std::sync::Arc::clone(&client), video.clone());
            std::thread::spawn(move || (percent, client.video_thumbnail(&video, 96, percent, true)))
        })
        .collect();

    for call in calls {
        let (percent, reply) = call.join().unwrap();
        let thumbnail = reply.unwrap_or_else(|error| panic!("{percent} %: {error:?}"));
        assert_eq!(
            avi::frame_index_in(&picture(&thumbnail)),
            frame_at(percent),
            "{percent} %"
        );
    }
    assert_eq!(client.stats().starts, 1, "one process served them all");
}

#[test]
fn the_program_exits_when_its_input_closes() {
    let mut child = Command::new(program())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    drop(child.stdin.take());

    let deadline = Instant::now() + Duration::from_secs(10);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break Some(status);
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            break None;
        }
        std::thread::sleep(Duration::from_millis(50));
    };

    assert_eq!(status.map(|status| status.success()), Some(true));
}
