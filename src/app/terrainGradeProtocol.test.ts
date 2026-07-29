import { describe, expect, it } from 'vitest';
import type { SavedTrailPart } from '../types';
import { terrainGradeGeometryKey } from './terrainGradeProtocol';

const part: SavedTrailPart = {
  polygon: [[[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]]],
  centerline: [[0.5, 0], [0.5, 1]],
  centerlineElevM: [100, 90],
};

describe('terrain grade geometry identity', () => {
  it('is deterministic and changes with geometry, elevations, or brush width', () => {
    const key = terrainGradeGeometryKey([part], 30);
    expect(terrainGradeGeometryKey([part], 30)).toBe(key);
    expect(terrainGradeGeometryKey([part], 31)).not.toBe(key);
    expect(terrainGradeGeometryKey([{ ...part, centerlineElevM: [100, 91] }], 30))
      .not.toBe(key);
    expect(terrainGradeGeometryKey([{
      ...part,
      centerline: [[0.51, 0], [0.5, 1]],
    }], 30)).not.toBe(key);
    expect(terrainGradeGeometryKey([part], 30, [part.polygon])).not.toBe(key);
    expect(terrainGradeGeometryKey([part], 30, [], 'road')).not.toBe(key);
    expect(terrainGradeGeometryKey([part], 30, [], 'trail',
      { maxWidthMultiplier: 3 })).not.toBe(key);
    expect(terrainGradeGeometryKey([part], 30, [], 'trail',
      { envelope: 'expand' })).not.toBe(key);
  });
});
