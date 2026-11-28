//! Renders a thumbnail: decode an image file, scale it to fit a square box,
//! and encode the result as JPEG.
//!
//! Decoding is most of the cost for a photo, so a JPEG is decoded straight
//! to a reduced size (a half, a quarter or an eighth) when the thumbnail is
//! much smaller than the picture, and the small preview most cameras embed
//! is used instead when it is large enough. The scaling that follows uses
//! SIMD. A picture is turned the way its Exif orientation says, so a
//! portrait photo is upright.

use exif::{In, Tag};
use fast_image_resize as fir;
use image::metadata::Orientation;
use image::{DynamicImage, GrayAlphaImage, GrayImage, RgbImage, RgbaImage};
use std::path::Path;

/// The reduced decode keeps at least this many pixels per thumbnail pixel on
/// each axis, so the final scaling still averages.
const REDUCED_DECODE_MARGIN: u32 = 2;

/// The most memory a reduced decode may ask for: the image library's own
/// default limit for a full decode.
const MAX_DECODE_BYTES: usize = 512 * 1024 * 1024;

/// Sources up to this many pixels are scaled with the sharper Lanczos
/// filter, which costs a few milliseconds at this size. Larger ones are
/// averaged with a box filter, which is what the eye sees of a strong
/// reduction anyway and several times cheaper.
const SHARP_FILTER_MAX_PIXELS: u64 = 2_500_000;

/// An embedded preview whose shape differs from the picture's by more than
/// this is not the picture: some cameras pad a 3:2 photo's preview to 4:3.
const PREVIEW_SHAPE_TOLERANCE: f64 = 0.02;

/// An image decoded for a thumbnail, turned the way its orientation says.
/// `image` may be smaller than the file's picture; `full_size` is always the
/// picture's size, turned the same way.
pub struct Decoded {
    pub image: DynamicImage,
    pub full_size: (u32, u32),
}

/// The JPEG bytes of a thumbnail of `path` that fits a `size` by `size` box
/// and keeps the picture's aspect ratio. `extension` is the lowercase file
/// extension.
pub fn render(path: &Path, extension: &str, size: u32) -> Result<Vec<u8>, String> {
    render_decoded(&decode(path, extension, size)?, size)
}

/// The JPEG bytes of a thumbnail of an image that is already decoded: it
/// fits a `size` by `size` box and has the shape of `decoded.full_size`.
/// Transparency is dropped, not blended; see `flatten_onto_white`.
pub fn render_decoded(decoded: &Decoded, size: u32) -> Result<Vec<u8>, String> {
    let (width, height) = fit(decoded.full_size.0, decoded.full_size.1, size);
    let thumbnail = scale(&decoded.image, width, height);
    let mut buffer = std::io::Cursor::new(Vec::new());
    thumbnail
        .write_to(&mut buffer, image::ImageFormat::Jpeg)
        .map_err(|e| format!("Failed to encode thumbnail: {e}"))?;
    Ok(buffer.into_inner())
}

/// `image` as it looks on a white page: each pixel blended with white by its
/// alpha. A picture without an alpha channel is returned as it is.
pub fn flatten_onto_white(image: &DynamicImage) -> DynamicImage {
    if !image.color().has_alpha() {
        return image.clone();
    }
    let mut flat = image.to_rgba8();
    for pixel in flat.pixels_mut() {
        let alpha = u32::from(pixel.0[3]);
        for channel in &mut pixel.0[..3] {
            let blended = (u32::from(*channel) * alpha + 255 * (255 - alpha) + 127) / 255;
            *channel = blended as u8;
        }
        pixel.0[3] = 255;
    }
    DynamicImage::ImageRgb8(DynamicImage::ImageRgba8(flat).to_rgb8())
}

/// Decodes `path` for a thumbnail of `size`.
pub fn decode(path: &Path, extension: &str, size: u32) -> Result<Decoded, String> {
    if matches!(extension, "jpg" | "jpeg") {
        if let Some(decoded) = decode_jpeg_fast(path, size) {
            return Ok(decoded);
        }
    }
    decode_full(path)
}

/// The whole picture, by the image library, turned upright.
fn decode_full(path: &Path) -> Result<Decoded, String> {
    let open = |error: &dyn std::fmt::Display| format!("Failed to open image: {error}");
    let mut decoder = image::ImageReader::open(path)
        .and_then(image::ImageReader::with_guessed_format)
        .map_err(|e| open(&e))?
        .into_decoder()
        .map_err(|e| open(&e))?;
    let orientation =
        image::ImageDecoder::orientation(&mut decoder).unwrap_or(Orientation::NoTransforms);
    let mut image = DynamicImage::from_decoder(decoder).map_err(|e| open(&e))?;
    image.apply_orientation(orientation);
    let full_size = (image.width(), image.height());
    Ok(Decoded { image, full_size })
}

/// A JPEG by the fast paths: the camera's embedded preview when it is large
/// enough, else a decode at a half, a quarter or an eighth of the size.
/// `None` when the thumbnail needs the full picture, and for any file the
/// reduced decoder does not take (CMYK, 16-bit, damaged): the full decoder
/// then handles the file and reports its errors.
fn decode_jpeg_fast(path: &Path, size: u32) -> Option<Decoded> {
    use jpeg_decoder::PixelFormat;

    let file = std::io::BufReader::new(std::fs::File::open(path).ok()?);
    let mut decoder = jpeg_decoder::Decoder::new(file);
    decoder.set_max_decoding_buffer_size(MAX_DECODE_BYTES);
    decoder.read_info().ok()?;
    let info = decoder.info()?;
    if !matches!(info.pixel_format, PixelFormat::RGB24 | PixelFormat::L8) {
        return None;
    }
    let sensor_size = (u32::from(info.width), u32::from(info.height));

    let exif = read_exif(path);
    let orientation = exif
        .as_ref()
        .and_then(|exif| orientation_in(exif, In::PRIMARY))
        .unwrap_or(Orientation::NoTransforms);
    let full_size = turned(sensor_size, orientation);
    let target = fit(full_size.0, full_size.1, size);

    if let Some(image) = exif
        .as_ref()
        .and_then(|exif| usable_preview(exif, orientation, full_size, target))
    {
        return Some(Decoded { image, full_size });
    }

    // The decoder sees the sensor's picture: ask for the target in that frame.
    let (want_width, want_height) = turned_back(target, orientation);
    let at_least = |length: u32| {
        u16::try_from(length.saturating_mul(REDUCED_DECODE_MARGIN)).unwrap_or(u16::MAX)
    };
    let (reduced_width, reduced_height) = decoder
        .scale(at_least(want_width), at_least(want_height))
        .ok()?;
    let reduced_size = (u32::from(reduced_width), u32::from(reduced_height));
    if reduced_size == sensor_size {
        // Nothing to save, and the full decoder is the faster of the two.
        return None;
    }

    let pixels = decoder.decode().ok()?;
    let mut image = match info.pixel_format {
        PixelFormat::RGB24 => {
            DynamicImage::ImageRgb8(RgbImage::from_raw(reduced_size.0, reduced_size.1, pixels)?)
        }
        _ => DynamicImage::ImageLuma8(GrayImage::from_raw(reduced_size.0, reduced_size.1, pixels)?),
    };
    image.apply_orientation(orientation);
    Some(Decoded { image, full_size })
}

/// The file's Exif, or `None` when it has none the reader takes.
fn read_exif(path: &Path) -> Option<exif::Exif> {
    let mut file = std::io::BufReader::new(std::fs::File::open(path).ok()?);
    exif::Reader::new().read_from_container(&mut file).ok()
}

/// The orientation tag of `ifd` (IFD0 is the picture's, IFD1 the preview's).
fn orientation_in(exif: &exif::Exif, ifd: In) -> Option<Orientation> {
    let value = exif.get_field(Tag::Orientation, ifd)?.value.get_uint(0)?;
    Orientation::from_exif(u8::try_from(value).ok()?)
}

/// The preview the camera embedded, turned upright, when it can stand in for
/// the picture: at least `target` in size and the same shape as the picture.
fn usable_preview(
    exif: &exif::Exif,
    picture_orientation: Orientation,
    full_size: (u32, u32),
    target: (u32, u32),
) -> Option<DynamicImage> {
    let offset = exif
        .get_field(Tag::JPEGInterchangeFormat, In::THUMBNAIL)?
        .value
        .get_uint(0)?;
    let length = exif
        .get_field(Tag::JPEGInterchangeFormatLength, In::THUMBNAIL)?
        .value
        .get_uint(0)?;
    let start = usize::try_from(offset).ok()?;
    let end = start.checked_add(usize::try_from(length).ok()?)?;
    let bytes = exif.buf().get(start..end)?;
    let mut preview = image::load_from_memory_with_format(bytes, image::ImageFormat::Jpeg).ok()?;
    // Stored the way the sensor saw it, like the picture, unless it says otherwise.
    preview.apply_orientation(orientation_in(exif, In::THUMBNAIL).unwrap_or(picture_orientation));
    if preview.width() < target.0 || preview.height() < target.1 {
        return None;
    }
    let picture_shape = f64::from(full_size.0) / f64::from(full_size.1);
    let preview_shape = f64::from(preview.width()) / f64::from(preview.height());
    if ((preview_shape - picture_shape) / picture_shape).abs() > PREVIEW_SHAPE_TOLERANCE {
        return None;
    }
    Some(preview)
}

/// Whether `orientation` turns the picture a quarter turn, swapping its axes.
fn swaps_axes(orientation: Orientation) -> bool {
    matches!(
        orientation,
        Orientation::Rotate90
            | Orientation::Rotate270
            | Orientation::Rotate90FlipH
            | Orientation::Rotate270FlipH
    )
}

/// `size` once the picture is turned by `orientation`.
fn turned(size: (u32, u32), orientation: Orientation) -> (u32, u32) {
    if swaps_axes(orientation) {
        (size.1, size.0)
    } else {
        size
    }
}

/// `size` in the sensor's frame: the inverse of [`turned`].
fn turned_back(size: (u32, u32), orientation: Orientation) -> (u32, u32) {
    turned(size, orientation)
}

/// The largest size with the aspect ratio of `width` by `height` that fits a
/// `size` by `size` box: the size `DynamicImage::thumbnail` gives.
pub fn fit(width: u32, height: u32, size: u32) -> (u32, u32) {
    let ratio = f64::min(
        f64::from(size) / f64::from(width),
        f64::from(size) / f64::from(height),
    );
    let scaled = |length: u32| ((f64::from(length) * ratio).round() as u64).max(1);
    let (new_width, new_height) = (scaled(width), scaled(height));
    (
        u32::try_from(new_width).unwrap_or(u32::MAX),
        u32::try_from(new_height).unwrap_or(u32::MAX),
    )
}

/// `image` scaled to exactly `width` by `height`.
pub fn scale(image: &DynamicImage, width: u32, height: u32) -> DynamicImage {
    let reduces = width <= image.width() && height <= image.height();
    reduces
        .then(|| resample(image, width, height))
        .flatten()
        // Enlarging, and pixel formats other than 8-bit, as the image library does it.
        .unwrap_or_else(|| image.thumbnail_exact(width, height))
}

/// `image` reduced to `width` by `height` with SIMD, or `None` for a pixel
/// format this path does not handle.
fn resample(image: &DynamicImage, width: u32, height: u32) -> Option<DynamicImage> {
    let (pixel_type, source): (fir::PixelType, &[u8]) = match image {
        DynamicImage::ImageLuma8(pixels) => (fir::PixelType::U8, pixels.as_raw()),
        DynamicImage::ImageLumaA8(pixels) => (fir::PixelType::U8x2, pixels.as_raw()),
        DynamicImage::ImageRgb8(pixels) => (fir::PixelType::U8x3, pixels.as_raw()),
        DynamicImage::ImageRgba8(pixels) => (fir::PixelType::U8x4, pixels.as_raw()),
        _ => return None,
    };
    let source =
        fir::images::ImageRef::new(image.width(), image.height(), source, pixel_type).ok()?;
    let mut target = fir::images::Image::new(width, height, pixel_type);

    let source_pixels = u64::from(image.width()) * u64::from(image.height());
    let filter = if source_pixels <= SHARP_FILTER_MAX_PIXELS {
        fir::FilterType::Lanczos3
    } else {
        fir::FilterType::Box
    };
    // The alpha channel is averaged like a colour channel, not used as a
    // weight: the thumbnail is a JPEG, which drops it afterwards.
    let options = fir::ResizeOptions::new()
        .resize_alg(fir::ResizeAlg::Convolution(filter))
        .use_alpha(false);
    fir::Resizer::new()
        .resize(&source, &mut target, &options)
        .ok()?;

    let pixels = target.into_vec();
    match image {
        DynamicImage::ImageLuma8(_) => {
            GrayImage::from_raw(width, height, pixels).map(DynamicImage::ImageLuma8)
        }
        DynamicImage::ImageLumaA8(_) => {
            GrayAlphaImage::from_raw(width, height, pixels).map(DynamicImage::ImageLumaA8)
        }
        DynamicImage::ImageRgb8(_) => {
            RgbImage::from_raw(width, height, pixels).map(DynamicImage::ImageRgb8)
        }
        _ => RgbaImage::from_raw(width, height, pixels).map(DynamicImage::ImageRgba8),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::{GenericImageView, Rgb, Rgba};
    use std::path::PathBuf;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new(name: &str) -> Self {
            let dir = std::env::temp_dir().join(format!(
                "sumafile-thumbnail-{name}-{}-{:?}",
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

    /// A smooth colour picture: large soft shapes, as a photo has.
    fn picture(width: u32, height: u32) -> RgbImage {
        RgbImage::from_fn(width, height, |x, y| {
            let (fx, fy) = (
                x as f32 / width as f32 * 6.3,
                y as f32 / height as f32 * 6.3,
            );
            Rgb([
                (127.0 + 120.0 * (fx * 1.3).sin()) as u8,
                (127.0 + 120.0 * (fy * 0.9 + fx * 0.4).cos()) as u8,
                (127.0 + 120.0 * (fx + fy).sin()) as u8,
            ])
        })
    }

    fn save_jpeg(dir: &TempDir, name: &str, width: u32, height: u32) -> PathBuf {
        let path = dir.file(name);
        picture(width, height).save(&path).unwrap();
        path
    }

    fn thumbnail(path: &Path, extension: &str, size: u32) -> DynamicImage {
        let bytes = render(path, extension, size).unwrap();
        image::load_from_memory_with_format(&bytes, image::ImageFormat::Jpeg).unwrap()
    }

    /// The thumbnail the full decoder and the image library's own scaling give.
    fn reference(path: &Path, size: u32) -> DynamicImage {
        image::open(path).unwrap().thumbnail(size, size)
    }

    fn mean_difference(a: &DynamicImage, b: &DynamicImage) -> f64 {
        let (a, b) = (a.to_rgb8(), b.to_rgb8());
        assert_eq!(a.dimensions(), b.dimensions());
        let total: u64 = a
            .as_raw()
            .iter()
            .zip(b.as_raw())
            .map(|(x, y)| u64::from(x.abs_diff(*y)))
            .sum();
        total as f64 / a.as_raw().len() as f64
    }

    #[test]
    fn fit_gives_the_size_the_image_library_gives() {
        for (width, height) in [
            (6000, 4000),
            (1003, 667),
            (999, 1501),
            (4001, 37),
            (16, 16),
            (1, 900),
        ] {
            for size in [32, 128, 256, 512] {
                let expected = DynamicImage::new_luma8(width, height)
                    .thumbnail(size, size)
                    .dimensions();
                assert_eq!(
                    expected,
                    fit(width, height, size),
                    "{width}x{height} in {size}"
                );
            }
        }
    }

    #[test]
    fn a_large_jpeg_is_decoded_at_reduced_size() {
        let dir = TempDir::new("reduced");
        let path = save_jpeg(&dir, "photo.jpg", 1600, 1200);

        let decoded = decode(&path, "jpg", 128).unwrap();

        assert_eq!((1600, 1200), decoded.full_size);
        // 128 by 96 is the thumbnail; the decode keeps at least twice that so
        // the final scaling still averages, and no more than four times.
        assert_eq!((400, 300), decoded.image.dimensions());
    }

    #[test]
    fn a_jpeg_no_larger_than_twice_the_thumbnail_is_decoded_in_full() {
        let dir = TempDir::new("full");
        let path = save_jpeg(&dir, "small.jpeg", 200, 150);

        let decoded = decode(&path, "jpeg", 128).unwrap();

        assert_eq!((200, 150), decoded.full_size);
        assert_eq!((200, 150), decoded.image.dimensions());
    }

    #[test]
    fn a_reduced_decode_gives_the_thumbnail_size_a_full_decode_gives() {
        let dir = TempDir::new("sizes");
        for (width, height) in [(1003, 667), (999, 1501), (4001, 37), (2049, 2047)] {
            let path = save_jpeg(&dir, "odd.jpg", width, height);
            for size in [128, 256, 512] {
                assert_eq!(
                    reference(&path, size).dimensions(),
                    thumbnail(&path, "jpg", size).dimensions(),
                    "{width}x{height} in {size}"
                );
            }
        }
    }

    #[test]
    fn a_reduced_decode_shows_the_same_picture_as_a_full_decode() {
        let dir = TempDir::new("picture");
        let path = save_jpeg(&dir, "photo.jpg", 1600, 1200);

        let difference = mean_difference(&reference(&path, 128), &thumbnail(&path, "jpg", 128));

        assert!(difference < 3.0, "mean difference {difference}");
    }

    #[test]
    fn a_grey_jpeg_stays_grey() {
        let dir = TempDir::new("grey");
        let path = dir.file("grey.jpg");
        GrayImage::from_fn(1600, 1200, |x, y| {
            image::Luma([((x / 8 + y / 8) % 256) as u8])
        })
        .save(&path)
        .unwrap();

        let decoded = decode(&path, "jpg", 128).unwrap();

        assert_eq!((400, 300), decoded.image.dimensions());
        assert_eq!(image::ColorType::L8, decoded.image.color());
        assert_eq!((128, 96), thumbnail(&path, "jpg", 128).dimensions());
    }

    #[test]
    fn a_progressive_jpeg_is_decoded_at_reduced_size() {
        let dir = TempDir::new("progressive");
        let path = dir.file("progressive.jpg");
        std::fs::write(&path, include_bytes!("testdata/progressive.jpg")).unwrap();

        let decoded = decode(&path, "jpg", 32).unwrap();

        assert_eq!((320, 240), decoded.full_size);
        assert_eq!((80, 60), decoded.image.dimensions());
        let difference = mean_difference(&reference(&path, 32), &scale(&decoded.image, 32, 24));
        assert!(difference < 3.0, "mean difference {difference}");
    }

    /// The reduced decoder does not convert CMYK; the full decoder does.
    #[test]
    fn a_cmyk_jpeg_falls_back_to_the_full_decoder() {
        let dir = TempDir::new("cmyk");
        let path = dir.file("cmyk.jpg");
        std::fs::write(&path, include_bytes!("testdata/cmyk.jpg")).unwrap();

        let decoded = decode(&path, "jpg", 32).unwrap();

        assert_eq!((320, 240), decoded.image.dimensions());
        let difference = mean_difference(&reference(&path, 32), &scale(&decoded.image, 32, 24));
        assert!(difference < 3.0, "mean difference {difference}");
        assert_eq!((32, 24), thumbnail(&path, "jpg", 32).dimensions());
    }

    #[test]
    fn a_file_that_is_not_a_picture_reports_the_decoder_error() {
        let dir = TempDir::new("garbage");
        let path = dir.file("broken.jpg");
        std::fs::write(&path, b"this is not a jpeg at all").unwrap();

        let error = render(&path, "jpg", 128).unwrap_err();

        assert!(error.starts_with("Failed to open image: "), "{error}");
    }

    /// One-pixel stripes cannot be shown at 128 pixels: a thumbnail has to
    /// average them to grey. Sampling them instead leaves bands (moire).
    #[test]
    fn fine_detail_is_averaged_rather_than_sampled() {
        let stripes = DynamicImage::ImageRgb8(RgbImage::from_fn(300, 300, |x, _| {
            if x % 2 == 0 {
                Rgb([255, 255, 255])
            } else {
                Rgb([0, 0, 0])
            }
        }));

        let scaled = scale(&stripes, 128, 128).to_rgb8();

        // Away from the left and right edges every pixel is close to mid grey.
        let (darkest, lightest) = (8..120).fold((255u8, 0u8), |(low, high), x| {
            let value = scaled.get_pixel(x, 64).0[0];
            (low.min(value), high.max(value))
        });
        assert!(
            darkest >= 96 && lightest <= 160,
            "stripes left bands from {darkest} to {lightest}"
        );
    }

    #[test]
    fn scaling_keeps_the_pixel_format() {
        let colour = DynamicImage::ImageRgb8(picture(640, 480));
        let transparent = DynamicImage::ImageRgba8(RgbaImage::from_fn(640, 480, |x, y| {
            Rgba([(x % 256) as u8, (y % 256) as u8, 90, (x / 3) as u8])
        }));
        let grey = DynamicImage::ImageLuma8(GrayImage::from_fn(640, 480, |x, _| {
            image::Luma([(x / 3) as u8])
        }));
        let deep = DynamicImage::ImageRgb16(image::ImageBuffer::from_fn(640, 480, |x, y| {
            Rgb([(x * 100) as u16, (y * 100) as u16, 9000])
        }));

        for source in [colour, transparent, grey, deep] {
            let scaled = scale(&source, 128, 96);
            assert_eq!((128, 96), scaled.dimensions());
            assert_eq!(source.color(), scaled.color());
            // Same picture as the image library's own scaling.
            let difference = mean_difference(&source.thumbnail_exact(128, 96), &scaled);
            assert!(
                difference < 3.0,
                "{:?}: mean difference {difference}",
                source.color()
            );
        }
    }

    /// Transparent pixels keep their own colour when averaged, as before:
    /// the thumbnail is a JPEG, so the alpha channel is dropped afterwards.
    #[test]
    fn transparent_pixels_are_averaged_like_any_other() {
        let source = DynamicImage::ImageRgba8(RgbaImage::from_fn(512, 512, |x, _| {
            if x < 256 {
                Rgba([200, 40, 40, 255])
            } else {
                Rgba([40, 40, 200, 0])
            }
        }));

        let scaled = scale(&source, 128, 128).to_rgba8();

        assert_eq!([200, 40, 40, 255], scaled.get_pixel(20, 64).0);
        assert_eq!([40, 40, 200, 0], scaled.get_pixel(108, 64).0);
    }

    #[test]
    fn a_small_picture_is_enlarged_exactly_as_before() {
        let icon = DynamicImage::ImageRgb8(picture(16, 12));

        let scaled = scale(&icon, 128, 96);

        assert_eq!(icon.thumbnail_exact(128, 96).to_rgb8(), scaled.to_rgb8());
    }

    #[test]
    fn a_png_thumbnail_fits_the_box() {
        let dir = TempDir::new("png");
        let path = dir.file("shot.png");
        picture(900, 300).save(&path).unwrap();

        let thumbnail = thumbnail(&path, "png", 128);

        assert_eq!((128, 43), thumbnail.dimensions());
        let difference = mean_difference(&reference(&path, 128), &thumbnail);
        assert!(difference < 3.0, "mean difference {difference}");
    }

    // ---- Orientation and the camera's embedded preview ----

    fn jpeg_bytes(image: &DynamicImage) -> Vec<u8> {
        let mut buffer = std::io::Cursor::new(Vec::new());
        image
            .write_to(&mut buffer, image::ImageFormat::Jpeg)
            .unwrap();
        buffer.into_inner()
    }

    /// `jpeg` with an Exif segment: IFD0 carries `orientation`; with `preview`, IFD1 carries that JPEG as the
    /// embedded thumbnail, with `preview_orientation` when given. Little-endian TIFF, as cameras write.
    fn with_exif(
        jpeg: &[u8],
        orientation: u16,
        preview: Option<&[u8]>,
        preview_orientation: Option<u16>,
    ) -> Vec<u8> {
        fn entry(tiff: &mut Vec<u8>, tag: u16, kind: u16, value: u32) {
            tiff.extend_from_slice(&tag.to_le_bytes());
            tiff.extend_from_slice(&kind.to_le_bytes());
            tiff.extend_from_slice(&1u32.to_le_bytes());
            tiff.extend_from_slice(&value.to_le_bytes());
        }
        let mut tiff = b"II\x2a\x00\x08\x00\x00\x00".to_vec();
        let ifd1_entries = preview.map_or(0, |_| 2 + usize::from(preview_orientation.is_some()));
        let ifd1_offset = 8 + 2 + 12 + 4;
        tiff.extend_from_slice(&1u16.to_le_bytes());
        entry(&mut tiff, 0x0112, 3, u32::from(orientation));
        tiff.extend_from_slice(
            &(if preview.is_some() {
                ifd1_offset as u32
            } else {
                0
            })
            .to_le_bytes(),
        );
        if let Some(bytes) = preview {
            let data_offset = (ifd1_offset + 2 + ifd1_entries * 12 + 4) as u32;
            tiff.extend_from_slice(&(ifd1_entries as u16).to_le_bytes());
            if let Some(value) = preview_orientation {
                entry(&mut tiff, 0x0112, 3, u32::from(value));
            }
            entry(&mut tiff, 0x0201, 4, data_offset);
            entry(&mut tiff, 0x0202, 4, bytes.len() as u32);
            tiff.extend_from_slice(&0u32.to_le_bytes());
            tiff.extend_from_slice(bytes);
        }
        assert_eq!(&jpeg[..2], b"\xff\xd8");
        let mut out = jpeg[..2].to_vec();
        out.extend_from_slice(b"\xff\xe1");
        out.extend_from_slice(&((2 + 6 + tiff.len()) as u16).to_be_bytes());
        out.extend_from_slice(b"Exif\0\0");
        out.extend_from_slice(&tiff);
        out.extend_from_slice(&jpeg[2..]);
        out
    }

    /// The thumbnail a full decode gives once the picture is turned the way `orientation` says.
    fn oriented_reference(path: &Path, orientation: u16, size: u32) -> DynamicImage {
        let mut image = image::open(path).unwrap();
        image
            .apply_orientation(image::metadata::Orientation::from_exif(orientation as u8).unwrap());
        image.thumbnail(size, size)
    }

    fn solid(width: u32, height: u32, colour: [u8; 3]) -> DynamicImage {
        DynamicImage::ImageRgb8(RgbImage::from_pixel(width, height, Rgb(colour)))
    }

    fn is_green(pixel: [u8; 3]) -> bool {
        pixel[0] < 40 && pixel[1] > 200 && pixel[2] < 40
    }

    /// Phone and camera photos carry the sensor's picture plus an orientation tag; a thumbnail must be turned
    /// the way the tag says, or a portrait photo shows on its side.
    #[test]
    fn a_rotated_jpeg_decoded_at_reduced_size_is_turned_upright() {
        let dir = TempDir::new("rotated-reduced");
        let path = dir.file("portrait.jpg");
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(640, 480))),
                6,
                None,
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128);

        assert_eq!((96, 128), thumbnail.dimensions());
        let difference = mean_difference(&oriented_reference(&path, 6, 128), &thumbnail);
        assert!(difference < 3.0, "mean difference {difference}");
    }

    #[test]
    fn a_rotated_jpeg_decoded_in_full_is_turned_upright() {
        let dir = TempDir::new("rotated-full");
        let path = dir.file("upside-down.jpg");
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(200, 150))),
                3,
                None,
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128);

        assert_eq!((128, 96), thumbnail.dimensions());
        let difference = mean_difference(&oriented_reference(&path, 3, 128), &thumbnail);
        assert!(difference < 3.0, "mean difference {difference}");
    }

    /// The path for files the reduced decoder does not take (here CMYK) turns the picture too.
    #[test]
    fn a_rotated_jpeg_the_reduced_decoder_refuses_is_turned_upright() {
        let dir = TempDir::new("rotated-cmyk");
        let path = dir.file("cmyk.jpg");
        std::fs::write(
            &path,
            with_exif(include_bytes!("testdata/cmyk.jpg"), 6, None, None),
        )
        .unwrap();

        let decoded = decode(&path, "jpg", 32).unwrap();

        // Decoded whole and turned a quarter turn. Compared before the JPEG encoding of the thumbnail: this
        // fixture is noisy enough that chroma subsampling alone moves a 24-pixel-wide picture more than the
        // tolerance, and the question here is only whether the picture was turned.
        assert_eq!((240, 320), decoded.image.dimensions());
        assert_eq!((240, 320), decoded.full_size);
        let mut upright = image::open(&path).unwrap();
        upright.apply_orientation(image::metadata::Orientation::Rotate90);
        let difference = mean_difference(&upright, &decoded.image);
        assert!(difference < 1.0, "mean difference {difference}");
        assert_eq!((24, 32), thumbnail(&path, "jpg", 32).dimensions());
    }

    #[test]
    fn a_mirrored_jpeg_is_mirrored_back() {
        let dir = TempDir::new("mirrored");
        let path = dir.file("mirrored.jpg");
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(640, 480))),
                2,
                None,
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128);

        assert_eq!((128, 96), thumbnail.dimensions());
        let difference = mean_difference(&oriented_reference(&path, 2, 128), &thumbnail);
        assert!(difference < 3.0, "mean difference {difference}");
    }

    /// Most camera JPEGs carry a small ready-made preview. When it is at least as large as the thumbnail, it
    /// is the thumbnail: no decoding of the picture at all. The preview here is a different picture (solid
    /// green) so the test can tell which one was used.
    #[test]
    fn a_large_enough_embedded_preview_is_used_as_the_thumbnail() {
        let dir = TempDir::new("preview-used");
        let path = dir.file("photo.jpg");
        let preview = jpeg_bytes(&solid(160, 120, [0, 255, 0]));
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(1600, 1200))),
                1,
                Some(&preview),
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128).to_rgb8();

        // The size the picture itself would give, not the preview's own size.
        assert_eq!((128, 96), thumbnail.dimensions());
        assert!(
            is_green(thumbnail.get_pixel(64, 48).0),
            "{:?}",
            thumbnail.get_pixel(64, 48).0
        );
        assert!(
            is_green(thumbnail.get_pixel(2, 2).0),
            "{:?}",
            thumbnail.get_pixel(2, 2).0
        );
    }

    #[test]
    fn an_embedded_preview_smaller_than_the_thumbnail_is_not_used() {
        let dir = TempDir::new("preview-small");
        let path = dir.file("photo.jpg");
        let preview = jpeg_bytes(&solid(160, 120, [0, 255, 0]));
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(1600, 1200))),
                1,
                Some(&preview),
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 256);

        assert_eq!((256, 192), thumbnail.dimensions());
        let difference = mean_difference(&reference(&path, 256), &thumbnail);
        assert!(difference < 3.0, "mean difference {difference}");
    }

    /// Some cameras pad the preview of a 3:2 photo to 4:3 with black bars. A preview whose shape is not the
    /// picture's is not the picture.
    #[test]
    fn an_embedded_preview_of_another_shape_is_not_used() {
        let dir = TempDir::new("preview-shape");
        let path = dir.file("photo.jpg");
        let preview = jpeg_bytes(&solid(160, 120, [0, 255, 0]));
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(1602, 1068))),
                1,
                Some(&preview),
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128);

        assert_eq!((128, 85), thumbnail.dimensions());
        let difference = mean_difference(&reference(&path, 128), &thumbnail);
        assert!(difference < 3.0, "mean difference {difference}");
    }

    #[test]
    fn a_damaged_embedded_preview_is_not_used() {
        let dir = TempDir::new("preview-damaged");
        let path = dir.file("photo.jpg");
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(1600, 1200))),
                1,
                Some(b"not a jpeg"),
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128);

        assert_eq!((128, 96), thumbnail.dimensions());
        let difference = mean_difference(&reference(&path, 128), &thumbnail);
        assert!(difference < 3.0, "mean difference {difference}");
    }

    /// The preview is stored the way the sensor saw it, like the picture: it is turned the same way.
    #[test]
    fn an_embedded_preview_is_turned_upright_too() {
        let dir = TempDir::new("preview-rotated");
        let path = dir.file("portrait.jpg");
        let mut halves = RgbImage::from_pixel(160, 120, Rgb([0, 255, 0]));
        for y in 0..120 {
            for x in 80..160 {
                halves.put_pixel(x, y, Rgb([0, 0, 255]));
            }
        }
        let preview = jpeg_bytes(&DynamicImage::ImageRgb8(halves));
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(1600, 1200))),
                6,
                Some(&preview),
                None,
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128).to_rgb8();

        // Rotated 90 degrees clockwise: the left (green) half is now the top half.
        assert_eq!((96, 128), thumbnail.dimensions());
        assert!(
            is_green(thumbnail.get_pixel(48, 20).0),
            "top {:?}",
            thumbnail.get_pixel(48, 20).0
        );
        let bottom = thumbnail.get_pixel(48, 108).0;
        assert!(bottom[2] > 200 && bottom[1] < 40, "bottom {bottom:?}");
    }

    /// A preview with its own orientation tag follows that tag rather than the picture's.
    #[test]
    fn an_embedded_previews_own_orientation_wins() {
        let dir = TempDir::new("preview-own-orientation");
        let path = dir.file("photo.jpg");
        let mut halves = RgbImage::from_pixel(160, 120, Rgb([0, 255, 0]));
        for y in 0..120 {
            for x in 80..160 {
                halves.put_pixel(x, y, Rgb([0, 0, 255]));
            }
        }
        let preview = jpeg_bytes(&DynamicImage::ImageRgb8(halves));
        // The picture is upright; the preview says it is upside down.
        std::fs::write(
            &path,
            with_exif(
                &jpeg_bytes(&DynamicImage::ImageRgb8(picture(1600, 1200))),
                1,
                Some(&preview),
                Some(3),
            ),
        )
        .unwrap();

        let thumbnail = thumbnail(&path, "jpg", 128).to_rgb8();

        // Turned half a turn: the green half is now on the right.
        assert_eq!((128, 96), thumbnail.dimensions());
        assert!(
            is_green(thumbnail.get_pixel(96, 48).0),
            "right {:?}",
            thumbnail.get_pixel(96, 48).0
        );
        let left = thumbnail.get_pixel(32, 48).0;
        assert!(left[2] > 200 && left[1] < 40, "left {left:?}");
    }

    #[test]
    fn render_decoded_fits_the_box_and_keeps_the_shape() {
        let decoded = Decoded {
            image: DynamicImage::ImageRgb8(picture(400, 200)),
            full_size: (400, 200),
        };

        let jpeg = render_decoded(&decoded, 100).unwrap();

        let thumbnail = image::load_from_memory(&jpeg).unwrap();
        assert_eq!(thumbnail.dimensions(), (100, 50));
    }

    /// A reduced decode is smaller than the picture; the thumbnail still has
    /// the picture's shape, taken from `full_size`.
    #[test]
    fn render_decoded_takes_the_shape_from_the_full_size() {
        let decoded = Decoded {
            image: DynamicImage::ImageRgb8(picture(202, 100)),
            full_size: (4000, 2000),
        };

        let jpeg = render_decoded(&decoded, 100).unwrap();

        assert_eq!(image::load_from_memory(&jpeg).unwrap().dimensions(), (100, 50));
    }

    #[test]
    fn flatten_onto_white_puts_transparent_pixels_on_white() {
        let clear = DynamicImage::ImageRgba8(RgbaImage::from_pixel(8, 8, Rgba([10, 20, 30, 0])));
        let half = DynamicImage::ImageRgba8(RgbaImage::from_pixel(8, 8, Rgba([0, 0, 0, 128])));

        let flat = flatten_onto_white(&clear).to_rgb8();
        let blended = flatten_onto_white(&half).to_rgb8();

        assert!(flat.pixels().all(|pixel| pixel.0 == [255, 255, 255]));
        assert!(blended
            .pixels()
            .all(|pixel| pixel.0.iter().all(|&channel| (126..=128).contains(&channel))));
    }

    #[test]
    fn flatten_onto_white_keeps_an_opaque_picture() {
        let opaque = DynamicImage::ImageRgb8(picture(8, 8));

        assert_eq!(flatten_onto_white(&opaque).to_rgb8(), opaque.to_rgb8());
    }
}
