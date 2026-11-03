"""Monta T-bar carriers (monta_tbar.json "carrier"; surface lifts, owner 2026-11-02: one lift, two carriers; rebuilt to
the owner's reference installation, review findings C1-C19, kept outside the repo).

  springbox  the grip, a small dark clamp on the rope's outboard side; the hanger, one tube that leaves the grip
             outboard and sweeps down in a half ellipse back in toward the rope (with the rope, a D); and the recoil
             box, a pale rounded housing hung on a pin through its upper part (the pin's boss on its outboard face, the
             hanger's end on the boss), swinging fore and aft. The cord leaves a collar at the box's bottom. Origin on
             the rope at the grip; u the way the carrier travels, +v outboard (away from the wheel's centre: the
             carrier's right on a lift whose loaded rope is on the right going up), w up. The box is a hinged part
             (pivot "box", axis v); socket "cord" at the collar's foot.
  bar_t      the T bar: a yellow stem with a plain socket at its top (where the cord ends) and one continuous black
             padded crossbar, a rider each side of the stem. Origin at the eye (the socket's top); sockets eye,
             seat_1 and seat_2 (on the pad's top).
  bar_j      the J bar: the same stem bending in one yellow curve into a seat to one side (-v), padded black, with a
             rounded upturned tip; one rider (seat_1).

The cord itself is drawn by the game from "cord" to the bar's "eye", like the rope; retracted, the eye hangs cordGap
under the cord socket.
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
        "grip": Style("paint_dark", snow=0.1),
        "steel": Style("galvanised", snow=0.3),
        "box": Style("white", snow=0.5),
        "pad": Style("seat_pad", snow=0.1),
        "stem": Style("safety_yellow", snow=0.1),
    }


def build(spec, lod, stage, variant):
    if variant == "springbox":
        return springbox(spec, lod)
    return bar(spec, lod, variant)


def rounded_rect(hu, hw, r, steps):
    """A rectangle 2hu x 2hw with corners of radius r (steps segments each, 0: square), counter-clockwise."""
    if steps == 0:
        return [(-hu, -hw), (hu, -hw), (hu, hw), (-hu, hw)]
    pts = []
    for cu, cw, a0 in ((hu - r, -hw + r, -90), (hu - r, hw - r, 0), (-hu + r, hw - r, 90), (-hu + r, -hw + r, 180)):
        for k in range(steps + 1):
            a = math.radians(a0 + 90 * k / steps)
            pts.append((cu + r * math.cos(a), cw + r * math.sin(a)))
    return pts


def hanger_points(g, h, v_end, w_end, n):
    """The hanger's centreline in the v-w plane, n segments: from the grip's outboard face, level and outboard, round
    a half ellipse (outmost at outV) and back in, level, to the boss on the box's outboard face."""
    v0, w0 = m(g["vOut"]), m(h["w"])
    amp = m(h["outV"]) - (v0 + v_end) / 2
    out = []
    for k in range(n + 1):
        t = math.pi * k / n
        out.append(Vector((0.0, v0 + (v_end - v0) * t / math.pi + amp * math.sin(t), w0 + (w_end - w0) * (1 - math.cos(t)) / 2)))
    return out


def springbox(spec, lod):
    cr = spec["carrier"]
    g, h, b = cr["grip"], cr["hanger"], cr["box"]
    st = styles()
    a = Asset("monta_tbar_springbox")
    body = MeshBuilder()
    box = MeshBuilder()
    hu, hv, hh = m(b["lengthU"]) / 2, m(b["widthV"]) / 2, m(b["height"]) / 2
    vc, top = m(b["centreV"]), m(b["top"])
    wc = top - hh
    pin_w = top - m(b["pinBelowTop"])
    v_face = vc + hv                          # the box's outboard face
    v_boss = v_face + m(b["bossOut"])         # the boss's face, where the hanger ends
    hr = m(h["dia"]) / 2

    # the grip: a small dark clamp outboard of the sheaves' faces, its thin jaw reaching in to the rope
    if lod <= 1:
        prims.box(body, (-m(g["halfU"]), m(g["vIn"]), m(g["wLow"])), (m(g["halfU"]), m(g["vOut"]), m(g["wHigh"])), st["grip"])
    if lod == 0:
        prims.box(body, (-m(g["jawHalfU"]), m(g["jawIn"]), -m(g["jawHalf"])), (m(g["jawHalfU"]), m(g["vIn"]) + 0.002, m(g["jawHalf"])),
                  st["grip"], skip=("+1",))
    # the hanger: one tube round the half ellipse
    if lod <= 1:
        pts = hanger_points(g, h, v_boss, pin_w, 14 if lod == 0 else 5)
        prims.tube_path(body, pts, hr, parts.sides(lod, 10, 5), st["steel"], caps=True)
    elif lod == 2:
        pts = hanger_points(g, h, v_boss, pin_w, 3)
        for p0, p1 in zip(pts, pts[1:]):
            prims.beam(body, p0, p1, 2 * hr, 2 * hr, st["steel"], up=U, skip_ends=True)

    # the recoil box: a rounded housing (its profile in the u-w plane, the drum's axle across the line), the pin's
    # boss on its outboard face, the cord's collar under it
    if lod <= 2:
        prof = rounded_rect(hu, hh, m(b["corner"]), (4, 1, 0, 0)[lod])
        prims.prism(box, [(pu, pw + wc) for pu, pw in prof], (0.0, vc - hv, 0.0), U, W, V, 2 * hv, st["box"])
    else:
        prims.box(box, (-hu, vc - hv, wc - hh), (hu, vc + hv, wc + hh), st["box"], skip=("-2",))
    if lod <= 1:
        br = m(b["bossDia"]) / 2
        prims.cylinder(box, (0.0, v_face - 0.005, pin_w), (0.0, v_boss, pin_w), br, parts.sides(lod, 12, 5), st["box"], caps=(False, True))
        cr_ = m(b["collarDia"]) / 2
        prims.cylinder(box, (0.0, vc, wc - hh + 0.01), (0.0, vc, wc - hh - m(b["collarLength"])), cr_, parts.sides(lod, 10, 6),
                       st["box"], caps=(False, True))
    a.parts["box"] = box
    a.pivots["box"] = {"pos": (0.0, vc, pin_w), "axis": (0.0, 1.0, 0.0), "hinge": True}

    cord_w = wc - hh - m(b["collarLength"])
    a.body = body
    a.sockets = {"grip": (0.0, 0.0, 0.0), "cord": (0.0, vc, cord_w)}
    a.socket_parents = {"cord": "box"}
    a.dims.update({"cordBelow": round(-cord_w * 1000), "boxOut": b["centreV"], "boxHeight": b["height"],
                   "hangerOut": h["outV"], "boxTopBelow": round(-top * 1000)})
    return a


def bar(spec, lod, variant):
    cr = spec["carrier"]
    sm = cr["stem"]
    st = styles()
    a = Asset("monta_tbar_" + variant)
    mb = MeshBuilder()
    L = m(sm["length"])
    sr = m(sm["dia"]) / 2
    kr, kl = m(sm["socketDia"]) / 2, m(sm["socketLength"])

    seats = {}
    if variant == "bar_t":
        t = cr["tBar"]
        half, br = m(t["length"]) / 2, m(t["dia"]) / 2
        z = -L
        # the stem: the socket at its top, then the plain tube down into the bar (fat far out, to stay a yellow stroke)
        if lod <= 1:
            prims.cylinder(mb, (0, 0, 0), (0, 0, -kl), kr, parts.sides(lod, 10, 6), st["stem"], caps=(True, False))
            prims.cylinder(mb, (0, 0, -kl + 0.01), (0, 0, z), sr, parts.sides(lod, 10, 6), st["stem"], caps=(False, False))
        elif lod == 2:
            prims.beam(mb, (0, 0, 0), (0, 0, z), 0.06, 0.06, st["stem"], up=U, skip_ends=True)
        else:
            prims.cylinder(mb, (0, 0, 0), (0, 0, z), 0.045, 3, st["stem"], caps=(False, False))
        # the crossbar: one continuous black pad, rounded ends
        if lod <= 1:
            e = 0.035
            prof = [(0.0, -half), (br * 0.55, -half + e * 0.15), (br * 0.88, -half + e * 0.5), (br, -half + e),
                    (br, half - e), (br * 0.88, half - e * 0.5), (br * 0.55, half - e * 0.15), (0.0, half)]
            if lod == 1:
                prof = [prof[0], prof[2], prof[3], prof[4], prof[5], prof[7]]
            prims.lathe(mb, Vector((0.0, 0.0, z)), V, prof, parts.sides(lod, 12, 8), st["pad"])
        elif lod == 2:
            prims.beam(mb, (0, -half, z), (0, half, z), 2 * br, 2 * br, st["pad"])
        else:
            prims.cylinder(mb, (0, -half, z), (0, half, z), br * 1.2, 3, st["pad"], caps=(False, False))
        so = m(t["seatOffset"])
        seats = {"seat_1": (0.0, -so, z + br), "seat_2": (0.0, so, z + br)}
        a.dims.update({"barWidth": t["length"], "barBelowEye": sm["length"]})
    else:
        j = cr["jBar"]
        reach, jr, bend = m(j["reach"]), m(j["dia"]) / 2, m(j["bend"])
        pr, rise = m(j["padDia"]) / 2, m(j["tipRise"])
        z = -L
        # one yellow curve: the stem, a bend into the seat toward -v, the seat, a rounded upturned tip
        if lod <= 1:
            raw = [Vector((0, 0, 0)), Vector((0, 0, z)), Vector((0, -reach, z)), Vector((0, -reach, z + rise))]
            pts = prims.fillet(raw, 2, rise * 0.6, 4 if lod == 0 else 1)
            pts = prims.fillet(pts, 1, bend, 6 if lod == 0 else 2)
            pts = [Vector(p) for p in pts]
            prims.cylinder(mb, (0, 0, 0), (0, 0, -kl), kr, parts.sides(lod, 10, 6), st["stem"], caps=(True, False))
            prims.tube_path(mb, [Vector((0, 0, -kl + 0.01))] + pts[1:], jr, parts.sides(lod, 10, 5), st["stem"], caps=True)
            e = 0.02
            p0, p1 = m(j["padFrom"]), m(j["padTo"])
            prof = [(0.0, -p1), (pr * 0.8, -p1 + e * 0.4), (pr, -p1 + e), (pr, -p0 - e), (pr * 0.8, -p0 - e * 0.4), (0.0, -p0)]
            if lod == 1:
                prof = [prof[0], prof[2], prof[3], prof[5]]
            prims.lathe(mb, Vector((0.0, 0.0, z)), V, prof, parts.sides(lod, 12, 6), st["pad"])
        elif lod == 2:
            prims.beam(mb, (0, 0, 0), (0, 0, z), 0.06, 0.06, st["stem"], up=U, skip_ends=True)
            prims.beam(mb, (0, 0.0, z), (0, -reach, z), 2 * pr, 2 * pr, st["pad"])
        else:
            prims.cylinder(mb, (0, 0, 0), (0, 0, z), 0.045, 3, st["stem"], caps=(False, False))
            prims.cylinder(mb, (0, 0.0, z), (0, -reach, z), pr * 1.2, 3, st["pad"], caps=(False, False))
        seats = {"seat_1": (0.0, -(m(j["padFrom"]) + m(j["padTo"])) / 2, z + pr)}
        a.dims.update({"barReach": j["reach"], "barBelowEye": sm["length"]})

    a.body = mb
    a.sockets = {"eye": (0.0, 0.0, 0.0), **seats}
    return a
