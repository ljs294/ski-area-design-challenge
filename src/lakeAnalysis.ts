import type { TerrainRecord, WaterPolygonFeature } from './types';

const EARTH_RADIUS_M = 6_371_000;
const M_TO_FT = 3.280839895;
const M3_TO_US_GALLONS = 264.172052;

export type LakeDepthSource = 'terrain-estimate' | 'override' | 'unavailable';

export interface LakeAnalysis {
  id: string;
  name: string;
  sourceName: string | null;
  nameSource: 'player' | 'osm' | 'unnamed';
  areaM2: number;
  surfaceElevationM: number | null;
  estimatedAverageDepthM: number | null;
  averageDepthM: number | null;
  depthSource: LakeDepthSource;
  volumeM3: number | null;
}

type Position = [number, number];

function median(values: number[]): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

function ringAreaM2(ring: Position[]): number {
  if (ring.length < 3) return 0;
  const toRadians = Math.PI / 180;
  let sphericalSum = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    let deltaLng = (b[0] - a[0]) * toRadians;
    if (deltaLng > Math.PI) deltaLng -= Math.PI * 2;
    if (deltaLng < -Math.PI) deltaLng += Math.PI * 2;
    sphericalSum += deltaLng * (2 + Math.sin(a[1] * toRadians) + Math.sin(b[1] * toRadians));
  }
  return Math.abs(sphericalSum) * EARTH_RADIUS_M * EARTH_RADIUS_M / 2;
}

export function pointInRing(point: Position, ring: Position[]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i];
    const [xj, yj] = ring[j];
    const crosses = ((yi > point[1]) !== (yj > point[1])) &&
      point[0] < (xj - xi) * (point[1] - yi) / (yj - yi) + xi;
    if (crosses) inside = !inside;
  }
  return inside;
}

function validHoles(feature: WaterPolygonFeature): Position[][] {
  const outer = feature.rings[0] ?? [];
  return feature.rings.slice(1).filter((ring) => ring.length >= 3 &&
    ring.some((point) => pointInRing(point, outer)));
}

function pointInLake(point: Position, feature: WaterPolygonFeature): boolean {
  const outer = feature.rings[0];
  return !!outer && pointInRing(point, outer) &&
    !validHoles(feature).some((hole) => pointInRing(point, hole));
}

/** Small-area geodesic approximation; subtracts only holes contained by the outer ring. */
export function lakeSurfaceAreaM2(feature: WaterPolygonFeature): number {
  const outer = feature.rings[0];
  if (!outer || outer.length < 3) return 0;
  return Math.max(0, ringAreaM2(outer) -
    validHoles(feature).reduce((sum, hole) => sum + ringAreaM2(hole), 0));
}

function sampleRecord(record: TerrainRecord, point: Position): number | null {
  const bounds = record.bounds;
  const width = record.sampleGridSize;
  const height = record.sampleHeights.length / width;
  if (!bounds || width < 2 || !Number.isInteger(height) || height < 2) return null;
  const u = (point[0] - bounds.west) / (bounds.east - bounds.west);
  const v = (bounds.north - point[1]) / (bounds.north - bounds.south);
  if (u < 0 || u > 1 || v < 0 || v > 1) return null;
  const x = u * (width - 1);
  const y = v * (height - 1);
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(width - 1, x0 + 1), y1 = Math.min(height - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const h00 = record.sampleHeights[y0 * width + x0];
  const h10 = record.sampleHeights[y0 * width + x1];
  const h01 = record.sampleHeights[y1 * width + x0];
  const h11 = record.sampleHeights[y1 * width + x1];
  if (![h00, h10, h01, h11].every(Number.isFinite)) return null;
  return h00 * (1 - tx) * (1 - ty) + h10 * tx * (1 - ty) +
    h01 * (1 - tx) * ty + h11 * tx * ty;
}

function interiorElevations(feature: WaterPolygonFeature, record: TerrainRecord): number[] {
  const outer = feature.rings[0] ?? [];
  if (outer.length < 3 || !record.bounds) return [];
  const lngs = outer.map((p) => p[0]);
  const lats = outer.map((p) => p[1]);
  const west = Math.min(...lngs), east = Math.max(...lngs);
  const south = Math.min(...lats), north = Math.max(...lats);
  const cols = Math.max(3, Math.min(20, Math.ceil(Math.sqrt(256 * (east - west) /
    Math.max(1e-12, north - south)))));
  const rows = Math.max(3, Math.min(20, Math.ceil(256 / cols)));
  const values: number[] = [];
  for (let row = 0; row < rows; row++) {
    for (let col = 0; col < cols; col++) {
      const point: Position = [west + (col + 0.5) / cols * (east - west),
        south + (row + 0.5) / rows * (north - south)];
      if (!pointInLake(point, feature)) continue;
      const value = sampleRecord(record, point);
      if (value != null) values.push(value);
    }
  }
  // Tiny polygons may fall between the interior grid's sample centers.
  if (values.length < 3) {
    for (const point of outer) {
      const value = sampleRecord(record, point);
      if (value != null) values.push(value);
    }
  }
  return values;
}

function shorelineSlopes(feature: WaterPolygonFeature, record: TerrainRecord,
  surfaceElevationM: number, radiusM: number): number[] {
  const ring = feature.rings[0] ?? [];
  if (ring.length < 3) return [];
  const bandM = Math.max(20, Math.min(100, radiusM * 0.25));
  const slopes: number[] = [];
  const step = Math.max(1, Math.ceil(ring.length / 64));
  for (let i = 0; i < ring.length; i += step) {
    const prev = ring[(i - 1 + ring.length) % ring.length];
    const point = ring[i];
    const next = ring[(i + 1) % ring.length];
    const latRad = point[1] * Math.PI / 180;
    const tx = (next[0] - prev[0]) * Math.cos(latRad);
    const ty = next[1] - prev[1];
    const length = Math.hypot(tx, ty);
    if (length === 0) continue;
    const normals: [number, number][] = [[-ty / length, tx / length], [ty / length, -tx / length]];
    for (const distanceM of [bandM / 2, bandM]) {
      for (const [nx, ny] of normals) {
        const candidate: Position = [
          point[0] + nx * distanceM / (EARTH_RADIUS_M * Math.cos(latRad)) * 180 / Math.PI,
          point[1] + ny * distanceM / EARTH_RADIUS_M * 180 / Math.PI,
        ];
        if (pointInLake(candidate, feature)) continue;
        const elevation = sampleRecord(record, candidate);
        if (elevation == null) continue;
        const slope = (elevation - surfaceElevationM) / distanceM;
        if (slope > 0.002 && Number.isFinite(slope)) slopes.push(slope);
      }
    }
  }
  return slopes;
}

export function estimateLakeAverageDepthM(feature: WaterPolygonFeature, record: TerrainRecord,
  areaM2 = lakeSurfaceAreaM2(feature)): { surfaceElevationM: number | null; averageDepthM: number | null } {
  if (!(areaM2 > 0)) return { surfaceElevationM: null, averageDepthM: null };
  const surfaceElevationM = median(interiorElevations(feature, record));
  if (surfaceElevationM == null) return { surfaceElevationM: null, averageDepthM: null };
  const radiusM = Math.sqrt(areaM2 / Math.PI);
  const slopes = shorelineSlopes(feature, record, surfaceElevationM, radiusM);
  if (slopes.length < 6) return { surfaceElevationM, averageDepthM: null };
  const shorelineSlope = median(slopes)!;
  const rawDepth = shorelineSlope * radiusM / 3;
  if (rawDepth < 0.25) return { surfaceElevationM, averageDepthM: null };
  const maximum = Math.max(0.5, Math.min(50, radiusM / 3));
  return { surfaceElevationM, averageDepthM: Math.max(0.5, Math.min(maximum, rawDepth)) };
}

export function analyzeLake(feature: WaterPolygonFeature, record: TerrainRecord,
  overrideDepthM?: number, overrideName?: string): LakeAnalysis {
  const areaM2 = lakeSurfaceAreaM2(feature);
  const estimate = estimateLakeAverageDepthM(feature, record, areaM2);
  const hasOverride = Number.isFinite(overrideDepthM) && overrideDepthM! > 0;
  const sourceName = feature.name?.trim() || null;
  const playerName = overrideName?.trim() || null;
  const averageDepthM = hasOverride ? overrideDepthM! : estimate.averageDepthM;
  return {
    id: feature.id,
    name: playerName ?? sourceName ?? 'Unnamed lake',
    sourceName,
    nameSource: playerName ? 'player' : sourceName ? 'osm' : 'unnamed',
    areaM2,
    surfaceElevationM: estimate.surfaceElevationM,
    estimatedAverageDepthM: estimate.averageDepthM,
    averageDepthM,
    depthSource: hasOverride ? 'override' : estimate.averageDepthM == null ? 'unavailable' : 'terrain-estimate',
    volumeM3: averageDepthM == null ? null : areaM2 * averageDepthM,
  };
}

export function formatLakeArea(areaM2: number, units: 'imperial' | 'metric'): string {
  return units === 'imperial'
    ? `${(areaM2 / 4046.8564224).toFixed(areaM2 < 40_468 ? 1 : 0)} ac`
    : `${(areaM2 / 10_000).toFixed(areaM2 < 100_000 ? 1 : 0)} ha`;
}

export function formatLakeDepth(depthM: number | null, units: 'imperial' | 'metric'): string {
  if (depthM == null) return '—';
  return `${(units === 'imperial' ? depthM * M_TO_FT : depthM).toFixed(1)} ${units === 'imperial' ? 'ft' : 'm'}`;
}

export function formatLakeVolume(volumeM3: number | null, units: 'imperial' | 'metric'): string {
  if (volumeM3 == null) return '—';
  const amount = units === 'imperial' ? volumeM3 * M3_TO_US_GALLONS : volumeM3 * 1000;
  const suffix = units === 'imperial' ? 'gal' : 'L';
  return amount >= 1_000_000
    ? `${(amount / 1_000_000).toFixed(1)}M ${suffix}`
    : `${(amount / 1_000).toFixed(1)}K ${suffix}`;
}

export function depthToDisplay(depthM: number, units: 'imperial' | 'metric'): number {
  return units === 'imperial' ? depthM * M_TO_FT : depthM;
}

export function depthFromDisplay(depth: number, units: 'imperial' | 'metric'): number {
  return units === 'imperial' ? depth / M_TO_FT : depth;
}

export function sanitizeLakeDepthOverrides(raw: unknown): Record<string, number> {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const result: Record<string, number> = {};
  for (const [id, value] of Object.entries(raw)) {
    if (id && typeof value === 'number' && Number.isFinite(value) && value > 0 && value <= 1000) {
      result[id] = value;
    }
  }
  return result;
}

export function sanitizeLakeNameOverrides(raw: unknown): Record<string, string> {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const result: Record<string, string> = {};
  for (const [id, value] of Object.entries(raw)) {
    if (!id || typeof value !== 'string') continue;
    const name = value.trim().slice(0, 80);
    if (name) result[id] = name;
  }
  return result;
}
