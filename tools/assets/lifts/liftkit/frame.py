"""The lift frame and its one conversion to Blender.

Every lift asset is written in the lift frame, in metres:
  u  along the line, toward the other terminal   (Unity +Z)
  v  to the right, looking along u               (Unity +X)
  w  up                                          (Unity +Y)
The origin is the mast centreline at the 0.00 level (the snow or deck surface where riders load or unload).

The FBX export (axis_forward -Z, axis_up Y, bake_space_transform) and Unity's import turn Blender (x, y, z)
into Unity (-x, z, -y), so Blender = (-v, -u, w). The mapping is a reflection (determinant -1): geometry code
never reasons about winding in the lift frame; MeshBuilder orients faces in Blender space instead.
"""
from mathutils import Vector

MM = 0.001


def b(p):
    """Lift-frame point or direction (u, v, w) -> Blender Vector."""
    return Vector((-p[1], -p[0], p[2]))


def lift(p):
    """Blender point -> lift frame (u, v, w)."""
    return (-p[1], -p[0], p[2])


def unity(p_lift):
    """Lift frame (u, v, w) -> Unity (x, y, z), for reports."""
    u, v, w = p_lift
    return (v, w, u)


def mm(*values):
    """Millimetres to metres (one value or several)."""
    return values[0] * MM if len(values) == 1 else tuple(x * MM for x in values)
