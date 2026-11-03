"""Sessellift Stern drive terminal (bottom station; sessellift_stern4.json "drive"), from the owner's layout:

  a portal of 508 pipe at the line end (round legs on piers, a pipe crossbeam on saddles); a 508 pipe frame per side
  outboard of the rope, from the hanger plates at the line end back under the crossbeam and through the hood to a
  transverse pipe on the tapered back column, the rails hung under it; the windowed hood over the tensioning
  carriage and the exposed bullwheel (static at its drawn position, owner); a tie across the hanger plates carrying
  the hold-down rows and the lifting portal; the ladder up to the frame on the loading side. No conveyor, pit or
  landings (owner; none drawn).

Loaded chairs leave on the right rope (v+), loading at chairLoadU; the bullwheel turns counter-clockwise seen from
above. Bottom drive only (owner, 2026-11-02): the hold-down rows are the terminal's one head (liftkit/swap.py), and
it gains no top head. Most of the drawn steel is data (spec "steel", stern_heads.members). Dimensions: millimetres;
the origin on the bullwheel axle at the loading level.
"""
import math

from mathutils import Vector

from liftkit import heads, parts, prims, swap
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder
from liftkit.prims import U, V, W

import drive_terminal
import stern_heads
from stern_heads import m

KIND = "terminal"
CHAIR = "chair"   # the chair the build sends round the bullwheel (liftkit/fit.py fixed)
LODS = 4
SPEC = "sessellift_stern4.json"


def end_u(pts, w):
    """u (mm) of a raked hood end (a polyline of (u, w) from its bottom edge up) at height w (mm)."""
    if w <= pts[0][1]:
        return pts[0][0]
    for (u0, w0), (u1, w1) in zip(pts, pts[1:]):
        if w0 <= w <= w1 and w1 > w0:
            return u0 + (u1 - u0) * (w - w0) / (w1 - w0)
    return pts[-1][0]


def hood_half(h, lod):
    """Half profile (v >= 0, w) in mm from the bottom edge to the crown, with rows at the panes' sill and head and,
    up close (LOD0), at every break of the two end profiles."""
    half = [tuple(p) for p in h["profile"]]
    pn = h["panes"]
    edges = {pn["sill"], pn["head"]} | ({w for _, w in h["lineEnd"] + h["farEnd"]} if lod == 0 else set())
    for edge in sorted(edges):
        for i in range(len(half) - 1):
            (v0, w0), (v1, w1) = half[i], half[i + 1]
            if w0 < edge < w1:
                half.insert(i + 1, (v0 + (v1 - v0) * (edge - w0) / (w1 - w0), edge))
                break
    if lod >= 2:   # further out: the ends, the panes' rows and every other (every third) row
        keep = {pn["sill"], pn["head"], half[0][1], half[-1][1]}
        step = 2 if lod == 2 else 3
        half = [p for i, p in enumerate(half) if p[1] in keep or i % step == 0]
    return half


def pane_spans(h, lod):
    """The windows' (u from, u to) spans, mm, along the hood: each run split into its panes by mullions (one band
    per run from LOD2)."""
    pn = h["panes"]
    spans = []
    for a, b, n in pn["runs"]:
        lo, hi = min(a, b), max(a, b)
        if lod >= 2:
            spans.append((lo, hi))
            continue
        p = (hi - lo - (n - 1) * pn["mullion"]) / n
        spans += [(lo + i * (p + pn["mullion"]), lo + i * (p + pn["mullion"]) + p) for i in range(n)]
    return sorted(spans)


def hood(mb, h, lod, st):
    """The hood: its end profile swept between the two raked ends, glazed between the sill and head over each pane,
    floored at its bottom edge; the end walls as strips between matching rows; the trims and roof ribs up close."""
    half = hood_half(h, lod)
    ring = half + [(-v, w) for v, w in reversed(half[:-1])]   # up the right side, over the crown, down the left
    spans = pane_spans(h, lod)
    cols = sorted({u for s in spans for u in s})
    glass = {(a, b) for a, b in spans}
    pn = h["panes"]
    grid = [[mb.vert((m(end_u(h["farEnd"], w)), m(v), m(w)))] + [mb.vert((m(u), m(v), m(w))) for u in cols]
            + [mb.vert((m(end_u(h["lineEnd"], w)), m(v), m(w)))] for v, w in ring]
    us = [None] + cols + [None]
    n = len(ring)
    mid_w = (ring[0][1] + max(w for _, w in ring)) / 2
    for i in range(n):
        i1 = (i + 1) % n
        (v0, w0), (v1, w1) = ring[i], ring[i1]
        floor = i1 == 0
        for k in range(len(us) - 1):
            if floor:
                style = st["trim"]
            elif (us[k], us[k + 1]) in glass and min(w0, w1) >= pn["sill"] - 1e-6 and max(w0, w1) <= pn["head"] + 1e-6:
                style = st["glass"]
            else:
                style = st["hood"]
            out = (0.0, 0.0, -1.0) if floor else (0.0, (v0 + v1) / 2, (w0 + w1) / 2 - mid_w)
            mb.face([grid[i][k], grid[i][k + 1], grid[i1][k + 1], grid[i1][k]], style, out)
    for pts, sign in ((h["farEnd"], -1), (h["lineEnd"], 1)):   # end walls, strips sharing their rows' vertices
        rows = []
        for v, w in half:
            u = m(end_u(pts, w))
            left = mb.vert((u, -m(v), m(w)))
            rows.append((left, left if v < 1e-6 else mb.vert((u, m(v), m(w)))))
        for (la, ra), (lb, rb) in zip(rows, rows[1:]):
            mb.face([la, ra, rb, lb] if rb != lb else [la, ra, rb], st["hood"], (sign, 0, 0))
    if lod <= 1:
        lining(mb, h, half, st)
        trims(mb, h, half, lod, st)
    if lod == 0:
        rb = h["ribs"]
        pitch = (rb["u"][1] - rb["u"][0]) / (rb["n"] - 1)
        crown = max(w for _, w in half)
        for i in range(rb["n"]):
            uc = rb["u"][0] + i * pitch
            prims.box(mb, (m(uc - rb["width"] / 2), -m(rb["halfV"]), m(crown)), (m(uc + rb["width"] / 2), m(rb["halfV"]), m(crown + rb["height"])),
                      st["hood"])


def side_v(half, w):
    """v (mm) of the hood's side at height w (mm), from the half profile."""
    for (v0, w0), (v1, w1) in zip(half, half[1:]):
        if w0 <= w <= w1 and w1 > w0:
            return v0 + (v1 - v0) * (w - w0) / (w1 - w0)
    return half[-1][0]


def trims(mb, h, half, lod, st):
    """The sill rail under the windows and the head trim over them, standing proud of each side between the ends."""
    for key in ("sillRail", "headTrim"):
        t = h[key]
        lo, hi = t["bottom"], t["top"]
        v_in = max(side_v(half, lo), side_v(half, hi))
        u0, u1 = end_u(h["farEnd"], hi), end_u(h["lineEnd"], hi)
        for s in (-1, 1):
            v0, v1 = sorted((s * m(v_in - 10), s * m(v_in + t["proud"])))
            prims.box(mb, (m(u0), v0, m(lo)), (m(u1), v1, m(hi)), st["trim"])


def lining(mb, h, half, st):
    """A dark shell just inside the hood, facing in, closed at both ends: in game the hood's own faces are culled
    from inside, so without it the windows would show straight through the hood (as the FGQ-4's,
    drive_terminal.interior_lining)."""
    inset = m(h["lining"])
    ring = [(m(v), m(w)) for v, w in half] + [(-m(v), m(w)) for v, w in reversed(half[:-1])]
    n = len(ring)
    inner = []
    for i, (v, w) in enumerate(ring):   # each point moved in along its two edges' inward normals (ccw ring)
        nv = nw = 0.0
        for a, b in ((ring[i - 1], ring[i]), (ring[i], ring[(i + 1) % n])):
            dv, dw = b[0] - a[0], b[1] - a[1]
            ln = math.hypot(dv, dw) or 1.0
            nv, nw = nv - dw / ln, nw + dv / ln
        ln = math.hypot(nv, nw) or 1.0
        inner.append((v + nv / ln * inset, w + nw / ln * inset))
    u_a, u_b = m(max(u for u, _ in h["farEnd"])) + 0.05, m(min(u for u, _ in h["lineEnd"])) - 0.05
    dark = st["interior"].but(smooth=False)
    for i in range(n):
        (v0, w0), (v1, w1) = inner[i], inner[(i + 1) % n]
        mb.face(mb.verts_lift([(u_a, v0, w0), (u_b, v0, w0), (u_b, v1, w1), (u_a, v1, w1)]), dark, (0.0, -(w1 - w0), v1 - v0))
    for u, facing in ((u_a, 1.0), (u_b, -1.0)):
        mb.face(mb.verts_lift([(u, v, w) for v, w in inner]), dark, (facing, 0.0, 0.0))


def portal(mb, d, lod, st):
    """The portal at the line end: round legs on base plates and piers, cap plates under the crossbeam. Returns the
    legs' pier tops."""
    po, pr = d["portal"], d["pier"]
    lg = po["legs"]
    bp, cp = po["basePlate"], po["capPlate"]
    tops = []
    for s in (-1, 1):
        uc, vc = lg["u"], s * lg["v"]
        hs = bp["size"] / 2
        foot = lg["bottom"] - bp["thick"]
        tops.append(stern_heads.pier(mb, (uc - hs, vc - hs), (uc + hs, vc + hs), foot, pr["bottom"], pr["margin"], st))
        if lod <= 2:
            prims.box(mb, (m(uc - hs), m(vc - hs), m(foot)), (m(uc + hs), m(vc + hs), m(lg["bottom"])), st["steel"])
            hc = cp["size"] / 2
            prims.box(mb, (m(uc - hc), m(vc - hc), m(lg["top"])), (m(uc + hc), m(vc + hc), m(cp["top"])), st["steel"])
        stern_heads.pipe(mb, (m(uc), m(vc), m(lg["bottom"])), (m(uc), m(vc), m(lg["top"])), m(lg["dia"]), lod, st["steel"])
    return tops


def back_column(mb, d, lod, st):
    """The tapered column at the back, on its base plate and pier, carrying the rails' ends and the tension anchor.
    Returns the pier top."""
    bc, pr = d["backColumn"], d["pier"]
    hv = m(bc["v"]) / 2
    prims.prism(mb, [(m(u), m(w)) for u, w in bc["profile"]], (0, -hv, 0), U, W, V, 2 * hv, st["steel"])
    bp = bc["basePlate"]
    foot = min(w for _, w in bc["profile"])
    if lod <= 2:
        prims.box(mb, (m(bp["u"][0]), -m(bp["v"]) / 2, m(foot - bp["thick"])), (m(bp["u"][1]), m(bp["v"]) / 2, m(foot)), st["steel"])
    return stern_heads.pier(mb, (bp["u"][0], -bp["v"] / 2), (bp["u"][1], bp["v"] / 2), foot - bp["thick"], pr["bottom"], pr["margin"], st)


def frame(mb, d, hg, lod, st):
    """The 508 pipe frame: one pipe per side outboard of the rope from the hanger plates back to the column head, and
    the transverse pipe across it there, the corners bent (drawn as mitres)."""
    fr = d["frame"]
    vf, w, f, b = hg + m(fr["v"]), m(fr["w"]), m(fr["front"]), m(fr["back"])
    stern_heads.tube(mb, [(f, -vf, w), (b, -vf, w), (b, vf, w), (f, vf, w)], m(fr["dia"]), lod, st["steel"], sides=(16, 12, 8, 6),
                     corner=m(fr["corner"]))


def ladder(mb, d, hg, lod, st):
    """The ladder on the loading side (+v), climbing across the line to the frame pipe, its stringers carried on up
    the slope, and its two bent grab hoops with their mid bars."""
    la = d["ladder"]
    uc, width = m(sum(la["u"])) / 2, m(la["u"][1] - la["u"][0]) - m(51)
    foot = (uc, hg + m(la["foot"][0]), m(la["foot"][1]))
    head = (uc, hg + m(la["head"][0]), m(la["head"][1]))
    stern_heads.ladder(mb, foot, head, width, m(la["rungRise"]), lod, st["galv"], st["yellow"], stringer=m(la["stringer"]),
                       extend=m(la["extend"]))
    hp = la["hoops"]
    if lod >= 2:
        return
    vi, vo = hg + m(hp["v"][0]), hg + m(hp["v"][1])
    top = m(hp["top"])
    for u in hp["u"]:
        stern_heads.tube(mb, [(m(u), vi, m(hp["foot"])), (m(u), vi, top), (m(u), vo, top), (m(u), vo, m(hp["outerFoot"]))], m(hp["dia"]), lod,
                         st["galv"], corner=m(hp["corner"]))
        if lod == 0:
            mid = hp["mid"]
            stern_heads.pipe(mb, (m(u), hg + m(mid["v"][0]), m(mid["w"])), (m(u), hg + m(mid["v"][1]), m(mid["w"])), m(hp["dia"]), lod,
                             st["galv"], sides=(8, 6), caps=(False, False))


def guides(mb, a, c, d, hg, lod, st):
    """The guide sheaves at the hood's mouth (rope on top), each on its axle from the pendant plate inboard of it
    (their mounts: spec "steel")."""
    rope, rope_r = m(c["ropeElevation"]), m(c["ropeDiameter"]) / 2
    gs = d["guideSheaves"]
    for side, name in ((-1, "l1"), (1, "r1")):
        centre = Vector((m(gs["u"]), side * hg, rope - rope_r - m(gs["dia"]) / 2))
        target = MeshBuilder() if lod <= 1 else mb
        if lod <= 2:
            parts.sheave(target, centre, V, m(gs["dia"]), m(gs["width"]), lod, st["red"], st["rubber"])
        if lod <= 1:
            prims.cylinder(mb, (centre.x, side * (hg + m(gs["axleFrom"])), centre.z), (centre.x, side * (hg + m(gs["width"]) / 2), centre.z),
                           m(gs["axle"]) / 2, parts.sides(lod, 10, 6), st["rod"])
            a.parts[f"sheave_{name}"] = target
            a.pivots[f"sheave_{name}"] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}


def bottom_head(mb, a, spec, lod, st):
    """The head (the bottom one; the terminal's only): the head's drawn steel (spec drive.heads.bottom.steel: the
    tie, the hanger plates and their gussets), the lifting portal on the tie, and on each rope the line towers'
    hold-down assembly (heads.hung_assembly) pinned on two lug plates from the hanger, levelled at its first sheave so
    the rope runs level through loading and leaves climbing. Returns the first axle's u, the rope's exit (u, w) and
    its angle."""
    c, d = spec["common"], spec["drive"]
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2
    hd = d["heads"]["bottom"]
    stern_heads.members(mb, hd["steel"], hg, lod, st)
    stern_heads.lifting_portal(mb, hd["liftingPortal"], hg, lod, st)
    asm = parts.line_spec(spec)
    for side, name in ((-1, "l"), (1, "r")):
        u_first, out, theta, _ = heads.hung_assembly(mb, a, name, side, hg, rope, m(hd["pinU"]), asm, hd["count"], "hold",
                                                     asm["arcs"][hd["arc"]], lod, st, m(hd["faceW"]), hd["lugs"],
                                                     level=hd["level"], both=True)
    return u_first, out, theta


def build(spec, lod, stage):
    c, d = spec["common"], spec["drive"]
    st = drive_terminal.styles(lod)
    a = Asset(spec.get("idPrefix", "sessellift_stern4") + "_drive")
    body = MeshBuilder()
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2

    leg_tops = portal(body, d, lod, st)
    frame(body, d, hg, lod, st)
    stern_heads.members(body, d["steel"], hg, lod, st)
    column_top = back_column(body, d, lod, st)
    h = d["hood"]
    hood(body, h, lod, st)
    if lod <= 1:   # the drive machinery behind the glass (a silhouette)
        mc = d["machinery"]
        prims.box(body, (m(mc["u"][0]), -m(mc["v"]) / 2, m(mc["bottom"])), (m(mc["u"][1]), m(mc["v"]) / 2, m(mc["top"])), st["interior"])
    ladder(body, d, hg, lod, st)
    guides(body, a, c, d, hg, lod, st)
    with swap.head(a, body, "bottom", (m(d["heads"]["bottom"]["pinU"]), 0.0, rope),
                   owns=("rope_left_hold", "rope_right_hold", "rope_left_out", "rope_right_out", "chair_load"),
                   dims={"sheaves": d["heads"]["bottom"]["count"]}):
        u_hold, (u_out, w_out), exit_angle = bottom_head(body, a, spec, lod, st)

    # the bullwheel on the tensioning carriage under the hood, pivoted on its vertical axle at rope elevation
    centre = Vector((0.0, 0.0, rope))
    if lod <= 2:
        wheel = MeshBuilder()
        stern_heads.drive_wheel_mesh(wheel, centre, stern_heads.drive_wheel(c, d["bullwheel"]), lod, st)
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    # LOD3: no wheel, as the FGQ-4's drive (it sits in the hood's shadow)

    a.body = body
    a.sockets.update({   # the rope: level from the bullwheel to the hold-down row (hold), leaving it climbing (out)
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (0.0, -hg, rope), "rope_right_bw": (0.0, hg, rope),
        "rope_left_hold": (u_hold, -hg, rope), "rope_right_hold": (u_hold, hg, rope),
        "rope_left_out": (u_out, -hg, w_out), "rope_right_out": (u_out, hg, w_out),
        "chair_load": (m(d["chairLoadU"]), hg, rope),
        "pier_top": column_top, "pier_top_2": leg_tops[0], "pier_top_3": leg_tops[1],
    })
    a.dims.update({"bullwheelU": 0, "portalU": d["portal"]["legs"]["u"], "hoodFarEnd": min(u for u, _ in h["farEnd"]),
                   "hoodCrown": max(w for _, w in h["profile"]), "pinU": d["heads"]["bottom"]["pinU"],
                   "ropeExitDeg": round(math.degrees(exit_angle), 2)})
    return a
