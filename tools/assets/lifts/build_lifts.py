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

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "assets"))
from liftkit import ao, export, frame, materials, palette, textures  # noqa: E402

MODULES = {"drive": "drive_terminal", "return": "return_terminal", "chair": "chair"}
# the line tower kit (assets/tower.py): name -> variant
TOWER = {"tower_s4": "s4", "tower_s6": "s6", "tower_b8": "b8", "tower_d8": "d8", "tower_c8": "c8",
         "tower_mast": "mast", "tower_base": "base"}
ALL = "drive,return,chair," + ",".join(TOWER)


def clear_scene():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    for c in list(bpy.data.collections):
        bpy.data.collections.remove(c)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)


def check_asset(kind, assets, report, budgets, spec):
    problems = []
    caps = budgets[kind]["lods"]
    if len(assets) != len(caps):
        problems.append(f"{len(assets)} LODs built, budget lists {len(caps)}")
    totals = [e["total"] for e in report["lods"]]
    for e, cap in zip(report["lods"], caps):
        if e["total"] > cap["maxTris"]:
            problems.append(f"LOD{e['lod']}: {e['total']:,} tris > {cap['maxTris']:,}")
    for a, b in zip(totals, totals[1:]):
        if b >= a:
            problems.append(f"LOD totals must decrease: {totals}")
            break
    for lod, a in enumerate(assets):
        err = export.pivot_centering_error(a)
        if err > 0.001:
            problems.append(f"LOD{lod}: a moving part is {err * 1000:.1f} mm off its axle")
    if kind == "terminal":
        c = spec["common"]
        line_v = assets[0].sockets.get("line", (0, 0, 0))[1]
        rope = c["ropeElevation"] / 1000
        for name, p in assets[0].sockets.items():
            if name.startswith("rope_"):
                # on the line gauge; level at the rope elevation, except where the rope leaves a hold-down row
                # climbing (the return's out sockets): above it, by less than a metre and a half
                off_gauge = abs(abs(p[1] - line_v) - c["lineGauge"] / 2000) > 0.001
                if name.endswith("_out") and f"{name[:-4]}_hold" in assets[0].sockets:
                    off_height = not (rope + 0.001 < p[2] < rope + 1.5)
                else:
                    off_height = abs(p[2] - rope) > 0.001
                if off_gauge or off_height:
                    problems.append(f"socket {name} at {p} is off the rope")
    if kind == "tower_head":   # ropes at the line gauge, level with each other, at the head's declared height
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
    names = opts.get("assets", ALL).split(",")
    spec = json.load(open(os.path.join(HERE, "sessellift_fgq4.json"), encoding="utf-8"))
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
        else:
            mod = importlib.import_module(MODULES[name])
            kind = mod.KIND
            assets = [mod.build(spec, lod, stage) for lod in range(mod.LODS)]
        aid = assets[0].id
        coll = bpy.data.collections.new(aid)
        bpy.context.scene.collection.children.link(coll)
        mats = [chair_mat] if kind == "chair" else [structure, glass]
        objs, rep = export.make_objects(assets, mats, coll)
        if opts.get("ao", "on") != "off":
            t_ao = time.time()
            ao_stats = [ao.bake([o for o in objs if o.type == "MESH" and o.name.endswith(f"_LOD{lod}")],
                                rays=32 if lod == 0 else 16, ground=kind != "chair") for lod in range(len(assets))]
            print(f"{aid}: ambient occlusion baked in {time.time() - t_ao:.1f} s (min/mean per LOD: "
                  + ", ".join(f"{lo:.2f}/{mean:.2f}" for lo, mean in ao_stats) + ")", flush=True)
        path = os.path.join(out_dir, f"{aid}.fbx")
        export.export_fbx(objs, path)
        problems = check_asset(kind, assets, rep, budgets, spec)
        a0 = assets[0]
        result["assets"][aid] = {
            "kind": kind,
            "fbx": os.path.basename(path),
            "lods": rep["lods"],
            "pivots": {k: {"pos": [round(x, 4) for x in frame.unity(v["pos"])], "axis": list(frame.unity(v["axis"]))}
                       for k, v in a0.pivots.items()},
            "sockets": {k: [round(x, 4) for x in frame.unity(p)] for k, p in a0.sockets.items()},
            "bounds": [[round(x, 3) for x in frame.unity(b)] for b in a0.body.bounds_lift()],
            "dims": a0.dims,
            "partTris": a0.part_tris,
            "partTargets": a0.budget_targets,
            "problems": problems,
        }
        built[name] = {"assets": assets, "objects": objs, "collection": coll}
        print(f"{aid}: tris per LOD {[e['total'] for e in rep['lods']]}" + (f"  PROBLEMS: {problems}" if problems else ""), flush=True)
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
    return failures


if __name__ == "__main__":
    sys.exit(1 if main() else 0)
