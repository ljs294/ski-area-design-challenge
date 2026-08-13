import type { SavedSnowgun } from '../types/snowmaking';

export interface LassoPoint {
  x: number;
  y: number;
}

export interface SnowmakingLassoRect {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export interface SnowmakingLassoMapState {
  rect: SnowmakingLassoRect;
  gunIds: readonly string[];
  /** The same rectangle expressed in map coordinates for the transient map layer. */
  geoBounds: readonly [number, number, number, number];
}

export interface SnowmakingLassoSelection extends SnowmakingLassoMapState {
  anchor: LassoPoint;
  selectedGunCount: number;
  unselectedGunCount: number;
  add(): void;
  remove(): void;
  cancel(): void;
}

export function normalizeLassoRect(start: LassoPoint, end: LassoPoint): SnowmakingLassoRect {
  return {
    minX: Math.min(start.x, end.x),
    minY: Math.min(start.y, end.y),
    maxX: Math.max(start.x, end.x),
    maxY: Math.max(start.y, end.y),
  };
}

export function pointInLasso(point: LassoPoint, rect: SnowmakingLassoRect): boolean {
  return point.x >= rect.minX && point.x <= rect.maxX &&
    point.y >= rect.minY && point.y <= rect.maxY;
}

export function connectedGunIdsInLasso(
  guns: readonly SavedSnowgun[],
  project: (point: [number, number]) => LassoPoint,
  rect: SnowmakingLassoRect,
): string[] {
  return guns.filter((gun) => gun.hydrantId != null && pointInLasso(project(gun.point), rect))
    .map((gun) => gun.id);
}
