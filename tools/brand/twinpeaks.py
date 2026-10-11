"""Twin Peaks, the chosen brand (task P2-03.1, owner 2026-10-11): the geometry of every mark, on a 24-unit grid, y down.

Two crisp peaks in the proportions of the demo mountain's real twin summit, the lower one standing in front, cut free by
one clean gap; every outer flank is exactly 2:1. The game's mark adds a winding trail down the tall peak (straight legs
joined by true arcs) and a straight lift bar with level ends up the front one. The studio's mark is the plain pair; in
the Alpine Labs lockup its A is a third peak rising from behind them. In the game's name the A is a peak at the mark's
angle. The decisions behind every number are in docs/brand/README.md and docs/plans/brand/logo-options.html (history).
Needs shapely.
"""
import math

from shapely.geometry import LineString, Polygon
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

BASE = 21.0                                     # the ground line: whole pixels at 16, 24, 32, 48, 64 and 256
BACK = [(0.5, BASE), (9.5, 3.0), (18.5, BASE)]  # the tall peak, behind
FRONT = [(8.0, BASE), (16.5, 8.0), (23.0, BASE)]
TRAIL = ([(9.5, 7.6), (8.3, 10.0), (10.15, 12.25), (6.1, 15.6), (4.6, 18.6)], [1.6, 1.6, 3.0])
LIFT = ((16.45, 12.0), (18.8, 19.25))           # the bar's top and bottom centres; its ends are cut level


def fillet(pts, radii, n=40):
    """A polyline through pts with each inner corner rounded by a circular arc (a spline's tight bends pinch)."""
    out = [pts[0]]
    for i in range(1, len(pts) - 1):
        a, b, c = pts[i - 1], pts[i], pts[i + 1]
        r = radii[i - 1]
        u = (a[0] - b[0], a[1] - b[1]); lu = math.hypot(*u); u = (u[0] / lu, u[1] / lu)
        v = (c[0] - b[0], c[1] - b[1]); lv = math.hypot(*v); v = (v[0] / lv, v[1] / lv)
        ang = math.acos(max(-1, min(1, u[0] * v[0] + u[1] * v[1])))
        t = r / math.tan(ang / 2)
        p1 = (b[0] + u[0] * t, b[1] + u[1] * t); p2 = (b[0] + v[0] * t, b[1] + v[1] * t)
        bis = (u[0] + v[0], u[1] + v[1]); lb = math.hypot(*bis); bis = (bis[0] / lb, bis[1] / lb)
        d = r / math.sin(ang / 2); ctr = (b[0] + bis[0] * d, b[1] + bis[1] * d)
        a1 = math.atan2(p1[1] - ctr[1], p1[0] - ctr[0]); a2 = math.atan2(p2[1] - ctr[1], p2[0] - ctr[0])
        da = (a2 - a1 + math.pi) % (2 * math.pi) - math.pi
        out += [(ctr[0] + r * math.cos(a1 + da * k / n), ctr[1] + r * math.sin(a1 + da * k / n)) for k in range(n + 1)]
    out.append(pts[-1])
    return out


def scaled(points, k):
    return [(x * k, y * k) for x, y in points]


def peaks(gap=1.0, grid=24.0):
    """The plain pair: the back peak minus the front peak grown by the gap, and the front peak."""
    k = grid / 24.0
    back, front = Polygon(scaled(BACK, k)), Polygon(scaled(FRONT, k))
    visible = back.difference(front.buffer(gap * k, join_style='mitre'))
    visible = max(getattr(visible, 'geoms', [visible]), key=lambda g: g.area)
    return unary_union([visible, front])


def game_mark(line=1.0, trail=1.08, gap=1.0):
    """The game's mark: the pair with the trail and the lift cut out. The trail is a hair heavier than the lift because
    a curved, round-ended stroke reads lighter than a straight one."""
    shape = peaks(gap)
    pts, radii = TRAIL
    t = LineString(fillet(pts, radii)).buffer(trail / 2, cap_style='round', join_style='round', resolution=24)
    (xt, yt), (xb, yb) = LIFT
    hw = (line / 2) * math.hypot(xb - xt, yb - yt) / abs(yb - yt)
    lift = Polygon([(xt - hw, yt), (xt + hw, yt), (xb + hw, yb), (xb - hw, yb)])
    return shape.difference(t).difference(lift)


def third_peak_a(apex_y=8.0, leg=2.3, behind_at=16.5, gap=1.0, inner=2.15, bar_top=0.37, bar=0.9):
    """The Alpine Labs A: a third peak behind the pair, on their ground line, its outer flanks 2:1, its apex level with
    the front summit. It tucks behind the front peak so that peak's right flank stands in for the A's left leg (the same
    gap cuts it free); the inner flanks taper (`inner`:1); a crossbar `bar` legs thick, its top at `bar_top` of the A's
    height. Returns the shape and the x of its right foot."""
    h = BASE - apex_y
    ax = behind_at - (8.0 + apex_y) / 2 + 16.5
    x0, w = ax - h / 2, h
    k = leg * math.sqrt(5) / 2
    xl = lambda y: x0 + k + (BASE - y) / inner
    xr = lambda y: x0 + w - k - (BASE - y) / inner
    ia = BASE - (w - 2 * k) * inner / 2
    bt = BASE - bar_top * h
    bb = bt + bar * leg
    outline = Polygon([(x0, BASE), (ax, apex_y), (x0 + w, BASE), (xr(BASE), BASE), (xr(bb), bb), (xl(bb), bb), (xl(BASE), BASE)])
    a = outline.difference(Polygon([(ax, ia), (xr(bt), bt), (xl(bt), bt)])) if bt > ia + 0.05 else outline
    a = a.difference(Polygon(FRONT).buffer(gap, join_style='mitre'))
    return a, x0 + w


def peak_a(x, base, cap, stem):
    """The A drawn as a peak to sit in Overpass: its point at Overpass's ascender (1.031 cap) as an overshoot, outer
    flanks 2:1 like the mark's, inner flanks 2.15:1 so the legs taper, a crossbar from the cap height (top 0.37 cap,
    0.88 stem thick). Returns the shape and its width."""
    h = 1.031 * cap
    w = h
    k = stem * math.sqrt(5) / 2
    xl = lambda y: x + k + (base - y) / 2.15
    xr = lambda y: x + w - k - (base - y) / 2.15
    ia = base - (w - 2 * k) * 1.075
    bt = base - 0.37 * cap
    bb = bt + 0.88 * stem
    outline = Polygon([(x, base), (x + w / 2, base - h), (x + w, base), (xr(base), base), (xr(bb), bb), (xl(bb), bb), (xl(base), base)])
    if bt > ia + 0.02:
        outline = outline.difference(Polygon([(x + w / 2, ia), (xr(bt), bt), (xl(bt), bt)]))
    return outline, w


def to_path(geom, digits=3):
    """SVG path data for a (multi)polygon, rings wound for the nonzero rule (outer counter-clockwise on screen)."""
    polys = list(geom.geoms) if hasattr(geom, 'geoms') else [geom]
    out = []
    for p in polys:
        p = orient(p, sign=-1.0)   # y points down, so a clockwise ring in maths coordinates is counter-clockwise on screen
        for ring in [p.exterior] + list(p.interiors):
            c = list(ring.coords)[:-1]
            out.append('M' + ' L'.join(f'{round(x, digits):g} {round(y, digits):g}' for x, y in c) + ' Z')
    return ' '.join(out)
