// Ported from Oculante (https://github.com/woelper/oculante), Copyright (c) Johann Woelper, MIT licence. See LICENSE-oculante.txt.

//! Bringing pictures that hold light (float samples) to 8 bits for the
//! screen, with a gamma curve.

use image::{Rgb, Rgb32FImage, RgbImage, Rgba, Rgba32FImage, RgbaImage};

pub fn tonemap_f32(px: f32) -> u8 {
    (px.powf(1.0 / 2.2).clamp(0.0, 1.0) * 255.0) as u8
}

pub fn tonemap_rgb32f(image: &Rgb32FImage) -> RgbImage {
    RgbImage::from_fn(image.width(), image.height(), |x, y| {
        let [red, green, blue] = image.get_pixel(x, y).0;
        Rgb([tonemap_f32(red), tonemap_f32(green), tonemap_f32(blue)])
    })
}

/// The colour channels are tone-mapped; alpha is coverage, not light, and is
/// only brought to 8 bits.
pub fn tonemap_rgba32f(image: &Rgba32FImage) -> RgbaImage {
    RgbaImage::from_fn(image.width(), image.height(), |x, y| {
        let [red, green, blue, alpha] = image.get_pixel(x, y).0;
        Rgba([
            tonemap_f32(red),
            tonemap_f32(green),
            tonemap_f32(blue),
            (alpha.clamp(0.0, 1.0) * 255.0).round() as u8,
        ])
    })
}
