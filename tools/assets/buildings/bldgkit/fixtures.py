"""Fixtures that fill a wall's openings or stand on and around a building: doors, windows, louvres, wall packs,
transformers, a pipe vault, ridge caps and vents, trims. Positions are in a wall's frame (shell.WallFrame) or the
building frame; metres. `lod` trims detail: 0 everything, 1 plain panels, 2 flat decals on the wall.
"""
import contextlib

from mathutils import Vector

from liftkit import prims

from .shell import W


def _quad(mb, pts, style, outward):
    return mb.face(mb.verts_lift(pts), style, tuple(outward))


def panel(mb, wf, a0, a1, w0, w1, n, style):
    """A flat rectangle parallel to the wall at `n` out of its outer face (negative: in the opening's back)."""
    return _quad(mb, [wf.p(a0, w0, n), wf.p(a1, w0, n), wf.p(a1, w1, n), wf.p(a0, w1, n)], style, wf.en)


def frame_trim(mb, wf, a0, a1, w0, w1, width, proud, style, sill=True):
    """A trim frame round an opening, standing `proud` out of the wall: head, jambs and (optionally) a sill."""
    def bar(b0, b1, c0, c1):
        lo = Vector(wf.p(b0, c0, 0.0))
        hi = Vector(wf.p(b1, c1, proud))
        prims.box(mb, tuple(map(min, lo, hi)), tuple(map(max, lo, hi)), style, skip=_back(wf))
    bar(a0 - width, a1 + width, w1, w1 + width)
    bar(a0 - width, a0, w0 if not sill else w0 - width, w1)
    bar(a1, a1 + width, w0 if not sill else w0 - width, w1)
    if sill:
        bar(a0 - width, a1 + width, w0 - width, w0)


def _back(wf):
    """The box face lying on the wall (never seen): the one facing -en."""
    for k, axis in enumerate(("u", "v")):
        c = wf.en[k]
        if abs(c) > 0.5:
            return (("-" if c > 0 else "+") + str(k),)
    return ()


def _wbox(mb, wf, a0, a1, w0, w1, n0, n1, style, back=True):
    """A box in a wall's frame: a0..a1 along it, w0..w1 up, n0..n1 out of its face; `back` skips the face toward the
    wall (never seen)."""
    lo = Vector(wf.p(a0, w0, n0))
    hi = Vector(wf.p(a1, w1, n1))
    prims.box(mb, tuple(map(min, lo, hi)), tuple(map(max, lo, hi)), style, skip=_back(wf) if back else ())


def man_door(mb, wf, a_mid, width, height, depth, styles, lod, half_light=False, lite=None):
    """A hollow-metal man door set `depth` into its opening (a0..a1, floor to `height`): its frame proud of the wall,
    the slab with an optional half-light, a kick plate, three hinges and a lever on a rose."""
    a0, a1 = a_mid - width / 2, a_mid + width / 2
    if lod >= 2:
        panel(mb, wf, a0, a1, 0.0, height, 0.012, styles["door"])
        return
    fw = 0.05   # the frame's face, round the opening
    frame_trim(mb, wf, a0, a1, 0.0, height, fw, 0.02, styles["trim"], sill=False)
    if half_light:
        g0, g1 = lite if lite else (height * 0.55, height * 0.88)   # lite: the glass's bottom and top
        ga0, ga1 = a_mid - width * 0.3, a_mid + width * 0.3
        for b0, b1, c0, c1 in ((a0, a1, 0.0, g0), (a0, a1, g1, height), (a0, ga0, g0, g1), (ga1, a1, g0, g1)):
            panel(mb, wf, b0, b1, c0, c1, -depth, styles["door"])
        for b0, b1, c0, c1, out in ((ga0, ga1, g0, g0, W), (ga0, ga1, g1, g1, -W)):
            _quad(mb, [wf.p(b0, c0, -depth), wf.p(b1, c0, -depth), wf.p(b1, c0, -depth - 0.03), wf.p(b0, c0, -depth - 0.03)],
                  styles["door"], out)
        for b, out in ((ga0, wf.ea), (ga1, -wf.ea)):
            _quad(mb, [wf.p(b, g0, -depth), wf.p(b, g1, -depth), wf.p(b, g1, -depth - 0.03), wf.p(b, g0, -depth - 0.03)],
                  styles["door"], out)
        panel(mb, wf, ga0, ga1, g0, g1, -depth - 0.03, styles["glass"])
        if lod == 0:   # the glazing bead round the glass
            for b0, b1, c0, c1 in ((ga0 - 0.025, ga1 + 0.025, g1, g1 + 0.025), (ga0 - 0.025, ga1 + 0.025, g0 - 0.025, g0),
                                   (ga0 - 0.025, ga0, g0, g1), (ga1, ga1 + 0.025, g0, g1)):
                _wbox(mb, wf, b0, b1, c0, c1, -depth, -depth + 0.012, styles["trim"])
    else:
        panel(mb, wf, a0, a1, 0.0, height, -depth, styles["door"])
    if lod == 0:
        n = -depth
        _wbox(mb, wf, a0 + 0.03, a1 - 0.03, 0.03, 0.28, n, n + 0.004, styles["kick"])           # kick plate
        for hw in (0.25, height / 2 + 0.05, height - 0.28):                                      # hinges, at a0
            _wbox(mb, wf, a0 - 0.012, a0 + 0.018, hw - 0.055, hw + 0.055, n, n + 0.02, styles["kick"])
        h = a1 - 0.08                                                                              # rose and lever
        _wbox(mb, wf, h - 0.03, h + 0.03, 0.96, 1.10, n, n + 0.012, styles["kick"])
        _wbox(mb, wf, h - 0.13, h + 0.02, 1.015, 1.045, n + 0.012, n + 0.06, styles["kick"])


def door_canopy(mb, wf, a_mid, width, w_top, projection, styles, lod):
    """A shed hood over a man door: a sloped steel canopy `projection` out of the wall, its top at `w_top` on the wall,
    on two diagonal brackets (LOD0)."""
    half = width / 2
    drop = projection * 0.25
    poly = [(0.0, w_top), (projection, w_top - drop), (projection, w_top - drop - 0.06), (0.0, w_top - 0.14)]
    pts = [(n, w) for n, w in poly]
    origin = Vector(wf.p(a_mid - half, 0.0, 0.0))
    prims.prism(mb, pts, origin, wf.en, W, wf.ea, width, styles["hood"], caps=(True, True))
    if lod == 0:
        for s in (-1, 1):
            a = a_mid + s * (half - 0.08)
            p0 = Vector(wf.p(a, w_top - 0.62, 0.0))
            p1 = Vector(wf.p(a, w_top - 0.27, projection * 0.8))   # meeting the hood's underside there
            prims.beam(mb, p0 - Vector(wf.en) * 0.0, p1, 0.04, 0.04, styles["trim"], up=W)


def rollup_door(mb, wf, a_mid, width, height, depth, styles, lod, slats=8, leaf=None):
    """An insulated sectional overhead door set `depth` in: its frame trim, section joints, a row of vision lites in
    the third section from the top, a bottom seal and a lift handle (LOD0); the face and lites (LOD1); a decal (LOD2).
    `leaf`: a context (MeshBuilder.grouped) the leaf is drawn in, so it is a moving part; the frame stays on the wall."""
    a0, a1 = a_mid - width / 2, a_mid + width / 2
    if lod >= 2:
        with leaf or contextlib.nullcontext():
            panel(mb, wf, a0, a1, 0.0, height, 0.012, styles["door"])
        return
    frame_trim(mb, wf, a0, a1, 0.0, height, 0.10, 0.025, styles["trim"], sill=False)
    with leaf or contextlib.nullcontext():
        _rollup_leaf(mb, wf, a_mid, a0, a1, width, height, depth, styles, lod, slats)


def _rollup_leaf(mb, wf, a_mid, a0, a1, width, height, depth, styles, lod, slats):
    n = -depth
    panel(mb, wf, a0, a1, 0.0, height, n, styles["slat"])
    pitch = height / slats
    row = slats - 3   # the section the lites sit in, counted from the bottom (0)
    lites, lw, lh = 6, 0.56, pitch * 0.48
    gap = (width - lites * lw) / (lites + 1)
    w0 = row * pitch + (pitch - lh) / 2
    for i in range(lites):
        b0 = a0 + gap + i * (lw + gap)
        if lod == 0:   # each lite's frame, then its glass
            _wbox(mb, wf, b0 - 0.03, b0 + lw + 0.03, w0 - 0.03, w0 + lh + 0.03, n, n + 0.008, styles["trim"])
        panel(mb, wf, b0, b0 + lw, w0, w0 + lh, n + (0.012 if lod == 0 else 0.004), styles["glass"])
    if lod == 0:
        for k in range(1, slats):   # the joints between sections: a shadow groove each
            w = k * pitch
            _wbox(mb, wf, a0, a1, w - 0.016, w + 0.016, n, n + 0.018, styles["groove"])
        _wbox(mb, wf, a0, a1, 0.0, 0.06, n, n + 0.03, styles["seal"])                       # bottom seal
        _wbox(mb, wf, a_mid - 0.16, a_mid + 0.16, 0.62, 0.68, n, n + 0.05, styles["trim"])   # lift handle
        for s in (-1, 1):   # the lock bar's housings either side
            _wbox(mb, wf, a_mid + s * (width / 2 - 0.25) - 0.05, a_mid + s * (width / 2 - 0.25) + 0.05, 0.9, 1.0, n, n + 0.03,
                  styles["trim"])


def window(mb, wf, a0, a1, w0, w1, depth, styles, lod, rail=None):
    """A fixed window: trim frame proud of the wall, a projecting sill and a drip cap over the head, the glass `depth`
    in within a sash frame and a mullion (LOD0); the frame and glass (LOD1); a flat glass decal (LOD2)."""
    if lod >= 2:
        panel(mb, wf, a0, a1, w0, w1, 0.012, styles["glass"])
        return
    frame_trim(mb, wf, a0, a1, w0, w1, 0.05, 0.022, styles["trim"], sill=False)
    panel(mb, wf, a0, a1, w0, w1, -depth, styles["glass"])
    if lod == 0:
        _wbox(mb, wf, a0 - 0.08, a1 + 0.08, w0 - 0.05, w0, 0.0, 0.07, styles["sill"])          # sill, with snow on it
        _wbox(mb, wf, a0 - 0.08, a1 + 0.08, w1 + 0.05, w1 + 0.08, 0.0, 0.06, styles["sill"])   # drip cap
        s = 0.04   # the sash frame round the glass, then the mullion
        n = -depth
        for b0, b1, c0, c1 in ((a0, a1, w1 - s, w1), (a0, a1, w0, w0 + s), (a0, a0 + s, w0 + s, w1 - s), (a1 - s, a1, w0 + s, w1 - s)):
            _wbox(mb, wf, b0, b1, c0, c1, n, n + 0.03, styles["trim"])
        if rail == "h":   # a rail across it (a sash that slides up), a little below the middle
            m = w1 - (w1 - w0) * 0.58
            _wbox(mb, wf, a0 + s, a1 - s, m - 0.02, m + 0.02, n, n + 0.025, styles["trim"])
        elif rail != "none":   # "none": one pane
            m = (a0 + a1) / 2
            _wbox(mb, wf, m - 0.02, m + 0.02, w0 + s, w1 - s, n, n + 0.025, styles["trim"])


def wall_pack(mb, wf, a_mid, w_bottom, styles, lod, size=(0.40, 0.30, 0.24), conduit_to=None):
    """An LED wall pack, full cutoff: a backplate on the wall, a body whose front slopes back to a narrower top, cooling
    fins along the top (LOD0) and the lens flush in the underside, so the light goes down and out. Returns the lens
    centre and its aim (building frame), for the light record. LOD2: a flat decal."""
    sa, sw, sn = size
    if lod >= 2:
        panel(mb, wf, a_mid - sa / 2, a_mid + sa / 2, w_bottom, w_bottom + sw, 0.012, styles["housing"])
    else:
        # the backplate, a little wider and taller than the body
        _wbox(mb, wf, a_mid - sa / 2 - 0.02, a_mid + sa / 2 + 0.02, w_bottom - 0.01, w_bottom + sw + 0.03, 0.0, 0.025,
              styles["housing"])
        # the body: in section (out, up) a deep bottom and a sloped front rising to a shallow top
        poly = [(0.025, 0.0), (sn, 0.0), (sn, sw * 0.35), (sn * 0.55, sw), (0.025, sw)]
        origin = Vector(wf.p(a_mid - sa / 2, w_bottom, 0.0))
        prims.prism(mb, poly, origin, wf.en, W, wf.ea, sa, styles["housing"], caps=(True, True))
        # the lens, just below the underside (never sharing its plane), and its bezel
        lw = w_bottom - 0.005
        q = [wf.p(a_mid - sa / 2 + 0.035, lw, 0.06), wf.p(a_mid + sa / 2 - 0.035, lw, 0.06),
             wf.p(a_mid + sa / 2 - 0.035, lw, sn - 0.03), wf.p(a_mid - sa / 2 + 0.035, lw, sn - 0.03)]
        _quad(mb, q, styles["lens"], -W)
        if lod == 0:
            for k in range(5):   # heat-sink ridges along the top
                a = a_mid - sa / 2 + 0.06 + k * (sa - 0.12) / 4
                _wbox(mb, wf, a - 0.008, a + 0.008, w_bottom + sw - 0.005, w_bottom + sw + 0.015, 0.03, sn * 0.55 - 0.01,
                      styles["housing"])
            # the conduit up the wall from the backplate's top to `conduit_to` (the eave), where the wiring goes in
            top = w_bottom + sw + 0.03
            if conduit_to is not None and conduit_to - top > 0.05:
                c0 = Vector(wf.p(a_mid, top, 0.03))
                prims.cylinder(mb, tuple(c0), tuple(c0 + W * (conduit_to - top)), 0.014, 6, styles["housing"].but(snow=0.0),
                               caps=(False, False))
    centre = Vector(wf.p(a_mid, w_bottom - 0.005, (0.06 + sn - 0.03) / 2))
    aim = (Vector(wf.en) * 0.35 - W).normalized()
    return tuple(centre), tuple(aim)


def gooseneck(mb, wf, a, w_mount, reach, w_target, styles, lod):
    """A gooseneck sign light (a barn light): a round backplate on the wall at `w_mount`, an arm curving out `reach` and
    down, and a shallow dish shade at its end, its lens facing down and back at the sign whose top is at `w_target`.
    LOD0 a three-piece arm and a 10-sided shade; LOD1 one arm piece and a 6-sided shade; LOD2 nothing (the record
    stays). Returns the lens centre and its aim (building frame)."""
    en, ea = Vector(wf.en), Vector(wf.ea)
    base = Vector(wf.p(a, w_mount, 0.0))
    tip = base + en * reach + W * 0.12
    shade_c = tip + en * 0.06 - W * 0.10            # the shade hangs from the arm's end, tipped back toward the wall
    target = Vector(wf.p(a, w_target - 0.35, 0.0))
    aim = (target - shade_c).normalized()
    if lod <= 1:
        prims.cylinder(mb, tuple(base - W * 0.08), tuple(base + W * 0.08), 0.07 if lod == 0 else 0.06,
                       10 if lod == 0 else 6, styles["housing"], caps=(False, True))   # the backplate, on the wall
        if lod == 0:
            mid = base + en * reach * 0.45 + W * 0.18
            for p0, p1 in ((base + en * 0.02, mid), (mid, tip), (tip, tip + en * 0.04 - W * 0.06)):
                prims.cylinder(mb, tuple(p0), tuple(p1), 0.016, 6, styles["housing"].but(snow=0.2), caps=(False, False))
        else:
            prims.cylinder(mb, tuple(base + en * 0.02), tuple(tip), 0.016, 4, styles["housing"], caps=(False, False))
        top, rim = shade_c - aim * 0.10, shade_c
        prims.cylinder(mb, tuple(top), tuple(rim), 0.05, 10 if lod == 0 else 6, styles["housing"], r1=0.17,
                       caps=(True, False))
        prims.cylinder(mb, tuple(rim - aim * 0.012), tuple(rim + aim * 0.002), 0.15, 10 if lod == 0 else 6, styles["lens"],
                       caps=(False, True))
    return tuple(shade_c + aim * 0.002), tuple(aim)


def canopy_light(mb, wf, a_mid, w_top, projection, styles, lod, size=0.20):
    """A square LED downlight under a door hood (door_canopy's geometry): a shallow housing hung from the hood's
    sloped underside halfway out, its lens facing straight down over the step; wired through the hood, so no conduit.
    Drawn at LOD0-1, where the hood is. Returns the lens centre and its aim (building frame)."""
    drop = projection * 0.25

    def under(n):   # the hood's underside at `n` out of the wall
        return w_top - 0.14 - n * (drop - 0.08) / projection
    nc, h = projection * 0.5, size / 2
    bottom = under(nc + h) - 0.035
    if lod <= 1:
        _wbox(mb, wf, a_mid - h, a_mid + h, bottom, under(nc - h) + 0.01, nc - h, nc + h, styles["housing"], back=False)
        lw = bottom - 0.004
        q = [wf.p(a_mid - h + 0.025, lw, nc - h + 0.025), wf.p(a_mid + h - 0.025, lw, nc - h + 0.025),
             wf.p(a_mid + h - 0.025, lw, nc + h - 0.025), wf.p(a_mid - h + 0.025, lw, nc + h - 0.025)]
        _quad(mb, q, styles["lens"], -W)
    return tuple(Vector(wf.p(a_mid, bottom - 0.004, nc))), tuple(-W)


def cage_sconce(mb, wf, a, w_mid, styles, lod):
    """A vapour-tight cage light beside a door ("jelly jar"): a cast backplate on the wall over its junction box, an arm
    out at the bottom, an upright glass jar (the lens, glow mask 3) under a cast cap, and at LOD0 a wire guard of four
    bars and two rings round the jar. Drawn at LOD0-1. Returns the jar's centre and an aim (down; it is a point light)."""
    jar_r, n_c = 0.045, 0.105
    j0, j1 = w_mid - 0.065, w_mid + 0.065

    def at(w, n=n_c, da=0.0, dn=0.0):
        return tuple(Vector(wf.p(a + da, w, n + dn)))
    if lod <= 1:
        _wbox(mb, wf, a - 0.055, a + 0.055, j0 - 0.07, j1 + 0.01, 0.0, 0.025, styles["housing"])          # backplate
        _wbox(mb, wf, a - 0.035, a + 0.035, j0 - 0.04, j0, 0.025, n_c + 0.05, styles["housing"])          # the arm
        prims.cylinder(mb, at(j0), at(j1), jar_r, 8 if lod == 0 else 6, styles["lens"], caps=(False, False))
        prims.cylinder(mb, at(j1), at(j1 + 0.022), jar_r + 0.008, 8 if lod == 0 else 6, styles["housing"],
                       caps=(True, True))
    if lod == 0:
        g = jar_r + 0.012
        for da, dn in ((g, 0.0), (-g, 0.0), (0.0, g), (0.0, -g)):   # the guard's bars
            prims.cylinder(mb, at(j0, da=da, dn=dn), at(j1, da=da, dn=dn), 0.004, 4, styles["housing"].but(snow=0.0),
                           caps=(False, False))
        for w in (j0 + 0.04, j1 - 0.035):   # and its rings
            prims.cylinder(mb, at(w - 0.004), at(w + 0.004), g + 0.003, 8, styles["housing"].but(snow=0.0),
                           caps=(False, False))
    return at(w_mid), tuple(-W)


def pole_light(mb, base, height, arm, styles, lod):
    """A yard light on a steel pole: a round concrete base, the pole, an arm reaching `arm` (a building-frame (du, dv)
    vector, along u or v) and a flat shoebox head with its lens underneath. LOD0 all; LOD1 no base; LOD2 the pole and
    head. Returns the lens centre and the head's (down) position for the aim."""
    x, y = base
    top = height
    hx, hy = x + arm[0], y + arm[1]
    if lod == 0:
        prims.cylinder(mb, (x, y, -0.1), (x, y, 0.6), 0.30, 12, styles["pad"].but(snow=1.0), caps=(False, True))
    prims.cylinder(mb, (x, y, 0.6 if lod == 0 else 0.0), (x, y, top + 0.05), 0.065, 8 if lod == 0 else 6,
                   styles["pole"], caps=(False, True))
    if lod <= 1:   # the arm, a square tube
        prims.beam(mb, Vector((x, y, top - 0.05)), Vector((hx, hy, top - 0.05)), 0.07, 0.07, styles["pole"])
    # the head: 0.55 long along the arm, 0.32 across, 0.13 deep, centred on the arm's end
    along_u = abs(arm[0]) >= abs(arm[1])
    eu, ev = (0.275, 0.16) if along_u else (0.16, 0.275)
    lo, hi = (hx - eu, hy - ev, top - 0.17), (hx + eu, hy + ev, top - 0.04)
    prims.box(mb, lo, hi, styles["housing"])
    if lod <= 1:
        z = lo[2] - 0.004
        q = [(lo[0] + 0.03, lo[1] + 0.03, z), (hi[0] - 0.03, lo[1] + 0.03, z), (hi[0] - 0.03, hi[1] - 0.03, z),
             (lo[0] + 0.03, hi[1] - 0.03, z)]
        mb.face(mb.verts_lift(q), styles["lens"], (0.0, 0.0, -1.0))
    return (hx, hy, lo[2] - 0.004)


def louvre(mb, wf, a0, a1, w0, w1, depth, styles, lod, blades=6):
    """A wall louvre: a dark back `depth` in and slanted blades (LOD0), a dark panel (LOD1), a decal (LOD2)."""
    if lod >= 2:
        panel(mb, wf, a0, a1, w0, w1, 0.012, styles["louvre"])
        return
    panel(mb, wf, a0, a1, w0, w1, -depth, styles["louvre"])
    if lod == 0:
        pitch = (w1 - w0) / blades
        for k in range(blades):
            top = w0 + (k + 1) * pitch
            # a blade sloping down and out: from the back (high) to the face (low)
            p = [wf.p(a0, top - 0.01, -depth + 0.01), wf.p(a1, top - 0.01, -depth + 0.01),
                 wf.p(a1, top - pitch * 0.9, -0.01), wf.p(a0, top - pitch * 0.9, -0.01)]
            _quad(mb, p, styles["blade"], tuple(Vector(wf.en) + W))
            _quad(mb, list(reversed(p)), styles["blade"].but(snow=0.0), tuple(-Vector(wf.en) - W))


def transformer(mb, centre, along, half, height, styles, lod):
    """A pad-mount transformer: a box cabinet with cooling fins on its back (LOD0), on the ground at `centre`."""
    cx, cy, cz = centre
    ex, ey = half
    if along == "u":
        lo, hi = (cx - ex, cy - ey, cz), (cx + ex, cy + ey, cz + height)
    else:
        lo, hi = (cx - ey, cy - ex, cz), (cx + ey, cy + ex, cz + height)
    prims.box(mb, lo, hi, styles["cabinet"], skip=("-2",))
    if lod == 0:   # a sloped lid lip and fins on the +v and -v faces
        prims.box(mb, (lo[0] - 0.03, lo[1] - 0.03, hi[2]), (hi[0] + 0.03, hi[1] + 0.03, hi[2] + 0.04), styles["cabinet"])
        n = 5
        for k in range(n):
            x = lo[0] + (hi[0] - lo[0]) * (k + 0.5) / n
            for y0, y1 in ((lo[1] - 0.12, lo[1]), (hi[1], hi[1] + 0.12)):
                prims.box(mb, (x - 0.015, y0, lo[2] + 0.15), (x + 0.015, y1, hi[2] - 0.25), styles["cabinet"], skip=("-2",))


def pad(mb, lo, hi, style):
    """A concrete pad (top and sides; the underside is in the ground)."""
    prims.box(mb, lo, hi, style.but(snow=1.0), skip=("-2",))


def ridge_cap(mb, hx, ridge_w, width, height, style):
    """A ridge cap: a shallow triangular prism along u over the ridge line."""
    prims.prism(mb, [(-width / 2, 0.0), (width / 2, 0.0), (0.0, height)], (-hx, 0.0, ridge_w - 0.02),
                Vector((0.0, 1.0, 0.0)), W, Vector((1.0, 0.0, 0.0)), 2 * hx, style, caps=(True, True))


def ridge_vent(mb, u, ridge_w, size, styles, lod):
    """A box ridge ventilator: a louvred base box and a hood over it, astride the ridge."""
    sx, sy, sz = size
    prims.box(mb, (u - sx / 2, -sy / 2, ridge_w - 0.3), (u + sx / 2, sy / 2, ridge_w + sz), styles["vent"], skip=("-2",))
    if lod == 0:
        prims.box(mb, (u - sx / 2 - 0.08, -sy / 2 - 0.08, ridge_w + sz), (u + sx / 2 + 0.08, sy / 2 + 0.08, ridge_w + sz + 0.08),
                  styles["hood"])
        for s in (-1, 1):   # louvre bands on its two long sides
            prims.box(mb, (u - sx / 2 + 0.05, s * sy / 2 - 0.01, ridge_w + 0.05), (u + sx / 2 - 0.05, s * sy / 2 + 0.01, ridge_w + sz - 0.08),
                      styles["louvre"])


def corner_trim(mb, corner, width, w0, w1, style):
    """An L of trim down a building corner (a square post `width` wide, centred on the corner)."""
    cx, cy = corner
    prims.box(mb, (cx - width / 2, cy - width / 2, w0), (cx + width / 2, cy + width / 2, w1), style, skip=("-2",))


def bollard(mb, at, styles, height=1.1, r=0.11):
    x, y = at
    prims.cylinder(mb, (x, y, 0.0), (x, y, height), r, 10, styles["bollard"], caps=(False, True))
