"""Monta FG4 bottom return terminal (drive at either end, owner 2026-11-02): the return for a Monta lift driven from
the top. Built from the owner's photos of the type and the Monta's own kit (spec monta_fg4.json "returnBottom",
millimetres): one leaning box column on a concrete pad under a flat frame, the return's carriage and exposed bullwheel
on the frame, and a spine rising from the frame toward the line that carries the drive's front cross tube and, under
it, the drive's exit trees (three trains of four per rope on a carrier and a link, monta_drive.exit_trees), so the
rope runs level from the wheel through loading and leaves climbing under them as it does at the drive.

Loaded chairs leave on the right rope (v+); riders sit down at the drive's load point. The lift frame's origin is on
the bullwheel's axle at the station's grade, u toward the line.
"""
from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder
from liftkit.prims import U, V, W

import monta_drive
import monta_return
from monta_drive import box_from, gauge_shift, m, mv, pier_sockets

KIND = "terminal"
CHAIR = "chairworks_chair:fixed"   # the chair the build sends round the bullwheel (liftkit/fit.py fixed)
LODS = 4
SPEC = "monta_fg4.json"


def spine(mb, rb, st):
    """The spine: a box member of constant depth on the centreline, segment by segment along its path ((u, w) of its
    centre line), from inside the frame up over the cross tube."""
    sp = rb["spine"]
    hv, hd = m(sp["halfV"]), m(sp["depth"]) / 2
    path = [(m(u), m(w)) for u, w in sp["path"]]
    for (u0, w0), (u1, w1) in zip(path, path[1:]):
        prims.prism(mb, [(u0, w0 - hd), (u1, w1 - hd), (u1, w1 + hd), (u0, w0 + hd)], (0.0, -hv, 0.0), U, W, V, 2 * hv, st["steel"])


def build(spec, lod, stage):
    c, d, r, rb = spec["common"], spec["drive"], spec["return"], spec["returnBottom"]
    st = monta_return.styles(lod)
    a = Asset(spec.get("idPrefix", "monta_fg4") + "_return_bottom")
    body = MeshBuilder()
    rope = m(rb["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    gs = gauge_shift(c)
    sv = parts.metres(c["sheave"], keep=())
    sheave_sides = parts.sides(lod, 14, 10, 6, 4)

    # the pad (really tall, as the other Monta terminals'; the footing is the game's) and the column's base plate
    pd = rb["pad"]
    prims.box(body, mv((pd["lo"][0], pd["lo"][1], c["pierBottom"])), mv(pd["hi"]), st["concrete"], skip=("-2",))
    pier_tops = [mv(((pd["lo"][0] + pd["hi"][0]) / 2, (pd["lo"][1] + pd["hi"][1]) / 2, pd["hi"][2]))]
    if lod <= 2:
        prims.box(body, mv(rb["basePlate"]["lo"]), mv(rb["basePlate"]["hi"]), st["steel"])

    # the column: a box section leaning back from the pad under the frame
    co = rb["column"]
    hv = m(co["halfV"])
    prims.prism(body, [(m(u), m(w)) for u, w in co["profile"]], (0.0, -hv, 0.0), U, W, V, 2 * hv, st["steel"])

    # the frame under the carriage, and the spine on to the cross tube
    prims.box(body, mv(rb["frame"]["lo"]), mv(rb["frame"]["hi"]), st["steel"])
    spine(body, rb, st)
    tb = rb["tube"]
    reach = m(tb["reach"] + gs)
    prims.cylinder(body, (m(tb["u"]), -reach, m(tb["w"])), (m(tb["u"]), reach, m(tb["w"])), m(tb["dia"]) / 2,
                   parts.sides(lod, 16, 10, 8, 6), st["steel"], fan_caps=True)

    # the return's carriage plates, C-frames and hub, and its exposed bullwheel
    for b in r["boxes"]:
        if b["name"] in rb["carriageBoxes"] and lod <= 2:
            box_from(body, b, st)
    monta_return.carriage_wheel(a, body, c, r, lod, st, rope, hg)

    # the drive's exit trees, hung from the cross tube as at the drive
    u_hold, out, exit_deg = monta_drive.exit_trees(a, body, c, d, lod, st, hg, gs, sv, sheave_sides)

    a.body = body
    a.sockets = {   # the rope: level from the wheel through loading to the first exit sheave (hold), leaving climbing
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_hold": (u_hold, -hg, rope), "rope_right_hold": (u_hold, hg, rope),
        "rope_left_out": out["l"], "rope_right_out": out["r"],
        "chair_load": (m(rb["loadU"]), hg, rope),
    }
    a.sockets.update(pier_sockets(pier_tops))
    a.dims.update({"rope": rb["ropeElevation"], "ropeExitDeg": exit_deg,
                   "exitSheaves": sum(len(t) for t in d["exit"]["trains"]["left"]["sheaves"])})
    return a
