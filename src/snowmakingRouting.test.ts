import { describe, expect, it } from 'vitest';
import { deriveSnowmakingRoutingForest } from './snowmakingRouting';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingPumpPort } from './types/snowmaking';

const LAT = 46.9;
const M_PER_LNG = 111320 * Math.cos(LAT * Math.PI / 180);
function point(eastM: number): [number, number] { return [-121.5 + eastM / M_PER_LNG, LAT]; }
function node(id: string, kind: SavedSnowmakingNode['kind'], eastM: number): SavedSnowmakingNode {
  return { id, name: id, kind, point: point(eastM), elevM: 1000,
    ...(kind === 'intake' ? { source: { kind: 'pond' as const, pondId: id } } : {}),
    createdAt: 'now' };
}
function pipe(id: string, from: SavedSnowmakingNode, to: SavedSnowmakingNode,
  ports: [SnowmakingPumpPort | null, SnowmakingPumpPort | null] = [null, null]): SavedSnowmakingPipe {
  return { id, name: id, diameterIn: 8, vertices: [
    { point: from.point, elevM: from.elevM, nodeId: from.id },
    { point: to.point, elevM: to.elevM, nodeId: to.id },
  ], lengthM: Math.abs((to.point[0] - from.point[0]) * M_PER_LNG), verticalM: 0,
  segments: [{ id: `${id}:segment:0`, startVertexIndex: 0, endVertexIndex: 1,
    startPumpPort: ports[0], endPumpPort: ports[1] }], createdAt: 'now' };
}
function gun(id: string, hydrant: SavedSnowmakingNode): SavedSnowgun {
  return { id, variantId: 'HKD_ImpulseR5_10s', point: hydrant.point, elevM: hydrant.elevM,
    hydrantId: hydrant.id, createdAt: 'now' };
}

describe('deriveSnowmakingRoutingForest', () => {
  it('grows the closest gun trunk and excludes the loop closure', () => {
    const source = node('source', 'intake', 0), near = node('near', 'hydrant', 100);
    const far = node('far', 'hydrant', 200);
    const loop = { ...node('loop', 'junction', 130), elevM: 1100 };
    const pipes = [pipe('trunk-near', source, near), pipe('trunk-far', near, far),
      pipe('loop-start', source, loop), pipe('loop-end', loop, far)];
    const result = deriveSnowmakingRoutingForest({ nodes: [source, near, far, loop], pipes,
      guns: [gun('far-gun', far), gun('near-gun', near)],
      selectedGunIds: ['far-gun', 'near-gun'], selectedIntakeNodeIds: ['source'],
      pumpSettings: {} });
    expect(result.failures).toEqual([]);
    expect(result.trees[0].gunIds).toEqual(['near-gun', 'far-gun']);
    expect(result.trees[0].segmentIds).toEqual([
      'trunk-far:segment:0', 'trunk-near:segment:0']);
  });

  it('chooses one stable equal-length parallel segment', () => {
    const source = node('source', 'intake', 0), hydrant = node('hydrant', 'hydrant', 100);
    const result = deriveSnowmakingRoutingForest({ nodes: [source, hydrant],
      pipes: [pipe('parallel-b', source, hydrant), pipe('parallel-a', source, hydrant)],
      guns: [gun('gun', hydrant)], selectedGunIds: ['gun'],
      selectedIntakeNodeIds: ['source'], pumpSettings: {} });
    expect(result.trees[0].segmentIds).toEqual(['parallel-a:segment:0']);
  });

  it('keeps selected source trees disjoint', () => {
    const west = node('west', 'intake', 0), east = node('east', 'intake', 300);
    const westGun = node('west-gun', 'hydrant', 80), eastGun = node('east-gun', 'hydrant', 220);
    const middle = node('middle', 'junction', 150);
    const result = deriveSnowmakingRoutingForest({ nodes: [west, east, westGun, eastGun, middle],
      pipes: [pipe('west-main', west, westGun), pipe('west-cross', westGun, middle),
        pipe('east-cross', middle, eastGun), pipe('east-main', eastGun, east)],
      guns: [gun('gun-west', westGun), gun('gun-east', eastGun)],
      selectedGunIds: ['gun-east', 'gun-west'], selectedIntakeNodeIds: ['west', 'east'],
      pumpSettings: {} });
    expect(result.trees).toHaveLength(2);
    expect(result.trees.flatMap((tree) => tree.segmentIds).sort()).toEqual([
      'east-main:segment:0', 'west-main:segment:0']);
  });

  it('routes an on pump only from suction to discharge', () => {
    const source = node('source', 'intake', 0), pump = node('pump', 'pump', 50);
    const hydrant = node('hydrant', 'hydrant', 100);
    const forward = deriveSnowmakingRoutingForest({ nodes: [source, pump, hydrant],
      pipes: [pipe('suction', source, pump, [null, 'suction']),
        pipe('discharge', pump, hydrant, ['discharge', null])], guns: [gun('gun', hydrant)],
      selectedGunIds: ['gun'], selectedIntakeNodeIds: ['source'], pumpSettings: { pump: { on: true } } });
    expect(forward.trees[0].segmentIds).toEqual(['discharge:segment:0', 'suction:segment:0']);
    expect(forward.trees[0].pumpNodeIds).toEqual(['pump']);

    const reverse = deriveSnowmakingRoutingForest({ nodes: [source, pump, hydrant],
      pipes: [pipe('suction', source, pump, [null, 'discharge']),
        pipe('discharge', pump, hydrant, ['suction', null])], guns: [gun('gun', hydrant)],
      selectedGunIds: ['gun'], selectedIntakeNodeIds: ['source'], pumpSettings: { pump: { on: true } } });
    expect(reverse.trees).toEqual([]);
    expect(reverse.failures[0].diagnostics[0]).toMatchObject({
      code: 'pump-direction-blocks-route', entityId: 'pump' });
  });
});
