"""The outfit of one guest: the one jacket style in a keyed random colour, with rules against ugly pairs.

outfit(serial) is a pure function of the skier's serial, so a guest wears the same clothes all day and the same
build gives the same crowd. Colours are sRGB; the ids are the material ids of mesh.MATS. Gloves and trims stay dark.
"""
import colorsys

SHELL = {"teal": (0.07, 0.55, 0.50), "rust": (0.72, 0.33, 0.14), "brick": (0.66, 0.18, 0.16),
         "mustard": (0.80, 0.62, 0.14), "cobalt": (0.13, 0.33, 0.72), "plum": (0.45, 0.20, 0.48),
         "moss": (0.36, 0.48, 0.20), "sky": (0.35, 0.62, 0.82), "coral": (0.90, 0.42, 0.36)}
NEUTRAL = {"black": (0.09, 0.09, 0.10), "charcoal": (0.22, 0.23, 0.25), "navy": (0.10, 0.15, 0.30),
           "stone": (0.62, 0.58, 0.50)}
LOUD = {"neon": (0.80, 0.92, 0.15), "pink": (0.92, 0.22, 0.52), "ice": (0.58, 0.72, 0.84)}
# (colour, share in a thousand): nine shell hues 65%, four neutrals 25%, three loud 10%
JACKETS = [(c, 72) for c in SHELL.values()] + [(NEUTRAL["black"], 100), (NEUTRAL["charcoal"], 62),
                                               (NEUTRAL["navy"], 50), (NEUTRAL["stone"], 40),
                                               (LOUD["neon"], 30), (LOUD["pink"], 40), (LOUD["ice"], 30)]
TROUSERS = [(NEUTRAL["black"], 190), (NEUTRAL["charcoal"], 150), (NEUTRAL["navy"], 110), ((0.30, 0.33, 0.18), 80),
            ((0.58, 0.50, 0.34), 80), ((0.45, 0.46, 0.48), 90)] + [(c, 33) for c in SHELL.values()]
HELMET_DARK = [NEUTRAL["black"], NEUTRAL["charcoal"]]
OFF_WHITE = (0.82, 0.82, 0.80)
LENSES = [(0.95, 0.55, 0.10), (0.90, 0.30, 0.45), (0.15, 0.35, 0.95), (0.20, 0.75, 0.50), (0.75, 0.78, 0.82)]
SKIN = [(0.94, 0.78, 0.67), (0.87, 0.67, 0.53), (0.78, 0.57, 0.42), (0.64, 0.44, 0.31), (0.47, 0.31, 0.22),
        (0.33, 0.22, 0.16)]
DECKS = list(SHELL.values()) + [NEUTRAL["black"], NEUTRAL["black"], NEUTRAL["navy"], LOUD["neon"], LOUD["pink"],
                                (0.85, 0.85, 0.83)]
FIXED = {"glove": (0.07, 0.07, 0.08), "strap": (0.06, 0.06, 0.07), "liner": (0.13, 0.13, 0.14),
         "boot": (0.30, 0.31, 0.34), "metal": (0.42, 0.43, 0.46), "base": (0.08, 0.08, 0.09)}


def _key(serial, salt):
    h = ((serial + 1) * 2654435761 + salt * 40503) & 0xFFFFFFFF
    h = ((h ^ (h >> 15)) * 0x2C1B3C6D) & 0xFFFFFFFF
    h = ((h ^ (h >> 12)) * 0x297A2D39) & 0xFFFFFFFF
    return (h ^ (h >> 15)) & 0xFFFFFFFF


def _weighted(table, k):
    r = k % sum(w for _, w in table)
    for c, w in table:
        if r < w:
            return c
        r -= w
    return table[-1][0]


def _hsv(c):
    return colorsys.rgb_to_hsv(*c)


def _family(c):
    h, s, v = _hsv(c)
    if s < 0.25 or v < 0.2:
        return "neutral"
    d = h * 360
    return "red" if d < 25 or d > 330 else "yellow" if d < 75 else "green" if d < 170 else "blue" if d < 265 else "purple"


def _clash(jacket, trousers):
    a, b = _family(jacket), _family(trousers)
    if {a, b} in ({"red", "green"}, {"yellow", "green"}):
        return True
    if a == b and a != "neutral":
        return True
    return abs(_hsv(jacket)[2] - _hsv(trousers)[2]) < 0.20


def outfit(serial, kind="skier"):
    """{material id: sRGB} for one guest."""
    jacket = _weighted(JACKETS, _key(serial, 1))
    dark = _hsv(jacket)[2] < 0.35
    roll = _key(serial, 2) % 100
    if roll < 60:      # the yoke: mostly the jacket's own hue, much darker (or lighter on a dark jacket)
        block = tuple(min(1.0, c * 1.9 + 0.08) for c in jacket) if dark else tuple(c * 0.45 for c in jacket)
    elif roll < 85:
        block = (0.34, 0.35, 0.38) if dark else NEUTRAL["charcoal"]
    else:
        block = jacket
    trousers = None
    for attempt in range(8):
        trousers = _weighted(TROUSERS, _key(serial, 10 + attempt))
        if not _clash(jacket, trousers):
            break
    roll = _key(serial, 3) % 100
    helmet = (HELMET_DARK[_key(serial, 4) % 2] if roll < 50 else block if roll < 70 else jacket if roll < 85
              else OFF_WHITE)
    if helmet == OFF_WHITE and _hsv(jacket)[2] > 0.75:     # a pale helmet over a pale jacket fades on snow
        helmet = HELMET_DARK[_key(serial, 4) % 2]
    roll = _key(serial, 5) % 100
    accent = jacket if roll < 55 else OFF_WHITE if roll < 75 else list(LOUD.values())[_key(serial, 6) % 3]
    if _hsv(accent)[2] - _hsv(helmet)[2] < 0.15 and accent == helmet:
        accent = OFF_WHITE
    out = dict(FIXED)
    out.update({"jacket": jacket, "block": block, "pants": trousers, "patch": tuple(c * 0.6 for c in trousers),
                "helmet": helmet, "lens": LENSES[_key(serial, 7) % len(LENSES)], "accent": accent,
                "skin": SKIN[_key(serial, 8) % len(SKIN)], "deck": DECKS[_key(serial, 9) % len(DECKS)]})
    return out


CONCEPT_SKIER = dict(FIXED, jacket=(0.05, 0.56, 0.47), block=(0.13, 0.22, 0.22), pants=(0.07, 0.25, 0.72),
                     patch=(0.10, 0.16, 0.34), helmet=(0.30, 0.32, 0.34), lens=(0.10, 0.28, 0.95),
                     accent=(0.86, 0.86, 0.84), skin=(0.87, 0.67, 0.53), deck=(0.20, 0.22, 0.26))
CONCEPT_BOARDER = dict(FIXED, jacket=(0.95, 0.42, 0.06), block=(0.22, 0.23, 0.26), pants=(0.07, 0.25, 0.72),
                       patch=(0.20, 0.21, 0.25), helmet=(0.30, 0.32, 0.34), lens=(0.95, 0.35, 0.10),
                       accent=(0.95, 0.42, 0.06), skin=(0.87, 0.67, 0.53), deck=(0.12, 0.12, 0.14),
                       base=(0.90, 0.45, 0.10))
