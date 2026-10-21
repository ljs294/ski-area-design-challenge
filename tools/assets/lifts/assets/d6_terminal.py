"""Sessellift D6 terminals (sessellift_d6.json "terminal"): one station design at both ends, end for end, measured on
the owner's 1:50 layouts and seen through the deliberate mirror (Gate 0, LM7): loaded chairs leave the bottom on the
right rope (v+) and reach the top on the left (v-).

  hood     the end view's profile swept along the station, its ends standing back from the top down (nose); a livery
           band, the side windows and the roof, mullions every bay up close, a lining inside so the hood reads from
           below, and the end walls with their big windows
  belly    the white tub under the hood between the masts
  frame    the two main girders over the ropes and their cross beams, the rail hangers
  rail     the chair's station rail round the back on its corner radius, and the tyre banks along its straights
  wheel    the bullwheel, flat, at the rope's height (a spinning part)
  masts    the round front column with its cap and the inclined ladder; the return's tapered rear leg, the drive's box
           rear support; under each a pier 15 m deep (sockets pier_top_1, pier_top_2: the game buries what it needs)

Variants: "drive" (top) and "return" (bottom). Origin on the bullwheel's axle at the load or unload level 0.00; u toward
the line, v right looking along u, w up.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import GLASS, MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "terminal_detach"
LODS = 4
SPEC = "sessellift_d6.json"
VARIANTS = ("drive", "return")


def m(x):
    return x / 1000.0


def styles(lod):
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("paint_grey", snow=0.5),
        "galv": Style("galvanised", snow=0.5),
        "dark": Style("paint_dark", snow=0.0),
        "rubber": Style("rubber"),
        "hood": Style("livery", livery=1.0, snow=1.0),
        "belly": Style("white", snow=0.0),
        "trim": Style("trim_dark", snow=0.0),
        "glass": Style("glass", slot=GLASS, smooth=False) if lod <= 1 else Style("glass", smooth=False),
        "lining": Style("interior"),
        "grating": Style("grating", snow=0.8),
        "yellow": Style("safety_yellow"),
    }


def interp(table, x):
    """Linear interpolation in a [[x, y], ...] table sorted by x, clamped at its ends."""
    if x <= table[0][0]:
        return table[0][1]
    for (x0, y0), (x1, y1) in zip(table, table[1:]):
        if x <= x1:
            return y0 + (y1 - y0) * (x - x0) / (x1 - x0) if x1 > x0 else y1
    return table[-1][1]


# -- hood --------------------------------------------------------------------------------------------
def hood_loop(h, lod):
    """The hood's cross-section (mm): from the right bottom edge over the crown to the left, with points added at the
    band breaks so every face is one band; far out the profile is thinned."""
    half = [tuple(p) for p in h["profile"]]
    if lod >= 2:
        keep = (0, 3, 6, 8, 10, 14) if lod == 2 else (0, 5, 10, 14)
        half = [half[i] for i in keep]
    breaks = (h["windows"]["from"], h["windows"]["to"]) if lod <= 1 else ()
    out = [half[0]]
    for a, b in zip(half, half[1:]):
        for wb in breaks:
            if min(a[1], b[1]) < wb < max(a[1], b[1]):
                f = (wb - a[1]) / (b[1] - a[1])
                out.append((a[0] + (b[0] - a[0]) * f, wb))
        out.append(b)
    right = out                                     # bottom edge up to the crown (v = 0)
    left = [(-v, w) for v, w in reversed(right[:-1])]
    return right + left


def band(h, w, lod):
    if lod <= 1 and h["windows"]["from"] <= w <= h["windows"]["to"]:
        return "glass"
    return "hood"


def hood(mb, h, ends, lod, st):
    """The shell between its two ends (u_back < u_front, mm), the lining inside it, the end walls and their windows,
    and up close the mullions."""
    loop = hood_loop(h, lod)
    u0, u1 = ends
    nose = h["nose"]

    def at(u_end, sign, v, w):   # a profile point on an end: the end stands back by the nose at its height
        return Vector((m(u_end + sign * interp(nose, w)), m(v), m(w)))

    def outward(a, b):   # outward across the profile segment a -> b (loop runs right to left over the crown)
        dv, dw = b[0] - a[0], b[1] - a[1]
        return Vector((0.0, dw, -dv))

    lining = m(h["inner"])
    for a, b in zip(loop, loop[1:]):
        out = outward(a, b)
        style = st[band(h, (a[1] + b[1]) / 2, lod)]
        prims.quad(mb, [at(u0, 1, *a), at(u0, 1, *b), at(u1, -1, *b), at(u1, -1, *a)], style, out)
        if lod <= 1:   # the lining, a hand's width inside, facing in
            n = out.normalized() * lining
            prims.quad(mb, [at(u0, 1, *a) - n, at(u0, 1, *b) - n, at(u1, -1, *b) - n, at(u1, -1, *a) - n], st["lining"], -out)
    # the bottom edge's lip, from the shell to the lining
    if lod <= 1:
        for p in (loop[0], loop[-1]):
            s = 1 if p[0] > 0 else -1
            prims.quad(mb, [at(u0, 1, *p), at(u1, -1, *p), at(u1, -1, p[0] - s * h["inner"], p[1]),
                            at(u0, 1, p[0] - s * h["inner"], p[1])], st["trim"], (0, 0, -1))
    # the end walls: level strips across the hood between the profile's heights, each standing back by the nose at its
    # height, so the wall leans back from the roof's edge and the band under it stands out as a ledge
    ew = h["endWindow"]
    right = loop[:len(loop) // 2 + 1]           # bottom edge up to the crown
    for u_end, sign in ((u0, 1), (u1, -1)):
        for a, b in zip(right, right[1:]):
            strip = [at(u_end, sign, -a[0], a[1]), at(u_end, sign, a[0], a[1]), at(u_end, sign, b[0], b[1])]
            if b[0] > 0:   # the last strip closes on the crown in a point
                strip.append(at(u_end, sign, -b[0], b[1]))
            mb.face(mb.verts_lift([tuple(p) for p in strip]), st["hood"], (-sign, 0.0, 0.0))
        if lod <= 2:
            proud = -sign * 0.015
            win = [at(u_end, sign, -ew["halfV"], ew["from"]), at(u_end, sign, ew["halfV"], ew["from"]),
                   at(u_end, sign, ew["halfV"], ew["to"]), at(u_end, sign, -ew["halfV"], ew["to"])]
            prims.quad(mb, [p + U * proud for p in win], st["glass"], (-sign, 0, 0))
    # mullions over the side windows, every bay, up close
    if lod == 0:
        lo, hi = h["windows"]["from"], h["windows"]["to"]
        side = [p for p in loop if lo <= p[1] <= hi and p[0] > 0]
        n_bays = max(1, round((u1 - u0) / h["bay"]))
        for k in range(1, n_bays):
            u = u0 + (u1 - u0) * k / n_bays
            for s in (1, -1):
                pts = [Vector((m(u), s * (m(v) + 0.02), m(w))) for v, w in side]
                prims.tube_path(mb, pts, 0.03, 4, st["trim"])


def belly(mb, b, lod, st):
    """The white tub under the hood: a trapezoid across the line, swept between its ends."""
    poly = [(-m(b["topHalf"]), m(b["top"])), (-m(b["bottomHalf"]), m(b["bottom"])),
            (m(b["bottomHalf"]), m(b["bottom"])), (m(b["topHalf"]), m(b["top"]))]
    prims.prism(mb, poly, (m(b["u"][0]), 0.0, 0.0), V, W, U, m(b["u"][1] - b["u"][0]), st["belly"])


# -- station frame and rail ----------------------------------------------------------------------------
def frame(mb, f, rail, lod, st):
    gw, gh = m(f["girder"][0]), m(f["girder"][1])
    w0 = m(f["w"])
    u0, u1 = m(f["u"][0]), m(f["u"][1])
    for s in (-1, 1):
        prims.box(mb, (u0, s * m(f["girderV"]) - gw / 2, w0), (u1, s * m(f["girderV"]) + gw / 2, w0 + gh), st["steel"])
    if lod <= 2:   # cross beams and the rail hangers down to the rail
        n = max(1, round((u1 - u0) / m(f["cross"])))
        for k in range(n + 1):
            u = u0 + (u1 - u0) * k / n
            prims.box(mb, (u - 0.12, -m(rail["v"]) - 0.1, w0 + gh - 0.25), (u + 0.12, m(rail["v"]) + 0.1, w0 + gh), st["steel"])
            if lod <= 1 and m(rail["back"]) + m(rail["corner"]) <= u:
                for s in (-1, 1):
                    v = s * m(rail["v"])
                    prims.box(mb, (u - 0.08, v - 0.08, m(rail["w"])), (u + 0.08, v + 0.08, w0 + gh - 0.25), st["steel"])


def rail_path(rail, u_front, steps):
    """The rail's centreline (m): out along the right straight from the line end, round the back on its corner radius,
    and back along the left straight."""
    v, back, r = m(rail["v"]), m(rail["back"]), m(rail["corner"])
    pts = [Vector((u_front, v, 0.0))]
    for corner, (cu, cv, a0) in enumerate(((back + r, v - r, 90.0), (back + r, -v + r, 180.0))):
        for i in range(steps + 1):
            a = math.radians(a0 + 90.0 * i / steps)
            pts.append(Vector((cu + r * math.cos(a), cv + r * math.sin(a), 0.0)))
    pts.append(Vector((u_front, -v, 0.0)))
    return pts


def chair_rail(mb, rail, u_front, lod, st):
    w = m(rail["w"])
    path = [p + W * w for p in rail_path(rail, u_front, 6 if lod == 0 else 3)]
    if lod <= 2:
        prims.tube_path(mb, path, m(rail["section"][1]) / 2, 6 if lod <= 1 else 4, st["galv"])
    ty = rail["tyres"]
    if lod <= 1:   # the tyre banks along the straights, inside the rail
        sw, sh = m(ty["size"][0]), m(ty["size"][1])
        for s in (-1, 1):
            v = s * (m(rail["v"]) - m(ty["inset"]))
            prims.box(mb, (m(ty["from"]), v - sw / 2, w + 0.05), (m(ty["to"]), v + sw / 2, w + 0.05 + sh), st["dark"])
    return [tuple(p) for p in path]


def walkways(mb, wk, rail, lod, st):
    if lod > 1:
        return
    v0, v1, w = m(wk["v"][0]), m(wk["v"][1]), m(wk["w"])
    u0, u1 = m(rail["back"]) + m(rail["corner"]), m(rail["tyres"]["to"])
    for s in (-1, 1):
        lo, hi = sorted((s * v0, s * v1))
        parts.deck(mb, (u0, lo, w - 0.06), (u1, hi, w), lod, st["grating"])


# -- masts and piers ----------------------------------------------------------------------------------
def front_mast(mb, t, end, lod, st):
    fm, f = t["frontMast"], end["frontMast"]
    u = m(f["u"])
    r = m(fm["dia"]) / 2
    toc, top = m(f["toc"]), m(fm["top"])
    prims.cylinder(mb, (u, 0.0, toc), (u, 0.0, top), r, parts.sides(lod, 24, 16, 10, 6), st["steel"], caps=(False, False))
    ch = fm["cap"]
    hu, hv = m(ch["half"][0]), m(ch["half"][1])
    prims.box(mb, (u - hu, -hv, top - m(ch["height"])), (u + hu, hv, top), st["steel"])
    if lod <= 1:   # the inclined ladder up to the station, beside the column (u from the column, w absolute)
        la = f["ladder"]
        (ut, wt), (ub, wb) = la["top"], la["bottom"]
        v = -m(fm["ladderV"])
        parts.ladder(mb, Vector((u + m(ub), v, m(wb))), Vector((u + m(ut), v, m(wt))), m(la["width"]), lod, st["galv"])
    return (u, 0.0, toc)


def rear_support(mb, t, end, lod, st):
    rs = end["rear"]
    toc = m(rs["toc"])
    top = m(t["frontMast"]["top"])
    hv = m(rs["halfV"])
    if rs["kind"] == "leg":   # the return's rear leg: a box tapering from its foot to the frame, leaning toward the line
        (a0, a1), (b0, b1) = rs["top"], rs["bottom"]
        poly = [(m(b0), toc), (m(b1), toc), (m(a1), m(rs["topW"])), (m(a0), m(rs["topW"]))]
        prims.prism(mb, poly, (0.0, -hv, 0.0), U, W, V, 2 * hv, st["steel"])
        prims.box(mb, (m(a0) - 0.2, -hv, m(rs["topW"])), (m(a1) + 0.2, hv, top), st["steel"])
        return (m(b0 + b1) / 2, 0.0, toc)
    # the drive's rear support: four posts and their ties, a box frame under the station
    u0, u1 = m(rs["u"][0]), m(rs["u"][1])
    p = m(rs["post"])
    for u in (u0 + p / 2, u1 - p / 2):
        for s in (-1, 1):
            prims.box(mb, (u - p / 2, s * hv - p / 2, toc), (u + p / 2, s * hv + p / 2, top), st["steel"])
    if lod <= 2:
        for w in (toc + 0.25, top - 0.25):
            for s in (-1, 1):
                prims.box(mb, (u0, s * hv - p / 3, w - 0.12), (u1, s * hv + p / 3, w + 0.12), st["steel"])
            for u in (u0 + p / 2, u1 - p / 2):
                prims.box(mb, (u - p / 3, -hv, w - 0.12), (u + p / 3, hv, w + 0.12), st["steel"])
    return ((u0 + u1) / 2, 0.0, toc)


def pier(mb, at, pr, lod, st):
    """A square concrete pier from the mast's top of concrete down pr.depth, buried as deep as the game needs."""
    u, v, toc = at
    h = m(pr["half"])
    prims.box(mb, (u - h, v - h, toc - m(pr["depth"])), (u + h, v + h, toc), st["concrete"], skip=("-z",))


# -- build ----------------------------------------------------------------------------------------------
def build(spec, lod, stage, variant="return"):
    c, t = spec["common"], spec["terminal"]
    end = t[variant]
    st = styles(lod)
    a = Asset(f"sessellift_d6_{variant}")
    body = MeshBuilder()
    rope = m(c["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    u_front = m(end["hood"][1]) - 0.4   # where the rope leaves the hood, just inside its end

    hood(body, t["hood"], end["hood"], lod, st)
    belly(body, t["belly"], lod, st)
    frame(body, t["frame"], t["rail"], lod, st)
    chair_rail(body, t["rail"], u_front, lod, st)
    walkways(body, t["walkway"], t["rail"], lod, st)
    front = front_mast(body, t, end, lod, st)
    rear = rear_support(body, t, end, lod, st)
    for p in (front, rear):
        pier(body, p, t["pier"], lod, st)

    centre = Vector((0.0, 0.0, rope))
    bw_spec = parts.bullwheel_spec(c)
    if lod <= 2:
        wheel = MeshBuilder()
        parts.bullwheel(wheel, centre, bw_spec, lod, st["dark"])
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        parts.bullwheel(body, centre, bw_spec, lod, st["dark"])

    a.body = body
    rv, rw = m(t["rail"]["v"]), m(t["rail"]["w"])
    corner_u = m(t["rail"]["back"]) + m(t["rail"]["corner"])
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_out": (u_front, -hg, rope), "rope_right_out": (u_front, hg, rope),
        "pier_top_1": front, "pier_top_2": rear,
    }
    # riders: the return loads on the back of the loop as chairs turn out onto the loaded (right) side (90 degree
    # loading); the drive unloads alongside the incoming loaded (left) straight before the back (parallel unload)
    if variant == "return":
        a.sockets["chair_load"] = (m(t["rail"]["back"]), -0.5 * rv, rw)
    else:
        a.sockets["chair_unload"] = (corner_u, -rv, rw)
    a.paths = {"rail": chair_rail(MeshBuilder(), t["rail"], u_front, 0, st)}
    a.dims.update({"rope": c["ropeElevation"], "gauge": c["lineGauge"], "hoodLength": end["hood"][1] - end["hood"][0],
                   "hoodBack": end["hood"][0], "hoodFront": end["hood"][1], "frontMastU": end["frontMast"]["u"],
                   "railBack": t["rail"]["back"], "railV": t["rail"]["v"], "driveEnd": c["driveEnd"]})
    return a
