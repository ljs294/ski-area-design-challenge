"""Shared pieces of the Sessellift Stern terminals (sessellift_stern4.json; assets/stern_drive.py, stern_return.py):
the gauge rule for the narrower models, the concrete piers, and the lifting portal over a head's sheave rows.

The Stern's overlays (sessellift_stern3.json, stern2.json) change only common.lineGauge; liftkit/gauge.py moves the
FGQ-4's values by its RULE, so the Stern moves its own here by the same rule (owner LD5): values given against the
rope follow it by construction, and the bullwheels' rims and plates follow the gauge, their section kept.
"""
import copy
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.prims import U, V, W


def m(x):
    return x / 1000.0


def pipe(mb, p0, p1, dia, lod, style, sides=(16, 12, 8, 6), caps=(True, True)):
    """A round member (pipe, tube, bar) of diameter dia (m) from p0 to p1 (lift points, m): the drawings' round steel
    stays round at every LOD, with fewer sides further out."""
    prims.cylinder(mb, p0, p1, dia / 2, parts.sides(lod, *sides), style, caps=caps, fan_caps=True)


def members(mb, items, hg, lod, st):
    """Drawn steel given as data (mm), each item one of:
      {"box": {"u": [a, b], "v": [a, b], "w": [a, b]}}        a block
      {"side": [[u, w], ...], "v": [a, b]}                    a side-view outline extruded across the line
      {"end": [[v, w], ...], "u": [a, b]}                     an end-view outline extruded along the line
      {"plan": [[u, v], ...], "w": [a, b]}                    a plan outline extruded upward
      {"pipe": [[u, v, w], [u, v, w]], "dia": d}              a round member
    with "sym": true to repeat it mirrored across the axis (v -> -v) or "on": -1 / 1 for one side only (v values then
    given on that side, outboard +), "rope": true when its v values are measured outboard (+) of the rope rather than
    from the axis (so it follows the gauge; implies sym unless "on"), or a list of the indices of the values (points)
    that are, "lod": the last LOD that draws it (default 2), "from": the first (default 0) and "style" (default
    "steel"). A side outline may carry an "eye" ({"u": [a, b], "w": [a, b]}, a slot with round ends) cut through it
    at LOD0."""
    for it in items:
        if lod > it.get("lod", 2) or lod < it.get("from", 0):
            continue
        style = st[it.get("style", "steel")]
        rope = it.get("rope", False)
        sides = (it["on"],) if "on" in it else (-1, 1) if rope or it.get("sym") else (1,)
        for s in sides:
            def vv(x, i=0, s=s):
                return s * (hg + m(x)) if rope is True or (isinstance(rope, list) and i in rope) else s * m(x)
            if "box" in it:
                b = it["box"]
                v0, v1 = sorted(vv(x, i) for i, x in enumerate(b["v"]))
                prims.box(mb, (m(b["u"][0]), v0, m(b["w"][0])), (m(b["u"][1]), v1, m(b["w"][1])), style)
            elif "side" in it:
                v0, v1 = sorted(vv(x, i) for i, x in enumerate(it["v"]))
                for poly in eyed(it["side"], it["eye"]) if "eye" in it and lod == 0 else (it["side"],):
                    prims.prism(mb, [(m(u), m(w)) for u, w in poly], (0, v0, 0), U, W, V, v1 - v0, style)
            elif "end" in it:
                poly = [(vv(v, i), m(w)) for i, (v, w) in enumerate(it["end"])]
                prims.prism(mb, poly, (m(it["u"][0]), 0, 0), V, W, U, m(it["u"][1] - it["u"][0]), style)
            elif "plan" in it:
                poly = [(m(u), vv(v, i)) for i, (u, v) in enumerate(it["plan"])]
                prims.prism(mb, poly, (0, 0, m(it["w"][0])), U, V, W, m(it["w"][1] - it["w"][0]), style)
            elif "path" in it:   # a rectangular bar along a plan path (u, v), w range, thickness across
                pts = [(m(u), vv(v, i)) for i, (u, v) in enumerate(it["path"])]
                rect_path(mb, pts, m(it["thick"]) / 2, m(it["w"][0]), m(it["w"][1]), style)
            elif "pipe" in it:
                (ua, va, wa), (ub, vb, wb) = it["pipe"]
                pipe(mb, (m(ua), vv(va, 0), m(wa)), (m(ub), vv(vb, 1), m(wb)), m(it["dia"]), lod, style,
                     sides=tuple(it.get("sides", (16, 12, 8, 6))))


def eyed(outline, eye, steps=2):
    """A side outline (u, w; mm) with a slot eye through it (round ends, `steps` segments a quarter) as two simple
    polygons either side of the eye's centre line, since a prism's caps can't hold a hole. The outline crosses that
    line twice, above and below the eye."""
    (e0, e1), (f0, f1) = eye["u"], eye["w"]
    uc, r = (e0 + e1) / 2, (e1 - e0) / 2
    area = sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(outline, outline[1:] + outline[:1]))
    poly = list(outline) if area > 0 else list(reversed(outline))   # counter-clockwise
    pts = []
    for p, q in zip(poly, poly[1:] + poly[:1]):
        pts.append(tuple(p))
        if (p[0] - uc) * (q[0] - uc) < 0:
            pts.append((uc, p[1] + (q[1] - p[1]) * (uc - p[0]) / (q[0] - p[0])))
    on = sorted((i for i, p in enumerate(pts) if p[0] == uc), key=lambda i: pts[i][1])
    assert len(on) == 2, ("the outline must cross the eye's centre line twice", on)
    bottom, top = on

    def walk(a, b):   # the outline counter-clockwise from point a to point b
        out = [pts[a]]
        while a != b:
            a = (a + 1) % len(pts)
            out.append(pts[a])
        return out

    def edge(spans):   # the eye's edge, clockwise round the hole
        return [(uc + r * math.cos(math.radians(a0 + (a1 - a0) * k / steps)), wc + r * math.sin(math.radians(a0 + (a1 - a0) * k / steps)))
                for wc, a0, a1 in spans for k in range(steps + 1)]
    return [walk(top, bottom) + edge(((f0 + r, 270, 180), (f1 - r, 180, 90))),
            walk(bottom, top) + edge(((f1 - r, 90, 0), (f0 + r, 0, -90)))]


def rect_path(mb, pts, half_w, lo, hi, style):
    """A rectangular bar (half_w across, lo to hi in w) swept along a horizontal polyline of (u, v) points (m),
    mitred at the bends (as return_terminal.rect_path)."""
    import return_terminal
    return_terminal.rect_path(mb, pts, half_w, lo, hi, style)


def tube(mb, pts, dia, lod, style, sides=(8, 6, 4), corner=0.0, steps=3):
    """A bent round tube along a polyline (lift points, m), its inner corners rounded to `corner` (m)."""
    if lod >= len(sides) + 1:
        return
    pts = [Vector(p) for p in pts]
    if corner > 0:
        for i in range(len(pts) - 2, 0, -1):
            pts = prims.fillet(pts, i, corner, steps if lod == 0 else 1)
    prims.tube_path(mb, pts, dia / 2, parts.sides(lod, *sides), style)


def railing(mb, posts, heights, lod, style, post_dia=0.034, rail_dia=0.042, toe=None):
    """A railing of round posts and rails as drawn: posts (lift points of their feet, m, in order along the rail),
    rails at `heights` above the feet (m, centres), and a toe board (toe: its height, m) along the feet. LOD2 draws
    the top rail and posts only; nothing from LOD3."""
    pts = [Vector(p) for p in posts]
    if lod >= 3 or len(pts) < 2:
        return
    top = max(heights)
    for p in pts:
        pipe(mb, p, p + W * (top + rail_dia / 2), post_dia, lod, style, sides=(8, 6, 4), caps=(False, True))
    for h in heights if lod <= 1 else (top,):
        for a, b in zip(pts, pts[1:]):
            pipe(mb, a + W * h, b + W * h, rail_dia, lod, style, sides=(8, 6, 4), caps=(False, False))
    if toe and lod <= 1:
        for a, b in zip(pts, pts[1:]):
            prims.beam(mb, a + W * (toe / 2), b + W * (toe / 2), 0.008, toe, style)


def ladder(mb, foot, head, width, rung_rise, lod, style, rung_style, stringer=0.15):
    """A ship's ladder from foot to head (lift points, m), its rungs every rung_rise (m) of height as drawn
    (parts.ladder spaces them along the stringer)."""
    foot, head = Vector(foot), Vector(head)
    rise = head.z - foot.z
    along = (head - foot).length
    parts.ladder(mb, foot, head, width, lod, style, rung_every=rung_rise * along / rise, handrails=False, rung_style=rung_style,
                 stringer=stringer)


def shift(c):
    """ds (mm): how far each rope sits outboard of the quad's, negative on a narrower model."""
    return (c["lineGauge"] - c["quadGauge"]) / 2


def drive_wheel(c, bw):
    """The drive's bullwheel section at the line gauge: the rim and the cone's outer edge moved by ds (the hub keeps
    its size)."""
    ds = shift(c)
    out = copy.deepcopy(bw)
    out["rim"] = [[r + ds, w] for r, w in bw["rim"]]
    out["cone"]["to"][0] += ds
    return out


def drive_wheel_mesh(mb, centre, bw, lod, st):
    """The drive's bullwheel as drawn (bw: spec "drive" "bullwheel", mm, radii from the axle, heights from the rope):
    the rim (the rope seated in the groove in its wall), the dished cone from the hub out to the rim (no spokes are
    drawn) and the hub's stacked rings."""
    c = Vector(centre)
    n = parts.sides(lod, 64, 28, 16, 8)
    rim = [(m(r), m(w)) for r, w in bw["rim"]]
    wheel = st["dark"]
    if lod <= 1:
        prims.lathe(mb, c, W, rim, n, wheel)
    else:
        r_lo, r_hi = min(r for r, _ in rim), max(r for r, _ in rim)
        bot, top = min(w for _, w in rim), max(w for _, w in rim)
        prims.lathe(mb, c, W, [(r_lo, bot), (r_hi, bot), (r_hi, top), (r_lo, top)], n, wheel)
    if lod >= 3:
        return
    cn = bw["cone"]
    (r0, w0), (r1, w1), th = [(m(a), m(b)) for a, b in (cn["from"], cn["to"])] + [m(cn["thick"])]
    prims.lathe(mb, c, W, [(r0, w0), (r1, w1), (r1, w1 + th), (r0, w0 + th)], n, wheel)
    hs = parts.sides(lod, 24, 12, 8)
    for rg in bw["rings"] if lod <= 1 else bw["rings"][:1]:
        prims.cylinder(mb, c + W * m(rg["bottom"]), c + W * m(rg["top"]), m(rg["dia"]) / 2, hs, wheel, fan_caps=True)
    sh = bw["shaft"]   # its ends are buried in the hub and the carriage plate
    prims.cylinder(mb, c + W * m(sh["bottom"]), c + W * m(sh["top"]), m(sh["dia"]) / 2, hs, wheel, caps=(False, False))


def return_wheel(c, bw):
    """The return's bullwheel section at the line gauge: the rim, the plate ring and the spokes' outer end moved by
    ds (the hub keeps its size)."""
    ds = shift(c)
    out = copy.deepcopy(bw)
    out["rim"] = [[r + ds, w] for r, w in bw["rim"]]
    out["plate"]["inner"] += ds
    out["plate"]["outer"] += ds
    out["spokes"]["under"][1][0] += ds
    return out


def return_wheel_mesh(mb, centre, bw, lod, st):
    """The return's exposed bullwheel as drawn (bw: spec "return" "bullwheel", mm, radii from the axle, heights from
    the rope): the rim (a bottom flange and a wall, the rope seated in the groove on its outer face), the thin top
    ring plate, the spokes rising from the hub's base flange to the rim with their tops flush with the plate
    (I-sections up close), the hub body and the bearing cap."""
    c = Vector(centre)
    n = parts.sides(lod, 64, 32, 16, 8)
    rim = [(m(r), m(w)) for r, w in bw["rim"]]
    pl = bw["plate"]
    p_lo, p_hi, pi_, po = m(pl["bottom"]), m(pl["top"]), m(pl["inner"]), m(pl["outer"])
    wheel = st["dark"]
    plate = wheel.but(snow=0.7)
    r_lo, r_hi = min(r for r, _ in rim), max(r for r, _ in rim)
    bot = min(w for _, w in rim)
    if lod <= 1:
        prims.lathe(mb, c, W, rim, n, wheel)
        prims.lathe(mb, c, W, [(pi_, p_lo), (po, p_lo), (po, p_hi), (pi_, p_hi)], n, plate)
    elif lod == 2:
        prims.lathe(mb, c, W, [(r_lo, bot), (r_hi, bot), (r_hi, p_lo), (po, p_lo), (po, p_hi), (pi_, p_hi), (pi_, p_lo), (r_lo, p_lo)], n, plate)
    else:   # far out the spokes are four flat bars across the hub and the hub a plain drum on the spigot (the ring floated)
        prims.lathe(mb, c, W, [(r_lo, bot), (po, bot), (po, p_hi), (pi_, p_hi)], n, plate)
        sw, hb = m(bw["spokes"]["width"]), bw["hub"]
        for i in range(4):
            ang = math.pi * i / 4
            d = U * math.cos(ang) + V * math.sin(ang)
            p = W.cross(d).normalized()
            prims.prism(mb, [(-r_lo, bot), (r_lo, bot), (r_lo, p_hi), (-r_lo, p_hi)], c - p * (sw / 2), d, W, p, sw, wheel)
        prims.cylinder(mb, c + W * m(hb["flange"]["bottom"]), c + W * m(hb["top"]), m(hb["dia"]) / 2, 8, wheel, fan_caps=True)
        return
    sp, hb = bw["spokes"], bw["hub"]
    (r0, w0), (r1, w1) = [(m(r), m(w)) for r, w in sp["under"]]

    def under(rr):   # the spokes' underside, straight from the hub's flange to the rim as drawn
        return w0 + (w1 - w0) * (rr - r0) / (r1 - r0)

    sw, tf, web = m(sp["width"]), 0.02, 0.014
    for i in range(sp["count"]):
        ang = 2 * math.pi * i / sp["count"]
        d = U * math.cos(ang) + V * math.sin(ang)
        p = W.cross(d).normalized()
        if lod == 0:   # web between the flanges, flanges full width
            prims.prism(mb, [(r0, under(r0) + tf), (r1, under(r1) + tf), (r1, p_hi - tf), (r0, p_hi - tf)], c - p * (web / 2), d, W, p, web, wheel)
            for fl in ([(r0, p_hi - tf), (r1, p_hi - tf), (r1, p_hi), (r0, p_hi)],
                       [(r0, under(r0)), (r1, under(r1)), (r1, under(r1) + tf), (r0, under(r0) + tf)]):
                prims.prism(mb, fl, c - p * (sw / 2), d, W, p, sw, wheel)
        else:
            prims.prism(mb, [(r0, under(r0)), (r1, under(r1)), (r1, p_hi), (r0, p_hi)], c - p * (sw / 2), d, W, p, sw, wheel)
    hs = parts.sides(lod, 24, 16, 8)
    fl = hb["flange"]
    prims.cylinder(mb, c + W * m(fl["bottom"]), c + W * m(fl["top"]), m(fl["dia"]) / 2, hs, wheel, fan_caps=True)
    prims.cylinder(mb, c + W * m(fl["top"]), c + W * m(hb["top"]), m(hb["dia"]) / 2, hs, wheel, fan_caps=True)
    if lod <= 1:
        cp = hb["cap"]
        prims.cylinder(mb, c + W * m(hb["top"]), c + W * m(cp["top"]), m(cp["dia"]) / 2, parts.sides(lod, 16, 10), wheel, fan_caps=True)


def pier(mb, lo, hi, top, bottom, margin, st):
    """A concrete pier under a base plate spanning lo..hi in (u, v) (mm), from far below grade (owner P3-07: really
    tall, so the terminal can be raised or lowered; the game puts a footing at grade under its pier_top socket) up
    to top. Returns the pier_top socket (its top centre, metres)."""
    u0, v0 = m(lo[0] - margin), m(lo[1] - margin)
    u1, v1 = m(hi[0] + margin), m(hi[1] + margin)
    prims.box(mb, (u0, v0, m(bottom)), (u1, v1, m(top)), st["concrete"], skip=("-2",))
    return ((u0 + u1) / 2, (v0 + v1) / 2, m(top))


def lifting_portal(mb, lp, hg, lod, st):
    """The lifting portal over a head's sheave rows, as drawn: two 152 mm legs leaning out from their feet to the
    top beam (square, or round where "dia" is given), and where drawn the beam's end caps and bent tubes from under the beam down to clamps on the legs; its
    plates (feet, lugs, hangers) are drawn steel in lp "steel" (members). Everything across the line that follows the
    rope is given against it (+ outboard), so it follows the gauge."""
    if lod >= 3:   # far out none of it (its beam alone hung in the air)
        return
    lg, bm = lp["legs"], lp["beam"]
    u0, u1 = m(lg["u"][0]), m(lg["u"][1])
    bu0, bu1 = (m(x) for x in bm.get("u", lg["u"]))
    b_lo, b_hi = m(bm["bottom"]), m(bm["top"])
    hb = hg + m(bm["out"])
    uc, wc = (bu0 + bu1) / 2, (b_lo + b_hi) / 2
    leg_top = b_lo
    if "dia" in bm:   # a round beam, the legs up to its lowest facet
        pipe(mb, (uc, -hb, wc), (uc, hb, wc), m(bm["dia"]), lod, st["steel"])
        leg_top += m(bm["dia"]) / 2 * (1 - math.cos(math.pi / parts.sides(lod, 16, 12, 8, 6)))
    else:
        prims.box(mb, (bu0, -hb, b_lo), (bu1, hb, b_hi), st["steel"])
    foot_w = m(lg["footW"])
    for s in (-1, 1):   # legs square in u, leaning out in v, cut level on the foot and under the beam
        f0, f1 = sorted((s * (hg + m(lg["foot"][0])), s * (hg + m(lg["foot"][1]))))
        h0, h1 = sorted((s * (hg + m(lg["head"][0])), s * (hg + m(lg["head"][1]))))
        prims.prism(mb, [(f0, foot_w), (f1, foot_w), (h1, leg_top), (h0, leg_top)], (u0, 0, 0), V, W, U, u1 - u0, st["steel"])
        cap = bm.get("caps")
        if cap and lod <= 1 and "dia" in cap:   # a round beam's end caps
            pipe(mb, (uc, s * hb, wc), (uc, s * (hb + m(cap["thick"])), wc), m(cap["dia"]), lod, st["steel"])
        elif cap and lod <= 1:   # the beam's end caps, deeper than the beam
            e0, e1 = sorted((s * hb, s * (hb - m(cap["thick"]))))
            prims.box(mb, (m(cap["u"][0]), e0, m(cap["bottom"])), (m(cap["u"][1]), e1, m(cap["top"])), st["steel"])
    members(mb, lp.get("steel", []), hg, lod, st)
    tb = lp.get("tubes")
    if tb and lod <= 1:   # bent tubes from under the beam, down and out to clamps on the legs
        uc = (u0 + u1) / 2
        for s in (-1, 1):
            pts = [(uc, s * m(tb["v"]), m(tb["top"])), (uc, s * m(tb["v"]), m(tb["level"])), (uc, s * (hg + m(tb["to"])), m(tb["level"]))]
            tube(mb, pts, m(tb["dia"]), lod, st["steel"], corner=m(tb["corner"]))
