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

The double and triple (the spec's model.seats, v0.3.2) are the quad narrowed by rule (derive): the support steel keeps
its sizes, the side frames' bow scales with the seats and the top bar shortens to meet them.
"""
import copy
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

KIND = "chair"
LODS = 4


def m(x):
    return x / 1000.0


LOD2_FRAME = 1080   # the quad's LOD2 side-frame boxes (half-width, mm)


def derive(quad, seats):
    """A narrower chair from the quad's spec (owner, 2026-11-01/02): the same seat per rider (the quad's bench over its
    four seats), so the bench, bottom member, backrest and the safety bar's rail move in by dw = (4 - seats) half-seats
    a side. The side frames keep their side profile and tube and the gap to the bench at seat level (their last point,
    b), and their bow across the rope (the top bar's corner in, the bulge out past the seat) scales by seats / 4: one
    rule from the double (half the quad's bow) to the quad. The top bar ends at the frames' new top; what sits on it
    (the safety bar's sleeves) is spaced along it from the clamp out, as the D6 widens it (d6_chair.derive), and the
    web plate's lens wings move in with the bar's end, cut at the clamp. The safety bar's footrests (its arms and their
    pads) sit by the riders (D3): one in the middle of a double, two either side of a triple's middle seat (the quad's
    two between its seat pairs); one in the middle drops in front of the clamp before it turns back, and the pivot
    sleeves run across to it. The counterweight (the bar's legs and rod) moves in with the rail. Tubes, hanger, clamp
    and grip keep the quad's sizes; nothing hangs lower."""
    seat = 2 * quad["bench"]["halfWidth"] / 4   # a rider's width (486)
    dw = (seats - 4) * seat / 2                  # per side
    f = seats / 4
    b = quad["sideFrame"][-1][1]

    def bow(hw):   # a side-frame half-width: the seat-level line moves in by dw, the bow about it scales by f
        return b + dw + (hw - b) * f

    c0, c1 = quad["clamp"]["halfWidth"], quad["topBar"]["halfLength"]
    top = bow(c1)
    k = (top - c0) / (c1 - c0)

    def on_bar(x):   # a half-width on the top bar, from the clamp to the bar's end
        return x if x <= c0 else c0 + (x - c0) * k

    ch = copy.deepcopy(quad)
    ch["seats"] = seats
    ch["topBar"]["halfLength"] = top
    ch["web"]["lower"] = web_wings(quad["web"]["lower"], top - c1, c0)
    ch["sleeves"].update({"from": on_bar(quad["sleeves"]["from"]), "to": on_bar(quad["sleeves"]["to"])})
    bar = ch["bar"]
    arm = bar["arm"]
    cl = quad["clamp"]
    clear = c0 + quad["tube"]["bar"] + 30   # an arm nearer the middle than this would run through the clamp
    arms = []
    for i in range(seats // 2 if seats % 2 == 0 else seats - 1):   # 1 on a double, 2 on a triple
        hw = 0.0 if seats == 2 else (i - 0.5) * seat
        path = arm if abs(hw) >= clear else [arm[0], [arm[0][0], cl["wTo"] - 40], arm[-1]]
        arms.append((hw, path))
    bar["arms"] = arms
    if min(abs(hw) for hw, _ in arms) < ch["sleeves"]["from"]:   # the sleeves run across to a middle arm
        ch["sleeves"]["from"] = 0.0
    ch["sideFrame"] = [[u, bow(hw), w] for u, hw, w in quad["sideFrame"]]
    ch["edgeRail"] = [[u, bow(hw), w] for u, hw, w in quad["edgeRail"]]
    bar["rail"]["halfWidth"] += dw
    bar["leg"] = [[u, hw + dw, w] for u, hw, w in bar["leg"]]
    bar["rod"]["halfWidth"] += dw
    for key in ("bench", "bottomMember", "backrest"):
        ch[key]["halfWidth"] += dw
    ch["lod2Frame"] = bow(LOD2_FRAME)
    return ch


def web_wings(lower, d, clamp):
    """The web plate's lower edge with its lens wings moved d along the bar (in, d < 0), cut where they meet the
    clamp's face (inside the clamp the plate is hidden)."""
    pts = [[hw + d, w] for hw, w in lower if hw > 0]
    out = [p for p in pts if p[0] > clamp]
    inside = [p for p in pts if p[0] <= clamp]
    if inside and out:   # the cut: on the edge between the last point inside and the first one out
        (h0, w0), (h1, w1) = inside[-1], out[0]
        out.insert(0, [clamp, w0 + (w1 - w0) * (clamp - h0) / (h1 - h0)])
    return [[0, lower[0][1]]] + out


def build(spec, lod, stage):
    ch = spec["chair"]
    seats = spec.get("model", {}).get("seats")
    if seats:   # a double or triple (an overlay spec, liftkit/gauge.py)
        ch = derive(ch, seats)
    steel = Style("chair_steel", snow=0.1)   # the D6's lessons (polish): seats and grips take no snow, steel a little
    seat = Style("seat_pad")
    grip_st = Style("grip")
    grip_steel = Style("chair_steel")          # the grip runs through the bullwheel's liner: no snow
    a = Asset(spec.get("idPrefix", "sessellift_fgq4") + "_chair")
    mb = MeshBuilder()
    ax = m(ch["axisV"])
    tube = {k: m(v) / 2 for k, v in ch["tube"].items()}   # radii
    top = ch["topBar"]
    bench = ch["bench"]

    if lod == 3:   # a speck out to 800 m (polish: chairs vanished at 300 m while the towers stayed): seat and back as one
        us = [p[0] for p in bench["profile"]] + [ch["backrest"]["top"][0], ch["backrest"]["bottom"][0]]   # block, the
        ws = [p[1] for p in bench["profile"]] + [ch["backrest"]["top"][1]]                                  # top bar, hanger
        hw = m(ch.get("lod2Frame", bench["halfWidth"]))   # a double or triple: as wide as its LOD2 frames
        prims.box(mb, (m(min(us)), ax - hw, m(min(ws))), (m(max(us)), ax + hw, m(max(ws))), seat, skip=("-2",))
        prims.box(mb, (-0.03, ax - m(top["halfLength"]), m(top["w"]) - 0.03), (0.03, ax + m(top["halfLength"]), m(top["w"]) + 0.03), steel, skip=("-2",))
        prims.box(mb, (-0.04, -0.04, m(top["w"])), (0.04, min(0.3, ax + m(top["halfLength"])), 0.0), steel, skip=("-2",))
        return finish(a, mb, ch)
    if lod == 2:   # distant stand-in: hanger, top bar, side frames, bench and backrest as boxes
        prims.box(mb, (-0.04, -0.04, m(top["w"])), (0.04, 0.3, 0.0), steel)
        prims.box(mb, (-0.03, ax - m(top["halfLength"]) - 0.25, m(top["w"]) - 0.03), (0.03, ax + m(top["halfLength"]) + 0.25, m(top["w"]) + 0.03), steel)
        fr = ch.get("lod2Frame", LOD2_FRAME)
        for s in (-1, 1):
            prims.box(mb, (-0.30, ax + s * m(fr) - 0.03, m(-2600)), (0.0, ax + s * m(fr) + 0.03, m(top["w"])), steel)
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

    # top bar (the frames' tube, or the D4's own on the D6's clamp), the web plate under it and the safety bar's pivot
    # sleeves on it
    prims.cylinder(mb, sym(0, -top["halfLength"], top["w"]), sym(0, top["halfLength"], top["w"]), tube.get("topBar", tube["frame"]), n_frame,
                   steel, caps=(False, False))
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
