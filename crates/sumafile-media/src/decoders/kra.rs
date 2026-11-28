//! Krita documents and OpenRaster files. Both are zip archives that keep a
//! PNG of the whole picture, `mergedimage.png`, beside their layers.

use super::simple::decode_reader;
use super::{open, Decoded};
use crate::render::RenderError;
use std::io::Read;
use std::path::Path;

const MERGED_IMAGE: &str = "mergedimage.png";
/// The most the merged image may take as a PNG. Its pixels then go through
/// the pixel budget like any other picture's.
const MAX_PNG_BYTES: u64 = 512 * 1024 * 1024;

pub(super) fn decode(path: &Path) -> Result<Decoded, RenderError> {
    let failed = |what: &dyn std::fmt::Display| {
        RenderError::Decode(format!("The document could not be decoded: {what}"))
    };
    let mut archive = zip::ZipArchive::new(open(path)?).map_err(|error| failed(&error))?;
    let entry = archive
        .by_name(MERGED_IMAGE)
        .map_err(|_| failed(&"it holds no picture of the whole document"))?;
    if entry.size() > MAX_PNG_BYTES {
        return Err(RenderError::TooLarge);
    }

    // The size an entry declares is only a claim: read no more than the limit.
    let mut png = Vec::new();
    entry
        .take(MAX_PNG_BYTES + 1)
        .read_to_end(&mut png)
        .map_err(|error| failed(&error))?;
    if png.len() as u64 > MAX_PNG_BYTES {
        return Err(RenderError::TooLarge);
    }
    decode_reader(std::io::Cursor::new(png), image::ImageFormat::Png)
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;
    use image::{Rgb, RgbImage};
    use std::io::Write;

    const PREVIEW: Purpose = Purpose::Preview {
        max_dimension: 2560,
    };

    fn picture() -> RgbImage {
        RgbImage::from_fn(4, 3, |x, y| Rgb([(x * 60) as u8, (y * 100) as u8, 200]))
    }

    fn archive(entries: &[(&str, Vec<u8>)]) -> Vec<u8> {
        let mut zip = zip::ZipWriter::new(std::io::Cursor::new(Vec::new()));
        let stored = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Stored);
        for (name, bytes) in entries {
            zip.start_file(*name, stored).unwrap();
            zip.write_all(bytes).unwrap();
        }
        zip.finish().unwrap().into_inner()
    }

    fn png() -> Vec<u8> {
        let mut png = Vec::new();
        picture()
            .write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png)
            .unwrap();
        png
    }

    #[test]
    fn decodes_kra_and_ora() {
        let folder = Folder::new("kra");
        let document = archive(&[
            ("mimetype", b"application/x-krita".to_vec()),
            ("maindoc.xml", b"<DOC/>".to_vec()),
            ("mergedimage.png", png()),
        ]);

        for extension in ["kra", "ora"] {
            let path = folder.write(&format!("painting.{extension}"), &document);

            let decoded = decode(&path, extension, PREVIEW).unwrap();

            assert_eq!(decoded.full_size, (4, 3));
            assert_eq!(decoded.image.to_rgb8(), picture());
        }
    }

    #[test]
    fn a_document_without_its_merged_image_answers_decode() {
        let folder = Folder::new("kra-empty");
        let no_picture = folder.write(
            "layers-only.kra",
            &archive(&[("maindoc.xml", b"<DOC/>".to_vec())]),
        );
        let not_a_png = folder.write(
            "odd.kra",
            &archive(&[("mergedimage.png", b"not a PNG".to_vec())]),
        );
        let text = folder.write("text.kra", b"not a zip archive");

        for path in [no_picture, not_a_png, text] {
            let error = decode(&path, "kra", PREVIEW).err();
            assert!(
                matches!(error, Some(RenderError::Decode(_))),
                "{path:?}: {error:?}"
            );
        }
    }
}
