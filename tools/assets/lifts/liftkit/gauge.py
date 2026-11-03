"""Overlay specs (v0.3.2, the fixed-grip doubles and triples): a small spec file names its "base" (the quad's spec it
is derived from), an "idPrefix" for its assets, the "model" (seats), its own "catalog" and the values it changes, such
as common.lineGauge. load() merges it onto a copy of the base; the quad's own spec is never touched, so the quads
build exactly as before.

The Sessellift gauge rule (owner LD5, Q1; 2026-11-01): at a narrower gauge the terminals' hood, piers, housing,
walkways, machinery and carriage keep the quad's size, and everything that carries or guides the rope moves in with
it by ds = (gauge - quad's gauge) / 2 per side, its own size unchanged: the crossbeams and lifting portals (eyes over
the trains, as on the quad), the entry platforms, the sheave rows' plates, the chair guide and rope guard, and the
bullwheels' rims and top plates (their section kept, the diameter following the gauge; hub and spokes as they are).
RULE lists every spec value it moves: "shift" moves a half-width (or each one of a list) out by ds, keeping its sign;
"width" changes a full width or diameter by 2 ds; "pathV" shifts the v of each (u, v) point; "outer" shifts the outer
end of each (v0, v1) span; "farV" shifts the v of each diagonal's second (u, v) point; "radius" shifts the first
value of a (radius, w) point. The Monta has its own rule (monta_drive.gauge_shift, gauge_wheel).
"""
import copy
import json
import os


def merge(base, over):
    """over's values onto base (a copy): dictionaries merge key by key, anything else is replaced. The catalog is
    always replaced whole: an overlay names only its own assets."""
    out = copy.deepcopy(base)
    for k, v in over.items():
        if isinstance(v, dict) and isinstance(out.get(k), dict) and k != "catalog":
            out[k] = merge(out[k], v)
        else:
            out[k] = copy.deepcopy(v)
    return out


RULE = {
    "width": ("common.bullwheel.pitch", "common.bullwheel.plateOuter", "common.bullwheel.plateInner",
              "drive.crossbeam.v", "drive.portal.beam.v", "drive.entryPlatform.v",
              "return.bullwheel.plateOuter", "return.bullwheel.plateInner",
              "return.liftingFrame.crossbeam.v", "return.liftingFrame.portal.v", "return.liftingFrame.platform.v",
              "return.guideSheaves.cross.v"),
    "shift": ("common.bullwheel.stubs.reach",
              "drive.crossbeam.footPlates.v", "drive.portal.leg.footV", "drive.portal.leg.headV", "drive.portal.lugs.v",
              "drive.portal.jRails.to", "drive.entryTrains.plate.v",
              "return.liftingFrame.crossbeam.stub.to", "return.liftingFrame.portal.leg.foot",
              "return.liftingFrame.portal.leg.head", "return.liftingFrame.portal.footPlates.v",
              "return.liftingFrame.portal.footPlates.boltV", "return.liftingFrame.portal.lugs.v",
              "return.liftingFrame.platform.sides", "return.liftingFrame.platform.braces.v",
              "return.liftingFrame.platform.hoop.v",
              "return.guideSheaves.hangers.v", "return.ropeGuard.v", "return.ropeGuard.hangerV",
              "return.chairGuide.hangers.vFrom", "return.chairGuide.hangers.vTo", "return.chairGuide.hangers.legV",
              "return.chairGuide.hangers.dropV", "return.chairGuide.hangers.outerBracketV",
              "return.chairGuide.hangers.innerBracketV", "return.chairGuide.beam.v",
              "tower.head.platformHalfV"),
    "pathV": ("return.chairGuide.outer.path", "return.chairGuide.inner.path"),
    "outer": ("return.overhead.spans",),
    "farV": ("return.overhead.diagonals",),
    "radius": ("return.bullwheel.lugs.from", "return.bullwheel.lugs.to"),
}


def _out(x, d):
    """x moved d further from 0 (its sign kept)."""
    return x + d if x >= 0 else x - d


def _apply(op, x, ds):
    if op == "width":
        return x + 2 * ds
    if op == "shift":
        return [_out(y, ds) for y in x] if isinstance(x, list) else _out(x, ds)
    if op == "pathV":
        return [[u, _out(v, ds)] for u, v in x]
    if op == "outer":
        return [[a, _out(b, ds)] if abs(b) >= abs(a) else [_out(a, ds), b] for a, b in x]
    if op == "farV":
        return [[p0, [p1[0], _out(p1[1], ds)]] for p0, p1 in x]
    if op == "radius":
        return [_out(x[0], ds)] + x[1:]
    raise ValueError(op)


def narrow(spec, gauge, base_gauge):
    """The Sessellift spec moved to `gauge` (mm) by RULE, in place."""
    ds = (gauge - base_gauge) / 2
    for op, paths in RULE.items():
        for path in paths:
            *head, last = path.split(".")
            node = spec
            for k in head:
                node = node[k]
            node[last] = _apply(op, node[last], ds)
    return spec


# The Sessellift D4 detachable quad (owner DQ1, DQ2, Gate 0): the D6's station, with one 3,200 mm portal bay (four
# 800 mm window bays) taken out of its line-end straight, ahead of D6_CUT (between the portals at 978 and 4,178 from the
# bullwheel's axle), and each end moved along the line by its own small shift (overlay "stations": from, shift). Every u
# of an end section ahead of the cut moves by shift - D6_BAY, every u behind it by shift. Across the station, RULE_D6
# moves everything that carries or guides the rope and the grips in with the gauge by ds per side, as RULE does for the
# fixed grips (the chair rail, tyre banks, portals' legs, horns, open rails, the floor's outer edge, the belly's tray,
# the line openings; the bullwheel's plates); the hood keeps its section and moves its side in by dh, the measured hood
# half-width less the D6's (terminal.hood.halfWidth in the overlay).
D6_BAY = 3200
D6_CUT = 3500
END_U = {
    "value": ("rail.end", "ropeBend", "belly.front", "belly.keelTop", "frontMast.u"),
    "list": ("hood", "windows", "bendSheaves.u", "belly.backs", "belly.wedge", "frame.beamU", "frame.overheadU",
             "frame.floorU", "carriage.u", "carriage.cross", "carriage.rollers", "rear.bottom",
             "rear.top", "rear.head", "rear.saddle", "rear.plate", "rear.bolts", "rear.sideCover.u", "pierFront.u",
             "pierRear.u", "pierRear.upstand.u", "landing.u"),
    "portals": ("frame.portals",),
    "first": ("riders", "rail.corner"),
    "points": ("frame.posts", "rear.saddleCurve"),
}
RULE_D6 = {
    "width": ("common.bullwheel.pitch", "common.bullwheel.plateOuter", "common.bullwheel.plateInner"),
    "shift": ("terminal.rail.v", "terminal.frame.tyreBank.v", "terminal.frame.tyreBank.guard.v",
              "terminal.frame.tyreBank.beam.v", "terminal.frame.openRails.v", "terminal.frame.portals.inner",
              "terminal.frame.portals.outer", "terminal.horns.vInner", "terminal.horns.vOuter",
              "terminal.hood.lineOpening.v"),
    "firsts": ("terminal.frame.portals.chamfer",),
    "second": ("terminal.frame.floor.v",),
    "tray": ("terminal.belly.section",),
}


def _node(spec, path):
    *head, last = path.split(".")
    node = spec
    for k in head:
        node = node[k]
    return node, last


def _bay(x, du):
    return x + du if x <= D6_CUT else x - D6_BAY + du


def station(section, du):
    """A D6 end section with the bay taken out and moved du along the line (END_U)."""
    sec = copy.deepcopy(section)
    for kind, paths in END_U.items():
        for path in paths:
            try:
                node, last = _node(sec, path)
                x = node[last]
            except KeyError:
                continue
            if kind == "value":
                node[last] = _bay(x, du)
            elif kind == "list":
                node[last] = [_bay(y, du) for y in x]
            elif kind == "portals":   # the taken-out bay's portal lands on the next one's (within a few mm): one of them
                out = []
                for y in map(lambda y: _bay(y, du), x):
                    if not out or abs(y - out[-1]) > 5:
                        out.append(y)
                node[last] = out
            elif kind == "first":
                node[last] = [_bay(x[0], du)] + x[1:]
            else:
                node[last] = [[_bay(p[0], du)] + p[1:] for p in x]
    w0 = section["windows"][0]
    sec["flipBays"] = [dict(f, bay=f["bay"] - (D6_BAY // 800 if w0 + 800 * f["bay"] > D6_CUT else 0)) for f in section["flipBays"]]
    return sec


def quad_station(spec, base, over):
    """The D6 spec moved to the D4 (in place): each end from its D6 section by station(), the overlay's own values for
    that end on top; the cross-section by RULE_D6 and the hood's side by dh."""
    t = spec["terminal"]
    for end, how in over["stations"].items():
        if end.startswith("_"):
            continue
        t[end] = merge(station(base["terminal"][how["from"]], how["shift"]), over.get("terminal", {}).get(end, {}))
        t[end]["load"] = how["load"]
        t["bullwheel"]["w"][end] = base["terminal"]["bullwheel"]["w"][how["from"]]
    ds = (spec["common"]["lineGauge"] - base["common"]["lineGauge"]) / 2
    for op, paths in RULE_D6.items():
        for path in paths:
            node, last = _node(spec, path)
            x = node[last]
            if op == "width":
                node[last] = x + 2 * ds
            elif op == "shift":
                node[last] = [_out(y, ds) for y in x] if isinstance(x, list) else _out(x, ds)
            elif op == "firsts":
                node[last] = [[_out(p[0], ds)] + p[1:] for p in x]
            elif op == "second":
                node[last] = [x[0], _out(x[1], ds)]
            else:   # the belly's tray (its top two points) in, the keel under the wheel's hub as it is
                node[last] = [[_out(p[0], ds)] + p[1:] if i < 2 else p for i, p in enumerate(x)]
    h = t["hood"]
    dh = h["halfWidth"] - base["terminal"]["hood"]["outline"][-1][0]
    h["outline"] = [[v + dh, w] for v, w in h["outline"]]
    h["arc"]["centre"][0] += dh
    h["arc"]["to"][0] += dh
    h["plan"] = [[v + dh if v else v, du] for v, du in h["plan"]]
    ew = h["endWindow"]
    k = (ew["frame"][0] + dh) / ew["frame"][0]   # the end walls' windows narrow with the wall
    ew["frame"][0] += dh
    ew["glass"][0] += dh
    ew["mullions"] = [[round(v * k), w] for v, w in ew["mullions"]]
    ew["guard"]["half"] = round(ew["guard"]["half"] * k)
    return spec


def load(folder, name):
    """The overlay spec `name` (a file in folder) merged onto its base; a Sessellift overlay at another gauge is moved
    there by RULE, the D4 by quad_station."""
    over = json.load(open(os.path.join(folder, name), encoding="utf-8"))
    base = json.load(open(os.path.join(folder, over["base"]), encoding="utf-8"))
    spec = merge(base, {k: v for k, v in over.items() if k not in ("base", "stations", "kit")})
    if "kit" in over:   # the line towers' assembly of another spec (the D4's integrated tower: the quad kit's rows)
        kit = json.load(open(os.path.join(folder, over["kit"]), encoding="utf-8"))
        spec["tower"] = {"assembly": kit["tower"]["assembly"]}
    base_gauge = base["common"]["lineGauge"]
    if over["base"] == "sessellift_fgq4.json" and spec["common"]["lineGauge"] != base_gauge:
        narrow(spec, spec["common"]["lineGauge"], base_gauge)
    if over["base"] == "sessellift_d6.json" and "stations" in over:
        quad_station(spec, base, over)
    return spec
