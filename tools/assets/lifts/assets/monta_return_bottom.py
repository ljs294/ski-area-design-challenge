"""Monta FG4 bottom return terminal (drive at either end, owner 2026-11-02): the return for a Monta lift driven from
the top. Built from the owner's photos of the type and the kits (spec monta_fg4.json "returnBottom", millimetres):
one leaning oval column on a concrete pad, a saddle on its top under a frame of two parallel round tubes (owner,
finding 5: not a block), the return's carriage and exposed bullwheel on the tubes, and toward the line a cross tube on
them carrying, on each rope, the kit 8-sheave depression assembly the Sessellift's bottom return uses
(parts.line_assembly on the station arc, levelled at its first sheave), its main pin on two lug plates rising from the
cross tube (owner, findings 1-2: as many hold-down sheaves as the Sessellift's bases, held from the frame below,
rounded edges). The rope runs level from the wheel through loading and leaves climbing.

Loaded chairs leave on the right rope (v+); riders sit down at the drive's load point. The lift frame's origin is on
the bullwheel's axle at the station's grade, u toward the line.
"""
import math

from liftkit import heads, parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder
from liftkit.prims import U, V, W

import monta_return
from monta_drive import box_from, m, mv, oval_leg, pier_sockets

KIND = "terminal"
CHAIR = "chairworks_chair:fixed"   # the chair the build sends round the bullwheel (liftkit/fit.py fixed)
LODS = 4
SPEC = "monta_fg4.json"


def rounded(a0, b0, a1, b1, r, steps):
    """A rectangle (a0, b0)-(a1, b1) with its corners rounded to radius r in `steps` segments each (square at 0)."""
    if steps == 0 or r <= 0:
        return [(a0, b0), (a1, b0), (a1, b1), (a0, b1)]
    pts = []
    for (ca, cb), start in (((a1 - r, b0 + r), -90), ((a1 - r, b1 - r), 0), ((a0 + r, b1 - r), 90), ((a0 + r, b0 + r), 180)):
        for k in range(steps + 1):
            t = math.radians(start + 90 * k / steps)
            pts.append((ca + r * math.cos(t), cb + r * math.sin(t)))
    return pts


def build(spec, lod, stage):
    c, r, rb = spec["common"], spec["return"], spec["returnBottom"]
    st = monta_return.styles(lod)
    a = Asset(spec.get("idPrefix", "monta_fg4") + "_return_bottom")
    body = MeshBuilder()
    rope = m(rb["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    corner = {0: 4, 1: 2}.get(lod, 0)   # rounded edges up close, square far out
    smooth = st["steel"].but(smooth=True)

    # the pad (really tall, as the other Monta terminals'; the footing is the game's) and the column's base plate
    pd = rb["pad"]
    prims.box(body, mv((pd["lo"][0], pd["lo"][1], c["pierBottom"])), mv(pd["hi"]), st["concrete"], skip=("-2",))
    pier_tops = [mv(((pd["lo"][0] + pd["hi"][0]) / 2, (pd["lo"][1] + pd["hi"][1]) / 2, pd["hi"][2]))]
    if lod <= 2:
        prims.box(body, mv(rb["basePlate"]["lo"]), mv(rb["basePlate"]["hi"]), st["steel"])

    # the column: an oval tube leaning back from the pad under the frame, cut level at both ends (the Monta's legs)
    co = rb["column"]
    (bu, bw), (tu, tw) = co["base"], co["top"]
    ax = ((tu - bu), 0.0, (tw - bw))
    n = math.hypot(ax[0], ax[2])
    ax = (ax[0] / n, 0.0, ax[2] / n)
    oval_leg(body, {"centre": [(bu + tu) / 2, 0.0, (bw + tw) / 2], "axis": list(ax), "e1": [0.0, 1.0, 0.0],
                    "e2": [-ax[2], 0.0, ax[0]], "a1": co["a1"], "a2": co["a2"], "bottomW": bw, "topW": tw},
             parts.sides(lod, 24, 14, 10, 6), smooth)

    # each rope's kit depression assembly, its first sheave where the level rope meets the row, its lug plates up from
    # the cross tube's centre (they reach into it, as the drive's hang from its cross tube)
    hd, tb, sd = rb["holdDown"], rb["tubes"], rb["saddle"]
    asm = parts.line_spec(spec)
    ct = hd["crossTube"]
    t_r, t_v = m(tb["dia"]) / 2, m(tb["v"])
    t_w = m(tb["top"]) - t_r                     # the frame tubes' centreline
    c_r = m(ct["dia"]) / 2
    c_w = m(tb["top"]) + c_r                     # the cross tube lies on them, clear of the chairs' hangers below
    for side, name in ((-1, "l"), (1, "r")):
        u_hold, (u_out, w_out), theta, (pin_u, _) = heads.hung_assembly(
            body, a, name, side, hg, rope, m(rb["firstU"]), asm, hd["count"], "hold", asm["arcs"][hd["arc"]], lod, st,
            c_w, hd["lugs"], both=True, at_first=True)

    # the frame: two round tubes along the line under the carriage and on past the cross tube; the cross tube across
    # them under the lug plates; a rounded saddle under both on the column's top
    ntube = parts.sides(lod, 20, 12, 8, 6)
    u_end = pin_u + c_r + m(tb["past"])
    for s in (-1, 1):
        prims.cylinder(body, (m(tb["from"]), s * t_v, t_w), (u_end, s * t_v, t_w), t_r, ntube, smooth)
    reach = hg - asm["x"]["beam"][0] + m(hd["lugs"]["thick"]) + m(ct["past"])   # past the outer lug plates
    prims.cylinder(body, (pin_u, -reach, c_w), (pin_u, reach, c_w), c_r, parts.sides(lod, 16, 10, 8, 6), smooth)
    su, sv = m(sd["halfU"]), t_v + t_r + m(sd["past"])
    prims.prism(body, rounded(-sv, m(co["top"][1]), sv, t_w, m(sd["corner"]), corner),
                (m(co["top"][0]) - su, 0.0, 0.0), V, W, U, 2 * su, st["steel"])

    # the return's carriage plates, C-frames and hub, and its exposed bullwheel
    for b in r["boxes"]:
        if b["name"] in rb["carriageBoxes"] and lod <= 2:
            box_from(body, b, st)
    monta_return.carriage_wheel(a, body, c, r, lod, st, rope, hg)

    a.body = body
    a.sockets = {   # the rope: level from the wheel through loading to the first sheave (hold), leaving climbing (out)
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_hold": (u_hold, -hg, rope), "rope_right_hold": (u_hold, hg, rope),
        "rope_left_out": (u_out, -hg, w_out), "rope_right_out": (u_out, hg, w_out),
        "chair_load": (m(rb["loadU"]), hg, rope),
    }
    a.sockets.update(pier_sockets(pier_tops))
    a.dims.update({"rope": rb["ropeElevation"], "ropeExitDeg": round(math.degrees(theta), 2), "sheaves": hd["count"], "arc": hd["arc"],
                   "frameEnd": round(u_end * 1000)})
    return a
