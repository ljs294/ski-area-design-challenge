"""Turns built assets into Blender objects (LODs, pivots, sockets) and exports FBX with the tree settings."""
import bpy
from mathutils import Vector

from . import frame


class Asset:
    """One LOD of one asset, as a builder returns it (all positions in the lift frame, metres)."""

    def __init__(self, asset_id):
        self.id = asset_id
        self.body = None            # MeshBuilder
        self.parts = {}             # moving part name -> MeshBuilder (built in place, not yet re-centred)
        self.pivots = {}            # moving part name -> {"pos": (u, v, w), "axis": (u, v, w)}
        self.sockets = {}           # socket name -> (u, v, w)
        self.dims = {}              # named model measurements (mm) for the dimension check
        self.budget_targets = {}    # part -> LOD0 triangle target (warnings only)
        self.part_tris = {}         # part -> triangles (filled by the builder, optional)


def make_objects(assets, materials, collection):
    """assets: list of Asset, one per LOD (index = LOD). Returns (objects to export, report)."""
    aid = assets[0].id
    objs, empties, report = [], {}, {"lods": []}
    base = assets[0]
    for name, piv in base.pivots.items():
        e = bpy.data.objects.new(f"{aid}_pivot_{name}", None)
        e.empty_display_type = "ARROWS"
        e.empty_display_size = 0.5
        e.location = frame.b(piv["pos"])
        collection.objects.link(e)
        empties[name] = e
        objs.append(e)
    for name, pos in base.sockets.items():
        e = bpy.data.objects.new(f"{aid}_socket_{name}", None)
        e.empty_display_type = "PLAIN_AXES"
        e.empty_display_size = 0.3
        e.location = frame.b(pos)
        collection.objects.link(e)
        objs.append(e)
    for lod, a in enumerate(assets):
        for mb in [a.body, *a.parts.values()]:
            for _ in range(3):   # a moved face can land flush on another; repeat until nothing moves
                if not mb.separate_flush():
                    break
        body = a.body.to_object(f"{aid}_LOD{lod}", materials, collection)
        objs.append(body)
        entry = {"lod": lod, "body": a.body.tri_count(), "parts": {}, "digest": {"body": a.body.digest()}}
        for name, mb in a.parts.items():
            piv = base.pivots[name]["pos"]
            local = mb.translated((-piv[0], -piv[1], -piv[2]))
            o = local.to_object(f"{aid}_{name}_LOD{lod}", materials, collection)
            o.parent = empties[name]
            objs.append(o)
            entry["parts"][name] = mb.tri_count()
            entry["digest"][name] = mb.digest()
        entry["total"] = entry["body"] + sum(entry["parts"].values())
        report["lods"].append(entry)
    return objs, report


def export_fbx(objs, path):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={"EMPTY", "MESH"},
                             apply_unit_scale=True, axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE",
                             colors_type="LINEAR", use_triangles=True, bake_space_transform=True,
                             bake_anim=False, add_leaf_bones=False, path_mode="RELATIVE")


def pivot_centering_error(asset):
    """Max distance (m) between each moving part's bounds centre and its axle, measured across the axle. Hinged
    parts (a snow gun's lance: pivot "hinge": true) swing about an axle at one end and are not checked."""
    worst = 0.0
    for name, mb in asset.parts.items():
        if asset.pivots[name].get("hinge"):
            continue
        piv = Vector(asset.pivots[name]["pos"])
        axis = Vector(asset.pivots[name]["axis"]).normalized()
        lo, hi = mb.bounds_lift()
        c = (Vector(lo) + Vector(hi)) / 2
        d = c - piv
        d -= axis * d.dot(axis)
        worst = max(worst, d.length)
    return worst
