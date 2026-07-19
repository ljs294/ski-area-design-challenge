import { describe, it, expect } from 'vitest';
import { haversineMeters } from './geo';
import {
  fixedGripCapacityPph,
  fixedGripDerived,
  liftStats,
  nextLiftName,
  orientBottomToTop,
  sanitizeLifts,
  fmtDistance,
  FIXED_GRIP_SPEC,
} from './lifts';
import type { SavedLift } from './types';

// Crystal Mountain, WA — base area to summit, roughly 2.4 km apart.
const BASE: [number, number] = [-121.4745, 46.9282];
const SUMMIT: [number, number] = [-121.5045, 46.9285];

describe('haversineMeters', () => {
  it('matches a known distance (1 degree of latitude ≈ 111.2 km)', () => {
    const d = haversineMeters([-121.5, 46.0], [-121.5, 47.0]);
    expect(d).toBeGreaterThan(110_000);
    expect(d).toBeLessThan(112_500);
  });

  it('is zero for identical points and symmetric', () => {
    expect(haversineMeters(BASE, BASE)).toBe(0);
    expect(haversineMeters(BASE, SUMMIT)).toBeCloseTo(haversineMeters(SUMMIT, BASE), 6);
  });
});

describe('liftStats', () => {
  it('computes slope length from horizontal + vertical', () => {
    const s = liftStats([BASE, SUMMIT], [1300, 2100]);
    expect(s.verticalM).toBe(800);
    expect(s.topIndex).toBe(1);
    expect(s.lengthM).toBeCloseTo(Math.hypot(s.horizontalM, 800), 6);
    expect(s.lengthM).toBeGreaterThan(s.horizontalM);
  });

  it('falls back to horizontal-only when elevations are unknown', () => {
    const s = liftStats([BASE, SUMMIT], [null, 2100]);
    expect(s.verticalM).toBeNull();
    expect(s.topIndex).toBeNull();
    expect(s.lengthM).toBe(s.horizontalM);
  });
});

describe('orientBottomToTop', () => {
  it('flips a top-first line so index 0 is the bottom terminal', () => {
    const { points, elevs } = orientBottomToTop([SUMMIT, BASE], [2100, 1300]);
    expect(points[0]).toEqual(BASE);
    expect(elevs).toEqual([1300, 2100]);
  });

  it('leaves bottom-first and unknown-elevation lines untouched', () => {
    expect(orientBottomToTop([BASE, SUMMIT], [1300, 2100]).points[0]).toEqual(BASE);
    expect(orientBottomToTop([SUMMIT, BASE], [null, 1300]).points[0]).toEqual(SUMMIT);
  });
});

describe('fixedGripCapacityPph', () => {
  it('is 600 pph per seat at the fixed 6 s headway', () => {
    expect(fixedGripCapacityPph(2)).toBe(1200);
    expect(fixedGripCapacityPph(3)).toBe(1800);
    expect(fixedGripCapacityPph(4)).toBe(2400);
  });
});

describe('fixedGripDerived', () => {
  it('derives headway, spacing, and ride time from length', () => {
    const d = fixedGripDerived(1500);
    expect(d.headwayS).toBe(FIXED_GRIP_SPEC.headwayS); // fixed 6 s
    expect(d.carrierSpacingM).toBeCloseTo(6 * FIXED_GRIP_SPEC.ropeSpeedMps, 6);
    expect(d.rideTimeS).toBeCloseTo(1500 / FIXED_GRIP_SPEC.ropeSpeedMps, 6);
  });
});

describe('sanitizeLifts', () => {
  const valid: SavedLift = {
    id: 'l1',
    name: 'Lift 1',
    liftClass: 'fixed-grip',
    points: [BASE, SUMMIT],
    endpointElevM: [1300, 2100],
    lengthM: 0, // stale on purpose — sanitize must recompute
    verticalM: null,
    chairSize: 2,
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('passes legacy empty arrays through', () => {
    expect(sanitizeLifts([])).toEqual([]);
  });

  it('drops garbage and keeps valid lifts, recomputing cached stats', () => {
    const out = sanitizeLifts([
      null,
      42,
      { liftClass: 'gondola' },
      { ...valid, points: [BASE] }, // wrong point count
      { ...valid, points: [BASE, ['x', 1]] }, // non-numeric coord
      valid,
    ]);
    expect(out).toHaveLength(1);
    expect(out[0].verticalM).toBe(800);
    expect(out[0].lengthM).toBeGreaterThan(800);
  });

  it('defaults bad chair sizes and migrates legacy Single (1) to Double', () => {
    expect(sanitizeLifts([{ ...valid, chairSize: 7 }])[0].chairSize).toBe(2);
    expect(sanitizeLifts([{ ...valid, chairSize: 1 }])[0].chairSize).toBe(2);
  });

  it('drops the legacy capacityPph field from old saves', () => {
    const out = sanitizeLifts([{ ...valid, capacityPph: 1200 }]);
    expect(out).toHaveLength(1);
    expect('capacityPph' in out[0]).toBe(false);
  });

  it('keeps a valid status and defaults legacy/garbage ones to complete', () => {
    const planning = sanitizeLifts([{ ...valid, status: 'planning' }]);
    expect(planning[0].status).toBe('planning');
    // Legacy saves (no status field) and bad values fall back to complete.
    const { status: _drop, ...noStatus } = valid;
    expect(sanitizeLifts([noStatus])[0].status).toBe('complete');
    expect(sanitizeLifts([{ ...valid, status: 'nonsense' }])[0].status).toBe('complete');
  });
});

describe('nextLiftName', () => {
  it('fills the first gap', () => {
    const lift = (name: string) => ({ name }) as SavedLift;
    expect(nextLiftName([])).toBe('Lift 1');
    expect(nextLiftName([lift('Lift 1'), lift('Lift 3')])).toBe('Lift 2');
  });
});

describe('fmtDistance', () => {
  it('formats per unit system', () => {
    expect(fmtDistance(1000, 'metric')).toBe('1,000 m');
    expect(fmtDistance(1000, 'imperial')).toBe('3,281 ft');
  });
});
