"""Sessellift FGQ-4 line tower, a modular kit (sessellift_fgq4.json "tower", decision LP11):

  base   concrete footing below grade and a square pier; the steel base plate with anchor nuts, gussets and the
         mast's foot. Origin at grade on the mast centreline; socket mast_foot on the plate.
  mast   one 1 m section of the round mast with its ladder (stringers, 4 rungs, a standoff). Origin at the
         section's foot; socket top. The game stacks sections to reach the tower's height.
  head   a cap on the mast top carrying the drive terminal's entry head (liftkit/heads.py: the chamfered
         crossbeam, the lifting portal with its bolted beam, lug plates and J-handrails, and the entry platform,
         narrowed inside the ropes with a hatch for the ladder), a sheave assembly on each rope attached directly
         to the crossbeam's end (see head()), the ladder's top with grab rails, and a number plate. Origin on the
         mast top; sockets rope_left/rope_right where the rope passes, mast_top, number_plate.

Head types (the "heads" table), after the owner's reference model (liftkit.parts.line_assembly, combo_assembly):
general support towers with 4 or 6 sheaves per rope on a flat arc (s4 s6), breakover with 8 on the reference
arc (b8), hold-down with 8, the same flipped (d8), and a combination of 4 hold-down sheaves directly over 4 support
sheaves on triangular frames (c8). The first and last sheave of each row are red, the rest galvanised. Assemblies
pin across the line, so they can follow the rope's angle; each sheave is a moving part on its axle at LOD0.

Tower frame: the lift frame's axes, with +u toward the platform side (the drive frame's +u, toward the return
terminal), v right, w up; ropes at v = +-lineGauge/2.
"""
import math

from mathutils import Vector

from liftkit import heads, parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

LODS = 4


def m(x):
    return x / 1000.0


def styles():
    """Towers are galvanised throughout (owner): structure, lug plates and ladder rungs alike; sheave faces red or
    galvanised by liftkit.parts.row_face; the number plate is black (the number itself comes later)."""
    galv = Style("galvanised", snow=0.5)
    return {
        "concrete": Style("concrete", snow=0.9),
        "steel": galv,          # liftkit.heads and liftkit.parts draw structure with "steel"
        "galv": galv,
        "grating": Style("grating", snow=0.8),
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "rod": Style("machined"),
        "yellow": galv,         # the drive head's yellow lug plates are galvanised on towers
        "plate": Style("sign_black", snow=0.3),
    }


def kind_of(variant):
    return "tower_" + ("head" if variant not in ("mast", "base") else variant)


def build(spec, lod, stage, variant):
    t = spec["tower"]
    st = styles()
    if variant == "base":
        return base(t, lod, st)
    if variant == "mast":
        return mast_section(t, lod, st)
    return head(spec, t, variant, lod, st)


# -- base ------------------------------------------------------------------------------------------
def base(t, lod, st):
    b = t["base"]
    a = Asset("sessellift_fgq4_tower_base")
    mb = MeshBuilder()
    fo, pi, pl = b["footing"], b["pier"], b["plate"]
    fh = m(fo["half"])
    prims.box(mb, (-fh, -fh, m(fo["bottom"])), (fh, fh, m(fo["top"])), st["concrete"])
    ph, top = m(pi["half"]), m(pi["top"])
    if lod <= 1:   # pier with chamfered vertical edges
        c = m(pi["chamfer"])
        poly = [(ph - c, -ph), (ph, -ph + c), (ph, ph - c), (ph - c, ph), (-ph + c, ph), (-ph, ph - c), (-ph, -ph + c), (-ph + c, -ph)]
        prims.prism(mb, poly, (0.0, 0.0, m(fo["top"])), U, V, W, top - m(fo["top"]), st["concrete"])
    else:
        prims.box(mb, (-ph, -ph, m(fo["top"])), (ph, ph, top), st["concrete"])
    plh, plt = m(pl["half"]), m(pl["thick"])
    prims.box(mb, (-plh, -plh, top), (plh, plh, top + plt), st["galv"])
    foot = top + plt
    r = m(t["mast"]["dia"]) / 2
    if lod <= 2:   # the mast's foot up to the first section
        prims.cylinder(mb, (0, 0, foot), (0, 0, foot + 0.02), r, parts.sides(lod, *t["mast"]["sides"]), st["galv"], caps=(False, True))
    if lod <= 1:
        bo, gu = b["bolts"], b["gussets"]
        for i in range(bo["count"]):   # anchor nuts on the plate
            ang = 2 * math.pi * (i + 0.5) / bo["count"]
            p = Vector((math.cos(ang), math.sin(ang), 0)) * m(bo["radius"])
            prims.cylinder(mb, p + W * foot, p + W * (foot + m(bo["height"])), m(bo["dia"]) / 2, 6, st["rod"], caps=(False, True))
        for i in range(gu["count"]):   # triangular gussets from the plate up the mast
            ang = 2 * math.pi * i / gu["count"]
            d = Vector((math.cos(ang), math.sin(ang), 0))
            side = Vector((-d.y, d.x, 0))
            tri = [(r - 0.01, 0.0), (r + m(gu["reach"]), 0.0), (r - 0.01, m(gu["height"]))]
            prims.prism(mb, tri, tuple(W * foot - side * m(gu["thick"]) / 2), d, W, side, m(gu["thick"]), st["galv"])
    a.body = mb
    a.sockets = {"mast_foot": (0.0, 0.0, foot), "foundation_base": (0.0, 0.0, m(fo["bottom"]))}
    a.dims.update({"mastFoot": round(foot * 1000), "pierHalf": pi["half"]})
    return a


# -- mast section ----------------------------------------------------------------------------------
def ladder_segment(mb, t, w0, w1, lod, st, rungs=True):
    """The mast ladder between w0 and w1: two flat-bar stringers on the downhill side, rungs on the 250 mm pitch
    (continuous across sections), and one standoff to the mast per section."""
    la = t["ladder"]
    r = m(t["mast"]["dia"]) / 2
    u_l = la["side"] * (r + m(la["standoff"]))
    hw = m(la["halfWidth"])
    sw, sd = m(la["stringer"][0]), m(la["stringer"][1])
    for s in (-1, 1):
        prims.box(mb, (u_l - sd / 2, s * hw - sw / 2, w0), (u_l + sd / 2, s * hw + sw / 2, w1), st["galv"])
    if rungs and lod <= 1:
        pitch = m(la["rungs"]) * (1 if lod == 0 else 2)
        k = math.ceil(w0 / pitch - 1e-6)
        while k * pitch + pitch / 2 < w1:
            w = k * pitch + pitch / 2
            if w > w0:
                prims.cylinder(mb, (u_l, -hw, w), (u_l, hw, w), 0.013, 6 if lod == 0 else 4, st["yellow"], caps=(False, False))
            k += 1
    if lod <= 1:   # standoff: two arms from the mast to the stringers
        w = (w0 + w1) / 2
        for s in (-1, 1):
            prims.box(mb, (u_l, s * hw - 0.01, w - 0.03), (la["side"] * (r - 0.01), s * hw * 0.5 + 0.01, w + 0.03), st["galv"])


def mast_section(t, lod, st):
    a = Asset("sessellift_fgq4_tower_mast")
    mb = MeshBuilder()
    h = m(t["mast"]["section"])
    r = m(t["mast"]["dia"]) / 2
    prims.cylinder(mb, (0, 0, 0), (0, 0, h), r, parts.sides(lod, *t["mast"]["sides"]), st["galv"], caps=(False, False))
    if lod <= 2:
        ladder_segment(mb, t, 0.0, h, lod, st)
    a.body = mb
    a.sockets = {"top": (0.0, 0.0, h)}
    a.dims.update({"section": t["mast"]["section"], "mastDia": t["mast"]["dia"]})
    return a


# -- head ------------------------------------------------------------------------------------------
def head(spec, t, variant, lod, st):
    """The drive terminal's entry head on a mast cap (unchanged from the approved head), with a sheave assembly on each
    rope hung from below the crossbeam end, the connection the owner approved on the return terminal's integrated
    tower: two lug plates under the crossbeam end carry the pin of the assembly's equaliser (a combination's pivot
    block), the pin the same distance below the crossbeam as there ("lugs.hang"). The rope passes the head where
    that puts it, so its height depends on the head type: support heads carry it level with the crossbeam, the
    hold-down head 0.69 m below it, as on the return."""
    c, d = spec["common"], spec["drive"]
    hd = t["head"]
    cfg = t["heads"][variant]
    asm = parts.line_spec(spec)
    radius = asm["arcs"][cfg["arc"]]
    a = Asset(f"sessellift_fgq4_tower_{variant}")
    mb = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    r = m(t["mast"]["dia"]) / 2
    cb, cap = d["crossbeam"], hd["cap"]
    ct = m(cap["thick"])
    off = (-m(hd["entryU"]), 0.0, ct - m(cb["bottom"]))   # the drive head, its crossbeam centred on the mast, on the cap
    ou, _, ow = off
    cb0, cb1, cv = m(cb["bottom"]) + ow, m(cb["top"]) + ow, m(cb["v"]) / 2

    # mast cap: a square plate on the mast top, gussets under it
    ch = m(cap["half"])
    prims.box(mb, (-ch, -ch, 0.0), (ch, ch, ct), st["galv"])
    if lod <= 1:
        gu = cap["gusset"]
        for i in range(4):
            ang = math.pi / 2 * i
            dvec = Vector((math.cos(ang), math.sin(ang), 0))
            side = Vector((-dvec.y, dvec.x, 0))
            tri = [(r - 0.01, 0.0), (r + m(gu["reach"]), 0.0), (r - 0.01, -m(gu["height"]))]
            prims.prism(mb, tri, tuple(-side * m(gu["thick"]) / 2), dvec, W, side, m(gu["thick"]), st["galv"])

    # the drive's entry head: crossbeam, portal, platform (narrowed inside the ropes, with a hatch for the ladder)
    heads.crossbeam(mb, cb, lod, st, off)
    heads.portal(mb, d["portal"], lod, st, off)
    heads.entry_platform(mb, d["entryPlatform"], cb, lod, st, off, half_v=m(hd["platformHalfV"]), hatch=(-m(hd["hatch"]), m(hd["hatch"])))

    # where the rope passes: every assembly's pin hangs "hang" below the crossbeam; place a trial assembly with the
    # rope at 0 to find how far its pin sits from the rope, and shift
    X = asm["x"]
    lg = hd["lugs"]
    pin_target = cb0 - m(lg["hang"])
    if "combo" in cfg:
        rope = pin_target                                 # a combination's pivot is at rope level
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
        w_lo, w_hi = pin_w - m(lg["past"]), cb0        # from under the crossbeam end down past the pin
        # lug plates either side of the equaliser (or block) on the crossbeam end, and the pin through them
        b_in, b_out = v_rope - side * X["beam"][1], v_rope - side * X["beam"][0]
        lt = m(lg["thick"])
        lugs = [sorted((b_in, b_in - side * lt))]
        if "combo" not in cfg and cfg[mode] > 4:   # a single train's or a combination's cross tube takes the outer side
            lugs.append(sorted((b_out, b_out + side * lt)))
        if lod <= 2:
            for va, vb in lugs:
                prims.box(mb, (pin_u - m(lg["halfU"]), va, w_lo), (pin_u + m(lg["halfU"]), vb, w_hi), st["galv"])
        if lod <= 1:
            p0, p1 = b_in - side * (lt + 0.012), b_out + side * (lt + 0.012)
            prims.cylinder(mb, (pin_u, p0, pin_w), (pin_u, p1, pin_w), asm["mainPin"] / 2, 12 if lod == 0 else 8, st["rod"])

    # the ladder's last stretch, from the mast top up through the platform hatch, with grab rails above the deck
    deck = m(d["entryPlatform"]["deck"]) + ow
    if lod <= 2:
        ladder_segment(mb, t, 0.0, deck, lod, st)
        if lod <= 1:
            la = t["ladder"]
            u_l = la["side"] * (r + m(la["standoff"]))
            for s in (-1, 1):
                p0 = Vector((u_l, s * m(la["halfWidth"]), deck))
                prims.cylinder(mb, p0, p0 + W * m(hd["grabRail"]), 0.021, 6 if lod == 0 else 4, st["galv"], caps=(False, True))
    if lod <= 1:   # number plate on the portal beam, facing the platform side (riders come up that way), stood off
        npl, bm = hd["numberPlate"], d["portal"]["beam"]   # the beam face past the through-bolt heads
        bu = m(bm["uTo"]) + ou + m(npl["standoff"])
        wc = (m(bm["bottom"]) + m(bm["top"])) / 2 + ow
        h2 = m(npl["height"]) / 2
        prims.box(mb, (bu, -m(npl["halfV"]), wc - h2), (bu + m(npl["thick"]), m(npl["halfV"]), wc + h2), st["plate"])
        a.sockets["number_plate"] = (bu + m(npl["thick"]), 0.0, wc)

    a.body = mb
    a.sockets.update({"mast_top": (0.0, 0.0, 0.0), "rope_left": (0.0, -hg, rope), "rope_right": (0.0, hg, rope)})
    n_sheaves = cfg.get("support", 0) + cfg.get("hold", 0) + 2 * cfg.get("combo", 0)
    a.dims.update({"rope": round(rope * 1000), "crossbeamTop": round(cb1 * 1000),
                   "portalTop": round((m(d["portal"]["beam"]["top"]) + ow) * 1000), "sheaves": 2 * n_sheaves})
    return a
