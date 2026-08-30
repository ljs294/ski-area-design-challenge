import type maplibregl from 'maplibre-gl';
import type { SavedRoad } from '../types';
import type { RoadFeature } from '../types/vectorFeatures';
import { roadSurfacePolygons } from '../roads';
import { analyzeBuiltRoad, analyzeImportedRoad, roadMarkingLines,
  type RoadAnalysis } from '../roadAnalysis';

export const ROAD_DRAFT_SOURCE = 'road-draft';
export const ROAD_SOURCE = 'roads';
export const ROAD_LAYER_IDS = [
  'road-pavement', 'road-selected', 'road-yellow-centerline', 'road-white-divider', 'road-hit',
] as const;
export const ROAD_HIT_LAYER_IDS = ['road-hit', 'road-pavement'] as const;
export const ROAD_PAVEMENT_PAINT: maplibregl.FillLayerSpecification['paint'] = {
  'fill-color': '#55534e', 'fill-opacity': 1,
};
export const ROAD_SELECTED_PAINT: maplibregl.FillLayerSpecification['paint'] = {
  'fill-color': '#38bdf8', 'fill-opacity': 1,
};
export const ROAD_CENTER_MARKING_PAINT: maplibregl.LineLayerSpecification['paint'] = {
  'line-color': '#e6c65c', 'line-width': 1,
  'line-opacity': 0.88, 'line-dasharray': [4, 4],
};
export const ROAD_DIVIDER_PAINT: maplibregl.LineLayerSpecification['paint'] = {
  'line-color': '#f8fafc', 'line-width': 1,
  'line-opacity': 0.82, 'line-dasharray': [4, 4],
};

export interface RoadDraftLine {
  points: [number, number][];
  cursor: [number, number] | null;
  widthM: number;
  gradingPolygons?: [number, number][][][];
  infeasibleLines?: [number, number][][];
}

function renderedRoadFeatures(road: RoadAnalysis): GeoJSON.Feature[] {
  const features: GeoJSON.Feature[] = [];
  const properties = {
    id: road.key, roadId: road.id, source: road.source, name: road.name,
    widthM: road.widthM, widthSource: road.widthSource, totalLanes: road.totalLanes,
  };
  for (const polygon of roadSurfacePolygons(road.points, road.widthM)) features.push({
    type: 'Feature', properties: { ...properties, kind: 'road-surface' },
    geometry: { type: 'Polygon', coordinates: polygon },
  });
  features.push({
    type: 'Feature', properties: { ...properties, kind: 'road-spine' },
    geometry: { type: 'LineString', coordinates: road.points },
  });
  for (const marking of roadMarkingLines(road)) {
    features.push({
      type: 'Feature', properties: { ...properties,
        kind: marking.kind === 'center' ? 'road-center-marking' : 'road-lane-divider' },
      geometry: { type: 'LineString', coordinates: marking.points },
    });
  }
  return features;
}

export function roadFeatures(imported: readonly RoadFeature[], built: readonly SavedRoad[]): GeoJSON.Feature[] {
  const roads = [
    ...imported.map(analyzeImportedRoad).filter((road): road is RoadAnalysis => road !== null),
    ...built.map(analyzeBuiltRoad),
  ];
  return roads.flatMap(renderedRoadFeatures);
}

export function roadGeoJSON(
  imported: readonly RoadFeature[],
  built: readonly SavedRoad[],
): GeoJSON.FeatureCollection {
  return { type: 'FeatureCollection', features: roadFeatures(imported, built) };
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
  if (coordinates.length >= 2) {
    for (const polygon of roadSurfacePolygons(coordinates, draft.widthM)) features.unshift({
      type: 'Feature', properties: { kind: 'road-surface', widthM: draft.widthM },
      geometry: { type: 'Polygon', coordinates: polygon },
    });
  }
  if (coordinates.length >= 2) features.unshift({
    type: 'Feature', properties: { kind: 'road-centerline', class: 'minor', widthM: draft.widthM },
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
    id: 'road-draft-surface', type: 'fill', source: ROAD_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'road-surface'],
    paint: ROAD_PAVEMENT_PAINT,
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
    filter: ['==', ['get', 'kind'], 'road-centerline'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#f4d35e', 'line-width': 1,
      'line-opacity': 0.95, 'line-dasharray': [4, 4] },
  });
  map.addLayer({
    id: 'road-draft-vertices', type: 'circle', source: ROAD_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'vertex'],
    paint: { 'circle-radius': 3.5, 'circle-color': '#55534e',
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1.5 },
  });
}

export function addRoadLayers(map: maplibregl.Map): void {
  if (map.getSource(ROAD_SOURCE)) return;
  map.addSource(ROAD_SOURCE, { type: 'geojson', data: roadGeoJSON([], []) });
  map.addLayer({
    id: ROAD_LAYER_IDS[0], type: 'fill', source: ROAD_SOURCE,
    filter: ['==', ['get', 'kind'], 'road-surface'],
    paint: ROAD_PAVEMENT_PAINT,
  });
  map.addLayer({
    id: ROAD_LAYER_IDS[1], type: 'fill', source: ROAD_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'road-surface'], ['==', ['get', 'id'], '']],
    paint: ROAD_SELECTED_PAINT,
  });
  map.addLayer({
    id: ROAD_LAYER_IDS[2], type: 'line', source: ROAD_SOURCE,
    filter: ['==', ['get', 'kind'], 'road-center-marking'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: ROAD_CENTER_MARKING_PAINT,
  });
  map.addLayer({
    id: ROAD_LAYER_IDS[3], type: 'line', source: ROAD_SOURCE,
    filter: ['==', ['get', 'kind'], 'road-lane-divider'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: ROAD_DIVIDER_PAINT,
  });
  map.addLayer({
    id: ROAD_LAYER_IDS[4], type: 'line', source: ROAD_SOURCE,
    filter: ['==', ['get', 'kind'], 'road-spine'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': 'rgba(0,0,0,0)', 'line-width': 18 },
  });
}

export function setRoadData(
  map: maplibregl.Map,
  imported: readonly RoadFeature[],
  built: readonly SavedRoad[],
): void {
  (map.getSource(ROAD_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(roadGeoJSON(imported, built));
}

export function setSelectedRoad(map: maplibregl.Map | null, roadKey: string | null): void {
  if (!map?.getLayer(ROAD_LAYER_IDS[1])) return;
  map.setFilter(ROAD_LAYER_IDS[1], [
    'all', ['==', ['get', 'kind'], 'road-surface'], ['==', ['get', 'id'], roadKey ?? ''],
  ]);
}

export function setRoadDraftData(map: maplibregl.Map, draft: RoadDraftLine | null): void {
  (map.getSource(ROAD_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(roadDraftGeoJSON(draft));
}
