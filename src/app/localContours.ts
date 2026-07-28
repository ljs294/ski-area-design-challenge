import type { TerrainRecord } from '../types';
import { unitToLngLat } from '../geo';

export function localContourGeoJSON(
  record: TerrainRecord,
  imperial: boolean
): GeoJSON.FeatureCollection {
  const b = record.bounds!;
  const byLevel = new Map<number, GeoJSON.Position[][]>();
  const data = record.contourSegments ?? [];
  for (let i = 0; i + 4 < data.length; i += 5) {
    const levelM = data[i + 4];
    const level = imperial ? levelM * 3.28084 : levelM;
    const lines = byLevel.get(level) ?? [];
    lines.push([
      unitToLngLat(data[i], data[i + 1], b),
      unitToLngLat(data[i + 2], data[i + 3], b),
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
