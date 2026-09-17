import type { Ray } from 'three';
import type { TerrainRecord } from '../../src/types/terrain';
import { sampleTerrainElevation, type LocalTerrainFrame } from './terrainSurface';

export interface TerrainHit { point: [number, number, number]; lngLat: [number, number]; distance: number }

/** Analytical picking stays independent of display mesh density. */
export function intersectTerrainRay(ray: Ray, record: TerrainRecord,
  frame: LocalTerrainFrame): TerrainHit | null {
  const minX = -frame.widthM / 2, maxX = frame.widthM / 2;
  const minZ = -frame.depthM / 2, maxZ = frame.depthM / 2;
  const horizontalSpeed = Math.hypot(ray.direction.x, ray.direction.z);
  if (horizontalSpeed < 1e-9) {
    if (ray.origin.x < minX || ray.origin.x > maxX || ray.origin.z < minZ || ray.origin.z > maxZ
      || Math.abs(ray.direction.y) < 1e-9) return null;
    const lngLat = frame.toLngLat(ray.origin.x, ray.origin.z);
    const elevation = sampleTerrainElevation(record, ...lngLat);
    if (elevation == null) return null;
    const y = elevation - frame.centerElevationM;
    const distance = (y - ray.origin.y) / ray.direction.y;
    return distance >= 0 ? { point: [ray.origin.x, y, ray.origin.z], lngLat, distance } : null;
  }
  let enter = 0, exit = Number.POSITIVE_INFINITY;
  for (const [origin, direction, low, high] of [
    [ray.origin.x, ray.direction.x, minX, maxX],
    [ray.origin.z, ray.direction.z, minZ, maxZ],
  ] as const) {
    if (Math.abs(direction) < 1e-9) { if (origin < low || origin > high) return null; continue; }
    const a = (low - origin) / direction, b = (high - origin) / direction;
    enter = Math.max(enter, Math.min(a, b)); exit = Math.min(exit, Math.max(a, b));
  }
  if (exit < enter || exit < 0) return null;
  const sourceCell = Math.min(frame.widthM, frame.depthM) / Math.max(2, record.sampleGridSize - 1);
  const dt = Math.max(0.25, sourceCell / horizontalSpeed);
  const residual = (distance: number): number | null => {
    const x = ray.origin.x + ray.direction.x * distance;
    const z = ray.origin.z + ray.direction.z * distance;
    const lngLat = frame.toLngLat(x, z);
    const elevation = sampleTerrainElevation(record, ...lngLat);
    return elevation == null ? null
      : ray.origin.y + ray.direction.y * distance - (elevation - frame.centerElevationM);
  };
  let priorT = Math.max(0, enter), prior = residual(priorT);
  for (let t = Math.min(exit, priorT + dt); t <= exit + 1e-6; t = Math.min(exit, t + dt)) {
    const value = residual(t);
    if (prior != null && value != null && ((prior >= 0 && value <= 0) || (prior <= 0 && value >= 0))) {
      let low = priorT, high = t;
      for (let iteration = 0; iteration < 24; iteration++) {
        const mid = (low + high) / 2, middle = residual(mid);
        if (middle == null || Math.abs(middle) < 0.005) { low = high = mid; break; }
        if ((prior >= 0) === (middle >= 0)) low = mid; else high = mid;
      }
      const distance = (low + high) / 2;
      const x = ray.origin.x + ray.direction.x * distance, z = ray.origin.z + ray.direction.z * distance;
      const lngLat = frame.toLngLat(x, z), elevation = sampleTerrainElevation(record, ...lngLat);
      return elevation == null ? null
        : { point: [x, elevation - frame.centerElevationM, z], lngLat, distance };
    }
    if (t >= exit) break;
    priorT = t; prior = value;
  }
  return null;
}
