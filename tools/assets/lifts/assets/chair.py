"""Sessellift FGQ-4 quad chair, built along traced outlines (sessellift_fgq4.json "chair"):

  grip on the rope, a question-mark hanger bowing outboard into a clamp on the top bar, the top bar with
  large rounded corners, two J-shaped side frames (down behind the seat, forward under it), an open tubular
  backrest frame hung from the top bar on two rods, the restraint bar lowered in front of the riders on two
  arms (its mirror image) with the footrest along its foot, and the seat tray with padded top and front.

The origin is the grip on the rope. Chair frame (the lift frame's axes, reused): u = direction of travel
(Unity +Z), v = outboard, away from the bullwheel (Unity +X), w = up. The chair is symmetric about
v = axisV (the drawn chair sits 32 mm inboard of the rope). Close-up detail beyond the 800-triangle budget
(padding seams, bolts) comes from the baked detail map.
"""
from mathutils import Vector

from liftkit import prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

KIND = "chair"
LODS = 3


def m(x):
    return x / 1000.0


def build(spec, lod, stage):
    ch = spec["chair"]
    steel = Style("chair_steel", snow=0.4)
    pad = Style("seat_pad", snow=1.0)
    grip_st = Style("grip")
    a = Asset("sessellift_fgq4_chair")
    mb = MeshBuilder()
    ax = m(ch["axisV"])
    r_hanger, r_frame, r_rail = (m(ch["tube"][k]) / 2 for k in ("hanger", "frame", "rail"))

    def sym(u, hw, w, s):
        """A point on side s (+1 outboard, -1 inboard) of the chair's symmetry axis."""
        return Vector((m(u), ax + s * m(hw), m(w)))

    g = ch["grip"]
    top = ch["topBar"]
    if lod == 2:
        # distant stand-in: hanger, top bar, backrest, bar and seat as boxes
        prims.box(mb, (-0.04, ax - 0.04, m(top["w"])), (0.04, ax + 0.04, 0.0), steel)
        prims.box(mb, (-0.04, ax - m(top["halfLength"]) - 0.25, m(top["w"]) - 0.04), (0.04, ax + m(top["halfLength"]) + 0.25, m(top["w"]) + 0.04), steel)
        bk, br = ch["backrest"], ch["bar"]
        prims.box(mb, (m(bk["top"][0]) - 0.02, ax - m(bk["bottomHalfWidth"]), m(bk["bottom"][1])), (m(bk["bottom"][0]), ax + m(bk["bottomHalfWidth"]), m(bk["top"][1])), steel)
        prims.box(mb, (m(br["bottom"][0]), ax - m(br["bottomHalfWidth"]), m(br["footEnd"][1])), (m(br["top"][0]) + 0.02, ax + m(br["bottomHalfWidth"]), m(br["top"][1])), steel)
        prof = ch["seat"]["profile"]
        prims.box(mb, (m(min(p[0] for p in prof)), ax - m(ch["seat"]["halfWidth"]), m(min(p[1] for p in prof))),
                  (m(max(p[0] for p in prof)), ax + m(ch["seat"]["halfWidth"]), m(max(p[1] for p in prof))), pad)
        return finish(a, mb, ch, lod)

    n_hanger, n_frame, n_rail = ((6, 5, 4), (4, 4, 3))[lod]

    # grip clamped on the rope, and the hanger's question mark into the clamp on the top bar
    prims.box(mb, (-m(g["u"]) / 2, m(g["vFrom"]), m(g["wFrom"])), (m(g["u"]) / 2, m(g["vTo"]), m(g["wTo"])), grip_st)
    hanger = [Vector((0.0, m(v), m(w))) for v, w in ch["hanger"]]
    if lod == 1:
        hanger = [hanger[i] for i in (0, 3, 6, 9, 12)]
    prims.tube_path(mb, hanger, r_hanger, n_hanger, steel)
    cl = ch["clamp"]
    if lod == 0:
        prims.box(mb, (-m(cl["u"]) / 2, ax - m(cl["halfWidth"]), m(cl["wTo"])), (m(cl["u"]) / 2, ax + m(cl["halfWidth"]), m(cl["wFrom"])), grip_st)

    # top bar and the two J side frames
    prims.cylinder(mb, sym(0, -top["halfLength"], top["w"], 1), sym(0, top["halfLength"], top["w"], 1), r_frame, n_frame, steel, caps=(False, False))
    frame = ch["sideFrame"]
    if lod == 1:
        frame = [frame[i] for i in (0, 2, 4, 6, 9, 12, 15)]
    for s in (-1, 1):
        prims.tube_path(mb, [sym(u, hw, w, s) for u, hw, w in frame], r_frame, n_frame, steel)

    # the open backrest frame behind the seat and the lowered restraint bar in front, mirror images of each
    # other in side view: each hangs from the top bar on two rods (backRods / barArms)
    def plane_u(d, w):
        (ut, wt), (ub, wb) = d["top"], d["bottom"]
        return ut + (ub - ut) * (wt - w) / (wt - wb)

    def pt(u, hw, w):
        return Vector((m(u), ax + m(hw), m(w)))

    bk = ch["backrest"]
    th, bh, c = bk["topHalfWidth"], bk["bottomHalfWidth"], bk["corner"]
    wt, wb = bk["top"][1], bk["bottom"][1]
    back = [(th - c, wt), (th, wt - c), (bh, wb + c), (bh - c, wb), (-(bh - c), wb), (-bh, wb + c), (-th, wt - c), (-(th - c), wt)]
    if lod == 1:
        back = [back[i] for i in (0, 2, 4, 6)]
    prims.tube_path(mb, [pt(plane_u(bk, w), hw, w) for hw, w in back], r_rail, n_rail, steel, closed=True)
    if lod == 0:   # legs below the backrest and the rear feet, as drawn
        for s in (-1, 1):
            prims.tube_path(mb, [sym(plane_u(bk, wb + c), bh, wb + c, s), sym(*_uhw(bk["legEnd"], bh), s), sym(*_uhw(bk["footEnd"], bh), s)],
                            r_rail, n_rail, steel)

    br = ch["bar"]
    th, bh, c = br["topHalfWidth"], br["bottomHalfWidth"], br["corner"]
    wt, wb = br["top"][1], br["bottom"][1]
    (lu, lw), (fu, fw) = br["legEnd"], br["footEnd"]
    # one closed loop: top rail, sides, legs, footrest arms and the footrest bar across their ends
    bar = [(plane_u(br, wt), th - c, wt), (plane_u(br, wt - c), th, wt - c), (plane_u(br, wb), bh, wb), (lu, bh, lw), (fu, bh, fw),
           (fu, -bh, fw), (lu, -bh, lw), (plane_u(br, wb), -bh, wb), (plane_u(br, wt - c), -th, wt - c), (plane_u(br, wt), -(th - c), wt)]
    if lod == 1:
        bar = [bar[i] for i in (0, 2, 4, 5, 7, 9)]
    prims.tube_path(mb, [pt(u, hw, w) for u, hw, w in bar], r_rail, n_rail, steel, closed=True)
    if lod == 0:
        for key in ("backRods", "barArms"):
            rd = ch[key]
            for s in (-1, 1):
                prims.tube_path(mb, [sym(u, rd["halfWidth"], w, s) for u, w in rd["path"]], r_rail, n_rail, steel, caps=False)

    # seat tray: the traced side profile extruded across the seat; padded top and front, steel below
    st = ch["seat"]
    prof = st["profile"] if lod == 0 else [st["profile"][i] for i in (0, 1, 3, 6, 8)]
    padded = st["cushionEdges"] if lod == 0 else 1
    hw = m(st["halfWidth"])
    near = [mb.vert((m(u), ax - hw, m(w))) for u, w in prof]
    far = [mb.vert((m(u), ax + hw, m(w))) for u, w in prof]
    cu = sum(u for u, _ in prof) / len(prof)
    cw = sum(w for _, w in prof) / len(prof)
    for k in range(len(prof)):
        k1 = (k + 1) % len(prof)
        (u0, w0), (u1, w1) = prof[k], prof[k1]
        out = ((u0 + u1) / 2 - cu, 0.0, (w0 + w1) / 2 - cw)
        mb.face([near[k], near[k1], far[k1], far[k]], pad if k < padded else steel, out)
    mb.face(list(near), steel, (0, -1, 0))
    mb.face(list(far), steel, (0, 1, 0))
    return finish(a, mb, ch, lod)


def _uhw(uw, hw):
    """(u, w) plus a half width -> (u, hw, w) for sym()."""
    return uw[0], hw, uw[1]


def finish(a, mb, ch, lod):
    a.body = mb
    ax = m(ch["axisV"])
    top = ch["topBar"]
    a.sockets = {"grip": (0.0, 0.0, 0.0), "bar_hinge": (m(ch["barArms"]["path"][0][0]), ax, m(top["w"]))}
    seat_w = m(max(w for _, w in ch["seat"]["profile"]))
    hw = m(ch["seat"]["halfWidth"])
    for i in range(ch["seats"]):
        vs = ax - hw + (i + 0.5) * (2 * hw / ch["seats"])
        a.sockets[f"seat_{i + 1}"] = (0.05, vs, seat_w)
    return a
