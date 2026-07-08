// Pure function: compute a directional hillshade from a dense row-major
// height grid using Horn's method (standard 3x3 central-difference normal
// estimation, same approach as GDAL/QGIS hillshade).
//
// Grid convention: row increases downward (matches canvas y, and matches
// the existing contour renderer's row->y mapping) and col increases
// rightward (matches canvas x). Light direction is defined in this same
// grid/screen space rather than true geographic north, since the renderer
// is a stylized top-down projection rather than a geographically corrected
// map.

function clampIndex(i: number, size: number): number {
  return Math.max(0, Math.min(size - 1, i));
}

/**
 * Returns a size*size Float32Array of shade values in [0, 1].
 */
export function computeHillshade(
  heights: number[],
  size: number,
  cellSizeMeters: number,
  azimuthDeg = 315,
  altitudeDeg = 45
): Float32Array {
  const shade = new Float32Array(size * size);

  const az = (azimuthDeg * Math.PI) / 180;
  const alt = (altitudeDeg * Math.PI) / 180;
  const lightX = Math.sin(az) * Math.cos(alt);
  const lightY = -Math.cos(az) * Math.cos(alt);
  const lightZ = Math.sin(alt);
  const lightLen = Math.hypot(lightX, lightY, lightZ);
  const lx = lightX / lightLen;
  const ly = lightY / lightLen;
  const lz = lightZ / lightLen;

  const at = (r: number, c: number) => heights[clampIndex(r, size) * size + clampIndex(c, size)];

  for (let r = 0; r < size; r++) {
    for (let c = 0; c < size; c++) {
      const z1 = at(r - 1, c - 1);
      const z2 = at(r - 1, c);
      const z3 = at(r - 1, c + 1);
      const z4 = at(r, c - 1);
      const z6 = at(r, c + 1);
      const z7 = at(r + 1, c - 1);
      const z8 = at(r + 1, c);
      const z9 = at(r + 1, c + 1);

      const dzdx = (z3 + 2 * z6 + z9 - (z1 + 2 * z4 + z7)) / (8 * cellSizeMeters);
      const dzdy = (z7 + 2 * z8 + z9 - (z1 + 2 * z2 + z3)) / (8 * cellSizeMeters);

      const nx = -dzdx;
      const ny = -dzdy;
      const nz = 1;
      const nLen = Math.hypot(nx, ny, nz);

      const dot = (nx / nLen) * lx + (ny / nLen) * ly + (nz / nLen) * lz;
      shade[r * size + c] = Math.max(0, Math.min(1, dot));
    }
  }

  return shade;
}
