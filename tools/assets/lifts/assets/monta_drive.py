"""Monta FG4 drive terminal (bottom station), a recreation from the owner's reference model's dimensions
(monta_fg4.json "drive", measured locally; see its "_adjust" notes): two round booms on leaning tubular legs over
concrete piers, a machinery frame and tension cylinders between them, the bullwheel on its carriage, a raked
barrel hood with framed panels, and the line's hold-down trains hung from a raised front cross tube.

The rope runs level from the bullwheel over the guide sheaves and leaves climbing under the exit trains (three
trains of four per rope). Loaded chairs leave on the right rope (v+). Millimetres in the spec; the lift frame's
origin is on the bullwheel's axle at grade, u toward the line.

Posable (task P3-07, the Monta's trains): each rope's exit row is the reference's equaliser tree, the long carrier on
the clamp's pin over a train and the link bar, the link bar over the other two trains (exit_pivots), so the game can
pose the row to the rope. The guide sheaves stay single wheels.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import GLASS, MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "terminal"
CHAIR = "chairworks_chair:fixed"   # the chair the build sends round the bullwheel (liftkit/fit.py fixed; polish)
LODS = 4
SPEC = "monta_fg4.json"


def m(x):
    return x / 1000.0


def mv(p):
    return tuple(m(x) for x in p)


def gauge_shift(c):
    """How far (mm) each rope's parts move out from where the spec measured them: the spec's positions are at the
    reference's gauge (common.referenceGauge), the lift runs at common.lineGauge (the owner, task P3-09: the
    Sessellift's, so both lifts share one tower head kit). Negative moves them in toward the centreline; a part on
    side s (-1 left, +1 right) moves s * gauge_shift in v."""
    return (c["lineGauge"] - c.get("referenceGauge", c["lineGauge"])) / 2


def gauge_wheel(c):
    """The bullwheel at the line gauge: its rim (outer, web and inner diameters) scaled from the reference's gauge;
    hub, spokes, thickness and flanges as measured."""
    k = c["lineGauge"] / c.get("referenceGauge", c["lineGauge"])
    return dict(c["wheel"], **{key: c["wheel"][key] * k for key in ("outer", "web", "inner")})


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
        "inside": Style("paint_grey", snow=0.0),   # under the hood (polish)
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
    rings, pts = [], []
    for wz in (m(lg["bottomW"]), m(lg["topW"])):
        ring = []
        for k in range(n):
            th = 2 * math.pi * k / n + math.pi / n
            q = c + e1 * (a1 * math.cos(th)) + e2 * (a2 * math.sin(th))
            ring.append(tuple(q + ax * ((wz - q.z) / ax.z)))
        rings.append(mb.verts_lift(ring))
        pts.append(ring)
    for k in range(n):
        k1 = (k + 1) % n
        th = 2 * math.pi * (k + 0.5) / n + math.pi / n
        out = e1 * (math.cos(th) / a1) + e2 * (math.sin(th) / a2)
        mb.face((rings[0][k], rings[0][k1], rings[1][k1], rings[1][k]), style, tuple(out))
    caps = style.but(smooth=False)   # fans, not n-gons: no AO line across the cut ends (polish)
    for ring, out in ((pts[0], (0.0, 0.0, -1.0)), (pts[1], (0.0, 0.0, 1.0))):
        prims.fan(mb, ring, tuple(sum((Vector(p) for p in ring), Vector()) / len(ring)), caps, out)


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
    back_pts = [(end_u(rear, w0 + r * s), v0 + r * c, w0 + r * s) for c, s in ring]
    fore_pts = [(end_u(front, w0 + r * s), v0 + r * c, w0 + r * s) for c, s in ring]
    back, fore = mb.verts_lift(back_pts), mb.verts_lift(fore_pts)
    for k in range(n):
        k1 = (k + 1) % n
        c, s = ring[k]
        mb.face((back[k], back[k1], fore[k1], fore[k]), style, (0.0, c, s))
    caps = style.but(smooth=False)   # fans: no AO line across the slanted cuts (polish)
    for pts_, out in ((back_pts, (-1.0, 0.0, 0.0)), (fore_pts, (1.0, 0.0, 0.0))):
        prims.fan(mb, pts_, tuple(sum((Vector(p) for p in pts_), Vector()) / len(pts_)), caps, out)


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
    shell = st["hood"].but(trim="gloss", group="shell", crease=35.0)   # polish: one smooth barrel with its glass, no
    glass = st["glass"].but(group="shell", crease=35.0)                # 1 m seam grid
    flat = st["hood"].but(trim="gloss")
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
        window = z0 <= min(w0, w1) and max(w0, w1) <= z1   # every LOD (polish: LOD3 had no glass)
        for a0, a1, style in ((left, mid_a, shell), (mid_a, mid_b, glass if window else shell), (mid_b, right, shell)):
            mb.face((a0[k], a0[k + 1], a1[k + 1], a1[k]), style, (nu, 0.0, nw))
    for side, vs in ((-1.0, [(u, -ve(w), w) for u, w in outline]), (1.0, [(u, ve(w), w) for u, w in outline])):
        prims.fan(mb, vs, tuple(sum((Vector(p) for p in vs), Vector()) / len(vs)), flat, (0.0, side, 0.0))   # polish
    w_lo = min(w for _, w in outline)   # the soffit closes the hood's underside (polish: it was open onto the shell's back)
    u_lo = max(abs(u) for u, w in outline if w <= w_lo + 1e-6)
    mb.face(mb.verts_lift([(-u_lo, -ve(w_lo), w_lo), (u_lo, -ve(w_lo), w_lo), (u_lo, ve(w_lo), w_lo), (-u_lo, ve(w_lo), w_lo)]),
            st["interior"].but(smooth=False), (0.0, 0.0, -1.0))
    bd = h["band"]   # the bottom band runs the full length, past the raked ends
    for sgn in (-1, 1):
        u0, u1 = sorted((sgn * m(bd["u"]), sgn * (m(bd["u"]) + m(bd["thick"]))))
        prims.box(mb, (u0, -m(bd["halfV"]), m(bd["bottom"])), (u1, m(bd["halfV"]), m(bd["top"])), flat)
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
                prims.obox(mb, tuple(mid + nrm * (d / 2)), (t, V, nrm), (ln / 2 + d / 2, width / 2, d / 2), flat.but(snow=0.3),
                           skip=("-0", "+0"))
        for (u0, w0) in (band[0], band[-1]):   # strips along the zone's bottom and top edges
            outward = Vector((sgn, 0.0, 0.0)) if w0 < m(pz[1]) - 1e-6 else Vector((sgn * 0.6, 0.0, 0.8)).normalized()
            c = Vector((u0, 0.0, w0)) + outward * (d / 2)
            lo_v, hi_v = m(h["ribs"][0]) - width / 2, m(h["ribs"][-1]) + width / 2
            prims.obox(mb, (c.x, (lo_v + hi_v) / 2, c.z), (outward, V, outward.cross(V).normalized()),
                       (d / 2, (hi_v - lo_v) / 2, width / 2), flat.but(snow=0.3))


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
def wheel(mb, centre, wl, pitch_r, lod, st, rope_r=0.021):
    """The bullwheel (spec common.wheel, mm): a flat web ring with flanges above and below the rope channel at
    its rim, tapered spokes and the hub; centre on the axle at rope elevation. The channel's liner sits 1 mm into
    the rope (radius rope_r, metres) on its pitch circle."""
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
        prims.lathe(mb, c, W, [(rw, -(t / 2 - fl)), (pitch_r - rope_r + 0.001, -(t / 2 - fl) + 0.01), (pitch_r - rope_r + 0.001, t / 2 - fl - 0.01), (rw, t / 2 - fl)],
                    n, st["rubber"], closed=False)
    hub_r = m(wl["hubDia"]) / 2
    w0, w1 = m(wl["spokeHub"]) / 2, m(wl["spokeRim"]) / 2
    for i in range(wl["spokes"]):
        a = 2 * math.pi * i / wl["spokes"]
        d = U * math.cos(a) + V * math.sin(a)
        p = W.cross(d).normalized()
        poly = [(hub_r * 0.9, -w0), (ri + 0.02, -w1), (ri + 0.02, w1), (hub_r * 0.9, w0)]
        prims.prism(mb, poly, c - W * (t / 2), d, p, W, t, dark)


# -- the exit trains' pivots -----------------------------------------------------------------------
def exit_pivots(a, ex, rows, hg, gs=0.0):
    """The exit rows' equaliser tree on both ropes (spec drive.exit.pivots; contract P3-07 M2): eq_<s> (the long
    carrier, on the clamp's pin) carries one train's yoke and eq_<s>2 (the link bar, on its hanger block's pin), which
    carries the other two; yoke_<s>1..3 each carry two rockers rocker_<s>1..6 (pinned at their plates' middle), each
    two sheaves sheave_<s>1..12, numbered along +u from the bullwheel as before. Every pin has its stop (spec
    pinStops). Returns the plate classifier (a left train plate -> the left part it rides with) and the pins."""
    pv = ex["pivots"]
    stops = pv["pinStops"]
    plates = [b for b in ex["boxes"] if b["name"] == "trainPlate" and b["c"][1] < 0]
    trains = ex["trains"]["left"]["sheaves"]
    rocker_u = [(rows[2 * i][0] + rows[2 * i + 1][0]) / 2 for i in range(len(rows) // 2)]
    yoke_u = [sum(u for u, _ in tr) / len(tr) for tr in trains]

    def nearest(us, u):
        return min(range(len(us)), key=lambda i: abs(us[i] - u))

    def plate_part(b):
        if b["h"][0] < 400:
            return f"rocker_l{nearest(rocker_u, b['c'][0]) + 1}"
        return f"yoke_l{nearest(yoke_u, b['c'][0]) + 1}"

    def plate_w(part):
        ws = [b["c"][2] for b in plates if plate_part(b) == part]
        return sum(ws) / len(ws)

    pins = []
    for side in (-1, 1):
        sd = "l" if side < 0 else "r"
        tr = pv["left" if side < 0 else "right"]
        v = side * hg

        def pivot(name, u, w, parent, stop, kind):
            a.pivots[name] = {"pos": (m(u), v, m(w)), "axis": (0.0, 1.0, 0.0), "parent": parent, "stop": stops[stop]}
            if kind:
                pins.append((name + "#pin" if kind != "carrier" else "#pin", side, m(u), m(w), kind))
        pivot(f"eq_{sd}", *tr["carrier"], None, "carrier", "carrier")
        pivot(f"eq_{sd}2", *tr["link"], f"eq_{sd}", "link", "link")
        for t in range(len(trains)):
            yk = f"yoke_l{t + 1}"
            yoke_u_plates = [b["c"][0] for b in plates if plate_part(b) == yk]
            pivot(f"yoke_{sd}{t + 1}", sum(yoke_u_plates) / len(yoke_u_plates), plate_w(yk),
                  f"eq_{sd}" if t + 1 == tr["onCarrier"] else f"eq_{sd}2", "yoke", "yoke")
        for i, u in enumerate(rocker_u):
            pivot(f"rocker_{sd}{i + 1}", u, plate_w(f"rocker_l{i + 1}"), f"yoke_{sd}{i // 2 + 1}", "rocker", "rocker")
        for k, (u, w) in enumerate(rows):
            a.pivots[f"sheave_{sd}{k + 1}"] = {"pos": (m(u), v, m(w)), "axis": (0.0, 1.0, 0.0), "parent": f"rocker_{sd}{k // 2 + 1}"}
    pin_v = {k: [x + gs for x in span] for k, span in pv["pinV"].items() if not k.startswith("_")}
    return {"plate": plate_part, "pins": pins, "dia": pv["pinDia"], "pinV": pin_v}


def settle_rows(a, mb, deg, lod):
    """Settles each rope's exit row to the rope (the owner, P3-07 Monta trains: the reference's rows step 0, 5 and 10
    degrees, so a rope leaving at its 10 degrees cut into the middle sheaves): the tree is posed for a rope coming in
    level and leaving at `deg`, sharing the turn equally with the first sheave held at the bullwheel's height
    (liftkit/pose.py), and that pose becomes the rest pose. Each face moves with its part, every pivot down the tree
    moves with its parents; then the faces' tags fold into the LOD's parts: sheaves ride in their rocker at LOD1, and
    at LOD2-3 each side's whole tree is one part under eq_<s>."""
    from liftkit import frame, pose
    angles = {}
    for sd in "lr":
        row = sorted((k for k in pose.subtree(a.pivots, f"eq_{sd}") if k.startswith("sheave_")), key=lambda k: a.pivots[k]["pos"][0])
        ang, err = pose.solve(a.pivots, f"eq_{sd}", [row], 0.0, math.radians(deg), anchor=(row[0], a.pivots[row[0]]["pos"][2]))
        if err > 1e-5:
            raise ValueError(f"monta_fg4_drive: the {sd} row can't hold its first sheave level ({err * 1000:.2f} mm)")
        angles.update(ang)
    done = {}
    for f, tag in zip(mb.faces, mb.tags):
        part = (tag or "").split("#")[0]
        if part not in a.pivots:
            continue
        for i in f:
            if i in done:
                if done[i] != part:
                    raise ValueError(f"monta_fg4_drive: a vertex is shared by {done[i]} and {part}")
                continue
            done[i] = part
            u, v, w = frame.lift(mb.verts[i])
            pu, pw = pose.carry(a.pivots, angles, part, (u, w))
            mb.verts[i] = frame.b((float(pu), v, float(pw)))
    moved = {k: pose.place(a.pivots, angles, k) for k in a.pivots if k in angles or k.startswith("sheave_")}
    for k, (pu, pw) in moved.items():
        p = a.pivots[k]["pos"]
        a.pivots[k]["pos"] = (float(pu), p[1], float(pw))

    def fold(part):
        if lod >= 2:
            return f"eq_{part.split('_')[1][0]}"
        if lod == 1 and part.startswith("sheave_"):
            return a.pivots[part]["parent"]
        return part
    mb.tags = [t if (t or "").split("#")[0] not in a.pivots else fold(t.split("#")[0]) + (t[t.index("#"):] if "#" in t else "")
               for t in mb.tags]


def exit_pins(mb, tree, lod, st):
    """The exit trees' pins across the rope's plane: the carrier's through the clamp (a piece of the body, LOD0-1), the
    link bar's, the yokes' and the rockers' (pieces of their parts, LOD0), each its own piece for the clash sweep."""
    for tag, side, u, w, kind in tree["pins"]:
        if lod >= 2 or (lod == 1 and kind != "carrier"):
            continue
        v0, v1 = sorted(side * m(x) for x in tree["pinV"][kind])
        with mb.grouped(tag):
            prims.cylinder(mb, (u, v0, w), (u, v1, w), m(tree["dia"][kind]) / 2, 10 if kind == "carrier" else 8, st["rod"])


# -- the build -------------------------------------------------------------------------------------
def build(spec, lod, stage):
    c, d = spec["common"], spec["drive"]
    st = styles(lod)
    a = Asset("monta_fg4_drive")
    body = MeshBuilder()
    rope = m(d["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    gs = gauge_shift(c)
    sv = parts.metres(c["sheave"], keep=())
    sheave_sides = parts.sides(lod, 14, 10, 6, 4)
    ntube = parts.sides(lod, 20, 12, 8, 6)

    # piers (footing and block, LP7) under the legs
    for p in d["piers"]:
        if lod <= 2:   # polish: far out the buried footing is left out
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
            prims.cylinder(body, mv((u, v, bot)), mv((u, v, bot + p["baseH"])), m(p["dia"]) / 2, 10, st["steel"], fan_caps=True)
            prims.cylinder(body, mv((u, v, bot + p["baseH"])), mv((u, v, top)), m(p["pinDia"]) / 2, 8, st["rod"], fan_caps=True)

    sti = dict(st, steel=st["inside"])   # polish: what stands under the hood takes no snow
    # tension cylinders between the booms and the carriage
    for cy in d["cylinders"]:
        u0, u1 = cy["u"]
        vc, wc = cy["v"], cy["w"]
        if lod <= 2:
            prims.cylinder(body, mv((cy["barrelFrom"], vc, wc)), mv((u1, vc, wc)), m(cy["barrelDia"]) / 2, parts.sides(lod, 14, 10, 8), sti["steel"])
            prims.cylinder(body, mv((u0, vc, wc)), mv((cy["barrelFrom"], vc, wc)), m(cy["rodDia"]) / 2, parts.sides(lod, 10, 8, 6), st["rod"])
        if lod <= 1:
            for pl in cy["plates"]:
                box_from(body, pl, sti)
            box_from(body, cy["clevis"], sti)

    # the bullwheel carriage: plates, the front piece, the hub, and the C-frames open around the wheel
    car = d["carriage"]
    for b in (car["boxes"] if lod <= 2 else []):
        box_from(body, b, sti)
    if lod <= 2:
        cframes(body, car["cframes"], sti, lod)
    hb = car["hub"]
    if lod <= 2:
        prims.cylinder(body, (0.0, 0.0, m(hb["bottom"])), (0.0, 0.0, m(hb["top"])), m(hb["dia"]) / 2, parts.sides(lod, 20, 12, 8), st["rod"],
                       fan_caps=True)

    # the bullwheel, pivoted on its axle at rope elevation
    centre = Vector((0.0, 0.0, rope))
    if lod <= 2:
        wb = MeshBuilder()
        wheel(wb, centre, gauge_wheel(c), hg, lod, st, m(c["ropeDiameter"]) / 2)
        a.parts["bullwheel"] = wb
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    # LOD3: no wheel (polish: it sat under the hood where nobody sees it)

    # guide sheaves on brackets and cross bars from the carriage (rope rides on top)
    for k, g in enumerate(d["guideSheaves"]):
        dv = (-1 if g["v"] < 0 else 1) * gs          # each sheave, its bracket and its bar's outer end move to the gauge
        centre_g = Vector(mv((g["u"], g["v"] + dv, g["w"])))
        if lod <= 2:
            target = MeshBuilder() if lod <= 1 else body
            parts.line_sheave(target, centre_g, V, sv, min(lod, 2), st, sheave_sides, st["red"])
            if lod <= 1:
                name = f"sheave_g{'l' if g['v'] < 0 else 'r'}"
                a.parts[name] = target
                a.pivots[name] = {"pos": tuple(centre_g), "axis": (0.0, 1.0, 0.0)}
            box_from(body, g["bracket"], sti, shift=(0.0, dv, 0.0))
            lo, hi = g["bar"]["lo"], g["bar"]["hi"]
            if g["v"] < 0:
                lo = (lo[0], min(lo[1], g["barReach"]) + dv, lo[2])
            else:
                hi = (hi[0], max(hi[1], g["barReach"]) + dv, hi[2])
            prims.box(body, mv(lo), mv(hi), sti["steel"])

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
    tree = exit_pivots(a, ex, rows, hg, gs)
    for side in (-1, 1):   # faces carry their finest part here; settle_rows folds them into each LOD's parts
        sd = "l" if side < 0 else "r"
        for k, (u, w) in enumerate(rows):
            centre_s = Vector((m(u), side * hg, m(w)))
            face = parts.row_face(k, n_row, st)
            if lod <= 2:
                with body.grouped(f"sheave_{sd}{k + 1}"):
                    parts.line_sheave(body, centre_s, V, sv, min(lod, 2), st, sheave_sides, face)
    if lod <= 2:
        for b in ex["boxes"]:
            side = -1 if b["c"][1] < 0 else 1
            sd = "l" if side < 0 else "r"
            if b["name"] in ("trainPlate", "clamp") and lod >= 2:
                continue
            if b["name"] == "trainPlate":
                if side < 0:   # the left trains' plates, and their mirror image for the right rope
                    part = tree["plate"](b)
                    for s2, mirror in (("l", False), ("r", True)):
                        with body.grouped(part.replace("_l", "_" + s2, 1)):
                            box_from(body, b, st, shift=(0.0, -gs, 0.0), mirror=mirror)
            elif b["name"] == "carrier":   # the long carrier and its hanger blocks swing on the clamp; the link bar on its block
                with body.grouped(f"eq_{sd}" if b["h"][0] < 1000 or b["h"][0] > 1500 else f"eq_{sd}2"):
                    box_from(body, b, st, shift=(0.0, side * gs, 0.0))
            elif b["name"] == "clamp" and (lod <= 1 or b["h"][0] > 150):
                box_from(body, b, st, shift=(0.0, dv_side[side] + side * gs, 0.0))
        exit_pins(body, tree, lod, st)
        if lod == 0:
            for p in ex["nuts"]:
                side = -1 if p[1] < 0 else 1
                q = (p[0], p[1] + dv_side[side] + side * gs, p[2])
                prims.cylinder(body, mv((q[0] - 15, q[1], q[2])), mv((q[0] + 15, q[1], q[2])), 0.02, 6, st["rod"])
    exit_deg = ex["pivots"]["restExitDeg"]
    settle_rows(a, body, exit_deg, lod)
    if lod >= 3:   # far out: one bar along each settled row
        for side in (-1, 1):
            sd = "l" if side < 0 else "r"
            q0, q1 = a.pivots[f"sheave_{sd}1"]["pos"], a.pivots[f"sheave_{sd}{n_row}"]["pos"]
            p0, p1 = Vector(q0), Vector(q1)
            dd = (p1 - p0).normalized()
            nn = dd.cross(V).normalized()
            with body.grouped(f"eq_{sd}"):
                prims.obox(body, tuple((p0 + p1) / 2), (dd, V, nn), ((p1 - p0).length / 2 + 0.2, 0.08, 0.2), st["steel"])

    # the rope: level from the wheel to the first exit sheave (hold), leaving under the last one (out) at the angle the
    # rows are settled to
    rr = m(c["sheave"]["grooveBottom"]) / 2 + m(c["ropeDiameter"]) / 2
    ang = math.radians(exit_deg)
    u_hold = m(rows[0][0])
    out = {}
    for sd in "lr":
        u_b, v_b, w_b = a.pivots[f"sheave_{sd}{n_row}"]["pos"]
        out[sd] = (u_b + rr * math.sin(ang), v_b, w_b - rr * math.cos(ang))
    a.body = body
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_hold": (u_hold, -hg, rope), "rope_right_hold": (u_hold, hg, rope),
        "rope_left_out": out["l"], "rope_right_out": out["r"],
        "chair_load": (m(d["loadU"]), hg, rope),
        "foundation_base": (0.0, 0.0, m(min(p["footing"]["lo"][2] for p in d["piers"]))),
    }
    a.dims.update({"rope": d["ropeElevation"], "ropeExitDeg": exit_deg, "exitSheaves": n_row,
                   "boomSpacing": round(d["booms"][1]["v"] - d["booms"][0]["v"])})
    return a
