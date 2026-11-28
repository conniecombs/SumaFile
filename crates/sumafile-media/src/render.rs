//! The two things the media process makes of a file: a thumbnail and a
//! screen-size preview.

use crate::decoders::{self, Decoded};
use image::codecs::jpeg::JpegEncoder;
use image::codecs::png::{CompressionType, FilterType, PngEncoder};
use image::{DynamicImage, ImageEncoder};
use std::path::Path;
use sumafile_imaging::thumbnail::{
    fit, flatten_onto_white, render_decoded, scale, scale_with_alpha,
};
use sumafile_media_client::protocol::{ImageKind, Method, Request};

pub use sumafile_media_client::Rendered;

/// Why a file gave no picture.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RenderError {
    /// There is no decoder for this file.
    Unsupported(String),
    /// The picture would not fit the pixel budget.
    TooLarge,
    /// The file's contents are not what its format says.
    Decode(String),
    /// The file could not be read.
    Io(String),
}

/// What a decode is for. A decoder may do less work for a thumbnail.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Purpose {
    Thumbnail { size: u32 },
    Preview { max_dimension: u32 },
}

const THUMBNAIL_SIZES: std::ops::RangeInclusive<u32> = 16..=1024;
const PREVIEW_SIZES: std::ops::RangeInclusive<u32> = 64..=8192;
const DEFAULT_THUMBNAIL_SIZE: u32 = 256;
const DEFAULT_PREVIEW_SIZE: u32 = 2560;
const PREVIEW_JPEG_QUALITY: u8 = 90;

/// Answer one request from the file service.
pub fn handle(request: &Request) -> Result<Rendered, RenderError> {
    let path = Path::new(&request.path);
    match request.method {
        Method::Thumbnail => thumbnail(path, request.size.unwrap_or(DEFAULT_THUMBNAIL_SIZE)),
        Method::Preview => preview(path, request.max_dimension.unwrap_or(DEFAULT_PREVIEW_SIZE)),
        Method::VideoThumbnail => Err(RenderError::Unsupported(
            "Video thumbnails come from the video process.".to_string(),
        )),
    }
}

/// A JPEG of `path` that fits a `size` by `size` box and keeps the picture's
/// shape. Transparent parts are put on white. A picture smaller than the box
/// is not enlarged.
pub fn thumbnail(path: &Path, size: u32) -> Result<Rendered, RenderError> {
    let size = size.clamp(*THUMBNAIL_SIZES.start(), *THUMBNAIL_SIZES.end());
    let decoded = decode(path, Purpose::Thumbnail { size })?;
    let (full_width, full_height) = decoded.full_size;
    let size = size.min(full_width.max(full_height));
    let (width, height) = fit(full_width, full_height, size);

    let flat = Decoded {
        image: flatten_onto_white(eight_bits(decoded.image)),
        full_size: decoded.full_size,
    };
    let bytes = render_decoded(&flat, size).map_err(RenderError::Decode)?;
    Ok(Rendered {
        kind: ImageKind::Jpeg,
        width,
        height,
        bytes,
    })
}

/// `path` at screen size: at most `max_dimension` on its long side, never
/// enlarged. A JPEG when nothing shows through the picture, a PNG when
/// something does.
pub fn preview(path: &Path, max_dimension: u32) -> Result<Rendered, RenderError> {
    let max_dimension = max_dimension.clamp(*PREVIEW_SIZES.start(), *PREVIEW_SIZES.end());
    let image = eight_bits(decode(path, Purpose::Preview { max_dimension })?.image);
    let image = if image.width().max(image.height()) > max_dimension {
        let (width, height) = fit(image.width(), image.height(), max_dimension);
        if shows_through(&image) {
            // The preview keeps its transparency, so each colour is weighed
            // by its alpha. That takes a copy of the picture, which one with
            // nothing to show through is spared.
            scale_with_alpha(&image, width, height)
        } else {
            scale(&image, width, height)
        }
    } else {
        image
    };
    encode_preview(image)
}

fn decode(path: &Path, purpose: Purpose) -> Result<Decoded, RenderError> {
    let extension = path
        .extension()
        .and_then(|extension| extension.to_str())
        .map(str::to_ascii_lowercase)
        .ok_or_else(|| {
            RenderError::Unsupported("SumaFile has no decoder for this file.".to_string())
        })?;
    let decoded = decoders::decode(path, &extension, purpose)?;
    if decoded.image.width() == 0
        || decoded.image.height() == 0
        || decoded.full_size.0 == 0
        || decoded.full_size.1 == 0
    {
        return Err(RenderError::Decode("The image is empty.".to_string()));
    }
    Ok(decoded)
}

/// 8 bits a channel: RGBA when the picture has an alpha channel, else RGB.
fn eight_bits(image: DynamicImage) -> DynamicImage {
    match image {
        DynamicImage::ImageRgb8(_) | DynamicImage::ImageRgba8(_) => image,
        other if other.color().has_alpha() => DynamicImage::ImageRgba8(other.into_rgba8()),
        other => DynamicImage::ImageRgb8(other.into_rgb8()),
    }
}

/// Whether any pixel of `image` lets what is behind it show.
fn shows_through(image: &DynamicImage) -> bool {
    match image {
        DynamicImage::ImageRgba8(pixels) => pixels.pixels().any(|pixel| pixel.0[3] != u8::MAX),
        _ => false,
    }
}

fn encode_preview(image: DynamicImage) -> Result<Rendered, RenderError> {
    let (width, height) = (image.width(), image.height());
    let failed = |error: image::ImageError| {
        RenderError::Decode(format!("The preview could not be encoded: {error}"))
    };
    let mut bytes = Vec::new();

    let kind = if shows_through(&image) {
        PngEncoder::new_with_quality(&mut bytes, CompressionType::Fast, FilterType::Adaptive)
            .write_image(image.as_bytes(), width, height, image.color().into())
            .map_err(failed)?;
        ImageKind::Png
    } else {
        let opaque = image.into_rgb8();
        JpegEncoder::new_with_quality(&mut bytes, PREVIEW_JPEG_QUALITY)
            .write_image(
                opaque.as_raw(),
                width,
                height,
                image::ExtendedColorType::Rgb8,
            )
            .map_err(failed)?;
        ImageKind::Jpeg
    };
    Ok(Rendered {
        kind,
        width,
        height,
        bytes,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Videos go to the video process. Asked by mistake, this one says so
    /// and decodes nothing.
    #[test]
    fn a_video_request_is_not_this_programs_to_answer() {
        let request = Request {
            id: 1,
            method: Method::VideoThumbnail,
            path: "clip.mp4".to_string(),
            size: Some(64),
            max_dimension: None,
            position_percent: Some(25),
            hardware: Some(true),
        };

        let answer = handle(&request);

        assert!(
            matches!(answer, Err(RenderError::Unsupported(_))),
            "{answer:?}"
        );
    }
    use crate::testing::Folder;
    use image::{GenericImageView, Rgb, RgbImage, Rgba, RgbaImage};
    use sumafile_media_client::protocol::ImageKind;

    /// Soft shapes, so a JPEG of it stays close to it.
    fn picture(width: u32, height: u32) -> RgbImage {
        RgbImage::from_fn(width, height, |x, y| {
            Rgb([
                (x * 255 / width.max(1)) as u8,
                (y * 255 / height.max(1)) as u8,
                128,
            ])
        })
    }

    fn saved(folder: &Folder, name: &str, width: u32, height: u32) -> std::path::PathBuf {
        let path = folder.path(name);
        picture(width, height).save(&path).unwrap();
        path
    }

    #[test]
    fn a_thumbnail_is_a_jpeg_that_fits_the_box() {
        let folder = Folder::new("thumb-fit");
        let path = saved(&folder, "wide.ppm", 400, 200);

        let rendered = thumbnail(&path, 64).unwrap();

        assert_eq!(rendered.kind, ImageKind::Jpeg);
        assert_eq!((rendered.width, rendered.height), (64, 32));
        let decoded = image::load_from_memory(&rendered.bytes).unwrap();
        assert_eq!(decoded.dimensions(), (64, 32));
    }

    /// The app draws a small picture at the size it likes; a blurry
    /// enlargement in the cache helps nobody.
    #[test]
    fn a_thumbnail_is_never_enlarged() {
        let folder = Folder::new("thumb-small");
        let path = saved(&folder, "tiny.ppm", 40, 30);

        let rendered = thumbnail(&path, 256).unwrap();

        assert_eq!((rendered.width, rendered.height), (40, 30));
        assert_eq!(
            image::load_from_memory(&rendered.bytes)
                .unwrap()
                .dimensions(),
            (40, 30)
        );
    }

    #[test]
    fn a_thumbnail_puts_transparency_on_white() {
        let folder = Folder::new("thumb-alpha");
        let path = folder.path("clear.tga");
        RgbaImage::from_pixel(32, 32, Rgba([0, 0, 0, 0]))
            .save(&path)
            .unwrap();

        let rendered = thumbnail(&path, 32).unwrap();

        let decoded = image::load_from_memory(&rendered.bytes).unwrap().to_rgb8();
        assert!(decoded
            .pixels()
            .all(|pixel| pixel.0.iter().all(|&channel| channel >= 250)));
    }

    #[test]
    fn a_preview_of_an_opaque_picture_is_a_jpeg_at_its_own_size() {
        let folder = Folder::new("preview-opaque");
        let path = saved(&folder, "photo.ppm", 40, 30);

        let rendered = preview(&path, 2560).unwrap();

        assert_eq!(rendered.kind, ImageKind::Jpeg);
        assert_eq!((rendered.width, rendered.height), (40, 30));
        assert_eq!(
            image::load_from_memory(&rendered.bytes)
                .unwrap()
                .dimensions(),
            (40, 30)
        );
    }

    #[test]
    fn a_preview_is_scaled_down_to_the_limit() {
        let folder = Folder::new("preview-limit");
        let path = saved(&folder, "wide.ppm", 400, 200);

        let rendered = preview(&path, 100).unwrap();

        assert_eq!((rendered.width, rendered.height), (100, 50));
    }

    #[test]
    fn a_preview_keeps_transparency_as_png() {
        let folder = Folder::new("preview-alpha");
        let path = folder.path("glass.tga");
        RgbaImage::from_pixel(8, 8, Rgba([200, 10, 10, 64]))
            .save(&path)
            .unwrap();

        let rendered = preview(&path, 2560).unwrap();

        assert_eq!(rendered.kind, ImageKind::Png);
        let decoded = image::load_from_memory(&rendered.bytes).unwrap().to_rgba8();
        assert_eq!(decoded.get_pixel(3, 3).0, [200, 10, 10, 64]);
    }

    /// White beside nothing. Scaled down, the pixels on the edge are partly
    /// see-through, and must still be white: the colour of a pixel that does
    /// not show (black here) has no part in them. Averaged in, it leaves a
    /// dark fringe around every cut-out.
    #[test]
    fn a_scaled_preview_has_no_fringe_at_a_transparent_edge() {
        let folder = Folder::new("preview-fringe");
        let path = folder.path("cutout.tga");
        RgbaImage::from_fn(256, 256, |x, _| {
            if x < 130 {
                Rgba([255, 255, 255, 255])
            } else {
                Rgba([0, 0, 0, 0])
            }
        })
        .save(&path)
        .unwrap();

        let rendered = preview(&path, 64).unwrap();

        assert_eq!(rendered.kind, ImageKind::Png);
        let decoded = image::load_from_memory(&rendered.bytes).unwrap().to_rgba8();
        assert_eq!(decoded.dimensions(), (64, 64));
        let edge: Vec<_> = (0..64)
            .map(|x| decoded.get_pixel(x, 32).0)
            .filter(|pixel| (1..255).contains(&pixel[3]))
            .collect();
        assert!(!edge.is_empty(), "the edge has no partly see-through pixel");
        for pixel in edge {
            assert!(
                pixel[..3].iter().all(|&channel| channel >= 250),
                "a dark fringe: {pixel:?}"
            );
        }
    }

    /// An alpha channel that hides nothing is not worth a PNG several times
    /// the size.
    #[test]
    fn a_preview_with_a_solid_alpha_channel_is_a_jpeg() {
        let folder = Folder::new("preview-solid");
        let path = folder.path("solid.tga");
        RgbaImage::from_pixel(8, 8, Rgba([200, 10, 10, 255]))
            .save(&path)
            .unwrap();

        assert_eq!(preview(&path, 2560).unwrap().kind, ImageKind::Jpeg);
    }

    #[test]
    fn a_file_without_a_known_extension_is_unsupported() {
        let folder = Folder::new("no-extension");
        let plain = folder.write("README", b"text");
        let unknown = folder.write("notes.xyz", b"text");

        let plain = thumbnail(&plain, 64).err();
        let unknown = preview(&unknown, 64).err();

        assert!(
            matches!(plain, Some(RenderError::Unsupported(_))),
            "{plain:?}"
        );
        assert!(
            matches!(unknown, Some(RenderError::Unsupported(_))),
            "{unknown:?}"
        );
    }

    #[test]
    fn the_extension_is_matched_in_any_case() {
        let folder = Folder::new("case");
        let path = saved(&folder, "photo.ppm", 8, 8);
        // Another name, not only another case: on Windows `PHOTO.PPM` is
        // the file `photo.ppm`, and a file cannot be copied onto itself.
        let upper = folder.path("UPPER.PPM");
        std::fs::copy(&path, &upper).unwrap();

        assert!(thumbnail(&upper, 64).is_ok());
    }
}
