"""Wide-gauge sheave trains (spec "towerWide.train", metres here), after the owner's reference tower (review T1,
2026-10-22): the kit's sheaves on a tree where every member carries exactly two children, pinned at their load centre,
the whole train hanging like a pendulum from one pin above it (the end block's, liftkit user: assets/tower_wide.py).

  rocker   twin shaped plates either side of its two sheaves (lobes round the axles, a waist, tails down and out), its
           pin below the axle chord (pinDrop); the end rockers carry a flared rope horn on the inboard plate
  beam4    a "4-beam": twin fish-belly bars straddling two rockers, its pin a little below their pins (inboard bar
           only on a hold-down row, so the grip side above the rope stays clear)
  beam6    a "6-beam": a box (RHS) inboard, from a rocker's pin to a 4-beam's
  yoke     the top member: an inclined box through the hanging pin with drop legs to its two children
  block    a combination's top member: twin hanger plates from the pin down past both rows' 4-beams

Trees: 4 sheaves yoke (rocker, rocker); 6 yoke (beam4, rocker); 10 yoke (beam6 (beam4, rocker), beam4), the
6-group on -u as the reference; a combination block (beam4 support row, beam4 hold-down row).
Posable (contract P3-07 M2), the game's prefixes: eq_<name> (yoke) or block_<name>, beam_<name>1 (beam6),
yoke_<name><i> (beam4; yoke_sup_/yoke_hold_<name> in a combination), rocker_<name><i>, sheave_<name><k>; parts as in
liftkit/parts._row (sheaves their own parts at LOD0 and in their rocker at LOD1; at LOD2-3 one part under the top).
Layers across the line are x, measured inboard from the rope's centre (the grips pass outboard).
"""
import math

from mathutils import Vector

from . import prims
from .parts import arc_axles, line_sheave, row_face, sides
from .prims import U, V, W


def _frame(p0, p1, sg):
    """Unit along p0 -> p1 in the u-w plane and the normal pointing away from the rope (sg: -1 the rope above,
    support; +1 below, hold)."""
    d = Vector((p1.x - p0.x, 0.0, p1.z - p0.z)).normalized()
    if d.x < 0:
        d = -d
    n = Vector((-d.z, 0.0, d.x))
    if n.z * sg < 0:
        n = -n
    return d, n


def _node(kind, name, pin, weight, kids=(), **kw):
    return dict(kind=kind, name=name, pin=pin, weight=weight, kids=list(kids), **kw)


def rocker_outline(h, lod, pin):
    """A rocker plate in (along, away from the rope) about its axle chord's middle; axles at +-h, the pin at (0, pin): the
    reference's V (owner, Gate 1 v5): a round lobe about each axle, arms down to a boss round the pin, tails down and out
    past each sheave, open between the sheaves above the arms."""
    if lod >= 1:
        right = [(0.0, pin - 0.075), (h - 0.04, -0.055), (h + 0.065, 0.0), (h + 0.2, 0.25), (h + 0.03, 0.09), (0.0, pin + 0.075)]
    else:
        lobe = [(h + 0.065 * math.sin(t), -0.065 * math.cos(t)) for t in (math.radians(a) for a in (-40, 0, 40, 80))]
        right = [(0.0, pin - 0.075), (0.06, pin - 0.068)] + lobe + [(h + 0.07, 0.04), (h + 0.205, 0.23), (h + 0.195, 0.27),
                                                                      (h + 0.145, 0.25), (h + 0.03, 0.095), (h - 0.06, 0.075),
                                                                      (0.07, pin + 0.06), (0.0, pin + 0.075)]
    left = [(-s, n) for s, n in reversed(right[1:-1])]
    return right + left


def build_tree(a, tr, count, mode, radius, rope_w, name):
    """The train's members and pins (lift frame, u-w; v added when drawn), before any geometry. Returns the top node,
    the axles and the list of rockers."""
    s = a["sheave"]
    r = s["dia"] / 2
    sg = -1.0 if mode == "support" else 1.0
    a2 = dict(a, pitch=tr["pitch"], rockerGap=tr["gap"], trainGap=tr["gap"])
    apex = rope_w + sg * (a["ropeR"] + r)
    axles = [Vector((du, 0.0, apex + sg * dw)) for du, dw in arc_axles(a2, count, radius, "apex")]
    rk = tr["rocker"]
    rockers = []
    for i in range(0, count, 2):
        p0, p1 = axles[i], axles[i + 1]
        d, n = _frame(p0, p1, sg)
        mid = (p0 + p1) / 2
        rockers.append(_node("rocker", f"rocker_{name}{i // 2 + 1}", mid + n * rk["pinDrop"], 2, axles=(p0, p1), mid=mid,
                             d=d, n=n, ks=(i, i + 1)))
    b4 = tr["beam4"]

    def beam4(nm, pair):
        q0, q1 = pair[0]["pin"], pair[1]["pin"]
        d, n = _frame(q0, q1, sg)
        return _node("beam4", nm, (q0 + q1) / 2 + n * b4["pinDrop"], 4, pair, d=d, n=n)

    def load_centre(kids):
        (k0, k1) = kids
        return k0["pin"] + (k1["pin"] - k0["pin"]) * (k1["weight"] / (k0["weight"] + k1["weight"]))

    if count == 4:
        kids = rockers
    elif count == 6:
        kids = [beam4(f"yoke_{name}1", rockers[0:2]), rockers[2]]
    elif count == 10:
        lo = beam4(f"yoke_{name}1", rockers[0:2])
        b6 = _node("beam6", f"beam_{name}1", load_centre([lo, rockers[2]]), 6, [lo, rockers[2]])
        kids = [b6, beam4(f"yoke_{name}2", rockers[3:5])]
    else:
        raise ValueError(f"no wide train of {count} sheaves")
    lc = load_centre(kids)
    return _node("yoke", f"eq_{name}", Vector((lc.x, 0.0, 0.0)), count, kids), axles, rockers, sg


def _shift(node, du):
    node["pin"] = node["pin"] + Vector((du, 0.0, 0.0))
    for k in ("mid",):
        if k in node:
            node[k] = node[k] + Vector((du, 0.0, 0.0))
    if "axles" in node:
        node["axles"] = tuple(p + Vector((du, 0.0, 0.0)) for p in node["axles"])
    for kid in node["kids"]:
        _shift(kid, du)


def _walk(node, parent=None):
    yield node, parent
    for kid in node["kids"]:
        yield from _walk(kid, node)


class _Row:
    """Drawing helpers for one rope: x (inboard from the rope) to v, and plates in a member's (along, normal) plane."""

    def __init__(self, mb, v_rope):
        self.mb = mb
        self.v_rope = v_rope
        self.inb = -1.0 if v_rope > 0 else 1.0

    def v(self, x):
        return self.v_rope + self.inb * x

    def plate(self, poly, origin, d, n, x0, x1, style):
        o = Vector((origin.x, self.v(x0), origin.z))
        prims.prism(self.mb, poly, o, d, n, V * self.inb, x1 - x0, style)

    def obox(self, centre, d, n, half_along, half_n, x0, x1, style):
        c = Vector((centre.x, self.v((x0 + x1) / 2), centre.z))
        prims.obox(self.mb, c, (d, V, n), (half_along, (x1 - x0) / 2, half_n), style)

    def rod(self, p, x0, x1, radius, n, style):
        prims.cylinder(self.mb, (p.x, self.v(x0), p.z), (p.x, self.v(x1), p.z), radius, n, style)


def wide_train(mb, asset, name, v_rope, rope_w, pin_w, a, tr, count, mode, radius, lod, st, top_name=None):
    """One rope's train hanging from the pin at (0, pin_w); returns the rockers (for walkway panels: name, pin, mid,
    d, n, axles) and the top's name. mode "support" or "hold"."""
    top, axles, rockers, sg = build_tree(a, tr, count, mode, radius, rope_w, name)
    _shift(top, -top["pin"].x)
    top["pin"] = Vector((0.0, 0.0, pin_w))
    if top_name:
        top["name"] = top_name
    axles = [p for rk in rockers for p in rk["axles"]]
    row = _Row(mb, v_rope)
    for node, parent in _walk(top):   # the pivots, the same at every LOD
        p = node["pin"]
        asset.pivots[node["name"]] = {"pos": (p.x, v_rope, p.z), "axis": (0.0, 1.0, 0.0)}
        if parent:
            asset.pivots[node["name"]]["parent"] = parent["name"]
        if node["kind"] == "rocker":
            asset.pivots[node["name"]]["stop"] = tr["rocker"]["stop"]
            for k, ax in zip(node["ks"], node["axles"]):
                asset.pivots[f"sheave_{name}{k + 1}"] = {"pos": (ax.x, v_rope, ax.z), "axis": (0.0, 1.0, 0.0),
                                                        "parent": node["name"]}
    draw(row, top, rockers, axles, name, a, tr, count, mode, lod, st)
    return rockers, top["name"]


def combo_train(mb, asset, name, v_rope, rope_w, pin_w, a, tr, count, radius, lod, st):
    """A combination: a support row under the rope and a hold-down row over it (count sheaves each), each on a 4-beam,
    both hanging from block_<name> at (0, pin_w) on twin plates."""
    rows = []
    for tag, mode, yk in (("s", "support", "sup"), ("h", "hold", "hold")):
        top, axles, rockers, sg = build_tree(a, tr, count, mode, radius, rope_w, name + tag)
        b4 = top["kids"]   # a 4-sheave row: the "yoke" is the two rockers' carrier: make it a 4-beam
        d, n = _frame(b4[0]["pin"], b4[1]["pin"], sg)
        beam = _node("beam4", f"yoke_{yk}_{name}", (b4[0]["pin"] + b4[1]["pin"]) / 2 + n * tr["beam4"]["pinDrop"], 4, b4,
                     d=d, n=n)
        rows.append((beam, rockers, mode))
    du = -(rows[0][0]["pin"].x + rows[1][0]["pin"].x) / 2
    for beam, _, _ in rows:
        _shift(beam, du)
    top = _node("block", f"block_{name}", Vector((0.0, 0.0, pin_w)), 2 * count, [rows[0][0], rows[1][0]])
    row = _Row(mb, v_rope)
    for node, parent in _walk(top):
        p = node["pin"]
        asset.pivots[node["name"]] = {"pos": (p.x, v_rope, p.z), "axis": (0.0, 1.0, 0.0)}
        if parent:
            asset.pivots[node["name"]]["parent"] = parent["name"]
        if node["kind"] == "rocker":
            asset.pivots[node["name"]]["stop"] = tr["rocker"]["stop"]
            nm = node["name"][len("rocker_"):].rstrip("0123456789")
            for k, ax in zip(node["ks"], node["axles"]):
                asset.pivots[f"sheave_{nm}{k + 1}"] = {"pos": (ax.x, v_rope, ax.z), "axis": (0.0, 1.0, 0.0),
                                                      "parent": node["name"]}
    out = []
    for beam, rockers, mode in rows:
        axles = [p for rk in rockers for p in rk["axles"]]
        nm = rockers[0]["name"][len("rocker_"):].rstrip("0123456789")
        draw(row, beam, rockers, axles, nm, a, tr, count, mode, lod, st, top_name=top["name"])
        out += rockers
    draw_top(row, top, tr, lod, st, "hold", top["name"])
    return out, top["name"]


def _layers(kind, mode, tr):
    """x extent (inboard from the rope) of a member's steel, for the pins through it."""
    if kind == "rocker":
        x = tr["rocker"]["x"]
        return -x[1], x[1]
    if kind == "beam4":
        x = tr["beam4"]["x"]
        return (-x[1] if mode == "support" else x[0]), x[0] + tr["beam4"]["boss"][1]
    if kind == "beam6":
        return tuple(tr["beam6"]["x"])
    return tr["yoke"]["x"][0], tr["yoke"]["x"][1]


def draw(row, top, rockers, axles, name, a, tr, count, mode, lod, st, top_name=None):
    """Sheaves and members of one row under `top` (a yoke, or a combination's 4-beam drawn below its block; no rope
    horns there, they would reach the other row)."""
    s = a["sheave"]
    r = s["dia"] / 2
    sg = -1.0 if mode == "support" else 1.0
    tname = top_name or top["name"]
    steel = st["steel"]
    rk = tr["rocker"]
    if lod >= 3:   # one block for the row and the top member's box
        p0, p1 = axles[0], axles[-1]
        d, n = _frame(p0, p1, sg)
        with row.mb.grouped(tname):
            mid = sum(axles, Vector()) / len(axles) + n * 0.05   # on the sheaves' centroid (a curved row sags below its chord)
            row.obox(mid, d, n, ((p1 - p0).length + 2 * r) / 2, r + 0.05, -s["width"] / 2, rk["x"][1], steel)
            if top["kind"] == "yoke":
                draw_top(row, top, tr, lod, st, mode, tname)
        return
    n_side = sides(lod, *a["sides"])
    for k, c in enumerate(axles):
        rk_name = rockers[k // 2]["name"]
        with row.mb.grouped(f"sheave_{name}{k + 1}" if lod == 0 else rk_name if lod == 1 else tname):
            line_sheave(row.mb, Vector((c.x, row.v_rope, c.z)), V, s, lod, st, n_side, row_face(k, count, st))
    if lod >= 2:   # far out: one bar along the rockers, the top member's box
        p0, p1 = axles[0], axles[-1]
        d, n = _frame(p0, p1, sg)
        with row.mb.grouped(tname):
            row.obox((p0 + p1) / 2 + n * 0.12, d, n, ((p1 - p0).length + 0.3) / 2, 0.1, rk["x"][0], tr["beam4"]["x"][1], steel)
            if top["kind"] == "yoke":
                draw_top(row, top, tr, lod, st, mode, tname)
        return
    h = tr["pitch"] / 2
    for i, node in enumerate(rockers):   # rockers: twin shaped plates (the outboard one thin on a hold-down row)
        with row.mb.grouped(node["name"]):
            poly = rocker_outline(h, lod, rk["pinDrop"])
            for x0, x1 in ((-rk["x"][1], -rk["x"][0]), (rk["x"][0], rk["x"][1])):
                row.plate(poly, node["mid"], node["d"], node["n"], x0, x1, steel)
            if rk.get("horn") and top_name is None and i in (0, len(rockers) - 1):   # the rope horn at the row's ends, inboard plate
                e = -1.0 if i == 0 else 1.0
                horn = [(e * (h + 0.04), 0.02), (e * (h + 0.11), 0.02), (e * (h + 0.34), -rk["horn"]), (e * (h + 0.29), -rk["horn"] - 0.02)]
                row.plate(horn, node["mid"], node["d"], node["n"], rk["x"][0], rk["x"][1], steel)
        if lod == 0:
            with row.mb.grouped(node["name"] + "#axle"):
                for p in node["axles"]:   # the axle shows only as its nuts (the spacers below hide the rest)
                    for x0, x1, cp in ((-rk["x"][1] - 0.014, -rk["x"][1], (True, False)), (rk["x"][1], rk["x"][1] + 0.014, (False, True))):
                        prims.cylinder(row.mb, (p.x, row.v(x0), p.z), (p.x, row.v(x1), p.z), 0.042, 6, st["rod"], caps=cp)   # the axle nuts
        if lod == 0:   # hub spacers from each sheave to both plates, as the reference's hubs run through them (Gate 1 v5)
            with row.mb.grouped(node["name"]):
                hw = s["width"] / 2
                for p in node["axles"]:
                    for x0, x1 in ((-rk["x"][0] - 0.002, -hw + 0.01), (hw - 0.01, rk["x"][0] + 0.002)):
                        prims.cylinder(row.mb, (p.x, row.v(x0), p.z), (p.x, row.v(x1), p.z), s["hub"] / 2 + 0.01,
                                       6, steel, caps=(False, False))
    for node, parent in _walk(top):
        if node["kind"] == "beam4":
            draw_beam4(row, node, tr, mode, lod, st)
        elif node["kind"] == "beam6":
            with row.mb.grouped(node["name"]):
                k0, k1 = node["kids"][0]["pin"], node["kids"][1]["pin"]
                d, n = _frame(k0, k1, sg)
                b6 = tr["beam6"]
                row.obox((k0 + k1) / 2, d, n, (k1 - k0).length / 2 + b6["overhang"], b6["depth"] / 2, b6["x"][0], b6["x"][1], steel)
        if parent is not None and lod == 0:   # the pin through the child and its parent, a nut outboard on support rows
            c0, c1 = _layers(node["kind"], mode, tr)
            p0, p1 = _layers("beam6" if parent["name"].startswith("yoke_hold_") else parent["kind"], mode, tr)
            pr = tr["rocker"]["pin"] / 2 if node["kind"] == "rocker" else tr["pin"]["dia"] / 2
            lo_, hi_ = min(c0, p0) - 0.012, max(c1, p1) + 0.012
            with row.mb.grouped(node["name"] + "#pin"):
                row.rod(node["pin"], lo_, hi_, pr, 8, st["rod"])
                if mode == "support" and node["kind"] == "rocker" and lo_ < -0.1:
                    ph = tr["pinHead"]
                    row.rod(node["pin"], lo_ - ph[1] + 0.012, lo_, ph[0] / 2, 6, st["rod"])
    if top["kind"] == "yoke":
        draw_top(row, top, tr, lod, st, mode, tname)


def draw_beam4(row, node, tr, mode, lod, st):
    b4 = tr["beam4"]
    k0, k1 = node["kids"][0]["pin"], node["kids"][1]["pin"]
    d, n = node["d"], node["n"]
    half = (k1 - k0).length / 2 + b4["overhang"]
    top_ = -(b4["pinDrop"] + b4["depth"][0] / 2)
    deep = b4["depth"] if mode == "support" else b4["holdDepth"]
    end = top_ + deep[0]
    belly = top_ + deep[1]
    poly = [(-half, top_), (half, top_), (half, end), (half * 0.4, belly), (-half * 0.4, belly), (-half, end)] if lod == 0 else \
        [(-half, top_), (half, top_), (half, end), (0.0, belly), (-half, end)]
    layers = [(b4["x"][0], b4["x"][1])] + ([(-b4["x"][1], -b4["x"][0])] if mode == "support" else [])
    slot = node["name"].startswith("yoke_hold_")   # a combination's hold-down beam: one box between the end block's cheeks
    if slot:                                       # (owner, Gate 2: the c8 hung too low), so the row can rise beside them
        layers = [tuple(tr["beam6"]["x"])]
        poly = [(-half, top_), (half, top_), (half, top_ + deep[0]), (-half, top_ + deep[0])]
    with row.mb.grouped(node["name"]):
        for x0, x1 in layers:
            row.plate(poly, node["pin"], d, n, x0, x1, st["steel"])
        if lod == 0 and not slot:   # the boss inboard on its pin
            row.rod(node["pin"], b4["x"][1], b4["x"][1] + b4["boss"][1], b4["boss"][0] / 2, 10, st["steel"])


def draw_top(row, top, tr, lod, st, mode, tname):
    """The top member: a yoke's box through the hanging pin with drop legs to its children (bosses at their pins), or
    a combination block's twin hanger plates down past both rows."""
    yk = tr["yoke"]
    steel = st["steel"]
    pin = top["pin"]
    kids = [k["pin"] for k in top["kids"]]
    x0, x1 = yk["x"]
    pl = yk["plate"]
    with row.mb.grouped(tname):
        if top["kind"] == "block":
            deep = max(pin.z - k.z for k in kids) + yk["boss"] / 2
            hw0, hw1 = yk["leg"] * 0.9, yk["leg"] / 2
            if lod >= 2:
                polys = [[(-hw0, -0.12), (hw0, -0.12), (hw1, deep), (-hw1, deep)]]
            else:   # an open frame (review T1 verify: not a post): two straps, round pads at the pin and both rows' pins
                n_c = 12 if lod == 0 else 6
                pads = [0.0] + [pin.z - k.z for k in kids]
                polys = [[(rr * math.cos(t), dz + rr * math.sin(t)) for t in (2 * math.pi * j / n_c for j in range(n_c))]
                         for dz, rr in zip(pads, (0.13, yk["boss"] / 2 + 0.02, yk["boss"] / 2 + 0.02))]
                sa, sb = 0.055, 0.125
                bot = max(pads)
                polys += [[(e * sa, 0.0), (e * sb, 0.0), (e * sb, bot), (e * sa, bot)] for e in (-1, 1)]
            for poly in polys:
                for xa, xb in ((x0, x0 + pl), (x1 - pl, x1)):
                    row.plate(poly, pin, U, -W, xa, xb, steel)
            if lod <= 1:   # a cross plate between them at the top
                row.obox(pin + W * 0.06, U, W, hw0, 0.09, x0 + pl, x1 - pl, steel)
            if lod == 0:
                for kid in top["kids"]:
                    held = kid["name"].startswith("yoke_hold_")   # the hold-down row's pin sits right under the cheeks:
                    if not held:                                  # no boss outside the plates there, the pin flush
                        row.rod(kid["pin"], x1, x1 + 0.02, yk["boss"] / 2, 10, steel)
                    with row.mb.grouped(kid["name"] + "#pin"):
                        x_in = tr["beam6"]["x"][0] if held else _layers("beam4", "hold", tr)[0]
                        row.rod(kid["pin"], x0 + 0.002 if held else min(x_in, x0) - 0.012, x1 + (-0.002 if held else 0.012),
                                tr["pin"]["dia"] / 2, 8, st["rod"])
            return
        d = (kids[1] - kids[0])
        d = Vector((d.x, 0.0, d.z)).normalized()
        if d.x < 0:
            d = -d
        down = Vector((-d.z, 0.0, d.x))
        if down.z > 0:
            down = -down
        lift = 0.0 if mode == "support" else yk["holdLift"]
        ts = [(k - pin).dot(d) for k in kids]
        t0, t1 = min(ts) - yk["overhang"], max(ts) + yk["overhang"]
        hd = (yk["depth"] if mode == "support" else yk["holdDepth"]) / 2   # shallower over a hold-down row: room to turn
        centre = pin + d * ((t0 + t1) / 2) - down * lift
        row.obox(centre, d, down, (t1 - t0) / 2, hd, x0, x1, steel)
        bottom = hd - lift   # the box's underside, measured down from the pin
        for k, t in zip(kids, ts):
            depth = (k - pin).dot(down)
            if depth <= bottom - 0.05:
                continue
            foot = pin + d * t + down * (depth + (0.06 if lod <= 1 else 0.0))
            head = pin + d * t + down * (bottom - 0.02)
            c = (foot + head) / 2
            half_n = (foot - head).length / 2
            for xa, xb in ((x0, x0 + pl), (x1 - pl, x1)):
                row.obox(c, d, down, yk["leg"] / 2, half_n, xa, xb, steel)
            if lod == 0:
                row.rod(k, x1, x1 + 0.02, yk["boss"] / 2, 10, steel)
