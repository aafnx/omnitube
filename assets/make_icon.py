"""Generates assets/app.ico (and app.png preview): OMNIBOX ring with a play glyph.

Palette from the OMNIBOX brand book: Deep Night background, the "Light" gradient
(Ice -> Iris -> Ember) on the ring, Signal for the glyph.
Run: python assets/make_icon.py
"""
import os
import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
S = 1024  # supersampled canvas

DEEP_NIGHT = (10, 12, 31)
ICE = np.array([190, 239, 255], float)
IRIS = np.array([124, 108, 255], float)
EMBER = np.array([255, 150, 84], float)
SIGNAL = (222, 228, 238)


def light_gradient(size):
    """Diagonal Ice -> Iris -> Ember, top-left to bottom-right (order is fixed by the brand book)."""
    y, x = np.mgrid[0:size, 0:size].astype(float)
    t = (x + y) / (2 * (size - 1))
    t = t[..., None]
    first = ICE + (IRIS - ICE) * np.clip(t / 0.5, 0, 1)
    second = IRIS + (EMBER - IRIS) * np.clip((t - 0.5) / 0.5, 0, 1)
    rgb = np.where(t < 0.5, first, second)
    return Image.fromarray(rgb.astype(np.uint8), "RGB")


def build():
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # rounded-square tile
    tile = Image.new("L", (S, S), 0)
    ImageDraw.Draw(tile).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)
    canvas.paste(Image.new("RGBA", (S, S), DEEP_NIGHT + (255,)), (0, 0), tile)

    # the ring
    c = S / 2
    outer = S * 0.36
    inner = S * 0.285
    ring = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(ring)
    d.ellipse([c - outer, c - outer, c + outer, c + outer], fill=255)
    d.ellipse([c - inner, c - inner, c + inner, c + inner], fill=0)
    canvas.paste(light_gradient(S).convert("RGBA"), (0, 0), ring)

    # play glyph, nudged right for optical centring
    r = S * 0.16
    cx = c + r * 0.12
    tri = [(cx - r * 0.62, c - r), (cx - r * 0.62, c + r), (cx + r, c)]
    glyph = Image.new("L", (S, S), 0)
    ImageDraw.Draw(glyph).polygon(tri, fill=255)
    canvas.paste(Image.new("RGBA", (S, S), SIGNAL + (255,)), (0, 0), glyph)

    return canvas


if __name__ == "__main__":
    img = build()
    img.resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "app.png"))
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    img.resize((256, 256), Image.LANCZOS).save(os.path.join(HERE, "app.ico"), sizes=sizes)
    print("wrote app.ico / app.png")
