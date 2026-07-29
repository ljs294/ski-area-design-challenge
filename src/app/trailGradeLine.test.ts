import { describe, expect, it } from 'vitest';
import { DEFAULT_MAX_DEVIATION_M, designGradeLine, smoothTrailProfile,
  traverseWeights, TRAVERSE_MAX_GRADE, TRAVERSE_MIN_GRADE } from './trailGradeLine';

const STATION_M = 8;
const chainage = (count: number) =>
  Array.from({ length: count }, (_, i) => i * STATION_M);

/** Grade between consecutive stations; positive means descending. */
const grades = (elevations: number[], stations = chainage(elevations.length)) =>
  elevations.slice(1).map((value, i) =>
    (elevations[i] - value) / (stations[i + 1] - stations[i]));

describe('smoothTrailProfile', () => {
  it('lightly smooths without imposing a monotonic profile and fixes endpoints', () => {
    const original = [120, 112, 105, 118, 116, 108, 100];
    const smoothed = smoothTrailProfile(original);
    expect(smoothed[0]).toBe(120);
    expect(smoothed.at(-1)).toBe(100);
    expect(smoothed[3]).toBeGreaterThan(smoothed[2]);
    expect(smoothed).toEqual(smoothTrailProfile(original));
  });
});

describe('traverse detection', () => {
  it('reads a near-level contour line as a traverse and a pitch as fall line', () => {
    const stations = chainage(40);
    const traverse = stations.map((s) => 1200 - s * 0.01);
    const pitch = stations.map((s) => 1200 - s * 0.45);
    expect(Math.min(...traverseWeights(stations, traverse))).toBeGreaterThan(0.9);
    expect(Math.max(...traverseWeights(stations, pitch))).toBeLessThan(0.1);
  });
});

describe('designGradeLine', () => {
  it('gives a traverse a descent inside the band when the ground allows it', () => {
    const stations = chainage(60);
    // 6% along the run: gentle enough to count as a traverse, steep enough that
    // a band-legal descent sits right on the ground.
    const ground = stations.map((s) => 1200 - s * 0.06 + Math.sin(s / 40) * 1.5);
    const design = designGradeLine(stations, ground);

    expect(design.mode.every((mode) => mode === 'traverse')).toBe(true);
    for (const grade of grades(design.elevations, stations)) {
      expect(grade).toBeGreaterThanOrEqual(TRAVERSE_MIN_GRADE - 1e-9);
      expect(grade).toBeLessThanOrEqual(TRAVERSE_MAX_GRADE + 1e-9);
    }
  });

  it('never lets a contour-painted traverse turn uphill, and will not trench', () => {
    const stations = chainage(60);
    // Painted dead along a contour. No descent is available here without
    // digging, so the run flattens rather than trenching — but never climbs.
    const ground = stations.map(() => 1200);
    const design = designGradeLine(stations, ground);

    for (const grade of grades(design.elevations, stations)) {
      expect(grade).toBeGreaterThanOrEqual(-1e-9);
      expect(grade).toBeLessThanOrEqual(TRAVERSE_MAX_GRADE + 1e-9);
    }
    for (let i = 0; i < ground.length; i++) {
      expect(Math.abs(design.elevations[i] - ground[i]))
        .toBeLessThanOrEqual(DEFAULT_MAX_DEVIATION_M + 1e-9);
    }
    // It still spends the whole earthwork budget getting skiers moving.
    const drop = design.elevations[0] - design.elevations.at(-1)!;
    expect(drop).toBeGreaterThan(DEFAULT_MAX_DEVIATION_M);
  });

  it('stays as close to natural ground as the band allows', () => {
    const stations = chainage(40);
    const ground = stations.map((s) => 1200 - s * 0.08);
    const design = designGradeLine(stations, ground);
    // An 8% natural grade is already inside the band, so the designed line
    // should sit essentially on the ground.
    for (let i = 0; i < ground.length; i++) {
      expect(Math.abs(design.elevations[i] - ground[i])).toBeLessThan(0.5);
    }
  });

  it('leaves a fall-line pitch alone', () => {
    const stations = chainage(30);
    const ground = stations.map((s) => 1400 - s * 0.5 + Math.sin(s / 30) * 4);
    const design = designGradeLine(stations, ground);
    expect(design.elevations).toEqual(smoothTrailProfile(ground));
  });

  it('breaks grade where a traverse runs out onto a pitch instead of bending it', () => {
    const stations = chainage(60);
    const ground = stations.map((s) => s < 240 ? 1200 - s * 0.01
      : 1200 - 2.4 - (s - 240) * 0.5);
    const design = designGradeLine(stations, ground);
    expect(design.mode[5]).toBe('traverse');
    expect(design.mode.at(-5)).toBe('fall-line');
    // The pitch keeps its own steepness rather than being flattened.
    const tail = grades(design.elevations, stations).slice(-5);
    expect(Math.min(...tail)).toBeGreaterThan(0.4);
  });

  it('smooths a rough traverse into a line a bench can be cut to', () => {
    const stations = chainage(40);
    // 6% overall with metre-scale noise on top — the kind of thing a DEM gives
    // you, and exactly what a bench must not reproduce.
    const ground = stations.map((s, i) =>
      1200 - s * 0.06 + (i % 2 === 0 ? 0.9 : -0.9));
    const design = designGradeLine(stations, ground);
    const roughness = (values: number[]) => values.slice(2)
      .reduce((sum, value, i) =>
        sum + Math.abs(value - 2 * values[i + 1] + values[i]), 0);
    expect(roughness(design.elevations)).toBeLessThan(roughness(ground) / 4);
  });

  it('is deterministic and respects a caller-supplied band', () => {
    const stations = chainage(50);
    const ground = stations.map((s) => 1200 - s * 0.075 + Math.cos(s / 60) * 1.5);
    const policy = { minGrade: 0.06, maxGrade: 0.09 };
    const first = designGradeLine(stations, ground, policy);
    const second = designGradeLine(stations, ground, policy);
    expect(first.elevations).toEqual(second.elevations);
    for (const grade of grades(first.elevations, stations)) {
      expect(grade).toBeGreaterThanOrEqual(0.06 - 1e-9);
      expect(grade).toBeLessThanOrEqual(0.09 + 1e-9);
    }
  });

  it('passes a two-station run straight through', () => {
    const design = designGradeLine([0, 8], [1200, 1199]);
    expect(design.elevations).toEqual([1200, 1199]);
  });
});
