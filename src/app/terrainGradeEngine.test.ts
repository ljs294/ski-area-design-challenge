import { describe, expect, it } from 'vitest';
import type { SavedTrailPart } from '../types';
import { gradeTerrainForTrail, MAX_FACE_SLOPE } from './terrainGradeEngine';
import { TRAVERSE_MAX_GRADE, TRAVERSE_MIN_GRADE } from './trailGradeLine';

// A 334 m square at ~3.5 m per cell — enough room for a wide run plus the cut
// and fill faces that have to fit inside it.
const bounds = { west: 0, south: 0, east: 0.003, north: 0.003 };
const N = 97;
const cellM = 111_320 * bounds.east / (N - 1);
const lngAt = (col: number) => (col / (N - 1)) * bounds.east;
const latAt = (row: number) => (1 - row / (N - 1)) * bounds.north;
const indexOf = (row: number, col: number) => row * N + col;

/**
 * `across` is the rise per metre toward the east (the cross slope a north-south
 * run has to bench through); `along` is the drop per metre toward the south.
 * `across` may be a function of row, for a hillside that steepens partway down.
 */
function hillside(across: number | ((row: number) => number), along = 0.02): number[] {
  const heights = new Array<number>(N * N);
  for (let row = 0; row < N; row++) {
    const slope = typeof across === 'function' ? across(row) : across;
    for (let col = 0; col < N; col++) {
      heights[indexOf(row, col)] =
        1200 - row * cellM * along + (col - 48) * cellM * slope;
    }
  }
  return heights;
}

/** A north-south run down column 48, painted `widthM` wide. */
function traversePart(widthM: number): SavedTrailPart {
  const halfCols = widthM / 2 / cellM;
  return {
    polygon: [[
      [lngAt(48 - halfCols), latAt(10)],
      [lngAt(48 + halfCols), latAt(10)],
      [lngAt(48 + halfCols), latAt(86)],
      [lngAt(48 - halfCols), latAt(86)],
      [lngAt(48 - halfCols), latAt(10)],
    ]],
    centerline: [
      [lngAt(48), latAt(10)],
      [lngAt(48), latAt(35)],
      [lngAt(48), latAt(60)],
      [lngAt(48), latAt(86)],
    ],
    centerlineElevM: [],
  };
}

function applyPatch(heights: number[], result: ReturnType<typeof gradeTerrainForTrail>) {
  const patched = heights.slice();
  for (let i = 0; i < result.patchIndices.length; i++)
    patched[result.patchIndices[i]] = result.patchHeights[i];
  return patched;
}

function insidePolygon(lng: number, lat: number, polygon: [number, number][][]): boolean {
  let inside = false;
  for (const ring of polygon) {
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const [xi, yi] = ring[i], [xj, yj] = ring[j];
      if (yi > lat !== yj > lat &&
          lng < ((xj - xi) * (lat - yi)) / (yj - yi) + xi) inside = !inside;
    }
  }
  return inside;
}

/** Steepest slope between horizontally adjacent cells anywhere in the patch. */
function steepestPatchSlope(
  patched: number[],
  result: ReturnType<typeof gradeTerrainForTrail>
): number {
  const touched = new Set([...result.patchIndices]);
  let steepest = 0;
  for (const index of touched) {
    const row = Math.floor(index / N), col = index % N;
    for (const [dr, dc] of [[0, 1], [1, 0]] as const) {
      const rr = row + dr, cc = col + dc;
      if (rr >= N || cc >= N) continue;
      steepest = Math.max(steepest,
        Math.abs(patched[indexOf(rr, cc)] - patched[index]) / cellM);
    }
  }
  return steepest;
}

/** Segments as comparable keys, for subset checks. */
const segmentKeys = (data: Float32Array) => {
  const keys: string[] = [];
  for (let i = 0; i + 4 < data.length; i += 5)
    keys.push(data.slice(i, i + 5).join(','));
  return keys;
};

describe('benched corridor grading', () => {
  it('levels the run across its width, which is what squares the contours', () => {
    const heights = hillside(0.20);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(60)], brushWidthM: 60,
      contourGridSize: N, contourIntervalM: 2,
    });
    const patched = applyPatch(heights, result);

    expect(result.ungradedLengthM).toBe(0);
    expect(result.patchIndices.length).toBeGreaterThan(0);
    expect(result.maxGroundCrossSlopePct).toBeGreaterThan(19);
    expect(result.cutM3).toBeGreaterThan(0);
    expect(result.fillM3).toBeGreaterThan(0);
    expect(result.balanceM3).toBeCloseTo(result.cutM3 - result.fillM3, 6);

    // The graded surface is dead level across the run wherever the bench is
    // established. A contour is a level line, so contours here can only run
    // across the trail — the whole point of the exercise.
    for (const row of [30, 40, 48, 56, 66]) {
      const centre = patched[indexOf(row, 48)];
      for (let col = 45; col <= 51; col++)
        expect(patched[indexOf(row, col)]).toBeCloseTo(centre, 3);
      // ...and the ground it replaced was anything but level.
      const naturalStep = Math.abs(heights[indexOf(row, 51)] - heights[indexOf(row, 45)]);
      expect(naturalStep).toBeGreaterThan(4);
    }
  });

  it('never touches a cell outside the painted run', () => {
    const heights = hillside(0.20);
    const part = traversePart(60);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [part], brushWidthM: 60,
      contourGridSize: N,
    });
    expect(result.patchIndices.length).toBeGreaterThan(0);
    for (const index of result.patchIndices) {
      const row = Math.floor(index / N), col = index % N;
      expect(insidePolygon(lngAt(col), latAt(row), part.polygon)).toBe(true);
    }
    // The stored footprint is exactly what was painted — grading never grows it.
    expect(result.expandedPolygons[0]).toEqual(part.polygon);
    expect(result.disturbancePolygons[0]).toEqual(part.polygon);
  });

  it('builds no face steeper than 45°', () => {
    const heights = hillside(0.30);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(90)], brushWidthM: 90,
      contourGridSize: N,
    });
    const patched = applyPatch(heights, result);
    expect(result.ungradedLengthM).toBe(0);
    expect(steepestPatchSlope(patched, result)).toBeLessThanOrEqual(MAX_FACE_SLOPE + 1e-4);
  });

  it('grades what it can and leaves a too-steep stretch alone', () => {
    // Gentle at the top and bottom, swelling smoothly past 45° across the
    // middle. Smoothly, so the only cliff in the test is one grading built.
    const heights = hillside((row) => 0.20 + 1.1 * Math.exp(-((row - 48) ** 2) / 50));
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(60)], brushWidthM: 60,
      contourGridSize: N,
    });
    const patched = applyPatch(heights, result);
    const delta = (row: number) => patched[indexOf(row, 48)] - heights[indexOf(row, 48)];

    // Grading is a tool, not a gate: the run still gets built.
    expect(result.patchIndices.length).toBeGreaterThan(0);
    expect(result.ungradedLengthM).toBeGreaterThan(0);
    expect(result.infeasibleLines.length).toBeGreaterThan(0);
    expect(result.maxGroundCrossSlopePct).toBeGreaterThan(100);

    // The steep middle kept its natural ground...
    expect(delta(48)).toBeCloseTo(0, 6);
    // ...while the gentle top benched level.
    const centre = patched[indexOf(20, 48)];
    for (let col = 45; col <= 51; col++)
      expect(patched[indexOf(20, col)]).toBeCloseTo(centre, 3);

    // And the join is a ramp, not a wall: the graded surface eases back onto
    // natural ground rather than stopping dead against it.
    for (let row = 20; row < 48; row++) {
      expect(Math.abs(delta(row + 1) - delta(row)) / cellM)
        .toBeLessThanOrEqual(MAX_FACE_SLOPE + 1e-4);
    }
  });

  it('cannot bench a hillside steeper than 45° at any painted width', () => {
    const heights = hillside(1.2);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(120)], brushWidthM: 120,
      contourGridSize: N,
    });
    expect(result.patchIndices).toHaveLength(0);
    expect(result.ungradedLengthM).toBeGreaterThan(0);
    expect(result.maxGroundCrossSlopePct).toBeGreaterThan(100);
  });

  it('highlights the contours it moved, and only those', () => {
    const heights = hillside(0.20);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(60)], brushWidthM: 60,
      contourGridSize: N, contourIntervalM: 2,
    });
    const all = new Set(segmentKeys(result.contourSegments));
    const edited = segmentKeys(result.editedContourSegments);

    expect(edited.length).toBeGreaterThan(0);
    expect(edited.length).toBeLessThan(all.size);
    for (const key of edited) expect(all.has(key)).toBe(true);

    // Every highlighted segment sits on or beside the run, never out on the
    // untouched hillside. Segments are in unit space; the run spans ~±8.5 cells
    // about column 48.
    for (let i = 0; i + 4 < result.editedContourSegments.length; i += 5) {
      const col = (result.editedContourSegments[i] + result.editedContourSegments[i + 2])
        / 2 * (N - 1);
      expect(Math.abs(col - 48)).toBeLessThanOrEqual(11);
    }
  });

  it('gives a traverse a designed descending grade line', () => {
    // 6% along the run, 20% across it: the classic benched cat track.
    const heights = hillside(0.20, 0.06);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(60)], brushWidthM: 60,
      contourGridSize: N,
    });
    expect(result.ungradedLengthM).toBe(0);

    const profile = result.gradedElevations[0];
    expect(profile).toHaveLength(4);
    const vertexRows = [10, 35, 60, 86];
    for (let i = 1; i < profile.length; i++) {
      const runM = (vertexRows[i] - vertexRows[i - 1]) * cellM;
      const grade = (profile[i - 1] - profile[i]) / runM;
      expect(grade).toBeGreaterThanOrEqual(TRAVERSE_MIN_GRADE - 1e-6);
      expect(grade).toBeLessThanOrEqual(TRAVERSE_MAX_GRADE + 1e-6);
    }
  });

  it('flattens a contour-hugging run rather than trenching it downhill', () => {
    // Nearly level along the run: there is no skiable descent to be had here
    // without digging, and digging is not on offer.
    const heights = hillside(0.20, 0.005);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(60)], brushWidthM: 60,
      contourGridSize: N,
    });
    const profile = result.gradedElevations[0];
    // Never uphill, never steeper than the band, and it still descends further
    // than the mountain does on its own.
    for (let i = 1; i < profile.length; i++) {
      expect(profile[i]).toBeLessThanOrEqual(profile[i - 1] + 1e-6);
    }
    const runM = (86 - 10) * cellM;
    const designed = (profile[0] - profile[3]) / runM;
    expect(designed).toBeGreaterThan(0.005);
    expect(designed).toBeLessThanOrEqual(TRAVERSE_MAX_GRADE + 1e-6);
  });

  it('leaves a fall-line run following the mountain', () => {
    const heights = hillside(0.02, 0.45);
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(60)], brushWidthM: 60,
      contourGridSize: N,
    });
    const profile = result.gradedElevations[0];
    const runM = (86 - 10) * cellM;
    // A 45% pitch stays a 45% pitch — it is not flattened into a traverse.
    expect((profile[0] - profile[3]) / runM).toBeCloseTo(0.45, 2);
  });

  it('keeps a road platform level and lets it grade outside the pavement', () => {
    const heights = hillside(0.25, 0.02);
    const part = traversePart(7);
    const result = gradeTerrainForTrail({
      kind: 'road',
      heights, gridSize: N, bounds, parts: [part], brushWidthM: 7,
      maxWidthMultiplier: 3,
      contourGridSize: N,
    });
    const patched = applyPatch(heights, result);
    expect(result.ungradedLengthM).toBe(0);
    expect(patched[indexOf(48, 47)]).toBeCloseTo(patched[indexOf(48, 49)], 4);
    // A 7 m pavement cannot bench inside 7 m, so a road alone grades past its
    // own footprint: the disturbed section is wider than the pavement.
    expect(result.maxDisturbedWidthM).toBeGreaterThan(7);
    expect(result.cutM3).toBeGreaterThan(0);
    expect(result.fillM3).toBeGreaterThan(0);
  });

  it('never edits protected trail cells and tapers before their boundary', () => {
    const heights = hillside(0.20);
    const protectedPolygon: [number, number][][] = [[
      [lngAt(24), latAt(45)],
      [lngAt(72), latAt(45)],
      [lngAt(72), latAt(53)],
      [lngAt(24), latAt(53)],
      [lngAt(24), latAt(45)],
    ]];
    const result = gradeTerrainForTrail({
      heights, gridSize: N, bounds, parts: [traversePart(60)], brushWidthM: 60,
      protectedPolygons: [protectedPolygon],
      contourGridSize: N,
    });
    const patched = applyPatch(heights, result);
    for (let row = 45; row <= 53; row++) for (let col = 24; col <= 72; col++)
      expect(patched[indexOf(row, col)]).toBe(heights[indexOf(row, col)]);
    // The graded surface tapers into the protected trail rather than ending in
    // a step at its boundary.
    for (let col = 34; col <= 62; col++) for (const [inside, outside] of
      [[45, 44], [53, 54]] as const) {
      const step = Math.abs(patched[indexOf(inside, col)] - patched[indexOf(outside, col)]);
      expect(step / cellM).toBeLessThanOrEqual(MAX_FACE_SLOPE + 1e-4);
    }
  });

  it('preserves holes and resolves duplicate/multipart work deterministically', () => {
    const base = traversePart(60);
    const withHole: SavedTrailPart = {
      ...base,
      polygon: [...base.polygon, [
        [lngAt(45), latAt(45)],
        [lngAt(51), latAt(45)],
        [lngAt(51), latAt(51)],
        [lngAt(45), latAt(51)],
        [lngAt(45), latAt(45)],
      ]],
    };
    const input = {
      heights: hillside(0.20),
      gridSize: N,
      bounds,
      parts: [withHole, base],
      brushWidthM: 60,
      contourGridSize: N,
      contourIntervalM: 2,
    };
    const first = gradeTerrainForTrail(input);
    const second = gradeTerrainForTrail(input);
    expect([...first.patchIndices]).toEqual([...second.patchIndices]);
    expect([...first.patchHeights]).toEqual([...second.patchHeights]);
    expect([...first.patchIndices]).not.toContain(indexOf(48, 48));
    expect(first.patchHeights.every(Number.isFinite)).toBe(true);

    const single = gradeTerrainForTrail({ ...input, parts: [base] });
    const duplicate = gradeTerrainForTrail({ ...input, parts: [base, base] });
    expect(duplicate.cutM3 + duplicate.fillM3)
      .toBeCloseTo(single.cutM3 + single.fillM3, 5);
  });

  it('rejects a grid whose dimensions do not match', () => {
    expect(() => gradeTerrainForTrail({
      heights: [1, 2, 3], gridSize: N, bounds, parts: [traversePart(60)],
      brushWidthM: 60,
    })).toThrow(/dimensions/);
  });
});
