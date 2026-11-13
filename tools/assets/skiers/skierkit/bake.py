"""The game's copy of the figures: every LOD mesh, every clip baked to bone matrices, and the outfit table, in one
little-endian file (out/skiers.bytes) that the Unity import copies as it is. A summary goes to out/skiers.json.

Everything is written in Unity's axes: x the figure's right, y up, z forward (Blender's (x, y, z) is (-x, z, -y)).
A bone matrix maps a rest-pose vertex to the posed one: v' = R v + t, in the figure's own frame (the origin on the
snow between the boots, or the seat for a seated pose). The game places and turns the figure; a clip only moves
the body.

Layout (version 1):
  "MPSK", u32 version, u32 kinds, u32 LOD count, f32 untilM per LOD, f32 hysteresis (budgets.json)
  per kind: u8 name length, name, u32 bones, u32 lods, u32 clips, u32 outfits
    per LOD: u32 vertices, then per vertex: f32 x, y, z; u8 bone[4]; u8 weight[4] (sum 255); u8 matid, tone, 0, 0
             (a triangle soup: vertices 3k, 3k+1, 3k+2 are one face, front faces clockwise seen from outside)
    per clip: u8 name length, name, u32 frames, u8 loops, u8 pitch (degrees), u8 0, u8 0
    per clip, per frame, per bone: f32 r00 r01 r02 tx, r10 r11 r12 ty, r20 r21 r22 tz
    per outfit: per material id (16): u8 r, g, b (sRGB), u8 0
"""
import json
import math
import os
import struct

from mathutils import Matrix, Vector

from . import build, clips, outfit, poses, rig
from . import skeleton as sk
from .mesh import MAT, MATS, face_tone

VERSION = 1
KINDS = ("skier", "boarder")
TURN_FRAMES = 32
TRAVEL_FRAMES = 16
PITCHES = (0, 12, 24, 36)        # slopes the turns are baked on; the game takes the nearest
OUTFITS = 1024                   # keyed outfits per kind; a guest wears outfit (serial % OUTFITS)
C = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))     # Blender figure axes -> Unity figure axes
STYLES = ("green", "blue", "black", "expert")


def unity(v):
    return C @ Vector(v)


# ---------------------------------------------------------------------------------------------------------------
# The two travel clips: a skier's shuffle (walking on skis to a lift and up a line) and a snowboarder's push with the
# rear foot out of its binding.
def _shuffle():
    base = clips.full("skier", poses.SKIER["stand"])

    def step(s):     # s = +1: the left ski slid forward, the right pole planted beside it
        return clips.merged(base, {
            "pelvis": {"pos": (0.03 * s, -0.02, -0.04), "rot": (6, 3 * s, 2 * s)}, "spine": (8, -4 * s, 0),
            "ski_l": {"pos": (0.13, -0.16 * s, 0), "yaw": 3}, "ski_r": {"pos": (-0.13, 0.16 * s, 0), "yaw": -3},
            "arm_l": {"hand": (0.10, -0.26 + 0.10 * s, -0.28), "pole": (0.05, 0.30 * s, -0.95)},
            "arm_r": {"hand": (-0.10, -0.26 - 0.10 * s, -0.28), "pole": (-0.05, -0.30 * s, -0.95)},
            "gaze": (2, 0, 0.3)})
    mid = clips.between(step(1), step(-1), 0.5)
    return clips.spline([(0.0, step(1)), (0.25, clips._lift(mid, 0.01)), (0.5, step(-1)), (0.75, clips._lift(mid, 0.01))])


def _push():
    base = clips.full("boarder", poses.BOARDER["queue"])
    low = poses.FREE_ANKLE

    def foot(y, z):
        return {"foot_r": {"pos": (-0.20, y, z), "rot": (0, -40, 0)}}
    keys = [(0.0, clips.merged(base, foot(-0.06, low))),                       # planted ahead of the board
            (0.5, clips.merged(base, {**foot(0.26, low), "spine": (10, 10, 0)})),   # pushed back
            (0.75, clips.merged(base, foot(0.12, low + 0.07)))]                # lifted, swinging forward
    return clips.spline(keys)


def _pitched(pose, slope):
    """The pose on a slope of `slope` degrees, falling toward the direction of travel: the skis or the board lie on
    the slope, the body stands over them a little more upright than square to it, and the head looks level."""
    if slope == 0:
        return pose
    g = pose.get("gaze", (0.0, 0.0, 1.0))
    pel = pose["pelvis"]
    out = clips.merged(pose, {"gaze": (g[0] - slope, g[1], g[2]),
                              "pelvis": {"pos": pel["pos"], "rot": (pel["rot"][0] - 0.3 * slope, pel["rot"][1], pel["rot"][2])}})
    return out


def clip_list(kind):
    """[(name, f(phase) -> pose or None for a still, frames, loops, pitch)]."""
    out = []
    if kind == "skier":
        for style in STYLES:
            turn = clips.skier_turn(style)
            for p in PITCHES:
                out.append((f"turn_{style}_{p}", turn, TURN_FRAMES, True, p))
        out.append(("travel", _shuffle(), TRAVEL_FRAMES, True, 0))
        for name in ("stand", "lean", "rest", "seated"):
            pose = poses.SKIER[name]
            out.append((name, lambda phase, pose=pose: pose, 1, False, 0))
    else:
        turn = clips.boarder_turn()
        for p in PITCHES:
            out.append((f"turn_board_{p}", turn, TURN_FRAMES, True, p))
        out.append(("travel", _push(), TRAVEL_FRAMES, True, 0))
        for name in ("stand", "queue", "seated"):
            pose = poses.BOARDER[name]
            out.append((name, lambda phase, pose=pose: pose, 1, False, 0))
    return out


def matrices(kind, pose, pitch):
    """[(R 3x3, t)] per bone in skeleton order, Unity axes, and the worst reach error (m)."""
    R = sk.rest(kind)
    W, head, err = rig.solve(kind, _pitched(clips.full(kind, pose), pitch))
    P = Matrix.Rotation(math.radians(pitch), 3, "X")       # tips the whole figure down the slope (toward -Y)
    out = []
    for name, h, _, _ in sk.bones(kind):
        w = P @ W[name]
        t = P @ head[name] - w @ R[name][0]
        out.append((C @ w @ C.transposed(), C @ t))
    return out, err


def skin(mats, pos, bones, weights):
    """One vertex skinned in Python the way the shader does it (Unity axes)."""
    acc = Vector()
    for b, w in zip(bones, weights):
        if w:
            r, t = mats[b]
            acc += (r @ pos + t) * (w / 255.0)
    return acc


# ---------------------------------------------------------------------------------------------------------------
def _quantise(ws):
    """Weights (floats summing to 1) as bytes summing exactly to 255."""
    raw = [w * 255.0 for w in ws]
    q = [int(math.floor(x)) for x in raw]
    order = sorted(range(len(raw)), key=lambda i: -(raw[i] - q[i]))
    for i in order[:255 - sum(q)]:
        q[i] += 1
    return q


def soup(kind, lod):
    """The LOD as a triangle soup: [(pos Unity, bones[4], weights[4], matid, tone)], plus its triangle count."""
    m = build.mesh(kind, lod)
    names = [n for n, _, _, _ in sk.bones(kind)]
    index = {n: i for i, n in enumerate(names)}
    out = []
    for idx, mat in m.faces:
        centre = sum((m.verts[i] for i in idx), Vector()) / len(idx)
        tone = 0.5 + face_tone(mat + 1, centre) * (0.08 if mat in rig.CLOTH_IDS else 0.03)
        if mat == MAT["lens"]:
            tone = 0.5 + max(-0.25, min(0.25, (1.722 - centre.z) * 12.0))
        tone_b = max(0, min(255, int(round(tone * 255))))
        for k in range(1, len(idx) - 1):
            # Blender winds faces counter-clockwise seen from outside. The axis change keeps what a viewer sees (its
            # left-right flip is undone by Unity's left-handed screen), so the face still reads counter-clockwise:
            # it is turned round, because Unity's front faces are clockwise.
            for vi in (idx[0], idx[k + 1], idx[k]):
                w = sorted(m.weights[vi].items(), key=lambda t: -t[1])[:4]
                bones = [index[b] for b, _ in w] + [0] * (4 - len(w))
                q = _quantise([x for _, x in w]) + [0] * (4 - len(w))
                out.append((unity(m.verts[vi]), bones, q, mat, tone_b))
    return out, m.tris()


def outfit_rows(kind):
    rows = []
    for serial in range(OUTFITS):
        o = outfit.outfit(serial, kind)
        row = []
        for i in range(16):
            c = o[MATS[i]] if i < len(MATS) else (1.0, 0.0, 1.0)
            row.append(tuple(max(0, min(255, int(round(x * 255)))) for x in c))
        rows.append(row)
    return rows


def write(path_bytes, path_json):
    summary = {"version": VERSION, "axes": "Unity: x right, y up, z forward; metres", "kinds": {}}
    with open(path_bytes, "wb") as f:
        f.write(b"MPSK")
        f.write(struct.pack("<II", VERSION, len(KINDS)))
        f.write(struct.pack("<I", build.LOD_COUNT) + struct.pack(f"<{build.LOD_COUNT}f", *build.UNTIL_M)
                + struct.pack("<f", build.HYSTERESIS))
        for kind in KINDS:
            names = [n for n, _, _, _ in sk.bones(kind)]
            cl = clip_list(kind)
            name = kind.encode()
            f.write(struct.pack("<B", len(name)) + name)
            f.write(struct.pack("<IIII", len(names), build.LOD_COUNT, len(cl), OUTFITS))
            ks = {"bones": names, "lods": [], "clips": [], "reference": []}
            soups = []
            for lod in range(build.LOD_COUNT):
                verts, tris = soup(kind, lod)
                if tris > build.BUDGET[lod]:
                    raise SystemExit(f"{kind} LOD{lod}: {tris} triangles, over the budget of {build.BUDGET[lod]}")
                soups.append(verts)
                f.write(struct.pack("<I", len(verts)))
                for p, bones, q, mat, tone in verts:
                    f.write(struct.pack("<fff4B4B4B", p.x, p.y, p.z, *bones, *q, mat, tone, 0, 0))
                ks["lods"].append({"lod": lod, "triangles": tris, "vertices": len(verts),
                                   "influences": max(sum(1 for x in v[2] if x) for v in verts)})
            frames = []
            for cname, fn, n, loops, pitch in cl:
                c = cname.encode()
                f.write(struct.pack("<B", len(c)) + c + struct.pack("<IBBBB", n, int(loops), pitch, 0, 0))
                worst = 0.0
                for k in range(n):
                    mats, err = matrices(kind, fn(k / n), pitch)
                    worst = max(worst, err)
                    frames.append(mats)
                ks["clips"].append({"name": cname, "frames": n, "loops": loops, "pitch": pitch,
                                    "reach_mm": round(worst * 1000, 1)})
            for mats in frames:
                for r, t in mats:
                    for row in range(3):
                        f.write(struct.pack("<4f", r[row][0], r[row][1], r[row][2], t[row]))
            for row in outfit_rows(kind):
                for rgb in row:
                    f.write(struct.pack("<4B", *rgb, 0))
            # Reference points for the Unity test: LOD0 vertices skinned here, in a few clips and frames.
            first = 0
            starts = []
            for cname, fn, n, loops, pitch in cl:
                starts.append(first)
                first += n
            for ci, k in ((0, 0), (len(STYLES) * len(PITCHES) - 2 if kind == "skier" else 2, 7), (len(cl) - 1, 0)):
                for vi in (0, len(soups[0]) // 3, (2 * len(soups[0])) // 3 + 1):
                    p, bones, q, _, _ = soups[0][vi]
                    s = skin(frames[starts[ci] + k], p, bones, q)
                    ks["reference"].append({"clip": cl[ci][0], "frame": k, "vertex": vi,
                                            "rest": [round(p.x, 6), round(p.y, 6), round(p.z, 6)],
                                            "posed": [round(s.x, 6), round(s.y, 6), round(s.z, 6)]})
            summary["kinds"][kind] = ks
    with open(path_json, "w") as f:
        json.dump(summary, f, indent=1)
    return summary
