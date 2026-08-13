import { describe, expect, it } from 'vitest';
import { haversineMeters } from './geo';
import {
  DEFAULT_LIFT_TYPE_ID,
  LIFT_TYPE_CATALOG,
  LIFT_TYPE_SPECS,
  TRAM_DWELL_S,
  fmtDistance,
  formatLiftLabel,
  liftPerformance,
  liftStats,
  liftTypeLabel,
  nextLiftIdentifier,
  nextLiftName,
  orientBottomToTop,
  sanitizeLifts,
} from './lifts';
import type { LiftTypeId, SavedLift } from './types';

const BASE: [number, number] = [-121.4745, 46.9282];
const SUMMIT: [number, number] = [-121.5045, 46.9285];

describe('haversineMeters', () => {
  it('matches a known distance (1 degree of latitude ≈ 111.2 km)', () => {
    const distance = haversineMeters([-121.5, 46], [-121.5, 47]);
    expect(distance).toBeGreaterThan(110_000);
    expect(distance).toBeLessThan(112_500);
  });

  it('is zero for identical points and symmetric', () => {
    expect(haversineMeters(BASE, BASE)).toBe(0);
    expect(haversineMeters(BASE, SUMMIT)).toBeCloseTo(haversineMeters(SUMMIT, BASE), 6);
  });
});

describe('liftStats', () => {
  it('computes slope length from horizontal and vertical', () => {
    const stats = liftStats([BASE, SUMMIT], [1300, 2100]);
    expect(stats.verticalM).toBe(800);
    expect(stats.topIndex).toBe(1);
    expect(stats.lengthM).toBeCloseTo(Math.hypot(stats.horizontalM, 800), 6);
  });

  it('falls back to horizontal-only when elevations are unknown', () => {
    const stats = liftStats([BASE, SUMMIT], [null, 2100]);
    expect(stats.verticalM).toBeNull();
    expect(stats.topIndex).toBeNull();
    expect(stats.lengthM).toBe(stats.horizontalM);
  });
});

describe('orientBottomToTop', () => {
  it('flips a top-first line and leaves unresolved lines untouched', () => {
    expect(orientBottomToTop([SUMMIT, BASE], [2100, 1300]))
      .toEqual({ points: [BASE, SUMMIT], elevs: [1300, 2100] });
    expect(orientBottomToTop([SUMMIT, BASE], [null, 1300]).points[0]).toEqual(SUMMIT);
  });
});

describe('lift type catalog', () => {
  const expected: Array<[LiftTypeId, string, number, number | 'tram']> = [
    ['rope-tow', 'Rope Tow', 500, 700],
    ['magic-carpet', 'Magic Carpet', 100, 1000],
    ['t-bar', 'T-Bar', 400, 1200],
    ['fixed-grip-double', 'Fixed-Grip Double Chairlift', 400, 1200],
    ['fixed-grip-triple', 'Fixed-Grip Triple Chairlift', 400, 1800],
    ['fixed-grip-quad', 'Fixed-Grip Quad Chairlift', 400, 2400],
    ['detachable-quad', 'Detachable Quad Chairlift', 1000, 2400],
    ['detachable-six-pack', 'Detachable Six-Pack Chairlift', 1000, 3000],
    ['detachable-eight-pack', 'Detachable Eight-Pack Chairlift', 1000, 3200],
    ['gondola-8', 'Detachable 8-Person Gondola', 1000, 2400],
    ['gondola-10', 'Detachable 10-Person Gondola', 1000, 2800],
    ['gondola-12', 'Detachable 12-Person Gondola', 1000, 3000],
    ['tram-60', '60-Person Aerial Tram', 2000, 'tram'],
    ['tram-80', '80-Person Aerial Tram', 2000, 'tram'],
  ];

  it('defines every approved type, label, speed, and fixed capacity exactly once', () => {
    expect(LIFT_TYPE_SPECS).toHaveLength(14);
    expect(new Set(LIFT_TYPE_SPECS.map((spec) => spec.id)).size).toBe(14);
    for (const [id, label, speedFpm, capacity] of expected) {
      expect(liftTypeLabel(id)).toBe(label);
      expect(LIFT_TYPE_CATALOG[id].operatingSpeedFpm).toBe(speedFpm);
      const performance = liftPerformance(id, 1000);
      expect(performance.operatingSpeedMps).toBeCloseTo(speedFpm * 0.00508, 8);
      if (capacity !== 'tram') expect(performance.capacityPph).toBe(capacity);
      expect(performance.rideTimeS).toBeCloseTo(1000 / (speedFpm * 0.00508), 8);
    }
  });

  it('derives both tram capacities from one-way ride time plus the four-minute dwell', () => {
    const lengthM = 1016;
    const rideTimeS = 100;
    expect(liftPerformance('tram-60', lengthM).capacityPph)
      .toBeCloseTo((60 * 3600) / (rideTimeS + TRAM_DWELL_S), 8);
    expect(liftPerformance('tram-80', lengthM).capacityPph)
      .toBeCloseTo((80 * 3600) / (rideTimeS + TRAM_DWELL_S), 8);
  });
});

describe('sanitizeLifts', () => {
  const current: SavedLift = {
    id: 'l1',
    identifier: 'A',
    name: 'Lift 1',
    liftTypeId: 'gondola-10',
    points: [BASE, SUMMIT],
    endpointElevM: [1300, 2100],
    lengthM: 0,
    verticalM: null,
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('round-trips all schema-14 leaf types and recomputes geometry stats', () => {
    const out = sanitizeLifts(LIFT_TYPE_SPECS.map((spec, index) => ({
      ...current,
      id: `lift-${index}`,
      liftTypeId: spec.id,
    })));
    expect(out.map((lift) => lift.liftTypeId)).toEqual(LIFT_TYPE_SPECS.map((spec) => spec.id));
    expect(out.every((lift) => lift.verticalM === 800 && lift.lengthM > 800)).toBe(true);
  });

  it('migrates schema 1-13 fixed-grip sizes and defaults invalid legacy sizes to Double', () => {
    const legacy = (chairSize: unknown) => {
      const { liftTypeId: _drop, ...base } = current;
      return { ...base, liftClass: 'fixed-grip', chairSize };
    };
    expect(sanitizeLifts([legacy(2)])[0].liftTypeId).toBe('fixed-grip-double');
    expect(sanitizeLifts([legacy(3)])[0].liftTypeId).toBe('fixed-grip-triple');
    expect(sanitizeLifts([legacy(4)])[0].liftTypeId).toBe('fixed-grip-quad');
    expect(sanitizeLifts([legacy(1)])[0].liftTypeId).toBe(DEFAULT_LIFT_TYPE_ID);
    expect(sanitizeLifts([legacy(7)])[0].liftTypeId).toBe(DEFAULT_LIFT_TYPE_ID);
  });

  it('rejects malformed current discriminators and unrelated legacy lift classes', () => {
    expect(sanitizeLifts([{ ...current, liftTypeId: 'future-hyperlift' }])).toEqual([]);
    const { liftTypeId: _drop, ...base } = current;
    expect(sanitizeLifts([{ ...base, liftClass: 'gondola' }])).toEqual([]);
  });

  it('keeps status and identifier compatibility while dropping obsolete derived fields', () => {
    const hydrated = sanitizeLifts([{ ...current, identifier: ' 12 ', capacityPph: 1 }])[0];
    expect(hydrated.identifier).toBe('12');
    expect('capacityPph' in hydrated).toBe(false);
    const { status: _status, identifier: _identifier, ...legacy } = current;
    const noStatus = sanitizeLifts([legacy])[0];
    expect(noStatus.status).toBe('complete');
    expect(noStatus.identifier).toBeUndefined();
  });
});

describe('lift naming', () => {
  it('formats identifiers and allocates the first unused default number', () => {
    expect(formatLiftLabel({ identifier: 'A', name: 'Summit Express' }))
      .toBe('A - Summit Express');
    expect(formatLiftLabel({ name: 'Legacy Double' })).toBe('Legacy Double');
    const existing = [
      { identifier: '1', name: 'Summit Express' },
      { identifier: '2', name: 'Lift 7' },
    ] as SavedLift[];
    expect(nextLiftIdentifier(existing)).toBe('3');
    expect(nextLiftName(existing)).toBe('Lift 3');
  });
});

describe('fmtDistance', () => {
  it('formats per unit system', () => {
    expect(fmtDistance(1000, 'metric')).toBe('1,000 m');
    expect(fmtDistance(1000, 'imperial')).toBe('3,281 ft');
  });
});
