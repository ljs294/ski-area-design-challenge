"""SLE ground snow gun on its tripod, after the owner's photos of a real unit and the reference's loose gun.

The gun: a 2 in barrel with a black nucleator cap (octagonal nose), a square flange into the black valve body with
the fan head on top (the stick gun's fan design at this size, its 12-nozzle face forward and up), the air rod back
to the aluminium hose block, whose valve (dial and maroon paddle), side and rear couplers and pressure gauge sit on
it. A tapered plate on the valve body's side hangs the gun on the tripod's pivot.

The tripod: an aluminium inverted-U arch across the gun, whose apex tube is the pivot and carries a quadrant disc; a
crossbar between the arch's legs; a rear leg welded to the disc; a T-pin in the disc's arc slot. The gun is one
hinged moving part on the pivot (its elevation); turning it is the placement's yaw.

Dimensions: sle_guns.json "groundGun" (gun frame, mm: x toward the nozzle, y across, z up, origin on the barrel's
axis at the valve body's middle) and "tripod" (the asset's frame: origin on the snow under the pivot, u the way the
gun fires, v along the pivot, w up).
"""
import math

from mathutils import Vector

from liftkit import prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style
from liftkit.prims import U, V, W

from sle_stick_gun import fan

KIND = "snowgun"
LODS = 4
SPEC = "sle_guns.json"


def m(x):
    return x / 1000.0


def styles(lod):
    return {
        "alu": Style("aluminium", snow=0.5),
        "black": Style("paint_dark", snow=0.3),
        "dial": Style("paint_grey", snow=0.2),
        "paddle": Style("valve_maroon", snow=0.2),
        "face": Style("white"),
        "rod": Style("machined"),
    }


class Gun:
    """The gun's frame (mm) to the asset's (m): raised by the elevation about the mount's pivot, which sits on the
    tripod's pivot; y offset so the tripod's disc lies on v = 0."""

    def __init__(self, gg, tp):
        e = math.radians(tp["elevationDeg"])
        self.c, self.s = math.cos(e), math.sin(e)
        self.px, self.pz = gg["mount"]["pivot"]
        self.h, self.dv = tp["pivotHeight"], tp["gunOffsetV"]
        self.X = Vector((self.c, 0.0, self.s))
        self.Z = Vector((-self.s, 0.0, self.c))

    def p(self, x, y=0.0, z=0.0):
        dx, dz = x - self.px, z - self.pz
        return Vector((m(dx * self.c - dz * self.s), m(y + self.dv), m(dx * self.s + dz * self.c + self.h)))

    def prism(self, mb, pts, y0, y1, st):
        """An (x, z) outline extruded along y."""
        o = self.p(0.0, y0, 0.0)
        prims.prism(mb, [(m(x), m(z)) for x, z in pts], o, self.X, self.Z, V, m(y1 - y0), st)

    def box(self, mb, lo, hi, st):
        c = self.p(*[(a + b) / 2 for a, b in zip(lo, hi)])
        prims.obox(mb, c, (self.X, V, self.Z), tuple(m(b - a) / 2 for a, b in zip(lo, hi)), st)

    def cyl(self, mb, x0, x1, r, sides, st, caps=(True, True), y=0.0, z=0.0):
        prims.cylinder(mb, self.p(x0, y, z), self.p(x1, y, z), m(r), sides, st, caps=caps)


def arch_path(tp, arc_steps):
    """The inverted U in the v-w plane: straight legs from the feet, tangent into a circular top whose apex is the
    pivot. arc_steps: points on the arc (odd, so the apex is one)."""
    ar, h = tp["arch"], tp["pivotHeight"]
    R, fv = ar["radius"], ar["footV"]
    cw = h - R                                     # the arc's centre height
    # the tangent angle from the apex: fv sin(a) - cw cos(a) = R (the leg from the foot touches the circle)
    k = math.hypot(fv, cw)
    a = math.asin(R / k) + math.atan2(cw, fv)
    pts = [Vector((0.0, -m(fv), 0.0))]
    for i in range(arc_steps):
        t = -a + 2 * a * i / (arc_steps - 1)
        pts.append(Vector((0.0, m(R * math.sin(t)), m(cw + R * math.cos(t)))))
    pts.append(Vector((0.0, m(fv), 0.0)))
    return pts, a


def build(spec, lod, stage):
    gg, tp = spec["groundGun"], spec["tripod"]
    st = styles(lod)
    a = Asset("sle_ground_gun")
    body = MeshBuilder()
    G = Gun(gg, tp)
    H = tp["pivotHeight"]

    # -- the tripod (static) ---------------------------------------------------------------------------------------
    ar = tp["arch"]
    sides = {0: 6, 1: 4, 2: 3}.get(lod)
    if sides:
        pts, _ = arch_path(tp, 5 if lod == 0 else 3)
        if lod == 2:   # the legs only, straight to the apex
            pts = [pts[0], Vector((0.0, 0.0, m(H))), pts[-1]]
        prims.tube_path(body, pts, m(ar["dia"]) / 2, sides, st["alu"], caps=False)
        rl = tp["rearLeg"]
        prims.cylinder(body, tuple(m(x) for x in rl["top"]), tuple(m(x) for x in rl["foot"]), m(rl["dia"]) / 2, sides,
                       st["alu"], caps=(False, False))
    if lod <= 1:
        cb = tp["crossbar"]
        tangent = pts[1]   # where the left leg meets the arc; the leg runs straight from its foot to here
        cv = m(ar["footV"]) + (abs(tangent.y) - m(ar["footV"])) * m(cb["w"]) / tangent.z
        prims.cylinder(body, (0.0, -cv, m(cb["w"])), (0.0, cv, m(cb["w"])), m(cb["dia"]) / 2, sides, st["alu"],
                       caps=(False, False))
        dk = tp["disc"]
        prims.cylinder(body, (0.0, m(dk["v"][0]), m(H)), (0.0, m(dk["v"][1]), m(H)), m(dk["dia"]) / 2, 12 if lod == 0 else 8,
                       st["alu"])
    if lod == 0:   # the T-pin through the disc's slot
        tpn = tp["tPin"]
        pu, pw = tpn["at"]
        prims.cylinder(body, (m(pu), m(tpn["v"][0]), m(H + pw)), (m(pu), m(tpn["v"][1]), m(H + pw)), m(tpn["dia"]) / 2, 6,
                       st["rod"])
        half = m(tpn["bar"]["length"]) / 2
        prims.cylinder(body, (m(pu) - half, m(tpn["v"][1]), m(H + pw)), (m(pu) + half, m(tpn["v"][1]), m(H + pw)),
                       m(tpn["bar"]["dia"]) / 2, 6, st["rod"])

    # -- the gun: one moving part hinged on the pivot ------------------------------------------------------------------
    gun = MeshBuilder()
    br, cp = gg["barrel"], gg["cap"]
    bd, rr = gg["body"], gg["rear"]
    if lod <= 1:
        n = 8 if lod == 0 else 6
        G.cyl(gun, br["from"][0], br["to"][0], br["dia"] / 2, n, st["alu"], caps=(False, False))
        # the nucleator cap: a black cylinder with an octagonal nose
        if lod == 0:
            G.cyl(gun, cp["from"][0], cp["noseFrom"], cp["dia"] / 2, 8, st["black"], caps=(True, False))
            prims.cylinder(gun, G.p(cp["noseFrom"]), G.p(cp["to"][0]), m(cp["dia"]) / 2, 8, st["black"],
                           r1=m(cp["noseDia"]) / 2, caps=(False, True))
        else:
            G.cyl(gun, cp["from"][0], cp["to"][0], cp["dia"] / 2, 6, st["black"], caps=(False, True))
        G.box(gun, (bd["x"][0], bd["y"][0], bd["z"][0]), (bd["x"][1], bd["y"][1], bd["z"][1]), st["black"])
        fan(gun, gg["fan"], G.p, st["alu"], arc=4 if lod == 0 else 1)
        G.box(gun, (rr["x"][0], rr["y"][0], rr["z"][0]), (rr["x"][1], rr["y"][1], rr["z"][1]), st["alu"])
        mt = gg["mount"]
        if lod == 0:
            G.prism(gun, mt["outline"], mt["y"][0], mt["y"][1], st["alu"])
            fl = gg["flange"]
            G.box(gun, (fl["x"][0], fl["y"][0], fl["z"][0]), (fl["x"][1], fl["y"][1], fl["z"][1]), st["alu"])
            rd = gg["rod"]
            prims.tube_path(gun, [G.p(x, 0.0, z) for x, z in rd["path"]], m(rd["dia"]) / 2, 4, st["alu"], caps=False)
            # the valve: dial, boss and the maroon paddle across it
            vl = gg["valve"]
            vx, vz = vl["at"]
            prims.cylinder(gun, G.p(vx, vl["dial"]["y"][0], vz), G.p(vx, vl["dial"]["y"][1], vz), m(vl["dial"]["dia"]) / 2, 10,
                           st["dial"], caps=(False, True))
            prims.cylinder(gun, G.p(vx, vl["boss"]["y"][0], vz), G.p(vx, vl["boss"]["y"][1], vz), m(vl["boss"]["dia"]) / 2, 6,
                           st["black"], caps=(False, False))
            pd = vl["paddle"]
            d = math.radians(pd["deg"])
            hl = pd["length"] / 2
            y_mid = (pd["y"][0] + pd["y"][1]) / 2
            prims.beam(gun, G.p(vx - hl * math.cos(d), y_mid, vz - hl * math.sin(d)),
                       G.p(vx + hl * math.cos(d), y_mid, vz + hl * math.sin(d)), m(abs(pd["y"][1] - pd["y"][0])), m(pd["width"]),
                       st["paddle"], up=V)
            # the rear: a hex fitting and the black coupler
            hx, cu = gg["hex"], gg["coupler"]
            prims.cylinder(gun, G.p(hx["x"][1]), G.p(hx["x"][0]), m(hx["r"]), 6, st["alu"], caps=(False, False), phase=0.0)
            G.cyl(gun, cu["x"][1], cu["x"][0], cu["r"], 8, st["black"], caps=(False, True))
            # the side port: a nipple bending into a coupler pointing out and back
            sd = gg["side"]
            prims.tube_path(gun, [G.p(*q) for q in sd["path"]], m(sd["nippleDia"]) / 2, 6, st["black"], caps=False)
            c2 = sd["coupler"]
            prims.cylinder(gun, G.p(*c2["from"]), G.p(*c2["to"]), m(c2["dia"]) / 2, 6, st["black"], caps=(False, True))
            # the pressure gauge on its stem, its white face outward
            ga = gg["gauge"]
            gx, gz = ga["at"]
            prims.cylinder(gun, G.p(gx, ga["stem"]["y"][0], gz), G.p(gx, ga["stem"]["y"][1], gz), m(ga["stem"]["dia"]) / 2, 4,
                           st["rod"], caps=(False, False))
            prims.cylinder(gun, G.p(gx, ga["dial"]["y"][0], gz), G.p(gx, ga["dial"]["y"][1], gz), m(ga["dial"]["dia"]) / 2, 8,
                           st["black"], caps=(True, False))
            face_y = ga["dial"]["y"][1]
            out = 1.0 if face_y > ga["dial"]["y"][0] else -1.0   # the face stands just proud of the dial's outer end
            prims.cylinder(gun, G.p(gx, face_y, gz), G.p(gx, face_y + out, gz), m(ga["dial"]["dia"]) / 2 - 0.005, 8,
                           st["face"], caps=(False, True))
        else:   # LOD1: the mount plate as its outline's hull, the rear coupler as a stub
            ol = mt["outline"]
            G.prism(gun, [ol[0], ol[1], ol[3], ol[5], ol[7]], mt["y"][0], mt["y"][1], st["alu"])
    else:   # far: the gun as one box from the hose block to the cap
        target = gun if lod == 2 else body
        G.box(target, (rr["x"][0], -40.0, bd["z"][0]), (cp["to"][0], 40.0, 110.0), st["alu"])

    if lod <= 2:
        a.parts["gun"] = gun
        a.pivots["gun"] = {"pos": (0.0, 0.0, m(H)), "axis": (0.0, 1.0, 0.0), "hinge": True}

    a.body = body
    a.sockets = {
        "base": (0.0, 0.0, 0.0),
        "nozzle": tuple(G.p(cp["to"][0])),
        "hose_side": tuple(G.p(*gg["side"]["coupler"]["to"])),
        "hose_rear": tuple(G.p(gg["coupler"]["x"][0])),
    }
    a.dims.update({"pivotHeight": H, "elevationDeg": tp["elevationDeg"], "barrelAxisW": round(G.p(0.0).z * 1000, 1)})
    return a
