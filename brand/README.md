# FoxTrans brand

An abstract mark: two intersecting rings with the intersection filled. Two
languages, one shared meaning — and, read as a system, one signal passing through
a shared core. No literal imagery, no gradient, no shadow, one colour.

It follows the same rules as the desktop studio: near-black or warm off-white
surfaces, one monoline weight, restrained geometry, everything derived from a
single grid.

Open [`index.html`](index.html) for the visual sheet.

## Files

| File | Use |
| --- | --- |
| `foxtrans-mark.svg` | The mark alone, square canvas. Inherits `currentColor`. |
| `foxtrans-mark-accent.svg` | Mark with the voice accent in the core. Optional, for live/active states. |
| `foxtrans-wordmark.svg` | Lettering alone, trimmed to the cap box. |
| `foxtrans-lockup.svg` | Mark + wordmark, horizontal. The default signature. |
| `foxtrans-lockup-stacked.svg` | Mark above wordmark, for square and narrow placements. |
| `foxtrans-icon-dark.svg` | App icon, dark theme: rounded square `#0D0D0D`. |
| `foxtrans-icon-light.svg` | App icon, light theme: rounded square `#F5F4F1`. |
| `favicon.svg` | Transparent, switches ink with `prefers-color-scheme`. |
| `generate.py` | Optional tooling. Regenerates every file above from one geometry block. |

Every asset except the two app icons and the favicon draws with `currentColor`,
so a container's text colour drives it:

```html
<span style="color: #ECECEC"><!-- paste foxtrans-mark.svg --></span>
```

## Geometry

All assets share a 32-unit vertical grid, so they compose without rescaling.

- Ring radius `9.8`, ring centres at `16 ± 3.8`, monoline stroke `2.8`.
- The lens is the exact intersection of the two circles inset by `2.8/2 + 0.62`,
  which keeps a visible gap between the core and the strokes. Without that gap
  the mark turns into a blob below ~24 px.
- Mark ink box: `30 × 22.4`. Wordmark: cap height `18`, x-height `13.2`,
  baseline `25`, advance width `102.84`.
- Lettering is geometric and monoline: `o` and `a` are true circles, `n` is
  a semicircular arch on two stems, `s` is two elliptical arcs with a `0.3` spine
  offset that opens both terminals.

Regenerate after changing any constant:

```bash
python brand/generate.py
```

## Rules

- **Clear space**: keep the mark's ring radius (`9.8` units, ~31 % of the mark
  height) free on every side. For the lockup, use the mark's full height.
- **Minimum size**: mark `16 px`; horizontal lockup `20 px` tall, below that the
  lettering fills in — use the mark alone.
- **Colour**: one ink per placement. Ink on background, never two inks in the
  mark, except the documented accent core.
- **Don't**: rotate, skew, outline, add a gradient or shadow, recolour the rings
  separately from the core, place on a busy photo, or rebuild the lettering in a
  system font — the wordmark is drawn geometry, not type.

## Palette

Taken from `FoxTrans.Desktop/Styles/DesignSystem.axaml`, so the brand and the
running application never drift apart.

| Token | Dark | Light |
| --- | --- | --- |
| Ink | `#ECECEC` | `#20201F` |
| Background | `#0D0D0D` | `#F5F4F1` |
| Voice accent | `#39BCE8` | `#1479B5` |

## Raster output

No PNG or ICO is committed; the SVGs are the source of truth. To produce raster
sizes, render `foxtrans-icon-dark.svg` (or the light variant) with any SVG
rasteriser, for example:

```bash
resvg --width 256 --height 256 brand/foxtrans-icon-dark.svg icon-256.png
```
