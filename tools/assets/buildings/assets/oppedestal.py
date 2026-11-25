"""The lift operator's outdoor pedestal with a phone (oppedestal.json; task 4.50 phase 4): bldgkit/op_pedestal.py
builds it. It stands on the unload ramp's hut bay, at the ramp's `pedestal` socket."""
from bldgkit import op_pedestal

KIND = "op_pedestal"
LODS = op_pedestal.LODS
SPEC = "oppedestal.json"


def build(spec, lod, stage=None):
    return op_pedestal.build(spec, lod)
