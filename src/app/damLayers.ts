import type maplibregl from 'maplibre-gl';
import type { SavedDam } from '../types';

const DAM_SOURCE = 'player-dams';
const DAM_DRAFT_SOURCE = 'dam-draft';
export const DAM_HIT_LAYERS = ['dam-hit', 'dam-pond-hit'];
export const DAM_BUILT_LAYER_IDS = ['dam-pond-fill', 'dam-pond-outline', 'dam-crest-casing',
  'dam-crest', 'dam-selected', ...DAM_HIT_LAYERS];

export interface DamDraftMapData {
  points: [number, number][];
  cursor: [number, number] | null;
  pondRings?: [number, number][][];
}

function damFeatures(dams: SavedDam[]): GeoJSON.Feature[] {
  return dams.flatMap((dam) => [
    { type: 'Feature' as const, properties: { kind: 'pond', id: dam.id, name: dam.name },
      geometry: { type: 'Polygon' as const, coordinates: dam.pondRings } },
    { type: 'Feature' as const, properties: { kind: 'dam', id: dam.id, name: dam.name },
      geometry: { type: 'LineString' as const, coordinates: dam.points } },
  ]);
}

export function damsToGeoJSON(dams: SavedDam[]): GeoJSON.FeatureCollection {
  return { type: 'FeatureCollection', features: damFeatures(dams) };
}

export function damDraftGeoJSON(draft: DamDraftMapData | null): GeoJSON.FeatureCollection {
  if (!draft) return { type: 'FeatureCollection', features: [] };
  const features: GeoJSON.Feature[] = [];
  if (draft.pondRings?.length) features.push({ type: 'Feature', properties: { kind: 'pond' },
    geometry: { type: 'Polygon', coordinates: draft.pondRings } });
  const line = [...draft.points];
  if (draft.cursor) line.push(draft.cursor);
  if (line.length >= 2) features.push({ type: 'Feature', properties: { kind: 'dam' },
    geometry: { type: 'LineString', coordinates: line } });
  for (const point of draft.points) features.push({ type: 'Feature', properties: { kind: 'endpoint' },
    geometry: { type: 'Point', coordinates: point } });
  if (draft.cursor) features.push({ type: 'Feature', properties: { kind: 'snap' },
    geometry: { type: 'Point', coordinates: draft.cursor } });
  return { type: 'FeatureCollection', features };
}

export function addDamLayers(map: maplibregl.Map): void {
  if (!map.getSource(DAM_SOURCE)) map.addSource(DAM_SOURCE, { type: 'geojson', data: damsToGeoJSON([]) });
  if (!map.getSource(DAM_DRAFT_SOURCE)) map.addSource(DAM_DRAFT_SOURCE,
    { type: 'geojson', data: damDraftGeoJSON(null) });
  map.addLayer({ id: 'dam-pond-fill', type: 'fill', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'fill-color': '#69a9c5', 'fill-opacity': 0.62 } });
  map.addLayer({ id: 'dam-pond-outline', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'line-color': '#397f9f', 'line-width': 1.5 } });
  map.addLayer({ id: 'dam-crest-casing', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'dam'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#f5f0e4', 'line-width': 7 } });
  map.addLayer({ id: 'dam-crest', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'dam'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#72553b', 'line-width': 4 } });
  map.addLayer({ id: 'dam-selected', type: 'line', source: DAM_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'dam'], ['==', ['get', 'id'], '']],
    paint: { 'line-color': '#fff6c7', 'line-width': 8, 'line-opacity': 0.9 } });
  map.addLayer({ id: 'dam-hit', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'dam'], paint: { 'line-color': 'rgba(0,0,0,0)', 'line-width': 18 } });
  map.addLayer({ id: 'dam-pond-hit', type: 'fill', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'fill-color': 'rgba(0,0,0,0)', 'fill-opacity': 0.01 } });
  map.addLayer({ id: 'dam-preview-fill', type: 'fill', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'fill-color': '#60b7d6', 'fill-opacity': 0.3 } });
  map.addLayer({ id: 'dam-preview-outline', type: 'line', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'line-color': '#d7f5ff', 'line-width': 2.2,
      'line-dasharray': [2, 2] } });
  map.addLayer({ id: 'dam-preview-crest', type: 'line', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'dam'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#f0b44d', 'line-width': 4, 'line-dasharray': [2, 1.5] } });
  map.addLayer({ id: 'dam-preview-points', type: 'circle', source: DAM_DRAFT_SOURCE,
    filter: ['in', ['get', 'kind'], ['literal', ['endpoint', 'snap']]], paint: {
      'circle-radius': 5, 'circle-color': ['match', ['get', 'kind'], 'snap', '#d7f5ff', '#f0b44d'],
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1.5 } });
}

export function setDamData(map: maplibregl.Map, dams: SavedDam[]): void {
  (map.getSource(DAM_SOURCE) as maplibregl.GeoJSONSource | undefined)?.setData(damsToGeoJSON(dams));
}

export function setDamDraftData(map: maplibregl.Map, draft: DamDraftMapData | null): void {
  (map.getSource(DAM_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)?.setData(damDraftGeoJSON(draft));
}

export function setSelectedDam(map: maplibregl.Map | null, id: string | null): void {
  if (map?.getLayer('dam-selected')) map.setFilter('dam-selected', [
    'all', ['==', ['get', 'kind'], 'dam'], ['==', ['get', 'id'], id ?? ''],
  ]);
}
