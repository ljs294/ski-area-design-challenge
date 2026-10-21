"""Sessellift D6 terminals (sessellift_d6.json "terminal"): one station design at both ends, end for end, measured on
the owner's 1:50 layouts (round 1 of the adversarial review: four reviewers, every view) and seen through the
deliberate mirror (Gate 0, LM7): loaded chairs leave the bottom on the right rope (v+) and reach the top on the left.

  hood     the end caps' outline (skirt, band, a circular arch, the roof) swept along the station. The body carries
           25 window bays of 800 mm between two solid end caps that stand 62 mm proud of it at the band; each cap ends
           in a ring whose front face stands back from the hood's extreme by the side view's nose or the plan's
           rounding; the end walls sit flat inside the rings with their windows; the line end's wall is open low on
           both sides where the chairs leave. A white soffit closes the hood's underside, slotted where hangers pass.
  belly    the white bullwheel housing under the soffit: the end view's section (a tray over a keel), round at the back
           in plan, straight to the line end, then a wedge rising into the soffit
  frame    the two main beams, inclined with the wheel; posts and overhead members; portals; the station floor; the
           chair rail (a 40 x 50 bar round the loop, on hangers) and the line end's hanger guides (horns)
  wheel    the bullwheel, about 680 mm below the rope and tilted 5 degrees about v, line side up (a spinning part); the
           ropes climb from it in its plane to the bend, then run level to the line; the drive's three bend sheaves
  masts    the round front column with its head, base plate and the inclined ladder; the tapered rear leg with its
           head, saddle and base plate; their piers, 15 m deep (sockets pier_top_1, pier_top_2)

Variants: "drive" (top) and "return" (bottom). Origin on the bullwheel's axle at the load or unload level 0.00; u
toward the line, v right looking along u, w up.
"""
import math

from mathutils import Vector

from liftkit import frame, parts, prims
from liftkit.export import Asset
from liftkit.mesh import GLASS, MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "terminal_detach"
LODS = 4
SPEC = "sessellift_d6.json"
VARIANTS = ("drive", "return")


def m(x):
    return x / 1000.0


def styles(lod):
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("paint_grey", snow=0.5),
        "galv": Style("galvanised", snow=0.5),
        "dark": Style("paint_dark", snow=0.0),
        "hood": Style("livery", livery=1.0, snow=1.0),
        "white": Style("white", snow=0.0),
        "trim": Style("trim_dark", snow=0.0),
        "glass": Style("glass", slot=GLASS, smooth=False) if lod <= 1 else Style("glass", smooth=False),
        "lining": Style("interior"),
        "grating": Style("grating", snow=0.8),
        "red": Style("sheave_red"),
        "rod": Style("machined"),
    }


def interp(table, x):
    """Linear interpolation in a [[x, y], ...] table sorted by x, clamped at its ends."""
    if x <= table[0][0]:
        return table[0][1]
    for (x0, y0), (x1, y1) in zip(table, table[1:]):
        if x <= x1:
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0) if x1 > x0 else y1
    return table[-1][1]


def face(mb, pts_mm, style, outward):
    """One face through lift-frame points given in mm."""
    return mb.face(mb.verts_lift([(m(u), m(v), m(w)) for u, v, w in pts_mm]), style, outward)


# -- the hood's section --------------------------------------------------------------------------------------
def outline(h, lod):
    """The end caps' outline, half (v >= 0), mm, from the bottom edge: up the skirt to the band, the band's face, the
    arch (a circular arc) to the roof's corner, the roof to the crown."""
    pts = [tuple(p) for p in h["outline"]]
    a = h["arc"]
    cv, cw = a["centre"]
    r = a["radius"]
    v0, w0 = pts[-1]
    v1, w1 = a["to"]
    t0, t1 = math.atan2(w0 - cw, v0 - cv), math.atan2(w1 - cw, v1 - cv)
    n = (16, 10, 6, 3)[lod]
    for i in range(1, n + 1):
        t = t0 + (t1 - t0) * i / n
        pts.append((cv + r * math.cos(t), cw + r * math.sin(t)))
    pts.append((0.0, h["roof"]))
    return pts


def with_breaks(half, levels):
    """The half outline with points added where it crosses each height in levels (so faces split there)."""
    out = [half[0]]
    for a, b in zip(half, half[1:]):
        lo, hi = sorted((a[1], b[1]))
        cut = sorted((wl for wl in levels if lo < wl < hi), reverse=a[1] > b[1])
        for wl in cut:
            f = (wl - a[1]) / (b[1] - a[1])
            out.append((a[0] + (b[0] - a[0]) * f, wl))
        out.append(b)
    return out


def normals(half):
    """Outward unit normals (v, w) along a half outline running from the bottom edge up and over to the crown."""
    ns = []
    for i in range(len(half)):
        a = half[max(0, i - 1)]
        b = half[min(len(half) - 1, i + 1)]
        dv, dw = b[0] - a[0], b[1] - a[1]
        ln = math.hypot(dv, dw) or 1.0
        ns.append((dw / ln, -dv / ln))
    return ns


def offset(half, d):
    """The half outline moved inward by d (mm) along its normals; the bottom point stays on the bottom edge and the
    crown on the centreline."""
    out = [(v - nv * d, w - nw * d) for (v, w), (nv, nw) in zip(half, normals(half))]
    out[0] = (half[0][0] - d, half[0][1])
    out[-1] = (0.0, half[-1][1] - d)
    return out


def full(half):
    """Right half (bottom edge to crown) and its mirror: the whole section, from the right bottom edge round to the left."""
    return list(half) + [(-v, w) for v, w in reversed(half[:-1])]


def setback(h, v, w):
    """How far an end ring's front face stands back from the hood's extreme at (v, w): the side view's nose at its
    height or the plan's rounding toward the sides, whichever is more."""
    return max(interp(h["nose"], w), interp(h["plan"], abs(v)))


def inner_v(curve, w):
    """A half curve's v (right side) at height w."""
    for a, b in zip(curve, curve[1:]):
        lo, hi = sorted((a[1], b[1]))
        if lo <= w <= hi and hi > lo:
            return a[0] + (b[0] - a[0]) * (w - a[1]) / (b[1] - a[1])
    return curve[-1][0]


# -- hood ------------------------------------------------------------------------------------------------------
def hood(mb, t, end, lod, st):
    """Skin (body and caps), the caps' steps, the rings, the end walls with their windows, the lining and the soffit."""
    h = t["hood"]
    E0, E1 = end["hood"]
    W0, W1 = end["windows"]
    seam0, seam1 = E0 + h["endWall"], E1 - h["endWall"]
    win = h["windows"]
    levels = (win["from"], win["to"], win["head"]) if lod <= 2 else ()
    cap = with_breaks(outline(h, lod), levels)
    top, band = h["roof"], h["band"][1]

    def inset(w):
        return h["bodyInset"] * min(1.0, max(0.0, (top - w) / (top - band)))

    body = [(v - nv * inset(w), w - nw * inset(w)) for (v, w), (nv, nw) in zip(cap, normals(cap))]
    body[-1] = (0.0, cap[-1][1])
    cap_f, body_f = full(cap), full(body)

    def P(u, p):
        return (u, p[0], p[1])

    def out(a, b):
        return (0.0, b[1] - a[1], -(b[0] - a[0]))

    # the body: the window section, split at every pane edge so panes are glass and mullions and bands livery
    panes = []
    if lod <= 1:
        bay = win["bay"]
        heavy = end["frontMast"]["u"] + h["heavyMullion"]["at"]

        def half_mullion(u):
            return (h["heavyMullion"]["width"] if abs(u - heavy) < bay / 2 else win["mullion"]) / 2
        for k in range(round((W1 - W0) / bay)):
            u0, u1 = W0 + bay * k, W0 + bay * (k + 1)
            panes.append((u0 + half_mullion(u0), u1 - half_mullion(u1), k))
    elif lod == 2:
        panes.append((W0, W1, -1))
    breaks = sorted({W0, W1} | {x for p in panes for x in p[:2]})
    flip = {(f["side"], f["bay"]): f["transoms"] for f in end["flipBays"]}
    for ua, ub in zip(breaks, breaks[1:]):
        pane = next((p for p in panes if abs(p[0] - ua) < 1 and abs(p[1] - ub) < 1), None)
        for a, b in zip(body_f, body_f[1:]):
            wm = (a[1] + b[1]) / 2
            glass = pane is not None and win["from"] < wm < win["to"]
            if glass:
                side = 1 if (a[0] + b[0]) > 0 else -1
                glass = not any(lo < wm < hi for lo, hi in flip.get((side, pane[2]), ()))
            face(mb, [P(ua, a), P(ua, b), P(ub, b), P(ub, a)], st["glass" if glass else "hood"], out(a, b))
    # the caps: from the body's end to the end wall's seam, then on to the ring's front face; the step up from the body
    for side, wedge, ext, seam in ((-1, W0, E0, seam0), (1, W1, E1, seam1)):
        for a, b, ba, bb in zip(cap_f, cap_f[1:], body_f, body_f[1:]):
            fa, fb = ext - side * setback(h, *a), ext - side * setback(h, *b)
            face(mb, [P(wedge, a), P(wedge, b), P(seam, b), P(seam, a)], st["hood"], out(a, b))
            face(mb, [P(seam, a), P(seam, b), P(fb, b), P(fa, a)], st["hood"], out(a, b))
            if lod <= 2 and (abs(ba[0] - a[0]) > 1 or abs(bb[0] - b[0]) > 1):
                face(mb, [P(wedge, ba), P(wedge, bb), P(wedge, b), P(wedge, a)], st["hood"], (-side, 0.0, 0.0))
    ring(mb, h, cap, E0, E1, seam0, seam1, lod, st)
    end_wall(mb, h, cap, seam0, -1, False, lod, st)
    end_wall(mb, h, cap, seam1, 1, True, lod, st)
    if lod <= 1:   # the lining, a hand's width inside, facing in (it shows through the soffit's slots and the glass)
        lin_f = full(offset(cap, h["lining"]))
        for a, b in zip(lin_f, lin_f[1:]):
            o = out(a, b)
            face(mb, [P(seam0, a), P(seam0, b), P(seam1, b), P(seam1, a)], st["lining"], (0.0, -o[1], -o[2]))
    soffit(mb, t, end, cap, body, seam0, seam1, lod, st)


def ring(mb, h, cap, E0, E1, seam0, seam1, lod, st):
    """Each end's ring: its front face from the outline in to its inner edge (with a crest line where the top's rounded
    nose stands furthest out), and its soffit back to the end wall."""
    rw = h["ring"]["width"]
    halves = [cap, offset(cap, h["ring"]["crest"]), offset(cap, rw)] if lod <= 1 else [cap, offset(cap, rw)]
    rings = [full(r) for r in halves]
    inner = rings[-1]
    for ext, side, seam in ((E0, -1, seam0), (E1, 1, seam1)):
        def F(p):
            return (ext - side * setback(h, *p), p[0], p[1])
        for ra, rb in zip(rings, rings[1:]):
            for i in range(len(ra) - 1):
                face(mb, [F(ra[i]), F(ra[i + 1]), F(rb[i + 1]), F(rb[i])], st["hood"], (side, 0.0, 0.0))
        for a, b in zip(inner, inner[1:]):   # the ring's soffit, back to the end wall, facing the opening
            face(mb, [F(a), F(b), (seam, b[0], b[1]), (seam, a[0], a[1])], st["hood"], (0.0, -(b[1] - a[1]), b[0] - a[0]))
        for s in (1, -1):   # the ring's underside along the bottom edge
            o, i_ = cap[0], halves[-1][0]
            face(mb, [(seam, s * i_[0], o[1]), (seam, s * o[0], o[1]), F((s * o[0], o[1])), F((s * i_[0], o[1]))],
                 st["white"], (0.0, 0.0, -1.0))


def end_wall(mb, h, cap, u, side, line_end, lod, st):
    """A flat end wall at u inside the ring, with its window (frame, glass set back, mullions); at the line end the wall
    is open low on both sides, where the chairs, the rope and the horns pass."""
    ew = h["endWindow"]
    inner = offset(cap, h["ring"]["width"])
    bot = inner[0][1]
    fv, fw0, fw1 = ew["frame"]
    gv, gw0, gw1 = ew["glass"]
    nrm = (side, 0.0, 0.0)

    def wall(poly, style=None, du=0.0):
        face(mb, [(u - side * du, v, w) for v, w in poly], style or st["hood"], nrm)

    def right(w0, w1):
        return [(v, w) for v, w in inner if w0 < w < w1]

    if lod >= 2:
        wall(full(inner))
        wall([(-gv, gw0), (gv, gw0), (gv, gw1), (-gv, gw1)], st["glass"], -20.0)
        return
    # above the window, across the top
    upper = [(inner_v(inner, fw1), fw1)] + right(fw1, 1e9)
    wall(upper + [(-v, w) for v, w in reversed(upper[:-1])])
    if line_end:
        ov, ow = h["lineOpening"]["v"], h["lineOpening"]["top"]
        wall([(-ov, bot), (ov, bot), (ov, fw0), (-ov, fw0)])                      # under the window, between the openings
        for s in (1, -1):
            pts = [(fv, ow), (inner_v(inner, ow), ow)] + right(ow, fw1) + [(inner_v(inner, fw1), fw1), (fv, fw1)]
            wall([(s * v, w) for v, w in pts])                                    # beside the window, over the opening
            wall([(s * fv, fw0), (s * ov, fw0), (s * ov, ow), (s * fv, ow)])     # between the frame and the opening
    else:
        lower = [(inner_v(inner, bot), bot)] + right(bot, fw0) + [(inner_v(inner, fw0), fw0)]
        wall(lower + [(-v, w) for v, w in reversed(lower)])
        for s in (1, -1):
            pts = [(fv, fw0), (inner_v(inner, fw0), fw0)] + right(fw0, fw1) + [(inner_v(inner, fw1), fw1), (fv, fw1)]
            wall([(s * v, w) for v, w in pts])
    # the window: its frame (in the wall's plane, a hand deep), the glass set back, the mullions in front of it
    d = ew["depth"]
    ua, ub = sorted((u, u - side * d))
    for (v0, w0), (v1, w1) in (((-fv, fw0), (-gv, fw1)), ((gv, fw0), (fv, fw1)), ((-gv, fw0), (gv, gw0)), ((-gv, gw1), (gv, fw1))):
        prims.box(mb, (m(ua), m(v0), m(w0)), (m(ub), m(v1), m(w1)), st["trim"])
    wall([(-gv, gw0), (gv, gw0), (gv, gw1), (-gv, gw1)], st["glass"], d)
    ua, ub = sorted((u - side * d * 0.3, u - side * d))
    for mv, mw in ew["mullions"]:
        prims.box(mb, (m(ua), m(mv - mw / 2), m(gw0)), (m(ub), m(mv + mw / 2), m(gw1)), st["trim"])


# -- soffit ----------------------------------------------------------------------------------------------------
def rail_shape(t, end):
    """(corner centre u, corner centre v, corner radius, straight v) of this end's rail loop (mm)."""
    (cu, cv), rc = end["rail"]["corner"], end["rail"]["radius"]
    return cu, cv, rc, t["rail"]["v"]


def slot_intervals(t, end, u):
    """The soffit's slot at u: v intervals (>= 0) where the hangers pass, the rail's loop moved outward."""
    sl = t["hood"]["soffit"]
    cu, cv, rc, rv = rail_shape(t, end)
    d0, d1 = sl["offset"] - sl["width"] / 2, sl["offset"] + sl["width"] / 2
    if u >= cu:
        return [(rv + d0, rv + d1)]
    dx = cu - u
    r0, r1 = rc + d0, rc + d1
    if dx <= r0:
        return [(cv + math.sqrt(r0 * r0 - dx * dx), cv + math.sqrt(r1 * r1 - dx * dx))]
    if dx <= r1:
        return [(0.0, cv + math.sqrt(r1 * r1 - dx * dx))]
    return []


def soffit(mb, t, end, cap, body, seam0, seam1, lod, st):
    """The hood's white underside at its bottom edge, from end wall to end wall, slotted round the rail's loop where
    the hangers pass, with the ladder's hatch."""
    h = t["hood"]
    sl = h["soffit"]
    w = cap[0][1]
    W0, W1 = end["windows"]
    cu, cv, rc, rv = rail_shape(t, end)
    r0, r1 = rc + sl["offset"] - sl["width"] / 2, rc + sl["offset"] + sl["width"] / 2
    stops = {seam0, seam1, W0, W1, cu, cu - r0, cu - r1}
    k = cu - r1
    while k < cu:
        stops.add(k)
        k += 250 if lod <= 1 else 800
    la = end["frontMast"]["ladder"]
    hatch = (end["frontMast"]["u"] + la["hatch"][0], end["frontMast"]["u"] + la["hatch"][1])
    if lod <= 1:
        stops |= set(hatch)
    stops = sorted(s for s in stops if seam0 <= s <= seam1)

    def keep(u):
        edge = body[0][0] if W0 <= u <= W1 else cap[0][0]
        cut = list(slot_intervals(t, end, u))
        if lod <= 1 and hatch[0] <= u <= hatch[1]:
            cut.append((0.0, sl["hatchHalf"]))
        out, v = [], 0.0
        for a, b in sorted(cut):
            if a > v:
                out.append((v, a))
            v = max(v, b)
        if v < edge:
            out.append((v, edge))
        return out

    for ua, ub in zip(stops, stops[1:]):
        if ub - ua < 1:
            continue
        ka, kb = keep(ua + 0.5), keep(ub - 0.5)
        if len(ka) != len(kb) or any((a[0] == 0.0) != (b[0] == 0.0) for a, b in zip(ka, kb)):
            ka = kb = keep((ua + ub) / 2)
        for (a0, a1), (b0, b1) in zip(ka, kb):
            if a0 == 0.0:   # a strip across the middle: one face over both sides
                face(mb, [(ua, -a1, w), (ua, a1, w), (ub, b1, w), (ub, -b1, w)], st["white"], (0.0, 0.0, -1.0))
                continue
            for s in (1, -1):
                face(mb, [(ua, s * a0, w), (ua, s * a1, w), (ub, s * b1, w), (ub, s * b0, w)], st["white"], (0.0, 0.0, -1.0))


# -- belly -------------------------------------------------------------------------------------------------------
def belly(mb, t, end, lod, st):
    """The white housing under the soffit round the bullwheel: the end view's section (a tray over a keel), each level's
    back round in plan, straight to the line end, then a wedge rising into the soffit."""
    sec = [tuple(p) for p in t["belly"]["section"]]
    be = end["belly"]
    backs, uf, wedge = be["backs"], be["front"], be["wedge"]
    top = sec[0][1]
    nose = max(wedge)
    stations = [uf + (nose - uf) * f for f in ((1.0, 0.75, 0.5, 0.25, 0.0) if lod <= 1 else (1.0, 0.0))]
    kc = (12, 8, 4, 2)[lod]

    def level_w(j, u):
        wj = sec[j][1]
        if j == 0 or u <= uf:
            return wj
        return wj + (top - wj) * min(1.0, (u - uf) / (wedge[j] - uf))

    rings = []
    for j, (hj, wj) in enumerate(sec):
        cu = backs[j] + hj
        pts = [(u, hj, level_w(j, u)) for u in stations]
        for i in range(kc + 1):
            a = math.pi / 2 + math.pi * i / kc
            pts.append((cu + hj * math.cos(a), hj * math.sin(a), wj))
        pts += [(u, -hj, level_w(j, u)) for u in reversed(stations)]
        rings.append(pts)
    inside = Vector(((min(backs) + uf) / 2, 0.0, 2650.0))
    for ra, rb in zip(rings, rings[1:]):
        for i in range(len(ra) - 1):
            q = [ra[i], ra[i + 1], rb[i + 1], rb[i]]
            span = max(abs(q[0][2] - q[3][2]), abs(q[1][2] - q[2][2]), abs(q[0][1] - q[3][1]), abs(q[1][1] - q[2][1]))
            if span < 1.0:   # a level that has already risen into the soffit: no band left
                continue
            c = Vector((sum(p[0] for p in q) / 4, sum(p[1] for p in q) / 4, sum(p[2] for p in q) / 4))
            face(mb, q, st["white"], tuple(c - inside))
    low = rings[-1]
    n = len(low)
    k = len(stations)
    flat = low[k - 1:n - k + 1]
    face(mb, flat, st["white"], (0.0, 0.0, -1.0))
    for i in range(k - 1):
        face(mb, [low[i], low[i + 1], low[n - 2 - i], low[n - 1 - i]], st["white"], (0.3, 0.0, -1.0))


# -- station frame ---------------------------------------------------------------------------------------------
def beam_w(t, end, u):
    """The main beams' underside at u (they rise with the wheel's plane)."""
    return end["frame"]["beamBottom"] + math.tan(math.radians(t["bullwheel"]["tilt"])) * u


def frame_steel(mb, t, end, lod, st):
    f, fe = t["frame"], end["frame"]
    slope = math.tan(math.radians(t["bullwheel"]["tilt"]))
    bv, bw, bd = f["beamV"], f["beamWidth"], f["beamDepth"]
    u0, u1 = fe["beamU"]
    d = Vector((1.0, 0.0, slope)).normalized()
    nrm = Vector((-d.z, 0.0, d.x))
    mid = (u0 + u1) / 2
    for s in (-1, 1):   # the two main beams, inclined with the wheel
        c = Vector((m(mid), s * m(bv), m(beam_w(t, end, mid) + bd / 2)))
        prims.obox(mb, c, (d, V, nrm), (m((u1 - u0) / d.x) / 2, m(bw) / 2, m(bd) / 2), st["steel"])
    if lod >= 3:
        return
    ps = f["postSize"]
    for pu, pw0, pw1 in fe["posts"]:   # posts from the beams up to the overhead members
        for s in (-1, 1):
            prims.box(mb, (m(pu - ps / 2), m(s * bv - ps / 2), m(pw0)), (m(pu + ps / 2), m(s * bv + ps / 2), m(pw1)), st["steel"])
    ow0, ow1 = f["overhead"]["w"]
    for s in (-1, 1):
        prims.box(mb, (m(fe["overheadU"][0]), m(s * bv - 80), m(ow0)), (m(fe["overheadU"][1]), m(s * bv + 80), m(ow1)), st["steel"])
    if lod >= 2:
        return
    po = f["portals"]
    hs = po["size"] / 2
    pw0, pw1 = po["w"]
    o0, o1 = po["outer"]
    (_, cw0), (cv1, cw1) = po["chamfer"]
    for pu in fe["portals"]:   # across the station: inner legs, outer legs chamfered into the top beam
        for s in (-1, 1):
            prims.box(mb, (m(pu - hs), m(s * po["inner"] - hs), m(pw0)), (m(pu + hs), m(s * po["inner"] + hs), m(pw1 - po["size"])),
                      st["steel"])
            leg = [(o0, pw0), (o1, pw0), (o1, cw0), (cv1, cw1), (cv1, pw1 - po["size"]), (o0, pw1 - po["size"])]
            prims.prism(mb, [(m(s * a), m(b)) for a, b in leg], (m(pu - hs), 0.0, 0.0), V, W, U, m(po["size"]), st["steel"])
        prims.box(mb, (m(pu - hs), -m(cv1), m(pw1 - po["size"])), (m(pu + hs), m(cv1), m(pw1)), st["steel"])
    fl = f["floor"]
    g0, g1 = fe["floorGap"]   # the wheel's front rim rises through the floor's level: the floor leaves it room
    for s in (-1, 1):
        a, b = sorted((s * fl["v"][0], s * fl["v"][1]))
        for fu0, fu1 in ((fe["floorU"][0], g0), (g1, fe["floorU"][1])):
            parts.deck(mb, (m(fu0), m(a), m(fl["w"][0])), (m(fu1), m(b), m(fl["w"][1])), lod, st["grating"])
        a, b = sorted((s * fl["gapV"], s * fl["v"][1]))
        parts.deck(mb, (m(g0), m(a), m(fl["w"][0])), (m(g1), m(b), m(fl["w"][1])), lod, st["grating"])


def rail_path(t, end, inset=0.0, steps=6):
    """The rail's centreline round the loop (mm, u-v), from the right straight's line end round the back to the left
    straight's; inset moves it toward the loop's inside (the chairs' grips ride 134 inside the rail)."""
    cu, cv, rc, rv = rail_shape(t, end)
    r = rc - inset
    u_end = end["rail"]["end"]
    pts = [(u_end, rv - inset)]
    for a0, c in ((90.0, cv), (180.0, -cv)):
        for i in range(steps + 1):
            a = math.radians(a0 + 90.0 * i / steps)
            pts.append((cu + r * math.cos(a), c + r * math.sin(a)))
    pts.append((u_end, -(rv - inset)))
    return pts


def chair_rail(mb, t, end, lod, st):
    rail = t["rail"]
    if lod <= 2:
        path = [(m(u), m(v), m(rail["w"])) for u, v in rail_path(t, end, steps=6 if lod == 0 else 3)]
        prims.tube_path(mb, path, m(rail["section"][1]) / 2, 4 if lod <= 1 else 3, st["galv"])
    if lod <= 1:   # hangers from the portals' top beams down to the rail
        po = t["frame"]["portals"]
        top = po["w"][1] - po["size"]
        for pu in end["frame"]["portals"]:
            if end["rail"]["corner"][0] <= pu <= end["rail"]["end"]:
                for s in (-1, 1):
                    prims.box(mb, (m(pu - 40), m(s * rail["v"] - 40), m(rail["w"])), (m(pu + 40), m(s * rail["v"] + 40), m(top)), st["steel"])


def horns(mb, t, end, lod, st):
    """The hanger guides at the line end: a flat fish-tail each side, outboard of the hangers, from inside the end wall
    out past the hood; its outer face flares toward the tip."""
    hn = t["horns"]
    E = end["hood"][1]
    poly = [(E + du, w) for du, w in hn["outline"]]
    root, tip = min(p[0] for p in poly), max(p[0] for p in poly)
    wind = prims.winding(poly)
    for s in (-1, 1):
        def vo(u):
            f = min(1.0, max(0.0, (u - root) / (tip - root)))
            return hn["vOuter"][0] + (hn["vOuter"][1] - hn["vOuter"][0]) * f
        inner = [(u, s * hn["vInner"], w) for u, w in poly]
        outer = [(u, s * vo(u), w) for u, w in poly]
        face(mb, inner, st["dark"], (0.0, -s, 0.0))
        face(mb, outer, st["dark"], (0.0, s, 0.0))
        n = len(poly)
        for i in range(n):
            j = (i + 1) % n
            (ua, wa), (ub, wb) = poly[i], poly[j]
            face(mb, [inner[i], inner[j], outer[j], outer[i]], st["dark"], ((wb - wa) * wind, 0.0, -(ub - ua) * wind))


def rope_w(t, end, rope, u):
    """The rope's height at u: in the wheel's plane from the tangent points, level at the rope's height past the bend."""
    return min(rope, t["bullwheel"]["w"][end["name"]] + math.tan(math.radians(t["bullwheel"]["tilt"])) * u)


def bend_sheaves(mb, t, end, rope, lod, st):
    """The rope's support sheaves where it bends from the wheel's plane to level (the drive draws three a rope)."""
    sh = end.get("bendSheaves")
    if not sh or lod >= 2:
        return
    sv = t["sheave"]
    for u in sh["u"]:
        wc = rope_w(t, end, rope, u) - t["ropeDia"] / 2 - sv["dia"] / 2
        for s in (-1, 1):
            c = Vector((m(u), m(s * t["gauge"] / 2), m(wc)))
            prims.cylinder(mb, c - V * m(sv["width"]) / 2, c + V * m(sv["width"]) / 2, m(sv["dia"]) / 2, parts.sides(lod, 20, 12),
                           st["red"])
            prims.box(mb, (m(u - 60), m(s * t["gauge"] / 2 - 120), m(wc)), (m(u + 60), m(s * t["gauge"] / 2 + 120), m(beam_w(t, end, u))),
                      st["steel"])


def carriage(mb, t, end, lod, st):
    """The bullwheel's carriage between the main beams (the return's rolls on the beams for the tensioning), in the
    wheel's plane, and the hub's housing down to the wheel."""
    cg = end["carriage"]
    if lod >= 3:
        return
    slope = math.tan(math.radians(t["bullwheel"]["tilt"]))
    bd = t["frame"]["beamDepth"]
    u0, u1 = cg["u"]
    hv = cg["halfV"]
    d = Vector((1.0, 0.0, slope)).normalized()
    nrm = Vector((-d.z, 0.0, d.x))
    mid = (u0 + u1) / 2
    for s in (-1, 1):   # side members along the inside of the beams
        c = Vector((m(mid), m(s * hv), m(beam_w(t, end, mid) + bd * 0.4)))
        prims.obox(mb, c, (d, V, nrm), (m((u1 - u0) / d.x) / 2, 0.11, m(bd) * 0.3), st["steel"])
    for cu_ in cg["cross"]:
        c = Vector((m(cu_), 0.0, m(beam_w(t, end, cu_) + bd * 0.4)))
        prims.obox(mb, c, (d, V, nrm), (0.12, m(hv), m(bd) * 0.3), st["steel"])
    if lod <= 1:
        for ru in cg["rollers"]:   # the rollers it runs on, on the beams
            for s in (-1, 1):
                c = Vector((m(ru), m(s * (t["frame"]["beamV"] - t["frame"]["beamWidth"] / 2 - 90)), m(beam_w(t, end, ru) + bd)))
                prims.cylinder(mb, c - V * 0.09, c + V * 0.09, 0.14, 12 if lod == 0 else 8, st["dark"])
    tilt = math.radians(t["bullwheel"]["tilt"])
    axis = Vector((-math.sin(tilt), 0.0, math.cos(tilt)))
    centre = Vector((0.0, 0.0, m(t["bullwheel"]["w"][end["name"]])))
    top = m(beam_w(t, end, 0.0) + bd * 0.1) - centre.z
    prims.cylinder(mb, centre + axis * 0.2, centre + axis * top, 0.3, 16 if lod == 0 else 10, st["dark"])


# -- masts and piers ---------------------------------------------------------------------------------------------
def front_mast(mb, t, end, lod, st):
    fm, f = t["frontMast"], end["frontMast"]
    u = f["u"]
    r = fm["dia"] / 2
    toc = f["toc"]
    pl = fm["plate"]
    plate_lo, plate_hi = toc + pl["grout"], toc + pl["grout"] + pl["thick"]
    hd = f["head"]
    head0, head1 = hd["w"]
    hu, hv = hd["half"]
    prims.cylinder(mb, (m(u), 0.0, m(plate_hi)), (m(u), 0.0, m(head0)), m(r), parts.sides(lod, 24, 16, 10, 6), st["galv"],
                   caps=(False, False))
    prims.box(mb, (m(u - hu), m(-hv), m(head0)), (m(u + hu), m(hv), m(head1)), st["galv"])
    if lod <= 1:
        ph = pl["half"]
        prims.box(mb, (m(u - ph), m(-ph), m(plate_lo)), (m(u + ph), m(ph), m(plate_hi)), st["galv"])
        prims.box(mb, (m(u - ph - 30), m(-ph - 30), m(toc)), (m(u + ph + 30), m(ph + 30), m(plate_lo)), st["concrete"])
        g = hd["gusset"]
        for k in range(4):   # sloped gussets from the column out to the head's edges
            dvec = Vector(((1, 0, 0), (0, 1, 0), (-1, 0, 0), (0, -1, 0))[k])
            side = Vector((-dvec.y, dvec.x, 0.0))
            reach = (hu if k % 2 == 0 else hv) - r
            tri = [(m(r) - 0.01, m(head0)), (m(r + reach), m(head0)), (m(r) - 0.01, m(head0 - g))]
            prims.prism(mb, tri, tuple(Vector((m(u), 0.0, 0.0)) - side * 0.01), dvec, W, side, 0.02, st["galv"])
        if lod == 0:
            for bu in pl["bolts"]:
                for bv in pl["bolts"]:
                    prims.cylinder(mb, (m(u + bu), m(bv), m(plate_hi)), (m(u + bu), m(bv), m(plate_hi + 80)), 0.03, 6, st["rod"])
    la = f["ladder"]
    if lod <= 1:   # the inclined ladder on the line's centreline, its handrails running on up through the hatch
        (ub, wb), (ut, wt) = la["bottom"], la["top"]
        parts.ladder(mb, Vector((m(u + ub), 0.0, m(wb))), Vector((m(u + ut), 0.0, m(wt))), m(la["width"]), lod, st["galv"],
                     handrails=False)
        (hu0, hw0), (hu1, hw1) = la["rail"]
        for s in (-1, 1):
            prims.cylinder(mb, (m(u + hu0), m(s * (la["width"] / 2 + 30)), m(hw0)), (m(u + hu1), m(s * (la["width"] / 2 + 30)), m(hw1)),
                           0.022, 6 if lod == 0 else 4, st["galv"])


def rear_leg(mb, t, end, lod, st):
    """The rear leg: a box tapering from its base plate up to its head, leaning back under the beams, with the saddle
    the beams sit on and its base plate and bolts."""
    rs = end["rear"]
    hv = rs["halfV"]
    (b0, b1), bw_ = rs["bottom"], rs["bottomW"]
    (a0, a1), aw = rs["top"], rs["topW"]
    prims.prism(mb, [(m(b0), m(bw_)), (m(b1), m(bw_)), (m(a1), m(aw)), (m(a0), m(aw))], (0.0, m(-hv), 0.0), U, W, V, m(2 * hv),
                st["steel"])
    (h0, h1), hw = rs["head"], rs["headW"]
    prims.box(mb, (m(h0), m(-hv), m(aw)), (m(h1), m(hv), m(hw)), st["steel"])
    if lod <= 1:
        (s0, s1), (sw0, sw1), sv = rs["saddle"], rs["saddleW"], rs["saddleHalfV"]
        prims.box(mb, (m(s0), m(-sv), m(sw0)), (m(s1), m(sv), m(sw1)), st["steel"])
        (p0, p1), pv = rs["plate"], rs["plateHalfV"]
        prims.box(mb, (m(p0), m(-pv), m(rs["toc"])), (m(p1), m(pv), m(bw_)), st["galv"])
        if lod == 0:
            for bu in rs["bolts"]:
                for s in (-1, 1):
                    prims.cylinder(mb, (m(bu), m(s * (pv - 100)), m(bw_)), (m(bu), m(s * (pv - 100)), m(bw_ + 80)), 0.03, 6, st["rod"])


def pier(mb, pr, depth, lod, st):
    """A concrete pier from its top of concrete down depth (the game buries what it needs), with an upstand where drawn.
    Returns its top's centre (m) for the pier_top socket."""
    (u0, u1), hv, top = pr["u"], pr["halfV"], pr["toc"]
    prims.box(mb, (m(u0), m(-hv), m(top - depth)), (m(u1), m(hv), m(top)), st["concrete"], skip=("-z",))
    up = pr.get("upstand")
    if up and lod <= 2:
        prims.box(mb, (m(up["u"][0]), m(-hv), m(top)), (m(up["u"][1]), m(hv), m(up["top"])), st["concrete"])
    return (m((u0 + u1) / 2), 0.0, m(top))


def tilted(mb, angle_deg, centre_lift):
    """Turns every vertex of mb by angle about the lift frame's v axis (u toward w), then moves it to centre."""
    a = math.radians(angle_deg)
    ca, sa = math.cos(a), math.sin(a)
    cu_, cv_, cw_ = centre_lift
    out = []
    for p in mb.verts:
        u, v, w = frame.lift(p)
        out.append(frame.b((cu_ + u * ca - w * sa, cv_ + v, cw_ + u * sa + w * ca)))
    mb.verts = out
    return mb


# -- build ---------------------------------------------------------------------------------------------------------
def build(spec, lod, stage, variant="return"):
    c = spec["common"]
    t = dict(spec["terminal"], gauge=c["lineGauge"], ropeDia=c["ropeDiameter"])
    end = dict(t[variant], name=variant)
    st = styles(lod)
    a = Asset(f"sessellift_d6_{variant}")
    body = MeshBuilder()
    rope = c["ropeElevation"]
    hg = c["lineGauge"] / 2

    hood(body, t, end, lod, st)
    belly(body, t, end, lod, st)
    frame_steel(body, t, end, lod, st)
    chair_rail(body, t, end, lod, st)
    horns(body, t, end, lod, st)
    bend_sheaves(body, t, end, rope, lod, st)
    carriage(body, t, end, lod, st)
    front_mast(body, t, end, lod, st)
    rear_leg(body, t, end, lod, st)
    p1 = pier(body, end["pierFront"], t["pierDepth"], lod, st)
    p2 = pier(body, end["pierRear"], t["pierDepth"], lod, st)

    tilt = t["bullwheel"]["tilt"]
    bw_w = t["bullwheel"]["w"][variant]
    centre = (0.0, 0.0, m(bw_w))
    wheel = MeshBuilder()
    parts.bullwheel(wheel, Vector((0.0, 0.0, 0.0)), parts.bullwheel_spec(c), lod, st["dark"])
    tilted(wheel, tilt, centre)
    if lod <= 2:
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": centre, "axis": (-math.sin(math.radians(tilt)), 0.0, math.cos(math.radians(tilt)))}
    else:
        body.merge(wheel)

    a.body = body
    rail = t["rail"]
    grip_w = rail["w"] + rail["grip"][1]
    bend, E1 = end["ropeBend"], end["hood"][1]
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        # the rope: at the tilted wheel's tangent points, climbing in the wheel's plane to the bend, then level to the line
        "rope_left_bw": (0.0, -m(hg), m(bw_w)), "rope_right_bw": (0.0, m(hg), m(bw_w)),
        "rope_left_bend": (m(bend), -m(hg), m(rope)), "rope_right_bend": (m(bend), m(hg), m(rope)),
        "rope_left_out": (m(E1), -m(hg), m(rope)), "rope_right_out": (m(E1), m(hg), m(rope)),
        "pier_top_1": p1, "pier_top_2": p2,
    }
    ru, rv = end["riders"]
    a.sockets["chair_load" if variant == "return" else "chair_unload"] = (m(ru), m(rv), m(grip_w))
    # the chairs' grips round the station: on the rail, 134 inside its centreline, at the rope's height
    a.paths = {"chairs": [(m(u), m(v), m(grip_w)) for u, v in rail_path(t, end, inset=-rail["grip"][0], steps=8)]}
    a.dims.update({"rope": rope, "gauge": c["lineGauge"], "bullwheelW": bw_w, "bullwheelTilt": tilt,
                   "hoodBack": end["hood"][0], "hoodFront": E1, "frontMastU": end["frontMast"]["u"],
                   "railEnd": end["rail"]["end"], "railW": rail["w"], "ropeBend": bend, "driveEnd": c["driveEnd"]})
    return a
