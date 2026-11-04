"""Builds the lift assets in Blender, with no manual steps (decisions LP1-LP8).

    blender -b --factory-startup --python-exit-code 1 --python tools/assets/lifts/build_lifts.py -- \
        [--out DIR] [--assets drive,return,chair] [--stage blockout|detail] [--render DIR] [--shots a,b] [--ao off] [--detail off]

Each asset is written in the lift frame (liftkit/frame.py) from the dimensions in sessellift_fgq4.json and
exported as one FBX: <id>_LOD0..N bodies, moving parts <id>_<part>_LODn under <id>_pivot_<part> empties
(origin on the real axle), and <id>_socket_<name> empties. The build fails (non-zero exit) if any LOD is
over its triangle budget (budgets.json), totals don't decrease, a moving part is off its axle, or a rope
socket is off the rope. out/lifts.json records triangles, sockets, pivots, dimensions and mesh hashes.
"""
import importlib
import json
import math
import os
import sys
import time
import types

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "assets"))
from liftkit import ao, clash, export, fit, frame, gauge, materials, palette, swap, textures  # noqa: E402

MODULES = {"drive": "drive_terminal", "return": "return_terminal", "chair": "chair", "sle_ground_gun": "sle_ground_gun",
           "terminal_footing": "terminal_footing",
           "monta_drive": "monta_drive", "monta_return": "monta_return", "monta_return_bottom": "monta_return_bottom",
           # name -> (module, variant): the stick gun's lance in feet of pipe
           "sle_stick_gun_10": ("sle_stick_gun", 10), "sle_stick_gun_20": ("sle_stick_gun", 20),
           "sle_stick_gun_30": ("sle_stick_gun", 30),
           # the Chairworks quad chair with either grip
           "chairworks_chair_detach": ("chairworks_chair", "detach"), "chairworks_chair_fixed": ("chairworks_chair", "fixed"),
           # the Sessellift D6 detachable six-pack (lift models 2)
           "d6_chair": "d6_chair", "d6_drive": ("d6_terminal", "drive"), "d6_return": ("d6_terminal", "return"),
           # the narrowed Chairworks chairs for the fixed-grip doubles and triples (v0.3.2)
           "chairworks_chair_double_fixed": ("chairworks_chair", "double_fixed"),
           "chairworks_chair_triple_fixed": ("chairworks_chair", "triple_fixed"),
           # the Sessellift Stern, the second fixed-grip style: bottom drive, top return (owner, 2026-11-02)
           "stern4_drive": "stern_drive", "stern4_return": "stern_return"}
# the fixed-grip doubles and triples (v0.3.2): the quads' modules built from an overlay spec (liftkit/gauge.py):
# name -> (module, spec, variant or None)
OVERLAY = {}
for model in ("fgd2", "fgt3"):
    over = f"sessellift_{model}.json"
    OVERLAY.update({f"{model}_chair": ("chair", over, None), f"{model}_drive": ("drive_terminal", over, None),
                    f"{model}_return": ("return_terminal", over, None)})
    OVERLAY.update({f"{model}_tower_{h}": ("tower", over, h) for h in ("s4", "s6", "b8", "d8", "c8")})
for model in ("fg2", "fg3"):
    OVERLAY.update({f"monta_{model}_drive": ("monta_drive", f"monta_{model}.json", None),
                    f"monta_{model}_return": ("monta_return", f"monta_{model}.json", None),
                    f"monta_{model}_return_bottom": ("monta_return_bottom", f"monta_{model}.json", None)})
for model in ("stern3", "stern2"):   # the Stern's triple and double, its own gauge rule (assets/stern_heads.py)
    OVERLAY.update({f"{model}_drive": ("stern_drive", f"sessellift_{model}.json", None),
                    f"{model}_return": ("stern_return", f"sessellift_{model}.json", None)})
# the Sessellift D4 detachable quad (v0.3.2, owner DQ1-DQ9): the D6's modules built from the D4's overlay spec
OVERLAY.update({"d4_chair": ("d6_chair", "sessellift_d4.json", None), "d4_bottom": ("d6_terminal", "sessellift_d4.json", "bottom"),
                "d4_top": ("d6_terminal", "sessellift_d4.json", "top")})
OVERLAY.update({f"d4_tower_{h}": ("tower", "sessellift_d4_towers.json", h) for h in ("s4", "s6", "b8", "d8", "c8")})   # DQ2
# the chairs an overlay's terminals send round their bullwheels: its own Sessellift chair and the Chairworks one (by seats)
FIT_CHAIRS = {2: ("sessellift_fgd2.json", "chairworks_chair:double_fixed"), 3: ("sessellift_fgt3.json", "chairworks_chair:triple_fixed")}
# the D6's wide heads (assets/tower_wide.py): one per Sessellift head type, at the D6's gauge
MODULES.update({f"d6_tower_{h}": ("tower_wide", h) for h in ("s4", "s6", "s10", "b10", "d10", "c8")})
# the line tower kit (assets/tower.py): name -> variant
TOWER = {"tower_s4": "s4", "tower_s6": "s6", "tower_b8": "b8", "tower_d8": "d8", "tower_c8": "c8",
         "tower_mast": "mast", "tower_base": "base"}
# TW-14's leaned bases (task P3-07): every 2.5 degrees to 15
TOWER.update({f"tower_base_l{d:03d}": f"base_l{d:03d}" for d in range(25, 151, 25)})
SLE = ("sle_stick_gun_10", "sle_stick_gun_20", "sle_stick_gun_30", "sle_ground_gun")
MONTA = ("monta_drive", "monta_return", "monta_return_bottom")
CHAIRWORKS = ("chairworks_chair_detach", "chairworks_chair_fixed", "chairworks_chair_double_fixed", "chairworks_chair_triple_fixed")
STERN = ("stern4_drive", "stern4_return")
D6 = ("d6_drive", "d6_return", "d6_chair") + tuple(f"d6_tower_{h}" for h in ("s4", "s6", "s10", "b10", "d10", "c8"))
# the Monta T-bar (surface lifts): towers (assets/tbar.py), terminals and carriers; ids monta_tbar_<name>
MODULES.update({f"monta_tbar_{v}": ("tbar", v) for v in ("tower_s4", "tower_s6", "tower_d4", "mast", "base")})
MODULES.update({"monta_tbar_drive": ("tbar_terminal", "drive"), "monta_tbar_return": ("tbar_terminal", "return"),
                "monta_tbar_hut": ("tbar_terminal", "hut"),
                "monta_tbar_springbox": ("tbar_carrier", "springbox"), "monta_tbar_bar_t": ("tbar_carrier", "bar_t"),
                "monta_tbar_bar_j": ("tbar_carrier", "bar_j")})
TBAR = tuple(k for k in MODULES if k.startswith("monta_tbar_"))
ALL = ("drive,return,terminal_footing,chair," + ",".join(TOWER) + "," + ",".join(SLE) + "," + ",".join(MONTA) + "," + ",".join(CHAIRWORKS)
       + "," + ",".join(D6) + "," + ",".join(STERN) + "," + ",".join(OVERLAY) + "," + ",".join(TBAR))


def clear_scene():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    for c in list(bpy.data.collections):
        bpy.data.collections.remove(c)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)


def stopped(rng, stop):
    """A pivot's clash range clamped to its mechanical stops (degrees either way), when it has them."""
    if stop is None:
        return rng
    return [max(rng[0], -stop), min(rng[1], stop)]


def ao_options(kind, assets):
    """(ground plane?, stacking section height or None) for an asset's AO bake. The ground plane only goes under what
    stands on it at its own 0.00 (contract M5, finding #9): heads sit on a mast, the D6's terminals on piers of any
    height (the FGQ-4's are baked at their standard grade, polish 2026-10-23); a mast section is baked between its neighbours (a cast-only ghost section above and one below, so
    both ends get the same light)."""
    ground = kind not in ("tower_head", "tower_head_wide", "tower_mast", "chair", "chair_detach", "terminal_footing",
                          "surface_tower_head", "surface_mast", "surface_carrier")         and not (kind == "terminal_detach" and "pier_top_1" in assets[0].sockets)
    return ground, (assets[0].sockets["top"][2] if kind in ("tower_mast", "surface_mast") else None)


def views(a):
    """(head key, asset as that head shows it) for each of a terminal's heads, its default first (liftkit/swap.py), or
    (None, a) without heads."""
    return [(k, swap.view(a, k)) for k in swap.keys(a)] or [(None, a)]


def head_totals(a, report):
    """A terminal with two heads draws one at a time: per LOD, the triangles shown with each head (the body, the
    parts of neither head and that head's), and the larger as the LOD's shown total (liftkit/swap.py)."""
    for e in report["lods"]:
        e["shown"] = e["total"]
        if a.heads:
            e["heads"] = {end: e["total"] - sum(t for k, t in e["parts"].items() if k in swap.other_parts(a, end))
                          for end in a.heads}
            e["shown"] = max(e["heads"].values())


def sweep_heads(a):
    """The clash sweep (liftkit/clash.py) with each head shown in turn; a pivot both views hold (the bullwheel, the
    carriage) keeps the narrower range of the two."""
    out = {}
    for _, v in views(a):
        for k, r in (clash.sweep(v) if v.pivots else {}).items():
            if k not in out:
                out[k] = r
            else:
                lo, hi = out[k]["range"]
                by = list(out[k]["by"])
                if r["range"][0] > lo:
                    lo, by[0] = r["range"][0], r["by"][0]
                if r["range"][1] < hi:
                    hi, by[1] = r["range"][1], r["by"][1]
                out[k] = {"range": [lo, hi], "by": by}
    return out


def check_asset(kind, assets, report, budgets, spec, split=None):
    problems = []
    caps = budgets[kind]["lods"]
    if len(assets) != len(caps):
        problems.append(f"{len(assets)} LODs built, budget lists {len(caps)}")
    head_totals(assets[0], report)
    totals = [e["shown"] for e in report["lods"]]
    for e, cap in zip(report["lods"], caps):
        if e["shown"] > cap["maxTris"]:
            problems.append(f"LOD{e['lod']}: {e['shown']:,} tris shown > {cap['maxTris']:,}")
    for a, b in zip(totals, totals[1:]):
        if b >= a:
            problems.append(f"LOD totals must decrease: {totals}")
            break
    err = export.pivot_centering_error(assets[0], split)
    if err > 0.001:
        problems.append(f"a wheel is {err * 1000:.1f} mm off its axle")
    for lod, (_, parts) in enumerate(split):
        for name in parts:
            parent = assets[0].pivots[name].get("parent")
            if parent and parent not in assets[0].pivots:
                problems.append(f"LOD{lod}: {name}'s parent {parent} has no pivot")
    for end, a in (views(assets[0]) if kind in ("terminal", "terminal_detach", "surface_terminal") else []):
        c = spec["common"]
        at = f" ({end} head)" if end else ""
        line_v = a.sockets.get("line", (0, 0, 0))[1]
        rope = a.dims.get("rope", c.get("ropeElevation", 0)) / 1000   # a terminal's own rope height, if it has one
        for name, p in a.sockets.items():
            if name.startswith("rope_"):
                # on the line gauge; level at the rope elevation, except where the rope leaves a row levelled at
                # its first sheave (the out sockets of a terminal with hold sockets): climbing out of a bottom
                # station or arriving from below at a top one, within a metre and a half
                off_gauge = abs(abs(p[1] - line_v) - c["lineGauge"] / 2000) > 0.001
                if name.endswith("_out") and f"{name[:-4]}_hold" in a.sockets:
                    off_height = not (0.001 < abs(p[2] - rope) < 1.5)
                elif name.endswith("_bw") and "bullwheelW" in a.dims:   # a lowered, tilted wheel (the D6)
                    off_height = abs(p[2] - a.dims["bullwheelW"] / 1000) > 0.001
                else:
                    off_height = abs(p[2] - rope) > 0.001
                if off_gauge or off_height:
                    problems.append(f"socket {name}{at} at {p} is off the rope")
    if kind in ("tower_head", "tower_head_wide", "surface_tower_head"):   # ropes at the line gauge, level with each other, at the head's declared height
        g = spec["common"]["lineGauge"] / 2000
        rope = assets[0].dims["rope"] / 1000
        for name in ("rope_left", "rope_right"):
            p = assets[0].sockets[name]
            if abs(abs(p[1]) - g) > 0.001 or abs(p[2] - rope) > 0.001:
                problems.append(f"socket {name} at {p} is off the rope")
    return problems


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {argv[i][2:]: argv[i + 1] for i in range(0, len(argv) - 1, 2) if argv[i].startswith("--")}
    out_dir = os.path.abspath(opts.get("out", os.path.join(HERE, "out")))
    tex_dir = os.path.join(out_dir, "textures")
    os.makedirs(tex_dir, exist_ok=True)
    stage = opts.get("stage", "detail")
    # the overlays' Sessellift assets go by their short names (fgd2_chair, d4_chair) or their ids (sessellift_fgd2_chair)
    names = [n[len("sessellift_"):] if n.startswith("sessellift_") and n[len("sessellift_"):] in OVERLAY else n
             for n in opts.get("assets", ALL).split(",")]
    unknown = [n for n in names if n not in TOWER and n not in OVERLAY and n not in MODULES]
    if unknown:
        raise SystemExit(f"build_lifts: no asset named {', '.join(unknown)}")
    spec = json.load(open(os.path.join(HERE, "sessellift_fgq4.json"), encoding="utf-8"))
    specs = {"sessellift_fgq4.json": spec}

    def spec_for(mod, name=None):
        """A module's spec (its SPEC file, the Sessellift's by default, or the overlay spec `name`, liftkit/gauge.py);
        other models share the Sessellift line assemblies (tower.assembly)."""
        overlay = name is not None
        name = name or getattr(mod, "SPEC", "sessellift_fgq4.json")
        if name not in specs:
            specs[name] = gauge.load(HERE, name) if overlay else json.load(open(os.path.join(HERE, name), encoding="utf-8"))
            specs[name].setdefault("tower", spec["tower"])
        return specs[name]
    budgets = json.load(open(os.path.join(HERE, "budgets.json"), encoding="utf-8"))

    t0 = time.time()
    clear_scene()
    pal = materials.palette_image(palette.image(), os.path.join(tex_dir, "lift_palette.png"))
    trim = materials.detail_image(textures.image(), os.path.join(tex_dir, "lift_trim.png"))
    if opts.get("detail", "on") == "off":   # diagnostic: preview materials without the detail atlas
        trim = None
    structure = materials.make("LiftStructure", pal, detail_img=trim)
    glass = materials.make("LiftGlass", pal, glass=True, detail_img=trim)
    chair_mat = materials.make("LiftChair", pal, detail_img=trim)

    result, failures, built = {"stage": stage, "budgets": budgets, "assets": {}}, [], {}
    for name in names:
        if name in TOWER:
            mod = importlib.import_module("tower")
            kind = mod.kind_of(TOWER[name])
            assets = [mod.build(spec, lod, stage, TOWER[name]) for lod in range(mod.LODS)]
        elif name in OVERLAY:
            modname, over, variant = OVERLAY[name]
            mod = importlib.import_module(modname)
            kind = mod.kind_of(variant) if variant else mod.KIND
            extra = () if variant is None else (variant,)
            assets = [mod.build(spec_for(mod, over), lod, stage, *extra) for lod in range(mod.LODS)]
        else:
            entry = MODULES[name]
            modname, variant = entry if isinstance(entry, tuple) else (entry, None)
            mod = importlib.import_module(modname)
            kind = mod.kind_of(variant) if hasattr(mod, "kind_of") else mod.KIND   # a module of several kinds (the T-bar's)
            extra = () if variant is None else (variant,)
            assets = [mod.build(spec_for(mod), lod, stage, *extra) for lod in range(mod.LODS)]
        aid = assets[0].id
        own = spec if name in TOWER else spec_for(mod, OVERLAY[name][1] if name in OVERLAY else None)
        coll = bpy.data.collections.new(aid)
        bpy.context.scene.collection.children.link(coll)
        mats = [chair_mat] if kind in ("chair", "chair_detach") else [structure, glass]
        objs, rep, split = export.make_objects(assets, mats, coll)
        sweep = sweep_heads(assets[0]) if assets[0].pivots else {}
        tiling = None
        if opts.get("ao", "on") != "off":
            t_ao = time.time()
            ground, section = ao_options(kind, assets)
            # moving parts hang under their pivots' empties: update the scene first, or their world matrices are still
            # the identity and they get baked where their pivots' origins are (as the shipped sheaves were, before P3-07)
            bpy.context.view_layer.update()
            ao_stats = []
            heads = assets[0].heads
            for lod in range(len(assets)):
                lod_objs = [o for o in objs if o.type == "MESH" and o.name.endswith(f"_LOD{lod}")]
                if not heads:
                    ao_stats.append(ao.bake(lod_objs, rays=32 if lod == 0 else 16, ground=ground,
                                            ghosts=(-section, section) if section else ()))
                    continue
                # two heads: each baked with only itself shown; the body and the shared parts with the default head
                part = {o: o.name[len(aid) + 1:-len(f"_LOD{lod}")] for o in lod_objs}
                for end in sorted(heads, key=lambda e: not heads[e]["default"]):
                    hidden = swap.other_parts(assets[0], end)
                    shown = [o for o in lod_objs if part[o] not in hidden]
                    targets = shown if heads[end]["default"] else [o for o in shown if part[o] in swap.owned(assets[0], end)]
                    ao_stats.append(ao.bake(shown, rays=32 if lod == 0 else 16, ground=ground, targets=targets))
                if section:
                    worst = ao.tiling_error(lod_objs, section)
                    tiling = max(tiling or 0.0, worst)
            print(f"{aid}: ambient occlusion baked in {time.time() - t_ao:.1f} s (min/mean per LOD: "
                  + ", ".join(f"{lo:.2f}/{mean:.2f}" for lo, mean in ao_stats) + ")", flush=True)
        path = os.path.join(out_dir, f"{aid}.fbx")
        export.export_fbx(objs, path)
        problems = check_asset(kind, assets, rep, budgets, own, split)
        if kind in ("surface_terminal", "surface_tower_head"):   # retracted carriers past the trains and round the wheel (T6)
            import surface_fit
            cmod = importlib.import_module("tbar_carrier")
            box = cmod.build(own, 0, stage, "springbox")
            for bar in ("bar_t", "bar_j"):
                problems += [f"{bar}: {p}" for p in surface_fit.check(assets[0], kind, own, box, cmod.build(own, 0, stage, bar))]
        if getattr(mod, "CHAIR", None):   # a terminal or a wide head: send its chair through (liftkit/fit.py)
            t_fit = time.time()
            chairs = [mod.CHAIR]
            chair_spec = None
            if name in OVERLAY and own.get("model", {}).get("seats"):   # a double's or triple's: both its chairs
                chair_spec, cw = FIT_CHAIRS[own["model"]["seats"]]
                chairs = ["chair", cw]
            fp = []
            for chair in chairs:
                cname, _, cvar = chair.partition(":")   # "module" or "module:variant" (the Chairworks fixed chair)
                cmod = importlib.import_module(cname)
                cargs = (cvar,) if cvar else ()
                cspec = spec_for(cmod, chair_spec) if chair_spec and cname == "chair" else spec_for(cmod)   # the narrow Sessellift chair
                if name in OVERLAY and cname == "d6_chair":   # the D4's terminals send the D4's chair
                    cspec = spec_for(cmod, OVERLAY[name][1])
                tag = f"{cname}{':' + cvar if cvar else ''} " if len(chairs) > 1 else ""
                if kind == "tower_head_wide":   # the chair past the head on both ropes (towers, Gate 1)
                    ls = __import__("liftkit.parts", fromlist=["line_spec"]).line_spec(spec_for(mod))
                    lp, _ = fit.tower(assets[0], cmod.build(cspec, 0, stage), ls["ropeR"], ls["sheave"]["dia"] / 2)
                    fp += [tag + p for p in lp]
                else:
                    check = fit.station if getattr(assets[0], "paths", {}).get("chairs") else fit.fixed   # fixed grips: round the wheel
                    for lod in range(min(len(assets), 3 if check is fit.station else 2)):   # every LOD chairs pass up close
                        for end, v in views(assets[lod]):   # with each head (liftkit/swap.py)
                            lp, _ = check(v, cmod.build(cspec, lod, stage, *cargs))
                            fp += [f"{tag}LOD{lod}{' ' + end if end else ''} {p}" for p in lp]
                    s0 = assets[0].sockets
                    if check is fit.station and "rope_right_hold" in s0:   # a detachable's integrated tower (the D4's bottom)
                        ls = __import__("liftkit.parts", fromlist=["line_spec"]).line_spec(own)
                        mine = lambda tg: tg is not None and (tg in ("#integrated_tower", "#pin")   # the tower alone: the
                                                              or fit.bare(tg.split("#")[0]).startswith(fit.TRAIN))   # station's own fit has the rest
                        row = types.SimpleNamespace(body=assets[0].body.filtered(mine), pivots=assets[0].pivots, sockets={
                            "rope_left": s0["rope_left_hold"], "rope_right": s0["rope_right_hold"]})
                        lp, _ = fit.tower(row, cmod.build(cspec, 0, stage), ls["ropeR"], ls["sheave"]["dia"] / 2, reach=1.2)
                        fp += [f"{tag}integrated tower {p}" for p in lp]
            problems += fp
            print(f"{aid}: fit in {time.time() - t_fit:.1f} s: " + ("clear" if not fp else f"{len(fp)} problems"), flush=True)
        if name in OVERLAY and kind == "tower_head":   # a double's or triple's head: its chair past it on both ropes
            ls = __import__("liftkit.parts", fromlist=["line_spec"]).line_spec(own)
            # the quad's approved heads seat their equalisers (a combination's block) flush on the lug plates at rest,
            # so fit.tower's at-rest contact check (written for the D6's wide heads) is reported, not failed
            if name.startswith("d4_"):   # the D4's heads: its detachable chair (sessellift_d4.json)
                ch = importlib.import_module("d6_chair")
                rider = ch.build(spec_for(ch, "sessellift_d4.json"), 0, stage)
            else:
                rider = importlib.import_module("chair").build(own, 0, stage)
            lp, info = fit.tower(assets[0], rider, ls["ropeR"], ls["sheave"]["dia"] / 2, rest_ok=True)
            rest = info["atRest"]
            problems += lp
            print(f"{aid}: chair past the head: " + ("clear" if not lp else f"{len(lp)} problems")
                  + (f" (at rest, as the quad's heads: {', '.join(rest)})" if rest else ""), flush=True)
        if tiling is not None and tiling > 0.01:
            problems.append(f"mast section AO differs by {tiling:.3f} between its top and bottom (> 0.01)")
        a0 = assets[0]
        result["assets"][aid] = {
            "kind": kind,
            "fbx": os.path.basename(path),
            "lods": rep["lods"],
            # parents before children; range in degrees for Quaternion.AngleAxis(angle, axis) from the rest pose
            "pivots": {k: {"pos": [round(x, 4) for x in frame.unity(a0.pivots[k]["pos"])],
                           "axis": list(frame.unity(a0.pivots[k]["axis"])),
                           "parent": a0.pivots[k].get("parent"),
                           "hinge": export.hinged(a0, split, k),
                           "range": stopped(sweep[k]["range"], a0.pivots[k].get("stop")),
                           "rangeBy": sweep[k]["by"] if a0.pivots[k].get("stop") is None
                           else [f"stop {a0.pivots[k]['stop']} deg" if abs(r) > a0.pivots[k]["stop"] else b
                                 for r, b in zip(sweep[k]["range"], sweep[k]["by"])],
                           # a part that hangs plumb: the game turns it back by its parents' turns (a walkway's hanger)
                           **({"plumb": True} if a0.pivots[k].get("plumb") else {}),
                           # a part that turns with another pivot in the world, whatever its parents do (a walkway on links)
                           **({"follow": a0.pivots[k]["follow"]} if a0.pivots[k].get("follow") else {})}
                       for k in export.pivot_order(a0.pivots)},
            "socketParents": dict(a0.socket_parents),
            "aoTiling": tiling,
            "sockets": {k: [round(x, 4) for x in frame.unity(p)] for k, p in a0.sockets.items()},
            # polylines the game follows (a detachable's station rail), when the asset has any
            "paths": {k: [[round(x, 4) for x in frame.unity(p)] for p in pts] for k, pts in getattr(a0, "paths", {}).items()},
            "bounds": [[round(x, 3) for x in frame.unity(b)] for b in a0.body.bounds_lift()],
            "dims": a0.dims,
            # a fixed-grip terminal's two heads, one shown at a time (liftkit/swap.py): its pivots and sockets
            **({"heads": {k: {"end": a0.heads[k]["end"], "default": a0.heads[k]["default"], "prefix": a0.heads[k]["prefix"],
                              "pivots": a0.heads[k]["pivots"], "sockets": a0.heads[k]["sockets"], "dims": a0.heads[k]["dims"]}
                          for k in swap.keys(a0)}} if a0.heads else {}),
            "partTris": a0.part_tris,
            "partTargets": a0.budget_targets,
            "problems": problems,
        }
        cat = own.get("catalog", {})   # the name the game shows, when the spec names this asset
        shown = cat.get("parts", {}).get(aid) or cat.get("parts", {}).get(name)
        if shown:
            model = cat.get("models", {}).get(aid, cat["name"])   # a family's other models (the Chairworks double, triple)
            result["assets"][aid]["catalog"] = {"maker": cat["maker"], "model": model, "name": shown}
        built[name] = {"assets": assets, "objects": objs, "collection": coll, "spec": own,
                       "ranges": {k: v["range"] for k, v in result["assets"][aid]["pivots"].items()}}   # Unity degrees
        print(f"{aid}: tris per LOD {[e['shown'] for e in rep['lods']]}"
              + "".join(f", {k} head {[e['heads'][k] for e in rep['lods']]}" for k in swap.keys(a0))
              + (f"  PROBLEMS: {problems}" if problems else ""), flush=True)
        failures += [f"{aid}: {p}" for p in problems]
    result["failures"] = failures
    result["buildSeconds"] = round(time.time() - t0, 1)
    with open(os.path.join(out_dir, "lifts.json"), "w") as f:
        json.dump(result, f, indent=2)
    print("Checks: " + ("all assets pass" if not failures else "FAILED"), flush=True)
    for line in failures:
        print("  " + line, flush=True)

    if "render" in opts:
        import render_review
        render_review.render_all(built, spec, opts["render"], opts.get("shots"), [structure, glass, chair_mat])
        if "surface_" in (opts.get("shots") or ""):   # the surface lifts' own review (render_surface.py)
            import render_surface
            render_surface.render_all(built, opts["render"], opts.get("shots"), [structure, glass, chair_mat])
    return failures


if __name__ == "__main__":
    sys.exit(1 if main() else 0)
