"""Sessellift D6 six-seat chair (sessellift_d6.json "chair"): the Sessellift quad chair made wider and hung lower by
rule (owner, LM2 and LM8), on the Sessellift detachable grip (sessellift_detach_grip.json, owner LM10).

  widen  every half-width of the quad's chair grows by widen x t: t = 0 at the hanger clamp, 1 at the top bar's end
         and beyond, linear between. The frames and top bar take widen.frames, the bench, backrest and safety bar
         widen.bench (round 2: the drawing's frames at 1,527 from the axis and its bench of six seats of 471).
  drop   every height under the hanger moves down by drop, so the bench hangs where the drawing hangs it.
  hanger the drawing's: from the grip's socket under the arm's head, down and outboard round the station's belly,
         in level into the top of the clamp.

The body is the D6's own (round 3, the photos and the game's look; the quad keeps chair.body): a padded bench on a
galvanised pan with a round front bolster banded per seat; a backrest padded in front and a ribbed galvanised shell
behind; the raised safety bar with a stay and a black handle per seat, three footrests and a black stop per seat on the
top bar; end brackets and black armrests at the bench's ends; the clamp a square plate with its bolts. Snow stays off
the seats and grips (round 3: the game's Snow layer turned the line white). The origin is the grip on the rope; up close
the grip and the hanger are moving parts (contract M3). Chair frame as the quad's: u = travel, v = outboard, w = up.
LOD2 keeps the backrest; LOD3 is a 30-triangle speck so a line reads out to 1.2 km.
"""
import copy
import json
import math
import os

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

KIND = "chair_detach"
LODS = 4
SPEC = "sessellift_d6.json"
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def m(x):
    return x / 1000.0


def derive(d6):
    """The six-seat chair's body spec from the quad's, by the widen and drop rules (mm), with the drawing's clamp, top
    bar, web plate and hanger."""
    quad = json.load(open(os.path.join(HERE, "sessellift_fgq4.json"), encoding="utf-8"))["chair"]
    wf, wb = d6["widen"]["frames"], d6["widen"]["bench"]
    c0, c1, dw = d6["clampHalf"], d6["topBarHalf"], d6["drop"]

    def hw(x, widen=wf):
        t = min(1.0, max(0.0, (abs(x) - c0) / (c1 - c0)))
        return x + (widen * t if x >= 0 else -widen * t)

    def hb(x):
        return hw(x, wb)

    ch = copy.deepcopy(quad)
    ch["seats"], ch["axisV"] = d6["seats"], d6["axisV"]
    ch["clamp"] = dict(d6["clamp"])
    ch["topBar"] = {"w": d6["topBar"]["w"], "halfLength": hw(quad["topBar"]["halfLength"])}
    ch["web"]["top"] = d6["topBar"]["w"] - d6["topBar"]["dia"] / 2 + 5
    ch["web"]["lower"] = [list(p) for p in d6["webLower"]]
    ch["sleeves"] = dict(quad["sleeves"], w=d6["topBar"]["w"], **{"from": hw(quad["sleeves"]["from"])}, to=hw(quad["sleeves"]["to"]))
    ch["sideFrame"] = [[u, hw(h), w + dw] for u, h, w in quad["sideFrame"]]
    ch["edgeRail"] = [[u, hw(h), w + dw] for u, h, w in quad["edgeRail"]]
    b = quad["bar"]
    ch["bar"] = {"pivot": [b["pivot"][0], b["pivot"][1] + dw], "arm": [[u, w + dw] for u, w in b["arm"]],
                 "armHalfWidth": hb(b["armHalfWidth"]),
                 "rail": dict(b["rail"], w=b["rail"]["w"] + dw, halfWidth=hb(b["rail"]["halfWidth"])),
                 "leg": [[u, hb(h), w + dw] for u, h, w in b["leg"]],
                 "rod": dict(b["rod"], w=b["rod"]["w"] + dw, halfWidth=hb(b["rod"]["halfWidth"])),
                 "handle": {"from": [b["handle"]["from"][0], b["handle"]["from"][1] + dw],
                            "to": [b["handle"]["to"][0], b["handle"]["to"][1] + dw], "halfWidth": hb(b["handle"]["halfWidth"])}}
    ch["bench"] = {"halfWidth": hb(quad["bench"]["halfWidth"]), "profile": [[u, w + dw] for u, w in quad["bench"]["profile"]]}
    ch["bottomMember"] = dict(quad["bottomMember"], w=quad["bottomMember"]["w"] + dw, halfWidth=hb(quad["bottomMember"]["halfWidth"]))
    br = quad["backrest"]
    ch["backrest"] = dict(br, top=[br["top"][0], br["top"][1] + dw], bottom=[br["bottom"][0], br["bottom"][1] + dw],
                          halfWidth=hb(br["halfWidth"]))
    ch["tube"] = dict(quad["tube"], hanger=d6["tubeHanger"], topBar=d6["topBar"]["dia"])
    ch["hanger"] = d6["hanger"]
    return ch


def styles():
    return {"steel": Style("chair_steel", snow=0.1), "seat": Style("seat_pad"), "pan": Style("chair_steel"),
            "black": Style("rubber"), "sign": Style("sign_black"), "band": Style("aluminium"), "galv": Style("galvanised", snow=0.1)}


def grip_styles():
    """No snow: grips run through the stations' tyres (round 3)."""
    return {"alu": Style("aluminium"), "black": Style("rubber"), "blue": Style("spring_blue"), "cream": Style("roller_cream"),
            "rod": Style("machined")}


def seat_centres(ch):
    """Each seat's middle, as a half-width from the chair's axis (mm)."""
    hwb = ch["bench"]["halfWidth"]
    n = ch["seats"]
    return [-hwb + (k + 0.5) * 2 * hwb / n for k in range(n)]


def build(spec, lod, stage):
    d6 = spec["chair"]
    ch = derive(d6)
    st = styles()
    gst = grip_styles()
    a = Asset("sessellift_d6_chair")
    mb = MeshBuilder()
    tube = {k: m(v) / 2 for k, v in ch["tube"].items()}
    if lod == 3:
        speck(mb, ch, st)
        return finish(a, mb, ch, d6)
    if lod == 2:
        far(mb, ch, st, gst)
        return finish(a, mb, ch, d6)
    near = lod == 0
    n_hanger, n_frame, n_thin = (6, 5, 4) if near else (4, 4, 3)
    # the hanger: its arm under the grip (the arm's inboard section turns inside the grip's body: its own piece for the
    # clash sweep), and the tube from the socket under the arm's head down round the belly and into the clamp
    mb.group = "hanger"
    parts.detach_grip_arm(mb, lod, gst, socket_group="hanger#socket")
    pts = d6["hanger"] if near else [d6["hanger"][i] for i in d6["hangerFar"]]
    prims.tube_path(mb, [(0.0, m(v), m(w)) for v, w in pts], tube["hanger"], n_hanger, st["steel"], caps=False, smooth=True)
    with mb.grouped("grip"):
        parts.detach_grip(mb, lod, gst)
    body(mb, ch, d6, near, n_frame, n_thin, tube, st)
    return finish(a, mb, ch, d6)


def backrest(mb, ch, d6, st, near):
    """The backrest: the black pad in front, the galvanised shell behind it with a pressed rib per bay (round 3: half the
    chairs on a line are seen from behind, and the photographed backs are bright steel)."""
    ax = m(ch["axisV"])
    br, bk = ch["backrest"], d6["backrest"]
    (tu, tw), (bu, bw) = br["top"], br["bottom"]
    along = Vector((m(tu - bu), 0.0, m(tw - bw)))
    length = along.length
    along.normalize()
    across = Vector((0.0, 1.0, 0.0))
    back = along.cross(across).normalized()            # toward the chair's back
    front = Vector((m(bu), ax, m(bw))) + along * (length / 2) - back * (m(br["thick"]) / 2)
    hw = m(br["halfWidth"])
    pad, shell = m(bk["pad"]), m(bk["shell"])
    prims.obox(mb, front + back * (pad / 2), (along, across, back), (length / 2, hw, pad / 2), st["seat"])
    sc = front + back * (pad + shell / 2)
    prims.obox(mb, sc, (along, across, back), (length / 2, hw, shell / 2), st["pan"], skip=("-2",))
    if near:
        out = sc + back * (shell / 2 + 0.001)
        n = bk["ribs"]
        rw = m(bk["ribWidth"]) / 2
        for k in range(n):   # a diagonal rib across each bay of the pressed shell
            v0, v1 = -hw + 2 * hw * k / n, -hw + 2 * hw * (k + 1) / n
            p0 = out - along * (length / 2 - 0.03) + across * (v0 + 0.02)
            p1 = out + along * (length / 2 - 0.03) + across * (v1 - 0.02)
            mb.face(mb.verts_lift([tuple(p0 - across * rw), tuple(p1 - across * rw), tuple(p1 + across * rw), tuple(p0 + across * rw)]),
                    st["pan"], tuple(back))
        for s in (-1, 1):   # the pad's black end caps
            c = front + back * (pad / 2) + across * (s * (hw + m(bk["endCap"]) / 2))
            prims.obox(mb, c, (along, across, back), (length / 2, m(bk["endCap"]) / 2, pad / 2 + 0.005), st["black"])


def bench(mb, ch, d6, st, near):
    """The bench: the seat padded on top and in front over a galvanised pan (round 3: from below the photographed pan is
    steel), its round front bolster in one black segment per seat between light bands, a lattice under the pan."""
    ax = m(ch["axisV"])
    b = ch["bench"]
    hwb = m(b["halfWidth"])
    prof = [(m(u), m(w)) for u, w in b["profile"]]
    n = len(prof)
    wind = prims.winding(prof)
    left = mb.verts_lift([(u, ax - hwb, w) for u, w in prof])
    right = mb.verts_lift([(u, ax + hwb, w) for u, w in prof])
    for k in range(n):
        k1 = (k + 1) % n
        (a0, b0), (a1, b1) = prof[k], prof[k1]
        na, nb = (b1 - b0) * wind, -(a1 - a0) * wind
        st_ = st["pan"] if nb < -0.5 * math.hypot(na, nb) or na < -0.7 * math.hypot(na, nb) else st["seat"]
        mb.face([left[k], left[k1], right[k1], right[k]], st_, (na, 0.0, nb))
    mb.face(list(left), st["seat"], (0.0, -1.0, 0.0))
    mb.face(list(right), st["seat"], (0.0, 1.0, 0.0))
    bo = d6["bench"]["bolster"]
    (bu, bw_), r = bo["at"], m(bo["dia"]) / 2
    sides = 8 if near else 4
    edges = [-hwb + 2 * hwb * k / ch["seats"] for k in range(ch["seats"] + 1)]
    band = m(bo["band"]) / 2
    for k in range(ch["seats"]):
        v0 = edges[k] + (band if k else 0.0)
        v1 = edges[k + 1] - (band if k < ch["seats"] - 1 else 0.0)
        prims.cylinder(mb, (m(bu), ax + v0, m(bw_)), (m(bu), ax + v1, m(bw_)), r, sides, st["seat"], smooth=True,
                       caps=(k == 0, k == ch["seats"] - 1))
    for k in range(1, ch["seats"]):
        prims.cylinder(mb, (m(bu), ax + edges[k] - band, m(bw_)), (m(bu), ax + edges[k] + band, m(bw_)), r + 0.003, sides, st["band"],
                       smooth=True, caps=(False, False))
    if near:   # the pan's flat-bar lattice, zig-zag across under it
        under = min(w for _, w in prof) - 0.006
        u0, u1 = min(u for u, _ in prof) + 0.03, max(u for u, _ in prof) - 0.03
        k = d6["bench"]["lattice"]
        for i in range(k):
            va, vb = -hwb + 2 * hwb * i / k, -hwb + 2 * hwb * (i + 1) / k
            ua, ub = (u0, u1) if i % 2 == 0 else (u1, u0)
            prims.beam(mb, Vector((ua, ax + va, under)), Vector((ub, ax + vb, under)), 0.03, 0.006, st["galv"])


def bar(mb, ch, d6, tube, near, n_thin, st):
    """The raised safety bar: its arms from the pivot sleeves (up close), the legs and rail, and up close the rod, a
    stay and a black handle per seat, the three footrests and a black stop per seat on the top bar (round 3: the comb
    and the stops are the bar's pattern in every photo)."""
    ax = m(ch["axisV"])
    b = ch["bar"]

    def sym(u, hw, w, s=1):
        return Vector((m(u), ax + s * m(hw), m(w)))

    rail = b["rail"]
    for s in ((-1, 1) if near else ()):
        prims.tube_path(mb, [sym(u, b["armHalfWidth"], w, s) for u, w in b["arm"]], tube["bar"], n_thin, st["steel"], smooth=True)
    leg = b["leg"]
    c = rail["corner"]
    right = [leg[i] for i in (4, 3, 2, 1, 0)] if near else [leg[4], leg[3], leg[0]]
    path = [sym(u, hw, w, 1) for u, hw, w in right]
    path += [sym(rail["u"], rail["halfWidth"] + c * 0.5, rail["w"] - c * 0.5, 1), sym(rail["u"], rail["halfWidth"], rail["w"], 1),
             sym(rail["u"], rail["halfWidth"], rail["w"], -1), sym(rail["u"], rail["halfWidth"] + c * 0.5, rail["w"] - c * 0.5, -1)]
    path += [sym(u, hw, w, -1) for u, hw, w in reversed(right)]
    prims.tube_path(mb, path, tube["bar"], n_thin, st["steel"], smooth=True)
    if not near:
        return
    rod = b["rod"]
    prims.cylinder(mb, sym(rod["u"], -rod["halfWidth"], rod["w"]), sym(rod["u"], rod["halfWidth"], rod["w"]), tube["rod"], 6, st["steel"],
                   caps=(False, False))
    bk = d6["bar"]
    for cv in seat_centres(ch):   # the comb: a stay from the rail down to the rod, and a black handle, at each seat
        stay = [(rail["u"], rail["w"])] + [(u, w) for u, _, w in leg[:3]]
        prims.tube_path(mb, [sym(u, cv, w) for u, w in stay], tube["rod"], 4, st["steel"], smooth=True)
        hd = b["handle"]
        prims.cylinder(mb, sym(hd["from"][0], cv, hd["from"][1]), sym(hd["to"][0], cv, hd["to"][1]), tube["handle"], 4, st["black"],
                       fan_caps=True)
    fr = bk["footrest"]
    for fv in bk["footrests"]:   # the footrests, on short legs below the rod
        for s in ((1, -1) if fv else (1,)):
            pts = [sym(u, s * fv, w) for u, _, w in leg[2:]]
            prims.tube_path(mb, pts, tube["bar"], 4, st["steel"], smooth=True)
            (fu, _, fw) = leg[-1]
            prims.box(mb, (m(fu - fr[0] / 2), ax + m(s * fv - fr[1] / 2), m(fw - fr[2])),
                      (m(fu + fr[0] / 2), ax + m(s * fv + fr[1] / 2), m(fw)), st["black"])
    top = ch["topBar"]
    sz = bk["stop"]
    r = m(d6["topBar"]["dia"]) / 2
    for cv in seat_centres(ch):   # the bar's stops on the top bar, behind it
        tri = [(-r, m(top["w"]) - m(sz[1]) / 2), (-r - m(sz[0]), m(top["w"])), (-r, m(top["w"]) + m(sz[1]) / 2)]
        prims.prism(mb, tri, (0.0, ax + m(cv - sz[2] / 2), 0.0), Vector((1, 0, 0)), Vector((0, 0, 1)), Vector((0, 1, 0)), m(sz[2]),
                    st["black"])


def body(mb, ch, d6, near, n_frame, n_thin, tube, st):
    """Everything under the hanger at LOD0-1: the clamp, the top bar with its web plate and the bar's sleeves, the side
    frames and edge rails, the bench, the bottom member, the backrest, the safety bar, and up close the bench's end
    brackets and armrests."""
    ax = m(ch["axisV"])

    def sym(u, hw, w, s=1):
        return Vector((m(u), ax + s * m(hw), m(w)))

    top, frame, cl = ch["topBar"], ch["sideFrame"], ch["clamp"]
    # the clamp: a square galvanised plate block the hanger runs into, its bolt heads on top and a black plate each face
    cu, chw = m(cl["u"]) / 2, m(cl["halfWidth"])
    prims.box(mb, (-cu, ax - chw, m(cl["wTo"])), (cu, ax + chw, m(cl["wFrom"])), st["steel"])
    if near:
        pw, ph = m(cl["plate"][0]) / 2, m(cl["plate"][1]) / 2
        wm = m((cl["wFrom"] + cl["wTo"]) / 2)
        for s in (-1, 1):
            mb.face(mb.verts_lift([(s * (cu + 0.001), ax - pw, wm - ph), (s * (cu + 0.001), ax + pw, wm - ph),
                                   (s * (cu + 0.001), ax + pw, wm + ph), (s * (cu + 0.001), ax - pw, wm + ph)]), st["sign"], (s, 0.0, 0.0))
            for t in (-1, 1):
                p = (s * (cu - 0.03), ax + t * (chw - 0.03), m(cl["wFrom"]))
                prims.cylinder(mb, p, (p[0], p[1], p[2] + 0.012), m(cl["bolts"]) / 2, 6, st["steel"])
    prims.cylinder(mb, sym(0, -top["halfLength"], top["w"]), sym(0, top["halfLength"], top["w"]), tube["topBar"], n_frame + 1, st["steel"],
                   caps=(False, False))
    if near:
        web = ch["web"]
        lower = web["lower"]
        poly = [(-m(lower[-1][0]), m(web["top"])), (m(lower[-1][0]), m(web["top"]))]
        poly += [(m(hw), m(w)) for hw, w in reversed(lower) if hw > 0]
        poly += [(-m(hw), m(w)) for hw, w in lower if hw > 0]
        prims.plate(mb, poly, (-m(web["thick"]) / 2, ax, 0), Vector((0, 1, 0)), Vector((0, 0, 1)), Vector((1, 0, 0)), m(web["thick"]),
                    st["steel"])
        parts.restraint_sleeves(mb, ch["sleeves"], tube["sleeve"], sym, st["steel"])
    main = frame if near else [frame[i] for i in (0, 3, 4, 5, 7, 9, 10)]
    edge = [ch["edgeRail"][i] for i in (0, 2, 3, 4, 6, 7, 8, 9)]
    for s in (-1, 1):
        prims.tube_path(mb, [sym(u, hw, w, s) for u, hw, w in main], tube["frame"], n_frame, st["steel"], caps=near, smooth=True)
        if near:
            prims.tube_path(mb, [sym(u, hw, w, s) for u, hw, w in edge], tube["edge"], n_thin, st["steel"])
    bench(mb, ch, d6, st, near)
    bm = ch["bottomMember"]
    if near:
        half = m(bm["size"]) / 2
        prims.box(mb, (m(bm["u"]) - half, ax - m(bm["halfWidth"]), m(bm["w"]) - half),
                  (m(bm["u"]) + half, ax + m(bm["halfWidth"]), m(bm["w"]) + half), st["steel"], skip=("-1", "+1"))
    backrest(mb, ch, d6, st, near)
    bar(mb, ch, d6, tube, near, n_thin, st)
    if near:
        hwb = ch["bench"]["halfWidth"]
        inner = min(h for _, h, _ in frame[4:8]) - ch["tube"]["frame"] / 2
        prof = ch["bench"]["profile"]
        bu0, bu1 = min(u for u, _ in prof), max(u for u, _ in prof)
        bw0 = min(w for _, w in prof)
        eb = d6["endBracket"]
        for s in (-1, 1):   # the brackets bolting the seat's pan to the frames, and the black armrests above them
            v0, v1 = sorted((s * hwb, s * (inner + 5)))
            prims.box(mb, (m(bu0 + 20), ax + m(v0), m(bw0 - eb["thick"])), (m(bu1 - 20), ax + m(v1), m(bw0 + eb["height"])), st["galv"])
            ar = d6["armrest"]
            prims.tube_path(mb, [sym(u, s * (hwb + ar["out"]), w) for u, w in ar["path"]], m(ar["dia"]) / 2, 5, st["black"])


def far(mb, ch, st, gst):
    """LOD2 (from 35-105 m to 300-900 m, most of a line): the grip as a block, the hanger post up into it (round 2: it
    floated off the grip), the top bar, the side frames, the bench and the backrest, two-tone (round 3: LOD2 had no
    backrest)."""
    ax = m(ch["axisV"])
    top, bench_ = ch["topBar"], ch["bench"]
    parts.detach_grip(mb, 2, gst)
    prims.box(mb, (-0.045, m(362), m(top["w"]) + 0.02), (0.045, m(460), -0.025), st["steel"])
    prims.box(mb, (-0.035, ax - m(top["halfLength"]), m(top["w"]) - 0.035), (0.035, ax + m(top["halfLength"]), m(top["w"]) + 0.035),
              st["steel"])
    side = max(h for _, h, _ in ch["sideFrame"]) - 48
    low = min(w for _, _, w in ch["sideFrame"]) + 57
    for s in (-1, 1):
        prims.box(mb, (-0.30, ax + s * m(side) - 0.03, m(low)), (0.0, ax + s * m(side) + 0.03, m(top["w"])), st["steel"])
    us = [p[0] for p in bench_["profile"]]
    ws = [p[1] for p in bench_["profile"]]
    prims.box(mb, (m(min(us)), ax - m(bench_["halfWidth"]), m(min(ws))), (m(max(us)), ax + m(bench_["halfWidth"]), m(max(ws))), st["seat"])
    br = ch["backrest"]
    (tu, tw), (bu, bw) = br["top"], br["bottom"]
    along = Vector((m(tu - bu), 0.0, m(tw - bw)))
    length = along.length
    along.normalize()
    back = along.cross(Vector((0.0, 1.0, 0.0))).normalized()
    c = Vector((m(bu), ax, m(bw))) + along * (length / 2)
    hw = m(br["halfWidth"])
    prims.obox(mb, c, (along, Vector((0.0, 1.0, 0.0)), back), (length / 2, hw, 0.04), st["seat"], skip=("+2",))
    rear = c + back * 0.041
    mb.face(mb.verts_lift([tuple(rear + sa * along * (length / 2) + sb * Vector((0.0, hw, 0.0))) for sa, sb in ((-1, -1), (1, -1), (1, 1), (-1, 1))]),
            st["pan"], tuple(back))


def speck(mb, ch, st):
    """LOD3, out to 1.2 km (round 3: chairs vanished at 300 m while the towers stayed): the seat and back as one dark
    block, the top bar and the hanger, none with an underside: 30 triangles."""
    ax = m(ch["axisV"])
    top, bench_, br = ch["topBar"], ch["bench"], ch["backrest"]
    us = [p[0] for p in bench_["profile"]] + [br["top"][0], br["bottom"][0]]
    ws = [p[1] for p in bench_["profile"]] + [br["top"][1]]
    hwb = m(bench_["halfWidth"])
    prims.box(mb, (m(min(us)), ax - hwb, m(min(ws))), (m(max(us)), ax + hwb, m(max(ws))), st["seat"], skip=("-2",))
    prims.box(mb, (-0.035, ax - m(top["halfLength"]), m(top["w"]) - 0.035), (0.035, ax + m(top["halfLength"]), m(top["w"]) + 0.035),
              st["steel"], skip=("-2",))
    prims.box(mb, (-0.045, m(362), m(top["w"])), (0.045, m(560), 0.0), st["steel"], skip=("-2",))


def finish(a, mb, ch, d6):
    mb.group = None
    a.body = mb
    ax = m(ch["axisV"])
    pivot = ch["bar"]["pivot"]
    a.sockets = {"grip": (0.0, 0.0, 0.0), "bar_hinge": (m(pivot[0]), ax, m(pivot[1]))}
    prof = ch["bench"]["profile"]
    top_w = m(max(w for _, w in prof))
    hw = m(ch["bench"]["halfWidth"])
    for i in range(ch["seats"]):
        vs = ax - hw + (i + 0.5) * (2 * hw / ch["seats"])
        a.sockets[f"seat_{i + 1}"] = (0.14, vs, top_w - 0.05)
    g = parts.detach_grip_spec()
    # the hanger hinges on the arm's axis, across the rope, under the grip
    a.pivots = {"grip": {"pos": (0.0, 0.0, 0.0), "axis": (0.0, 1.0, 0.0)},
                "hanger": {"pos": (0.0, m(g["socket"]["v"]), m(g["arm"]["axisW"])), "axis": (0.0, 1.0, 0.0), "parent": "grip",
                           "hinge": True}}
    a.socket_parents = {k: "hanger" for k in a.sockets if k != "grip"}
    bench_under = min(w for _, w in prof)
    a.dims.update({"seats": ch["seats"], "benchWidth": round(2 * ch["bench"]["halfWidth"]),
                   "drop": round(-min(bench_under, ch["bottomMember"]["w"] - ch["bottomMember"]["size"] / 2)),
                   "chairHalfWidth": round(max(h for _, h, _ in ch["sideFrame"]) + ch["tube"]["frame"] / 2),
                   "axisV": ch["axisV"], "ropeW": 3600})
    return a
