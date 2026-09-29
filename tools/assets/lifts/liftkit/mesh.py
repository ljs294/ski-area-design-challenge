"""MeshBuilder: vertices, faces and the per-face data the lift shader reads (see ../README.md).

Per face: palette class (-> UV2 swatch), snow capacity (UV1.x), livery mask (UV1.y), material slot
(0 structure, 1 glass), flat or smooth shading, and per-corner UV0 (box-mapped detail-atlas coordinates).
Vertices are stored in Blender space; faces are oriented against an explicit outward direction.
"""
import hashlib
import struct

import bpy
from mathutils import Vector, geometry

from . import frame, palette, textures

STRUCTURE, GLASS = 0, 1
TILE_SPAN = 256.0   # UV0.x = tile * TILE_SPAN + metres; the shader splits it back (LiftStructure.shader)
ORIGIN = 128.0      # keeps box-mapped metres positive
FLUSH_GAP = 0.002   # how far separate_flush pulls the smaller of two flush faces into its own part (m)


def detail_uv(points_lift, tile):
    """Box-mapped UV0 for one face: its corners projected on the plane of its dominant axis, in metres, with
    the detail-atlas tile in the integer part of U (see textures.py)."""
    n = newell([Vector(p) for p in points_lift])
    ax = max(range(3), key=lambda k: abs(n[k]))
    a, b = {0: (1, 2), 1: (0, 2), 2: (0, 1)}[ax]   # u-facing -> (v, w); v-facing -> (u, w); up -> (u, v)
    return [(tile * TILE_SPAN + p[a] + ORIGIN, p[b] + ORIGIN) for p in points_lift]


def _overlap(tris_a, tris_b, eps=0.001):
    """True if two sets of 2D triangles overlap by more than eps (separating-axis test; touching is not)."""
    for p in tris_a:
        for q in tris_b:
            for tri in (p, q):
                sep = False
                for i in range(3):
                    e = tri[(i + 1) % 3] - tri[i]
                    ln = e.length
                    if ln < 1e-9:
                        continue
                    nx, ny = -e.y / ln, e.x / ln
                    pa = [v.x * nx + v.y * ny for v in p]
                    pb = [v.x * nx + v.y * ny for v in q]
                    if max(pa) <= min(pb) + eps or max(pb) <= min(pa) + eps:
                        sep = True
                        break
                if sep:
                    break
            else:
                return True
    return False


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
        uv = list(uv0) if uv0 is not None else None   # None: box-mapped when the object is made
        if outward_lift is not None:
            n = newell([self.verts[i] for i in idx])
            if n.dot(frame.b(outward_lift)) < 0:
                idx.reverse()
                if uv is not None:
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

    def separate_flush(self, gap=FLUSH_GAP):
        """Parts are built as overlapping solids, so where two meet flush (a brace across a frame, spoke tops level
        with the rim) two faces of one slot share a plane, face the same way and overlap: they z-fight in Unity and
        render black in Cycles. Pull the smaller face of each such pair `gap` into its own part, moving its
        corners, so the part shrinks by that much. Deterministic: faces in index order. Returns faces moved."""
        planes = {}
        for fi, f in enumerate(self.faces):
            pts = [self.verts[i] for i in f]
            n = newell(pts)
            if n.length < 1e-12:
                continue
            n = n.normalized()
            key = (tuple(round(c, 3) for c in n), round(n.dot(pts[0]), 3), self.style[fi].slot)
            planes.setdefault(key, []).append(fi)
        moved = set()
        for (nk, _, _), group in planes.items():
            if len(group) < 2:
                continue
            n = Vector(nk).normalized()
            ax = max(range(3), key=lambda k: abs(n[k]))
            a, b = [k for k in range(3) if k != ax]
            info = []
            for fi in group:
                flat = [Vector((self.verts[i][a], self.verts[i][b], 0.0)) for i in self.faces[fi]]
                tris = [[flat[k] for k in tri] for tri in geometry.tessellate_polygon([flat])]
                area = sum(abs((t[1] - t[0]).cross(t[2] - t[0]).z) for t in tris) / 2
                info.append((fi, tris, area))
            for x in range(len(info)):
                for y in range(x + 1, len(info)):
                    (fa, ta, aa), (fb, tb, ab) = info[x], info[y]
                    if fa in moved or fb in moved or not _overlap(ta, tb):
                        continue
                    small = fb if ab <= aa else fa
                    for i in set(self.faces[small]):
                        self.verts[i] = self.verts[i] - n * gap
                    moved.add(small)
        return len(moved)

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
        uvs = [u if u is not None else detail_uv([frame.lift(self.verts[i]) for i in f], textures.tile_of(s))
               for f, s, u in zip(self.faces, self.style, self.uv0)]
        uv0 = mesh.uv_layers.new(name="UVMap")
        uv0.data.foreach_set("uv", [c for fi, k in corners for c in uvs[fi][k]])
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
