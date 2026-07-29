#!/usr/bin/env python3
"""Generates every FoxTrans brand asset in brand/ from one geometry definition.

Optional tooling: the produced .svg files are the deliverables and are committed.
Re-run it (`python brand/generate.py`) after changing any constant below so the
mark, the wordmark, and the lockups stay on the same grid.

Design grid
-----------
Everything lives on a 32-unit vertical grid. The mark is two intersecting rings
whose intersection - the lens - is filled: two languages, one shared meaning.
The lens is inset from the ring strokes so it reads as a separate core instead of
merging into a blob. The wordmark is monoline geometric lettering whose stroke
weight and radii come from the same grid, so mark and text share one voice.
"""
import math
import os

# ----------------------------------------------------------------- palette
INK_DARK = "#ECECEC"      # Color.TextPrimary, dark theme
INK_LIGHT = "#20201F"     # Color.TextPrimary, light theme
BG_DARK = "#0D0D0D"       # Color.AppBackground, dark theme
BG_LIGHT = "#F5F4F1"      # Color.AppBackground, light theme
ACCENT = "#39BCE8"        # Color.VoiceAccent, dark theme

# ----------------------------------------------------------------- mark grid
GRID = 32.0
CY = GRID / 2             # 16, vertical centre of every asset
RING_R = 9.8              # ring radius, stroke centreline
RING_DX = 3.8             # horizontal offset of each ring centre from centre
STROKE = 2.8              # monoline weight, shared with the wordmark
LENS_GAP = 0.62           # clear space between ring stroke and lens
MARK_W = 2 * (RING_DX + RING_R) + STROKE          # 30.0
MARK_H = 2 * RING_R + STROKE                      # 22.4

# ------------------------------------------------------------ wordmark grid
BASELINE = 25.0
CAP = 7.0                                 # cap height 18
X_TOP = 11.8                              # x-height 13.2
HALF = STROKE / 2                         # endpoint inset for round caps
T = CAP + HALF                            # 8.4  cap-height centreline top
BB = BASELINE - HALF                      # 23.6 baseline centreline
XX = X_TOP + HALF                         # 13.2 x-height centreline top
MID = 15.2                                # crossbar of F, a shade above centre
ROUND_CY = (X_TOP + BASELINE) / 2         # 18.4 centre of round letters
ROUND_R = (BASELINE - X_TOP) / 2 - HALF   # 5.2  centreline radius
OVERSHOOT = 0.16                          # round letters break the line slightly
LOCKUP_GAP = 8.6                          # mark to wordmark
STACK_GAP = 7.0                           # mark to wordmark, stacked lockup


def n(v):
    """Trim a float to a short, stable SVG token."""
    s = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


# ------------------------------------------------------------------ the mark
def _circle(cx, cy, r):
    return (f"M{n(cx - r)} {n(cy)}"
            f"A{n(r)} {n(r)} 0 1 0 {n(cx + r)} {n(cy)}"
            f"A{n(r)} {n(r)} 0 1 0 {n(cx - r)} {n(cy)}Z")


def _lens(cx, cy, dx, r):
    """Exact intersection of two equal circles at cx +/- dx."""
    h = math.sqrt(r * r - dx * dx)
    return (f"M{n(cx)} {n(cy - h)}"
            f"A{n(r)} {n(r)} 0 0 1 {n(cx)} {n(cy + h)}"
            f"A{n(r)} {n(r)} 0 0 1 {n(cx)} {n(cy - h)}Z")


def mark(cx=CY, cy=CY, ink="currentColor", lens_ink=None):
    """Two intersecting rings with the intersection filled."""
    rings = _circle(cx - RING_DX, cy, RING_R) + _circle(cx + RING_DX, cy, RING_R)
    lens = _lens(cx, cy, RING_DX, RING_R - HALF - LENS_GAP)
    return (f'<path d="{rings}" fill="none" stroke="{ink}" stroke-width="{n(STROKE)}"/>'
            f'<path d="{lens}" fill="{lens_ink or ink}"/>')


# -------------------------------------------------------------- the wordmark
def _line(x0, y0, x1, y1):
    return f"M{n(x0)} {n(y0)}L{n(x1)} {n(y1)}"


def _ring_at(cx, r):
    return _circle(cx, ROUND_CY, r)


def _letter_F(x):
    stem = _line(x + HALF, T, x + HALF, BB)
    top = _line(x + HALF, T, x + 9.9, T)
    mid = _line(x + HALF, MID, x + 8.7, MID)
    return stem + top + mid, 12.7


def _letter_o(x):
    r = ROUND_R + OVERSHOOT
    return _ring_at(x + HALF + r, r), 2 * (r + HALF) + 1.5


def _letter_x(x):
    w = 9.4
    return (_line(x + HALF, XX, x + HALF + w, BB)
            + _line(x + HALF + w, XX, x + HALF, BB)), w + STROKE + 1.4


def _letter_T(x):
    w = 11.6
    bar = _line(x + HALF, T, x + HALF + w, T)
    stem = _line(x + HALF + w / 2, T, x + HALF + w / 2, BB)
    return bar + stem, w + STROKE + 0.4


def _letter_r(x):
    r = 5.0
    stem = _line(x + HALF, XX, x + HALF, BB)
    shoulder = (f"M{n(x + HALF)} {n(XX + r)}"
                f"A{n(r)} {n(r)} 0 0 1 {n(x + HALF + r)} {n(XX)}")
    return stem + shoulder, r + STROKE + 1.6


def _letter_a(x):
    r = ROUND_R + OVERSHOOT
    cx = x + HALF + r
    bowl = _ring_at(cx, r)
    stem = _line(cx + r, XX, cx + r, BB)
    return bowl + stem, 2 * (r + HALF) + 1.5


def _letter_n(x):
    r = ROUND_R
    left = x + HALF
    right = left + 2 * r
    arch = (f"M{n(left)} {n(BB)}L{n(left)} {n(ROUND_CY)}"
            f"A{n(r)} {n(r)} 0 0 1 {n(right)} {n(ROUND_CY)}"
            f"L{n(right)} {n(BB)}")
    return arch, 2 * r + STROKE + 1.5


def _letter_s(x):
    """Two elliptical arcs; the small spine offset opens both terminals."""
    rx, ry, spine = 3.8, (BB - XX) / 4, 0.3
    cx = x + HALF + rx + spine
    ty, by = XX + ry, BB - ry
    a = math.radians(48)
    start = (cx + spine + rx * math.cos(a), ty - ry * math.sin(a))
    joint = (cx, ty + ry)
    end = (cx - spine - rx * math.cos(a), by + ry * math.sin(a))
    return (f"M{n(start[0])} {n(start[1])}"
            f"A{n(rx)} {n(ry)} 0 1 0 {n(joint[0])} {n(joint[1])}"
            f"A{n(rx)} {n(ry)} 0 1 1 {n(end[0])} {n(end[1])}"), \
        2 * (rx + spine) + STROKE + 1.4


LETTERS = [_letter_F, _letter_o, _letter_x, _letter_T,
           _letter_r, _letter_a, _letter_n, _letter_s]
# Optical corrections between adjacent pairs, left to right.
KERNS = [0.0, -0.5, -0.4, -1.5, -1.1, -0.5, -0.4, -0.4]


def wordmark(x0=0.0, ink="currentColor"):
    """Monoline geometric 'FoxTrans'. Returns (svg, advance width)."""
    parts, x = [], x0
    for i, letter in enumerate(LETTERS):
        x += KERNS[i]
        d, advance = letter(x)
        parts.append(d)
        x += advance
    body = "".join(parts)
    return (f'<path d="{body}" fill="none" stroke="{ink}" stroke-width="{n(STROKE)}"'
            ' stroke-linecap="round" stroke-linejoin="round"/>'), x - x0


_, WORD_W = wordmark()


# ------------------------------------------------------------------- writing
def svg(view_w, view_h, body, min_x=0.0, min_y=0.0, title=None):
    head = (f'<svg xmlns="http://www.w3.org/2000/svg" '
            f'viewBox="{n(min_x)} {n(min_y)} {n(view_w)} {n(view_h)}" '
            f'width="{n(view_w)}" height="{n(view_h)}" fill="none" '
            f'role="img" aria-label="{title or "FoxTrans"}">')
    return head + body + "</svg>\n"


def rounded_square(size, radius, fill):
    return (f'<rect width="{n(size)}" height="{n(size)}" rx="{n(radius)}" '
            f'ry="{n(radius)}" fill="{fill}"/>')


def write(name, content):
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), name)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(content)
    return name


def build():
    written = []

    # Mark, square canvas, inherits colour from its container.
    written.append(write("foxtrans-mark.svg", svg(GRID, GRID, mark(), title="FoxTrans")))

    # Mark with the live-voice accent in the core.
    written.append(write("foxtrans-mark-accent.svg",
                         svg(GRID, GRID, mark(lens_ink=ACCENT), title="FoxTrans")))

    # Wordmark only, trimmed to the cap box with 1u breathing room.
    word, w = wordmark()
    written.append(write("foxtrans-wordmark.svg",
                         svg(w, 20.0, word, min_y=6.0, title="FoxTrans")))

    # Horizontal lockup.
    total = MARK_W + LOCKUP_GAP + WORD_W
    body = mark(cx=MARK_W / 2) + wordmark(MARK_W + LOCKUP_GAP)[0]
    written.append(write("foxtrans-lockup.svg",
                         svg(total, GRID, body, title="FoxTrans")))

    # Readme lockup: light ink on the app's near-black surface. Unlike the
    # currentColor variants, it remains legible when embedded as an image.
    readme_lockup = (f'<rect width="{n(total)}" height="{n(GRID)}" '
                     f'rx="6" ry="6" fill="{BG_DARK}"/>'
                     + mark(cx=MARK_W / 2, ink=INK_DARK)
                     + wordmark(MARK_W + LOCKUP_GAP, ink=INK_DARK)[0])
    written.append(write("foxtrans-lockup-on-dark.svg",
                         svg(total, GRID, readme_lockup, title="FoxTrans")))

    # Stacked lockup: mark centred over the wordmark, trimmed to the ink.
    stack_h = MARK_H + STACK_GAP + (BASELINE - CAP) + OVERSHOOT
    stacked = (mark(cx=WORD_W / 2, cy=MARK_H / 2)
               + f'<g transform="translate(0 {n(MARK_H + STACK_GAP - CAP)})">'
               + wordmark()[0] + '</g>')
    written.append(write("foxtrans-lockup-stacked.svg",
                         svg(WORD_W, stack_h, stacked, title="FoxTrans")))

    # App icons: mark at 78% on a rounded square, one per theme.
    for name, bg, ink in (("foxtrans-icon-dark.svg", BG_DARK, INK_DARK),
                          ("foxtrans-icon-light.svg", BG_LIGHT, INK_LIGHT)):
        scale = 0.78
        offset = (GRID - GRID * scale) / 2
        inner = (rounded_square(GRID, 7.4, bg)
                 + f'<g transform="translate({n(offset)} {n(offset)}) '
                   f'scale({n(scale)})">' + mark(ink=ink) + '</g>')
        written.append(write(name, svg(GRID, GRID, inner, title="FoxTrans")))

    # Favicon: transparent, follows the viewer's theme through currentColor.
    favicon = (f'<style>svg{{color:{INK_LIGHT}}}'
               f'@media (prefers-color-scheme:dark){{svg{{color:{INK_DARK}}}}}'
               '</style>' + mark())
    written.append(write("favicon.svg", svg(GRID, GRID, favicon, title="FoxTrans")))

    written.append(write("index.html", sheet()))
    return written


# -------------------------------------------------------------- brand sheet
def _at(inner, view_w, view_h, height, min_x=0.0, min_y=0.0):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" '
            f'viewBox="{n(min_x)} {n(min_y)} {n(view_w)} {n(view_h)}" '
            f'style="height:{n(height)}px;width:auto" fill="none">{inner}</svg>')


def _construction():
    guide = 'stroke="currentColor" stroke-width="0.14" stroke-dasharray="1 1" opacity="0.55"'
    lines = "".join(f'<line x1="0.5" y1="{n(y)}" x2="31.5" y2="{n(y)}" {guide}/>'
                    for y in (CY - RING_R, CY, CY + RING_R))
    lines += f'<line x1="16" y1="0.5" x2="16" y2="31.5" {guide}/>'
    for dx in (-RING_DX, RING_DX):
        lines += (f'<circle cx="{n(CY + dx)}" cy="{n(CY)}" r="0.42" '
                  'fill="currentColor" opacity="0.55"/>')
    return _at(lines + mark(), GRID, GRID, 168)


def sheet():
    word, w = wordmark()
    lock_w = MARK_W + LOCKUP_GAP + WORD_W
    lockup = mark(cx=MARK_W / 2) + wordmark(MARK_W + LOCKUP_GAP)[0]
    stack_h = MARK_H + STACK_GAP + (BASELINE - CAP) + OVERSHOOT
    stacked = (mark(cx=WORD_W / 2, cy=MARK_H / 2)
               + f'<g transform="translate(0 {n(MARK_H + STACK_GAP - CAP)})">' + word + '</g>')

    def icon(bg, ink):
        scale = 0.78
        off = (GRID - GRID * scale) / 2
        return (rounded_square(GRID, 7.4, bg)
                + f'<g transform="translate({n(off)} {n(off)}) scale({n(scale)})">'
                + mark(ink=ink) + '</g>')

    swatches = "".join(
        f'<div class="sw"><span style="background:{value}"></span>'
        f'<b>{name}</b><code>{value}</code></div>'
        for name, value in (("Ink dark", INK_DARK), ("Ink light", INK_LIGHT),
                            ("Background dark", BG_DARK), ("Background light", BG_LIGHT),
                            ("Voice accent", ACCENT)))

    rows = [
        ("Mark", "Two intersecting rings, intersection filled: two languages, one "
                 "shared meaning. Holds down to 16&nbsp;px.",
         "".join(_at(mark(), GRID, GRID, h) for h in (128, 64, 40, 28, 20, 16))),
        ("Construction", f"32-unit grid. Ring radius {n(RING_R)}, centres at "
                         f"&plusmn;{n(RING_DX)}, monoline {n(STROKE)}, lens inset "
                         f"{n(LENS_GAP)} from the stroke.", _construction()),
        ("Wordmark", "Monoline geometric lettering drawn on the same grid: cap height "
                     f"{n(BASELINE - CAP)}, x-height {n(BASELINE - X_TOP)}, stroke {n(STROKE)}.",
         "".join(_at(word, w, 20.0, h, min_y=6.0) for h in (52, 30, 18))),
        ("Lockup", f"Mark, {n(LOCKUP_GAP)} units of air, wordmark. Optical centres align.",
         "".join(_at(lockup, lock_w, GRID, h) for h in (44, 30, 20))),
        ("Stacked lockup", "For square and narrow placements.",
         "".join(_at(stacked, WORD_W, stack_h, h) for h in (96, 56))),
        ("App icon", "Mark at 78&nbsp;% on a rounded square, one per theme.",
         "".join(_at(icon(BG_DARK, INK_DARK), GRID, GRID, h) for h in (88, 48, 32, 20))
         + "".join(_at(icon(BG_LIGHT, INK_LIGHT), GRID, GRID, h) for h in (88, 48, 32, 20))),
        ("Accent", "Optional live-voice variant: the core carries the accent.",
         _at(mark(lens_ink=ACCENT), GRID, GRID, 88) + _at(mark(lens_ink=ACCENT), GRID, GRID, 32)),
        ("Palette", "Straight from the desktop design system.", swatches),
    ]
    body = "".join(
        f'<section><h2>{title}</h2><p>{note}</p><div class="row">{content}</div></section>'
        for title, note, content in rows)
    return f"""<!doctype html>
<html lang="en"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>FoxTrans brand</title>
<style>
  :root {{ color-scheme: light dark; }}
  body {{ margin:0; padding:48px 32px 72px; background:{BG_LIGHT}; color:{INK_LIGHT};
    font:14px/1.55 "Segoe UI Variable","Segoe UI",system-ui,sans-serif; }}
  header {{ max-width:640px; margin-bottom:44px; }}
  h1 {{ font-size:20px; font-weight:600; margin:0 0 8px; letter-spacing:-0.01em; }}
  header p {{ margin:0; opacity:0.66; }}
  section {{ max-width:960px; padding:22px 0; border-top:1px solid rgba(0,0,0,0.12); }}
  h2 {{ font-size:13px; font-weight:600; margin:0 0 4px; }}
  section p {{ margin:0 0 18px; font-size:12.5px; opacity:0.62; max-width:60ch; }}
  .row {{ display:flex; align-items:center; gap:26px; flex-wrap:wrap; }}
  .sw {{ display:flex; align-items:center; gap:10px; font-size:12px; }}
  .sw span {{ width:26px; height:26px; border-radius:7px;
    box-shadow:inset 0 0 0 1px rgba(0,0,0,0.16); }}
  .sw b {{ font-weight:500; }}
  .sw code {{ opacity:0.55; }}
  @media (prefers-color-scheme: dark) {{
    body {{ background:{BG_DARK}; color:{INK_DARK}; }}
    section {{ border-color:rgba(255,255,255,0.10); }}
    .sw span {{ box-shadow:inset 0 0 0 1px rgba(255,255,255,0.18); }}
  }}
</style>
<header><h1>FoxTrans brand</h1>
<p>One monoline system for the mark and the lettering, monochrome, inheriting the
current text colour. Generated by <code>brand/generate.py</code>.</p></header>
{body}
</html>
"""


if __name__ == "__main__":
    for name in build():
        print(name)
    print(f"wordmark width {n(WORD_W)}  mark {n(MARK_W)}x{n(MARK_H)}")
