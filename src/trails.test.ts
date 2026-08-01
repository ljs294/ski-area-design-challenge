import { describe, it, expect } from 'vitest';
import {
  difficultyForSlopes,
  trailStats,
  orientTopToBottom,
  fillElevationGaps,
  pinTrailHead,
  pinTrailEndpoints,
  sanitizeTrails,
  nextTrailName,
  DEFAULT_BRUSH_WIDTH_M,
} from './trails';
import type { SavedTrail } from './types';

// A short north–south spine, ~111 m between adjacent stations (0.001° lat).
const SPINE: [number, number][] = [
  [-121.5, 46.9300],
  [-121.5, 46.9290],
  [-121.5, 46.9280],
];

describe('difficultyForSlopes', () => {
  it('grades the 3:1 weighted average/max rating pitch', () => {
    expect(difficultyForSlopes(5, 10)).toBe('green'); // both gentle
    expect(difficultyForSlopes(16, 20)).toBe('blue');
    expect(difficultyForSlopes(30, 30)).toBe('black'); // sustained steep
    expect(difficultyForSlopes(10, 45)).toBe('blue'); // isolated steep pitch is tempered
  });

  it('respects the 16 / 24 / 37° band edges', () => {
    expect(difficultyForSlopes(15.9, 15.9)).toBe('green');
    expect(difficultyForSlopes(16, 16)).toBe('blue');
    expect(difficultyForSlopes(23.9, 23.9)).toBe('blue');
    expect(difficultyForSlopes(24, 24)).toBe('black');
    expect(difficultyForSlopes(37, 37)).toBe('red');
  });
});

describe('trailStats', () => {
  it('computes vertical, length, and slope from spine + elevations', () => {
    // Uniform 100 m drop per ~111 m station → ~42° pitch throughout.
    const s = trailStats(SPINE, [2000, 1900, 1800]);
    expect(s.verticalM).toBe(200);
    expect(s.lengthM).toBeGreaterThan(200); // 3D length exceeds pure vertical
    expect(s.avgSlopeDeg).toBeGreaterThan(35);
    expect(s.maxSlopeDeg).toBeGreaterThanOrEqual(s.avgSlopeDeg);
  });

  it('falls back to horizontal-only with zero slope when elevations are absent', () => {
    const s = trailStats(SPINE, []);
    expect(s.verticalM).toBeNull();
    expect(s.avgSlopeDeg).toBe(0);
    expect(s.maxSlopeDeg).toBe(0);
    expect(s.lengthM).toBeGreaterThan(200); // still ~222 m of horizontal run
  });
});

describe('orientTopToBottom', () => {
  it('reverses a bottom-first spine so station 0 is the summit', () => {
    const { spine, elevM } = orientTopToBottom(SPINE, [1800, 1900, 2000]);
    expect(elevM[0]).toBe(2000);
    expect(spine[0]).toEqual(SPINE[2]);
  });

  it('leaves an already top-first spine untouched', () => {
    const { elevM } = orientTopToBottom(SPINE, [2000, 1900, 1800]);
    expect(elevM[0]).toBe(2000);
  });
});

describe('fillElevationGaps', () => {
  it('interpolates an interior gap between its resolved neighbours', () => {
    expect(fillElevationGaps([2000, null, null, 1700])).toEqual([2000, 1900, 1800, 1700]);
  });

  it('extends the nearest resolved value across leading and trailing gaps', () => {
    // Nothing to interpolate against past the ends — a flat shoulder beats a
    // hole, and it keeps the array the same length as the centerline.
    expect(fillElevationGaps([null, null, 1900, 1800, null])).toEqual([1900, 1900, 1900, 1800, 1800]);
  });

  it('returns null only when no point resolved at all', () => {
    expect(fillElevationGaps([null, null, null])).toBeNull();
    expect(fillElevationGaps([])).toBeNull();
    // A single survivor is still enough to build a (flat) profile from.
    expect(fillElevationGaps([null, 1850, null])).toEqual([1850, 1850, 1850]);
  });
});

describe('pinTrailHead', () => {
  it('makes the exact anchor station 0 and keeps elevations aligned', () => {
    const head: [number, number] = [-121.5, 46.931];
    const other = {
      polygon: [[[0, 0], [1, 0], [1, 1], [0, 0]]] as [number, number][][],
      centerline: [[-121.6, 46.9], [-121.6, 46.8]] as [number, number][],
      centerlineElevM: [1500, 1400],
    };
    const connected = {
      polygon: other.polygon,
      centerline: [...SPINE].reverse(),
      centerlineElevM: [1800, 1900, 2000],
    };

    const pinned = pinTrailHead([other, connected], head);

    expect(pinned[0].centerline[0]).toEqual(head);
    expect(pinned[0].centerline.slice(1)).toEqual(SPINE.slice(1));
    expect(pinned[0].centerlineElevM).toEqual([2000, 1900, 1800]);
    expect(pinned[1]).toBe(other);
  });
});

describe('pinTrailEndpoints', () => {
  // The paint flow pins once at analysis and again after elevations resolve, on
  // the already-pinned result. That second call must succeed — if it can't, the
  // run is rejected in review with no way forward, which is exactly the failure
  // this pair of tests exists to keep from coming back.
  const head: [number, number] = [-121.5, 46.9305];
  const tail: [number, number] = [-121.5, 46.9295];
  const box: [number, number][][] = [[
    [-121.501, 46.931], [-121.499, 46.931], [-121.499, 46.929],
    [-121.501, 46.929], [-121.501, 46.931],
  ]];

  it('pins both ends onto the footprint containing them', () => {
    const pinned = pinTrailEndpoints([{ polygon: box, centerline: SPINE, centerlineElevM: [] }], head, tail);
    expect(pinned).not.toBeNull();
    expect(pinned![0].centerline[0]).toEqual(head);
    expect(pinned![0].centerline.at(-1)).toEqual(tail);
  });

  it('is idempotent — re-pinning its own output still resolves', () => {
    const once = pinTrailEndpoints([{ polygon: box, centerline: SPINE, centerlineElevM: [] }], head, tail)!;
    // Elevations arrive between the two calls; geometry is untouched.
    const sampled = once.map((p) => ({ ...p, centerlineElevM: p.centerline.map(() => 1800) }));
    const twice = pinTrailEndpoints(sampled, head, tail);
    expect(twice).not.toBeNull();
    expect(twice![0].centerline).toEqual(once[0].centerline);
    expect(twice![0].centerlineElevM).toHaveLength(once[0].centerline.length);
  });
});

describe('sanitizeTrails', () => {
  const square: [number, number][] = [
    [-121.5, 46.93],
    [-121.499, 46.93],
    [-121.499, 46.929],
    [-121.5, 46.929],
    [-121.5, 46.93],
  ];
  const valid: SavedTrail = {
    id: 't1',
    name: 'Run 1',
    parts: [{ polygon: [square], centerline: SPINE, centerlineElevM: [2000, 1900, 1800] }],
    brushWidthM: 30,
    areaM2: 0,
    lengthM: 0, // stale on purpose — sanitize must recompute
    verticalM: null,
    avgSlopeDeg: 0,
    maxSlopeDeg: 0,
    difficulty: 'green',
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('passes an empty array through', () => {
    expect(sanitizeTrails([])).toEqual([]);
  });

  it('drops garbage and keeps valid runs, recomputing cached stats', () => {
    const out = sanitizeTrails([
      null,
      42,
      { id: 'x', name: 'y' }, // no geometry
      { ...valid, parts: [{ ...valid.parts[0], centerline: [SPINE[0]] }] },
      { ...valid, parts: [{ ...valid.parts[0], polygon: [[[0, 0]]] }] },
      valid,
    ]);
    expect(out).toHaveLength(1);
    expect(out[0].verticalM).toBe(200);
    expect(out[0].lengthM).toBeGreaterThan(200);
  });

  it('migrates the schema-v1 polygon/spine shape into one analyzed part', () => {
    const legacy = {
      ...valid,
      polygon: valid.parts[0].polygon,
      spine: valid.parts[0].centerline,
      spineElevM: valid.parts[0].centerlineElevM,
      parts: undefined,
    };
    const out = sanitizeTrails([legacy]);
    expect(out[0].parts).toHaveLength(1);
    expect(out[0].parts[0].centerline).toEqual(SPINE);
    expect(out[0].areaM2).toBeGreaterThan(0);
  });

  it('recomputes difficulty when the stored grade is missing/invalid', () => {
    const { difficulty: _drop, ...noGrade } = valid;
    // ~42° pitch → expert (red), not the stored green.
    expect(sanitizeTrails([noGrade])[0].difficulty).toBe('red');
    expect(sanitizeTrails([{ ...valid, difficulty: 'nonsense' }])[0].difficulty).toBe('red');
  });

  it('replaces a stored override with the automatic rating', () => {
    expect(sanitizeTrails([{ ...valid, difficulty: 'blue' }])[0].difficulty).toBe('red');
  });

  it('defaults a bad brush width and drops mismatched elevations', () => {
    const out = sanitizeTrails([{ ...valid, brushWidthM: -3,
      parts: [{ ...valid.parts[0], centerlineElevM: [1] }] }]);
    expect(out[0].brushWidthM).toBe(DEFAULT_BRUSH_WIDTH_M);
    expect(out[0].parts[0].centerlineElevM).toEqual([]); // wrong length → dropped
    expect(out[0].verticalM).toBeNull();
  });

  it('preserves only an explicit terrain-grading marker', () => {
    expect(sanitizeTrails([{ ...valid, terrainGraded: true }])[0].terrainGraded).toBe(true);
    expect(sanitizeTrails([{ ...valid, terrainGraded: 'yes' }])[0].terrainGraded).toBe(false);
    expect(sanitizeTrails([valid])[0].terrainGraded).toBe(false);
  });

  it('sanitizes persisted earthwork estimates', () => {
    expect(sanitizeTrails([{ ...valid,
      earthwork: { cutM3: 120, fillM3: 30, balanceM3: 90 },
    }])[0].earthwork).toEqual({ cutM3: 120, fillM3: 30, balanceM3: 90 });
    expect(sanitizeTrails([{ ...valid,
      earthwork: { cutM3: -1, fillM3: 30, balanceM3: -31 },
    }])[0].earthwork).toBeUndefined();
  });
});

describe('nextTrailName', () => {
  it('fills the first gap', () => {
    const trail = (name: string) => ({ name }) as SavedTrail;
    expect(nextTrailName([])).toBe('Run 1');
    expect(nextTrailName([trail('Run 1'), trail('Run 3')])).toBe('Run 2');
  });
});
