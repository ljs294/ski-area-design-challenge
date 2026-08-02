import { METERS_PER_DEGREE_LAT, unitToLngLat } from './geo';
import { lakeSurfaceAreaM2, pointInRing } from './lakeAnalysis';
import type { SavedPond, TerrainRecord } from './types';

export const MIN_STANDALONE_POND_AREA_M2 = 100;

export interface StandalonePondAnalysis {
  boundary: [number, number][];
  topElevationM: number;
  areaM2: number;
  averageDepthM: number;
  maxDepthM: number;
  capacityM3: number;
}

export type StandalonePondOutcome = { ok: true; result: StandalonePondAnalysis } |
  { ok: false; error: string };

function validElevation(value: number): boolean {
  return Number.isFinite(value) && value > -1000 && value < 10000;
}

function validPoint(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 &&
    value.every((coordinate) => typeof coordinate === 'number' && Number.isFinite(coordinate));
}

function closedBoundary(points: [number, number][]): [number, number][] {
  if (!points.length) return [];
  const first = points[0], last = points[points.length - 1];
  return first[0] === last[0] && first[1] === last[1] ? [...points] : [...points, first];
}

function orientation(a: [number, number], b: [number, number], c: [number, number]): number {
  return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
}

function segmentsCross(a: [number, number], b: [number, number], c: [number, number], d: [number, number]): boolean {
  const abC = orientation(a, b, c), abD = orientation(a, b, d);
  const cdA = orientation(c, d, a), cdB = orientation(c, d, b);
  return abC * abD < 0 && cdA * cdB < 0;
}

export function pondBoundaryIsSimple(points: [number, number][]): boolean {
  const ring = closedBoundary(points);
  const edgeCount = ring.length - 1;
  if (edgeCount < 3) return false;
  for (let i = 0; i < edgeCount; i++) for (let j = i + 1; j < edgeCount; j++) {
    if (j === i + 1 || (i === 0 && j === edgeCount - 1)) continue;
    if (segmentsCross(ring[i], ring[i + 1], ring[j], ring[j + 1])) return false;
  }
  return true;
}

function geometryAreaM2(boundary: [number, number][]): number {
  return lakeSurfaceAreaM2({ id: 'draft-pond', rings: [boundary] });
}

function sampleTerrain(record: TerrainRecord, point: [number, number]): number | null {
  const bounds = record.bounds, n = record.sampleGridSize;
  if (!bounds || n < 2) return null;
  const x = (point[0] - bounds.west) / (bounds.east - bounds.west) * (n - 1);
  const y = (bounds.north - point[1]) / (bounds.north - bounds.south) * (n - 1);
  if (x < 0 || y < 0 || x > n - 1 || y > n - 1) return null;
  const x0 = Math.floor(x), y0 = Math.floor(y), x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const values = [record.sampleHeights[y0 * n + x0], record.sampleHeights[y0 * n + x1],
    record.sampleHeights[y1 * n + x0], record.sampleHeights[y1 * n + x1]];
  if (!values.every(validElevation)) return null;
  return (values[0] * (1 - tx) + values[1] * tx) * (1 - ty) +
    (values[2] * (1 - tx) + values[3] * tx) * ty;
}

/** Suggest a useful first value while leaving the final full-pool elevation user-controlled. */
export function suggestedPondTopElevationM(record: TerrainRecord, points: [number, number][]): number | null {
  const bounds = record.bounds, n = record.sampleGridSize;
  if (!bounds || n < 2 || record.sampleHeights.length !== n * n) return null;
  const ring = closedBoundary(points);
  if (ring.length < 4) return null;
  const lngs = ring.map((point) => point[0]), lats = ring.map((point) => point[1]);
  const x0 = Math.max(0, Math.floor((Math.min(...lngs) - bounds.west) / (bounds.east - bounds.west) * (n - 1)));
  const x1 = Math.min(n - 1, Math.ceil((Math.max(...lngs) - bounds.west) / (bounds.east - bounds.west) * (n - 1)));
  const y0 = Math.max(0, Math.floor((bounds.north - Math.max(...lats)) / (bounds.north - bounds.south) * (n - 1)));
  const y1 = Math.min(n - 1, Math.ceil((bounds.north - Math.min(...lats)) / (bounds.north - bounds.south) * (n - 1)));
  let highest = -Infinity;
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
    const point = unitToLngLat(x / (n - 1), y / (n - 1), bounds);
    if (!pointInRing(point, ring)) continue;
    const elevation = record.sampleHeights[y * n + x];
    if (validElevation(elevation)) highest = Math.max(highest, elevation);
  }
  if (!Number.isFinite(highest)) {
    const center: [number, number] = [points.reduce((sum, point) => sum + point[0], 0) / points.length,
      points.reduce((sum, point) => sum + point[1], 0) / points.length];
    for (const point of [...points, center]) {
      const elevation = sampleTerrain(record, point);
      if (elevation != null) highest = Math.max(highest, elevation);
    }
  }
  return Number.isFinite(highest) ? Math.ceil(highest * 2) / 2 + 1 : null;
}

export function analyzeStandalonePond(record: TerrainRecord, points: [number, number][],
  topElevationM: number): StandalonePondOutcome {
  const bounds = record.bounds, n = record.sampleGridSize;
  if (!bounds || n < 2 || record.sampleHeights.length !== n * n)
    return { ok: false, error: 'Terrain coverage is unavailable for this pond.' };
  if (!validElevation(topElevationM)) return { ok: false, error: 'Enter a valid top-of-pond elevation.' };
  if (!pondBoundaryIsSimple(points))
    return { ok: false, error: 'Draw a boundary with at least three points that does not cross itself.' };
  const boundary = closedBoundary(points);
  if (boundary.some(([lng, lat]) => lng < bounds.west || lng > bounds.east || lat < bounds.south || lat > bounds.north))
    return { ok: false, error: 'Keep the entire pond boundary inside the available terrain.' };
  const areaM2 = geometryAreaM2(boundary);
  if (areaM2 < MIN_STANDALONE_POND_AREA_M2)
    return { ok: false, error: 'This pond is too small. Draw a boundary of at least 100 m².' };

  const centerLat = (bounds.north + bounds.south) / 2;
  const gridWidthM = (bounds.east - bounds.west) * METERS_PER_DEGREE_LAT * Math.cos(centerLat * Math.PI / 180);
  const gridHeightM = (bounds.north - bounds.south) * METERS_PER_DEGREE_LAT;
  const cellAreaM2 = gridWidthM * gridHeightM / ((n - 1) * (n - 1));
  const lngs = boundary.map((point) => point[0]), lats = boundary.map((point) => point[1]);
  const x0 = Math.max(0, Math.floor((Math.min(...lngs) - bounds.west) / (bounds.east - bounds.west) * (n - 1)));
  const x1 = Math.min(n - 2, Math.ceil((Math.max(...lngs) - bounds.west) / (bounds.east - bounds.west) * (n - 1)));
  const y0 = Math.max(0, Math.floor((bounds.north - Math.max(...lats)) / (bounds.north - bounds.south) * (n - 1)));
  const y1 = Math.min(n - 2, Math.ceil((bounds.north - Math.min(...lats)) / (bounds.north - bounds.south) * (n - 1)));
  let validCells = 0, coveredCells = 0, depthSumM = 0, maxDepthM = 0;
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
    const center = unitToLngLat((x + 0.5) / (n - 1), (y + 0.5) / (n - 1), bounds);
    if (!pointInRing(center, boundary)) continue;
    coveredCells++;
    const corners = [record.sampleHeights[y * n + x], record.sampleHeights[y * n + x + 1],
      record.sampleHeights[(y + 1) * n + x], record.sampleHeights[(y + 1) * n + x + 1]];
    if (!corners.every(validElevation)) continue;
    validCells++;
    const groundM = corners.reduce((sum, value) => sum + value, 0) / 4;
    const depthM = Math.max(0, topElevationM - groundM);
    depthSumM += depthM;
    maxDepthM = Math.max(maxDepthM, depthM);
  }
  if (coveredCells > 0 && validCells / coveredCells < 0.8)
    return { ok: false, error: 'The pond boundary does not have enough valid terrain coverage.' };
  if (coveredCells === 0) {
    const openBoundary = boundary.slice(0, -1);
    const center: [number, number] = [openBoundary.reduce((sum, point) => sum + point[0], 0) / openBoundary.length,
      openBoundary.reduce((sum, point) => sum + point[1], 0) / openBoundary.length];
    const groundM = sampleTerrain(record, center);
    if (groundM == null) return { ok: false, error: 'The pond boundary does not have enough valid terrain coverage.' };
    const depthM = Math.max(0, topElevationM - groundM);
    if (depthM < 0.1) return { ok: false, error: 'Raise the top elevation above the ground inside the pond.' };
    return { ok: true, result: { boundary, topElevationM, areaM2,
      averageDepthM: depthM, maxDepthM: depthM, capacityM3: areaM2 * depthM } };
  }
  // Scale the grid integration to the geodesic boundary area so coarse grids
  // do not make saved area and volume disagree at small pond sizes.
  const sampledAreaM2 = validCells * cellAreaM2;
  const capacityM3 = sampledAreaM2 > 0 ? depthSumM * cellAreaM2 * areaM2 / sampledAreaM2 : 0;
  if (capacityM3 <= 1 || maxDepthM < 0.1)
    return { ok: false, error: 'Raise the top elevation above the ground inside the pond.' };
  return { ok: true, result: { boundary, topElevationM, areaM2,
    averageDepthM: capacityM3 / areaM2, maxDepthM, capacityM3 } };
}

export function sanitizePonds(raw: unknown[]): SavedPond[] {
  const ponds: SavedPond[] = [];
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue;
    const value = item as Record<string, unknown>;
    if (typeof value.id !== 'string' || typeof value.name !== 'string' ||
      !Array.isArray(value.boundary) || !value.boundary.every(validPoint) ||
      !pondBoundaryIsSimple(value.boundary as [number, number][])) continue;
    if (typeof value.topElevationM !== 'number' || !validElevation(value.topElevationM)) continue;
    const numeric = ['topElevationM', 'areaM2', 'averageDepthM', 'maxDepthM', 'capacityM3'];
    if (numeric.some((key) => typeof value[key] !== 'number' || !Number.isFinite(value[key]) ||
      (key !== 'topElevationM' && (value[key] as number) <= 0))) continue;
    ponds.push({ id: value.id, name: value.name,
      boundary: closedBoundary(value.boundary as [number, number][]),
      topElevationM: value.topElevationM as number, areaM2: value.areaM2 as number,
      averageDepthM: value.averageDepthM as number, maxDepthM: value.maxDepthM as number,
      capacityM3: value.capacityM3 as number,
      createdAt: typeof value.createdAt === 'string' ? value.createdAt : new Date().toISOString() });
  }
  return ponds;
}

export function nextPondName(existing: SavedPond[]): string {
  const names = new Set(existing.map((pond) => pond.name));
  for (let i = 1; ; i++) if (!names.has(`Pond ${i}`)) return `Pond ${i}`;
}
