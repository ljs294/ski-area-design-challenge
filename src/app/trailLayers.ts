import type maplibregl from 'maplibre-gl';
import type { SavedTrail, SavedTrailPart, TrailDifficulty } from '../types';
import { DIFFICULTY_COLORS, DIFFICULTY_SYMBOL, TRAIL_DIFFICULTIES } from '../trails';
import type { TrailPresentationResult } from '../types/trailPresentation';

export const TRAIL_SOURCE = 'trails';
export const TRAIL_HIT_SOURCE = 'trail-hits';
export const TRAIL_DRAFT_SOURCE = 'trail-draft';
export const TRAIL_PAINT_SOURCE = 'trail-paint-preview';

// The built (persisted) trail layers, for a show/hide toggle. Excludes the
// transient draft/paint-preview layers used only while painting a run.
export const TRAIL_BUILT_LAYER_IDS = [
  'trail-fill',
  'trail-surface-shadow',
  'trail-outline',
  'trail-route',
  'trail-route-planning',
  'trail-direction',
  'trail-closed-marks',
  'trail-hover',
  'trail-selected-halo',
  'trail-selected',
  'trail-hit',
  'trail-head-labels',
  'trail-labels',
];
const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

export interface TrailReview {
  parts: SavedTrailPart[];
  difficulty: TrailDifficulty;
  name: string;
  infeasibleLines?: [number, number][][];
}

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

export function trailPresentationToGeoJSON(
  presentation: TrailPresentationResult,
): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = presentation.surface.map((polygon, index) => ({
    type: 'Feature',
    properties: { kind: 'surface', featureId: `surface:${index}` },
    geometry: { type: 'Polygon', coordinates: polygon },
  }));
  for (const route of presentation.routes) features.push({
    type: 'Feature',
    properties: { kind: 'route', featureId: route.featureId, id: route.trailId,
      name: route.name, label: route.label, difficulty: route.difficulty,
      status: route.status, closed: route.closed },
    geometry: { type: 'LineString', coordinates: route.coordinates },
  });
  for (const label of presentation.labels) features.push({
    type: 'Feature',
    properties: { kind: label.geometry.type === 'Point' ? 'head-label' : 'line-label',
      featureId: label.featureId, id: label.trailId, name: label.name, label: label.label,
      symbol: DIFFICULTY_SYMBOL[label.difficulty], difficulty: label.difficulty,
      status: label.status, closed: label.closed },
    geometry: label.geometry,
  });
  return { type: 'FeatureCollection', features };
}

export function trailsToHitGeoJSON(trails: SavedTrail[]): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  for (const trail of trails) trail.parts.forEach((part, partIndex) => {
    if (part.polygon.length) features.push({
      type: 'Feature', properties: { kind: 'hit', id: trail.id,
        featureId: `hit:${trail.id}:${partIndex}` },
      geometry: { type: 'Polygon', coordinates: part.polygon },
    });
    if (part.centerline.length >= 2) features.push({
      type: 'Feature', properties: { kind: 'identity', id: trail.id,
        featureId: `identity:${trail.id}:${partIndex}`, difficulty: trail.difficulty,
        closed: trail.closed === true },
      geometry: { type: 'LineString', coordinates: part.centerline },
    });
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
  for (const line of review?.infeasibleLines ?? []) features.push({
    type: 'Feature', properties: { kind: 'infeasible' },
    geometry: { type: 'LineString', coordinates: line },
  });
  return { type: 'FeatureCollection', features };
}

export interface TrailPaintPreview {
  path: [number, number][];
  cursor: [number, number] | null;
  brushWidthM: number;
  candidate?: [number, number] | null;
  head?: [number, number] | null;
  tail?: [number, number] | null;
}

type MeterPoint = { x: number; y: number };

const PREVIEW_ARC_STEP = Math.PI / 24;

function normalizedTurn(from: number, to: number): number {
  let turn = to - from;
  while (turn <= -Math.PI) turn += Math.PI * 2;
  while (turn > Math.PI) turn -= Math.PI * 2;
  return turn;
}

function appendArc(out: MeterPoint[], center: MeterPoint, radius: number,
  from: number, sweep: number, includeStart = false): void {
  const steps = Math.max(1, Math.ceil(Math.abs(sweep) / PREVIEW_ARC_STEP));
  for (let i = includeStart ? 0 : 1; i <= steps; i++) {
    const angle = from + sweep * i / steps;
    out.push({ x: center.x + Math.cos(angle) * radius,
      y: center.y + Math.sin(angle) * radius });
  }
}

/** Buffer the display-only centerline in local ground metres. This keeps the
 * live corridor aligned with the geographic brush guide at every pitch/zoom;
 * the worker's sparse raster remains the analytical source of truth. */
function brushCorridor(path: [number, number][], brushWidthM: number): GeoJSON.Polygon | null {
  if (!path.length) return null;
  const origin = path[0];
  const metersLng = Math.max(1, 111_320 * Math.cos(origin[1] * Math.PI / 180));
  const toMeters = (p: [number, number]): MeterPoint => ({
    x: (p[0] - origin[0]) * metersLng,
    y: (p[1] - origin[1]) * 111_320,
  });
  const toLngLat = (p: MeterPoint): [number, number] => [
    origin[0] + p.x / metersLng,
    origin[1] + p.y / 111_320,
  ];
  const radius = Math.max(0.01, brushWidthM / 2);
  const points: MeterPoint[] = [];
  for (const coordinate of path) {
    const point = toMeters(coordinate);
    const previous = points.at(-1);
    if (!previous || Math.hypot(point.x - previous.x, point.y - previous.y) > 1e-4)
      points.push(point);
  }

  if (points.length === 1) {
    const ring: MeterPoint[] = [];
    appendArc(ring, points[0], radius, 0, Math.PI * 2, true);
    ring[ring.length - 1] = ring[0];
    return { type: 'Polygon', coordinates: [ring.map(toLngLat)] };
  }

  const directions: number[] = [];
  for (let i = 1; i < points.length; i++)
    directions.push(Math.atan2(points[i].y - points[i - 1].y,
      points[i].x - points[i - 1].x));

  const buildSide = (side: 1 | -1): MeterPoint[] => {
    const sidePoints: MeterPoint[] = [];
    const firstAngle = directions[0] + side * Math.PI / 2;
    sidePoints.push({ x: points[0].x + Math.cos(firstAngle) * radius,
      y: points[0].y + Math.sin(firstAngle) * radius });
    for (let i = 1; i < points.length - 1; i++) {
      const from = directions[i - 1] + side * Math.PI / 2;
      const sweep = normalizedTurn(directions[i - 1], directions[i]);
      appendArc(sidePoints, points[i], radius, from, sweep);
    }
    const lastAngle = directions.at(-1)! + side * Math.PI / 2;
    sidePoints.push({ x: points.at(-1)!.x + Math.cos(lastAngle) * radius,
      y: points.at(-1)!.y + Math.sin(lastAngle) * radius });
    return sidePoints;
  };

  const left = buildSide(1);
  const right = buildSide(-1);
  const end = points.at(-1)!;
  const endLeftAngle = directions.at(-1)! + Math.PI / 2;
  const ring = left.slice();
  appendArc(ring, end, radius, endLeftAngle, -Math.PI);
  for (let i = right.length - 2; i >= 0; i--) ring.push(right[i]);
  const startRightAngle = directions[0] - Math.PI / 2;
  appendArc(ring, points[0], radius, startRightAngle, -Math.PI);
  ring[ring.length - 1] = ring[0];
  return { type: 'Polygon', coordinates: [ring.map(toLngLat)] };
}

/** Geographic brush geometry. The ring is built in local meters rather than
 * screen pixels, so it stays true to the analytical brush on pitched maps. */
export function paintPreviewGeoJSON({ path, cursor, brushWidthM, candidate, head, tail }: TrailPaintPreview): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  const corridor = brushCorridor(path, brushWidthM);
  if (corridor) features.push({ type: 'Feature', properties: { kind: 'paint' }, geometry: corridor });
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
  if (candidate) features.push({ type: 'Feature', properties: { kind: 'head-candidate' },
    geometry: { type: 'Point', coordinates: candidate } });
  if (head) features.push({ type: 'Feature', properties: { kind: 'trailhead' },
    geometry: { type: 'Point', coordinates: head } });
  if (tail) features.push({ type: 'Feature', properties: { kind: 'trailtail' },
    geometry: { type: 'Point', coordinates: tail } });
  return { type: 'FeatureCollection', features };
}

export function addTrailLayers(map: maplibregl.Map): void {
  if (map.getSource(TRAIL_SOURCE)) return;
  map.addSource(TRAIL_SOURCE, { type: 'geojson', data: EMPTY, promoteId: 'featureId' });
  map.addSource(TRAIL_HIT_SOURCE, { type: 'geojson', data: EMPTY, promoteId: 'featureId' });
  map.addSource(TRAIL_DRAFT_SOURCE, { type: 'geojson', data: EMPTY });
  map.addSource(TRAIL_PAINT_SOURCE, { type: 'geojson', data: EMPTY });

  map.addLayer({ id: 'trail-fill', type: 'fill', source: TRAIL_SOURCE,
    filter: ['==', ['get', 'kind'], 'surface'],
    paint: { 'fill-color': '#f7f8f4', 'fill-opacity': 0.58, 'fill-antialias': true } });
  map.addLayer({ id: 'trail-surface-shadow', type: 'line', source: TRAIL_SOURCE,
    filter: ['==', ['get', 'kind'], 'surface'], layout: { 'line-join': 'round' },
    paint: { 'line-color': '#34424d', 'line-width': 4, 'line-opacity': 0.18,
      'line-blur': 3 } });
  map.addLayer({ id: 'trail-outline', type: 'line', source: TRAIL_SOURCE,
    filter: ['==', ['get', 'kind'], 'surface'], layout: { 'line-join': 'round' },
    paint: { 'line-color': '#53616c', 'line-width': 1.25, 'line-opacity': 0.62 } });
  map.addLayer({ id: 'trail-route', type: 'line', source: TRAIL_SOURCE, minzoom: 14,
    filter: ['all', ['==', ['get', 'kind'], 'route'], ['==', ['get', 'status'], 'complete']],
    layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: {
      'line-color': ['case', ['get', 'closed'], '#89939c', difficultyMatch('#66717b')],
      'line-width': 1.35, 'line-opacity': ['case', ['get', 'closed'], 0.42, 0.72],
    } });
  map.addLayer({ id: 'trail-route-planning', type: 'line', source: TRAIL_SOURCE, minzoom: 14,
    filter: ['all', ['==', ['get', 'kind'], 'route'], ['==', ['get', 'status'], 'planning']],
    layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: {
      'line-color': difficultyMatch('#66717b'), 'line-width': 1.35, 'line-opacity': 0.68,
      'line-dasharray': [2, 2],
    } });
  map.addLayer({ id: 'trail-direction', type: 'symbol', source: TRAIL_SOURCE, minzoom: 14,
    filter: ['all', ['==', ['get', 'kind'], 'route'], ['!', ['get', 'closed']]],
    layout: { 'symbol-placement': 'line', 'symbol-spacing': 180, 'text-field': '▶',
      'text-size': 8, 'text-font': ['Noto Sans Regular'], 'text-keep-upright': false,
      'text-allow-overlap': false },
    paint: { 'text-color': '#56636d', 'text-opacity': 0.48,
      'text-halo-color': '#f7f8f4', 'text-halo-width': 1 } });
  map.addLayer({ id: 'trail-closed-marks', type: 'symbol', source: TRAIL_SOURCE, minzoom: 14,
    filter: ['all', ['==', ['get', 'kind'], 'route'], ['get', 'closed']],
    layout: { 'symbol-placement': 'line', 'symbol-spacing': 120, 'text-field': '×',
      'text-size': 13, 'text-font': ['Noto Sans Regular'], 'text-allow-overlap': false },
    paint: { 'text-color': '#6b7280', 'text-halo-color': '#f7f8f4', 'text-halo-width': 1.2 } });
  map.addLayer({ id: 'trail-hover', type: 'line', source: TRAIL_HIT_SOURCE, minzoom: 12,
    filter: ['all', ['==', ['get', 'kind'], 'identity'], ['==', ['get', 'id'], '']],
    layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: {
      'line-color': difficultyMatch('#64748b'), 'line-width': 3, 'line-opacity': 0.65,
    } });
  map.addLayer({ id: 'trail-selected-halo', type: 'line', source: TRAIL_HIT_SOURCE, minzoom: 11,
    filter: ['all', ['==', ['get', 'kind'], 'identity'], ['==', ['get', 'id'], '']],
    layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: {
      'line-color': '#ffffff', 'line-width': 7, 'line-opacity': 0.82, 'line-blur': 0.6,
    } });
  map.addLayer({ id: 'trail-selected', type: 'line', source: TRAIL_HIT_SOURCE, minzoom: 11,
    filter: ['all', ['==', ['get', 'kind'], 'identity'], ['==', ['get', 'id'], '']],
    layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: {
      'line-color': difficultyMatch('#334155'), 'line-width': 3.5, 'line-opacity': 0.95,
    } });
  map.addLayer({ id: 'trail-hit', type: 'fill', source: TRAIL_HIT_SOURCE,
    filter: ['==', ['get', 'kind'], 'hit'],
    paint: { 'fill-color': '#000000', 'fill-opacity': 0.01 } });

  map.addLayer({ id: 'trail-draft-fill', type: 'fill', source: TRAIL_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'trail'],
    paint: { 'fill-color': '#38bdf8', 'fill-opacity': 0.12 } });
  map.addLayer({ id: 'trail-draft-outline', type: 'line', source: TRAIL_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'trail'], layout: { 'line-join': 'round' },
    paint: { 'line-color': '#38bdf8', 'line-width': 2.5, 'line-dasharray': [1.5, 1] } });
  map.addLayer({ id: 'trail-draft-spine', type: 'line', source: TRAIL_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'spine'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#fff', 'line-width': 1.5, 'line-dasharray': [1, 2] } });
  map.addLayer({ id: 'trail-draft-infeasible', type: 'line', source: TRAIL_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'infeasible'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#dc2626', 'line-width': 5, 'line-opacity': 0.95,
      'line-dasharray': [1.5, 1] } });

  map.addLayer({ id: 'trail-paint', type: 'fill', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'paint'],
    paint: { 'fill-color': '#38bdf8', 'fill-opacity': 0.48, 'fill-antialias': true } });
  map.addLayer({ id: 'trail-paint-guide', type: 'line', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'guide'], layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#38bdf8', 'line-width': 2.25, 'line-opacity': 0.95 } });
  map.addLayer({ id: 'trail-paint-crosshair', type: 'line', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'crosshair'], layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#38bdf8', 'line-width': 1.5, 'line-opacity': 0.95 } });
  map.addLayer({ id: 'trail-head-candidate', type: 'circle', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'head-candidate'],
    paint: { 'circle-radius': 9, 'circle-color': '#f59e0b', 'circle-opacity': 0.18,
      'circle-stroke-color': '#f59e0b', 'circle-stroke-width': 2.5 } });
  map.addLayer({ id: 'trail-head-marker', type: 'circle', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'trailhead'],
    paint: { 'circle-radius': 6, 'circle-color': '#0f172a',
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 2 } });
  map.addLayer({ id: 'trail-tail-marker', type: 'circle', source: TRAIL_PAINT_SOURCE,
    filter: ['==', ['get', 'kind'], 'trailtail'],
    paint: { 'circle-radius': 6, 'circle-color': '#f59e0b',
      'circle-stroke-color': '#ffffff', 'circle-stroke-width': 2 } });
  map.addLayer({ id: 'trail-head-labels', type: 'symbol', source: TRAIL_SOURCE, minzoom: 14,
    filter: ['==', ['get', 'kind'], 'head-label'], layout: {
      'text-field': ['concat', ['get', 'symbol'], ' ', ['get', 'label']], 'text-size': 12,
      'text-font': ['Noto Sans Regular'], 'text-offset': [0.8, 0], 'text-anchor': 'left',
      'text-optional': true, 'text-allow-overlap': false },
    paint: { 'text-color': difficultyMatch('#29323b'), 'text-halo-color': '#f7f8f4',
      'text-halo-width': 1.8 } });
  map.addLayer({ id: 'trail-labels', type: 'symbol', source: TRAIL_SOURCE, minzoom: 12.5,
    filter: ['==', ['get', 'kind'], 'line-label'], layout: {
      'symbol-placement': 'line-center',
      'text-field': ['concat', ['get', 'symbol'], ' ', ['get', 'label']], 'text-size': 12.5,
      'text-font': ['Noto Sans Regular'], 'text-optional': true,
      'text-max-angle': 35, 'text-allow-overlap': false },
    paint: { 'text-color': difficultyMatch('#29323b'), 'text-halo-color': '#f7f8f4',
      'text-halo-width': 1.8 } });
}

function setSource(map: maplibregl.Map, id: string, data: GeoJSON.FeatureCollection) {
  (map.getSource(id) as maplibregl.GeoJSONSource | undefined)?.setData(data);
}
export const setTrailData = (map: maplibregl.Map, data: GeoJSON.FeatureCollection) => setSource(map, TRAIL_SOURCE, data);
export const setTrailHitData = (map: maplibregl.Map, data: GeoJSON.FeatureCollection) =>
  setSource(map, TRAIL_HIT_SOURCE, data);
export const setTrailDraftData = (map: maplibregl.Map, data: GeoJSON.FeatureCollection) => setSource(map, TRAIL_DRAFT_SOURCE, data);
export const setTrailPaintPreview = (map: maplibregl.Map, preview: TrailPaintPreview) =>
  setSource(map, TRAIL_PAINT_SOURCE, paintPreviewGeoJSON(preview));
export function setTrailPaintMode(map: maplibregl.Map, mode: 'paint' | 'erase') {
  const color = mode === 'paint' ? '#38bdf8' : '#f97316';
  if (map.getLayer('trail-paint')) map.setPaintProperty('trail-paint', 'fill-color', color);
  for (const layer of ['trail-paint-guide', 'trail-paint-crosshair'])
    if (map.getLayer(layer)) map.setPaintProperty(layer, 'line-color', color);
}

function identityFilter(id: string | null): maplibregl.FilterSpecification {
  return ['all', ['==', ['get', 'kind'], 'identity'], ['==', ['get', 'id'], id ?? '']];
}

export function setTrailSelection(map: maplibregl.Map, selectedId: string | null): void {
  for (const layer of ['trail-selected-halo', 'trail-selected'])
    if (map.getLayer(layer)) map.setFilter(layer, identityFilter(selectedId));
}

export function setTrailHover(map: maplibregl.Map, hoveredId: string | null): void {
  if (map.getLayer('trail-hover')) map.setFilter('trail-hover', identityFilter(hoveredId));
}

export function applyTrailTheme(map: maplibregl.Map, theme: 'light' | 'dark'): void {
  const dark = theme === 'dark';
  const snow = dark ? '#dfe7ea' : '#f7f8f4';
  const edge = dark ? '#91a0aa' : '#53616c';
  if (map.getLayer('trail-fill')) {
    map.setPaintProperty('trail-fill', 'fill-color', snow);
    map.setPaintProperty('trail-fill', 'fill-opacity', dark ? 0.42 : 0.58);
  }
  if (map.getLayer('trail-outline')) map.setPaintProperty('trail-outline', 'line-color', edge);
  for (const layer of ['trail-direction', 'trail-closed-marks', 'trail-head-labels', 'trail-labels'])
    if (map.getLayer(layer)) map.setPaintProperty(layer, 'text-halo-color', snow);
}
