import type maplibregl from 'maplibre-gl';
import type { SavedRoad } from '../types';

export const ROAD_DRAFT_SOURCE = 'road-draft';

/** Shared by imported and player-built roads so their cartography is exactly identical. */
export const LOCAL_ROAD_PAINT: maplibregl.LineLayerSpecification['paint'] = {
  'line-color': '#55534e',
  'line-width': ['match', ['get', 'class'], 'major', 1.7, 'minor', 1, 0.55],
  'line-opacity': 0.72,
};

export interface RoadDraftLine {
  points: [number, number][];
  cursor: [number, number] | null;
}

export function playerRoadFeatures(roads: SavedRoad[]): GeoJSON.Feature[] {
  return roads.map((road) => ({
    type: 'Feature', properties: { kind: 'road', class: 'minor',
      playerBuilt: true, roadId: road.id, name: road.name },
    geometry: { type: 'LineString', coordinates: road.points },
  }));
}

export function roadDraftGeoJSON(draft: RoadDraftLine | null): GeoJSON.FeatureCollection {
  if (!draft || draft.points.length === 0) return { type: 'FeatureCollection', features: [] };
  const coordinates = [...draft.points];
  if (draft.cursor) coordinates.push(draft.cursor);
  const features: GeoJSON.Feature[] = draft.points.map((point, index) => ({
    type: 'Feature', properties: { kind: 'vertex', index }, geometry: { type: 'Point', coordinates: point },
  }));
  if (coordinates.length >= 2) features.unshift({
    type: 'Feature', properties: { kind: 'road', class: 'minor' },
    geometry: { type: 'LineString', coordinates },
  });
  return { type: 'FeatureCollection', features };
}

export function addRoadDraftLayers(map: maplibregl.Map): void {
  if (map.getSource(ROAD_DRAFT_SOURCE)) return;
  map.addSource(ROAD_DRAFT_SOURCE, { type: 'geojson', data: roadDraftGeoJSON(null) });
  map.addLayer({
    id: 'road-draft-line', type: 'line', source: ROAD_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'road'], layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { ...LOCAL_ROAD_PAINT, 'line-opacity': 0.95 },
  });
  map.addLayer({
    id: 'road-draft-vertices', type: 'circle', source: ROAD_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'vertex'],
    paint: { 'circle-radius': 3.5, 'circle-color': '#55534e',
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1.5 },
  });
}

export function setRoadDraftData(map: maplibregl.Map, draft: RoadDraftLine | null): void {
  (map.getSource(ROAD_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(roadDraftGeoJSON(draft));
}
