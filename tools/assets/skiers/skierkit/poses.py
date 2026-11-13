"""Named still poses (see rig.solve for the fields). Angles in degrees, positions in metres.

Positive pitch leans forward, positive yaw turns to the figure's left, positive roll leans to the figure's left.
A hand target is the wrist, measured from the shoulder in the chest's own axes.
"""
from . import clips, skeleton as sk

FREE_ANKLE = sk.ANKLE_Z - 0.036      # the ankle of a boot standing on the snow, out of its binding

SKIER = {
    # waiting: elbows bent, fists at waist height, the poles planted just ahead of the boots
    "stand": {
        "pelvis": {"pos": (0, 0.01, -0.02), "rot": (2, 0, 0)},
        "ski_l": {"pos": (0.14, 0, 0)}, "ski_r": {"pos": (-0.14, 0, 0)},
        "arm_l": {"hand": (0.10, -0.20, -0.30), "pole": (0.04, -0.10, -1)},
        "arm_r": {"hand": (-0.10, -0.20, -0.30), "pole": (-0.04, -0.10, -1)},
        "gaze": (0, 0, 0.3),
    },
    # waiting, leaning on both poles planted together in front, the weight on the left leg, looking to the right
    "lean": {
        "pelvis": {"pos": (0.05, 0.02, -0.03), "rot": (5, -6, 3)}, "spine": (9, 4, -3),
        "ski_l": {"pos": (0.13, 0, 0)}, "ski_r": {"pos": (-0.15, -0.05, 0)},
        "arm_l": {"hand": (-0.03, -0.30, -0.25), "pole": (0.02, -0.22, -0.97)},
        "arm_r": {"hand": (0.03, -0.30, -0.25), "pole": (-0.02, -0.22, -0.97)},
        "gaze": (0, -25, 0.3),
    },
    # waiting, relaxed: arms down, the poles trailing behind, the hips to one side, looking to the left
    "rest": {
        "pelvis": {"pos": (-0.04, 0.01, -0.02), "rot": (2, 8, -3)}, "spine": (4, 0, 2),
        "ski_l": {"pos": (0.15, -0.03, 0)}, "ski_r": {"pos": (-0.13, 0, 0)},
        "arm_l": {"hand": (0.07, -0.08, -0.43), "pole": (0.05, 0.45, -0.89)},
        "arm_r": {"hand": (-0.07, -0.08, -0.43), "pole": (-0.05, 0.45, -0.89)},
        "gaze": (0, 22, 0.3),
    },
    # the apex of an expert's carved turn to the figure's left
    "carve": clips.skier_turn("expert")(0.30),
    # on a chair: the root is on the seat under the hips; the skis hang tips up, the poles lie level along the thighs
    "seated": {
        "pelvis": {"pos": (0, 0.06, -0.845), "rot": (-10, 0, 0)},
        "spine": (10, 0, 0), "gaze": (2, 0, 0.3),
        "ski_l": {"pos": (0.12, -0.40, -0.40), "pitch": -12}, "ski_r": {"pos": (-0.12, -0.40, -0.40), "pitch": -12},
        "arm_l": {"hand": (-0.06, -0.20, -0.28), "elbow": (0.25, 0.6, -0.8), "pole": (0.04, -0.99, -0.03)},
        "arm_r": {"hand": (0.06, -0.20, -0.28), "elbow": (-0.25, 0.6, -0.8), "pole": (-0.04, -0.99, -0.03)},
    },
}

BOARDER = {
    # standing on the flat, both feet in, arms down
    "stand": {
        "pelvis": {"pos": (0, 0.0, -0.06), "rot": (0, 0, 0)},
        "board": {"pos": (0, 0, 0)},
        "arm_l": {"hand": (0.05, -0.05, -0.47)}, "arm_r": {"hand": (-0.05, -0.05, -0.47)},
        "gaze": (0, 0, 0.3),
    },
    # in a lift line: the board points along the line with only the leading foot strapped in; the rear foot stands
    # on the snow beside it
    "queue": {
        "pelvis": {"pos": (-0.04, 0.02, -0.06), "rot": (2, -35, 0)}, "spine": (4, 10, 0),
        "board": {"pos": (0.06, 0.16, 0), "yaw": -90},
        "foot_r": {"pos": (-0.20, 0.10, FREE_ANKLE), "rot": (0, -40, 0)},
        "arm_l": {"hand": (0.05, -0.05, -0.47)}, "arm_r": {"hand": (-0.05, -0.05, -0.47)},
        "gaze": (0, 0, 0.3),
    },
    # the apex of a toe-side carve, riding toward -Y
    "carve": clips.boarder_turn()(0.80),
    # on a chair: the board hangs from the leading (left) foot, its tail low; the rear foot is out of its binding
    "seated": {
        "pelvis": {"pos": (0, 0.06, -0.845), "rot": (-10, 0, 0)},
        "spine": (10, 0, 0), "gaze": (2, 0, 0.3),
        "board": {"lead": (0.13, -0.42, -0.30), "yaw": -35, "edge": -10, "pitch": -4},
        "foot_r": {"pos": (-0.12, -0.44, -0.29), "rot": (-8, 4, 0)},
        "arm_l": {"hand": (-0.03, -0.26, -0.40), "elbow": (0.25, 0.6, -0.8)},
        "arm_r": {"hand": (0.03, -0.26, -0.40), "elbow": (-0.25, 0.6, -0.8)},
    },
}

ALL = {"skier": SKIER, "boarder": BOARDER}
