//! Formats the `image` library decodes: Targa, QOI, the portable anymap
//! family and Radiance HDR.

use super::{open, whole, Decoded};
use crate::limits::{check_budget, PIXEL_BUDGET_BYTES};
use crate::render::RenderError;
use crate::tonemap::{tonemap_rgb32f, tonemap_rgba32f};
use image::{DynamicImage, ImageDecoder, ImageError, ImageFormat};
use std::io::{BufRead, Seek};
use std::path::Path;

pub(super) fn decode(path: &Path, extension: &str) -> Result<Decoded, RenderError> {
    let format = match extension {
        "tga" => ImageFormat::Tga,
        "qoi" => ImageFormat::Qoi,
        "hdr" => ImageFormat::Hdr,
        _ => ImageFormat::Pnm,
    };
    decode_reader(open(path)?, format)
}

/// Decode a picture in one of the `image` library's formats from `reader`.
pub(crate) fn decode_reader<R: BufRead + Seek>(
    reader: R,
    format: ImageFormat,
) -> Result<Decoded, RenderError> {
    let mut reader = image::ImageReader::with_format(reader, format);
    reader.limits(library_limits());
    let decoder = reader.into_decoder().map_err(image_error)?;

    let (width, height) = decoder.dimensions();
    let colour = decoder.color_type();
    let channels = colour.channel_count().max(1);
    check_budget(width, height, channels, colour.bytes_per_pixel() / channels)?;

    let image = DynamicImage::from_decoder(decoder).map_err(image_error)?;
    Ok(whole(to_screen_values(image)))
}

/// The `image` library's own guard, set to the same budget.
pub(crate) fn library_limits() -> image::Limits {
    let mut limits = image::Limits::default();
    limits.max_alloc = Some(PIXEL_BUDGET_BYTES);
    limits
}

pub(crate) fn image_error(error: ImageError) -> RenderError {
    match error {
        ImageError::Limits(_) => RenderError::TooLarge,
        other => RenderError::Decode(format!("The image could not be decoded: {other}")),
    }
}

/// Float pictures hold light; everything else is already screen values.
pub(crate) fn to_screen_values(image: DynamicImage) -> DynamicImage {
    match image {
        DynamicImage::ImageRgb32F(light) => DynamicImage::ImageRgb8(tonemap_rgb32f(&light)),
        DynamicImage::ImageRgba32F(light) => DynamicImage::ImageRgba8(tonemap_rgba32f(&light)),
        other => other,
    }
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;
    use image::{DynamicImage, Rgb, RgbImage, Rgba, RgbaImage};

    const PREVIEW: Purpose = Purpose::Preview { max_dimension: 2560 };

    /// 4 by 3, each pixel different.
    fn picture() -> RgbImage {
        RgbImage::from_fn(4, 3, |x, y| Rgb([(x * 60) as u8, (y * 100) as u8, 200]))
    }

    fn assert_is_the_picture(decoded: &DynamicImage) {
        assert_eq!((decoded.width(), decoded.height()), (4, 3));
        assert_eq!(decoded.to_rgb8(), picture());
    }

    #[test]
    fn decodes_tga() {
        let folder = Folder::new("tga");
        let path = folder.path("picture.tga");
        picture().save(&path).unwrap();

        let decoded = decode(&path, "tga", PREVIEW).unwrap();

        assert_is_the_picture(&decoded.image);
        assert_eq!(decoded.full_size, (4, 3));
    }

    #[test]
    fn decodes_qoi() {
        let folder = Folder::new("qoi");
        let path = folder.path("picture.qoi");
        picture().save(&path).unwrap();

        assert_is_the_picture(&decode(&path, "qoi", PREVIEW).unwrap().image);
    }

    #[test]
    fn decodes_the_anymap_family_by_any_of_its_extensions() {
        let folder = Folder::new("pnm");
        let source = folder.path("picture.ppm");
        picture().save(&source).unwrap();
        let bytes = std::fs::read(&source).unwrap();

        for extension in ["ppm", "pnm", "pbm", "pgm", "pam"] {
            let path = folder.write(&format!("picture.{extension}"), &bytes);
            assert_is_the_picture(&decode(&path, extension, PREVIEW).unwrap().image);
        }
    }

    /// Radiance pictures hold light, not screen values: they are brought to
    /// 8 bits with a gamma curve, as Oculante shows them.
    #[test]
    fn decodes_hdr_tone_mapped() {
        let folder = Folder::new("hdr");
        let path = folder.path("light.hdr");
        let light = [Rgb([0.0f32, 0.5, 1.0]), Rgb([4.0, 1.0, 0.25])];
        let file = std::fs::File::create(&path).unwrap();
        image::codecs::hdr::HdrEncoder::new(file)
            .encode(&light, 2, 1)
            .unwrap();

        let decoded = decode(&path, "hdr", PREVIEW).unwrap().image.to_rgb8();

        let near = |value: u8, expected: u8| value.abs_diff(expected) <= 2;
        let [dark, half, full] = decoded.get_pixel(0, 0).0;
        assert!(dark == 0 && near(half, 186) && full == 255, "{:?}", decoded.get_pixel(0, 0));
        let [over, one, quarter] = decoded.get_pixel(1, 0).0;
        assert!(over == 255 && one == 255 && near(quarter, 135), "{:?}", decoded.get_pixel(1, 0));
    }

    #[test]
    fn keeps_an_alpha_channel() {
        let folder = Folder::new("alpha");
        let path = folder.path("glass.tga");
        RgbaImage::from_pixel(2, 2, Rgba([10, 20, 30, 40]))
            .save(&path)
            .unwrap();

        let decoded = decode(&path, "tga", PREVIEW).unwrap().image;

        assert_eq!(decoded.to_rgba8().get_pixel(1, 1).0, [10, 20, 30, 40]);
    }

    /// The size is in the header: the answer comes before any pixel is read
    /// or any memory for 4.8 GB of them is asked for.
    #[test]
    fn over_budget_answers_too_large() {
        let folder = Folder::new("budget");
        let path = folder.write("huge.ppm", b"P6\n40000 40000\n255\n\0\0\0");

        let error = decode(&path, "ppm", PREVIEW).err();

        assert!(matches!(error, Some(RenderError::TooLarge)), "{error:?}");
    }

    #[test]
    fn wrong_contents_answer_decode() {
        let folder = Folder::new("wrong");
        let jpeg_named_qoi = folder.write("photo.qoi", b"\xff\xd8\xff\xe0\x00\x10JFIF\x00");
        let cut_off = folder.write("cut.ppm", b"P6\n4 3\n255\n\x01\x02");

        let wrong = decode(&jpeg_named_qoi, "qoi", PREVIEW).err();
        let truncated = decode(&cut_off, "ppm", PREVIEW).err();

        assert!(matches!(wrong, Some(RenderError::Decode(_))), "{wrong:?}");
        assert!(matches!(truncated, Some(RenderError::Decode(_))), "{truncated:?}");
    }

    #[test]
    fn a_missing_file_is_an_io_error() {
        let folder = Folder::new("missing");

        let error = decode(&folder.path("gone.tga"), "tga", PREVIEW).err();

        assert!(matches!(error, Some(RenderError::Io(_))), "{error:?}");
    }

    #[test]
    fn an_unknown_extension_is_unsupported() {
        let folder = Folder::new("unknown");
        let path = folder.write("notes.xyz", b"hello");

        let error = decode(&path, "xyz", PREVIEW).err();

        assert!(matches!(error, Some(RenderError::Unsupported(_))), "{error:?}");
    }
}
