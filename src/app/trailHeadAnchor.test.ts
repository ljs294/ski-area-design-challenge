import { describe, expect, it } from 'vitest';
import type { SavedLift, SavedTrail } from '../types';
import { nearestTrailHeadAnchor, nearestTrailTailAnchor, TrailAnchorIndex } from './trailHeadAnchor';

const origin: [number, number] = [-121.5, 46.93];
const at = (eastM: number, northM: number): [number, number] => [
  origin[0] + eastM / (111_320 * Math.cos(origin[1] * Math.PI / 180)),
  origin[1] + northM / 111_320,
];
const lift: SavedLift = {
  id: 'lift', name: 'Lift', liftTypeId: 'fixed-grip-double', points: [at(0, 0), at(0, 200)],
  endpointElevM: [100, 200], lengthM: 200, verticalM: 100,
  status: 'complete', createdAt: '',
};
const trail: SavedTrail = {
  id: 'trail', name: 'Run', parts: [{
    polygon: [[at(-20, 50), at(20, 50), at(20, 150), at(-20, 50)]],
    centerline: [at(0, 50), at(0, 150)], centerlineElevM: [180, 120],
  }], brushWidthM: 20, areaM2: 1, lengthM: 100, verticalM: 60,
  avgSlopeDeg: 1, maxSlopeDeg: 1, difficulty: 'green', status: 'complete', createdAt: '',
};

describe('nearestTrailHeadAnchor', () => {
  it('selects the directional lift terminal', () => {
    expect(nearestTrailTailAnchor(at(3, 2), [lift], [], 60)).toMatchObject({ kind: 'lift', end: 'base' });
    expect(nearestTrailHeadAnchor(at(3, 198), [lift], [], 60)).toMatchObject({ kind: 'lift', end: 'top' });
  });

  it('projects exactly onto a trail centerline', () => {
    const anchor = nearestTrailHeadAnchor(at(25, 100), [], [trail], 60);
    expect(anchor).toMatchObject({ kind: 'trail', trailId: 'trail' });
    expect(anchor?.point[0]).toBeCloseTo(at(0, 100)[0], 10);
    expect(anchor?.point[1]).toBeCloseTo(at(0, 100)[1], 10);
  });

  it('rejects targets outside the snap radius', () => {
    expect(nearestTrailHeadAnchor(at(100, 100), [lift], [trail], 40)).toBeNull();
  });

  it('prefers a lift terminal over an exactly coincident trail', () => {
    const coincident = { ...trail, parts: [{ ...trail.parts[0], centerline: [at(-50, 0), at(50, 0)] }] };
    expect(nearestTrailTailAnchor(at(0, 0), [lift], [coincident], 60)).toMatchObject({ kind: 'lift' });
  });

  it('limits a dense resort query to nearby indexed geometry', () => {
    const distant = Array.from({ length: 500 }, (_, index): SavedTrail => ({
      ...trail,
      id: `distant-${index}`,
      parts: [{ ...trail.parts[0], centerline: [at(1_000 + index * 100, 50),
        at(1_000 + index * 100, 150)] }],
    }));
    const index = new TrailAnchorIndex([lift], [trail, ...distant]);
    expect(index.nearestHead(at(20, 100), 60)).toMatchObject({
      kind: 'trail', trailId: 'trail',
    });
    expect(index.candidateCount(at(20, 100), 60)).toBeLessThan(10);
  });
});
