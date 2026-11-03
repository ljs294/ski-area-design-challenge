"""Monta FG4 bottom return terminal (drive at either end, owner 2026-11-02): the return for a Monta lift driven from
the top. Built from the owner's photos of the type and the kits (spec monta_fg4.json "returnBottom", millimetres):
all its steel rectangular HSS with rounded corners (owner, findings 2, 5 and 7): one leaning column on a concrete pad,
a saddle on its top under a frame of two parallel tubes (not a block), the return's carriage and exposed bullwheel on
the tubes, and toward the line a cross tube on
them carrying, on each rope, the kit 8-sheave depression assembly the Sessellift's bottom return uses
(parts.line_assembly on the station arc, levelled at its first sheave), its main pin on two lug plates rising from the
cross tube (owner, findings 1-2: as many hold-down sheaves as the Sessellift's bases, held from the frame below,
rounded edges). The rope runs level from the wheel through loading and leaves climbing.

Loaded chairs leave on the right rope (v+); riders sit down at the drive's load point. The lift frame's origin is on
the bullwheel's axle at the station's grade, u toward the line.
"""
import math

from mathutils import Vector

from liftkit import heads, parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder
from liftkit.prims import U, V, W

import monta_return
from monta_drive import box_from, m, mv, pier_sockets

KIND = "terminal"
CHAIR = "chairworks_chair:fixed"   # the chair the build sends round the bullwheel (liftkit/fit.py fixed)
LODS = 4
SPEC = "monta_fg4.json"


def rounded(a0, b0, a1, b1, r, steps):
    """A rectangle (a0, b0)-(a1, b1) with its corners rounded to radius r in `steps` segments each (square at 0)."""
    if steps == 0 or r <= 0:
        return [(a0, b0), (a1, b0), (a1, b1), (a0, b1)]
    pts = []
    for (ca, cb), start in (((a1 - r, b0 + r), -90), ((a1 - r, b1 - r), 0), ((a0 + r, b1 - r), 90), ((a0 + r, b0 + r), 180)):
        for k in range(steps + 1):
            t = math.radians(start + 90 * k / steps)
            pts.append((ca + r * math.cos(t), cb + r * math.sin(t)))
    return pts


def hss_leg(mb, base, top, a1, a2, r, steps, style):
    """A leaning rectangular tube (lift frame, metres): its centreline from base (u, w) to top (u, w) at v = 0, its
    section 2*a1 across the line by 2*a2 square to the lean with corners of radius r, cut level at both ends."""
    ax = Vector((top[0] - base[0], 0.0, top[1] - base[1])).normalized()
    e1, e2 = Vector((0.0, 1.0, 0.0)), Vector((-ax.z, 0.0, ax.x))
    c = Vector((base[0], 0.0, base[1]))
    ring2 = rounded(-a1, -a2, a1, a2, r, steps)
    rings, pts = [], []
    for wz in (base[1], top[1]):
        ring = []
        for x, y in ring2:
            q = c + e1 * x + e2 * y
            ring.append(tuple(q + ax * ((wz - q.z) / ax.z)))
        rings.append(mb.verts_lift(ring))
        pts.append(ring)
    n = len(ring2)
    for k in range(n):
        k1 = (k + 1) % n
        (x0, y0), (x1, y1) = ring2[k], ring2[k1]
        mb.face((rings[0][k], rings[0][k1], rings[1][k1], rings[1][k]), style, tuple(e1 * (y1 - y0) - e2 * (x1 - x0)))
    for ring, out in ((pts[0], (0.0, 0.0, -1.0)), (pts[1], (0.0, 0.0, 1.0))):
        prims.fan(mb, ring, tuple(sum((Vector(p) for p in ring), Vector()) / len(ring)), style, out)


def build(spec, lod, stage):
    c, r, rb = spec["common"], spec["return"], spec["returnBottom"]
    st = monta_return.styles(lod)
    a = Asset(spec.get("idPrefix", "monta_fg4") + "_return_bottom")
    body = MeshBuilder()
    rope = m(rb["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    corner = {0: 4, 1: 2}.get(lod, 0)   # rounded edges up close, square far out

    # the pad (really tall, as the other Monta terminals'; the footing is the game's) and the column's base plate
    pd = rb["pad"]
    prims.box(body, mv((pd["lo"][0], pd["lo"][1], c["pierBottom"])), mv(pd["hi"]), st["concrete"], skip=("-2",))
    pier_tops = [mv(((pd["lo"][0] + pd["hi"][0]) / 2, (pd["lo"][1] + pd["hi"][1]) / 2, pd["hi"][2]))]
    if lod <= 2:
        prims.box(body, mv(rb["basePlate"]["lo"]), mv(rb["basePlate"]["hi"]), st["steel"])

    # the column: a rectangular tube leaning back from the pad under the frame, cut level at both ends
    co = rb["column"]
    hss_leg(body, (m(co["base"][0]), m(co["base"][1])), (m(co["top"][0]), m(co["top"][1])), m(co["a1"]),
            m(co["a2"]), m(co["corner"]), corner, st["steel"])

    # each rope's kit depression assembly, its first sheave where the level rope meets the row, its lug plates up from
    # the cross tube's centre (they reach into it, as the drive's hang from its cross tube)
    hd, tb, sd = rb["holdDown"], rb["tubes"], rb["saddle"]
    asm = parts.line_spec(spec)
    ct = hd["crossTube"]
    hr = m(rb["hss"]["corner"])
    t_hw, t_v = m(tb["width"]) / 2, m(tb["v"])
    t_top = m(tb["top"])
    t_bot = t_top - m(tb["depth"])
    c_hu = m(ct["width"]) / 2
    c_w = t_top + m(ct["depth"]) / 2             # the cross tube lies on them, clear of the chairs' hangers below
    for side, name in ((-1, "l"), (1, "r")):
        u_hold, (u_out, w_out), theta, (pin_u, _) = heads.hung_assembly(
            body, a, name, side, hg, rope, m(rb["firstU"]), asm, hd["count"], "hold", asm["arcs"][hd["arc"]], lod, st,
            c_w, hd["lugs"], both=True, at_first=True)

    # the frame: two rectangular tubes along the line under the carriage and on past the cross tube; the cross tube
    # across them under the lug plates; a saddle under both on the column's top
    u_end = pin_u + c_hu + m(tb["past"])
    for s in (-1, 1):
        prims.prism(body, rounded(s * t_v - t_hw, t_bot, s * t_v + t_hw, t_top, hr, corner),
                    (m(tb["from"]), 0.0, 0.0), V, W, U, u_end - m(tb["from"]), st["steel"])
    reach = hg - asm["x"]["beam"][0] + m(hd["lugs"]["thick"]) + m(ct["past"])   # past the outer lug plates
    prims.prism(body, rounded(pin_u - c_hu, t_top, pin_u + c_hu, t_top + m(ct["depth"]), hr, corner),
                (0.0, -reach, 0.0), U, W, V, 2 * reach, st["steel"])
    su, sv = m(sd["halfU"]), t_v + t_hw + m(sd["past"])
    prims.prism(body, rounded(-sv, m(co["top"][1]), sv, (t_bot + t_top) / 2, m(sd["corner"]), corner),
                (m(co["top"][0]) - su, 0.0, 0.0), V, W, U, 2 * su, st["steel"])

    # the return's carriage plates, C-frames and hub, and its exposed bullwheel
    for b in r["boxes"]:
        if b["name"] in rb["carriageBoxes"] and lod <= 2:
            box_from(body, b, st)
    monta_return.carriage_wheel(a, body, c, r, lod, st, rope, hg)

    a.body = body
    a.sockets = {   # the rope: level from the wheel through loading to the first sheave (hold), leaving climbing (out)
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_hold": (u_hold, -hg, rope), "rope_right_hold": (u_hold, hg, rope),
        "rope_left_out": (u_out, -hg, w_out), "rope_right_out": (u_out, hg, w_out),
        "chair_load": (m(rb["loadU"]), hg, rope),
    }
    a.sockets.update(pier_sockets(pier_tops))
    a.dims.update({"rope": rb["ropeElevation"], "ropeExitDeg": round(math.degrees(theta), 2), "sheaves": hd["count"], "arc": hd["arc"],
                   "frameEnd": round(u_end * 1000)})
    return a
