"""Primitives in the lift frame (u, v, w metres). Each adds faces to a MeshBuilder with a Style."""
import math

from mathutils import Vector

from .mesh import MeshBuilder, Style

U, V, W = Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))


def vec(p):
    return p if isinstance(p, Vector) else Vector(p)


def basis_along(d, up=W):
    """Unit axes (x along d, y, z) for a member along d; z leans toward `up`."""
    x = vec(d).normalized()
    ref = vec(up) if abs(x.dot(vec(up))) < 0.99 else (U if abs(x.dot(U)) < 0.9 else V)
    y = ref.cross(x).normalized()
    z = x.cross(y).normalized()
    return x, y, z


# -- boxes ------------------------------------------------------------------------------------
def obox(mb, center, axes, half, style, skip=()):
    """Oriented box: center, three unit axes, half sizes. skip: face keys like '-2' (bottom along axis 2)."""
    c = vec(center)
    a = [vec(x) * h for x, h in zip(axes, half)]
    corners = {}
    for i in (-1, 1):
        for j in (-1, 1):
            for k in (-1, 1):
                corners[(i, j, k)] = mb.vert(tuple(c + a[0] * i + a[1] * j + a[2] * k))
    quads = {
        "+0": [(1, -1, -1), (1, 1, -1), (1, 1, 1), (1, -1, 1)], "-0": [(-1, -1, -1), (-1, 1, -1), (-1, 1, 1), (-1, -1, 1)],
        "+1": [(-1, 1, -1), (1, 1, -1), (1, 1, 1), (-1, 1, 1)], "-1": [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)],
        "+2": [(-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)], "-2": [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1)],
    }
    out = []
    for key, q in quads.items():
        if key in skip:
            continue
        axis = int(key[1])
        normal = vec(axes[axis]) * (1 if key[0] == "+" else -1)
        out.append(mb.face([corners[k] for k in q], style, tuple(normal)))
    return out


def box(mb, lo, hi, style, skip=()):
    """Axis-aligned box between corners lo and hi."""
    lo, hi = vec(lo), vec(hi)
    return obox(mb, (lo + hi) / 2, (U, V, W), tuple((hi - lo) / 2), style, skip)


def beam(mb, p0, p1, width, height, style, up=W, skip_ends=False):
    """Rectangular member from p0 to p1 (width across, height toward `up`)."""
    p0, p1 = vec(p0), vec(p1)
    x, y, z = basis_along(p1 - p0, up)
    skip = ("-0", "+0") if skip_ends else ()
    return obox(mb, (p0 + p1) / 2, (x, y, z), ((p1 - p0).length / 2, width / 2, height / 2), style, skip)


def ibeam(mb, p0, p1, depth, flange, tf, tw, style, up=W):
    """I-section member: two flanges and a web (depth toward `up`)."""
    p0, p1 = vec(p0), vec(p1)
    x, y, z = basis_along(p1 - p0, up)
    half = (p1 - p0).length / 2
    mid = (p0 + p1) / 2
    obox(mb, mid + z * (depth / 2 - tf / 2), (x, y, z), (half, flange / 2, tf / 2), style)
    obox(mb, mid - z * (depth / 2 - tf / 2), (x, y, z), (half, flange / 2, tf / 2), style)
    obox(mb, mid, (x, y, z), (half, tw / 2, depth / 2 - tf), style, skip=("+2", "-2"))


def winding(poly):
    """+1 for a counter-clockwise 2D polygon, -1 for clockwise (sign of the shoelace area)."""
    n = len(poly)
    area2 = sum(poly[k][0] * poly[(k + 1) % n][1] - poly[(k + 1) % n][0] * poly[k][1] for k in range(n))
    return 1.0 if area2 > 0 else -1.0


# -- round things -----------------------------------------------------------------------------
def ring_points(center, axis, radius, sides, phase=0.0):
    x, y, z = basis_along(axis)
    c = vec(center)
    return [tuple(c + (y * math.cos(phase + 2 * math.pi * k / sides) + z * math.sin(phase + 2 * math.pi * k / sides)) * radius)
            for k in range(sides)]


def cylinder(mb, p0, p1, r, sides, style, caps=(True, True), r1=None, phase=None, smooth=None):
    """Cylinder (or frustum when r1 is given) from p0 to p1; sides smooth (from six sides, unless smooth says
    otherwise, as for a hex nut), caps flat."""
    p0, p1 = vec(p0), vec(p1)
    axis = p1 - p0
    r1 = r if r1 is None else r1
    ph = math.pi / sides if phase is None else phase
    a = mb.verts_lift(ring_points(p0, axis, r, sides, ph))
    b = mb.verts_lift(ring_points(p1, axis, r1, sides, ph))
    side = style.but(smooth=sides >= 6 if smooth is None else smooth)
    x, y, z = basis_along(axis)
    for k in range(sides):
        k1 = (k + 1) % sides
        ang = ph + 2 * math.pi * (k + 0.5) / sides
        mb.face([a[k], a[k1], b[k1], b[k]], side, tuple(y * math.cos(ang) + z * math.sin(ang)))
    flat = style.but(smooth=False)
    if caps[0] and r > 0:
        ca = mb.verts_lift(ring_points(p0, axis, r, sides, ph))
        mb.face(ca, flat, tuple(-axis))
    if caps[1] and r1 > 0:
        cb = mb.verts_lift(ring_points(p1, axis, r1, sides, ph))
        mb.face(cb, flat, tuple(axis))


def tube_path(mb, pts, r, sides, style, caps=True, closed=False):
    """A round tube swept along a polyline (bent handrails, hangers), with parallel-transported rings.
    closed: the path is a loop (the last point joins the first); no caps. Returns the rings: (vertex indices, y, z)
    per point, so another part can join the tube on one of them."""
    pts = [vec(p) for p in pts]
    rings, prev_y, prev_t = [], None, None
    n_pts = len(pts)
    for i, p in enumerate(pts):
        if closed:
            t_in = (p - pts[i - 1]).normalized()
            t_out = (pts[(i + 1) % n_pts] - p).normalized()
        else:
            t_in = (p - pts[i - 1]).normalized() if i > 0 else None
            t_out = (pts[i + 1] - p).normalized() if i < len(pts) - 1 else None
        t = ((t_in or t_out) + (t_out or t_in)).normalized()
        if prev_y is None:
            _, y, _ = basis_along(t)
        else:
            y = prev_y - t * prev_y.dot(t)
            if y.length < 0.5:   # a sharp turn (e.g. a chair frame's two corners): turn the previous ring's frame
                y = prev_t.rotation_difference(t) @ prev_y     # with the tangent; projecting it would collapse
                y = y - t * y.dot(t)
            y = y.normalized()
        z = t.cross(y).normalized()
        prev_y, prev_t = y, t
        scale = 1.0
        if t_in is not None and t_out is not None:   # mitre: widen the ring at the bend
            scale = 1.0 / max(0.5, t_in.dot(t))
        ring = []
        for k in range(sides):
            ang = 2 * math.pi * k / sides
            off = y * math.cos(ang) * r + z * math.sin(ang) * r
            # stretch only the component in the bend plane
            if scale != 1.0:
                bend = (t_out - t_in)
                if bend.length > 1e-6:
                    bn = (bend - t * bend.dot(t)).normalized()
                    off = off + bn * off.dot(bn) * (scale - 1.0)
            ring.append(mb.vert(tuple(p + off)))
        rings.append((ring, y, z))
    side = style.but(smooth=sides >= 5)
    segs = len(rings) if closed else len(rings) - 1
    for i in range(segs):
        (ra, ya, za), (rb, _, _) = rings[i], rings[(i + 1) % len(rings)]
        for k in range(sides):
            k1 = (k + 1) % sides
            ang = 2 * math.pi * (k + 0.5) / sides
            mb.face([ra[k], ra[k1], rb[k1], rb[k]], side, tuple(ya * math.cos(ang) + za * math.sin(ang)))
    if caps and not closed:
        flat = style.but(smooth=False)
        mb.face(list(rings[0][0]), flat, tuple(pts[0] - pts[1]))
        mb.face(list(rings[-1][0]), flat, tuple(pts[-1] - pts[-2]))
    return rings


def fillet(pts, i, radius, steps):
    """A polyline with its corner at pts[i] rounded: an arc of `radius` (m, shrunk if a neighbouring segment is too
    short) in `steps` segments, tangent to both segments. Returns the new list of points."""
    a, p, b = vec(pts[i - 1]), vec(pts[i]), vec(pts[i + 1])
    d1, d2 = (p - a).normalized(), (b - p).normalized()
    theta = math.acos(max(-1.0, min(1.0, d1.dot(d2))))
    if theta < 1e-3:
        return list(pts)
    t = min(radius * math.tan(theta / 2), 0.45 * (p - a).length, 0.45 * (b - p).length)
    r = t / math.tan(theta / 2)
    n = (d2 - d1 * d1.dot(d2)).normalized()     # toward the inside of the bend
    c = p - d1 * t + n * r
    arc = [tuple(c - n * (r * math.cos(f)) + d1 * (r * math.sin(f))) for f in (theta * k / steps for k in range(steps + 1))]
    return list(pts[:i]) + arc + list(pts[i + 1:])


def lathe(mb, center, axis, profile, segments, style, closed=True, smooth=True, phase=0.0, arc=None):
    """Revolves a (radius, height) profile about `axis` through `center`.

    closed: the profile is a closed section (last point joins the first); faces point away from the
    section's centroid. arc: (start, end) radians for a partial revolution (no end caps)."""
    c, ax = vec(center), vec(axis).normalized()
    _, y, z = basis_along(ax)
    full = arc is None
    a0, a1 = (0.0, 2 * math.pi) if full else arc
    steps = segments if full else segments + 1
    angles = [phase + a0 + (a1 - a0) * k / segments for k in range(steps)]
    rings = []
    for ang in angles:
        radial = y * math.cos(ang) + z * math.sin(ang)
        rings.append([mb.vert(tuple(c + radial * r + ax * h)) for r, h in profile])
    n = len(profile)
    cr = sum(r for r, _ in profile) / n
    ch = sum(h for _, h in profile) / n
    wind = winding(profile) if closed else 0.0
    segs = n if closed else n - 1
    st = style.but(smooth=smooth)
    for j in range(segs):
        j1 = (j + 1) % n
        (r0, h0), (r1, h1) = profile[j], profile[j1]
        nr, nh = (h1 - h0), -(r1 - r0)
        if closed:   # outward from the section's winding (right of each edge when counter-clockwise)
            nr, nh = nr * wind, nh * wind
        elif nr * ((r0 + r1) / 2 - cr) + nh * ((h0 + h1) / 2 - ch) < 0:
            nr, nh = -nr, -nh
        flat_seg = st if abs(nh) < 0.8 * math.hypot(nr, nh) else st.but(smooth=False)   # discs / annuli stay flat
        for k in range(len(rings) if full else len(rings) - 1):
            k1 = (k + 1) % len(rings)
            ang = (angles[k] + (angles[k1] if k1 else angles[k] + 2 * math.pi / segments)) / 2
            radial = y * math.cos(ang) + z * math.sin(ang)
            out = radial * nr + ax * nh
            q = [rings[k][j], rings[k1][j], rings[k1][j1], rings[k][j1]]
            if q[0] == q[3] or q[1] == q[2]:
                continue
            mb.face(q, flat_seg, tuple(out))
    return rings


def prism(mb, poly, origin, ea, eb, en, depth, style, caps=(True, True), side_style=None):
    """A 2D polygon (in the plane of axes ea, eb at origin) extruded by depth along en."""
    o, ea, eb, en = vec(origin), vec(ea), vec(eb), vec(en)
    front = [tuple(o + ea * a + eb * bb) for a, bb in poly]
    back = [tuple(o + ea * a + eb * bb + en * depth) for a, bb in poly]
    fi = mb.verts_lift(front)
    bi = mb.verts_lift(back)
    n = len(poly)
    wind = winding(poly)
    ss = side_style or style
    for k in range(n):
        k1 = (k + 1) % n
        (a0, b0), (a1, b1) = poly[k], poly[k1]
        na, nb = (b1 - b0) * wind, -(a1 - a0) * wind   # outward for any simple polygon, concave included
        mb.face([fi[k], fi[k1], bi[k1], bi[k]], ss, tuple(ea * na + eb * nb))
    if caps[0]:
        mb.face(mb.verts_lift(front), style.but(smooth=False), tuple(-en))
    if caps[1]:
        mb.face(mb.verts_lift(back), style.but(smooth=False), tuple(en))


def plate(mb, poly, origin, ea, eb, en, depth, style):
    """A thin plate: the polygon's two faces only (front at origin, back at origin + en * depth), no edges."""
    o, ea, eb, en = vec(origin), vec(ea), vec(eb), vec(en)
    front = [tuple(o + ea * a + eb * b) for a, b in poly]
    back = [tuple(o + ea * a + eb * b + en * depth) for a, b in poly]
    mb.face(mb.verts_lift(front), style.but(smooth=False), tuple(-en))
    mb.face(mb.verts_lift(back), style.but(smooth=False), tuple(en))


def quad(mb, pts, style, outward):
    return mb.face(mb.verts_lift([tuple(vec(p)) for p in pts]), style, tuple(vec(outward)))
