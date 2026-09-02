use crate::models::{FilePreview, ThumbnailResult};

use crate::utils::resolve_readable_path;
use std::fs;

pub fn read_file_preview(path: String, max_size: Option<u64>) -> Result<FilePreview, String> {
    let path_buf = resolve_readable_path(&path)?;
    if path_buf.is_dir() {
        return Err("Cannot preview a directory".to_string());
    }

    let metadata = fs::metadata(&path_buf).map_err(|e| format!("Failed to get metadata: {e}"))?;
    let size = metadata.len();
    // Cap at 10 MB to prevent memory exhaustion from a malicious/buggy frontend
    const MAX_ALLOWED: u64 = 10 * 1024 * 1024;
    let max_preview_size = max_size.unwrap_or(1024 * 1024).min(MAX_ALLOWED);
    let extension = path_buf
        .extension()
        .map(|e| e.to_string_lossy().to_lowercase())
        .unwrap_or_default();

    let (file_type, mime_type) = if let Some(known) = classify_known_extension(&extension) {
        known
    } else if size <= max_preview_size {
        // Only read the first 8KB to detect binary content instead of the entire file.
        let detect_size = std::cmp::min(size, 8192) as usize;
        if let Ok(mut file) = fs::File::open(&path_buf) {
            use std::io::Read;
            let mut buffer = vec![0u8; detect_size];
            if let Ok(bytes_read) = file.read(&mut buffer) {
                buffer.truncate(bytes_read);
                if buffer
                    .iter()
                    .all(|&b| b != 0 && (b >= 32 || b == 9 || b == 10 || b == 13))
                {
                    ("text", "text/plain".to_string())
                } else {
                    ("binary", "application/octet-stream".to_string())
                }
            } else {
                ("unsupported", "application/octet-stream".to_string())
            }
        } else {
            ("unsupported", "application/octet-stream".to_string())
        }
    } else {
        ("unsupported", "application/octet-stream".to_string())
    };

    let (content, encoding) = match file_type {
        "text" => {
            if size > max_preview_size {
                let mut file =
                    fs::File::open(&path_buf).map_err(|e| format!("Failed to open file: {e}"))?;
                let mut buffer = vec![0u8; max_preview_size as usize];
                use std::io::Read;
                let bytes_read = file
                    .read(&mut buffer)
                    .map_err(|e| format!("Failed to read file: {e}"))?;
                buffer.truncate(bytes_read);
                let text = String::from_utf8_lossy(&buffer).to_string();
                (
                    Some(text + "\n\n[File truncated...]"),
                    Some("utf-8".to_string()),
                )
            } else {
                let text = fs::read_to_string(&path_buf)
                    .map_err(|e| format!("Failed to read file: {e}"))?;
                (Some(text), Some("utf-8".to_string()))
            }
        }
        "image" => {
            if size > max_preview_size * 5 {
                (None, None)
            } else {
                let bytes = fs::read(&path_buf).map_err(|e| format!("Failed to read file: {e}"))?;
                use base64::{engine::general_purpose, Engine as _};
                let base64 = general_purpose::STANDARD.encode(&bytes);
                (Some(base64), Some("base64".to_string()))
            }
        }
        _ => (None, None),
    };

    Ok(FilePreview {
        file_type: file_type.to_string(),
        content,
        mime_type,
        size,
        encoding,
    })
}

pub fn generate_thumbnail(path: String, size: Option<u32>) -> Result<String, String> {
    use base64::{engine::general_purpose, Engine as _};

    let path_buf = resolve_readable_path(&path)?;
    let extension = path_buf
        .extension()
        .map(|e| e.to_string_lossy().to_lowercase())
        .unwrap_or_default();
    let supported = matches!(
        extension.as_str(),
        "jpg" | "jpeg" | "png" | "gif" | "webp" | "bmp"
    );
    if !supported {
        return Err(format!("Unsupported image format: {extension}"));
    }

    let thumb_size = size.unwrap_or(128);
    let img = image::open(&path_buf).map_err(|e| format!("Failed to open image: {e}"))?;
    // Let the image library handle aspect-ratio-preserving resize
    let thumbnail = img.thumbnail(thumb_size, thumb_size);
    let mut buffer = std::io::Cursor::new(Vec::new());
    thumbnail
        .write_to(&mut buffer, image::ImageFormat::Jpeg)
        .map_err(|e| format!("Failed to encode thumbnail: {e}"))?;
    let base64_thumb = general_purpose::STANDARD.encode(buffer.into_inner());
    Ok(base64_thumb)
}

pub fn generate_thumbnails(paths: Vec<String>, size: Option<u32>) -> Vec<ThumbnailResult> {
    const MAX_PARALLEL: usize = 4;
    let chunks: Vec<Vec<String>> = paths
        .chunks(MAX_PARALLEL.max(1))
        .map(|chunk| chunk.to_vec())
        .collect();
    let mut results = Vec::with_capacity(paths.len());
    for chunk in chunks {
        let batch: Vec<ThumbnailResult> = std::thread::scope(|scope| {
            let handles: Vec<_> = chunk
                .into_iter()
                .map(|path| {
                    scope.spawn(move || match generate_thumbnail(path.clone(), size) {
                        Ok(data) => ThumbnailResult {
                            path,
                            data: Some(data),
                            error: None,
                        },
                        Err(e) => ThumbnailResult {
                            path,
                            data: None,
                            error: Some(e),
                        },
                    })
                })
                .collect();
            handles.into_iter().map(|h| h.join().unwrap()).collect()
        });
        results.extend(batch);
    }
    results
}

fn classify_known_extension(extension: &str) -> Option<(&'static str, String)> {
    let extension = extension.to_ascii_lowercase();
    let mime = |value: &'static str| value.to_string();
    match extension.as_str() {
        "txt" => Some(("text", mime("text/plain"))),
        "md" | "markdown" => Some(("text", mime("text/markdown"))),
        "json" | "jsonc" | "map" => Some(("text", mime("application/json"))),
        "xml" | "xaml" => Some(("text", mime("application/xml"))),
        "yaml" | "yml" => Some(("text", mime("application/yaml"))),
        "toml" | "ini" | "cfg" | "conf" | "config" | "properties" | "env" | "editorconfig"
        | "gitignore" | "gitattributes" | "npmrc" | "log" | "srt" | "vtt" => {
            Some(("text", mime("text/plain")))
        }
        "csv" => Some(("text", mime("text/csv"))),
        "tsv" => Some(("text", mime("text/tab-separated-values"))),
        "html" | "htm" => Some(("text", mime("text/html"))),
        "css" => Some(("text", mime("text/css"))),
        "scss" | "sass" | "less" => Some(("text", format!("text/x-{extension}"))),
        "rs" | "js" | "mjs" | "cjs" | "ts" | "jsx" | "tsx" | "py" | "rb" | "go" | "java" | "c"
        | "cc" | "cpp" | "cxx" | "h" | "hh" | "hpp" | "hxx" | "cs" | "php" | "swift" | "kt"
        | "kts" | "scala" | "sh" | "bash" | "zsh" | "fish" | "ps1" | "bat" | "cmd" | "sql"
        | "r" | "lua" | "pl" | "pm" | "perl" | "ex" | "exs" | "erl" | "hrl" | "fs" | "fsx"
        | "fsi" | "vb" | "clj" | "cljs" | "groovy" | "gradle" | "dart" | "vue" | "svelte"
        | "astro" => Some(("text", format!("text/x-{extension}"))),
        "png" => Some(("image", mime("image/png"))),
        "jpg" | "jpeg" => Some(("image", mime("image/jpeg"))),
        "gif" => Some(("image", mime("image/gif"))),
        "webp" => Some(("image", mime("image/webp"))),
        "bmp" => Some(("image", mime("image/bmp"))),
        "svg" => Some(("image", mime("image/svg+xml"))),
        "ico" | "cur" => Some(("image", mime("image/x-icon"))),
        "tif" | "tiff" => Some(("image", mime("image/tiff"))),
        "heic" | "heif" => Some(("image", mime("image/heif"))),
        "avif" => Some(("image", mime("image/avif"))),
        "jxl" => Some(("image", mime("image/jxl"))),
        "pdf" => Some(("pdf", mime("application/pdf"))),
        "mp3" => Some(("audio", mime("audio/mpeg"))),
        "wav" => Some(("audio", mime("audio/wav"))),
        "flac" => Some(("audio", mime("audio/flac"))),
        "aac" => Some(("audio", mime("audio/aac"))),
        "m4a" => Some(("audio", mime("audio/mp4"))),
        "ogg" | "oga" => Some(("audio", mime("audio/ogg"))),
        "opus" => Some(("audio", mime("audio/opus"))),
        "wma" => Some(("audio", mime("audio/x-ms-wma"))),
        "aiff" | "aif" => Some(("audio", mime("audio/aiff"))),
        "mid" | "midi" => Some(("audio", mime("audio/midi"))),
        "wv" => Some(("audio", mime("audio/x-wavpack"))),
        "ape" => Some(("audio", mime("audio/ape"))),
        "mp4" | "m4v" => Some(("video", mime("video/mp4"))),
        "mov" => Some(("video", mime("video/quicktime"))),
        "webm" => Some(("video", mime("video/webm"))),
        "mkv" => Some(("video", mime("video/x-matroska"))),
        "avi" => Some(("video", mime("video/x-msvideo"))),
        "wmv" => Some(("video", mime("video/x-ms-wmv"))),
        "mpg" | "mpeg" => Some(("video", mime("video/mpeg"))),
        "flv" => Some(("video", mime("video/x-flv"))),
        "3gp" => Some(("video", mime("video/3gpp"))),
        "doc" => Some(("document", mime("application/msword"))),
        "docx" => Some((
            "document",
            mime("application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        )),
        "rtf" => Some(("document", mime("application/rtf"))),
        "odt" => Some(("document", mime("application/vnd.oasis.opendocument.text"))),
        "pages" | "wpd" => Some(("document", mime("application/octet-stream"))),
        "xls" => Some(("spreadsheet", mime("application/vnd.ms-excel"))),
        "xlsx" | "xlsm" => Some((
            "spreadsheet",
            mime("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        )),
        "ods" => Some((
            "spreadsheet",
            mime("application/vnd.oasis.opendocument.spreadsheet"),
        )),
        "numbers" => Some(("spreadsheet", mime("application/octet-stream"))),
        "ppt" => Some(("presentation", mime("application/vnd.ms-powerpoint"))),
        "pptx" | "pptm" => Some((
            "presentation",
            mime("application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        )),
        "odp" => Some((
            "presentation",
            mime("application/vnd.oasis.opendocument.presentation"),
        )),
        "zip" | "zipx" => Some(("archive", mime("application/zip"))),
        "7z" => Some(("archive", mime("application/x-7z-compressed"))),
        "rar" => Some(("archive", mime("application/vnd.rar"))),
        "tar" => Some(("archive", mime("application/x-tar"))),
        "gz" | "tgz" => Some(("archive", mime("application/gzip"))),
        "bz2" | "tbz" | "tbz2" => Some(("archive", mime("application/x-bzip2"))),
        "xz" | "txz" => Some(("archive", mime("application/x-xz"))),
        "zst" | "tzst" => Some(("archive", mime("application/zstd"))),
        "cab" => Some(("archive", mime("application/vnd.ms-cab-compressed"))),
        "jar" => Some(("archive", mime("application/java-archive"))),
        "apk" | "ipa" | "crx" | "xpi" => Some(("package", mime("application/octet-stream"))),
        "exe" | "com" | "dll" | "sys" | "drv" | "ocx" | "scr" | "msi" | "msp" | "appx" | "msix"
        | "deb" | "rpm" => Some(("executable", mime("application/octet-stream"))),
        "ttf" => Some(("font", mime("font/ttf"))),
        "otf" => Some(("font", mime("font/otf"))),
        "woff" => Some(("font", mime("font/woff"))),
        "woff2" => Some(("font", mime("font/woff2"))),
        "eot" | "fon" => Some(("font", mime("application/octet-stream"))),
        "db" | "sqlite" | "sqlite3" | "mdb" | "accdb" | "dbf" | "parquet" | "orc" => {
            Some(("database", mime("application/octet-stream")))
        }
        "iso" | "img" | "dmg" | "vhd" | "vhdx" | "vmdk" | "qcow" | "qcow2" => {
            Some(("disk-image", mime("application/octet-stream")))
        }
        "epub" => Some(("ebook", mime("application/epub+zip"))),
        "mobi" | "azw" | "azw3" | "fb2" => Some(("ebook", mime("application/octet-stream"))),
        "eml" => Some(("email", mime("message/rfc822"))),
        "msg" | "pst" | "ost" => Some(("email", mime("application/octet-stream"))),
        "ics" => Some(("calendar", mime("text/calendar"))),
        "vcf" => Some(("contact", mime("text/vcard"))),
        "cer" | "crt" | "der" | "pem" | "pfx" | "p12" | "csr" | "key" => {
            Some(("certificate", mime("application/octet-stream")))
        }
        "psd" | "ai" | "eps" | "indd" | "xd" | "fig" | "sketch" | "afdesign" => {
            Some(("design", mime("application/octet-stream")))
        }
        "obj" | "fbx" | "stl" | "blend" | "dae" | "gltf" | "glb" | "3ds" | "ply" => {
            Some(("model", mime("application/octet-stream")))
        }
        "dwg" | "dxf" => Some(("cad", mime("application/octet-stream"))),
        "torrent" => Some(("torrent", mime("application/x-bittorrent"))),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::{classify_known_extension, read_file_preview};
    use std::{fs, path::PathBuf};

    #[test]
    fn classify_known_extension_keeps_inline_preview_types() {
        for (extension, expected_kind) in [
            ("txt", "text"),
            ("json", "text"),
            ("rs", "text"),
            ("png", "image"),
            ("svg", "image"),
            ("webp", "image"),
            ("pdf", "pdf"),
            ("mp3", "audio"),
            ("wv", "audio"),
            ("mp4", "video"),
        ] {
            let (kind, _) = classify_known_extension(extension).expect("extension is known");
            assert_eq!(kind, expected_kind, "{extension}");
        }
    }

    #[test]
    fn classify_known_extension_covers_icon_preview_types() {
        for (extension, expected_kind) in [
            ("docx", "document"),
            ("xlsx", "spreadsheet"),
            ("pptx", "presentation"),
            ("zip", "archive"),
            ("apk", "package"),
            ("exe", "executable"),
            ("ttf", "font"),
            ("sqlite", "database"),
            ("iso", "disk-image"),
            ("epub", "ebook"),
            ("eml", "email"),
            ("cer", "certificate"),
            ("psd", "design"),
            ("stl", "model"),
            ("dwg", "cad"),
            ("torrent", "torrent"),
        ] {
            let (kind, _) = classify_known_extension(extension).expect("extension is known");
            assert_eq!(kind, expected_kind, "{extension}");
        }
    }

    #[test]
    fn read_file_preview_reports_known_icon_preview_category() {
        let path = temp_preview_file("sample.docx", b"not a real office package");
        let preview =
            read_file_preview(path.to_string_lossy().to_string(), Some(128)).expect("preview");
        fs::remove_file(path).ok();

        assert_eq!(preview.file_type, "document");
        assert_eq!(
            preview.mime_type,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        );
        assert_eq!(preview.content, None);
    }

    #[test]
    fn read_file_preview_does_not_inline_pdf_content() {
        let path = temp_preview_file("sample.pdf", b"%PDF-1.7\nnot a full pdf");
        let preview = read_file_preview(path.to_string_lossy().to_string(), Some(2_000_000))
            .expect("preview");
        fs::remove_file(path).ok();

        assert_eq!(preview.file_type, "pdf");
        assert_eq!(preview.mime_type, "application/pdf");
        assert_eq!(preview.content, None);
        assert_eq!(preview.encoding, None);
    }

    #[test]
    fn read_file_preview_reports_media_without_inline_content() {
        for (name, expected_kind, expected_mime) in [
            ("sample.mp3", "audio", "audio/mpeg"),
            ("sample.mp4", "video", "video/mp4"),
        ] {
            let path = temp_preview_file(name, b"not real media bytes");
            let preview = read_file_preview(path.to_string_lossy().to_string(), Some(2_000_000))
                .expect("preview");
            fs::remove_file(path).ok();

            assert_eq!(preview.file_type, expected_kind);
            assert_eq!(preview.mime_type, expected_mime);
            assert_eq!(preview.content, None);
            assert_eq!(preview.encoding, None);
        }
    }

    fn temp_preview_file(name: &str, content: &[u8]) -> PathBuf {
        let path =
            std::env::temp_dir().join(format!("simplefile-preview-{}-{name}", std::process::id()));
        fs::write(&path, content).expect("write temp preview file");
        path
    }
}
