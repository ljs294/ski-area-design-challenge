import { describe, expect, it } from 'vitest';
import maplibregl from 'maplibre-gl';
import type { SavedSnowmakingNode, SavedSnowmakingPipe } from '../types/snowmaking';
import { pipeSnapAt, snowmakingSnapAt } from './snowmakingNetworkSnap';

const project = ([x, y]: [number, number]) => new maplibregl.Point(x * 100, y * 100);
const map = { project } as unknown as maplibregl.Map;
const pipe: SavedSnowmakingPipe = { id: 'pipe', name: 'Pipe', diameterIn: 8,
  vertices: [{ point: [0, 0], elevM: 100, nodeId: null },
    { point: [1, 0], elevM: 100, nodeId: null }], lengthM: 1, verticalM: 0, createdAt: 'now' };
const node: SavedSnowmakingNode = { id: 'node', name: 'Hydrant 1', kind: 'hydrant',
  labelNumber: 1, point: [0.6, 0.05], elevM: 100, createdAt: 'now' };

describe('snowmaking map snapping', () => {
  it('prefers a node within tolerance over a closer pipe', () => {
    expect(snowmakingSnapAt(map, [0.5, 0.05], [node], [pipe])).toMatchObject({
      kind: 'node', nodeId: 'node',
    });
  });

  it('projects directly onto a pipe when selecting a hydrant run', () => {
    expect(pipeSnapAt(map, [0.5, 0.05], [pipe])).toEqual({ pipeId: 'pipe', point: [0.5, 0] });
  });
});
