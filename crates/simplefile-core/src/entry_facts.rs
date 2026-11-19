//! Lazy column values ("facts") read per file on demand: image dimensions,
//! audio and video duration, a photo's date taken and camera, a PDF's page
//! count. Folder size and item count are facts too, but the service reads
//! those with its folder-metrics walk, not here.

use crate::models::EntryFact;
use std::path::Path;

/// The kinds of fact a lazy column shows. The wire names are what the app
/// asks for and what the cache is keyed by.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum FactKind {
    FolderSize,
    ItemCount,
    Dimensions,
    Duration,
    DateTaken,
    Camera,
    Pages,
}

impl FactKind {
    pub const ALL: [FactKind; 7] = [
        FactKind::FolderSize,
        FactKind::ItemCount,
        FactKind::Dimensions,
        FactKind::Duration,
        FactKind::DateTaken,
        FactKind::Camera,
        FactKind::Pages,
    ];

    pub fn as_wire(self) -> &'static str {
        match self {
            FactKind::FolderSize => "folder_size",
            FactKind::ItemCount => "item_count",
            FactKind::Dimensions => "dimensions",
            FactKind::Duration => "duration",
            FactKind::DateTaken => "date_taken",
            FactKind::Camera => "camera",
            FactKind::Pages => "pages",
        }
    }

    pub fn from_wire(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|kind| kind.as_wire() == name)
    }

    /// Folder size and item count come from one walk of the folder, not from a file read.
    pub fn is_folder_kind(self) -> bool {
        matches!(self, FactKind::FolderSize | FactKind::ItemCount)
    }
}

/// The value is known and `text` shows it.
pub const STATE_READY: &str = "ready";
/// The file has nothing of this kind (a photo without Exif, a silent WAV).
pub const STATE_NONE: &str = "none";
/// A format the service does not read; the app may ask Windows.
pub const STATE_UNSUPPORTED: &str = "unsupported";
/// The file could not be read or parsed.
pub const STATE_FAILED: &str = "failed";

/// PDFs larger than this are not opened for a page count (lopdf reads the whole file).
const MAX_PDF_BYTES: u64 = 32 * 1024 * 1024;

pub fn fact(
    path: &Path,
    kind: FactKind,
    state: &str,
    text: String,
    sort: Option<f64>,
) -> EntryFact {
    EntryFact {
        path: path.to_string_lossy().into_owned(),
        kind: kind.as_wire().to_string(),
        state: state.to_string(),
        text,
        sort,
    }
}

fn ready(path: &Path, kind: FactKind, text: String, sort: Option<f64>) -> EntryFact {
    fact(path, kind, STATE_READY, text, sort)
}

fn settled(path: &Path, kind: FactKind, state: &str) -> EntryFact {
    fact(path, kind, state, String::new(), None)
}

/// Reads one file fact. Never panics and never errors: a file that cannot be
/// read or parsed is `failed`, one with nothing of the kind is `none`, a
/// format the service does not read is `unsupported`.
pub fn read_file_fact(path: &Path, kind: FactKind) -> EntryFact {
    let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| match kind {
        FactKind::FolderSize | FactKind::ItemCount => Outcome::Failed,
        FactKind::Dimensions => dimensions(path),
        FactKind::Duration => duration(path),
        FactKind::DateTaken => exif_date_taken(path),
        FactKind::Camera => exif_camera(path),
        FactKind::Pages => pdf_pages(path),
    }));
    match outcome.unwrap_or(Outcome::Failed) {
        Outcome::Ready(text, sort) => ready(path, kind, text, sort),
        Outcome::None => settled(path, kind, STATE_NONE),
        Outcome::Unsupported => settled(path, kind, STATE_UNSUPPORTED),
        Outcome::Failed => settled(path, kind, STATE_FAILED),
    }
}

enum Outcome {
    Ready(String, Option<f64>),
    None,
    Unsupported,
    Failed,
}

fn extension_of(path: &Path) -> String {
    path.extension()
        .map(|extension| extension.to_string_lossy().to_ascii_lowercase())
        .unwrap_or_default()
}

/// "6000 × 4000", with a multiplication sign.
pub fn format_dimensions(width: u32, height: u32) -> String {
    format!("{width} \u{d7} {height}")
}

/// "1:02:03" with hours, else "0:45"; whole seconds, rounded down.
pub fn format_duration(seconds: f64) -> String {
    let total = seconds.max(0.0).floor() as u64;
    let (hours, minutes, secs) = (total / 3600, (total % 3600) / 60, total % 60);
    if hours > 0 {
        format!("{hours}:{minutes:02}:{secs:02}")
    } else {
        format!("{minutes}:{secs:02}")
    }
}

/// The picture's size from the file header, without decoding it.
fn dimensions(path: &Path) -> Outcome {
    let reader =
        match image::ImageReader::open(path).and_then(image::ImageReader::with_guessed_format) {
            Ok(reader) => reader,
            Err(_) => return Outcome::Failed,
        };
    match reader.into_dimensions() {
        Ok((width, height)) => Outcome::Ready(
            format_dimensions(width, height),
            Some(f64::from(width) * f64::from(height)),
        ),
        Err(_) => Outcome::Failed,
    }
}

const AUDIO_EXTENSIONS: [&str; 11] = [
    "mp3", "flac", "wav", "m4a", "ogg", "oga", "opus", "aac", "wma", "aiff", "aif",
];
const MP4_EXTENSIONS: [&str; 4] = ["mp4", "m4v", "mov", "3gp"];
const MATROSKA_EXTENSIONS: [&str; 2] = ["mkv", "webm"];
/// Video containers the service does not parse: the app asks Windows.
const FALLBACK_VIDEO_EXTENSIONS: [&str; 5] = ["avi", "wmv", "mpg", "mpeg", "asf"];

/// The playing time in seconds, by container: audio through lofty, MP4 and
/// Matroska through their parsers. `Ok(None)` where the file has no duration,
/// `Err` where it could not be read.
fn duration_of(path: &Path) -> Result<Option<f64>, ()> {
    let extension = extension_of(path);
    if AUDIO_EXTENSIONS.contains(&extension.as_str()) {
        return audio_duration(path);
    }
    if MP4_EXTENSIONS.contains(&extension.as_str()) {
        return mp4_duration(path).or_else(|()| audio_duration(path));
    }
    if MATROSKA_EXTENSIONS.contains(&extension.as_str()) {
        return matroska_duration(path);
    }
    Err(())
}

fn duration(path: &Path) -> Outcome {
    let extension = extension_of(path);
    if FALLBACK_VIDEO_EXTENSIONS.contains(&extension.as_str()) {
        return Outcome::Unsupported;
    }
    if !AUDIO_EXTENSIONS.contains(&extension.as_str())
        && !MP4_EXTENSIONS.contains(&extension.as_str())
        && !MATROSKA_EXTENSIONS.contains(&extension.as_str())
    {
        return Outcome::None;
    }
    match duration_of(path) {
        Ok(Some(seconds)) => Outcome::Ready(format_duration(seconds), Some(seconds)),
        Ok(None) => Outcome::None,
        Err(()) => Outcome::Failed,
    }
}

fn audio_duration(path: &Path) -> Result<Option<f64>, ()> {
    use lofty::file::AudioFile;
    let file = lofty::read_from_path(path).map_err(|_| ())?;
    let duration = file.properties().duration();
    Ok((!duration.is_zero()).then_some(duration.as_secs_f64()))
}

fn mp4_duration(path: &Path) -> Result<Option<f64>, ()> {
    let file = std::fs::File::open(path).map_err(|_| ())?;
    let size = file.metadata().map_err(|_| ())?.len();
    let reader =
        mp4::Mp4Reader::read_header(std::io::BufReader::new(file), size).map_err(|_| ())?;
    let duration = reader.duration();
    Ok((!duration.is_zero()).then_some(duration.as_secs_f64()))
}

fn matroska_duration(path: &Path) -> Result<Option<f64>, ()> {
    let file = matroska::open(path).map_err(|_| ())?;
    Ok(file.info.duration.map(|duration| duration.as_secs_f64()))
}

fn pdf_pages(path: &Path) -> Outcome {
    match std::fs::metadata(path) {
        Ok(metadata) if metadata.len() <= MAX_PDF_BYTES => {}
        _ => return Outcome::Failed,
    }
    match lopdf::Document::load(path) {
        Ok(document) => {
            let pages = document.get_pages().len();
            Outcome::Ready(pages.to_string(), Some(pages as f64))
        }
        Err(_) => Outcome::Failed,
    }
}

/// The file's Exif: `Ok(None)` when it has none, `Err` when it cannot be read.
fn read_exif(path: &Path) -> Result<Option<exif::Exif>, ()> {
    let file = std::fs::File::open(path).map_err(|_| ())?;
    let mut reader = std::io::BufReader::new(file);
    match exif::Reader::new().read_from_container(&mut reader) {
        Ok(exif) => Ok(Some(exif)),
        Err(exif::Error::NotFound(_)) => Ok(None),
        Err(_) => Err(()),
    }
}

fn exif_text(exif: &exif::Exif, tag: exif::Tag) -> Option<String> {
    let field = exif.get_field(tag, exif::In::PRIMARY)?;
    let text = match &field.value {
        exif::Value::Ascii(values) => values
            .first()
            .map(|bytes| String::from_utf8_lossy(bytes).into_owned())?,
        other => other.display_as(tag).to_string(),
    };
    let text = text
        .trim_matches(|c: char| c == '\0' || c.is_whitespace())
        .to_string();
    (!text.is_empty()).then_some(text)
}

fn exif_date_taken(path: &Path) -> Outcome {
    let Some(exif) = (match read_exif(path) {
        Ok(exif) => exif,
        Err(()) => return Outcome::Failed,
    }) else {
        return Outcome::None;
    };
    let Some(text) = exif_text(&exif, exif::Tag::DateTimeOriginal)
        .or_else(|| exif_text(&exif, exif::Tag::DateTime))
    else {
        return Outcome::None;
    };
    // Exif writes "YYYY:MM:DD HH:MM:SS" in the camera's local time.
    match chrono::NaiveDateTime::parse_from_str(&text, "%Y:%m:%d %H:%M:%S") {
        Ok(taken) => {
            let sort = chrono::TimeZone::from_local_datetime(&chrono::Local, &taken)
                .single()
                .map(|local| local.timestamp_millis() as f64)
                .or_else(|| Some(taken.and_utc().timestamp_millis() as f64));
            Outcome::Ready(taken.format("%Y-%m-%d %H:%M").to_string(), sort)
        }
        Err(_) => Outcome::Ready(text, None),
    }
}

fn exif_camera(path: &Path) -> Outcome {
    let Some(exif) = (match read_exif(path) {
        Ok(exif) => exif,
        Err(()) => return Outcome::Failed,
    }) else {
        return Outcome::None;
    };
    let make = exif_text(&exif, exif::Tag::Make);
    let model = exif_text(&exif, exif::Tag::Model);
    let camera = match (make, model) {
        (Some(make), Some(model)) if model.to_lowercase().starts_with(&make.to_lowercase()) => {
            model
        }
        (Some(make), Some(model)) => format!("{make} {model}"),
        (Some(one), None) | (None, Some(one)) => one,
        (None, None) => return Outcome::None,
    };
    Outcome::Ready(camera, None)
}

#[cfg(test)]
mod tests {
    use super::*;
    use lopdf::dictionary;
    use std::io::Write;
    use std::path::PathBuf;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "sumafile-facts-{name}-{}-{:?}",
                std::process::id(),
                std::thread::current().id()
            ));
            let _ = std::fs::remove_dir_all(&dir);
            std::fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }

        fn file(&self, name: &str) -> PathBuf {
            self.0.join(name)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn write(path: &Path, bytes: &[u8]) {
        std::fs::File::create(path)
            .unwrap()
            .write_all(bytes)
            .unwrap();
    }

    fn png_bytes(width: u32, height: u32) -> Vec<u8> {
        let image = image::DynamicImage::new_rgb8(width, height);
        let mut buffer = std::io::Cursor::new(Vec::new());
        image
            .write_to(&mut buffer, image::ImageFormat::Png)
            .unwrap();
        buffer.into_inner()
    }

    /// A WAV header for `seconds` of 8 kHz mono 8-bit silence, followed by the samples.
    fn wav_bytes(seconds: u32) -> Vec<u8> {
        let data_len = 8_000 * seconds;
        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&(36 + data_len).to_le_bytes());
        bytes.extend_from_slice(b"WAVEfmt ");
        bytes.extend_from_slice(&16u32.to_le_bytes());
        bytes.extend_from_slice(&1u16.to_le_bytes()); // PCM
        bytes.extend_from_slice(&1u16.to_le_bytes()); // mono
        bytes.extend_from_slice(&8_000u32.to_le_bytes());
        bytes.extend_from_slice(&8_000u32.to_le_bytes());
        bytes.extend_from_slice(&1u16.to_le_bytes());
        bytes.extend_from_slice(&8u16.to_le_bytes());
        bytes.extend_from_slice(b"data");
        bytes.extend_from_slice(&data_len.to_le_bytes());
        bytes.resize(bytes.len() + data_len as usize, 128);
        bytes
    }

    /// An MP4 with a movie header only: `duration` ticks at `timescale`.
    fn mp4_bytes(timescale: u32, duration: u32) -> Vec<u8> {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(&24u32.to_be_bytes());
        bytes.extend_from_slice(b"ftypisom");
        bytes.extend_from_slice(&512u32.to_be_bytes());
        bytes.extend_from_slice(b"isommp41");
        let mut mvhd = Vec::new();
        mvhd.extend_from_slice(&[0, 0, 0, 0]); // version 0, flags
        mvhd.extend_from_slice(&0u32.to_be_bytes()); // creation
        mvhd.extend_from_slice(&0u32.to_be_bytes()); // modification
        mvhd.extend_from_slice(&timescale.to_be_bytes());
        mvhd.extend_from_slice(&duration.to_be_bytes());
        mvhd.extend_from_slice(&0x0001_0000u32.to_be_bytes()); // rate 1.0
        mvhd.extend_from_slice(&0x0100u16.to_be_bytes()); // volume 1.0
        mvhd.extend_from_slice(&[0; 10]); // reserved
        for value in [0x0001_0000u32, 0, 0, 0, 0x0001_0000, 0, 0, 0, 0x4000_0000] {
            mvhd.extend_from_slice(&value.to_be_bytes());
        }
        mvhd.extend_from_slice(&[0; 24]); // pre_defined
        mvhd.extend_from_slice(&1u32.to_be_bytes()); // next track id
        let moov_size = 8 + 8 + mvhd.len() as u32;
        bytes.extend_from_slice(&moov_size.to_be_bytes());
        bytes.extend_from_slice(b"moov");
        bytes.extend_from_slice(&(8 + mvhd.len() as u32).to_be_bytes());
        bytes.extend_from_slice(b"mvhd");
        bytes.extend_from_slice(&mvhd);
        bytes
    }

    /// A Matroska file: EBML header, then a Segment whose Info says `millis` at the default timecode scale.
    fn mkv_bytes(millis: f64) -> Vec<u8> {
        fn element(id: &[u8], payload: &[u8]) -> Vec<u8> {
            assert!(payload.len() < 127);
            let mut out = id.to_vec();
            out.push(0x80 | payload.len() as u8);
            out.extend_from_slice(payload);
            out
        }
        let header = [
            element(&[0x42, 0x86], &[1]),
            element(&[0x42, 0x82], b"matroska"),
            element(&[0x42, 0x87], &[4]),
            element(&[0x42, 0x85], &[2]),
        ]
        .concat();
        let info = [
            element(&[0x2A, 0xD7, 0xB1], &1_000_000u32.to_be_bytes()),
            element(&[0x44, 0x89], &millis.to_be_bytes()),
            element(&[0x4D, 0x80], b"sumafile"),
            element(&[0x57, 0x41], b"sumafile"),
        ]
        .concat();
        let segment_payload = element(&[0x15, 0x49, 0xA9, 0x66], &info);
        [
            element(&[0x1A, 0x45, 0xDF, 0xA3], &header),
            element(&[0x18, 0x53, 0x80, 0x67], &segment_payload),
        ]
        .concat()
    }

    fn pdf_with_pages(path: &Path, pages: usize) {
        let mut document = lopdf::Document::with_version("1.5");
        let pages_id = document.new_object_id();
        let kids: Vec<lopdf::Object> = (0..pages)
            .map(|_| {
                lopdf::Object::Reference(document.add_object(dictionary! {
                    "Type" => lopdf::Object::Name(b"Page".to_vec()),
                    "Parent" => lopdf::Object::Reference(pages_id),
                }))
            })
            .collect();
        document.objects.insert(
            pages_id,
            lopdf::Object::Dictionary(dictionary! {
                "Type" => lopdf::Object::Name(b"Pages".to_vec()),
                "Kids" => lopdf::Object::Array(kids),
                "Count" => lopdf::Object::Integer(pages as i64),
            }),
        );
        let catalog = document.add_object(dictionary! {
            "Type" => lopdf::Object::Name(b"Catalog".to_vec()),
            "Pages" => lopdf::Object::Reference(pages_id),
        });
        document
            .trailer
            .set("Root", lopdf::Object::Reference(catalog));
        document.save(path).unwrap();
    }

    /// A JPEG with Exif: Make and Model in IFD0 and DateTimeOriginal in the Exif IFD (little-endian TIFF).
    fn jpeg_with_exif(make: &str, model: &str, date_time_original: &str) -> Vec<u8> {
        let image = image::DynamicImage::new_rgb8(32, 24);
        let mut buffer = std::io::Cursor::new(Vec::new());
        image
            .write_to(&mut buffer, image::ImageFormat::Jpeg)
            .unwrap();
        let jpeg = buffer.into_inner();

        // IFD0: Make, Model, ExifIFDPointer; Exif IFD: DateTimeOriginal. Strings live in a data area after the IFDs.
        let ascii = |value: &str| {
            let mut bytes = value.as_bytes().to_vec();
            bytes.push(0);
            bytes
        };
        let (make, model, date) = (ascii(make), ascii(model), ascii(date_time_original));
        let ifd0_offset = 8u32;
        let ifd0_len = 2 + 3 * 12 + 4;
        let exif_offset = ifd0_offset + ifd0_len;
        let exif_len = 2 + 12 + 4;
        let mut data_offset = exif_offset + exif_len;
        let mut data = Vec::new();
        let mut place = |bytes: &[u8]| {
            let at = data_offset;
            data.extend_from_slice(bytes);
            data_offset += bytes.len() as u32;
            at
        };
        let make_at = place(&make);
        let model_at = place(&model);
        let date_at = place(&date);
        let entry = |tag: u16, kind: u16, count: u32, value: u32| {
            [
                tag.to_le_bytes().as_slice(),
                &kind.to_le_bytes(),
                &count.to_le_bytes(),
                &value.to_le_bytes(),
            ]
            .concat()
        };
        let mut tiff = b"II\x2a\x00".to_vec();
        tiff.extend_from_slice(&ifd0_offset.to_le_bytes());
        tiff.extend_from_slice(&3u16.to_le_bytes());
        tiff.extend_from_slice(&entry(0x010F, 2, make.len() as u32, make_at));
        tiff.extend_from_slice(&entry(0x0110, 2, model.len() as u32, model_at));
        tiff.extend_from_slice(&entry(0x8769, 4, 1, exif_offset));
        tiff.extend_from_slice(&0u32.to_le_bytes());
        tiff.extend_from_slice(&1u16.to_le_bytes());
        tiff.extend_from_slice(&entry(0x9003, 2, date.len() as u32, date_at));
        tiff.extend_from_slice(&0u32.to_le_bytes());
        tiff.extend_from_slice(&data);

        let mut out = jpeg[..2].to_vec();
        out.extend_from_slice(b"\xff\xe1");
        out.extend_from_slice(&((2 + 6 + tiff.len()) as u16).to_be_bytes());
        out.extend_from_slice(b"Exif\0\0");
        out.extend_from_slice(&tiff);
        out.extend_from_slice(&jpeg[2..]);
        out
    }

    #[test]
    fn dimensions_come_from_the_header_only() {
        let dir = TempDir::new("dimensions");
        let png = png_bytes(4000, 3000);
        let path = dir.file("big.png");
        // Only the signature and the IHDR chunk: the picture itself is missing.
        write(&path, &png[..64]);

        let fact = read_file_fact(&path, FactKind::Dimensions);

        assert_eq!(
            ("ready", "4000 × 3000", Some(12_000_000.0)),
            (fact.state.as_str(), fact.text.as_str(), fact.sort)
        );
        assert_eq!(FactKind::Dimensions.as_wire(), fact.kind);
    }

    #[test]
    fn a_wav_has_a_duration() {
        let dir = TempDir::new("wav");
        let path = dir.file("tone.wav");
        write(&path, &wav_bytes(65));

        let fact = read_file_fact(&path, FactKind::Duration);

        assert_eq!(
            ("ready", "1:05", Some(65.0)),
            (fact.state.as_str(), fact.text.as_str(), fact.sort)
        );
    }

    #[test]
    fn an_mp4_has_a_duration() {
        let dir = TempDir::new("mp4");
        let path = dir.file("clip.mp4");
        write(&path, &mp4_bytes(1_000, 3_723_500));

        let fact = read_file_fact(&path, FactKind::Duration);

        assert_eq!(
            ("ready", "1:02:03"),
            (fact.state.as_str(), fact.text.as_str())
        );
        assert!(
            (fact.sort.unwrap() - 3723.5).abs() < 0.001,
            "{:?}",
            fact.sort
        );
    }

    #[test]
    fn an_mkv_has_a_duration() {
        let dir = TempDir::new("mkv");
        let path = dir.file("clip.mkv");
        write(&path, &mkv_bytes(65_000.0));

        let fact = read_file_fact(&path, FactKind::Duration);

        assert_eq!(
            ("ready", "1:05", Some(65.0)),
            (fact.state.as_str(), fact.text.as_str(), fact.sort)
        );
    }

    #[test]
    fn an_avi_is_unsupported_for_the_app_to_fall_back() {
        let dir = TempDir::new("avi");
        let path = dir.file("old.avi");
        write(&path, b"RIFF\0\0\0\0AVI LIST");

        let fact = read_file_fact(&path, FactKind::Duration);

        assert_eq!(
            ("unsupported", ""),
            (fact.state.as_str(), fact.text.as_str())
        );
    }

    #[test]
    fn a_pdf_counts_its_pages() {
        let dir = TempDir::new("pdf");
        let path = dir.file("paper.pdf");
        pdf_with_pages(&path, 3);

        let fact = read_file_fact(&path, FactKind::Pages);

        assert_eq!(
            ("ready", "3", Some(3.0)),
            (fact.state.as_str(), fact.text.as_str(), fact.sort)
        );
    }

    #[test]
    fn date_taken_and_camera_come_from_exif() {
        let dir = TempDir::new("exif");
        let path = dir.file("photo.jpg");
        write(
            &path,
            &jpeg_with_exif("Canon", "Canon EOS R6", "2026:01:02 03:04:05"),
        );

        let taken = read_file_fact(&path, FactKind::DateTaken);
        let camera = read_file_fact(&path, FactKind::Camera);

        assert_eq!(
            ("ready", "2026-01-02 03:04"),
            (taken.state.as_str(), taken.text.as_str())
        );
        // Unix milliseconds of 2026-01-02T03:04:05, read as a local time, is what the column sorts by.
        assert!(taken.sort.is_some());
        // The model already names the make: no "Canon Canon EOS R6".
        assert_eq!(
            ("ready", "Canon EOS R6"),
            (camera.state.as_str(), camera.text.as_str())
        );
    }

    #[test]
    fn a_photo_without_exif_has_none() {
        let dir = TempDir::new("no-exif");
        let path = dir.file("plain.jpg");
        let image = image::DynamicImage::new_rgb8(8, 8);
        let mut buffer = std::io::Cursor::new(Vec::new());
        image
            .write_to(&mut buffer, image::ImageFormat::Jpeg)
            .unwrap();
        write(&path, &buffer.into_inner());

        assert_eq!("none", read_file_fact(&path, FactKind::DateTaken).state);
        assert_eq!("none", read_file_fact(&path, FactKind::Camera).state);
    }

    #[test]
    fn a_damaged_file_is_failed_not_an_error() {
        let dir = TempDir::new("damaged");
        for (name, kind) in [
            ("x.png", FactKind::Dimensions),
            ("x.mp3", FactKind::Duration),
            ("x.mp4", FactKind::Duration),
            ("x.mkv", FactKind::Duration),
            ("x.pdf", FactKind::Pages),
        ] {
            let path = dir.file(name);
            write(&path, b"not really");
            let fact = read_file_fact(&path, kind);
            assert_eq!(
                ("failed", ""),
                (fact.state.as_str(), fact.text.as_str()),
                "{name}"
            );
        }
        let empty = dir.file("empty.png");
        write(&empty, b"");
        assert_eq!("failed", read_file_fact(&empty, FactKind::Dimensions).state);
        assert_eq!(
            "failed",
            read_file_fact(&dir.file("missing.png"), FactKind::Dimensions).state
        );
    }

    #[test]
    fn folder_kinds_are_not_file_facts() {
        let dir = TempDir::new("folder-kind");
        assert_eq!("failed", read_file_fact(&dir.0, FactKind::FolderSize).state);
    }

    #[test]
    fn durations_format_like_a_clock() {
        assert_eq!("0:00", format_duration(0.0));
        assert_eq!("0:45", format_duration(45.4));
        assert_eq!("1:05", format_duration(65.0));
        assert_eq!("1:02:03", format_duration(3723.0));
        assert_eq!("6000 × 4000", format_dimensions(6000, 4000));
    }

    #[test]
    fn kinds_round_trip_their_wire_names() {
        for kind in FactKind::ALL {
            assert_eq!(Some(kind), FactKind::from_wire(kind.as_wire()));
        }
        assert_eq!(None, FactKind::from_wire("colour"));
    }
}
