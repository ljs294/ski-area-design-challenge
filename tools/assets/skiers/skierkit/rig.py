"""The armature, the skinned objects and the pose solver.

A pose is written at a high level (where the pelvis is, how the spine bends, where the skis or the board are, where
the hands are) and solved to one world rotation per bone: the legs and arms by two-bone IK, so the boots stay on
the skis or the board and the bone lengths never change.
"""
import math

import bpy
from mathutils import Matrix, Vector

from . import gear
from . import skeleton as sk
from .mesh import MAT, MATS, face_tone, rot

CLOTH_IDS = {MAT[x] for x in ("jacket", "block", "pants", "patch")}


def make_armature(name, kind, collection):
    arm = bpy.data.armatures.new(name)
    ob = bpy.data.objects.new(name, arm)
    collection.objects.link(ob)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode="EDIT")
    made = {}
    for n, h, t, p in sk.bones(kind):
        e = arm.edit_bones.new(n)
        e.head, e.tail = h, t
        e.roll = 0.0
        if p:
            e.parent = made[p]
        made[n] = e
    bpy.ops.object.mode_set(mode="OBJECT")
    for pb in ob.pose.bones:
        pb.rotation_mode = "QUATERNION"
    return ob


def make_object(name, mesh, materials, armature, collection):
    """A Blender object from a Mesh: flat shaded, one material slot per id, vertex groups. The colour layer "matid"
    carries the material id in red ((id + 0.5) / 16) and a keyed tone for the face in green (0.5 is no change; cloth
    varies by 8 percent, hard parts by 3): the painted facets of the concept art, with no texture."""
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(v) for v in mesh.verts], [], [idx for idx, _ in mesh.faces])
    for mname in MATS:
        me.materials.append(materials[mname])
    col = me.color_attributes.new("matid", "FLOAT_COLOR", "CORNER")
    for poly, (idx, mat) in zip(me.polygons, mesh.faces):
        poly.material_index = mat
        centre = sum((mesh.verts[i] for i in idx), Vector()) / len(idx)
        tone = 0.5 + face_tone(mat + 1, centre) * (0.08 if mat in CLOTH_IDS else 0.03)
        if mat == MAT["lens"]:   # two tones: darker at the brow, lighter toward the nose
            tone = 0.5 + max(-0.25, min(0.25, (1.722 - centre.z) * 12.0))
        for li in poly.loop_indices:
            col.data[li].color = ((mat + 0.5) / 16.0, tone, 0.0, 1.0)
    me.update()
    ob = bpy.data.objects.new(name, me)
    collection.objects.link(ob)
    groups = {}
    for i, w in enumerate(mesh.weights):
        for b, x in w.items():
            if b not in groups:
                groups[b] = ob.vertex_groups.new(name=b)
            groups[b].add([i], x, "REPLACE")
    if armature is not None:
        ob.parent = armature
        mod = ob.modifiers.new("Armature", "ARMATURE")
        mod.object = armature
    return ob


# ---------------------------------------------------------------------------------------------------------------
def _frame(d, f):
    """A rotation matrix whose Z column is d and whose Y column leans toward f."""
    d = d.normalized()
    y = f - d * f.dot(d)
    if y.length < 1e-6:
        y = Vector((0, -1, 0)) - d * Vector((0, -1, 0)).dot(d)
    y.normalize()
    x = y.cross(d)
    return Matrix((x, y, d)).transposed()


def _two_bone(p0, target, a, b, hint):
    """Returns (middle joint, end, bend direction, reach error)."""
    v = target - p0
    c = v.length
    cc = min(max(c, abs(a - b) + 1e-4), a + b - 1e-4)
    n = v / c if c > 1e-9 else Vector((0, 0, -1))
    m = hint - n * hint.dot(n)
    if m.length < 1e-6:
        m = Vector((0, -1, 0)) - n * Vector((0, -1, 0)).dot(n)
    m.normalize()
    cos_a = (a * a + cc * cc - b * b) / (2 * a * cc)
    sin_a = math.sqrt(max(0.0, 1 - cos_a * cos_a))
    mid = p0 + n * (a * cos_a) + m * (a * sin_a)
    return mid, p0 + n * cc, m, abs(c - cc)


def _plank(p, half_width, tilt):
    """World rotation and position of a ski or a board from {"pos", "yaw", "edge", "pitch"}. A plank on edge rests
    on that edge, so it is lifted by half its width times the sine of its tilt (`tilt` names the field)."""
    w = rot(p.get("pitch", 0.0), p.get("yaw", 0.0), 0.0) @ Matrix.Rotation(math.radians(p.get("edge", 0.0)), 3, "Y")
    lift = half_width * abs(math.sin(math.radians(p.get(tilt, 0.0))))
    return w, Vector(p.get("pos", (0, 0, 0))) + Vector((0, 0, lift))


def solve(kind, pose):
    """Returns ({bone: world rotation 3x3}, {bone: world offset of its head}, worst reach error in metres)."""
    R = sk.rest(kind)
    W, head = {"root": Matrix.Identity(3)}, {"root": Vector((0, 0, 0))}
    err = 0.0

    def fk(b, w):
        h, _, p = R[b]
        W[b] = w
        head[b] = head[p] + W[p] @ (h - R[p][0])

    pel = pose.get("pelvis", {})
    W["pelvis"] = rot(*pel.get("rot", (0, 0, 0)))
    head["pelvis"] = R["pelvis"][0] + Vector(pel.get("pos", (0, 0, 0)))
    sp = pose.get("spine", (0, 0, 0))
    q = rot(sp[0] / 3, sp[1] / 3, sp[2] / 3)
    for b in ("spine_01", "spine_02", "spine_03"):
        fk(b, W[R[b][2]] @ q)
    gaze = pose.get("gaze")
    if gaze:    # (pitch, yaw, follow): the head held level, looking that way, whatever the body does under it;
        #         follow 0..1 lets it tip a little with the chest
        chest = W["spine_03"].to_quaternion()
        qh = rot(gaze[0], gaze[1], 0.0).to_quaternion().slerp(chest, max(0.0, min(1.0, gaze[2])))
        fk("neck", chest.slerp(qh, 0.5).to_matrix())
        fk("head", qh.to_matrix())
    else:
        fk("neck", W["spine_03"] @ rot(*pose.get("neck", (0, 0, 0))))
        fk("head", W["neck"] @ rot(*pose.get("head", (0, 0, 0))))

    # the skis or the board, and the boots fixed on them
    feet = {}
    if kind == "skier":
        for sx, s in (("_l", 1), ("_r", -1)):
            w, pos = _plank(pose.get("ski" + sx, {"pos": (sk.HIP_X * s, 0, 0)}), 0.040, "edge")
            feet[sx] = (w, pos + w @ Vector((0, 0, sk.ANKLE_Z)), w, pos)
    else:
        spec = pose.get("board", {})
        wb, pb = _plank(spec, gear.BOARD_W / 2, "pitch")
        if "lead" in spec:   # the board hangs from the leading foot: `lead` is that ankle
            wl = wb @ Matrix.Rotation(math.radians(gear.BIND_ANGLE["_l"]), 3, "Z")
            pb = Vector(spec["lead"]) - wb @ Vector((sk.STANCE / 2, 0, 0)) - wl @ Vector((0, 0, sk.ANKLE_Z))
        W["board"], head["board"] = wb, pb
        for sx, s in (("_l", 1), ("_r", -1)):
            free = pose.get("foot" + sx)     # a foot out of its binding: {"pos": the ankle, "rot": (pitch, yaw, roll)}
            if free:
                feet[sx] = (rot(*free.get("rot", (0, 0, 0))), Vector(free["pos"]), None, None)
                continue
            w = wb @ Matrix.Rotation(math.radians(gear.BIND_ANGLE[sx]), 3, "Z")
            feet[sx] = (w, pb + wb @ Vector((s * sk.STANCE / 2, 0, 0)) + w @ Vector((0, 0, sk.ANKLE_Z)), None, None)

    for sx, s in (("_l", 1), ("_r", -1)):
        # the leg
        th, ca, ft = "thigh" + sx, "calf" + sx, "foot" + sx
        hip = head["pelvis"] + W["pelvis"] @ (R[th][0] - R["pelvis"][0])
        wf, ankle, wski, pski = feet[sx]
        a, b = (R[th][1] - R[th][0]).length, (R[ca][1] - R[ca][0]).length
        fwd = (W["pelvis"] @ Vector((0, -1, 0)) + wf @ Vector((0, -1, 0))).normalized()
        hint = fwd + Vector(pose.get("knee" + sx, (0, 0, 0)))
        knee, end, m, e = _two_bone(hip, ankle, a, b, hint)
        err = max(err, e)
        rest_leg = _frame(Vector((0, 0, -1)), Vector((0, -1, 0)))
        W[th], head[th] = _frame(knee - hip, m) @ rest_leg.inverted(), hip
        W[ca], head[ca] = _frame(end - knee, m) @ rest_leg.inverted(), knee
        W[ft], head[ft] = wf, end
        if wski is not None:
            W["ski" + sx], head["ski" + sx] = wski, end - wf @ Vector((0, 0, sk.ANKLE_Z))
        # the arm
        cl, up, lo, ha = "clavicle" + sx, "upperarm" + sx, "lowerarm" + sx, "hand" + sx
        fk(cl, W["spine_03"])
        sh = head[cl] + W[cl] @ (R[up][0] - R[cl][0])
        spec = pose.get("arm" + sx, {})
        d0 = (R[up][1] - R[up][0]).normalized()
        a, b = (R[up][1] - R[up][0]).length, (R[lo][1] - R[lo][0]).length
        if "hand" in spec:      # a wrist target, given beside the chest (in the chest's own axes) or in the world
            t = Vector(spec["hand"])
            target = sh + W["spine_03"] @ t if spec.get("chest", True) else t
        else:
            target = sh + W["spine_03"] @ (d0 * (a + b - 0.01))
        hint = W["spine_03"] @ Vector(spec.get("elbow", (0.3 * s, 1.0, -0.4)))
        elbow, end, m, _ = _two_bone(sh, target, a, b, hint)
        rest_arm = _frame(d0, Vector((0, 1, 0)))
        W[up], head[up] = _frame(elbow - sh, m) @ rest_arm.inverted(), sh
        W[lo], head[lo] = _frame(end - elbow, m) @ rest_arm.inverted(), elbow
        W[ha], head[ha] = W[lo] @ rot(*spec.get("wrist", (0, 0, 0))), end
        if kind == "skier":
            # The fist is closed on the grip, so the hand turns with the pole: the grip points where the pose aims
            # the tip (in the world), and the wrist side of the fist stays toward the forearm.
            pl = "pole" + sx
            aim = Vector(spec.get("pole", (0.0, 0.25, -1.0))).normalized()
            reach = sk.POLE - 0.06
            for _ in range(3):   # a tip cannot go under the snow: raise the aim until it rests on it
                W[ha] = _frame(aim, end - elbow) @ _frame(Vector((0, 0, -1)), d0).inverted()
                fk(pl, W[ha])
                need = (0.02 - head[pl].z) / reach
                if aim.z >= need - 1e-6 or need >= 0.999:
                    break
                flat = Vector((aim.x, aim.y, 0.0))
                flat = flat.normalized() if flat.length > 1e-6 else Vector((0, 1, 0))
                aim = flat * math.sqrt(1.0 - need * need) + Vector((0, 0, need))
            W[pl] = W[ha]
    return W, head, err


def diagnose(kind, pose):
    """Numbers an audit can check: the reach error (mm), each knee and elbow angle (180 = straight) and, for a
    skier, the angle between each pole and its forearm (a wrist cannot hold a pole along the arm or folded back)."""
    W, head, err = solve(kind, pose)
    out = {"reach_mm": round(err * 1000, 1)}

    def angle(a, b, c):
        return round(math.degrees((head[a] - head[b]).angle(head[c] - head[b])), 0)
    for sx in ("_l", "_r"):
        out["knee" + sx] = angle("thigh" + sx, "calf" + sx, "foot" + sx)
        shin = head["calf" + sx] - head["foot" + sx]          # a boot lets the shin lean about 30 degrees, no more
        out["shin_lean" + sx] = round(math.degrees(shin.angle(W["foot" + sx] @ Vector((0, 0, 1)))), 0)
        out["elbow" + sx] = angle("upperarm" + sx, "lowerarm" + sx, "hand" + sx)
        if kind == "skier":
            fore = head["hand" + sx] - head["lowerarm" + sx]
            out["pole_to_forearm" + sx] = round(math.degrees(fore.angle(W["pole" + sx] @ Vector((0, 0, -1)))), 0)
            tip = head["pole" + sx] + W["pole" + sx] @ Vector((0, 0, 0.06 - sk.POLE))
            out["pole_tip_z" + sx] = round(tip.z, 2)
    return out


def apply(armature, kind, pose, frame=None):
    """Poses the armature; keys every bone at `frame` when given. Returns the reach error (m)."""
    R = sk.rest(kind)
    W, head, err = solve(kind, pose)
    for pb in armature.pose.bones:
        b = pb.name
        rb = armature.data.bones[b].matrix_local.to_3x3()
        p = R[b][2]
        wp = W[p] if p else Matrix.Identity(3)
        pb.rotation_quaternion = (rb.inverted() @ wp.inverted() @ W[b] @ rb).to_quaternion()
        if p:
            want = head[b] - (head[p] + wp @ (R[b][0] - R[p][0]))    # only the pelvis and the board ever move
            pb.location = rb.inverted() @ wp.inverted() @ want
        if frame is not None:
            pb.keyframe_insert("rotation_quaternion", frame=frame)
            pb.keyframe_insert("location", frame=frame)
    return err
