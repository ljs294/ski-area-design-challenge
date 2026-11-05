"""Fit checks for the Glidepath carpet (build_lifts.py; owner C1-C11, 2026-11-04). Returns problems, as surface_fit.

  modules    a belt module's (or the rails') back and front ends are the same section, so modules join end to end at
             every LOD: the corners at u = 0 and u = length of every face running the module's whole length match;
             sockets back and front on the centre line at the belt top, the module's length apart.
  stations   the station's end at its joint carries the module's section (every corner of the module's back end at
             that LOD is a corner of a face ending at the joint), and belt_in or belt_out sits there at the belt top.
  gallery    a bay's sockets are the module's; its arch stands on its own deck's side decks (single) or on brackets that
             reach in under the deck (double), at the deck top or the bracket's top; its arches leave at least
             HEADROOM over the outer belt edges (owner C14).
  portal     its opening clears the gallery's arches all round and stays under the sign box, and its posts stand
             outside the opening.
"""
import importlib

from liftkit import frame

TOL = 1e-5
HEADROOM = 2100   # mm over the belt at its outer edges, under the arches: 6' 11"


def _faces(mb):
    for f in mb.faces:
        yield [frame.lift(mb.verts[i]) for i in f]


def _ends(mb, u0, u1, at):
    """(v, w) in mm (0.1) of the corners at u = at of the faces that run from u0 (or before it, when u0 is None) to u1."""
    out = set()
    for pts in _faces(mb):
        us = [p[0] for p in pts]
        if abs(max(us) - u1) > TOL:
            continue
        if (u0 is None and min(us) < u1 - 0.5) or (u0 is not None and abs(min(us) - u0) < TOL):
            out |= {(round(p[1] * 1000, 1), round(p[2] * 1000, 1)) for p in pts if abs(p[0] - at) < TOL}
    return out


def _flushed(a):
    """The asset's body after the export's separate_flush (liftkit/export.lod_parts), as the built ones are."""
    for _ in range(3):
        if not a.body.separate_flush(once_per_plane=any(t is not None for t in a.body.tags)):
            break
    return a.body


def _near(p, q, tol=0.0011):
    return all(abs(x - y) <= tol for x, y in zip(p, q))


def check(assets, kind, spec, stage):
    a0 = assets[0]
    s = spec["section"]
    top = s["beltTop"] / 1000
    problems = []
    mod = importlib.import_module("carpet")
    if kind == "carpet_segment" or kind == "carpet_gallery" and "front" in a0.sockets:
        L = a0.sockets["front"][0]
        if not (_near(a0.sockets["back"], (0.0, 0.0, top)) and _near(a0.sockets["front"], (L, 0.0, top))):
            problems.append(f"sockets back {a0.sockets['back']} and front {a0.sockets['front']} are not on the belt top")
    if kind == "carpet_segment":
        L = a0.sockets["front"][0]
        for lod, a in enumerate(assets):
            back, front = _ends(a.body, 0.0, L, 0.0), _ends(a.body, 0.0, L, L)
            if not back or back != front:
                problems.append(f"LOD{lod}: the back and front ends differ ({len(back ^ front)} corners)")
    elif kind == "carpet_station":
        end = a0.dims["end"]
        joint = spec[end]["joint"] / 1000
        sock = "belt_in" if end == "top" else "belt_out"
        if not _near(a0.sockets.get(sock, (None,) * 3), (joint, 0.0, top)):
            problems.append(f"socket {sock} at {a0.sockets.get(sock)} is not at the joint on the belt top")
        for lod, a in enumerate(assets):
            seg = _flushed(mod.build(spec, lod, stage, "segment"))
            L = spec["module"]["length"] / 1000
            need = _ends(seg, 0.0, L, 0.0)
            have = _ends(a.body, None, joint, joint)
            if not need or need - have:
                problems.append(f"LOD{lod}: the station's end is missing {len(need - have)} of the module's "
                                f"{len(need)} corners at its joint")
    elif kind == "carpet_gallery" and "front" in a0.sockets:   # a bay
        g = spec["gallery"]
        v0, v1, w = a0.dims["feet"][0]
        belts = [c / 1000 for c in spec["belts"]]
        out = max(abs(c) for c in belts) + s["deckOut"] / 1000
        if g["shape"] == "round":
            inner = max(belts) + s["bar"]["to"] / 1000
            if abs(w - s["deckTop"]) > 0.5 or v0 / 1000 < inner or v1 / 1000 > out:
                problems.append(f"the arch's foot ({v0}-{v1} mm at {w}) is off the side deck "
                                f"({round(inner * 1000)}-{round(out * 1000)} at {s['deckTop']})")
        else:
            b = g["bracket"]
            if b["from"] / 1000 >= out or abs(w - b["top"]) > 0.5 or not (b["from"] <= v0 and v1 <= b["to"]):
                problems.append(f"the arch's foot ({v0}-{v1} mm at {w}) is not on its bracket "
                                f"({b['from']}-{b['to']} at {b['top']}, deck edge {round(out * 1000)})")
        if a0.dims["beltEdgeHeadroom"] < HEADROOM:
            problems.append(f"the headroom over the outer belt edges ({a0.dims['beltEdgeHeadroom']} mm) is under {HEADROOM}")
    elif kind == "carpet_gallery":   # the portal
        g, p = spec["gallery"], spec["portal"]
        bay = mod.build(spec, 0, stage, "gallery")
        if g["shape"] == "round":
            if p["opening"] <= g["arch"]["outer"]:
                problems.append(f"the opening (radius {p['opening']}) doesn't clear the arches ({g['arch']['outer']})")
        elif g["shape"] == "arch":   # the opening is the outline offset past the arches' outside: check it is clear
            o = a0.dims["opening"]
            if p["opening"]["clear"] <= 0 or o["halfV"] <= bay.dims["outerHalfV"] or o["top"] <= bay.dims["outerHeight"]:
                problems.append(f"the opening ({o['halfV']} x {o['top']}) doesn't clear the arches "
                                f"({bay.dims['outerHalfV']} x {bay.dims['outerHeight']})")
            if o["top"] >= p["sign"]["from"]:
                problems.append(f"the opening's top ({o['top']}) reaches the sign box ({p['sign']['from']})")
        else:
            o = p["opening"]
            if o["halfV"] <= bay.dims["outerHalfV"] or o["top"] <= bay.dims["outerHeight"]:
                problems.append(f"the opening ({o['halfV']} x {o['top']}) doesn't clear the arches "
                                f"({bay.dims['outerHalfV']} x {bay.dims['outerHeight']})")
        reach = p["opening"] if g["shape"] == "round" else a0.dims["opening"]["halfV"]
        if p["halfV"] - p["post"] < reach:
            problems.append(f"the posts ({p['halfV'] - p['post']}-{p['halfV']}) stand in the opening ({reach})")
    return problems
