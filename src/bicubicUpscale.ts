// Pure math: upscale a row-major square height grid via separable bicubic
// (Catmull-Rom) convolution. Used to turn a modest real-world elevation
// sample (e.g. 64x64) into a dense display grid (e.g. 512x512) without
// inventing terrain that wasn't actually sampled.

function cubicWeight(t: number, a = -0.5): number {
  const at = Math.abs(t);
  if (at <= 1) return (a + 2) * at ** 3 - (a + 3) * at ** 2 + 1;
  if (at < 2) return a * at ** 3 - 5 * a * at ** 2 + 8 * a * at - 4 * a;
  return 0;
}

function clampIndex(i: number, size: number): number {
  return Math.max(0, Math.min(size - 1, i));
}

/**
 * Upscale a srcSize x srcSize row-major grid to dstSize x dstSize.
 * Corner-aligned mapping keeps the output boundary exactly matching the
 * input boundary. Each output sample is clamped to the min/max of its
 * 4x4 source neighborhood to prevent Catmull-Rom ringing from producing
 * a peak/pit higher/lower than anything actually sampled.
 */
export function bicubicUpscale(src: number[], srcSize: number, dstSize: number): number[] {
  if (srcSize < 2) {
    // Degenerate input — nothing to interpolate, just flood-fill.
    return new Array(dstSize * dstSize).fill(src[0] ?? 0);
  }

  const dst = new Array<number>(dstSize * dstSize);
  const scale = (srcSize - 1) / (dstSize - 1);

  for (let dr = 0; dr < dstSize; dr++) {
    const sy = dr * scale;
    const y0 = Math.floor(sy);
    const fy = sy - y0;
    const wy = [cubicWeight(fy + 1), cubicWeight(fy), cubicWeight(fy - 1), cubicWeight(fy - 2)];
    const rows = [
      clampIndex(y0 - 1, srcSize),
      clampIndex(y0, srcSize),
      clampIndex(y0 + 1, srcSize),
      clampIndex(y0 + 2, srcSize),
    ];

    for (let dc = 0; dc < dstSize; dc++) {
      const sx = dc * scale;
      const x0 = Math.floor(sx);
      const fx = sx - x0;
      const wx = [cubicWeight(fx + 1), cubicWeight(fx), cubicWeight(fx - 1), cubicWeight(fx - 2)];
      const cols = [
        clampIndex(x0 - 1, srcSize),
        clampIndex(x0, srcSize),
        clampIndex(x0 + 1, srcSize),
        clampIndex(x0 + 2, srcSize),
      ];

      let value = 0;
      let neighborhoodMin = Infinity;
      let neighborhoodMax = -Infinity;

      for (let r = 0; r < 4; r++) {
        let rowValue = 0;
        for (let c = 0; c < 4; c++) {
          const sample = src[rows[r] * srcSize + cols[c]];
          rowValue += sample * wx[c];
          if (sample < neighborhoodMin) neighborhoodMin = sample;
          if (sample > neighborhoodMax) neighborhoodMax = sample;
        }
        value += rowValue * wy[r];
      }

      dst[dr * dstSize + dc] = Math.min(neighborhoodMax, Math.max(neighborhoodMin, value));
    }
  }

  return dst;
}
