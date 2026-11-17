"""Sessellift G8 eight-seat gondola cabin (sessellift_g8.json "cabin", task 4.24), on the Sessellift detachable grip
(sessellift_detach_grip.json): the G8 runs on the D6's terminals and wide heads, only the carrier is new. It hangs
centred under the haul rope (owner).

  body   a barrel: crisp rounded-rectangle plan sections lofted through the measured profile, glazed between the skirt
         and the roof band (the glass slot up close, opaque glass far off); the skirt, the roof band, the roof and the
         corner pillars (the corner arcs and a strip beside them) in the lift's livery, as the reference paints them red;
         a dark underside; black rubber bumpers on the end faces, rounded every way; a guide tube bowed out under the
         inboard side.
  door   two leaves on the outboard face, shut (no door motion): livery stiles and rails proud of the glass, a black seal
         where they meet, a dark step plate under them, and on each leaf a ski rack (a tipped basket for the tails with
         rounded outer corners and a dark rim, three straps the skis lean on, standing off on short legs).
  inside two benches of four across the line, facing each other; a dark floor, a white ceiling, the skirt's inner face,
         a dark handrail on the inboard wall.
  roof   the hanger's frame: two bars along the line on four black mounts, two channels across them, and a fork of two
         plates clasping the hanger's arm, joined over it by two crossbars.
  hanger a square bar: the D6 hanger's path from the grip's socket down and outboard round the stations' bellies, a broad
         elbow, then a level arm in over the roof through the fork.

The origin is the grip on the rope; the grip and the hanger are moving parts (contract M3), pivots as the D6 chair's.
Seat sockets seat_1..4 face +u (the bench at -u), seat_5..8 face -u (dims seatFacing). LOD2 is the barrel in six
sections with the glass opaque, the grip a block; LOD3 a 3-band box (livery, glass, livery) and the hanger's drop.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import GLASS, MeshBuilder, Style

KIND = "carrier_cabin"
LODS = 4
SPEC = "sessellift_g8.json"


def m(x):
    return x / 1000.0


def styles(lod):
    near = lod <= 1
    return {
        "livery": Style("livery_gloss", livery=1.0, snow=0.0, smooth=True, group="shell", crease=50.0),
        "glass": (Style("glass", slot=GLASS, smooth=True, group="glass", crease=50.0) if near
                  else Style("glass_black", smooth=True, group="glass", crease=50.0)),
        "roof": Style("livery_gloss", livery=1.0, snow=0.3),   # the reference paints its roof red
        "under": Style("trim_dark"),
        "frame": Style("trim_dark"),
        "rubber": Style("rubber"),
        "floor": Style("rubber"),
        "ceiling": Style("white"),
        "inner": Style("interior"),
        "seat": Style("trim_dark"),        # the reference's charcoal cushions
        "seat_base": Style("paint_grey"),
        "back": Style("paint_grey"),       # backrests and bench ends (light in the reference)
        "hanger": Style("grip", snow=0.1),  # the reference's hanger is dark grey
        "steel": Style("chair_steel", snow=0.1),
        "galv": Style("galvanised", snow=0.1),
        "alu": Style("aluminium"),
    }


def grip_styles():
    """No snow: grips run through the stations' tyres (as the D6 chair's)."""
    return {"alu": Style("aluminium"), "black": Style("rubber"), "blue": Style("spring_blue"), "cream": Style("roller_cream"),
            "rod": Style("machined")}


class Barrel:
    """The cabin's plan section at any height: a rounded rectangle about (0, centreV) (metres)."""

    def __init__(self, cab):
        self.cv = m(cab["centreV"])
        self.r = m(cab["cornerR"])
        self.flat = m(cab["pillar"]["flat"])
        self.prof = [(m(w), m(hu), m(hv)) for w, hu, hv in cab["profile"]]

    def at(self, w):
        p = self.prof
        if w <= p[0][0]:
            return p[0][1], p[0][2]
        for (w0, u0, v0), (w1, u1, v1) in zip(p, p[1:]):
            if w <= w1:
                t = (w - w0) / (w1 - w0)
                return u0 + (u1 - u0) * t, v0 + (v1 - v0) * t
        return p[-1][1], p[-1][2]

    def ring(self, w, k, inset=0.0):
        """The section at w as a loop of points (u, v, w): k arc steps a corner (0: a plain chamfer's two points, -1: the
        bare rectangle)."""
        hu, hv = self.at(w)
        hu, hv = hu - inset, hv - inset
        if k < 0:
            return [(cu * hu, self.cv + cvs * hv, w) for cu, cvs in ((1, 1), (-1, 1), (-1, -1), (1, -1))]
        r = min(self.r, hu * 0.5, hv * 0.5)
        f = self.flat if k >= 1 else 0.0   # the livery pillar's strip either side of a corner's arc
        pts = []
        for cu, cvs, a0 in ((1, 1, 0.0), (-1, 1, 90.0), (-1, -1, 180.0), (1, -1, 270.0)):
            oc = (cu * (hu - r), cvs * (hv - r))
            arc = []
            for j in range(k + 2 if k else 2):
                a = math.radians(a0 + 90.0 * j / (k + 1 if k else 1))
                arc.append((oc[0] + r * math.cos(a), self.cv + oc[1] + r * math.sin(a), w))
            if f:
                a, b = math.radians(a0), math.radians(a0 + 90.0)   # tangents (CCW) at the arc's two ends
                arc.insert(0, (arc[0][0] + f * math.sin(a), arc[0][1] - f * math.cos(a), w))
                arc.append((arc[-1][0] - f * math.sin(b), arc[-1][1] + f * math.cos(b), w))
            pts += arc
        out = []   # drop the doubled points where an edge has no straight run
        for p in pts:
            if not out or (abs(p[0] - out[-1][0]) > 1e-6 or abs(p[1] - out[-1][1]) > 1e-6):
                out.append(p)
        if abs(out[0][0] - out[-1][0]) < 1e-6 and abs(out[0][1] - out[-1][1]) < 1e-6:
            out.pop()
        return out

    def levels(self, cab, lod):
        """The profile's sections a LOD lofts through."""
        if lod == 0:
            return self.prof
        return [self.prof[i] for i in cab["profileFar" if lod == 1 else "profileMid"]]

    def side_v(self, u, w):
        """The outboard face's v at u (on the barrel's +v side) at height w."""
        hu, hv = self.at(w)
        r = min(self.r, hu * 0.5, hv * 0.5)
        x = abs(u) - (hu - r)
        if x <= 0:
            return self.cv + hv
        x = min(x, r)
        return self.cv + hv - r + math.sqrt(max(r * r - x * x, 0.0))

    def corner(self, w, cu, cvs, out=0.0):
        """The middle of a corner's arc at w (cu, cvs = +-1), pushed out along the diagonal by out."""
        hu, hv = self.at(w)
        r = min(self.r, hu * 0.5, hv * 0.5)
        d = r * math.sqrt(0.5) + out * math.sqrt(0.5)
        return (cu * (hu - r + d), self.cv + cvs * (hv - r + d), w)


def loft(mb, bar, levels, k, style_of, inward=False, inset=0.0):
    """Shell faces between the sections at each of `levels` (w, metres); style_of(w0, w1, corner) picks a face's style,
    corner: the face is on a corner's arc or the pillar strip beside it."""
    rings = [bar.ring(w, k, inset) for w in levels]
    n = len(rings[0])
    idx = [mb.verts_lift(r) for r in rings]
    for (w0, w1), a, b, ra in zip(zip(levels, levels[1:]), idx, idx[1:], rings):
        hu, hv = bar.at(w0)
        r = min(bar.r, hu * 0.5, hv * 0.5)
        reach = r + (bar.flat if k >= 1 else 0.0) + 1e-4
        for j in range(n):
            j1 = (j + 1) % n
            mid = ((ra[j][0] + ra[j1][0]) / 2, (ra[j][1] + ra[j1][1]) / 2 - bar.cv, 0.0)
            corner = k >= 0 and abs(mid[0]) > hu - inset - reach and abs(mid[1]) > hv - inset - reach
            out = (-mid[0], -mid[1], 0.0) if inward else mid
            mb.face([a[j], a[j1], b[j1], b[j]], style_of(w0, w1, corner), out)
    return rings


def cap(mb, ring, style, up):
    c = (sum(p[0] for p in ring) / len(ring), sum(p[1] for p in ring) / len(ring), ring[0][2])
    prims.fan(mb, ring, c, style, (0.0, 0.0, 1.0 if up else -1.0))


def shell(mb, cab, bar, st, lod):
    """The barrel's outside, its underside and roof cap, and up close the inside: floor, ceiling, the skirt's inner face."""
    g0, g1 = m(cab["glass"][0]), m(cab["glass"][1])
    allw = [p[0] for p in bar.prof]
    if lod == 0:
        levels = allw
    elif lod <= 2:
        levels = [allw[i] for i in cab["profileFar" if lod == 1 else "profileMid"]]
    else:
        levels = [allw[i] for i in cab["profileSpeck"]]
    k = (3, 1, 0, -1)[lod]

    def zone(w0, w1, corner):
        if lod == 3:   # far off: the livery skirt, the dark glass, the livery roof band
            return st["glass"] if w0 >= g0 - 1e-6 and w1 <= levels[-2] + 1e-6 else st["livery"]
        mid = (w0 + w1) / 2
        glazed = g0 - 1e-6 <= mid <= g1 + 1e-6 and g0 - 1e-6 <= w0
        return st["glass"] if glazed and not corner else st["livery"]   # the corners are livery pillars
    rings = loft(mb, bar, levels, k, zone)
    cap(mb, rings[-1], st["livery"] if lod == 3 else st["roof"], True)
    if lod <= 2:
        cap(mb, rings[0], st["under"], False)
    if lod > 1:
        return
    fl, ce = m(cab["floor"]), m(cab["ceiling"])
    cap(mb, bar.ring(fl, k, inset=0.012), st["floor"], True)
    cap(mb, bar.ring(ce, k, inset=0.012), st["ceiling"], False)
    loft(mb, bar, [fl, (fl + g0) / 2, g0], k, lambda a, b, c: st["inner"], inward=True, inset=0.012)


def frames(mb, cab, bar, st, lod):
    """The black rubber bumpers on the end faces at the corners (LOD0), and inside the dark handrail on the inboard wall
    (LOD0-1). The corner pillars are the livery shell itself (shell(); the reference has no dark frames or rails)."""
    if lod == 0:
        bp = cab["bumpers"]
        w0, w1 = m(bp["w"][0]), m(bp["w"][1])
        for cu, cvs in ((1, 1), (-1, 1), (-1, -1), (1, -1)):
            capsule(mb, (cu * m(bp["u"]), bar.cv + cvs * m(bp["v"])), (w0, w1), m(bp["size"][0]) / 2, m(bp["size"][1]) / 2,
                    st["rubber"])
    hr = cab["handrail"]
    prims.box(mb, (-m(hr["u"]), bar.cv + m(hr["v"][0]), m(hr["w"][0])), (m(hr["u"]), bar.cv + m(hr["v"][1]), m(hr["w"][1])),
              st["frame"])


def door(mb, cab, bar, st, lod):
    """The two leaves on the outboard face: livery frames proud of the glass (outer and inner stiles, top and bottom
    rails; LOD1 one wide centre stile), a black seal where the leaves meet, the dark step plate under them, and on each
    leaf a ski rack: a basket for the tails and (LOD0) three flat bars the skis lean on."""
    d = cab["door"]
    w0, w1 = m(d["w"][0]), m(d["w"][1])
    dp, hu = m(d["depth"]), m(d["halfU"])
    so, si, se = m(d["stileOuter"]), m(d["stileInner"]), m(d["seal"])
    ws = [w0] + [w for w, _, _ in bar.levels(cab, lod) if w0 < w < w1] + [w1]

    def stile(u, width, style):
        bar_sweep(mb, [(u, bar.side_v(u, w) + dp / 2, w) for w in ws], [width] * len(ws), [dp] * len(ws), style)
    for s in (-1, 1):
        stile(s * (hu - so / 2), so, st["livery"])
        if lod == 0:
            stile(s * (se + si) / 2, si, st["livery"])
    if lod == 0:   # the seal stands proud of the stiles and rails, sill to roof band
        sd = m(d["sealDepth"])
        bar_sweep(mb, [(0.0, bar.side_v(0.0, w) + sd / 2, w) for w in ws], [se] * len(ws), [sd] * len(ws), st["rubber"])
    else:
        stile(0.0, se + 2 * si, st["livery"])
    for wa, wb in ((w0, w0 + m(d["railBottom"])), (w1 - m(d["railTop"]), w1)):   # rails on the sloping face
        va, vb = bar.side_v(0.0, wa), bar.side_v(0.0, wb)
        along = Vector((0.0, vb - va, wb - wa))
        ln = along.length
        along.normalize()
        out = Vector((0.0, along.z, -along.y))
        c = Vector((0.0, (va + vb) / 2, (wa + wb) / 2)) + out * (dp / 2)
        prims.obox(mb, c, (Vector((1.0, 0.0, 0.0)), along, out), (hu, ln / 2, dp / 2), st["livery"])
    sp = cab["step"]
    prims.box(mb, (-m(sp["halfU"]), bar.cv + m(sp["v"][0]), m(sp["w"][0])), (m(sp["halfU"]), bar.cv + m(sp["v"][1]), m(sp["w"][1])),
              st["frame"])
    sk = cab["skiRack"]
    bk = sk["basket"]
    tilt = math.radians(bk["tilt"])
    out = (0.0, math.cos(tilt), -math.sin(tilt))
    up = (0.0, math.sin(tilt), math.cos(tilt))
    o_v, o_w = m(bk["origin"][0]), m(bk["origin"][1])
    depth, height, rim = m(bk["out"]), m(bk["height"]), m(bk["rim"])
    r = min(m(bk["round"]), depth / 2)
    steps = 3 if lod == 0 else 0
    bb = sk["bar"]
    bt, bh, so = m(bb["size"][0]), m(bb["size"][1]) / 2, m(bb["standoff"])
    for s in (-1, 1):
        ua, ub = sorted((s * m(bk["u"][0]), s * m(bk["u"][1])))
        poly = [(ua, 0.0), (ub, 0.0)]   # its plan (u, out): square at the door, the outer corners rounded (LOD0)
        if steps:
            poly += [(ub - r + r * math.cos(t), depth - r + r * math.sin(t)) for t in
                     (math.pi / 2 * k / steps for k in range(steps + 1))]
            poly += [(ua + r + r * math.cos(t), depth - r + r * math.sin(t)) for t in
                     (math.pi / 2 + math.pi / 2 * k / steps for k in range(steps + 1))]
        else:
            poly += [(ub, depth), (ua, depth)]
        base = (0.0, bar.cv + o_v, o_w)
        top = tuple(b + e * (height - rim) for b, e in zip(base, up))
        # the mesh body (its base lighter, as the reference's), then the dark rim; looking in, the dark inside
        prims.prism(mb, poly, base, (1.0, 0.0, 0.0), out, up, height - rim, st["alu"], caps=(True, False),
                    side_style=st["galv"])
        prims.prism(mb, poly, top, (1.0, 0.0, 0.0), out, up, rim, st["frame"])
        if lod > 0:
            continue
        xa, xb = sorted((s * m(bb["u"][0]), s * m(bb["u"][1])))
        for wb in sk["bars"]:   # a strap standing off the door on a short leg at each end
            wb = m(wb)
            fv = bar.side_v((xa + xb) / 2, wb)
            prims.box(mb, (xa + bt, fv + so, wb - bh), (xb - bt, fv + so + bt, wb + bh), st["frame"])
            for x0, x1 in ((xa, xa + bt), (xb - bt, xb)):
                prims.box(mb, (x0, fv - 0.005, wb - bh), (x1, fv + so + bt, wb + bh), st["frame"])


def benches(mb, cab, bar, st, lod):
    """Two benches of four across the line, facing each other: a grey base, a black pad, a pedestal to the floor, a
    padded backrest against the wall; base and pedestal kept inside the barrel's sloping skirt."""
    bn = cab["bench"]
    v0, v1 = m(bn["v"][0]), m(bn["v"][1])
    fr, bk = m(bn["front"]), m(bn["back"])
    top, bot, pad = m(bn["top"]), m(bn["bottom"]), m(bn["pad"])
    fl = m(cab["floor"])
    br = bn["back_rest"]
    hu_f, hv_f = bar.at(fl)
    hu_b, _ = bar.at(bot)
    for s in (-1, 1):
        ua, ub = sorted((s * fr, s * min(bk, hu_b - 0.015)))
        prims.box(mb, (ua, v0, bot), (ub, v1, top - pad), st["seat_base"], skip=("+2",))
        ua, ub = sorted((s * fr, s * bk))
        prims.box(mb, (ua, v0, top - pad), (ub, v1, top), st["seat"])
        pa, pb = sorted((s * (fr + 0.06), s * (hu_f - 0.04)))
        prims.box(mb, (pa, bar.cv - hv_f + 0.05, fl), (pb, bar.cv + hv_f - 0.05, bot), st["seat_base"], skip=("-2", "+2"))
        if lod == 0:
            (bu0, bu1), (bw0, bw1) = [m(x) for x in br["u"]], [m(x) for x in br["w"]]
            along = Vector((s * (bu1 - bu0), 0.0, bw1 - bw0))
            ln = along.length
            along.normalize()
            across = Vector((0.0, 1.0, 0.0))
            n = along.cross(across).normalized()
            c = Vector((s * ((bu0 + bu1) / 2 - 0.03), (v0 + v1) / 2, (bw0 + bw1) / 2))
            prims.obox(mb, c, (along, across, n), (ln / 2, (v1 - v0) / 2, 0.03), st["back"])
            en = bn["ends"]
            ea, eb = sorted((s * m(en["u"][0]), s * m(en["u"][1])))
            for sv in (-1, 1):
                va, vb = sorted((sv * m(en["v"][0]), sv * m(en["v"][1])))
                prims.box(mb, (ea, bar.cv + va, m(en["w"][0])), (eb, bar.cv + vb, m(en["w"][1])), st["back"])


def skid(mb, cab, bar, st):
    """The guide tube under the inboard side (LOD0): bowed out past the floor, its ends hooked back in under it, hung
    drop under the underside (Gate 0: the reference's hung 70 under, which would reach under the load level)."""
    sk = cab["skid"]
    r = m(sk["dia"]) / 2
    w = m(cab["profile"][0][0]) - m(sk["drop"]) + r
    half = [(m(u), m(v)) for u, v in sk["path"]]
    pts = [(-u, bar.cv + v, w) for u, v in reversed(half)] + [(u, bar.cv + v, w) for u, v in half[1:]]
    prims.tube_path(mb, pts, r, 6, st["alu"])


def capsule(mb, c, ws, ru, rv, style, sides=10, end=0.03):
    """A pad rounded every way: an upright elliptic section (ru along u, rv along v) at c (u, v) from ws[0] to ws[1],
    its ends rounded over end."""
    w0, w1 = ws
    rings = []
    for w, k in ((w0, 0.55), (w0 + end * 0.3, 0.88), (w0 + end, 1.0), (w1 - end, 1.0), (w1 - end * 0.3, 0.88), (w1, 0.55)):
        ring = [(c[0] + ru * k * math.cos(t), c[1] + rv * k * math.sin(t), w)
                for t in (2 * math.pi * i / sides for i in range(sides))]
        rings.append(([mb.vert(p) for p in ring], ring, w))
    smooth = style.but(smooth=True)
    for (a, pa, wa), (b, pb, wb) in zip(rings, rings[1:]):
        for i in range(sides):
            j = (i + 1) % sides
            mid = [(pa[i][k] + pa[j][k] + pb[i][k] + pb[j][k]) / 4 for k in range(3)]
            mb.face([a[i], a[j], b[j], b[i]], smooth, (mid[0] - c[0], mid[1] - c[1], mid[2] - (wa + wb) / 2))
    mb.face(rings[0][0], style.but(smooth=False), (0.0, 0.0, -1.0))
    mb.face(rings[-1][0], style.but(smooth=False), (0.0, 0.0, 1.0))


def roof_frame(mb, cab, st, lod):
    """The hanger's frame on the roof: two bars along u on four black mounts, the spine's two channels across them at the
    same height, and the fork's two plates clasping the hanger's arm with a pin through it (LOD0-1); LOD2 the spine as
    one block."""
    rf = cab["roofFrame"]
    roof = m(cab["profile"][-1][0])
    mt, sp, fk = rf["mount"], rf["spine"], rf["fork"]
    w0 = roof + m(mt["height"])
    s0, s1 = m(rf["spineV"][0]), m(rf["spineV"][1])
    su0, su1, sh = m(sp["u"][0]), m(sp["u"][1]), m(sp["height"])
    if lod >= 2:
        prims.box(mb, (-su1, s0, roof), (su1, s1, w0 + sh), st["steel"], skip=("-2",))
        return
    hu = m(rf["halfU"])
    bw, bh = m(rf["bar"][0]) / 2, m(rf["bar"][1])
    for vb in rf["barV"]:
        vb = m(vb)
        if lod == 0:
            kd, kh = (m(x) for x in mt["knob"])
            for u in (-m(mt["u"]), m(mt["u"])):
                prims.cylinder(mb, (u, vb, roof), (u, vb, w0), m(mt["dia"]) / 2, 8, st["rubber"], caps=(False, True))
                prims.cylinder(mb, (u, vb, w0 + bh), (u, vb, w0 + bh + kh), kd / 2, 8, st["galv"], caps=(False, True))
        prims.box(mb, (-hu, vb - bw, w0), (hu, vb + bw, w0 + bh), st["steel"])
    hg = cab["hanger"]
    arm_w = m(hg["path"][-1][1])
    b, t, top = m(fk["bottomV"]), m(fk["topV"]), m(fk["top"])
    for s in (-1, 1):
        ua, ub = sorted((s * su0, s * su1))
        prims.box(mb, (ua, s0, w0), (ub, s1, w0 + sh), st["steel"])
        fa, fb = sorted((s * m(fk["u"][0]), s * m(fk["u"][1])))
        fu, bw2 = (fa + fb) / 2, m(fk["bar"]) / 2
        for sv in (-1, 1):   # two legs leaning in, the arm between them
            a, c = Vector((fu, sv * b, w0 + sh)), Vector((fu, sv * t, top - bw2))
            along = (c - a).normalized()
            n = Vector((1.0, 0.0, 0.0)).cross(along).normalized()
            prims.obox(mb, (a + c) / 2, (Vector((1.0, 0.0, 0.0)), along, n), ((fb - fa) / 2, (c - a).length / 2, bw2), st["alu"])
        prims.box(mb, (fa, -t - bw2, top - 2 * bw2), (fb, t + bw2, top), st["alu"])
    if lod == 0:
        prims.cylinder(mb, (-m(fk["u"][1]) - 0.01, 0.0, arm_w), (m(fk["u"][1]) + 0.01, 0.0, arm_w), 0.02, 8, st["galv"])
        fu1 = m(fk["u"][1])
        for sv in (-1, 1):   # two crossbars join the plates over the arm, clear of its top
            prims.box(mb, (-fu1, sv * t - bw2, top - 0.03), (fu1, sv * t + bw2, top), st["alu"])


def bar_sweep(mb, pts, widths, depths, style, caps=True):
    """A rectangular bar swept along a path in a v-w plane (the hanger, the door stiles): width along u, depth in the
    plane, both per point (metres), so the bar can taper; flat shaded, the ends capped."""
    pts = [Vector(p) for p in pts]
    rings = []
    for i, p in enumerate(pts):
        t_in = (p - pts[i - 1]).normalized() if i > 0 else None
        t_out = (pts[i + 1] - p).normalized() if i < len(pts) - 1 else None
        t = ((t_in or t_out) + (t_out or t_in)).normalized()
        n = Vector((0.0, -t.z, t.y))   # in the plane, square to the path
        if t_in is not None and t_out is not None:   # keep the depth across a bend (mitre)
            n = n / max(n.dot(Vector((0.0, -t_in.z, t_in.y))), 0.5)
        hu, hd = widths[i] / 2, depths[i] / 2
        corners = [p + Vector((su * hu, 0.0, 0.0)) + n * (sd * hd) for su, sd in ((1, 1), (-1, 1), (-1, -1), (1, -1))]
        rings.append(([mb.vert(tuple(c)) for c in corners], corners, t))
    for i, ((a, ca, _), (b, cb, _)) in enumerate(zip(rings, rings[1:])):
        mid = (pts[i] + pts[i + 1]) / 2
        for k in range(4):
            out = (ca[k] + ca[(k + 1) % 4] + cb[k] + cb[(k + 1) % 4]) / 4 - mid
            mb.face([a[k], a[(k + 1) % 4], b[(k + 1) % 4], b[k]], style, tuple(out))
    if caps:
        mb.face(rings[0][0], style, tuple(-rings[0][2]))
        mb.face(rings[-1][0], style, tuple(rings[-1][2]))
    return rings


def hanger(mb, cab, st, gst, g, lod):
    """The grip's arm and the hanger, a square bar: from the grip's socket down and outboard, a broad elbow, the level
    arm in over the roof through the roof frame's fork (LOD0-1); LOD2 the arm, the drop and the run as blocks, LOD3 the
    drop down to the roof."""
    hg = cab["hanger"]
    sw, sd = m(hg["section"][0]), m(hg["section"][1])
    if lod <= 1:
        parts.detach_grip_arm(mb, lod, gst, g, socket_group="hanger#socket")
        pts = hg["path"] if lod == 0 else [hg["path"][i] for i in hg["far"]]
        bar_sweep(mb, [(0.0, m(v), m(w)) for v, w in pts], [sw] * len(pts), [sd] * len(pts), st["hanger"])
        return
    top = -0.089
    run_w, end_v = m(hg["path"][-1][1]), m(hg["path"][-1][0])
    drop_v = m(470)
    if lod == 2:   # as the D6 chair's far hanger: the arm out from the grip, the drop outboard, the run in over the roof
        prims.box(mb, (-0.06, m(186), top), (0.06, drop_v + sd, 0.016), st["hanger"])
        prims.box(mb, (-sw / 2, end_v, run_w - sd / 2), (sw / 2, drop_v, run_w + sd / 2), st["hanger"])
        prims.box(mb, (-sw / 2, drop_v, run_w - sd / 2), (sw / 2, drop_v + sd, top), st["hanger"])
    else:
        prims.box(mb, (-sw / 2, drop_v, m(cab["profile"][-1][0])), (sw / 2, drop_v + sd, top), st["hanger"], skip=("-2",))


def finish(a, mb, cab):
    mb.group = None
    a.body = mb
    ss = cab["seatSocket"]
    bn = cab["bench"]
    v0, v1 = bn["v"]
    n = cab["seats"] // 2
    a.sockets = {"grip": (0.0, 0.0, 0.0)}
    facing = []
    for side, s in enumerate((-1, 1)):   # the bench at -u faces +u
        for i in range(n):
            vs = v0 + (i + 0.5) * (v1 - v0) / n
            a.sockets[f"seat_{side * n + i + 1}"] = (s * m(ss["u"]), m(vs), m(ss["w"]))
            facing.append(-s)
    g = parts.detach_grip_spec()
    a.pivots = {"grip": {"pos": (0.0, 0.0, 0.0), "axis": (0.0, 1.0, 0.0)},
                "hanger": {"pos": (0.0, m(g["socket"]["v"]), m(g["arm"]["axisW"])), "axis": (0.0, 1.0, 0.0), "parent": "grip",
                           "hinge": True}}
    a.socket_parents = {k: "hanger" for k in a.sockets if k != "grip"}
    prof = cab["profile"]
    a.dims.update({"seats": cab["seats"], "drop": -prof[0][0], "floor": -cab["floor"], "roof": -prof[-1][0],
                   "cabinHalfLength": max(p[1] for p in prof), "cabinHalfWidth": max(p[2] for p in prof),
                   "axisV": cab["centreV"], "ropeW": 3600, "seatFacing": facing})
    return a


def build(spec, lod, stage):
    cab = spec["cabin"]
    st = styles(lod)
    gst = grip_styles()
    g = parts.detach_grip_on(spec["common"]["ropeDiameter"])
    bar = Barrel(cab)
    a = Asset("sessellift_g8_cabin")
    mb = MeshBuilder()
    mb.group = "hanger"
    hanger(mb, cab, st, gst, g, lod)
    if lod <= 1:
        with mb.grouped("grip"):
            parts.detach_grip(mb, lod, gst, g)
    elif lod == 2:
        with mb.grouped("grip"):
            parts.detach_grip(mb, 2, gst)
    shell(mb, cab, bar, st, lod)
    if lod <= 1:
        frames(mb, cab, bar, st, lod)
        door(mb, cab, bar, st, lod)
        benches(mb, cab, bar, st, lod)
    if lod == 0:
        skid(mb, cab, bar, st)
    if lod <= 2:
        roof_frame(mb, cab, st, 2 if lod == 2 else lod)
    return finish(a, mb, cab)
