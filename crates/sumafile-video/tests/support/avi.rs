//! Test support: a small uncompressed AVI whose frames name themselves.
//!
//! An uncompressed video needs no codec, so what a test sees does not depend
//! on what Windows has installed. Every frame is six upright bars, black or
//! white: bar `k` is white when bit `k` of the frame's number is set. Black
//! and white survive colour conversion, scaling and JPEG, so a thumbnail
//! says which frame it shows.
//!
//! The top quarter of every frame is a strip of twelve narrow stripes,
//! black then white. Bars look the same upside down; the strip says which
//! way up the picture is.

#![allow(dead_code)]

use image::{Rgb, RgbImage};
use std::path::Path;

/// The bars a frame is drawn as, so frames 0 to 63 can be told apart.
pub const BARS: u32 = 6;

/// Whether the pixel at (`x`, `y`) of frame `index` is white.
fn is_white(index: usize, x: u32, y: u32, width: u32, height: u32) -> bool {
    if y < height / 4 {
        // The strip: each bar's width holds a black stripe, then a white one.
        return x * 2 * BARS / width % 2 == 1;
    }
    let bar = x * BARS / width;
    index >> bar & 1 == 1
}

/// Frame `index` as a picture.
pub fn frame_picture(width: u32, height: u32, index: usize) -> RgbImage {
    RgbImage::from_fn(width, height, |x, y| {
        let value = if is_white(index, x, y, width, height) {
            255
        } else {
            0
        };
        Rgb([value, value, value])
    })
}

fn is_bright(picture: &RgbImage, x: u32, y: u32) -> bool {
    let (width, height) = picture.dimensions();
    let Rgb([red, green, blue]) = *picture.get_pixel(x.min(width - 1), y.min(height - 1));
    u32::from(red) + u32::from(green) + u32::from(blue) > 3 * 128
}

/// The number of the frame a picture shows, read at the middle of each bar.
pub fn frame_index_in(picture: &RgbImage) -> usize {
    let (width, height) = picture.dimensions();
    (0..BARS)
        .filter(|bar| is_bright(picture, (2 * bar + 1) * width / (2 * BARS), height * 5 / 8))
        .map(|bar| 1usize << bar)
        .sum()
}

/// Whether the strip of stripes is where it belongs: across the top, black
/// stripe first. A picture flipped, mirrored or turned fails this.
pub fn frame_is_upright(picture: &RgbImage) -> bool {
    let (width, height) = picture.dimensions();
    let stripe = |number: u32| (2 * number + 1) * width / (4 * BARS);
    (0..BARS).all(|bar| {
        !is_bright(picture, stripe(2 * bar), height / 8)
            && is_bright(picture, stripe(2 * bar + 1), height / 8)
    })
}

fn chunk(name: &[u8; 4], data: &[u8]) -> Vec<u8> {
    let mut chunk = Vec::with_capacity(8 + data.len() + 1);
    chunk.extend_from_slice(name);
    chunk.extend_from_slice(&(data.len() as u32).to_le_bytes());
    chunk.extend_from_slice(data);
    if data.len() % 2 == 1 {
        chunk.push(0);
    }
    chunk
}

fn list(kind: &[u8; 4], children: &[u8]) -> Vec<u8> {
    let mut list = Vec::with_capacity(12 + children.len());
    list.extend_from_slice(b"LIST");
    list.extend_from_slice(&(4 + children.len() as u32).to_le_bytes());
    list.extend_from_slice(kind);
    list.extend_from_slice(children);
    list
}

fn words(values: &[u32]) -> Vec<u8> {
    values
        .iter()
        .flat_map(|value| value.to_le_bytes())
        .collect()
}

/// The bytes of an AVI file of `frame_count` frames, uncompressed at 24 bits
/// a pixel, with an index.
pub fn bytes(width: u32, height: u32, frames_per_second: u32, frame_count: usize) -> Vec<u8> {
    // A bitmap's rows are padded to four bytes, and the bottom row comes
    // first.
    let row_bytes = (width as usize * 3).div_ceil(4) * 4;
    let frame_bytes = row_bytes * height as usize;
    let frames = frame_count as u32;

    const HAS_INDEX: u32 = 0x10;
    let main_header = words(&[
        1_000_000 / frames_per_second,
        frame_bytes as u32 * frames_per_second,
        0,
        HAS_INDEX,
        frames,
        0,
        1,
        frame_bytes as u32,
        width,
        height,
        0,
        0,
        0,
        0,
    ]);

    let mut stream_header = Vec::new();
    stream_header.extend_from_slice(b"vids");
    stream_header.extend_from_slice(b"DIB ");
    stream_header.extend(words(&[
        0, // flags
        0, // priority and language
        0, // initial frames
        1, // scale: rate / scale frames a second
        frames_per_second,
        0, // start
        frames,
        frame_bytes as u32,
        u32::MAX, // quality: the default
        0,        // sample size: frames vary
    ]));
    for edge in [0, 0, width as u16, height as u16] {
        stream_header.extend_from_slice(&edge.to_le_bytes());
    }

    let mut format = words(&[40, width, height]);
    format.extend_from_slice(&1u16.to_le_bytes()); // planes
    format.extend_from_slice(&24u16.to_le_bytes()); // bits a pixel
    format.extend(words(&[0, frame_bytes as u32, 0, 0, 0, 0]));

    let stream = [chunk(b"strh", &stream_header), chunk(b"strf", &format)].concat();
    let headers = [chunk(b"avih", &main_header), list(b"strl", &stream)].concat();

    const KEY_FRAME: u32 = 0x10;
    let mut movie = Vec::with_capacity(frame_count * (8 + frame_bytes));
    let mut index = Vec::with_capacity(frame_count * 16);
    for frame in 0..frame_count {
        let mut rows = vec![0u8; frame_bytes];
        for (row, y) in rows.chunks_exact_mut(row_bytes).zip((0..height).rev()) {
            for x in 0..width {
                if is_white(frame, x, y, width, height) {
                    row[x as usize * 3..x as usize * 3 + 3].fill(255);
                }
            }
        }
        index.extend_from_slice(b"00db");
        // Counted from the word "movi", which is four bytes before the
        // first frame's chunk.
        index.extend(words(&[
            KEY_FRAME,
            4 + movie.len() as u32,
            frame_bytes as u32,
        ]));
        movie.extend(chunk(b"00db", &rows));
    }

    let body = [
        list(b"hdrl", &headers),
        list(b"movi", &movie),
        chunk(b"idx1", &index),
    ]
    .concat();
    let mut file = Vec::with_capacity(12 + body.len());
    file.extend_from_slice(b"RIFF");
    file.extend_from_slice(&(4 + body.len() as u32).to_le_bytes());
    file.extend_from_slice(b"AVI ");
    file.extend(body);
    file
}

/// Write the file `bytes` describes.
pub fn write(
    path: &Path,
    width: u32,
    height: u32,
    frames_per_second: u32,
    frame_count: usize,
) -> std::io::Result<()> {
    std::fs::write(path, bytes(width, height, frames_per_second, frame_count))
}
