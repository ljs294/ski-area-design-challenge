import type maplibregl from 'maplibre-gl';
import type { SavedTrail, SavedTrailPart, TrailDifficulty } from '../types';
import { DIFFICULTY_COLORS, DIFFICULTY_SYMBOL, TRAIL_DIFFICULTIES } from '../trails';

export const TRAIL_SOURCE = 'trails';
export const TRAIL_DRAFT_SOURCE = 'trail-draft';
export const TRAIL_PAINT_SOURCE = 'trail-paint-preview';
const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

export interface TrailReview { parts: SavedTrailPart[]; difficulty: TrailDifficulty; name: string }

function difficultyMatch(fallback: string): maplibregl.ExpressionSpecification {
  return ['match', ['get', 'difficulty'],
    ...TRAIL_DIFFICULTIES.flatMap((d) => [d, DIFFICULTY_COLORS[d]]), fallback] as unknown as maplibregl.ExpressionSpecification;
}

function pushParts(features: GeoJSON.Feature[], parts: Pick<SavedTrailPart, 'polygon' | 'centerline'>[], props: Record<string, unknown>) {
  for (const part of parts) {
    if (part.polygon.length) features.push({ type: 'Feature', properties: { kind: 'trail', ...props },
      geometry: { type: 'Polygon', coordinates: part.polygon } });
    if (part.centerline.length >= 2) features.push({ type: 'Feature', properties: { kind: 'spine', ...props },
      geometry: { type: 'LineString', coordinates: part.centerline } });
  }
}

export function trailsToGeoJSON(trails: SavedTrail[]): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  for (const trail of trails) pushParts(features, trail.parts, {
    id: trail.id, name: trail.name, label: `${DIFFICULTY_SYMBOL[trail.difficulty]} ${trail.name}`,
    difficulty: trail.difficulty, status: trail.status,
  });
  return { type: 'FeatureCollection', features };
}

export function draftToGeoJSON(
  polygons: [number, number][][][],
  review: TrailReview | null = null,
): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  if (review) pushParts(features, review.parts, { draft: true, name: review.name,
    difficulty: review.difficulty, status: 'planning' });
  else for (const polygon of polygons) features.push({ type: 'Feature', properties: { kind: 'trail', draft: true,
    difficulty: 'blue', status: 'planning' }, geometry: { type: 'Polygon', coordinates: polygon } });
  return { type: 'FeatureCollection', features };
}

export interface TrailPaintPreview {
  path: [number, number][];
  cursor: [number, number] | null;
  brushWidthM: number;
}

/** Geographic brush geometry. The ring is built in local meters rather than
 * screen pixels, so it stays true to the analytical brush on pitched maps. */
export function paintPreviewGeoJSON({ path, cursor, brushWidthM }: TrailPaintPreview): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  if (path.length) features.push({ type: 'Feature', properties: { kind: 'paint' },
    geometry: { type: 'LineString', coordinates: path.length === 1 ? [path[0], path[0]] : path } });
  if (cursor) {
    const radiusM = brushWidthM / 2;
    const metersLng = Math.max(1, 111_320 * Math.cos(cursor[1] * Math.PI / 180));
    const ring: [number, number][] = [];
    for (let i = 0; i <= 48; i++) {
      const angle = i / 48 * Math.PI * 2;
      ring.push([cursor[0] + Math.cos(angle) * radiusM / metersLng,
        cursor[1] + Math.sin(angle) * radiusM / 111_320]);
    }
    const armM = Math.max(1, radiusM * 0.3);
    features.push({ type: 'Feature', properties: { kind: 'guide' },
      geometry: { type: 'Polygon', coordinates: [ring] } });
    features.push({ type: 'Feature', properties: { kind: 'crosshair' },
      geometry: { type: 'MultiLineString', coordinates: [
        [[cursor[0] - armM / metersLng, cursor[1]], [cursor[0] + armM / metersLng, cursor[1]]],
        [[cursor[0], cursor[1] - armM / 111_320], [cursor[0], cursor[1] + armM / 111_320]],
      ] } });
  }
  return { type: 'FeatureCollection', features };
}

export function addTrailLayers(map: maplibregl.Map): void {
  if (map.getSource(TRAIL_SOURCE)) return;
  map.addSource(TRAIL_SOURCE, { type: 'geojson', data: EMPTY });
  map.addSource(TRAIL_DRAFT_SOURCE, { type: 'geojson', data: EMPTY });
  map.addSource(TRAIL_PAINT_SOURCE, { type: 'geojson', data: EMPTY });

  map.addLayer({ id: 'trail-fill', type: 'fill', source: TRAIL_SOURCE, filter: ['==', ['get', 'kind'], 'trail'],
    paint: { 'fill-color': difficultyMatch('#888'), 'fill-opacity': 0.32, 'fill-antialias': true } });
  map.addLayer({ id: 'trail-outline', type: 'line', source: TRAIL_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'trail'], ['==', ['get', 'status'], 'complete']],
    layout: { 'line-join': 'round' }, paint: { 'line-color': difficultyMatch('#888'), 'line-width': 2 } });
  map.addLayer({ id: 'trail-outline-planning', type: 'line', source: TRAIL_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'trail'], ['==', ['get', 'status'], 'planning']],
    layout: { 'line-join': 'round' }, paint: { 'line-color': difficultyMatch('#888'), 'line-width': 2, 'line-dasharray': [2, 1.5] } });
  map.addLayer({ id: 'trail-spine', type: 'line', source: TRAIL_SOURCE, filter: ['==', ['get', 'kind'], 'spine'],
    layout: { 'line-cap': 'round' }, paint: { 'line-color': 'rgba(255,255,255,.7)', 'line-width': 1.2, 'line-dasharray': [1, 2] } });

  map.addLayer({ id: 'trail-draft-fill', type: 'fill', source: TRAIL_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'trail'], paint: { 'fill-color': difficultyMatch('#38bdf8'), 'fill-opacity': 0.42 } });
  map.addLayer({ id: 'trail-draft-outline', type: 'line', source: TRAIL_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'trail'], layout: { 'line-join': 'round' },
    paint: { 'line-color': difficultyMatch('#38bdf8'), 'line-width': 2.5, 'line-dasharray': [1.5, 1] } });
  map.addLayer({ id: 'trail-draft-spine', type: 'line', source: TRAIL_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'spine'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#fff', 'line-width': 1.5, 'line-dasharray': [1, 2] } });

  map.addLayer({ id: 'trail-paint', type: 'line', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'paint'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#38bdf8', 'line-width': 20, 'line-opacity': 0.48 } });
  map.addLayer({ id: 'trail-paint-guide', type: 'line', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'guide'], layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#38bdf8', 'line-width': 2.25, 'line-opacity': 0.95 } });
  map.addLayer({ id: 'trail-paint-crosshair', type: 'line', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'crosshair'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#38bdf8', 'line-width': 1.5, 'line-opacity': 0.95 } });
  map.addLayer({ id: 'trail-labels', type: 'symbol', source: TRAIL_SOURCE,
    filter: ['==', ['get', 'kind'], 'trail'], layout: { 'text-field': ['get', 'label'], 'text-size': 13,
      'text-font': ['Noto Sans Regular'], 'text-optional': true },
    paint: { 'text-color': '#1f2937', 'text-halo-color': '#fff', 'text-halo-width': 1.6 } });
}

function setSource(map: maplibregl.Map, id: string, data: GeoJSON.FeatureCollection) {
  (map.getSource(id) as maplibregl.GeoJSONSource | undefined)?.setData(data);
}
export const setTrailData = (map: maplibregl.Map, data: GeoJSON.FeatureCollection) => setSource(map, TRAIL_SOURCE, data);
export const setTrailDraftData = (map: maplibregl.Map, data: GeoJSON.FeatureCollection) => setSource(map, TRAIL_DRAFT_SOURCE, data);
export const setTrailPaintPreview = (map: maplibregl.Map, preview: TrailPaintPreview) =>
  setSource(map, TRAIL_PAINT_SOURCE, paintPreviewGeoJSON(preview));
export function setTrailPaintWidth(map: maplibregl.Map, widthPx: number) {
  if (map.getLayer('trail-paint')) map.setPaintProperty('trail-paint', 'line-width', Math.max(2, widthPx));
}
export function setTrailPaintMode(map: maplibregl.Map, mode: 'paint' | 'erase') {
  const color = mode === 'paint' ? '#38bdf8' : '#f97316';
  for (const layer of ['trail-paint', 'trail-paint-guide', 'trail-paint-crosshair'])
    if (map.getLayer(layer)) map.setPaintProperty(layer, 'line-color', color);
}
