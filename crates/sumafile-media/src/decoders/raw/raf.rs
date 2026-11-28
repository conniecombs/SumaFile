//! Fujifilm RAF: a header that says where the preview JPEG is.

use std::ops::Range;

pub(super) const MAGIC: &[u8] = b"FUJIFILMCCD-RAW";
/// The header up to and including the preview's offset and length.
pub(super) const HEADER_BYTES: usize = 92;

/// The preview's byte range, from the first `HEADER_BYTES` of the file.
pub(super) fn jpeg_range(header: &[u8]) -> Option<Range<u64>> {
    if !header.starts_with(MAGIC) {
        return None;
    }
    let long = |at: usize| {
        let bytes = header.get(at..at + 4)?;
        Some(u64::from(u32::from_be_bytes([
            bytes[0], bytes[1], bytes[2], bytes[3],
        ])))
    };
    let (start, length) = (long(84)?, long(88)?);
    Some(start..start + length)
}
