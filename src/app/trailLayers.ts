import type maplibregl from 'maplibre-gl';
import type { SavedTrail, SavedTrailPart, TrailDifficulty } from '../types';
import { DIFFICULTY_COLORS, DIFFICULTY_SYMBOL, TRAIL_DIFFICULTIES } from '../trails';

export const TRAIL_SOURCE = 'trails';
export const TRAIL_DRAFT_SOURCE = 'trail-draft';
export const TRAIL_PAINT_SOURCE = 'trail-paint-preview';

// The built (persisted) trail layers, for a show/hide toggle. Excludes the
// transient draft/paint-preview layers used only while painting a run.
export const TRAIL_BUILT_LAYER_IDS = [
  'trail-fill',
  'trail-outline',
  'trail-outline-planning',
  'trail-spine',
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
export function paintPreviewGeoJSON({ path, cursor, brushWidthM, candidate, head }: TrailPaintPreview): GeoJSON.FeatureCollection {
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
export function setTrailPaintMode(map: maplibregl.Map, mode: 'paint' | 'erase') {
  const color = mode === 'paint' ? '#38bdf8' : '#f97316';
  if (map.getLayer('trail-paint')) map.setPaintProperty('trail-paint', 'fill-color', color);
  for (const layer of ['trail-paint-guide', 'trail-paint-crosshair'])
    if (map.getLayer(layer)) map.setPaintProperty(layer, 'line-color', color);
}
