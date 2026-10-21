"""Sessellift D6 six-seat chair (sessellift_d6.json "chair"): the Sessellift quad chair made wider and hung lower by
rule (owner, LM2 and LM8), on the Chairworks detachable grip.

  widen  every half-width of the quad's chair grows by widen x t: t = 0 at the hanger clamp, 1 at the top bar's end
         and beyond, linear between; widen = (seats - 4) / 2 x seatPitch, so the bench takes seats x seatPitch.
  drop   every height under the hanger moves down by drop, so the bench hangs where the drawing hangs it.
  hanger the drawing's: out of the grip's arm, down and outboard round the station frame, in level through the clamp.

The quad's body code builds it (chair.body); the grip is chairworks_chair.detach_grip. The safety bar gains a middle
leg with a footrest, as drawn. The origin is the grip on the rope; up close the grip and the hanger are moving parts
(contract M3): the grip along the rope, the hanger and all under it plumb about the axis across the rope through the
end of the grip's arm, where the hanger leaves it. Chair frame as the quad's: u = travel, v = outboard, w = up.
"""
import copy
import json
import os

from mathutils import Vector

import chair
import chairworks_chair
from liftkit import prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "chair_detach"
LODS = 3
SPEC = "sessellift_d6.json"
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def m(x):
    return x / 1000.0


def derive(d6):
    """The six-seat chair's body spec from the quad's, by the widen and drop rules (mm)."""
    quad = json.load(open(os.path.join(HERE, "sessellift_fgq4.json"), encoding="utf-8"))["chair"]
    widen = (d6["seats"] - quad["seats"]) / 2 * d6["seatPitch"]
    c0, c1, dw = d6["clampHalf"], d6["topBarHalf"], d6["drop"]

    def hw(x):
        t = min(1.0, max(0.0, (abs(x) - c0) / (c1 - c0)))
        return x + (widen * t if x >= 0 else -widen * t)

    ch = copy.deepcopy(quad)
    ch["seats"], ch["axisV"] = d6["seats"], d6["axisV"]
    ch["clamp"]["wFrom"] += dw
    ch["clamp"]["wTo"] += dw
    ch["topBar"] = {"w": quad["topBar"]["w"] + dw, "halfLength": hw(quad["topBar"]["halfLength"])}
    ch["web"]["top"] += dw
    ch["web"]["lower"] = [[hw(a), b + dw] for a, b in quad["web"]["lower"]]
    ch["sleeves"] = dict(quad["sleeves"], w=quad["sleeves"]["w"] + dw, **{"from": hw(quad["sleeves"]["from"])},
                         to=hw(quad["sleeves"]["to"]))
    ch["sideFrame"] = [[u, hw(h), w + dw] for u, h, w in quad["sideFrame"]]
    ch["edgeRail"] = [[u, hw(h), w + dw] for u, h, w in quad["edgeRail"]]
    b = quad["bar"]
    ch["bar"] = {"pivot": [b["pivot"][0], b["pivot"][1] + dw], "arm": [[u, w + dw] for u, w in b["arm"]],
                 "armHalfWidth": hw(b["armHalfWidth"]),
                 "rail": dict(b["rail"], w=b["rail"]["w"] + dw, halfWidth=hw(b["rail"]["halfWidth"])),
                 "leg": [[u, hw(h), w + dw] for u, h, w in b["leg"]],
                 "rod": dict(b["rod"], w=b["rod"]["w"] + dw, halfWidth=hw(b["rod"]["halfWidth"])),
                 "handle": {"from": [b["handle"]["from"][0], b["handle"]["from"][1] + dw],
                            "to": [b["handle"]["to"][0], b["handle"]["to"][1] + dw], "halfWidth": hw(b["handle"]["halfWidth"])}}
    ch["bench"] = {"halfWidth": hw(quad["bench"]["halfWidth"]), "profile": [[u, w + dw] for u, w in quad["bench"]["profile"]]}
    ch["bottomMember"] = dict(quad["bottomMember"], w=quad["bottomMember"]["w"] + dw, halfWidth=hw(quad["bottomMember"]["halfWidth"]))
    br = quad["backrest"]
    ch["backrest"] = dict(br, top=[br["top"][0], br["top"][1] + dw], bottom=[br["bottom"][0], br["bottom"][1] + dw],
                          halfWidth=hw(br["halfWidth"]))
    ch["tube"] = dict(quad["tube"], hanger=d6["tubeHanger"])
    ch["hanger"] = d6["hanger"]
    return ch


def grip_spec():
    return json.load(open(os.path.join(HERE, "chairworks_chair.json"), encoding="utf-8"))["gripDetach"]


def build(spec, lod, stage):
    d6 = spec["chair"]
    ch = derive(d6)
    steel = Style("chair_steel", snow=0.4)
    seat = Style("seat_pad", snow=1.0)
    grip_st = Style("grip", snow=0.3)
    wheel_st = Style("rubber")
    a = Asset("sessellift_d6_chair")
    mb = MeshBuilder()
    ax = m(ch["axisV"])
    tube = {k: m(v) / 2 for k, v in ch["tube"].items()}
    top, bench = ch["topBar"], ch["bench"]
    g = grip_spec()

    if lod == 2:   # distant stand-in: the grip, hanger, top bar, side frames and bench as boxes
        prims.box(mb, (m(-277.0), m(-25.0), m(-68.0)), (m(277.0), m(380.0), m(99.0)), grip_st)
        lo = min(w for _, w in d6["hanger"])
        prims.box(mb, (-0.045, m(450), m(lo)), (0.045, m(560), m(-60)), steel)
        prims.box(mb, (-0.03, ax - m(top["halfLength"]) - 0.25, m(top["w"]) - 0.03),
                  (0.03, ax + m(top["halfLength"]) + 0.25, m(top["w"]) + 0.03), steel)
        side = max(h for _, h, _ in ch["sideFrame"]) - 48
        low = min(w for _, _, w in ch["sideFrame"]) + 57
        for s in (-1, 1):
            prims.box(mb, (-0.30, ax + s * m(side) - 0.03, m(low)), (0.0, ax + s * m(side) + 0.03, m(top["w"])), steel)
        us = [p[0] for p in bench["profile"]]
        ws = [p[1] for p in bench["profile"]]
        prims.box(mb, (m(min(us)), ax - m(bench["halfWidth"]), m(min(ws))), (m(max(us)), ax + m(bench["halfWidth"]), m(max(ws))), seat)
        return finish(a, mb, ch, d6)

    near = lod == 0
    n_hanger, n_frame, n_thin = (6, 5, 4) if near else (4, 4, 3)
    # the hanger, out of the grip's arm through a cast reducer (as the Chairworks detachable chair's) and into the clamp
    mb.group = "hanger"
    pts = d6["hanger"] if near else [d6["hanger"][i] for i in d6["hangerFar"]]
    reduce_from = None
    if near:
        reduce_from, pts = (pts[0][0] - 3.0, pts[0][1]), pts[1:]
    hanger_pts = [(0.0, m(v), m(w)) for v, w in pts]
    rings = prims.tube_path(mb, hanger_pts, tube["hanger"], n_hanger, steel, caps=not near)
    if reduce_from is not None:   # the reducer turns inside the grip's arm with the hanger (its own piece for the sweep)
        arm = g["arm"]["u"]
        with mb.grouped("hanger#socket"):
            chairworks_chair.reducer(mb, rings[0], reduce_from, (arm[1] - arm[0]) / 2, steel)

    with mb.grouped("grip"):
        if near:
            chairworks_chair.detach_grip(mb, g, grip_st, wheel_st, Style("trim_dark"), Style("machined"))
        else:   # as the Chairworks chair's LOD1: the carriage as one block under its arch, the arm, wheels and springs
            tp = g["topPlate"]["top"]
            hull = [tp[0], (0.0, max(w for _, w in tp)), tp[-1], (189.0, -4.0), (-189.0, -4.0)]
            prims.prism(mb, [(m(u), m(w)) for u, w in hull], Vector((0.0, m(60.0), 0.0)), U, W, V, m(36.0), grip_st)
            ar = g["arm"]["outline"]
            arm = [ar[1], ar[2], (193.0, -45.0), ar[5], ar[6], ar[0]]
            prims.prism(mb, [(m(v), m(w)) for v, w in arm], Vector((m(g["arm"]["u"][0]), 0.0, 0.0)), V, W, U,
                        m(g["arm"]["u"][1] - g["arm"]["u"][0]), grip_st)
            wh = g["wheels"]
            for u in wh["u"]:
                ty = wh["tyre"]
                prims.cylinder(mb, (m(u), m(ty["v"][0]), m(wh["w"])), (m(u), m(ty["v"][1]), m(wh["w"])), m(ty["dia"]) / 2, 4,
                               wheel_st, caps=(True, False))
            for c, axis, sp in chairworks_chair.springs(g):
                s0, s1 = sp["seats"]["s"]
                prims.cylinder(mb, tuple(c + axis * m(s0)), tuple(c + axis * m(s1)), m(sp["coilDia"] + sp["wire"]) / 2, 4,
                               grip_st, caps=(False, False))

    chair.body(mb, ch, near, n_frame, n_thin, tube, steel, seat, grip_st)
    if d6.get("middleLeg"):   # the bar's middle leg down to its footrest, as drawn
        leg = ch["bar"]["leg"]
        path = [Vector((m(u), ax, m(w))) for u, _, w in (leg if near else [leg[0], leg[3], leg[4]])]
        prims.tube_path(mb, path, tube["bar"], n_thin, steel)
    return finish(a, mb, ch, d6)


def finish(a, mb, ch, d6):
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
    v0, w0 = d6["hanger"][0]   # the end of the grip's arm, where the hanger leaves it
    a.pivots = {"grip": {"pos": (0.0, 0.0, 0.0), "axis": (0.0, 1.0, 0.0)},
                "hanger": {"pos": (0.0, m(v0), m(w0)), "axis": (0.0, 1.0, 0.0), "parent": "grip", "hinge": True}}
    a.socket_parents = {k: "hanger" for k in a.sockets if k != "grip"}
    benchUnder = min(w for _, w in prof)
    a.dims.update({"seats": ch["seats"], "benchWidth": round(2 * ch["bench"]["halfWidth"]),
                   "drop": round(-min(benchUnder, ch["bottomMember"]["w"] - ch["bottomMember"]["size"] / 2)),
                   "chairHalfWidth": round(max(h for _, h, _ in ch["sideFrame"]) + ch["tube"]["frame"] / 2),
                   "axisV": ch["axisV"], "ropeW": 3600})
    return a
