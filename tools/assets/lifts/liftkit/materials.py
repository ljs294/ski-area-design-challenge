"""Blender preview materials that mirror LiftStructure.shader, for the review renders.

Palette swatch (UV2 "Palette") -> albedo, metallic, smoothness; livery tint where Data.y = 1; snow where
Data.x > 0 on up-facing surfaces; vertex AO darkens. The FBX only carries the material names; Unity builds
its own materials (LiftImport.cs).
"""
import bpy

SLOTS = ("LiftStructure", "LiftGlass")
CHAIR = "LiftChair"
SNOW_RGB = (0.93, 0.95, 0.98)


def _nodes(m):
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    return nt


def make(name, palette_img, livery_rgb=(0.72, 0.12, 0.09), snow=0.0, glass=False, detail_img=None):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    nt = _nodes(m)
    N, L = nt.nodes.new, nt.links.new
    out = N("ShaderNodeOutputMaterial")
    bsdf = N("ShaderNodeBsdfPrincipled")
    L(bsdf.outputs["BSDF"], out.inputs["Surface"])

    pal_uv = N("ShaderNodeUVMap")
    pal_uv.uv_map = "Palette"
    tex = N("ShaderNodeTexImage")
    tex.image = palette_img
    tex.interpolation = "Closest"
    L(pal_uv.outputs["UV"], tex.inputs["Vector"])

    data_uv = N("ShaderNodeUVMap")
    data_uv.uv_map = "Data"
    sep = N("ShaderNodeSeparateXYZ")
    L(data_uv.outputs["UV"], sep.inputs["Vector"])

    # alpha -> metallic (A >= 0.5) and smoothness ((A mod 0.5) * 2)
    def math(op, a, b=None, clamp=False):
        n = N("ShaderNodeMath")
        n.operation = op
        n.use_clamp = clamp
        for i, x in enumerate((a, b)):
            if x is None:
                continue
            if isinstance(x, (int, float)):
                n.inputs[i].default_value = x
            else:
                L(x, n.inputs[i])
        return n.outputs[0]

    metallic = math("GREATER_THAN", tex.outputs["Alpha"], 0.499)
    smooth = math("MULTIPLY", math("MODULO", tex.outputs["Alpha"], 0.5), 2.0)
    rough = math("SUBTRACT", 1.0, smooth, clamp=True)

    livery = N("ShaderNodeMix")
    livery.data_type = "RGBA"
    livery.blend_type = "MULTIPLY"
    livery.inputs["B"].default_value = (*livery_rgb, 1.0)
    L(sep.outputs["Y"], livery.inputs["Factor"])
    L(tex.outputs["Color"], livery.inputs["A"])

    ao = N("ShaderNodeVertexColor")
    ao.layer_name = "AO"
    ao_mix = N("ShaderNodeMix")
    ao_mix.data_type = "RGBA"
    ao_mix.blend_type = "MULTIPLY"
    ao_mix.inputs["Factor"].default_value = 1.0
    L(livery.outputs["Result"], ao_mix.inputs["A"])
    L(ao.outputs["Color"], ao_mix.inputs["B"])

    # snow: capacity x snow load x up-facing (true normal, like the shader's geometric normal)
    geo = N("ShaderNodeNewGeometry")
    sepn = N("ShaderNodeSeparateXYZ")
    L(geo.outputs["True Normal"], sepn.inputs["Vector"])
    up = N("ShaderNodeMapRange")
    up.inputs["From Min"].default_value = 0.3
    up.inputs["From Max"].default_value = 0.8
    up.interpolation_type = "SMOOTHSTEP"
    L(sepn.outputs["Z"], up.inputs["Value"])
    load = N("ShaderNodeValue")
    load.name = "SnowLoad"
    load.outputs[0].default_value = snow
    snow_amt = math("MULTIPLY", math("MULTIPLY", sep.outputs["X"], load.outputs[0]), up.outputs["Result"], clamp=True)
    col = N("ShaderNodeMix")
    col.data_type = "RGBA"
    col.inputs["B"].default_value = (*SNOW_RGB, 1.0)
    L(snow_amt, col.inputs["Factor"])
    L(ao_mix.outputs["Result"], col.inputs["A"])
    L(col.outputs["Result"], bsdf.inputs["Base Color"])
    met = N("ShaderNodeMix")
    met.inputs["B"].default_value = 0.0
    L(snow_amt, met.inputs["Factor"])
    L(metallic, met.inputs["A"])
    L(met.outputs["Result"], bsdf.inputs["Metallic"])
    rgh = N("ShaderNodeMix")
    rgh.inputs["B"].default_value = 0.7
    L(snow_amt, rgh.inputs["Factor"])
    L(rough, rgh.inputs["A"])
    L(rgh.outputs["Result"], bsdf.inputs["Roughness"])

    lv = N("ShaderNodeRGB")
    lv.name = "Livery"
    lv.outputs[0].default_value = (*livery_rgb, 1.0)
    L(lv.outputs[0], livery.inputs["B"])
    if glass:
        bsdf.inputs["Alpha"].default_value = 0.6
    nt.nodes.active = tex
    return m


def set_snow(materials, snow):
    for m in materials:
        n = m.node_tree.nodes.get("SnowLoad")
        if n:
            n.outputs[0].default_value = snow


def set_livery(materials, rgb):
    for m in materials:
        n = m.node_tree.nodes.get("Livery")
        if n:
            n.outputs[0].default_value = (*rgb, 1.0)


def use_palette(materials, img):
    """Points every material's palette texture at img (Workbench previews need an opaque copy: its alpha is
    the packed metallic/smoothness, which Workbench would read as transparency)."""
    for m in materials:
        for n in m.node_tree.nodes:
            if n.type == "TEX_IMAGE":
                n.image = img


def opaque_copy(img):
    import numpy as np
    name = img.name + "_opaque"
    out = bpy.data.images.get(name) or bpy.data.images.new(name, img.size[0], img.size[1], alpha=True)
    px = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    px[3::4] = 1.0
    out.colorspace_settings.name = "sRGB"
    out.pixels.foreach_set(px)
    return out


def palette_image(pixels, path):
    import numpy as np
    size = pixels.shape[0]
    img = bpy.data.images.get("lift_palette") or bpy.data.images.new("lift_palette", size, size, alpha=True)
    img.colorspace_settings.name = "sRGB"
    img.alpha_mode = "STRAIGHT"
    img.pixels.foreach_set(np.ascontiguousarray(pixels, dtype=np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    return img
