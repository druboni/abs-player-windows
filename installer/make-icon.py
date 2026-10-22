"""Draws the app icon (white headphones on an orange rounded square) as a multi-size .ico and a PNG preview."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

out = Path(sys.argv[1])
SIZE = 256
ORANGE = (240, 134, 26, 255)
GLYPH = "\uE7F6"  # Headphones, Segoe Fluent Icons

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((8, 8, SIZE - 8, SIZE - 8), radius=56, fill=ORANGE)
font = ImageFont.truetype(r"C:\Windows\Fonts\SegoeIcons.ttf", 150)
box = d.textbbox((0, 0), GLYPH, font=font)
w, h = box[2] - box[0], box[3] - box[1]
d.text(((SIZE - w) / 2 - box[0], (SIZE - h) / 2 - box[1]), GLYPH, font=font, fill="white")

img.save(out / "AppIcon.png")
img.save(out / "AppIcon.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print("wrote", out / "AppIcon.ico")
