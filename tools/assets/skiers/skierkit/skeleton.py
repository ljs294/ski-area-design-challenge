"""The shared rest skeleton of the skier and the snowboarder.

Bone lengths and joint heights come from the MakeHuman base mesh (hm08, CC0) with its game-engine rig, scaled to a
1.75 m adult, then stood on a 40 mm boot sole with the legs straight down and the arms 45 degrees out (see
provenance.json). Blender axes: +X the figure's left, -Y forward, +Z up; the origin is on the snow between the boots.
"""
from mathutils import Vector

SOLE = 0.040
HIP_X = 0.100
SHOULDER = Vector((0.185, 0.0, 1.415))
UPPER_ARM, FOREARM, HAND = 0.270, 0.250, 0.100
ARM_DIR = Vector((0.7071, 0.0, -0.7071))
HIP_Z, KNEE_Z, ANKLE_Z = 0.950, 0.510, 0.115
STANCE = 0.52          # snowboard binding spacing
POLE = 1.20


def _arm(s):
    d = Vector((ARM_DIR.x * s, 0.0, ARM_DIR.z))
    sh = Vector((SHOULDER.x * s, SHOULDER.y, SHOULDER.z))
    el = sh + d * UPPER_ARM
    wr = el + d * FOREARM
    return sh, el, wr, wr + d * HAND


def fist(s):
    """The middle of the closed hand, where a pole is held."""
    sh, el, wr, tip = _arm(s)
    return wr + (tip - wr) * 0.7


def bones(kind):
    """[(name, head, tail, parent)], parents first. kind: "skier" or "boarder"."""
    b = [("root", (0, 0, 0), (0, 0.1, 0), None),
         ("pelvis", (0, 0.01, 0.975), (0, 0.0, 1.020), "root"),
         ("spine_01", (0, 0.0, 1.020), (0, 0.0, 1.095), "pelvis"),
         ("spine_02", (0, 0.0, 1.095), (0, 0.0, 1.190), "spine_01"),
         ("spine_03", (0, 0.0, 1.190), (0, 0.0, 1.350), "spine_02"),
         ("neck", (0, 0.005, 1.515), (0, 0.0, 1.630), "spine_03"),
         ("head", (0, 0.0, 1.630), (0, 0.0, 1.790), "neck")]
    for s, sx in ((1, "_l"), (-1, "_r")):
        sh, el, wr, tip = _arm(s)
        b += [("clavicle" + sx, (0.03 * s, 0.0, 1.410), sh, "spine_03"),
              ("upperarm" + sx, sh, el, "clavicle" + sx),
              ("lowerarm" + sx, el, wr, "upperarm" + sx),
              ("hand" + sx, wr, tip, "lowerarm" + sx),
              ("thigh" + sx, (HIP_X * s, 0.0, HIP_Z), (HIP_X * s, 0.0, KNEE_Z), "pelvis"),
              ("calf" + sx, (HIP_X * s, 0.0, KNEE_Z), (HIP_X * s, 0.0, ANKLE_Z), "thigh" + sx),
              ("foot" + sx, (HIP_X * s, 0.0, ANKLE_Z), (HIP_X * s, -0.16, SOLE), "calf" + sx)]
        if kind == "skier":
            b += [("ski" + sx, (HIP_X * s, 0.0, 0.0), (HIP_X * s, -0.3, 0.0), "foot" + sx),
                  ("pole" + sx, fist(s), fist(s) + Vector((0, 0, -0.3)), "hand" + sx)]
    if kind == "boarder":
        b.append(("board", (0, 0, 0), (0.3, 0, 0), "root"))
    return [(n, Vector(h), Vector(t), p) for n, h, t, p in b]


def rest(kind):
    return {n: (h, t, p) for n, h, t, p in bones(kind)}
