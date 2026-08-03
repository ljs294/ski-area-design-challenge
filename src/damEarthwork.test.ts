import { describe, expect, it } from 'vitest';
import { DAM_CREST_WIDTH_M, DAM_DOWNSTREAM_SLOPE, DAM_FREEBOARD_M,
  DAM_UPSTREAM_SLOPE, designDamEmbankment } from './damEarthwork';
import type { TerrainRecord } from './types';

const SIZE = 64;
// 0.002° of latitude ≈ 222.6 m, so samples land ~3.5 m apart.
const BOUNDS = { west: 0, south: 0, east: 0.002, north: 0.002 };
const SPAN_M = 0.002 * 111320;
const CELL_M = SPAN_M / (SIZE - 1);

function terrain(heightAt: (x: number, y: number) => number): TerrainRecord {
  const sampleHeights: number[] = [];
  for (let y = 0; y < SIZE; y++) for (let x = 0; x < SIZE; x++) sampleHeights.push(heightAt(x, y));
  return { schemaVersion: 3, key: 'dam-earthwork', mountainName: 'Test', latitude: 0,
    longitude: 0, areaSizeMeters: Math.round(SPAN_M), sampleGridSize: SIZE, sampleHeights,
    bounds: BOUNDS, climate: { monthly: [] }, sourceType: 'live', createdAt: '', updatedAt: '' };
}

/** A valley running north-south down the middle: floor at 100 on the centre
 * column, walls climbing 1 m per 2 m of horizontal distance either side. */
const valley = () => terrain((x) => 100 + Math.abs(x - 31) * CELL_M * 0.5);

/** Alignment across the valley at mid-height, west bank to east bank. */
function crossing(halfCells: number): [[number, number], [number, number]] {
  const step = 0.002 / (SIZE - 1);
  return [[(31 - halfCells) * step, 0.001], [(31 + halfCells) * step, 0.001]];
}

function patched(record: TerrainRecord, indices: Uint32Array, heights: Float32Array): Float32Array {
  const out = Float32Array.from(record.sampleHeights);
  for (let i = 0; i < indices.length; i++) out[indices[i]] = heights[i];
  return out;
}

describe('dam embankment', () => {
  it('grades an embankment a freeboard above full pool and charges it as fill', () => {
    const record = valley();
    const design = designDamEmbankment(record, crossing(8), 104, 1)!;
    expect(design.crestElevationM).toBeCloseTo(104 + DAM_FREEBOARD_M, 6);
    expect(design.fillM3).toBeGreaterThan(0);
    // An embankment is built up, never dug out — the bill is borrow material.
    expect(design.cutM3).toBe(0);
    expect(design.balanceM3).toBeCloseTo(-design.fillM3, 6);
    expect(design.maxHeightM).toBeCloseTo(4 + DAM_FREEBOARD_M, 1);
    expect(design.averageHeightM).toBeGreaterThan(0);
    expect(design.averageHeightM).toBeLessThanOrEqual(design.maxHeightM);
    expect(design.truncated).toBe(false);
  });

  it('carries a crest deck over the built stretch only', () => {
    const record = valley();
    // The alignment runs well past the point where the banks rise above the
    // crest, so both ends are buried in the abutment.
    const design = designDamEmbankment(record, crossing(24), 104, 1)!;
    expect(design.builtLengthM).toBeGreaterThan(0);
    expect(design.builtLengthM).toBeLessThan(design.lengthM);
    expect(design.crestRing.length).toBeGreaterThan(4);
    expect(design.crestRing[0]).toEqual(design.crestRing.at(-1));
  });

  it('lays both faces back into the hillside instead of ending in a wall', () => {
    const record = valley();
    const design = designDamEmbankment(record, crossing(12), 104, 1)!;
    const surface = patched(record, design.patchIndices, design.patchHeights);
    // No neighbouring pair of samples may step by more than the steeper face
    // allows, plus the natural fall already in the valley wall.
    const limit = CELL_M / DAM_DOWNSTREAM_SLOPE + CELL_M * 0.5 + 1e-3;
    for (let y = 1; y < SIZE; y++) for (let x = 0; x < SIZE; x++) {
      expect(Math.abs(surface[y * SIZE + x] - surface[(y - 1) * SIZE + x]))
        .toBeLessThanOrEqual(limit);
    }
    // Fill only, and the far corners are left alone.
    for (let i = 0; i < design.patchIndices.length; i++)
      expect(design.patchHeights[i]).toBeGreaterThan(record.sampleHeights[design.patchIndices[i]]);
    const touched = new Set(design.patchIndices);
    expect(touched.has(0)).toBe(false);
    expect(touched.has(SIZE * SIZE - 1)).toBe(false);
  });

  it('lays the wet face flatter than the dry one', () => {
    const record = terrain(() => 100);
    // Flat ground isolates the two faces: the only relief is the dam itself.
    const design = designDamEmbankment(record, crossing(16), 120, 1)!;
    const surface = patched(record, design.patchIndices, design.patchHeights);
    const heights: number[] = [];
    for (let y = 0; y < SIZE; y++) heights.push(surface[y * SIZE + 31]);
    const crestRow = heights.indexOf(Math.max(...heights));
    const toeReach = (step: -1 | 1) => {
      let row = crestRow;
      while (row + step >= 0 && row + step < SIZE && heights[row + step] > 100.001) row += step;
      return Math.abs(row - crestRow) * CELL_M;
    };
    const heightM = 20 + DAM_FREEBOARD_M;
    // A horizontal alignment puts the reservoir on the southern flank, which is
    // rows below the crest in this frame.
    expect(toeReach(1)).toBeGreaterThan(toeReach(-1));
    expect(toeReach(1)).toBeCloseTo(heightM * DAM_UPSTREAM_SLOPE + DAM_CREST_WIDTH_M / 2, -1);
    expect(toeReach(-1)).toBeCloseTo(heightM * DAM_DOWNSTREAM_SLOPE + DAM_CREST_WIDTH_M / 2, -1);
  });

  it('rejects terrain it cannot read', () => {
    const broken = { ...valley(), sampleHeights: [1, 2, 3] } as TerrainRecord;
    expect(designDamEmbankment(broken, crossing(8), 104, 1)).toBeNull();
  });
});
