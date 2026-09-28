"""Sessellift FGQ-4 return terminal (bottom station), built along the drawn outlines (sessellift_fgq4.json "return",
checked by the Gate 1 review):

  a concrete pier and footing under a steel mast head with concave flares and rail beds fore and aft, carrying
  two frame rails; the lower frame, the hydraulic cylinder and the frame-end block with its eye under the
  rails; the tension carriage between the rails (a head box under the bullwheel with its bearing plinth and
  cylinder lugs, side and centre members forward to a front plate) with a railed catwalk over the wheel; the
  guide sheaves on a cross member, hung from overhead frames on posts; a rope guard on the left and the
  loading-side chair guide on the right (two rails flared at both ends, on C-frame hangers); and the lifting
  frame at the entry (the integrated first tower): two curved plate legs from the rails to the crossbeam,
  adjusting rods, a portal with lug plates, a small platform railed across its outer end, and a four-sheave
  hold-down train on each rope (from photos: the drawing leaves the trains to the line drawings).

Loaded chairs leave on the right rope (v+); the bullwheel turns counter-clockwise seen from above. The carriage
is shown as drawn, at the near end of its 2.44 m travel (bullwheel centre 1.99 m behind the pier).
Dimensions: sessellift_fgq4.json "return" and "common" (millimetres); carriage and catwalk positions are
measured from the bullwheel axis, bullwheel heights from the rope.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "terminal"
LODS = 4


def m(x):
    return x / 1000.0


def styles():
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": Style("paint_grey", snow=0.5),
        "galv": Style("galvanised", snow=0.5),
        "grating": Style("grating", snow=0.8),
        "dark": Style("paint_dark", snow=0.6),
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "rod": Style("machined"),
    }


def thin(pts, lod):
    """Every point at LOD0, every other one (ends kept) at LOD1, the ends and the middle further out."""
    if lod == 0 or len(pts) <= 3:
        return pts
    if lod == 1:
        out = pts[::2]
        return out if out[-1] == pts[-1] else out + [pts[-1]]
    return [pts[0], pts[len(pts) // 2], pts[-1]]


def rect_path(mb, pts, half_w, lo, hi, style):
    """A rectangular section (half_w across, lo to hi in w) swept along a horizontal polyline of (u, v) points,
    mitred at the bends: the chair-guide rails."""
    pts = [Vector((u, v, 0.0)) for u, v in pts]
    n = len(pts)
    rings = []
    for i, p in enumerate(pts):
        t_in = (p - pts[i - 1]).normalized() if i > 0 else None
        t_out = (pts[i + 1] - p).normalized() if i < n - 1 else None
        t = ((t_in or t_out) + (t_out or t_in)).normalized()
        scale = 1.0 / max(0.5, t_in.dot(t)) if t_in is not None and t_out is not None else 1.0
        off = Vector((-t.y, t.x, 0.0)) * (half_w * scale)
        rings.append([mb.vert(tuple(p + off + W * lo)), mb.vert(tuple(p - off + W * lo)),
                      mb.vert(tuple(p - off + W * hi)), mb.vert(tuple(p + off + W * hi))])
    for i in range(n - 1):
        a, b = rings[i], rings[i + 1]
        t = (pts[i + 1] - pts[i]).normalized()
        side = Vector((-t.y, t.x, 0.0))
        for k, out in ((0, -W), (1, -side), (2, W), (3, side)):
            k1 = (k + 1) % 4
            mb.face([a[k], a[k1], b[k1], b[k]], style, tuple(out))
    mb.face(list(rings[0]), style, tuple(pts[0] - pts[1]))
    mb.face(list(rings[-1]), style, tuple(pts[-1] - pts[-2]))


def hoop(mb, ends, top, base, corner, lod, style):
    """A railing hoop between two end posts (lift points, same height): up each post from `base`, rounded
    corners, the top rail between them. LOD2 draws it as boxes."""
    a, b = Vector(ends[0]), Vector(ends[1])
    along = (b - a).normalized()
    if lod >= 2:
        lo_c = Vector((min(a.x, b.x), min(a.y, b.y), top)) - Vector((0.025, 0.025, 0.025))
        hi_c = Vector((max(a.x, b.x), max(a.y, b.y), top)) + Vector((0.025, 0.025, 0.025))
        prims.box(mb, tuple(lo_c), tuple(hi_c), style)
        for p in (a, b):
            prims.box(mb, (p.x - 0.025, p.y - 0.025, base), (p.x + 0.025, p.y + 0.025, top), style)
        return
    n_arc = 3 if lod == 0 else 1
    a0, b0 = Vector((a.x, a.y, 0.0)), Vector((b.x, b.y, 0.0))
    path = [a0 + W * base]
    for k in range(n_arc + 1):
        t = math.pi / 2 * k / n_arc
        path.append(a0 + along * (corner - corner * math.cos(t)) + W * (top - corner + corner * math.sin(t)))
    for k in range(n_arc + 1):
        t = math.pi / 2 * k / n_arc
        path.append(b0 - along * (corner - corner * math.sin(t)) + W * (top - corner + corner * math.cos(t)))
    path.append(b0 + W * base)
    prims.tube_path(mb, path, 0.024, 6 if lod == 0 else 4, style, caps=False)


def base(mb, r, lod, st):
    """Footing and pier (concrete); the steel mast head with its concave flares, skirt and rail beds; the two
    frame rails with the brackets under them; the lower frame, the hydraulic cylinder and the frame-end block."""
    fo, pe, hd, rb, rl = r["footing"], r["pedestal"], r["head"], r["railBeds"], r["rails"]
    prims.box(mb, (-m(fo["u"]) / 2, -m(fo["v"]) / 2, m(fo["bottom"])), (m(fo["u"]) / 2, m(fo["v"]) / 2, m(fo["top"])), st["concrete"])
    prims.box(mb, (-m(pe["u"]) / 2, -m(pe["v"]) / 2, m(pe["bottom"])), (m(pe["u"]) / 2, m(pe["v"]) / 2, m(pe["top"])),
              st["concrete"], skip=("-2",))
    hu, hv, neck = m(hd["u"]) / 2, m(hd["v"]) / 2, m(hd["neck"]) / 2
    h_lo, h_hi = m(hd["bottom"]), m(hd["top"])
    if lod <= 2:
        prims.box(mb, (-hu, -hv, h_lo), (hu, hv, h_hi), st["steel"])
        for su in (-1, 1):   # rail beds fore and aft of the head
            for sv in (-1, 1):
                u0, u1 = sorted((su * m(rb["uIn"]), su * m(rb["uOut"])))
                v0, v1 = sorted((sv * m(rb["vIn"]), sv * m(rb["vOut"])))
                prims.box(mb, (u0, v0, m(rb["bottom"])), (u1, v1, m(rb["top"])), st["steel"], skip=("-0",) if su > 0 else ("+0",))
        fl = [(m(v), m(w)) for v, w in hd["flare"]]
        if lod == 2:   # the concave flare as one chamfer
            fl = [fl[0], fl[-2], fl[-1]]
        prims.prism(mb, fl + [(-v, w) for v, w in reversed(fl)], (-hu, 0, 0), V, W, U, 2 * hu, st["steel"])
        prims.box(mb, (-hu, -neck, m(hd["skirt"])), (hu, neck, m(hd["base"])), st["steel"], skip=("+2",))
    else:   # head and rail beds as one block over a plain neck
        prims.box(mb, (-m(rb["uOut"]), -hv, h_lo), (m(rb["uOut"]), hv, h_hi), st["steel"])
        prims.box(mb, (-hu, -neck, m(hd["skirt"])), (hu, neck, h_lo), st["steel"], skip=("+2",))

    lo, hi = m(rl["bottom"]), m(rl["top"])
    if lod == 3:   # both rails as one slab
        prims.box(mb, (m(rl["uFrom"]), -m(rl["vOut"]), lo), (m(rl["uTo"]), m(rl["vOut"]), hi), st["steel"])
    for s in ((-1, 1) if lod <= 2 else ()):   # frame rails, rounded at the entry end in plan
        v0, v1 = sorted((s * m(rl["vIn"]), s * m(rl["vOut"])))
        if lod == 0:
            cap = (v1 - v0) / 2
            u_cap = m(rl["capTo"]) - cap
            prims.box(mb, (m(rl["uFrom"]), v0, lo), (u_cap, v1, hi), st["steel"], skip=("+0",))
            arc = [(u_cap + cap * math.cos(t), (v0 + v1) / 2 + cap * math.sin(t)) for t in (math.pi * (k / 6 - 0.5) for k in range(7))]
            prims.prism(mb, arc, (0, 0, lo), U, V, W, hi - lo, st["steel"])
        else:
            prims.box(mb, (m(rl["uFrom"]), v0, lo), (m(rl["uTo"]), v1, hi), st["steel"])

    br = r["bracket"]
    if lod <= 1:   # plates under the rails ahead of the head, sloping up to the rails
        prof = [(m(br["uFrom"]), m(br["bottom"])), (m(br["uTo"]), m(br["bottom"])), (m(br["slopeTo"]), lo), (m(br["uFrom"]), lo)]
        for s in (-1, 1):
            vc = s * (m(rl["vIn"]) + m(rl["vOut"])) / 2
            prims.prism(mb, prof, (0, vc - 0.01, 0), U, W, V, 0.02, st["steel"])
    if lod <= 2:
        lw = r["lowerFrame"]   # from inside the head forward to the lifting-frame legs
        for s in (-1, 1):
            v0, v1 = sorted((s * m(lw["vIn"]), s * m(lw["vOut"])))
            prims.box(mb, (m(lw["uFrom"]), v0, m(lw["bottom"])), (m(lw["uTo"]), v1, m(lw["top"])), st["steel"])
        cy = r["cylinder"]
        wc = m(cy["w"])
        prims.cylinder(mb, (m(cy["from"]), 0, wc), (m(cy["to"]), 0, wc), m(cy["dia"]) / 2, parts.sides(lod, 16, 10, 6), st["steel"])
        fe = r["frameEnd"]   # side members on the rail lines, from the legs' lower edge to the sloped end, tied across there
        for s in (-1, 1):
            v0, v1 = sorted((s * m(fe["vIn"]), s * m(fe["vOut"])))
            prims.prism(mb, [(m(u), m(w)) for u, w in fe["u"]], (0, v0, 0), U, W, V, v1 - v0, st["steel"])
        prims.box(mb, (m(fe["tie"][0]), -m(fe["vIn"]), m(fe["u"][0][1])), (m(fe["tie"][1]), m(fe["vIn"]), m(fe["u"][2][1])), st["steel"])
        if lod <= 1:   # rod end, eye lugs under the frame-end block and the pin
            eu, ew = m(cy["eye"][0]), m(cy["eye"][1])
            er = m(cy["eyeDia"]) / 2
            prims.cylinder(mb, (m(cy["to"]), 0, wc), (eu, 0, ew), er * 1.4, parts.sides(lod, 10, 6), st["rod"])
            for s in (-1, 1):
                prims.box(mb, (eu - 0.06, s * 0.075 - 0.01, ew - er - 0.03), (eu + 0.06, s * 0.075 + 0.01, m(fe["u"][0][1])), st["steel"])
            if lod == 0:
                prims.cylinder(mb, (eu, -0.1, ew), (eu, 0.1, ew), er / 2, 8, st["rod"])


def carriage(mb, r, u_bw, rope, lod, st):
    """The tension carriage as drawn: head box under the bullwheel with its bearing plinth and the cylinder lugs,
    side and centre members forward between the rails to the front plate, and the catwalk posts."""
    ca = r["carriage"]

    def cu(x):
        return u_bw + m(x)

    hb = ca["head"]
    c0, c1, cw = hb["chamfer"]
    top, bot = m(hb["top"]), m(hb["bottom"])
    if lod <= 2:   # side profile with the chamfered lower front corner, across v
        prof = [(cu(hb["uFrom"]), bot), (cu(c0), bot), (cu(c1), m(cw)), (cu(hb["uTo"]), m(cw)), (cu(hb["uTo"]), top), (cu(hb["uFrom"]), top)]
        prims.prism(mb, prof, (0, -m(hb["v"]) / 2, 0), U, W, V, m(hb["v"]), st["steel"])
    else:
        prims.box(mb, (cu(hb["uFrom"]), -m(hb["v"]) / 2, bot), (cu(hb["uTo"]), m(hb["v"]) / 2, top), st["steel"])
    pl = ca["plinth"]
    prims.box(mb, (cu(pl["uFrom"]), -m(pl["v"]) / 2, m(pl["bottom"])), (cu(pl["uTo"]), m(pl["v"]) / 2, m(pl["top"])), st["steel"])
    if lod <= 1:   # bearing from the head box through the plinth to the hub; the cylinder lugs
        prims.cylinder(mb, (u_bw, 0, top), (u_bw, 0, rope + m(r["bullwheel"]["hubBottom"])), 0.3, parts.sides(lod, 16, 10), st["steel"])
        lg = ca["lugs"]
        for s in (-1, 1):
            vc = s * m(lg["v"])
            prims.box(mb, (cu(lg["uFrom"]), vc - 0.015, m(lg["bottom"])), (cu(lg["uTo"]), vc + 0.015, m(lg["top"])), st["steel"])
    sd, ce, fr = ca["sides"], ca["centre"], ca["front"]
    lo, hi = m(sd["bottom"]), m(sd["top"])
    if lod <= 2:
        for s in (-1, 1):
            vc = s * m(sd["v"])
            prims.box(mb, (cu(sd["uFrom"]), vc - m(sd["width"]) / 2, lo), (cu(sd["uTo"]), vc + m(sd["width"]) / 2, hi), st["steel"])
        prims.box(mb, (cu(fr["uFrom"]), -m(fr["v"]) / 2, lo), (cu(fr["uTo"]), m(fr["v"]) / 2, hi), st["steel"])
        prims.box(mb, (cu(hb["uTo"]), -m(ce["v"]) / 2, lo), (cu(ce["uTo"]), m(ce["v"]) / 2, hi), st["steel"])
        po = ca["posts"]
        fascia = m(r["catwalk"]["fascia"])
        for uc in po["u"]:
            u0, u1 = cu(uc) - m(po["width"]) / 2, cu(uc) + m(po["width"]) / 2
            if lod <= 1:
                for s in (-1, 1):
                    v0, v1 = sorted((s * 0.12, s * 0.2))
                    prims.box(mb, (u0, v0, hi), (u1, v1, fascia), st["steel"])
            else:
                prims.box(mb, (u0, -0.2, hi), (u1, 0.2, fascia), st["steel"])


def catwalk(mb, cw, u_bw, lod, st):
    """The catwalk over the bullwheel, carried by the carriage: grating deck in edge channels, railed both sides
    (end posts bent into the top rail, a middle post, a mid rail)."""
    u0, u1 = u_bw + m(cw["uFrom"]), u_bw + m(cw["uTo"])
    hv = m(cw["v"]) / 2
    top, fascia, t = m(cw["top"]), m(cw["fascia"]), m(cw["thick"])
    edge = 0.01
    if lod == 3:
        prims.box(mb, (u0, -hv - edge, fascia), (u1, hv + edge, top), st["galv"])
        return
    prims.box(mb, (u0, -hv, top - t), (u1, hv, top), st["grating"])
    for s in (-1, 1):
        v0, v1 = sorted((s * hv, s * (hv + edge)))
        prims.box(mb, (u0, v0, fascia), (u1, v1, top), st["galv"])
    if lod <= 1:
        for uu in (u0, u1 - edge):
            prims.box(mb, (uu, -hv, fascia), (uu + edge, hv, top - t), st["galv"])
    rl = cw["rail"]
    rv = m(rl["v"]) / 2
    ptop, pmid = m(rl["top"]), m(rl["mid"])
    posts = [u_bw + m(x) for x in rl["posts"]]
    foot = top - 0.1   # the posts clamp to the outside of the edge channel
    for s in (-1, 1):
        vv = s * rv
        hoop(mb, ((posts[0], vv, 0.0), (posts[-1], vv, 0.0)), ptop, foot, m(rl["bend"]), lod, st["galv"])
        if lod <= 1:
            n = 6 if lod == 0 else 4
            for um in posts[1:-1]:
                prims.cylinder(mb, (um, vv, foot), (um, vv, ptop), 0.024, n, st["galv"], caps=(False, False))
            prims.cylinder(mb, (posts[0], vv, pmid), (posts[-1], vv, pmid), 0.02, n, st["galv"], caps=(False, False))


def guides(mb, a, c, r, lod, st):
    """The guide sheaves (rope on top) on their plates and cross member, hung from overhead frames on posts; the
    rope guard on the left; the loading-side chair guide on the right (rails on C-frame hangers)."""
    rope, hg = m(c["ropeElevation"]), m(c["lineGauge"]) / 2
    gs, gp = c["guideSheave"], r["guideSheaves"]
    ug = m(gp["u"])
    wz = rope - m(c["ropeDiameter"]) / 2 - m(gs["dia"]) / 2
    cr, hn, ov = gp["cross"], gp["hangers"], r["overhead"]
    o_lo, o_hi = m(ov["bottom"]), m(ov["top"])
    o_mid = (o_lo + o_hi) / 2
    ou0, ou1 = m(ov["u"]) - m(ov["width"]) / 2, m(ov["u"]) + m(ov["width"]) / 2
    prims.box(mb, (m(cr["uFrom"]), -m(cr["v"]) / 2, m(cr["bottom"])), (m(cr["uTo"]), m(cr["v"]) / 2, m(cr["top"])), st["steel"])
    if lod <= 2:
        for v0, v1 in ov["spans"]:
            prims.box(mb, (ou0, m(v0), o_lo), (ou1, m(v1), o_hi), st["steel"])
        for v0, v1 in ov["posts"]:
            prims.box(mb, (ou0, m(v0), m(cr["top"])), (ou1, m(v1), o_lo), st["steel"])
    if lod <= 1:
        for p0, p1 in ov["diagonals"]:
            prims.beam(mb, (m(p0[0]), m(p0[1]), o_mid), (m(p1[0]), m(p1[1]), o_mid), 0.08, o_hi - o_lo, st["steel"])

    for side, name, plate_lo in ((-1, "l1", hn["bottom"][0]), (1, "r1", hn["bottom"][1])):
        centre = Vector((ug, side * hg, wz))
        pv = side * m(hn["v"])
        if lod <= 2:   # plate inboard of the sheave, cross member to overhead frame
            prims.box(mb, (ug - 0.085, pv - 0.01, m(plate_lo)), (ug + 0.085, pv + 0.01, o_lo), st["steel"])
        if lod <= 1:   # axle boss
            prims.cylinder(mb, (ug, pv + side * 0.01, wz), (ug, side * (hg - m(gs["width"]) / 2), wz), 0.045, parts.sides(lod, 10, 6), st["rod"])
        target = MeshBuilder() if lod <= 1 else mb
        if lod <= 2:
            parts.sheave(target, centre, V, m(gs["dia"]), m(gs["width"]), lod, st["red"], st["rubber"])
        if lod <= 1:
            a.parts[f"sheave_{name}"] = target
            a.pivots[f"sheave_{name}"] = {"pos": tuple(centre), "axis": (0.0, 1.0, 0.0)}

    rg = r["ropeGuard"]
    if lod <= 2:   # outboard of the left rope, just above it
        prims.box(mb, (m(rg["uFrom"]), m(rg["v"][0]), m(rg["bottom"])), (m(rg["uTo"]), m(rg["v"][1]), m(rg["top"])), st["steel"])
    if lod <= 1:   # hangers up to the overhead level
        hv_ = m(rg["hangerV"])
        for uh in rg["hangersU"]:
            prims.box(mb, (m(uh) - 0.04, hv_ - 0.04, m(rg["top"])), (m(uh) + 0.04, hv_ + 0.04, o_hi), st["steel"])

    cg = r["chairGuide"]
    lo, hi = m(cg["bottom"]), m(cg["top"])
    if lod <= 2:
        for rail in (cg["outer"], cg["inner"]):
            rect_path(mb, thin([(m(u), m(v)) for u, v in rail["path"]], lod), m(rail["width"]) / 2, lo, hi, st["galv"])
    hk = cg["hangers"]
    b_lo, b_hi = m(hk["bracketW"][0]), m(hk["bracketW"][1])
    for u0, u1 in hk["u"]:   # C-frames: top beam, outer leg and bracket, inner drop and bracket (hangers pass between)
        if lod <= 2:
            prims.box(mb, (m(u0), m(hk["vFrom"]), o_lo), (m(u1), m(hk["vTo"]), o_hi), st["steel"])
        if lod <= 1:
            for vs, br_v in ((hk["legV"], hk["outerBracketV"]), (hk["dropV"], hk["innerBracketV"])):
                prims.box(mb, (m(u0), m(vs[0]), b_hi), (m(u1), m(vs[1]), o_lo), st["steel"])
                prims.box(mb, (m(u0), m(br_v[0]), b_lo), (m(u1), m(br_v[1]), b_hi), st["steel"])
    bm = cg["beam"]
    if lod <= 1:
        prims.box(mb, (m(bm["uFrom"]), m(bm["v"][0]), o_lo), (m(bm["uTo"]), m(bm["v"][1]), o_hi), st["steel"])


def lifting_frame(mb, lf, lod, st):
    """The lifting frame at the entry: two curved plate legs from the rails to the crossbeam, adjusting rods, the
    crossbeam with its shallower end stubs and end plates, the portal (legs, top beam, lug plates) and the
    platform railed across its outer end, with knee braces."""
    cb = lf["crossbeam"]
    cu0, cu1 = m(cb["uFrom"]), m(cb["uTo"])
    half, stub = m(cb["v"]) / 2, m(cb["stubTo"]) / 2
    prims.box(mb, (cu0, -half, m(cb["bottom"])), (cu1, half, m(cb["top"])), st["steel"])
    if lod <= 2:
        for s in (-1, 1):
            v0, v1 = sorted((s * half, s * stub))
            prims.box(mb, (cu0, v0, m(cb["stubBottom"])), (cu1, v1, m(cb["top"])), st["steel"])
            if lod <= 1:
                e0, e1 = sorted((s * stub, s * (stub + 0.02)))
                prims.box(mb, (cu0 - 0.01, e0, m(cb["plate"])), (cu1 + 0.01, e1, m(cb["top"]) + 0.01), st["steel"])

    lg = lf["legs"]
    edge_a = [(m(u), m(w)) for u, w in lg["edgeA"]]
    edge_b = [(m(u), m(w)) for u, w in lg["edgeB"]]
    if lod == 3:
        edge_a, edge_b = [edge_a[0], edge_a[-1]], [edge_b[0], edge_b[-1]]
    else:
        edge_a, edge_b = thin(edge_a, lod), (edge_b if lod <= 1 else [edge_b[0], edge_b[-1]])
    for s in (-1, 1):   # curved upper edge A up to the crossbeam, straight 60-degree lower edge B back down
        vc = s * m(lg["vCentre"])
        prims.prism(mb, edge_a + edge_b, (0, vc - m(lg["thick"]) / 2, 0), U, W, V, m(lg["thick"]), st["steel"])

    pv = lf["pivot"]
    if lod <= 1:   # adjusting rods beside the legs, with nuts above the crossbeam
        rd = pv["rod"]
        for s in (-1, 1):
            p0 = Vector((m(rd["u"]), s * m(rd["v"]), m(rd["bottom"])))
            p1 = Vector((m(rd["u"]), s * m(rd["v"]), m(rd["top"])))
            prims.cylinder(mb, p0, p1, m(rd["dia"]) / 2, parts.sides(lod, 8, 5), st["rod"])
            if lod == 0:
                prims.box(mb, tuple(p1 - Vector((0.035, 0.035, 0.09))), tuple(p1 + Vector((0.035, 0.035, -0.03))), st["rod"])

    po = lf["portal"]
    pu0, pu1 = m(po["uFrom"]), m(po["uTo"])
    pc = (pu0 + pu1) / 2
    t_lo, t_hi = m(po["bottom"]), m(po["top"])
    prims.box(mb, (pu0, -m(po["v"]) / 2, t_lo), (pu1, m(po["v"]) / 2, t_hi), st["steel"])
    for s in (-1, 1):
        prims.beam(mb, (pc, s * m(po["legFoot"]), m(cb["top"])), (pc, s * m(po["legHead"]), t_lo), m(po["leg"]), m(po["leg"]), st["steel"], up=U)
    lu = po["lugs"]
    if lod <= 1:   # twin lug plates under each end of the top beam, eye pin through them; small lugs near the middle
        for s in (-1, 1):
            for pv_ in lu["v"]:
                v0 = s * m(pv_)
                prims.box(mb, (pc - 0.06, v0 - 0.01, m(lu["bottom"])), (pc + 0.06, v0 + 0.01, t_lo), st["steel"])
            if lod == 0:
                w_pin = m(lu["bottom"]) + 0.06
                prims.cylinder(mb, (pc, s * (m(lu["v"][0]) - 0.03), w_pin), (pc, s * (m(lu["v"][1]) + 0.03), w_pin), 0.03, 8, st["rod"])
                prims.box(mb, (pc - 0.04, s * m(po["smallLugs"]) - 0.01, t_lo - 0.08), (pc + 0.04, s * m(po["smallLugs"]) + 0.01, t_lo), st["steel"])

    pf = lf["platform"]
    if lod <= 2:
        hv = m(pf["v"]) / 2
        deck = m(pf["top"])
        prims.box(mb, (m(pf["uFrom"]), -hv, deck - 0.05), (m(pf["uTo"]), hv, deck), st["grating"])
        if lod <= 1:   # knee braces from the crossbeam out under the deck edge
            (bu0, bw0), (bu1, bw1) = [(m(u), m(w)) for u, w in pf["brace"]]
            for s in (-1, 1):
                vb = s * (hv - 0.15)
                prims.beam(mb, (cu1, vb, bw0), (bu0, vb, bw0), 0.06, 0.06, st["steel"])
                prims.beam(mb, (bu0, vb, bw0), (bu1, vb, bw1), 0.06, 0.06, st["steel"])
        ho = pf["hoop"]
        uh, vh = m(ho["u"]), m(ho["v"])
        hoop(mb, ((uh, -vh, 0.0), (uh, vh, 0.0)), m(ho["top"]), deck, m(ho["corner"]), lod, st["galv"])
        if lod <= 1:
            prims.cylinder(mb, (uh, -vh, m(ho["mid"])), (uh, vh, m(ho["mid"])), 0.02, 6 if lod == 0 else 4, st["galv"], caps=(False, False))


def entry_trains(mb, a, c, et, lod, st):
    """Hold-down trains on both ropes, hung from the lifting-frame stubs (as photographed; see spec
    common.sheaveTrain)."""
    tr = c["sheaveTrain"]
    train = {"n": tr["n"], "pitch": m(tr["pitch"]), "rocker": m(tr["rocker"]), "beam": m(tr["beam"])}
    gs, hg = c["guideSheave"], m(c["lineGauge"]) / 2
    for side, name in ((-1, "l"), (1, "r")):
        parts.sheave_train(mb, a, name, m(et["u"]), side * hg, m(c["ropeElevation"]), m(c["ropeDiameter"]) / 2, m(gs["dia"]),
                           m(gs["width"]), train, et["mode"], (side * m(et["attach"][0]), m(et["attach"][1])), lod, st)


def bullwheel(mb, centre, pitch, bw, lod, st):
    """The exposed bullwheel: rim channel with the rope groove and a spacer ring, the top ring plate, eight spokes
    whose undersides rise from the hub to the rim (I-sections up close), the hub drum and bearing cap, and hook
    lugs under the rim (bw: spec "return" "bullwheel", mm, heights from the rope)."""
    c = Vector(centre)
    n = parts.sides(lod, 64, 32, 16, 8)
    rp = m(pitch) / 2
    bot, top = m(bw["rimBottom"]), m(bw["rimTop"])
    p_hi = m(bw["plateTop"])
    p_lo = p_hi - m(bw["plateThick"])
    ro, ri = m(bw["plateOuter"]) / 2, m(bw["plateInner"]) / 2
    r_out, r_in = rp + 0.045, rp - 0.11
    wheel = st["dark"]
    plate = wheel.but(snow=0.7)
    if lod <= 1:
        rim = [(r_in, bot), (r_out, bot), (r_out, -0.028), (rp + 0.008, -0.016), (rp + 0.008, 0.012), (r_out, 0.02),
               (r_out, top), (r_in + 0.06, top), (r_in + 0.06, p_lo), (r_in, p_lo)]
        prims.lathe(mb, c, W, rim, n, wheel)
        if lod == 0:
            prims.lathe(mb, c, W, [(rp + 0.008, -0.016), (rp + 0.001, -0.01), (rp + 0.001, 0.008), (rp + 0.008, 0.012)], n,
                        st["rubber"], closed=False)
        prims.lathe(mb, c, W, [(ri, p_lo), (ro, p_lo), (ro, p_hi), (ri, p_hi)], n, plate)
    elif lod == 2:
        prims.lathe(mb, c, W, [(r_in, bot), (r_out, bot), (r_out, p_lo), (ro, p_lo), (ro, p_hi), (ri, p_hi), (ri, p_lo), (r_in, p_lo)], n, plate)
    else:
        prims.lathe(mb, c, W, [(ri, bot), (ro, bot), (ro, p_hi), (ri, p_hi)], n, plate)
        return

    hr = m(bw["hubDia"]) / 2
    s_in, s_out = m(bw["spokeInner"]), m(bw["spokeOuter"])
    r0, r1 = hr * 0.95, r_in

    def under(rr):   # spoke underside: drawn at the hub and at the rim, straight between
        return s_in + (s_out - s_in) * (rr - hr) / (r_in - hr)

    sw, tf, web = m(bw["spokeWidth"]), 0.02, 0.014
    for i in range(8):
        ang = 2 * math.pi * i / 8
        d = U * math.cos(ang) + V * math.sin(ang)
        p = W.cross(d).normalized()
        prof = [(r0, under(r0)), (r1, under(r1)), (r1, p_lo), (r0, p_lo)]
        if lod == 0:   # web between the flanges, flanges full width
            web_prof = [(r0, under(r0) + tf), (r1, under(r1) + tf), (r1, p_lo - tf), (r0, p_lo - tf)]
            prims.prism(mb, web_prof, c - p * (web / 2), d, W, p, web, wheel)
            for fl in ([(r0, p_lo - tf), (r1, p_lo - tf), (r1, p_lo), (r0, p_lo)],
                       [(r0, under(r0)), (r1, under(r1)), (r1, under(r1) + tf), (r0, under(r0) + tf)]):
                prims.prism(mb, fl, c - p * (sw / 2), d, W, p, sw, wheel)
        else:
            prims.prism(mb, prof, c - p * (sw / 2), d, W, p, sw, wheel)
    prims.cylinder(mb, c + W * m(bw["hubBottom"]), c + W * m(bw["hubTop"]), hr, parts.sides(lod, 24, 16, 8), wheel)
    if lod <= 1:
        prims.cylinder(mb, c + W * m(bw["hubTop"]), c + W * m(bw["capTop"]), m(bw["capDia"]) / 2, parts.sides(lod, 16, 10), wheel)
    lg = bw["lugs"]
    if lod == 0:   # hook lugs under the rim, on the spoke lines
        (ra, wa), (rb, wb) = [(m(x), m(y)) for x, y in (lg["from"], lg["to"])]
        prof = [(ra, bot), (r_in + 0.07, bot), (rb, wb + 0.05), (rb, wb), (rb - 0.05, wb), (ra, wa)]
        for i in range(lg["count"]):
            ang = 2 * math.pi * i / lg["count"]
            d = U * math.cos(ang) + V * math.sin(ang)
            p = W.cross(d).normalized()
            prims.prism(mb, prof, c - p * 0.01, d, W, p, 0.02, wheel)


def build(spec, lod, stage):
    c, r = spec["common"], spec["return"]
    st = styles()
    a = Asset("sessellift_fgq4_return")
    body = MeshBuilder()
    rope = m(c["ropeElevation"])
    hg = m(c["lineGauge"]) / 2
    u_bw = m(r["carriageAt"])   # as drawn: the near end of the carriage's travel (carriageTravel)

    base(body, r, lod, st)
    carriage(body, r, u_bw, rope, lod, st)
    catwalk(body, r["catwalk"], u_bw, lod, st)
    guides(body, a, c, r, lod, st)
    lifting_frame(body, r["liftingFrame"], lod, st)
    entry_trains(body, a, c, r["entryTrains"], lod, st)

    centre = Vector((u_bw, 0.0, rope))
    if lod <= 2:
        wheel = MeshBuilder()
        bullwheel(wheel, centre, c["bullwheel"]["pitch"], r["bullwheel"], lod, st)
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        bullwheel(body, centre, c["bullwheel"]["pitch"], r["bullwheel"], lod, st)

    lf = r["liftingFrame"]
    ul = m(lf["u"])
    a.body = body
    a.sockets = {
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (u_bw, -hg, rope), "rope_right_bw": (u_bw, hg, rope),
        "rope_left_out": (ul, -hg, rope), "rope_right_out": (ul, hg, rope),
        "chair_load": (0.0, hg, rope),   # riders sit down over the pier, along the chair guide
        "foundation_base": (0.0, 0.0, m(r["footing"]["bottom"])),
    }
    a.dims.update({"bullwheelU": round(u_bw * 1000), "guideSheaveU": r["guideSheaves"]["u"], "liftingFrameU": lf["u"],
                   "railsEnd": r["rails"]["uTo"], "pedestalTop": r["pedestal"]["top"], "portalTop": lf["portal"]["top"],
                   "catwalkTop": r["catwalk"]["top"], "hubDia": r["bullwheel"]["hubDia"]})
    return a
