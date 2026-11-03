"""Shared pieces of the Sessellift Stern terminals (sessellift_stern4.json; assets/stern_drive.py, stern_return.py):
the gauge rule for the narrower models, the concrete piers, and the lifting portal over a head's sheave rows.

The Stern's overlays (sessellift_stern3.json, stern2.json) change only common.lineGauge; liftkit/gauge.py moves the
FGQ-4's values by its RULE, so the Stern moves its own here by the same rule (owner LD5): values given against the
rope follow it by construction, and the bullwheels' rims and plates follow the gauge, their section kept.
"""
import copy
import math

from liftkit import prims
from liftkit.prims import U, V, W


def m(x):
    return x / 1000.0


def shift(c):
    """ds (mm): how far each rope sits outboard of the quad's, negative on a narrower model."""
    return (c["lineGauge"] - c["quadGauge"]) / 2


def drive_wheel(c):
    """common with the drive's bullwheel (parts.bullwheel_spec) at the line gauge: its pitch, plates and stubs."""
    ds = shift(c)
    out = copy.deepcopy(c)
    bw = out["bullwheel"]
    bw["pitch"] = c["lineGauge"]
    bw["plateOuter"] += 2 * ds
    bw["plateInner"] += 2 * ds
    bw["stubs"]["reach"] += ds
    return out


def return_wheel(c, bw):
    """The return's bullwheel (return_terminal.bullwheel) at the line gauge: plates and hook lugs moved by ds."""
    ds = shift(c)
    out = copy.deepcopy(bw)
    out["plateOuter"] += 2 * ds
    out["plateInner"] += 2 * ds
    for k in ("from", "to"):
        out["lugs"][k][0] += ds
    return out


def pier(mb, lo, hi, top, bottom, margin, st):
    """A concrete pier under a base plate spanning lo..hi in (u, v) (mm), from far below grade (owner P3-07: really
    tall, so the terminal can be raised or lowered; the game puts a footing at grade under its pier_top socket) up
    to top. Returns the pier_top socket (its top centre, metres)."""
    u0, v0 = m(lo[0] - margin), m(lo[1] - margin)
    u1, v1 = m(hi[0] + margin), m(hi[1] + margin)
    prims.box(mb, (u0, v0, m(bottom)), (u1, v1, m(top)), st["concrete"], skip=("-2",))
    return ((u0 + u1) / 2, (v0 + v1) / 2, m(top))


def lifting_portal(mb, lp, hg, lod, st, foot_w):
    """The lifting portal over a head's sheave rows: two 152 mm legs leaning out from foot_w (mm) to the beam, the
    top beam reaching past the ropes, and lug plates with eyes under the beam over each rope (for lifting the rows
    out). Leg and beam positions are given against the rope (lp "foot", "head", "beam.out"), so they follow the gauge."""
    u0, u1 = m(lp["u"][0]), m(lp["u"][1])
    bm = lp["beam"]
    b_lo, b_hi = m(bm["bottom"]), m(bm["top"])
    hb = hg + m(bm["out"])
    prims.box(mb, (u0, -hb, b_lo), (u1, hb, b_hi), st["steel"])
    if lod >= 3:
        return
    for s in (-1, 1):   # legs square in u, leaning out in v, cut level on the head and under the beam
        f0, f1 = sorted((s * (hg + m(lp["foot"][0])), s * (hg + m(lp["foot"][1]))))
        h0, h1 = sorted((s * (hg + m(lp["head"][0])), s * (hg + m(lp["head"][1]))))
        prims.prism(mb, [(f0, m(foot_w)), (f1, m(foot_w)), (h1, b_lo), (h0, b_lo)], (u0, 0, 0), V, W, U, u1 - u0, st["steel"])
    if lod <= 1:   # lug plates under the beam over each rope, a round end with the eye
        lu = lp["lugs"]
        uc, hu, th = (u0 + u1) / 2, m(lu["halfU"]), m(lu["thick"])
        drop = m(lu["drop"])
        r = hu
        ctr = b_lo - drop + r
        outline = [(uc - hu, b_lo), (uc - hu, ctr)]
        outline += [(uc - r * math.cos(a), ctr - r * math.sin(a)) for a in (math.pi * k / 6 for k in range(1, 6))]
        outline += [(uc + hu, ctr), (uc + hu, b_lo)]
        for s in (-1, 1):
            prims.prism(mb, outline, (0, s * hg - th / 2, 0), U, W, V, th, st["yellow"])
            if lod == 0:
                prims.cylinder(mb, (uc, s * hg - th / 2 - 0.012, ctr), (uc, s * hg + th / 2 + 0.012, ctr), r * 0.55, 10, st["yellow"])
