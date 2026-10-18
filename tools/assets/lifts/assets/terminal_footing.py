"""Sessellift FGQ-4 terminal footing (task P3-07, findings #5 and #8): the concrete footing under either terminal's pier
(both terminals' footings are the same block, sessellift_fgq4.json "drive.footing" and "return.footing"), as its own
prefab. The terminals' piers are really tall (owner) and run on below grade, so a raised or lowered terminal always
stands on its pier; the game places this footing at the ground or the terminal's pad, origin at grade, centred under
the pier. Sockets: foundation_base (the footing's underside, where the terminals' own socket of that name used to be)
and pier_seat (its top, where the pier stands in it).
"""
from liftkit import prims
from liftkit.export import Asset
from liftkit.mesh import MeshBuilder, Style

KIND = "terminal_footing"
LODS = 1


def m(x):
    return x / 1000.0


def build(spec, lod, stage):
    fo = spec["drive"]["footing"]
    if fo != spec["return"]["footing"]:
        raise ValueError("the drive's and the return's footings differ: build one footing prefab each")
    a = Asset("sessellift_fgq4_terminal_footing")
    mb = MeshBuilder()
    hu, hv = m(fo["u"]) / 2, m(fo["v"]) / 2
    prims.box(mb, (-hu, -hv, m(fo["bottom"])), (hu, hv, m(fo["top"])), Style("concrete", snow=0.9), skip=("-2",))
    a.body = mb
    a.sockets = {"foundation_base": (0.0, 0.0, m(fo["bottom"])), "pier_seat": (0.0, 0.0, m(fo["top"]))}
    a.dims.update({"footingU": fo["u"], "footingV": fo["v"], "footingTop": fo["top"], "footingBottom": fo["bottom"]})
    return a
