//! SumaFile's video process. It answers `video_thumbnail` requests from the
//! file service on its standard input and output, in the wire format the
//! media process speaks (see `sumafile-media-client`).
//!
//! It is a program of its own, apart from the media process, for two
//! reasons. A video that hangs a decoder or crashes a graphics driver ends
//! only this process, and picture thumbnails carry on. And it needs Media
//! Foundation, which some Windows editions ship without: there it does not
//! start, and the picture formats are not lost with it.

pub mod attempt;
pub mod pixels;
pub mod rules;

use std::path::Path;
use sumafile_media::render::RenderError;
use sumafile_media::server::Answered;
use sumafile_media_client::protocol::{Method, Request};

const THUMBNAIL_SIZES: std::ops::RangeInclusive<u32> = 16..=1024;
const DEFAULT_THUMBNAIL_SIZE: u32 = 256;
const DEFAULT_POSITION_PERCENT: u8 = 25;

/// Answer one request from the file service.
pub fn handle(request: &Request) -> Result<Answered, RenderError> {
    if request.method != Method::VideoThumbnail {
        return Err(RenderError::Unsupported(
            "The video process makes video thumbnails only.".to_string(),
        ));
    }
    let size = request
        .size
        .unwrap_or(DEFAULT_THUMBNAIL_SIZE)
        .clamp(*THUMBNAIL_SIZES.start(), *THUMBNAIL_SIZES.end());
    thumbnail(
        Path::new(&request.path),
        size,
        request.position_percent.unwrap_or(DEFAULT_POSITION_PERCENT),
        request.hardware.unwrap_or(true),
    )
}

fn thumbnail(
    _path: &Path,
    _box_side: u32,
    _position_percent: u8,
    _hardware: bool,
) -> Result<Answered, RenderError> {
    Err(RenderError::Unsupported(
        "Video thumbnails need Windows.".to_string(),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn request(method: Method) -> Request {
        Request {
            id: 1,
            method,
            path: "clip.mp4".to_string(),
            size: Some(256),
            max_dimension: None,
            position_percent: Some(25),
            hardware: Some(true),
        }
    }

    #[test]
    fn other_methods_are_unsupported() {
        for method in [Method::Thumbnail, Method::Preview] {
            let answer = handle(&request(method));

            assert!(
                matches!(&answer, Err(RenderError::Unsupported(message)) if message.contains("video thumbnails only")),
                "{answer:?}"
            );
        }
    }

    #[cfg(not(windows))]
    #[test]
    fn video_needs_windows() {
        let answer = handle(&request(Method::VideoThumbnail));

        assert!(
            matches!(&answer, Err(RenderError::Unsupported(message)) if message == "Video thumbnails need Windows."),
            "{answer:?}"
        );
    }
}
