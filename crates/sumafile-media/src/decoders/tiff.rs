// Ported from Oculante (https://github.com/woelper/oculante), Copyright (c) Johann Woelper, MIT licence. See LICENSE-oculante.txt.

//! TIFF, through the `tiff` library, for sample types the `image` library
//! does not take: 16 and 32 bits, floats, CMYK.

use super::{open, whole, Decoded};
use crate::limits::{check_budget, PIXEL_BUDGET_BYTES};
use crate::render::RenderError;
use image::metadata::Orientation;
use image::{DynamicImage, GrayAlphaImage, GrayImage, RgbImage, RgbaImage};
use std::path::Path;
use tiff::decoder::{Decoder, DecodingResult, Limits};
use tiff::tags::Tag;
use tiff::ColorType;

pub(super) fn decode(path: &Path) -> Result<Decoded, RenderError> {
    let budget = usize::try_from(PIXEL_BUDGET_BYTES).unwrap_or(usize::MAX);
    let mut limits = Limits::default();
    limits.decoding_buffer_size = budget;
    limits.intermediate_buffer_size = budget;
    let mut decoder = Decoder::new(open(path)?)
        .map_err(tiff_error)?
        .with_limits(limits);

    let (width, height) = decoder.dimensions().map_err(tiff_error)?;
    let colour = decoder.colortype().map_err(tiff_error)?;
    let (channels, bits) = match colour {
        ColorType::Gray(bits) => (1, bits),
        ColorType::GrayA(bits) => (2, bits),
        ColorType::RGB(bits) => (3, bits),
        ColorType::RGBA(bits) | ColorType::CMYK(bits) => (4, bits),
        other => {
            return Err(RenderError::Decode(format!(
                "This kind of TIFF ({other:?}) is not supported."
            )))
        }
    };
    // Samples that are not 8 or 16 bits pass through 32-bit floats.
    let sample_bytes = if bits <= 16 {
        bits.div_ceil(8).max(1)
    } else {
        4
    };
    check_budget(width, height, channels, sample_bytes)?;
    let orientation = decoder
        .get_tag_u32(Tag::Orientation)
        .ok()
        .and_then(|value| u8::try_from(value).ok())
        .and_then(Orientation::from_exif);

    let floats = |samples: &mut dyn Iterator<Item = f32>| autoscale(&samples.collect::<Vec<_>>());
    let samples: Vec<u8> = match decoder.read_image().map_err(tiff_error)? {
        DecodingResult::U8(samples) => samples,
        // The photo as it is: each sample's high byte. Oculante spreads these
        // from the darkest to the brightest sample, which changes the picture.
        DecodingResult::U16(samples) => samples.iter().map(|sample| (sample >> 8) as u8).collect(),
        DecodingResult::F16(samples) => {
            floats(&mut samples.iter().map(|sample| f32::from(*sample)))
        }
        DecodingResult::F32(samples) => autoscale(&samples),
        DecodingResult::F64(samples) => floats(&mut samples.iter().map(|sample| *sample as f32)),
        DecodingResult::U32(samples) => floats(&mut samples.iter().map(|sample| *sample as f32)),
        DecodingResult::U64(samples) => floats(&mut samples.iter().map(|sample| *sample as f32)),
        DecodingResult::I8(samples) => floats(&mut samples.iter().map(|sample| f32::from(*sample))),
        DecodingResult::I16(samples) => {
            floats(&mut samples.iter().map(|sample| f32::from(*sample)))
        }
        DecodingResult::I32(samples) => floats(&mut samples.iter().map(|sample| *sample as f32)),
        DecodingResult::I64(samples) => floats(&mut samples.iter().map(|sample| *sample as f32)),
    };

    let image = match colour {
        ColorType::Gray(_) => {
            GrayImage::from_raw(width, height, samples).map(DynamicImage::ImageLuma8)
        }
        ColorType::GrayA(_) => {
            GrayAlphaImage::from_raw(width, height, samples).map(DynamicImage::ImageLumaA8)
        }
        ColorType::RGB(_) => {
            RgbImage::from_raw(width, height, samples).map(DynamicImage::ImageRgb8)
        }
        ColorType::RGBA(_) => {
            RgbaImage::from_raw(width, height, samples).map(DynamicImage::ImageRgba8)
        }
        _ => RgbImage::from_raw(width, height, cmyk_to_rgb(&samples)).map(DynamicImage::ImageRgb8),
    };
    let mut image = image.ok_or_else(|| {
        RenderError::Decode("The TIFF file holds fewer pixels than its size says.".to_string())
    })?;
    if let Some(orientation) = orientation {
        image.apply_orientation(orientation);
    }
    Ok(whole(image))
}

/// Samples spread from the lowest (0) to the highest (255).
fn autoscale(values: &[f32]) -> Vec<u8> {
    let mut lowest = f32::MAX;
    let mut highest = f32::MIN;
    for value in values {
        lowest = lowest.min(*value);
        highest = highest.max(*value);
    }
    let range = highest - lowest;
    values
        .iter()
        .map(|value| ((value - lowest) / range * 255.0) as u8)
        .collect()
}

/// Ink on paper to light on a screen, without a colour profile.
fn cmyk_to_rgb(inks: &[u8]) -> Vec<u8> {
    let mut colour = Vec::with_capacity(inks.len() / 4 * 3);
    for ink in inks.as_chunks::<4>().0 {
        let black = 255 - u32::from(ink[3]);
        for channel in &ink[..3] {
            colour.push(((255 - u32::from(*channel)) * black / 255) as u8);
        }
    }
    colour
}

fn tiff_error(error: tiff::TiffError) -> RenderError {
    match error {
        tiff::TiffError::LimitsExceeded => RenderError::TooLarge,
        other => RenderError::Decode(format!("The TIFF file could not be decoded: {other}")),
    }
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;
    use std::fs::File;
    use tiff::encoder::{colortype, TiffEncoder};
    use tiff::tags::Tag;

    const PREVIEW: Purpose = Purpose::Preview {
        max_dimension: 2560,
    };

    #[test]
    fn decodes_8_bit_rgb() {
        let folder = Folder::new("tiff-8");
        let path = folder.path("photo.tif");
        let pixels: Vec<u8> = (0..4 * 3 * 3).map(|value| (value * 7) as u8).collect();
        TiffEncoder::new(File::create(&path).unwrap())
            .unwrap()
            .write_image::<colortype::RGB8>(4, 3, &pixels)
            .unwrap();

        let decoded = decode(&path, "tif", PREVIEW).unwrap();

        assert_eq!(decoded.full_size, (4, 3));
        assert_eq!(decoded.image.to_rgb8().into_raw(), pixels);
    }

    /// A 16-bit photo must look like the photo: each sample's high byte, not
    /// a stretch to the darkest and brightest sample.
    #[test]
    fn decodes_16_bit_by_its_high_byte() {
        let folder = Folder::new("tiff-16");
        let path = folder.path("deep.tiff");
        let pixels: [u16; 6] = [0, 0x8000, 0xFFFF, 0x0100, 0x4000, 0xC000];
        TiffEncoder::new(File::create(&path).unwrap())
            .unwrap()
            .write_image::<colortype::RGB16>(2, 1, &pixels)
            .unwrap();

        let decoded = decode(&path, "tiff", PREVIEW).unwrap().image.to_rgb8();

        assert_eq!(decoded.into_raw(), [0, 0x80, 0xFF, 0x01, 0x40, 0xC0]);
    }

    /// Float samples have no fixed range; they are spread from the lowest to
    /// the highest, as Oculante shows them.
    #[test]
    fn decodes_float_samples_spread_over_their_range() {
        let folder = Folder::new("tiff-float");
        let path = folder.path("field.tif");
        let samples: [f32; 4] = [-1.0, 0.0, 1.0, 3.0];
        TiffEncoder::new(File::create(&path).unwrap())
            .unwrap()
            .write_image::<colortype::Gray32Float>(2, 2, &samples)
            .unwrap();

        let decoded = decode(&path, "tif", PREVIEW).unwrap().image.to_luma8();

        assert_eq!(decoded.into_raw(), [0, 63, 127, 255]);
    }

    #[test]
    fn decodes_cmyk_as_colour() {
        let folder = Folder::new("tiff-cmyk");
        let path = folder.path("print.tif");
        // Magenta and yellow ink: red. No ink: white. Black ink: black.
        let inks: [u8; 12] = [0, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 255];
        TiffEncoder::new(File::create(&path).unwrap())
            .unwrap()
            .write_image::<colortype::CMYK8>(3, 1, &inks)
            .unwrap();

        let decoded = decode(&path, "tif", PREVIEW).unwrap().image.to_rgb8();

        assert_eq!(decoded.into_raw(), [255, 0, 0, 255, 255, 255, 0, 0, 0]);
    }

    #[test]
    fn a_tiff_is_turned_by_its_orientation() {
        let folder = Folder::new("tiff-turned");
        let path = folder.path("portrait.tif");
        let mut encoder = TiffEncoder::new(File::create(&path).unwrap()).unwrap();
        let mut image = encoder.new_image::<colortype::RGB8>(2, 1).unwrap();
        // 6: the picture's top is the file's right-hand side.
        image.encoder().write_tag(Tag::Orientation, 6u16).unwrap();
        image.write_data(&[255, 0, 0, 0, 0, 255]).unwrap();

        let decoded = decode(&path, "tif", PREVIEW).unwrap();

        assert_eq!(decoded.full_size, (1, 2));
        let pixels = decoded.image.to_rgb8();
        assert_eq!(pixels.get_pixel(0, 0).0, [255, 0, 0]);
        assert_eq!(pixels.get_pixel(0, 1).0, [0, 0, 255]);
    }

    #[test]
    fn a_truncated_tiff_answers_decode() {
        let folder = Folder::new("tiff-cut");
        let path = folder.path("photo.tif");
        TiffEncoder::new(File::create(&path).unwrap())
            .unwrap()
            .write_image::<colortype::RGB8>(64, 64, &vec![9u8; 64 * 64 * 3])
            .unwrap();
        let whole = std::fs::read(&path).unwrap();
        let cut = folder.write("cut.tif", &whole[..200]);
        let text = folder.write("text.tif", b"no TIFF here");

        let cut = decode(&cut, "tif", PREVIEW).err();
        let text = decode(&text, "tif", PREVIEW).err();

        assert!(matches!(cut, Some(RenderError::Decode(_))), "{cut:?}");
        assert!(matches!(text, Some(RenderError::Decode(_))), "{text:?}");
    }
}
