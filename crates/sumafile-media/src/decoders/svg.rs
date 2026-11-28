// Ported from Oculante (https://github.com/woelper/oculante), Copyright (c) Johann Woelper, MIT licence. See LICENSE-oculante.txt.

//! SVG, drawn by `resvg` at the size that was asked for.

use super::{whole, Decoded};
use crate::limits::check_budget;
use crate::render::{Purpose, RenderError};
use image::{DynamicImage, RgbaImage};
use resvg::{tiny_skia, usvg};
use std::path::Path;
use std::sync::{Arc, OnceLock};

/// A preview is drawn at up to this many times the SVG's own size.
const PREVIEW_SCALE: f32 = 4.0;

/// The system's fonts, found once: the search takes a few hundred
/// milliseconds, and most SVGs with text need it.
fn fonts() -> Arc<usvg::fontdb::Database> {
    static FONTS: OnceLock<Arc<usvg::fontdb::Database>> = OnceLock::new();
    Arc::clone(FONTS.get_or_init(|| {
        let mut fonts = usvg::fontdb::Database::new();
        fonts.load_system_fonts();
        Arc::new(fonts)
    }))
}

pub(super) fn decode(path: &Path, purpose: Purpose) -> Result<Decoded, RenderError> {
    let data = std::fs::read(path)
        .map_err(|error| RenderError::Io(format!("Cannot open {}: {error}", path.display())))?;
    let options = usvg::Options {
        fontdb: fonts(),
        // Pictures inside the SVG (data URLs) are drawn. A name of another
        // file is not followed: usvg would open any path the SVG gives.
        image_href_resolver: usvg::ImageHrefResolver {
            resolve_data: usvg::ImageHrefResolver::default_data_resolver(),
            resolve_string: Box::new(|_, _| None),
        },
        ..usvg::Options::default()
    };
    let tree = usvg::Tree::from_data(&data, &options)
        .map_err(|error| RenderError::Decode(format!("The SVG could not be read: {error}")))?;

    let (own_width, own_height) = (tree.size().width(), tree.size().height());
    let own_long_side = own_width.max(own_height);
    let long_side = match purpose {
        Purpose::Thumbnail { size } => size as f32,
        Purpose::Preview { max_dimension } => {
            (own_long_side * PREVIEW_SCALE).min(max_dimension as f32)
        }
    };
    let scale = long_side / own_long_side;
    let side = |length: f32| (length * scale).round().clamp(1.0, 65_535.0) as u32;
    let (width, height) = (side(own_width), side(own_height));
    check_budget(width, height, 4, 1)?;

    let mut pixmap = tiny_skia::Pixmap::new(width, height)
        .ok_or_else(|| RenderError::Decode("The SVG has no size to draw at.".to_string()))?;
    resvg::render(
        &tree,
        tiny_skia::Transform::from_scale(scale, scale),
        &mut pixmap.as_mut(),
    );

    let mut pixels = pixmap.take();
    // The drawing has colour multiplied by alpha; pictures here do not.
    for pixel in pixels.as_chunks_mut::<4>().0 {
        let alpha = u32::from(pixel[3]);
        if alpha != 0 && alpha != 255 {
            for channel in &mut pixel[..3] {
                *channel = ((u32::from(*channel) * 255 + alpha / 2) / alpha).min(255) as u8;
            }
        }
    }
    let image = RgbaImage::from_raw(width, height, pixels)
        .ok_or_else(|| RenderError::Decode("The SVG could not be drawn.".to_string()))?;
    Ok(whole(DynamicImage::ImageRgba8(image)))
}

#[cfg(test)]
mod tests {
    use crate::decoders::decode;
    use crate::render::{Purpose, RenderError};
    use crate::testing::Folder;

    const HALVES: &str = r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
        <rect x="0" y="0" width="50" height="50" fill="#ff0000"/>
        <rect x="50" y="0" width="50" height="50" fill="#0000ff"/>
    </svg>"##;

    #[test]
    fn draws_an_svg_at_the_size_of_the_thumbnail() {
        let folder = Folder::new("svg");
        let path = folder.write("halves.svg", HALVES.as_bytes());

        let decoded = decode(&path, "svg", Purpose::Thumbnail { size: 200 }).unwrap();

        assert_eq!(decoded.full_size, (200, 100));
        let pixels = decoded.image.to_rgba8();
        assert_eq!(pixels.dimensions(), (200, 100));
        assert_eq!(pixels.get_pixel(50, 50).0, [255, 0, 0, 255]);
        assert_eq!(pixels.get_pixel(150, 50).0, [0, 0, 255, 255]);
    }

    /// A preview is sharp, but a 10-pixel icon is not blown up to 2560.
    #[test]
    fn a_preview_is_at_most_four_times_the_svg() {
        let folder = Folder::new("svg-small");
        let icon = r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">
            <circle cx="5" cy="5" r="4" fill="green"/></svg>"#;
        let path = folder.write("icon.svg", icon.as_bytes());

        let small = decode(
            &path,
            "svg",
            Purpose::Preview {
                max_dimension: 2560,
            },
        )
        .unwrap();
        let capped = decode(
            &folder.write("halves.svg", HALVES.as_bytes()),
            "svg",
            Purpose::Preview { max_dimension: 300 },
        )
        .unwrap();

        assert_eq!(small.full_size, (40, 40));
        assert_eq!(capped.full_size, (300, 150));
    }

    #[test]
    fn an_svg_keeps_its_transparency() {
        let folder = Folder::new("svg-clear");
        let dot = r#"<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">
            <rect x="8" y="8" width="4" height="4" fill="black"/></svg>"#;
        let path = folder.write("dot.svg", dot.as_bytes());

        let pixels = decode(&path, "svg", Purpose::Thumbnail { size: 20 })
            .unwrap()
            .image
            .to_rgba8();

        assert_eq!(pixels.get_pixel(0, 0).0[3], 0);
        assert_eq!(pixels.get_pixel(10, 10).0, [0, 0, 0, 255]);
    }

    /// An SVG can name other files to draw. The media process draws what is
    /// in the SVG, and opens nothing else.
    #[test]
    fn an_svg_cannot_pull_in_another_file() {
        let folder = Folder::new("svg-href");
        let other = folder.path("other.png");
        image::RgbImage::from_pixel(8, 8, image::Rgb([255, 0, 0]))
            .save(&other)
            .unwrap();
        let svg = format!(
            r#"<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="8" height="8">
            <image width="8" height="8" href="{0}" xlink:href="{0}"/></svg>"#,
            other.display()
        );
        let path = folder.write("frame.svg", svg.as_bytes());

        let pixels = decode(&path, "svg", Purpose::Thumbnail { size: 8 })
            .unwrap()
            .image
            .to_rgba8();

        assert!(
            pixels.pixels().all(|pixel| pixel.0[3] == 0),
            "the other file was drawn"
        );
    }

    #[test]
    fn text_that_is_not_svg_answers_decode() {
        let folder = Folder::new("svg-bad");
        let path = folder.write("notes.svg", b"just some notes, no markup");

        let error = decode(&path, "svg", Purpose::Thumbnail { size: 64 }).err();

        assert!(matches!(error, Some(RenderError::Decode(_))), "{error:?}");
    }
}
