"""A small low-poly mesh builder with bone weights and material ids.

Everything is in Blender axes, metres: +X is the figure's left, -Y is forward, +Z is up; the origin is on the snow
between the boots. Faces are flat shaded (the faceted look of the concept art). A face's material id is one of
MATS; a vertex's weights are {bone name: weight}.
"""
import math

from mathutils import Matrix, Vector

MATS = ["jacket", "block", "pants", "patch", "helmet", "lens", "strap", "skin", "glove", "boot", "deck", "metal",
        "base", "accent", "liner"]
MAT = {name: i for i, name in enumerate(MATS)}


def ring(c, u, v, ru, rv, n, a0=0.0, a1=None, squash=None, power=2.0):
    """n points round centre c in the plane of unit vectors u, v. A closed ring when a1 is None, else an arc from a0
    to a1 (radians, both ends included). squash(a) -> (ku, kv) scales a point by its angle."""
    c, u, v = Vector(c), Vector(u), Vector(v)
    pts = []
    for j in range(n):
        a = a0 + (2 * math.pi * j / n if a1 is None else (a1 - a0) * j / (n - 1))
        ku, kv = squash(a) if squash else (1.0, 1.0)
        ca, sa = math.cos(a), math.sin(a)
        if power != 2.0:   # a superellipse: boxier sections for the shell jacket and the trouser legs
            ca = math.copysign(abs(ca) ** (2.0 / power), ca)
            sa = math.copysign(abs(sa) ** (2.0 / power), sa)
        pts.append(c + u * (ru * ku * ca) + v * (rv * kv * sa))
    return pts


class Mesh:
    def __init__(self):
        self.verts = []
        self.weights = []
        self.faces = []     # (vertex indices, material id)

    def add(self, pts, w):
        """w: one weight dict for every point, or a list with one dict per point."""
        i0 = len(self.verts)
        for i, p in enumerate(pts):
            self.verts.append(Vector(p))
            self.weights.append(dict(w[i] if isinstance(w, list) else w))
        return list(range(i0, i0 + len(pts)))

    def face(self, idx, mat):
        self.faces.append((tuple(idx), mat if isinstance(mat, int) else MAT[mat]))

    def _mat(self, mat, idx):
        if callable(mat):
            c = sum((self.verts[i] for i in idx), Vector()) / len(idx)
            return mat(c)
        return mat

    def loft(self, rings, mat, closed=True, cap0=None, cap1=None, inside=None, stagger=None):
        """rings: [(points, weights)], ordered along U x V of their planes (see ring), so faces point outward.
        mat: a name, or f(face centre) -> name. cap0/cap1 close the ends with a fan. inside: a material for a second
        skin facing inward (a collar or a hood seen from above). stagger: one flag per ring, true where the ring is
        turned half a step (see half_step): those rows join in triangles along the short diagonal, as hand-cut
        low-poly cloth does, where level rings would show as hoops."""
        rows = [self.add(p, w) for p, w in rings]
        n = len(rows[0])
        last = n if closed else n - 1
        for i, (a, b) in enumerate(zip(rows, rows[1:])):
            seg = mat[i] if isinstance(mat, list) else mat      # a list gives one material (or rule) per segment
            turn = 0 if stagger is None else int(bool(stagger[i + 1])) - int(bool(stagger[i]))
            for j in range(last):
                k = (j + 1) % n
                idx = (a[j], a[k], b[k], b[j])
                if turn > 0:
                    cut = [(a[j], a[k], b[j]), (a[k], b[k], b[j])]
                elif turn < 0:
                    cut = [(a[j], a[k], b[k]), (a[j], b[k], b[j])]
                else:
                    cut = [idx]
                for f in cut:
                    self.face(f, self._mat(seg, f))
                    if inside is not None:
                        self.face(f[::-1], inside)
        if cap0 is not None:
            self.fan(rows[0][::-1], cap0)
        if cap1 is not None:
            self.fan(rows[-1], cap1)
        return rows

    def tube(self, path, radii, n, w, mat, cap0=None, cap1=None, hint=(0.0, 0.0, 1.0), power=2.0, inside=None,
             a0=0.0, a1=None):
        """A loft along a polyline. radii: one (ru, rv) for all points or one per point (rv is toward `hint`);
        w: one weight dict or one per point. The section keeps its twist from point to point."""
        path = [Vector(p) for p in path]
        rows = []
        v = Vector(hint)
        for i, p in enumerate(path):
            d = (path[min(i + 1, len(path) - 1)] - path[max(i - 1, 0)]).normalized()
            u, v = frame_along(d, v)
            ru, rv = radii[i] if isinstance(radii, list) else radii
            rows.append((ring(p, u, v, ru, rv, n, a0, a1, power=power), w[i] if isinstance(w, list) else w))
        return self.loft(rows, mat, closed=a1 is None, cap0=cap0, cap1=cap1, inside=inside)

    def facet(self, seed, amount, mats, split=True, still=None):
        """The hand-cut look of the concept art, for cloth only: every vertex of the named materials moves a few
        millimetres along its normal (keyed on where it is, so the same build gives the same folds), and each quad
        is cut into two triangles along a keyed diagonal, so flat shading shows irregular facets, not tube bands.
        still(point) holds a vertex where it is (a hem must stay a clean line)."""
        ids = {MAT[x] for x in mats}
        normal = [Vector() for _ in self.verts]
        hard = set()
        for idx, mat in self.faces:
            if len(idx) < 3:
                continue
            a, b, c = (self.verts[i] for i in idx[:3])
            nrm = (b - a).cross(c - a)
            for i in idx:
                if mat in ids:
                    normal[i] += nrm
                else:
                    hard.add(i)
        for i, p in enumerate(self.verts):
            if i in hard or normal[i].length < 1e-12 or (still is not None and still(p)):
                continue
            self.verts[i] = p + normal[i].normalized() * (amount * (_hash01(seed, p) * 2.0 - 1.0))
        if not split:
            return
        out = []
        for idx, mat in self.faces:
            if mat in ids and len(idx) == 4:
                a, b, c, d = idx
                if _hash01(seed + 7, (self.verts[a] + self.verts[c]) * 0.5) < 0.5:
                    out += [((a, b, c), mat), ((a, c, d), mat)]
                else:
                    out += [((a, b, d), mat), ((b, c, d), mat)]
            else:
                out.append((idx, mat))
        self.faces = out

    def fan(self, idx, mat):
        """A flat cap as one polygon (triangulated at export)."""
        self.face(idx, self._mat(mat, idx))

    def box(self, lo, hi, w, mat, m=None):
        """An axis-aligned box from lo to hi, then moved by matrix m."""
        x0, y0, z0 = lo
        x1, y1, z1 = hi
        p = [Vector(q) for q in ((x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
                                 (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1))]
        if m is not None:
            p = [m @ q for q in p]
        i = self.add(p, w)
        for f in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
            idx = tuple(i[k] for k in f)
            self.face(idx, self._mat(mat, idx))
        return i

    def merge(self, other, m=None):
        i0 = len(self.verts)
        for p, w in zip(other.verts, other.weights):
            self.verts.append(m @ p if m is not None else p.copy())
            self.weights.append(dict(w))
        for idx, mat in other.faces:
            self.faces.append((tuple(i + i0 for i in idx), mat))
        return self

    def mirrored(self, swap=None):
        """A copy mirrored across X=0 (left <-> right), bone names swapped _l <-> _r, winding kept outward."""
        out = Mesh()
        for p, w in zip(self.verts, self.weights):
            out.verts.append(Vector((-p.x, p.y, p.z)))
            out.weights.append({_swap(b): x for b, x in w.items()})
        for idx, mat in self.faces:
            out.faces.append((tuple(reversed(idx)), mat))
        return out

    def tris(self):
        return sum(len(idx) - 2 for idx, _ in self.faces)

    def limit_influences(self, k):
        for w in self.weights:
            keep = sorted(w.items(), key=lambda t: -t[1])[:k]
            s = sum(x for _, x in keep)
            w.clear()
            w.update({b: x / s for b, x in keep})

    def max_influences(self):
        return max(len(w) for w in self.weights) if self.weights else 0


def half_step(n):
    """The angle that turns an n-sided ring half a side (for staggered lofts)."""
    return math.pi / n


def face_tone(seed, centre):
    """A keyed tone for one face, -1..1: the painted facets of the concept art (kept in the colour layer)."""
    return _hash01(seed, centre) * 2.0 - 1.0


def _hash01(seed, p):
    """A keyed value in 0..1 from a point (to the millimetre): the same point always gives the same value."""
    h = (int(round(p.x * 1000)) * 73856093) ^ (int(round(p.y * 1000)) * 19349663) ^ (int(round(p.z * 1000)) * 83492791)
    h = (h ^ (seed * 2654435761)) & 0xFFFFFFFF
    h = ((h ^ (h >> 15)) * 0x2C1B3C6D) & 0xFFFFFFFF
    h = ((h ^ (h >> 12)) * 0x297A2D39) & 0xFFFFFFFF
    return ((h ^ (h >> 15)) & 0xFFFFFF) / float(0x1000000)


def _swap(b):
    if b.endswith("_l"):
        return b[:-2] + "_r"
    if b.endswith("_r"):
        return b[:-2] + "_l"
    return b


def frame_along(d, hint=(0.0, -1.0, 0.0)):
    """Unit vectors (u, v) square to direction d with u x v = d; v leans toward hint."""
    d = Vector(d).normalized()
    h = Vector(hint)
    v = (h - d * h.dot(d))
    if v.length < 1e-6:
        h = Vector((1.0, 0.0, 0.0))
        v = h - d * h.dot(d)
    v.normalize()
    u = v.cross(d)
    return u, v


def rot(pitch=0.0, yaw=0.0, roll=0.0):
    """Degrees. pitch: lean forward (toward -Y). yaw: turn to the figure's left. roll: lean to the figure's left."""
    return (Matrix.Rotation(math.radians(yaw), 3, "Z") @ Matrix.Rotation(math.radians(pitch), 3, "X")
            @ Matrix.Rotation(math.radians(roll), 3, "Y"))
