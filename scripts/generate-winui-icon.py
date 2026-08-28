from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parents[1]
BASE_ICON = ROOT / "base_icon.png"
WINUI_ICON = ROOT / "packaging" / "winui" / "icon.ico"
SIZE = 1024
SCALE = 4
CANVAS = SIZE * SCALE


def c(value: str) -> tuple[int, int, int, int]:
    value = value.lstrip("#")
    return (
        int(value[0:2], 16),
        int(value[2:4], 16),
        int(value[4:6], 16),
        255,
    )


def sc(value: int | float) -> int:
    return int(round(value * SCALE))


def rounded_mask(size: tuple[int, int], radius: int) -> Image.Image:
    mask = Image.new("L", size, 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=radius, fill=255)
    return mask


def vertical_gradient(
    size: tuple[int, int],
    top: tuple[int, int, int, int],
    bottom: tuple[int, int, int, int],
) -> Image.Image:
    image = Image.new("RGBA", size)
    pixels = image.load()
    height = max(1, size[1] - 1)
    for y in range(size[1]):
        ratio = y / height
        row = tuple(
            int(top[index] * (1.0 - ratio) + bottom[index] * ratio)
            for index in range(4)
        )
        for x in range(size[0]):
            pixels[x, y] = row
    return image


def paste_round(
    base: Image.Image,
    box: tuple[int, int, int, int],
    radius: int,
    top: str,
    bottom: str,
    outline: str | None = None,
    outline_width: int = 1,
) -> None:
    x1, y1, x2, y2 = [sc(value) for value in box]
    width = x2 - x1
    height = y2 - y1
    mask = rounded_mask((width, height), sc(radius))
    shape = vertical_gradient((width, height), c(top), c(bottom))
    base.alpha_composite(Image.composite(shape, Image.new("RGBA", (width, height)), mask), (x1, y1))
    if outline:
        draw = ImageDraw.Draw(base)
        draw.rounded_rectangle(
            (x1, y1, x2, y2),
            radius=sc(radius),
            outline=c(outline),
            width=sc(outline_width),
        )


def add_shadow(
    base: Image.Image,
    box: tuple[int, int, int, int],
    radius: int,
    opacity: int,
    blur: int,
    offset: tuple[int, int],
) -> None:
    x1, y1, x2, y2 = [sc(value) for value in box]
    width = x2 - x1
    height = y2 - y1
    shadow = Image.new("RGBA", (width, height), (0, 0, 0, opacity))
    mask = rounded_mask((width, height), sc(radius))
    shadow.putalpha(mask)
    shadow = shadow.filter(ImageFilter.GaussianBlur(sc(blur)))
    base.alpha_composite(shadow, (x1 + sc(offset[0]), y1 + sc(offset[1])))


def draw_icon() -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    tile = (96, 96, 928, 928)
    add_shadow(image, tile, 184, 90, 34, (0, 26))
    paste_round(image, tile, 184, "#27313c", "#111820", outline="#3a4654", outline_width=2)

    # Quiet blue top-left sheen, clipped into the app tile.
    sheen = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    sheen_draw = ImageDraw.Draw(sheen)
    sheen_draw.ellipse(
        (sc(126), sc(120), sc(650), sc(590)),
        fill=(49, 142, 190, 46),
    )
    tile_mask = Image.new("L", (CANVAS, CANVAS), 0)
    tile_mask_draw = ImageDraw.Draw(tile_mask)
    tile_mask_draw.rounded_rectangle(
        tuple(sc(value) for value in tile),
        radius=sc(184),
        fill=255,
    )
    image.alpha_composite(Image.composite(sheen, Image.new("RGBA", (CANVAS, CANVAS)), tile_mask))

    # Folder back and tab.
    add_shadow(image, (216, 318, 818, 782), 58, 110, 20, (0, 18))
    paste_round(image, (250, 282, 492, 396), 34, "#ffe07b", "#f9bd31", outline="#ffe596", outline_width=2)
    paste_round(image, (212, 340, 820, 758), 58, "#f8bc31", "#d9820c", outline="#ffdc68", outline_width=2)

    # Folder front, slightly taller and cleaner for small-size recognition.
    paste_round(image, (184, 414, 840, 790), 64, "#ffd44c", "#ee9f13", outline="#ffe38a", outline_width=2)
    draw.rounded_rectangle(
        (sc(224), sc(446), sc(800), sc(520)),
        radius=sc(34),
        fill=(255, 235, 143, 92),
    )

    font_path = Path("C:/Windows/Fonts/segoeuib.ttf")
    if not font_path.exists():
        font_path = Path("C:/Windows/Fonts/arialbd.ttf")
    font = ImageFont.truetype(str(font_path), sc(520))
    text = "S"
    bbox = draw.textbbox((0, 0), text, font=font)
    text_width = bbox[2] - bbox[0]
    text_height = bbox[3] - bbox[1]
    x = (CANVAS - text_width) // 2 - bbox[0] + sc(6)
    y = sc(218) - bbox[1]

    # Shadow and highlight make the S readable against both tile and folder.
    draw.text((x + sc(8), y + sc(10)), text, font=font, fill=(32, 23, 7, 118))
    draw.text((x - sc(2), y - sc(2)), text, font=font, fill=(255, 255, 255, 62))
    draw.text((x, y), text, font=font, fill=(255, 255, 255, 250))

    return image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def main() -> None:
    icon = draw_icon()
    BASE_ICON.parent.mkdir(parents=True, exist_ok=True)
    WINUI_ICON.parent.mkdir(parents=True, exist_ok=True)
    icon.save(BASE_ICON)
    icon.save(
        WINUI_ICON,
        format="ICO",
        sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)],
    )
    print(f"Wrote {BASE_ICON}")
    print(f"Wrote {WINUI_ICON}")


if __name__ == "__main__":
    main()
