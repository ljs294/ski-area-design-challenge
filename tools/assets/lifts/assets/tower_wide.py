"""Wide-gauge line tower heads (sessellift_d6.json "towerWide"; owner LM3, LM5, LM12), after the owner's reference tower
(Gate 0, 2026-10-21; review T1, 2026-10-22), built from the line gauge, so the one builder makes a head for any gauge
(5.6 m for the D6 now, bigger lifts later). On the kit's straight mast and bases (owner: no taper, the same tubes and
foundations):

  cap          an octagonal plate on the mast top, sized to our tube with the reference's overhangs, bolted
  crossarm     a box across the line, haunched down to the cap with its bottom flange flared, ending just inboard of
               each rope
  end blocks   at each crossarm end, twin cheek plates clamped round the arm (top bars, tie rods) with the pin the rope's
               train hangs from like a pendulum, about 100 above a support rope; the round stub over the rope on the
               arm's axis carries nothing (review T1: Gate 0 had the train hanging from it)
  trains       the kit's sheaves on the reference's train (liftkit/wide_train.py): s4 s6 s10 b10 d10 c8, the c8 the
               quads' 4 hold-down over 4 support; a hold-down row hangs wholly below the end block, so its rope runs
               lower (owner, 2026-10-22)
  lifting beam above, on two tapered box legs leaning out from the crossarm, with lifting eyes over each rope, end
               plates and a hoist bar on standoffs
  walkways     on trains of ten only (owner): one stringer with stepped treads on a single hanger from the train's
               yoke, so it turns with the train (owner, Gate 1 v4: no supports off each rocker); railed on the inner side; two small caged platforms on the crossarm's +u face; the mast ladder carried on up to
               the beam, side ladders from the platforms down to the walkways
  number plate a light plate on the crossarm's uphill (-u) face

Origin on the mast top; sockets rope_left/rope_right, mast_top, number_plate. Tower frame: the lift frame's axes, +u the
ladder and platform side.
"""
import math

from mathutils import Vector

import tower
from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W
from liftkit.wide_train import combo_train, wide_train

KIND = "tower_head_wide"
LODS = 4
SPEC = "sessellift_d6.json"
CHAIR = "d6_chair"   # the chair the build sends past the head (liftkit/fit.py tower)


def m(x):
    return x / 1000.0


def hexahedron(mb, lo4, hi4, style):
    """A closed solid between two quads (lo4 at the foot, hi4 at the top, matching corners, lift frame)."""
    a = mb.verts_lift([tuple(p) for p in lo4])
    b = mb.verts_lift([tuple(p) for p in hi4])
    centre = sum((Vector(p) for p in list(lo4) + list(hi4)), Vector()) / 8
    for k in range(4):
        k1 = (k + 1) % 4
        q = [a[k], a[k1], b[k1], b[k]]
        mid = (Vector(lo4[k]) + Vector(lo4[k1]) + Vector(hi4[k1]) + Vector(hi4[k])) / 4
        mb.face(q, style, tuple(mid - centre))
    mb.face(a, style, tuple(Vector(lo4[0]) - Vector(hi4[0])))
    mb.face(b, style, tuple(Vector(hi4[0]) - Vector(lo4[0])))


def cheek_outline(eb, pin_w, lod):
    """An end-block cheek in (u, w): the reference's V plate from under the arm down to a round nose about the pin, a
    notch up the middle under the arm."""
    hu, top, sh, R = m(eb["halfU"]), m(eb["top"]), m(eb["shoulder"]), m(eb["nose"])
    nh, nb = m(eb["notch"]["halfU"]), m(eb["notch"]["bottom"])
    if lod >= 2:
        return [(-hu, top), (hu, top), (hu, sh), (R * 0.8, pin_w - R), (-R * 0.8, pin_w - R), (-hu, sh)]
    steps = 6 if lod == 0 else 3
    arc = [(R * math.cos(t), pin_w + R * math.sin(t)) for t in
           (math.radians(340 - 140 * k / steps) for k in range(steps + 1))]
    notch = [(nh, top), (nh, nb + 0.05), (nh * 0.66, nb + 0.012), (0.0, nb), (-nh * 0.66, nb + 0.012), (-nh, nb + 0.05), (-nh, top)]
    if lod >= 1:
        notch = [(nh, top), (nh, nb + 0.03), (0.0, nb), (-nh, nb + 0.03), (-nh, top)]
    return [(-hu, top)] + list(reversed(notch)) + [(hu, top), (hu, sh)] + arc + [(-hu, sh)]


def obround(wu, ww, n):
    """An obround (u wide, w tall) as a polygon of about 2n points round its centre."""
    r = wu / 2
    c = max(0.0, ww / 2 - r)
    pts = [(r * math.cos(t), c + r * math.sin(t)) for t in (math.pi * k / n for k in range(n + 1))]
    return pts + [(-x, -y) for x, y in pts]


def build(spec, lod, stage, variant):
    c, t, hw = spec["common"], spec["tower"], spec["towerWide"]
    st = dict(tower.styles(), white=Style("white", snow=0.3))
    cfg = hw["heads"][variant]
    asm = parts.line_spec(spec)
    radius = m(cfg["radius"]) if "radius" in cfg else asm["arcs"][cfg["arc"]]
    tr = parts.metres(hw["train"], keep=("sides", "stop"))
    a = Asset(f"sessellift_d6_tower_{variant}")
    mb = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    r = m(t["mast"]["dia"]) / 2
    cap, ca, eb, sb = hw["cap"], hw["crossarm"], hw["endBlock"], hw["stub"]
    lb, wk, pf = hw["liftingBeam"], hw["walkway"], hw["platform"]
    cw = m(ca["width"]) / 2
    cb0, cb1 = m(ca["bottom"]), m(ca["top"])
    end = hg - m(ca["endInset"])
    pin_w = tr["pin"]["w"]
    combo = "combo" in cfg
    mode = "hold" if (combo or "hold" in cfg) else "support"
    rope = tr["rope"]["combo" if combo else mode]

    # cap plate (the reference's elongated octagon) and the haunched crossarm, its bottom flange flared to the cap
    hu, hv, cu, cv = m(cap["halfU"]), m(cap["halfV"]), m(cap["cut"][0]), m(cap["cut"][1])
    ct = m(cap["thick"])
    octo = [(-hu + cu, -hv), (hu - cu, -hv), (hu, -hv + cv), (hu, hv - cv), (hu - cu, hv), (-hu + cu, hv), (-hu, hv - cv), (-hu, -hv + cv)]
    prims.prism(mb, octo, (0.0, 0.0, 0.0), U, V, W, ct, st["galv"])
    if lod == 0:
        bo = cap["bolts"]
        for su in (-1, 1):
            for vv in bo["v"]:
                for sv in (-1, 1):
                    prims.cylinder(mb, (su * m(bo["u"]), sv * m(vv), ct), (su * m(bo["u"]), sv * m(vv), ct + 0.025), m(bo["dia"]) / 2, 6, st["rod"])
    hn = ca["haunch"]
    sec = [(-end, cb0), (-m(hn["from"]), cb0), (-m(hn["to"]), ct), (m(hn["to"]), ct),
           (m(hn["from"]), cb0), (end, cb0), (end, cb1), (-end, cb1)]
    prims.prism(mb, sec, (-cw, 0.0, 0.0), V, W, U, 2 * cw, st["galv"])
    if lod <= 2:   # the flared bottom flange and its sloped side plates, one wedge each side of the mast
        fl, web = m(hn["flare"]) / 2, m(hn["web"])
        v_f, v_t = m(hn["from"]), m(hn["to"])
        for s in (-1, 1):
            lo4 = [(-cw, s * v_f, cb0), (cw, s * v_f, cb0), (fl, s * v_t, ct), (-fl, s * v_t, ct)]
            hi4 = [(-cw, s * v_f, cb0 + 0.001), (cw, s * v_f, cb0 + 0.001), (cw, s * v_t, web), (-cw, s * v_t, web)]
            hexahedron(mb, lo4, hi4, st["galv"])

    # the end blocks, the stubs, and each rope's train hanging from its block's pin
    rockers = {}
    tops = {}
    for side, tag in ((-1, "l"), (1, "r")):
        v_rope = side * hg
        inb = -side

        def vx(x):
            return v_rope + inb * x
        if lod >= 3:   # the end block as one box, so the train doesn't float under the arm
            prims.box(mb, (-m(eb["halfU"]), min(vx(m(eb["cheeks"][0][0])), vx(m(eb["cheeks"][1][1]))), pin_w - m(eb["nose"])),
                      (m(eb["halfU"]), max(vx(m(eb["cheeks"][0][0])), vx(m(eb["cheeks"][1][1]))), m(eb["top"])), st["galv"])
        if lod <= 2:
            out = cheek_outline(eb, pin_w, lod)
            for x0, x1 in eb["cheeks"]:
                prims.prism(mb, out, (0.0, vx(m(x0)), 0.0), U, W, V * inb, m(x1) - m(x0), st["galv"])
            fw, fh, ft = (m(x) for x in sb["flange"])   # the unloaded stub over the rope, on its flange
            sw0 = (cb0 + cb1) / 2
            x_end = hg - end
            if lod <= 1:
                prims.box(mb, (-fw / 2, vx(x_end - ft), sw0 - fh / 2), (fw / 2, vx(x_end), sw0 + fh / 2), st["galv"])
            prims.prism(mb, obround(m(sb["u"]), m(sb["w"]), 4 if lod == 0 else 2), (0.0, vx(x_end - ft), sw0), U, W, -V * inb,
                        x_end - ft + m(sb["reach"]), st["galv"])
        if lod <= 1:
            bx = m(eb["boss"]["thick"])
            (c0, c1), (c2, c3) = ((m(x0), m(x1)) for x0, x1 in eb["cheeks"])
            with mb.grouped("#pin"):   # the hanging pin through both cheeks
                prims.cylinder(mb, (0.0, vx(c0 - bx), pin_w), (0.0, vx(c3 + bx + 0.01), pin_w), tr["pin"]["dia"] / 2,
                               parts.sides(lod, 12, 8), st["rod"])
            tb = eb["topBars"]
            for xa, xb in tb["x"]:   # the clamp: top bars across the arm, tie rods down past it to the cheeks
                prims.box(mb, (-m(tb["halfU"]), vx(m(xa)), cb1), (m(tb["halfU"]), vx(m(xb)), cb1 + m(tb["thick"])), st["galv"])
            if lod == 0:
                for xb0, xb1 in ((c0 - bx, c0), (c3, c3 + bx)):
                    prims.cylinder(mb, (0.0, vx(xb0), pin_w), (0.0, vx(xb1), pin_w), m(eb["boss"]["dia"]) / 2, 12, st["galv"])
                rd = eb["rods"]
                for su in (-1, 1):
                    for xc in ((c0 + c1) / 2, (c2 + c3) / 2):
                        p = (su * m(rd["u"]), vx(xc))
                        prims.cylinder(mb, (p[0], p[1], m(eb["top"])), (p[0], p[1], m(rd["top"])), m(rd["dia"]) / 2, 8, st["rod"])
                        nw = cb1 + m(tb["thick"])
                        prims.cylinder(mb, (p[0], p[1], nw), (p[0], p[1], nw + m(eb["nut"][1])), m(eb["nut"][0]) / 2, 6, st["rod"])
        if combo:
            rk, top = combo_train(mb, a, tag, v_rope, rope, pin_w, asm, tr, cfg["combo"], radius, lod, st)
        else:
            rk, top = wide_train(mb, a, tag, v_rope, rope, pin_w, asm, tr, cfg[mode], mode, radius, lod, st)
        rockers[side], tops[side] = rk, top

    # the lifting beam on its two tapered legs, kept to LOD3 (round 3: the frame is the head's signature at range)
    bs = m(lb["size"])
    bw0 = m(lb["w"])
    bl = m(lb["halfLength"])
    bl_in = bl - m(lb["endPlate"]["thick"])   # the end plates within the beam's length
    prims.box(mb, (-bs / 2, -bl_in, bw0), (bs / 2, bl_in, bw0 + bs), st["galv"])
    lg = lb["legs"]
    ft_, tp = lg["foot"], lg["top"]
    for s in (-1, 1):
        fv0, fv1 = sorted((s * m(ft_["v"][0]), s * m(ft_["v"][1])))
        tv0, tv1 = sorted((s * m(tp["v"][0]), s * m(tp["v"][1])))
        fu, tu = m(ft_["u"]), m(tp["u"])
        fw = m(ft_["w"])
        lo4 = [(-fu, fv0, fw), (fu, fv0, fw), (fu, fv1, fw), (-fu, fv1, fw)]
        hi4 = [(-tu, tv0, bw0), (tu, tv0, bw0), (tu, tv1, bw0), (-tu, tv1, bw0)]
        if lod >= 3:
            prims.beam(mb, (0.0, (fv0 + fv1) / 2, fw), (0.0, (tv0 + tv1) / 2, bw0), 0.2, 0.25, st["galv"], up=U)
        else:
            hexahedron(mb, lo4, hi4, st["galv"])
        if lod <= 1:   # the foot plate on the crossarm
            fp = lg["footPlate"]
            vc = s * m(ft_["v"][0]) + s * m(fp[1]) / 2 - s * 0.021
            prims.box(mb, (-m(fp[0]) / 2, vc - m(fp[1]) / 2, cb1), (m(fp[0]) / 2, vc + m(fp[1]) / 2, cb1 + m(fp[2])), st["galv"])
        if lod == 0:   # bolts through the foot plate, and the grab bar on the inner face
            for su in (-1, 1):
                for sv in (-1, 1):
                    pv = s * m(ft_["v"][0]) + s * 0.05 + sv * 0.035
                    prims.cylinder(mb, (su * 0.2, pv, cb1 + m(fp[2])), (su * 0.2, pv, cb1 + m(fp[2]) + 0.02), 0.016, 6, st["rod"])
            gb = lg["grab"]

            def inner_v(w):
                k = (w - fw) / (bw0 - fw)
                return s * (m(ft_["v"][0]) + (m(tp["v"][0]) - m(ft_["v"][0])) * k) - s * m(gb["off"])
            g0, g1 = m(gb["w"][0]), m(gb["w"][1])
            prims.cylinder(mb, (0.0, inner_v(g0), g0), (0.0, inner_v(g1), g1), m(gb["dia"]) / 2, 6, st["galv"])
            for gw in (g0, g1):
                prims.cylinder(mb, (0.0, inner_v(gw), gw), (0.0, inner_v(gw) + s * (m(gb["off"]) + 0.01), gw), m(gb["dia"]) / 2, 6, st["galv"])
    if lod <= 1:
        ep = lb["endPlate"]   # end plates, a bore and four bolts
        pr = bs / 2 + m(ep["proud"])
        for s in (-1, 1):
            v0, v1 = sorted((s * bl_in, s * bl))
            prims.box(mb, (-pr, v0, bw0 + bs / 2 - pr), (pr, v1, bw0 + bs / 2 + pr), st["galv"])
            if lod == 0:
                vo = s * bl
                prims.cylinder(mb, (0.0, vo, bw0 + bs / 2), (0.0, vo + s * 0.003, bw0 + bs / 2), m(ep["bore"]) / 2, 10, st["rubber"])
                for bu in (-1, 1):
                    for bw_ in (-1, 1):
                        p = (bu * m(ep["bolts"]), bw0 + bs / 2 + bw_ * m(ep["bolts"]))
                        prims.cylinder(mb, (p[0], vo, p[1]), (p[0], vo + s * 0.012, p[1]), 0.016, 6, st["rod"])
        lu = lb["lugs"]   # lifting eyes over each rope: a plate across the beam, a tongue to a round end, a boss eye
        cr, er = m(lu["corner"]), m(lu["endR"])
        ce = bw0 - m(lu["tip"]) + er
        top_w = bw0 + bs - 0.02
        hu_ = m(lu["halfU"])
        steps = 6 if lod == 0 else 3
        outline = [(-hu_, bw0), (-hu_, top_w - cr), (-hu_ + cr, top_w), (hu_ - cr, top_w), (hu_, top_w - cr), (hu_, bw0)]
        outline += [(er * math.cos(t_), ce + er * math.sin(t_)) for t_ in (-math.pi * k / steps for k in range(steps + 1))]
        th = m(lu["thick"])
        for s in (-1, 1):
            for dv in lu["v"]:
                vc = s * (hg - m(dv))
                prims.prism(mb, outline, (0.0, vc - th / 2, 0.0), U, W, V, th, st["galv"])
                if lod == 0:
                    ew, bl_ = bw0 - m(lu["eye"]), m(lu["boss"][1])
                    prims.cylinder(mb, (0.0, vc - bl_ / 2, ew), (0.0, vc + bl_ / 2, ew), m(lu["boss"][0]) / 2, 10, st["galv"])
                    for e in (-1, 1):
                        prims.cylinder(mb, (0.0, vc + e * bl_ / 2, ew), (0.0, vc + e * (bl_ / 2 + 0.002), ew), m(lu["bore"]) / 2, 8, st["rubber"])
            rl = lb["rail"]   # the hoist bar under the beam on V standoffs
            rw = bw0 - m(rl["below"])
            v0, v1 = sorted((s * m(rl["v"][0]), s * m(rl["v"][1])))
            prims.cylinder(mb, (0.0, v0, rw), (0.0, v1, rw), m(rl["dia"]) / 2, 6 if lod == 0 else 4, st["galv"])
            for sv in rl["standoffs"]:
                vs = s * m(sv)
                tri = [(-m(rl["halfU"]), bw0), (m(rl["halfU"]), bw0), (0.012, rw), (-0.012, rw)]
                prims.prism(mb, tri, (0.0, vs - m(rl["thick"]) / 2, 0.0), U, W, V, m(rl["thick"]), st["galv"])

    # walkways: on trains of ten, one panel per rocker riding it
    n_row = cfg.get("support", 0) + cfg.get("hold", 0)
    decks = {}
    if n_row >= wk["minSheaves"] and lod <= 2:
        for side in (-1, 1):
            yoke_w = pin_w + (tr["yoke"]["holdLift"] if mode == "hold" else 0.0)
            decks[side] = walkway(mb, a, rockers[side], tops[side], side * hg, mode, asm, wk, lod, st, tr["yoke"]["x"][1], yoke_w)

    # the two caged platforms on the crossarm's +u face, reaching out toward the sheaves (owner, Gate 1 v6): to just
    # inboard of the train's yoke, or of the walkway where there is one; the ladder on up to the beam, side ladders from
    # the platforms' outer ends down to the walkways
    if lod <= 1:
        pu0, pu1 = m(pf["u"][0]), m(pf["u"][1])
        dw = m(pf["deckW"])
        d0 = dw - m(pf["deck"])
        for s in (-1, 1):
            far = m(pf["reachWalkway"]) if s in decks else hg - m(pf["reach"])
            v0, v1 = sorted((s * m(pf["v"][0]), s * far))
            parts.deck(mb, (pu0, v0, d0), (pu1, v1, dw), lod, st["grating"], channels=False)
            n_c = max(2, math.ceil((far - m(pf["v"][0])) / 0.6) + 1)
            for k in range(n_c):   # cantilevers from the crossarm
                vk = s * (m(pf["v"][0]) + 0.06 + (far - m(pf["v"][0]) - 0.12) * k / (n_c - 1))
                prims.box(mb, (cw, vk - 0.056, d0 - 0.112), (pu1, vk + 0.056, d0), st["galv"])
            guard = [Vector((pu1, s * m(pf["v"][0]), dw)), Vector((pu1, s * far, dw))]   # the +u face (review T1 verify)
            parts.railing(mb, guard, m(pf["railing"]), lod, st["galv"], post_r=m(pf["post"]))
            ue0 = pu0 if s not in decks else m(hw["sideLadder"]["u"]) + m(hw["sideLadder"]["width"]) / 2 + 0.03
            parts.railing(mb, [Vector((ue0, s * far, dw)), Vector((pu1, s * far, dw))], m(pf["railing"]), lod, st["galv"],
                          post_r=m(pf["post"]))   # the end rail, open at the side ladder
            if s in decks:
                side_ladder(mb, hw["sideLadder"], s, d0, decks[s], lod, st, v=far + m(hw["sideLadder"]["off"]))
    if lod <= 2:   # the mast ladder up past the crossarm, then leaning in to the lifting beam (the reference), clamped
        tower.ladder_segment(mb, t, 0.0, cb1, lod, st)
        la, ml = t["ladder"], hw["mastLadder"]
        u_l = la["side"] * (r + m(la["standoff"]))
        if lod <= 1:
            u_t = la["side"] * (bs / 2 + 0.06)
            w_t = bw0 + bs + m(ml["past"])
            parts.ladder(mb, Vector((u_l, 0.0, cb1)), Vector((u_t, 0.0, w_t)), 2 * m(la["halfWidth"]), lod, st["galv"],
                         handrails=False, rung_style=st["galv"], stringer=m(ml["stringer"]))
            cl = ml["clamp"]
            for sv in (-1, 1):
                vc = sv * m(la["halfWidth"])
                u0_, u1_ = sorted((la["side"] * bs / 2, u_t))
                prims.box(mb, (u0_, vc - 0.01, bw0 + bs / 2 - m(cl[1]) / 2), (u1_, vc + 0.01, bw0 + bs / 2 + m(cl[1]) / 2), st["galv"])
        npl = hw["numberPlate"]   # a light plate on the uphill (-u) face, clear of the ladder on the other
        bu = -cw - m(npl["standoff"])
        wc = (cb0 + cb1) / 2
        h2 = m(npl["height"]) / 2
        if lod <= 1:
            prims.box(mb, (bu - m(npl["thick"]), -m(npl["halfV"]), wc - h2), (bu, m(npl["halfV"]), wc + h2), st["white"])
        a.sockets["number_plate"] = (bu - m(npl["thick"]), 0.0, wc)

    a.body = mb
    a.sockets.update({"mast_top": (0.0, 0.0, 0.0), "rope_left": (0.0, -hg, rope), "rope_right": (0.0, hg, rope)})
    if "number_plate" not in a.sockets:
        a.sockets["number_plate"] = (-cw - m(hw["numberPlate"]["standoff"] + hw["numberPlate"]["thick"]), 0.0, (cb0 + cb1) / 2)
    n_sheaves = cfg.get("support", 0) + cfg.get("hold", 0) + 2 * cfg.get("combo", 0)
    a.dims.update({"rope": round(rope * 1000), "crossarmTop": round(cb1 * 1000), "gauge": c["lineGauge"],
                   "sheaves": 2 * n_sheaves})
    return a


def walkway(mb, a, rockers, top, v_rope, mode, asm, wk, lod, st, yoke_x, yoke_w):
    """One rope's walkway, as the reference: one stringer along the train with stepped treads on cross-bearers, carried
    by a single hanger post and beam at the middle, here from the train's main yoke (owner, Gate 1 v4: no supports
    off the rockers), so the whole walkway turns with the train. Railed on the inner side, open over the middle (the
    hanger, the side ladder). Returns its deck line [(u0, w0, u1, w1)] at rest, for the side ladder.
    The hanger hangs plumb (owner, Gate 1 v5): the walkway is its own part, hanger_<side>, pinned to the yoke at the
    beam's end and marked "plumb", so the game turns it back level whatever the train does (game task)."""
    inb = -1.0 if v_rope > 0 else 1.0

    def vx(x):
        return v_rope + inb * x
    rho = asm["sheave"]["dia"] / 2 + asm["ropeR"]
    pts = sorted(((p - rk["n"] * rho) for rk in rockers for p in rk["axles"]), key=lambda p: p.x)
    line = [(p.x, p.z - m(wk["below"])) for p in pts]   # the deck: the rope's line over the sheaves, this far below it

    def at(u):
        if u <= line[0][0]:
            (u0_, w0), (u1_, w1) = line[0], line[1]
        elif u >= line[-1][0]:
            (u0_, w0), (u1_, w1) = line[-2], line[-1]
        else:
            for (u0_, w0), (u1_, w1) in zip(line, line[1:]):
                if u0_ <= u <= u1_:
                    break
        return w0 + (w1 - w0) * (u - u0_) / ((u1_ - u0_) or 1.0)
    u0, u1 = line[0][0] - m(wk["past"]), line[-1][0] + m(wk["past"])
    x_in, x_out = m(wk["inner"]), m(wk["outer"])
    tt = m(wk["tread"]["thick"])
    sw, sd = (m(x) for x in wk["stringer"])
    va, vb = sorted((vx(x_in), vx(x_out)))
    hg_ = wk["hanger"]
    uh = m(hg_["u"])   # clear of the end block's cheeks and the side ladder
    hang = "hanger_" + top.split("_", 1)[1]
    a.pivots[hang] = {"pos": (uh, v_rope, yoke_w), "axis": (0.0, 1.0, 0.0), "parent": top, "plumb": True}
    if lod <= 0:   # the pin through the yoke's inboard web and the beam's end
        with mb.grouped(hang + "#pin"):
            prims.cylinder(mb, (uh, vx(yoke_x - 0.05), yoke_w), (uh, vx(yoke_x + 0.06), yoke_w), 0.04, 8, st["rod"])
    with mb.grouped(hang if lod <= 1 else top):
        if lod >= 2:   # one slab each way from the middle
            for ua, ub in ((u0, 0.0), (0.0, u1)):
                pa = Vector(((ua + ub) / 2, (va + vb) / 2, (at(ua) + at(ub)) / 2 - 0.03))
                d = Vector((ub - ua, 0.0, at(ub) - at(ua))).normalized()
                prims.obox(mb, pa, (d, V, Vector((-d.z, 0.0, d.x))), ((ub - ua) / 2 / d.x, (vb - va) / 2, 0.02), st["galv"])
                vr = vx(x_in)   # the inner rail, so the far walkway reads as a walkway, not a dark bar (Gate 2)
                pr = Vector(((ua + ub) / 2, vr, (at(ua) + at(ub)) / 2 + m(wk["railing"]["height"]) - 0.02))
                prims.obox(mb, pr, (d, V, Vector((-d.z, 0.0, d.x))), ((ub - ua) / 2 / d.x, 0.02, 0.02), st["galv"])
            return [(u0, at(u0), u1, at(u1))]
        pitch = m(wk["pitch"]) * (1 if lod == 0 else 2)
        tu = m(wk["tread"]["u"]) * (1 if lod == 0 else 2)
        n = max(2, round((u1 - u0 - tu) / pitch) + 1)
        us = [u0 + tu / 2 + (u1 - u0 - tu) * k / (n - 1) for k in range(n)]
        vs0, vs1 = sorted((vx(x_in), vx(x_in + sw)))
        for u in us:   # level treads stepping with the rope, each on a cross-bearer from the stringer
            w = at(u)
            parts.deck(mb, (u - tu / 2, va, w - tt), (u + tu / 2, vb, w), lod, st["grating"], channels=False)
            if lod == 0:
                prims.box(mb, (u - 0.02, min(va, vs0), w - tt - 0.05), (u + 0.02, max(vb, vs1), w - tt), st["galv"])
        for ua, ub in zip([u0] + us, us + [u1]):   # the stringer, under the treads' inner ends
            prims.beam(mb, (ua, (vs0 + vs1) / 2, at(ua) - tt - sd / 2), (ub, (vs0 + vs1) / 2, at(ub) - tt - sd / 2), sw, sd, st["galv"])
        pu, pv = m(hg_["post"][0]) / 2, m(hg_["post"][1])   # the hanger: a box post on a flared saddle, a beam to the yoke
        x_p = x_in + sw / 2
        w_foot = at(uh) - tt - sd
        w_top = yoke_w + m(hg_["beam"]) / 2
        p0, p1 = sorted((vx(x_p - pv / 2), vx(x_p + pv / 2)))
        prims.box(mb, (uh - pu, p0, at(uh) - tt), (uh + pu, p1, w_top), st["galv"])
        if lod == 0:
            sad = [(-pu - 0.12, 0.0), (pu + 0.12, 0.0), (pu, 0.12), (-pu, 0.12)]
            prims.prism(mb, sad, (uh, vx(x_in - 0.005), w_foot + sd - 0.01), U, W, V * inb, sw + 0.01, st["galv"])
        b0, b1 = sorted((vx(yoke_x + 0.005), vx(x_p - pv / 2)))
        hb = m(hg_["beam"]) / 2
        prims.box(mb, (uh - hb, b0, yoke_w - hb), (uh + hb, b1, yoke_w + hb), st["galv"])
        rl = wk["railing"]   # the inner railing in flat bar, open over the middle
        bar_w, bar_h = (m(x) for x in rl["bar"])
        pw_, pd_ = (m(x) for x in rl["post"])
        vr = vx(x_in - pw_ / 2)
        o0, o1 = (m(x) for x in hg_["open"])
        for ua, ub in ((u0 + 0.03, o0), (o1, u1 - 0.03)):
            k_n = max(1, round((ub - ua) / 1.0))
            for k in range(k_n + 1):
                u = ua + (ub - ua) * k / k_n
                if lod == 0 or k in (0, k_n):
                    prims.box(mb, (u - pd_ / 2, vr - pw_ / 2, at(u)), (u + pd_ / 2, vr + pw_ / 2, at(u) + m(rl["height"])), st["galv"])
            for hh in ((m(rl["height"]), m(rl["knee"])) if lod == 0 else (m(rl["height"]),)):
                prims.beam(mb, (ua, vr, at(ua) + hh - bar_h / 2), (ub, vr, at(ub) + hh - bar_h / 2), bar_w, bar_h, st["galv"])
        for ue in (u0 + 0.03, u1 - 0.03):   # the end cages
            vo = vx(x_out + pw_ / 2)
            prims.box(mb, (ue - pd_ / 2, vo - pw_ / 2, at(ue)), (ue + pd_ / 2, vo + pw_ / 2, at(ue) + m(rl["height"])), st["galv"])
            for hh in (m(rl["height"]), m(rl["knee"])):
                c0, c1 = sorted((vr, vo))
                prims.box(mb, (ue - bar_w / 2, c0, at(ue) + hh - bar_h), (ue + bar_w / 2, c1, at(ue) + hh), st["galv"])
    return [(a, at(a), b, at(b)) for a, b in zip([u0] + us, us + [u1])]


def side_ladder(mb, sl, s, top_w, decks, lod, st, v=None):
    """A side ladder from the platform down to the walkway, its rungs along the line, stopping above the panel deck (the
    panels turn with the train; the ladder is fixed)."""
    u = m(sl["u"])
    w_deck = None
    for u0, w0, u1, w1 in decks:
        if u0 <= u <= u1:
            w_deck = w0 + (w1 - w0) * (u - u0) / ((u1 - u0) or 1.0)
    if w_deck is None:
        return
    bot = w_deck + m(sl["above"])
    v = s * (v if v is not None else m(sl["v"]))
    hwid = m(sl["width"]) / 2
    for e in (-1, 1):   # stringers, angles in the reference
        prims.box(mb, (u + e * hwid - 0.02, v - 0.02, bot), (u + e * hwid + 0.02, v + 0.02, top_w), st["galv"])
    pitch = m(sl["pitch"]) * (1 if lod == 0 else 2)
    w = bot + 0.05
    while w < top_w - 0.1:
        if lod == 0:
            prims.cylinder(mb, (u - hwid, v, w), (u + hwid, v, w), 0.012, 6, st["galv"], caps=(False, False))
        else:
            prims.box(mb, (u - hwid, v - 0.01, w - 0.01), (u + hwid, v + 0.01, w + 0.01), st["galv"])
        w += pitch
