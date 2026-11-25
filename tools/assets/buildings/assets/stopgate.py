"""Fixed-grip top returns' stop gates (stopgate.json; task 4.50 phase 4): bldgkit/stop_gate.py builds them, one per
terminal style. build_buildings.py names them gate_sessellift, gate_stern and gate_monta (or all three as
`stopgate`); variant() gives each a module-like handle. A post on the ramp past the wheel and a bar along the line,
at the place bldgkit/unload_ramp.gate_at() gives for the lift's terminal."""
from types import SimpleNamespace

from bldgkit import stop_gate

KIND = "stop_gate"
SPEC = "stopgate.json"
VARIANTS = ("sessellift", "stern", "monta")


def variant(name):
    return SimpleNamespace(KIND=KIND, LODS=stop_gate.LODS, SPEC=SPEC,
                           pick=lambda spec: stop_gate.pick(spec, name),
                           build=lambda spec, lod, stage=None: stop_gate.build(spec, lod))
