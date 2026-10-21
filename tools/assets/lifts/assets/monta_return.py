"""Monta FG4 return terminal (top station), a recreation from the owner's reference model's dimensions
(monta_fg4.json "return", measured locally): the exposed bullwheel on the same carriage as the drive's, carried
by an H-section column with a tall head plate and a diagonal strut from a concrete pad, and a cross arm on the
strut whose end brackets carry the deflection sheaves where the arriving rope levels out.

Loaded chairs arrive on the left rope (v-) and unload before the wheel. Millimetres in the spec; the lift frame's
origin is on the bullwheel's axle at the station's grade, u toward the line.
"""
from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

import monta_drive
from monta_drive import box_from, cframes, gauge_shift, gauge_wheel, m, mv, pier_sockets, wheel

KIND = "terminal"
LODS = 4
SPEC = "monta_fg4.json"


def styles(lod):
    return monta_drive.styles(lod)


def build(spec, lod, stage):
    c, r = spec["common"], spec["return"]
    st = styles(lod)
    a = Asset("monta_fg4_return")
    body = MeshBuilder()
    rope = m(r["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    gs = gauge_shift(c)                 # the deflection sheaves, their brackets and the arm's ends move to the gauge
    sv = parts.metres(c["sheave"], keep=())

    # concrete foundations: under the strut, standing at grade, and under the column, below grade; really tall (owner,
    # P3-09 LT36: down to common.pierBottom, as the Sessellift's piers), the strut's first (pier_top), the column's next
    pier_tops = []
    for p in sorted(r["pads"], key=lambda p: -p["hi"][2]):
        lo, hi = p["lo"], p["hi"]
        prims.box(body, mv((lo[0], lo[1], c["pierBottom"])), mv(hi), st["concrete"], skip=("-2",))
        pier_tops.append(mv(((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, hi[2])))

    # the column: a flat I-bar from its base plate, splice plates near the top (the head plate is in "boxes")
    co = r["column"]
    prims.box(body, mv(co["shaft"]["lo"]), mv(co["shaft"]["hi"]), st["steel"])
    if lod <= 1:
        for sp in co["splice"]:
            prims.box(body, mv(sp["lo"]), mv(sp["hi"]), st["steel"])

    # the strut: a square member at 45 degrees, cut level on its base plate and against the head plate, under the
    # top plate that carries the carriage; the strut head (two gussets and a top plate) under the cross arm
    sr = r["strut"]
    hv = m(sr["halfV"])
    prims.prism(body, [(m(u), m(w)) for u, w in sr["profile"]], (0.0, -hv, 0.0), U, W, V, 2 * hv, st["steel"])
    prims.box(body, mv(sr["top"]["lo"]), mv(sr["top"]["hi"]), st["steel"])
    sh = r["strutHead"]
    for g in sh["gussets"]:
        v0, v1 = g["v"]
        prims.prism(body, [(m(u), m(w)) for u, w in g["profile"]], (0.0, m(v0), 0.0), U, W, V, m(v1 - v0), st["steel"])
    tw0, tw1 = sh["top"]["w"]
    prims.prism(body, [(m(u), m(v)) for u, v in sh["top"]["plan"]], (0.0, 0.0, m(tw0)), U, V, W, m(tw1 - tw0), st["steel"])

    # the cross arm (an I-beam across the line) on the strut, reaching the deflection sheaves' brackets
    ar = r["arm"]
    au, aw = m((ar["lo"][0] + ar["hi"][0]) / 2), m((ar["lo"][2] + ar["hi"][2]) / 2)
    reach = m(ar["reach"] + gs)
    ah, af = m(ar["hi"][2] - ar["lo"][2]), m(ar["hi"][0] - ar["lo"][0])
    if lod <= 1:
        prims.ibeam(body, (au, -reach, aw), (au, reach, aw), ah, af, 0.02, 0.014, st["steel"], up=W)
    else:
        prims.box(body, (au - af / 2, -reach, aw - ah / 2), (au + af / 2, reach, aw + ah / 2), st["steel"])

    # plates, blocks and brackets
    for b in r["boxes"]:
        if b["name"] in ("columnBasePlate", "strutBasePlate") and lod >= 2:
            continue
        dv = (-1 if b["c"][1] < 0 else 1) * gs if b["name"] == "armBracket" else 0.0
        box_from(body, b, st, shift=(0.0, dv, 0.0))
    if lod == 0:   # anchor bolts on the base plates
        for p in r["bolts"]:
            prims.cylinder(body, mv((p[0], p[1], p[2] - 30)), mv((p[0], p[1], p[2] + 40)), 0.012, 6, st["rod"])

    # the wheel carriage's C-frames and its hub
    car = r["carriage"]
    cframes(body, car["cframes"], st, lod)
    hb = car["hub"]
    prims.cylinder(body, (0.0, 0.0, m(hb["bottom"])), (0.0, 0.0, m(hb["top"])), m(hb["dia"]) / 2, parts.sides(lod, 20, 12, 8, 6), st["rod"])

    # the bullwheel on its axle at rope elevation
    centre = Vector((0.0, 0.0, rope))
    if lod <= 2:
        wb = MeshBuilder()
        wheel(wb, centre, gauge_wheel(c), hg, lod, st, m(c["ropeDiameter"]) / 2)
        a.parts["bullwheel"] = wb
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        wheel(body, centre, gauge_wheel(c), hg, lod, st, m(c["ropeDiameter"]) / 2)

    # deflection sheaves at the arm's ends (the rope rides on top; a single sheave is first and last: red)
    for dfl in r["deflection"]:
        cs = Vector(mv((dfl["u"], dfl["v"] + (-1 if dfl["v"] < 0 else 1) * gs, dfl["w"])))
        if lod <= 2:
            target = MeshBuilder() if lod <= 1 else body
            parts.line_sheave(target, cs, V, sv, min(lod, 2), st, parts.sides(lod, 14, 10, 6), st["red"])
            if lod <= 1:
                name = f"sheave_d{'l' if dfl['v'] < 0 else 'r'}"
                a.parts[name] = target
                a.pivots[name] = {"pos": tuple(cs), "axis": (0.0, 1.0, 0.0)}

    u_out = m(r["deflection"][0]["u"])
    a.body = body
    a.sockets = {   # the rope runs level from the wheel over the deflection sheaves toward the line
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_out": (u_out, -hg, rope), "rope_right_out": (u_out, hg, rope),
        "chair_unload": (m(r["unloadU"]), -hg, rope),
    }
    a.sockets.update(pier_sockets(pier_tops))
    a.dims.update({"rope": r["ropeElevation"], "deflectionU": r["deflection"][0]["u"]})
    return a
