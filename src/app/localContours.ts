import type { LatLonBounds } from '../types/geo';
import type { TerrainRecord } from '../types';
import { unitToLngLat } from '../geo';

/** Group a flat `[x1, y1, x2, y2, levelM]` segment stream into one feature per
 * elevation, in the caller's display units. */
export function contourGeoJSON(
  segments: ArrayLike<number>,
  bounds: LatLonBounds,
  imperial: boolean
): GeoJSON.FeatureCollection {
  const byLevel = new Map<number, GeoJSON.Position[][]>();
  for (let i = 0; i + 4 < segments.length; i += 5) {
    const levelM = segments[i + 4];
    const level = imperial ? levelM * 3.28084 : levelM;
    const lines = byLevel.get(level) ?? [];
    lines.push([
      unitToLngLat(segments[i], segments[i + 1], bounds),
      unitToLngLat(segments[i + 2], segments[i + 3], bounds),
    ]);
    byLevel.set(level, lines);
  }
  return {
    type: 'FeatureCollection',
    features: [...byLevel.entries()].map(([ele, coordinates]) => ({
      type: 'Feature',
      properties: {
        ele,
        level: Math.round(ele / (imperial ? 20 : 6.096)) % 5 === 0 ? 1 : 0,
      },
      geometry: { type: 'MultiLineString', coordinates },
    })),
  };
}

export function localContourGeoJSON(
  record: TerrainRecord,
  imperial: boolean
): GeoJSON.FeatureCollection {
  return contourGeoJSON(record.contourSegments ?? [], record.bounds!, imperial);
}
