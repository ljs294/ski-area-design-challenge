"""The pump house (pumphouse.json; task 4.50 phase 1): the 4.15 snowmaking pump house, 60 x 40 ft, from the shared
metal-panel builder (bldgkit/metal_building.py). Its water side (-v) takes the intake; the mains leave the east gable."""
from bldgkit import metal_building

KIND = "pumphouse"
LODS = metal_building.LODS
SPEC = "pumphouse.json"
ASSET_ID = "bldg_pumphouse"


def build(spec, lod, stage=None):
    return metal_building.build(spec, lod, ASSET_ID)
