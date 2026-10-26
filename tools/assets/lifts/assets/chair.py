"""Sessellift FGQ-4 quad chair, built along the drawn outlines (sessellift_fgq4.json "chair", checked by the
Gate 1 review):

  the grip and its spring cap on the rope; the question-mark hanger into a clamp on the top bar, with the
  lens-shaped web plate under the bar and the safety bar's pivot sleeves on it; two side frames, each a
  closed loop: a 60 mm tube down behind the seat and forward under it, then a 52 mm edge rail around the
  front and back along the seat edge; a thin bench tilted back, a bottom cross member and a low slatted
  backrest; and the safety bar, raised behind the seat on arms from the front sleeves, with its handles,
  footrest stubs and the rod through its eyes.

The origin is the grip on the rope. Up close (LOD0-1) the grip and the hanger are moving parts (task P3-07): the
grip clamps along the rope, and the hanger with everything under it hangs plumb, turning about the grip's own stack
axis (where the grip's arm reaches across to the rope), with no geometry added. Chair frame (the lift frame's axes, reused): u = direction of travel
(Unity +Z), v = outboard, away from the bullwheel (Unity +X), w = up. The chair is symmetric about
v = axisV (the drawn chair sits 32 mm inboard of the rope).
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

KIND = "chair"
LODS = 4


def m(x):
    return x / 1000.0


def build(spec, lod, stage):
    ch = spec["chair"]
    steel = Style("chair_steel", snow=0.1)   # the D6's lessons (polish): seats and grips take no snow, steel a little
    seat = Style("seat_pad")
    grip_st = Style("grip")
    grip_steel = Style("chair_steel")          # the grip runs through the bullwheel's liner: no snow
    a = Asset("sessellift_fgq4_chair")
    mb = MeshBuilder()
    ax = m(ch["axisV"])
    tube = {k: m(v) / 2 for k, v in ch["tube"].items()}   # radii
    top = ch["topBar"]
    bench = ch["bench"]

    if lod == 3:   # a speck out to 800 m (polish: chairs vanished at 300 m while the towers stayed): seat and back as one
        us = [p[0] for p in bench["profile"]] + [ch["backrest"]["top"][0], ch["backrest"]["bottom"][0]]   # block, the
        ws = [p[1] for p in bench["profile"]] + [ch["backrest"]["top"][1]]                                  # top bar, hanger
        prims.box(mb, (m(min(us)), ax - m(bench["halfWidth"]), m(min(ws))), (m(max(us)), ax + m(bench["halfWidth"]), m(max(ws))), seat, skip=("-2",))
        prims.box(mb, (-0.03, ax - m(top["halfLength"]), m(top["w"]) - 0.03), (0.03, ax + m(top["halfLength"]), m(top["w"]) + 0.03), steel, skip=("-2",))
        prims.box(mb, (-0.04, -0.04, m(top["w"])), (0.04, 0.3, 0.0), steel, skip=("-2",))
        return finish(a, mb, ch)
    if lod == 2:   # distant stand-in: hanger, top bar, side frames, bench and backrest as boxes
        prims.box(mb, (-0.04, -0.04, m(top["w"])), (0.04, 0.3, 0.0), steel)
        prims.box(mb, (-0.03, ax - m(top["halfLength"]) - 0.25, m(top["w"]) - 0.03), (0.03, ax + m(top["halfLength"]) + 0.25, m(top["w"]) + 0.03), steel)
        for s in (-1, 1):
            prims.box(mb, (-0.30, ax + s * m(1080) - 0.03, m(-2600)), (0.0, ax + s * m(1080) + 0.03, m(top["w"])), steel)
        us = [p[0] for p in bench["profile"]]
        ws = [p[1] for p in bench["profile"]]
        prims.box(mb, (m(min(us)), ax - m(bench["halfWidth"]), m(min(ws))), (m(max(us)), ax + m(bench["halfWidth"]), m(max(ws))), seat)
        backrest(mb, ch, ax, seat, 1, 0.0)
        return finish(a, mb, ch)

    near = lod == 0
    n_hanger, n_frame, n_thin = (6, 5, 4) if near else (4, 4, 3)

    # Posable (contract P3-07 M3, the owner's call: no new geometry): the grip clamps along the rope and the hanger
    # swings plumb from it about the grip's stack axis, the axis of the arm reaching across to the rope (hinge_point).
    # The cast housing is turned round that axis, so the hanger's socket, which flows up into the housing, turns inside
    # it without opening a gap; far out the hanger starts inside the grip's bar, on the same axis.
    mb.group = "hanger"
    # hanger: the question mark from the grip into the clamp; up close it starts where the grip's socket ends
    if near:
        hanger = [Vector((0.0, m(v), m(w))) for v, w in parts.hanger_from_socket(ch["hanger"])]
    else:
        hanger = [Vector((0.0, m(v), m(w))) for v, w in ch["hanger"]]
        hanger = [hanger[i] for i in (0, 3, 6, 9, 12)]
    rings = prims.tube_path(mb, hanger, tube["hanger"], n_hanger, steel, caps=False, smooth=True)

    # the grip on the rope (fixed_grip.json, every fixed-grip chair's), flowing down into the hanger up close; the
    # socket rides with the hanger
    with mb.grouped("grip"):
        joint = parts.hanger_joint(rings, hanger, tube["hanger"], steel) if near else None
        parts.fixed_grip(mb, lod, grip_steel, dark=Style("trim_dark"), metal=Style("machined"), hanger=joint,
                         socket_group="hanger#socket")
    body(mb, ch, near, n_frame, n_thin, tube, steel, seat, grip_st)
    return finish(a, mb, ch)


def body(mb, ch, near, n_frame, n_thin, tube, steel, seat, grip_st):
    """Everything under the hanger, up close and at LOD1 (the D6 chair builds the same body, made wider by rule): the
    clamp, the top bar with its web plate and the safety bar's sleeves, the side frames and edge rails, the bench, the
    bottom member, the backrest and the safety bar. tube: radii (m)."""
    ax = m(ch["axisV"])

    def sym(u, hw, w, s=1):
        return Vector((m(u), ax + s * m(hw), m(w)))

    top = ch["topBar"]
    frame = ch["sideFrame"]
    bench = ch["bench"]
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
        prims.tube_path(mb, [sym(u, hw, w, s) for u, hw, w in main], tube["frame"], n_frame, steel, caps=near, smooth=True)
        if near:
            prims.tube_path(mb, [sym(u, hw, w, s) for u, hw, w in edge], tube["edge"], n_thin, steel, smooth=True)

    # bench (thin, tilted back), bottom cross member and the low slatted backrest
    hwb = m(bench["halfWidth"])
    prims.prism(mb, [(m(u), m(w)) for u, w in bench["profile"]], (0, ax - hwb, 0), Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, 1, 0)),
                2 * hwb, seat)
    bm = ch["bottomMember"]
    if near:
        half = m(bm["size"]) / 2
        prims.box(mb, (m(bm["u"]) - half, ax - m(bm["halfWidth"]), m(bm["w"]) - half), (m(bm["u"]) + half, ax + m(bm["halfWidth"]), m(bm["w"]) + half), steel, skip=("-1", "+1"))
    backrest(mb, ch, ax, seat, ch["backrest"]["slats"] if near else 1, 0.4 if near else 0.0)

    # safety bar, raised behind the seat: arms from the front sleeves, rail, legs, footrest stubs, rod, handles
    parts.restraint_bar(mb, ch["bar"], tube, sym, near, n_thin, steel, grip_st)


def backrest(mb, ch, ax, seat, slats, gap):
    """The low slatted backrest (one board far out)."""
    br = ch["backrest"]
    (tu, tw), (bu, bw) = br["top"], br["bottom"]
    along = Vector((m(tu - bu), 0, m(tw - bw)))
    length = along.length
    along.normalize()
    across = Vector((0, 1, 0))
    normal = along.cross(across).normalized()
    pitch = length / slats
    for k in range(slats):
        centre = Vector((m(bu), ax, m(bw))) + along * (pitch * (k + 0.5))
        prims.obox(mb, centre, (along, across, normal), (pitch * (1 - gap) / 2 if slats > 1 else length / 2, m(br["halfWidth"]), m(br["thick"]) / 2), seat,
                   skip=("-1", "+1"))


def hinge_point(ch):
    """Where the hanger hinges on the grip: on the grip's stack axis (the cast arm, housing and bolt turned round it,
    reaching across to the rope), at the middle of the socket the hanger flows up into."""
    g = parts.fixed_grip_spec()
    top = g["socket"]["top"]
    return Vector((0.0, m((top["v"][0] + top["v"][1]) / 2), m(g["axisW"])))


def finish(a, mb, ch):
    mb.group = None
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
    # the grip takes the rope's slope about the axis across the rope; the hanger, its seats and its safety bar hang
    # plumb, turning about the grip's stack axis (far out the chair is one part and hangs plumb)
    a.pivots = {"grip": {"pos": (0.0, 0.0, 0.0), "axis": (0.0, 1.0, 0.0)},
                "hanger": {"pos": tuple(hinge_point(ch)), "axis": (0.0, 1.0, 0.0), "parent": "grip", "hinge": True}}
    a.socket_parents = {k: "hanger" for k in a.sockets if k != "grip"}
    return a
