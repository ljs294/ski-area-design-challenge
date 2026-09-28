"""MeshBuilder: vertices, faces and the per-face data the lift shader reads (see ../README.md).

Per face: palette class (-> UV2 swatch), snow capacity (UV1.x), livery mask (UV1.y), material slot
(0 structure, 1 glass), flat or smooth shading, and per-corner UV0 (detail/trim coordinates).
Vertices are stored in Blender space; faces are oriented against an explicit outward direction.
"""
import hashlib
import struct

import bpy
from mathutils import Vector

from . import frame, palette

STRUCTURE, GLASS = 0, 1
PLAIN_UV = (0.5, 0.5)


def newell(points):
    n = Vector((0.0, 0.0, 0.0))
    for i in range(len(points)):
        a, c = points[i], points[(i + 1) % len(points)]
        n += Vector(((a.y - c.y) * (a.z + c.z), (a.z - c.z) * (a.x + c.x), (a.x - c.x) * (a.y + c.y)))
    return n


class Style:
    """How a primitive's faces are tagged. Copy with changes via .but(...)."""

    __slots__ = ("cls", "snow", "livery", "slot", "smooth", "trim")

    def __init__(self, cls="paint_grey", snow=0.0, livery=0.0, slot=STRUCTURE, smooth=False, trim=None):
        self.cls, self.snow, self.livery, self.slot, self.smooth, self.trim = cls, snow, livery, slot, smooth, trim

    def but(self, **kw):
        s = Style(self.cls, self.snow, self.livery, self.slot, self.smooth, self.trim)
        for k, v in kw.items():
            setattr(s, k, v)
        return s


class MeshBuilder:
    def __init__(self):
        self.verts = []          # Blender-space Vectors
        self.faces = []          # tuples of vertex indices
        self.style = []          # Style per face
        self.uv0 = []            # per face: list of (u, v) per corner

    # -- construction -------------------------------------------------------------------------
    def vert(self, p_lift):
        self.verts.append(frame.b(p_lift))
        return len(self.verts) - 1

    def verts_lift(self, pts):
        return [self.vert(p) for p in pts]

    def face(self, idx, style, outward_lift=None, uv0=None):
        """Adds a face; if outward_lift is given the face is flipped as needed to face that way."""
        idx = list(idx)
        uv = list(uv0) if uv0 is not None else [PLAIN_UV] * len(idx)
        if outward_lift is not None:
            n = newell([self.verts[i] for i in idx])
            if n.dot(frame.b(outward_lift)) < 0:
                idx.reverse()
                uv.reverse()
        self.faces.append(tuple(idx))
        self.style.append(style)
        self.uv0.append(uv)
        return len(self.faces) - 1

    def merge(self, other):
        off = len(self.verts)
        self.verts.extend(v.copy() for v in other.verts)
        self.faces.extend(tuple(i + off for i in f) for f in other.faces)
        self.style.extend(other.style)
        self.uv0.extend(other.uv0)
        return self

    def translated(self, d_lift):
        """A copy moved by d_lift (lift frame)."""
        m = MeshBuilder().merge(self)
        d = frame.b(d_lift)
        m.verts = [v + d for v in m.verts]
        return m

    # -- queries ------------------------------------------------------------------------------
    def tri_count(self):
        return sum(len(f) - 2 for f in self.faces)

    def bounds_lift(self):
        pts = [frame.lift(v) for v in self.verts]
        lo = tuple(min(p[i] for p in pts) for i in range(3))
        hi = tuple(max(p[i] for p in pts) for i in range(3))
        return lo, hi

    def digest(self):
        h = hashlib.sha1()
        for v in self.verts:
            h.update(struct.pack("<3i", *(round(c * 1e5) for c in v)))
        for f, s in zip(self.faces, self.style):
            h.update(struct.pack(f"<{len(f)}i", *f))
            h.update(f"{s.cls}{s.snow:.3f}{s.livery:.1f}{s.slot}{int(s.smooth)}{s.trim}".encode())
        return h.hexdigest()

    # -- Blender object -----------------------------------------------------------------------
    def to_object(self, name, materials, collection=None):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata([tuple(v) for v in self.verts], [], self.faces)
        mesh.update()
        for m in materials:
            mesh.materials.append(m)
        mesh.polygons.foreach_set("material_index", [s.slot for s in self.style])
        mesh.polygons.foreach_set("use_smooth", [s.smooth for s in self.style])
        corners = [(fi, k) for fi, f in enumerate(self.faces) for k in range(len(f))]
        # Blender keeps polygon corner order as given by from_pydata.
        uv0 = mesh.uv_layers.new(name="UVMap")
        uv0.data.foreach_set("uv", [c for fi, k in corners for c in self.uv0[fi][k]])
        data = mesh.uv_layers.new(name="Data")
        data.data.foreach_set("uv", [c for fi, k in corners for c in (self.style[fi].snow, self.style[fi].livery)])
        pal = mesh.uv_layers.new(name="Palette")
        pal.data.foreach_set("uv", [c for fi, k in corners for c in palette.swatch_uv(self.style[fi].cls)])
        mesh.uv_layers.active_index = 0
        col = mesh.color_attributes.new(name="AO", type="FLOAT_COLOR", domain="CORNER")
        col.data.foreach_set("color", [1.0] * (4 * len(corners)))
        mesh.color_attributes.active_color_name = "AO"
        obj = bpy.data.objects.new(name, mesh)
        (collection or bpy.context.scene.collection).objects.link(obj)
        return obj
