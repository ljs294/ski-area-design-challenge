import type maplibregl from 'maplibre-gl';
import type { SavedPond, TerrainRecord } from '../types';
import { pondRelativeRenderHeightM, smoothPondRing } from '../pondGeometry';

const POND_SOURCE = 'player-standalone-ponds';
const POND_DRAFT_SOURCE = 'standalone-pond-draft';
export const POND_HIT_LAYERS = ['standalone-pond-hit'];
export const POND_BUILT_LAYER_IDS = ['standalone-pond-fill', 'standalone-pond-outline',
  'standalone-pond-selected', ...POND_HIT_LAYERS];

export interface PondDraftMapData {
  points: [number, number][];
  cursor: [number, number] | null;
  closed: boolean;
  topElevationM?: number;
  averageDepthM?: number;
}

export function pondsToGeoJSON(ponds: SavedPond[], terrain?: TerrainRecord | null): GeoJSON.FeatureCollection {
  return { type: 'FeatureCollection', features: ponds.map((pond) => ({
    type: 'Feature', properties: { id: pond.id, name: pond.name,
      renderHeightM: pondRelativeRenderHeightM(pond.boundary, pond.topElevationM,
        pond.averageDepthM, terrain) },
    geometry: { type: 'Polygon', coordinates: [smoothPondRing(pond.boundary)] },
  })) };
}

export function pondDraftGeoJSON(draft: PondDraftMapData | null,
  terrain?: TerrainRecord | null): GeoJSON.FeatureCollection {
  if (!draft) return { type: 'FeatureCollection', features: [] };
  const coordinates = [...draft.points];
  if (draft.cursor) coordinates.push(draft.cursor);
  if (draft.closed && coordinates.length >= 3) coordinates.push(coordinates[0]);
  const features: GeoJSON.Feature[] = [];
  if (draft.closed && coordinates.length >= 4) features.push({ type: 'Feature', properties: { kind: 'pond',
    renderHeightM: pondRelativeRenderHeightM(coordinates, draft.topElevationM ?? 0,
      draft.averageDepthM ?? 0.01, terrain) },
    geometry: { type: 'Polygon', coordinates: [smoothPondRing(coordinates)] } });
  if (!draft.closed && coordinates.length >= 2) features.push({ type: 'Feature', properties: { kind: 'line' },
    geometry: { type: 'LineString', coordinates } });
  for (const point of draft.points) features.push({ type: 'Feature', properties: { kind: 'point' },
    geometry: { type: 'Point', coordinates: point } });
  return { type: 'FeatureCollection', features };
}

export function addPondLayers(map: maplibregl.Map): void {
  if (!map.getSource(POND_SOURCE)) map.addSource(POND_SOURCE, { type: 'geojson', data: pondsToGeoJSON([]) });
  if (!map.getSource(POND_DRAFT_SOURCE)) map.addSource(POND_DRAFT_SOURCE,
    { type: 'geojson', data: pondDraftGeoJSON(null) });
  map.addLayer({ id: 'standalone-pond-fill', type: 'fill-extrusion', source: POND_SOURCE,
    paint: { 'fill-extrusion-color': '#69a9c5', 'fill-extrusion-opacity': 0.62,
      'fill-extrusion-height': ['get', 'renderHeightM'],
      'fill-extrusion-base': ['max', 0.001, ['-', ['get', 'renderHeightM'], 0.05]],
      'fill-extrusion-vertical-gradient': false } });
  map.addLayer({ id: 'standalone-pond-outline', type: 'line', source: POND_SOURCE,
    paint: { 'line-color': '#397f9f', 'line-width': 1.5 } });
  map.addLayer({ id: 'standalone-pond-selected', type: 'line', source: POND_SOURCE,
    filter: ['==', ['get', 'id'], ''], paint: { 'line-color': '#fff6c7', 'line-width': 4 } });
  map.addLayer({ id: 'standalone-pond-hit', type: 'fill', source: POND_SOURCE,
    paint: { 'fill-color': 'rgba(0,0,0,0)', 'fill-opacity': 0.01 } });
  map.addLayer({ id: 'standalone-pond-preview-fill', type: 'fill-extrusion', source: POND_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'pond'], paint: { 'fill-extrusion-color': '#60b7d6',
      'fill-extrusion-opacity': 0.3, 'fill-extrusion-height': ['get', 'renderHeightM'],
      'fill-extrusion-base': ['max', 0.001, ['-', ['get', 'renderHeightM'], 0.05]],
      'fill-extrusion-vertical-gradient': false } });
  map.addLayer({ id: 'standalone-pond-preview-outline', type: 'line', source: POND_DRAFT_SOURCE,
    filter: ['in', ['get', 'kind'], ['literal', ['pond', 'line']]],
    paint: { 'line-color': '#d7f5ff', 'line-width': 2.2, 'line-dasharray': [2, 2] } });
  map.addLayer({ id: 'standalone-pond-preview-points', type: 'circle', source: POND_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'point'], paint: { 'circle-radius': 4, 'circle-color': '#f0b44d',
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 1.5 } });
}

export function setPondData(map: maplibregl.Map, ponds: SavedPond[], terrain?: TerrainRecord | null): void {
  (map.getSource(POND_SOURCE) as maplibregl.GeoJSONSource | undefined)?.setData(pondsToGeoJSON(ponds, terrain));
}

export function setPondDraftData(map: maplibregl.Map, draft: PondDraftMapData | null,
  terrain?: TerrainRecord | null): void {
  (map.getSource(POND_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(pondDraftGeoJSON(draft, terrain));
}

export function setSelectedPond(map: maplibregl.Map | null, id: string | null): void {
  if (map?.getLayer('standalone-pond-selected'))
    map.setFilter('standalone-pond-selected', ['==', ['get', 'id'], id ?? '']);
}
