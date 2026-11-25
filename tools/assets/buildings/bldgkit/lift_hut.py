"""A factory lift operator hut from its spec (task 4.50 phase 3; lifthut.json): a flat-roofed panel box on four pipe
posts (or, the mini, on timber skids), its glazed front and a deep canopy facing the chairs (+v), a tall-lite door
and a tall window on the wall round the corner (`doorSide`, -u or +u: the hut's hand), a steel landing with rails and a stair, straight out from the
door or down along the wall. The stair runs on at its own pitch down to the posts' depth (`below`), so wherever
the hut stands the ground clips it at the right step, as it clips the posts. Three bands on the walls: a wainscot and a fascia in the lift's livery (mask 1), light
ribbed panels between; the canopy carries the fascia on. The other end wall has a window too (`farWindow`), so the
operator sees both up and down the line. The large hut is wider than deep: its whole front is big single panes, the
drive's cabinets stand along its blank back wall, and its door has an awning.

LOD0 everything; LOD1 no rails, plain fixtures; LOD2 a closed shell with the openings as flat decals, the landing and
the stair as slabs; LOD3 a banded box, its canopy and a roof. Lights: downlights recessed under the canopy, a cage
sconce beside each door (light_* sockets and records); window glass masked to glow while the lift runs. Sockets: each
door's sill, `label` (the name board on the canopy's fascia), `view` (the operator's eye at the front window) and
every light.
"""
import math

from mathutils import Vector

from liftkit import prims
from liftkit.export import Asset
from liftkit.frame import mm
from liftkit.mesh import GLASS, MeshBuilder, Style

from . import fixtures as fx
from .palette import LAMP, WINDOW
from .shell import WallFrame, wall

LODS = 4
WALL = "panel_grey"
LEGS = "legs"   # what the hut stands on and climbs by (posts, conduits, skids, step, landing legs, stair): its own
                # part (<id>_legs_LODn), hidden on a ramp deck, where the ramp gives the hut posts to the ground
UP = Vector((0.0, 0.0, 1.0))


def styles():
    trim = Style("trim_grey")
    livery = Style("livery", livery=1.0)
    return {
        "wall": Style(WALL), "wainscot": livery, "fascia": livery.but(snow=0.2),
        "reveal": trim, "trim": trim, "sill": Style("trim_grey", snow=0.8),
        "roof": Style("roof_charcoal", snow=1.0), "parapet": livery.but(snow=1.0), "soffit": Style("trim_grey"),
        "door": Style("paint_grey"), "glass": Style("window_glass", livery=WINDOW, slot=GLASS),
        "kick": Style("galvanised"), "seal": Style("louvre_dark"),
        "housing": Style("louvre_dark", snow=0.5), "lens": Style("lamp_lens", livery=LAMP),
        "post": Style("trim_grey", snow=0.0), "under": Style("trim_grey", snow=0.0),
        "grating": Style("grating", snow=1.0), "steel": Style("galvanised", snow=0.0),
        "rail": Style("galvanised", snow=0.3), "timber": Style("door_panel", snow=0.3),
        "step": Style("concrete_base", snow=1.0),
    }


def pick(spec, variant):
    """The variant's spec: `common` overlaid with the variant's keys (one level deep for dicts)."""
    out = _copy(spec["common"])
    for k, v in spec["variants"][variant].items():
        out[k] = {**out[k], **v} if isinstance(v, dict) and isinstance(out.get(k), dict) else v
    return out


def _copy(x):
    if isinstance(x, dict):
        return {k: _copy(v) for k, v in x.items()}
    if isinstance(x, list):
        return [_copy(v) for v in x]
    return x


def _wall_frame(key, hx, hy, floor):
    """A wall's frame, its origin at the floor: `a` runs along +u on the front and back walls, along +v (from the back
    end to the front) on the side walls."""
    if key in ("+v", "-v"):
        s = 1 if key == "+v" else -1
        return WallFrame((-hx, s * hy, floor), (1.0, 0.0, 0.0), (0.0, s, 0.0)), 2 * hx
    s = 1 if key == "+u" else -1
    return WallFrame((s * hx, -hy, floor), (0.0, 1.0, 0.0), (s, 0.0, 0.0)), 2 * hy


def build(spec, lod, asset_id=None):
    st = styles()
    a = Asset(asset_id or spec["id"])
    mb = MeshBuilder()
    hx, hy = mm(spec["footprint"]["length"]) / 2, mm(spec["footprint"]["width"]) / 2
    F, H = mm(spec["floor"]), mm(spec["height"])
    fas, can, base, wains = mm(spec["fascia"]), mm(spec["canopy"]), mm(spec["base"]), mm(spec["wainscot"])
    below = mm(spec["below"])
    soffit = H - fas                               # the canopy's underside and the fascia's lower edge, from the floor
    a.dims.update({"footprint": [spec["footprint"]["length"], spec["footprint"]["width"]], "floor": spec["floor"],
                   "height": spec["height"], "ridge": round((F + soffit) * 1000), "roofTop": round((F + H) * 1000),
                   "canopy": spec["canopy"]})
    a.lights, a.glow, a.problems = [], dict(spec["lights"]["glow"]), []

    sides = {}
    for key in ("+v", "-v", "+u", "-u"):
        wf, length = _wall_frame(key, hx, hy, F)
        sides[key] = {"wf": wf, "length": length, "openings": [], "fill": []}
    ds = spec.get("doorSide", "-u")                # which end wall the door is in: the hut's hand
    door_side, far_side = sides[ds], sides["+u" if ds == "-u" else "-u"]
    L = door_side["length"]

    fr = spec["front"]                             # the front glazing
    if fr.get("panes"):                            # single panes side by side, from the door's end
        n_p, post = fr["panes"], mm(fr["post"])
        pane = (mm(fr["width"]) - (n_p - 1) * post) / n_p
        for i in range(n_p):
            f0 = mm(fr["fromDoorEnd"]) + i * (pane + post)
            box = (f0, f0 + pane, mm(fr["sill"]), mm(fr["head"]))
            if ds == "+u":
                box = (2 * hx - box[1], 2 * hx - box[0], box[2], box[3])
            sides["+v"]["openings"].append(box)
            sides["+v"]["fill"].append(("window", box, "none"))
    else:                                          # one window, centred, with a mullion
        w2 = mm(fr["width"]) / 2
        box = (hx - w2, hx + w2, mm(fr["sill"]), mm(fr["head"]))
        sides["+v"]["openings"].append(box)
        sides["+v"]["fill"].append(("window", box, None))
    sw = spec.get("sideWindow")
    if sw:                                         # the tall window beside the door, toward the front, a rail across it
        a1 = L - mm(sw["fromFront"])
        box = (a1 - mm(sw["width"]), a1, mm(sw["sill"]), mm(sw["head"]))
        door_side["openings"].append(box)
        door_side["fill"].append(("window", box, "h"))
    fw = spec.get("farWindow")
    if fw:                                         # the other end wall's window (true: the side window again), so
        s2 = sw if fw is True else fw              # the operator sees both up and down the line
        a1 = L - mm(s2["fromFront"])
        box = (a1 - mm(s2["width"]), a1, mm(s2["sill"]), mm(s2["head"]))
        far_side["openings"].append(box)
        far_side["fill"].append(("window", box, "h"))
    dr = spec["door"]
    a_door, dw, dh = L - mm(dr["fromFront"]), mm(dr["width"]), mm(dr["height"])
    door_side["openings"].append((a_door - dw / 2, a_door + dw / 2, 0.0, dh))
    door_side["fill"].append(("door", (a_door, dw, dh, tuple(mm(x) for x in dr["lite"])), None))
    bd = spec.get("backDoor")
    if bd:                                         # the cabinet room's door, in the back end
        sides["-v"]["openings"].append((hx - mm(bd["width"]) / 2, hx + mm(bd["width"]) / 2, 0.0, mm(bd["height"])))
        sides["-v"]["fill"].append(("door", (hx, mm(bd["width"]), mm(bd["height"]), None), None))
    for key, sd in sides.items():                  # every opening inside its wall, clear of the corners
        for o in sd["openings"]:
            if o[0] < 0.09 or o[1] > sd["length"] - 0.09:
                a.problems.append(f"an opening on {key} runs into a corner ({o[0]:.2f}-{o[1]:.2f} of {sd['length']:.2f} m)")
            if o[3] > soffit - 0.03:
                a.problems.append(f"an opening on {key} runs into the fascia")

    # walls in three bands; the canopy covers the front wall's fascia band
    reveal = mm(spec["reveal"])
    for key, sd in sides.items():
        bands = [(-base, wains, st["wainscot"]), (wains, soffit, st["wall"])]
        if key != "+v":
            bands.append((soffit, H, st["fascia"]))
        wall(mb, sd["wf"], sd["length"], bands, sd["openings"] if lod <= 1 else [], st["reveal"], depth=reveal)
        if lod == 3:
            continue
        for kind, item, opt in sd["fill"]:
            if kind == "door":
                c, w_, h_, lite = item
                fx.man_door(mb, sd["wf"], c, w_, h_, reveal * 0.6, st, lod, lite is not None, lite=lite)
            else:
                fx.window(mb, sd["wf"], *item, reveal * 0.6, st, lod, rail=opt)

    aw = dr.get("awning")
    if aw and lod <= 1:   # a small awning over the door, at the fascia's lower edge
        fx._wbox(mb, door_side["wf"], a_door - mm(aw["wide"]) / 2, a_door + mm(aw["wide"]) / 2, dh + 0.10,
                 dh + 0.10 + mm(aw["thick"]), 0.0, mm(aw["out"]), st["sill"])

    # the canopy: the fascia carried on past the front wall; then the roof over walls and canopy
    v0, v1 = hy, hy + can
    w0, w1 = F + soffit, F + H
    for s in (-1, 1):
        mb.face(mb.verts_lift([(s * hx, v0, w0), (s * hx, v1, w0), (s * hx, v1, w1), (s * hx, v0, w1)]), st["fascia"], (s, 0.0, 0.0))
    mb.face(mb.verts_lift([(-hx, v1, w0), (hx, v1, w0), (hx, v1, w1), (-hx, v1, w1)]), st["fascia"], (0.0, 1.0, 0.0))
    mb.face(mb.verts_lift([(-hx, v0, w0), (hx, v0, w0), (hx, v1, w0), (-hx, v1, w0)]), st["soffit"], (0.0, 0.0, -1.0))
    r0, r1 = -hy, hy + can
    if lod <= 1:   # a parapet round a recessed roof deck
        pw, pd = mm(spec["parapet"]["wide"]), mm(spec["parapet"]["deep"])
        outer = [(-hx, r0), (hx, r0), (hx, r1), (-hx, r1)]
        inner = [(-hx + pw, r0 + pw), (hx - pw, r0 + pw), (hx - pw, r1 - pw), (-hx + pw, r1 - pw)]
        for i in range(4):
            j = (i + 1) % 4
            mb.face(mb.verts_lift([(*outer[i], w1), (*outer[j], w1), (*inner[j], w1), (*inner[i], w1)]), st["parapet"], (0.0, 0.0, 1.0))
            mid = (Vector(inner[i]) + Vector(inner[j])) / 2
            mb.face(mb.verts_lift([(*inner[i], w1 - pd), (*inner[j], w1 - pd), (*inner[j], w1), (*inner[i], w1)]),
                    st["parapet"].but(snow=0.0), (-mid.x, -(mid.y - (r0 + r1) / 2), 0.0))
        mb.face(mb.verts_lift([(*p, w1 - pd) for p in inner]), st["roof"], (0.0, 0.0, 1.0))
    else:
        mb.face(mb.verts_lift([(-hx, r0, w1), (hx, r0, w1), (hx, r1, w1), (-hx, r1, w1)]), st["roof"], (0.0, 0.0, 1.0))

    # downlights recessed under the canopy
    vd, half = hy + can / 2, mm(spec["downlight"]["size"]) / 2
    for i, at in enumerate(spec["downlights"]):
        u = mm(at)
        if lod <= 1:
            for h_, off, style in ((half + 0.02, 0.003, st["housing"]), (half, 0.006, st["lens"])):
                q = [(u - h_, vd - h_, w0 - off), (u + h_, vd - h_, w0 - off), (u + h_, vd + h_, w0 - off), (u - h_, vd + h_, w0 - off)]
                mb.face(mb.verts_lift(q), style, (0.0, 0.0, -1.0))
        _light(a, f"light_down_{i + 1}", spec["lights"]["downlight"], (u, vd, w0 - 0.006), (0.0, 0.0, -1.0))

    # the floor frame's underside, then what the hut stands on
    if lod <= 2:
        mb.face(mb.verts_lift([(-hx, -hy, F - base), (hx, -hy, F - base), (hx, hy, F - base), (-hx, hy, F - base)]),
                st["under"], (0.0, 0.0, -1.0))
    ps = spec.get("posts")
    if ps and lod <= 2:   # four pipe posts, inset, running on below the origin (the `legs` part)
        r = mm(ps["dia"]) / 2
        pu, pf = mm(ps["across"]) / 2, hy - mm(ps["fromFront"])
        with mb.grouped(LEGS):
            for su in (-1, 1):
                for v in (pf, pf - mm(ps["along"])):
                    prims.cylinder(mb, (su * pu, v, -below), (su * pu, v, F - base), r, 8 if lod == 0 else 6,
                                   st["post"], caps=(False, False))
            cd = spec.get("conduits")
            if cd and lod == 0:   # the cabinets' conduits, dropping from the floor into the ground
                for i in range(cd["count"]):
                    u = (i - (cd["count"] - 1) / 2) * mm(cd["pitch"])
                    v = -hy + mm(cd["fromBack"])
                    prims.cylinder(mb, (u, v, -below), (u, v, F - base), mm(cd["dia"]) / 2, 6, st["steel"],
                                   caps=(False, False))
    sk = spec.get("skids")
    if sk and lod <= 2:   # the mini hut's timber skids
        s2, inset = mm(sk["size"]) / 2, mm(sk["inset"])
        with mb.grouped(LEGS):
            for su in (-1, 1):
                u = su * (hx - inset)
                prims.box(mb, (u - s2, -hy - 0.1, 0.0), (u + s2, hy + 0.1, F - base), st["timber"], skip=("-2",))

    wf = door_side["wf"]
    if spec.get("deck") and ds != "-u":
        a.problems.append("a deck needs the door in the -u wall")
    if spec.get("landing") and lod <= 2:
        _landing(mb, spec, wf, L, a_door, F, below, st, lod, hx, hy)
    stp = spec.get("step")
    if stp and lod <= 2:   # the mini hut's step at its door
        with mb.grouped(LEGS):
            fx._wbox(mb, wf, a_door - dw / 2 - 0.15, a_door + dw / 2 + 0.15, -F, -F + mm(stp["top"]) + (F - 0.03) / 2,
                     0.0, mm(stp["out"]), st["step"])

    for which in spec["sconces"]:   # a cage sconce beside a door, on the side away from the window
        w_s = mm(spec["sconce"]["height"])
        if which == "door":
            sd, a_s, name = door_side, a_door - dw / 2 - mm(spec["sconce"]["beyond"]), "light_sconce"
        else:
            sd, a_s, name = sides["-v"], hx + mm(bd["width"]) / 2 + mm(spec["sconce"]["beyond"]), "light_sconce_back"
        for o in sd["openings"]:
            if o[0] - 0.1 < a_s < o[1] + 0.1 and o[2] - 0.2 < w_s < o[3] + 0.2:
                a.problems.append(f"{name} sits on an opening at {o[0]:.2f}-{o[1]:.2f} m")
        if not 0.15 < a_s < sd["length"] - 0.15:
            a.problems.append(f"{name} runs off its wall")
        pos, aim = fx.cage_sconce(mb, sd["wf"], a_s, w_s, st, lod)
        _light(a, name, spec["lights"]["sconce"], pos, aim)

    a.body = mb
    if any(t == LEGS for t in mb.tags):   # a fixed part the game hides where the hut stands on a deck (phase 4)
        a.pivots[LEGS] = {"pos": (0.0, 0.0, 0.0), "axis": (0.0, 0.0, 1.0)}
    a.sockets["door"] = wf.p(a_door, 0.0)
    if bd:
        a.sockets["door_back"] = sides["-v"]["wf"].p(hx, 0.0)
    a.sockets["label"] = (0.0, hy + can, F + H - fas / 2)
    a.sockets["view"] = (mm(spec.get("viewAt", 0)), hy, F + mm(spec["view"]))
    for lt in a.lights:
        a.sockets[lt["name"]] = tuple(lt["pos"])
    a.dims.update({"doors": 2 if bd else 1, "windows": sum(k == "window" for sd in sides.values() for k, _, _ in sd["fill"]),
                   "lights": len(a.lights)})
    return a


def _landing(mb, spec, wf, L, a_door, F, below, st, lod, hx, hy):
    """The steel landing along the door's wall, its stair straight out from the door or (`stair.along`) down along the
    wall from the landing's back edge, rails (LOD0) and legs; a `deck` carries the landing on to the back end and
    round it to a back door. Wall frame: `a` from the back end, `n` out of the wall, w from the floor."""
    ld, sr, rl = spec["landing"], spec["stair"], spec["rail"]
    out, th = mm(ld["out"]), mm(ld["thick"])
    a0, a1 = L - mm(ld["to"]), L - mm(ld["from"])
    dk = spec.get("deck")
    back = mm(dk["back"]) if dk else 0.0
    if dk:
        a0 = -back                                  # the side deck runs to the back deck's far edge
    fx._wbox(mb, wf, a0, a1, -th, 0.0, 0.0, out, st["grating"], back=False)
    if dk:   # the back deck, across the back end
        prims.box(mb, (-hx, -hy - back, F - th), (hx, -hy, F), st["grating"], skip=())
    sw2 = mm(sr["width"]) / 2
    rise, run = mm(sr["rise"]), mm(sr["run"])
    n_r = math.ceil((F + below) / rise)            # on down to the posts' depth: the ground clips it at any height
    s0, s1 = a_door - sw2, a_door + sw2
    foot_n, foot_w = out + n_r * run, -n_r * rise
    along = bool(sr.get("along"))
    n0, n1, foot_a = max(0.05, out - 2 * sw2), out, a0 - n_r * run   # the stair along the wall: its width, its foot
    with mb.grouped(LEGS):   # the stair and the landing's legs
        if lod <= 1 and along:
            for j in range(1, n_r):                     # treads
                fx._wbox(mb, wf, a0 - j * run, a0 - (j - 1) * run, -j * rise - 0.04, -j * rise, n0, n1, st["grating"], back=False)
            for nn in (n0 + 0.025, n1 - 0.025):         # stringers
                prims.beam(mb, Vector(wf.p(a0, -0.12, nn)), Vector(wf.p(foot_a, foot_w - 0.12, nn)), 0.05, 0.22, st["steel"])
        elif lod <= 1:
            for j in range(1, n_r):                     # treads
                fx._wbox(mb, wf, s0, s1, -j * rise - 0.04, -j * rise, out + (j - 1) * run, out + j * run, st["grating"], back=False)
            for aa in (s0 + 0.025, s1 - 0.025):         # stringers
                prims.beam(mb, Vector(wf.p(aa, -0.12, out)), Vector(wf.p(aa, foot_w - 0.12, foot_n)), 0.05, 0.22, st["steel"])
        if lod <= 1:
            legs = [(a0 + 0.06, out - 0.06), (a1 - 0.06, out - 0.06)]
            if dk:
                legs += [((a0 + a1) / 2, out - 0.06)]
            for aa, nn in legs:                         # the landing's legs, running on below the origin
                p = Vector(wf.p(aa, 0.0, nn))
                prims.box(mb, (p.x - 0.04, p.y - 0.04, -below), (p.x + 0.04, p.y + 0.04, F - th), st["steel"], skip=("-2", "+2"))
            if dk:
                for u in (-hx + 0.3, hx - 0.06):
                    prims.box(mb, (u - 0.04, -hy - back + 0.02, -below), (u + 0.04, -hy - back + 0.10, F - th), st["steel"],
                              skip=("-2", "+2"))
        elif along:   # LOD2: the stair as one slab
            prims.beam(mb, Vector(wf.p(a0, -0.1, (n0 + n1) / 2)), Vector(wf.p(foot_a, foot_w - 0.1, (n0 + n1) / 2)), n1 - n0,
                       0.2, st["grating"])
        else:
            prims.beam(mb, Vector(wf.p(a_door, -0.1, out)), Vector(wf.p(a_door, foot_w - 0.1, foot_n)), 2 * sw2, 0.2, st["grating"])
    if lod != 0:
        return
    hr, hm = mm(rl["height"]), mm(rl["mid"])

    def rail(p0, p1, posts=(True, True)):
        """A top rail and a mid rail between two floor points (building frame), with a post at each chosen end."""
        p0, p1 = Vector(p0), Vector(p1)
        for h in (hr, hm):
            prims.cylinder(mb, p0 + UP * h, p1 + UP * h, 0.021 if h == hr else 0.014, 6, st["rail"], caps=(True, True))
        for p, on in ((p0, posts[0]), (p1, posts[1])):
            if on:
                prims.cylinder(mb, p, p + UP * hr, 0.021, 6, st["rail"], caps=(False, False))
    P = lambda aa, nn, w=0.0: wf.p(aa, w, nn)       # noqa: E731
    rail(P(a1, 0.05), P(a1, out))                   # the front end
    if along:
        rail(P(a1, out), P(a0, out), posts=(False, True))   # the outer edge
        with mb.grouped(LEGS):
            for nn in (n0, n1):                     # the stair's rails, one each side
                rail(P(a0, nn), P(foot_a + run, nn, foot_w + rise), posts=(nn == n0, True))
        return
    rail(P(a1, out), P(s1, out), posts=(False, True))
    rail(P(s0, out), P(a0, out))                    # the outer edge, back from the stair
    if dk:   # round the back deck to the far corner, and its end
        rail(P(a0, out), (hx, -hy - back, F), posts=(False, True))
        rail((hx, -hy - back, F), (hx, -hy - 0.05, F), posts=(False, True))
    else:
        rail(P(a0, out), P(a0, 0.05), posts=(False, True))
    with mb.grouped(LEGS):
        for aa in (s0, s1):                         # the stair's rails, one each side
            rail(P(aa, out), P(aa, foot_n - run, foot_w + rise), posts=(False, True))


def _light(a, name, lt, pos, aim):
    a.lights.append({"name": name, "kind": lt["kind"], "pos": [round(x, 4) for x in pos],
                     "aim": [round(x, 4) for x in aim], "kelvin": lt["kelvin"], "lumens": lt["lumens"],
                     "rangeM": lt["rangeM"], "coneDeg": lt["coneDeg"], "schedule": lt["schedule"]})
