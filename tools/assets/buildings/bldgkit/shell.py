"""A building's shell: walls with openings, gable infills and a gable roof, in the building (lift) frame, metres.

A building is a closed shell with no interior: every opening has reveal faces into the wall and a fixture (door,
window, louvre) filling its back, so nothing looks through into the empty inside, where the game culls back faces.
"""
from mathutils import Vector

W = Vector((0.0, 0.0, 1.0))


class WallFrame:
    """A wall's own frame: `origin` is the outer face's start at w = 0, `ea` runs along it, `en` points out of it."""

    def __init__(self, origin, ea, en):
        self.o, self.ea, self.en = Vector(origin), Vector(ea).normalized(), Vector(en).normalized()

    def p(self, a, w, n=0.0):
        """A point `a` along the wall, at height `w`, `n` out of the outer face (negative: into the wall)."""
        return tuple(self.o + self.ea * a + W * w + self.en * n)


def _quad(mb, pts, style, outward):
    return mb.face(mb.verts_lift(pts), style, tuple(outward))


def wall(mb, wf, length, bands, openings, reveal_style, depth=0.12):
    """The wall's outer face from a = 0 to `length`, split into horizontal `bands` [(w0, w1, Style)], with rectangular
    `openings` [(a0, a1, w0, w1)] cut out, and each opening's four reveal faces `depth` deep (into the wall). Returns
    the number of faces added. The face is decomposed into strips between the openings' edges, then into the
    bands' and openings' heights, so each quad has one style and no T-junction crosses an opening."""
    n0 = len(mb.faces)
    cuts = sorted({0.0, length} | {x for o in openings for x in (o[0], o[1]) if 0.0 < x < length})
    w_marks = sorted({b[0] for b in bands} | {b[1] for b in bands} | {x for o in openings for x in (o[2], o[3])})
    lo, hi = bands[0][0], bands[-1][1]
    w_marks = [w for w in w_marks if lo <= w <= hi]
    for a0, a1 in zip(cuts, cuts[1:]):
        mid = (a0 + a1) / 2
        holes = [o for o in openings if o[0] < mid < o[1]]
        for w0, w1 in zip(w_marks, w_marks[1:]):
            wm = (w0 + w1) / 2
            if any(o[2] < wm < o[3] for o in holes):
                continue
            style = next(b[2] for b in bands if b[0] <= wm <= b[1])
            _quad(mb, [wf.p(a0, w0), wf.p(a1, w0), wf.p(a1, w1), wf.p(a0, w1)], style, wf.en)
    for a0, a1, w0, w1 in openings:
        d = -depth
        _quad(mb, [wf.p(a0, w0), wf.p(a0, w1), wf.p(a0, w1, d), wf.p(a0, w0, d)], reveal_style, wf.ea)      # jamb
        _quad(mb, [wf.p(a1, w0), wf.p(a1, w1), wf.p(a1, w1, d), wf.p(a1, w0, d)], reveal_style, -wf.ea)     # jamb
        _quad(mb, [wf.p(a0, w1), wf.p(a1, w1), wf.p(a1, w1, d), wf.p(a0, w1, d)], reveal_style, -W)         # head
        _quad(mb, [wf.p(a0, w0), wf.p(a1, w0), wf.p(a1, w0, d), wf.p(a0, w0, d)], reveal_style.but(snow=0.3), W)   # sill
    return len(mb.faces) - n0


def gable_infill(mb, wf, length, eave, ridge, style):
    """The triangle of a gable end wall above the eave line, apex at the middle."""
    _quad(mb, [wf.p(0.0, eave), wf.p(length, eave), wf.p(length / 2, ridge)], style, wf.en)


def gable_roof(mb, hx, hy, eave, pitch, thick, overhang, rake, style, fascia_style):
    """A gable roof along u over a footprint of half sizes (hx, hy). Its underside passes through the outer wall's top
    edge (w = eave at v = +-hy) and rises `pitch` (rise over run) to the ridge at v = 0; `thick` vertical; eaves
    `overhang` past the walls, rakes `rake` past the gable walls. Returns the ridge's top height."""
    ridge_under = eave + hy * pitch
    ue = hx + rake
    for s in (-1, 1):
        ve = s * (hy + overhang)
        w_e = eave - overhang * pitch
        top = [(-ue, 0.0, ridge_under + thick), (ue, 0.0, ridge_under + thick), (ue, ve, w_e + thick), (-ue, ve, w_e + thick)]
        bot = [(-ue, 0.0, ridge_under), (ue, 0.0, ridge_under), (ue, ve, w_e), (-ue, ve, w_e)]
        up = Vector((0.0, s * pitch, 1.0)).normalized()
        _quad(mb, top, style, up)
        _quad(mb, bot, fascia_style.but(snow=0.0), -up)
        _quad(mb, [bot[3], bot[2], top[2], top[3]], fascia_style, (0.0, s, 0.0))     # eave fascia
        for e, (i, j) in ((-1, (0, 3)), (1, (1, 2))):                                   # rake edges
            _quad(mb, [bot[i], bot[j], top[j], top[i]], fascia_style, (e, 0.0, 0.0))
    return ridge_under + thick


def shed_infill(mb, wf, length, low, high, high_end, style):
    """A single-slope building's end wall above the low eave: a right triangle rising to `high` at the `high_end`
    (0: a = 0, 1: a = length)."""
    _quad(mb, [wf.p(0.0, low), wf.p(length, low), wf.p(length * high_end, high)], style, wf.en)


def shed_roof(mb, hx, hy, hs, eave, pitch, thick, overhang, rake, style, fascia_style):
    """A single-slope (shed) roof over a footprint of half sizes (hx, hy): its underside passes through the high wall's
    top (w = eave at v = hs * hy, hs = +-1) and falls `pitch` toward the other wall; `thick` vertical; eaves `overhang`
    past both long walls, rakes `rake` past the end walls. Returns the high edge's top height."""
    ue = hx + rake
    vh, vl = hs * (hy + overhang), -hs * (hy + overhang)
    wh, wl = eave + overhang * pitch, eave - (2 * hy + overhang) * pitch
    top = [(-ue, vh, wh + thick), (ue, vh, wh + thick), (ue, vl, wl + thick), (-ue, vl, wl + thick)]
    bot = [(-ue, vh, wh), (ue, vh, wh), (ue, vl, wl), (-ue, vl, wl)]
    up = Vector((0.0, -hs * pitch, 1.0)).normalized()
    _quad(mb, top, style, up)
    _quad(mb, bot, fascia_style.but(snow=0.0), -up)
    _quad(mb, [bot[0], bot[1], top[1], top[0]], fascia_style, (0.0, hs, 0.0))      # the high eave's fascia
    _quad(mb, [bot[3], bot[2], top[2], top[3]], fascia_style, (0.0, -hs, 0.0))     # the low eave's
    for e, (i, j) in ((-1, (0, 3)), (1, (1, 2))):                                   # rake edges
        _quad(mb, [bot[i], bot[j], top[j], top[i]], fascia_style, (e, 0.0, 0.0))
    return wh + thick


def roof_w(v, hy, eave, pitch, thick):
    """The roof's top surface height over a point v across the building (inside the eaves)."""
    return eave + (hy - abs(v)) * pitch + thick
