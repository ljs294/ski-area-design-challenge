"""The groomer garage (garage.json; task 4.50 phase 5): a two-bay maintenance shop with its fuel pad, from the shared
metal-panel builder (bldgkit/metal_building.py). build_buildings.py names it `garage`.

The fit check (spec `fit`): each machine passes its bay's door and fits the bay with `spare` on every side, the open
blade passes the door, the roof's underside clears each machine (at its ends, where a slope is lowest) by `spare`,
and the fuel tank and the fuelling machine stand `tankGap` and `laneGap` off the wall. The results go into dims["fit"] (metres
of spare), a shortfall into problems."""
from bldgkit import metal_building

KIND = "garage"
LODS = metal_building.LODS
SPEC = "garage.json"
ASSET_ID = "bldg_garage"


def build(spec, lod, stage=None, asset_id=ASSET_ID):
    a = metal_building.build(spec, lod, asset_id)
    if lod == 0 and spec.get("fit"):
        a.dims["fit"], problems = fit(spec, a.dims.get("fuel"))
        a.problems += problems
    return a


def _gap(box, hx, hy):
    """The least distance from a plan box (u0, u1, v0, v1) to the footprint (0 where they touch or overlap)."""
    du = max(box[0] - hx, -hx - box[1], 0.0)
    dv = max(box[2] - hy, -hy - box[3], 0.0)
    return (du * du + dv * dv) ** 0.5


def fit(spec, fuel_dims=None):
    f = spec["fit"]
    sp, wall = f["spare"], f["wall"]
    doors = {d["name"]: d for d in spec["doors"]}
    bays = len(f["bays"])
    bay_w = f.get("bayWidth", spec["footprint"]["length"] / bays) - wall   # inside, a wall or half a partition off each side
    bay_d = spec["footprint"]["width"] - 2 * wall
    out, problems = {"bayInside": [round(bay_d), round(bay_w)]}, []
    hy, eave, pitch = spec["footprint"]["width"] / 2, spec["eave"], spec["pitch"][0] / spec["pitch"][1]
    hs = {"+v": 1, "-v": -1}.get(spec.get("shed"), 0)

    def under(v):   # the roof's underside over v (mm), a gable's or a single slope's
        return eave - (hy - hs * v) * pitch if hs else eave + (hy - abs(v)) * pitch
    for bay in f["bays"]:
        d = doors[bay["door"]]
        for name, (ln, wd, ht) in f["machines"].items():
            r = {"doorSide": (d["width"] - wd) / 2, "doorHead": d["height"] - ht,
                 "bayEnds": (bay_d - ln) / 2, "baySides": (bay_w - wd) / 2,
                 "roofOver": min(under(ln / 2), under(-ln / 2)) - ht}
            out[f"{bay['name']}/{name}"] = {k: round(v / 1000, 2) for k, v in r.items()}
            for k, v in r.items():
                if v < sp:
                    problems.append(f"{name} in {bay['name']}: {k} spare {v:.0f} mm < {sp} mm")
        blade = (d["width"] - f["openBlade"]) / 2
        out[f"{bay['name']}/openBlade"] = round(blade / 1000, 2)
        if blade < 0:
            problems.append(f"the groomer's open blade doesn't pass {bay['door']}")
    if spec.get("fuel") and fuel_dims:   # the tank and the fuelling machine off the wall (building-frame boxes, m)
        hx, hy = spec["footprint"]["length"] / 2000, spec["footprint"]["width"] / 2000
        for key, need, what in (("tankBox", f["tankGap"], "the fuel tank"), ("machineBox", f["laneGap"], "the fuelling machine")):
            gap = _gap(fuel_dims[key], hx, hy) * 1000
            out[key[:-3] + "Gap"] = round(gap / 1000, 2)
            if gap < need - 1:
                problems.append(f"{what} stands {gap:.0f} mm off the wall, under {need} mm")
    return out, problems

