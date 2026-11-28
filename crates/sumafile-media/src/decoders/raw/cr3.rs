//! Canon CR3: an ISO media file whose boxes include a preview (`PRVW`) and
//! a thumbnail (`THMB`), each a short header followed by a JPEG.

use std::io::{Read, Seek, SeekFrom};
use std::ops::Range;

const MAX_BOXES: usize = 64;
/// How far into a preview box its JPEG may start.
const JPEG_SEARCH_BYTES: u64 = 64;

/// The boxes laid end to end in `span`: each one's type and the range of
/// its contents.
fn boxes<R: Read + Seek>(input: &mut R, span: Range<u64>) -> Vec<([u8; 4], Range<u64>)> {
    let mut found = Vec::new();
    let mut at = span.start;
    while found.len() < MAX_BOXES && at.saturating_add(8) <= span.end {
        let mut header = [0u8; 16];
        if input.seek(SeekFrom::Start(at)).is_err() || input.read_exact(&mut header[..8]).is_err() {
            break;
        }
        let kind = [header[4], header[5], header[6], header[7]];
        let mut size = u64::from(u32::from_be_bytes([
            header[0], header[1], header[2], header[3],
        ]));
        let mut header_bytes = 8;
        if size == 1 {
            // The size did not fit 32 bits and follows the type.
            if input.read_exact(&mut header[8..]).is_err() {
                break;
            }
            size = u64::from_be_bytes([
                header[8], header[9], header[10], header[11], header[12], header[13], header[14],
                header[15],
            ]);
            header_bytes = 16;
        } else if size == 0 {
            size = span.end - at;
        }
        let Some(end) = at.checked_add(size).filter(|end| *end <= span.end) else {
            break;
        };
        if size < header_bytes {
            break;
        }
        found.push((kind, at + header_bytes..end));
        at = end;
    }
    found
}

/// Where the file's preview and thumbnail JPEGs are.
pub(super) fn jpeg_ranges<R: Read + Seek>(input: &mut R, length: u64) -> Vec<Range<u64>> {
    // Canon's boxes sit inside `uuid` boxes, at the top level and in `moov`.
    let mut holders = Vec::new();
    for (kind, contents) in boxes(input, 0..length) {
        match &kind {
            b"uuid" => holders.push(contents),
            b"moov" => {
                for (kind, contents) in boxes(input, contents) {
                    if &kind == b"uuid" {
                        holders.push(contents);
                    }
                }
            }
            _ => {}
        }
    }

    let mut jpegs = Vec::new();
    for holder in holders {
        // A 16-byte identifier, then the boxes; the preview's holder has 8
        // more bytes before them.
        for skipped in [16, 24] {
            let inside = holder.start.saturating_add(skipped)..holder.end;
            for (kind, contents) in boxes(input, inside) {
                if matches!(&kind, b"PRVW" | b"THMB") {
                    jpegs.extend(jpeg_in(input, contents));
                }
            }
        }
    }
    jpegs
}

/// The JPEG in a preview box: from its start-of-image mark to the box's end.
fn jpeg_in<R: Read + Seek>(input: &mut R, contents: Range<u64>) -> Option<Range<u64>> {
    let searched = (contents.end - contents.start).min(JPEG_SEARCH_BYTES) as usize;
    let mut head = vec![0u8; searched];
    input.seek(SeekFrom::Start(contents.start)).ok()?;
    input.read_exact(&mut head).ok()?;
    let mark = head
        .windows(3)
        .position(|bytes| bytes == [0xFF, 0xD8, 0xFF])?;
    Some(contents.start + mark as u64..contents.end)
}
