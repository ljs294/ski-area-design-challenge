"""Timber unload ramps (task 4.50 phase 4): three assets from one spec, unloadramp.json (bldgkit/unload_ramp.py builds
them). build_buildings.py names them ramp_quad, ramp_triple and ramp_double (or all three, with the operator's
pedestal and the stop gate, as `unloadramp`); variant() gives each a module-like handle. Each is one model for every
lift of its chair width: its build checks it against every terminal in the spec's table."""
from types import SimpleNamespace

from bldgkit import unload_ramp

KIND = "unload_ramp"
SPEC = "unloadramp.json"
VARIANTS = ("quad", "triple", "double", "quad_wide", "triple_wide", "double_wide")


def variant(name):
    return SimpleNamespace(KIND=KIND, LODS=unload_ramp.LODS, SPEC=SPEC,
                           pick=lambda spec: unload_ramp.pick(spec, name),
                           build=lambda spec, lod, stage=None: unload_ramp.build(spec, lod))
