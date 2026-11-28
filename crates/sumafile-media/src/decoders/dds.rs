//! DirectDraw Surface textures, through `image_dds`: the largest mipmap of
//! the first picture in the file.

use super::{open, whole, Decoded};
use crate::limits::{check_budget, PIXEL_BUDGET_BYTES};
use crate::render::RenderError;
use image::DynamicImage;
use image_dds::ddsfile::Dds;
use image_dds::error::SurfaceError;
use image_dds::Surface;
use std::io::Read;
use std::path::Path;

/// The magic number, the header and the DX10 header that may follow it.
const HEADERS_BYTES: u64 = 4 + 124 + 20;
/// No pixel format takes more of the file for one pixel: four 32-bit floats.
const MOST_BYTES_PER_PIXEL: u64 = 16;

pub(super) fn decode(path: &Path) -> Result<Decoded, RenderError> {
    let file = open(path)?;
    let length = file
        .get_ref()
        .metadata()
        .map_err(|error| RenderError::Io(format!("Cannot read {}: {error}", path.display())))?
        .len();
    decode_reader(file, length)
}

/// Decode the DDS file that `file` reads, which is `length` bytes long.
fn decode_reader(mut file: impl Read, length: u64) -> Result<Decoded, RenderError> {
    let failed = |error: &dyn std::fmt::Display| {
        RenderError::Decode(format!("The DDS file could not be decoded: {error}"))
    };
    // The headers alone at first: the size they claim is checked before any
    // pixel data is read. `ddsfile` reads to the end of what it is given.
    let headers = file.by_ref().take(HEADERS_BYTES);
    let mut dds = Dds::read(headers).map_err(|error| failed(&error))?;
    let (width, height) = (dds.get_width(), dds.get_height());
    check_budget(width, height, 4, 1)?;
    let image_format = image_dds::dds_image_format(&dds)
        .map_err(|format| failed(&SurfaceError::UnsupportedDdsFormat(format)))?;

    // A cube map, a texture array and a volume hold many pictures, and each
    // may have smaller copies of itself. The first picture at full size is
    // the one shown, and it is the first thing in the file. However long the
    // file is, no more of it is read than that picture can take.
    let most = (u64::from(width) * u64::from(height))
        .saturating_mul(MOST_BYTES_PER_PIXEL)
        .min(PIXEL_BUDGET_BYTES);
    // Room for it is asked for once: grown as it is read, the buffer would
    // end up to twice as large as what it holds.
    dds.data.reserve_exact(most.min(length) as usize);
    file.take(most)
        .read_to_end(&mut dds.data)
        .map_err(|error| failed(&error))?;
    let first = Surface {
        width,
        height,
        depth: 1,
        layers: 1,
        mipmaps: 1,
        image_format,
        data: dds.data.as_slice(),
    };
    let image = first
        .decode_rgba8()
        .map_err(|error| match error {
            // The picture takes more of the file than was read of it.
            SurfaceError::NotEnoughData { expected, .. }
                if expected as u64 > PIXEL_BUDGET_BYTES =>
            {
                RenderError::TooLarge
            }
            other => failed(&other),
        })?
        .into_image()
        .map_err(|error| failed(&error))?;
    Ok(whole(DynamicImage::ImageRgba8(image)))
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;

    const PREVIEW: Purpose = Purpose::Preview {
        max_dimension: 2560,
    };

    const DXT1: u32 = u32::from_le_bytes(*b"DXT1");
    /// Four 32-bit floats a pixel (D3DFMT_A32B32G32R32F).
    const FLOATS: u32 = 116;
    const CUBE_MAP: u32 = 0xFE00;
    const VOLUME: u32 = 0x20_0000;
    /// In the DX10 header: the same blocks as DXT1, and a flat texture.
    const BC1_UNORM: u32 = 71;
    const TEXTURE_2D: u32 = 3;
    /// One DXT1 block of 4 by 4 pixels, all of the one colour.
    const RED: [u8; 8] = [0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0];
    const BLUE: [u8; 8] = [0x1F, 0x00, 0x00, 0xF8, 0, 0, 0, 0];

    /// A DDS header for a BC1 (DXT1) texture of this size, without its data.
    fn bc1_header(width: u32, height: u32) -> Vec<u8> {
        header(width, height, 1, 0, DXT1)
    }

    /// A DDS header without its data. `depth` is the number of slices of a
    /// volume, and `caps2` says whether the file is a cube map or a volume.
    fn header(width: u32, height: u32, depth: u32, caps2: u32, format: u32) -> Vec<u8> {
        let blocks = width.div_ceil(4) * height.div_ceil(4);
        let mut header = Vec::new();
        header.extend_from_slice(b"DDS ");
        let mut field = |value: u32| header.extend_from_slice(&value.to_le_bytes());
        field(124); // header size
        let has_depth = if depth > 1 { 0x0080_0000 } else { 0 };
        field(0x0008_1007 | has_depth); // caps, height, width, pixel format, linear size
        field(height);
        field(width);
        field(blocks.saturating_mul(8)); // linear size
        field(depth);
        field(0); // mipmap count
        for _ in 0..11 {
            field(0);
        }
        field(32); // pixel format size
        field(0x4); // four-character code
        field(format);
        for _ in 0..5 {
            field(0); // bit count and masks
        }
        field(0x1000); // texture
        field(caps2);
        for _ in 0..3 {
            field(0);
        }
        assert_eq!(header.len(), 128);
        header
    }

    /// Counts the bytes read through it.
    struct Counted<R> {
        inner: R,
        read: usize,
    }

    impl<R: std::io::Read> std::io::Read for Counted<R> {
        fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
            let read = self.inner.read(buffer)?;
            self.read += read;
            Ok(read)
        }
    }

    #[test]
    fn decodes_dds_bc1() {
        let folder = Folder::new("dds");
        let mut file = bc1_header(4, 4);
        // One block: colour 0 red, colour 1 blue, every pixel colour 0.
        file.extend_from_slice(&RED);
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

    /// A cube map holds six pictures, a volume one for every slice and a
    /// texture array as many as it says. The first is the one shown.
    /// Decoding all of them would take their number times the memory the
    /// budget allows for one.
    #[test]
    fn a_file_of_many_pictures_shows_its_first() {
        let folder = Folder::new("dds-many");
        let mut cube = header(4, 4, 1, CUBE_MAP, DXT1);
        cube.extend_from_slice(&RED);
        cube.extend_from_slice(&BLUE.repeat(5));
        let mut volume = header(4, 4, 3, VOLUME, DXT1);
        volume.extend_from_slice(&RED);
        volume.extend_from_slice(&BLUE.repeat(2));
        // A texture array says how many pictures it holds in a second header.
        let mut array = header(4, 4, 1, 0, u32::from_le_bytes(*b"DX10"));
        for field in [BC1_UNORM, TEXTURE_2D, 0, 3, 0] {
            array.extend_from_slice(&field.to_le_bytes());
        }
        array.extend_from_slice(&RED);
        array.extend_from_slice(&BLUE.repeat(2));

        for (name, file) in [
            ("cube.dds", cube),
            ("volume.dds", volume),
            ("array.dds", array),
        ] {
            let decoded = decode(&folder.write(name, &file), "dds", PREVIEW).unwrap();

            assert_eq!(decoded.full_size, (4, 4), "{name}");
            assert!(
                decoded
                    .image
                    .to_rgba8()
                    .pixels()
                    .all(|pixel| pixel.0 == [255, 0, 0, 255]),
                "{name}"
            );
        }
    }

    /// What follows the first picture (its smaller copies, the other faces,
    /// or whatever a hostile file is padded with) is not read, so the file's
    /// length does not matter.
    #[test]
    fn nothing_far_behind_the_first_picture_is_read() {
        let mut file = bc1_header(4, 4);
        file.extend_from_slice(&RED);
        file.resize(file.len() + (1 << 20), 0);
        let mut counted = Counted {
            inner: file.as_slice(),
            read: 0,
        };

        let decoded = super::decode_reader(&mut counted, file.len() as u64).unwrap();

        assert_eq!(decoded.full_size, (4, 4));
        assert!(counted.read <= 1024, "{} bytes were read", counted.read);
    }

    /// Float pixels can fit the budget once decoded and still take more of
    /// the file than is read: 2 GiB here, for a picture of 512 MiB.
    #[test]
    fn a_first_picture_longer_than_the_budget_answers_too_large() {
        let folder = Folder::new("dds-floats");
        let path = folder.write("floats.dds", &header(16_384, 8_192, 1, 0, FLOATS));

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

        assert!(
            matches!(no_data, Some(RenderError::Decode(_))),
            "{no_data:?}"
        );
        assert!(matches!(text, Some(RenderError::Decode(_))), "{text:?}");
    }
}
