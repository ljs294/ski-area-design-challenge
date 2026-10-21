"""Turns built assets into Blender objects (LODs, pivots, sockets) and exports FBX with the tree settings."""
import bpy
from mathutils import Vector

from . import frame


class Asset:
    """One LOD of one asset, as a builder returns it (all positions in the lift frame, metres)."""

    def __init__(self, asset_id):
        self.id = asset_id
        self.body = None            # MeshBuilder; faces tagged with a pivot's name (MeshBuilder.grouped) become parts
        self.parts = {}             # moving part name -> MeshBuilder built on its own (not yet re-centred)
        # moving part name -> {"pos": (u, v, w), "axis": (u, v, w), "parent": pivot name or None, "spin": True for a
        # wheel turning on its axle (centred, any angle), "hinge": True for a part swinging about one end}
        self.pivots = {}
        self.sockets = {}           # socket name -> (u, v, w)
        self.socket_parents = {}    # socket name -> the pivot it rides with (LiftImport parents it to the pivot)
        self.dims = {}              # named model measurements (mm) for the dimension check
        self.budget_targets = {}    # part -> LOD0 triangle target (warnings only)
        self.part_tris = {}         # part -> triangles (filled by the builder, optional)


def pivot_order(pivots):
    """Pivot names with every parent before its children (stable: the builder's order otherwise)."""
    done, out = set(), []

    def visit(name):
        if name in done:
            return
        parent = pivots[name].get("parent")
        if parent:
            visit(parent)
        done.add(name)
        out.append(name)
    for name in pivots:
        visit(name)
    return out


def lod_parts(a):
    """One LOD's body and moving parts, in the lift frame: separate_flush on the merged mesh (body with its tagged
    faces), then the tagged faces cut out into parts (contract M1); parts built on their own are flushed alone."""
    posable = any(t is not None for t in a.body.tags)
    for _ in range(3):   # a moved face can land flush on another; repeat until nothing moves
        if not a.body.separate_flush(once_per_plane=posable):
            break
    body, tagged = a.body.split()
    parts = dict(tagged)
    for name, mb in a.parts.items():
        for _ in range(3):
            if not mb.separate_flush():
                break
        parts[name] = parts[name].merge(mb) if name in parts else mb
    return body, parts


def make_objects(assets, materials, collection):
    """assets: list of Asset, one per LOD (index = LOD). Returns (objects to export, report, per-LOD parts)."""
    aid = assets[0].id
    objs, empties, report = [], {}, {"lods": []}
    base = assets[0]
    # Pivot and socket empties are exported flat, at their places in the root's frame: Blender's FBX export with
    # bake_space_transform writes a third level of nesting wrong (a grandchild lands off its place), so the nesting
    # (pivot "parent", socket_parents) goes to lifts.json and LiftImport builds it in the prefab. Each part is a
    # child of its own pivot's empty, two levels, as before.
    for name in pivot_order(base.pivots):
        e = bpy.data.objects.new(f"{aid}_pivot_{name}", None)
        e.empty_display_type = "ARROWS"
        e.empty_display_size = 0.5
        e.location = frame.b(base.pivots[name]["pos"])
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
    split = []
    for lod, a in enumerate(assets):
        body, parts = lod_parts(a)
        split.append((body, parts))
        entry = {"lod": lod, "body": body.tri_count(), "parts": {}, "digest": {"body": body.digest()}}
        if body.faces:   # a chair's body is empty where every face rides with the grip or the hanger
            objs.append(body.to_object(f"{aid}_LOD{lod}", materials, collection))
        for name, mb in parts.items():
            if name not in base.pivots:
                raise ValueError(f"{aid} LOD{lod}: part {name} has no pivot")
            piv = base.pivots[name]["pos"]
            local = mb.bake_uv().translated((-piv[0], -piv[1], -piv[2]))   # texture fixed in the lift frame first
            o = local.to_object(f"{aid}_{name}_LOD{lod}", materials, collection)
            o.parent = empties[name]
            objs.append(o)
            entry["parts"][name] = mb.tri_count()
            entry["digest"][name] = mb.digest()
        entry["total"] = entry["body"] + sum(entry["parts"].values())
        report["lods"].append(entry)
    return objs, report, split


def export_fbx(objs, path):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={"EMPTY", "MESH"},
                             apply_unit_scale=True, axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE",
                             colors_type="LINEAR", use_triangles=True, bake_space_transform=True,
                             bake_anim=False, add_leaf_bones=False, path_mode="RELATIVE")


def spins(name, piv):
    """A wheel turning on its axle: sheaves and bullwheels, unless the pivot says otherwise."""
    return piv.get("spin", name == "bullwheel" or name.startswith("sheave_"))


def centring_error(mb, piv):
    """Distance (m) between a part's bounds centre and its pivot's axle, measured across the axle: the bounds are taken
    in the plane square to the axle (the lift frame's own axes when the axle is one of them, as it is for every wheel
    but the D6's tilted bullwheel)."""
    p = Vector(piv["pos"])
    axis = Vector(piv["axis"]).normalized()
    unit = [Vector(e) for e in ((1, 0, 0), (0, 1, 0), (0, 0, 1))]
    along = [e for e in unit if abs(abs(axis.dot(e)) - 1.0) < 1e-9]
    if along:
        e1, e2 = [e for e in unit if e is not along[0]]
    else:
        e1 = axis.cross(unit[1] if abs(axis.dot(unit[1])) < 0.9 else unit[0]).normalized()
        e2 = axis.cross(e1).normalized()
    pts = [Vector(frame.lift(v)) - p for v in mb.verts]
    d1 = [q.dot(e1) for q in pts]
    d2 = [q.dot(e2) for q in pts]
    return Vector(((min(d1) + max(d1)) / 2, (min(d2) + max(d2)) / 2)).length


def pivot_centering_error(base, split):
    """Max centring error (m) over the parts that must be centred: wheels turning on their axles (pivot "spin"). Other
    parts swing about a pin off their middle or not, which build_lifts records as the pivot's "hinge" flag."""
    worst = 0.0
    for _, parts in split:
        for name, mb in parts.items():
            if spins(name, base.pivots[name]):
                worst = max(worst, centring_error(mb, base.pivots[name]))
    return worst


def hinged(base, split, name):
    """True when a pivot's part is off its axle at any LOD (contract M1 "hinge"), or the builder says so."""
    if base.pivots[name].get("hinge"):
        return True
    return any(centring_error(parts[name], base.pivots[name]) > 0.001 for _, parts in split if name in parts)
