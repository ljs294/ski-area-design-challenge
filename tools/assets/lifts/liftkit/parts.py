"""Lift parts built from primitives, in the lift frame. Detail follows an LOD level (0 = nearest)."""
import math

from mathutils import Vector

from . import prims
from .mesh import MeshBuilder, Style
from .prims import U, V, W, vec


def sides(lod, near, *far):
    """Polygon sides for a round part at this LOD: near at LOD0, then the listed values."""
    seq = (near,) + far
    return seq[min(lod, len(seq) - 1)]


def sheave(mb, center, axis, dia, width, lod, style_wheel, style_liner, spokes=True):
    """A grooved rope sheave: rim with a rubber liner, a web and a hub. Axis through center."""
    r = dia / 2
    n = sides(lod, 24, 12, 8, 6)
    ax = vec(axis).normalized()
    c = vec(center)
    hw = width / 2
    if lod >= 2:
        prims.cylinder(mb, c - ax * hw, c + ax * hw, r, n, style_wheel)
        return
    # rim (flanges) and liner groove, as one lathe section: (radius, height along axis)
    groove = 0.35 * width
    rim = [(r * 0.80, -hw), (r + 0.012, -hw), (r + 0.012, -groove), (r - 0.02, -groove * 0.5),
           (r - 0.02, groove * 0.5), (r + 0.012, groove), (r + 0.012, hw), (r * 0.80, hw)]
    prims.lathe(mb, c, ax, rim, n, style_wheel)
    if lod == 0:
        prims.lathe(mb, c, ax, [(r - 0.02, -groove * 0.5), (r - 0.005, -groove * 0.3), (r - 0.005, groove * 0.3), (r - 0.02, groove * 0.5)],
                    n, style_liner, closed=False)
    # web and hub
    prims.cylinder(mb, c - ax * (width * 0.12), c + ax * (width * 0.12), r * 0.80, n, style_wheel, caps=(True, True))
    prims.cylinder(mb, c - ax * (hw + 0.02), c + ax * (hw + 0.02), r * 0.22, sides(lod, 12, 8), style_wheel.but(cls="machined"))


def sheave_bracket(mb, centre, width, from_w, lod, style):
    """A plate on the inboard side of a rope sheave (toward v = 0), from from_w to just past the axle, so grips
    pass freely on the outboard side."""
    c = vec(centre)
    inboard = -1.0 if c.y > 0 else 1.0
    pv = c.y + inboard * (width / 2 + 0.03)
    lo_w, hi_w = sorted((from_w, c.z + (0.06 if from_w < c.z else -0.06)))
    prims.box(mb, (c.x - 0.09, pv - 0.015, lo_w), (c.x + 0.09, pv + 0.015, hi_w), style)
    if lod <= 1:   # stub axle boss
        prims.cylinder(mb, (c.x, pv, c.z), (c.x, c.y + inboard * (width / 2), c.z), 0.045, sides(lod, 10, 6), style.but(cls="machined"))


def bullwheel(mb, center, spec, lod, style):
    """Horizontal bullwheel (vertical axle through center at rope elevation).

    spec (metres): pitch (rope circle diameter), rimTop/rimBottom (w relative to rope), plateOuter/plateInner
    (top annulus diameters), plateThick, hubDia, hubBottom/hubTop (relative), spokes, spokeWidth, spokeDepth."""
    c = vec(center)
    n = sides(lod, 64, 32, 16, 8)
    rp = spec["pitch"] / 2
    top, bot = spec["rimTop"], spec["rimBottom"]
    ri = spec["plateInner"] / 2
    st = style
    liner = style.but(cls="rubber")
    if lod <= 1:
        # rim channel with the rope groove on its outer face
        rim = [(ri + 0.05, bot), (rp + 0.045, bot), (rp + 0.045, -0.03), (rp + 0.012, -0.018), (rp + 0.012, 0.018),
               (rp + 0.045, 0.03), (rp + 0.045, top), (ri + 0.05, top)]
        prims.lathe(mb, c, W, rim, n, st)
        if lod == 0:
            prims.lathe(mb, c, W, [(rp + 0.012, -0.018), (rp + 0.002, -0.01), (rp + 0.002, 0.01), (rp + 0.012, 0.018)], n, liner, closed=False)
        # top annulus plate
        plate = [(ri, top), (spec["plateOuter"] / 2, top), (spec["plateOuter"] / 2, top + spec["plateThick"]), (ri, top + spec["plateThick"])]
        prims.lathe(mb, c, W, plate, n, st.but(snow=0.7))
    else:
        prims.lathe(mb, c, W, [(ri, bot), (spec["plateOuter"] / 2, bot), (spec["plateOuter"] / 2, top + spec["plateThick"]),
                                (ri, top + spec["plateThick"])], n, st.but(snow=0.7))
        if lod >= 3:
            return
    # hub
    hr = spec["hubDia"] / 2
    hb, ht = spec["hubBottom"], spec["hubTop"]
    prims.cylinder(mb, c + W * hb, c + W * ht, hr, sides(lod, 24, 16, 8), st)
    # spokes (sloping from the hub up to the rim), I-section at LOD0, boxes further out
    k = spec["spokes"] if lod <= 1 else spec["spokes"] // 2
    for i in range(k):   # spokes on the u and v axes and the diagonals
        a = 2 * math.pi * i / k
        d = U * math.cos(a) + V * math.sin(a)
        p0 = c + d * (hr * 0.9) + W * (hb + (ht - hb) * 0.35)
        p1 = c + d * (ri + 0.06) + W * (bot + (top - bot) * 0.5)
        if lod == 0:
            prims.ibeam(mb, p0, p1, spec["spokeDepth"], spec["spokeWidth"], 0.018, 0.012, st, up=W)
        else:
            prims.beam(mb, p0, p1, spec["spokeWidth"], spec["spokeDepth"], st, up=W)
    # inner ring tying the spokes (LOD0-1)
    if lod <= 1:
        ring = [(ri, bot), (ri + 0.05, bot), (ri + 0.05, top), (ri, top)]
        prims.lathe(mb, c, W, ring, n // 2, st)


def railing(mb, points, height, lod, style, posts_every=1.2, toe=True, closed=False):
    """A handrail along a polyline on a deck: posts, top and knee rails, toe board."""
    pts = [vec(p) for p in points]
    if closed:
        pts.append(pts[0])
    r_post = 0.024
    if lod >= 3:
        return
    for a, b in zip(pts, pts[1:]):
        seg = b - a
        length = seg.length
        if length < 1e-6:
            continue
        d = seg / length
        if lod <= 1:
            n_posts = max(1, round(length / (posts_every * (1 if lod == 0 else 2))))
            for i in range(n_posts + 1):
                p = a + d * (length * i / n_posts)
                if lod == 0:
                    prims.cylinder(mb, p, p + W * height, r_post, 6, style)
                else:
                    prims.beam(mb, p, p + W * height, 0.045, 0.045, style, up=U if abs(d.dot(U)) < 0.9 else V)
            n_rail = 6 if lod == 0 else 4
            prims.cylinder(mb, a + W * height, b + W * height, r_post, n_rail, style)
            prims.cylinder(mb, a + W * (height * 0.5), b + W * (height * 0.5), r_post * 0.8, n_rail, style)
        else:
            prims.beam(mb, a + W * (height - 0.02), b + W * (height - 0.02), 0.05, 0.05, style)
        if toe and lod <= 2:
            prims.beam(mb, a + W * 0.05, b + W * 0.05, 0.008, 0.10, style)


def ladder(mb, bottom, top, width, lod, style, rung_every=0.28, handrails=True):
    """A ship's ladder (stringers and rungs) from bottom to top, width across."""
    b, t = vec(bottom), vec(top)
    x, y, z = prims.basis_along(t - b, W)
    hw = width / 2
    for s in (-1, 1):
        if lod <= 2:
            prims.beam(mb, b + y * s * hw, t + y * s * hw, 0.012, 0.15, style, up=z)
    if lod <= 1:
        length = (t - b).length
        n = int(length / (rung_every * (1 if lod == 0 else 2)))
        for i in range(1, n + 1):
            p = b + (t - b) * (i / (n + 1))
            prims.beam(mb, p - y * hw, p + y * hw, 0.06 if lod == 0 else 0.04, 0.03, style.but(cls="safety_yellow" if lod == 0 else style.cls), up=z)
    if handrails and lod <= 1:
        for s in (-1, 1):
            prims.cylinder(mb, b + y * s * (hw + 0.03) + W * 0.9, t + y * s * (hw + 0.03) + W * 0.9, 0.022, 6 if lod == 0 else 4, style)


def deck(mb, lo, hi, lod, style, grating=True):
    """A walkway deck plate with edge channels."""
    st = style.but(cls="grating" if grating else style.cls, snow=0.8)
    lo, hi = vec(lo), vec(hi)
    prims.box(mb, lo, hi, st)
    if lod == 0:   # edge channels under the plate, along both long sides
        along_u = (hi.x - lo.x) >= (hi.y - lo.y)
        depth = 0.12
        for s in (0, 1):
            if along_u:
                y0 = lo.y if s == 0 else hi.y - 0.05
                prims.box(mb, (lo.x, y0, lo.z - depth), (hi.x, y0 + 0.05, lo.z), style)
            else:
                x0 = lo.x if s == 0 else hi.x - 0.05
                prims.box(mb, (x0, lo.y, lo.z - depth), (x0 + 0.05, hi.y, lo.z), style)
