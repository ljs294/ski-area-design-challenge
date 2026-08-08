import type { SavedDam, SavedPond } from '../types';
import type { SavedSnowmakingNode, SnowmakingLakeSource } from '../types/snowmaking';
import type { XY } from '../network';

export function ringAreaM2(points: XY[]): number {
  let sum = 0;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    sum += points[j].x * points[i].y - points[i].x * points[j].y;
  }
  return Math.abs(sum) / 2;
}

export function ringPathD(points: XY[]): string {
  if (points.length < 2) return '';
  return 'M' + points.map((point) => `${point.x.toFixed(1)},${point.y.toFixed(1)}`)
    .join('L') + 'Z';
}

/** Resolve the display name and storage capacity behind a persisted intake. */
export function snowmakingSourceInfo(
  node: SavedSnowmakingNode,
  dams: SavedDam[],
  ponds: SavedPond[],
  lakes: SnowmakingLakeSource[] = [],
): { name: string; capacityM3: number } | null {
  const source = node.source;
  if (!source) return null;
  if (source.kind === 'dam') {
    const dam = dams.find((item) => item.id === source.damId);
    return dam ? { name: dam.name, capacityM3: dam.capacityM3 }
      : { name: 'Unknown', capacityM3: 0 };
  }
  if (source.kind === 'lake') {
    const lake = lakes.find((item) => item.id === source.lakeId);
    return lake ? { name: lake.name, capacityM3: lake.capacityM3 ?? 0 }
      : { name: 'Unknown', capacityM3: 0 };
  }
  const pond = ponds.find((item) => item.id === source.pondId);
  return pond ? { name: pond.name, capacityM3: pond.capacityM3 }
    : { name: 'Unknown', capacityM3: 0 };
}
