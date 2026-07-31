import { describe, it, expect } from 'vitest';
import {
  boxBlur,
  maskToPolygons,
  maskToPolygonsRect,
  prepareMaskRingsRect,
  preparedRingsToPolygons,
} from './coverPolygons';

/** Build an n×n mask, marking cells [r0..r1]×[c0..c1] (inclusive) as 1. */
function block(n: number, r0: number, r1: number, c0: number, c1: number): Uint8Array {
  const m = new Uint8Array(n * n);
  for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) m[r * n + c] = 1;
  return m;
}

// Blur off so geometry is exact and assertions are deterministic.
const RAW = { blurRadius: 0, minAreaCells: 1, simplifyTol: 0.1 };

function referenceBoxBlur(
  src: Float32Array,
  width: number,
  height: number,
  radius: number,
  iterations: number
): Float32Array {
  if (radius <= 0 || iterations <= 0) return src;
  let buf = src;
  const windowSize = radius * 2 + 1;
  for (let iteration = 0; iteration < iterations; iteration++) {
    const horizontal = new Float32Array(src.length);
    for (let row = 0; row < height; row++) {
      for (let col = 0; col < width; col++) {
        let sum = 0;
        for (let offset = -radius; offset <= radius; offset++) {
          const sampleCol = Math.min(width - 1, Math.max(0, col + offset));
          sum += buf[row * width + sampleCol];
        }
        horizontal[row * width + col] = sum / windowSize;
      }
    }
    const vertical = new Float32Array(src.length);
    for (let col = 0; col < width; col++) {
      for (let row = 0; row < height; row++) {
        let sum = 0;
        for (let offset = -radius; offset <= radius; offset++) {
          const sampleRow = Math.min(height - 1, Math.max(0, row + offset));
          sum += horizontal[sampleRow * width + col];
        }
        vertical[row * width + col] = sum / windowSize;
      }
    }
    buf = vertical;
  }
  return buf;
}

describe('boxBlur', () => {
  it('matches the clamped reference blur across iterations and oversized radii', () => {
    const width = 7, height = 5;
    const source = Float32Array.from(
      { length: width * height },
      (_, index) => ((index * 37) % 101) / 100
    );
    const original = source.slice();

    for (const radius of [1, 2, 6]) {
      const expected = referenceBoxBlur(source, width, height, radius, 3);
      const actual = boxBlur(source, width, height, radius, 3);
      expect(actual).toHaveLength(expected.length);
      for (let index = 0; index < actual.length; index++) {
        expect(actual[index]).toBeCloseTo(expected[index], 6);
      }
    }
    expect(source).toEqual(original);
  });

  it('keeps source reads linear instead of repeating the whole radius per pixel', () => {
    const width = 40, height = 20, radius = 12;
    const source = Float32Array.from(
      { length: width * height },
      (_, index) => index % 3
    );
    let reads = 0;
    const observed = new Proxy(source, {
      get(target, property) {
        if (typeof property === 'string' && /^\d+$/.test(property)) reads++;
        return Reflect.get(target, property, target);
      },
    });

    boxBlur(observed, width, height, radius, 1);

    // The rolling pass needs an initial window plus two reads per output.
    // A radius loop at every pixel would perform 20,000 source reads here.
    expect(reads).toBeLessThan(width * height * 4);
  });
});

describe('maskToPolygons', () => {
  it('returns nothing for an empty mask', () => {
    expect(maskToPolygons(new Uint8Array(100), 10, RAW)).toEqual([]);
  });

  it('traces a single solid block as one hole-less polygon', () => {
    const polys = maskToPolygons(block(10, 3, 6, 3, 6), 10, RAW);
    expect(polys).toHaveLength(1);
    expect(polys[0].holes).toHaveLength(0);
    // Outer ring is a closed loop.
    const ring = polys[0].outer;
    expect(ring.length).toBeGreaterThanOrEqual(4);
    expect(ring[0]).toEqual(ring[ring.length - 1]);
  });

  it('traces an annulus as one outer ring with one hole', () => {
    // Filled 2..8, interior 4..6 cleared -> ring shape.
    const m = block(12, 2, 9, 2, 9);
    for (let r = 4; r <= 7; r++) for (let c = 4; c <= 7; c++) m[r * 12 + c] = 0;
    const polys = maskToPolygons(m, 12, RAW);
    expect(polys).toHaveLength(1);
    expect(polys[0].holes).toHaveLength(1);
  });

  it('traces two disjoint blocks as two polygons', () => {
    const m = new Uint8Array(16 * 16);
    for (let r = 2; r <= 4; r++) for (let c = 2; c <= 4; c++) m[r * 16 + c] = 1;
    for (let r = 10; r <= 12; r++) for (let c = 10; c <= 12; c++) m[r * 16 + c] = 1;
    const polys = maskToPolygons(m, 16, RAW);
    expect(polys).toHaveLength(2);
  });

  it('drops speckle below the minimum area', () => {
    // A single lit cell has area ~1; require >= 4 to reject it.
    const m = new Uint8Array(10 * 10);
    m[5 * 10 + 5] = 1;
    expect(maskToPolygons(m, 10, { blurRadius: 0, minAreaCells: 4 })).toEqual([]);
  });

  it('reuses a prepared trace for multiple simplification tolerances', () => {
    const width = 24, height = 16;
    const mask = new Uint8Array(width * height);
    for (let row = 2; row < height - 2; row++) {
      const right = 12 + (row % 4 < 2 ? 3 : -2);
      for (let col = 3; col <= right; col++) mask[row * width + col] = 1;
    }
    const traceOptions = { blurRadius: 2, blurIterations: 2, minAreaCells: 1 };
    const rings = prepareMaskRingsRect(mask, width, height, traceOptions);

    for (const simplifyTol of [0.5, 2, 4]) {
      const options = { ...traceOptions, simplifyTol };
      expect(preparedRingsToPolygons(rings, options))
        .toEqual(maskToPolygonsRect(mask, width, height, options));
    }
  });
});
