"""Wide-gauge line tower heads (sessellift_d6.json "towerWide"; owner LM3, LM5, LM12), measured on the owner's reference
tower (Gate 0, 2026-10-21) and built from the line gauge, so the one builder makes a head for any gauge (5.6 m for the
D6 now, bigger lifts later). On the kit's straight mast and bases (owner: no taper, the same tubes and foundations):

  cap          a plate on the mast top, sized to our tube with the reference's overhangs
  crossarm     a box across the line, haunched down to the cap, its ends stopping just inboard of each rope
  end blocks   at each crossarm end, carrying the shaft the rope's sheave train hangs from: the train pivots on the shaft,
               so every head type has its pivot at the same height and the rope's height follows the train (round 3:
               the old crossarm ran past the rope and the rope and grips went through it)
  trains       the Sessellift kit's sheave assemblies, unchanged (tower.assembly, tower.heads: s4 s6 s10 b8 d8 c8; c8 is
               the quads' 4 hold-down over 4 support), frames inboard, the grip side clear
  lifting beam above, on two legs leaning out from the crossarm, with clevis lugs over each rope and a hoist rail
  walkways     one per rope, inboard of its train, stepped treads following the rope 700 below it, railed, hung from
               the crossarm and from the lifting beam by rods; two small caged platforms on the crossarm's +u face; the
               mast ladder carried on up to the beam, side ladders from the walkways up to the platforms
  number plate a light plate on the crossarm's +u face

Origin on the mast top; sockets rope_left/rope_right, mast_top, number_plate. Tower frame: the lift frame's axes, +u the
ladder and platform side.
"""
import math

from mathutils import Vector

import tower
from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

KIND = "tower_head_wide"
LODS = 4
SPEC = "sessellift_d6.json"
CHAIR = "d6_chair"   # the chair the build sends past the head (liftkit/fit.py tower)


def m(x):
    return x / 1000.0


def deck_line(asm, pivots, tag, mode, drop):
    """(u, w) of the walkway's deck along a train: the rope over (support) or under (hold) each sheave, `drop` below it."""
    rr = asm["sheave"]["dia"] / 2 + asm["ropeR"]
    pts = sorted((p["pos"][0], p["pos"][2] + (rr if mode == "support" else -rr)) for k, p in pivots.items()
                 if k.startswith(f"sheave_{tag}"))
    return [(u, w - drop) for u, w in pts]


def along(line, u):
    """The line's w at u, extended straight past its ends."""
    if u <= line[0][0]:
        (u0, w0), (u1, w1) = line[0], line[1]
    elif u >= line[-1][0]:
        (u0, w0), (u1, w1) = line[-2], line[-1]
    else:
        for (u0, w0), (u1, w1) in zip(line, line[1:]):
            if u0 <= u <= u1:
                break
    return w0 + (w1 - w0) * (u - u0) / ((u1 - u0) or 1.0)


def build(spec, lod, stage, variant):
    c, t, hw = spec["common"], spec["tower"], spec["towerWide"]
    st = dict(tower.styles(), white=Style("white", snow=0.3))
    cfg = hw["heads"][variant]
    asm = parts.line_spec(spec)
    radius = asm["arcs"][cfg["arc"]]
    a = Asset(f"sessellift_d6_tower_{variant}")
    mb = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    r = m(t["mast"]["dia"]) / 2
    cap, ca, sh, eb = hw["cap"], hw["crossarm"], hw["shaft"], hw["endBlock"]
    lb, wk, pf = hw["liftingBeam"], hw["walkway"], hw["platform"]
    cw = m(ca["width"]) / 2
    cb0, cb1 = m(ca["bottom"]), m(ca["top"])
    end = hg - m(ca["endInset"])
    shaft_w = m(sh["w"])

    # cap plate and the haunched crossarm
    prims.box(mb, (-m(cap["halfU"]), -m(cap["halfV"]), 0.0), (m(cap["halfU"]), m(cap["halfV"]), m(cap["thick"])), st["galv"])
    hn = ca["haunch"]
    sec = [(-end, cb0), (-m(hn["from"]), cb0), (-m(hn["to"]), m(cap["thick"])), (m(hn["to"]), m(cap["thick"])),
           (m(hn["from"]), cb0), (end, cb0), (end, cb1), (-end, cb1)]
    prims.prism(mb, sec, (-cw, 0.0, 0.0), Vector((0, 1, 0)), Vector((0, 0, 1)), Vector((1, 0, 0)), 2 * cw, st["galv"])

    # the lifting beam on its two legs leaning out, kept to LOD3 (round 3: the frame is the head's signature at range)
    bs = m(lb["size"])
    bw0 = m(lb["w"])
    bl = m(lb["halfLength"])
    prims.box(mb, (-bs / 2, -bl, bw0), (bs / 2, bl, bw0 + bs), st["galv"])
    lg = lb["legs"]
    for s in (-1, 1):
        prims.beam(mb, (0.0, s * m(lg["foot"]), cb1), (0.0, s * m(lg["top"]), bw0), m(lg["thick"]), 2 * m(lg["halfU"]), st["galv"],
                   up=Vector((1, 0, 0)))
    if lod <= 1:
        lu = lb["lugs"]
        for s in (-1, 1):   # clevis lugs over each rope
            for dv in lu["v"]:
                v0 = s * (hg - m(dv))
                prims.box(mb, (-m(lu["halfU"]), v0 - m(lu["thick"]) / 2, bw0 - m(lu["drop"])), (m(lu["halfU"]), v0 + m(lu["thick"]) / 2, bw0),
                          st["galv"])
            rl = lb["rail"]   # the hoist rail under the beam
            v0, v1 = sorted((s * m(rl["v"][0]), s * m(rl["v"][1])))
            prims.box(mb, (-m(rl["size"][0]) / 2, v0, bw0 - m(rl["size"][1])), (m(rl["size"][0]) / 2, v1, bw0), st["galv"])

    # each rope's train on the end shaft: a trial finds where the kit's assembly puts its pivot over the rope, so the
    # pivot lands on the shaft (u 0, shaft_w) and the rope's height follows the type
    combo = "combo" in cfg
    mode = None if combo else ("support" if "support" in cfg else "hold")
    pin_du, pin_w = 0.0, None
    if combo:
        rope = shaft_w - m(hw["hangDrop"])
    else:
        pu, pw = parts.line_assembly(MeshBuilder(), Asset("trial"), "t", 0.0, hg, 0.0, asm, cfg[mode], mode, lod, st, radius,
                                     outer_yoke=mode == "support")
        pin_du = -pu
        if mode == "hold":   # a hold-down train's pin is above it: straight on the shaft
            rope = shaft_w - pw
        else:                # a support train hangs below the shaft on a link, the rope hangDrop under it (Gate 0)
            rope = shaft_w - m(hw["hangDrop"])
            pin_w = rope + pw
    X = asm["x"]
    lines = {}
    for side, tag in ((-1, "l"), (1, "r")):
        v_rope = side * hg
        if combo:
            parts.combo_assembly(mb, a, tag, v_rope, rope, asm, cfg["combo"], lod, st, radius)
            row_tag, row_mode = tag + "s", "support"
        else:
            parts.line_assembly(mb, a, tag, pin_du, v_rope, rope, asm, cfg[mode], mode, lod, st, radius,
                                outer_yoke=mode == "support")   # hold-down rows: no outboard plates where grips pass
            row_tag, row_mode = tag, mode
        lines[side] = deck_line(asm, a.pivots, row_tag, row_mode, m(wk["below"]))
        # the end block and the shaft through it to the train's pivot (and on over the rope, as the reference, except a
        # combination's, whose block sits at the rope's height)
        if lod <= 2:
            e0, e1 = sorted((side * (hg - m(eb["v"][0])), side * (hg - m(eb["v"][1]))))
            prims.box(mb, (-m(eb["halfU"]), e0, m(eb["w"][0])), (m(eb["halfU"]), e1, m(eb["w"][1])), st["galv"])
            s_out = hg + m(sh["stub"]) if mode == "support" else hg - X["beam"][0]   # over the rope on support heads
            s0, s1 = sorted((side * (hg - m(eb["v"][0])), side * s_out))
            prims.cylinder(mb, (0.0, s0, shaft_w), (0.0, s1, shaft_w), m(sh["dia"]) / 2, parts.sides(lod, 16, 10, 6), st["galv"])
            low = rope + asm["combo"]["blockHalfW"] if combo else pin_w
            if low is not None:   # the link the train hangs on, inboard of the grips
                va, vb = sorted((side * (hg - X["beam"][1]), side * (hg - X["beam"][0])))
                prims.box(mb, (-0.09, va, low), (0.09, vb, shaft_w), st["galv"])

    # walkways: inboard of each train, 700 below the rope, stepped treads with a railing, hung from the crossarm and the
    # lifting beam
    for side in (-1, 1):
        line = lines[side]
        u0, u1 = line[0][0] - m(wk["past"]), line[-1][0] + m(wk["past"])
        vi, vo = sorted((side * (hg - m(wk["inner"])), side * (hg - m(wk["outer"]))))
        v_in = side * (hg - m(wk["inner"]))
        if lod >= 2:   # one slab each way from the middle
            um = (u0 + u1) / 2
            for ua, ub in ((u0, um), (um, u1)):
                pa = Vector(((ua + ub) / 2, (vi + vo) / 2, (along(line, ua) + along(line, ub)) / 2 - 0.03))
                d = Vector((ub - ua, 0.0, along(line, ub) - along(line, ua))).normalized()
                prims.obox(mb, pa, (d, Vector((0, 1, 0)), d.cross(Vector((0, 1, 0))).normalized()), ((ub - ua) / 2 / d.x, (vo - vi) / 2, 0.03),
                           st["grating"])
            continue
        pitch = m(wk["pitch"]) * (1 if lod == 0 else 2)
        n = max(2, int((u1 - u0) / pitch) + 1)
        tread = m(wk["tread"]) * (1 if lod == 0 else 2)
        for k in range(n):
            u = u0 + (u1 - u0) * k / (n - 1)
            w = along(line, u)
            parts.deck(mb, (u - tread / 2, vi, w - 0.04), (u + tread / 2, vo, w), lod, st["grating"], channels=False)
        sw, sd = m(wk["stringer"][0]), m(wk["stringer"][1])
        for vv in (vi - sw / 2, vo + sw / 2):   # stringers along the steps
            pts = [(u0 + (u1 - u0) * k / (n - 1)) for k in range(n)]
            for ua, ub in zip(pts, pts[1:]):
                prims.beam(mb, (ua, vv, along(line, ua) - 0.04 - sd / 2), (ub, vv, along(line, ub) - 0.04 - sd / 2), sw, sd, st["galv"])
        rail = [Vector((u, v_in, along(line, u))) for u in (u0, (u0 + u1) / 2, u1)]
        parts.railing(mb, rail, m(wk["railing"]), lod, st["galv"])
        for ue in (u0, u1):   # the end gates
            parts.railing(mb, [Vector((ue, vi, along(line, ue))), Vector((ue, vo, along(line, ue)))], m(wk["railing"]), lod, st["galv"])
        hp = wk["plate"]   # the hanger plate from the crossarm down to the walkway
        p0, p1 = sorted((side * (hg - m(hp["v"][0])), side * (hg - m(hp["v"][1]))))
        top_w = along(line, 0.0)
        if top_w < cb0:
            prims.box(mb, (-m(hp["halfU"]), p0, top_w), (m(hp["halfU"]), p1, cb0), st["galv"])
        if lod == 0:   # rods from the walkway's ends up to the lifting beam
            hv = side * m(wk["hanger"]["v"])
            for ue in (u0, u1):
                prims.cylinder(mb, (ue, hv, along(line, ue)), (0.0, hv, bw0), m(wk["hanger"]["dia"]) / 2, 6, st["galv"], caps=(False, False))

    # the two small caged platforms on the crossarm's +u face, the ladder on up to the beam, side ladders to the walkways
    if lod <= 1:
        pu0, pu1 = m(pf["u"][0]), m(pf["u"][1])
        dw = m(pf["deckW"])
        for s in (-1, 1):
            v0, v1 = sorted((s * m(pf["v"][0]), s * m(pf["v"][1])))
            parts.deck(mb, (pu0, v0, dw - 0.04), (pu1, v1, dw), lod, st["grating"])
            vb = s * (m(pf["v"][0]) + m(pf["v"][1])) / 2
            prims.box(mb, (cw, vb - 0.056, dw - 0.16), (pu1, vb + 0.056, dw - 0.04), st["galv"])
            cage = [Vector((pu0, s * m(pf["v"][0]), dw)), Vector((pu1, s * m(pf["v"][0]), dw)), Vector((pu1, s * m(pf["v"][1]), dw)),
                    Vector((pu0, s * m(pf["v"][1]), dw))]
            parts.railing(mb, cage, m(pf["railing"]), lod, st["galv"])
            sl = hw["sideLadder"]
            foot = along(lines[s], m(sl["u"]))
            p0 = Vector((m(sl["u"]), s * m(sl["v"]), foot))
            parts.ladder(mb, p0, Vector((m(sl["u"]), s * m(sl["v"]), dw)), m(sl["width"]), lod, st["galv"], handrails=False,
                         rung_style=st["galv"])
    if lod <= 2:
        tower.ladder_segment(mb, t, 0.0, bw0 + bs, lod, st)
        npl = hw["numberPlate"]   # a light plate on the +u face (round 3: the photographed number is on that face)
        bu = cw + m(npl["standoff"])
        wc = (cb0 + cb1) / 2
        h2 = m(npl["height"]) / 2
        if lod <= 1:
            prims.box(mb, (bu, -m(npl["halfV"]), wc - h2), (bu + m(npl["thick"]), m(npl["halfV"]), wc + h2), st["white"])
        a.sockets["number_plate"] = (bu + m(npl["thick"]), 0.0, wc)

    a.body = mb
    a.sockets.update({"mast_top": (0.0, 0.0, 0.0), "rope_left": (0.0, -hg, rope), "rope_right": (0.0, hg, rope)})
    if "number_plate" not in a.sockets:
        a.sockets["number_plate"] = (cw + m(hw["numberPlate"]["standoff"] + hw["numberPlate"]["thick"]), 0.0, (cb0 + cb1) / 2)
    n_sheaves = cfg.get("support", 0) + cfg.get("hold", 0) + 2 * cfg.get("combo", 0)
    a.dims.update({"rope": round(rope * 1000), "crossarmTop": round(cb1 * 1000), "gauge": c["lineGauge"],
                   "sheaves": 2 * n_sheaves})
    return a
