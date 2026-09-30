"""Chairworks quad chair, a recreation from the owner's reference model's dimensions (chairworks_chair.json, measured
locally): the hanger out of the grip and down its dogleg into a clamp on the top bar; the top bar bending into two
side frames that run down and back to a knee, then down and forward to the seat (bent tube: the knees and the top
bar's corners on a radius); the seat's frame of two rear rails and a looped rail at each end; a bench tilted up to the
front and a low backrest. No safety bar yet (the owner).

One chair body, two grips (variant): "detach", the reference's detachable grip part by part (the jaw block; the
carriage with its arched top plate, holed webs, axle bar and two running wheels; two coil springs on guide rods
between seat discs; the lever and its disc; the dogleg arm; the roller), and "fixed", the fixed grip every
fixed-grip chair carries (fixed_grip.json), its hanger coming straight out of the grip's boot.

The origin is the grip on the rope. Chair frame (the lift frame's axes, reused): u = direction of travel, v =
outboard (away from the bullwheel), w = up.
"""
import math

from mathutils import Vector

from liftkit import parts, prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

KIND = "chair"
LODS = 3
SPEC = "chairworks_chair.json"
VARIANTS = ("detach", "fixed")


def m(x):
    return x / 1000.0


def mv(p):
    return tuple(m(x) for x in p)


def plate(mb, a, b, v0, v1, thick, style, below=True):
    """A plate whose face runs from a to b ((u, w) mm) across v0..v1, thick to one side of the face (below it, or
    behind it for a leaning plate)."""
    (ua, wa), (ub, wb) = a, b
    along = Vector((ub - ua, 0.0, wb - wa))
    length = along.length
    along.normalize()
    normal = Vector((-along.z, 0.0, along.x))     # up for a plate running forward, forward for a leaning one
    if normal.z < 0 or (abs(normal.z) < 1e-6 and normal.x < 0):
        normal = -normal
    mid = Vector(((ua + ub) / 2, (v0 + v1) / 2, (wa + wb) / 2))
    c = mid - normal * (thick / 2) if below else mid - normal * (thick / 2)
    prims.obox(mb, mv(c), (along, V, normal), (m(length) / 2, m(v1 - v0) / 2, m(thick) / 2), style)


def reducer(mb, ring, start, r_small, style):
    """A cast reducer from a small round end (start = (v, w) mm on the u = 0 plane, radius r_small mm, facing along
    v) to a tube's first ring (tube_path's rings[0]: its vertices, so the shading runs on without a seam)."""
    idx, y, z = ring
    n = len(idx)
    c = Vector((0.0, m(start[0]), m(start[1])))
    zs = V.cross(y).normalized()
    small = mb.verts_lift([tuple(c + (y * math.cos(2 * math.pi * i / n) + zs * math.sin(2 * math.pi * i / n)) * m(r_small))
                           for i in range(n)])
    st = style.but(smooth=True)
    for i in range(n):
        i1 = (i + 1) % n
        a = 2 * math.pi * (i + 0.5) / n
        mb.face([small[i], small[i1], idx[i1], idx[i]], st, tuple(y * math.cos(a) + zs * math.sin(a)))


KNEE_STEPS, CORNER_STEPS = 3, 4   # arcs up close: the knees turn 43 degrees, the corners 90


def springs(g):
    """Each coil spring's centre and axis (lift frame, m) with its spec."""
    sp = g["springs"]
    ax = Vector((0.0, sp["axis"][0], sp["axis"][1])).normalized()
    cv, cw = sp["centre"]
    return [(Vector((m(u), m(cv), m(cw))), ax, sp) for u in sp["u"]]


def detach_grip(mb, g, st, rubber, dark, metal):
    """The detachable grip up close (gripDetach, mm): the jaw block; the carriage (the arched top plate, the two webs
    with their holes as dark discs on the outer faces, the axle bar) and its wheels on hubs; the coil springs, each a
    helix round its guide rod between two seat discs, tied by rods across the seats; the lever (lugs, cross block,
    the bent rod and its disc); the dogleg arm; the roller arm and the roller."""
    jw = g["jaw"]
    prims.prism(mb, [(m(v), m(w)) for v, w in jw["outline"]], Vector((m(jw["u"][0]), 0.0, 0.0)), V, W, U,
                m(jw["u"][1] - jw["u"][0]), st)
    # the carriage
    tp = g["topPlate"]
    top = [(m(u), m(w)) for u, w in tp["top"]]
    prims.prism(mb, top + [(u, w - m(tp["thick"])) for u, w in reversed(top)], Vector((0.0, m(tp["v"][0]), 0.0)), U, W, V,
                m(tp["v"][1] - tp["v"][0]), st)
    wb = g["webs"]
    web, hl = [(m(u), m(w)) for u, w in wb["outline"]], wb["holes"]
    for n, (v0, v1) in enumerate(wb["v"]):
        prims.plate(mb, web, Vector((0.0, m(v0), 0.0)), U, W, V, m(v1 - v0), st)
        v_out, out = (v0 - 1.0, -1.0) if n == 0 else (v1 + 1.0, 1.0)   # the web's outer face, where its holes show
        for u in hl["u"]:
            ring = prims.ring_points(Vector((m(u), m(v_out), m(hl["w"]))), V, m(hl["dia"]) / 2, 8)
            mb.face(mb.verts_lift([tuple(q) for q in ring]), dark, (0.0, out, 0.0))
    prims.box(mb, mv(g["axleBar"]["lo"]), mv(g["axleBar"]["hi"]), st)
    wh = g["wheels"]
    for u in wh["u"]:
        ty, hb = wh["tyre"], wh["hub"]
        prims.cylinder(mb, (m(u), m(ty["v"][0]), m(wh["w"])), (m(u), m(ty["v"][1]), m(wh["w"])), m(ty["dia"]) / 2, 12, rubber,
                       caps=(True, False))
        prims.cylinder(mb, (m(u), m(hb["v"][0]), m(wh["w"])), (m(u), m(hb["v"][1]), m(wh["w"])), m(hb["dia"]) / 2, 8, metal,
                       caps=(True, False))
    # the springs: a helix of wire round each guide rod, between its seats
    e1 = U
    for c, ax, sp in springs(g):
        e2 = ax.cross(e1).normalized()
        seg = 5
        half = sp["turns"] * sp["pitch"] / 2
        pts = []
        for q in range(sp["turns"] * seg + 1):
            th = 2 * math.pi * q / seg
            pts.append(tuple(c + ax * m(-half + sp["pitch"] * q / seg) + (e1 * math.cos(th) + e2 * math.sin(th)) * m(sp["coilDia"] / 2)))
        prims.tube_path(mb, pts, m(sp["wire"]) / 2, 3, st, caps=False)
        se, rd = sp["seats"], sp["rod"]
        for s0 in se["s"]:
            prims.cylinder(mb, tuple(c + ax * m(s0 - se["thick"] / 2)), tuple(c + ax * m(s0 + se["thick"] / 2)), m(se["dia"]) / 2, 8, st)
        prims.cylinder(mb, tuple(c + ax * m(rd["s"][0])), tuple(c + ax * m(rd["s"][1])), m(rd["dia"]) / 2, 6, metal,
                       caps=(False, False))
    sb = g["springBars"]
    for key in ("top", "bottom"):
        (v, w), d = sb[key]["at"], sb[key]["dia"]
        prims.cylinder(mb, (m(sb["u"][0]), m(v), m(w)), (m(sb["u"][1]), m(v), m(w)), m(d) / 2, 6, metal)
    # the lever
    lv = g["lever"]
    for u0, u1 in lv["lugs"]["u"]:
        prims.prism(mb, [(m(v), m(w)) for v, w in lv["lugs"]["outline"]], Vector((m(u0), 0.0, 0.0)), V, W, U, m(u1 - u0), st)
    prims.box(mb, mv(lv["block"]["lo"]), mv(lv["block"]["hi"]), st)
    prims.tube_path(mb, [(0.0, m(v), m(w)) for v, w in lv["rod"]["path"]], m(lv["rod"]["dia"]) / 2, 6, st, caps=False)
    dk = lv["disc"]
    prims.cylinder(mb, (0.0, m(dk["v"][0]), m(dk["w"])), (0.0, m(dk["v"][1]), m(dk["w"])), m(dk["dia"]) / 2, 12, st)
    # the dogleg arm to the hanger, the roller arm and the roller
    ar = g["arm"]
    prims.prism(mb, [(m(v), m(w)) for v, w in ar["outline"]], Vector((m(ar["u"][0]), 0.0, 0.0)), V, W, U,
                m(ar["u"][1] - ar["u"][0]), st)
    ra, ro = g["rollerArm"], g["roller"]
    prims.cylinder(mb, (0.0, m(ra["from"][0]), m(ra["from"][1])), (0.0, m(ra["to"][0]), m(ra["to"][1])), m(ra["dia"]) / 2, 6, st,
                   caps=(False, False))
    prims.cylinder(mb, (m(ro["u"]), m(ro["v"][0]), m(ro["w"])), (m(ro["u"]), m(ro["v"][1]), m(ro["w"])), m(ro["dia"]) / 2, 10, rubber)
    prims.cylinder(mb, (m(ro["u"]), m(ro["hub"]["v"][0]), m(ro["w"])), (m(ro["u"]), m(ro["hub"]["v"][1]), m(ro["w"])),
                   m(ro["hub"]["dia"]) / 2, 6, metal, caps=(False, True))


def build(spec, lod, stage, grip="detach"):
    ch = spec
    steel = Style("chair_steel", snow=0.4)
    seat = Style("seat_pad", snow=1.0)
    grip_st = Style("grip", snow=0.3)
    wheel_st = Style("rubber")
    a = Asset(f"chairworks_chair_{grip}")
    mb = MeshBuilder()
    tube = {k: m(d) / 2 for k, d in ch["tube"].items()}   # radii: the Sessellift chair's hanger, frame and seat rails
    r = tube["hanger"]
    hp = ch["hanger"]["path"]
    far = (0, 3, 4, 7, 8)                   # the hanger's points kept at LOD1
    if grip == "fixed":                     # out of the fixed grip's boot, straight down the dogleg
        hp = [ch["hanger"]["fixedTop"]] + hp[ch["hanger"]["fixedFrom"]:]
        far = (0, 1, len(hp) - 2, len(hp) - 1)
    sf, rr, el = ch["sideFrames"], ch["rearRails"], ch["endLoops"]
    vl, vr = sf["v"]
    if lod <= 1:
        n_main, n_seat = (6, 6) if lod == 0 else (4, 4)   # the hanger and frame as the Sessellift chair's hanger
        # the hanger: out of the grip, down the dogleg, back in through the clamp (up close, out of the fixed grip's
        # socket)
        pts = hp if lod == 0 else [hp[i] for i in far]
        if lod == 0 and grip == "fixed":
            pts = parts.hanger_from_socket(pts)
        reduce_from = None
        if lod == 0 and grip == "detach":   # the hanger narrows into the grip's arm through a cast reducer
            reduce_from, pts = (pts[0][0] - 3.0, pts[0][1]), pts[1:]
        hanger_pts = [(0.0, m(v), m(w)) for v, w in pts]
        # far out the detachable grip's arm is narrower than the hanger: close the hanger's ends there and in the clamp
        hanger_rings = prims.tube_path(mb, hanger_pts, r, n_main, steel, caps=grip == "detach" and lod > 0)
        if reduce_from is not None:
            ar = ch["gripDetach"]["arm"]["u"]
            reducer(mb, hanger_rings[0], reduce_from, (ar[1] - ar[0]) / 2, steel)
        # the frame: the left side frame up to the corner, the top bar across, the right side frame down
        path = sf["path"]                                   # (u, w): the top bar's corner, the knee, the lower rear rail
        left = [(m(u), m(vl), m(w)) for u, w in reversed(path)]
        right = [(m(u), m(vr), m(w)) for u, w in path]
        frame = left + right   # the left side up, the top bar across, the right side down: knees at 1 and 4
        if lod == 0:   # bent tube (the owner): the knees first, then the corners take what is left of the run between
            kr, cr = m(sf["kneeRadius"]), m(sf["cornerRadius"])
            frame = prims.fillet(frame, 4, kr, KNEE_STEPS)
            frame = prims.fillet(frame, 1, kr, KNEE_STEPS)
            frame = prims.fillet(frame, 3 + KNEE_STEPS, cr, CORNER_STEPS)
            frame = prims.fillet(frame, 2 + KNEE_STEPS, cr, CORNER_STEPS)
        prims.tube_path(mb, frame, tube["frame"], n_main, steel, caps=False)
        # the seat frame: two rear rails and a looped rail at each end
        for key in ("upper", "lower"):
            u, w = rr[key]
            prims.cylinder(mb, (m(u), m(rr["v"][0]), m(w)), (m(u), m(rr["v"][1]), m(w)), tube["seat"], n_seat, steel,
                           caps=(False, False))
        loop = el["path"] if lod == 0 else [el["path"][i] for i in (0, 1, 5, 6)]
        for v in el["v"]:
            prims.tube_path(mb, [(m(u), m(v), m(w)) for u, w in loop], tube["seat"], n_seat, steel, caps=False)
        prims.box(mb, mv(ch["clamp"]["lo"]), mv(ch["clamp"]["hi"]), steel)
        bk = ch["backrest"]
        plate(mb, bk["bottom"], bk["top"], bk["v"][0], bk["v"][1], bk["thick"], seat)
    else:   # far: the hanger, the top bar and the side frames as bars, the bench as a slab
        hd, fd = 2 * tube["hanger"], 2 * tube["frame"]
        prims.beam(mb, (0.0, m(hp[0][0]), m(hp[0][1])), (0.0, m(hp[-2][0]), m(hp[-2][1])), hd, hd, steel, skip_ends=True)
        top = sf["path"][0]
        prims.beam(mb, (m(top[0]), m(vl), m(top[1])), (m(top[0]), m(vr), m(top[1])), fd, fd, steel, skip_ends=True)
        for v in (vl, vr):
            prims.beam(mb, (m(top[0]), m(v), m(top[1])), (m(sf["path"][-1][0]), m(v), m(sf["path"][-1][1])), fd, fd,
                       steel, skip_ends=True)
    bn = ch["bench"]
    plate(mb, bn["back"], bn["front"], bn["v"][0], bn["v"][1], bn["thick"], seat)

    # -- the grip ---------------------------------------------------------------------------------------------------
    if grip == "detach":
        g = ch["gripDetach"]
        if lod == 0:
            detach_grip(mb, g, grip_st, wheel_st, Style("trim_dark"), Style("machined"))
        elif lod == 1:   # the carriage as one block under its arch, the arm, the wheels and the springs as bars
            tp = g["topPlate"]["top"]
            hull = [tp[0], (0.0, max(w for _, w in tp)), tp[-1], (189.0, -4.0), (-189.0, -4.0)]
            prims.prism(mb, [(m(u), m(w)) for u, w in hull], Vector((0.0, m(60.0), 0.0)), U, W, V, m(36.0), grip_st)
            ar = g["arm"]["outline"]
            arm = [ar[1], ar[2], (193.0, -45.0), ar[5], ar[6], ar[0]]
            prims.prism(mb, [(m(v), m(w)) for v, w in arm], Vector((m(g["arm"]["u"][0]), 0.0, 0.0)), V, W, U,
                        m(g["arm"]["u"][1] - g["arm"]["u"][0]), grip_st)
            wh = g["wheels"]
            for u in wh["u"]:
                ty = wh["tyre"]
                prims.cylinder(mb, (m(u), m(ty["v"][0]), m(wh["w"])), (m(u), m(ty["v"][1]), m(wh["w"])), m(ty["dia"]) / 2, 4,
                               wheel_st, caps=(True, False))
            for c, ax, sp in springs(g):
                s0, s1 = sp["seats"]["s"]
                prims.cylinder(mb, tuple(c + ax * m(s0)), tuple(c + ax * m(s1)), m(sp["coilDia"] + sp["wire"]) / 2, 4, grip_st,
                               caps=(False, False))
        else:
            prims.box(mb, (m(-277.0), m(-25.0), m(-68.0)), (m(277.0), m(380.0), m(99.0)), grip_st)
    else:   # the fixed grip every fixed-grip chair shares (fixed_grip.json)
        if lod <= 1:
            joint = parts.hanger_joint(hanger_rings, hanger_pts, r, steel) if lod == 0 else None
            parts.fixed_grip(mb, lod, steel, dark=Style("trim_dark"), metal=Style("machined"), hanger=joint)
        else:
            g = parts.fixed_grip_spec()
            prims.box(mb, (-m(g["tails"]["to"]) * 0.6, m(g["body"]["v"][0]), m(g["body"]["w"][0])),
                      (m(g["tails"]["to"]) * 0.6, m(g["bolt"]["v"][1]), m(g["body"]["w"][1])), grip_st)

    a.body = mb
    # seats: four along the bench, on its top surface at mid depth
    (ub, wb), (uf, wf) = bn["back"], bn["front"]
    um, wm = (ub + uf) / 2, (wb + wf) / 2
    width = bn["v"][1] - bn["v"][0]
    a.sockets = {"grip": (0.0, 0.0, 0.0)}
    for i in range(ch["seats"]):
        vs = bn["v"][0] + width * (i + 0.5) / ch["seats"]
        a.sockets[f"seat_{i + 1}"] = (m(um), m(vs), m(wm) + 0.03)
    a.dims.update({"grip": grip, "hangerDrop": round(-hp[-1][1]), "benchWidth": round(width),
                   "seatW": round(wm), "axisV": ch["axisV"]})
    return a
