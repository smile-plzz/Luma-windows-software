# Luma identity and executable

**Luma: Your memories, beautifully within reach.**

Product description: A local media gallery for Windows. Browse photos and videos on existing folders and drives, revisit your timeline, and keep your memories organized without moving your originals.

The original icon combines layered photo cards, a mountain and sun, and an L monogram on a violet rounded square. `src/Luma.App/Assets/Luma.svg` is the editable source. `Luma.ico` includes 16, 24, 32, 48, 64, 128 and 256 pixel images and is embedded into the executable and used by the native window.

Regenerate from the repository root with Python, Pillow and CairoSVG installed:

```sh
python scripts/Generate-Icon.py
```

These packages are icon-development tools only, not app runtime dependencies. The ICO is checked in so Windows builds do not require them.

`Luma.App.csproj` owns the product/title, description, copyright, semantic version and Windows file/assembly versions. Settings shows the product tagline and version. Update these together for each release. Keep `Luma.App.exe` and its bundled files together; the icon does not turn the portable distribution into an installer or signed package.

The native smoke verifies Windows file metadata, the published window icon and extraction of the embedded EXE icon. Inspect the resulting icon and gallery screenshots with each branding change.
