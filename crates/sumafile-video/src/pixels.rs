//! From the rows of pixels Windows hands back to an upright picture.

use crate::rules::Turn;
use image::{imageops, Rgb, RgbImage};

const BYTES_PER_PIXEL: usize = 4;

/// The upright picture in `rows`, or `None` when `rows` is too short for a
/// picture of `width` by `height`.
///
/// `rows` are 32-bit pixels in the order blue, green, red, unused. `stride`
/// is the number of bytes from one row to the next: positive when the first
/// row in `rows` is the top one, negative when it is the bottom one. It may
/// be wider than a row; the last row need not be padded out to it.
pub fn upright_rgb(
    rows: &[u8],
    stride: i32,
    width: u32,
    height: u32,
    turn: Turn,
) -> Option<RgbImage> {
    if width == 0 || height == 0 {
        return None;
    }
    let row_bytes = usize::try_from(width).ok()?.checked_mul(BYTES_PER_PIXEL)?;
    let step = usize::try_from(stride.unsigned_abs()).ok()?;
    let row_count = usize::try_from(height).ok()?;
    let needed = step.checked_mul(row_count - 1)?.checked_add(row_bytes)?;
    if step < row_bytes || rows.len() < needed {
        return None;
    }

    let mut image = RgbImage::new(width, height);
    for (y, target) in image.rows_mut().enumerate() {
        let source = if stride < 0 { row_count - 1 - y } else { y };
        let row = &rows[source * step..source * step + row_bytes];
        for (pixel, &[blue, green, red, _]) in target.zip(row.as_chunks::<BYTES_PER_PIXEL>().0) {
            *pixel = Rgb([red, green, blue]);
        }
    }
    Some(match turn {
        Turn::None => image,
        Turn::Quarter => imageops::rotate90(&image),
        Turn::Half => imageops::rotate180(&image),
        Turn::ThreeQuarters => imageops::rotate270(&image),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use image::Rgb;

    const RED: [u8; 4] = [0, 0, 255, 0];
    const GREEN: [u8; 4] = [0, 255, 0, 0];
    const BLUE: [u8; 4] = [255, 0, 0, 0];
    const WHITE: [u8; 4] = [255, 255, 255, 0];
    const GREY: [u8; 4] = [128, 128, 128, 0];
    const BLACK: [u8; 4] = [0, 0, 0, 0];

    /// A picture two pixels wide and three high, top row first:
    /// red green / blue white / grey black.
    fn two_by_three() -> Vec<u8> {
        [RED, GREEN, BLUE, WHITE, GREY, BLACK].concat()
    }

    #[test]
    fn blue_green_red_becomes_red_green_blue() {
        let rows = [10, 20, 30, 99].to_vec();

        let image = upright_rgb(&rows, 4, 1, 1, Turn::None).unwrap();

        assert_eq!(image.get_pixel(0, 0), &Rgb([30, 20, 10]));
    }

    #[test]
    fn top_down_rows_are_read_in_order() {
        let image = upright_rgb(&two_by_three(), 8, 2, 3, Turn::None).unwrap();

        assert_eq!(image.dimensions(), (2, 3));
        assert_eq!(image.get_pixel(0, 0), &Rgb([255, 0, 0]));
        assert_eq!(image.get_pixel(1, 0), &Rgb([0, 255, 0]));
        assert_eq!(image.get_pixel(0, 1), &Rgb([0, 0, 255]));
        assert_eq!(image.get_pixel(1, 2), &Rgb([0, 0, 0]));
    }

    /// A negative stride: the first row in the buffer is the bottom one.
    #[test]
    fn bottom_up_rows_are_flipped() {
        let bottom_up = [GREY, BLACK, BLUE, WHITE, RED, GREEN].concat();

        let image = upright_rgb(&bottom_up, -8, 2, 3, Turn::None).unwrap();

        assert_eq!(image.get_pixel(0, 0), &Rgb([255, 0, 0]));
        assert_eq!(image.get_pixel(1, 0), &Rgb([0, 255, 0]));
        assert_eq!(image.get_pixel(0, 2), &Rgb([128, 128, 128]));
    }

    #[test]
    fn a_stride_wider_than_the_row_skips_the_padding() {
        const PAD: [u8; 4] = [7, 7, 7, 7];
        let padded = [RED, GREEN, PAD, BLUE, WHITE, PAD, GREY, BLACK].concat();

        let image = upright_rgb(&padded, 12, 2, 3, Turn::None).unwrap();

        assert_eq!(image.get_pixel(1, 0), &Rgb([0, 255, 0]));
        assert_eq!(image.get_pixel(0, 1), &Rgb([0, 0, 255]));
        assert_eq!(image.get_pixel(1, 2), &Rgb([0, 0, 0]));
    }

    /// Red is the top-left pixel of the frame as decoded. A phone video shot
    /// upright is stored lying on its side and says by how much to turn it.
    #[test]
    fn each_turn_puts_the_top_left_pixel_where_it_belongs() {
        let red = Rgb([255, 0, 0]);
        let turned = |turn| upright_rgb(&two_by_three(), 8, 2, 3, turn).unwrap();

        let quarter = turned(Turn::Quarter);
        assert_eq!(quarter.dimensions(), (3, 2));
        assert_eq!(
            quarter.get_pixel(2, 0),
            &red,
            "clockwise: top-left goes to top-right"
        );

        let half = turned(Turn::Half);
        assert_eq!(half.dimensions(), (2, 3));
        assert_eq!(half.get_pixel(1, 2), &red);

        let three_quarters = turned(Turn::ThreeQuarters);
        assert_eq!(three_quarters.dimensions(), (3, 2));
        assert_eq!(three_quarters.get_pixel(0, 1), &red);
    }

    #[test]
    fn a_buffer_too_short_gives_none() {
        let rows = two_by_three();

        assert!(upright_rgb(&rows[..rows.len() - 1], 8, 2, 3, Turn::None).is_none());
        assert!(
            upright_rgb(&rows, 4, 2, 3, Turn::None).is_none(),
            "a stride shorter than a row"
        );
        assert!(upright_rgb(&rows, 8, 0, 3, Turn::None).is_none());
        assert!(upright_rgb(&rows, 8, 2, 0, Turn::None).is_none());
        assert!(upright_rgb(&[], 8, 2, 3, Turn::None).is_none());
    }

    /// The last row need not be padded out to the stride.
    #[test]
    fn the_last_row_may_end_at_its_last_pixel() {
        const PAD: [u8; 4] = [7, 7, 7, 7];
        let unpadded_end = [RED, GREEN, PAD, BLUE, WHITE].concat();

        let image = upright_rgb(&unpadded_end, 12, 2, 2, Turn::None).unwrap();

        assert_eq!(image.get_pixel(1, 1), &Rgb([255, 255, 255]));
    }
}
