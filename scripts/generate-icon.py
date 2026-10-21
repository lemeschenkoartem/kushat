"""Render the project's original price-tag icon. Requires Pillow (builds use the committed ICO)."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
scale = 4
im = Image.new('RGBA', (256 * scale, 256 * scale), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
def box(coords): return tuple(round(n * scale) for n in coords)
def points(coords): return [(round(x * scale), round(y * scale)) for x, y in coords]
d.rounded_rectangle(box((7, 7, 249, 249)), radius=52 * scale, fill='#215C45')
d.polygon(points([(51, 56), (164, 56), (214, 106), (214, 191), (51, 191)]), fill='#FFFFFF')
d.ellipse(box((166, 80, 182, 96)), fill='#215C45')
# Letter K drawn as geometry, independent of machine fonts.
d.line(points([(86, 88), (86, 162)]), fill='#215C45', width=13 * scale)
d.line(points([(134, 88), (89, 125), (138, 163)]), fill='#215C45', width=13 * scale)
im = im.resize((256, 256), Image.Resampling.LANCZOS)
im.save(root / 'src/Kushat.App/Assets/Kushat.ico', sizes=[(n,n) for n in (16,24,32,48,64,128,256)])
im.save(root / 'artifacts/kushat-icon.png')
