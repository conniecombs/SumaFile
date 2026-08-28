use serde_json::Value;
use simplefile_core::models::{
    DirectoryListing, DirectoryListingChunk, FileChangeEvent, FileEntry, ProgressUpdate,
    SearchResult, ThumbnailResult,
};
use simplefile_ipc::{
    BINARY_FILE_CHANGE, BINARY_FRAME_MAGIC, BINARY_FRAME_VERSION, BINARY_LIST_DIRECTORY_CHUNK,
    BINARY_LIST_DIRECTORY_RESULT, BINARY_OPERATION_PROGRESS, BINARY_SEARCH_RESULTS_BATCH,
    BINARY_SEARCH_RESULTS_RESULT, BINARY_THUMBNAILS_RESULT, BINARY_THUMBNAIL_RESULT,
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

pub(crate) fn encode_search_results_batch(results: &[SearchResult]) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_SEARCH_RESULTS_BATCH);
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
    Ok(writer.finish())
}

pub(crate) fn encode_file_change(change: &FileChangeEvent) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_FILE_CHANGE);
    writer.string(&change.path)?;
    writer.string(&change.kind)?;
    Ok(writer.finish())
}

pub(crate) fn encode_thumbnail_result(request_id: i32, data: &str) -> EncodeResult<Vec<u8>> {
    let mut writer = BinaryWriter::new(BINARY_THUMBNAIL_RESULT);
    writer.i32(request_id);
    writer.string(data)?;
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
        writer.opt_string(result.data.as_deref())?;
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
