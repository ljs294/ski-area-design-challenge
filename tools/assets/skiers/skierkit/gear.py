"""Skis, bindings, poles and the snowboard, built in the rest pose on their own bones.

Sizes are fitted to the 1.75 m figure: skis reach the nose (1.70 m), poles put the forearm level when planted
(1.20 m), the board reaches the collar bone (1.56 m). lod 0 is the close-up model, lod 5 a speck.
"""
import math

from mathutils import Matrix, Vector

from . import skeleton as sk
from .mesh import Mesh, ring

SKI_LEN, BOARD_LEN, BOARD_W = 1.70, 1.56, 0.295
BIND_ANGLE = {"_l": 15.0, "_r": -9.0}     # degrees; the left foot leads (regular stance)
X, Y = Vector((1, 0, 0)), Vector((0, 1, 0))


def _plank(m, stations, w, top, side, along_x=False, x0=0.0):
    """A ski or a board as a row of rectangles. stations: (station, half width, height of the base, thickness).
    The top takes one material, the sidewalls and the base another."""
    rows = []
    for st, hw, zb, th in stations:
        if along_x:   # the loft runs toward +X: u = +Y, v = +Z
            pts = [(st, hw, zb), (st, hw, zb + th), (st, -hw, zb + th), (st, -hw, zb)]
        else:         # the loft runs toward -Y: u = +X, v = +Z
            pts = [(x0 + hw, st, zb), (x0 + hw, st, zb + th), (x0 - hw, st, zb + th), (x0 - hw, st, zb)]
        rows.append(m.add(pts, w))
    for a, b in zip(rows, rows[1:]):
        for j, mat in enumerate((side, top, side, side)):
            k = (j + 1) % 4
            m.face((a[j], a[k], b[k], b[j]), mat)
    m.face(rows[0][::-1], side)
    m.face(rows[-1], side)


def _pick(rows, lod):
    """Rows whose first number is the last LOD that keeps them."""
    return [r[1:] for r in rows if r[0] >= lod]


def ski(lod):
    """The left ski with its binding; mirror it for the right. The boot centre is 100 mm behind the ski's middle."""
    m = Mesh()
    w = {"ski_l": 1}
    x = sk.HIP_X
    if lod >= 4:
        hw = 0.050 if lod == 4 else 0.060
        i = m.add([(x - hw, 0.75, 0.012), (x + hw, 0.75, 0.012), (x + hw, -0.95, 0.012), (x - hw, -0.95, 0.012)], w)
        m.face(i, "deck")
        return m
    stations = _pick([
        (3, 0.750, 0.036, 0.022, 0.006), (1, 0.735, 0.050, 0.010, 0.007), (2, 0.690, 0.056, 0.000, 0.009),
        (0, 0.350, 0.046, 0.000, 0.014), (3, 0.000, 0.040, 0.000, 0.018), (0, -0.400, 0.047, 0.000, 0.013),
        (2, -0.740, 0.060, 0.000, 0.008), (1, -0.850, 0.058, 0.014, 0.006), (0, -0.920, 0.044, 0.045, 0.005),
        (3, -0.950, 0.022, 0.066, 0.005)], lod)
    _plank(m, stations, w, "deck", "base", x0=x)
    t = 0.018   # the top of the ski under the boot
    if lod == 0:
        m.box((x - 0.036, -0.200, t), (x + 0.036, 0.140, t + 0.016), w, "strap")            # the plate under the boot
        m.box((x - 0.034, -0.280, t), (x + 0.034, -0.200, t + 0.030), w, "strap")           # the toe piece: its base
        m.box((x - 0.042, -0.262, t + 0.030), (x + 0.042, -0.204, t + 0.066), w, "accent")  # and its wings
        m.box((x - 0.036, 0.140, t), (x + 0.036, 0.228, t + 0.072), w, "strap")             # the heel piece
        lever = Matrix.Translation((x, 0.214, t + 0.066)) @ Matrix.Rotation(math.radians(32), 4, "X")
        m.box((-0.022, 0.0, -0.010), (0.022, 0.085, 0.012), w, "accent", lever)             # its lever
        for s in (-1, 1):                                                                   # the brake arms, raised
            m.box((x + s * 0.050 - 0.005, 0.030, t + 0.004), (x + s * 0.050 + 0.005, 0.190, t + 0.014), w, "metal")
    elif lod == 1:
        m.box((x - 0.036, -0.200, t), (x + 0.036, 0.140, t + 0.016), w, "strap")
        m.box((x - 0.038, -0.275, t), (x + 0.038, -0.200, t + 0.060), w, "accent")
        m.box((x - 0.036, 0.140, t), (x + 0.036, 0.235, t + 0.072), w, "strap")
    elif lod == 2:
        m.box((x - 0.036, -0.275, t), (x + 0.036, 0.235, t + 0.045), w, "strap")
    return m


def pole(lod):
    """The left pole in the rest pose, hanging straight down through the closed fist; mirror it for the right."""
    m = Mesh()
    if lod >= 4:
        return m
    w = {"pole_l": 1}
    c = sk.fist(1)
    top = c.z + 0.06
    tip = top - sk.POLE

    def at(z):
        return (c.x, c.y, z)
    if lod == 0:
        m.loft([(ring(at(tip), X, Y, 0.0025, 0.0025, 6), w), (ring(at(tip + 0.045), X, Y, 0.0050, 0.0050, 6), w),
                (ring(at(top - 0.130), X, Y, 0.0090, 0.0090, 6), w)], "metal", cap0="metal")
        grip = [(top - 0.135, 0.0120), (top - 0.118, 0.0150), (top - 0.060, 0.0158), (top - 0.022, 0.0150),
                (top - 0.010, 0.0200), (top, 0.0165)]
        m.loft([(ring(at(z), X, Y, r, r, 8), w) for z, r in grip], "glove", cap0="glove", cap1="glove")
        z = tip + 0.060                                             # the basket: a dished disc on a short cone
        m.loft([(ring(at(z - 0.006), X, Y, 0.0060, 0.0060, 8), w), (ring(at(z), X, Y, 0.0450, 0.0450, 8), w),
                (ring(at(z + 0.004), X, Y, 0.0450, 0.0450, 8), w), (ring(at(z + 0.030), X, Y, 0.0075, 0.0075, 8), w)],
               "accent")
    elif lod == 1:
        m.loft([(ring(at(tip), X, Y, 0.0035, 0.0035, 4), w), (ring(at(top - 0.130), X, Y, 0.0090, 0.0090, 4), w)],
               "metal", cap0="metal")
        m.loft([(ring(at(top - 0.130), X, Y, 0.0150, 0.0150, 5), w), (ring(at(top), X, Y, 0.0175, 0.0175, 5), w)],
               "glove", cap0="glove", cap1="glove")
        z = tip + 0.060
        m.loft([(ring(at(z), X, Y, 0.0450, 0.0450, 6), w), (ring(at(z + 0.025), X, Y, 0.0075, 0.0075, 6), w)],
               "accent", cap0="accent")
    else:   # a far pole is drawn thicker than life, or it breaks into dots
        r = 0.015 if lod == 2 else 0.025
        m.loft([(ring(at(tip), X, Y, r * 0.6, r * 0.6, 3), w), (ring(at(top), X, Y, r, r, 3), w)], "metal")
    return m


def _binding(m, w, f, lod):
    """One snowboard binding in its own frame f: the toes toward -Y, the heel toward +Y."""
    if lod >= 3:
        return
    m.box((-0.064, -0.150, 0.010), (0.064, 0.135, 0.030), w, "strap", f)                      # the baseplate
    if lod == 2:
        back = f @ Matrix.Translation((0, 0.135, 0.030)) @ Matrix.Rotation(math.radians(-10), 4, "X")
        m.box((-0.052, -0.004, 0.0), (0.052, 0.010, 0.220), w, "strap", back)
        return
    k = 7 if lod == 0 else 4                                                                 # the highback, curved
    part = Mesh()
    rows = [(0.050, 0.100, 0.066, 0.074), (0.170, 0.092, 0.062, 0.066), (0.285, 0.080, 0.052, 0.056)]
    if lod == 1:
        rows = [rows[0], rows[2]]
    a0, a1 = math.radians(25), math.radians(155)
    part.loft([(ring((0, cy, z), X, Y, rx, ry, k, a0, a1), w) for z, cy, rx, ry in rows], "strap", closed=False,
              inside="liner")
    m.merge(part, f)
    arches = [("liner", [(0.068, 0.040, 0.045), (0.060, 0.010, 0.150), (0.0, -0.015, 0.218), (-0.060, 0.010, 0.150),
                         (-0.068, 0.040, 0.045)], 0.026),                                   # the ankle strap
              ("liner", [(0.070, -0.110, 0.040), (0.058, -0.135, 0.110), (0.0, -0.145, 0.142), (-0.058, -0.135, 0.110),
                         (-0.070, -0.110, 0.040)], 0.020)]                                  # the toe strap
    for mat, path, wide in arches:
        if lod == 1:
            path = [path[0], path[2], path[4]]
        part = Mesh()
        part.tube(path, (0.006, wide), 4, w, mat, hint=(0, -1, 0), cap0=mat, cap1=mat)
        m.merge(part, f)
    if lod == 0:
        m.box((0.066, 0.000, 0.085), (0.082, 0.040, 0.125), w, "accent", f)                   # the ratchets
        m.box((0.066, -0.135, 0.070), (0.080, -0.100, 0.100), w, "accent", f)


def board(lod):
    """The snowboard lying along X (the nose toward the figure's left), with both bindings."""
    m = Mesh()
    w = {"board": 1}
    h = BOARD_LEN / 2
    if lod >= 4:
        i = m.add([(-h, -0.14, 0.012), (-h, 0.14, 0.012), (h, 0.14, 0.012), (h, -0.14, 0.012)], w)
        m.face(i, "deck")
        return m
    half = _pick([(3, h, 0.046, 0.052), (1, h - 0.022, 0.098, 0.040), (2, h - 0.062, 0.134, 0.022),
                  (0, h - 0.130, 0.1475, 0.006), (2, h - 0.210, 0.146, 0.0), (0, 0.300, 0.133, 0.0)], lod)
    stations = [(-x, hw, zb, 0.010) for x, hw, zb in half] + [(0.0, 0.126, 0.0, 0.010)] \
        + [(x, hw, zb, 0.010) for x, hw, zb in reversed(half)]
    _plank(m, stations, w, "deck", "base", along_x=True)
    for sx, s in (("_l", 1), ("_r", -1)):
        f = Matrix.Translation((s * sk.STANCE / 2, 0, 0)) @ Matrix.Rotation(math.radians(BIND_ANGLE[sx]), 4, "Z")
        _binding(m, w, f, lod)
    return m


def skier_gear(lod):
    m = Mesh()
    for part in (ski(lod), pole(lod)):
        m.merge(part)
        m.merge(part.mirrored())
    return m
