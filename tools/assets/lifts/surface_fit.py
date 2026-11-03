"""The carrier sweep for surface lifts (Gate 1 v2 finding T6): a retracted carrier (the recoil box on its grip with
its bar hanging under it) is carried along the rope past a terminal's trains, round its bullwheel and back out, or past
a tower head on both ropes, and must meet nothing.

Works on LOD0 assets in the lift frame. A terminal's carriers arrive on -v travelling -u, go round the back of the
wheel (-u) and leave on +v travelling +u (the drive's loaded rope leaves on +v; the return's arrives on its v-); a
head's pass on both ropes. The carrier's frame: u the way it travels, +v outboard. The rope itself is the game's.
"""
import math

from mathutils import Vector
from mathutils.bvhtree import BVHTree

from liftkit import frame


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
    """The retracted carrier's mesh in its own frame (origin on the rope at the grip): the box asset, the bar hung
    `gap` under the cord socket."""
    eye = Vector(box.sockets["cord"]) - Vector((0.0, 0.0, gap))
    bv, bf = _lift(bar.body)
    return _merge([_lift(box.body)] + [_lift(mb) for mb in box.parts.values()] + [([v + eye for v in bv], bf)])


def _posed(mesh, at, yaw):
    vs, fs = mesh
    c, s = math.cos(yaw), math.sin(yaw)
    return [Vector((at[0] + v.x * c - v.y * s, at[1] + v.x * s + v.y * c, at[2] + v.z)) for v in vs], fs


def poses_terminal(a, hg, rope, pitch, step=0.25, dphi=4.0):
    """(where, yaw radians) along a terminal's rope: in on -v from the out socket, round the back, out on +v."""
    u_out = max(a.sockets["rope_left_out"][0], a.sockets["rope_right_out"][0]) + 1.0
    R = pitch / 2
    out = []
    n = int(u_out / step)
    for k in range(n, -1, -1):
        out.append(((k * step, -hg, rope), math.pi, f"in u {k * step:.2f}"))
    phi = 270.0
    while phi >= 90.0 - 1e-6:
        p = math.radians(phi)
        out.append(((R * math.cos(p), R * math.sin(p), rope), p - math.pi / 2, f"wheel {phi:.0f} deg"))
        phi -= dphi
    for k in range(n + 1):
        out.append(((k * step, hg, rope), 0.0, f"out u {k * step:.2f}"))
    return out


def poses_head(hg, rope, reach=3.0, step=0.1):
    out = []
    n = int(reach / step)
    for k in range(-n, n + 1):
        out.append(((k * step, hg, rope), 0.0, f"right u {k * step:.1f}"))
        out.append(((k * step, -hg, rope), math.pi, f"left u {k * step:.1f}"))
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
    for at, yaw, label in poses:
        vs, fs = _posed(car, at, yaw)
        ct = BVHTree.FromPolygons(vs, fs)
        for name, t in trees.items():
            if name in seen:
                continue
            if ct.overlap(t):
                seen.add(name)
                problems.append(f"a retracted carrier meets {name} ({label})")
    return problems
