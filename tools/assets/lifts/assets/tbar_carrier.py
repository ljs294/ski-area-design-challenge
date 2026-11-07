"""Monta T-bar carriers (monta_tbar.json "carrier"; surface lifts, owner 2026-11-02: one lift, two carriers; rebuilt to
the owner's reference installation, review findings C1-C19, kept outside the repo).

  springbox  the grip, a small dark clamp with a domed cap on the rope's outboard side; the hanger, one tube that
             leaves the grip outboard and sweeps down in a half ellipse back in toward the rope (with the rope, a D);
             and the recoil box, a pale rounded housing tapering to its collar, hung on a pin through its upper part
             (the pin's boss on its outboard face, the hanger's end on the boss), swinging fore and aft. The cord leaves
             the collar at the box's bottom. Origin on the rope at the grip; u the way the carrier travels, +v outboard
             (away from the wheel's centre: the carrier's right on a lift whose loaded rope is on the right going up),
             w up. The box is a hinged part (pivot "box", axis v); socket "cord" at the collar's foot.
  bar_t      the T bar: a yellow stem with a plain socket at its top (where the cord ends) and one continuous black
             padded crossbar in a shallow arch, a rider each side of the stem. Origin at the eye (the socket's top);
             sockets eye, seat_1 and seat_2 (on the pad's top).
  bar_j      the J bar (owner 2026-11-06): the T bar with one side cut off, the same stem and the same padded bar
             to one side (-v), stopping just past the stem; one rider (seat_1).

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
        cap = m(g["capDia"]) / 2   # the clamp's domed cap (R22), on the clamp's outboard half, clear of a hold-down sheave's dish
        vg = m(g["vOut"]) - cap
        prims.cylinder(body, (0.0, vg, m(g["wHigh"])), (0.0, vg, m(g["wHigh"]) + 1.5 * cap), cap, 8, st["grip"], caps=(False, True), r1=cap * 0.3)
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
        taper = m(b["taper"]) / (2 * hu)   # the lower third narrows toward the collar (R23)
        prof = [(pu * (1.0 - taper * max(0.0, -hh / 3 - pw) / (2 * hh / 3)), pw) for pu, pw in prof]
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
    # owner 2026-11-06: the J is the T with one side cut off, not a hooked bar: the same stem straight down into the same
    # padded bar in the same shallow arch, which on the J stops just past the stem (jBar.past) and carries one rider (-v)
    t = cr["tBar"]
    half, br = m(t["length"]) / 2, m(t["dia"]) / 2
    j = variant == "bar_j"
    end = m(cr["jBar"]["past"]) if j else half
    z = -L
    # the stem: the socket at its top, then the plain tube down into the bar (fat far out, to stay a yellow stroke)
    if lod <= 1:
        prims.cylinder(mb, (0, 0, 0), (0, 0, -kl), kr, parts.sides(lod, 10, 6), st["stem"], caps=(True, False))
        prims.cylinder(mb, (0, 0, -kl + 0.01), (0, 0, z), sr, parts.sides(lod, 10, 6), st["stem"], caps=(False, False))
    elif lod == 2:
        prims.beam(mb, (0, 0, 0), (0, 0, z), 0.06, 0.06, st["stem"], up=U)   # its top end shows from above
    else:
        prims.cylinder(mb, (0, 0, 0), (0, 0, z), 0.045, 3, st["stem"], caps=(False, False))
    # the crossbar: one continuous black pad in a shallow arch, its ends turned up (R21); the J's half of it
    arch = m(t["arch"])
    if lod <= 1:
        n_arc = (6 if lod == 0 else 2) // (2 if j else 1)
        ys = [-half + (end + half) * k / n_arc for k in range(n_arc + 1)]
        pts = [Vector((0.0, y, z + arch * (y / half) ** 2)) for y in ys]
        prims.tube_path(mb, pts, br, parts.sides(lod, 10, 6), st["pad"], caps=True)
    elif lod == 2:
        prims.beam(mb, (0, -half, z + arch), (0, end, z + arch), 2 * br, 2 * br, st["pad"])
    else:
        prims.cylinder(mb, (0, -half, z), (0, end, z), br * 1.2, 3, st["pad"], caps=(False, False))
    so = m(t["seatOffset"])
    ws = z + arch * (so / half) ** 2 + br
    seats = {"seat_1": (0.0, -so, ws)} if j else {"seat_1": (0.0, -so, ws), "seat_2": (0.0, so, ws)}
    if j:
        a.dims.update({"barReach": round((half + end) * 1000), "barBelowEye": sm["length"]})
    else:
        a.dims.update({"barWidth": t["length"], "barBelowEye": sm["length"]})

    a.body = mb
    a.sockets = {"eye": (0.0, 0.0, 0.0), **seats}
    return a
