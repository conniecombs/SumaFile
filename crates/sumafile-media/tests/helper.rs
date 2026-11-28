//! The real media process, driven by the file service's client.

use image::{GenericImageView, Rgb, RgbImage};
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};
use sumafile_media_client::protocol::ImageKind;
use sumafile_media_client::{ClientConfig, MediaClient, MediaError};

struct Folder(PathBuf);

impl Folder {
    fn new(name: &str) -> Self {
        let dir = std::env::temp_dir().join(format!(
            "sumafile-media-helper-{name}-{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        Self(dir)
    }

    fn picture(&self, name: &str, width: u32, height: u32) -> PathBuf {
        let path = self.0.join(name);
        RgbImage::from_fn(width, height, |x, y| {
            Rgb([(x * 255 / width) as u8, (y * 255 / height) as u8, 128])
        })
        .save_with_format(&path, image::ImageFormat::Pnm)
        .unwrap();
        path
    }
}

impl Drop for Folder {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn helper() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_sumafile-media"))
}

fn client() -> MediaClient {
    MediaClient::new(ClientConfig::for_helper(helper()))
}

#[test]
fn thumbnail_end_to_end() {
    let folder = Folder::new("thumbnail");
    let client = client();

    let rendered = client
        .thumbnail(&folder.picture("wide.ppm", 400, 200), 64)
        .unwrap();

    assert_eq!(rendered.kind, ImageKind::Jpeg);
    assert_eq!((rendered.width, rendered.height), (64, 32));
    let decoded = image::load_from_memory(&rendered.bytes).unwrap();
    assert_eq!(decoded.dimensions(), (64, 32));
    // Left edge dark red, right edge bright red: the picture, not a blank.
    let (left, right) = (decoded.get_pixel(2, 16).0, decoded.get_pixel(61, 16).0);
    assert!(left[0] < 40 && right[0] > 215, "{left:?} {right:?}");
}

#[test]
fn preview_end_to_end() {
    let folder = Folder::new("preview");
    let client = client();

    let rendered = client
        .preview(&folder.picture("wide.ppm", 400, 200), 100)
        .unwrap();

    assert_eq!((rendered.width, rendered.height), (100, 50));
    assert_eq!(
        image::load_from_memory(&rendered.bytes)
            .unwrap()
            .dimensions(),
        (100, 50)
    );
}

#[test]
fn a_unicode_path_round_trips() {
    let folder = Folder::new("unicode");
    let deep = folder.0.join("été").join("写真 – копия");
    std::fs::create_dir_all(&deep).unwrap();
    let source = folder.picture("plain.ppm", 32, 32);
    let path = deep.join("Übersicht ✓.ppm");
    std::fs::copy(source, &path).unwrap();

    let rendered = client().thumbnail(&path, 32).unwrap();

    assert_eq!((rendered.width, rendered.height), (32, 32));
}

#[test]
fn failures_cross_the_pipe_with_their_kind() {
    let folder = Folder::new("failures");
    let client = client();
    let huge = folder.0.join("huge.ppm");
    std::fs::write(&huge, b"P6\n40000 40000\n255\n\0\0\0").unwrap();
    let wrong = folder.0.join("photo.qoi");
    std::fs::write(&wrong, b"\xff\xd8\xff\xe0not a qoi file").unwrap();
    let unknown = folder.0.join("notes.xyz");
    std::fs::write(&unknown, b"text").unwrap();

    let too_large = client.thumbnail(&huge, 64);
    let decode = client.thumbnail(&wrong, 64);
    let unsupported = client.preview(&unknown, 640);

    assert!(
        matches!(too_large, Err(MediaError::TooLarge)),
        "{too_large:?}"
    );
    assert!(matches!(decode, Err(MediaError::Decode(_))), "{decode:?}");
    assert!(
        matches!(unsupported, Err(MediaError::Unsupported)),
        "{unsupported:?}"
    );
    assert_eq!(
        client.stats().starts,
        1,
        "none of these stopped the process"
    );
    assert!(client.stats().running);
}

#[test]
fn several_requests_at_once_all_come_back() {
    let folder = Folder::new("several");
    let client = std::sync::Arc::new(client());
    let files: Vec<PathBuf> = (0..8)
        .map(|index| folder.picture(&format!("picture-{index}.ppm"), 300 + index * 10, 200))
        .collect();

    let calls: Vec<_> = files
        .into_iter()
        .map(|file| {
            let client = std::sync::Arc::clone(&client);
            std::thread::spawn(move || client.thumbnail(&file, 100))
        })
        .collect();

    for call in calls {
        let rendered = call.join().unwrap().unwrap();
        assert_eq!(rendered.width, 100);
    }
    assert_eq!(client.stats().starts, 1);
}

/// The file service going away closes the pipe; the process must not stay.
#[test]
fn the_helper_exits_when_its_input_closes() {
    let mut child = Command::new(helper())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();

    drop(child.stdin.take());

    let deadline = Instant::now() + Duration::from_secs(10);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() > deadline {
            let _ = child.kill();
            panic!("the media process stayed after its input closed");
        }
        std::thread::sleep(Duration::from_millis(20));
    };
    assert!(status.success());
}
