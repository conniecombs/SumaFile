use crate::models::{FileMetadata, ImageMetadata};
use crate::utils::validate_existing_path_no_resolve;
use image::GenericImageView;
use lofty::file::{AudioFile, TaggedFileExt};
use lofty::tag::{Accessor, ItemKey};
use std::fs::{self, File};
use std::io::{BufReader, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
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
            let image = extract_image_metadata(&path_buf)?;
            Ok(file_metadata_from_image(image))
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
    Unsupported,
}

fn resolve_readable_path(path: &str) -> Result<PathBuf, String> {
    if crate::archive::is_archive_virtual_path(path) {
        return crate::archive::materialize_archive_entry_to_temp(path);
    }

    validate_existing_path_no_resolve(path)
}

fn classify_extension(extension: &str) -> MetadataKind {
    match extension.to_ascii_lowercase().as_str() {
        "png" | "jpg" | "jpeg" | "gif" | "bmp" | "webp" | "tif" | "tiff" => MetadataKind::Image,
        "pdf" => MetadataKind::Pdf,
        "mp3" | "flac" | "ogg" | "oga" | "opus" | "wav" | "m4a" | "aac" | "aiff" | "aif"
        | "wma" | "wv" | "ape" => MetadataKind::Audio,
        "mp4" | "m4v" | "mov" | "webm" | "mkv" | "avi" | "wmv" => MetadataKind::Video,
        "docx" | "xlsx" | "pptx" | "odt" | "ods" | "odp" => MetadataKind::Office,
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
    use std::io::Write;
    use std::path::PathBuf;
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

    #[test]
    fn classify_extension_covers_supported_kinds() {
        assert_eq!(classify_extension("png"), MetadataKind::Image);
        assert_eq!(classify_extension("PDF"), MetadataKind::Pdf);
        assert_eq!(classify_extension("mp3"), MetadataKind::Audio);
        assert_eq!(classify_extension("mp4"), MetadataKind::Video);
        assert_eq!(classify_extension("docx"), MetadataKind::Office);
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
