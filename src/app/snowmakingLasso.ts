import type { SavedSnowgun } from '../types/snowmaking';

export interface LassoPoint {
  x: number;
  y: number;
}

export type LassoRing = readonly LassoPoint[];

export interface SnowmakingLassoMapState {
  /** The closed ring in map coordinates. */
  ring: readonly [number, number][];
  gunIds: readonly string[];
}

export interface SnowmakingLassoSelection extends SnowmakingLassoMapState {
  anchor: LassoPoint;
  anchorLngLat: [number, number];
  selectedGunCount: number;
  unselectedGunCount: number;
  add(): void;
  remove(): void;
  cancel(): void;
}

export function distanceSquared(left: LassoPoint, right: LassoPoint): number {
  return (left.x - right.x) ** 2 + (left.y - right.y) ** 2;
}

export function appendLassoSample(
  path: readonly LassoPoint[],
  point: LassoPoint,
  minimumDistance = 3,
): LassoPoint[] {
  const last = path.at(-1);
  return last && distanceSquared(last, point) < minimumDistance ** 2
    ? [...path] : [...path, point];
}

export function simplifyLassoRadial(
  path: readonly LassoPoint[],
  minimumDistance = 3,
): LassoPoint[] {
  if (path.length < 3) return [...path];
  const result = [path[0]];
  for (const point of path.slice(1)) {
    if (distanceSquared(result.at(-1)!, point) >= minimumDistance ** 2) result.push(point);
  }
  const last = path.at(-1)!;
  if (result.at(-1) !== last) result.push(last);
  return result;
}

function perpendicularDistance(point: LassoPoint, start: LassoPoint, end: LassoPoint): number {
  const dx = end.x - start.x, dy = end.y - start.y;
  if (dx === 0 && dy === 0) return Math.sqrt(distanceSquared(point, start));
  const t = Math.max(0, Math.min(1, ((point.x - start.x) * dx + (point.y - start.y) * dy) /
    (dx * dx + dy * dy)));
  return Math.hypot(point.x - (start.x + t * dx), point.y - (start.y + t * dy));
}

export function simplifyLassoRdp(path: readonly LassoPoint[], tolerance = 2): LassoPoint[] {
  if (path.length < 3) return [...path];
  let farthest = 0, distance = 0;
  const start = path[0], end = path.at(-1)!;
  for (let index = 1; index < path.length - 1; index += 1) {
    const candidate = perpendicularDistance(path[index], start, end);
    if (candidate > distance) { distance = candidate; farthest = index; }
  }
  if (distance <= tolerance) return [start, end];
  const left = simplifyLassoRdp(path.slice(0, farthest + 1), tolerance);
  const right = simplifyLassoRdp(path.slice(farthest), tolerance);
  return [...left.slice(0, -1), ...right];
}

export function closeLassoPath(path: readonly LassoPoint[]): LassoPoint[] {
  if (!path.length) return [];
  const simplified = simplifyLassoRdp(simplifyLassoRadial(path));
  const first = simplified[0], last = simplified.at(-1)!;
  return distanceSquared(first, last) === 0 ? simplified : [...simplified, first];
}

function pointOnSegment(point: LassoPoint, start: LassoPoint, end: LassoPoint): boolean {
  const cross = (point.y - start.y) * (end.x - start.x) -
    (point.x - start.x) * (end.y - start.y);
  if (Math.abs(cross) > 1e-7) return false;
  return point.x >= Math.min(start.x, end.x) - 1e-7 &&
    point.x <= Math.max(start.x, end.x) + 1e-7 &&
    point.y >= Math.min(start.y, end.y) - 1e-7 &&
    point.y <= Math.max(start.y, end.y) + 1e-7;
}

export function pointInLasso(point: LassoPoint, ring: readonly LassoPoint[]): boolean {
  if (ring.length < 3) return false;
  let inside = false;
  for (let index = 0, previous = ring.length - 1; index < ring.length; previous = index++) {
    const current = ring[index], prior = ring[previous];
    if (pointOnSegment(point, prior, current)) return true;
    const crosses = (current.y > point.y) !== (prior.y > point.y) &&
      point.x < (prior.x - current.x) * (point.y - current.y) /
        (prior.y - current.y) + current.x;
    if (crosses) inside = !inside;
  }
  return inside;
}

function ringBounds(ring: readonly LassoPoint[]) {
  return { minX: Math.min(...ring.map((point) => point.x)),
    minY: Math.min(...ring.map((point) => point.y)),
    maxX: Math.max(...ring.map((point) => point.x)),
    maxY: Math.max(...ring.map((point) => point.y)) };
}

export function connectedGunIdsInLasso(
  guns: readonly SavedSnowgun[],
  projected: ReadonlyMap<string, LassoPoint>,
  ring: readonly LassoPoint[],
): string[] {
  if (ring.length < 3) return [];
  const bounds = ringBounds(ring);
  return guns.filter((gun) => {
    if (gun.hydrantId == null) return false;
    const point = projected.get(gun.id);
    return !!point && point.x >= bounds.minX && point.x <= bounds.maxX &&
      point.y >= bounds.minY && point.y <= bounds.maxY && pointInLasso(point, ring);
  }).map((gun) => gun.id);
}
