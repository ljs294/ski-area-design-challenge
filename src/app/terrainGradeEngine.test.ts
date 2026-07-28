import { describe, expect, it } from 'vitest';
import type { SavedTrailPart } from '../types';
import { gradeTerrainForTrail, smoothTrailProfile } from './terrainGradeEngine';

const bounds = { west: 0, south: 0, east: 0.001, north: 0.001 };
const N = 41;
const lngAt = (col: number) => (col / (N - 1)) * 0.001;
const latAt = (row: number) => (1 - row / (N - 1)) * 0.001;
const indexOf = (row: number, col: number) => row * N + col;

function sideHill(): number[] {
  return Array.from({ length: N * N }, (_, index) => {
    const row = Math.floor(index / N);
    const col = index % N;
    return 220 - row * 0.65 + (col - 20) * 0.55;
  });
}

function straightPart(
  elevations = [217.4, 210.9, 204.4, 197.9]
): SavedTrailPart {
  return {
    polygon: [[
      [lngAt(10), latAt(4)],
      [lngAt(30), latAt(4)],
      [lngAt(30), latAt(36)],
      [lngAt(10), latAt(36)],
      [lngAt(10), latAt(4)],
    ]],
    centerline: [
      [lngAt(20), latAt(4)],
      [lngAt(20), latAt(14)],
      [lngAt(20), latAt(24)],
      [lngAt(20), latAt(34)],
    ],
    centerlineElevM: elevations,
  };
}

function applyPatch(heights: number[], result: ReturnType<typeof gradeTerrainForTrail>) {
  const patched = heights.slice();
  for (let i = 0; i < result.patchIndices.length; i++) {
    patched[result.patchIndices[i]] = result.patchHeights[i];
  }
  return patched;
}

describe('trail terrain grading', () => {
  it('lightly smooths without imposing a monotonic profile and fixes endpoints', () => {
    const original = [120, 112, 105, 118, 116, 108, 100];
    const smoothed = smoothTrailProfile(original);
    expect(smoothed[0]).toBe(120);
    expect(smoothed.at(-1)).toBe(100);
    expect(smoothed[3]).toBeGreaterThan(smoothed[2]);
    expect(smoothed).toEqual(smoothTrailProfile(original));
  });

  it('grades the full inner width with uphill cut, downhill fill, and an inside shoulder', () => {
    const heights = sideHill();
    const part = straightPart();
    const result = gradeTerrainForTrail({
      heights,
      gridSize: N,
      bounds,
      parts: [part],
      brushWidthM: 56,
      contourGridSize: N,
      contourIntervalM: 2,
    });
    const patched = applyPatch(heights, result);
    const row = 20;
    const target = result.gradedElevations[0][1] * 0.4 +
      result.gradedElevations[0][2] * 0.6;

    // Both sides of the inner width become the same station elevation.
    expect(patched[indexOf(row, 14)]).toBeCloseTo(target, 4);
    expect(patched[indexOf(row, 26)]).toBeCloseTo(target, 4);
    expect(patched[indexOf(row, 14)]).toBeGreaterThan(heights[indexOf(row, 14)]);
    expect(patched[indexOf(row, 26)]).toBeLessThan(heights[indexOf(row, 26)]);

    // The one-cell-inside shoulder participates, but moves less than the core.
    const shoulderDelta = Math.abs(patched[indexOf(row, 11)] - heights[indexOf(row, 11)]);
    const coreDelta = Math.abs(patched[indexOf(row, 14)] - heights[indexOf(row, 14)]);
    expect(shoulderDelta).toBeGreaterThan(0);
    expect(shoulderDelta).toBeLessThan(coreDelta);
    expect(patched[indexOf(row, 9)]).toBe(heights[indexOf(row, 9)]);
  });

  it('regenerates contours orthogonal to the centerline across the graded core', () => {
    const result = gradeTerrainForTrail({
      heights: sideHill(),
      gridSize: N,
      bounds,
      parts: [straightPart()],
      brushWidthM: 56,
      contourGridSize: N,
      contourIntervalM: 2,
    });
    const angles: number[] = [];
    for (let i = 0; i < result.contourSegments.length; i += 5) {
      const x1 = result.contourSegments[i];
      const y1 = result.contourSegments[i + 1];
      const x2 = result.contourSegments[i + 2];
      const y2 = result.contourSegments[i + 3];
      const mx = (x1 + x2) / 2;
      const my = (y1 + y2) / 2;
      if (mx < 0.36 || mx > 0.64 || my < 0.18 || my > 0.82) continue;
      const dx = x2 - x1;
      const dy = y2 - y1;
      const angle = Math.abs(Math.atan2(dx, dy)) * 180 / Math.PI;
      angles.push(Math.min(angle, 180 - angle));
    }
    expect(angles.length).toBeGreaterThan(10);
    expect(Math.min(...angles)).toBeGreaterThanOrEqual(80);
  });

  it('preserves holes, outside cells, and resolves multipart overlap deterministically', () => {
    const base = straightPart();
    const withHole: SavedTrailPart = {
      ...base,
      polygon: [...base.polygon, [
        [lngAt(18), latAt(18)],
        [lngAt(22), latAt(18)],
        [lngAt(22), latAt(22)],
        [lngAt(18), latAt(22)],
        [lngAt(18), latAt(18)],
      ]],
    };
    const shifted: SavedTrailPart = {
      ...base,
      polygon: base.polygon.map((ring) =>
        ring.map(([lng, lat]) => [lng + 0.0003, lat] as [number, number])),
      centerline: base.centerline.map(([lng, lat]) => [lng + 0.0003, lat]),
      centerlineElevM: base.centerlineElevM.map((height) => height + 2),
    };
    const input = {
      heights: sideHill(),
      gridSize: N,
      bounds,
      parts: [withHole, shifted],
      brushWidthM: 56,
      contourGridSize: N,
      contourIntervalM: 2,
    };
    const first = gradeTerrainForTrail(input);
    const second = gradeTerrainForTrail(input);
    expect([...first.patchIndices]).toEqual([...second.patchIndices]);
    expect([...first.patchHeights]).toEqual([...second.patchHeights]);
    expect([...first.patchIndices]).not.toContain(indexOf(20, 20));
    expect([...first.patchIndices]).not.toContain(indexOf(20, 8));
    expect(first.patchHeights.every(Number.isFinite)).toBe(true);
  });
});
