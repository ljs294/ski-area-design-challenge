"""The garage's fuel pad (task 4.50 phase 5; owner 2026-11-24: "a fuel tank model with it with a little dispenser"; round
3: "rotate the fuel module and put it as close to the building as possible realistically so a groomer can pull up to
it"): a concrete pad with a protected double-wall diesel tank on two steel saddles, a small dispenser on a yellow
curbed island between the tank and the fuelling lane, bollards round the tank's ends and the island, and a pole light
over the island. metal_building builds it as the `fuel` part (the game can leave it out where a site has no room).

The spec (garage.json `fuel`) is in the pad's own frame, millimetres: x along the tank, y across it toward the lane
(+y), the tank's centre at the origin; `origin` puts that centre in the building frame and `heading` (0, 90, 180, 270
degrees, anticlockwise from +u) turns x. LOD0 everything (the tank's end bands, manway, fill box, vent and gauge, the
pipe to the dispenser, its hose); LOD1 the tank, saddles, vent, island, dispenser and bollards; LOD2 the pad, the
tank, the dispenser as one box and the pole; LOD3 the tank as a box. Returns the pad's centre, the pole light's record,
the sockets (`fuel`: where a machine stands to fuel; `fuel_fill`: the tank's fill box, for the delivery truck) and dims
(with the tank's and the fuelling groomer's boxes in the building frame, for the garage's fit check).
"""
from mathutils import Vector

from liftkit import prims
from liftkit.frame import mm

from . import fixtures as fx

GALLON_M3 = 0.003785411784
TURN = {0: (1, 0), 90: (0, 1), 180: (-1, 0), 270: (0, -1)}


class Frame:
    """The pad's frame: local metres (x along the tank, y toward the lane, z up) to the building frame."""

    def __init__(self, spec):
        self.ou, self.ov = (mm(x) for x in spec["origin"])
        self.c, self.s = TURN[spec.get("heading", 0) % 360]

    def p(self, x, y, z=0.0):
        return (self.ou + self.c * x - self.s * y, self.ov + self.s * x + self.c * y, z)

    def vec(self, x, y, z=0.0):
        return (self.c * x - self.s * y, self.s * x + self.c * y, z)

    def box(self, lo, hi):
        a, b = self.p(*lo), self.p(*hi)
        return tuple(map(min, a, b)), tuple(map(max, a, b))

    def skip(self, keys):
        """Box face keys ("-1": the face toward local -y) in the building's axes."""
        out = []
        for k in keys:
            if k[1] == "2":
                out.append(k)
                continue
            sign = 1 if k[0] == "+" else -1
            d = self.vec(sign, 0) if k[1] == "0" else self.vec(0, sign)
            axis = 0 if abs(d[0]) > 0.5 else 1
            out.append(("+" if d[axis] > 0 else "-") + str(axis))
        return tuple(out)


def build(mb, spec, st, lod, a):
    F = Frame(spec)
    pd, tk, isl, dp = spec["pad"], spec["tank"], spec["island"], spec["dispenser"]
    x0, x1 = (mm(x) for x in pd["x"])
    y0, y1 = (mm(x) for x in pd["y"])
    top = mm(pd["top"])
    r, half = mm(tk["dia"]) / 2, mm(tk["length"]) / 2
    zc = top + mm(tk["clear"]) + r                      # the tank's axis
    iu, iv = (mm(x) for x in isl["centre"])
    ix, iy, ih = mm(isl["size"][0]) / 2, mm(isl["size"][1]) / 2, mm(isl["size"][2])
    out = {"centre": F.p((x0 + x1) / 2, (y0 + y1) / 2), "lights": {}, "sockets": {}, "dims": {}}

    def box(lo, hi, style, skip=()):
        prims.box(mb, *F.box(lo, hi), style, skip=F.skip(skip))

    def cyl(p0, p1, rad, sides, style, **kw):
        prims.cylinder(mb, F.p(*p0), F.p(*p1), rad, sides, style, **kw)

    if lod == 3:   # the tank as a box
        box((-half, -r, top), (half, r, zc + r), st["tank"], skip=("-2",))
        _records(out, spec, F, r, half, zc)
        return out

    if lod <= 2:   # the pad
        fx.pad(mb, *F.box((x0, y0, -0.15), (x1, y1, top)), st["pad"])
    sides = (16, 12, 8)[lod]
    cyl((-half, 0, zc), (half, 0, zc), r, sides, st["tank"].but(smooth=True), caps=(True, True))
    vu = -half + 0.45
    if lod <= 1:   # the saddles, steel cradles under the tank
        sw, sl = mm(tk["saddle"][0]) / 2, mm(tk["saddle"][1]) / 2
        for s in tk["saddles"]:
            su = mm(s)
            box((su - sw, -sl, top), (su + sw, sl, zc - r * 0.55), st["saddle"], skip=("-2",))
        # the vent pipe, up from the top near the -x end to the code's 12 ft over the ground
        cyl((vu, 0, zc + r - 0.02), (vu, 0, mm(tk["vent"])), 0.04, 6, st["saddle"], caps=(False, True))
        # the island: a yellow concrete curb, the dispenser's cabinet and its head on it
        box((iu - ix, iv - iy, top), (iu + ix, iv + iy, top + ih), st["curb"], skip=("-2",))
    dx, dy, dz = (mm(x) / 2 for x in dp["size"])
    hx, hy, hz = mm(dp["head"][0]) / 2, mm(dp["head"][1]) / 2, mm(dp["head"][2])
    base = top + ih
    if lod == 2:   # the dispenser as one box
        box((iu - hx, iv - hy, base), (iu + hx, iv + hy, base + 2 * dz + hz), st["dispenser"], skip=("-2",))
    else:
        box((iu - dx, iv - dy, base), (iu + dx, iv + dy, base + 2 * dz), st["dispenser"], skip=("-2",))
        box((iu - hx, iv - hy, base + 2 * dz), (iu + hx, iv + hy, base + 2 * dz + hz), st["dispenser"])
        # its display, a dark face toward the lane (+y), and the nozzle's holster below it
        fy = iv + hy + 0.004
        z0, z1 = base + 2 * dz + 0.06, base + 2 * dz + hz - 0.06
        mb.face(mb.verts_lift([F.p(iu - hx + 0.06, fy, z0), F.p(iu + hx - 0.06, fy, z0), F.p(iu + hx - 0.06, fy, z1),
                               F.p(iu - hx + 0.06, fy, z1)]), st["glass"], F.vec(0.0, 1.0))
        box((iu + dx - 0.22, iv + dy, base + 0.85), (iu + dx - 0.06, iv + dy + 0.10, base + 1.05), st["hose"], skip=("-1",))
    if lod == 0:
        for e in (-1, 1):   # the end bands, where the heads meet the shell
            eu = e * (half - 0.05)
            cyl((eu - 0.04, 0, zc), (eu + 0.04, 0, zc), r + 0.015, 16, st["tank"], caps=(False, False))
        zt = zc + r
        cyl((0.3, 0, zt - 0.03), (0.3, 0, zt + 0.10), 0.28, 12, st["tank"], caps=(False, True))   # the manway
        fu = half - 0.55
        box((fu - 0.22, -0.22, zt - 0.05), (fu + 0.22, 0.22, zt + 0.26), st["saddle"], skip=("-2",))   # the fill box
        box((-0.8, -0.08, zt - 0.03), (-0.6, 0.08, zt + 0.16), st["saddle"], skip=("-2",))          # the level gauge
        cyl((vu, 0, mm(tk["vent"])), (vu, 0, mm(tk["vent"]) + 0.08), 0.09, 8, st["saddle"], caps=(True, True))
        # the supply pipe: up off the tank, across to the island, down into the dispenser's head
        pu = iu - 0.12
        zp = max(zt, base + 2 * dz + hz) + 0.25
        pts = [(pu, 0, zt - 0.03), (pu, 0, zp), (pu, iv, zp), (pu, iv, base + 2 * dz + hz)]
        for p0, p1 in zip(pts, pts[1:]):
            cyl(p0, p1, 0.03, 6, st["saddle"], caps=(False, False))
        # the hose, hanging in a loop from the head's side to the nozzle in its holster
        h = [(iu + hx, iv + 0.05, base + 2 * dz + 0.08), (iu + hx + 0.18, iv + hy + 0.05, base + 0.55),
             (iu + dx - 0.14, iv + dy + 0.10, base + 0.80)]
        for p0, p1 in zip(h, h[1:]):
            cyl(p0, p1, 0.016, 6, st["hose"], caps=(False, False))
    if lod <= 1:   # bollards, on the pad
        for b in spec["bollards"]:
            x, y = mm(b[0]), mm(b[1])
            cyl((x, y, top), (x, y, top + 1.1), 0.11, 10 if lod == 0 else 6, st["bollard"], caps=(False, True))
    pl = spec["pole"]
    base_p = F.p(mm(pl["at"][0]), mm(pl["at"][1]))[:2]
    arm = F.vec(mm(pl["arm"][0]), mm(pl["arm"][1]))[:2]
    head = fx.pole_light(mb, base_p, mm(pl["height"]), arm, st, lod)
    tgt = Vector(F.p(*(mm(x) for x in pl["aimAt"])))
    out["lights"][pl["name"]] = (head, tuple((tgt - Vector(head)).normalized()))
    _records(out, spec, F, r, half, zc)
    # the island stands between the tank and the lane; the tank and the fuelling machine on the pad
    ln, wd = (mm(x) for x in spec["machine"])
    ly = mm(spec["lane"][1])
    if not (y0 < -r and r < iv - iy and iv + iy < ly - wd / 2):
        a.problems.append("the fuel island doesn't stand between the tank and the lane")
    if not (x0 < -half and half < x1 and ly + wd / 2 <= y1):
        a.problems.append("the fuel tank or its lane runs off the pad")
    return out


def _records(out, spec, F, r, half, zc):
    tk = spec["tank"]
    litres = 3.14159265 * r * r * 2 * half * 1000
    out["sockets"]["fuel"] = F.p(*(mm(x) for x in spec["lane"]))
    out["sockets"]["fuel_fill"] = F.p(half - 0.55, 0.0, zc + r + 0.26)
    ln, wd = (mm(x) for x in spec["machine"])
    lx, ly = mm(spec["lane"][0]), mm(spec["lane"][1])
    tb = F.box((-half, -r, 0.0), (half, r, 0.0))
    mbx = F.box((lx - ln / 2, ly - wd / 2, 0.0), (lx + ln / 2, ly + wd / 2, 0.0))
    out["dims"] = {"tank": {"dia": tk["dia"], "length": tk["length"], "gallons": tk["gallons"],
                            "shellGallons": round(litres / 1000 / GALLON_M3)},
                   "pad": [spec["pad"]["x"][1] - spec["pad"]["x"][0], spec["pad"]["y"][1] - spec["pad"]["y"][0]],
                   "tankBox": [round(tb[0][0], 3), round(tb[1][0], 3), round(tb[0][1], 3), round(tb[1][1], 3)],
                   "machineBox": [round(mbx[0][0], 3), round(mbx[1][0], 3), round(mbx[0][1], 3), round(mbx[1][1], 3)]}
