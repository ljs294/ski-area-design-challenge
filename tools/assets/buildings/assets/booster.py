"""The booster (booster.json; task 4.50 phase 2): the game's Inline pump on a snowmaking main, 30 x 20 ft, from the
shared metal-panel builder (bldgkit/metal_building.py). The main runs straight through: in at the west gable (inlet),
out at the east (discharge)."""
from bldgkit import metal_building

KIND = "booster"
LODS = metal_building.LODS
SPEC = "booster.json"
ASSET_ID = "bldg_booster"


def build(spec, lod, stage=None):
    return metal_building.build(spec, lod, ASSET_ID)
