import maplibregl from 'maplibre-gl';
import type { SavedSnowmakingNode, SavedSnowmakingPipe } from '../types/snowmaking';
import type { SnowmakingSnapIntent } from './snowmakingNetworkControllerModel';

const SNAP_TOLERANCE_PX = 16;

function projectedDistance(a: maplibregl.Point, b: maplibregl.Point): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

export function pipeSnapAt(map: maplibregl.Map, point: [number, number],
  pipes: readonly SavedSnowmakingPipe[]): { pipeId: string; point: [number, number] } | null {
  const cursor = map.project(point);
  let best: { pipeId: string; point: [number, number]; distance: number } | null = null;
  for (const pipe of pipes) for (let index = 0; index < pipe.vertices.length - 1; index += 1) {
    const aLngLat = pipe.vertices[index].point, bLngLat = pipe.vertices[index + 1].point;
    const a = map.project(aLngLat), b = map.project(bLngLat);
    const dx = b.x - a.x, dy = b.y - a.y, lengthSquared = dx * dx + dy * dy;
    const u = lengthSquared > 0 ? Math.max(0, Math.min(1,
      ((cursor.x - a.x) * dx + (cursor.y - a.y) * dy) / lengthSquared)) : 0;
    const projected = new maplibregl.Point(a.x + dx * u, a.y + dy * u);
    const distance = projectedDistance(cursor, projected);
    if (distance <= SNAP_TOLERANCE_PX && (!best || distance < best.distance)) best = {
      pipeId: pipe.id, point: [aLngLat[0] + (bLngLat[0] - aLngLat[0]) * u,
        aLngLat[1] + (bLngLat[1] - aLngLat[1]) * u], distance,
    };
  }
  return best ? { pipeId: best.pipeId, point: best.point } : null;
}

export function snowmakingSnapAt(map: maplibregl.Map, point: [number, number],
  nodes: readonly SavedSnowmakingNode[],
  pipes: readonly SavedSnowmakingPipe[]): SnowmakingSnapIntent | null {
  const cursor = map.project(point);
  let nodeBest: { node: SavedSnowmakingNode; distance: number } | null = null;
  for (const node of nodes) {
    const distance = projectedDistance(cursor, map.project(node.point));
    if (distance <= SNAP_TOLERANCE_PX && (!nodeBest || distance < nodeBest.distance)) {
      nodeBest = { node, distance };
    }
  }
  if (nodeBest) return { kind: 'node', nodeId: nodeBest.node.id, point: nodeBest.node.point };
  const pipe = pipeSnapAt(map, point, pipes);
  return pipe ? { kind: 'pipe', ...pipe } : null;
}
