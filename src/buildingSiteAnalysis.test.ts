import { describe, expect, it } from 'vitest';
import {
  BUILDING_PAD_APRON_M, SLOPE_FOUNDATION_CLEARANCE_M, analyzeBuildingSite,
} from './buildingSiteAnalysis';
import type { BuildingSiteInput } from './buildingSiteAnalysis';

const N = 65;
const BOUNDS = { west: 0, south: 0, east: 0.01, north: 0.01 };

function input(heightAt: (column: number, row: number) => number,
  foundationMode: 'flattened' | 'slope' = 'flattened'): BuildingSiteInput {
  const heights: number[] = [];
  for (let row = 0; row < N; row++) for (let column = 0; column < N; column++)
    heights.push(heightAt(column, row));
  return {
    center: [0.005, 0.005], bearingDeg: 0,
    dimensions: { lengthM: 18.288, widthM: 12.192, eaveHeightM: 4.8768 },
    foundationMode, heights, gridSize: N, bounds: BOUNDS,
    terrainRevision: 7, baseElevationChecksum: 'elevation-a',
    buildingGeometryKey: `test-${foundationMode}`,
  };
}

describe('building site analysis', () => {
  it('chooses the median pad datum, including a six-foot apron', () => {
    const outcome = analyzeBuildingSite(input((column) => column === 32 ? 100 : 80));
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.result.foundationMode).toBe('flattened');
    expect(outcome.result.finishedFloorElevationM).toBeCloseTo(100, 5);
    expect(outcome.result.terrainGraded).toBe(true);
    expect(outcome.result.patchIndices.length).toBeGreaterThan(0);
    expect(outcome.result.disturbancePolygons.length).toBeGreaterThan(0);
    expect(outcome.result.padRing).not.toEqual(outcome.result.footprintRing);
    expect(outcome.result.padRing[0]).not.toEqual(outcome.result.footprintRing[0]);
    expect(BUILDING_PAD_APRON_M).toBeCloseTo(1.8288, 7);
  });

  it('uses eight clockwise perimeter samples and does not edit slope terrain', () => {
    const outcome = analyzeBuildingSite(input((column, row) => 100 + column + row, 'slope'));
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.result.perimeterSamples).toHaveLength(8);
    expect(outcome.result.perimeterElevationsM).toHaveLength(8);
    expect(outcome.result.finishedFloorElevationM).toBeCloseTo(
      Math.max(...outcome.result.perimeterElevationsM) + SLOPE_FOUNDATION_CLEARANCE_M, 5);
    expect(outcome.result.patchIndices).toHaveLength(0);
    expect(outcome.result.terrainGraded).toBe(false);
    expect(outcome.result.earthwork).toEqual({ cutM3: 0, fillM3: 0, balanceM3: 0 });
  });

  it('rejects a flattened grade whose pad cannot daylight', () => {
    const outcome = analyzeBuildingSite(input((column, row) =>
      column >= 31 && column <= 33 && row >= 31 && row <= 33 ? 1000 : 0));
    // The exact diagnostic can evolve, but this site must never yield a
    // reviewable grade with a cut face escaping the terrain package.
    expect(outcome.ok).toBe(false);
  });

  it('rejects missing elevation at a slope perimeter sample', () => {
    const bad = input(() => 100, 'slope');
    const heights = Array.from(bad.heights);
    heights[32 * N + 32] = Number.NaN;
    bad.heights = heights;
    const outcome = analyzeBuildingSite(bad);
    expect(outcome.ok).toBe(false);
  });
});
