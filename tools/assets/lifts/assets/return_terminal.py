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
    """The lifting frame at the entry (the integrated first tower): two curved plate legs from the rails up to a
    flat top under a plate, seated on a joint plate against the crossbeam; the adjusting rods through the leg-top
    plates and the rod plates on the crossbeam; the crossbeam with narrower end stubs closed by plates with a V
    pendant and a ring boss; the portal (legs cut level on bolted foot plates and under the beam, end lug plates,
    small U-lugs, ring bosses on the beam ends); and the platform (channel frame with grating, brackets and an
    inner-end plate at the crossbeam, flat-bar knee braces to the hoop posts, the railing hoop across its outer
    end with a centre post)."""
    cb = lf["crossbeam"]
    cu0, cu1, cb0, cb1 = m(cb["uFrom"]), m(cb["uTo"]), m(cb["bottom"]), m(cb["top"])
    half = m(cb["v"]) / 2
    prims.box(mb, (cu0, -half, cb0), (cu1, half, cb1), st["steel"])
    sb = cb["stub"]
    su0, su1, stub = m(sb["uFrom"]), m(sb["uTo"]), m(sb["to"])
    for s in (-1, 1):   # narrower end stubs
        v0, v1 = sorted((s * half, s * stub))
        prims.box(mb, (su0, v0, m(sb["bottom"])), (su1, v1, cb1), st["steel"], skip=("-1",) if s > 0 else ("+1",))
    ep = cb["endPlate"]
    if lod <= 1:   # stub end plates: the stub outline with a V pendant, and a ring boss
        outline = [(m(u), m(w)) for u, w in ep["outline"]]
        th = m(ep["thick"])
        rg = ep["ring"]
        for s in (-1, 1):
            prims.prism(mb, outline, (0, stub if s > 0 else -stub - th, 0), U, W, V, th, st["steel"])
            if lod == 0:
                vr = s * (stub + th)
                prims.cylinder(mb, (m(rg["at"][0]), vr, m(rg["at"][1])), (m(rg["at"][0]), vr + s * m(rg["proud"]), m(rg["at"][1])),
                               m(rg["dia"]) / 2, 16, st["steel"])
    rp = cb["rodPlates"]
    if lod <= 2:
        for s in (-1, 1):
            v0, v1 = sorted((s * m(rp["v"][0]), s * m(rp["v"][1])))
            prims.box(mb, (m(rp["u"][0]), v0, m(rp["w"][0])), (m(rp["u"][1]), v1, m(rp["w"][1])), st["steel"])

    lg = lf["legs"]
    edge_a = [(m(u), m(w)) for u, w in lg["edgeA"]]
    edge_b = [(m(u), m(w)) for u, w in lg["edgeB"]]
    if lod == 3:
        edge_a, edge_b = [edge_a[0], edge_a[-2], edge_a[-1]], [edge_b[0], edge_b[-1]]
    elif lod >= 1:   # fewer points on the curve; the flat top and the back edge stay
        edge_a = thin(edge_a[:-2], lod) + edge_a[-2:]
        edge_b = edge_b if lod == 1 else [edge_b[0], edge_b[1], edge_b[-1]]
    vlo, vhi = m(lg["vCentre"]) - m(lg["thick"]) / 2, m(lg["vCentre"]) + m(lg["thick"]) / 2
    tp, jp = lg["topPlate"], lg["jointPlate"]
    for s in (-1, 1):   # curved upper edge A to a flat top, vertical back edge, straight 60-degree lower edge B
        prims.prism(mb, edge_a + edge_b, (0, s * m(lg["vCentre"]) - m(lg["thick"]) / 2, 0), U, W, V, m(lg["thick"]), st["steel"])
        if lod <= 2:
            v0, v1 = sorted((s * m(tp["v"][0]), s * m(tp["v"][1])))
            prims.box(mb, (m(tp["u"][0]), v0, m(tp["w"][0])), (m(tp["u"][1]), v1, m(tp["w"][1])), st["steel"])
            j0, j1 = sorted((s * vlo, s * vhi))
            prims.box(mb, (m(jp["u"][0]), j0, m(jp["w"][0])), (m(jp["u"][1]), j1, m(jp["w"][1])), st["steel"])

    rd = lf["rods"]
    if lod <= 1:   # adjusting rods through the leg-top plates and the rod plates, with nuts and a washer
        for s in (-1, 1):
            ru, rv = m(rd["u"]), s * m(rd["v"])
            prims.cylinder(mb, (ru, rv, m(rd["bottom"])), (ru, rv, m(rd["top"])), m(rd["dia"]) / 2, parts.sides(lod, 8, 5), st["rod"])
            if lod == 0:
                circ = m(rd["nut"]) / 2 / math.cos(math.pi / 6)
                for n0, n1 in rd["nuts"]:
                    prims.cylinder(mb, (ru, rv, m(n0)), (ru, rv, m(n1)), circ, 6, st["rod"], phase=0.0)
                wa = rd["washer"]
                prims.cylinder(mb, (ru, rv, m(wa["w"][0])), (ru, rv, m(wa["w"][1])), m(wa["dia"]) / 2, 10, st["rod"])

    po = lf["portal"]
    pu0, pu1 = m(po["uFrom"]), m(po["uTo"])
    t_lo, t_hi = m(po["bottom"]), m(po["top"])
    hv = m(po["v"]) / 2
    prims.box(mb, (pu0, -hv, t_lo), (pu1, hv, t_hi), st["steel"])
    lgp = po["leg"]
    for s in (-1, 1):   # legs square in u, leaning out in v, cut level on the foot plates and under the beam
        f0, f1 = sorted((s * m(lgp["foot"][0]), s * m(lgp["foot"][1])))
        h0, h1 = sorted((s * m(lgp["head"][0]), s * m(lgp["head"][1])))
        prims.prism(mb, [(f0, m(lgp["footW"])), (f1, m(lgp["footW"])), (h1, t_lo), (h0, t_lo)], (pu0, 0, 0), V, W, U, pu1 - pu0, st["steel"])
    fpl = po["footPlates"]
    if lod <= 2:
        for s in (-1, 1):
            v0, v1 = sorted((s * m(fpl["v"][0]), s * m(fpl["v"][1])))
            prims.box(mb, (m(fpl["u"][0]), v0, m(fpl["w"][0])), (m(fpl["u"][1]), v1, m(fpl["w"][1])), st["steel"])
            if lod == 0:
                for bu in fpl["boltsU"]:
                    prims.cylinder(mb, (m(bu), s * m(fpl["boltV"]), m(fpl["w"][1])), (m(bu), s * m(fpl["boltV"]), m(fpl["w"][1]) + 0.02),
                                   0.018, 6, st["rod"])
    if lod <= 1:
        lu = po["lugs"]   # end lug plates through the beam, narrowing to a round-bottomed lobe
        a0, a1 = m(lu["top"][0]), m(lu["top"][1])
        l0, l1 = m(lu["lobe"][0]), m(lu["lobe"][1])
        sh, lr = m(lu["shoulder"]), (m(lu["lobe"][1]) - m(lu["lobe"][0])) / 2
        lc, ctr = (l0 + l1) / 2, m(lu["bottom"]) + lr
        outline = [(a0, t_hi), (a0, sh), (l0, ctr + 0.035), (l0, ctr)]
        outline += [(lc + lr * math.cos(a_), ctr + lr * math.sin(a_)) for a_ in (math.pi * (1 + k / 6) for k in range(1, 6))]
        outline += [(l1, ctr), (l1, ctr + 0.035), (a1, sh), (a1, t_hi)]
        th = m(lu["thick"])
        for s in (-1, 1):
            for vl in lu["v"]:
                prims.prism(mb, outline, (0, s * m(vl) - th / 2, 0), U, W, V, th, st["steel"])
        er = po["endRing"]
        for s in (-1, 1):
            prims.cylinder(mb, (m(er["at"][0]), s * hv, m(er["at"][1])), (m(er["at"][0]), s * (hv + m(er["proud"])), m(er["at"][1])),
                           m(er["dia"]) / 2, parts.sides(lod, 16, 10), st["steel"])
        sl = po["smallLugs"]   # U-lugs under the beam, facing along u
        sv0, sv1 = m(sl["v"][0]), m(sl["v"][1])
        r_, c_ = (sv1 - sv0) / 2, (sv0 + sv1) / 2
        wc = m(sl["bottom"]) + r_
        for s in (-1, 1):
            pts = [(s * sv0, t_lo), (s * sv0, wc)]
            pts += [(s * (c_ - r_ * math.cos(a_)), wc - r_ * math.sin(a_)) for a_ in (math.pi * k / 6 for k in range(1, 6))]
            pts += [(s * sv1, wc), (s * sv1, t_lo)]
            prims.prism(mb, pts, (m(sl["u"][0]), 0, 0), V, W, U, m(sl["u"][1]) - m(sl["u"][0]), st["steel"])

    pf = lf["platform"]
    if lod <= 2:
        pu0_, pu1_ = m(pf["uFrom"]), m(pf["uTo"])
        ho, deck, ch, si = m(pf["v"]) / 2, m(pf["top"]), m(pf["channel"]), m(pf["sides"])
        if lod <= 1:   # channel frame (sides and both ends) with grating set in, tied to the crossbeam
            for s in (-1, 1):
                v0, v1 = sorted((s * si, s * ho))
                prims.box(mb, (pu0_, v0, deck - ch), (pu1_, v1, deck), st["steel"])
            prims.box(mb, (pu0_, -si, deck - ch), (m(pf["innerEnd"]), si, deck), st["steel"])
            prims.box(mb, (m(pf["outerEnd"]), -si, deck - ch), (pu1_, si, deck), st["steel"])
            prims.box(mb, (m(pf["innerEnd"]), -si, deck - m(pf["grating"])), (m(pf["outerEnd"]), si, deck), st["grating"])
            bk = pf["brackets"]
            for s in (-1, 1):
                v0, v1 = sorted((s * m(bk["v"][0]), s * m(bk["v"][1])))
                prims.box(mb, (m(bk["u"][0]), v0, deck - ch), (m(bk["u"][1]), v1, deck), st["steel"])
            ip = pf["innerPlate"]
            prims.box(mb, (m(ip["u"][0]), -ho, m(ip["w"][0])), (m(ip["u"][1]), ho, m(ip["w"][1])), st["steel"])
            br = pf["braces"]
            for s in (-1, 1):
                vb = s * m(br["v"])
                prims.beam(mb, (m(br["from"][0]), vb, m(br["from"][1])), (m(br["to"][0]), vb, m(br["to"][1])), m(br["thick"]), m(br["depth"]),
                           st["steel"])
        else:
            prims.box(mb, (pu0_, -ho, deck - ch), (pu1_, ho, deck), st["grating"])
        hp = pf["hoop"]
        uh, vh = m(hp["u"]), m(hp["v"])
        hoop(mb, ((uh, -vh, 0.0), (uh, vh, 0.0)), m(hp["top"]), m(hp["postFoot"]), m(hp["corner"]), lod, st["galv"])
        if lod <= 1:
            n = 6 if lod == 0 else 4
            prims.cylinder(mb, (uh, -vh, m(hp["mid"])), (uh, vh, m(hp["mid"])), 0.02, n, st["galv"], caps=(False, False))
            prims.cylinder(mb, (uh, 0, deck), (uh, 0, m(hp["top"])), m(hp["dia"]) / 2, n, st["galv"], caps=(False, False))


def entry_trains(mb, a, spec, r, lod, st):
    """The integrated first tower is a hold-down tower (owner): on each rope, the line towers' hold-down assembly
    (liftkit.parts.line_assembly, after the owner's reference model), its equaliser hung directly under the lifting
    frame's crossbeam end through two lug plates. On the reference arc levelled at its first sheave (spec "level":
    "first"), the rope runs level from the bullwheel through the loading area and bends up under the assembly, so
    the line leaves the station climbing (owner). Returns the first axle's u (where the level rope meets the row),
    the rope's exit point (u, w) over the last sheave and its exit angle above level (radians)."""
    c, et = spec["common"], r["entryTrains"]
    asm = parts.line_spec(spec)
    radius = asm["arcs"][et["arc"]]
    level = et.get("level", "apex")
    hg, rope = m(c["lineGauge"]) / 2, m(c["ropeElevation"])
    cb0 = m(r["liftingFrame"]["crossbeam"]["bottom"])
    lg, X = et["lugs"], asm["x"]
    # a row levelled at its first sheave tilts, which moves its main pin along the line: a trial at full detail
    # finds that offset, so the pin lands under the crossbeam (et.u) at every LOD
    pin_off, _ = parts.line_assembly(MeshBuilder(), Asset("trial"), "t", 0.0, hg, rope, asm, et["count"], "hold", 0, st,
                                     radius, level=level)
    u0 = m(et["u"]) - pin_off
    for side, name in ((-1, "l"), (1, "r")):
        v_rope = side * hg
        pin_u, pin_w = parts.line_assembly(mb, a, name, u0, v_rope, rope, asm, et["count"], "hold", lod, st, radius,
                                           first=2, parts_lod=1, level=level)
        b_in, b_out = v_rope - side * X["beam"][1], v_rope - side * X["beam"][0]
        lt = m(lg["thick"])
        if lod <= 2:   # lug plates either side of the equaliser, from the crossbeam's underside down past the pin
            for va, vb in (sorted((b_in, b_in - side * lt)), sorted((b_out, b_out + side * lt))):
                prims.box(mb, (pin_u - m(lg["halfU"]), va, pin_w - m(lg["past"])), (pin_u + m(lg["halfU"]), vb, cb0), st["steel"])
        if lod <= 1:
            prims.cylinder(mb, (pin_u, b_in - side * (lt + 0.012), pin_w), (pin_u, b_out + side * (lt + 0.012), pin_w),
                           asm["mainPin"] / 2, 12 if lod == 0 else 8, st["rod"])
    axles = parts.arc_axles(asm, et["count"], radius, level)
    u_first = u0 + axles[0][0]
    if level == "first":
        path, theta = parts.hold_rope(asm, et["count"], radius, u_first, rope)
        return u_first, path[-1], theta
    return u_first, (u0 + axles[-1][0], rope), 0.0   # a level rope leaves over the outermost sheave


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
    u_hold, (u_out, w_out), exit_angle = entry_trains(body, a, spec, r, lod, st)

    centre = Vector((u_bw, 0.0, rope))
    if lod <= 2:
        wheel = MeshBuilder()
        bullwheel(wheel, centre, c["bullwheel"]["pitch"], r["bullwheel"], lod, st)
        a.parts["bullwheel"] = wheel
        a.pivots["bullwheel"] = {"pos": tuple(centre), "axis": (0.0, 0.0, 1.0)}
    else:
        bullwheel(body, centre, c["bullwheel"]["pitch"], r["bullwheel"], lod, st)

    lf = r["liftingFrame"]
    a.body = body
    a.sockets = {   # the rope: level from the bullwheel to the hold-down row (hold), leaving it climbing (out)
        "line": (0.0, 0.0, 0.0),
        "rope_left_bw": (u_bw, -hg, rope), "rope_right_bw": (u_bw, hg, rope),
        "rope_left_hold": (u_hold, -hg, rope), "rope_right_hold": (u_hold, hg, rope),
        "rope_left_out": (u_out, -hg, w_out), "rope_right_out": (u_out, hg, w_out),
        "chair_load": (0.0, hg, rope),   # riders sit down over the pier, along the chair guide
        "foundation_base": (0.0, 0.0, m(r["footing"]["bottom"])),
    }
    a.dims.update({"bullwheelU": round(u_bw * 1000), "guideSheaveU": r["guideSheaves"]["u"], "liftingFrameU": lf["u"],
                   "railsEnd": r["rails"]["uTo"], "pedestalTop": r["pedestal"]["top"], "portalTop": lf["portal"]["top"],
                   "catwalkTop": r["catwalk"]["top"], "hubDia": r["bullwheel"]["hubDia"],
                   "ropeExitDeg": round(math.degrees(exit_angle), 2)})
    return a
