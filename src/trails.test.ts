import { describe, it, expect } from 'vitest';
import {
  difficultyForSlopes,
  trailStats,
  orientTopToBottom,
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
  it('grades by the harder of average and max slope', () => {
    expect(difficultyForSlopes(5, 10)).toBe('green'); // both gentle
    expect(difficultyForSlopes(10, 20)).toBe('blue'); // max lifts it a grade
    expect(difficultyForSlopes(30, 30)).toBe('black'); // sustained steep
    expect(difficultyForSlopes(10, 45)).toBe('red'); // one expert pitch
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
    polygon: [square],
    spine: SPINE,
    brushWidthM: 30,
    spineElevM: [2000, 1900, 1800],
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
      { ...valid, spine: [SPINE[0]] }, // spine too short
      { ...valid, polygon: [[[0, 0]]] }, // ring too short
      valid,
    ]);
    expect(out).toHaveLength(1);
    expect(out[0].verticalM).toBe(200);
    expect(out[0].lengthM).toBeGreaterThan(200);
  });

  it('recomputes difficulty when the stored grade is missing/invalid', () => {
    const { difficulty: _drop, ...noGrade } = valid;
    // ~42° pitch → expert (red), not the stored green.
    expect(sanitizeTrails([noGrade])[0].difficulty).toBe('red');
    expect(sanitizeTrails([{ ...valid, difficulty: 'nonsense' }])[0].difficulty).toBe('red');
  });

  it('honors a valid stored difficulty override', () => {
    expect(sanitizeTrails([{ ...valid, difficulty: 'blue' }])[0].difficulty).toBe('blue');
  });

  it('defaults a bad brush width and drops mismatched elevations', () => {
    const out = sanitizeTrails([{ ...valid, brushWidthM: -3, spineElevM: [1] }]);
    expect(out[0].brushWidthM).toBe(DEFAULT_BRUSH_WIDTH_M);
    expect(out[0].spineElevM).toEqual([]); // wrong length → dropped
    expect(out[0].verticalM).toBeNull();
  });
});

describe('nextTrailName', () => {
  it('fills the first gap', () => {
    const trail = (name: string) => ({ name }) as SavedTrail;
    expect(nextTrailName([])).toBe('Run 1');
    expect(nextTrailName([trail('Run 1'), trail('Run 3')])).toBe('Run 2');
  });
});
