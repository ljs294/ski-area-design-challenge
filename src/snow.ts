import type { LatLonBounds } from './types/geo';
import type { SavedSnowGrid, SnowGrid, SnowSurfaceCode } from './types/snow';
import type { TerrainRecord } from './types/terrain';

export const SNOW_SURFACES = [
  { code: 'P', name: 'Powder' },
  { code: 'PP', name: 'Packed Powder' },
  { code: 'MG', name: 'Machine Groomed' },
  { code: 'HP', name: 'Hard Packed' },
  { code: 'IS', name: 'Icy Surface' },
  { code: 'CO', name: 'Corn Snow' },
  { code: 'FG', name: 'Frozen Granular' },
  { code: 'LG', name: 'Loose Granular' },
  { code: 'SC', name: 'Spring Conditions' },
  { code: 'WG', name: 'Wet Granular' },
  { code: 'WP', name: 'Wet Powder' },
] as const satisfies readonly { code: SnowSurfaceCode; name: string }[];

export const SNOW_SURFACE_NONE = 0;
export const SNOW_SURFACE_POWDER = 1;
const MAX_DEPTH_CM = 4095;
const TARGET_CELL_M = 10;
const MAX_GRID_DIMENSION = 512;

export function snowSurfaceCode(value: number): SnowSurfaceCode | null {
  return SNOW_SURFACES[value - 1]?.code ?? null;
}

export function snowSurfaceName(value: number): string | null {
  return SNOW_SURFACES[value - 1]?.name ?? null;
}

function dimensions(bounds: LatLonBounds): { width: number; height: number } {
  const midLat = (bounds.north + bounds.south) / 2;
  const widthM = (bounds.east - bounds.west) * 111_320 * Math.cos(midLat * Math.PI / 180);
  const heightM = (bounds.north - bounds.south) * 111_320;
  let width = Math.max(2, Math.round(widthM / TARGET_CELL_M));
  let height = Math.max(2, Math.round(heightM / TARGET_CELL_M));
  const scale = Math.min(1, MAX_GRID_DIMENSION / Math.max(width, height));
  width = Math.max(2, Math.round(width * scale));
  height = Math.max(2, Math.round(height * scale));
  return { width, height };
}

function bilinearHeight(record: TerrainRecord, u: number, v: number): number {
  const n = record.sampleGridSize;
  const x = Math.max(0, Math.min(n - 1, u * (n - 1)));
  const y = Math.max(0, Math.min(n - 1, v * (n - 1)));
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const a = record.sampleHeights[y0 * n + x0];
  const b = record.sampleHeights[y0 * n + x1];
  const c = record.sampleHeights[y1 * n + x0];
  const d = record.sampleHeights[y1 * n + x1];
  return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}

function winterSnowProbability(record: TerrainRecord): number {
  const months = record.climate?.monthly;
  if (!Array.isArray(months) || months.length < 12) return 0.5;
  const indices = record.latitude >= 0 ? [10, 11, 0, 1, 2] : [4, 5, 6, 7, 8];
  const values = indices.map((index) => months[index]?.snowProbability)
    .filter((value): value is number => typeof value === 'number' && Number.isFinite(value));
  return values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : 0.5;
}

export function snowSlopeRetention(slopeDeg: number): number {
  if (slopeDeg <= 15) return 1;
  if (slopeDeg < 35) return 1 - ((slopeDeg - 15) / 20) * 0.35;
  if (slopeDeg < 50) return 0.65 - ((slopeDeg - 35) / 15) * 0.5;
  if (slopeDeg < 60) return 0.15 * (1 - (slopeDeg - 50) / 10);
  return 0;
}

/** Deterministic midwinter baseline, derived only from the committed terrain. */
export function generateSnowBaseline(record: TerrainRecord): SnowGrid {
  if (!record.bounds || record.sampleGridSize < 2 ||
    record.sampleHeights.length !== record.sampleGridSize ** 2) {
    throw new Error('Snow requires a valid resort elevation grid.');
  }
  const { width, height } = dimensions(record.bounds);
  const depthM = new Float32Array(width * height);
  const surface = new Uint8Array(width * height);
  let min = Infinity, max = -Infinity;
  for (const value of record.sampleHeights) {
    if (!Number.isFinite(value)) continue;
    min = Math.min(min, value); max = Math.max(max, value);
  }
  if (!Number.isFinite(min) || !Number.isFinite(max)) throw new Error('Snow requires finite elevations.');
  const range = Math.max(1, max - min);
  const baseDepth = 0.3 + 1.2 * winterSnowProbability(record);
  const midLat = (record.bounds.north + record.bounds.south) / 2;
  const cellX = ((record.bounds.east - record.bounds.west) * 111_320 *
    Math.cos(midLat * Math.PI / 180)) / Math.max(1, width - 1);
  const cellY = ((record.bounds.north - record.bounds.south) * 111_320) /
    Math.max(1, height - 1);
  const du = 1 / Math.max(1, width - 1), dv = 1 / Math.max(1, height - 1);

  for (let row = 0; row < height; row++) for (let col = 0; col < width; col++) {
    const u = col / Math.max(1, width - 1), v = row / Math.max(1, height - 1);
    const elevation = bilinearHeight(record, u, v);
    const west = bilinearHeight(record, Math.max(0, u - du), v);
    const east = bilinearHeight(record, Math.min(1, u + du), v);
    const north = bilinearHeight(record, u, Math.max(0, v - dv));
    const south = bilinearHeight(record, u, Math.min(1, v + dv));
    const dxDenom = (col === 0 || col === width - 1) ? cellX : 2 * cellX;
    const dyDenom = (row === 0 || row === height - 1) ? cellY : 2 * cellY;
    const dzdx = (east - west) / Math.max(0.01, dxDenom);
    const dzdy = (south - north) / Math.max(0.01, dyDenom);
    const slopeDeg = Math.atan(Math.hypot(dzdx, dzdy)) * 180 / Math.PI;
    const aspectDeg = (Math.atan2(-dzdx, dzdy) * 180 / Math.PI + 360) % 360;
    const elevationFactor = 0.65 + 0.7 * ((elevation - min) / range);
    const aspectFactor = slopeDeg < 4 ? 1 : 1 + 0.2 * Math.cos(aspectDeg * Math.PI / 180);
    const depth = Math.max(0, Math.min(4,
      baseDepth * elevationFactor * aspectFactor * snowSlopeRetention(slopeDeg)));
    const index = row * width + col;
    if (depth >= 0.02) {
      depthM[index] = depth;
      surface[index] = SNOW_SURFACE_POWDER;
    }
  }
  return { bounds: { ...record.bounds }, width, height, depthM, surface };
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

function base64ToBytes(value: string): Uint8Array | null {
  try {
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    return bytes;
  } catch {
    return null;
  }
}

export function encodeSnowGrid(grid: SnowGrid): SavedSnowGrid {
  const count = grid.width * grid.height;
  if (grid.depthM.length !== count || grid.surface.length !== count) {
    throw new Error('Snow grid dimensions do not match its cell arrays.');
  }
  const bytes = new Uint8Array(count * 2);
  const view = new DataView(bytes.buffer);
  for (let index = 0; index < count; index++) {
    const depthCm = Math.max(0, Math.min(MAX_DEPTH_CM, Math.round(grid.depthM[index] * 100)));
    const surface = depthCm === 0 ? 0 : Math.max(1, Math.min(11, grid.surface[index]));
    view.setUint16(index * 2, depthCm | (surface << 12), true);
  }
  return { version: 1, bounds: { ...grid.bounds }, width: grid.width, height: grid.height,
    cells: bytesToBase64(bytes) };
}

function validBounds(value: unknown): value is LatLonBounds {
  if (!value || typeof value !== 'object') return false;
  const bounds = value as Partial<LatLonBounds>;
  return [bounds.west, bounds.south, bounds.east, bounds.north].every((entry) =>
    typeof entry === 'number' && Number.isFinite(entry)) &&
    bounds.west! < bounds.east! && bounds.south! < bounds.north!;
}

export function decodeSnowGrid(value: unknown): SnowGrid | null {
  if (!value || typeof value !== 'object') return null;
  const saved = value as Partial<SavedSnowGrid>;
  if (saved.version !== 1 || !validBounds(saved.bounds) ||
    !Number.isInteger(saved.width) || !Number.isInteger(saved.height) ||
    saved.width! < 2 || saved.height! < 2 || saved.width! > MAX_GRID_DIMENSION ||
    saved.height! > MAX_GRID_DIMENSION || typeof saved.cells !== 'string') return null;
  const count = saved.width! * saved.height!;
  const bytes = base64ToBytes(saved.cells);
  if (!bytes || bytes.length !== count * 2) return null;
  const depthM = new Float32Array(count), surface = new Uint8Array(count);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  for (let index = 0; index < count; index++) {
    const packed = view.getUint16(index * 2, true);
    const depthCm = packed & MAX_DEPTH_CM, condition = packed >>> 12;
    if (condition > 11 || (depthCm > 0 && condition === 0)) return null;
    depthM[index] = depthCm / 100;
    surface[index] = depthCm === 0 ? 0 : condition;
  }
  return { bounds: { ...saved.bounds }, width: saved.width!, height: saved.height!, depthM, surface };
}

function sameBounds(left: LatLonBounds, right: LatLonBounds): boolean {
  return Math.abs(left.west - right.west) < 1e-8 && Math.abs(left.east - right.east) < 1e-8 &&
    Math.abs(left.south - right.south) < 1e-8 && Math.abs(left.north - right.north) < 1e-8;
}

/** Use a valid matching snapshot, otherwise regenerate without blocking load. */
export function hydrateSnowGrid(saved: unknown, terrain: TerrainRecord): SnowGrid {
  const decoded = decodeSnowGrid(saved);
  return decoded && terrain.bounds && sameBounds(decoded.bounds, terrain.bounds)
    ? decoded : generateSnowBaseline(terrain);
}

export function sampleSnowGrid(grid: SnowGrid, lng: number, lat: number):
  { depthM: number; surface: number } | null {
  const b = grid.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const u = (lng - b.west) / (b.east - b.west);
  const v = (b.north - lat) / (b.north - b.south);
  const x = Math.max(0, Math.min(grid.width - 1, u * (grid.width - 1)));
  const y = Math.max(0, Math.min(grid.height - 1, v * (grid.height - 1)));
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(grid.width - 1, x0 + 1), y1 = Math.min(grid.height - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const at = (col: number, row: number) => grid.depthM[row * grid.width + col];
  const depthM = (at(x0, y0) * (1 - tx) + at(x1, y0) * tx) * (1 - ty) +
    (at(x0, y1) * (1 - tx) + at(x1, y1) * tx) * ty;
  const nearestX = Math.min(grid.width - 1, Math.max(0, Math.round(x)));
  const nearestY = Math.min(grid.height - 1, Math.max(0, Math.round(y)));
  return { depthM, surface: grid.surface[nearestY * grid.width + nearestX] };
}
