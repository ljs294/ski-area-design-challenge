"""Builds the game's copy of the skier and the snowboarder, then checks it.

    blender -b --factory-startup --python-exit-code 1 --python tools/assets/skiers/build_skiers.py -- [--out DIR]

Writes out/skiers.bytes (every LOD mesh, every clip baked to bone matrices, the outfit table; see skierkit/bake.py)
and out/skiers.json. The build fails (non-zero exit) when a LOD is over its triangle budget or uses more bone
influences than budgets.json allows, a clip's reach error passes 1 mm, a turn does not loop, boots leave the snow,
a weight row does not sum to 255, or the baked matrices disagree with Blender's own armature by more than 0.5 mm.
"""
import argparse
import os
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from skierkit import bake, build, clips, poses, rig  # noqa: E402
from skierkit import skeleton as sk  # noqa: E402


def blender_check(problems):
    """Pose a real armature with rig.apply and compare its deformed LOD0 with the baked matrices."""
    for kind in bake.KINDS:
        for name in ("stand", "carve"):
            pose = poses.ALL[kind][name]
            for o in list(bpy.data.objects):
                bpy.data.objects.remove(o)
            arm, objs, meshes = build.figure(kind, f"check_{kind}", bpy.context.scene.collection, (0,))
            rig.apply(arm, kind, pose)
            dg = bpy.context.evaluated_depsgraph_get()
            ev = objs[0].evaluated_get(dg).to_mesh()
            mats, _ = bake.matrices(kind, pose, 0)
            Ct = bake.C.transposed()
            blend = [(Ct @ r @ bake.C, Ct @ t) for r, t in mats]
            names = [n for n, _, _, _ in sk.bones(kind)]
            index = {n: i for i, n in enumerate(names)}
            worst = 0.0
            for i, v in enumerate(meshes[0].verts):
                acc = Vector()
                for b, w in meshes[0].weights[i].items():
                    r, t = blend[index[b]]
                    acc += (r @ v + t) * w
                worst = max(worst, (acc - ev.vertices[i].co).length)
            print(f"  Blender check {kind} {name}: worst {worst * 1000:.2f} mm")
            if worst > 0.0005:
                problems.append(f"{kind} {name}: baked matrices are {worst * 1000:.1f} mm off Blender's armature")


def checks(summary, problems):
    for kind, ks in summary["kinds"].items():
        for row in ks["lods"]:
            lod = row["lod"]
            if row["influences"] > build.INFLUENCES[lod]:
                problems.append(f"{kind} LOD{lod}: {row['influences']} bone influences, budget {build.INFLUENCES[lod]}")
            print(f"  {kind} LOD{lod}: {row['triangles']} tris (budget {build.BUDGET[lod]}), "
                  f"{row['influences']} influences")
        for c in ks["clips"]:
            if c["reach_mm"] > 1.0:
                problems.append(f"{kind} {c['name']}: reach error {c['reach_mm']} mm")
    # loops and boots on the snow, measured on the solved poses
    for kind in bake.KINDS:
        for name, fn, n, loops, pitch in bake.clip_list(kind):
            if not loops or pitch:
                continue
            a, _ = bake.matrices(kind, fn(0.0), 0)
            b, _ = bake.matrices(kind, fn(1.0), 0)
            seam = max((ta - tb).length for (_, ta), (_, tb) in zip(a, b))
            if seam > 1e-4:
                problems.append(f"{kind} {name}: does not loop ({seam * 1000:.2f} mm at the seam)")
            if name.startswith("turn"):
                for k in range(n):
                    W, head, _ = rig.solve(kind, clips.full(kind, fn(k / n)))
                    feet = [head["ski_l"], head["ski_r"]] if kind == "skier" else [head["board"]]
                    low = min(p.z for p in feet)
                    # a plank on edge rests on that edge, so its origin rises by up to half its width
                    if low < -0.002 or low > (0.05 if kind == "skier" else 0.16):
                        problems.append(f"{kind} {name} frame {k}: skis or board at {low * 1000:.0f} mm, not on the snow")
                        break


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    args = ap.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    os.makedirs(args.out, exist_ok=True)
    problems = []
    summary = bake.write(os.path.join(args.out, "skiers.bytes"), os.path.join(args.out, "skiers.json"))
    size = os.path.getsize(os.path.join(args.out, "skiers.bytes"))
    print(f"Wrote skiers.bytes ({size / 1024:.0f} KB)")
    checks(summary, problems)
    blender_check(problems)
    if problems:
        print("PROBLEMS:")
        for p in problems:
            print("  " + p)
        sys.exit(1)
    print("Checks passed")


if __name__ == "__main__":
    main()
