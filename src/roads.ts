import type { Polygon } from './coverEdit';
import { haversineMeters, METERS_PER_DEGREE_LAT } from './geo';
import type { RoadType, SavedRoad } from './types';

export const TWO_LANE_ROAD_WIDTH_M = 7;
export const ROAD_CLEAR_BUFFER_M = 3;
export const TWO_LANE_CLEAR_HALF_WIDTH_M = TWO_LANE_ROAD_WIDTH_M / 2 + ROAD_CLEAR_BUFFER_M;

export const ROAD_TYPE_LABELS: Record<RoadType, string> = {
  'two-lane': 'Two-lane road — 7 m',
};

function isLngLat(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 &&
    typeof value[0] === 'number' && Number.isFinite(value[0]) &&
    typeof value[1] === 'number' && Number.isFinite(value[1]);
}

export function roadLengthM(points: [number, number][]): number {
  let length = 0;
  for (let i = 1; i < points.length; i++) length += haversineMeters(points[i - 1], points[i]);
  return length;
}

/** Hydration shield for legacy/untrusted saves. Cached width and length are
 * normalized from the supported road type and stored centerline. */
export function sanitizeRoads(raw: unknown[]): SavedRoad[] {
  const roads: SavedRoad[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const value = item as Record<string, unknown>;
    if (typeof value.id !== 'string' || typeof value.name !== 'string') continue;
    if (value.roadType !== 'two-lane' || !Array.isArray(value.points)) continue;
    const points: [number, number][] = [];
    for (const point of value.points) {
      if (!isLngLat(point)) { points.length = 0; break; }
      const previous = points.at(-1);
      if (!previous || haversineMeters(previous, point) >= 0.05) points.push(point);
    }
    if (points.length < 2) continue;
    const rawEarthwork = typeof value.earthwork === 'object' && value.earthwork !== null
      ? value.earthwork as Record<string, unknown> : null;
    const earthwork = rawEarthwork &&
      typeof rawEarthwork.cutM3 === 'number' && Number.isFinite(rawEarthwork.cutM3) && rawEarthwork.cutM3 >= 0 &&
      typeof rawEarthwork.fillM3 === 'number' && Number.isFinite(rawEarthwork.fillM3) && rawEarthwork.fillM3 >= 0
      ? {
          cutM3: rawEarthwork.cutM3,
          fillM3: rawEarthwork.fillM3,
          balanceM3: typeof rawEarthwork.balanceM3 === 'number' && Number.isFinite(rawEarthwork.balanceM3)
            ? rawEarthwork.balanceM3 : rawEarthwork.cutM3 - rawEarthwork.fillM3,
        }
      : undefined;
    roads.push({
      id: value.id,
      name: value.name,
      roadType: 'two-lane',
      widthM: TWO_LANE_ROAD_WIDTH_M,
      points,
      lengthM: roadLengthM(points),
      terrainGraded: value.terrainGraded === true,
      earthwork,
      createdAt: typeof value.createdAt === 'string' ? value.createdAt : new Date().toISOString(),
    });
  }
  return roads;
}

export function nextRoadName(existing: SavedRoad[]): string {
  const names = new Set(existing.map((road) => road.name));
  for (let number = 1; ; number++) {
    const name = `Road ${number}`;
    if (!names.has(name)) return name;
  }
}

/** One rounded capsule per centerline segment. Their overlap is the exact road
 * clearing union for grid stamping and avoids expensive polygon boolean work at
 * bends and self-crossings. */
export function roadClearingPolygons(points: [number, number][],
  halfWidthM = TWO_LANE_CLEAR_HALF_WIDTH_M): Polygon[] {
  const polygons: Polygon[] = [];
  const capSteps = 12;
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1];
    const b = points[i];
    const metersLng = Math.max(1, METERS_PER_DEGREE_LAT * Math.cos(a[1] * Math.PI / 180));
    const bx = (b[0] - a[0]) * metersLng;
    const by = (b[1] - a[1]) * METERS_PER_DEGREE_LAT;
    const length = Math.hypot(bx, by);
    if (length < 0.05) continue;
    const direction = Math.atan2(by, bx);
    const ringM: [number, number][] = [];
    // Start cap: right edge to left edge through the backward hemisphere.
    for (let step = 0; step <= capSteps; step++) {
      const angle = direction - Math.PI / 2 - step * Math.PI / capSteps;
      ringM.push([Math.cos(angle) * halfWidthM, Math.sin(angle) * halfWidthM]);
    }
    // End cap: left edge to right edge through the forward hemisphere.
    for (let step = 0; step <= capSteps; step++) {
      const angle = direction + Math.PI / 2 - step * Math.PI / capSteps;
      ringM.push([bx + Math.cos(angle) * halfWidthM, by + Math.sin(angle) * halfWidthM]);
    }
    const ring: [number, number][] = ringM.map(([x, y]) =>
      [a[0] + x / metersLng, a[1] + y / METERS_PER_DEGREE_LAT]);
    ring.push(ring[0]);
    polygons.push([ring]);
  }
  return polygons;
}
