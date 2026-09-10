/** Offline-only decorative material bake. Inputs are 3x3 RGBA tile mosaics.
 * Kept self-contained so the exact same function runs in Node tests and Canvas.
 * Texture coordinates are global Web Mercator metres, never tile-local seeds. */
export function bakeAlpineTile({ cover, terrain, size, z, x, y, padding = 0 }) {
  const width = size * 3;
  const outputSize = size + padding * 2;
  const result = new Uint8ClampedArray(outputSize * outputSize * 4);
  const worldPixels = size * 2 ** z;
  const mercatorPixelM = 40075016.686 / worldPixels;
  const clamp = (n) => Math.max(0, Math.min(1, n));
  const smooth = (a, b, n) => { const t = clamp((n - a) / (b - a)); return t * t * (3 - 2 * t); };
  const hash = (a, b) => {
    let h = Math.imul(a | 0, 374761393) ^ Math.imul(b | 0, 668265263);
    h = Math.imul(h ^ (h >>> 13), 1274126177);
    return ((h ^ (h >>> 16)) >>> 0) / 4294967295;
  };
  const noise = (a, b, scale) => {
    a /= scale; b /= scale;
    const ix = Math.floor(a), iy = Math.floor(b);
    const u = smooth(0, 1, a - ix), v = smooth(0, 1, b - iy);
    const top = hash(ix, iy) * (1 - u) + hash(ix + 1, iy) * u;
    const bottom = hash(ix, iy + 1) * (1 - u) + hash(ix + 1, iy + 1) * u;
    return top * (1 - v) + bottom * v;
  };
  const elevation = (col, row) => {
    const i = (row * width + col) * 4;
    return terrain[i] * 256 + terrain[i + 1] + terrain[i + 2] / 256 - 32768;
  };
  // Source colors are the preserved recolored WorldCover inputs, not the output.
  // Blended weights retain the softened classification boundaries.
  const materials = [
    { source: [82, 114, 89], color: [48, 83, 70], forest: 1, water: 0 },
    { source: [145, 165, 121], color: [133, 145, 112], forest: 0, water: 0 },
    { source: [168, 173, 128], color: [151, 151, 123], forest: 0, water: 0 },
    { source: [170, 166, 155], color: [158, 157, 147], forest: 0, water: 0 },
    { source: [230, 238, 233], color: [239, 240, 232], forest: 0, water: 0 },
    { source: [82, 140, 164], color: [74, 123, 139], forest: 0, water: 1 },
  ];
  for (let row = -padding; row < size + padding; row++) {
    const worldY = y * size + row + 0.5;
    const groundPixelM = mercatorPixelM / Math.cosh(Math.PI * (1 - 2 * worldY / worldPixels));
    for (let col = -padding; col < size + padding; col++) {
      const cx = col + size, cy = row + size, input = (cy * width + cx) * 4;
      const mx = (x * size + col + 0.5) * mercatorPixelM, my = worldY * mercatorPixelM;
      const height = elevation(cx, cy);
      const dx = (elevation(cx + 1, cy) - elevation(cx - 1, cy)) / (2 * groundPixelM);
      const dy = (elevation(cx, cy + 1) - elevation(cx, cy - 1)) / (2 * groundPixelM);
      const slope = Math.atan(Math.hypot(dx, dy)) * 180 / Math.PI;
      let red = 0, green = 0, blue = 0, forest = 0, water = 0, total = 0;
      for (const material of materials) {
        const d = (cover[input] - material.source[0]) ** 2
          + (cover[input + 1] - material.source[1]) ** 2 + (cover[input + 2] - material.source[2]) ** 2;
        const weight = 1 / (d + 20) ** 2;
        total += weight;
        red += material.color[0] * weight; green += material.color[1] * weight; blue += material.color[2] * weight;
        forest += material.forest * weight; water += material.water * weight;
      }
      red /= total; green /= total; blue /= total; forest /= total; water /= total;
      const broad = noise(mx, my, 380);
      // Suppress unresolved fine texture in overview tiles to avoid distant speckle.
      const fineStrength = 1 - smooth(18, 100, groundPixelM);
      const fine = (noise(mx, my, 26) - 0.5) * fineStrength;
      const texture = (broad - 0.5) * (10 + forest * 14) + fine * (7 + forest * 15);
      const snowline = 1680 + (noise(mx, my, 620) - 0.5) * 220;
      const snow = smooth(snowline - 130, snowline + 200, height)
        * (1 - smooth(38, 60, slope)) * (1 - forest * 0.72) * (1 - water);
      const snowTone = (broad - 0.5) * 3;
      const i = ((row + padding) * outputSize + col + padding) * 4;
      result[i] = (red + texture) * (1 - snow) + (243 + snowTone) * snow;
      result[i + 1] = (green + texture) * (1 - snow) + (242 + snowTone) * snow;
      result[i + 2] = (blue + texture) * (1 - snow) + (232 + snowTone) * snow;
      result[i + 3] = 255;
    }
  }
  return result;
}
