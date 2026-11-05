"""Glidepath magic carpet stations (glidepath_carpet.json "top" and "bottom", the double's overlay glidepath_carpet2.json;
owner C1-C11, 2026-11-04). Carpets drive at the top and tension at the bottom (C4, the exception to D1 for carpets).

  top     the drive station: the drum under the comb (hidden), the motor and gearbox on the drum's outer end under a
          livery cover (C8), the ribbed exit plate past the comb with two round marker lights and a hatch, steel side
          panels with a white top edge and the type plate, the double light barrier on the decks' edges just before
          the comb, and the control cabinet standing beside the station.
  bottom  the tension station: the deflection roller under the comb (hidden), the hydraulic tension unit under a
          cover on the outer side, the entrance plate, padded livery bumpers at the entry corners (and between a
          double's belts).

Both are as wide as the belt modules and end at "joint" with the deck section of a module (carpet.deck_section, belt
"belt" scrolling as on the modules) and its joint covers lapping onto the first module; socket belt_in (top) or
belt_out (bottom) at the belt top there, where the first module's front or back socket goes. The origin is at grade
under the drum's or roller's axis, u toward the line (downhill at the top, uphill at the bottom); the drive pit and the
side panels reach below grade ("pit"), as the stations stand in a snow pit.

Each station ends at its plate, closed by an end panel (owner C13, Gate 1 round 2): riders ski on and off over snow,
not a hard ramp. The game grades the earth approach the spec's "approach" gives (dims "approach": level with the plate
top for "flat", the top's "runOff" falling "runOffPct" first, then "easePct" down to natural ground, "margin" wider
than the deck each side) and snow covers it; the control post's socket is at the plate top, on that approach.
"""
import math

import carpet
from carpet import FACES, layout, m, side_faces, slab
from liftkit import prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "carpet_station"
LODS = 4
SPEC = "glidepath_carpet.json"


def kind_of(variant):
    return KIND


def build(spec, lod, stage, variant):
    return station(spec, lod, variant)


def styles(spec):
    st = carpet.styles(spec)
    st.update({"panel": Style("galvanised", snow=0.3), "cover": Style("livery", livery=1.0, snow=0.8),
               "unit": Style("paint_grey", snow=0.8), "louvre": Style("paint_dark"), "plate": Style("aluminium"),
               "cabinet": Style("paint_grey", snow=0.6), "light": Style("white", trim="plain"),
               "lens": Style("glass_black")})
    return st


def sides_of(belts, inward=False):
    """Each belt's outer side (+1: +v): a single's motor goes on +v and its tension unit on -v (inward=True), a
    double's both on each belt's own outer side."""
    if len(belts) == 1:
        return [-1 if inward else 1]
    return [1 if c > 0 else -1 for c in belts]


def edge_posts(spec):
    """v of the posts along the decks: both outer edges and the middle of each walking deck between two belts."""
    belts, out, decks = layout(spec)
    return [-(out - 0.06)] + [(v0 + v1) / 2 for v0, v1 in decks[1:-1]] + [out - 0.06]


def rounded_box(mb, u0, u1, vc, hv, w0, w1, rc, lod, st):
    """A box along u with its top edges rounded (radius rc, steps by LOD), centred at v = vc."""
    steps = (3, 1, 0, 0)[lod]
    if not steps:
        slab(mb, u0, u1, vc - hv, vc + hv, w0, w1, st, ("top", "vmin", "vmax", "umin", "umax"))
        return
    prof = [(-hv, w0), (hv, w0)]
    for k in range(steps + 1):
        a = math.pi / 2 * k / steps
        prof.append((hv - rc + rc * math.cos(a), w1 - rc + rc * math.sin(a)))
    for k in range(steps + 1):
        a = math.pi / 2 + math.pi / 2 * k / steps
        prof.append((-hv + rc + rc * math.cos(a), w1 - rc + rc * math.sin(a)))
    prims.prism(mb, [(v + vc, w) for v, w in prof], (u0, 0.0, 0.0), V, W, U, u1 - u0, st)


def padded(mb, u, v, hu, hv, w0, w1, lod, st):
    """A padded block standing at (u, v), its vertical edges rounded."""
    if lod >= 2:
        slab(mb, u - hu, u + hu, v - hv, v + hv, w0, w1, st, ("top", "vmin", "vmax", "umin", "umax"))
        return
    r = min(hu, hv) * 0.5
    steps = 2 if lod == 0 else 1
    poly = []
    for cu, cv, a0 in ((hu - r, hv - r, 0.0), (-hu + r, hv - r, 90.0), (-hu + r, -hv + r, 180.0), (hu - r, -hv + r, 270.0)):
        for k in range(steps + 1):
            a = math.radians(a0 + 90.0 * k / steps)
            poly.append((cu + r * math.cos(a), cv + r * math.sin(a)))
    prims.prism(mb, poly, (u, v, w0), U, V, W, w1 - w0, st, caps=(False, True))


def station(spec, lod, end):
    st = styles(spec)
    S, s = spec[end], spec["section"]
    a = Asset(f"{spec['idPrefix']}_{end}")
    mb = MeshBuilder()
    belts, out, decks = layout(spec)
    edge = out - m(s["deckOut"])
    comb, joint, pf, ptop = m(S["comb"]), m(S["joint"]), m(S["plateFrom"]), m(S["plateTop"])
    pit = m(S["pit"])
    top, dt, du = m(s["beltTop"]), m(s["deckTop"]), m(s["deckUnder"])
    bh = m(s["beltHalf"])
    # the approach: a module's deck from the comb to the joint, the belt from under the comb; the joint covers
    carpet.deck_section(mb, spec, comb, joint, lod, st, belt_from=comb - 0.12)
    carpet.joint_covers(mb, spec, joint, lod, st)
    # the plate over the drum or roller, falling to the belt at the comb; the end panel under its front edge
    wedge = 0.06
    slab(mb, pf, comb - wedge, -out, out, du, ptop, st["deck"], ("top",))
    slab(mb, pf, comb - wedge, -out, out, du, ptop, st["skirt"], ("vmin", "vmax"))
    prims.prism(mb, [(comb - wedge, ptop), (comb - wedge, du), (comb, du), (comb, dt)], (0.0, -out, 0.0), U, W, V, 2 * out,
                st["skirt"], side_style=st["deck"])
    slab(mb, pf, pf + 0.006, -out, out, pit, ptop, st["panel"], ("umin",))
    if lod <= 1:   # the comb across each belt
        for c in belts:
            slab(mb, comb - 0.025, comb + 0.004, c - bh + 0.01, c + bh - 0.01, top + 0.001, dt + 0.005, st["joint"],
                 ("top", "umax", "vmin", "vmax"))
    # the side panels under the plate, down into the pit, flush with the modules' skirts; the white top edge
    for sg in (-1, 1):
        outer, inner = side_faces(sg)
        slab(mb, pf, comb, sg * (edge + m(s["skirtIn"])), sg * (edge + m(s["skirtOut"])), pit, du, st["panel"], (outer,))
        if lod <= 2:
            slab(mb, pf, joint, sg * (out - 0.03), sg * (out + 0.006), dt, ptop + 0.008, st["strip"],
                 ("top", outer, inner, "umin", "umax") if lod <= 1 else ("top", outer, inner))
    if lod <= 1:   # the marker lights on the plate, LOD0 the hatch; the type plate on the +v panel
        for c in belts:
            for dv in (-(bh - 0.14), bh - 0.14):
                prims.cylinder(mb, (comb - 0.2, c + dv, ptop - 0.002), (comb - 0.2, c + dv, ptop + 0.012), 0.035,
                               8 if lod == 0 else 6, st["light"], caps=(False, True))
            if lod == 0:
                h0, h1, hv = pf + 0.2, comb - 0.4, 0.28
                for u0, u1, v0, v1 in ((h0, h1, c - hv, c - hv + 0.008), (h0, h1, c + hv - 0.008, c + hv),
                                       (h0, h0 + 0.008, c - hv, c + hv), (h1 - 0.008, h1, c - hv, c + hv)):
                    slab(mb, u0, u1, v0, v1, ptop + 0.0006, ptop + 0.0006, st["joint"], ("top",))
                slab(mb, h1 - 0.09, h1 - 0.03, c - 0.08, c + 0.08, ptop, ptop + 0.012, st["bolt"],
                     ("top", "umin", "umax", "vmin", "vmax"))
        so = edge + m(s["skirtOut"])
        tu = pf + 0.25
        slab(mb, tu, tu + 0.32, so, so + 0.003, 0.12, 0.27, st["plate"], ("vmax", "top", "bottom", "umin", "umax"))
        if lod == 0:
            for k in range(3):
                w = 0.24 - k * 0.04
                slab(mb, tu + 0.03, tu + 0.29 - k * 0.05, so + 0.003, so + 0.004, w - 0.012, w, st["black"],
                     ("vmax", "top", "bottom", "umin", "umax"))
    if end == "top":
        top_parts(mb, spec, S, lod, st, belts, out, dt, ptop)
    else:
        bottom_parts(mb, spec, S, lod, st, belts, out, dt, ptop)
    a.body = mb
    rider = "rider_unload" if end == "top" else "rider_load"
    at = pf + (0.4 if end == "top" else 0.35)
    a.sockets = {("belt_in" if end == "top" else "belt_out"): (joint, 0.0, top)}
    for i, c in enumerate(belts):
        a.sockets[rider + ("" if len(belts) == 1 else ("_l", "_r")[i])] = (at, c, ptop)
    a.sockets["post"] = (pf - 0.3, -(out + 0.5), ptop)   # on the approach, level with the plate
    a.pivots = {"belt": {"pos": ((comb + joint) / 2, 0.0, top), "axis": (1.0, 0.0, 0.0), "fixed": True}}
    carpet.line_dims(spec, a, joint - comb)
    a.dims.update({"end": end, "drumDia": S["drumDia"], "comb": S["comb"], "joint": S["joint"], "plateFrom": S["plateFrom"],
                   "plateTop": S["plateTop"], "pit": S["pit"], "approach": {k: v for k, v in S["approach"].items()
                                                                             if not k.startswith("_")},
                   "drive": end == "top"})
    del a.dims["module"]
    return a


def top_parts(mb, spec, S, lod, st, belts, out, dt, ptop):
    """The drive station's motor covers, light barrier and control cabinet."""
    mo = S["motor"]
    cu, cv, ch = (m(x) for x in mo["cover"])
    for c, sg in zip(belts, sides_of(belts)):   # the motor and gearbox on the drum's outer end, under its cover
        vc = sg * (out + 0.02 + cv / 2)
        rounded_box(mb, -cu / 2, cu / 2, vc, cv / 2, -0.05, ch, 0.12, lod, st["cover"])
        if lod == 0:   # louvres on its outer face
            vo = vc + sg * cv / 2
            for k in range(4):
                w = 0.12 + k * 0.07
                slab(mb, -cu * 0.3, cu * 0.3, vo, vo + sg * 0.012, w, w + 0.03, st["louvre"],
                     ("vmax" if sg > 0 else "vmin", "top", "bottom", "umin", "umax"))
    ba = S["barrier"]
    bu, bh = m(ba["u"]) + m(S["comb"]), m(ba["height"])
    if lod <= 2:   # the double light barrier: a post on each deck edge (and between a double's belts), two sensors
        for v in edge_posts(spec):
            slab(mb, bu - 0.025, bu + 0.025, v - 0.025, v + 0.025, dt, dt + bh, st["frame"],
                 ("umin", "umax", "vmin", "vmax", "top"))
            if lod <= 1:
                slab(mb, bu - 0.045, bu + 0.045, v - 0.04, v + 0.04, dt + bh - 0.14, dt + bh + 0.02, st["cover"], FACES)
                for w in (dt + 0.3, dt + 0.55):
                    slab(mb, bu - 0.035, bu + 0.035, v - 0.035, v + 0.035, w - 0.04, w + 0.04, st["frame"], FACES)
                    if lod == 0:
                        for sg in ((1,) if v < -0.1 else (-1,) if v > 0.1 else (-1, 1)):
                            vl = v + sg * 0.035
                            prims.cylinder(mb, (bu, vl, w), (bu, vl + sg * 0.006, w), 0.016, 8, st["lens"], caps=(False, True))
    cab = S["cabinet"]
    au, av = m(cab["u"]), m(cab["v"])
    xu, xv, xh = m(cab["size"][0]) / 2, m(cab["size"][1]) / 2, m(cab["size"][2])
    sw = m(cab["stand"])
    face = "vmax" if av < 0 else "vmin"   # its door faces the carpet
    sg = 1 if av < 0 else -1
    if lod <= 2:   # the stand's two legs
        for du_ in (-xu + 0.05, xu - 0.05):
            slab(mb, au + du_ - 0.03, au + du_ + 0.03, av - 0.03, av + 0.03, 0.0, sw, st["foot"], ("umin", "umax", "vmin", "vmax"))
    slab(mb, au - xu, au + xu, av - xv, av + xv, sw, sw + xh, st["cabinet"], FACES if lod <= 1 else ("top", "umin", "umax", "vmin", "vmax"))
    if lod <= 1:   # a little roof, the door's seam and handle, the stop button
        slab(mb, au - xu - 0.03, au + xu + 0.03, av - xv - 0.04, av + xv + 0.04, sw + xh, sw + xh + 0.025, st["frame"], FACES)
        fv = av + sg * xv
        slab(mb, au - 0.004, au + 0.004, fv, fv + sg * 0.002, sw + 0.05, sw + xh - 0.05, st["louvre"], (face,))
        prims.cylinder(mb, (au + xu * 0.55, fv, sw + xh * 0.7), (au + xu * 0.55, fv + sg * 0.03, sw + xh * 0.7), 0.03, 8, st["red"])
        if lod == 0:
            slab(mb, au + 0.03, au + 0.05, fv, fv + sg * 0.025, sw + xh * 0.45, sw + xh * 0.55, st["bolt"],
                 (face, "top", "bottom", "umin", "umax"))



def bottom_parts(mb, spec, S, lod, st, belts, out, dt, ptop):
    """The tension station's tension-unit covers and padded bumpers."""
    te = S["tension"]
    tu, tv, th = (m(x) for x in te["size"])
    for c, sg in zip(belts, sides_of(belts, inward=True)):
        vc = sg * (out + 0.02 + tv / 2)
        u0 = -0.4
        rounded_box(mb, u0, u0 + tu, vc, tv / 2, -0.05, th, 0.08, lod, st["unit"])
        if lod == 0:
            vo = vc + sg * tv / 2
            for k in range(5):
                u = u0 + 0.2 + k * 0.16
                slab(mb, u, u + 0.06, vo, vo + sg * 0.01, 0.1, th - 0.1, st["louvre"],
                     ("vmax" if sg > 0 else "vmin", "top", "bottom", "umin", "umax"))
    bp = S["bumper"]
    hu, hv, bhgt = m(bp["size"][0]) / 2, m(bp["size"][1]) / 2, m(bp["size"][2])
    u = m(bp["u"]) + hu
    if lod <= 2:
        for sg in (-1, 1):   # at the entry corners, standing on the snow beside the plate
            padded(mb, u, sg * (out + hv + 0.01), hu, hv, 0.0, bhgt, lod, st["pad"])
        _, _, decks = layout(spec)
        for v0, v1 in decks[1:-1]:   # between a double's belts, on the plate
            padded(mb, u, (v0 + v1) / 2, hu, min(hv, (v1 - v0) / 2 - 0.02), ptop, ptop + bhgt, lod, st["pad"])
