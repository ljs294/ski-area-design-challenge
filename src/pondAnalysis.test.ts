import { describe, expect, it } from 'vitest';
import { analyzeStandalonePond, pondBoundaryIsSimple, sanitizePonds,
  suggestedPondTopElevationM } from './pondAnalysis';
import type { TerrainRecord } from './types';

function terrain(heights: number[]): TerrainRecord {
  return { schemaVersion: 3, key: 'pond-test', mountainName: 'Test', latitude: 0, longitude: 0,
    areaSizeMeters: 3000, sampleGridSize: 5, sampleHeights: heights,
    bounds: { west: 0, south: 0, east: 0.001, north: 0.001 },
    climate: { monthly: [] }, sourceType: 'live',
    createdAt: '', updatedAt: '' };
}

const boundary: [number, number][] = [
  [0.0001, 0.0001], [0.0009, 0.0001], [0.0009, 0.0009], [0.0001, 0.0009],
];

describe('standalone pond analysis', () => {
  it('integrates terrain depth within the user boundary', () => {
    const record = terrain(Array.from({ length: 25 }, (_, index) => 100 + (index % 5) * 0.25));
    const outcome = analyzeStandalonePond(record, boundary, 102);
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.result.boundary[0]).toEqual(outcome.result.boundary.at(-1));
    expect(outcome.result.areaM2).toBeGreaterThan(100);
    expect(outcome.result.averageDepthM).toBeGreaterThan(1);
    expect(outcome.result.capacityM3).toBeCloseTo(
      outcome.result.areaM2 * outcome.result.averageDepthM, 6);
  });

  it('reports the berm crest and an earthwork bill', () => {
    const record = terrain(Array(25).fill(100));
    const outcome = analyzeStandalonePond(record, boundary, 102);
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.result.crestElevationM).toBeCloseTo(102.6, 6);
    expect(outcome.result.excavationDepthM).toBe(0);
    expect(outcome.result.bermLengthM).toBeGreaterThan(0); // every shoreline is low
    expect(outcome.result.earthwork.balanceM3).toBeCloseTo(
      outcome.result.earthwork.cutM3 - outcome.result.earthwork.fillM3, 6);
  });

  it('trades excavation for imported fill', () => {
    const record = terrain(Array(25).fill(100));
    const shallow = analyzeStandalonePond(record, boundary, 102);
    const dug = analyzeStandalonePond(record, boundary, 102, 4);
    expect(shallow.ok && dug.ok).toBe(true);
    if (!shallow.ok || !dug.ok) return;
    expect(dug.result.excavationDepthM).toBe(4);
    expect(dug.result.earthwork.cutM3).toBeGreaterThan(shallow.result.earthwork.cutM3);
    expect(dug.result.capacityM3).toBeGreaterThan(shallow.result.capacityM3);
    expect(dug.result.earthwork.balanceM3).toBeGreaterThan(shallow.result.earthwork.balanceM3);
  });

  it('refuses a berm past earth-fill height', () => {
    const record = terrain(Array.from({ length: 25 }, (_, index) => 100 + (index % 5) * 40));
    const outcome = analyzeStandalonePond(record, boundary, 200);
    expect(outcome.ok).toBe(false);
    if (outcome.ok) return;
    expect(outcome.error).toMatch(/berm/);
  });

  it('suggests a top elevation above the enclosed terrain', () => {
    const record = terrain(Array(25).fill(100));
    expect(suggestedPondTopElevationM(record, boundary)).toBe(101);
  });

  it('rejects self-crossing and dry boundaries', () => {
    expect(pondBoundaryIsSimple([[0, 0], [1, 1], [0, 1], [1, 0]])).toBe(false);
    expect(analyzeStandalonePond(terrain(Array(25).fill(100)), boundary, 99)).toEqual({
      ok: false, error: 'Raise the top elevation above the ground inside the pond.',
    });
  });

  it('sanitizes persisted ponds while ignoring malformed entries', () => {
    const valid = { id: 'pond-1', name: 'Pond 1', boundary, topElevationM: 101,
      areaM2: 500, averageDepthM: 1, maxDepthM: 2, capacityM3: 500, createdAt: 'now' };
    expect(sanitizePonds([valid, { ...valid, id: 4 }])).toHaveLength(1);
    expect(sanitizePonds([valid])[0].boundary[0]).toEqual(sanitizePonds([valid])[0].boundary.at(-1));
    expect(sanitizePonds([valid])[0].isSnowmaking).toBe(true);
    expect(sanitizePonds([{ ...valid, isSnowmaking: false }])[0].isSnowmaking).toBe(false);
  });
});
