"""Procedural tree textures (numpy only; runs inside Blender or plain Python).

Every texture is generated from a seed, so the library stays repeatable and free (TR2):
  foliage cards  needle sprays (fir, spruce, hemlock, douglas, pine), leaf clusters (maple, birch,
                 beech, aspen) in summer and autumn colours, bare twig silhouettes for winter
  bark           furrowed, scaly, birch, aspen, beech and yellow-birch styles (tileable)

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
# Needle sprays

NEEDLE_STYLES = {
    #          needle len, spacing, angle (rad from twig), side twigs, bottlebrush, tuft
    "fir":     dict(length=0.08, step=0.005, angle=1.25, twigs=10, brush=False, tuft=False),
    "hemlock": dict(length=0.06, step=0.0045, angle=1.35, twigs=13, brush=False, tuft=False),
    "douglas": dict(length=0.085, step=0.006, angle=1.05, twigs=10, brush=False, tuft=False),
    "spruce":  dict(length=0.07, step=0.004, angle=0.9, twigs=11, brush=True, tuft=False),
    "pine":    dict(length=0.17, step=0.012, angle=0.35, twigs=6, brush=False, tuft=True),
}


def needle_spray(style, colours, seed, n=512):
    """A branch spray: twig along u (x), needles either side (or all round, or in tufts)."""
    rng = np.random.default_rng(seed)
    s = NEEDLE_STYLES[style]
    img = canvas(n)
    twig = hex_rgb("#4A3A2C")
    greens = [hex_rgb(c) for c in colours]

    dark = np.clip(min(greens, key=lambda c: c.sum()) * 0.72, 0, 1)

    def twig_with_needles(x0, y0, x1, y1, length_scale):
        # A solid foliage mass under the needles, so a spray reads as a clump from a distance.
        mass = s["length"] * length_scale * n * (2.4 if not s["tuft"] else 1.6)
        if s["tuft"]:
            stroke(img, ((x0 + (x1 - x0) * 0.62) * n, (y0 + (y1 - y0) * 0.62) * n), (x1 * n, y1 * n), mass, mass * 0.7, dark)
        else:
            stroke(img, (x0 * n, y0 * n), (x1 * n, y1 * n), mass * 0.9, mass * 0.45, dark)
        stroke(img, (x0 * n, y0 * n), (x1 * n, y1 * n), 3.2 * length_scale + 0.8, 1.0, twig)
        if s["tuft"]:  # pine: a dense burst of long needles around the last third of each twig
            ang = math.atan2(y1 - y0, x1 - x0)
            for i in range(70):
                t = rng.uniform(0.55, 1.0)
                px, py = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
                a = ang + rng.normal(0, 0.75)
                ln = s["length"] * length_scale * rng.uniform(0.6, 1.1)
                col = np.clip(greens[rng.integers(len(greens))] * rng.uniform(0.85, 1.2), 0, 1)
                stroke(img, (px * n, py * n), ((px + math.cos(a) * ln) * n, (py + math.sin(a) * ln) * n), 2.6, 1.1, col)
            return
        seg = math.hypot(x1 - x0, y1 - y0)
        steps = max(2, int(seg / s["step"]))
        ang0 = math.atan2(y1 - y0, x1 - x0)
        for i in range(steps):
            t = (i + rng.random() * 0.5) / steps
            px, py = x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
            taper = (1 - 0.55 * t) * length_scale
            if s["tuft"] and t < 0.72:
                continue  # pines keep their needles in tufts toward the tips
            sides = (-1, 1) if not s["brush"] else (-1, 1, -1, 1)
            for k, side in enumerate(sides):
                a = ang0 + side * (s["angle"] + rng.normal(0, 0.12)) + (k // 2) * side * 0.5
                ln = s["length"] * taper * rng.uniform(0.8, 1.15)
                col = greens[rng.integers(len(greens))] * rng.uniform(0.85, 1.15)
                col = np.clip(col, 0, 1)
                stroke(img, (px * n, py * n), ((px + math.cos(a) * ln) * n, (py + math.sin(a) * ln) * n),
                       3.0 if style != "hemlock" else 2.4, 1.2, col)

    # Main twig from the branch end (u = 0) to the tip, curving slightly.
    bend = rng.uniform(-0.05, 0.05)
    twig_with_needles(0.02, 0.5, 0.96, 0.5 + bend, 1.0)
    for i in range(s["twigs"]):
        t = 0.12 + 0.8 * (i + rng.random() * 0.4) / s["twigs"]
        side = -1 if i % 2 else 1
        bx, by = 0.02 + 0.94 * t, 0.5 + bend * t
        ln = (0.42 - 0.3 * t) * rng.uniform(0.8, 1.1)
        a = side * rng.uniform(0.55, 0.85)
        twig_with_needles(bx, by, bx + math.cos(a) * ln, by + math.sin(a) * ln, 0.8 - 0.3 * t)
    if s["tuft"]:
        # Extra needle bursts at the tips.
        pass
    return img


# ---------------------------------------------------------------------------------------------
# Leaves and twigs (deciduous)

def _leaf_shape(kind):
    if kind == "maple":
        return lambda th: (0.42 + 0.58 * np.abs(np.cos(2.5 * th)) ** 0.7) * (0.75 + 0.25 * np.cos(th)) * (1 + 0.05 * np.sin(22 * th))
    if kind == "birch":
        return lambda th: (0.55 + 0.45 * np.cos(th)) ** 0.9 * (1 + 0.06 * np.sin(30 * th)) * np.where(np.abs(th) > 2.6, 0.7, 1.0)
    if kind == "beech":
        return lambda th: (0.5 + 0.5 * np.cos(th) ** 2) ** 0.8 * (1 + 0.04 * np.sin(18 * th))
    if kind == "aspen":
        return lambda th: 0.8 + 0.2 * np.cos(th) + 0.04 * np.sin(24 * th)
    raise ValueError(kind)


def twig_card(seed, n=512, bark="#6B5B4E"):
    """A bare winter twig silhouette: fine forking twigs spreading toward u = 1."""
    rng = np.random.default_rng(seed)
    img = canvas(n)
    col = hex_rgb(bark)

    def grow(x, y, a, length, width, depth):
        x1, y1 = x + math.cos(a) * length, y + math.sin(a) * length
        stroke(img, (x * n, y * n), (x1 * n, y1 * n), width, width * 0.7, np.clip(col * rng.uniform(0.85, 1.1), 0, 1))
        if depth == 0 or length < 0.03:
            return
        for side in (-1, 1):
            if rng.random() < 0.92:
                grow(x1, y1, a + side * rng.uniform(0.2, 0.55), length * rng.uniform(0.62, 0.8), max(1.0, width * 0.72), depth - 1)

    for k in range(4):
        grow(0.0, 0.5 + rng.uniform(-0.05, 0.05), rng.uniform(-0.5, 0.5), rng.uniform(0.2, 0.3), 4.0, 7)
    return img


def leaf_card(kind, colours, seed, n=512, bark="#6B5B4E", count=16):
    """A leafy twig: leaves of one species along a short twig, as seen from above."""
    rng = np.random.default_rng(seed)
    img = canvas(n)
    stroke(img, (0, n * 0.5), (n * 0.9, n * 0.5), 5, 2, hex_rgb(bark))
    shape = _leaf_shape(kind)
    size = {"maple": 0.13, "birch": 0.09, "beech": 0.09, "aspen": 0.085}[kind] * n
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


def bark(style, base, seed, n=512):
    """Tileable bark: u wraps around the trunk, v runs up it."""
    rng = np.random.default_rng(seed)
    c = hex_rgb(base)
    img = np.ones((n, n, 4), np.float32)
    if style in ("furrowed", "scaly", "douglas"):
        # Vertical plates separated by dark furrows; v (rows) runs up the trunk.
        cx, cy = {"furrowed": (10, 3), "scaly": (14, 10), "douglas": (7, 2)}[style]
        f = _fbm(n, cx, cy, rng)
        plates = np.clip((f - 0.35) * 3.0, 0, 1)
        detail = _fbm(n, cx * 4, cy * 4, rng, 3)
        v = 0.45 + 0.55 * plates * (0.8 + 0.4 * detail)
        img[..., :3] = c * v[..., None]
    elif style == "birch":
        f = _fbm(n, 6, 20, rng)
        img[..., :3] = c * (0.9 + 0.1 * f)[..., None]
        dash = _noise(n, 8, 60, rng)
        marks = (dash > 0.82).astype(np.float32)
        img[..., :3] *= (1 - 0.8 * marks)[..., None]
        patches = np.clip((_fbm(n, 4, 8, rng) - 0.62) * 6, 0, 1)
        img[..., :3] *= (1 - 0.75 * patches)[..., None]
    elif style == "aspen":
        f = _fbm(n, 5, 5, rng)
        img[..., :3] = c * (0.88 + 0.12 * f)[..., None]
        eyes = np.clip((_noise(n, 5, 7, rng) - 0.8) * 8, 0, 1)
        img[..., :3] *= (1 - 0.7 * eyes)[..., None]
    elif style == "beech":
        f = _fbm(n, 4, 4, rng)
        img[..., :3] = c * (0.85 + 0.25 * f)[..., None]
    elif style == "yellowbirch":
        f = _fbm(n, 4, 30, rng)
        curls = np.clip((_noise(n, 6, 40, rng) - 0.7) * 5, 0, 1)
        img[..., :3] = c * (0.85 + 0.2 * f)[..., None] * (1 - 0.35 * curls)[..., None]
    else:
        raise ValueError(style)
    img[..., 3] = 1
    return np.clip(img, 0, 1)
