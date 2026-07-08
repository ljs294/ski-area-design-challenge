import { describe, it, expect } from 'vitest';
import { fetchElevationGrid, SAMPLE_GRID_SIZE } from './elevation';
import { boundsForSquareMeters } from './geo';

// Real integration test against the live Open-Meteo elevation API — it
// downloads an actual grid, not a mock. Downloads are deliberately paced
// (see the comment atop elevation.ts) to avoid the provider's rate limit
// rather than race it, so a full 64x64 grid takes a few minutes by design.
// The generous per-test timeout below is intentional, not a mistake.
// Requires network access; skip in fully offline environments.

const TEST_TIMEOUT_MS = 300_000;

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
      const expectedPoints = SAMPLE_GRID_SIZE * SAMPLE_GRID_SIZE;
      const expectedBatches = Math.ceil(expectedPoints / 100);

      let lastProgress = { completedBatches: 0, totalBatches: 0, rateLimited: false };
      const grid = await fetchElevationGrid(bounds, (p) => {
        lastProgress = p;
      });

      expect(grid).toHaveLength(expectedPoints);
      expect(grid.every((h) => Number.isFinite(h))).toBe(true);

      const min = Math.min(...grid);
      const max = Math.max(...grid);
      expect(min).toBeGreaterThan(EXPECTED_MIN_METERS);
      expect(max).toBeLessThan(EXPECTED_MAX_METERS);

      expect(lastProgress.completedBatches).toBe(expectedBatches);
      expect(lastProgress.totalBatches).toBe(expectedBatches);
    },
    TEST_TIMEOUT_MS
  );
});
