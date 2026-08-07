import { ringCenter } from './snowmakingNodes';
import type { TerrainRecord } from './types/terrain';

type Point = [number, number];

function samePoint(a: Point, b: Point): boolean {
  return a[0] === b[0] && a[1] === b[1];
}

/**
 * Cosmetic Chaikin corner cutting for pond shorelines. The persisted/design
 * polygon remains untouched so smoothing cannot change capacity calculations.
 */
export function smoothPondRing(ring: Point[], iterations = 2): Point[] {
  if (ring.length < 4) return ring.map((point) => [...point]);
  let points = (samePoint(ring[0], ring[ring.length - 1]) ? ring.slice(0, -1) : ring)
    .map((point) => [...point] as Point);
  if (points.length < 3) return ring.map((point) => [...point]);
  for (let pass = 0; pass < iterations; pass++) {
    const next: Point[] = [];
    for (let i = 0; i < points.length; i++) {
      const a = points[i], b = points[(i + 1) % points.length];
      next.push([a[0] * 0.75 + b[0] * 0.25, a[1] * 0.75 + b[1] * 0.25]);
      next.push([a[0] * 0.25 + b[0] * 0.75, a[1] * 0.25 + b[1] * 0.75]);
    }
    points = next;
  }
  return [...points, points[0]];
}

export function smoothPondRings(rings: Point[][]): Point[][] {
  return rings.map((ring) => smoothPondRing(ring));
}

function validElevation(value: number): boolean {
  return Number.isFinite(value) && value > -1000 && value < 10000;
}

function sampleTerrain(record: TerrainRecord, point: Point): number | null {
  const bounds = record.bounds, n = record.sampleGridSize;
  if (!bounds || n < 2 || record.sampleHeights.length !== n * n) return null;
  const x = (point[0] - bounds.west) / (bounds.east - bounds.west) * (n - 1);
  const y = (bounds.north - point[1]) / (bounds.north - bounds.south) * (n - 1);
  if (x < 0 || y < 0 || x > n - 1 || y > n - 1) return null;
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const h = record.sampleHeights;
  const values = [h[y0 * n + x0], h[y0 * n + x1], h[y1 * n + x0], h[y1 * n + x1]];
  if (!values.every(validElevation)) return null;
  return (values[0] * (1 - tx) + values[1] * tx) * (1 - ty) +
    (values[2] * (1 - tx) + values[3] * tx) * ty;
}

/**
 * MapLibre places a fill extrusion relative to the terrain at the feature's
 * representative point. Using the outer-ring vertex mean closely matches that
 * point and gives the zero-thickness extrusion an absolute, level water top.
 */
export function pondRelativeRenderHeightM(ring: Point[], waterElevationM: number,
  fallbackDepthM: number, record?: TerrainRecord | null): number {
  if (record && ring.length) {
    const center = ringCenter(ring);
    const groundM = sampleTerrain(record, center);
    if (groundM != null) return Math.max(0.01, waterElevationM - groundM);
  }
  return Math.max(0.01, fallbackDepthM);
}
