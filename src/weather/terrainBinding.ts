import type { TerrainRecord } from '../types/terrain';

/** Stable map identity. Elevation edits intentionally do not change this binding. */
export function weatherTerrainBinding(record: Pick<TerrainRecord, 'key' | 'latitude' | 'longitude' | 'areaSizeMeters' | 'bounds'>): string {
  const bounds = record.bounds;
  return [record.key, record.latitude.toFixed(6), record.longitude.toFixed(6), record.areaSizeMeters,
    bounds?.west.toFixed(6) ?? '', bounds?.south.toFixed(6) ?? '', bounds?.east.toFixed(6) ?? '', bounds?.north.toFixed(6) ?? ''].join('|');
}
