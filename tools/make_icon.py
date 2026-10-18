"""Draws the app icon (magnifier on a blue gradient tile) as PNG + multi-size ICO."""
import sys
from PIL import Image, ImageDraw

def draw(size: int) -> Image.Image:
    s = size * 4  # supersample for smooth edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / s
        gd.line([(0, y), (s, y)], fill=(int(64 + 40 * t), int(156 - 60 * t), int(255 - 30 * t), 255))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.23), fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    w = int(s * 0.085)
    cx, cy, r = s * 0.44, s * 0.44, s * 0.2
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(255, 255, 255, 255), width=w)
    off = r * 0.72
    d.line([(cx + off, cy + off), (s * 0.76, s * 0.76)], fill=(255, 255, 255, 255), width=int(w * 1.25))
    d.ellipse([s * 0.76 - w * 0.62, s * 0.76 - w * 0.62, s * 0.76 + w * 0.62, s * 0.76 + w * 0.62], fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
big = draw(256)
big.save(f"{out}/icon.png")
big.save(f"{out}/icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
