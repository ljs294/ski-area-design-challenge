"""The detail atlas lift_trim.png: 4 x 4 tiles of 256 px, each tile one metre of surface (3.9 mm per px).

Channels, as LiftStructure.shader reads them (0.5 is neutral everywhere): RG tangent-space normal, B cavity
(multiplies occlusion by 2B), A brightness (multiplies albedo by 2A). Every pattern is periodic over its tile
(FFT-filtered noise, integer-pitch bars and seams), so a tile repeats without seams. Deterministic: fixed seeds.

Faces pick a tile by palette class (CLASS_TILE) or by Style.trim, and get box-mapped UV0 in metres; see
mesh.detail_uv for the encoding.
"""
import zlib

import numpy as np

N, T = 4, 256
SIZE = N * T
TILES = ["plain", "paint", "galvanised", "grating", "checker", "concrete", "panel", "rubber", "seat"]
TILE = {name: i for i, name in enumerate(TILES)}
CLASS_TILE = {
    "galvanised": "galvanised", "paint_grey": "paint", "paint_dark": "paint", "machined": "plain",
    "rubber": "rubber", "sheave_red": "paint", "concrete": "concrete", "livery": "panel", "glass": "plain",
    "interior": "plain", "seat_pad": "seat", "chair_steel": "galvanised", "safety_yellow": "paint",
    "grating": "grating", "trim_dark": "paint", "white": "panel", "grip": "galvanised", "sheave_rim": "paint",
    "sign_black": "paint", "aluminium": "plain", "valve_maroon": "paint",
}


def tile_of(style):
    return TILE[style.trim or CLASS_TILE.get(style.cls, "plain")]


def _noise(name, sigma):
    """Periodic Gaussian-filtered white noise over one tile, zero mean, unit deviation."""
    rng = np.random.default_rng(zlib.crc32(name.encode()))
    white = rng.standard_normal((T, T))
    f = np.fft.fftfreq(T)
    fx, fy = np.meshgrid(f, f)
    kernel = np.exp(-2 * (np.pi * sigma) ** 2 * (fx ** 2 + fy ** 2))
    out = np.real(np.fft.ifft2(np.fft.fft2(white) * kernel))
    return (out - out.mean()) / (out.std() + 1e-9)


def _grid():
    y, x = np.mgrid[0:T, 0:T]
    return x.astype(np.float64), y.astype(np.float64)


def _tile(name):
    """(height in px units, brightness multiplier, cavity multiplier, normal strength) for one tile."""
    x, y = _grid()
    ones = np.ones((T, T))
    if name == "plain":
        return np.zeros((T, T)), ones, ones, 0.0
    if name == "paint":   # orange peel and faint mottling
        return _noise("paint_h", 1.6) * 0.35, 1 + _noise("paint_b", 24) * 0.025, ones, 0.6
    if name == "galvanised":   # spangle patches and a fine grain
        sp = np.tanh(_noise("galv_s", 7) * 1.8)
        return _noise("galv_h", 1.5) * 0.1, 1 + sp * 0.05 + _noise("galv_f", 1.2) * 0.015, ones, 0.3
    if name == "grating":   # bearing bars every 8 px (31 mm) along x, cross bars every 32 px (125 mm)
        bar = (x % 8) < 2
        cross = (y % 32) < 2
        solid = bar | cross
        h = np.where(solid, 1.0, -1.2)
        return h * 1.2, np.where(solid, 1.08, 0.35), np.where(solid, 1.0, 0.4), 1.0
    if name == "checker":   # raised lugs on a 16 px (62 mm) grid, alternating direction
        cx, cy = (x % 16) - 7.5, (y % 16) - 7.5
        alt = ((x // 16 + y // 16) % 2) * 2 - 1
        u, v = (cx + alt * cy) / 1.414, (cx - alt * cy) / 1.414
        lug = np.clip(1 - (np.abs(u) / 6.5) ** 2 - (np.abs(v) / 1.6) ** 2, 0, 1)
        return lug * 2.0 + _noise("chk_h", 1) * 0.1, 1 + lug * 0.06, 1 - (1 - lug) * 0.1, 0.9
    if name == "concrete":   # form boards 64 px (0.25 m) high with their joints, pores, board-to-board tone
        joint = (y % 64) < 1.5
        board = (y // 64).astype(int)
        tone = np.array([0.03, -0.02, 0.01, -0.03])[board % 4]
        pores = np.clip(-_noise("con_p", 0.8) - 2.2, 0, 1)
        h = _noise("con_h", 3) * 0.3 - joint * 1.0 - pores * 0.8
        return h, 1 + tone + _noise("con_b", 18) * 0.03 - joint * 0.12, 1 - joint * 0.35 - pores * 0.3, 0.7
    if name == "panel":   # smooth painted panels with a seam at each metre
        seam = (x < 1.5) | (y < 1.5)
        return _noise("pan_h", 2.0) * 0.12 - seam * 1.5, 1 - seam * 0.1, 1 - seam * 0.4, 0.8
    if name == "rubber":
        return _noise("rub_h", 0.8) * 0.2, 1 + _noise("rub_b", 6) * 0.03, ones, 0.4
    if name == "seat":   # padded vinyl: a fine grain over soft, broad undulation
        return _noise("seat_h", 0.7) * 0.06 + _noise("seat_w", 10) * 0.8, 1 + _noise("seat_b", 12) * 0.02, ones, 0.25
    raise KeyError(name)


def _pack(h, bright, cavity, strength):
    """Tile channels as RGBA floats (T, T, 4); normals from periodic central differences."""
    dx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5 * strength
    dy = (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.5 * strength
    n = np.stack([-dx, -dy, np.ones_like(h)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    rgba = np.empty((T, T, 4))
    rgba[..., 0] = n[..., 0] * 0.5 + 0.5
    rgba[..., 1] = n[..., 1] * 0.5 + 0.5
    rgba[..., 2] = np.clip(cavity, 0, 2) * 0.5
    rgba[..., 3] = np.clip(bright, 0, 2) * 0.5
    return rgba


def image():
    """RGBA float array (SIZE, SIZE, 4), rows bottom-up like Blender images; linear data, not colour."""
    img = np.empty((SIZE, SIZE, 4), np.float32)
    img[...] = 0.5
    for name, i in TILE.items():
        col, row = i % N, i // N
        img[row * T:(row + 1) * T, col * T:(col + 1) * T] = _pack(*_tile(name))
    return img
