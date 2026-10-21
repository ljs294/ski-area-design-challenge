"""Sessellift D6 six-seat chair (sessellift_d6.json "chair"): the Sessellift quad chair made wider and hung lower by
rule (owner, LM2 and LM8), on the Sessellift detachable grip (sessellift_detach_grip.json, owner LM10).

  widen  every half-width of the quad's chair grows by widen x t: t = 0 at the hanger clamp, 1 at the top bar's end
         and beyond, linear between; widen = (seats - 4) / 2 x seatPitch, so the bench takes seats x seatPitch.
  drop   every height under the hanger moves down by drop, so the bench hangs where the drawing hangs it.
  hanger the drawing's: from the grip's socket under the arm's head, down and outboard round the station's belly,
         in level through the clamp.

The quad's body code builds it (chair.body); the grip is liftkit.parts.detach_grip and the hanger's arm
liftkit.parts.detach_grip_arm. The safety bar gains a middle leg with a footrest, as drawn. The origin is the grip on
the rope; up close the grip and the hanger are moving parts (contract M3): the grip along the rope, and the hanger
(its arm, guide roller and tube, and everything under it) plumb about the arm's axis, across the rope. Chair frame as
the quad's: u = travel, v = outboard, w = up.
"""
import copy
import json
import os

from mathutils import Vector

import chair
from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

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


def grip_styles():
    return {"alu": Style("aluminium", snow=0.3), "black": Style("rubber", snow=0.5), "blue": Style("spring_blue", snow=0.3),
            "cream": Style("roller_cream"), "rod": Style("machined")}


def build(spec, lod, stage):
    d6 = spec["chair"]
    ch = derive(d6)
    steel = Style("chair_steel", snow=0.4)
    seat = Style("seat_pad", snow=1.0)
    gst = grip_styles()
    a = Asset("sessellift_d6_chair")
    mb = MeshBuilder()
    ax = m(ch["axisV"])
    tube = {k: m(v) / 2 for k, v in ch["tube"].items()}
    top, bench = ch["topBar"], ch["bench"]

    if lod == 2:   # distant stand-in: the grip, hanger, top bar, side frames and bench as boxes
        parts.detach_grip(mb, lod, gst)
        lo = min(w for _, w in d6["hanger"])
        prims.box(mb, (-0.045, m(430), m(lo)), (0.045, m(560), m(-90)), steel)
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
    # the hanger: its arm under the grip (the arm's inboard section turns inside the grip's body: its own piece for the
    # clash sweep), and the tube from the socket under the arm's head down round the belly and into the clamp
    mb.group = "hanger"
    parts.detach_grip_arm(mb, lod, gst, socket_group="hanger#socket")
    pts = d6["hanger"] if near else [d6["hanger"][i] for i in d6["hangerFar"]]
    prims.tube_path(mb, [(0.0, m(v), m(w)) for v, w in pts], tube["hanger"], n_hanger, steel, caps=False)
    with mb.grouped("grip"):
        parts.detach_grip(mb, lod, gst)

    chair.body(mb, ch, near, n_frame, n_thin, tube, steel, seat, Style("grip"))
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
    g = parts.detach_grip_spec()
    # the hanger hinges on the arm's axis, across the rope, under the grip
    a.pivots = {"grip": {"pos": (0.0, 0.0, 0.0), "axis": (0.0, 1.0, 0.0)},
                "hanger": {"pos": (0.0, m(g["socket"]["v"]), m(g["arm"]["axisW"])), "axis": (0.0, 1.0, 0.0), "parent": "grip",
                           "hinge": True}}
    a.socket_parents = {k: "hanger" for k in a.sockets if k != "grip"}
    bench_under = min(w for _, w in prof)
    a.dims.update({"seats": ch["seats"], "benchWidth": round(2 * ch["bench"]["halfWidth"]),
                   "drop": round(-min(bench_under, ch["bottomMember"]["w"] - ch["bottomMember"]["size"] / 2)),
                   "chairHalfWidth": round(max(h for _, h, _ in ch["sideFrame"]) + ch["tube"]["frame"] / 2),
                   "axisV": ch["axisV"], "ropeW": 3600})
    return a
