"""Procedural tree textures (numpy only; runs inside Blender or plain Python).

Every texture is generated from a seed, so the library stays repeatable and free (TR2):
  foliage cards  needle sprays (fir, spruce, hemlock, douglas, pine, silverfir, lacy, noble, whitepine),
                 leaf clusters (maple, birch, beech, aspen, oak, cherry) in summer and autumn colours, bare
                 twig silhouettes for winter
  bark           ridged, furrowed, plated, scaly, smooth, birch, aspen, yellow-birch, oak and cherry styles
                 (tileable), each with a height field for its normal map

Cards are laid out with the branch running along +u (u = 0 at the branch, u = 1 at the tip) and
the spray spreading across v, so a card placed along a branch reads correctly.
"""
import math

import numpy as np


def hex_rgb(h):
    return np.array([int(h[i:i + 2], 16) / 255 for i in (1, 3, 5)], np.float32)


def canvas(n):
    return np.zeros((n, n, 4), np.float32)


def _over(region, rgb, a):
    """Composite a colour with coverage a over an RGBA region (straight alpha)."""
    a = a[..., None]
    out_a = a + region[..., 3:4] * (1 - a)
    safe = np.maximum(out_a, 1e-6)
    region[..., :3] = (rgb * a + region[..., :3] * region[..., 3:4] * (1 - a)) / safe
    region[..., 3:4] = out_a


def stroke(img, p0, p1, w0, w1, rgb):
    """An anti-aliased line from p0 to p1 (pixels), tapering from width w0 to w1."""
    h, w = img.shape[:2]
    pad = max(w0, w1) + 1.5
    xa, xb = int(max(0, min(p0[0], p1[0]) - pad)), int(min(w, max(p0[0], p1[0]) + pad + 1))
    ya, yb = int(max(0, min(p0[1], p1[1]) - pad)), int(min(h, max(p0[1], p1[1]) + pad + 1))
    if xb <= xa or yb <= ya:
        return
    ys, xs = np.mgrid[ya:yb, xa:xb].astype(np.float32) + 0.5
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    l2 = dx * dx + dy * dy or 1e-9
    t = np.clip(((xs - p0[0]) * dx + (ys - p0[1]) * dy) / l2, 0, 1)
    d = np.hypot(xs - (p0[0] + t * dx), ys - (p0[1] + t * dy))
    r = (w0 + (w1 - w0) * t) / 2
    _over(img[ya:yb, xa:xb], rgb, np.clip(r - d + 0.5, 0, 1))


def blob(img, centre, radius_fn, angle, size, rgb, vein=None):
    """A filled shape defined in polar form: radius_fn(theta) in 0..1 of size, rotated by angle."""
    h, w = img.shape[:2]
    cx, cy = centre
    xa, xb = int(max(0, cx - size - 2)), int(min(w, cx + size + 2))
    ya, yb = int(max(0, cy - size - 2)), int(min(h, cy + size + 2))
    if xb <= xa or yb <= ya:
        return
    ys, xs = np.mgrid[ya:yb, xa:xb].astype(np.float32) + 0.5
    x, y = xs - cx, ys - cy
    ca, sa = math.cos(-angle), math.sin(-angle)
    lx, ly = x * ca - y * sa, x * sa + y * ca
    th = np.arctan2(ly, lx)
    rr = np.hypot(lx, ly)
    edge = radius_fn(th) * size
    cov = np.clip(edge - rr + 0.7, 0, 1)
    # Light falls off toward the edge a little, giving leaves some shape.
    shade = 0.82 + 0.18 * np.clip(1 - rr / np.maximum(edge, 1e-3), 0, 1)
    region = img[ya:yb, xa:xb]
    _over(region, rgb, cov * 1.0)
    region[..., :3] *= np.where(cov[..., None] > 0, shade[..., None], 1.0)
    if vein is not None:
        stroke(img, (cx, cy), (cx + math.cos(angle) * size * 0.9, cy + math.sin(angle) * size * 0.9), 1.2, 0.4, vein)


# ---------------------------------------------------------------------------------------------
# Needle sprays and branch clusters (conifers)
#
# A spray is a frond: a tapering (lanceolate) foliage body with a needle-serrated edge, side twigs and
# hundreds of short needles, styled per species. The body keeps a frond mostly opaque, as real fir and
# spruce foliage is (the first version drew round-capped "sausages" that left most of each card empty,
# so crowns looked sparse and cartoonish: tree realism review). Albedo only: no baked lighting.

FROND_STYLES = {
    #          side-twig angle, twigs per side, needle length (of frond length), needle angle, needle spacing,
    #          body half-width (of frond length), bottlebrush, tufts, side-twig curl toward the tip
    "fir":     dict(twig_angle=0.95, twigs=10, needle=0.05, needle_angle=1.05, step=0.0055, width=0.34, brush=False, tuft=False, curl=0.25),
    "hemlock": dict(twig_angle=1.05, twigs=12, needle=0.034, needle_angle=1.25, step=0.0045, width=0.32, brush=False, tuft=False, curl=0.1),
    "douglas": dict(twig_angle=0.78, twigs=9, needle=0.055, needle_angle=0.95, step=0.0055, width=0.36, brush=False, tuft=False, curl=0.15),
    "spruce":  dict(twig_angle=0.85, twigs=10, needle=0.05, needle_angle=0.85, step=0.0042, width=0.34, brush=True, tuft=False, curl=0.05),
    "pine":    dict(twig_angle=0.55, twigs=7, needle=0.17, needle_angle=0.35, step=0.012, width=0.30, brush=False, tuft=True, curl=0.0),
    # Task 09 (Crystal Mountain). Optional keys: comb (a needle every other station combed forward over the
    # twig, as on the top of a Pacific silver fir spray), body (the foliage body's opacity and width; 0 draws
    # none, so the spray is see-through and lacy: a faint body would be clipped at the game's 0.4 alpha
    # cutoff anyway), subtwigs (twiglets per side twig: a finely divided, fern-like
    # western hemlock spray), hook (needles curve toward the tip over this fraction of their length: noble
    # fir's upturned "hockey-stick" needles, which crowd the top of the twig when seen from above).
    "silverfir": dict(twig_angle=0.92, twigs=11, needle=0.048, needle_angle=1.0, step=0.0046, width=0.34, brush=False, tuft=False, curl=0.3, comb=0.35),
    "lacy":      dict(twig_angle=1.0, twigs=12, needle=0.03, needle_angle=1.35, step=0.0046, width=0.32, brush=False, tuft=False, curl=0.15, body=0.0, subtwigs=3),
    "noble":     dict(twig_angle=0.65, twigs=12, needle=0.036, needle_angle=0.9, step=0.0036, width=0.3, brush=True, tuft=False, curl=0.1, hook=0.4),
    # Task 09 phase 2 (New England). Tuft keys: tuft_needles (needles per tuft), needle_width (pixels at
    # scale 1) and spread (how far the needles fan out, radians): eastern white pine's long, soft, slender
    # needles in fives make dense, fine brushes that fan forward along the twig. tuft_body: the foliage
    # body's start, length and width (of the frond).
    "whitepine": dict(twig_angle=0.5, twigs=10, needle=0.24, needle_angle=0.3, step=0.012, width=0.30, brush=False, tuft=True, curl=0.0,
                      tuft_needles=64, needle_width=1.8, spread=0.42, tuft_body=(0.1, 0.86, 0.7)),
}


def _body(img, p0, ang, length, half_width, rgb, rng, serration, opacity=0.96):
    """The frond's foliage body: a soft lanceolate shape along ang from p0 (pixels), serrated edge."""
    h, w = img.shape[:2]
    d = np.array([math.cos(ang), math.sin(ang)], np.float32)
    q = np.array([-d[1], d[0]], np.float32)
    p0 = np.asarray(p0, np.float32)
    corners = [p0 + d * length + q * half_width * sgn for sgn in (-1, 1)] + [p0 + q * half_width * sgn for sgn in (-1, 1)]
    xs_all = [c[0] for c in corners]
    ys_all = [c[1] for c in corners]
    xa, xb = int(max(0, min(xs_all) - 2)), int(min(w, max(xs_all) + 3))
    ya, yb = int(max(0, min(ys_all) - 2)), int(min(h, max(ys_all) + 3))
    if xb <= xa or yb <= ya:
        return
    ys, xs = np.mgrid[ya:yb, xa:xb].astype(np.float32) + 0.5
    rx, ry = xs - p0[0], ys - p0[1]
    t = (rx * d[0] + ry * d[1]) / length
    lat = np.abs(rx * q[0] + ry * q[1])
    shape = np.clip(np.sin(np.pi * np.clip(t * 1.02 + 0.03, 0, 1)), 0, 1) ** 0.65
    # An irregular edge (random, not a regular zigzag), inside the needle tips so needles make the outline.
    knots = rng.uniform(0.6, 0.78, serration + 2)
    wobble = np.interp(np.clip(t, 0, 1) * (serration + 1), np.arange(serration + 2), knots)
    edge = half_width * shape * wobble
    cov = np.clip(edge - lat + 0.5, 0, 1) * ((t >= 0) & (t <= 1))
    # A touch darker along the twig (needles overlap there), lighter toward the edge.
    shade = 0.85 + 0.15 * np.clip(lat / np.maximum(edge, 1e-3), 0, 1)
    region = img[ya:yb, xa:xb]
    _over(region, rgb, cov * opacity)
    region[..., :3] *= np.where(cov[..., None] > 0, shade[..., None], 1.0)


def frond(img, p0, ang, length, style, colours, rng, twig_rgb, scale=1.0, sheen=None):
    """One conifer frond from p0 (pixels) along ang (radians), length in pixels. sheen (colours): some of the
    side needles, more toward the edge of the spray, take these paler colours, as a Pacific silver fir's
    spreading needles show their silvery undersides at the spray's edges."""
    s = FROND_STYLES[style]
    greens = [hex_rgb(c) for c in colours]
    base = np.clip(min(greens, key=lambda c: c.sum()) * 0.82, 0, 1)
    half = length * s["width"]
    n_needle = length * s["needle"] / 0.9
    p0 = np.asarray(p0, np.float64)
    d = np.array([math.cos(ang), math.sin(ang)])

    silver = [hex_rgb(c) for c in sheen] if sheen else None

    def needle(pt, na, nl, width, toward=None, palette=None):
        source = palette or greens
        col = np.clip(source[rng.integers(len(source))] * rng.uniform(0.82, 1.2), 0, 1)
        if toward is not None and s.get("hook"):
            # An upturned needle: straight, then curving toward the twig's tip.
            k = 1 - s["hook"]
            mid = (pt[0] + math.cos(na) * nl * k, pt[1] + math.sin(na) * nl * k)
            nb = na + math.atan2(math.sin(toward - na), math.cos(toward - na)) * 0.6
            end = (mid[0] + math.cos(nb) * nl * (1 - k), mid[1] + math.sin(nb) * nl * (1 - k))
            stroke(img, (pt[0], pt[1]), mid, width, width * 0.8, col)
            stroke(img, mid, end, width * 0.8, 1.0, col)
            return
        stroke(img, (pt[0], pt[1]), (pt[0] + math.cos(na) * nl, pt[1] + math.sin(na) * nl), width, 1.0, col)

    if s["tuft"]:
        # Pine: long needles in bundles toward the ends of the twigs, over a small soft body.
        # tuft_body: where the foliage body starts and how long and wide it is. White pine starts it near the
        # branch, where the game's snow pattern (SnowPattern) is heaviest, so its plumes hold snow.
        start, along, wide = s.get("tuft_body", (0.4, 0.58, 0.62))
        _body(img, p0 + d * length * start, ang, length * along, half * wide, base, rng, 7)
        stroke(img, tuple(p0), tuple(p0 + d * length), 3.0 * scale + 0.8, 1.2, twig_rgb)
        for i in range(s["twigs"] * 2):
            t = 0.35 + 0.6 * (i + rng.random() * 0.5) / (s["twigs"] * 2)
            side = -1 if i % 2 else 1
            a = ang + side * s["twig_angle"] * rng.uniform(0.7, 1.2)
            start = p0 + d * length * t
            tip = start + np.array([math.cos(a), math.sin(a)]) * half * 0.8
            stroke(img, tuple(start), tuple(tip), 2.2 * scale, 1.0, twig_rgb)
            for _ in range(s.get("tuft_needles", 34)):
                # White pine's sheen: some needles show their whitish stomatal lines.
                palette = silver if silver and rng.random() < 0.22 else None
                needle(tip, a + rng.normal(0, s.get("spread", 0.6)), n_needle * rng.uniform(0.6, 1.15), s.get("needle_width", 2.0) * scale,
                       palette=palette)
        return

    body = s.get("body", 1.0)
    if body == 1.0:
        _body(img, p0, ang, length, half, base, rng, 9 + s["twigs"])
    elif body > 0:
        _body(img, p0, ang, length, half * (0.55 + 0.45 * body), base, rng, 9 + s["twigs"], opacity=0.96 * body)
    stroke(img, tuple(p0), tuple(p0 + d * length), 2.8 * scale + 0.6, 1.0, twig_rgb)
    spacing = s["step"] * length / 0.9
    # Side twigs, alternating, shorter toward the tip, each carrying needles on both sides.
    for i in range(s["twigs"] * 2):
        t = 0.08 + 0.86 * (i + rng.random() * 0.4) / (s["twigs"] * 2)
        side = -1 if i % 2 else 1
        shape = math.sin(math.pi * min(1.0, t * 1.02 + 0.03)) ** 0.65
        a = ang + side * (s["twig_angle"] - s["curl"] * t) + rng.normal(0, 0.06)
        ln = half * shape / max(0.35, math.sin(s["twig_angle"])) * rng.uniform(0.85, 1.05)
        start = p0 + d * length * t
        dir2 = np.array([math.cos(a), math.sin(a)])
        stroke(img, tuple(start), tuple(start + dir2 * ln), 1.6 * scale + 0.4, 0.8, twig_rgb)
        steps = max(3, int(ln / spacing))
        for k in range(steps):
            u = (k + rng.random() * 0.5) / steps
            pt = start + dir2 * ln * u
            taper = 1 - 0.45 * u
            sides = (-1, 1) if not s["brush"] else (-1, 1, -1, 1)
            for j, sd in enumerate(sides):
                na = a + sd * (s["needle_angle"] + rng.normal(0, 0.14)) + (j // 2) * sd * 0.55
                nl = n_needle * taper * rng.uniform(0.75, 1.15) * (0.7 if j >= 2 else 1.0)
                palette = silver if silver and rng.random() < 0.03 + 0.17 * u else None
                needle(pt, na, nl, 2.4 * scale if style not in ("hemlock", "lacy") else 1.9 * scale, toward=a, palette=palette)
            if s.get("comb") and k % 2 == 0:
                # Needles on the top of the twig, combed forward so they hide it.
                needle(pt, a + rng.normal(0, 0.25), n_needle * s["comb"] * 2 * taper * rng.uniform(0.8, 1.1), 2.2 * scale)
        for m in range(s.get("subtwigs", 0)):
            # Twiglets off the side twig, each with its own short needles: a lacy, fern-like spray.
            u = (m + 0.6 + rng.random() * 0.3) / (s["subtwigs"] + 0.8)
            sub = start + dir2 * ln * u
            sa = a + side * rng.uniform(0.6, 0.9)
            sl = ln * (1 - u) * rng.uniform(0.45, 0.6)
            sdir = np.array([math.cos(sa), math.sin(sa)])
            stroke(img, tuple(sub), tuple(sub + sdir * sl), 1.1 * scale + 0.3, 0.7, twig_rgb)
            count = max(2, int(sl / spacing))
            for q in range(count):
                pt = sub + sdir * sl * (q + rng.random() * 0.5) / count
                for sd in (-1, 1):
                    needle(pt, sa + sd * (s["needle_angle"] + rng.normal(0, 0.14)), n_needle * 0.8 * rng.uniform(0.75, 1.1), 1.7 * scale)
    # Needles on the main axis too.
    count = max(1, int(length / spacing * 0.6))
    for k in range(count):
        u = (k + rng.random()) / count
        pt = p0 + d * length * u
        for sd in (-1, 1):
            needle(pt, ang + sd * (s["needle_angle"] * 0.8 + rng.normal(0, 0.12)), n_needle * (1 - 0.4 * u) * rng.uniform(0.7, 1.0), 2.2 * scale)


def needle_spray(style, colours, seed, n=512, sheen=None):
    """A spray card: one frond along u (u = 0 at the branch, u = 1 at the tip), filling the card across v.
    The whole card is one frond: the game's snow pattern (SnowPattern) expects the branch line at v = 0.5."""
    rng = np.random.default_rng(seed)
    img = canvas(n)
    frond(img, (0.02 * n, 0.5 * n), rng.uniform(-0.04, 0.04), 0.95 * n, style, colours, rng, hex_rgb("#4A3A2C"), sheen=sheen)
    return img


def branch_cluster(style, colours, seed, n=512, sheen=None):
    """A whole branch on one card (mid-distance LODs): fronds either side of a branch along u, shorter
    toward the tip, plus a tip frond, so one card carries a branch's worth of foliage."""
    rng = np.random.default_rng(seed + 7)
    img = canvas(n)
    twig = hex_rgb("#4A3A2C")
    axis_y = 0.5 * n
    count = 7
    for i in range(count):
        t = 0.06 + 0.8 * (i + rng.random() * 0.3) / count
        for side in (-1, 1):
            ln = (0.5 - 0.3 * t) * n * rng.uniform(0.85, 1.05)
            frond(img, (t * n, axis_y), side * rng.uniform(0.55, 0.8), ln, style, colours, rng, twig, scale=0.8, sheen=sheen)
    frond(img, (0.55 * n, axis_y), rng.uniform(-0.05, 0.05), 0.43 * n, style, colours, rng, twig, scale=0.8, sheen=sheen)
    stroke(img, (0.0, axis_y), (0.97 * n, axis_y), 5.0, 1.6, twig)
    return img


# ---------------------------------------------------------------------------------------------
# Leaves and twigs (deciduous)

def _ellipse(th, aspect):
    """An ellipse's radius at angle th from its long axis (1 along it, aspect across it)."""
    return 1.0 / np.sqrt(np.cos(th) ** 2 + (np.sin(th) / aspect) ** 2)


def _leaf_shape(kind):
    if kind == "maple":
        return lambda th: (0.42 + 0.58 * np.abs(np.cos(2.5 * th)) ** 0.7) * (0.75 + 0.25 * np.cos(th)) * (1 + 0.05 * np.sin(22 * th))
    if kind == "birch":
        return lambda th: (0.55 + 0.45 * np.cos(th)) ** 0.9 * (1 + 0.06 * np.sin(30 * th)) * np.where(np.abs(th) > 2.6, 0.7, 1.0)
    if kind == "beech":
        return lambda th: (0.5 + 0.5 * np.cos(th) ** 2) ** 0.8 * (1 + 0.04 * np.sin(18 * th))
    if kind == "aspen":
        return lambda th: 0.8 + 0.2 * np.cos(th) + 0.04 * np.sin(24 * th)
    if kind == "oak":
        # Northern red oak: an elongated blade (an ellipse, under half as wide as long) with pointed lobes along
        # each side and rounded sinuses a third of the way to the midrib (|cos 5 theta| is 1 on a lobe, 0 in a
        # sinus), narrowing to a wedge at the stalk.
        return lambda th: (_ellipse(th, 0.45) * (0.64 + 0.36 * np.abs(np.cos(5 * th)) ** 1.2)
                           * np.where(np.abs(th) > 2.6, 0.6, 1.0))
    if kind == "cherry":
        # Black cherry: narrow and lance-shaped (a third as wide as long), finely toothed, with a drawn-out tip.
        return lambda th: (_ellipse(th, 0.32) * (1 + 0.22 * np.clip(np.cos(th), 0, 1) ** 6)
                           * (1 + 0.035 * np.sin(44 * th)))
    raise ValueError(kind)


def twig_card(seed, n=512, bark="#6B5B4E", dense=False):
    """A bare winter twig silhouette: fine forking twigs spreading toward u = 1. dense (task 09 phase 2 audit:
    winter hardwood crowns read as bare skeletons): more, finer twigs that fork deeper and end in a fringe of
    twiglets, so a winter crown reads as the fine-twig haze of a real leafless hardwood."""
    rng = np.random.default_rng(seed)
    img = canvas(n)
    col = hex_rgb(bark)
    if dense:
        # Muted halfway toward a cool grey-brown: thousands of fine twigs read as a soft grey haze, not the twig's
        # full colour (at 30%, a stand read as late-autumn red-brown).
        col = col * 0.5 + hex_rgb("#6C6865") * 0.5
    branch, stop = (0.95, 0.022) if dense else (0.92, 0.03)

    def grow(x, y, a, length, width, depth):
        x1, y1 = x + math.cos(a) * length, y + math.sin(a) * length
        stroke(img, (x * n, y * n), (x1 * n, y1 * n), width, width * 0.7, np.clip(col * rng.uniform(0.85, 1.1), 0, 1))
        if depth == 0 or length < stop:
            if dense:
                # A fringe of short twiglets at each tip: the fine haze at the edge of a winter crown.
                for _ in range(2):
                    b = a + rng.uniform(-0.9, 0.9)
                    tl = length * rng.uniform(0.5, 0.9)
                    stroke(img, (x1 * n, y1 * n), ((x1 + math.cos(b) * tl) * n, (y1 + math.sin(b) * tl) * n), 1.0, 0.8,
                           np.clip(col * rng.uniform(0.95, 1.2), 0, 1))
            return
        for side in (-1, 1):
            if rng.random() < branch:
                grow(x1, y1, a + side * rng.uniform(0.2, 0.55), length * rng.uniform(0.62, 0.8), max(1.0, width * 0.72), depth - 1)

    if dense:
        for k in range(7):
            grow(0.0, 0.5 + rng.uniform(-0.12, 0.12), rng.uniform(-0.55, 0.55), rng.uniform(0.22, 0.32), 4.5, 8)
        return img
    for k in range(4):
        grow(0.0, 0.5 + rng.uniform(-0.05, 0.05), rng.uniform(-0.5, 0.5), rng.uniform(0.2, 0.3), 4.0, 7)
    return img


def leaf_card(kind, colours, seed, n=512, bark="#6B5B4E", count=16):
    """A leafy twig: leaves of one species along a short twig, as seen from above."""
    rng = np.random.default_rng(seed)
    img = canvas(n)
    stroke(img, (0, n * 0.5), (n * 0.9, n * 0.5), 5, 2, hex_rgb(bark))
    shape = _leaf_shape(kind)
    size = {"maple": 0.13, "birch": 0.09, "beech": 0.09, "aspen": 0.085, "oak": 0.135, "cherry": 0.095}[kind] * n
    for i in range(count):
        t = 0.1 + 0.62 * (i + rng.random() * 0.5) / count
        side = -1 if i % 2 else 1
        bx, by = 0.9 * t * n, n * 0.5
        a = side * rng.uniform(0.5, 1.2) + rng.normal(0, 0.15)
        stem = size * 0.5
        cx, cy = bx + math.cos(a) * (stem + size), by + math.sin(a) * (stem + size)
        stroke(img, (bx, by), (bx + math.cos(a) * stem, by + math.sin(a) * stem), 1.8, 1.2, hex_rgb(bark))
        col = np.clip(hex_rgb(colours[rng.integers(len(colours))]) * rng.uniform(0.85, 1.15), 0, 1)
        vein = np.clip(col * 1.25, 0, 1)
        blob(img, (cx, cy), shape, a, size * rng.uniform(0.85, 1.1), col, vein)
    return img


# ---------------------------------------------------------------------------------------------
# Bark (tileable)

def _noise(n, cells_x, cells_y, rng):
    """Tileable value noise: a random grid, wrapped, smoothly interpolated to n×n."""
    g = rng.random((cells_y, cells_x)).astype(np.float32)
    ys = np.linspace(0, cells_y, n, endpoint=False)
    xs = np.linspace(0, cells_x, n, endpoint=False)
    y0, x0 = np.floor(ys).astype(int), np.floor(xs).astype(int)
    fy, fx = ys - y0, xs - x0
    fy, fx = fy * fy * (3 - 2 * fy), fx * fx * (3 - 2 * fx)
    y1, x1 = (y0 + 1) % cells_y, (x0 + 1) % cells_x
    a = g[y0][:, x0] * (1 - fx) + g[y0][:, x1] * fx
    b = g[y1][:, x0] * (1 - fx) + g[y1][:, x1] * fx
    return a * (1 - fy)[:, None] + b * fy[:, None]


def _fbm(n, cx, cy, rng, octaves=4):
    out = np.zeros((n, n), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        out += _noise(n, cx * 2 ** o, cy * 2 ** o, rng) * amp
        total += amp
        amp *= 0.5
    return out / total


def _warp(f, dx, dy):
    """f (tileable) sampled at pixel offsets dx, dy (arrays), bilinear and wrapped."""
    n = f.shape[0]
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
    x, y = (xs + dx) % n, (ys + dy) % n
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    x1, y1 = (x0 + 1) % n, (y0 + 1) % n
    return (f[y0, x0] * (1 - fx) + f[y0, x1] * fx) * (1 - fy) + (f[y1, x0] * (1 - fx) + f[y1, x1] * fx) * fy


def _blur(f, passes=1):
    """A small tileable blur (1-2-1 per axis, repeated)."""
    for _ in range(passes):
        f = (np.roll(f, 1, 0) + 2 * f + np.roll(f, -1, 0)) / 4
        f = (np.roll(f, 1, 1) + 2 * f + np.roll(f, -1, 1)) / 4
    return f


def _cells(n, cx, cy, rng, jitter=0.85, warp=0.0):
    """Tileable Worley noise on cx × cy jittered points, in pixels: nearest and second-nearest distance,
    the nearest cell's id and the offset from its point (rows grow down the texture). warp (pixels)
    bends the cell walls so they aren't straight-edged polygons."""
    jx = (rng.random((cy, cx)) - 0.5) * jitter
    jy = (rng.random((cy, cx)) - 0.5) * jitter
    sx, sy = n / cx, n / cy
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32) + 0.5
    if warp:
        xs = xs + (_fbm(n, 6, 6, rng, 3) - 0.5) * 2 * warp
        ys = ys + (_fbm(n, 6, 6, rng, 3) - 0.5) * 2 * warp
    gx, gy = np.floor(xs / sx).astype(int), np.floor(ys / sy).astype(int)
    f1 = np.full((n, n), np.inf, np.float32)
    f2 = f1.copy()
    ids = np.zeros((n, n), np.int64)
    ox_near, oy_near = np.zeros((n, n), np.float32), np.zeros((n, n), np.float32)
    rx = 1 + int(math.ceil(sy / sx)) if sy > sx else 1
    ry = 1 + int(math.ceil(sx / sy)) if sx > sy else 1
    for oy in range(-ry, ry + 1):
        for ox in range(-rx, rx + 1):
            cxi, cyi = gx + ox, gy + oy
            wx, wy = cxi % cx, cyi % cy
            dx = xs - (cxi + 0.5 + jx[wy, wx]) * sx
            dy = ys - (cyi + 0.5 + jy[wy, wx]) * sy
            d = np.hypot(dx, dy)
            closer = d < f1
            f2 = np.where(closer, f1, np.minimum(f2, d))
            ids = np.where(closer, wy * cx + wx, ids)
            ox_near, oy_near = np.where(closer, dx, ox_near), np.where(closer, dy, oy_near)
            f1 = np.where(closer, d, f1)
    return f1, f2, ids, ox_near, oy_near


def _bands(t):
    """A triangle wave: 0 where t is a whole number (a furrow), 1 midway between (a ridge crest)."""
    return 1 - np.abs(2 * (t - np.floor(t)) - 1)


def _ridge_field(n, ridges, meander, rng, cx=4):
    """Furrow lines running up the trunk: `ridges` around it (a whole number, so the texture wraps),
    displaced sideways by noise stretched up the trunk. Where the noise bends faster than the ridges
    are spaced, neighbouring lines split and rejoin into long lens-shaped plates, as Douglas-fir and
    hemlock bark does (contours of plain noise close into loops, which reads as wood grain)."""
    xs = (np.arange(n, dtype=np.float32) + 0.5) / n
    return xs[None, :] * ridges + (_fbm(n, cx, 1, rng, 5) - 0.5) * 2 * meander


def _lerp3(a, b, t):
    return a + (b - a) * t[..., None]


def bark(style, base, seed, n=512):
    """Tileable bark (u wraps around the trunk, v runs up it): (albedo RGBA, height 0..1, normal strength).

    Real bark reads through relief: ridges and plates lit on top, furrows and gaps in shadow. Height
    drives both the albedo (a baked cavity term: ambient light doesn't reach into furrows) and the normal
    map (bark_normal), so the sun rakes across ridges at runtime. Styles (tree realism review; the first
    version was blurred colour noise that read as flat painted pipes):
      douglas      thick rounded ridges that split and rejoin, deep cinnamon furrows (Douglas-fir)
      furrowed     narrower flat-topped ridges broken into long plates (mountain hemlock, western hemlock,
                   sugar maple)
      plated       grey plates over reddish-brown furrows: a fir's smooth grey bark turning plated with
                   age (noble fir)
      scaly        small thin overlapping scales, lower edges proud (Engelmann spruce, lodgepole pine)
      fir          smooth grey with resin blisters and lichen (subalpine fir, Pacific silver fir, krummholz)
      beech        smooth grey with faint mottling and lichen (beech, red maple)
      birch        chalk white, dark lenticels, peeling strips, black scars (paper birch)
      aspen        cream-green, dark diamond branch scars (quaking aspen)
      yellowbirch  bronze with thin horizontal curls (yellow birch)
      oak          long, flat-topped, smooth-topped ridges ("ski tracks") over shallow dark furrows
                   (northern red oak)
      cherry       small, dark, thick plates curling up at their edges, reddish inner bark between them
                   (black cherry)
    """
    rng = np.random.default_rng(seed)
    c = hex_rgb(base)
    detail = _fbm(n, 64, 32, rng, 3)
    grain = _noise(n, 128, 16, rng)   # fine vertical fibres
    lichen_rgb = np.array([0.66, 0.69, 0.62], np.float32)

    if style == "plated":
        # Shallow furrows cut the grey bark into short, flat plates, showing the reddish-brown inner bark.
        # Grey on top, so the trunk still reads as a fir's.
        tri = _bands(_ridge_field(n, 11, 2.6, rng, cx=4))
        ridge = np.clip((tri - 0.2 - 0.2 * _fbm(n, 6, 3, rng, 3)) / 0.25, 0, 1)
        ridge = ridge * ridge * (3 - 2 * ridge)
        cross = _bands(_fbm(n, 3, 9, rng, 4) * 2.4)
        breaks = np.clip(1 - cross / 0.22, 0, 1) ** 1.2 * np.clip((_noise(n, 20, 14, rng) - 0.4) * 5, 0, 1)
        ridge = ridge * (1 - 0.85 * breaks)
        h = ridge * (0.8 + 0.2 * _fbm(n, 20, 10, rng, 3)) + 0.04 * grain
        h = _blur(h, 1)
        furrow = c * np.array([0.88, 0.62, 0.5], np.float32)
        wall = c * np.array([0.95, 0.8, 0.7], np.float32)
        rgb = _lerp3(_lerp3(furrow, wall, np.clip(h / 0.45, 0, 1)), c, np.clip((h - 0.45) / 0.35, 0, 1))
        # Plates weather unevenly: some greyer, some warmer.
        tone = _fbm(n, 8, 12, rng, 3)
        warm = np.clip((tone - 0.55) * 3, 0, 1) * np.clip(h * 2 - 0.6, 0, 1) * 0.6
        rgb = _lerp3(rgb, np.clip(c * np.array([1.05, 0.92, 0.84], np.float32), 0, 1), warm)
        lichen = np.clip((_fbm(n, 5, 6, rng, 4) - 0.62) * 5, 0, 1) * np.clip((h - 0.6) * 3, 0, 1)
        rgb = _lerp3(rgb, lichen_rgb * (0.85 + 0.15 * detail[..., None]), lichen * 0.35)
        rgb *= (0.9 + 0.2 * detail)[..., None]
        strength = 9.0
    elif style in ("douglas", "furrowed"):
        thick = style == "douglas"
        tri = _bands(_ridge_field(n, 12 if thick else 18, 3.2 if thick else 3.6, rng, cx=5))
        # Ridges vary in width along their length and from one to the next.
        width = 0.12 + 0.22 * _fbm(n, 6, 3, rng, 3)
        ridge = np.clip((tri - width) / (0.55 if thick else 0.3), 0, 1)
        ridge = 1 - (1 - ridge) ** 2 if thick else ridge * ridge * (3 - 2 * ridge)   # rounded, or flat plates
        if not thick:
            # Short V-notched breaks across the ridges, so they read as long plates (hemlock, maple).
            cross = _bands(_fbm(n, 3, 12, rng, 4) * 2.0)
            breaks = np.clip(1 - cross / 0.3, 0, 1) ** 1.5 * np.clip((_noise(n, 24, 16, rng) - 0.55) * 6, 0, 1)
            ridge = ridge * (1 - 0.75 * breaks)
        # Corky, fibrous ridge surfaces.
        cork = _fbm(n, 24, 8, rng, 4)
        h = ridge * (0.72 + 0.28 * cork) + 0.05 * grain
        h = _blur(h, 1)
        top = c
        wall = c * np.array([0.84, 0.7, 0.62] if thick else [0.82, 0.68, 0.62], np.float32)
        bottom = c * np.array([0.4, 0.3, 0.26] if thick else [0.4, 0.32, 0.29], np.float32)
        rgb = _lerp3(_lerp3(bottom, wall, np.clip(h / 0.4, 0, 1)), top, np.clip((h - 0.4) / 0.45, 0, 1))
        lichen = np.clip((_fbm(n, 5, 5, rng, 4) - 0.6) * 5, 0, 1) * np.clip((h - 0.6) * 3, 0, 1)
        rgb = _lerp3(rgb, lichen_rgb * (0.8 + 0.2 * detail[..., None]), lichen * 0.3)
        rgb *= (0.88 + 0.24 * detail)[..., None]
        strength = 16.0 if thick else 10.0
    elif style == "scaly":
        f1, f2, ids, _, oy = _cells(n, 30, 26, rng, 0.9, warp=5.0)
        sy = n / 26
        per = np.random.default_rng(seed + 3).random(30 * 26).astype(np.float32)[ids]
        # Scales overlap like shingles: each one's lower, free edge stands proud of the scale below and
        # shades its top.
        lift = np.clip(0.5 + 0.5 * oy / (0.55 * sy), 0, 1)
        edge = np.clip((f2 - f1) / 2.5, 0, 1)
        h = edge * (0.35 + 0.5 * lift + 0.15 * per) + 0.06 * detail
        h = _blur(h, 1)
        rgb = c * (0.9 + 0.2 * per)[..., None] * (0.78 + 0.22 * lift)[..., None]
        fresh = np.clip((per - 0.93) * 20, 0, 1)            # a few freshly shed scales: warmer inner bark
        rgb = _lerp3(rgb, np.clip(c * np.array([1.12, 0.96, 0.84], np.float32), 0, 1), fresh * 0.8)
        lichen = np.clip((_fbm(n, 4, 4, rng, 4) - 0.62) * 5, 0, 1)
        rgb = _lerp3(rgb, lichen_rgb * 0.9, lichen * 0.25)
        rgb *= (0.9 + 0.2 * detail)[..., None]
        strength = 7.0
    elif style in ("fir", "beech"):
        mottle = _fbm(n, 4, 6, rng, 5)
        h = 0.5 + 0.1 * (mottle - 0.5) + 0.04 * detail
        rgb = c * (0.82 + 0.34 * mottle)[..., None]
        # Rain streaks down the trunk and horizontal lenticels (short raised dashes).
        rgb *= (0.94 + 0.12 * _noise(n, 40, 3, rng))[..., None]
        dash = np.clip((_noise(n, 12, 110, rng) - 0.8) * 8, 0, 1) * np.clip((_noise(n, 48, 14, rng) - 0.45) * 4, 0, 1)
        h += 0.12 * dash
        rgb *= (1 - 0.35 * dash)[..., None]
        if style == "fir":
            # Resin blisters: scattered round bumps, a little lighter on top.
            f1, _, ids, _, oy = _cells(n, 10, 14, rng, 0.9)
            keep = np.random.default_rng(seed + 5).random(10 * 14)[ids] < 0.5
            size = 5.0 + 5.0 * np.random.default_rng(seed + 6).random(10 * 14)[ids]
            blister = np.clip(1 - f1 / size, 0, 1) ** 1.2 * keep
            h += 0.35 * blister
            rgb *= (1 + 0.1 * blister)[..., None]
        # Crustose lichen: pale, irregular patches.
        lichen = np.clip((_fbm(n, 6, 7, rng, 5) - 0.6) * 6, 0, 1)
        rgb = _lerp3(rgb, lichen_rgb * (0.95 + 0.15 * detail[..., None]), lichen * (0.55 if style == "fir" else 0.4))
        dark = np.clip((_fbm(n, 5, 5, rng, 4) - 0.64) * 6, 0, 1)
        rgb *= (1 - 0.22 * dark)[..., None]
        h = _blur(h, 1)
        strength = 5.0
    elif style == "birch":
        rgb = c * (0.93 + 0.07 * _fbm(n, 6, 20, rng))[..., None]
        # Peeling strips: horizontal bands with ragged edges, a step in height and a warmer layer.
        peel = np.clip((_fbm(n, 3, 26, rng, 4) - 0.6) * 12, 0, 1)
        h = 0.5 + 0.12 * peel
        rgb = _lerp3(rgb, np.clip(c * np.array([0.95, 0.86, 0.8], np.float32), 0, 1), peel * 0.6)
        dash = np.clip((_noise(n, 9, 80, rng) - 0.78) * 7, 0, 1) * np.clip((_noise(n, 30, 12, rng) - 0.35) * 4, 0, 1)
        h -= 0.12 * dash
        rgb *= (1 - 0.75 * dash)[..., None]
        # Black rough chevrons below branch stubs: wide, short and sharp-edged.
        f1, _, ids, ox, oy = _cells(n, 4, 6, rng, 0.8, warp=9.0)
        keep = np.random.default_rng(seed + 11).random(4 * 6)[ids] < 0.4
        size = 0.6 + 0.7 * np.random.default_rng(seed + 12).random(4 * 6)[ids]
        v = (np.abs(ox) / 34.0 + np.abs(oy + 0.35 * np.abs(ox)) / 9.0) / size + 1.1 * (_fbm(n, 20, 20, rng, 3) - 0.5)
        scar = np.clip((1 - v) * 4, 0, 1) * keep
        h -= 0.15 * scar * (0.5 + 0.5 * grain)
        rgb *= (1 - 0.85 * scar)[..., None]
        h = _blur(h, 1)
        strength = 4.0
    elif style == "aspen":
        mottle = _fbm(n, 5, 5, rng, 4)
        rgb = c * (0.9 + 0.12 * mottle)[..., None]
        rgb = _lerp3(rgb, np.clip(c * np.array([0.9, 0.97, 0.86], np.float32), 0, 1), np.clip((mottle - 0.5) * 3, 0, 1))
        h = 0.5 + 0.04 * detail
        # Branch-scar "eyes": dark diamonds, much wider than tall, sunken inside a raised rim.
        f1, _, ids, ox, oy = _cells(n, 4, 6, rng, 0.8, warp=8.0)
        keep = np.random.default_rng(seed + 9).random(4 * 6)[ids] < 0.55
        size = 0.6 + 0.8 * np.random.default_rng(seed + 10).random(4 * 6)[ids]
        v = (np.abs(ox) / 30.0 + np.abs(oy) / 10.0) / size + 1.2 * (_fbm(n, 20, 20, rng, 3) - 0.5)
        eye = np.clip((1 - v) * 5, 0, 1) * keep
        rim = np.clip(1 - np.abs(v - 1.15) * 5, 0, 1) * keep
        h += 0.15 * rim - 0.2 * eye
        rgb *= (1 - 0.85 * eye)[..., None]
        dash = np.clip((_noise(n, 8, 90, rng) - 0.84) * 8, 0, 1) * np.clip((_noise(n, 30, 10, rng) - 0.4) * 3, 0, 1)
        rgb *= (1 - 0.55 * dash)[..., None]
        h -= 0.06 * dash
        h = _blur(h, 1)
        strength = 4.0
    elif style == "yellowbirch":
        f = _fbm(n, 4, 30, rng)
        rgb = c * (0.85 + 0.2 * f)[..., None]
        # Thin papery curls: horizontal strips that lift at their lower edge and catch the light.
        f1, f2, ids, _, oy = _cells(n, 7, 40, rng, 0.9, warp=3.0)
        keep = np.random.default_rng(seed + 13).random(7 * 40)[ids] < 0.45
        sy = n / 40
        lift = np.clip(0.5 + 0.5 * oy / (0.5 * sy), 0, 1) * keep * np.clip((f2 - f1) / 2.0, 0, 1)
        h = 0.45 + 0.35 * lift + 0.05 * detail
        rgb = rgb * (1 - 0.2 * keep)[..., None] + np.clip(c * 1.2, 0, 1) * (0.3 * lift)[..., None]
        h = _blur(h, 1)
        strength = 6.0
    elif style == "oak":
        # Long, nearly straight ridges with flat, smooth, paler tops (the "ski tracks"), steep walls and
        # shallow dark furrows; a few long breaks split the ridges into very long plates.
        tri = _bands(_ridge_field(n, 13, 3.0, rng, cx=5))
        width = 0.15 + 0.3 * _fbm(n, 6, 3, rng, 3)          # ridges vary in width along and between them
        ridge = np.clip((tri - width) / 0.22, 0, 1)
        ridge = ridge * ridge * (3 - 2 * ridge)
        cross = _bands(_fbm(n, 3, 9, rng, 4) * 2.0)
        breaks = np.clip(1 - cross / 0.18, 0, 1) * np.clip((_noise(n, 16, 10, rng) - 0.5) * 5, 0, 1)
        ridge = ridge * (1 - 0.85 * breaks)
        h = ridge * (0.88 + 0.12 * _fbm(n, 28, 6, rng, 3)) + 0.04 * grain
        h = _blur(h, 1)
        furrow = c * np.array([0.6, 0.54, 0.5], np.float32)
        wall = c * np.array([0.8, 0.75, 0.7], np.float32)
        top = np.clip(c * np.array([1.06, 1.06, 1.05], np.float32), 0, 1)
        rgb = _lerp3(_lerp3(furrow, wall, np.clip(h / 0.4, 0, 1)), top, np.clip((h - 0.55) / 0.3, 0, 1))
        # Green algae and lichen in the furrows' lee.
        algae = np.clip((_fbm(n, 4, 6, rng, 4) - 0.55) * 4, 0, 1) * np.clip(1 - h * 2, 0, 1)
        rgb = _lerp3(rgb, np.array([0.36, 0.42, 0.3], np.float32), algae * 0.35)
        rgb *= (0.92 + 0.16 * detail)[..., None]
        strength = 9.0
    elif style == "cherry":
        # Small, dark, thick plates whose side edges curl outward ("burnt cornflakes"), catching the light,
        # with the reddish-brown inner bark showing in the gaps between them.
        cols, rows = 18, 26
        f1, f2, ids, ox, _ = _cells(n, cols, rows, rng, 0.9, warp=4.0)
        per = np.random.default_rng(seed + 3).random(cols * rows).astype(np.float32)[ids]
        edge = np.clip((f2 - f1) / 3.0, 0, 1)
        curl = np.clip(np.abs(ox) / (0.5 * n / cols), 0, 1) ** 2
        h = edge ** 0.6 * (0.45 + 0.4 * curl + 0.15 * per) + 0.05 * detail
        h = _blur(h, 1)
        gap = 1 - np.clip(edge * 4, 0, 1)
        inner = np.clip(c * np.array([1.9, 1.25, 1.0], np.float32), 0, 1)
        rgb = c * (0.85 + 0.3 * per)[..., None] * (0.8 + 0.35 * curl)[..., None]
        rgb = _lerp3(rgb, inner, gap * 0.75)
        rgb *= (0.92 + 0.16 * detail)[..., None]
        strength = 11.0
    else:
        raise ValueError(style)

    # Baked cavity: furrows and gaps get little ambient light.
    rgb *= (0.6 + 0.4 * np.clip(h, 0, 1))[..., None]
    img = np.ones((n, n, 4), np.float32)
    img[..., :3] = np.clip(rgb, 0, 1)
    return img, np.clip(h, 0, 1), strength


def bark_normal(height, strength):
    """A tangent-space normal map (u across, v up; +G points up the trunk) from a tileable height field."""
    gu = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * 0.5
    grow = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * 0.5   # rows grow downward, v upward
    nrm = np.stack([-gu * strength, grow * strength, np.ones_like(height)], -1)
    nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
    img = np.ones(height.shape + (4,), np.float32)
    img[..., :3] = nrm * 0.5 + 0.5
    return img
