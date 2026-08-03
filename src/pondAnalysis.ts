import { unitToLngLat } from './geo';
import { lakeSurfaceAreaM2, pointInRing } from './lakeAnalysis';
import { designPondEarthwork, MAX_POND_BERM_HEIGHT_M,
  MAX_POND_EXCAVATION_M } from './pondEarthwork';
import type { EarthworkEstimate, SavedPond, TerrainRecord } from './types';

export const MIN_STANDALONE_POND_AREA_M2 = 100;

export interface StandalonePondAnalysis {
  boundary: [number, number][];
  topElevationM: number;
  /** How far the floor is dug below full pool before the berm is built. */
  excavationDepthM: number;
  /** Top of the berm: full pool plus freeboard. */
  crestElevationM: number;
  areaM2: number;
  averageDepthM: number;
  maxDepthM: number;
  capacityM3: number;
  maxBermHeightM: number;
  bermLengthM: number;
  maxCutDepthM: number;
  disturbedAreaM2: number;
  earthwork: EarthworkEstimate;
}

export type StandalonePondOutcome = { ok: true; result: StandalonePondAnalysis } |
  { ok: false; error: string };

function validElevation(value: number): boolean {
  return Number.isFinite(value) && value > -1000 && value < 10000;
}

function finiteOrUndefined(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
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

/**
 * Suggest a useful first value while leaving the final full-pool elevation
 * user-controlled. Mean ground inside the boundary is the balanced starting
 * point: on a hillside the uphill half is cut and the downhill half becomes
 * berm, which is how a pond gets sited. Setting it to the high point instead
 * would ask for a dam as tall as the slope the player drew across.
 */
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
  let sumM = 0, samples = 0;
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
    const point = unitToLngLat(x / (n - 1), y / (n - 1), bounds);
    if (!pointInRing(point, ring)) continue;
    const elevation = record.sampleHeights[y * n + x];
    if (validElevation(elevation)) { sumM += elevation; samples++; }
  }
  if (samples === 0) {
    const center: [number, number] = [points.reduce((sum, point) => sum + point[0], 0) / points.length,
      points.reduce((sum, point) => sum + point[1], 0) / points.length];
    for (const point of [...points, center]) {
      const elevation = sampleTerrain(record, point);
      if (elevation != null) { sumM += elevation; samples++; }
    }
  }
  // The extra metre keeps a pond on genuinely flat ground from starting dry.
  return samples > 0 ? Math.ceil(sumM / samples * 2) / 2 + 1 : null;
}

/**
 * Full pond design: the pool the player drew, the berm that holds it in, and
 * the earthwork bill for both. Depths and volume are measured on the *graded*
 * surface, so the numbers describe the pond that will exist after construction
 * rather than the natural hollow it started from.
 */
export function analyzeStandalonePond(record: TerrainRecord, points: [number, number][],
  topElevationM: number, excavationDepthM = 0): StandalonePondOutcome {
  const bounds = record.bounds, n = record.sampleGridSize;
  if (!bounds || n < 2 || record.sampleHeights.length !== n * n)
    return { ok: false, error: 'Terrain coverage is unavailable for this pond.' };
  if (!validElevation(topElevationM)) return { ok: false, error: 'Enter a valid top-of-pond elevation.' };
  if (!Number.isFinite(excavationDepthM) || excavationDepthM < 0 ||
    excavationDepthM > MAX_POND_EXCAVATION_M)
    return { ok: false, error: `Enter an excavation depth between 0 and ${MAX_POND_EXCAVATION_M} m.` };
  if (!pondBoundaryIsSimple(points))
    return { ok: false, error: 'Draw a boundary with at least three points that does not cross itself.' };
  const boundary = closedBoundary(points);
  if (boundary.some(([lng, lat]) => lng < bounds.west || lng > bounds.east || lat < bounds.south || lat > bounds.north))
    return { ok: false, error: 'Keep the entire pond boundary inside the available terrain.' };
  const areaM2 = geometryAreaM2(boundary);
  if (areaM2 < MIN_STANDALONE_POND_AREA_M2)
    return { ok: false, error: 'This pond is too small. Draw a boundary of at least 100 m².' };

  const design = designPondEarthwork(record, boundary,
    { topElevationM, excavationDepthM, poolAreaM2: areaM2 });
  if (!design) return { ok: false, error: 'Terrain coverage is unavailable for this pond.' };
  if (design.coveredSamples > 0 && design.validSamples / design.coveredSamples < 0.8)
    return { ok: false, error: 'The pond boundary does not have enough valid terrain coverage.' };
  if (design.truncated)
    return { ok: false, error: 'The berm cannot reach natural ground inside the available terrain. Lower the top of pond or draw a smaller boundary.' };
  if (design.maxBermHeightM > MAX_POND_BERM_HEIGHT_M)
    return { ok: false, error: `This pond needs a ${Math.round(design.maxBermHeightM)} m berm. Keep it under ${MAX_POND_BERM_HEIGHT_M} m — lower the top of pond, or dam a stream instead.` };

  const shared = {
    boundary, topElevationM, excavationDepthM: design.excavationDepthM,
    crestElevationM: design.crestElevationM, areaM2,
    maxBermHeightM: design.maxBermHeightM, bermLengthM: design.bermLengthM,
    maxCutDepthM: design.maxCutDepthM, disturbedAreaM2: design.disturbedAreaM2,
    earthwork: { cutM3: design.cutM3, fillM3: design.fillM3, balanceM3: design.balanceM3 },
  };
  // A pond smaller than the elevation grid encloses no sample. Fall back to a
  // single interpolated reading at its centre so tiny ponds still price out.
  if (design.coveredSamples === 0) {
    const openBoundary = boundary.slice(0, -1);
    const center: [number, number] = [openBoundary.reduce((sum, point) => sum + point[0], 0) / openBoundary.length,
      openBoundary.reduce((sum, point) => sum + point[1], 0) / openBoundary.length];
    const groundM = sampleTerrain(record, center);
    if (groundM == null) return { ok: false, error: 'The pond boundary does not have enough valid terrain coverage.' };
    const depthM = Math.max(0, topElevationM - Math.min(groundM, topElevationM - excavationDepthM));
    if (depthM < 0.1) return { ok: false, error: 'Raise the top elevation above the ground inside the pond.' };
    return { ok: true, result: { ...shared,
      averageDepthM: depthM, maxDepthM: depthM, capacityM3: areaM2 * depthM } };
  }
  if (design.capacityM3 <= 1 || design.maxDepthM < 0.1)
    return { ok: false, error: 'Raise the top elevation above the ground inside the pond.' };
  return { ok: true, result: { ...shared, averageDepthM: design.averageDepthM,
    maxDepthM: design.maxDepthM, capacityM3: design.capacityM3 } };
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
    const earthwork = value.earthwork as Record<string, unknown> | undefined;
    ponds.push({ id: value.id, name: value.name,
      boundary: closedBoundary(value.boundary as [number, number][]),
      topElevationM: value.topElevationM as number, areaM2: value.areaM2 as number,
      averageDepthM: value.averageDepthM as number, maxDepthM: value.maxDepthM as number,
      capacityM3: value.capacityM3 as number,
      // Schema-v9 ponds were all described as snowmaking ponds.
      isSnowmaking: typeof value.isSnowmaking === 'boolean' ? value.isSnowmaking : true,
      // Grading fields arrived with schema v10; ponds built before it kept the
      // natural hollow they were drawn on.
      excavationDepthM: finiteOrUndefined(value.excavationDepthM),
      crestElevationM: finiteOrUndefined(value.crestElevationM),
      maxBermHeightM: finiteOrUndefined(value.maxBermHeightM),
      bermLengthM: finiteOrUndefined(value.bermLengthM),
      maxCutDepthM: finiteOrUndefined(value.maxCutDepthM),
      disturbedAreaM2: finiteOrUndefined(value.disturbedAreaM2),
      terrainGraded: value.terrainGraded === true ? true : undefined,
      earthwork: earthwork && ['cutM3', 'fillM3', 'balanceM3'].every((key) =>
        typeof earthwork[key] === 'number' && Number.isFinite(earthwork[key]))
        ? { cutM3: earthwork.cutM3 as number, fillM3: earthwork.fillM3 as number,
          balanceM3: earthwork.balanceM3 as number } : undefined,
      createdAt: typeof value.createdAt === 'string' ? value.createdAt : new Date().toISOString() });
  }
  return ponds;
}

export function nextPondName(existing: SavedPond[]): string {
  const names = new Set(existing.map((pond) => pond.name));
  for (let i = 1; ; i++) if (!names.has(`Pond ${i}`)) return `Pond ${i}`;
}
