import { describe, it, expect } from 'vitest';
import { PERIMETER_MARGIN_M, expandBoundsByMeters, isUsCoverage } from './elevation';
import { boundsForSquareMeters } from './geo';

const CENTER = { latitude: 39.6061, longitude: -106.355 };

describe('expandBoundsByMeters (offline perimeter extent)', () => {
  const core = boundsForSquareMeters(CENTER.latitude, CENTER.longitude, 4000);

  it('grows the bbox by the given metres on every side, keeping the centre fixed', () => {
    const ring = expandBoundsByMeters(core, PERIMETER_MARGIN_M);
    expect((ring.west + ring.east) / 2).toBeCloseTo((core.west + core.east) / 2, 9);
    expect((ring.south + ring.north) / 2).toBeCloseTo((core.south + core.north) / 2, 9);
    // A 4 km box grown by 3 km per side spans ~10 km each way.
    const expectedLatSpan = (core.north - core.south) + 2 * (PERIMETER_MARGIN_M / 111320);
    expect(ring.north - ring.south).toBeCloseTo(expectedLatSpan, 9);
    // Latitude margin is exactly 3 km; longitude margin is ≥ that (cos(lat) < 1).
    const latMarginKm = ((ring.north - core.north) * 111320) / 1000;
    expect(latMarginKm).toBeCloseTo(PERIMETER_MARGIN_M / 1000, 3);
  });

  it('fully contains the core so the composite has core inside and the ring around', () => {
    const ring = expandBoundsByMeters(core, PERIMETER_MARGIN_M);
    expect(ring.west).toBeLessThan(core.west);
    expect(ring.east).toBeGreaterThan(core.east);
    expect(ring.south).toBeLessThan(core.south);
    expect(ring.north).toBeGreaterThan(core.north);
  });
});

describe('isUsCoverage', () => {
  it('accepts a selection within the contiguous US', () => {
    const bounds = boundsForSquareMeters(CENTER.latitude, CENTER.longitude, 4000);
    expect(isUsCoverage(bounds)).toBe(true);
  });

  it('rejects a selection outside US coverage (Whistler, BC)', () => {
    const bounds = boundsForSquareMeters(50.1163, -122.9574, 4000);
    expect(isUsCoverage(bounds)).toBe(false);
  });
});
