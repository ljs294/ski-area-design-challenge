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


def load(folder, name):
    """The overlay spec `name` (a file in folder) merged onto its base; a Sessellift overlay at another gauge is moved
    there by RULE."""
    over = json.load(open(os.path.join(folder, name), encoding="utf-8"))
    base = json.load(open(os.path.join(folder, over["base"]), encoding="utf-8"))
    spec = merge(base, {k: v for k, v in over.items() if k != "base"})
    base_gauge = base["common"]["lineGauge"]
    if over["base"] == "sessellift_fgq4.json" and spec["common"]["lineGauge"] != base_gauge:
        narrow(spec, spec["common"]["lineGauge"], base_gauge)
    return spec
