import { describe, expect, it } from 'vitest';
import type { SavedSnowgun } from '../types/snowmaking';
import { appendLassoSample, closeLassoPath, connectedGunIdsInLasso,
  pointInLasso, simplifyLassoRdp, type LassoPoint } from './snowmakingLasso';

const gun = (id: string, x: number, y: number, hydrantId: string | null): SavedSnowgun => ({
  id, variantId: 'HKD_ImpulseR5_20t', point: [x, y], elevM: null, hydrantId,
  createdAt: '2026-01-01',
});

describe('snowmaking free-form lasso geometry', () => {
  it('samples only after the minimum movement distance and closes the path', () => {
    const sampled = appendLassoSample([{ x: 0, y: 0 }], { x: 1, y: 1 });
    expect(sampled).toHaveLength(1);
    const path = closeLassoPath([{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 }]);
    expect(path[0]).toEqual(path.at(-1));
    expect(path.length).toBeGreaterThanOrEqual(4);
  });

  it('simplifies a nearly straight path with the configured tolerance', () => {
    const path: LassoPoint[] = [{ x: 0, y: 0 }, { x: 5, y: 0.5 }, { x: 10, y: 0 }];
    expect(simplifyLassoRdp(path, 2)).toEqual([path[0], path.at(-1)]);
  });

  it('handles concave rings, self-intersections, and boundary points', () => {
    const concave = [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 },
      { x: 5, y: 5 }, { x: 0, y: 10 }, { x: 0, y: 0 }];
    expect(pointInLasso({ x: 1, y: 1 }, concave)).toBe(true);
    expect(pointInLasso({ x: 6, y: 7 }, concave)).toBe(false);
    expect(pointInLasso({ x: 0, y: 5 }, concave)).toBe(true);
    const bowTie = [{ x: 0, y: 0 }, { x: 10, y: 10 }, { x: 0, y: 10 },
      { x: 10, y: 0 }, { x: 0, y: 0 }];
    expect(pointInLasso({ x: 2, y: 8 }, bowTie)).toBe(true);
  });

  it('selects only connected guns after a bounding-box and polygon test', () => {
    const guns = [gun('inside', 5, 5, 'hydrant-1'), gun('disconnected', 6, 6, null),
      gun('outside', 20, 20, 'hydrant-2')];
    const projected = new Map(guns.map((candidate) => [candidate.id,
      { x: candidate.point[0], y: candidate.point[1] }] as const));
    const ring = [{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 },
      { x: 0, y: 10 }, { x: 0, y: 0 }];
    expect(connectedGunIdsInLasso(guns, projected, ring)).toEqual(['inside']);
    expect(connectedGunIdsInLasso(guns, projected, [{ x: 30, y: 30 },
      { x: 40, y: 30 }, { x: 40, y: 40 }, { x: 30, y: 40 }])).toEqual([]);
  });
});
