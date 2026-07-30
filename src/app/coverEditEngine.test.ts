import { describe, expect, it } from 'vitest';
import { boundsForSquareMeters, unitToLngLat } from '../geo';
import { TERRAIN_COVER_CODES } from '../fourClassCover';
import type { CoverGrid } from '../types';
import { processCoverEdit } from './coverEditEngine';

const bounds = boundsForSquareMeters(47, -121.5, 100);
const ring: [number, number][] = [
  unitToLngLat(0.25, 0.25, bounds), unitToLngLat(0.75, 0.25, bounds),
  unitToLngLat(0.75, 0.75, bounds), unitToLngLat(0.25, 0.75, bounds),
  unitToLngLat(0.25, 0.25, bounds),
];

function cover(fill: number): CoverGrid {
  return {
    bounds, width: 50, height: 50, cellSizeM: 2,
    data: new Uint8Array(2500).fill(fill),
    complete: true, nodataCount: 0, source: 'usgs-four-class-v1',
    vintage: '2021',
  } as CoverGrid;
}

describe('processCoverEdit', () => {
  it('returns a transferable edited grid and vector display', () => {
    const result = processCoverEdit({
      grid: cover(TERRAIN_COVER_CODES.forest),
      polygons: [[ring]],
      deriveDisplay: true,
    });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.changed).toBeGreaterThan(0);
    expect(result.gridData).toBeInstanceOf(Uint8Array);
    expect(result.displayGeometry).toBeInstanceOf(Float32Array);
    expect(result.displayStats?.vertexCount).toBeGreaterThan(0);
  });

  it('skips vectorization when the edit changes no cells', () => {
    const result = processCoverEdit({
      grid: cover(TERRAIN_COVER_CODES.grassland),
      polygons: [[ring]],
      deriveDisplay: true,
    });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.changed).toBe(0);
    expect(result.displayGeometry).toBeUndefined();
  });
});
