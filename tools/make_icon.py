"""Builds Mica's icon files from the transparent logo.

Usage: python -I tools/make_icon.py assets/mica-logo-original.webp assets
Removes stray specks around the logo, crops it, pads it to a square and
exports PNGs plus a multi-size .ico.
"""
import sys
from collections import deque
from PIL import Image, ImageFilter

src, out_dir = sys.argv[1], sys.argv[2]
im = Image.open(src).convert("RGBA")
W, H = im.size
alpha = im.getchannel("A")

# Keep only the main blob: flood fill from the centre over alpha > 8 pixels
# (after a small dilation so soft edges stay attached), drop everything else.
solid = alpha.point(lambda v: 255 if v > 8 else 0).filter(ImageFilter.MaxFilter(9))
sp = solid.load()
keep = bytearray(W * H)
q = deque([(W // 2, H // 2)])
while q:
    x, y = q.popleft()
    i = y * W + x
    if keep[i] or not sp[x, y]:
        continue
    keep[i] = 1
    if x > 0: q.append((x - 1, y))
    if x < W - 1: q.append((x + 1, y))
    if y > 0: q.append((x, y - 1))
    if y < H - 1: q.append((x, y + 1))
keep_mask = Image.frombytes("L", (W, H), bytes(255 if k else 0 for k in keep))
im.putalpha(Image.composite(alpha, Image.new("L", (W, H), 0), keep_mask))

bbox = im.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
im = im.crop(bbox)
side = int(max(im.size) * 1.04)
sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
sq.paste(im, ((side - im.size[0]) // 2, (side - im.size[1]) // 2))
sq = sq.resize((1024, 1024), Image.LANCZOS)
sq.save(f"{out_dir}/mica-1024.png")
for s in (256, 128, 64, 48, 32, 16):
    sq.resize((s, s), Image.LANCZOS).save(f"{out_dir}/mica-{s}.png")
sq.save(f"{out_dir}/mica.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

prev = Image.new("RGBA", (1100, 550), (30, 30, 36, 255))
prev.paste((245, 245, 250, 255), (550, 0, 1100, 550))
small = sq.resize((500, 500), Image.LANCZOS)
prev.alpha_composite(small, (25, 25))
prev.alpha_composite(small, (575, 25))
prev.convert("RGB").save(f"{out_dir}/_preview.png")
print("ok", bbox)
