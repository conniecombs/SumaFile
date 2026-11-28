// Ported from Oculante (https://github.com/woelper/oculante), Copyright (c) Johann Woelper, MIT licence. See LICENSE-oculante.txt.

//! OpenEXR: the first layer with colour channels, tone-mapped to 8 bits.

use super::{open, whole, Decoded};
use crate::limits::check_budget;
use crate::render::RenderError;
use crate::tonemap::tonemap_f32;
use exr::prelude as exrs;
use exr::prelude::*;
use image::{DynamicImage, GrayImage, RgbaImage};
use std::path::Path;

// `exr::prelude` brings its own `Result`; this is the standard one.
pub(super) fn decode(path: &Path) -> std::result::Result<Decoded, RenderError> {
    // Opened once here so that a file that cannot be read is an I/O error,
    // not a decode error.
    drop(open(path)?);
    let meta = exr::meta::MetaData::read_from_file(path, false).map_err(exr_error)?;
    for header in &meta.headers {
        let size = header.layer_size;
        let side = |length: usize| u32::try_from(length).unwrap_or(u32::MAX);
        // Read as four float channels before they are tone-mapped.
        check_budget(side(size.width()), side(size.height()), 4, 4)?;
    }

    let reader = exrs::read()
        .no_deep_data()
        .largest_resolution_level()
        .rgba_channels(
            |resolution, _channels: &RgbaChannels| -> RgbaImage {
                image::ImageBuffer::new(resolution.width() as u32, resolution.height() as u32)
            },
            |pixels, position, (red, green, blue, alpha): (f32, f32, f32, f32)| {
                pixels.put_pixel(
                    position.x() as u32,
                    position.y() as u32,
                    // Oculante puts alpha through the gamma curve as well.
                    // Alpha is coverage, so here it is only brought to 8 bits.
                    image::Rgba([
                        tonemap_f32(red),
                        tonemap_f32(green),
                        tonemap_f32(blue),
                        (alpha.clamp(0.0, 1.0) * 255.0).round() as u8,
                    ]),
                );
            },
        )
        .first_valid_layer()
        .all_attributes();
    let colour: std::result::Result<
        Image<Layer<SpecificChannels<RgbaImage, RgbaChannels>>>,
        exrs::Error,
    > = reader.from_file(path);

    match colour {
        Ok(image) => Ok(whole(DynamicImage::ImageRgba8(
            image.layer_data.channel_data.pixels,
        ))),
        // No red, green and blue channels: show the first channel as grey.
        Err(colour_error) => first_channel(path).ok_or_else(|| exr_error(colour_error)),
    }
}

fn first_channel(path: &Path) -> Option<Decoded> {
    let layer = exrs::read_first_flat_layer_from_file(path).ok()?;
    let size = layer.layer_data.size;
    let channel = layer.layer_data.channel_data.list.into_iter().next()?;
    let grey: Vec<u8> = match channel.sample_data {
        FlatSamples::F16(samples) => samples
            .iter()
            .map(|sample| tonemap_f32(sample.to_f32()))
            .collect(),
        FlatSamples::F32(samples) => samples.iter().map(|sample| tonemap_f32(*sample)).collect(),
        FlatSamples::U32(_) => return None,
    };
    let grey = GrayImage::from_raw(size.width() as u32, size.height() as u32, grey)?;
    Some(whole(DynamicImage::ImageLuma8(grey)))
}

fn exr_error(error: exrs::Error) -> RenderError {
    RenderError::Decode(format!("The OpenEXR file could not be decoded: {error}"))
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;

    const PREVIEW: Purpose = Purpose::Preview {
        max_dimension: 2560,
    };

    fn light(folder: &Folder) -> std::path::PathBuf {
        let path = folder.path("light.exr");
        exr::prelude::write_rgba_file(&path, 4, 3, |x, y| {
            (x as f32 / 3.0, y as f32 / 2.0, 0.5f32, 1.0f32)
        })
        .unwrap();
        path
    }

    #[test]
    fn decodes_exr_tone_mapped() {
        let folder = Folder::new("exr");

        let decoded = decode(&light(&folder), "exr", PREVIEW).unwrap();

        assert_eq!(decoded.full_size, (4, 3));
        let pixels = decoded.image.to_rgba8();
        let near = |value: u8, expected: u8| value.abs_diff(expected) <= 2;
        let dark = pixels.get_pixel(0, 0).0;
        assert!(
            dark[0] == 0 && dark[1] == 0 && near(dark[2], 186) && dark[3] == 255,
            "{dark:?}"
        );
        let bright = pixels.get_pixel(3, 2).0;
        assert!(
            bright[0] == 255 && bright[1] == 255 && near(bright[2], 186),
            "{bright:?}"
        );
    }

    #[test]
    fn a_truncated_exr_answers_decode() {
        let folder = Folder::new("exr-cut");
        let whole = std::fs::read(light(&folder)).unwrap();
        let cut = folder.write("cut.exr", &whole[..whole.len() / 2]);
        let text = folder.write("text.exr", b"not an OpenEXR file at all");

        let cut = decode(&cut, "exr", PREVIEW).err();
        let text = decode(&text, "exr", PREVIEW).err();

        assert!(matches!(cut, Some(RenderError::Decode(_))), "{cut:?}");
        assert!(matches!(text, Some(RenderError::Decode(_))), "{text:?}");
    }
}
