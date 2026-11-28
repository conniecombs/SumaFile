//! One decoder per format, picked by the file's extension.

mod dds;
mod exr;
mod jxl;
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
