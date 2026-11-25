"""The buildings' palette classes, registered into the lift palette (liftkit/palette.py) from swatch 128 on.

The lift palette fills swatches from 0 and only ever appends; the buildings take the bottom half of the 16 x 16 atlas
(rows 8-15), so the two never collide and one atlas serves both (the lift rows are identical in either). Detail-atlas
tiles per class go into liftkit/textures.CLASS_TILE the same way. Call register() before building anything.

UV1 y (MeshBuilder Style.livery) on buildings: 0 plain, 3 a lamp's lens (emissive at night, by its light's schedule),
4 window glass (glows warm at night, by the building's glow schedule). The lifts use 1 (livery) and 2 (accent).
"""
from liftkit import palette as lift_palette
from liftkit import textures

FIRST = 128
LAMP, WINDOW = 3.0, 4.0

# name: (sRGB hex, metallic, smoothness, detail tile). Order is the swatch index from FIRST; append only.
CLASSES = [
    ("panel_tan", "#B8AD97", 0, 0.35, "ribbed"),         # insulated metal wall panels (warm tan, as the metal reference)
    ("roof_charcoal", "#3C3F43", 0, 0.45, "roof_rib"),   # standing-seam roof, painted (metallic stays lift steel only)
    ("trim_bronze", "#4A3F34", 0, 0.40, "paint"),        # doors, frames, corner and eave trim
    ("concrete_base", "#A9A49B", 0, 0.10, "concrete"),   # stem wall, pads, apron, vault lid
    ("transformer_green", "#3F5A46", 0, 0.40, "paint"),  # pad-mount transformers
    ("window_glass", "#121A20", 0, 0.88, "plain"),       # window and door glass (opaque: nothing behind it yet)
    ("lamp_lens", "#F1EDDF", 0, 0.60, "plain"),          # wall-pack lenses
    ("louvre_dark", "#2D3034", 0, 0.30, "paint"),        # louvre blades and the dark behind them
    ("door_panel", "#4F4438", 0, 0.38, "panel"),         # roll-up door slats and man-door faces
    ("fixture_bronze", "#4A3F34", 0, 0.45, "plain"),     # light fixtures: smooth powder-coated cast housings
    ("panel_grey", "#C6C9CA", 0, 0.40, "ribbed"),        # lift huts' ribbed factory panels (light grey, as the terminals)
    ("trim_grey", "#575D62", 0, 0.40, "paint"),          # lift huts' frames, posts and soffit
    ("timber_deck", "#8B7D66", 0, 0.25, "concrete"),     # unload ramp decking, weathered (the tile's board joints)
    ("timber_post", "#6F5D47", 0, 0.25, "concrete"),     # unload ramp posts, beams and joists (treated timber)
    ("sign_blue", "#1F4E8C", 0, 0.40, "paint"),          # the UNLOAD HERE board (the game letters it)
    ("door_amber", "#B9832F", 0, 0.42, "panel"),         # the garage's overhead doors, a deep groomer amber (the map's dots)
    ("panel_translucent", "#E4E1D4", 0, 0.50, "ribbed"), # the garage's light band (polycarbonate; glows with the building)
    ("tank_white", "#E2E0D8", 0, 0.40, "paint"),         # the fuel tank, double-wall steel, painted
]


def register():
    if "panel_tan" in lift_palette.INDEX:
        return
    for k, (name, hexstr, metallic, smooth, tile) in enumerate(CLASSES):
        lift_palette.CLASSES.append((name, hexstr, metallic, smooth))
        lift_palette.INDEX[name] = FIRST + k
        textures.CLASS_TILE[name] = tile
