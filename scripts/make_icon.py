#!/usr/bin/env python3
"""Generates Resources/AppIcon.icns: three overlapping emission-line discs on a dark sky."""
import math, os, subprocess, tempfile
from PIL import Image, ImageDraw, ImageFilter

def render(size):
    s = size
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = s * 0.46
    d.rounded_rectangle([s*0.04, s*0.04, s*0.96, s*0.96], radius=s*0.22, fill=(14, 18, 34, 255))
    # faint stars
    import random
    random.seed(3)
    for _ in range(int(s*s/4000)):
        x, y = random.uniform(s*0.08, s*0.92), random.uniform(s*0.08, s*0.92)
        rad = random.uniform(s*0.002, s*0.006)
        d.ellipse([x-rad, y-rad, x+rad, y+rad], fill=(220, 225, 240, 200))
    glow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    g = ImageDraw.Draw(glow)
    cx, cy, rr = s*0.5, s*0.52, s*0.26
    offs = s*0.13
    cols = [((255, 120, 60, 170), (cx - offs, cy + offs*0.6)), ((255, 205, 90, 160), (cx + offs, cy + offs*0.6)), ((80, 190, 230, 170), (cx, cy - offs*0.9))]
    for col, (x, y) in cols:
        g.ellipse([x-rr, y-rr, x+rr, y+rr], fill=col)
    glow = glow.filter(ImageFilter.GaussianBlur(s*0.05))
    img.alpha_composite(glow)
    return img

out = os.path.join(os.path.dirname(__file__), "..", "Resources")
with tempfile.TemporaryDirectory() as tmp:
    iconset = os.path.join(tmp, "AppIcon.iconset")
    os.makedirs(iconset)
    for base in [16, 32, 128, 256, 512]:
        render(base).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
        render(base*2).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
    subprocess.check_call(["iconutil", "-c", "icns", iconset, "-o", os.path.join(out, "AppIcon.icns")])
    render(256).save(os.path.join(out, "AppIcon-256.png"))
print("icon written")
