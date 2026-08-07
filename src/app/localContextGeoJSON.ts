import type { TerrainRecord } from '../types';
import { effectiveStreamWidth } from '../streamAnalysis';

/** Build imported local context, including save-only lake names. */
export function localContextGeoJSON(record: TerrainRecord,
  lakeNameOverrides: Record<string, string> = {},
  streamWidthOverrides: Record<string, number> = {}): GeoJSON.FeatureCollection {
  const vectors = record.vectorFeatures;
  const features: GeoJSON.Feature[] = [];
  if (vectors) {
    for (const water of vectors.waterPolygons) {
      features.push({ type: 'Feature', id: water.id, properties: {
        kind: 'water', id: water.id, name: water.name ?? '',
        customName: lakeNameOverrides[water.id] ?? '',
      }, geometry: { type: 'Polygon', coordinates: water.rings } });
    }
    for (const water of vectors.waterLines) {
      const effective = effectiveStreamWidth(water, streamWidthOverrides[water.id]);
      features.push({ type: 'Feature', id: water.id, properties: {
        kind: 'water-line', id: water.id, name: water.name ?? '', class: water.waterClass,
        widthM: effective.widthM, widthSource: effective.source,
      }, geometry: { type: 'LineString', coordinates: water.points } });
    }
    for (const road of vectors.roads) {
      features.push({ type: 'Feature', properties: { kind: 'road', class: road.roadClass }, geometry: { type: 'LineString', coordinates: road.points } });
    }
  }
  return { type: 'FeatureCollection', features };
}
