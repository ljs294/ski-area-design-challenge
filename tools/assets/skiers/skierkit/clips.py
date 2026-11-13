"""Motion clips. A clip is f(phase 0..1) -> pose: a closed curve through key poses, so it loops by construction.

One clip is a linked pair of turns. Phase 0 is the change of edges into a turn to the figure's LEFT (its apex comes
late, at 0.30); phase 0.5 is the change into the turn to the right. Each turn has four keys:
  0.00  change of edges: the new inside pole touches the snow, the head and chest already face the new turn
  0.10  start: the tallest moment, the pole let go
  0.30  apex: the lowest, most on edge (and most skid, in a skidded turn)
  0.42  release: the legs give, the other pole swings forward for its plant
For a skier the second turn is the mirror of the first. A snowboarder (left foot leading) turns left on the heel
edge and right on the toe edge, so the two halves are written out.

The game moves and turns the figure's root along its path; a clip only moves the body over the skis or the board.
Angles in degrees, positions in metres (see rig.solve for the fields).
"""
from . import skeleton as sk

_REACH = sk.UPPER_ARM + sk.FOREARM - 0.01
POLE_LAG = 0.03     # the pole tips follow the hands a little late


def full(kind, pose):
    """The pose with every field filled in, so that poses can be mixed."""
    out = {"pelvis": {"pos": (0.0, 0.0, 0.0), "rot": (0.0, 0.0, 0.0)}, "spine": (0.0, 0.0, 0.0),
           "gaze": (0.0, 0.0, 1.0)}
    for sx, s in (("_l", 1), ("_r", -1)):
        out["knee" + sx] = (0.0, 0.0, 0.0)
        out["arm" + sx] = {"hand": (sk.ARM_DIR.x * s * _REACH, 0.0, sk.ARM_DIR.z * _REACH),
                           "elbow": (0.3 * s, 1.0, -0.4), "wrist": (0.0, 0.0, 0.0), "pole": (0.0, 0.25, -1.0)}
        if kind == "skier":
            out["ski" + sx] = {"pos": (sk.HIP_X * s, 0.0, 0.0), "yaw": 0.0, "edge": 0.0, "pitch": 0.0}
    if kind == "boarder":
        out["board"] = {"pos": (0.0, 0.0, 0.0), "yaw": 0.0, "edge": 0.0, "pitch": 0.0}
    return merged(out, pose)


def merged(base, over):
    """`base` with the fields of `over` laid on top (one level into each part)."""
    out = dict(base)
    for k, v in over.items():
        out[k] = {**out.get(k, {}), **v} if isinstance(v, dict) else v
    return out


def mix(parts):
    """A weighted sum of full poses: [(pose, weight), ...]."""
    first = parts[0][0]
    if isinstance(first, dict):
        return {k: mix([(p[k], w) for p, w in parts]) for k in first}
    if isinstance(first, (tuple, list)):
        return tuple(sum(p[i] * w for p, w in parts) for i in range(len(first)))
    if isinstance(first, bool):
        return first
    return sum(p * w for p, w in parts)


def between(a, b, t):
    return mix([(a, 1.0 - t), (b, t)])


def _flip_x(v):
    return (-v[0], v[1], v[2])


def _flip_rot(r):
    return (r[0], -r[1], -r[2])


def mirror(pose):
    """The same full skier pose turning the other way."""
    out = {}
    for k, v in pose.items():
        other = k[:-2] + ("_r" if k.endswith("_l") else "_l") if k[-2:] in ("_l", "_r") else k
        if k == "pelvis":
            out[k] = {"pos": _flip_x(v["pos"]), "rot": _flip_rot(v["rot"])}
        elif k == "spine":
            out[k] = _flip_rot(v)
        elif k == "gaze":
            out[k] = (v[0], -v[1], v[2])
        elif k.startswith("ski"):
            out[other] = {"pos": _flip_x(v["pos"]), "yaw": -v["yaw"], "edge": -v["edge"], "pitch": v["pitch"]}
        elif k.startswith("knee"):
            out[other] = _flip_x(v)
        elif k.startswith("arm"):
            out[other] = {"hand": _flip_x(v["hand"]), "elbow": _flip_x(v["elbow"]), "wrist": _flip_rot(v["wrist"]),
                          "pole": _flip_x(v["pole"])}
        else:
            out[k] = v
    return out


def spline(keys, lag=0.0):
    """A closed smooth curve through [(phase, full pose), ...] (phases rising, the first at 0). lag: the pole aims
    are read this much earlier in the cycle, so the tips trail the hands."""
    n = len(keys)

    def at(i):
        return keys[i % n][0] + (i // n), keys[i % n][1]

    def raw(phase):
        phase %= 1.0
        i = max(j for j in range(n) if keys[j][0] <= phase)
        (ta, pa), (tb, pb), (tc, pc), (td, pd) = at(i - 1), at(i), at(i + 1), at(i + 2)
        span = tc - tb
        s = (phase - tb) / span
        h00, h10 = 2 * s ** 3 - 3 * s ** 2 + 1, s ** 3 - 2 * s ** 2 + s
        h01, h11 = -2 * s ** 3 + 3 * s ** 2, s ** 3 - s ** 2
        mb, mc = h10 * span / (tc - ta), h11 * span / (td - tb)     # the slopes at b and c, from their neighbours
        return mix([(pa, -mb), (pb, h00 - mc), (pc, h01 + mb), (pd, mc)])

    def clip(phase):
        pose = raw(phase)
        if lag:
            late = raw(phase - lag)
            for sx in ("_l", "_r"):
                pose["arm" + sx] = dict(pose["arm" + sx], pole=late["arm" + sx]["pole"])
        return pose
    return clip


def _lift(pose, dz):
    x, y, z = pose["pelvis"]["pos"]
    return merged(pose, {"pelvis": {"pos": (x, y, z + dz)}})


# ---------------------------------------------------------------------------------------------------------------
# Skiing. A turn's apex is written turning to the figure's left: the left ski is inside, the right ski outside.
def _arms(reach=0.32, drop=0.20, spread=0.10, tips=(0.06, 0.5, -0.86)):
    """Hands forward at hip height, elbows bent and out, the baskets trailing beside the boot heels."""
    return {"arm_l": {"hand": (spread, -reach, -drop), "elbow": (0.7, 0.2, -0.7), "pole": tips},
            "arm_r": {"hand": (-spread, -reach, -drop), "elbow": (-0.7, 0.2, -0.7), "pole": _flip_x(tips)}}


def _parallel(k, skid=0.0, stem=0.0):
    """k: how hard the turn is, 0..1 (1 = laid over, hip near the snow). skid: degrees the skis point into the
    turn off the line of travel. stem: degrees the tips converge at the change of edges (an intermediate's habit)."""
    neutral = {"pelvis": {"pos": (0, 0.09 + 0.02 * k, -0.05 - 0.06 * k), "rot": (14, 0, 0)}, "spine": (16, 0, 0),
               "ski_l": {"pos": (0.13, 0, 0), "yaw": -stem}, "ski_r": {"pos": (-0.13, 0, 0), "yaw": stem}, **_arms()}
    apex = {"pelvis": {"pos": (0.55 * k, 0.08 - 0.02 * k, -0.06 - 0.10 * k - 0.17 * k * k),
                       "rot": (14 + 6 * k, 0.6 * skid - 8 * k, 30 * k)},
            "spine": (16 + 8 * k, -4 * k, -22 * k),
            "ski_l": {"pos": (0.13 + 0.17 * k, -0.16 * k, 0), "edge": 60 * k, "yaw": skid},
            "ski_r": {"pos": (-0.13 - 0.03 * k, 0.04 * k, 0), "edge": 60 * k, "yaw": skid},
            "knee_l": (0.5 * k, 0, 0), "knee_r": (0.6 * k, 0, 0),
            "arm_l": {"hand": (0.10 + 0.05 * k, -0.30, -0.20 - 0.06 * k), "elbow": (0.7, 0.2, -0.7),
                      "pole": (0.10 + 0.25 * k, 0.5, -0.86)},
            # the outside pole swings out so its basket trails outside the outside ski, not between the skis
            "arm_r": {"hand": (-0.10, -0.34, -0.18), "elbow": (-0.7, 0.2, -0.7), "pole": (-0.06 - 0.40 * k, 0.5, -0.80)}}
    return neutral, apex


def _short(k):
    """Short turns: the chest stays quiet and faces down the hill while the legs swing the skis out to the side and
    pivot them; the skier stays low the whole way and plants a pole at every turn."""
    neutral = {"pelvis": {"pos": (0, 0.10, -0.12), "rot": (15, 0, 0)}, "spine": (18, 0, 0),
               "ski_l": {"pos": (0.13, 0, 0)}, "ski_r": {"pos": (-0.13, 0, 0)}, **_arms()}
    apex = {"pelvis": {"pos": (0.10 * k, 0.10, -0.15), "rot": (16, -10 * k, 8 * k)}, "spine": (20, -6 * k, -10 * k),
            "ski_l": {"pos": (0.13 - 0.22 * k, -0.10 * k, 0), "edge": 40 * k, "yaw": 12 * k},
            "ski_r": {"pos": (-0.13 - 0.26 * k, 0.03 * k, 0), "edge": 40 * k, "yaw": 12 * k},
            "knee_l": (0.5 * k, 0, 0), "knee_r": (0.6 * k, 0, 0),
            "arm_l": {"hand": (0.12, -0.31, -0.22), "elbow": (0.7, 0.2, -0.7), "pole": (0.14, 0.5, -0.86)},
            "arm_r": {"hand": (-0.10, -0.34, -0.18), "elbow": (-0.7, 0.2, -0.7), "pole": (-0.30 * k, 0.5, -0.82)}}
    return neutral, apex


def _wedge():
    """The snowplough: tips together, tails apart, both skis on their inside edges; the outside ski takes the weight
    and steers. Upright, arms wide, poles dragging."""
    wide = _arms(reach=0.28, drop=0.22, spread=0.20, tips=(0.10, 0.6, -0.80))
    neutral = {"pelvis": {"pos": (0, 0.11, -0.075), "rot": (8, 0, 0)}, "spine": (14, 0, 0),
               "ski_l": {"pos": (0.29, 0, 0), "yaw": -13, "edge": -12}, "ski_r": {"pos": (-0.29, 0, 0), "yaw": 13, "edge": 12},
               **wide}
    apex = {"pelvis": {"pos": (-0.16, 0.12, -0.115), "rot": (8, 18, -6)}, "spine": (12, 8, -6),
            "ski_l": {"pos": (0.29, 0, 0), "yaw": -7, "edge": -4}, "ski_r": {"pos": (-0.29, 0, 0), "yaw": 17, "edge": 20},
            **wide}
    return neutral, apex


SKI_STYLES = {"green": _wedge, "blue": lambda: _parallel(0.38, skid=14, stem=3), "black": lambda: _short(0.85),
              "expert": lambda: _parallel(1.0)}
RISE = {"green": 0.03, "blue": 0.03, "black": 0.01, "expert": 0.02}     # how far the skier comes up to start a turn
# How a viewer tells them apart from far off: the ski V and wide arms (green); tails brushing and a clear bob (blue);
# short quick turns, twice to a traverse, with a quiet chest and firm plants (black); long arcs laid right over (expert).
TURNS_PER_SWING = {"green": 1, "blue": 1, "black": 2, "expert": 1}


def skier_turn(style):
    neutral, apex = (full("skier", p) for p in SKI_STYLES[style]())
    plant = style != "green"
    change = merged(neutral, {"spine": (neutral["spine"][0], 10.0, 0.0), "gaze": (4.0, 15.0, 0.2)})
    if plant:    # the left pole reaches forward and touches as the edges change
        change = merged(change, {"arm_l": {"hand": (0.14, -0.40, -0.07), "pole": (0.03, -0.30, -0.95)}})
    start = merged(_lift(between(neutral, apex, 0.25), RISE[style]), {"gaze": (4.0, 12.0, 0.2)})
    top = merged(apex, {"gaze": (4.0, 8.0, 0.25)})
    release = merged(_lift(between(neutral, apex, 0.45), -RISE[style]), {"gaze": (4.0, 0.0, 0.2)})
    if plant:    # the right pole swings forward for the next plant
        release = merged(release, {"arm_r": {"hand": (-0.12, -0.38, -0.06), "pole": (-0.03, -0.15, -0.99)}})
    half = [(0.00, change), (0.10, start), (0.30, top), (0.42, release)]
    return spline(half + [(t + 0.5, mirror(p)) for t, p in half], lag=POLE_LAG)


# ---------------------------------------------------------------------------------------------------------------
# Snowboarding, riding toward -Y with the left foot leading: the heel edge is on the figure's left in the world, so
# the first turn of the clip (to the left) is the heel-side turn and the second (to the right) the toe-side turn.
def _board_arms(forward=0.14):
    """The leading hand out over the nose, the rear hand trailing: both low and quiet."""
    return {"arm_l": {"hand": (0.30, -forward, -0.30), "elbow": (0.6, 0.6, -0.5)},
            "arm_r": {"hand": (-0.26, -forward + 0.04, -0.32), "elbow": (-0.6, 0.6, -0.5)}}


def boarder_turn(k=1.0):
    neutral = {"pelvis": {"pos": (0, -0.05, -0.10), "rot": (6, -80, 0)}, "spine": (8, 6, 0), "board": {"yaw": -90},
               "gaze": (2.0, 0.0, 0.2), **_board_arms()}
    # heel side: the hips out over the heel edge as if sitting on a stool, the chest up, the hands forward
    heel = {"pelvis": {"pos": (0.42 * k, -0.02, -0.12), "rot": (6 - 8 * k, -80, 0)}, "spine": (8 + 6 * k, 6, 0),
            "board": {"yaw": -90, "pitch": -26 * k}, "gaze": (2.0, 8.0, 0.25), **_board_arms(forward=0.25)}
    # toe side: the whole body laid in over the toe edge like a plank, knees bent a little, the back straight
    toe = {"pelvis": {"pos": (-0.46 * k, -0.02, -0.10), "rot": (6 + 10 * k, -80, 0)}, "spine": (8, 6, 0),
           "board": {"yaw": -90, "pitch": 30 * k}, "gaze": (2.0, -8.0, 0.25), **_board_arms()}
    neutral, heel, toe = (full("boarder", p) for p in (neutral, heel, toe))
    keys = []
    for t0, apex, look in ((0.0, heel, 10.0), (0.5, toe, -10.0)):
        keys += [(t0, merged(neutral, {"gaze": (2.0, look, 0.2)})), (t0 + 0.10, _lift(between(neutral, apex, 0.25), 0.02)),
                 (t0 + 0.30, apex), (t0 + 0.42, _lift(between(neutral, apex, 0.45), -0.02))]
    return spline(keys)
