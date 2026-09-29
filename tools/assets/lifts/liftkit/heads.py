"""The Sessellift entry head, shared by the drive terminal's integrated first tower and the line towers (so they
match): the chamfered crossbeam with its foot plates, the lifting portal (legs cut level at both ends, a top beam
with through-bolts, lug plates with eyes, J-handrails) and the entry platform (grated deck on an end member, a U
hoop across its end, struts).

Dimensions come from sessellift_fgq4.json "drive" (crossbeam, portal, entryPlatform), in millimetres in the drive
terminal's frame; `off` (metres, lift frame) moves the whole head, so a line tower can seat it on its mast. The
drive terminal calls these with no offset, in the order its approved mesh was built.
"""
import math

from mathutils import Vector

from . import parts, prims
from .prims import U, V, W


def m(x):
    return x / 1000.0


def crossbeam(mb, cb, lod, st, off=(0.0, 0.0, 0.0)):
    """The crossbeam (section with chamfered long edges at LOD0-1) and the portal legs' foot plates on it."""
    ou, ov, ow = off
    u0c, u1c, wb0, wb1 = m(cb["uFrom"]) + ou, m(cb["uTo"]) + ou, m(cb["bottom"]) + ow, m(cb["top"]) + ow
    hvc = m(cb["v"]) / 2
    if lod <= 1:
        c = m(cb["chamfer"])
        sec = [(u0c + c, wb0), (u1c - c, wb0), (u1c, wb0 + c), (u1c, wb1 - c), (u1c - c, wb1), (u0c + c, wb1), (u0c, wb1 - c), (u0c, wb0 + c)]
        prims.prism(mb, sec, (0, -hvc + ov, 0), U, W, V, 2 * hvc, st["steel"])
    else:
        prims.box(mb, (u0c, -hvc + ov, wb0), (u1c, hvc + ov, wb1), st["steel"])
    fp = cb["footPlates"]
    for s in ((-1, 1) if lod <= 2 else ()):
        prims.box(mb, (m(fp["uFrom"]) + ou, s * m(fp["v"]) - m(fp["halfWidth"]) + ov, m(fp["bottom"]) + ow),
                  (m(fp["uTo"]) + ou, s * m(fp["v"]) + m(fp["halfWidth"]) + ov, m(fp["top"]) + ow), st["steel"])


def portal(mb, po, lod, st, off=(0.0, 0.0, 0.0)):
    """The lifting portal: 152 mm square legs leaning out, cut level on the foot plates and under the beam; the top
    beam with through-bolts; 25 mm lug plates across the beam ends with eyes; J-handrails in the portal plane."""
    ou, ov, ow = off
    bm = po["beam"]
    if lod <= 2:
        lg = po["leg"]
        for s in (-1, 1):
            f0, f1 = sorted((s * m(lg["footV"][0]), s * m(lg["footV"][1])))
            h0, h1 = sorted((s * m(lg["headV"][0]), s * m(lg["headV"][1])))
            poly = [(f0 + ov, m(lg["footW"]) + ow), (f1 + ov, m(lg["footW"]) + ow), (h1 + ov, m(lg["headW"]) + ow), (h0 + ov, m(lg["headW"]) + ow)]
            prims.prism(mb, poly, (m(lg["uFrom"]) + ou, 0, 0), V, W, U, m(lg["uTo"]) - m(lg["uFrom"]), st["steel"])
        prims.box(mb, (m(bm["uFrom"]) + ou, -m(bm["v"]) / 2 + ov, m(bm["bottom"]) + ow), (m(bm["uTo"]) + ou, m(bm["v"]) / 2 + ov, m(bm["top"]) + ow), st["steel"])
        if lod == 0:   # through-bolts, heads on both faces
            bo = bm["bolts"]
            for vb in bo["v"]:
                for uf, du in ((m(bm["uFrom"]) + ou, -0.015), (m(bm["uTo"]) + ou, 0.015)):
                    prims.cylinder(mb, (uf, m(vb) + ov, m(bo["w"]) + ow), (uf + du, m(vb) + ov, m(bo["w"]) + ow), m(bo["dia"]) / 2, 6, st["rod"])
    if lod <= 1:   # 25 mm lug plates across the beam ends, narrowing below it to a round end with the eye
        lu = po["lugs"]
        t0, t1 = m(lu["top"][0]) + ou, m(lu["top"][1]) + ou
        n0, n1 = m(lu["neck"][0]) + ou, m(lu["neck"][1]) + ou
        bw0, bw1, cr = m(bm["bottom"]) + ow, m(bm["top"]) + ow, m(lu["corner"])
        nr = (n1 - n0) / 2
        cu_ = (n0 + n1) / 2
        ctr = m(lu["bottom"]) + ow + nr
        neck_w = m(lu["neckW"]) + ow
        outline = [(t0, bw0), (t0, bw1 - cr), (t0 + cr, bw1), (t1 - cr, bw1), (t1, bw1 - cr), (t1, bw0), (n1, neck_w), (n1, ctr)]
        outline += [(cu_ + nr * math.cos(a_), ctr + nr * math.sin(a_)) for a_ in (math.pi * k / 6 for k in range(-1, -6, -1))]
        outline += [(n0, ctr), (n0, neck_w)]
        th = m(lu["thick"])
        for s in (-1, 1):
            for vl in lu["v"]:
                vc = s * m(vl) + ov
                prims.prism(mb, outline, (0, vc - th / 2, 0), U, W, V, th, st["yellow"])
                if lod == 0:
                    eu, ew = m(lu["eye"][0]) + ou, m(lu["eye"][1]) + ow
                    prims.cylinder(mb, (eu, vc - th / 2 - 0.012, ew), (eu, vc + th / 2 + 0.012, ew), m(lu["boss"]) / 2, 10, st["yellow"])
        jr = po["jRails"]   # J-handrails in the portal plane: from the beam, round a bend, level to the legs
        vt, drop, bend, wl, to = m(jr["v"]), m(jr["drop"]) + ow, m(jr["bend"]), m(jr["w"]) + ow, m(jr["to"])
        n_arc = 4 if lod == 0 else 2
        for s in (-1, 1):
            pts = [Vector((m(po["u"]) + ou, s * vt + ov, bw0))]
            pts += [Vector((m(po["u"]) + ou, s * (vt + bend * (1 - math.cos(a_))) + ov, drop - bend * math.sin(a_)))
                    for a_ in (math.pi / 2 * k / n_arc for k in range(n_arc + 1))]
            pts.append(Vector((m(po["u"]) + ou, s * to + ov, wl)))
            prims.tube_path(mb, pts, m(jr["dia"]) / 2, 6 if lod == 0 else 4, st["galv"], caps=False)


def entry_platform(mb, ep, cb, lod, st, off=(0.0, 0.0, 0.0), half_v=None, hatch=None):
    """The platform beyond the crossbeam: grated deck, end member on the crossbeam face, U hoop across its end with a
    mid rail, struts back under the crossbeam. half_v (metres) narrows it (line towers keep it inside the ropes);
    hatch = (v0, v1) leaves an opening in the deck where a ladder comes through."""
    ou, ov, ow = off
    hv = m(ep["v"]) / 2 if half_v is None else half_v
    deck_lo = (m(ep["uFrom"]) + ou, -hv + ov, m(ep["under"]) + ow)
    deck_hi = (m(ep["uTo"]) + ou, hv + ov, m(ep["deck"]) + ow)
    if hatch is None:
        parts.deck(mb, deck_lo, deck_hi, lod, st["galv"])
    else:   # two decks either side of the ladder opening
        parts.deck(mb, deck_lo, (deck_hi[0], hatch[0] + ov, deck_hi[2]), lod, st["galv"])
        parts.deck(mb, (deck_lo[0], hatch[1] + ov, deck_lo[2]), deck_hi, lod, st["galv"])
    if lod <= 2:
        wb0, wb1 = m(cb["bottom"]) + ow, m(cb["top"]) + ow
        em = ep["endMember"]
        prims.box(mb, (m(em[0]) + ou, -hv + ov, wb0), (m(em[1]) + ou, hv + ov, wb1), st["steel"])
        hp = ep["hoop"]
        c = m(hp["corner"])
        hu, dk, top = m(hp["u"]) + ou, m(ep["deck"]) + ow, m(hp["top"]) + ow
        pts = [(hu, -hv + 0.03 + ov, dk), (hu, -hv + 0.03 + ov, top - c), (hu, -hv + 0.03 + c + ov, top),
               (hu, hv - 0.03 - c + ov, top), (hu, hv - 0.03 + ov, top - c), (hu, hv - 0.03 + ov, dk)]
        prims.tube_path(mb, [Vector(p) for p in pts], 0.02, 6 if lod == 0 else 4, st["galv"])
        if lod <= 1:
            prims.cylinder(mb, (hu, -hv + 0.03 + ov, m(hp["mid"]) + ow), (hu, hv - 0.03 + ov, m(hp["mid"]) + ow), 0.016, 6 if lod == 0 else 4, st["galv"])
            sr = ep["strut"]
            sf, stt, sz = sr["from"], sr["to"], m(sr["size"])
            for s in (-1, 1):
                vs_ = s * m(sr["v"]) + ov
                prims.beam(mb, (m(sf[0]) + ou, vs_, m(sf[1]) + ow), (m(stt[0]) + ou, vs_, m(stt[1]) + ow), sz, sz, st["steel"])
