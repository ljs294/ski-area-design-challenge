import type { CoverClassCode } from '../../src/types/cover';
import type { LatLonBounds } from '../../src/types/geo';
import type { TerrainRecord } from '../../src/types/terrain';

const METERS_PER_DEGREE = 111_320;
const NODATA_FLOOR = -1_000;
const FEATHER_FRAC = 0.08;

export interface LocalTerrainFrame {
  readonly bounds: LatLonBounds;
  readonly widthM: number;
  readonly depthM: number;
  readonly centerElevationM: number;
  toLocal(lng: number, lat: number, elevationM?: number): [number, number, number];
  toLngLat(x: number, z: number): [number, number];
}

function displayBounds(record: TerrainRecord): LatLonBounds {
  if (record.surround) return record.surround.bounds;
  if (record.bounds) return record.bounds;
  const halfLat = record.areaSizeMeters / METERS_PER_DEGREE / 2;
  const halfLng = halfLat / Math.max(0.05, Math.cos(record.latitude * Math.PI / 180));
  return { west: record.longitude - halfLng, east: record.longitude + halfLng,
    south: record.latitude - halfLat, north: record.latitude + halfLat };
}

function bilinear(values: ArrayLike<number>, width: number, height: number,
  bounds: LatLonBounds, lng: number, lat: number, rejectNodata: boolean): number | null {
  if (lng < bounds.west || lng > bounds.east || lat < bounds.south || lat > bounds.north) return null;
  const x = (lng - bounds.west) / (bounds.east - bounds.west) * (width - 1);
  const y = (bounds.north - lat) / (bounds.north - bounds.south) * (height - 1);
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(width - 1, x0 + 1), y1 = Math.min(height - 1, y0 + 1);
  const a = values[y0 * width + x0]!, b = values[y0 * width + x1]!;
  const c = values[y1 * width + x0]!, d = values[y1 * width + x1]!;
  if (rejectNodata && Math.min(a, b, c, d) <= NODATA_FLOOR) return null;
  const tx = x - x0, ty = y - y0;
  return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}

export function sampleTerrainElevation(record: TerrainRecord, lng: number, lat: number): number | null {
  const coreBounds = record.bounds;
  const core = coreBounds
    ? bilinear(record.sampleHeights, record.sampleGridSize, record.sampleGridSize, coreBounds, lng, lat, false)
    : null;
  const surround = record.surround;
  if (!surround) return core;
  const outer = bilinear(surround.heights, surround.width, surround.height, surround.bounds, lng, lat, true);
  if (core == null) return outer;
  if (outer == null || !coreBounds) return core;
  const dx = Math.min(lng - coreBounds.west, coreBounds.east - lng)
    / ((coreBounds.east - coreBounds.west) * FEATHER_FRAC);
  const dy = Math.min(lat - coreBounds.south, coreBounds.north - lat)
    / ((coreBounds.north - coreBounds.south) * FEATHER_FRAC);
  const t = Math.max(0, Math.min(1, Math.min(dx, dy)));
  const weight = t * t * (3 - 2 * t);
  return core * weight + outer * (1 - weight);
}

export function sampleTerrainCover(record: TerrainRecord, lng: number, lat: number): CoverClassCode | null {
  const grid = record.coverGrid;
  if (!grid || lng < grid.bounds.west || lng > grid.bounds.east
    || lat < grid.bounds.south || lat > grid.bounds.north) return null;
  const column = Math.min(grid.width - 1, Math.max(0,
    Math.floor((lng - grid.bounds.west) / (grid.bounds.east - grid.bounds.west) * grid.width)));
  const row = Math.min(grid.height - 1, Math.max(0,
    Math.floor((grid.bounds.north - lat) / (grid.bounds.north - grid.bounds.south) * grid.height)));
  const code = grid.data[row * grid.width + column] as CoverClassCode;
  return code === 255 ? null : code;
}

export function createLocalTerrainFrame(record: TerrainRecord): LocalTerrainFrame {
  const bounds = displayBounds(record);
  const centerLat = (bounds.north + bounds.south) / 2;
  const metersPerLng = METERS_PER_DEGREE * Math.cos(centerLat * Math.PI / 180);
  const widthM = (bounds.east - bounds.west) * metersPerLng;
  const depthM = (bounds.north - bounds.south) * METERS_PER_DEGREE;
  const centerElevationM = sampleTerrainElevation(record, record.longitude, record.latitude) ?? 0;
  return { bounds, widthM, depthM, centerElevationM,
    toLocal(lng, lat, elevationM = centerElevationM) {
      return [(lng - bounds.west) * metersPerLng - widthM / 2,
        elevationM - centerElevationM,
        (bounds.north - lat) * METERS_PER_DEGREE - depthM / 2];
    },
    toLngLat(x, z) {
      return [bounds.west + (x + widthM / 2) / metersPerLng,
        bounds.north - (z + depthM / 2) / METERS_PER_DEGREE];
    } };
}
