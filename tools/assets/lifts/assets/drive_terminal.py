"""Sessellift FGQ-4 drive terminal (top station): a barrel hood over the drive and a horizontal bullwheel,
cantilevered 3.23 m behind a concrete pier, with an entry deck, a lower entry platform, a ladder and a
lifting portal toward the line.

Loaded chairs arrive on the left rope (v-) and unload over the pier; the bullwheel turns counter-clockwise
seen from above. Dimensions: sessellift_fgq4.json "drive" and "common" (millimetres), checked against the
source elevations by the adversarial review (review record, Gate 1).
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import GLASS, MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "terminal"
LODS = 4


def m(x):
    return x / 1000.0


def styles(lod):
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("paint_grey", snow=0.5),
        "galv": Style("galvanised", snow=0.5),
        "dark": Style("paint_dark", snow=0.0),
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "hood": Style("livery", livery=1.0, snow=1.0),
        "band": Style("white", snow=0.0),
        "trim": Style("trim_dark", snow=0.0),
        "glass": Style("glass", slot=GLASS, smooth=False) if lod <= 1 else Style("glass", smooth=False),
        "interior": Style("interior"),
        "yellow": Style("safety_yellow"),
        "rod": Style("machined"),
    }


# -- hood ------------------------------------------------------------------------------------------
def end_u(end, w):
    """u of a hood end at height w: vertical roof cap, raked end wall, set-in vertical band (metres)."""
    cap_from = m(end["capFrom"])
    (ut, wt), (ub, wb) = [(m(a), m(b)) for a, b in (end["rakeTop"], end["rakeBottom"])]
    if w >= cap_from:
        return m(end["cap"])
    if w >= wt:
        return ut + (m(end["cap"]) - ut) * (w - wt) / (cap_from - wt)
    if w >= wb - 1e-6:
        return ub + (ut - ub) * (w - wb) / (wt - wb)
    return m(end["band"])


def hood_profile(h, lod):
    """Half profile (v >= 0, w) from the soffit's inner edge to the crown, with rows at every break height."""
    half = [(m(v), m(w)) for v, w in h["profile"]]
    for edge in [m(b) for b in h["breaks"]]:
        for i in range(len(half) - 1):
            (v0, w0), (v1, w1) = half[i], half[i + 1]
            if w0 < edge < w1:
                t = (edge - w0) / (w1 - w0)
                half.insert(i + 1, (v0 + (v1 - v0) * t, edge))
                break
    if lod == 0:   # rounder barrel near the camera: subdivide the long arc segments
        fine = [half[0]]
        for (v0, w0), (v1, w1) in zip(half, half[1:]):
            n = max(1, round(((v1 - v0) ** 2 + (w1 - w0) ** 2) ** 0.5 / 0.3)) if w0 >= 3.873 and w1 <= 6.42 else 1
            for k in range(1, n + 1):
                fine.append((v0 + (v1 - v0) * k / n, w0 + (w1 - w0) * k / n))
        half = fine
    elif lod >= 2:
        keep = {0, len(half) - 1}
        step = 2 if lod == 2 else 4
        half = [p for i, p in enumerate(half) if i in keep or i % step == 0]
    return half


def build_hood(spec, lod, st):
    d = spec["drive"]
    h = d["hood"]
    mb = MeshBuilder()
    half = hood_profile(h, lod)
    prof = [(-v, w) for v, w in half] + [(v, w) for v, w in reversed(half[:-1])]   # left base, over the crown, right base
    win = h["windows"]
    breaks = []
    for j in reversed(range(win["bays"])):
        u0 = m(win["firstU"]) - j * m(win["bay"] + win["rib"])
        breaks += [u0 - m(win["bay"]), u0]
    if lod >= 3:   # far away: no window columns, just the shell
        breaks = []
    bay_spans = set(range(1, len(breaks), 2)) if lod <= 2 else set()
    low, high = m(win["low"]), m(win["high"])
    band_lo, band_hi = m(h["whiteBand"]["low"]), m(h["whiteBand"]["high"])
    far, line = h["farEnd"], h["lineEnd"]
    grid = []
    for v, w in prof:
        grid.append([mb.vert((end_u(far, w), v, w))] + [mb.vert((u, v, w)) for u in breaks] + [mb.vert((end_u(line, w), v, w))])
    n_rows = len(prof)
    for i in range(n_rows):
        i1 = (i + 1) % n_rows
        (v0, w0), (v1, w1) = prof[i], prof[i1]
        bottom = i1 == 0
        for k in range(len(breaks) + 1):
            lo_w, hi_w = min(w0, w1), max(w0, w1)
            if bottom or hi_w <= band_lo + 1e-6:
                style = st["trim"]
            elif hi_w <= band_hi + 1e-6:
                style = st["band"]
            elif k in bay_spans and low - 1e-6 <= lo_w and hi_w <= high + 1e-6:
                style = st["glass"]
            else:
                style = st["hood"].but(smooth=lo_w > 3.87)
            out = (0.0, 0.0, -1.0) if bottom else (0.0, (v0 + v1) / 2, (w0 + w1) / 2 - 4.5)
            mb.face([grid[i][k], grid[i][k + 1], grid[i1][k + 1], grid[i1][k]], style, out)
    # end walls: horizontal strips between matching left/right rows, sharing their vertices so the wall is
    # one continuous surface (each strip is planar; strips meet at the rake and cap creases only)
    for end, sign in ((far, -1), (line, 1)):
        rows = []
        for v, w in half:
            u = end_u(end, w)
            left = mb.vert((u, -v, w))
            right = left if v < 1e-6 else mb.vert((u, v, w))
            rows.append((left, right))
        for (la, ra), (lb, rb) in zip(rows, rows[1:]):
            idx = [la, ra, rb, lb] if rb != lb else [la, ra, rb]
            mb.face(idx, st["hood"].but(smooth=False), (sign, 0, 0))
    if lod <= 1:
        interior_lining(mb, h, half, st)
    if lod <= 2:
        end_openings(mb, h, half, st, lod)
    return mb


def interior_lining(mb, h, half, st):
    """A dark shell just inside the hood, facing in, closed at both ends and along the floor. In game the hood's
    own faces are culled from inside, so without it the side glass would show straight through the hood; with it
    every window reads as tinted glass over a dark interior, like the photos. The drive machinery sits inside."""
    band = m(h["whiteBand"]["high"])
    ring = [(v, w) for v, w in half if w >= band]
    inset = 0.08
    right = []
    for i, (v, w) in enumerate(ring):   # offset inward along the profile's normal
        (va, wa), (vb, wb) = ring[max(0, i - 1)], ring[min(len(ring) - 1, i + 1)]
        ln = math.hypot(vb - va, wb - wa) or 1.0
        right.append((max(0.0, v - (wb - wa) / ln * inset), w + (vb - va) / ln * inset))
    prof = right + [(-v, w) for v, w in reversed(right[:-1])]   # right side up over the crown, down the left side
    u_a = m(h["farEnd"]["band"]) + 0.05   # the end walls are innermost at the band
    u_b = m(h["lineEnd"]["band"]) - 0.05
    centre = (0.0, (band + prof[len(prof) // 2][1]) / 2)
    dark = st["interior"].but(smooth=False)
    for (v0, w0), (v1, w1) in zip(prof, prof[1:]):
        mv, mw = (v0 + v1) / 2, (w0 + w1) / 2
        nv, nw = w1 - w0, -(v1 - v0)
        if nv * (centre[0] - mv) + nw * (centre[1] - mw) < 0:
            nv, nw = -nv, -nw
        mb.face(mb.verts_lift([(u_a, v0, w0), (u_b, v0, w0), (u_b, v1, w1), (u_a, v1, w1)]), dark, (0.0, nv, nw))
    vf = prof[0][0]   # floor, and the two ends closed by the profile and its floor chord
    mb.face(mb.verts_lift([(u_a, -vf, band), (u_b, -vf, band), (u_b, vf, band), (u_a, vf, band)]), dark, (0.0, 0.0, 1.0))
    for u, facing in ((u_a, 1.0), (u_b, -1.0)):
        mb.face(mb.verts_lift([(u, v, w) for v, w in prof]), dark, (facing, 0.0, 0.0))


def arc_v(half, w):
    """The hood's outer half-width at height w (metres), from the profile."""
    for (v0, w0), (v1, w1) in zip(half, half[1:]):
        if w0 <= w <= w1 and w1 > w0:
            return v0 + (v1 - v0) * (w - w0) / (w1 - w0)
    return half[-1][0]


def end_openings(mb, h, half, st, lod):
    """Windows and the door on the end walls, as thin panels just proud of the wall. Each pane has a dark backing
    between it and the wall, so the end windows read like the side windows (tinted glass over a dark interior)
    rather than glass over the hood colour."""
    far, line = h["farEnd"], h["lineEnd"]
    proud = 0.012

    def rows(end, w0, w1, n):
        """Heights for a pane's edges: n even steps plus the end wall's creases in between, so every vertex sits on
        the raked wall or the vertical cap."""
        creases = [m(end["rakeBottom"][1]), m(end["rakeTop"][1]), m(end["capFrom"])]
        return sorted({w0, w1} | {w0 + (w1 - w0) * k / n for k in range(1, n)} | {c for c in creases if w0 < c < w1})

    def pane(end, sign, pts, style):
        if style is st["glass"] and lod <= 1:
            mb.face(mb.verts_lift([(end_u(end, w) + sign * proud * 0.5, v, w) for v, w in pts]), st["interior"], (sign, 0, 0))
        mb.face(mb.verts_lift([(end_u(end, w) + sign * proud, v, w) for v, w in pts]), style, (sign, 0, 0))

    def panel(end, sign, v0, v1, w0, w1, style, inset=None, arc=False):
        """v0 to v1 between w0 and w1. arc: the outer edge follows the barrel, inset from it (v1 unused); inset
        without arc: v1, but kept that far inside the barrel. Negative v0 mirrors to the -v side."""
        ws = rows(end, w0, w1, 6 if inset is not None else 1)
        s = -1.0 if v0 < 0 else 1.0
        left = [(v0, w) for w in ws]
        if arc:
            right = [(s * (arc_v(half, w) - inset), w) for w in ws]
        elif inset is not None:
            right = [(s * min(abs(v1), arc_v(half, w) - inset), w) for w in ws]
        else:
            right = [(v1, w) for w in ws]
        pane(end, sign, left + list(reversed(right)), style)

    # every end window shares the side windows' band: bottoms and tops on the side glass lines (owner request)
    fw, lw = h["farWall"], h["lineWall"]
    for s in (-1, 1):   # far end: two slots either side of the axis and two barrel-following corner windows
        sl, sd = fw["slots"], fw["side"]
        a, b = sorted((s * m(sl["vIn"]), s * m(sl["vOut"])))
        panel(far, -1, a, b, m(sl["low"]), m(sl["high"]), st["glass"])
        panel(far, -1, s * m(sd["vIn"]), None, m(sd["low"]), m(sd["high"]), st["glass"], inset=m(sd["inset"]), arc=True)
    door = lw["door"]   # line end: door on +v (steel lower panel, glazed upper, transom above), one window on -v
    panel(line, 1, m(door["vFrom"]), m(door["vTo"]), m(door["low"]), m(door["glassLow"]), st["trim"])
    panel(line, 1, m(door["vFrom"]), m(door["vTo"]), m(door["glassLow"]), m(door["high"]), st["glass"])
    tr = door["transom"]
    panel(line, 1, m(door["vFrom"]), m(door["vTo"]), m(tr["low"]), m(tr["high"]), st["glass"], inset=m(lw["side"]["inset"]))
    sd = lw["side"]
    panel(line, 1, -m(sd["vIn"]), None, m(sd["low"]), m(sd["high"]), st["glass"], inset=m(sd["inset"]), arc=True)


# -- entry end -------------------------------------------------------------------------------------
def build_entry(mb, d, lod, st):
    """The entry head (integrated first tower) and the walkways: the chamfered crossbeam at rope height with its
    foot plates, tied to the entry beams by the drawn link (end plates, rods with tabs, a member against the
    crossbeam); the lifting portal (152 mm square legs cut level at both ends, top beam with through-bolts,
    25 mm lug plates with eyes, J-handrails); the upper walkway with its railings and tube gate; the lower
    entry platform with its end member, U hoop and struts; the ladder."""
    cb = d["crossbeam"]
    u0c, u1c, wb0, wb1 = m(cb["uFrom"]), m(cb["uTo"]), m(cb["bottom"]), m(cb["top"])
    hvc = m(cb["v"]) / 2
    if lod <= 1:   # section with chamfered long edges
        c = m(cb["chamfer"])
        sec = [(u0c + c, wb0), (u1c - c, wb0), (u1c, wb0 + c), (u1c, wb1 - c), (u1c - c, wb1), (u0c + c, wb1), (u0c, wb1 - c), (u0c, wb0 + c)]
        prims.prism(mb, sec, (0, -hvc, 0), U, W, V, 2 * hvc, st["steel"])
    else:
        prims.box(mb, (u0c, -hvc, wb0), (u1c, hvc, wb1), st["steel"])
    fp = cb["footPlates"]
    for s in ((-1, 1) if lod <= 2 else ()):
        prims.box(mb, (m(fp["uFrom"]), s * m(fp["v"]) - m(fp["halfWidth"]), m(fp["bottom"])),
                  (m(fp["uTo"]), s * m(fp["v"]) + m(fp["halfWidth"]), m(fp["top"])), st["steel"])
    # link to the terminal: end plates on the entry beams, a member against the crossbeam, rods with tabs
    lk, eb = d["link"], d["entryBeams"]
    mem = lk["member"]
    prims.box(mb, (m(mem["u"][0]), -m(mem["v"]), m(mem["w"][0])), (m(mem["u"][1]), m(mem["v"]), m(mem["w"][1])), st["steel"])
    if lod <= 2:
        pl = lk["plates"]
        for s in (-1, 1):
            vc = s * (m(eb["vIn"]) + m(eb["vOut"])) / 2
            prims.box(mb, (m(pl["u"][0]), vc - m(pl["thick"]) / 2, m(pl["w"][0])), (m(pl["u"][1]), vc + m(pl["thick"]) / 2, m(pl["w"][1])), st["steel"])
    if lod <= 1:
        rd, tb, lw = lk["rods"], lk["tab"], lk["lower"]
        for s in (-1, 1):
            vr = s * m(rd["v"])
            if lod == 0:
                prims.cylinder(mb, (m(rd["u"]), vr, m(rd["w"][0])), (m(rd["u"]), vr, m(rd["w"][1])), m(rd["dia"]) / 2, 6, st["rod"])
            prims.box(mb, (m(tb["u"][0]), vr - m(tb["width"]) / 2, m(tb["w"][0])), (m(tb["u"][1]), vr + m(tb["width"]) / 2, m(tb["w"][1])), st["steel"])
            prims.box(mb, (m(lw["u"][0]), vr - m(lw["width"]) / 2, m(lw["w"][0])), (m(lw["u"][1]), vr + m(lw["width"]) / 2, m(lw["w"][1])), st["steel"])
    # lifting portal
    po = d["portal"]
    bm = po["beam"]
    if lod <= 2:
        lg = po["leg"]
        for s in (-1, 1):   # 152 mm square legs leaning out 14 degrees, cut level on the foot plate and under the beam
            f0, f1 = sorted((s * m(lg["footV"][0]), s * m(lg["footV"][1])))
            h0, h1 = sorted((s * m(lg["headV"][0]), s * m(lg["headV"][1])))
            poly = [(f0, m(lg["footW"])), (f1, m(lg["footW"])), (h1, m(lg["headW"])), (h0, m(lg["headW"]))]
            prims.prism(mb, poly, (m(lg["uFrom"]), 0, 0), V, W, U, m(lg["uTo"]) - m(lg["uFrom"]), st["steel"])
        prims.box(mb, (m(bm["uFrom"]), -m(bm["v"]) / 2, m(bm["bottom"])), (m(bm["uTo"]), m(bm["v"]) / 2, m(bm["top"])), st["steel"])
        if lod == 0:   # through-bolts, heads on both faces
            bo = bm["bolts"]
            for vb in bo["v"]:
                for uf, du in ((m(bm["uFrom"]), -0.015), (m(bm["uTo"]), 0.015)):
                    prims.cylinder(mb, (uf, m(vb), m(bo["w"])), (uf + du, m(vb), m(bo["w"])), m(bo["dia"]) / 2, 6, st["rod"])
    if lod <= 1:   # 25 mm lug plates across the beam ends, narrowing below it to a round end with the eye
        lu = po["lugs"]
        t0, t1 = m(lu["top"][0]), m(lu["top"][1])
        n0, n1 = m(lu["neck"][0]), m(lu["neck"][1])
        bw0, bw1, cr = m(bm["bottom"]), m(bm["top"]), m(lu["corner"])
        nr = (n1 - n0) / 2
        cu_ = (n0 + n1) / 2
        ctr = m(lu["bottom"]) + nr
        outline = [(t0, bw0), (t0, bw1 - cr), (t0 + cr, bw1), (t1 - cr, bw1), (t1, bw1 - cr), (t1, bw0), (n1, m(lu["neckW"])), (n1, ctr)]
        outline += [(cu_ + nr * math.cos(a_), ctr + nr * math.sin(a_)) for a_ in (math.pi * k / 6 for k in range(-1, -6, -1))]
        outline += [(n0, ctr), (n0, m(lu["neckW"]))]
        th = m(lu["thick"])
        for s in (-1, 1):
            for vl in lu["v"]:
                vc = s * m(vl)
                prims.prism(mb, outline, (0, vc - th / 2, 0), U, W, V, th, st["yellow"])
                if lod == 0:
                    eu, ew = m(lu["eye"][0]), m(lu["eye"][1])
                    prims.cylinder(mb, (eu, vc - th / 2 - 0.012, ew), (eu, vc + th / 2 + 0.012, ew), m(lu["boss"]) / 2, 10, st["yellow"])
        jr = po["jRails"]   # J-handrails in the portal plane: from the beam, round a bend, level to the legs
        vt, drop, bend, wl, to = m(jr["v"]), m(jr["drop"]), m(jr["bend"]), m(jr["w"]), m(jr["to"])
        n_arc = 4 if lod == 0 else 2
        for s in (-1, 1):
            pts = [Vector((m(po["u"]), s * vt, bw0))]
            pts += [Vector((m(po["u"]), s * (vt + bend * (1 - math.cos(a_))), drop - bend * math.sin(a_)))
                    for a_ in (math.pi / 2 * k / n_arc for k in range(n_arc + 1))]
            pts.append(Vector((m(po["u"]), s * to, wl)))
            prims.tube_path(mb, pts, m(jr["dia"]) / 2, 6 if lod == 0 else 4, st["galv"], caps=False)
    # upper walkway on the +v longitudinal beam, with the hood wing and the ladder landing
    wk = d["walkway"]
    deck, t = m(wk["deck"]), 0.05
    stringer = m(wk["stringer"])
    u0, u1, v0, v1 = m(wk["uFrom"]), m(wk["uTo"]), m(wk["vFrom"]), m(wk["vTo"])
    wing, land = wk["hoodWing"], wk["landing"]
    decks = [((u0, v0, deck - t), (u1, v1, deck)),
             ((m(wing["uFrom"]), m(wing["vFrom"]), deck - t), (m(wing["uTo"]), v0, deck)),
             ((m(land["uFrom"]), m(land["vFrom"]), deck - t), (m(land["uTo"]), v0, deck))]
    for lo_, hi_ in (decks if lod <= 2 else decks[:1]):
        parts.deck(mb, lo_, hi_, lod, st["galv"])
    if lod <= 2:
        for vs in (v0 + 0.05, v1 - 0.05):   # stringers under the walkway
            prims.box(mb, (u0, vs - 0.05, deck - stringer), (u1, vs + 0.05, deck - t), st["steel"])
    rl = wk["rail"]
    rail_h = m(rl["top"]) - deck
    wing_v = m(wing["vFrom"])
    posts = [(m(pu), v1 - 0.03, deck) for pu in rl["posts"]] + [(u1 - 0.03, v0 + 0.03, deck)]   # as drawn: ends and a gate pair
    parts.railing(mb, [(u0 + 0.03, v1 - 0.03, deck), (u1 - 0.03, v1 - 0.03, deck), (u1 - 0.03, v0 + 0.03, deck)], rail_h, lod, st["galv"],
                  posts_at=posts)
    parts.railing(mb, [(m(land["uFrom"]), v0 + 0.03, deck), (m(wing["uTo"]), v0 + 0.03, deck), (m(wing["uTo"]), wing_v + 0.03, deck),
                       (u0 + 0.03, wing_v + 0.03, deck)], rail_h, lod, st["galv"])
    gate = wk["gate"]
    if lod <= 1:   # tube-frame gate, two open panels
        gr, gu = m(gate["tube"]) / 2, m(gate["u"])
        gv0, gv1, gb, gt = m(gate["vFrom"]), m(gate["vTo"]), m(gate["bottom"]), m(gate["top"])
        frame = [Vector((gu, gv0, gb)), Vector((gu, gv1, gb)), Vector((gu, gv1, gt)), Vector((gu, gv0, gt))]
        prims.tube_path(mb, frame, gr, 6 if lod == 0 else 4, st["yellow"], closed=True)
        prims.cylinder(mb, (gu, gv0, m(gate["mid"])), (gu, gv1, m(gate["mid"])), gr * 0.8, 6 if lod == 0 else 4, st["yellow"], caps=(False, False))
        if lod == 0:
            gm = (gv0 + gv1) / 2
            prims.cylinder(mb, (gu, gm, gb), (gu, gm, gt), gr * 0.8, 6, st["yellow"], caps=(False, False))
    # lower entry platform beyond the crossbeam: end member on the crossbeam face, U hoop across its end, struts
    ep = d["entryPlatform"]
    hv = m(ep["v"]) / 2
    parts.deck(mb, (m(ep["uFrom"]), -hv, m(ep["under"])), (m(ep["uTo"]), hv, m(ep["deck"])), lod, st["galv"])
    if lod <= 2:
        em = ep["endMember"]
        prims.box(mb, (m(em[0]), -hv, wb0), (m(em[1]), hv, wb1), st["steel"])
        hp = ep["hoop"]
        c = m(hp["corner"])
        pts = [(m(hp["u"]), -hv + 0.03, m(ep["deck"])), (m(hp["u"]), -hv + 0.03, m(hp["top"]) - c), (m(hp["u"]), -hv + 0.03 + c, m(hp["top"])),
               (m(hp["u"]), hv - 0.03 - c, m(hp["top"])), (m(hp["u"]), hv - 0.03, m(hp["top"]) - c), (m(hp["u"]), hv - 0.03, m(ep["deck"]))]
        prims.tube_path(mb, [Vector(p) for p in pts], 0.02, 6 if lod == 0 else 4, st["galv"])
        if lod <= 1:
            prims.cylinder(mb, (m(hp["u"]), -hv + 0.03, m(hp["mid"])), (m(hp["u"]), hv - 0.03, m(hp["mid"])), 0.016, 6 if lod == 0 else 4, st["galv"])
            sr = ep["strut"]
            sf, stt, sz = sr["from"], sr["to"], m(sr["size"])
            for s in (-1, 1):
                vs_ = s * m(sr["v"])
                prims.beam(mb, (m(sf[0]), vs_, m(sf[1])), (m(stt[0]), vs_, m(stt[1])), sz, sz, st["steel"])
    # ladder from the landing plate up to the walkway; handrails rise with it to capped ends (no return leg)
    ld = d["ladder"]
    foot = Vector((m(ld["foot"][0]), m(ld["foot"][1]), m(ld["foot"][2])))
    head = Vector((m(ld["head"][0]), m(ld["head"][1]), m(ld["head"][2])))
    parts.ladder(mb, foot, head, 2 * m(ld["halfWidth"]), lod, st["galv"], rung_every=m(ld["rungs"]), handrails=False)
    if lod <= 1:
        hr = m(ld["handrail"])
        gn = ld["gooseneck"]
        for s in (-1, 1):
            vv = s * (m(ld["halfWidth"]) + 0.03)
            pts = [foot + Vector((0.05, vv, hr)), head + Vector((0, vv, hr)), Vector((m(gn[0]), vv, m(gn[1])))]
            prims.tube_path(mb, pts, 0.019, 6 if lod == 0 else 4, st["galv"])
    lp = d["landingPlate"]
    if lod <= 2:
        prims.box(mb, (m(lp["uFrom"]), -m(lp["v"]) / 2, m(lp["bottom"])), (m(lp["uTo"]), m(lp["v"]) / 2, m(lp["top"])), st["galv"].but(snow=0.9))


def build(spec, lod, stage):
    c, d = spec["common"], spec["drive"]
    st = styles(lod)
    a = Asset("sessellift_fgq4_drive")
    body = MeshBuilder()
    rope = m(c["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    rope_r = m(c["ropeDiameter"]) / 2
    u_bw = m(d["bullwheel"]["u"])

    # pier and footing (LP7), steel pier head, longitudinal beams and the drive base frame
    pi, fo = d["pier"], d["footing"]
    prims.box(body, (-m(fo["u"]) / 2, -m(fo["v"]) / 2, m(fo["bottom"])), (m(fo["u"]) / 2, m(fo["v"]) / 2, m(fo["top"])), st["concrete"])
    prims.box(body, (-m(pi["u"]) / 2, -m(pi["v"]) / 2, m(pi["bottom"])), (m(pi["u"]) / 2, m(pi["v"]) / 2, m(pi["top"])), st["concrete"], skip=("-2",))
    ph = d["pierHead"]
    prims.box(body, (-m(ph["u"]) / 2, -m(ph["v"]) / 2, m(ph["bottom"])), (m(ph["u"]) / 2, m(ph["v"]) / 2, m(ph["top"])), st["steel"])
    eb = d["entryBeams"]
    for s in (-1, 1):
        a_, b_ = sorted((s * m(eb["vIn"]), s * m(eb["vOut"])))
        prims.box(body, (m(eb["uFrom"]), a_, m(eb["bottom"])), (m(eb["uTo"]), b_, m(eb["top"])), st["steel"])
    bf = d["baseFrame"]
    prims.box(body, (m(bf["uTo"]), -m(bf["v"]) / 2, m(bf["bottom"])), (m(bf["uFrom"]), m(bf["v"]) / 2, m(bf["top"])), st["steel"])

    # drive machinery behind the glass (silhouettes only; LOD0-1)
    if lod <= 1:
        g = d["gearbox"]
        prims.box(body, (u_bw - m(g["u"]) / 2, -m(g["v"]) / 2, m(g["bottom"])), (u_bw + m(g["u"]) / 2, m(g["v"]) / 2, m(g["top"])), st["interior"])
        mo = d["motor"]
        prims.cylinder(body, (m(mo["from"]), 0, m(mo["w"])), (m(mo["to"]), 0, m(mo["w"])), m(mo["dia"]) / 2, parts.sides(lod, 12, 8), st["interior"])
        cb = d["cabinet"]
        prims.box(body, (m(cb["uFrom"]), m(cb["vFrom"]), m(cb["bottom"])), (m(cb["uTo"]), m(cb["vTo"]), m(cb["top"])), st["interior"])

    # hood, with its gutter lip in the profile and the down spout on the +v side
    hood = build_hood(spec, lod, st)
    lo, hi = hood.bounds_lift()
    a.dims.update({"hoodTopLength": round((hi[0] - lo[0]) * 1000), "hoodWidth": round((hi[1] - lo[1]) * 1000),
                   "hoodCrown": round(hi[2] * 1000), "hoodLineEndTop": round(hi[0] * 1000), "hoodFarEndTop": round(lo[0] * 1000)})
    body.merge(hood)
    if lod <= 1:
        ds = d["hood"]["downSpout"]
        prims.cylinder(body, tuple(m(x) for x in ds["from"]), tuple(m(x) for x in ds["to"]), m(ds["dia"]) / 2, 8 if lod == 0 else 5, st["trim"])

    build_entry(body, d, lod, st)
    # support trains on both ropes at the entry frame (rope on top; from photos, see spec common.sheaveTrain)
    et, tr = d["entryTrains"], c["sheaveTrain"]
    train = {"n": tr["n"], "pitch": m(tr["pitch"]), "rocker": m(tr["rocker"]), "beam": m(tr["beam"])}
    pl = et["plate"]   # hung from plates bolted to the crossbeam ends
    plate = {"v": (m(pl["v"][0]), m(pl["v"][1])), "top": m(pl["top"]), "width": m(pl["width"]), "pin": m(pl["pin"])}
    for side, name in ((-1, "l"), (1, "r")):
        parts.sheave_train(body, a, name, m(et["u"]), side * hg, rope, rope_r, m(c["guideSheave"]["dia"]), m(c["guideSheave"]["width"]),
                           train, et["mode"], None, lod, st, plate=plate)
    u_out = m(et["u"]) + (tr["n"] - 1) / 2 * m(tr["pitch"])   # the rope leaves over the outermost train sheave

    # guide sheaves between the pier and the bullwheel (rope rides on top), hung inboard from the base frame
    gs = c["guideSheave"]
    for name, side in (("l1", -1), ("r1", 1)):
        r = m(gs["dia"]) / 2
        centre = Vector((m(d["guideSheaves"]["u"]), side * hg, rope - rope_r - r))
        target = MeshBuilder() if lod <= 1 else body
        if lod <= 2:
            parts.sheave(target, centre, V, m(gs["dia"]), m(gs["width"]), lod, st["red"], st["rubber"])
            parts.sheave_bracket(body, centre, m(gs["width"]), m(bf["bottom"]), lod, st["steel"])
        if lod <= 1:
            a.parts[f"sheave_{name}"] = target
            a.pivots[f"sheave_{name}"] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}

    # bullwheel (pivoted on its vertical axle at rope elevation), sheltered by the hood (no snow)
    bw_spec = parts.bullwheel_spec(c)
    bw_centre = Vector((u_bw, 0.0, rope))
    if lod <= 2:
        bw = MeshBuilder()
        parts.bullwheel(bw, bw_centre, bw_spec, lod, st["dark"])
        a.parts["bullwheel"] = bw
        a.pivots["bullwheel"] = {"pos": tuple(bw_centre), "axis": (0.0, 0.0, 1.0)}
    else:
        parts.bullwheel(body, bw_centre, bw_spec, lod, st["dark"])

    a.body = body
    cbm = d["crossbeam"]
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (u_bw, -hg, rope), "rope_right_bw": (u_bw, hg, rope),
        "rope_left_out": (u_out, -hg, rope), "rope_right_out": (u_out, hg, rope),
        "chair_unload": (0.0, -hg, rope),
        "foundation_base": (0.0, 0.0, m(d["footing"]["bottom"])),
    }
    a.dims.update({"bullwheelU": round(u_bw * 1000), "guideSheaveU": d["guideSheaves"]["u"], "columnU": pi["u"],
                   "columnV": pi["v"], "columnTop": pi["top"], "platformEnd": d["entryPlatform"]["uTo"], "entryU": d["portal"]["u"]})
    return a
