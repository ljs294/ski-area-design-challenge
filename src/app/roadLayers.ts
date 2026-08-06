import type maplibregl from 'maplibre-gl';
import type { SavedRoad } from '../types';

export const ROAD_DRAFT_SOURCE = 'road-draft';
export const ROAD_BUILT_SOURCE = 'player-roads';
export const ROAD_BUILT_LAYER_IDS = ['player-roads'] as const;

/** Shared by imported and player-built roads so their cartography is exactly identical. */
export const LOCAL_ROAD_PAINT: maplibregl.LineLayerSpecification['paint'] = {
  'line-color': '#55534e',
  'line-width': ['match', ['get', 'class'], 'major', 1.7, 'minor', 1, 0.55],
  'line-opacity': 0.72,
};

export interface RoadDraftLine {
  points: [number, number][];
  cursor: [number, number] | null;
  gradingPolygons?: [number, number][][][];
  infeasibleLines?: [number, number][][];
}

export function playerRoadFeatures(roads: SavedRoad[]): GeoJSON.Feature[] {
  return roads.map((road) => ({
    type: 'Feature', properties: { kind: 'road', class: 'minor',
      playerBuilt: true, roadId: road.id, name: road.name },
    geometry: { type: 'LineString', coordinates: road.points },
  }));
}

export function playerRoadGeoJSON(roads: SavedRoad[]): GeoJSON.FeatureCollection {
  return { type: 'FeatureCollection', features: playerRoadFeatures(roads) };
}

export function roadDraftGeoJSON(draft: RoadDraftLine | null): GeoJSON.FeatureCollection {
  if (!draft || draft.points.length === 0) return { type: 'FeatureCollection', features: [] };
  const coordinates = [...draft.points];
  if (draft.cursor) coordinates.push(draft.cursor);
  const features: GeoJSON.Feature[] = draft.points.map((point, index) => ({
    type: 'Feature', properties: { kind: 'vertex', index }, geometry: { type: 'Point', coordinates: point },
  }));
  for (const polygon of draft.gradingPolygons ?? []) features.push({
    type: 'Feature', properties: { kind: 'grade' },
    geometry: { type: 'Polygon', coordinates: polygon },
  });
  for (const line of draft.infeasibleLines ?? []) features.push({
    type: 'Feature', properties: { kind: 'infeasible' },
    geometry: { type: 'LineString', coordinates: line },
  });
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
    id: 'road-draft-grade-fill', type: 'fill', source: ROAD_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'grade'],
    paint: { 'fill-color': '#8b7a62', 'fill-opacity': 0.16 },
  });
  map.addLayer({
    id: 'road-draft-grade-outline', type: 'line', source: ROAD_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'grade'],
    paint: { 'line-color': '#6b5e4c', 'line-opacity': 0.8,
      'line-width': 1.5, 'line-dasharray': [2, 1.5] },
  });
  map.addLayer({
    id: 'road-draft-infeasible', type: 'line', source: ROAD_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'infeasible'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#dc2626', 'line-opacity': 0.95,
      'line-width': 5, 'line-dasharray': [1.5, 1] },
  });
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

/** Player-built roads are separate from imported OSM context so the road
 * contribution owns their source, data, order, visibility, and cleanup. */
export function addRoadLayers(map: maplibregl.Map): void {
  if (map.getSource(ROAD_BUILT_SOURCE)) return;
  map.addSource(ROAD_BUILT_SOURCE, { type: 'geojson', data: playerRoadGeoJSON([]) });
  map.addLayer({
    id: ROAD_BUILT_LAYER_IDS[0], type: 'line', source: ROAD_BUILT_SOURCE,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: LOCAL_ROAD_PAINT,
  });
}

export function setRoadData(map: maplibregl.Map, roads: SavedRoad[]): void {
  (map.getSource(ROAD_BUILT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(playerRoadGeoJSON(roads));
}

export function setRoadDraftData(map: maplibregl.Map, draft: RoadDraftLine | null): void {
  (map.getSource(ROAD_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(roadDraftGeoJSON(draft));
}
