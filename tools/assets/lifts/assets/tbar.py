"""Monta T-bar line towers, a modular kit of their own (monta_tbar.json "tower", surface lifts, owner 2026-11-02: all
new, nothing shared with the chairlift towers; rebuilt to the owner's reference installation, review
findings W1-W24 and decisions D3-D6, D9-D11, kept with the model notes outside the repo).

The signature T-bar lean (D11, Q1): every mast leans `lean` degrees across the line toward the loaded track (+v), so
the ropes stay level and centred on the head while the foundation stands off to the return side, clear of the drag
track. The base and head are built for it; mast sections are built upright and the game tilts them by `lean` about u
(dims "leanDeg"), stacking each on the previous one's top socket along the leaned axis. A tower whose head is N
sections above its base stands with its pier at v = -(mastFoot along the axis + N x section) x sin(lean) from the line
centre (dims "footOffsetPerSection", base dims "footOffset").

  base   concrete footing below grade, a square pier, a grout wedge under the leaned base plate, anchor nuts, and the
         mast's first 2.2 m wrapped in the red tower pad (D5). Origin at grade on the pier's centre; socket mast_foot
         on the stub's top, along the leaned axis.
  mast   one 1 m section of the round mast, upright, with a two-stile ladder on its +u face (rungs mirror-symmetric
         about mid-height, so sections stack without an AO band, contract M5). Origin at the section's foot; socket top.
  head   two levels (W1): a sleeve over the mast top rising in a tall cone to a flange under a deep gusseted box crossarm
         whose heavy end plates carry the trains on end pins (octagonal pin covers); above it the fixed-grip chair
         tower's lifting frame in miniature (owner, 2026-11-03: liftkit.heads.portal on the chair's numbers scaled to
         the gauge): two legs leaning out from foot plates on the arm to a bolted lifting beam, lug plates with eyes over
         each rope, J-handrails, a black number plate on the beam's face toward the bottom station; curled hook brackets
         for the wire on the beam and the cables clipped along it (D9); the chair towers' catwalk beside the arm on the
         ladder's side (owner, 2026-11-03: liftkit.heads.entry_platform, at walking size), its grated deck level with
         the arm's top, a hoop rail and struts, the mast ladder climbing on through its hatch to grab rails.
         Origin on the mast top (on the leaned axis); the frame is level. Sockets mast_top, rope_left, rope_right,
         number_plate, wire_left, wire_right.

Trains (train()): sheaves in pairs on rockers (a cast web inboard, a cheek plate outboard), rockers on a pentagonal
equaliser on the end pin. Support trains stand on the pin with the rope on top; hold-down trains hang inverted from the
same pins, the rope at the arm. The first and last sheave of each row are red (D3). A black catcher tube sweeps out and
down from each end of each train, inside the carrier hanger's D, so a swinging hanger can't reach the sheaves.

Head types ("heads"): support with 4 or 6 sheaves a rope (s4, s6), hold-down with 4 (d4).
Tower frame: the lift frame's axes, u along the line uphill, v right, w up; ropes at v = +-lineGauge/2, the loaded one
at +v.
"""
import math

from mathutils import Vector

from liftkit import frame, heads, parts, prims
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
    """Galvanised throughout, as the chairlift towers (owner rule); red and galvanised sheaves by parts.row_face; the
    red pad, the black number plate (as on the chair towers), black catchers and cables. "steel" and "yellow" are what
    liftkit.heads draws the lifting frame with: galvanised, as on the chair towers."""
    galv = Style("galvanised", snow=0.5)
    return {
        "concrete": Style("concrete", snow=0.9),
        "galv": galv,
        "steel": galv,
        "yellow": galv,
        "sign": Style("sign_black", snow=0.3),
        "grating": Style("grating", snow=0.8),
        "red": Style("sheave_red"),
        "pad": Style("sheave_red", snow=0.3),
        "rubber": Style("rubber"),
        "rod": Style("machined"),
        "black": Style("trim_dark", snow=0.3),
    }


def leaned(src, lean, about=(0.0, 0.0, 0.0)):
    """A copy of `src` turned by `lean` (radians) about the u axis through `about`: +w tips toward +v."""
    out = MeshBuilder().merge(src)
    c, cs, sn = Vector(about), math.cos(lean), math.sin(lean)
    for i, vb in enumerate(out.verts):
        p = Vector(frame.lift(vb)) - c
        out.verts[i] = frame.b(tuple(c + Vector((p.x, p.y * cs + p.z * sn, -p.y * sn + p.z * cs))))
    return out


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
    lean = math.radians(t["lean"])
    a = Asset(PREFIX + "base")
    mb = MeshBuilder()
    fo, pi, pl, pad = b["footing"], b["pier"], b["plate"], b["pad"]
    fh, ph = m(fo["half"]), m(pi["half"])
    top = m(pi["top"])
    if lod <= 1:   # the footing is below grade: left out far away
        prims.box(mb, (-fh, -fh, m(fo["bottom"])), (fh, fh, m(fo["top"])), st["concrete"])
    prims.box(mb, (-ph, -ph, m(fo["top"])), (ph, ph, top), st["concrete"], skip=("-2",))
    plh, plt = m(pl["half"]), m(pl["thick"])
    lift = plh * math.sin(lean) + 0.002            # the plate's low edge just clears the pier
    o = Vector((0.0, 0.0, top + lift))              # the plate's underside on the mast axis
    if lod <= 1:   # the grout wedge under the leaned plate
        hi = lift + plh * math.sin(lean)
        prims.prism(mb, [(-ph + 0.05, 0.0), (ph - 0.05, 0.0), (ph - 0.05, 0.0005), (-ph + 0.05, hi)],
                    (-plh, 0.0, top), V, W, U, 2 * plh, st["concrete"].but(snow=0.3))
    up = Vector((0.0, math.sin(lean), math.cos(lean)))
    r = m(t["mast"]["dia"]) / 2
    stub = m(b["stub"])
    lm = MeshBuilder()   # the leaned parts, built upright about the plate's underside, then turned
    if lod <= 1:
        prims.box(lm, (-plh, -plh, 0.0), (plh, plh, plt), st["galv"])
    n = parts.sides(lod, *t["mast"]["sides"]) if lod <= 2 else 5
    if lod <= 2:
        prims.cylinder(lm, (0, 0, plt if lod <= 1 else 0.0), (0, 0, stub), r, n, st["galv"], caps=(False, False))
    pr = r + m(pad["thick"])
    cap = m(pad["cap"]) if lod <= 1 else 0.0
    prims.cylinder(lm, (0, 0, m(pad["from"]) if lod <= 2 else 0.0), (0, 0, stub - cap), pr, n, st["pad"], caps=(lod <= 2, lod <= 2 and not cap))
    if cap:   # the pad's light cap ring at its top (R6)
        prims.cylinder(lm, (0, 0, stub - cap), (0, 0, stub), pr + 0.004, n, st["galv"], caps=(False, True))
    if lod == 0:
        bo = b["bolts"]
        for i in range(bo["count"]):
            ang = 2 * math.pi * (i + 0.5) / bo["count"]
            p = Vector((math.cos(ang), math.sin(ang), 0.0)) * m(bo["radius"]) + W * plt
            prims.cylinder(lm, p, p + W * m(bo["height"]), m(bo["dia"]) / 2, 6, st["rod"], caps=(False, True))
    mb.merge(leaned(lm, lean).translated(tuple(o)))
    foot = o + up * stub
    a.body = mb
    a.sockets = {"mast_foot": tuple(foot), "foundation_base": (0.0, 0.0, m(fo["bottom"]))}
    a.dims.update({"leanDeg": t["lean"], "mastFoot": round(foot.z * 1000), "footOffset": round(foot.y * 1000),
                   "padTop": round((o + up * stub).z * 1000), "pierHalf": pi["half"]})
    return a


# -- mast section ----------------------------------------------------------------------------------
def ladder(mb, t, h, lod, st):
    """The two-stile ladder on the mast's +u face for one section: stiles the full height, rungs mirror-symmetric
    about mid-height, a standoff pair at mid-height; far out a flat strip."""
    ld = t["ladder"]
    r = m(t["mast"]["dia"]) / 2
    uo, hw = r + m(ld["standoff"]), m(ld["halfWidth"])
    sw, sd = m(ld["stile"][0]), m(ld["stile"][1])
    if lod >= 3:
        return
    if lod == 2:
        prims.box(mb, (uo - sd, -hw, 0.0), (uo + sd, hw, h), st["galv"], skip=("-2", "+2"))
        return
    for s in (-1, 1):
        prims.box(mb, (uo - sw / 2, s * hw - sd / 2, 0.0), (uo + sw / 2, s * hw + sd / 2, h), st["galv"], skip=("-2", "+2"))
        prims.beam(mb, (r * 0.75, s * r * 0.55, h / 2), (uo - sw / 2, s * hw, h / 2), 0.04, 0.01, st["galv"], skip_ends=True)
    n = ld["rungs"]
    for k in range(n):
        w = h * (k + 0.5) / n
        prims.cylinder(mb, (uo, -hw, w), (uo, hw, w), m(ld["rungDia"]) / 2, 6 if lod == 0 else 4, st["galv"], caps=(False, False))


def ladder_top(t, cw, lean, lod, st):
    """The ladder's last stretch, on the head: on from the mast top along the leaned axis up through the catwalk's
    hatch on the sections' rung pitch, the stiles capped at the deck, grab rails on 1 m above it (as on the chair
    towers). Built upright, for the caller to lean."""
    ld = t["ladder"]
    mb = MeshBuilder()
    r = m(t["mast"]["dia"]) / 2
    uo, hw = r + m(ld["standoff"]), m(ld["halfWidth"])
    sw, sd = m(ld["stile"][0]), m(ld["stile"][1])
    h = m(cw["deck"]) / math.cos(lean)   # along the axis to the deck's top
    if lod == 2:
        prims.box(mb, (uo - sd, -hw, 0.0), (uo + sd, hw, h), st["galv"], skip=("-2",))
    if lod >= 2:
        return mb
    for sv in (-1, 1):
        prims.box(mb, (uo - sw / 2, sv * hw - sd / 2, 0.0), (uo + sw / 2, sv * hw + sd / 2, h), st["galv"], skip=("-2",))
        prims.cylinder(mb, (uo, sv * hw, h), (uo, sv * hw, h + m(cw["grabRail"])), 0.021, 6 if lod == 0 else 4, st["galv"],
                       caps=(False, True))
    pitch = m(t["mast"]["section"]) / ld["rungs"]
    k = 0
    while (k + 0.5) * pitch < h - 0.05:
        prims.cylinder(mb, (uo, -hw, (k + 0.5) * pitch), (uo, hw, (k + 0.5) * pitch), m(ld["rungDia"]) / 2, 6 if lod == 0 else 4,
                       st["galv"], caps=(False, False))
        k += 1
    return mb


def mast_section(t, lod, st):
    a = Asset(PREFIX + "mast")
    mb = MeshBuilder()
    h = m(t["mast"]["section"])
    r = m(t["mast"]["dia"]) / 2
    prims.cylinder(mb, (0, 0, 0), (0, 0, h), r, parts.sides(lod, *t["mast"]["sides"]), st["galv"], caps=(False, False))
    ladder(mb, t, h, lod, st)
    a.body = mb
    a.sockets = {"top": (0.0, 0.0, h)}
    a.dims.update({"section": t["mast"]["section"], "mastDia": t["mast"]["dia"], "leanDeg": t["lean"]})
    return a


# -- sheave trains ----------------------------------------------------------------------------------
def sheave(mb, c, s, out, lod, face, st):
    """A line sheave, its axle along v: a dished wheel (face: red or galvanised) and a rubber liner whose inboard
    flange stands proud and whose outboard lip is low, so a grip on the rope's outboard side passes (C12). LOD1 a
    plainer profile, then a drum."""
    R, rg, rl = s["dia"] / 2, s["grooveBottom"] / 2, s["lip"] / 2
    hw, rim = s["width"] / 2, s["rim"] / 2
    n = parts.sides(lod, 16, 8)
    if lod >= 2:
        prims.cylinder(mb, c - V * hw, c + V * hw, R, 6, face)
        return
    o = out
    # the dished wheel, inboard and outboard faces, each facing away from the wheel (Gate 2: the outboard face's dish
    # and a -v sheave's LOD1 faces had faced in, so the game, culling back faces, showed through the wheel)
    if lod == 0:
        prims.lathe(mb, c, V, [(0.03, -o * 0.045), (0.07, -o * 0.045), (rim, -o * 0.03)], n, face, closed=False, side=-o)
        prims.lathe(mb, c, V, [(rim, o * 0.03), (0.075, o * 0.015), (0.03, o * 0.045)], n, face, closed=False, side=o)
    else:
        prims.lathe(mb, c, V, [(0.03, -o * 0.04), (rim, -o * 0.03)], n, face, closed=False, side=-o)
        prims.lathe(mb, c, V, [(rim, o * 0.03), (0.03, o * 0.04)], n, face, closed=False, side=o)
    liner = [(rim, -o * hw), (R, -o * hw), (R, -o * (hw - s["flange"])), (rg, 0.0), (rl, o * hw), (rim, o * hw)]
    if lod == 1:
        liner = [(rim, -o * hw), (R, -o * hw), (rg, o * hw * 0.3), (rim, o * hw)]
    if o < 0:
        liner.reverse()
    prims.lathe(mb, c, V, liner, n, st["rubber"])


def axles_u(T, count):
    """Axle positions along u for `count` sheaves: pairs on rockers (pitch), rockers rockerGap apart, centred on 0."""
    us, u = [0.0], 0.0
    for k in range(1, count):
        u += T["pitch"] if k % 2 else T["rockerGap"]
        us.append(u)
    mid = us[-1] / 2
    return [x - mid for x in us]


def train_v(T, v_rope):
    """The v planes of a train on the rope at v_rope: the inner rocker web and outer cheek plate, the equaliser, the
    end plate's inboard face (the crossarm's end), all from the sheave's hub outward-in."""
    out = 1.0 if v_rope > 0 else -1.0
    inb = -out
    ci = v_rope + inb * (T["hubHalf"] + T["cheekGap"] + T["rockerThick"] / 2)
    co = v_rope + out * (T["hubHalf"] + T["cheekGap"] + T["cheekThick"] / 2)
    eq = ci + inb * (T["rockerThick"] / 2 + T["eqGap"] + T["eqThick"] / 2)
    if T.get("yokeDrop"):   # a row on yokes: the yokes take that plane, the main equaliser one plate further inboard
        eq += inb * (T["eqThick"] + T["eqGap"])
    ep = eq + inb * (T["eqThick"] / 2 + T["endGap"])   # the end plate's outboard face
    return out, inb, ci, co, eq, ep


def train(mb, a, tag, v_rope, mode, n, T, s, hd, lod, st, w_pin, u_c=0.0, ends=(-1, 1), follow=False):
    """A sheave train on the rope at v_rope, on the end pin at (u_c, w_pin): n sheaves in pairs on rockers (cheek
    plates both sides), the rockers on a pentagonal equaliser, black catchers out from its ends (`ends`: -1 the -u
    end, +1 the +u end). Returns the rope's height. Pivots (the model contract's names): eq_<tag> on the end pin (the
    whole train follows the rope's angle), rocker_<tag><i> under it, sheave_<tag><k> under its rocker. With
    T["yokeDrop"] (a terminal's long row, sheaves in fours) each pair of rockers hangs on a yoke, yoke_<tag><j>, its pin
    yokeDrop under theirs, and the yokes on the main equaliser, as real long rows are built. `follow` (a support row only): the
    catchers keep their place under the rope as it leaves the end sheave down the arc's tangent, not under a level
    rope (a terminal's breakover row, v3.1)."""
    if u_c:   # built about u = 0, then moved along the line with its pivots
        tmp, before = MeshBuilder(), set(a.pivots)
        rope = train(tmp, a, tag, v_rope, mode, n, T, s, hd, lod, st, w_pin, 0.0, ends, follow)
        mb.merge(tmp.translated((u_c, 0.0, 0.0)))
        for k in set(a.pivots) - before:
            p = a.pivots[k]["pos"]
            a.pivots[k]["pos"] = (p[0] + u_c, p[1], p[2])
        return rope
    out, inb, v_ci, v_co, v_eq, v_ep = train_v(T, v_rope)
    yd = T.get("yokeDrop")
    v_y = v_eq - inb * (T["eqThick"] + T["eqGap"]) if yd else v_eq   # the rockers' pins end in this plane
    sg = -1.0 if mode == "support" else 1.0          # the structure is below a support row, above a hold-down
    Rc = s["grooveBottom"] / 2 + T["ropeR"]
    rope = w_pin - sg * (T["eqDrop"] + T["rockerDrop"] + Rc)
    us = axles_u(T, n)
    axles = [Vector((u, v_rope, rope + sg * (Rc + u * u / (2 * T["arcR"])))) for u in us]
    tname = f"eq_{tag}"
    a.pivots[tname] = {"pos": (0.0, v_eq, w_pin), "axis": (0.0, 1.0, 0.0), "hinge": True}
    pt2, wt2 = T["cheekThick"] / 2, T["rockerThick"] / 2
    pins = []
    for i in range(n // 2):
        pin = (axles[2 * i] + axles[2 * i + 1]) / 2 + W * sg * T["rockerDrop"]
        pin.y = v_y
        pins.append(pin)
    ypins = []
    if yd:
        assert n % 4 == 0, "yokes take the rockers in pairs"
        for j in range(n // 4):
            yp = (pins[2 * j] + pins[2 * j + 1]) / 2 + W * sg * yd
            yp.y = v_y
            a.pivots[f"yoke_{tag}{j + 1}"] = {"pos": tuple(yp), "axis": (0.0, 1.0, 0.0), "parent": tname, "hinge": True}
            ypins.append(yp)
    for i in range(n // 2):
        a0, a1 = axles[2 * i], axles[2 * i + 1]
        pin = pins[i]
        rname = f"rocker_{tag}{i + 1}"
        a.pivots[rname] = {"pos": tuple(pin), "axis": (0.0, 1.0, 0.0), "parent": f"yoke_{tag}{i // 2 + 1}" if yd else tname,
                           "hinge": True}
        rocker_tag = rname if lod <= 1 else tname
        if lod <= 1:
            with mb.grouped(rocker_tag):   # the rocker: a cast web inboard (wide at the axles, narrow at its pin, R2),
                lo = min(a0.z, a1.z, pin.z) - 0.045   # a thin cheek plate outboard
                hi = max(a0.z, a1.z, pin.z) + 0.045
                wide = (a0.z + a1.z) / 2 - sg * 0.045
                narrow = pin.z + sg * 0.055
                web = [(a0.x - 0.09, wide), (a1.x + 0.09, wide), (pin.x + 0.07, narrow), (pin.x - 0.07, narrow)]
                prims.prism(mb, web, (0.0, v_ci - wt2, 0.0), U, W, V, 2 * wt2, st["galv"])
                prims.box(mb, (a0.x - 0.075, v_co - pt2, lo), (a1.x + 0.075, v_co + pt2, hi), st["galv"])
            if lod == 0:   # the rocker's pin from the equaliser into the inner cheek, its own piece for the clash sweep
                with mb.grouped(f"{rocker_tag}#pin"):
                    prims.cylinder(mb, Vector((pin.x, v_y, pin.z)), Vector((pin.x, v_ci, pin.z)), T["rockerPinDia"] / 2, 8, st["rod"])
        if lod == 2:   # far out: a block per sheave, coloured as its sheave (Gate 2: a block per pair turned rows red)
            with mb.grouped(tname):
                R, hw = s["dia"] / 2, s["width"] / 2
                for j, ax in enumerate((a0, a1)):
                    prims.box(mb, (ax.x - R, v_rope - hw, ax.z - R), (ax.x + R, v_rope + hw, ax.z + R),
                              parts.row_face(2 * i + j, n, st))
        if lod >= 2:
            continue
        for j, ax in enumerate((a0, a1)):
            k = 2 * i + j
            sname = f"sheave_{tag}{k + 1}"
            a.pivots[sname] = {"pos": tuple(ax), "axis": (0.0, 1.0, 0.0), "parent": rname}
            sheave_tag = sname if lod == 0 else rocker_tag
            with mb.grouped(sheave_tag):
                sheave(mb, ax, s, out, lod, parts.row_face(k, n, st), st)
            if lod == 0:   # the axle through the sheave and both cheeks
                with mb.grouped(f"{sheave_tag}#pin"):
                    prims.cylinder(mb, Vector((ax.x, v_ci - inb * pt2, ax.z)), Vector((ax.x, v_co + inb * pt2, ax.z)),
                                   T["axleDia"] / 2, 8, st["rod"])
    if lod >= 2:   # the pivots the far LODs don't draw on their own still name every rocker and sheave
        for i in range(n // 2):
            for j in range(2):
                k = 2 * i + j
                a.pivots.setdefault(f"sheave_{tag}{k + 1}", {"pos": tuple(axles[k]), "axis": (0.0, 1.0, 0.0),
                                                             "parent": f"rocker_{tag}{i + 1}"})

    # the yokes: pentagons like the equaliser, each through its two rocker pins, its point on its own pin
    for j, yp in enumerate(ypins):
        yname = f"yoke_{tag}{j + 1}"
        p0, p1 = pins[2 * j], pins[2 * j + 1]
        if lod <= 1:
            yl = abs(p1.x - p0.x) / 2 + 0.08
            yf = (max(p0.z, p1.z) if sg < 0 else min(p0.z, p1.z)) - sg * 0.045
            ys = (min(p0.z, p1.z) if sg < 0 else max(p0.z, p1.z)) + sg * 0.05
            poly = [(yp.x - yl, yf), (yp.x + yl, yf), (yp.x + yl, ys), (yp.x, yp.z + sg * 0.055), (yp.x - yl, ys)]
            with mb.grouped(yname):
                prims.prism(mb, poly, (0.0, v_y - T["eqThick"] / 2, 0.0), U, W, V, T["eqThick"], st["galv"])
        if lod == 0:   # the yoke's pin into the main equaliser
            with mb.grouped(f"{yname}#pin"):
                prims.cylinder(mb, Vector((yp.x, v_y - inb * T["eqThick"] / 2, yp.z)),
                               Vector((yp.x, v_eq + inb * (T["eqThick"] / 2 + 0.006), yp.z)), T["rockerPinDia"] / 2, 8, st["rod"])

    # the equaliser: a pentagon, its flat edge through the rocker pins (the yoke pins on a row with yokes), its point on
    # the end pin
    tops = ypins or pins
    L = max(abs(p.x) for p in tops) + 0.08
    far = max(p.z for p in tops) if sg < 0 else min(p.z for p in tops)
    near = min(p.z for p in tops) if sg < 0 else max(p.z for p in tops)
    flat = far - sg * 0.045
    shoulder = near + sg * 0.05
    tip = w_pin + sg * 0.055
    with mb.grouped(tname):
        if lod <= 1:
            poly = [(-L, flat), (L, flat), (L, shoulder), (0.0, tip), (-L, shoulder)]
            prims.prism(mb, poly, (0.0, v_eq - T["eqThick"] / 2, 0.0), U, W, V, T["eqThick"], st["galv"])
        elif lod == 2:
            prims.box(mb, (-L, v_eq - T["eqThick"] / 2, min(flat, tip)), (L, v_eq + T["eqThick"] / 2, max(flat, tip)), st["galv"])
        else:   # the whole row as one block, galvanised as the row reads from afar (Gate 2: a red block popped)
            R, hw = s["dia"] / 2, s["width"] / 2
            prims.box(mb, (axles[0].x - R, v_rope - hw, min(p.z for p in axles) - R),
                      (axles[-1].x + R, v_rope + hw, max(p.z for p in axles) + R), st["galv"], skip=("-2",))

    # the catchers: from each end of the equaliser a bracket out along u, across under the rope, and the black tube out
    # past the train in one smooth sweep outboard and down (R29), the same on both ropes (owner, v3.1)
    c = hd["catcher"]
    end_u = max(us) + s["dia"] / 2
    u0 = end_u + m(c["gap"])
    w_c = rope - m(c["below"])
    v_c = v_rope + out * m(c["out"])
    cr = m(c["dia"]) / 2
    Lc = m(c["length"])
    so, sd = m(c["sweep"][0]), m(c["sweep"][1])

    def drop(e, x):   # how far the rope leaving end e has fallen below `rope` at x past the train's middle
        if not follow:
            return 0.0
        ue = us[-1] if e > 0 else us[0]
        al = math.atan(-ue / T["arcR"])   # the rope's slope over the end sheave, kept past it
        cu, cw = ue - Rc * math.sin(al), rope - Rc - ue * ue / (2 * T["arcR"]) + Rc * math.cos(al)
        return cw + (e * x - cu) * math.tan(al) - rope

    if lod <= 2:
        with mb.grouped(tname):
            for e in ends:
                d0, d45, d80, d1 = (drop(e, u0 + Lc * f) for f in (0.0, 0.45, 0.8, 1.0))
                if lod <= 1:
                    prims.beam(mb, (e * (L - 0.04), v_eq, flat), (e * u0, v_eq, w_c + d0), 0.05, T["eqThick"], st["galv"], up=V)
                    prims.beam(mb, (e * u0, v_eq, w_c + d0), (e * u0, v_c, w_c + d0), 0.05, 0.04, st["galv"])
                    pts = [Vector((e * u0, v_c, w_c + d0)),
                           Vector((e * (u0 + Lc * 0.45), v_c + out * so * 0.3, w_c + d45 - sd * 0.3)),
                           Vector((e * (u0 + Lc * 0.8), v_c + out * so * 0.72, w_c + d80 - sd * 0.72)),
                           Vector((e * (u0 + Lc), v_c + out * so, w_c + d1 - sd))]   # a t^1.5 droop: no kink at the tip
                    if lod == 1:
                        pts = [pts[0], pts[2], pts[3]]
                    prims.tube_path(mb, pts, cr, parts.sides(lod, 8, 5), st["black"], caps=True)
                else:
                    prims.beam(mb, (e * u0, v_c, w_c + d0), (e * (u0 + Lc), v_c + out * so * 0.5, w_c + d1 - sd * 0.5),
                               2 * cr, 2 * cr, st["black"], skip_ends=True)
    return rope


def breakover(mb, a, tag, v_rope, n, T, s, hd, lod, st, rope, u_c, ends=(1,)):
    """A breakover row at a terminal (owner, Gate 1 v3.1): a support train on the station arc T["arcR"], turned about
    its end pin so the rope runs level from the bullwheel onto its first (-u) sheave at `rope` and bends down over the
    rest toward the line. The turn is the rest pose (the pivots turn with it); the rocker angles stay zero. The game
    re-poses the row for the line's own angle (eq_<tag> on the end pin, yoke_<tag>j when T["yokeDrop"], rocker_<tag>i),
    holding the first sheave, so the rope always reaches the wheel level. Returns {"pin": (u, w), "rope": [(u, w) of the rope over each
    sheave, along +u], "exit": the rope's angle past the last sheave (radians, negative: down toward +u)}."""
    R = T["arcR"]
    Rc = s["grooveBottom"] / 2 + T["ropeR"]
    us = axles_u(T, n)
    phi = math.atan(us[0] / R)   # levels the first sheave's tangent (us[0] < 0, so the +u end goes down)
    cp, sp = math.cos(phi), math.sin(phi)
    # the first axle from the pin before the turn (train(): axles at w_pin + eqDrop + rockerDrop - u^2 / 2R), then
    # the pin's height that puts the rope over that axle at `rope` once turned
    lift_f = T["eqDrop"] + T["rockerDrop"] - us[0] ** 2 / (2 * R)
    w_pin = rope - Rc - (us[0] * sp + lift_f * cp)
    tmp, before = MeshBuilder(), set(a.pivots)
    train(tmp, a, tag, v_rope, "support", n, T, s, hd, lod, st, w_pin, 0.0, ends, follow=True)

    def turn(u, w):
        dw = w - w_pin
        return u * cp - dw * sp + u_c, w_pin + u * sp + dw * cp

    verts = []
    for p in tmp.verts:
        u, v, w = frame.lift(p)
        tu, tw = turn(u, w)
        verts.append(frame.b((tu, v, tw)))
    tmp.verts = verts
    mb.merge(tmp)
    for k in set(a.pivots) - before:
        p = a.pivots[k]["pos"]
        tu, tw = turn(p[0], p[2])
        a.pivots[k]["pos"] = (tu, p[1], tw)
    rope_pts = []
    for u in us:
        tu, tw = turn(u, w_pin + T["eqDrop"] + T["rockerDrop"] - u * u / (2 * R))
        al = math.atan(-u / R) + phi   # the rope's slope over this sheave
        rope_pts.append((tu - Rc * math.sin(al), tw + Rc * math.cos(al)))
    return {"pin": (u_c, w_pin), "rope": rope_pts, "exit": math.atan(-us[-1] / R) + phi}


def head(spec, t, variant, lod, st):
    c = spec["common"]
    hd = t["head"]
    cfg = t["heads"][variant]
    lean = math.radians(t["lean"])
    T = parts.metres(spec["train"])
    T["ropeR"] = c["ropeDiameter"] / 2000.0
    s = parts.metres(c["sheave"])
    a = Asset(PREFIX + "tower_" + variant)
    mb = MeshBuilder()
    hg = m(c["lineGauge"]) / 2
    r = m(t["mast"]["dia"]) / 2
    sl, fl, arm, ep = hd["sleeve"], hd["flange"], hd["arm"], hd["endPlate"]

    # the sleeve over the mast top flaring into the flange, built along the leaned axis
    lm = MeshBuilder()
    n = parts.sides(lod, 16, 10, 8, 6)
    rs = r + m(sl["gap"])
    prims.cylinder(lm, (0, 0, m(sl["from"])), (0, 0, m(sl["to"]) if lod <= 2 else 0.0), rs, n, st["galv"], caps=(lod <= 1, False))
    if lod <= 2:
        prims.cylinder(lm, (0, 0, m(sl["to"])), (0, 0, m(hd["cone"]["top"])), rs, n, st["galv"], caps=(False, False),
                       r1=m(hd["cone"]["dia"]) / 2)
    mb.merge(leaned(lm, lean))
    fh, ft = m(fl["half"]), m(fl["thick"])
    if lod <= 2:
        prims.box(mb, (-fh, -fh, -ft), (fh, fh, 0.0), st["galv"])

    # the crossarm: a deep box between the end plates, stiffening ribs on both faces
    o_, i_, v_ci, v_co, v_eq, v_ep = train_v(T, hg)
    hv = v_ep - m(ep["thick"])        # the arm's end: the end plate's inboard face
    au, ad = m(arm["halfU"]), m(arm["depth"])
    prims.box(mb, (-au, -hv, 0.0), (au, hv, ad), st["galv"], skip=("-1", "+1") if lod <= 2 else ())
    cw = hd["catwalk"]
    if lod == 0:   # triangular gussets on both faces (R7): wide at the arm's foot, to a point at its top
        k = int(hv // m(arm["ribs"]))
        ro, rt = m(arm["ribOut"]), m(arm["ribThick"])
        for i in range(-k, k + 1):
            for su in (-1, 1):
                v0 = i * m(arm["ribs"])
                if su > 0 and abs(v0) < m(cw["v"]) / 2:   # inside the catwalk's end member on the +u face
                    continue
                gusset = [(su * au, 0.0), (su * (au + ro), 0.0), (su * au, ad)]
                prims.prism(mb, gusset, (0.0, v0 - rt / 2, 0.0), U, W, V, rt, st["galv"])
    w_pin = ad + m(hd["ropeAboveArm"]) - (T["eqDrop"] + T["rockerDrop"] + s["grooveBottom"] / 2 + T["ropeR"])

    # the end plates with the end pins and their octagonal covers
    for sv in (-1, 1):
        lo_v, hi_v = sorted((sv * hv, sv * v_ep))
        if lod <= 2:
            prims.box(mb, (-m(ep["halfU"]), lo_v, 0.0), (m(ep["halfU"]), hi_v, w_pin + m(ep["above"])), st["galv"])
        if lod == 0:
            pc = hd["pinCover"]
            prims.cylinder(mb, (0.0, sv * v_ep, w_pin), (0.0, sv * (v_ep + m(pc["length"])), w_pin), m(pc["dia"]) / 2, 8,
                           st["galv"], caps=(False, True), phase=math.pi / 8, smooth=False)   # on the plate's outer face
        if lod == 0:
            with mb.grouped("#pin"):
                prims.cylinder(mb, (0.0, sv * (hv - 0.01), w_pin), (0.0, sv * (v_eq + T["eqThick"] / 2 + 0.006), w_pin),
                               T["pinDia"] / 2, 10, st["rod"])

    # the trains
    mode = "support" if "support" in cfg else "hold"
    rope = None
    for side, tag in ((-1, "l"), (1, "r")):
        rope = train(mb, a, tag, side * hg, mode, cfg[mode], T, s, hd, lod, st, w_pin)

    # the upper level (owner, 2026-11-03): the fixed-grip chair tower's lifting frame in miniature (spec "portal", the
    # chair's own numbers scaled to the gauge, read by the kit's portal), its legs on foot plates on the arm's top
    po, fp, lg, bm = hd["portal"], hd["footPlates"], hd["portal"]["leg"], hd["portal"]["beam"]
    bt, bu0, bu1 = m(bm["top"]), m(bm["uFrom"]), m(bm["uTo"])
    if lod <= 2:
        for sv in (-1, 1):
            prims.box(mb, (m(fp["uFrom"]), sv * m(fp["v"]) - m(fp["halfWidth"]), ad), (m(fp["uTo"]), sv * m(fp["v"]) + m(fp["halfWidth"]), m(fp["top"])),
                      st["galv"])
        heads.portal(mb, po, lod, st)
    else:   # far out: the legs and the beam as blocks (the kit's portal draws nothing at LOD3)
        for sv in (-1, 1):
            f0, f1 = sorted((sv * m(lg["footV"][0]), sv * m(lg["footV"][1])))
            h0, h1 = sorted((sv * m(lg["headV"][0]), sv * m(lg["headV"][1])))
            prims.prism(mb, [(f0, ad), (f1, ad), (h1, m(lg["headW"])), (h0, m(lg["headW"]))], (m(lg["uFrom"]), 0, 0), V, W, U,
                        m(lg["uTo"]) - m(lg["uFrom"]), st["galv"])
        prims.box(mb, (bu0, -m(bm["v"]) / 2, m(bm["bottom"])), (bu1, m(bm["v"]) / 2, bt), st["galv"])
    # the number plate as on the chair towers: on the beam's face toward the bottom station, stood off past the bolt heads
    npl = hd["numberPlate"]
    nu = bu0 - m(npl["standoff"])
    wc, h2 = (m(bm["bottom"]) + bt) / 2, m(npl["height"]) / 2
    if lod <= 1:
        prims.box(mb, (nu - m(npl["thick"]), -m(npl["halfV"]), wc - h2), (nu, m(npl["halfV"]), wc + h2), st["sign"])
    a.sockets["number_plate"] = (nu - m(npl["thick"]), 0.0, wc)
    hk = hd["hook"]
    eye_r = m(hk["dia"]) / 2
    curl = m(hk["curl"])
    for sv, name in ((-1, "wire_left"), (1, "wire_right")):   # a curled hook bracket on the beam's top each side (R27)
        p0 = Vector((0.0, sv * m(hk["v"]), bt))
        p1 = p0 + W * (m(hk["post"]) + eye_r)
        p2 = p1 + V * sv * curl
        p3 = p2 - W * eye_r * 1.6
        if lod == 0:
            pts = prims.fillet(prims.fillet([p0, p1, p2, p3], 2, 0.05, 2), 1, 0.05, 2)
            prims.tube_path(mb, [Vector(p) for p in pts], m(hk["rod"]), 6, st["galv"], caps=True)
        a.sockets[name] = tuple(p1 + V * sv * curl * 0.5 - W * m(hk["rod"]))

    # the cables (D9): along the beam's +u face below the bolt heads, down each leg's outer edge, over the arm's top to
    # its +u face by the end plates, held by clips clear of the bolts
    if lod == 0:
        cab = hd["cable"]
        cr = m(cab["dia"]) / 2
        uf = bu1 + cr
        wb = m(bm["bottom"]) + 0.025
        for sv in (-1, 1):
            top_v, foot_v = sv * (m(lg["headV"][1]) - 0.03), sv * (m(lg["footV"][1]) - 0.03)
            pts = [Vector((uf, 0.0, wb)), Vector((uf, top_v, wb)), Vector((m(lg["uTo"]) + cr, top_v, m(lg["headW"]) - 0.05)),
                   Vector((m(lg["uTo"]) + cr, foot_v, m(lg["footW"]) + 0.04)), Vector((au + cr, sv * (hv - 0.12), ad + cr)),
                   Vector((au + cr, sv * (hv - 0.04), ad - 0.03))]
            prims.tube_path(mb, pts, cr, 4, st["black"], caps=True)
        clips = [(uf, sv * x, wb) for sv in (-1, 1) for x in (0.18, 0.62, 1.0)]
        for p in clips:
            p = Vector(p)
            prims.box(mb, p - Vector((cr + 0.006, 0.015, cr + 0.006)), p + Vector((cr + 0.006, 0.015, cr + 0.006)), st["galv"])

    # the catwalk (owner, 2026-11-03): the chair towers' head platform beside the arm on the ladder's side, its hatch
    # centred where the leaned ladder meets the deck; far out one slab, the hatch, hoop and struts under a pixel
    vc = m(cw["deck"]) * math.tan(lean)
    if lod <= 1:
        heads.entry_platform(mb, cw, {"bottom": 0, "top": arm["depth"]}, lod, st, hatch=(vc - m(cw["hatch"]), vc + m(cw["hatch"])))
    elif lod == 2:
        parts.deck(mb, (m(cw["endMember"][0]), -m(cw["v"]) / 2, m(cw["under"])), (m(cw["uTo"]), m(cw["v"]) / 2, m(cw["deck"])),
                   lod, st["galv"])
    mb.merge(leaned(ladder_top(t, cw, lean, lod, st), lean))

    a.body = mb
    a.sockets.update({"mast_top": (0.0, 0.0, 0.0), "rope_left": (0.0, -hg, rope), "rope_right": (0.0, hg, rope)})
    n_sheaves = cfg[mode]
    a.dims.update({"rope": round(rope * 1000), "armTop": round(ad * 1000), "sheaves": 2 * n_sheaves, "leanDeg": t["lean"],
                   "portalTop": bm["top"], "catwalk": cw["deck"],
                   "footOffsetPerSection": round(m(t["mast"]["section"]) * math.sin(lean) * 1000, 1)})
    return a
