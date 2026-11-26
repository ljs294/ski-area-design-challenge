"""Builds the building assets in Blender, with no manual steps (task 4.50).

    blender -b --factory-startup --python-exit-code 1 --python tools/assets/buildings/build_buildings.py -- \
        [--out DIR] [--assets pumphouse,booster,lifthut,unloadramp,garages] [--render DIR] [--cases DIR] [--photos DIR [--preview 1]] [--ao off]

Each building is written in the building frame (README.md) from its spec (<name>.json), reusing the lift kit
(tools/assets/lifts/liftkit: MeshBuilder, primitives, palette, detail atlas, AO, FBX export). One FBX per building:
<id>_LOD0..3 bodies and <id>_socket_<name> empties. The build fails (non-zero exit) if a LOD is over its budget
(budgets.json), the totals don't decrease, two builds of a LOD differ, the walls miss the spec's footprint or ridge
by more than 1 mm, or a required socket is missing. out/buildings.json records triangles, sockets, lights, dimensions
and mesh hashes.
"""
import importlib
import json
import os
import sys
import time

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "assets"))
import bldgkit  # noqa: E402,F401  (puts the lift kit on the path)
from bldgkit import palette as bpal  # noqa: E402
from liftkit import ao, export, frame, materials, palette, textures  # noqa: E402

HUTS = ("hut_large", "hut_small", "hut_mini")
RAMPS = ("ramp_quad", "ramp_triple", "ramp_double", "ramp_quad_wide", "ramp_triple_wide", "ramp_double_wide")
GATES = ("gate_sessellift", "gate_stern", "gate_monta", "gate_sessellift_drive", "gate_monta_drive")
MODULES = {"pumphouse": "pumphouse", "booster": "booster", **{h: "lifthut:" + h[4:] for h in HUTS},
           **{r: "unloadramp:" + r[5:] for r in RAMPS}, "pedestal": "oppedestal",
           **{g: "stopgate:" + g[5:] for g in GATES}, "garage": "garage"}
GROUPS = {"lifthut": ",".join(HUTS), "stopgate": ",".join(GATES),
          "unloadramp": ",".join(RAMPS + ("pedestal",) + GATES),
          "garages": "garage"}   # a name standing for several buildings
ALL = "pumphouse,booster,lifthut,unloadramp,garages"
REQUIRED = {"pumphouse": ("intake", "discharge", "door_main", "label"),
            "booster": ("inlet", "discharge", "door_main", "door_man", "label"),
            "lift_hut": ("door", "label", "view"),
            "unload_ramp": ("chair_unload", "breakover", "ramp_end", "hut", "pedestal", "walkway"),
            "op_pedestal": ("stop", "phone"), "stop_gate": ("pivot", "bar_end"),
            "garage": ("door_1", "door_2", "door_man", "bay_1", "bay_2", "fuel", "fuel_fill", "label")}
WALL = "panel_tan"


def clear_scene():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    for c in list(bpy.data.collections):
        bpy.data.collections.remove(c)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)


def load(name):
    """A building's module, or for a variant ("lifthut:large") the module's handle for that variant."""
    mod_name, _, var = MODULES[name].partition(":")
    mod = importlib.import_module(mod_name)
    return mod.variant(var) if var else mod


def wall_extent(mb, wall_cls=WALL):
    """The walls' (panel faces') extent in the building frame: (u min, u max, v min, v max, w max)."""
    pts = [frame.lift(mb.verts[i]) for f, s in zip(mb.faces, mb.style) if s.cls == wall_cls for i in f]
    return (min(p[0] for p in pts), max(p[0] for p in pts), min(p[1] for p in pts), max(p[1] for p in pts),
            max(p[2] for p in pts))


def check(kind, mod, spec, assets, report, budgets, digests):
    problems = []
    caps = budgets[kind]["lods"]
    if len(assets) != len(caps):
        problems.append(f"{len(assets)} LODs built, budget lists {len(caps)}")
    totals = [e["total"] for e in report["lods"]]
    for e, cap in zip(report["lods"], caps):
        if e["total"] > cap["maxTris"]:
            problems.append(f"LOD{e['lod']}: {e['total']:,} tris > {cap['maxTris']:,}")
    if any(b >= a for a, b in zip(totals, totals[1:])):
        problems.append(f"LOD totals must decrease: {totals}")
    for lod, d in enumerate(digests):   # determinism: a second build of the same LOD gives the same mesh
        if mod.build(spec, lod).body.digest() != d:
            problems.append(f"LOD{lod}: two builds differ")
    fp = spec.get("footprint")   # walls to their footprint and ridge (a ramp has no walls: its own checks)
    hx, hy = (fp["length"] / 2000, fp["width"] / 2000) if fp else (0, 0)
    ridge = assets[0].dims["ridge"] / 1000 if fp else 0
    for lod, a in enumerate(assets if fp else []):
        u0, u1, v0, v1, w1 = wall_extent(a.body, getattr(mod, "WALL", WALL))
        err = max(abs(u0 + hx), abs(u1 - hx), abs(v0 + hy), abs(v1 - hy))
        if err > 0.001:
            problems.append(f"LOD{lod}: walls {err * 1000:.1f} mm off the {fp['length']} x {fp['width']} footprint")
        if abs(w1 - ridge) > 0.001:
            problems.append(f"LOD{lod}: walls' top at {w1 * 1000:.0f} mm, spec {ridge * 1000:.0f}")
    for lod, a in enumerate(assets):   # the builder's own findings (a light's conduit across an opening, ...)
        problems += [f"LOD{lod}: {p}" for p in getattr(a, "problems", [])]
    for name in REQUIRED.get(kind, REQUIRED.get(mod.KIND, ())):
        if name not in assets[0].sockets:
            problems.append(f"socket {name} missing")
    for lt in assets[0].lights:
        if lt["name"] not in assets[0].sockets:
            problems.append(f"light {lt['name']} has no socket")
    return problems


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {argv[i][2:]: argv[i + 1] for i in range(0, len(argv) - 1, 2) if argv[i].startswith("--")}
    out_dir = os.path.abspath(opts.get("out", os.path.join(HERE, "out")))
    tex_dir = os.path.join(out_dir, "textures")
    os.makedirs(tex_dir, exist_ok=True)
    names = [n for g in opts.get("assets", ALL).split(",") for n in GROUPS.get(g, g).split(",")]
    unknown = [n for n in names if n not in MODULES]
    if unknown:
        raise SystemExit(f"build_buildings: no building named {', '.join(unknown)}")
    budgets = json.load(open(os.path.join(HERE, "budgets.json"), encoding="utf-8"))
    bpal.register()

    t0 = time.time()
    clear_scene()
    pal = materials.palette_image(palette.image(), os.path.join(tex_dir, "lift_palette.png"))
    trim = materials.detail_image(textures.image(), os.path.join(tex_dir, "lift_trim.png"))
    white = (1.0, 1.0, 1.0)   # the buildings use no livery: masks 3 and 4 are the lamp and window glow (bldgkit/palette.py)
    structure = materials.make("BuildingStructure", pal, livery_rgb=white, detail_img=trim)
    window = materials.make("BuildingWindow", pal, livery_rgb=white, detail_img=trim)
    mats = [structure, window]

    result, failures, built = {"budgets": budgets, "assets": {}}, [], {}
    for name in names:
        mod = load(name)
        spec = json.load(open(os.path.join(HERE, mod.SPEC), encoding="utf-8"))
        if hasattr(mod, "pick"):   # one spec, several buildings: this one's
            spec = mod.pick(spec)
        assets = [mod.build(spec, lod) for lod in range(mod.LODS)]
        digests = [a.body.digest() for a in assets]   # before make_objects, whose flush pass edits the bodies
        aid = assets[0].id
        coll = bpy.data.collections.new(aid)
        bpy.context.scene.collection.children.link(coll)
        objs, rep, split = export.make_objects(assets, mats, coll)
        if opts.get("ao", "on") != "off":
            t_ao = time.time()
            bpy.context.view_layer.update()
            stats = []
            for lod in range(len(assets)):
                lod_objs = [o for o in objs if o.type == "MESH" and o.name.endswith(f"_LOD{lod}")]
                stats.append(ao.bake(lod_objs, rays=32 if lod == 0 else 16, ground=True, reach=2.0))
            print(f"{aid}: ambient occlusion baked in {time.time() - t_ao:.1f} s (min/mean per LOD: "
                  + ", ".join(f"{lo:.2f}/{mean:.2f}" for lo, mean in stats) + ")", flush=True)
        path = os.path.join(out_dir, f"{aid}.fbx")
        export.export_fbx(objs, path)
        problems = check(mod.KIND, mod, spec, assets, rep, budgets, digests)
        a0 = assets[0]
        result["assets"][aid] = {
            "kind": mod.KIND,
            "catalog": spec.get("catalog", {}),
            "fbx": os.path.basename(path),
            "lods": rep["lods"],
            "sockets": {k: [round(x, 4) for x in frame.unity(p)] for k, p in a0.sockets.items()},
            # every light the game places, in Unity's frame (x = v, y = w, z = u); schedule tags: README.md
            "lights": [{**lt, "pos": [round(x, 4) for x in frame.unity(lt["pos"])],
                        "aim": [round(x, 4) for x in frame.unity(lt["aim"])]} for lt in a0.lights],
            "glow": a0.glow,
            "bounds": [[round(x, 3) for x in frame.unity(b)] for b in a0.body.bounds_lift()],
            "dims": a0.dims,
            "problems": problems,
        }
        built[name] = {"assets": assets, "objects": objs, "collection": coll, "spec": spec}
        print(f"{aid}: tris per LOD {[e['total'] for e in rep['lods']]}"
              + (f"  PROBLEMS: {problems}" if problems else ""), flush=True)
        failures += [f"{aid}: {p}" for p in problems]
    result["failures"] = failures
    result["buildSeconds"] = round(time.time() - t0, 1)
    with open(os.path.join(out_dir, "buildings.json"), "w") as f:
        json.dump(result, f, indent=2)
    print("Checks: " + ("all buildings pass" if not failures else "FAILED"), flush=True)
    for line in failures:
        print("  " + line, flush=True)

    if "render" in opts:
        import render_review
        render_review.render_all(built, opts["render"], mats)
    if "cases" in opts:   # the unload ramps' Gate 1 cases (heights, falling ground, hut on its bay)
        import render_ramp_cases
        render_ramp_cases.render_cases(built, opts["cases"], mats, opts.get("terminals"))
    if "photos" in opts:   # the ramps' Gate 2 stills come from their cases' scenes; the rest from render_photos
        import render_photos
        preview = opts.get("preview", "0") == "1"
        ramps = {k: b for k, b in built.items() if b["assets"][0].id.startswith("bldg_ramp")}
        others = {k: b for k, b in built.items() if k not in ramps and "photos" in b["spec"].get("review", {})}
        if others:
            render_photos.render_all(others, opts["photos"], mats, preview=preview)
        if ramps:
            import render_ramp_cases
            render_ramp_cases.render_photos(built, opts["photos"], mats, opts.get("terminals"), preview=preview)
    return failures


if __name__ == "__main__":
    sys.exit(1 if main() else 0)
