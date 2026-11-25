"""Lift operator huts (task 4.50 phase 3): three assets from one spec, lifthut.json (bldgkit/lift_hut.py builds them).
build_buildings.py names them hut_large, hut_small and hut_mini (or all three as `lifthut`); variant() gives each a
module-like handle."""
from types import SimpleNamespace

from bldgkit import lift_hut

KIND = "lift_hut"
SPEC = "lifthut.json"
VARIANTS = ("large", "small", "mini")


def variant(name):
    return SimpleNamespace(KIND=KIND, LODS=lift_hut.LODS, SPEC=SPEC, WALL=lift_hut.WALL,
                           pick=lambda spec: lift_hut.pick(spec, name),
                           build=lambda spec, lod, stage=None: lift_hut.build(spec, lod))
