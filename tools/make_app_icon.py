"""Builds src/StpClient/Resources/Icons/Stp.ico: "STP UG" in white on a blue rounded tile.

The blue is the step-2 icon's (#1565C0), so the app icon matches the menu icons.
Each frame is drawn at 8x its size and downsampled, which keeps the text crisp at 16-256 px.

    python tools/make_app_icon.py [--preview out.png]

Needs Pillow and the Segoe UI Bold font (Windows).
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

BLUE = (21, 101, 192, 255)
WHITE = (255, 255, 255, 255)
SIZES = (16, 24, 32, 48, 64, 96, 128, 256)
FONT = Path(r"C:\Windows\Fonts\segoeuib.ttf")
TARGET = Path(__file__).resolve().parent.parent / "src" / "StpClient" / "Resources" / "Icons" / "Stp.ico"
SCALE = 8


def _fit(draw: ImageDraw.ImageDraw, text: str, width: float, height: float) -> ImageFont.FreeTypeFont:
    """The largest Segoe UI Bold that fits ``text`` in width x height (ink box)."""
    size = int(height * 1.6)
    while size > 4:
        font = ImageFont.truetype(str(FONT), size)
        left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
        if right - left <= width and bottom - top <= height:
            return font
        size -= 1
    return ImageFont.truetype(str(FONT), 4)


def _centered(draw: ImageDraw.ImageDraw, text: str, font, centre_x: float, centre_y: float) -> None:
    left, top, right, bottom = draw.textbbox((0, 0), text, font=font)
    draw.text((centre_x - (left + right) / 2, centre_y - (top + bottom) / 2), text, font=font, fill=WHITE)


def frame(size: int) -> Image.Image:
    big = size * SCALE
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    radius = big * 0.18
    draw.rounded_rectangle((0, 0, big - 1, big - 1), radius=radius, fill=BLUE)
    margin = big * (0.10 if size <= 24 else 0.13)
    inner = big - 2 * margin
    gap = inner * 0.10
    line = (inner - gap) / 2
    # Both lines share one font size: the one that fits "STP" (the wider word).
    font = _fit(draw, "STP", inner, line)
    _centered(draw, "STP", font, big / 2, margin + line / 2)
    _centered(draw, "UG", font, big / 2, margin + line + gap + line / 2)
    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", type=Path, help="also write a PNG strip of every frame")
    arguments = parser.parse_args()
    frames = [frame(size) for size in SIZES]
    frames[-1].save(TARGET, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    print(f"wrote {TARGET}")
    if arguments.preview:
        strip = Image.new("RGBA", (sum(SIZES) + 10 * (len(SIZES) + 1), max(SIZES) + 20), WHITE)
        x = 10
        for image in frames:
            strip.alpha_composite(image, (x, 10))
            x += image.width + 10
        strip.save(arguments.preview)


if __name__ == "__main__":
    main()
