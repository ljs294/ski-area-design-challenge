"""An outdoor lift operator's pedestal with a phone, from its spec (task 4.50 phase 4; oppedestal.json).

The owner asked for it on the unload ramp (2026-11-24): where the operator stands outside beside the unload point, a
steel post with a control head (a red stop button on a yellow plate, a slow button) and a weatherproof phone box on
the post's side, its conduit running down into the deck. Its own small asset, so it can stand at other stations too;
the unload ramp places it at its `pedestal` socket.

Frame (millimetres in the spec, metres here): the origin is the middle of the base on the deck; +v is the side the
operator stands on (the control head's sloped face looks up toward +v), so the operator looks over it toward -v, at
the chairs; u runs along the lift line and w is up. The phone box hangs on the post's -u side, its door toward +v.

LOD0 everything; LOD1 the post, head, stop button and phone box; LOD2 the post, head and phone box as plain boxes;
LOD3 one box. Sockets: `stop` (the stop button's top) and `phone` (the phone box's door).
"""
from liftkit import prims
from liftkit.export import Asset
from liftkit.frame import mm
from liftkit.mesh import MeshBuilder, Style

LODS = 4


def styles():
    return {
        "post": Style("galvanised", snow=0.0), "head": Style("paint_grey", snow=0.6),
        "plate": Style("safety_yellow", snow=0.0), "stop": Style("sheave_red", snow=0.0),
        "slow": Style("paint_dark", snow=0.0), "phone": Style("safety_yellow", snow=0.5),
        "door": Style("paint_dark", snow=0.0),
    }


def build(spec, lod, asset_id=None):
    st = styles()
    a = Asset(asset_id or spec["id"])
    mb = MeshBuilder()
    a.lights, a.glow, a.problems = [], {}, []
    H = mm(spec["height"])
    p2 = mm(spec["post"]) / 2
    hd, ph = spec["head"], spec["phone"]
    hw2, hd2 = mm(hd["width"]) / 2, mm(hd["depth"]) / 2
    h_back, h_front = mm(hd["back"]), mm(hd["front"])     # the head's height at its far (-v) and near (+v) edges
    top_back, top_front = H + h_back, H + h_front
    pw2, pd, pz0, pz1 = mm(ph["width"]) / 2, mm(ph["depth"]), mm(ph["bottom"]), mm(ph["bottom"]) + mm(ph["height"])
    pu = -p2 - pw2                                        # the phone box's middle, on the post's -u side

    if lod == 3:
        prims.box(mb, (pu - pw2, -hd2, 0.0), (hw2, hd2, top_back), st["head"], skip=("-2",))
    else:
        if lod == 0:
            b2 = mm(spec["base"]) / 2
            prims.box(mb, (-b2, -b2, 0.0), (b2, b2, 0.012), st["post"], skip=("-2",))
        prims.box(mb, (-p2, -p2, 0.0), (p2, p2, H), st["post"], skip=("-2", "+2"))
        # the control head: a box whose top slopes down toward the operator (+v)
        poly = [(-hd2, H), (hd2, H), (hd2, top_front), (-hd2, top_back)]
        prims.prism(mb, poly, (-hw2, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0), 2 * hw2, st["head"])
        prims.box(mb, (pu - pw2, -pd / 2, pz0), (pu + pw2, pd / 2, pz1), st["phone"])
    # the sloped face: a point `s` across it (0 at its far edge, 1 at its near edge), `out` proud of it
    run, drop = 2 * hd2, h_back - h_front
    ln = (run ** 2 + drop ** 2) ** 0.5
    n = (0.0, drop / ln, run / ln)

    def on_face(u, s, out=0.0):
        return (u + n[0] * out, -hd2 + s * run + n[1] * out, top_back - s * drop + n[2] * out)
    stop_u, slow_u = -hw2 * 0.4, hw2 * 0.5
    stop_h = mm(hd["stop"][1])
    if lod <= 1:
        prims.cylinder(mb, on_face(stop_u, 0.5), on_face(stop_u, 0.5, stop_h), mm(hd["stop"][0]) / 2,
                       10 if lod == 0 else 6, st["stop"], caps=(False, True))
    if lod == 0:
        r_pl = mm(hd["stop"][0]) / 2 + 0.02
        prims.cylinder(mb, on_face(stop_u, 0.5), on_face(stop_u, 0.5, 0.004), r_pl, 12, st["plate"], caps=(False, True))
        prims.cylinder(mb, on_face(slow_u, 0.5), on_face(slow_u, 0.5, mm(hd["slow"][1])), mm(hd["slow"][0]) / 2, 8,
                       st["slow"], caps=(False, True))
        # the phone box's door (toward the operator), its hood and the conduit down the post
        prims.box(mb, (pu - pw2 + 0.02, pd / 2, pz0 + 0.02), (pu + pw2 - 0.02, pd / 2 + 0.008, pz1 - 0.02), st["door"],
                  skip=("-1",))
        prims.box(mb, (pu - pw2 - 0.01, -pd / 2 - 0.01, pz1), (pu + pw2 + 0.01, pd / 2 + 0.03, pz1 + 0.015), st["phone"])
        prims.cylinder(mb, (pu, -pd / 2 + 0.03, 0.0), (pu, -pd / 2 + 0.03, pz0), 0.014, 6, st["post"], caps=(False, False))

    if pz0 < 0.5:
        a.problems.append("the phone box hangs under 0.5 m: snow buries it")
    if not 0.9 <= H <= 1.2:
        a.problems.append(f"the control head sits at {H:.2f} m, outside a standing hand's reach (0.9 to 1.2 m)")
    a.body = mb
    a.dims.update({"height": round(top_back * 1000), "head": [hd["width"], hd["depth"]], "lights": 0})
    a.sockets["stop"] = on_face(stop_u, 0.5, stop_h)
    a.sockets["phone"] = (pu, pd / 2, (pz0 + pz1) / 2)
    return a
