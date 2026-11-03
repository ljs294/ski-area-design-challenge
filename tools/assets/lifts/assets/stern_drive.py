"""Sessellift Stern drive terminal (bottom station; sessellift_stern4.json "drive"), from the owner's layout (Gate 0):

  a portal at the line end (two square legs on piers under a deep crossbeam) carries two arms forward over the
  ropes to the hold-down rows, tied across under a lifting portal; a frame runs back from the portal into the hood,
  a long windowed hood over the drive machinery, which bridges back to a tapered column on its own pier; the
  bullwheel hangs under the hood on the tensioning carriage (static, owner); a landing at the hood's line end,
  reached by a ladder on the loading side. No conveyor or pit (owner).

Loaded chairs leave on the right rope (v+), loading at chairLoadU; the bullwheel turns counter-clockwise seen from
above. Bottom drive only (owner, 2026-11-02): the hold-down rows are the terminal's one head (liftkit/swap.py), and
it gains no top head. Dimensions: millimetres; the origin on the bullwheel axle at the loading level.
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


def line_u(h, w):
    """u (mm) of the hood's raked line end at height w (mm)."""
    pts = h["lineEnd"]
    if w <= pts[0][1]:
        return pts[0][0]
    for (u0, w0), (u1, w1) in zip(pts, pts[1:]):
        if w0 <= w <= w1 and w1 > w0:
            return u0 + (u1 - u0) * (w - w0) / (w1 - w0)
    return pts[-1][0]


def hood_half(h, lod):
    """Half profile (v >= 0, w) in mm from the bottom edge to the crown, with rows at the panes' sill and head."""
    half = [tuple(p) for p in h["profile"]]
    pn = h["panes"]
    for edge in (pn["sill"], pn["head"]):
        for i in range(len(half) - 1):
            (v0, w0), (v1, w1) = half[i], half[i + 1]
            if w0 < edge < w1:
                half.insert(i + 1, (v0 + (v1 - v0) * (edge - w0) / (w1 - w0), edge))
                break
    if lod >= 2:   # further out: the bottom edge, the widest row, the panes' rows, one shoulder and the crown
        keep = {pn["sill"], pn["head"], h["profile"][0][1], h["profile"][1][1], h["profile"][-1][1]}
        keep |= {h["profile"][3][1], h["profile"][5][1]} if lod == 2 else {h["profile"][4][1]}
        half = [p for p in half if p[1] in keep]
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
    """The hood: its end profile swept from the plumb far end to the raked line end, glazed between the sill and
    head over each pane, floored at its bottom edge; the end walls as strips between matching rows."""
    half = hood_half(h, lod)
    ring = half + [(-v, w) for v, w in reversed(half[:-1])]   # up the right side, over the crown, down the left
    spans = pane_spans(h, lod)
    cols = sorted({u for s in spans for u in s})
    glass = {(a, b) for a, b in spans}
    far = h["farEnd"]
    pn = h["panes"]
    grid = [[mb.vert((m(far), m(v), m(w)))] + [mb.vert((m(u), m(v), m(w))) for u in cols] + [mb.vert((m(line_u(h, w)), m(v), m(w)))]
            for v, w in ring]
    us = [far] + cols + [None]
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
    for line, sign in ((False, -1), (True, 1)):   # end walls, strips sharing their rows' vertices
        rows = []
        for v, w in half:
            u = m(line_u(h, w) if line else far)
            left = mb.vert((u, -m(v), m(w)))
            rows.append((left, left if v < 1e-6 else mb.vert((u, m(v), m(w)))))
        for (la, ra), (lb, rb) in zip(rows, rows[1:]):
            mb.face([la, ra, rb, lb] if rb != lb else [la, ra, rb], st["hood"], (sign, 0, 0))
    if lod <= 1:
        lining(mb, h, half, st)


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
    u_a, u_b = m(h["farEnd"]) + 0.05, m(min(u for u, _ in h["lineEnd"])) - 0.05
    dark = st["interior"].but(smooth=False)
    for i in range(n):
        (v0, w0), (v1, w1) = inner[i], inner[(i + 1) % n]
        mb.face(mb.verts_lift([(u_a, v0, w0), (u_b, v0, w0), (u_b, v1, w1), (u_a, v1, w1)]), dark, (0.0, -(w1 - w0), v1 - v0))
    for u, facing in ((u_a, 1.0), (u_b, -1.0)):
        mb.face(mb.verts_lift([(u, v, w) for v, w in inner]), dark, (facing, 0.0, 0.0))


def portal(mb, d, hg, lod, st):
    """The portal at the line end: square legs on base plates and piers, the crossbeam over them, deepened between
    the arms; the arms forward over the ropes. Returns the legs' pier tops."""
    po, pr, ar = d["portal"], d["pier"], d["arms"]
    u0, u1 = po["u"]
    cb = po["crossbeam"]
    tops = []
    for s in (-1, 1):
        v0, v1 = sorted((s * po["legV"][0], s * po["legV"][1]))
        foot = po["bottom"] - po["basePlate"]
        tops.append(stern_heads.pier(mb, (u0, v0), (u1, v1), foot, pr["bottom"], pr["margin"], st))
        if lod <= 2:
            prims.box(mb, (m(u0 - 60), m(v0 - 60), m(foot)), (m(u1 + 60), m(v1 + 60), m(po["bottom"])), st["steel"])
        prims.box(mb, (m(u0), m(v0), m(po["bottom"])), (m(u1), m(v1), m(cb["bottom"])), st["steel"])
    prims.box(mb, (m(u0), -m(cb["v"]), m(cb["bottom"])), (m(u1), m(cb["v"]), m(cb["top"])), st["steel"])
    mid = hg + m(cb["midOut"])
    prims.box(mb, (m(u0), -mid, m(cb["midBottom"])), (m(u1), mid, m(cb["bottom"])), st["steel"], skip=("+2",))
    for s in (-1, 1):   # the arms, from the crossbeam's line face forward past the hold-down rows
        v0, v1 = sorted((s * (hg + m(ar["in"])), s * (hg + m(ar["out"]))))
        prims.box(mb, (m(u1), v0, m(ar["bottom"])), (m(ar["u"][1]), v1, m(ar["top"])), st["steel"])
    return tops


def frame(mb, d, lod, st):
    """The frame from the portal back into the hood, on brackets under the crossbeam's deep middle."""
    fr = d["frame"]
    mid_bottom = d["portal"]["crossbeam"]["midBottom"]
    for s in (-1, 1):
        v0, v1 = sorted((s * m(fr["v"][0]), s * m(fr["v"][1])))
        prims.box(mb, (m(fr["u"][0]), v0, m(fr["bottom"])), (m(fr["u"][1]), v1, m(fr["top"])), st["steel"])
        if lod <= 2:
            prims.box(mb, (m(fr["bracket"][0]), v0, m(fr["top"])), (m(fr["bracket"][1]), v1, m(mid_bottom)), st["steel"])


def back_column(mb, d, lod, st):
    """The tapered column under the hood's far end, on its base plate and pier. Returns the pier top."""
    bc, pr = d["backColumn"], d["pier"]
    hv = m(bc["v"]) / 2
    prims.prism(mb, [(m(u), m(w)) for u, w in bc["profile"]], (0, -hv, 0), U, W, V, 2 * hv, st["steel"])
    bp = bc["basePlate"]
    foot = min(w for _, w in bc["profile"])
    if lod <= 2:
        prims.box(mb, (m(bp["u"][0]), -m(bp["v"]) / 2, m(foot - bp["thick"])), (m(bp["u"][1]), m(bp["v"]) / 2, m(foot)), st["steel"])
    return stern_heads.pier(mb, (bp["u"][0], -bp["v"] / 2), (bp["u"][1], bp["v"] / 2), foot - bp["thick"], pr["bottom"], pr["margin"], st)


def landing(mb, d, lod, st):
    """The landing at the hood's line end on the loading side, railed with a gate where the ladder comes up; the
    ladder from the snow."""
    ld = d["landing"]
    u0, u1 = m(ld["u"][0]), m(ld["u"][1])
    v0, v1 = m(ld["v"][0]), m(ld["v"][1])
    top = m(ld["top"])
    parts.deck(mb, (u0, v0, top - 0.05), (u1, v1, top), lod, st["galv"])
    if lod <= 2:
        prims.box(mb, (u0, v0, top - 0.2), (u1, v0 + 0.1, top - 0.05), st["steel"])
        prims.box(mb, (u0, v1 - 0.1, top - 0.2), (u1, v1, top - 0.05), st["steel"])
    rail = m(ld["rail"])
    g0, g1 = m(ld["gate"][0]), m(ld["gate"][1])
    vo, vi = v1 - 0.03, v0 + 0.03
    parts.railing(mb, [(g0, vo, top), (u0 + 0.03, vo, top), (u0 + 0.03, vi, top), (u1 - 0.03, vi, top)], rail, lod, st["galv"])
    parts.railing(mb, [(g1, vo, top), (u1 - 0.03, vo, top)], rail, lod, st["galv"])
    la = d["ladder"]
    foot = Vector((m(la["u"]), m(la["foot"][0]), m(la["foot"][1])))
    head = Vector((m(la["u"]), m(la["head"][0]), m(la["head"][1])))
    parts.ladder(mb, foot, head, 2 * m(la["halfWidth"]), lod, st["galv"], rung_every=m(la["rungs"]), rung_style=st["yellow"])


def bottom_head(mb, a, spec, lod, st):
    """The head (the bottom one; the terminal's only): the tie across the arms' ends, the lifting portal on it, and
    on each rope the line towers' hold-down assembly (heads.hung_assembly) pinned under the tie on two lug plates,
    levelled at its first sheave so the rope runs level through loading and leaves climbing. Returns the first
    axle's u, the rope's exit (u, w) and its angle."""
    c, d = spec["common"], spec["drive"]
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2
    ar, hd = d["arms"], d["heads"]["bottom"]
    reach = hg + m(ar["out"])
    prims.box(mb, (m(d["tie"]["u"][0]), -reach, m(ar["bottom"])), (m(d["tie"]["u"][1]), reach, m(ar["top"])), st["steel"])
    stern_heads.lifting_portal(mb, d["liftingPortal"], hg, lod, st, ar["top"])
    asm = parts.line_spec(spec)
    for side, name in ((-1, "l"), (1, "r")):
        u_first, out, theta, _ = heads.hung_assembly(mb, a, name, side, hg, rope, m(hd["pinU"]), asm, hd["count"], "hold",
                                                     asm["arcs"][hd["arc"]], lod, st, m(ar["bottom"]), hd["lugs"],
                                                     level=hd["level"], both=True)
    return u_first, out, theta


def build(spec, lod, stage):
    c, d = spec["common"], spec["drive"]
    st = drive_terminal.styles(lod)
    a = Asset(spec.get("idPrefix", "sessellift_stern4") + "_drive")
    body = MeshBuilder()
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2

    leg_tops = portal(body, d, hg, lod, st)
    frame(body, d, lod, st)
    column_top = back_column(body, d, lod, st)
    h = d["hood"]
    hood(body, h, lod, st)
    if lod <= 1:   # the drive machinery behind the glass (a silhouette)
        mc = d["machinery"]
        prims.box(body, (m(mc["u"][0]), -m(mc["v"]) / 2, m(mc["bottom"])), (m(mc["u"][1]), m(mc["v"]) / 2, m(mc["top"])), st["interior"])
    landing(body, d, lod, st)
    with swap.head(a, body, "bottom", (m(d["heads"]["bottom"]["pinU"]), 0.0, rope),
                   owns=("rope_left_hold", "rope_right_hold", "rope_left_out", "rope_right_out", "chair_load"),
                   dims={"sheaves": d["heads"]["bottom"]["count"]}):
        u_hold, (u_out, w_out), exit_angle = bottom_head(body, a, spec, lod, st)

    # the bullwheel on the tensioning carriage under the hood, pivoted on its vertical axle at rope elevation
    centre = Vector((0.0, 0.0, rope))
    wheel = MeshBuilder() if lod <= 2 else body
    if lod <= 2:
        parts.bullwheel(wheel, centre, parts.bullwheel_spec(stern_heads.drive_wheel(c)), lod, st["dark"], plate_snow=0.0, fan_caps=True)
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
    a.dims.update({"bullwheelU": 0, "portalU": round(sum(d["portal"]["u"]) / 2), "hoodFarEnd": h["farEnd"],
                   "hoodCrown": max(w for _, w in h["profile"]), "pinU": d["heads"]["bottom"]["pinU"],
                   "ropeExitDeg": round(math.degrees(exit_angle), 2)})
    return a
