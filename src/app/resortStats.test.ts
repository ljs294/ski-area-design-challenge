import { describe, expect, it } from 'vitest';
import { resortTrailTotals, snowmakingWaterCapacityM3 } from './resortStats';
import type { SavedDam, SavedPond, SavedTrail } from '../types';

const run = (id: string, lengthM: number, areaM2: number): SavedTrail => ({
  id, name: id, parts: [], brushWidthM: 30, areaM2, lengthM, verticalM: null,
  avgSlopeDeg: 0, maxSlopeDeg: 0, difficulty: 'green', status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
});

describe('resortTrailTotals', () => {
  // The stats panel prints one skiable-area figure for the whole resort; it has
  // to be the sum of the same per-run areas the trail panels show, or the total
  // stops reconciling with its parts.
  it('sums run count, length and footprint area', () => {
    const totals = resortTrailTotals([run('a', 800, 40_000), run('b', 1_200, 60_000)]);
    expect(totals).toEqual({ count: 2, totalLengthM: 2_000, totalAreaM2: 100_000 });
  });

  it('reports zeroes for a resort with no runs', () => {
    expect(resortTrailTotals([])).toEqual({ count: 0, totalLengthM: 0, totalAreaM2: 0 });
  });
});

describe('snowmakingWaterCapacityM3', () => {
  const dam = (capacityM3: number) => ({ capacityM3 } as SavedDam);
  const pond = (capacityM3: number, isSnowmaking?: boolean) =>
    ({ capacityM3, isSnowmaking } as SavedPond);

  it('includes all dam ponds and only standalone ponds designated for snowmaking', () => {
    expect(snowmakingWaterCapacityM3([dam(2_000)], [pond(1_000, true), pond(4_000, false)]))
      .toBe(3_000);
  });

  it('includes legacy standalone ponds whose designation is absent', () => {
    expect(snowmakingWaterCapacityM3([], [pond(750)])).toBe(750);
  });
});
