//! JPEG XL, through `jxl-oxide`.

use super::simple::{image_error, library_limits, to_screen_values};
use super::{open, whole, Decoded};
use crate::limits::check_budget;
use crate::render::RenderError;
use image::{DynamicImage, ImageDecoder, ImageError};
use jxl_oxide::integration::JxlDecoder;
use std::path::Path;

pub(super) fn decode(path: &Path) -> Result<Decoded, RenderError> {
    decode_within(path, library_limits())
}

fn decode_within(path: &Path, limits: image::Limits) -> Result<Decoded, RenderError> {
    let mut decoder = JxlDecoder::new(open(path)?).map_err(image_error)?;
    // The decoder works in buffers several times the size of the picture it
    // returns. It counts them itself, against this limit.
    decoder.set_limits(limits).map_err(image_error)?;
    let (width, height) = decoder.dimensions();
    let colour = decoder.color_type();
    let channels = colour.channel_count().max(1);
    check_budget(width, height, channels, colour.bytes_per_pixel() / channels)?;

    let image = DynamicImage::from_decoder(decoder).map_err(decoding_error)?;
    Ok(whole(to_screen_values(image)))
}

/// `jxl-oxide` has no error kind for a buffer its limit refused: it reports a
/// decoding error that ends in its allocation counter's own words.
fn decoding_error(error: ImageError) -> RenderError {
    if error.to_string().contains("failed to allocate") {
        return RenderError::TooLarge;
    }
    image_error(error)
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
        jxl_of(4, 3, &pixels())
    }

    fn jxl_of(width: usize, height: usize, pixels: &[u8]) -> Vec<u8> {
        let options = EncoderOptions::new(width, height, ColorSpace::RGB, BitDepth::Eight);
        let mut encoded = Vec::new();
        JxlSimpleEncoder::new(pixels, options)
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

    /// The start of a JPEG XL file whose header claims this size, for 8-bit
    /// colour with every other setting at its default. No picture follows.
    fn header_only(width: u64, height: u64) -> Vec<u8> {
        // Fields are packed from each byte's lowest bit up.
        let (mut bits, mut used) = (0u64, 0);
        let mut put = |value: u64, length: u32| {
            bits |= value << used;
            used += length;
        };
        put(0, 1); // the sizes are not in units of eight pixels
        put(2, 2); // the height takes 18 bits
        put(height - 1, 18);
        put(0, 3); // no fixed shape: the width follows
        put(2, 2);
        put(width - 1, 18);
        put(1, 1); // every other setting is at its default,
        put(1, 1); // and so is the colour transform
        let mut file = vec![0xFF, 0x0A];
        file.extend_from_slice(&bits.to_le_bytes()[..used.div_ceil(8) as usize]);
        file
    }

    /// The size is in the header: the answer comes before any of the
    /// picture's 4.8 GB is asked for.
    #[test]
    fn a_jxl_over_budget_answers_too_large() {
        let folder = Folder::new("jxl-huge");
        let path = folder.write("huge.jxl", &header_only(40_000, 40_000));

        let error = decode(&path, "jxl", PREVIEW).err();

        assert!(matches!(error, Some(RenderError::TooLarge)), "{error:?}");
    }

    /// The decoder works in buffers several times the size of the picture it
    /// returns. Those count against the budget too: here the picture itself
    /// (192 KiB) fits the limit and the decoder's buffers do not.
    #[test]
    fn the_decoders_own_buffers_count_against_the_limit() {
        let folder = Folder::new("jxl-limit");
        let pixels: Vec<u8> = (0..256 * 256 * 3)
            .map(|value| (value % 251) as u8)
            .collect();
        let path = folder.write("picture.jxl", &jxl_of(256, 256, &pixels));
        let mut limits = image::Limits::default();
        limits.max_alloc = Some(256 * 1024);

        let error = super::decode_within(&path, limits).err();

        assert!(matches!(error, Some(RenderError::TooLarge)), "{error:?}");
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
