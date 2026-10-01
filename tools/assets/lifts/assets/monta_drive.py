"""Monta FG4 drive terminal (bottom station), a recreation from the owner's reference model's dimensions
(monta_fg4.json "drive", measured locally; see its "_adjust" notes): two round booms on leaning tubular legs over
concrete piers, a machinery frame and tension cylinders between them, the bullwheel on its carriage, a raked
barrel hood with framed panels, and the line's hold-down trains hung from a raised front cross tube.

The rope runs level from the bullwheel over the guide sheaves and leaves climbing under the exit trains (three
trains of four per rope). Loaded chairs leave on the right rope (v+). Millimetres in the spec; the lift frame's
origin is on the bullwheel's axle at grade, u toward the line.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import GLASS, MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "terminal"
LODS = 4
SPEC = "monta_fg4.json"


def m(x):
    return x / 1000.0


def mv(p):
    return tuple(m(x) for x in p)


def styles(lod):
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("paint_grey", snow=0.5),
        "galv": Style("galvanised", snow=0.5),
        "dark": Style("paint_dark", snow=0.0),
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "hood": Style("livery", livery=1.0, snow=1.0),
        "glass": Style("glass", slot=GLASS, smooth=False) if lod <= 1 else Style("glass", smooth=False),
        "interior": Style("interior"),
        "rod": Style("machined"),
    }


# -- generic parts ---------------------------------------------------------------------------------
def box_from(mb, b, st, shift=(0.0, 0.0, 0.0), mirror=False):
    """A part from the spec ({"c", "ax", "h"}, mm): an oriented box, or for a plate ("poly": its outline in the
    plane of its first two axes, about its centre) the outline extruded through its thickness; optionally moved
    (mm) or mirrored across v = 0."""
    c = [b["c"][0] + shift[0], b["c"][1] + shift[1], b["c"][2] + shift[2]]
    axes = [Vector(a) for a in b["ax"]]
    if mirror:
        c[1] = -c[1]
        axes = [Vector((a.x, -a.y, a.z)) for a in axes]
    style = st[b.get("style", "steel")]
    if "poly" in b:
        t = m(b["h"][2])
        origin = Vector(mv(c)) - axes[2] * t
        prims.prism(mb, [(m(x), m(y)) for x, y in b["poly"]], tuple(origin), axes[0], axes[1], axes[2], 2 * t, style)
    else:
        prims.obox(mb, mv(c), axes, mv(b["h"]), style)


def oval_leg(mb, lg, n, style):
    """A leaning tube of oval section (semi-axes a1 across the line, a2 in the leg's plane) along the leg's axis,
    cut level at the base plate (bottomW) and under the saddle (topW)."""
    c, ax = Vector(mv(lg["centre"])), Vector(lg["axis"])
    e1, e2 = Vector(lg["e1"]), Vector(lg["e2"])
    a1, a2 = m(lg["a1"]), m(lg["a2"])
    rings = []
    for wz in (m(lg["bottomW"]), m(lg["topW"])):
        ring = []
        for k in range(n):
            th = 2 * math.pi * k / n + math.pi / n
            q = c + e1 * (a1 * math.cos(th)) + e2 * (a2 * math.sin(th))
            ring.append(tuple(q + ax * ((wz - q.z) / ax.z)))
        rings.append(mb.verts_lift(ring))
    for k in range(n):
        k1 = (k + 1) % n
        th = 2 * math.pi * (k + 0.5) / n + math.pi / n
        out = e1 * (math.cos(th) / a1) + e2 * (math.sin(th) / a2)
        mb.face((rings[0][k], rings[0][k1], rings[1][k1], rings[1][k]), style, tuple(out))
    mb.face(tuple(rings[0]), style, (0.0, 0.0, -1.0))
    mb.face(tuple(rings[1]), style, (0.0, 0.0, 1.0))


def cframes(mb, frames, st, lod):
    """The wheel carriage's side beams: C-frames of a bottom and a top flange, joined by a web at the front, the
    wheel running between the flanges (spec: bounds, flange and web depths, mm)."""
    for cf in frames:
        (u0, v0, w0), (u1, v1, w1) = cf["lo"], cf["hi"]
        fl, wb = cf["flange"], cf["web"]
        prims.box(mb, mv((u0, v0, w0)), mv((u1, v1, w0 + fl)), st["steel"])
        prims.box(mb, mv((u0, v0, w1 - fl)), mv((u1, v1, w1)), st["steel"])
        if lod <= 2:
            prims.box(mb, mv((u1 - wb, v0, w0 + fl)), mv((u1, v1, w1 - fl)), st["steel"])


def loft(mb, levels, style):
    """A solid lofted through level rectangles [(w, u0, u1, v0, v1), ...] (mm), bottom to top."""
    rects = []
    for w, u0, u1, v0, v1 in levels:
        rects.append(mb.verts_lift([mv((u0, v0, w)), mv((u1, v0, w)), mv((u1, v1, w)), mv((u0, v1, w))]))
    outs = [(0.0, -1.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (-1.0, 0.0, 0.0)]
    for lo, hi in zip(rects, rects[1:]):
        for k in range(4):
            k1 = (k + 1) % 4
            mb.face((lo[k], lo[k1], hi[k1], hi[k]), style, outs[k])
    mb.face(tuple(rects[0]), style, (0.0, 0.0, -1.0))
    mb.face(tuple(rects[-1]), style, (0.0, 0.0, 1.0))


def tube_from(mb, t, st, n):
    prims.cylinder(mb, mv(t["from"]), mv(t["to"]), m(t["dia"]) / 2, n, st[t.get("style", "steel")])


def slanted_tube(mb, v0, w0, r, rear, front, n, style):
    """A round tube along u at (v0, w0), its ends cut on parallel slants: rear/front = (u at the top, u at the
    bottom) (metres)."""
    def end_u(e, w):
        top, bot = e
        return (top + bot) / 2 + (top - bot) / 2 * (w - w0) / r
    ring = [(math.cos(2 * math.pi * k / n + math.pi / n), math.sin(2 * math.pi * k / n + math.pi / n)) for k in range(n)]
    back = mb.verts_lift([(end_u(rear, w0 + r * s), v0 + r * c, w0 + r * s) for c, s in ring])
    fore = mb.verts_lift([(end_u(front, w0 + r * s), v0 + r * c, w0 + r * s) for c, s in ring])
    for k in range(n):
        k1 = (k + 1) % n
        c, s = ring[k]
        mb.face((back[k], back[k1], fore[k1], fore[k]), style, (0.0, c, s))
    mb.face(tuple(back), style, (-1.0, 0.0, 0.0))
    mb.face(tuple(fore), style, (1.0, 0.0, 0.0))


# -- the hood --------------------------------------------------------------------------------------
HOOD_SHELL = [(1950, 4050), (1950, 4250), (1965, 4478), (2014, 4700), (2021, 4921), (2014, 5143), (1983, 5363),
              (1929, 5579), (1852, 5788), (1754, 5987), (1637, 6173), (1496, 6352), (1350, 6524), (1203, 6689),
              (1110, 6537), (0, 6537)]
PANEL_ZONE = (4478, 6352)      # the framed panels run between these heights on the curved shells


def panel_zone(h):
    """The heights (mm) between which the framed panels run on the curved shells (the spec's, after any lowering)."""
    return h.get("panelZone", PANEL_ZONE)


def hood(mb, h, lod, st):
    """The barrel hood: the shell profile (u, w) across the station, its ends raked in toward the top (spec
    "rake"), and on LOD0-1 the panel frame on both curved shells: ribs at the panel stations and strips along
    the panel zone's top and bottom. The panels between the outer ribs are tinted glass (the photographed
    terminals' window band) over a dark lining just inside, so the windows never show through the hood."""
    prof = [(m(u), m(w)) for u, w in h.get("shell", HOOD_SHELL)]
    if lod >= 2:
        keep = {0, 3, 5, 8, 11, 13, 14, len(prof) - 1} if lod == 2 else {0, 4, 9, 13, 14, len(prof) - 1}
        prof = [p for i, p in enumerate(prof) if i in keep]
    half = m(h["halfV"])
    rk0, rk = m(h["rakeFrom"]), h["rake"]

    def ve(w):
        return half - max(0.0, w - rk0) * rk

    outline = [(-u, w) for u, w in prof] + [(u, w) for u, w in reversed(prof[:-1])]
    # the outline runs from the -u bottom, over the crown, to the +u bottom; the bottom stays open. Each strip
    # is split at the outer ribs: the window band between them is glass inside the panel zone
    n = len(outline)
    ga, gb = m(h["ribs"][0]), m(h["ribs"][-1])
    pz = panel_zone(h)
    z0, z1 = m(pz[0]) - 1e-6, m(pz[1]) + 1e-6
    left = mb.verts_lift([(u, -ve(w), w) for u, w in outline])
    mid_a = mb.verts_lift([(u, ga, w) for u, w in outline])
    mid_b = mb.verts_lift([(u, gb, w) for u, w in outline])
    right = mb.verts_lift([(u, ve(w), w) for u, w in outline])
    for k in range(n - 1):
        (u0, w0), (u1, w1) = outline[k], outline[k + 1]
        nu, nw = -(w1 - w0), (u1 - u0)           # outward: left of travel (the outline runs over the top, -u to +u)
        window = z0 <= min(w0, w1) and max(w0, w1) <= z1 and lod <= 2
        for a0, a1, style in ((left, mid_a, st["hood"]), (mid_a, mid_b, st["glass"] if window else st["hood"]),
                              (mid_b, right, st["hood"])):
            mb.face((a0[k], a0[k + 1], a1[k + 1], a1[k]), style, (nu, 0.0, nw))
    mb.face(tuple(left), st["hood"], (0.0, -1.0, 0.0))
    mb.face(tuple(right), st["hood"], (0.0, 1.0, 0.0))
    bd = h["band"]   # the bottom band runs the full length, past the raked ends
    for sgn in (-1, 1):
        u0, u1 = sorted((sgn * m(bd["u"]), sgn * (m(bd["u"]) + m(bd["thick"]))))
        prims.box(mb, (u0, -m(bd["halfV"]), m(bd["bottom"])), (u1, m(bd["halfV"]), m(bd["top"])), st["hood"])
    if lod >= 2:
        return
    hood_lining(mb, prof, h, st)
    # panel frame: ribs across the curved shells and strips along the zone's edges, standing proud of the shell
    d, width = m(h["rib"]["height"]), m(h["rib"]["width"])
    zone = [(u, w) for u, w in prof if m(pz[0]) - 1e-6 <= w <= m(pz[1]) + 1e-6]
    axis_w = m(h["axisW"])                        # outward on the shells: away from the barrel's axis
    for sgn in (-1, 1):
        band = [(sgn * u, w) for u, w in zone]
        for vs in h["ribs"]:
            vc = m(vs)
            for k in range(len(band) - 1):
                (u0, w0), (u1, w1) = band[k], band[k + 1]
                t = Vector((u1 - u0, 0.0, w1 - w0))
                ln = t.length
                t.normalize()
                nrm = Vector((t.z, 0.0, -t.x))
                mid = Vector(((u0 + u1) / 2, vc, (w0 + w1) / 2))
                if nrm.dot(Vector((mid.x, 0.0, mid.z - axis_w))) < 0:
                    nrm = -nrm
                # the segments overlap at the bends, so their end faces are hidden: leave them out
                prims.obox(mb, tuple(mid + nrm * (d / 2)), (t, V, nrm), (ln / 2 + d / 2, width / 2, d / 2), st["hood"],
                           skip=("-0", "+0"))
        for (u0, w0) in (band[0], band[-1]):   # strips along the zone's bottom and top edges
            outward = Vector((sgn, 0.0, 0.0)) if w0 < m(pz[1]) - 1e-6 else Vector((sgn * 0.6, 0.0, 0.8)).normalized()
            c = Vector((u0, 0.0, w0)) + outward * (d / 2)
            lo_v, hi_v = m(h["ribs"][0]) - width / 2, m(h["ribs"][-1]) + width / 2
            prims.obox(mb, (c.x, (lo_v + hi_v) / 2, c.z), (outward, V, outward.cross(V).normalized()),
                       (d / 2, (hi_v - lo_v) / 2, width / 2), st["hood"])


def hood_lining(mb, prof, h, st, inset=0.08):
    """A dark shell just inside the hood over the window band and the crown, facing in, closed along its floor
    and at both ends inside the outer ribs: through the tinted glass it reads as the hood's dark interior."""
    floor = m(panel_zone(h)[0]) - 0.05
    half_prof = [(u, w) for u, w in prof if w >= floor]
    axis_w = m(h["axisW"])
    ring = []
    for i, (u, w) in enumerate(half_prof):   # offset toward the inside along the profile's normal
        (ua, wa), (ub, wb) = half_prof[max(0, i - 1)], half_prof[min(len(half_prof) - 1, i + 1)]
        ln = math.hypot(ub - ua, wb - wa) or 1.0
        nu, nw = (wb - wa) / ln, -(ub - ua) / ln
        if nu * (0.0 - u) + nw * (axis_w - w) < 0:
            nu, nw = -nu, -nw
        ring.append((max(0.0, u + nu * inset), w + nw * inset))
    ring = [(max(0.0, u), max(w, floor)) for u, w in ring]
    loop = [(-u, w) for u, w in ring] + [(u, w) for u, w in reversed(ring[:-1])]   # -u side, over the crown, +u side
    v0, v1 = m(h["ribs"][0]) - 0.1, m(h["ribs"][-1]) + 0.1
    dark = st["interior"].but(smooth=False)
    centre = (0.0, (floor + max(w for _, w in loop)) / 2)
    for (ua, wa), (ub, wb) in zip(loop, loop[1:]):
        nu, nw = (wb - wa), -(ub - ua)
        if nu * (centre[0] - (ua + ub) / 2) + nw * (centre[1] - (wa + wb) / 2) < 0:
            nu, nw = -nu, -nw
        mb.face(mb.verts_lift([(ua, v0, wa), (ua, v1, wa), (ub, v1, wb), (ub, v0, wb)]), dark, (nu, 0.0, nw))
    uf = loop[0][0]
    mb.face(mb.verts_lift([(uf, v0, floor), (-uf, v0, floor), (-uf, v1, floor), (uf, v1, floor)]), dark, (0.0, 0.0, 1.0))
    for v, facing in ((v0, 1.0), (v1, -1.0)):
        mb.face(mb.verts_lift([(u, v, w) for u, w in loop]), dark, (0.0, facing, 0.0))


# -- the bullwheel ---------------------------------------------------------------------------------
def wheel(mb, centre, wl, pitch_r, lod, st):
    """The bullwheel (spec common.wheel, mm): a flat web ring with flanges above and below the rope channel at
    its rim, tapered spokes and the hub; centre on the axle at rope elevation."""
    c = Vector(centre)
    t = m(wl["thick"])
    ro, rw, ri = m(wl["outer"]) / 2, m(wl["web"]) / 2, m(wl["inner"]) / 2
    fl = m(wl["flange"])
    n = parts.sides(lod, 64, 40, 20, 8)
    dark = st["dark"]
    if lod >= 3:
        prims.lathe(mb, c, W, [(ri, -t / 2), (ro, -t / 2), (ro, t / 2), (ri, t / 2)], n, dark)
        return
    prims.lathe(mb, c, W, [(ri, -t / 2), (rw, -t / 2), (rw, t / 2), (ri, t / 2)], n, dark)   # web ring
    for s in (-1, 1):   # flanges either side of the rope channel
        a, b = sorted((s * t / 2, s * (t / 2 - fl)))
        prims.lathe(mb, c, W, [(rw, a), (ro, a), (ro, b), (rw, b)], n, dark)
    if lod == 0:   # the channel's liner, where the rope runs
        prims.lathe(mb, c, W, [(rw, -(t / 2 - fl)), (pitch_r - 0.02, -(t / 2 - fl) + 0.01), (pitch_r - 0.02, t / 2 - fl - 0.01), (rw, t / 2 - fl)],
                    n, st["rubber"], closed=False)
    hub_r = m(wl["hubDia"]) / 2
    w0, w1 = m(wl["spokeHub"]) / 2, m(wl["spokeRim"]) / 2
    for i in range(wl["spokes"]):
        a = 2 * math.pi * i / wl["spokes"]
        d = U * math.cos(a) + V * math.sin(a)
        p = W.cross(d).normalized()
        poly = [(hub_r * 0.9, -w0), (ri + 0.02, -w1), (ri + 0.02, w1), (hub_r * 0.9, w0)]
        prims.prism(mb, poly, c - W * (t / 2), d, p, W, t, dark)


# -- the build -------------------------------------------------------------------------------------
def build(spec, lod, stage):
    c, d = spec["common"], spec["drive"]
    st = styles(lod)
    a = Asset("monta_fg4_drive")
    body = MeshBuilder()
    rope = m(d["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    sv = parts.metres(c["sheave"], keep=())
    sheave_sides = parts.sides(lod, 14, 10, 6, 4)
    ntube = parts.sides(lod, 20, 12, 8, 6)

    # piers (footing and block, LP7) under the legs
    for p in d["piers"]:
        prims.box(body, mv(p["footing"]["lo"]), mv(p["footing"]["hi"]), st["concrete"])
        prims.box(body, mv(p["block"]["lo"]), mv(p["block"]["hi"]), st["concrete"], skip=("-2",))

    # legs: leaning tubes on gusseted base plates, saddles and saddle plates cradling the booms
    for lg in d["legs"]:
        oval_leg(body, lg, ntube, st["steel"].but(smooth=True))
        if lod >= 3:
            continue
        bp = lg["basePlate"]
        prims.box(body, mv(bp["lo"]), mv(bp["hi"]), st["steel"])
        loft(body, lg["saddle"], st["steel"])
        box_from(body, lg["saddlePlate"], st)
        if lod <= 1:
            for g in lg["gussets"]:
                box_from(body, g, st)

    # booms: round tubes with parallel slanted end cuts
    for bm in d["booms"]:
        slanted_tube(body, m(bm["v"]), m(bm["w"]), m(bm["dia"]) / 2, (m(bm["rear"][0]), m(bm["rear"][1])),
                     (m(bm["front"][0]), m(bm["front"][1])), ntube, st["steel"].but(smooth=True))

    # struts: round, cut level at both ends (the same cut tube as the legs)
    for sr in d["struts"]:
        oval_leg(body, sr, parts.sides(lod, 16, 10, 8, 6), st["steel"].but(smooth=True))

    # cross tubes and pins; plates, rails, ledges, cross beams and connectors
    small = {"topGusset", "strutPlate", "rearBracket", "boomLedge"}
    for t in d["tubes"]:
        if t["name"] == "pin" and lod >= 2:
            continue
        tube_from(body, t, st, parts.sides(lod, 16, 10, 8, 6) if t["name"] != "pin" else parts.sides(lod, 10, 8))
    for b in d["boxes"]:
        if (b["name"] in small and lod >= 2) or (lod >= 3 and b["name"] != "crossBeam"):
            continue
        if b["name"] == "topGusset" and lod >= 1:
            continue
        box_from(body, b, st)
    if lod <= 1:   # vertical pins on bases
        for p in d["posts"]:
            (u, v), bot, top = p["c"], p["bottom"], p["top"]
            prims.cylinder(body, mv((u, v, bot)), mv((u, v, bot + p["baseH"])), m(p["dia"]) / 2, 10, st["steel"])
            prims.cylinder(body, mv((u, v, bot + p["baseH"])), mv((u, v, top)), m(p["pinDia"]) / 2, 8, st["rod"])

    # tension cylinders between the booms and the carriage
    for cy in d["cylinders"]:
        u0, u1 = cy["u"]
        vc, wc = cy["v"], cy["w"]
        if lod <= 2:
            prims.cylinder(body, mv((cy["barrelFrom"], vc, wc)), mv((u1, vc, wc)), m(cy["barrelDia"]) / 2, parts.sides(lod, 14, 10, 8), st["steel"])
            prims.cylinder(body, mv((u0, vc, wc)), mv((cy["barrelFrom"], vc, wc)), m(cy["rodDia"]) / 2, parts.sides(lod, 10, 8, 6), st["rod"])
        if lod <= 1:
            for pl in cy["plates"]:
                box_from(body, pl, st)
            box_from(body, cy["clevis"], st)

    # the bullwheel carriage: plates, the front piece, the hub, and the C-frames open around the wheel
    car = d["carriage"]
    for b in (car["boxes"] if lod <= 2 else []):
        box_from(body, b, st)
    if lod <= 2:
        cframes(body, car["cframes"], st, lod)
    hb = car["hub"]
    if lod <= 2:
        prims.cylinder(body, (0.0, 0.0, m(hb["bottom"])), (0.0, 0.0, m(hb["top"])), m(hb["dia"]) / 2, parts.sides(lod, 20, 12, 8), st["rod"])

    # the bullwheel, pivoted on its axle at rope elevation
    centre = Vector((0.0, 0.0, rope))
    if lod <= 2:
        wb = MeshBuilder()
        wheel(wb, centre, c["wheel"], hg, lod, st)
        a.parts["bullwheel"] = wb
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        wheel(body, centre, c["wheel"], hg, lod, st)

    # guide sheaves on brackets and cross bars from the carriage (rope rides on top)
    for k, g in enumerate(d["guideSheaves"]):
        centre_g = Vector(mv((g["u"], g["v"], g["w"])))
        if lod <= 2:
            target = MeshBuilder() if lod <= 1 else body
            parts.line_sheave(target, centre_g, V, sv, min(lod, 2), st, sheave_sides, st["red"])
            if lod <= 1:
                name = f"sheave_g{'l' if g['v'] < 0 else 'r'}"
                a.parts[name] = target
                a.pivots[name] = {"pos": tuple(centre_g), "axis": (0.0, 1.0, 0.0)}
            box_from(body, g["bracket"], st)
            lo, hi = g["bar"]["lo"], g["bar"]["hi"]
            if g["v"] < 0:
                lo = (lo[0], min(lo[1], g["barReach"]), lo[2])
            else:
                hi = (hi[0], max(hi[1], g["barReach"]), hi[2])
            prims.box(body, mv(lo), mv(hi), st["steel"])

    # the hood over the machinery
    hb_ = MeshBuilder()
    hood(hb_, d["hood"], lod, st)
    lo, hi = hb_.bounds_lift()
    a.dims.update({"hoodLength": round((hi[1] - lo[1]) * 1000), "hoodDepth": round((hi[0] - lo[0]) * 1000), "hoodTop": round(hi[2] * 1000)})
    body.merge(hb_)

    # exit trains: three trains of four per rope under a carrier beam and a link hung from the front cross tube
    ex = d["exit"]
    left = ex["trains"]["left"]
    rows = [s for tr in left["sheaves"] for s in tr]
    n_row = len(rows)
    dv_side = {-1: ex["trains"]["left"]["shiftV"], 1: ex["trains"]["right"]["shiftV"]}
    for side in (-1, 1):
        for k, (u, w) in enumerate(rows):
            centre_s = Vector((m(u), side * hg, m(w)))
            face = parts.row_face(k, n_row, st)
            if lod <= 2:
                target = MeshBuilder() if lod <= 1 else body
                parts.line_sheave(target, centre_s, V, sv, min(lod, 2), st, sheave_sides, face)
                if lod <= 1:
                    name = f"sheave_{'l' if side < 0 else 'r'}{k + 1}"
                    a.parts[name] = target
                    a.pivots[name] = {"pos": tuple(centre_s), "axis": (0.0, 1.0, 0.0)}
        if lod >= 3:   # far out: one bar along each row
            p0, p1 = Vector((m(rows[0][0]), side * hg, m(rows[0][1]))), Vector((m(rows[-1][0]), side * hg, m(rows[-1][1])))
            dd = (p1 - p0).normalized()
            nn = dd.cross(V).normalized()
            prims.obox(body, tuple((p0 + p1) / 2), (dd, V, nn), ((p1 - p0).length / 2 + 0.2, 0.08, 0.2), st["steel"])
    if lod <= 2:
        for b in ex["boxes"]:
            side = -1 if b["c"][1] < 0 else 1
            if b["name"] in ("trainPlate", "clamp") and lod >= 2:
                continue
            if b["name"] == "trainPlate":
                if side < 0:   # the left trains' plates, and their mirror image for the right rope
                    box_from(body, b, st)
                    box_from(body, b, st, mirror=True)
            elif b["name"] == "carrier":
                box_from(body, b, st)
            elif b["name"] == "clamp" and (lod <= 1 or b["h"][0] > 150):
                box_from(body, b, st, shift=(0.0, dv_side[side], 0.0))
        if lod == 0:
            for p in ex["nuts"]:
                side = -1 if p[1] < 0 else 1
                q = (p[0], p[1] + dv_side[side], p[2])
                prims.cylinder(body, mv((q[0] - 15, q[1], q[2])), mv((q[0] + 15, q[1], q[2])), 0.02, 6, st["rod"])

    # the rope: level from the wheel to the first exit sheave (hold), leaving under the last one (out)
    rr = m(c["sheave"]["grooveBottom"]) / 2 + m(c["ropeDiameter"]) / 2
    (u_a, w_a), (u_b, w_b) = rows[-2], rows[-1]
    ang = math.atan2(w_b - w_a, u_b - u_a)
    u_hold = m(rows[0][0])
    u_out, w_out = m(u_b) + rr * math.sin(ang), m(w_b) - rr * math.cos(ang)
    a.body = body
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_hold": (u_hold, -hg, rope), "rope_right_hold": (u_hold, hg, rope),
        "rope_left_out": (u_out, -hg, w_out), "rope_right_out": (u_out, hg, w_out),
        "chair_load": (m(d["loadU"]), hg, rope),
        "foundation_base": (0.0, 0.0, m(min(p["footing"]["lo"][2] for p in d["piers"]))),
    }
    a.dims.update({"rope": d["ropeElevation"], "ropeExitDeg": round(math.degrees(ang), 2), "exitSheaves": n_row,
                   "boomSpacing": round(d["booms"][1]["v"] - d["booms"][0]["v"])})
    return a
