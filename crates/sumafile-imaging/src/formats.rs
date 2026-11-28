//! The image formats the media process decodes, and the video extensions the
//! video process makes thumbnails of. Both lists are generated from
//! `ipc/schema/v1/media-formats.json`, which the app's own lists are
//! generated from too, so the two never disagree.

#[path = "formats_generated.rs"]
mod generated;

pub use generated::{MEDIA_PROCESS_EXTENSIONS, VIDEO_EXTENSIONS};

/// True for an extension of a format the media process decodes. Any case,
/// with or without a leading dot.
pub fn is_media_process_extension(extension: &str) -> bool {
    let extension = extension
        .trim()
        .trim_start_matches('.')
        .to_ascii_lowercase();
    MEDIA_PROCESS_EXTENSIONS
        .binary_search(&extension.as_str())
        .is_ok()
}

/// True for a video extension. Any case, with or without a leading dot.
pub fn is_video_extension(extension: &str) -> bool {
    let extension = extension
        .trim()
        .trim_start_matches('.')
        .to_ascii_lowercase();
    VIDEO_EXTENSIONS.binary_search(&extension.as_str()).is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_list_matches_the_schema_file() {
        let schema = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../ipc/schema/v1/media-formats.json");
        let schema: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(schema).unwrap()).unwrap();
        let mut expected: Vec<String> = schema["formats"]
            .as_array()
            .unwrap()
            .iter()
            .flat_map(|format| format["extensions"].as_array().unwrap().iter())
            .map(|extension| extension.as_str().unwrap().to_string())
            .collect();
        expected.sort();

        assert!(!expected.is_empty());
        assert_eq!(MEDIA_PROCESS_EXTENSIONS, expected);
    }

    #[test]
    fn the_video_list_is_sorted_and_holds_what_the_schema_lists() {
        let schema = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../ipc/schema/v1/media-formats.json");
        let schema: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(schema).unwrap()).unwrap();
        let mut expected: Vec<String> = schema["video"]
            .as_array()
            .unwrap()
            .iter()
            .map(|extension| extension.as_str().unwrap().to_string())
            .collect();
        expected.sort();

        assert_eq!(expected.len(), 30);
        assert_eq!(VIDEO_EXTENSIONS, expected);
        assert!(is_video_extension("mp4"));
        assert!(is_video_extension(".M2TS"));
        assert!(!is_video_extension("psd"));
        assert!(!is_video_extension("ts"));
        assert!(!is_video_extension(""));
    }

    #[test]
    fn no_extension_is_both_a_picture_and_a_video() {
        for extension in VIDEO_EXTENSIONS {
            assert!(!is_media_process_extension(extension), "{extension}");
        }
    }

    #[test]
    fn lookup_ignores_case_and_a_leading_dot() {
        assert!(is_media_process_extension("psd"));
        assert!(is_media_process_extension(".PSD"));
        assert!(is_media_process_extension("Cr2"));
        assert!(!is_media_process_extension("jpg"));
        assert!(!is_media_process_extension("heic"));
        assert!(!is_media_process_extension(""));
    }
}
