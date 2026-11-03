"""Monta T-bar terminals and the operator hut (monta_tbar.json "drive", "return", "hut"; surface lifts, owner
2026-11-02; rebuilt to the owner's reference installation, review findings T1-T17 and decisions D1,
D2, D8, D9, kept outside the repo).

  drive   the BOTTOM station (D1: surface lifts always drive at the bottom): one box column, its lower part a cabinet,
          gusseted at its foot on a concrete block, carrying a head frame of two girders along the line. Behind the
          column the bullwheel hangs under a hoop-railed grating deck that ends over its centre (the wheel's back half in
          the open) with the enclosed machinery house on it; toward the line a separate narrower platform a step down
          over two hold-down trains, 8 sheaves on the leaving (loaded) rope and 4 on the arriving one, black hanger
          guides at their line ends. The loading hoop hangs just under the leaving train on a short rod from a bracket
          off its girder, outboard of the bars, where the operator steadies them. No ladder on this station. Rope at
          3.4 m (D8).
  return  the TOP station: one tapered box column leaning back toward the wheel, hinged at its foot, and a plumb box
          strut, on a flat pad, carrying bare girders with two hoop-railed platforms (over the trains, round the
          bearing); the bullwheel cantilevered behind with a thin guard rail round it and along the trains, 8-sheave
          hold-down trains on both ropes toward the line, a galvanised ladder up the column's line face to the trains'
          platform (its railing open at the ladder's head), and the box-section gallows for the wire (D9). Tension is
          hidden (D2). Rope at 4.5 m.
  hut     the red operator hut (D9, Q3): its own asset, placed by the game beside the bottom station.

The loaded rope is on the right going up: it leaves the drive on v+ and arrives at the return on v-. The lift frame's
origin is on the bullwheel's axle at grade, u toward the line; the hut's on its floor's centre, its door facing -u.
Blocks and pads run down to "pierBottom" (15 m), as on the chairlift terminals, so a raised terminal still stands.
"""
import math

from mathutils import Vector

import tbar
from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "surface_terminal"
LODS = 4
SPEC = "monta_tbar.json"


def m(x):
    return x / 1000.0


def kind_of(variant):
    return "surface_hut" if variant == "hut" else KIND


def styles():
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("galvanised", snow=0.5),
        "grating": Style("grating", snow=0.8),
        "paint": Style("paint_grey", snow=0.6),
        "house": Style("paint_grey", snow=0.8),
        "dark": Style("trim_dark", snow=0.3),
        "rod": Style("machined"),
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "galv": Style("galvanised", snow=0.5),
        "black": Style("trim_dark", snow=0.3),
        "hut": Style("sheave_red", snow=0.4),
        "white": Style("white", snow=0.6),
    }


def build(spec, lod, stage, variant):
    if variant == "hut":
        return hut(spec, lod)
    return drive(spec, lod) if variant == "drive" else ret(spec, lod)


# -- shared parts ---------------------------------------------------------------------------------------------------
def wheel(a, body, c, rope, lod, st):
    """The bullwheel, flat on its shaft: a deep grooved rim, eight flat spokes, the inner ring at ringAt of the rim,
    the hub; galvanised. A spinning part (pivot "bullwheel") up to LOD2."""
    bw = c["bullwheel"]
    centre = Vector((0.0, 0.0, rope))
    mb = MeshBuilder() if lod <= 2 else body
    ro, ri, rg = m(bw["rimOut"]), m(bw["rimIn"]), m(bw["pitch"]) / 2 - c["ropeDiameter"] / 2000.0
    d2, mouth = m(bw["rimDepth"]) / 2, m(bw["grooveMouth"]) / 2
    n = parts.sides(lod, 48, 32, 16, 12)
    if lod <= 1:
        prims.lathe(mb, centre, W, [(ri, -d2), (ro, -d2), (ro, -mouth), (rg, 0.0), (ro, mouth), (ro, d2), (ri, d2)], n, st["galv"])
    elif lod == 2:
        prims.lathe(mb, centre, W, [(ri, -d2), (ro, -d2), (ro, d2), (ri, d2)], n, st["galv"])
    else:
        prims.cylinder(mb, centre - W * d2, centre + W * d2, ro, n, st["galv"], caps=(False, False))
    if lod <= 1:   # the inner ring
        rr, h = ri * bw["ringAt"], m(bw["ringSize"]) / 2
        prims.lathe(mb, centre, W, [(rr - h, -h), (rr + h, -h), (rr + h, h), (rr - h, h)], parts.sides(lod, 32, 20), st["galv"])
    hub = m(bw["hubDia"]) / 2
    if lod <= 2:
        for k in range(bw["spokes"]):
            ang = 2 * math.pi * (k + 0.5) / bw["spokes"]
            d = Vector((math.cos(ang), math.sin(ang), 0.0))
            prims.beam(mb, centre + d * (hub - 0.02), centre + d * (ri + 0.02), m(bw["spokeWidth"]), m(bw["spokeThick"]),
                       st["galv"], skip_ends=True)
        prims.cylinder(mb, centre - W * m(bw["hubHeight"]) / 2, centre + W * m(bw["hubHeight"]) / 2, hub, parts.sides(lod, 16, 10, 8),
                       st["galv"])
    if lod <= 2:
        a.parts["bullwheel"] = mb
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    return centre.z + m(bw["hubHeight"]) / 2


def girders(mb, g, u_from, u_to, lod, st):
    """The head frame: two box girders along the line at +-halfV, the walkway grating between them on top."""
    hv, gw, gd, gb = m(g["halfV"]), m(g["width"]) / 2, m(g["depth"]), m(g["bottom"])
    for s in (-1, 1):
        prims.box(mb, (u_from, s * hv - gw, gb), (u_to, s * hv + gw, gb + gd), st["steel"])
    return gb + gd


def cross(mb, g, u, half_u, lod, st, w0=None, w1=None):
    """A cross girder between the two girders at u, at the girders' depth unless w0/w1 say otherwise."""
    hv, gw, gb, gd = m(g["halfV"]), m(g["width"]) / 2, m(g["bottom"]), m(g["depth"])
    prims.box(mb, (u - half_u, -hv + gw, gb if w0 is None else w0), (u + half_u, hv - gw, gb + gd if w1 is None else w1), st["steel"],
              skip=("-1", "+1"))


def walkway(mb, g, u_from, u_to, top, lod, st, deck_v=None, rail=None, open_ends=()):
    """Grating on the girders from u_from to u_to (deck_v: its half width, the girders' outer faces by default), a
    railing round it (rail: its height; open_ends: which ends, -1 or +1, stay open)."""
    hv = m(g["halfV"]) + m(g["width"]) / 2 if deck_v is None else deck_v
    prims.box(mb, (u_from, -hv, top), (u_to, hv, top + 0.04), st["grating"], skip=("-2",) if lod >= 2 else ())
    if rail is None or lod >= 3:
        return
    t = top + 0.04
    r = hv - 0.03
    for s in (-1, 1):
        parts.railing(mb, [(u_from + 0.03, s * r, t), (u_to - 0.03, s * r, t)], rail, lod, st["steel"], posts_every=1.4)
    for e, u in ((-1, u_from + 0.03), (1, u_to - 0.03)):
        if e not in open_ends:
            parts.railing(mb, [(u, -r, t), (u, r, t)], rail, lod, st["steel"], posts_every=1.4)


def trains(mb, a, spec, rope, w_pin, rows, g, lod, st):
    """The hold-down trains under the girders: rows = [(tag, v side, sheaves, centre u)], each hung from its girder on
    a hanger plate per rocker pair, the end pin through the plate; hanger guides at the line ends."""
    c = spec["common"]
    T = parts.metres(spec["train"])
    T["ropeR"] = c["ropeDiameter"] / 2000.0
    s = parts.metres(c["sheave"])
    hd = spec["tower"]["head"]
    hg = m(c["lineGauge"]) / 2
    gb = m(g["bottom"])
    ends = []
    for tag, side, n, uc in rows:
        v_rope = side * hg
        r = tbar.train(mb, a, tag, v_rope, "hold", n, T, s, hd, lod, st, w_pin, m(uc), ends=(1,))
        _, inb, _, _, v_eq, v_ep = tbar.train_v(T, v_rope)
        v0, v1 = sorted((v_ep, v_ep + inb * 0.03))
        if lod <= 2:   # the hanger plate from the girder down past the end pin
            prims.box(mb, (m(uc) - 0.18, v0, w_pin - 0.08), (m(uc) + 0.18, v1, gb + 0.01), st["steel"])
        if lod == 0:
            with mb.grouped("#pin"):
                prims.cylinder(mb, (m(uc), v_ep + inb * 0.04, w_pin), (m(uc), v_eq - inb * (T["eqThick"] / 2 + 0.006), w_pin),
                               T["pinDia"] / 2, 10, st["rod"])
        us = tbar.axles_u(T, n)
        ends.append(m(uc) + max(us) + s["dia"] / 2)
        assert abs(r - rope) < 1e-6
    return ends


def block(mb, lo_u, hi_u, half_v, top, bottom, st):
    prims.box(mb, (lo_u, -half_v, bottom), (hi_u, half_v, top), st["concrete"], skip=("-2",))


def rope_sockets(a, hg, rope, u_out, sk, load):
    a.sockets.update({
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_out": (u_out, -hg, rope), "rope_right_out": (u_out, hg, rope),
    })
    if load:   # riders take a bar under the leaving loaded rope (v+)
        a.sockets["carrier_load"] = (m(sk["loadU"]), hg, 0.0)
    else:      # and let go of it before the wheel under the arriving one (v-)
        a.sockets["carrier_unload"] = (m(sk["unloadU"]), -hg, 0.0)


def house(mb, h, top, lod, st):
    """The machinery house on the drive's deck: a box with rounded top edges, louvres on its back (-u) end, a band of
    dark window panels down its -v side (R14)."""
    u0, u1, hv, ht, rc = m(h["uFrom"]), m(h["uTo"]), m(h["halfV"]), m(h["height"]), m(h["corner"])
    steps = (4, 2, 0, 0)[lod]
    if steps:
        prof = [(-hv, 0.0), (hv, 0.0)]
        for k in range(steps + 1):
            ang = math.pi / 2 * k / steps
            prof.append((hv - rc + rc * math.cos(ang), ht - rc + rc * math.sin(ang)))
        for k in range(steps + 1):
            ang = math.pi / 2 + math.pi / 2 * k / steps
            prof.append((-hv + rc + rc * math.cos(ang), ht - rc + rc * math.sin(ang)))
        prims.prism(mb, [(v, w + top) for v, w in prof], (u0, 0.0, 0.0), V, W, U, u1 - u0, st["house"])
    else:
        prims.box(mb, (u0, -hv, top), (u1, hv, top + ht), st["house"], skip=("-2",))
    if lod >= 2:
        return
    win = h["windows"]
    pw = (u1 - u0 - 0.3) / win
    for k in range(win):   # the window panels on the -v side
        a0 = u0 + 0.15 + k * pw + 0.06
        prims.box(mb, (a0, -hv - 0.006, top + m(h["windowFrom"])), (a0 + pw - 0.12, -hv + 0.006, top + m(h["windowTo"])),
                  st["dark"], skip=("-1", "+1") if lod else ())
    if lod == 0:   # louvres on the back end
        for k in range(h["louvres"]):
            w = top + 0.45 + k * 0.12
            prims.box(mb, (u0 - 0.032, -hv * 0.6, w), (u0 - 0.002, hv * 0.6, w + 0.04), st["paint"])


def hook(mb, a, at, lod, st, rod=0.012, dia=0.07):
    """The wire's hooked eye on a short rod at `at` (lift point), socket "wire" at the eye's saddle."""
    at = Vector(at)
    eye = at + W * (0.06 + dia / 2)
    if lod == 0:
        prims.cylinder(mb, at, eye - W * dia * 0.3, rod, 6, st["steel"], caps=(False, True))
        ring = [eye + (U * math.cos(2 * math.pi * k / 8) + W * math.sin(2 * math.pi * k / 8)) * dia / 2 for k in range(8)]
        prims.tube_path(mb, [Vector(p) for p in ring], rod * 0.8, 6, st["steel"], caps=False, closed=True)
    a.sockets["wire"] = tuple(eye - W * dia / 2)


# -- the bottom station ---------------------------------------------------------------------------------------------
def drive(spec, lod):
    c, d = spec["common"], spec["drive"]
    st = styles()
    a = Asset("monta_tbar_drive")
    body = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    rope = m(d["rope"])
    g = d["girders"]
    col = d["column"]
    cu, chu, chv = m(col["u"]), m(col["halfU"]), m(col["halfV"])

    # the block and the column, its lower part the cabinet with its door, a slim side cabinet, conduits, foot gussets
    bl = d["block"]
    block(body, cu - m(bl["half"]), cu + m(bl["half"]), m(bl["half"]), m(bl["top"]), m(d["pierBottom"]), st)
    base = m(bl["top"])
    gb = m(g["bottom"])
    prims.box(body, (cu - chu, -chv, base), (cu + chu, chv, gb), st["paint"], skip=("-2", "+2"))
    if lod <= 1:
        cab = d["cabinet"]
        prims.box(body, (cu - chu - 0.012, -chv + 0.05, base + m(cab["doorFrom"])), (cu - chu, chv - 0.05, base + m(cab["doorTo"])),
                  st["dark"], skip=("+0",))
        sc = cab["side"]
        prims.box(body, (cu - m(sc["halfU"]), -chv - m(sc["depth"]), base + m(sc["from"])), (cu + m(sc["halfU"]), -chv, base + m(sc["to"])),
                  st["paint"])
        gu = col["gusset"]
        gh, go, gt = m(gu["height"]), m(gu["out"]), m(gu["thick"])
        prims.box(body, (cu - chu - go, -chv - go, base), (cu + chu + go, chv + go, base + 0.03), st["paint"], skip=("-2",))
        for axis, half, en in ((U, chu, V), (V, chv, U)):   # a gusset on each face (R15)
            for sgn in (-1, 1):
                gusset = [(sgn * half, 0.03), (sgn * (half + go), 0.03), (sgn * half, gh)]
                origin = Vector((cu, 0.0, base)) - en * gt / 2
                prims.prism(body, gusset, origin, axis, W, en, gt, st["paint"])
    if lod == 0:
        for k in range(2):   # conduits from the ground into the side cabinet
            p = Vector((cu - 0.12 + 0.24 * k, -chv - 0.06, base))
            prims.cylinder(body, p, p + W * (m(d["cabinet"]["side"]["from"]) + 0.02), 0.02, 8, st["dark"], caps=(False, False))

    # the head frame: girders from the deck's back end to past the trains, cross girders over the column and the shaft
    dk = d["deck"]
    u_from, u_to = m(dk["uFrom"]), m(g["uTo"])
    top = girders(body, g, u_from, u_to, lod, st)
    cross(body, g, cu, chu, lod, st)
    if lod <= 2:
        for u in (-0.35, 0.35):
            cross(body, g, u, 0.1, lod, st)
        cross(body, g, u_from + 0.1, 0.1, lod, st)
        cross(body, g, u_to - 0.1, 0.1, lod, st)
    # the wheel on its shaft under the deck's back end (its back half in the open, R8), the bearing box between the
    # cross girders
    hub_top = wheel(a, body, c, rope, lod, st)
    if lod <= 2:
        sh = m(c["bullwheel"]["shaftDia"]) / 2
        prims.cylinder(body, (0, 0, hub_top - 0.02), (0, 0, gb - 0.15), sh, parts.sides(lod, 16, 10, 8), st["paint"], caps=(False, False))
        prims.box(body, (-0.25, -0.35, gb - 0.15), (0.25, 0.35, gb), st["paint"], skip=("+2",))
    # the deck: thin grating over the girders with edge channels (R25), hooped railings (R13), the house on it
    dh, du1, ch = m(dk["halfV"]), m(dk["uTo"]), m(dk["channel"])
    prims.box(body, (u_from, -dh, top), (du1, dh, top + 0.04), st["grating"], skip=("-2",) if lod >= 2 else ())
    if lod <= 1:
        for sv in (-1, 1):
            prims.box(body, (u_from, sv * dh - (0.05 if sv > 0 else 0.0), top - ch), (du1, sv * dh + (0.05 if sv < 0 else 0.0), top),
                      st["steel"], skip=("+2",))
        prims.box(body, (u_from, -dh, top - ch), (u_from + 0.05, dh, top), st["steel"], skip=("+2",))
    t_rail, rail = top + 0.04, m(d["rail"])
    r = dh - 0.03
    td = d["trainDeck"]
    tu0, tu1, th = m(td["uFrom"]), m(td["uTo"]), m(td["halfV"])
    if lod <= 2:
        parts.railing_hoops(body, [(du1 - 0.03, -r, t_rail), (u_from + 0.03, -r, t_rail), (u_from + 0.03, r, t_rail), (du1 - 0.03, r, t_rail)],
                            rail, lod, st["steel"])
        for sv in (-1, 1):
            parts.railing_hoops(body, [(du1 - 0.03, sv * r, t_rail), (du1 - 0.03, sv * (th + 0.03), t_rail)], rail, lod, st["steel"])
    house(body, d["house"], top + 0.04, lod, st)
    # the separate narrower platform over the trains, a step down from the deck (R9): grating on two cross beams hung
    # under the girders, its own hoop railing open toward the deck
    tt = top - m(td["drop"])
    prims.box(body, (tu0, -th, tt), (tu1, th, tt + 0.04), st["grating"], skip=("-2",) if lod >= 2 else ())
    if lod <= 2:
        hv_w = m(g["halfV"]) + m(g["width"]) / 2
        for u in (tu0 + 0.15, tu1 - 0.15):
            prims.box(body, (u - 0.05, -hv_w, tt - 0.1), (u + 0.05, hv_w, tt), st["steel"])
            if lod <= 1:
                for sv in (-1, 1):
                    prims.box(body, (u - 0.05, sv * hv_w - (0.1 if sv > 0 else 0.0), tt), (u + 0.05, sv * hv_w + (0.1 if sv < 0 else 0.0), gb),
                              st["steel"], skip=("-2", "+2"))
        parts.railing_hoops(body, [(tu0 + 0.03, -th + 0.03, tt + 0.04), (tu1 - 0.03, -th + 0.03, tt + 0.04), (tu1 - 0.03, th - 0.03, tt + 0.04),
                                   (tu0 + 0.03, th - 0.03, tt + 0.04)], rail, lod, st["steel"])

    # the trains under the girders: 8 sheaves on the leaving (loaded) rope, 4 on the arriving one
    T = parts.metres(spec["train"])
    s = parts.metres(c["sheave"])
    w_pin = rope + T["eqDrop"] + T["rockerDrop"] + s["grooveBottom"] / 2 + c["ropeDiameter"] / 2000.0
    tr = d["trains"]
    ends = trains(body, a, spec, rope, w_pin, [("r", 1, tr["leave"], tr["leaveU"]), ("l", -1, tr["arrive"], tr["arriveU"])], g, lod, st)

    # the loading hoop (R12): a black ring in the u-w plane, outboard of the bars' path, on a rod from a bracket off
    # the loaded girder
    hp = d["hoop"]
    if lod <= 1:
        hu, hr, ht = m(hp["u"]), m(hp["dia"]) / 2, m(hp["tube"]) / 2
        hc = Vector((hu, m(hp["v"]), m(hp["w"])))
        ring = [hc + (U * math.cos(2 * math.pi * k / 16) + W * math.sin(2 * math.pi * k / 16)) * hr for k in range(16 if lod == 0 else 8)]
        prims.tube_path(body, [Vector(p) for p in ring], ht, parts.sides(lod, 8, 5), st["black"], caps=False, closed=True)
        gv = m(g["halfV"]) + m(g["width"]) / 2
        prims.beam(body, (hu, gv - 0.02, gb + 0.1), (hu, hc.y, gb + 0.1), 0.05, 0.05, st["steel"])
        prims.cylinder(body, (hu, hc.y, gb + 0.1), hc + W * hr, m(hp["rod"]) / 2, 6, st["steel"], caps=(False, False))

    a.body = body
    rope_sockets(a, hg, rope, max(ends), spec["sockets"], load=True)
    a.dims.update({"rope": d["rope"], "deck": round(top * 1000), "houseTop": round((top + 0.04 + m(d["house"]["height"])) * 1000),
                   "girderEnd": g["uTo"], "trains": [tr["leave"], tr["arrive"]]})
    return a


# -- the top station ------------------------------------------------------------------------------------------------
def ret(spec, lod):
    c, r = spec["common"], spec["return"]
    st = styles()
    a = Asset("monta_tbar_return")
    body = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    rope = m(r["rope"])
    g = r["girders"]

    # the pad, the hinge block, the tapered leaning column (R19, R31) and the plumb box strut
    pd = r["pad"]
    block(body, m(pd["uFrom"]), m(pd["uTo"]), m(pd["halfV"]), m(pd["top"]), m(r["pierBottom"]), st)
    base = m(pd["top"])
    gb = m(g["bottom"])
    col = r["column"]
    foot, head_u = m(col["footU"]), m(col["topU"])
    hu_t, hv = m(col["top"]["halfU"]), m(col["top"]["halfV"])
    hu_f = m(col["foot"]["halfU"])
    hg_ = r["hinge"]
    hh = m(hg_["height"])
    if lod <= 1:   # the base plate and the hinge block with its pin
        prims.box(body, (foot - m(hg_["lengthU"]) / 2 - 0.15, -m(hg_["halfV"]) - 0.15, base), (foot + m(hg_["lengthU"]) / 2 + 0.15, m(hg_["halfV"]) + 0.15, base + 0.03),
                  st["steel"], skip=("-2",))
        prims.box(body, (foot - m(hg_["lengthU"]) / 2, -m(hg_["halfV"]), base + 0.03), (foot + m(hg_["lengthU"]) / 2, m(hg_["halfV"]), base + hh),
                  st["paint"], skip=("-2",))
        pr_ = m(hg_["pinDia"]) / 2
        prims.cylinder(body, (foot, -m(hg_["halfV"]) - 0.04, base + hh / 2), (foot, m(hg_["halfV"]) + 0.04, base + hh / 2), pr_, 10, st["rod"])
    else:
        prims.box(body, (foot - m(hg_["lengthU"]) / 2, -m(hg_["halfV"]), base), (foot + m(hg_["lengthU"]) / 2, m(hg_["halfV"]), base + hh),
                  st["paint"], skip=("-2",))
    foot_pt, head_pt = Vector((foot, 0.0, base + hh)), Vector((head_u, 0.0, gb))
    axis = head_pt - foot_pt
    lean = math.atan2(axis.x, axis.z)
    ad = axis.normalized()
    n = Vector((math.cos(lean), 0.0, -math.sin(lean)))   # the column's line face normal
    taper = [(0.0, -hu_f), (0.0, hu_f), (axis.length, hu_t), (axis.length, -hu_t)]
    prims.prism(body, taper, foot_pt - V * hv, ad, n, V, 2 * hv, st["paint"], caps=(False, False))
    sr = r["strut"]
    prims.box(body, (m(sr["u"]) - m(sr["halfU"]), -m(sr["halfV"]), base), (m(sr["u"]) + m(sr["halfU"]), m(sr["halfV"]), gb), st["paint"],
              skip=("-2", "+2"))

    # the head frame: bare girders from the wheel's shaft to past the trains, cross girders
    u_from, u_to = m(g["uFrom"]), m(g["uTo"])
    top = girders(body, g, u_from, u_to, lod, st)
    cross(body, g, head_u, hu_t, lod, st)
    if lod <= 2:
        cross(body, g, m(sr["u"]), 0.12, lod, st)
        for u in (u_from + 0.1, 0.35, u_to - 0.1):
            cross(body, g, u, 0.1, lod, st)
    hub_top = wheel(a, body, c, rope, lod, st)
    if lod <= 2:   # the shaft up into the bearing housing at the girders' back end (the tensioning hidden inside, D2)
        sh = m(c["bullwheel"]["shaftDia"]) / 2
        prims.cylinder(body, (0, 0, hub_top - 0.02), (0, 0, gb - 0.15), sh, parts.sides(lod, 16, 10, 8), st["paint"], caps=(False, False))
        bh = r["bearing"]
        prims.box(body, (-m(bh["halfU"]), -m(bh["halfV"]), gb - 0.15), (m(bh["halfU"]), m(bh["halfV"]), top + m(bh["above"])), st["paint"],
                  skip=("-2",) if lod else ())
    # two separate hoop-railed platforms (R16): over the trains, and round the bearing beside the wheel
    pf = r["platforms"]
    hv_w = m(g["halfV"]) + m(g["width"]) / 2
    rail, t_rail = m(r["rail"]), top + 0.04
    for key, (u0, u1) in (("trains", pf["trains"]), ("wheel", pf["wheel"])):
        u0, u1 = m(u0), m(u1)
        prims.box(body, (u0, -hv_w, top), (u1, hv_w, top + 0.04), st["grating"], skip=("-2",) if lod >= 2 else ())
        if lod <= 2:
            rv = hv_w - 0.03
            loop = [(u0 + 0.03, -rv, t_rail), (u1 - 0.03, -rv, t_rail), (u1 - 0.03, rv, t_rail), (u0 + 0.03, rv, t_rail)]
            if key == "trains":   # its back side open at the ladder's head
                gap = m(r["ladderWidth"]) / 2 + 0.15
                parts.railing_hoops(body, loop + [(u0 + 0.03, gap, t_rail)], rail, lod, st["steel"])
                parts.railing_hoops(body, [(u0 + 0.03, -gap, t_rail), (u0 + 0.03, -rv, t_rail)], rail, lod, st["steel"])
            else:
                parts.railing_hoops(body, loop + [(u0 + 0.03, -rv, t_rail)], rail, lod, st["steel"])

    # the trains: 8 sheaves on both ropes
    T = parts.metres(spec["train"])
    s = parts.metres(c["sheave"])
    w_pin = rope + T["eqDrop"] + T["rockerDrop"] + s["grooveBottom"] / 2 + c["ropeDiameter"] / 2000.0
    tr = r["trains"]
    ends = trains(body, a, spec, rope, w_pin, [("l", -1, tr["arrive"], tr["u"]), ("r", 1, tr["leave"], tr["u"])], g, lod, st)

    # the galvanised ladder up the column's line face to the trains' platform (R20)
    if lod <= 2:
        parts.ladder(body, foot_pt + n * (hu_f + 0.1), Vector((head_u, 0.0, top + 0.04)) + n * (hu_t + 0.1), m(r["ladderWidth"]), lod,
                     st["steel"], handrails=False, rung_style=st["steel"])

    # the box-section gallows for the wire (D9, R17): two legs leaning back over the wheel, a cross strut, braces back
    # to the girders, the hook at its head
    af = r["aFrame"]
    w0 = top + 0.04
    h_af = m(af["height"])
    apex = Vector((m(af["u"]) - h_af * math.tan(math.radians(af["lean"])), 0.0, w0 + h_af))
    if lod <= 2:
        sz = m(af["size"])
        feet, tips = [], []
        for s_ in (-1, 1):
            p0 = Vector((m(af["u"]), s_ * m(af["footHalf"]), w0))
            tip = apex + V * s_ * m(af["topHalf"])
            prims.beam(body, p0, tip, sz, sz, st["steel"], up=U)
            feet.append(p0)
            tips.append(tip)
        prims.beam(body, tips[0] - V * sz / 2, tips[1] + V * sz / 2, sz, sz, st["steel"], up=U)
        if lod <= 1:
            k = af["strutAt"]
            prims.beam(body, feet[0] + (tips[0] - feet[0]) * k, feet[1] + (tips[1] - feet[1]) * k, m(af["strut"]), m(af["strut"]), st["steel"],
                       up=U, skip_ends=True)
            for s_, tip in zip((-1, 1), tips):
                prims.cylinder(body, tip - W * 0.1, Vector((m(af["braceU"]), s_ * m(af["footHalf"]), w0)), m(af["braceDia"]) / 2,
                               parts.sides(lod, 8, 5), st["steel"], caps=(False, False))
        hook(body, a, apex + W * sz / 2, lod, st)
    else:
        a.sockets["wire"] = tuple(apex + W * 0.15)

    # the guard rail round the wheel's back half at rope level and along the outside of both trains (R18)
    gd = r["guard"]
    if lod <= 2:
        w_g = rope + m(gd["above"])
        R_g = hg + m(gd["out"])
        u_end = max(ends)
        arc = [Vector((R_g * math.cos(math.radians(th)), R_g * math.sin(math.radians(th)), w_g)) for th in range(90, 271, 15 if lod == 0 else 30)]
        path = [Vector((u_end, R_g, w_g))] + arc + [Vector((u_end, -R_g, w_g))]
        prims.tube_path(body, path, m(gd["dia"]) / 2, parts.sides(lod, 8, 5, 4), st["steel"], caps=True)
        if lod <= 1:   # brackets from the girders out to the guard
            br = m(gd["bracketDia"]) / 2
            u = 0.3
            while u < u_end - 0.3:
                for sv in (-1, 1):
                    prims.cylinder(body, (u, sv * hv_w, gb + 0.02), (u, sv * R_g, w_g), br, 6, st["steel"], caps=(False, False))
                u += m(gd["bracketEvery"])
            for th in (135, 180, 225):
                p = Vector((R_g * math.cos(math.radians(th)), R_g * math.sin(math.radians(th)), w_g))
                prims.cylinder(body, (u_from + 0.1, p.y * 0.5, gb + 0.02), p, br, 6, st["steel"], caps=(False, False))

    a.body = body
    rope_sockets(a, hg, rope, max(ends), spec["sockets"], load=False)
    a.dims.update({"rope": r["rope"], "deck": round(top * 1000), "columnLeanDeg": round(math.degrees(lean), 1),
                   "girderEnd": g["uTo"], "trains": [tr["arrive"], tr["leave"]]})
    return a


# -- the operator hut -----------------------------------------------------------------------------------------------
def hut(spec, lod):
    h = spec["hut"]
    st = styles()
    a = Asset("monta_tbar_hut")
    mb = MeshBuilder()
    hx, hy = m(h["halfU"]), m(h["halfV"])
    sk, wall = m(h["skid"]), m(h["wall"])
    lo, hi = m(h["roofLow"]), m(h["roofHigh"])
    ov = m(h["overhang"])
    if lod <= 2:   # the skids
        for s in (-1, 1):
            prims.box(mb, (-hx - 0.05, s * (hy - 0.15) - 0.06, 0.0), (hx + 0.05, s * (hy - 0.15) + 0.06, sk), st["dark"], skip=("-2",))
    # the walls: a box up to the roof's low edge, a wedge up to its high edge (the roof falls toward +u)
    prims.box(mb, (-hx, -hy, sk), (hx, hy, lo if lod <= 2 else (lo + hi) / 2), st["hut"], skip=("-2", "+2"))
    if lod <= 2:
        prims.prism(mb, [(-hx, lo), (hx, lo), (-hx, hi)], (0.0, -hy, 0.0), U, W, V, 2 * hy, st["hut"], caps=(True, True))
    # the roof slab, overhanging
    t = m(h["roofThick"])
    slope = (hi - lo) / (2 * hx)
    p0, p1 = Vector((-hx - ov, 0.0, hi + slope * ov)), Vector((hx + ov, 0.0, lo - slope * ov))
    prims.beam(mb, p0 + W * t / 2, p1 + W * t / 2, 2 * (hy + ov), t, st["white"].but(snow=1.0))
    if lod >= 2:
        a.body = mb
        a.sockets = {"door": (-hx, 0.0, sk)}
        return a
    # the door on the -u face, windows on the -u face and both sides, white frames
    dr = h["door"]
    dv0, dv1 = m(dr["v"]) - m(dr["width"]) / 2, m(dr["v"]) + m(dr["width"]) / 2
    prims.box(mb, (-hx - 0.02, dv0, sk + 0.02), (-hx, dv1, sk + m(dr["height"])), st["white"], skip=("+0",))
    win = h["window"]
    wins = [((-hx, -1), m(win["v"])), ((-hy, -1), 0.0), ((hy, 1), 0.0)]
    ww, w0, w1 = m(win["width"]) / 2, sk + m(win["from"]), sk + m(win["to"])
    for (pos, s), c in wins:
        if pos == -hx:   # on the front, beside the door
            prims.box(mb, (-hx - 0.03, c - ww - 0.04, w0 - 0.04), (-hx, c + ww + 0.04, w1 + 0.04), st["white"], skip=("+0",))
            prims.box(mb, (-hx - 0.035, c - ww, w0), (-hx - 0.03, c + ww, w1), st["dark"], skip=("+0",))
        else:            # on a side: the frame standing out of the wall, the pane on it
            f0, f1 = sorted((pos, pos + s * 0.03))
            g0, g1 = sorted((pos + s * 0.03, pos + s * 0.035))
            prims.box(mb, (c - ww - 0.04, f0, w0 - 0.04), (c + ww + 0.04, f1, w1 + 0.04), st["white"], skip=("-1",) if s > 0 else ("+1",))
            prims.box(mb, (c - ww, g0, w0), (c + ww, g1, w1), st["dark"], skip=("-1",) if s > 0 else ("+1",))
    if lod == 0:   # corner trim
        for su in (-1, 1):
            for sv in (-1, 1):
                prims.box(mb, (su * hx - 0.03, sv * hy - 0.03, sk), (su * hx + 0.03, sv * hy + 0.03, lo if su > 0 else hi - 0.02),
                          st["white"], skip=("-2",))
    a.body = mb
    a.sockets = {"door": (-hx - 0.02, m(dr["v"]), sk)}
    a.dims.update({"footprint": [2 * h["halfU"], 2 * h["halfV"]], "height": h["roofHigh"]})
    return a
