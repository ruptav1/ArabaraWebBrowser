# Arabara — triangular machine emblem

Created with the built-in ImageGen tool on 2026-10-05 for version 0.3.3. A sharp upright crimson triangle with horizontal negative-space grooves and a triangular inner opening. The mood is futuristic machine intelligence and hacker terminals, inspired by the user's Skynet reference.

- Edit reference: the previous rounded emblem, preserved as `archive/arabara-rounded-0.3.2.png`.
- Source image: `arabara.png`, 1254 x 1254 pixels, transparent alpha background.
- Windows icon: `arabara.ico`, with 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames.
- Embedded UI image: `arabara-mark-128.png`; one decoded bitmap is shared within each window.
- `build-icon.ps1` only resizes and packages the image; it does not redraw the artwork.
- The icon is static. No animation, additional rendering loop or full-resolution runtime logo decoding was added.
- Executable: `../bin/Arabara.exe`.

## Final ImageGen prompt

```text
Use case: logo-brand, precise redesign of an existing application emblem.
Asset type: Windows browser icon and native UI logo for Arabara.
Input image 1 is the current Arabara logo, an edit target. Keep only the crimson brand identity and its clean standalone emblem format. Replace the rounded rings, sun and stem completely with an original sharp triangular technological mark.
Primary request: a dramatically sharper hacker / futuristic machine-intelligence emblem, evoking the ominous industrial-computer mood of Skynet. One upright, bilaterally symmetric triangular silhouette, razor-sharp apex and crisp straight bevel-like angular corners. Make the triangle out of strong crimson geometry with 3 or 4 deliberate horizontal machined negative-space grooves, like segmented circuit plates or horizontal ventilation slots. The grooves must be broad, clean, spaced and readable when reduced to 24 or 32 pixels. A small centered triangular negative-space core can subtly suggest an angular A; keep the whole symbol unified and immediately triangular. Bold iconic mass, not a web of tiny circuitry. No rounded edges.
Style: professionally designed minimal vector-like logo, exceptionally clean hard edges, flat vivid crimson red (#FF294A), essentially one color. Do not add surface noise, grunge, visual artifacts, glow, shadow, metallic texture, 3D extrusion, backdrop, or cinematic scenery. This is the application icon itself, not a presentation mockup.
Composition: square canvas, one large centered complete emblem occupying about 82 percent of the canvas width with balanced transparent padding; sharp corners fully inside the canvas. True alpha transparency, including the horizontal cuts and inner cutouts. No background tile, no circle or square surrounding the triangle.
No text, no letters printed below, no wordmark, no exclamation mark, no skull, no eye, no biohazard symbol, no decorative extra marks. Return exactly one finished logo.
```

The edit used the previous `assets/arabara.png`, now preserved in the archive. Generated source: a local image (not part of the repository). The project uses its own saved copy.
