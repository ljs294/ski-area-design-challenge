"""Blender preview materials that mirror LiftStructure.shader, for the review renders.

Palette swatch (UV2 "Palette") -> albedo, metallic, smoothness; livery tint where Data.y = 1; snow where
Data.x > 0 on up-facing surfaces; vertex AO darkens. The FBX only carries the material names; Unity builds
its own materials (LiftImport.cs).
"""
import bpy

SLOTS = ("LiftStructure", "LiftGlass")
CHAIR = "LiftChair"
SNOW_RGB = (0.93, 0.95, 0.98)


def _sock(sockets, name, kind="RGBA"):
    """A mix node has float, vector and colour sockets under the same name; pick by name and type."""
    return next(s for s in sockets if s.name == name and s.type == kind)


def _nodes(m):
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    return nt


def linear(rgb):
    """sRGB (0-1) to linear, as Unity converts a material colour in a linear-colour project."""
    return tuple(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in rgb)


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
    tex.name = "PaletteTex"
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
    livery.inputs["B"].default_value = (*linear(livery_rgb), 1.0)
    L(sep.outputs["Y"], livery.inputs["Factor"])
    base = tex.outputs["Color"]
    cavity = None
    if detail_img is not None:   # detail atlas, sampled like LiftStructure.shader: tile in UV0.x's integer part
        d_uv = N("ShaderNodeUVMap")
        d_uv.uv_map = "UVMap"
        sep_d = N("ShaderNodeSeparateXYZ")
        L(d_uv.outputs["UV"], sep_d.inputs["Vector"])
        tile = math("FLOOR", math("DIVIDE", sep_d.outputs["X"], 256.0))
        lx = math("SUBTRACT", sep_d.outputs["X"], math("MULTIPLY", tile, 256.0))
        du = math("MULTIPLY", math("ADD", math("MODULO", tile, 4.0), math("FRACT", lx)), 0.25)
        dv = math("MULTIPLY", math("ADD", math("FLOOR", math("DIVIDE", tile, 4.0)), math("FRACT", sep_d.outputs["Y"])), 0.25)
        comb = N("ShaderNodeCombineXYZ")
        L(du, comb.inputs["X"])
        L(dv, comb.inputs["Y"])
        dtex = N("ShaderNodeTexImage")
        dtex.name = "DetailTex"
        dtex.image = detail_img
        dtex.interpolation = "Linear"
        L(comb.outputs["Vector"], dtex.inputs["Vector"])
        sep_c = N("ShaderNodeSeparateColor")
        L(dtex.outputs["Color"], sep_c.inputs["Color"])
        bright = math("MULTIPLY", dtex.outputs["Alpha"], 2.0)
        cavity = math("MULTIPLY", sep_c.outputs["Blue"], 2.0, clamp=True)
        lit = N("ShaderNodeMix")
        lit.data_type = "RGBA"
        lit.blend_type = "MULTIPLY"
        lit.inputs["Factor"].default_value = 1.0
        L(base, _sock(lit.inputs, "A"))
        L(bright, _sock(lit.inputs, "B"))
        base = _sock(lit.outputs, "Result")
        ncol = N("ShaderNodeCombineColor")
        L(sep_c.outputs["Red"], ncol.inputs["Red"])
        L(sep_c.outputs["Green"], ncol.inputs["Green"])
        ncol.inputs["Blue"].default_value = 1.0
        nmap = N("ShaderNodeNormalMap")
        nmap.uv_map = "UVMap"
        L(ncol.outputs["Color"], nmap.inputs["Color"])
        L(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    L(base, _sock(livery.inputs, "A"))

    ao = N("ShaderNodeVertexColor")
    ao.layer_name = "AO"
    ao_mix = N("ShaderNodeMix")
    ao_mix.data_type = "RGBA"
    ao_mix.blend_type = "MULTIPLY"
    ao_mix.inputs["Factor"].default_value = 1.0
    L(livery.outputs["Result"], ao_mix.inputs["A"])
    if cavity is None:
        L(ao.outputs["Color"], ao_mix.inputs["B"])
    else:
        occl = N("ShaderNodeMix")
        occl.data_type = "RGBA"
        occl.blend_type = "MULTIPLY"
        occl.inputs["Factor"].default_value = 1.0
        L(ao.outputs["Color"], _sock(occl.inputs, "A"))
        L(cavity, _sock(occl.inputs, "B"))
        L(_sock(occl.outputs, "Result"), _sock(ao_mix.inputs, "B"))

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
    lv.outputs[0].default_value = (*linear(livery_rgb), 1.0)
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
            n.outputs[0].default_value = (*linear(rgb), 1.0)


def use_palette(materials, img):
    """Points every material's palette texture at img (Workbench previews need an opaque copy: its alpha is
    the packed metallic/smoothness, which Workbench would read as transparency)."""
    for m in materials:
        for n in m.node_tree.nodes:
            if n.type == "TEX_IMAGE" and n.name == "PaletteTex":
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


def detail_image(pixels, path):
    """The detail atlas as a non-colour image, saved beside the palette."""
    import numpy as np
    size = pixels.shape[0]
    img = bpy.data.images.get("lift_trim") or bpy.data.images.new("lift_trim", size, size, alpha=True)
    img.colorspace_settings.name = "Non-Color"
    img.alpha_mode = "STRAIGHT"
    img.pixels.foreach_set(np.ascontiguousarray(pixels, dtype=np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    return img
