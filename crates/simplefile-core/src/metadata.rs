use crate::models::{FileMetadata, ImageMetadata};
use crate::utils::resolve_readable_path;
use image::GenericImageView;
use lofty::file::{AudioFile, TaggedFileExt};
use lofty::tag::{Accessor, ItemKey};
use std::fs::{self, File};
use std::io::{BufReader, Read, Seek, SeekFrom};
use std::path::Path;
use std::time::Duration;
use zip::ZipArchive;

/// Hard size caps keep metadata extraction from blocking the UI on huge files.
const MAX_IMAGE_METADATA_BYTES: u64 = 50 * 1024 * 1024;
const MAX_PDF_METADATA_BYTES: u64 = 32 * 1024 * 1024;
const MAX_AUDIO_METADATA_BYTES: u64 = 100 * 1024 * 1024;
const MAX_VIDEO_PROBE_BYTES: u64 = 12 * 1024 * 1024;
const MAX_OFFICE_METADATA_BYTES: u64 = 50 * 1024 * 1024;
const MAX_FIELD_VALUE_CHARS: usize = 512;
const MAX_EXIF_FIELDS: usize = 80;

/// Extract basic metadata from an image file. This command returns the
/// pixel dimensions and any EXIF fields found in the image. If the
/// file cannot be decoded as an image, an error is returned. If the
/// image contains no EXIF metadata or EXIF parsing fails, the `exif`
/// vector in the result will simply be empty.
pub fn get_image_metadata(path: String) -> Result<ImageMetadata, String> {
    let path_buf = resolve_readable_path(&path)?;
    ensure_regular_file(&path_buf)?;
    ensure_size_limit(&path_buf, MAX_IMAGE_METADATA_BYTES, "image")?;
    extract_image_metadata(&path_buf)
}

/// Extract structured metadata for common document, media, and image files.
/// Unsupported types return `kind = "unsupported"` with an empty field list
/// rather than an error, so the properties UI can keep rendering base info.
pub fn get_file_metadata(path: String) -> Result<FileMetadata, String> {
    let path_buf = resolve_readable_path(&path)?;
    ensure_regular_file(&path_buf)?;
    let extension = path_buf
        .extension()
        .and_then(|ext| ext.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();

    match classify_extension(&extension) {
        MetadataKind::Image => {
            ensure_size_limit(&path_buf, MAX_IMAGE_METADATA_BYTES, "image")?;
            match extract_image_metadata(&path_buf) {
                Ok(image) => Ok(file_metadata_from_image(image)),
                Err(_) => Ok(format_only_metadata(
                    "image",
                    "Image file",
                    extension.to_ascii_uppercase(),
                )),
            }
        }
        MetadataKind::Pdf => {
            ensure_size_limit(&path_buf, MAX_PDF_METADATA_BYTES, "PDF")?;
            extract_pdf_metadata(&path_buf)
        }
        MetadataKind::Audio => {
            ensure_size_limit(&path_buf, MAX_AUDIO_METADATA_BYTES, "audio")?;
            extract_audio_metadata(&path_buf)
        }
        MetadataKind::Video => {
            // Container probing only reads a header window; reject empty paths and
            // impossible sizes, but allow multi‑GB media files.
            let len = fs::metadata(&path_buf)
                .map_err(|e| format!("Failed to stat video: {e}"))?
                .len();
            if len == 0 {
                return Err("Video file is empty".to_string());
            }
            extract_video_metadata(&path_buf, &extension)
        }
        MetadataKind::Office => {
            ensure_size_limit(&path_buf, MAX_OFFICE_METADATA_BYTES, "Office document")?;
            extract_office_metadata(&path_buf, &extension)
        }
        MetadataKind::Data => extract_data_metadata(&path_buf, &extension),
        MetadataKind::Archive => extract_archive_metadata(&path_buf, &extension),
        MetadataKind::Font => extract_font_metadata(&path_buf, &extension),
        MetadataKind::Ebook => extract_ebook_metadata(&path_buf, &extension),
        MetadataKind::Email => extract_email_metadata(&path_buf, &extension),
        MetadataKind::Calendar => extract_calendar_metadata(&path_buf, &extension),
        MetadataKind::Contact => extract_contact_metadata(&path_buf, &extension),
        MetadataKind::Certificate => extract_certificate_metadata(&path_buf, &extension),
        MetadataKind::Unsupported => Ok(FileMetadata {
            kind: "unsupported".to_string(),
            summary: None,
            fields: Vec::new(),
        }),
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum MetadataKind {
    Image,
    Pdf,
    Audio,
    Video,
    Office,
    Data,
    Archive,
    Font,
    Ebook,
    Email,
    Calendar,
    Contact,
    Certificate,
    Unsupported,
}

fn classify_extension(extension: &str) -> MetadataKind {
    match extension.to_ascii_lowercase().as_str() {
        "png" | "jpg" | "jpeg" | "jpe" | "jfif" | "gif" | "bmp" | "dib" | "webp" | "tif"
        | "tiff" | "svg" | "ico" | "cur" | "heic" | "heif" | "avif" | "avifs" | "jxl" | "jp2"
        | "j2k" | "jpf" | "tga" | "dds" | "exr" | "hdr" | "qoi" | "pnm" | "pbm" | "pgm" | "ppm"
        | "pam" | "dng" | "arw" | "cr2" | "cr3" | "nef" | "orf" | "rw2" | "raf" | "srw" | "pef"
        | "x3f" => MetadataKind::Image,
        "pdf" => MetadataKind::Pdf,
        "mp3" | "flac" | "ogg" | "oga" | "opus" | "wav" | "m4a" | "aac" | "aiff" | "aif"
        | "wma" | "wv" | "ape" | "alac" | "amr" | "caf" | "mka" | "ra" => MetadataKind::Audio,
        "mp4" | "m4v" | "mov" | "qt" | "webm" | "mkv" | "mk3d" | "avi" | "divx" | "wmv" | "asf"
        | "mpg" | "mpeg" | "mpe" | "m2v" | "m2ts" | "mts" | "vob" | "flv" | "f4v" | "3gp"
        | "3g2" | "ogv" | "mxf" | "rm" | "rmvb" | "h264" | "h265" | "hevc" | "y4m" => {
            MetadataKind::Video
        }
        "docx" | "xlsx" | "pptx" | "odt" | "ods" | "odp" => MetadataKind::Office,
        "md" | "markdown" | "mdx" | "json" | "jsonc" | "map" | "jsonl" | "ndjson" | "csv"
        | "tsv" | "xml" | "xaml" | "html" | "htm" | "yaml" | "yml" | "toml" | "ini" | "cfg"
        | "conf" | "config" | "properties" | "env" | "editorconfig" | "gitignore"
        | "gitattributes" | "npmrc" | "log" | "srt" | "vtt" | "ass" | "ssa" | "lrc" | "nfo"
        | "cue" | "m3u" | "m3u8" | "pls" | "diff" | "patch" | "reg" | "lock" | "adoc"
        | "asciidoc" | "rst" | "tex" => MetadataKind::Data,
        "zip" | "zipx" | "7z" | "rar" | "tar" | "gz" | "tgz" | "bz2" | "tbz" | "tbz2" | "xz"
        | "txz" | "zst" | "tzst" | "cab" | "jar" | "apk" | "ipa" | "crx" | "xpi" => {
            MetadataKind::Archive
        }
        "ttf" | "otf" | "woff" | "woff2" | "eot" | "fon" => MetadataKind::Font,
        "epub" | "mobi" | "azw" | "azw3" | "fb2" => MetadataKind::Ebook,
        "eml" | "msg" | "pst" | "ost" => MetadataKind::Email,
        "ics" => MetadataKind::Calendar,
        "vcf" => MetadataKind::Contact,
        "cer" | "crt" | "der" | "pem" | "pfx" | "p12" | "csr" | "key" => MetadataKind::Certificate,
        _ => MetadataKind::Unsupported,
    }
}

fn ensure_regular_file(path: &Path) -> Result<(), String> {
    let meta = fs::symlink_metadata(path).map_err(|e| format!("Failed to stat path: {e}"))?;
    if meta.file_type().is_symlink() {
        // Resolve only after validating the symlink entry exists; follow for metadata.
        let target_meta =
            fs::metadata(path).map_err(|e| format!("Failed to follow symlink: {e}"))?;
        if !target_meta.is_file() {
            return Err("Metadata is available for files only".to_string());
        }
        return Ok(());
    }
    if !meta.is_file() {
        return Err("Metadata is available for files only".to_string());
    }
    Ok(())
}

fn ensure_size_limit(path: &Path, max_bytes: u64, label: &str) -> Result<(), String> {
    let len = fs::metadata(path)
        .map_err(|e| format!("Failed to stat {label}: {e}"))?
        .len();
    if len > max_bytes {
        return Err(format!(
            "{label} is too large for metadata extraction ({} > {} limit)",
            format_bytes(len),
            format_bytes(max_bytes)
        ));
    }
    Ok(())
}

fn format_bytes(bytes: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    let mut size = bytes as f64;
    let mut unit = 0usize;
    while size >= 1024.0 && unit < UNITS.len() - 1 {
        size /= 1024.0;
        unit += 1;
    }
    if unit == 0 {
        format!("{bytes} {}", UNITS[unit])
    } else {
        format!("{size:.1} {}", UNITS[unit])
    }
}

fn truncate_value(value: impl AsRef<str>) -> String {
    let value = value.as_ref().trim();
    if value.chars().count() <= MAX_FIELD_VALUE_CHARS {
        return value.to_string();
    }
    let mut out: String = value
        .chars()
        .take(MAX_FIELD_VALUE_CHARS.saturating_sub(1))
        .collect();
    out.push('…');
    out
}

fn push_field(fields: &mut Vec<(String, String)>, label: &str, value: Option<impl AsRef<str>>) {
    if let Some(value) = value {
        let trimmed = truncate_value(value);
        if !trimmed.is_empty() {
            fields.push((label.to_string(), trimmed));
        }
    }
}

fn format_duration(duration: Duration) -> String {
    let total_secs = duration.as_secs();
    let hours = total_secs / 3600;
    let minutes = (total_secs % 3600) / 60;
    let seconds = total_secs % 60;
    if hours > 0 {
        format!("{hours}:{minutes:02}:{seconds:02}")
    } else {
        format!("{minutes}:{seconds:02}")
    }
}

fn extract_image_metadata(path: &Path) -> Result<ImageMetadata, String> {
    // Prefer lightweight dimension probing; fall back to a full decode if needed.
    let (width, height) = match image::image_dimensions(path) {
        Ok(dims) => dims,
        Err(_) => {
            let img = image::open(path).map_err(|e| format!("Failed to open image: {e}"))?;
            img.dimensions()
        }
    };

    let exif_pairs = {
        match File::open(path) {
            Ok(file) => {
                let mut reader = BufReader::new(file);
                match exif::Reader::new().read_from_container(&mut reader) {
                    Ok(exif) => {
                        let mut pairs = Vec::new();
                        for field in exif.fields().take(MAX_EXIF_FIELDS) {
                            let tag = format!("{}", field.tag);
                            let value =
                                truncate_value(field.display_value().with_unit(&exif).to_string());
                            pairs.push((tag, value));
                        }
                        pairs
                    }
                    Err(_) => Vec::new(),
                }
            }
            Err(_) => Vec::new(),
        }
    };

    Ok(ImageMetadata {
        width,
        height,
        exif: exif_pairs,
    })
}

fn file_metadata_from_image(image: ImageMetadata) -> FileMetadata {
    let mut fields = vec![(
        "Dimensions".to_string(),
        format!("{} × {}", image.width, image.height),
    )];
    for (tag, value) in image.exif {
        fields.push((tag, value));
    }
    FileMetadata {
        kind: "image".to_string(),
        summary: Some(format!("{} × {}", image.width, image.height)),
        fields,
    }
}

fn format_only_metadata(kind: &str, format_label: &str, format_value: String) -> FileMetadata {
    FileMetadata {
        kind: kind.to_string(),
        summary: Some(format_label.to_string()),
        fields: vec![("Format".to_string(), format_value)],
    }
}

fn extract_pdf_metadata(path: &Path) -> Result<FileMetadata, String> {
    let document = lopdf::Document::load(path).map_err(|e| format!("Failed to open PDF: {e}"))?;
    let page_count = document.get_pages().len() as u32;

    let mut fields = vec![("Pages".to_string(), page_count.to_string())];

    if let Ok(info_obj) = document.trailer.get(b"Info") {
        if let Ok(info_ref) = info_obj.as_reference() {
            if let Ok(info_dict) = document.get_dictionary(info_ref) {
                for (key, label) in [
                    (b"Title".as_slice(), "Title"),
                    (b"Author".as_slice(), "Author"),
                    (b"Subject".as_slice(), "Subject"),
                    (b"Creator".as_slice(), "Creator"),
                    (b"Producer".as_slice(), "Producer"),
                    (b"Keywords".as_slice(), "Keywords"),
                ] {
                    if let Ok(value) = info_dict.get(key) {
                        if let Some(text) = pdf_object_text(value) {
                            push_field(&mut fields, label, Some(text));
                        }
                    }
                }
            }
        }
    }

    let title = fields
        .iter()
        .find(|(label, _)| label == "Title")
        .map(|(_, value)| value.clone());
    let summary = match title {
        Some(title) => Some(format!("{page_count} pages · {title}")),
        None => Some(format!("{page_count} pages")),
    };

    Ok(FileMetadata {
        kind: "pdf".to_string(),
        summary,
        fields,
    })
}

fn pdf_object_text(object: &lopdf::Object) -> Option<String> {
    match object {
        lopdf::Object::String(bytes, _) => Some(String::from_utf8_lossy(bytes).into_owned()),
        lopdf::Object::Name(name) => Some(String::from_utf8_lossy(name).into_owned()),
        _ => None,
    }
}

fn extract_audio_metadata(path: &Path) -> Result<FileMetadata, String> {
    let tagged =
        lofty::read_from_path(path).map_err(|e| format!("Failed to read audio tags: {e}"))?;
    let properties = tagged.properties();
    let duration = properties.duration();

    let mut fields = Vec::new();
    if !duration.is_zero() {
        fields.push(("Duration".to_string(), format_duration(duration)));
    }
    if let Some(bitrate) = properties.audio_bitrate() {
        fields.push(("Bitrate".to_string(), format!("{bitrate} kbps")));
    }
    if let Some(sample_rate) = properties.sample_rate() {
        fields.push(("Sample rate".to_string(), format!("{sample_rate} Hz")));
    }
    if let Some(channels) = properties.channels() {
        fields.push(("Channels".to_string(), channels.to_string()));
    }

    if let Some(tag) = tagged.primary_tag().or_else(|| tagged.first_tag()) {
        push_field(&mut fields, "Title", tag.title().map(|v| v.to_string()));
        push_field(&mut fields, "Artist", tag.artist().map(|v| v.to_string()));
        push_field(&mut fields, "Album", tag.album().map(|v| v.to_string()));
        push_field(
            &mut fields,
            "Album artist",
            tag.get_string(ItemKey::AlbumArtist).map(|v| v.to_string()),
        );
        push_field(&mut fields, "Genre", tag.genre().map(|v| v.to_string()));
        if let Some(date) = tag.date() {
            fields.push(("Date".to_string(), date.to_string()));
        } else {
            push_field(
                &mut fields,
                "Year",
                tag.get_string(ItemKey::Year).map(|v| v.to_string()),
            );
        }
        if let Some(track) = tag.track() {
            let track_text = match tag.track_total() {
                Some(total) => format!("{track} / {total}"),
                None => track.to_string(),
            };
            fields.push(("Track".to_string(), track_text));
        }
        if let Some(disc) = tag.disk() {
            let disc_text = match tag.disk_total() {
                Some(total) => format!("{disc} / {total}"),
                None => disc.to_string(),
            };
            fields.push(("Disc".to_string(), disc_text));
        }
    }

    let summary = {
        let title = fields
            .iter()
            .find(|(label, _)| label == "Title")
            .map(|(_, value)| value.as_str());
        let artist = fields
            .iter()
            .find(|(label, _)| label == "Artist")
            .map(|(_, value)| value.as_str());
        let duration_text = fields
            .iter()
            .find(|(label, _)| label == "Duration")
            .map(|(_, value)| value.as_str());

        match (artist, title, duration_text) {
            (Some(artist), Some(title), Some(duration)) => {
                Some(format!("{artist} — {title} ({duration})"))
            }
            (Some(artist), Some(title), None) => Some(format!("{artist} — {title}")),
            (_, Some(title), Some(duration)) => Some(format!("{title} ({duration})")),
            (_, _, Some(duration)) => Some(duration.to_string()),
            (_, Some(title), _) => Some(title.to_string()),
            _ => None,
        }
    };

    Ok(FileMetadata {
        kind: "audio".to_string(),
        summary,
        fields,
    })
}

fn extract_video_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    match extension {
        "mp4" | "m4v" | "mov" => extract_mp4_metadata(path),
        _ => Ok(FileMetadata {
            kind: "video".to_string(),
            summary: Some("Container metadata not available for this format".to_string()),
            fields: vec![("Format".to_string(), extension.to_ascii_uppercase())],
        }),
    }
}

fn extract_mp4_metadata(path: &Path) -> Result<FileMetadata, String> {
    let file = File::open(path).map_err(|e| format!("Failed to open video: {e}"))?;
    let mut reader = BufReader::new(file);
    let mut timescale = 0u32;
    let mut duration_units = 0u64;
    let mut width = 0u32;
    let mut height = 0u32;
    let mut brand = String::new();

    scan_mp4_atoms(
        &mut reader,
        0,
        None,
        &mut timescale,
        &mut duration_units,
        &mut width,
        &mut height,
        &mut brand,
        0,
    )?;

    let mut fields = Vec::new();
    if !brand.is_empty() {
        fields.push(("Brand".to_string(), brand.to_ascii_uppercase()));
    }

    let duration = if timescale > 0 && duration_units > 0 {
        let secs = duration_units as f64 / timescale as f64;
        let duration = Duration::from_secs_f64(secs.max(0.0));
        let text = format_duration(duration);
        fields.push(("Duration".to_string(), text.clone()));
        Some(text)
    } else {
        None
    };

    if width > 0 && height > 0 {
        fields.push(("Dimensions".to_string(), format!("{width} × {height}")));
    }

    if fields.is_empty() {
        return Ok(FileMetadata {
            kind: "video".to_string(),
            summary: Some("No container metadata found".to_string()),
            fields,
        });
    }

    let summary = match (duration, width > 0 && height > 0) {
        (Some(duration), true) => Some(format!("{width} × {height} · {duration}")),
        (Some(duration), false) => Some(duration),
        (None, true) => Some(format!("{width} × {height}")),
        _ => None,
    };

    Ok(FileMetadata {
        kind: "video".to_string(),
        summary,
        fields,
    })
}

#[allow(clippy::too_many_arguments)]
fn scan_mp4_atoms<R: Read + Seek>(
    reader: &mut R,
    end: u64,
    parent: Option<[u8; 4]>,
    timescale: &mut u32,
    duration_units: &mut u64,
    width: &mut u32,
    height: &mut u32,
    brand: &mut String,
    depth: usize,
) -> Result<(), String> {
    if depth > 12 {
        return Ok(());
    }

    let stream_end = if end == 0 {
        let file_end = reader
            .seek(SeekFrom::End(0))
            .map_err(|e| format!("Failed to measure video: {e}"))?;
        reader
            .seek(SeekFrom::Start(0))
            .map_err(|e| format!("Failed to rewind video: {e}"))?;
        file_end
    } else {
        end
    };

    let mut bytes_read_limit = MAX_VIDEO_PROBE_BYTES;
    while reader
        .stream_position()
        .map_err(|e| format!("Failed to read video position: {e}"))?
        + 8
        <= stream_end
        && bytes_read_limit > 0
    {
        let atom_start = reader
            .stream_position()
            .map_err(|e| format!("Failed to read video position: {e}"))?;
        let mut header = [0u8; 8];
        if reader.read_exact(&mut header).is_err() {
            break;
        }
        bytes_read_limit = bytes_read_limit.saturating_sub(8);

        let mut size = u32::from_be_bytes([header[0], header[1], header[2], header[3]]) as u64;
        let kind = [header[4], header[5], header[6], header[7]];
        let mut header_len = 8u64;

        if size == 1 {
            let mut large = [0u8; 8];
            if reader.read_exact(&mut large).is_err() {
                break;
            }
            header_len = 16;
            bytes_read_limit = bytes_read_limit.saturating_sub(8);
            size = u64::from_be_bytes(large);
        } else if size == 0 {
            size = stream_end.saturating_sub(atom_start);
        }

        if size < header_len {
            break;
        }

        let content_size = size - header_len;
        let content_end = atom_start + size;
        if content_end > stream_end {
            break;
        }

        match &kind {
            b"ftyp" if content_size >= 4 => {
                let mut major = [0u8; 4];
                if reader.read_exact(&mut major).is_ok() {
                    *brand = String::from_utf8_lossy(&major).into_owned();
                }
                let skip = content_size.saturating_sub(4);
                reader
                    .seek(SeekFrom::Current(skip as i64))
                    .map_err(|e| format!("Failed to skip ftyp: {e}"))?;
                bytes_read_limit = bytes_read_limit.saturating_sub(content_size);
            }
            b"moov" | b"trak" | b"mdia" | b"minf" | b"stbl" => {
                scan_mp4_atoms(
                    reader,
                    content_end,
                    Some(kind),
                    timescale,
                    duration_units,
                    width,
                    height,
                    brand,
                    depth + 1,
                )?;
                reader
                    .seek(SeekFrom::Start(content_end))
                    .map_err(|e| format!("Failed to seek after container atom: {e}"))?;
            }
            b"mvhd" => {
                let mut buf = vec![0u8; content_size.min(100) as usize];
                if reader.read_exact(&mut buf).is_ok() {
                    parse_mvhd(&buf, timescale, duration_units);
                }
                if content_size > buf.len() as u64 {
                    reader
                        .seek(SeekFrom::Current((content_size - buf.len() as u64) as i64))
                        .ok();
                }
                bytes_read_limit = bytes_read_limit.saturating_sub(content_size);
            }
            b"tkhd" => {
                let mut buf = vec![0u8; content_size.min(100) as usize];
                if reader.read_exact(&mut buf).is_ok() {
                    if let Some((w, h)) = parse_tkhd(&buf) {
                        if w > 0 && h > 0 && (*width == 0 || w * h > *width * *height) {
                            *width = w;
                            *height = h;
                        }
                    }
                }
                if content_size > buf.len() as u64 {
                    reader
                        .seek(SeekFrom::Current((content_size - buf.len() as u64) as i64))
                        .ok();
                }
                bytes_read_limit = bytes_read_limit.saturating_sub(content_size);
            }
            _ => {
                reader
                    .seek(SeekFrom::Start(content_end))
                    .map_err(|e| format!("Failed to skip atom: {e}"))?;
                bytes_read_limit =
                    bytes_read_limit.saturating_sub(content_size.min(bytes_read_limit));
            }
        }

        // Keep Clippy quiet about unused parent in non-debug builds.
        let _ = parent;
    }

    Ok(())
}

fn parse_mvhd(buf: &[u8], timescale: &mut u32, duration_units: &mut u64) {
    if buf.is_empty() {
        return;
    }
    let version = buf[0];
    if version == 1 {
        if buf.len() >= 32 {
            *timescale = u32::from_be_bytes([buf[20], buf[21], buf[22], buf[23]]);
            *duration_units = u64::from_be_bytes([
                buf[24], buf[25], buf[26], buf[27], buf[28], buf[29], buf[30], buf[31],
            ]);
        }
    } else if buf.len() >= 20 {
        *timescale = u32::from_be_bytes([buf[12], buf[13], buf[14], buf[15]]);
        *duration_units = u32::from_be_bytes([buf[16], buf[17], buf[18], buf[19]]) as u64;
    }
}

fn parse_tkhd(buf: &[u8]) -> Option<(u32, u32)> {
    if buf.is_empty() {
        return None;
    }
    let version = buf[0];
    let (width_off, height_off) = if version == 1 {
        (90usize, 94usize)
    } else {
        (76usize, 80usize)
    };
    if buf.len() < height_off + 4 {
        return None;
    }
    let width = u32::from_be_bytes([
        buf[width_off],
        buf[width_off + 1],
        buf[width_off + 2],
        buf[width_off + 3],
    ]) >> 16;
    let height = u32::from_be_bytes([
        buf[height_off],
        buf[height_off + 1],
        buf[height_off + 2],
        buf[height_off + 3],
    ]) >> 16;
    Some((width, height))
}

fn extract_data_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    const MAX_TEXT_METADATA_BYTES: u64 = 2 * 1024 * 1024;
    let text = read_limited_text(path, MAX_TEXT_METADATA_BYTES)?;
    let mut fields = vec![(
        "Format".to_string(),
        data_format_label(extension).to_string(),
    )];
    let mut summary = data_format_label(extension).to_string();

    match extension {
        "json" | "jsonc" | "map" => {
            if let Ok(value) = serde_json::from_str::<serde_json::Value>(&text) {
                summarize_json_value(&mut fields, &mut summary, &value);
            } else {
                fields.push(("Parse".to_string(), "Invalid JSON".to_string()));
            }
        }
        "jsonl" | "ndjson" => summarize_json_lines(&mut fields, &mut summary, &text),
        "csv" => summarize_delimited(&mut fields, &mut summary, &text, ','),
        "tsv" => summarize_delimited(&mut fields, &mut summary, &text, '\t'),
        "xml" | "xaml" => {
            if let Some(root) = xml_root_name(&text) {
                summary = format!("XML root: {root}");
                fields.push(("Root".to_string(), root));
            }
        }
        "html" | "htm" => {
            if let Some(title) = xml_local_text(&text, "title") {
                summary = title.clone();
                fields.push(("Title".to_string(), title));
            }
        }
        "md" | "markdown" | "mdx" => summarize_markdown(&mut fields, &mut summary, &text),
        "yaml" | "yml" | "toml" | "ini" | "cfg" | "conf" | "config" | "properties" | "env" => {
            summarize_key_value_text(&mut fields, &text, extension)
        }
        _ => {}
    }

    let line_count = text.lines().count();
    if line_count > 0 {
        fields.push(("Lines".to_string(), line_count.to_string()));
    }

    Ok(FileMetadata {
        kind: "data".to_string(),
        summary: Some(summary),
        fields,
    })
}

fn summarize_json_value(
    fields: &mut Vec<(String, String)>,
    summary: &mut String,
    value: &serde_json::Value,
) {
    match value {
        serde_json::Value::Object(map) => {
            *summary = format!("JSON object with {} keys", map.len());
            fields.push(("Structure".to_string(), "Object".to_string()));
            fields.push(("Top-level keys".to_string(), map.len().to_string()));
            if let Some(keys) = join_limited(map.keys().cloned(), 12) {
                fields.push(("Keys".to_string(), keys));
            }
        }
        serde_json::Value::Array(items) => {
            *summary = format!("JSON array with {} items", items.len());
            fields.push(("Structure".to_string(), "Array".to_string()));
            fields.push(("Items".to_string(), items.len().to_string()));
        }
        _ => {
            *summary = "JSON value".to_string();
            fields.push(("Structure".to_string(), "Value".to_string()));
        }
    }
}

fn summarize_json_lines(fields: &mut Vec<(String, String)>, summary: &mut String, text: &str) {
    let lines: Vec<&str> = text
        .lines()
        .filter(|line| !line.trim().is_empty())
        .collect();
    let valid = lines
        .iter()
        .take(500)
        .filter(|line| serde_json::from_str::<serde_json::Value>(line).is_ok())
        .count();
    *summary = format!("JSON Lines with {} records", lines.len());
    fields.push(("Records".to_string(), lines.len().to_string()));
    fields.push(("Valid sample records".to_string(), valid.to_string()));
}

fn summarize_delimited(
    fields: &mut Vec<(String, String)>,
    summary: &mut String,
    text: &str,
    delimiter: char,
) {
    let rows: Vec<&str> = text
        .lines()
        .filter(|line| !line.trim().is_empty())
        .collect();
    let headers = rows
        .first()
        .map(|line| split_delimited_line(line, delimiter))
        .unwrap_or_default();
    let column_count = headers.len();
    *summary = if column_count > 0 {
        format!("{} rows x {} columns", rows.len(), column_count)
    } else {
        format!("{} rows", rows.len())
    };
    fields.push(("Rows".to_string(), rows.len().to_string()));
    if column_count > 0 {
        fields.push(("Columns".to_string(), column_count.to_string()));
        if let Some(header_text) = join_limited(headers, 10) {
            fields.push(("Headers".to_string(), header_text));
        }
    }
}

fn summarize_markdown(fields: &mut Vec<(String, String)>, summary: &mut String, text: &str) {
    let heading = text
        .lines()
        .map(str::trim)
        .find_map(|line| line.strip_prefix("# ").map(str::trim))
        .filter(|value| !value.is_empty())
        .map(ToOwned::to_owned);
    if let Some(heading) = heading {
        *summary = heading.clone();
        fields.push(("Title".to_string(), heading));
    } else {
        *summary = "Markdown document".to_string();
    }

    let headings = text
        .lines()
        .filter(|line| {
            let trimmed = line.trim_start();
            trimmed.starts_with('#') && trimmed.chars().take_while(|ch| *ch == '#').count() <= 6
        })
        .count();
    let words = text.split_whitespace().count();
    fields.push(("Headings".to_string(), headings.to_string()));
    fields.push(("Words".to_string(), words.to_string()));
    fields.push(("Links".to_string(), text.matches("](").count().to_string()));
}

fn summarize_key_value_text(fields: &mut Vec<(String, String)>, text: &str, extension: &str) {
    let separator = if extension == "toml" { '=' } else { ':' };
    let keys = top_level_keys(text, separator);
    if !keys.is_empty() {
        fields.push(("Top-level keys".to_string(), keys.len().to_string()));
        if let Some(key_text) = join_limited(keys, 12) {
            fields.push(("Keys".to_string(), key_text));
        }
    }
}

fn extract_archive_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    match extension {
        "zip" | "zipx" | "jar" | "apk" | "ipa" | "crx" | "xpi" => {
            extract_zip_archive_metadata(path, archive_format_label(extension))
        }
        "tar" => {
            let file = File::open(path).map_err(|e| format!("Failed to open archive: {e}"))?;
            extract_tar_archive_metadata(file, archive_format_label(extension))
        }
        "tgz" => {
            let file = File::open(path).map_err(|e| format!("Failed to open archive: {e}"))?;
            let decoder = flate2::read::GzDecoder::new(file);
            extract_tar_archive_metadata(decoder, "Compressed tar archive")
        }
        _ => Ok(format_only_metadata(
            "archive",
            archive_format_label(extension),
            extension.to_ascii_uppercase(),
        )),
    }
}

fn extract_zip_archive_metadata(path: &Path, format_label: &str) -> Result<FileMetadata, String> {
    const MAX_ARCHIVE_ENTRIES: usize = 2_000;
    let file = File::open(path).map_err(|e| format!("Failed to open archive: {e}"))?;
    let mut archive = ZipArchive::new(file).map_err(|e| format!("Failed to read archive: {e}"))?;
    let mut files = 0usize;
    let mut directories = 0usize;
    let mut uncompressed = 0u64;
    let mut compressed = 0u64;
    let mut names = Vec::new();
    for index in 0..archive.len().min(MAX_ARCHIVE_ENTRIES) {
        if let Ok(file) = archive.by_index(index) {
            if file.is_dir() {
                directories += 1;
            } else {
                files += 1;
            }

            uncompressed = uncompressed.saturating_add(file.size());
            compressed = compressed.saturating_add(file.compressed_size());
            if names.len() < 8 {
                names.push(file.name().replace('\\', "/"));
            }
        }
    }

    let mut fields = vec![
        ("Format".to_string(), format_label.to_string()),
        ("Files".to_string(), files.to_string()),
        ("Folders".to_string(), directories.to_string()),
        ("Uncompressed size".to_string(), format_bytes(uncompressed)),
        ("Compressed size".to_string(), format_bytes(compressed)),
    ];
    if let Some(entries) = join_limited(names, 8) {
        fields.push(("Sample entries".to_string(), entries));
    }

    Ok(FileMetadata {
        kind: "archive".to_string(),
        summary: Some(format!("{files} files, {directories} folders")),
        fields,
    })
}

fn extract_tar_archive_metadata<R: Read>(
    reader: R,
    format_label: &str,
) -> Result<FileMetadata, String> {
    const MAX_ARCHIVE_ENTRIES: usize = 2_000;
    let mut archive = tar::Archive::new(reader);
    let mut files = 0usize;
    let mut directories = 0usize;
    let mut size = 0u64;
    let mut names = Vec::new();
    for entry in archive
        .entries()
        .map_err(|e| format!("Failed to read archive: {e}"))?
        .take(MAX_ARCHIVE_ENTRIES)
    {
        let entry = entry.map_err(|e| format!("Failed to read archive entry: {e}"))?;
        if entry.header().entry_type().is_dir() {
            directories += 1;
        } else {
            files += 1;
        }

        size = size.saturating_add(entry.header().size().unwrap_or(0));
        if names.len() < 8 {
            names.push(
                entry
                    .path()
                    .map(|path| path.display().to_string())
                    .unwrap_or_default(),
            );
        }
    }

    let mut fields = vec![
        ("Format".to_string(), format_label.to_string()),
        ("Files".to_string(), files.to_string()),
        ("Folders".to_string(), directories.to_string()),
        ("Stored size".to_string(), format_bytes(size)),
    ];
    if let Some(entries) = join_limited(names, 8) {
        fields.push(("Sample entries".to_string(), entries));
    }

    Ok(FileMetadata {
        kind: "archive".to_string(),
        summary: Some(format!("{files} files, {directories} folders")),
        fields,
    })
}

fn extract_font_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    let bytes = read_limited_bytes(path, 2 * 1024 * 1024)?;
    let format_label = font_format_label(extension);
    let mut fields = vec![("Format".to_string(), format_label.to_string())];
    for (label, value) in parse_font_name_table(&bytes) {
        push_field(&mut fields, &label, Some(value));
    }

    let summary = fields
        .iter()
        .find(|(label, _)| label == "Full name")
        .or_else(|| fields.iter().find(|(label, _)| label == "Family"))
        .map(|(_, value)| value.clone())
        .unwrap_or_else(|| format_label.to_string());
    Ok(FileMetadata {
        kind: "font".to_string(),
        summary: Some(summary),
        fields,
    })
}

fn extract_ebook_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    if extension != "epub" {
        return Ok(format_only_metadata(
            "ebook",
            ebook_format_label(extension),
            extension.to_ascii_uppercase(),
        ));
    }

    let file = File::open(path).map_err(|e| format!("Failed to open ebook: {e}"))?;
    let mut archive =
        ZipArchive::new(file).map_err(|e| format!("Failed to read EPUB package: {e}"))?;
    let container = read_zip_text(&mut archive, "META-INF/container.xml");
    let opf_path = container
        .as_deref()
        .and_then(|xml| xml_attribute(xml, "full-path"))
        .or_else(|| find_zip_entry_by_suffix(&mut archive, ".opf"))
        .unwrap_or_else(|| "content.opf".to_string());
    let opf = read_zip_text(&mut archive, &opf_path).unwrap_or_default();

    let mut fields = vec![("Format".to_string(), "EPUB ebook".to_string())];
    push_field(&mut fields, "Title", xml_local_text(&opf, "title"));
    push_field(&mut fields, "Creator", xml_local_text(&opf, "creator"));
    push_field(&mut fields, "Language", xml_local_text(&opf, "language"));
    push_field(
        &mut fields,
        "Identifier",
        xml_local_text(&opf, "identifier"),
    );
    let documents = count_zip_suffixes(&mut archive, &[".xhtml", ".html", ".htm"]);
    if documents > 0 {
        fields.push(("Documents".to_string(), documents.to_string()));
    }

    let summary = fields
        .iter()
        .find(|(label, _)| label == "Title")
        .map(|(_, value)| value.clone())
        .unwrap_or_else(|| "EPUB ebook".to_string());
    Ok(FileMetadata {
        kind: "ebook".to_string(),
        summary: Some(summary),
        fields,
    })
}

fn extract_email_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    if extension != "eml" {
        return Ok(format_only_metadata(
            "email",
            email_format_label(extension),
            extension.to_ascii_uppercase(),
        ));
    }

    let text = read_limited_text(path, 1024 * 1024)?;
    let headers = unfolded_header_lines(&text);
    let mut fields = vec![("Format".to_string(), "Email message".to_string())];
    push_field(&mut fields, "Subject", header_value(&headers, "Subject"));
    push_field(&mut fields, "From", header_value(&headers, "From"));
    push_field(&mut fields, "To", header_value(&headers, "To"));
    push_field(&mut fields, "Date", header_value(&headers, "Date"));
    push_field(
        &mut fields,
        "Content type",
        header_value(&headers, "Content-Type"),
    );

    let summary = fields
        .iter()
        .find(|(label, _)| label == "Subject")
        .map(|(_, value)| value.clone())
        .unwrap_or_else(|| "Email message".to_string());
    Ok(FileMetadata {
        kind: "email".to_string(),
        summary: Some(summary),
        fields,
    })
}

fn extract_calendar_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    if extension != "ics" {
        return Ok(format_only_metadata(
            "calendar",
            "Calendar file",
            extension.to_ascii_uppercase(),
        ));
    }

    let text = read_limited_text(path, 1024 * 1024)?;
    let lines = unfolded_property_lines(&text);
    let mut fields = vec![("Format".to_string(), "iCalendar".to_string())];
    push_field(&mut fields, "Summary", property_value(&lines, "SUMMARY"));
    push_field(&mut fields, "Starts", property_value(&lines, "DTSTART"));
    push_field(&mut fields, "Ends", property_value(&lines, "DTEND"));
    push_field(&mut fields, "Location", property_value(&lines, "LOCATION"));
    let events = lines
        .iter()
        .filter(|line| line.eq_ignore_ascii_case("BEGIN:VEVENT"))
        .count();
    fields.push(("Events".to_string(), events.to_string()));

    let summary = fields
        .iter()
        .find(|(label, _)| label == "Summary")
        .map(|(_, value)| value.clone())
        .unwrap_or_else(|| "Calendar file".to_string());
    Ok(FileMetadata {
        kind: "calendar".to_string(),
        summary: Some(summary),
        fields,
    })
}

fn extract_contact_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    if extension != "vcf" {
        return Ok(format_only_metadata(
            "contact",
            "Contact file",
            extension.to_ascii_uppercase(),
        ));
    }

    let text = read_limited_text(path, 1024 * 1024)?;
    let lines = unfolded_property_lines(&text);
    let mut fields = vec![("Format".to_string(), "vCard".to_string())];
    push_field(&mut fields, "Name", property_value(&lines, "FN"));
    push_field(&mut fields, "Organization", property_value(&lines, "ORG"));
    push_field(&mut fields, "Title", property_value(&lines, "TITLE"));
    push_field(&mut fields, "Email", property_value(&lines, "EMAIL"));
    push_field(&mut fields, "Phone", property_value(&lines, "TEL"));

    let summary = fields
        .iter()
        .find(|(label, _)| label == "Name")
        .map(|(_, value)| value.clone())
        .unwrap_or_else(|| "Contact file".to_string());
    Ok(FileMetadata {
        kind: "contact".to_string(),
        summary: Some(summary),
        fields,
    })
}

fn extract_certificate_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    let mut fields = vec![(
        "Format".to_string(),
        certificate_format_label(extension).to_string(),
    )];
    if matches!(extension, "pem" | "csr" | "key" | "crt" | "cer") {
        if let Ok(text) = read_limited_text(path, 512 * 1024) {
            if let Some(block) = pem_block_label(&text) {
                fields.push(("PEM block".to_string(), block));
            }
        }
    }

    Ok(FileMetadata {
        kind: "certificate".to_string(),
        summary: Some(certificate_format_label(extension).to_string()),
        fields,
    })
}

fn extract_office_metadata(path: &Path, extension: &str) -> Result<FileMetadata, String> {
    let file = File::open(path).map_err(|e| format!("Failed to open document: {e}"))?;
    let mut archive =
        ZipArchive::new(file).map_err(|e| format!("Failed to read Office package: {e}"))?;

    let mut fields = Vec::new();
    let core_xml = read_zip_text(&mut archive, "docProps/core.xml")
        .or_else(|| read_zip_text(&mut archive, "meta.xml"));

    if let Some(xml) = core_xml.as_deref() {
        push_field(&mut fields, "Title", xml_local_text(xml, "title"));
        push_field(&mut fields, "Creator", xml_local_text(xml, "creator"));
        push_field(
            &mut fields,
            "Last modified by",
            xml_local_text(xml, "lastModifiedBy"),
        );
        push_field(&mut fields, "Subject", xml_local_text(xml, "subject"));
        push_field(
            &mut fields,
            "Description",
            xml_local_text(xml, "description"),
        );
        push_field(&mut fields, "Created", xml_local_text(xml, "created"));
        push_field(&mut fields, "Modified", xml_local_text(xml, "modified"));
        push_field(&mut fields, "Keywords", xml_local_text(xml, "keywords"));
        push_field(&mut fields, "Revision", xml_local_text(xml, "revision"));
    }

    // App properties and package-structure counts.
    if let Some(app_xml) = read_zip_text(&mut archive, "docProps/app.xml") {
        push_field(
            &mut fields,
            "Application",
            xml_local_text(&app_xml, "Application"),
        );
        push_field(&mut fields, "Pages", xml_local_text(&app_xml, "Pages"));
        push_field(&mut fields, "Words", xml_local_text(&app_xml, "Words"));
        push_field(
            &mut fields,
            "Paragraphs",
            xml_local_text(&app_xml, "Paragraphs"),
        );
        push_field(&mut fields, "Slides", xml_local_text(&app_xml, "Slides"));
        push_field(&mut fields, "Notes", xml_local_text(&app_xml, "Notes"));
        push_field(&mut fields, "Company", xml_local_text(&app_xml, "Company"));
    }

    match extension {
        "docx" => {
            if !fields.iter().any(|(label, _)| label == "Pages") {
                // Fallback: no reliable page count without layout; report body presence only.
            }
        }
        "xlsx" => {
            let sheets = count_zip_prefix(&mut archive, "xl/worksheets/sheet");
            if sheets > 0 {
                fields.push(("Sheets".to_string(), sheets.to_string()));
            }
        }
        "pptx" => {
            let slides = count_zip_prefix(&mut archive, "ppt/slides/slide");
            if slides > 0 && !fields.iter().any(|(label, _)| label == "Slides") {
                fields.push(("Slides".to_string(), slides.to_string()));
            }
        }
        "ods" => {
            if let Some(content) = read_zip_text(&mut archive, "content.xml") {
                let sheets = content
                    .matches("office:spreadsheet")
                    .count()
                    .max(content.matches("<table:table").count());
                if sheets > 0 {
                    fields.push(("Sheets".to_string(), sheets.to_string()));
                }
            }
        }
        "odp" => {
            if let Some(content) = read_zip_text(&mut archive, "content.xml") {
                let slides = content.matches("<draw:page").count();
                if slides > 0 {
                    fields.push(("Slides".to_string(), slides.to_string()));
                }
            }
        }
        _ => {}
    }

    let format_label = match extension {
        "docx" => "Word document",
        "xlsx" => "Excel spreadsheet",
        "pptx" => "PowerPoint presentation",
        "odt" => "OpenDocument text",
        "ods" => "OpenDocument spreadsheet",
        "odp" => "OpenDocument presentation",
        _ => "Office document",
    };
    fields.insert(0, ("Format".to_string(), format_label.to_string()));

    let title = fields
        .iter()
        .find(|(label, _)| label == "Title")
        .map(|(_, value)| value.clone());
    let summary = title.or_else(|| Some(format_label.to_string()));

    Ok(FileMetadata {
        kind: "office".to_string(),
        summary,
        fields,
    })
}

fn read_zip_text<R: Read + Seek>(archive: &mut ZipArchive<R>, name: &str) -> Option<String> {
    let mut file = archive.by_name(name).ok()?;
    // Cap individual XML parts so malicious packages cannot force large reads.
    const MAX_PART_BYTES: u64 = 2 * 1024 * 1024;
    if file.size() > MAX_PART_BYTES {
        return None;
    }
    let mut buf = String::new();
    file.read_to_string(&mut buf).ok()?;
    Some(buf)
}

fn count_zip_prefix<R: Read + Seek>(archive: &mut ZipArchive<R>, prefix: &str) -> usize {
    let mut count = 0usize;
    for index in 0..archive.len() {
        if let Ok(file) = archive.by_index(index) {
            let name = file.name().replace('\\', "/");
            if name.starts_with(prefix)
                && name.ends_with(".xml")
                && !name.contains("/_rels/")
                && !name.contains("/media/")
            {
                count += 1;
            }
        }
    }
    count
}

fn count_zip_suffixes<R: Read + Seek>(archive: &mut ZipArchive<R>, suffixes: &[&str]) -> usize {
    let mut count = 0usize;
    for index in 0..archive.len() {
        if let Ok(file) = archive.by_index(index) {
            let name = file.name().to_ascii_lowercase();
            if suffixes.iter().any(|suffix| name.ends_with(suffix)) {
                count += 1;
            }
        }
    }
    count
}

fn find_zip_entry_by_suffix<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
    suffix: &str,
) -> Option<String> {
    for index in 0..archive.len() {
        if let Ok(file) = archive.by_index(index) {
            let name = file.name().replace('\\', "/");
            if name.to_ascii_lowercase().ends_with(suffix) {
                return Some(name);
            }
        }
    }

    None
}

fn read_limited_bytes(path: &Path, max_bytes: u64) -> Result<Vec<u8>, String> {
    let mut file = File::open(path).map_err(|e| format!("Failed to open file: {e}"))?;
    let mut bytes = Vec::new();
    file.by_ref()
        .take(max_bytes)
        .read_to_end(&mut bytes)
        .map_err(|e| format!("Failed to read file: {e}"))?;
    Ok(bytes)
}

fn read_limited_text(path: &Path, max_bytes: u64) -> Result<String, String> {
    let bytes = read_limited_bytes(path, max_bytes)?;
    Ok(String::from_utf8_lossy(&bytes).into_owned())
}

fn data_format_label(extension: &str) -> &'static str {
    match extension {
        "md" | "markdown" | "mdx" => "Markdown document",
        "json" | "jsonc" | "map" => "JSON document",
        "jsonl" | "ndjson" => "JSON Lines document",
        "csv" => "CSV table",
        "tsv" => "TSV table",
        "xml" | "xaml" => "XML document",
        "html" | "htm" => "HTML document",
        "yaml" | "yml" => "YAML document",
        "toml" => "TOML document",
        "ini" | "cfg" | "conf" | "config" | "properties" | "env" => "Configuration file",
        "srt" | "vtt" | "ass" | "ssa" | "lrc" => "Timed text",
        "diff" | "patch" => "Patch file",
        "reg" => "Registry file",
        "adoc" | "asciidoc" => "AsciiDoc document",
        "rst" => "reStructuredText document",
        "tex" => "TeX document",
        _ => "Text document",
    }
}

fn archive_format_label(extension: &str) -> &'static str {
    match extension {
        "zip" | "zipx" => "ZIP archive",
        "jar" => "Java archive",
        "apk" => "Android package",
        "ipa" => "iOS package",
        "crx" => "Chrome extension package",
        "xpi" => "Firefox extension package",
        "tar" => "Tar archive",
        "tgz" => "Compressed tar archive",
        "gz" => "Gzip archive",
        "bz2" | "tbz" | "tbz2" => "Bzip2 archive",
        "xz" | "txz" => "XZ archive",
        "zst" | "tzst" => "Zstandard archive",
        "7z" => "7-Zip archive",
        "rar" => "RAR archive",
        "cab" => "Cabinet archive",
        _ => "Archive",
    }
}

fn font_format_label(extension: &str) -> &'static str {
    match extension {
        "ttf" => "TrueType font",
        "otf" => "OpenType font",
        "woff" => "Web Open Font",
        "woff2" => "Web Open Font 2",
        "eot" => "Embedded OpenType font",
        _ => "Font file",
    }
}

fn ebook_format_label(extension: &str) -> &'static str {
    match extension {
        "epub" => "EPUB ebook",
        "mobi" => "Mobipocket ebook",
        "azw" | "azw3" => "Kindle ebook",
        "fb2" => "FictionBook ebook",
        _ => "Ebook",
    }
}

fn email_format_label(extension: &str) -> &'static str {
    match extension {
        "eml" => "Email message",
        "msg" => "Outlook message",
        "pst" => "Outlook data file",
        "ost" => "Outlook offline data file",
        _ => "Email file",
    }
}

fn certificate_format_label(extension: &str) -> &'static str {
    match extension {
        "pem" => "PEM certificate/key",
        "crt" | "cer" => "Certificate",
        "der" => "DER certificate",
        "pfx" | "p12" => "PKCS#12 certificate",
        "csr" => "Certificate signing request",
        "key" => "Key file",
        _ => "Certificate/key file",
    }
}

fn join_limited(values: impl IntoIterator<Item = String>, limit: usize) -> Option<String> {
    let mut out = Vec::new();
    for value in values {
        let trimmed = value.trim();
        if !trimmed.is_empty() && !out.iter().any(|item: &String| item.as_str() == trimmed) {
            out.push(trimmed.to_string());
        }
        if out.len() >= limit {
            break;
        }
    }

    if out.is_empty() {
        None
    } else {
        Some(out.join(", "))
    }
}

fn split_delimited_line(line: &str, delimiter: char) -> Vec<String> {
    let mut cells = Vec::new();
    let mut cell = String::new();
    let mut quoted = false;
    let chars: Vec<char> = line.chars().collect();
    let mut index = 0usize;
    while index < chars.len() {
        let ch = chars[index];
        if ch == '"' {
            if quoted && index + 1 < chars.len() && chars[index + 1] == '"' {
                cell.push('"');
                index += 1;
            } else {
                quoted = !quoted;
            }
        } else if ch == delimiter && !quoted {
            cells.push(cell.trim().to_string());
            cell.clear();
        } else {
            cell.push(ch);
        }

        index += 1;
    }

    cells.push(cell.trim().to_string());
    cells
}

fn top_level_keys(text: &str, separator: char) -> Vec<String> {
    let mut keys = Vec::new();
    for line in text.lines().take(200) {
        let trimmed = line.trim();
        if trimmed.is_empty()
            || trimmed.starts_with('#')
            || trimmed.starts_with("//")
            || trimmed.starts_with('[')
        {
            continue;
        }

        if let Some((key, _)) = trimmed.split_once(separator) {
            let key = key.trim().trim_matches('"').trim_matches('\'');
            if !key.is_empty() {
                keys.push(key.to_string());
            }
        }
    }

    keys
}

fn xml_root_name(xml: &str) -> Option<String> {
    let start = xml.find('<')?;
    let rest = &xml[start + 1..];
    if rest.starts_with('?') || rest.starts_with('!') {
        return xml_root_name(&rest[1..]);
    }

    let end = rest.find(|ch: char| ch == '>' || ch.is_whitespace() || ch == '/')?;
    let name = rest[..end].trim();
    if name.is_empty() {
        None
    } else {
        Some(name.to_string())
    }
}

fn xml_attribute(xml: &str, attribute: &str) -> Option<String> {
    for quote in ['"', '\''] {
        let pattern = format!("{attribute}={quote}");
        if let Some(start) = xml.find(&pattern) {
            let value_start = start + pattern.len();
            if let Some(end) = xml[value_start..].find(quote) {
                return Some(xml[value_start..value_start + end].to_string());
            }
        }
    }

    None
}

fn unfolded_header_lines(text: &str) -> Vec<String> {
    let mut lines = Vec::<String>::new();
    for line in text.lines() {
        if line.trim().is_empty() {
            break;
        }

        if line.starts_with(' ') || line.starts_with('\t') {
            if let Some(last) = lines.last_mut() {
                last.push(' ');
                last.push_str(line.trim());
            }
        } else {
            lines.push(line.trim_end().to_string());
        }
    }

    lines
}

fn unfolded_property_lines(text: &str) -> Vec<String> {
    let mut lines = Vec::<String>::new();
    for line in text.lines() {
        if line.starts_with(' ') || line.starts_with('\t') {
            if let Some(last) = lines.last_mut() {
                last.push_str(line.trim());
            }
        } else {
            lines.push(line.trim_end().to_string());
        }
    }

    lines
}

fn header_value(lines: &[String], name: &str) -> Option<String> {
    let prefix = format!("{name}:");
    lines
        .iter()
        .find(|line| {
            line.len() >= prefix.len() && line[..prefix.len()].eq_ignore_ascii_case(&prefix)
        })
        .map(|line| decode_basic_xml_entities(line[prefix.len()..].trim()))
        .filter(|value| !value.is_empty())
}

fn property_value(lines: &[String], name: &str) -> Option<String> {
    lines.iter().find_map(|line| {
        let (key, value) = line.split_once(':')?;
        let property_name = key.split(';').next().unwrap_or(key);
        if property_name.eq_ignore_ascii_case(name) {
            Some(decode_basic_xml_entities(value.trim()))
        } else {
            None
        }
    })
}

fn pem_block_label(text: &str) -> Option<String> {
    text.lines().find_map(|line| {
        let trimmed = line.trim();
        let value = trimmed
            .strip_prefix("-----BEGIN ")
            .and_then(|rest| rest.strip_suffix("-----"))?;
        Some(value.to_string())
    })
}

fn parse_font_name_table(bytes: &[u8]) -> Vec<(String, String)> {
    let Some(offset) = font_directory_offset(bytes) else {
        return Vec::new();
    };
    if bytes.len() < offset + 12 {
        return Vec::new();
    }

    let table_count = read_be_u16(bytes, offset + 4) as usize;
    let records_start = offset + 12;
    let mut name_offset = 0usize;
    let mut name_length = 0usize;
    for index in 0..table_count {
        let record = records_start + (index * 16);
        if bytes.len() < record + 16 {
            break;
        }

        if &bytes[record..record + 4] == b"name" {
            name_offset = read_be_u32(bytes, record + 8) as usize;
            name_length = read_be_u32(bytes, record + 12) as usize;
            break;
        }
    }

    if name_offset == 0 || name_length == 0 || bytes.len() < name_offset + name_length {
        return Vec::new();
    }

    let table = &bytes[name_offset..name_offset + name_length];
    if table.len() < 6 {
        return Vec::new();
    }

    let count = read_be_u16(table, 2) as usize;
    let string_offset = read_be_u16(table, 4) as usize;
    let mut names = Vec::new();
    for index in 0..count {
        let record = 6 + (index * 12);
        if table.len() < record + 12 {
            break;
        }

        let platform_id = read_be_u16(table, record);
        let name_id = read_be_u16(table, record + 6);
        let length = read_be_u16(table, record + 8) as usize;
        let offset = read_be_u16(table, record + 10) as usize;
        let value_start = string_offset + offset;
        if table.len() < value_start + length {
            continue;
        }

        let Some(label) = font_name_label(name_id) else {
            continue;
        };
        if names.iter().any(|(existing, _)| existing == label) {
            continue;
        }

        let value = decode_font_name(platform_id, &table[value_start..value_start + length]);
        if !value.trim().is_empty() {
            names.push((label.to_string(), value));
        }
    }

    names
}

fn font_directory_offset(bytes: &[u8]) -> Option<usize> {
    if bytes.len() < 12 {
        return None;
    }

    if &bytes[..4] == b"ttcf" {
        if bytes.len() < 16 {
            return None;
        }

        let offset = read_be_u32(bytes, 12) as usize;
        return (bytes.len() >= offset + 12).then_some(offset);
    }

    Some(0)
}

fn font_name_label(name_id: u16) -> Option<&'static str> {
    match name_id {
        1 => Some("Family"),
        2 => Some("Subfamily"),
        4 => Some("Full name"),
        5 => Some("Version"),
        6 => Some("PostScript name"),
        _ => None,
    }
}

fn decode_font_name(platform_id: u16, bytes: &[u8]) -> String {
    if platform_id == 0 || platform_id == 3 {
        let mut units = Vec::with_capacity(bytes.len() / 2);
        for chunk in bytes.chunks(2) {
            if chunk.len() == 2 {
                units.push(u16::from_be_bytes([chunk[0], chunk[1]]));
            }
        }

        String::from_utf16_lossy(&units)
    } else {
        String::from_utf8_lossy(bytes).into_owned()
    }
}

fn read_be_u16(bytes: &[u8], offset: usize) -> u16 {
    u16::from_be_bytes([bytes[offset], bytes[offset + 1]])
}

fn read_be_u32(bytes: &[u8], offset: usize) -> u32 {
    u32::from_be_bytes([
        bytes[offset],
        bytes[offset + 1],
        bytes[offset + 2],
        bytes[offset + 3],
    ])
}

fn xml_local_text(xml: &str, local_name: &str) -> Option<String> {
    // Accept both <title> and <dc:title> style tags without a full XML parser.
    let patterns = [format!("<{local_name}>"), format!(":{local_name}>")];
    for pattern in patterns {
        let mut search_from = 0usize;
        while let Some(rel) = xml[search_from..].find(&pattern) {
            let start_tag_end = search_from + rel + pattern.len();
            // Ensure we matched a tag end, not an attribute blob mid-tag for `:{name}>`.
            let tag_open_start = xml[..start_tag_end].rfind('<')?;
            let tag_open = &xml[tag_open_start..start_tag_end];
            if tag_open.contains('/') {
                search_from = start_tag_end;
                continue;
            }
            let rest = &xml[start_tag_end..];
            if let Some(close_rel) = rest.find("</") {
                let after_close = &rest[close_rel + 2..];
                if after_close.starts_with(local_name)
                    || after_close
                        .find('>')
                        .map(|idx| after_close[..idx].ends_with(local_name))
                        .unwrap_or(false)
                    || after_close
                        .split('>')
                        .next()
                        .map(|name| {
                            name.ends_with(local_name) || name.contains(&format!(":{local_name}"))
                        })
                        .unwrap_or(false)
                {
                    let raw = &rest[..close_rel];
                    let decoded = decode_basic_xml_entities(raw);
                    let trimmed = decoded.trim();
                    if !trimmed.is_empty() {
                        return Some(trimmed.to_string());
                    }
                }
            }
            search_from = start_tag_end;
        }
    }
    None
}

fn decode_basic_xml_entities(input: &str) -> String {
    input
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&apos;", "'")
        .replace("&amp;", "&")
}

#[cfg(test)]
mod tests {
    use super::*;
    use lopdf::dictionary;
    use std::io::Write;
    use std::path::{Path, PathBuf};
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(label: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("time")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("simplefile-metadata-{label}-{nanos}"));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn field_value<'a>(metadata: &'a FileMetadata, label: &str) -> Option<&'a str> {
        metadata
            .fields
            .iter()
            .find(|(field_label, _)| field_label == label)
            .map(|(_, value)| value.as_str())
    }

    fn write_pdf_fixture(path: &Path) {
        let mut document = lopdf::Document::with_version("1.5");
        let pages_id = document.new_object_id();
        let page_id = document.new_object_id();
        let catalog_id = document.new_object_id();
        let info_id = document.new_object_id();

        document.objects.insert(
            catalog_id,
            lopdf::Object::Dictionary(lopdf::dictionary! {
                "Type" => lopdf::Object::Name(b"Catalog".to_vec()),
                "Pages" => lopdf::Object::Reference(pages_id),
            }),
        );
        document.objects.insert(
            pages_id,
            lopdf::Object::Dictionary(lopdf::dictionary! {
                "Type" => lopdf::Object::Name(b"Pages".to_vec()),
                "Kids" => lopdf::Object::Array(vec![lopdf::Object::Reference(page_id)]),
                "Count" => 1,
            }),
        );
        document.objects.insert(
            page_id,
            lopdf::Object::Dictionary(lopdf::dictionary! {
                "Type" => lopdf::Object::Name(b"Page".to_vec()),
                "Parent" => lopdf::Object::Reference(pages_id),
                "MediaBox" => lopdf::Object::Array(vec![
                    lopdf::Object::Integer(0),
                    lopdf::Object::Integer(0),
                    lopdf::Object::Integer(1),
                    lopdf::Object::Integer(1),
                ]),
            }),
        );
        document.objects.insert(
            info_id,
            lopdf::Object::Dictionary(lopdf::dictionary! {
                "Title" => lopdf::Object::String(
                    b"Fixture PDF".to_vec(),
                    lopdf::StringFormat::Literal,
                ),
                "Author" => lopdf::Object::String(
                    b"SumaFile".to_vec(),
                    lopdf::StringFormat::Literal,
                ),
            }),
        );
        document
            .trailer
            .set("Root", lopdf::Object::Reference(catalog_id));
        document
            .trailer
            .set("Info", lopdf::Object::Reference(info_id));
        document.save(path).unwrap();
    }

    fn write_wav_fixture(path: &Path) {
        let channels = 1u16;
        let sample_rate = 8_000u32;
        let bits_per_sample = 16u16;
        let bytes_per_sample = u32::from(bits_per_sample / 8);
        let data_size = sample_rate * u32::from(channels) * bytes_per_sample;
        let byte_rate = sample_rate * u32::from(channels) * bytes_per_sample;
        let block_align = channels * (bits_per_sample / 8);

        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&(36 + data_size).to_le_bytes());
        bytes.extend_from_slice(b"WAVE");
        bytes.extend_from_slice(b"fmt ");
        bytes.extend_from_slice(&16u32.to_le_bytes());
        bytes.extend_from_slice(&1u16.to_le_bytes());
        bytes.extend_from_slice(&channels.to_le_bytes());
        bytes.extend_from_slice(&sample_rate.to_le_bytes());
        bytes.extend_from_slice(&byte_rate.to_le_bytes());
        bytes.extend_from_slice(&block_align.to_le_bytes());
        bytes.extend_from_slice(&bits_per_sample.to_le_bytes());
        bytes.extend_from_slice(b"data");
        bytes.extend_from_slice(&data_size.to_le_bytes());
        bytes.resize(44 + data_size as usize, 0);
        fs::write(path, bytes).unwrap();
    }

    fn atom(kind: &[u8; 4], payload: Vec<u8>) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(8 + payload.len());
        bytes.extend_from_slice(&((8 + payload.len()) as u32).to_be_bytes());
        bytes.extend_from_slice(kind);
        bytes.extend_from_slice(&payload);
        bytes
    }

    fn write_mp4_fixture(path: &Path) {
        let mut ftyp_payload = Vec::new();
        ftyp_payload.extend_from_slice(b"isom");
        ftyp_payload.extend_from_slice(&0u32.to_be_bytes());
        ftyp_payload.extend_from_slice(b"isom");

        let mut mvhd = vec![0u8; 100];
        mvhd[0] = 0;
        mvhd[12..16].copy_from_slice(&1_000u32.to_be_bytes());
        mvhd[16..20].copy_from_slice(&2_000u32.to_be_bytes());

        let mut tkhd = vec![0u8; 100];
        tkhd[0] = 0;
        tkhd[76..80].copy_from_slice(&(640u32 << 16).to_be_bytes());
        tkhd[80..84].copy_from_slice(&(360u32 << 16).to_be_bytes());

        let mut moov_payload = atom(b"mvhd", mvhd);
        moov_payload.extend(atom(b"trak", atom(b"tkhd", tkhd)));

        let mut bytes = atom(b"ftyp", ftyp_payload);
        bytes.extend(atom(b"moov", moov_payload));
        fs::write(path, bytes).unwrap();
    }

    fn utf16be(value: &str) -> Vec<u8> {
        value
            .encode_utf16()
            .flat_map(u16::to_be_bytes)
            .collect::<Vec<u8>>()
    }

    fn push_u16(bytes: &mut Vec<u8>, value: u16) {
        bytes.extend_from_slice(&value.to_be_bytes());
    }

    fn push_u32(bytes: &mut Vec<u8>, value: u32) {
        bytes.extend_from_slice(&value.to_be_bytes());
    }

    fn write_ttf_name_fixture(path: &Path) {
        let family = utf16be("Suma Sans");
        let full_name = utf16be("Suma Sans Regular");
        let string_offset = 6 + (2 * 12);
        let mut name = Vec::new();
        push_u16(&mut name, 0);
        push_u16(&mut name, 2);
        push_u16(&mut name, string_offset as u16);
        for (name_id, length, offset) in [
            (1u16, family.len() as u16, 0u16),
            (4u16, full_name.len() as u16, family.len() as u16),
        ] {
            push_u16(&mut name, 3);
            push_u16(&mut name, 1);
            push_u16(&mut name, 0x0409);
            push_u16(&mut name, name_id);
            push_u16(&mut name, length);
            push_u16(&mut name, offset);
        }
        name.extend_from_slice(&family);
        name.extend_from_slice(&full_name);

        let name_offset = 28u32;
        let mut font = Vec::new();
        font.extend_from_slice(&0x0001_0000u32.to_be_bytes());
        push_u16(&mut font, 1);
        push_u16(&mut font, 0);
        push_u16(&mut font, 0);
        push_u16(&mut font, 0);
        font.extend_from_slice(b"name");
        push_u32(&mut font, 0);
        push_u32(&mut font, name_offset);
        push_u32(&mut font, name.len() as u32);
        font.extend_from_slice(&name);
        fs::write(path, font).unwrap();
    }

    #[test]
    fn classify_extension_covers_supported_kinds() {
        assert_eq!(classify_extension("png"), MetadataKind::Image);
        assert_eq!(classify_extension("avif"), MetadataKind::Image);
        assert_eq!(classify_extension("PDF"), MetadataKind::Pdf);
        assert_eq!(classify_extension("mp3"), MetadataKind::Audio);
        assert_eq!(classify_extension("mp4"), MetadataKind::Video);
        assert_eq!(classify_extension("m2ts"), MetadataKind::Video);
        assert_eq!(classify_extension("docx"), MetadataKind::Office);
        assert_eq!(classify_extension("json"), MetadataKind::Data);
        assert_eq!(classify_extension("zip"), MetadataKind::Archive);
        assert_eq!(classify_extension("ttf"), MetadataKind::Font);
        assert_eq!(classify_extension("epub"), MetadataKind::Ebook);
        assert_eq!(classify_extension("eml"), MetadataKind::Email);
        assert_eq!(classify_extension("ics"), MetadataKind::Calendar);
        assert_eq!(classify_extension("vcf"), MetadataKind::Contact);
        assert_eq!(classify_extension("pem"), MetadataKind::Certificate);
        assert_eq!(classify_extension("exe"), MetadataKind::Unsupported);
    }

    #[test]
    fn format_duration_renders_minutes_and_hours() {
        assert_eq!(format_duration(Duration::from_secs(65)), "1:05");
        assert_eq!(format_duration(Duration::from_secs(3661)), "1:01:01");
    }

    #[test]
    fn xml_local_text_reads_namespaced_and_plain_tags() {
        let xml = r#"
            <cp:coreProperties>
              <dc:title>Quarterly Report</dc:title>
              <dc:creator>Ada</dc:creator>
              <cp:lastModifiedBy>Grace</cp:lastModifiedBy>
            </cp:coreProperties>
        "#;
        assert_eq!(
            xml_local_text(xml, "title").as_deref(),
            Some("Quarterly Report")
        );
        assert_eq!(xml_local_text(xml, "creator").as_deref(), Some("Ada"));
        assert_eq!(
            xml_local_text(xml, "lastModifiedBy").as_deref(),
            Some("Grace")
        );
    }

    #[test]
    fn office_metadata_reads_docx_core_props() {
        let dir = temp_dir("docx");
        let path = dir.join("sample.docx");

        {
            let file = File::create(&path).unwrap();
            let mut zip = zip::ZipWriter::new(file);
            let options = zip::write::SimpleFileOptions::default()
                .compression_method(zip::CompressionMethod::Stored);
            zip.start_file("docProps/core.xml", options).unwrap();
            zip.write_all(
                br#"<?xml version="1.0"?>
                <cp:coreProperties xmlns:cp="http://example" xmlns:dc="http://example">
                  <dc:title>Budget</dc:title>
                  <dc:creator>Finance</dc:creator>
                </cp:coreProperties>"#,
            )
            .unwrap();
            zip.start_file("docProps/app.xml", options).unwrap();
            zip.write_all(
                br#"<?xml version="1.0"?>
                <Properties>
                  <Application>SimpleFile Test</Application>
                  <Pages>3</Pages>
                  <Words>120</Words>
                </Properties>"#,
            )
            .unwrap();
            zip.finish().unwrap();
        }

        let meta = extract_office_metadata(&path, "docx").unwrap();
        assert_eq!(meta.kind, "office");
        assert!(meta
            .fields
            .iter()
            .any(|(k, v)| k == "Title" && v == "Budget"));
        assert!(meta
            .fields
            .iter()
            .any(|(k, v)| k == "Creator" && v == "Finance"));
        assert!(meta.fields.iter().any(|(k, v)| k == "Pages" && v == "3"));
        assert!(meta.summary.as_deref() == Some("Budget"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn pdf_metadata_reads_generated_fixture_info() {
        let dir = temp_dir("pdf");
        let path = dir.join("sample.pdf");
        write_pdf_fixture(&path);

        let meta = get_file_metadata(path.to_string_lossy().into_owned()).unwrap();

        assert_eq!(meta.kind, "pdf");
        assert_eq!(field_value(&meta, "Pages"), Some("1"));
        assert_eq!(field_value(&meta, "Title"), Some("Fixture PDF"));
        assert_eq!(field_value(&meta, "Author"), Some("SumaFile"));
        assert_eq!(meta.summary.as_deref(), Some("1 pages · Fixture PDF"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn audio_metadata_reads_wav_duration_fixture() {
        let dir = temp_dir("wav");
        let path = dir.join("tone.wav");
        write_wav_fixture(&path);

        let meta = get_file_metadata(path.to_string_lossy().into_owned()).unwrap();

        assert_eq!(meta.kind, "audio");
        assert_eq!(field_value(&meta, "Duration"), Some("0:01"));
        assert_eq!(field_value(&meta, "Sample rate"), Some("8000 Hz"));
        assert_eq!(field_value(&meta, "Channels"), Some("1"));
        assert_eq!(meta.summary.as_deref(), Some("0:01"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn video_metadata_reads_mp4_duration_dimensions_and_brand() {
        let dir = temp_dir("mp4");
        let path = dir.join("clip.mp4");
        write_mp4_fixture(&path);

        let meta = get_file_metadata(path.to_string_lossy().into_owned()).unwrap();

        assert_eq!(meta.kind, "video");
        assert_eq!(field_value(&meta, "Brand"), Some("ISOM"));
        assert_eq!(field_value(&meta, "Duration"), Some("0:02"));
        assert_eq!(field_value(&meta, "Dimensions"), Some("640 × 360"));
        assert_eq!(meta.summary.as_deref(), Some("640 × 360 · 0:02"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn data_metadata_reads_markdown_json_and_csv_shapes() {
        let dir = temp_dir("data");
        let markdown = dir.join("notes.md");
        let json = dir.join("package.json");
        let csv = dir.join("people.csv");
        fs::write(
            &markdown,
            "# Preview Notes\n\nA small [link](https://example.com).",
        )
        .unwrap();
        fs::write(&json, r#"{"name":"SumaFile","items":[1,2]}"#).unwrap();
        fs::write(&csv, "Name,Role\nConnie,Owner\nAda,Engineer\n").unwrap();

        let markdown_meta = get_file_metadata(markdown.to_string_lossy().into_owned()).unwrap();
        let json_meta = get_file_metadata(json.to_string_lossy().into_owned()).unwrap();
        let csv_meta = get_file_metadata(csv.to_string_lossy().into_owned()).unwrap();

        assert_eq!(markdown_meta.kind, "data");
        assert_eq!(field_value(&markdown_meta, "Title"), Some("Preview Notes"));
        assert_eq!(field_value(&markdown_meta, "Links"), Some("1"));
        assert_eq!(field_value(&json_meta, "Structure"), Some("Object"));
        assert_eq!(field_value(&json_meta, "Top-level keys"), Some("2"));
        assert_eq!(field_value(&csv_meta, "Rows"), Some("3"));
        assert_eq!(field_value(&csv_meta, "Columns"), Some("2"));
        assert_eq!(field_value(&csv_meta, "Headers"), Some("Name, Role"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn archive_metadata_reads_zip_entries() {
        let dir = temp_dir("zip-meta");
        let path = dir.join("bundle.zip");
        {
            let file = File::create(&path).unwrap();
            let mut zip = zip::ZipWriter::new(file);
            let options = zip::write::SimpleFileOptions::default()
                .compression_method(zip::CompressionMethod::Stored);
            zip.start_file("folder/readme.txt", options).unwrap();
            zip.write_all(b"hello").unwrap();
            zip.start_file("data.json", options).unwrap();
            zip.write_all(br#"{"ok":true}"#).unwrap();
            zip.finish().unwrap();
        }

        let meta = get_file_metadata(path.to_string_lossy().into_owned()).unwrap();

        assert_eq!(meta.kind, "archive");
        assert_eq!(field_value(&meta, "Format"), Some("ZIP archive"));
        assert_eq!(field_value(&meta, "Files"), Some("2"));
        assert!(field_value(&meta, "Sample entries")
            .is_some_and(|value| value.contains("folder/readme.txt")));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn ebook_metadata_reads_epub_package_metadata() {
        let dir = temp_dir("epub");
        let path = dir.join("book.epub");
        {
            let file = File::create(&path).unwrap();
            let mut zip = zip::ZipWriter::new(file);
            let options = zip::write::SimpleFileOptions::default()
                .compression_method(zip::CompressionMethod::Stored);
            zip.start_file("META-INF/container.xml", options).unwrap();
            zip.write_all(
                br#"<container><rootfiles><rootfile full-path="OPS/content.opf" /></rootfiles></container>"#,
            )
            .unwrap();
            zip.start_file("OPS/content.opf", options).unwrap();
            zip.write_all(
                br#"<package><metadata><dc:title>Rusty Preview</dc:title><dc:creator>SumaFile</dc:creator><dc:language>en</dc:language></metadata></package>"#,
            )
            .unwrap();
            zip.start_file("OPS/chapter.xhtml", options).unwrap();
            zip.write_all(b"<html><body>Chapter</body></html>").unwrap();
            zip.finish().unwrap();
        }

        let meta = get_file_metadata(path.to_string_lossy().into_owned()).unwrap();

        assert_eq!(meta.kind, "ebook");
        assert_eq!(field_value(&meta, "Title"), Some("Rusty Preview"));
        assert_eq!(field_value(&meta, "Creator"), Some("SumaFile"));
        assert_eq!(field_value(&meta, "Documents"), Some("1"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn message_metadata_reads_email_calendar_and_contact_headers() {
        let dir = temp_dir("message");
        let email = dir.join("message.eml");
        let calendar = dir.join("event.ics");
        let contact = dir.join("person.vcf");
        fs::write(
            &email,
            "Subject: Preview mail\nFrom: Connie <c@example.com>\nTo: Ada <a@example.com>\nDate: Tue, 8 Sep 2026 12:00:00 -0500\n\nBody",
        )
        .unwrap();
        fs::write(
            &calendar,
            "BEGIN:VCALENDAR\nBEGIN:VEVENT\nSUMMARY:Preview review\nDTSTART:20260908T120000Z\nLOCATION:Desk\nEND:VEVENT\nEND:VCALENDAR\n",
        )
        .unwrap();
        fs::write(
            &contact,
            "BEGIN:VCARD\nFN:Connie Combs\nORG:SumaFile\nEMAIL:connie@example.com\nEND:VCARD\n",
        )
        .unwrap();

        let email_meta = get_file_metadata(email.to_string_lossy().into_owned()).unwrap();
        let calendar_meta = get_file_metadata(calendar.to_string_lossy().into_owned()).unwrap();
        let contact_meta = get_file_metadata(contact.to_string_lossy().into_owned()).unwrap();

        assert_eq!(email_meta.kind, "email");
        assert_eq!(field_value(&email_meta, "Subject"), Some("Preview mail"));
        assert_eq!(calendar_meta.kind, "calendar");
        assert_eq!(
            field_value(&calendar_meta, "Summary"),
            Some("Preview review")
        );
        assert_eq!(field_value(&calendar_meta, "Events"), Some("1"));
        assert_eq!(contact_meta.kind, "contact");
        assert_eq!(field_value(&contact_meta, "Name"), Some("Connie Combs"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn font_metadata_reads_ttf_name_table() {
        let dir = temp_dir("font");
        let path = dir.join("suma.ttf");
        write_ttf_name_fixture(&path);

        let meta = get_file_metadata(path.to_string_lossy().into_owned()).unwrap();

        assert_eq!(meta.kind, "font");
        assert_eq!(field_value(&meta, "Family"), Some("Suma Sans"));
        assert_eq!(field_value(&meta, "Full name"), Some("Suma Sans Regular"));
        assert_eq!(meta.summary.as_deref(), Some("Suma Sans Regular"));

        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn unsupported_extension_returns_empty_metadata() {
        assert_eq!(classify_extension("bin"), MetadataKind::Unsupported);
        assert_eq!(classify_extension("exe"), MetadataKind::Unsupported);
    }

    #[test]
    fn size_limit_rejects_oversized_files() {
        let dir = temp_dir("limit");
        let path = dir.join("huge.bin");
        fs::write(&path, vec![0u8; 64]).unwrap();
        let err = ensure_size_limit(&path, 16, "test").unwrap_err();
        assert!(err.contains("too large"));
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn parse_mvhd_version0() {
        let mut buf = vec![0u8; 20];
        buf[0] = 0; // version
        buf[12..16].copy_from_slice(&1000u32.to_be_bytes()); // timescale
        buf[16..20].copy_from_slice(&2500u32.to_be_bytes()); // duration
        let mut timescale = 0;
        let mut duration = 0;
        parse_mvhd(&buf, &mut timescale, &mut duration);
        assert_eq!(timescale, 1000);
        assert_eq!(duration, 2500);
    }
}
