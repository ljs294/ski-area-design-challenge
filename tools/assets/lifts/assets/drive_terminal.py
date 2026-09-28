"""Sessellift FGQ-4 drive terminal (top station): a barrel hood over the drive and a horizontal bullwheel,
cantilevered 3.23 m behind a concrete pier, with an entry deck, a lower entry platform, a ladder and a
lifting portal toward the line.

Loaded chairs arrive on the left rope (v-) and unload over the pier; the bullwheel turns counter-clockwise
seen from above. Dimensions: sessellift_fgq4.json "drive" and "common" (millimetres), checked against the
source elevations by the adversarial review (review record, Gate 1).
"""
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
    if lod <= 2:
        end_openings(mb, h, half, st, lod)
    return mb


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

    def pane(end, sign, pts, style):
        if style is st["glass"] and lod <= 1:
            mb.face(mb.verts_lift([(end_u(end, w) + sign * proud * 0.5, v, w) for v, w in pts]), st["interior"], (sign, 0, 0))
        mb.face(mb.verts_lift([(end_u(end, w) + sign * proud, v, w) for v, w in pts]), style, (sign, 0, 0))

    def panel(end, sign, v0, v1, w0, w1, style, arc=None):
        rows = [w0 + (w1 - w0) * k / 6 for k in range(7)] if arc else [w0, w1]
        left = [(v0, w) for w in rows]
        right = [((arc_v(half, w) - arc) if arc else v1, w) for w in rows]
        pane(end, sign, left + list(reversed(right)), style)

    fw, lw = h["farWall"], h["lineWall"]
    for s in (-1, 1):   # far end: two slots either side of the axis and two barrel-following side windows
        sl = fw["slots"]
        a, b = sorted((s * m(sl["vIn"]), s * m(sl["vOut"])))
        panel(far, -1, a, b, m(sl["low"]), m(sl["high"]), st["glass"])
        sd = fw["side"]
        if s > 0:
            panel(far, -1, m(sd["vIn"]), None, m(sd["low"]), m(sd["high"]), st["glass"], arc=m(sd["inset"]))
        else:
            rows = [m(sd["low"]) + (m(sd["high"]) - m(sd["low"])) * k / 6 for k in range(7)]
            pane(far, -1, [(-(arc_v(half, w) - m(sd["inset"])), w) for w in rows] + [(-m(sd["vIn"]), w) for w in reversed(rows)], st["glass"])
    door = lw["door"]   # line end: door on +v (steel lower panel, glazed upper), one window on -v
    panel(line, 1, m(door["vFrom"]), m(door["vTo"]), m(door["low"]), m(door["glassLow"]), st["trim"])
    panel(line, 1, m(door["vFrom"]), m(door["vTo"]), m(door["glassLow"]), m(door["high"]), st["glass"])
    sd = lw["side"]
    rows = [m(sd["low"]) + (m(sd["high"]) - m(sd["low"])) * k / 6 for k in range(7)]
    pane(line, 1, [(-(arc_v(half, w) - m(sd["inset"])), w) for w in rows] + [(-m(sd["vIn"]), w) for w in reversed(rows)], st["glass"])


# -- entry end -------------------------------------------------------------------------------------
def build_entry(mb, d, lod, st):
    cb = d["crossbeam"]
    prims.box(mb, (m(cb["uFrom"]), -m(cb["v"]) / 2, m(cb["bottom"])), (m(cb["uTo"]), m(cb["v"]) / 2, m(cb["top"])), st["steel"])
    fp = cb["footPlates"]
    for s in ((-1, 1) if lod <= 2 else ()):
        prims.box(mb, (m(fp["uFrom"]), s * m(fp["v"]) - m(fp["halfWidth"]), m(fp["bottom"])),
                  (m(fp["uTo"]), s * m(fp["v"]) + m(fp["halfWidth"]), m(fp["top"])), st["steel"])
    # lifting portal: legs lean inward going down onto the crossbeam's foot plates
    po = d["portal"]
    if lod <= 2:
        leg = po["leg"]
        for s in (-1, 1):
            foot = (m(po["u"]), s * m(leg["foot"][0]), m(leg["foot"][1]))
            head = (m(po["u"]), s * m(leg["head"][0]), m(leg["head"][1]))
            prims.beam(mb, foot, head, m(leg["across"]), m(leg["along"]), st["steel"], up=U)
        bm = po["beam"]
        prims.box(mb, (m(bm["uFrom"]), -m(bm["v"]) / 2, m(bm["bottom"])), (m(bm["uTo"]), m(bm["v"]) / 2, m(bm["top"])), st["steel"])
        if lod <= 1:
            for s in (-1, 1):
                for vl in po["lugs"]["v"]:
                    prims.box(mb, (m(po["u"]) - 0.012, s * m(vl) - 0.06, m(po["lugs"]["bottom"])),
                              (m(po["u"]) + 0.012, s * m(vl) + 0.06, m(bm["bottom"])), st["yellow"])
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
    if lod <= 1:
        prims.box(mb, (m(gate["u"]) - 0.02, m(gate["vFrom"]), deck + 0.15), (m(gate["u"]) + 0.02, m(gate["vTo"]), m(gate["top"])), st["yellow"])
    # lower entry platform beyond the crossbeam, with a U hoop across its end and a strut under it
    ep = d["entryPlatform"]
    hv = m(ep["v"]) / 2
    parts.deck(mb, (m(ep["uFrom"]), -hv, m(ep["under"])), (m(ep["uTo"]), hv, m(ep["deck"])), lod, st["galv"])
    if lod <= 2:
        hp = ep["hoop"]
        c = m(hp["corner"])
        pts = [(m(hp["u"]), -hv + 0.03, m(ep["deck"])), (m(hp["u"]), -hv + 0.03, m(hp["top"]) - c), (m(hp["u"]), -hv + 0.03 + c, m(hp["top"])),
               (m(hp["u"]), hv - 0.03 - c, m(hp["top"])), (m(hp["u"]), hv - 0.03, m(hp["top"]) - c), (m(hp["u"]), hv - 0.03, m(ep["deck"]))]
        prims.tube_path(mb, [Vector(p) for p in pts], 0.02, 6 if lod == 0 else 4, st["galv"])
        if lod <= 1:
            prims.cylinder(mb, (m(hp["u"]), -hv + 0.03, m(hp["mid"])), (m(hp["u"]), hv - 0.03, m(hp["mid"])), 0.016, 6 if lod == 0 else 4, st["galv"])
        sf, stt = ep["strut"]["from"], ep["strut"]["to"]
        for s in (-1, 1):
            prims.beam(mb, (m(sf[0]), s * (hv - 0.2), m(sf[1])), (m(stt[0]), s * (hv - 0.2), m(stt[1])), 0.08, 0.08, st["steel"])
    # ladder from the landing plate up to the walkway, with handrails and goosenecks
    ld = d["ladder"]
    foot = Vector((m(ld["foot"][0]), m(ld["foot"][1]), m(ld["foot"][2])))
    head = Vector((m(ld["head"][0]), m(ld["head"][1]), m(ld["head"][2])))
    parts.ladder(mb, foot, head, 2 * m(ld["halfWidth"]), lod, st["galv"], rung_every=m(ld["rungs"]), handrails=False)
    if lod <= 1:
        hr = m(ld["handrail"])
        gn = ld["gooseneck"]
        for s in (-1, 1):
            vv = s * (m(ld["halfWidth"]) + 0.03)
            pts = [foot + Vector((0.05, vv, hr)), head + Vector((0, vv, hr)), Vector((m(gn[0]), vv, m(gn[1]))), Vector((m(gn[0]) + 0.05, vv, deck + 0.05))]
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
    for side, name in ((-1, "l"), (1, "r")):
        parts.sheave_train(body, a, name, m(et["u"]), side * hg, rope, rope_r, m(c["guideSheave"]["dia"]), m(c["guideSheave"]["width"]),
                           train, et["mode"], (side * m(et["attach"][0]), m(et["attach"][1])), lod, st)

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
        "rope_left_out": (m(cbm["uTo"]), -hg, rope), "rope_right_out": (m(cbm["uTo"]), hg, rope),
        "chair_unload": (0.0, -hg, rope),
        "foundation_base": (0.0, 0.0, m(d["footing"]["bottom"])),
    }
    a.dims.update({"bullwheelU": round(u_bw * 1000), "guideSheaveU": d["guideSheaves"]["u"], "columnU": pi["u"],
                   "columnV": pi["v"], "columnTop": pi["top"], "platformEnd": d["entryPlatform"]["uTo"], "entryU": d["portal"]["u"]})
    return a
