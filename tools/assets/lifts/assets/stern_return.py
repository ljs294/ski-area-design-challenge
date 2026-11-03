"""Sessellift Stern return terminal (top station; sessellift_stern4.json "return"), from the owner's layout (Gate 0):

  a steel column on its pier, a boom from it back to the hub under the exposed bullwheel, a railed deck on the
  column over the wheel's inner half with a ladder up to it, guide sheaves on a cross member through the boom, and
  the line frame ahead of the column: a truss out to a crossbeam with the 4-sheave support trains on
  plates at its ends and a lifting portal over them.

Loaded chairs arrive on the left rope (v-) and unload over the column (chairUnloadU); the bullwheel turns
counter-clockwise seen from above. Bottom drive only (owner, 2026-11-02): the support rows are the terminal's one
head, the top (liftkit/swap.py), with the 6 and 8 sheave rows as its variants (as the other fixed grips' top heads);
it gains no bottom head. Dimensions: millimetres; the origin on the bullwheel axle at the loading level.
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


def column(mb, r, lod, st):
    """The column on its base plate and pier, its upper section up to the deck, and the boom back to the hub.
    Returns the pier top."""
    co, pr = r["column"], r["pier"]
    hv = m(co["v"]) / 2
    u0, u1 = co["u"]
    foot = co["bottom"] - co["basePlate"]
    top = stern_heads.pier(mb, (u0, -co["v"] / 2), (u1, co["v"] / 2), foot, pr["bottom"], pr["margin"], st)
    if lod <= 2:
        prims.box(mb, (m(u0 - 80), -hv - 0.08, m(foot)), (m(u1 + 80), hv + 0.08, m(co["bottom"])), st["steel"])
    prims.box(mb, (m(u0), -hv, m(co["bottom"])), (m(u1), hv, m(co["top"])), st["steel"], skip=("+2",))
    up = co["upper"]
    prims.box(mb, (m(up["u"][0]), -hv, m(co["top"])), (m(up["u"][1]), hv, m(up["top"])), st["steel"])
    bm = r["boom"]   # the boom: level top, its underside rising from the column to the hub
    b0, b1 = bm["bottom"]
    prof = [(m(bm["u"][0]), m(b0)), (m(bm["u"][1]), m(b1)), (m(bm["u"][1]), m(bm["top"])), (m(bm["u"][0]), m(bm["top"]))]
    prims.prism(mb, prof, (0, -m(bm["v"]) / 2, 0), U, W, V, m(bm["v"]), st["steel"])
    hb = r["hub"]
    prims.cylinder(mb, (0.0, 0.0, m(hb["bottom"])), (0.0, 0.0, m(hb["top"])), m(hb["dia"]) / 2, parts.sides(lod, 20, 12, 8, 6), st["steel"],
                   fan_caps=True)
    return top


def deck(mb, r, lod, st):
    """The deck on the column over the wheel's inner half, railed round, open at the line end where the ladder
    comes up past its end; the ladder from the snow, beside the line frame."""
    dk = r["deck"]
    u0, u1, hv, top = m(dk["u"][0]), m(dk["u"][1]), m(dk["v"]), m(dk["top"])
    parts.deck(mb, (u0, -hv, m(dk["bottom"])), (u1, hv, top), lod, st["galv"])
    rail = m(dk["rail"])
    e = 0.03
    parts.railing(mb, [(u1 - e, -hv + e, top), (u0 + e, -hv + e, top), (u0 + e, hv - e, top), (u1 - e, hv - e, top)], rail, lod, st["galv"])
    la = r["ladder"]
    lu, lv = m(la["u"]), m(la["v"])
    parts.ladder(mb, (lu + m(la["lean"]), lv, 0.0), (lu, lv, top), 2 * m(la["halfWidth"]), lod, st["galv"], rung_every=m(la["rungs"]),
                 handrails=False)   # between the line frame and the leaving chairs: no room for side rails


def guides(mb, a, c, r, hg, lod, st):
    """The guide sheaves (rope on top) on brackets inboard of them, on a cross member through the boom."""
    rope, rope_r = m(c["ropeElevation"]), m(c["ropeDiameter"]) / 2
    gs, gp = c["guideSheave"], r["guideSheaves"]
    cr = gp["cross"]
    reach = hg + m(cr["in"])
    prims.box(mb, (m(cr["u"][0]), -reach, m(cr["bottom"])), (m(cr["u"][1]), reach, m(cr["top"])), st["steel"])
    for side, name in ((-1, "l1"), (1, "r1")):
        centre = Vector((m(gp["u"]), side * hg, rope - rope_r - m(gs["dia"]) / 2))
        target = MeshBuilder() if lod <= 1 else mb
        if lod <= 2:
            parts.sheave(target, centre, V, m(gs["dia"]), m(gs["width"]), lod, st["red"], st["rubber"])
            parts.sheave_bracket(mb, centre, m(gs["width"]), m(cr["top"]), lod, st["steel"])
        if lod <= 1:
            a.parts[f"sheave_{name}"] = target
            a.pivots[f"sheave_{name}"] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}


def line_frame(mb, r, lod, st):
    """The line frame, a truss on the axis: brackets on the column's line face, an upper member and a lower strut
    from them out to a post under the head's crossbeam."""
    lf = r["lineFrame"]
    up, sr, po, br = lf["upper"], lf["strut"], lf["post"], lf["brackets"]
    prims.beam(mb, (m(up["from"][0]), 0.0, m(up["from"][1])), (m(up["to"][0]), 0.0, m(up["to"][1])), m(up["v"]), m(up["depth"]), st["steel"])
    prims.box(mb, (m(po["u"][0]), -m(po["v"]) / 2, m(po["bottom"])), (m(po["u"][1]), m(po["v"]) / 2, m(r["heads"]["top"]["crossbeam"]["bottom"])),
              st["steel"])
    if lod <= 2:
        prims.beam(mb, (m(sr["from"][0]), 0.0, m(sr["from"][1])), (m(sr["to"][0]), 0.0, m(sr["to"][1])), m(sr["v"]), m(sr["depth"]), st["steel"])
        prims.box(mb, (m(br["u"][0]), -m(br["v"]) / 2, m(br["bottom"])), (m(br["u"][1]), m(br["v"]) / 2, m(br["top"])), st["steel"])


def head_steel(mb, r, hg, lod, st):
    """A top head's steel: the crossbeam (inboard of the ropes, so the grips pass) and the lifting portal on it."""
    hd = r["heads"]["top"]
    cb = hd["crossbeam"]
    reach = hg + m(cb["in"])
    prims.box(mb, (m(cb["u"][0]), -reach, m(cb["bottom"])), (m(cb["u"][1]), reach, m(cb["top"])), st["steel"])
    stern_heads.lifting_portal(mb, hd["liftingPortal"], hg, lod, st, cb["top"])


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
    u_c = m(sum(cb["u"]) / 2)
    for side, name in ((-1, "l"), (1, "r")):
        parts.sheave_train(mb, a, name, u_c, side * hg, rope, rope_r, m(c["guideSheave"]["dia"]), m(c["guideSheave"]["width"]),
                           train, tr["mode"], None, lod, st, plate=plate, stops=tr.get("pinStops"))
    return u_c + (st_["n"] - 1) / 2 * m(st_["pitch"])


def variant(a, body, spec, lod, key, combo):
    """A top head with a bigger support row (6 or 8 sheaves; the line towers' kit assembly, heads.hung_assembly),
    levelled at its first sheave and pinned under the crossbeam end on two lug plates, its own crossbeam and portal:
    grafted on as head `key` of the top end (liftkit/swap.py), as the FGQ-4 drive's top_variant."""
    c, r = spec["common"], spec["return"]
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2
    st = styles(lod)
    hd = r["heads"]["top"]
    cb = hd["crossbeam"]
    asm = parts.line_spec(spec)
    src, smb = Asset(key), MeshBuilder()
    head_steel(smb, r, hg, lod, st)
    pin_u = m(sum(cb["u"]) / 2)
    sockets = {}
    for side, name in ((-1, "l"), (1, "r")):
        u_first, (u_out, w_out), theta, _ = heads.hung_assembly(smb, src, name, side, hg, rope, pin_u, asm, combo["count"], "support",
                                                                asm["arcs"][combo["arc"]], lod, st, m(cb["bottom"]), hd["lugs"])
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
    deck(body, r, lod, st)
    guides(body, a, c, r, hg, lod, st)
    line_frame(body, r, lod, st)
    cb = r["heads"]["top"]["crossbeam"]
    with swap.head(a, body, "top", (m(sum(cb["u"]) / 2), 0.0, rope), owns=("rope_left_out", "rope_right_out", "chair_unload"),
                   dims={"sheaves": c["sheaveTrain"]["n"]}):
        head_steel(body, r, hg, lod, st)
        u_out = support_trains(body, a, c, r, hg, lod, st)
    for key, combo in r["heads"]["top"]["variants"].items():
        variant(a, body, spec, lod, key, combo)

    centre = Vector((0.0, 0.0, rope))
    bw = stern_heads.return_wheel(c, r["bullwheel"])
    if lod <= 2:
        wheel = MeshBuilder()
        return_terminal.bullwheel(wheel, centre, c["lineGauge"], bw, lod, st)
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        return_terminal.bullwheel(body, centre, c["lineGauge"], bw, lod, st)

    a.body = body
    a.sockets.update({   # the rope runs level from the wheel over the guide sheaves and the support trains
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_out": (u_out, -hg, rope), "rope_right_out": (u_out, hg, rope),
        "chair_unload": (m(r["chairUnloadU"]), -hg, rope),
        "pier_top": pier_top,
    })
    a.dims.update({"bullwheelU": 0, "columnU": round(sum(r["column"]["u"]) / 2), "deckTop": r["deck"]["top"],
                   "guideSheaveU": r["guideSheaves"]["u"], "headU": round(sum(cb["u"]) / 2)})
    return a
