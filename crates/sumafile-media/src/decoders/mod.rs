//! One decoder per format, picked by the file's extension.

mod dds;
mod exr;
mod jxl;
mod kra;
mod psd;
mod raw;
mod simple;
mod svg;
mod tiff;

use crate::render::{Purpose, RenderError};
use std::fs::File;
use std::io::BufReader;
use std::path::Path;

pub use sumafile_imaging::thumbnail::Decoded;

/// Decode `path`, whose lowercase extension is `extension`, for `purpose`.
/// A decoder may return less than the whole picture when the purpose needs
/// less: an embedded preview for a thumbnail, for example.
pub fn decode(path: &Path, extension: &str, purpose: Purpose) -> Result<Decoded, RenderError> {
    match extension {
        "tga" | "qoi" | "pnm" | "pbm" | "pgm" | "ppm" | "pam" | "hdr" => {
            simple::decode(path, extension)
        }
        "exr" => exr::decode(path),
        "tif" | "tiff" => tiff::decode(path),
        "jxl" => jxl::decode(path),
        "svg" => svg::decode(path, purpose),
        "dds" => dds::decode(path),
        "psd" | "psb" => psd::decode(path, purpose),
        "kra" | "ora" => kra::decode(path),
        "dng" | "nef" | "nrw" | "cr2" | "cr3" | "arw" | "srf" | "sr2" | "pef" | "srw" | "orf"
        | "rw2" | "raf" | "erf" | "3fr" | "mos" | "dcr" | "kdc" => raw::decode(path, purpose),
        _ => Err(RenderError::Unsupported(format!(
            "SumaFile has no decoder for .{extension} files."
        ))),
    }
}

pub(crate) fn open(path: &Path) -> Result<BufReader<File>, RenderError> {
    File::open(path)
        .map(BufReader::new)
        .map_err(|error| RenderError::Io(format!("Cannot open {}: {error}", path.display())))
}

/// A whole picture as its decoder gave it.
pub(crate) fn whole(image: image::DynamicImage) -> Decoded {
    Decoded {
        full_size: (image.width(), image.height()),
        image,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The app and the file service send a file here because its extension
    /// is on the generated list. Every extension on it must have a decoder.
    #[test]
    fn every_listed_format_has_a_decoder() {
        let missing = Path::new("this-file-does-not-exist");
        for extension in sumafile_imaging::formats::MEDIA_PROCESS_EXTENSIONS {
            let error = decode(missing, extension, Purpose::Thumbnail { size: 64 }).err();
            assert!(matches!(error, Some(RenderError::Io(_))), ".{extension}: {error:?}");
        }
    }
}
