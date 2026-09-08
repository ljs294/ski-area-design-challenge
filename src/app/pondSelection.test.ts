import { describe, expect, it } from 'vitest';
import type { SavedPond } from '../types';
import { isStandalonePond } from './pondSelection';

const pond = (id: string, isSnowmaking?: boolean): SavedPond => ({
  id, name: id, boundary: [[0, 0], [0, 1], [1, 0], [0, 0]], topElevationM: 1000,
  areaM2: 100, averageDepthM: 2, maxDepthM: 3, capacityM3: 200, isSnowmaking, createdAt: 'now',
});

describe('isStandalonePond', () => {
  it('identifies only ponds explicitly excluded from snowmaking', () => {
    const ponds = [pond('standalone', false), pond('snowmaking', true), pond('legacy')];
    expect(isStandalonePond(ponds, 'standalone')).toBe(true);
    expect(isStandalonePond(ponds, 'snowmaking')).toBe(false);
    expect(isStandalonePond(ponds, 'legacy')).toBe(false);
    expect(isStandalonePond(ponds, 'missing')).toBe(false);
  });
});
