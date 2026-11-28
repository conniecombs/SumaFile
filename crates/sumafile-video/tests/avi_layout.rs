//! The uncompressed AVI the Windows tests and the UI smoke feed to the video
//! process is written by this crate's own test support. These tests run on
//! every system, so a mistake in the file's layout shows before a Windows
//! run does.

#[path = "support/avi.rs"]
mod avi;

use image::{Rgb, RgbImage};

fn u32_at(bytes: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap())
}

fn find(bytes: &[u8], word: &[u8]) -> usize {
    bytes
        .windows(word.len())
        .position(|window| window == word)
        .unwrap_or_else(|| panic!("{} is missing", String::from_utf8_lossy(word)))
}

#[test]
fn the_file_is_a_riff_avi_whose_sizes_add_up() {
    let (width, height, frames) = (96usize, 64usize, 5usize);
    let file = avi::bytes(width as u32, height as u32, 10, frames);
    let frame_bytes = width * 3 * height;

    assert_eq!(&file[0..4], b"RIFF");
    assert_eq!(u32_at(&file, 4) as usize, file.len() - 8);
    assert_eq!(&file[8..12], b"AVI ");

    let main_header = find(&file, b"avih");
    assert_eq!(u32_at(&file, main_header + 4), 56);
    assert_eq!(
        u32_at(&file, main_header + 8),
        100_000,
        "microseconds a frame"
    );
    assert_eq!(u32_at(&file, main_header + 8 + 16) as usize, frames);
    assert_eq!(u32_at(&file, main_header + 8 + 32) as usize, width);
    assert_eq!(u32_at(&file, main_header + 8 + 36) as usize, height);

    let stream_header = find(&file, b"strh");
    assert_eq!(&file[stream_header + 8..stream_header + 12], b"vids");
    assert_eq!(u32_at(&file, stream_header + 8 + 20), 1, "scale");
    assert_eq!(u32_at(&file, stream_header + 8 + 24), 10, "rate");
    assert_eq!(u32_at(&file, stream_header + 8 + 32) as usize, frames);

    let format = find(&file, b"strf");
    assert_eq!(u32_at(&file, format + 4), 40);
    assert_eq!(u32_at(&file, format + 8 + 4) as usize, width);
    assert_eq!(u32_at(&file, format + 8 + 8) as usize, height);
    assert_eq!(
        &file[format + 8 + 14..format + 8 + 16],
        [24, 0],
        "bits a pixel"
    );
    assert_eq!(u32_at(&file, format + 8 + 16), 0, "uncompressed");

    let movie = find(&file, b"movi");
    assert_eq!(&file[movie - 8..movie - 4], b"LIST");
    assert_eq!(
        u32_at(&file, movie - 4) as usize,
        4 + frames * (8 + frame_bytes)
    );
    for frame in 0..frames {
        let chunk = movie + 4 + frame * (8 + frame_bytes);
        assert_eq!(&file[chunk..chunk + 4], b"00db");
        assert_eq!(u32_at(&file, chunk + 4) as usize, frame_bytes);
    }

    let index = movie + 4 + frames * (8 + frame_bytes);
    assert_eq!(&file[index..index + 4], b"idx1");
    assert_eq!(u32_at(&file, index + 4) as usize, frames * 16);
    for frame in 0..frames {
        let entry = index + 8 + frame * 16;
        assert_eq!(&file[entry..entry + 4], b"00db");
        // Counted from the word "movi".
        assert_eq!(
            u32_at(&file, entry + 8) as usize,
            4 + frame * (8 + frame_bytes)
        );
        assert_eq!(u32_at(&file, entry + 12) as usize, frame_bytes);
    }
    assert_eq!(file.len(), index + 8 + frames * 16);
}

/// A row of a 24-bit bitmap is padded to four bytes.
#[test]
fn rows_of_an_odd_width_are_padded() {
    let file = avi::bytes(30, 4, 10, 2);
    let movie = find(&file, b"movi");

    assert_eq!(
        u32_at(&file, movie + 4 + 4),
        92 * 4,
        "30 pixels take 90 bytes, padded to 92"
    );
}

/// Every frame is six upright bars, black or white, that spell its number.
/// Black and white survive what happens to a frame on its way to a JPEG.
#[test]
fn a_frame_names_itself() {
    for index in 0..64 {
        let exact = avi::frame_picture(96, 64, index);
        assert_eq!(avi::frame_index_in(&exact), index);
        assert!(avi::frame_is_upright(&exact), "frame {index}");

        // Lifted blacks and dulled whites, as a colour conversion leaves them.
        let washed_out = RgbImage::from_fn(96, 64, |x, y| {
            let Rgb([value, _, _]) = *exact.get_pixel(x, y);
            let value = if value > 128 { 190 } else { 45 };
            Rgb([value, value.saturating_add(12), value.saturating_sub(12)])
        });
        assert_eq!(avi::frame_index_in(&washed_out), index);
        assert!(avi::frame_is_upright(&washed_out), "frame {index}");
    }
}

/// Bars alone look the same upside down. The top quarter of every frame is
/// a strip of twelve narrow stripes, which no row of bars can be taken for.
#[test]
fn a_frame_turned_or_flipped_is_not_upright() {
    for index in [0, 1, 15, 21, 42, 58, 63] {
        let upright = avi::frame_picture(96, 64, index);

        let upside_down = image::imageops::flip_vertical(&upright);
        assert!(
            !avi::frame_is_upright(&upside_down),
            "frame {index} flipped"
        );
        let half_turn = image::imageops::rotate180(&upright);
        assert!(!avi::frame_is_upright(&half_turn), "frame {index} turned");
        let mirrored = image::imageops::flip_horizontal(&upright);
        assert!(!avi::frame_is_upright(&mirrored), "frame {index} mirrored");
    }
}

/// Black and white cannot show red and blue changing places. The light
/// stripes of the strip are yellow: swapped, they would be cyan.
#[test]
fn a_frame_with_red_and_blue_swapped_is_caught() {
    for index in [0, 21, 63] {
        let right = avi::frame_picture(96, 64, index);
        assert!(avi::frame_colours_are_right(&right), "frame {index}");
        assert_eq!(right.get_pixel(12, 4), &Rgb([255, 255, 0]));

        let swapped = RgbImage::from_fn(96, 64, |x, y| {
            let Rgb([red, green, blue]) = *right.get_pixel(x, y);
            Rgb([blue, green, red])
        });
        assert!(!avi::frame_colours_are_right(&swapped), "frame {index}");
        assert!(
            avi::frame_is_upright(&swapped),
            "the stripes are still there"
        );
    }
}

/// In the file a pixel is blue, green, red.
#[test]
fn the_strip_is_yellow_in_the_file() {
    let (width, height) = (96usize, 64usize);
    let file = avi::bytes(width as u32, height as u32, 10, 1);
    let movie = find(&file, b"movi");
    let top_row = movie + 4 + 8 + (height - 1) * width * 3;

    assert_eq!(&file[top_row + 12 * 3..top_row + 12 * 3 + 3], [0, 255, 255]);
    assert_eq!(&file[top_row + 4 * 3..top_row + 4 * 3 + 3], [0, 0, 0]);
}

/// A bitmap stores its bottom row first.
#[test]
fn the_bottom_row_comes_first_in_the_file() {
    let (width, height) = (96usize, 64usize);
    let file = avi::bytes(width as u32, height as u32, 10, 4);
    let movie = find(&file, b"movi");
    let frame = movie + 4 + 8;
    // The green byte of a pixel: bright in white and in yellow.
    let pixel = |row: usize, x: usize| file[frame + row * width * 3 + x * 3 + 1];

    // Frame 0: black bars below, the strip of stripes on top.
    assert_eq!(pixel(0, 4), 0, "the first row in the file is bars");
    assert_eq!(pixel(0, 12), 0);
    assert_eq!(
        pixel(height - 1, 4),
        0,
        "the last row in the file is the strip"
    );
    assert_eq!(pixel(height - 1, 12), 255);
}

#[test]
fn the_first_pixels_of_a_frame_are_its_first_bar() {
    let (width, height) = (96usize, 64usize);
    let file = avi::bytes(width as u32, height as u32, 10, 4);
    let movie = find(&file, b"movi");
    let frame = |index: usize| movie + 4 + index * (8 + width * 3 * height) + 8;

    // Frame 0 is black. Frame 1 has its first bar white, frame 2 its second.
    assert_eq!(&file[frame(0)..frame(0) + 3], [0, 0, 0]);
    assert_eq!(&file[frame(1)..frame(1) + 3], [255, 255, 255]);
    assert_eq!(&file[frame(2)..frame(2) + 3], [0, 0, 0]);
    assert_eq!(
        &file[frame(2) + 16 * 3..frame(2) + 16 * 3 + 3],
        [255, 255, 255]
    );
}
