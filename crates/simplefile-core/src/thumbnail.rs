//! Renders a thumbnail: decode an image file, scale it to fit a square box,
//! and encode the result as JPEG.
//!
//! Decoding is most of the cost for a photo, so a JPEG is decoded straight
//! to a reduced size (a half, a quarter or an eighth) when the thumbnail is
//! much smaller than the picture. The scaling that follows uses SIMD.

use fast_image_resize as fir;
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

/// An image decoded for a thumbnail. `image` may be smaller than the file's
/// picture; `full_size` is always the size the file declares.
pub(crate) struct Decoded {
    pub(crate) image: DynamicImage,
    pub(crate) full_size: (u32, u32),
}

/// The JPEG bytes of a thumbnail of `path` that fits a `size` by `size` box
/// and keeps the picture's aspect ratio. `extension` is the lowercase file
/// extension.
pub(crate) fn render(path: &Path, extension: &str, size: u32) -> Result<Vec<u8>, String> {
    let decoded = decode(path, extension, size)?;
    let (width, height) = fit(decoded.full_size.0, decoded.full_size.1, size);
    let thumbnail = scale(&decoded.image, width, height);
    let mut buffer = std::io::Cursor::new(Vec::new());
    thumbnail
        .write_to(&mut buffer, image::ImageFormat::Jpeg)
        .map_err(|e| format!("Failed to encode thumbnail: {e}"))?;
    Ok(buffer.into_inner())
}

/// Decodes `path` for a thumbnail of `size`.
pub(crate) fn decode(path: &Path, extension: &str, size: u32) -> Result<Decoded, String> {
    if matches!(extension, "jpg" | "jpeg") {
        if let Some(decoded) = decode_jpeg_reduced(path, size) {
            return Ok(decoded);
        }
    }
    let image = image::open(path).map_err(|e| format!("Failed to open image: {e}"))?;
    let full_size = (image.width(), image.height());
    Ok(Decoded { image, full_size })
}

/// A JPEG decoded at a half, a quarter or an eighth of its size. `None` when
/// the thumbnail needs the full picture, and for any file this decoder does
/// not take (CMYK, 16-bit, damaged): the full decoder then handles the file
/// and reports its errors.
fn decode_jpeg_reduced(path: &Path, size: u32) -> Option<Decoded> {
    use jpeg_decoder::PixelFormat;

    let file = std::io::BufReader::new(std::fs::File::open(path).ok()?);
    let mut decoder = jpeg_decoder::Decoder::new(file);
    decoder.set_max_decoding_buffer_size(MAX_DECODE_BYTES);
    decoder.read_info().ok()?;
    let info = decoder.info()?;
    if !matches!(info.pixel_format, PixelFormat::RGB24 | PixelFormat::L8) {
        return None;
    }
    let full_size = (u32::from(info.width), u32::from(info.height));

    let (width, height) = fit(full_size.0, full_size.1, size);
    let at_least = |length: u32| {
        u16::try_from(length.saturating_mul(REDUCED_DECODE_MARGIN)).unwrap_or(u16::MAX)
    };
    let (reduced_width, reduced_height) = decoder.scale(at_least(width), at_least(height)).ok()?;
    let reduced_size = (u32::from(reduced_width), u32::from(reduced_height));
    if reduced_size == full_size {
        // Nothing to save, and the full decoder is the faster of the two.
        return None;
    }

    let pixels = decoder.decode().ok()?;
    let image = match info.pixel_format {
        PixelFormat::RGB24 => {
            DynamicImage::ImageRgb8(RgbImage::from_raw(reduced_size.0, reduced_size.1, pixels)?)
        }
        _ => DynamicImage::ImageLuma8(GrayImage::from_raw(reduced_size.0, reduced_size.1, pixels)?),
    };
    Some(Decoded { image, full_size })
}

/// The largest size with the aspect ratio of `width` by `height` that fits a
/// `size` by `size` box: the size `DynamicImage::thumbnail` gives.
pub(crate) fn fit(width: u32, height: u32, size: u32) -> (u32, u32) {
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
pub(crate) fn scale(image: &DynamicImage, width: u32, height: u32) -> DynamicImage {
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
}
