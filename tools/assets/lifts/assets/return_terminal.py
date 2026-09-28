"""Sessellift FGQ-4 return terminal (bottom station): an exposed horizontal bullwheel on a hydraulic tension
carriage that runs on rails over a concrete pier, with a catwalk on the carriage and a lifting portal at the
entry toward the line.

Loaded chairs leave on the right rope (v+); the bullwheel turns counter-clockwise seen from above. The
carriage travels 2.44 m (bullwheel centre 1.99-4.43 m behind the pier); the asset shows it mid-travel.
Dimensions: sessellift_fgq4.json "return" and "common" (millimetres).
"""
from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "terminal"
LODS = 4


def m(x):
    return x / 1000.0


def build(spec, lod, stage):
    c, r = spec["common"], spec["return"]
    st = {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("paint_grey", snow=0.5),
        "galv": Style("galvanised", snow=0.5),
        "dark": Style("paint_dark", snow=0.6),
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "rod": Style("machined"),
    }
    a = Asset("sessellift_fgq4_return")
    body = MeshBuilder()
    rope = m(c["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    rope_r = m(c["ropeDiameter"]) / 2
    u_bw = m(r["carriageAt"])   # as drawn: the near end of the carriage's travel (carriageTravel)

    # footing and pier (concrete), steel mast head
    fo, pe, mh = r["footing"], r["pedestal"], r["mastHead"]
    prims.box(body, (-m(fo["u"]) / 2, -m(fo["v"]) / 2, m(fo["bottom"])), (m(fo["u"]) / 2, m(fo["v"]) / 2, m(fo["top"])), st["concrete"])
    prims.box(body, (-m(pe["u"]) / 2, -m(pe["v"]) / 2, m(pe["bottom"])), (m(pe["u"]) / 2, m(pe["v"]) / 2, m(pe["top"])), st["concrete"], skip=("-2",))
    prims.box(body, (-m(mh["u"]) / 2, -m(mh["v"]) / 2, m(mh["bottom"]) + 0.15), (m(mh["u"]) / 2, m(mh["v"]) / 2, m(mh["top"])), st["steel"])
    # flare from the pier width to the head (the haunches seen from the end)
    prims.prism(body, [(-m(pe["v"]) / 2, m(mh["bottom"])), (m(pe["v"]) / 2, m(mh["bottom"])), (m(mh["v"]) / 2, m(mh["bottom"]) + 0.15),
                       (-m(mh["v"]) / 2, m(mh["bottom"]) + 0.15)],
                (-m(mh["u"]) / 2, 0, 0), V, W, U, m(mh["u"]), st["steel"])

    # fixed frame: under-frame box, two rails, end cross member
    uf, rl = r["underFrame"], r["rails"]
    prims.box(body, (m(uf["uFrom"]), -m(uf["v"]) / 2, m(uf["bottom"])), (m(uf["uTo"]), m(uf["v"]) / 2, m(uf["top"])), st["steel"])
    for s in (-1, 1):
        vc = s * m(rl["vCentre"])
        prims.box(body, (m(rl["uFrom"]), vc - m(rl["width"]) / 2, m(rl["bottom"])), (m(rl["uTo"]), vc + m(rl["width"]) / 2, m(rl["top"])), st["steel"])
    prims.box(body, (m(rl["uTo"]) - 0.25, -m(rl["vCentre"]) - m(rl["width"]) / 2, m(rl["bottom"])),
              (m(rl["uTo"]), m(rl["vCentre"]) + m(rl["width"]) / 2, m(rl["top"])), st["steel"])
    prims.box(body, (m(rl["uFrom"]), -m(rl["vCentre"]) - m(rl["width"]) / 2, m(rl["bottom"])),
              (m(rl["uFrom"]) + 0.2, m(rl["vCentre"]) + m(rl["width"]) / 2, m(rl["top"])), st["steel"])

    # tension carriage (static at mid-travel) and hydraulic cylinder
    ca = r["carriage"]
    cu0, cu1 = u_bw + m(ca["uFrom"]), u_bw + m(ca["uTo"])
    prims.box(body, (cu0, -m(ca["v"]) / 2, m(ca["bottom"])), (cu1, m(ca["v"]) / 2, m(ca["top"])), st["steel"])
    for s in (-1, 1):     # roller trucks on the rails
        for uu in (cu0 + 0.25, cu1 - 0.25):
            prims.box(body, (uu - 0.22, s * m(rl["vCentre"]) - 0.17, m(rl["top"])), (uu + 0.22, s * m(rl["vCentre"]) + 0.17, m(rl["top"]) + 0.2), st["steel"])
    prims.box(body, (u_bw - 0.35, -0.35, rope + m(c["bullwheel"]["rimTop"]) + 0.04), (u_bw + 0.35, 0.35, m(ca["top"]) + 0.25), st["steel"])   # bearing housing
    prims.box(body, (u_bw + 0.3, -0.25, m(ca["top"])), (cu0 + 0.4, 0.25, m(ca["top"]) + 0.25), st["steel"])   # housing to carriage
    cy = r["cylinder"]
    prims.cylinder(body, (cu1, 0, m(cy["w"])), (m(cy["uTo"]) - 1.0, 0, m(cy["w"])), m(cy["dia"]) / 2 * 0.55, parts.sides(lod, 12, 8, 6), st["rod"])
    prims.cylinder(body, (m(cy["uTo"]) - 1.2, 0, m(cy["w"])), (m(cy["uTo"]), 0, m(cy["w"])), m(cy["dia"]) / 2, parts.sides(lod, 16, 8, 6), st["steel"])

    # catwalk on the carriage
    cw = r["catwalk"]
    if lod <= 2:
        cwu0, cwu1 = u_bw + m(cw["uFrom"]), u_bw + m(cw["uTo"])
        hv = m(cw["v"]) / 2
        parts.deck(body, (cwu0, -hv, m(cw["top"] - cw["thick"])), (cwu1, hv, m(cw["top"])), lod, st["galv"])
        for s in (-1, 1):
            parts.railing(body, [(cwu0 + 0.03, s * (hv - 0.03), m(cw["top"])), (cwu1 - 0.03, s * (hv - 0.03), m(cw["top"]))], m(cw["railing"]), lod, st["galv"])
        for uu in (cwu0 + 0.3, cwu1 - 0.3):   # posts from carriage to catwalk
            prims.box(body, (uu - 0.06, -0.2, m(ca["top"])), (uu + 0.06, 0.2, m(cw["top"] - cw["thick"])), st["steel"])

    # guide sheaves (rope on top) on arms cantilevered from the rails
    gs, gsp = c["guideSheave"], r["guideSheaves"]
    ug = m(gsp["u"])
    arm = gsp["arm"]
    for s in (-1, 1):
        v0 = s * (m(rl["vCentre"]) + m(rl["width"]) / 2)
        v1 = s * (hg - m(gs["width"]) / 2 - 0.045)
        prims.box(body, (ug - 0.1, min(v0, v1), m(arm["bottom"])), (ug + 0.1, max(v0, v1), m(arm["top"])), st["steel"])
    # lifting frame: curved legs, crossbeam, portal, top platform, hold-down sheaves at each rope
    lf = r["liftingFrame"]
    ul = m(lf["u"])
    cb = lf["crossbeam"]
    prims.box(body, (ul - m(cb["du"]) / 2, -m(cb["v"]) / 2, m(cb["bottom"])), (ul + m(cb["du"]) / 2, m(cb["v"]) / 2, m(cb["top"])), st["steel"])
    legs = lf["legs"]
    if lod <= 2:
        for s in (-1, 1):
            vc = s * m(legs["vCentre"])
            (fu, fw), (tu, tw) = [(m(x), m(y)) for x, y in (legs["from"], legs["to"])]
            n = 4 if lod == 0 else 2
            pts = []
            for k in range(n + 1):   # a swooping channel: flat at the rails, rising steeply to the crossbeam
                t = k / n
                pts.append(Vector((fu + (tu - fu) * (t ** 0.6), vc, fw + (tw - fw) * t * t)))
            for p0, p1 in zip(pts, pts[1:]):
                prims.beam(body, p0, p1, 0.2, 0.25, st["steel"], up=W)
    po = lf["portal"]
    if lod <= 2:
        tw_, th = m(po["topHalfWidth"]), m(po["top"])
        beam = m(po["beam"])
        prims.box(body, (ul - beam / 2, -tw_, th - beam), (ul + beam / 2, tw_, th), st["steel"])
        for s in (-1, 1):
            prims.beam(body, (ul, s * m(po["legFootHalfWidth"]), m(cb["top"])), (ul, s * m(po["legTopHalfWidth"]), th - beam), 0.14, 0.14, st["steel"], up=U)
        lp = lf["platform"]
        hv = m(lp["v"]) / 2
        parts.deck(body, (m(lp["uFrom"]), -hv, m(lp["top"]) - 0.05), (m(lp["uTo"]), hv, m(lp["top"])), lod, st["galv"])
        parts.railing(body, [(m(lp["uFrom"]), hv - 0.03, m(lp["top"])), (m(lp["uTo"]) - 0.03, hv - 0.03, m(lp["top"])),
                             (m(lp["uTo"]) - 0.03, -hv + 0.03, m(lp["top"])), (m(lp["uFrom"]), -hv + 0.03, m(lp["top"]))],
                      m(lp["railing"]), lod, st["galv"])

    sheaves = [("l1", ug, -1, gs, "support"), ("r1", ug, 1, gs, "support")]
    hs = lf["sheave"]
    for k, du in enumerate((-m(hs["pitch"]) / 2, m(hs["pitch"]) / 2)):
        sheaves += [(f"l{k + 2}", ul + du, -1, hs, "hold"), (f"r{k + 2}", ul + du, 1, hs, "hold")]
    for name, us, side, shs, kind in sheaves:
        rr = m(shs["dia"]) / 2
        wz = rope - rope_r - rr if kind == "support" else rope + rope_r + rr
        centre = Vector((us, side * hg, wz))
        target = MeshBuilder() if lod <= 1 else body
        if lod <= 2:
            parts.sheave(target, centre, V, m(shs["dia"]), m(shs["width"]), lod, st["red"], st["rubber"])
        if lod <= 1:
            a.parts[f"sheave_{name}"] = target
            a.pivots[f"sheave_{name}"] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}
        if lod <= 2:   # inboard brackets: from the guide arm below, or down from the lifting-frame crossbeam
            parts.sheave_bracket(body, centre, m(shs["width"]), m(arm["top"]) if kind == "support" else m(cb["bottom"]), lod, st["steel"])

    # bullwheel (exposed, takes snow on its top plate)
    bw_spec = {k: m(v) if k != "spokes" else v for k, v in c["bullwheel"].items()}
    bw_spec.update({"hubBottom": m(r["bullwheel"]["hubBottom"]), "hubTop": m(r["bullwheel"]["hubTop"]), "hubDia": m(r["bullwheel"]["hubDia"])})
    centre = Vector((u_bw, 0.0, rope))
    if lod <= 2:
        bw = MeshBuilder()
        parts.bullwheel(bw, centre, bw_spec, lod, st["dark"])
        a.parts["bullwheel"] = bw
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        parts.bullwheel(body, centre, bw_spec, lod, st["dark"])

    a.body = body
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (u_bw, -hg, rope), "rope_right_bw": (u_bw, hg, rope),
        "rope_left_out": (ul, -hg, rope), "rope_right_out": (ul, hg, rope),
        "chair_load": (u_bw + 1.0, hg, rope),
        "foundation_base": (0.0, 0.0, m(fo["bottom"])),
    }
    a.dims.update({"bullwheelU": round(u_bw * 1000), "guideSheaveU": gsp["u"], "liftingFrameU": lf["u"], "railsEnd": rl["uTo"],
                   "pedestalTop": pe["top"], "portalTop": po["top"]})
    return a
