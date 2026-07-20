import { describe, it, expect } from 'vitest';
import { maskToPolygons } from './coverPolygons';

/** Build an n×n mask, marking cells [r0..r1]×[c0..c1] (inclusive) as 1. */
function block(n: number, r0: number, r1: number, c0: number, c1: number): Uint8Array {
  const m = new Uint8Array(n * n);
  for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) m[r * n + c] = 1;
  return m;
}

// Blur off so geometry is exact and assertions are deterministic.
const RAW = { blurRadius: 0, minAreaCells: 1, simplifyTol: 0.1 };

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
});
