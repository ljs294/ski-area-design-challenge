"""Monta T-bar line towers, a modular kit of their own (monta_tbar.json "tower", surface lifts, owner 2026-11-02: all
new, nothing shared with the chairlift towers):

  base   concrete footing below grade, a square pier and the steel base plate with anchor nuts. Origin at grade on the
         mast centreline; socket mast_foot on the plate.
  mast   one 1 m section of the round tube mast with welded step pegs in pairs every 500 mm, so the sections stack
         without a seam. Origin at the section's foot; socket top.
  head   a cap on the mast top, a square crossarm across both ropes, and a sheave train hung under each end (train()),
         with a number plate. Origin on the mast top; sockets rope_left/rope_right where the rope passes, mast_top,
         number_plate.

Head types ("heads"): support with 4 or 6 sheaves a rope (s4, s6), hold-down with 4 (d4), and a combination with 2
hold-down sheaves over 2 support sheaves (c4). The first and last sheave of each row are red, the rest galvanised,
as on the chairlift kit. The trains' plates sit inboard of the sheaves, so grips and recoil boxes pass outboard.

Tower frame: the lift frame's axes, u along the line, v right, w up; ropes at v = +-lineGauge/2.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

LODS = 4
SPEC = "monta_tbar.json"
PREFIX = "monta_tbar_"


def m(x):
    return x / 1000.0


def kind_of(variant):
    return {"mast": "surface_mast", "base": "surface_base"}.get(variant, "surface_tower_head")


def _head_type(variant):
    return variant[len("tower_"):] if variant.startswith("tower_") else variant


def styles():
    """Galvanised throughout, as the chairlift towers (owner rule); red and galvanised sheaves by parts.row_face; a
    black number plate."""
    galv = Style("galvanised", snow=0.5)
    return {
        "concrete": Style("concrete", snow=0.9),
        "galv": galv,
        "red": Style("sheave_red"),
        "rubber": Style("rubber"),
        "rod": Style("machined"),
        "plate": Style("sign_black", snow=0.3),
    }


def build(spec, lod, stage, variant):
    t = spec["tower"]
    st = styles()
    if variant == "base":
        return base(t, lod, st)
    if variant == "mast":
        return mast_section(t, lod, st)
    return head(spec, t, _head_type(variant), lod, st)


# -- base ------------------------------------------------------------------------------------------
def base(t, lod, st):
    b = t["base"]
    a = Asset(PREFIX + "base")
    mb = MeshBuilder()
    fo, pi, pl = b["footing"], b["pier"], b["plate"]
    fh, ph = m(fo["half"]), m(pi["half"])
    if lod <= 2:   # the footing is below grade: left out far away
        prims.box(mb, (-fh, -fh, m(fo["bottom"])), (fh, fh, m(fo["top"])), st["concrete"])
    prims.box(mb, (-ph, -ph, m(fo["top"])), (ph, ph, m(pi["top"])), st["concrete"], skip=("-2",))
    plh, plt = m(pl["half"]), m(pl["thick"])
    top = m(pi["top"])
    if lod <= 1:
        prims.box(mb, (-plh, -plh, top), (plh, plh, top + plt), st["galv"], skip=("-2",))
    foot = top + plt
    r = m(t["mast"]["dia"]) / 2
    if lod <= 1:   # the mast's foot, up to the first section
        prims.cylinder(mb, (0, 0, foot), (0, 0, foot + 0.02), r, parts.sides(lod, *t["mast"]["sides"]), st["galv"], caps=(False, True))
    if lod == 0:   # anchor nuts round the mast
        bo = b["bolts"]
        for i in range(bo["count"]):
            ang = 2 * math.pi * (i + 0.5) / bo["count"]
            p = Vector((math.cos(ang), math.sin(ang), 0.0)) * m(bo["radius"]) + W * foot
            prims.cylinder(mb, p, p + W * m(bo["height"]), m(bo["dia"]) / 2, 6, st["rod"], caps=(False, True))
    a.body = mb
    a.sockets = {"mast_foot": (0.0, 0.0, top + plt), "foundation_base": (0.0, 0.0, m(fo["bottom"]))}
    a.dims.update({"mastFoot": round((top + plt) * 1000), "pierHalf": pi["half"]})
    return a


# -- mast section ----------------------------------------------------------------------------------
def steps(mb, t, w0, w1, lod, st):
    """Step pegs welded to the mast between w0 and w1 on the -u side: a pair, 40 degrees either side of it, every
    `pitch` x 2 (a section holds whole pairs, mirror-symmetric about its middle, so the sections stack without an AO
    band, contract M5)."""
    if lod >= 2:
        return
    sp = t["steps"]
    r = m(t["mast"]["dia"]) / 2
    pitch = 2 * m(sp["pitch"])
    k = math.ceil(w0 / pitch - 1e-6)
    while k * pitch + pitch / 2 < w1 + 1e-9:
        w = k * pitch + pitch / 2
        if w > w0:
            for deg in (140, 220):
                ang = math.radians(deg)
                d = U * math.cos(ang) + V * math.sin(ang)
                prims.cylinder(mb, W * w + d * (r - 0.01), W * w + d * (r + m(sp["reach"])), m(sp["dia"]) / 2,
                               6 if lod == 0 else 4, st["galv"], caps=(False, True))
        k += 1


def mast_section(t, lod, st):
    a = Asset(PREFIX + "mast")
    mb = MeshBuilder()
    h = m(t["mast"]["section"])
    r = m(t["mast"]["dia"]) / 2
    prims.cylinder(mb, (0, 0, 0), (0, 0, h), r, parts.sides(lod, *t["mast"]["sides"]), st["galv"], caps=(False, False))
    steps(mb, t, 0.0, h, lod, st)
    a.body = mb
    a.sockets = {"top": (0.0, 0.0, h)}
    a.dims.update({"section": t["mast"]["section"], "mastDia": t["mast"]["dia"]})
    return a


# -- sheave trains ----------------------------------------------------------------------------------
def sheave(mb, c, s, lod, face):
    """A small line sheave, its axle along v: a grooved rim (one lathe section), and a hub boss; a plain drum far out."""
    R, hw = s["dia"] / 2, s["width"] / 2
    n = parts.sides(lod, 12, 8, 6)
    if lod >= 1:
        prims.cylinder(mb, c - V * hw, c + V * hw, R, n, face)
        return
    g, rg, hr = hw - s["flange"], s["grooveBottom"] / 2, s["recess"] / 2
    rim = [(hr, -hw), (R, -hw), (R, -g), (rg, 0.0), (R, g), (R, hw), (hr, hw)]
    prims.lathe(mb, c, V, rim, n, face.but(cls="sheave_rim"))
    prims.cylinder(mb, c - V * (hw - s["recessDepth"]), c + V * (hw - s["recessDepth"]), hr, n, face)
    prims.cylinder(mb, c - V * (hw + 0.01), c + V * (hw + 0.01), s["hub"] / 2, 6, face.but(cls="machined"))


def axles_u(T, count):
    """Axle positions along u for `count` sheaves: pairs on rockers (pitch), rockers rockerGap apart, centred on 0."""
    us, u = [0.0], 0.0
    for k in range(1, count):
        u += T["pitch"] if k % 2 else T["rockerGap"]
        us.append(u)
    mid = us[-1] / 2
    return [x - mid for x in us]


def row(mb, a, tag, prefix, v_rope, rope_w, count, mode, T, s, lod, st, parent):
    """One row of sheaves on one rope, in pairs on rockers: axles on the bend arc under (support) or over (hold) the
    rope, rocker plates inboard. Returns (the rockers' pins, the side the structure is on in w: -1 below, +1 above).
    Pivots: rocker_<tag><prefix><i> under `parent`, sheave_<tag><prefix><k> under its rocker."""
    R, rr = s["dia"] / 2, T["ropeR"]
    sg = -1.0 if mode == "support" else 1.0          # the axles and structure are below a support row, above a hold-down
    inb = -1.0 if v_rope > 0 else 1.0                # toward the tower's centre
    us = axles_u(T, count)
    axles = [Vector((u, v_rope, rope_w + sg * (R + rr + u * u / (2 * T["arcR"])))) for u in us]
    v_plate = v_rope + inb * (s["width"] / 2 + T["plateGap"])
    pt = T["plateThick"]
    pins = []
    for i in range(count // 2):
        a0, a1 = axles[2 * i], axles[2 * i + 1]
        pin = (a0 + a1) / 2 + W * sg * T["rockerDepth"] / 2
        pin.y = v_plate
        rname = f"rocker_{tag}{prefix}{i + 1}"
        a.pivots[rname] = {"pos": tuple(pin), "axis": (0.0, 1.0, 0.0), "parent": parent, "hinge": True}
        pins.append(pin)
        rocker_tag = rname if lod <= 1 else parent
        with mb.grouped(rocker_tag):
            if lod <= 1:   # the rocker: one plate inboard through both axles and the pin
                hu = (a1.x - a0.x) / 2 + 0.06
                cu, cw = (a0.x + a1.x) / 2, (a0.z + a1.z) / 2 + sg * T["rockerDepth"] / 4
                prims.box(mb, (cu - hu, v_plate - pt / 2, cw - T["rockerDepth"] / 2 - 0.03),
                          (cu + hu, v_plate + pt / 2, cw + T["rockerDepth"] / 2 + 0.03), st["galv"])
            if lod <= 1:   # the rocker's pin, its own piece for the clash sweep
                with mb.grouped(f"{rocker_tag}#pin"):
                    prims.cylinder(mb, pin - V * 0.03, pin + V * 0.03, T["pinDia"] / 2 * 0.7, 8, st["rod"])
        if lod >= 2:   # far out: the pair as one block (LOD2); LOD3 draws the whole train as one (train())
            if lod == 2:
                with mb.grouped(parent):
                    hu, hw = (a1.x - a0.x) / 2 + R, s["width"] / 2
                    prims.box(mb, (a0.x - R, v_rope - hw, min(a0.z, a1.z) - R), (a1.x + R, v_rope + hw, max(a0.z, a1.z) + R),
                              parts.row_face(0, 2, st))
            continue
        for j, ax in enumerate((a0, a1)):
            k = 2 * i + j
            sname = f"sheave_{tag}{prefix}{k + 1}"
            a.pivots[sname] = {"pos": tuple(ax), "axis": (0.0, 1.0, 0.0), "parent": rname}
            sheave_tag = sname if lod == 0 else rocker_tag
            with mb.grouped(sheave_tag):
                sheave(mb, ax, s, lod, parts.row_face(k, count, st))
                if lod <= 1:   # the axle stub from the sheave to the plate
                    with mb.grouped(f"{sheave_tag}#pin"):
                        prims.cylinder(mb, Vector((ax.x, v_rope, ax.z)), Vector((ax.x, v_plate, ax.z)), 0.022, 6, st["rod"])
    if lod >= 3 and not prefix:   # the whole row as one block (a combination's two rows: one block, train())
        with mb.grouped(parent):
            hw = s["width"] / 2
            prims.box(mb, (axles[0].x - R, v_rope - hw, min(p.z for p in axles) - R),
                      (axles[-1].x + R, v_rope + hw, max(p.z for p in axles) + R), parts.row_face(0, 2, st))
    return pins, sg


def train(mb, a, tag, v_rope, rope_w, cfg, T, s, lod, st, arm_w):
    """A sheave train on one rope, hung from the crossarm's underside at arm_w: the rows on a beam pinned at its middle,
    the hanger plate from the arm down to that pin, inboard of everything. A combination (cfg "combo") carries a
    hold-down row over a support row on one block. Pivot train_<tag> on the main pin (the whole train follows the
    rope's angle); at LOD2-3 the whole train is one part under it."""
    inb = -1.0 if v_rope > 0 else 1.0
    v_plate = v_rope + inb * (s["width"] / 2 + T["plateGap"])
    v_beam = v_plate + inb * (T["plateThick"] / 2 + T["beamWidth"] / 2)
    v_hang = v_beam + inb * (T["beamWidth"] / 2 + T["hangerThick"] / 2)
    tname = f"train_{tag}"
    pivot_tag = tname
    R, rr = s["dia"] / 2, T["ropeR"]
    if "combo" in cfg:
        n = cfg["combo"]
        # the block's pin at rope level, between the rows; both rows hang from it
        a.pivots[tname] = {"pos": tuple(Vector((0.0, v_beam, rope_w))), "axis": (0.0, 1.0, 0.0), "hinge": True}
        hold_pins, _ = row(mb, a, tag, "h", v_rope, rope_w, n, "hold", T, s, lod, st, tname)
        sup_pins, _ = row(mb, a, tag, "s", v_rope, rope_w, n, "support", T, s, lod, st, tname)
        reach = max(abs(p.x) for p in hold_pins + sup_pins) + 0.08
        lo = min(p.z for p in sup_pins) - 0.06
        hi = max(p.z for p in hold_pins) + 0.06
        with mb.grouped(pivot_tag):   # the block: one plate through every rocker pin
            if lod <= 2:
                prims.box(mb, (-reach, v_beam - T["beamWidth"] / 2, lo), (reach, v_beam + T["beamWidth"] / 2, hi), st["galv"])
            else:   # far out: both rows as one block
                ext = max(abs(u) for u in axles_u(T, n)) + R
                prims.box(mb, (-ext, v_rope - s["width"] / 2, rope_w - 2 * R - 0.05), (ext, v_rope + s["width"] / 2, rope_w + 2 * R + 0.05),
                          parts.row_face(0, 2, st))
        main = Vector((0.0, v_beam, rope_w))
    else:
        mode = "support" if "support" in cfg else "hold"
        n = cfg[mode]
        sg = -1.0 if mode == "support" else 1.0
        rows_tag = tname
        a.pivots[tname] = {"pos": (0.0, 0.0, 0.0), "axis": (0.0, 1.0, 0.0), "hinge": True}   # placed below
        pins, _ = row(mb, a, tag, "", v_rope, rope_w, n, mode, T, s, lod, st, tname)
        bw = max(p.z for p in pins) if sg > 0 else min(p.z for p in pins)
        beam_w = bw + sg * T["beamDepth"] / 2
        main = Vector((0.0, v_beam, beam_w + sg * T["beamDepth"] / 2))
        a.pivots[tname]["pos"] = tuple(main)
        reach = max(abs(p.x) for p in pins) + 0.07
        with mb.grouped(rows_tag):   # the beam through the rocker pins and up (or down) to the main pin
            if lod <= 2:
                prims.box(mb, (-reach, v_beam - T["beamWidth"] / 2, beam_w - T["beamDepth"] / 2),
                      (reach, v_beam + T["beamWidth"] / 2, beam_w + T["beamDepth"] / 2), st["galv"])
            if lod <= 1:
                prims.box(mb, (-0.09, v_beam - T["beamWidth"] / 2, min(beam_w, main.z) - 0.02),
                          (0.09, v_beam + T["beamWidth"] / 2, max(beam_w, main.z) + 0.06), st["galv"])
    # the hanger plate from the arm down to the main pin (the body), and the pin through it
    hw = T["hangerWidth"] / 2
    if lod <= 2:
        prims.box(mb, (-hw, v_hang - T["hangerThick"] / 2, main.z - 0.08), (hw, v_hang + T["hangerThick"] / 2, arm_w), st["galv"])
    if lod <= 1:
        with mb.grouped("#pin"):
            p0 = Vector((0.0, v_hang + inb * T["hangerThick"], main.z))
            p1 = Vector((0.0, v_plate, main.z))
            prims.cylinder(mb, p0, p1, T["pinDia"] / 2, 10 if lod == 0 else 6, st["rod"])
    return main


def head(spec, t, variant, lod, st):
    c = spec["common"]
    hd = t["head"]
    cfg = t["heads"][variant]
    T = parts.metres(spec["train"])
    T["ropeR"] = c["ropeDiameter"] / 2000.0
    s = parts.metres(c["sheave"])
    a = Asset(PREFIX + "tower_" + variant)
    mb = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    r = m(t["mast"]["dia"]) / 2
    cap, arm = hd["cap"], hd["crossarm"]
    ct, ch = m(cap["thick"]), m(cap["half"])
    sz, hv = m(arm["size"]), m(arm["halfV"])

    # the mast's last 0.3 m into the cap, the cap plate, gussets under it
    prims.cylinder(mb, (0, 0, -0.3), (0, 0, 0.0), r, parts.sides(lod, *t["mast"]["sides"]), st["galv"], caps=(False, False))
    if lod <= 2:
        prims.box(mb, (-ch, -ch, 0.0), (ch, ch, ct), st["galv"])
    if lod <= 1:
        for i in range(4):
            ang = math.pi / 2 * i + math.pi / 4
            d = Vector((math.cos(ang), math.sin(ang), 0))
            side = Vector((-d.y, d.x, 0))
            tri = [(r - 0.01, 0.0), (ch * 1.3, 0.0), (r - 0.01, -0.22)]
            prims.prism(mb, tri, tuple(-side * 0.006), d, W, side, 0.012, st["galv"])
    # the crossarm: a square tube across both ropes, end plates closing it
    arm_lo = ct
    prims.box(mb, (-sz / 2, -hv, arm_lo), (sz / 2, hv, arm_lo + sz), st["galv"])

    mode = "combo" if "combo" in cfg else ("support" if "support" in cfg else "hold")
    rope = arm_lo - m(hd["ropeBelowArm"][mode])
    for side, tag in ((-1, "l"), (1, "r")):
        train(mb, a, tag, side * hg, rope, cfg, T, s, lod, st, arm_lo)

    # step pegs up the last 0.3 m of mast
    steps(mb, t, -0.3, 0.0, lod, st)
    if lod <= 1:   # number plate on the arm's -u face (riders come up from -u at the bottom terminal's side)
        npl = hd["numberPlate"]
        bu = -sz / 2 - m(npl["standoff"])
        wc = arm_lo + sz / 2
        h2 = m(npl["height"]) / 2
        prims.box(mb, (bu - m(npl["thick"]), -m(npl["halfV"]), wc - h2), (bu, m(npl["halfV"]), wc + h2), st["plate"])
        a.sockets["number_plate"] = (bu - m(npl["thick"]), 0.0, wc)

    a.body = mb
    a.sockets.update({"mast_top": (0.0, 0.0, 0.0), "rope_left": (0.0, -hg, rope), "rope_right": (0.0, hg, rope)})
    n_sheaves = cfg.get("support", 0) + cfg.get("hold", 0) + 2 * cfg.get("combo", 0)
    a.dims.update({"rope": round(rope * 1000), "armTop": round((arm_lo + sz) * 1000), "sheaves": 2 * n_sheaves,
                   "ropeBelowMastTop": round(-rope * 1000)})
    return a
