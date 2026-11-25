"""A fixed-grip chair's timber unload ramp at its top return terminal, from its spec (task 4.50 phase 4; unloadramp.json).

The owner's rules (2026-11-24): a top return terminal whose unload stands 5 ft or more over the ground gets this ramp
(under 5 ft an earth mound of the same plan, graded by the game; a top drive gets none); it clips into the terrain so
ramp and ground meet smoothly, so the ski-off ramp has no railings or walls (the level deck has a guard rail round the
hut bay and along the return side's outer edge, and a 2x4 lip marks every other edge: the ramp's two sides and the rear
edge, owner 2026-11-24); it is double wide, like the photos: one deck across
both sides of the lift, and one ski-off ramp ahead across the full width, flaring out as it falls. No ramp runs back
down the lift line: the deck's back edges are a plain drop.

One model for every lift (owner, 2026-11-24): the ramp is the same model whatever the lift's maker and wherever it
stands; only the chair's width changes it, so there are three (quad, triple, double). Clipping makes that work. The
ramp is built for the tallest site it serves (`reach`): its ski-off runs on at its slope for `reach.length`, and every
post runs down past `reach.raise`, so the terrain clips the ramp and the posts wherever the ground lies. The deck is
one plain surface with no opening for the lift: each maker's terminal stands through it (the boards and joists clip
round its columns), and only the post rows and their beams keep clear of every terminal in the spec's table.

Ramp frame (millimetres in the spec, metres here): the origin is the unload point on the deck's top, under the up
rope (the terminal's `chair_unload` socket, dropped to the deck); u runs toward the ski-off (away from the lift
line), v outboard (away from the lift's centreline, toward the hut), w up. The lift's centreline is at v = -gauge/2.
The up side's outer edge is `width` past a line `pierClear` inside the centreline, and the down side mirrors it, so
the deck is centred on the lift. The level deck runs from u = -before (the up side; -downBack on the down side) to
u = +after, with the hut bay outboard of the up side. The ramp starts at the deck's front edge and falls at `slope`,
its edges flaring out `flare` per metre.

The terminals (`terminals`, one entry per top return style, measured on the lift models): `wheelU`, the bullwheel's
axle past the unload point; `through`, the columns and piers that stand through the deck; `under`, what lies just
under it. Post rows and beams part round all of them (`posts.clear`), for every terminal at once.

The hut (`hutBay`): the deck is framed round the hut's footprint and the hut stands in the opening on four steel pipe
posts of its own, built here down into the ground, its floor a threshold above the deck; the hut's skids, step,
landing and stair (its `legs` part) are hidden. The bay ends flush with the hut's back wall (owner 2026-11-24), and
a timber guard rail (`rail`: posts, a top rail and a mid rail) runs round the bay's open edges, from the walkway's
top round the front corner to the hut and from the hut round the back corner to the up side's outer edge, and along
the return side's outer edge (opposite the hut) for the level deck's length. A lip (`lip`, a 2x4 laid flat on the
boards, its outer face flush with the edge) runs down both sides of the ski-off ramp and along the rear edge, but not
across the lane where the riders' skis come in (the mat's width). The operator walks up from the snow on a walkway (`walkway`, owner
2026-11-24): the ski-off ramp carries on `width` further on the hut side, at the same slope and flare, as one ramp,
and that outer strip has a cleat across it every `cleats.pitch`; it meets the hut bay's front edge beside the
operator's pedestal.

No light pole and no signs (owner, 2026-11-24). The stop gate copies the real ones (owner, 2026-11-24, after the
photo survey): a post on the ramp past the unload, on the lift's centreline, and a thin bar from its top along the line
under the chair turning round the far end of the wheel (bldgkit/stop_gate.py, its own model, one per terminal style);
gate_at() gives its place for each terminal.

site_report() gives what depends on where a ramp stands (its `raise` over the natural ground, the ground's `fall`
ahead, the lift's `terminal`): where the ramp meets the ground, the deck above the ground, the cost and the stop gate's bar.

LOD0 boards on joists, the hut's opening and its frame, the rim fascia, beams, posts and bracing (across each row and
knee braces along the deck), the hut's posts and the mat; LOD1 the deck as a slab with the hut's opening, beams,
posts, the hut's posts and the mat; LOD2 the slab, edge posts and the hut's posts; LOD3 the deck's top and sides. Sockets:
`chair_unload` (the lift's own socket: under the up rope at rope height), `breakover` (the middle of the ramp's top
edge), `ramp_end` (the middle of the model's buried end), `hut` (the hut's origin: its footprint centre, its floor a
threshold over the deck, the hut facing -v), `pedestal` (where the operator's pedestal stands,
bldg_op_pedestal) and `walkway` (the middle of the walkway's top edge, on the hut bay's front edge).
"""
import math

from mathutils import Vector

from liftkit import prims
from liftkit.export import Asset
from liftkit.frame import mm
from liftkit.mesh import MeshBuilder, Style

LODS = 4
TIMBER = ("timber_deck", "timber_post")
UP, DOWN = (0.0, 0.0, 1.0), (0.0, 0.0, -1.0)
GAIN_MIN = 0.02   # the least rate the ramp closes on falling ground (per metre) before the game fills under its toe


def styles():
    return {
        "rail": Style("timber_post", snow=0.3), "lip": Style("timber_post", snow=0.3),
        "deck": Style("timber_deck", snow=1.0, trim="planks"), "rim": Style("timber_deck", snow=0.0),
        "under": Style("timber_deck", snow=0.0), "joist": Style("timber_post", snow=0.0),
        "beam": Style("timber_post", snow=0.0), "post": Style("timber_post", snow=0.0),
        "brace": Style("timber_post", snow=0.0), "mat": Style("rubber", snow=1.0),
        "steel": Style("galvanised", snow=0.0), "cleat": Style("timber_post", snow=0.3),
    }


def pick(spec, variant):
    """The variant's spec: `common` overlaid with the variant's keys (one level deep for dicts)."""
    out = _copy(spec["common"])
    for k, v in spec["variants"][variant].items():
        out[k] = {**out[k], **v} if isinstance(v, dict) and isinstance(out.get(k), dict) else v
    return out


def _copy(x):
    if isinstance(x, dict):
        return {k: _copy(v) for k, v in x.items()}
    if isinstance(x, list):
        return [_copy(v) for v in x]
    return x


def terminals(spec):
    """The spec's terminal styles (its table less the note)."""
    return [k for k in spec["terminals"] if not k.startswith("_")]


class Plan:
    """The ramp's plan from its spec: the deck's edges and the ski-off's line."""

    def __init__(self, spec):
        lv = spec["level"]
        self.A, self.B, self.Dn = mm(lv["before"]), mm(lv["after"]), mm(lv["downBack"])
        self.g2 = mm(spec["gauge"]) / 2
        self.chair = mm(spec["chairWidth"])
        self.vc = -self.g2                                              # the lift's centreline
        self.v_out = -(self.g2 - mm(spec["pierClear"])) + mm(spec["width"])   # the up side's outer edge
        self.v_far = 2 * self.vc - self.v_out                           # its mirror: the down side's outer edge
        self.W = self.v_out - self.v_far                                # the deck's and the ramp's width at the breakover
        self.bay = spec.get("hutBay")
        self.v_bay = self.v_out + (mm(self.bay["width"]) if self.bay else 0.0)
        self.slope, self.flare = spec["slope"], spec["flare"]
        self.length = mm(spec["reach"]["length"])                       # the ski-off's run, as modelled
        self.reach = mm(spec["reach"]["raise"])                         # the tallest site it serves
        self.walk = spec.get("walkway") if self.bay else None            # the operator's walkway: the ramp's outer strip
        self.walkW = mm(self.walk["width"]) if self.walk else 0.0

    def u(self, x):
        return self.B + x

    def z(self, x):
        return -self.slope * x

    def edges(self, x):
        """The ramp's edges `x` down from the breakover, the walkway's strip included."""
        return self.v_far - self.flare * x, self.v_out + self.flare * x + self.walkW

    def walk_edges(self, x):
        """The walkway's strip, the ramp's outer `walkW` on the hut side."""
        return self.v_out + self.flare * x, self.v_out + self.flare * x + self.walkW

    def level_m2(self):
        """The level deck and the hut bay, less the hut's opening."""
        m2 = (self.A + self.B) * (self.v_bay - self.vc) + (self.Dn + self.B) * (self.vc - self.v_far)
        if self.bay:
            g = mm(self.bay["gap"])
            back = min(self.v_out + mm(self.bay["hutV"]) + mm(self.bay["hut"][1]) / 2 + g, self.v_bay)
            front = self.v_out + mm(self.bay["hutV"]) - mm(self.bay["hut"][1]) / 2 - g
            m2 -= (mm(self.bay["hut"][0]) + 2 * g) * (back - front)
        return m2


def _solids(spec, pl):
    """Everything any terminal in the table stands in or just under the deck: (u0, u1, v0, v1, top, terminal)."""
    out = []
    for name in terminals(spec):
        t = spec["terminals"][name]
        out += [(mm(c[0]), mm(c[1]), pl.vc - mm(c[2]), pl.vc + mm(c[2]), 1e9, name) for c in t["through"]]
        out += [(mm(c[0]), mm(c[1]), pl.vc - mm(c[2]), pl.vc + mm(c[2]), mm(c[3]), name) for c in t.get("under", [])]
    return out


def gate_at(spec, terminal):
    """The stop gate (bldg_stop_gate_<terminal>, a post and a bar) for a terminal, in the ramp's frame: `window`, the
    stretch (u from, u to) the bar must cover: where the chair's seat crosses the line round the far end of the wheel,
    `gate.before` inside its inner end to `gate.past` beyond its outer end; `at`, the gate's origin, the post's axis
    `gate.postClear` inside the chair's inner end (well inside the circle the chairs' inner ends sweep round the
    wheel), on the lift's centreline at the unload level, the bar pointing +u; `foot`, the ramp's
    surface under the post (the model's own deck and ski-off); `height`, the bar's height over the unload level. The
    game finds the same from the lift's own sockets (the bullwheel's axle, the gauge and the chair's width)."""
    pl = Plan(spec)
    t, g = spec["terminals"][terminal], spec["gate"]
    cross = mm(t["wheelU"]) + pl.g2                  # where the chairs cross the lift's centreline
    low = mm(spec["ropeOverDeck"]) - mm(spec["chairBelowRope"])   # the chair frame's lowest point over the deck
    w0, pu = cross - pl.chair / 2 - mm(g["before"]), cross - pl.chair / 2 - mm(g["postClear"])
    return {"at": (pu, pl.vc, 0.0), "foot": pl.z(pu - pl.B) if pu > pl.B else 0.0,
            "window": (w0, cross + pl.chair / 2 + mm(g["past"])), "height": low - mm(g["under"])}


def site_report(spec, site):
    """What depends on where the ramp stands (`raise` mm over the natural ground, `fall` per metre ahead, `terminal`):
    the ski-off's run to where it meets the ground, its width there, the deck above the ground, the cost, whether the
    game must fill under the toe, the stop gate's place and the stretch its bar covers, and the bar's least height over the surface."""
    pl = Plan(spec)
    R = mm(site["raise"])
    fall = site.get("fall", [0.0, 0.0])[0]
    gain = pl.slope - fall                           # how fast the ramp closes on its ground, per metre
    meet = R / max(gain, GAIN_MIN)
    fill = gain < GAIN_MIN or meet > pl.length or R > pl.reach   # the game grades fill for what the model cannot reach
    meet = min(meet, pl.length)
    area = pl.level_m2() + (pl.W + pl.walkW) * meet + pl.flare * meet ** 2
    cost = spec["cost"]
    g = gate_at(spec, site["terminal"])

    def surface(u):                                  # the deck, the ramp or the ground under the bar
        x = u - pl.B
        return 0.0 if x <= 0 else (pl.z(x) if x <= meet else -R - fall * x)
    n = 40
    path = [g["at"][0] + (g["window"][1] - g["at"][0]) * k / n for k in range(n + 1)]
    over = min(g["height"] - surface(u) for u in path)
    problems = []
    if over < mm(spec["gate"]["least"]) - 1e-9:
        problems.append(f"the stop gate's bar is {over * 1000:.0f} mm over the surface, under {spec['gate']['least']} mm")
    if fill:
        problems.append("the site is past the model's reach: the game fills under the toe")
    return {"raise": site["raise"], "fall": fall, "terminal": site["terminal"], "run": round(meet * 1000),
            "widthAtMeet": round((pl.W + pl.walkW + 2 * pl.flare * meet) * 1000),
            "deckAreaM2": round(area, 1),
            "costUSD": round(cost["perM2"] * area + cost["fixed"], -2), "fillAtToe": fill,
            "gate": [round(x, 3) for x in g["at"]], "gateWindow": [round(x, 3) for x in g["window"]],
            "barOverSurface": round(over * 1000), "problems": problems}


def _minus(spans, lo, hi):
    """The intervals `spans` less (lo, hi)."""
    out = []
    for s0, s1 in spans:
        if hi <= s0 or lo >= s1:
            out.append((s0, s1))
            continue
        if lo > s0:
            out.append((s0, lo))
        if hi < s1:
            out.append((hi, s1))
    return out


def _rail(spec, pl, lod, st, tbox, a, hut_at, hut_half):
    """The level deck's guard rail: posts, a top rail and a mid rail, the posts' outer faces `inset` inside the edge.
    Runs round the hut bay's open edges: the front edge from the walkway's top to the front corner, the back edge from
    that corner to the hut, from the hut to the rear corner, and the rear edge from that corner to the up side's outer
    edge; and (`returnSide`, owner 2026-11-24) the return side's outer edge, opposite the hut, from the breakover to the
    rear corner. LOD0 posts, top and mid rails; LOD1 the end and corner posts and the top rail (its underside left
    out); LOD2 the top rail; LOD3 none. Returns the runs' lengths."""
    if lod >= 3:
        return []
    rl = spec["rail"]
    H, (pw, _), (cw, ct), (mw, mt) = mm(rl["height"]), [mm(x) for x in rl["post"]], [mm(x) for x in rl["cap"]], \
        [mm(x) for x in rl["mid"]]
    ins, pitch, mid_at, hg = mm(rl["inset"]) + pw / 2, mm(rl["pitch"]), mm(rl["midAt"]), mm(rl["gapToHut"])
    A, B, v_out, v_bay = pl.A, pl.B, pl.v_out, pl.v_bay
    hl, hw = hut_half
    uf, ub, vb = B - ins, -A + ins, v_bay - ins                       # the post lines: front, rear, back
    hut0, hut1 = hut_at[0] - hl - hg - pw / 2, hut_at[0] + hl + hg + pw / 2   # posts beside the hut's ends
    runs = [((uf, v_out + pl.walkW + pw / 2), (uf, vb)), ((uf, vb), (hut1, vb)),
            ((hut0, vb), (ub, vb)), ((ub, vb), (ub, v_out + pw / 2))]
    if rl.get("returnSide"):
        vr = pl.v_far + ins
        runs.append(((uf, vr), (-pl.Dn + ins, vr)))
        if (2 * pl.vc - pl.chair / 2) - (pl.v_far + pw) < 0.5:
            a.problems.append("the return side's rail stands within 0.5 m of the chairs' path")
    if hut1 > uf - 0.3 or hut0 < ub + 0.3:
        a.problems.append("the hut bay's rail has no room beside the hut")
    lengths = []
    for r, ((u0, v0), (u1, v1)) in enumerate(runs):
        n = max(1, math.ceil(max(abs(u1 - u0), abs(v1 - v0)) / pitch))
        pts = [(u0 + (u1 - u0) * k / n, v0 + (v1 - v0) * k / n) for k in range(n + 1)]
        lengths.append(max(abs(u1 - u0), abs(v1 - v0)))
        along_u = abs(u1 - u0) > abs(v1 - v0)
        if lod <= 1:
            for k, (pu, pv) in enumerate(pts):
                if k == 0 and r in (1, 3):
                    continue                                          # the corner post, built by the run before
                if lod == 1 and 0 < k < n:
                    continue
                tbox((pu - pw / 2, pv - pw / 2, 0.0), (pu + pw / 2, pv + pw / 2, H - ct), st["rail"], skip=("+2", "-2"))

        def bar(w, t, z0, z1, ext, skip):
            lo_u, hi_u, lo_v, hi_v = min(u0, u1), max(u0, u1), min(v0, v1), max(v0, v1)
            if along_u:
                tbox((lo_u - ext, lo_v - w / 2, z0), (hi_u + ext, hi_v + w / 2, z1), st["rail"], skip=skip)
            else:
                tbox((lo_u - w / 2, lo_v - ext, z0), (hi_u + w / 2, hi_v + ext, z1), st["rail"], skip=skip)
        bar(cw, ct, H - ct, H, cw / 2, ("-2",) if lod >= 1 else ())
        if lod == 0:
            bar(mt, mw, mid_at - mw / 2, mid_at + mw / 2, -pw / 2, ("-0", "+0") if along_u else ("-1", "+1"))
    if lod == 0:
        if not 0.991 <= H <= 1.143:
            a.problems.append(f"the hut bay's rail is {H * 1000:.0f} mm high, not 42 in (39-45 in)")
        if abs(mid_at - H / 2) > 0.08:
            a.problems.append("the hut bay's mid rail is not about halfway up")
    if v_out + pw / 2 < pl.chair / 2 + 0.5:
        a.problems.append("the hut bay's rail stands within 0.5 m of the chairs' path")
    return lengths


def _lip(spec, pl, lod, st, mb, tbox, a, P):
    """The deck's lip (owner 2026-11-24): a 2x4 laid flat on the boards along each edge that has no rail, its outer
    face flush with the edge: down both sides of the ski-off ramp (the walkway's outer edge on the hut side), and
    along the rear edge between the rails' corner posts, but not across the lane where the riders' skis come in (the
    mat's width about the up rope). LOD0 only: 38 mm tall, it is under a pixel beyond that. Returns its length."""
    if lod > 0:
        return 0.0
    w, t = (mm(x) for x in spec["lip"]["size"])
    L = pl.length
    total = 0.0
    for side, s in ((0, 1.0), (1, -1.0)):            # the ramp's sides; s points inward
        e0, e1 = pl.edges(0.0)[side], pl.edges(L)[side]
        o0, o1, i0, i1 = P(0.0, e0), P(L, e1), P(0.0, e0 + s * w), P(L, e1 + s * w)

        def up(p):
            return (p[0], p[1], p[2] + t)
        mb.face(mb.verts_lift([up(o0), up(o1), up(i1), up(i0)]), st["lip"], UP)
        mb.face(mb.verts_lift([i0, i1, up(i1), up(i0)]), st["lip"], (0.0, s, 0.0))        # the inner face
        mb.face(mb.verts_lift([o0, o1, up(o1), up(o0)]), st["lip"], (0.0, -s, 0.0))       # the outer face
        mb.face(mb.verts_lift([o0, i0, up(i0), up(o0)]), st["lip"], (-1.0, 0.0, 0.0))     # its top end
        total += L
    pw = mm(spec["rail"]["post"][0]) if spec.get("rail") else 0.0
    ret = pw if spec.get("rail", {}).get("returnSide") else 0.0
    mh = pl.chair / 2 + mm(spec["mat"]["margin"])    # the riders' lane: no lip across it
    if abs(pl.A - pl.Dn) < 1e-6:
        edges = [(-pl.A, pl.v_far + ret, pl.v_out)]
    else:                                            # the step along the centreline between them stays plain
        edges = [(-pl.A, pl.vc, pl.v_out), (-pl.Dn, pl.v_far + ret, pl.vc)]
    for u, v0, v1 in edges:
        for s0, s1 in _minus([(v0, v1)], -mh, mh):
            if s1 - s0 < 0.3:
                continue
            tbox((u, s0, 0.0), (u + w, s1, t), st["lip"], skip=("-2",))   # kept clear of the terminals, as a beam
            total += s1 - s0
    if not 0.03 <= t <= 0.1:
        a.problems.append(f"the deck's lip is {t * 1000:.0f} mm tall, not a 2x4's")
    return total


def build(spec, lod, asset_id=None):
    st = styles()
    a = Asset(asset_id or spec["id"])
    mb = MeshBuilder()
    pl = Plan(spec)
    A, B, Dn, g2, chair = pl.A, pl.B, pl.Dn, pl.g2, pl.chair
    vc, v_out, v_far, v_bay, W = pl.vc, pl.v_out, pl.v_far, pl.v_bay, pl.W
    L, Rmax = pl.length, pl.reach
    dk, ps = spec["deck"], spec["posts"]
    D, board = mm(dk["depth"]), mm(dk["boards"])
    bay = pl.bay
    a.lights, a.glow, a.problems = [], {}, []

    def in_deck(u, v):
        return u < B and ((v >= vc and u > -A and v < v_bay) or (v < vc and u > -Dn and v > v_far))

    # -- the hut's opening (the lift's terminal gets none: one model serves every lift, its columns clip the deck) ----
    holes = []                                       # (u0, u1, v0, v1)
    hut_at = None
    if bay:
        hl, hw = mm(bay["hut"][0]) / 2, mm(bay["hut"][1]) / 2
        hut_at = (mm(bay["hutU"]), v_out + mm(bay["hutV"]))
        hg = mm(bay["gap"])
        holes.append((hut_at[0] - hl - hg, hut_at[0] + hl + hg, hut_at[1] - hw - hg, hut_at[1] + hw + hg))

    def in_hole(u, v):
        return any(h[0] < u < h[1] and h[2] < v < h[3] for h in holes)

    # -- the level deck as a grid of cells: the up side (with the hut bay) from -A, the down side from -Dn, less the
    #    hut's opening (LOD0-1; further out the hut hides it) -------------------------------------------------------
    cut = lod <= 1
    gu_ = sorted({-A, -Dn, B} | ({x for h in holes for x in h[:2] if -A < x < B} if cut else set()))
    gv_ = sorted({v_far, vc, v_bay} | ({v_out + pl.walkW} if pl.walk else set()) | ({x for h in holes for x in h[2:] if v_far < x < v_bay} if cut else set()))

    def cell(i, j):
        if not (0 <= i < len(gu_) - 1 and 0 <= j < len(gv_) - 1):
            return False
        u, v = (gu_[i] + gu_[i + 1]) / 2, (gv_[j] + gv_[j + 1]) / 2
        return in_deck(u, v) and not (cut and in_hole(u, v))
    rects = []                                       # cells merged along u
    for j in range(len(gv_) - 1):
        i = 0
        while i < len(gu_) - 1:
            if cell(i, j):
                k = i
                while cell(k + 1, j):
                    k += 1
                rects.append((gu_[i], gu_[k + 1], gv_[j], gv_[j + 1]))
                i = k + 1
            else:
                i += 1

    def rect(r, w, style, normal):
        u0, u1, v0, v1 = r
        return mb.face(mb.verts_lift([(u0, v0, w), (u1, v0, w), (u1, v1, w), (u0, v1, w)]), style, normal)

    def P(x, v, dz=0.0):
        return (pl.u(x), v, pl.z(x) + dz)

    def ramp_quad(x0, x1, dz, style, normal):
        e0, e1 = pl.edges(x0), pl.edges(x1)
        q = [P(x0, e0[0], dz), P(x1, e1[0], dz), P(x1, e1[1], dz), P(x0, e0[1], dz)]
        return mb.face(mb.verts_lift(q), style, normal)

    for r in rects:                                  # one face per cell: the boards are drawn by the planks detail tile
        rect(r, 0.0, st["deck"], UP)                 # (owner 2026-11-24: texture, not modelled boards)
    ramp_quad(0.0, L, 0.0, st["deck"], UP)

    # -- the edges (rim fascia, below the deck's top: nothing stands above the deck at its edges): every cell edge
    #    with no deck beyond it, but not where the ramp joins ------------------------------------------------------
    bottom = -D

    def wall(p0, p1, normal):
        q = [(p0[0], p0[1], 0.0), (p1[0], p1[1], 0.0), (p1[0], p1[1], bottom), (p0[0], p0[1], bottom)]
        mb.face(mb.verts_lift(q), st["rim"], normal)
    for j in range(len(gv_) - 1):
        v0, v1 = gv_[j], gv_[j + 1]
        for i in range(len(gu_) - 1):
            if not cell(i, j):
                continue
            u0, u1 = gu_[i], gu_[i + 1]
            if not cell(i - 1, j):
                wall((u0, v0), (u0, v1), (-1.0, 0.0, 0.0))
            if not cell(i + 1, j) and not (abs(u1 - B) < 1e-6 and v1 <= v_out + pl.walkW + 1e-6):
                wall((u1, v0), (u1, v1), (1.0, 0.0, 0.0))
            if not cell(i, j - 1):
                wall((u0, v0), (u1, v0), (0.0, -1.0, 0.0))
            if not cell(i, j + 1):
                wall((u0, v1), (u1, v1), (0.0, 1.0, 0.0))
    for side in (0, 1):                                           # the ramp's sides
        e0, e1 = pl.edges(0.0)[side], pl.edges(L)[side]
        n = (0.0, -1.0 if side == 0 else 1.0, 0.0)
        q = [P(0.0, e0), P(L, e1), P(L, e1, bottom), P(0.0, e0, bottom)]
        mb.face(mb.verts_lift(q), st["rim"], n)
    if lod <= 2:   # the far end (LOD3 leaves it open: it is under the ground)
        e = pl.edges(L)
        q = [P(L, e[0]), P(L, e[1]), P(L, e[1], bottom), P(L, e[0], bottom)]
        mb.face(mb.verts_lift(q), st["rim"], (1.0, 0.0, 0.0))

    # the posts' and beams' boxes (u0, u1, v0, v1, w0, w1): they alone keep clear of every terminal (checked below)
    members = []

    def tbox(lo, hi, style, skip=(), keep=True):
        if keep:
            members.append((lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
        prims.box(mb, lo, hi, style, skip=skip)

    # -- underneath: LOD0 the boards' underside, the joists and the hut opening's trimmers and headers; LOD1-2 the
    #    slab's underside -------------------------------------------------------------------------------------------
    if lod <= 2:
        under_w = -board if lod == 0 else bottom
        for r in rects:
            rect(r, under_w, st["under"], DOWN)
        ramp_quad(0.0, L, under_w, st["under"], DOWN)
    jw, jd = mm(dk["joist"][0]), mm(dk["joist"][1])
    if lod == 0:
        pitch = mm(dk["joistPitch"])

        def fractions(width):
            n = max(2, math.ceil(width / pitch) + 1)
            return [k / (n - 1) for k in range(n)]
        for k in fractions(v_bay - v_far):           # level deck joists along u, stopping at the hut's opening
            v = v_far + 0.03 + k * (v_bay - v_far - 0.06)
            spans = [(-A if v > vc else -Dn, B)]
            for h in holes:
                if h[2] - jw / 2 < v < h[3] + jw / 2:
                    spans = _minus(spans, h[0], h[1])
            for s0, s1 in spans:
                if s1 - s0 > 0.05:
                    tbox((s0, v - jw / 2, -board - jd), (s1, v + jw / 2, -board), st["joist"], skip=("+2", "-0", "+0"),
                         keep=False)
        for h in holes:   # doubled trimmers along each side of the opening, doubled headers across its ends
            t0, t1 = max(h[0] - 0.30, -A if h[3] > vc else -Dn), min(h[1] + 0.30, B)
            for v, s in ((h[2], -1), (h[3], 1)):
                if in_deck((t0 + t1) / 2, v + s * jw):
                    tbox((t0, min(v, v + s * 2 * jw), -board - jd), (t1, max(v, v + s * 2 * jw), -board), st["joist"],
                         skip=("+2",), keep=False)
            for u, s in ((h[0], -1), (h[1], 1)):
                if in_deck(u + s * jw, (h[2] + h[3]) / 2):
                    tbox((min(u, u + s * 2 * jw), h[2], -board - jd), (max(u, u + s * 2 * jw), min(h[3], v_bay - 0.02), -board), st["joist"],
                         skip=("+2",), keep=False)
        for k in fractions(W + pl.walkW + 2 * pl.flare * L):    # ramp joists, fanning with the flare
            e0, e1 = pl.edges(0.0), pl.edges(L)
            p0 = Vector(P(0.0, e0[0] + 0.03 + k * (e0[1] - e0[0] - 0.06), -board - jd / 2))
            p1 = Vector(P(L, e1[0] + 0.03 + k * (e1[1] - e1[0] - 0.06), -board - jd / 2))
            jx, jy, jz = prims.basis_along(p1 - p0)
            prims.obox(mb, (p0 + p1) / 2, (jx, jy, jz), ((p1 - p0).length / 2, jw / 2, jd / 2), st["joist"],
                       skip=("-0", "+0", "+2"))   # their tops lie on the boards' underside

    # -- post rows: beams across, posts down past the tallest site's ground, bracing under the deck; rows part round
    #    whatever any terminal stands in or under the deck, and round the hut ---------------------------------------
    bw, bd = mm(spec["beam"][0]), mm(spec["beam"][1])
    psz, pitch, across = mm(ps["size"]), mm(ps["pitch"]), mm(ps["across"])
    below, brace_d = mm(ps["below"]), mm(ps["brace"])
    clear = mm(ps["clear"])                          # posts and beams keep this far from steel and concrete
    solids = _solids(spec, pl)
    blockers = [(s[0] - clear, s[1] + clear, s[2] - clear, s[3] + clear) for s in solids]
    blockers += [holes[-1]] if bay else []
    foot = -Rmax - below                             # the level deck's posts run to here
    rows = []                                        # (u, deck top z, v0, v1, the posts' feet)

    def level_row(u, v0, v1):
        spans = [(v0, v1)]
        for b in blockers:
            if b[0] - bw / 2 < u < b[1] + bw / 2:
                spans = _minus(spans, b[2], b[3])
        rows.extend((u, 0.0, s0, s1, foot) for s0, s1 in spans if s1 - s0 > 0.3)
    n_lvl = max(1, math.ceil((A + B) / pitch))
    us = [-A + bw / 2 + k * (A + B - bw / 2) / n_lvl for k in range(n_lvl + 1)]   # the back row inside the edge
    for u in us:
        level_row(u, v_far if u >= -Dn - 1e-6 else vc, v_bay)
    if not any(-Dn - 1e-6 <= u <= -Dn + 0.6 for u in us):   # carry the down side's back edge
        level_row(-Dn + psz / 2 + 0.02, v_far, vc)
    k = 1
    while k * pitch < L - 0.6:                       # the ski-off's rows, to its end: the ground buries what it must
        x = k * pitch
        e = pl.edges(x)
        rows.append((pl.u(x), pl.z(x), e[0], e[1], min(foot, pl.z(x) - D - bd - below)))
        k += 1
    n_posts, post_at = 0, []
    if lod <= 2:
        for u, z, v0, v1, ft in rows:
            top = z - D
            if lod <= 1:
                tbox((u - bw / 2, v0 + 0.02, top - bd), (u + bw / 2, v1 - 0.02, top), st["beam"], skip=("+2",))
            if v1 - v0 < 0.8:                        # a short piece beside a terminal or the hut: one post
                vs = [(v0 + v1) / 2]
            else:
                n = max(1, math.ceil((v1 - v0 - 2 * 0.15) / across))
                vs = [v0 + 0.15 + i * (v1 - v0 - 0.30) / n for i in range(n + 1)]
            if lod == 2 and len(vs) > 1:
                vs = [vs[0], vs[-1]]
            ptop = top - (bd if lod <= 1 else 0.0)
            for v in vs:
                tbox((u - psz / 2, v - psz / 2, ft), (u + psz / 2, v + psz / 2, ptop), st["post"], skip=("+2", "-2"))
                post_at.append((u, v))
                n_posts += 1
            if lod == 0 and z > -Rmax + 0.5:         # bracing where a row can stand clear of the ground
                ub = u + (psz / 2 + 0.02) * (1 if u < 0 else -1)   # on the posts' faces, inside the deck's edge
                for v_a, v_b in zip(vs, vs[1:]):     # one diagonal between each pair of posts, 2x6
                    prims.beam(mb, Vector((ub, v_a, ptop - 0.15)), Vector((ub, v_b, ptop - brace_d)), 0.04, 0.14,
                               st["brace"], up=(1.0, 0.0, 0.0))
                for v, s in ((vs[0], -1), (vs[-1], 1)):   # a knee brace along the deck at each end of the row
                    u2 = u + 0.9 if u + 0.9 <= B else u - 0.9
                    z2 = (pl.z(u2 - B) if u2 > B else 0.0) - D
                    vb = v - s * (psz / 2 + 0.02)     # on the post's inner face
                    prims.beam(mb, Vector((u, vb, z2 - 0.9)), Vector((u2, vb, z2 - 0.02)), 0.04, 0.14, st["brace"],
                               up=(0.0, 1.0, 0.0))
    if lod <= 1:   # a post under each corner of the hut opening's frame, where no row's post already stands near
        for h in holes:
            for cu, cv in ((h[0] - jw, h[2] - jw), (h[0] - jw, h[3] + jw), (h[1] + jw, h[2] - jw), (h[1] + jw, h[3] + jw)):
                if not all(in_deck(cu + du, cv + dv) for du in (-psz, psz) for dv in (-psz, psz)):
                    continue
                if any(abs(cu - pu) < 0.7 and abs(cv - pv) < 0.7 for pu, pv in post_at):
                    continue
                tbox((cu - psz / 2, cv - psz / 2, foot), (cu + psz / 2, cv + psz / 2, -board - jd), st["post"],
                     skip=("+2", "-2"))
                post_at.append((cu, cv))
                n_posts += 1

    # -- the operator's walkway: the ramp's outer strip on the hut side, a cleat across it every `cleats.pitch` -------
    if pl.walk and lod == 0:
        cl = pl.walk["cleats"]
        cw, ch, cp = mm(cl["size"][0]), mm(cl["size"][1]), mm(cl["pitch"])
        lip_in = max(0.03, mm(spec["lip"]["size"][0])) if spec.get("lip") else 0.03   # the cleats stop at the lip
        x = mm(cl["from"])
        while x < L - 0.2:
            e0, e1 = pl.walk_edges(x)
            u, z = pl.u(x), pl.z(x)
            prims.box(mb, (u - cw / 2, e0 + 0.03, z - 0.012), (u + cw / 2, e1 - lip_in, z + ch), st["cleat"],
                      skip=("-1", "+1", "-2"))
            x += cp

    # -- the hut's own four steel posts, through its opening down into the ground ------------------------------------
    hut_floor = mm(bay["threshold"]) if bay else 0.0   # the hut's floor over the deck: a threshold at its door
    if bay and lod <= 2:
        hp = bay["posts"]
        for su_ in (-1, 1):
            for sv_ in (-1, 1):
                pu, pv = hut_at[0] + su_ * (hl - mm(hp["inset"])), hut_at[1] + sv_ * (hw - mm(hp["inset"]))
                prims.cylinder(mb, (pu, pv, foot), (pu, pv, hut_floor - mm(bay["base"])), mm(hp["dia"]) / 2,
                               6 if lod == 0 else 4, st["steel"], caps=(False, False))

    # -- the mat where skis touch, as wide as the chair, on to the start of the ski-off -------------------------------
    mt = spec["mat"]
    mh, m0, m1 = chair / 2 + mm(mt["margin"]), mm(mt["from"]), mm(mt["to"])
    if lod <= 1:
        level_quad_m = [(m0, -mh, 0.006), (B, -mh, 0.006), (B, mh, 0.006), (m0, mh, 0.006)]
        mb.face(mb.verts_lift(level_quad_m), st["mat"], UP)
        x1 = m1 - B
        q = [P(0.0, -mh, 0.006), P(x1, -mh, 0.006), P(x1, mh, 0.006), P(0.0, mh, 0.006)]
        mb.face(mb.verts_lift(q), st["mat"], UP)

    # -- checks ---------------------------------------------------------------------------------------------------
    rope = mm(spec["ropeOverDeck"])
    low = rope - mm(spec["chairBelowRope"])          # the chair frame's lowest point over the deck
    seat = rope - mm(spec["seatBelowRope"])          # the top of the seat's pad, mid-depth (the code's chair height)
    if low - mm(spec["gate"]["under"]) < 0.10:
        a.problems.append(f"the stop gate's bar is not 0.1 m over the unload level (the chair's lowest point is "
                          f"{low:.2f} m up)")
    for name in terminals(spec):
        g = gate_at(spec, name)
        if g["at"][0] < mm(spec["terminals"][name]["gateFace"]) + 0.3:
            a.problems.append(f"the stop gate's post at the {name} terminal stands within 0.3 m of its column")
    if mm(spec["gate"]["postClear"]) < 0.4:
        a.problems.append("the stop gate's post stands within 0.4 m of the chair's inner end: turning chairs may strike it")
        if g["window"][1] > B + L - 1.0:
            a.problems.append(f"the stop gate's window at the {name} terminal runs past the model's end")
    if seat > mm(spec["seatMax"]) + 1e-9:
        a.problems.append(f"the chair's seat is {seat:.2f} m over the deck, more than {spec['seatMax']} mm")
    if pl.slope > spec["slopeMax"] + 1e-9:
        a.problems.append(f"the ramp falls {pl.slope:.0%}, more than {spec['slopeMax']:.0%}")
    if pl.slope * L < Rmax + mm(spec["clip"]):
        a.problems.append(f"the ramp ends {pl.slope * L:.1f} m down, not {spec['clip']} mm under its tallest site's ground")
    top_ok = max((Vector(mb.verts[i]).z for f, s in zip(mb.faces, mb.style) if s.cls in TIMBER for i in f), default=0)
    if top_ok > 0.001:
        a.problems.append(f"timber stands {top_ok * 1000:.0f} mm above the deck (no walls; the hut bay's rail comes after)")
    rail_runs = _rail(spec, pl, lod, st, tbox, a, hut_at, (hl, hw) if bay else None) if bay and spec.get("rail") else []
    lip_m = _lip(spec, pl, lod, st, mb, tbox, a, P) if spec.get("lip") else 0.0
    keep = mm(spec["keepClear"])                     # no post or beam this near any terminal's steel or concrete
    for name in terminals(spec):
        hits = sum(1 for m_ in members for s_ in solids
                   if s_[5] == name and m_[0] < s_[1] + keep and m_[1] > s_[0] - keep and m_[2] < s_[3] + keep
                   and m_[3] > s_[2] - keep and m_[4] < s_[4] + keep)
        if hits:
            a.problems.append(f"{hits} post(s) or beam(s) within {spec['keepClear']} mm of the {name} terminal")
    if pl.walk:
        wk, cl = pl.walk, pl.walk["cleats"]
        if pl.slope > wk["slopeMax"] + 1e-9:
            a.problems.append(f"the walkway falls {pl.slope:.0%}, more than {wk['slopeMax']:.0%}")
        if pl.slope > 0.125 and cl["pitch"] > cl["pitchMax"]:
            a.problems.append(f"the walkway's cleats are {cl['pitch']} mm apart, more than {cl['pitchMax']} mm")
        if v_out + pl.walkW > v_bay - 0.3:
            a.problems.append("the walkway is wider than the hut bay's front edge")
    if bay and not all(in_deck(hut_at[0] + su_ * (hl + 0.3), hut_at[1] - hw - 0.3) for su_ in (-1, 1)) \
            or bay and not all(in_deck(hut_at[0] + su_ * (hl + 0.3), hut_at[1]) for su_ in (-1, 1)):
        a.problems.append("the hut's opening is not inside the deck with 0.3 m round its front and ends")
    if bay and abs(hut_at[1] + hw - v_bay) > 0.001:
        a.problems.append(f"the hut bay's back edge is {(v_bay - hut_at[1] - hw) * 1000:.0f} mm off the hut's back wall")

    a.body = mb
    a.members = members   # for a check against the lifts' own models (a local review tool)

    a.dims.update({
        "unloadWidth": spec["width"], "gauge": spec["gauge"], "chairWidth": spec["chairWidth"],
        "width": round(W * 1000), "level": round((A + B) * 1000), "hutBay": bay["width"] if bay else 0,
        "slope": pl.slope, "rampLength": round(L * 1000), "reachRaise": spec["reach"]["raise"],
        "widthAtEnd": round((W + 2 * pl.flare * L) * 1000), "levelAreaM2": round(pl.level_m2(), 1),
        "seatOverDeck": round(seat * 1000), "posts": n_posts, "lights": 0,
        "walkwayWidth": pl.walk["width"] if pl.walk else 0,
        "railLength": round(sum(r for r in rail_runs) * 1000), "railHeight": spec["rail"]["height"] if rail_runs else 0,
        "lipLength": round(lip_m * 1000),
    })
    mid = (v_far + v_out) / 2
    a.sockets["chair_unload"] = (0.0, 0.0, rope)
    a.sockets["breakover"] = (B, mid, 0.0)
    a.sockets["ramp_end"] = (pl.u(L), mid, pl.z(L))
    if bay:
        a.sockets["hut"] = (hut_at[0], hut_at[1], hut_floor - mm(bay["floor"]))   # the hut's origin, its floor a threshold up
        pd = spec["pedestal"]
        a.sockets["pedestal"] = (mm(pd["u"]), v_out + mm(pd["v"]), 0.0)
        if pl.walk:
            a.sockets["walkway"] = (B, v_out + pl.walkW / 2, 0.0)
    return a

