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
  it('edits the transferred grid in place and returns vector display metadata', () => {
    const grid = cover(TERRAIN_COVER_CODES.forest);
    const transferredData = grid.data as Uint8Array;
    const result = processCoverEdit({
      grid,
      clearings: [{ polygon: [ring] }],
      deriveDisplay: true,
    });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.changed).toBeGreaterThan(0);
    expect(result.gridData).toBe(transferredData);
    expect(result.displayGeometry).toBeInstanceOf(Float32Array);
    expect(result.coverMetadata.byteLength).toBe(transferredData.byteLength);
    expect(result.coverMetadata.checksum).toMatch(/^fnv1a32-[0-9a-f]{8}$/);
    expect(result.displayMetadata?.vertexCount).toBeGreaterThan(0);
    expect(result.displayMetadata?.byteLength)
      .toBe(result.displayGeometry?.byteLength);
    expect(result.displayMetadata?.checksum).toMatch(/^fnv1a32-[0-9a-f]{8}$/);
  });

  it('returns the same transferred buffer and skips vectorization when no cells change', () => {
    const grid = cover(TERRAIN_COVER_CODES.grassland);
    const transferredData = grid.data as Uint8Array;
    const result = processCoverEdit({
      grid,
      clearings: [{ polygon: [ring] }],
      deriveDisplay: true,
    });
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.changed).toBe(0);
    expect(result.gridData).toBe(transferredData);
    expect(result.coverMetadata.byteLength).toBe(transferredData.byteLength);
    expect(result.displayGeometry).toBeUndefined();
    expect(result.displayMetadata).toBeUndefined();
  });
});
