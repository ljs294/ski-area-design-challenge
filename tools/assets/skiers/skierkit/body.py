"""The figure: one jacket style, trousers, gloves, boots, head, helmet and goggles, built in the rest pose.

The look follows the owner's concept art ("low-poly + technical alpine shells"): a boxy shell jacket to the hips
with a dark yoke over the shoulders, a low collar over a neck gaiter and a rolled hood, straight wide trousers with
knee patches, chunky boots and gloves, a wide low helmet with a short brim, ear pads and a big mirrored lens.

lod 0 is the close-up model and lod 5 a speck; the first number of a ring row is the last LOD that keeps it.
Cloth rings are turned half a step from row to row and cut into irregular facets on the three nearest LODs, so the
shell reads as hand-cut planes, not hoops. Every shell of the head sits on the same angular stations.
"""
import math

from mathutils import Matrix, Vector

from . import skeleton as sk
from .mesh import Mesh, half_step, ring

X, Y, Z = Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))
LODS = 6
SIDES = {"torso": (16, 12, 8, 6, 4, 4), "limb": (10, 8, 6, 4, 3, 3), "head": (16, 12, 8, 6, 4, 4)}
CLOTH = ("jacket", "block", "pants")
FOLD = (0.0050, 0.0045, 0.0040)     # how deep the cloth facets are, by LOD; farther LODs stay smooth
POWER = 2.8                         # the boxy section of the shell


def vr(z, rx, ry, n, cx=0.0, cy=0.0, a0=0.0, a1=None, power=2.0):
    """A level ring; angle 270 degrees is the front, 0 the figure's left."""
    return ring((cx, cy, z), X, Y, rx, ry, n, a0, a1, power=power)


def _keep(rows, lod):
    return [r[1:] for r in rows if r[0] >= lod]


def _turns(count, lod):
    """Which rings of a cloth loft are turned half a step (every other one, on the near LODs)."""
    return [lod <= 2 and i % 2 == 1 for i in range(count)]


def _toward(m, pts, w, mat, way):
    """A flat patch facing `way`, whichever order its corners come in."""
    i = m.add(pts, w)
    a, b, c = (Vector(p) for p in pts[:3])
    m.face(i if (b - a).cross(c - a).dot(Vector(way)) > 0 else i[::-1], mat)


# ---------------------------------------------------------------------------------------------------------------
P, S3 = {"pelvis": 1}, {"spine_03": 1}
TORSO = [   # (keep, z, rx, ry, cy, weights, tag)
    (4, 0.870, 0.215, 0.168, 0.006, P, "hem"),
    (1, 0.905, 0.218, 0.171, 0.006, P, "hem2"),
    (3, 0.990, 0.200, 0.152, 0.004, {"pelvis": 0.5, "spine_01": 0.5}, None),
    (1, 1.090, 0.198, 0.148, 0.000, {"spine_01": 0.5, "spine_02": 0.5}, None),
    (2, 1.190, 0.208, 0.154, 0.000, {"spine_02": 0.6, "spine_03": 0.4}, None),
    (4, 1.335, 0.216, 0.154, 0.002, {"spine_03": 0.8, "spine_02": 0.2}, "yoke"),
    (1, 1.390, 0.214, 0.148, 0.004, S3, None),
    (4, 1.432, 0.196, 0.136, 0.008, S3, None),
    (2, 1.468, 0.150, 0.124, 0.012, S3, None),
    (4, 1.495, 0.120, 0.118, 0.014, S3, None),
]


def _torso_ring(z, rx, ry, cy, n, w, tag, turned):
    """Points and weights of one jacket ring."""
    pts = vr(z, rx, ry, n, cy=cy, a0=half_step(n) if turned else 0.0, power=POWER)
    if tag == "yoke":      # the yoke's lower edge: a chevron dipping to the zip in front, a shallow curve behind
        pts = [Vector((p.x, p.y, z - (0.36 if p.y < cy else 0.10) * max(0.0, rx - abs(p.x)))) for p in pts]
    elif tag == "hem":     # a level hem with a drop tail
        pts = [Vector((p.x, p.y, z - 0.030 * max(0.0, (p.y - cy) / ry))) for p in pts]
    if tag in ("hem", "hem2"):   # the front of the hem rides on the thighs, so a seated rider's lap stays covered
        share = 0.5 if tag == "hem" else 0.3
        ws = []
        for p in pts:
            if p.y > cy - 0.02:
                ws.append(P)
            elif abs(p.x) < 0.03:
                ws.append({"pelvis": 1 - share, "thigh_l": share / 2, "thigh_r": share / 2})
            else:
                ws.append({"pelvis": 1 - share, "thigh_l" if p.x > 0 else "thigh_r": share})
        return pts, ws
    return pts, w


def _front_y(z, x):
    """The jacket's front surface (y) at height z and offset x, from the ring table."""
    rows = [(r[1], r[2], r[3], r[4]) for r in TORSO]
    for (z0, rx0, ry0, cy0), (z1, rx1, ry1, cy1) in zip(rows, rows[1:]):
        if z0 <= z <= z1:
            t = (z - z0) / (z1 - z0)
            rx, ry, cy = rx0 + (rx1 - rx0) * t, ry0 + (ry1 - ry0) * t, cy0 + (cy1 - cy0) * t
            return cy - ry * max(0.0, 1.0 - abs(x / rx) ** POWER) ** (1.0 / POWER)
    return -0.15


def torso(lod):
    m = Mesh()
    n = SIDES["torso"][lod]
    rows = _keep(TORSO, lod)
    turns = _turns(len(rows), lod)
    yoke = next(i for i, r in enumerate(rows) if r[5] == "yoke")
    rings = [_torso_ring(z, rx, ry, cy, n, w, tag, turns[i]) for i, (z, rx, ry, cy, w, tag) in enumerate(rows)]
    mats = ["jacket" if i < yoke else "block" for i in range(len(rows) - 1)]
    if lod >= 3:     # far off there is no collar: the jacket closes into a dark neck that meets the chin
        rings.append((vr(1.608, 0.070, 0.080, n, cy=-0.004), {"neck": 0.5, "head": 0.5}))
        mats.append("liner")
        turns = turns + [False]
    m.loft(rings, mats, cap1="block" if lod <= 2 else None, stagger=turns)
    top = {"spine_03": 0.6, "neck": 0.4}

    def tilt(pts):   # the collar's top edge: lower at the chin than at the nape
        return [p + Z * (0.16 * (p.y - 0.03)) for p in pts]
    if lod <= 2:     # the collar: low, one colour, with thickness
        m.loft([(vr(1.490, 0.124, 0.122, n, cy=0.014), S3), (tilt(vr(1.580, 0.120, 0.132, n, cy=0.022)), top),
                (tilt(vr(1.580, 0.110, 0.122, n, cy=0.022)), top), (vr(1.500, 0.100, 0.100, n, cy=0.016), S3)], "jacket")
    if lod <= 2:
        m.facet(11, FOLD[lod], CLOTH, still=lambda p: p.z < 0.885)      # the hem stays a clean line
    if lod <= 3:
        m.fan(m.add(vr(0.935, 0.186, 0.146, n, cy=0.006, power=POWER), P)[::-1], "pants")   # the seat, inside the hem
    if lod <= 2:
        k = (8, 6, 5)[lod]   # a dark neck gaiter up to the chin, inside the collar
        m.loft([(vr(1.470, 0.078, 0.080, k, cy=0.012), {"neck": 1}),
                ([p + Z * (0.10 * p.y) for p in vr(1.618, 0.074, 0.084, k, cy=-0.002)], {"neck": 0.5, "head": 0.5})],
               "block")
        k = (7, 5, 4)[lod]   # the hood, rolled into a crescent behind the neck
        path, radii = [], []
        for i in range(k):
            a = math.radians(-28 + 236 * i / (k - 1))
            s = math.sin(math.pi * i / (k - 1))
            path.append((0.140 * math.cos(a), 0.030 + 0.150 * math.sin(a), 1.490 + 0.030 * s))
            radii.append((0.012 + 0.022 * s, 0.016 + 0.034 * s))
        m.tube(path, radii, (6, 5, 4)[lod], S3, "jacket", cap0="jacket", cap1="jacket")
        # the zip: one raised strip from the hem to the top of the collar
        line = [(z - (0.36 * rx if tag == "yoke" else 0.0), cy - ry, P if tag in ("hem", "hem2") else w)
                for z, rx, ry, cy, w, tag in rows]
        line += [(1.558, 0.022 - 0.132, top)]
        m.loft([([(0.009, y - 0.009, z), (0.009, y + 0.004, z), (-0.009, y + 0.004, z), (-0.009, y - 0.009, z)], w)
                for z, y, w in line], "strap", cap1="strap")
    if lod <= 1:   # pocket zips: one on the chest, two hand pockets
        for x0, z0, x1, z1, w in ((0.090, 1.120, 0.090, 1.235, S3), (0.100, 1.045, 0.160, 0.940, P),
                                  (-0.100, 1.045, -0.160, 0.940, P)):
            h = 0.006
            _toward(m, [(x0 - h, _front_y(z0, x0) - 0.008, z0), (x0 + h, _front_y(z0, x0) - 0.008, z0),
                        (x1 + h, _front_y(z1, x1) - 0.008, z1), (x1 - h, _front_y(z1, x1) - 0.008, z1)], w, "strap",
                    (0, -1, 0))
    if lod == 0:   # the zip pull
        y = 0.022 - 0.132
        m.box((-0.007, y - 0.020, 1.505), (0.007, y - 0.009, 1.545), top, "metal")
    return m


# ---------------------------------------------------------------------------------------------------------------
def _sleeve_ring(sh, d, t, off, ru, rv, n, lean, a0):
    """One sleeve ring. lean 0 lays the ring in the armhole (flat against the body), 1 squares it to the arm; `off`
    drops it toward the armpit, so the sleeve leaves the shoulder without a step."""
    fwd = Vector((0, -1, 0))
    up_out = fwd.cross(d)
    nrm = (X * (1 - lean) + d * lean).normalized()
    return ring(sh + d * t - up_out * off, fwd.cross(nrm), fwd, ru, rv, n, a0)


def arm(lod, kind="skier"):
    """The left sleeve and glove; mirror it for the right."""
    m = Mesh()
    n = SIDES["limb"][lod]
    sh, el, wr, tip = sk._arm(1)
    d = (el - sh).normalized()
    v = Vector((0, -1, 0))
    u = v.cross(d)                                 # up and out; u x v = d
    cl, up, lo, hand = "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l"
    rows = _keep([   # (keep, along the arm, drop to the armpit, radius up-out, radius fore-aft, weights, lean, tag)
        (4, -0.045, 0.040, 0.060, 0.082, {"spine_03": 0.5, cl: 0.25, up: 0.25}, 0.0, None),
        (2, 0.040, 0.024, 0.074, 0.086, {cl: 0.3, up: 0.7}, 0.5, None),
        (4, 0.125, 0.008, 0.080, 0.084, {up: 1}, 1.0, "yoke"),
        (1, 0.200, 0.000, 0.078, 0.080, {up: 1}, 1.0, None),
        (1, 0.240, 0.000, 0.076, 0.078, {up: 0.75, lo: 0.25}, 1.0, None),
        (4, 0.270, 0.000, 0.075, 0.077, {up: 0.5, lo: 0.5}, 1.0, None),
        (1, 0.300, 0.000, 0.074, 0.076, {up: 0.25, lo: 0.75}, 1.0, None),
        (1, 0.345, 0.000, 0.072, 0.074, {lo: 1}, 1.0, None),
        (2, 0.420, 0.000, 0.068, 0.069, {lo: 1}, 1.0, None),
        (4, 0.500, 0.000, 0.066, 0.066, {lo: 1}, 1.0, None),
        (0, 0.512, 0.000, 0.054, 0.054, {lo: 1}, 1.0, None),
    ], lod)
    turns = _turns(len(rows), lod)
    yoke = next(i for i, r in enumerate(rows) if r[6] == "yoke")
    m.loft([(_sleeve_ring(sh, d, t, off, ru, rv, n, lean, half_step(n) if turns[i] else 0.0), w)
            for i, (t, off, ru, rv, w, lean, tag) in enumerate(rows)],
           ["block" if i < yoke else "jacket" for i in range(len(rows) - 1)], cap0="block" if lod <= 2 else None,
           cap1="liner" if lod <= 3 else "glove", stagger=turns)
    if lod <= 2:
        m.facet(23, FOLD[lod], CLOTH)
    frame = Matrix(((u.x, v.x, d.x), (u.y, v.y, d.y), (u.z, v.z, d.z))).to_4x4()
    if lod == 0:   # the cuff tab
        m.box((-0.003, -0.022, -0.012), (0.004, 0.022, 0.012), {lo: 1}, "strap", Matrix.Translation(sh + d * 0.474 + u * 0.068) @ frame)
    if lod >= 4:
        return m
    g = Mesh()
    k = max(4, n - 2)
    if kind == "skier":     # a fist closed on the pole's grip (the grip stands upright in the rest pose)
        g.loft([(ring(sh + d * 0.478, u, v, 0.058, 0.058, k), {lo: 0.5, hand: 0.5}),
                (ring(sh + d * 0.552, u, v, 0.046, 0.048, k), {hand: 1})], "glove", cap0="glove", cap1="glove")
        c = sk.fist(1)
        fist = _keep([(3, -0.058, 0.030, 0.034), (0, -0.046, 0.042, 0.046), (3, 0.000, 0.045, 0.049),
                      (0, 0.042, 0.043, 0.047), (3, 0.056, 0.032, 0.036)], lod)
        g.loft([(vr(c.z + dz, rx, ry, k, cx=c.x + 0.004, cy=c.y, power=2.6), {hand: 1}) for dz, rx, ry in fist],
               "glove", cap0="glove", cap1="glove")
        if lod <= 1:        # the thumb, wrapped over the top of the fingers
            g.box((c.x - 0.030, c.y - 0.062, c.z + 0.008), (c.x + 0.030, c.y - 0.036, c.z + 0.050), {hand: 1}, "glove")
    else:                   # an open mitt, a little curled
        mitt = _keep([(3, 0.478, 0.058, 0.058, {lo: 0.5, hand: 0.5}), (1, 0.552, 0.045, 0.062, {hand: 1}),
                      (0, 0.640, 0.043, 0.064, {hand: 1}), (3, 0.700, 0.036, 0.058, {hand: 1}),
                      (1, 0.738, 0.022, 0.040, {hand: 1})], lod)
        g.loft([(ring(sh + d * t - u * (0.25 * max(0.0, t - 0.60)), u, v, ru, rv, k), w) for t, ru, rv, w in mitt],
               "glove", cap0="glove", cap1="glove")
        if lod <= 1:
            g.box((-0.022, -0.022, -0.045), (0.022, 0.022, 0.045), {hand: 1}, "glove",
                  Matrix.Translation(sh + d * 0.612 + v * 0.070) @ frame)
    m.merge(g)
    return m


# ---------------------------------------------------------------------------------------------------------------
def leg(lod, kind="skier"):
    """The left trouser leg and boot; mirror it for the right."""
    m = Mesh()
    n = SIDES["limb"][lod]
    x = sk.HIP_X
    cx = x + 0.006
    th, ca, ft = "thigh_l", "calf_l", "foot_l"
    rows = _keep([
        (4, 0.965, 0.088, 0.104, 0.010, {"pelvis": 0.6, th: 0.4}),
        (2, 0.850, 0.094, 0.110, 0.006, {th: 1}),
        (1, 0.700, 0.102, 0.110, 0.000, {th: 1}),
        (3, 0.565, 0.098, 0.105, -0.006, {th: 0.75, ca: 0.25}),
        (4, 0.510, 0.098, 0.108, -0.012, {th: 0.5, ca: 0.5}),
        (3, 0.455, 0.096, 0.101, -0.005, {th: 0.25, ca: 0.75}),
        (1, 0.300, 0.096, 0.100, 0.004, {ca: 1}),
        (4, 0.150, 0.106, 0.114, 0.000, {ca: 1}),
        (0, 0.134, 0.090, 0.098, 0.000, {ca: 1}),
    ], lod)[::-1]
    turns = _turns(len(rows), lod)

    def cloth(c):   # the knee patch: the front facets round the knee, pointed above and below
        knee = c.y < -0.048 and abs(c.z - 0.510) < 0.105 - 0.9 * abs(c.x - cx)
        return "patch" if knee and lod <= 3 else "pants"
    m.loft([(vr(z, rx, ry, n, cx=cx, cy=cy, a0=half_step(n) if turns[i] else 0.0, power=2.5), w)
            for i, (z, rx, ry, cy, w) in enumerate(rows)], cloth, cap0="liner", cap1="pants" if lod <= 2 else None,
           stagger=turns)
    if lod <= 2:
        m.facet(37, FOLD[lod], CLOTH + ("patch",))
    if lod == 0:   # a thigh pocket flap on the outside
        m.box((cx + 0.099, -0.046, 0.700), (cx + 0.106, 0.036, 0.766), {th: 1}, "patch")
    soft = kind == "boarder"
    if lod >= 4:
        sec = [(0.150, 0.060, 0.036, 0.190), (-0.215, 0.056, 0.036, 0.095)]
    elif soft:     # a snowboard boot: round and bulky
        sec = _keep([(3, 0.152, 0.054, 0.036, 0.185), (1, 0.115, 0.069, 0.034, 0.212), (2, 0.000, 0.070, 0.034, 0.218),
                     (1, -0.100, 0.070, 0.034, 0.160), (2, -0.185, 0.066, 0.034, 0.120), (3, -0.218, 0.050, 0.038, 0.098)],
                    lod)
    else:          # a ski boot: a hard shell, lower at the toe
        sec = _keep([(3, 0.152, 0.050, 0.040, 0.150), (1, 0.122, 0.062, 0.036, 0.206), (2, 0.000, 0.065, 0.036, 0.216),
                     (1, -0.090, 0.065, 0.036, 0.150), (2, -0.178, 0.061, 0.036, 0.106), (3, -0.218, 0.046, 0.040, 0.086)],
                    lod)

    def section(y, hw, z0, z1):   # near: a shell with sloped shoulders; far: a box
        if lod >= 2:
            return [(x + hw, y, z0), (x + hw, y, z1), (x - hw, y, z1), (x - hw, y, z0)]
        zm = z0 + 0.62 * (z1 - z0)
        return [(x + hw, y, z0), (x + hw, y, zm), (x + hw * 0.58, y, z1), (x - hw * 0.58, y, z1), (x - hw, y, zm),
                (x - hw, y, z0)]
    m.loft([(section(*row), {ft: 1}) for row in sec], "boot", cap0="boot", cap1="boot")
    if lod <= 1 and not soft:     # the sole's toe and heel lugs, and two buckles across the shell
        m.box((x - 0.038, -0.232, 0.022), (x + 0.038, -0.196, 0.044), {ft: 1}, "strap")
        m.box((x - 0.038, 0.128, 0.022), (x + 0.038, 0.166, 0.048), {ft: 1}, "strap")
        for y, z in ((-0.140, 0.128), (-0.060, 0.168)):
            m.box((x - 0.068, y - 0.011, z - 0.040), (x + 0.070, y + 0.011, z + 0.006), {ft: 1}, "strap")
            if lod == 0:
                m.box((x + 0.066, y - 0.016, z - 0.034), (x + 0.078, y + 0.016, z - 0.004), {ft: 1}, "metal")
    if lod <= 1 and soft:         # the lace panel up the instep
        _toward(m, [(x - 0.022, -0.200, 0.114), (x + 0.022, -0.200, 0.114), (x + 0.026, -0.100, 0.168),
                    (x - 0.026, -0.100, 0.168)], {ft: 1}, "strap", (0, -0.5, 1))
        _toward(m, [(x - 0.026, -0.100, 0.168), (x + 0.026, -0.100, 0.168), (x + 0.028, 0.000, 0.226),
                    (x - 0.028, 0.000, 0.226)], {ft: 1}, "strap", (0, -0.5, 1))
    return m


# ---------------------------------------------------------------------------------------------------------------
DOME = [(4, 1.765, 0.126, 0.146), (1, 1.815, 0.124, 0.143), (3, 1.855, 0.106, 0.124), (1, 1.878, 0.070, 0.084),
        (4, 1.888, 0.028, 0.034)]
FACE = [(4, 1.600, 0.045, 0.055, -0.022), (1, 1.635, 0.066, 0.082, -0.008), (2, 1.690, 0.076, 0.094, 0.0),
        (4, 1.765, 0.078, 0.096, 0.0)]
OPEN = (3, 2, 1, 1, 1, 1)           # half the width of the face opening, in stations


def head(lod):
    """Every shell (face, helmet, ear pads, lens, strap, brim) is cut on the same stations round the head."""
    m = Mesh()
    n = SIDES["head"][lod]
    w = {"head": 1}
    step = 360.0 / n
    phase = math.radians(30.0) if n == 6 else 0.0      # so that 270 degrees (the front) is a station
    lo, hi = 270.0 - OPEN[lod] * step, 270.0 + OPEN[lod] * step

    def arc(z, rx, ry, a_from, steps, cy=0.008):
        return [Vector((rx * math.cos(math.radians(a_from + i * step)), cy + ry * math.sin(math.radians(a_from + i * step)), z))
                for i in range(steps + 1)]
    m.loft([(vr(z, rx, ry, n, cy=cy, a0=phase), w) for z, rx, ry, cy in _keep(FACE, lod)], "skin",
           cap0="skin" if lod <= 3 else None)
    dome = _keep(DOME, lod)
    if lod >= 4:
        dome = [dome[0], (1.872, 0.088, 0.104)]
    m.loft([(vr(z, rx, ry, n, cy=0.008, a0=phase), w) for z, rx, ry in dome], "helmet",
           cap0="liner" if lod <= 2 else None, cap1="helmet")
    back = n - 2 * OPEN[lod]                           # the shell round the sides and the back, to the nape
    near = lod <= 1
    low = [Vector((p.x, p.y, 1.676 - 0.046 * max(0.0, (p.y - 0.008) / 0.126) if near else 1.640))
           for p in arc(0.0, 0.110, 0.126, hi, back)]
    m.loft([(low, w), (arc(1.765, 0.126, 0.146, hi, back), w)], "helmet", closed=False,
           inside="liner" if lod <= 2 else None)
    if near:        # soft ear pads from the shell's edge down to the jaw
        pad = 3 if lod == 0 else 2
        for a_from in (hi, lo - pad * step):
            m.loft([(arc(1.610, 0.090, 0.104, a_from, pad), w), (arc(1.646, 0.100, 0.116, a_from, pad), w),
                    (arc(1.682, 0.104, 0.120, a_from, pad), w)], "liner", closed=False, inside="liner")
    # the goggles: a big lens from the brow to the tip of the nose, wider than the face, convex, notched at the nose
    lens = [(1.672, 0.092, 0.114), (1.706, 0.099, 0.124), (1.740, 0.100, 0.126), (1.770, 0.096, 0.120)]
    if lod >= 2:
        lens = [lens[0], (1.722, 0.100, 0.126), lens[3]]
    if lod >= 4:
        lens = [lens[0], lens[2]]
    rows = [arc(z, rx, ry, lo, 2 * OPEN[lod], cy=0.0) for z, rx, ry in lens]
    if lod <= 2:
        rows[0][OPEN[lod]].z += 0.016
    # three bands of lens between dark side frames (the frames only where there are columns to spare)
    m.loft([(r, w) for r in rows], (lambda c: "liner" if abs(c.x) > 0.076 else "lens") if lod == 0 else "lens",
           closed=False)
    if lod <= 2:    # the strap round the helmet, in the guest's accent colour
        m.loft([(arc(1.730, 0.125, 0.143, hi, back), w), (arc(1.756, 0.129, 0.149, hi, back), w)], "accent", closed=False)
    if near:        # the foam frame behind the lens, the brim (a short lip), the nose (one facet)
        m.loft([(arc(1.662, 0.086, 0.106, lo, 2 * OPEN[lod], cy=0.0), w), (arc(1.780, 0.090, 0.112, lo, 2 * OPEN[lod], cy=0.0), w)],
               "liner", closed=False)
        b = max(1, OPEN[lod] - 1)
        m.tube(arc(1.764, 0.138, 0.160, 270.0 - b * step, 2 * b),
               [(0.007 + 0.007 * math.sin(math.pi * i / (2 * b)), 0.004 + 0.002 * math.sin(math.pi * i / (2 * b)))
                for i in range(2 * b + 1)], 4, w, "helmet", cap0="helmet", cap1="helmet")
        tipn = Vector((0.0, -0.121, 1.662))
        for a, b in (((0.0, -0.098, 1.700), (0.017, -0.092, 1.652)), ((-0.017, -0.092, 1.652), (0.0, -0.098, 1.700)),
                     ((0.017, -0.092, 1.652), (-0.017, -0.092, 1.652))):
            mid = (Vector(a) + Vector(b) + tipn) / 3.0
            _toward(m, [a, b, tuple(tipn)], w, "skin", mid - Vector((0.0, -0.085, 1.672)))
    if lod == 0:    # vents in the dome, the chin strap
        dome = [r[1:] for r in DOME]

        def shell(a, z):   # a point just proud of the dome
            (z0, rx0, ry0), (z1, rx1, ry1) = next((p, q) for p, q in zip(dome, dome[1:]) if p[0] <= z <= q[0])
            t = (z - z0) / (z1 - z0)
            rx, ry = rx0 + (rx1 - rx0) * t + 0.0008, ry0 + (ry1 - ry0) * t + 0.0008
            return (rx * math.cos(math.radians(a)), 0.008 + ry * math.sin(math.radians(a)), z)
        for a, z in ((247.5, 1.838), (270.0, 1.842), (292.5, 1.838), (225.0, 1.795), (315.0, 1.795), (90.0, 1.830),
                     (67.5, 1.795), (112.5, 1.795)):
            _toward(m, [shell(a - 3.2, z - 0.016), shell(a + 3.2, z - 0.016), shell(a + 3.2, z + 0.016),
                        shell(a - 3.2, z + 0.016)], w, "liner",
                    (math.cos(math.radians(a)), math.sin(math.radians(a)), 0.6))
        m.tube([(0.090, -0.028, 1.622), (0.060, -0.066, 1.596), (0.0, -0.084, 1.588), (-0.060, -0.066, 1.596),
                (-0.090, -0.028, 1.622)], (0.005, 0.009), 4, w, "strap")
    return m


# ---------------------------------------------------------------------------------------------------------------
def speck():
    """The farthest model: legs, a jacket and a helmet as three boxes."""
    m = Mesh()
    m.loft([(vr(0.06, 0.150, 0.110, 4, power=4), P), (vr(0.900, 0.200, 0.150, 4, power=4), P)], "pants")
    m.loft([(vr(0.880, 0.225, 0.165, 4, power=4), P), (vr(1.470, 0.220, 0.160, 4, power=4), S3),
            (vr(1.600, 0.120, 0.130, 4, power=4), {"head": 1}), (vr(1.888, 0.105, 0.120, 4, power=4), {"head": 1})],
           ["jacket", "jacket", "helmet"], cap1="helmet")
    return m


def figure(lod, kind="skier"):
    """The whole dressed figure in the rest pose."""
    if lod >= 5:
        return speck()
    m = Mesh()
    m.merge(torso(lod))
    for part in (arm(lod, kind), leg(lod, kind)):
        m.merge(part)
        m.merge(part.mirrored())
    m.merge(head(lod))
    return m
