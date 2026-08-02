import type maplibregl from 'maplibre-gl';
import type { SavedDam, TerrainRecord } from '../types';
import { pondRelativeRenderHeightM, smoothPondRings } from '../pondGeometry';

const DAM_SOURCE = 'player-dams';
const DAM_DRAFT_SOURCE = 'dam-draft';
export const DAM_HIT_LAYERS = ['dam-hit', 'dam-pond-hit'];
export const DAM_BUILT_LAYER_IDS = ['dam-embankment-fill', 'dam-embankment-outline',
  'dam-crest-fill', 'dam-crest-outline', 'dam-pond-fill', 'dam-pond-outline',
  'dam-crest-casing', 'dam-crest', 'dam-selected', ...DAM_HIT_LAYERS];

export interface DamDraftMapData {
  points: [number, number][];
  cursor: [number, number] | null;
  pondRings?: [number, number][][];
  /** Graded embankment toe and deck, once the alignment has been designed. */
  footprintRings?: [number, number][][];
  crestRing?: [number, number][];
  crestElevationM?: number;
  averageDepthM?: number;
}

/** Plan-view earthwork for a designed dam: the toe the grading reaches and the
 * deck on top. Dams saved before the game graded terrain have neither, and keep
 * the old crest-line drawing. */
function embankmentFeatures(dam: { footprintRings?: [number, number][][];
  crestRing?: [number, number][] }, properties: Record<string, unknown>): GeoJSON.Feature[] {
  const features: GeoJSON.Feature[] = [];
  if (dam.footprintRings?.length) features.push({ type: 'Feature',
    properties: { ...properties, kind: 'embankment' },
    geometry: { type: 'Polygon', coordinates: dam.footprintRings } });
  if (dam.crestRing && dam.crestRing.length >= 4) features.push({ type: 'Feature',
    properties: { ...properties, kind: 'crest' },
    geometry: { type: 'Polygon', coordinates: [dam.crestRing] } });
  return features;
}

function damFeatures(dams: SavedDam[], terrain?: TerrainRecord | null): GeoJSON.Feature[] {
  return dams.flatMap((dam) => [
    ...embankmentFeatures(dam, { id: dam.id, name: dam.name }),
    { type: 'Feature' as const, properties: { kind: 'pond', id: dam.id, name: dam.name,
      renderHeightM: pondRelativeRenderHeightM(dam.pondRings[0], dam.crestElevationM,
        dam.averageDepthM, terrain) },
      geometry: { type: 'Polygon' as const, coordinates: smoothPondRings(dam.pondRings) } },
    { type: 'Feature' as const, properties: { kind: 'dam', id: dam.id, name: dam.name,
      graded: dam.crestRing && dam.crestRing.length >= 4 ? 1 : 0 },
      geometry: { type: 'LineString' as const, coordinates: dam.points } },
  ]);
}

export function damsToGeoJSON(dams: SavedDam[], terrain?: TerrainRecord | null): GeoJSON.FeatureCollection {
  return { type: 'FeatureCollection', features: damFeatures(dams, terrain) };
}

export function damDraftGeoJSON(draft: DamDraftMapData | null,
  terrain?: TerrainRecord | null): GeoJSON.FeatureCollection {
  if (!draft) return { type: 'FeatureCollection', features: [] };
  const features: GeoJSON.Feature[] = [...embankmentFeatures(draft, {})];
  if (draft.pondRings?.length) features.push({ type: 'Feature', properties: { kind: 'pond',
    renderHeightM: pondRelativeRenderHeightM(draft.pondRings[0], draft.crestElevationM ?? 0,
      draft.averageDepthM ?? 0.01, terrain) },
    geometry: { type: 'Polygon', coordinates: smoothPondRings(draft.pondRings) } });
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
  // Earthwork first: the shaped ground reads under the water it holds back.
  map.addLayer({ id: 'dam-embankment-fill', type: 'fill', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'embankment'],
    paint: { 'fill-color': '#8b7355', 'fill-opacity': 0.45 } });
  map.addLayer({ id: 'dam-embankment-outline', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'embankment'],
    paint: { 'line-color': '#5f4a33', 'line-width': 1.2, 'line-opacity': 0.85 } });
  map.addLayer({ id: 'dam-crest-fill', type: 'fill', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'crest'],
    paint: { 'fill-color': '#c3b394', 'fill-opacity': 0.95 } });
  map.addLayer({ id: 'dam-crest-outline', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'crest'],
    paint: { 'line-color': '#6b563d', 'line-width': 1.4 } });
  map.addLayer({ id: 'dam-pond-fill', type: 'fill-extrusion', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'fill-extrusion-color': '#69a9c5',
      'fill-extrusion-opacity': 0.62, 'fill-extrusion-height': ['get', 'renderHeightM'],
      'fill-extrusion-base': ['max', 0.001, ['-', ['get', 'renderHeightM'], 0.05]],
      'fill-extrusion-vertical-gradient': false } });
  map.addLayer({ id: 'dam-pond-outline', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'line-color': '#397f9f', 'line-width': 1.5 } });
  // Dams built before the embankment was graded have no deck polygon to draw,
  // so they keep the original crest line.
  map.addLayer({ id: 'dam-crest-casing', type: 'line', source: DAM_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'dam'], ['!=', ['get', 'graded'], 1]],
    layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#f5f0e4', 'line-width': 7 } });
  map.addLayer({ id: 'dam-crest', type: 'line', source: DAM_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'dam'], ['!=', ['get', 'graded'], 1]],
    layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#72553b', 'line-width': 4 } });
  map.addLayer({ id: 'dam-selected', type: 'line', source: DAM_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'dam'], ['==', ['get', 'id'], '']],
    paint: { 'line-color': '#fff6c7', 'line-width': 8, 'line-opacity': 0.9 } });
  map.addLayer({ id: 'dam-hit', type: 'line', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'dam'], paint: { 'line-color': 'rgba(0,0,0,0)', 'line-width': 18 } });
  map.addLayer({ id: 'dam-pond-hit', type: 'fill', source: DAM_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'fill-color': 'rgba(0,0,0,0)', 'fill-opacity': 0.01 } });
  map.addLayer({ id: 'dam-preview-embankment', type: 'fill', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'embankment'],
    paint: { 'fill-color': '#c69a53', 'fill-opacity': 0.28 } });
  map.addLayer({ id: 'dam-preview-embankment-outline', type: 'line', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'embankment'], paint: { 'line-color': '#f0b44d',
      'line-width': 1.6, 'line-dasharray': [3, 2] } });
  map.addLayer({ id: 'dam-preview-crest', type: 'fill', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'crest'],
    paint: { 'fill-color': '#f0d9a8', 'fill-opacity': 0.6 } });
  map.addLayer({ id: 'dam-preview-fill', type: 'fill-extrusion', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'fill-extrusion-color': '#60b7d6',
      'fill-extrusion-opacity': 0.3, 'fill-extrusion-height': ['get', 'renderHeightM'],
      'fill-extrusion-base': ['max', 0.001, ['-', ['get', 'renderHeightM'], 0.05]],
      'fill-extrusion-vertical-gradient': false } });
  map.addLayer({ id: 'dam-preview-outline', type: 'line', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'line-color': '#d7f5ff', 'line-width': 2.2,
      'line-dasharray': [2, 2] } });
  map.addLayer({ id: 'dam-preview-line', type: 'line', source: DAM_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'dam'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#f0b44d', 'line-width': 4, 'line-dasharray': [2, 1.5] } });
  map.addLayer({ id: 'dam-preview-points', type: 'circle', source: DAM_DRAFT_SOURCE,
    filter: ['in', ['get', 'kind'], ['literal', ['endpoint', 'snap']]], paint: {
      'circle-radius': 5, 'circle-color': ['match', ['get', 'kind'], 'snap', '#d7f5ff', '#f0b44d'],
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1.5 } });
}

export function setDamData(map: maplibregl.Map, dams: SavedDam[], terrain?: TerrainRecord | null): void {
  (map.getSource(DAM_SOURCE) as maplibregl.GeoJSONSource | undefined)?.setData(damsToGeoJSON(dams, terrain));
}

export function setDamDraftData(map: maplibregl.Map, draft: DamDraftMapData | null,
  terrain?: TerrainRecord | null): void {
  (map.getSource(DAM_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(damDraftGeoJSON(draft, terrain));
}

export function setSelectedDam(map: maplibregl.Map | null, id: string | null): void {
  if (map?.getLayer('dam-selected')) map.setFilter('dam-selected', [
    'all', ['==', ['get', 'kind'], 'dam'], ['==', ['get', 'id'], id ?? ''],
  ]);
}
