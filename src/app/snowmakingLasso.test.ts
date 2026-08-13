import { describe, expect, it } from 'vitest';
import type { SavedSnowgun } from '../types/snowmaking';
import { connectedGunIdsInLasso, normalizeLassoRect, pointInLasso } from './snowmakingLasso';

const gun = (id: string, x: number, y: number, hydrantId: string | null): SavedSnowgun => ({
  id, variantId: 'HKD_ImpulseR5_20t', point: [x, y], elevM: null, hydrantId,
  createdAt: '2026-01-01',
});

describe('snowmaking lasso geometry', () => {
  it('normalizes rectangles and includes boundaries', () => {
    const rect = normalizeLassoRect({ x: 8, y: 20 }, { x: 2, y: 4 });
    expect(rect).toEqual({ minX: 2, minY: 4, maxX: 8, maxY: 20 });
    expect(pointInLasso({ x: 2, y: 20 }, rect)).toBe(true);
    expect(pointInLasso({ x: 9, y: 20 }, rect)).toBe(false);
  });

  it('selects only connected guns and handles empty regions', () => {
    const guns = [gun('inside', 5, 5, 'hydrant-1'), gun('disconnected', 6, 6, null),
      gun('outside', 20, 20, 'hydrant-2')];
    const project = (point: [number, number]) => ({ x: point[0], y: point[1] });
    expect(connectedGunIdsInLasso(guns, project,
      normalizeLassoRect({ x: 5, y: 5 }, { x: 6, y: 6 }))).toEqual(['inside']);
    expect(connectedGunIdsInLasso(guns, project,
      normalizeLassoRect({ x: 0, y: 0 }, { x: 1, y: 1 }))).toEqual([]);
  });
});
