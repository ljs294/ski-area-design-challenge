"""Sessellift Stern drive terminal (bottom station; sessellift_stern4.json "drive"), from the owner's layout:

  a portal of 508 pipe at the line end (round legs on piers, a pipe crossbeam on saddles); a 508 pipe frame per side
  outboard of the rope, from the hanger plates at the line end back under the crossbeam and through the hood to a
  transverse pipe on the tapered back column, the rails hung under it; the windowed hood over the tensioning
  carriage and the exposed bullwheel (static at its drawn position, owner), its ends flat and vertical (owner); a tie across the hanger plates carrying
  the hold-down rows and the lifting portal; the ladder up to the frame on the loading side and the catwalk across the
  line end to the engine room's door (owner). No conveyor or pit (owner; none drawn).

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
    """The hood: its end profile swept between the two flat ends (owner, 2026-11-03), glazed between the sill and
    head over each pane, floored at its bottom edge; each end a rim round a recessed flat wall with its windows; the
    trims and roof ribs up close."""
    half = hood_half(h, lod)
    ring = half + [(-v, w) for v, w in reversed(half[:-1])]   # up the right side, over the crown, down the left
    spans = pane_spans(h, lod)
    cols = sorted({u for s in spans for u in s})
    glass = {(a, b) for a, b in spans}
    pn = h["panes"]
    ends = h["ends"]
    us = [ends["far"]] + cols + [ends["line"]]
    grid = [[mb.vert((m(u), m(v), m(w))) for u in us] for v, w in ring]
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
            if floor and k in (0, len(us) - 2):   # the end bays' floor from above too: it shows through the end windows
                top = [mb.vert((m(u), m(v), m(w) + 0.002)) for u, (v, w) in ((us[k], ring[i]), (us[k + 1], ring[i]), (us[k + 1], ring[i1]), (us[k], ring[i1]))]
                mb.face(top, style, (0.0, 0.0, 1.0))
    for u_end, sign in ((ends["far"], -1), (ends["line"], 1)):
        end_cap(mb, h, half, u_end, sign, lod, st)
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


def inset(pts, d, closed=True):
    """A ccw outline of (v, w) points moved in by d along its edges' inward normals (each point along the mean of
    its two edges'; an open outline's ends along their one edge's)."""
    n = len(pts)
    out = []
    for i, (v, w) in enumerate(pts):
        nv = nw = 0.0
        pairs = [(pts[i - 1], pts[i]), (pts[i], pts[(i + 1) % n])]
        if not closed:
            pairs = pairs[1:] if i == 0 else pairs[:1] if i == n - 1 else pairs
        for a, b in pairs:
            dv, dw = b[0] - a[0], b[1] - a[1]
            ln = math.hypot(dv, dw) or 1.0
            nv, nw = nv - dw / ln, nw + dv / ln
        ln = math.hypot(nv, nw) or 1.0
        out.append((v + nv / ln * d, w + nw / ln * d))
    return out


def rim(half, d):
    """The hood's outline from the right foot over the crown to the left foot (mm), and that outline inset by d with
    its feet kept on the bottom edge."""
    outline = half + [(-v, w) for v, w in reversed(half[:-1])]
    inner = inset(outline, d, closed=False)
    w_foot = outline[0][1]
    for i, j in ((0, 1), (-1, -2)):   # each foot slid along its edge back down to the bottom edge
        (va, wa), (vb, wb) = inner[i], inner[j]
        inner[i] = (va + (vb - va) * (w_foot - wa) / (wb - wa), w_foot)
    return outline, inner


def end_cap(mb, h, half, u_end, sign, lod, st):
    """One flat end (owner, 2026-11-03: vertical and plane, as the D6's): the rim, the hood's shell turned in at u_end
    (spec hood.ring), its soffit back to the wall recessed inside it, and the wall with its window: three panes
    (spec hood.endPanes, the sides' pane size from their sill; the owner's, not drawn) in a frame a hand
    deep, the glass set back behind the mullions; at the line end the middle pane is the door onto the catwalk
    (endPanes.door, owner 2026-11-03: a kick panel over its lower glass and a pull handle). sign: +1 the line end, -1
    the far end."""
    rg, pn, ep = h["ring"], h["panes"], h["endPanes"]
    out = (sign, 0.0, 0.0)
    gv = (ep["n"] * ep["width"] + (ep["n"] - 1) * pn["mullion"]) / 2
    gw0, gw1 = pn["sill"], ep["head"]
    pane = [(-gv, gw0), (gv, gw0), (gv, gw1), (-gv, gw1)]
    door = ep.get("door") if sign > 0 else None
    if door:   # the middle pane's span and its kick panel's top
        dv0 = -gv + (ep["n"] // 2) * (ep["width"] + pn["mullion"])
        dv1, dw = dv0 + ep["width"], gw0 + door["panel"]
        kick = [(dv0, gw0), (dv1, gw0), (dv1, dw), (dv0, dw)]

    def face(u, poly, style):
        mb.face(mb.verts_lift([(m(u), m(v), m(w)) for v, w in poly]), style, out)

    def mirror(pts):
        return [(-v, w) for v, w in reversed(pts)]

    if lod >= 3:   # far out: the end flush, the glass on it
        face(u_end, half + mirror(half[:-1]), st["hood"])
        face(u_end + sign * 10, pane, st["glass"])
        if door:
            face(u_end + sign * 12, kick, st["hood"])
        return
    outline, inner = rim(half, h["ring"]["width"])
    u_wall = u_end - sign * rg["recess"]
    for a, b, c, d in zip(outline, outline[1:], inner, inner[1:]):   # the rim's face
        face(u_end, [a, b, d, c], st["hood"])
    for a, b in zip(inner, inner[1:]):   # its soffit, facing the opening
        mb.face(mb.verts_lift([(m(u_end), m(a[0]), m(a[1])), (m(u_end), m(b[0]), m(b[1])), (m(u_wall), m(b[0]), m(b[1])),
                               (m(u_wall), m(a[0]), m(a[1]))]), st["hood"], (0.0, -(b[1] - a[1]), b[0] - a[0]))
    right = inner[:len(half)]   # the inner outline's right half, foot to crown
    if lod == 2:   # the wall whole, the glass on it
        face(u_wall, right + mirror(right[:-1]), st["hood"])
        face(u_wall + sign * 10, pane, st["glass"])
        if door:
            face(u_wall + sign * 12, kick, st["hood"])
        return

    def v_at(w):
        return side_v(right, w)

    def between(w0, w1):
        return [p for p in right if w0 < p[1] < w1]

    fr = ep["frame"]
    fv, fw0, fw1 = gv + fr, gw0 - fr, gw1 + fr
    top = [(v_at(fw1), fw1)] + between(fw1, 1e9)
    face(u_wall, top + mirror(top[:-1]), st["hood"])                                    # above the window
    low = [right[0]] + between(right[0][1], fw0) + [(v_at(fw0), fw0)]
    face(u_wall, low + mirror(low), st["hood"])                                         # below it
    for s in (1, -1):                                                                   # beside it
        pts = [(fv, fw0), (v_at(fw0), fw0)] + between(fw0, fw1) + [(v_at(fw1), fw1), (fv, fw1)]
        face(u_wall, [(s * v, w) for v, w in pts], st["hood"])
    dp = ep["depth"]
    if lod == 1:   # the frame as the opening's four reveals, the mullions as strips in front of the glass
        u_in = u_wall - sign * dp
        rim_pts = [(-fv, fw0), (fv, fw0), (fv, fw1), (-fv, fw1)]
        for (va, wa), (vb, wb) in zip(rim_pts, rim_pts[1:] + rim_pts[:1]):
            mb.face(mb.verts_lift([(m(u_wall), m(va), m(wa)), (m(u_wall), m(vb), m(wb)), (m(u_in), m(vb), m(wb)), (m(u_in), m(va), m(wa))]),
                    st["trim"], (0.0, -(wb - wa), vb - va))
        face(u_in, rim_pts, st["glass"])
        for k in range(1, ep["n"]):
            vc, hw = -gv + k * ep["width"] + (k - 0.5) * pn["mullion"], pn["mullion"] / 2
            face(u_in + sign * 10, [(vc - hw, gw0), (vc + hw, gw0), (vc + hw, gw1), (vc - hw, gw1)], st["trim"])
        if door:
            face(u_in + sign * 5, kick, st["hood"])
        return
    ua, ub = sorted((m(u_wall), m(u_wall - sign * dp)))   # the frame, lining the opening a hand deep
    for (v0, w0), (v1, w1) in (((-fv, fw0), (-gv, fw1)), ((gv, fw0), (fv, fw1)), ((-gv, fw0), (gv, gw0)), ((-gv, gw1), (gv, fw1))):
        prims.box(mb, (ua, m(v0), m(w0)), (ub, m(v1), m(w1)), st["trim"])
    face(u_wall - sign * dp, pane, st["glass"])
    ua, ub = sorted((m(u_wall - sign * dp * 0.3), m(u_wall - sign * dp)))   # the mullions, in front of the glass
    for k in range(1, ep["n"]):
        vc = -gv + k * ep["width"] + (k - 0.5) * pn["mullion"]
        prims.box(mb, (ua, m(vc - pn["mullion"] / 2), m(gw0)), (ub, m(vc + pn["mullion"] / 2), m(gw1)), st["trim"])
    if door:   # the kick panel on the glass, the pull handle on the glass above it near the door's edge
        u_gl = u_wall - sign * dp
        ua, ub = sorted((m(u_gl), m(u_gl + sign * 20)))
        prims.box(mb, (ua, m(dv0), m(gw0)), (ub, m(dv1), m(dw)), st["hood"])
        hd = door["handle"]
        vh = dv1 - hd["in"]
        ua, ub = sorted((m(u_gl), m(u_gl + sign * hd["proud"])))
        prims.box(mb, (ua, m(vh - hd["width"] / 2), m(hd["w"][0])), (ub, m(vh + hd["width"] / 2), m(hd["w"][1])), st["trim"])


def side_v(half, w):
    """v (mm) of the hood's side at height w (mm), from the half profile."""
    for (v0, w0), (v1, w1) in zip(half, half[1:]):
        if w0 <= w <= w1 and w1 > w0:
            return v0 + (v1 - v0) * (w - w0) / (w1 - w0)
    return half[-1][0]


def trims(mb, h, half, lod, st):
    """The sill rail under the windows and the head trim over them, standing proud of each side between the ends."""
    u0, u1 = h["ends"]["far"], h["ends"]["line"]
    for key in ("sillRail", "headTrim"):
        t = h[key]
        lo, hi = t["bottom"], t["top"]
        v_in = max(side_v(half, lo), side_v(half, hi))
        for s in (-1, 1):
            v0, v1 = sorted((s * m(v_in - 10), s * m(v_in + t["proud"])))
            prims.box(mb, (m(u0), v0, m(lo)), (m(u1), v1, m(hi)), st["trim"])


def lining(mb, h, half, st):
    """A dark shell just inside the hood, facing in, closed behind the end walls' glass: in game the hood's own faces
    are culled from inside, so without it the windows would show straight through the hood (as the FGQ-4's,
    drive_terminal.interior_lining)."""
    ring = half + [(-v, w) for v, w in reversed(half[:-1])]
    inner = [(m(v), m(w)) for v, w in inset(ring, h["lining"])]
    n = len(inner)
    back = h["ring"]["recess"] + h["endPanes"]["depth"] + 50
    u_a, u_b = m(h["ends"]["far"] + back), m(h["ends"]["line"] - back)
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
    """The ladder on the loading side (+v), climbing across the line to the catwalk's edge, and at its sides two bent
    grab hoops, walk-through onto the deck (owner, 2026-11-03), with their mid bars."""
    la = d["ladder"]
    uc, width = m(sum(la["u"])) / 2, m(la["u"][1] - la["u"][0]) - m(51)
    foot = (uc, hg + m(la["foot"][0]), m(la["foot"][1]))
    head = (uc, hg + m(la["head"][0]), m(la["head"][1]))
    stern_heads.ladder(mb, foot, head, width, m(la["rungRise"]), lod, st["galv"], st["yellow"], stringer=m(la["stringer"]))
    hp = la["hoops"]
    if lod >= 2:
        return
    vi, vo = hg + m(hp["v"][0]), hg + m(hp["v"][1])
    foot, top = m(hp["foot"]), m(hp["top"])
    for u in hp["u"]:
        stern_heads.tube(mb, [(m(u), vi, foot), (m(u), vi, top), (m(u), vo, top), (m(u), vo, foot)], m(hp["dia"]), lod, st["galv"],
                         corner=m(hp["corner"]))
        if lod == 0:
            stern_heads.pipe(mb, (m(u), vi, m(hp["mid"])), (m(u), vo, m(hp["mid"])), m(hp["dia"]), lod, st["galv"], sides=(8, 6),
                             caps=(False, False))


def catwalk(mb, d, h, hg, lod, st):
    """The catwalk across the line end (owner, 2026-11-03; spec "catwalk"): grating on two cross beams between the
    frame pipes, from the recessed end wall (the door, end_cap) to the drawn rail's line; the railing, a bent tube
    up from the deck at the rim's face, along the -v end and the line edge and down at the +v corner, its mid rail,
    posts and toe plate. The ladder's hoops guard the +v edge."""
    cw = d["catwalk"]
    u0, u1 = m(h["ends"]["line"] - h["ring"]["recess"]), m(cw["to"])
    v0, v1 = -(hg + m(cw["v"][0])), hg + m(cw["v"][1])
    top = m(cw["top"])
    bottom = top - m(cw["thick"])
    parts.deck(mb, (u0, v0, bottom), (u1, v1, top), lod, st["galv"], channels=False)
    if lod <= 1:
        bm = cw["beams"]
        vb = hg + m(d["frame"]["v"])
        for a, b in bm["u"]:
            prims.box(mb, (m(a), -vb, bottom - m(bm["depth"])), (m(b), vb, bottom), st["galv"])
    if lod >= 3:
        return
    rl = cw["railing"]
    e, dia = m(rl["edge"]), m(rl["dia"])
    ua, ub, va, vb = m(h["ends"]["line"]) + e, u1 - e, v0 + e, v1 - e
    rail, mid = top + m(rl["top"]), top + m(rl["mid"])
    sides = (8, 4, 4)
    pts = [Vector(p) for p in ((ua, va, top), (ua, va, rail), (ub, va, rail), (ub, vb, rail), (ub, vb, top))]
    for i in (3, 1):   # the bends at its ends rounded, the corner left square over its post
        pts = prims.fillet(pts, i, m(rl["corner"]), 3 if lod == 0 else 1)
    stern_heads.tube(mb, pts, dia, lod, st["galv"], sides=sides)
    n = max(1, round((vb - va) / m(rl["posts"])))
    for k in range(n):   # the corner's post and the line edge's between the ends
        v = va + (vb - va) * k / n
        stern_heads.pipe(mb, (ub, v, top), (ub, v, rail), dia, lod, st["galv"], sides=sides, caps=(False, False))
    if lod <= 1:
        stern_heads.tube(mb, [(ua, va, mid), (ub, va, mid), (ub, vb, mid)], dia, lod, st["galv"], sides=sides)
        t = m(rl["toeThick"]) / 2
        stern_heads.rect_path(mb, [(ua, v0 + t), (u1 - t, v0 + t), (u1 - t, v1)], t, top, top + m(rl["toe"]), st["galv"])


def guides(mb, a, c, d, hg, lod, st):
    """The guide sheaves at the hood's mouth (rope on top), each on its axle from the pendant plate inboard of it
    (their mounts: spec "steel")."""
    rope, rope_r = m(c["ropeElevation"]), m(c["ropeDiameter"]) / 2
    gs = d["guideSheaves"]
    for side, name in ((-1, "l1"), (1, "r1")):
        centre = Vector((m(gs["u"]), side * hg, rope - rope_r - m(gs["dia"]) / 2))
        target = MeshBuilder() if lod <= 1 else mb
        if lod <= 2:   # the axle at LOD2 too, or the sheave floats clear of its pendant plate
            parts.sheave(target, centre, V, m(gs["dia"]), m(gs["width"]), lod, st["red"], st["rubber"])
            prims.cylinder(mb, (centre.x, side * (hg + m(gs["axleFrom"])), centre.z), (centre.x, side * (hg + m(gs["width"]) / 2), centre.z),
                           m(gs["axle"]) / 2, parts.sides(lod, 10, 6), st["rod"])
        if lod <= 1:
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
    catwalk(body, d, h, hg, lod, st)
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
    a.dims.update({"bullwheelU": 0, "portalU": d["portal"]["legs"]["u"], "hoodFarEnd": h["ends"]["far"],
                   "hoodCrown": max(w for _, w in h["profile"]), "pinU": d["heads"]["bottom"]["pinU"],
                   "ropeExitDeg": round(math.degrees(exit_angle), 2)})
    return a
