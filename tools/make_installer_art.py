"""Draws the installer's side banner and header images from the Mica logo.

Usage: python -I tools/make_installer_art.py assets/mica-1024.png installer/art
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFilter

logo_path, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
logo = Image.open(logo_path).convert("RGBA")

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def banner(w, h):
    # soft diagonal gradient: cyan -> periwinkle -> violet
    img = Image.new("RGB", (w, h))
    px = img.load()
    c1, c2, c3 = (94, 200, 245), (107, 124, 246), (155, 107, 242)
    for y in range(h):
        for x in range(w):
            t = (x / w * 0.35 + y / h * 0.65)
            px[x, y] = lerp(c1, c2, t / 0.5) if t < 0.5 else lerp(c2, c3, (t - 0.5) / 0.5)
    # glassy light blobs
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(glow)
    d.ellipse((-w * 0.4, -h * 0.15, w * 0.9, h * 0.35), fill=(255, 255, 255, 60))
    d.ellipse((w * 0.2, h * 0.7, w * 1.4, h * 1.2), fill=(255, 255, 255, 40))
    glow = glow.filter(ImageFilter.GaussianBlur(w * 0.12))
    img = Image.alpha_composite(img.convert("RGBA"), glow)
    # logo with a soft shadow
    s = int(w * 0.7)
    lg = logo.resize((s, s), Image.LANCZOS)
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    sh = Image.new("RGBA", lg.size, (30, 20, 90, 0))
    sh.putalpha(lg.getchannel("A").point(lambda a: int(a * 0.45)))
    pos = ((w - s) // 2, int(h * 0.30))
    shadow.paste(sh, (pos[0], pos[1] + int(s * 0.08)), sh)
    shadow = shadow.filter(ImageFilter.GaussianBlur(s * 0.08))
    img = Image.alpha_composite(img, shadow)
    img.alpha_composite(lg, pos)
    return img.convert("RGB")

def header(sz):
    img = Image.new("RGB", (sz, sz), (255, 255, 255))
    lg = logo.resize((sz, sz), Image.LANCZOS)
    img.paste(lg, (0, 0), lg)
    return img

scales = [1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5]
for sc in scales:
    tag = str(int(sc * 100))
    banner(int(164 * sc), int(314 * sc)).save(os.path.join(out, f"wizard-{tag}.bmp"))
    header(int(55 * sc)).save(os.path.join(out, f"small-{tag}.bmp"))
print("ok")
