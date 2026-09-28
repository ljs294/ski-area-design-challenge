"""Sessellift FGQ-4 drive terminal (top station): a barrel hood over the drive and a horizontal bullwheel,
cantilevered 3.23 m behind a concrete pier, with an entry platform, ladder and lifting portal toward the line.

Loaded chairs arrive on the left rope (v-) and unload over the pier; the bullwheel turns counter-clockwise
seen from above. Dimensions: sessellift_fgq4.json "drive" and "common" (millimetres).
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
        "glass": Style("glass", slot=GLASS, smooth=True) if lod <= 1 else Style("glass", smooth=True),
        "interior": Style("interior"),
    }


def hood_profile(spec, lod):
    """Full closed hood outline in the (v, w) plane, metres, from the left base over the crown to the right."""
    h = spec["drive"]["hood"]
    half = [(m(v), m(w)) for v, w in h["profile"]]
    # glass edges become profile vertices so window faces start and stop exactly there
    win = h["windows"]
    for edge in (m(win["low"]), m(win["high"])):
        for i in range(len(half) - 1):
            (v0, w0), (v1, w1) = half[i], half[i + 1]
            if w0 < edge < w1:
                t = (edge - w0) / (w1 - w0)
                half.insert(i + 1, (v0 + (v1 - v0) * t, edge))
                break
    # subdivide the long arc segments near the camera for a rounder barrel
    if lod == 0:
        fine = [half[0]]
        for (v0, w0), (v1, w1) in zip(half, half[1:]):
            n = max(1, round(((v1 - v0) ** 2 + (w1 - w0) ** 2) ** 0.5 / 0.3)) if w0 >= m(h["whiteBand"]["high"]) - 1e-6 else 1
            for k in range(1, n + 1):
                t = k / n
                fine.append((v0 + (v1 - v0) * t, w0 + (w1 - w0) * t))
        half = fine
    elif lod >= 2:
        keep = {0, len(half) - 1}
        step = 2 if lod == 2 else 3
        half = [p for i, p in enumerate(half) if i in keep or i % step == 0]
    left = [(-v, w) for v, w in half]
    right = [(v, w) for v, w in reversed(half[:-1])]
    return left + right


def build_hood(spec, lod, st):
    """The barrel hood: profile extruded along u between raked end walls, glazed bays along both sides."""
    h = spec["drive"]["hood"]
    mb = MeshBuilder()
    prof = hood_profile(spec, lod)
    base, crown = m(h["base"]), max(w for _, w in prof)

    def end_u(which, w):
        e = h[which]
        t = (w - base) / (crown - base)
        return m(e["bottom"]) + (m(e["top"]) - m(e["bottom"])) * t

    win = h["windows"]
    cols = []   # interior u breakpoints from the far end toward the line, and whether each span is a window bay
    for j in reversed(range(win["bays"])):
        u0 = m(win["firstU"]) - j * m(win["bay"] + win["rib"])
        cols.append((u0 - m(win["bay"]), u0))
    breaks = []
    for a, b in cols:
        breaks += [a, b]
    bay_spans = set(range(1, 2 * len(cols), 2)) if lod <= 2 else set()   # span k between breaks[k-1], breaks[k]
    low, high = m(win["low"]), m(win["high"])
    band_lo, band_hi = m(h["whiteBand"]["low"]), m(h["whiteBand"]["high"])

    # vertex grid: rows follow the profile, columns: far end, breaks..., line end
    grid = []
    for v, w in prof:
        row = [mb.vert((end_u("farEnd", w), v, w))]
        row += [mb.vert((u, v, w)) for u in breaks]
        row.append(mb.vert((end_u("lineEnd", w), v, w)))
        grid.append(row)
    n_rows = len(prof)
    for i in range(n_rows):
        i1 = (i + 1) % n_rows
        (v0, w0), (v1, w1) = prof[i], prof[i1]
        wmid, vmid = (w0 + w1) / 2, (v0 + v1) / 2
        bottom = i1 == 0
        side_normal = Vector((0, v1 - v0, w1 - w0)).cross(Vector((1, 0, 0)))   # perpendicular to the segment
        out = (0.0, vmid, wmid - (base + 1.2))
        for k in range(len(breaks) + 1):
            if bottom:
                style = st["trim"]
            elif band_lo - 1e-6 <= min(w0, w1) and max(w0, w1) <= band_hi + 1e-6 and abs(v1 - v0) < 1e-3:
                style = st["band"]
            elif min(w0, w1) < band_lo - 1e-6:
                style = st["trim"]
            elif k in bay_spans and low - 1e-6 <= min(w0, w1) and max(w0, w1) <= high + 1e-6:
                style = st["glass"]
            else:
                style = st["hood"].but(smooth=True)
            if bottom:
                out_k = (0, 0, -1)
            else:
                out_k = out
            mb.face([grid[i][k], grid[i][k + 1], grid[i1][k + 1], grid[i1][k]], style, out_k)
    # end walls (planar: u is linear in w along each raked end)
    for which, col, direction in (("farEnd", 0, -1), ("lineEnd", -1, 1)):
        ring = [mb.vert((end_u(which, w), v, w)) for v, w in prof]
        mb.face(ring, st["hood"].but(smooth=False), (direction, 0, 0))
    return mb


def build(spec, lod, stage):
    c, d = spec["common"], spec["drive"]
    st = styles(lod)
    a = Asset("sessellift_fgq4_drive")
    body = MeshBuilder()
    rope = m(c["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    rope_r = m(c["ropeDiameter"]) / 2
    u_bw = m(d["bullwheel"]["u"])

    # foundation and pier
    f = d["foundation"]
    prims.box(body, (-m(f["u"]) / 2, -m(f["v"]) / 2, m(f["bottom"])), (m(f["u"]) / 2, m(f["v"]) / 2, m(f["top"])), st["concrete"])
    col = d["column"]
    prims.box(body, (-m(col["u"]) / 2, -m(col["v"]) / 2, m(f["top"])), (m(col["u"]) / 2, m(col["v"]) / 2, m(col["top"])), st["concrete"],
              skip=("-2",))
    # steel head on the pier and the drive base frame under the hood
    hb = d["headBox"]
    prims.box(body, (-m(hb["u"]) / 2, -m(hb["v"]) / 2, m(hb["bottom"])), (m(hb["u"]) / 2, m(hb["v"]) / 2, m(hb["top"])), st["steel"])
    bf = d["baseFrame"]
    prims.box(body, (m(bf["uTo"]), -m(bf["v"]) / 2, m(bf["bottom"])), (m(bf["uFrom"]), m(bf["v"]) / 2, m(bf["top"])), st["steel"])

    # drive machinery behind the glass (silhouettes only; LOD0-1)
    if lod <= 1:
        g = d["gearbox"]
        prims.box(body, (u_bw - m(g["u"]) / 2, -m(g["v"]) / 2, m(g["bottom"])), (u_bw + m(g["u"]) / 2, m(g["v"]) / 2, m(g["top"])), st["interior"])
        mo = d["motor"]
        prims.cylinder(body, (m(mo["from"]), 0, m(mo["w"])), (m(mo["to"]), 0, m(mo["w"])), m(mo["dia"]) / 2, parts.sides(lod, 12, 8), st["interior"])
        cb = d["cabinet"]
        prims.box(body, (m(cb["uFrom"]), -m(cb["v"]) / 2, m(cb["bottom"])), (m(cb["uTo"]), m(cb["v"]) / 2, m(cb["top"])), st["interior"])

    # hood
    hood = build_hood(spec, lod, st)
    lo, hi = hood.bounds_lift()
    a.dims.update({"hoodTopLength": round((hi[0] - lo[0]) * 1000), "hoodWidth": round((hi[1] - lo[1]) * 1000),
                   "hoodCrown": round(hi[2] * 1000), "hoodLineEndTop": round(hi[0] * 1000), "hoodFarEndTop": round(lo[0] * 1000)})
    body.merge(hood)

    # entry toward the line: beams from the head to the crossbeam, platform, ladder, portal
    e = d["entry"]
    ue = m(e["u"])
    cbm = e["crossbeam"]
    pl = d["platform"]
    for s in (-1, 1):
        prims.box(body, (m(hb["u"]) / 2 - 0.05, s * 0.45 - 0.15, m(cbm["top"])), (ue + m(cbm["du"]) / 2, s * 0.45 + 0.15, m(pl["top"] - pl["thick"])), st["steel"])
    prims.box(body, (ue - m(cbm["du"]) / 2, -m(cbm["v"]) / 2, m(cbm["bottom"])), (ue + m(cbm["du"]) / 2, m(cbm["v"]) / 2, m(cbm["top"])), st["steel"])
    # platform deck (with the ladder opening) and railings
    top = m(pl["top"])
    t = m(pl["thick"])
    op = pl["ladderOpening"]
    u0, u1, hv = m(pl["uFrom"]), m(pl["uTo"]), m(pl["v"]) / 2
    ou0, ou1, ov0, ov1 = m(op["uFrom"]), m(op["uTo"]), m(op["vFrom"]), m(op["vTo"])
    for lo_, hi_ in (((u0, -hv, top - t), (ou0, hv, top)), ((ou1, -hv, top - t), (u1, hv, top)),
                     ((ou0, -hv, top - t), (ou1, ov0, top)), ((ou0, ov1, top - t), (ou1, hv, top))):
        parts.deck(body, lo_, hi_, lod, st["galv"])
    rail_h = m(pl["railing"])
    parts.railing(body, [(u0 + 0.05, hv - 0.03, top), (u1 - 0.03, hv - 0.03, top), (u1 - 0.03, -hv + 0.03, top), (u0 + 0.05, -hv + 0.03, top)],
                  rail_h, lod, st["galv"])
    lad = d["ladder"]
    ltop = Vector((m(lad["top"][0]), m(lad["top"][1]), m(lad["top"][2])))
    lbot = Vector((m(lad["bottom"][0]), m(lad["bottom"][1]), m(lad["bottom"][2])))
    parts.ladder(body, lbot, ltop, m(lad["width"]), lod, st["galv"])
    # lifting portal over the entry
    po = e["portal"]
    if lod <= 2:
        tw, th = m(po["topHalfWidth"]), m(po["top"])
        beam = m(po["beam"])
        prims.box(body, (ue - beam / 2, -tw - 0.1, th - beam), (ue + beam / 2, tw + 0.1, th), st["steel"])
        for s in (-1, 1):
            prims.beam(body, (ue, s * m(po["legFootHalfWidth"]), m(cbm["top"])), (ue, s * tw, th - beam), 0.16, 0.16, st["steel"], up=U)

    # rope sheaves: guide sheaves between pier and bullwheel, entry sheaves at the crossbeam (rope rides on top)
    gs = c["guideSheave"]
    es = e["sheave"]
    sheaves = [("l1", m(d["guideSheaves"]["u"]), -1, gs), ("r1", m(d["guideSheaves"]["u"]), 1, gs),
               ("l2", ue, -1, es), ("r2", ue, 1, es)]
    for name, us, side, sh in sheaves:
        r = m(sh["dia"]) / 2
        centre = Vector((us, side * hg, rope - rope_r - r))
        target = MeshBuilder() if lod <= 1 else body
        if lod <= 2:
            parts.sheave(target, centre, V, m(sh["dia"]), m(sh["width"]), lod, st["red"], st["rubber"])
        if lod <= 1:
            a.parts[f"sheave_{name}"] = target
            a.pivots[f"sheave_{name}"] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}
        # inboard bracket from the drive frame (guide sheaves) or the entry crossbeam, so grips pass outboard
        if lod <= 2:
            parts.sheave_bracket(body, centre, m(sh["width"]), m(cbm["bottom"]) if us == ue else m(bf["bottom"]), lod, st["steel"])

    # bullwheel (pivoted on its vertical axle at rope elevation)
    bw_spec = {k: m(v) if isinstance(v, (int, float)) and k not in ("spokes",) else v for k, v in c["bullwheel"].items()}
    bw_spec.update({"hubBottom": m(d["bullwheel"]["hubBottom"]), "hubTop": m(d["bullwheel"]["hubTop"])})
    bw_centre = Vector((u_bw, 0.0, rope))
    if lod <= 2:
        bw = MeshBuilder()
        parts.bullwheel(bw, bw_centre, bw_spec, lod, st["dark"])
        a.parts["bullwheel"] = bw
        a.pivots["bullwheel"] = {"pos": tuple(bw_centre), "axis": (0.0, 0.0, 1.0)}
    else:
        parts.bullwheel(body, bw_centre, bw_spec, lod, st["dark"])

    a.body = body
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (u_bw, -hg, rope), "rope_right_bw": (u_bw, hg, rope),
        "rope_left_out": (ue, -hg, rope), "rope_right_out": (ue, hg, rope),
        "chair_unload": (0.0, -hg, rope),
        "foundation_base": (0.0, 0.0, m(f["bottom"])),
    }
    a.dims.update({"bullwheelU": round(u_bw * 1000), "guideSheaveU": d["guideSheaves"]["u"], "columnU": col["u"],
                   "columnV": col["v"], "columnTop": col["top"], "platformEnd": pl["uTo"], "entryU": e["u"]})
    return a
