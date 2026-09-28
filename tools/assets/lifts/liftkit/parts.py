"""Lift parts built from primitives, in the lift frame. Detail follows an LOD level (0 = nearest)."""
import math

from mathutils import Vector

from . import prims
from .mesh import MeshBuilder, Style
from .prims import U, V, W, vec


def sides(lod, near, *far):
    """Polygon sides for a round part at this LOD: near at LOD0, then the listed values."""
    seq = (near,) + far
    return seq[min(lod, len(seq) - 1)]


def sheave(mb, center, axis, dia, width, lod, style_wheel, style_liner, spokes=True):
    """A grooved rope sheave: rim with a rubber liner, a web and a hub. Axis through center."""
    r = dia / 2
    n = sides(lod, 24, 12, 8, 6)
    ax = vec(axis).normalized()
    c = vec(center)
    hw = width / 2
    if lod >= 2:
        prims.cylinder(mb, c - ax * hw, c + ax * hw, r, n, style_wheel)
        return
    # rim (flanges) and liner groove, as one lathe section: (radius, height along axis)
    groove = 0.35 * width
    rim = [(r * 0.80, -hw), (r + 0.012, -hw), (r + 0.012, -groove), (r - 0.02, -groove * 0.5),
           (r - 0.02, groove * 0.5), (r + 0.012, groove), (r + 0.012, hw), (r * 0.80, hw)]
    prims.lathe(mb, c, ax, rim, n, style_wheel)
    if lod == 0:
        prims.lathe(mb, c, ax, [(r - 0.02, -groove * 0.5), (r - 0.005, -groove * 0.3), (r - 0.005, groove * 0.3), (r - 0.02, groove * 0.5)],
                    n, style_liner, closed=False)
    # web and hub
    prims.cylinder(mb, c - ax * (width * 0.12), c + ax * (width * 0.12), r * 0.80, n, style_wheel, caps=(True, True))
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
        sheave(target, centre, V, dia, width, lod, st["red"], st["rubber"])
        if lod <= 1:
            key = f"sheave_{name}{first + k}"
            asset.parts[key] = target
            asset.pivots[key] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}
            if lod == 0:   # axle boss from the rocker plate
                prims.cylinder(mb, (centre.x, pv, wz), (centre.x, v_rope + inboard * width / 2, wz), 0.035, 8, st["rod"])
    plate_t = 0.02
    v0, v1 = sorted((pv - plate_t / 2, pv + plate_t / 2))
    for i in range(0, n - 1, 2):   # rockers: one plate per pair, from the axles to the rocker pin
        a_u, b_u = u_c + offs[i] - 0.07, u_c + offs[i + 1] + 0.07
        prims.box(mb, (a_u, v0, min(wz, w_rock) - 0.05), (b_u, v1, max(wz, w_rock) + 0.05), st["steel"])
    half = pitch * (n // 2) / 2 + 0.1   # main beam between the rocker pins, just inboard of the rockers
    b0, b1 = sorted((pv + inboard * plate_t, pv + inboard * (plate_t + 0.1)))
    prims.box(mb, (u_c - half, b0, min(w_rock, w_beam) - 0.05), (u_c + half, b1, max(w_rock, w_beam) + 0.05), st["steel"])
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


def ladder(mb, bottom, top, width, lod, style, rung_every=0.28, handrails=True):
    """A ship's ladder (stringers and rungs) from bottom to top, width across."""
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
            prims.beam(mb, p - y * hw, p + y * hw, 0.06 if lod == 0 else 0.04, 0.03, style.but(cls="safety_yellow" if lod == 0 else style.cls), up=z)
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
