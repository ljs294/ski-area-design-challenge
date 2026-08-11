import maplibregl from 'maplibre-gl';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import { haversineMeters } from '../geo';
import { toLngLat, toMeters, trailsFromLift, type SkiNetwork } from '../network';
import { snowmakingPipeSegments } from '../snowmakingNetwork';
import type { SnowmakingSegmentAnalysisResult } from '../snowmakingHydraulics';
import type { SavedDam, SavedLift, SavedPond, SavedSnowmakingNode, SavedTrail,
  TerrainRecord } from '../types';
import type { SavedSnowgun, SavedSnowmakingPipe, SnowmakingLakeSource } from '../types/snowmaking';
import { DIFFICULTY_COLORS } from '../trails';
import { FILL_BY_CODE } from './coverVectorize';
import type { DashboardKind, SnowmakingDashboardMode } from './dashboardMode';
import { localContourGeoJSON } from './localContours';
import { snowmakingPressureColor } from './snowmakingPressureHeatmap';
import type { Units } from './SettingsContext';

export const DASHBOARD_SOURCE = 'dashboard-map';
export const DASHBOARD_LAYER_IDS = [
  'dashboard-backdrop', 'dashboard-grid', 'dashboard-snow-cover',
  'dashboard-snow-contours', 'dashboard-snow-water', 'dashboard-trail-ties',
  'dashboard-trail-edges', 'dashboard-trail-arrows', 'dashboard-trail-labels',
  'dashboard-trail-nodes', 'dashboard-lift-hit', 'dashboard-trail-hit',
  'dashboard-snow-pipes', 'dashboard-snow-flow-arrows', 'dashboard-snow-flow-labels',
  'dashboard-snow-gun-connections', 'dashboard-snow-nodes',
  'dashboard-snow-hydrants', 'dashboard-snow-node-labels', 'dashboard-snow-guns',
  'dashboard-snow-gun-labels', 'dashboard-snow-gun-warnings',
  'dashboard-snow-pipe-hit', 'dashboard-snow-node-hit', 'dashboard-snow-gun-hit',
] as const;

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };
const WORLD: GeoJSON.Polygon = { type: 'Polygon', coordinates: [[
  [-180, -85], [180, -85], [180, 85], [-180, 85], [-180, -85],
]] };

export interface SnowmakingMapPresentation {
  mode: SnowmakingDashboardMode;
  segments: readonly SnowmakingSegmentAnalysisResult[];
  relevantSegmentColors: ReadonlyMap<string, string>;
  selectedGunIds: ReadonlySet<string>;
  gunStatuses: Readonly<Record<string, 'ready' | 'failed'>>;
  pressureRange: { minPsi: number; maxPsi: number } | null;
  showGunTypes: boolean;
  toggleGun(id: string): void;
  setHoveredSegment(id: string | null): void;
}

export interface DashboardMapData {
  kind: DashboardKind | null;
  dark: boolean;
  units: Units;
  network: SkiNetwork;
  selectedLiftId: string | null;
  selectedEdgeId: string | null;
  dams: readonly SavedDam[];
  ponds: readonly SavedPond[];
  lakes: readonly SnowmakingLakeSource[];
  trails: readonly SavedTrail[];
  lifts: readonly SavedLift[];
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  coverDisplay: CoverDisplayGeoJSON | null;
  terrainRecord: TerrainRecord | null;
  selectedSnowmaking: { kind: 'node' | 'pipe' | 'gun'; id: string } | null;
  snowmakingPresentation: SnowmakingMapPresentation | null;
}

type Props = Record<string, string | number | boolean | null>;
type Feature = GeoJSON.Feature<GeoJSON.Geometry, Props>;

function feature(kind: string, geometry: GeoJSON.Geometry, properties: Props = {}): Feature {
  return { type: 'Feature', properties: { kind, ...properties }, geometry };
}

function gridFeatures(network: SkiNetwork): Feature[] {
  const samples = network.edges.flatMap((edge) => edge.id.endsWith(':r') ? [] : edge.path);
  if (!samples.length) return [];
  const meters = samples.map((point) => toMeters(network.frame, point));
  const xs = meters.map((point) => point.x), ys = meters.map((point) => point.y);
  const minX = Math.floor((Math.min(...xs) - 400) / 200) * 200;
  const maxX = Math.ceil((Math.max(...xs) + 400) / 200) * 200;
  const minY = Math.floor((Math.min(...ys) - 400) / 200) * 200;
  const maxY = Math.ceil((Math.max(...ys) + 400) / 200) * 200;
  const result: Feature[] = [];
  for (let x = minX; x <= maxX; x += 200) result.push(feature('grid', {
    type: 'LineString', coordinates: [toLngLat(network.frame, { x, y: minY }),
      toLngLat(network.frame, { x, y: maxY })],
  }));
  for (let y = minY; y <= maxY; y += 200) result.push(feature('grid', {
    type: 'LineString', coordinates: [toLngLat(network.frame, { x: minX, y }),
      toLngLat(network.frame, { x: maxX, y })],
  }));
  return result;
}

function trailFeatures(input: DashboardMapData): Feature[] {
  const features: Feature[] = [...gridFeatures(input.network)];
  const served = input.selectedLiftId ? trailsFromLift(input.network, input.selectedLiftId) : null;
  const reachable = new Set(served?.reachableEdgeIds ?? []);
  const liftEdgeId = input.selectedLiftId ? input.network.liftEdgeIds.get(input.selectedLiftId) : null;
  if (liftEdgeId) reachable.add(liftEdgeId);
  const directTrailIds = new Set(served?.direct ?? []);
  for (const edge of input.network.edges) {
    if (edge.id.endsWith(':r')) continue;
    const entityId = edge.kind === 'lift' ? edge.liftId : edge.kind === 'trail' ? edge.trailId : edge.pathId;
    const name = edge.kind === 'lift' ? edge.liftName : edge.kind === 'trail' ? edge.trailName : edge.pathName;
    const direct = edge.kind === 'trail' && edge.segmentIndex === 0 && directTrailIds.has(edge.trailId);
    const color = edge.kind === 'lift' ? (input.dark ? '#e5e7eb' : '#27303f')
      : DIFFICULTY_COLORS[edge.difficulty];
    features.push(feature('trail-edge', { type: 'LineString', coordinates: edge.path }, {
      id: edge.kind === 'lift' ? entityId : edge.id, edgeId: edge.id, entityId,
      edgeKind: edge.kind, name, color, closed: edge.condition === 'closed',
      planned: edge.planned, selected: input.selectedEdgeId === edge.id,
      direct, dimmed: !!served && !reachable.has(edge.id),
    }));
    const from = input.network.nodeById.get(edge.from)?.lngLat;
    const to = input.network.nodeById.get(edge.to)?.lngLat;
    const ends: [[number, number], [number, number]][] = [];
    if (from) ends.push([edge.path[0], from]);
    if (to) ends.push([edge.path.at(-1)!, to]);
    for (const [a, b] of ends) features.push(feature('trail-tie', {
      type: 'LineString', coordinates: [a, b],
    }, { dimmed: !!served && !reachable.has(edge.id) }));
  }
  for (const node of input.network.nodes) features.push(feature('trail-node', {
    type: 'Point', coordinates: node.lngLat,
  }, { terminal: node.liftBases.length > 0 || node.liftTops.length > 0,
    user: node.kind === 'user-node' }));
  return features;
}

function polygon(ring: readonly [number, number][]): GeoJSON.Polygon {
  const coordinates = [...ring];
  if (coordinates.length && (coordinates[0][0] !== coordinates.at(-1)![0] ||
    coordinates[0][1] !== coordinates.at(-1)![1])) coordinates.push(coordinates[0]);
  return { type: 'Polygon', coordinates: [coordinates] };
}

export function snowmakingSegmentMidpoint(
  points: readonly [number, number][],
): [number, number] | null {
  if (!points.length) return null;
  if (points.length === 1) return points[0];
  const lengths = points.slice(1).map((point, index) => haversineMeters(points[index], point));
  const halfway = lengths.reduce((sum, length) => sum + length, 0) / 2;
  let traveled = 0;
  for (let index = 0; index < lengths.length; index += 1) {
    const length = lengths[index];
    if (traveled + length >= halfway) {
      const ratio = length > 0 ? (halfway - traveled) / length : 0;
      return [
        points[index][0] + (points[index + 1][0] - points[index][0]) * ratio,
        points[index][1] + (points[index + 1][1] - points[index][1]) * ratio,
      ];
    }
    traveled += length;
  }
  return points.at(-1) ?? null;
}

function snowmakingFeatures(input: DashboardMapData): Feature[] {
  const features: Feature[] = [];
  for (const row of input.coverDisplay?.features ?? []) {
    if (row.properties.code === 4 || row.properties.code === 80) continue;
    features.push(feature('snow-cover', row.geometry, {
      color: FILL_BY_CODE[row.properties.code] ?? '#888888',
    }));
  }
  if (input.terrainRecord) for (const row of localContourGeoJSON(
    input.terrainRecord, input.units === 'imperial').features) {
    features.push(feature('snow-contour', row.geometry, {
      major: row.properties?.level === 1,
    }));
  }
  for (const pond of input.ponds) features.push(feature('snow-water', polygon(pond.boundary)));
  for (const lake of input.lakes) features.push(feature('snow-water', polygon(lake.boundary)));
  for (const dam of input.dams) for (const ring of dam.pondRings) {
    features.push(feature('snow-water', polygon(ring)));
  }
  const presentation = input.snowmakingPresentation;
  const solved = new Map((presentation?.segments ?? []).map((segment) => [segment.id, segment]));
  for (const pipe of input.pipes) for (const segment of snowmakingPipeSegments(pipe)) {
    const result = solved.get(segment.id);
    const relevant = presentation?.relevantSegmentColors.get(segment.id) ?? null;
    const pressure = result && presentation?.pressureRange
      ? snowmakingPressureColor((result.upstreamPressurePsi + result.downstreamPressurePsi) / 2,
        presentation.pressureRange) : relevant;
    const flowLabel = result
      ? `${Math.abs(result.flowGpm).toFixed(1)} GPM\n${result.upstreamPressurePsi.toFixed(1)} → ${result.downstreamPressurePsi.toFixed(1)} PSI`
      : '';
    const properties = { id: pipe.id, segmentId: segment.id, segmentIndex: segment.segmentIndex,
      name: pipe.name, diameterIn: pipe.diameterIn, lengthM: pipe.lengthM,
      verticalM: pipe.verticalM, selected: input.selectedSnowmaking?.kind === 'pipe' &&
        input.selectedSnowmaking.id === pipe.id, analysis: presentation?.mode === 'analysis',
      relevant: !!relevant, active: result?.active ?? false,
      color: pressure ?? '#2c83a5', flowLabel };
    features.push(feature('snow-pipe', {
      type: 'LineString', coordinates: segment.vertices.map((vertex) => vertex.point),
    }, properties));
    const midpoint = flowLabel && snowmakingSegmentMidpoint(
      segment.vertices.map((vertex) => vertex.point));
    if (midpoint) features.push(feature('snow-pipe-label', {
      type: 'Point', coordinates: midpoint,
    }, properties));
  }
  for (const gun of input.guns) {
    const hydrant = gun.hydrantId ? input.nodes.find((node) => node.id === gun.hydrantId) : null;
    if (hydrant) features.push(feature('snow-gun-connection', {
      type: 'LineString', coordinates: [hydrant.point, gun.point],
    }));
  }
  for (const node of input.nodes) features.push(feature('snow-node', {
    type: 'Point', coordinates: node.point,
  }, { id: node.id, nodeKind: node.kind, label: node.labelNumber != null
    ? `${node.kind === 'pump' ? 'P' : node.kind === 'hydrant' ? 'H' : 'J'}${node.labelNumber}` : node.name,
    selected: input.selectedSnowmaking?.kind === 'node' && input.selectedSnowmaking.id === node.id }));
  for (const gun of input.guns) {
    const status = presentation?.gunStatuses[gun.id] ?? null;
    features.push(feature('snow-gun', { type: 'Point', coordinates: gun.point }, {
      id: gun.id, connected: !!gun.hydrantId, selected: presentation?.mode === 'analysis'
        ? presentation.selectedGunIds.has(gun.id) : input.selectedSnowmaking?.kind === 'gun' &&
          input.selectedSnowmaking.id === gun.id,
      status, label: presentation?.showGunTypes ? gun.variantId : '',
    }));
  }
  return features;
}

export function dashboardGeoJSON(input: DashboardMapData): GeoJSON.FeatureCollection {
  const features: Feature[] = [feature('backdrop', WORLD)];
  if (input.kind === 'trails') features.push(...trailFeatures(input));
  if (input.kind === 'snowmaking') features.push(...snowmakingFeatures(input));
  return { type: 'FeatureCollection', features };
}

const filter = (kind: string): maplibregl.ExpressionSpecification =>
  ['==', ['get', 'kind'], kind] as maplibregl.ExpressionSpecification;
const allFilter = (...rows: maplibregl.ExpressionSpecification[]): maplibregl.FilterSpecification =>
  ['all', ...rows] as maplibregl.FilterSpecification;

export function addDashboardMapLayers(map: maplibregl.Map): void {
  if (map.getSource(DASHBOARD_SOURCE)) return;
  map.addSource(DASHBOARD_SOURCE, { type: 'geojson', data: EMPTY });
  map.addLayer({ id: 'dashboard-backdrop', type: 'fill', source: DASHBOARD_SOURCE,
    filter: filter('backdrop'), layout: { visibility: 'none' },
    paint: { 'fill-color': '#f4f1ea', 'fill-opacity': 1 } });
  map.addLayer({ id: 'dashboard-grid', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('grid'), layout: { visibility: 'none' },
    paint: { 'line-color': '#9ca3af', 'line-width': 1, 'line-opacity': 0.25 } });
  map.addLayer({ id: 'dashboard-snow-cover', type: 'fill', source: DASHBOARD_SOURCE,
    filter: filter('snow-cover'), layout: { visibility: 'none' },
    paint: { 'fill-color': ['get', 'color'], 'fill-opacity': 0.15 } });
  map.addLayer({ id: 'dashboard-snow-contours', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('snow-contour'), layout: { visibility: 'none' }, paint: {
      'line-color': '#6b7280', 'line-width': ['case', ['get', 'major'], 1.25, 0.7],
      'line-opacity': ['case', ['get', 'major'], 0.5, 0.25],
    } });
  map.addLayer({ id: 'dashboard-snow-water', type: 'fill', source: DASHBOARD_SOURCE,
    filter: filter('snow-water'), layout: { visibility: 'none' }, paint: {
      'fill-color': '#76b7d2', 'fill-opacity': 0.52, 'fill-outline-color': '#397f9f',
    } });
  map.addLayer({ id: 'dashboard-trail-ties', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('trail-tie'), layout: { visibility: 'none' }, paint: {
      'line-color': '#6b7280', 'line-width': 1, 'line-dasharray': [2, 3],
      'line-opacity': ['case', ['get', 'dimmed'], 0.08, 0.55],
    } });
  map.addLayer({ id: 'dashboard-trail-edges', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('trail-edge'), layout: { visibility: 'none', 'line-cap': 'round',
      'line-join': 'round' }, paint: {
      'line-color': ['case', ['get', 'closed'], '#9ca3af', ['get', 'color']],
      'line-width': ['case', ['get', 'selected'], 6, ['get', 'direct'], 5.5,
        ['==', ['get', 'edgeKind'], 'path'], 2, 3],
      'line-opacity': ['case', ['get', 'dimmed'], 0.12, ['get', 'closed'], 0.55, 1],
    } });
  map.addLayer({ id: 'dashboard-trail-arrows', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('trail-edge'), layout: { visibility: 'none', 'symbol-placement': 'line-center',
      'text-field': '▶', 'text-size': 11, 'text-keep-upright': false,
      'text-font': ['Noto Sans Regular'], 'text-allow-overlap': true }, paint: {
      'text-color': ['case', ['get', 'closed'], '#9ca3af', ['get', 'color']],
      'text-opacity': ['case', ['get', 'dimmed'], 0.12, 1],
    } });
  map.addLayer({ id: 'dashboard-trail-labels', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('trail-edge'), layout: { visibility: 'none', 'symbol-placement': 'line',
      'text-field': ['get', 'name'], 'text-size': 12, 'text-font': ['Noto Sans Regular'],
      'text-optional': true }, paint: { 'text-color': '#394150',
      'text-halo-color': '#f4f1ea', 'text-halo-width': 2,
      'text-opacity': ['case', ['get', 'dimmed'], 0.12, 0.8] } });
  map.addLayer({ id: 'dashboard-trail-nodes', type: 'circle', source: DASHBOARD_SOURCE,
    filter: filter('trail-node'), layout: { visibility: 'none' }, paint: {
      'circle-radius': ['case', ['any', ['get', 'terminal'], ['get', 'user']], 5, 3],
      'circle-color': ['case', ['get', 'user'], '#efb84f', ['get', 'terminal'], '#27303f', '#f4f1ea'],
      'circle-stroke-color': '#6b7280', 'circle-stroke-width': 1.5,
    } });
  map.addLayer({ id: 'dashboard-lift-hit', type: 'line', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('trail-edge'), ['==', ['get', 'edgeKind'], 'lift']),
    layout: { visibility: 'none' }, paint: { 'line-width': 16, 'line-color': '#000',
      'line-opacity': 0.01 } });
  map.addLayer({ id: 'dashboard-trail-hit', type: 'line', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('trail-edge'), ['!=', ['get', 'edgeKind'], 'lift']),
    layout: { visibility: 'none' }, paint: { 'line-width': 16, 'line-color': '#000',
      'line-opacity': 0.01 } });
  map.addLayer({ id: 'dashboard-snow-pipes', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('snow-pipe'), layout: { visibility: 'none', 'line-cap': 'round',
      'line-join': 'round' }, paint: { 'line-color': ['get', 'color'],
      'line-width': ['case', ['get', 'selected'], 5, ['interpolate', ['linear'],
        ['get', 'diameterIn'], 4, 2, 24, 4]],
      'line-opacity': ['case', ['all', ['get', 'analysis'], ['!', ['get', 'relevant']]], 0.16,
        ['all', ['get', 'analysis'], ['!', ['get', 'active']]], 0.45, 1] } });
  map.addLayer({ id: 'dashboard-snow-flow-arrows', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('snow-pipe'), ['get', 'active']), layout: { visibility: 'none',
      'symbol-placement': 'line', 'symbol-spacing': 90, 'text-field': '▶', 'text-size': 10,
      'text-font': ['Noto Sans Regular'], 'text-keep-upright': false },
    paint: { 'text-color': '#172033' } });
  map.addLayer({ id: 'dashboard-snow-flow-labels', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('snow-pipe-label'), layout: {
      visibility: 'none', 'symbol-placement': 'point', 'text-field': ['get', 'flowLabel'],
      'text-size': ['interpolate', ['linear'], ['zoom'], 10, 10, 15, 12],
      'text-font': ['Noto Sans Regular'], 'text-line-height': 1.15,
      'text-offset': [0, -1.35], 'text-anchor': 'bottom', 'text-optional': true },
    paint: { 'text-color': '#27303f',
      'text-halo-color': '#f4f1ea', 'text-halo-width': 2 } });
  map.addLayer({ id: 'dashboard-snow-gun-connections', type: 'line', source: DASHBOARD_SOURCE,
    filter: filter('snow-gun-connection'), layout: { visibility: 'none' }, paint: {
      'line-color': '#4b5563', 'line-width': 1, 'line-dasharray': [2, 1.5],
    } });
  map.addLayer({ id: 'dashboard-snow-nodes', type: 'circle', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('snow-node'), ['!=', ['get', 'nodeKind'], 'hydrant']),
    layout: { visibility: 'none' }, paint: { 'circle-radius': ['case', ['get', 'selected'], 7, 5],
      'circle-color': ['match', ['get', 'nodeKind'], 'intake', '#397f9f', 'pump', '#f0b44d',
        'junction', '#4b5563', '#397f9f'], 'circle-stroke-color': ['case', ['get', 'selected'],
        '#efb84f', 'rgba(0,0,0,0)'], 'circle-stroke-width': 2 } });
  map.addLayer({ id: 'dashboard-snow-hydrants', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('snow-node'), ['==', ['get', 'nodeKind'], 'hydrant']), layout: {
      visibility: 'none', 'text-field': '×', 'text-size': 18,
      'text-font': ['Noto Sans Regular'], 'text-allow-overlap': true },
    paint: { 'text-color': '#172033', 'text-halo-color': ['case', ['get', 'selected'],
      '#efb84f', 'rgba(0,0,0,0)'], 'text-halo-width': 2 } });
  map.addLayer({ id: 'dashboard-snow-node-labels', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: filter('snow-node'), layout: { visibility: 'none', 'text-field': ['get', 'label'],
      'text-size': 11, 'text-offset': [0, -1.2], 'text-anchor': 'bottom',
      'text-font': ['Noto Sans Regular'], 'text-optional': true },
    paint: { 'text-color': '#27303f', 'text-halo-color': '#f4f1ea', 'text-halo-width': 1.5 } });
  map.addLayer({ id: 'dashboard-snow-guns', type: 'circle', source: DASHBOARD_SOURCE,
    filter: filter('snow-gun'), layout: { visibility: 'none' }, paint: {
      'circle-radius': ['case', ['get', 'selected'], 8, 5],
      'circle-color': ['match', ['get', 'status'], 'ready', '#22c55e', 'failed', '#dc2626', '#000000'],
      'circle-stroke-color': ['case', ['get', 'connected'], '#000000', '#dc2626'],
      'circle-stroke-width': ['case', ['get', 'selected'], 3, ['get', 'connected'], 0, 1.5],
    } });
  map.addLayer({ id: 'dashboard-snow-gun-labels', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('snow-gun'), ['!=', ['get', 'label'], '']), layout: {
      visibility: 'none', 'text-field': ['get', 'label'], 'text-size': 10,
      'text-offset': [0, 1.2], 'text-anchor': 'top', 'text-font': ['Noto Sans Regular'],
      'text-optional': true }, paint: { 'text-color': '#27303f',
      'text-halo-color': '#f4f1ea', 'text-halo-width': 1.5 } });
  map.addLayer({ id: 'dashboard-snow-gun-warnings', type: 'symbol', source: DASHBOARD_SOURCE,
    filter: allFilter(filter('snow-gun'), ['!', ['get', 'connected']]), layout: {
      visibility: 'none', 'text-field': '!', 'text-size': 14, 'text-offset': [0.75, -0.75],
      'text-font': ['Noto Sans Regular'], 'text-allow-overlap': true },
    paint: { 'text-color': '#b91c1c' } });
  for (const [id, kind, type] of [
    ['dashboard-snow-pipe-hit', 'snow-pipe', 'line'],
    ['dashboard-snow-node-hit', 'snow-node', 'circle'],
    ['dashboard-snow-gun-hit', 'snow-gun', 'circle'],
  ] as const) map.addLayer(type === 'line' ? { id, type, source: DASHBOARD_SOURCE,
    filter: filter(kind), layout: { visibility: 'none' }, paint: { 'line-width': 24,
      'line-color': '#000', 'line-opacity': 0.01 } } : { id, type, source: DASHBOARD_SOURCE,
    filter: filter(kind), layout: { visibility: 'none' }, paint: { 'circle-radius': 13,
      'circle-color': '#000', 'circle-opacity': 0.01 } });
}

export function setDashboardMapData(map: maplibregl.Map | null, input: DashboardMapData): void {
  (map?.getSource(DASHBOARD_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(dashboardGeoJSON(input));
  if (map?.getLayer('dashboard-backdrop')) map.setPaintProperty('dashboard-backdrop',
    'fill-color', input.dark ? '#18202a' : '#f4f1ea');
}

export function setDashboardMapVisibility(map: maplibregl.Map, kind: DashboardKind | null): void {
  for (const id of DASHBOARD_LAYER_IDS) if (map.getLayer(id)) {
    const snow = id.startsWith('dashboard-snow-');
    const trail = id.startsWith('dashboard-trail-') || id === 'dashboard-lift-hit';
    const common = id === 'dashboard-backdrop' || id === 'dashboard-grid';
    const visible = !!kind && (common || kind === 'trails' && trail || kind === 'snowmaking' && snow);
    map.setLayoutProperty(id, 'visibility', visible ? 'visible' : 'none');
  }
}

export function dashboardBounds(input: DashboardMapData): maplibregl.LngLatBoundsLike | null {
  const points: [number, number][] = input.kind === 'trails'
    ? input.network.edges.flatMap((edge) => edge.id.endsWith(':r') ? [] : edge.path)
    : [...input.nodes.map((node) => node.point), ...input.pipes.flatMap((pipe) =>
      pipe.vertices.map((vertex) => vertex.point)), ...input.guns.map((gun) => gun.point),
      ...input.ponds.flatMap((pond) => pond.boundary), ...input.dams.flatMap((dam) => dam.pondRings.flat()),
      ...input.lakes.flatMap((lake) => lake.boundary)];
  if (!points.length) return null;
  return points.reduce((bounds, point) => bounds.extend(point),
    new maplibregl.LngLatBounds(points[0], points[0]));
}
