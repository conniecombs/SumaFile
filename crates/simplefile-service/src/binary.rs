use serde_json::Value;
use simplefile_core::models::{
    DirectoryListing, DirectoryListingChunk, FileChangeEvent, FileEntry, ListingViewPage,
    ProgressUpdate, SearchResult, ThumbnailResult,
};
use simplefile_ipc::{
    BINARY_FILE_CHANGE, BINARY_FRAME_MAGIC, BINARY_FRAME_VERSION, BINARY_IMAGE_PREVIEW_RESULT,
    BINARY_LISTING_VIEW_PAGE, BINARY_LIST_DIRECTORY_CHUNK, BINARY_LIST_DIRECTORY_RESULT,
    BINARY_OPERATION_PROGRESS, BINARY_SEARCH_RESULTS_BATCH, BINARY_SEARCH_RESULTS_RESULT,
    BINARY_THUMBNAILS_RESULT, BINARY_THUMBNAIL_RESULT,
};

type EncodeResult<T> = Result<T, String>;

pub(crate) fn request_id_i32(id: &Option<Value>) -> Option<i32> {
    let value = id.as_ref()?;
    if let Some(number) = value.as_i64() {
        i32::try_from(number).ok()
    } else if let Some(number) = value.as_u64() {
        i32::try_from(number).ok()
    } else {
        None
    }
}

pub(crate) fn encode_directory_listing_chunk(
    request_id: i32,
    chunk: &DirectoryListingChunk,
) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_LIST_DIRECTORY_CHUNK);
    writer.i32(request_id);
    write_directory_listing_chunk(&mut writer, chunk)?;
    Ok(writer.finish())
}

pub(crate) fn encode_directory_listing_result(
    request_id: i32,
    listing: &DirectoryListing,
) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_LIST_DIRECTORY_RESULT);
    writer.i32(request_id);
    writer.string(&listing.path)?;
    writer.opt_string(listing.parent.as_deref())?;
    writer.entries(&listing.entries)?;
    writer.bool(listing.is_network);
    Ok(writer.finish())
}

/// A listing view page. Unlike the list_directory frames, each entry carries
/// its Hidden and System attributes (one flags byte), and no retired
/// git_status field. Frame version 2 added created, accessed and the
/// attribute letters to every entry encoding.
pub(crate) fn encode_listing_view_page(
    request_id: i32,
    page: &ListingViewPage,
) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_LISTING_VIEW_PAGE);
    writer.i32(request_id);
    writer.string(&page.view_id)?;
    writer.string(&page.path)?;
    writer.opt_string(page.parent.as_deref())?;
    writer.bool(page.is_network);
    writer.u32(page.total);
    writer.u64(page.revision);
    writer.u32(page.offset);
    writer.len(page.entries.len())?;
    for entry in &page.entries {
        writer.string(&entry.name)?;
        writer.string(&entry.path)?;
        writer.bytes_raw(&[u8::from(entry.is_dir)
            | u8::from(entry.is_symlink) << 1
            | u8::from(entry.is_hidden) << 2
            | u8::from(entry.is_system) << 3]);
        writer.u64(entry.size);
        writer.string(&entry.modified)?;
        writer.string(&entry.extension)?;
        writer.opt_string(entry.permissions.as_deref())?;
        writer.opt_string(entry.symlink_target.as_deref())?;
        writer.string(&entry.created)?;
        writer.string(&entry.accessed)?;
        writer.string(&entry.attributes)?;
    }
    Ok(writer.finish())
}

pub(crate) fn encode_search_results_batch(
    search_id: Option<&str>,
    results: &[SearchResult],
) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_SEARCH_RESULTS_BATCH);
    writer.opt_string(search_id)?;
    writer.search_results(results)?;
    Ok(writer.finish())
}

pub(crate) fn encode_search_results_result(
    request_id: i32,
    results: &[SearchResult],
) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_SEARCH_RESULTS_RESULT);
    writer.i32(request_id);
    writer.search_results(results)?;
    Ok(writer.finish())
}

pub(crate) fn encode_progress_update(update: &ProgressUpdate) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_OPERATION_PROGRESS);
    writer.string(&update.operation_id)?;
    writer.string(&update.operation_type)?;
    writer.u64(update.current);
    writer.u64(update.total);
    writer.u64(update.current_files);
    writer.u64(update.total_files);
    writer.string(&update.current_item)?;
    writer.string(&update.status)?;
    writer.opt_string(update.error.as_deref())?;
    writer.u64(update.transferred);
    Ok(writer.finish())
}

pub(crate) fn encode_file_change(change: &FileChangeEvent) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_FILE_CHANGE);
    writer.string(&change.path)?;
    writer.string(&change.kind)?;
    Ok(writer.finish())
}

pub(crate) fn encode_thumbnail_result(request_id: i32, data: &[u8]) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_THUMBNAIL_RESULT);
    writer.i32(request_id);
    writer.bytes(data)?;
    Ok(writer.finish())
}

/// `i32 request id | string format | u32 width | u32 height | bytes data`
pub(crate) fn encode_image_preview_result(
    request_id: i32,
    preview: &simplefile_core::media::EncodedImage,
) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_IMAGE_PREVIEW_RESULT);
    writer.i32(request_id);
    writer.string(&preview.format)?;
    writer.u32(preview.width);
    writer.u32(preview.height);
    writer.bytes(&preview.data)?;
    Ok(writer.finish())
}

pub(crate) fn encode_thumbnail_results_result(
    request_id: i32,
    results: &[ThumbnailResult],
) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_THUMBNAILS_RESULT);
    writer.i32(request_id);
    writer.len(results.len())?;
    for result in results {
        writer.string(&result.path)?;
        writer.opt_bytes(result.data.as_deref())?;
        writer.opt_string(result.error.as_deref())?;
    }
    Ok(writer.finish())
}

fn write_directory_listing_chunk(
    writer: &mut BinaryWriter,
    chunk: &DirectoryListingChunk,
) -> EncodeResult<()> {
    writer.string(&chunk.path)?;
    writer.opt_string(chunk.parent.as_deref())?;
    writer.entries(&chunk.entries)?;
    writer.u32(chunk.chunk_index);
    writer.bool(chunk.done);
    writer.bool(chunk.is_network);
    Ok(())
}

struct BinaryWriter {
    bytes: Vec<u8>,
}

impl BinaryWriter {
    fn new(tag: u8) -> Self {
        let mut bytes = Vec::with_capacity(256);
        bytes.extend_from_slice(&BINARY_FRAME_MAGIC);
        bytes.push(BINARY_FRAME_VERSION);
        bytes.push(tag);
        Self { bytes }
    }

    fn finish(self) -> Vec<u8> {
        self.bytes
    }

    fn bytes_raw(&mut self, value: &[u8]) {
        self.bytes.extend_from_slice(value);
    }

    fn bool(&mut self, value: bool) {
        self.bytes.push(u8::from(value));
    }

    fn i32(&mut self, value: i32) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn u32(&mut self, value: u32) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn u64(&mut self, value: u64) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn len(&mut self, len: usize) -> EncodeResult<()> {
        let len = u32::try_from(len).map_err(|_| "binary payload count exceeds u32".to_string())?;
        self.u32(len);
        Ok(())
    }

    fn string(&mut self, value: &str) -> EncodeResult<()> {
        let bytes = value.as_bytes();
        self.len(bytes.len())?;
        self.bytes.extend_from_slice(bytes);
        Ok(())
    }

    fn opt_string(&mut self, value: Option<&str>) -> EncodeResult<()> {
        match value {
            Some(value) => {
                self.bool(true);
                self.string(value)
            }
            None => {
                self.bool(false);
                Ok(())
            }
        }
    }

    fn bytes(&mut self, value: &[u8]) -> EncodeResult<()> {
        self.len(value.len())?;
        self.bytes.extend_from_slice(value);
        Ok(())
    }

    fn opt_bytes(&mut self, value: Option<&[u8]>) -> EncodeResult<()> {
        match value {
            Some(value) => {
                self.bool(true);
                self.bytes(value)
            }
            None => {
                self.bool(false);
                Ok(())
            }
        }
    }

    fn entries(&mut self, entries: &[FileEntry]) -> EncodeResult<()> {
        self.len(entries.len())?;
        for entry in entries {
            self.string(&entry.name)?;
            self.string(&entry.path)?;
            self.bool(entry.is_dir);
            self.bool(entry.is_symlink);
            self.u64(entry.size);
            self.string(&entry.modified)?;
            self.string(&entry.extension)?;
            self.opt_string(entry.permissions.as_deref())?;
            self.opt_string(entry.symlink_target.as_deref())?;
            self.opt_string(entry.git_status.as_deref())?;
            self.string(&entry.created)?;
            self.string(&entry.accessed)?;
            self.string(&entry.attributes)?;
        }
        Ok(())
    }

    fn search_results(&mut self, results: &[SearchResult]) -> EncodeResult<()> {
        self.len(results.len())?;
        for result in results {
            self.string(&result.name)?;
            self.string(&result.path)?;
            self.bool(result.is_dir);
            self.u64(result.size);
            self.string(&result.modified)?;
            self.string(&result.extension)?;
            self.string(&result.match_type)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The same bytes are decoded in the host's ListingViewIpcTests
    /// (BinaryPage_DecodesTheServicesGoldenFrame): the two sides agree on
    /// the page frame byte for byte.
    const GOLDEN_LISTING_VIEW_PAGE: &str = "5346423103090700000002000000763104000000433A5C7700010300000002010000000000000100000002000000010000006400000000090000000000000000010000006D00000000000000000000000000000000000005000000662E74787408000000583A5C662E74787406010200000000000000000000030000007478740102000000727701010000007401000000630100000061020000005241";

    fn entry(name: &str) -> FileEntry {
        FileEntry {
            name: name.to_string(),
            path: String::new(),
            is_dir: false,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size: 0,
            modified: String::new(),
            extension: String::new(),
            permissions: None,
            symlink_target: None,
            git_status: None,
            created: String::new(),
            accessed: String::new(),
            attributes: String::new(),
        }
    }

    /// The host decodes these same bytes in its test
    /// `BinaryPreview_DecodesTheServicesGoldenFrame`: request 7, a 640 by
    /// 480 PNG of five bytes.
    const GOLDEN_IMAGE_PREVIEW: &str =
        "53464231030A0700000003000000706E6780020000E0010000050000000102030405";

    #[test]
    fn image_preview_frame_matches_the_golden_bytes() {
        let preview = simplefile_core::media::EncodedImage {
            format: "png".to_string(),
            width: 640,
            height: 480,
            data: vec![1, 2, 3, 4, 5],
        };

        let frame = encode_image_preview_result(7, &preview).unwrap();

        let hex: String = frame.iter().map(|byte| format!("{byte:02X}")).collect();
        assert_eq!(GOLDEN_IMAGE_PREVIEW, hex);
    }

    #[test]
    fn listing_view_page_frame_matches_the_golden_bytes() {
        let page = ListingViewPage {
            view_id: "v1".to_string(),
            path: r"C:\w".to_string(),
            parent: None,
            is_network: true,
            total: 3,
            revision: 258,
            offset: 1,
            entries: vec![
                FileEntry {
                    is_dir: true,
                    is_system: true,
                    modified: "m".to_string(),
                    ..entry("d")
                },
                FileEntry {
                    path: r"X:\f.txt".to_string(),
                    is_symlink: true,
                    is_hidden: true,
                    size: 513,
                    extension: "txt".to_string(),
                    permissions: Some("rw".to_string()),
                    symlink_target: Some("t".to_string()),
                    // Retired and never sent in a page.
                    git_status: Some("M".to_string()),
                    created: "c".to_string(),
                    accessed: "a".to_string(),
                    attributes: "RA".to_string(),
                    ..entry("f.txt")
                },
            ],
        };

        let frame = encode_listing_view_page(7, &page).expect("the page encodes");
        let hex: String = frame.iter().map(|byte| format!("{byte:02X}")).collect();
        assert_eq!(GOLDEN_LISTING_VIEW_PAGE, hex);
    }
}
