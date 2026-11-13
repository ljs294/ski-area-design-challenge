"""Builds a rigged figure (armature + one skinned object per LOD) in the current Blender scene."""
import json
import os

import bpy

from . import body, gear, outfit, rig
from .mesh import MATS, Mesh

# The ladder lives in budgets.json beside the build script (the Unity import reads the same file): the most triangles
# of a skier with skis and poles, how far each level is used, and how many bones may move one vertex.
with open(os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "budgets.json")) as _f:
    _LODS = json.load(_f)["figure"]["lods"]
LOD_COUNT = len(_LODS)
assert LOD_COUNT == body.LODS, "budgets.json and body.py disagree on the number of LODs"
BUDGET = tuple(row["maxTris"] for row in _LODS)
UNTIL_M = tuple(row["untilM"] for row in _LODS)
INFLUENCES = tuple(row["boneInfluences"] for row in _LODS)

ROUGH = {"lens": 0.10, "helmet": 0.35, "deck": 0.40, "metal": 0.40, "boot": 0.45, "base": 0.50}


def _lin(c):
    return tuple(((x + 0.055) / 1.055) ** 2.4 if x > 0.04045 else x / 12.92 for x in c)


def materials(tag, colours):
    """One Principled material per id from {id: sRGB}; each face's keyed tone (the colour layer's green) scales it."""
    out = {}
    for name in MATS:
        m = bpy.data.materials.new(f"{tag}_{name}")
        m.use_nodes = True
        nodes, links = m.node_tree.nodes, m.node_tree.links
        b = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        attr = nodes.new("ShaderNodeAttribute")
        attr.attribute_name = "matid"
        sep = nodes.new("ShaderNodeSeparateColor")
        gain = nodes.new("ShaderNodeMath")
        gain.operation = "ADD"
        gain.inputs[1].default_value = 0.5
        tint = nodes.new("ShaderNodeVectorMath")
        tint.operation = "SCALE"
        tint.inputs[0].default_value = _lin(colours[name])
        links.new(attr.outputs["Color"], sep.inputs[0])
        links.new(sep.outputs[1], gain.inputs[0])
        links.new(gain.outputs[0], tint.inputs["Scale"])
        links.new(tint.outputs["Vector"], b.inputs["Base Color"])
        b.inputs["Roughness"].default_value = ROUGH.get(name, 0.80)
        b.inputs["Metallic"].default_value = 1.0 if name == "lens" else 0.0
        out[name] = m
    return out


def mesh(kind, lod):
    m = Mesh()
    m.merge(body.figure(lod, kind))
    m.merge(gear.skier_gear(lod) if kind == "skier" else gear.board(lod))
    m.limit_influences(INFLUENCES[lod])
    return m


def figure(kind, name, collection, lods=(0,), colours=None):
    """Returns (armature, {lod: object}, {lod: Mesh})."""
    arm = rig.make_armature(name, kind, collection)
    if colours is None:
        colours = outfit.CONCEPT_SKIER if kind == "skier" else outfit.CONCEPT_BOARDER
    mats = materials(name, colours)
    objs, meshes = {}, {}
    for lod in lods:
        meshes[lod] = mesh(kind, lod)
        objs[lod] = rig.make_object(f"{name}_LOD{lod}", meshes[lod], mats, arm, collection)
    return arm, objs, meshes
