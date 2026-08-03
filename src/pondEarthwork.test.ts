import { describe, expect, it } from 'vitest';
import { designPondEarthwork, pondTerrainPatch, POND_FREEBOARD_M,
  POND_OUTER_SLOPE } from './pondEarthwork';
import type { TerrainRecord } from './types';

const SIZE = 64;
// 0.002° of latitude ≈ 222.6 m, so samples land ~3.5 m apart.
const BOUNDS = { west: 0, south: 0, east: 0.002, north: 0.002 };
const SPAN_M = 0.002 * 111320;
const CELL_M = SPAN_M / (SIZE - 1);

function terrain(heightAt: (x: number, y: number) => number): TerrainRecord {
  const sampleHeights: number[] = [];
  for (let y = 0; y < SIZE; y++) for (let x = 0; x < SIZE; x++) sampleHeights.push(heightAt(x, y));
  return { schemaVersion: 3, key: 'pond-earthwork', mountainName: 'Test', latitude: 0,
    longitude: 0, areaSizeMeters: Math.round(SPAN_M), sampleGridSize: SIZE, sampleHeights,
    bounds: BOUNDS, climate: { monthly: [] }, sourceType: 'live', createdAt: '', updatedAt: '' };
}

/** A square pond centred on the grid, `halfM` metres to a side from centre. */
function squareBoundary(halfM: number): [number, number][] {
  const halfDeg = halfM / 111320;
  const cx = 0.001, cy = 0.001;
  return [[cx - halfDeg, cy - halfDeg], [cx + halfDeg, cy - halfDeg],
    [cx + halfDeg, cy + halfDeg], [cx - halfDeg, cy + halfDeg], [cx - halfDeg, cy - halfDeg]];
}

const flat = () => terrain(() => 100);
/** 20% fall to the east. */
const slope = () => terrain((x) => 100 + x * CELL_M * 0.2);

describe('pond earthwork', () => {
  it('rings a flat-ground pool with a berm and charges it as fill', () => {
    const design = designPondEarthwork(flat(), squareBoundary(20),
      { topElevationM: 101, poolAreaM2: 1600 })!;
    expect(design.crestElevationM).toBeCloseTo(101 + POND_FREEBOARD_M, 6);
    expect(design.fillM3).toBeGreaterThan(0);
    expect(design.cutM3).toBeLessThan(1);
    expect(design.balanceM3).toBeCloseTo(design.cutM3 - design.fillM3, 6);
    // Ground is a metre under full pool the whole way round, so the entire
    // shoreline has to be built up.
    expect(design.bermLengthM).toBeCloseTo(160, 0);
    expect(design.maxBermHeightM).toBeCloseTo(1 + POND_FREEBOARD_M, 1);
    expect(design.maxDepthM).toBeCloseTo(1, 1);
    // The berm's inner face lays back into the pool, so mean depth falls short
    // of the metre of water standing over the middle.
    expect(design.averageDepthM).toBeGreaterThan(0.5);
    expect(design.averageDepthM).toBeLessThan(1);
  });

  it('excavates to the design floor and deepens the pool', () => {
    const shallow = designPondEarthwork(flat(), squareBoundary(20),
      { topElevationM: 101, poolAreaM2: 1600 })!;
    const dug = designPondEarthwork(flat(), squareBoundary(20),
      { topElevationM: 101, excavationDepthM: 2, poolAreaM2: 1600 })!;
    expect(dug.floorElevationM).toBe(99);
    expect(dug.cutM3).toBeGreaterThan(shallow.cutM3);
    expect(dug.capacityM3).toBeGreaterThan(shallow.capacityM3);
    expect(dug.maxDepthM).toBeCloseTo(2, 1);
    // Digging the floor is what lets a pond pay for its own berm.
    expect(dug.balanceM3).toBeGreaterThan(shallow.balanceM3);
  });

  it('cuts the uphill shoreline and fills the downhill one on a slope', () => {
    const design = designPondEarthwork(slope(), squareBoundary(20),
      { topElevationM: 100 + 32 * CELL_M * 0.2, poolAreaM2: 1600 })!;
    expect(design.cutM3).toBeGreaterThan(0);
    expect(design.fillM3).toBeGreaterThan(0);
    expect(design.maxCutDepthM).toBeGreaterThan(1);
    expect(design.maxBermHeightM).toBeGreaterThan(1);
  });

  it('lays the berm back to natural ground instead of ending in a wall', () => {
    const record = slope();
    const design = designPondEarthwork(record, squareBoundary(20),
      { topElevationM: 100 + 32 * CELL_M * 0.2, poolAreaM2: 1600 })!;
    const patched = Float32Array.from(record.sampleHeights);
    for (let i = 0; i < design.patchIndices.length; i++)
      patched[design.patchIndices[i]] = design.patchHeights[i];
    // No neighbouring pair of samples may step by more than the steepest face
    // the design uses, plus the natural fall already in the ground.
    const limit = CELL_M / Math.min(POND_OUTER_SLOPE, 1) + CELL_M * 0.2 + 1e-3;
    for (let y = 0; y < SIZE; y++) for (let x = 1; x < SIZE; x++) {
      expect(Math.abs(patched[y * SIZE + x] - patched[y * SIZE + x - 1])).toBeLessThanOrEqual(limit);
    }
    // Every edit is a real move, and the far corners are untouched.
    for (let i = 0; i < design.patchIndices.length; i++) {
      expect(Math.abs(design.patchHeights[i] -
        record.sampleHeights[design.patchIndices[i]])).toBeGreaterThanOrEqual(1e-3);
    }
    const touched = new Set(design.patchIndices);
    expect(touched.has(0)).toBe(false);
    expect(touched.has(SIZE * SIZE - 1)).toBe(false);
  });

  it('produces a committable patch with contours and a disturbed footprint', () => {
    const record = slope();
    const design = designPondEarthwork(record, squareBoundary(20),
      { topElevationM: 100 + 32 * CELL_M * 0.2, poolAreaM2: 1600 })!;
    const patch = pondTerrainPatch(record, design);
    expect(patch.contourSegments.length % 5).toBe(0);
    expect(patch.contourSegments.every(Number.isFinite)).toBe(true);
    expect(patch.disturbancePolygons.length).toBeGreaterThan(0);
    expect(patch.disturbancePolygons[0][0].length).toBeGreaterThan(3);
  });

  it('singles out the contours the grade moves, for the pre-build highlight', () => {
    const record = slope();
    const design = designPondEarthwork(record, squareBoundary(20),
      { topElevationM: 100 + 32 * CELL_M * 0.2, poolAreaM2: 1600 })!;
    const patch = pondTerrainPatch(record, design);
    const key = (segments: number[]) => {
      const keys = new Set<string>();
      for (let i = 0; i + 4 < segments.length; i += 5) keys.add(segments.slice(i, i + 5).join(','));
      return keys;
    };
    const all = key(patch.contourSegments), edited = key(patch.editedContourSegments);
    expect(edited.size).toBeGreaterThan(0);
    expect(edited.size).toBeLessThan(all.size);
    for (const segment of edited) expect(all.has(segment)).toBe(true);
    // The highlight stays over the pond: nothing on the far side of the grid.
    for (let i = 0; i + 4 < patch.editedContourSegments.length; i += 5)
      expect(Math.abs(patch.editedContourSegments[i] - 0.5)).toBeLessThan(0.35);
  });

  it('rejects terrain it cannot read', () => {
    const broken = { ...flat(), sampleHeights: [1, 2, 3] } as TerrainRecord;
    expect(designPondEarthwork(broken, squareBoundary(20),
      { topElevationM: 101, poolAreaM2: 1600 })).toBeNull();
  });
});
