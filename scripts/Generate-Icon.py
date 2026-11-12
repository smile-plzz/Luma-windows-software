"""Regenerate the checked-in Windows icon from the original SVG."""
from io import BytesIO
from pathlib import Path
import cairosvg
from PIL import Image

assets = Path(__file__).resolve().parents[1] / 'src/Luma.App/Assets'
png = cairosvg.svg2png(url=str(assets / 'Luma.svg'), output_width=256, output_height=256)
Image.open(BytesIO(png)).save(assets / 'Luma.ico', sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)])
