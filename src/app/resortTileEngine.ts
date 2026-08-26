import type { CoverClassCode } from '../types/cover';
import type { LatLonBounds } from '../types/geo';

export type ResortTileKind = 'dem' | 'cover' | 'slope' | 'aspect';

export interface RasterTerrainRecord {
  key: string;
  bounds: LatLonBounds;
  sampleGridSize: number;
  sampleHeights: Float32Array;
  surround?: {
    bounds: LatLonBounds;
    width: number;
    height: number;
    heights: Float32Array;
  };
  coverGrid?: {
    bounds: LatLonBounds;
    width: number;
    height: number;
    data: Uint8Array;
  };
}

const NODATA_FLOOR = -8999;
const FEATHER_FRAC = 0.08;
const COVER_RGBA: Record<number, [number, number, number, number]> = {
  1: [82, 105, 82, 205], 2: [215, 216, 207, 150], 3: [177, 183, 145, 150], 4: [83, 142, 174, 185],
  10: [47, 81, 53, 205], 20: [113, 128, 90, 190], 30: [197, 200, 153, 120],
  40: [202, 184, 139, 120], 50: [154, 135, 125, 160], 60: [157, 151, 140, 140],
  70: [237, 240, 238, 150], 80: [83, 142, 174, 185], 90: [79, 145, 137, 170],
  95: [39, 105, 69, 205], 100: [192, 193, 153, 120], 255: [0, 0, 0, 0],
};

function unit(lng: number, lat: number, bounds: LatLonBounds): [number, number] {
  return [
    (lng - bounds.west) / (bounds.east - bounds.west),
    (bounds.north - lat) / (bounds.north - bounds.south),
  ];
}

function pixelLngLat(z: number, x: number, y: number, px: number, py: number): [number, number] {
  const n = 2 ** z;
  const xf = (x + (px + 0.5) / 256) / n;
  const yf = (y + (py + 0.5) / 256) / n;
  return [xf * 360 - 180, (Math.atan(Math.sinh(Math.PI * (1 - 2 * yf))) * 180) / Math.PI];
}

function tileAxes(z: number, x: number, y: number): { lng: number[]; lat: number[] } {
  const lng = new Array<number>(258), lat = new Array<number>(258);
  for (let px = -1; px <= 256; px++) lng[px + 1] = pixelLngLat(z, x, y, px, 0)[0];
  for (let py = -1; py <= 256; py++) lat[py + 1] = pixelLngLat(z, x, y, 0, py)[1];
  return { lng, lat };
}

function bilinear(
  values: ArrayLike<number>, width: number, height: number,
  u: number, v: number,
): [number, number, number, number, number, number] {
  const x = u * (width - 1), y = v * (height - 1);
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(width - 1, x0 + 1), y1 = Math.min(height - 1, y0 + 1);
  return [values[y0 * width + x0], values[y0 * width + x1],
    values[y1 * width + x0], values[y1 * width + x1], x - x0, y - y0];
}

function interpolate(parts: [number, number, number, number, number, number]): number {
  const [a, b, c, d, tx, ty] = parts;
  return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}

function sampleGrid(record: RasterTerrainRecord, lng: number, lat: number): number | null {
  const b = record.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const [u, v] = unit(lng, lat, b);
  return interpolate(bilinear(record.sampleHeights, record.sampleGridSize, record.sampleGridSize, u, v));
}

function sampleSurround(record: RasterTerrainRecord, lng: number, lat: number): number | null {
  const surround = record.surround;
  if (!surround) return null;
  const b = surround.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const [u, v] = unit(lng, lat, b);
  const parts = bilinear(surround.heights, surround.width, surround.height, u, v);
  if (parts.slice(0, 4).some((value) => value <= NODATA_FLOOR)) return null;
  return interpolate(parts);
}

function sampleElevation(record: RasterTerrainRecord, lng: number, lat: number): number | null {
  const core = sampleGrid(record, lng, lat);
  if (!record.surround) return core;
  const surrounding = sampleSurround(record, lng, lat);
  if (core == null) return surrounding;
  if (surrounding == null) return core;
  const b = record.bounds;
  const dx = Math.min(lng - b.west, b.east - lng) / ((b.east - b.west) * FEATHER_FRAC);
  const dy = Math.min(lat - b.south, b.north - lat) / ((b.north - b.south) * FEATHER_FRAC);
  const t = Math.max(0, Math.min(1, Math.min(dx, dy)));
  const weight = t * t * (3 - 2 * t);
  return core * weight + surrounding * (1 - weight);
}

function sampleCover(record: RasterTerrainRecord, lng: number, lat: number): CoverClassCode | null {
  const grid = record.coverGrid;
  if (!grid) return null;
  const b = grid.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const [u, v] = unit(lng, lat, b);
  const column = Math.min(grid.width - 1, Math.max(0, Math.floor(u * grid.width)));
  const row = Math.min(grid.height - 1, Math.max(0, Math.floor(v * grid.height)));
  const code = grid.data[row * grid.width + column] as CoverClassCode;
  return code === 255 ? null : code;
}

function analysisColor(kind: 'slope' | 'aspect', slope: number, aspect: number): [number, number, number, number] {
  if (kind === 'slope') {
    if (slope < 6) return [0, 0, 0, 0];
    if (slope < 16) return [67, 160, 71, 150];
    if (slope < 24) return [30, 136, 229, 150];
    if (slope < 37) return [33, 33, 33, 150];
    return [211, 47, 47, 150];
  }
  if (slope < 4) return [0, 0, 0, 0];
  const colors: [number, number, number][] = [[66,133,244],[45,165,190],[46,170,100],[140,190,60],[245,214,60],[240,150,50],[220,70,70],[150,90,200]];
  const color = colors[Math.round(aspect / 45) % 8];
  return [color[0], color[1], color[2], 150];
}

export function renderResortTilePixels(
  record: RasterTerrainRecord,
  kind: ResortTileKind,
  z: number,
  x: number,
  y: number,
): Uint8ClampedArray {
  const axes = tileAxes(z, x, y);
  const output = new Uint8ClampedArray(256 * 256 * 4);
  for (let py = 0; py < 256; py++) for (let px = 0; px < 256; px++) {
    const lng = axes.lng[px + 1], lat = axes.lat[py + 1], index = (py * 256 + px) * 4;
    let rgba: [number, number, number, number];
    if (kind === 'dem') {
      const elevation = sampleElevation(record, lng, lat);
      const encoded = Math.max(0, Math.min(65535.996, (elevation ?? 0) + 32768));
      rgba = [Math.floor(encoded / 256), Math.floor(encoded) % 256,
        Math.floor((encoded - Math.floor(encoded)) * 256), elevation == null ? 0 : 255];
    } else if (kind === 'cover') {
      rgba = COVER_RGBA[sampleCover(record, lng, lat) ?? 255] ?? COVER_RGBA[255];
    } else {
      const ewM = 2 * 156543.03392 * Math.cos((lat * Math.PI) / 180) / (2 ** z);
      const dzdx = ((sampleGrid(record, axes.lng[px + 2], lat) ?? 0) -
        (sampleGrid(record, axes.lng[px], lat) ?? 0)) / ewM;
      const dzdy = ((sampleGrid(record, lng, axes.lat[py + 2]) ?? 0) -
        (sampleGrid(record, lng, axes.lat[py]) ?? 0)) / ewM;
      const slope = Math.atan(Math.hypot(dzdx, dzdy)) * 180 / Math.PI;
      const aspect = (Math.atan2(-dzdx, dzdy) * 180 / Math.PI + 360) % 360;
      rgba = analysisColor(kind, slope, aspect);
    }
    output[index] = rgba[0]; output[index + 1] = rgba[1];
    output[index + 2] = rgba[2]; output[index + 3] = rgba[3];
  }
  return output;
}
