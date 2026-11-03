"""Monta T-bar terminals (monta_tbar.json "drive", "return", "guide"; surface lifts, owner 2026-11-02, from public
research and the kit, no reference drawing).

  drive   the top station: the horizontal bullwheel on its gearbox, the motor beside it, on a steel deck carried by a
          tube column and a rear strut, with a ladder up the column; an electrical cabinet at grade; the stop gate (a
          swing arm at waist height from a post outboard of the arriving track, before the wheel).
  return  the bottom station with tensioning: the bullwheel on a carriage that slides along u on two rails, held back
          by a cable over a sheave to a concrete counterweight hanging in a tube frame behind the wheel; the rails sit
          on a portal of two tube columns.

Both carry the guide ring round the back of the wheel at the hanging bars' middle height, outboard of them. The
uphill (loaded) rope is on the right going up: it leaves the return on v+ and arrives at the drive on v-.
The lift frame's origin is on the bullwheel's axle at grade; u toward the line. Piers and pads run down to
"pierBottom" (15 m), as on the chairlift terminals, so a raised terminal still stands on them; pier_top sockets.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "surface_terminal"
LODS = 4
SPEC = "monta_tbar.json"


def m(x):
    return x / 1000.0


def styles():
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("galvanised", snow=0.5),
        "paint": Style("paint_grey", snow=0.5),
        "dark": Style("paint_dark", snow=0.6),
        "rod": Style("machined"),
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "cabinet": Style("paint_grey", snow=0.8),
        "yellow": Style("safety_yellow", snow=0.3),
        "black": Style("sign_black", snow=0.3),
    }


def wheel_spec(c):
    bw = c["bullwheel"]
    spec = {k: v / 1000.0 for k, v in bw.items() if k != "spokes"}
    spec["spokes"] = bw["spokes"]
    return spec


def pier(mb, u, v, half, top, bottom, st, lod):
    """A square concrete pier from `bottom` (far below grade) to `top`, the part below grade left open underneath."""
    prims.box(mb, (u - half, v - half, bottom), (u + half, v + half, top), st["concrete"], skip=("-2",))
    return (u, v, top)


def tube(mb, p0, p1, dia, lod, st, near=12):
    prims.cylinder(mb, p0, p1, dia / 2, parts.sides(lod, near, 8, 6, 4), st, caps=(False, True))


def guide(mb, g, hg, side_sign, lod, st):
    """The guide ring: a tube on a half circle behind the wheel (-u), radius g.radius, and straight runs from its ends
    toward the line; posts down to grade."""
    if lod >= 3:
        return
    R, w = m(g["radius"]), m(g["w"])
    steps = {0: 16, 1: 10, 2: 6}[lod]
    pts = [Vector((m(g["straightU"]), R, w))]
    for k in range(steps + 1):
        a = math.pi / 2 + math.pi * k / steps                 # from +v round the back (-u) to -v
        pts.append(Vector((R * math.cos(a), R * math.sin(a), w)))
    pts.append(Vector((m(g["straightU"]), -R, w)))
    prims.tube_path(mb, pts, m(g["dia"]) / 2, parts.sides(lod, 8, 6, 4), st["steel"], caps=True)
    if lod <= 1:
        n = g["posts"]
        for k in range(n):
            a = math.pi / 2 + math.pi * k / (n - 1)
            p = Vector((R * math.cos(a), R * math.sin(a), 0.0))
            tube(mb, p, p + W * w, m(g["postDia"]), lod, st["steel"], near=8)


def bullwheel(a, body, c, rope, lod, st):
    centre = Vector((0.0, 0.0, rope))
    if lod <= 2:
        wb = MeshBuilder()
        parts.bullwheel(wb, centre, wheel_spec(c), lod, st["dark"], plate_snow=0.7, fan_caps=True)
        a.parts["bullwheel"] = wb
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        parts.bullwheel(body, centre, wheel_spec(c), lod, st["dark"], plate_snow=0.7, fan_caps=True)


def rope_sockets(a, hg, rope, sk, load):
    a.sockets.update({
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_out": (0.0, -hg, rope), "rope_right_out": (0.0, hg, rope),
    })
    if load:   # riders take a bar under the leaving loaded rope (v+)
        a.sockets["carrier_load"] = (m(sk["loadU"]), hg, 0.0)
    else:      # and let go of it before the wheel under the arriving one (v-)
        a.sockets["carrier_unload"] = (m(sk["unloadU"]), -hg, 0.0)


def build(spec, lod, stage, variant):
    return drive(spec, lod) if variant == "drive" else ret(spec, lod)


# -- the top station -----------------------------------------------------------------------------------------------
def drive(spec, lod):
    c, d = spec["common"], spec["drive"]
    st = styles()
    a = Asset("monta_tbar_drive")
    body = MeshBuilder()
    rope = m(c["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    bottom = m(d["pierBottom"])
    pad, co, sr, dk = d["pad"], d["column"], d["strut"], d["deck"]

    piers = [pier(body, 0.0, 0.0, m(pad["half"]), m(pad["top"]), bottom, st, lod)]
    foot_u = m(sr["footU"])
    piers.append(pier(body, foot_u, 0.0, m(pad["half"]) * 0.6, m(pad["top"]), bottom, st, lod))

    # the column and the rear strut up to the deck's underside
    d_lo, d_hi = m(dk["top"]) - m(dk["thick"]), m(dk["top"])
    tube(body, (0, 0, m(pad["top"])), (0, 0, d_lo), m(co["dia"]), lod, st["steel"], near=16)
    if lod <= 2:
        tube(body, (foot_u, 0, m(pad["top"])), (m(dk["uFrom"]) + 0.15, 0, d_lo), m(sr["dia"]), lod, st["steel"])
    if lod <= 1:   # base plates
        for u, h in ((0.0, m(co["dia"]) * 0.75), (foot_u, m(sr["dia"]) * 0.9)):
            prims.box(body, (u - h, -h, m(pad["top"])), (u + h, h, m(pad["top"]) + 0.03), st["steel"])
    # the deck: a box girder plate, open to the wheel above
    hv = m(dk["halfV"])
    prims.box(body, (m(dk["uFrom"]), -hv, d_lo), (m(dk["uTo"]), hv, d_hi), st["paint"])

    # the gearbox under the wheel, the motor beside it on the deck, coupled by a short shaft
    gb = d["gearbox"]
    prims.box(body, tuple(m(x) for x in gb["lo"]), tuple(m(x) for x in gb["hi"]), st["dark"])
    mo = d["motor"]
    mu, mw, mr, ml = m(mo["u"]), m(mo["w"]), m(mo["dia"]) / 2, m(mo["length"])
    if lod <= 2:
        prims.cylinder(body, (mu - ml / 2, m(mo["v"]), mw), (mu + ml / 2, m(mo["v"]), mw), mr, parts.sides(lod, 16, 10, 6), st["dark"])
    if lod <= 1:
        prims.box(body, (mu - ml / 2, -mr * 0.8, d_hi), (mu + ml / 2, mr * 0.8, mw - mr * 0.5), st["dark"])   # its feet
        prims.cylinder(body, (mu + ml / 2, 0, mw), (m(gb["lo"][0]), 0, mw), 0.05, 8, st["rod"])           # the shaft
    if lod == 0:   # cooling fins round the motor
        for k in range(6):
            uu = mu - ml / 2 + ml * (k + 0.5) / 6
            prims.cylinder(body, (uu - 0.01, 0, mw), (uu + 0.01, 0, mw), mr + 0.02, 12, st["dark"])

    # the ladder up the column's -v side to the deck
    if lod <= 1:
        side = d["ladderSide"]
        parts.ladder(body, (0.0, side * (m(co["dia"]) / 2 + 0.2), m(pad["top"])), (0.0, side * (m(co["dia"]) / 2 + 0.2), d_hi),
                     0.45, lod, st["steel"], handrails=False)

    # the electrical cabinet at grade, on its own plinth
    cb = d["cabinet"]
    lo, hi = [m(x) for x in cb["lo"]], [m(x) for x in cb["hi"]]
    prims.box(body, (lo[0] - 0.05, lo[1] - 0.05, -0.2), (hi[0] + 0.05, hi[1] + 0.05, 0.1), st["concrete"], skip=("-2",))
    prims.box(body, (lo[0], lo[1], 0.1), (hi[0], hi[1], hi[2]), st["cabinet"])

    # the stop gate: a post outboard of the arriving (v-) track, a yellow swing arm toward the rope's line
    sg = d["stopGate"]
    if lod <= 2:
        gu, pv, ph = m(sg["u"]), -m(sg["postV"]), m(sg["postHeight"])
        tube(body, (gu, pv, -0.3), (gu, pv, ph), m(sg["postDia"]), lod, st["steel"], near=10)
        prims.cylinder(body, (gu, pv, m(sg["armW"])), (gu, -m(sg["armToV"]), m(sg["armW"])), m(sg["armDia"]) / 2,
                       parts.sides(lod, 8, 6, 4), st["yellow"])
        if lod == 0:   # the switch box on the post
            prims.box(body, (gu - 0.08, pv - 0.12, m(sg["armW"]) + 0.1), (gu + 0.08, pv + 0.04, m(sg["armW"]) + 0.35), st["black"])

    guide(body, spec["guide"], hg, -1, lod, st)
    bullwheel(a, body, c, rope, lod, st)

    a.body = body
    rope_sockets(a, hg, rope, spec["sockets"], load=False)
    for i, p in enumerate(piers):
        a.sockets["pier_top" if i == 0 else f"pier_top_{i + 1}"] = p
    a.dims.update({"rope": c["ropeElevation"], "deckTop": dk["top"], "lineGauge": c["lineGauge"]})
    return a


# -- the bottom station with tensioning ----------------------------------------------------------------------------
def ret(spec, lod):
    c, r = spec["common"], spec["return"]
    st = styles()
    a = Asset("monta_tbar_return")
    body = MeshBuilder()
    rope = m(c["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    bottom = m(r["pierBottom"])
    po, ra, ca = r["portal"], r["rails"], r["carriage"]
    wf, wt, cs = r["weightFrame"], r["weight"], r["cableSheave"]
    ph, pt = m(r["padHalf"]), m(r["padTop"])

    # the portal: two tube columns either side of the rails and a crossbeam under them
    pu, pv, top = m(po["columnU"]), m(po["halfV"]), m(po["top"])
    piers = []
    for s in (-1, 1):
        piers.append(pier(body, pu, s * pv, ph, pt, bottom, st, lod))
        tube(body, (pu, s * pv, pt), (pu, s * pv, top - 0.25), m(po["dia"]), lod, st["steel"], near=12)
    prims.box(body, (pu - 0.15, -pv - 0.15, top - 0.25), (pu + 0.15, pv + 0.15, top), st["steel"])

    # the rails along u on the crossbeam and on the counterweight frame
    rv, rd, rw = m(ra["halfV"]), m(ra["depth"]), m(ra["width"])
    u0, u1 = m(ra["uFrom"]), m(ra["uTo"])
    for s in (-1, 1):
        if lod <= 1:
            prims.ibeam(body, (u0, s * rv, top + rd / 2), (u1, s * rv, top + rd / 2), rd, rw, 0.016, 0.01, st["steel"], up=W)
        else:
            prims.box(body, (u0, s * rv - rw / 2, top), (u1, s * rv + rw / 2, top + rd), st["steel"])
    rail_top = top + rd

    # the carriage on the rails, carrying the wheel's axle
    hu, hv, ct = m(ca["halfU"]), m(ca["halfV"]), m(ca["thick"])
    prims.box(body, (-hu, -hv, rail_top), (hu, hv, rail_top + ct), st["paint"])
    if lod <= 1:   # its running wheels on the rails
        for su in (-1, 1):
            for sv in (-1, 1):
                p = Vector((su * (hu - 0.15), sv * (rv + rw / 2 + 0.03), rail_top + 0.02))
                prims.cylinder(body, p - V * 0.025, p + V * 0.025, 0.09, parts.sides(lod, 12, 8), st["dark"])

    # the counterweight frame: four tube posts round the hanging weight, tied at the top, the cable sheave on its front
    fu, fh, fd = m(wf["u"]), m(wf["half"]), m(wf["dia"])
    sheave_u, sr = m(cs["u"]), m(cs["dia"]) / 2
    cable_w = rail_top + ct / 2                         # the cable leaves the carriage's back level
    sheave_w = cable_w - sr                             # and runs over the sheave's top
    frame_top = cable_w + 0.25
    piers.append(pier(body, fu, 0.0, fh + 0.25, pt, bottom, st, lod))
    for su in (-1, 1):
        for sv in (-1, 1):
            tube(body, (fu + su * fh, sv * fh, pt), (fu + su * fh, sv * fh, frame_top), fd, lod, st["steel"], near=10)
    if lod <= 2:
        for k, (p0, p1) in enumerate((((fu - fh, -fh), (fu + fh, -fh)), ((fu - fh, fh), (fu + fh, fh)),
                                      ((fu - fh, -fh), (fu - fh, fh)), ((fu + fh, -fh), (fu + fh, fh)))):
            prims.box(body, (min(p0[0], p1[0]) - 0.06, min(p0[1], p1[1]) - 0.06, frame_top - 0.12),
                      (max(p0[0], p1[0]) + 0.06, max(p0[1], p1[1]) + 0.06, frame_top), st["steel"])
    # the sheave on a bracket at the frame's front, its axle across the line
    sc = Vector((sheave_u, 0.0, sheave_w))
    if lod <= 2:
        sb = MeshBuilder() if lod <= 1 else body
        parts.sheave(sb, sc, V, 2 * sr, m(cs["width"]), lod, st["red"], st["rubber"])
        if lod <= 1:
            a.parts["sheave_t"] = sb
            a.pivots["sheave_t"] = {"pos": tuple(sc), "axis": (0.0, 1.0, 0.0)}
        for s in (-1, 1):
            prims.box(body, (sheave_u - 0.08, s * (m(cs["width"]) / 2 + 0.05) - 0.01, sheave_w - 0.08),
                      (fu + fh, s * (m(cs["width"]) / 2 + 0.05) + 0.01, frame_top - 0.12), st["steel"])
    # the weight hangs at the sheave's back, the cable from the carriage over the sheave and down to it
    wu = sheave_u - sr
    wh, wb, whh = m(wt["half"]), m(wt["bottom"]), m(wt["height"])
    prims.box(body, (wu - wh, -wh, wb), (wu + wh, wh, wb + whh), st["concrete"])
    cr = m(r["cableDia"]) / 2
    if lod <= 2:
        n = parts.sides(lod, 6, 4, 3)
        prims.cylinder(body, (-hu, 0.0, cable_w), (sheave_u, 0.0, cable_w), cr, n, st["rod"], caps=(False, False))
        prims.cylinder(body, (wu, 0.0, sheave_w), (wu, 0.0, wb + whh), cr, n, st["rod"], caps=(False, False))
    if lod <= 1:   # the lifting eye on the weight
        prims.box(body, (wu - 0.06, -0.02, wb + whh), (wu + 0.06, 0.02, wb + whh + 0.1), st["steel"])

    guide(body, spec["guide"], hg, 1, lod, st)
    bullwheel(a, body, c, rope, lod, st)

    a.body = body
    rope_sockets(a, hg, rope, spec["sockets"], load=True)
    for i, p in enumerate(piers):
        a.sockets["pier_top" if i == 0 else f"pier_top_{i + 1}"] = p
    a.dims.update({"rope": c["ropeElevation"], "railTop": round(rail_top * 1000), "weightU": round(wu * 1000),
                   "lineGauge": c["lineGauge"]})
    return a
