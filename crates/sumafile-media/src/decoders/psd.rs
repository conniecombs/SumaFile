//! Photoshop documents, PSD and PSB, by SumaFile's own reader.
//!
//! A document holds, after its layers, the picture as Photoshop last drew it
//! (the flattened image), and usually a small JPEG thumbnail among its image
//! resources. This reader takes those two and skips the layers without
//! loading them, so a multi-gigabyte document costs what its flattened image
//! costs. Libraries that parse the layers were left aside for that reason,
//! and because they do not read PSB.
//!
//! Layout (big-endian): header, colour mode data, image resources, layer and
//! mask information, image data. PSB differs in the version number, in the
//! length of the layer section (8 bytes for 4) and in the row lengths of
//! RLE-compressed image data (4 bytes for 2).

use super::{open, Decoded};
use crate::limits::check_budget;
use crate::render::{Purpose, RenderError};
use crate::tonemap::tonemap_f32;
use image::{DynamicImage, GrayAlphaImage, GrayImage, RgbImage, RgbaImage};
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

/// The image resource that holds the thumbnail Photoshop saves (5.0 and later).
const THUMBNAIL_RESOURCE: u16 = 1036;
/// The thumbnail resource's own header, before its JPEG.
const THUMBNAIL_HEADER_BYTES: u32 = 28;
const MAX_THUMBNAIL_BYTES: u32 = 16 * 1024 * 1024;
/// Photoshop's own limits: a PSB is at most this many pixels a side, and a
/// document has at most this many channels.
const MAX_SIDE: u32 = 300_000;
const MAX_CHANNELS: u16 = 56;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Mode {
    Bitmap,
    Grey,
    Indexed,
    Rgb,
    Cmyk,
}

impl Mode {
    /// How many channels hold the colour; any further ones are alpha
    /// channels and saved selections.
    fn colour_channels(self) -> usize {
        match self {
            Self::Bitmap | Self::Grey | Self::Indexed => 1,
            Self::Rgb => 3,
            Self::Cmyk => 4,
        }
    }
}

struct Header {
    psb: bool,
    channels: u16,
    width: u32,
    height: u32,
    depth: u16,
    mode: Mode,
}

fn damaged(what: &str) -> RenderError {
    RenderError::Decode(format!(
        "The Photoshop document could not be decoded: {what}."
    ))
}

fn ends_early(_error: std::io::Error) -> RenderError {
    damaged("the file ends early")
}

fn bytes<const N: usize>(input: &mut impl Read) -> Result<[u8; N], RenderError> {
    let mut block = [0u8; N];
    input.read_exact(&mut block).map_err(ends_early)?;
    Ok(block)
}

fn u16_of(input: &mut impl Read) -> Result<u16, RenderError> {
    bytes(input).map(u16::from_be_bytes)
}

fn u32_of(input: &mut impl Read) -> Result<u32, RenderError> {
    bytes(input).map(u32::from_be_bytes)
}

/// A section length: 8 bytes in a PSB where a PSD has 4.
fn long_of(input: &mut impl Read, psb: bool) -> Result<u64, RenderError> {
    if psb {
        bytes(input).map(u64::from_be_bytes)
    } else {
        u32_of(input).map(u64::from)
    }
}

fn position(input: &mut impl Seek) -> Result<u64, RenderError> {
    input.stream_position().map_err(ends_early)
}

fn go_to(input: &mut impl Seek, offset: u64) -> Result<(), RenderError> {
    input.seek(SeekFrom::Start(offset)).map(drop).map_err(ends_early)
}

pub(super) fn decode(path: &Path, purpose: Purpose) -> Result<Decoded, RenderError> {
    let mut input = open(path)?;
    let header = read_header(&mut input)?;

    let palette_bytes = u64::from(u32_of(&mut input)?);
    let palette_end = position(&mut input)? + palette_bytes;
    let palette = if header.mode == Mode::Indexed {
        if palette_bytes < 768 {
            return Err(damaged("its colour table is missing"));
        }
        bytes::<768>(&mut input)?.to_vec()
    } else {
        Vec::new()
    };
    go_to(&mut input, palette_end)?;

    let resource_bytes = u64::from(u32_of(&mut input)?);
    let resources_end = position(&mut input)? + resource_bytes;
    if let Purpose::Thumbnail { size } = purpose {
        if let Some(preview) = embedded_thumbnail(&mut input, resource_bytes) {
            if preview.width().max(preview.height()) >= size {
                return Ok(Decoded {
                    image: preview,
                    full_size: (header.width, header.height),
                });
            }
        }
    }
    go_to(&mut input, resources_end)?;

    let layer_bytes = long_of(&mut input, header.psb)?;
    let layers_end = position(&mut input)? + layer_bytes;
    let transparency = has_merged_transparency(&mut input, &header, layer_bytes);
    go_to(&mut input, layers_end)?;

    let colours = header.mode.colour_channels();
    if usize::from(header.channels) < colours {
        return Err(damaged("it has fewer channels than its colour mode needs"));
    }
    let alpha = transparency
        && usize::from(header.channels) > colours
        && matches!(header.mode, Mode::Rgb | Mode::Grey);
    let planes = colours + usize::from(alpha);
    // An indexed picture becomes three channels; nothing takes more than four.
    let widest = if header.mode == Mode::Indexed { 3 } else { planes };
    check_budget(header.width, header.height, widest as u8, 1)?;

    let samples = read_image_data(&mut input, &header, planes)?;
    let (width, height) = (header.width, header.height);
    let image = match (header.mode, alpha) {
        (Mode::Rgb, false) => RgbImage::from_raw(width, height, samples).map(DynamicImage::ImageRgb8),
        (Mode::Rgb, true) => {
            RgbaImage::from_raw(width, height, samples).map(DynamicImage::ImageRgba8)
        }
        (Mode::Grey, true) => {
            GrayAlphaImage::from_raw(width, height, samples).map(DynamicImage::ImageLumaA8)
        }
        (Mode::Grey | Mode::Bitmap, false) => {
            GrayImage::from_raw(width, height, samples).map(DynamicImage::ImageLuma8)
        }
        (Mode::Cmyk, _) => {
            RgbImage::from_raw(width, height, inks_to_rgb(&samples)).map(DynamicImage::ImageRgb8)
        }
        (Mode::Indexed, _) => RgbImage::from_raw(width, height, look_up(&samples, &palette))
            .map(DynamicImage::ImageRgb8),
        (Mode::Bitmap, true) => None,
    };
    let image = image.ok_or_else(|| damaged("its image data does not match its size"))?;
    Ok(Decoded {
        full_size: (width, height),
        image,
    })
}

fn read_header(input: &mut impl Read) -> Result<Header, RenderError> {
    if &bytes::<4>(input)? != b"8BPS" {
        return Err(damaged("it is not a Photoshop document"));
    }
    let psb = match u16_of(input)? {
        1 => false,
        2 => true,
        _ => return Err(damaged("its version is not known")),
    };
    let _reserved = bytes::<6>(input)?;
    let channels = u16_of(input)?;
    let height = u32_of(input)?;
    let width = u32_of(input)?;
    let depth = u16_of(input)?;
    let mode = match u16_of(input)? {
        0 => Mode::Bitmap,
        // A duotone document stores a grey picture and how to print it.
        1 | 8 => Mode::Grey,
        2 => Mode::Indexed,
        3 => Mode::Rgb,
        4 => Mode::Cmyk,
        _ => return Err(damaged("its colour mode is not supported")),
    };

    let sides = 1..=MAX_SIDE;
    if !(1..=MAX_CHANNELS).contains(&channels)
        || !sides.contains(&width)
        || !sides.contains(&height)
    {
        return Err(damaged("its size is not valid"));
    }
    let depth_fits = match mode {
        Mode::Bitmap => depth == 1,
        Mode::Indexed => depth == 8,
        _ => matches!(depth, 8 | 16 | 32),
    };
    if !depth_fits {
        return Err(damaged("its bit depth is not supported"));
    }
    Ok(Header {
        psb,
        channels,
        width,
        height,
        depth,
        mode,
    })
}

/// The JPEG thumbnail among the image resources, which start at the input's
/// position and are `length` bytes long. `None` when there is none or it
/// cannot be read; the position afterwards is anywhere in the section.
pub(super) fn embedded_thumbnail<R: Read + Seek>(input: &mut R, length: u64) -> Option<DynamicImage> {
    let end = input.stream_position().ok()?.checked_add(length)?;
    loop {
        // Signature, id, an empty name and a size take 12 bytes.
        if input.stream_position().ok()?.checked_add(12)? > end {
            return None;
        }
        let _signature = bytes::<4>(input).ok()?;
        let id = u16_of(input).ok()?;
        // The name: a length byte and that many characters, padded to an even total.
        let name = i64::from(bytes::<1>(input).ok()?[0]);
        input.seek(SeekFrom::Current(name + (name + 1) % 2)).ok()?;
        let size = u32_of(input).ok()?;

        if id == THUMBNAIL_RESOURCE {
            if !(THUMBNAIL_HEADER_BYTES + 1..=MAX_THUMBNAIL_BYTES).contains(&size) {
                return None;
            }
            input
                .seek(SeekFrom::Current(i64::from(THUMBNAIL_HEADER_BYTES)))
                .ok()?;
            let mut jpeg = vec![0u8; (size - THUMBNAIL_HEADER_BYTES) as usize];
            input.read_exact(&mut jpeg).ok()?;
            return image::load_from_memory_with_format(&jpeg, image::ImageFormat::Jpeg).ok();
        }
        input
            .seek(SeekFrom::Current(i64::from(size) + i64::from(size % 2)))
            .ok()?;
    }
}

/// Photoshop records a negative layer count when the first channel after the
/// colours is the flattened image's transparency. Without that mark, a
/// further channel is a saved selection and says nothing about the picture.
fn has_merged_transparency<R: Read>(input: &mut R, header: &Header, layer_bytes: u64) -> bool {
    let length_bytes: u64 = if header.psb { 8 } else { 4 };
    if layer_bytes < length_bytes + 2 {
        return false;
    }
    let Ok(info_bytes) = long_of(input, header.psb) else {
        return false;
    };
    info_bytes >= 2 && bytes::<2>(input).is_ok_and(|count| i16::from_be_bytes(count) < 0)
}

/// The first `planes` channels of the flattened image as 8-bit samples,
/// pixel by pixel (`planes` samples each).
fn read_image_data<R: Read>(
    input: &mut R,
    header: &Header,
    planes: usize,
) -> Result<Vec<u8>, RenderError> {
    let (width, height) = (header.width as usize, header.height as usize);
    let row_bytes = match header.depth {
        1 => width.div_ceil(8),
        depth => width * usize::from(depth / 8),
    };
    let compressed = match u16_of(input)? {
        0 => false,
        1 => true,
        _ => return Err(damaged("its image data is compressed in a way SumaFile does not read")),
    };
    // Every row's packed length, channel after channel, comes before the rows.
    let row_lengths = if compressed {
        let rows = height * usize::from(header.channels);
        let mut lengths = Vec::with_capacity(rows);
        for _ in 0..rows {
            lengths.push(if header.psb {
                u32_of(input)? as usize
            } else {
                usize::from(u16_of(input)?)
            });
        }
        lengths
    } else {
        Vec::new()
    };
    // A packer may spend a count byte on every byte it writes. More than
    // that is not a packed row.
    let longest_packed = row_bytes * 2 + 2;

    let mut samples = vec![0u8; width * height * planes];
    let mut row = vec![0u8; row_bytes];
    let mut packed = Vec::new();
    for plane in 0..planes {
        for y in 0..height {
            if compressed {
                let length = row_lengths[plane * height + y];
                if length > longest_packed {
                    return Err(damaged("a row of its image data is too long"));
                }
                packed.resize(length, 0);
                input.read_exact(&mut packed).map_err(ends_early)?;
                unpack_bits(&packed, &mut row)?;
            } else {
                input.read_exact(&mut row).map_err(ends_early)?;
            }
            let pixels = samples[y * width * planes..].iter_mut().skip(plane).step_by(planes);
            match header.depth {
                // A set bit is black.
                1 => {
                    for (x, sample) in pixels.take(width).enumerate() {
                        let bit = row[x / 8] >> (7 - x % 8) & 1;
                        *sample = if bit == 1 { 0 } else { 255 };
                    }
                }
                8 => {
                    for (sample, value) in pixels.zip(&row) {
                        *sample = *value;
                    }
                }
                // Big-endian: the high byte comes first.
                16 => {
                    for (sample, value) in pixels.zip(row.iter().step_by(2)) {
                        *sample = *value;
                    }
                }
                _ => {
                    for (sample, value) in pixels.zip(row.as_chunks::<4>().0) {
                        *sample = tonemap_f32(f32::from_be_bytes(*value));
                    }
                }
            }
        }
    }
    Ok(samples)
}

/// PackBits: a count byte, then either that many bytes as they are or one
/// byte to repeat. The row must come out exactly full.
fn unpack_bits(packed: &[u8], row: &mut [u8]) -> Result<(), RenderError> {
    let overrun = || damaged("a row of its image data does not unpack to its width");
    let (mut read, mut written) = (0, 0);
    while read < packed.len() {
        let count = packed[read] as i8;
        read += 1;
        if count >= 0 {
            let length = count as usize + 1;
            let source = packed.get(read..read + length).ok_or_else(overrun)?;
            row.get_mut(written..written + length)
                .ok_or_else(overrun)?
                .copy_from_slice(source);
            read += length;
            written += length;
        } else if count != i8::MIN {
            let length = 1 - isize::from(count);
            let length = length as usize;
            let value = *packed.get(read).ok_or_else(overrun)?;
            row.get_mut(written..written + length)
                .ok_or_else(overrun)?
                .fill(value);
            read += 1;
            written += length;
        }
    }
    if written != row.len() {
        return Err(overrun());
    }
    Ok(())
}

/// Photoshop stores ink inverted: 255 is no ink.
fn inks_to_rgb(inks: &[u8]) -> Vec<u8> {
    let mut colour = Vec::with_capacity(inks.len() / 4 * 3);
    for ink in inks.as_chunks::<4>().0 {
        let black = u32::from(ink[3]);
        for channel in &ink[..3] {
            colour.push((u32::from(*channel) * black / 255) as u8);
        }
    }
    colour
}

/// The colour table holds 256 reds, then 256 greens, then 256 blues.
fn look_up(indexes: &[u8], palette: &[u8]) -> Vec<u8> {
    let mut colour = Vec::with_capacity(indexes.len() * 3);
    for index in indexes {
        let index = usize::from(*index);
        colour.extend_from_slice(&[palette[index], palette[256 + index], palette[512 + index]]);
    }
    colour
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;
    use image::{DynamicImage, Rgb, RgbImage};

    const PREVIEW: Purpose = Purpose::Preview { max_dimension: 2560 };
    const RGB: u16 = 3;

    /// A Photoshop document to write, one plane of samples per channel.
    struct Document {
        psb: bool,
        mode: u16,
        depth: u16,
        width: u32,
        height: u32,
        planes: Vec<Vec<u8>>,
        rle: bool,
        palette: Option<Vec<u8>>,
        thumbnail: Option<Vec<u8>>,
        /// The layer count Photoshop records. Negative: the first channel
        /// after the colours is the flattened image's transparency.
        layer_count: Option<i16>,
    }

    impl Document {
        fn new(mode: u16, depth: u16, width: u32, height: u32, planes: Vec<Vec<u8>>) -> Self {
            Self {
                psb: false,
                mode,
                depth,
                width,
                height,
                planes,
                rle: false,
                palette: None,
                thumbnail: None,
                layer_count: None,
            }
        }

        fn bytes(&self) -> Vec<u8> {
            let mut file = Vec::new();
            file.extend_from_slice(b"8BPS");
            file.extend_from_slice(&(if self.psb { 2u16 } else { 1 }).to_be_bytes());
            file.extend_from_slice(&[0; 6]);
            file.extend_from_slice(&(self.planes.len() as u16).to_be_bytes());
            file.extend_from_slice(&self.height.to_be_bytes());
            file.extend_from_slice(&self.width.to_be_bytes());
            file.extend_from_slice(&self.depth.to_be_bytes());
            file.extend_from_slice(&self.mode.to_be_bytes());

            let palette = self.palette.clone().unwrap_or_default();
            file.extend_from_slice(&(palette.len() as u32).to_be_bytes());
            file.extend_from_slice(&palette);

            let mut resources = Vec::new();
            // A resource that is not the thumbnail, with an odd-length name
            // and odd-length data, so the padding rules are exercised.
            resources.extend_from_slice(b"8BIM");
            resources.extend_from_slice(&1005u16.to_be_bytes());
            resources.extend_from_slice(&[4, b'n', b'a', b'm', b'e', 0]);
            resources.extend_from_slice(&3u32.to_be_bytes());
            resources.extend_from_slice(&[1, 2, 3, 0]);
            if let Some(jpeg) = &self.thumbnail {
                resources.extend_from_slice(b"8BIM");
                resources.extend_from_slice(&1036u16.to_be_bytes());
                resources.extend_from_slice(&[0, 0]);
                let size = 28 + jpeg.len();
                resources.extend_from_slice(&(size as u32).to_be_bytes());
                resources.extend_from_slice(&1u32.to_be_bytes()); // JPEG
                resources.extend_from_slice(&[0; 24]);
                resources.extend_from_slice(jpeg);
                if size % 2 == 1 {
                    resources.push(0);
                }
            }
            file.extend_from_slice(&(resources.len() as u32).to_be_bytes());
            file.extend_from_slice(&resources);

            let mut layers = Vec::new();
            if let Some(count) = self.layer_count {
                // Layer info: its length, the layer count, then bytes a
                // reader must skip.
                let info = [count.to_be_bytes().as_slice(), &[0xAA; 10]].concat();
                if self.psb {
                    layers.extend_from_slice(&(info.len() as u64).to_be_bytes());
                } else {
                    layers.extend_from_slice(&(info.len() as u32).to_be_bytes());
                }
                layers.extend_from_slice(&info);
            }
            if self.psb {
                file.extend_from_slice(&(layers.len() as u64).to_be_bytes());
            } else {
                file.extend_from_slice(&(layers.len() as u32).to_be_bytes());
            }
            file.extend_from_slice(&layers);

            if self.rle {
                file.extend_from_slice(&1u16.to_be_bytes());
                let row_bytes = self.planes[0].len() / self.height as usize;
                let rows: Vec<Vec<u8>> = self
                    .planes
                    .iter()
                    .flat_map(|plane| plane.chunks(row_bytes))
                    .map(pack_bits)
                    .collect();
                for row in &rows {
                    if self.psb {
                        file.extend_from_slice(&(row.len() as u32).to_be_bytes());
                    } else {
                        file.extend_from_slice(&(row.len() as u16).to_be_bytes());
                    }
                }
                for row in &rows {
                    file.extend_from_slice(row);
                }
            } else {
                file.extend_from_slice(&0u16.to_be_bytes());
                for plane in &self.planes {
                    file.extend_from_slice(plane);
                }
            }
            file
        }
    }

    /// PackBits: runs of one byte as a repeat, everything else as it is.
    fn pack_bits(row: &[u8]) -> Vec<u8> {
        let mut packed = Vec::new();
        let mut index = 0;
        while index < row.len() {
            let run = row[index..]
                .iter()
                .take(128)
                .take_while(|byte| **byte == row[index])
                .count();
            if run >= 2 {
                packed.push((257 - run) as u8);
                packed.push(row[index]);
                index += run;
            } else {
                packed.push(0);
                packed.push(row[index]);
                index += 1;
            }
        }
        packed
    }

    /// 4 by 3, each pixel different, with a run of equal bytes in blue.
    fn picture() -> RgbImage {
        RgbImage::from_fn(4, 3, |x, y| Rgb([(x * 60) as u8, (y * 100 + x) as u8, 200]))
    }

    fn planes_of(picture: &RgbImage) -> Vec<Vec<u8>> {
        (0..3)
            .map(|channel| picture.pixels().map(|pixel| pixel.0[channel]).collect())
            .collect()
    }

    fn rgb_document() -> Document {
        Document::new(RGB, 8, 4, 3, planes_of(&picture()))
    }

    fn decoded(folder: &Folder, name: &str, document: &Document, purpose: Purpose) -> DynamicImage {
        decode(&folder.write(name, &document.bytes()), "psd", purpose)
            .unwrap()
            .image
    }

    #[test]
    fn decodes_a_raw_rgb_psd() {
        let folder = Folder::new("psd-raw");
        let path = folder.write("flat.psd", &rgb_document().bytes());

        let decoded = decode(&path, "psd", PREVIEW).unwrap();

        assert_eq!(decoded.full_size, (4, 3));
        assert_eq!(decoded.image.to_rgb8(), picture());
    }

    #[test]
    fn decodes_an_rle_psd() {
        let folder = Folder::new("psd-rle");
        let document = Document {
            rle: true,
            ..rgb_document()
        };

        assert_eq!(decoded(&folder, "packed.psd", &document, PREVIEW).to_rgb8(), picture());
    }

    #[test]
    fn decodes_a_psb_raw_and_rle() {
        let folder = Folder::new("psb");
        for rle in [false, true] {
            let document = Document {
                psb: true,
                rle,
                layer_count: Some(2),
                ..rgb_document()
            };
            let path = folder.write("large.psb", &document.bytes());

            let decoded = decode(&path, "psb", PREVIEW).unwrap();

            assert_eq!(decoded.image.to_rgb8(), picture(), "rle: {rle}");
        }
    }

    #[test]
    fn decodes_greyscale_cmyk_and_indexed() {
        let folder = Folder::new("psd-modes");
        let grey = Document::new(1, 8, 2, 1, vec![vec![0, 200]]);
        // Photoshop stores ink inverted: 255 is no ink. No cyan, full
        // magenta and yellow, no black: red.
        let cmyk = Document::new(4, 8, 1, 1, vec![vec![255], vec![0], vec![0], vec![255]]);
        let mut palette = vec![0u8; 768];
        (palette[1], palette[257], palette[513]) = (10, 20, 30);
        let indexed = Document {
            palette: Some(palette),
            ..Document::new(2, 8, 2, 1, vec![vec![1, 0]])
        };

        let grey = decoded(&folder, "grey.psd", &grey, PREVIEW).to_luma8();
        let cmyk = decoded(&folder, "cmyk.psd", &cmyk, PREVIEW).to_rgb8();
        let indexed = decoded(&folder, "indexed.psd", &indexed, PREVIEW).to_rgb8();

        assert_eq!(grey.into_raw(), [0, 200]);
        assert_eq!(cmyk.into_raw(), [255, 0, 0]);
        assert_eq!(indexed.into_raw(), [10, 20, 30, 0, 0, 0]);
    }

    #[test]
    fn decodes_16_bit_by_its_high_byte() {
        let folder = Folder::new("psd-16");
        let plane = |high: u8| vec![high, 0x12, 0xFF, 0xFF];
        let document = Document::new(RGB, 16, 2, 1, vec![plane(0x80), plane(0x00), plane(0x40)]);

        let pixels = decoded(&folder, "deep.psd", &document, PREVIEW).to_rgb8();

        assert_eq!(pixels.into_raw(), [0x80, 0x00, 0x40, 0xFF, 0xFF, 0xFF]);
    }

    /// A fourth channel is the picture's transparency only when the document
    /// says so. Otherwise it is a saved selection, and showing it as
    /// transparency would punch holes in the picture.
    #[test]
    fn a_fourth_channel_is_alpha_only_when_the_document_says_so() {
        let folder = Folder::new("psd-alpha");
        let mut planes = planes_of(&picture());
        planes.push(vec![64; 12]);
        let transparency = Document {
            layer_count: Some(-1),
            ..Document::new(RGB, 8, 4, 3, planes.clone())
        };
        let selection = Document {
            layer_count: Some(1),
            ..Document::new(RGB, 8, 4, 3, planes.clone())
        };
        let unsaid = Document::new(RGB, 8, 4, 3, planes);

        let transparency = decoded(&folder, "glass.psd", &transparency, PREVIEW);
        let selection = decoded(&folder, "selection.psd", &selection, PREVIEW);
        let unsaid = decoded(&folder, "unsaid.psd", &unsaid, PREVIEW);

        assert!(transparency.to_rgba8().pixels().all(|pixel| pixel.0[3] == 64));
        assert_eq!(transparency.to_rgb8(), picture());
        assert!(!selection.color().has_alpha());
        assert!(!unsaid.color().has_alpha());
        assert_eq!(selection.to_rgb8(), picture());
    }

    #[test]
    fn a_thumbnail_uses_the_embedded_preview_when_it_is_large_enough() {
        let folder = Folder::new("psd-thumb");
        let mut jpeg = Vec::new();
        RgbImage::from_pixel(64, 48, Rgb([255, 0, 0]))
            .write_to(&mut std::io::Cursor::new(&mut jpeg), image::ImageFormat::Jpeg)
            .unwrap();
        let blue = RgbImage::from_pixel(128, 96, Rgb([0, 0, 255]));
        let document = Document {
            thumbnail: Some(jpeg),
            ..Document::new(RGB, 8, 128, 96, planes_of(&blue))
        };
        let path = folder.write("art.psd", &document.bytes());
        let is = |image: &DynamicImage, colour: [u8; 3]| {
            let pixel = image.to_rgb8().get_pixel(5, 5).0;
            pixel.iter().zip(colour).all(|(have, want)| have.abs_diff(want) < 8)
        };

        let small = decode(&path, "psd", Purpose::Thumbnail { size: 48 }).unwrap();
        let large = decode(&path, "psd", Purpose::Thumbnail { size: 100 }).unwrap();
        let preview = decode(&path, "psd", PREVIEW).unwrap();

        assert!(is(&small.image, [255, 0, 0]), "the embedded preview");
        assert_eq!((small.image.width(), small.image.height()), (64, 48));
        assert_eq!(small.full_size, (128, 96), "the document's size, for the shape");
        assert!(is(&large.image, [0, 0, 255]), "64 pixels are too few for 100");
        assert!(is(&preview.image, [0, 0, 255]));
    }

    #[test]
    fn an_unsupported_colour_mode_answers_decode() {
        let folder = Folder::new("psd-lab");
        let lab = Document::new(9, 8, 1, 1, vec![vec![0], vec![0], vec![0]]);
        let path = folder.write("lab.psd", &lab.bytes());

        let error = decode(&path, "psd", PREVIEW).err();

        assert!(matches!(error, Some(RenderError::Decode(_))), "{error:?}");
    }

    #[test]
    fn a_document_over_budget_answers_too_large() {
        let folder = Folder::new("psd-huge");
        let mut huge = Document::new(RGB, 8, 300_000, 300_000, vec![vec![], vec![], vec![]]);
        huge.psb = true;
        let mut bytes = huge.bytes();
        bytes.truncate(bytes.len() - 2); // the header and sections, no image data
        let path = folder.write("huge.psb", &bytes);

        let error = decode(&path, "psb", PREVIEW).err();

        assert!(matches!(error, Some(RenderError::TooLarge)), "{error:?}");
    }

    #[test]
    fn a_damaged_document_answers_decode() {
        let folder = Folder::new("psd-cut");
        let whole = Document {
            rle: true,
            ..rgb_document()
        }
        .bytes();
        let cases = [
            ("cut.psd", whole[..whole.len() - 5].to_vec()),
            ("header.psd", whole[..20].to_vec()),
            ("text.psd", b"no Photoshop document here".to_vec()),
            ("version.psd", [b"8BPS\x00\x07".as_slice(), &whole[6..]].concat()),
        ];

        for (name, bytes) in cases {
            let error = decode(&folder.write(name, &bytes), "psd", PREVIEW).err();
            assert!(matches!(error, Some(RenderError::Decode(_))), "{name}: {error:?}");
        }
    }
}
