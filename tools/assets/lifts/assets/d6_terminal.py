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
CHAIR = "d6_chair"   # the chair the build sends through the station (liftkit/fit.py)
VARIANTS = ("drive", "return")


def m(x):
    return x / 1000.0


def styles(lod):
    """Round 3 (the photos and the game's look): the hood is mirror-gloss livery, shaded smooth by surface (a smoothing
    group per part of the outline, so the band, the shoulder and the roof keep their edges), the skirt under the band
    gloss enough to throw back the snow, the flat crown a ribbed aluminium roof, the glass black. Under the roof
    nothing takes snow; the ribs between the panes take little."""
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("paint_grey", snow=0.5),
        "galv": Style("galvanised", snow=0.5),
        "dark": Style("paint_dark", snow=0.0),
        "hood": Style("livery_gloss", livery=1.0, snow=1.0, crease=30.0),
        "skirt": Style("livery_mirror", livery=1.0, snow=0.0, crease=30.0),
        "roof": Style("roof_alu", snow=1.0, crease=30.0),
        "white": Style("white_smooth", snow=0.0, group="belly", crease=40.0),
        "joint": Style("white", snow=0.0),
        "soffit": Style("white_smooth", snow=0.0),
        "trim": Style("trim_dark", snow=0.0),
        "glass": (Style("glass_black", slot=GLASS, group="glass", crease=30.0) if lod <= 1
                  else Style("glass_black", group="glass", crease=30.0)),
        "inner": Style("interior"),
        "lining": Style("interior"),
        "foot": Style("aluminium", snow=0.0),
        "sign": Style("sign_black", snow=0.0),
        "lamp": Style("sheave_red"),
        "grating": Style("grating", snow=0.8),
        "red": Style("sheave_red"),
        "rod": Style("machined"),
        "horn": Style("galvanised", snow=0.5),
        # under the roof: no snow (round 3: snow is by normal only, so indoor tops turned white)
        "rail": Style("galvanised"),
        "inside": Style("paint_grey"),
        "tyre": Style("rubber"),
        "guard": Style("sheave_red"),
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
    n = (16, 10, 10, 3)[lod]   # LOD2 keeps a round shoulder (round 3: 6 facets read as a polygon)
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
def twin(mb, pts_mm, style, outward, gap=2.0):
    """The back of a single-sided face: the same polygon `gap` mm behind it, facing the other way (round 3: through the
    glass, Unity culls a face's back, so an unlined soffit or wall showed the snow and sky behind it)."""
    pv = [Vector(p) for p in pts_mm]
    n = Vector((0.0, 0.0, 0.0))
    for i in range(len(pv)):
        a, c = pv[i], pv[(i + 1) % len(pv)]
        n += Vector(((a.y - c.y) * (a.z + c.z), (a.z - c.z) * (a.x + c.x), (a.x - c.x) * (a.y + c.y)))
    if n.length < 1e-9:
        return
    n.normalize()
    if n.dot(Vector(outward)) < 0:
        n = -n
    face(mb, [tuple(p - n * gap) for p in pv], style, tuple(-n))


def hood(mb, t, end, lod, st):
    """Skin (body and caps), the caps' steps, the rings, the end walls with their windows, the panel joints, the lining
    and the inner ribs, and the soffit."""
    h = t["hood"]
    E0, E1 = end["hood"]
    W0, W1 = end["windows"]
    seam0, seam1 = E0 + h["endWall"], E1 - h["endWall"]
    win = h["windows"]
    bay = win["bay"]
    nbays = round((W1 - W0) / bay)
    levels = (win["from"], win["to"], win["head"]) if lod <= 2 else (win["from"], win["to"])
    if lod == 0:   # the flip-up bays' transoms are their own bands, not whole facets of the arch
        levels += tuple(x for f in end["flipBays"] for tr in f["transoms"] for x in tr)
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

    def skin(a, b):
        """The outline segment a-b's finish and smoothing group: the skirt under the band, the band, the arch (where the
        windows are, little snow on the ribs and bands between them) or the flat crown's ribbed roof."""
        wm = (a[1] + b[1]) / 2
        if wm < h["band"][0] - 1:
            return st["skirt"].but(group="skirt")
        if wm <= band + 1:
            return st["hood"].but(group="band")
        if wm >= top - 1:
            return st["roof"].but(group="roof")
        if win["from"] - 60 <= wm <= win["to"] + 70:
            return st["hood"].but(group="arch", snow=0.1)
        return st["hood"].but(group="arch")

    # the body: the window section, split at every pane edge so panes are glass and mullions and bands livery; from
    # LOD2 one glass band each side
    panes = []
    if lod <= 1:
        heavy = end["frontMast"]["u"] + h["heavyMullion"]["at"]

        def half_mullion(u):
            return (h["heavyMullion"]["width"] if abs(u - heavy) < bay / 2 else win["mullion"]) / 2
        for k in range(nbays):
            u0, u1 = W0 + bay * k, W0 + bay * (k + 1)
            panes.append((u0 + half_mullion(u0), u1 - half_mullion(u1), k))
    else:
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
            face(mb, [P(ua, a), P(ua, b), P(ub, b), P(ub, a)], st["glass"] if glass else skin(a, b), out(a, b))
    # the caps: from the body's end to the end wall's seam, then on to the ring's front face; the step up from the body
    for side, wedge, ext, seam in ((-1, W0, E0, seam0), (1, W1, E1, seam1)):
        for a, b, ba, bb in zip(cap_f, cap_f[1:], body_f, body_f[1:]):
            fa, fb = ext - side * setback(h, *a), ext - side * setback(h, *b)
            face(mb, [P(wedge, a), P(wedge, b), P(seam, b), P(seam, a)], skin(a, b), out(a, b))
            face(mb, [P(seam, a), P(seam, b), P(fb, b), P(fa, a)], skin(a, b), out(a, b))
            if lod <= 2 and (abs(ba[0] - a[0]) > 1 or abs(bb[0] - b[0]) > 1):
                face(mb, [P(wedge, ba), P(wedge, bb), P(wedge, b), P(wedge, a)], st["hood"], (-side, 0.0, 0.0))
    if lod == 2:   # the ribs between the bays, proud of the one glass band (round 3: LOD2 lost the hood's rhythm)
        glaze = [p for p in body if win["from"] - 1 <= p[1] <= win["to"] + 1]
        pick = [glaze[0], glaze[len(glaze) // 3], glaze[2 * len(glaze) // 3], glaze[-1]]
        rib = win["mullion"] / 2
        lift = h["ribLift"]
        for k in range(nbays + 1):
            uk = W0 + bay * k
            for s in (1, -1):
                for a, b in zip(pick, pick[1:]):
                    a2, b2 = (s * a[0], a[1]), (s * b[0], b[1])
                    o = Vector((0.0, s * (b[1] - a[1]), -(b[0] - a[0]))).normalized()   # outward on either side
                    pa = (a2[0] + o.y * lift, a2[1] + o.z * lift)
                    pb = (b2[0] + o.y * lift, b2[1] + o.z * lift)
                    face(mb, [P(uk - rib, pa), P(uk - rib, pb), P(uk + rib, pb), P(uk + rib, pa)], st["hood"].but(snow=0.1),
                         tuple(o))
    ring(mb, h, cap, E0, E1, seam0, seam1, lod, st)
    end_wall(mb, h, cap, seam0, -1, False, lod, st)
    end_wall(mb, h, cap, seam1, 1, True, lod, st)
    if lod <= 1:
        # panel joints on the skirt and the band, every second bay line (round 3: no 1 m seam grid, joints only where
        # the photographed panels break)
        sb = [i for i, (a, b) in enumerate(zip(body_f, body_f[1:])) if (a[1] + b[1]) / 2 <= band + 1]
        jh = h["joint"] / 2
        for k in range(0, nbays + 1, 2):
            uj = W0 + bay * k
            for i in sb:
                a, b = body_f[i], body_f[i + 1]
                o = Vector(out(a, b)).normalized()
                pa, pb = (a[0] + o.y * 2, a[1] + o.z * 2), (b[0] + o.y * 2, b[1] + o.z * 2)
                face(mb, [P(uj - jh, pa), P(uj - jh, pb), P(uj + jh, pb), P(uj + jh, pa)], st["trim"], tuple(o))
        # the lining, a hand's width inside, only where there is no glass: through the glazing the far side's glass,
        # its inner ribs and their bracing show, crossing the near ribs as the photographed lattice
        half_in = offset(body, h["lining"])
        lin_f = full(half_in)
        for a, b in zip(lin_f, lin_f[1:]):
            wm = (a[1] + b[1]) / 2
            if win["from"] - 30 < wm < win["to"] + 30:
                continue
            o = out(a, b)
            face(mb, [P(seam0, a), P(seam0, b), P(seam1, b), P(seam1, a)], st["lining"], (0.0, -o[1], -o[2]))
        rib = win["mullion"] / 2
        for k in range(nbays + 1):
            uk = W0 + bay * k
            for a, b in zip(lin_f, lin_f[1:]):
                if win["from"] - 1 < (a[1] + b[1]) / 2 < win["to"] + 1:
                    o = out(a, b)
                    face(mb, [P(uk - rib, a), P(uk - rib, b), P(uk + rib, b), P(uk + rib, a)], st["trim"], (0.0, -o[1], -o[2]))
        if lod == 0:   # X bracing in the upper part of each bay, facing in
            xb = h["bracing"]
            wl, wh = xb["w"]
            bh = xb["width"] / 2
            for s in (1, -1):
                lo, hi = (s * inner_v(half_in, wl), wl), (s * inner_v(half_in, wh), wh)
                inward = (0.0, -s, 0.0)
                for k in range(nbays):
                    ua, ub = W0 + bay * k, W0 + bay * (k + 1)
                    for (u0, p0), (u1, p1) in (((ua, lo), (ub, hi)), ((ua, hi), (ub, lo))):
                        face(mb, [(u0, p0[0], p0[1] - bh), (u1, p1[0], p1[1] - bh), (u1, p1[0], p1[1] + bh), (u0, p0[0], p0[1] + bh)],
                             st["trim"], inward)
    soffit(mb, t, end, cap, body, seam0, seam1, lod, st)


def ring(mb, h, cap, E0, E1, seam0, seam1, lod, st):
    """Each end's ring: its front face a rounded bead from the outline in to its inner edge, standing `bead` proud at
    its middle (round 3: the photos' thick rim with one continuous highlight), a bright cap at each foot, and its
    soffit back to the end wall."""
    rg = h["ring"]
    rw, bead = rg["width"], rg["bead"]
    n = (6, 4, 3, 1)[lod]
    halves = [offset(cap, rw * k / n) for k in range(n + 1)]
    rings = [full(r) for r in halves]
    inner = rings[-1]
    rs = st["hood"].but(group="ring", crease=50.0)
    for ext, side, seam in ((E0, -1, seam0), (E1, 1, seam1)):
        def F(p, lift=0.0):
            return (ext - side * (setback(h, *p) - lift), p[0], p[1])
        for k, (ra, rb) in enumerate(zip(rings, rings[1:])):
            la, lb = bead * math.sin(math.pi * k / n), bead * math.sin(math.pi * (k + 1) / n)
            for i in range(len(ra) - 1):
                face(mb, [F(ra[i], la), F(ra[i + 1], la), F(rb[i + 1], lb), F(rb[i], lb)], rs, (side, 0.0, 0.0))
        for a, b in zip(inner, inner[1:]):   # the ring's soffit, back to the end wall, facing the opening
            face(mb, [F(a), F(b), (seam, b[0], b[1]), (seam, a[0], a[1])], st["hood"], (0.0, -(b[1] - a[1]), b[0] - a[0]))
        for s in (1, -1):   # the ring's underside along the bottom edge
            o, i_ = cap[0], halves[-1][0]
            face(mb, [(seam, s * i_[0], o[1]), (seam, s * o[0], o[1]), F((s * o[0], o[1])), F((s * i_[0], o[1]))],
                 st["white"], (0.0, 0.0, -1.0))
            if lod <= 1:   # the polished cap at the ring's foot
                fu = F((s * o[0], o[1]))[0]
                v0, v1 = sorted((s * i_[0], s * o[0]))
                ua, ub = sorted((fu - side * 40, fu + side * (bead / 2 + 15)))
                prims.box(mb, (m(ua), m(v0), m(o[1])), (m(ub), m(v1), m(o[1] + rg["foot"])), st["foot"])


def end_wall(mb, h, cap, u, side, line_end, lod, st):
    """A flat end wall at u inside the ring, with its window (frame, glass set back, mullions); at the line end the wall
    is open low on both sides, where the chairs, the rope and the horns pass."""
    ew = h["endWindow"]
    inner = offset(cap, h["ring"]["width"])
    bot = inner[0][1]
    fv, fw0, fw1 = ew["frame"]
    gv, gw0, gw1 = ew["glass"]
    nrm = (side, 0.0, 0.0)
    gb = ew["guard"]
    bars = [gw0 + (gw1 - gw0) * f for f in gb["at"]]
    bar_u = u + side * gb["out"]

    def wall(poly, style=None, du=0.0):
        pts = [(u - side * du, v, w) for v, w in poly]
        face(mb, pts, style or st["hood"], nrm)
        if style is None and lod <= 1:
            twin(mb, pts, st["inner"], nrm)

    def right(w0, w1):
        return [(v, w) for v, w in inner if w0 < w < w1]

    if lod >= 2:
        wall(full(inner))
        wall([(-gv, gw0), (gv, gw0), (gv, gw1), (-gv, gw1)], st["glass"], -20.0)
        if lod == 2:   # the guard bars as one box
            ua, ub = sorted((bar_u - 25, bar_u + 25))
            prims.box(mb, (m(ua), m(-gb["half"]), m(bars[0] - 25)), (m(ub), m(gb["half"]), m(bars[-1] + 25)), st["galv"])
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
    # the guard bars across the middle panes and their header rail, in front of the glass (round 3: the brightest
    # accents on a dark end at 20-150 m), on brackets back to the wall
    hb = gb["half"]
    for w in bars:
        prims.cylinder(mb, (m(bar_u), m(-hb), m(w)), (m(bar_u), m(hb), m(w)), m(gb["dia"]) / 2, parts.sides(lod, 10, 6), st["galv"])
    wh = gw0 + (gw1 - gw0) * gb["header"]
    ua, ub = sorted((bar_u - 5, bar_u + 5))
    prims.box(mb, (m(ua), m(-hb), m(wh - 40)), (m(ub), m(hb), m(wh + 40)), st["galv"])
    for bv in (-hb, hb):
        ua, ub = sorted((u, bar_u))
        prims.box(mb, (m(ua), m(bv - 15), m(bars[0] - 20)), (m(ub), m(bv + 15), m(wh + 40)), st["galv"])
    if not line_end and lod <= 1:   # the maker's plate on the back wall under the window (a fictional name: no text)
        pl = ew["plate"]
        ua, ub = sorted((u, u + side * 15))
        prims.box(mb, (m(ua), m(-pl["half"]), m(fw0 - pl["w"][1])), (m(ub), m(pl["half"]), m(fw0 - pl["w"][0])), st["sign"])
        ua, ub = sorted((u + side * 15, u + side * 20))
        prims.box(mb, (m(ua), m(-pl["half"] + 60), m(fw0 - pl["w"][1] + 70)), (m(ub), m(pl["half"] - 60), m(fw0 - pl["w"][1] + 110)),
                  st["foot"])


def fittings(mb, t, end, lod, st):
    """What the photos show on and round the hood (round 3): on the flat crown, lifeline tripods at both ends with their
    cable, the low box at the line end with its A-frame mast, small roof units; the red lamps at the back end's bottom
    corners and inside the line end's ring."""
    if lod >= 3:
        return
    h = t["hood"]
    rk, top = h["roofKit"], h["roof"]
    E0, E1 = end["hood"]
    ph = rk["postHeight"]
    posts = (E0 + rk["postIn"], E1 - rk["postIn"])
    for pu in posts:
        if lod <= 1:
            for du, dv in ((-300, -250), (-300, 250), (300, 0)):
                prims.cylinder(mb, (m(pu + du), m(dv), m(top)), (m(pu), 0.0, m(top + ph)), 0.02, 6 if lod == 0 else 4, st["galv"],
                               caps=(False, False))
        else:
            prims.box(mb, (m(pu - 30), -0.03, m(top)), (m(pu + 30), 0.03, m(top + ph)), st["galv"])
    if lod == 0:
        prims.cylinder(mb, (m(posts[0]), 0.0, m(top + ph)), (m(posts[1]), 0.0, m(top + ph)), 0.004, 4, st["galv"], caps=(False, False))
    bx = rk["box"]
    bu0 = E1 - bx["from"]
    bu1 = bu0 + bx["length"]
    bt = top + bx["height"]
    prims.box(mb, (m(bu0), m(-bx["half"]), m(top)), (m(bu1), m(bx["half"]), m(bt)), st["roof"].but(group=None))
    mu, mh = (bu0 + bu1) / 2, rk["mast"]["height"]
    if lod <= 1:
        for dv in (-350, 350):
            for du in (-250, 250):
                prims.cylinder(mb, (m(mu + du), m(dv), m(bt)), (m(mu), m(dv * 0.2), m(bt + mh)), 0.025, 6 if lod == 0 else 4,
                               st["galv"], caps=(False, False))
    else:
        prims.box(mb, (m(mu - 30), -0.1, m(bt)), (m(mu + 30), 0.1, m(bt + mh)), st["galv"])
    mid = (E0 + E1) / 2
    ul, uw, uh = rk["unit"]
    for du, dv in rk["units"]:
        prims.box(mb, (m(mid + du - ul / 2), m(dv - uw / 2), m(top)), (m(mid + du + ul / 2), m(dv + uw / 2), m(top + uh)), st["inside"].but(snow=1.0))
    if lod == 0:
        lp = h["lamps"]
        for (du, lv, lw), uu in ((lp["back"], E0 + lp["back"][0]), (lp["lineRing"], E1 - lp["lineRing"][0])):
            for s in (-1, 1):
                prims.box(mb, (m(uu - 40), m(s * lv - 30), m(lw - 25)), (m(uu + 40), m(s * lv + 30), m(lw + 25)), st["lamp"])


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
    if lod >= 3:   # from that far, one plain strip under each part of the hood
        for ua, ub in zip((seam0, W0, W1), (W0, W1, seam1)):
            e = body[0][0] if W0 <= (ua + ub) / 2 <= W1 else cap[0][0]
            face(mb, [(ua, -e, w), (ua, e, w), (ub, e, w), (ub, -e, w)], st["soffit"], (0.0, 0.0, -1.0))
        return
    cu, cv, rc, rv = rail_shape(t, end)
    r0, r1 = rc + sl["offset"] - sl["width"] / 2, rc + sl["offset"] + sl["width"] / 2
    stops = {seam0, seam1, W0, W1, cu, cu - r0, cu - r1}
    k = cu - r1
    while k < cu:
        stops.add(k)
        k += 250 if lod <= 1 else 800
    if lod <= 1:   # a vertex every metre along the station, so the baked AO has somewhere to fall (round 3: 20 m strips)
        stops |= {float(x) for x in range(int(seam0) // 1000 * 1000, int(seam1), 1000)}
    la = end["frontMast"]["ladder"]
    hatch = (end["frontMast"]["u"] + la["hatch"][0], end["frontMast"]["u"] + la["hatch"][1])
    if lod <= 1:
        stops |= set(hatch)
    # the belly's open top: the soffit stops at the white tub's edge (round 2: it spanned the tub, through the wheel)
    bh, bback, bcentre, bnose = belly_top(t, end)
    stops |= {bback, bcentre, bnose}
    k = bback
    while k < bcentre:
        stops.add(k)
        k += 200 if lod <= 1 else 600
    stops = sorted(s for s in stops if seam0 <= s <= seam1)

    def keep(u):
        edge = body[0][0] if W0 <= u <= W1 else cap[0][0]
        cut = list(slot_intervals(t, end, u))
        if lod <= 1 and hatch[0] <= u <= hatch[1]:
            cut.append((0.0, sl["hatchHalf"]))
        if bback < u < bnose:
            cut.append((0.0, bh if u >= bcentre else math.sqrt(max(0.0, bh * bh - (bcentre - u) ** 2))))
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
            quads = ([[(ua, -a1, w), (ua, a1, w), (ub, b1, w), (ub, -b1, w)]] if a0 == 0.0   # a strip across the middle
                     else [[(ua, s * a0, w), (ua, s * a1, w), (ub, s * b1, w), (ub, s * b0, w)] for s in (1, -1)])
            for q in quads:
                face(mb, q, st["soffit"], (0.0, 0.0, -1.0))
                if lod <= 1:   # its inside, dark, for the view down through the glass
                    twin(mb, q, st["inner"], (0.0, 0.0, -1.0))


# -- belly -------------------------------------------------------------------------------------------------------
def belly_top(t, end):
    """The belly's top edge in plan (mm): its half-width (the wall's plus its cove), the back, the round back's centre
    and the nose."""
    b = t["belly"]
    h = b["section"][0][0] + b["cove"]
    back = end["belly"]["backs"][0]
    return h, back, back + h, max(end["belly"]["wedge"])


def belly(mb, t, end, lod, st):
    """The white housing under the soffit round the bullwheel: the end view's section (a tray over a keel), each level's
    back round in plan, straight to the line end, then a wedge rising into the soffit. Round 3 (the photos' smooth drum):
    a cove where the wall meets the soffit, a lip at the wall's foot, panel joints, smooth shading, an inside for the
    view down through the glass, and the boxy chin round the rear leg's top."""
    b = t["belly"]
    sec = [tuple(p) for p in b["section"]]
    be = end["belly"]
    backs, uf, wedge = be["backs"], be["front"], be["wedge"]
    keel_top = be.get("keelTop", backs[1])
    top = sec[0][1]
    nose = max(wedge)
    fr = (1.0, 0.75, 0.5, 0.25, 0.0) if lod <= 1 else (1.0, 0.0)
    extra = {x for x in wedge if uf < x < nose} if lod <= 1 else set()   # every level's end is a station (no slivers)
    stations = sorted({uf + (nose - uf) * f for f in fr} | extra, reverse=True)
    kc = (16, 8, 8, 4)[lod]
    (h0, _), (_, w1) = sec[0], sec[1]
    cv_, (lo_, lh) = b["cove"], b["lip"]
    # the levels (half-width, height, the wedge end it rises by): the cove, the wall down to the lip, then the keel
    cove = [(h0 + cv_ + cv_ * math.cos(a), top - cv_ + cv_ * math.sin(a), wedge[1])
            for a in ((math.pi / 2, math.pi * 3 / 4, math.pi) if lod <= 1 else (math.pi / 2, math.pi))]
    cove[0] = (h0 + cv_, top, wedge[0])
    wall = [(h0, w1 + lh, wedge[1]), (h0 + lo_, w1 + lh, wedge[1]), (h0 + lo_, w1, wedge[1])] if lod <= 1 else [(h0, w1, wedge[1])]
    tray_levels = cove + wall
    keel_levels = [tray_levels[-1], (sec[2][0], sec[2][1], wedge[2]), (sec[3][0], sec[3][1], wedge[3])]

    def level_w(wj, we, u):
        if we <= uf or u <= uf:
            return wj
        return wj + (top - wj) * min(1.0, (u - uf) / (we - uf))

    def ring(level, back):
        hj, wj, we = level
        cu = back + hj
        pts = [(u, hj, level_w(wj, we, u)) for u in stations]
        for i in range(kc + 1):
            a = math.pi / 2 + math.pi * i / kc
            pts.append((cu + hj * math.cos(a), hj * math.sin(a), wj))
        pts += [(u, -hj, level_w(wj, we, u)) for u in reversed(stations)]
        return pts

    inside = Vector(((min(backs) + uf) / 2, 0.0, 2650.0))

    def skin(q, outward):
        face(mb, q, st["white"], outward)
        if lod <= 1:
            twin(mb, q, st["inner"], outward)

    def loft(ra, rb):
        for i in range(len(ra) - 1):
            q = [ra[i], ra[i + 1], rb[i + 1], rb[i]]
            span = max(abs(q[0][2] - q[3][2]), abs(q[1][2] - q[2][2]), abs(q[0][1] - q[3][1]), abs(q[1][1] - q[2][1]),
                       abs(q[0][0] - q[3][0]), abs(q[1][0] - q[2][0]))
            if span < 1.0:   # a level that has already risen into the soffit: no band left
                continue
            c = Vector((sum(p[0] for p in q) / 4, sum(p[1] for p in q) / 4, sum(p[2] for p in q) / 4))
            skin(q, tuple(c - inside))

    # the tray (its walls, back to backs[1]) over the keel (from keelTop down); where the tray reaches further back
    # than the keel (the drive) its bottom is flat behind the keel, whose back drops plumb (round 2)
    tray = [ring(lv, backs[0] if k < len(cove) else backs[1]) for k, lv in enumerate(tray_levels)]
    keel = [ring(keel_levels[0], keel_top), ring(keel_levels[1], backs[2]), ring(keel_levels[2], backs[3])]
    for ra, rb in zip(tray, tray[1:]):
        loft(ra, rb)
    if keel_top != backs[1]:
        down = backs[1] < keel_top
        for i in range(len(tray[-1]) - 1):
            q = [tray[-1][i], tray[-1][i + 1], keel[0][i + 1], keel[0][i]]
            pts = [Vector(p) for p in q]
            if (pts[1] - pts[0]).cross(pts[3] - pts[0]).length + (pts[1] - pts[2]).cross(pts[3] - pts[2]).length < 1e3:
                continue   # the straights: the two outlines coincide
            skin(q, (0.0, 0.0, -1.0 if down else 1.0))
    for ra, rb in zip(keel, keel[1:]):
        loft(ra, rb)
    low = keel[-1]
    n = len(low)
    k = len(stations)
    skin(low[k - 1:n - k + 1], (0.0, 0.0, -1.0))
    for i in range(k - 1):
        skin([low[i], low[i + 1], low[n - 2 - i], low[n - 1 - i]], (0.3, 0.0, -1.0))
    if lod <= 1:   # the panel joints on the tray's walls (the side view)
        jh = b["jointWidth"] / 2
        wa, wb = (w1 + lh), top - cv_
        for ju in b["joints"]:
            if backs[0] + h0 <= ju <= uf:
                for s in (1, -1):
                    vv = s * (h0 + 2)
                    face(mb, [(ju - jh, vv, wa), (ju + jh, vv, wa), (ju + jh, vv, wb), (ju - jh, vv, wb)], st["joint"], (0.0, s, 0.0))
    # the chin: a white box round the rear leg where it rises into the keel, with the maker's plate, a spot and a lamp
    rs, ch = end["rear"], b["chin"]
    w3 = sec[3][1]
    f = (w3 - rs["bottomW"]) / (rs["topW"] - rs["bottomW"])
    ua = rs["bottom"][0] + (rs["top"][0] - rs["bottom"][0]) * f - ch["pad"]
    ub = rs["bottom"][1] + (rs["top"][1] - rs["bottom"][1]) * f + ch["pad"]
    hv = rs["halfV"] + ch["pad"]
    prims.box(mb, (m(ua), m(-hv), m(w3 - ch["height"])), (m(ub), m(hv), m(w3)), st["white"].but(group=None))
    if lod <= 1:
        wm = w3 - ch["height"] / 2
        prims.box(mb, (m(ua - 12), m(-200), m(wm - 70)), (m(ua), m(200), m(wm + 70)), st["sign"])
        prims.cylinder(mb, (m(ua), m(-hv + 90), m(wm + 120)), (m(ua - 60), m(-hv + 90), m(wm + 120)), 0.05, 10, st["foot"],
                       fan_caps=True)
        prims.cylinder(mb, (m(ua), m(hv - 70), m(wm + 120)), (m(ua - 40), m(hv - 70), m(wm + 120)), 0.02, 8, st["lamp"],
                       fan_caps=True)


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
    if lod >= 3:   # hidden in the hood from that far (round 3: LOD3 spent its triangles on them)
        return
    for s in (-1, 1):   # the two main beams, inclined with the wheel
        c = Vector((m(mid), s * m(bv), m(beam_w(t, end, mid) + bd / 2)))
        prims.obox(mb, c, (d, V, nrm), (m((u1 - u0) / d.x) / 2, m(bw) / 2, m(bd) / 2), st["inside"])
    ps = f["postSize"]
    for pu, pw0, pw1 in fe["posts"]:   # posts from the beams' ends up to the roof
        for s in (-1, 1):
            prims.box(mb, (m(pu - ps / 2), m(s * bv - ps / 2), m(pw0)), (m(pu + ps / 2), m(s * bv + ps / 2), m(pw1)), st["inside"])
    ow0, ow1 = f["overhead"]["w"]
    for s in (-1, 1):
        prims.box(mb, (m(fe["overheadU"][0]), m(s * bv - 80), m(ow0)), (m(fe["overheadU"][1]), m(s * bv + 80), m(ow1)), st["inside"])
    # where the frame meets the masts (round 2: it touched neither): at the back a cross-head on the leg's head under the
    # beams' ends and the rear posts; at the front a cross-head on the column's head, a drop post under each beam and a
    # bracket from the beam's end to the front post
    rs, fm = end["rear"], end["frontMast"]
    half = bv + bw / 2
    rear_post = min(fe["posts"], key=lambda p: p[0])
    prims.box(mb, (m(rs["head"][0]), -m(half), m(rs["headW"])), (m(rs["head"][1]), m(half), m(rear_post[1])), st["inside"])
    front_post = max(fe["posts"], key=lambda p: p[0])
    mu, head1 = fm["u"], fm["head"]["w"][1]
    xa, xb = mu - 286, mu + 4
    prims.box(mb, (m(xa), -m(half), m(head1)), (m(xb), m(half), m(head1 + 200)), st["inside"])
    for s in (-1, 1):
        prims.box(mb, (m(xa + 50), m(s * bv - 120), m(head1 + 200)), (m(xb - 50), m(s * bv + 120), m(front_post[1])), st["inside"])
        prims.box(mb, (m(u1 - 24), m(s * bv - bw / 2), m(front_post[1])), (m(mu + 191), m(s * bv + bw / 2), m(front_post[1] + 174)),
                  st["inside"])
    if lod >= 2:
        return
    portals(mb, t, end, lod, st)
    floor(mb, t, end, lod, st)


def portals(mb, t, end, lod, st):
    """One inverted-U frame over each grip path at each portal (the end views and the isometric): an inner leg inboard
    of the grips, an outer leg outboard of the guide rollers with a knee into its top beam; nothing across the middle.
    Near the bullwheel the inner legs start above its spokes; round a corner of the loop they stand inside it."""
    po = t["frame"]["portals"]
    hs = po["size"] / 2
    pw0, pw1 = po["w"]
    top0 = pw1 - po["size"]
    o0, o1 = po["outer"]
    (_, cw0), (cv1, cw1) = po["chamfer"]
    cu, cv, rc, _ = rail_shape(t, end)
    rg = rc + t["rail"]["grip"][0]                  # the grips' radius round a corner
    floor_top = t["frame"]["floor"]["w"][1]
    for pu in end["frame"]["portals"]:
        i0, i1 = po["inner"]
        dx = cu - pu + hs
        if pu < cu and dx < rg:   # the grips pass at v_grip here: the leg stands 150 inside them
            v_grip = cv + math.sqrt(rg * rg - dx * dx)
            i1 = min(i1, v_grip - 150)
            i0 = i1 - po["size"]
        bottom = po["innerFrom"] if math.hypot(pu, i0) < t["gauge"] / 2 + 400 else floor_top
        for s in (-1, 1):
            a, b = sorted((s * i0, s * i1))
            prims.box(mb, (m(pu - hs), m(a), m(bottom)), (m(pu + hs), m(b), m(top0)), st["inside"])
            leg = [(o0, pw0), (o1, pw0), (o1, cw0), (cv1, cw1), (cv1, top0), (o0, po["knee"])]
            prims.prism(mb, [(m(s * a), m(b)) for a, b in leg], (m(pu - hs), 0.0, 0.0), V, W, U, m(po["size"]), st["inside"])
            a, b = sorted((s * i0, s * cv1))
            prims.box(mb, (m(pu - hs), m(a), m(top0)), (m(pu + hs), m(b), m(pw1)), st["inside"])


def floor(mb, t, end, lod, st):
    """The station floor (a grated deck, no snow under the roof). Along the straights it runs between the belly's
    opening and the rail; behind the corners' centres it stays inside the hangers' slot round the loop (round 2: the
    hangers cut through a rectangular floor there). No edge channels over the bullwheel, whose spokes pass under."""
    f, fe = t["frame"], end["frame"]
    fl, sl = f["floor"], t["hood"]["soffit"]
    w0, w1 = fl["w"]
    cu, cv, rc, _ = rail_shape(t, end)
    r_in = rc + sl["offset"] - sl["width"] / 2
    g0, g1 = fe["floorGap"]
    wheel = t["gauge"] / 2 + 500

    def deck(ua, ub, va, vb):
        over_wheel = ub > -wheel and ua < wheel and min(abs(va), abs(vb)) < wheel
        parts.deck(mb, (m(ua), m(va), m(w0)), (m(ub), m(vb), m(w1)), lod, st["grating"], snow=0.0, channels=not over_wheel)

    for s in (-1, 1):
        a, b = sorted((s * fl["v"][0], s * fl["v"][1]))
        for ua, ub in ((cu, -wheel), (-wheel, g0), (g1, fe["floorU"][1])):   # open over the wheel's front: its rim
            if ub > ua:                                                         # rises through the floor's level there
                deck(ua, ub, a, b)
    step = 400 if lod == 0 else 800
    ub = cu
    while ub > max(fe["floorU"][0], cu - r_in):
        ua = max(ub - step, cu - r_in)
        dx = cu - ua
        if dx < r_in:
            vh = min(fl["v"][1], cv + math.sqrt(r_in * r_in - dx * dx) - 20)
            if vh > 150:
                deck(ua, ub, -vh, vh)
        ub = ua


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


def bar_path(mb, pts, width, height, style):
    """A rectangular bar (width across, height up) swept along a polyline of lift-frame points (mm), mitred at its
    bends, ends capped."""
    n = len(pts)
    secs = []
    for i, (u, v, w) in enumerate(pts):
        d_in = Vector((u - pts[i - 1][0], v - pts[i - 1][1], 0.0)).normalized() if i > 0 else None
        d_out = Vector((pts[i + 1][0] - u, pts[i + 1][1] - v, 0.0)).normalized() if i < n - 1 else None
        tng = ((d_in or d_out) + (d_out or d_in)).normalized()
        side = Vector((-tng.y, tng.x, 0.0)) * (width / 2 / max(0.5, (d_in or tng).dot(tng)))
        secs.append([(u + side.x * a, v + side.y * a, w + b * height / 2) for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1))])
    for i in range(n - 1):
        sa, sb = secs[i], secs[i + 1]
        mid = [(sa[k][0] + sb[k][0]) / 2 for k in range(4)], [(sa[k][1] + sb[k][1]) / 2 for k in range(4)]
        cu_ = sum(mid[0]) / 4
        cv_ = sum(mid[1]) / 4
        cw_ = (sa[0][2] + sa[2][2] + sb[0][2] + sb[2][2]) / 4
        for k in range(4):
            k1 = (k + 1) % 4
            q = [sa[k], sa[k1], sb[k1], sb[k]]
            c = [sum(p[j] for p in q) / 4 for j in range(3)]
            face(mb, q, style, (c[0] - cu_, c[1] - cv_, c[2] - cw_))
    for sec, nxt in ((secs[0], secs[1]), (secs[-1], secs[-2])):
        c = [sum(p[j] for p in sec) / 4 - sum(p[j] for p in nxt) / 4 for j in range(3)]
        face(mb, sec, style, tuple(c))


def chair_rail(mb, t, end, lod, st):
    """The chair rail the grips' running wheels stand on: a flat-topped bar round the loop, its ends dropping by
    lead.drop over lead.length so the arriving wheels land on it (#rail, the fit check's contact for the wheels). It is
    carried from below (#rail_posts): posts from the floor and, at the portals along the straights, brackets from the
    inner legs (round 2: hangers from above stood in every grip's path)."""
    rail = t["rail"]
    if lod >= 3:
        return
    sw, sh = rail["section"]
    w = rail["w"]
    lead = rail["lead"]
    loop = rail_path(t, end, steps=6 if lod == 0 else 3)
    (ue, ve), (ue2, ve2) = loop[0], loop[-1]
    pts = ([(ue, ve, w - lead["drop"]), (ue - lead["length"], ve, w)] + [(u, v, w) for u, v in loop[1:-1]]
           + [(ue2 - lead["length"], ve2, w), (ue2, ve2, w - lead["drop"])])
    bar_path(mb, pts, sw, sh, st["rail"])
    if lod >= 2:
        return
    under = w - sh / 2
    floor_top = t["frame"]["floor"]["w"][1]
    ps = rail["posts"]["size"] / 2
    with mb.grouped("#rail_posts"):
        along, nxt = 0.0, rail["posts"]["every"] / 2
        for (ua, va), (ub, vb) in zip(loop, loop[1:]):
            seg = math.hypot(ub - ua, vb - va)
            while nxt <= along + seg:
                f = (nxt - along) / seg
                pu, pv = ua + (ub - ua) * f, va + (vb - va) * f
                prims.box(mb, (m(pu - ps), m(pv - ps), m(floor_top)), (m(pu + ps), m(pv + ps), m(under)), st["inside"])
                nxt += rail["posts"]["every"]
            along += seg
        po = t["frame"]["portals"]
        bw0, bw1 = rail["bracketW"][0], under
        for pu in end["frame"]["portals"]:
            if end["rail"]["corner"][0] <= pu <= end["rail"]["end"]:
                for s in (-1, 1):
                    a, b = sorted((s * po["inner"][1], s * (rail["v"] + sw / 2)))
                    prims.box(mb, (m(pu - 40), m(a), m(bw0)), (m(pu + 40), m(b), m(bw1)), st["inside"])


def horns(mb, t, end, lod, st):
    """The hanger guides at the line end, one each side from inside the end wall out past the hood: a channel open
    toward the rope that the hanger's guide roller runs in (round 2: solid wedges swallowed it). A galvanised web on the
    drawn fish-tail outline, its outer face flaring toward the tip; along the outline's lower edge a guide under the
    roller (ending where that edge rises toward the tip), along its upper edge one over it; both reach in to vInner."""
    if lod >= 3:
        return
    hn = t["horns"]
    E = end["hood"][1]
    poly = [(E + du, w) for du, w in hn["outline"]]
    root, tip = min(p[0] for p in poly), max(p[0] for p in poly)
    wind = prims.winding(poly)
    web = hn["web"]
    g = hn["guides"]

    def vo(u):
        f = min(1.0, max(0.0, (u - root) / (tip - root)))
        return hn["vOuter"][0] + (hn["vOuter"][1] - hn["vOuter"][0]) * f
    k_tip = max(range(len(poly)), key=lambda k: poly[k][0])
    lower = poly[:k_tip + 1]   # the root's bottom corner out to the tip
    upper = poly[k_tip:]       # the tip back to the root's top corner
    for s in (-1, 1):
        inner = [(u, s * (vo(u) - web), w) for u, w in poly]
        outer = [(u, s * vo(u), w) for u, w in poly]
        face(mb, inner, st["horn"], (0.0, -s, 0.0))
        face(mb, outer, st["horn"], (0.0, s, 0.0))
        n = len(poly)
        for i in range(n):
            j = (i + 1) % n
            (ua, wa), (ub, wb) = poly[i], poly[j]
            face(mb, [inner[i], inner[j], outer[j], outer[i]], st["horn"], ((wb - wa) * wind, 0.0, -(ub - ua) * wind))
        if lod >= 2:
            continue
        # the guides: plates along the outline's edges, from vInner out into the web
        for edge, thick, sign in ((lower, g["lowerThick"], 1), (upper, g["upperThick"], -1)):
            for (ua, wa), (ub, wb) in zip(edge, edge[1:]):
                if sign > 0 and max(wa, wb) + thick > g["lowerTop"]:
                    break   # the lower edge has risen to the roller: the guide ends, the web flares on alone
                vi, vx = s * hn["vInner"], s * (vo((ua + ub) / 2) - web + 5)
                a = [(ua, vi, wa), (ub, vi, wb), (ub, vx, wb), (ua, vx, wa)]
                b = [(u, v, w + sign * thick) for u, v, w in a]
                face(mb, a, st["horn"], (0.0, 0.0, -sign))
                face(mb, b, st["horn"], (0.0, 0.0, sign))
                face(mb, [a[0], a[1], b[1], b[0]], st["horn"], (0.0, -s, 0.0))
                face(mb, [a[0], b[0], b[3], a[3]], st["horn"], (-1.0, 0.0, 0.0))
                face(mb, [a[1], a[2], b[2], b[1]], st["horn"], (1.0, 0.0, 0.0))


def rope_w(t, end, rope, u):
    """The rope's height at u as the game draws it (socket to socket): straight from the wheel's tangent point up to the
    bend, level beyond."""
    bw = t["bullwheel"]["w"][end["name"]]
    return bw + (rope - bw) * max(0.0, u) / end["ropeBend"] if u < end["ropeBend"] else rope


def bend_sheaves(mb, t, end, rope, lod, st):
    """The rope's support sheaves round its bend from the wheel's plane to level: three a rope (drawn at the drive; the
    return needs them as much, round 3). Each turns on a stub axle from inboard: a hanger plate inboard of the grips'
    jaws and a cross member out from the main beam (round 2: boxes up from the axles stood in every grip's path)."""
    sh = end.get("bendSheaves")
    if not sh or lod >= 2:
        return
    sv, f = t["sheave"], t["frame"]
    hg = t["gauge"] / 2
    pv = hg - sv["width"] / 2 - sv["mount"]   # the hanger plate's outer face
    for u in sh["u"]:
        wc = rope_w(t, end, rope, u) - t["ropeDia"] / 2 - sv["dia"] / 2
        bw = beam_w(t, end, u)
        for s in (-1, 1):
            c = Vector((m(u), m(s * hg), m(wc)))
            with mb.grouped("#bend_sheaves"):
                prims.cylinder(mb, c - V * m(sv["width"]) / 2, c + V * m(sv["width"]) / 2, m(sv["dia"]) / 2,
                               parts.sides(lod, 20, 12), st["red"], fan_caps=True)
            with mb.grouped("#bend_brackets"):
                a = Vector((m(u), m(s * (hg - sv["width"] / 2)), m(wc)))
                b = Vector((m(u), m(s * (pv - 20)), m(wc)))
                prims.cylinder(mb, a, b, 0.05, parts.sides(lod, 10, 6), st["rod"])
                v0, v1 = sorted((s * (pv - 20), s * pv))
                prims.box(mb, (m(u - 90), m(v0), m(wc - 150)), (m(u + 90), m(v1), m(bw + 300)), st["inside"])
                v0, v1 = sorted((s * (f["beamV"] + f["beamWidth"] / 2), s * (pv - 20)))
                prims.box(mb, (m(u - 60), m(v0), m(bw + 100)), (m(u + 60), m(v1), m(bw + 300)), st["inside"])


def machinery(mb, t, end, lod, st):
    """The detach machinery the photos show through the glass (owner, 2026-10-21: simplified, LOD0-1). Along each
    straight a tyre conveyor whose tyres bear on the grips' tyre plates (#tyres), under red guards on a beam from the
    portals; beyond the rope's bend, short rails over the levers' top rollers where the grips open and close
    (#openrail). Static: the grip stays rigid (LM10)."""
    if lod >= 2:
        return
    tb, orl = t["frame"]["tyreBank"], t["frame"]["openRails"]
    u0, u1 = end["rail"]["corner"][0] + 300, end["rail"]["end"] - 600
    top = t["frame"]["portals"]["w"][1] - t["frame"]["portals"]["size"]
    for s in (-1, 1):
        if lod == 0:
            with mb.grouped("#tyres"):
                for k in range(int((u1 - u0) // tb["pitch"]) + 1):
                    c = Vector((m(u0 + k * tb["pitch"]), m(s * tb["v"]), m(tb["w"])))
                    prims.cylinder(mb, c - V * m(tb["width"]) / 2, c + V * m(tb["width"]) / 2, m(tb["dia"]) / 2, 12, st["tyre"],
                                   fan_caps=True)
        with mb.grouped("#tyre_guards"):
            for key, style in (("guard", st["guard"]), ("beam", st["inside"])):
                a, b = sorted((s * tb[key]["v"][0], s * tb[key]["v"][1]))
                prims.box(mb, (m(u0 - 300), m(a), m(tb[key]["w"][0])), (m(u1 + 300), m(b), m(tb[key]["w"][1])), style)
        ra = end["ropeBend"] + orl["from"]
        rb = ra + orl["length"]
        hw, hh = orl["section"][0] / 2, orl["section"][1]
        with mb.grouped("#openrail"):
            prims.box(mb, (m(ra), m(s * orl["v"] - hw), m(orl["w"])), (m(rb), m(s * orl["v"] + hw), m(orl["w"] + hh)), st["rail"])
        with mb.grouped("#openrail"):   # its hangers up to the portal: one rigid piece with the rail
            for hu in (ra + 100, rb - 100):
                prims.box(mb, (m(hu - 30), m(s * orl["v"] - 30), m(orl["w"] + hh)), (m(hu + 30), m(s * orl["v"] + 30), m(top)),
                          st["inside"])


def carriage(mb, t, end, lod, st):
    """The bullwheel's carriage between the main beams (the return's rolls on the beams for the tensioning), in the
    wheel's plane, and the hub's housing down to the wheel. It stays inside the hood (round 2: one built from the rear
    footing's outline hung out under it)."""
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
        prims.obox(mb, c, (d, V, nrm), (m((u1 - u0) / d.x) / 2, 0.11, m(bd) * 0.3), st["inside"])
    for cu_ in cg["cross"]:
        c = Vector((m(cu_), 0.0, m(beam_w(t, end, cu_) + bd * 0.4)))
        prims.obox(mb, c, (d, V, nrm), (0.12, m(hv), m(bd) * 0.3), st["inside"])
    if lod <= 1:
        for ru in cg["rollers"]:   # the rollers it runs on, on the beams
            for s in (-1, 1):
                c = Vector((m(ru), m(s * (t["frame"]["beamV"] - t["frame"]["beamWidth"] / 2 - 90)), m(beam_w(t, end, ru) + bd)))
                prims.cylinder(mb, c - V * 0.09, c + V * 0.09, 0.14, 12 if lod == 0 else 8, st["dark"])
    tilt = math.radians(t["bullwheel"]["tilt"])
    axis = Vector((-math.sin(tilt), 0.0, math.cos(tilt)))
    centre = Vector((0.0, 0.0, m(t["bullwheel"]["w"][end["name"]])))
    top = m(beam_w(t, end, 0.0) + bd * 0.1) - centre.z
    # the hub's housing, from just above the hub's top (the spec's hubTop, 250) up to the carriage
    prims.cylinder(mb, centre + axis * 0.26, centre + axis * top, 0.3, 16 if lod == 0 else 10, st["dark"])


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
        fl = fm["flange"]   # the round flange the head sits on (the plan view)
        prims.cylinder(mb, (m(u), 0.0, m(head0 - fl["thick"])), (m(u), 0.0, m(head0)), m(fl["dia"]) / 2, parts.sides(lod, 24, 16),
                       st["galv"], fan_caps=True)
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
                     handrails=False, rung_style=st["galv"])   # all galvanised (round 3: yellow rungs only up close)
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
                st["galv"])
    (h0, h1), hw = rs["head"], rs["headW"]
    prims.box(mb, (m(h0), m(-hv), m(aw)), (m(h1), m(hv), m(hw)), st["galv"])
    if lod <= 1:
        (s0, s1), (sw0, sw1), sv = rs["saddle"], rs["saddleW"], rs["saddleHalfV"]
        curve = rs.get("saddleCurve")
        if curve:   # a bracket whose back edge curves from its top corner down and forward (the return's side view)
            (ca, wa), (cb, wb) = curve
            arc = [(cb - (cb - ca) * math.cos(a), wa - (wa - wb) * math.sin(a))
                   for a in (math.pi / 2 * k / 4 for k in range(5))]
            poly = [(s1, sw0), (s1, sw1)] + arc + [(cb, sw0)]
            prims.prism(mb, [(m(u), m(w)) for u, w in poly], (0.0, m(-sv), 0.0), U, W, V, m(2 * sv), st["galv"])
        else:
            prims.box(mb, (m(s0), m(-sv), m(sw0)), (m(s1), m(sv), m(sw1)), st["galv"])
        sc = rs.get("sideCover")
        if sc and lod == 0:   # the access cover on each flank of the leg
            (c0, c1), (cw0, cw1) = sc["u"], sc["w"]
            oval = [(m((c0 + c1) / 2 + (c1 - c0) / 2 * math.cos(a)), m((cw0 + cw1) / 2 + (cw1 - cw0) / 2 * math.sin(a)))
                    for a in (2 * math.pi * k / 12 for k in range(12))]
            for s in (-1, 1):
                prims.prism(mb, oval, (0.0, m(s * hv) if s > 0 else m(-hv - 12), 0.0), U, W, V, 0.012, st["galv"])
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

    # each builder's faces are a named piece of the body ("#name"; the export folds them back into it), so the
    # station fit check (liftkit/fit.py) can say what a chair met and knows the parts it may bear on (#rail)
    for piece, make in (("#hood", hood), ("#belly", belly), ("#frame", frame_steel), ("#rail", chair_rail),
                        ("#horns", horns), ("#carriage", carriage), ("#front_mast", front_mast),
                        ("#rear_leg", rear_leg)):
        with body.grouped(piece):
            make(body, t, end, lod, st)
    with body.grouped("#bend_sheaves"):
        bend_sheaves(body, t, end, rope, lod, st)
    with body.grouped("#machinery"):
        machinery(body, t, end, lod, st)
    with body.grouped("#fittings"):
        fittings(body, t, end, lod, st)
    la = end.get("landing")
    if la and lod <= 1:   # where the ladder comes up through its hatch onto the floor
        with body.grouped("#frame"):
            for s in (-1, 1):
                va, vb = sorted((s * t["hood"]["soffit"]["hatchHalf"], s * t["frame"]["floor"]["v"][0]))
                fw = t["frame"]["floor"]["w"]
                parts.deck(body, (m(la["u"][0]), m(va), m(fw[0])), (m(la["u"][1]), m(vb), m(fw[1])), lod, st["grating"], snow=0.0)
    with body.grouped("#piers"):
        p1 = pier(body, end["pierFront"], t["pierDepth"], lod, st)
        p2 = pier(body, end["pierRear"], t["pierDepth"], lod, st)

    tilt = t["bullwheel"]["tilt"]
    bw_w = t["bullwheel"]["w"][variant]
    centre = (0.0, 0.0, m(bw_w))
    if lod <= 2:   # hidden in the tub but for glimpses from above: a kit level coarser, a plain ring at LOD2, none
        wheel = MeshBuilder()   # at LOD3 (round 3: 2.5k triangles nobody sees)
        parts.bullwheel(wheel, Vector((0.0, 0.0, 0.0)), parts.bullwheel_spec(c), lod + 1, st["dark"])
        tilted(wheel, tilt, centre)
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": centre, "axis": (-math.sin(math.radians(tilt)), 0.0, math.cos(math.radians(tilt)))}

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
