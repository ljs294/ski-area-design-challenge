import { describe, it, expect } from 'vitest';
import { fetchElevationGrid, isUsCoverage, sampleGridSizeFor } from './elevation';
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
      // smaller-but-square grid is a pass, not just an exact match.
      const actualGridSize = Math.round(Math.sqrt(grid.length));
      expect(actualGridSize * actualGridSize).toBe(grid.length);
      expect(actualGridSize).toBeLessThanOrEqual(maxGridSize);
      expect(grid.every((h) => Number.isFinite(h))).toBe(true);

      let min = Infinity;
      let max = -Infinity;
      for (const h of grid) {
        if (h < min) min = h;
        if (h > max) max = h;
      }
      expect(min).toBeGreaterThan(EXPECTED_MIN_METERS);
      expect(max).toBeLessThan(EXPECTED_MAX_METERS);
    },
    TEST_TIMEOUT_MS
  );
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
