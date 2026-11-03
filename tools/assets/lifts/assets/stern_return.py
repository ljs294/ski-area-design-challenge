"""Sessellift Stern return terminal (top station; sessellift_stern4.json "return"), from the owner's layout:

  a chamfered box column standing in the ground, its narrower upper section up to the walkway; a flanged box boom
  from it back past the axle, the exposed bullwheel turning on a pedestal on the boom's nose; a narrow walkway on the
  axis over the column and boom, railed on the departing side only; guide sheaves on pedestals at the ends of a cross
  member ahead of the wheel, a rope guard outboard of the arriving rope braced back to that cross member; and the
  line frame ahead of the column: a box girder out to a square tube crossbeam with the support rows hanging from its
  ends and a lifting portal standing on it.

Loaded chairs arrive on the left rope (v-) and unload over the column (chairUnloadU); the bullwheel turns
counter-clockwise seen from above. Bottom drive only (owner, 2026-11-02): the support rows are the terminal's one
head, the top (liftkit/swap.py), with the 6 and 8 sheave rows as its variants (as the other fixed grips' top heads);
it gains no bottom head. Most of the drawn steel is data (spec "steel", stern_heads.members). Dimensions:
millimetres; the origin on the bullwheel axle at the loading level.
"""
import math

from mathutils import Vector

from liftkit import heads, parts, prims, swap
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder
from liftkit.prims import U, V, W

import drive_terminal
import return_terminal
import stern_heads
from stern_heads import m

KIND = "terminal"
CHAIR = "chair"   # the chair the build sends round the bullwheel (liftkit/fit.py fixed)
LODS = 4
SPEC = "sessellift_stern4.json"


def styles(lod):
    st = return_terminal.styles()
    st["yellow"] = drive_terminal.styles(lod)["yellow"]
    return st


def chamfered(mb, u, hv, lo, hi, ch, style):
    """A box u[0]..u[1] by +-hv (m) from lo to hi with its four vertical edges chamfered by ch (m)."""
    u0, u1 = u
    plan = [(u0 + ch, -hv), (u1 - ch, -hv), (u1, -hv + ch), (u1, hv - ch), (u1 - ch, hv), (u0 + ch, hv), (u0, hv - ch), (u0, -hv + ch)]
    prims.prism(mb, plan, (0, 0, lo), U, V, W, hi - lo, style)


def column(mb, r, lod, st):
    """The column standing in the ground on its pier (no base plate is drawn) and the narrower upper section up to
    the walkway, both with chamfered edges. Returns the pier top."""
    co, pr = r["column"], r["pier"]
    hv = m(co["v"]) / 2
    u0, u1 = co["u"]
    top = stern_heads.pier(mb, (u0, -co["v"] / 2), (u1, co["v"] / 2), co["bottom"], pr["bottom"], pr["margin"], st)
    ch = m(co["chamfer"]) if lod <= 1 else 0.0
    chamfered(mb, (m(u0), m(u1)), hv, m(co["bottom"]), m(co["top"]), ch, st["steel"])
    up = co["upper"]
    chamfered(mb, (m(up["u"][0]), m(up["u"][1])), hv, m(co["top"]), m(up["top"]), ch, st["steel"])
    return top


def deck(mb, r, lod, st):
    """The walkway on the axis over the column and the boom: grating flush with the top of its frame of 102 mm
    channels (the toe band), on a stringer of two channels; the railing on the departing side only, as two bent
    hoops of pipe with posts and a mid rail between their ends."""
    dk = r["deck"]
    u0, u1, hv = m(dk["u"][0]), m(dk["u"][1]), m(dk["v"]) / 2
    top, fr = m(dk["top"]), m(dk["frame"])
    parts.deck(mb, (u0, -hv, top - m(dk["thick"])), (u1, hv, top), lod, st["galv"], channels=False)
    if lod <= 2:   # the frame's channels round the grating
        e = m(dk["edge"])
        for lo, hi in (((u0, -hv, top - fr), (u1, -hv + e, top)), ((u0, hv - e, top - fr), (u1, hv, top)),
                       ((u0, -hv, top - fr), (u0 + e, hv, top)), ((u1 - e, -hv, top - fr), (u1, hv, top))):
            prims.box(mb, lo, hi, st["galv"])
    rl = dk["railing"]
    if lod >= 3:
        return
    v_r = m(rl["v"])
    foot, rail, mid = m(rl["foot"]), m(rl["top"]), m(rl["mid"])
    for hp in rl["hoops"]:   # each hoop: up the end posts, rounded corners, the top rail; posts and the mid rail between
        a, b = m(hp[0]), m(hp[-1])
        stern_heads.tube(mb, [(a, v_r, foot), (a, v_r, rail), (b, v_r, rail), (b, v_r, foot)], m(rl["dia"]), lod, st["galv"],
                         corner=m(rl["corner"]))
        if lod <= 1:
            for p in hp[1:-1]:
                stern_heads.pipe(mb, (m(p), v_r, foot), (m(p), v_r, rail), m(rl["dia"]), lod, st["galv"], sides=(8, 6))
            stern_heads.pipe(mb, (a, v_r, mid), (b, v_r, mid), m(rl["dia"]), lod, st["galv"], sides=(8, 6), caps=(False, False))


def guides(mb, a, c, r, hg, lod, st):
    """The guide sheaves (rope on top), each on its axle from the hanger plates inboard of it (their steel: spec
    "steel")."""
    rope, rope_r = m(c["ropeElevation"]), m(c["ropeDiameter"]) / 2
    gp = r["guideSheaves"]
    for side, name in ((-1, "l1"), (1, "r1")):
        centre = Vector((m(gp["u"]), side * hg, rope - rope_r - m(gp["dia"]) / 2))
        target = MeshBuilder() if lod <= 1 else mb
        if lod <= 2:
            parts.sheave(target, centre, V, m(gp["dia"]), m(gp["width"]), lod, st["red"], st["rubber"])
        if lod <= 1:
            prims.cylinder(mb, (centre.x, side * (hg + m(gp["axleFrom"])), centre.z), (centre.x, side * (hg - m(gp["width"]) / 2), centre.z),
                           m(gp["axle"]) / 2, parts.sides(lod, 10, 6), st["rod"])
            a.parts[f"sheave_{name}"] = target
            a.pivots[f"sheave_{name}"] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}


def head_steel(mb, r, hg, lod, st):
    """A top head's steel: the square tube crossbeam across the line frame's end (its ends inboard of the ropes, so
    the grips pass), the drawn plates on it (spec heads.top.steel) and the lifting portal standing on it."""
    hd = r["heads"]["top"]
    cb = hd["crossbeam"]
    reach = hg + m(cb["out"])
    prims.box(mb, (m(cb["u"][0]), -reach, m(cb["bottom"])), (m(cb["u"][1]), reach, m(cb["top"])), st["steel"])
    stern_heads.members(mb, hd["steel"], hg, lod, st)
    stern_heads.lifting_portal(mb, hd["liftingPortal"], hg, lod, st)


def support_trains(mb, a, c, r, hg, lod, st):
    """The default top head's 4-sheave support trains (the FGQ-4 drive's, parts.sheave_train) on plates at the
    crossbeam's ends. Returns the u where the rope leaves over the outermost sheave."""
    rope, rope_r = m(c["ropeElevation"]), m(c["ropeDiameter"]) / 2
    hd = r["heads"]["top"]
    cb, tr = hd["crossbeam"], hd["trains"]
    st_ = c["sheaveTrain"]
    train = {"n": st_["n"], "pitch": m(st_["pitch"]), "rocker": m(st_["rocker"]), "beam": m(st_["beam"])}
    pl = tr["plate"]
    plate = {"v": (hg + m(pl["v"][0]), hg + m(pl["v"][1])), "top": m(cb["top"]), "width": m(pl["width"]), "pin": m(pl["pin"])}
    u_c = m(hd["pinU"])
    for side, name in ((-1, "l"), (1, "r")):
        parts.sheave_train(mb, a, name, u_c, side * hg, rope, rope_r, m(c["guideSheave"]["dia"]), m(c["guideSheave"]["width"]),
                           train, tr["mode"], None, lod, st, plate=plate, stops=tr.get("pinStops"))
    return u_c + (st_["n"] - 1) / 2 * m(st_["pitch"])


def variant(a, body, spec, lod, key, combo):
    """A top head with a bigger support row (6 or 8 sheaves; the line towers' kit assembly, heads.hung_assembly),
    levelled at its first sheave and pinned on two lug plates up to the crossbeam, with its own head steel: grafted on
    as head `key` of the top end (liftkit/swap.py), as the FGQ-4 drive's top_variant."""
    c, r = spec["common"], spec["return"]
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2
    st = styles(lod)
    hd = r["heads"]["top"]
    asm = parts.line_spec(spec)
    src, smb = Asset(key), MeshBuilder()
    head_steel(smb, r, hg, lod, st)
    pin_u = m(hd["pinU"])
    sockets = {}
    for side, name in ((-1, "l"), (1, "r")):
        u_first, (u_out, w_out), theta, _ = heads.hung_assembly(smb, src, name, side, hg, rope, pin_u, asm, combo["count"], "support",
                                                                asm["arcs"][combo["arc"]], lod, st, m(hd["faceW"]), hd["lugs"])
        sd = "left" if side < 0 else "right"
        sockets.update({f"rope_{sd}_hold": (u_first, side * hg, rope), f"rope_{sd}_out": (u_out, side * hg, w_out)})
    src.sockets = sockets
    swap.graft(a, body, src, smb, key, (0.0, 0.0, 0.0), (pin_u, 0.0, rope), end="top",
               dims={"sheaves": combo["count"], "arc": combo["arc"], "ropeExitDeg": -round(math.degrees(theta), 2)},
               sockets={"chair_unload": (m(r["chairUnloadU"]), -hg, rope)})


def build(spec, lod, stage):
    c, r = spec["common"], spec["return"]
    st = styles(lod)
    a = Asset(spec.get("idPrefix", "sessellift_stern4") + "_return")
    body = MeshBuilder()
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2

    pier_top = column(body, r, lod, st)
    stern_heads.members(body, r["steel"], hg, lod, st)
    deck(body, r, lod, st)
    guides(body, a, c, r, hg, lod, st)
    hd = r["heads"]["top"]
    with swap.head(a, body, "top", (m(hd["pinU"]), 0.0, rope), owns=("rope_left_out", "rope_right_out", "chair_unload"),
                   dims={"sheaves": c["sheaveTrain"]["n"]}):
        head_steel(body, r, hg, lod, st)
        u_out = support_trains(body, a, c, r, hg, lod, st)
    for key, combo in hd["variants"].items():
        variant(a, body, spec, lod, key, combo)

    centre = Vector((0.0, 0.0, rope))
    bw = stern_heads.return_wheel(c, r["bullwheel"])
    wheel = MeshBuilder() if lod <= 2 else body
    stern_heads.return_wheel_mesh(wheel, centre, bw, lod, st)
    if lod <= 2:
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}

    a.body = body
    a.sockets.update({   # the rope runs level from the wheel over the guide sheaves and the support trains
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_out": (u_out, -hg, rope), "rope_right_out": (u_out, hg, rope),
        "chair_unload": (m(r["chairUnloadU"]), -hg, rope),
        "pier_top": pier_top,
    })
    a.dims.update({"bullwheelU": 0, "columnU": round(sum(r["column"]["u"]) / 2), "deckTop": r["deck"]["top"],
                   "guideSheaveU": r["guideSheaves"]["u"], "headU": hd["pinU"]})
    return a
