//! JPEG XL, through `jxl-oxide`.

use super::simple::{image_error, to_screen_values};
use super::{open, whole, Decoded};
use crate::limits::check_budget;
use crate::render::RenderError;
use image::{DynamicImage, ImageDecoder};
use jxl_oxide::integration::JxlDecoder;
use std::path::Path;

pub(super) fn decode(path: &Path) -> Result<Decoded, RenderError> {
    let decoder = JxlDecoder::new(open(path)?).map_err(image_error)?;
    let (width, height) = decoder.dimensions();
    let colour = decoder.color_type();
    let channels = colour.channel_count().max(1);
    check_budget(width, height, channels, colour.bytes_per_pixel() / channels)?;

    let image = DynamicImage::from_decoder(decoder).map_err(image_error)?;
    Ok(whole(to_screen_values(image)))
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;
    use zune_core::bit_depth::BitDepth;
    use zune_core::colorspace::ColorSpace;
    use zune_core::options::EncoderOptions;
    use zune_jpegxl::JxlSimpleEncoder;

    const PREVIEW: Purpose = Purpose::Preview {
        max_dimension: 2560,
    };

    fn pixels() -> Vec<u8> {
        (0..4 * 3 * 3).map(|value| (value * 7) as u8).collect()
    }

    /// A lossless JPEG XL of a 4 by 3 picture.
    fn jxl() -> Vec<u8> {
        let options = EncoderOptions::new(4, 3, ColorSpace::RGB, BitDepth::Eight);
        let mut encoded = Vec::new();
        JxlSimpleEncoder::new(&pixels(), options)
            .encode(&mut encoded)
            .unwrap();
        encoded
    }

    #[test]
    fn decodes_jxl() {
        let folder = Folder::new("jxl");
        let path = folder.write("picture.jxl", &jxl());

        let decoded = decode(&path, "jxl", PREVIEW).unwrap();

        assert_eq!(decoded.full_size, (4, 3));
        assert_eq!(decoded.image.to_rgb8().into_raw(), pixels());
    }

    #[test]
    fn a_truncated_jxl_answers_decode() {
        let folder = Folder::new("jxl-cut");
        let whole = jxl();
        let cut = folder.write("cut.jxl", &whole[..whole.len() / 2]);
        let text = folder.write("text.jxl", b"no JPEG XL here, only text");

        let cut = decode(&cut, "jxl", PREVIEW).err();
        let text = decode(&text, "jxl", PREVIEW).err();

        assert!(matches!(cut, Some(RenderError::Decode(_))), "{cut:?}");
        assert!(matches!(text, Some(RenderError::Decode(_))), "{text:?}");
    }
}
