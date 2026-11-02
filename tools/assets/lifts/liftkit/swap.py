"""Terminal heads (owner, 2026-11-02): the player chooses which end of a fixed-grip lift drives, so every fixed-grip
terminal carries a head for each end and the game shows one. The bottom head holds the rope down (it runs level
through loading and leaves climbing); the top head takes it in over support sheaves. A terminal keeps the head it was
built with (its default end) under its own names, and gains the other end's head, built by its partner terminal's
code and grafted on with the end as a prefix on every pivot and socket (bottom_eq_l, top_rope_left_out).
An end may have variants (owner, 2026-11-02: a top head with a 4, 6 or 8 sheave train, for a sharper breakover at
the summit): each is a whole head under its own key and prefix (top6_, top8_); the game shows one head at a time.

Each head's steel is a part on a fixed pivot, <end>_head (never turned; liftkit/clash.py), its trains hang under
their own pivots as before, and asset.heads lists what belongs to which head. view() gives the asset as one head
shows it (the other head's parts and sockets gone, the head's sockets under their plain names), for the clash sweep,
the chair's fit, the ambient occlusion and the review renders. LiftImport leaves the default head showing.
"""
import contextlib

from .export import Asset
from .mesh import MeshBuilder

ENDS = ("bottom", "top")


def other(end):
    return "top" if end == "bottom" else "bottom"


def _offset(p, d):
    return tuple(a + b for a, b in zip(p, d))


@contextlib.contextmanager
def head(a, mb, end, pos, owns=(), dims=None):
    """The terminal's own head (its default end): faces drawn inside are the head's steel (tagged <end>_head),
    pivots added inside are the head's. owns: the plain socket names this head sets (its rope exits, its chair
    socket), which another head's view replaces."""
    before = set(a.pivots)
    a.pivots[f"{end}_head"] = {"pos": tuple(pos), "axis": (0.0, 1.0, 0.0), "fixed": True}
    with mb.grouped(f"{end}_head"):
        yield
    a.heads[end] = {"end": end, "default": True, "prefix": "", "pivots": sorted(set(a.pivots) - before),
                    "sockets": list(owns), "dims": dict(dims or {})}


def graft(a, mb, src, src_mb, key, offset, pos, dims=None, sockets=None, end=None):
    """Grafts the head built on its own (src: an Asset holding its pivots, parts and sockets; src_mb: its faces, the
    steel untagged) onto asset a as head `key` (an end, or a variant of one: top6) at `end` (default: key), moved by
    offset (lift frame, m) and prefixed with the key. sockets: more of the head's sockets, already in a's frame (its
    chair socket at a's own pier)."""
    pre = key + "_"

    def tag(t):
        if t is None:
            return f"{key}_head"
        base, _, piece = t.partition("#")
        return (pre + base if base else f"{key}_head") + ("#" + piece if piece else "")
    moved = src_mb.translated(offset)
    moved.tags = [tag(t) for t in moved.tags]
    mb.merge(moved)
    for name, part in src.parts.items():
        a.parts[pre + name] = part.translated(offset)
    names = [f"{key}_head"]
    a.pivots[f"{key}_head"] = {"pos": _offset(pos, offset), "axis": (0.0, 1.0, 0.0), "fixed": True}
    for name, piv in src.pivots.items():
        p = dict(piv)
        p["pos"] = _offset(piv["pos"], offset)
        if piv.get("parent"):
            p["parent"] = pre + piv["parent"]
        a.pivots[pre + name] = p
        names.append(pre + name)
    for name, p in src.sockets.items():
        a.sockets[pre + name] = _offset(p, offset)
    for name, p in (sockets or {}).items():
        a.sockets[pre + name] = tuple(p)
    for name, parent in src.socket_parents.items():
        a.socket_parents[pre + name] = pre + parent
    a.heads[key] = {"end": end or key, "default": False, "prefix": pre, "pivots": names,
                    "sockets": [pre + n for n in list(src.sockets) + list(sockets or {})], "dims": dict(dims or {})}


def keys(a):
    """a's head keys, its default head first."""
    return sorted(a.heads, key=lambda k: not a.heads[k]["default"])


def owned(a, key):
    """The pivot names of a's head `key`."""
    return set(a.heads[key]["pivots"]) if key in a.heads else set()


def view(a, key):
    """Asset a as its head `key` shows it: every other head's faces, parts, pivots and sockets left out, and this
    head's sockets under their plain names (a default head's are plain already). Without heads, a itself."""
    if not a.heads:
        return a
    end = key
    drop = set().union(*(owned(a, e) for e in a.heads if e != end))
    v = Asset(a.id)
    v.body = a.body.filtered(lambda t: (t or "").split("#")[0] not in drop)
    v.parts = {k: p for k, p in a.parts.items() if k not in drop}
    v.pivots = {k: p for k, p in a.pivots.items() if k not in drop}
    h = a.heads[end]
    gone = set()
    for e, oh in a.heads.items():
        gone |= set(oh["sockets"]) if e != end or not oh["default"] else set()
    v.sockets = {k: p for k, p in a.sockets.items() if k not in gone}
    if not h["default"]:   # the default head's plain sockets give way to this head's
        for k in a.heads[keys(a)[0]]["sockets"]:
            v.sockets.pop(k, None)
        for k in h["sockets"]:
            v.sockets[k[len(h["prefix"]):]] = a.sockets[k]
    v.socket_parents = {k: p for k, p in a.socket_parents.items() if p not in drop}
    v.dims = dict(a.dims, **h["dims"])
    v.paths = getattr(a, "paths", {})
    return v


def other_parts(a, key):
    """The part names (pivots) that belong to heads other than `key`: what a render of it hides."""
    return set().union(*(owned(a, e) for e in a.heads if e != key)) if a.heads else set()
