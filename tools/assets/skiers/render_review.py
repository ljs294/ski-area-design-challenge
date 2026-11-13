"""Review renders of the skier and snowboarder models (Cycles).

blender -b --factory-startup --python render_review.py -- --out <abs dir> [--shots sheet,chair,...] [--lod 0]
"""
import argparse
import json
import math
import os
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from skierkit import build, clips, outfit, poses, rig   # noqa: E402

LOD = 0


def scene_setup(res=(1920, 1080), samples=64, snow=False):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = samples
    sc.cycles.use_denoising = True
    try:   # the graphics card when there is one
        prefs = bpy.context.preferences.addons["cycles"].preferences
        prefs.compute_device_type = "OPTIX"
        prefs.get_devices()
        for d in prefs.devices:
            d.use = d.type == "OPTIX"
        sc.cycles.device = "GPU"
    except Exception as e:   # noqa: BLE001
        print("Cycles stays on the CPU:", e)
    sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.image_settings.file_format = "PNG"
    sc.view_settings.view_transform = "AgX" if snow else "Standard"
    world = bpy.data.worlds.new("W")
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0.45, 0.62, 0.95, 1) if snow else (0.42, 0.42, 0.42, 1)
    bg.inputs["Strength"].default_value = 1.0 if snow else 0.9
    sc.world = world
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sc.collection.objects.link(sun)
    sun.data.energy = 4.0 if snow else 3.2
    sun.data.angle = math.radians(2 if snow else 4)
    to_sun = Vector((-0.5, -0.55, 0.75)).normalized()
    sun.rotation_euler = (-to_sun).to_track_quat("-Z", "Y").to_euler()
    bpy.ops.mesh.primitive_plane_add(size=2000, location=(0, 0, 0))
    floor = bpy.context.active_object
    m = bpy.data.materials.new("Floor")
    m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (0.80, 0.82, 0.86, 1) if snow else (0.30, 0.30, 0.30, 1)
    b.inputs["Roughness"].default_value = 0.9
    floor.data.materials.append(m)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    sc.collection.objects.link(cam)
    sc.camera = cam
    cam.data.clip_end = 2000
    return sc, cam


def shoot(sc, cam, path, target, az, el, dist, lens=85, ortho=None):
    """az 0 looks at the figure's front, 90 at its left side, 180 at its back."""
    a, e = math.radians(az), math.radians(el)
    d = Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e)))
    cam.location = Vector(target) + d * dist
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam.data.type = "ORTHO" if ortho else "PERSP"
    if ortho:
        cam.data.ortho_scale = ortho
    cam.data.lens = lens
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("wrote", path)


def place(kind, pose, at, yaw=0.0, colours=None, name=None, lod=None):
    """pose: a name from poses.py or a pose itself."""
    lod = LOD if lod is None else lod
    label = pose if isinstance(pose, str) else "clip"
    name = name or f"{kind}_{label}"
    arm, objs, meshes = build.figure(kind, name, bpy.context.scene.collection, (lod,), colours)
    err = rig.apply(arm, kind, poses.ALL[kind][pose] if isinstance(pose, str) else pose)
    arm.location = at
    arm.rotation_euler = (0, 0, math.radians(yaw))
    print(f"{name}: LOD{lod} {meshes[lod].tris()} tris, reach error {err * 1000:.0f} mm")
    return arm


def sheet(out):
    """The concept sheet again: skier, skier carving, snowboarder, snowboarder carving."""
    sc, cam = scene_setup()
    place("skier", "stand", (-2.7, 0, 0))
    place("skier", "carve", (-1.0, 0, 0), yaw=12)
    place("boarder", "stand", (1.1, 0, 0))
    place("boarder", "carve", (2.9, 0, 0), yaw=20)
    views = {"front": (0, 6), "34": (35, 12), "back": (180, 8)}
    for v in (os.environ.get("SKIER_VIEWS") or "front,34,back").split(","):
        shoot(sc, cam, os.path.join(out, f"sheet_{v}.png"), (0, 0, 0.95), *views[v], 30, ortho=7.2)


def _chair(sc, lift=3.6):
    """The FGQ-4 chair from tools/assets/lifts/out, hung `lift` metres up; returns its seat points."""
    bpy.ops.import_scene.fbx(filepath=os.path.join(HERE, "..", "lifts", "out", "sessellift_fgq4_chair.fbx"))
    steel = build.materials("chair", outfit.CONCEPT_SKIER)["helmet"]
    seats = {}
    up = Vector((0, 0, lift))
    for o in list(bpy.data.objects):
        if not o.name.startswith("sessellift"):
            continue
        if o.type == "MESH":
            o.hide_render = "LOD0" not in o.name
            o.data.materials.clear()
            o.data.materials.append(steel)
        elif "socket_seat" in o.name:
            seats[o.name[-1]] = o.matrix_world.translation.copy() + up
        if o.parent is None:
            o.location += up
    return seats


def chair(out):
    """Both riders on the FGQ-4 chair, for scale and the seated pose."""
    sc, cam = scene_setup()
    seats = _chair(sc)
    place("skier", "seated", seats["2"])
    place("boarder", "seated", seats["3"])
    mid = (seats["2"] + seats["3"]) / 2 + Vector((0, 0, 0.35))
    for name, az, el in (("34", 35, 12), ("side", 90, 0), ("front", 0, 5)):
        shoot(sc, cam, os.path.join(out, f"chair_{name}.png"), mid, az, el, 30, ortho=4.4)


def lods(out):
    """Every LOD of both figures side by side, standing and in a carve, with the counts written to lods.json."""
    counts = {}
    n = build.LOD_COUNT
    for pose in ("stand", "carve"):
        sc, cam = scene_setup()
        for row, kind in enumerate(("skier", "boarder")):
            for lod in range(n):
                arm, objs, meshes = build.figure(kind, f"{kind}_lod{lod}", sc.collection, (lod,))
                rig.apply(arm, kind, poses.ALL[kind][pose])
                arm.location = (-1.6 * n - 0.4 + 1.6 * lod + row * (1.6 * n + 0.4), 0, 0)
                arm.rotation_euler = (0, 0, math.radians(20 if pose == "carve" and kind == "boarder" else 0))
                counts[f"{kind}_LOD{lod}"] = {"tris": meshes[lod].tris(), "budget": build.BUDGET[lod],
                                              "until_m": build.UNTIL_M[lod], "influences": meshes[lod].max_influences()}
        name = "lods.png" if pose == "stand" else "lods_carve.png"
        shoot(sc, cam, os.path.join(out, name), (0, 0, 0.9), 25, 10, 40, ortho=3.5 * n)
    with open(os.path.join(out, "lods.json"), "w") as f:
        json.dump(counts, f, indent=1)
    print(counts)


def colours(out):
    """One jacket style in random colours: ten riders, skiers and snowboarders mixed."""
    sc, cam = scene_setup()
    for i in range(10):
        kind = "boarder" if i % 3 == 2 else "skier"
        place(kind, "stand", (-5.4 + 1.2 * i, 0.5 * (i % 2), 0), yaw=(-12, 8, 0)[i % 3],
              colours=outfit.outfit(100 + i, kind), name=f"c{i}")
    shoot(sc, cam, os.path.join(out, "colours.png"), (0, 0.2, 0.95), 15, 12, 40, ortho=13.0)


def closeups(out):
    """What a close camera sees: the figures from 1 to 4 metres, with a 50 mm lens."""
    sc, cam = scene_setup((1440, 1080), 96)
    place("skier", "stand", (0, 0, 0))
    place("boarder", "stand", (3, 0, 0))
    place("skier", "carve", (6, 0, 0))
    place("boarder", "carve", (9, 0, 0), yaw=20)
    for name, target, az, el, dist, lens in (
            ("skier_full_34", (0, 0, 0.95), 35, 10, 4.2, 50), ("skier_upper_front", (0, 0, 1.45), 15, 5, 1.9, 50),
            ("skier_head_side", (0, 0, 1.72), 70, 5, 0.9, 50), ("skier_back", (0, 0, 1.30), 160, 12, 2.4, 50),
            ("skier_boots", (0, -0.2, 0.15), 40, 25, 1.5, 50), ("skier_glove_pole", (-0.29, -0.27, 1.06), -50, 8, 0.75, 50),
            ("boarder_full_34", (3, 0, 0.95), 35, 10, 4.2, 50), ("boarder_boots", (3, 0, 0.15), 35, 25, 1.8, 50),
            ("skier_carve_34", (6.2, 0, 0.75), 30, 12, 3.6, 50), ("boarder_carve_34", (9, 0, 0.8), 30, 12, 3.6, 50)):
        shoot(sc, cam, os.path.join(out, f"close_{name}.png"), target, az, el, dist, lens)
    sc, cam = scene_setup((1440, 1080), 96)
    seats = _chair(sc)
    place("skier", "seated", seats["2"])
    place("boarder", "seated", seats["3"])
    mid = (seats["2"] + seats["3"]) / 2 + Vector((0, 0, 0.45))
    shoot(sc, cam, os.path.join(out, "close_chair_34.png"), mid, 30, 8, 4.0, 50)


def strips(out):
    """Each turn cycle as eight frames in a row, left to right in time, from the front and from above behind. The
    rear view mirrors the row, so its figures are placed the other way round to keep time running to the right."""
    sets = [("skier", f"ski_{s}", clips.skier_turn(s)) for s in ("green", "blue", "black", "expert")]
    sets.append(("boarder", "board", clips.boarder_turn()))
    report = {}
    for kind, tag, clip in sets:
        for view, az, el, target in (("front", 0, 6, (0, 0, 0.85)), ("rear", 180, 35, (0, 0, 0.6))):
            sc, cam = scene_setup((1920, 620), 48)
            worst = 0.0
            for i in range(8):
                pose = clip(i / 8)
                x = -6.3 + 1.8 * i
                place(kind, pose, (x if view == "front" else -x, 0, 0), name=f"{tag}{i}")
                worst = max(worst, rig.solve(kind, pose)[2])
            report[tag] = round(worst * 1000, 1)
            shoot(sc, cam, os.path.join(out, f"strip_{tag}_{view}.png"), target, az, el, 40, ortho=14.6)
    print("worst reach error by clip (mm):", report)


def _lod_at(distance):
    """The LOD the game would draw at this distance (budgets.json)."""
    for lod, until in enumerate(build.UNTIL_M):
        if distance <= until:
            return lod
    return build.LOD_COUNT - 1


def tycoon(out):
    """A slope as the game camera sees it (60 degree field of view, 1080p): riders in mid-turn, a lift line, a chair.
    Every figure is built at the LOD its distance from the camera calls for, so these shots show the ladder in use."""
    styles = ["green", "blue", "black", "expert"]
    spots = [(-7, -4), (-4, 3), (-1.5, -7), (1, 1), (3.5, -4), (6, 4), (8, -8), (-9, 6), (0, 8), (5, -11), (-5, -11),
             (10, 0), (-11, -2), (2.5, 10)]
    target, az, el = Vector((2, -2, 0.9)), 35, 35
    a, e = math.radians(az), math.radians(el)
    way = Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e)))
    for dist in (12, 25, 60, 120):
        sc, cam = scene_setup((1920, 1080), 96, snow=True)
        eye = target + way * dist
        used = {}

        def put(kind, pose, at, yaw, serial, name):
            lod = _lod_at((Vector(at) - eye).length)
            used[lod] = used.get(lod, 0) + 1
            place(kind, pose, at, yaw=yaw, colours=outfit.outfit(serial, kind), name=name, lod=lod)
        for i, (x, y) in enumerate(spots):
            kind = "boarder" if i % 4 == 3 else "skier"
            clip = clips.boarder_turn() if kind == "boarder" else clips.skier_turn(styles[i % 4])
            put(kind, clip((i * 0.37) % 1.0), (x, y, 0), (i * 53) % 40 - 20, i, f"s{i}")
        for i in range(7):   # a lift line: three ways of waiting, and snowboarders with one foot out
            kind = "boarder" if i % 3 == 1 else "skier"
            pose = "queue" if kind == "boarder" else ("stand", "lean", "rest")[(i * 5 + 1) % 3]
            put(kind, pose, (13 + 0.25 * ((i * 7) % 3 - 1), -6 + 1.5 * i, 0), 180 + (i * 37) % 24 - 12, i + 20, f"q{i}")
        seats = _chair(sc, lift=6.0)
        for k, kind in (("1", "skier"), ("2", "boarder"), ("4", "skier")):
            put(kind, "seated", tuple(seats[k] + Vector((13, -12, 0))), 0, int(k) + 40, f"r{k}")
        for o in bpy.data.objects:
            if o.name.startswith("sessellift") and o.parent is None:
                o.location += Vector((13, -12, 0))
        print(f"tycoon {dist} m: figures by LOD {dict(sorted(used.items()))}")
        shoot(sc, cam, os.path.join(out, f"tycoon_{dist}m.png"), tuple(target), az, el, dist, lens=17.5)


def idles(out):
    """The ways of waiting in a lift line, close: three for a skier, one-foot-out for a snowboarder."""
    sc, cam = scene_setup((1920, 900), 96)
    for i, (kind, pose) in enumerate((("skier", "stand"), ("skier", "lean"), ("skier", "rest"), ("boarder", "queue"),
                                      ("boarder", "stand"))):
        place(kind, pose, (-3.4 + 1.7 * i, 0, 0), colours=outfit.outfit(200 + i * 3, kind), name=f"idle{i}")
    shoot(sc, cam, os.path.join(out, "idles.png"), (0, 0, 0.95), 25, 10, 30, ortho=9.0)


def debug(out):
    """One pose from four sides, small and quick: SKIER_POSE=skier:carve."""
    kind, pose = (os.environ.get("SKIER_POSE") or "skier:carve").split(":")
    sc, cam = scene_setup((960, 720), 24)
    place(kind, pose, (0, 0, 0))
    for az in (0, 90, 180, 270):
        shoot(sc, cam, os.path.join(out, f"dbg_{kind}_{pose}_{az}.png"), (0, 0, 0.8), az, 10, 30, ortho=3.0)


SHOTS = {"sheet": sheet, "chair": chair, "lods": lods, "colours": colours, "closeups": closeups, "strips": strips,
         "tycoon": tycoon, "idles": idles, "debug": debug}

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--shots", default="sheet")
    ap.add_argument("--lod", type=int, default=0)
    args = ap.parse_args(sys.argv[sys.argv.index("--") + 1:])
    LOD = args.lod
    os.makedirs(args.out, exist_ok=True)
    for s in args.shots.split(","):
        SHOTS[s](args.out)
