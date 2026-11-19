# Arabara — striped menu logo and inverted application icon

Version 0.4.2 replaces the ASCII menu/start-page logo with a vector version of the original artwork drawn by `HomeView.DrawStripedMark` in `src/Chrome.cs`. The triangle, central triangular opening and three horizontal slots are traced from `arabara.png`, flipped vertically so the apex points up, stretched to about 2.4x the original width-to-height ratio (184 x 76 logical px), and filled with thin, evenly spaced horizontal stripes (crimson-to-deep-red gradient). The start-page ARABARA title beside it uses Windows' Bahnschrift SemiBold (Segoe UI Semibold fallback), stretched up to 1.55x horizontally with open letter spacing to match the wide mark. The same mark (28 px tall) replaces the ARABARA wordmark at the top-left of the tab strip (`MarkLabel`). Stripes are drawn in device pixels (3 px period, 1 px gap at 100% scaling) so they stay crisp at any DPI. It is drawn only on normal paint events, with no animation timer or bitmap decoding. The Windows application icon remains the artwork described below.

## Windows application icon

Version 0.4.0 uses the exact triangular design selected by the user on 2026-10-05, turned upside down so its apex points downward. The broad horizontal cutouts and central triangular opening are preserved. The final edit used the built-in ImageGen tool.

- User-selected source: a local image (not part of the repository).
- Current project image: `arabara.png`.
- Windows icon: `arabara.ico`, with 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames.
- Generated 128 px preview: `arabara-mark-128.png` (not embedded or loaded by the start page).
- Since 2026-10-11 the icon sits on a charcoal tile (RGB 18, 16, 16), a rounded square with corners rounded by a fifth of its side and a faint lighter edge (RGB 48, 42, 40) from 32 px up, so it reads on dark and light taskbars. The logo's ink spans 64% of the tile's width (78% at 24 px and below), centred on it; the artwork itself keeps its transparency. (A white-on-crimson variant was tried on 2026-10-05 and reverted at the user's request.)
- `build-icon.ps1` draws the tile, scales the generated artwork onto it and packages the frames; `site/favicon.ico` is a copy of `arabara.ico`.
- No animated visual effects, shaders or background loops are added.
- Executable: `../bin/Arabara.exe`.

## Final ImageGen prompt

```text
Precise edit of the supplied Arabara logo: rotate the ENTIRE EXISTING IMAGE by exactly 180 degrees, so the triangle's sharp apex points DOWN and its long horizontal base is on TOP. Treat this as rotating the original finished artwork, not redesigning it. Preserve all existing crimson-red shapes, the number and width of its horizontal slots, the central triangular hole, stroke shapes, proportions, colors, fine shading, and clear margins exactly. No new rings, no additional stripes, no text, no retouching, no new effects. Preserve the transparent background and transparent internal cutouts. Return only the same complete logo turned upside down on a square canvas.
```

Generated image: a local image (not part of the repository). The project consumes its own saved copy.
