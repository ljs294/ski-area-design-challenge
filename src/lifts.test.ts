import { describe, it, expect } from 'vitest';
import { haversineMeters } from './geo';
import {
  capacityRange,
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

describe('capacityRange', () => {
  it('scales with chair size, double defaulting to the classic 1200 pph', () => {
    expect(capacityRange(2)).toEqual({ min: 300, max: 1800, step: 50, default: 1200 });
    expect(capacityRange(4).max).toBe(3600);
    expect(capacityRange(1).min).toBe(150);
  });
});

describe('fixedGripDerived', () => {
  it('derives headway and ride time from capacity and length', () => {
    const d = fixedGripDerived(1200, 2, 1500);
    expect(d.headwayS).toBeCloseTo(6, 6); // 2 seats * 3600 / 1200
    expect(d.carrierSpacingM).toBeCloseTo(6 * FIXED_GRIP_SPEC.ropeSpeedMps, 6);
    expect(d.rideTimeS).toBeCloseTo(1500 / FIXED_GRIP_SPEC.ropeSpeedMps, 6);
    expect(d.aggressive).toBe(false);
  });

  it('flags unrealistically tight headways', () => {
    expect(fixedGripDerived(1800, 2, 1500).aggressive).toBe(true); // 4 s headway
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
    capacityPph: 1200,
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

  it('clamps out-of-range capacity and defaults bad chair sizes', () => {
    const out = sanitizeLifts([{ ...valid, capacityPph: 99_999, chairSize: 7 }]);
    expect(out[0].chairSize).toBe(2);
    expect(out[0].capacityPph).toBe(1800); // clamped to double max
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
