"""SLE stick snow gun: a base mast (a 4 in post standing from the ground) with a base plate and two ears carrying
the pivot pin; the lance hangs on the pin by two tabs under a U sleeve, held at its lean by a stay bar from a clevis
on the mast. The lance is a 2 in pipe on a U-channel stiffener with a hose block and couplers at its foot, and the
head on the pipe's end: a Y block carrying the ground gun's head (its fan block on the upper branch, its barrel and
nucleator cap off the side). The lance is one hinged moving part on the pin.

Three lance lengths from one build (variant = feet of pipe: 10, 20 or 30); the head rides on the pipe's end and the
channel stops 5 ft short of it. Built for hundreds on a mountain: a few hundred triangles up close, one bar far out.

Dimensions: sle_guns.json "stickGun" (millimetres), in the frame they were measured in: on the post's axis, w 0 at
the reference's snow line, u the way the lance leans, v along the pin, w up. Lance parts sit at (s along the lance,
c across it, v). The asset's origin is on the post's axis at grade ("grade", below that line): everything is built in
the measured frame, then raised onto it.
"""
from mathutils import Vector

from liftkit import prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "snowgun"
LODS = 4
SPEC = "sle_guns.json"
VARIANTS = (10, 20, 30)
FT = 304.8


def m(x):
    return x / 1000.0


def mv(p):
    return tuple(m(x) for x in p)


def styles(lod):
    return {
        "post": Style("galvanised", snow=0.6),
        "alu": Style("aluminium", snow=0.5),
        "dark": Style("paint_dark", snow=0.2),
        "dial": Style("paint_grey", snow=0.2),
        "paddle": Style("valve_maroon", snow=0.2),
        "rod": Style("machined"),
    }


class Lance:
    """Lance coordinates (s along the axis, c across it, v) to the gun frame, in metres."""

    def __init__(self, ln, shift_mm):
        self.a = Vector(ln["axis"])
        self.x = Vector(ln["across"])
        self.c0 = ln["c0"]
        self.shift = self.a * m(shift_mm)

    def p(self, s, c=0.0, v=0.0):
        return self.a * m(s) + self.x * m(self.c0 + c) + V * m(v)


def outline_prism(mb, pts, v0, v1, st, shift=Vector()):
    """A (u, w) outline in millimetres extruded along v from v0 to v1."""
    prims.prism(mb, [(m(u), m(w)) for u, w in pts], Vector((0.0, m(v0), 0.0)) + shift, U, W, V, m(v1 - v0), st)


def fan(mb, f, place, st, arc=4):
    """The fan head: a block tapering from the back (halfBack wide) to the nozzle face (halfFront), whose face bows
    out by `bulge` in a shallow arc across (arc segments; 1 = flat). `place(x, y, z)` maps the gun's millimetres
    (side outline in x, z; width along y) to the asset's frame."""
    b1, f1, f2, b2 = [place(u, 0.0, w) for u, w in f["side"]]
    o = place(0.0, 0.0, 0.0)
    V = (place(0.0, 1.0, 0.0) - o).normalized()
    up = (place(0.0, 0.0, 1.0) - o).normalized()
    hb, hf = m(f["halfBack"]), m(f["halfFront"])
    along = ((f1 + f2) / 2 - (b1 + b2) / 2).normalized()
    top = (f1 - b1).cross(V).normalized()
    if top.dot(up) < 0:
        top = -top
    # the face's columns across, from v- to v+: bowed out by a circular-ish profile (1 - t^2)
    cols = []
    for k in range(arc + 1):
        t = -1.0 + 2.0 * k / arc
        out = along * m(f["bulge"]) * (1.0 - t * t)
        cols.append((mb.vert(tuple(f1 + V * (hf * t) + out)), mb.vert(tuple(f2 + V * (hf * t) + out)), t))
    b1m, b1p = mb.vert(tuple(b1 - V * hb)), mb.vert(tuple(b1 + V * hb))
    b2m, b2p = mb.vert(tuple(b2 - V * hb)), mb.vert(tuple(b2 + V * hb))
    mb.face([b1m, b2m, b2p, b1p], st, tuple(-along))
    mb.face([b1m, b1p] + [c[0] for c in reversed(cols)], st, tuple(top))
    mb.face([b2m] + [c[1] for c in cols] + [b2p], st, tuple(-top))
    mb.face([b1m, cols[0][0], cols[0][1], b2m], st, tuple(-V))
    mb.face([b1p, b2p, cols[-1][1], cols[-1][0]], st, tuple(V))
    for (a0, a1, ta), (c0, c1, tc) in zip(cols, cols[1:]):
        tm = (ta + tc) / 2
        mb.face([a0, c0, c1, a1], st, tuple(along + V * (0.6 * tm)))


def build(spec, lod, stage, feet=20):
    g = spec["stickGun"]
    ln = g["lance"]
    st = styles(lod)
    a = Asset(f"sle_stick_gun_{feet}")
    body = MeshBuilder()
    shift_mm = (feet - ln["lengthFt"]["reference"]) * FT
    L = Lance(ln, shift_mm)

    # -- the post, its base plate, the ears and the pin (static) ----------------------------------------------------
    p = g["post"]
    sides = {0: 12, 1: 8}.get(lod, 4)
    if lod <= 2:
        prims.cylinder(body, (0.0, 0.0, m(p["bottom"])), (0.0, 0.0, m(p["top"])), m(p["dia"]) / 2, sides, st["post"],
                       caps=(False, lod == 2))
    if lod <= 1:   # the base plate (its underside sits on the post)
        bp = g["basePlate"]
        prims.box(body, mv(bp["lo"]), mv(bp["hi"]), st["post"], skip=("-2",))
    if lod == 0:   # the ears, their rounded tops on three points
        ears = [g["ears"]["outline"][i] for i in (0, 2, 4, 5, 6)]
        for v0, v1 in g["ears"]["v"]:
            outline_prism(body, ears, v0, v1, st["post"])
        pn = g["pin"]
        prims.cylinder(body, (m(pn["u"]), m(pn["v"][0]), m(pn["w"])), (m(pn["u"]), m(pn["v"][1]), m(pn["w"])),
                       m(pn["dia"]) / 2, 6, st["rod"])
    elif lod == 1:   # both ears as one triangle
        lo, hi = g["basePlate"]["lo"], g["basePlate"]["hi"]
        top = max(w for _, w in g["ears"]["outline"])
        outline_prism(body, [(lo[0], hi[2]), (hi[0], hi[2]), (0.0, top)], lo[1], hi[1], st["post"])
    if lod == 0:   # the stay's clevis on the mast: a plate on the post's face and two tabs (their outlines as quads)
        cl = g["bottomClevis"]
        prims.box(body, mv(cl["plate"]["lo"]), mv(cl["plate"]["hi"]), st["post"], skip=("-0",))
        ol = cl["tabs"]["outline"]
        hull = [ol[0], ol[3], ol[4], ol[6]]
        for v0, v1 in cl["tabs"]["v"]:
            outline_prism(body, hull, v0, v1, st["post"])

    # -- the lance: one moving part hinged on the pin ---------------------------------------------------------------
    lance = MeshBuilder()
    head = ln["head"]
    s_pipe0, s_pipe1 = ln["pipe"]["s"][0], ln["pipe"]["s"][1] + shift_mm
    s_chan1 = s_pipe1 - ln["channel"]["freeTop"] if feet != ln["lengthFt"]["reference"] else ln["channel"]["s"][1]
    s_inlet0, s_inlet1 = ln["inlet"]["s"]
    tip = Vector(mv(head["nozzle"]["to"])) + L.shift
    if lod >= 2:   # far: a bar from the foot to the head, the head as a box, the nozzle arm as a bar
        prims.beam(lance, L.p(s_inlet0), L.p(s_pipe1), 0.06, 0.06, st["alu"], up=L.x, skip_ends=True)
        if lod == 2:
            bo = [Vector((m(u), 0.0, m(w))) + L.shift for u, w in head["block"]["outline"]]
            lo_u, hi_u = min(q.x for q in bo), max(q.x for q in bo)
            lo_w, hi_w = min(q.z for q in bo), max(q.z for q in bo)
            fs = [Vector((m(u), 0.0, m(w))) + L.shift for u, w in head["fan"]["side"]]
            hi_w = max(hi_w, max(q.z for q in fs))
            lo_u = min(lo_u, min(q.x for q in fs))
            prims.box(lance, (lo_u, -0.04, lo_w), (hi_u, 0.04, hi_w), st["alu"])
            arm0 = Vector(mv(head["arm"]["from"])) + L.shift
            prims.beam(lance, tuple(arm0), tuple(tip), 0.055, 0.055, st["alu"], skip_ends=True)
        else:   # LOD3: the bar runs on to the nozzle
            pass
    else:
        # the sleeve (a U channel round the lance's foot) and the lance channel under the pipe
        sl, ch = ln["sleeve"], ln["channel"]
        if lod == 0:
            prims.prism(lance, [(m(c), m(v)) for c, v in sl["profile"]], L.p(sl["s"][0]), L.x, V, L.a,
                        m(sl["s"][1] - sl["s"][0]), st["alu"])
            prims.prism(lance, [(m(c), m(v)) for c, v in ch["profile"]], L.p(s_inlet1), L.x, V, L.a,
                        m(s_chan1 - s_inlet1), st["alu"])
        else:
            for prof, s0, s1 in ((sl["profile"], sl["s"][0], sl["s"][1]), (ch["profile"], s_inlet1, s_chan1)):
                cs = [c for c, _ in prof]
                vs = [v for _, v in prof]
                rect = [(min(cs), min(vs)), (max(cs), min(vs)), (max(cs), max(vs)), (min(cs), max(vs))]
                prims.prism(lance, [(m(c), m(v)) for c, v in rect], L.p(s0), L.x, V, L.a, m(s1 - s0), st["alu"])
        # the hose block at the foot
        il = ln["inlet"]
        c_mid, s_mid = (il["c"][0] + il["c"][1]) / 2, (s_inlet0 + s_inlet1) / 2
        prims.obox(lance, L.p(s_mid, c_mid), (L.a, V, L.x),
                   (m(s_inlet1 - s_inlet0) / 2, m(il["v"][1] - il["v"][0]) / 2, m(il["c"][1] - il["c"][0]) / 2), st["alu"])
        # the pipe, from inside the hose block into the head's flange (both ends buried, so no caps and no gaps)
        prims.cylinder(lance, L.p(s_inlet1 - 5.0), L.p(s_pipe1 + 5.0), m(ln["pipe"]["dia"]) / 2, 10 if lod == 0 else 6,
                       st["alu"], caps=(False, False))
        # the stay bar (rides with the lance: its foot sits in a clevis below the snow)
        outline_prism(lance, ln["bar"]["outline"], ln["bar"]["v"][0], ln["bar"]["v"][1], st["post"])
        if lod == 0:
            # the pivot and bar tabs under the sleeve, each with its doubler strip
            for t in ln["tabs"]:
                for v0, v1 in t["v"]:
                    outline_prism(lance, t["outline"], v0, v1, st["post"])
                outline_prism(lance, t["strip"]["outline"], t["strip"]["v"][0], t["strip"]["v"][1], st["post"])
            # hose couplers: one on a nipple out of the block's side, one under the block on the lance's axis
            for f in ln["fittings"]:
                if f["name"] == "side":
                    prims.tube_path(lance, [mv(q) for q in f["path"]], m(f["nippleDia"]) / 2, 6, st["dark"], caps=False)
                    cp = f["coupler"]
                    prims.cylinder(lance, mv(cp["from"]), mv(cp["to"]), m(cp["dia"]) / 2, 6, st["dark"], caps=(False, True))
                else:
                    s0, s1 = f["s"]
                    prims.cylinder(lance, L.p(s0), L.p(s1), m(f["dia"]) / 2, 6, st["dark"], caps=(False, True))
            # the valve on the block's other side: disc, boss, spindle and handle
            vl = ln["valve"]
            vs, vc = vl["at"]
            for part, n, caps, style in (("disc", 10, (False, True), "dial"), ("boss", 6, (False, False), "dark"),
                                         ("spindle", 4, (False, True), "rod")):
                d = vl[part]
                prims.cylinder(lance, L.p(vs, vc, d["v"][0]), L.p(vs, vc, d["v"][1]), m(d["dia"]) / 2, n, st[style], caps=caps)
            pd = vl["paddle"]   # the maroon paddle across the dial
            ds, dc = pd["dir"]
            k = pd["length"] / 2 / (ds * ds + dc * dc) ** 0.5
            hv_mid = (pd["v"][0] + pd["v"][1]) / 2
            prims.beam(lance, L.p(vs - ds * k, vc - dc * k, hv_mid), L.p(vs + ds * k, vc + dc * k, hv_mid), m(pd["width"]),
                       m(abs(pd["v"][1] - pd["v"][0])), st["paddle"], up=V)
        # the head: the Y block on its flange, the arm flange, the fan head, the nozzle arm and the nozzle
        hv = head["block"]["v"]
        if lod == 0:
            for key in ("lanceFlange", "block", "armFlange"):
                outline_prism(lance, head[key]["outline"], hv[0], hv[1], st["alu"], L.shift)
        else:   # the block's outline without its notches
            keep = [0, 2, 3, 5, 6, 8]
            outline_prism(lance, [head["block"]["outline"][i] for i in keep], hv[0], hv[1], st["alu"], L.shift)
        fan(lance, head["fan"], lambda x, y, z: Vector((m(x), m(y), m(z))) + L.shift, st["alu"], arc=4 if lod == 0 else 1)
        arm, nz = head["arm"], head["nozzle"]
        prims.cylinder(lance, Vector(mv(arm["from"])) + L.shift, Vector(mv(arm["to"])) + L.shift, m(arm["dia"]) / 2,
                       8 if lod == 0 else 6, st["alu"], caps=(False, False))
        n0 = Vector(mv(nz["from"])) + L.shift
        if lod == 0:   # the nucleator cap: a black octagon with a chamfered nose
            nose = n0 + (tip - n0) * (1.0 - nz["nose"])
            prims.cylinder(lance, n0, nose, m(nz["dia"]) / 2, 8, st["dark"], caps=(True, False))
            prims.cylinder(lance, nose, tip, m(nz["dia"]) / 2, 8, st["dark"], r1=m(nz["noseDia"]) / 2, caps=(False, True))
        else:
            prims.cylinder(lance, n0, tip, m(nz["dia"]) / 2, 6, st["dark"], caps=(False, True))

    # onto the ground: the measured frame rises by the grade's depth below its zero
    up = Vector((0.0, 0.0, -m(g["grade"])))
    if lod >= 3:   # LOD3: one bar from the lance's foot to the nozzle, in the body
        prims.beam(body, L.p(s_inlet0), tuple(tip), 0.07, 0.07, st["alu"], up=L.x, skip_ends=True)
    else:
        a.parts["lance"] = lance.translated(tuple(up))
        a.pivots["lance"] = {"pos": tuple(Vector(mv(g["pivot"])) + up), "axis": (0.0, 1.0, 0.0), "hinge": True}

    a.body = body.translated(tuple(up))
    side = next(f for f in ln["fittings"] if f["name"] == "side")
    bottom = next(f for f in ln["fittings"] if f["name"] == "bottom")
    a.sockets = {
        "base": (0.0, 0.0, 0.0),
        "nozzle": tuple(tip + up),
        "hose_side": tuple(Vector(mv(side["coupler"]["to"])) + up),
        "hose_bottom": tuple(L.p(bottom["s"][1]) + up),
    }
    grade = g["grade"]
    a.dims.update({"lanceFt": feet, "pipeMm": round(s_pipe1 - s_pipe0, 1), "postTop": p["top"] - grade,
                   "pivotW": g["pivot"][2] - grade, "nozzleW": round((tip + up).z * 1000, 1)})
    return a
