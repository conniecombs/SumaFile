//! Walks the directories of a TIFF file for embedded JPEGs. Most camera RAW
//! formats are TIFF files with their own tags.

use std::collections::{HashSet, VecDeque};
use std::io::{Read, Seek, SeekFrom};
use std::ops::Range;

/// A file may chain and nest directories without end; this many are read.
const MAX_DIRECTORIES: usize = 64;
const MAX_ENTRIES: usize = 4096;
const MAX_SUB_DIRECTORIES: usize = 16;

const COMPRESSION: u16 = 0x0103;
const STRIP_OFFSETS: u16 = 0x0111;
const ORIENTATION: u16 = 0x0112;
const STRIP_BYTE_COUNTS: u16 = 0x0117;
const SUB_DIRECTORIES: u16 = 0x014A;
const JPEG_OFFSET: u16 = 0x0201;
const JPEG_LENGTH: u16 = 0x0202;
/// Panasonic: a whole JPEG as the tag's value.
const JPG_FROM_RAW: u16 = 0x002E;

pub(super) struct Found {
    /// Where JPEGs may be, as byte ranges of the file. Unchecked: a range
    /// may lie outside the file or hold something else.
    pub(super) jpegs: Vec<Range<u64>>,
    /// The first directory's orientation, as Exif numbers it.
    pub(super) orientation: Option<u16>,
}

struct Tiff<'a, R> {
    input: &'a mut R,
    length: u64,
    big_endian: bool,
}

impl<R: Read + Seek> Tiff<'_, R> {
    fn at(&mut self, offset: u64, length: usize) -> Option<Vec<u8>> {
        if offset.checked_add(length as u64)? > self.length {
            return None;
        }
        self.input.seek(SeekFrom::Start(offset)).ok()?;
        let mut block = vec![0u8; length];
        self.input.read_exact(&mut block).ok()?;
        Some(block)
    }

    fn short(&self, bytes: &[u8]) -> u16 {
        let bytes = [bytes[0], bytes[1]];
        if self.big_endian {
            u16::from_be_bytes(bytes)
        } else {
            u16::from_le_bytes(bytes)
        }
    }

    fn long(&self, bytes: &[u8]) -> u32 {
        let bytes = [bytes[0], bytes[1], bytes[2], bytes[3]];
        if self.big_endian {
            u32::from_be_bytes(bytes)
        } else {
            u32::from_le_bytes(bytes)
        }
    }
}

/// The bytes one value of this TIFF type takes; 0 for a type not known.
fn type_size(kind: u16) -> u32 {
    match kind {
        1 | 2 | 6 | 7 => 1,
        3 | 8 => 2,
        4 | 9 | 11 | 13 => 4,
        5 | 10 | 12 => 8,
        _ => 0,
    }
}

/// `None` when the file is not a TIFF of a kind camera makers use.
pub(super) fn walk<R: Read + Seek>(input: &mut R, length: u64) -> Option<Found> {
    let mut tiff = Tiff {
        input,
        length,
        big_endian: false,
    };
    let header = tiff.at(0, 8)?;
    tiff.big_endian = match &header[..2] {
        b"II" => false,
        b"MM" => true,
        _ => return None,
    };
    // 42 is TIFF. Panasonic writes 0x55; Olympus writes "RO" or "RS".
    if !matches!(tiff.short(&header[2..]), 42 | 0x55 | 0x4F52 | 0x5352) {
        return None;
    }

    let mut found = Found {
        jpegs: Vec::new(),
        orientation: None,
    };
    let first = u64::from(tiff.long(&header[4..]));
    let mut waiting = VecDeque::from([first]);
    let mut seen = HashSet::new();
    while let Some(offset) = waiting.pop_front() {
        if offset == 0 || seen.len() >= MAX_DIRECTORIES || !seen.insert(offset) {
            continue;
        }
        let Some(count) = tiff
            .at(offset, 2)
            .map(|count| usize::from(tiff.short(&count)))
        else {
            continue;
        };
        if count > MAX_ENTRIES {
            continue;
        }
        let Some(entries) = tiff.at(offset + 2, count * 12 + 4) else {
            continue;
        };

        let (mut compression, mut strip, mut strip_length) = (None, None, None);
        let (mut jpeg, mut jpeg_length) = (None, None);
        for entry in entries[..count * 12].as_chunks::<12>().0 {
            let (tag, kind) = (tiff.short(&entry[0..]), tiff.short(&entry[2..]));
            let values = tiff.long(&entry[4..]);
            let value = match kind {
                3 => u32::from(tiff.short(&entry[8..])),
                _ => tiff.long(&entry[8..]),
            };
            match tag {
                COMPRESSION => compression = Some(value),
                STRIP_OFFSETS if values == 1 => strip = Some(value),
                STRIP_BYTE_COUNTS if values == 1 => strip_length = Some(value),
                JPEG_OFFSET => jpeg = Some(value),
                JPEG_LENGTH => jpeg_length = Some(value),
                ORIENTATION if offset == first => found.orientation = u16::try_from(value).ok(),
                JPG_FROM_RAW => {
                    let bytes = u64::from(values) * u64::from(type_size(kind));
                    if bytes > 4 {
                        let start = u64::from(tiff.long(&entry[8..]));
                        found.jpegs.push(start..start + bytes);
                    }
                }
                SUB_DIRECTORIES if values == 1 => waiting.push_back(u64::from(value)),
                SUB_DIRECTORIES => {
                    let listed = (values as usize).min(MAX_SUB_DIRECTORIES);
                    if let Some(list) = tiff.at(u64::from(value), listed * 4) {
                        for sub in list.as_chunks::<4>().0 {
                            waiting.push_back(u64::from(tiff.long(sub)));
                        }
                    }
                }
                _ => {}
            }
        }
        waiting.push_back(u64::from(tiff.long(&entries[count * 12..])));

        let mut candidate = |start: Option<u32>, bytes: Option<u32>| {
            if let (Some(start), Some(bytes)) = (start, bytes) {
                let start = u64::from(start);
                found.jpegs.push(start..start + u64::from(bytes));
            }
        };
        candidate(jpeg, jpeg_length);
        // 6 and 7 are the two numbers TIFF has for JPEG compression.
        if matches!(compression, Some(6 | 7)) {
            candidate(strip, strip_length);
        }
    }
    Some(found)
}
