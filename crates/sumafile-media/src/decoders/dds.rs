//! DirectDraw Surface textures, through `image_dds`: the largest mipmap.

use super::{open, whole, Decoded};
use crate::limits::check_budget;
use crate::render::RenderError;
use image::DynamicImage;
use std::path::Path;

pub(super) fn decode(path: &Path) -> Result<Decoded, RenderError> {
    let failed = |error: &dyn std::fmt::Display| {
        RenderError::Decode(format!("The DDS file could not be decoded: {error}"))
    };
    let mut file = open(path)?;
    let dds = image_dds::ddsfile::Dds::read(&mut file).map_err(|error| failed(&error))?;
    check_budget(dds.get_width(), dds.get_height(), 4, 1)?;

    let image = image_dds::image_from_dds(&dds, 0).map_err(|error| failed(&error))?;
    Ok(whole(DynamicImage::ImageRgba8(image)))
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;

    const PREVIEW: Purpose = Purpose::Preview { max_dimension: 2560 };

    /// A DDS header for a BC1 (DXT1) texture of this size, without its data.
    fn bc1_header(width: u32, height: u32) -> Vec<u8> {
        let blocks = width.div_ceil(4) * height.div_ceil(4);
        let mut header = Vec::new();
        header.extend_from_slice(b"DDS ");
        let mut field = |value: u32| header.extend_from_slice(&value.to_le_bytes());
        field(124); // header size
        field(0x0008_1007); // caps, height, width, pixel format, linear size
        field(height);
        field(width);
        field(blocks.saturating_mul(8)); // linear size
        field(0); // depth
        field(0); // mipmap count
        for _ in 0..11 {
            field(0);
        }
        field(32); // pixel format size
        field(0x4); // four-character code
        field(u32::from_le_bytes(*b"DXT1"));
        for _ in 0..5 {
            field(0); // bit count and masks
        }
        field(0x1000); // texture
        for _ in 0..4 {
            field(0);
        }
        assert_eq!(header.len(), 128);
        header
    }

    #[test]
    fn decodes_dds_bc1() {
        let folder = Folder::new("dds");
        let mut file = bc1_header(4, 4);
        // One block: colour 0 red, colour 1 blue, every pixel colour 0.
        file.extend_from_slice(&[0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0]);
        let path = folder.write("red.dds", &file);

        let decoded = decode(&path, "dds", PREVIEW).unwrap();

        assert_eq!(decoded.full_size, (4, 4));
        assert!(decoded
            .image
            .to_rgba8()
            .pixels()
            .all(|pixel| pixel.0 == [255, 0, 0, 255]));
    }

    #[test]
    fn a_dds_over_budget_answers_too_large() {
        let folder = Folder::new("dds-huge");
        let path = folder.write("huge.dds", &bc1_header(40_000, 40_000));

        let error = decode(&path, "dds", PREVIEW).err();

        assert!(matches!(error, Some(RenderError::TooLarge)), "{error:?}");
    }

    #[test]
    fn a_truncated_dds_answers_decode() {
        let folder = Folder::new("dds-cut");
        let no_data = folder.write("cut.dds", &bc1_header(64, 64));
        let text = folder.write("text.dds", b"no texture here");

        let no_data = decode(&no_data, "dds", PREVIEW).err();
        let text = decode(&text, "dds", PREVIEW).err();

        assert!(matches!(no_data, Some(RenderError::Decode(_))), "{no_data:?}");
        assert!(matches!(text, Some(RenderError::Decode(_))), "{text:?}");
    }
}
