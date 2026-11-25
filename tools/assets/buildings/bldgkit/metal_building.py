"""A metal-panel gable building on a concrete stem wall, from its spec (task 4.50): the pump house's builder, shared by
every building of that family (pump house, booster).

LOD0 everything; LOD1 the same shell and openings with plain fixtures; LOD2 a closed shell with the openings as flat
decals; LOD3 a box and a roof. Lights: a wall pack over each overhead door, a downlight under each man door's hood and
a cage sconce beside it, pole lights beside a transformer pad, never over a hood or a cabinet (light_* sockets and
records); window glass masked to glow while the building runs. Optional parts (an empty list or a missing key): pole
lights, transformers, vaults, ridge vents. Spec keys: pumphouse.json's `_about`.

Optional keys the garage brought (phase 5; garage.json's `_about`): a man door on a gable end (its `at` along the wall,
a stoop off the gable, no snow guards: the roof sheds to the eaves); a door's own colour (`style`) and the door as a
moving part (`part`: its leaf on a pivot at the head, so the game can roll it up); `aprons`, one pad along several
doors with bollards at its corners and between them; `lightBand`, a translucent strip under the eaves that glows with
the building; `signs`, a board the game letters, with gooseneck lights over it; `sockets` given as a `pos`; `fuel`, the
fuel pad (bldgkit/fuel_pad.py) as its own part. Every building checks that its openings stay on their wall, under the
eave and clear of each other.
"""
import contextlib

from mathutils import Vector

from liftkit import prims
from liftkit.export import Asset
from liftkit.frame import mm
from liftkit.mesh import GLASS, STRUCTURE, MeshBuilder, Style

from . import fixtures as fx
from . import fuel_pad
from .palette import LAMP, WINDOW
from .shell import WallFrame, gable_infill, gable_roof, roof_w, shed_infill, shed_roof, wall

LODS = 4


def styles():
    trim = Style("trim_bronze")
    return {
        "wall": Style("panel_tan"), "base": Style("concrete_base"), "reveal": trim, "fascia": trim, "trim": trim,
        "roof": Style("roof_charcoal", snow=0.6), "cap": Style("roof_charcoal", snow=0.6),
        "door": Style("door_panel"), "slat": Style("door_panel"), "groove": Style("louvre_dark"),
        "glass": Style("window_glass", livery=WINDOW, slot=GLASS),
        "louvre": Style("louvre_dark"), "blade": Style("louvre_dark", snow=0.2),
        "housing": Style("fixture_bronze", snow=0.5), "lens": Style("lamp_lens", livery=LAMP),
        "cabinet": Style("transformer_green", snow=1.0), "pad": Style("concrete_base", snow=1.0),
        "vent": Style("roof_charcoal", snow=0.6), "hood": Style("roof_charcoal", snow=0.8),
        "bollard": Style("safety_yellow"), "pole": Style("galvanised", snow=0.0), "guard": Style("trim_bronze", snow=0.4),
        "kick": Style("galvanised"), "seal": Style("louvre_dark"), "sill": Style("trim_bronze", snow=0.8),
        "band": Style("panel_translucent", livery=WINDOW, slot=GLASS), "sign": Style("louvre_dark", snow=0.3),
        "tank": Style("tank_white", snow=0.6), "saddle": Style("galvanised", snow=0.4),
        "dispenser": Style("panel_grey", snow=0.6), "hose": Style("louvre_dark"), "curb": Style("safety_yellow", snow=1.0),
    }


def _at(d):
    """A door's place along its wall: `u` on a long side, `at` (along v) on a gable."""
    return d["at"] if "at" in d else d["u"]


def _part(mb, d):
    """A door's leaf as a moving part (`part`: true), or plain body."""
    return mb.grouped(d["name"]) if d.get("part") else contextlib.nullcontext()


def _door_styles(st, d):
    """A door painted its own colour (`style`, a palette class); its frame stays trim."""
    if "style" not in d:
        return st
    return {**st, "door": Style(d["style"]), "slat": Style(d["style"])}


class Side:
    """One wall: its frame, its length, and how a building coordinate along it becomes `a` on it."""

    def __init__(self, key, hx, hy):
        self.key = key
        if key in ("+v", "-v"):
            s = 1 if key == "+v" else -1
            self.wf = WallFrame((-hx, s * hy, 0.0), (1.0, 0.0, 0.0), (0.0, s, 0.0))
            self.length, self.half = 2 * hx, hx
        else:
            s = 1 if key == "+u" else -1
            self.wf = WallFrame((s * hx, -hy, 0.0), (0.0, 1.0, 0.0), (s, 0.0, 0.0))
            self.length, self.half = 2 * hy, hy
        self.openings, self.fill = [], []

    def a(self, at_mm):
        return mm(at_mm) + self.half


def _row(item):
    n = item["count"]
    if n == 1:
        return [item["first"]]
    step = (item["last"] - item["first"]) / (n - 1)
    return [item["first"] + k * step for k in range(n)]


def build(spec, lod, asset_id):
    st = styles()
    a = Asset(asset_id)
    mb = MeshBuilder()
    hx, hy = mm(spec["footprint"]["length"]) / 2, mm(spec["footprint"]["width"]) / 2
    eave = mm(spec["eave"])
    k = spec["pitch"][0] / spec["pitch"][1]
    r = spec["roof"]
    thick, over, rake = mm(r["thick"]), mm(r["overhang"]), mm(r["rake"])
    # `shed`: the high side of a single-slope roof (the eave is that wall's top; the roof falls `pitch` to the other)
    hs = {"+v": 1, "-v": -1}[spec["shed"]] if spec.get("shed") else 0
    low = eave - 2 * hy * k if hs else eave
    apex = eave if hs else eave + hy * k
    base_top, skirt = mm(spec["base"]["top"]), mm(spec["base"]["skirt"])
    a.dims.update({"footprint": [spec["footprint"]["length"], spec["footprint"]["width"]], "eave": spec["eave"],
                   "ridge": round((apex) * 1000), "ridgeTop": round((apex + thick) * 1000)})
    if hs:
        a.dims["lowEave"] = round(low * 1000)

    def top_of(key, at=None):
        """A wall's top (the roof's underside on its line) at `at` metres along it; a gable's at its eave line."""
        if not hs:
            return eave
        if key[1] == "v":
            return eave if hs == (1 if key[0] == "+" else -1) else low
        return eave - (hy - hs * ((at if at is not None else 0.0) - hy)) * k

    def infill(sd):
        if hs:
            shed_infill(mb, sd.wf, sd.length, low, eave, 1 if hs > 0 else 0, st["wall"])
        else:
            gable_infill(mb, sd.wf, sd.length, eave, apex, st["wall"])

    def wall_bands(key):
        return [(skirt, base_top, st["base"]), (base_top, top_of(key) if key[1] == "v" else low, st["wall"])]
    a.lights, a.glow, a.problems = [], dict(spec["lights"]["glow"]), []

    if lod == 3:   # a box and a roof: walls to the eave, gable triangles, two roof planes on the wall line
        sides = {key: Side(key, hx, hy) for key in ("+v", "-v", "+u", "-u")}
        for key, sd in sides.items():
            wall(mb, sd.wf, sd.length, wall_bands(key), [], st["reveal"])
            if key in ("+u", "-u"):
                infill(sd)
        if hs:
            pts = [(-hx, hs * hy, eave), (hx, hs * hy, eave), (hx, -hs * hy, low), (-hx, -hs * hy, low)]
            mb.face(mb.verts_lift(pts), st["roof"], (0.0, -hs * k, 1.0))
        for s in (-1, 1) if not hs else ():
            pts = [(-hx, 0.0, apex), (hx, 0.0, apex), (hx, s * hy, eave), (-hx, s * hy, eave)]
            mb.face(mb.verts_lift(pts), st["roof"], (0.0, s * k, 1.0))
        if spec.get("fuel"):
            _fuel(a, mb, spec, st, lod)
        a.body = mb
        _sockets(a, spec, hx, hy, sides=None)
        return a

    sides = {key: Side(key, hx, hy) for key in ("+v", "-v", "+u", "-u")}
    doors = {d["name"]: d for d in spec["doors"]}
    for d in spec["doors"]:
        sd = sides[d["side"]]
        c, w2 = sd.a(_at(d)), mm(d["width"]) / 2
        sd.openings.append((c - w2, c + w2, 0.0, mm(d["height"])))
        sd.fill.append(("door", d, c))
    for row in spec["windows"]:
        sd = sides[row["side"]]
        for at in _row(row):
            c, w2 = sd.a(at), mm(row["width"]) / 2
            box = (c - w2, c + w2, mm(row["sill"]), mm(row["sill"] + row["height"]))
            sd.openings.append(box)
            sd.fill.append(("window", box, None))
    for lv in spec["louvres"]:
        sd = sides[lv["side"]]
        c, w2 = sd.a(lv["at"]), mm(lv["width"]) / 2
        box = (c - w2, c + w2, mm(lv["sill"]), mm(lv["sill"] + lv["height"]))
        sd.openings.append(box)
        sd.fill.append(("louvre", box, None))

    for key, sd in sides.items():   # every opening on its wall, under the eave, clear of the corners and of the others
        ops = sorted(sd.openings)
        for o in ops:
            if o[0] < 0.15 or o[1] > sd.length - 0.15:
                a.problems.append(f"an opening on {key} at {o[0]:.2f}-{o[1]:.2f} m runs off its wall")
            if o[3] > min(top_of(key, o[0]), top_of(key, o[1])) - 0.1:
                a.problems.append(f"an opening on {key} at {o[0]:.2f}-{o[1]:.2f} m reaches the eave")
        for i, p in enumerate(ops):
            for q in ops[i + 1:]:
                if q[0] < p[1] + 0.1 and p[0] < q[1] + 0.1 and q[2] < p[3] + 0.1 and p[2] < q[3] + 0.1:
                    a.problems.append(f"openings on {key} at {p[0]:.2f} and {q[0]:.2f} m overlap")

    reveal = mm(spec["reveal"])
    for key, sd in sides.items():
        wall(mb, sd.wf, sd.length, wall_bands(key), sd.openings if lod <= 1 else [], st["reveal"], depth=reveal)
        if key in ("+u", "-u"):
            infill(sd)
        for kind, item, c in sd.fill:
            if kind == "door":
                dst = _door_styles(st, item)
                if item["kind"] == "rollup":
                    fx.rollup_door(mb, sd.wf, c, mm(item["width"]), mm(item["height"]), reveal, dst, lod, item.get("slats", 8),
                                   leaf=_part(mb, item))
                    if item.get("part"):   # the leaf rolls up about its head
                        a.pivots[item["name"]] = {"pos": sd.wf.p(c, mm(item["height"]), 0.0), "axis": tuple(sd.wf.ea)}
                else:
                    fx.man_door(mb, sd.wf, c, mm(item["width"]), mm(item["height"]), reveal * 0.6, dst, lod,
                                item.get("halfLight", False))
                    cn = spec["canopy"]
                    w_top, proj = mm(item["height"] + cn["rise"]), mm(cn["projection"])
                    if lod <= 1:   # a hood over every man door
                        fx.door_canopy(mb, sd.wf, c, mm(item["width"] + 2 * cn["over"]), w_top, proj, st, lod)
                    # and its downlight underneath (drawn where the hood is; the record at every level)
                    pos, aim = fx.canopy_light(mb, sd.wf, c, w_top, proj, st, lod)
                    _light(a, "light_" + item["name"], spec["lights"]["canopy"], pos, aim)
            elif kind == "window":
                fx.window(mb, sd.wf, *item, reveal * 0.6, st, lod)
            else:
                fx.louvre(mb, sd.wf, *item, reveal, st, lod)

    band = _light_band(mb, spec, sides, top_of, st, lod, a)
    _signs(a, mb, spec, sides, band, st, lod)

    if hs:   # a single slope: no ridge, so no cap or ridge vents
        shed_roof(mb, hx, hy, hs, eave, k, thick, over, rake, st["roof"], st["fascia"])
    else:
        ridge_top = gable_roof(mb, hx, hy, eave, k, thick, over, rake, st["roof"], st["fascia"])
        cw, ch = mm(r["ridgeCap"][0]), mm(r["ridgeCap"][1])
        if lod <= 2:
            fx.ridge_cap(mb, hx + rake, ridge_top, cw, ch, st["cap"])
    vents = spec.get("ridgeVents") if not hs else None
    for at in vents["at"] if vents else ():
        size = tuple(mm(x) for x in vents["size"])
        if lod <= 2:
            fx.ridge_vent(mb, mm(at), ridge_top, size, st, lod)

    if lod <= 1:   # corner trim, and at LOD0 the flashing on the stem wall's top
        ct = mm(spec["cornerTrim"])
        for su in (-1, 1):
            for sv in (-1, 1):
                fx.corner_trim(mb, (su * hx, sv * hy), ct, base_top, top_of("+v" if sv > 0 else "-v"), st["trim"])
    if lod == 0:
        fl = mm(spec["base"]["flashing"])
        for key, sd in sides.items():
            floor_doors = sorted((o[0], o[1]) for o in sd.openings if o[2] <= 0.0)
            start = 0.0
            for d0, d1 in floor_doors + [(sd.length, sd.length)]:
                if d0 - start > 0.05:
                    lo = Vector(sd.wf.p(start, base_top - fl / 2, 0.0))
                    hi = Vector(sd.wf.p(d0, base_top + fl / 2, 0.03))
                    prims.box(mb, tuple(map(min, lo, hi)), tuple(map(max, lo, hi)), st["trim"].but(snow=0.3),
                              skip=fx._back(sd.wf))
                start = d1

    # cage sconces on the wall beside the man doors, clear of the hood's side
    for sc in spec["sconces"]:
        d = doors[sc["door"]]
        sd, cn = sides[d["side"]], spec["canopy"]
        off = mm(d["width"] / 2 + cn["over"] + sc["beyond"])
        a_s = sd.a(_at(d)) + (off if sc["at"] == "latch" else -off)
        w_s = mm(sc["height"])
        for o in sd.openings:   # never on a window, door or louvre
            if o[0] - 0.1 < a_s < o[1] + 0.1 and o[2] - 0.2 < w_s < o[3] + 0.2:
                a.problems.append(f"{sc['name']} sits on an opening at {o[0]:.2f}-{o[1]:.2f} m")
        pos, aim = fx.cage_sconce(mb, sd.wf, a_s, w_s, st, lod)
        _light(a, sc["name"], spec["lights"]["sconce"], pos, aim)

    # wall packs over doors with no hood (a light never sits on top of a hood or over a cabinet)
    for wp in spec["wallPacks"]:
        d = doors[wp["over"]]
        sd, c, bottom = sides[d["side"]], sides[d["side"]].a(_at(d)), mm(d["height"]) + mm(wp["gap"])
        if d["kind"] == "man":
            a.problems.append(f"{wp['name']}: a man door has a hood, and its light goes under it (canopy_light)")
        for o in sd.openings:   # its conduit runs up the wall to the eave: never across a window, door or louvre
            if o[0] - 0.05 < c < o[1] + 0.05 and o[3] > bottom:
                a.problems.append(f"{wp['name']}'s conduit crosses an opening at {o[0]:.2f}-{o[1]:.2f} m")
        top = top_of(d["side"], c)
        if d["side"] in band and band[d["side"]][0] < c < band[d["side"]][1]:   # the conduit goes in under the band
            top = band[d["side"]][2]
        pos, aim = fx.wall_pack(mb, sd.wf, c, bottom, st, lod, conduit_to=top)
        _light(a, wp["name"], spec["lights"]["wallPack"], pos, aim)

    # the transformer pad and its cabinets along a long side
    tr = spec.get("transformers")
    if tr:
        s = 1 if tr["side"] == "+v" else -1
        pd = tr["pad"]
        v0, v1 = sorted((s * (hy + mm(pd["from"])), s * (hy + mm(pd["to"]))))
        ptop = mm(pd["top"])
        fx.pad(mb, (mm(pd["uFrom"]), v0, -0.1), (mm(pd["uTo"]), v1, ptop), st["pad"])
        ex, ey = mm(tr["half"][0]), mm(tr["half"][1])
        vc = s * (hy + mm(tr["gap"]) + ey)
        for at in tr["at"]:
            fx.transformer(mb, (mm(at), vc, ptop), "u", (ex, ey), mm(tr["height"]), st, 0 if lod == 0 else 1)

    # pole lights in the yard: beside the pad, lighting the cabinets' fronts from the side, nothing over them
    for pl in spec.get("poleLights", []):
        ps = 1 if pl["side"] == "+v" else -1
        base = (mm(pl["at"]), ps * (hy + mm(pl["out"])))
        arm = (mm(pl["arm"][0]), ps * mm(pl["arm"][1]))
        tgt = Vector((mm(pl["aimAt"][0]), ps * (hy + mm(pl["aimAt"][1])), mm(pl["aimAt"][2])))
        head = fx.pole_light(mb, base, mm(pl["height"]), arm, st, lod)
        _light(a, pl["name"], spec["lights"]["pole"], head, tuple((tgt - Vector(head)).normalized()))
        hu, hv = base[0] + arm[0], abs(base[1] + arm[1]) - hy
        for at in tr["at"]:   # the head stays clear of every cabinet, seen from above
            if abs(hu - mm(at)) < ex + 0.3 and mm(tr["gap"]) - 0.3 < hv < mm(tr["gap"]) + 2 * ey + 0.3:
                a.problems.append(f"{pl['name']}'s head is over a transformer")

    if spec.get("fuel"):
        _fuel(a, mb, spec, st, lod)

    if lod <= 1:
        # the pipes' valve vault lids outside the walls, the apron at the big door, stoops at the man doors
        for vt in spec.get("vaults", []):
            vs = mm(vt["size"]) / 2
            sg = 1 if vt["side"][0] == "+" else -1
            if vt["side"][1] == "v":
                vx, vy = mm(vt["at"]), sg * (hy + mm(vt["out"]))
            else:
                vx, vy = sg * (hx + mm(vt["out"])), mm(vt["at"])
            fx.pad(mb, (vx - vs, vy - vs, -0.1), (vx + vs, vy + vs, mm(vt["top"])), st["pad"])
            if lod == 0:   # its hatch
                prims.box(mb, (vx - 0.45, vy - 0.45, mm(vt["top"])), (vx + 0.45, vy + 0.45, mm(vt["top"]) + 0.02),
                          Style("grating", snow=1.0), skip=("-2",))
        aprons = spec["aprons"] if "aprons" in spec else [{**spec["apron"], "doors": [spec["apron"]["door"]]}]
        posts = []
        for ap in aprons:   # one pad along its doors, out from their wall
            ds = [doors[n] for n in ap["doors"]]
            sv = 1 if ds[0]["side"] == "+v" else -1
            if any(d["side"] != ds[0]["side"] or d["side"][1] != "v" for d in ds):
                a.problems.append(f"an apron's doors {ap['doors']} are not all on one long side")
            half = [mm(d["width"]) / 2 + mm(ap["margin"]) for d in ds]
            u0 = min(mm(d["u"]) - h for d, h in zip(ds, half))
            u1 = max(mm(d["u"]) + h for d, h in zip(ds, half))
            lo_v, hi_v = sorted((sv * hy, sv * (hy + mm(ap["out"]))))
            fx.pad(mb, (u0, lo_v, -0.15), (u1, hi_v, 0.02), st["pad"])
            at = sorted(mm(d["u"]) + off * (h + 0.15) for d, h in zip(ds, half) for off in (-1, 1))
            merged = []   # a door's bollard and its neighbour's become one between them
            for u in at:
                if merged and u - merged[-1][-1] < 0.6:
                    merged[-1].append(u)
                else:
                    merged.append([u])
            posts += [(sum(m) / len(m), sv * (hy + 0.45)) for m in merged]
        for dd in spec["doors"]:
            if dd["kind"] != "man":
                continue
            sx, sy = mm(spec["stoop"]["size"][0]) / 2, mm(spec["stoop"]["size"][1])
            if dd["side"][1] == "u":   # a stoop off a gable
                ssu = 1 if dd["side"] == "+u" else -1
                lo_u, hi_u = sorted((ssu * hx, ssu * (hx + sy)))
                fx.pad(mb, (lo_u, mm(_at(dd)) - sx, -0.15), (hi_u, mm(_at(dd)) + sx, mm(spec["stoop"]["top"])), st["pad"])
                continue
            ssv = 1 if dd["side"] == "+v" else -1
            lo_v, hi_v = sorted((ssv * hy, ssv * (hy + sy)))
            fx.pad(mb, (mm(dd["u"]) - sx, lo_v, -0.15), (mm(dd["u"]) + sx, hi_v, mm(spec["stoop"]["top"])), st["pad"])
        if lod == 0:
            for p in posts:   # bollards at the big doors' corners
                fx.bollard(mb, p, st)
            # snow guards on the roof over every door, so the roof doesn't dump on whoever walks out
            g = spec["snowGuards"]
            for dd in spec["doors"]:
                if dd["side"][1] != "v":   # a gable door: the roof sheds to the eaves, not over it
                    continue
                if hs and dd["side"] == spec["shed"]:   # under a single slope's high side: the roof sheds away from it
                    continue
                ssv = 1 if dd["side"] == "+v" else -1
                vg = ssv * (hy - mm(g["inset"]))
                wg = roof_w(vg, hy, eave, k, thick) if not hs else eave - (hy - hs * vg) * k + thick
                u0 = mm(dd["u"]) - mm(dd["width"]) / 2 - mm(g["beyond"])
                u1 = mm(dd["u"]) + mm(dd["width"]) / 2 + mm(g["beyond"])
                n = int((u1 - u0) / mm(g["pitch"])) + 1
                for i in range(n):
                    u = u0 + (u1 - u0) * i / max(1, n - 1)
                    prims.box(mb, (u - 0.03, vg - 0.05, wg - 0.03), (u + 0.03, vg + 0.05, wg + 0.07), st["guard"], skip=("-2",))

    a.body = mb
    _sockets(a, spec, hx, hy, sides)
    a.dims.update({"doors": {d["name"]: [d["width"], d["height"]] for d in spec["doors"]},
                   "windows": sum(r["count"] for r in spec["windows"]), "louvres": len(spec["louvres"]),
                   "lights": len(a.lights)})
    return a


def _light_band(mb, spec, sides, top_of, st, lod, a):
    """The translucent light band (`lightBand`) along its walls just under the eave: a strip of polycarbonate panel on the
    wall, glowing with the building's glow (mask 4), with a trim edge above and below (LOD0-1); a strip at LOD2. Returns
    {side: (a from, a to, bottom)} for the wall packs' conduits, which go in under it."""
    lb = spec.get("lightBand")
    out = {}
    if not lb:
        return out
    for key in lb["sides"]:
        sd = sides[key]
        a0, a1 = mm(lb["inset"]), sd.length - mm(lb["inset"])
        w0, w1 = mm(lb["bottom"]), mm(lb["bottom"] + lb["height"])
        out[key] = (a0, a1, w0)
        if w1 > min(top_of(key, a0), top_of(key, a1)) - 0.03:
            a.problems.append(f"the light band on {key} runs up past the eave")
        for o in sd.openings:
            if o[3] > w0 - 0.05:
                a.problems.append(f"the light band on {key} crosses an opening at {o[0]:.2f} m")
        if lod > 2:
            continue
        fx.panel(mb, sd.wf, a0, a1, w0, w1, 0.012, st["band"])
        if lod <= 1:
            for b0, b1 in ((w0 - 0.05, w0), (w1, w1 + 0.04)):
                fx._wbox(mb, sd.wf, a0 - 0.02, a1 + 0.02, b0, b1, 0.0, 0.03, st["trim"].but(snow=0.3))
    return out


def _signs(a, mb, spec, sides, band, st, lod):
    """Sign boards (`signs`) on a wall: a dark board standing off the wall that the game letters (its text and size in
    dims; the socket at its face's centre), with gooseneck lights over it (`lights`: how many, spread across it). LOD0-1
    the board and lights; LOD2 a decal; the light records at every level."""
    for sg in spec.get("signs", []):
        sd = sides[sg["side"]]
        c, h2 = sd.a(sg["at"]), mm(sg["width"]) / 2
        w0, w1, dp = mm(sg["bottom"]), mm(sg["bottom"] + sg["height"]), mm(sg["depth"])
        for o in sd.openings:
            if o[0] - 0.1 < c + h2 and c - h2 < o[1] + 0.1 and o[2] - 0.1 < w1 and w0 < o[3] + 0.1:
                a.problems.append(f"{sg['name']} covers an opening at {o[0]:.2f} m")
        if sg["side"] in band and w1 > band[sg["side"]][2] - 0.02:
            a.problems.append(f"{sg['name']} runs up into the light band")
        if lod <= 1:
            fx._wbox(mb, sd.wf, c - h2, c + h2, w0, w1, 0.0, dp, st["sign"])
            if lod == 0:   # a trim edge round its face
                for b0, b1, c0, c1 in ((c - h2, c + h2, w1 - 0.04, w1), (c - h2, c + h2, w0, w0 + 0.04),
                                       (c - h2, c - h2 + 0.04, w0 + 0.04, w1 - 0.04), (c + h2 - 0.04, c + h2, w0 + 0.04, w1 - 0.04)):
                    fx._wbox(mb, sd.wf, b0, b1, c0, c1, dp, dp + 0.012, st["trim"])
        elif lod == 2:
            fx.panel(mb, sd.wf, c - h2, c + h2, w0, w1, 0.012, st["sign"])
        a.sockets[sg["name"]] = tuple(sd.wf.p(c, (w0 + w1) / 2, dp))
        a.dims.setdefault("signs", {})[sg["name"]] = {"text": sg["text"], "size": [sg["width"], sg["height"]],
                                                       "facing": [round(x, 4) for x in sd.wf.en]}
        gl = sg.get("lights")
        if gl:
            n = gl["count"]
            for i in range(n):
                ai = c - h2 + (i + 0.5) * 2 * h2 / n
                pos, aim = fx.gooseneck(mb, sd.wf, ai, mm(gl["mount"]), mm(gl["reach"]), w1, st, lod)
                _light(a, f"{gl['name']}_{i + 1}", spec["lights"]["sign"], pos, aim)


def _fuel(a, mb, spec, st, lod):
    """The fuel pad (bldgkit/fuel_pad.py) as the `fuel` part on a pivot at its centre, so the game can leave it out."""
    with mb.grouped("fuel"):
        made = fuel_pad.build(mb, spec["fuel"], st, lod, a)
    a.pivots["fuel"] = {"pos": made["centre"], "axis": (0.0, 0.0, 1.0)}
    for name, (pos, aim) in made["lights"].items():
        _light(a, name, spec["lights"]["pole"], pos, aim)
    for name, pos in made["sockets"].items():
        a.sockets[name] = pos
    a.dims["fuel"] = made["dims"]


def _light(a, name, lt, pos, aim):
    a.lights.append({"name": name, "kind": lt["kind"], "pos": [round(x, 4) for x in pos],
                     "aim": [round(x, 4) for x in aim], "kelvin": lt["kelvin"], "lumens": lt["lumens"],
                     "rangeM": lt["rangeM"], "coneDeg": lt["coneDeg"], "schedule": lt["schedule"]})


def _sockets(a, spec, hx, hy, sides):
    """Sockets in the building frame: the pipe ends (buried, on their walls; the spec's `sockets` other than `label`),
    each door's sill outside, the name label, and every light."""
    sk = spec["sockets"]
    half = {"+v": hy, "-v": hy, "+u": hx, "-u": hx}

    def on_side(side, at, w):
        sgn = 1 if side[0] == "+" else -1
        if side[1] == "v":
            return (mm(at), sgn * half[side], w)
        return (sgn * half[side], mm(at), w)
    for name, at in sk.items():
        if name == "label":
            continue
        if "pos" in at:   # a socket given in the building frame (a bay's floor, where a machine stands)
            a.sockets[name] = tuple(mm(x) for x in at["pos"])
        else:
            a.sockets[name] = on_side(at["side"], at["at"], mm(at["w"]))
    for d in spec["doors"]:
        a.sockets[d["name"]] = on_side(d["side"], _at(d), 0.0)
    a.sockets["label"] = (0.0, 0.0, mm(sk["label"]["w"]))
    for lt in a.lights:
        a.sockets[lt["name"]] = tuple(lt["pos"])
