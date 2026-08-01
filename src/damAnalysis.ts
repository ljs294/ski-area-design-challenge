import { maskToPolygons } from './coverPolygons';
import { haversineMeters, lngLatToUnit, METERS_PER_DEGREE_LAT, unitToLngLat } from './geo';
import type { SavedDam, TerrainRecord, WaterLineFeature } from './types';

export const DEFAULT_STREAM_WIDTH_M = 3;
export const DEFAULT_RIVER_WIDTH_M = 15;
export const DAM_SNAP_RADIUS_M = 75;
export const MIN_DAM_LENGTH_M = 5;
export const MIN_POND_AREA_M2 = 100;

function validElevation(value: number): boolean {
  return Number.isFinite(value) && value > -1000 && value < 10000;
}

export interface DamStreamCrossing {
  stream: WaterLineFeature;
  point: [number, number];
  segmentIndex: number;
  segmentT: number;
}

export interface DamAnalysisResult {
  pondRings: [number, number][][];
  areaM2: number;
  averageDepthM: number;
  capacityM3: number;
  averageDamHeightM: number;
  maxDamHeightM: number;
  crossing: DamStreamCrossing;
  sourceWidthM: number;
  inflowM3s: number;
}

export type DamAnalysisOutcome = { ok: true; result: DamAnalysisResult } |
  { ok: false; error: string };

export function effectiveWaterwayWidthM(stream: WaterLineFeature): number {
  return typeof stream.widthM === 'number' && Number.isFinite(stream.widthM) && stream.widthM > 0
    ? stream.widthM
    : stream.waterClass === 'stream' ? DEFAULT_STREAM_WIDTH_M : DEFAULT_RIVER_WIDTH_M;
}

/** Fixed gameplay supply used by snowmaking; deliberately not a hydraulic model. */
export function gameplayStreamFlowM3s(widthM: number): number {
  if (widthM <= 1) return 0.03;
  if (widthM <= 2) return 0.10;
  if (widthM <= 4) return 0.30;
  if (widthM <= 8) return 1;
  if (widthM <= 15) return 3;
  if (widthM <= 30) return 10;
  if (widthM <= 60) return 30;
  return 75;
}

export function damFillTimeSeconds(capacityM3: number, inflowM3s: number): number {
  return capacityM3 > 0 && inflowM3s > 0 ? capacityM3 / inflowM3s : Infinity;
}

function sample(record: TerrainRecord, point: [number, number]): number | null {
  const bounds = record.bounds;
  const n = record.sampleGridSize;
  if (!bounds || n < 2 || record.sampleHeights.length !== n * n) return null;
  const [u, v] = lngLatToUnit(point[0], point[1], bounds);
  if (u < 0 || u > 1 || v < 0 || v > 1) return null;
  const x = u * (n - 1), y = v * (n - 1);
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const h = record.sampleHeights;
  const a = h[y0 * n + x0], b = h[y0 * n + x1];
  const c = h[y1 * n + x0], d = h[y1 * n + x1];
  if (![a, b, c, d].every(validElevation)) return null;
  return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}

function segmentIntersection(a: [number, number], b: [number, number], c: [number, number],
  d: [number, number]): { point: [number, number]; t: number; u: number } | null {
  const rx = b[0] - a[0], ry = b[1] - a[1];
  const sx = d[0] - c[0], sy = d[1] - c[1];
  const cross = rx * sy - ry * sx;
  if (Math.abs(cross) < 1e-14) return null;
  const qx = c[0] - a[0], qy = c[1] - a[1];
  const t = (qx * sy - qy * sx) / cross;
  const u = (qx * ry - qy * rx) / cross;
  if (t < 0 || t > 1 || u < 0 || u > 1) return null;
  return { point: [a[0] + t * rx, a[1] + t * ry], t, u };
}

export function damStreamCrossings(points: [[number, number], [number, number]],
  streams: WaterLineFeature[]): DamStreamCrossing[] {
  const crossings: DamStreamCrossing[] = [];
  for (const stream of streams) {
    for (let i = 1; i < stream.points.length; i++) {
      const hit = segmentIntersection(points[0], points[1], stream.points[i - 1], stream.points[i]);
      if (hit && !crossings.some((crossing) => crossing.stream.id === stream.id &&
        haversineMeters(crossing.point, hit.point) < 0.1)) crossings.push({
          stream, point: hit.point, segmentIndex: i - 1, segmentT: hit.u });
    }
  }
  return crossings;
}

/** Nearest exact crossing of the clicked crest elevation on local DEM grid edges. */
export function snapDamEndpoint(record: TerrainRecord, first: [number, number], cursor: [number, number],
  radiusM = DAM_SNAP_RADIUS_M): [number, number] | null {
  const bounds = record.bounds;
  const n = record.sampleGridSize;
  const target = sample(record, first);
  if (!bounds || target == null || n < 2) return null;
  const [cu, cv] = lngLatToUnit(cursor[0], cursor[1], bounds);
  const metersLng = METERS_PER_DEGREE_LAT * Math.cos(cursor[1] * Math.PI / 180);
  const dxM = (bounds.east - bounds.west) * metersLng / (n - 1);
  const dyM = (bounds.north - bounds.south) * METERS_PER_DEGREE_LAT / (n - 1);
  const cx = cu * (n - 1), cy = cv * (n - 1);
  const rx = Math.ceil(radiusM / Math.max(0.1, dxM));
  const ry = Math.ceil(radiusM / Math.max(0.1, dyM));
  const x0 = Math.max(0, Math.floor(cx - rx)), x1 = Math.min(n - 1, Math.ceil(cx + rx));
  const y0 = Math.max(0, Math.floor(cy - ry)), y1 = Math.min(n - 1, Math.ceil(cy + ry));
  let best: [number, number] | null = null, bestDistance = radiusM;
  const heights = record.sampleHeights;
  const consider = (ax: number, ay: number, ah: number, bx: number, by: number, bh: number) => {
    if (!validElevation(ah) || !validElevation(bh) || (ah < target) === (bh < target) || ah === bh) return;
    const t = (target - ah) / (bh - ah);
    const candidate = unitToLngLat((ax + (bx - ax) * t) / (n - 1),
      (ay + (by - ay) * t) / (n - 1), bounds);
    const distance = haversineMeters(candidate, cursor);
    if (distance <= bestDistance && haversineMeters(first, candidate) >= MIN_DAM_LENGTH_M) {
      best = candidate; bestDistance = distance;
    }
  };
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
    if (x < x1) consider(x, y, heights[y * n + x], x + 1, y, heights[y * n + x + 1]);
    if (y < y1) consider(x, y, heights[y * n + x], x, y + 1, heights[(y + 1) * n + x]);
  }
  return best;
}

function gridPoint(record: TerrainRecord, point: [number, number]): [number, number] {
  const [u, v] = lngLatToUnit(point[0], point[1], record.bounds!);
  return [u * (record.sampleGridSize - 1), v * (record.sampleGridSize - 1)];
}

function markBarrier(mask: Uint8Array, n: number, a: [number, number], b: [number, number]): void {
  const steps = Math.max(1, Math.ceil(Math.hypot(b[0] - a[0], b[1] - a[1]) * 2));
  for (let i = 0; i <= steps; i++) {
    const t = i / steps, x = Math.round(a[0] + (b[0] - a[0]) * t), y = Math.round(a[1] + (b[1] - a[1]) * t);
    for (let oy = -1; oy <= 1; oy++) for (let ox = -1; ox <= 1; ox++) {
      const px = x + ox, py = y + oy;
      if (px >= 0 && px < n && py >= 0 && py < n) mask[py * n + px] = 1;
    }
  }
}

function seedFor(record: TerrainRecord, crossing: DamStreamCrossing, crest: number): [number, number] | null {
  const a = crossing.stream.points[crossing.segmentIndex];
  const b = crossing.stream.points[crossing.segmentIndex + 1];
  const before: [number, number] = [a[0] + (b[0] - a[0]) * Math.max(0, crossing.segmentT - 0.12),
    a[1] + (b[1] - a[1]) * Math.max(0, crossing.segmentT - 0.12)];
  const after: [number, number] = [a[0] + (b[0] - a[0]) * Math.min(1, crossing.segmentT + 0.12),
    a[1] + (b[1] - a[1]) * Math.min(1, crossing.segmentT + 0.12)];
  const beforeH = sample(record, before), afterH = sample(record, after);
  // OSM waterways are digitized downstream, so `before` is the deterministic fallback.
  const useAfter = beforeH != null && afterH != null && afterH > beforeH + 0.25;
  const [crossX, crossY] = gridPoint(record, crossing.point);
  const target = gridPoint(record, useAfter ? b : a);
  const vectorX = target[0] - crossX, vectorY = target[1] - crossY;
  const vectorLength = Math.max(0.001, Math.hypot(vectorX, vectorY));
  // Start several cells upstream so the three-cell dam barrier cannot swallow
  // the seed when an OSM waterway is split into very short segments.
  const gx = crossX + vectorX / vectorLength * 5;
  const gy = crossY + vectorY / vectorLength * 5;
  const n = record.sampleGridSize;
  const ix = Math.round(gx), iy = Math.round(gy);
  for (let radius = 0; radius <= 8; radius++) for (let oy = -radius; oy <= radius; oy++) {
    for (let ox = -radius; ox <= radius; ox++) {
      const x = ix + ox, y = iy + oy;
      if (x >= 0 && x < n && y >= 0 && y < n && validElevation(record.sampleHeights[y * n + x]) &&
        record.sampleHeights[y * n + x] <= crest + 0.25)
        return [x, y];
    }
  }
  return null;
}

export function analyzeDam(record: TerrainRecord, points: [[number, number], [number, number]],
  crestElevationM: number, streams: WaterLineFeature[]): DamAnalysisOutcome {
  const bounds = record.bounds, n = record.sampleGridSize;
  if (!bounds || n < 2 || record.sampleHeights.length !== n * n)
    return { ok: false, error: 'Terrain coverage is unavailable for this dam.' };
  const crossings = damStreamCrossings(points, streams);
  if (crossings.length !== 1) return { ok: false, error: crossings.length === 0
    ? 'The dam must cross one stream.' : 'The dam cannot cross more than one stream.' };
  const barrier = new Uint8Array(n * n);
  markBarrier(barrier, n, gridPoint(record, points[0]), gridPoint(record, points[1]));
  const seed = seedFor(record, crossings[0], crestElevationM);
  if (!seed) return { ok: false, error: 'The upstream pond basin could not be located.' };
  const filled = new Uint8Array(n * n);
  const queue = new Int32Array(n * n);
  let head = 0, tail = 0, escaped = false;
  const start = seed[1] * n + seed[0];
  if (barrier[start]) return { ok: false, error: 'The dam is too short to separate the upstream basin.' };
  filled[start] = 1; queue[tail++] = start;
  let depthSum = 0, maxDepth = 0;
  while (head < tail) {
    const index = queue[head++], x = index % n, y = Math.floor(index / n);
    if (x === 0 || y === 0 || x === n - 1 || y === n - 1) escaped = true;
    const depth = Math.max(0, crestElevationM - record.sampleHeights[index]);
    depthSum += depth; maxDepth = Math.max(maxDepth, depth);
    const neighbours = [index - 1, index + 1, index - n, index + n];
    for (const next of neighbours) {
      const nx = next % n, ny = Math.floor(next / n);
      if (next < 0 || next >= filled.length || Math.abs(nx - x) + Math.abs(ny - y) !== 1) continue;
      const height = record.sampleHeights[next];
      if (!filled[next] && !barrier[next] && validElevation(height) && height <= crestElevationM + 0.25) {
        filled[next] = 1; queue[tail++] = next;
      }
    }
  }
  if (escaped) return { ok: false, error: 'The pond is not contained within the available terrain.' };
  const centerLat = (bounds.north + bounds.south) / 2;
  const widthM = (bounds.east - bounds.west) * METERS_PER_DEGREE_LAT * Math.cos(centerLat * Math.PI / 180);
  const heightM = (bounds.north - bounds.south) * METERS_PER_DEGREE_LAT;
  const cellAreaM2 = widthM * heightM / ((n - 1) * (n - 1));
  const areaM2 = tail * cellAreaM2;
  const capacityM3 = depthSum * cellAreaM2;
  if (areaM2 < MIN_POND_AREA_M2 || capacityM3 <= 1 || maxDepth <= 0.1)
    return { ok: false, error: 'This alignment does not create a usable pond.' };
  const polygons = maskToPolygons(filled, n, { blurRadius: 0, blurIterations: 0,
    simplifyTol: 1, minAreaCells: 4 });
  if (!polygons.length) return { ok: false, error: 'The pond boundary could not be generated.' };
  const largest = polygons.reduce((best, value) => value.outer.length > best.outer.length ? value : best);
  const pondRings = [largest.outer, ...largest.holes].map((ring) => ring.map(([x, y]) =>
    unitToLngLat(x / (n - 1), y / (n - 1), bounds)));
  let maxDamHeightM = 0;
  let damHeightSumM = 0;
  let damHeightSamples = 0;
  for (let i = 0; i <= 64; i++) {
    const t = i / 64;
    const point: [number, number] = [points[0][0] + (points[1][0] - points[0][0]) * t,
      points[0][1] + (points[1][1] - points[0][1]) * t];
    const height = sample(record, point);
    if (height != null) {
      const structuralHeightM = Math.max(0, crestElevationM - height);
      maxDamHeightM = Math.max(maxDamHeightM, structuralHeightM);
      damHeightSumM += structuralHeightM;
      damHeightSamples++;
    }
  }
  const averageDamHeightM = damHeightSamples > 0 ? damHeightSumM / damHeightSamples : 0;
  const sourceWidthM = effectiveWaterwayWidthM(crossings[0].stream);
  return { ok: true, result: { pondRings, areaM2, averageDepthM: capacityM3 / areaM2,
    capacityM3, averageDamHeightM, maxDamHeightM, crossing: crossings[0], sourceWidthM,
    inflowM3s: gameplayStreamFlowM3s(sourceWidthM) } };
}

function validPoint(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 && value.every((v) => typeof v === 'number' && Number.isFinite(v));
}

export function sanitizeDams(raw: unknown[]): SavedDam[] {
  const dams: SavedDam[] = [];
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue;
    const value = item as Record<string, unknown>;
    if (typeof value.id !== 'string' || typeof value.name !== 'string' ||
      !Array.isArray(value.points) || value.points.length !== 2 || !value.points.every(validPoint) ||
      !Array.isArray(value.pondRings) || !value.pondRings.every((ring) => Array.isArray(ring) && ring.every(validPoint))) continue;
    if (typeof value.crestElevationM !== 'number' || !Number.isFinite(value.crestElevationM)) continue;
    const positive = ['sourceWidthM', 'inflowM3s', 'areaM2', 'averageDepthM', 'capacityM3'];
    if (positive.some((key) => typeof value[key] !== 'number' || !Number.isFinite(value[key]) || (value[key] as number) <= 0)) continue;
    dams.push({ id: value.id, name: value.name, points: value.points as SavedDam['points'],
      crestElevationM: value.crestElevationM as number, streamId: typeof value.streamId === 'string' ? value.streamId : '',
      streamName: typeof value.streamName === 'string' ? value.streamName : 'Unnamed stream',
      sourceWidthM: value.sourceWidthM as number, inflowM3s: value.inflowM3s as number,
      pondRings: value.pondRings as SavedDam['pondRings'], areaM2: value.areaM2 as number,
      averageDepthM: value.averageDepthM as number, capacityM3: value.capacityM3 as number,
      averageDamHeightM: typeof value.averageDamHeightM === 'number' && Number.isFinite(value.averageDamHeightM)
        ? Math.max(0, value.averageDamHeightM) : undefined,
      maxDamHeightM: typeof value.maxDamHeightM === 'number' && Number.isFinite(value.maxDamHeightM)
        ? Math.max(0, value.maxDamHeightM) : 0,
      createdAt: typeof value.createdAt === 'string' ? value.createdAt : new Date().toISOString() });
  }
  return dams;
}

export function nextDamName(existing: SavedDam[]): string {
  const names = new Set(existing.map((dam) => dam.name));
  for (let i = 1; ; i++) if (!names.has(`Dam ${i}`)) return `Dam ${i}`;
}
