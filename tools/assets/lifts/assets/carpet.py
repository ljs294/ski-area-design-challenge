"""Glidepath magic carpet line modules, gallery, control post and hut (glidepath_carpet.json, the double's overlay
glidepath_carpet2.json; owner C1-C11, 2026-11-04).

  segment       a 2 m belt module: the belt (its own part "belt", UV0 in metres along u so the game scrolls it at
                160 fpm, C11), a white wear strip over each belt edge, the aluminium cover bar beside it with the livery
                line along it (C8), the ribbed side decks out to the skirt panels, and between two belts a walking deck
                (C9). Dark cover strips at its front joint lap onto the next module (top of the decks, outside of the
                skirts): a bend of 2 deg at the joint opens no gap. The frame stands on adjustable screw feet in two
                pairs, parts feet_back and feet_front, which the game stretches between the spec's min and max (feet
                up to 0.3 m; more than that is cut and fill). Sockets back and front at the belt top on the centre line
                (the joints the game hinges the modules about), foot_back and foot_front at grade.
  segment_1m    the 1 m filler, built the same way.
  rail          optional side rails along a 2 m module (C6, default off): posts on the decks' outer edges, top and knee
                rails running through to the next module.
  gallery       a 2 m gallery bay (C3, C10): one arch over the module's back joint and glass panes (C7) to the next
                arch, the panes' last 0.1 m stepped out to lap over the next bay's panes (no gap at a 2 deg crest, no
                coplanar glass when straight). Single: a near-round tube of aluminium arches on foot plates on the side
                decks; double: white rounded-rectangle arches on brackets off the skirts.
  gallery_exit  the same bay with an emergency-exit door each side.
  portal        the dark steel portal at a gallery's end: a face plate round the opening, posts to the ground and the
                livery sign box over it; origin at grade on the plane where the gallery's first bay starts (socket
                "gallery" at the belt top), the portal standing on the -u side of it.
  post          the control post at each end (shared by the single and the double): a steel post in a livery pad, the
                push-button box (red stop, coloured buttons, key) and the rules sign on top; its buttons face -u.
  hut           the Glidepath operator hut (shared): a white cabin on skids, glazed on three sides, a livery band under
                its flat roof; origin at its floor's centre, its door on -u.

Everything is in the lift frame (u along the carpet toward the other station, v right, w up), metres; a module's
origin is at grade under the belt's centre at its back joint, so a module's front socket is the next one's back.
"""
import math

from liftkit import prims
from liftkit.export import Asset
from liftkit.mesh import GLASS, MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "carpet_segment"
LODS = 4
SPEC = "glidepath_carpet.json"


def m(x):
    return x / 1000.0


def kind_of(variant):
    if variant in ("gallery", "gallery_exit", "portal"):
        return "carpet_gallery"
    if variant in ("post", "hut"):
        return "surface_hut"
    return KIND


def styles(spec):
    arch = spec.get("gallery", {}).get("archClass", "aluminium")
    return {
        "belt": Style("rubber", snow=0.15, trim="belt"),
        "strip": Style("white", snow=0.3, trim="plain"),
        "bar": Style("aluminium", snow=0.3),
        "line": Style("livery", livery=1.0, snow=0.2, trim="plain"),
        "deck": Style("paint_grey", snow=0.7, trim="ribbed"),
        "skirt": Style("paint_grey", snow=0.4),
        "joint": Style("trim_dark", snow=0.4),
        "bolt": Style("machined"),
        "under": Style("paint_dark"),
        "foot": Style("galvanised", snow=0.3),
        "rail": Style("galvanised", snow=0.5),
        "arch": Style(arch, snow=0.5),
        "glass": Style("glass", slot=GLASS),
        "door": Style("trim_dark", snow=0.3),
        "portal": Style("paint_dark", snow=0.5),
        "sign": Style("livery", livery=1.0, trim="plain"),
        "white": Style("white", snow=0.5, trim="plain"),
        "red": Style("sheave_red"),
        "pad": Style("livery", livery=1.0, snow=0.3, trim="seat"),
        "steel": Style("paint_grey", snow=0.4),
        "black": Style("sign_black"),
        "blue": Style("spring_blue"),
        "yellow": Style("safety_yellow"),
        "key": Style("machined"),
        "hut": Style("white", snow=0.4),
        "roof": Style("roof_alu", snow=1.0),
        "pane": Style("glass_black"),
        "frame": Style("trim_dark", snow=0.3),
    }


def build(spec, lod, stage, variant):
    if variant in ("segment", "segment_1m"):
        return segment(spec, lod, variant)
    if variant == "rail":
        return rail(spec, lod)
    if variant in ("gallery", "gallery_exit"):
        return gallery(spec, lod, variant)
    if variant == "portal":
        return portal(spec, lod)
    if variant == "post":
        return post(spec, lod)
    if variant == "hut":
        return hut(spec, lod)
    raise ValueError(f"carpet: no variant {variant}")


# -- helpers --------------------------------------------------------------------------------------------------------
FACES = ("top", "bottom", "vmin", "vmax", "umin", "umax")


def slab(mb, u0, u1, v0, v1, w0, w1, st, faces=FACES):
    """An axis-aligned box with only the listed faces (top, bottom, vmin, vmax, umin, umax)."""
    u0, u1 = sorted((u0, u1))
    v0, v1 = sorted((v0, v1))
    w0, w1 = sorted((w0, w1))
    quads = {
        "top": ([(u0, v0, w1), (u1, v0, w1), (u1, v1, w1), (u0, v1, w1)], W),
        "bottom": ([(u0, v0, w0), (u1, v0, w0), (u1, v1, w0), (u0, v1, w0)], -W),
        "vmin": ([(u0, v0, w0), (u1, v0, w0), (u1, v0, w1), (u0, v0, w1)], -V),
        "vmax": ([(u0, v1, w0), (u1, v1, w0), (u1, v1, w1), (u0, v1, w1)], V),
        "umin": ([(u0, v0, w0), (u0, v1, w0), (u0, v1, w1), (u0, v0, w1)], -U),
        "umax": ([(u1, v0, w0), (u1, v1, w0), (u1, v1, w1), (u1, v0, w1)], U),
    }
    for f in faces:
        pts, n = quads[f]
        prims.quad(mb, pts, st, n)


def side_faces(sg):
    """The face keys of a slab on side sg (+1: +v) facing outward (away from v = 0) and inward."""
    return ("vmax" if sg > 0 else "vmin"), ("vmin" if sg > 0 else "vmax")


def layout(spec):
    """(belt centres, the deck's outer edge |v|, the decks' v spans: the side decks first and last, the walking decks
    between two belts)."""
    s = spec["section"]
    belts = [m(c) for c in spec["belts"]]
    out = max(abs(c) for c in belts) + m(s["deckOut"])
    bt = m(s["bar"]["to"])
    decks = [(-out, belts[0] - bt)] + [(belts[i] + bt, belts[i + 1] - bt) for i in range(len(belts) - 1)] \
        + [(belts[-1] + bt, out)]
    return belts, out, decks


def deck_section(mb, spec, u0, u1, lod, st, belt_from=None):
    """The deck from u0 to u1 (open at both ends, so modules join): the belt (part "belt", from belt_from), the
    strips, bars and livery lines along each belt edge, the decks, the skirts and the frame's underside. With belt_from
    (a station's approach) the strips, bars and lines are closed at u0, where they stand above the comb's wedge."""
    s = spec["section"]
    belts, out, decks = layout(spec)
    edge = out - m(s["deckOut"])   # the outermost belt's centre |v|
    bh, top = m(s["beltHalf"]), m(s["beltTop"])
    dt, du = m(s["deckTop"]), m(s["deckUnder"])
    sb = m(s["skirtBottom"])
    bf = u0 if belt_from is None else belt_from
    with mb.grouped("belt"):
        for c in belts:
            slab(mb, bf, u1, c - bh + 0.003, c + bh - 0.003, top - m(s["beltThick"]), top, st["belt"], ("top",))
    if lod >= 3:   # the decks from each belt's edge, the side decks down to the skirts' foot in one face
        for i, (v0, v1) in enumerate(decks):
            if i == 0:
                v1 = belts[0] - bh
            elif i == len(decks) - 1:
                v0 = belts[-1] + bh
            else:
                v0, v1 = belts[i - 1] + bh, belts[i] - bh
            slab(mb, u0, u1, v0, v1, du, dt, st["deck"], ("top",))
            if i in (0, len(decks) - 1):
                slab(mb, u0, u1, v0, v1, sb, dt, st["skirt"], ("vmin",) if i == 0 else ("vmax",))
        return
    st_, ba, ln = s["strip"], s["bar"], s["line"]
    cap = ("umin",) if belt_from is not None else ()
    for c in belts:
        for sg in (-1, 1):
            outer, inner = side_faces(sg)
            if lod <= 1:
                slab(mb, u0, u1, c + sg * m(st_["from"]), c + sg * m(st_["to"]), m(st_["bottom"]), m(st_["top"]),
                     st["strip"], ("top", inner) + cap)
                slab(mb, u0, u1, c + sg * m(ba["from"]), c + sg * m(ba["to"]), m(ba["bottom"]), m(ba["top"]),
                     st["bar"], ("top", inner, outer) + cap)
                slab(mb, u0, u1, c + sg * m(ln["from"]), c + sg * m(ln["to"]), m(ba["top"]), m(ln["top"]),
                     st["line"], (("top", inner, outer) if lod == 0 else ("top",)) + cap)
            else:   # LOD2: strip and bar as one, the line on top
                slab(mb, u0, u1, c + sg * m(st_["from"]), c + sg * m(ba["to"]), m(ba["bottom"]), m(ba["top"]),
                     st["bar"], ("top", inner, outer) + cap)
                slab(mb, u0, u1, c + sg * m(ln["from"]), c + sg * m(ln["to"]), m(ba["top"]), m(ln["top"]),
                     st["line"], ("top",))
    for i, (v0, v1) in enumerate(decks):
        side = i in (0, len(decks) - 1)
        slab(mb, u0, u1, v0, v1, du, dt, st["deck"], ("top",))
        if side and lod <= 1:   # the deck plate's edge over the skirt
            slab(mb, u0, u1, v0, v1, du, dt, st["skirt"], ("vmin",) if i == 0 else ("vmax",))
    for sg in (-1, 1):
        outer, inner = side_faces(sg)
        if lod <= 1:
            slab(mb, u0, u1, sg * (edge + m(s["skirtIn"])), sg * (edge + m(s["skirtOut"])), sb, du, st["skirt"],
                 (outer, inner, "bottom"))
        else:   # LOD2: the skirt and the deck plate's edge as one panel, both faces
            slab(mb, u0, u1, sg * (out - 0.01), sg * out, sb, dt, st["skirt"], (outer, inner))
    fb = m(s["frameBottom"])
    slab(mb, u0, u1, -(edge + m(s["skirtIn"])), edge + m(s["skirtIn"]), fb, fb + 0.01, st["under"], ("bottom",))


def joint_covers(mb, spec, at, lod, st):
    """The dark cover strips over the joint at u = at, lapping onto the next module: across each deck's top and down the
    outside of each skirt (LOD0 with three bolts each)."""
    if lod >= 2:
        return
    s = spec["section"]
    belts, out, decks = layout(spec)
    edge = out - m(s["deckOut"])
    dt = m(s["deckTop"])
    j = m(s["jointStrip"])
    for v0, v1 in decks:
        slab(mb, at - j, at + j, v0, v1, dt, dt + 0.0015, st["joint"], ("top", "umin", "umax"))
    c = s["cover"]
    ch, pr = m(c["half"]), m(c["proud"])
    sb = m(s["skirtBottom"])
    for sg in (-1, 1):
        outer, inner = side_faces(sg)
        so = edge + m(s["skirtOut"])
        slab(mb, at - ch, at + ch, sg * so, sg * (so + pr), sb, dt - 0.004, st["joint"], (outer, "umin", "umax", "bottom", "top"))
        if lod == 0:
            for w in (sb + 0.06, (sb + dt) / 2, dt - 0.06):
                slab(mb, at - 0.007, at + 0.007, sg * (so + pr), sg * (so + pr + 0.004), w - 0.007, w + 0.007, st["bolt"],
                     (outer, "umin", "umax", "top", "bottom"))


def feet(mb, spec, us, lod, st):
    """The adjustable feet in pairs across the module (and one under a double's middle), each pair its own part
    (feet_back at us[0], feet_front at us[1]): square tube, base plate, LOD0 the adjusting nut."""
    if lod >= 2:
        return
    f = spec["feet"]
    belts, out, _ = layout(spec)
    edge = out - m(spec["section"]["deckOut"])
    vs = [-(edge + m(f["v"])), edge + m(f["v"])] + ([0.0] if len(belts) > 1 else [])
    t, p, pt = m(f["tube"]) / 2, m(f["plate"]) / 2, m(f["plateThick"])
    fb = m(spec["section"]["frameBottom"])
    for name, u in zip(("feet_back", "feet_front"), us):
        with mb.grouped(name):
            for v in vs:
                slab(mb, u - t, u + t, v - t, v + t, pt, fb, st["foot"], ("vmin", "vmax", "umin", "umax"))
                slab(mb, u - p, u + p, v - p, v + p, 0.0, pt, st["foot"], ("top", "vmin", "vmax", "umin", "umax"))
                if lod == 0:
                    slab(mb, u - 0.045, u + 0.045, v - 0.045, v + 0.045, 0.06, 0.085, st["foot"], FACES)


def line_dims(spec, a, length):
    s = spec["section"]
    belts, out, _ = layout(spec)
    a.dims.update({"module": round(length * 1000), "beltTop": s["beltTop"], "beltWidth": 2 * s["beltHalf"],
                   "deckTop": s["deckTop"], "deckWidth": round(2 * out * 1000), "belts": spec["belts"],
                   "speedFpm": spec["common"]["speedFpm"]})


# -- the belt modules -----------------------------------------------------------------------------------------------
def segment(spec, lod, variant):
    st = styles(spec)
    L = m(spec["module"]["filler" if variant == "segment_1m" else "length"])
    a = Asset(f"{spec['idPrefix']}_{variant}")
    mb = MeshBuilder()
    deck_section(mb, spec, 0.0, L, lod, st)
    joint_covers(mb, spec, L, lod, st)
    us = (0.25 * L, 0.75 * L)
    feet(mb, spec, us, lod, st)
    a.body = mb
    s, f = spec["section"], spec["feet"]
    top, fb = m(s["beltTop"]), m(s["frameBottom"])
    a.sockets = {"back": (0.0, 0.0, top), "front": (L, 0.0, top), "foot_back": (us[0], 0.0, 0.0), "foot_front": (us[1], 0.0, 0.0)}
    # the belt scrolls (UV0 x in metres along u); the feet stretch along w from the frame's underside: none turns
    a.pivots = {"belt": {"pos": (L / 2, 0.0, top), "axis": (1.0, 0.0, 0.0), "fixed": True},
                "feet_back": {"pos": (us[0], 0.0, fb), "axis": (0.0, 0.0, 1.0), "fixed": True},
                "feet_front": {"pos": (us[1], 0.0, fb), "axis": (0.0, 0.0, 1.0), "fixed": True}}
    line_dims(spec, a, L)
    a.dims.update({"footNominal": f["nominal"], "footMin": f["min"], "footMax": f["max"], "frameBottom": s["frameBottom"],
                   "maxBendDeg": 2.0})
    return a


def rail(spec, lod):
    """Side rails along a 2 m module (C6): galvanised posts on the decks' outer edges at the spec's u, top and knee
    rails running through both ends (open, so they join the next module's)."""
    st = styles(spec)
    r = spec["rail"]
    L = m(spec["module"]["length"])
    a = Asset(f"{spec['idPrefix']}_rail")
    mb = MeshBuilder()
    belts, out, _ = layout(spec)
    edge = out - m(spec["section"]["deckOut"])
    dt = m(spec["section"]["deckTop"])
    h, pr = m(r["height"]), m(r["post"]) / 2
    for sg in (-1, 1):
        v = sg * (edge + m(r["v"]))
        if lod <= 2:
            for u in (m(x) for x in r["posts"]):
                if lod <= 1:
                    prims.cylinder(mb, (u, v, dt), (u, v, dt + h), pr, 6 if lod == 0 else 4, st["rail"], caps=(False, True))
                    prims.box(mb, (u - 0.07, v - 0.07, dt), (u + 0.07, v + 0.07, dt + 0.01), st["rail"], skip=("-2",))
                else:
                    slab(mb, u - pr, u + pr, v - pr, v + pr, dt, dt + h, st["rail"], ("vmin", "vmax", "umin", "umax"))
        for k, w in enumerate((dt + h, dt + h * 0.5)):
            if lod <= 1:
                prims.cylinder(mb, (0.0, v, w), (L, v, w), pr * (1 if k == 0 else 0.8), 6 if lod == 0 else 4, st["rail"],
                               caps=(False, False))
            elif lod == 2 or k == 0:
                rr = pr * (1 if k == 0 else 0.8)
                slab(mb, 0.0, L, v - rr, v + rr, w - rr, w + rr, st["rail"], ("top", "bottom", "vmin", "vmax"))
    a.body = mb
    top = m(spec["section"]["beltTop"])
    a.sockets = {"back": (0.0, 0.0, top), "front": (L, 0.0, top)}
    line_dims(spec, a, L)
    a.dims.update({"railHeight": r["height"], "railV": round((edge + m(r["v"])) * 1000)})
    return a


# -- the gallery ----------------------------------------------------------------------------------------------------
def outline(spec, off, lod):
    """The gallery's cross-section line offset `off` outward (m) from its panes: (v, w) points from the right foot up,
    over the top and down to the left foot. Single: a circle cut at the deck top; double: a rounded rectangle on its
    brackets."""
    g = spec["gallery"]
    if g["shape"] == "round":
        R, cw, foot = m(g["radius"]) + off, m(g["centreW"]), m(spec["section"]["deckTop"])
        t0 = math.asin((foot - cw) / R)
        t1 = math.pi - t0
        n = g["facets"][lod]
        return [(R * math.cos(t0 + (t1 - t0) * k / n), cw + R * math.sin(t0 + (t1 - t0) * k / n)) for k in range(n + 1)]
    H, top, rc, fw = m(g["halfV"]) + off, m(g["roof"]) + off, m(g["corner"]) + off, m(g["footW"])
    steps = g["cornerSteps"][lod]
    right = [(H, fw), (H, top - rc)] + [(H - rc + rc * math.cos(math.pi / 2 * k / steps),
                                         top - rc + rc * math.sin(math.pi / 2 * k / steps)) for k in range(1, steps + 1)]
    return right + [(-v, w) for v, w in reversed(right)]


def outward(p0, p1):
    """The outward normal (lift frame) of the outline's facet from p0 to p1: the outline runs from the right foot over
    the top to the left foot, counter-clockwise in (v, w), so outward is its tangent turned clockwise."""
    tv, tw = p1[0] - p0[0], p1[1] - p0[1]
    n = math.hypot(tv, tw)
    return (0.0, tw / n, -tv / n)


def ribbon(mb, us, offs, spec, lod, st, gap=0.001):
    """A pane along u through the outline at each (u, offset) pair (us[i], offs[i]), facet by facet, facing out and,
    `gap` further in, facing in (seen from inside the gallery, and through the near side from outside, at every LOD:
    the far wall's tint doesn't pop at a LOD switch)."""
    for off_in, sign in ((0.0, 1), (-gap, -1)):
        lines = [outline(spec, o + off_in, lod) for o in offs]
        for k in range(len(lines[0]) - 1):
            nrm = tuple(sign * x for x in outward(lines[0][k], lines[0][k + 1]))
            for i in range(len(us) - 1):
                prims.quad(mb, [(us[i], *lines[i][k]), (us[i + 1], *lines[i + 1][k]), (us[i + 1], *lines[i + 1][k + 1]),
                                (us[i], *lines[i][k + 1])], st, nrm)


def arch(mb, spec, lod, st):
    """The arch over the bay's back joint (u = 0), its width along u, between the outline's inner and outer offsets:
    outer and inner faces, LOD0-1 the sides too; none at LOD3 (the panes alone). Returns its inner and outer lines."""
    g = spec["gallery"]
    hw = m(g["arch"]["width"]) / 2
    if g["shape"] == "round":
        o_in, o_out = m(g["arch"]["inner"] - g["radius"]), m(g["arch"]["outer"] - g["radius"])
    else:
        o_in, o_out = -m(g["arch"]["depth"]) / 2, m(g["arch"]["depth"]) / 2
    pin, pout = outline(spec, o_in, lod), outline(spec, o_out, lod)
    for k in range(len(pin) - 1 if lod <= 2 else 0):
        nrm = outward(pout[k], pout[k + 1])
        prims.quad(mb, [(-hw, *pout[k]), (hw, *pout[k]), (hw, *pout[k + 1]), (-hw, *pout[k + 1])], st, nrm)
        prims.quad(mb, [(-hw, *pin[k]), (hw, *pin[k]), (hw, *pin[k + 1]), (-hw, *pin[k + 1])], st, tuple(-x for x in nrm))
        if lod <= 1:
            for u, d in ((-hw, -U), (hw, U)):
                prims.quad(mb, [(u, *pin[k]), (u, *pout[k]), (u, *pout[k + 1]), (u, *pin[k + 1])], st, d)
    return pin, pout


def strip_at(pts, w, wide):
    """Where a polyline going up reaches height w: the point, half of a `wide` step along the line there, and the
    segment it lies on."""
    for p0, p1 in zip(pts, pts[1:]):
        if p0[1] <= w <= p1[1]:
            t = (w - p0[1]) / (p1[1] - p0[1])
            ln = math.dist(p0, p1)
            q = (p0[0] + (p1[0] - p0[0]) * t, w)
            return q, ((p1[0] - p0[0]) / ln * wide / 2, (p1[1] - p0[1]) / ln * wide / 2), (p0, p1)
    raise ValueError(f"no point at w = {w}")


def door(mb, spec, lod, st):
    """An emergency-exit door each side of the bay: dark frame strips just off the panes (jambs up the outline to the
    door's top, the header and a push bar), outside, and LOD0 inside too."""
    d = spec["gallery"]["exitDoor"]
    u0, u1, top, fw = m(d["from"]), m(d["to"]), m(d["top"]), m(d["frame"])
    w_bar = m(spec["section"]["deckTop"]) + 1.0
    for off in ((0.008, -0.008) if lod == 0 else (0.008,)):
        line = outline(spec, off, lod)
        right = []   # up the right side to the door's top
        for p0, p1 in zip(line, line[1:]):
            right.append(p0)
            if p1[1] >= top:
                t = (top - p0[1]) / (p1[1] - p0[1])
                right.append((p0[0] + (p1[0] - p0[0]) * t, top))
                break
        face = 1 if off > 0 else -1
        for sg in (1, -1):
            pts = [(sg * v, w) for v, w in right]

            def nrm(p0, p1):   # the left side runs down the outline: its facets turned round
                n = outward(p0, p1) if sg > 0 else outward(p1, p0)
                return tuple(x * face for x in n)
            for p0, p1 in zip(pts, pts[1:]):
                for ua, ub in ((u0, u0 + fw), (u1 - fw, u1)):   # the jambs
                    prims.quad(mb, [(ua, *p0), (ub, *p0), (ub, *p1), (ua, *p1)], st, nrm(p0, p1))
            for w, wide in ((top - fw / 2, fw), (w_bar, fw * 0.7)):   # the header, the push bar
                q, e, (p0, p1) = strip_at(pts, w, wide)
                prims.quad(mb, [(u0 + fw, q[0] - e[0], q[1] - e[1]), (u1 - fw, q[0] - e[0], q[1] - e[1]),
                                (u1 - fw, q[0] + e[0], q[1] + e[1]), (u0 + fw, q[0] + e[0], q[1] + e[1])], st, nrm(p0, p1))


def gallery(spec, lod, variant):
    st = styles(spec)
    g = spec["gallery"]
    L = m(spec["module"]["length"])
    a = Asset(f"{spec['idPrefix']}_{variant}")
    mb = MeshBuilder()
    pin, pout = arch(mb, spec, lod, st["arch"])
    pn = g["pane"]
    if lod <= 1:   # the panes from inside this arch to inside the next, the last stretch stepped out over the next bay's
        lf, lt, lo = m(pn["lapFrom"]), m(pn["lapTo"]), m(pn["lapOut"])
        ribbon(mb, [0.0, lf, lf + 0.02, lt], [0.0, 0.0, lo, lo], spec, lod, st["glass"])
    else:          # far: joint to joint
        ribbon(mb, [0.0, L], [0.0, 0.0], spec, lod, st["glass"])
    s = spec["section"]
    dt = m(s["deckTop"])
    if lod <= 1:
        if g["shape"] == "round":   # foot plates on the side decks
            fa, ft = m(g["foot"]["along"]) / 2, m(g["foot"]["thick"])
            for i in (0, -1):
                v0, v1 = sorted((pin[i][0], pout[i][0]))
                slab(mb, -fa, fa, v0 - 0.02, v1 + 0.02, dt, dt + ft, st["arch"], ("top", "vmin", "vmax", "umin", "umax"))
        else:   # brackets off the skirts under the legs
            b = g["bracket"]
            ba = m(b["along"]) / 2
            for sg in (1, -1):
                outer, _ = side_faces(sg)
                slab(mb, -ba, ba, sg * m(b["from"]), sg * m(b["to"]), m(b["bottom"]), m(b["top"]), st["portal"],
                     ("top", "bottom", "umin", "umax", outer))
    if variant == "gallery_exit" and lod <= 1:
        door(mb, spec, lod, st["door"])
    a.body = mb
    top = m(s["beltTop"])
    a.sockets = {"back": (0.0, 0.0, top), "front": (L, 0.0, top)}
    line_dims(spec, a, L)
    hi = max(w for _, w in pout)
    a.dims.update({"shape": g["shape"], "outerHeight": round(hi * 1000), "outerHalfV": round(max(v for v, _ in pout) * 1000),
                   "innerHeight": round((max(w for _, w in pin) - top) * 1000),
                   "innerHalfV": round(max(v for v, _ in pin) * 1000), "archWidth": g["arch"]["width"],
                   "feet": [[round(min(pin[0][0], pout[0][0]) * 1000), round(max(pin[0][0], pout[0][0]) * 1000),
                             round(pin[0][1] * 1000)]]})
    if variant == "gallery_exit":
        a.dims["exitDoor"] = [g["exitDoor"]["from"], g["exitDoor"]["to"], g["exitDoor"]["top"]]
    return a


def portal(spec, lod):
    """The portal at a gallery's end (u from -depth to 0): the face plate between the opening and the outer rectangle,
    its edges, the posts and the sign box (livery panels front and back)."""
    st = styles(spec)
    p, g, s = spec["portal"], spec["gallery"], spec["section"]
    a = Asset(f"{spec['idPrefix']}_portal")
    mb = MeshBuilder()
    H, top, d, sf = m(p["halfV"]), m(p["top"]), m(p["depth"]), m(p["sign"]["from"])
    belts, out, _ = layout(spec)
    dt = m(s["deckTop"])
    if g["shape"] == "round":
        R, cw = m(p["opening"]), m(g["centreW"])
        t0 = math.asin((dt - cw) / R)
        n = g["facets"][lod]
        arc = [(R * math.cos(t0 + (math.pi - 2 * t0) * k / n), cw + R * math.sin(t0 + (math.pi - 2 * t0) * k / n))
               for k in range(n + 1)]
        inner = [(out, 0.0), (out, dt)] + arc + [(-out, dt), (-out, 0.0)]
        c, low = (0.0, cw), cw
    else:
        o = p["opening"]
        oH, oT, rc = m(o["halfV"]), m(o["top"]), m(o["corner"])
        steps = g["cornerSteps"][lod]
        right = [(oH, 0.0), (oH, oT - rc)] + [(oH - rc + rc * math.cos(math.pi / 2 * k / steps),
                                               oT - rc + rc * math.sin(math.pi / 2 * k / steps)) for k in range(1, steps + 1)]
        inner = right + [(-v, w) for v, w in reversed(right)]
        c, low = (0.0, oT - rc), oT - rc

    def outer_of(q):   # where the plate meets the rectangle: level out below the centre, along the ray from it above
        v, w = q
        if w <= low + 1e-9:
            return (math.copysign(H, v) if abs(v) > 1e-9 else H, w)
        dv, dw = v - c[0], w - c[1]
        ts = [t for t in ((math.copysign(H, dv) - c[0]) / dv if abs(dv) > 1e-9 else None, (sf - c[1]) / dw if dw > 1e-9 else None)
              if t is not None and t > 0]
        t = min(ts)
        return (c[0] + dv * t, c[1] + dw * t)

    def edge_of(q):
        return "top" if abs(q[1] - sf) < 1e-6 else ("r" if q[0] > 0 else "l")

    outer = [outer_of(q) for q in inner]
    mid = -d / 2
    pair = ((-d, (-1.0, 0.0, 0.0)), (0.0, (1.0, 0.0, 0.0))) if lod <= 1 else \
        ((mid - 0.0005, (-1.0, 0.0, 0.0)), (mid + 0.0005, (1.0, 0.0, 0.0)))
    if lod <= 2:
        for k in range(len(inner) - 1):
            poly = [inner[k], inner[k + 1], outer[k + 1]]
            if edge_of(outer[k]) != edge_of(outer[k + 1]) and "top" in (edge_of(outer[k]), edge_of(outer[k + 1])):
                poly.append((math.copysign(H, outer[k][0] + outer[k + 1][0]), sf))
            poly.append(outer[k])
            uniq = []
            for q in poly:
                if not uniq or math.dist(q, uniq[-1]) > 1e-6:
                    uniq.append(q)
            if math.dist(uniq[0], uniq[-1]) < 1e-6:
                uniq.pop()
            if len(uniq) < 3:
                continue
            for u, nrm in pair:
                prims.quad(mb, [(u, *q) for q in uniq], st["portal"], nrm)
        if lod <= 1:   # the opening's edge
            for q0, q1 in zip(inner, inner[1:]):
                tv, tw = q1[0] - q0[0], q1[1] - q0[1]
                ln = math.hypot(tv, tw)
                prims.quad(mb, [(-d, *q0), (0.0, *q0), (0.0, *q1), (-d, *q1)], st["portal"], (0.0, -tw / ln, tv / ln))
    # posts to the ground (and below it, for uneven snow), the sign box over the plate
    pw = m(p["post"])
    for sg in (1, -1):
        outer_k, inner_k = side_faces(sg)
        if lod <= 2:
            slab(mb, -d - 0.02, 0.02, sg * (H - pw), sg * H, -0.3, sf, st["portal"],
                 (outer_k, "umin", "umax") if lod <= 1 else (outer_k, inner_k, "umin", "umax"))
        else:
            slab(mb, mid - 0.0005, mid + 0.0005, sg * (H - pw), sg * H, -0.3, sf, st["portal"], ("umin", "umax"))
    sg_ = p["sign"]
    st_th = m(sg_["thick"])
    s0, s1 = -d / 2 - st_th / 2, -d / 2 + st_th / 2
    if lod <= 2:
        slab(mb, s0, s1, -H - 0.01, H + 0.01, sf, top, st["portal"])
    else:
        slab(mb, mid - 0.0005, mid + 0.0005, -H - 0.01, H + 0.01, sf, top, st["portal"], ("umin", "umax"))
    if lod <= 2:   # the livery panels, 3 mm plates on the sign box; LOD0 white lettering 2 mm plates on them
        for base, sgn, nrm in ((s0, -1, "umin"), (s1, 1, "umax")):
            edges = (nrm, "top", "bottom", "vmin", "vmax") if lod <= 1 else (nrm,)
            pu = base + sgn * 0.003
            slab(mb, min(base, pu), max(base, pu), -H + 0.05, H - 0.05, sf + 0.05, top - 0.05, st["sign"], edges)
            if lod == 0:
                lu = pu + sgn * 0.002
                for w0, w1, half in ((0.62, 0.74, 0.55), (0.38, 0.46, 0.35)):
                    ww0, ww1 = sf + (top - sf) * w0, sf + (top - sf) * w1
                    slab(mb, min(pu, lu), max(pu, lu), -H * half, H * half, ww0, ww1, st["white"], edges)
    a.body = mb
    a.sockets = {"gallery": (0.0, 0.0, m(s["beltTop"]))}
    a.dims.update({"halfV": p["halfV"], "top": p["top"], "depth": p["depth"],
                   "opening": p["opening"] if isinstance(p["opening"], dict) else {"radius": p["opening"], "centreW": g["centreW"]}})
    return a


# -- the control post and the hut (shared by the single and the double) ---------------------------------------------
def post(spec, lod):
    st = styles(spec)
    p = spec["post"]
    a = Asset("glidepath_carpet_post")
    mb = MeshBuilder()
    pr, h = m(p["post"]) / 2, m(p["height"])
    pad = p["pad"]
    pw0, pw1, ps = m(pad["from"]), m(pad["to"]), m(pad["size"]) / 2
    b = p["box"]
    bu, bv, bh = (m(x) / 2 for x in b["size"])
    bw = m(b["w"])
    if lod >= 3:   # far: the padded post and its box as one block, and the sign
        slab(mb, -ps, ps, -ps, ps, 0.0, bw + 2 * bh, st["pad"], ("umin", "umax", "vmin", "vmax", "top"))
    else:
        slab(mb, -pr, pr, -pr, pr, -0.2, h, st["steel"], ("umin", "umax", "vmin", "vmax", "top"))
    if lod <= 1:   # the padding, round
        prims.cylinder(mb, (0, 0, pw0), (0, 0, pw1), ps * 1.08, 10 if lod == 0 else 6, st["pad"])
    elif lod == 2:
        slab(mb, -ps, ps, -ps, ps, pw0, pw1, st["pad"], ("umin", "umax", "vmin", "vmax", "top"))
    if lod <= 2:
        slab(mb, -bu, bu, -bv, bv, bw, bw + 2 * bh, st["steel"], FACES if lod <= 1 else ("umin", "umax", "vmin", "vmax", "top"))
    if lod <= 1:   # the buttons on the box's -u face: the red stop (a mushroom), LOD0 the others and the key
        fu = -bu
        cz = bw + bh
        prims.cylinder(mb, (fu, 0.0, cz + 0.05), (fu - 0.02, 0.0, cz + 0.05), 0.022, 8 if lod == 0 else 6, st["steel"], caps=(False, True))
        prims.cylinder(mb, (fu - 0.02, 0.0, cz + 0.05), (fu - 0.045, 0.0, cz + 0.05), 0.036, 10 if lod == 0 else 6, st["red"],
                       caps=(True, True))
        if lod == 0:
            for (v, w), key in (((-0.055, -0.03), "white"), ((0.0, -0.03), "blue"), ((0.055, -0.03), "yellow"),
                                ((-0.055, -0.09), "black"), ((0.055, -0.09), "key")):
                prims.cylinder(mb, (fu, v, cz + w), (fu - 0.012, v, cz + w), 0.014, 6, st[key], caps=(False, True))
    sg = p["sign"]
    sw, sh, sz = m(sg["width"]) / 2, m(sg["height"]), m(sg["w"])
    top = bw + 2 * bh
    if lod <= 2:   # the pole, thinner than the sign it carries, up into it
        slab(mb, -0.006, 0.006, -0.012, 0.012, top, sz + 0.02, st["steel"], ("umin", "umax", "vmin", "vmax"))
    if lod <= 2:
        slab(mb, -0.008, 0.008, -sw, sw, sz, sz + sh, st["white"])
    else:
        slab(mb, -0.0005, 0.0005, -sw, sw, sz, sz + sh, st["white"], ("umin", "umax"))
    if lod <= 1:   # the red border and, LOD0, lines of text: thin plates on the front
        br = m(sg["border"])
        for v0, v1, w0, w1 in ((-sw, sw, sz + sh - br, sz + sh), (-sw, sw, sz, sz + br), (-sw, -sw + br, sz + br, sz + sh - br),
                               (sw - br, sw, sz + br, sz + sh - br)):
            slab(mb, -0.009, -0.008, v0, v1, w0, w1, st["red"], ("umin", "top", "bottom", "vmin", "vmax"))
        if lod == 0:
            for k in range(5):
                w = sz + sh - br - 0.05 - k * 0.055
                slab(mb, -0.009, -0.008, -sw + br + 0.04, sw - br - 0.04 - (0.1 if k % 2 else 0.0), w - 0.012, w,
                     st["black"], ("umin", "top", "bottom", "vmin", "vmax"))
    a.body = mb
    a.sockets = {"panel": (-bu, 0.0, bw + bh)}
    a.dims.update({"height": round((sz + sh) * 1000), "padSize": pad["size"]})
    return a


def hut(spec, lod):
    st = styles(spec)
    h = spec["hut"]
    a = Asset("glidepath_carpet_hut")
    mb = MeshBuilder()
    hx, hy, sk, ht = m(h["halfU"]), m(h["halfV"]), m(h["skid"]), m(h["height"])
    rt, ov = m(h["roofThick"]), m(h["overhang"])
    if lod <= 2:   # skids
        for sg in (-1, 1):
            slab(mb, -hx - 0.05, hx + 0.05, sg * (hy - 0.2) - 0.06, sg * (hy - 0.2) + 0.06, 0.0, sk, st["frame"],
                 ("top", "umin", "umax", "vmin", "vmax"))
    slab(mb, -hx, hx, -hy, hy, sk, ht, st["hut"], ("umin", "umax", "vmin", "vmax", "bottom") if lod <= 1 else
         ("umin", "umax", "vmin", "vmax"))
    # the flat roof, overhanging: a dark fascia, the ribbed top
    slab(mb, -hx - ov, hx + ov, -hy - ov, hy + ov, ht, ht + rt, st["frame"],
         ("umin", "umax", "vmin", "vmax", "bottom"))
    slab(mb, -hx - ov, hx + ov, -hy - ov, hy + ov, ht, ht + rt, st["roof"], ("top",))
    if lod <= 2:   # the livery band under the roof
        b0, b1 = m(h["band"][0]), m(h["band"][1])
        slab(mb, -hx - 0.006, hx + 0.006, -hy - 0.006, hy + 0.006, b0, b1, st["sign"])
    if lod >= 2:
        a.body = mb
        a.sockets = {"door": (-hx, m(h["door"]["v"]), sk)}
        a.dims.update({"footprint": [2 * h["halfU"], 2 * h["halfV"]], "height": h["height"] + h["roofThick"]})
        return a
    win = h["window"]
    w0, w1 = m(win["from"]), m(win["to"])
    dr = h["door"]
    dv0, dv1 = m(dr["v"]) - m(dr["width"]) / 2, m(dr["v"]) + m(dr["width"]) / 2
    # the windows: frames standing out of the walls, black panes in them; the front's beside the door
    wins = [("umin", -hx, (dv1 + 0.2, hy - 0.18)), ("vmin", -hy, (-hx + 0.18, hx - 0.18)), ("vmax", hy, (-hx + 0.18, hx - 0.18))]
    for face, pos, (a0, a1) in wins:
        s = -1 if face in ("umin", "vmin") else 1
        if face == "umin":
            slab(mb, pos + s * 0.03, pos, a0 - 0.04, a1 + 0.04, w0 - 0.04, w1 + 0.04, st["frame"], (face, "top", "bottom", "vmin", "vmax"))
            slab(mb, pos + s * 0.031, pos + s * 0.031, a0, a1, w0, w1, st["pane"], (face,))
        else:
            slab(mb, a0 - 0.04, a1 + 0.04, pos, pos + s * 0.03, w0 - 0.04, w1 + 0.04, st["frame"], (face, "top", "bottom", "umin", "umax"))
            slab(mb, a0, a1, pos + s * 0.031, pos + s * 0.031, w0, w1, st["pane"], (face,))
            if lod == 0:   # mullions
                for k in (1, 2):
                    um = a0 + (a1 - a0) * k / 3
                    slab(mb, um - 0.025, um + 0.025, pos + s * 0.031, pos + s * 0.04, w0, w1, st["frame"], (face, "umin", "umax"))
    # the door on the front (-u): a grey leaf in a dark frame, a handle
    slab(mb, -hx - 0.025, -hx, dv0 - 0.04, dv1 + 0.04, sk, sk + m(dr["height"]) + 0.04, st["frame"], ("umin", "top", "vmin", "vmax"))
    slab(mb, -hx - 0.03, -hx - 0.03, dv0, dv1, sk + 0.02, sk + m(dr["height"]), st["steel"], ("umin",))
    if lod == 0:
        slab(mb, -hx - 0.07, -hx - 0.03, dv1 - 0.1, dv1 - 0.08, sk + 1.0, sk + 1.04, st["key"], ("umin", "top", "bottom", "vmin", "vmax"))
        for su in (-1, 1):   # corner trim
            for sv in (-1, 1):
                slab(mb, su * hx - 0.03, su * hx + 0.03, sv * hy - 0.03, sv * hy + 0.03, sk, m(h["band"][0]), st["frame"],
                     ("umin", "umax", "vmin", "vmax", "top"))
    a.body = mb
    a.sockets = {"door": (-hx - 0.03, m(dr["v"]), sk)}
    a.dims.update({"footprint": [2 * h["halfU"], 2 * h["halfV"]], "height": h["height"] + h["roofThick"]})
    return a
