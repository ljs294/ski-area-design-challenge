"""Sessellift FGQ-4 quad chair, built along the drawn outlines (sessellift_fgq4.json "chair", checked by the
Gate 1 review):

  the grip and its spring cap on the rope; the question-mark hanger into a clamp on the top bar, with the
  lens-shaped web plate under the bar and the safety bar's pivot sleeves on it; two side frames, each a
  closed loop: a 60 mm tube down behind the seat and forward under it, then a 52 mm edge rail around the
  front and back along the seat edge; a thin bench tilted back, a bottom cross member and a low slatted
  backrest; and the safety bar, raised behind the seat on arms from the front sleeves, with its handles,
  footrest stubs and the rod through its eyes.

The origin is the grip on the rope. Chair frame (the lift frame's axes, reused): u = direction of travel
(Unity +Z), v = outboard, away from the bullwheel (Unity +X), w = up. The chair is symmetric about
v = axisV (the drawn chair sits 32 mm inboard of the rope).
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

KIND = "chair"
LODS = 3


def m(x):
    return x / 1000.0


def build(spec, lod, stage):
    ch = spec["chair"]
    steel = Style("chair_steel", snow=0.4)
    seat = Style("seat_pad", snow=1.0)
    grip_st = Style("grip")
    a = Asset("sessellift_fgq4_chair")
    mb = MeshBuilder()
    ax = m(ch["axisV"])
    tube = {k: m(v) / 2 for k, v in ch["tube"].items()}   # radii

    def sym(u, hw, w, s=1):
        return Vector((m(u), ax + s * m(hw), m(w)))

    top = ch["topBar"]
    frame = ch["sideFrame"]
    bench = ch["bench"]

    if lod == 2:   # distant stand-in: hanger, top bar, side frames and bench as boxes
        prims.box(mb, (-0.04, -0.04, m(top["w"])), (0.04, 0.3, 0.0), steel)
        prims.box(mb, (-0.03, ax - m(top["halfLength"]) - 0.25, m(top["w"]) - 0.03), (0.03, ax + m(top["halfLength"]) + 0.25, m(top["w"]) + 0.03), steel)
        for s in (-1, 1):
            prims.box(mb, (-0.30, ax + s * m(1080) - 0.03, m(-2600)), (0.0, ax + s * m(1080) + 0.03, m(top["w"])), steel)
        us = [p[0] for p in bench["profile"]]
        ws = [p[1] for p in bench["profile"]]
        prims.box(mb, (m(min(us)), ax - m(bench["halfWidth"]), m(min(ws))), (m(max(us)), ax + m(bench["halfWidth"]), m(max(ws))), seat)
        return finish(a, mb, ch)

    near = lod == 0
    n_hanger, n_frame, n_thin = (6, 5, 4) if near else (4, 4, 3)

    # hanger: the question mark from the grip into the clamp; up close it starts where the grip's socket ends
    if near:
        hanger = [Vector((0.0, m(v), m(w))) for v, w in parts.hanger_from_socket(ch["hanger"])]
    else:
        hanger = [Vector((0.0, m(v), m(w))) for v, w in ch["hanger"]]
        hanger = [hanger[i] for i in (0, 3, 6, 9, 12)]
    rings = prims.tube_path(mb, hanger, tube["hanger"], n_hanger, steel, caps=False)

    # the grip on the rope (fixed_grip.json, every fixed-grip chair's), flowing down into the hanger up close
    joint = parts.hanger_joint(rings, hanger, tube["hanger"], steel) if near else None
    parts.fixed_grip(mb, lod, steel, dark=Style("trim_dark"), metal=Style("machined"), hanger=joint)
    cl = ch["clamp"]
    prims.box(mb, (-m(cl["u"]) / 2, ax - m(cl["halfWidth"]), m(cl["wTo"])), (m(cl["u"]) / 2, ax + m(cl["halfWidth"]), m(cl["wFrom"])), grip_st)

    # top bar, the web plate under it and the safety bar's pivot sleeves on it
    prims.cylinder(mb, sym(0, -top["halfLength"], top["w"]), sym(0, top["halfLength"], top["w"]), tube["frame"], n_frame, steel, caps=(False, False))
    if near:
        web = ch["web"]
        lower = web["lower"]
        poly = [(-m(lower[-1][0]), m(web["top"])), (m(lower[-1][0]), m(web["top"]))]
        poly += [(m(hw), m(w)) for hw, w in reversed(lower) if hw > 0]
        poly += [(-m(hw), m(w)) for hw, w in lower if hw > 0]
        prims.plate(mb, poly, (-m(web["thick"]) / 2, ax, 0), Vector((0, 1, 0)), Vector((0, 0, 1)), Vector((1, 0, 0)), m(web["thick"]), steel)
        parts.restraint_sleeves(mb, ch["sleeves"], tube["sleeve"], sym, steel)

    # side frames: 60 mm loop down behind and under the seat, then the 52 mm edge rail around the front and back
    main = frame if near else [frame[i] for i in (0, 3, 4, 5, 7, 9, 10)]
    edge = [ch["edgeRail"][i] for i in (0, 2, 3, 4, 6, 7, 8, 9)]
    for s in (-1, 1):
        prims.tube_path(mb, [sym(u, hw, w, s) for u, hw, w in main], tube["frame"], n_frame, steel, caps=near)
        if near:
            prims.tube_path(mb, [sym(u, hw, w, s) for u, hw, w in edge], tube["edge"], n_thin, steel)

    # bench (thin, tilted back), bottom cross member and the low slatted backrest
    hwb = m(bench["halfWidth"])
    prims.prism(mb, [(m(u), m(w)) for u, w in bench["profile"]], (0, ax - hwb, 0), Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, 1, 0)),
                2 * hwb, seat)
    bm = ch["bottomMember"]
    if near:
        half = m(bm["size"]) / 2
        prims.box(mb, (m(bm["u"]) - half, ax - m(bm["halfWidth"]), m(bm["w"]) - half), (m(bm["u"]) + half, ax + m(bm["halfWidth"]), m(bm["w"]) + half), steel, skip=("-1", "+1"))
    br = ch["backrest"]
    (tu, tw), (bu, bw) = br["top"], br["bottom"]
    along = Vector((m(tu - bu), 0, m(tw - bw)))
    length = along.length
    along.normalize()
    across = Vector((0, 1, 0))
    normal = along.cross(across).normalized()
    slats = br["slats"] if near else 1
    gap = 0.4 if near else 0.0
    pitch = length / slats
    for k in range(slats):
        centre = Vector((m(bu), ax, m(bw))) + along * (pitch * (k + 0.5))
        prims.obox(mb, centre, (along, across, normal), (pitch * (1 - gap) / 2 if slats > 1 else length / 2, m(br["halfWidth"]), m(br["thick"]) / 2), seat,
                   skip=("-1", "+1"))

    # safety bar, raised behind the seat: arms from the front sleeves, rail, legs, footrest stubs, rod, handles
    parts.restraint_bar(mb, ch["bar"], tube, sym, near, n_thin, steel, grip_st)
    return finish(a, mb, ch)


def finish(a, mb, ch):
    a.body = mb
    ax = m(ch["axisV"])
    pivot = ch["bar"]["pivot"]
    a.sockets = {"grip": (0.0, 0.0, 0.0), "bar_hinge": (m(pivot[0]), ax, m(pivot[1]))}
    prof = ch["bench"]["profile"]
    top_w = m(max(w for _, w in prof))
    hw = m(ch["bench"]["halfWidth"])
    for i in range(ch["seats"]):
        vs = ax - hw + (i + 0.5) * (2 * hw / ch["seats"])
        a.sockets[f"seat_{i + 1}"] = (0.14, vs, top_w - 0.05)
    return a
