import type { SavedRoad, TerrainRecord } from '../types';
import { playerRoadFeatures } from './roadLayers';

/** Build the shared local OSM/player context, including save-only lake names. */
export function localContextGeoJSON(record: TerrainRecord, playerRoads: SavedRoad[] = [],
  lakeNameOverrides: Record<string, string> = {}): GeoJSON.FeatureCollection {
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
      features.push({ type: 'Feature', properties: { kind: 'water-line', class: water.waterClass }, geometry: { type: 'LineString', coordinates: water.points } });
    }
    for (const road of vectors.roads) {
      features.push({ type: 'Feature', properties: { kind: 'road', class: road.roadClass }, geometry: { type: 'LineString', coordinates: road.points } });
    }
  }
  features.push(...playerRoadFeatures(playerRoads));
  return { type: 'FeatureCollection', features };
}
