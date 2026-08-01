import { describe, expect, it } from 'vitest';
import { sanitizeTrails } from './trails';
import { hydrateJunctions, hydrateTopology, splitTrailAt } from './topology';

const rawTrail = {
  id: 'run', name: 'Run', parts: [{
    polygon: [[[-121.501, 46.931], [-121.499, 46.931], [-121.499, 46.929],
      [-121.501, 46.929], [-121.501, 46.931]]],
    centerline: [[-121.5, 46.931], [-121.5, 46.93], [-121.5, 46.929]],
    centerlineElevM: [300, 250, 200],
  }], brushWidthM: 30, status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
};

describe('persisted trail topology', () => {
  it('migrates a continuous legacy centerline into a durable segment and endpoint junctions', () => {
    const trails = sanitizeTrails([rawTrail]);
    expect(trails[0].parts[0].segments).toHaveLength(1);
    expect(trails[0].parts[0].segments?.[0].id).toBe('run:0:0');
    const junctions = hydrateJunctions(trails, []);
    expect(junctions.map((junction) => junction.id)).toEqual([
      'junction:run:0:start', 'junction:run:0:end',
    ]);
  });

  it('splits one segment exactly, interpolates elevation, and reuses the junction', () => {
    const trails = sanitizeTrails([rawTrail]);
    const junctions = hydrateJunctions(trails, []);
    let sequence = 0;
    const first = splitTrailAt(trails, junctions, 'run', [-121.5, 46.93],
      () => `new-${++sequence}`)!;
    const segments = first.trails[0].parts[0].segments!;
    expect(segments).toHaveLength(2);
    expect(segments[0].id).toBe('run:0:0');
    expect(segments[0].toJunctionId).toBe(first.junction.id);
    expect(segments[1].fromJunctionId).toBe(first.junction.id);
    expect(segments[0].centerlineElevM.at(-1)).toBeCloseTo(250);
    const again = splitTrailAt(first.trails, first.junctions, 'run', first.junction.point,
      () => `new-${++sequence}`)!;
    expect(again.junction.id).toBe(first.junction.id);
    expect(again.trails[0].parts[0].segments).toHaveLength(2);
  });

  it('migrates an explicit legacy trail anchor into one shared junction', () => {
    const child = { ...rawTrail, id: 'child', name: 'Child',
      parts: [{ ...rawTrail.parts[0],
        centerline: [[-121.5, 46.93], [-121.499, 46.929]], centerlineElevM: [250, 200] }],
      anchor: { kind: 'trail', trailId: 'run', point: [-121.5, 46.93] } };
    const migrated = hydrateTopology(sanitizeTrails([rawTrail, child]), [], [], []);
    const target = migrated.trails.find((trail) => trail.id === 'run')!;
    const branch = migrated.trails.find((trail) => trail.id === 'child')!;
    expect(target.parts[0].segments).toHaveLength(2);
    const shared = branch.parts[0].segments![0].fromJunctionId;
    expect(target.parts[0].segments!.some((segment) =>
      segment.fromJunctionId === shared || segment.toJunctionId === shared)).toBe(true);
  });
});
