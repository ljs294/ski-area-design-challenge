"""Game-rule audit of a lift build, run before the owner's model review (task P3-07): previews can hide what the game
will see, so this re-imports every exported FBX the way Unity reads it and checks the posable parts.

    blender -b --factory-startup --python-exit-code 1 --python tools/assets/lifts/audit_lifts.py -- [--out DIR] [--assets id,id]

Checks per asset (out/lifts.json and out/<id>.fbx):
  - every pivot's empty exists, flat (LiftImport nests them from lifts.json "parent": Blender's FBX export misplaces
    deep nesting), and lands where the build put it, in Unity's frame, to 1 mm (LiftImport checks again); the
    parents form a tree;
  - every mesh hangs under the root or straight under a pivot's empty, at that empty's origin;
  - sockets that ride with a pivot (socketParents) name a known pivot;
  - each posable model's train top pivots carry a part at every LOD (contract M1: far trains stay parts);
  - each pivot's range contains the rest pose, a wheel's is the whole turn, and only rigid parts have none;
  - mast sections tile their ambient occlusion (aoTiling <= 0.01) and the build reported no failures.
Exit code 1 if anything fails.
"""
import json
import os
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
TOPS = ("eq_", "yoke_", "block_", "beam_")


def unity(p_blender):
    x, y, z = p_blender   # Blender = (-v, -u, w) -> Unity (v, w, u)
    return Vector((-x, z, -y))


def audit(aid, info, fbx):
    problems = []
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    bpy.ops.import_scene.fbx(filepath=fbx, axis_forward="-Z", axis_up="Y")
    bpy.context.view_layer.update()
    objs = {o.name: o for o in bpy.data.objects}
    pivots = info["pivots"]
    empties = {}
    for name, p in pivots.items():
        e = objs.get(f"{aid}_pivot_{name}")
        if e is None:
            problems.append(f"pivot {name}: no empty in the FBX")
            continue
        empties[name] = e
        want = Vector(p["pos"])
        got = unity(e.matrix_world.translation)
        if (got - want).length > 0.001:
            problems.append(f"pivot {name}: at {tuple(round(c, 4) for c in got)}, built at {tuple(want)}")
        if e.parent is not None and e.parent.name.startswith(f"{aid}_"):   # flat in the FBX; LiftImport nests them
            problems.append(f"pivot {name}: under {e.parent.name} in the FBX (Blender's export misplaces deep nesting)")
    for name, p in pivots.items():   # the nesting LiftImport will build: a tree of known pivots
        seen, at = set(), name
        while at:
            if at in seen or at not in pivots:
                problems.append(f"pivot {name}: its parents loop or name an unknown pivot ({at})")
                break
            seen.add(at)
            at = pivots[at].get("parent")
    for o in objs.values():
        if o.type != "MESH":
            continue
        if o.parent is not None:
            if not o.parent.name.startswith(f"{aid}_pivot_"):
                problems.append(f"mesh {o.name}: under {o.parent.name}, not a pivot")
            elif o.location.length > 0.001:
                problems.append(f"mesh {o.name}: {o.location.length * 1000:.1f} mm off its pivot")
    for sock, parent in info.get("socketParents", {}).items():
        if f"{aid}_socket_{sock}" not in objs or parent not in pivots:
            problems.append(f"socket {sock}: missing, or rides with unknown pivot {parent}")
    lods = len(info["lods"])
    tops = [k for k, p in pivots.items() if not p.get("parent") and k.startswith(TOPS)]
    for top in tops:
        under = {top}
        grew = True
        while grew:
            grew = False
            for k, p in pivots.items():
                if p.get("parent") in under and k not in under:
                    under.add(k)
                    grew = True
        for lod in range(lods):
            if not any(o.type == "MESH" and o.name.endswith(f"_LOD{lod}") and o.parent is not None
                       and o.parent.name[len(f"{aid}_pivot_"):] in under for o in objs.values()):
                problems.append(f"train {top}: no part at LOD{lod}")
    for name, p in pivots.items():
        lo, hi = p.get("range", [-180, 180])
        if not lo <= 0 <= hi:
            problems.append(f"pivot {name}: range {lo}..{hi} excludes the rest pose")
        wheel = name == "bullwheel" or name.startswith("sheave_")
        if wheel and (lo, hi) != (-180.0, 180.0):
            problems.append(f"pivot {name}: a wheel limited to {lo}..{hi}")
        if not wheel and lo == hi == 0 and not name.startswith("yoke_") and "_" in name:
            problems.append(f"pivot {name}: can't turn at all")
    if info.get("aoTiling") is not None and info["aoTiling"] > 0.01:
        problems.append(f"AO tiling {info['aoTiling']:.3f} > 0.01")
    problems += info.get("problems", [])
    return problems


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {argv[i][2:]: argv[i + 1] for i in range(0, len(argv) - 1, 2) if argv[i].startswith("--")}
    out = os.path.abspath(opts.get("out", os.path.join(HERE, "out")))
    report = json.load(open(os.path.join(out, "lifts.json"), encoding="utf-8"))
    wanted = opts.get("assets")
    failed = 0
    for aid, info in report["assets"].items():
        if wanted and aid not in wanted.split(","):
            continue
        problems = audit(aid, info, os.path.join(out, info["fbx"]))
        print(f"{aid}: {len(info['pivots'])} pivots, " + ("ok" if not problems else f"{len(problems)} problems"), flush=True)
        for line in problems:
            print("  " + line, flush=True)
        failed += bool(problems)
    print("Audit: " + ("all assets pass" if not failed else f"{failed} assets FAILED"), flush=True)
    return failed


if __name__ == "__main__":
    sys.exit(1 if main() else 0)
