"""Prepares the ground textures (task 12c) for Unity: each Poly Haven source in cache/<id>/ is halved to 1024²
(a 2×2 box filter, so the tile still wraps), colour-graded toward the art direction's palette (0.5 §2) while keeping
the photo's light and shade, and packed as colour + height (alpha) and normal PNGs in cache/prepared/.

Unity's Mountain Planner > Generate Ground Textures then compresses them into the terrain's texture arrays.
Deterministic: the same sources give the same bytes. Usage: python tools/assets/ground/prepare.py
"""
import json
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
OUT = os.path.join(CACHE, "prepared")

# Slot name -> (source id, target colour (linear-ish sRGB 0-1), how much of the photo's own hue to keep 0-1,
# contrast of the photo's light and shade around the target). Targets follow 0.5 §2 / §5.
GRADES = {
    "forest_floor": ("forest_leaves_04", (0.25, 0.19, 0.13), 0.35, 1.0),    # dark umber needles and soil
    "grass": ("withered_grass", (0.40, 0.45, 0.24), 0.2, 0.9),             # valley grass, summer olive (seasons tint it)
    "rock": ("rock_face_03", (0.45, 0.45, 0.46), 0.15, 1.15),              # cool grey granite
    "developed": ("aerial_asphalt_01", (0.36, 0.36, 0.37), 0.2, 1.0),      # asphalt, grey
    "meadow": ("sparse_grass", (0.38, 0.42, 0.24), 0.45, 0.9),             # alpine meadow: sparser, a touch drier
    "scree": ("dry_ground_rocks", (0.48, 0.46, 0.43), 0.2, 1.1),           # grey-brown talus
    "dirt": ("brown_mud_dry", (0.36, 0.29, 0.21), 0.5, 1.0),               # bare dirt on steep grass
    "meadow_far": ("rocky_terrain_02", (0.38, 0.42, 0.24), 0.4, 0.8),      # meadow with rock patches, 90 m tile
}


def luminance(rgb):
    r, g, b = rgb
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def grade(colour, target, keep, contrast):
    """Recolours toward target: the photo's luminance (relative to its mean) times the target, mixed with the
    photo's own colour scaled to the target's brightness by `keep`."""
    pixels = list(colour.getdata())
    lums = [luminance((p[0] / 255, p[1] / 255, p[2] / 255)) for p in pixels]
    mean = sum(lums) / len(lums)
    target_lum = luminance(target)
    out = []
    for p, l in zip(pixels, lums):
        rel = max(0.0, 1 + (l / mean - 1) * contrast)
        own = [c / 255 * target_lum / max(mean, 1e-4) for c in p[:3]]
        rgb = [(1 - keep) * t * rel + keep * o for t, o in zip(target, own)]
        out.append(tuple(max(0, min(255, round(c * 255))) for c in rgb))
    graded = Image.new("RGB", colour.size)
    graded.putdata(out)
    return graded


def main():
    os.makedirs(OUT, exist_ok=True)
    for slot, (source, target, keep, contrast) in GRADES.items():
        folder = os.path.join(CACHE, source)
        colour = Image.open(os.path.join(folder, "colour.jpg")).convert("RGB").reduce(2)
        normal = Image.open(os.path.join(folder, "normal.jpg")).convert("RGB").reduce(2)
        height = Image.open(os.path.join(folder, "height.png"))
        height = height.convert("I") if height.mode not in ("L", "I", "I;16") else height
        height = height.point(lambda v: v / 256) if height.mode in ("I", "I;16") and max(height.getextrema()) > 255 else height
        height = height.convert("L").reduce(2)
        graded = grade(colour, target, keep, contrast)
        r, g, b = graded.split()
        Image.merge("RGBA", (r, g, b, height)).save(os.path.join(OUT, f"{slot}_albedo.png"))
        normal.save(os.path.join(OUT, f"{slot}_normal.png"))
        print(f"{slot}: {source} -> {graded.size[0]}²")
    with open(os.path.join(OUT, "grades.json"), "w") as f:
        json.dump({k: {"source": v[0], "target": v[1], "keep": v[2], "contrast": v[3]} for k, v in GRADES.items()}, f, indent=1)


if __name__ == "__main__":
    main()
