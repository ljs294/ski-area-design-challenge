"""The carrier sweep for surface lifts (Gate 1 v2 finding T6): a retracted carrier (the recoil box on its grip with
its bar hanging under it) is carried along the rope past a terminal's trains, round its bullwheel and back out, or past
a tower head on both ropes, and must meet nothing.

Works on LOD0 assets in the lift frame. A terminal's carriers arrive on -v travelling -u, go round the back of the
wheel (-u) and leave on +v travelling +u (the drive's loaded rope leaves on +v; the return's arrives on its v-); a
head's pass on both ropes. The carrier's frame: u the way it travels, +v outboard. The rope itself is the game's; over a
breakover row (v3.1) it follows the row's sheaves at rest, the grip and its hanger pitch with it and the hinged box
(pivot "box") hangs plumb from its pin with the bar under it.
"""
import math

from mathutils import Vector
from mathutils.bvhtree import BVHTree

from liftkit import frame
from liftkit import pose


def _lift(mb):
    return [Vector(frame.lift(v)) for v in mb.verts], [list(f) for f in mb.faces]


def _merge(meshes):
    verts, faces = [], []
    for vs, fs in meshes:
        base = len(verts)
        verts += vs
        faces += [[base + i for i in f] for f in fs]
    return verts, faces


def structure(a):
    """{name: (verts, faces)}: the asset's body and each moving part, in the lift frame."""
    out = {"body": _lift(a.body)}
    for name, mb in a.parts.items():
        out[name] = _lift(mb)
    return out


def carrier(box, bar, gap):
    """The retracted carrier in its own frame (origin on the rope at the grip): (rigid, hung, pin). `rigid` is the grip
    and hanger (the box asset's body), `hung` the hinged box with the bar hung `gap` under its cord socket, `pin` the
    box's hinge (pivot "box")."""
    eye = Vector(box.sockets["cord"]) - Vector((0.0, 0.0, gap))
    bv, bf = _lift(bar.body)
    hung = [_lift(mb) for name, mb in box.parts.items() if name in box.pivots]
    rigid = [_lift(mb) for name, mb in box.parts.items() if name not in box.pivots]
    return (_merge([_lift(box.body)] + rigid), _merge(hung + [([v + eye for v in bv], bf)]),
            Vector(box.pivots["box"]["pos"]))


def _posed(car, at, yaw, pitch=0.0):
    """The carrier at `at` travelling along `yaw`, its grip pitched `pitch` (nose up) with the rope, the box plumb."""
    (rv, rf), (hv, hf), pin = car
    cp, sp = math.cos(pitch), math.sin(pitch)

    def tilt(v):
        return Vector((v.x * cp - v.z * sp, v.y, v.x * sp + v.z * cp))
    shift = tilt(pin) - pin
    c, s = math.cos(yaw), math.sin(yaw)

    def turn(v):
        return Vector((at[0] + v.x * c - v.y * s, at[1] + v.x * s + v.y * c, at[2] + v.z))
    return [turn(tilt(v)) for v in rv] + [turn(v + shift) for v in hv], rf + [[len(rv) + i for i in f] for f in hf]


def rope_line(a, side, far=10.0):
    """The rope's (u, w) polyline along one side ("left" or "right") of a terminal at rest: level from the wheel to
    rope_<side>_hold, over the side's breakover row (the sheaves under eq_<l|r>, liftkit/pose.rope_path) and on at
    the asset's ropeExitDeg for `far` metres (v3.1). Without a hold socket the rope stays level."""
    s = a.sockets
    rope = s[f"rope_{side}_bw"][2]
    if f"rope_{side}_hold" not in s:
        return [(-1.0, rope), (100.0, rope)]
    top = f"eq_{side[0]}"
    row = sorted((k for k in pose.subtree(a.pivots, top) if k.startswith("sheave_")), key=lambda k: a.pivots[k]["pos"][0])
    rho = s[f"rope_{side}_hold"][2] - a.pivots[row[0]]["pos"][2]   # the hold sits straight over the first axle
    path = pose.rope_path(a.pivots, {}, row, rho, 0.0, math.radians(a.dims["ropeExitDeg"]), 1.0, far)
    return [(-1.0, rope)] + [(float(u), float(w)) for u, w in path[1:]]


def _segment(line, u):
    for (u0, w0), (u1, w1) in zip(line, line[1:]):
        if u <= u1:
            return u0, w0, u1, w1
    return line[-2] + line[-1]


def rope_profile(a, side):
    """The rope's height along u on one side of a terminal (rope_line), as a function of u."""
    line = rope_line(a, side)

    def w(u):
        u0, w0, u1, w1 = _segment(line, u)
        return w0 + (w1 - w0) * (u - u0) / (u1 - u0)
    return w


def rope_slope(a, side):
    """dw/du of the rope along one side of a terminal (rope_line), as a function of u."""
    line = rope_line(a, side)

    def k(u):
        u0, w0, u1, w1 = _segment(line, u)
        return (w1 - w0) / (u1 - u0)
    return k


def poses_terminal(a, hg, rope, pitch, step=0.25, dphi=4.0):
    """(where, yaw radians, pitch radians, label) along a terminal's rope: in on -v from the out socket, round the
    back, out on +v, at the rope's height and slope along each side (rope_line)."""
    u_out = max(a.sockets["rope_left_out"][0], a.sockets["rope_right_out"][0]) + 1.0
    w_in, w_out = rope_profile(a, "left"), rope_profile(a, "right")
    k_in, k_out = rope_slope(a, "left"), rope_slope(a, "right")
    R = pitch / 2
    out = []
    n = int(u_out / step)
    for k in range(n, -1, -1):
        out.append(((k * step, -hg, w_in(k * step)), math.pi, math.atan(-k_in(k * step)), f"in u {k * step:.2f}"))
    phi = 270.0
    while phi >= 90.0 - 1e-6:
        p = math.radians(phi)
        out.append(((R * math.cos(p), R * math.sin(p), rope), p - math.pi / 2, 0.0, f"wheel {phi:.0f} deg"))
        phi -= dphi
    for k in range(n + 1):
        out.append(((k * step, hg, w_out(k * step)), 0.0, math.atan(k_out(k * step)), f"out u {k * step:.2f}"))
    return out


def poses_head(hg, rope, reach=3.0, step=0.1):
    out = []
    n = int(reach / step)
    for k in range(-n, n + 1):
        out.append(((k * step, hg, rope), 0.0, 0.0, f"right u {k * step:.1f}"))
        out.append(((k * step, -hg, rope), math.pi, 0.0, f"left u {k * step:.1f}"))
    return out


def check(a, kind, spec, box, bar):
    """Problems (strings) where the retracted carrier meets the asset `a` (a surface terminal or tower head)."""
    c = spec["common"]
    hg = c["lineGauge"] / 2000
    gap = spec["carrier"]["cordGap"] / 1000
    rope = a.dims["rope"] / 1000
    if kind == "surface_terminal":
        poses = poses_terminal(a, hg, rope, c["bullwheel"]["pitch"] / 1000)
    else:
        poses = poses_head(hg, rope)
    car = carrier(box, bar, gap)
    trees = {name: BVHTree.FromPolygons(vs, fs) for name, (vs, fs) in structure(a).items() if fs}
    problems, seen = [], set()
    for at, yaw, pitch, label in poses:
        vs, fs = _posed(car, at, yaw, pitch)
        ct = BVHTree.FromPolygons(vs, fs)
        for name, t in trees.items():
            if name in seen:
                continue
            if ct.overlap(t):
                seen.add(name)
                problems.append(f"a retracted carrier meets {name} ({label})")
    return problems
