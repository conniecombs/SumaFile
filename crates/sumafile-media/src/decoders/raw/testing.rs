//! Builds the containers camera RAW files use, around real JPEGs, for tests.

use image::{Rgb, RgbImage};

/// A JPEG of one colour.
pub(crate) fn jpeg(width: u32, height: u32, colour: [u8; 3]) -> Vec<u8> {
    let mut bytes = Vec::new();
    RgbImage::from_pixel(width, height, Rgb(colour))
        .write_to(
            &mut std::io::Cursor::new(&mut bytes),
            image::ImageFormat::Jpeg,
        )
        .unwrap();
    bytes
}

pub(crate) enum Field {
    Short(u16, u16),
    Long(u16, u32),
    /// JPEGInterchangeFormat and its length.
    Jpeg(Vec<u8>),
    /// One strip: StripOffsets and StripByteCounts.
    Strip(Vec<u8>),
    /// A tag whose value is these bytes.
    Blob(u16, Vec<u8>),
    /// SubIFDs: the directories at these indexes.
    SubDirs(Vec<usize>),
}

pub(crate) struct Dir {
    fields: Vec<Field>,
    next: Option<usize>,
}

impl Dir {
    pub(crate) fn new(fields: Vec<Field>) -> Self {
        Self { fields, next: None }
    }

    /// The directory at `index` follows this one in the chain.
    pub(crate) fn then(mut self, index: usize) -> Self {
        self.next = Some(index);
        self
    }

    fn entries(&self) -> usize {
        self.fields
            .iter()
            .map(|field| match field {
                Field::Jpeg(_) | Field::Strip(_) => 2,
                _ => 1,
            })
            .sum()
    }

    fn size(&self) -> usize {
        2 + 12 * self.entries() + 4
    }
}

pub(crate) struct Tiff {
    big_endian: bool,
    magic: u16,
}

impl Tiff {
    pub(crate) fn little_endian() -> Self {
        Self {
            big_endian: false,
            magic: 42,
        }
    }

    pub(crate) fn big_endian() -> Self {
        Self {
            big_endian: true,
            magic: 42,
        }
    }

    pub(crate) fn with_magic(mut self, magic: u16) -> Self {
        self.magic = magic;
        self
    }

    fn short(&self, value: u16) -> [u8; 2] {
        if self.big_endian {
            value.to_be_bytes()
        } else {
            value.to_le_bytes()
        }
    }

    fn long(&self, value: u32) -> [u8; 4] {
        if self.big_endian {
            value.to_be_bytes()
        } else {
            value.to_le_bytes()
        }
    }

    /// The file: a header, the directories one after another, then the data
    /// their fields point at. The first directory is the file's first.
    pub(crate) fn build(&self, dirs: &[Dir]) -> Vec<u8> {
        let mut offsets = Vec::new();
        let mut offset = 8;
        for dir in dirs {
            offsets.push(offset as u32);
            offset += dir.size();
        }
        let mut data_at = offset as u32;
        let mut data = Vec::new();
        let mut place = |bytes: &[u8]| {
            let at = data_at;
            data.extend_from_slice(bytes);
            data_at += bytes.len() as u32;
            at
        };

        let mut file = Vec::new();
        file.extend_from_slice(if self.big_endian { b"MM" } else { b"II" });
        file.extend_from_slice(&self.short(self.magic));
        file.extend_from_slice(&self.long(offsets[0]));
        for dir in dirs {
            file.extend_from_slice(&self.short(dir.entries() as u16));
            let mut entry = |tag: u16, kind: u16, count: u32, value: [u8; 4]| {
                file.extend_from_slice(&self.short(tag));
                file.extend_from_slice(&self.short(kind));
                file.extend_from_slice(&self.long(count));
                file.extend_from_slice(&value);
            };
            for field in &dir.fields {
                match field {
                    Field::Short(tag, value) => {
                        let value = self.short(*value);
                        entry(*tag, 3, 1, [value[0], value[1], 0, 0]);
                    }
                    Field::Long(tag, value) => entry(*tag, 4, 1, self.long(*value)),
                    Field::Jpeg(bytes) => {
                        entry(0x0201, 4, 1, self.long(place(bytes)));
                        entry(0x0202, 4, 1, self.long(bytes.len() as u32));
                    }
                    Field::Strip(bytes) => {
                        entry(0x0111, 4, 1, self.long(place(bytes)));
                        entry(0x0117, 4, 1, self.long(bytes.len() as u32));
                    }
                    Field::Blob(tag, bytes) => {
                        entry(*tag, 7, bytes.len() as u32, self.long(place(bytes)));
                    }
                    Field::SubDirs(indexes) => {
                        if let [index] = indexes.as_slice() {
                            entry(0x014A, 4, 1, self.long(offsets[*index]));
                        } else {
                            let list: Vec<u8> = indexes
                                .iter()
                                .flat_map(|index| self.long(offsets[*index]))
                                .collect();
                            entry(0x014A, 4, indexes.len() as u32, self.long(place(&list)));
                        }
                    }
                }
            }
            let next = dir.next.map_or(0, |index| offsets[index]);
            file.extend_from_slice(&self.long(next));
        }
        file.extend_from_slice(&data);
        file
    }
}
