import { describe, expect, it } from 'vitest';
import { fetchElevationGrid, sampleGridSizeFor } from './elevation';
import { boundsForSquareMeters } from './geo';

// Live integration tests against the USGS 3DEP exportImage endpoint. These are
// intentionally excluded from the deterministic unit-test and CI gates.
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
