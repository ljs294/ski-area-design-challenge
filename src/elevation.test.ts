import { describe, it, expect } from 'vitest';
import { PERIMETER_MARGIN_M, expandBoundsByMeters, fetchElevationGrid, isUsCoverage, sampleGridSizeFor } from './elevation';
import { boundsForSquareMeters } from './geo';

// Real integration test against the live USGS 3DEP exportImage endpoint —
// it downloads an actual raster, not a mock. Unlike the old point-by-point
// EPQS approach, this is a single request per area size, so it completes in
// seconds rather than minutes.
// Requires network access; skip in fully offline environments.

const TEST_TIMEOUT_MS = 30_000;

// Vail, CO — real mountainous terrain with known elevation range
// (base ~2476m, summit ~3527m) to sanity-check the downloaded values.
const CENTER = { latitude: 39.6061, longitude: -106.355 };
const EXPECTED_MIN_METERS = 1000;
const EXPECTED_MAX_METERS = 4500;

const AVAILABLE_AREA_SIZES_METERS = [2000, 4000, 8000] as const;

describe.each(AVAILABLE_AREA_SIZES_METERS)('terrain download at %dm square', (sizeMeters) => {
  it(
    'downloads a complete, real-valued elevation grid',
    async () => {
      const bounds = boundsForSquareMeters(CENTER.latitude, CENTER.longitude, sizeMeters);
      const maxGridSize = sampleGridSizeFor(sizeMeters);

      const grid = await fetchElevationGrid(bounds, sizeMeters);

      // fetchElevationGrid may shrink the requested grid on transient
      // server-side failures (see fetchWithShrink in elevation.ts) — a
      // smaller grid is a pass, not just an exact match.
      expect(grid.width * grid.height).toBe(grid.heights.length);
      expect(Math.max(grid.width, grid.height)).toBeLessThanOrEqual(maxGridSize);
      expect(grid.heights.every((h) => Number.isFinite(h))).toBe(true);

      // Alignment invariant: the extent the service reports MUST match the
      // pixel grid's aspect ratio, or placing the grid across those bounds
      // stretches it (the exportImage extent-snap bug). Every real download
      // in CI re-checks this.
      const b = grid.bounds;
      const lonSpan = b.east - b.west;
      const latSpan = b.north - b.south;
      expect(lonSpan / latSpan).toBeCloseTo(grid.width / grid.height, 2);
      // The service expands symmetrically, so the requested center stays put.
      expect((b.west + b.east) / 2).toBeCloseTo(CENTER.longitude, 4);
      expect((b.south + b.north) / 2).toBeCloseTo(CENTER.latitude, 4);

      let min = Infinity;
      let max = -Infinity;
      for (const h of grid.heights) {
        if (h < min) min = h;
        if (h > max) max = h;
      }
      expect(min).toBeGreaterThan(EXPECTED_MIN_METERS);
      expect(max).toBeLessThan(EXPECTED_MAX_METERS);
    },
    TEST_TIMEOUT_MS
  );
});

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
