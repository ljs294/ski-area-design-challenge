import { maskToPolygons } from './coverPolygons';
import { designDamEmbankment, MAX_DAM_HEIGHT_M } from './damEarthwork';
import { localMeters, segmentOffset, terrainMetrics } from './earthwork';
import { haversineMeters, lngLatToUnit, METERS_PER_DEGREE_LAT, unitToLngLat } from './geo';
import type { SavedDam } from './types';
import type { EarthworkEstimate } from './types/earthwork';
import type { TerrainRecord } from './types/terrain';
import type { WaterLineFeature } from './types/vectorFeatures';

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
  /** Top of the embankment: full pool plus freeboard. */
  damCrestElevationM: number;
  /** Deck polygon along the built stretch of the alignment. */
  crestRing: [number, number][];
  /** Length of alignment standing above natural ground. */
  builtLengthM: number;
  disturbedAreaM2: number;
  earthwork: EarthworkEstimate;
  /** Elevation edits the embankment commits to the terrain package. */
  patchIndices: Uint32Array;
  patchHeights: Float32Array;
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

/**
 * Ground elevation on the core sample grid. The dam tool takes full pool from
 * this rather than the surround-blended resort sampler, so the clicked bank,
 * the snapped opposite bank, and the surface the pond is flooded over are all
 * the same grid.
 */
export function damCrestElevationAt(record: TerrainRecord,
  point: [number, number]): number | null {
  return sample(record, point);
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
  const dx = b[0] - a[0], dy = b[1] - a[1];
  const length = Math.max(0.001, Math.hypot(dx, dy));
  // Delineation uses the full cross-valley plane. The clicked endpoints are
  // where the structure meets equal-height banks, but stopping the raster wall
  // there lets a flood route around a sub-cell contour endpoint, onto the
  // downstream side, and then all the way to the terrain boundary. Extending
  // the same plane to the raster edge only blocks that impossible downstream
  // detour; a genuinely uncontained upstream basin still reaches one of the
  // boundary edges on its own side and is rejected.
  const extension = n * 2;
  const start: [number, number] = [a[0] - dx / length * extension, a[1] - dy / length * extension];
  const end: [number, number] = [b[0] + dx / length * extension, b[1] + dy / length * extension];
  const steps = Math.max(1, Math.ceil(Math.hypot(end[0] - start[0], end[1] - start[1]) * 2));
  for (let i = 0; i <= steps; i++) {
    const t = i / steps, x = Math.round(start[0] + (end[0] - start[0]) * t),
      y = Math.round(start[1] + (end[1] - start[1]) * t);
    for (let oy = -1; oy <= 1; oy++) for (let ox = -1; ox <= 1; ox++) {
      const px = x + ox, py = y + oy;
      if (px >= 0 && px < n && py >= 0 && py < n) mask[py * n + px] = 1;
    }
  }
}

function pointAlongStream(crossing: DamStreamCrossing, direction: -1 | 1,
  distanceM: number): [number, number] {
  const points = crossing.stream.points;
  let segment = crossing.segmentIndex;
  let current = crossing.point;
  let target = direction < 0 ? points[segment] : points[segment + 1];
  let remaining = distanceM;
  while (true) {
    const length = haversineMeters(current, target);
    if (length >= remaining && length > 0) {
      const t = remaining / length;
      return [current[0] + (target[0] - current[0]) * t,
        current[1] + (target[1] - current[1]) * t];
    }
    remaining -= length;
    current = target;
    segment += direction;
    const nextIndex = direction < 0 ? segment : segment + 1;
    if (nextIndex < 0 || nextIndex >= points.length) return current;
    target = points[nextIndex];
  }
}

function median(values: number[]): number | null {
  if (!values.length) return null;
  values.sort((a, b) => a - b);
  const middle = Math.floor(values.length / 2);
  return values.length % 2 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
}

/** Robustly choose the rising (upstream) direction across several DEM samples. */
function upstreamDirection(record: TerrainRecord, crossing: DamStreamCrossing): -1 | 1 {
  const elevations = (direction: -1 | 1) => median([20, 50, 100]
    .map((distance) => sample(record, pointAlongStream(crossing, direction, distance)))
    .filter((value): value is number => value != null));
  const beforeM = elevations(-1), afterM = elevations(1);
  if (beforeM != null && afterM != null && Math.abs(afterM - beforeM) > 0.25)
    return afterM > beforeM ? 1 : -1;
  // OSM commonly follows downstream order, but only use that convention when
  // the wider terrain signal really is flat or unavailable.
  return -1;
}

function seedFor(record: TerrainRecord, crossing: DamStreamCrossing, crest: number,
  barrier: Uint8Array): [number, number] | null {
  const direction = upstreamDirection(record, crossing);
  const upstream = pointAlongStream(crossing, direction, 50);
  const [crossX, crossY] = gridPoint(record, crossing.point);
  const target = gridPoint(record, upstream);
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
      const index = y * n + x;
      const upstreamDistance = ((x - crossX) * vectorX + (y - crossY) * vectorY) / vectorLength;
      if (x >= 0 && x < n && y >= 0 && y < n && upstreamDistance >= 2 && !barrier[index] &&
        validElevation(record.sampleHeights[index]) &&
        record.sampleHeights[y * n + x] <= crest)
        return [x, y];
    }
  }
  return null;
}

interface Basin {
  filled: Uint8Array;
  cells: number;
  depthSumM: number;
  maxDepthM: number;
  escaped: boolean;
  /** Deepest cell in the basin — the seed that survives the embankment. */
  lowestIndex: number;
}

/** Flood the terrain behind the alignment up to full pool. */
function floodBasin(heights: ArrayLike<number>, n: number, barrier: Uint8Array,
  start: number, waterElevationM: number): Basin {
  const filled = new Uint8Array(n * n);
  const queue = new Int32Array(n * n);
  let head = 0, tail = 0, escaped = false, depthSumM = 0, maxDepthM = 0;
  let lowestIndex = start;
  filled[start] = 1; queue[tail++] = start;
  while (head < tail) {
    const index = queue[head++], x = index % n, y = Math.floor(index / n);
    if (x === 0 || y === 0 || x === n - 1 || y === n - 1) escaped = true;
    const depth = Math.max(0, waterElevationM - heights[index]);
    depthSumM += depth;
    if (depth > maxDepthM) { maxDepthM = depth; lowestIndex = index; }
    const neighbours = [index - 1, index + 1, index - n, index + n];
    for (const next of neighbours) {
      const nx = next % n, ny = Math.floor(next / n);
      if (next < 0 || next >= filled.length || Math.abs(nx - x) + Math.abs(ny - y) !== 1) continue;
      const height = heights[next];
      // Strictly the water surface: full pool sits on the contour the player
      // clicked, so any over-tolerance here is a band of cells at bank height
      // that the flood can creep along and off the edge of the raster.
      if (!filled[next] && !barrier[next] && validElevation(height) && height <= waterElevationM) {
        filled[next] = 1; queue[tail++] = next;
      }
    }
  }
  return { filled, cells: tail, depthSumM, maxDepthM, escaped, lowestIndex };
}

/**
 * Full dam design: the embankment that holds the water back, and the pond it
 * impounds. The reservoir is delineated twice — once on natural ground to find
 * the basin and which flank of the alignment it sits on, then again on the
 * graded surface, so the reported shoreline and capacity describe the pond that
 * will exist once the dam is built rather than the bare valley it started as.
 *
 * `crestElevationM` is full pool, taken from the snapped alignment; the
 * structure itself is graded a freeboard above it.
 */
export function analyzeDam(record: TerrainRecord, points: [[number, number], [number, number]],
  crestElevationM: number, streams: WaterLineFeature[]): DamAnalysisOutcome {
  const bounds = record.bounds, n = record.sampleGridSize;
  const metrics = terrainMetrics(record);
  if (!bounds || !metrics || n < 2 || record.sampleHeights.length !== n * n)
    return { ok: false, error: 'Terrain coverage is unavailable for this dam.' };
  const crossings = damStreamCrossings(points, streams);
  if (crossings.length !== 1) return { ok: false, error: crossings.length === 0
    ? 'The dam must cross one stream.' : 'The dam cannot cross more than one stream.' };
  const barrier = new Uint8Array(n * n);
  markBarrier(barrier, n, gridPoint(record, points[0]), gridPoint(record, points[1]));
  const seed = seedFor(record, crossings[0], crestElevationM, barrier);
  if (!seed) return { ok: false, error: 'The upstream pond basin could not be located.' };
  const start = seed[1] * n + seed[0];
  if (barrier[start]) return { ok: false, error: 'The dam is too short to separate the upstream basin.' };
  const natural = floodBasin(record.sampleHeights, n, barrier, start, crestElevationM);
  if (natural.escaped) return { ok: false, error: 'Full pool at this bank backs water past the edge of the available terrain. Pick a lower point on the bank, or move the dam upstream.' };

  // The basin tells the embankment which face is the wet one.
  const a = localMeters(metrics, points[0]), b = localMeters(metrics, points[1]);
  const seedX = (natural.lowestIndex % n) * metrics.dxM;
  const seedY = Math.floor(natural.lowestIndex / n) * metrics.dyM;
  const seedSide = segmentOffset(a, b, seedX, seedY).side;
  const embankment = designDamEmbankment(record, points, crestElevationM,
    seedSide === 0 ? 1 : seedSide);
  if (!embankment) return { ok: false, error: 'The dam embankment could not be graded into this terrain.' };
  if (embankment.truncated)
    return { ok: false, error: 'The embankment cannot reach natural ground inside the available terrain. Move the dam to a narrower crossing or lower the crest.' };
  if (embankment.maxHeightM > MAX_DAM_HEIGHT_M)
    return { ok: false, error: `This dam would stand ${Math.round(embankment.maxHeightM)} m tall. Keep it under ${MAX_DAM_HEIGHT_M} m — lower the crest or find a narrower crossing.` };

  // Re-delineate against the built surface. The deepest *natural* cell is the
  // thalweg right at the alignment, which the upstream face buries on any grid
  // finer than a few metres, so seed from the deepest cell that is still under
  // water once the embankment is in — one seed, so the pond stays one body.
  const graded = Float32Array.from(record.sampleHeights);
  for (let i = 0; i < embankment.patchIndices.length; i++)
    graded[embankment.patchIndices[i]] = embankment.patchHeights[i];
  let seedIndex = -1, deepestM = 0;
  for (let i = 0; i < graded.length; i++) {
    if (!natural.filled[i]) continue;
    const depthM = crestElevationM - graded[i];
    if (depthM > deepestM) { deepestM = depthM; seedIndex = i; }
  }
  if (seedIndex < 0)
    return { ok: false, error: 'The embankment fills the basin it would impound. Move the dam to a narrower crossing.' };
  // The graded surface only ever rises, so this basin is a subset of the
  // natural one and cannot reach an edge the natural flood did not.
  const pool = floodBasin(graded, n, barrier, seedIndex, crestElevationM);

  const areaM2 = pool.cells * metrics.cellAreaM2;
  const capacityM3 = pool.depthSumM * metrics.cellAreaM2;
  if (areaM2 < MIN_POND_AREA_M2 || capacityM3 <= 1 || pool.maxDepthM <= 0.1)
    return { ok: false, error: 'This alignment does not create a usable pond.' };
  const polygons = maskToPolygons(pool.filled, n, { blurRadius: 0, blurIterations: 0,
    simplifyTol: 1, minAreaCells: 4 });
  if (!polygons.length) return { ok: false, error: 'The pond boundary could not be generated.' };
  const largest = polygons.reduce((best, value) => value.outer.length > best.outer.length ? value : best);
  const pondRings = [largest.outer, ...largest.holes].map((ring) => ring.map(([x, y]) =>
    unitToLngLat(x / (n - 1), y / (n - 1), bounds)));
  const sourceWidthM = effectiveWaterwayWidthM(crossings[0].stream);
  return { ok: true, result: { pondRings, areaM2, averageDepthM: capacityM3 / areaM2,
    capacityM3, averageDamHeightM: embankment.averageHeightM,
    maxDamHeightM: embankment.maxHeightM,
    damCrestElevationM: embankment.crestElevationM,
    crestRing: embankment.crestRing,
    builtLengthM: embankment.builtLengthM,
    disturbedAreaM2: embankment.disturbedAreaM2,
    earthwork: { cutM3: embankment.cutM3, fillM3: embankment.fillM3,
      balanceM3: embankment.balanceM3 },
    patchIndices: embankment.patchIndices, patchHeights: embankment.patchHeights,
    crossing: crossings[0], sourceWidthM,
    inflowM3s: gameplayStreamFlowM3s(sourceWidthM) } };
}

function validPoint(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 && value.every((v) => typeof v === 'number' && Number.isFinite(v));
}

function finiteOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function ringOrUndefined(value: unknown): [number, number][] | undefined {
  return Array.isArray(value) && value.length >= 4 && value.every(validPoint)
    ? value as [number, number][] : undefined;
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
    const earthwork = value.earthwork as Record<string, unknown> | undefined;
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
      // Embankment fields arrived with schema v10; dams saved before it were a
      // crest line on the map and never regraded the terrain.
      damCrestElevationM: finiteOrUndefined(value.damCrestElevationM),
      crestRing: ringOrUndefined(value.crestRing),
      footprintRings: Array.isArray(value.footprintRings) &&
        value.footprintRings.every((ring) => Array.isArray(ring) && ring.every(validPoint))
        ? value.footprintRings as [number, number][][] : undefined,
      builtLengthM: finiteOrUndefined(value.builtLengthM),
      disturbedAreaM2: finiteOrUndefined(value.disturbedAreaM2),
      terrainGraded: value.terrainGraded === true ? true : undefined,
      earthwork: earthwork && ['cutM3', 'fillM3', 'balanceM3'].every((key) =>
        typeof earthwork[key] === 'number' && Number.isFinite(earthwork[key]))
        ? { cutM3: earthwork.cutM3 as number, fillM3: earthwork.fillM3 as number,
          balanceM3: earthwork.balanceM3 as number } : undefined,
      createdAt: typeof value.createdAt === 'string' ? value.createdAt : new Date().toISOString() });
  }
  return dams;
}

export function nextDamName(existing: SavedDam[]): string {
  const names = new Set(existing.map((dam) => dam.name));
  for (let i = 1; ; i++) if (!names.has(`Dam ${i}`)) return `Dam ${i}`;
}
