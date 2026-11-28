//! Camera RAW files: the JPEG preview the camera stored in the file.
//!
//! A RAW file holds sensor data, which SumaFile does not develop, and one or
//! more JPEG previews, which the camera made. This reader finds the previews,
//! takes the largest that decodes, and turns it the way the camera was held.
//! That is the picture Explorer shows for these files too.
//!
//! Three containers are read: TIFF (most makers), Fujifilm's RAF header and
//! Canon's CR3. Maker notes are not read, so a format that keeps its only
//! large preview there gives nothing here and falls back to Windows.

mod cr3;
mod raf;
mod tiff_walk;

use super::{open, Decoded};
use crate::limits::check_budget;
use crate::render::{Purpose, RenderError};
use image::metadata::Orientation;
use image::{DynamicImage, GrayImage, ImageFormat, RgbImage};
use std::io::{Cursor, Read, Seek, SeekFrom};
use std::ops::Range;
use std::path::Path;

/// The most one embedded preview may take as a JPEG.
const MAX_PREVIEW_BYTES: u64 = 64 * 1024 * 1024;
/// How many places are looked at for a preview.
const MAX_CANDIDATES: usize = 16;
/// How much of a candidate is read to learn its size: enough for a JPEG's
/// header with a full Exif block and a colour profile.
const SIZE_PROBE_BYTES: u64 = 512 * 1024;
/// A preview for the preview pane must be a real picture, not a thumbnail.
const PANE_MIN_LONG_SIDE: u32 = 1000;
/// A reduced decode keeps this many pixels for each one of the thumbnail.
const REDUCED_DECODE_MARGIN: u32 = 2;

struct Candidate {
    place: Range<u64>,
    width: u32,
    height: u32,
}

pub(super) fn decode(path: &Path, purpose: Purpose) -> Result<Decoded, RenderError> {
    let unreadable = |_error: std::io::Error| nothing_to_show();
    let mut input = open(path)?;
    let length = input.seek(SeekFrom::End(0)).map_err(unreadable)?;
    let mut head = Vec::new();
    input.seek(SeekFrom::Start(0)).map_err(unreadable)?;
    input
        .by_ref()
        .take(raf::HEADER_BYTES as u64)
        .read_to_end(&mut head)
        .map_err(unreadable)?;

    let (places, held) = if head.starts_with(raf::MAGIC) {
        (raf::jpeg_range(&head).into_iter().collect(), None)
    } else if head.get(4..8) == Some(b"ftyp".as_slice()) {
        (cr3::jpeg_ranges(&mut input, length), None)
    } else {
        tiff_walk::walk(&mut input, length)
            .map_or((Vec::new(), None), |found| (found.jpegs, found.orientation))
    };

    let mut candidates: Vec<Candidate> = Vec::new();
    for place in places.into_iter().take(MAX_CANDIDATES) {
        if candidates.iter().any(|known| known.place == place) {
            continue;
        }
        // The start of a JPEG says how large it is. Some files label tens of
        // megabytes of sensor data as a JPEG strip; reading all of it to
        // learn that it is no picture would cost more than the thumbnail.
        let start = place.start..place.end.min(place.start.saturating_add(SIZE_PROBE_BYTES));
        let Some(head) = read(&mut input, &start, length) else {
            continue;
        };
        let size = image::ImageReader::with_format(Cursor::new(&head), ImageFormat::Jpeg)
            .into_dimensions();
        if let Ok((width, height)) = size {
            candidates.push(Candidate {
                place,
                width,
                height,
            });
        }
    }
    candidates.sort_by_key(|candidate| {
        std::cmp::Reverse(u64::from(candidate.width) * u64::from(candidate.height))
    });

    for candidate in candidates {
        let long_side = candidate.width.max(candidate.height);
        let large_enough = match purpose {
            Purpose::Thumbnail { size } => long_side.saturating_mul(2) >= size,
            Purpose::Preview { .. } => long_side >= PANE_MIN_LONG_SIDE,
        };
        if !large_enough || check_budget(candidate.width, candidate.height, 3, 1).is_err() {
            continue;
        }
        let Some(jpeg) = read(&mut input, &candidate.place, length) else {
            continue;
        };
        let Some(mut image) = decode_jpeg(&jpeg, purpose, (candidate.width, candidate.height))
        else {
            continue;
        };

        // The preview's own Exif says how it lies when it has one; else the
        // file's first directory does.
        let orientation = orientation_of(&jpeg)
            .or(held)
            .and_then(|number| u8::try_from(number).ok())
            .and_then(Orientation::from_exif)
            .unwrap_or(Orientation::NoTransforms);
        image.apply_orientation(orientation);
        let turns = matches!(
            orientation,
            Orientation::Rotate90
                | Orientation::Rotate270
                | Orientation::Rotate90FlipH
                | Orientation::Rotate270FlipH
        );
        let full_size = if turns {
            (candidate.height, candidate.width)
        } else {
            (candidate.width, candidate.height)
        };
        return Ok(Decoded { image, full_size });
    }
    Err(nothing_to_show())
}

fn nothing_to_show() -> RenderError {
    RenderError::Decode("The file holds no embedded preview large enough to show.".to_string())
}

/// The bytes at `place`, when it lies inside the file and is not too long.
fn read<R: Read + Seek>(input: &mut R, place: &Range<u64>, length: u64) -> Option<Vec<u8>> {
    let bytes = place.end.checked_sub(place.start)?;
    if place.end > length || !(4..=MAX_PREVIEW_BYTES).contains(&bytes) {
        return None;
    }
    input.seek(SeekFrom::Start(place.start)).ok()?;
    let mut jpeg = vec![0u8; bytes as usize];
    input.read_exact(&mut jpeg).ok()?;
    Some(jpeg)
}

fn orientation_of(jpeg: &[u8]) -> Option<u16> {
    let exif = exif::Reader::new()
        .read_from_container(&mut Cursor::new(jpeg))
        .ok()?;
    let value = exif
        .get_field(exif::Tag::Orientation, exif::In::PRIMARY)?
        .value
        .get_uint(0)?;
    u16::try_from(value).ok()
}

fn decode_jpeg(jpeg: &[u8], purpose: Purpose, size: (u32, u32)) -> Option<DynamicImage> {
    if let Purpose::Thumbnail { size: thumbnail } = purpose {
        if let Some(image) = decode_reduced(jpeg, thumbnail, size) {
            return Some(image);
        }
    }
    image::load_from_memory_with_format(jpeg, ImageFormat::Jpeg).ok()
}

/// The JPEG decoded at a half, a quarter or an eighth of its size, when that
/// still leaves enough for a thumbnail of `thumbnail` pixels. `None` when the
/// whole picture is needed, or for a JPEG the reduced decoder does not take.
fn decode_reduced(jpeg: &[u8], thumbnail: u32, (width, height): (u32, u32)) -> Option<DynamicImage> {
    use jpeg_decoder::PixelFormat;

    let long_side = width.max(height);
    let wanted = thumbnail.saturating_mul(REDUCED_DECODE_MARGIN);
    if wanted >= long_side {
        return None;
    }
    let mut decoder = jpeg_decoder::Decoder::new(Cursor::new(jpeg));
    decoder.read_info().ok()?;
    let format = decoder.info()?.pixel_format;
    if !matches!(format, PixelFormat::RGB24 | PixelFormat::L8) {
        return None;
    }
    let at_least = |side: u32| {
        let scaled = (u64::from(side) * u64::from(wanted)).div_ceil(u64::from(long_side));
        u16::try_from(scaled).unwrap_or(u16::MAX)
    };
    let (reduced_width, reduced_height) = decoder.scale(at_least(width), at_least(height)).ok()?;
    let (reduced_width, reduced_height) = (u32::from(reduced_width), u32::from(reduced_height));
    let pixels = decoder.decode().ok()?;
    match format {
        PixelFormat::RGB24 => {
            RgbImage::from_raw(reduced_width, reduced_height, pixels).map(DynamicImage::ImageRgb8)
        }
        _ => GrayImage::from_raw(reduced_width, reduced_height, pixels)
            .map(DynamicImage::ImageLuma8),
    }
}

#[cfg(test)]
pub(crate) mod testing;

#[cfg(test)]
mod tests {
    use super::testing::{jpeg, Dir, Field, Tiff};
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;
    use image::DynamicImage;

    const PREVIEW: Purpose = Purpose::Preview { max_dimension: 2560 };
    const RED: [u8; 3] = [255, 0, 0];
    const GREEN: [u8; 3] = [0, 255, 0];

    fn is(image: &DynamicImage, colour: [u8; 3]) -> bool {
        let pixel = image.to_rgb8().get_pixel(image.width() / 2, image.height() / 2).0;
        pixel.iter().zip(colour).all(|(have, want)| have.abs_diff(want) < 12)
    }

    fn thumbnail(size: u32) -> Purpose {
        Purpose::Thumbnail { size }
    }

    /// The layout of a Nikon file: a small thumbnail in the second
    /// directory, the preview in a sub-directory of the first.
    fn nikon_like(orientation: u16) -> Vec<u8> {
        Tiff::little_endian().build(&[
            Dir::new(vec![Field::Short(0x0112, orientation), Field::SubDirs(vec![2])]).then(1),
            Dir::new(vec![Field::Jpeg(jpeg(16, 16, GREEN))]),
            Dir::new(vec![Field::Jpeg(jpeg(64, 48, RED))]),
        ])
    }

    #[test]
    fn a_tiff_raw_uses_the_largest_embedded_jpeg() {
        let folder = Folder::new("raw-largest");
        let path = folder.write("photo.nef", &nikon_like(1));

        let decoded = decode(&path, "nef", thumbnail(64)).unwrap();

        assert!(is(&decoded.image, RED), "the 64 by 48 preview, not the 16 by 16 thumbnail");
        assert_eq!(decoded.full_size, (64, 48));
    }

    #[test]
    fn a_raw_preview_is_turned_by_its_orientation() {
        let folder = Folder::new("raw-turned");
        // 6: the camera was held with its right-hand side up.
        let path = folder.write("portrait.nef", &nikon_like(6));

        let decoded = decode(&path, "nef", thumbnail(64)).unwrap();

        assert_eq!(decoded.full_size, (48, 64));
        assert_eq!((decoded.image.width(), decoded.image.height()), (48, 64));
    }

    /// Canon's CR2 keeps its preview as the first directory's one strip.
    #[test]
    fn a_strip_of_jpeg_data_is_found() {
        let folder = Folder::new("raw-strip");
        let file = Tiff::little_endian().build(&[Dir::new(vec![
            Field::Short(0x0103, 6),
            Field::Strip(jpeg(64, 48, RED)),
        ])]);

        let decoded = decode(&folder.write("photo.cr2", &file), "cr2", thumbnail(64)).unwrap();

        assert!(is(&decoded.image, RED));
    }

    /// Pentax keeps its preview in the third directory of the chain.
    #[test]
    fn a_preview_in_the_third_directory_is_found() {
        let folder = Folder::new("raw-third");
        let file = Tiff::big_endian().build(&[
            Dir::new(vec![Field::Short(0x0112, 1)]).then(1),
            Dir::new(vec![Field::Jpeg(jpeg(16, 16, GREEN))]).then(2),
            Dir::new(vec![Field::Jpeg(jpeg(64, 48, RED))]),
        ]);

        let decoded = decode(&folder.write("photo.pef", &file), "pef", thumbnail(64)).unwrap();

        assert!(is(&decoded.image, RED));
    }

    #[test]
    fn a_panasonic_jpg_from_raw_is_found() {
        let folder = Folder::new("raw-rw2");
        let file = Tiff::little_endian().with_magic(0x0055).build(&[Dir::new(vec![
            Field::Blob(0x002E, jpeg(64, 48, RED)),
        ])]);

        let decoded = decode(&folder.write("photo.rw2", &file), "rw2", thumbnail(64)).unwrap();

        assert!(is(&decoded.image, RED));
    }

    #[test]
    fn a_raf_preview_is_found() {
        let folder = Folder::new("raw-raf");
        let picture = jpeg(64, 48, RED);
        let mut file = b"FUJIFILMCCD-RAW 0201FF129502X-T1".to_vec();
        file.resize(84, 0);
        file.extend_from_slice(&148u32.to_be_bytes());
        file.extend_from_slice(&(picture.len() as u32).to_be_bytes());
        file.resize(148, 0);
        file.extend_from_slice(&picture);
        file.extend_from_slice(&[0x55; 64]); // the sensor data that follows

        let decoded = decode(&folder.write("photo.raf", &file), "raf", thumbnail(64)).unwrap();

        assert!(is(&decoded.image, RED));
        assert_eq!(decoded.full_size, (64, 48));
    }

    #[test]
    fn a_cr3_preview_is_found() {
        let folder = Folder::new("raw-cr3");
        let picture = jpeg(64, 48, RED);
        let boxed = |kind: &[u8; 4], payload: &[u8]| {
            let mut bytes = ((payload.len() + 8) as u32).to_be_bytes().to_vec();
            bytes.extend_from_slice(kind);
            bytes.extend_from_slice(payload);
            bytes
        };
        // Canon's preview box: a short header, then the JPEG.
        let mut preview = vec![0, 0, 0, 0, 0, 1, 0, 64, 0, 48, 0, 1];
        preview.extend_from_slice(&(picture.len() as u32).to_be_bytes());
        preview.extend_from_slice(&picture);
        let mut holder = vec![
            0xea, 0xf4, 0x2b, 0x5e, 0x1c, 0x98, 0x4b, 0x88, 0xb9, 0xfb, 0xb7, 0xdc, 0x40, 0x6e,
            0x4d, 0x16,
        ];
        holder.extend_from_slice(&[0, 0, 0, 0, 0, 0, 0, 1]);
        holder.extend_from_slice(&boxed(b"PRVW", &preview));
        let file = [
            boxed(b"ftyp", b"crx \0\0\0\x01crx isom"),
            boxed(b"moov", &boxed(b"free", &[0; 32])),
            boxed(b"uuid", &holder),
            boxed(b"mdat", &[0x55; 256]),
        ]
        .concat();

        let decoded = decode(&folder.write("photo.cr3", &file), "cr3", thumbnail(64)).unwrap();

        assert!(is(&decoded.image, RED));
    }

    /// A small preview is better than an icon, up to a point: it may be half
    /// the size asked for. The preview pane needs a real picture.
    #[test]
    fn a_small_preview_is_refused_for_a_large_thumbnail_and_for_the_preview_pane() {
        let folder = Folder::new("raw-small");
        let path = folder.write("photo.nef", &nikon_like(1));

        let half = decode(&path, "nef", thumbnail(128));
        let too_small = decode(&path, "nef", thumbnail(256)).err();
        let pane = decode(&path, "nef", PREVIEW).err();

        assert!(half.is_ok(), "64 pixels serve a 128-pixel thumbnail");
        assert!(matches!(too_small, Some(RenderError::Decode(_))), "{too_small:?}");
        assert!(matches!(pane, Some(RenderError::Decode(_))), "{pane:?}");
    }

    #[test]
    fn the_preview_pane_gets_a_large_preview_whole() {
        let folder = Folder::new("raw-pane");
        let file = Tiff::little_endian().build(&[Dir::new(vec![Field::Jpeg(jpeg(1200, 800, RED))])]);

        let decoded = decode(&folder.write("photo.arw", &file), "arw", PREVIEW).unwrap();

        assert_eq!((decoded.image.width(), decoded.image.height()), (1200, 800));
    }

    /// A camera's preview is thousands of pixels wide. A thumbnail does not
    /// need them all, and decoding fewer is several times faster.
    #[test]
    fn a_thumbnail_decodes_a_large_preview_at_a_reduced_size() {
        let folder = Folder::new("raw-reduced");
        let file = Tiff::little_endian().build(&[Dir::new(vec![Field::Jpeg(jpeg(1600, 1200, RED))])]);

        let decoded = decode(&folder.write("photo.dng", &file), "dng", thumbnail(100)).unwrap();

        assert_eq!(decoded.full_size, (1600, 1200), "the preview's own size, for the shape");
        assert!(decoded.image.width() < 1600 && decoded.image.width() >= 200);
        assert!(is(&decoded.image, RED));
    }

    #[test]
    fn a_candidate_that_is_not_a_picture_is_passed_over() {
        let folder = Folder::new("raw-garbage");
        // Lossless sensor data is stored as one "JPEG" strip in some files.
        let file = Tiff::little_endian().build(&[
            Dir::new(vec![Field::Short(0x0103, 7), Field::Strip(vec![0xAB; 4096])]).then(1),
            Dir::new(vec![Field::Jpeg(jpeg(64, 48, RED))]),
        ]);

        let decoded = decode(&folder.write("photo.dng", &file), "dng", thumbnail(64)).unwrap();

        assert!(is(&decoded.image, RED));
    }

    #[test]
    fn a_file_without_a_usable_preview_answers_decode() {
        let folder = Folder::new("raw-none");
        let no_preview = Tiff::little_endian().build(&[Dir::new(vec![Field::Short(0x0112, 1)])]);
        let outside = Tiff::little_endian().build(&[Dir::new(vec![
            Field::Long(0x0201, 0x00FF_FFF0),
            Field::Long(0x0202, 4096),
        ])]);
        let looping = Tiff::little_endian().build(&[Dir::new(vec![Field::Short(0x0112, 1)]).then(0)]);
        let cases = [
            ("none.nef", no_preview),
            ("outside.nef", outside),
            ("loop.nef", looping),
            ("text.nef", b"no camera made this file".to_vec()),
            ("short.nef", b"II*\0".to_vec()),
            ("fuji.raf", b"FUJIFILMCCD-RAW ".to_vec()),
        ];

        for (name, bytes) in cases {
            let extension = name.rsplit('.').next().unwrap();
            let error = decode(&folder.write(name, &bytes), extension, thumbnail(64)).err();
            assert!(matches!(error, Some(RenderError::Decode(_))), "{name}: {error:?}");
        }
    }
}
