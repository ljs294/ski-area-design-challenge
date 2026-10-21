"""Wide-gauge line tower heads (sessellift_d6.json "towerWide"; owner LM3, LM5), in the photographed style and built
from the line gauge, so the one kit makes a head for any gauge (5.6 m for the D6 now, an eight-seater's later):

  cap          a square plate on the mast top
  crossarm     a box beam across the line, reaching overhang past each rope
  lifting beam above it on two posts, tied to the crossarm by inclined braces: the trapezoid of the photos
  platform     grated, along the crossarm's platform side (+u) between the ropes, with railings and a ladder hatch
  assemblies   the Sessellift kit's sheave assemblies (tower.assembly, tower.heads: s4 s6 b8 d8 c8), each hung on lug
               plates under the crossarm's end with its pin lugs.hang below it, so the rope's height follows the type

The masts and bases are the Sessellift kit's (sessellift_fgq4_tower_mast, _base, the leaned bases). Origin on the mast
top; sockets rope_left/rope_right, mast_top, number_plate. Tower frame: the lift frame's axes, +u the platform side.
"""
from mathutils import Vector

import tower
from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder

KIND = "tower_head_wide"
LODS = 4
SPEC = "sessellift_d6.json"


def m(x):
    return x / 1000.0


def build(spec, lod, stage, variant):
    c, t, hw = spec["common"], spec["tower"], spec["towerWide"]
    st = tower.styles()
    cfg = t["heads"][variant]
    asm = parts.line_spec(spec)
    radius = asm["arcs"][cfg["arc"]]
    a = Asset(f"sessellift_d6_tower_{variant}")
    mb = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    r = m(t["mast"]["dia"]) / 2
    cap, ca, lb, pf, lg = hw["cap"], hw["crossarm"], hw["liftingBeam"], hw["platform"], hw["lugs"]
    ct = m(cap["thick"])
    reach = hg + m(ca["overhang"])
    cw, cd = m(ca["width"]) / 2, m(ca["depth"])
    cb0, cb1 = ct, ct + cd                      # the crossarm's bottom and top

    # mast cap and gussets
    ch = m(cap["half"])
    prims.box(mb, (-ch, -ch, 0.0), (ch, ch, ct), st["galv"])
    if lod <= 1:
        for i in range(4):
            d = Vector(((1, 0, 0), (0, 1, 0), (-1, 0, 0), (0, -1, 0))[i])
            side = Vector((-d.y, d.x, 0.0))
            tri = [(r - 0.01, 0.0), (r + 0.08, 0.0), (r - 0.01, -0.28)]
            prims.prism(mb, tri, tuple(-side * 0.01), d, Vector((0, 0, 1)), side, 0.02, st["galv"])

    # the crossarm, the lifting beam on its posts and the braces between them
    prims.box(mb, (-cw, -reach, cb0), (cw, reach, cb1), st["galv"])
    if lod <= 2:
        lw, ld = m(lb["width"]) / 2, m(lb["depth"])
        top0 = cb1 + m(lb["above"])
        prims.box(mb, (-lw, -reach, top0), (lw, reach, top0 + ld), st["galv"])
        for s in (-1, 1):
            pv = s * m(lb["postV"])
            prims.box(mb, (-lw * 0.8, pv - 0.09, cb1), (lw * 0.8, pv + 0.09, top0), st["galv"])
            prims.beam(mb, (0.0, s * m(lb["braceV"]), cb1), (0.0, s * (reach - 0.1), top0), 0.16, 0.16, st["galv"])

    # the platform along the +u side between the ropes, its railing, and the hatch for the ladder
    if lod <= 2:
        pv = hg - m(pf["inset"])
        u0, u1 = cw + m(pf["gap"]), cw + m(pf["gap"]) + m(pf["depth"])
        deck_w = cb0 + 0.06
        hatch = m(hw["hatch"])
        if lod <= 1:
            for lo, hi in (((u0, -pv), (u1, -hatch)), ((u0, hatch), (u1, pv))):
                parts.deck(mb, (lo[0], lo[1], deck_w - 0.06), (hi[0], hi[1], deck_w), lod, st["grating"])
            for s in (-1, 1):   # brackets from the crossarm out under the deck
                prims.box(mb, (cw, s * pv - 0.05, deck_w - 0.2), (u1, s * pv + 0.05, deck_w - 0.06), st["galv"])
            rail = [Vector((u0, -pv, deck_w)), Vector((u1, -pv, deck_w)), Vector((u1, pv, deck_w)), Vector((u0, pv, deck_w))]
            parts.railing(mb, rail, m(pf["railing"]), lod, st["galv"])
        else:
            prims.box(mb, (u0, -pv, deck_w - 0.06), (u1, pv, deck_w), st["grating"])

    # each rope's assembly on lug plates under the crossarm's end; a trial assembly finds the rope's height
    X = asm["x"]
    pin_target = cb0 - m(lg["hang"])
    if "combo" in cfg:
        rope, mode = pin_target, None
    else:
        mode = "support" if "support" in cfg else "hold"
        _, trial_w = parts.line_assembly(MeshBuilder(), Asset("trial"), "t", 0.0, hg, 0.0, asm, cfg[mode], mode, lod, st, radius)
        rope = pin_target - trial_w
    for side, tag in ((-1, "l"), (1, "r")):
        v_rope = side * hg
        if "combo" in cfg:
            pin_u, pin_w = parts.combo_assembly(mb, a, tag, v_rope, rope, asm, cfg["combo"], lod, st, radius)
        else:
            pin_u, pin_w = parts.line_assembly(mb, a, tag, 0.0, v_rope, rope, asm, cfg[mode], mode, lod, st, radius)
        w_lo, w_hi = pin_w - m(lg["past"]), cb0
        b_in, b_out = v_rope - side * X["beam"][1], v_rope - side * X["beam"][0]
        lt = m(lg["thick"])
        lugs = [sorted((b_in, b_in - side * lt))]
        if "combo" not in cfg and cfg[mode] > 4:
            lugs.append(sorted((b_out, b_out + side * lt)))
        if lod <= 2:
            for va, vb in lugs:
                prims.box(mb, (pin_u - m(lg["halfU"]), va, w_lo), (pin_u + m(lg["halfU"]), vb, w_hi), st["galv"])
        if lod <= 1:
            p0, p1 = b_in - side * (lt + 0.012), b_out + side * (lt + 0.012)
            with mb.grouped("#pin"):
                prims.cylinder(mb, (pin_u, p0, pin_w), (pin_u, p1, pin_w), asm["mainPin"] / 2, 12 if lod == 0 else 8, st["rod"])

    # the ladder's last stretch up through the hatch, with grab rails
    if lod <= 2:
        deck = cb0 + 0.06
        tower.ladder_segment(mb, t, 0.0, deck, lod, st)
        if lod <= 1:
            la = t["ladder"]
            u_l = la["side"] * (r + m(la["standoff"]))
            for s in (-1, 1):
                p0 = Vector((u_l, s * m(la["halfWidth"]), deck))
                prims.cylinder(mb, p0, p0 + Vector((0, 0, m(hw["grabRail"]))), 0.021, 6 if lod == 0 else 4, st["galv"], caps=(False, True))
    if lod <= 1:   # number plate on the crossarm's platform face
        npl = hw["numberPlate"]
        bu = -cw - m(npl["standoff"])
        wc = (cb0 + cb1) / 2
        h2 = m(npl["height"]) / 2
        prims.box(mb, (bu - m(npl["thick"]), -m(npl["halfV"]), wc - h2), (bu, m(npl["halfV"]), wc + h2), st["plate"])
        a.sockets["number_plate"] = (bu - m(npl["thick"]), 0.0, wc)

    a.body = mb
    a.sockets.update({"mast_top": (0.0, 0.0, 0.0), "rope_left": (0.0, -hg, rope), "rope_right": (0.0, hg, rope)})
    n_sheaves = cfg.get("support", 0) + cfg.get("hold", 0) + 2 * cfg.get("combo", 0)
    a.dims.update({"rope": round(rope * 1000), "crossarmTop": round(cb1 * 1000), "gauge": c["lineGauge"],
                   "sheaves": 2 * n_sheaves})
    return a
