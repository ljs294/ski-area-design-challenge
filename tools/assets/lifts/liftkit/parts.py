"""Lift parts built from primitives, in the lift frame. Detail follows an LOD level (0 = nearest)."""
import json
import math
import os

from mathutils import Vector

from . import prims
from .mesh import MeshBuilder, Style
from .prims import U, V, W, vec


def sides(lod, near, *far):
    """Polygon sides for a round part at this LOD: near at LOD0, then the listed values."""
    seq = (near,) + far
    return seq[min(lod, len(seq) - 1)]


def sheave(mb, center, axis, dia, width, lod, style_wheel, style_liner, spokes=True, n=None):
    """A grooved rope sheave: rim with a rubber liner, a web and a hub. Axis through center. n: sides (default
    by LOD); line towers use fewer, since a tower carries up to 16."""
    r = dia / 2
    n = n or sides(lod, 24, 12, 8, 6)
    ax = vec(axis).normalized()
    c = vec(center)
    hw = width / 2
    if lod >= 2:
        prims.cylinder(mb, c - ax * hw, c + ax * hw, r, n, style_wheel)
        return
    # rim (flanges) and liner groove, as one lathe section: (radius, height along axis)
    groove = 0.35 * width
    rim = [(r * 0.88, -hw), (r + 0.012, -hw), (r + 0.012, -groove), (r - 0.02, -groove * 0.5),
           (r - 0.02, groove * 0.5), (r + 0.012, groove), (r + 0.012, hw), (r * 0.88, hw)]
    prims.lathe(mb, c, ax, rim, n, style_wheel.but(cls="sheave_rim"))   # light rims, red faces (photo)
    if lod == 0:
        prims.lathe(mb, c, ax, [(r - 0.02, -groove * 0.5), (r - 0.005, -groove * 0.3), (r - 0.005, groove * 0.3), (r - 0.02, groove * 0.5)],
                    n, style_liner, closed=False)
    # web and hub
    # web: red faces nearly flush with the light rim, as photographed
    prims.cylinder(mb, c - ax * (hw - 0.012), c + ax * (hw - 0.012), r * 0.88, n, style_wheel, caps=(True, True))
    prims.cylinder(mb, c - ax * (hw + 0.02), c + ax * (hw + 0.02), r * 0.22, sides(lod, 12, 8), style_wheel.but(cls="machined"))


def sheave_bracket(mb, centre, width, from_w, lod, style):
    """A plate on the inboard side of a rope sheave (toward v = 0), from from_w to just past the axle, so grips
    pass freely on the outboard side."""
    c = vec(centre)
    inboard = -1.0 if c.y > 0 else 1.0
    pv = c.y + inboard * (width / 2 + 0.03)
    lo_w, hi_w = sorted((from_w, c.z + (0.06 if from_w < c.z else -0.06)))
    prims.box(mb, (c.x - 0.09, pv - 0.015, lo_w), (c.x + 0.09, pv + 0.015, hi_w), style)
    if lod <= 1:   # stub axle boss
        prims.cylinder(mb, (c.x, pv, c.z), (c.x, c.y + inboard * (width / 2), c.z), 0.045, sides(lod, 10, 6), style.but(cls="machined"))


def sheave_train(mb, asset, name, u_c, v_rope, rope_w, rope_r, dia, width, train, mode, attach, lod, st, first=2, plate=None):
    """A balanced sheave train on one rope: sheaves in pairs on rockers, the rockers on a main beam pivoted on a
    hanger that runs to the structure at attach = (v, w). mode "hold": sheaves above the rope (rope under them);
    "support": below it (rope on top). Rockers and hanger sit inboard of the sheaves, so grips pass outboard.
    plate: instead of that hanger, a plate bolted to a structure face, {"v": (from, to) unsigned, "top": w,
    "width": along u, "pin": dia} in metres, with the beam's pin through it.
    Sheaves are moving parts on their axles at LOD0-1 (sheave_<name><first>...), merged into the body at LOD2;
    LOD3 keeps a box for the whole train. train: n, pitch, rocker, beam (metres)."""
    r = dia / 2
    up = 1.0 if mode == "hold" else -1.0          # from the axles toward the rockers and beam
    wz = rope_w + up * (rope_r + r)               # axle height
    inboard = -1.0 if v_rope > 0 else 1.0
    pv = v_rope + inboard * (width / 2 + 0.03)    # side-plate line
    n, pitch = train["n"], train["pitch"]
    offs = [(k - (n - 1) / 2) * pitch for k in range(n)]
    w_rock, w_beam = wz + up * train["rocker"], wz + up * train["beam"]
    if lod >= 3:
        va, vb = sorted((pv, v_rope - inboard * width / 2))
        prims.box(mb, (u_c + offs[0] - r, va, min(wz - r, w_beam)), (u_c + offs[-1] + r, vb, max(wz + r, w_beam)), st["steel"])
        return
    for k, du in enumerate(offs):
        centre = Vector((u_c + du, v_rope, wz))
        target = MeshBuilder() if lod <= 1 else mb
        sheave(target, centre, V, dia, width, lod, row_face(k, n, st), st["rubber"])   # red ends, galvanised middle
        if lod <= 1:
            key = f"sheave_{name}{first + k}"
            asset.parts[key] = target
            asset.pivots[key] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}
            if lod == 0:   # axle boss from the rocker plate
                prims.cylinder(mb, (centre.x, pv, wz), (centre.x, v_rope + inboard * width / 2, wz), 0.035, 8, st["rod"])
    plate_t, bar = 0.02, 0.06
    v0, v1 = sorted((pv - plate_t / 2, pv + plate_t / 2))
    pins = []
    for i in range(0, n - 1, 2):   # rockers: a slim bar through each pair's axles, a lug up (or down) to the rocker pin
        a_u, b_u = u_c + offs[i] - 0.07, u_c + offs[i + 1] + 0.07
        prims.box(mb, (a_u, v0, wz - bar), (b_u, v1, wz + bar), st["steel"])
        rc = u_c + (offs[i] + offs[i + 1]) / 2
        if lod <= 1:
            lo_, hi_ = sorted((wz + up * bar, w_rock + up * 0.04))
            prims.box(mb, (rc - 0.06, v0, lo_), (rc + 0.06, v1, hi_), st["steel"])
        pins.append((rc, w_rock))
    half = pitch * (n // 2) / 2 + 0.1   # main beam between the rocker pins, against the rockers
    b0, b1 = sorted((pv + inboard * plate_t / 2, pv + inboard * (plate_t / 2 + 0.1)))
    beam_c = (w_rock + w_beam) / 2
    prims.box(mb, (u_c - half, b0, beam_c - 0.08), (u_c + half, b1, beam_c + 0.08), st["steel"])
    if lod == 0:   # rocker pins through the beam and rockers
        for pu, pw in pins:
            prims.cylinder(mb, (pu, min(b0, v0) - 0.01, pw), (pu, max(b1, v1) + 0.01, pw), 0.03, 8, st["rod"])
    if plate:   # plate on the structure face, down past the beam, with the beam's pin through both
        side = -inboard
        p0, p1 = sorted((side * plate["v"][0], side * plate["v"][1]))
        hw = plate["width"] / 2
        prims.box(mb, (u_c - hw, p0, w_beam - 0.05), (u_c + hw, p1, plate["top"]), st["steel"])
        if lod <= 1:
            q0, q1 = min(b0, p0), max(b1, p1)
            prims.cylinder(mb, (u_c, q0 - 0.01, w_beam), (u_c, q1 + 0.01, w_beam), plate["pin"] / 2, sides(lod, 10, 6), st["rod"])
        return
    av, aw = attach   # hanger from the beam's centre pin to the structure
    h0, h1 = min(b0, av), max(b1, av)
    prims.box(mb, (u_c - 0.08, h0, min(w_beam, aw)), (u_c + 0.08, h1, max(w_beam, aw)), st["steel"])


def metres(x, keep=("sides",)):
    """A spec subtree in millimetres, as metres (counts under `keep` stay as they are)."""
    if isinstance(x, dict):
        return {k: (v if k in keep else metres(v, keep)) for k, v in x.items()}
    if isinstance(x, list):
        return [metres(v, keep) for v in x]
    return x / 1000.0 if isinstance(x, (int, float)) else x


def line_spec(spec):
    """The line assembly's spec ("tower.assembly") in metres, with the rope's radius."""
    a = metres(spec["tower"]["assembly"])
    a["ropeR"] = spec["common"]["ropeDiameter"] / 2000.0
    return a


def row_face(k, count, st):
    """Face style of sheave k (0-based) in a row of `count`: the first and last are red (the owner's rule: they carry
    the lightning-protection grounding), the rest galvanised."""
    return st["red"] if k in (0, count - 1) else st["red"].but(cls="galvanised")


def line_sheave(mb, center, axis, s, lod, st, n, face):
    """A line-tower sheave (spec "tower.assembly.sheave", metres): flange rims at the full diameter (light), the
    side faces down to the recess and the recessed web in `face` (red or galvanised, see row_face), a rounded rope
    groove with its rubber liner, and the axle hub. Axis through center; n polygon sides."""
    c, ax = vec(center), vec(axis).normalized()
    hw, R = s["width"] / 2, s["dia"] / 2
    f, rr, rw, rg = s["flange"], s["recess"] / 2, s["grooveTop"] / 2, s["grooveBottom"] / 2
    rim = face.but(cls="sheave_rim")
    if lod >= 2:
        prims.cylinder(mb, c - ax * hw, c + ax * hw, R, n, face)
        return
    if lod == 1:   # flanges and a V groove in one ring, the web as a plain drum
        prims.lathe(mb, c, ax, [(rw, -hw), (R, -hw), (R, -hw + f), (rg, 0.0), (R, hw - f), (R, hw), (rw, hw)], n, rim)
        prims.cylinder(mb, c - ax * hw, c + ax * hw, rw, n, face)
        return
    for sgn in (-1, 1):   # flange rings (light): side face, rim, inner face; the face ring and the recess wall
        prims.lathe(mb, c, ax, [(rw, sgn * hw), (R, sgn * hw), (R, sgn * (hw - f)), (rw, sgn * (hw - f))], n, rim, closed=False)
        prims.lathe(mb, c, ax, [(rr, sgn * hw), (rw, sgn * hw)], n, face, closed=False)
        prims.lathe(mb, c, ax, [(rr, sgn * (hw - s["recessDepth"])), (rr, sgn * hw)], n, face, closed=False)
    g = hw - f   # groove liner between the flanges, rounded to its bottom
    prims.lathe(mb, c, ax, [(rw, -g), (rg + (rw - rg) * 0.4, -g * 0.55), (rg, -g * 0.15), (rg, g * 0.15), (rg + (rw - rg) * 0.4, g * 0.55), (rw, g)],
                n, st["rubber"], closed=False)
    prims.cylinder(mb, c - ax * (hw - s["recessDepth"]), c + ax * (hw - s["recessDepth"]), rr, n, face, caps=(True, True))
    prims.cylinder(mb, c - ax * (hw + 0.012), c + ax * (hw + 0.012), s["hub"] / 2, 10, st["rod"])


def arc_axles(a, count, radius, level="apex"):
    """Axle positions (u, dw) for `count` sheaves on an axle circle of `radius` (metres): pairs on rockers (pitch),
    two rockers to a train (rockerGap), trains or a train and a lone rocker on the main beam (trainGap). dw is the
    distance from the arc's apex (>= 0). level "apex": the apex is midway, at u = 0, as on a line tower, where the
    rope bends equally either side. level "first": the apex is at the first (lowest u) axle, so the rope runs level
    up to the row and bends through all of it, as at the return terminal where the line leaves the station
    climbing; u = 0 is then the middle of the chord."""
    p, g, t = a["pitch"], a["rockerGap"], a["trainGap"]
    chords = {2: [p], 4: [p, g, p], 6: [p, g, p, t, p], 8: [p, g, p, t, p, g, p]}[count]
    angs = [0.0]
    for ch in chords:
        angs.append(angs[-1] + 2 * math.asin(ch / (2 * radius)))
    if level == "first":
        pts = [(radius * math.sin(x), radius * (1 - math.cos(x))) for x in angs]
        mid_u = (pts[0][0] + pts[-1][0]) / 2
        return [(du - mid_u, dw) for du, dw in pts]
    mid = (angs[0] + angs[-1]) / 2
    return [(radius * math.sin(x - mid), radius * (1 - math.cos(x - mid))) for x in angs]


def hold_rope(a, count, radius, u_first, rope_w, steps=16, mode="hold"):
    """The rope along a row levelled at its first sheave (arc_axles level "first"): (u, w) points along the rope's
    circle from the first sheave's contact, where the rope is level at rope_w, to the last sheave's; and the angle
    it leaves at (radians). Under a hold-down row the rope leaves climbing; over a support row it leaves
    descending (mode "support"). u_first is the first axle's u."""
    rho = a["ropeR"] + a["sheave"]["dia"] / 2          # axle to the rope's centreline
    p, g, t = a["pitch"], a["rockerGap"], a["trainGap"]
    chords = {2: [p], 4: [p, g, p], 6: [p, g, p, t, p], 8: [p, g, p, t, p, g, p]}[count]
    theta = sum(2 * math.asin(ch / (2 * radius)) for ch in chords)
    rr = radius + rho                                  # the rope's circle, about the axle circle's centre
    sg = 1.0 if mode == "hold" else -1.0               # the centre is above a hold-down row, below a support row
    return [(u_first + rr * math.sin(theta * k / steps), rope_w + sg * rr * (1 - math.cos(theta * k / steps)))
            for k in range(steps + 1)], theta


def _row(mb, asset, name, u0, v_rope, rope_w, a, count, mode, radius, lod, st, first, parts_lod, outer_yoke, level="apex"):
    """One row of a line assembly: sheaves (moving parts up to parts_lod), rockers (twin plates through both axles,
    pinned at their middle) and train yokes (twin bridge plates, legs down to the rocker pins; only the inboard one
    when outer_yoke is False). Returns what the frame above it needs: axles, rocker pins, train pins with their
    weights, and the (along, toward-structure) frame of the row."""
    s = a["sheave"]
    r = s["dia"] / 2
    sg = -1.0 if mode == "support" else 1.0            # from the rope toward the axles and on to the structure
    out = 1.0 if v_rope > 0 else -1.0
    X = a["x"]

    def span(x0, x1):
        return tuple(sorted((v_rope - out * x0, v_rope - out * x1)))

    def local(p0, p1):   # unit along (p0 -> p1) in the u-w plane and the normal toward the structure
        d = Vector((p1.x - p0.x, 0.0, p1.z - p0.z)).normalized()
        nrm = Vector((-d.z, 0.0, d.x))
        if nrm.z * sg < 0:
            nrm = -nrm
        return d, nrm

    apex = rope_w + sg * (a["ropeR"] + r)
    axles = [Vector((u0 + du, 0.0, apex + sg * dw)) for du, dw in arc_axles(a, count, radius, level)]
    # the far LODs pin at the axles' centroid: the load centre of the rockers and trains up close, so a curved row
    # hangs at the same height at every LOD (the chord between the end axles runs well off a tight arc)
    centroid = sum(axles, Vector()) / len(axles)
    if lod >= 3:   # one block for the row: sheaves, rockers and yokes
        va, vb = span(X["rockerOut"][0], X["yokeIn"][1])
        d, nrm = local(axles[0], axles[-1])
        mid = (axles[0] + axles[-1]) / 2 + nrm * 0.05
        length = (axles[-1] - axles[0]).length + 2 * r
        prims.obox(mb, (mid.x, (va + vb) / 2, mid.z), (d, V, nrm), (length / 2, (vb - va) / 2, r + 0.05), st["steel"])
        pins = [(centroid + nrm * a["yoke"]["pinOffset"], count)]
        return {"axles": axles, "rockers": [axles[0], axles[-1]], "trains": pins, "local": local, "span": span, "sg": sg}
    n_side = sides(lod, *a["sides"])
    for k, c in enumerate(axles):
        centre = Vector((c.x, v_rope, c.z))
        target = MeshBuilder() if lod <= parts_lod else mb
        line_sheave(target, centre, V, s, lod, st, n_side, row_face(k, count, st))
        if lod <= parts_lod:
            key = f"sheave_{name}{first + k}"
            asset.parts[key] = target
            asset.pivots[key] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}
    if lod >= 2:   # far out: one bar for the rockers and yokes
        va, vb = span(X["rockerOut"][0], X["yokeIn"][1])
        d, nrm = local(axles[0], axles[-1])
        mid = (axles[0] + axles[-1]) / 2 + nrm * 0.06
        length = (axles[-1] - axles[0]).length + 0.2
        prims.obox(mb, (mid.x, (va + vb) / 2, mid.z), (d, V, nrm), (length / 2, (vb - va) / 2, 0.08), st["steel"])
        pins = [(centroid + nrm * a["yoke"]["pinOffset"], count)]
        return {"axles": axles, "rockers": [], "trains": pins, "local": local, "span": span, "sg": sg}

    rocker_pins = []
    rk = a["rocker"]
    for i in range(0, count, 2):   # rockers: twin plates against the sheave faces, through both axles
        p0, p1 = axles[i], axles[i + 1]
        d, nrm = local(p0, p1)
        mid = (p0 + p1) / 2
        rocker_pins.append(mid)
        for xr in (X["rockerOut"], X["rockerIn"]):
            va, vb = span(*xr)
            prims.obox(mb, (mid.x, (va + vb) / 2, mid.z), (d, V, nrm), (rk["length"] / 2, (vb - va) / 2, rk["height"] / 2), st["steel"])
        if lod == 0:   # axle ends and the rocker pin
            for p in (p0, p1):
                a0, a1 = span(X["rockerOut"][0] - 0.012, X["rockerIn"][1] + 0.012)
                prims.cylinder(mb, (p.x, a0, p.z), (p.x, a1, p.z), a["axle"] / 2, 8, st["rod"])
            a0, a1 = span((X["yokeOut"][0] if outer_yoke else X["rockerOut"][0]) - 0.012, X["yokeIn"][1] + 0.012)
            prims.cylinder(mb, (mid.x, a0, mid.z), (mid.x, a1, mid.z), a["pin"] / 2, 8, st["rod"])

    yk = a["yoke"]
    trains = []
    for grp in [rocker_pins[i:i + 2] for i in range(0, len(rocker_pins), 2)]:
        lw = yk["leg"] / 2
        if len(grp) == 2:   # bridge between two rocker pins
            q0, q1 = grp
            d, nrm = local(q0, q1)
            mid = (q0 + q1) / 2
            half = (q1 - q0).length / 2
            e, b0, b1, foot = yk["overhang"], yk["bar"][0], yk["bar"][1], -yk["legPast"]
            outline = [(-half - e, b1), (-half - e, foot), (-half + lw, foot), (-half + lw, b0),
                       (half - lw, b0), (half - lw, foot), (half + e, foot), (half + e, b1)]
            weight = 4
        else:               # a lone rocker: one hanger plate up to the beam
            mid = grp[0]
            d, nrm = local(axles[-2], axles[-1])
            outline = [(-lw, yk["bar"][1]), (-lw, -yk["legPast"]), (lw, -yk["legPast"]), (lw, yk["bar"][1])]
            weight = 2
        for xr in ((X["yokeOut"], X["yokeIn"]) if outer_yoke else (X["yokeIn"],)):
            va, vb = span(*xr)
            prims.prism(mb, outline, (mid.x, va, mid.z), d, nrm, V, vb - va, st["steel"])
        trains.append((mid + nrm * yk["pinOffset"], weight))
    return {"axles": axles, "rockers": rocker_pins, "trains": trains, "local": local, "span": span, "sg": sg}


def line_assembly(mb, asset, name, u0, v_rope, rope_w, a, count, mode, lod, st, radius, first=1, parts_lod=0, level="apex"):
    """A line-tower sheave assembly on one rope, after the owner's reference model (spec "tower.assembly", metres):
    a row of sheaves on an arc of `radius` (support: the rope rides on them and the chain hangs below; hold: the
    mirror image, pressing down on the rope), rockers and train yokes (see _row), a cross tube from each yoke's
    middle to the main beam (163 mm square, inboard of the sheaves) that carries two trains or a train and a lone
    rocker and is pinned at the load centre; a single train takes the pin in a short block. The first and last
    sheave are red, the rest galvanised. level: see arc_axles. Returns (u, w) of the main pin."""
    X = a["x"]
    row = _row(mb, asset, name, u0, v_rope, rope_w, a, count, mode, radius, lod, st, first, parts_lod, True, level)
    span, local = row["span"], row["local"]
    carry = row["trains"]
    if lod <= 1:   # cross tubes from the yokes to the beam
        a0, a1 = span(X["tube"][0], X["tube"][1] + 0.01)
        for tp, _ in carry:
            prims.cylinder(mb, (tp.x, a0, tp.z), (tp.x, a1, tp.z), a["tube"] / 2, sides(lod, 10, 6), st["steel"])
    bm = a["beam"]
    va, vb = span(*X["beam"])
    if len(carry) >= 2:
        (p0, w0), (p1, w1) = carry[0], carry[-1]
        d, nrm = local(p0, p1)
        pin = p0 + (p1 - p0) * (w1 / (w0 + w1))       # at the load centre
        length, centre = (p1 - p0).length + 2 * bm["overhang"], (p0 + p1) / 2
    else:
        pin = carry[0][0]
        axles = row["axles"]
        d, nrm = local(axles[0], axles[-1])
        length, centre = bm["block"], pin
    prims.obox(mb, (centre.x, (va + vb) / 2, centre.z), (d, V, nrm), (length / 2, (vb - va) / 2, bm["size"] / 2), st["steel"])
    return pin.x, pin.z


def combo_assembly(mb, asset, name, v_rope, rope_w, a, count, lod, st, radius, parts_lod=0):
    """A combination assembly: a support row under the rope and a hold-down row over it, each hold-down sheave
    directly above a support sheave. The frame is open on the outboard side (grips pass) and closes on the inboard
    side: each row's train frame is a triangular plate from its rocker pins to an apex pointing at the rope, and
    the two apexes meet a central block at rope level, which takes the pin. Returns (u, w) of the pin."""
    X = a["x"]
    fr = a["combo"]
    rows = [_row(mb, asset, name + tag, 0.0, v_rope, rope_w, a, count, mode, radius, lod, st, first, parts_lod, False)
            for tag, mode, first in (("s", "support", 1), ("h", "hold", 1))]
    if lod >= 2:   # one block joining the rows
        va, vb = rows[0]["span"](X["yokeIn"][0], X["beam"][1])
        prims.box(mb, (-0.25, va, rope_w - 0.3), (0.25, vb, rope_w + 0.3), st["steel"])
        return 0.0, rope_w
    for row in rows:   # triangular inboard plate per row: along the rocker pins, apex toward the rope
        sg = row["sg"]
        q0, q1 = row["rockers"][0], row["rockers"][-1]
        d, nrm = row["local"](q0, q1)
        mid = (q0 + q1) / 2
        half = (q1 - q0).length / 2 + a["yoke"]["overhang"]
        apex_n = (rope_w + sg * fr["apex"] - mid.z) * sg         # toward-structure coordinate of the apex (< 0)
        tri = [(-half, a["yoke"]["bar"][1]), (half, a["yoke"]["bar"][1]), (half, -a["yoke"]["legPast"]),
               (fr["apexHalf"], apex_n), (-fr["apexHalf"], apex_n), (-half, -a["yoke"]["legPast"])]
        va, vb = row["span"](*X["yokeIn"])
        prims.prism(mb, tri, (mid.x, va, mid.z), d, nrm, V, vb - va, st["steel"])
        if lod <= 1:   # cross tube from the apex to the central block
            a0, a1 = row["span"](X["tube"][0], X["tube"][1] + 0.01)
            w_t = rope_w + sg * (fr["apex"] + 0.05)
            prims.cylinder(mb, (0.0, a0, w_t), (0.0, a1, w_t), a["tube"] / 2, sides(lod, 10, 6), st["steel"])
    va, vb = rows[0]["span"](*X["beam"])
    h = fr["blockHalfW"]
    prims.box(mb, (-fr["blockHalfU"], va, rope_w - h), (fr["blockHalfU"], vb, rope_w + h), st["steel"])
    return 0.0, rope_w


def bullwheel_spec(common):
    """The shared bullwheel dimensions from sessellift_fgq4.json "common" (mm), in metres, for bullwheel()."""
    bw = common["bullwheel"]
    spec = {k: v / 1000.0 for k, v in bw.items() if isinstance(v, (int, float)) and k != "spokes"}
    spec["spokes"] = bw["spokes"]
    if "hub" in bw:
        spec["hub"] = {k: v / 1000.0 for k, v in bw["hub"].items()}
        spec["hubDia"] = spec["hub"]["flangeDia"]
        spec["hubBottom"] = spec["hub"]["baseBottom"]
        spec["hubTop"] = spec["hub"]["flangeTop"]
    if "stubs" in bw:
        spec["stubs"] = {"count": bw["stubs"]["count"], "dia": bw["stubs"]["dia"] / 1000.0, "reach": bw["stubs"]["reach"] / 1000.0}
    spec["plateBelowTop"] = bool(bw.get("plateBelowTop", False))
    return spec


def bullwheel(mb, center, spec, lod, style):
    """Horizontal bullwheel (vertical axle through center at rope elevation).

    spec (metres): pitch (rope circle diameter), rimTop/rimBottom (w relative to rope), plateOuter/plateInner
    (top annulus diameters), plateThick, hubDia, hubBottom/hubTop (relative), spokes, spokeWidth, spokeDepth."""
    c = vec(center)
    n = sides(lod, 64, 32, 16, 8)
    rp = spec["pitch"] / 2
    top, bot = spec["rimTop"], spec["rimBottom"]
    ri = spec["plateInner"] / 2
    thick = spec["plateThick"]
    # the top plate either caps the rim (flush with its top, as drawn) or sits on it
    p_lo, p_hi = (top - thick, top) if spec.get("plateBelowTop") else (top, top + thick)
    rim_top = p_lo
    st = style
    liner = style.but(cls="rubber")
    if lod <= 1:
        # rim channel with the rope groove on its outer face
        rim = [(ri + 0.05, bot), (rp + 0.045, bot), (rp + 0.045, -0.03), (rp + 0.012, -0.018), (rp + 0.012, 0.018),
               (rp + 0.045, 0.03), (rp + 0.045, rim_top), (ri + 0.05, rim_top)]
        prims.lathe(mb, c, W, rim, n, st)
        if lod == 0:
            prims.lathe(mb, c, W, [(rp + 0.012, -0.018), (rp + 0.002, -0.01), (rp + 0.002, 0.01), (rp + 0.012, 0.018)], n, liner, closed=False)
        # top annulus plate
        plate = [(ri, p_lo), (spec["plateOuter"] / 2, p_lo), (spec["plateOuter"] / 2, p_hi), (ri, p_hi)]
        prims.lathe(mb, c, W, plate, n, st.but(snow=0.7))
    else:
        prims.lathe(mb, c, W, [(ri, bot), (spec["plateOuter"] / 2, bot), (spec["plateOuter"] / 2, p_hi), (ri, p_hi)], n, st.but(snow=0.7))
        if lod >= 3:
            return
    # hub: a drawn base, flange and shaft where given, else a plain drum
    hub = spec.get("hub")
    if hub and lod <= 1:
        hs = sides(lod, 24, 16)
        prims.cylinder(mb, c + W * hub["baseBottom"], c + W * hub["baseTop"], hub["baseDia"] / 2, hs, st)
        prims.cylinder(mb, c + W * hub["flangeBottom"], c + W * hub["flangeTop"], hub["flangeDia"] / 2, hs, st)
        prims.cylinder(mb, c + W * hub["flangeTop"], c + W * hub["shaftTop"], hub["shaftDia"] / 2, sides(lod, 12, 8), st.but(cls="machined"))
        hr = hub["flangeDia"] / 2
        spoke_w = (hub["flangeBottom"] + hub["flangeTop"]) / 2
    else:
        hr = spec["hubDia"] / 2
        hb, ht = spec["hubBottom"], spec["hubTop"]
        prims.cylinder(mb, c + W * hb, c + W * ht, hr, sides(lod, 24, 16, 8), st)
        spoke_w = hb + (ht - hb) * 0.35
    # spokes (sloping from the hub up to the rim), I-section at LOD0, boxes further out
    k = spec["spokes"] if lod <= 1 else spec["spokes"] // 2
    stubs = spec.get("stubs")
    if stubs and lod == 0:   # chair-guide mounting stubs beyond the plate, one per spoke
        for i in range(stubs["count"]):
            a = 2 * math.pi * i / stubs["count"]
            d = U * math.cos(a) + V * math.sin(a)
            prims.cylinder(mb, c + d * (spec["plateOuter"] / 2 - 0.02) + W * (p_lo - 0.05), c + d * stubs["reach"] + W * (p_lo - 0.05),
                           stubs["dia"] / 2, 6, st)
    for i in range(k):   # spokes on the u and v axes and the diagonals
        a = 2 * math.pi * i / k
        d = U * math.cos(a) + V * math.sin(a)
        p0 = c + d * (hr * 0.9) + W * spoke_w
        p1 = c + d * (ri + 0.06) + W * (bot + (rim_top - bot) * 0.5)
        if lod == 0:
            prims.ibeam(mb, p0, p1, spec["spokeDepth"], spec["spokeWidth"], 0.018, 0.012, st, up=W)
        else:
            prims.beam(mb, p0, p1, spec["spokeWidth"], spec["spokeDepth"], st, up=W)
    # inner ring tying the spokes (LOD0-1)
    if lod <= 1:
        ring = [(ri, bot), (ri + 0.05, bot), (ri + 0.05, top), (ri, top)]
        prims.lathe(mb, c, W, ring, n // 2, st)


def railing(mb, points, height, lod, style, posts_every=1.2, toe=True, closed=False, posts_at=None):
    """A handrail along a polyline on a deck: posts, top and knee rails, toe board. posts_at: explicit post
    feet (lift points) instead of posts spaced evenly along each segment."""
    pts = [vec(p) for p in points]
    if closed:
        pts.append(pts[0])
    r_post = 0.024
    if lod >= 3:
        return
    if posts_at is not None and lod <= 1:
        for p in (vec(q) for q in posts_at):
            if lod == 0:
                prims.cylinder(mb, p, p + W * height, r_post, 6, style)
            else:
                prims.beam(mb, p, p + W * height, 0.045, 0.045, style, up=U)
    for a, b in zip(pts, pts[1:]):
        seg = b - a
        length = seg.length
        if length < 1e-6:
            continue
        d = seg / length
        if lod <= 1:
            if posts_at is None:
                n_posts = max(1, round(length / (posts_every * (1 if lod == 0 else 2))))
                for i in range(n_posts + 1):
                    p = a + d * (length * i / n_posts)
                    if lod == 0:
                        prims.cylinder(mb, p, p + W * height, r_post, 6, style)
                    else:
                        prims.beam(mb, p, p + W * height, 0.045, 0.045, style, up=U if abs(d.dot(U)) < 0.9 else V)
            n_rail = 6 if lod == 0 else 4
            prims.cylinder(mb, a + W * height, b + W * height, r_post, n_rail, style)
            prims.cylinder(mb, a + W * (height * 0.5), b + W * (height * 0.5), r_post * 0.8, n_rail, style)
        else:
            prims.beam(mb, a + W * (height - 0.02), b + W * (height - 0.02), 0.05, 0.05, style)
        if toe and lod <= 2:
            prims.beam(mb, a + W * 0.05, b + W * 0.05, 0.008, 0.10, style)


def ladder(mb, bottom, top, width, lod, style, rung_every=0.28, handrails=True, rung_style=None):
    """A ship's ladder (stringers and rungs) from bottom to top, width across. Rungs are safety yellow up close
    unless rung_style is given."""
    b, t = vec(bottom), vec(top)
    x, y, z = prims.basis_along(t - b, W)
    hw = width / 2
    for s in (-1, 1):
        if lod <= 2:
            prims.beam(mb, b + y * s * hw, t + y * s * hw, 0.012, 0.15, style, up=z)
    if lod <= 1:
        length = (t - b).length
        n = int(length / (rung_every * (1 if lod == 0 else 2)))
        for i in range(1, n + 1):
            p = b + (t - b) * (i / (n + 1))
            rs = rung_style or style.but(cls="safety_yellow" if lod == 0 else style.cls)
            prims.beam(mb, p - y * hw, p + y * hw, 0.06 if lod == 0 else 0.04, 0.03, rs, up=z)
    if handrails and lod <= 1:
        for s in (-1, 1):
            prims.cylinder(mb, b + y * s * (hw + 0.03) + W * 0.9, t + y * s * (hw + 0.03) + W * 0.9, 0.022, 6 if lod == 0 else 4, style)


def deck(mb, lo, hi, lod, style, grating=True):
    """A walkway deck plate with edge channels."""
    st = style.but(cls="grating" if grating else style.cls, snow=0.8)
    lo, hi = vec(lo), vec(hi)
    prims.box(mb, lo, hi, st)
    if lod == 0:   # edge channels under the plate, along both long sides
        along_u = (hi.x - lo.x) >= (hi.y - lo.y)
        depth = 0.12
        for s in (0, 1):
            if along_u:
                y0 = lo.y if s == 0 else hi.y - 0.05
                prims.box(mb, (lo.x, y0, lo.z - depth), (hi.x, y0 + 0.05, lo.z), style)
            else:
                x0 = lo.x if s == 0 else hi.x - 0.05
                prims.box(mb, (x0, lo.y, lo.z - depth), (x0 + 0.05, hi.y, lo.z), style)


# -- the Sessellift chair's safety bar, shared -----------------------------------------------------------------
_SL_CHAIR = None


def sessellift_chair_spec():
    """The Sessellift chair's section of sessellift_fgq4.json: other chairs share its safety bar."""
    global _SL_CHAIR
    if _SL_CHAIR is None:
        path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "sessellift_fgq4.json")
        with open(path, encoding="utf-8") as f:
            _SL_CHAIR = json.load(f)["chair"]
    return _SL_CHAIR


def restraint_sleeves(mb, sl, r, place, style):
    """The safety bar's pivot sleeves along the top bar. place(u, halfWidth, w, side) maps the Sessellift chair's
    millimetres (halfWidth out from its middle, side -1 or 1) into the chair's frame."""
    for s in (-1, 1):
        prims.cylinder(mb, place(sl["u"], sl["from"], sl["w"], s), place(sl["u"], sl["to"], sl["w"], s), r, 5, style, caps=(False, False))


def restraint_bar(mb, bar, radii, place, near, sides, style, handle_style):
    """The Sessellift chair's safety bar, raised behind the seat: arms from the pivot sleeves (up close), the rail,
    the legs with their footrest stubs, and up close the rod and the handles. place as for restraint_sleeves; radii:
    the bar, rod and handle tubes (m)."""
    rail = bar["rail"]
    for s in ((-1, 1) if near else ()):
        prims.tube_path(mb, [place(u, bar["armHalfWidth"], w, s) for u, w in bar["arm"]], radii["bar"], sides, style)
    leg = bar["leg"]
    c = rail["corner"]
    right = [leg[i] for i in (4, 3, 2, 1, 0)] if near else [leg[4], leg[3], leg[0]]
    path = [place(u, hw, w, 1) for u, hw, w in right]
    path += [place(rail["u"], rail["halfWidth"] + c * 0.5, rail["w"] - c * 0.5, 1), place(rail["u"], rail["halfWidth"], rail["w"], 1),
             place(rail["u"], rail["halfWidth"], rail["w"], -1), place(rail["u"], rail["halfWidth"] + c * 0.5, rail["w"] - c * 0.5, -1)]
    path += [place(u, hw, w, -1) for u, hw, w in reversed(right)]
    prims.tube_path(mb, path, radii["bar"], sides, style)
    if near:
        rod = bar["rod"]
        prims.cylinder(mb, place(rod["u"], -rod["halfWidth"], rod["w"], 1), place(rod["u"], rod["halfWidth"], rod["w"], 1), radii["rod"], 3,
                       style, caps=(False, False))
        hd = bar["handle"]
        for s in (-1, 1):
            prims.cylinder(mb, place(hd["from"][0], hd["halfWidth"], hd["from"][1], s), place(hd["to"][0], hd["halfWidth"], hd["to"][1], s),
                           radii["handle"], 4, handle_style, caps=(False, False))


# -- chair grips -----------------------------------------------------------------------------------
_FIXED_GRIP = None


def fixed_grip_spec():
    """The fixed grip's dimensions (fixed_grip.json), one design for every fixed-grip chair."""
    global _FIXED_GRIP
    if _FIXED_GRIP is None:
        path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "fixed_grip.json")
        with open(path, encoding="utf-8") as f:
            _FIXED_GRIP = json.load(f)
    return _FIXED_GRIP


def hanger_from_socket(path, g=None):
    """A hanger's path (mm, (v, w) points in the u = 0 plane) from where it leaves the fixed grip's socket: cut at
    socket.w below the rope, so the tube starts on the socket's last ring."""
    g = g or fixed_grip_spec()
    cut = g["socket"]["w"]
    for i in range(len(path) - 1):
        (v0, w0), (v1, w1) = path[i], path[i + 1]
        if w0 > cut >= w1:
            f = (cut - w0) / (w1 - w0)
            return [[v0 + (v1 - v0) * f, cut]] + [list(q) for q in path[i + 1:]]
    raise ValueError("the hanger never drops below the fixed grip's socket")


def hanger_joint(rings, pts, r, style):
    """What fixed_grip's socket needs to join a hanger built by prims.tube_path(mb, pts, r, ...): its first ring
    (returned as rings), that ring's frame, and the tube's radius and style."""
    ring, y, z = rings[0]
    p0, p1 = vec(pts[0]), vec(pts[1])
    return {"ring": list(ring), "y": y, "z": z, "centre": p0, "t": (p1 - p0).normalized(), "r": r, "style": style}


def _socket(mb, g, hanger, steps=3):
    """The cast housing flowing down into the hanger: a funnel from an ellipse inside the housing, at the stack's
    axis, to the hanger tube's own first ring (its vertices, so the shading runs on without a seam). Its axis leaves
    the tube straight up the tube and curves to the ellipse's middle; the flare grows from nothing at the tube."""
    k = 0.001
    top_s = g["socket"]["top"]
    cv = (top_s["v"][0] + top_s["v"][1]) / 2 * k
    ru, rv = top_s["u"] * k, (top_s["v"][1] - top_s["v"][0]) / 2 * k
    top_c = Vector((0.0, cv, g["axisW"] * k))
    c0, t, y, z, r = hanger["centre"], hanger["t"], hanger["y"], hanger["z"], hanger["r"]
    n = len(hanger["ring"])
    handle = c0 - t * ((top_c - c0).length * 0.5)
    dirs = [y * math.cos(2 * math.pi * i / n) + z * math.sin(2 * math.pi * i / n) for i in range(n)]
    top = []
    for d in dirs:   # onto the ellipse in the direction the tube's vertex faces
        h = Vector((d.x, d.y, 0.0))
        h = h.normalized() if h.length > 1e-6 else Vector((0.0, 1.0, 0.0))
        top.append(Vector((h.x * ru, h.y * rv, 0.0)))
    st = hanger["style"].but(smooth=True)
    prev = hanger["ring"]
    for j in range(1, steps + 1):
        f = j / steps
        axis = c0 * (1 - f) ** 2 + handle * (2 * (1 - f) * f) + top_c * (f * f)
        e = f * f
        cur = mb.verts_lift([tuple(axis + d * (r * (1 - e)) + top[i] * e) for i, d in enumerate(dirs)])
        for i in range(n):
            i1 = (i + 1) % n
            mb.face([prev[i], prev[i1], cur[i1], cur[i]], st, tuple(dirs[i] + dirs[i1]))
        prev = cur


def fixed_grip(mb, lod, style, dark=None, metal=None, g=None, hanger=None):
    """The fixed grip on the rope at the chair frame's origin (fixed_grip.json, mm), after photographs of the grip
    in service: a chamfered jaw block on the rope with black jaw blades over the top half of the rope both ways (just
    wider than it, their tops flaring up toward the block); out across the rope one cast arm turned round the stack's
    axis, swelling from the jaw into the housing the hanger leaves from; then a dark collar ring, the spring housing,
    the nut and the bolt's dark end. Up close the hanger flows out of the housing's underside through a socket joined
    to the hanger's own tube (hanger: from hanger_joint; the chair builds the hanger first, from hanger_from_socket).
    LOD1: the jaws as one block, the stack as one bar. style: the castings (the hanger's silver); dark: the jaw
    blades, the collar and the bolt's end; metal: the nut."""
    g = g or fixed_grip_spec()
    dark = dark or style
    metal = metal or style
    k = 0.001
    b, t = g["body"], g["tails"]
    aw = g["axisW"] * k
    hs, co, sp, nt, bt = (g[n] for n in ("housing", "collar", "spring", "nut", "bolt"))

    def along(v0, v1, r, n, st, caps=(True, True), r1=None):
        prims.cylinder(mb, (0.0, v0 * k, aw), (0.0, v1 * k, aw), r * k, n, st, caps=caps, r1=None if r1 is None else r1 * k)

    def hexagon(v0, v1, af, st, caps):   # a corner up, as the drawings show the nut: its height across the corners
        prims.cylinder(mb, (0.0, v0 * k, aw), (0.0, v1 * k, aw), af / 2 / math.cos(math.pi / 6) * k, 6, st, caps=caps,
                       phase=math.pi / 6, smooth=False)

    if lod >= 1:
        rt = t["root"]
        prims.box(mb, (-t["to"] * 0.6 * k, -rt["v"] * k, rt["w"][0] * k), (t["to"] * 0.6 * k, rt["v"] * k, rt["w"][1] * k), dark)
        along(0.0, bt["v"][1], sp["dia"][1] / 2, 4, style, caps=(False, True))
        return
    # the jaw block on the rope: its long edges chamfered
    (v0, v1), (w0, w1), c = b["v"], b["w"], b["chamfer"]
    sec = [(v0, w0 + c), (v0 + c, w0), (v1 - c, w0), (v1, w0 + c), (v1, w1 - c), (v1 - c, w1), (v0 + c, w1), (v0, w1 - c)]
    prims.prism(mb, [(v * k, w * k) for v, w in sec], Vector((b["u"][0] * k, 0.0, 0.0)), V, W, U, (b["u"][1] - b["u"][0]) * k, style)
    # the black jaw blades over the top half of the rope: an arch from the rope's sides at its centreline up over the
    # top, from the block's ends to their tips, their tops flaring up toward the block
    steps = t["arc"]

    def section(u, sq):
        (lo, hi), half = sq["w"], sq["v"]
        return [(u * k, half * math.cos(a) * k, (lo + (hi - lo) * math.sin(a)) * k)
                for a in (math.pi * i / steps for i in range(steps + 1))]
    blade = dark.but(smooth=True)
    for sgn, u0 in ((-1, b["u"][0]), (1, b["u"][1])):
        ri = mb.verts_lift(section(u0, t["root"]))
        ti = mb.verts_lift(section(sgn * t["to"], t["tip"]))
        for i in range(steps):   # the arch
            a = math.pi * (i + 0.5) / steps
            mb.face([ri[i], ri[i + 1], ti[i + 1], ti[i]], blade, (0.0, math.cos(a), math.sin(a)))
        mb.face([ri[steps], ri[0], ti[0], ti[steps]], dark, (0.0, 0.0, -1.0))   # its flat underside, on the rope
        mb.face(mb.verts_lift(section(sgn * t["to"], t["tip"])), dark, (sgn, 0.0, 0.0))
    # the cast arm and housing, turned round the stack's axis from the jaw block out to the collar, and the socket
    # from its underside into the hanger
    prims.lathe(mb, (0.0, 0.0, aw), V, [(r * k, v * k) for r, v in hs["profile"]], hs["sides"], style, closed=False,
                phase=math.pi / hs["sides"])
    if hanger is not None:
        _socket(mb, g, hanger)
    # the dark collar ring, the spring housing, the nut and the bolt's dark end
    along(co["v"][0], co["v"][1], co["dia"] / 2, 8, dark, caps=(False, False))
    s0, s1 = sp["v"]
    along(s0, s0 + sp["shoulder"], sp["dia"][0] / 2, 10, style, caps=(True, False), r1=sp["dia"][1] / 2)
    along(s0 + sp["shoulder"], s1, sp["dia"][1] / 2, 10, style, caps=(False, False))
    hexagon(nt["v"][0], nt["v"][1], nt["af"], metal, (True, True))
    along(bt["v"][0], bt["v"][1], bt["dia"] / 2, 8, dark, caps=(False, True))
