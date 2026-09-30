"""Material classes and the shared palette atlas (lift_palette.png, 512 x 512, 16 x 16 swatches of 32 px).

Each face samples one swatch through UV2. RGB is the albedo (sRGB); alpha packs the surface:
metallic = A >= 0.5, smoothness = (A mod 0.5) * 2. Roughness is constant per class and only lift steel is
metallic (roadmap 12). Swatches are flat colour, so the atlas is imported without mips or filtering.
"""
import numpy as np

GRID, CELL = 16, 32
SIZE = GRID * CELL

# name: (sRGB hex, metallic, smoothness). Order is the swatch index; append only, never reorder.
CLASSES = [
    ("galvanised", "#A7ABAE", 1, 0.45),     # structural steel, rails, railings
    ("paint_grey", "#8D949A", 0, 0.40),     # painted terminal steel
    ("paint_dark", "#2E3134", 0, 0.35),     # bullwheel, dark machinery
    ("machined", "#C9CCCF", 1, 0.75),       # axles, pins, cylinder rods
    ("rubber", "#1D1D1E", 0, 0.25),         # sheave liners, bumpers
    ("sheave_red", "#B0281F", 0, 0.45),     # sheave wheels (fixed colour, not livery)
    ("concrete", "#A6A29B", 0, 0.10),       # piers and footings
    ("livery", "#F4F4F2", 0, 0.50),         # hood paint: tinted by the player's colour in the shader
    ("glass", "#1B252D", 0, 0.88),          # tinted hood glass
    ("interior", "#2A2C2E", 0, 0.30),       # machinery seen through the glass
    ("seat_pad", "#161719", 0, 0.28),       # chair bench and backrest padding (black vinyl)
    ("chair_steel", "#AEB4BA", 1, 0.40),    # chair frame, hanger, bar (galvanised)
    ("safety_yellow", "#D6A11E", 0, 0.40),  # ladder rungs, lifting eyes
    ("grating", "#80868B", 1, 0.35),        # deck grating (reads darker than plate)
    ("trim_dark", "#3A3D41", 0, 0.35),      # gutters, frames, rubber seals
    ("white", "#E7E8E8", 0, 0.45),          # hood base band
    ("grip", "#6C7176", 1, 0.55),           # grip body
    ("sheave_rim", "#D9DAD6", 0, 0.45),     # light rims round the red sheave faces (as photographed)
    ("sign_black", "#121315", 0, 0.35),     # tower number plates (black, white number)
]
INDEX = {name: i for i, (name, *_) in enumerate(CLASSES)}


def swatch_uv(name):
    i = INDEX[name]
    col, row = i % GRID, i // GRID
    return ((col + 0.5) / GRID, (row + 0.5) / GRID)


def srgb(hexstr):
    h = hexstr.lstrip("#")
    return tuple(int(h[k:k + 2], 16) / 255.0 for k in (0, 2, 4))


def alpha(metallic, smoothness):
    return 0.5 * metallic + min(smoothness, 0.98) * 0.5


def image():
    """RGBA float array (SIZE, SIZE, 4), rows bottom-up like Blender images; RGB are sRGB values."""
    img = np.zeros((SIZE, SIZE, 4), np.float32)
    img[..., :3] = 0.5
    img[..., 3] = alpha(0, 0.4)
    for name, hexstr, metallic, smooth in CLASSES:
        i = INDEX[name]
        col, row = i % GRID, i // GRID
        img[row * CELL:(row + 1) * CELL, col * CELL:(col + 1) * CELL, :3] = srgb(hexstr)
        img[row * CELL:(row + 1) * CELL, col * CELL:(col + 1) * CELL, 3] = alpha(metallic, smooth)
    return img
