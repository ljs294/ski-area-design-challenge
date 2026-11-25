"""A fixed-grip chair's stop gate at its top return terminal, from its spec (task 4.50 phase 4; stopgate.json).

A rider who has not got off by the time the chair turns round the bullwheel must stop the lift. The owner's rule
(2026-11-24, after the stop-gate photo survey): copy the real ones. A typical top terminal layout and a lift maker's
return terminal drawing agree with the one clear photo: a slim post standing on the ramp past the unload, on the lift's
centreline under the wheel, and a thin rigid bar (1 in square) from its top along the lift line, out under the chair
as it turns round the far side of the wheel, below the seat, about 3 m long; a proximity switch at the bar's pivot stops
the lift when the bar is struck. An empty chair passes over the bar; a rider still seated, whose legs and skis hang
below the seat, strikes it. The post stands inside the circle the chairs' inner ends sweep, so no chair passes over it.

Its own model, not part of the ramp, because where it stands depends on the lift's terminal (its bullwheel's place):
one per terminal style in unloadramp.json's table (`variants`: sessellift, stern, monta), each for every chair width.
unload_ramp.gate_at() gives its place for a ramp and a terminal (the post at the inner end of the window where the
chair crosses the line); the post runs down to the ramp's surface there, for the deepest of the three chair widths
(the others bury its foot a few centimetres into the boards), so pick() reads unloadramp.json for it.

Frame (millimetres in the spec, metres here): the origin is on the post's axis at the unload level (the deck's top);
the bar points +u (away from the wheel's column, toward the ski-off) at `bar.height`; w is up. LOD0 the post, its
base plate, the bar, its brace and the switch; LOD1 the post, the bar and the switch; LOD2 the post and the bar;
LOD3 the bar. Sockets: `pivot` (the bar's root on the post) and `bar_end` (its tip).
"""
import json
import os

from liftkit import prims
from liftkit.export import Asset
from liftkit.frame import mm
from liftkit.mesh import MeshBuilder, Style

LODS = 4
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def styles():
    return {"post": Style("white", snow=0.2), "bar": Style("paint_dark", snow=0.0),
            "switch": Style("paint_grey", snow=0.2), "plate": Style("galvanised", snow=0.3)}


def _copy(x):
    return json.loads(json.dumps(x))


def pick(spec, terminal, ramps=None):
    """The terminal's stop gate: `common` overlaid with its variant, the post's depth (`post.depth`, mm under the
    unload level) set to the ramp's surface under it for the deepest chair width, and the review's ground there."""
    from bldgkit import unload_ramp                  # here, so the ramp kit need not import this one
    out = _copy(spec["common"])
    out.update(_copy(spec["variants"][terminal]))
    ramps = ramps or json.load(open(os.path.join(HERE, "unloadramp.json"), encoding="utf-8"))
    depths, reach = [], []
    for width in ramps["variants"]:
        rs = unload_ramp.pick(ramps, width)
        if terminal not in unload_ramp.terminals(rs):   # only the ramps that serve this terminal (its set)
            continue
        g = unload_ramp.gate_at(rs, terminal)
        depths.append(-g["foot"])
        reach.append(g["window"][1] - g["at"][0])
    out["post"]["depth"] = round(max(depths) * 1000)
    out["_fit"] = {"bury": round((max(depths) - min(depths)) * 1000), "reach": round(max(reach) * 1000),
                   "height": round(g["height"] * 1000)}
    out.setdefault("review", {})["ground"] = -max(depths)
    return out


def build(spec, lod, asset_id=None):
    st = styles()
    a = Asset(asset_id or spec["id"])
    mb = MeshBuilder()
    a.lights, a.glow, a.problems = [], {}, []
    po, br = spec["post"], spec["bar"]
    s, above, depth = mm(po["size"]), mm(po["above"]), mm(po["depth"])
    pw, pt = (mm(x) for x in po["plate"])
    h, b, L = mm(br["height"]), mm(br["size"]), mm(br["length"])
    sw, sd, sh = (mm(x) for x in spec["switch"])     # across, along, tall
    top = h + above
    if lod <= 2:
        prims.box(mb, (-s / 2, -s / 2, -depth), (s / 2, s / 2, top), st["post"], skip=("-2",))
    prims.box(mb, (s / 2, -b / 2, h - b / 2), (s / 2 + L, b / 2, h + b / 2), st["bar"], skip=("-0",))
    if lod <= 1:                                     # the proximity switch at the pivot, on the post's top
        prims.box(mb, (-sd / 2, -sw / 2, top), (sd / 2, sw / 2, top + sh), st["switch"], skip=("-2",))
    if lod == 0:
        prims.box(mb, (-pw / 2, -pw / 2, -depth), (pw / 2, pw / 2, -depth + pt), st["plate"], skip=("-2",))
        bc = spec["brace"]
        prims.beam(mb, (s / 2, 0.0, top - mm(bc["drop"])), (s / 2 + mm(bc["reach"]), 0.0, h + b / 2),
                   mm(bc["size"]), mm(bc["size"]), st["post"], up=(0.0, 1.0, 0.0), skip_ends=True)
    fit = spec.get("_fit", {})
    if fit:
        if fit["bury"] > po["buryMax"]:
            a.problems.append(f"the post's foot is {fit['bury']} mm into the boards for one chair width, over "
                              f"{po['buryMax']} mm")
        if fit["reach"] > round((s / 2 + L) * 1000) + 1:
            a.problems.append(f"the bar ends {fit['reach'] - round((s / 2 + L) * 1000)} mm short of the window's far end")
        if abs(fit["height"] - br["height"]) > 1:
            a.problems.append(f"the bar is {br['height']} mm up, the ramp wants {fit['height']} mm")
    a.body = mb
    a.dims.update({"barHeight": br["height"], "barLength": br["length"], "postDepth": po["depth"],
                   "postAbove": round(top * 1000), "lights": 0})
    a.sockets["pivot"] = (s / 2, 0.0, h)
    a.sockets["bar_end"] = (s / 2 + L, 0.0, h)
    return a
