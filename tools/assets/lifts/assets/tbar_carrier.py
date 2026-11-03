"""Monta T-bar carriers (monta_tbar.json "carrier"; surface lifts, owner 2026-11-02: one lift, two carriers).

  springbox  the fixed grip clamped on the rope with an arm out and down, and the recoil box (a drum housing, its
             axle across the line, with rim lips and the cord's outlet underneath) hung from the arm's pin. Origin on
             the rope at the grip; u the way the carrier travels, +v outboard (away from the wheel's centre: the
             carrier's right on a lift whose loaded rope is on the right going up), w up. The box is a hinged part
             (pivot "box", on the arm's pin, swinging fore and aft); socket "cord" at its outlet.
  bar_t      the T bar: an eye where the cord ends, a damper tube, the stem and the crossbar with two pads, one rider
             each side of the stem. Origin at the eye; sockets eye, seat_1 and seat_2 (on the pads' tops).
  bar_j      the J bar: the same stem with the bar to one side (-v) and a short upturned tip; one rider (seat_1).

The cord itself is drawn by the game from "cord" to the bar's "eye", like the rope.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "surface_carrier"
LODS = 4
SPEC = "monta_tbar.json"


def m(x):
    return x / 1000.0


def styles():
    return {
        "grip": Style("grip", snow=0.2),
        "steel": Style("galvanised", snow=0.2),
        "box": Style("white", snow=0.6),
        "rim": Style("paint_dark", snow=0.3),
        "rod": Style("machined"),
        "pad": Style("seat_pad", snow=0.0),
        "stem": Style("aluminium", snow=0.1),
        "damper": Style("paint_dark", snow=0.1),
    }


def build(spec, lod, stage, variant):
    if variant == "springbox":
        return springbox(spec, lod)
    return bar(spec, lod, variant)


def springbox(spec, lod):
    cr = spec["carrier"]
    g, b = cr["grip"], cr["box"]
    st = styles()
    a = Asset("monta_tbar_springbox")
    body = MeshBuilder()
    box = MeshBuilder()
    rope_r = spec["common"]["ropeDiameter"] / 2000.0
    gl, gh, gw = m(g["length"]), m(g["height"]), m(g["width"])
    out = m(g["armOut"])
    ar = m(g["armDia"]) / 2
    centre_w = -m(b["centreBelow"])
    R, bw = m(b["dia"]) / 2, m(b["width"])
    pin = Vector((0.0, out, centre_w + R + 0.07))         # the box hangs from the arm's end on this pin

    # the grip: a clamp block round the rope (its jaws closed on it), with the arm leaving it outboard and turning down
    if lod <= 1:
        prims.box(body, (-gl / 2, -gw / 2, -gh / 2), (gl / 2, gw / 2 + rope_r, gh / 2), st["grip"])
    if lod <= 1:
        bend = Vector((0.0, out, 0.0))
        prims.tube_path(body, [Vector((0.0, gw / 2, 0.0)), bend + V * -0.06, bend + W * -0.06, pin + W * 0.02],
                        ar, parts.sides(lod, 8, 4), st["steel"], caps=True)
        with body.grouped("#pin"):   # the pin through the arm's end and the box's lugs
            prims.cylinder(body, pin - V * 0.05, pin + V * 0.05, 0.014, 6, st["rod"])
    elif lod == 2:
        prims.beam(body, (0.0, gw / 2, 0.0), (0.0, out, 0.0), 2 * ar, 2 * ar, st["steel"])
        prims.beam(body, (0.0, out, 0.0), pin, 2 * ar, 2 * ar, st["steel"], up=U)

    # the recoil box: lugs up to the pin, the drum housing, its rim lips, the cord's outlet underneath
    c = Vector((0.0, out, centre_w))
    if lod <= 2:
        n = parts.sides(lod, 18, 10, 6)
        prims.cylinder(box, c - V * bw / 2, c + V * bw / 2, R, n, st["box"], caps=(lod <= 1, lod <= 1))
    else:
        prims.box(box, c - Vector((R, bw / 2, R)), c + Vector((R, bw / 2, R)), st["box"])
    if lod == 0:
        rr, rw = m(b["rimDia"]) / 2, m(b["rimWidth"])
        for s in (-1, 1):
            prims.cylinder(box, c + V * s * (bw / 2 - rw), c + V * s * (bw / 2 + 0.002), rr, 18, st["rim"])
            prims.cylinder(box, c + V * s * (bw / 2 + 0.002), c + V * s * (bw / 2 + 0.03), 0.04, 8, st["rod"])   # the drum's hub
    if lod <= 1:
        for s in (-1, 1):   # the lugs from the housing up to the pin
            prims.box(box, (-0.035, out + s * 0.035 - 0.006, pin.z - 0.03), (0.035, out + s * 0.035 + 0.006, c.z + R - 0.02), st["steel"])
        cord_top = c.z - R + 0.01
        prims.cylinder(box, (0.0, out, cord_top), (0.0, out, -m(b["cordBelow"])), 0.03, parts.sides(lod, 8, 6), st["rim"],
                       caps=(False, True))
    a.parts["box"] = box
    a.pivots["box"] = {"pos": tuple(pin), "axis": (0.0, 1.0, 0.0), "hinge": True}

    a.body = body
    a.sockets = {"grip": (0.0, 0.0, 0.0), "cord": (0.0, out, -m(b["cordBelow"]))}
    a.socket_parents = {"cord": "box"}
    a.dims.update({"cordBelow": b["cordBelow"], "boxOut": g["armOut"], "boxDia": b["dia"]})
    return a


def bar(spec, lod, variant):
    cr = spec["carrier"]
    sm = cr["stem"]
    st = styles()
    a = Asset("monta_tbar_" + variant)
    mb = MeshBuilder()
    L = m(sm["length"])
    sr, dl, dr = m(sm["dia"]) / 2, m(sm["damperLength"]), m(sm["damperDia"]) / 2
    er = m(sm["eyeDia"]) / 2
    if lod <= 1:
        # the eye: a ring across the line (the cord's thimble passes through it), then the damper tube and the stem
        prims.lathe(mb, Vector((0.0, 0.0, -er)), V, [(er - 0.008, -0.006), (er, -0.006), (er, 0.006), (er - 0.008, 0.006)],
                    parts.sides(lod, 10, 6), st["rod"])
        prims.cylinder(mb, (0, 0, -2 * er + 0.005), (0, 0, -2 * er - dl), dr, parts.sides(lod, 8, 6), st["damper"])
        prims.cylinder(mb, (0, 0, -2 * er - dl), (0, 0, -L), sr, parts.sides(lod, 8, 5), st["stem"], caps=(False, False))
    elif lod == 2:
        prims.beam(mb, (0, 0, 0), (0, 0, -L), 2 * sr, 2 * sr, st["stem"], up=U)
    else:
        prims.cylinder(mb, (0, 0, 0), (0, 0, -L), sr * 1.4, 3, st["stem"], caps=(False, False))

    seats = {}
    if variant == "bar_t":
        t = cr["tBar"]
        hw, br, ph, pr = m(t["halfWidth"]), m(t["dia"]) / 2, m(t["padHalf"]), m(t["padDia"]) / 2
        z = -L - br
        if lod <= 1:
            prims.cylinder(mb, (0, -hw, z), (0, hw, z), br, parts.sides(lod, 8, 5), st["stem"])
            for s in (-1, 1):   # a pad each side of the stem
                prims.cylinder(mb, (0, s * 0.06, z), (0, s * (0.06 + ph), z), pr, parts.sides(lod, 10, 6), st["pad"])
            if lod == 0:   # the T's welded crown
                prims.cylinder(mb, (0, 0, -L + 0.06), (0, 0, z), sr * 1.6, 8, st["stem"], r1=br * 1.3)
        elif lod == 2:
            prims.beam(mb, (0, -hw, z), (0, hw, z), 2 * pr, 2 * pr, st["pad"])
        else:
            prims.cylinder(mb, (0, -hw, z), (0, hw, z), pr, 3, st["pad"], caps=(False, False))
        seats = {"seat_1": (0.0, -(0.06 + ph / 2), z + pr), "seat_2": (0.0, 0.06 + ph / 2, z + pr)}
        a.dims.update({"barWidth": round(2 * hw * 1000), "barBelowEye": round(-z * 1000)})
    else:
        j = cr["jBar"]
        reach, br, pr, up = m(j["reach"]), m(j["dia"]) / 2, m(j["padDia"]) / 2, m(j["upturn"])
        z = -L - br
        if lod <= 1:
            n = parts.sides(lod, 8, 5)
            # the stem bends into the bar toward -v, which runs out and turns up at its tip
            prims.tube_path(mb, [Vector((0, 0, -L + 0.05)), Vector((0, -0.05, z)), Vector((0, -reach, z)), Vector((0, -reach - 0.04, z + up))],
                            br, n, st["stem"], caps=True)
            prims.cylinder(mb, (0, -0.08, z), (0, -reach + 0.04, z), pr, parts.sides(lod, 10, 6), st["pad"])
        elif lod == 2:
            prims.beam(mb, (0, 0.0, z), (0, -reach, z), 2 * pr, 2 * pr, st["pad"])
        else:
            prims.cylinder(mb, (0, 0.0, z), (0, -reach, z), pr, 3, st["pad"], caps=(False, False))
        seats = {"seat_1": (0.0, -(0.08 + reach) / 2, z + pr)}
        a.dims.update({"barReach": j["reach"], "barBelowEye": round(-z * 1000)})

    a.body = mb
    a.sockets = {"eye": (0.0, 0.0, 0.0), **seats}
    return a
