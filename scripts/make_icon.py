#!/usr/bin/env python3
"""Builds Resources/AppIcon.icns (and AppIcon-256.png) from Resources/AppIcon-source.png.

The source is the finished rounded-square artwork with transparent corners. It is trimmed to its
opaque bounds, squared, and placed on the macOS icon grid (the shape fills 824 of 1024 points so
it sits like the system icons), then exported at every iconset size.
"""
import os, subprocess, tempfile
from PIL import Image

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Resources")
src = Image.open(os.path.join(root, "AppIcon-source.png")).convert("RGBA")
# Trim to the opaque artwork (alpha > 8) and square it on a transparent canvas.
bbox = src.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
art = src.crop(bbox)
side = max(art.size)
square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
square.alpha_composite(art, ((side - art.width) // 2, (side - art.height) // 2))

def render(size):
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    inner = max(1, round(size * 824 / 1024))
    scaled = square.resize((inner, inner), Image.LANCZOS)
    off = (size - inner) // 2
    canvas.alpha_composite(scaled, (off, off))
    return canvas

with tempfile.TemporaryDirectory() as tmp:
    iconset = os.path.join(tmp, "AppIcon.iconset")
    os.makedirs(iconset)
    for base in [16, 32, 128, 256, 512]:
        render(base).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
        render(base * 2).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
    subprocess.check_call(["iconutil", "-c", "icns", iconset, "-o", os.path.join(root, "AppIcon.icns")])
    render(256).save(os.path.join(root, "AppIcon-256.png"))
print("icon written")
