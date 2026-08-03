import { describe, expect, it } from 'vitest';
import { pondRelativeRenderHeightM, smoothPondRing } from './pondGeometry';
import type { TerrainRecord } from './types';

describe('pond display geometry', () => {
  it('rounds a closed boundary without mutating the design ring', () => {
    const ring: [number, number][] = [[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]];
    const smoothed = smoothPondRing(ring);
    expect(ring).toEqual([[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]]);
    expect(smoothed.length).toBe(17);
    expect(smoothed[0]).toEqual(smoothed.at(-1));
    expect(smoothed).not.toContainEqual([0, 0]);
  });

  it('converts an absolute water elevation to a terrain-relative render height', () => {
    const terrain = { bounds: { west: 0, south: 0, east: 1, north: 1 }, sampleGridSize: 2,
      sampleHeights: new Float32Array([100, 100, 100, 100]) } as unknown as TerrainRecord;
    const ring: [number, number][] = [[0.2, 0.2], [0.8, 0.2], [0.8, 0.8], [0.2, 0.8], [0.2, 0.2]];
    expect(pondRelativeRenderHeightM(ring, 112.5, 3, terrain)).toBeCloseTo(12.5);
    expect(pondRelativeRenderHeightM(ring, 112.5, 3, null)).toBe(3);
  });
});
